using System.Text;

namespace Aurora386.Core.Firmware;

/// <summary>
/// What the BIOS needs to know about the booted CPU to fill in the MP
/// table's own processor entry: the same three facts a real BIOS reads off
/// the processor at POST rather than compiling in, but Aurora's BIOS is
/// prepared in C# before the guest ever runs a single instruction, so
/// <c>Machine.InstallBios</c> reads them out of <c>Cpu.State</c> itself and
/// hands them in here. Null (the default <c>Build</c> parameter) means "no
/// MP tables at all" -- an i430FX board, or an i430TX board whose CPU has no
/// on-die APIC (coordinator decision A2 in plans/specs/apic.md).
/// </summary>
public readonly record struct MpSystemInfo(uint CpuSignature, uint CpuidEdx, byte LocalApicVersion);

public static partial class BiosRom
{
    // ---- MP 1.4 floating pointer structure (16 bytes, 16-byte aligned; plans/specs/apic.md §3) ----

    private const uint MpFloatingPointerSignature = 0x5F504D5F;   // "_MP_"
    private const byte MpFloatingPointerLengthParagraphs = 1;     // 16 bytes / 16
    private const byte MpSpecificationRevision14 = 4;
    private const byte MpFeature1TablePresent = 0;                // 0 = a real PCMP table follows, not a default configuration
    private const byte MpFeature2ImcrBit = 1 << 7;                // IMCRP: an IMCR exists and starts in PIC mode

    // ---- PCMP base configuration table header (44 bytes; plans/specs/apic.md §3) ----

    private const uint McpSignature = 0x504D4350;   // "PCMP"
    private const byte McpRevision14 = 4;
    private const string OemId = "AURORA  ";        // 8 bytes, space-padded
    private const string ProductId = "AURORA386   ";   // 12 bytes, space-padded
    private const uint LocalApicPhysicalBase = 0xFEE00000;   // IA32_APIC_BASE's own reset address
    private const uint IoApicPhysicalBase = 0xFEC00000;      // fixed; see Machine.IoApicPhysicalBase and decision A3

    // ---- entry types and their fixed sizes -------------------------------

    private const byte EntryTypeProcessor = 0;
    private const byte EntryTypeBus = 1;
    private const byte EntryTypeIoApic = 2;
    private const byte EntryTypeIoInterrupt = 3;
    private const byte EntryTypeLocalInterrupt = 4;
    private const int ProcessorEntryLength = 20;
    private const int BusEntryLength = 8;
    private const int IoApicEntryLength = 8;
    private const int IoInterruptEntryLength = 8;
    private const int LocalInterruptEntryLength = 8;

    // Interrupt entry "type" byte (distinct from the table entry type above): plans/specs/apic.md §3.
    private const byte InterruptTypeInt = 0;
    private const byte InterruptTypeNmi = 1;
    private const byte InterruptTypeExtInt = 3;

    // Interrupt entry flags, bits 1:0 PO and 3:2 EL: 00 conforms to the named bus's own default,
    // 11 is active-low/level -- the pair PIRQ A-D need (plans/specs/apic.md §2's pin table).
    private const ushort InterruptFlagsConforming = 0b0000;
    private const ushort InterruptFlagsActiveLowLevel = 0b1111;

    private const byte IsaBusId = 0;
    private const byte PciBusId = 1;
    private const byte IoApicId = 0;   // matches IoApic.Reset()'s own default; nothing reprograms it during POST
    private const byte AllLocalApics = 0xFF;

    /// <summary>The virtual PCI slots this board's INTA-D rotation covers; see <see cref="Devices.Pci.PciInterruptRouter.LastSlotDevice"/>.</summary>
    private const byte FirstSlotDevice = Devices.Pci.PciInterruptRouter.FirstSlotDevice;
    private const byte LastSlotDevice = Devices.Pci.PciInterruptRouter.LastSlotDevice;

    /// <summary>
    /// Every ISA IRQ this board wires to the I/O APIC, and which INTIN pin
    /// each lands on -- <see cref="Devices.Interrupts.InterruptController"/>'s
    /// own <c>IsaPin</c> table, restated here because the BIOS's MP entries
    /// and the running machine's actual wiring must never disagree about
    /// which pin an IRQ reaches. IRQ 2 (the cascade line, never raised on
    /// its own) has no entry, matching that same rule.
    /// </summary>
    private static readonly (byte Irq, byte Pin)[] WiredIsaLines =
    [
        (0, 2), (1, 1), (3, 3), (4, 4), (5, 5), (6, 6), (7, 7), (8, 8), (9, 9),
        (10, 10), (11, 11), (12, 12), (13, 13), (14, 14), (15, 15),
    ];

    /// <summary>
    /// Builds the floating pointer and PCMP table at <see
    /// cref="Entry.MpFloatingPointer"/>/<see cref="Entry.MpConfigurationTable"/>.
    /// Only called for a board with both an I/O APIC and an IMCR, and a CPU
    /// whose silicon carries a local APIC -- see the call site in
    /// <c>Machine.InstallBios</c>.
    /// </summary>
    private static void WriteMpTables(byte[] rom, MpSystemInfo info)
    {
        int entryCount = 0;
        int at = Entry.MpConfigurationTable + 44;   // entries follow the 44-byte header

        at = WriteProcessorEntry(rom, at, info, ref entryCount);
        at = WriteBusEntry(rom, at, IsaBusId, "ISA   ", ref entryCount);
        at = WriteBusEntry(rom, at, PciBusId, "PCI   ", ref entryCount);
        at = WriteIoApicEntry(rom, at, ref entryCount);

        foreach ((byte irq, byte pin) in WiredIsaLines)
        {
            at = WriteIoInterruptEntry(rom, at, InterruptTypeInt, InterruptFlagsConforming,
                IsaBusId, irq, pin, ref entryCount);
        }

        // One entry per wired (device, pin), not per PIRQ link (independent
        // review, item 5): the board's virtual slots (devices 2-7)
        // each carry all four INTA-D pins, source bus IRQ encoded the way
        // the MP spec's own PCI convention asks -- bits 6:2 device, 1:0 pin
        // -- and destination INTIN computed from the same
        // PciInterruptRouter.Link the running machine uses to route them,
        // so the table and the machine can never disagree about which pin
        // a (device, pin) combination reaches.
        for (byte device = FirstSlotDevice; device <= LastSlotDevice; device++)
        {
            for (byte pin = 1; pin <= 4; pin++)
            {
                ushort address = (ushort)(device << 3);
                int link = Devices.Pci.PciInterruptRouter.Link(address, pin);
                byte sourceBusIrq = (byte)((device << 2) | (pin - 1));
                at = WriteIoInterruptEntry(rom, at, InterruptTypeInt, InterruptFlagsActiveLowLevel,
                    PciBusId, sourceBusIrq, (byte)(16 + link), ref entryCount);
            }
        }

        // PIIX4's own integrated USB function, 00:01.2 (address 000Ah):
        // fixed INTA wired to PIRQD regardless of the slot rotation above
        // (PciInterruptRouter.Link's own special case for this address).
        {
            const byte UsbDevice = 1;
            const byte UsbPin = 1;
            const ushort UsbAddress = 0x000A;
            int link = Devices.Pci.PciInterruptRouter.Link(UsbAddress, UsbPin);
            byte sourceBusIrq = (byte)((UsbDevice << 2) | (UsbPin - 1));
            at = WriteIoInterruptEntry(rom, at, InterruptTypeInt, InterruptFlagsActiveLowLevel,
                PciBusId, sourceBusIrq, (byte)(16 + link), ref entryCount);
        }

        at = WriteLocalInterruptEntry(rom, at, InterruptTypeExtInt, lintPin: 0, ref entryCount);
        at = WriteLocalInterruptEntry(rom, at, InterruptTypeNmi, lintPin: 1, ref entryCount);

        int baseTableLength = at - Entry.MpConfigurationTable;
        WriteConfigurationHeader(rom, baseTableLength, entryCount);
        WriteFloatingPointer(rom);
    }

    private static int WriteProcessorEntry(byte[] rom, int at, MpSystemInfo info, ref int entryCount)
    {
        const byte LocalApicId = 0;
        const byte FlagEnabled = 1 << 0;
        const byte FlagBootstrapProcessor = 1 << 1;

        rom[at] = EntryTypeProcessor;
        rom[at + 1] = LocalApicId;
        rom[at + 2] = info.LocalApicVersion;
        rom[at + 3] = FlagEnabled | FlagBootstrapProcessor;
        WriteDword(rom, at + 4, info.CpuSignature);
        WriteDword(rom, at + 8, info.CpuidEdx);
        // Bytes 12-19 are reserved and stay zero (Array.Fill with 0xCC below the entries already cleared to CC; explicit clear here since this range must read zero regardless of that filler).
        Array.Clear(rom, at + 12, 8);
        entryCount++;
        return at + ProcessorEntryLength;
    }

    private static int WriteBusEntry(byte[] rom, int at, byte busId, string busType, ref int entryCount)
    {
        rom[at] = EntryTypeBus;
        rom[at + 1] = busId;
        Encoding.ASCII.GetBytes(busType).CopyTo(rom, at + 2);
        entryCount++;
        return at + BusEntryLength;
    }

    private static int WriteIoApicEntry(byte[] rom, int at, ref int entryCount)
    {
        const byte FlagEnabled = 1 << 0;

        rom[at] = EntryTypeIoApic;
        rom[at + 1] = IoApicId;
        rom[at + 2] = 0x11;   // version, matching IoApic's own IOAPICVER (00170011h's low byte)
        rom[at + 3] = FlagEnabled;
        WriteDword(rom, at + 4, IoApicPhysicalBase);
        entryCount++;
        return at + IoApicEntryLength;
    }

    private static int WriteIoInterruptEntry(
        byte[] rom, int at, byte interruptType, ushort flags, byte sourceBusId, byte sourceBusIrq, byte destinationIntin,
        ref int entryCount)
    {
        rom[at] = EntryTypeIoInterrupt;
        rom[at + 1] = interruptType;
        rom[at + 2] = (byte)flags;
        rom[at + 3] = (byte)(flags >> 8);
        rom[at + 4] = sourceBusId;
        rom[at + 5] = sourceBusIrq;
        rom[at + 6] = IoApicId;
        rom[at + 7] = destinationIntin;
        entryCount++;
        return at + IoInterruptEntryLength;
    }

    private static int WriteLocalInterruptEntry(byte[] rom, int at, byte interruptType, byte lintPin, ref int entryCount)
    {
        rom[at] = EntryTypeLocalInterrupt;
        rom[at + 1] = interruptType;
        rom[at + 2] = (byte)InterruptFlagsConforming;
        rom[at + 3] = (byte)(InterruptFlagsConforming >> 8);
        rom[at + 4] = IsaBusId;   // no real source; a placeholder like the PIRQ entries' source IRQ
        rom[at + 5] = 0;
        rom[at + 6] = AllLocalApics;
        rom[at + 7] = lintPin;
        entryCount++;
        return at + LocalInterruptEntryLength;
    }

    private static void WriteConfigurationHeader(byte[] rom, int baseTableLength, int entryCount)
    {
        int header = Entry.MpConfigurationTable;
        Array.Clear(rom, header, 44);

        WriteDword(rom, header, McpSignature);
        WriteWord(rom, header + 4, (ushort)baseTableLength);
        rom[header + 6] = McpRevision14;
        // header + 7 is the checksum, filled in last.
        Encoding.ASCII.GetBytes(OemId).CopyTo(rom, header + 8);
        Encoding.ASCII.GetBytes(ProductId).CopyTo(rom, header + 16);
        WriteDword(rom, header + 28, 0);           // no OEM table
        WriteWord(rom, header + 32, 0);
        WriteWord(rom, header + 34, (ushort)entryCount);
        WriteDword(rom, header + 36, LocalApicPhysicalBase);
        WriteWord(rom, header + 40, 0);             // no extended table
        rom[header + 42] = 0;                       // extended checksum: none, since there is no extended table
        rom[header + 43] = 0;

        int sum = 0;
        for (int i = 0; i < baseTableLength; i++) sum += rom[header + i];
        rom[header + 7] = unchecked((byte)-sum);
    }

    private static void WriteFloatingPointer(byte[] rom)
    {
        int at = Entry.MpFloatingPointer;
        Array.Clear(rom, at, 16);

        WriteDword(rom, at, MpFloatingPointerSignature);
        WriteDword(rom, at + 4, Base + Entry.MpConfigurationTable);
        rom[at + 8] = MpFloatingPointerLengthParagraphs;
        rom[at + 9] = MpSpecificationRevision14;
        // at + 10 is the checksum, filled in last.
        rom[at + 11] = MpFeature1TablePresent;
        rom[at + 12] = MpFeature2ImcrBit;
        rom[at + 13] = 0;
        rom[at + 14] = 0;
        rom[at + 15] = 0;

        int sum = 0;
        for (int i = 0; i < 16; i++) sum += rom[at + i];
        rom[at + 10] = unchecked((byte)-sum);
    }
}
