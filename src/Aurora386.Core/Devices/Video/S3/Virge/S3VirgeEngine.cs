using System.Buffers.Binary;
using Aurora386.Core.Memory;

namespace Aurora386.Core.Devices.Video;

/// <summary>
/// The triangle register block of S3's 1996 ViRGE 86C325 accelerator.
/// Software supplies fixed-point edge slopes through memory-mapped registers;
/// the engine walks scan lines into the same video memory used for display.
/// The implemented synchronous subsets draw legacy flat RGB888 triangles and
/// the Gouraud-shaded ZRGB1555 command measured from S3d Toolkit 2.6 CUBE.
/// Textures, blending, fog, interrupts and FIFO timing remain unsupported and
/// unsupported drawing requests are reported to diagnostics.
/// Register definitions follow S3's August 1996 ViRGE databook, sections 19
/// and 22. Offsets are relative to the card's 64 KB MMIO register window.
/// </summary>
public sealed class S3VirgeEngine : IMemoryDevice
{
    private const uint StatusRegister = 0x8504;
    private const uint DepthBase = 0xB4D4;
    private const uint DestinationBase = 0xB4D8;
    private const uint ClipLeftRight = 0xB4DC;
    private const uint ClipTopBottom = 0xB4E0;
    private const uint DestinationStride = 0xB4E4;
    private const uint DepthStride = 0xB4E8;
    private const uint CommandSet = 0xB500;
    private const uint GreenBlueDeltaX = 0xB53C;
    private const uint AlphaRedDeltaX = 0xB540;
    private const uint GreenBlueDeltaY = 0xB544;
    private const uint AlphaRedDeltaY = 0xB548;
    private const uint GreenBlueStart = 0xB54C;
    private const uint AlphaRedStart = 0xB550;
    private const uint DepthDeltaX = 0xB554;
    private const uint DepthDeltaY = 0xB558;
    private const uint DepthStart = 0xB55C;
    private const uint Edge12Slope = 0xB560;
    private const uint Edge12Start = 0xB564;
    private const uint Edge01Slope = 0xB568;
    private const uint Edge01Start = 0xB56C;
    private const uint Edge02Slope = 0xB570;
    private const uint Edge02Start = 0xB574;
    private const uint YStart = 0xB578;
    private const uint YCount = 0xB57C;
    private const uint AutoExecute = 1;
    private const uint HardwareClipping = 2;
    private const uint FlatRgb888Command = 0x83000008;
    private const uint GouraudZRgb1555Command = 0x80C00006;
    private const uint AllowedCommandOptions = AutoExecute | HardwareClipping;
    private const int CoordinateFractionBits = 20;
    private const long CoordinateUnit = 1L << CoordinateFractionBits;
    private const int ColourFractionBits = 7;
    private const int Rgb1555DiscardedBits = ColourFractionBits + 3;
    private const int DepthFractionBits = 15;
    private const uint SnapshotMagic = 0x31475256;
    private const ushort LegacySnapshotVersion = 1;
    private const ushort LegacySnapshotRegisterCount = 19;
    private const ushort SnapshotVersion = 2;
    private const ushort SnapshotRegisterCount = 24;
    private const int SnapshotHeaderSize = 8;
    private const int SnapshotSize = SnapshotHeaderSize + SnapshotRegisterCount * sizeof(uint);

    private static readonly uint[] SnapshotRegisters =
    [
        DestinationBase,
        ClipLeftRight,
        ClipTopBottom,
        DestinationStride,
        CommandSet,
        GreenBlueDeltaX,
        AlphaRedDeltaX,
        GreenBlueDeltaY,
        AlphaRedDeltaY,
        GreenBlueStart,
        AlphaRedStart,
        Edge12Slope,
        Edge12Start,
        Edge01Slope,
        Edge01Start,
        Edge02Slope,
        Edge02Start,
        YStart,
        YCount,
        DepthBase,
        DepthStride,
        DepthDeltaX,
        DepthDeltaY,
        DepthStart
    ];

    private readonly VgaMemory _memory;
    private readonly Dictionary<uint, uint> _registers = new();

    public S3VirgeEngine(VgaMemory memory)
    {
        _memory = memory;
    }

    /// <summary>
    /// Captures the twenty-four programmed triangle latches in a fixed little
    /// endian payload. Video memory and host diagnostic events are external
    /// state and are deliberately excluded from this engine snapshot.
    /// </summary>
    public byte[] Capture()
    {
        byte[] snapshot = new byte[SnapshotSize];
        BinaryPrimitives.WriteUInt32LittleEndian(snapshot, SnapshotMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(snapshot.AsSpan(4), SnapshotVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(snapshot.AsSpan(6), SnapshotRegisterCount);

        for (int index = 0; index < SnapshotRegisters.Length; index++)
        {
            uint value = ReadDword(SnapshotRegisters[index]);
            BinaryPrimitives.WriteUInt32LittleEndian(
                snapshot.AsSpan(SnapshotHeaderSize + index * sizeof(uint)), value);
        }

        return snapshot;
    }

    /// <summary>
    /// Validates the fixed latch payload without changing registers, raising
    /// diagnostics, or writing video memory.
    /// </summary>
    public static void ValidateSnapshot(ReadOnlySpan<byte> snapshot)
    {
        if (snapshot.Length < SnapshotHeaderSize)
        {
            throw new ArgumentException("ViRGE snapshot header is truncated.", nameof(snapshot));
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(snapshot);
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(snapshot[4..]);
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(snapshot[6..]);
        bool legacyHeader = version == LegacySnapshotVersion
            && count == LegacySnapshotRegisterCount;
        bool currentHeader = version == SnapshotVersion
            && count == SnapshotRegisterCount;
        if (magic != SnapshotMagic || (!legacyHeader && !currentHeader))
        {
            throw new ArgumentException("ViRGE snapshot header is invalid.", nameof(snapshot));
        }

        int expectedLength = SnapshotHeaderSize + count * sizeof(uint);
        if (snapshot.Length != expectedLength)
        {
            throw new ArgumentException("ViRGE snapshot length is invalid.", nameof(snapshot));
        }
    }

    /// <summary>
    /// Restores the fixed latch payload after validation, without executing a
    /// command, raising diagnostics, or writing video memory.
    /// </summary>
    public void Restore(ReadOnlySpan<byte> snapshot)
    {
        ValidateSnapshot(snapshot);

        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(snapshot[6..]);
        uint[] values = new uint[count];
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                snapshot.Slice(SnapshotHeaderSize + index * sizeof(uint), sizeof(uint)));
        }

        _registers.Clear();
        for (int index = 0; index < values.Length; index++)
        {
            _registers[SnapshotRegisters[index]] = MaskRegister(SnapshotRegisters[index], values[index]);
        }
    }

    /// <summary>
    /// Reports a command outside this implementation's documented subset.
    /// This is an emulator diagnostic, not a guest-visible hardware fault.
    /// </summary>
    public event Action<uint>? UnsupportedCommandIssued;

    /// <summary>Reads one byte of a register without executing a command.</summary>
    public byte ReadByte(uint offset)
    {
        return (byte)(ReadDword(offset & ~3u) >> ((int)(offset & 3) * 8));
    }

    /// <summary>Reads the two requested little-endian register bytes.</summary>
    public ushort ReadWord(uint offset)
    {
        return (ushort)(ReadByte(offset) | (ReadByte(offset + 1) << 8));
    }

    /// <summary>Returns a register latch, or the polling status of this synchronous engine.</summary>
    public uint ReadDword(uint offset)
    {
        if (offset == StatusRegister)
        {
            // Databook 22-2 specifies bit 13 = 1 for idle and sixteen free
            // slots in bits 12:8. Its introduction at 15-3 reverses bit 13;
            // the register definition and 86Box agree on the value here.
            return 0x3000;
        }

        if (!IsTriangleRegister(offset))
        {
            return uint.MaxValue;
        }

        // Hardware power-on values are undefined. Zero is a deterministic
        // initial latch value, not a claim about a particular chip stepping.
        return _registers.GetValueOrDefault(offset);
    }

    /// <summary>
    /// Triangle programming currently accepts aligned dword writes only,
    /// matching the reference's engine access path rather than inventing
    /// byte assembly and premature command triggers.
    /// </summary>
    public void WriteByte(uint offset, byte value)
    {
    }

    /// <summary>Word writes do not submit triangle register commands.</summary>
    public void WriteWord(uint offset, ushort value)
    {
    }

    /// <summary>Latches one register and executes at the trigger chosen by CMD_SET.</summary>
    public void WriteDword(uint offset, uint value)
    {
        if (!IsTriangleRegister(offset))
        {
            return;
        }

        _registers[offset] = MaskRegister(offset, value);

        uint command = ReadDword(CommandSet);
        bool commandTrigger = offset == CommandSet && (command & AutoExecute) == 0;
        bool countTrigger = offset == YCount && (command & AutoExecute) != 0;
        if (commandTrigger || countTrigger)
        {
            Execute(command);
        }
    }

    /// <summary>Separates the supported register block from unmapped MMIO locations.</summary>
    private static bool IsTriangleRegister(uint offset)
    {
        return (offset & 3) == 0
            && (offset == DepthBase || offset == DestinationBase || offset == ClipLeftRight
                || offset == ClipTopBottom || offset == DestinationStride
                || offset == DepthStride
                || offset == CommandSet
                || (offset >= GreenBlueDeltaX && offset <= AlphaRedStart)
                || (offset >= DepthDeltaX && offset <= DepthStart)
                || (offset >= Edge12Slope && offset <= YCount));
    }

    /// <summary>Reserved and alignment bits cannot change the engine's address calculations.</summary>
    private static uint MaskRegister(uint offset, uint value)
    {
        switch (offset)
        {
            case DepthBase:
            case DestinationBase:
                return value & 0x003FFFF8;
            case ClipLeftRight:
            case ClipTopBottom:
                return value & 0x07FF07FF;
            case DestinationStride:
                return value & 0x0FF80FF8;
            case DepthStride:
                return value & 0x00000FF8;
            case YStart:
                return value & 0x7FF;
            case YCount:
                return value & 0x87FF07FF;
            default:
                return value;
        }
    }

    /// <summary>Completes one supported triangle before the next guest register access.</summary>
    private void Execute(uint command)
    {
        // NOP is also the documented way to disable autoexecute safely.
        if (((command >> 27) & 0xF) == 0xF)
        {
            return;
        }

        bool constantColour = ReadDword(GreenBlueDeltaX) == 0
            && ReadDword(AlphaRedDeltaX) == 0
            && ReadDword(GreenBlueDeltaY) == 0
            && ReadDword(AlphaRedDeltaY) == 0;
        if ((command & ~AllowedCommandOptions) == FlatRgb888Command && constantColour)
        {
            DrawFlatRgb888(command);
            return;
        }

        if (command == GouraudZRgb1555Command)
        {
            DrawGouraudZRgb1555(command);
            return;
        }

        UnsupportedCommandIssued?.Invoke(command);
    }

    /// <summary>Draws the original constant-colour RGB888 compatibility subset.</summary>
    private void DrawFlatRgb888(uint command)
    {
        byte blue = ColourComponent(ReadDword(GreenBlueStart));
        byte green = ColourComponent(ReadDword(GreenBlueStart) >> 16);
        byte red = ColourComponent(ReadDword(AlphaRedStart));

        WalkTriangle((currentY, longEdge, shortEdge, direction, _) =>
        {
            if (!TryGetScanLineBounds(
                currentY,
                longEdge,
                shortEdge,
                direction,
                command,
                out int minimum,
                out int maximum,
                out _))
            {
                return;
            }

            long rowAddress = ReadDword(DestinationBase)
                + (long)currentY * (ReadDword(DestinationStride) >> 16);
            for (int x = minimum; x <= maximum; x++)
            {
                long address = rowAddress + (long)x * 3;
                if (address < 0 || address + 2 >= VgaMemory.TotalSize)
                {
                    continue;
                }

                _memory.WriteLinear((uint)address, blue);
                _memory.WriteLinear((uint)address + 1, green);
                _memory.WriteLinear((uint)address + 2, red);
            }
        });
    }

    /// <summary>
    /// Draws the measured Gouraud ZRGB1555 command with strict less-than depth
    /// comparison and updates the separate sixteen-bit Z buffer on a pass. The
    /// point-A attributes advance by their Y deltas on each upward scan line;
    /// X deltas advance in the B57C-selected rendering direction from that
    /// row's long-edge integer origin. Fractional edge correction is not
    /// specified by the databook and remains unclaimed.
    /// </summary>
    private void DrawGouraudZRgb1555(uint command)
    {
        int blueStart = SignedColourComponent(ReadDword(GreenBlueStart));
        int greenStart = SignedColourComponent(ReadDword(GreenBlueStart) >> 16);
        int redStart = SignedColourComponent(ReadDword(AlphaRedStart));
        int blueDeltaX = SignedColourComponent(ReadDword(GreenBlueDeltaX));
        int greenDeltaX = SignedColourComponent(ReadDword(GreenBlueDeltaX) >> 16);
        int redDeltaX = SignedColourComponent(ReadDword(AlphaRedDeltaX));
        int blueDeltaY = SignedColourComponent(ReadDword(GreenBlueDeltaY));
        int greenDeltaY = SignedColourComponent(ReadDword(GreenBlueDeltaY) >> 16);
        int redDeltaY = SignedColourComponent(ReadDword(AlphaRedDeltaY));
        int depthStart = unchecked((int)ReadDword(DepthStart));
        int depthDeltaX = unchecked((int)ReadDword(DepthDeltaX));
        int depthDeltaY = unchecked((int)ReadDword(DepthDeltaY));

        WalkTriangle((currentY, longEdge, shortEdge, direction, rowIndex) =>
        {
            if (!TryGetScanLineBounds(
                currentY,
                longEdge,
                shortEdge,
                direction,
                command,
                out int minimum,
                out int maximum,
                out int attributeOriginX))
            {
                return;
            }

            long rowBlue = blueStart + (long)blueDeltaY * rowIndex;
            long rowGreen = greenStart + (long)greenDeltaY * rowIndex;
            long rowRed = redStart + (long)redDeltaY * rowIndex;
            long rowDepth = depthStart + (long)depthDeltaY * rowIndex;
            for (int x = minimum; x <= maximum; x++)
            {
                if (!TryGetPixelAddress(
                    ReadDword(DestinationBase),
                    ReadDword(DestinationStride) >> 16,
                    currentY,
                    x,
                    out uint destinationAddress)
                    || !TryGetPixelAddress(
                        ReadDword(DepthBase),
                        ReadDword(DepthStride),
                        currentY,
                        x,
                        out uint depthAddress))
                {
                    continue;
                }

                int traversalOffset = direction > 0
                    ? x - attributeOriginX
                    : attributeOriginX - x;
                ushort sourceDepth = DepthValue(
                    rowDepth + (long)depthDeltaX * traversalOffset);
                ushort bufferedDepth = ReadWordLinear(depthAddress);
                if (sourceDepth >= bufferedDepth)
                {
                    continue;
                }

                // Section 15.4.6 requires the depth comparison and update to
                // precede all pixel-colour generation for a passing fragment.
                WriteWordLinear(depthAddress, sourceDepth);
                ushort colour = PackRgb1555(
                    rowRed + (long)redDeltaX * traversalOffset,
                    rowGreen + (long)greenDeltaX * traversalOffset,
                    rowBlue + (long)blueDeltaX * traversalOffset);
                WriteWordLinear(destinationAddress, colour);
            }
        });
    }

    /// <summary>Walks both triangle halves while preserving every programmed latch.</summary>
    private void WalkTriangle(Action<int, int, int, int, int> drawScanLine)
    {
        // Local edge accumulators preserve the programmed latches for the
        // next autoexecute submission, including submissions with zero rows.
        uint counts = ReadDword(YCount);
        int direction = (counts & 0x80000000) != 0 ? 1 : -1;
        int currentY = (int)ReadDword(YStart);
        int longEdge = unchecked((int)ReadDword(Edge02Start));
        int longSlope = unchecked((int)ReadDword(Edge02Slope));
        int rowIndex = 0;

        DrawHalf((int)((counts >> 16) & 0x7FF), Edge01Start, Edge01Slope);
        DrawHalf((int)(counts & 0x7FF), Edge12Start, Edge12Slope);

        // The long side remains continuous while the other side changes
        // slope at the middle vertex; both halves walk upward on screen.
        void DrawHalf(int lineCount, uint startRegister, uint slopeRegister)
        {
            int shortEdge = unchecked((int)ReadDword(startRegister));
            int shortSlope = unchecked((int)ReadDword(slopeRegister));
            for (int line = 0; line < lineCount; line++)
            {
                drawScanLine(currentY, longEdge, shortEdge, direction, rowIndex);
                longEdge = unchecked(longEdge + longSlope);
                shortEdge = unchecked(shortEdge + shortSlope);
                currentY--;
                rowIndex++;
            }
        }
    }

    /// <summary>Converts the unsigned starting part of an S8.7 colour attribute.</summary>
    private static byte ColourComponent(uint packed)
    {
        int component = (short)(packed & 0xFFFF);
        return (byte)Math.Clamp(component >> 7, 0, 255);
    }

    /// <summary>Extracts one signed S8.7 component without discarding its fraction.</summary>
    private static short SignedColourComponent(uint packed)
    {
        return unchecked((short)(packed & 0xFFFF));
    }

    /// <summary>
    /// Converts three S8.7 accumulators to RGB1555. The databook does not state
    /// a rounding rule, so this measured subset discards fractional and low
    /// colour bits and saturates values outside the representable range.
    /// </summary>
    private static ushort PackRgb1555(long red, long green, long blue)
    {
        int red5 = (int)Math.Clamp(red >> Rgb1555DiscardedBits, 0, 31);
        int green5 = (int)Math.Clamp(green >> Rgb1555DiscardedBits, 0, 31);
        int blue5 = (int)Math.Clamp(blue >> Rgb1555DiscardedBits, 0, 31);
        return (ushort)((red5 << 10) | (green5 << 5) | blue5);
    }

    /// <summary>Discards the S16.15 fraction and saturates it to the Z-buffer word.</summary>
    private static ushort DepthValue(long depth)
    {
        return (ushort)Math.Clamp(depth >> DepthFractionBits, 0, ushort.MaxValue);
    }

    /// <summary>Finds covered pixels and the integer point-A attribute origin for a row.</summary>
    private bool TryGetScanLineBounds(int y, int longEdge, int shortEdge, int direction,
        uint command, out int minimum, out int maximum, out int attributeOriginX)
    {
        int first = (int)(((long)longEdge + CoordinateUnit - 1) >> CoordinateFractionBits);
        int end = (int)(((long)shortEdge + CoordinateUnit - 1) >> CoordinateFractionBits);
        if (direction < 0)
        {
            first--;
            end--;
        }

        // The reference excludes the opposite edge in both directions.
        // Integer-edge regression vectors pin that coverage without claiming
        // that all subpixel rounding has already been measured on silicon.
        minimum = direction > 0 ? first : end + 1;
        maximum = direction > 0 ? end - 1 : first;
        attributeOriginX = direction > 0 ? first : first + 1;
        if ((command & HardwareClipping) != 0)
        {
            uint verticalClip = ReadDword(ClipTopBottom);
            if (y < (int)(verticalClip >> 16) || y > (int)(verticalClip & 0x7FF))
            {
                return false;
            }

            uint horizontalClip = ReadDword(ClipLeftRight);
            minimum = Math.Max(minimum, (int)(horizontalClip >> 16));
            maximum = Math.Min(maximum, (int)(horizontalClip & 0x7FF));
        }

        return minimum <= maximum;
    }

    /// <summary>Keeps incomplete out-of-range bus semantics outside host memory.</summary>
    private static bool TryGetPixelAddress(
        uint baseAddress,
        uint stride,
        int y,
        int x,
        out uint address)
    {
        long candidate = baseAddress + (long)y * stride + (long)x * 2;
        if (candidate < 0 || candidate + 1 >= VgaMemory.TotalSize)
        {
            address = 0;
            return false;
        }

        address = (uint)candidate;
        return true;
    }

    /// <summary>Reads one little-endian ZRGB1555 or depth word from video memory.</summary>
    private ushort ReadWordLinear(uint address)
    {
        return (ushort)(_memory.ReadLinear(address) | (_memory.ReadLinear(address + 1) << 8));
    }

    /// <summary>Writes one little-endian ZRGB1555 or depth word to video memory.</summary>
    private void WriteWordLinear(uint address, ushort value)
    {
        _memory.WriteLinear(address, (byte)value);
        _memory.WriteLinear(address + 1, (byte)(value >> 8));
    }
}
