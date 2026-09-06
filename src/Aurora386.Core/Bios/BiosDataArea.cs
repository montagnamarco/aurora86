using Aurora386.Core.Memory;

namespace Aurora386.Core.Bios;

/// <summary>
/// BIOS Data Area: the block at 0x400 where the BIOS publishes the state of
/// the machine. DOS and application software read it directly, so every field
/// has to sit at exactly the historical offset -- these addresses are as much
/// a part of the interface as the INT calls themselves.
/// </summary>
public sealed class BiosDataArea
{
    public const uint Base = 0x400;

    private readonly PhysicalMemoryBus _memory;

    public BiosDataArea(PhysicalMemoryBus memory) => _memory = memory;

    private ushort ReadWord(uint offset) => _memory.ReadWord(Base + offset);
    private void WriteWord(uint offset, ushort value) => _memory.WriteWord(Base + offset, value);
    private byte ReadByte(uint offset) => _memory.ReadByte(Base + offset);
    private void WriteByte(uint offset, byte value) => _memory.WriteByte(Base + offset, value);

    /// <summary>Base I/O port of the first serial port (0x400).</summary>
    public ushort Com1Port { get => ReadWord(0x00); set => WriteWord(0x00, value); }

    /// <summary>
    /// Equipment word (0x410): how many floppy drives, which video adapter,
    /// how much memory. It is the first thing the POST writes.
    /// </summary>
    public ushort EquipmentWord { get => ReadWord(0x10); set => WriteWord(0x10, value); }

    /// <summary>Conventional memory in KB (0x413), typically 640.</summary>
    public ushort MemorySizeKb { get => ReadWord(0x13); set => WriteWord(0x13, value); }

    /// <summary>
    /// Segment of the Extended BIOS Data Area (0x40E), the kilobyte at the
    /// top of conventional memory that the PS/2 BIOS carved out for itself.
    /// The pointing-device support keeps its state there, and Microsoft's
    /// mouse drivers take its presence as the sign that INT 15h C2xx exists.
    /// </summary>
    public ushort ExtendedBiosDataSegment { get => ReadWord(0x0E); set => WriteWord(0x0E, value); }

    /// <summary>
    /// Keyboard modifier state (0x417): shift, ctrl, alt, caps lock. Programs
    /// read this byte directly when they need to know whether shift is held.
    /// </summary>
    public byte KeyboardFlags { get => ReadByte(0x17); set => WriteByte(0x17, value); }
    public byte KeyboardFlags2 { get => ReadByte(0x18); set => WriteByte(0x18, value); }

    /// <summary>
    /// The third keyboard status byte, at 0x496.
    ///
    /// Bit 4 is the one that matters: it declares that a 101-key keyboard is
    /// installed, and software reads it to decide whether the extended INT
    /// 16h functions are worth calling. Without it a program assumes the
    /// original 84-key keyboard, and then never asks about F11, F12 or the
    /// separate cursor keys -- it cannot, because on that keyboard they do
    /// not exist.
    /// </summary>
    public byte KeyboardFlags3 { get => ReadByte(0x96); set => WriteByte(0x96, value); }

    /// <summary>Bit 4 of the third status byte: a 101-key keyboard is present.</summary>
    public const byte EnhancedKeyboardInstalled = 0x10;

    /// <summary>
    /// Whether anything has ever taken a keystroke out of the buffer.
    ///
    /// The head pointer only moves when software reads a key, so a head that
    /// has left its starting position is proof that somebody asked. It is a
    /// cheap way to tell that the guest has reached a prompt: during startup
    /// DOS writes and loads but never waits for the user.
    /// </summary>
    public bool HasReadKeyboard => KeyboardBufferHead != (ushort)KeyboardBufferStart;

    /// <summary>
    /// Whether the guest is sitting at a prompt waiting for someone to type.
    ///
    /// The BIOS's keyboard buffer is empty and its pointers have settled: the
    /// head has caught up with the tail, which happens when software has
    /// taken every keystroke there was. During startup DOS never waits -- it
    /// loads, writes and moves on -- so a buffer that has been drained and
    /// left alone means the machine has finished starting and is asking the
    /// user what to do next.
    ///
    /// This is what tells an emulator when it is safe to call into DOS. Doing
    /// it earlier reaches a system still relocating itself, which treats the
    /// confusion as a critical error and stops with nothing to explain it.
    /// </summary>
    public bool WaitingAtPrompt => KeyboardPollCount >= PollsThatMeanIdle;

    /// <summary>
    /// How many polls in a row mean the guest is genuinely idle rather than
    /// merely passing through.
    ///
    /// A few polls happen during startup -- DOS checks whether a key is down
    /// to see if the user wants to skip the configuration files. A prompt
    /// polls thousands of times a second, so a generous threshold tells the
    /// two apart without needing to know anything else about the guest.
    /// </summary>
    private const int PollsThatMeanIdle = 5000;

    /// <summary>
    /// How many times software has asked the BIOS for a keystroke.
    ///
    /// Nothing in the architecture counts this -- it is the emulator's own
    /// tally, kept because it answers a question the guest cannot be asked
    /// directly: has the system finished starting?
    ///
    /// During startup DOS never waits for the user. It loads drivers, reads
    /// its configuration files and relocates itself, and none of that
    /// involves the keyboard. The first request for a key comes from the
    /// command interpreter, once everything else is in place and it is ready
    /// to be told what to do. So the first wait is the signal, and it arrives
    /// whether or not anybody actually types: the guest asks, finds the
    /// buffer empty, and asks again.
    /// </summary>
    public int KeyboardPollCount { get; set; }

    /// <summary>
    /// Keyboard circular buffer (0x41E-0x43D): 16 two-byte entries, each an
    /// ASCII code paired with its scancode. Head and tail live at 0x41A and
    /// 0x41C.
    ///
    /// Both pointers hold an <b>offset within segment 0x40</b>, not an absolute
    /// linear address. Programs that walk the buffer themselves -- and plenty
    /// of DOS-era software does, rather than going through INT 16h -- form the
    /// address as 0040:[041A]. Store an absolute address here and those
    /// programs end up reading 0x400 bytes past the real buffer, so keystrokes
    /// never reach them. This is the PC convention, not a design choice.
    /// </summary>
    public ushort KeyboardBufferHead { get => ReadWord(0x1A); set => WriteWord(0x1A, value); }
    public ushort KeyboardBufferTail { get => ReadWord(0x1C); set => WriteWord(0x1C, value); }

    /// <summary>
    /// Status of the last floppy operation (0x441). INT 13h function 0x01 does
    /// nothing but hand this byte back, and some programs skip the call and
    /// read it straight out of the BDA.
    /// </summary>
    public byte DiskStatus { get => ReadByte(0x41); set => WriteByte(0x41, value); }

    /// <summary>Status of the last hard disk operation (0x474).</summary>
    public byte HardDiskStatus { get => ReadByte(0x74); set => WriteByte(0x74, value); }

    /// <summary>Number of hard disks installed (0x475).</summary>
    public byte HardDiskCount { get => ReadByte(0x75); set => WriteByte(0x75, value); }

    public const uint KeyboardBufferStart = 0x1E;
    public const uint KeyboardBufferEnd = 0x3E;

    /// <summary>
    /// Where the keyboard buffer begins and ends, at 0x480 and 0x482.
    ///
    /// The buffer's location is not fixed by the architecture: these two words
    /// declare it, and software is entitled to point them somewhere larger.
    /// The AT BIOS introduced them precisely so that a program could enlarge
    /// the sixteen-keystroke buffer.
    ///
    /// They must be filled in even when the buffer stays where it has always
    /// been, because software reads them rather than assuming: DOS walks the
    /// buffer itself in places, and a pair of zeroes here sends it to the very
    /// bottom of the BIOS data area, where it corrupts whatever it finds and
    /// then loops on the garbage it wrote.
    /// </summary>
    public ushort KeyboardBufferStartPointer
    {
        get => ReadWord(0x80);
        set => WriteWord(0x80, value);
    }

    public ushort KeyboardBufferEndPointer
    {
        get => ReadWord(0x82);
        set => WriteWord(0x82, value);
    }

    /// <summary>Current video mode (0x449).</summary>
    public byte VideoMode { get => ReadByte(0x49); set => WriteByte(0x49, value); }

    /// <summary>Screen width in columns (0x44A).</summary>
    public ushort VideoColumns { get => ReadWord(0x4A); set => WriteWord(0x4A, value); }

    /// <summary>Size of a video page in bytes (0x44C).</summary>
    public ushort VideoPageSize { get => ReadWord(0x4C); set => WriteWord(0x4C, value); }

    /// <summary>Offset of the active video page (0x44E).</summary>
    public ushort VideoPageOffset { get => ReadWord(0x4E); set => WriteWord(0x4E, value); }

    /// <summary>Cursor position for page 0 (0x450): column then row.</summary>
    public byte CursorColumn { get => ReadByte(0x50); set => WriteByte(0x50, value); }
    public byte CursorRow { get => ReadByte(0x51); set => WriteByte(0x51, value); }

    /// <summary>Cursor shape (0x460): end scanline in the low byte, start in the high byte.</summary>
    public ushort CursorShape { get => ReadWord(0x60); set => WriteWord(0x60, value); }

    /// <summary>Active video page (0x462).</summary>
    public byte ActivePage { get => ReadByte(0x62); set => WriteByte(0x62, value); }

    /// <summary>CRTC base port (0x463): 0x3D4 for color adapters, 0x3B4 for monochrome.</summary>
    public ushort CrtcPort { get => ReadWord(0x63); set => WriteWord(0x63, value); }

    /// <summary>
    /// Tick counter since power-on (0x46C), bumped 18.2 times a second by the
    /// IRQ 0 handler. DOS derives the time of day from it.
    /// </summary>
    public uint TimerTicks
    {
        get => _memory.ReadDword(Base + 0x6C);
        set => _memory.WriteDword(Base + 0x6C, value);
    }

    /// <summary>Midnight flag (0x470): the tick counter has rolled past 24 hours.</summary>
    public byte MidnightFlag { get => ReadByte(0x70); set => WriteByte(0x70, value); }

    /// <summary>Screen height in rows minus one (0x484): 24 for 80x25 text.</summary>
    public byte VideoRowsMinusOne { get => ReadByte(0x84); set => WriteByte(0x84, value); }

    /// <summary>Character cell height in pixels (0x485).</summary>
    public ushort CharacterHeight { get => ReadWord(0x85); set => WriteWord(0x85, value); }

    /// <summary>
    /// Brings the BDA up the way a POST would on a machine with a color VGA
    /// adapter, one floppy drive and the given amount of memory.
    /// </summary>
    public void Initialize(ushort conventionalMemoryKb, int hardDisks)
    {
        // COM1's I/O address. A serial mouse driver does not probe the
        // hardware to find the port -- it reads this word, which is where the
        // BIOS publishes what it found during POST. Leave it zero, as it was
        // when no serial port was fitted, and the driver concludes there is no
        // COM1 at all and never touches 0x3F8.
        Com1Port = Devices.SerialMouse.BasePort;

        // Bit 0: a floppy drive is present. Bits 4-5: 10 = 80-column color
        // video. Bits 6-7: number of floppy drives minus one. Bits 9-11 count
        // the serial ports, and there is now one -- the mouse's.
        EquipmentWord = 0b0000_0010_0010_0001;

        MemorySizeKb = conventionalMemoryKb;

        KeyboardFlags = 0;
        KeyboardFlags2 = 0;

        // Declare the 101-key keyboard. Everything since the PC/AT has one,
        // and software that is not told assumes the older 84-key layout and
        // stops asking about the keys that arrived with it.
        KeyboardFlags3 = EnhancedKeyboardInstalled;
        // Head and tail are offsets within segment 0x40, not absolute
        // addresses: software that reads the buffer on its own computes
        // 0040:[041A], so an absolute value would send it 0x400 bytes too far.
        // This is the PC convention, not something we get to pick.
        KeyboardBufferHead = (ushort)KeyboardBufferStart;
        KeyboardBufferTail = (ushort)KeyboardBufferStart;
        KeyboardBufferStartPointer = (ushort)KeyboardBufferStart;
        KeyboardBufferEndPointer = (ushort)KeyboardBufferEnd;

        VideoMode = 0x03;              // 80x25 color text
        VideoColumns = 80;
        VideoPageSize = 4000;          // 80 * 25 * 2 bytes
        VideoPageOffset = 0;
        CursorColumn = 0;
        CursorRow = 0;
        CursorShape = 0x0607;          // underline cursor
        ActivePage = 0;
        CrtcPort = 0x3D4;
        VideoRowsMinusOne = 24;
        CharacterHeight = 16;

        TimerTicks = 0;
        MidnightFlag = 0;
        HardDiskCount = (byte)hardDisks;
    }
}
