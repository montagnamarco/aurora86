// 64-bit mode's own instruction decoder (plans/long-mode.md, stage C1): the
// prefixes with REX, the fetch from RIP, the 64-bit ModR/M and SIB forms
// with RIP-relative addressing, and memory accesses through 64-bit linear
// addresses checked for canonical form. AMD introduced all of it with the
// AMD64 architecture (K8, 2003); Intel adopted it as EM64T in 2004.
//
// It is a separate path from the 16/32-bit decoder on purpose, not a
// generalisation of it: the legacy path is what DOS and Windows 3.x run and
// what the 386 hardware vectors check, and none of it changes. Step() hands
// an instruction here only when the processor is in 64-bit mode proper
// (EFER.LMA with a CS whose L bit is set); compatibility mode stays on the
// legacy decoder. Sources: SDM Vol.2 §2.2 (REX, RIP-relative, 64-bit
// operand and address sizes), Vol.1 §3.3.7 (canonical addresses), Vol.3A
// §3.4.4 (segmentation in 64-bit mode); AMD APM Vol.3 §1.2-1.7.

using System.Runtime.CompilerServices;

namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>An instruction longer than 15 bytes is #GP(0) (SDM Vol.2 §2.3.11), whatever prefixes pad it.</summary>
    private const int MaximumInstructionLength = 15;

    /// <summary>
    /// The prefixes of one 64-bit-mode instruction. REX (40h-4Fh) counts only
    /// when it is the last prefix before the opcode: any legacy prefix after
    /// it makes the processor ignore it (SDM Vol.2 §2.2.1).
    /// </summary>
    private struct LongModePrefixes
    {
        /// <summary>An FS or GS override. ES, CS, SS and DS overrides are accepted and ignored in 64-bit mode (SDM Vol.3A §3.4.4).</summary>
        public SegReg? Segment;

        /// <summary>66h: 16-bit operands, unless REX.W asks for 64.</summary>
        public bool OperandSize;

        /// <summary>67h: 32-bit addresses instead of 64.</summary>
        public bool AddressSize;

        public bool Rep;
        public bool RepNe;
        public bool Lock;

        /// <summary>The REX byte's low four bits (W, R, X, B), when a REX prefix is in force.</summary>
        public byte Rex;

        /// <summary>Whether a REX prefix is in force, even 40h with no bit set: it still changes which byte registers 4-7 name.</summary>
        public bool RexPresent;

        public readonly bool RexW => (Rex & 0x8) != 0;
        public readonly bool RexR => (Rex & 0x4) != 0;
        public readonly bool RexX => (Rex & 0x2) != 0;
        public readonly bool RexB => (Rex & 0x1) != 0;
    }

    /// <summary>
    /// A decoded ModR/M operand in 64-bit mode. A RIP-relative address is
    /// relative to the end of the instruction, which lies past any immediate
    /// still to come, so <see cref="DecodeModRm64"/> is told how many
    /// immediate bytes follow and resolves the address at decode time, as
    /// QEMU does with its rip_offset. Resolving it later, at the access,
    /// would be wrong for every instruction that reads its memory operand
    /// before fetching an imm8 (SHUFPS, PSHUFD, CMPPS and more in the
    /// legacy helpers) -- independent review of stage C1, finding 2.
    /// </summary>
    private struct LongModeOperand
    {
        /// <summary>The reg field extended by REX.R: a register 0-15, or an opcode extension.</summary>
        public byte Reg;

        public bool IsRegister;

        /// <summary>The r/m register 0-15 (with REX.B) when <see cref="IsRegister"/>.</summary>
        public byte Register;

        public SegReg Segment;

        /// <summary>Base plus scaled index plus displacement, or only the displacement when <see cref="RipRelative"/>.</summary>
        public ulong Address;
    }

    private LongModePrefixes _longPrefixes;
    private ulong _longInstructionStart;

    // The 64-bit fetch page cache, the counterpart of FetchByte's.
    private ulong _fetchPage64 = ulong.MaxValue;
    private uint _fetchPhysical64;
    private byte _fetchCpl64 = 0xFF;

    /// <summary>
    /// Whether an address is canonical: bits 63:47 all equal, what a
    /// processor with 48 linear address bits requires of every memory
    /// reference in 64-bit mode (SDM Vol.1 §3.3.7.1).
    /// </summary>
    public static bool IsCanonical(ulong address)
    {
        const int SignBitsShift = 47;
        long signBits = (long)address >> SignBitsShift;
        return signBits is 0 or -1;
    }

    /// <summary>
    /// One instruction in 64-bit mode: prefixes, opcode, execution, and the
    /// same fault handling <see cref="Step"/> gives the legacy path -- a
    /// fault restarts the instruction from its first byte.
    /// </summary>
    private int Step64()
    {
        _longInstructionStart = State.Rip;
        _longPrefixes = default;

        try
        {
            byte opcode = FetchPrefixesAndOpcode64();
            Execute64(opcode);
        }
        catch (PageFaultException fault)
        {
            State.Rip = _longInstructionStart;
            State.Cr2 = (uint)fault.LinearAddress;   // CR2 is 32 bits wide until stage D
            DeliverException(14, fault.ErrorCode);
        }
        catch (CpuException exception)
        {
            State.Rip = _longInstructionStart;
            DeliverException(exception.Vector, exception.ErrorCode);
        }

        Cycles++;
        return 1;
    }

    /// <summary>
    /// Consumes the prefixes and returns the opcode. A REX byte is kept only
    /// if the opcode follows it directly; a legacy prefix after a REX cancels
    /// it, exactly as if it had not been there.
    /// </summary>
    private byte FetchPrefixesAndOpcode64()
    {
        while (true)
        {
            byte value = FetchByte64();
            bool isRex = (value & 0xF0) == 0x40;

            if (isRex)
            {
                _longPrefixes.Rex = (byte)(value & 0x0F);
                _longPrefixes.RexPresent = true;
                continue;
            }

            bool isLegacyPrefix = true;
            switch (value)
            {
                case 0x26:
                case 0x2E:
                case 0x36:
                case 0x3E:
                    // ES, CS, SS, DS: no effect in 64-bit mode.
                    break;
                case 0x64:
                    _longPrefixes.Segment = SegReg.Fs;
                    break;
                case 0x65:
                    _longPrefixes.Segment = SegReg.Gs;
                    break;
                case 0x66:
                    _longPrefixes.OperandSize = true;
                    break;
                case 0x67:
                    _longPrefixes.AddressSize = true;
                    break;
                case 0xF0:
                    _longPrefixes.Lock = true;
                    break;
                case 0xF2:
                    _longPrefixes.RepNe = true;
                    break;
                case 0xF3:
                    _longPrefixes.Rep = true;
                    break;
                default:
                    isLegacyPrefix = false;
                    break;
            }

            if (!isLegacyPrefix)
            {
                return value;
            }

            // A legacy prefix after REX: the REX no longer precedes the opcode.
            _longPrefixes.Rex = 0;
            _longPrefixes.RexPresent = false;
        }
    }

    // --- operand and address sizes ---------------------------------------------

    /// <summary>
    /// The operand size in bytes: 8 with REX.W (which wins over 66h), 2 with
    /// 66h, otherwise the 64-bit mode default of 4 (SDM Vol.2 Table 2-4).
    /// </summary>
    private int OperandBytes64
    {
        get
        {
            if (_longPrefixes.RexW)
            {
                return 8;
            }

            return _longPrefixes.OperandSize ? 2 : 4;
        }
    }

    /// <summary>
    /// The operand size of the instructions that default to 64 bits in 64-bit
    /// mode -- near branches, PUSH, POP (SDM Vol.2 Table 2-5): 8, or 2 with
    /// 66h. There is no 32-bit form.
    /// </summary>
    private int StackOperandBytes64 => _longPrefixes.OperandSize && !_longPrefixes.RexW ? 2 : 8;

    /// <summary>Whether 67h made the addresses 32 bits wide.</summary>
    private bool AddressSize32In64 => _longPrefixes.AddressSize;

    // --- instruction fetch from RIP --------------------------------------------

    /// <summary>
    /// Fetches the next instruction byte from RIP. There is no CS limit in
    /// 64-bit mode, but RIP must stay canonical (#GP(0)), the instruction
    /// must stay within 15 bytes (#GP(0)), and the page must allow execution.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte FetchByte64()
    {
        ulong rip = State.Rip;
        if (rip - _longInstructionStart >= MaximumInstructionLength || !IsCanonical(rip))
        {
            throw new CpuException(13, 0);
        }

        State.Rip = rip + 1;

        ulong page = rip & ~0xFFFUL;
        if (page != _fetchPage64 || State.Cpl != _fetchCpl64)
        {
            _fetchPhysical64 = TranslateLongMode(rip, write: false, asSupervisor: false, execute: true) & 0xFFFFF000;
            _fetchPage64 = page;
            _fetchCpl64 = State.Cpl;
        }

        return _memory.ReadByte(_fetchPhysical64 | ((uint)rip & 0xFFF));
    }

    private ushort FetchWord64()
    {
        uint low = FetchByte64();
        uint high = FetchByte64();
        return (ushort)(low | (high << 8));
    }

    private uint FetchDword64()
    {
        uint low = FetchWord64();
        uint high = FetchWord64();
        return low | (high << 16);
    }

    private ulong FetchQword64()
    {
        ulong low = FetchDword64();
        ulong high = FetchDword64();
        return low | (high << 32);
    }

    /// <summary>
    /// An immediate of the operand size, as 64-bit mode encodes it: 16 or 32
    /// bits, and for a 64-bit operand a 32-bit immediate sign-extended (SDM
    /// Vol.2 §2.2.1.5) -- only MOV r64, imm64 has a full 8-byte one.
    /// </summary>
    private ulong FetchImmediate64(int operandBytes)
    {
        switch (operandBytes)
        {
            case 1:
                return FetchByte64();
            case 2:
                return FetchWord64();
            case 4:
                return FetchDword64();
            default:
                return (ulong)(long)(int)FetchDword64();
        }
    }

    // --- ModR/M and SIB ----------------------------------------------------------

    /// <summary>
    /// Decodes ModR/M, and SIB and the displacement when the operand is in
    /// memory, in 64-bit mode's forms (SDM Vol.2 §2.2.1.2-2.2.1.6):
    /// <list type="bullet">
    /// <item>REX.R extends reg, REX.B extends r/m or the SIB base, REX.X the SIB index;</item>
    /// <item>mod 00 with r/m 101 is RIP-relative (disp32 from the end of the instruction), whatever REX.B says;</item>
    /// <item>a SIB index of 100 without REX.X means no index (with REX.X it is R12);</item>
    /// <item>a SIB base of 101 with mod 00 means disp32 and no base;</item>
    /// <item>RSP or RBP as base selects SS (which matters only for the fault a non-canonical address raises).</item>
    /// </list>
    /// With 67h the registers are read as 32 bits and the address wraps at 4 GB.
    /// </summary>
    /// <param name="immediateBytes">
    /// How many immediate bytes follow the ModR/M operand in this
    /// instruction: the distance from the end of the displacement to the end
    /// of the instruction, which a RIP-relative address counts from.
    /// </param>
    private LongModeOperand DecodeModRm64(int immediateBytes)
    {
        byte modrm = FetchByte64();
        int mod = modrm >> 6;
        int rmField = modrm & 7;

        LongModeOperand operand = default;
        operand.Reg = (byte)(((modrm >> 3) & 7) | (_longPrefixes.RexR ? 8 : 0));

        if (mod == 3)
        {
            operand.IsRegister = true;
            operand.Register = (byte)(rmField | (_longPrefixes.RexB ? 8 : 0));
            return operand;
        }

        const int SibFollows = 4;
        const int RipRelativeOrNoBase = 5;
        const int StackPointerIndex = 4;
        const int FramePointerIndex = 5;

        SegReg segment = SegReg.Ds;
        ulong address = 0;

        if (rmField == SibFollows)
        {
            byte sib = FetchByte64();
            int scale = sib >> 6;
            int index = ((sib >> 3) & 7) | (_longPrefixes.RexX ? 8 : 0);
            int baseField = sib & 7;
            int baseRegister = baseField | (_longPrefixes.RexB ? 8 : 0);

            if (index != StackPointerIndex)
            {
                address += AddressRegister(index) << scale;
            }

            if (baseField == RipRelativeOrNoBase && mod == 0)
            {
                address += SignExtendedDisplacement32();
            }
            else
            {
                address += AddressRegister(baseRegister);
                if (baseRegister is StackPointerIndex or FramePointerIndex)
                {
                    segment = SegReg.Ss;
                }
            }
        }
        else if (rmField == RipRelativeOrNoBase && mod == 0)
        {
            ulong displacement = SignExtendedDisplacement32();
            ulong nextInstruction = State.Rip + (ulong)immediateBytes;
            address = nextInstruction + displacement;
        }
        else
        {
            int baseRegister = rmField | (_longPrefixes.RexB ? 8 : 0);
            address = AddressRegister(baseRegister);
            if (baseRegister == FramePointerIndex)
            {
                segment = SegReg.Ss;
            }
        }

        if (mod == 1)
        {
            address += (ulong)(long)(sbyte)FetchByte64();
        }
        else if (mod == 2)
        {
            address += SignExtendedDisplacement32();
        }

        operand.Address = AddressSize32In64 ? (uint)address : address;
        operand.Segment = _longPrefixes.Segment ?? segment;
        return operand;
    }

    private ulong SignExtendedDisplacement32() => (ulong)(long)(int)FetchDword64();

    /// <summary>A register as an address component: all 64 bits, or the low 32 under 67h.</summary>
    private ulong AddressRegister(int index)
    {
        ulong value = State.Gpr[index];
        return AddressSize32In64 ? (uint)value : value;
    }

    /// <summary>The operand's offset: resolved at decode time, already wrapped at 4 GB under 67h.</summary>
    private static ulong EffectiveOffset(in LongModeOperand operand) => operand.Address;

    // --- 64-bit linear addresses and memory --------------------------------------

    /// <summary>
    /// Segment plus offset in 64-bit mode: CS, DS, ES and SS have a base of
    /// zero and no limit; FS and GS keep a 64-bit base (SDM Vol.3A §3.4.4).
    /// A non-canonical result is #SS(0) through SS and #GP(0) otherwise.
    /// </summary>
    private ulong LinearAddress64(SegReg segment, ulong offset, int size)
    {
        ulong segmentBase = segment is SegReg.Fs or SegReg.Gs ? State.Segments[(int)segment].Base64 : 0;
        ulong first = segmentBase + offset;
        ulong last = first + (ulong)(size - 1);

        if (!IsCanonical(first) || !IsCanonical(last))
        {
            byte vector = segment == SegReg.Ss ? (byte)12 : (byte)13;
            throw new CpuException(vector, 0);
        }

        return first;
    }

    /// <summary>
    /// Reads 1, 2, 4 or 8 bytes. An access within one page is translated
    /// once; one that crosses a page is translated byte by byte, so a fault
    /// on the second page is raised before anything is returned.
    /// </summary>
    private ulong ReadMemory64(SegReg segment, ulong offset, int size)
    {
        ulong linear = LinearAddress64(segment, offset, size);
        bool crossesPage = (linear & 0xFFF) + (ulong)size > 0x1000;

        if (!crossesPage)
        {
            uint physical = TranslateLongMode(linear, write: false, asSupervisor: false, execute: false);
            return ReadPhysical(physical, size);
        }

        ulong value = 0;
        for (int i = size - 1; i >= 0; i--)
        {
            uint physical = TranslateLongMode(linear + (ulong)i, write: false, asSupervisor: false, execute: false);
            value = (value << 8) | _memory.ReadByte(physical);
        }

        return value;
    }

    /// <summary>
    /// Writes 1, 2, 4 or 8 bytes. Every page is translated -- and its dirty
    /// bit set -- before the first byte lands, so a fault on the second page
    /// leaves memory untouched (the rule the legacy WriteSplit keeps).
    /// </summary>
    private void WriteMemory64(SegReg segment, ulong offset, int size, ulong value)
    {
        ulong linear = LinearAddress64(segment, offset, size);
        bool crossesPage = (linear & 0xFFF) + (ulong)size > 0x1000;

        if (!crossesPage)
        {
            uint physical = TranslateLongMode(linear, write: true, asSupervisor: false, execute: false);
            WritePhysical(physical, size, value);
            return;
        }

        Span<uint> physicalBytes = stackalloc uint[8];
        for (int i = 0; i < size; i++)
        {
            physicalBytes[i] = TranslateLongMode(linear + (ulong)i, write: true, asSupervisor: false, execute: false);
        }

        for (int i = 0; i < size; i++)
        {
            _memory.WriteByte(physicalBytes[i], (byte)(value >> (8 * i)));
        }
    }

    private ulong ReadPhysical(uint physical, int size)
    {
        switch (size)
        {
            case 1:
                return _memory.ReadByte(physical);
            case 2:
                return _memory.ReadWord(physical);
            case 4:
                return _memory.ReadDword(physical);
            default:
                ulong low = _memory.ReadDword(physical);
                ulong high = _memory.ReadDword(physical + 4);
                return low | (high << 32);
        }
    }

    private void WritePhysical(uint physical, int size, ulong value)
    {
        switch (size)
        {
            case 1:
                _memory.WriteByte(physical, (byte)value);
                break;
            case 2:
                _memory.WriteWord(physical, (ushort)value);
                break;
            case 4:
                _memory.WriteDword(physical, (uint)value);
                break;
            default:
                _memory.WriteDword(physical, (uint)value);
                _memory.WriteDword(physical + 4, (uint)(value >> 32));
                break;
        }
    }

    // --- registers ------------------------------------------------------------------

    /// <summary>
    /// Reads a general register at an operand size. Byte registers follow the
    /// REX rule (SDM Vol.2 §2.2.1.2): with any REX prefix, 4-7 are SPL, BPL,
    /// SIL and DIL and 8-15 R8B-R15B; without one, 4-7 are AH, CH, DH, BH.
    /// </summary>
    private ulong ReadRegister64(int index, int size)
    {
        switch (size)
        {
            case 1:
                return _longPrefixes.RexPresent ? (byte)State.Gpr[index] : State.GetReg8(index);
            case 2:
                return State.GetReg16(index);
            case 4:
                return State.GetReg32(index);
            default:
                return State.Gpr[index];
        }
    }

    /// <summary>
    /// Writes a general register at an operand size: 8- and 16-bit writes
    /// keep the rest of the register, a 32-bit write zero-extends into bits
    /// 63:32, a 64-bit write replaces it (SDM Vol.1 §3.4.1.1).
    /// </summary>
    private void WriteRegister64(int index, int size, ulong value)
    {
        switch (size)
        {
            case 1:
                if (_longPrefixes.RexPresent)
                {
                    State.Gpr[index] = (State.Gpr[index] & ~0xFFUL) | (byte)value;
                }
                else
                {
                    State.SetReg8(index, (byte)value);
                }

                break;
            case 2:
                State.SetReg16(index, (ushort)value);
                break;
            case 4:
                State.SetReg32(index, (uint)value);
                break;
            default:
                State.Gpr[index] = value;
                break;
        }
    }

    /// <summary>Reads the r/m operand at an operand size, from a register or memory.</summary>
    private ulong ReadOperand64(in LongModeOperand operand, int size)
    {
        if (operand.IsRegister)
        {
            return ReadRegister64(operand.Register, size);
        }

        return ReadMemory64(operand.Segment, EffectiveOffset(operand), size);
    }

    /// <summary>Writes the r/m operand at an operand size, to a register or memory.</summary>
    private void WriteOperand64(in LongModeOperand operand, int size, ulong value)
    {
        if (operand.IsRegister)
        {
            WriteRegister64(operand.Register, size, value);
            return;
        }

        WriteMemory64(operand.Segment, EffectiveOffset(operand), size, value);
    }
}
