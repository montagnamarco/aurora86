namespace Aurora386.Core.Firmware;

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
public static partial class BiosRom
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
        public const ushort DiskPioRead = 0x1C00;    // IDE transfer executed by the guest
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
        public const ushort Coprocessor = 0x1190;    // INT 07h, the software FPU

        /// <summary>
        /// The routine that programs the adapter's registers for a mode with
        /// OUT instructions executed by the processor, as a VGA ROM does. The
        /// mode set itself is handled natively, but a monitor that traps the
        /// adapter's ports -- Windows' virtual display device keeps a shadow
        /// of the registers that way and never reads the hardware back --
        /// only learns of the change from the port writes.
        /// </summary>
        public const ushort VgaProgram = 0x1200;

        /// <summary>The register tables the routine reads: mode byte, then
        /// the 61 register values; a mode byte of FF ends the table and is
        /// also the default set.</summary>
        public const ushort VgaTables = 0x1300;

        /// <summary>
        /// The VESA mode set's share of the port writes: for INT 10h AX=4F02h
        /// the standard VGA registers are programmed through the ports with
        /// the table of the nearest VGA mode -- 13h for a packed-pixel mode,
        /// 12h for a planar one, the mode itself for a return to VGA --
        /// before the native handler sets the VBE mode proper. Without it a
        /// monitor's shadow of the graphics controller still said what the
        /// last VGA mode set left there. Windows' S3 display driver sets
        /// BIOS mode 6 and then VBE mode 103h, and its virtual display
        /// device, believing from mode 6 that the adapter decoded B8000h,
        /// gave the driver's memory-mapped engine registers at A8000h a page
        /// of nothing.
        /// </summary>
        public const ushort VesaProgram = 0x16E0;
        public const ushort VesaModeProgram = 0x1D00;
        public const ushort VesaRegisterTable = 0x1E00;

        /// <summary>Where the INT 10h entry jumps: the mode-set and palette
        /// routines are chosen here before the native handler runs.</summary>
        public const ushort VideoDispatch = 0x1700;

        /// <summary>The palette functions of INT 10h AH=10h that write the
        /// adapter, done with OUT instructions for the same reason as the
        /// mode set.</summary>
        public const ushort VgaPalette = 0x1720;

        /// <summary>
        /// The system configuration table INT 15h AH=C0 hands back, which says
        /// what kind of machine this is and which BIOS features it has.
        /// </summary>
        public const ushort SystemConfiguration = 0x11A0;

        /// <summary>
        /// Where every vector with no handler of its own points: a bare IRET.
        ///
        /// It is not the same thing as <see cref="DummyIrq"/>, and the
        /// difference matters. The dummy IRQ entry acknowledges the interrupt
        /// controllers before returning, which is right for a hardware line
        /// nobody claimed and wrong for everything else: a software INT that
        /// sends two end-of-interrupt commands clears whichever line really
        /// was in service, and a device that had raised one never gets
        /// another look. It also makes an unclaimed vector answer like a
        /// working handler. INT 67h is the case that showed it up: EMM386
        /// calls it to find out whether an EMS manager is already installed,
        /// our filler answered as if one were, and EMM386 gave up with "the
        /// EMM386 driver was not installed" -- so there were no upper memory
        /// blocks, and with none the DOS box under Windows had nowhere to
        /// live.
        /// </summary>
        public const ushort UnusedVector = 0x11B0;

        /// <summary>
        /// The card's name, as a far pointer in the VBE information block.
        ///
        /// It has to live in ROM because the structure hands back a pointer
        /// rather than the text: on a real card that pointer goes into its
        /// own ROM, and a program is entitled to keep it and read the string
        /// whenever it likes.
        /// </summary>
        public const ushort VesaOemName = 0x1800;

        /// <summary>
        /// The list of supported mode numbers, terminated by 0xFFFF. Also
        /// pointed at rather than copied, for the same reason.
        /// </summary>
        public const ushort VesaModeList = 0x1840;

        /// <summary>
        /// The Plug and Play BIOS installation check structure. It has to
        /// sit on a 16-byte boundary somewhere in the F000h segment, because
        /// that is where an operating system scans for "$PnP".
        /// </summary>
        public const ushort PnpHeader = 0x1A00;

        /// <summary>
        /// The Plug and Play entry point, reached by a far call rather than
        /// an interrupt, with its arguments on the stack. The same code
        /// serves real mode and 16-bit protected mode: the header gives
        /// both entries as this one offset.
        /// </summary>
        public const ushort PlugAndPlay = 0x1A40;

        /// <summary>
        /// The APM protected-mode entry point, for the 16-bit and 32-bit
        /// interfaces alike: a far call with the function in AX, answered by
        /// the same handler as INT 15h AH=53h and returned with RETF, which
        /// pops whatever size of return address the caller's segment pushed.
        /// </summary>
        public const ushort AdvancedPowerManagement = 0x1A50;
        public const ushort Bios32Header = 0x1A60;
        public const ushort Bios32Directory = 0x1A70;
        public const ushort PciProtected = 0x1A80;
        public const ushort PciRoutingTable = 0x1AA0;

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
        /// Where the INT 15h C207h pointing-device handler returns to. The
        /// BIOS calls it with four words on the stack -- status, X, Y, Z --
        /// so the return path has to discard them: ADD SP,8 then RETF onto
        /// the interrupt handler that made the call.
        /// </summary>
        public const ushort PointingCallbackReturn = 0x1124;

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
        DiskReadPrepare = 0x56,
        DiskReadComplete = 0x57,
        Serial = 0x14,
        System = 0x15,
        Keyboard = 0x16,
        Printer = 0x17,
        Bootstrap = 0x19,
        TimeOfDay = 0x1A,
        TimerTick = 0x08,
        KeyboardIrq = 0x09,
        DummyIrq = 0xFE,

        /// <summary>
        /// The software coprocessor on vector 7. It is not a BIOS service in
        /// the usual sense -- nothing calls it deliberately -- but it is
        /// reached the same way, through a fault rather than an INT.
        /// </summary>
        Coprocessor = 0x07,

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

        /// <summary>The Plug and Play BIOS, called far rather than by INT.</summary>
        PlugAndPlay = 0x50,

        /// <summary>APM through its protected-mode entry, called far with the function in AX.</summary>
        AdvancedPowerManagement = 0x53,
        Bios32Directory = 0x54,
        PciProtected = 0x55,
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
        WriteVideoEntry(rom);
        WriteInterruptEntry(rom, Entry.EquipmentList, Handler.EquipmentList);
        WriteInterruptEntry(rom, Entry.MemorySize, Handler.MemorySize);
        WriteInterruptEntry(rom, Entry.Disk, Handler.Disk);
        WriteDiskPioRead(rom);
        WriteInterruptEntry(rom, Entry.Serial, Handler.Serial);
        WriteInterruptEntry(rom, Entry.System, Handler.System);
        WriteInterruptEntry(rom, Entry.Keyboard, Handler.Keyboard);
        WriteInterruptEntry(rom, Entry.Printer, Handler.Printer);
        WriteInterruptEntry(rom, Entry.Bootstrap, Handler.Bootstrap);
        WriteInterruptEntry(rom, Entry.TimeOfDay, Handler.TimeOfDay);
        WriteIrqEntry(rom, Entry.TimerTick, Handler.TimerTick, slave: false, userTick: true, readKeyboard: false);
        WriteIrqEntry(rom, Entry.KeyboardIrq, Handler.KeyboardIrq, slave: false, userTick: false, readKeyboard: false);
        WriteIrqEntry(rom, Entry.DummyIrq, Handler.DummyIrq, slave: true, userTick: false, readKeyboard: false);
        rom[Entry.UnusedVector] = 0xCF;      // IRET, and nothing else
        WriteInterruptEntry(rom, Entry.Multiplex, Handler.Multiplex);
        WriteInterruptEntry(rom, Entry.Coprocessor, Handler.Coprocessor);

        // The system configuration table. Its shape is the one IBM defined for
        // the AT and every clone BIOS has copied since: a length, a model, and
        // a byte of feature bits that software reads to decide what the
        // machine can do. Programs do not all check the carry flag after
        // asking for it -- several test ES:BX for zero instead -- so a BIOS
        // that refuses the call has to leave those registers cleared, and one
        // that answers has to answer properly. Answering is the honest option
        // here, since everything the table claims is true of this machine.
        {
            int table = Entry.SystemConfiguration;
            rom[table + 0] = 0x08;   // length, low byte: eight bytes follow
            rom[table + 1] = 0x00;
            rom[table + 2] = 0xFC;   // model FC: the PC/AT and its descendants
            rom[table + 3] = 0x01;   // submodel
            rom[table + 4] = 0x00;   // BIOS revision

            // Feature byte 1. Bit 6 says interrupts are cascaded through a
            // second controller, bit 5 that there is a real-time clock, bit 4
            // that INT 15h AH=4F filters keyboard scan codes, and bit 2 that
            // an Extended BIOS Data Area is allocated -- which is also how
            // Microsoft's mouse drivers decide the PS/2 mouse services of
            // INT 15h C2xx are there. All true here.
            rom[table + 5] = 0x74;

            rom[table + 6] = 0x00;
            rom[table + 7] = 0x00;
            rom[table + 8] = 0x00;
            rom[table + 9] = 0x00;
        }

        // The Plug and Play BIOS: an installation check structure and an
        // entry point that returns with RETF, since it is called far with
        // its arguments on the stack in the C convention. Windows 95 looks
        // for this before it looks at anything else on the board.
        {
            int header = Entry.PnpHeader;
            // Cleared first: the fields left unset (event flag, OEM id) mean
            // "none" as zeros, and the checksum slot must not count itself.
            Array.Clear(rom, header, 0x21);
            rom[header + 0] = (byte)'$';
            rom[header + 1] = (byte)'P';
            rom[header + 2] = (byte)'n';
            rom[header + 3] = (byte)'P';
            rom[header + 4] = 0x10;                  // version 1.0
            rom[header + 5] = 0x21;                  // length of this structure
            rom[header + 6] = 0x00;                  // control: no event notification
            rom[header + 7] = 0x00;
            // +8 is the checksum, filled in last.
            // +9: event notification flag address, none.
            WriteWord(rom, header + 0x0D, Entry.PlugAndPlay);   // real-mode entry offset
            WriteWord(rom, header + 0x0F, 0xF000);              // real-mode entry segment
            WriteWord(rom, header + 0x11, Entry.PlugAndPlay);   // protected-mode entry offset
            WriteDword(rom, header + 0x13, Base);               // protected-mode code segment base
            // +17: OEM device id, none.
            WriteWord(rom, header + 0x1B, 0xF000);              // real-mode data segment
            WriteDword(rom, header + 0x1D, Base);               // protected-mode data segment base

            int sum = 0;
            for (int i = 0; i < 0x21; i++) sum += rom[header + i];
            rom[header + 8] = (byte)(-sum);

            rom[Entry.PlugAndPlay + 0] = HookByte1;
            rom[Entry.PlugAndPlay + 1] = HookByte2;
            rom[Entry.PlugAndPlay + 2] = (byte)Handler.PlugAndPlay;
            rom[Entry.PlugAndPlay + 3] = 0xCB;       // RETF

            rom[Entry.AdvancedPowerManagement + 0] = HookByte1;
            rom[Entry.AdvancedPowerManagement + 1] = HookByte2;
            rom[Entry.AdvancedPowerManagement + 2] = (byte)Handler.AdvancedPowerManagement;
            rom[Entry.AdvancedPowerManagement + 3] = 0xCB;

            Aurora386.Core.Devices.Pci.PciInterruptRouter.BuildRoutingTable().CopyTo(rom, Entry.PciRoutingTable);
            int b32 = Entry.Bios32Header;
            Array.Clear(rom, b32, 16);
            WriteDword(rom, b32, 0x5F32335F); // _32_
            WriteDword(rom, b32 + 4, Base + Entry.Bios32Directory);
            rom[b32 + 9] = 1; // one paragraph, revision zero
            int checksum32 = 0;
            for (int i = 0; i < 16; i++) checksum32 += rom[b32 + i];
            rom[b32 + 10] = unchecked((byte)-checksum32);
            rom[Entry.Bios32Directory] = rom[Entry.PciProtected] = HookByte1;
            rom[Entry.Bios32Directory + 1] = rom[Entry.PciProtected + 1] = HookByte2;
            rom[Entry.Bios32Directory + 2] = (byte)Handler.Bios32Directory;
            rom[Entry.PciProtected + 2] = (byte)Handler.PciProtected;
            rom[Entry.Bios32Directory + 3] = rom[Entry.PciProtected + 3] = 0xCB; // RETF
        }

        // The two VBE tables the information block points at rather than
        // copies. They have to be in ROM because a program is entitled to
        // keep the pointer and read them later, long after the call that
        // handed it over has returned.
        {
            const string name = "Aurora386 SVGA";
            for (int i = 0; i < name.Length; i++)
                rom[Entry.VesaOemName + i] = (byte)name[i];
            rom[Entry.VesaOemName + name.Length] = 0;

            int at = Entry.VesaModeList;
            foreach (Devices.Video.VesaModes.VesaModeInfo mode
                     in Devices.Video.VesaModes.Available(Devices.Video.VgaMemory.TotalSize))
            {
                rom[at++] = (byte)mode.Number;
                rom[at++] = (byte)(mode.Number >> 8);
            }

            // 0xFFFF ends the list. A program walks it until it sees this,
            // so leaving it out sends the program off through whatever
            // follows in ROM.
            rom[at++] = 0xFF;
            rom[at] = 0xFF;
        }

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
        WriteMouseIrqEntry(rom, Entry.MouseIrq, Handler.MouseIrq);

        // The landing pad a mouse event handler returns through. It is a bare
        // RETF: the far CALL that entered the handler pushed this address, so
        // returning here pops the address the guest was really at and carries
        // on as though nothing had happened.
        rom[Entry.MouseCallbackReturn] = 0xCB;   // RETF
        rom[Entry.PointingCallbackReturn + 0] = 0x83;   // ADD SP,8
        rom[Entry.PointingCallbackReturn + 1] = 0xC4;
        rom[Entry.PointingCallbackReturn + 2] = 0x08;
        rom[Entry.PointingCallbackReturn + 3] = 0xCB;   // RETF

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

    /// <summary>
    /// INT 10h. A mode set (AH = 0) first runs the ROM routine that writes
    /// the adapter's registers through the ports, then the native handler
    /// does the rest -- BIOS data area, screen clear, palette, cursor -- as
    /// for every other function.
    /// </summary>
    private static void WriteVideoEntry(byte[] rom)
    {
        // The entry itself is a jump: the sixteen bytes of the slot are not
        // enough for the dispatch.
        int o = Entry.Video;
        int rel = Entry.VideoDispatch - (o + 3);
        rom[o++] = 0xE9; rom[o++] = (byte)rel; rom[o++] = (byte)(rel >> 8);   // JMP VideoDispatch

        var a = new Asm(rom, Entry.VideoDispatch);
        a.B(0x0A, 0xE4);                    // OR AH,AH
        a.Jcc(0x75, "not_mode");            // JNZ
        a.CallNear(Entry.VgaProgram);
        a.Jmp("hook");
        a.Label("not_mode");
        a.B(0x80, 0xFC, 0x10);              // CMP AH,10h
        a.Jcc(0x75, "not_palette");         // JNE
        a.CallNear(Entry.VgaPalette);
        a.Jmp("hook");
        a.Label("not_palette");
        a.CallNear(Entry.VesaProgram);      // does nothing unless AX = 4F02h
        a.Label("hook");
        a.B(HookByte1, HookByte2, (byte)Handler.Video);
        a.B(0xCF);                          // IRET
        a.Resolve();
        if (a.Position > Entry.VgaPalette) throw new InvalidOperationException("video dispatch overruns the palette routine");

        WriteVgaProgram(rom);
        WriteVesaProgram(rom);
        WriteVgaTables(rom);
        WriteVgaPalette(rom);
    }

    /// <summary>
    /// INT 10h AH=10h, the functions that write the palette: 00h one
    /// attribute register, 02h all sixteen and the overscan, 03h the blink
    /// bit, 10h one DAC entry, 12h a block of them, 13h the colour select.
    /// The read functions stay native. AL holds the subfunction on entry;
    /// every register is preserved. Programs that could not care less about
    /// the ports -- WIN.COM's logo, Windows' display driver -- set their
    /// colours this way, and the monitor that shadows the adapter for a
    /// virtual machine must see them go by.
    /// </summary>
    private static void WriteVgaPalette(byte[] rom)
    {
        var a = new Asm(rom, Entry.VgaPalette);
        a.B(0x50, 0x53, 0x51, 0x52, 0x56, 0x1E);          // PUSH AX,BX,CX,DX,SI,DS
        foreach ((byte sub, string target) in new (byte, string)[] { (0x00, "set_one"), (0x02, "set_all"), (0x03, "blink"), (0x10, "dac_one"), (0x12, "dac_block"), (0x13, "colsel") })
        {
            a.B(0x3C, sub);                               // CMP AL,sub
            a.Jcc(0x75, "skip_" + target);                // JNE over the jump
            a.JmpNear(target);
            a.Label("skip_" + target);
        }
        a.JmpNear("done");

        a.Label("set_one");                               // BL = register, BH = value
        a.B(0xBA, 0xDA, 0x03, 0xEC);                      // MOV DX,3DAh / IN AL,DX  (flip-flop to index)
        a.B(0xBA, 0xC0, 0x03);                            // MOV DX,3C0h
        a.B(0x88, 0xD8, 0xEE);                            // MOV AL,BL / OUT DX,AL
        a.B(0x88, 0xF8, 0xEE);                            // MOV AL,BH / OUT DX,AL
        a.B(0xB0, 0x20, 0xEE);                            // MOV AL,20h / OUT DX,AL  (display on)
        a.JmpNear("done");

        a.Label("set_all");                               // ES:DX -> 16 registers and the overscan
        a.B(0x8B, 0xF2, 0x06, 0x1F);                      // MOV SI,DX / PUSH ES / POP DS
        a.B(0xBA, 0xDA, 0x03, 0xEC);                      // MOV DX,3DAh / IN AL,DX
        a.B(0xBA, 0xC0, 0x03, 0x30, 0xDB);                // MOV DX,3C0h / XOR BL,BL
        a.Label("set_all_loop");
        a.B(0x88, 0xD8, 0xEE, 0xAC, 0xEE);                // MOV AL,BL / OUT / LODSB / OUT
        a.B(0xFE, 0xC3, 0x80, 0xFB, 0x10);                // INC BL / CMP BL,10h
        a.Jcc(0x72, "set_all_loop");                      // JB
        a.B(0xB0, 0x11, 0xEE, 0xAC, 0xEE);                // MOV AL,11h / OUT / LODSB / OUT  (overscan)
        a.B(0xB0, 0x20, 0xEE);                            // MOV AL,20h / OUT
        a.JmpNear("done");

        a.Label("blink");                                 // BL bit 0: 1 = blink, 0 = intensity
        a.B(0xBA, 0xDA, 0x03, 0xEC);                      // MOV DX,3DAh / IN AL,DX
        a.B(0xBA, 0xC0, 0x03, 0xB0, 0x10, 0xEE);          // MOV DX,3C0h / MOV AL,10h / OUT
        a.B(0x42, 0xEC);                                  // INC DX / IN AL,DX  (mode register)
        a.B(0x24, 0xF7, 0xF6, 0xC3, 0x01);                // AND AL,F7h / TEST BL,1
        a.Jcc(0x74, "blink_write");                       // JZ
        a.B(0x0C, 0x08);                                  // OR AL,08h
        a.Label("blink_write");
        a.B(0x88, 0xC4);                                  // MOV AH,AL
        a.B(0xBA, 0xDA, 0x03, 0xEC);                      // MOV DX,3DAh / IN AL,DX
        a.B(0xBA, 0xC0, 0x03, 0xB0, 0x10, 0xEE);          // MOV DX,3C0h / MOV AL,10h / OUT
        a.B(0x88, 0xE0, 0xEE, 0xB0, 0x20, 0xEE);          // MOV AL,AH / OUT / MOV AL,20h / OUT
        a.JmpNear("done");

        a.Label("dac_one");                               // BX = entry, DH = red, CH = green, CL = blue
        a.B(0x88, 0xF4);                                  // MOV AH,DH
        a.B(0xBA, 0xC8, 0x03, 0x88, 0xD8, 0xEE);          // MOV DX,3C8h / MOV AL,BL / OUT
        a.B(0x42, 0x88, 0xE0, 0xEE);                      // INC DX / MOV AL,AH / OUT
        a.B(0x88, 0xE8, 0xEE, 0x88, 0xC8, 0xEE);          // MOV AL,CH / OUT / MOV AL,CL / OUT
        a.JmpNear("done");

        a.Label("dac_block");                             // ES:DX -> triples, BX = first entry, CX = count
        a.B(0x8B, 0xF2, 0x06, 0x1F);                      // MOV SI,DX / PUSH ES / POP DS
        a.B(0xBA, 0xC8, 0x03, 0x88, 0xD8, 0xEE, 0x42);    // MOV DX,3C8h / MOV AL,BL / OUT / INC DX
        a.Jcc(0xE3, "dac_block_end");                               // JCXZ
        a.Label("dac_block_loop");
        a.B(0xAC, 0xEE, 0xAC, 0xEE, 0xAC, 0xEE);          // three LODSB / OUT
        a.Jcc(0xE2, "dac_block_loop");                    // LOOP
        a.JmpNear("done");

        a.Label("dac_block_end"); a.JmpNear("done");

        a.Label("colsel");                                // BL = 0: mode bit 7 from BH; BL = 1: colour select = BH
        a.B(0xF6, 0xC3, 0x01);                            // TEST BL,1
        a.Jcc(0x75, "colsel_reg");                        // JNZ
        a.B(0xBA, 0xDA, 0x03, 0xEC);                      // MOV DX,3DAh / IN AL,DX
        a.B(0xBA, 0xC0, 0x03, 0xB0, 0x10, 0xEE);          // MOV DX,3C0h / MOV AL,10h / OUT
        a.B(0x42, 0xEC, 0x24, 0x7F);                      // INC DX / IN AL,DX / AND AL,7Fh
        a.B(0xF6, 0xC7, 0x01);                            // TEST BH,1
        a.Jcc(0x74, "colsel_write");                      // JZ
        a.B(0x0C, 0x80);                                  // OR AL,80h
        a.Label("colsel_write");
        a.B(0x88, 0xC4);                                  // MOV AH,AL
        a.B(0xBA, 0xDA, 0x03, 0xEC);                      // MOV DX,3DAh / IN AL,DX
        a.B(0xBA, 0xC0, 0x03, 0xB0, 0x10, 0xEE);          // MOV DX,3C0h / MOV AL,10h / OUT
        a.B(0x88, 0xE0, 0xEE, 0xB0, 0x20, 0xEE);          // MOV AL,AH / OUT / MOV AL,20h / OUT
        a.JmpNear("done");
        a.Label("colsel_reg");
        a.B(0xBA, 0xDA, 0x03, 0xEC);                      // MOV DX,3DAh / IN AL,DX
        a.B(0xBA, 0xC0, 0x03, 0xB0, 0x14, 0xEE);          // MOV DX,3C0h / MOV AL,14h / OUT
        a.B(0x88, 0xF8, 0xEE, 0xB0, 0x20, 0xEE);          // MOV AL,BH / OUT / MOV AL,20h / OUT

        a.Label("done");
        a.B(0x1F, 0x5E, 0x5A, 0x59, 0x5B, 0x58);          // POP DS,SI,DX,CX,BX,AX
        a.B(0xC3);                                        // RET
        a.Resolve();
        if (a.Position > 0x1900) throw new InvalidOperationException("palette routine too long");
    }

    /// <summary>
    /// A very small assembler for the ROM's hand-written routines: bytes,
    /// labels, and eight-bit relative jumps resolved at the end. It exists
    /// so that no displacement is ever counted by hand.
    /// </summary>
    private sealed class Asm(byte[] rom, int start)
    {
        private readonly Dictionary<string, int> _labels = new();
        private readonly List<(int At, string Label)> _fixups = new();
        public int Position { get; private set; } = start;

        public void B(params byte[] bytes) { foreach (byte b in bytes) rom[Position++] = b; }
        public void Label(string name) => _labels[name] = Position;

        /// <summary>A conditional (or LOOP/JCXZ) short jump to a label.</summary>
        public void Jcc(byte opcode, string label) { B(opcode, 0); _fixups.Add((Position - 1, label)); }
        public void Jmp(string label) => Jcc(0xEB, label);

        /// <summary>An unconditional jump with a 16-bit displacement, for targets further than 127 bytes.</summary>
        public void JmpNear(string label) { B(0xE9, 0, 0); _nearFixups.Add((Position - 2, label)); }
        private readonly List<(int At, string Label)> _nearFixups = new();

        public void CallNear(int target) { int rel = target - (Position + 3); B(0xE8, (byte)rel, (byte)(rel >> 8)); }

        public void Resolve()
        {
            foreach ((int at, string label) in _fixups)
            {
                int rel = _labels[label] - (at + 1);
                if (rel is < -128 or > 127) throw new InvalidOperationException($"jump to {label} out of range");
                rom[at] = (byte)(sbyte)rel;
            }
            foreach ((int at, string label) in _nearFixups)
            {
                int rel = _labels[label] - (at + 2);
                rom[at] = (byte)rel; rom[at + 1] = (byte)(rel >> 8);
            }
        }
    }

    /// <summary>
    /// Real-mode code. AL holds the mode on entry; every register is
    /// preserved. The table entry is found by a linear search, then the
    /// values are written in the order a ROM writes them: miscellaneous
    /// output, the sequencer around a synchronous reset, the CRTC after
    /// unlocking registers 0-7, the graphics controller, and the attribute
    /// controller through its index/data flip-flop.
    /// </summary>
    private static void WriteVgaProgram(byte[] rom)
    {
        const int EntrySize = 62;
        int o = Entry.VgaProgram;
        void B(params byte[] bytes) { foreach (byte b in bytes) rom[o++] = b; }

        B(0x50, 0x53, 0x51, 0x52, 0x56, 0x1E);            // PUSH AX,BX,CX,DX,SI,DS
        B(0x0E, 0x1F);                                    // PUSH CS / POP DS
        B(0x24, 0x7F);                                    // AND AL,7Fh
        B(0xBE, unchecked((byte)Entry.VgaTables), unchecked((byte)(Entry.VgaTables >> 8)));   // MOV SI,tables
        int find = o;
        B(0x38, 0x04);                                    // CMP [SI],AL
        B(0x74, 0x0A);                                    // JE found
        B(0x80, 0x3C, 0xFF);                              // CMP BYTE [SI],FFh
        B(0x74, 0x05);                                    // JE found (the default set)
        B(0x83, 0xC6, EntrySize);                         // ADD SI,62
        B(0xEB, (byte)(find - (o + 2)));                  // JMP find
        // found:
        B(0x46);                                          // INC SI
        B(0xBA, 0xC2, 0x03, 0xAC, 0xEE);                  // MOV DX,3C2h / LODSB / OUT DX,AL
        B(0xBA, 0xC4, 0x03);                              // MOV DX,3C4h
        B(0xB8, 0x00, 0x01, 0xEF);                        // MOV AX,0100h / OUT DX,AX  (synchronous reset)
        B(0xAC, 0x88, 0xC3);                              // LODSB / MOV BL,AL  (register 0, written last)
        B(0xB9, 0x04, 0x00, 0xB0, 0x01);                  // MOV CX,4 / MOV AL,1
        B(0x8A, 0x24, 0x46, 0xEF, 0xFE, 0xC0, 0xE2, 0xF8);   // loop: MOV AH,[SI] / INC SI / OUT DX,AX / INC AL / LOOP
        B(0x88, 0xDC, 0x30, 0xC0, 0xEF);                  // MOV AH,BL / XOR AL,AL / OUT DX,AX  (reset released)
        B(0xBA, 0xCC, 0x03, 0xEC);                        // MOV DX,3CCh / IN AL,DX  (misc output: colour or mono?)
        B(0xBA, 0xD4, 0x03, 0xA8, 0x01, 0x75, 0x02, 0xB2, 0xB4);   // MOV DX,3D4h / TEST AL,1 / JNZ +2 / MOV DL,B4h
        B(0xB8, 0x11, 0x00, 0xEF);                        // MOV AX,0011h / OUT DX,AX  (unlock CRTC 0-7)
        B(0xB9, 0x19, 0x00, 0x30, 0xC0);                  // MOV CX,25 / XOR AL,AL
        B(0x8A, 0x24, 0x46, 0xEF, 0xFE, 0xC0, 0xE2, 0xF8);   // CRTC loop
        B(0xBA, 0xCE, 0x03, 0xB9, 0x09, 0x00, 0x30, 0xC0);   // MOV DX,3CEh / MOV CX,9 / XOR AL,AL
        B(0x8A, 0x24, 0x46, 0xEF, 0xFE, 0xC0, 0xE2, 0xF8);   // graphics controller loop
        B(0xBA, 0xDA, 0x03, 0xEC);                        // MOV DX,3DAh / IN AL,DX  (flip-flop to index)
        B(0xBA, 0xC0, 0x03, 0xB9, 0x15, 0x00, 0x30, 0xDB);   // MOV DX,3C0h / MOV CX,21 / XOR BL,BL
        B(0x88, 0xD8, 0xEE, 0xAC, 0xEE, 0xFE, 0xC3, 0xE2, 0xF7);   // loop: MOV AL,BL / OUT / LODSB / OUT / INC BL / LOOP (-9)
        B(0xB0, 0x20, 0xEE);                              // MOV AL,20h / OUT DX,AL  (display back on)
        B(0x1F, 0x5E, 0x5A, 0x59, 0x5B, 0x58);            // POP DS,SI,DX,CX,BX,AX
        B(0xC3);                                          // RET
        if (o > Entry.VgaTables) throw new InvalidOperationException("VGA routine overruns its tables");
    }

    /// <summary>
    /// Real-mode code, entered with the INT 10h registers. For AX = 4F02h
    /// it runs <see cref="Entry.VgaProgram"/> with the mode whose register
    /// set best describes the VBE mode in BX: the mode number itself when
    /// BX names a VGA mode (a program going back to text), 12h for the
    /// planar 16-colour mode 102h, 13h for everything else -- one byte per
    /// pixel or more, graphics, decoded at A0000h. Every register is
    /// preserved for the native handler that follows.
    /// </summary>
    private static void WriteVesaProgram(byte[] rom)
    {
        var entry = new Asm(rom, Entry.VesaProgram);
        entry.CallNear(Entry.VesaModeProgram);
        entry.B(0xC3);
        WriteVesaModeProgram(rom);
    }

    private static void WriteVgaTables(byte[] rom)
    {
        int o = Entry.VgaTables;
        void Put(byte mode, Devices.Video.VgaRegisterTables.RegisterSet set)
        {
            rom[o++] = mode;
            rom[o++] = set.MiscOutput;
            foreach (byte b in set.Sequencer) rom[o++] = b;
            foreach (byte b in set.Crtc) rom[o++] = b;
            foreach (byte b in set.Graphics) rom[o++] = b;
            foreach (byte b in set.Attribute) rom[o++] = b;
        }
        foreach (byte mode in new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x0D, 0x0E, 0x0F, 0x10, 0x11, 0x12, 0x13 })
            Put(mode, Devices.Video.VgaRegisterTables.For(mode));
        Put(0xFF, Devices.Video.VgaRegisterTables.For(0x03));   // end marker, and the default
        if (o > Entry.VesaProgram) throw new InvalidOperationException("VGA tables overrun the VESA routine");
    }

    /// <summary>
    /// A hardware interrupt entry. The native handler does its bookkeeping,
    /// then the ROM itself finishes the job with real instructions: the timer
    /// calls the INT 1Ch user hook, the mouse reads its byte from the
    /// keyboard controller, and every one of them acknowledges the interrupt
    /// controller with OUT -- the slave first where the line hangs off it.
    /// These have to be processor instructions and not native shortcuts: a
    /// monitor that virtualises the interrupt controller for a virtual
    /// machine only learns of the end of interrupt from the OUT it traps,
    /// and Windows would otherwise hold the line "in service" for ever and
    /// deliver no further ticks to the machine.
    /// </summary>
    private static void WriteIrqEntry(byte[] rom, ushort offset, Handler handler, bool slave, bool userTick, bool readKeyboard)
    {
        int o = offset;
        rom[o++] = HookByte1;
        rom[o++] = HookByte2;
        rom[o++] = (byte)handler;
        rom[o++] = 0x50;                                   // PUSH AX
        if (userTick) { rom[o++] = 0xCD; rom[o++] = 0x1C; }   // INT 1Ch
        if (readKeyboard) { rom[o++] = 0xE4; rom[o++] = 0x60; }   // IN AL,60h
        rom[o++] = 0xB0; rom[o++] = 0x20;                  // MOV AL,20h
        if (slave) { rom[o++] = 0xE6; rom[o++] = 0xA0; }   // OUT A0h,AL
        rom[o++] = 0xE6; rom[o++] = 0x20;                  // OUT 20h,AL
        rom[o++] = 0x58;                                   // POP AX
        rom[o++] = 0xCF;                                   // IRET
        if (o > offset + 16) throw new InvalidOperationException("IRQ entry overruns its slot");
    }

    /// <summary>
    /// IRQ 12, the PS/2 mouse. Unlike the other IRQ entries the hook comes
    /// <b>after</b> the port read and the EOI, with the byte parked in AH:
    /// the native side has to see the byte, and the real BIOS too acknowledges
    /// the interrupt before it calls the INT 15h C207h handler, so that the
    /// handler runs with the controller free to accept the next byte.
    /// </summary>
    private static void WriteMouseIrqEntry(byte[] rom, ushort offset, Handler handler)
    {
        int o = offset;
        rom[o++] = 0x50;                                   // PUSH AX
        rom[o++] = 0xE4; rom[o++] = 0x60;                  // IN AL,60h
        rom[o++] = 0x88; rom[o++] = 0xC4;                  // MOV AH,AL
        rom[o++] = 0xB0; rom[o++] = 0x20;                  // MOV AL,20h
        rom[o++] = 0xE6; rom[o++] = 0xA0;                  // OUT A0h,AL
        rom[o++] = 0xE6; rom[o++] = 0x20;                  // OUT 20h,AL
        rom[o++] = HookByte1;
        rom[o++] = HookByte2;
        rom[o++] = (byte)handler;
        rom[o++] = 0x58;                                   // POP AX
        rom[o++] = 0xCF;                                   // IRET
        if (o > offset + 16) throw new InvalidOperationException("IRQ entry overruns its slot");
    }

    private static void WriteInterruptEntry(byte[] rom, ushort offset, Handler handler)
    {
        rom[offset + 0] = HookByte1;
        rom[offset + 1] = HookByte2;
        rom[offset + 2] = (byte)handler;
        rom[offset + 3] = 0xCF;        // IRET
    }

    private static void WriteWord(byte[] rom, int offset, ushort value)
    {
        rom[offset] = (byte)value;
        rom[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteDword(byte[] rom, int offset, uint value)
    {
        WriteWord(rom, offset, (ushort)value);
        WriteWord(rom, offset + 2, (ushort)(value >> 16));
    }
}
