using System.Buffers.Binary;

namespace Aurora86.Core.Devices.Video;

/// <summary>
/// The primary-stream presentation latches in the 1996 ViRGE Streams
/// Processor. DOS period software programs these DWORD registers to choose a
/// visible VRAM surface, its pitch and its screen window; the current subset
/// presents unscaled KRGB1555 and leaves secondary-stream composition out.
/// The databook does not defer these primary fields to VSYNC, so writes take
/// effect immediately and the display samples them at its next render.
/// </summary>
public sealed class S3VirgeStreams
{
    private const uint SnapshotMagic = 0x31545356; // VST1, emulator snapshot payload.
    private const ushort SnapshotVersion = 1;
    private const ushort SnapshotRegisterCount = 7;
    private const int SnapshotHeaderSize = 8;
    private const int SnapshotSize = SnapshotHeaderSize + SnapshotRegisterCount * sizeof(uint);

    public const uint PrimaryControlRegister = 0x8180;
    public const uint PrimaryBufferAddress0Register = 0x81C0;
    public const uint PrimaryBufferAddress1Register = 0x81C4;
    public const uint PrimaryStrideRegister = 0x81C8;
    public const uint BufferSelectRegister = 0x81CC;
    public const uint PrimaryWindowStartRegister = 0x81F0;
    public const uint PrimaryWindowSizeRegister = 0x81F4;

    private const byte FullStreamsMode = 0x0C; // CR67 bits 3-2 = 11b.
    private const uint UnscaledKrgb1555 = 0x03000000;
    private const uint PrimaryControlMask = 0x77000000;
    private const uint BufferAddressMask = 0x003FFFF8;
    private const uint StrideMask = 0x00000FFF;
    private const uint PrimaryBufferSelectMask = 0x00000001;
    private const uint WindowFieldMask = 0x07FF07FF;

    private static readonly uint[] RegisterOffsets =
    [
        PrimaryControlRegister,
        PrimaryBufferAddress0Register,
        PrimaryBufferAddress1Register,
        PrimaryStrideRegister,
        BufferSelectRegister,
        PrimaryWindowStartRegister,
        PrimaryWindowSizeRegister
    ];

    private readonly VgaRegisters _vgaRegisters;
    private readonly uint[] _registers = new uint[SnapshotRegisterCount];

    public S3VirgeStreams(VgaRegisters registers)
    {
        ArgumentNullException.ThrowIfNull(registers);
        _vgaRegisters = registers;
    }

    /// <summary>
    /// Changes whenever a primary latch changes or state is restored. The renderer
    /// uses this independently of VGA dirty rows because a page flip changes
    /// the picture without writing VRAM.
    /// </summary>
    public ulong Revision { get; private set; }

    /// <summary>True only for CR67's full primary-and-secondary Streams mode.</summary>
    public bool IsFullStreamsMode => (_vgaRegisters.Crtc(0x67) & FullStreamsMode) == FullStreamsMode;

    /// <summary>
    /// True for the measured primary format: KRGB1555 with pass-through
    /// horizontal filtering. Other full-Streams formats remain unsupported.
    /// </summary>
    public bool HasSupportedPrimaryFormat => ReadDword(PrimaryControlRegister) == UnscaledKrgb1555;

    /// <summary>The primary buffer chosen by MM81CC bit zero.</summary>
    public uint PrimaryBufferAddress =>
        ReadDword((ReadDword(BufferSelectRegister) & PrimaryBufferSelectMask) == 0
            ? PrimaryBufferAddress0Register
            : PrimaryBufferAddress1Register);

    public int PrimaryStride => (int)ReadDword(PrimaryStrideRegister);

    /// <summary>
    /// Screen coordinates decoded from the hardware's position-plus-one
    /// fields. A programmed zero therefore denotes coordinate -1.
    /// </summary>
    public (int X, int Y) PrimaryWindowOrigin
    {
        get
        {
            uint value = ReadDword(PrimaryWindowStartRegister);
            return ((int)((value >> 16) & 0x7FF) - 1, (int)(value & 0x7FF) - 1);
        }
    }

    /// <summary>Width is stored minus one; height is stored directly.</summary>
    public (int Width, int Height) PrimaryWindowSize
    {
        get
        {
            uint value = ReadDword(PrimaryWindowSizeRegister);
            return ((int)((value >> 16) & 0x7FF) + 1, (int)(value & 0x7FF));
        }
    }

    /// <summary>Identifies the seven aligned DWORD registers in the primary subset.</summary>
    public static bool IsRegister(uint register)
    {
        return Array.IndexOf(RegisterOffsets, register) >= 0;
    }

    /// <summary>Returns a masked latch or an open-bus value outside the subset.</summary>
    public uint ReadDword(uint register)
    {
        int index = Array.IndexOf(RegisterOffsets, register);
        return index >= 0 ? _registers[index] : uint.MaxValue;
    }

    /// <summary>
    /// Stores one primary register immediately. Values documented as undefined
    /// at power-on start at zero here for deterministic reset and snapshots.
    /// </summary>
    public void WriteDword(uint register, uint value)
    {
        int index = Array.IndexOf(RegisterOffsets, register);
        if (index < 0)
        {
            return;
        }

        uint maskedValue = MaskRegister(register, value);
        if (_registers[index] == maskedValue)
        {
            return;
        }

        _registers[index] = maskedValue;
        Revision++;
    }

    /// <summary>Captures all primary latches in fixed register order.</summary>
    public byte[] Capture()
    {
        byte[] snapshot = new byte[SnapshotSize];
        BinaryPrimitives.WriteUInt32LittleEndian(snapshot, SnapshotMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(snapshot.AsSpan(4), SnapshotVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(snapshot.AsSpan(6), SnapshotRegisterCount);
        for (int index = 0; index < _registers.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                snapshot.AsSpan(SnapshotHeaderSize + index * sizeof(uint)),
                _registers[index]);
        }

        return snapshot;
    }

    /// <summary>Validates the fixed VST1 payload and every stored register mask.</summary>
    public static void ValidateSnapshot(ReadOnlySpan<byte> snapshot)
    {
        if (snapshot.Length != SnapshotSize
            || BinaryPrimitives.ReadUInt32LittleEndian(snapshot) != SnapshotMagic
            || BinaryPrimitives.ReadUInt16LittleEndian(snapshot[4..]) != SnapshotVersion
            || BinaryPrimitives.ReadUInt16LittleEndian(snapshot[6..]) != SnapshotRegisterCount)
        {
            throw new ArgumentException("Invalid ViRGE Streams snapshot header.", nameof(snapshot));
        }

        for (int index = 0; index < RegisterOffsets.Length; index++)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(
                snapshot[(SnapshotHeaderSize + index * sizeof(uint))..]);
            if (MaskRegister(RegisterOffsets[index], value) != value)
            {
                throw new ArgumentException("Invalid ViRGE Streams register value.", nameof(snapshot));
            }
        }
    }

    /// <summary>Restores validated latches without touching VRAM or presenting a frame.</summary>
    public void Restore(ReadOnlySpan<byte> snapshot)
    {
        ValidateSnapshot(snapshot);
        for (int index = 0; index < _registers.Length; index++)
        {
            _registers[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                snapshot[(SnapshotHeaderSize + index * sizeof(uint))..]);
        }

        Revision++;
    }

    /// <summary>Returns every primary latch to the deterministic emulator reset state.</summary>
    public void Reset()
    {
        Array.Clear(_registers);
        Revision++;
    }

    private static uint MaskRegister(uint register, uint value)
    {
        return register switch
        {
            PrimaryControlRegister => value & PrimaryControlMask,
            PrimaryBufferAddress0Register or PrimaryBufferAddress1Register => value & BufferAddressMask,
            PrimaryStrideRegister => value & StrideMask,
            BufferSelectRegister => value & PrimaryBufferSelectMask,
            PrimaryWindowStartRegister or PrimaryWindowSizeRegister => value & WindowFieldMask,
            _ => value
        };
    }
}
