using System;
using Avalonia;

namespace Aurora86.Gui.Controls;

/// <summary>
/// Fits an emulated display into the Avalonia control. Modern linear-color
/// scanout uses square pixels; historical VGA output keeps its 4:3 monitor
/// presentation even when the framebuffer itself is 640 by 400.
/// </summary>
internal readonly record struct DisplayViewport(double X, double Y, double Width, double Height)
{
    /// <summary>Finds the largest centered viewport that fits the available area.</summary>
    public static DisplayViewport Fit(
        Size available,
        int sourceWidth,
        int sourceHeight,
        bool squarePixels,
        double renderScaling = 1.0)
    {
        if (available.Width <= 0 || available.Height <= 0 || sourceWidth <= 0 || sourceHeight <= 0
            || renderScaling <= 0)
        {
            return default;
        }

        double aspect = squarePixels ? (double)sourceWidth / sourceHeight : 4.0 / 3.0;
        double width = available.Width;
        double height = width / aspect;

        if (height > available.Height)
        {
            height = available.Height;
            width = height * aspect;
        }

        // Keep one physical output pixel per source pixel whenever the full
        // image fits. Integer physical dimensions preserve nearest-neighbour
        // sharpness; only downscale when the available area cannot fit 1x.
        if (squarePixels)
        {
            double physicalScale = Math.Min(
                available.Width * renderScaling / sourceWidth,
                available.Height * renderScaling / sourceHeight);
            int integerScale = (int)Math.Floor(physicalScale);
            if (integerScale >= 1)
            {
                double physicalWidth = sourceWidth * (double)integerScale;
                double physicalHeight = sourceHeight * (double)integerScale;
                width = physicalWidth / renderScaling;
                height = physicalHeight / renderScaling;

                // Center on whole physical pixels too: a half-pixel origin
                // would undo the crisp sizing when renderScaling is applied.
                double leftPixels = Math.Floor(
                    (available.Width * renderScaling - physicalWidth) / 2.0);
                double topPixels = Math.Floor(
                    (available.Height * renderScaling - physicalHeight) / 2.0);
                return new DisplayViewport(leftPixels / renderScaling,
                                           topPixels / renderScaling,
                                           width,
                                           height);
            }
        }

        return new DisplayViewport((available.Width - width) / 2,
                                   (available.Height - height) / 2,
                                   width,
                                   height);
    }

    /// <summary>Maps a viewport position to the source pixel containing it.</summary>
    public (int X, int Y) MapToSource(Point position, int sourceWidth, int sourceHeight)
    {
        if (Width <= 0 || Height <= 0 || sourceWidth <= 0 || sourceHeight <= 0)
        {
            return (0, 0);
        }

        int x = (int)Math.Floor((position.X - X) / Width * sourceWidth);
        int y = (int)Math.Floor((position.Y - Y) / Height * sourceHeight);
        return (x, y);
    }

    public Rect ToRect() => new(X, Y, Width, Height);
}
