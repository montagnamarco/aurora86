// SSE2's 128-bit widened integer set (work package P4.2, plans/specs/sse2.md
// §4): "every MMX integer instruction widened to XMM with the same per-lane
// rules", plus the handful of genuinely new instructions SSE2 introduces
// alongside them (PUNPCKLQDQ/HQDQ, MOVDQA/MOVDQU, MOVD/MOVQ's own
// zero-extending XMM forms, MOVDQ2Q/MOVQ2DQ, PMOVMSKB's 16-bit XMM form,
// MASKMOVDQU, MOVNTDQ, PSHUFD/HW/LW, PSRLDQ/PSLLDQ).
//
// Most of these opcodes decompose into the *same* per-lane computation
// Cpu.Mmx.cs's own MmxAluLane already implements, applied independently to
// each of the two 64-bit halves of the wider XMM operands (PADDQ, PSUBQ and
// PMULUDQ included -- each is exactly one such 64-bit-wide lane, computed
// twice); PACKSSWB/PACKUSWB/PACKSSDW and PUNPCKL*/PUNPCKH* are the
// exceptions, since each combines both 64-bit halves of (up to) both
// operands into one result and so needs its own widened code, below.
//
// Dispatched from Cpu.Mmx.cs's ExecuteMmx, which has already decoded and
// validated the ModRM byte (the same invalid-shift-encoding and
// fixed-rm-shape checks the unprefixed MMX path uses, reused directly since
// the rule is identical) and confirmed the XMM-touching gating (CPUID.SSE2,
// CR0.EM, CR4.OSFXSR, CR0.TS) before calling in.

using Aurora86.Core.Cpu.Sse;

namespace Aurora86.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>
    /// Whether 0F 71/72/73, 66-prefixed, names a real instruction: a
    /// register operand, and reg /2 (logical right), /4 (arithmetic right,
    /// not for quadwords), /6 (left) -- the same rule
    /// <see cref="IsValidShiftImmediateEncoding"/> already applies to the
    /// unprefixed MMX form -- or, only for opcode 0x73, /3 (PSRLDQ) and /7
    /// (PSLLDQ), the byte-granular whole-register shifts that have no
    /// 64-bit MMX counterpart at all.
    /// </summary>
    private static bool IsValidXmmShiftImmediateEncoding(byte opcode, in ModRm m)
    {
        if (!m.IsRegister) return false;

        bool quadword = opcode == 0x73;
        return m.Reg switch
        {
            2 or 6 => true,
            4 => !quadword,
            3 or 7 => quadword,
            _ => false,
        };
    }

    private void ExecuteSse2Integer(byte opcode, in ModRm m)
    {
        switch (opcode)
        {
            case 0x6E: ExecuteMovdLoadXmm(m); return;
            case 0x7E:
                if (_prefixes.Rep) { ExecuteMovqLoadZeroExtend(m); return; }
                ExecuteMovdStoreXmm(m); return;
            case 0x6F: ExecuteMovdqLoad(m); return;
            case 0x7F: ExecuteMovdqStore(m); return;
            case 0x6C: ExecutePunpcklqdq(m); return;
            case 0x6D: ExecutePunpckhqdq(m); return;
            case 0x70: ExecutePshuf(m); return;
            case 0x71: ExecuteShiftImmediateXmm(opcode, m); return;
            case 0x72: ExecuteShiftImmediateXmm(opcode, m); return;
            case 0x73: ExecuteShiftImmediateXmm(opcode, m); return;
            case 0xC4: ExecutePinsrwXmm(m); return;
            case 0xC5: ExecutePextrwXmm(m); return;
            case 0xD7: ExecutePmovmskbXmm(m); return;
            case 0xD6:
                if (_prefixes.RepNe) { ExecuteMovdq2q(m); return; }
                if (_prefixes.Rep) { ExecuteMovq2dq(m); return; }
                ExecuteMovqStoreXmm(m); return;
            case 0xE7: ExecuteMovntdq(m); return;
            case 0xF7: ExecuteMaskmovdqu(m); return;
            case 0xD1: case 0xD2: case 0xD3:
            case 0xE1: case 0xE2:
            case 0xF1: case 0xF2: case 0xF3:
                ExecuteShiftRegisterXmm(opcode, m); return;
            default: ExecuteSse2IntegerAlu(opcode, m); return;
        }
    }

    // --- the per-half-decomposable ALU set, plus PACK*/PUNPCK* -----------------------

    private void ExecuteSse2IntegerAlu(byte opcode, in ModRm m)
    {
        XmmValue destination = Sse.Xmm(m.Reg);
        XmmValue source = ReadXmmSourceAligned(m);

        if (opcode is 0x60 or 0x61 or 0x62 or 0x68 or 0x69 or 0x6A)
        {
            bool highHalf = opcode is 0x68 or 0x69 or 0x6A;
            ulong destHalf = highHalf ? destination.High : destination.Low;
            ulong srcHalf = highHalf ? source.High : source.Low;

            (ulong low, ulong high) = opcode switch
            {
                0x60 or 0x68 => (UnpackLowBytes(destHalf, srcHalf), UnpackHighBytes(destHalf, srcHalf)),
                0x61 or 0x69 => (UnpackLowWords(destHalf, srcHalf), UnpackHighWords(destHalf, srcHalf)),
                _ => (UnpackLowDwords(destHalf, srcHalf), UnpackHighDwords(destHalf, srcHalf)),
            };
            Sse.Xmm(m.Reg) = XmmValue.FromHalves(low, high);
            return;
        }

        if (opcode is 0x63 or 0x67 or 0x6B)
        {
            Sse.Xmm(m.Reg) = opcode switch
            {
                0x63 => PackWordsXmm(destination, source, value => SignedSaturateToByte(value)),
                0x67 => PackWordsXmm(destination, source, value => UnsignedSaturateToByte(value)),
                _ => PackSignedDwordsToWordsXmm(destination, source),
            };
            return;
        }

        // Every remaining opcode -- PADD*/PSUB*/PMUL*/PCMP*/PAND*/POR/PXOR/
        // PAVG*/PMIN*/PMAX*/PSADBW/PMADDWD and SSE2's own PADDQ/PSUBQ/
        // PMULUDQ -- decomposes into two independent 64-bit lanes, exactly
        // the same per-lane computation the unprefixed 64-bit MMX form
        // already implements (the type summary's own remark).
        ulong lowResult = MmxAluLane(opcode, destination.Low, source.Low);
        ulong highResult = MmxAluLane(opcode, destination.High, source.High);
        Sse.Xmm(m.Reg) = XmmValue.FromHalves(lowResult, highResult);
    }

    /// <summary>PACKSSWB/PACKUSWB widened: the destination's own eight words (both 64-bit halves), saturated, become the low eight bytes; the source's eight words become the high eight, in the same low-to-high order.</summary>
    private static XmmValue PackWordsXmm(in XmmValue destination, in XmmValue source, Func<short, byte> saturate)
    {
        ulong low = 0, high = 0;
        for (int i = 0; i < 8; i++) low |= (ulong)saturate((short)GetXmmWordLane(destination, i)) << (i * 8);
        for (int i = 0; i < 8; i++) high |= (ulong)saturate((short)GetXmmWordLane(source, i)) << (i * 8);
        return XmmValue.FromHalves(low, high);
    }

    /// <summary>PACKSSDW widened: the destination's own four dwords (both halves), signed-saturated, become the low four words; the source's four dwords become the high four.</summary>
    private static XmmValue PackSignedDwordsToWordsXmm(in XmmValue destination, in XmmValue source)
    {
        ulong low = 0, high = 0;
        for (int i = 0; i < 4; i++) low |= (ulong)SignedSaturateToWord((int)GetXmmDwordLane(destination, i)) << (i * 16);
        for (int i = 0; i < 4; i++) high |= (ulong)SignedSaturateToWord((int)GetXmmDwordLane(source, i)) << (i * 16);
        return XmmValue.FromHalves(low, high);
    }

    private static byte GetXmmByteLane(in XmmValue v, int lane) => lane < 8 ? GetByteLane(v.Low, lane) : GetByteLane(v.High, lane - 8);
    private static ushort GetXmmWordLane(in XmmValue v, int lane) => lane < 4 ? GetWordLane(v.Low, lane) : GetWordLane(v.High, lane - 4);
    private static uint GetXmmDwordLane(in XmmValue v, int lane) => lane < 2 ? GetDwordLane(v.Low, lane) : GetDwordLane(v.High, lane - 2);

    // --- MOVD / MOVQ's XMM forms (0F 6E, 7E, 6F, 7F, D6) --------------------------------

    /// <summary>66 0F 6E: MOVD xmm, r/m32 -- zero-extends into the *entire* 128 bits, unlike the MMX form's 64.</summary>
    private void ExecuteMovdLoadXmm(in ModRm m)
    {
        uint value = m.IsRegister ? State.GetReg32(m.Register) : ReadMem32(m.Segment, m.Offset);
        Sse.Xmm(m.Reg) = new XmmValue { D0 = value };
    }

    /// <summary>66 0F 7E: MOVD r/m32, xmm -- the low dword only.</summary>
    private void ExecuteMovdStoreXmm(in ModRm m)
    {
        uint value = Sse.Xmm(m.Reg).D0;
        if (m.IsRegister) State.SetReg32(m.Register, value);
        else WriteMem32(m.Segment, m.Offset, value);
    }

    /// <summary>F3 0F 7E: MOVQ xmm1, xmm2/m64 -- the low 64 bits, zero-extended into the destination's upper 64 (plans/specs/sse2.md §1).</summary>
    private void ExecuteMovqLoadZeroExtend(in ModRm m)
    {
        ulong value = m.IsRegister ? Sse.Xmm(m.Register).Low : ReadMemQword(m.Segment, m.Offset);
        Sse.Xmm(m.Reg) = new XmmValue { D0 = (uint)value, D1 = (uint)(value >> 32) };
    }

    /// <summary>66/F3 0F 6F: MOVDQA (66, aligned) / MOVDQU (F3, unaligned) xmm, xmm/m128 -- a whole-128-bit load, exactly like MOVAPS/MOVUPS.</summary>
    private void ExecuteMovdqLoad(in ModRm m)
    {
        if (_prefixes.OperandSize)
        {
            Sse.Xmm(m.Reg) = ReadXmmSourceAligned(m);
            return;
        }

        Sse.Xmm(m.Reg) = m.IsRegister
            ? Sse.Xmm(m.Register)
            : XmmValue.FromHalves(ReadMemQword(m.Segment, m.Offset), ReadMemQword(m.Segment, m.Offset + 8));
    }

    /// <summary>66/F3 0F 7F: MOVDQA/MOVDQU store, the reverse of <see cref="ExecuteMovdqLoad"/>.</summary>
    private void ExecuteMovdqStore(in ModRm m)
    {
        XmmValue value = Sse.Xmm(m.Reg);
        if (m.IsRegister) { Sse.Xmm(m.Register) = value; return; }

        if (_prefixes.OperandSize) RequireAligned16(m.Segment, m.Offset);
        Validate128BitStore(m.Segment, m.Offset);
        WriteMemQword(m.Segment, m.Offset, value.Low);
        WriteMemQword(m.Segment, m.Offset + 8, value.High);
    }

    /// <summary>66 0F D6: MOVQ xmm2/m64, xmm1 -- stores the low 64 bits; a register destination has its own upper 64 bits zeroed (plans/specs/sse2.md §1: "D6 MOVQ xmm/m64, xmm (store)").</summary>
    private void ExecuteMovqStoreXmm(in ModRm m)
    {
        ulong value = Sse.Xmm(m.Reg).Low;
        if (m.IsRegister)
        {
            Sse.Xmm(m.Register) = new XmmValue { D0 = (uint)value, D1 = (uint)(value >> 32) };
            return;
        }
        WriteMemQword(m.Segment, m.Offset, value);
    }

    /// <summary>F2 0F D6: MOVDQ2Q mm, xmm -- register-register only, no memory form; the low 64 bits of an XMM register into an MM one, an "MMX in use" event like every other access to MM0-7.</summary>
    private void ExecuteMovdq2q(in ModRm m)
    {
        if (!m.IsRegister) throw new CpuException(6);
        if (DeliverPendingX87Exception())
        {
            return;
        }

        Fpu.WriteMmx(m.Reg, Sse.Xmm(m.Register).Low);
        Fpu.EnterMmxState();
    }

    /// <summary>F3 0F D6: MOVQ2DQ xmm, mm -- register-register only; an MM register's 64 bits, zero-extended into an XMM one.</summary>
    private void ExecuteMovq2dq(in ModRm m)
    {
        if (!m.IsRegister) throw new CpuException(6);
        if (DeliverPendingX87Exception())
        {
            return;
        }

        ulong value = Fpu.ReadMmx(m.Register);
        Sse.Xmm(m.Reg) = new XmmValue { D0 = (uint)value, D1 = (uint)(value >> 32) };
        Fpu.EnterMmxState();
    }

    // --- PUNPCKLQDQ / PUNPCKHQDQ (0F 6C/6D) ---------------------------------------------

    private void ExecutePunpcklqdq(in ModRm m)
    {
        XmmValue destination = Sse.Xmm(m.Reg);
        XmmValue source = ReadXmmSourceAligned(m);
        Sse.Xmm(m.Reg) = XmmValue.FromHalves(destination.Low, source.Low);
    }

    private void ExecutePunpckhqdq(in ModRm m)
    {
        XmmValue destination = Sse.Xmm(m.Reg);
        XmmValue source = ReadXmmSourceAligned(m);
        Sse.Xmm(m.Reg) = XmmValue.FromHalves(destination.High, source.High);
    }

    // --- PSHUFD / PSHUFHW / PSHUFLW (0F 70) ---------------------------------------------

    /// <summary>66 PSHUFD: all four dword lanes of the destination come from the (16-byte aligned, when memory) source alone, selected by imm8. F3 PSHUFHW: the low four words pass through unchanged, the high four are shuffled among themselves. F2 PSHUFLW: the reverse.</summary>
    private void ExecutePshuf(in ModRm m)
    {
        XmmValue source = ReadXmmSourceAligned(m);
        byte imm8 = FetchByte();

        if (_prefixes.OperandSize)
        {
            Sse.Xmm(m.Reg) = new XmmValue
            {
                D0 = source.Lane(imm8 & 3),
                D1 = source.Lane((imm8 >> 2) & 3),
                D2 = source.Lane((imm8 >> 4) & 3),
                D3 = source.Lane((imm8 >> 6) & 3),
            };
            return;
        }

        bool highShuffled = _prefixes.Rep;   // F3: PSHUFHW shuffles the high half; F2: PSHUFLW shuffles the low half.
        ulong shuffledSource = highShuffled ? source.High : source.Low;
        ulong unchanged = highShuffled ? source.Low : source.High;

        ulong shuffled = 0;
        for (int lane = 0; lane < 4; lane++)
        {
            int selector = (imm8 >> (lane * 2)) & 3;
            shuffled |= (ulong)GetWordLane(shuffledSource, selector) << (lane * 16);
        }

        Sse.Xmm(m.Reg) = highShuffled ? XmmValue.FromHalves(unchanged, shuffled) : XmmValue.FromHalves(shuffled, unchanged);
    }

    // --- shifts (0F 71/72/73 immediate group, D1-D3/E1-E2/F1-F3 by register) -----------

    /// <summary>The immediate-count shift group, widened: the rm field (mod=11 only, already validated) names the whole XMM register shifted in place; the same count applies to both 64-bit halves independently -- except reg /3 (PSRLDQ) and /7 (PSLLDQ), only valid for opcode 0x73, a byte-granular shift of the *whole* 128 bits, not per-lane at all.</summary>
    private void ExecuteShiftImmediateXmm(byte opcode, in ModRm m)
    {
        byte count = FetchByte();

        if (opcode == 0x73 && m.Reg is 3 or 7)
        {
            ExecuteByteShiftWholeRegister(m, left: m.Reg == 7, count);
            return;
        }

        ShiftWidth width = opcode switch { 0x71 => ShiftWidth.Word, 0x72 => ShiftWidth.Dword, _ => ShiftWidth.Qword };
        (bool left, bool arithmetic) = m.Reg switch
        {
            6 => (true, false),
            2 => (false, false),
            4 => (false, true),
            _ => throw new InvalidOperationException("unreachable: already validated by IsValidXmmShiftImmediateEncoding"),
        };

        XmmValue destination = Sse.Xmm(m.Register);
        ulong low = ApplyShift(destination.Low, count, width, left, arithmetic);
        ulong high = ApplyShift(destination.High, count, width, left, arithmetic);
        Sse.Xmm(m.Register) = XmmValue.FromHalves(low, high);
    }

    /// <summary>PSRLDQ (right) / PSLLDQ (left): a logical, zero-filling shift of the entire 128-bit value by <paramref name="count"/> bytes; a count of 16 or more clears the whole register.</summary>
    private void ExecuteByteShiftWholeRegister(in ModRm m, bool left, byte count)
    {
        if (count >= 16)
        {
            Sse.Xmm(m.Register) = default;
            return;
        }

        XmmValue value = Sse.Xmm(m.Register);
        UInt128 wide = ((UInt128)value.D3 << 96) | ((UInt128)value.D2 << 64) | ((UInt128)value.D1 << 32) | value.D0;
        wide = left ? wide << (count * 8) : wide >> (count * 8);

        Sse.Xmm(m.Register) = new XmmValue
        {
            D0 = (uint)wide,
            D1 = (uint)(wide >> 32),
            D2 = (uint)(wide >> 64),
            D3 = (uint)(wide >> 96),
        };
    }

    /// <summary>D1-D3 (right, logical), E1-E2 (right, arithmetic), F1-F3 (left): the count comes from the low 64 bits of the (16-byte aligned, when memory) xmm/m128 source -- its own upper 64 bits are never consulted, exactly as the SDM's own operation pseudocode reads only COUNT[63:0].</summary>
    private void ExecuteShiftRegisterXmm(byte opcode, in ModRm m)
    {
        XmmValue destination = Sse.Xmm(m.Reg);
        ulong count = ReadXmmSourceAligned(m).Low;

        (ShiftWidth width, bool left, bool arithmetic) = opcode switch
        {
            0xF1 => (ShiftWidth.Word, true, false),
            0xF2 => (ShiftWidth.Dword, true, false),
            0xF3 => (ShiftWidth.Qword, true, false),
            0xD1 => (ShiftWidth.Word, false, false),
            0xD2 => (ShiftWidth.Dword, false, false),
            0xD3 => (ShiftWidth.Qword, false, false),
            0xE1 => (ShiftWidth.Word, false, true),
            _ => (ShiftWidth.Dword, false, true),   // 0xE2
        };

        ulong low = ApplyShift(destination.Low, count, width, left, arithmetic);
        ulong high = ApplyShift(destination.High, count, width, left, arithmetic);
        Sse.Xmm(m.Reg) = XmmValue.FromHalves(low, high);
    }

    // --- PINSRW / PEXTRW / PMOVMSKB, widened (0F C4/C5/D7) ------------------------------

    /// <summary>66 0F C4: PINSRW xmm, r32/m16, imm8 -- the low word of the source replaces one of the destination's eight words, named by the low three bits of the immediate (one bit wider than the MMX form's four-word field).</summary>
    private void ExecutePinsrwXmm(in ModRm m)
    {
        ushort value = m.IsRegister ? (ushort)State.GetReg32(m.Register) : ReadMem16(m.Segment, m.Offset);
        byte imm8 = FetchByte();
        int lane = imm8 & 7;

        ref XmmValue destination = ref Sse.Xmm(m.Reg);
        int dwordLane = lane / 2;
        uint dword = destination.Lane(dwordLane);
        dword = (lane % 2) == 0 ? (dword & 0xFFFF_0000u) | value : (dword & 0x0000_FFFFu) | ((uint)value << 16);
        destination.SetLane(dwordLane, dword);
    }

    /// <summary>66 0F C5: PEXTRW r32, xmm, imm8 -- register-register only, already rejected with #UD by <see cref="IsInvalidRmShapeForOpcode"/> when this dispatch was reached: one of the source's eight words, zero-extended into a GPR.</summary>
    private void ExecutePextrwXmm(in ModRm m)
    {
        byte imm8 = FetchByte();
        int lane = imm8 & 7;
        ushort value = GetXmmWordLane(Sse.Xmm(m.Register), lane);
        State.SetReg32(m.Reg, value);
    }

    /// <summary>66 0F D7: PMOVMSKB r32, xmm -- register-register only (see <see cref="ExecutePextrwXmm"/>'s own remark): a GPR gets the sign (high) bit of each of the source's sixteen bytes.</summary>
    private void ExecutePmovmskbXmm(in ModRm m)
    {
        XmmValue value = Sse.Xmm(m.Register);
        uint mask = 0;
        for (int lane = 0; lane < 16; lane++)
        {
            if ((GetXmmByteLane(value, lane) & 0x80) != 0) mask |= 1u << lane;
        }
        State.SetReg32(m.Reg, mask);
    }

    // --- MOVNTDQ (0F E7), MASKMOVDQU (0F F7) --------------------------------------------

    /// <summary>66 0F E7: MOVNTDQ, memory only, 16-byte aligned -- already rejected with #UD by <see cref="IsInvalidRmShapeForOpcode"/> for a register operand: a non-temporal hint with nothing behind it to cache, so an ordinary aligned store.</summary>
    private void ExecuteMovntdq(in ModRm m)
    {
        RequireAligned16(m.Segment, m.Offset);
        Validate128BitStore(m.Segment, m.Offset);
        XmmValue value = Sse.Xmm(m.Reg);
        WriteMemQword(m.Segment, m.Offset, value.Low);
        WriteMemQword(m.Segment, m.Offset + 8, value.High);
    }

    /// <summary>
    /// 66 0F F7: MASKMOVDQU xmm, xmm -- register-register only, already
    /// rejected with #UD by <see cref="IsInvalidRmShapeForOpcode"/> for a
    /// memory operand. The reg-field register supplies the data, the
    /// rm-field register the mask: each of the sixteen bytes whose mask
    /// byte has bit 7 set is stored to DS:(E)DI, exactly like MASKMOVQ one
    /// register file up (Cpu.Mmx.cs's own ExecuteMaskmovq), sixteen bytes
    /// instead of eight and with no alignment requirement on the implicit
    /// destination (the SDM documents none, and it is addressed through
    /// EDI, not a ModRM operand the general legacy-SSE alignment rule ever
    /// reaches).
    /// </summary>
    private void ExecuteMaskmovdqu(in ModRm m)
    {
        XmmValue data = Sse.Xmm(m.Reg);
        XmmValue mask = Sse.Xmm(m.Register);
        SegReg segment = EffectiveSegment(SegReg.Ds);
        uint baseOffset = AddressSize32 ? State.Edi : State.Di;

        ValidateMaskmovdquDestination(segment, baseOffset);

        for (int lane = 0; lane < 16; lane++)
        {
            if ((GetXmmByteLane(mask, lane) & 0x80) == 0) continue;

            uint offset = AddressSize32 ? baseOffset + (uint)lane : (ushort)(baseOffset + lane);
            WriteMem8(segment, offset, GetXmmByteLane(data, lane));
        }
    }

    /// <summary>MASKMOVDQU's own 16-byte version of <see cref="ValidateMaskmovqDestination"/>: the whole access validated as one unit before any byte is actually stored.</summary>
    private void ValidateMaskmovdquDestination(SegReg segment, uint baseOffset)
    {
        const uint operandSize = 16;

        if (!AddressSize32 && baseOffset + operandSize - 1 > 0xFFFFu)
        {
            throw new CpuException(13, 0);
        }

        CheckLimit(segment, baseOffset, operandSize);

        uint linear = LinearAddress(segment, baseOffset);
        uint lastLinear = linear + operandSize - 1;
        TranslateLinear(linear, true);
        TranslateLinear(lastLinear, true);
    }
}
