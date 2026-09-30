namespace Aurora86.Core.Cpu.Fpu;

/// <summary>
/// The integer part of x87 FISTTP: it truncates an unmodified 80-bit register
/// toward zero without consulting FCW or changing the x87 stack. The escape
/// decoder decides masking, stores the returned bits and performs the pop.
/// </summary>
public static class IntegerConversion
{
    public const ushort InvalidOperation = 0x0001;
    public const ushort Precision = 0x0020;

    private const ulong IntegerBit = 1UL << 63;
    private const ushort SignMask = 0x8000;
    private const int ExponentBias = 16383;
    private const int MaximumExponent = 0x7FFF;

    /// <summary>Truncates one raw extended value toward zero into a signed integer width.</summary>
    public static IntegerConversionResult Truncate(Raw80 value, int width)
    {
        if (width is not (16 or 32 or 64))
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        int exponent = value.BiasedExponent;
        bool negative = (value.SignExponent & SignMask) != 0;
        ulong significand = value.Significand;
        ulong widthMask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
        ulong indefinite = 1UL << (width - 1);

        if (exponent == MaximumExponent || exponent != 0 && (significand & IntegerBit) == 0)
        {
            return new IntegerConversionResult(indefinite, InvalidOperation);
        }

        if (exponent == 0)
        {
            ushort exceptions = significand == 0 ? (ushort)0 : Precision;
            return new IntegerConversionResult(0, exceptions);
        }

        int unbiasedExponent = exponent - ExponentBias;
        if (unbiasedExponent < 0)
        {
            return new IntegerConversionResult(0, Precision);
        }

        if (unbiasedExponent >= 64)
        {
            return new IntegerConversionResult(indefinite, InvalidOperation);
        }

        int shift = 63 - unbiasedExponent;
        ulong magnitude = significand >> shift;
        ulong discarded = shift == 0 ? 0 : significand & ((1UL << shift) - 1);
        ulong negativeLimit = 1UL << (width - 1);
        ulong positiveLimit = negativeLimit - 1;
        ulong magnitudeLimit = negative ? negativeLimit : positiveLimit;
        if (magnitude > magnitudeLimit)
        {
            return new IntegerConversionResult(indefinite, InvalidOperation);
        }

        ulong bits = negative ? unchecked(0UL - magnitude) : magnitude;
        ushort precision = discarded == 0 ? (ushort)0 : Precision;
        return new IntegerConversionResult(bits & widthMask, precision);
    }
}

/// <summary>The integer bits and x87 sticky exceptions FISTTP derives from one raw register.</summary>
public readonly record struct IntegerConversionResult(ulong Bits, ushort Exceptions);
