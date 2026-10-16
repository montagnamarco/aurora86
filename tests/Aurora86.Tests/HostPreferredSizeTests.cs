using Aurora86.Graphics;

namespace Aurora86.Tests;

/// <summary>Checks the host-area policy used by both interactive frontends.</summary>
public sealed class HostPreferredSizeTests
{
    [Theory]
    [InlineData(320, 240, 320, 240)]
    [InlineData(800.9, 600.9, 800, 600)]
    [InlineData(1921, 1081, 1920, 1080)]
    [InlineData(double.MaxValue, double.MaxValue, 1920, 1080)]
    public void AcceptedAreasAreWholeLogicalPixels(double width, double height,
        int expectedWidth, int expectedHeight)
    {
        Assert.True(HostPreferredSize.TryFromLogical(width, height, false,
            out int actualWidth, out int actualHeight));
        Assert.Equal(expectedWidth, actualWidth);
        Assert.Equal(expectedHeight, actualHeight);
    }

    [Theory]
    [InlineData(319.99, 600)]
    [InlineData(800, 239.99)]
    [InlineData(double.NaN, 600)]
    [InlineData(800, double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity, 600)]
    [InlineData(-1, 600)]
    public void TransientOrInvalidAreasAreIgnored(double width, double height)
    {
        Assert.False(HostPreferredSize.TryFromLogical(width, height, false,
            out int actualWidth, out int actualHeight));
        Assert.Equal(0, actualWidth);
        Assert.Equal(0, actualHeight);
    }

    [Fact]
    public void MinimizedWindowWithRetainedClientAreaIsIgnored()
    {
        Assert.False(HostPreferredSize.TryFromLogical(1024, 768, true,
            out int actualWidth, out int actualHeight));
        Assert.Equal(0, actualWidth);
        Assert.Equal(0, actualHeight);
    }
}
