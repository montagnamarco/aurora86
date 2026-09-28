using System.Buffers.Binary;

namespace Aurora386.Core.Devices.Audio;

/// <summary>
/// Preserves the CT1745 mixer settings selected by 1990s Sound Blaster drivers,
/// including an unfinished index/data pair and the DSP-owned resource latches.
/// </summary>
public sealed partial class SbMixer
{
    private const uint SnapshotMagic = 0x3158494D; // "MIX1"
    private const ushort SnapshotVersion = 1;
    private const ushort SnapshotRegisterCount = 256;
    private const int SnapshotIndexOffset = 8;
    private const int SnapshotStatusOffset = 9;
    private const int SnapshotIrqOffset = 10;
    private const int SnapshotDma8Offset = 14;
    private const int SnapshotDma16Offset = 18;
    private const int SnapshotRegistersOffset = 22;
    private const int SnapshotSize = SnapshotRegistersOffset + SnapshotRegisterCount;

    /// <summary>
    /// Captures raw latches while the caller has stopped audio/device execution.
    /// Resource values are stored as signed integers, matching the public owner
    /// properties; consistency with DSP/PnP belongs to machine-level preflight.
    /// </summary>
    public byte[] Capture()
    {
        byte[] data = new byte[SnapshotSize];
        BinaryPrimitives.WriteUInt32LittleEndian(data, SnapshotMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), SnapshotVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), SnapshotRegisterCount);
        data[SnapshotIndexOffset] = _index;
        data[SnapshotStatusOffset] = InterruptStatus;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(SnapshotIrqOffset), Irq);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(SnapshotDma8Offset), Dma8);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(SnapshotDma16Offset), Dma16);
        _registers.CopyTo(data, SnapshotRegistersOffset);
        return data;
    }

    /// <summary>
    /// Validates the complete standalone mixer payload without changing state.
    /// Register bytes, interrupt status and resource properties retain every
    /// value accepted by the current model instead of imposing new hardware rules.
    /// </summary>
    public static void ValidateSnapshot(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length != SnapshotSize)
        {
            throw new InvalidDataException("Mixer snapshot length is invalid.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(data) != SnapshotMagic ||
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)) != SnapshotVersion ||
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6)) != SnapshotRegisterCount)
        {
            throw new InvalidDataException("Mixer snapshot header is invalid.");
        }
    }

    /// <summary>
    /// Installs raw state after validation without replaying volume aliases,
    /// reset writes or IRQ acknowledgements. The caller keeps execution stopped.
    /// </summary>
    public void Restore(byte[] data)
    {
        ValidateSnapshot(data);
        _index = data[SnapshotIndexOffset];
        InterruptStatus = data[SnapshotStatusOffset];
        Irq = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(SnapshotIrqOffset));
        Dma8 = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(SnapshotDma8Offset));
        Dma16 = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(SnapshotDma16Offset));
        data.AsSpan(SnapshotRegistersOffset, SnapshotRegisterCount).CopyTo(_registers);
    }

    /// <summary>
    /// Restores constructed defaults, including owner properties and selection.
    /// Unlike a guest write to register 00h, this also clears pending IRQ status.
    /// </summary>
    public void ResetSnapshotState()
    {
        Reset();
        _index = 0;
        InterruptStatus = 0;
        Irq = 5;
        Dma8 = 1;
        Dma16 = 5;
    }
}
