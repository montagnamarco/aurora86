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

    private static void WriteInterruptEntry(byte[] rom, ushort offset, Handler handler)
    {
        rom[offset + 0] = HookByte1;
        rom[offset + 1] = HookByte2;
        rom[offset + 2] = (byte)handler;
        rom[offset + 3] = 0xCF;        // IRET
    }
}
