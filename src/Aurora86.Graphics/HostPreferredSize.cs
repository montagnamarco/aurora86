namespace Aurora86.Graphics;

/// <summary>
/// Converts a frontend's logical client area into the Aurora adapter's advisory
/// display size. Keeping this policy shared preserves readable integer scaling
/// on high-DPI hosts and ignores transient minimised or undersized windows.
/// </summary>
public static class HostPreferredSize
{
    public const int MinimumWidth = 320;
    public const int MinimumHeight = 240;
    public const int MaximumWidth = 1920;
    public const int MaximumHeight = 1080;

    /// <summary>Returns a bounded whole-pixel preference without changing any device.</summary>
    public static bool TryFromLogical(double width, double height, bool minimized,
        out int preferredWidth, out int preferredHeight)
    {
        preferredWidth = 0;
        preferredHeight = 0;
        if (minimized || !double.IsFinite(width) || !double.IsFinite(height)
            || width < MinimumWidth || height < MinimumHeight)
        {
            return false;
        }

        // Clamp before conversion: even a finite value may exceed Int32.MaxValue.
        preferredWidth = (int)Math.Floor(Math.Min(width, MaximumWidth));
        preferredHeight = (int)Math.Floor(Math.Min(height, MaximumHeight));
        return true;
    }
}
