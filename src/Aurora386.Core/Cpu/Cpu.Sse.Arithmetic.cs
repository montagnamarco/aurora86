// The IEEE single-precision half of SSE: CVT*, the packed/scalar arithmetic
// (ADDPS/ADDSS and its five siblings), the four bitwise instructions, SQRT/
// RSQRT/RCP, CMPPS/CMPSS and COMISS/UCOMISS. Every actual floating-point
// decision -- rounding, NaN propagation, which MXCSR flags a result raises
// -- lives in Sse/SingleFloat.cs; this file only does what an instruction
// does with those answers: which lanes it touches, where its operands and
// result live, and (through Cpu.Sse.cs's RaiseIfUnmasked) whether an
// unmasked exception leaves its destination untouched.
//
// Dispatched from Cpu.Sse.cs's ExecuteSse switch.

using Aurora386.Core.Cpu.Sse;

namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    private enum SseBinaryOp { Add, Mul, Sub, Min, Div, Max }
    private enum SseUnaryOp { Sqrt, Rsqrt, Rcp }
    private enum SseLogicalOp { And, AndNot, Or, Xor }

    // --- ADDPS/ADDSS, MULPS/MULSS, SUBPS/SUBSS, MINPS/MINSS, DIVPS/DIVSS, MAXPS/MAXSS ---

    private void ExecuteBinaryArith(bool scalar, SseBinaryOp op)
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
            uint source = ReadXmmSourceScalarDword(m);
            (uint result, FpFlags flags) = ApplyBinary(op, destination.D0, source, rc, flushToZero, underflowMasked, daz);
            if (RaiseIfUnmasked(flags)) return;
            Sse.Xmm(m.Reg).D0 = result;
            return;
        }

        XmmValue source4 = ReadXmmSourceAligned(m);
        Span<uint> results = stackalloc uint[4];
        FpFlags allFlags = FpFlags.None;
        for (int lane = 0; lane < 4; lane++)
        {
            (uint result, FpFlags flags) = ApplyBinary(op, destination.Lane(lane), source4.Lane(lane), rc, flushToZero, underflowMasked, daz);
            results[lane] = result;
            allFlags |= flags;
        }
        if (RaiseIfUnmasked(allFlags)) return;

        Sse.Xmm(m.Reg) = new XmmValue { D0 = results[0], D1 = results[1], D2 = results[2], D3 = results[3] };
    }

    private static (uint, FpFlags) ApplyBinary(SseBinaryOp op, uint a, uint b, RoundingMode rc, bool flushToZero, bool underflowMasked, bool daz) => op switch
    {
        SseBinaryOp.Add => SingleFloat.Add(a, b, rc, flushToZero, underflowMasked, daz),
        SseBinaryOp.Mul => SingleFloat.Multiply(a, b, rc, flushToZero, underflowMasked, daz),
        SseBinaryOp.Sub => SingleFloat.Subtract(a, b, rc, flushToZero, underflowMasked, daz),
        SseBinaryOp.Div => SingleFloat.Divide(a, b, rc, flushToZero, underflowMasked, daz),
        SseBinaryOp.Min => SingleFloat.Min(a, b, daz),
        SseBinaryOp.Max => SingleFloat.Max(a, b, daz),
        _ => throw new InvalidOperationException(),
    };

    // --- SQRTPS/SQRTSS, RSQRTPS/RSQRTSS, RCPPS/RCPSS -----------------------------

    private void ExecuteUnaryArith(bool scalar, SseUnaryOp op)
    {
        var m = DecodeModRm();
        if (SseTaskSwitched()) return;

        RoundingMode rc = CurrentRoundingMode();
        bool flushToZero = CurrentFlushToZero();
        bool underflowMasked = CurrentUnderflowMasked();
        bool daz = CurrentDaz();

        if (scalar)
        {
            uint source = ReadXmmSourceScalarDword(m);
            (uint result, FpFlags flags) = ApplyUnary(op, source, rc, flushToZero, underflowMasked, daz);
            if (RaiseIfUnmasked(flags)) return;
            Sse.Xmm(m.Reg).D0 = result;
            return;
        }

        XmmValue source4 = ReadXmmSourceAligned(m);
        Span<uint> results = stackalloc uint[4];
        FpFlags allFlags = FpFlags.None;
        for (int lane = 0; lane < 4; lane++)
        {
            (uint result, FpFlags flags) = ApplyUnary(op, source4.Lane(lane), rc, flushToZero, underflowMasked, daz);
            results[lane] = result;
            allFlags |= flags;
        }
        if (RaiseIfUnmasked(allFlags)) return;

        Sse.Xmm(m.Reg) = new XmmValue { D0 = results[0], D1 = results[1], D2 = results[2], D3 = results[3] };
    }

    private static (uint, FpFlags) ApplyUnary(SseUnaryOp op, uint value, RoundingMode rc, bool flushToZero, bool underflowMasked, bool daz) => op switch
    {
        SseUnaryOp.Sqrt => SingleFloat.Sqrt(value, rc, flushToZero, underflowMasked, daz),
        SseUnaryOp.Rsqrt => SingleFloat.ReciprocalSqrtApprox(value),
        SseUnaryOp.Rcp => SingleFloat.ReciprocalApprox(value),
        _ => throw new InvalidOperationException(),
    };

    // --- ANDPS, ANDNPS, ORPS, XORPS -----------------------------------------------

    /// <summary>Whole-128-bit bitwise, no MXCSR flag ever, packed only -- there is no scalar (SS) form of any of the four.</summary>
    private void ExecuteLogical(bool scalar, SseLogicalOp op)
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

    // --- CMPPS/CMPSS (0F C2) -------------------------------------------------------

    private void ExecuteCmp(bool scalar)
    {
        var m = DecodeModRm();
        if (SseTaskSwitched()) return;

        XmmValue destination = Sse.Xmm(m.Reg);
        bool daz = CurrentDaz();

        if (scalar)
        {
            uint source = ReadXmmSourceScalarDword(m);
            byte predicate = FetchByte();
            (uint result, FpFlags flags) = SingleFloat.ComparePredicate(destination.D0, source, predicate & 7, daz);
            if (RaiseIfUnmasked(flags)) return;
            Sse.Xmm(m.Reg).D0 = result;
            return;
        }

        XmmValue source4 = ReadXmmSourceAligned(m);
        byte predicate4 = FetchByte();
        Span<uint> results = stackalloc uint[4];
        FpFlags allFlags = FpFlags.None;
        for (int lane = 0; lane < 4; lane++)
        {
            (uint result, FpFlags flags) = SingleFloat.ComparePredicate(destination.Lane(lane), source4.Lane(lane), predicate4 & 7, daz);
            results[lane] = result;
            allFlags |= flags;
        }
        if (RaiseIfUnmasked(allFlags)) return;

        Sse.Xmm(m.Reg) = new XmmValue { D0 = results[0], D1 = results[1], D2 = results[2], D3 = results[3] };
    }

    // --- COMISS/UCOMISS (0F 2F/2E) --------------------------------------------------

    /// <summary>Neither has an F3 (scalar-prefixed) form -- both already compare a single m32 pair.</summary>
    private void ExecuteComiss(bool scalar, bool signalOnQuietNaN)
    {
        var m = DecodeModRm();
        if (scalar) throw new CpuException(6);
        if (SseTaskSwitched()) return;

        uint destination = Sse.Xmm(m.Reg).D0;
        uint source = ReadXmmSourceScalarDword(m);

        (bool zf, bool pf, bool cf, FpFlags flags) = SingleFloat.CompareForFlags(destination, source, unorderedOnlySignaling: !signalOnQuietNaN, CurrentDaz());
        if (RaiseIfUnmasked(flags)) return;

        Flags.Zero = zf;
        Flags.Parity = pf;
        Flags.Carry = cf;
        Flags.Overflow = false;
        Flags.Sign = false;
        Flags.Adjust = false;
    }

    // --- CVTPI2PS/CVTSI2SS (0F 2A) ---------------------------------------------------

    /// <summary>CVTPI2PS: mm/m64 -&gt; the low two lanes of xmm, upper two unchanged. CVTSI2SS (F3): r/m32 -&gt; the low lane only, upper three unchanged.</summary>
    private void ExecuteCvtToPs(bool scalar)
    {
        var m = DecodeModRm();
        if (SseTaskSwitched()) return;
        if (!scalar && DeliverPendingX87Exception())
        {
            return;
        }

        RoundingMode rc = CurrentRoundingMode();
        bool flushToZero = CurrentFlushToZero();
        bool underflowMasked = CurrentUnderflowMasked();

        if (scalar)
        {
            int value = (int)(m.IsRegister ? State.GetReg32(m.Register) : ReadMem32(m.Segment, m.Offset));
            (uint result, FpFlags flags) = SingleFloat.ConvertInt32ToSingle(value, rc, flushToZero, underflowMasked);
            if (RaiseIfUnmasked(flags)) return;
            Sse.Xmm(m.Reg).D0 = result;
            return;
        }

        // CVTPI2PS reads an MM register when its source is one: an "MMX in
        // use" event like every other access to MM0-7 (Cpu.Mmx.cs's own
        // remark on Fpu.EnterMmxState()), even though this instruction is
        // gated on Sse alone, not Mmx -- the spec's §2 MMX-register list
        // does not include it.
        ulong source = m.IsRegister ? Fpu.ReadMmx(m.Register) : ReadMemQword(m.Segment, m.Offset);
        (uint r0, FpFlags f0) = SingleFloat.ConvertInt32ToSingle((int)(uint)source, rc, flushToZero, underflowMasked);
        (uint r1, FpFlags f1) = SingleFloat.ConvertInt32ToSingle((int)(uint)(source >> 32), rc, flushToZero, underflowMasked);
        if (RaiseIfUnmasked(f0 | f1)) return;

        Sse.Xmm(m.Reg).D0 = r0;
        Sse.Xmm(m.Reg).D1 = r1;
        if (m.IsRegister) Fpu.EnterMmxState();
    }

    // --- CVTPS2PI/CVTTPS2PI, CVTSS2SI/CVTTSS2SI (0F 2D/2C) ---------------------------

    /// <summary>CVT(T)PS2PI: xmm/m64 -&gt; the two packed int32 in an MM register (only the source's low two lanes are read). CVT(T)SS2SI (F3): xmm/m32 -&gt; r32.</summary>
    private void ExecuteCvtFromPs(bool scalar, bool truncate)
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
            uint source = ReadXmmSourceScalarDword(m);
            (int result, FpFlags flags) = SingleFloat.ConvertToInt32(source, rc, truncate, daz);
            if (RaiseIfUnmasked(flags)) return;
            State.SetReg32(m.Reg, unchecked((uint)result));
            return;
        }

        uint lane0, lane1;
        if (m.IsRegister)
        {
            XmmValue value = Sse.Xmm(m.Register);
            lane0 = value.D0;
            lane1 = value.D1;
        }
        else
        {
            ulong low = ReadMemQword(m.Segment, m.Offset);
            lane0 = (uint)low;
            lane1 = (uint)(low >> 32);
        }

        (int r0, FpFlags f0) = SingleFloat.ConvertToInt32(lane0, rc, truncate, daz);
        (int r1, FpFlags f1) = SingleFloat.ConvertToInt32(lane1, rc, truncate, daz);
        if (RaiseIfUnmasked(f0 | f1)) return;

        Fpu.WriteMmx(m.Reg, unchecked((uint)r0) | ((ulong)unchecked((uint)r1) << 32));
        Fpu.EnterMmxState();
    }
}
