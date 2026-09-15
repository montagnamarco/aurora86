using System.Buffers.Binary;

namespace Aurora386.Core.Devices.System;

/// <summary>
/// Saves the AT-era 8237 pair between the port writes and DMA requests issued
/// by DOS drivers. Restore installs raw state while device execution is stopped.
/// </summary>
public sealed partial class Dma8237
{
    private const uint SnapshotMagic = 0x31414D44; // "DMA1"
    private const ushort SnapshotVersion = 1;
    private const ushort SnapshotChannelCount = 8;
    private const int SnapshotHeaderSize = sizeof(uint) + sizeof(ushort) + sizeof(ushort);
    private const int SnapshotChannelSize = sizeof(ushort) * 4 + sizeof(byte) * 4;
    // Offsets within each channel record, after its four 16-bit registers.
    private const int PageOffset = 8;
    private const int ModeOffset = 9;
    private const int MaskedOffset = 10;
    private const int TerminalCountOffset = 11;
    private const int SnapshotFlipFlopSize = sizeof(byte) * 2;
    private const int SnapshotSize = SnapshotHeaderSize + SnapshotChannelCount * SnapshotChannelSize + SnapshotFlipFlopSize;

    /// <summary>
    /// Captures the two 8237 controllers' channel registers and byte-pointer
    /// flip-flops in a fixed little-endian payload. Memory, requests and bus
    /// registrations remain owned by the target machine and are excluded.
    /// </summary>
    public byte[] Capture()
    {
        byte[] snapshot = new byte[SnapshotSize];
        Span<byte> data = snapshot;

        BinaryPrimitives.WriteUInt32LittleEndian(data, SnapshotMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(data[sizeof(uint)..], SnapshotVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(
            data[(sizeof(uint) + sizeof(ushort))..], SnapshotChannelCount);

        int offset = SnapshotHeaderSize;
        foreach (Channel channel in _channels)
        {
            WriteChannel(data.Slice(offset, SnapshotChannelSize), channel);
            offset += SnapshotChannelSize;
        }

        data[offset] = ToSnapshotBoolean(_highByte8);
        data[offset + 1] = ToSnapshotBoolean(_highByte16);
        return snapshot;
    }

    /// <summary>
    /// Validates a fixed DMA payload without changing channel state. Boolean
    /// fields are deliberately strict so corrupted snapshots cannot acquire
    /// an ambiguous byte-pointer or mask state.
    /// </summary>
    public static void ValidateSnapshot(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length != SnapshotSize)
        {
            throw new InvalidDataException("DMA snapshot length is invalid.");
        }

        ReadOnlySpan<byte> snapshot = data;
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(snapshot);
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(snapshot[sizeof(uint)..]);
        ushort channelCount = BinaryPrimitives.ReadUInt16LittleEndian(
            snapshot[(sizeof(uint) + sizeof(ushort))..]);
        if (magic != SnapshotMagic || version != SnapshotVersion || channelCount != SnapshotChannelCount)
        {
            throw new InvalidDataException("DMA snapshot header is invalid.");
        }

        int offset = SnapshotHeaderSize;
        for (int channel = 0; channel < SnapshotChannelCount; channel++)
        {
            ValidateSnapshotBoolean(snapshot[offset + MaskedOffset]);
            ValidateSnapshotBoolean(snapshot[offset + TerminalCountOffset]);
            offset += SnapshotChannelSize;
        }

        ValidateSnapshotBoolean(snapshot[offset]);
        ValidateSnapshotBoolean(snapshot[offset + 1]);
    }

    /// <summary>
    /// Restores channel registers and byte-pointer flip-flops after complete
    /// validation. It does not transfer data, read memory, or issue I/O.
    /// </summary>
    public void Restore(byte[] data)
    {
        ValidateSnapshot(data);

        ReadOnlySpan<byte> snapshot = data;
        int offset = SnapshotHeaderSize;
        foreach (Channel channel in _channels)
        {
            RestoreChannel(snapshot.Slice(offset, SnapshotChannelSize), channel);
            offset += SnapshotChannelSize;
        }

        _highByte8 = snapshot[offset] != 0;
        _highByte16 = snapshot[offset + 1] != 0;
    }

    /// <summary>
    /// Returns the captured fields to the raw power-on defaults without
    /// replacing channels referenced by attached devices or test code.
    /// </summary>
    public void ResetSnapshotState()
    {
        foreach (Channel channel in _channels)
        {
            channel.BaseAddress = 0;
            channel.BaseCount = 0;
            channel.CurrentAddress = 0;
            channel.CurrentCount = 0;
            channel.Page = 0;
            channel.Mode = 0;
            channel.Masked = true;
            channel.TerminalCount = false;
        }

        _highByte8 = false;
        _highByte16 = false;
    }

    private static void WriteChannel(Span<byte> data, Channel channel)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(data, channel.BaseAddress);
        BinaryPrimitives.WriteUInt16LittleEndian(data[sizeof(ushort)..], channel.BaseCount);
        BinaryPrimitives.WriteUInt16LittleEndian(data[(sizeof(ushort) * 2)..], channel.CurrentAddress);
        BinaryPrimitives.WriteUInt16LittleEndian(data[(sizeof(ushort) * 3)..], channel.CurrentCount);
        data[PageOffset] = channel.Page;
        data[ModeOffset] = channel.Mode;
        data[MaskedOffset] = ToSnapshotBoolean(channel.Masked);
        data[TerminalCountOffset] = ToSnapshotBoolean(channel.TerminalCount);
    }

    private static void RestoreChannel(ReadOnlySpan<byte> data, Channel channel)
    {
        channel.BaseAddress = BinaryPrimitives.ReadUInt16LittleEndian(data);
        channel.BaseCount = BinaryPrimitives.ReadUInt16LittleEndian(data[sizeof(ushort)..]);
        channel.CurrentAddress = BinaryPrimitives.ReadUInt16LittleEndian(data[(sizeof(ushort) * 2)..]);
        channel.CurrentCount = BinaryPrimitives.ReadUInt16LittleEndian(data[(sizeof(ushort) * 3)..]);
        channel.Page = data[PageOffset];
        channel.Mode = data[ModeOffset];
        channel.Masked = data[MaskedOffset] != 0;
        channel.TerminalCount = data[TerminalCountOffset] != 0;
    }

    private static byte ToSnapshotBoolean(bool value) => value ? (byte)1 : (byte)0;

    private static void ValidateSnapshotBoolean(byte value)
    {
        if (value > 1)
        {
            throw new InvalidDataException("DMA snapshot boolean is invalid.");
        }
    }
}
