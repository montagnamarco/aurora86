namespace Aurora386.Core.Devices.Video;

/// <summary>
/// The command ring of the Aurora Display Adapter (docs/AURORA-GPU.md
/// sections 4-6): a circular buffer in guest memory that the driver fills
/// and the card reads by bus mastering. Writing RING_TAIL is the doorbell.
///
/// The emulator executes every command up to the new tail before the
/// doorbell write returns. The interface does not promise that, and a driver
/// must still wait on fences; doing it synchronously keeps the machine
/// deterministic, which is what a snapshot and a reproducible trace need.
/// </summary>
public sealed partial class AuroraGpuCard
{
    private const int CommandHeaderLengthShift = 16;
    private const uint CommandHeaderOpcodeMask = 0xFFFF;

    /// <summary>The longest command any opcode needs; longer commands' extra dwords are skipped, not read.</summary>
    private const int MaximumRequiredLength = 11;

    private uint _ringBase;
    private uint _ringSize;
    private uint _ringHead;
    private uint _ringTail;
    private uint _ringControl;
    private uint _fence;
    private uint _error;
    private uint _errorOffset;

    private readonly AuroraGpuSurface[] _surfaces = new AuroraGpuSurface[AuroraGpuInterface.SurfaceCount];
    private readonly uint[] _cursorImage = new uint[AuroraGpuInterface.CursorSize * AuroraGpuInterface.CursorSize];

    public bool CursorVisible { get; private set; }
    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    public int CursorHotSpotX { get; private set; }
    public int CursorHotSpotY { get; private set; }

    /// <summary>The 64x64 premultiplied cursor image, row after row.</summary>
    public ReadOnlySpan<uint> CursorImage => _cursorImage;

    private void ResetRing()
    {
        _ringBase = 0;
        _ringSize = 0;
        _ringHead = 0;
        _ringTail = 0;
        _ringControl = 0;
        _fence = 0;
        _error = AuroraGpuInterface.ErrorNone;
        _errorOffset = 0;
    }

    private void ResetDrawingState()
    {
        Array.Clear(_surfaces);
        Array.Clear(_cursorImage);
        CursorVisible = false;
        CursorX = 0;
        CursorY = 0;
        CursorHotSpotX = 0;
        CursorHotSpotY = 0;
        Engine.ClipEnabled = false;
        Engine.ClipX = 0;
        Engine.ClipY = 0;
        Engine.ClipWidth = 0;
        Engine.ClipHeight = 0;
    }

    /// <summary>
    /// RING_CONTROL: bit 0 enables the ring; writing bit 1 empties it and
    /// clears a pending error, which is the only way out of the stopped state
    /// an invalid command leaves behind.
    /// </summary>
    private void WriteRingControl(uint value)
    {
        if ((value & AuroraGpuInterface.RingControlReset) != 0)
        {
            _ringHead = 0;
            _ringTail = 0;
            _error = AuroraGpuInterface.ErrorNone;
            _errorOffset = 0;
        }

        _ringControl = value & AuroraGpuInterface.RingControlEnable;
    }

    /// <summary>
    /// Executes commands from the head to the tail, stopping before the first
    /// invalid one. Nothing happens while the ring is disabled, while bus
    /// mastering is off, or while an earlier error has not been cleared.
    /// </summary>
    private void ProcessRing()
    {
        bool enabled = (_ringControl & AuroraGpuInterface.RingControlEnable) != 0;
        if (!enabled || !Function.BusMasterEnabled || _error != AuroraGpuInterface.ErrorNone)
        {
            return;
        }

        if (!RingConfigurationValid())
        {
            StopWithError(AuroraGpuInterface.ErrorBadRing);
            return;
        }

        // A driver that shrank RING_SIZE may have left the head beyond the
        // new end; offsets are defined modulo the size.
        uint mask = _ringSize - 1;
        uint tail = _ringTail & mask;
        _ringHead &= mask;
        Span<uint> command = stackalloc uint[MaximumRequiredLength];

        while (_ringHead != tail)
        {
            uint header = ReadRingDword(_ringHead);
            ushort opcode = (ushort)(header & CommandHeaderOpcodeMask);
            int length = (int)(header >> CommandHeaderLengthShift);
            int required = AuroraGpuInterface.RequiredLength(opcode);

            if (required == 0)
            {
                StopWithError(AuroraGpuInterface.ErrorUnknownOpcode);
                return;
            }

            uint available = (tail - _ringHead) & mask;
            bool lengthValid = length >= required && (uint)length * sizeof(uint) <= available;
            if (!lengthValid)
            {
                StopWithError(AuroraGpuInterface.ErrorBadLength);
                return;
            }

            for (int index = 0; index < required; index++)
            {
                command[index] = ReadRingDword((_ringHead + (uint)(index * sizeof(uint))) & mask);
            }

            uint error = Execute(opcode, command[..required]);
            if (error != AuroraGpuInterface.ErrorNone)
            {
                StopWithError(error);
                return;
            }

            _ringHead = (_ringHead + (uint)length * sizeof(uint)) & mask;
        }
    }

    private bool RingConfigurationValid()
    {
        bool sizeIsPowerOfTwo = _ringSize != 0 && (_ringSize & (_ringSize - 1)) == 0;
        bool sizeInRange = _ringSize is >= AuroraGpuInterface.MinimumRingSize and <= AuroraGpuInterface.MaximumRingSize;
        bool baseAligned = _ringBase % AuroraGpuInterface.RingBaseAlignment == 0;
        bool tailAligned = _ringTail % sizeof(uint) == 0;
        return sizeIsPowerOfTwo && sizeInRange && baseAligned && tailAligned;
    }

    private uint ReadRingDword(uint offset)
    {
        return ReadGuestDword(_ringBase + offset);
    }

    private void StopWithError(uint error)
    {
        _error = error;
        _errorOffset = _ringHead;
        RaiseInterrupt(AuroraGpuInterface.InterruptCommandError);
    }

    /// <summary>Runs one command whose dwords, header included, are in <paramref name="command"/>.</summary>
    private uint Execute(ushort opcode, ReadOnlySpan<uint> command)
    {
        switch (opcode)
        {
            case AuroraGpuInterface.OpcodeNop:
                return AuroraGpuInterface.ErrorNone;

            case AuroraGpuInterface.OpcodeFence:
                return ExecuteFence(command);

            case AuroraGpuInterface.OpcodeDefineSurface:
                return ExecuteDefineSurface(command);

            case AuroraGpuInterface.OpcodeSetClip:
                return ExecuteSetClip(command);

            case AuroraGpuInterface.OpcodeFillRect:
                return ExecuteFillRect(command);

            case AuroraGpuInterface.OpcodeCopyRect:
                return ExecuteCopyRect(command);

            case AuroraGpuInterface.OpcodeBlitImage:
                return ExecuteBlitImage(command);

            case AuroraGpuInterface.OpcodeDrawMask:
                return ExecuteDrawMask(command);

            case AuroraGpuInterface.OpcodeDrawLine:
                return ExecuteDrawLine(command);

            case AuroraGpuInterface.OpcodeCursorImage:
                return ExecuteCursorImage(command);

            case AuroraGpuInterface.OpcodeCursorMove:
                return ExecuteCursorMove(command);

            case AuroraGpuInterface.OpcodePresent:
                return ExecutePresent(command);

            default:
                return AuroraGpuInterface.ErrorUnknownOpcode;
        }
    }

    // ---- individual commands --------------------------------------------------------------
    // Field positions follow the table in docs/AURORA-GPU.md section 5; index
    // 0 is always the header.

    /// <summary>FENCE: flags, value.</summary>
    private uint ExecuteFence(ReadOnlySpan<uint> command)
    {
        uint flags = command[1];
        _fence = command[2];
        if ((flags & AuroraGpuInterface.FlagFenceInterrupt) != 0)
        {
            RaiseInterrupt(AuroraGpuInterface.InterruptFence);
        }

        return AuroraGpuInterface.ErrorNone;
    }

    /// <summary>DEFINE_SURFACE: id, VRAM offset, pitch, width, height.</summary>
    private uint ExecuteDefineSurface(ReadOnlySpan<uint> command)
    {
        uint id = command[1];
        uint offset = command[2];
        uint pitch = command[3];
        uint width = command[4];
        uint height = command[5];

        if (id == 0 || id >= AuroraGpuInterface.SurfaceCount)
        {
            return AuroraGpuInterface.ErrorBadSurface;
        }

        bool dimensionsValid = width is >= 1 and <= AuroraGpuInterface.MaximumDisplayDimension
            && height is >= 1 and <= AuroraGpuInterface.MaximumDisplayDimension;
        if (!dimensionsValid)
        {
            return AuroraGpuInterface.ErrorBadSurfaceDefinition;
        }

        bool layoutValid = offset % BytesPerPixel == 0
            && pitch % BytesPerPixel == 0
            && pitch >= width * BytesPerPixel;
        long end = offset + (long)(height - 1) * pitch + (long)width * BytesPerPixel;
        if (!layoutValid || end > _videoMemoryBytes.Length)
        {
            return AuroraGpuInterface.ErrorBadSurfaceDefinition;
        }

        _surfaces[id] = new AuroraGpuSurface(true, offset, pitch, (int)width, (int)height);
        return AuroraGpuInterface.ErrorNone;
    }

    /// <summary>SET_CLIP: flags, x, y, width, height.</summary>
    private uint ExecuteSetClip(ReadOnlySpan<uint> command)
    {
        Engine.ClipEnabled = (command[1] & AuroraGpuInterface.FlagClipEnabled) != 0;
        Engine.ClipX = (int)command[2];
        Engine.ClipY = (int)command[3];
        Engine.ClipWidth = (int)command[4];
        Engine.ClipHeight = (int)command[5];
        return AuroraGpuInterface.ErrorNone;
    }

    /// <summary>FILL_RECT: surface, flags, x, y, width, height, colour.</summary>
    private uint ExecuteFillRect(ReadOnlySpan<uint> command)
    {
        if (!TryGetSurface(command[1], out AuroraGpuSurface target))
        {
            return AuroraGpuInterface.ErrorBadSurface;
        }

        bool blend = (command[2] & AuroraGpuInterface.FlagBlend) != 0;
        Engine.FillRect(target, (int)command[3], (int)command[4], (int)command[5], (int)command[6], command[7], blend);
        return AuroraGpuInterface.ErrorNone;
    }

    /// <summary>COPY_RECT: flags, source surface, source x, source y, destination surface, x, y, width, height.</summary>
    private uint ExecuteCopyRect(ReadOnlySpan<uint> command)
    {
        if (!TryGetSurface(command[2], out AuroraGpuSurface source) || !TryGetSurface(command[5], out AuroraGpuSurface target))
        {
            return AuroraGpuInterface.ErrorBadSurface;
        }

        bool blend = (command[1] & AuroraGpuInterface.FlagBlend) != 0;
        Engine.CopyRect(
            source,
            (int)command[3],
            (int)command[4],
            target,
            (int)command[6],
            (int)command[7],
            (int)command[8],
            (int)command[9],
            blend);
        return AuroraGpuInterface.ErrorNone;
    }

    /// <summary>BLIT_IMAGE: flags, source space, source address, source pitch, destination surface, x, y, width, height.</summary>
    private uint ExecuteBlitImage(ReadOnlySpan<uint> command)
    {
        if (!TryGetSurface(command[5], out AuroraGpuSurface target))
        {
            return AuroraGpuInterface.ErrorBadSurface;
        }

        bool blend = (command[1] & AuroraGpuInterface.FlagBlend) != 0;
        return Engine.BlitImage(
            command[2],
            command[3],
            command[4],
            target,
            (int)command[6],
            (int)command[7],
            (int)command[8],
            (int)command[9],
            blend);
    }

    /// <summary>DRAW_MASK: flags, source space, source address, source pitch, destination surface, x, y, width, height, colour.</summary>
    private uint ExecuteDrawMask(ReadOnlySpan<uint> command)
    {
        if (!TryGetSurface(command[5], out AuroraGpuSurface target))
        {
            return AuroraGpuInterface.ErrorBadSurface;
        }

        return Engine.DrawMask(
            command[2],
            command[3],
            command[4],
            target,
            (int)command[6],
            (int)command[7],
            (int)command[8],
            (int)command[9],
            command[10]);
    }

    /// <summary>DRAW_LINE: surface, flags, x0, y0, x1, y1, colour.</summary>
    private uint ExecuteDrawLine(ReadOnlySpan<uint> command)
    {
        if (!TryGetSurface(command[1], out AuroraGpuSurface target))
        {
            return AuroraGpuInterface.ErrorBadSurface;
        }

        bool blend = (command[2] & AuroraGpuInterface.FlagBlend) != 0;
        return Engine.DrawLine(target, (int)command[3], (int)command[4], (int)command[5], (int)command[6], command[7], blend);
    }

    /// <summary>CURSOR_IMAGE: source space, source address, hot spot x, hot spot y.</summary>
    private uint ExecuteCursorImage(ReadOnlySpan<uint> command)
    {
        uint error = Engine.ReadCursorImage(command[1], command[2], _cursorImage);
        if (error != AuroraGpuInterface.ErrorNone)
        {
            return error;
        }

        CursorHotSpotX = (int)Math.Min(command[3], AuroraGpuInterface.CursorSize - 1);
        CursorHotSpotY = (int)Math.Min(command[4], AuroraGpuInterface.CursorSize - 1);
        MarkDisplayChanged();
        return AuroraGpuInterface.ErrorNone;
    }

    /// <summary>CURSOR_MOVE: flags, x, y.</summary>
    private uint ExecuteCursorMove(ReadOnlySpan<uint> command)
    {
        // No repaint is requested here: the scanout compares the cursor's
        // position itself and redraws only the rows it left and entered.
        CursorVisible = (command[1] & AuroraGpuInterface.FlagCursorVisible) != 0;
        CursorX = (int)command[2];
        CursorY = (int)command[3];
        return AuroraGpuInterface.ErrorNone;
    }

    /// <summary>PRESENT: flags, display offset.</summary>
    private uint ExecutePresent(ReadOnlySpan<uint> command)
    {
        if ((command[1] & AuroraGpuInterface.FlagPresentSetOffset) != 0)
        {
            _displayOffset = command[2];
        }

        MarkDisplayChanged();
        return AuroraGpuInterface.ErrorNone;
    }

    /// <summary>Surface 0 is the displayed frame; 1-15 are whatever DEFINE_SURFACE left there.</summary>
    private bool TryGetSurface(uint id, out AuroraGpuSurface surface)
    {
        if (id >= AuroraGpuInterface.SurfaceCount)
        {
            surface = default;
            return false;
        }

        surface = id == 0 ? DisplaySurface : _surfaces[id];
        return surface.Defined;
    }
}
