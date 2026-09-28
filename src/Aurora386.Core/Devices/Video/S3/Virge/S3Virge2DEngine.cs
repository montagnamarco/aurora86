using System.Buffers.Binary;

namespace Aurora386.Core.Devices.Video;

/// <summary>
/// The synchronous 2D BitBLT block of S3's 1996 ViRGE 86C325 accelerator.
/// Period display drivers program its memory-mapped latches and either submit
/// a screen operation immediately or stream image rows through the CPU
/// transfer aperture. This bounded implementation covers 8- and 16-bit
/// destinations, the full three-operand raster-operation table, mono
/// patterns, screen sources, and CPU sources in colour or mono form: the
/// operations the DOS S3d CUBE client and the Windows 95 S3V.DRV desktop were
/// measured issuing (solid fills, PATINVERT, transparent glyph uploads).
/// Colour patterns, 24-bit surfaces and autoexecute are rejected before they
/// can alter video memory.
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

    // Command Set register (A500h) fields, databook section 19.
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
    private const uint ThreeDimensionalSelector = 1u << 31;
    private const uint DestinationFormatMask = 0x1Cu;
    private const uint EightBitFormat = 0;
    private const uint SixteenBitFormat = 1;
    private const uint SourceCopyRasterOperation = 0xCC;
    private const uint PatternCopyRasterOperation = 0xF0;
    private const uint BitBltCommand = 0;
    private const uint RectangleFillCommand = 2;
    private const uint NopCommand = 15;
    private const uint ByteImageAlignment = 0;
    private const uint WordImageAlignment = 1;
    private const uint DwordImageAlignment = 2;
    private const uint ReservedImageAlignment = 3;
    private const int BitsPerMonoByte = 8;
    private const int PatternSize = 8;

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
    /// Supplies four little-endian bytes to an active CPU image transfer. The
    /// stream is consumed one byte at a time so that byte- and word-aligned
    /// rows may start in the middle of a DWORD, as section 19 describes for
    /// ITA. Pixels clipped from the destination are still consumed.
    /// </summary>
    public bool WriteImageDword(uint value)
    {
        ImageTransfer? transfer = _imageTransfer;
        if (transfer is null)
        {
            return false;
        }

        // FDO discards leading bytes of the very first DWORD only. Nothing has
        // been consumed yet exactly when the cursor still sits at row 0, byte 0.
        int firstByte = 0;
        if (transfer.CurrentRow == 0 && transfer.ByteOffsetInRow == 0)
        {
            firstByte = (int)FirstDwordOffset(transfer.Command);
        }

        for (int byteIndex = firstByte; byteIndex < sizeof(uint); byteIndex++)
        {
            byte data = (byte)(value >> (byteIndex * 8));
            ConsumeUploadByte(transfer, data);
            if (_imageTransfer is null)
            {
                // The rectangle is complete; trailing bytes of this DWORD are padding.
                break;
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

        if (!IsReachableUploadCursor(command, (int)width, (int)currentRow, (int)byteOffsetInRow))
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
        if ((command & AutoExecute) != 0 || (command & ThreeDimensionalSelector) != 0)
        {
            UnsupportedCommandIssued?.Invoke(command);
            return;
        }

        int bytesPerPixel = BytesPerPixel(command);
        uint rasterOperation = RasterOperation(command);
        bool patternIsAvailable = !RasterOperationUsesPattern(rasterOperation)
            || (command & MonoPattern) != 0;
        if (bytesPerPixel == 0 || !patternIsAvailable)
        {
            // 24-bit surfaces and the colour pattern block at A100h are not modelled.
            UnsupportedCommandIssued?.Invoke(command);
            return;
        }

        if (commandType == RectangleFillCommand && !RasterOperationUsesSource(rasterOperation))
        {
            ExecuteRectangleFill(command, bytesPerPixel);
            return;
        }

        if (commandType == BitBltCommand && (command & ImageDataSource) == 0)
        {
            ExecuteScreenBitBlt(command, bytesPerPixel);
            return;
        }

        if (IsSupportedImageTransfer(command))
        {
            BeginImageTransfer(command);
            return;
        }

        UnsupportedCommandIssued?.Invoke(command);
    }

    /// <summary>
    /// Recognizes CPU uploads whose stream this engine can consume: 8- or
    /// 16-bit colour, or mono data with a mono pattern when the ROP needs one.
    /// </summary>
    private static bool IsSupportedImageTransfer(uint command)
    {
        // XP and YP govern screen-source traversal only; CPU data always
        // arrives in positive row order, so both values are accepted here.
        if ((command & AutoExecute) != 0
            || !IsTwoDimensionalCommand(command, BitBltCommand)
            || (command & ImageDataSource) == 0
            || BytesPerPixel(command) == 0
            || ImageAlignment(command) == ReservedImageAlignment)
        {
            return false;
        }

        bool patternIsAvailable = !RasterOperationUsesPattern(RasterOperation(command))
            || (command & MonoPattern) != 0;

        // An odd FDO on a 16-bit colour upload would split a pixel across two
        // CPU DWORDs. No driver produces it, because application scanlines are
        // DWORD aligned, and rejecting it keeps the transfer cursor byte-exact.
        bool splitsColourPixel = (command & MonoSource) == 0
            && BytesPerPixel(command) == sizeof(ushort)
            && (FirstDwordOffset(command) & 1) != 0;

        return patternIsAvailable && !splitsColourPixel;
    }

    /// <summary>Checks the command selector and rejects the 3D register interpretation.</summary>
    private static bool IsTwoDimensionalCommand(uint command, uint expectedType)
    {
        bool threeDimensional = (command & ThreeDimensionalSelector) != 0;
        uint commandType = (command >> CommandTypeShift) & 0xF;
        return !threeDimensional && commandType == expectedType;
    }

    /// <summary>Maps the DEST FORMAT field to a pixel size; zero marks an unsupported depth.</summary>
    private static int BytesPerPixel(uint command)
    {
        return DestinationFormat(command) switch
        {
            EightBitFormat => 1,
            SixteenBitFormat => sizeof(ushort),
            _ => 0
        };
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

    /// <summary>
    /// Evaluates one of the 256 Windows three-operand raster operations. The
    /// 8-bit code is the truth table of the operation indexed by the pattern,
    /// source and destination bits (appendix A): SRCCOPY is CCh, PATCOPY F0h
    /// and PATINVERT 5Ah. The two copies are short-circuited because the
    /// desktop issues them for nearly every pixel.
    /// </summary>
    private static uint CombinePixels(
        uint rasterOperation,
        uint pattern,
        uint source,
        uint destination,
        int bytesPerPixel)
    {
        if (rasterOperation == SourceCopyRasterOperation)
        {
            return source;
        }

        if (rasterOperation == PatternCopyRasterOperation)
        {
            return pattern;
        }

        uint result = 0;
        int bitsPerPixel = bytesPerPixel * 8;
        for (int bit = 0; bit < bitsPerPixel; bit++)
        {
            uint patternBit = (pattern >> bit) & 1;
            uint sourceBit = (source >> bit) & 1;
            uint destinationBit = (destination >> bit) & 1;
            int truthTableIndex = (int)(patternBit << 2 | sourceBit << 1 | destinationBit);
            result |= ((rasterOperation >> truthTableIndex) & 1) << bit;
        }

        return result;
    }

    /// <summary>Reports whether the truth table changes when the source operand flips.</summary>
    private static bool RasterOperationUsesSource(uint rasterOperation)
    {
        // Source is bit 1 of the table index: rows 0, 1, 4, 5 have it clear
        // and rows 2, 3, 6, 7 have it set.
        uint sourceClearRows = rasterOperation & 0x33;
        uint sourceSetRows = (rasterOperation >> 2) & 0x33;
        return sourceClearRows != sourceSetRows;
    }

    /// <summary>Reports whether the truth table changes when the pattern operand flips.</summary>
    private static bool RasterOperationUsesPattern(uint rasterOperation)
    {
        // Pattern is bit 2 of the table index: rows 0-3 clear, rows 4-7 set.
        uint patternClearRows = rasterOperation & 0x0F;
        uint patternSetRows = (rasterOperation >> 4) & 0x0F;
        return patternClearRows != patternSetRows;
    }

    /// <summary>
    /// Fills a rectangle. Section 15.4.4.2: the pattern operand is forced to
    /// the pattern foreground colour, so only source-free ROPs are meaningful
    /// and the caller has already excluded the others.
    /// </summary>
    private void ExecuteRectangleFill(uint command, int bytesPerPixel)
    {
        (int width, int height) = RectangleDimensions();
        if (height == 0)
        {
            return;
        }

        var target = new DrawTarget(
            command,
            ReadDword(DestinationBaseRegister),
            DestinationStride(),
            ReadDword(ClipLeftRightRegister),
            ReadDword(ClipTopBottomRegister),
            bytesPerPixel);
        uint destinationCoordinates = ReadDword(DestinationCoordinatesRegister);
        int destinationX = HorizontalCoordinate(destinationCoordinates);
        int destinationY = VerticalCoordinate(destinationCoordinates);
        uint foreground = MaskColor(ReadDword(PatternForegroundRegister), bytesPerPixel);

        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
            {
                DrawPixel(target, destinationX + column, destinationY + row, foreground, source: 0);
            }
        }
    }

    /// <summary>
    /// Runs a BitBLT whose source, when the ROP needs one, is the screen.
    /// Pixels are visited in the guest-selected XP/YP order so correctly
    /// directed overlapping copies keep their source intact.
    /// </summary>
    private void ExecuteScreenBitBlt(uint command, int bytesPerPixel)
    {
        (int width, int height) = RectangleDimensions();
        if (height == 0)
        {
            return;
        }

        var target = new DrawTarget(
            command,
            ReadDword(DestinationBaseRegister),
            DestinationStride(),
            ReadDword(ClipLeftRightRegister),
            ReadDword(ClipTopBottomRegister),
            bytesPerPixel);
        bool readsSource = RasterOperationUsesSource(RasterOperation(command));
        int xStep = (command & (1u << XPositiveBit)) != 0 ? 1 : -1;
        int yStep = (command & (1u << YPositiveBit)) != 0 ? 1 : -1;
        uint sourceBase = ReadDword(SourceBaseRegister);
        uint sourceStride = SourceStride();
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

                uint source = 0;
                if (readsSource)
                {
                    long sourceAddress = PixelAddress(sourceBase, sourceStride, sourceX, sourceY, bytesPerPixel);
                    if (!IsCompletePixelInVideoMemory(sourceAddress, bytesPerPixel))
                    {
                        continue;
                    }

                    source = ReadPixel(sourceAddress, bytesPerPixel);
                }

                uint pattern = PatternPixel(destinationX, destinationY, bytesPerPixel);
                DrawPixel(target, destinationX, destinationY, pattern, source);
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

    /// <summary>
    /// Consumes one byte of the CPU stream: pixel data while the row's payload
    /// lasts, alignment padding afterwards. The row and cursor advance so that
    /// the next row starts at the byte, word or DWORD boundary ITA selects.
    /// </summary>
    private void ConsumeUploadByte(ImageTransfer transfer, byte data)
    {
        int byteOffset = transfer.ByteOffsetInRow;
        if (byteOffset < transfer.PixelBytesPerRow)
        {
            if (transfer.IsMonoSource)
            {
                DrawMonoSourceByte(transfer, byteOffset, data);
            }
            else if (transfer.BytesPerPixel == 1)
            {
                DrawColourSourcePixel(transfer, byteOffset, data);
            }
            else if ((byteOffset & 1) == 0)
            {
                // The low byte of a 16-bit pixel waits for its high byte. Both
                // always arrive inside the same DWORD (see IsSupportedImageTransfer).
                transfer.PendingLowByte = data;
            }
            else
            {
                uint pixel = (uint)(transfer.PendingLowByte | data << 8);
                DrawColourSourcePixel(transfer, byteOffset / sizeof(ushort), pixel);
            }
        }

        transfer.ByteOffsetInRow++;
        if (transfer.ByteOffsetInRow == transfer.AlignedBytesPerRow)
        {
            transfer.ByteOffsetInRow = 0;
            transfer.CurrentRow++;
            if (transfer.CurrentRow == transfer.Height)
            {
                _imageTransfer = null;
            }
        }
    }

    /// <summary>
    /// Expands one byte of mono source data. The leftmost pixel is the most
    /// significant bit, the layout the databook gives for mono pattern lines
    /// and the one Windows font bitmaps use. Bit 9 (TP) leaves the screen
    /// untouched where the image bit is clear: transparent text.
    /// </summary>
    private void DrawMonoSourceByte(ImageTransfer transfer, int byteOffsetInRow, byte data)
    {
        int firstColumn = byteOffsetInRow * BitsPerMonoByte;
        bool transparent = (transfer.Command & Transparent) != 0;
        uint foreground = MaskColor(ReadDword(SourceForegroundRegister), transfer.BytesPerPixel);
        uint background = MaskColor(ReadDword(SourceBackgroundRegister), transfer.BytesPerPixel);

        for (int bit = 0; bit < BitsPerMonoByte; bit++)
        {
            int column = firstColumn + bit;
            if (column >= transfer.Width)
            {
                break;
            }

            bool imageBitSet = (data & (0x80 >> bit)) != 0;
            if (!imageBitSet && transparent)
            {
                continue;
            }

            DrawUploadedPixel(transfer, column, imageBitSet ? foreground : background);
        }
    }

    /// <summary>
    /// Draws one CPU colour pixel. With TP set a colour equal to SRC_FG_CLR is
    /// the transparent key and leaves the destination unchanged (section 19).
    /// </summary>
    private void DrawColourSourcePixel(ImageTransfer transfer, int pixelIndex, uint pixel)
    {
        if ((transfer.Command & Transparent) != 0)
        {
            uint transparentKey = MaskColor(ReadDword(SourceForegroundRegister), transfer.BytesPerPixel);
            if (pixel == transparentKey)
            {
                return;
            }
        }

        DrawUploadedPixel(transfer, pixelIndex, pixel);
    }

    /// <summary>Places one consumed upload pixel at its frozen destination position.</summary>
    private void DrawUploadedPixel(ImageTransfer transfer, int pixelIndex, uint source)
    {
        int destinationX = HorizontalCoordinate(transfer.DestinationCoordinates) + pixelIndex;
        int destinationY = VerticalCoordinate(transfer.DestinationCoordinates) + transfer.CurrentRow;
        var target = new DrawTarget(
            transfer.Command,
            transfer.DestinationBase,
            transfer.DestinationStride,
            transfer.ClipLeftRight,
            transfer.ClipTopBottom,
            transfer.BytesPerPixel);
        uint pattern = PatternPixel(destinationX, destinationY, transfer.BytesPerPixel);
        DrawPixel(target, destinationX, destinationY, pattern, source);
    }

    /// <summary>
    /// Combines the operands for one destination pixel and writes the result
    /// when clipping and DE allow it. A DE of 0 still consumes the command's
    /// data without updating the screen, as section 19 describes.
    /// </summary>
    private void DrawPixel(DrawTarget target, int x, int y, uint pattern, uint source)
    {
        if (!IsInsideClip(x, y, target.Command, target.ClipLeftRight, target.ClipTopBottom))
        {
            return;
        }

        if ((target.Command & DrawEnable) == 0)
        {
            return;
        }

        long address = PixelAddress(target.DestinationBase, target.DestinationStride, x, y, target.BytesPerPixel);
        if (!IsCompletePixelInVideoMemory(address, target.BytesPerPixel))
        {
            return;
        }

        uint destination = ReadPixel(address, target.BytesPerPixel);
        uint result = CombinePixels(
            RasterOperation(target.Command),
            pattern,
            source,
            destination,
            target.BytesPerPixel);
        WritePixel(address, result, target.BytesPerPixel);
    }

    /// <summary>
    /// Reads the 8x8 mono pattern colour at a screen position. MONO_PAT_0
    /// holds pattern lines 0-3 one byte per line and MONO_PAT_1 lines 4-7;
    /// within a line the leftmost pixel is the most significant bit (section
    /// 19, A4E8h). The pattern is anchored to screen coordinates, so a driver
    /// aligns brush origins by rotating the bits it programs.
    /// </summary>
    private uint PatternPixel(int x, int y, int bytesPerPixel)
    {
        int line = y & (PatternSize - 1);
        uint patternRegister = line < PatternSize / 2
            ? ReadDword(MonoPattern0Register)
            : ReadDword(MonoPattern1Register);
        uint lineBits = (patternRegister >> ((line & 3) * 8)) & 0xFF;
        bool foreground = (lineBits & (0x80u >> (x & (PatternSize - 1)))) != 0;
        uint color = foreground
            ? ReadDword(PatternForegroundRegister)
            : ReadDword(PatternBackgroundRegister);
        return MaskColor(color, bytesPerPixel);
    }

    private static uint MaskColor(uint color, int bytesPerPixel)
    {
        return bytesPerPixel == 1 ? color & 0xFF : color & 0xFFFF;
    }

    private uint ReadPixel(long address, int bytesPerPixel)
    {
        uint low = _memory.ReadLinear((uint)address);
        if (bytesPerPixel == 1)
        {
            return low;
        }

        uint high = _memory.ReadLinear((uint)address + 1);
        return low | high << 8;
    }

    private void WritePixel(long address, uint pixel, int bytesPerPixel)
    {
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

    /// <summary>Number of payload bytes in one uploaded row for the command's source format.</summary>
    private static int UploadPixelBytesPerRow(uint command, int width)
    {
        if ((command & MonoSource) != 0)
        {
            return (width + BitsPerMonoByte - 1) / BitsPerMonoByte;
        }

        return width * BytesPerPixel(command);
    }

    /// <summary>Row length including the padding ITA adds before the next row starts.</summary>
    private static int UploadAlignedBytesPerRow(uint command, int width)
    {
        int pixelBytes = UploadPixelBytesPerRow(command, width);
        return ImageAlignment(command) switch
        {
            ByteImageAlignment => pixelBytes,
            WordImageAlignment => (pixelBytes + 1) & ~1,
            _ => (pixelBytes + 3) & ~3
        };
    }

    /// <summary>
    /// Every CPU write consumes four bytes, less FDO on the first one, so a
    /// cursor is reachable only when the bytes consumed so far fit that rule.
    /// </summary>
    private static bool IsReachableUploadCursor(uint command, int width, int currentRow, int byteOffsetInRow)
    {
        int alignedBytesPerRow = UploadAlignedBytesPerRow(command, width);
        if (byteOffsetInRow >= alignedBytesPerRow)
        {
            return false;
        }

        if (currentRow == 0 && byteOffsetInRow == 0)
        {
            return true;
        }

        long consumedBytes = (long)currentRow * alignedBytesPerRow + byteOffsetInRow;
        return (consumedBytes + FirstDwordOffset(command)) % sizeof(uint) == 0;
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

    /// <summary>The destination surface and command a group of pixel writes shares.</summary>
    private readonly record struct DrawTarget(
        uint Command,
        uint DestinationBase,
        uint DestinationStride,
        uint ClipLeftRight,
        uint ClipTopBottom,
        int BytesPerPixel);

    /// <summary>
    /// Frozen CPU-upload registers plus the byte cursor inside the current
    /// row. Rows are padded according to ITA, so the cursor may sit at any
    /// byte offset for byte-aligned mono text.
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
            BytesPerPixel = S3Virge2DEngine.BytesPerPixel(command);
            IsMonoSource = (command & MonoSource) != 0;
            PixelBytesPerRow = UploadPixelBytesPerRow(command, width);
            AlignedBytesPerRow = UploadAlignedBytesPerRow(command, width);
        }

        public uint Command { get; }
        public uint DestinationBase { get; }
        public uint DestinationStride { get; }
        public uint ClipLeftRight { get; }
        public uint ClipTopBottom { get; }
        public uint DestinationCoordinates { get; }
        public int Width { get; }
        public int Height { get; }
        public int BytesPerPixel { get; }
        public bool IsMonoSource { get; }
        public int PixelBytesPerRow { get; }
        public int AlignedBytesPerRow { get; }
        public int CurrentRow { get; set; }
        public int ByteOffsetInRow { get; set; }

        /// <summary>Low byte of a 16-bit pixel whose high byte is still in flight within one DWORD.</summary>
        public byte PendingLowByte { get; set; }
    }
}
