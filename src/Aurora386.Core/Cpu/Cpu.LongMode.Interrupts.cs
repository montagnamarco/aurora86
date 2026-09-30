// Interrupts, exceptions and privilege transfers in IA-32e mode
// (plans/long-mode.md, stage D2). Once EFER.LMA is set every interrupt and
// exception -- from 64-bit code and from compatibility-mode code alike --
// goes through the 64-bit IDT: 16-byte interrupt and trap gates whose
// handlers run in 64-bit code segments. The frame is always SS, RSP, RFLAGS,
// CS, RIP and, for the exceptions that define one, an error code, pushed as
// qwords on a stack aligned down to 16 bytes, taken from the 64-bit TSS when
// the privilege rises or an IST slot is named. IRETQ undoes it; RETF and the
// far JMP/CALL through memory move between code segments. Sources: SDM
// Vol.3A §6.14 (64-bit mode exception and interrupt handling), §7.7 (the
// 64-bit TSS), Vol.2 IRET, RET, JMP, CALL, INT n; AMD APM Vol.2 §8.9.

using FlagBits = Aurora386.Core.Cpu.Flags;

namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>The two gate types an IA-32e IDT may hold: 64-bit interrupt gate (Eh) and 64-bit trap gate (Fh). Every other type is #GP (SDM Vol.3A §6.14.1).</summary>
    private const int InterruptGate64Type = 0x0E;
    private const int TrapGate64Type = 0x0F;

    /// <summary>Offsets in the 64-bit TSS: RSP0 at 4, RSP1 at 12, RSP2 at 20; IST1-IST7 from 24h (SDM Vol.3A Figure 7-11).</summary>
    private const uint TssRsp0Offset = 0x04;
    private const uint TssFirstIstOffset = 0x24;

    /// <summary>A 64-bit code descriptor: the L bit (53) set and the D bit (54) clear.</summary>
    private const ulong DescriptorLongBit = 1UL << 53;
    private const ulong DescriptorDefaultBigBit = 1UL << 54;

    /// <summary>RFLAGS bits 18-20: AC, VIF, VIP (ID, bit 21, is Cpu.Ops.cs's IdentificationFlag).</summary>
    private const uint AlignmentCheckFlag = 1u << 18;
    private const uint VirtualInterruptFlag = 1u << 19;
    private const uint VirtualInterruptPendingFlag = 1u << 20;

    /// <summary>
    /// Delivers an interrupt or exception in IA-32e mode. The gate, the
    /// handler's code segment and the new stack are all checked, and the
    /// whole frame written, before any register changes: a fault here is
    /// reported against the state the interrupt found (DeliverException's
    /// escalation relies on that).
    /// </summary>
    private void EnterInterruptLongMode(byte vector, uint errorCode, bool isSoftware, bool isException)
    {
        uint gateErrorCode = (uint)(vector * 8 + 2);

        // The 16-byte gate, read as a supervisor access.
        const uint GateSize = 16;
        uint offsetInTable = vector * GateSize;
        if (offsetInTable + GateSize - 1 > State.IdtLimit)
        {
            throw new CpuException(13, gateErrorCode);
        }

        ulong gateAddress = State.IdtBase64 + offsetInTable;
        ulong gateLow = ReadSupervisorQword64(gateAddress);
        ulong gateHigh = ReadSupervisorQword64(gateAddress + 8);

        byte gateAccess = (byte)(gateLow >> 40);
        int gateType = gateAccess & 0x1F;
        byte gateDpl = (byte)((gateAccess >> 5) & 3);
        bool gatePresent = (gateAccess & 0x80) != 0;

        if (gateType is not (InterruptGate64Type or TrapGate64Type))
        {
            throw new CpuException(13, gateErrorCode);
        }

        // The gate's DPL limits only INT n, INT3 and INTO -- not hardware
        // interrupts or exceptions (the same rule as the legacy path).
        if (isSoftware && gateDpl < State.Cpl)
        {
            throw new CpuException(13, gateErrorCode);
        }

        if (!gatePresent)
        {
            throw new CpuException(11, gateErrorCode);
        }

        ushort selector = (ushort)(gateLow >> 16);
        int istIndex = (int)((gateLow >> 32) & 7);
        ulong target = (gateLow & 0xFFFF) | ((gateLow >> 32) & 0xFFFF0000UL) | ((gateHigh & 0xFFFFFFFF) << 32);

        // The handler's code segment: a present, 64-bit code segment no less
        // privileged than the interrupted code.
        (ulong codeDescriptor, byte targetPrivilege) = CheckInterruptCodeSegment(selector);

        if (!IsCanonical(target))
        {
            throw new CpuException(13, 0);
        }

        // The stack: an IST slot if the gate names one, else RSPn from the TSS
        // when the privilege rises, else the current RSP -- then aligned down
        // to 16 bytes (SDM Vol.3A §6.14.2).
        bool privilegeRises = targetPrivilege < State.Cpl;
        ulong newStackPointer;
        if (istIndex != 0)
        {
            newStackPointer = ReadTss64(TssFirstIstOffset + (uint)(istIndex - 1) * 8);
        }
        else if (privilegeRises)
        {
            newStackPointer = ReadTss64(TssRsp0Offset + (uint)targetPrivilege * 8);
        }
        else
        {
            newStackPointer = State.Gpr[StackPointerRegister];
        }

        const ulong StackAlignmentMask = ~0xFUL;
        newStackPointer &= StackAlignmentMask;

        // The frame, written in full before anything is committed.
        bool pushesErrorCode = isException && ExceptionPushesErrorCode(vector);
        ulong[] frame = pushesErrorCode
            ? [errorCode, State.Rip, State.Cs.Selector, Flags.Value, State.Gpr[StackPointerRegister], State.Ss.Selector]
            : [State.Rip, State.Cs.Selector, Flags.Value, State.Gpr[StackPointerRegister], State.Ss.Selector];

        ulong frameTop = newStackPointer - (ulong)frame.Length * 8;
        bool supervisorStack = targetPrivilege != 3;
        for (int i = 0; i < frame.Length; i++)
        {
            WriteStackQword64(frameTop + (ulong)i * 8, frame[i], supervisorStack);
        }

        InterruptEntered?.Invoke(vector, State.Cs.Selector, (uint)State.Rip, false, isSoftware);

        // Commit: SS becomes a null selector carrying the new privilege when
        // the privilege rises (SDM Vol.3A §6.14.2), then CS, CPL, RSP, RIP
        // and the flags.
        if (privilegeRises)
        {
            LoadNullStackSegment(targetPrivilege);
        }

        LoadCodeSegmentLongMode(selector, codeDescriptor, targetPrivilege);
        State.Gpr[StackPointerRegister] = frameTop;
        State.Rip = target;

        Flags.Trap = false;
        ClearFlagBits(FlagBits.NestedTask | FlagBits.Resume);
        if (gateType == InterruptGate64Type)
        {
            Flags.Interrupt = false;
        }
    }

    /// <summary>
    /// The interrupt handler's code segment checks (SDM Vol.3A §6.14.1): not
    /// null, a code segment, 64-bit (L=1, D=0), DPL no greater than CPL (a
    /// conforming segment runs at CPL), present. Returns the descriptor and
    /// the privilege the handler runs at.
    /// </summary>
    private (ulong Descriptor, byte Privilege) CheckInterruptCodeSegment(ushort selector)
    {
        if ((selector & 0xFFFC) == 0)
        {
            throw new CpuException(13, 0);
        }

        ulong descriptor = ReadDescriptorLongMode(selector, offsetInDescriptor: 0);
        byte access = (byte)(descriptor >> 40);
        bool isCode = (access & 0x18) == 0x18;
        bool conforming = (access & 0x04) != 0;
        bool longCode = (descriptor & DescriptorLongBit) != 0 && (descriptor & DescriptorDefaultBigBit) == 0;
        byte dpl = (byte)((access >> 5) & 3);

        if (!isCode || !longCode)
        {
            throw new CpuException(13, selector & 0xFFFCu);
        }

        if (dpl > State.Cpl)
        {
            throw new CpuException(13, selector & 0xFFFCu);
        }

        if ((access & 0x80) == 0)
        {
            throw new CpuException(11, selector & 0xFFFCu);
        }

        byte privilege = conforming ? State.Cpl : dpl;
        return (descriptor, privilege);
    }

    /// <summary>Reads a qword of the 64-bit TSS; beyond its limit is #TS(TR's selector).</summary>
    private ulong ReadTss64(uint offset)
    {
        if (offset + 7 > State.TssLimit)
        {
            throw new CpuException(10, State.TaskRegister & 0xFFFCu);
        }

        return ReadSupervisorQword64(State.TssBase64 + offset);
    }

    /// <summary>
    /// Writes one qword of an interrupt or call frame. The address has to be
    /// canonical (#SS(0) otherwise, the stack's fault); the page is checked
    /// as the target privilege would see it, since the frame lands on the
    /// handler's stack, not the interrupted code's.
    /// </summary>
    private void WriteStackQword64(ulong linear, ulong value, bool asSupervisor)
    {
        if (!IsCanonical(linear) || !IsCanonical(linear + 7))
        {
            throw new CpuException(12, 0);
        }

        bool crossesPage = (linear & 0xFFF) > 0xFF8;
        if (!crossesPage)
        {
            uint physical = TranslateLongMode(linear, write: true, asSupervisor, execute: false);
            _memory.WriteDword(physical, (uint)value);
            _memory.WriteDword(physical + 4, (uint)(value >> 32));
            return;
        }

        Span<uint> physicalBytes = stackalloc uint[8];
        for (int i = 0; i < 8; i++)
        {
            physicalBytes[i] = TranslateLongMode(linear + (ulong)i, write: true, asSupervisor, execute: false);
        }

        for (int i = 0; i < 8; i++)
        {
            _memory.WriteByte(physicalBytes[i], (byte)(value >> (8 * i)));
        }
    }

    /// <summary>
    /// Installs a code segment already checked by the caller, with its RPL
    /// and the CPL set to <paramref name="privilege"/>: the descriptor's own
    /// cache fields, and the L bit that decides 64-bit versus compatibility.
    /// </summary>
    private void LoadCodeSegmentLongMode(ushort selector, ulong descriptor, byte privilege)
    {
        ref SegmentCache cs = ref State.Segments[(int)SegReg.Cs];
        byte access = (byte)(descriptor >> 40);

        cs.Selector = (ushort)((selector & 0xFFFC) | privilege);
        cs.Base = DescriptorBase(descriptor);
        cs.BaseHigh = 0;
        cs.Limit = DescriptorLimitOf(descriptor);
        cs.Big = (descriptor & DescriptorDefaultBigBit) != 0;
        cs.Long = (descriptor & DescriptorLongBit) != 0;
        cs.Dpl = (byte)((access >> 5) & 3);
        cs.Readable = (access & 0x02) != 0;
        cs.Writable = false;
        cs.ExpandDown = false;

        State.Cpl = privilege;
        InvalidateFetchPage();
    }

    /// <summary>SS as IA-32e mode leaves it after a privilege change: a null selector whose RPL is the new CPL.</summary>
    private void LoadNullStackSegment(byte privilege)
    {
        ref SegmentCache ss = ref State.Segments[(int)SegReg.Ss];
        ss.Selector = privilege;
        ss.Base = 0;
        ss.BaseHigh = 0;
        ss.Dpl = privilege;
        ss.Writable = true;
        ss.Readable = true;
        ss.Big = true;
    }

    private void ClearFlagBits(FlagBits bits)
    {
        uint value = Flags.Value & ~(uint)bits;
        Flags.SetValue(value);
    }

    // === IRET in IA-32e mode ===================================================

    /// <summary>
    /// IRET/IRETD/IRETQ in IA-32e mode, from 64-bit or compatibility code
    /// (SDM Vol.2 IRET, "IA-32e mode"): RIP, CS and RFLAGS are popped at the
    /// operand size (8 with REX.W, 4 by default, 2 with 66h), then RSP and SS
    /// when IRET began in 64-bit mode or returns to an outer level. In
    /// compatibility mode the stack is read at its 64-bit linear address,
    /// ignoring SS's base and limit and a 16-bit SP -- a gap left for stage E.
    /// Everything is read and checked before anything is committed. A return
    /// to a task (NT set) does not exist in IA-32e mode: #GP(0).
    /// </summary>
    private void InterruptReturnLongMode(int operandBytes)
    {
        if ((Flags.Value & (uint)FlagBits.NestedTask) != 0)
        {
            throw new CpuException(13, 0);
        }

        ulong stackPointer = State.Gpr[StackPointerRegister];
        ulong newRip = ReadStackValue(stackPointer, operandBytes);
        ushort newCsSelector = (ushort)ReadStackValue(stackPointer + (ulong)operandBytes, operandBytes);
        uint poppedFlags = (uint)ReadStackValue(stackPointer + 2UL * (ulong)operandBytes, operandBytes);

        (ulong codeDescriptor, byte newPrivilege) = CheckReturnCodeSegment(newCsSelector);
        bool toLongCode = IsSixtyFourBitCodeDescriptor(codeDescriptor);
        if (toLongCode ? !IsCanonical(newRip) : newRip > DescriptorLimitOf(codeDescriptor))
        {
            throw new CpuException(13, 0);
        }

        // SS:RSP is popped when IRET began in 64-bit mode, or when it returns
        // to an outer privilege level; an IRET in compatibility mode to the
        // same level leaves SS and ESP where the three pops put them (SDM
        // Vol.2 IRET, IA-32e-MODE; AMD APM Vol.3 IRET). Review of stage D.
        byte oldPrivilege = State.Cpl;
        bool popsStack = _executingLongModeInstruction || newPrivilege > oldPrivilege;
        ulong newStackPointer = stackPointer + 3UL * (ulong)operandBytes;
        ushort newSsSelector = State.Ss.Selector;
        ulong? stackDescriptor = null;
        if (popsStack)
        {
            newStackPointer = ReadStackValue(stackPointer + 3UL * (ulong)operandBytes, operandBytes);
            newSsSelector = (ushort)ReadStackValue(stackPointer + 4UL * (ulong)operandBytes, operandBytes);
            stackDescriptor = CheckReturnStackSegment(newSsSelector, newPrivilege, toLongCode);
        }

        // Commit.
        uint newFlags = FlagsAfterInterruptReturn(poppedFlags, operandBytes);
        LoadCodeSegmentLongMode(newCsSelector, codeDescriptor, newPrivilege);
        if (popsStack)
        {
            LoadReturnStackSegment(newSsSelector, stackDescriptor, newPrivilege);
        }

        State.Gpr[StackPointerRegister] = newStackPointer;
        State.Rip = newRip;
        Flags.SetValue(newFlags);

        if (newPrivilege > oldPrivilege)
        {
            NullDataSegmentsAboveReturnPrivilege(newPrivilege);
        }
    }

    /// <summary>
    /// Whether a code descriptor is 64-bit code (L=1, D=0). L=1 with D=1 is
    /// reserved and is #GP(selector) for every far transfer (SDM Vol.2 RET,
    /// IA-32e mode), checked in <see cref="CheckReturnCodeSegment"/> and the
    /// far JMP/CALL.
    /// </summary>
    private static bool IsSixtyFourBitCodeDescriptor(ulong descriptor) =>
        (descriptor & DescriptorLongBit) != 0 && (descriptor & DescriptorDefaultBigBit) == 0;

    private static bool IsReservedLongAndBigDescriptor(ulong descriptor) =>
        (descriptor & DescriptorLongBit) != 0 && (descriptor & DescriptorDefaultBigBit) != 0;

    /// <summary>
    /// The CS a return (IRET, RETF) may go to: not null, a present code
    /// segment, RPL no lower than CPL (a return never raises privilege), and
    /// for a non-conforming segment DPL equal to RPL (conforming: DPL no
    /// greater than RPL). The new CPL is the selector's RPL.
    /// </summary>
    private (ulong Descriptor, byte Privilege) CheckReturnCodeSegment(ushort selector)
    {
        if ((selector & 0xFFFC) == 0)
        {
            throw new CpuException(13, 0);
        }

        ulong descriptor = ReadDescriptorLongMode(selector, offsetInDescriptor: 0);
        byte access = (byte)(descriptor >> 40);
        byte requestedPrivilege = (byte)(selector & 3);
        byte dpl = (byte)((access >> 5) & 3);
        bool isCode = (access & 0x18) == 0x18;
        bool conforming = (access & 0x04) != 0;

        if (!isCode || requestedPrivilege < State.Cpl || IsReservedLongAndBigDescriptor(descriptor))
        {
            throw new CpuException(13, selector & 0xFFFCu);
        }

        bool privilegeMatches = conforming ? dpl <= requestedPrivilege : dpl == requestedPrivilege;
        if (!privilegeMatches)
        {
            throw new CpuException(13, selector & 0xFFFCu);
        }

        if ((access & 0x80) == 0)
        {
            throw new CpuException(11, selector & 0xFFFCu);
        }

        return (descriptor, requestedPrivilege);
    }

    /// <summary>
    /// The SS a return may load. A null SS is allowed when returning to
    /// 64-bit code below ring 3; otherwise it must be a present, writable data
    /// segment with RPL and DPL equal to the new CPL. Returns null for a
    /// null selector.
    /// </summary>
    private ulong? CheckReturnStackSegment(ushort selector, byte newPrivilege, bool toLongCode)
    {
        if ((selector & 0xFFFC) == 0)
        {
            // A null SS may go back to 64-bit code below ring 3, and only with
            // an RPL equal to the new CPL (SDM Vol.2 IRET, 64-bit exceptions).
            if (toLongCode && newPrivilege != 3 && (selector & 3) == newPrivilege)
            {
                return null;
            }

            throw new CpuException(13, 0);
        }

        ulong descriptor = ReadDescriptorLongMode(selector, offsetInDescriptor: 0);
        byte access = (byte)(descriptor >> 40);
        bool writableData = (access & 0x1A) == 0x12;
        byte dpl = (byte)((access >> 5) & 3);

        if (!writableData || (selector & 3) != newPrivilege || dpl != newPrivilege)
        {
            throw new CpuException(13, selector & 0xFFFCu);
        }

        if ((access & 0x80) == 0)
        {
            throw new CpuException(12, selector & 0xFFFCu);
        }

        return descriptor;
    }

    private void LoadReturnStackSegment(ushort selector, ulong? descriptor, byte newPrivilege)
    {
        if (descriptor is not { } stackDescriptor)
        {
            LoadNullStackSegment(newPrivilege);
            State.Ss.Selector = selector;
            return;
        }

        ref SegmentCache ss = ref State.Segments[(int)SegReg.Ss];
        byte access = (byte)(stackDescriptor >> 40);
        ss.Selector = selector;
        ss.Base = DescriptorBase(stackDescriptor);
        ss.BaseHigh = 0;
        ss.Limit = DescriptorLimitOf(stackDescriptor);
        ss.Big = (stackDescriptor & DescriptorDefaultBigBit) != 0;
        ss.Long = false;
        ss.Dpl = (byte)((access >> 5) & 3);
        ss.Readable = true;
        ss.Writable = true;
        ss.ExpandDown = (access & 0x04) != 0;
    }

    /// <summary>
    /// RFLAGS after IRET, judged by the privilege IRET ran at (SDM Vol.2
    /// IRET): the arithmetic and control flags always; IOPL, VIF and VIP only
    /// at CPL 0; IF only when CPL is at most IOPL. VM stays clear -- IA-32e
    /// mode has no virtual 8086 mode -- and bit 1 reads 1.
    /// </summary>
    private uint FlagsAfterInterruptReturn(uint popped, int operandBytes)
    {
        uint writable = (uint)(FlagBits.Carry | FlagBits.Parity | FlagBits.Adjust | FlagBits.Zero | FlagBits.Sign
                        | FlagBits.Trap | FlagBits.Direction | FlagBits.Overflow | FlagBits.NestedTask);

        // A 16-bit IRET pops only FLAGS: RF, AC, ID, VIF and VIP, which live in
        // the upper half, keep their values (SDM Vol.2 IRET).
        bool wide = operandBytes > 2;
        if (wide)
        {
            writable |= (uint)FlagBits.Resume | AlignmentCheckFlag | IdentificationFlag;
        }

        uint currentIopl = (Flags.Value & (uint)FlagBits.Iopl) >> 12;
        if (State.Cpl == 0)
        {
            writable |= (uint)FlagBits.Iopl;
            if (wide)
            {
                writable |= VirtualInterruptFlag | VirtualInterruptPendingFlag;
            }
        }

        if (State.Cpl <= currentIopl)
        {
            writable |= (uint)FlagBits.Interrupt;
        }

        uint result = (Flags.Value & ~writable) | (popped & writable);
        result |= (uint)FlagBits.Reserved1;
        result &= ~(uint)FlagBits.Virtual8086;
        return result;
    }

    /// <summary>
    /// On a return to a less privileged level, DS, ES, FS and GS that hold a
    /// data or non-conforming code segment more privileged than the new CPL
    /// are made null, so the outer level cannot keep using them (SDM Vol.2
    /// IRET, RET). FS and GS keep their bases, which in 64-bit mode come from
    /// the MSRs rather than the descriptor.
    /// </summary>
    private void NullDataSegmentsAboveReturnPrivilege(byte newPrivilege)
    {
        foreach (SegReg segment in new[] { SegReg.Ds, SegReg.Es, SegReg.Fs, SegReg.Gs })
        {
            ref SegmentCache cache = ref State.Segments[(int)segment];
            if ((cache.Selector & 0xFFFC) != 0 && cache.Dpl < newPrivilege)
            {
                cache.Selector = 0;
                cache.Readable = false;
                cache.Writable = false;
            }
        }
    }

    /// <summary>A stack read at the given width through SS, as a POP does.</summary>
    private ulong ReadStackValue(ulong linear, int operandBytes) => ReadMemory64(SegReg.Ss, linear, operandBytes);

    // === far transfers ==========================================================

    /// <summary>
    /// RETF / RETF imm16 in 64-bit mode: pops RIP and CS at the operand size
    /// (4 by default, 8 with REX.W as Linux's LRETQ uses, 2 with 66h); to an
    /// outer privilege it also pops RSP and SS. Checked in full before
    /// anything is committed.
    /// </summary>
    private void FarReturn64(ushort releaseBytes, int operandBytes)
    {
        ulong stackPointer = State.Gpr[StackPointerRegister];
        ulong newRip = ReadStackValue(stackPointer, operandBytes);
        ushort newCsSelector = (ushort)ReadStackValue(stackPointer + (ulong)operandBytes, operandBytes);

        (ulong codeDescriptor, byte newPrivilege) = CheckReturnCodeSegment(newCsSelector);
        bool toLongCode = IsSixtyFourBitCodeDescriptor(codeDescriptor);
        if (toLongCode ? !IsCanonical(newRip) : newRip > DescriptorLimitOf(codeDescriptor))
        {
            throw new CpuException(13, 0);
        }

        ulong afterReturnAddress = stackPointer + 2UL * (ulong)operandBytes + releaseBytes;
        byte oldPrivilege = State.Cpl;

        if (newPrivilege == oldPrivilege)
        {
            LoadCodeSegmentLongMode(newCsSelector, codeDescriptor, newPrivilege);
            State.Gpr[StackPointerRegister] = afterReturnAddress;
            State.Rip = newRip;
            return;
        }

        ulong newStackPointer = ReadStackValue(afterReturnAddress, operandBytes);
        ushort newSsSelector = (ushort)ReadStackValue(afterReturnAddress + (ulong)operandBytes, operandBytes);
        ulong? stackDescriptor = CheckReturnStackSegment(newSsSelector, newPrivilege, toLongCode);

        LoadCodeSegmentLongMode(newCsSelector, codeDescriptor, newPrivilege);
        LoadReturnStackSegment(newSsSelector, stackDescriptor, newPrivilege);
        State.Gpr[StackPointerRegister] = newStackPointer + releaseBytes;
        State.Rip = newRip;
        NullDataSegmentsAboveReturnPrivilege(newPrivilege);
    }

    /// <summary>The operand size of the far transfers in 64-bit mode: 4 by default, 8 with REX.W, 2 with 66h (SDM Vol.2 RET, JMP, CALL).</summary>
    private int FarOperandBytes64 => OperandBytes64;

    /// <summary>
    /// JMP far / CALL far through memory (FF /5, FF /3): the operand is an
    /// offset (4, or 8 with REX.W, or 2 with 66h) followed by a selector.
    /// The target must be a code segment reachable without a privilege
    /// change -- non-conforming with DPL = CPL and RPL no greater than CPL,
    /// or conforming with DPL no greater than CPL. A call gate is stage D3's
    /// and is #GP(selector) until then. CALL pushes CS then RIP at the
    /// operand size, after every check has passed.
    /// </summary>
    private void FarTransferThroughMemory64(LongModeOperand operand, bool isCall)
    {
        if (operand.IsRegister)
        {
            throw new CpuException(6);
        }

        int operandBytes = FarOperandBytes64;
        ulong offset = EffectiveOffset(operand);
        ulong newRip = ReadMemory64(operand.Segment, offset, operandBytes);
        ushort selector = (ushort)ReadMemory64(operand.Segment, offset + (ulong)operandBytes, 2);

        if ((selector & 0xFFFC) == 0)
        {
            throw new CpuException(13, 0);
        }

        ulong descriptor = ReadDescriptorLongMode(selector, offsetInDescriptor: 0);
        byte access = (byte)(descriptor >> 40);
        bool isCode = (access & 0x18) == 0x18;
        bool conforming = (access & 0x04) != 0;
        byte dpl = (byte)((access >> 5) & 3);
        byte requestedPrivilege = (byte)(selector & 3);

        bool reachable = isCode
                         && !IsReservedLongAndBigDescriptor(descriptor)
                         && (conforming ? dpl <= State.Cpl : dpl == State.Cpl && requestedPrivilege <= State.Cpl);
        if (!reachable)
        {
            throw new CpuException(13, selector & 0xFFFCu);
        }

        if ((access & 0x80) == 0)
        {
            throw new CpuException(11, selector & 0xFFFCu);
        }

        bool toLongCode = IsSixtyFourBitCodeDescriptor(descriptor);
        if (toLongCode ? !IsCanonical(newRip) : newRip > DescriptorLimitOf(descriptor))
        {
            throw new CpuException(13, 0);
        }

        if (isCall)
        {
            ulong stackPointer = State.Gpr[StackPointerRegister];
            ulong csSlot = stackPointer - (ulong)operandBytes;
            ulong ripSlot = csSlot - (ulong)operandBytes;
            WriteMemory64(SegReg.Ss, csSlot, operandBytes, State.Cs.Selector);
            WriteMemory64(SegReg.Ss, ripSlot, operandBytes, State.Rip);
            State.Gpr[StackPointerRegister] = ripSlot;
        }

        LoadCodeSegmentLongMode(selector, descriptor, State.Cpl);
        State.Rip = newRip;
    }
}
