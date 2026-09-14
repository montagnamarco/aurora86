using System.Buffers.Binary;

namespace Aurora386.Core.Devices.Video;

public sealed partial class S3VirgeCard
{
    private const uint EngineBundleMagic = 0x31454756; // VGE1, emulator snapshot envelope.
    private const uint LegacyTriangleMagic = 0x31475256; // Original VRG1 triangle payload.
    private const ushort EngineBundleVersion = 1;
    private const int EngineBundleHeaderSize = 16;

    /// <summary>
    /// Captures the independent 1996 ViRGE triangle and BitBLT engines,
    /// including a partially supplied CPU image. VRAM belongs to the machine
    /// snapshot and is deliberately not duplicated in this envelope.
    /// </summary>
    public byte[] CaptureEngines()
    {
        byte[] triangle = Engine.Capture();
        byte[] blitter = Blitter.Capture();
        byte[] snapshot = new byte[EngineBundleHeaderSize + triangle.Length + blitter.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(snapshot, EngineBundleMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(snapshot.AsSpan(4), EngineBundleVersion);
        // Bytes 6 and 7 are reserved and remain zero.
        BinaryPrimitives.WriteInt32LittleEndian(snapshot.AsSpan(8), triangle.Length);
        BinaryPrimitives.WriteInt32LittleEndian(snapshot.AsSpan(12), blitter.Length);
        triangle.CopyTo(snapshot, EngineBundleHeaderSize);
        blitter.CopyTo(snapshot, EngineBundleHeaderSize + triangle.Length);
        return snapshot;
    }

    /// <summary>
    /// Validates both engine payloads before any machine state changes.
    /// Original VRG1-only snapshots remain valid and imply a reset blitter.
    /// </summary>
    public static void ValidateEngineSnapshot(ReadOnlySpan<byte> snapshot)
    {
        if (IsLegacyTriangleSnapshot(snapshot))
        {
            S3VirgeEngine.ValidateSnapshot(snapshot);
            return;
        }

        if (snapshot.Length < EngineBundleHeaderSize
            || BinaryPrimitives.ReadUInt32LittleEndian(snapshot) != EngineBundleMagic
            || BinaryPrimitives.ReadUInt16LittleEndian(snapshot[4..]) != EngineBundleVersion
            || BinaryPrimitives.ReadUInt16LittleEndian(snapshot[6..]) != 0)
        {
            throw new ArgumentException("Invalid ViRGE engine bundle header.", nameof(snapshot));
        }

        int triangleLength = BinaryPrimitives.ReadInt32LittleEndian(snapshot[8..]);
        int blitterLength = BinaryPrimitives.ReadInt32LittleEndian(snapshot[12..]);
        if (triangleLength <= 0 || blitterLength <= 0
            || (long)EngineBundleHeaderSize + triangleLength + blitterLength != snapshot.Length)
        {
            throw new ArgumentException("Invalid ViRGE engine bundle lengths.", nameof(snapshot));
        }

        S3VirgeEngine.ValidateSnapshot(snapshot.Slice(EngineBundleHeaderSize, triangleLength));
        S3Virge2DEngine.ValidateSnapshot(snapshot[(EngineBundleHeaderSize + triangleLength)..]);
    }

    /// <summary>
    /// Restores latches and input position without executing a command or
    /// replaying already consumed pixels. Legacy snapshots cancel any upload
    /// active in the target because that state did not exist in their model.
    /// </summary>
    public void RestoreEngines(ReadOnlySpan<byte> snapshot)
    {
        ValidateEngineSnapshot(snapshot);
        if (IsLegacyTriangleSnapshot(snapshot))
        {
            Engine.Restore(snapshot);
            Blitter.Reset();
            return;
        }

        int triangleLength = BinaryPrimitives.ReadInt32LittleEndian(snapshot[8..]);
        Engine.Restore(snapshot.Slice(EngineBundleHeaderSize, triangleLength));
        Blitter.Restore(snapshot[(EngineBundleHeaderSize + triangleLength)..]);
    }

    private static bool IsLegacyTriangleSnapshot(ReadOnlySpan<byte> snapshot)
    {
        return snapshot.Length >= sizeof(uint)
            && BinaryPrimitives.ReadUInt32LittleEndian(snapshot) == LegacyTriangleMagic;
    }
}
