// The MMX instruction set (Pentium MMX, "P55C"): eight packed 64-bit integer
// operations aliased onto the x87 register file rather than a file of their
// own -- see Fpu/X87Unit.cs's "MMX" section and Fpu/Raw80.cs for why one
// register file serves both, and why every instruction below (EMMS excepted)
// calls Fpu.EnterMmxState() when it is done: "TOP <- 0, every tag valid" is
// what an operating system's FSAVE/FRSTOR-based task switch and a mixed
// FPU/MMX program both rely on (SDM Vol.1 ch.8, "MMX Technology System
// Programming Model").
//
// Dispatched from Cpu.TwoByte.cs's 0F switch; decoding (DecodeModRm, the r/m
// operand) reuses the same helpers every other two-byte opcode does. A 64-bit
// memory operand is checked and translated as one 8-byte unit -- exactly the
// way CMPXCHG8B is in Cpu.Pentium.cs, and for the same reason: a segment-limit
// violation must never leave a half-written result behind, so the whole
// access is validated before either half is touched. The 66/F2/F3 prefixes
// turn these same opcodes into SSE2's packed-integer instructions on later
// silicon; SSE2 is not implemented here, so a prefixed encoding is simply
// undefined, exactly as it is on every processor before the Pentium 4.

namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>Which lane width a shift instruction works on.</summary>
    private enum ShiftWidth { Word, Dword, Qword }

    /// <summary>
    /// EMMS and the sixty-odd 0F-prefixed opcodes the Pentium MMX added.
    /// <paramref name="opcode"/> is the second opcode byte, already read by
    /// Cpu.TwoByte.cs's switch.
    /// </summary>
    /// <summary>
    /// Whether 0F 71/72/73 names a real instruction: a register operand, and
    /// reg /2 (logical right), /4 (arithmetic right, not for quadwords) or /6
    /// (left).
    /// </summary>
    private static bool IsValidShiftImmediateEncoding(byte opcode, in ModRm m)
    {
        if (!m.IsRegister)
        {
            return false;
        }

        bool quadword = opcode == 0x73;

        return m.Reg switch
        {
            2 or 6 => true,
            4 => !quadword,
            _ => false,
        };
    }

    private void ExecuteMmx(byte opcode)
    {
        // Absent silicon, or a CR0.EM ("no coprocessor at all") processor:
        // both are #UD, not #NM. Unlike an x87 escape -- always a defined
        // opcode whether or not a coprocessor exists -- these bytes simply
        // are not instructions at all without MMX or with EM set, since
        // there is no register file behind them to alias. (Every MMX
        // instruction's SDM Vol.2B reference page lists CPUID.MMX and
        // CR0.EM together under #UD, and CR0.TS alone under #NM.)
        RequireFeature(CpuFeature.Mmx);
        if ((State.Cr0 & CpuState.Cr0Emulation) != 0) throw new CpuException(6);

        // With a 66/F2/F3 prefix these same bytes are SSE2's packed-integer
        // forms on a Pentium 4 or later. Aurora does not implement SSE2 yet,
        // so the prefixed encoding is undefined here, as it is on every
        // processor that predates it.
        if (_prefixes.OperandSize || _prefixes.Rep || _prefixes.RepNe)
        {
            throw new CpuException(6);
        }

        if (opcode == 0x77)   // EMMS: no ModRM byte, no operand.
        {
            if ((State.Cr0 & CpuState.Cr0TaskSwitched) != 0)
            {
                FaultNow(7, 0);
                return;
            }

            Fpu.EmptyMmxState();
            return;
        }

        ModRm m = DecodeModRm();

        // An invalid encoding of the immediate shift group is #UD, and #UD
        // outranks #NM (SDM vol. 3, exception priorities): it is checked
        // before TS, not inside the shift itself.
        if (opcode is 0x71 or 0x72 or 0x73 && !IsValidShiftImmediateEncoding(opcode, m))
        {
            throw new CpuException(6);
        }

        // TS says the registers belong to another task and must be saved and
        // reloaded first. The ModRM byte is decoded before this fault,
        // exactly as Cpu.Fpu.cs does for the x87 escapes, so the effective
        // address is computed -- and any segment or page fault raised -- in
        // the order a real processor would raise it.
        if ((State.Cr0 & CpuState.Cr0TaskSwitched) != 0)
        {
            FaultNow(7, 0);
            return;
        }

        switch (opcode)
        {
            case 0x6E: ExecuteMovdLoad(m); return;
            case 0x7E: ExecuteMovdStore(m); return;
            case 0x6F: ExecuteMovqLoad(m); return;
            case 0x7F: ExecuteMovqStore(m); return;

            case 0x71: ExecuteShiftImmediate(m, ShiftWidth.Word); return;
            case 0x72: ExecuteShiftImmediate(m, ShiftWidth.Dword); return;
            case 0x73: ExecuteShiftImmediate(m, ShiftWidth.Qword); return;

            case 0xF1: ExecuteShiftRegister(m, ShiftWidth.Word, left: true, arithmetic: false); return;
            case 0xF2: ExecuteShiftRegister(m, ShiftWidth.Dword, left: true, arithmetic: false); return;
            case 0xF3: ExecuteShiftRegister(m, ShiftWidth.Qword, left: true, arithmetic: false); return;
            case 0xD1: ExecuteShiftRegister(m, ShiftWidth.Word, left: false, arithmetic: false); return;
            case 0xD2: ExecuteShiftRegister(m, ShiftWidth.Dword, left: false, arithmetic: false); return;
            case 0xD3: ExecuteShiftRegister(m, ShiftWidth.Qword, left: false, arithmetic: false); return;
            case 0xE1: ExecuteShiftRegister(m, ShiftWidth.Word, left: false, arithmetic: true); return;
            case 0xE2: ExecuteShiftRegister(m, ShiftWidth.Dword, left: false, arithmetic: true); return;

            default: ExecuteMmxAlu(opcode, m); return;
        }
    }

    // --- MOVD / MOVQ -----------------------------------------------------------

    /// <summary>0F 6E: MMreg &lt;- r/m32, zero-extended into the high dword.</summary>
    private void ExecuteMovdLoad(in ModRm m)
    {
        uint value = m.IsRegister ? State.GetReg32(m.Register) : ReadMem32(m.Segment, m.Offset);
        Fpu.WriteMmx(m.Reg, value);
        Fpu.EnterMmxState();
    }

    /// <summary>0F 7E: r/m32 &lt;- the low dword of MMreg.</summary>
    private void ExecuteMovdStore(in ModRm m)
    {
        uint value = (uint)Fpu.ReadMmx(m.Reg);
        if (m.IsRegister) State.SetReg32(m.Register, value);
        else WriteMem32(m.Segment, m.Offset, value);
        Fpu.EnterMmxState();
    }

    /// <summary>0F 6F: MMreg &lt;- MMreg/m64.</summary>
    private void ExecuteMovqLoad(in ModRm m)
    {
        ulong value = ReadMmxSource(m);
        Fpu.WriteMmx(m.Reg, value);
        Fpu.EnterMmxState();
    }

    /// <summary>0F 7F: MMreg/m64 &lt;- MMreg.</summary>
    private void ExecuteMovqStore(in ModRm m)
    {
        ulong value = Fpu.ReadMmx(m.Reg);
        if (m.IsRegister) Fpu.WriteMmx(m.Register, value);
        else WriteMemQword(m.Segment, m.Offset, value);
        Fpu.EnterMmxState();
    }

    /// <summary>The rm-side 64-bit operand every arithmetic, logical and MOVQ-load form reads: MMreg when mod==3, an 8-byte memory operand otherwise.</summary>
    private ulong ReadMmxSource(in ModRm m)
        => m.IsRegister ? Fpu.ReadMmx(m.Register) : ReadMemQword(m.Segment, m.Offset);

    // --- packed arithmetic, logic, pack and unpack ------------------------------

    /// <summary>
    /// Every two-operand MMX instruction except the shifts: the destination is
    /// always MMreg (the reg field), the source always MMreg/m64 (the rm
    /// field), and the whole 64-bit result replaces the destination.
    /// </summary>
    private void ExecuteMmxAlu(byte opcode, in ModRm m)
    {
        ulong destination = Fpu.ReadMmx(m.Reg);
        ulong source = ReadMmxSource(m);

        ulong result = opcode switch
        {
            0x63 => PackWords(destination, source, value => SignedSaturateToByte(value)),      // PACKSSWB
            0x6B => PackSignedDwordsToWords(destination, source),                              // PACKSSDW
            0x67 => PackWords(destination, source, value => UnsignedSaturateToByte(value)),    // PACKUSWB

            0x60 => UnpackLowBytes(destination, source),                      // PUNPCKLBW
            0x61 => UnpackLowWords(destination, source),                      // PUNPCKLWD
            0x62 => UnpackLowDwords(destination, source),                     // PUNPCKLDQ
            0x68 => UnpackHighBytes(destination, source),                     // PUNPCKHBW
            0x69 => UnpackHighWords(destination, source),                     // PUNPCKHWD
            0x6A => UnpackHighDwords(destination, source),                    // PUNPCKHDQ

            0xFC => PerByte(destination, source, (a, b) => (byte)(a + b)),                                  // PADDB
            0xFD => PerWord(destination, source, (a, b) => (ushort)(a + b)),                                // PADDW
            0xFE => PerDword(destination, source, (a, b) => a + b),                                         // PADDD
            0xEC => PerByte(destination, source, (a, b) => SignedSaturateToByte((sbyte)a + (sbyte)b)),      // PADDSB
            0xED => PerWord(destination, source, (a, b) => SignedSaturateToWord((short)a + (short)b)),      // PADDSW
            0xDC => PerByte(destination, source, (a, b) => UnsignedSaturateToByte(a + b)),                  // PADDUSB
            0xDD => PerWord(destination, source, (a, b) => UnsignedSaturateToWord(a + b)),                  // PADDUSW

            0xF8 => PerByte(destination, source, (a, b) => (byte)(a - b)),                                  // PSUBB
            0xF9 => PerWord(destination, source, (a, b) => (ushort)(a - b)),                                // PSUBW
            0xFA => PerDword(destination, source, (a, b) => a - b),                                         // PSUBD
            0xE8 => PerByte(destination, source, (a, b) => SignedSaturateToByte((sbyte)a - (sbyte)b)),      // PSUBSB
            0xE9 => PerWord(destination, source, (a, b) => SignedSaturateToWord((short)a - (short)b)),      // PSUBSW
            0xD8 => PerByte(destination, source, (a, b) => UnsignedSaturateToByte(a - b)),                  // PSUBUSB
            0xD9 => PerWord(destination, source, (a, b) => UnsignedSaturateToWord(a - b)),                  // PSUBUSW

            0xD5 => PerWord(destination, source, (a, b) => (ushort)(a * b)),                                 // PMULLW: low 16 bits, sign-independent
            0xE5 => PerWord(destination, source, (a, b) => (ushort)(((short)a * (short)b) >> 16)),           // PMULHW: high 16 bits of the signed product
            0xF5 => MultiplyAddWords(destination, source),                                                   // PMADDWD

            0x74 => PerByte(destination, source, (a, b) => a == b ? (byte)0xFF : (byte)0),                   // PCMPEQB
            0x75 => PerWord(destination, source, (a, b) => a == b ? (ushort)0xFFFF : (ushort)0),             // PCMPEQW
            0x76 => PerDword(destination, source, (a, b) => a == b ? 0xFFFFFFFFu : 0u),                      // PCMPEQD
            0x64 => PerByte(destination, source, (a, b) => (sbyte)a > (sbyte)b ? (byte)0xFF : (byte)0),      // PCMPGTB
            0x65 => PerWord(destination, source, (a, b) => (short)a > (short)b ? (ushort)0xFFFF : (ushort)0),// PCMPGTW
            0x66 => PerDword(destination, source, (a, b) => (int)a > (int)b ? 0xFFFFFFFFu : 0u),             // PCMPGTD

            0xDB => destination & source,          // PAND
            0xDF => ~destination & source,         // PANDN: NOT(DEST) AND SRC
            0xEB => destination | source,          // POR
            0xEF => destination ^ source,          // PXOR

            _ => throw new CpuException(6),
        };

        Fpu.WriteMmx(m.Reg, result);
        Fpu.EnterMmxState();
    }

    // --- shifts ------------------------------------------------------------------
    //
    // The count is the *entire* 64-bit source operand, register or memory, not
    // its low byte: a count at or past the element width yields zero for a
    // logical shift and the sign bit repeated for an arithmetic one, computed
    // from that full width so a huge count (say, the top 32 bits nonzero)
    // saturates exactly as a small out-of-range one does (SDM Vol.2B, e.g.
    // PSLLW/PSLLD/PSLLQ "Operation").

    /// <summary>0F D1/D2/D3 (right, logical), E1/E2 (right, arithmetic), F1/F2/F3 (left): count comes from MMreg/m64.</summary>
    private void ExecuteShiftRegister(in ModRm m, ShiftWidth width, bool left, bool arithmetic)
    {
        ulong destination = Fpu.ReadMmx(m.Reg);
        ulong count = ReadMmxSource(m);
        Fpu.WriteMmx(m.Reg, ApplyShift(destination, count, width, left, arithmetic));
        Fpu.EnterMmxState();
    }

    /// <summary>
    /// Group 0F 71/72/73: the rm field is the register shifted in place (a
    /// memory operand is undefined here, unlike every other MMX instruction),
    /// and the reg field is an opcode extension -- /6 left, /2 right logical,
    /// /4 right arithmetic (not defined for the quadword form, since there is
    /// no PSRAQ). Every other reg field is undefined.
    /// </summary>
    private void ExecuteShiftImmediate(in ModRm m, ShiftWidth width)
    {
        if (!m.IsRegister) throw new CpuException(6);

        bool left, arithmetic;
        switch (m.Reg)
        {
            case 6: left = true; arithmetic = false; break;
            case 2: left = false; arithmetic = false; break;
            case 4:
                if (width == ShiftWidth.Qword) throw new CpuException(6);
                left = false; arithmetic = true;
                break;
            default:
                throw new CpuException(6);
        }

        byte count = FetchByte();
        ulong destination = Fpu.ReadMmx(m.Register);
        Fpu.WriteMmx(m.Register, ApplyShift(destination, count, width, left, arithmetic));
        Fpu.EnterMmxState();
    }

    private static ulong ApplyShift(ulong value, ulong count, ShiftWidth width, bool left, bool arithmetic)
        => width switch
        {
            ShiftWidth.Word => ShiftWordLanes(value, count, left, arithmetic),
            ShiftWidth.Dword => ShiftDwordLanes(value, count, left, arithmetic),
            _ => ShiftQword(value, count, left),
        };

    private static ulong ShiftWordLanes(ulong value, ulong count, bool left, bool arithmetic)
    {
        ulong result = 0;
        for (int lane = 0; lane < 4; lane++)
            result |= (ulong)ShiftWord(GetWordLane(value, lane), count, left, arithmetic) << (lane * 16);
        return result;
    }

    private static ushort ShiftWord(ushort value, ulong count, bool left, bool arithmetic)
    {
        if (left) return count >= 16 ? (ushort)0 : (ushort)(value << (int)count);

        if (arithmetic)
        {
            short signed = (short)value;
            if (count >= 16) return signed < 0 ? (ushort)0xFFFF : (ushort)0;
            return (ushort)(signed >> (int)count);
        }

        return count >= 16 ? (ushort)0 : (ushort)(value >> (int)count);
    }

    private static ulong ShiftDwordLanes(ulong value, ulong count, bool left, bool arithmetic)
    {
        uint low = ShiftDword(GetDwordLane(value, 0), count, left, arithmetic);
        uint high = ShiftDword(GetDwordLane(value, 1), count, left, arithmetic);
        return low | ((ulong)high << 32);
    }

    private static uint ShiftDword(uint value, ulong count, bool left, bool arithmetic)
    {
        if (left) return count >= 32 ? 0u : value << (int)count;

        if (arithmetic)
        {
            int signed = (int)value;
            if (count >= 32) return signed < 0 ? 0xFFFFFFFFu : 0u;
            return (uint)(signed >> (int)count);
        }

        return count >= 32 ? 0u : value >> (int)count;
    }

    private static ulong ShiftQword(ulong value, ulong count, bool left)
        => count >= 64 ? 0ul : left ? value << (int)count : value >> (int)count;

    // --- packed lane helpers -----------------------------------------------------

    private static byte GetByteLane(ulong value, int lane) => (byte)(value >> (lane * 8));
    private static ushort GetWordLane(ulong value, int lane) => (ushort)(value >> (lane * 16));
    private static uint GetDwordLane(ulong value, int lane) => lane == 0 ? (uint)value : (uint)(value >> 32);

    private static ulong PerByte(ulong a, ulong b, Func<byte, byte, byte> op)
    {
        ulong result = 0;
        for (int lane = 0; lane < 8; lane++)
            result |= (ulong)op(GetByteLane(a, lane), GetByteLane(b, lane)) << (lane * 8);
        return result;
    }

    private static ulong PerWord(ulong a, ulong b, Func<ushort, ushort, ushort> op)
    {
        ulong result = 0;
        for (int lane = 0; lane < 4; lane++)
            result |= (ulong)op(GetWordLane(a, lane), GetWordLane(b, lane)) << (lane * 16);
        return result;
    }

    private static ulong PerDword(ulong a, ulong b, Func<uint, uint, uint> op)
        => op(GetDwordLane(a, 0), GetDwordLane(b, 0)) | ((ulong)op(GetDwordLane(a, 1), GetDwordLane(b, 1)) << 32);

    /// <summary>
    /// PACKSSWB/PACKUSWB: the destination's four words, saturated by
    /// <paramref name="saturate"/>, become the low four bytes; the source's
    /// four words become the high four, in the same low-to-high order.
    /// </summary>
    private static ulong PackWords(ulong destination, ulong source, Func<short, byte> saturate)
    {
        ulong result = 0;
        for (int i = 0; i < 4; i++)
            result |= (ulong)saturate((short)GetWordLane(destination, i)) << (i * 8);
        for (int i = 0; i < 4; i++)
            result |= (ulong)saturate((short)GetWordLane(source, i)) << ((i + 4) * 8);
        return result;
    }

    /// <summary>PACKSSDW: the destination's two dwords become the low two (signed-saturated) words, the source's the high two.</summary>
    private static ulong PackSignedDwordsToWords(ulong destination, ulong source)
    {
        ulong result = 0;
        result |= (ulong)SignedSaturateToWord((int)GetDwordLane(destination, 0)) << 0;
        result |= (ulong)SignedSaturateToWord((int)GetDwordLane(destination, 1)) << 16;
        result |= (ulong)SignedSaturateToWord((int)GetDwordLane(source, 0)) << 32;
        result |= (ulong)SignedSaturateToWord((int)GetDwordLane(source, 1)) << 48;
        return result;
    }

    /// <summary>PUNPCKLBW: interleaves the low four bytes of each operand, destination first in each pair.</summary>
    private static ulong UnpackLowBytes(ulong destination, ulong source)
    {
        ulong result = 0;
        for (int i = 0; i < 4; i++)
        {
            result |= (ulong)GetByteLane(destination, i) << (i * 16);
            result |= (ulong)GetByteLane(source, i) << (i * 16 + 8);
        }
        return result;
    }

    /// <summary>PUNPCKHBW: as <see cref="UnpackLowBytes"/>, but from the high four bytes of each operand.</summary>
    private static ulong UnpackHighBytes(ulong destination, ulong source)
    {
        ulong result = 0;
        for (int i = 0; i < 4; i++)
        {
            result |= (ulong)GetByteLane(destination, i + 4) << (i * 16);
            result |= (ulong)GetByteLane(source, i + 4) << (i * 16 + 8);
        }
        return result;
    }

    /// <summary>PUNPCKLWD: interleaves the low two words of each operand.</summary>
    private static ulong UnpackLowWords(ulong destination, ulong source)
    {
        ulong result = 0;
        for (int i = 0; i < 2; i++)
        {
            result |= (ulong)GetWordLane(destination, i) << (i * 32);
            result |= (ulong)GetWordLane(source, i) << (i * 32 + 16);
        }
        return result;
    }

    /// <summary>PUNPCKHWD: as <see cref="UnpackLowWords"/>, but from the high two words of each operand.</summary>
    private static ulong UnpackHighWords(ulong destination, ulong source)
    {
        ulong result = 0;
        for (int i = 0; i < 2; i++)
        {
            result |= (ulong)GetWordLane(destination, i + 2) << (i * 32);
            result |= (ulong)GetWordLane(source, i + 2) << (i * 32 + 16);
        }
        return result;
    }

    /// <summary>PUNPCKLDQ: the destination's low dword, then the source's low dword.</summary>
    private static ulong UnpackLowDwords(ulong destination, ulong source)
        => GetDwordLane(destination, 0) | ((ulong)GetDwordLane(source, 0) << 32);

    /// <summary>PUNPCKHDQ: the destination's high dword, then the source's high dword.</summary>
    private static ulong UnpackHighDwords(ulong destination, ulong source)
        => GetDwordLane(destination, 1) | ((ulong)GetDwordLane(source, 1) << 32);

    /// <summary>
    /// PMADDWD: each of the two result dwords is the sum of two signed
    /// word-by-word products, computed and summed at full 32-bit width with
    /// no saturation -- the SDM's own operation listing wraps rather than
    /// saturates on the one input that can overflow (every lane at
    /// -32768 x -32768, whose sum is exactly 0x80000000), and the plain
    /// <c>int</c> arithmetic below reproduces that wrap directly.
    /// </summary>
    private static ulong MultiplyAddWords(ulong destination, ulong source)
    {
        uint LowDwordOfPair(int pairIndex)
        {
            int a0 = (short)GetWordLane(destination, pairIndex * 2);
            int b0 = (short)GetWordLane(source, pairIndex * 2);
            int a1 = (short)GetWordLane(destination, pairIndex * 2 + 1);
            int b1 = (short)GetWordLane(source, pairIndex * 2 + 1);
            return (uint)(a0 * b0 + a1 * b1);
        }

        return LowDwordOfPair(0) | ((ulong)LowDwordOfPair(1) << 32);
    }

    // --- saturation ----------------------------------------------------------

    private static byte SignedSaturateToByte(int value) => (byte)Math.Clamp(value, sbyte.MinValue, sbyte.MaxValue);
    private static byte UnsignedSaturateToByte(int value) => (byte)Math.Clamp(value, 0, byte.MaxValue);
    private static ushort SignedSaturateToWord(int value) => (ushort)Math.Clamp(value, short.MinValue, short.MaxValue);
    private static ushort UnsignedSaturateToWord(int value) => (ushort)Math.Clamp(value, 0, ushort.MaxValue);

    // --- the 8-byte memory operand ---------------------------------------------
    //
    // Checked and translated as one unit, exactly as CMPXCHG8B is in
    // Cpu.Pentium.cs (whose physical-quadword helpers are reused directly
    // below): a segment-limit violation must fault before either half of the
    // access has touched memory, not after the first dword already has.

    private ulong ReadMemQword(SegReg segment, uint offset)
    {
        const uint operandSize = 8;
        CheckLimit(segment, offset, operandSize);
        if (State.AlignmentChecked) CheckAlignment(offset, operandSize);

        uint linear = LinearAddress(segment, offset);
        uint lastLinear = linear + operandSize - 1;
        uint firstPhysical = TranslateLinear(linear, false);
        uint lastPhysical = TranslateLinear(lastLinear, false);
        bool onePage = (linear & QuadwordPageMask) == (lastLinear & QuadwordPageMask);

        return onePage ? ReadPhysicalQword(firstPhysical) : ReadSplitQword(linear, firstPhysical, lastPhysical);
    }

    private void WriteMemQword(SegReg segment, uint offset, ulong value)
    {
        const uint operandSize = 8;
        CheckLimit(segment, offset, operandSize);
        if (State.AlignmentChecked) CheckAlignment(offset, operandSize);

        uint linear = LinearAddress(segment, offset);
        uint lastLinear = linear + operandSize - 1;
        uint firstPhysical = TranslateLinear(linear, true);
        uint lastPhysical = TranslateLinear(lastLinear, true);
        bool onePage = (linear & QuadwordPageMask) == (lastLinear & QuadwordPageMask);

        if (onePage) WritePhysicalQword(firstPhysical, value);
        else WriteSplitQword(linear, firstPhysical, lastPhysical, value);
    }
}
