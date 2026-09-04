namespace Aurora386.Core.Bios;

/// <summary>
/// Builds the BIOS ROM image.
///
/// The ROM holds almost no real x86 code: for each interrupt there is a
/// three-byte entry point that invokes the native handler and returns. All the
/// actual work -- drawing characters, reading sectors, servicing the keyboard
/// -- is written in C#.
///
/// That is deliberate. A real BIOS image is not redistributable and would have
/// to be supplied by the user; more importantly it would run a full POST,
/// probing chipset registers we do not emulate. Implementing the services in
/// C# makes them far faster than instruction-by-instruction emulation and much
/// easier to fix: a bug shows up in an ordinary debugger instead of in a trace
/// of machine code.
/// </summary>
public static class BiosRom
{
    /// <summary>Physical address the ROM is mapped at (the top 64 KB of the first megabyte).</summary>
    public const uint Base = 0xF0000;
    public const int Size = 0x10000;

    /// <summary>Opcode prefix that traps into the native handlers.</summary>
    private const byte HookByte1 = 0x0F;
    private const byte HookByte2 = 0xFF;

    /// <summary>
    /// Offsets of the entry points inside the ROM. The values are arbitrary,
    /// but they must stay put: the interrupt vector table points at them.
    /// </summary>
    public static class Entry
    {
        public const ushort Video = 0x1000;          // INT 10h
        public const ushort EquipmentList = 0x1010;  // INT 11h
        public const ushort MemorySize = 0x1020;     // INT 12h
        public const ushort Disk = 0x1030;           // INT 13h
        public const ushort Serial = 0x1040;         // INT 14h
        public const ushort System = 0x1050;         // INT 15h
        public const ushort Keyboard = 0x1060;       // INT 16h
        public const ushort Printer = 0x1070;        // INT 17h
        public const ushort Bootstrap = 0x1080;      // INT 19h
        public const ushort TimeOfDay = 0x1090;      // INT 1Ah
        public const ushort TimerTick = 0x10A0;      // INT 08h, IRQ 0
        public const ushort KeyboardIrq = 0x10B0;    // INT 09h, IRQ 1
        public const ushort DummyIrq = 0x10C0;       // unhandled IRQs
        public const ushort Multiplex = 0x10D0;      // INT 2Fh

        /// <summary>
        /// The redirector's own INT 2Fh entry point, which chains.
        ///
        /// It has to exist separately from <see cref="Multiplex"/> because
        /// the two behave differently at the end. The plain multiplex handler
        /// answers and returns with IRET, which is right while nothing else
        /// is listening. But DOS installs a multiplex handler of its own
        /// during startup and takes the vector over, and from then on a
        /// redirector has to sit in front of DOS's handler and pass on
        /// everything it does not answer -- exactly as a TSR would, and for
        /// the same reason: INT 2Fh is a bus, not a service, and a handler
        /// that swallows what it does not recognise silences everyone behind
        /// it.
        ///
        /// So this entry runs the native handler and then jumps to whatever
        /// was in the vector before, through a pointer kept in RAM.
        /// </summary>
        public const ushort RedirectorMultiplex = 0x1130;

        /// <summary>
        /// Where the far jump at the end of the redirector's entry reads its
        /// target from: the INT 2Fh vector that was displaced.
        ///
        /// It sits in the ROM image, which the emulator writes to directly --
        /// the guest cannot, but the guest has no business changing it. Put
        /// it in RAM instead and the jump would need a DS it cannot safely
        /// load, since the handler runs with whatever segment registers the
        /// interrupted program happened to have.
        /// </summary>
        public const ushort PreviousMultiplexVector = 0x1140;
        public const ushort Mouse = 0x10F0;          // INT 33h
        public const ushort MouseIrq = 0x1110;       // INT 74h, IRQ 12

        /// <summary>
        /// Where an INT 33h event handler returns to.
        ///
        /// A mouse event handler is entered with a far CALL and leaves with
        /// RETF, so it needs a return address to pop. It cannot be given the
        /// address the guest was interrupted at directly -- that would have
        /// the RETF land mid-instruction -- so it returns here instead, to a
        /// lone RETF that unwinds onto the real one.
        /// </summary>
        public const ushort MouseCallbackReturn = 0x1120;

        /// <summary>
        /// The diskette parameter table, which INT 1Eh points at rather than
        /// calls: it is eleven bytes of data, not code.
        ///
        /// DOS reads it to find out how the drive is laid out -- most of all
        /// how many sectors there are per track, which is the difference
        /// between a 1.44 MB floppy and a 2.88 MB one. Leave the vector unset
        /// and DOS follows it to whatever happens to be in memory there, and
        /// reads the disk with the wrong geometry: on a 2.88 MB disk it finds
        /// half a directory, because it looks for the second half in the
        /// place a 1.44 MB disk would have kept it.
        /// </summary>
        public const ushort DiskParameters = 0x10E0;

        /// <summary>
        /// The CD-ROM device driver's header, and the two entry points behind
        /// it.
        ///
        /// MSCDEX does not talk to a CD. It talks to a device driver, through
        /// the same interface DOS uses for every driver it loads: a header
        /// naming the device, a "strategy" routine that receives a request,
        /// and an "interrupt" routine that carries it out. The driver that
        /// came with the drive spoke to the hardware; ours answers out of the
        /// image the emulator has mounted.
        ///
        /// This is worth doing rather than answering MSCDEX's own INT 2Fh
        /// calls directly, because it puts the emulator underneath the real
        /// MSCDEX instead of in place of it. MSCDEX then does the hard part
        /// -- the redirector, the drive letter, the ISO 9660 directories --
        /// exactly as it did on the machines this software was written for.
        /// </summary>
        public const ushort CompactDiscDriver = 0x1150;

        /// <summary>The driver's strategy routine: stores the request and returns.</summary>
        public const ushort CompactDiscStrategy = 0x1170;

        /// <summary>The driver's interrupt routine: acts on the stored request.</summary>
        public const ushort CompactDiscInterrupt = 0x1180;
        public const ushort XmsEntry = 0x10E0;       // XMS entry point
        public const ushort Reset = 0xFFF0;          // power-on entry point
        public const ushort PostEntry = 0x1100;      // initialization
    }

    /// <summary>
    /// Identifiers passed to the native handler in the byte following the hook
    /// opcode.
    /// </summary>
    public enum Handler : byte
    {
        Video = 0x10,
        EquipmentList = 0x11,
        MemorySize = 0x12,
        Disk = 0x13,
        Serial = 0x14,
        System = 0x15,
        Keyboard = 0x16,
        Printer = 0x17,
        Bootstrap = 0x19,
        TimeOfDay = 0x1A,
        TimerTick = 0x08,
        KeyboardIrq = 0x09,
        DummyIrq = 0xFE,
        Multiplex = 0x2F,

        /// <summary>
        /// The chaining multiplex handler. It answers if the call is the
        /// redirector's, and otherwise falls through to the JMP that follows.
        /// </summary>
        RedirectorMultiplex = 0x2E,

        /// <summary>The CD-ROM driver's two routines, told apart by this id.</summary>
        CompactDiscStrategy = 0xFC,
        CompactDiscInterrupt = 0xFD,

        Mouse = 0x33,
        MouseIrq = 0x74,
        Xms = 0x43,
        Post = 0xFF,
    }

    public static byte[] Build()
    {
        var rom = new byte[Size];

        // Fill with a breakpoint opcode: if execution ever wanders somewhere it
        // should not, it stops immediately instead of drifting through the ROM.
        Array.Fill(rom, (byte)0xCC);

        // Interrupt entry points: call the native handler, then return with
        // IRET, which also restores the flags.
        WriteInterruptEntry(rom, Entry.Video, Handler.Video);
        WriteInterruptEntry(rom, Entry.EquipmentList, Handler.EquipmentList);
        WriteInterruptEntry(rom, Entry.MemorySize, Handler.MemorySize);
        WriteInterruptEntry(rom, Entry.Disk, Handler.Disk);
        WriteInterruptEntry(rom, Entry.Serial, Handler.Serial);
        WriteInterruptEntry(rom, Entry.System, Handler.System);
        WriteInterruptEntry(rom, Entry.Keyboard, Handler.Keyboard);
        WriteInterruptEntry(rom, Entry.Printer, Handler.Printer);
        WriteInterruptEntry(rom, Entry.Bootstrap, Handler.Bootstrap);
        WriteInterruptEntry(rom, Entry.TimeOfDay, Handler.TimeOfDay);
        WriteInterruptEntry(rom, Entry.TimerTick, Handler.TimerTick);
        WriteInterruptEntry(rom, Entry.KeyboardIrq, Handler.KeyboardIrq);
        WriteInterruptEntry(rom, Entry.DummyIrq, Handler.DummyIrq);
        WriteInterruptEntry(rom, Entry.Multiplex, Handler.Multiplex);

        // The chaining redirector entry. It is only three bytes and an IRET,
        // because the chaining itself is done in C#: when the handler decides
        // the call is not its own it moves CS:IP straight to the handler DOS
        // installed, leaving the interrupt frame untouched so that handler's
        // own IRET returns to the caller. That is what the JMP FAR through a
        // saved vector at the end of every TSR did, and doing it here saves
        // the ROM from needing a data pointer it cannot hold.
        {
            int entry = Entry.RedirectorMultiplex;
            rom[entry + 0] = HookByte1;
            rom[entry + 1] = HookByte2;
            rom[entry + 2] = (byte)Handler.RedirectorMultiplex;
            rom[entry + 3] = 0xCF;                       // IRET, when we answered

            // ...and when we did not, the handler points execution here
            // instead, at a far jump through the vector we displaced.
            //
            // The jump reads its target from a pointer in the ROM's own
            // segment, which is what the CS: prefix is for: the handler that
            // runs here has whatever DS the interrupted program had, and
            // there is no safe moment to load one.
            //   2E FF 2E <disp16>   JMP FAR [CS:disp16]
            rom[entry + 4] = 0x2E;                       // CS segment override
            rom[entry + 5] = 0xFF;                       // group 5
            rom[entry + 6] = 0x2E;                       // /5, disp16 addressing
            rom[entry + 7] = unchecked((byte)Entry.PreviousMultiplexVector);
            rom[entry + 8] = unchecked((byte)(Entry.PreviousMultiplexVector >> 8));
        }
        WriteCompactDiscDriver(rom);
        WriteInterruptEntry(rom, Entry.Mouse, Handler.Mouse);
        WriteInterruptEntry(rom, Entry.MouseIrq, Handler.MouseIrq);

        // The landing pad a mouse event handler returns through. It is a bare
        // RETF: the far CALL that entered the handler pushed this address, so
        // returning here pops the address the guest was really at and carries
        // on as though nothing had happened.
        rom[Entry.MouseCallbackReturn] = 0xCB;   // RETF

        // The XMS entry point is not an interrupt handler: programs reach it
        // through a far CALL, so it has to return with RETF rather than IRET.
        {
            int xms = Entry.XmsEntry;
            rom[xms + 0] = HookByte1;
            rom[xms + 1] = HookByte2;
            rom[xms + 2] = (byte)Handler.Xms;
            rom[xms + 3] = 0xCB;      // RETF
        }

        // Startup sequence: run the POST handler, then jump to the operating
        // system loader.
        int offset = Entry.PostEntry;
        rom[offset++] = HookByte1;
        rom[offset++] = HookByte2;
        rom[offset++] = (byte)Handler.Post;
        rom[offset++] = 0xCD;          // INT 19h: load the boot sector
        rom[offset++] = 0x19;
        rom[offset++] = 0xF4;          // HLT, since if INT 19h returns there is nothing left to do

        // Reset vector at F000:FFF0. The processor starts fetching here, and
        // the jump lands in the startup sequence.
        offset = Entry.Reset;
        rom[offset++] = 0xEA;                                     // JMP far
        rom[offset++] = unchecked((byte)Entry.PostEntry);          // offset, low byte
        rom[offset++] = unchecked((byte)(Entry.PostEntry >> 8));   // offset, high byte
        rom[offset++] = 0x00;                          // segment F000
        rom[offset++] = 0xF0;

        // Release date at F000:FFF5, in the format some programs read to
        // identify the BIOS.
        "01/01/93"u8.CopyTo(rom.AsSpan(0xFFF5));

        // Model identification byte: 0xFC = AT and compatibles.
        rom[0xFFFE] = 0xFC;

        return rom;
    }

    /// <summary>
    /// Lays out the CD-ROM device driver: an eighteen-byte header followed by
    /// the two routines it names.
    ///
    /// The header is the shape DOS has expected of a driver since version 2,
    /// and MSCDEX checks it before believing there is a drive there:
    ///
    ///   +0  pointer to the next driver in the chain (FFFF:FFFF = last)
    ///   +4  attribute word
    ///   +6  offset of the strategy routine
    ///   +8  offset of the interrupt routine
    ///   +10 eight bytes of name
    ///
    /// For a block driver those eight bytes are not a name at all: the first
    /// says how many units the driver serves, and the rest is signature. A
    /// CD-ROM driver puts 1 there and then "MSCD001", which is the name
    /// MSCDEX looks for on the command line -- /D:MSCD001 in AUTOEXEC.BAT is
    /// asking for exactly this driver.
    /// </summary>
    private static void WriteCompactDiscDriver(byte[] rom)
    {
        int header = Entry.CompactDiscDriver;

        // No driver after this one.
        rom[header + 0] = 0xFF; rom[header + 1] = 0xFF;
        rom[header + 2] = 0xFF; rom[header + 3] = 0xFF;

        // Attributes. Bit 15 marks a character device, which is what a
        // CD-ROM driver is as far as DOS is concerned -- the blocks are
        // MSCDEX's business, not DOS's. Bit 14 says the driver understands
        // the IOCTL calls, and bit 11 that it can tell when the disc has been
        // changed, which is how MSCDEX knows to re-read a directory.
        const ushort Attributes = 0xC800;
        rom[header + 4] = unchecked((byte)Attributes);
        rom[header + 5] = (byte)(Attributes >> 8);

        rom[header + 6] = unchecked((byte)Entry.CompactDiscStrategy);
        rom[header + 7] = (byte)(Entry.CompactDiscStrategy >> 8);
        rom[header + 8] = unchecked((byte)Entry.CompactDiscInterrupt);
        rom[header + 9] = (byte)(Entry.CompactDiscInterrupt >> 8);

        // One unit, then the signature MSCDEX is told to look for.
        rom[header + 10] = 1;
        "MSCD001"u8.CopyTo(rom.AsSpan(header + 11, 7));

        WriteInterruptEntry(rom, Entry.CompactDiscStrategy, Handler.CompactDiscStrategy);
        WriteInterruptEntry(rom, Entry.CompactDiscInterrupt, Handler.CompactDiscInterrupt);
    }

    private static void WriteInterruptEntry(byte[] rom, ushort offset, Handler handler)
    {
        rom[offset + 0] = HookByte1;
        rom[offset + 1] = HookByte2;
        rom[offset + 2] = (byte)handler;
        rom[offset + 3] = 0xCF;        // IRET
    }
}
