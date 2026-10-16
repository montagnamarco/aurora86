using System.Buffers.Binary;

namespace Aurora86.Core.Devices.Video;

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

    /// <summary>
    /// The version of the "AGPU" snapshot section this build writes. Version 2
    /// appends the host preferred size and its generation after video memory;
    /// version 1, written before the host could suggest a size, is still read.
    /// </summary>
    public const int SnapshotVersion = 2;

    /// <summary>The version that predates the host preferred size.</summary>
    public const int LegacySnapshotVersion = 1;

    private const int SnapshotRegisterCount = 18;
    private const int SnapshotBackendKindRegister = 15;
    private const int SnapshotBackendOperationsRegister = 16;
    private const int SnapshotHostPreferredFields = 3;
    private const int SnapshotSurfaceFields = 5;
    private const int SnapshotClipFields = 5;
    private const int SnapshotCursorFields = 5;
    private const int CursorPixelCount = AuroraGpuInterface.CursorSize * AuroraGpuInterface.CursorSize;

    /// <summary>The fixed length of a version-1 payload.</summary>
    public const int LegacySnapshotLength =
        sizeof(uint)
        + SnapshotRegisterCount * sizeof(uint)
        + AuroraGpuInterface.SurfaceCount * SnapshotSurfaceFields * sizeof(uint)
        + SnapshotClipFields * sizeof(uint)
        + SnapshotCursorFields * sizeof(uint)
        + CursorPixelCount * sizeof(uint)
        + (int)AuroraGpuInterface.VideoMemorySize;

    /// <summary>The fixed length of a version-2 payload: version 1, then width, height and generation.</summary>
    public const int SnapshotLength = LegacySnapshotLength + SnapshotHostPreferredFields * sizeof(uint);

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
        writer.Write(_reportedBackendKind);
        writer.Write(_reportedBackendOperations);
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

        // The guest reads these registers, so a restore must give back the
        // same values; the front end's next publication then moves the
        // generation past the recorded one if the host window differs.
        lock (_hostPreferredSizeGate)
        {
            writer.Write(_hostPreferredWidth);
            writer.Write(_hostPreferredHeight);
            writer.Write(_hostPreferredGeneration);
        }
        return payload;
    }

    /// <summary>
    /// Checks a payload's shape without touching the card: its length, its
    /// magic word, the booleans, and every defined surface lying inside video
    /// memory, which the 2D engine relies on when it slices memory.
    /// </summary>
    public static void ValidateSnapshot(ReadOnlySpan<byte> payload, int version = SnapshotVersion)
    {
        if (version != SnapshotVersion && version != LegacySnapshotVersion)
        {
            throw new InvalidDataException($"Aurora Display Adapter snapshot version {version} is not supported.");
        }

        int expectedLength = version == SnapshotVersion ? SnapshotLength : LegacySnapshotLength;
        if (payload.Length != expectedLength)
        {
            throw new InvalidDataException("Aurora Display Adapter snapshot has the wrong length.");
        }

        var reader = new PayloadReader(payload);
        if (reader.ReadUInt32() != SnapshotMagic)
        {
            throw new InvalidDataException("Aurora Display Adapter snapshot header is invalid.");
        }

        reader.Skip(SnapshotBackendKindRegister * sizeof(uint));
        uint backendKind = reader.ReadUInt32();
        uint backendOperations = reader.ReadUInt32();
        if (backendKind > AuroraGpuInterface.BackendKindVulkan ||
            (backendOperations & ~AuroraGpuInterface.BackendOperationsKnown) != 0u ||
            (backendKind == AuroraGpuInterface.BackendKindSoftware && backendOperations != 0u))
        {
            throw new InvalidDataException("Aurora Display Adapter snapshot backend is invalid.");
        }
        reader.Skip((SnapshotRegisterCount - SnapshotBackendOperationsRegister - 1) * sizeof(uint));

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

        if (version == SnapshotVersion)
        {
            var preferred = new PayloadReader(payload[LegacySnapshotLength..]);
            uint width = preferred.ReadUInt32();
            uint height = preferred.ReadUInt32();
            bool cleared = width == 0 && height == 0;
            bool inRange = width is >= 1 and <= AuroraGpuInterface.MaximumHostPreferredWidth
                && height is >= 1 and <= AuroraGpuInterface.MaximumHostPreferredHeight;
            if (!cleared && !inRange)
            {
                throw new InvalidDataException("Aurora Display Adapter snapshot host preferred size is out of range.");
            }
        }
    }

    /// <summary>
    /// Restores a validated payload. The interrupt line is re-derived from
    /// status and mask, and the picture is repainted whole. A version-1
    /// payload describes a machine on which no host size was ever published,
    /// so the preferred size comes back cleared at generation 0.
    /// </summary>
    public void Restore(ReadOnlySpan<byte> payload, int version = SnapshotVersion)
    {
        ValidateSnapshot(payload, version);

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
        _reportedBackendKind = reader.ReadUInt32();
        _reportedBackendOperations = reader.ReadUInt32();
        reader.Skip(sizeof(uint));

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

        lock (_hostPreferredSizeGate)
        {
            bool recorded = version == SnapshotVersion;
            _hostPreferredWidth = recorded ? reader.ReadUInt32() : 0u;
            _hostPreferredHeight = recorded ? reader.ReadUInt32() : 0u;
            _hostPreferredGeneration = recorded ? reader.ReadUInt32() : 0u;
        }

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
