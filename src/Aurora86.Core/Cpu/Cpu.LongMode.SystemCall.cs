// SYSCALL and SYSRET, and port I/O, in IA-32e mode (plans/long-mode.md,
// stage D3). SYSCALL is how every 64-bit operating system's user code
// enters the kernel -- Linux, Windows, the BSDs -- and SYSRET how it goes
// back; neither touches a descriptor table: the code and stack segments are
// fixed flat segments whose selectors STAR supplies. Port I/O keeps the
// legacy rules (IOPL, then the TSS bitmap), with the bitmap reached through
// the 64-bit TSS. Sources: SDM Vol.2 SYSCALL, SYSRET, IN, OUT, INS, OUTS;
// AMD APM Vol.2 §6.1.

using FlagBits = Aurora86.Core.Cpu.Flags;

namespace Aurora86.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>The RFLAGS bits SYSRET may restore from R11: 3C7FD7h, the architectural mask (SDM Vol.2 SYSRET).</summary>
    private const ulong SysretFlagsMask = 0x3C7FD7;

    /// <summary>
    /// SYSCALL (0F 05): #UD unless EFER.SCE. RCX takes the next RIP, R11 the
    /// flags (RF cleared); CS becomes STAR[47:32] -- a flat 64-bit ring-0
    /// code segment -- and SS STAR[47:32] + 8, loaded without reading the
    /// GDT; RFLAGS loses the SFMASK bits; RIP becomes LSTAR, or CSTAR when
    /// SYSCALL came from compatibility mode.
    /// </summary>
    private void ExecuteSyscall64()
    {
        if ((State.Efer & CpuState.EferSyscallEnable) == 0)
        {
            throw new CpuException(6);
        }

        bool fromSixtyFourBitCode = State.SixtyFourBitMode;
        ulong target = fromSixtyFourBitCode ? Msrs.Lstar : Msrs.Cstar;
        ushort kernelSelector = (ushort)((Msrs.Star >> 32) & 0xFFFC);

        uint flags = Flags.Value;
        State.Gpr[(int)Reg.Ecx] = State.Rip;
        State.Gpr[11] = flags & ~(uint)FlagBits.Resume;

        LoadFlatCodeSegment(kernelSelector, privilege: 0, sixtyFourBit: true);
        LoadFlatStackSegment((ushort)(kernelSelector + 8), privilege: 0);

        uint maskedFlags = flags & ~(uint)Msrs.Sfmask & ~(uint)FlagBits.Resume;
        Flags.SetValue(maskedFlags | (uint)FlagBits.Reserved1);
        State.Rip = target;
    }

    /// <summary>
    /// SYSRET (0F 07): #UD unless EFER.SCE, #GP(0) below ring 0. With REX.W
    /// it returns to 64-bit code at RCX, which must be canonical (Intel
    /// raises #GP(0) in ring 0 otherwise); without it to compatibility code
    /// at ECX. CS is STAR[63:48] + 16 (or + 0 for compatibility) and SS
    /// STAR[63:48] + 8, both with RPL 3; RFLAGS comes from R11.
    /// </summary>
    private void ExecuteSysret64()
    {
        if ((State.Efer & CpuState.EferSyscallEnable) == 0)
        {
            throw new CpuException(6);
        }

        if (State.Cpl != 0)
        {
            throw new CpuException(13, 0);
        }

        bool toSixtyFourBitCode = _longPrefixes.RexW;
        ulong returnAddress = State.Gpr[(int)Reg.Ecx];
        if (toSixtyFourBitCode && !IsCanonical(returnAddress))
        {
            throw new CpuException(13, 0);
        }

        ushort userBase = (ushort)((Msrs.Star >> 48) & 0xFFFC);
        const ushort SixtyFourBitCodeOffset = 16;
        const ushort StackOffset = 8;
        const byte UserPrivilege = 3;

        ushort codeSelector = toSixtyFourBitCode ? (ushort)(userBase + SixtyFourBitCodeOffset) : userBase;
        LoadFlatCodeSegment((ushort)(codeSelector | UserPrivilege), UserPrivilege, toSixtyFourBitCode);
        LoadFlatStackSegment((ushort)((userBase + StackOffset) | UserPrivilege), UserPrivilege);

        ulong restoredFlags = (State.Gpr[11] & SysretFlagsMask) | (uint)FlagBits.Reserved1;
        Flags.SetValue((uint)restoredFlags);
        State.Rip = toSixtyFourBitCode ? returnAddress : (uint)returnAddress;
    }

    /// <summary>The fixed code segment SYSCALL and SYSRET load (SDM Vol.2): base 0, limit 4 GB, readable, 64-bit or 32-bit.</summary>
    private void LoadFlatCodeSegment(ushort selector, byte privilege, bool sixtyFourBit)
    {
        ref SegmentCache cs = ref State.Segments[(int)SegReg.Cs];
        cs.Selector = selector;
        cs.Base = 0;
        cs.BaseHigh = 0;
        cs.Limit = 0xFFFFFFFF;
        cs.Long = sixtyFourBit;
        cs.Big = !sixtyFourBit;
        cs.Dpl = privilege;
        cs.Readable = true;
        cs.Writable = false;
        cs.ExpandDown = false;

        State.Cpl = privilege;
        InvalidateFetchPage();
    }

    /// <summary>The fixed stack segment SYSCALL and SYSRET load: base 0, limit 4 GB, writable, 32-bit.</summary>
    private void LoadFlatStackSegment(ushort selector, byte privilege)
    {
        ref SegmentCache ss = ref State.Segments[(int)SegReg.Ss];
        ss.Selector = selector;
        ss.Base = 0;
        ss.BaseHigh = 0;
        ss.Limit = 0xFFFFFFFF;
        ss.Long = false;
        ss.Big = true;
        ss.Dpl = privilege;
        ss.Readable = true;
        ss.Writable = true;
        ss.ExpandDown = false;
    }

    // === port I/O ================================================================

    /// <summary>
    /// The I/O permission bitmap check in IA-32e mode: the map's offset at
    /// 66h of the 64-bit TSS, one bit per port, every byte of the access
    /// inside the TSS limit (SDM Vol.1 §19.5.2).
    /// </summary>
    private bool IoPermittedByLongModeBitmap(ushort port, int bytes)
    {
        const uint MapOffsetField = 0x66;
        if (State.TssLimit < MapOffsetField + 1)
        {
            return false;
        }

        ulong tssBase = State.TssBase64;
        ushort mapBase = (ushort)(ReadSupervisorByte64(tssBase + MapOffsetField)
                                  | (ReadSupervisorByte64(tssBase + MapOffsetField + 1) << 8));

        uint firstByte = mapBase + (uint)(port >> 3);
        uint lastByte = mapBase + (uint)((port + bytes - 1) >> 3);
        if (lastByte > State.TssLimit)
        {
            return false;
        }

        int bits = ReadSupervisorByte64(tssBase + firstByte);
        if (lastByte != firstByte)
        {
            bits |= ReadSupervisorByte64(tssBase + lastByte) << 8;
        }

        int mask = ((1 << bytes) - 1) << (port & 7);
        return (bits & mask) == 0;
    }

    private byte ReadSupervisorByte64(ulong linear) =>
        _memory.ReadByte(TranslateLongMode(linear, write: false, asSupervisor: true, execute: false));

    /// <summary>The width of IN/OUT with eAX: 2 with 66h, otherwise 4 -- REX.W does not make it 8 (SDM Vol.2 IN).</summary>
    private int PortOperandBytes64 => _longPrefixes.OperandSize && !_longPrefixes.RexW ? 2 : 4;

    /// <summary>E4h-E7h and ECh-EFh: IN and OUT with an immediate port or DX.</summary>
    private void ExecutePortInstruction64(byte opcode)
    {
        const byte ImmediatePortLast = 0xE7;
        const byte OutputBit = 0x02;
        const byte WideBit = 0x01;

        ushort port = opcode <= ImmediatePortLast ? FetchByte64() : State.Dx;
        bool output = (opcode & OutputBit) != 0;
        int bytes = (opcode & WideBit) != 0 ? PortOperandBytes64 : 1;

        // The VMware backdoor answers a 32-bit IN EAX, DX from any ring,
        // before the permission check (IBackdoorPort), as in legacy mode.
        const byte InAccumulatorFromDx = 0xED;
        if (opcode == InAccumulatorFromDx && bytes == 4 && Backdoor is { } backdoor && backdoor.TryHandleInput(State, port))
        {
            return;
        }

        if (!IoPermitted(port, bytes))
        {
            throw new CpuException(13, 0);
        }

        if (output)
        {
            WritePort64(port, bytes, State.Gpr[(int)Reg.Eax]);
            return;
        }

        WriteRegister64((int)Reg.Eax, bytes, ReadPort64(port, bytes));
    }

    private ulong ReadPort64(ushort port, int bytes) => bytes switch
    {
        1 => _io.ReadByte(port),
        2 => _io.ReadWord(port),
        _ => _io.ReadDword(port),
    };

    private void WritePort64(ushort port, int bytes, ulong value)
    {
        switch (bytes)
        {
            case 1:
                _io.WriteByte(port, (byte)value);
                break;
            case 2:
                _io.WriteWord(port, (ushort)value);
                break;
            default:
                _io.WriteDword(port, (uint)value);
                break;
        }
    }

    /// <summary>
    /// The most elements one step of REP INS/OUTS moves before letting
    /// interrupts in; the instruction repeats from its first byte. Unlike
    /// the memory string instructions (one element per step, as the legacy
    /// path does), a port run moves up to this many at once: devices do not
    /// advance between elements, so a REP INSB from a free-running port
    /// reads one sample repeated -- deterministic, and what a sector
    /// transfer from a disk controller's data port needs to be fast.
    /// </summary>
    private const int MaximumPortStringElementsPerStep = 1024;

    /// <summary>
    /// 6Ch-6Fh: INS and OUTS, with REP. INS stores to ES:RDI (base 0, no
    /// override); OUTS reads DS:RSI, which FS or GS may override. RSI/RDI,
    /// or ESI/EDI under 67h, step by the size according to DF; RCX (ECX)
    /// counts. Each element is checked, moved, then committed, so a fault
    /// leaves the registers at the faulting element; a long run gives way
    /// to interrupts after <see cref="MaximumPortStringElementsPerStep"/>.
    /// </summary>
    private void ExecutePortString64(byte opcode)
    {
        const byte OutsFirst = 0x6E;
        const byte WideBit = 0x01;

        bool output = opcode >= OutsFirst;
        int bytes = (opcode & WideBit) != 0 ? PortOperandBytes64 : 1;
        bool repeated = _longPrefixes.Rep || _longPrefixes.RepNe;
        ushort port = State.Dx;
        int indexRegister = output ? (int)Reg.Esi : (int)Reg.Edi;
        long step = Flags.Direction ? -bytes : bytes;

        for (int element = 0; ; element++)
        {
            if (repeated && CounterValue64() == 0)
            {
                return;
            }

            if (!IoPermitted(port, bytes))
            {
                throw new CpuException(13, 0);
            }

            ulong index = IndexValue64(indexRegister);
            if (output)
            {
                SegReg source = _longPrefixes.Segment ?? SegReg.Ds;
                ulong value = ReadMemory64(source, index, bytes);
                WritePort64(port, bytes, value);
            }
            else
            {
                ulong value = ReadPort64(port, bytes);
                WriteMemory64(SegReg.Es, index, bytes, value);
            }

            CommitIndex64(indexRegister, index + (ulong)step);
            if (!repeated)
            {
                return;
            }

            ulong remaining = CounterValue64() - 1;
            CommitCounterValue64(remaining);
            if (remaining != 0 && element + 1 >= MaximumPortStringElementsPerStep)
            {
                State.Rip = _longInstructionStart;
                return;
            }
        }
    }

    private ulong CounterValue64() => AddressSize32In64 ? (uint)State.Gpr[(int)Reg.Ecx] : State.Gpr[(int)Reg.Ecx];

    private void CommitCounterValue64(ulong value)
    {
        if (AddressSize32In64)
        {
            WriteRegister64((int)Reg.Ecx, 4, value);
            return;
        }

        State.Gpr[(int)Reg.Ecx] = value;
    }

    private ulong IndexValue64(int register) => AddressSize32In64 ? (uint)State.Gpr[register] : State.Gpr[register];

    private void CommitIndex64(int register, ulong value)
    {
        if (AddressSize32In64)
        {
            WriteRegister64(register, 4, value);
            return;
        }

        State.Gpr[register] = value;
    }
}
