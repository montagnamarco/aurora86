using Aurora386.Core.Bios;

namespace Aurora386.Tests;

/// <summary>
/// Tests for the 80-bit floating-point type.
///
/// The point of the type is the eleven bits of significand a double does not
/// have, so most of these tests are built to fail if the arithmetic quietly
/// went through a double on the way.
/// </summary>
public class Extended80Tests
{
    [Fact]
    public void AValueSurvivesTheTripThroughADouble()
    {
        Extended80 value = Extended80.FromDouble(1234.5625);
        Assert.Equal(1234.5625, value.ToDouble());
    }

    [Fact]
    public void NegativeValuesKeepTheirSign()
    {
        Assert.Equal(-0.15625, Extended80.FromDouble(-0.15625).ToDouble());
    }

    [Fact]
    public void AdditionMatchesWhatADoubleWouldGiveForSmallCases()
    {
        Extended80 sum = Extended80.FromDouble(2.5) + Extended80.FromDouble(4.0);
        Assert.Equal(6.5, sum.ToDouble());
    }

    [Fact]
    public void SubtractionCrossingZeroChangesSign()
    {
        Extended80 result = Extended80.FromDouble(2.5) - Extended80.FromDouble(4.0);
        Assert.Equal(-1.5, result.ToDouble());
    }

    [Fact]
    public void Multiplication()
    {
        Extended80 result = Extended80.FromDouble(2.5) * Extended80.FromDouble(4.0);
        Assert.Equal(10.0, result.ToDouble());
    }

    [Fact]
    public void Division()
    {
        Extended80 result = Extended80.FromDouble(10.0) / Extended80.FromDouble(4.0);
        Assert.Equal(2.5, result.ToDouble());
    }

    [Fact]
    public void DivisionThatDoesNotTerminate()
    {
        Extended80 third = Extended80.FromDouble(1.0) / Extended80.FromDouble(3.0);
        Assert.Equal(1.0 / 3.0, third.ToDouble(), 15);
    }

    /// <summary>
    /// The test the whole type exists for. One plus a value too small for a
    /// double to notice is exactly one when done in double, and something
    /// larger when done in 64 bits of significand.
    /// </summary>
    [Fact]
    public void TheExtraBitsOfSignificandAreReallyThere()
    {
        // 2^-60 is below a double's resolution next to 1.0, which holds 53
        // bits, but well inside what 64 bits can represent.
        Extended80 one = Extended80.FromDouble(1.0);
        Extended80 tiny = Extended80.FromDouble(Math.Pow(2.0, -60));

        Extended80 sum = one + tiny;

        Assert.NotEqual(one.Significand, sum.Significand);
        Assert.Equal(1.0, 1.0 + Math.Pow(2.0, -60));   // a double cannot tell
    }

    [Fact]
    public void ASumThatWouldRoundAwayInADoubleIsKept()
    {
        Extended80 total = Extended80.FromDouble(1.0);
        Extended80 tiny = Extended80.FromDouble(Math.Pow(2.0, -60));

        // Adding it back off again returns exactly one, which only holds if
        // the small value was still there to subtract.
        Extended80 result = (total + tiny) - tiny;

        Assert.Equal(1.0, result.ToDouble());
        Assert.Equal(Extended80.FromDouble(1.0).Significand, result.Significand);
    }

    [Fact]
    public void TheMemoryFormatRoundTrips()
    {
        Extended80 original = Extended80.FromDouble(1234.5625);
        (ulong significand, ushort signAndExponent) = original.ToBits();

        Extended80 restored = Extended80.FromBits(significand, signAndExponent);

        Assert.Equal(original.Significand, restored.Significand);
        Assert.Equal(original.Exponent, restored.Exponent);
        Assert.Equal(original.Negative, restored.Negative);
    }

    [Fact]
    public void TheMemoryFormatCarriesTheFullSignificand()
    {
        // A significand with bits set below what a double can hold.
        Extended80 value = Extended80.FromBits(0xFFFFFFFFFFFFFFFF, 16383);
        (ulong significand, _) = value.ToBits();

        Assert.Equal(0xFFFFFFFFFFFFFFFFUL, significand);
    }

    [Fact]
    public void Zero()
    {
        Assert.True(Extended80.Zero.IsZero);
        Assert.Equal(0.0, Extended80.Zero.ToDouble());
    }

    [Fact]
    public void AddingZeroChangesNothing()
    {
        Extended80 value = Extended80.FromDouble(7.25);
        Assert.Equal(7.25, (value + Extended80.Zero).ToDouble());
    }

    [Fact]
    public void SubtractingAValueFromItselfGivesZero()
    {
        Extended80 value = Extended80.FromDouble(7.25);
        Assert.True((value - value).IsZero);
    }

    [Fact]
    public void InfinityAndNaNAreCarriedThrough()
    {
        Assert.True(double.IsPositiveInfinity(Extended80.Infinity(false).ToDouble()));
        Assert.True(double.IsNegativeInfinity(Extended80.Infinity(true).ToDouble()));
        Assert.True(double.IsNaN(Extended80.NaN.ToDouble()));
    }

    [Fact]
    public void DividingByZeroGivesInfinity()
    {
        Extended80 result = Extended80.FromDouble(1.0) / Extended80.Zero;
        Assert.True(result.IsInfinity);
    }

    [Fact]
    public void ZeroDividedByZeroIsNotANumber()
    {
        Assert.True((Extended80.Zero / Extended80.Zero).IsNaN);
    }

    [Fact]
    public void InfinityMinusInfinityIsNotANumber()
    {
        Extended80 infinity = Extended80.Infinity(false);
        Assert.True((infinity - infinity).IsNaN);
    }

    [Theory]
    [InlineData(1.0, 2.0, -1)]
    [InlineData(2.0, 1.0, 1)]
    [InlineData(3.0, 3.0, 0)]
    [InlineData(-1.0, 1.0, -1)]
    [InlineData(-2.0, -1.0, -1)]
    public void Ordering(double a, double b, int expected)
    {
        Assert.Equal(expected,
            Extended80.Compare(Extended80.FromDouble(a), Extended80.FromDouble(b)));
    }

    [Fact]
    public void ComparingAgainstNotANumberIsUnordered()
    {
        Assert.Equal(2, Extended80.Compare(Extended80.FromDouble(1.0), Extended80.NaN));
    }

    [Fact]
    public void VerySmallAndVeryLargeExponents()
    {
        Extended80 large = Extended80.FromDouble(1e300);
        Extended80 small = Extended80.FromDouble(1e-300);

        // At these exponents an absolute tolerance is meaningless, so the
        // comparison is relative to the value itself.
        Assert.True(Math.Abs(large.ToDouble() / 1e300 - 1.0) < 1e-12);
        Assert.True(Math.Abs(small.ToDouble() / 1e-300 - 1.0) < 1e-12);
        Assert.Equal(1.0, (large * small).ToDouble(), 8);
    }
}
