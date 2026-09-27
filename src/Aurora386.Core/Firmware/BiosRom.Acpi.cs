namespace Aurora386.Core.Firmware;

/// <summary>
/// What the BIOS needs to know about the machine's own configuration to
/// fill in the ACPI tables (work package P4.5): the same facts a real
/// BIOS would read off its own chipset and RAM size during POST, handed in
/// here for the same reason <see cref="MpSystemInfo"/> is -- <c>BiosRom</c>
/// builds the whole ROM image in C# before the guest runs a single
/// instruction. Null (the default <c>Build</c> parameter) means "no ACPI
/// tables at all": an i430FX board (coordinator decision C1).
/// </summary>
public readonly record struct AcpiSystemInfo(
    bool HasMadt,
    byte CenturyRegister,
    ushort PmBase,
    uint Pci0MemoryBase,
    uint Pci0MemoryLength);

public static partial class BiosRom
{
    /// <summary>
    /// Builds the RSDP, RSDT, FADT, FACS, MADT (when <see
    /// cref="AcpiSystemInfo.HasMadt"/>) and DSDT at and after <see
    /// cref="Entry.AcpiRegion"/>. Only called for the i430TX/PIIX4 board --
    /// see the call site in <c>Machine.BuildBiosImage</c>.
    ///
    /// The DSDT's own byte length never depends on <paramref name="info"/>'s
    /// fields (its dynamic PCI0 memory window is written as a fixed-width
    /// resource-descriptor field, not an AML integer whose encoding would
    /// grow or shrink with the value -- see <c>AmlWriter.Integer</c>'s own
    /// summary), so there is no chicken-and-egg problem building it before
    /// anything else knows where it will sit: nothing inside the DSDT ever
    /// needs to name its own address. The FADT, by contrast, needs the FACS
    /// and DSDT addresses already placed, and the RSDT needs the FADT's and
    /// (when it exists) the MADT's -- so those are built in that dependency
    /// order, each straight into its own place in <paramref name="rom"/>.
    /// </summary>
    private static void WriteAcpiTables(byte[] rom, AcpiSystemInfo info)
    {
        int at = Entry.AcpiRegion;

        // FACS: 16-byte aligned, which Entry.AcpiRegion already is.
        int facsOffset = at;
        byte[] facs = Acpi.AcpiTables.BuildFacs();
        facs.CopyTo(rom, facsOffset);
        at += facs.Length;

        int dsdtOffset = at;
        byte[] dsdt = Acpi.AcpiTables.BuildDsdt(info.Pci0MemoryBase, info.Pci0MemoryLength);
        dsdt.CopyTo(rom, dsdtOffset);
        at += dsdt.Length;

        uint? madtAddress = null;
        if (info.HasMadt)
        {
            int madtOffset = at;

            // The same fixed I/O APIC address every other reference to it in
            // this file uses (WriteIoApicEntry, above): the MADT and the MP
            // table must never be able to name a different one.
            byte[] madt = Acpi.AcpiTables.BuildMadt(IoApicPhysicalBase);
            madt.CopyTo(rom, madtOffset);
            at += madt.Length;
            madtAddress = Base + (uint)madtOffset;
        }

        int fadtOffset = at;
        byte[] fadt = Acpi.AcpiTables.BuildFadt(
            facsAddress: Base + (uint)facsOffset,
            dsdtAddress: Base + (uint)dsdtOffset,
            hasMadt: info.HasMadt,
            pmBase: info.PmBase,
            centuryRegister: info.CenturyRegister);
        fadt.CopyTo(rom, fadtOffset);
        at += fadt.Length;

        int rsdtOffset = at;
        byte[] rsdt = Acpi.AcpiTables.BuildRsdt(Base + (uint)fadtOffset, madtAddress);
        rsdt.CopyTo(rom, rsdtOffset);
        at += rsdt.Length;

        // The RSDP is the one table an OS finds by scanning for its own
        // signature rather than being pointed at, and ACPI 1.0b §5.2.5.1
        // requires it on a 16-byte boundary wherever it lands.
        at = (at + 15) & ~15;
        int rsdpOffset = at;
        byte[] rsdp = Acpi.AcpiTables.BuildRsdp(Base + (uint)rsdtOffset);
        rsdp.CopyTo(rom, rsdpOffset);
        at += rsdp.Length;

        if (at - Entry.AcpiRegion > Entry.AcpiRegionLength)
        {
            throw new InvalidOperationException("ACPI tables overran their reserved ROM region.");
        }
    }
}
