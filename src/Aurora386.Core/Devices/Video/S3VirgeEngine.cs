using System.Buffers.Binary;
using Aurora386.Core.Memory;

namespace Aurora386.Core.Devices.Video;

/// <summary>
/// The triangle register block of S3's 1996 ViRGE 86C325 accelerator.
/// Software supplies fixed-point edge slopes through memory-mapped registers;
/// the engine walks scan lines into the same video memory used for display.
/// This first subset draws constant-colour RGB888 triangles synchronously.
/// Textures, interpolated colours, depth, interrupts and FIFO timing are not
/// implemented, and unsupported drawing requests are reported to diagnostics.
/// Register definitions follow S3's August 1996 ViRGE databook, sections 19
/// and 22. Offsets are relative to the card's 64 KB MMIO register window.
/// </summary>
public sealed class S3VirgeEngine : IMemoryDevice
{
    private const uint StatusRegister = 0x8504;
    private const uint DestinationBase = 0xB4D8;
    private const uint ClipLeftRight = 0xB4DC;
    private const uint ClipTopBottom = 0xB4E0;
    private const uint DestinationStride = 0xB4E4;
    private const uint CommandSet = 0xB500;
    private const uint GreenBlueDeltaX = 0xB53C;
    private const uint AlphaRedDeltaX = 0xB540;
    private const uint GreenBlueDeltaY = 0xB544;
    private const uint AlphaRedDeltaY = 0xB548;
    private const uint GreenBlueStart = 0xB54C;
    private const uint AlphaRedStart = 0xB550;
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
    private const uint AllowedCommandOptions = AutoExecute | HardwareClipping;
    private const int CoordinateFractionBits = 20;
    private const long CoordinateUnit = 1L << CoordinateFractionBits;
    private const uint SnapshotMagic = 0x31475256;
    private const ushort SnapshotVersion = 1;
    private const ushort SnapshotRegisterCount = 19;
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
        YCount
    ];

    private readonly VgaMemory _memory;
    private readonly Dictionary<uint, uint> _registers = new();

    public S3VirgeEngine(VgaMemory memory)
    {
        _memory = memory;
    }

    /// <summary>
    /// Captures the nineteen programmed triangle latches in a fixed little
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
        if (magic != SnapshotMagic || version != SnapshotVersion || count != SnapshotRegisterCount)
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

        uint[] values = new uint[SnapshotRegisterCount];
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                snapshot.Slice(SnapshotHeaderSize + index * sizeof(uint), sizeof(uint)));
        }

        _registers.Clear();
        for (int index = 0; index < SnapshotRegisters.Length; index++)
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
            && (offset == DestinationBase || offset == ClipLeftRight
                || offset == ClipTopBottom || offset == DestinationStride
                || offset == CommandSet
                || (offset >= GreenBlueDeltaX && offset <= AlphaRedStart)
                || (offset >= Edge12Slope && offset <= YCount));
    }

    /// <summary>Reserved and alignment bits cannot change the engine's address calculations.</summary>
    private static uint MaskRegister(uint offset, uint value)
    {
        switch (offset)
        {
            case DestinationBase:
                return value & 0x003FFFF8;
            case ClipLeftRight:
            case ClipTopBottom:
                return value & 0x07FF07FF;
            case DestinationStride:
                return value & 0x0FF80FF8;
            case YStart:
                return value & 0x7FF;
            case YCount:
                return value & 0x87FF07FF;
            default:
                return value;
        }
    }

    /// <summary>Completes one flat triangle before the next guest register access.</summary>
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
        if ((command & ~AllowedCommandOptions) != FlatRgb888Command || !constantColour)
        {
            UnsupportedCommandIssued?.Invoke(command);
            return;
        }

        // Local edge accumulators preserve the programmed latches for the
        // next autoexecute submission, including submissions with zero rows.
        uint counts = ReadDword(YCount);
        int direction = (counts & 0x80000000) != 0 ? 1 : -1;
        int currentY = (int)ReadDword(YStart);
        int longEdge = unchecked((int)ReadDword(Edge02Start));
        int longSlope = unchecked((int)ReadDword(Edge02Slope));
        byte blue = ColourComponent(ReadDword(GreenBlueStart));
        byte green = ColourComponent(ReadDword(GreenBlueStart) >> 16);
        byte red = ColourComponent(ReadDword(AlphaRedStart));

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
                DrawScanLine(currentY, longEdge, shortEdge, direction, command, blue, green, red);
                longEdge = unchecked(longEdge + longSlope);
                shortEdge = unchecked(shortEdge + shortSlope);
                currentY--;
            }
        }
    }

    /// <summary>Converts the unsigned starting part of an S8.7 colour attribute.</summary>
    private static byte ColourComponent(uint packed)
    {
        int component = (short)(packed & 0xFFFF);
        return (byte)Math.Clamp(component >> 7, 0, 255);
    }

    /// <summary>Writes the covered pixels after inclusive hardware clipping.</summary>
    private void DrawScanLine(int y, int longEdge, int shortEdge, int direction,
        uint command, byte blue, byte green, byte red)
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
        int minimum = direction > 0 ? first : end + 1;
        int maximum = direction > 0 ? end - 1 : first;
        if ((command & HardwareClipping) != 0)
        {
            uint verticalClip = ReadDword(ClipTopBottom);
            if (y < (int)(verticalClip >> 16) || y > (int)(verticalClip & 0x7FF))
            {
                return;
            }

            uint horizontalClip = ReadDword(ClipLeftRight);
            minimum = Math.Max(minimum, (int)(horizontalClip >> 16));
            maximum = Math.Min(maximum, (int)(horizontalClip & 0x7FF));
        }

        // Bounds checks keep incomplete out-of-range bus semantics from
        // turning a guest register value into a host memory error.
        long rowAddress = ReadDword(DestinationBase)
            + (long)y * (ReadDword(DestinationStride) >> 16);
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
    }
}
