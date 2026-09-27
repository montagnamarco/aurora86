namespace Aurora386.Core.Cpu.Sse;

/// <summary>MXCSR bits 13-14: how a rounding operation breaks a tie or picks a direction when its exact result is not representable.</summary>
public enum RoundingMode : uint
{
    Nearest = 0,
    Down = 1,        // toward -infinity
    Up = 2,          // toward +infinity
    TowardZero = 3,
}

/// <summary>
/// The six IEEE-754 exception conditions a single-precision operation can
/// raise, numbered exactly like MXCSR's own sticky flag bits (bits 0-5) so a
/// computed value ORs straight into MXCSR with no translation.
/// </summary>
[Flags]
public enum FpFlags : uint
{
    None = 0,
    Invalid = MxcsrBits.InvalidFlag,
    Denormal = MxcsrBits.DenormalFlag,
    DivideByZero = MxcsrBits.DivideByZeroFlag,
    Overflow = MxcsrBits.OverflowFlag,
    Underflow = MxcsrBits.UnderflowFlag,
    Precision = MxcsrBits.PrecisionFlag,
}

/// <summary>How COMISS/UCOMISS and CMPPS/CMPSS report an ordered comparison's outcome, before NaN is even considered.</summary>
public enum CompareOutcome { LessThan, Equal, GreaterThan, Unordered }

/// <summary>
/// IEEE-754 single-precision arithmetic at the bit level, computed the way
/// MXCSR's rounding control and exception flags require -- not through
/// .NET's <see langword="float"/> operators, which are round-to-nearest
/// only and report no exception flags at all.
///
/// Multiply, divide and square root are computed in <see langword="double"/>
/// and the result is then rounded down to single with the requested
/// <see cref="RoundingMode"/> (<see cref="RoundDoubleToSingle"/>), rather
/// than working out each operation's 24-bit mantissa by hand:
/// <list type="bullet">
/// <item>Multiply of two values that already fit a single's 24-bit mantissa
/// never needs more than about 48 significant bits to state its result
/// exactly -- comfortably inside a double's 53-bit mantissa -- so the
/// double result is the true, infinitely precise answer with no rounding of
/// its own.</item>
/// <item>Divide and square root are themselves computed by .NET's own
/// double arithmetic, which is IEEE-754 correctly-rounded to 53 bits. Given
/// that 53-bit intermediate, rounding it again to 24 bits reproduces
/// exactly what rounding the true (infinite-precision) result straight to
/// 24 bits would have given, ties included: this is the standard
/// double-rounding safety argument (see e.g. Figueroa, "On the Behavior of
/// IEEE Division and Square Root with Double Rounding"), which holds
/// whenever the intermediate precision is at least twice the target's plus
/// two bits -- 53 >= 2*24+2 = 50 here, with headroom to spare.</item>
/// </list>
/// Add and subtract are <em>not</em> computed this way: a first version
/// widened both operands to double and let double arithmetic combine them,
/// on the reasoning above -- but single's own exponent range spans about
/// 276 powers of two, and two operands whose exponents differ by more than
/// a double's 53-bit mantissa can bridge (a denormal added to -FLT_MAX is
/// the extreme case) silently lose the Precision and Underflow flags that
/// way: the double addition itself already rounds the tiny operand away
/// before this code ever sees the result. Measuring that version against
/// the host oracle (tools/NativeOracle) is what caught it.
/// <see cref="AddFinite"/> instead aligns both operands' significands on a
/// common binary point in a 128-bit fixed-point frame with a real
/// guard/round/sticky triple, exactly as hardware itself does.
///
/// Every arithmetic and comparison instruction's exact NaN-propagation
/// priority, its Denormal-flag conditions on each special-cased shortcut,
/// and MIN/MAX's and DIVPS's own departures from the SDM's usual prose were
/// likewise settled by measuring against the host oracle rather than
/// assumed from the spec text -- each such place says so and cites what was
/// measured.
/// </summary>
public static class SingleFloat
{
    public const uint SignMask = 0x8000_0000u;
    public const uint ExponentMask = 0x7F80_0000u;
    public const uint MantissaMask = 0x007F_FFFFu;

    public const uint PositiveZero = 0u;
    public const uint NegativeZero = SignMask;
    public const uint PositiveInfinity = ExponentMask;
    public const uint NegativeInfinity = SignMask | ExponentMask;

    /// <summary>The default QNaN a masked invalid operation with no NaN operand of its own to propagate produces (SDM Vol.1 §4.8.3.4; plans/specs/sse.md §4's own decision).</summary>
    public const uint DefaultQNaN = 0xFFC0_0000u;

    private const uint QuietBit = 0x0040_0000u;

    /// <summary>
    /// Where a normalized significand's own leading 1 sits inside the
    /// 128-bit fixed-point frame <see cref="AddFinite"/> and
    /// <see cref="RoundDoubleToSingle"/> both align significands on: a
    /// single's own 24-bit significand (bit 23) shifted left by 96 for
    /// headroom, or a double's own 53-bit significand (bit 52) shifted left
    /// by 67 for the same reason -- either way this is the one reference
    /// point <see cref="PackNormalized"/> and <see cref="RoundSignificandTo24Bits"/>
    /// both measure every shift and every extracted bit against.
    /// </summary>
    /// <summary>internal (not private): DoubleFloat.cs's CVTSD2SS/CVTPD2PS share this exact reference point when handing an already-decomposed double bit pattern to <see cref="RoundWideSignificandToSingle"/> -- see that method's own remark.</summary>
    internal const int WideFrameTopBitIndex = 119;

    public static bool SignBit(uint bits) => (bits & SignMask) != 0;
    public static bool IsNaN(uint bits) => (bits & ExponentMask) == ExponentMask && (bits & MantissaMask) != 0;
    public static bool IsQuietNaN(uint bits) => IsNaN(bits) && (bits & QuietBit) != 0;
    public static bool IsSignalingNaN(uint bits) => IsNaN(bits) && (bits & QuietBit) == 0;
    public static bool IsInfinity(uint bits) => (bits & ExponentMask) == ExponentMask && (bits & MantissaMask) == 0;
    public static bool IsZero(uint bits) => (bits & ~SignMask) == 0;
    public static bool IsDenormal(uint bits) => (bits & ExponentMask) == 0 && (bits & MantissaMask) != 0;
    public static uint Quiet(uint bits) => bits | QuietBit;

    public static float ToFloat(uint bits) => BitConverter.Int32BitsToSingle(unchecked((int)bits));
    public static uint FromFloat(float value) => unchecked((uint)BitConverter.SingleToInt32Bits(value));

    private static FpFlags DenormalFlagOf(uint a) => IsDenormal(a) ? FpFlags.Denormal : FpFlags.None;
    private static FpFlags DenormalFlagOf(uint a, uint b) => (IsDenormal(a) || IsDenormal(b)) ? FpFlags.Denormal : FpFlags.None;

    /// <summary>
    /// MXCSR's DAZ bit (work package P4.2, plans/specs/sse2.md's coordinator
    /// decision: "DAZ implemented -- denormal inputs treated as signed zero,
    /// sets no DE"). Only settable at all on a model whose MXCSR_MASK has bit
    /// 6 (<see cref="SseUnit.MxcsrMaskFor(CpuModel)"/> is per-model from this work package
    /// on); every call site below applies this to each floating-point
    /// <em>input</em> before any other classification runs, so a denormal
    /// operand DAZ replaced with a zero of its own sign is indistinguishable,
    /// from here on, from an operand that really was zero -- it raises no
    /// Denormal flag and takes whichever zero-operand shortcut the real
    /// instruction would. RCPPS/RSQRTPS already zero a denormal source
    /// unconditionally, independently of DAZ (their own remark on
    /// <see cref="ZeroOutADenormalSource"/>), so they take no <c>daz</c>
    /// parameter at all.
    /// </summary>
    private static uint ApplyDaz(uint bits, bool daz) => daz && IsDenormal(bits) ? (SignBit(bits) ? NegativeZero : PositiveZero) : bits;

    /// <summary>
    /// The NaN a binary operation returns when at least one operand is a
    /// NaN. plans/specs/sse.md §4 states the priority as "destination SNaN
    /// (quieted) beats source SNaN (quieted) beats destination QNaN beats
    /// source QNaN"; measuring this Pentium III work package's own
    /// coordinator-mandated host oracle against a real CPU's MULSS
    /// (tools/NativeOracle, dest=QNaN/src=SNaN and dest=SNaN/src=QNaN
    /// fixed cases) instead shows a strict operand-order rule with no
    /// SNaN/QNaN cross-comparison at all: the destination (SRC1) wins
    /// whenever it is any NaN, signaling or not, and only when it is not a
    /// NaN at all does the source (SRC2) get a turn. Implemented as
    /// measured -- the coordinator's own decision for this file is to match
    /// the host bit for bit -- with the spec's contrary claim flagged here
    /// as a correction for whoever maintains plans/specs/sse.md next. Sets
    /// <see cref="FpFlags.Invalid"/> whenever either operand was signaling,
    /// independently of which one is actually returned.
    /// </summary>
    public static (uint Result, FpFlags Flags) PropagateNaN(uint destination, uint source)
    {
        bool destSignaling = IsSignalingNaN(destination);
        bool srcSignaling = IsSignalingNaN(source);
        FpFlags flags = (destSignaling || srcSignaling) ? FpFlags.Invalid : FpFlags.None;

        if (IsNaN(destination)) return (Quiet(destination), flags);
        if (IsNaN(source)) return (Quiet(source), flags);
        return (DefaultQNaN, flags);
    }

    public static (uint Result, FpFlags Flags) Add(uint a, uint b, RoundingMode rc, bool flushToZero, bool underflowMasked, bool daz = false) => AddOrSubtract(a, b, subtract: false, rc, flushToZero, underflowMasked, daz);
    public static (uint Result, FpFlags Flags) Subtract(uint a, uint b, RoundingMode rc, bool flushToZero, bool underflowMasked, bool daz = false) => AddOrSubtract(a, b, subtract: true, rc, flushToZero, underflowMasked, daz);

    private static (uint, FpFlags) AddOrSubtract(uint a, uint b, bool subtract, RoundingMode rc, bool flushToZero, bool underflowMasked, bool daz)
    {
        a = ApplyDaz(a, daz);
        b = ApplyDaz(b, daz);

        // NaN propagation looks at the operands exactly as given: measuring
        // SUBPS against the host oracle shows its effective "b <- -b"
        // negation is an arithmetic step that never reaches a NaN's own
        // sign bit -- a NaN operand propagates with the sign it already
        // had, not the one negating it first would leave it with. The
        // negation below is applied afterwards, only on the path that
        // still needs an actual floating-point subtraction.
        if (IsNaN(a) || IsNaN(b)) return PropagateNaN(a, b);

        if (subtract) b ^= SignMask;   // a - b computes as a + (-b) from here on.

        // Denormal is suppressed only on a shortcut that never really looks
        // at the operands' values at all: the NaN path above, and (below)
        // the genuinely invalid inf-inf combination that produces the
        // default QNaN -- the same rule measured for MAXPS's own NaN
        // shortcut. An ordinary "one operand is infinity" or "both are
        // zero" shortcut still reports a denormal *other* operand
        // (measured against DIVPS's own infinity/zero shortcuts): it still
        // has to inspect that operand's class to know what the shortcut's
        // result even is.
        FpFlags denormalFlag = DenormalFlagOf(a, b);

        if (IsInfinity(a) || IsInfinity(b))
        {
            if (IsInfinity(a) && IsInfinity(b) && SignBit(a) != SignBit(b))
            {
                return (DefaultQNaN, FpFlags.Invalid);   // +inf + -inf
            }

            uint infinityResult = IsInfinity(a) ? a : b;
            return (infinityResult, denormalFlag);
        }

        if (IsZero(a) && IsZero(b))
        {
            // IEEE 754's own sign-of-zero rule: operands of the same sign
            // that are both that zero keep that sign; operands of opposite
            // sign that cancel to zero are +0 in every rounding-direction
            // attribute except round-toward-negative, where they are -0.
            bool sameSign = SignBit(a) == SignBit(b);
            uint result = sameSign
                ? (SignBit(a) ? NegativeZero : PositiveZero)
                : (rc == RoundingMode.Down ? NegativeZero : PositiveZero);
            return (result, denormalFlag);
        }

        return AddFinite(a, b, rc, flushToZero, underflowMasked, denormalFlag);
    }

    /// <summary>
    /// The actual bit-level addition, once NaN, infinity and the
    /// both-zero special case are out of the way: this is deliberately
    /// <em>not</em> ".NET double addition of the two widened singles" --
    /// measuring that approach against the host oracle
    /// (tools/NativeOracle) showed it silently loses the Precision and
    /// Underflow flags whenever the two operands' exponents differ by more
    /// than a double's own 53-bit mantissa can bridge (e.g. the smallest
    /// denormal added to -FLT_MAX: a double already rounds that sum back to
    /// exactly -FLT_MAX before this code ever sees it, so the fact that the
    /// true sum was not exactly representable is gone). Single's own
    /// exponent range spans about 276 powers of two, far past what a
    /// double's 53-bit significand can bridge, so addition -- unlike
    /// multiply, divide and square root, none of which combine two
    /// independently-scaled quantities this way -- needs its own aligned,
    /// sticky-bit-tracked integer arithmetic instead of a wider hardware
    /// float.
    /// </summary>
    private static (uint, FpFlags) AddFinite(uint a, uint b, RoundingMode rc, bool flushToZero, bool underflowMasked, FpFlags accumulated)
    {
        bool signA = SignBit(a);
        bool signB = SignBit(b);
        (int expA, uint sigA) = Decompose(a);
        (int expB, uint sigB) = Decompose(b);

        // Align both significands on the larger exponent's binary point, in
        // a 128-bit fixed-point frame with 96 fractional bits of headroom:
        // a normal's own leading bit sits at bit 119 once shifted, so even
        // the widest possible gap (a denormal against FLT_MAX) still leaves
        // every bit that could influence a 24-bit rounded result inside the
        // frame, and ShiftRightSticky folds anything shifted past it into a
        // single sticky bit rather than discarding it silently.
        int exponent = Math.Max(expA, expB);
        UInt128 wideA = ShiftRightSticky(((UInt128)sigA) << 96, exponent - expA);
        UInt128 wideB = ShiftRightSticky(((UInt128)sigB) << 96, exponent - expB);

        UInt128 sum;
        bool resultNegative;
        if (signA == signB)
        {
            sum = wideA + wideB;
            resultNegative = signA;
        }
        else if (wideA >= wideB)
        {
            sum = wideA - wideB;
            resultNegative = signA;
        }
        else
        {
            sum = wideB - wideA;
            resultNegative = signB;
        }

        if (sum == UInt128.Zero)
        {
            // Opposite-sign operands that cancelled exactly (equal
            // magnitude, unequal sign): the same rounding-direction rule as
            // the both-zero-operand case above.
            return (rc == RoundingMode.Down ? NegativeZero : PositiveZero, accumulated);
        }

        return PackNormalized(resultNegative, exponent, sum, topBitIndex: WideFrameTopBitIndex, rc, flushToZero, underflowMasked, accumulated);
    }

    /// <summary>
    /// Normalizes a significand so its leading 1 sits at <paramref name="topBitIndex"/>
    /// (shifting right, sticky-folding what falls off, for a same-sign
    /// carry; left, exactly, for cancellation -- stopping early rather than
    /// pushing the exponent below -126, which is exactly how a denormal
    /// result is represented: fewer than 24 significant bits, biased
    /// exponent 0), rounds the top 24 bits by <paramref name="rc"/> with a
    /// real guard/round/sticky triple, and packs the result -- handling a
    /// rounding carry back into 25 bits, and an exponent that overflows the
    /// format either way (SDM/IEEE 754: round-to-nearest and the
    /// rounding-toward-the-result's-own-sign-infinity modes overflow to
    /// infinity; the other two clamp to the largest finite value, still
    /// setting Precision alongside Overflow since the true result was
    /// never representable either way). <paramref name="underflowMasked"/>
    /// is MXCSR's own UM bit (a P4.1 review finding, plans/specs/sse.md):
    /// with UM unmasked, Underflow is signalled whenever the result is tiny
    /// at all, exact or not, and flush-to-zero never applies -- only when
    /// UM is masked does the older "tiny and inexact, or flushed" rule
    /// (unchanged from before this finding) govern the sticky flag.
    /// </summary>
    private static (uint, FpFlags) PackNormalized(bool negative, int exponent, UInt128 significandBits, int topBitIndex, RoundingMode rc, bool flushToZero, bool underflowMasked, FpFlags accumulated)
    {
        int top = TopBitIndex(significandBits);
        int shift = top - topBitIndex;

        if (shift > 0)
        {
            significandBits = ShiftRightSticky(significandBits, shift);
            exponent += shift;
        }
        else if (shift < 0)
        {
            int leftShift = -shift;
            int roomAboveMinimum = exponent - MinNormalExponent;
            int actualShift = Math.Min(leftShift, Math.Max(roomAboveMinimum, 0));
            significandBits <<= actualShift;
            exponent -= actualShift;
        }

        // The top 24 bits (bits topBitIndex..topBitIndex-23) are the
        // candidate mantissa; the next bit down is the round bit, and
        // whether anything nonzero remains below it is the sticky bit.
        int mantissaShift = topBitIndex - 23;
        uint mantissa = (uint)(significandBits >> mantissaShift);
        UInt128 remainderMask = ((UInt128)1 << mantissaShift) - 1;
        UInt128 remainder = significandBits & remainderMask;
        bool roundBit = (remainder >> (mantissaShift - 1) & 1) != 0;
        bool stickyBit = (remainder & (((UInt128)1 << (mantissaShift - 1)) - 1)) != UInt128.Zero;

        bool inexact = roundBit || stickyBit;
        bool roundUp = rc switch
        {
            RoundingMode.Nearest => roundBit && (stickyBit || (mantissa & 1) != 0),
            RoundingMode.Up => inexact && !negative,
            RoundingMode.Down => inexact && negative,
            RoundingMode.TowardZero => false,
            _ => false,
        };

        if (roundUp)
        {
            mantissa++;
            if (mantissa == 0x0100_0000u)   // 25 bits: the rounding carried into one more bit.
            {
                mantissa >>= 1;
                exponent++;
            }
        }

        FpFlags flags = accumulated;
        if (inexact) flags |= FpFlags.Precision;

        bool isNormalized = (mantissa & 0x0080_0000u) != 0;
        bool tiny = !isNormalized || exponent < MinNormalExponent;
        bool flushApplies = flushToZero && underflowMasked;
        if (tiny && (!underflowMasked || inexact || flushApplies)) flags |= FpFlags.Underflow;

        if (tiny && flushApplies)
        {
            return (negative ? NegativeZero : PositiveZero, flags | FpFlags.Precision);
        }

        if (exponent > MaxNormalExponent)
        {
            flags |= FpFlags.Overflow;
            bool roundToInfinity = rc switch
            {
                RoundingMode.Nearest => true,
                RoundingMode.Up => !negative,
                RoundingMode.Down => negative,
                _ => false,
            };

            uint overflowResult = roundToInfinity
                ? (negative ? NegativeInfinity : PositiveInfinity)
                : (negative ? (NegativeInfinity - 1) : (PositiveInfinity - 1));   // largest finite, same sign.
            // Overflow always carries Precision too, clamped to infinity or
            // not: the true result was never representable either way
            // (measured against the host oracle -- FLT_MAX+FLT_MAX rounding
            // to infinity still sets PE).
            return (overflowResult, flags | FpFlags.Precision);
        }

        uint signBit = negative ? SignMask : 0;
        uint biasedExponent = isNormalized ? (uint)(exponent + 127) : 0u;
        uint storedMantissa = mantissa & MantissaMask;
        return (signBit | (biasedExponent << 23) | storedMantissa, flags);
    }

    private const int MinNormalExponent = -126;
    private const int MaxNormalExponent = 127;

    /// <summary>
    /// A finite single's (exponent, 24-bit significand) pair, on a common
    /// scale: value = significand * 2^(exponent-23). Normals get their
    /// implicit leading 1 made explicit; denormals (and zero) use the same
    /// -126 exponent a normal's smallest exponent does, so the two
    /// decompositions already share one binary-point grid with no special
    /// case needed to combine them.
    /// </summary>
    private static (int Exponent, uint Significand) Decompose(uint bits)
    {
        uint biasedExponent = (bits & ExponentMask) >> 23;
        uint mantissa = bits & MantissaMask;
        return biasedExponent == 0
            ? (MinNormalExponent, mantissa)
            : ((int)biasedExponent - 127, mantissa | 0x0080_0000u);
    }

    /// <summary>internal (not private): DoubleFloat.cs's own wide-significand arithmetic shares this exact shift-and-fold-to-sticky routine rather than duplicating it (a P4.2 shared helper).</summary>
    internal static UInt128 ShiftRightSticky(UInt128 value, int shift)
    {
        if (shift <= 0) return value;
        if (shift >= 128) return value == UInt128.Zero ? UInt128.Zero : UInt128.One;

        UInt128 mask = (((UInt128)1) << shift) - 1;
        UInt128 shiftedOut = value & mask;
        UInt128 result = value >> shift;
        return shiftedOut != UInt128.Zero ? result | 1 : result;
    }

    /// <summary>internal (not private): shared with DoubleFloat.cs, see <see cref="ShiftRightSticky"/>'s own remark.</summary>
    internal static int TopBitIndex(UInt128 value)
    {
        ulong hi = (ulong)(value >> 64);
        if (hi != 0) return 64 + 63 - System.Numerics.BitOperations.LeadingZeroCount(hi);
        ulong lo = (ulong)value;
        return 63 - System.Numerics.BitOperations.LeadingZeroCount(lo);
    }

    public static (uint Result, FpFlags Flags) Multiply(uint a, uint b, RoundingMode rc, bool flushToZero, bool underflowMasked, bool daz = false)
    {
        a = ApplyDaz(a, daz);
        b = ApplyDaz(b, daz);
        if (IsNaN(a) || IsNaN(b)) return PropagateNaN(a, b);

        bool resultNegative = SignBit(a) ^ SignBit(b);

        // Denormal is suppressed only on the genuinely-invalid 0*inf
        // shortcut (which never really looks at either operand's class
        // beyond "is it zero or infinite" to know it is invalid); the
        // ordinary "one operand is infinity" and "one operand is zero"
        // shortcuts still report the *other* operand's denormal-ness, the
        // same as AddOrSubtract's own infinity/zero shortcuts.
        if ((IsZero(a) && IsInfinity(b)) || (IsInfinity(a) && IsZero(b)))
        {
            return (DefaultQNaN, FpFlags.Invalid);
        }

        FpFlags denormalFlag = DenormalFlagOf(a, b);

        if (IsInfinity(a) || IsInfinity(b))
        {
            return (resultNegative ? NegativeInfinity : PositiveInfinity, denormalFlag);
        }

        if (IsZero(a) || IsZero(b))
        {
            return (resultNegative ? NegativeZero : PositiveZero, denormalFlag);
        }

        double exact = (double)ToFloat(a) * (double)ToFloat(b);
        return RoundDoubleToSingle(exact, rc, flushToZero, underflowMasked, denormalFlag);
    }

    public static (uint Result, FpFlags Flags) Divide(uint a, uint b, RoundingMode rc, bool flushToZero, bool underflowMasked, bool daz = false)
    {
        a = ApplyDaz(a, daz);
        b = ApplyDaz(b, daz);
        if (IsNaN(a) || IsNaN(b)) return PropagateNaN(a, b);

        bool resultNegative = SignBit(a) ^ SignBit(b);

        // As Multiply above: only the genuinely-invalid 0/0 and inf/inf
        // shortcuts leave Denormal clear.
        if (IsZero(a) && IsZero(b)) return (DefaultQNaN, FpFlags.Invalid);
        if (IsInfinity(a) && IsInfinity(b)) return (DefaultQNaN, FpFlags.Invalid);

        FpFlags denormalFlag = DenormalFlagOf(a, b);

        // inf/finite (inf/inf already handled above) has to be checked
        // before the zero-divisor case below: measuring DIVPS against the
        // host oracle shows infinity divided by zero is just infinity, no
        // DivideByZero flag at all -- the flag is specifically for a
        // finite nonzero dividend, not for "any zero divisor".
        if (IsInfinity(a))
        {
            return (resultNegative ? NegativeInfinity : PositiveInfinity, denormalFlag);
        }

        if (IsZero(b))   // x/0, x finite and nonzero (0/0, inf/0 both already handled above).
        {
            // Unlike every other shortcut in this function, DivideByZero's
            // own path measures as never reporting Denormal, even with a
            // denormal dividend -- an asymmetry against IsInfinity(a) and
            // IsInfinity(b)/IsZero(a) just below, but that is what the host
            // oracle shows, case after case.
            return (resultNegative ? NegativeInfinity : PositiveInfinity, FpFlags.DivideByZero);
        }

        if (IsInfinity(b) || IsZero(a))   // finite/inf, or 0/finite (0/0 already handled above).
        {
            return (resultNegative ? NegativeZero : PositiveZero, denormalFlag);
        }

        double exact = (double)ToFloat(a) / (double)ToFloat(b);
        return RoundDoubleToSingle(exact, rc, flushToZero, underflowMasked, denormalFlag);
    }

    public static (uint Result, FpFlags Flags) Sqrt(uint a, RoundingMode rc, bool flushToZero, bool underflowMasked, bool daz = false)
    {
        a = ApplyDaz(a, daz);
        if (IsNaN(a)) return PropagateNaN(a, a);

        // Denormal is reported only on the path that actually computes a
        // square root; the zero/negative/infinity shortcuts leave it clear
        // (the same rule measured for MAXPS/DIVPS/MULPS above).
        if (IsZero(a)) return (a, FpFlags.None);                                  // sqrt(+-0) = +-0 exactly.
        if (SignBit(a)) return (DefaultQNaN, FpFlags.Invalid);                    // negative, nonzero.
        if (IsInfinity(a)) return (PositiveInfinity, FpFlags.None);

        double exact = Math.Sqrt((double)ToFloat(a));
        return RoundDoubleToSingle(exact, rc, flushToZero, underflowMasked, DenormalFlagOf(a));
    }

    /// <summary>
    /// Rounds an exact (or, for divide/sqrt, correctly-rounded-to-53-bits)
    /// double result down to single, at the requested rounding mode, and
    /// works out every flag from that rounding step -- Overflow and
    /// Underflow (tininess) included.
    ///
    /// This replaces an earlier version that compared the <em>exact</em>
    /// double value against FLT_MAX/the smallest normal to decide those two
    /// flags. Measuring MULSS/DIVSS against the host oracle (the P4.1
    /// review's overflow and tininess vectors, kept in
    /// SseReviewFindingsTests.cs) showed that is wrong at both ends of the
    /// exponent range:
    /// <list type="bullet">
    /// <item>Overflow has to be decided from the <em>rounded</em>, RC-aware
    /// result, not the exact one: MULSS 5FB504F3h x 5F3504F3h rounds to
    /// exactly FLT_MAX under round-toward-zero (only Precision, no
    /// Overflow) even though the exact product exceeds FLT_MAX, and only
    /// carries into Overflow at all under round-to-nearest or
    /// round-toward+infinity, where the rounding step itself pushes the
    /// result past FLT_MAX into infinity.</item>
    /// <item>Underflow (tininess) is decided by IEEE 754's "round to the
    /// destination precision with an unbounded exponent range" step, a
    /// <em>different</em> rounding than the one that decides the actual
    /// stored bits when the result needs the denormal grid: MULSS
    /// 3F7FFFFFh x 00800000h rounds, at 24 bits with its own natural
    /// (unbounded) exponent, to a value whose exponent already sits one
    /// below the smallest normal -- tiny, even though re-rounding that same
    /// value at the *reduced* precision the denormal grid actually offers
    /// happens to carry it back up to exactly the smallest normal
    /// (00800000h) with a round-to-even tie-break. The host still raises
    /// Underflow there, so tininess is settled here, before that second
    /// rounding ever runs.</item>
    /// </list>
    /// Both are implemented directly on the double's own (exponent,
    /// 53-bit significand) pair -- <see cref="DecomposeDouble"/> and
    /// <see cref="RoundSignificandTo24Bits"/> -- rather than reusing
    /// <see cref="PackNormalized"/>: that method's own left-shift-and-clamp
    /// step assumes its caller's exponent already sits at or above
    /// <see cref="MinNormalExponent"/> (true for every operand
    /// <see cref="AddFinite"/> ever decomposes), and folding tininess and
    /// the denormal-storage rounding into that same single pass is exactly
    /// what produces the wrong answer for the tie-to-normal case above.
    /// <paramref name="underflowMasked"/> is MXCSR's own UM bit
    /// (<see cref="PackNormalized"/>'s own remark on it): with UM
    /// unmasked, Underflow is signalled for any tiny result, exact or not,
    /// and flush-to-zero never applies.
    /// </summary>
    private static (uint Result, FpFlags Flags) RoundDoubleToSingle(double exact, RoundingMode rc, bool flushToZero, bool underflowMasked, FpFlags accumulated)
    {
        if (exact == 0.0)
        {
            return (double.IsNegative(exact) ? NegativeZero : PositiveZero, accumulated);
        }

        bool negative = double.IsNegative(exact);
        (int exponent, UInt128 significand) = DecomposeDouble(exact);
        return RoundWideSignificandToSingle(negative, exponent, significand, rc, flushToZero, underflowMasked, accumulated);
    }

    /// <summary>
    /// The reusable tail of <see cref="RoundDoubleToSingle"/>: given a
    /// nonzero finite value already expressed as (<paramref name="exponent"/>,
    /// <paramref name="significand"/>) on the same <see cref="WideFrameTopBitIndex"/>
    /// scale <see cref="DecomposeDouble"/> and <see cref="AddFinite"/> both
    /// use (value = significand * 2^(exponent - <see cref="WideFrameTopBitIndex"/>),
    /// significand's own top bit at or below that index), rounds it to
    /// single precision with the overflow/tininess rule the type summary
    /// above documents, deciding both from the <em>natural</em>, unbounded-
    /// exponent rounding before ever consulting the format's actual limits.
    /// A shared helper (P4.2, plans/specs/sse2.md): <c>Cpu.Sse2.Arithmetic.cs</c>'s
    /// own CVTPD2PS/CVTSD2SS call this directly with a double bit pattern's
    /// own (possibly denormal, unlike every caller <see cref="DecomposeDouble"/>
    /// itself ever sees) significand already decomposed and placed on this
    /// same scale, rather than duplicating this rounding tail a second time.
    /// </summary>
    internal static (uint Result, FpFlags Flags) RoundWideSignificandToSingle(bool negative, int exponent, UInt128 significand, RoundingMode rc, bool flushToZero, bool underflowMasked, FpFlags accumulated)
    {
        // Stage 1: round the double's own 53-bit significand to 24 bits at
        // its natural exponent, with no regard for whether that exponent
        // even fits the single format -- IEEE 754's own "as if the exponent
        // range were unbounded" step, which alone decides Underflow and
        // Overflow (the type summary above).
        (uint naturalMantissa, int naturalExponent, bool naturalInexact) = RoundSignificandTo24Bits(significand, exponent, rc, negative);

        bool tiny = naturalExponent < MinNormalExponent;
        bool overflow = naturalExponent > MaxNormalExponent;
        FpFlags flags = accumulated;

        if (overflow)
        {
            flags |= FpFlags.Overflow | FpFlags.Precision;
            bool roundToInfinity = rc switch
            {
                RoundingMode.Nearest => true,
                RoundingMode.Up => !negative,
                RoundingMode.Down => negative,
                _ => false,
            };
            uint overflowResult = roundToInfinity
                ? (negative ? NegativeInfinity : PositiveInfinity)
                : (negative ? (NegativeInfinity - 1) : (PositiveInfinity - 1));   // largest finite, same sign.
            return (overflowResult, flags);
        }

        if (!tiny)
        {
            // The natural, unbounded-exponent rounding above already *is*
            // the final stored value: its exponent fits the format, so no
            // further rounding is needed.
            if (naturalInexact) flags |= FpFlags.Precision;
            return (PackSingle(negative, (uint)(naturalExponent + 127), naturalMantissa), flags);
        }

        // Tiny: the true value still needs a second rounding, at the
        // reduced precision the denormal grid actually offers at
        // MinNormalExponent -- redone from the original 53-bit significand
        // (not from naturalMantissa above), so this is one rounding of the
        // true value, not a second rounding of an already-rounded one.
        int denormalShift = MinNormalExponent - exponent;
        UInt128 denormalFrame = ShiftRightSticky(significand, denormalShift);
        (uint denormalMantissa, int denormalExponent, bool denormalInexact) = RoundSignificandTo24Bits(denormalFrame, MinNormalExponent, rc, negative);

        bool flushApplies = flushToZero && underflowMasked;
        if (!underflowMasked || denormalInexact || flushApplies) flags |= FpFlags.Underflow;

        if (flushApplies)
        {
            return (negative ? NegativeZero : PositiveZero, flags | FpFlags.Precision);
        }

        if (denormalInexact) flags |= FpFlags.Precision;

        // A round-to-even tie can still carry this back out of the
        // denormal range into exactly the smallest normal (the type
        // summary's own example): denormalMantissa's own top bit says
        // which one actually happened.
        bool denormalNormalized = (denormalMantissa & 0x0080_0000u) != 0;
        uint storedBiasedExponent = denormalNormalized ? (uint)(denormalExponent + 127) : 0u;
        return (PackSingle(negative, storedBiasedExponent, denormalMantissa), flags);
    }

    /// <summary>
    /// A finite double's (exponent, 53-bit significand) pair, placed in the
    /// same 128-bit frame and at the same <see cref="WideFrameTopBitIndex"/>
    /// reference point <see cref="AddFinite"/> already uses for singles:
    /// value = significand (as an integer) * 2^(exponent - <see cref="WideFrameTopBitIndex"/>).
    /// Only ever called on a normal, nonzero, finite double -- every
    /// caller's own exact/correctly-rounded double result is far inside
    /// double's normal range for any single-precision-sized operand, so a
    /// double subnormal or a biased exponent of 0 never actually reaches
    /// here.
    /// </summary>
    private static (int Exponent, UInt128 Significand) DecomposeDouble(double value)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        int biasedExponent = (int)((bits >> 52) & 0x7FFL);
        ulong mantissa53 = ((ulong)bits & 0x000F_FFFF_FFFF_FFFFul) | 0x0010_0000_0000_0000ul;   // the implicit leading bit, made explicit.
        int exponent = biasedExponent - 1023;

        const int doubleMantissaTopBit = 52;
        UInt128 significand = ((UInt128)mantissa53) << (WideFrameTopBitIndex - doubleMantissaTopBit);
        return (exponent, significand);
    }

    /// <summary>
    /// Rounds whatever sits at bits <see cref="WideFrameTopBitIndex"/> down
    /// to <c>WideFrameTopBitIndex - 23</c> in <paramref name="significandBits"/>
    /// to a 24-bit mantissa, by <paramref name="rc"/>, with a real
    /// guard/round/sticky triple -- the same rounding tail
    /// <see cref="PackNormalized"/> already uses, factored out so
    /// <see cref="RoundDoubleToSingle"/> can call it twice (natural, then
    /// denormal) without the initial normalize-and-clamp step that method's
    /// own single callers need and this one does not: every significand
    /// handed to this function already has its top bit at exactly
    /// <see cref="WideFrameTopBitIndex"/> or below (never above), so there
    /// is never a right-shift-to-normalize to do, only the rounding itself.
    /// A carry into the 25th bit is folded back into 24 with the exponent
    /// incremented, exactly as <see cref="PackNormalized"/> does.
    /// </summary>
    private static (uint Mantissa, int Exponent, bool Inexact) RoundSignificandTo24Bits(UInt128 significandBits, int exponent, RoundingMode rc, bool negative)
    {
        const int mantissaShift = WideFrameTopBitIndex - 23;
        uint mantissa = (uint)(significandBits >> mantissaShift);
        UInt128 remainderMask = ((UInt128)1 << mantissaShift) - 1;
        UInt128 remainder = significandBits & remainderMask;
        bool roundBit = (remainder >> (mantissaShift - 1) & 1) != 0;
        bool stickyBit = (remainder & (((UInt128)1 << (mantissaShift - 1)) - 1)) != UInt128.Zero;
        bool inexact = roundBit || stickyBit;

        bool roundUp = rc switch
        {
            RoundingMode.Nearest => roundBit && (stickyBit || (mantissa & 1) != 0),
            RoundingMode.Up => inexact && !negative,
            RoundingMode.Down => inexact && negative,
            RoundingMode.TowardZero => false,
            _ => false,
        };

        if (roundUp)
        {
            mantissa++;
            if (mantissa == 0x0100_0000u)   // 25 bits: the rounding carried into one more bit.
            {
                mantissa >>= 1;
                exponent++;
            }
        }

        return (mantissa, exponent, inexact);
    }

    /// <summary>Assembles a raw single from its parts, dropping the mantissa's implicit leading bit -- <paramref name="mantissa24"/> carries it only when <paramref name="biasedExponent"/> is nonzero (a normal); a denormal's caller passes biasedExponent 0 and a mantissa whose top bit is already clear.</summary>
    private static uint PackSingle(bool negative, uint biasedExponent, uint mantissa24)
    {
        uint signBit = negative ? SignMask : 0;
        uint storedMantissa = mantissa24 & MantissaMask;
        return signBit | (biasedExponent << 23) | storedMantissa;
    }

    // --- comparisons -----------------------------------------------------

    /// <summary>
    /// COMISS/UCOMISS's own ZF:PF:CF encoding (plans/specs/sse.md §4): 000
    /// greater, 001 less, 100 equal, 111 unordered. <paramref name="unorderedOnlySignaling"/>
    /// is true for UCOMISS (only a signaling NaN raises Invalid), false for
    /// COMISS (any NaN does).
    /// </summary>
    public static (bool Zf, bool Pf, bool Cf, FpFlags Flags) CompareForFlags(uint a, uint b, bool unorderedOnlySignaling, bool daz = false)
    {
        a = ApplyDaz(a, daz);
        b = ApplyDaz(b, daz);
        bool eitherNaN = IsNaN(a) || IsNaN(b);
        bool eitherSignaling = IsSignalingNaN(a) || IsSignalingNaN(b);
        FpFlags invalidFlag = (eitherSignaling || (eitherNaN && !unorderedOnlySignaling)) ? FpFlags.Invalid : FpFlags.None;

        // Denormal is suppressed on the NaN path, exactly like MAXPS/MINPS
        // measure (AddOrSubtract's own remark): only the ordinary numeric
        // comparison below reports it.
        if (eitherNaN) return (true, true, true, invalidFlag);

        FpFlags flags = invalidFlag | DenormalFlagOf(a, b);
        float fa = ToFloat(a), fb = ToFloat(b);
        if (fa < fb) return (false, false, true, flags);
        if (fa > fb) return (false, false, false, flags);
        return (true, false, false, flags);
    }

    /// <summary>
    /// CMPPS/CMPSS's eight predicates (plans/specs/sse.md §4): 0 EQ, 1 LT, 2
    /// LE, 3 UNORD, 4 NEQ, 5 NLT, 6 NLE, 7 ORD. LT/LE/NLT/NLE signal Invalid
    /// on a quiet NaN too, not only a signaling one; the rest only on a
    /// signaling NaN. An unordered comparison answers false for EQ/LT/LE/
    /// ORD and true for UNORD/NEQ/NLT/NLE.
    /// </summary>
    public static (uint Result, FpFlags Flags) ComparePredicate(uint a, uint b, int predicate, bool daz = false)
    {
        a = ApplyDaz(a, daz);
        b = ApplyDaz(b, daz);
        bool eitherNaN = IsNaN(a) || IsNaN(b);
        bool eitherSignaling = IsSignalingNaN(a) || IsSignalingNaN(b);
        bool quietAlsoSignals = predicate is 1 or 2 or 5 or 6;
        FpFlags flags = (eitherSignaling || (eitherNaN && quietAlsoSignals)) ? FpFlags.Invalid : FpFlags.None;

        // Denormal is suppressed on the NaN path (measured, like MAXPS/
        // MINPS/COMISS all show -- AddOrSubtract's own remark); only the
        // ordinary numeric comparison below reports it.
        bool trueResult;
        if (eitherNaN)
        {
            trueResult = predicate is 3 or 4 or 5 or 6;
        }
        else
        {
            flags |= DenormalFlagOf(a, b);
            float fa = ToFloat(a), fb = ToFloat(b);
            trueResult = predicate switch
            {
                0 => fa == fb,
                1 => fa < fb,
                2 => fa <= fb,
                3 => false,
                4 => fa != fb,
                5 => !(fa < fb),
                6 => !(fa <= fb),
                7 => true,
                _ => false,
            };
        }

        return (trueResult ? 0xFFFF_FFFFu : 0u, flags);
    }

    // --- MAX/MIN -----------------------------------------------------------

    /// <summary>MAXPS/MAXSS: DEST &gt; SRC ? DEST : SRC, and the source wins on a NaN in either operand or on equal operands, +0/-0 included (plans/specs/sse.md §4).</summary>
    public static (uint Result, FpFlags Flags) Max(uint destination, uint source, bool daz = false) => MaxOrMin(destination, source, wantGreater: true, daz);

    /// <summary>MINPS/MINSS: the same rule with DEST &lt; SRC.</summary>
    public static (uint Result, FpFlags Flags) Min(uint destination, uint source, bool daz = false) => MaxOrMin(destination, source, wantGreater: false, daz);

    private static (uint, FpFlags) MaxOrMin(uint destination, uint source, bool wantGreater, bool daz)
    {
        destination = ApplyDaz(destination, daz);
        source = ApplyDaz(source, daz);
        // Measuring MAXPS/MINPS against the host oracle (dest a quiet NaN,
        // source an ordinary float, RC varied) shows two things the SDM's
        // usual "only SNaN signals" text does not: Invalid is set for any
        // NaN operand at all, quiet included; and -- like every other
        // shortcut this file measured (AddOrSubtract's own remark) --
        // Denormal stays clear on this NaN path even when the other
        // operand genuinely is one.
        bool eitherNaN = IsNaN(destination) || IsNaN(source);
        if (eitherNaN) return (source, FpFlags.Invalid);

        FpFlags flags = DenormalFlagOf(destination, source);
        float fd = ToFloat(destination), fs = ToFloat(source);
        if (fd == fs) return (source, flags);

        bool destinationWins = wantGreater ? fd > fs : fd < fs;
        return (destinationWins ? destination : source, flags);
    }

    // --- conversions ---------------------------------------------------------

    /// <summary>
    /// CVTSI2SS/CVTPI2PS: a 32-bit signed integer is always exact and
    /// finite -- far too large in magnitude ever to be tiny, far too small
    /// ever to overflow a single -- so this is nothing but
    /// <see cref="RoundDoubleToSingle"/> with Underflow structurally
    /// unreachable; <paramref name="underflowMasked"/> is threaded through
    /// only so every caller of that shared rounding tail carries the same
    /// four parameters.
    /// </summary>
    public static (uint Result, FpFlags Flags) ConvertInt32ToSingle(int value, RoundingMode rc, bool flushToZero, bool underflowMasked)
        => RoundDoubleToSingle(value, rc, flushToZero, underflowMasked, FpFlags.None);

    /// <summary>
    /// CVTSS2SI/CVTPS2PI (<paramref name="truncate"/> false, rounds by
    /// <paramref name="rc"/>) and CVTTSS2SI/CVTTPS2PI (<paramref name="truncate"/>
    /// true, always chops): a NaN, an infinity or an in-range value that
    /// rounds outside [-2^31, 2^31-1] all produce the integer indefinite
    /// 80000000h and Invalid, masked or not (plans/specs/sse.md §4).
    /// </summary>
    public static (int Result, FpFlags Flags) ConvertToInt32(uint bits, RoundingMode rc, bool truncate, bool daz = false)
    {
        const int IntegerIndefinite = unchecked((int)0x8000_0000);

        bits = ApplyDaz(bits, daz);
        if (IsNaN(bits) || IsInfinity(bits))
        {
            return (IntegerIndefinite, FpFlags.Invalid);
        }

        double exactValue = ToFloat(bits);
        // Unlike every arithmetic instruction above, CVT(T)SS2SI/CVT(T)PS2PI
        // measures as never reporting Denormal at all, a denormal source
        // included.
        FpFlags flags = FpFlags.None;

        double rounded = truncate ? Math.Truncate(exactValue) : RoundToIntegerDouble(exactValue, rc);

        if (rounded < -2147483648.0 || rounded > 2147483647.0)
        {
            return (IntegerIndefinite, flags | FpFlags.Invalid);
        }

        if (rounded != exactValue) flags |= FpFlags.Precision;

        return ((int)rounded, flags);
    }

    private static double RoundToIntegerDouble(double value, RoundingMode rc) => rc switch
    {
        RoundingMode.Down => Math.Floor(value),
        RoundingMode.Up => Math.Ceiling(value),
        RoundingMode.TowardZero => Math.Truncate(value),
        _ => Math.Round(value, MidpointRounding.ToEven),
    };

    // --- RCPPS/RCPSS, RSQRTPS/RSQRTSS ---------------------------------------
    //
    // Neither instruction's approximation algorithm is architecturally
    // defined, and the SDM documents them as raising no floating-point
    // exception at all (a real Pentium III's own microcode table has
    // nothing behind IE/DE/etc. for these two). Aurora's approximation is
    // its own choice (coordinator decision, plans/specs/sse.md): compute the
    // correctly-rounded single result and then force the mantissa's low 11
    // bits to zero, keeping only its top 12 bits. Truncating k mantissa
    // bits off a normalized value introduces at most a 2^-k relative error,
    // so this keeps the documented 1.5*2^-12 bound with headroom, is
    // trivially deterministic and reproducible bit for bit, and needs no
    // lookup table.

    public static (uint Result, FpFlags Flags) ReciprocalApprox(uint bits)
    {
        if (IsNaN(bits)) return (IsSignalingNaN(bits) ? Quiet(bits) : bits, FpFlags.None);
        bits = ZeroOutADenormalSource(bits);
        if (IsZero(bits)) return (SignBit(bits) ? NegativeInfinity : PositiveInfinity, FpFlags.None);
        if (IsInfinity(bits)) return (SignBit(bits) ? NegativeZero : PositiveZero, FpFlags.None);

        double exact = 1.0 / (double)ToFloat(bits);
        return (TruncateApproximation(exact), FpFlags.None);
    }

    public static (uint Result, FpFlags Flags) ReciprocalSqrtApprox(uint bits)
    {
        if (IsNaN(bits)) return (IsSignalingNaN(bits) ? Quiet(bits) : bits, FpFlags.None);
        bits = ZeroOutADenormalSource(bits);
        if (IsZero(bits)) return (SignBit(bits) ? NegativeInfinity : PositiveInfinity, FpFlags.None);
        if (SignBit(bits)) return (DefaultQNaN, FpFlags.None);      // negative, nonzero, not NaN.
        if (IsInfinity(bits)) return (PositiveZero, FpFlags.None);

        double exact = 1.0 / Math.Sqrt((double)ToFloat(bits));
        return (TruncateApproximation(exact), FpFlags.None);
    }

    /// <summary>
    /// A P4.1 review finding, confirmed on the host (RCPSS 00400000h -&gt;
    /// 7F800000h -- exactly rcp(+0), not the finite ~2^127 that a literal
    /// 1/x of that denormal's own value would give; RSQRTSS 00000001h -&gt;
    /// 7F800000h and 80000001h -&gt; FF800000h, likewise rsqrt(+-0)): both
    /// approximations treat a denormal source as a zero of the same sign,
    /// before any of the ordinary zero/infinity/negative special cases
    /// below even run.
    /// </summary>
    private static uint ZeroOutADenormalSource(uint bits) => IsDenormal(bits) ? (SignBit(bits) ? NegativeZero : PositiveZero) : bits;

    private static uint TruncateApproximation(double exact)
    {
        uint bits = FromFloat((float)exact);
        if (IsNaN(bits) || IsInfinity(bits) || IsZero(bits)) return bits;

        // A P4.1 review finding, confirmed on the host (RCPSS 7F000000h --
        // reciprocal of a huge normal, landing in the denormal range --
        // -&gt; +0, not the denormal 1/2^127 would otherwise round to, and
        // the same whatever MXCSR.FZ says: plans/specs/sse.md documents
        // these two instructions as consulting no MXCSR control bit at
        // all): a tiny result flushes to zero outright rather than keeping
        // a denormal approximation.
        if (IsDenormal(bits)) return SignBit(bits) ? NegativeZero : PositiveZero;

        return bits & 0xFFFF_F800u;
    }
}
