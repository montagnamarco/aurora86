using System.Buffers.Binary;

namespace Aurora386.Core.Devices.Video;

public sealed partial class S3VirgeCard
{
    private const uint EngineBundleMagic = 0x31454756; // VGE1, emulator snapshot envelope.
    private const uint LegacyTriangleMagic = 0x31475256; // Original VRG1 triangle payload.
    private const ushort LegacyEngineBundleVersion = 1;
    private const ushort EngineBundleVersion = 2;
    private const int LegacyEngineBundleHeaderSize = 16;
    private const int EngineBundleHeaderSize = 20;

    /// <summary>
    /// Captures the 1996 ViRGE triangle and BitBLT engines and primary Streams
    /// latches, including a partially supplied CPU image. VRAM belongs to the machine
    /// snapshot and is deliberately not duplicated in this envelope.
    /// </summary>
    public byte[] CaptureEngines()
    {
        byte[] triangle = Engine.Capture();
        byte[] blitter = Blitter.Capture();
        byte[] streams = Streams.Capture();
        byte[] snapshot = new byte[EngineBundleHeaderSize + triangle.Length + blitter.Length + streams.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(snapshot, EngineBundleMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(snapshot.AsSpan(4), EngineBundleVersion);
        // Bytes 6 and 7 are reserved and remain zero.
        BinaryPrimitives.WriteInt32LittleEndian(snapshot.AsSpan(8), triangle.Length);
        BinaryPrimitives.WriteInt32LittleEndian(snapshot.AsSpan(12), blitter.Length);
        BinaryPrimitives.WriteInt32LittleEndian(snapshot.AsSpan(16), streams.Length);
        triangle.CopyTo(snapshot, EngineBundleHeaderSize);
        blitter.CopyTo(snapshot, EngineBundleHeaderSize + triangle.Length);
        streams.CopyTo(snapshot, EngineBundleHeaderSize + triangle.Length + blitter.Length);
        return snapshot;
    }

    /// <summary>
    /// Validates every video payload before any machine state changes.
    /// Original VRG1-only snapshots imply a reset blitter and Streams block;
    /// VGE1 version 1 bundles imply reset Streams latches only.
    /// </summary>
    public static void ValidateEngineSnapshot(ReadOnlySpan<byte> snapshot)
    {
        if (IsLegacyTriangleSnapshot(snapshot))
        {
            S3VirgeEngine.ValidateSnapshot(snapshot);
            return;
        }

        if (snapshot.Length < LegacyEngineBundleHeaderSize
            || BinaryPrimitives.ReadUInt32LittleEndian(snapshot) != EngineBundleMagic
            || BinaryPrimitives.ReadUInt16LittleEndian(snapshot[6..]) != 0)
        {
            throw new ArgumentException("Invalid ViRGE engine bundle header.", nameof(snapshot));
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(snapshot[4..]);
        int headerSize = HeaderSize(version);
        if (snapshot.Length < headerSize)
        {
            throw new ArgumentException("Truncated ViRGE engine bundle header.", nameof(snapshot));
        }

        int triangleLength = BinaryPrimitives.ReadInt32LittleEndian(snapshot[8..]);
        int blitterLength = BinaryPrimitives.ReadInt32LittleEndian(snapshot[12..]);
        int streamsLength = version == EngineBundleVersion
            ? BinaryPrimitives.ReadInt32LittleEndian(snapshot[16..]) : 0;
        if (triangleLength <= 0 || blitterLength <= 0
            || (version == EngineBundleVersion && streamsLength <= 0)
            || (long)headerSize + triangleLength + blitterLength + streamsLength != snapshot.Length)
        {
            throw new ArgumentException("Invalid ViRGE engine bundle lengths.", nameof(snapshot));
        }

        S3VirgeEngine.ValidateSnapshot(snapshot.Slice(headerSize, triangleLength));
        S3Virge2DEngine.ValidateSnapshot(snapshot.Slice(headerSize + triangleLength, blitterLength));
        if (version == EngineBundleVersion)
        {
            S3VirgeStreams.ValidateSnapshot(snapshot[(headerSize + triangleLength + blitterLength)..]);
        }
    }

    /// <summary>
    /// Restores latches and input position without executing a command or
    /// replaying already consumed pixels. Triangle-only snapshots cancel an
    /// upload active in the target. All pre-Streams formats reset its latches.
    /// </summary>
    public void RestoreEngines(ReadOnlySpan<byte> snapshot)
    {
        ValidateEngineSnapshot(snapshot);
        if (IsLegacyTriangleSnapshot(snapshot))
        {
            Engine.Restore(snapshot);
            Blitter.Reset();
            Streams.Reset();
            return;
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(snapshot[4..]);
        int headerSize = HeaderSize(version);
        int triangleLength = BinaryPrimitives.ReadInt32LittleEndian(snapshot[8..]);
        int blitterLength = BinaryPrimitives.ReadInt32LittleEndian(snapshot[12..]);
        Engine.Restore(snapshot.Slice(headerSize, triangleLength));
        Blitter.Restore(snapshot.Slice(headerSize + triangleLength, blitterLength));
        if (version == EngineBundleVersion)
        {
            Streams.Restore(snapshot[(headerSize + triangleLength + blitterLength)..]);
        }
        else
        {
            Streams.Reset();
        }
    }

    /// <summary>Distinguishes legacy two-engine envelopes from Streams-aware captures.</summary>
    private static int HeaderSize(ushort version)
    {
        return version switch
        {
            LegacyEngineBundleVersion => LegacyEngineBundleHeaderSize,
            EngineBundleVersion => EngineBundleHeaderSize,
            _ => throw new ArgumentException("Invalid ViRGE engine bundle version.", nameof(version))
        };
    }

    private static bool IsLegacyTriangleSnapshot(ReadOnlySpan<byte> snapshot)
    {
        return snapshot.Length >= sizeof(uint)
            && BinaryPrimitives.ReadUInt32LittleEndian(snapshot) == LegacyTriangleMagic;
    }
}
