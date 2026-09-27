using Aurora386.Core.Cpu.Fpu;

namespace Aurora386.Tests;

/// <summary>Raw80 vectors for FISTTP's truncation before the x87 decoder stores or pops it.</summary>
public class FisttpConversionTests
{
    private static Raw80 Value(ulong significand, int unbiasedExponent, bool negative = false)
    {
        ushort exponent = (ushort)(unbiasedExponent + 16383);
        if (negative)
        {
            exponent |= 0x8000;
        }

        return new Raw80(significand, exponent);
    }

    [Theory]
    [InlineData(16, 0x8000UL)]
    [InlineData(32, 0x80000000UL)]
    [InlineData(64, 0x8000000000000000UL)]
    public void InvalidValuesReturnTheWidthsIntegerIndefinite(int width, ulong expected)
    {
        IntegerConversionResult result = IntegerConversion.Truncate(new Raw80(0xC000000000000001, 0x7FFF), width);
        Assert.Equal(expected, result.Bits);
        Assert.Equal(IntegerConversion.InvalidOperation, result.Exceptions);
    }

    [Fact]
    public void FractionsTruncateTowardZeroAndKeepPrecision()
    {
        IntegerConversionResult positive = IntegerConversion.Truncate(Value(0xC000000000000000, 0), 32); // 1.5
        IntegerConversionResult negative = IntegerConversion.Truncate(Value(0xC000000000000000, 0, negative: true), 32);
        Assert.Equal(1UL, positive.Bits);
        Assert.Equal(0xFFFFFFFFUL, negative.Bits);
        Assert.Equal(IntegerConversion.Precision, positive.Exceptions);
        Assert.Equal(IntegerConversion.Precision, negative.Exceptions);
    }

    [Fact]
    public void RangeIsCheckedAfterTruncation()
    {
        IntegerConversionResult minimum = IntegerConversion.Truncate(new Raw80(0x8000C00000000000, 0xC00E), 16);
        Assert.Equal(0x8000UL, minimum.Bits);
        Assert.Equal(IntegerConversion.Precision, minimum.Exceptions);
    }

    [Fact]
    public void DenormalsAndPseudoDenormalsAreZeroWithPrecisionOnly()
    {
        Assert.Equal(new IntegerConversionResult(0, IntegerConversion.Precision),
            IntegerConversion.Truncate(new Raw80(1, 0), 64));
        Assert.Equal(new IntegerConversionResult(0, IntegerConversion.Precision),
            IntegerConversion.Truncate(new Raw80(0x8000000000000001, 0), 64));
    }

    [Fact]
    public void FullPrecisionIntegersAboveDoublePrecisionArePreserved()
    {
        IntegerConversionResult result = IntegerConversion.Truncate(Value(0x8000000000000400, 53), 64);
        Assert.Equal(0x0020000000000001UL, result.Bits);
        Assert.Equal(0, result.Exceptions);
    }

    [Fact]
    public void Signed64BitLimitsAndTheirNeighboursUseTheTruncatedMagnitude()
    {
        Assert.Equal(new IntegerConversionResult(0x7FFFFFFFFFFFFFFFUL, 0),
            IntegerConversion.Truncate(Value(0xFFFFFFFFFFFFFFFE, 62), 64));
        Assert.Equal(new IntegerConversionResult(0x8000000000000000UL, 0),
            IntegerConversion.Truncate(Value(0x8000000000000000, 63, negative: true), 64));
        Assert.Equal(new IntegerConversionResult(0x8000000000000000UL, IntegerConversion.InvalidOperation),
            IntegerConversion.Truncate(Value(0x8000000000000000, 63), 64));
        Assert.Equal(new IntegerConversionResult(0x8000000000000001UL, IntegerConversion.Precision),
            IntegerConversion.Truncate(Value(0xFFFFFFFFFFFFFFFF, 62, negative: true), 64));
    }

    [Theory]
    [InlineData(0UL, 0x0000)]
    [InlineData(0UL, 0x8000)]
    public void BothZeroSignsHaveNoException(ulong significand, ushort signExponent)
    {
        Assert.Equal(new IntegerConversionResult(0, 0),
            IntegerConversion.Truncate(new Raw80(significand, signExponent), 32));
    }

    [Theory]
    [InlineData(0UL, 0x0001)]
    [InlineData(1UL, 0x0001)]
    [InlineData(0UL, 0x3FFF)]
    [InlineData(0x4000000000000000UL, 0x7FFF)]
    [InlineData(0xC000000000000001UL, 0x7FFF)]
    public void UnsupportedRawEncodingsAreInvalidOnly(ulong significand, ushort signExponent)
    {
        Assert.Equal(new IntegerConversionResult(0x80000000, IntegerConversion.InvalidOperation),
            IntegerConversion.Truncate(new Raw80(significand, signExponent), 32));
    }

    [Theory]
    [InlineData(16, 0x8000000000000000UL, 15, false)]
    [InlineData(16, 0x8001000000000000UL, 15, true)]
    [InlineData(32, 0x8000000000000000UL, 31, false)]
    [InlineData(32, 0x8000000100000000UL, 31, true)]
    public void NarrowWidthOverflowUsesOnlyTheIntegerIndefinite(int width, ulong significand, int exponent, bool negative)
    {
        IntegerConversionResult result = IntegerConversion.Truncate(Value(significand, exponent, negative), width);
        Assert.Equal(1UL << (width - 1), result.Bits);
        Assert.Equal(IntegerConversion.InvalidOperation, result.Exceptions);
    }

    [Fact]
    public void UnsupportedWidthIsRejectedBeforeAnyConversion()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => IntegerConversion.Truncate(new Raw80(0, 0), 8));
    }
}
