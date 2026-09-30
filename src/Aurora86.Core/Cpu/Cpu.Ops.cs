using System.Runtime.CompilerServices;


// The arithmetic and logic of the one-byte opcodes: ADD through CMP, the
// accumulator forms, INC and DEC (which leave the carry alone), MUL and
// DIV, the flag instructions, IN and OUT with their IOPL check, and the
// segment loads that decide between a real-mode paragraph and a protected-
// mode selector. Mode-sensitive only where a segment or a port is touched;
// the rest is the same on every processor since the 8086.

namespace Aurora86.Core.Cpu;

public sealed partial class Cpu
{
    // --- the accumulator at the current operand size --------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadAccumulator() => OperandSize32 ? State.Eax : State.Ax;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteAccumulator(uint value)
    {
        if (OperandSize32) State.Eax = value; else State.Ax = (ushort)value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadMemOperand(SegReg segment, uint offset)
        => OperandSize32 ? ReadMem32(segment, offset) : ReadMem16(segment, offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteMemOperand(SegReg segment, uint offset, uint value)
    {
        if (OperandSize32) WriteMem32(segment, offset, value);
        else WriteMem16(segment, offset, (ushort)value);
    }

    /// <summary>Operand size in bits, as the flag logic wants it.</summary>
    private byte OperandBits => OperandSize32 ? (byte)32 : (byte)16;

    // --- ALU operations --------------------------------------------------------

    /// <summary>
    /// The eight ALU operations all share the same shape: only the computation
    /// and the flavour of flags differ. The index is bits 3-5 of the opcode,
    /// which is exactly how the hardware itself picks the operation.
    /// </summary>
    private uint AluOperate(int operation, uint left, uint right, byte size)
    {
        bool carryIn = false;
        uint result;
        FlagOp flagOp;

        switch (operation)
        {
            case 0: result = left + right; flagOp = FlagOp.Add; break;
            case 1: result = left | right; flagOp = FlagOp.Logic; break;
            case 2:
                carryIn = Flags.Carry;
                result = left + right + (carryIn ? 1u : 0u);
                flagOp = FlagOp.Adc;
                break;
            case 3:
                carryIn = Flags.Carry;
                result = left - right - (carryIn ? 1u : 0u);
                flagOp = FlagOp.Sbb;
                break;
            case 4: result = left & right; flagOp = FlagOp.Logic; break;
            case 5: result = left - right; flagOp = FlagOp.Sub; break;
            case 6: result = left ^ right; flagOp = FlagOp.Logic; break;
            default: result = left - right; flagOp = FlagOp.Sub; break;   // CMP
        }

        Flags.Set(flagOp, left, right, result, size, carryIn);
        return result;
    }

    /// <summary>
    /// One of the six forms an ALU operation can take:
    /// 0: r/m8, r8   1: r/m, r   2: r8, r/m8   3: r, r/m   4: AL, imm8   5: eAX, imm
    /// </summary>
    private void ExecuteAluGroup(int operation, int form)
    {
        bool isCompare = operation == 7;   // CMP throws the result away

        switch (form)
        {
            case 0:
            {
                var m = DecodeModRm();
                uint r = AluOperate(operation, ReadRm8(m), State.GetReg8(m.Reg), 8);
                if (!isCompare) WriteRm8(m, (byte)r);
                return;
            }
            case 1:
            {
                var m = DecodeModRm();
                uint r = AluOperate(operation, ReadRm(m), ReadRegOperand(m), OperandBits);
                if (!isCompare) WriteRm(m, r);
                return;
            }
            case 2:
            {
                var m = DecodeModRm();
                uint r = AluOperate(operation, State.GetReg8(m.Reg), ReadRm8(m), 8);
                if (!isCompare) State.SetReg8(m.Reg, (byte)r);
                return;
            }
            case 3:
            {
                var m = DecodeModRm();
                uint r = AluOperate(operation, ReadRegOperand(m), ReadRm(m), OperandBits);
                if (!isCompare) WriteRegOperand(m, r);
                return;
            }
            case 4:
            {
                uint r = AluOperate(operation, State.GetReg8(0), FetchByte(), 8);
                if (!isCompare) State.SetReg8(0, (byte)r);
                return;
            }
            default:
            {
                uint r = AluOperate(operation, ReadAccumulator(), FetchImmediate(), OperandBits);
                if (!isCompare) WriteAccumulator(r);
                return;
            }
        }
    }

    /// <summary>Group 0x80: an ALU operation on r/m8 with an 8-bit immediate.</summary>
    private void AluImmediate8()
    {
        var m = DecodeModRm();
        byte immediate = FetchByte();
        uint result = AluOperate(m.Reg, ReadRm8(m), immediate, 8);
        if (m.Reg != 7) WriteRm8(m, (byte)result);
    }

    /// <summary>
    /// Groups 0x81/0x83. The second one takes an 8-bit immediate sign-extended
    /// to the operand size: it is the compact encoding used for small
    /// constants, and it is extremely common in real code, since most
    /// immediates in practice are tiny.
    /// </summary>
    private void AluImmediate(bool signExtendedByte)
    {
        var m = DecodeModRm();
        uint immediate = signExtendedByte ? (uint)(sbyte)FetchByte() : FetchImmediate();
        uint result = AluOperate(m.Reg, ReadRm(m), immediate, OperandBits);
        if (m.Reg != 7) WriteRm(m, result);
    }

    private void SetLogicFlags(uint result) => Flags.Set(FlagOp.Logic, 0, 0, result, OperandBits);
    private void SetLogicFlags8(byte result) => Flags.Set(FlagOp.Logic, 0, 0, result, 8);

    // --- INC/DEC ---------------------------------------------------------------

    private void IncReg(int index)
    {
        if (OperandSize32)
        {
            uint value = State.GetReg32(index) + 1;
            Flags.Set(FlagOp.Inc, State.GetReg32(index), 1, value, 32);
            State.SetReg32(index, value);
        }
        else
        {
            ushort value = (ushort)(State.GetReg16(index) + 1);
            Flags.Set(FlagOp.Inc, State.GetReg16(index), 1, value, 16);
            State.SetReg16(index, value);
        }
    }

    private void DecReg(int index)
    {
        if (OperandSize32)
        {
            uint value = State.GetReg32(index) - 1;
            Flags.Set(FlagOp.Dec, State.GetReg32(index), 1, value, 32);
            State.SetReg32(index, value);
        }
        else
        {
            ushort value = (ushort)(State.GetReg16(index) - 1);
            Flags.Set(FlagOp.Dec, State.GetReg16(index), 1, value, 16);
            State.SetReg16(index, value);
        }
    }

    // --- PUSHA/POPA ------------------------------------------------------------

    private void Pusha()
    {
        // The SP value that gets stored is the one from *before* the pushes
        // began, not the value it happens to hold when its turn comes round.
        // That is what lets POPA restore a coherent stack.
        uint originalSp = State.Esp;

        if (OperandSize32)
        {
            Push32(State.Eax); Push32(State.Ecx); Push32(State.Edx); Push32(State.Ebx);
            Push32(originalSp); Push32(State.Ebp); Push32(State.Esi); Push32(State.Edi);
        }
        else
        {
            Push16(State.Ax); Push16(State.Cx); Push16(State.Dx); Push16(State.Bx);
            Push16((ushort)originalSp); Push16(State.Bp); Push16(State.Si); Push16(State.Di);
        }
    }

    private void Popa()
    {
        // POPA reads one contiguous block of 16 or 32 bytes: the processor
        // checks the whole extent before it starts, so a block that overruns
        // the limit fails up front instead of wrapping the way an individual
        // pop would.
        // This applies to the 32-bit form; the 16-bit one is built from
        // ordinary pops and follows the rules for individual reads.
        if (!State.Ss.Big && OperandSize32)
            CheckLimit(SegReg.Ss, State.Sp, 32u);

        if (OperandSize32)
        {
            State.Edi = Pop32(); State.Esi = Pop32(); State.Ebp = Pop32();
            // The stacked ESP is not discarded outright: its high half does
            // reach the register, and only the low half stays as the pops left
            // it. With a 16-bit SS that is the only explanation that matches
            // the final ESP observed on a real 386.
            uint stacked = Pop32();
            if (!State.Ss.Big)
            {
                ushort sp = State.Sp;
                State.Esp = (stacked & 0xFFFF0000u) | sp;
            }
            State.Ebx = Pop32(); State.Edx = Pop32(); State.Ecx = Pop32(); State.Eax = Pop32();
        }
        else
        {
            State.Di = Pop16(); State.Si = Pop16(); State.Bp = Pop16();
            Pop16();
            State.Bx = Pop16(); State.Dx = Pop16(); State.Cx = Pop16(); State.Ax = Pop16();
        }
    }

    // --- flags -----------------------------------------------------------------

    // PUSHFD pushes the flags as 32 bits, but bits 16-31 come out zeroed: the
    // 386 has nothing to put up there yet (VM and RF arrive with the 486).
    // The flag bits that the stack is not allowed to change. VM cannot be
    // set or cleared by popping it -- only IRET from ring 0 and the fault path
    // move a task in and out of virtual mode -- and RF is the processor's own.
    private const uint InterruptFlag = 1u << 9;
    private const uint IoplMask = 3u << 12;
    private const uint IdentificationFlag = 1u << 21;

    /// <summary>EFLAGS.AC, bit 18: the alignment-check flag the 486 added.</summary>
    private const uint AlignmentCheckBit = 1u << 18;

    private const uint ResumeFlag = 1u << 16;

    private void Pushf()
    {
        // In virtual 8086 mode PUSHF is IOPL-sensitive: a task that could read
        // the real flags would learn the real IF, and the monitor virtualises
        // that flag precisely so the task cannot. Below IOPL 3 it traps --
        // unless CR4.VME is on, in which case Table 20-2 replaces the trap
        // with a virtualized image (Cpu.VirtualMode.cs) instead of a fault
        // into the monitor for every PUSHF a DOS box executes.
        if (State.Virtual8086Mode)
        {
            if (TryPushVirtualizedFlags()) return;
            RequireIoPrivilege();
        }

        // PUSHFD pushes EFLAGS AND 00FCFFFFh: VM and RF are always seen as
        // zero, and everything above them -- AC, VIF, VIP and ID, on the
        // processors that have them -- is visible. Masking the image to 16
        // bits, as this did until the P2.3 review, made the standard probes
        // fail: a 486 is told from a 386 by toggling AC with POPFD and reading
        // it back with PUSHFD, and CPUID is found the same way through ID, so
        // no guest could ever see that CPUID was there.
        // Of those upper bits only the ones this processor has are visible:
        // the rest are reserved and read as zero, which the 386EX vectors show
        // -- PUSHFD there pushes zero above bit 15 whatever the flags hold.
        PushOperand(Flags.Value & PushedFlagsMask());
    }

    /// <summary>
    /// The EFLAGS bits PUSHFD lets the stack see: the low sixteen, then AC
    /// from the 486, VIF and VIP with the virtual-mode extensions, and ID
    /// where CPUID exists. VM and RF are never among them.
    /// </summary>
    private uint PushedFlagsMask()
    {
        uint mask = 0x0000FFFF;

        if (State.Features.Has(CpuFeature.Isa486))
        {
            mask |= AlignmentCheckBit;
        }

        if (State.Features.Has(CpuFeature.Vme))
        {
            mask |= VifFlag | VipFlag;
        }

        if (State.Features.Has(CpuFeature.Identification))
        {
            mask |= IdentificationFlag;
        }

        return mask;
    }

    private void Popf()
    {
        // IOPL-sensitive in virtual 8086 mode, for the same reason as PUSHF,
        // and with the same CR4.VME exception below IOPL 3
        // (TryPopVirtualizedFlags, Cpu.VirtualMode.cs): the monitor owns the
        // interrupt flag, and a task below IOPL 3 that tried to write it has
        // to be caught and have the write emulated -- unless VME lets the
        // write land on VIF directly instead.
        if (State.Virtual8086Mode)
        {
            if (TryPopVirtualizedFlags(iret: false)) return;
            RequireIoPrivilege();
        }

        uint value = PopOperand();
        uint next = OperandSize32 ? value : (Flags.Value & 0xFFFF0000) | (value & 0xFFFF);
        Flags.SetValue(GuardFlagsFromStack(next, loadsVirtualInterruptBits: false));
    }

    /// <summary>
    /// Applies the privilege rules to a flags value taken off the stack, as
    /// POPF and IRET both must. Real mode may write anything but VM; under
    /// protection IOPL changes only at ring 0, IF only when the caller is at
    /// least as privileged as IOPL says, and a virtual 8086 task -- which is
    /// ring 3 by definition -- may therefore change neither IOPL nor, unless
    /// IOPL is already 3, IF. Without these rules a task could raise its own
    /// IOPL and step out from under the monitor.
    /// </summary>
    private uint GuardFlagsFromStack(uint next, bool loadsVirtualInterruptBits)
    {
        uint current = Flags.Value;
        const uint fixedBits = VirtualModeFlag | ResumeFlag;
        next = (next & ~fixedBits) | (current & fixedBits);

        // The identification bit only moves on a processor that has CPUID.
        // This is not a detail: trying to invert it and seeing whether the
        // change sticks is precisely how software decides the instruction is
        // there, so a 386 that let it move would be asked a question it
        // cannot answer.
        // AC (bit 18) arrived with the 486: on a 386 the bit does not exist,
        // and a 386 that let POPFD set it would pass the probe that tells the
        // two apart.
        if (!State.Features.Has(CpuFeature.Isa486))
            next = (next & ~AlignmentCheckBit) | (current & AlignmentCheckBit);

        if (!State.Features.Has(CpuFeature.Identification))
            next = (next & ~IdentificationFlag) | (current & IdentificationFlag);

        // VIF and VIP are loaded from a stack image only by a 32-bit IRET at
        // ring 0 in protected mode (and by the task switch and the return to
        // V86, which do not come through here). POPF never loads them at any
        // privilege -- the SDM: "VIP, VIF and VM remain unaffected" -- and
        // neither does a real-mode IRETD. They also exist only on processors
        // with the virtual-mode extensions.
        bool virtualInterruptBitsLoad = loadsVirtualInterruptBits
                                        && State.Features.Has(CpuFeature.Vme)
                                        && State.ProtectedMode
                                        && State.Cpl == 0;

        if (!virtualInterruptBitsLoad)
            next = (next & ~VirtualInterruptBits) | (current & VirtualInterruptBits);

        if (!State.ProtectedMode) return next;

        byte iopl = (byte)((current & IoplMask) >> 12);
        if (State.Cpl != 0) next = (next & ~IoplMask) | (current & IoplMask);
        if (State.Cpl > iopl) next = (next & ~InterruptFlag) | (current & InterruptFlag);
        return next;
    }

    private void Sahf()
    {
        // SAHF loads SF, ZF, AF, PF and CF from AH -- bits 7, 6, 4, 2 and 0.
        // The three bits between them are not flags: bit 1 always reads as 1
        // and bits 3 and 5 as 0, whatever AH holds. The 386EX vectors show it
        // (AH = FE gives a low byte of D6), and so does the SDM's definition,
        // EFLAGS(SF:ZF:0:AF:0:PF:1:CF) <- AH. Copying AH whole let a program
        // plant 1s in bits 3 and 5 and read them back through PUSHF or LAHF.
        const uint loadedFlags = 0xD5;
        const uint alwaysOne = 0x02;

        uint ah = State.GetReg8(4);
        uint value = (Flags.Value & 0xFFFFFF00) | (ah & loadedFlags) | alwaysOne;
        Flags.SetValue(value);
    }

    private void Lahf() => State.SetReg8(4, (byte)(Flags.Value & 0xFF));

    // --- sign extension --------------------------------------------------------

    /// <summary>CBW/CWDE: widens AL into AX, or AX into EAX.</summary>
    private void Cbw()
    {
        if (OperandSize32) State.Eax = (uint)(short)State.Ax;
        else State.Ax = (ushort)(sbyte)State.GetReg8(0);
    }

    /// <summary>CWD/CDQ: sign-extends AX into DX:AX, or EAX into EDX:EAX.</summary>
    private void Cwd()
    {
        if (OperandSize32) State.Edx = (State.Eax & 0x80000000) != 0 ? 0xFFFFFFFF : 0;
        else State.Dx = (ushort)((State.Ax & 0x8000) != 0 ? 0xFFFF : 0);
    }

    // --- addresses and jumps ---------------------------------------------------

    private void Lea()
    {
        var m = DecodeModRm();
        // LEA computes an address without ever touching memory; with mod == 3
        // there is no address to compute, and real hardware treats it as an
        // invalid instruction.
        if (m.IsRegister) throw new CpuException(6);
        WriteRegOperand(m, m.Offset);
    }

    /// <summary>
    /// Relative jump. In 16-bit mode EIP is truncated to 16 bits, and that
    /// truncation is what makes execution wrap around inside the segment
    /// instead of escaping past its end.
    /// </summary>
    private void JumpRelative(int offset)
    {
        uint target = (uint)(State.Eip + offset);
        State.Eip = OperandSize32 ? target : target & 0xFFFF;
    }

    /// <summary>
    /// Direct far CALL (0x9A): the complete segment:offset destination is
    /// encoded in the instruction itself. It pushes the return CS and IP before
    /// jumping, so that a RETF can find its way back.
    ///
    /// This is how 16-bit code calls routines outside its own segment, so DOS
    /// leans on it constantly for its internal calls.
    /// </summary>
    /// <summary>
    /// Refuses a direct far jump or call into code at a different privilege.
    ///
    /// Entering more privileged code has to go through a call gate, which is
    /// the whole point of gates: they are the only doors in the wall, and each
    /// names the single entry point the kernel chose to expose. A direct far
    /// call into ring 0 would be a door of the caller's own choosing.
    ///
    /// This check belongs here rather than in the general segment load,
    /// because that path also carries the transfers where the processor is
    /// legitimately changing privilege -- through a gate, through an interrupt,
    /// returning from one -- and refusing those breaks the mechanism this is
    /// meant to protect.
    /// </summary>
    private void RequireSamePrivilegeTarget(ushort selector)
    {
        if (!State.ProtectedMode || State.Virtual8086Mode) return;
        if ((selector & 0xFFFC) == 0) return;

        ulong descriptor;
        try { descriptor = ReadDescriptor(selector); }
        catch (CpuException) { return; }        // the load itself will fault

        byte access = (byte)(descriptor >> 40);
        if ((access & 0x18) != 0x18) return;    // not a code segment

        byte dpl = (byte)((access >> 5) & 3);
        bool conforming = (access & 0x04) != 0;

        // Conforming code runs at the caller's level, so entering it grants
        // nothing; its DPL is a floor the caller must already be below.
        if (conforming)
        {
            if (dpl > State.Cpl) throw new CpuException(13, selector);
            return;
        }

        if (dpl != State.Cpl) throw new CpuException(13, selector);
    }

    private void CallFar()
    {
        // As in JumpFar: the operand size is the calling segment's, and CS is
        // about to change. Settled here, before anything moves.
        bool wide = OperandSize32;

        uint offset = FetchImmediate();
        ushort selector = FetchWord();

        // Under protection the selector may name a call gate rather than a
        // code segment, and then the offset in the instruction is ignored
        // entirely: the gate holds the real destination.
        if (State.ProtectedMode && !State.Virtual8086Mode && IsCallGate(selector))
        {
            CallThroughGate(selector);
            return;
        }

        // A call to a task, unlike a jump to one, keeps the way back: the new
        // task's link field records where it came from and its flags carry
        // NT, so that its IRET returns to this task rather than popping a
        // frame that was never pushed.
        if (State.ProtectedMode && !State.Virtual8086Mode
            && (IsTaskSegment(selector) || IsTaskGateSelector(selector)))
        {
            SwitchTask(selector, TaskSwitchKind.Call);
            return;
        }

        RequireSamePrivilegeTarget(selector);
        CallFarInto(selector, offset, wide);
    }

    /// <summary>
    /// The tail of a far call to a code segment: return address on the stack,
    /// then the new CS and EIP. The push order is fixed by the architecture,
    /// segment first, then offset, so that RETF pops them back in the
    /// opposite order.
    ///
    /// A fault while loading the new segment -- not present, for one -- must
    /// leave the stack where it was. The processor checks the descriptor
    /// before it writes anything; here the writes come first, so the stack
    /// pointer is put back when the check fails. Windows depends on this: it
    /// keeps code segments not present until they are called, loads them in
    /// the #NP handler, and restarts the call. A return address left behind
    /// by the first attempt would shift every argument of the second.
    /// </summary>
    private void CallFarInto(ushort selector, uint offset, bool wide)
    {
        uint stackPointer = State.Esp;
        PushOperand(State.Cs.Selector);
        PushOperand(State.Eip);

        try
        {
            LoadSegment(SegReg.Cs, selector);
        }
        catch (Exception ex) when (ex is CpuException or PageFaultException)
        {
            State.Esp = stackPointer;
            throw;
        }

        State.Eip = wide ? offset : offset & 0xFFFF;
    }

    /// <summary>
    /// XLAT (0xD7): replaces AL with the byte found at DS:[BX + AL].
    ///
    /// It is a whole table lookup in a single instruction, used for character
    /// translation: upper/lower case, code pages, national character sets. DOS
    /// uses it very heavily indeed.
    /// </summary>
    private void Xlat()
    {
        SegReg segment = EffectiveSegment(SegReg.Ds);
        uint index = AddressSize32
            ? State.Ebx + State.GetReg8(0)
            : (uint)(ushort)(State.Bx + State.GetReg8(0));

        State.SetReg8(0, ReadMem8(segment, index));
    }

    private void JumpFar()
    {
        // The operand size belongs to the segment being jumped *from*, so it
        // has to be settled before CS moves. Reading it afterwards asks the
        // destination how wide the offset was, which is the wrong question and
        // gives the wrong answer precisely when the two differ -- a 32-bit
        // segment jumping to a 16-bit one, which is how every DOS extender
        // returns to real mode. The offset then keeps sixteen bits it should
        // have dropped, or loses sixteen it should have kept, and control
        // lands at an address nothing put there.
        bool wide = OperandSize32;

        uint offset = FetchImmediate();
        ushort selector = FetchWord();

        if (State.ProtectedMode && !State.Virtual8086Mode && IsCallGate(selector))
        {
            JumpThroughGate(selector);
            return;
        }

        // A selector naming a task state segment does not jump anywhere: it
        // replaces the whole running context with another one. The offset in
        // the instruction is discarded, because the destination's EIP comes
        // out of its own saved state rather than from here.
        if (State.ProtectedMode && !State.Virtual8086Mode
            && (IsTaskSegment(selector) || IsTaskGateSelector(selector)))
        {
            SwitchTask(selector, TaskSwitchKind.Jump);
            return;
        }

        RequireSamePrivilegeTarget(selector);
        LoadSegment(SegReg.Cs, selector);
        State.Eip = wide ? offset : offset & 0xFFFF;
    }

    /// <summary>
    /// The limit a segment would end up with if this selector were loaded. In
    /// real mode it is always 64 KB minus one, but the far returns need to know
    /// it *before* they touch CS, so the question has to be asked separately.
    /// </summary>
    private static uint RealModeLimit(ushort selector) => 0xFFFF;

    /// <summary>
    /// POP into a segment register. The pop and the load are one instruction:
    /// if the load faults, the stack pointer has to be where it was before, or
    /// the handler that inspects the frame finds the popped value already
    /// gone and the pointer one slot too far.
    /// </summary>
    private void PopSegment(SegReg segment)
    {
        uint espBefore = State.Esp;
        try { LoadSegment(segment, (ushort)PopSegmentOperand()); }
        catch (CpuException) { State.Esp = espBefore; throw; }
    }

    private void RetFar(ushort popBytes)
    {
        // RETF from compatibility mode follows IA-32e mode's rules, and may
        // return to 64-bit code.
        if (State.LongModeActive)
        {
            FarReturn64(popBytes, OperandSize32 ? 4 : 2);
            return;
        }

        uint espBefore = State.Esp;
        SegmentCache codeBefore = State.Cs;
        SegmentCache stackBefore = State.Ss;
        byte privilegeBefore = State.Cpl;
        try
        {
            // Only the 32-bit form reads the block in one go.
            if (OperandSize32) CheckStackBlock(8u);
            uint offset = PopOperand();
            ushort selector = (ushort)PopOperand();
            uint destination = OperandSize32 ? offset : offset & 0xFFFF;

            if (!State.ProtectedMode || State.Virtual8086Mode)
            {
                // The limit is checked against the segment about to be loaded,
                // but before it is loaded: if the address does not fit, CS has
                // to stay as it was, because it is the old CS that ends up in
                // the exception's frame and that the handler will report.
                if (destination > RealModeLimit(selector)) throw new CpuException(13, 0);
                LoadSegment(SegReg.Cs, selector);
                State.Eip = destination;
            }
            else
            {
                // Under protection the limit is the descriptor's, not 64 KB:
                // asking the real-mode question here refused every return to
                // an address above 0xFFFF in a flat segment, which is to say
                // every far return a 32-bit program makes.
                if ((selector & 0xFFFC) == 0) throw new CpuException(13, 0);
                byte returnPrivilege = (byte)(selector & 3);
                if (returnPrivilege < State.Cpl) throw new CpuException(13, selector);   // no returning inward
                // Windows discards code segments and reuses the absent
                // descriptor's base/limit fields as loader bookkeeping.
                // RET must report #NP before consulting that placeholder
                // limit, so the loader can bring the return segment back.
                ulong returnDescriptor = ReadDescriptor(selector);
                byte access = (byte)(returnDescriptor >> 40);
                bool codeSegment = (access & 0x18) == 0x18;
                bool conforming = (access & 0x04) != 0;
                byte descriptorPrivilege = (byte)((access >> 5) & 3);
                if (!codeSegment || descriptorPrivilege > returnPrivilege
                    || (!conforming && descriptorPrivilege != returnPrivilege))
                {
                    throw new CpuException(13, selector);
                }
                if ((access & 0x80) == 0)
                {
                    throw new CpuException(11, selector);
                }
                if (destination > DescriptorLimitOf(returnDescriptor))
                {
                    throw new CpuException(13, 0);
                }

                if (returnPrivilege > State.Cpl)
                {
                    // A call gate copies parameters onto the inner stack,
                    // between CS:IP and the saved outer SS:SP. RET n must
                    // skip that copy before restoring the outer stack, then
                    // discard the original parameters from that stack too.
                    // SYSDETMG's RETF 4 otherwise reads its IDE port argument
                    // as SP and the following zero word as SS.
                    uint outerFrameBytes = OperandSize32 ? 8u : 4u;
                    CheckStackBlock((uint)popBytes + outerFrameBytes);
                    AdjustStack(popBytes);
                    uint returnEsp = PopOperand();
                    ushort returnSs = (ushort)PopOperand();

                    LoadSegmentProtected(SegReg.Cs, selector);
                    State.Cpl = returnPrivilege;
                    LoadSegmentProtected(SegReg.Ss, returnSs);
                    State.Esp = returnEsp;
                    State.Eip = destination;
                    DropInaccessibleDataSegments(returnPrivilege);
                    AdjustStack(popBytes);
                    return;
                }

                LoadSegmentProtected(SegReg.Cs, selector);
                State.Eip = destination;
            }
        }
        catch (CpuException)
        {
            // A failure loading the outer stack must not leave the caller's
            // CS or privilege installed. The exception handler needs the
            // original instruction and stack frame to diagnose or retry RET.
            State.Cs = codeBefore;
            State.Ss = stackBefore;
            State.Cpl = privilegeBefore;
            State.Esp = espBefore;
            throw;
        }
        AdjustStack(popBytes);
    }

    /// <summary>Removes the bytes that RET n asks to discard from the stack.</summary>
    private void AdjustStack(ushort bytes)
    {
        if (bytes == 0) return;
        if (State.Ss.Big) State.Esp += bytes;
        else State.Sp += bytes;
    }

    private void Leave()
    {
        // LEAVE undoes ENTER: it moves EBP into ESP and then restores EBP.
        uint espBefore = State.Esp;
        try
        {
            if (State.Ss.Big) State.Esp = State.Ebp; else State.Sp = State.Bp;
            if (OperandSize32) State.Ebp = Pop32(); else State.Bp = Pop16();
        }
        catch (CpuException) { State.Esp = espBefore; throw; }
    }

    /// <summary>
    /// Loads a segment register. In real mode that means base = selector * 16;
    /// in protected mode it will go through the descriptor.
    ///
    /// Loading SS inhibits interrupts for the following instruction. That is an
    /// architectural guarantee rather than an implementation detail: switching
    /// stacks takes two instructions (MOV SS,.. followed by MOV SP,..) and
    /// between them SS:SP points at an area that does not exist. An interrupt
    /// arriving right there would push its own context somewhere arbitrary,
    /// corrupting memory and leaving the caller's stack in a state the
    /// interrupted code has no way to anticipate.
    /// </summary>
    /// <summary>
    /// Raised whenever a segment register is loaded, with the value that is
    /// about to go into it. Diagnostics use it to catch a selector arriving
    /// from somewhere it should not.
    /// </summary>
    public event Action<SegReg, ushort, uint>? SegmentLoaded;

    private void LoadSegment(SegReg segment, ushort selector)
    {
        SegmentLoaded?.Invoke(segment, selector, _instructionStart);

        if (segment == SegReg.Ss) _interruptShadow = true;

        // Virtual 8086 mode loads segments the real-mode way -- base is the
        // selector times sixteen, limit is 64 KB -- because that is the whole
        // point of it: an unmodified DOS program has to compute the addresses
        // it has always computed. What differs is everything around it. The
        // address it arrives at is linear, not physical, so paging can put
        // each task's first megabyte somewhere else entirely, and privileged
        // instructions still trap to the monitor.
        if (State.Virtual8086Mode)
        {
            // The reset variant: a task is confined to the first megabyte and
            // must not be able to widen a segment past 64 KB, which the
            // ordinary real-mode load would allow by leaving the limit alone.
            State.LoadSegmentRealModeReset(segment, selector);
            return;
        }

        if (!State.ProtectedMode)
        {
            State.LoadSegmentRealMode(segment, selector);
            return;
        }
        LoadSegmentProtected(segment, selector);
    }

    // --- jump conditions -------------------------------------------------------

    /// <summary>
    /// Evaluates the condition of a Jcc/SETcc/CMOVcc. Bit 0 of the condition
    /// code inverts the test, which is why the cases come in pairs -- and why a
    /// single table serves all three instruction families.
    /// </summary>
    private bool TestCondition(byte condition) => condition switch
    {
        0x0 => Flags.Overflow,
        0x1 => !Flags.Overflow,
        0x2 => Flags.Carry,                                  // below
        0x3 => !Flags.Carry,                                 // above or equal
        0x4 => Flags.Zero,
        0x5 => !Flags.Zero,
        0x6 => Flags.Carry || Flags.Zero,                    // below or equal
        0x7 => !Flags.Carry && !Flags.Zero,                  // above
        0x8 => Flags.Sign,
        0x9 => !Flags.Sign,
        0xA => Flags.Parity,
        0xB => !Flags.Parity,
        0xC => Flags.Sign != Flags.Overflow,                 // less
        0xD => Flags.Sign == Flags.Overflow,                 // greater or equal
        0xE => Flags.Zero || Flags.Sign != Flags.Overflow,   // less or equal
        _ => !Flags.Zero && Flags.Sign == Flags.Overflow,    // greater
    };

    // --- interrupts ------------------------------------------------------------

    /// <summary>
    /// INT n. In real mode the vector is read out of the table at address 0:
    /// four bytes per vector, offset first and then segment.
    /// </summary>
    /// <summary>
    /// Raised before every INT instruction the guest executes, with the vector
    /// number. Diagnostics hang from this to watch a particular DOS call go by
    /// without having to single-step the whole machine.
    /// </summary>
    public event Action<byte>? SoftwareInterruptIssued;

    private void SoftwareInterrupt(byte vector)
    {
        SoftwareInterruptIssued?.Invoke(vector);
        EnterInterrupt(vector, isSoftware: true);
    }

    /// <summary>An exception raised by the processor itself.</summary>
    /// <summary>Raised whenever the processor itself signals a fault.</summary>
    public event Action<byte, uint, uint, bool>? ProcessorException;

    private void RaiseException(byte vector, uint errorCode)
    {
        ProcessorException?.Invoke(vector, errorCode, _instructionStart, State.ProtectedMode);

        // In protected mode some exceptions also push an error code; in real
        // mode there is no such thing and it is simply ignored.
        EnterInterrupt(vector, isSoftware: false, errorCode, isException: true);
    }

    private void EnterInterrupt(byte vector, bool isSoftware, uint errorCode = 0, bool isException = false)
    {
        // Once long mode is active every interrupt goes through the 64-bit
        // IDT, from compatibility-mode code as well (Cpu.LongMode.Interrupts.cs).
        if (State.LongModeActive)
        {
            EnterInterruptLongMode(vector, errorCode, isSoftware, isException);
            return;
        }

        if (State.ProtectedMode)
        {
            EnterInterruptProtected(vector, errorCode, isSoftware, isException);
            return;
        }

        // Real mode: save FLAGS, CS and IP, then jump through the vector.
        Push16((ushort)Flags.Value);
        Push16(State.Cs.Selector);
        Push16((ushort)State.Eip);

        Flags.Interrupt = false;   // interrupts start out disabled in the handler
        Flags.Trap = false;

        uint tableEntry = (uint)vector * 4;
        ushort offset = _memory.ReadWord(tableEntry);
        ushort segment = _memory.ReadWord(tableEntry + 2);

        State.LoadSegmentRealMode(SegReg.Cs, segment);
        State.Eip = offset;
    }

    /// <summary>
    /// IRET inside a virtual 8086 task. IOPL-sensitive like the other flag
    /// instructions: below IOPL 3 it traps to the monitor as #GP(0), which is
    /// how the monitor sees every interrupt return the task makes and keeps
    /// its virtual interrupt flag honest -- unless CR4.VME is on, in which
    /// case Table 20-2 replaces that trap with the same virtualized image
    /// POPF applies (Cpu.VirtualMode.cs's ApplyVirtualInterruptImage), faulting
    /// like POPF on TF = 1 or on IF = 1 with VIP = 1 in the image: the SDM
    /// IRET pseudocode and Bochs agree on this, against the note's reading of
    /// Collins (unverified against silicon). At
    /// IOPL 3 it is a real-mode return within the task -- 16-bit pops, a
    /// paragraph loaded into CS, a 64 KB limit -- with the flags filtered so
    /// the task can change neither IOPL nor VM.
    /// </summary>
    private void IretVirtual8086()
    {
        // Only the 16-bit IRET is virtualised: an IRETD below IOPL 3 traps
        // to the monitor with VME on exactly as without it (SDM IRET).
        bool virtualized = VmeBelowIopl3() && !OperandSize32;
        if (!virtualized) RequireIoPrivilege();

        uint espBefore = State.Esp;
        try
        {
            uint returnEip, returnFlags;
            ushort returnCs;

            if (OperandSize32)
            {
                returnEip = Pop32();
                returnCs = (ushort)Pop32();
                returnFlags = Pop32();
            }
            else
            {
                returnEip = Pop16();
                returnCs = Pop16();
                returnFlags = (Flags.Value & 0xFFFF0000) | Pop16();
            }

            // Computed -- and, on the virtualized path, potentially faulted
            // on -- before CS or EIP moves, so a #GP(0) here (VIP = 1 and the
            // image asks for interrupts) leaves the whole IRET looking like
            // it never started once the catch below restores ESP.
            uint guardedFlags = virtualized
                ? ApplyVirtualInterruptImage(returnFlags, trapFlagFaults: true)
                : GuardFlagsFromStack(returnFlags, loadsVirtualInterruptBits: false);

            // LoadSegment routes a V86 load to the real-mode form that also
            // resets the limit, which is the confinement the task must keep.
            LoadSegment(SegReg.Cs, returnCs);
            State.Eip = returnEip & 0xFFFF;
            Flags.SetValue(guardedFlags);
        }
        catch (CpuException)
        {
            State.Esp = espBefore;
            throw;
        }
    }

    private void Iret()
    {
        // A virtual 8086 task has to be told apart from ordinary protected
        // code before anything else: it is protected mode as far as CR0 is
        // concerned, but its stack holds real-mode segment values, and
        // handing those to the protected-mode IRET makes it load a paragraph
        // number as a selector -- #GP with that number as the error code,
        // which is exactly what a monitor does not expect and cannot emulate.
        if (State.Virtual8086Mode)
        {
            IretVirtual8086();
            return;
        }

        // IRET from compatibility mode is IA-32e mode's IRET: it pops SS:ESP
        // only on a return to an outer level, and may return to 64-bit code
        // (SDM Vol.2 IRET, IA-32e-MODE).
        if (State.LongModeActive)
        {
            InterruptReturnLongMode(OperandSize32 ? 4 : 2);
            return;
        }

        if (State.ProtectedMode)
        {
            IretProtected();
            return;
        }

        // IRET restores the whole flags register, IF included: that is how
        // interrupts get re-enabled on the way out of a handler, without the
        // handler having to remember whether they were on when it was entered.
        // With an operand-size prefix every stacked entry occupies four bytes,
        // and the CS selector wastes two of them.
        uint espBefore = State.Esp;
        try
        {
            if (OperandSize32)
            {
                uint destination = Pop32();
                ushort selector = (ushort)Pop32();
                if (destination > RealModeLimit(selector)) throw new CpuException(13, 0);
                State.LoadSegmentRealMode(SegReg.Cs, selector);
                State.Eip = destination;
                // A real-mode IRETD loads (image AND 257FD5h) and keeps VIF, VIP
                // and VM (SDM IRET); the ID and AC rules still apply.
                const uint realModeLoaded = 0x257FD5;
                uint image = (Pop32() & realModeLoaded) | (Flags.Value & ~realModeLoaded);
                Flags.SetValue(GuardFlagsFromStack(image, loadsVirtualInterruptBits: false));
            }
            else
            {
                uint destination = Pop16();
                ushort selector = Pop16();
                if (destination > RealModeLimit(selector)) throw new CpuException(13, 0);
                State.LoadSegmentRealMode(SegReg.Cs, selector);
                State.Eip = destination;
                Flags.SetValue((Flags.Value & 0xFFFF0000) | Pop16());
            }
        }
        catch (CpuException) { State.Esp = espBefore; throw; }
    }
}

public sealed partial class Cpu
{
    // --- I/O ports at the current operand size ---------------------------------

    private void ReadPortToAccumulator(ushort port)
    {
        CheckIoPermission(port, OperandSize32 ? 4 : 2);
        if (OperandSize32) State.Eax = _io.ReadDword(port);
        else State.Ax = _io.ReadWord(port);
    }

    private void WriteAccumulatorToPort(ushort port)
    {
        CheckIoPermission(port, OperandSize32 ? 4 : 2);
        if (OperandSize32) _io.WriteDword(port, State.Eax);
        else _io.WriteWord(port, State.Ax);
    }

    // --- three-operand IMUL -----------------------------------------------------

    /// <summary>
    /// IMUL reg, r/m, imm. Unlike the one-operand form it leaves DX:AX alone:
    /// the whole result goes into the destination register, and CF/OF report
    /// whether it had to be truncated to fit.
    /// </summary>
    private void ImulImmediate(in ModRm m, uint immediate)
    {
        if (OperandSize32)
        {
            long result = (long)(int)ReadRm32(m) * (int)immediate;
            State.SetReg32(m.Reg, (uint)result);
            bool overflow = result != (int)result;
            Flags.Set(FlagOp.Imul, 0, (uint)(overflow ? 1 : 0), (uint)result, 32);
        }
        else
        {
            int result = (short)ReadRm16(m) * (short)immediate;
            State.SetReg16(m.Reg, (ushort)result);
            bool overflow = result != (short)result;
            Flags.Set(FlagOp.Imul, 0, (uint)(overflow ? 1 : 0), (uint)result, 16);
        }
    }

    // --- far pointers -----------------------------------------------------------

    /// <summary>
    /// LES/LDS: load a complete far pointer from memory, the offset into a
    /// general register and the segment into the named segment register. It is
    /// how 16-bit code passes pointers that reach beyond a single segment's
    /// 64 KB.
    /// </summary>
    private void LoadFarPointer(SegReg segment)
    {
        var m = DecodeModRm();
        if (m.IsRegister) throw new CpuException(6);

        uint offset = ReadMemOperand(m.Segment, m.Offset);
        // The selector follows the offset, and its address wraps inside the
        // segment: with an offset of 0xFFFE the selector is read back at 0x0000
        // rather than past the end. Only a single access straddling the limit
        // is an error; two separate accesses each wrap on their own.
        uint selectorOffset = m.Offset + (OperandSize32 ? 4u : 2u);
        // The wrap follows the address size in effect, not the segment's B bit:
        // with the 0x67 prefix the offset stays 32-bit even inside a 16-bit
        // segment.
        if (!AddressSize32) selectorOffset &= 0xFFFF;
        ushort selector = ReadMem16(m.Segment, selectorOffset);

        WriteRegOperand(m, offset);
        LoadSegment(segment, selector);
    }

    // --- ENTER -------------------------------------------------------------------

    /// <summary>
    /// ENTER: builds the stack frame for a nested procedure. The nesting level
    /// copies pointers to the enclosing frames, a mechanism designed for
    /// languages like Pascal that allow nested procedures to see their parents'
    /// locals; C code always uses level 0, which reduces ENTER to a push of BP
    /// plus a subtraction.
    /// </summary>
    private void Enter()
    {
        ushort frameSize = FetchWord();
        byte level = (byte)(FetchByte() & 0x1F);

        // The entries to copy are read at descending BP: the processor checks
        // that all of them fall inside the segment before it touches the stack
        // at all, so if one overruns it does not even perform the initial push
        // of BP. That all-or-nothing behaviour is what lets a fault be retried.
        if (!State.Ss.Big && level > 1)
        {
            uint entry = OperandSize32 ? 4u : 2u;
            for (int k = 1; k < level; k++)
                CheckLimit(SegReg.Ss, (uint)(ushort)(State.Bp - entry * (uint)k), entry);
        }

        PushOperand(State.Ebp);
        uint framePointer = State.Ss.Big ? State.Esp : State.Sp;

        for (int i = 1; i < level; i++)
        {
            // The pointer the nested frames are copied from follows the stack
            // width, not the operand size: with a 16-bit SS it stays BP,
            // otherwise EBP.
            // The pointer descends by one entry and only then reads what it
            // finds: reading before descending would copy the current frame
            // instead of the one enclosing it.
            // The push is checked for room first and the entry read second, so
            // that when the space runs out the processor stops having written
            // nothing, leaving the stack exactly as the previous iteration did.
            if (OperandSize32)
            {
                if (State.Ss.Big) State.Ebp -= 4; else State.Bp -= 4;
                if (!State.Ss.Big) CheckLimit(SegReg.Ss, (uint)(ushort)(State.Sp - 4), 4);
                uint entry = ReadMem32(SegReg.Ss, State.Ss.Big ? State.Ebp : State.Bp);
                Push32(entry);
            }
            else
            {
                if (State.Ss.Big) State.Ebp -= 2; else State.Bp -= 2;
                if (!State.Ss.Big) CheckLimit(SegReg.Ss, (uint)(ushort)(State.Sp - 2), 2);
                ushort entry = ReadMem16(SegReg.Ss, State.Ss.Big ? State.Ebp : State.Bp);
                Push16(entry);
            }
        }

        if (level > 0) PushOperand(framePointer);

        // With 32-bit operands EBP receives the whole pointer even when the
        // stack is 16-bit: in that case the top half simply comes out zeroed,
        // because framePointer was taken from SP.
        if (OperandSize32) State.Ebp = framePointer;
        else State.Bp = (ushort)framePointer;

        if (State.Ss.Big) State.Esp -= frameSize;
        else State.Sp -= frameSize;
    }

    // --- loops ---------------------------------------------------------------------

    /// <summary>
    /// LOOP, LOOPE and LOOPNE: decrement CX and branch while it is non-zero,
    /// optionally also testing ZF. The counter is CX or ECX according to the
    /// address size, not the operand size -- an easy detail to get backwards.
    /// </summary>
    private void LoopCondition(bool zeroRequired, bool useZeroFlag)
    {
        sbyte offset = (sbyte)FetchByte();

        uint remaining;
        if (AddressSize32) remaining = --State.Ecx;
        else { State.Cx = (ushort)(State.Cx - 1); remaining = State.Cx; }

        bool take = remaining != 0;
        if (take && useZeroFlag) take = Flags.Zero == zeroRequired;

        if (take) JumpRelative(offset);
    }

    /// <summary>JCXZ/JECXZ: branch when the counter is already zero.</summary>
    private void JumpIfCxZero()
    {
        sbyte offset = (sbyte)FetchByte();
        uint count = AddressSize32 ? State.Ecx : State.Cx;
        if (count == 0) JumpRelative(offset);
    }

    /// <summary>
    /// Refuses an instruction that touches the outside world when the code
    /// running is not privileged enough to be trusted with it.
    ///
    /// The I/O privilege level in EFLAGS names the least privileged ring
    /// allowed to use the I/O instructions and to change the interrupt flag.
    /// A monitor sets it below 3 precisely so that these instructions trap:
    /// it can then look at what the task was trying to do and decide. That is
    /// how EMM386 lets a DOS program believe it is programming the hardware
    /// while the monitor quietly does something else, and how Windows keeps
    /// one DOS box from disabling interrupts for the whole machine.
    /// </summary>
    /// <summary>
    /// Refuses an instruction that only the kernel may execute.
    ///
    /// HLT, CLTS, LGDT, LIDT, LMSW and the control- and debug-register moves
    /// change how the whole machine behaves, so the architecture reserves them
    /// to ring 0. Without this check an application could stop the processor
    /// or rewrite the descriptor tables, and an operating system that relies on
    /// being able to run untrusted code -- which is what rings are for -- has
    /// no protection at all.
    ///
    /// Real mode has no privilege levels and permits everything; a V86 task is
    /// nominally at ring 3 and so is refused, which is how the monitor gets a
    /// chance to emulate what the task was trying to do.
    /// </summary>
    /// <summary>
    /// Raises the invalid-opcode fault when the processor being emulated
    /// does not carry the given feature. Deliberately a fault and not a
    /// silent no-op: a program probing for a 486 executes one of its new
    /// instructions and waits to see whether it faults, and CPUID itself is
    /// gated the same way for the processors that predate it. One field
    /// read and one bit test, same cost as the model comparison this
    /// replaces.
    /// </summary>
    private void RequireFeature(CpuFeature feature)
    {
        if (!State.Features.Has(feature)) throw new CpuException(6);
    }

    private void RequireSupervisor()
    {
        if (!State.ProtectedMode) return;
        if (State.Cpl != 0) throw new CpuException(13, 0);
    }

    private void RequireIoPrivilege()
    {
        if (!IoPrivileged()) throw new CpuException(13, 0);
    }

    /// <summary>True when the current ring may execute IOPL-sensitive instructions.</summary>
    private bool IoPrivileged()
    {
        if (!State.ProtectedMode) return true;
        byte iopl = (byte)((Flags.Value >> 12) & 3);
        return State.Cpl <= iopl;
    }

    /// <summary>
    /// The check every port access goes through. Under protection, code that
    /// is not privileged enough for IOPL -- and a virtual 8086 task always,
    /// whatever IOPL says -- is allowed a port only if the task state
    /// segment's I/O permission bitmap has a clear bit for every byte of it;
    /// a bit that is set, or a bitmap too short to hold the port, means #GP.
    ///
    /// This is the mechanism a monitor uses to virtualise devices: it marks
    /// the ports it wants to see and lets the rest through at full speed.
    /// Windows keeps a shadow of the display adapter's registers this way
    /// and never reads the hardware back, so a mode set the BIOS performs
    /// in a task must fault into the monitor port by port.
    /// </summary>
    private void CheckIoPermission(ushort port, int bytes)
    {
        if (!IoPermitted(port, bytes)) throw new CpuException(13, 0);
    }

    private bool IoPermitted(ushort port, int bytes)
    {
        if (!State.ProtectedMode) return true;

        if (!State.Virtual8086Mode)
        {
            byte iopl = (byte)((Flags.Value >> 12) & 3);
            if (State.Cpl <= iopl) return true;
        }

        if ((State.TaskRegister & 0xFFFC) == 0) return false;

        // In IA-32e mode the TSS is the 64-bit one LTR cached, reached
        // through its 64-bit base (Cpu.LongMode.SystemCall.cs).
        if (State.LongModeActive) return IoPermittedByLongModeBitmap(port, bytes);

        ulong descriptor = ReadDescriptor(State.TaskRegister);
        uint tssBase = (uint)(((descriptor >> 16) & 0xFFFFFF) | ((descriptor >> 32) & 0xFF000000));
        uint tssLimit = (uint)((descriptor & 0xFFFF) | ((descriptor >> 32) & 0x0F0000));
        if (((descriptor >> 55) & 1) != 0) tssLimit = (tssLimit << 12) | 0xFFF;

        // The map's offset lives at 0x66 in the segment. A segment too short
        // to hold even that word has no map, and nothing is permitted.
        if (tssLimit < 0x67) return false;
        // The I/O permission bitmap lives in the task state segment, which the
        // processor reads on its own behalf. These reads happen precisely when
        // ring 3 executes IN or OUT, so applying the current privilege to them
        // would make every port access from user mode fault on the segment
        // rather than on the permission it was meant to check.
        ushort mapBase = _memory.ReadWord(TranslateSupervisor(tssBase + 0x66, false));

        // Every byte of the access has its own bit; the last of them has to
        // lie inside the segment.
        uint firstByte = mapBase + (uint)(port >> 3);
        uint lastByte = mapBase + (uint)((port + bytes - 1) >> 3);
        if (lastByte > tssLimit) return false;

        int bits = _memory.ReadByte(TranslateSupervisor(tssBase + firstByte, false));
        if (lastByte != firstByte)
            bits |= _memory.ReadByte(TranslateSupervisor(tssBase + lastByte, false)) << 8;

        int mask = ((1 << bytes) - 1) << (port & 7);
        return (bits & mask) == 0;
    }
}
