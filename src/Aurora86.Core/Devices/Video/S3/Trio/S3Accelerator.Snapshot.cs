using System.Buffers.Binary;

namespace Aurora86.Core.Devices.Video;

/// <summary>
/// Saves the 1995 Trio64V+ accelerator latches and unfinished processor uploads
/// used by Windows display drivers, without executing register writes on restore.
/// </summary>
public sealed partial class S3Accelerator
{
    private const uint SnapshotMagic = 0x31443354; // T3D1
    private const ushort SnapshotVersion = 1;
    private const int SnapshotHeaderLength = 8;
    private const int PendingEntryLength = 3; // Port WORD and low byte.
    private const int MaximumPendingPorts = 32768; // Every even 16-bit I/O address.
    internal const int MaximumSnapshotLength = SnapshotHeaderLength
        + (int)Field.Count * sizeof(uint) + sizeof(int) + MaximumPendingPorts * PendingEntryLength;

    // Fixed DWORD slots in T3D1 v1; additions require a new version.
    private enum Field
    {
        CurrentX,
        CurrentY,
        DestinationX,
        DestinationY,
        Width,
        Height,
        Foreground,
        Background,
        WriteMask,
        WriteMaskWritten,
        ReadMask,
        ColourCompare,
        Command,
        Mix,
        BackgroundMix,
        ClipTop,
        ClipLeft,
        ClipBottom,
        ClipRight,
        PixelControl,
        ReadSelect,
        MultiMisc,
        MultiMisc2,
        PatternX,
        PatternY,
        LastMultifunction,
        Unit,
        UnitBytes,
        ProcessorPixel,
        ProcessorPixelBytes,
        TransferPresent,
        TransferLeft,
        TransferTop,
        TransferWidth,
        TransferHeight,
        TransferRightwards,
        TransferDownwards,
        TransferMask,
        TransferColumn,
        TransferRow,
        Count,
    }

    /// <summary>Captures latches, transfer progress and pending low-byte writes.</summary>
    public byte[] Capture()
    {
        uint[] values =
        [
            _currentX,
            _currentY,
            _destinationX,
            _destinationY,
            _width,
            _height,
            _foreground,
            _background,
            _writeMask,
            _writeMaskWritten ? 1u : 0u,
            _readMask,
            _colourCompare,
            _command,
            _mix,
            _backgroundMix,
            _clipTop,
            _clipLeft,
            _clipBottom,
            _clipRight,
            _pixelControl,
            (uint)_readSelect,
            _multiMisc,
            _multiMisc2,
            _patternX,
            _patternY,
            _lastMultifunctionCommand,
            _unit,
            (uint)_unitBytes,
            _processorPixel,
            (uint)_processorPixelBytes,
            _transfer is null ? 0u : 1u,
            0, 0, 0, 0, 0, 0, 0, 0, 0,
        ];
        _transfer?.Capture().CopyTo(values, (int)Field.TransferLeft);
        int fixedLength = SnapshotHeaderLength + (int)Field.Count * sizeof(uint);
        byte[] result = new byte[fixedLength + sizeof(int) + _pendingLow.Count * PendingEntryLength];
        BinaryPrimitives.WriteUInt32LittleEndian(result, SnapshotMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), SnapshotVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6), (ushort)Field.Count);
        for (int index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(SnapshotHeaderLength + index * 4), values[index]);
        }
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(fixedLength), _pendingLow.Count);
        int offset = fixedLength + sizeof(int);
        foreach (var entry in _pendingLow.OrderBy(entry => entry.Key))
        {
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(offset), entry.Key);
            result[offset + 2] = entry.Value;
            offset += PendingEntryLength;
        }
        return result;
    }

    /// <summary>Validates the whole payload before any device or memory mutation.</summary>
    public static void ValidateSnapshot(ReadOnlySpan<byte> snapshot)
    {
        _ = Decode(snapshot);
    }

    private static uint[] Decode(ReadOnlySpan<byte> snapshot)
    {
        int fixedLength = SnapshotHeaderLength + (int)Field.Count * sizeof(uint);
        if (snapshot.Length < fixedLength + sizeof(int)
            || BinaryPrimitives.ReadUInt32LittleEndian(snapshot) != SnapshotMagic
            || BinaryPrimitives.ReadUInt16LittleEndian(snapshot[4..]) != SnapshotVersion
            || BinaryPrimitives.ReadUInt16LittleEndian(snapshot[6..]) != (ushort)Field.Count)
        {
            throw new InvalidDataException("Invalid Trio accelerator snapshot header.");
        }
        int count = BinaryPrimitives.ReadInt32LittleEndian(snapshot[fixedLength..]);
        if (count < 0 || count > MaximumPendingPorts
            || snapshot.Length != fixedLength + sizeof(int) + count * PendingEntryLength)
        {
            throw new InvalidDataException("Invalid Trio pending-byte snapshot length.");
        }
        int previousPort = -1;
        for (int index = 0; index < count; index++)
        {
            int offset = fixedLength + sizeof(int) + index * PendingEntryLength;
            ushort port = BinaryPrimitives.ReadUInt16LittleEndian(snapshot[offset..]);
            if ((port & 1) != 0 || port <= previousPort)
            {
                throw new InvalidDataException("Invalid or duplicate Trio pending-byte port.");
            }
            previousPort = port;
        }
        uint[] values = new uint[(int)Field.Count];
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = BinaryPrimitives.ReadUInt32LittleEndian(snapshot[(SnapshotHeaderLength + index * 4)..]);
        }
        Field[] wordFields =
        [
            Field.CurrentX, Field.CurrentY, Field.DestinationX, Field.DestinationY,
            Field.Width, Field.Height, Field.Command, Field.Mix,
            Field.BackgroundMix, Field.ClipTop, Field.ClipLeft, Field.ClipBottom,
            Field.ClipRight, Field.PixelControl, Field.MultiMisc, Field.MultiMisc2,
            Field.PatternX, Field.PatternY, Field.LastMultifunction,
        ];
        foreach (Field field in wordFields)
        {
            if (values[(int)field] > ushort.MaxValue)
            {
                throw new InvalidDataException("Trio word register exceeds 16 bits.");
            }
        }
        Field[] booleanFields = [Field.WriteMaskWritten, Field.TransferPresent,
            Field.TransferRightwards, Field.TransferDownwards, Field.TransferMask];
        foreach (Field field in booleanFields)
        {
            if (values[(int)field] > 1)
            {
                throw new InvalidDataException("Invalid Trio snapshot boolean.");
            }
        }
        if (values[(int)Field.ReadSelect] > 15
            || values[(int)Field.UnitBytes] > 3
            || values[(int)Field.ProcessorPixelBytes] > 3)
        {
            throw new InvalidDataException("Invalid Trio byte or register cursor.");
        }
        if (values[(int)Field.TransferPresent] != 0)
        {
            uint width = values[(int)Field.TransferWidth];
            uint height = values[(int)Field.TransferHeight];
            if (values[(int)Field.TransferLeft] > ushort.MaxValue
                || values[(int)Field.TransferTop] > ushort.MaxValue
                || width is 0 or > 65536 || height is 0 or > 65536
                || values[(int)Field.TransferColumn] >= width
                || values[(int)Field.TransferRow] >= height)
            {
                throw new InvalidDataException("Invalid Trio transfer geometry or cursor.");
            }
        }
        else if (values.AsSpan((int)Field.TransferLeft).ContainsAnyExcept(0u))
        {
            throw new InvalidDataException("Absent Trio transfer contains geometry.");
        }
        return values;
    }

    /// <summary>Restores raw state without drawing, advancing data or invoking callbacks.</summary>
    public void Restore(ReadOnlySpan<byte> snapshot)
    {
        uint[] values = Decode(snapshot);
        _currentX = (ushort)values[(int)Field.CurrentX];
        _currentY = (ushort)values[(int)Field.CurrentY];
        _destinationX = (ushort)values[(int)Field.DestinationX];
        _destinationY = (ushort)values[(int)Field.DestinationY];
        _width = (ushort)values[(int)Field.Width];
        _height = (ushort)values[(int)Field.Height];
        _foreground = (uint)values[(int)Field.Foreground];
        _background = (uint)values[(int)Field.Background];
        _writeMask = (uint)values[(int)Field.WriteMask];
        _writeMaskWritten = values[(int)Field.WriteMaskWritten] != 0;
        _readMask = (uint)values[(int)Field.ReadMask];
        _colourCompare = (uint)values[(int)Field.ColourCompare];
        _command = (ushort)values[(int)Field.Command];
        _mix = (ushort)values[(int)Field.Mix];
        _backgroundMix = (ushort)values[(int)Field.BackgroundMix];
        _clipTop = (ushort)values[(int)Field.ClipTop];
        _clipLeft = (ushort)values[(int)Field.ClipLeft];
        _clipBottom = (ushort)values[(int)Field.ClipBottom];
        _clipRight = (ushort)values[(int)Field.ClipRight];
        _pixelControl = (ushort)values[(int)Field.PixelControl];
        _readSelect = (int)values[(int)Field.ReadSelect];
        _multiMisc = (ushort)values[(int)Field.MultiMisc];
        _multiMisc2 = (ushort)values[(int)Field.MultiMisc2];
        _patternX = (ushort)values[(int)Field.PatternX];
        _patternY = (ushort)values[(int)Field.PatternY];
        _lastMultifunctionCommand = (ushort)values[(int)Field.LastMultifunction];
        _unit = (uint)values[(int)Field.Unit];
        _unitBytes = (int)values[(int)Field.UnitBytes];
        _processorPixel = (uint)values[(int)Field.ProcessorPixel];
        _processorPixelBytes = (int)values[(int)Field.ProcessorPixelBytes];
        _transfer = values[(int)Field.TransferPresent] == 0 ? null : new Transfer(
            (int)values[(int)Field.TransferLeft], (int)values[(int)Field.TransferTop],
            (int)values[(int)Field.TransferWidth], (int)values[(int)Field.TransferHeight],
            values[(int)Field.TransferRightwards] != 0, values[(int)Field.TransferDownwards] != 0,
            values[(int)Field.TransferMask] != 0,
            (int)values[(int)Field.TransferColumn], (int)values[(int)Field.TransferRow]);
        _pendingLow.Clear();
        int offset = SnapshotHeaderLength + (int)Field.Count * sizeof(uint) + sizeof(int);
        while (offset < snapshot.Length)
        {
            _pendingLow.Add(BinaryPrimitives.ReadUInt16LittleEndian(snapshot[offset..]), snapshot[offset + 2]);
            offset += PendingEntryLength;
        }
    }

    /// <summary>Clears state missing from historical snapshots, preserving host diagnostics.</summary>
    public void ResetSnapshotState()
    {
        Restore(new S3Accelerator(_memory, _registers).Capture());
    }
}
