// 64-bit mode's system instructions (plans/long-mode.md, stage D1): the
// descriptor-table registers with 10-byte operands, LLDT and LTR with the
// 16-byte system descriptors of IA-32e mode, segment-register moves, PUSH and
// POP of FS and GS, SWAPGS and INVLPG at a 64-bit address. A 64-bit kernel
// runs these while it sets itself up -- Linux's startup_64 and cpu_init
// load a GDT, an IDT and a TSS this way, then switch GS with SWAPGS on every
// kernel entry. Sources: SDM Vol.2 (LGDT/LIDT, SGDT/SIDT, LLDT, LTR, MOV,
// PUSH/POP, SWAPGS, INVLPG, SMSW/LMSW) and Vol.3A §3.5.1, §7.2.3.

namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>
    /// The one-byte opcodes of this file: MOV r/m, Sreg (8Ch), MOV Sreg, r/m
    /// (8Eh), RETF (CAh, CBh), INT3 (CCh), INT n (CDh), IRET (CFh) and the far
    /// forms of group 5 (FF /3, /5). INTO (CEh) does not exist in 64-bit mode
    /// and stays #UD. False for anything else.
    /// </summary>
    private bool TryExecuteSystem64(byte opcode)
    {
        switch (opcode)
        {
            case 0x8C:
                MoveFromSegmentRegister64();
                return true;

            case 0x8E:
                MoveToSegmentRegister64();
                return true;

            case 0xCA:
            {
                ushort releaseBytes = FetchWord64();
                FarReturn64(releaseBytes, FarOperandBytes64);
                return true;
            }

            case 0xCB:
                FarReturn64(0, FarOperandBytes64);
                return true;

            case 0xCC:
                SoftwareInterrupt(3);
                return true;

            case 0xCD:
                SoftwareInterrupt(FetchByte64());
                return true;

            case 0xCF:
                InterruptReturnLongMode(OperandBytes64);
                return true;

            case 0xFF:
                return TryExecuteFarGroup5();

            case >= 0x6C and <= 0x6F:
                ExecutePortString64(opcode);
                return true;

            case >= 0xE4 and <= 0xE7:
            case >= 0xEC and <= 0xEF:
                ExecutePortInstruction64(opcode);
                return true;

            default:
                return false;
        }
    }

    /// <summary>The 0Fh opcodes of this file. False for anything else.</summary>
    private bool TryExecuteSystemTwoByte64(byte opcode)
    {
        switch (opcode)
        {
            case 0x00:
                ExecuteGroup6Long();
                return true;

            case 0x01:
                ExecuteGroup7Long();
                return true;

            case 0x05:
                ExecuteSyscall64();
                return true;

            case 0x07:
                ExecuteSysret64();
                return true;

            case 0xA0:
                PushSegmentRegister64(SegReg.Fs);
                return true;

            case 0xA1:
                PopSegmentRegister64(SegReg.Fs);
                return true;

            case 0xA8:
                PushSegmentRegister64(SegReg.Gs);
                return true;

            case 0xA9:
                PopSegmentRegister64(SegReg.Gs);
                return true;

            default:
                return false;
        }
    }

    /// <summary>FF /3 (CALL far m16:32/64) and FF /5 (JMP far): the group-5 forms the ALU and control files leave alone.</summary>
    private bool TryExecuteFarGroup5()
    {
        const int CallFar = 3;
        const int JumpFar = 5;
        int reg = (PeekModRmByte64() >> 3) & 7;
        if (reg is not (CallFar or JumpFar))
        {
            return false;
        }

        LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
        FarTransferThroughMemory64(operand, isCall: reg == CallFar);
        return true;
    }

    // === 0F 01: group 7 ===========================================================

    /// <summary>ModR/M reg values of 0F 01 (group 7).</summary>
    private const int Group7Sgdt = 0;
    private const int Group7Sidt = 1;
    private const int Group7Lgdt = 2;
    private const int Group7Lidt = 3;
    private const int Group7Smsw = 4;
    private const int Group7Lmsw = 6;
    private const int Group7InvlpgOrSwapgs = 7;

    /// <summary>
    /// 0F 01. With a memory operand: SGDT, SIDT, LGDT, LIDT (10-byte
    /// operands: a 16-bit limit, then a 64-bit base), SMSW, LMSW, INVLPG.
    /// With a register operand only SMSW, LMSW and, as 0F 01 F8, SWAPGS are
    /// carried; every other register form (MONITOR, XGETBV, RDTSCP and the
    /// rest) is #UD until the feature behind it is declared.
    /// </summary>
    private void ExecuteGroup7Long()
    {
        LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
        int function = operand.Reg & 7;

        if (operand.IsRegister)
        {
            ExecuteGroup7RegisterForm(function, operand);
            return;
        }

        switch (function)
        {
            case Group7Sgdt:
                StoreTableRegister64(operand, State.GdtLimit, State.GdtBase64);
                return;

            case Group7Sidt:
                StoreTableRegister64(operand, State.IdtLimit, State.IdtBase64);
                return;

            case Group7Lgdt:
            {
                RequireSupervisor();
                (ushort limit, ulong tableBase) = LoadTableRegisterOperand64(operand);
                State.GdtLimit = limit;
                State.GdtBase = (uint)tableBase;
                State.GdtBaseHigh = (uint)(tableBase >> 32);
                return;
            }

            case Group7Lidt:
            {
                RequireSupervisor();
                (ushort limit, ulong tableBase) = LoadTableRegisterOperand64(operand);
                State.IdtLimit = limit;
                State.IdtBase = (uint)tableBase;
                State.IdtBaseHigh = (uint)(tableBase >> 32);
                return;
            }

            case Group7Smsw:
                // A memory destination is always a word (SDM Vol.2 SMSW).
                WriteOperand64(operand, 2, (ushort)State.Cr0);
                return;

            case Group7Lmsw:
                RequireSupervisor();
                LoadMachineStatusWord64((ushort)ReadOperand64(operand, 2));
                return;

            case Group7InvlpgOrSwapgs:
                InvalidatePageLongMode(operand);
                return;

            default:
                throw new CpuException(6);
        }
    }

    /// <summary>The register forms of 0F 01 carried here: SMSW r, LMSW r and SWAPGS (0F 01 F8).</summary>
    private void ExecuteGroup7RegisterForm(int function, LongModeOperand operand)
    {
        const int SwapgsRm = 0;

        switch (function)
        {
            case Group7Smsw:
            {
                // A register destination takes the operand size, zero-extended
                // from CR0 -- with REX.W all 64 bits of CR0 (its upper half is 0).
                int operandBytes = OperandBytes64;
                WriteRegister64(operand.Register, operandBytes, State.Cr0);
                return;
            }

            case Group7Lmsw:
                RequireSupervisor();
                LoadMachineStatusWord64((ushort)ReadRegister64(operand.Register, 2));
                return;

            case Group7InvlpgOrSwapgs when (operand.Register & 7) == SwapgsRm:
                ExecuteSwapgs();
                return;

            default:
                throw new CpuException(6);
        }
    }

    /// <summary>SGDT/SIDT: the limit, then all eight bytes of the base (SDM Vol.2: in 64-bit mode the operand is always 10 bytes).</summary>
    private void StoreTableRegister64(LongModeOperand operand, ushort limit, ulong tableBase)
    {
        ulong offset = EffectiveOffset(operand);
        WriteMemory64(operand.Segment, offset, 2, limit);
        WriteMemory64(operand.Segment, offset + 2, 8, tableBase);
    }

    /// <summary>
    /// LGDT/LIDT's 10-byte operand. A non-canonical base is #GP(0), checked
    /// before anything is loaded (SDM Vol.2 LGDT).
    /// </summary>
    private (ushort Limit, ulong Base) LoadTableRegisterOperand64(LongModeOperand operand)
    {
        ulong offset = EffectiveOffset(operand);
        ushort limit = (ushort)ReadMemory64(operand.Segment, offset, 2);
        ulong tableBase = ReadMemory64(operand.Segment, offset + 2, 8);

        if (!IsCanonical(tableBase))
        {
            throw new CpuException(13, 0);
        }

        return (limit, tableBase);
    }

    /// <summary>
    /// LMSW: loads CR0's low four bits (PE, MP, EM, TS). It can set PE but
    /// never clear it (SDM Vol.2 LMSW), so leaving protected mode this way is
    /// impossible -- which in long mode also keeps PG's precondition intact.
    /// </summary>
    private void LoadMachineStatusWord64(ushort value)
    {
        const uint MachineStatusBits = 0xF;
        uint protectionEnable = State.Cr0 & CpuState.Cr0ProtectionEnable;
        uint newCr0 = (State.Cr0 & ~MachineStatusBits) | (value & MachineStatusBits) | protectionEnable;
        WriteCr0(newCr0);
    }

    /// <summary>
    /// INVLPG m at CPL 0. The operand's address is a linear address, taken
    /// without a memory access. A non-canonical address makes INVLPG a no-op
    /// in 64-bit mode rather than a fault (SDM Vol.2 INVLPG).
    /// </summary>
    private void InvalidatePageLongMode(LongModeOperand operand)
    {
        RequireSupervisor();

        ulong segmentBase = operand.Segment is SegReg.Fs or SegReg.Gs ? State.Segments[(int)operand.Segment].Base64 : 0;
        ulong linear = segmentBase + EffectiveOffset(operand);
        if (!IsCanonical(linear))
        {
            return;
        }

        InvalidatePage64(linear);
    }

    /// <summary>
    /// SWAPGS (0F 01 F8): exchanges GS's base with IA32_KERNEL_GS_BASE, how a
    /// 64-bit kernel finds its per-CPU area on entry from user mode without
    /// spending a register. CPL 0 only, #GP(0) otherwise (SDM Vol.2 SWAPGS).
    /// </summary>
    private void ExecuteSwapgs()
    {
        if (State.Cpl != 0)
        {
            throw new CpuException(13, 0);
        }

        ref SegmentCache gs = ref State.Segments[(int)SegReg.Gs];
        ulong userBase = gs.Base64;
        ulong kernelBase = State.KernelGsBase;

        gs.Base = (uint)kernelBase;
        gs.BaseHigh = (uint)(kernelBase >> 32);
        State.KernelGsBase = userBase;
    }

    // === 0F 00: group 6 ===========================================================

    /// <summary>ModR/M reg values of 0F 00 (group 6).</summary>
    private const int Group6Sldt = 0;
    private const int Group6Str = 1;
    private const int Group6Lldt = 2;
    private const int Group6Ltr = 3;

    /// <summary>
    /// 0F 00: SLDT, STR, LLDT, LTR. VERR and VERW (/4, /5) are not carried
    /// yet and are #UD; no processor model here declares the MD_CLEAR that
    /// makes a kernel use VERW as a buffer flush.
    /// </summary>
    private void ExecuteGroup6Long()
    {
        LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
        int function = operand.Reg & 7;

        switch (function)
        {
            case Group6Sldt:
                StoreSelector64(operand, State.Ldtr);
                return;

            case Group6Str:
                StoreSelector64(operand, State.TaskRegister);
                return;

            case Group6Lldt:
                RequireSupervisor();
                LoadLocalDescriptorTableLongMode((ushort)ReadOperand64(operand, 2));
                return;

            case Group6Ltr:
                RequireSupervisor();
                LoadTaskRegisterLongMode((ushort)ReadOperand64(operand, 2));
                return;

            default:
                throw new CpuException(6);
        }
    }

    /// <summary>
    /// SLDT/STR and MOV r/m, Sreg: a memory destination is always a word; a
    /// register destination takes the operand size, zero-extended (SDM Vol.2).
    /// </summary>
    private void StoreSelector64(LongModeOperand operand, ushort selector)
    {
        if (!operand.IsRegister)
        {
            WriteOperand64(operand, 2, selector);
            return;
        }

        int operandBytes = OperandBytes64;
        WriteRegister64(operand.Register, operandBytes, selector);
    }

    /// <summary>
    /// The system descriptor type IA-32e mode gives an LDT (2) and an
    /// available or busy 64-bit TSS (9, B); the 16-bit TSS types do not exist
    /// there (SDM Vol.3A Table 3-2).
    /// </summary>
    private const int LdtDescriptorType = 0x2;
    private const int AvailableTss64Type = 0x9;
    private const byte TssBusyBit = 0x02;

    /// <summary>
    /// The 64-bit base of a 16-byte system descriptor: the legacy 32-bit base
    /// in the first qword, bits 63:32 in the second. The second qword's type
    /// field (bits 12:8) must be zero, or the descriptor is not a valid upper
    /// half and the load is #GP(selector) (SDM Vol.3A §7.2.3).
    /// </summary>
    private ulong SystemDescriptorBase64(ushort selector, ulong lowQword)
    {
        ulong highQword = ReadDescriptorLongMode(selector, offsetInDescriptor: 8);
        const ulong UpperHalfTypeMask = 0x1FUL << 8;
        if ((highQword & UpperHalfTypeMask) != 0)
        {
            throw new CpuException(13, selector);
        }

        return DescriptorBase(lowQword) | ((highQword & 0xFFFFFFFF) << 32);
    }

    /// <summary>LLDT in long mode: a null selector removes the LDT; otherwise a 16-byte LDT descriptor in the GDT.</summary>
    private void LoadLocalDescriptorTableLongMode(ushort selector)
    {
        if ((selector & 0xFFFC) == 0)
        {
            State.Ldtr = selector;
            State.LdtBase = 0;
            State.LdtBaseHigh = 0;
            State.LdtLimit = 0;
            return;
        }

        if ((selector & 0x04) != 0)
        {
            throw new CpuException(13, selector);
        }

        ulong descriptor = ReadDescriptorLongMode(selector, offsetInDescriptor: 0);
        byte access = (byte)(descriptor >> 40);
        if ((access & 0x1F) != LdtDescriptorType)
        {
            throw new CpuException(13, selector);
        }

        if ((access & 0x80) == 0)
        {
            throw new CpuException(11, selector);
        }

        ulong tableBase = SystemDescriptorBase64(selector, descriptor);
        if (!IsCanonical(tableBase))
        {
            throw new CpuException(13, selector);
        }

        State.Ldtr = selector;
        State.LdtBase = (uint)tableBase;
        State.LdtBaseHigh = (uint)(tableBase >> 32);
        State.LdtLimit = DescriptorLimitOf(descriptor);
    }

    /// <summary>
    /// LTR in long mode: only an available 64-bit TSS (type 9, a 16-byte
    /// descriptor) may be loaded; loading it marks it busy in the GDT.
    /// </summary>
    private void LoadTaskRegisterLongMode(ushort selector)
    {
        if ((selector & 0xFFFC) == 0)
        {
            throw new CpuException(13, 0);
        }

        if ((selector & 0x04) != 0)
        {
            throw new CpuException(13, selector);
        }

        ulong descriptor = ReadDescriptorLongMode(selector, offsetInDescriptor: 0);
        byte access = (byte)(descriptor >> 40);
        if ((access & 0x1F) != AvailableTss64Type)
        {
            throw new CpuException(13, selector);
        }

        if ((access & 0x80) == 0)
        {
            throw new CpuException(11, selector);
        }

        ulong tssBase = SystemDescriptorBase64(selector, descriptor);
        if (!IsCanonical(tssBase))
        {
            throw new CpuException(13, selector);
        }

        const uint AccessByteOffset = 5;
        ulong accessAddress = State.GdtBase64 + (uint)(selector & 0xFFF8) + AccessByteOffset;
        WriteSupervisorByte64(accessAddress, (byte)(access | TssBusyBit));

        State.TaskRegister = selector;
        State.TssBase = (uint)tssBase;
        State.TssBaseHigh = (uint)(tssBase >> 32);
        State.TssLimit = DescriptorLimitOf(descriptor);
    }

    // === segment registers ==========================================================

    /// <summary>8Ch: MOV r/m, Sreg. Reg values 6 and 7 name no segment register: #UD.</summary>
    private void MoveFromSegmentRegister64()
    {
        LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
        int segment = operand.Reg & 7;
        if (segment > (int)SegReg.Gs)
        {
            throw new CpuException(6);
        }

        StoreSelector64(operand, State.Segments[segment].Selector);
    }

    /// <summary>
    /// 8Eh: MOV Sreg, r/m16. CS cannot be loaded this way (#UD). In 64-bit
    /// mode SS may be loaded with a null selector below ring 3, provided its
    /// RPL equals CPL (SDM Vol.2 MOV, 64-bit mode exceptions); everything
    /// else goes through the ordinary protected-mode load, whose descriptor
    /// reads use the 64-bit table bases in long mode.
    /// </summary>
    private void MoveToSegmentRegister64()
    {
        LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
        int segmentIndex = operand.Reg & 7;
        if (segmentIndex == (int)SegReg.Cs || segmentIndex > (int)SegReg.Gs)
        {
            throw new CpuException(6);
        }

        ushort selector = (ushort)ReadOperand64(operand, 2);
        LoadSegment64((SegReg)segmentIndex, selector);
    }

    /// <summary>A segment-register load in 64-bit mode, with the null-SS rule of <see cref="MoveToSegmentRegister64"/>.</summary>
    private void LoadSegment64(SegReg segment, ushort selector)
    {
        bool nullSelector = (selector & 0xFFFC) == 0;
        if (segment == SegReg.Ss && nullSelector)
        {
            byte requestedPrivilege = (byte)(selector & 3);
            if (State.Cpl == 3 || requestedPrivilege != State.Cpl)
            {
                throw new CpuException(13, 0);
            }

            ref SegmentCache stack = ref State.Segments[(int)SegReg.Ss];
            stack.Selector = selector;
            stack.Base = 0;
            stack.BaseHigh = 0;
            _interruptShadow = true;
            return;
        }

        LoadSegment(segment, selector);
    }

    /// <summary>0F A0h/A8h: PUSH FS/GS -- the selector, zero-extended to the stack operand size (8, or 2 with 66h).</summary>
    private void PushSegmentRegister64(SegReg segment)
    {
        int operandBytes = StackOperandBytes64;
        PushValue64(State.Segments[(int)segment].Selector, operandBytes);
    }

    /// <summary>
    /// 0F A1h/A9h: POP FS/GS. The selector is read and loaded -- either can
    /// fault -- before RSP moves, so a fault leaves RSP as POP found it.
    /// </summary>
    private void PopSegmentRegister64(SegReg segment)
    {
        int operandBytes = StackOperandBytes64;
        ulong stackPointer = State.Gpr[StackPointerRegister];
        ushort selector = (ushort)ReadMemory64(SegReg.Ss, stackPointer, operandBytes);

        LoadSegment64(segment, selector);
        State.Gpr[StackPointerRegister] = stackPointer + (ulong)operandBytes;
    }
}
