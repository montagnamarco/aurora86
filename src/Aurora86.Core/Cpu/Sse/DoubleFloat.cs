namespace Aurora86.Core.Cpu.Sse;

/// <summary>
/// IEEE-754 double-precision arithmetic at the bit level, for SSE2
/// (work package P4.2, plans/specs/sse2.md). .NET's <see langword="double"/>
/// operators are round-to-nearest only and report no MXCSR-shaped exception
/// flags at all, and -- unlike <see cref="SingleFloat"/>, which can widen a
/// single into a hardware <see langword="double"/> and let .NET's own
/// correctly-rounded double arithmetic stand in for "infinite precision" --
/// there is no type wider than <see langword="double"/> to widen into here.
/// Every operation below is therefore genuine integer significand
/// arithmetic in a <see cref="UInt128"/> frame, mirroring
/// <see cref="SingleFloat"/>'s own design (aligned add/subtract with a real
/// guard/round/sticky triple, the same tininess-on-the-unbounded-exponent
/// convention, the same per-instruction NaN/zero/infinity shortcuts) at
/// double's own 53-bit significand width instead of single's 24 bits:
/// <list type="bullet">
/// <item>Add/Subtract: both significands aligned on a common binary point in
/// a 128-bit fixed-point frame (<see cref="WideFrameTopBitIndex"/>), exactly
/// as <see cref="SingleFloat.AddFinite"/> does.</item>
/// <item>Multiply: two 53-bit significands multiplied exactly as a single
/// <see cref="UInt128"/> product (up to 106 bits, comfortably inside 128) --
/// no rounding of its own, unlike single's own single-to-double widening
/// trick, because this <em>is</em> the widest arithmetic type available.</item>
/// <item>Divide: the dividend's own significand, shifted left just far
/// enough to pin the numerator's own top bit at bit 127 of the
/// <see cref="UInt128"/> register whatever the dividend's own bit length is
/// (a fixed 64-bit shift very nearly starves the quotient of precision
/// whenever the dividend is a small denormal against a near-maximum
/// divisor -- a measured bug, see <see cref="Divide"/>'s own remark), is
/// divided by the divisor with <see cref="UInt128"/>'s own native division
/// -- the quotient's own low bits are exact bits of the true,
/// infinite-precision quotient, and a nonzero remainder folds into the
/// quotient's own bit 0 as a sticky bit (long division to a guard/sticky
/// tail, exactly as the spec's own "digit recurrence to 53 + guard bits and
/// sticky" describes).</item>
/// <item>Square root: the classical binary digit-by-digit integer square
/// root (<see cref="IntegerSqrt"/>) applied to the significand scaled up by
/// a shift chosen the same dynamic way Divide's own is (see <see cref="Sqrt"/>'s
/// own remark), after adjusting for the operand's exponent parity (a
/// square root only has a clean integer exponent when doubled, so an odd
/// exponent is folded into one extra bit of the significand first) -- again
/// a digit-recurrence to a guard/sticky tail, not the double-rounding-safety
/// argument <see cref="SingleFloat.Sqrt"/> uses, since there is no wider
/// hardware type to compute an intermediate correctly-rounded result in.</item>
/// </list>
/// Every NaN-propagation order, MAX/MIN tie-break and DE-on-conversion rule
/// below was confirmed against the host oracle (tools/NativeOracle
/// <c>--probe</c>, this work package's own measurements) rather than assumed
/// from <see cref="SingleFloat"/>'s own measured rules, even though every one
/// of them turned out identical: ADDSD's own destination-always-wins NaN
/// rule, MAXSD/MINSD's own source-always-wins rule (NaN or equal operands
/// alike), CVT(T)SD2SI/CVT(T)PD2PI never reporting Denormal for a denormal
/// source, and -- the one genuine difference from the integer conversions --
/// CVTSD2SS/CVTPD2PS (an FP-to-FP width conversion, not FP-to-integer) *does*
/// report Denormal for a denormal source, confirmed by probing a source
/// whose value underflows entirely (Denormal, Underflow and Precision all
/// set, result flushed to zero) and one that converts exactly (Denormal
/// alone).
/// </summary>
public static class DoubleFloat
{
    public const ulong SignMask = 0x8000_0000_0000_0000ul;
    public const ulong ExponentMask = 0x7FF0_0000_0000_0000ul;
    public const ulong MantissaMask = 0x000F_FFFF_FFFF_FFFFul;
    private const ulong ImplicitBit = 0x0010_0000_0000_0000ul;   // bit 52: the leading 1 a normal's stored 52-bit mantissa leaves implicit.
    private const ulong QuietBit = 0x0008_0000_0000_0000ul;      // bit 51: the same relative position as SingleFloat's own QuietBit (bit 22 of 23).

    public const ulong PositiveZero = 0ul;
    public const ulong NegativeZero = SignMask;
    public const ulong PositiveInfinity = ExponentMask;
    public const ulong NegativeInfinity = SignMask | ExponentMask;

    /// <summary>The default QNaN a masked invalid operation with no NaN operand of its own to propagate produces (plans/specs/sse2.md §2's own coordinator decision).</summary>
    public const ulong DefaultQNaN = 0xFFF8_0000_0000_0000ul;

    private const int MinNormalExponent = -1022;
    private const int MaxNormalExponent = 1023;
    private const int Bias = 1023;

    /// <summary>
    /// Where a normalized 53-bit significand's own leading 1 (bit 52) sits
    /// inside the 128-bit fixed-point frame every arithmetic operation below
    /// aligns on: chosen with headroom on both sides -- 23 bits above for a
    /// same-sign carry (only ever 1 bit is needed; multiply's own raw
    /// 106-bit product is the widest anything here ever produces, and even
    /// that fits with room to spare), and 52 bits below for the
    /// guard/round/sticky region <see cref="SingleFloat.ShiftRightSticky"/>
    /// folds anything past into a single sticky bit -- unlike
    /// <see cref="SingleFloat.WideFrameTopBitIndex"/>, this is a frame of
    /// double's own, not the one that method's own single-to-double bridge
    /// (<see cref="ConvertDoubleToSingle"/> reuses that one instead,
    /// deliberately, to hand off into <see cref="SingleFloat.RoundWideSignificandToSingle"/>
    /// directly).
    /// </summary>
    private const int WideFrameTopBitIndex = 104;

    public static bool SignBit(ulong bits) => (bits & SignMask) != 0;
    public static bool IsNaN(ulong bits) => (bits & ExponentMask) == ExponentMask && (bits & MantissaMask) != 0;
    public static bool IsQuietNaN(ulong bits) => IsNaN(bits) && (bits & QuietBit) != 0;
    public static bool IsSignalingNaN(ulong bits) => IsNaN(bits) && (bits & QuietBit) == 0;
    public static bool IsInfinity(ulong bits) => (bits & ExponentMask) == ExponentMask && (bits & MantissaMask) == 0;
    public static bool IsZero(ulong bits) => (bits & ~SignMask) == 0;
    public static bool IsDenormal(ulong bits) => (bits & ExponentMask) == 0 && (bits & MantissaMask) != 0;
    public static ulong Quiet(ulong bits) => bits | QuietBit;

    public static double ToDouble(ulong bits) => BitConverter.Int64BitsToDouble(unchecked((long)bits));
    public static ulong FromDouble(double value) => unchecked((ulong)BitConverter.DoubleToInt64Bits(value));

    private static FpFlags DenormalFlagOf(ulong a) => IsDenormal(a) ? FpFlags.Denormal : FpFlags.None;
    private static FpFlags DenormalFlagOf(ulong a, ulong b) => (IsDenormal(a) || IsDenormal(b)) ? FpFlags.Denormal : FpFlags.None;

    /// <summary>MXCSR's DAZ bit: a denormal input treated as a signed zero, before anything else classifies it (see <see cref="SingleFloat.ApplyDaz"/>'s own remark; mirrored here rather than shared, since the two operate on different bit widths).</summary>
    private static ulong ApplyDaz(ulong bits, bool daz) => daz && IsDenormal(bits) ? (SignBit(bits) ? NegativeZero : PositiveZero) : bits;

    /// <summary>
    /// The NaN a binary double operation returns when at least one operand
    /// is a NaN: measured on the host (ADDSD, dest=QNaN/src=SNaN and
    /// dest=SNaN/src=QNaN, tools/NativeOracle --probe) to be the identical
    /// strict destination-wins rule <see cref="SingleFloat.PropagateNaN"/>
    /// measures for MULSS -- the destination (SRC1) wins whenever it is any
    /// NaN, signaling or not, and only when it is not a NaN at all does the
    /// source (SRC2) get a turn. Sets <see cref="FpFlags.Invalid"/> whenever
    /// either operand was signaling, independently of which one is actually
    /// returned.
    /// </summary>
    public static (ulong Result, FpFlags Flags) PropagateNaN(ulong destination, ulong source)
    {
        bool destSignaling = IsSignalingNaN(destination);
        bool srcSignaling = IsSignalingNaN(source);
        FpFlags flags = (destSignaling || srcSignaling) ? FpFlags.Invalid : FpFlags.None;

        if (IsNaN(destination)) return (Quiet(destination), flags);
        if (IsNaN(source)) return (Quiet(source), flags);
        return (DefaultQNaN, flags);
    }

    // --- add/subtract --------------------------------------------------------

    public static (ulong Result, FpFlags Flags) Add(ulong a, ulong b, RoundingMode rc, bool flushToZero, bool underflowMasked, bool daz = false) => AddOrSubtract(a, b, subtract: false, rc, flushToZero, underflowMasked, daz);
    public static (ulong Result, FpFlags Flags) Subtract(ulong a, ulong b, RoundingMode rc, bool flushToZero, bool underflowMasked, bool daz = false) => AddOrSubtract(a, b, subtract: true, rc, flushToZero, underflowMasked, daz);

    private static (ulong, FpFlags) AddOrSubtract(ulong a, ulong b, bool subtract, RoundingMode rc, bool flushToZero, bool underflowMasked, bool daz)
    {
        a = ApplyDaz(a, daz);
        b = ApplyDaz(b, daz);

        if (IsNaN(a) || IsNaN(b)) return PropagateNaN(a, b);

        if (subtract) b ^= SignMask;

        FpFlags denormalFlag = DenormalFlagOf(a, b);

        if (IsInfinity(a) || IsInfinity(b))
        {
            if (IsInfinity(a) && IsInfinity(b) && SignBit(a) != SignBit(b))
            {
                return (DefaultQNaN, FpFlags.Invalid);
            }

            ulong infinityResult = IsInfinity(a) ? a : b;
            return (infinityResult, denormalFlag);
        }

        if (IsZero(a) && IsZero(b))
        {
            bool sameSign = SignBit(a) == SignBit(b);
            ulong result = sameSign
                ? (SignBit(a) ? NegativeZero : PositiveZero)
                : (rc == RoundingMode.Down ? NegativeZero : PositiveZero);
            return (result, denormalFlag);
        }

        return AddFinite(a, b, rc, flushToZero, underflowMasked, denormalFlag);
    }

    private static (ulong, FpFlags) AddFinite(ulong a, ulong b, RoundingMode rc, bool flushToZero, bool underflowMasked, FpFlags accumulated)
    {
        bool signA = SignBit(a);
        bool signB = SignBit(b);
        (int expA, ulong sigA) = Decompose(a);
        (int expB, ulong sigB) = Decompose(b);

        int exponent = Math.Max(expA, expB);
        const int placementShift = WideFrameTopBitIndex - 52;   // 52: room to place either operand's own top bit (52) at WideFrameTopBitIndex.
        UInt128 wideA = SingleFloat.ShiftRightSticky(((UInt128)sigA) << placementShift, exponent - expA);
        UInt128 wideB = SingleFloat.ShiftRightSticky(((UInt128)sigB) << placementShift, exponent - expB);

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
            return (rc == RoundingMode.Down ? NegativeZero : PositiveZero, accumulated);
        }

        return PackNormalized(resultNegative, exponent, sum, rc, flushToZero, underflowMasked, accumulated);
    }

    /// <summary>
    /// Normalizes an arbitrary <see cref="UInt128"/> significand (its top bit
    /// need not already sit at <see cref="WideFrameTopBitIndex"/>: the
    /// invariant every caller keeps is "value = significandBits *
    /// 2^(exponent - WideFrameTopBitIndex)", exactly like
    /// <see cref="SingleFloat"/>'s own <c>PackNormalized</c>) to that
    /// reference point, rounds the top 53 bits by <paramref name="rc"/> with
    /// a real guard/round/sticky triple, and packs the result -- the same
    /// algorithm as <see cref="SingleFloat"/>'s, at double's own width and
    /// exponent range. Shared by Add/Subtract's own aligned sum, Multiply's
    /// raw product, Divide's quotient and Sqrt's integer root: none of those
    /// four needs its own copy of the renormalize-round-pack tail, only its
    /// own way of producing a (exponent, significandBits) pair under this one
    /// invariant.
    /// </summary>
    private static (ulong, FpFlags) PackNormalized(bool negative, int exponent, UInt128 significandBits, RoundingMode rc, bool flushToZero, bool underflowMasked, FpFlags accumulated)
    {
        // Stage 1: normalize the significand's own top bit to
        // WideFrameTopBitIndex with NO clamping at MinNormalExponent, and
        // round it there -- IEEE 754's own "as if the exponent range were
        // unbounded" step, exactly mirroring
        // <see cref="SingleFloat.RoundWideSignificandToSingle"/>'s own
        // two-stage design, which alone decides tininess.
        //
        // A measured bug (tools/NativeOracle -- an independent review's own
        // trace, MULSD 0x3FEFFFFFFFFFFFFF * 0x0010000000000000): the
        // earlier, single-stage version clamped this normalizing shift at
        // MinNormalExponent -- the same left-shift-and-clamp trick
        // <see cref="SingleFloat"/>'s own PackNormalized (Add's own use)
        // gets away with, because every operand Decompose ever hands *that*
        // method already sits within the format's exponent range. Multiply's
        // own expA+expB (and Divide's, and Sqrt's) can land far below that
        // range even while the raw significand's own top bit already sits
        // at or above WideFrameTopBitIndex, so the clamp never triggered at
        // all: the single pass rounded straight on the *denormal* grid,
        // whose own round-to-even tie can carry the stored value back up to
        // exactly the smallest normal (this trace's own case) -- silently
        // losing Underflow, since a mantissa that happens to look normalized
        // after that one rounding pass was taken as proof the result was
        // never tiny to begin with. Deciding tininess here, before that
        // second rounding ever runs, is what fixes it.
        int top = SingleFloat.TopBitIndex(significandBits);
        int naturalShift = top - WideFrameTopBitIndex;
        UInt128 naturalSignificand = naturalShift > 0
            ? SingleFloat.ShiftRightSticky(significandBits, naturalShift)
            : significandBits << -naturalShift;
        int naturalExponent = exponent + naturalShift;

        (ulong naturalMantissa, int roundedNaturalExponent, bool naturalInexact) =
            RoundSignificandTo53Bits(naturalSignificand, naturalExponent, rc, negative);

        bool tiny = roundedNaturalExponent < MinNormalExponent;
        bool overflow = roundedNaturalExponent > MaxNormalExponent;
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

            ulong overflowResult = roundToInfinity
                ? (negative ? NegativeInfinity : PositiveInfinity)
                : (negative ? (NegativeInfinity - 1) : (PositiveInfinity - 1));
            return (overflowResult, flags);
        }

        if (!tiny)
        {
            // The natural, unbounded-exponent rounding above already *is*
            // the final stored value (and is always normalized: the
            // top-bit search that built naturalSignificand guarantees it,
            // any round-up carry included): its exponent fits the format,
            // so no further rounding is needed.
            if (naturalInexact) flags |= FpFlags.Precision;
            ulong normalBiasedExponent = (ulong)(roundedNaturalExponent + Bias);
            return ((negative ? SignMask : 0) | (normalBiasedExponent << 52) | (naturalMantissa & MantissaMask), flags);
        }

        // Tiny: the true value still needs a second rounding, at the
        // reduced precision the denormal grid actually offers at
        // MinNormalExponent -- redone from the *original* significandBits
        // (not from naturalSignificand/naturalMantissa above), so this is
        // one rounding of the true value, not a second rounding of an
        // already-rounded one.
        //
        // The realignment can go *either* direction, unlike Add/Multiply's
        // own case: for those two, the raw <paramref name="exponent"/>
        // already sits close to its value's own natural exponent (Multiply's
        // raw product's own top bit is always at or within one bit of
        // WideFrameTopBitIndex, so naturalShift there is 0 or +-1), so the
        // old "always shift right by MinNormalExponent-exponent" version
        // happened to work for them by coincidence. Divide's own quotient
        // (and Sqrt's own root) can have their raw top bit far below
        // WideFrameTopBitIndex -- naturalShift there is a large *negative*
        // number -- which pushes the raw <paramref name="exponent"/> well
        // *above* the value's own natural exponent, and MinNormalExponent-exponent
        // then comes out negative even in the tiny branch: a measured bug
        // (tools/NativeOracle -- DIVSD 0x001FFFFFFFFFFFFF / 2.0, the same
        // tie-to-normal shape one level up from the MULSD case above), where
        // <see cref="SingleFloat.ShiftRightSticky"/>'s own "shift &lt;= 0:
        // return unchanged" rule silently skipped the realignment
        // altogether and rounded raw, unaligned bits instead. Recomputed
        // here as a real left-or-right shift, exactly mirroring the
        // unclamped stage-1 search above, rather than assuming the sign.
        int rawShift = exponent - MinNormalExponent;
        UInt128 denormalFrame = rawShift <= 0
            ? SingleFloat.ShiftRightSticky(significandBits, -rawShift)
            : significandBits << rawShift;
        (ulong denormalMantissa, int denormalExponent, bool denormalInexact) =
            RoundSignificandTo53Bits(denormalFrame, MinNormalExponent, rc, negative);

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
        bool denormalNormalized = (denormalMantissa & ImplicitBit) != 0;
        ulong storedBiasedExponent = denormalNormalized ? (ulong)(denormalExponent + Bias) : 0ul;
        ulong signBit = negative ? SignMask : 0;
        return (signBit | (storedBiasedExponent << 52) | (denormalMantissa & MantissaMask), flags);
    }

    /// <summary>
    /// Rounds whatever sits at bits <see cref="WideFrameTopBitIndex"/> down
    /// to <c>WideFrameTopBitIndex - 52</c> in <paramref name="significandBits"/>
    /// to a 53-bit mantissa, by <paramref name="rc"/>, with a real
    /// guard/round/sticky triple -- the shared rounding tail
    /// <see cref="PackNormalized"/> calls twice (natural, then denormal),
    /// exactly mirroring <see cref="SingleFloat"/>'s own
    /// <c>RoundSignificandTo24Bits</c>. Every significand handed to this
    /// function already has its top bit at exactly <see cref="WideFrameTopBitIndex"/>
    /// or below (never above), so there is never a right-shift-to-normalize
    /// to do here, only the rounding itself. A carry into the 54th bit is
    /// folded back into 53 with the exponent incremented.
    /// </summary>
    private static (ulong Mantissa, int Exponent, bool Inexact) RoundSignificandTo53Bits(UInt128 significandBits, int exponent, RoundingMode rc, bool negative)
    {
        const int mantissaShift = WideFrameTopBitIndex - 52;
        ulong mantissa = (ulong)(significandBits >> mantissaShift);
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
            if (mantissa == (1ul << 53))
            {
                mantissa >>= 1;
                exponent++;
            }
        }

        return (mantissa, exponent, inexact);
    }

    /// <summary>A finite double's (exponent, 53-bit significand) pair: value = significand * 2^(exponent-52). See <see cref="SingleFloat.Decompose(uint)"/>'s own equivalent remark.</summary>
    private static (int Exponent, ulong Significand) Decompose(ulong bits)
    {
        ulong biasedExponent = (bits & ExponentMask) >> 52;
        ulong mantissa = bits & MantissaMask;
        return biasedExponent == 0
            ? (MinNormalExponent, mantissa)
            : ((int)biasedExponent - Bias, mantissa | ImplicitBit);
    }

    // --- multiply --------------------------------------------------------------

    public static (ulong Result, FpFlags Flags) Multiply(ulong a, ulong b, RoundingMode rc, bool flushToZero, bool underflowMasked, bool daz = false)
    {
        a = ApplyDaz(a, daz);
        b = ApplyDaz(b, daz);
        if (IsNaN(a) || IsNaN(b)) return PropagateNaN(a, b);

        bool resultNegative = SignBit(a) ^ SignBit(b);

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

        (int expA, ulong sigA) = Decompose(a);
        (int expB, ulong sigB) = Decompose(b);

        // Two 53-bit significands multiply to at most 106 bits: an exact
        // UInt128 product, no rounding of its own -- see the type summary.
        UInt128 product = (UInt128)sigA * (UInt128)sigB;
        return PackNormalized(resultNegative, expA + expB, product, rc, flushToZero, underflowMasked, denormalFlag);
    }

    // --- divide ------------------------------------------------------------------

    public static (ulong Result, FpFlags Flags) Divide(ulong a, ulong b, RoundingMode rc, bool flushToZero, bool underflowMasked, bool daz = false)
    {
        a = ApplyDaz(a, daz);
        b = ApplyDaz(b, daz);
        if (IsNaN(a) || IsNaN(b)) return PropagateNaN(a, b);

        bool resultNegative = SignBit(a) ^ SignBit(b);

        if (IsZero(a) && IsZero(b)) return (DefaultQNaN, FpFlags.Invalid);
        if (IsInfinity(a) && IsInfinity(b)) return (DefaultQNaN, FpFlags.Invalid);

        FpFlags denormalFlag = DenormalFlagOf(a, b);

        if (IsInfinity(a))
        {
            return (resultNegative ? NegativeInfinity : PositiveInfinity, denormalFlag);
        }

        if (IsZero(b))
        {
            return (resultNegative ? NegativeInfinity : PositiveInfinity, FpFlags.DivideByZero);
        }

        if (IsInfinity(b) || IsZero(a))
        {
            return (resultNegative ? NegativeZero : PositiveZero, denormalFlag);
        }

        (int expA, ulong sigA) = Decompose(a);
        (int expB, ulong sigB) = Decompose(b);

        // Long division to a guard/sticky tail: sigA shifted left so the
        // numerator always uses the full 128-bit register, whatever sigA's
        // own bit length is -- a *fixed* shift (64 bits, this method's first
        // version) starves the quotient of precision whenever sigA itself
        // has few significant bits (a review finding, measured directly:
        // DIVSD 0x001FFFFFFFFFFFFF / 2.0, RN, gave a wrong, unrelated result
        // instead of the correctly-rounded tie-to-normal one -- Sqrt's own
        // extraBits is the same shape one level down, fixed the same way).
        // <paramref name="sigA"/>'s own bit length varies (1 for the
        // smallest denormal, up to 53 for a normal), so <c>extraBits</c>
        // does too, keeping the numerator's own top bit pinned at bit 127
        // every time. The quotient's own bits are then exact bits of the
        // true quotient down to 2^-extraBits relative precision; a nonzero
        // remainder becomes one sticky bit folded into the quotient's own
        // bit 0.
        int sigABits = sigA == 0 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount(sigA);
        int extraBits = 128 - sigABits;
        UInt128 numerator = (UInt128)sigA << extraBits;
        UInt128 quotient = numerator / sigB;
        UInt128 remainder = numerator % sigB;
        if (remainder != UInt128.Zero) quotient |= 1;

        // value = (sigA/sigB) * 2^(expA-expB) = (quotient/2^extraBits) * 2^(expA-expB)
        // = quotient * 2^(expA-expB-extraBits); matching the PackNormalized
        // invariant "value = quotient * 2^(exponent-WideFrameTopBitIndex)"
        // gives exponent = expA-expB-extraBits+WideFrameTopBitIndex.
        int exponent = expA - expB - extraBits + WideFrameTopBitIndex;
        return PackNormalized(resultNegative, exponent, quotient, rc, flushToZero, underflowMasked, denormalFlag);
    }

    // --- square root ---------------------------------------------------------------

    public static (ulong Result, FpFlags Flags) Sqrt(ulong a, RoundingMode rc, bool flushToZero, bool underflowMasked, bool daz = false)
    {
        a = ApplyDaz(a, daz);
        if (IsNaN(a)) return PropagateNaN(a, a);

        if (IsZero(a)) return (a, FpFlags.None);
        if (SignBit(a)) return (DefaultQNaN, FpFlags.Invalid);
        if (IsInfinity(a)) return (PositiveInfinity, FpFlags.None);

        FpFlags denormalFlag = DenormalFlagOf(a);
        (int exponent, ulong sig) = Decompose(a);

        // sqrt(sig * 2^(exponent-52)) needs an even "exponent-52" to split
        // cleanly into sqrt(sig')*2^m; an odd one is folded into one extra
        // bit of the significand first (see the type summary).
        int workingExponent = exponent - 52;
        ulong sig2 = sig;
        if ((workingExponent & 1) != 0)
        {
            sig2 <<= 1;
            workingExponent -= 1;
        }
        int m = workingExponent / 2;

        // extraBits varies with sig2's own bit length, for the same reason
        // Divide's own extraBits now does (this method's own measured bug,
        // tools/NativeOracle: SQRTPD of a denormal source landed one ULP
        // off, with Precision wrongly left clear, because a *fixed*
        // extraBits left too few bits in the root when sig2 itself was a
        // small denormal): scaled's own bit length is kept close to the
        // full 128-bit register every time, so the root -- half that many
        // bits -- always has far more than the 53 the result needs,
        // whatever sig2's own bit length was.
        int sig2Bits = sig2 == 0 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount(sig2);
        int extraBits = (126 - sig2Bits) / 2;   // sqrt(sig2 * 2^(2*extraBits)) ~= sqrt(sig2) * 2^extraBits.
        UInt128 scaled = ((UInt128)sig2) << (2 * extraBits);
        (UInt128 root, bool inexactRoot) = IntegerSqrt(scaled);
        if (inexactRoot) root |= 1;

        // value = sqrt(sig2)*2^m ~= (root/2^extraBits)*2^m = root*2^(m-extraBits);
        // matching the PackNormalized invariant gives exponent = m-extraBits+WideFrameTopBitIndex.
        int packedExponent = m - extraBits + WideFrameTopBitIndex;
        return PackNormalized(negative: false, packedExponent, root, rc, flushToZero, underflowMasked, denormalFlag);
    }

    /// <summary>
    /// The classical binary digit-by-digit integer square root: exact
    /// (<c>Root * Root &lt;= n &lt; (Root+1) * (Root+1)</c>), with
    /// <paramref name="n"/>'s own leftover remainder (n - Root*Root) telling
    /// whether the true root is irrational at this precision -- the sticky
    /// bit <see cref="Sqrt"/>'s own caller folds into the root's bit 0.
    /// </summary>
    private static (UInt128 Root, bool Inexact) IntegerSqrt(UInt128 n)
    {
        if (n == UInt128.Zero) return (UInt128.Zero, false);

        UInt128 bit = (UInt128)1 << 126;
        while (bit > n) bit >>= 2;

        UInt128 result = UInt128.Zero;
        UInt128 remainder = n;
        while (bit != UInt128.Zero)
        {
            UInt128 candidate = result + bit;
            if (remainder >= candidate)
            {
                remainder -= candidate;
                result = (result >> 1) + bit;
            }
            else
            {
                result >>= 1;
            }
            bit >>= 2;
        }

        return (result, remainder != UInt128.Zero);
    }

    // --- comparisons -----------------------------------------------------------

    /// <summary>UCOMISD/COMISD's own ZF:PF:CF encoding -- identical to <see cref="SingleFloat.CompareForFlags"/>.</summary>
    public static (bool Zf, bool Pf, bool Cf, FpFlags Flags) CompareForFlags(ulong a, ulong b, bool unorderedOnlySignaling, bool daz = false)
    {
        a = ApplyDaz(a, daz);
        b = ApplyDaz(b, daz);
        bool eitherNaN = IsNaN(a) || IsNaN(b);
        bool eitherSignaling = IsSignalingNaN(a) || IsSignalingNaN(b);
        FpFlags invalidFlag = (eitherSignaling || (eitherNaN && !unorderedOnlySignaling)) ? FpFlags.Invalid : FpFlags.None;

        if (eitherNaN) return (true, true, true, invalidFlag);

        FpFlags flags = invalidFlag | DenormalFlagOf(a, b);
        double da = ToDouble(a), db = ToDouble(b);
        if (da < db) return (false, false, true, flags);
        if (da > db) return (false, false, false, flags);
        return (true, false, false, flags);
    }

    /// <summary>CMPPD/CMPSD's eight predicates -- identical to <see cref="SingleFloat.ComparePredicate"/>.</summary>
    public static (ulong Result, FpFlags Flags) ComparePredicate(ulong a, ulong b, int predicate, bool daz = false)
    {
        a = ApplyDaz(a, daz);
        b = ApplyDaz(b, daz);
        bool eitherNaN = IsNaN(a) || IsNaN(b);
        bool eitherSignaling = IsSignalingNaN(a) || IsSignalingNaN(b);
        bool quietAlsoSignals = predicate is 1 or 2 or 5 or 6;
        FpFlags flags = (eitherSignaling || (eitherNaN && quietAlsoSignals)) ? FpFlags.Invalid : FpFlags.None;

        bool trueResult;
        if (eitherNaN)
        {
            trueResult = predicate is 3 or 4 or 5 or 6;
        }
        else
        {
            flags |= DenormalFlagOf(a, b);
            double da = ToDouble(a), db = ToDouble(b);
            trueResult = predicate switch
            {
                0 => da == db,
                1 => da < db,
                2 => da <= db,
                3 => false,
                4 => da != db,
                5 => !(da < db),
                6 => !(da <= db),
                7 => true,
                _ => false,
            };
        }

        return (trueResult ? 0xFFFF_FFFF_FFFF_FFFFul : 0ul, flags);
    }

    // --- MAX/MIN -------------------------------------------------------------------

    /// <summary>
    /// MAXPD/MAXSD: DEST &gt; SRC ? DEST : SRC, source wins on any NaN or
    /// equal operands -- measured on the host (tools/NativeOracle --probe:
    /// MAXSD/MINSD with a QNaN in either operand, and with +0/-0) to be the
    /// identical rule <see cref="SingleFloat.Max"/>/<see cref="SingleFloat.Min"/>
    /// measure for MAXPS/MINPS.
    /// </summary>
    public static (ulong Result, FpFlags Flags) Max(ulong destination, ulong source, bool daz = false) => MaxOrMin(destination, source, wantGreater: true, daz);

    public static (ulong Result, FpFlags Flags) Min(ulong destination, ulong source, bool daz = false) => MaxOrMin(destination, source, wantGreater: false, daz);

    private static (ulong, FpFlags) MaxOrMin(ulong destination, ulong source, bool wantGreater, bool daz)
    {
        destination = ApplyDaz(destination, daz);
        source = ApplyDaz(source, daz);

        bool eitherNaN = IsNaN(destination) || IsNaN(source);
        if (eitherNaN) return (source, FpFlags.Invalid);

        FpFlags flags = DenormalFlagOf(destination, source);
        double dd = ToDouble(destination), ds = ToDouble(source);
        if (dd == ds) return (source, flags);

        bool destinationWins = wantGreater ? dd > ds : dd < ds;
        return (destinationWins ? destination : source, flags);
    }

    // --- conversions -----------------------------------------------------------------

    /// <summary>CVTSI2SD/CVTPI2PD: a 32-bit signed integer is always exactly representable as a double (53-bit mantissa, 32-bit magnitude): no rounding, ever, in any RC (plans/specs/sse2.md's own test case 11).</summary>
    public static (ulong Result, FpFlags Flags) ConvertInt32ToDouble(int value) => (FromDouble(value), FpFlags.None);

    /// <summary>
    /// CVTSD2SI/CVTPD2PI (<paramref name="truncate"/> false, rounds by
    /// <paramref name="rc"/>) and CVTTSD2SI/CVTTPD2PI (<paramref name="truncate"/>
    /// true): a NaN, an infinity or an in-range value that rounds outside
    /// [-2^31, 2^31-1] all produce 80000000h and Invalid -- measured
    /// identical to <see cref="SingleFloat.ConvertToInt32"/> (tools/NativeOracle
    /// --probe: a denormal double source sets Precision alone, never
    /// Denormal; an overflowing source, 2^62, sets Invalid alone).
    /// </summary>
    public static (int Result, FpFlags Flags) ConvertToInt32(ulong bits, RoundingMode rc, bool truncate, bool daz = false)
    {
        const int IntegerIndefinite = unchecked((int)0x8000_0000);

        bits = ApplyDaz(bits, daz);
        if (IsNaN(bits) || IsInfinity(bits))
        {
            return (IntegerIndefinite, FpFlags.Invalid);
        }

        double exactValue = ToDouble(bits);
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

    /// <summary>
    /// CVTPS2PD/CVTSS2SD: single to double is always exact -- double's own
    /// exponent and mantissa both comfortably exceed single's, so every
    /// finite single (normal or denormal) converts to a normal double with
    /// no rounding; only NaN and infinity need their own bit patterns
    /// rebuilt at the wider width. A signaling source is quieted (Invalid
    /// set); a quiet source's own quiet bit already lands in the right place
    /// after the mantissa is widened, since single's own bit 22 (its quiet
    /// bit) shifts to double's bit 51 (its own) by the same 29-bit widening
    /// every other mantissa bit gets.
    /// </summary>
    public static (ulong Result, FpFlags Flags) ConvertSingleToDouble(uint bits, bool daz = false)
    {
        if (daz && SingleFloat.IsDenormal(bits))
        {
            bits = SingleFloat.SignBit(bits) ? SingleFloat.NegativeZero : SingleFloat.PositiveZero;
        }

        ulong sign = SingleFloat.SignBit(bits) ? SignMask : 0;

        if (SingleFloat.IsNaN(bits))
        {
            bool signaling = SingleFloat.IsSignalingNaN(bits);
            ulong mantissa = ((ulong)(bits & SingleFloat.MantissaMask)) << 29;
            if (signaling) mantissa |= QuietBit;
            return (sign | ExponentMask | mantissa, signaling ? FpFlags.Invalid : FpFlags.None);
        }

        if (SingleFloat.IsInfinity(bits)) return (sign | ExponentMask, FpFlags.None);
        if (SingleFloat.IsZero(bits)) return (sign, FpFlags.None);

        uint mantissa23 = bits & SingleFloat.MantissaMask;
        uint biasedExponent32 = (bits & SingleFloat.ExponentMask) >> 23;
        FpFlags denormalFlag = biasedExponent32 == 0 ? FpFlags.Denormal : FpFlags.None;

        int exponent;
        uint sig;
        if (biasedExponent32 == 0)
        {
            // Denormal single: normalize it (find the leading 1, shift it to
            // bit 23, and lower the exponent to match) before widening --
            // double has no denormal in this range to preserve the shape of.
            exponent = -126;
            sig = mantissa23;
            int top = 31 - System.Numerics.BitOperations.LeadingZeroCount(sig);
            int shift = 23 - top;
            sig <<= shift;
            exponent -= shift;
        }
        else
        {
            exponent = (int)biasedExponent32 - 127;
            sig = mantissa23 | 0x0080_0000u;
        }

        ulong doubleMantissa = ((ulong)(sig & 0x007F_FFFFu)) << 29;
        ulong doubleBiasedExponent = (ulong)(exponent + Bias);
        return (sign | (doubleBiasedExponent << 52) | doubleMantissa, denormalFlag);
    }

    /// <summary>
    /// CVTPD2PS/CVTSD2SS: double to single rounds by <paramref name="rc"/>
    /// and can overflow or underflow -- handed off to
    /// <see cref="SingleFloat.RoundWideSignificandToSingle"/> (a shared
    /// helper this work package added to <c>SingleFloat.cs</c> precisely for
    /// this) once NaN/infinity/zero are out of the way, since that method's
    /// own overflow/tininess-on-the-natural-exponent rule already handles
    /// every case correctly for an arbitrary (exponent, significand) pair,
    /// denormal source included -- unlike its own <c>DecomposeDouble</c>,
    /// which only ever sees a hardware arithmetic result and so is never
    /// called here.
    /// </summary>
    public static (uint Result, FpFlags Flags) ConvertDoubleToSingle(ulong bits, RoundingMode rc, bool flushToZero, bool underflowMasked, bool daz = false)
    {
        bits = ApplyDaz(bits, daz);

        if (IsNaN(bits))
        {
            bool signaling = IsSignalingNaN(bits);
            uint sign = SignBit(bits) ? SingleFloat.SignMask : 0;
            uint mantissa = (uint)((bits & MantissaMask) >> 29) & SingleFloat.MantissaMask;
            if (signaling) mantissa |= 0x0040_0000u;
            return (sign | SingleFloat.ExponentMask | mantissa, signaling ? FpFlags.Invalid : FpFlags.None);
        }

        if (IsInfinity(bits)) return ((SignBit(bits) ? SingleFloat.SignMask : 0) | SingleFloat.ExponentMask, FpFlags.None);
        if (IsZero(bits)) return (SignBit(bits) ? SingleFloat.NegativeZero : SingleFloat.PositiveZero, FpFlags.None);

        bool negative = SignBit(bits);
        FpFlags denormalFlag = IsDenormal(bits) ? FpFlags.Denormal : FpFlags.None;
        (int exponent, ulong sig) = Decompose(bits);

        UInt128 wideSignificand = ((UInt128)sig) << (SingleFloat.WideFrameTopBitIndex - 52);
        return SingleFloat.RoundWideSignificandToSingle(negative, exponent, wideSignificand, rc, flushToZero, underflowMasked, denormalFlag);
    }
}
