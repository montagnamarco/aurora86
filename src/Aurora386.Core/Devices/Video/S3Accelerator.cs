using Aurora386.Core.IO;
using Aurora386.Core.Memory;

namespace Aurora386.Core.Devices.Video;

/// <summary>
/// The S3's 2D drawing engine.
///
/// The idea is older than the S3 and comes from IBM's 8514/A: instead of the
/// processor writing every pixel of a rectangle through a 64 KB window, it
/// tells the card where the rectangle is and the card fills it. At 1024x768
/// that is the difference between three quarters of a megabyte crossing the
/// ISA bus and about a dozen register writes, which is why every graphical
/// system of the period is built on exactly three operations: fill a
/// rectangle, copy a rectangle, and draw a line.
///
/// Windows' display driver is a thin layer over those three. Scrolling a
/// window is a copy, clearing it is a fill, and the border is four lines --
/// and once the card does them, a 486 can move windows about smoothly on a
/// screen it could not possibly repaint pixel by pixel.
///
/// The register layout is the 8514/A's, kept for compatibility: ports at
/// 0x82E8 and upwards, each a fixed sixteen bits, addressed by their own
/// address rather than through an index register. It is a wasteful use of I/O
/// space and it is why the addresses look arbitrary -- they are the ones IBM
/// happened to choose in 1987.
/// </summary>
public sealed partial class S3Accelerator : IIoDwordDevice
{
    // MULT_MISC selects the 32-bit PIO form with CMR32B. In its normal
    // sequential form, RSF selects the next half of every colour register.
    private const ushort MultiMiscRegisterSelectFlip = 0x0010;
    private const ushort MultiMiscColourRegister32Bit = 0x0200;

    private readonly VgaMemory _memory;
    private readonly VgaRegisters _registers;

    public S3Accelerator(VgaMemory memory, VgaRegisters registers)
    {
        _memory = memory;
        _registers = registers;
    }

    // --- the register file ----------------------------------------------------
    //
    // Each of these is one port. The names are the 8514/A's, and several mean
    // different things depending on the command -- CUR_X is the destination
    // for a fill and the source for a copy -- which is the sort of economy
    // that made sense when every register cost pins.

    private ushort _currentX, _currentY;         // 0x86E8, 0x86EA
    private ushort _destinationX, _destinationY; // 0x8EE8, 0x8EEA
    private ushort _width, _height;              // 0x96E8, 0x8EE8
    // The five colour registers are 32 bits in the Trio64V+ register file.
    // In 8- and 16-bpp modes only their low bits reach the framebuffer.
    private uint _foreground, _background;       // 0xA6E8, 0xA2E8
    // The power-on value is undefined.  Until the driver has written it, use
    // the conventional all-planes value; once written, zero is a real mask
    // value and prevents every update.
    private uint _writeMask;
    private bool _writeMaskWritten;
    private uint _readMask;                       // 0xAAE8, 0xAEE8
    private uint _colourCompare;                  // 0xB2E8
    private ushort _command;                     // 0x9AE8

    /// <summary>
    /// The raster operation, from 0xBAE8: the mix in the low four bits and,
    /// in bits 6-5, where the colour comes from -- the background register,
    /// the foreground one, the processor or the screen. It powers on as
    /// plain replacement in the foreground colour, because a zero here would
    /// mean "clear" and a driver that had not set it yet would find every
    /// draw painting black.
    /// </summary>
    private ushort _mix = 0x27;

    /// <summary>The same for pixels the pattern leaves as background, from 0xB6E8.</summary>
    private ushort _backgroundMix = 0x07;

    /// <summary>Clipping: nothing is drawn outside this rectangle.</summary>
    private ushort _clipTop, _clipLeft, _clipBottom, _clipRight;

    /// <summary>The pixel control register, which holds the colour compare condition.</summary>
    private ushort _pixelControl;

    /// <summary>Operations performed, which the tests read.</summary>
    public long OperationsPerformed { get; private set; }

    /// <summary>
    /// Diagnostics: every command as it executes, with the operation bits
    /// and the rectangle it names. What a driver asks the engine to do is
    /// the only evidence there is when the screen comes out wrong.
    /// </summary>
    public Action<ushort, int, int, int, int, uint, ushort, ushort>? CommandTrace { get; set; }

    /// <summary>
    /// The same registers, reached through memory instead of I/O ports.
    ///
    /// Every register of the drawing engine has an address in a 64 KB window
    /// as well as an I/O port, and a driver in protected mode uses the window
    /// -- because an OUT instruction from a protected-mode driver is a fault
    /// the monitor has to catch and emulate, while a memory write is just a
    /// memory write. On a 486 that is the difference between a few hundred
    /// cycles per register and a few.
    ///
    /// The mapping is the port address with the top bits replaced: port
    /// 0x82E8 becomes offset 0x82E8 in the window, and so on, which makes the
    /// two interfaces the same register file seen twice.
    /// </summary>
    public sealed class MemoryMappedRegisters(S3Accelerator engine) : IMemoryDevice
    {
        public byte ReadByte(uint offset)
        {
            return engine.ReadMmioByte((ushort)offset);
        }

        public ushort ReadWord(uint offset) => engine.ReadMmioWord((ushort)(offset & 0xFFFE));

        public uint ReadDword(uint offset) =>
            (uint)(engine.ReadMmioWord((ushort)(offset & 0xFFFC)) | (engine.ReadMmioWord((ushort)((offset & 0xFFFC) + 2)) << 16));

        // The accelerator decodes byte accesses to its registers immediately.
        // This matters to the Windows S3 driver, which writes BAE8h once with
        // the foreground mix and streams glyph bytes one at a time to E2E8h.
        // The command register is the exception: its high byte starts work,
        // so the low half can be installed without firing a partial command.
        public void WriteByte(uint offset, byte value)
        {
            engine.WriteMmioByte((ushort)offset, value);
        }

        public void WriteWord(uint offset, ushort value)
        {
            if (offset < 0x8000) { engine.PushDataByte((byte)value); engine.PushDataByte((byte)(value >> 8)); return; }
            engine.WriteMmioWord((ushort)(offset & 0xFFFE), value);
        }

        public void WriteDword(uint offset, uint value)
        {
            if (offset < 0x8000) { for (int i = 0; i < 4; i++) engine.PushDataByte((byte)(value >> (8 * i))); return; }
            engine.WriteMmioWord((ushort)(offset & 0xFFFC), (ushort)value);
            engine.WriteMmioWord((ushort)((offset & 0xFFFC) + 2), (ushort)(value >> 16));
        }
    }

    /// <summary>
    /// A register written through memory. Above 0x8200 the offset is the
    /// port number and the register is the same one; from 0x8100 to 0x81FF
    /// sit the Trio64's packed registers, pairs of 16-bit values at
    /// doubleword addresses so that a driver sets a coordinate pair or a
    /// width and height with one 32-bit write: current Y and X, destination
    /// Y and X, the command, the colours, the mixes, the clip rectangle, the
    /// pixel control and the two miscellaneous registers, the two axis
    /// counts, the pattern position. The S3 Windows driver uses nothing
    /// else for drawing.
    /// </summary>
    internal void WriteMmioWord(ushort offset, ushort value)
    {
        switch (offset)
        {
            case 0x8100: _currentY = value; return;
            case 0x8102: _currentX = value; return;
            case 0x8108: _destinationY = value; return;
            case 0x810A: _destinationX = value; return;
            case 0x8110: return;                        // error term: the line takes endpoints here
            case 0x8118: _command = value; Execute(); return;
            case 0x811A: return;                        // short stroke vectors
            case 0x8120: WritePackedColourWord(0x8120, value); return;
            case 0x8122: WritePackedColourWord(0x8122, value); return;
            case 0x8124: WritePackedColourWord(0x8124, value); return;
            case 0x8126: WritePackedColourWord(0x8126, value); return;
            case 0x8128: WritePackedColourWord(0x8128, value); return;
            case 0x812A: WritePackedColourWord(0x812A, value); return;
            case 0x812C: WritePackedColourWord(0x812C, value); return;
            case 0x812E: WritePackedColourWord(0x812E, value); return;
            case 0x8130: WritePackedColourWord(0x8130, value); return;
            case 0x8132: WritePackedColourWord(0x8132, value); return;
            case 0x8134: _backgroundMix = value; return;
            case 0x8136: _mix = value; return;
            case 0x8138: _clipTop = (ushort)(value & 0x0FFF); return;
            case 0x813A: _clipLeft = (ushort)(value & 0x0FFF); return;
            case 0x813C: _clipBottom = (ushort)(value & 0x0FFF); return;
            case 0x813E: _clipRight = (ushort)(value & 0x0FFF); return;
            case 0x8140: _pixelControl = value; return;
            case 0x8142: _multiMisc2 = (ushort)(value & 0x0FFF); return;
            case 0x8144: _multiMisc = (ushort)(value & 0x0FFF); return;
            case 0x8146: _readSelect = value & 0x0F; return;
            case 0x8148: _height = value; return;       // ALT_PCNT: the minor count low, the major high
            case 0x814A: _width = value; return;
            case 0x814C: _patternY = value; return;      // ALT_PAT: Y low, X high, like the others
            case 0x814E: _patternX = value; return;
        }

        // In a 32-bit sequential mode, classic MMIO uses RSF just as the
        // reference model does. The real driver writes C0C0h then 00C0h to
        // AA6E8h, rather than addressing the second half at AA6EAh.
        if (IsClassicMmioColourRegister(offset))
        {
            if (UsesSequentialClassicMmioColourWords)
            {
                WriteSequentialColourWord((ushort)(offset & 0xFFFD), value);
            }
            else
            {
                WriteClassicMmioColourWord(offset, value);
            }

            return;
        }

        if (offset >= 0x8200 && (offset & 3) == 0 || offset is 0x82E8 or 0x86E8 or 0x8AE8 or 0x8EE8 or 0x92E8 or 0x96E8 or 0x9AE8 or 0x9EE8
            or 0xB6E8 or 0xBAE8 or 0xBEE8 or 0xE2E8 or 0xE2EA)
            WriteRegister(offset, value);
    }

    /// <summary>
    /// Writes one byte through the MMIO aperture. The S3 accepts byte stores
    /// to its ordinary registers immediately, retaining the untouched half
    /// of the register. PIX_TRANS is byte-addressable data, not a word
    /// register. CMD is deliberately different: its high byte commits the
    /// command, as described in the Trio64V+ programming model.
    /// </summary>
    internal void WriteMmioByte(ushort offset, byte value)
    {
        if (offset < 0x8000 || offset is >= 0xE2E8 and <= 0xE2EB)
        {
            PushDataByte(value);
            return;
        }

        ushort register = (ushort)(offset & 0xFFFE);

        if (register is 0x8118 or 0x9AE8)
        {
            if ((offset & 1) == 0)
            {
                _command = (ushort)((_command & 0xFF00) | value);
                return;
            }

            _command = (ushort)((_command & 0x00FF) | (value << 8));
            Execute();
            return;
        }

        // BEE8 is a multiplexed register. Its low byte supplies the data,
        // while its high byte supplies the index and commits both together.
        // Applying the low byte immediately would send it through whichever
        // index the previous command selected, moving a clip edge before the
        // driver had named the new one.
        if (register == 0xBEE8)
        {
            if ((offset & 1) == 0)
            {
                _lastMultifunctionCommand = (ushort)((_lastMultifunctionCommand & 0xFF00) | value);
                return;
            }

            _lastMultifunctionCommand = (ushort)((_lastMultifunctionCommand & 0x00FF) | (value << 8));
            WriteMultifunction(_lastMultifunctionCommand);
            return;
        }

        if (IsClassicMmioColourRegister(register)
            && UsesSequentialClassicMmioColourWords)
        {
            WriteClassicMmioColourByte(offset, value);
            return;
        }

        ushort existing = MmioValueForByteWrite(register);
        ushort word;

        if ((offset & 1) == 0)
        {
            word = (ushort)((existing & 0xFF00) | value);
        }
        else
        {
            word = (ushort)((existing & 0x00FF) | (value << 8));
        }

        WriteMmioWord(register, word);
    }

    private ushort MmioValueForByteWrite(ushort offset)
    {
        return offset switch
        {
            0x8100 => _currentY,
            0x8102 => _currentX,
            0x8108 => _destinationY,
            0x810A => _destinationX,
            0x8118 => _command,
            0x8120 => (ushort)_background,
            0x8122 => (ushort)(_background >> 16),
            0x8124 => (ushort)_foreground,
            0x8126 => (ushort)(_foreground >> 16),
            0x8128 => (ushort)_writeMask,
            0x812A => (ushort)(_writeMask >> 16),
            0x812C => (ushort)_readMask,
            0x812E => (ushort)(_readMask >> 16),
            0x8130 => (ushort)_colourCompare,
            0x8132 => (ushort)(_colourCompare >> 16),
            0x8134 => _backgroundMix,
            0x8136 => _mix,
            0x8138 => _clipTop,
            0x813A => _clipLeft,
            0x813C => _clipBottom,
            0x813E => _clipRight,
            0x8140 => _pixelControl,
            0x8142 => _multiMisc2,
            0x8144 => _multiMisc,
            0x8146 => (ushort)_readSelect,
            0x8148 => _height,
            0x814A => _width,
            0x814C => _patternY,
            0x814E => _patternX,
            0x82E8 => _currentY,
            0x86E8 => _currentX,
            0x8AE8 => _destinationY,
            0x8EE8 => _destinationX,
            0x92E8 => _height,
            0x96E8 => _width,
            0x9AE8 => _command,
            0xA2E8 => (ushort)_background,
            0xA2EA => (ushort)(_background >> 16),
            0xA6E8 => (ushort)_foreground,
            0xA6EA => (ushort)(_foreground >> 16),
            0xAAE8 => (ushort)_writeMask,
            0xAAEA => (ushort)(_writeMask >> 16),
            0xAEE8 => (ushort)_readMask,
            0xAEEA => (ushort)(_readMask >> 16),
            0xB2E8 => (ushort)_colourCompare,
            0xB2EA => (ushort)(_colourCompare >> 16),
            0xB6E8 => _backgroundMix,
            0xBAE8 => _mix,
            0xBEE8 => _lastMultifunctionCommand,
            _ => 0,
        };
    }

    internal ushort ReadMmioWord(ushort offset)
    {
        if (IsClassicMmioColourRegister(offset)
            && UsesSequentialClassicMmioColourWords
            && TryReadColourRegister((ushort)(offset & 0xFFFD), out uint colour))
        {
            return ReadSequentialColourWord(colour);
        }

        return offset switch
        {
            0x8100 => _currentY,
            0x8102 => _currentX,
            0x8120 => (ushort)_background,
            0x8122 => (ushort)(_background >> 16),
            0x8124 => (ushort)_foreground,
            0x8126 => (ushort)(_foreground >> 16),
            0x8128 => (ushort)_writeMask,
            0x812A => (ushort)(_writeMask >> 16),
            0x812C => (ushort)_readMask,
            0x812E => (ushort)(_readMask >> 16),
            0x8130 => (ushort)_colourCompare,
            0x8132 => (ushort)(_colourCompare >> 16),
            0x8138 => _clipTop, 0x813A => _clipLeft, 0x813C => _clipBottom, 0x813E => _clipRight,
            0x8140 => _pixelControl, 0x8142 => _multiMisc2, 0x8144 => _multiMisc,
            0x8148 => _height, 0x814A => _width,
            0xA2E8 => (ushort)_background,
            0xA2EA => (ushort)(_background >> 16),
            0xA6E8 => (ushort)_foreground,
            0xA6EA => (ushort)(_foreground >> 16),
            0xAAE8 => (ushort)_writeMask,
            0xAAEA => (ushort)(_writeMask >> 16),
            0xAEE8 => (ushort)_readMask,
            0xAEEA => (ushort)(_readMask >> 16),
            0xB2E8 => (ushort)_colourCompare,
            0xB2EA => (ushort)(_colourCompare >> 16),
            _ => ReadRegister(offset),
        };
    }

    /// <summary>
    /// Reads one byte from classic MMIO without advancing RSF until the high
    /// byte completes its selected word. Calling the word reader for both
    /// bytes would skip the second colour half before the driver reads it.
    /// </summary>
    internal byte ReadMmioByte(ushort offset)
    {
        ushort wordOffset = (ushort)(offset & 0xFFFE);

        if (IsClassicMmioColourRegister(wordOffset)
            && UsesSequentialClassicMmioColourWords
            && TryReadColourRegister((ushort)(wordOffset & 0xFFFD), out uint colour))
        {
            bool highByte = (offset & 1) != 0;
            ushort word = ReadSequentialColourWord(colour, highByte);
            return highByte ? (byte)(word >> 8) : (byte)word;
        }

        ushort value = ReadMmioWord(wordOffset);
        return (offset & 1) != 0 ? (byte)(value >> 8) : (byte)value;
    }

    /// <summary>The engine's register file, addressed as memory.</summary>
    public MemoryMappedRegisters Mmio => _mmio ??= new MemoryMappedRegisters(this);
    private MemoryMappedRegisters? _mmio;

    public void Register(IoBus io)
    {
        // The 8514/A ports, each sixteen bits wide. They are scattered because
        // IBM chose addresses that did not clash with anything else in 1987,
        // not because there is a pattern to find.
        foreach (ushort port in Ports)
        {
            int width = IsColourRegister(port) || port == 0xE2E8 ? 4 : 2;
            io.Register(port, width, this);
        }
    }

    private static readonly ushort[] Ports =
    [
        0x82E8, 0x86E8, 0x8AE8, 0x8EE8, 0x92E8, 0x96E8, 0x9AE8, 0x9EE8,
        0xA2E8, 0xA6E8, 0xAAE8, 0xAEE8, 0xB2E8, 0xB6E8, 0xBAE8, 0xBEE8,
        0xE2E8,
    ];

    public byte ReadByte(ushort port)
    {
        ushort register = (ushort)(port & ~1);

        if (Is32BitPixelMode
            && !UsesPioDwordColourRegisters
            && TryReadColourRegister(register, out uint colour))
        {
            bool highByte = (port & 1) != 0;
            ushort colourWord = ReadSequentialColourWord(colour, highByte);
            return highByte ? (byte)(colourWord >> 8) : (byte)colourWord;
        }

        ushort word = ReadRegister(register);
        return (port & 1) != 0 ? (byte)(word >> 8) : (byte)word;
    }

    public ushort ReadWord(ushort port) => ReadRegister((ushort)(port & ~1));

    /// <summary>
    /// The registers a driver reads back. 0x9AE8 is the status: bit 9 says
    /// the engine is busy, and ours never is -- the operation completed
    /// before the write that started it returned -- so a wait loop finds it
    /// idle at once. 0xBEE8 returns whichever multifunction register the
    /// read-select register named, and the selection advances after each
    /// read, as the Trio64 does; the S3 Windows driver reads a register
    /// back at start-up to satisfy itself that the engine is there.
    /// </summary>
    private ushort ReadRegister(ushort port)
    {
        switch (port)
        {
            case 0x9AE8: return 0x0000;
            case 0x82E8: return _currentY;
            case 0x86E8: return _currentX;
            case 0x96E8: return _width;
            case 0xA2E8: return ReadSequentialColourWord(_background);
            case 0xA6E8: return ReadSequentialColourWord(_foreground);
            case 0xAAE8: return ReadSequentialColourWord(_writeMask);
            case 0xAEE8: return ReadSequentialColourWord(_readMask);
            case 0xB2E8: return ReadSequentialColourWord(_colourCompare);
            case 0xBEE8: return ReadMultifunction();
            default: return 0xFFFF;
        }
    }

    private ushort ReadMultifunction()
    {
        int selected = _readSelect;
        _readSelect = (_readSelect + 1) & 0x0F;
        return selected switch
        {
            0 => _height,
            1 => _clipTop,
            2 => _clipLeft,
            3 => _clipBottom,
            4 => _clipRight,
            5 => _pixelControl,
            6 => _multiMisc2,
            7 => _multiMisc,
            _ => 0,
        };
    }

    public void WriteByte(ushort port, byte value)
    {
        // The ports are sixteen bits and drivers write them as words. A word
        // written through the bus arrives here as two bytes, the low one
        // first; the low half is kept until the high one completes the pair,
        // and the register is written then, as the chip does.
        ushort address = (ushort)(port & ~1);
        if ((port & 1) == 0)
        {
            _pendingLow[address] = value;
            return;
        }

        ushort word = (ushort)(_pendingLow.GetValueOrDefault(address) | (value << 8));
        WriteRegister(address, word);
    }

    public void WriteWord(ushort port, ushort value) => WriteRegister((ushort)(port & ~1), value);

    public uint ReadDword(ushort port)
    {
        ushort register = (ushort)(port & 0xFFFE);

        if (UsesPioDwordColourRegisters && TryReadColourRegister(register, out uint value))
        {
            return value;
        }

        ushort low = ReadWord(register);
        ushort high = ReadWord((ushort)(register + 2));
        return (uint)(low | (high << 16));
    }

    public void WriteDword(ushort port, uint value)
    {
        ushort register = (ushort)(port & 0xFFFE);

        if (UsesPioDwordColourRegisters && TryWriteColourRegister(register, value))
        {
            return;
        }

        if (register == 0xE2E8)
        {
            PushDataByte((byte)value);
            PushDataByte((byte)(value >> 8));
            PushDataByte((byte)(value >> 16));
            PushDataByte((byte)(value >> 24));
            return;
        }

        WriteWord(register, (ushort)value);
        WriteWord((ushort)(register + 2), (ushort)(value >> 16));
    }

    private readonly Dictionary<ushort, byte> _pendingLow = [];
    private int _readSelect;
    private ushort _multiMisc;
    private ushort _multiMisc2;
    private ushort _patternX;
    private ushort _patternY;
    private ushort _lastMultifunctionCommand;

    /// <summary>Whether the engine stores one pixel in all four colour bytes.</summary>
    private bool Is32BitPixelMode => BytesPerPixel == 4;

    /// <summary>
    /// Whether MULT_MISC bit 9 selects the programmed-I/O doubleword form.
    /// The data book makes byte and word PIO accesses invalid in this state;
    /// classic MMIO continues to expose direct colour lanes.
    /// </summary>
    private bool UsesPioDwordColourRegisters => (_multiMisc & MultiMiscColourRegister32Bit) != 0;

    /// <summary>
    /// Whether classic MMIO uses the sequential colour half selected by RSF.
    /// The Trio64V+ manual describes RSF for PIO, while the reference MMIO
    /// model and the Windows driver show the same selection for these words.
    /// </summary>
    private bool UsesSequentialClassicMmioColourWords => Is32BitPixelMode && !UsesPioDwordColourRegisters;

    /// <summary>Whether a classic port names one of the five colour registers.</summary>
    private static bool IsColourRegister(ushort port)
    {
        return port is 0xA2E8 or 0xA6E8 or 0xAAE8 or 0xAEE8 or 0xB2E8;
    }

    /// <summary>
    /// Whether a classic MMIO offset names either word of a colour register.
    /// Clearing bit one turns its high-word address back into the base port.
    /// </summary>
    private static bool IsClassicMmioColourRegister(ushort offset)
    {
        ushort register = (ushort)(offset & 0xFFFD);
        return IsColourRegister(register);
    }

    /// <summary>Replaces either 16-bit lane of a 32-bit colour register.</summary>
    private static uint ReplaceColourWord(uint colour, ushort value, bool upperWord)
    {
        if (upperWord)
        {
            return (colour & 0x0000FFFF) | ((uint)value << 16);
        }

        return (colour & 0xFFFF0000) | value;
    }

    /// <summary>Writes a packed-MMIO colour lane through its classic register.</summary>
    private void WritePackedColourWord(ushort offset, ushort value)
    {
        bool upperWord = (offset & 0x0002) != 0;
        ushort register = (ushort)(offset & 0xFFFD);

        switch (register)
        {
            case 0x8120:
                WriteColourWord(0xA2E8, value, upperWord);
                return;

            case 0x8124:
                WriteColourWord(0xA6E8, value, upperWord);
                return;

            case 0x8128:
                WriteColourWord(0xAAE8, value, upperWord);
                return;

            case 0x812C:
                WriteColourWord(0xAEE8, value, upperWord);
                return;

            case 0x8130:
                WriteColourWord(0xB2E8, value, upperWord);
                return;
        }
    }

    /// <summary>
    /// Writes either direct lane of a classic MMIO colour register. CMR32B
    /// selects this form; with it clear, the sequential path above uses RSF.
    /// </summary>
    private void WriteClassicMmioColourWord(ushort offset, ushort value)
    {
        bool upperWord = (offset & 0x0002) != 0;
        ushort register = (ushort)(offset & 0xFFFD);
        WriteColourWord(register, value, upperWord);
    }

    /// <summary>
    /// Writes a sequential colour word. In 32-bpp sequential mode,
    /// MULT_MISC bit 4 selects the active half and flips after that word.
    /// Classic MMIO follows this behaviour when CMR32B is clear; the driver
    /// uses two WORD stores to one address, C0C0h then 00C0h, for grey.
    /// </summary>
    private void WriteSequentialColourWord(ushort register, ushort value)
    {
        bool upperWord = Is32BitPixelMode && (_multiMisc & MultiMiscRegisterSelectFlip) != 0;
        WriteColourWord(register, value, upperWord);

        if (Is32BitPixelMode && !UsesPioDwordColourRegisters)
        {
            AdvanceColourWordSelector();
        }
    }

    /// <summary>
    /// Writes one byte into the word currently selected by RSF. The low byte
    /// is visible immediately, but the high byte is what completes the word
    /// and advances the selector to the other 16-bit half.
    /// </summary>
    private void WriteClassicMmioColourByte(ushort offset, byte value)
    {
        ushort wordOffset = (ushort)(offset & 0xFFFE);
        ushort register = (ushort)(wordOffset & 0xFFFD);

        if (!TryReadColourRegister(register, out uint colour))
        {
            return;
        }

        bool upperWord = (_multiMisc & MultiMiscRegisterSelectFlip) != 0;
        ushort selectedWord = upperWord ? (ushort)(colour >> 16) : (ushort)colour;
        bool highByte = (offset & 1) != 0;

        ushort updatedWord;
        if (highByte)
        {
            updatedWord = (ushort)((selectedWord & 0x00FF) | (value << 8));
        }
        else
        {
            updatedWord = (ushort)((selectedWord & 0xFF00) | value);
        }

        WriteColourWord(register, updatedWord, upperWord);

        if (highByte)
        {
            AdvanceColourWordSelector();
        }
    }

    /// <summary>Writes a selected 16-bit lane into one colour register.</summary>
    private void WriteColourWord(ushort register, ushort value, bool upperWord)
    {
        switch (register)
        {
            case 0xA2E8:
                _background = ReplaceColourWord(_background, value, upperWord);
                return;

            case 0xA6E8:
                _foreground = ReplaceColourWord(_foreground, value, upperWord);
                return;

            case 0xAAE8:
                _writeMask = ReplaceColourWord(_writeMask, value, upperWord);
                _writeMaskWritten = true;
                return;

            case 0xAEE8:
                _readMask = ReplaceColourWord(_readMask, value, upperWord);
                return;

            case 0xB2E8:
                _colourCompare = ReplaceColourWord(_colourCompare, value, upperWord);
                return;
        }
    }

    /// <summary>
    /// Reads a sequential colour word, optionally advancing the RSF
    /// selector after its high byte completes the word access.
    /// </summary>
    private ushort ReadSequentialColourWord(uint colour, bool advance = true)
    {
        bool upperWord = Is32BitPixelMode && (_multiMisc & MultiMiscRegisterSelectFlip) != 0;
        ushort value = upperWord ? (ushort)(colour >> 16) : (ushort)colour;

        if (advance && Is32BitPixelMode && !UsesPioDwordColourRegisters)
        {
            AdvanceColourWordSelector();
        }

        return value;
    }

    /// <summary>Moves RSF to the other half of a sequential colour register.</summary>
    private void AdvanceColourWordSelector()
    {
        _multiMisc ^= MultiMiscRegisterSelectFlip;
    }

    /// <summary>Gets a complete colour register for a direct DWORD PIO access.</summary>
    private bool TryReadColourRegister(ushort register, out uint value)
    {
        switch (register)
        {
            case 0xA2E8:
                value = _background;
                return true;

            case 0xA6E8:
                value = _foreground;
                return true;

            case 0xAAE8:
                value = _writeMask;
                return true;

            case 0xAEE8:
                value = _readMask;
                return true;

            case 0xB2E8:
                value = _colourCompare;
                return true;

            default:
                value = 0;
                return false;
        }
    }

    /// <summary>Writes a complete colour register for a direct DWORD PIO access.</summary>
    private bool TryWriteColourRegister(ushort register, uint value)
    {
        if (!IsColourRegister(register))
        {
            return false;
        }

        WriteColourWord(register, (ushort)value, false);
        WriteColourWord(register, (ushort)(value >> 16), true);
        return true;
    }

    private void WriteRegister(ushort port, ushort value)
    {
        switch (port)
        {
            case 0x82E8: _currentY = value; return;
            case 0x86E8: _currentX = value; return;
            case 0x8AE8: _destinationY = value; return;
            case 0x8EE8: _destinationX = value; return;

            // 0x96E8 is the width and 0x8EE8 the height, and the height is
            // written first because it shares its port with the destination
            // Y -- one of the economies that makes this register file
            // confusing to read and cheap to build.
            case 0x92E8: _height = value; return;
            case 0x96E8: _width = value; return;

            case 0xA2E8: WriteSequentialColourWord(0xA2E8, value); return;
            case 0xA6E8: WriteSequentialColourWord(0xA6E8, value); return;
            case 0xAAE8: WriteSequentialColourWord(0xAAE8, value); return;
            case 0xAEE8: WriteSequentialColourWord(0xAEE8, value); return;
            case 0xB2E8: WriteSequentialColourWord(0xB2E8, value); return;
            case 0xB6E8: _backgroundMix = value; return;
            case 0xBAE8: _mix = value; return;

            // 0xBEE8 is a bank of registers behind one port: the top four bits
            // of the value say which. Clipping lives here, and so does the
            // pixel control that decides where a pattern comes from.
            case 0xBEE8: WriteMultifunction(value); return;

            // 0xE2E8 is the pixel data port: everything the processor sends
            // the engine arrives here, a word at a time, or a doubleword
            // when the high half comes through 0xE2EA.
            case 0xE2E8:
            case 0xE2EA:
                PushDataByte((byte)value);
                PushDataByte((byte)(value >> 8));
                return;

            // Writing the command register is what starts the operation. Every
            // other register is set up first and then this one fires.
            case 0x9AE8: _command = value; Execute(); return;
        }
    }

    /// <summary>
    /// The multifunction register at 0xBEE8: sixteen registers behind one
    /// port, chosen by the top four bits of the value written.
    ///
    /// It exists because the 8514/A had run out of I/O addresses, and it is
    /// the reason a driver's setup code looks like a list of magic numbers:
    /// 0x1000 plus a value is the top clip, 0x2000 plus a value the left, and
    /// so on.
    /// </summary>
    private void WriteMultifunction(ushort value)
    {
        _lastMultifunctionCommand = value;
        int data = value & 0x0FFF;

        switch (value >> 12)
        {
            // 0xA000 is the pixel control register, which holds the colour
            // compare condition among other things.
            case 0xA: _pixelControl = (ushort)data; return;

            case 0x0: _height = (ushort)data; return;   // MIN_AXIS_PCNT again, by another door
            case 0x1: _clipTop = (ushort)data; return;
            case 0x2: _clipLeft = (ushort)data; return;
            case 0x3: _clipBottom = (ushort)data; return;
            case 0x4: _clipRight = (ushort)data; return;

            // The miscellaneous registers choose how colour registers are
            // accessed, while read-select chooses what this port returns.
            // A driver writes them and reads them back, and a card that
            // answers with 0xFFFF is a card the driver decides is not there.
            case 0xD: _multiMisc2 = (ushort)data; return;
            case 0xE: _multiMisc = (ushort)data; return;
            case 0xF: _readSelect = data & 0x0F; return;
        }
    }

    /// <summary>
    /// Performs whatever the command register asked for.
    ///
    /// The command's top three bits pick the operation, and the rest are
    /// modifiers -- which direction to draw in, whether the source is the
    /// foreground colour or the screen, how wide a pixel is. The three that
    /// matter are the ones every graphical system is built from.
    /// </summary>
    private void Execute()
    {
        ResetSequentialColourSelector();

        int operation = (_command >> 13) & 7;
        CommandTrace?.Invoke(_command, _currentX, _currentY, _width + 1, _height + 1, _foreground, _mix, _pixelControl);
        _transfer = null;
        _unit = 0;
        _unitBytes = 0;
        _processorPixel = 0;
        _processorPixelBytes = 0;

        // Every drawing routine below addresses video memory as a flat array
        // of pixels, which is what the engine sees on a real card: the S3's
        // accelerator works on the packed framebuffer and has no path to the
        // VGA's four planes. A command issued while the card is in a planar
        // mode is therefore not something to carry out approximately -- the
        // bytes would land at linear offsets over planar memory and corrupt
        // whatever is on screen. Refusing is both safer and truer: a driver
        // that sets a planar mode uses the processor, not the engine.
        if (!HasLinearFramebuffer) return;

        // The encoding is the 8514/A's, kept by every S3: 1 draws a line, 2
        // fills a rectangle, 3 fills a polygon (a rectangle here: the
        // polygon form differs only in how the edges are given), 6 copies
        // a rectangle, 7 fills with a pattern taken from the screen. It
        // used to say 2 was the line and 3 the fill, and a driver's every
        // fill came out as a line along its top edge.
        switch (operation)
        {
            case 1: DrawLine(); break;
            case 2 or 3: FillRectangle(); break;
            case 6: CopyRectangle(); break;
            case 7: PatternFill(); break;
        }

        OperationsPerformed++;
    }

    /// <summary>
    /// A completed command starts the next colour sequence at its low word.
    /// The low CMD byte merely prepares the command, so only the high byte
    /// that reaches <see cref="Execute"/> can reset RSF.
    /// </summary>
    private void ResetSequentialColourSelector()
    {
        if (UsesSequentialClassicMmioColourWords)
        {
            _multiMisc = (ushort)(_multiMisc & ~MultiMiscRegisterSelectFlip);
        }
    }

    /// <summary>
    /// Fills a rectangle: the operation a graphical system does more than any
    /// other, because clearing a window, painting a background and drawing a
    /// scroll bar are all this.
    /// </summary>
    private void FillRectangle()
    {
        // Bit 8 -- "PCDATA" -- says the pixels come from the processor: the
        // engine takes the rectangle and then waits for data through the
        // pixel transfer port or the image transfer area, painting as it
        // arrives. It is how a driver uploads a brush, draws text from a
        // glyph bitmap or copies an image in: the rectangle is the frame,
        // the processor supplies what goes in it.
        if ((_command & 0x0100) != 0)
        {
            _transfer = new Transfer(_currentX, _currentY, _width + 1, _height + 1,
                                     (_command & 0x0020) != 0, (_command & 0x0080) != 0,
                                     (_pixelControl & 0x00C0) == 0x0080);
            return;
        }

        int width = _width + 1;
        int height = _height + 1;

        int horizontalStep = (_command & 0x0020) != 0 ? 1 : -1;
        int verticalStep = (_command & 0x0080) != 0 ? 1 : -1;

        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
            {
                int x = _currentX + column * horizontalStep;
                int y = _currentY + row * verticalStep;
                PutPixel(x, y, ForegroundSource(x, y), _mix);
            }
        }
    }

    /// <summary>
    /// The colour a foreground pixel takes: bits 6-5 of the mix register
    /// say whether it is the background colour, the foreground colour, the
    /// processor's data or what is already on the screen at the source.
    /// </summary>
    private uint ForegroundSource(int x, int y) => ((_mix >> 5) & 3) switch
    {
        0 => _background,
        3 => GetPixel(x, y),
        _ => _foreground,
    };

    private uint BackgroundSource(int x, int y) => ((_backgroundMix >> 5) & 3) switch
    {
        1 => _foreground,
        3 => GetPixel(x, y),
        _ => _background,
    };

    // --- data from the processor ------------------------------------------------

    /// <summary>
    /// A rectangle waiting for its pixels from the processor. The cursor
    /// walks it in the command's directions, one pixel per unit of data in
    /// colour mode and one per bit in mask mode, where each bit picks the
    /// foreground mix or the background one -- which is how a font glyph
    /// is drawn in one pass.
    /// </summary>
    private sealed class Transfer(int left, int top, int width, int height, bool rightwards, bool downwards, bool mask,
        int column = 0, int row = 0)
    {
        public readonly bool Mask = mask;
        private int _column = column;
        private int _row = row;

        public uint[] Capture() =>
        [
            (uint)left, (uint)top, (uint)width, (uint)height,
            rightwards ? 1u : 0u, downwards ? 1u : 0u, Mask ? 1u : 0u,
            (uint)_column, (uint)_row,
        ];

        public bool Done => _row >= height;
        public bool RowStarting => _column == 0;

        /// <summary>The next pixel's position, or null when the rectangle is full.</summary>
        public (int X, int Y)? Next()
        {
            if (Done) return null;
            int x = rightwards ? left + _column : left - _column;
            int y = downwards ? top + _row : top - _row;
            if (++_column >= width) { _column = 0; _row++; }
            return (x, y);
        }
    }

    private Transfer? _transfer;
    private uint _unit;
    private int _unitBytes;
    private uint _processorPixel;
    private int _processorPixelBytes;

    /// <summary>
    /// The width of one unit of processor data, in bytes: bits 10-9 of the
    /// command say whether the driver sends bytes, words or doublewords.
    /// Each row of the rectangle starts on a fresh unit; whatever is left of
    /// the previous one is thrown away, which is the padding a driver's
    /// bitmap rows carry.
    /// </summary>
    private int UnitBytes => ((_command >> 9) & 3) switch { 0 => 1, 1 => 2, _ => 4 };

    private bool HasNew32BitTransfer => ((_command >> 9) & 3) == 3;

    /// <summary>
    /// One byte of data from the processor, by whichever door it came
    /// through. Bytes are gathered into a unit and the unit is consumed
    /// whole; bit 12 of the command -- BYTSEQ -- says whether the low byte
    /// of a unit is the first pixel or the last.
    /// </summary>
    internal void PushDataByte(byte value)
    {
        if (_transfer is null || _transfer.Done) return;

        _unit |= (uint)value << (8 * _unitBytes);
        if (++_unitBytes < UnitBytes) return;

        uint unit = _unit;
        int bytes = _unitBytes;
        _unit = 0;
        _unitBytes = 0;
        if (HasNew32BitTransfer)
        {
            ConsumeNew32BitUnit(unit, bytes);
        }
        else
        {
            ConsumeUnit(unit, bytes);
        }
    }

    /// <summary>
    /// The Trio's BUS_SIZE=11b form still accepts doubleword writes, but it
    /// does not restart every scan line on a doubleword boundary.  It drops
    /// only the unused bits of the byte which ended a monochrome row, then
    /// continues with the next byte.  Icon rows whose width is not a multiple
    /// of 32 otherwise begin at the wrong source byte.
    /// </summary>
    private void ConsumeNew32BitUnit(uint unit, int bytes)
    {
        bool lowFirst = (_command & 0x1000) != 0;

        for (int i = 0; i < bytes; i++)
        {
            int sourceByteIndex = lowFirst ? i : bytes - 1 - i;
            byte value = (byte)(unit >> (8 * sourceByteIndex));

            if (_transfer!.Mask)
            {
                for (int bit = 7; bit >= 0; bit--)
                {
                    if (!PlotFromProcessor(((value >> bit) & 1) != 0, null))
                    {
                        return;
                    }

                    if (_transfer.RowStarting)
                    {
                        break;
                    }
                }

                continue;
            }

            _processorPixel |= (uint)value << (8 * _processorPixelBytes);
            _processorPixelBytes++;

            if (_processorPixelBytes < BytesPerPixel)
            {
                continue;
            }

            bool hasMorePixels = PlotFromProcessor(true, _processorPixel);
            _processorPixel = 0;
            _processorPixelBytes = 0;

            if (!hasMorePixels)
            {
                return;
            }
        }
    }

    private void ConsumeUnit(uint unit, int bytes)
    {
        bool lowFirst = (_command & 0x1000) != 0;
        int pixelBytes = _transfer!.Mask ? 1 : BytesPerPixel;

        for (int i = 0; i < bytes; i++)
        {
            int index = lowFirst ? i : bytes - 1 - i;
            byte b = (byte)(unit >> (8 * index));

            if (_transfer.Mask)
            {
                for (int bit = 7; bit >= 0; bit--)
                {
                    if (!PlotFromProcessor(((b >> bit) & 1) != 0, null)) return;
                    if (_transfer.RowStarting) return;      // the row ended: the rest of the unit is padding
                }
                continue;
            }

            _processorPixel |= (uint)b << (8 * _processorPixelBytes);
            _processorPixelBytes++;

            if (_processorPixelBytes < pixelBytes)
            {
                continue;
            }

            bool hasMorePixels = PlotFromProcessor(true, _processorPixel);
            _processorPixel = 0;
            _processorPixelBytes = 0;

            if (!hasMorePixels || _transfer.RowStarting)
            {
                return;
            }
        }
    }

    /// <summary>Paints the next pixel of the transfer; false when the rectangle is full.</summary>
    private bool PlotFromProcessor(bool foreground, uint? data)
    {
        if (_transfer!.Next() is not { } at) return false;

        if (data is { } colour && ((_mix >> 5) & 3) == 2)
            PutPixel(at.X, at.Y, colour, _mix);
        else if (foreground)
            PutPixel(at.X, at.Y, ForegroundSource(at.X, at.Y), _mix);
        else
            PutPixel(at.X, at.Y, BackgroundSource(at.X, at.Y), _backgroundMix);

        if (_transfer.Done) _transfer = null;
        return _transfer is not null;
    }

    /// <summary>
    /// Copies a rectangle from one place on the screen to another, which is
    /// what scrolling a window is.
    ///
    /// When the source and
    /// destination overlap -- and scrolling is exactly that -- copying from
    /// the top down would overwrite rows that have not been read yet. The
    /// driver picks the direction that reads each pixel before it is
    /// clobbered, and the card obeys rather than deciding for itself.
    /// </summary>
    private void CopyRectangle()
    {
        int width = _width + 1;
        int height = _height + 1;

        bool downwards = (_command & 0x0080) != 0;
        bool rightwards = (_command & 0x0020) != 0;

        for (int i = 0; i < height; i++)
        {
            // CUR and DEST name the first pixel transferred, not always the
            // top-left corner. For a window moved down/right the S3 driver
            // supplies both bottom-right corners and clears INC_X/INC_Y.
            // Reversing a loop over positive offsets would copy a different
            // rectangle entirely, leaving only fragments of the window.
            int y = downwards ? i : -i;

            for (int j = 0; j < width; j++)
            {
                int x = rightwards ? j : -j;

                int sourceX = _currentX + x;
                int sourceY = _currentY + y;
                uint sourcePixel = GetPixel(sourceX, sourceY);
                ushort mix = MixSelectedByBitmap(sourcePixel) ? _mix : _backgroundMix;
                uint colour = SourceForMix(mix, sourcePixel, sourceX, sourceY);
                PutPixel(_destinationX + x, _destinationY + y, colour, mix);
            }
        }
    }

    /// <summary>
    /// The pattern fill: an 8x8 tile of pixels already on the screen,
    /// repeated over the destination rectangle. A Windows driver keeps its
    /// brushes off-screen as pixels and paints a window's background with
    /// one of these, which is why the pattern comes from video memory and
    /// not from the data port. The source is where the current position
    /// points, the destination where the destination registers point, and
    /// the tile is anchored to the screen rather than to the rectangle, so
    /// that two adjoining fills line up instead of showing a seam.
    /// </summary>
    private void PatternFill()
    {
        int width = _width + 1;
        int height = _height + 1;

        int horizontalStep = (_command & 0x0020) != 0 ? 1 : -1;
        int verticalStep = (_command & 0x0080) != 0 ? 1 : -1;

        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
            {
                int destinationX = _destinationX + column * horizontalStep;
                int destinationY = _destinationY + row * verticalStep;
                int sourceX = _currentX + (destinationX & 7);
                int sourceY = _currentY + (destinationY & 7);
                uint sourcePixel = GetPixel(sourceX, sourceY);
                ushort mix = MixSelectedByBitmap(sourcePixel) ? _mix : _backgroundMix;
                uint colour = SourceForMix(mix, sourcePixel, sourceX, sourceY);
                PutPixel(destinationX, destinationY, colour, mix);
            }
        }
    }

    /// <summary>
    /// Pixel control normally selects the foreground mix.  With display
    /// memory selected, every enabled bit of the source pixel must be one to
    /// select it; a zero in any enabled plane selects the background mix.
    /// </summary>
    private bool MixSelectedByBitmap(uint sourcePixel)
    {
        if ((_pixelControl & 0x00C0) != 0x00C0)
        {
            return true;
        }

        return (sourcePixel & _readMask) == _readMask;
    }

    private uint SourceForMix(ushort mix, uint sourcePixel, int sourceX, int sourceY)
    {
        int sourceSelect = (mix >> 5) & 3;

        switch (sourceSelect)
        {
            case 0:
                return _background;

            case 1:
                return _foreground;

            case 3:
                return sourcePixel;

            default:
                return GetPixel(sourceX, sourceY);
        }
    }

    /// <summary>
    /// Draws a line, by Bresenham's algorithm -- which is what the hardware
    /// implements too, since it needs no division and no fractions.
    ///
    /// A driver sets up the error terms itself on a real 8514/A, handing the
    /// card the increments rather than the endpoints. Here the endpoints are
    /// recovered from those terms, which produces the same line and is far
    /// easier to read.
    /// </summary>
    private void DrawLine()
    {
        int x = _currentX, y = _currentY;
        int steps = _width;

        // Bit 7 chooses the major direction and bits 5-6 the octant. Rather
        // than reconstruct the whole encoding, the common cases -- horizontal
        // and vertical runs, which is what a window border is made of -- are
        // handled directly.
        bool horizontal = (_command & 0x0080) != 0;
        int step = (_command & 0x0020) != 0 ? 1 : -1;

        for (int i = 0; i <= steps; i++)
        {
            PutPixel(x, y, ForegroundSource(x, y), _mix);
            if (horizontal) x += step; else y += step;
        }
    }

    // --- pixels ---------------------------------------------------------------

    /// <summary>
    /// Whether video memory is currently a flat array of pixels, which is the
    /// only arrangement the engine can draw into. False in the VGA's planar
    /// modes -- including the 640x480 at 16 colours that Windows uses with
    /// its standard driver -- where a pixel's bits are spread across four
    /// planes and reached through the sequencer's masks.
    /// </summary>
    private bool HasLinearFramebuffer =>
        _registers.VesaMode is not null
        || _registers.EffectiveMode.Layout == VgaMemoryLayout.Linear8;

    private int BytesPerPixel =>
        _registers.VesaMode is { } mode ? mode.BytesPerPixel : 1;

    private int Stride
    {
        get
        {
            if (_registers.VesaScanLineBytes > 0) return _registers.VesaScanLineBytes;
            return _registers.VesaMode is { } mode ? mode.BytesPerRow : 1024;
        }
    }

    /// <summary>
    /// Writes one pixel, respecting the clip rectangle, the raster operation,
    /// the write mask and the colour compare.
    ///
    /// Clipping is done here rather than by trimming the rectangle first
    /// because that is what the hardware does: every pixel is tested. It
    /// costs nothing worth measuring and it means a line drawn diagonally out
    /// of the clip region stops at the boundary rather than at a corner.
    /// </summary>
    private void PutPixel(int x, int y, uint colour) => PutPixel(x, y, colour, _mix);

    private void PutPixel(int x, int y, uint colour, ushort mix)
    {
        if (x < _clipLeft || x > _clipRight) return;
        if (y < _clipTop || y > _clipBottom) return;
        if (x < 0 || y < 0) return;

        uint existing = GetPixel(x, y);

        // Colour compare: a pixel already holding the compare colour is
        // either the only one that may be written or the only one that may
        // not, depending on the condition. This is how a card draws a
        // transparent sprite in one pass -- and how a driver paints only the
        // background of a window without disturbing what is on it.
        if (ColourCompareEnabled)
        {
            uint compareMask = ColourCompareMask;
            bool matches = (existing & compareMask) == (_colourCompare & compareMask);
            if (matches != ColourCompareWritesOnMatch) return;
        }

        uint result = ApplyMix(colour, existing, mix);

        // The write mask -- "plane write mask" in the manual -- lets through
        // only the bits it has set. A driver that wants to change the top
        // four bits of every pixel and leave the rest alone does it here
        // rather than reading, modifying and writing back.
        uint writeMask = _writeMaskWritten ? _writeMask : uint.MaxValue;
        result = (result & writeMask) | (existing & ~writeMask);

        uint at = (uint)(y * Stride + x * BytesPerPixel);

        _memory.WriteLinear(at, (byte)result);
        if (BytesPerPixel >= 2) _memory.WriteLinear(at + 1, (byte)(result >> 8));
        if (BytesPerPixel == 4)
        {
            _memory.WriteLinear(at + 2, (byte)(result >> 16));
            _memory.WriteLinear(at + 3, (byte)(result >> 24));
        }
    }

    /// <summary>
    /// Whether the colour compare is in force, from bits 3-5 of the
    /// multifunction pixel control register.
    /// </summary>
    private bool ColourCompareEnabled => (_pixelControl & 0x0038) != 0;

    /// <summary>
    /// The colour compare registers remain 32 bits at every depth, but only
    /// the bits stored by the active pixel can take part in a comparison.
    /// High bits left by an earlier mode must not turn an 8- or 16-bpp match
    /// into a miss.
    /// </summary>
    private uint ColourCompareMask => BytesPerPixel switch
    {
        1 => byte.MaxValue,
        2 => ushort.MaxValue,
        4 => uint.MaxValue,
        _ => ushort.MaxValue,
    };

    /// <summary>
    /// Whether a matching pixel is the one that gets written or the one that
    /// is skipped. Both are used: skipping matches draws a sprite with a
    /// transparent colour, and writing only matches replaces one colour
    /// throughout an area.
    /// </summary>
    private bool ColourCompareWritesOnMatch => (_pixelControl & 0x0020) != 0;

    /// <summary>
    /// Combines the new pixel with the one already there, according to the
    /// low four bits of the mix register.
    ///
    /// These are the raster operations, and the two that matter are 7 -- plain
    /// replacement, what almost every draw uses -- and 5, exclusive or. XOR is
    /// how a rubber-band selection rectangle works: draw it once and it
    /// appears, draw it again in the same place and it vanishes, leaving what
    /// was underneath exactly as it was. No saving of the background, no
    /// second buffer.
    /// </summary>
    /// <summary>
    /// The sixteen mixes of the 8514/A, in its own numbering, which every
    /// S3 kept: 3 leaves the destination alone, 5 is the exclusive or of the
    /// rubber band, 7 the plain replacement. A Windows driver draws text
    /// with the background mix set to 3, so that only the glyph's own
    /// pixels touch the screen; a table in another order painted the
    /// background too, and every word came out as a black block.
    /// </summary>
    private static uint ApplyMix(uint source, uint destination, ushort mix) => (mix & 0x0F) switch
    {
        0x0 => ~destination,
        0x1 => 0,                                            // clear
        0x2 => uint.MaxValue,                                // set
        0x3 => destination,                                  // leave it alone
        0x4 => ~source,
        0x5 => destination ^ source,                         // the rubber band
        0x6 => ~(destination ^ source),
        0x7 => source,                                       // replace: the common case
        0x8 => ~source | ~destination,
        0x9 => source | ~destination,
        0xA => ~source | destination,
        0xB => source | destination,
        0xC => source & destination,
        0xD => source & ~destination,
        0xE => ~source & destination,
        _ => ~source & ~destination,
    };

    private uint GetPixel(int x, int y)
    {
        if (x < 0 || y < 0) return 0;

        uint at = (uint)(y * Stride + x * BytesPerPixel);
        uint value = _memory.ReadLinear(at);
        if (BytesPerPixel >= 2) value |= (uint)_memory.ReadLinear(at + 1) << 8;
        if (BytesPerPixel == 4)
        {
            value |= (uint)_memory.ReadLinear(at + 2) << 16;
            value |= (uint)_memory.ReadLinear(at + 3) << 24;
        }

        return value;
    }

    /// <summary>
    /// Opens the clip rectangle to the whole screen, which is where a driver
    /// leaves it when it is not clipping. A zero clip rectangle would draw
    /// exactly one pixel and look like the engine was broken.
    /// </summary>
    public void ResetClip(int width, int height)
    {
        _clipTop = 0;
        _clipLeft = 0;
        _clipBottom = (ushort)(height - 1);
        _clipRight = (ushort)(width - 1);
    }
}
