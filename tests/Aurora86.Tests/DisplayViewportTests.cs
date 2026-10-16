using Avalonia;
using Aurora86.Gui.Controls;

namespace Aurora86.Tests;

public sealed class DisplayViewportTests
{
    [Fact]
    public void SquarePixelScanoutUsesFull1920By1080Viewport()
    {
        DisplayViewport viewport = DisplayViewport.Fit(new Size(1920, 1080), 1920, 1080, true);

        Assert.Equal(new DisplayViewport(0, 0, 1920, 1080), viewport);
    }

    [Fact]
    public void WideSquarePixelModeKeepsItsNativeAspectRatio()
    {
        DisplayViewport viewport = DisplayViewport.Fit(new Size(1920, 1080), 1280, 800, true);

        Assert.Equal(1.6, viewport.Width / viewport.Height, 10);
        Assert.True(viewport.X >= 0);
        Assert.True(viewport.Y >= 0);
        Assert.True(viewport.X + viewport.Width <= 1920);
        Assert.True(viewport.Y + viewport.Height <= 1080);
    }

    [Fact]
    public void IntegerPhysicalScaleLeavesCenteredBarsWhenLessThanTwoFits()
    {
        DisplayViewport viewport = DisplayViewport.Fit(new Size(1600, 1000), 1280, 800, true);

        Assert.Equal(new DisplayViewport(160, 100, 1280, 800), viewport);
    }

    [Fact]
    public void SquarePixelModeUsesIntegerUpscaleWhenTwoPhysicalPixelsFit()
    {
        DisplayViewport viewport = DisplayViewport.Fit(new Size(1920, 1080), 640, 480, true);

        Assert.Equal(new DisplayViewport(320, 60, 1280, 960), viewport);
    }

    [Fact]
    public void NonIntegerDpiUsesIntegerScaleAndWholePhysicalPixelOrigin()
    {
        DisplayViewport viewport = DisplayViewport.Fit(new Size(1000, 562), 1280, 720, true, 1.5);

        Assert.Equal(1280.0 / 1.5, viewport.Width, 8);
        Assert.Equal(720.0 / 1.5, viewport.Height, 8);
        Assert.Equal(110.0 / 1.5, viewport.X, 8);
        Assert.Equal(61.0 / 1.5, viewport.Y, 8);
        Assert.Equal(61, viewport.Y * 1.5, 8);
        Assert.Equal(62, (562 - viewport.Y - viewport.Height) * 1.5, 8);
    }

    [Fact]
    public void OnePhysicalPixelPerSourcePixelIsPreferredToFractionalUpscaling()
    {
        DisplayViewport viewport = DisplayViewport.Fit(new Size(1920, 1080), 1280, 800, true);

        Assert.Equal(new DisplayViewport(320, 140, 1280, 800), viewport);
    }

    [Fact]
    public void RetinaViewportUsesIntegerPhysicalPixelsAtOnePointZeroFourScale()
    {
        DisplayViewport viewport = DisplayViewport.Fit(
            new Size(1000, 562.5), 1920, 1080, true, 2.0);

        Assert.Equal(new DisplayViewport(20, 11, 960, 540), viewport);
        Assert.Equal(1920, viewport.Width * 2);
        Assert.Equal(1080, viewport.Height * 2);
    }

    [Fact]
    public void Historical640By400ModeUsesFourByThreeMonitorArea()
    {
        DisplayViewport viewport = DisplayViewport.Fit(new Size(800, 600), 640, 400, false);

        Assert.Equal(new DisplayViewport(0, 0, 800, 600), viewport);
    }

    [Fact]
    public void HistoricalModeIsLetterboxedWhenAvailableAreaIsWider()
    {
        DisplayViewport viewport = DisplayViewport.Fit(new Size(1920, 1080), 640, 400, false);

        Assert.Equal(1440, viewport.Width, 10);
        Assert.Equal(1080, viewport.Height, 10);
        Assert.Equal(240, viewport.X, 10);
        Assert.Equal(0, viewport.Y, 10);
    }

    [Fact]
    public void SmallAreaFitsWithoutExceedingEitherDimension()
    {
        DisplayViewport viewport = DisplayViewport.Fit(new Size(320, 180), 1600, 900, true);

        Assert.Equal(new DisplayViewport(0, 0, 320, 180), viewport);
    }

    [Fact]
    public void PointerMappingUsesTheRenderedViewportAndSourceGeometry()
    {
        DisplayViewport viewport = DisplayViewport.Fit(new Size(1920, 1080), 1280, 800, true);
        Assert.Equal(new DisplayViewport(320, 140, 1280, 800), viewport);
        var point = new Point(viewport.X + viewport.Width * 0.25, viewport.Y + viewport.Height * 0.75);

        Assert.Equal((320, 600), viewport.MapToSource(point, 1280, 800));
    }
}
