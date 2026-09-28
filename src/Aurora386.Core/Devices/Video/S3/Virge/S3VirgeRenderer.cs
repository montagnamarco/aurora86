namespace Aurora386.Core.Devices.Video;

/// <summary>
/// Presents the primary Streams Processor of the original 1996 S3 ViRGE
/// 86C325, alongside its VGA raster and hardware cursor. Period S3d software
/// programs MM8180-MM81F4 and CR67 to select a VRAM surface and display
/// window; this renderer preserves the measured unscaled KRGB1555 subset.
/// </summary>
public sealed class S3VirgeRenderer : S3VgaRenderer
{
    private readonly S3VirgeStreams _streams;
    private ulong _lastStreamsRevision;
    private byte _lastStreamsMode;
    private bool _fullStreamsMode;

    public S3VirgeRenderer(VgaMemory memory, S3VgaRegisters registers, S3VirgeStreams streams)
        : base(memory, registers)
    {
        ArgumentNullException.ThrowIfNull(streams);
        _streams = streams;
        _lastStreamsRevision = streams.Revision;
        _lastStreamsMode = (byte)(registers.Crtc(0x67) & 0x0C);
    }

    protected override bool RequiresFullScanout()
    {
        byte streamsMode = (byte)(Registers.Crtc(0x67) & 0x0C);
        ulong streamsRevision = _streams.Revision;
        _fullStreamsMode = streamsMode == 0x0C;
        bool streamsModeChanged = streamsMode != _lastStreamsMode;
        bool streamsRegistersChanged = _fullStreamsMode && streamsRevision != _lastStreamsRevision;
        _lastStreamsMode = streamsMode;
        _lastStreamsRevision = streamsRevision;

        // Streams addresses can change the visible page without touching
        // VRAM. Conversely, VRAM's dirty rows are calculated from the VGA
        // origin and pitch, which do not describe an off-screen Streams page.
        // Repaint the complete primary window for either kind of change.
        return streamsModeChanged || streamsRegistersChanged || (_fullStreamsMode && Memory.HasChanges);
    }

    protected override bool RenderCardScanout(bool force)
    {
        if (!_fullStreamsMode)
        {
            return false;
        }

        RenderPrimaryStream();
        return true;
    }

    /// <summary>
    /// Presents the measured ViRGE primary-stream subset. A full Streams mode
    /// with another format or filter is deliberately black: falling through
    /// to VBE would display a surface the programmed hardware did not select.
    /// </summary>
    private void RenderPrimaryStream()
    {
        Array.Fill(Pixels, 0xFF000000u);
        S3VirgeStreams streams = _streams;
        if (!streams.HasSupportedPrimaryFormat)
        {
            return;
        }

        (int windowX, int windowY) = streams.PrimaryWindowOrigin;
        (int windowWidth, int windowHeight) = streams.PrimaryWindowSize;
        int firstScreenX = Math.Max(0, windowX);
        int firstScreenY = Math.Max(0, windowY);
        int lastScreenX = Math.Min(Width, windowX + windowWidth);
        int lastScreenY = Math.Min(Height, windowY + windowHeight);

        for (int screenY = firstScreenY; screenY < lastScreenY; screenY++)
        {
            int sourceY = screenY - windowY;
            ulong sourceRow = streams.PrimaryBufferAddress
                + (ulong)sourceY * (uint)streams.PrimaryStride;
            int destination = screenY * Width;

            for (int screenX = firstScreenX; screenX < lastScreenX; screenX++)
            {
                int sourceX = screenX - windowX;
                ulong pixelAddress = sourceRow + (ulong)sourceX * sizeof(ushort);
                if (pixelAddress + 1 >= VgaMemory.TotalSize)
                {
                    continue;
                }

                int pixel = Memory.ReadLinear((uint)pixelAddress)
                    | (Memory.ReadLinear((uint)pixelAddress + 1) << 8);
                Pixels[destination + screenX] = From15(pixel);
            }
        }
    }
}
