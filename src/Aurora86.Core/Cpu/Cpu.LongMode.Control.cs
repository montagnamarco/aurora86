// 64-bit mode's stack, control-flow, conditional and miscellaneous
// instructions (plans/long-mode.md, stage C2b): PUSH/POP and their relatives,
// PUSHF/POPF, ENTER/LEAVE, the near branches (JMP, CALL, RET, Jcc, LOOP,
// JRCXZ), SETcc/CMOVcc, MOVZX/MOVSX/BSWAP, CBW/CWD and their wider forms, the
// flag instructions, and CPUID/RDTSC/RDMSR/WRMSR/CLTS/multi-byte NOP.
//
// Cpu.LongMode.Execute.cs (the owner's dispatch tables) calls into
// TryExecuteControl64 and TryExecuteControlTwoByte64 for whatever opcode
// neither its own switch nor Cpu.LongMode.Alu.cs's TryExecuteAlu64/
// TryExecuteAluTwoByte64 recognises. This file never touches Execute64 or
// ExecuteTwoByte64 itself. None of these opcodes accepts LOCK: every one of
// them falls through LockAllowedAlu64's own switch to its default "false"
// arm (verified by inspection -- none matches its 0x80/81/83/F6/F7/FE/FF or
// 0xAB/B3/BB/BA/B0/B1/C0/C1/C7 cases), so a LOCK prefix in front of any of
// them is already #UD before Execute64 or ExecuteTwoByte64 ever calls here.
//
// Far forms (FF /3, FF /5, CA/CB RETF, CF IRET), 0F 01's descriptor-table and
// SWAPGS group, SYSCALL/SYSRET, IN/OUT/INT/IRET and INVLPG are stage D's: this
// file returns false for all of them so the caller's own #UD stands. INVLPG
// in particular needs a 64-bit linear address, but Cpu.InvalidatePage takes
// uint (plans/long-mode.md's C1 review notes) -- not something to improvise
// here.
//
// This file writes its own PUSH/POP/branch helpers rather than reusing the
// legacy ones in Cpu.Ops.cs: those write Eip/Esp (32 bits, zero-extending
// the upper half of RIP/RSP, which is wrong here) and several call FaultNow,
// which stamps the legacy 32-bit instruction-restart address instead of
// Step64's own _longInstructionStart. The legacy helpers that are pure
// EFLAGS or condition-code arithmetic -- TestCondition, PushedFlagsMask,
// GuardFlagsFromStack, IoPrivileged/RequireIoPrivilege, Sahf, Lahf -- touch
// neither Eip/Esp nor FaultNow, so they are called directly. Likewise
// ExecuteCpuId/ExecuteRdtsc/ExecuteRdmsr/ExecuteWrmsr (Cpu.Identification.cs,
// Cpu.Pentium.cs) only read/write EAX-EDX/ECX through State's 32-bit
// accessors, which already zero-extend into the upper half of the 64-bit
// register on every write (CpuState.SetReg32), so they need no 64-bit
// wrapper at all.
//
// Sources: SDM Vol.2 Tables 2-4/2-5 (default and default-64 operand sizes in
// 64-bit mode) and the per-instruction reference pages this file's own
// comments cite; AMD APM Vol.3 for the AMD64 origin of REX and RIP-relative
// addressing that Cpu.LongMode.cs already documents.

using System.Buffers.Binary;

namespace Aurora86.Core.Cpu;

public sealed partial class Cpu
{
    private const int AccumulatorRegister = 0;
    private const int CounterRegister = 1;
    private const int StackPointerRegister = 4;
    private const int FramePointerRegister = 5;

    /// <summary>
    /// The operand size of every near branch in 64-bit mode: forced to 64
    /// bits unconditionally, on Intel silicon (the model this emulator
    /// reports) -- SDM Vol.2 CALL: "the operand size for near call (and all
    /// near branches) is forced to 64-bits". 66h has no effect on JMP, CALL,
    /// RET, Jcc, LOOP*/JRCXZ: there is never a 16-bit near branch, no rel16
    /// displacement and no truncation of RIP. PUSH and POP are unrelated
    /// instructions and keep honouring 66h through StackOperandBytes64.
    /// </summary>
    private const int NearBranchOperandBytes = 8;

    // === dispatch entry points, called from Cpu.LongMode.Execute.cs =========

    /// <summary>
    /// The one-byte opcodes of stage C2b: stack, near-branch, conversion and
    /// flag instructions. Returns false for anything it does not handle, so
    /// the caller's default branch can raise #UD.
    /// </summary>
    private bool TryExecuteControl64(byte opcode)
    {
        switch (opcode)
        {
            case >= 0x50 and <= 0x57: ExecutePushRegister64(opcode); return true;
            case >= 0x58 and <= 0x5F: ExecutePopRegister64(opcode); return true;

            case 0x68: ExecutePushImmediate64(byteImmediate: false); return true;
            case 0x6A: ExecutePushImmediate64(byteImmediate: true); return true;

            case 0x8F: ExecutePopOperand64(); return true;

            case 0x98: ExecuteCbw64(); return true;
            case 0x99: ExecuteCwd64(); return true;

            case 0x9C: ExecutePushf64(); return true;
            case 0x9D: ExecutePopf64(); return true;

            case 0x9E: RequireFeature(CpuFeature.LahfLm); Sahf(); return true;
            case 0x9F: RequireFeature(CpuFeature.LahfLm); Lahf(); return true;

            case >= 0x70 and <= 0x7F: ExecuteJcc64((byte)(opcode & 0x0F), byteOffset: true); return true;

            case 0xC2: ExecuteRet64(hasImmediate: true); return true;
            case 0xC3: ExecuteRet64(hasImmediate: false); return true;

            case 0xC8: ExecuteEnter64(); return true;
            case 0xC9: ExecuteLeave64(); return true;

            case 0xE0: ExecuteLoop64(zeroRequired: false, useZeroFlag: true); return true;
            case 0xE1: ExecuteLoop64(zeroRequired: true, useZeroFlag: true); return true;
            case 0xE2: ExecuteLoop64(zeroRequired: false, useZeroFlag: false); return true;
            case 0xE3: ExecuteJrcxz64(); return true;

            case 0xE8: ExecuteCallRelative64(); return true;
            case 0xE9: ExecuteJmpRelative64(byteOffset: false); return true;
            case 0xEB: ExecuteJmpRelative64(byteOffset: true); return true;

            case 0xF5: Flags.Carry = !Flags.Carry; return true;   // CMC
            case 0xF8: Flags.Carry = false; return true;          // CLC
            case 0xF9: Flags.Carry = true; return true;           // STC
            case 0xFA: ExecuteCli64(); return true;
            case 0xFB: ExecuteSti64(); return true;
            case 0xFC: Flags.Direction = false; return true;      // CLD
            case 0xFD: Flags.Direction = true; return true;       // STD

            case 0xFF: return TryExecuteGroup5Control64();

            default: return false;
        }
    }

    /// <summary>The 0Fh opcodes of stage C2b: SETcc, CMOVcc, MOVZX/MOVSX, BSWAP and the system instructions this stage carries.</summary>
    private bool TryExecuteControlTwoByte64(byte opcode)
    {
        switch (opcode)
        {
            case 0x06: RequireSupervisor(); State.Cr0 &= ~CpuState.Cr0TaskSwitched; return true;   // CLTS

            // The legacy hint-NOP range (Cpu.TwoByte.cs 0x18-0x1F), gated the
            // same way there: CpuFeature.HintNops, the reserved-NOP space
            // Intel documents from the Pentium Pro onward. 18h is PREFETCHh,
            // whose SDM encoding is memory-only, so a register form is #UD;
            // 19h-1Fh (which includes 1Eh, ENDBR64's plain encoding, and the
            // F3-prefixed one this dispatch never distinguishes since a REP
            // prefix does not change which opcode case runs) accept either.
            case 0x18:
                RequireFeature(CpuFeature.HintNops);
                if (DecodeModRm64(immediateBytes: 0).IsRegister)
                {
                    throw new CpuException(6);
                }

                return true;

            case >= 0x19 and <= 0x1F:
                RequireFeature(CpuFeature.HintNops);
                DecodeModRm64(immediateBytes: 0);
                return true;

            case 0x30: ExecuteWrmsr(); return true;
            case 0x31: ExecuteRdtsc(); return true;
            case 0x32: ExecuteRdmsr(); return true;

            case >= 0x40 and <= 0x4F: ExecuteCmovcc64((byte)(opcode & 0x0F)); return true;

            case >= 0x80 and <= 0x8F: ExecuteJcc64((byte)(opcode & 0x0F), byteOffset: false); return true;
            case >= 0x90 and <= 0x9F: ExecuteSetcc64((byte)(opcode & 0x0F)); return true;

            case 0xA2: RequireFeature(CpuFeature.Identification); ExecuteCpuId(); return true;

            case 0xB6: ExecuteMovzxMovsx64(sourceBytes: 1, signExtend: false); return true;
            case 0xB7: ExecuteMovzxMovsx64(sourceBytes: 2, signExtend: false); return true;
            case 0xBE: ExecuteMovzxMovsx64(sourceBytes: 1, signExtend: true); return true;
            case 0xBF: ExecuteMovzxMovsx64(sourceBytes: 2, signExtend: true); return true;

            case >= 0xC8 and <= 0xCF: ExecuteBswap64(opcode); return true;

            default: return false;
        }
    }

    // === PUSH/POP of a full-width memory access, shared by every stack instruction ===

    /// <summary>
    /// PUSH's memory side: writes the value below the current top of stack,
    /// and only then moves RSP down to it, so a faulting write (a
    /// non-canonical address, a page fault) leaves RSP exactly where it was
    /// (SDM Vol.2 PUSH; the ordering AGENTS.md's fault-state invariant asks
    /// for). RSP itself always moves by the full 64 bits, even when the
    /// pushed operand is 16 bits wide (the contract's own "RSP arithmetic is
    /// 64-bit, with no SS limit").
    /// </summary>
    private void PushValue64(ulong value, int operandBytes)
    {
        ulong newStackPointer = State.Gpr[StackPointerRegister] - (ulong)operandBytes;
        WriteMemory64(SegReg.Ss, newStackPointer, operandBytes, value);
        State.Gpr[StackPointerRegister] = newStackPointer;
    }

    /// <summary>
    /// POP's memory side: reads the value at the current top of stack before
    /// RSP moves past it, so a faulting read leaves RSP untouched. The
    /// caller decides what happens to the value -- writing it to a register
    /// after this call is what lets "pop rsp" have the popped value win over
    /// the address this function just computed.
    /// </summary>
    private ulong PopValue64(int operandBytes)
    {
        ulong stackPointer = State.Gpr[StackPointerRegister];
        ulong value = ReadMemory64(SegReg.Ss, stackPointer, operandBytes);
        State.Gpr[StackPointerRegister] = stackPointer + (ulong)operandBytes;
        return value;
    }

    // === PUSH/POP r64 (50h-5Fh) ===============================================

    /// <summary>50h-57h: PUSH r64/r16, with REX.B reaching R8-R15. The register is read before RSP moves, so PUSH RSP pushes RSP's old value.</summary>
    private void ExecutePushRegister64(byte opcode)
    {
        int register = (opcode & 7) | (_longPrefixes.RexB ? 8 : 0);
        int operandBytes = StackOperandBytes64;
        ulong value = ReadRegister64(register, operandBytes);
        PushValue64(value, operandBytes);
    }

    /// <summary>
    /// 58h-5Fh: POP r64/r16, with REX.B reaching R8-R15. RSP moves first, as
    /// every POP requires; only then does the destination register receive
    /// the popped value -- which is why "pop rsp" ends with RSP holding what
    /// was popped, not the address PopValue64 computed along the way.
    /// </summary>
    private void ExecutePopRegister64(byte opcode)
    {
        int register = (opcode & 7) | (_longPrefixes.RexB ? 8 : 0);
        int operandBytes = StackOperandBytes64;
        ulong value = PopValue64(operandBytes);
        WriteRegister64(register, operandBytes, value);
    }

    // === PUSH imm (68h/6Ah), POP r/m (8Fh /0), FFh /6 PUSH r/m =================

    /// <summary>
    /// 68h: PUSH imm32, sign-extended to the operand size -- except at a
    /// 16-bit operand size (66h, no REX.W), where the instruction encodes a
    /// genuine two-byte immediate instead (SDM Vol.2 PUSH): "66 68" fetches
    /// an imm16, not an imm32 truncated after sign-extension. 6Ah: PUSH imm8
    /// is always sign-extended; at a 16-bit operand size that sign-extension
    /// stops at bit 15, which WriteMemory64's own truncation to two bytes
    /// then reproduces exactly, so that case needs no special fetch.
    /// </summary>
    private void ExecutePushImmediate64(bool byteImmediate)
    {
        int operandBytes = StackOperandBytes64;
        ulong value;

        if (byteImmediate)
        {
            value = (ulong)(long)(sbyte)FetchByte64();
        }
        else if (operandBytes == 2)
        {
            value = FetchWord64();
        }
        else
        {
            value = (ulong)(long)(int)FetchDword64();
        }

        PushValue64(value, operandBytes);
    }

    /// <summary>
    /// 8Fh /0: POP r/m. Any other reg field is #UD -- Group 1A has no other
    /// defined form. Neither the stack read nor RSP is committed until the
    /// destination write has succeeded (SDM Vol.3A §6.5's precise-fault
    /// invariant: no register moves before the last point the instruction
    /// can still fault), so a not-present destination page leaves RSP
    /// exactly where it was. If the destination is itself memory addressed
    /// through RSP (<see cref="LongModeOperand.StackPointerBase"/>), the
    /// address is computed after RSP's own increment, as POP alone requires
    /// (SDM Vol.2 POP) -- eight bytes further on by default, or two with
    /// 66h, wrapped to 32 bits under 67h like every other address here.
    /// </summary>
    private void ExecutePopOperand64()
    {
        LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
        if ((operand.Reg & 7) != 0)
        {
            throw new CpuException(6);
        }

        int operandBytes = StackOperandBytes64;
        ulong stackPointer = State.Gpr[StackPointerRegister];
        ulong value = ReadMemory64(SegReg.Ss, stackPointer, operandBytes);

        LongModeOperand destination = operand;
        if (operand.StackPointerBase)
        {
            ulong destinationAddress = operand.Address + (ulong)operandBytes;
            destination.Address = AddressSize32In64 ? (uint)destinationAddress : destinationAddress;
        }

        // A register destination cannot fault, so RSP moves first and the
        // popped value is written after: "pop rsp" in its r/m form (8F C4)
        // leaves the popped value, as POP requires (SDM Vol.2 POP). A memory
        // destination is written first, so a fault leaves RSP unmoved.
        if (operand.IsRegister)
        {
            State.Gpr[StackPointerRegister] = stackPointer + (ulong)operandBytes;
            WriteOperand64(destination, operandBytes, value);
            return;
        }

        WriteOperand64(destination, operandBytes, value);
        State.Gpr[StackPointerRegister] = stackPointer + (ulong)operandBytes;
    }

    // === FFh group 5: the reg fields this stage owns (2 CALL, 4 JMP, 6 PUSH) ===

    /// <summary>
    /// FFh /2 (near CALL), /4 (near JMP) and /6 (PUSH r/m): the three reg
    /// fields of group 5 this stage implements. /0 and /1 (INC/DEC) already
    /// belong to Cpu.LongMode.Alu.cs's TryExecuteGroup5IncDec64, which the
    /// caller tries first and which leaves the ModR/M byte unconsumed when it
    /// declines; /3 and /5 (far CALL/JMP) are stage D and return false here,
    /// so the caller's #UD stands for them.
    /// </summary>
    private bool TryExecuteGroup5Control64()
    {
        int reg = PeekModRmReg64();
        switch (reg)
        {
            case 2: ExecuteCallIndirect64(); return true;
            case 4: ExecuteJmpIndirect64(); return true;
            case 6: ExecutePushOperand64(); return true;
            default: return false;
        }
    }

    private void ExecutePushOperand64()
    {
        LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
        int operandBytes = StackOperandBytes64;
        ulong value = ReadOperand64(operand, operandBytes);
        PushValue64(value, operandBytes);
    }

    // === PUSHF/POPF (9Ch/9Dh) ===================================================

    /// <summary>
    /// PUSHFQ/PUSHFD: pushes RFLAGS' low 32 bits (the upper 32 are always
    /// zero in 64-bit mode, SDM Vol.2 PUSHF/PUSHFD/PUSHFQ), masked exactly as
    /// the legacy PUSHFD is by PushedFlagsMask (Cpu.Ops.cs) -- VM and RF are
    /// never among the visible bits there, which already satisfies this
    /// stage's own "VM and RF are never set by POPF" sibling rule for the
    /// push side too.
    /// </summary>
    private void ExecutePushf64()
    {
        int operandBytes = StackOperandBytes64;
        ulong value = Flags.Value & PushedFlagsMask();
        PushValue64(value, operandBytes);
    }

    /// <summary>
    /// POPFQ/POPFD: reuses GuardFlagsFromStack (Cpu.Ops.cs), the same
    /// privilege filter the legacy POPF applies -- IOPL changes only at CPL
    /// 0, IF only when the caller is at least as privileged as IOPL says --
    /// with virtual-8086 mode's own branch never reachable here (64-bit mode
    /// and V86 are mutually exclusive). That function already keeps VM and RF
    /// fixed at their current value and forces the reserved bit, so this
    /// function only has to assemble the 32-bit image GuardFlagsFromStack
    /// expects: the popped 16 bits merged under the current upper half at a
    /// 16-bit operand size, or the popped value whole otherwise.
    /// </summary>
    private void ExecutePopf64()
    {
        int operandBytes = StackOperandBytes64;
        ulong popped = PopValue64(operandBytes);

        uint next = operandBytes == 2
            ? (Flags.Value & 0xFFFF0000) | ((uint)popped & 0xFFFF)
            : (uint)popped;

        Flags.SetValue(GuardFlagsFromStack(next, loadsVirtualInterruptBits: false));
    }

    // === ENTER (C8h) / LEAVE (C9h) ==============================================

    /// <summary>
    /// ENTER: builds a nested procedure's stack frame. frameSize is always a
    /// 16-bit immediate and level is masked to five bits, as in the legacy
    /// instruction (Cpu.Ops.cs's Enter); the frame pointer and every copied
    /// entry are the operand size instead -- 8 bytes by default, 2 with 66h,
    /// matching StackOperandBytes64 the way every other stack instruction in
    /// this file does.
    ///
    /// Every push, at every level including 0 and 1, goes through a local
    /// tempRsp rather than PushValue64: RSP and RBP are committed only once,
    /// after the last write has succeeded, per the precise-fault invariant
    /// (SDM Vol.3A §6.5) -- a fault partway through a deep nesting level
    /// must leave RSP and RBP exactly as ENTER found them, even though the
    /// bytes already written below the old RSP are free to stay (nothing
    /// reads them until a matching LEAVE or POP does, and by then the fault
    /// has already been handled).
    /// </summary>
    private void ExecuteEnter64()
    {
        ushort frameSize = FetchWord64();
        byte level = (byte)(FetchByte64() & 0x1F);
        int operandBytes = StackOperandBytes64;

        ulong tempRsp = State.Gpr[StackPointerRegister];
        ulong oldFramePointer = State.Gpr[FramePointerRegister];

        tempRsp -= (ulong)operandBytes;
        WriteMemory64(SegReg.Ss, tempRsp, operandBytes, oldFramePointer);
        ulong framePointer = tempRsp;

        ulong displayPointer = oldFramePointer;
        for (int level_ = 1; level_ < level; level_++)
        {
            displayPointer -= (ulong)operandBytes;
            ulong entry = ReadMemory64(SegReg.Ss, displayPointer, operandBytes);
            tempRsp -= (ulong)operandBytes;
            WriteMemory64(SegReg.Ss, tempRsp, operandBytes, entry);
        }

        if (level > 0)
        {
            tempRsp -= (ulong)operandBytes;
            WriteMemory64(SegReg.Ss, tempRsp, operandBytes, framePointer);
        }

        tempRsp -= frameSize;

        State.Gpr[StackPointerRegister] = tempRsp;
        WriteRegister64(FramePointerRegister, operandBytes, framePointer);
    }

    /// <summary>
    /// LEAVE: RSP &lt;- RBP, then RBP &lt;- Pop(). The read happens directly at
    /// RBP, before either register moves: a non-canonical or not-present RBP
    /// faults with RSP and RBP both exactly as LEAVE found them.
    /// </summary>
    private void ExecuteLeave64()
    {
        int operandBytes = StackOperandBytes64;
        ulong framePointer = State.Gpr[FramePointerRegister];
        ulong value = ReadMemory64(SegReg.Ss, framePointer, operandBytes);

        State.Gpr[StackPointerRegister] = framePointer + (ulong)operandBytes;
        WriteRegister64(FramePointerRegister, operandBytes, value);
    }

    // === near branches: JMP, CALL, RET, Jcc, LOOP, JRCXZ ========================

    /// <summary>
    /// Checks a computed branch target for a canonical address: #GP(0)
    /// otherwise, raised before anything is committed -- RIP is restored by
    /// Step64, and every caller validates before it pushes, pops or moves a
    /// counter.
    /// </summary>
    private static ulong ValidateBranchTarget(ulong target)
    {
        if (!IsCanonical(target))
        {
            throw new CpuException(13, 0);
        }

        return target;
    }

    /// <summary>EBh/E9h: JMP rel8/rel32; with 66h still rel32 (see <see cref="NearBranchOperandBytes"/>).</summary>
    private void ExecuteJmpRelative64(bool byteOffset)
    {
        long displacement = byteOffset ? (sbyte)FetchByte64() : (int)FetchDword64();
        State.Rip = ValidateBranchTarget((ulong)((long)State.Rip + displacement));
    }

    /// <summary>70h-7Fh and 0F 80h-8Fh: Jcc rel8/rel32. TestCondition (Cpu.Ops.cs) is pure EFLAGS arithmetic, so it is reused unchanged.</summary>
    private void ExecuteJcc64(byte condition, bool byteOffset)
    {
        long displacement = byteOffset ? (sbyte)FetchByte64() : (int)FetchDword64();
        if (TestCondition(condition))
        {
            State.Rip = ValidateBranchTarget((ulong)((long)State.Rip + displacement));
        }
    }

    /// <summary>E8h: CALL rel32. The target is validated before the return address is pushed, so a non-canonical destination leaves the stack untouched as well as RIP.</summary>
    private void ExecuteCallRelative64()
    {
        int displacement = (int)FetchDword64();
        ulong returnAddress = State.Rip;
        ulong target = ValidateBranchTarget((ulong)((long)returnAddress + displacement));

        PushValue64(returnAddress, NearBranchOperandBytes);
        State.Rip = target;
    }

    /// <summary>FFh /4: indirect near JMP through an r/m64 operand, whatever 66h says (see <see cref="NearBranchOperandBytes"/>).</summary>
    private void ExecuteJmpIndirect64()
    {
        LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
        ulong target = ReadOperand64(operand, NearBranchOperandBytes);
        State.Rip = ValidateBranchTarget(target);
    }

    /// <summary>FFh /2: indirect near CALL, the same validate-then-push-then-jump order as ExecuteCallRelative64.</summary>
    private void ExecuteCallIndirect64()
    {
        LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
        ulong rawTarget = ReadOperand64(operand, NearBranchOperandBytes);
        ulong target = ValidateBranchTarget(rawTarget);
        ulong returnAddress = State.Rip;

        PushValue64(returnAddress, NearBranchOperandBytes);
        State.Rip = target;
    }

    /// <summary>
    /// C3h/C2h: RET/RET imm16. The return address is read and validated
    /// before RSP moves: faults are precise (SDM Vol.3A §6.5), so a
    /// non-canonical target leaves RSP exactly where RET found it. RSP then
    /// moves past the return address and the imm16 bytes together.
    /// </summary>
    private void ExecuteRet64(bool hasImmediate)
    {
        ushort popBytes = hasImmediate ? FetchWord64() : (ushort)0;
        ulong stackPointer = State.Gpr[StackPointerRegister];

        ulong rawTarget = ReadMemory64(SegReg.Ss, stackPointer, NearBranchOperandBytes);
        ulong target = ValidateBranchTarget(rawTarget);

        State.Gpr[StackPointerRegister] = stackPointer + NearBranchOperandBytes + popBytes;
        State.Rip = target;
    }

    /// <summary>E0h-E2h: LOOP/LOOPE/LOOPNE. The counter is RCX, or ECX under 67h (address size, not operand size) -- an easy pair to swap, which is why the contract calls it out by name.</summary>
    private void ExecuteLoop64(bool zeroRequired, bool useZeroFlag)
    {
        sbyte offset = (sbyte)FetchByte64();
        ulong remaining = CounterAfterDecrement64();

        bool take = remaining != 0;
        if (take && useZeroFlag)
        {
            take = Flags.Zero == zeroRequired;
        }

        // The target is validated before the counter is written: a
        // non-canonical target leaves RCX as LOOP found it (precise faults).
        ulong target = take ? ValidateBranchTarget((ulong)((long)State.Rip + offset)) : 0;
        CommitCounter64(remaining);

        if (take)
        {
            State.Rip = target;
        }
    }

    /// <summary>E3h: JRCXZ, or JECXZ under 67h.</summary>
    private void ExecuteJrcxz64()
    {
        sbyte offset = (sbyte)FetchByte64();
        ulong count = AddressSize32In64 ? (uint)State.Gpr[CounterRegister] : State.Gpr[CounterRegister];

        if (count == 0)
        {
            State.Rip = ValidateBranchTarget((ulong)((long)State.Rip + offset));
        }
    }

    /// <summary>The counter LOOP would leave, without writing it: RCX - 1, or ECX - 1 under 67h.</summary>
    private ulong CounterAfterDecrement64()
    {
        if (AddressSize32In64)
        {
            return (uint)State.Gpr[CounterRegister] - 1;
        }

        return State.Gpr[CounterRegister] - 1;
    }

    /// <summary>Writes LOOP's counter: all of RCX, or ECX under 67h, zero-extended like any 32-bit write.</summary>
    private void CommitCounter64(ulong counter)
    {
        if (AddressSize32In64)
        {
            WriteRegister64(CounterRegister, 4, counter);
            return;
        }

        State.Gpr[CounterRegister] = counter;
    }

    // === SETcc (0F 90h-9Fh) / CMOVcc (0F 40h-4Fh) ===============================

    /// <summary>SETcc: writes 1 or 0 to a byte destination, following the REX byte-register rule (SPL/BPL/SIL/DIL versus AH/CH/DH/BH) ReadRegister64/WriteRegister64 already apply.</summary>
    private void ExecuteSetcc64(byte condition)
    {
        LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
        byte value = TestCondition(condition) ? (byte)1 : (byte)0;
        WriteOperand64(operand, 1, value);
    }

    /// <summary>
    /// CMOVcc: the r/m source is always read, so a bad address faults whether
    /// or not the move happens. At a 32-bit operand size the destination is
    /// still rewritten even when the condition is false, because the
    /// write-back stage always writes 32 bits and any 32-bit register write
    /// zero-extends bits 63:32 in 64-bit mode (SDM Vol.2 CMOVcc) -- so the
    /// upper half of a 32-bit CMOV's destination is cleared regardless of
    /// which way the condition went.
    /// </summary>
    private void ExecuteCmovcc64(byte condition)
    {
        RequireFeature(CpuFeature.Cmov);

        int operandBytes = OperandBytes64;
        LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
        ulong sourceValue = ReadOperand64(operand, operandBytes);

        if (TestCondition(condition))
        {
            WriteRegister64(operand.Reg, operandBytes, sourceValue);
        }
        else if (operandBytes == 4)
        {
            WriteRegister64(operand.Reg, 4, ReadRegister64(operand.Reg, 4));
        }
    }

    // === MOVZX/MOVSX (0F B6h/B7h/BEh/BFh) =======================================

    /// <summary>MOVZX/MOVSX: widens an 8- or 16-bit source into a 16/32/64-bit destination, with or without sign extension.</summary>
    private void ExecuteMovzxMovsx64(int sourceBytes, bool signExtend)
    {
        LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
        int destinationBytes = OperandBytes64;
        ulong sourceValue = ReadOperand64(operand, sourceBytes);

        ulong extended = signExtend
            ? (ulong)SignExtendToLong(sourceValue, sourceBytes * 8)
            : sourceValue;

        WriteRegister64(operand.Reg, destinationBytes, extended);
    }

    // === BSWAP (0F C8h-CFh) ======================================================

    /// <summary>
    /// BSWAP: reverses the bytes of a register -- 4 bytes without REX.W, 8
    /// with it. 66h + BSWAP has no defined result at all (the SDM leaves it
    /// unspecified); this follows the legacy path's own choice for the same
    /// case (Cpu.TwoByte.cs) rather than picking a different one here: the
    /// low 16 bits are zeroed and the rest of the register is left alone,
    /// the same way <see cref="WriteRegister64"/>'s 16-bit write always
    /// preserves what it does not touch.
    /// </summary>
    private void ExecuteBswap64(byte opcode)
    {
        RequireFeature(CpuFeature.Isa486);
        int register = (opcode & 7) | (_longPrefixes.RexB ? 8 : 0);

        if (_longPrefixes.RexW)
        {
            State.Gpr[register] = BinaryPrimitives.ReverseEndianness(State.Gpr[register]);
            return;
        }

        if (_longPrefixes.OperandSize)
        {
            WriteRegister64(register, 2, 0);
            return;
        }

        uint value = (uint)State.Gpr[register];
        WriteRegister64(register, 4, BinaryPrimitives.ReverseEndianness(value));
    }

    // === CBW/CWDE/CDQE (98h), CWD/CDQ/CQO (99h) =================================

    /// <summary>98h: widens AL into AX (CBW), AX into EAX (CWDE, the default in 64-bit mode without REX.W), or EAX into RAX (CDQE, REX.W).</summary>
    private void ExecuteCbw64()
    {
        switch (OperandBytes64)
        {
            case 2:
                WriteRegister64(AccumulatorRegister, 2, (ulong)(ushort)(sbyte)ReadRegister64(AccumulatorRegister, 1));
                return;
            case 4:
                WriteRegister64(AccumulatorRegister, 4, (ulong)(uint)(int)(short)ReadRegister64(AccumulatorRegister, 2));
                return;
            default:
                WriteRegister64(AccumulatorRegister, 8, (ulong)(long)(int)ReadRegister64(AccumulatorRegister, 4));
                return;
        }
    }

    /// <summary>99h: sign-extends AX into DX:AX (CWD), EAX into EDX:EAX (CDQ, the default without REX.W), or RAX into RDX:RAX (CQO, REX.W).</summary>
    private void ExecuteCwd64()
    {
        switch (OperandBytes64)
        {
            case 2:
                WriteRegister64(2, 2, (ReadRegister64(AccumulatorRegister, 2) & 0x8000) != 0 ? 0xFFFFUL : 0UL);
                return;
            case 4:
                WriteRegister64(2, 4, (ReadRegister64(AccumulatorRegister, 4) & 0x80000000UL) != 0 ? 0xFFFFFFFFUL : 0UL);
                return;
            default:
                WriteRegister64(2, 8, (ReadRegister64(AccumulatorRegister, 8) & 0x8000000000000000UL) != 0 ? ulong.MaxValue : 0UL);
                return;
        }
    }

    // === CLI/STI (FAh/FBh) =======================================================

    /// <summary>
    /// CLI, with the IOPL rule from the legacy instruction (Cpu.Ops.cs's own
    /// Cli/IoPrivileged) but without CR4.VME's virtualized IF: virtual-8086
    /// mode cannot coexist with 64-bit mode, so that branch never applies
    /// here. Reuses IoPrivileged/RequireIoPrivilege directly, which read only
    /// EFLAGS and CPL and touch neither Eip/Esp nor FaultNow.
    /// </summary>
    private void ExecuteCli64()
    {
        RequireIoPrivilege();
        Flags.Interrupt = false;
    }

    /// <summary>STI, with the same IOPL rule as ExecuteCli64. The one-instruction interrupt shadow after STI (_interruptShadow, Cpu.cs) is set exactly as the legacy Sti does.</summary>
    private void ExecuteSti64()
    {
        RequireIoPrivilege();
        Flags.Interrupt = true;
        _interruptShadow = true;
    }
}
