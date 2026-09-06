namespace Aurora386.Core.Firmware.Fpu;

/// <summary>
/// The 80-bit floating-point value an 80387 keeps in its registers: a sign, a
/// 15-bit exponent, and a 64-bit significand.
///
/// C# has no such type. A double carries 53 bits of significand, so a chain of
/// operations done in double and stored back to 80 bits has already lost the
/// bottom eleven -- invisible in any one result, and the reason a long sum can
/// drift from what the hardware would have produced. This type carries all 64.
///
/// The significand is held normalised, with its top bit set, exactly as the
/// 387 stores it; that explicit top bit is the format's peculiarity, since
/// every shorter floating-point format leaves it implied. Zero is the case
/// that cannot be normalised, and is marked by a zero significand.
/// </summary>
public readonly struct Extended80
{
    /// <summary>All 64 bits, top bit set unless the value is zero or denormal.</summary>
    public readonly ulong Significand;

    /// <summary>Unbiased, so a value in [1,2) has exponent 0.</summary>
    public readonly int Exponent;

    public readonly bool Negative;

    /// <summary>Infinities and NaNs are carried as flags rather than as bit patterns.</summary>
    public readonly bool IsNaN;
    public readonly bool IsInfinity;

    private Extended80(ulong significand, int exponent, bool negative,
                       bool isNaN = false, bool isInfinity = false)
    {
        Significand = significand;
        Exponent = exponent;
        Negative = negative;
        IsNaN = isNaN;
        IsInfinity = isInfinity;
    }

    public static readonly Extended80 Zero = new(0, 0, false);
    public static readonly Extended80 NaN = new(0xC000000000000000, 0, false, isNaN: true);

    public static Extended80 Infinity(bool negative)
        => new(0x8000000000000000, 0, negative, isInfinity: true);

    public bool IsZero => Significand == 0 && !IsNaN && !IsInfinity;

    /// <summary>
    /// Builds a value from a significand that need not be normalised yet,
    /// shifting it up until its top bit is set and adjusting the exponent to
    /// match. Every operation ends here, because the format only ever holds
    /// normalised values.
    /// </summary>
    private static Extended80 Normalise(ulong significand, int exponent, bool negative)
    {
        if (significand == 0) return negative ? new Extended80(0, 0, true) : Zero;

        int shift = System.Numerics.BitOperations.LeadingZeroCount(significand);
        return new Extended80(significand << shift, exponent - shift, negative);
    }

    // ---- conversions -------------------------------------------------------

    public static Extended80 FromDouble(double value)
    {
        if (double.IsNaN(value)) return NaN;
        if (double.IsInfinity(value)) return Infinity(value < 0);
        if (value == 0.0) return double.IsNegative(value) ? new Extended80(0, 0, true) : Zero;

        long bits = BitConverter.DoubleToInt64Bits(value);
        bool negative = bits < 0;
        int rawExponent = (int)((bits >> 52) & 0x7FF);
        ulong fraction = (ulong)(bits & 0xFFFFFFFFFFFFF);

        if (rawExponent == 0)
        {
            // Denormal: no implied top bit, and the exponent is fixed.
            return Normalise(fraction << 11, -1022 - 52 + 63 - 11, negative);
        }

        // The implied bit goes back on, and the significand is shifted up so
        // that bit lands at the top of the 64.
        ulong significand = (fraction | 0x10000000000000) << 11;
        return new Extended80(significand, rawExponent - 1023, negative);
    }

    public double ToDouble()
    {
        if (IsNaN) return double.NaN;
        if (IsInfinity) return Negative ? double.NegativeInfinity : double.PositiveInfinity;
        if (IsZero) return Negative ? -0.0 : 0.0;

        // Round the 64-bit significand to the 53 a double holds, to nearest,
        // carrying into the exponent when rounding overflows.
        ulong significand = Significand;
        int exponent = Exponent;

        const ulong Half = 1UL << 10;
        ulong rounded = significand + Half;
        if (rounded < significand) { rounded = (rounded >> 1) | 0x8000000000000000; exponent++; }

        ulong fraction = (rounded >> 11) & 0xFFFFFFFFFFFFF;
        int rawExponent = exponent + 1023;

        if (rawExponent >= 0x7FF) return Negative ? double.NegativeInfinity : double.PositiveInfinity;
        if (rawExponent <= 0) return Negative ? -0.0 : 0.0;

        long bits = ((long)(Negative ? 1UL : 0UL) << 63)
                  | ((long)rawExponent << 52)
                  | (long)fraction;
        return BitConverter.Int64BitsToDouble(bits);
    }

    /// <summary>Reads the format as it is laid out in memory: significand, then sign and exponent.</summary>
    public static Extended80 FromBits(ulong significand, ushort signAndExponent)
    {
        bool negative = (signAndExponent & 0x8000) != 0;
        int biased = signAndExponent & 0x7FFF;

        if (biased == 0x7FFF)
        {
            ulong rest = significand & 0x7FFFFFFFFFFFFFFF;
            return rest == 0 ? Infinity(negative) : NaN;
        }

        if (biased == 0 && significand == 0) return new Extended80(0, 0, negative);

        return Normalise(significand, biased - 16383, negative);
    }

    /// <summary>The pair of words the format occupies in memory.</summary>
    public (ulong Significand, ushort SignAndExponent) ToBits()
    {
        if (IsNaN) return (0xC000000000000000, 0x7FFF);
        if (IsInfinity) return (0x8000000000000000, (ushort)(Negative ? 0xFFFF : 0x7FFF));
        if (IsZero) return (0, (ushort)(Negative ? 0x8000 : 0));

        int biased = Exponent + 16383;
        if (biased >= 0x7FFF) return (0x8000000000000000, (ushort)(Negative ? 0xFFFF : 0x7FFF));
        if (biased <= 0) return (0, (ushort)(Negative ? 0x8000 : 0));

        return (Significand, (ushort)(biased | (Negative ? 0x8000 : 0)));
    }

    // ---- arithmetic --------------------------------------------------------

    public static Extended80 operator -(Extended80 a)
        => new(a.Significand, a.Exponent, !a.Negative, a.IsNaN, a.IsInfinity);

    public static Extended80 operator +(Extended80 a, Extended80 b)
    {
        if (a.IsNaN || b.IsNaN) return NaN;
        if (a.IsInfinity && b.IsInfinity) return a.Negative == b.Negative ? a : NaN;
        if (a.IsInfinity) return a;
        if (b.IsInfinity) return b;
        if (a.IsZero) return b;
        if (b.IsZero) return a;

        // Line the two up on the larger exponent. A shift of 64 or more means
        // the smaller value cannot reach the other's lowest bit at all.
        Extended80 large = a.Exponent >= b.Exponent ? a : b;
        Extended80 small = a.Exponent >= b.Exponent ? b : a;

        int shift = large.Exponent - small.Exponent;
        if (shift >= 64) return large;

        ulong largeSignificand = large.Significand;
        ulong smallSignificand = small.Significand >> shift;

        if (large.Negative == small.Negative)
        {
            ulong sum = largeSignificand + smallSignificand;
            if (sum < largeSignificand)
            {
                // Carried out of the top: shift back down and step the exponent.
                sum = (sum >> 1) | 0x8000000000000000;
                return new Extended80(sum, large.Exponent + 1, large.Negative);
            }
            return Normalise(sum, large.Exponent, large.Negative);
        }

        // Opposite signs: the larger magnitude keeps its sign.
        if (largeSignificand >= smallSignificand)
            return Normalise(largeSignificand - smallSignificand, large.Exponent, large.Negative);

        return Normalise(smallSignificand - largeSignificand, large.Exponent, small.Negative);
    }

    public static Extended80 operator -(Extended80 a, Extended80 b) => a + (-b);

    public static Extended80 operator *(Extended80 a, Extended80 b)
    {
        if (a.IsNaN || b.IsNaN) return NaN;
        bool negative = a.Negative != b.Negative;

        if (a.IsInfinity || b.IsInfinity)
            return (a.IsZero || b.IsZero) ? NaN : Infinity(negative);
        if (a.IsZero || b.IsZero) return negative ? new Extended80(0, 0, true) : Zero;

        // The full 128-bit product keeps every bit the two significands can
        // produce; taking only the top 64 is what rounding to the format means.
        UInt128 product = (UInt128)a.Significand * b.Significand;
        ulong significand = (ulong)(product >> 64);
        int exponent = a.Exponent + b.Exponent + 1;

        // Both inputs are normalised, so the product's top bit is in one of two
        // places; when it is the lower one the value is shifted up by one.
        if ((significand & 0x8000000000000000) == 0)
        {
            significand = (significand << 1) | (ulong)((product >> 63) & 1);
            exponent--;
        }

        return new Extended80(significand, exponent, negative);
    }

    public static Extended80 operator /(Extended80 a, Extended80 b)
    {
        if (a.IsNaN || b.IsNaN) return NaN;
        bool negative = a.Negative != b.Negative;

        if (a.IsInfinity) return b.IsInfinity ? NaN : Infinity(negative);
        if (b.IsInfinity) return negative ? new Extended80(0, 0, true) : Zero;
        if (b.IsZero) return a.IsZero ? NaN : Infinity(negative);
        if (a.IsZero) return negative ? new Extended80(0, 0, true) : Zero;

        // Shifting the numerator up by 64 before dividing keeps 64 bits of
        // quotient; doing it the other way round would keep almost none.
        UInt128 numerator = (UInt128)a.Significand << 64;
        UInt128 quotient = numerator / b.Significand;

        // Both significands are normalised, so the quotient lands in [1,2)
        // relative to a 64-bit fraction -- one binary place above where the
        // result is held. Shifting the numerator up by 64 also scaled the
        // answer up by 2^64, and the exponent has to come back down by both.
        int exponent = a.Exponent - b.Exponent - 1;

        if (quotient > ulong.MaxValue)
        {
            quotient >>= 1;
            exponent++;
        }

        return Normalise((ulong)quotient, exponent, negative);
    }

    /// <summary>
    /// Orders two values. Returns less than, equal to or greater than zero,
    /// and 2 for unordered -- the answer a NaN forces, which the 387 reports
    /// with a distinct combination of condition bits rather than as an error.
    /// </summary>
    public static int Compare(Extended80 a, Extended80 b)
    {
        if (a.IsNaN || b.IsNaN) return 2;
        if (a.IsZero && b.IsZero) return 0;

        if (a.Negative != b.Negative) return a.Negative ? -1 : 1;

        int sign = a.Negative ? -1 : 1;

        if (a.IsInfinity && b.IsInfinity) return 0;
        if (a.IsInfinity) return sign;
        if (b.IsInfinity) return -sign;

        if (a.Exponent != b.Exponent) return a.Exponent > b.Exponent ? sign : -sign;
        if (a.Significand != b.Significand) return a.Significand > b.Significand ? sign : -sign;
        return 0;
    }
}
