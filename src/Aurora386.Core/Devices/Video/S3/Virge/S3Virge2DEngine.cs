using System.Buffers.Binary;

namespace Aurora386.Core.Devices.Video;

/// <summary>
/// The synchronous 2D BitBLT block of S3's 1996 ViRGE 86C325 accelerator.
/// Period display drivers program its memory-mapped latches and either submit
/// a screen operation immediately or stream DWORD-aligned image rows through
/// the CPU transfer aperture. This bounded implementation covers the measured
/// CUBE fill, screen-copy and 16-bit colour-upload commands; unsupported modes
/// are rejected before they can alter video memory.
/// </summary>
public sealed class S3Virge2DEngine
{
    private const uint SourceBaseRegister = 0xA4D4;
    private const uint DestinationBaseRegister = 0xA4D8;
    private const uint ClipLeftRightRegister = 0xA4DC;
    private const uint ClipTopBottomRegister = 0xA4E0;
    private const uint StrideRegister = 0xA4E4;
    private const uint MonoPattern0Register = 0xA4E8;
    private const uint MonoPattern1Register = 0xA4EC;
    private const uint PatternBackgroundRegister = 0xA4F0;
    private const uint PatternForegroundRegister = 0xA4F4;
    private const uint SourceBackgroundRegister = 0xA4F8;
    private const uint SourceForegroundRegister = 0xA4FC;
    private const uint CommandRegister = 0xA500;
    private const uint WidthHeightRegister = 0xA504;
    private const uint SourceCoordinatesRegister = 0xA508;
    private const uint DestinationCoordinatesRegister = 0xA50C;

    private const uint AutoExecute = 1u << 0;
    private const uint HardwareClipping = 1u << 1;
    private const uint DrawEnable = 1u << 5;
    private const uint MonoSource = 1u << 6;
    private const uint ImageDataSource = 1u << 7;
    private const uint MonoPattern = 1u << 8;
    private const uint Transparent = 1u << 9;
    private const int ImageAlignmentShift = 10;
    private const int FirstDwordOffsetShift = 12;
    private const int RasterOperationShift = 17;
    private const int XPositiveBit = 25;
    private const int YPositiveBit = 26;
    private const int CommandTypeShift = 27;
    private const uint DestinationFormatMask = 0x1Cu;
    private const uint EightBitFormat = 0;
    private const uint SixteenBitFormat = 1;
    private const uint SourceCopyRasterOperation = 0xCC;
    private const uint PatternCopyRasterOperation = 0xF0;
    private const uint BitBltCommand = 0;
    private const uint RectangleFillCommand = 2;
    private const uint NopCommand = 15;
    private const uint DwordImageAlignment = 2;

    private const uint SnapshotMagic = 0x31443256; // V2D1.
    private const ushort SnapshotVersion = 1;
    private const ushort SnapshotRegisterCount = 15;
    private const int SnapshotHeaderSize = 8;
    private const int SnapshotTransferFieldCount = 11;
    private const int SnapshotSize = SnapshotHeaderSize
        + SnapshotRegisterCount * sizeof(uint)
        + SnapshotTransferFieldCount * sizeof(uint);

    private static readonly uint[] SnapshotRegisters =
    [
        SourceBaseRegister,
        DestinationBaseRegister,
        ClipLeftRightRegister,
        ClipTopBottomRegister,
        StrideRegister,
        MonoPattern0Register,
        MonoPattern1Register,
        PatternBackgroundRegister,
        PatternForegroundRegister,
        SourceBackgroundRegister,
        SourceForegroundRegister,
        CommandRegister,
        WidthHeightRegister,
        SourceCoordinatesRegister,
        DestinationCoordinatesRegister
    ];

    private readonly VgaMemory _memory;
    private readonly Dictionary<uint, uint> _registers = new();
    private ImageTransfer? _imageTransfer;

    public S3Virge2DEngine(VgaMemory memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        _memory = memory;
    }

    /// <summary>
    /// Reports whether the engine is waiting for more CPU image DWORDs.
    /// Screen copies and fills finish synchronously during their command write.
    /// </summary>
    public bool IsBusy => _imageTransfer is not null;

    /// <summary>
    /// Reports a command outside the measured subset. The notification is an
    /// emulator diagnostic and does not model a guest-visible hardware fault.
    /// </summary>
    public event Action<uint>? UnsupportedCommandIssued;

    /// <summary>Identifies the fifteen aligned DWORD latches in the BitBLT register block.</summary>
    public static bool IsRegister(uint register)
    {
        return register >= SourceBaseRegister
            && register <= DestinationCoordinatesRegister
            && (register & 3) == 0;
    }

    /// <summary>Returns a masked register latch, or an open-bus value outside this block.</summary>
    public uint ReadDword(uint register)
    {
        if (!IsRegister(register))
        {
            return uint.MaxValue;
        }

        // The databook leaves power-on contents undefined. Zero gives reset
        // and snapshots a deterministic value without claiming silicon state.
        return _registers.GetValueOrDefault(register);
    }

    /// <summary>
    /// Latches one DWORD and submits CMD_SET immediately. This bounded engine
    /// has no FIFO: a new command cancels an unfinished CPU upload before the
    /// replacement is decoded.
    /// </summary>
    public void WriteDword(uint register, uint value)
    {
        if (!IsRegister(register))
        {
            return;
        }

        _registers[register] = MaskRegister(register, value);
        if (register != CommandRegister)
        {
            return;
        }

        _imageTransfer = null;
        ExecuteCommand(ReadDword(CommandRegister));
    }

    /// <summary>
    /// Supplies four little-endian bytes to a supported CPU colour transfer.
    /// Pixels clipped from the destination are still consumed from the stream.
    /// </summary>
    public bool WriteImageDword(uint value)
    {
        ImageTransfer? transfer = _imageTransfer;
        if (transfer is null)
        {
            return false;
        }

        for (int byteOffset = 0; byteOffset < sizeof(uint); byteOffset += sizeof(ushort))
        {
            int lineOffset = transfer.ByteOffsetInRow + byteOffset;
            if (lineOffset >= transfer.PixelBytesPerRow)
            {
                continue;
            }

            int pixelIndex = lineOffset / sizeof(ushort);
            ushort pixel = (ushort)(value >> (byteOffset * 8));
            WriteUploadedPixel(transfer, pixelIndex, pixel);
        }

        transfer.ByteOffsetInRow += sizeof(uint);
        if (transfer.ByteOffsetInRow == transfer.AlignedBytesPerRow)
        {
            transfer.ByteOffsetInRow = 0;
            transfer.CurrentRow++;
            if (transfer.CurrentRow == transfer.Height)
            {
                _imageTransfer = null;
            }
        }

        return true;
    }

    /// <summary>
    /// Captures all programmed latches and the frozen parameters and cursor of
    /// an in-progress image transfer. Video memory remains machine-level state.
    /// </summary>
    public byte[] Capture()
    {
        byte[] snapshot = new byte[SnapshotSize];
        BinaryPrimitives.WriteUInt32LittleEndian(snapshot, SnapshotMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(snapshot.AsSpan(4), SnapshotVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(snapshot.AsSpan(6), SnapshotRegisterCount);

        int offset = SnapshotHeaderSize;
        foreach (uint register in SnapshotRegisters)
        {
            WriteSnapshotField(snapshot, ref offset, ReadDword(register));
        }

        ImageTransfer? transfer = _imageTransfer;
        WriteSnapshotField(snapshot, ref offset, transfer is null ? 0u : 1u);
        WriteSnapshotField(snapshot, ref offset, transfer?.Command ?? 0);
        WriteSnapshotField(snapshot, ref offset, transfer?.DestinationBase ?? 0);
        WriteSnapshotField(snapshot, ref offset, transfer?.DestinationStride ?? 0);
        WriteSnapshotField(snapshot, ref offset, transfer?.ClipLeftRight ?? 0);
        WriteSnapshotField(snapshot, ref offset, transfer?.ClipTopBottom ?? 0);
        WriteSnapshotField(snapshot, ref offset, transfer?.DestinationCoordinates ?? 0);
        WriteSnapshotField(snapshot, ref offset, (uint)(transfer?.Width ?? 0));
        WriteSnapshotField(snapshot, ref offset, (uint)(transfer?.Height ?? 0));
        WriteSnapshotField(snapshot, ref offset, (uint)(transfer?.CurrentRow ?? 0));
        WriteSnapshotField(snapshot, ref offset, (uint)(transfer?.ByteOffsetInRow ?? 0));
        return snapshot;
    }

    /// <summary>
    /// Validates a fixed snapshot payload without changing latches, emitting
    /// diagnostics, advancing an upload, or writing video memory.
    /// </summary>
    public static void ValidateSnapshot(ReadOnlySpan<byte> snapshot)
    {
        if (snapshot.Length < SnapshotHeaderSize)
        {
            throw new ArgumentException("ViRGE 2D snapshot header is truncated.", nameof(snapshot));
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(snapshot);
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(snapshot[4..]);
        ushort registerCount = BinaryPrimitives.ReadUInt16LittleEndian(snapshot[6..]);
        if (magic != SnapshotMagic
            || version != SnapshotVersion
            || registerCount != SnapshotRegisterCount
            || snapshot.Length != SnapshotSize)
        {
            throw new ArgumentException("ViRGE 2D snapshot header or length is invalid.", nameof(snapshot));
        }

        int offset = SnapshotHeaderSize;
        uint commandLatch = 0;
        foreach (uint register in SnapshotRegisters)
        {
            uint value = ReadSnapshotField(snapshot, ref offset);
            if (MaskRegister(register, value) != value)
            {
                throw new ArgumentException("ViRGE 2D snapshot contains non-canonical register bits.", nameof(snapshot));
            }

            if (register == CommandRegister)
            {
                commandLatch = value;
            }
        }

        uint active = ReadSnapshotField(snapshot, ref offset);
        uint command = ReadSnapshotField(snapshot, ref offset);
        uint destinationBase = ReadSnapshotField(snapshot, ref offset);
        uint destinationStride = ReadSnapshotField(snapshot, ref offset);
        uint clipLeftRight = ReadSnapshotField(snapshot, ref offset);
        uint clipTopBottom = ReadSnapshotField(snapshot, ref offset);
        uint destinationCoordinates = ReadSnapshotField(snapshot, ref offset);
        uint width = ReadSnapshotField(snapshot, ref offset);
        uint height = ReadSnapshotField(snapshot, ref offset);
        uint currentRow = ReadSnapshotField(snapshot, ref offset);
        uint byteOffsetInRow = ReadSnapshotField(snapshot, ref offset);

        bool inactiveFieldsAreZero = command == 0
            && destinationBase == 0
            && destinationStride == 0
            && clipLeftRight == 0
            && clipTopBottom == 0
            && destinationCoordinates == 0
            && width == 0
            && height == 0
            && currentRow == 0
            && byteOffsetInRow == 0;
        if (active == 0)
        {
            if (!inactiveFieldsAreZero)
            {
                throw new ArgumentException("Inactive ViRGE 2D transfer state is not empty.", nameof(snapshot));
            }

            return;
        }

        if (active != 1
            || !IsSupportedImageTransfer(command)
            || MaskRegister(CommandRegister, command) != command
            || commandLatch != command
            || MaskRegister(DestinationBaseRegister, destinationBase) != destinationBase
            || MaskRegister(StrideRegister, destinationStride << 16) >> 16 != destinationStride
            || MaskRegister(ClipLeftRightRegister, clipLeftRight) != clipLeftRight
            || MaskRegister(ClipTopBottomRegister, clipTopBottom) != clipTopBottom
            || MaskRegister(DestinationCoordinatesRegister, destinationCoordinates) != destinationCoordinates
            || width is 0 or > 2048
            || height is 0 or > 2047
            || currentRow >= height)
        {
            throw new ArgumentException("Active ViRGE 2D transfer state is invalid.", nameof(snapshot));
        }

        uint alignedBytesPerRow = (width * sizeof(ushort) + 3) & ~3u;
        if (byteOffsetInRow >= alignedBytesPerRow || (byteOffsetInRow & 3) != 0)
        {
            throw new ArgumentException("ViRGE 2D transfer cursor is invalid.", nameof(snapshot));
        }
    }

    /// <summary>
    /// Restores a previously validated payload without executing its command
    /// or redrawing pixels that the saved upload already consumed.
    /// </summary>
    public void Restore(ReadOnlySpan<byte> snapshot)
    {
        ValidateSnapshot(snapshot);

        int offset = SnapshotHeaderSize;
        var restoredRegisters = new uint[SnapshotRegisterCount];
        for (int index = 0; index < restoredRegisters.Length; index++)
        {
            restoredRegisters[index] = ReadSnapshotField(snapshot, ref offset);
        }

        bool active = ReadSnapshotField(snapshot, ref offset) != 0;
        uint command = ReadSnapshotField(snapshot, ref offset);
        uint destinationBase = ReadSnapshotField(snapshot, ref offset);
        uint destinationStride = ReadSnapshotField(snapshot, ref offset);
        uint clipLeftRight = ReadSnapshotField(snapshot, ref offset);
        uint clipTopBottom = ReadSnapshotField(snapshot, ref offset);
        uint destinationCoordinates = ReadSnapshotField(snapshot, ref offset);
        int width = (int)ReadSnapshotField(snapshot, ref offset);
        int height = (int)ReadSnapshotField(snapshot, ref offset);
        int currentRow = (int)ReadSnapshotField(snapshot, ref offset);
        int byteOffsetInRow = (int)ReadSnapshotField(snapshot, ref offset);

        _registers.Clear();
        for (int index = 0; index < SnapshotRegisters.Length; index++)
        {
            _registers[SnapshotRegisters[index]] = restoredRegisters[index];
        }

        _imageTransfer = active
            ? new ImageTransfer(
                command,
                destinationBase,
                destinationStride,
                clipLeftRight,
                clipTopBottom,
                destinationCoordinates,
                width,
                height,
                currentRow,
                byteOffsetInRow)
            : null;
    }

    /// <summary>Clears every latch and cancels any in-progress image transfer.</summary>
    public void Reset()
    {
        _registers.Clear();
        _imageTransfer = null;
    }

    /// <summary>Decodes one submitted command only after its full DWORD has been latched.</summary>
    private void ExecuteCommand(uint command)
    {
        uint commandType = (command >> CommandTypeShift) & 0xF;
        if (commandType == NopCommand)
        {
            return;
        }

        // Autoexecute would require a second trigger and FIFO semantics. It is
        // deliberately rejected here instead of firing at the wrong register.
        if ((command & AutoExecute) != 0)
        {
            UnsupportedCommandIssued?.Invoke(command);
            return;
        }

        if (IsSupportedRectangleFill(command))
        {
            ExecuteRectangleFill(command);
            return;
        }

        if (IsSupportedScreenCopy(command))
        {
            ExecuteScreenCopy(command);
            return;
        }

        if (IsSupportedImageTransfer(command))
        {
            BeginImageTransfer(command);
            return;
        }

        UnsupportedCommandIssued?.Invoke(command);
    }

    /// <summary>Recognizes PATCOPY rectangle fills whose foreground color is 16 bits wide.</summary>
    private static bool IsSupportedRectangleFill(uint command)
    {
        // XP and YP govern source traversal for overlapping BitBLTs. A fill
        // has no source rectangle, so both values leave its positive extent
        // unchanged; the observed command happens to set both bits.
        return IsTwoDimensionalCommand(command, RectangleFillCommand)
            && DestinationFormat(command) == SixteenBitFormat
            && RasterOperation(command) == PatternCopyRasterOperation
            && (command & (DrawEnable | MonoPattern)) == (DrawEnable | MonoPattern)
            && (command & (ImageDataSource | MonoSource | Transparent)) == 0
            && ImageAlignment(command) == 0
            && FirstDwordOffset(command) == 0;
    }

    /// <summary>Recognizes 8- and 16-bit screen-to-screen SRCCOPY operations.</summary>
    private static bool IsSupportedScreenCopy(uint command)
    {
        uint format = DestinationFormat(command);
        return IsTwoDimensionalCommand(command, BitBltCommand)
            && (format == EightBitFormat || format == SixteenBitFormat)
            && RasterOperation(command) == SourceCopyRasterOperation
            && (command & DrawEnable) != 0
            && (command & (ImageDataSource | MonoSource | MonoPattern)) == 0;
        // TP, FDO and ITA only affect image data according to section 19.3.
    }

    /// <summary>Recognizes the measured 16-bit, DWORD-row-aligned CPU colour upload.</summary>
    private static bool IsSupportedImageTransfer(uint command)
    {
        // The databook's upload example clears XP/YP while the measured CUBE
        // command sets them. CPU data still arrives in positive row order, so
        // treating these source-overlap controls as irrelevant reconciles both.
        return (command & AutoExecute) == 0
            && IsTwoDimensionalCommand(command, BitBltCommand)
            && DestinationFormat(command) == SixteenBitFormat
            && RasterOperation(command) == SourceCopyRasterOperation
            && (command & (DrawEnable | ImageDataSource)) == (DrawEnable | ImageDataSource)
            && (command & (MonoSource | MonoPattern | Transparent)) == 0
            && ImageAlignment(command) == DwordImageAlignment
            && FirstDwordOffset(command) == 0;
    }

    /// <summary>Checks the command selector and rejects the 3D register interpretation.</summary>
    private static bool IsTwoDimensionalCommand(uint command, uint expectedType)
    {
        bool threeDimensional = (command & 0x80000000) != 0;
        uint commandType = (command >> CommandTypeShift) & 0xF;
        return !threeDimensional && commandType == expectedType;
    }

    private static uint DestinationFormat(uint command)
    {
        return (command & DestinationFormatMask) >> 2;
    }

    private static uint RasterOperation(uint command)
    {
        return (command >> RasterOperationShift) & 0xFF;
    }

    private static uint ImageAlignment(uint command)
    {
        return (command >> ImageAlignmentShift) & 3;
    }

    private static uint FirstDwordOffset(uint command)
    {
        return (command >> FirstDwordOffsetShift) & 3;
    }

    /// <summary>Fills a positive-size rectangle with PAT_FG_CLR as required for rectangle fill.</summary>
    private void ExecuteRectangleFill(uint command)
    {
        (int width, int height) = RectangleDimensions();
        if (height == 0)
        {
            return;
        }

        uint destinationBase = ReadDword(DestinationBaseRegister);
        uint destinationStride = DestinationStride();
        uint destinationCoordinates = ReadDword(DestinationCoordinatesRegister);
        int destinationX = HorizontalCoordinate(destinationCoordinates);
        int destinationY = VerticalCoordinate(destinationCoordinates);
        ushort color = (ushort)ReadDword(PatternForegroundRegister);

        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
            {
                int x = destinationX + column;
                int y = destinationY + row;
                if (IsInsideClip(x, y, command))
                {
                    WritePixel(destinationBase, destinationStride, x, y, color, sizeof(ushort));
                }
            }
        }
    }

    /// <summary>Copies in the guest-selected order so correctly directed overlaps remain intact.</summary>
    private void ExecuteScreenCopy(uint command)
    {
        (int width, int height) = RectangleDimensions();
        if (height == 0)
        {
            return;
        }

        int bytesPerPixel = DestinationFormat(command) == EightBitFormat ? 1 : sizeof(ushort);
        int xStep = (command & (1u << XPositiveBit)) != 0 ? 1 : -1;
        int yStep = (command & (1u << YPositiveBit)) != 0 ? 1 : -1;
        uint sourceBase = ReadDword(SourceBaseRegister);
        uint destinationBase = ReadDword(DestinationBaseRegister);
        uint sourceStride = SourceStride();
        uint destinationStride = DestinationStride();
        uint sourceCoordinates = ReadDword(SourceCoordinatesRegister);
        uint destinationCoordinates = ReadDword(DestinationCoordinatesRegister);
        int sourceStartX = HorizontalCoordinate(sourceCoordinates);
        int sourceStartY = VerticalCoordinate(sourceCoordinates);
        int destinationStartX = HorizontalCoordinate(destinationCoordinates);
        int destinationStartY = VerticalCoordinate(destinationCoordinates);

        for (int row = 0; row < height; row++)
        {
            int sourceY = sourceStartY + row * yStep;
            int destinationY = destinationStartY + row * yStep;
            for (int column = 0; column < width; column++)
            {
                int sourceX = sourceStartX + column * xStep;
                int destinationX = destinationStartX + column * xStep;
                if (!IsInsideClip(destinationX, destinationY, command))
                {
                    continue;
                }

                CopyPixel(
                    sourceBase,
                    sourceStride,
                    sourceX,
                    sourceY,
                    destinationBase,
                    destinationStride,
                    destinationX,
                    destinationY,
                    bytesPerPixel);
            }
        }
    }

    /// <summary>Freezes every parameter that controls the following image-data stream.</summary>
    private void BeginImageTransfer(uint command)
    {
        (int width, int height) = RectangleDimensions();
        if (height == 0)
        {
            return;
        }

        _imageTransfer = new ImageTransfer(
            command,
            ReadDword(DestinationBaseRegister),
            DestinationStride(),
            ReadDword(ClipLeftRightRegister),
            ReadDword(ClipTopBottomRegister),
            ReadDword(DestinationCoordinatesRegister),
            width,
            height,
            currentRow: 0,
            byteOffsetInRow: 0);
    }

    /// <summary>Writes one consumed upload pixel if its frozen destination survives clipping.</summary>
    private void WriteUploadedPixel(ImageTransfer transfer, int pixelIndex, ushort pixel)
    {
        int destinationX = HorizontalCoordinate(transfer.DestinationCoordinates) + pixelIndex;
        int destinationY = VerticalCoordinate(transfer.DestinationCoordinates) + transfer.CurrentRow;
        if (!IsInsideClip(
                destinationX,
                destinationY,
                transfer.Command,
                transfer.ClipLeftRight,
                transfer.ClipTopBottom))
        {
            return;
        }

        WritePixel(
            transfer.DestinationBase,
            transfer.DestinationStride,
            destinationX,
            destinationY,
            pixel,
            sizeof(ushort));
    }

    /// <summary>Copies one complete pixel only when both linear addresses are in VRAM.</summary>
    private void CopyPixel(
        uint sourceBase,
        uint sourceStride,
        int sourceX,
        int sourceY,
        uint destinationBase,
        uint destinationStride,
        int destinationX,
        int destinationY,
        int bytesPerPixel)
    {
        long sourceAddress = PixelAddress(sourceBase, sourceStride, sourceX, sourceY, bytesPerPixel);
        long destinationAddress = PixelAddress(
            destinationBase,
            destinationStride,
            destinationX,
            destinationY,
            bytesPerPixel);
        if (!IsCompletePixelInVideoMemory(sourceAddress, bytesPerPixel)
            || !IsCompletePixelInVideoMemory(destinationAddress, bytesPerPixel))
        {
            return;
        }

        // At most two bytes are read before either is written, so a pixel
        // copied onto itself and adjacent overlap both retain the source value.
        byte low = _memory.ReadLinear((uint)sourceAddress);
        byte high = bytesPerPixel == sizeof(ushort)
            ? _memory.ReadLinear((uint)sourceAddress + 1)
            : (byte)0;
        _memory.WriteLinear((uint)destinationAddress, low);
        if (bytesPerPixel == sizeof(ushort))
        {
            _memory.WriteLinear((uint)destinationAddress + 1, high);
        }
    }

    /// <summary>Writes one 8- or 16-bit pixel without permitting a partial edge write.</summary>
    private void WritePixel(
        uint destinationBase,
        uint destinationStride,
        int x,
        int y,
        uint pixel,
        int bytesPerPixel)
    {
        long address = PixelAddress(destinationBase, destinationStride, x, y, bytesPerPixel);
        if (!IsCompletePixelInVideoMemory(address, bytesPerPixel))
        {
            return;
        }

        _memory.WriteLinear((uint)address, (byte)pixel);
        if (bytesPerPixel == sizeof(ushort))
        {
            _memory.WriteLinear((uint)address + 1, (byte)(pixel >> 8));
        }
    }

    private static long PixelAddress(uint baseAddress, uint stride, int x, int y, int bytesPerPixel)
    {
        return baseAddress + (long)y * stride + (long)x * bytesPerPixel;
    }

    private static bool IsCompletePixelInVideoMemory(long address, int bytesPerPixel)
    {
        return address >= 0 && address + bytesPerPixel <= VgaMemory.TotalSize;
    }

    /// <summary>Applies the inclusive clip limits documented in section 19.3.</summary>
    private bool IsInsideClip(int x, int y, uint command)
    {
        return IsInsideClip(
            x,
            y,
            command,
            ReadDword(ClipLeftRightRegister),
            ReadDword(ClipTopBottomRegister));
    }

    private static bool IsInsideClip(
        int x,
        int y,
        uint command,
        uint clipLeftRight,
        uint clipTopBottom)
    {
        if ((command & HardwareClipping) == 0)
        {
            return true;
        }

        int left = HorizontalCoordinate(clipLeftRight);
        int right = VerticalCoordinate(clipLeftRight);
        int top = HorizontalCoordinate(clipTopBottom);
        int bottom = VerticalCoordinate(clipTopBottom);
        return x >= left && x <= right && y >= top && y <= bottom;
    }

    private (int Width, int Height) RectangleDimensions()
    {
        uint packed = ReadDword(WidthHeightRegister);
        return (HorizontalCoordinate(packed) + 1, VerticalCoordinate(packed));
    }

    private uint SourceStride()
    {
        return ReadDword(StrideRegister) & 0x0FF8;
    }

    private uint DestinationStride()
    {
        return (ReadDword(StrideRegister) >> 16) & 0x0FF8;
    }

    private static int HorizontalCoordinate(uint packed)
    {
        return (int)((packed >> 16) & 0x7FF);
    }

    private static int VerticalCoordinate(uint packed)
    {
        return (int)(packed & 0x7FF);
    }

    /// <summary>Removes reserved address, coordinate and stride bits at the latch boundary.</summary>
    private static uint MaskRegister(uint register, uint value)
    {
        return register switch
        {
            SourceBaseRegister or DestinationBaseRegister => value & 0x003FFFF8,
            ClipLeftRightRegister or ClipTopBottomRegister => value & 0x07FF07FF,
            StrideRegister => value & 0x0FF80FF8,
            WidthHeightRegister or SourceCoordinatesRegister or DestinationCoordinatesRegister
                => value & 0x07FF07FF,
            CommandRegister => value & 0xFFFE3FFF,
            _ => value
        };
    }

    private static void WriteSnapshotField(byte[] snapshot, ref int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(snapshot.AsSpan(offset), value);
        offset += sizeof(uint);
    }

    private static uint ReadSnapshotField(ReadOnlySpan<byte> snapshot, ref int offset)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(snapshot[offset..]);
        offset += sizeof(uint);
        return value;
    }

    /// <summary>
    /// Frozen CPU-upload registers plus the next DWORD position. Rows are
    /// padded to a DWORD, so the cursor always advances in four-byte steps.
    /// </summary>
    private sealed class ImageTransfer
    {
        public ImageTransfer(
            uint command,
            uint destinationBase,
            uint destinationStride,
            uint clipLeftRight,
            uint clipTopBottom,
            uint destinationCoordinates,
            int width,
            int height,
            int currentRow,
            int byteOffsetInRow)
        {
            Command = command;
            DestinationBase = destinationBase;
            DestinationStride = destinationStride;
            ClipLeftRight = clipLeftRight;
            ClipTopBottom = clipTopBottom;
            DestinationCoordinates = destinationCoordinates;
            Width = width;
            Height = height;
            CurrentRow = currentRow;
            ByteOffsetInRow = byteOffsetInRow;
        }

        public uint Command { get; }
        public uint DestinationBase { get; }
        public uint DestinationStride { get; }
        public uint ClipLeftRight { get; }
        public uint ClipTopBottom { get; }
        public uint DestinationCoordinates { get; }
        public int Width { get; }
        public int Height { get; }
        public int PixelBytesPerRow => Width * sizeof(ushort);
        public int AlignedBytesPerRow => (PixelBytesPerRow + 3) & ~3;
        public int CurrentRow { get; set; }
        public int ByteOffsetInRow { get; set; }
    }
}
