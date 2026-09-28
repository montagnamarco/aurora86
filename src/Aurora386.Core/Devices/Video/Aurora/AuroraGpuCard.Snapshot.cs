using System.Buffers.Binary;

namespace Aurora386.Core.Devices.Video;

/// <summary>
/// The Aurora Display Adapter's part of a machine snapshot: every register a
/// driver can read or that decides what is drawn next, the surfaces, the
/// clip rectangle, the cursor, and the whole of video memory.
///
/// The PCI configuration -- command register and both BARs -- is not here:
/// it belongs to the PCI section, and restoring it moves the BAR decoders on
/// its own. The legacy VGA registers and memory are the VREG and VMEM
/// sections, as for every other card.
/// </summary>
public sealed partial class AuroraGpuCard
{
    /// <summary>"AGP1" read as a little-endian dword.</summary>
    private const uint SnapshotMagic = 0x31504741;

    /// <summary>The version of the "AGPU" snapshot section this build writes and reads.</summary>
    public const int SnapshotVersion = 1;

    private const int SnapshotRegisterCount = 18;
    private const int SnapshotSurfaceFields = 5;
    private const int SnapshotClipFields = 5;
    private const int SnapshotCursorFields = 5;
    private const int CursorPixelCount = AuroraGpuInterface.CursorSize * AuroraGpuInterface.CursorSize;

    /// <summary>The fixed length of a version-1 payload.</summary>
    public const int SnapshotLength =
        sizeof(uint)
        + SnapshotRegisterCount * sizeof(uint)
        + AuroraGpuInterface.SurfaceCount * SnapshotSurfaceFields * sizeof(uint)
        + SnapshotClipFields * sizeof(uint)
        + SnapshotCursorFields * sizeof(uint)
        + CursorPixelCount * sizeof(uint)
        + (int)AuroraGpuInterface.VideoMemorySize;

    /// <summary>Captures the card while the machine is stopped.</summary>
    public byte[] Capture()
    {
        byte[] payload = new byte[SnapshotLength];
        var writer = new PayloadWriter(payload);

        writer.Write(SnapshotMagic);

        writer.Write(_displayControl);
        writer.Write(_displayWidth);
        writer.Write(_displayHeight);
        writer.Write(_displayPitch);
        writer.Write(_displayOffset);
        writer.Write(_interruptStatus);
        writer.Write(_interruptMask);
        writer.Write(_ringBase);
        writer.Write(_ringSize);
        writer.Write(_ringHead);
        writer.Write(_ringTail);
        writer.Write(_ringControl);
        writer.Write(_fence);
        writer.Write(_error);
        writer.Write(_errorOffset);
        writer.Write(0u);   // reserved
        writer.Write(0u);   // reserved
        writer.Write(0u);   // reserved

        foreach (AuroraGpuSurface surface in _surfaces)
        {
            writer.Write(surface.Defined ? 1u : 0u);
            writer.Write(surface.Offset);
            writer.Write(surface.Pitch);
            writer.Write((uint)surface.Width);
            writer.Write((uint)surface.Height);
        }

        writer.Write(Engine.ClipEnabled ? 1u : 0u);
        writer.Write((uint)Engine.ClipX);
        writer.Write((uint)Engine.ClipY);
        writer.Write((uint)Engine.ClipWidth);
        writer.Write((uint)Engine.ClipHeight);

        writer.Write(CursorVisible ? 1u : 0u);
        writer.Write((uint)CursorX);
        writer.Write((uint)CursorY);
        writer.Write((uint)CursorHotSpotX);
        writer.Write((uint)CursorHotSpotY);
        foreach (uint pixel in _cursorImage)
        {
            writer.Write(pixel);
        }

        writer.Write(_videoMemoryBytes);
        return payload;
    }

    /// <summary>
    /// Checks a payload's shape without touching the card: its length, its
    /// magic word, the booleans, and every defined surface lying inside video
    /// memory, which the 2D engine relies on when it slices memory.
    /// </summary>
    public static void ValidateSnapshot(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != SnapshotLength)
        {
            throw new InvalidDataException("Aurora Display Adapter snapshot has the wrong length.");
        }

        var reader = new PayloadReader(payload);
        if (reader.ReadUInt32() != SnapshotMagic)
        {
            throw new InvalidDataException("Aurora Display Adapter snapshot header is invalid.");
        }

        reader.Skip(SnapshotRegisterCount * sizeof(uint));

        for (int index = 0; index < AuroraGpuInterface.SurfaceCount; index++)
        {
            uint defined = reader.ReadUInt32();
            uint offset = reader.ReadUInt32();
            uint pitch = reader.ReadUInt32();
            uint width = reader.ReadUInt32();
            uint height = reader.ReadUInt32();
            ValidateSnapshotBoolean(defined);

            bool surfaceZeroDefined = index == 0 && defined != 0;
            if (surfaceZeroDefined || (defined != 0 && !SurfaceFits(offset, pitch, width, height)))
            {
                throw new InvalidDataException("Aurora Display Adapter snapshot holds an invalid surface.");
            }
        }

        ValidateSnapshotBoolean(reader.ReadUInt32());
        reader.Skip((SnapshotClipFields - 1) * sizeof(uint));

        ValidateSnapshotBoolean(reader.ReadUInt32());
        reader.Skip(2 * sizeof(uint));
        uint hotSpotX = reader.ReadUInt32();
        uint hotSpotY = reader.ReadUInt32();
        if (hotSpotX >= AuroraGpuInterface.CursorSize || hotSpotY >= AuroraGpuInterface.CursorSize)
        {
            throw new InvalidDataException("Aurora Display Adapter snapshot cursor hot spot is out of range.");
        }
    }

    /// <summary>
    /// Restores a validated payload. The interrupt line is re-derived from
    /// status and mask, and the picture is repainted whole.
    /// </summary>
    public void Restore(ReadOnlySpan<byte> payload)
    {
        ValidateSnapshot(payload);

        var reader = new PayloadReader(payload);
        reader.ReadUInt32();

        _displayControl = reader.ReadUInt32();
        _displayWidth = reader.ReadUInt32();
        _displayHeight = reader.ReadUInt32();
        _displayPitch = reader.ReadUInt32();
        _displayOffset = reader.ReadUInt32();
        _interruptStatus = reader.ReadUInt32();
        _interruptMask = reader.ReadUInt32();
        _ringBase = reader.ReadUInt32();
        _ringSize = reader.ReadUInt32();
        _ringHead = reader.ReadUInt32();
        _ringTail = reader.ReadUInt32();
        _ringControl = reader.ReadUInt32();
        _fence = reader.ReadUInt32();
        _error = reader.ReadUInt32();
        _errorOffset = reader.ReadUInt32();
        reader.Skip(3 * sizeof(uint));

        for (int index = 0; index < AuroraGpuInterface.SurfaceCount; index++)
        {
            bool defined = reader.ReadUInt32() != 0;
            uint offset = reader.ReadUInt32();
            uint pitch = reader.ReadUInt32();
            int width = (int)reader.ReadUInt32();
            int height = (int)reader.ReadUInt32();
            _surfaces[index] = new AuroraGpuSurface(defined, offset, pitch, width, height);
        }

        Engine.ClipEnabled = reader.ReadUInt32() != 0;
        Engine.ClipX = (int)reader.ReadUInt32();
        Engine.ClipY = (int)reader.ReadUInt32();
        Engine.ClipWidth = (int)reader.ReadUInt32();
        Engine.ClipHeight = (int)reader.ReadUInt32();

        CursorVisible = reader.ReadUInt32() != 0;
        CursorX = (int)reader.ReadUInt32();
        CursorY = (int)reader.ReadUInt32();
        CursorHotSpotX = (int)reader.ReadUInt32();
        CursorHotSpotY = (int)reader.ReadUInt32();
        for (int index = 0; index < CursorPixelCount; index++)
        {
            _cursorImage[index] = reader.ReadUInt32();
        }

        reader.ReadBytes(_videoMemoryBytes);

        UpdateInterrupt();
        MarkDisplayChanged();
    }

    private static bool SurfaceFits(uint offset, uint pitch, uint width, uint height)
    {
        bool dimensionsValid = width is >= 1 and <= AuroraGpuInterface.MaximumDisplayDimension
            && height is >= 1 and <= AuroraGpuInterface.MaximumDisplayDimension;
        if (!dimensionsValid)
        {
            return false;
        }

        bool layoutValid = offset % BytesPerPixel == 0 && pitch % BytesPerPixel == 0 && pitch >= width * BytesPerPixel;
        long end = offset + (long)(height - 1) * pitch + (long)width * BytesPerPixel;
        return layoutValid && end <= AuroraGpuInterface.VideoMemorySize;
    }

    private static void ValidateSnapshotBoolean(uint value)
    {
        if (value > 1)
        {
            throw new InvalidDataException("Aurora Display Adapter snapshot boolean is invalid.");
        }
    }

    /// <summary>Sequential little-endian writes into a preallocated payload.</summary>
    private ref struct PayloadWriter(Span<byte> payload)
    {
        private readonly Span<byte> _payload = payload;
        private int _position;

        public void Write(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_payload[_position..], value);
            _position += sizeof(uint);
        }

        public void Write(ReadOnlySpan<byte> bytes)
        {
            bytes.CopyTo(_payload[_position..]);
            _position += bytes.Length;
        }
    }

    /// <summary>Sequential little-endian reads from a payload whose length has been checked.</summary>
    private ref struct PayloadReader(ReadOnlySpan<byte> payload)
    {
        private readonly ReadOnlySpan<byte> _payload = payload;
        private int _position;

        public uint ReadUInt32()
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_payload[_position..]);
            _position += sizeof(uint);
            return value;
        }

        public void ReadBytes(Span<byte> destination)
        {
            _payload.Slice(_position, destination.Length).CopyTo(destination);
            _position += destination.Length;
        }

        public void Skip(int count)
        {
            _position += count;
        }
    }
}
