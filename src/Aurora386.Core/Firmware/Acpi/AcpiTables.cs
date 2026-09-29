using System.Buffers.Binary;
using System.Text;

namespace Aurora386.Core.Firmware.Acpi;

/// <summary>
/// Builds the ACPI 1.0b tables the i430TX/PIIX4 board's BIOS ships (work
/// package P4.5, coordinator decisions C1-C3 in plans/specs/acpi.md): the
/// RSDP, RSDT, FADT (FACP), FACS and MADT as fixed-format byte tables, and
/// the DSDT's AML namespace through <see cref="AmlWriter"/> and <see
/// cref="AcpiResources"/>. <see cref="BiosRom"/> calls these once the
/// addresses each table needs to embed (FACS/DSDT inside the FADT, FADT/
/// MADT inside the RSDT) are known, and patches in checksums that depend on
/// where in the ROM the caller finally places each table.
/// </summary>
public static class AcpiTables
{
    // =====================================================================
    // The System Description Table header every table but the RSDP and the
    // FACS opens with (ACPI 1.0b §5.2.6): 36 bytes, common to the RSDT, the
    // FADT, the MADT and the DSDT.
    // =====================================================================

    private const int SdtSignatureOffset = 0;
    private const int SdtLengthOffset = 4;
    private const int SdtRevisionOffset = 8;
    private const int SdtChecksumOffset = 9;
    private const int SdtOemIdOffset = 10;        // 6 bytes
    private const int SdtOemTableIdOffset = 16;   // 8 bytes
    private const int SdtOemRevisionOffset = 24;
    private const int SdtCreatorIdOffset = 28;
    private const int SdtCreatorRevisionOffset = 32;
    public const int SdtHeaderLength = 36;

    private const string OemId = "AURORA";           // 6 bytes, matches BiosRom.Mp.cs's own OEM ID text.
    private const string OemTableId = "AURORA386";    // 8 bytes, matches BiosRom.Mp.cs's own product string.
    private const uint CreatorId = 0x31525541;        // "AUR1": this BIOS's own stand-in for an ASL compiler's id, since there is no iasl here (decision C3).
    private const uint CreatorRevision = 1;

    private static void WriteHeader(byte[] table, string signature, byte revision)
    {
        Encoding.ASCII.GetBytes(signature).CopyTo(table, SdtSignatureOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(SdtLengthOffset), (uint)table.Length);
        table[SdtRevisionOffset] = revision;
        WritePadded(table, SdtOemIdOffset, OemId, 6);
        WritePadded(table, SdtOemTableIdOffset, OemTableId, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(SdtOemRevisionOffset), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(SdtCreatorIdOffset), CreatorId);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(SdtCreatorRevisionOffset), CreatorRevision);
        FixChecksum(table, SdtChecksumOffset);
    }

    private static void WritePadded(byte[] table, int offset, string text, int length)
    {
        for (int i = 0; i < length; i++)
        {
            table[offset + i] = (byte)(i < text.Length ? text[i] : ' ');
        }
    }

    /// <summary>Every ACPI table checksums to zero over its whole length (§5.2.6); this zeroes the checksum byte first so it does not count itself twice.</summary>
    private static void FixChecksum(byte[] table, int checksumOffset)
    {
        table[checksumOffset] = 0;
        int sum = 0;
        foreach (byte value in table) sum += value;
        table[checksumOffset] = unchecked((byte)-sum);
    }

    // =====================================================================
    // RSDP -- ACPI 1.0b §5.2.5.1. Revision 0, 20 bytes, no XSDT (decision C1).
    // =====================================================================

    private const int RsdpSignatureOffset = 0;      // "RSD PTR ", 8 bytes.
    private const int RsdpChecksumOffset = 8;       // over bytes 0-19.
    private const int RsdpOemIdOffset = 9;          // 6 bytes.
    private const int RsdpRevisionOffset = 15;
    private const int RsdpRsdtAddressOffset = 16;
    public const int RsdpLength = 20;
    public const byte RsdpRevision = 0;

    public static byte[] BuildRsdp(uint rsdtAddress)
    {
        byte[] table = new byte[RsdpLength];
        Encoding.ASCII.GetBytes("RSD PTR ").CopyTo(table, RsdpSignatureOffset);
        WritePadded(table, RsdpOemIdOffset, OemId, 6);
        table[RsdpRevisionOffset] = RsdpRevision;
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(RsdpRsdtAddressOffset), rsdtAddress);
        FixChecksum(table, RsdpChecksumOffset);
        return table;
    }

    // =====================================================================
    // RSDT -- ACPI §5.2.7: the header above, then one 32-bit pointer per
    // top-level table -- the FADT always, the MADT only when it exists
    // (decision C1: exactly when the MP tables do).
    // =====================================================================

    public static byte[] BuildRsdt(uint fadtAddress, uint? madtAddress)
    {
        int entryCount = madtAddress.HasValue ? 2 : 1;
        byte[] table = new byte[SdtHeaderLength + entryCount * 4];

        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(SdtHeaderLength), fadtAddress);
        if (madtAddress is { } madt)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(SdtHeaderLength + 4), madt);
        }

        WriteHeader(table, "RSDT", revision: 1);
        return table;
    }

    // =====================================================================
    // FACS -- ACPI 1.0b §5.2.9: 64 bytes, no SDT header (the RSDT never
    // points at it directly; only the FADT's FIRMWARE_CTRL does). S3 is out
    // of scope (decision C6), so there is no waking vector and every field
    // past the length stays zero.
    // =====================================================================

    public const int FacsLength = 64;
    private const uint FacsSignature = 0x53434146;   // "FACS"

    public static byte[] BuildFacs()
    {
        byte[] table = new byte[FacsLength];
        BinaryPrimitives.WriteUInt32LittleEndian(table, FacsSignature);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(4), (uint)FacsLength);
        return table;
    }

    // =====================================================================
    // FADT (FACP) -- ACPI 1.0b §5.2.8, revision 1, 116 bytes. Every offset
    // is coordinator decision C2 in plans/specs/acpi.md, restated here as a
    // named constant per skills.md's rule against unnamed table offsets.
    // =====================================================================

    public const int FadtLength = 116;

    private const int FadtFirmwareCtrlOffset = 36;
    private const int FadtDsdtOffset = 40;
    private const int FadtIntModelOffset = 44;
    // 45 reserved.
    private const int FadtSciIntOffset = 46;
    private const int FadtSmiCmdOffset = 48;
    private const int FadtAcpiEnableOffset = 52;
    private const int FadtAcpiDisableOffset = 53;
    // 54 S4BIOS_REQ, 55 reserved: neither is modelled (no S4, decision C6), so both stay zero.
    private const int FadtPm1aEvtBlkOffset = 56;
    // 60 PM1b_EVT_BLK, 68 PM1b_CNT_BLK, 72 PM2_CNT_BLK, 84 GPE1_BLK: all zero (decision C2, "everything B, PM2 and GPE1 is 0").
    private const int FadtPm1aCntBlkOffset = 64;
    private const int FadtPmTmrBlkOffset = 76;
    private const int FadtGpe0BlkOffset = 80;
    private const int FadtPm1EvtLenOffset = 88;
    private const int FadtPm1CntLenOffset = 89;
    private const int FadtPmTmLenOffset = 91;
    private const int FadtGpe0BlkLenOffset = 92;
    // 95 reserved.
    private const int FadtPLvl2LatOffset = 96;
    private const int FadtPLvl3LatOffset = 98;
    // 100-107: FLUSH_SIZE/STRIDE, DUTY_OFFSET/WIDTH, DAY_ALRM, MON_ALRM -- none of these legacy fields is modelled; all stay zero.
    private const int FadtCenturyOffset = 108;
    // 109-111 reserved.
    private const int FadtFlagsOffset = 112;

    /// <summary>SCI_INT 9 (decision C4; plans/specs/apic.md §2's pin table).</summary>
    private const ushort SciInt = 9;

    /// <summary>SMI_CMD B2h, cross-checked against the actual device port so the two can never silently drift apart.</summary>
    private const uint SmiCmdPort = Devices.Chipset.Piix4PowerManagement.SmiCommandPort;

    private const byte AcpiEnableCommand = 0xF1;
    private const byte AcpiDisableCommand = 0xF0;

    private const byte Pm1EvtLen = 4;
    private const byte Pm1CntLen = 2;
    private const byte PmTmrLen = 4;
    private const byte Gpe0BlkLen = 4;

    /// <summary>P_LVL2_LAT above 100 microseconds: C2 unsupported (decision C2).</summary>
    private const ushort PLvl2LatUnsupported = 101;

    /// <summary>P_LVL3_LAT above 1000 microseconds: C3 unsupported (decision C2).</summary>
    private const ushort PLvl3LatUnsupported = 1001;

    private const uint FlagWbInvd = 1 << 0;
    private const uint FlagProcC1 = 1 << 2;
    private const uint FlagPwrButton = 0;          // bit 4: a fixed-feature power button (this board has none -- decision C2/C5).
    private const uint FlagSlpButton = 1 << 5;     // no sleep button (decision C2).
    private const uint FlagTmrValExt = 0;          // bit 8: the PM timer is 24-bit, not 32 (decision C2).

    public static byte[] BuildFadt(
        uint facsAddress, uint dsdtAddress, bool hasMadt, ushort pmBase, byte centuryRegister)
    {
        byte[] table = new byte[FadtLength];

        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(FadtFirmwareCtrlOffset), facsAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(FadtDsdtOffset), dsdtAddress);
        table[FadtIntModelOffset] = hasMadt ? (byte)1 : (byte)0;   // 0 dual PIC, 1 multiple APIC (decision C2).
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(FadtSciIntOffset), SciInt);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(FadtSmiCmdOffset), SmiCmdPort);
        table[FadtAcpiEnableOffset] = AcpiEnableCommand;
        table[FadtAcpiDisableOffset] = AcpiDisableCommand;

        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(FadtPm1aEvtBlkOffset), pmBase);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(FadtPm1aCntBlkOffset), (uint)(pmBase + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(FadtPmTmrBlkOffset), (uint)(pmBase + 8));
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(FadtGpe0BlkOffset), (uint)(pmBase + 0x0C));

        table[FadtPm1EvtLenOffset] = Pm1EvtLen;
        table[FadtPm1CntLenOffset] = Pm1CntLen;
        table[FadtPmTmLenOffset] = PmTmrLen;
        table[FadtGpe0BlkLenOffset] = Gpe0BlkLen;

        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(FadtPLvl2LatOffset), PLvl2LatUnsupported);
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(FadtPLvl3LatOffset), PLvl3LatUnsupported);

        table[FadtCenturyOffset] = centuryRegister;

        uint flags = FlagWbInvd | FlagProcC1 | FlagPwrButton | FlagSlpButton | FlagTmrValExt;
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(FadtFlagsOffset), flags);

        WriteHeader(table, "FACP", revision: 1);
        return table;
    }

    // =====================================================================
    // MADT -- ACPI §5.2.12. Built from exactly the same facts as the MP
    // tables (BiosRom.Mp.cs), never independently: one CPU, one I/O APIC,
    // the same two overrides and the same NMI wiring, so an OS reading
    // either table learns the identical configuration (plans/specs/apic.md,
    // cross-checked against the P4.5 gating note).
    // =====================================================================

    /// <summary>IA32_APIC_BASE's own reset address (plans/specs/apic.md §1.1) -- what the table names, whatever a running guest later relocates the MSR to.</summary>
    private const uint MadtLocalApicAddress = 0xFEE00000;

    private const uint MadtFlagPcatCompat = 1 << 0;

    private const byte MadtTypeLocalApic = 0;
    private const byte MadtTypeIoApic = 1;
    private const byte MadtTypeInterruptOverride = 2;
    private const byte MadtTypeLocalApicNmi = 4;

    private const int MadtLocalApicEntryLength = 8;
    private const int MadtIoApicEntryLength = 12;
    private const int MadtOverrideEntryLength = 10;
    private const int MadtLocalApicNmiEntryLength = 6;

    private const uint MadtLocalApicFlagEnabled = 1 << 0;

    /// <summary>MPS INTI flags (ACPI §5.2.12.5): bits 1:0 Polarity, bits 3:2 Trigger Mode; 00 conforms to the bus default.</summary>
    private const ushort MpsIntiConforms = 0b0000;

    /// <summary>Active low (11), level (11): the SCI's own wiring (decision C4).</summary>
    private const ushort MpsIntiActiveLowLevel = 0b1111;

    /// <summary>Matches <c>BiosRom.Mp.cs</c>'s own fixed IDs: local APIC ID 0 (the one BSP) and I/O APIC ID 0 (<c>IoApic.Reset()</c>'s own default), so the two tables can never name different processors or a different I/O APIC.</summary>
    public static byte[] BuildMadt(uint ioApicAddress)
    {
        const byte LocalApicId = 0;
        const byte IoApicId = 0;
        const byte IsaBus = 0;

        int length = SdtHeaderLength + 8
            + MadtLocalApicEntryLength
            + MadtIoApicEntryLength
            + 2 * MadtOverrideEntryLength
            + MadtLocalApicNmiEntryLength;
        byte[] table = new byte[length];

        int at = SdtHeaderLength;
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(at), MadtLocalApicAddress);
        at += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(at), MadtFlagPcatCompat);
        at += 4;

        // Processor Local APIC: the one BSP the MP table's own single processor entry names.
        table[at++] = MadtTypeLocalApic;
        table[at++] = MadtLocalApicEntryLength;
        table[at++] = 0;                 // ACPI Processor ID.
        table[at++] = LocalApicId;
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(at), MadtLocalApicFlagEnabled);
        at += 4;

        // I/O APIC, fixed at FEC00000h (decision A3), GSI base 0.
        table[at++] = MadtTypeIoApic;
        table[at++] = MadtIoApicEntryLength;
        table[at++] = IoApicId;
        table[at++] = 0;                 // reserved.
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(at), ioApicAddress);
        at += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(at), 0);   // GSI base.
        at += 4;

        // IRQ0 -> GSI2, conforming: the PIIX3/4 wiring table (plans/specs/apic.md §2).
        at = WriteOverride(table, at, IsaBus, irq: 0, gsi: 2, MpsIntiConforms);

        // IRQ9 -> GSI9, level, active low: the SCI (decision C4).
        at = WriteOverride(table, at, IsaBus, irq: 9, gsi: 9, MpsIntiActiveLowLevel);

        // Local APIC NMI on LINT1, every processor (plans/specs/apic.md §1.4's virtual-wire wiring).
        table[at++] = MadtTypeLocalApicNmi;
        table[at++] = MadtLocalApicNmiEntryLength;
        table[at++] = 0xFF;              // all processors.
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(at), MpsIntiConforms);
        at += 2;
        table[at] = 1;                   // LINT1.

        WriteHeader(table, "APIC", revision: 1);
        return table;
    }

    private static int WriteOverride(byte[] table, int at, byte sourceBus, byte irq, uint gsi, ushort flags)
    {
        table[at++] = MadtTypeInterruptOverride;
        table[at++] = MadtOverrideEntryLength;
        table[at++] = sourceBus;
        table[at++] = irq;
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(at), gsi);
        at += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(at), flags);
        at += 2;
        return at;
    }

    // =====================================================================
    // DSDT -- the namespace of plans/specs/acpi.md §3, built with AmlWriter
    // and AcpiResources rather than compiled from ASL (decision C3).
    // =====================================================================

    private const byte FirstSlotDevice = Devices.Pci.PciInterruptRouter.FirstSlotDevice;
    private const byte LastSlotDevice = Devices.Pci.PciInterruptRouter.LastSlotDevice;
    private const int UsbDevice = 1;
    private const ushort UsbAddress = 0x000A;
    private const byte UsbPin = 1;

    /// <summary>ACPI §6.5.4: PCI_Config operation regions. Used for the PIIX4 southbridge's own 60h-63h PIRQ routing registers.</summary>
    private const byte PciConfigSpace = 2;

    /// <summary>Field access type Byte (bits 3:0 = 1), NoLock (bit 4 = 0), Preserve (bits 6:5 = 0): ACPI §19.6.51's Field flags byte.</summary>
    private const byte ByteAccNoLockPreserve = 0x01;

    public static byte[] BuildDsdt(uint pci0MemoryBase, uint pci0MemoryLength)
    {
        var aml = new AmlWriter();

        WriteSleepPackage(aml, "\\_S0");
        WriteSleepPackage(aml, "\\_S5");

        aml.BeginScope("\\_PR");
        aml.BeginProcessor("CPU0", procId: 0, pblkAddress: 0, pblkLength: 0);
        aml.EndProcessor();
        aml.EndScope();

        aml.BeginScope("\\_SB");
        WritePci0(aml, pci0MemoryBase, pci0MemoryLength);
        WriteIsaChild(aml, "PWRB", "PNP0C0C", crs: null);
        aml.EndScope();

        byte[] body = aml.ToArray();
        byte[] table = new byte[SdtHeaderLength + body.Length];
        body.CopyTo(table, SdtHeaderLength);
        WriteHeader(table, "DSDT", revision: 1);
        return table;
    }

    private static void WriteSleepPackage(AmlWriter aml, string path)
    {
        aml.BeginName(path);
        aml.BeginPackage(4);
        aml.Integer(0);
        aml.Integer(0);
        aml.Integer(0);
        aml.Integer(0);
        aml.EndPackage();
    }

    private static void WritePci0(AmlWriter aml, uint pci0MemoryBase, uint pci0MemoryLength)
    {
        aml.BeginDevice("PCI0");

        aml.BeginName("_HID");
        aml.Integer(EisaId("PNP0A03"));
        aml.BeginName("_ADR");
        aml.Integer(0);

        aml.BeginName("_CRS");
        aml.WriteBuffer(AcpiResources.Combine(
            AcpiResources.WordBusNumberRange(0, 0),
            AcpiResources.FixedIoRange(0x0000, 0x0CF7, 0x0CF8),
            AcpiResources.FixedIoRange(0x0D00, 0xFFFF, 0xF300),
            AcpiResources.DWordMemoryRange(pci0MemoryBase, pci0MemoryLength),
            AcpiResources.EndTag()));

        WriteIsaBridge(aml);
        WritePrt(aml);

        aml.EndDevice();
    }

    private static void WriteIsaBridge(AmlWriter aml)
    {
        aml.BeginDevice("ISA");
        aml.BeginName("_ADR");
        aml.Integer(0x00010000);   // PIIX4 at 00:01.0 (device 1, function 0): (device << 16) | function.

        WriteIsaChild(aml, "DMAC", "PNP0200", crs: null);
        WriteIsaChild(aml, "PIC", "PNP0000", crs: null);
        WriteIsaChild(aml, "TIMR", "PNP0100", crs: null);

        WriteIsaChild(aml, "RTC", "PNP0B00", crs: AcpiResources.Combine(
            AcpiResources.FixedIoRange(0x70, 0x70, 2),
            AcpiResources.EdgeIrq(1 << 8),
            AcpiResources.EndTag()));

        WriteIsaChild(aml, "KBD", "PNP0303", crs: AcpiResources.Combine(
            AcpiResources.FixedIoRange(0x60, 0x60, 1),
            AcpiResources.FixedIoRange(0x64, 0x64, 1),
            AcpiResources.EdgeIrq(1 << 1),
            AcpiResources.EndTag()));

        WriteIsaChild(aml, "MOU", "PNP0F13", crs: AcpiResources.Combine(
            AcpiResources.EdgeIrq(1 << 12),
            AcpiResources.EndTag()));

        // DMA channel 2 is not encoded as a resource: decision C3's own list
        // of resource descriptors this writer emits names no DMA descriptor
        // tag, and none is independently confirmed here (flagged, decision C6).
        WriteIsaChild(aml, "FDC", "PNP0700", crs: AcpiResources.Combine(
            AcpiResources.FixedIoRange(0x3F0, 0x3F0, 8),
            AcpiResources.EdgeIrq(1 << 6),
            AcpiResources.EndTag()));

        WriteIsaChild(aml, "COM1", "PNP0501", crs: AcpiResources.Combine(
            AcpiResources.FixedIoRange(0x3F8, 0x3F8, 8),
            AcpiResources.EdgeIrq(1 << 4),
            AcpiResources.EndTag()));

        WriteIsaChild(aml, "MATH", "PNP0C04", crs: AcpiResources.Combine(
            AcpiResources.FixedIoRange(0xF0, 0xF0, 1),
            AcpiResources.EdgeIrq(1 << 13),
            AcpiResources.EndTag()));

        // COM2 and LPT are omitted outright: this codebase fits neither a
        // second serial port nor a parallel port device to describe, so
        // "only if fitted" (plans/specs/acpi.md §3) is never true here.
        WriteLinkDevice(aml, "LNKA", uid: 1, configOffset: 0x60);
        WriteLinkDevice(aml, "LNKB", uid: 2, configOffset: 0x61);
        WriteLinkDevice(aml, "LNKC", uid: 3, configOffset: 0x62);
        WriteLinkDevice(aml, "LNKD", uid: 4, configOffset: 0x63);

        aml.EndDevice();
    }

    private static void WriteIsaChild(AmlWriter aml, string name, string hid, byte[]? crs)
    {
        aml.BeginDevice(name);
        aml.BeginName("_HID");
        aml.Integer(EisaId(hid));
        if (crs is not null)
        {
            aml.BeginName("_CRS");
            aml.WriteBuffer(crs);
        }

        aml.EndDevice();
    }

    /// <summary>
    /// One PIRQ link: <c>_PRS</c> is the fixed set of IRQs it could take
    /// (<see cref="Devices.Pci.PciInterruptRouter.AllowedIrqs"/>), and
    /// <c>_CRS</c>/<c>_SRS</c>/<c>_DIS</c> read and write the single Field
    /// unit over the PIIX4's own 60h-63h register through the OperationRegion
    /// this device declares (plans/specs/acpi.md §3).
    /// </summary>
    private static void WriteLinkDevice(AmlWriter aml, string name, byte uid, byte configOffset)
    {
        aml.BeginDevice(name);
        aml.BeginName("_HID");
        aml.Integer(EisaId("PNP0C0F"));
        aml.BeginName("_UID");
        aml.Integer(uid);

        aml.OperationRegion("PIRQ", PciConfigSpace, configOffset, 1);
        aml.BeginField("PIRQ", ByteAccNoLockPreserve);
        aml.FieldUnit("RIRQ", 8);
        aml.EndField();

        aml.BeginName("_PRS");
        aml.WriteBuffer(AcpiResources.Combine(
            AcpiResources.LevelIrq(Devices.Pci.PciInterruptRouter.AllowedIrqs),
            AcpiResources.EndTag()));

        WriteCrsMethod(aml);
        WriteSrsMethod(aml);
        WriteDisMethod(aml);

        aml.EndDevice();
    }

    /// <summary>
    /// _CRS: builds a fresh IRQ resource template naming whichever single
    /// IRQ the 60h-63h register currently holds. Kept minimal (decision
    /// C3): the disable bit (bit 7 of that register) is not reflected here
    /// at all -- a fully disabled link answering an empty template is out
    /// of scope for the DOS/Win9x-era guests this BIOS targets, and no
    /// PIIX4 datasheet-cited behaviour for that case exists to encode
    /// (flagged, decision C6).
    /// </summary>
    private static void WriteCrsMethod(AmlWriter aml)
    {
        byte[] template = AcpiResources.Combine(AcpiResources.LevelIrq(0), AcpiResources.EndTag());

        aml.BeginMethod("_CRS", argCount: 0);

        aml.BeginName("BUF");
        aml.WriteBuffer(template);

        // The IRQ mask word sits one byte into the template, right after its tag byte.
        aml.CreateWordField(() => aml.WritePath("BUF"), byteIndex: 1, newName: "IRQW");

        aml.Store(() => aml.WritePath("RIRQ"), () => aml.Local(0));
        aml.And(() => aml.Local(0), () => aml.Integer(0x0F), () => aml.Local(0));
        aml.ShiftLeft(() => aml.Integer(1), () => aml.Local(0), () => aml.Local(1));
        aml.Store(() => aml.Local(1), () => aml.WritePath("IRQW"));

        aml.Return(() => aml.WritePath("BUF"));

        aml.EndMethod();
    }

    /// <summary>_SRS: the OS hands back a buffer shaped like _CRS's own template; FindSetRightBit turns its one-hot IRQ mask back into an IRQ number to store in the Field.</summary>
    private static void WriteSrsMethod(AmlWriter aml)
    {
        aml.BeginMethod("_SRS", argCount: 1);

        aml.CreateWordField(() => aml.Arg(0), byteIndex: 1, newName: "TMPW");
        aml.FindSetRightBit(() => aml.WritePath("TMPW"), () => aml.Local(0));
        aml.Decrement(() => aml.Local(0));
        aml.Store(() => aml.Local(0), () => aml.WritePath("RIRQ"));

        aml.EndMethod();
    }

    /// <summary>_DIS: sets the register's own disable bit (bit 7) without disturbing the IRQ it still names.</summary>
    private static void WriteDisMethod(AmlWriter aml)
    {
        aml.BeginMethod("_DIS", argCount: 0);
        aml.Or(() => aml.WritePath("RIRQ"), () => aml.Integer(0x80), () => aml.WritePath("RIRQ"));
        aml.EndMethod();
    }

    /// <summary>
    /// One entry per wired (device, pin), not per PIRQ link, exactly as
    /// <c>BiosRom.Mp.cs</c>'s own MP interrupt entries are built from the
    /// same <see cref="Devices.Pci.PciInterruptRouter.Link"/> computation --
    /// so the DSDT's routing and the MP/MADT tables' own routing can never
    /// disagree about which link a (device, pin) combination reaches.
    /// </summary>
    private static void WritePrt(AmlWriter aml)
    {
        aml.BeginName("_PRT");
        aml.BeginPackage(4 * (LastSlotDevice - FirstSlotDevice + 1) + 1);

        for (uint device = FirstSlotDevice; device <= LastSlotDevice; device++)
        {
            for (byte pin = 1; pin <= 4; pin++)
            {
                int link = Devices.Pci.PciInterruptRouter.Link((ushort)(device << 3), pin);
                WritePrtEntry(aml, device, (byte)(pin - 1), LinkPath(link));
            }
        }

        // PIIX4's own integrated USB function, 00:01.2: fixed INTA wired to
        // PIRQD regardless of the slot rotation above, as in the MP table.
        int usbLink = Devices.Pci.PciInterruptRouter.Link(UsbAddress, UsbPin);
        WritePrtEntry(aml, UsbDevice, (byte)(UsbPin - 1), LinkPath(usbLink));

        aml.EndPackage();
    }

    private static string LinkPath(int link) => link switch
    {
        0 => "\\_SB.PCI0.ISA.LNKA",
        1 => "\\_SB.PCI0.ISA.LNKB",
        2 => "\\_SB.PCI0.ISA.LNKC",
        3 => "\\_SB.PCI0.ISA.LNKD",
        _ => throw new ArgumentOutOfRangeException(nameof(link), link, "PciInterruptRouter.Link only ever returns 0-3."),
    };

    private static void WritePrtEntry(AmlWriter aml, uint device, byte zeroBasedPin, string linkPath)
    {
        aml.BeginPackage(4);
        aml.Integer((device << 16) | 0xFFFF);   // Address: high word the PCI device number, low word 0xFFFF ("any function").
        aml.Integer(zeroBasedPin);
        aml.WritePath(linkPath);
        aml.Integer(0);                          // SourceIndex: unused when Source names a device with its own resources (ACPI §6.2.13).
        aml.EndPackage();
    }

    /// <summary>
    /// ACPI §6.1.5's compressed device ID: three letters packed five bits
    /// each into the top of a conceptual 32-bit value, four hex digits
    /// packed four bits each into the bottom, the whole value then stored
    /// byte-swapped relative to that packing. Cross-checked against a known
    /// dump ("PNP0303" packs to 0x41D00303 here and is stored as 0x0303D041).
    /// </summary>
    private static uint EisaId(string id)
    {
        if (id.Length != 7)
        {
            throw new ArgumentException("An ACPI compressed device ID is three letters and four hex digits.", nameof(id));
        }

        uint packed =
            ((uint)(id[0] - 'A' + 1) << 26) |
            ((uint)(id[1] - 'A' + 1) << 21) |
            ((uint)(id[2] - 'A' + 1) << 16) |
            (HexNibble(id[3]) << 12) |
            (HexNibble(id[4]) << 8) |
            (HexNibble(id[5]) << 4) |
            HexNibble(id[6]);

        return BinaryPrimitives.ReverseEndianness(packed);
    }

    private static uint HexNibble(char c)
        => (uint)(c is >= '0' and <= '9' ? c - '0' : char.ToUpperInvariant(c) - 'A' + 10);
}
