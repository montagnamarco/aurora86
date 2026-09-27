// The IEEE double-precision half of SSE2: CVT*, the packed/scalar arithmetic
// (ADDPD/ADDSD and its five siblings), the four bitwise instructions,
// SQRTPD/SQRTSD, CMPPD/CMPSD and COMISD/UCOMISD. Every actual floating-point
// decision lives in Sse/DoubleFloat.cs; this file only does what an
// instruction does with those answers -- which lanes it touches, where its
// operands and result live, and (through Cpu.Sse.cs's RaiseIfUnmasked)
// whether an unmasked exception leaves its destination untouched. There are
// no SSE2 analogues of RSQRTPD/RCPPD -- the SDM never defines any.
//
// Dispatched from Cpu.Sse2.cs's ExecuteSse2Float switch, except the three
// conversions that mix single/double/int32 in ways no PS/SS opcode shares
// (CVTPS2PD/CVTPD2PS/CVTSD2SS/CVTSS2SD at 0x5A, CVTDQ2PS/CVTPS2DQ/CVTTPS2DQ
// at 0x5B, CVTTPD2DQ/CVTPD2DQ/CVTDQ2PD at 0xE6), which have no valid
// unprefixed-PS meaning to share an opcode byte with and so are dispatched
// directly from Cpu.TwoByte.cs into this file's own ExecuteSse2Conversions.

using Aurora386.Core.Cpu.Sse;

namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    // --- ADDPD/ADDSD, MULPD/MULSD, SUBPD/SUBSD, MINPD/MINSD, DIVPD/DIVSD, MAXPD/MAXSD ---

    private void ExecuteBinaryArithPd(bool scalar, SseBinaryOp op)
    {
        var m = DecodeModRm();
        if (SseTaskSwitched()) return;

        RoundingMode rc = CurrentRoundingMode();
        bool flushToZero = CurrentFlushToZero();
        bool underflowMasked = CurrentUnderflowMasked();
        bool daz = CurrentDaz();
        XmmValue destination = Sse.Xmm(m.Reg);

        if (scalar)
        {
            ulong source = ReadXmmSourceScalarQword(m);
            (ulong result, FpFlags flags) = ApplyBinaryPd(op, destination.Low, source, rc, flushToZero, underflowMasked, daz);
            if (RaiseIfUnmasked(flags)) return;
            ref XmmValue destinationRef = ref Sse.Xmm(m.Reg);
            destinationRef.D0 = (uint)result;
            destinationRef.D1 = (uint)(result >> 32);
            return;
        }

        XmmValue source2 = ReadXmmSourceAligned(m);
        (ulong lowResult, FpFlags lowFlags) = ApplyBinaryPd(op, destination.Low, source2.Low, rc, flushToZero, underflowMasked, daz);
        (ulong highResult, FpFlags highFlags) = ApplyBinaryPd(op, destination.High, source2.High, rc, flushToZero, underflowMasked, daz);
        if (RaiseIfUnmasked(lowFlags | highFlags)) return;

        Sse.Xmm(m.Reg) = XmmValue.FromHalves(lowResult, highResult);
    }

    private static (ulong, FpFlags) ApplyBinaryPd(SseBinaryOp op, ulong a, ulong b, RoundingMode rc, bool flushToZero, bool underflowMasked, bool daz) => op switch
    {
        SseBinaryOp.Add => DoubleFloat.Add(a, b, rc, flushToZero, underflowMasked, daz),
        SseBinaryOp.Mul => DoubleFloat.Multiply(a, b, rc, flushToZero, underflowMasked, daz),
        SseBinaryOp.Sub => DoubleFloat.Subtract(a, b, rc, flushToZero, underflowMasked, daz),
        SseBinaryOp.Div => DoubleFloat.Divide(a, b, rc, flushToZero, underflowMasked, daz),
        SseBinaryOp.Min => DoubleFloat.Min(a, b, daz),
        SseBinaryOp.Max => DoubleFloat.Max(a, b, daz),
        _ => throw new InvalidOperationException(),
    };

    // --- SQRTPD/SQRTSD -----------------------------------------------------------------

    private void ExecuteUnaryArithPd(bool scalar)
    {
        var m = DecodeModRm();
        if (SseTaskSwitched()) return;

        RoundingMode rc = CurrentRoundingMode();
        bool flushToZero = CurrentFlushToZero();
        bool underflowMasked = CurrentUnderflowMasked();
        bool daz = CurrentDaz();

        if (scalar)
        {
            ulong source = ReadXmmSourceScalarQword(m);
            (ulong result, FpFlags flags) = DoubleFloat.Sqrt(source, rc, flushToZero, underflowMasked, daz);
            if (RaiseIfUnmasked(flags)) return;
            ref XmmValue destinationRef = ref Sse.Xmm(m.Reg);
            destinationRef.D0 = (uint)result;
            destinationRef.D1 = (uint)(result >> 32);
            return;
        }

        XmmValue source2 = ReadXmmSourceAligned(m);
        (ulong lowResult, FpFlags lowFlags) = DoubleFloat.Sqrt(source2.Low, rc, flushToZero, underflowMasked, daz);
        (ulong highResult, FpFlags highFlags) = DoubleFloat.Sqrt(source2.High, rc, flushToZero, underflowMasked, daz);
        if (RaiseIfUnmasked(lowFlags | highFlags)) return;

        Sse.Xmm(m.Reg) = XmmValue.FromHalves(lowResult, highResult);
    }

    // --- ANDPD, ANDNPD, ORPD, XORPD -----------------------------------------------------

    /// <summary>Whole-128-bit bitwise, no MXCSR flag ever, packed only -- identical to Cpu.Sse.Arithmetic.cs's own ExecuteLogical, just under the 66 prefix.</summary>
    private void ExecuteLogicalPd(bool scalar, SseLogicalOp op)
    {
        var m = DecodeModRm();
        if (scalar) throw new CpuException(6);
        if (SseTaskSwitched()) return;

        XmmValue destination = Sse.Xmm(m.Reg);
        XmmValue source = ReadXmmSourceAligned(m);

        (ulong low, ulong high) = op switch
        {
            SseLogicalOp.And => (destination.Low & source.Low, destination.High & source.High),
            SseLogicalOp.AndNot => (~destination.Low & source.Low, ~destination.High & source.High),
            SseLogicalOp.Or => (destination.Low | source.Low, destination.High | source.High),
            _ => (destination.Low ^ source.Low, destination.High ^ source.High),
        };

        Sse.Xmm(m.Reg) = XmmValue.FromHalves(low, high);
    }

    // --- CMPPD/CMPSD (0F C2) -------------------------------------------------------------

    private void ExecuteCmpPd(bool scalar)
    {
        var m = DecodeModRm();
        if (SseTaskSwitched()) return;

        XmmValue destination = Sse.Xmm(m.Reg);
        bool daz = CurrentDaz();

        if (scalar)
        {
            ulong source = ReadXmmSourceScalarQword(m);
            byte predicate = FetchByte();
            (ulong result, FpFlags flags) = DoubleFloat.ComparePredicate(destination.Low, source, predicate & 7, daz);
            if (RaiseIfUnmasked(flags)) return;
            ref XmmValue destinationRef = ref Sse.Xmm(m.Reg);
            destinationRef.D0 = (uint)result;
            destinationRef.D1 = (uint)(result >> 32);
            return;
        }

        XmmValue source2 = ReadXmmSourceAligned(m);
        byte predicate4 = FetchByte();
        (ulong lowResult, FpFlags lowFlags) = DoubleFloat.ComparePredicate(destination.Low, source2.Low, predicate4 & 7, daz);
        (ulong highResult, FpFlags highFlags) = DoubleFloat.ComparePredicate(destination.High, source2.High, predicate4 & 7, daz);
        if (RaiseIfUnmasked(lowFlags | highFlags)) return;

        Sse.Xmm(m.Reg) = XmmValue.FromHalves(lowResult, highResult);
    }

    // --- COMISD/UCOMISD (0F 2F/2E) --------------------------------------------------------

    private void ExecuteComisd(bool scalar, bool signalOnQuietNaN)
    {
        var m = DecodeModRm();
        if (scalar) throw new CpuException(6);
        if (SseTaskSwitched()) return;

        ulong destination = Sse.Xmm(m.Reg).Low;
        ulong source = ReadXmmSourceScalarQword(m);

        (bool zf, bool pf, bool cf, FpFlags flags) = DoubleFloat.CompareForFlags(destination, source, unorderedOnlySignaling: !signalOnQuietNaN, CurrentDaz());
        if (RaiseIfUnmasked(flags)) return;

        Flags.Zero = zf;
        Flags.Parity = pf;
        Flags.Carry = cf;
        Flags.Overflow = false;
        Flags.Sign = false;
        Flags.Adjust = false;
    }

    // --- CVTPI2PD/CVTSI2SD (0F 2A) ------------------------------------------------------

    /// <summary>CVTPI2PD: mm/m64 -&gt; both lanes of xmm, exact. CVTSI2SD (F2): r/m32 -&gt; the low lane only, upper 64 bits unchanged, exact in every RC (plans/specs/sse2.md's own test case 11).</summary>
    private void ExecuteCvtToPd(bool scalar)
    {
        var m = DecodeModRm();
        if (SseTaskSwitched()) return;
        // Intel's SIMD exception table exempts CVTPI2PD's memory source:
        // only the MMX-register form synchronizes pending x87 exceptions.
        if (!scalar && m.IsRegister && DeliverPendingX87Exception())
        {
            return;
        }

        if (scalar)
        {
            int value = (int)(m.IsRegister ? State.GetReg32(m.Register) : ReadMem32(m.Segment, m.Offset));
            (ulong result, _) = DoubleFloat.ConvertInt32ToDouble(value);
            ref XmmValue destinationRef = ref Sse.Xmm(m.Reg);
            destinationRef.D0 = (uint)result;
            destinationRef.D1 = (uint)(result >> 32);
            return;
        }

        // CVTPI2PD reads an MM register when its source is one: an "MMX in
        // use" event like CVTPI2PS's own remark (Cpu.Sse.Arithmetic.cs),
        // even though this instruction is gated on Sse2, not Mmx.
        ulong source = m.IsRegister ? Fpu.ReadMmx(m.Register) : ReadMemQword(m.Segment, m.Offset);
        (ulong r0, _) = DoubleFloat.ConvertInt32ToDouble((int)(uint)source);
        (ulong r1, _) = DoubleFloat.ConvertInt32ToDouble((int)(uint)(source >> 32));

        Sse.Xmm(m.Reg) = XmmValue.FromHalves(r0, r1);
        if (m.IsRegister) Fpu.EnterMmxState();
    }

    // --- CVTPD2PI/CVTTPD2PI, CVTSD2SI/CVTTSD2SI (0F 2D/2C) -----------------------------

    /// <summary>CVT(T)PD2PI: xmm/m128 -&gt; the two packed int32 in an MM register. CVT(T)SD2SI (F2): xmm/m64 -&gt; r32.</summary>
    private void ExecuteCvtFromPd(bool scalar, bool truncate)
    {
        var m = DecodeModRm();
        if (SseTaskSwitched()) return;
        if (!scalar && DeliverPendingX87Exception())
        {
            return;
        }

        RoundingMode rc = CurrentRoundingMode();
        bool daz = CurrentDaz();

        if (scalar)
        {
            ulong source = ReadXmmSourceScalarQword(m);
            (int result, FpFlags flags) = DoubleFloat.ConvertToInt32(source, rc, truncate, daz);
            if (RaiseIfUnmasked(flags)) return;
            State.SetReg32(m.Reg, unchecked((uint)result));
            return;
        }

        XmmValue source2 = ReadXmmSourceAligned(m);
        (int r0, FpFlags f0) = DoubleFloat.ConvertToInt32(source2.Low, rc, truncate, daz);
        (int r1, FpFlags f1) = DoubleFloat.ConvertToInt32(source2.High, rc, truncate, daz);
        if (RaiseIfUnmasked(f0 | f1)) return;

        Fpu.WriteMmx(m.Reg, unchecked((uint)r0) | ((ulong)unchecked((uint)r1) << 32));
        Fpu.EnterMmxState();
    }

    // --- 0F 5A / 5B / E6: the conversions with no unprefixed-PS meaning to share ---------
    //
    // None of these three opcodes has a valid encoding at all without
    // SSE2 -- unlike every opcode ExecuteSse2Float shares with Cpu.Sse.cs's
    // own PS/SS switch, there is no "none = PS" case here to fall back to,
    // so this method does its own RequireSseInstruction/TS gating rather
    // than being reached through ExecuteSse.

    /// <summary>Dispatched directly from Cpu.TwoByte.cs for opcodes 0x5A, 0x5B and 0xE6.</summary>
    private void ExecuteSse2Conversions(byte opcode)
    {
        RequireSseInstruction(CpuFeature.Sse2);

        bool packedDouble = _prefixes.OperandSize;   // 66
        bool scalarDoubleToSingle = _prefixes.RepNe;  // F2
        bool scalarOrTruncate = _prefixes.Rep;        // F3
        int prefixCount = (packedDouble ? 1 : 0) + (scalarDoubleToSingle ? 1 : 0) + (scalarOrTruncate ? 1 : 0);
        if (prefixCount > 1) throw new CpuException(6);

        // Not every one of the four prefix states is a real instruction at
        // every one of these three opcodes -- a review finding: 0x5A alone
        // has all four (none/66/F2/F3); 0x5B has no F2 form at all (no
        // "CVTSS2DQ" exists -- only none/66/F3), and 0xE6 has no unprefixed
        // form at all (only 66/F2/F3 -- CVTDQ2PS already owns the bare
        // 0F 5B byte, not 0F E6). Without this check both fell through to
        // whichever branch below happened to read false/false, silently
        // executing CVTDQ2PS for F2 0F 5B and CVTPD2DQ for unprefixed 0F E6
        // instead of #UD.
        bool none = !packedDouble && !scalarDoubleToSingle && !scalarOrTruncate;
        if (opcode == 0x5B && scalarDoubleToSingle) throw new CpuException(6);
        if (opcode == 0xE6 && none) throw new CpuException(6);

        switch (opcode)
        {
            case 0x5A: ExecuteCvt5A(packedDouble, scalarDoubleToSingle, scalarOrTruncate); return;
            case 0x5B: ExecuteCvt5B(packedDouble, scalarOrTruncate); return;
            case 0xE6: ExecuteCvtE6(packedDouble, scalarDoubleToSingle, scalarOrTruncate); return;
            default: throw new CpuException(6);
        }
    }

    /// <summary>0F 5A: none CVTPS2PD (xmm/m64 -&gt; xmm, two lanes, exact), 66 CVTPD2PS (xmm/m128 -&gt; xmm low 2 lanes, upper 2 zeroed, rounds by RC), F2 CVTSD2SS (scalar, rounds by RC), F3 CVTSS2SD (scalar, exact).</summary>
    private void ExecuteCvt5A(bool packedDouble, bool scalarDoubleToSingle, bool scalarSingleToDouble)
    {
        var m = DecodeModRm();
        if (SseTaskSwitched()) return;

        RoundingMode rc = CurrentRoundingMode();
        bool flushToZero = CurrentFlushToZero();
        bool underflowMasked = CurrentUnderflowMasked();
        bool daz = CurrentDaz();

        if (scalarSingleToDouble)   // F3: CVTSS2SD, exact.
        {
            uint source = ReadXmmSourceScalarDword(m);
            (ulong result, FpFlags flags) = DoubleFloat.ConvertSingleToDouble(source, daz);
            if (RaiseIfUnmasked(flags)) return;
            ref XmmValue destinationRef = ref Sse.Xmm(m.Reg);
            destinationRef.D0 = (uint)result;
            destinationRef.D1 = (uint)(result >> 32);
            return;
        }

        if (scalarDoubleToSingle)   // F2: CVTSD2SS, rounds by RC.
        {
            ulong source = ReadXmmSourceScalarQword(m);
            (uint result, FpFlags flags) = DoubleFloat.ConvertDoubleToSingle(source, rc, flushToZero, underflowMasked, daz);
            if (RaiseIfUnmasked(flags)) return;
            Sse.Xmm(m.Reg).D0 = result;
            return;
        }

        if (packedDouble)   // 66: CVTPD2PS, rounds by RC; source is m128 (aligned), result low 2 lanes, upper 2 zeroed.
        {
            XmmValue source = ReadXmmSourceAligned(m);
            (uint r0, FpFlags f0) = DoubleFloat.ConvertDoubleToSingle(source.Low, rc, flushToZero, underflowMasked, daz);
            (uint r1, FpFlags f1) = DoubleFloat.ConvertDoubleToSingle(source.High, rc, flushToZero, underflowMasked, daz);
            if (RaiseIfUnmasked(f0 | f1)) return;
            Sse.Xmm(m.Reg) = new XmmValue { D0 = r0, D1 = r1, D2 = 0, D3 = 0 };
            return;
        }

        // none: CVTPS2PD, exact; source is m64 (two packed singles, unaligned).
        ulong lowDwordPair = m.IsRegister ? Sse.Xmm(m.Register).Low : ReadMemQword(m.Segment, m.Offset);
        (ulong d0, FpFlags flags0) = DoubleFloat.ConvertSingleToDouble((uint)lowDwordPair, daz);
        (ulong d1, FpFlags flags1) = DoubleFloat.ConvertSingleToDouble((uint)(lowDwordPair >> 32), daz);
        if (RaiseIfUnmasked(flags0 | flags1)) return;
        Sse.Xmm(m.Reg) = XmmValue.FromHalves(d0, d1);
    }

    /// <summary>0F 5B: none CVTDQ2PS (int32x4 -&gt; float x4, rounds by RC), 66 CVTPS2DQ (float x4 -&gt; int32x4, rounds by RC), F3 CVTTPS2DQ (truncate). Every lane independent, single precision throughout -- no DoubleFloat involvement at all.</summary>
    private void ExecuteCvt5B(bool packedDouble, bool truncate)
    {
        var m = DecodeModRm();
        if (SseTaskSwitched()) return;

        RoundingMode rc = CurrentRoundingMode();
        bool flushToZero = CurrentFlushToZero();
        bool underflowMasked = CurrentUnderflowMasked();
        bool daz = CurrentDaz();

        XmmValue source = ReadXmmSourceAligned(m);
        Span<uint> results = stackalloc uint[4];
        FpFlags allFlags = FpFlags.None;

        for (int lane = 0; lane < 4; lane++)
        {
            uint sourceLane = source.Lane(lane);
            if (packedDouble)   // CVTPS2DQ: float -> int32, rounds by RC.
            {
                (int r, FpFlags f) = SingleFloat.ConvertToInt32(sourceLane, rc, truncate: false, daz);
                results[lane] = unchecked((uint)r);
                allFlags |= f;
            }
            else if (truncate)   // CVTTPS2DQ: float -> int32, truncate.
            {
                (int r, FpFlags f) = SingleFloat.ConvertToInt32(sourceLane, rc, truncate: true, daz);
                results[lane] = unchecked((uint)r);
                allFlags |= f;
            }
            else   // CVTDQ2PS: int32 -> float, rounds by RC.
            {
                (uint r, FpFlags f) = SingleFloat.ConvertInt32ToSingle(unchecked((int)sourceLane), rc, flushToZero, underflowMasked);
                results[lane] = r;
                allFlags |= f;
            }
        }

        if (RaiseIfUnmasked(allFlags)) return;
        Sse.Xmm(m.Reg) = new XmmValue { D0 = results[0], D1 = results[1], D2 = results[2], D3 = results[3] };
    }

    /// <summary>0F E6: 66 CVTTPD2DQ (truncate), F2 CVTPD2DQ (rounds by RC), F3 CVTDQ2PD (int32x2 -&gt; double x2, exact, from the low 64 bits of the source). The two float-to-integer forms read a packed double m128 and write two int32 (upper 64 bits of the destination zeroed); CVTDQ2PD reads an m64/low-64-of-xmm source.</summary>
    private void ExecuteCvtE6(bool truncate66, bool round64, bool intToDouble)
    {
        var m = DecodeModRm();
        if (SseTaskSwitched()) return;

        RoundingMode rc = CurrentRoundingMode();
        bool daz = CurrentDaz();

        if (intToDouble)   // F3: CVTDQ2PD, exact.
        {
            ulong lowDwordPair = m.IsRegister ? Sse.Xmm(m.Register).Low : ReadMemQword(m.Segment, m.Offset);
            (ulong d0, _) = DoubleFloat.ConvertInt32ToDouble((int)(uint)lowDwordPair);
            (ulong d1, _) = DoubleFloat.ConvertInt32ToDouble((int)(uint)(lowDwordPair >> 32));
            Sse.Xmm(m.Reg) = XmmValue.FromHalves(d0, d1);
            return;
        }

        // 66 (truncate) or F2 (round by RC): packed double -> packed int32, upper 64 bits zeroed.
        bool truncate = truncate66;
        XmmValue source = ReadXmmSourceAligned(m);
        (int r0, FpFlags f0) = DoubleFloat.ConvertToInt32(source.Low, rc, truncate, daz);
        (int r1, FpFlags f1) = DoubleFloat.ConvertToInt32(source.High, rc, truncate, daz);
        if (RaiseIfUnmasked(f0 | f1)) return;

        Sse.Xmm(m.Reg) = new XmmValue { D0 = unchecked((uint)r0), D1 = unchecked((uint)r1), D2 = 0, D3 = 0 };
    }
}
