namespace Aurora386.Core.Devices.Video;

/// <summary>
/// The pixels and timing controls a frontend consumes from a video device.
/// The buffer uses the emulator's 0xAABBGGRR packing and remains valid until
/// the next render or mode resize.
/// </summary>
public interface IVideoOutput
{
    int Width { get; }

    int Height { get; }

    ReadOnlySpan<uint> Framebuffer { get; }

    bool CursorBlinkOn { get; set; }

    bool Render(bool force = false);
}
