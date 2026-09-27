using System.Buffers.Binary;
using System.Text;
using Aurora386.Core;
using Aurora386.Core.Cpu;
using Aurora386.Core.Devices.Chipset;
using Aurora386.Core.Devices.Pci;
using Aurora386.Core.Firmware;

namespace Aurora386.Tests;

/// <summary>
/// Work package P4.5's static ACPI tables: the RSDP, RSDT, FADT, FACS and
/// MADT built as fixed-format bytes, and the DSDT's AML namespace decoded
/// with <see cref="AcpiAmlReader"/> rather than trusted from the writer's
/// own logic (plans/specs/acpi.md's own test cases 1-8 and 14-20).
///
/// Every test builds a real <see cref="Machine"/> and reads the tables the
/// way an operating system does: scanning F0000h-FFFFFh for the RSDP's own
/// signature, then following its own pointers -- never a fixed offset a
/// test happens to know from the builder's internals.
/// </summary>
public class AcpiTableTests
{
    private const int RsdpLength = 20;

    private static Machine NewTxMachine(uint ramSize = 16u * 1024 * 1024, CpuModel model = CpuModel.PentiumMmx)
    {
        var machine = new Machine(ramSize, chipset: Chipset.I430Tx, initialCpuModel: model);
        machine.RunInstructions(200);
        return machine;
    }

    private static byte[] ReadRom(Machine machine, uint physicalAddress, int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = machine.Memory.ReadByte(physicalAddress + (uint)i);
        }

        return bytes;
    }

    private static byte Sum(byte[] bytes) => unchecked((byte)bytes.Sum(value => value));

    /// <summary>Finds the RSDP the way a real OS does (ACPI 1.0b §5.2.5.1): scanning the F0000h segment in 16-byte steps for its own signature.</summary>
    private static uint FindRsdp(Machine machine)
    {
        for (uint offset = BiosRom.Entry.AcpiRegion;
             offset < BiosRom.Entry.AcpiRegion + BiosRom.Entry.AcpiRegionLength;
             offset += 16)
        {
            uint address = BiosRom.Base + offset;
            if (Encoding.ASCII.GetString(ReadRom(machine, address, 8)) == "RSD PTR ")
            {
                return address;
            }
        }

        throw new InvalidOperationException("No RSDP signature found in the ACPI region.");
    }

    private static byte[] ReadTable(Machine machine, uint address)
    {
        byte[] header = ReadRom(machine, address, 8);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        return ReadRom(machine, address, (int)length);
    }

    private static byte[] ReadRsdt(Machine machine)
    {
        byte[] rsdp = ReadRom(machine, FindRsdp(machine), RsdpLength);
        uint rsdtAddress = BinaryPrimitives.ReadUInt32LittleEndian(rsdp.AsSpan(16));
        return ReadTable(machine, rsdtAddress);
    }

    private static byte[] ReadFadt(Machine machine)
    {
        byte[] rsdt = ReadRsdt(machine);
        uint fadtAddress = BinaryPrimitives.ReadUInt32LittleEndian(rsdt.AsSpan(36));
        return ReadTable(machine, fadtAddress);
    }

    private static byte[]? ReadMadtOrNull(Machine machine)
    {
        byte[] rsdt = ReadRsdt(machine);
        if (rsdt.Length < 44) return null;
        uint madtAddress = BinaryPrimitives.ReadUInt32LittleEndian(rsdt.AsSpan(40));
        return ReadTable(machine, madtAddress);
    }

    private static AmlNode ReadDsdtNamespace(Machine machine)
    {
        byte[] fadt = ReadFadt(machine);
        uint dsdtAddress = BinaryPrimitives.ReadUInt32LittleEndian(fadt.AsSpan(40));
        byte[] dsdt = ReadTable(machine, dsdtAddress);
        return AcpiAmlReader.ParseDefinitionBlock(dsdt[36..]);
    }

    // ---- test case 1: the RSDP's own checksum and revision -------------------

    [Fact]
    public void RsdpChecksumIsCorrectAndRevisionIsZero()
    {
        using Machine machine = NewTxMachine();
        byte[] rsdp = ReadRom(machine, FindRsdp(machine), RsdpLength);

        Assert.Equal(0, Sum(rsdp));
        Assert.Equal(0, rsdp[15]);
    }

    // ---- test case 2: the RSDT's own checksum ---------------------------------

    [Fact]
    public void RsdtChecksumIsCorrect()
    {
        using Machine machine = NewTxMachine();
        byte[] rsdt = ReadRsdt(machine);

        Assert.Equal("RSDT", Encoding.ASCII.GetString(rsdt, 0, 4));
        Assert.Equal(0, Sum(rsdt));
    }

    // ---- test cases 3-6: the FADT's length, revision, SCI/SMI fields, PM
    // block addresses and lengths, and its flags -------------------------------

    [Fact]
    public void FadtLengthAndRevisionAreCorrect()
    {
        using Machine machine = NewTxMachine();
        byte[] fadt = ReadFadt(machine);

        Assert.Equal("FACP", Encoding.ASCII.GetString(fadt, 0, 4));
        Assert.Equal(116, fadt.Length);
        Assert.Equal(1, fadt[8]);
        Assert.Equal(0, Sum(fadt));
    }

    [Fact]
    public void FadtSciAndSmiFieldsAreCorrect()
    {
        using Machine machine = NewTxMachine();
        byte[] fadt = ReadFadt(machine);

        Assert.Equal(9, BinaryPrimitives.ReadUInt16LittleEndian(fadt.AsSpan(46)));
        Assert.Equal(0xB2u, BinaryPrimitives.ReadUInt32LittleEndian(fadt.AsSpan(48)));
        Assert.Equal(0xF1, fadt[52]);
        Assert.Equal(0xF0, fadt[53]);
    }

    [Fact]
    public void FadtPmBlockAddressesAndLengthsAreCorrect()
    {
        using Machine machine = NewTxMachine();
        byte[] fadt = ReadFadt(machine);

        Assert.Equal(0xB000u, BinaryPrimitives.ReadUInt32LittleEndian(fadt.AsSpan(56)));   // PM1a_EVT_BLK.
        Assert.Equal(0xB004u, BinaryPrimitives.ReadUInt32LittleEndian(fadt.AsSpan(64)));   // PM1a_CNT_BLK.
        Assert.Equal(0xB008u, BinaryPrimitives.ReadUInt32LittleEndian(fadt.AsSpan(76)));   // PM_TMR_BLK.
        Assert.Equal(0xB00Cu, BinaryPrimitives.ReadUInt32LittleEndian(fadt.AsSpan(80)));   // GPE0_BLK.

        Assert.Equal(4, fadt[88]);   // PM1_EVT_LEN.
        Assert.Equal(2, fadt[89]);   // PM1_CNT_LEN.
        Assert.Equal(4, fadt[91]);   // PM_TM_LEN.
        Assert.Equal(4, fadt[92]);   // GPE0_BLK_LEN.

        // Everything B, PM2 and GPE1 is 0 (decision C2).
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(fadt.AsSpan(60)));    // PM1b_EVT_BLK.
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(fadt.AsSpan(68)));    // PM1b_CNT_BLK.
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(fadt.AsSpan(72)));    // PM2_CNT_BLK.
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(fadt.AsSpan(84)));    // GPE1_BLK.
    }

    [Fact]
    public void FadtFlagsAreCorrect()
    {
        using Machine machine = NewTxMachine();
        byte[] fadt = ReadFadt(machine);

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(fadt.AsSpan(112));
        Assert.Equal(1u, flags & 1);          // WBINVD.
        Assert.Equal(1u, (flags >> 2) & 1);   // PROC_C1.
        Assert.Equal(0u, (flags >> 4) & 1);   // PWR_BUTTON.
        Assert.Equal(1u, (flags >> 5) & 1);   // SLP_BUTTON.
        Assert.Equal(0u, (flags >> 8) & 1);   // TMR_VAL_EXT.

        Assert.Equal(1, fadt[44]);      // INT_MODEL: multiple APIC, since PentiumMmx has an on-die local APIC.
        Assert.Equal(0x32, fadt[108]);  // CENTURY: Cmos's own register 0x32.
        Assert.Equal(101, BinaryPrimitives.ReadUInt16LittleEndian(fadt.AsSpan(96)));    // P_LVL2_LAT: C2 unsupported.
        Assert.Equal(1001, BinaryPrimitives.ReadUInt16LittleEndian(fadt.AsSpan(98)));   // P_LVL3_LAT: C3 unsupported.
    }

    // ---- test case 7: the FACS ------------------------------------------------

    [Fact]
    public void FacsIs64BytesAnd16ByteAligned()
    {
        using Machine machine = NewTxMachine();
        byte[] fadt = ReadFadt(machine);
        uint facsAddress = BinaryPrimitives.ReadUInt32LittleEndian(fadt.AsSpan(36));
        byte[] facs = ReadRom(machine, facsAddress, 64);

        Assert.Equal("FACS", Encoding.ASCII.GetString(facs, 0, 4));
        Assert.Equal(64u, BinaryPrimitives.ReadUInt32LittleEndian(facs.AsSpan(4)));
        Assert.Equal(0u, facsAddress % 16);
    }

    // ---- test case 8: the sleep packages --------------------------------------

    [Fact]
    public void SleepPackagesDecodeAsZeroAndOnlyS0AndS5Exist()
    {
        using Machine machine = NewTxMachine();
        AmlNode dsdt = ReadDsdtNamespace(machine);

        AmlNode s0 = Assert.Single(dsdt.Children, c => c.Name == "_S0");
        AmlNode s5 = Assert.Single(dsdt.Children, c => c.Name == "_S5");
        Assert.Equal(new List<object> { 0UL, 0UL, 0UL, 0UL }, s0.Value);
        Assert.Equal(new List<object> { 0UL, 0UL, 0UL, 0UL }, s5.Value);

        Assert.DoesNotContain(dsdt.Children, c => c.Name is "_S1" or "_S3" or "_S4");
    }

    // ---- test case 14: the MADT overrides -------------------------------------

    [Fact]
    public void MadtOverridesIrq0ToGsi2ConformingAndIrq9ToGsi9LevelActiveLow()
    {
        using Machine machine = NewTxMachine();
        byte[]? madt = ReadMadtOrNull(machine);
        Assert.NotNull(madt);
        Assert.Equal("APIC", Encoding.ASCII.GetString(madt, 0, 4));
        Assert.Equal(0, Sum(madt));

        const int HeaderLength = 44;             // 36-byte SDT header + LocalApicAddress(4) + Flags(4).
        const int LocalApicEntryLength = 8;
        const int IoApicEntryLength = 12;
        const int OverrideEntryLength = 10;

        int override0 = HeaderLength + LocalApicEntryLength + IoApicEntryLength;
        Assert.Equal(2, madt[override0]);            // type: Interrupt Source Override.
        Assert.Equal(0, madt[override0 + 2]);         // source bus: ISA.
        Assert.Equal(0, madt[override0 + 3]);         // source IRQ 0.
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(madt.AsSpan(override0 + 4)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(madt.AsSpan(override0 + 8)));   // conforming.

        int override1 = override0 + OverrideEntryLength;
        Assert.Equal(2, madt[override1]);
        Assert.Equal(9, madt[override1 + 3]);         // source IRQ 9.
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(madt.AsSpan(override1 + 4)));
        Assert.Equal(0b1111, BinaryPrimitives.ReadUInt16LittleEndian(madt.AsSpan(override1 + 8)));   // level, active low.
    }

    // ---- test case 15: the MADT is absent without a local APIC ---------------

    [Fact]
    public void MadtAbsentWithoutApicWhileOtherTablesStayConsistent()
    {
        using Machine machine = NewTxMachine(model: CpuModel.Pentium);   // P54C: no on-die local APIC.
        Assert.NotNull(machine.IoApic);
        Assert.False(machine.Cpu.State.Features.Has(CpuFeature.Apic));

        Assert.Null(ReadMadtOrNull(machine));

        byte[] fadt = ReadFadt(machine);
        Assert.Equal(0, fadt[44]);   // INT_MODEL: dual PIC.

        byte[] rsdt = ReadRsdt(machine);
        Assert.Equal(40, rsdt.Length);   // header (36) + one pointer: the FADT alone.
        Assert.Equal(0, Sum(rsdt));
    }

    // ---- a CPU model switch refreshes the ACPI region the same way it
    // already refreshes the MP tables (Machine.RefreshBiosMpTables) --------

    [Fact]
    public void CpuModelSwitchRefreshesMadtIntModelAndRsdtTheSameWayItRefreshesMpTables()
    {
        using Machine machine = NewTxMachine(model: CpuModel.Pentium);   // P54C: no local APIC yet.
        Assert.Null(ReadMadtOrNull(machine));
        Assert.Equal(0, ReadFadt(machine)[44]);
        Assert.Equal(40, ReadRsdt(machine).Length);

        machine.CpuModel = CpuModel.PentiumMmx;   // gains an on-die local APIC.
        Assert.NotNull(ReadMadtOrNull(machine));
        Assert.Equal(1, ReadFadt(machine)[44]);
        Assert.Equal(44, ReadRsdt(machine).Length);

        machine.CpuModel = CpuModel.Pentium;   // and loses it again.
        Assert.Null(ReadMadtOrNull(machine));
        Assert.Equal(0, ReadFadt(machine)[44]);
        Assert.Equal(40, ReadRsdt(machine).Length);
    }

    // ---- test case 16: _PRT and the link devices' _CRS/_SRS/_DIS -------------

    [Fact]
    public void PrtMatchesPciInterruptRouterLinkForEveryWiredDeviceAndPin()
    {
        using Machine machine = NewTxMachine();
        AmlNode dsdt = ReadDsdtNamespace(machine);
        AmlNode pci0 = dsdt.Find("_SB.PCI0")!;
        var prt = (List<object>)pci0.Find("_PRT")!.Value!;

        Assert.Equal(17, prt.Count);   // 4 devices x 4 pins, plus PIIX4's own USB function.

        int index = 0;
        for (uint device = 2; device <= 5; device++)
        {
            for (byte pin = 1; pin <= 4; pin++)
            {
                var entry = (List<object>)prt[index++];
                Assert.Equal((device << 16) | 0xFFFFu, (ulong)entry[0]);
                Assert.Equal((ulong)(pin - 1), (ulong)entry[1]);
                Assert.Equal(0ul, (ulong)entry[3]);

                int expectedLink = PciInterruptRouter.Link((ushort)(device << 3), pin);
                string expectedName = LinkDeviceName(expectedLink);
                Assert.EndsWith(expectedName, ((AmlNameReference)entry[2]).Path);
            }
        }

        var usbEntry = (List<object>)prt[16];
        Assert.Equal(0x1FFFFul, (ulong)usbEntry[0]);   // device 1 (PIIX4's own function), any function.
        Assert.Equal(0ul, (ulong)usbEntry[1]);          // INTA.
        int usbLink = PciInterruptRouter.Link(0x000A, 1);
        Assert.EndsWith(LinkDeviceName(usbLink), ((AmlNameReference)usbEntry[2]).Path);
    }

    private static string LinkDeviceName(int link) => link switch
    {
        0 => "LNKA",
        1 => "LNKB",
        2 => "LNKC",
        3 => "LNKD",
        _ => throw new ArgumentOutOfRangeException(nameof(link)),
    };

    [Fact]
    public void LinkCrsSrsAndDisRoundTripThroughAConfigRegisterByte()
    {
        using Machine machine = NewTxMachine();
        AmlNode dsdt = ReadDsdtNamespace(machine);
        AmlNode linkA = dsdt.Find("_SB.PCI0.ISA.LNKA")!;
        byte[] crsBody = linkA.Children.Single(c => c.Name == "_CRS" && c.Kind == "Method").MethodBody!;
        byte[] srsBody = linkA.Children.Single(c => c.Name == "_SRS" && c.Kind == "Method").MethodBody!;
        byte[] disBody = linkA.Children.Single(c => c.Name == "_DIS" && c.Kind == "Method").MethodBody!;

        byte register = 10;   // IRQ 10, as though the BIOS's own default routing had assigned it.

        byte[] crsBuffer = (byte[])new AmlMethodExecutor(null, () => register, value => register = value)
            .Execute(crsBody)!;
        int mask = crsBuffer[1] | (crsBuffer[2] << 8);
        Assert.Equal(1 << 10, mask);

        // _SRS: the OS hands back a buffer of the same shape, asking for IRQ 11 instead.
        const int RequestedIrq11Mask = 1 << 11;
        byte[] srsRequest = (byte[])crsBuffer.Clone();
        srsRequest[1] = unchecked((byte)RequestedIrq11Mask);
        srsRequest[2] = (byte)(RequestedIrq11Mask >> 8);
        new AmlMethodExecutor(srsRequest, () => register, value => register = value).Execute(srsBody);
        Assert.Equal(11, register & 0x0F);

        // _DIS: only the disable bit changes.
        new AmlMethodExecutor(null, () => register, value => register = value).Execute(disBody);
        Assert.Equal(0x80 | 11, register);
    }

    // ---- test case 17: the PCI0 memory window ---------------------------------

    [Theory]
    [InlineData(16u * 1024 * 1024)]
    [InlineData(512u * 1024 * 1024)]
    public void Pci0MemoryWindowTracksRamSizeAndNeverOverlapsApicPages(uint ramSize)
    {
        using Machine machine = NewTxMachine(ramSize);
        AmlNode dsdt = ReadDsdtNamespace(machine);
        AmlNode pci0 = dsdt.Find("_SB.PCI0")!;
        byte[] crs = (byte[])pci0.Find("_CRS")!.Value!;

        // Word Bus Number Range (16 bytes) then two I/O Port descriptors (8
        // bytes each) come before the DWord Address Space Descriptor.
        const int MemoryDescriptorOffset = 16 + 8 + 8;
        Assert.Equal(0x87, crs[MemoryDescriptorOffset]);

        uint minimum = BinaryPrimitives.ReadUInt32LittleEndian(crs.AsSpan(MemoryDescriptorOffset + 10));
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(crs.AsSpan(MemoryDescriptorOffset + 22));

        Assert.Equal(machine.LinearFrameBufferBase, minimum);
        Assert.Equal(Machine.TrioMmioOffset + 0x10000, length);

        uint maximum = minimum + length - 1;
        Assert.True(maximum < Machine.IoApicPhysicalBase, "the PCI0 memory window must stay below the I/O APIC's fixed page");
    }

    // ---- test case 18: E820 is unchanged --------------------------------------

    [Fact]
    public void E820GainsOnlyTheApicWindowsAlreadyKnownFromWorkPackageP44()
    {
        using Machine fx = new Machine(chipset: Chipset.I430Fx, initialCpuModel: CpuModel.PentiumMmx);
        using Machine tx = new Machine(chipset: Chipset.I430Tx, initialCpuModel: CpuModel.PentiumMmx);

        var fxMap = fx.MemoryMap();
        var txMap = tx.MemoryMap();

        Assert.Contains(txMap, entry => entry.Start == 0xF0000 && entry.Length == 0x10000 && entry.Type == 2);

        // The only difference between the two boards' maps is the I/O
        // APIC's own fixed window (work package P4.4): ACPI itself adds no
        // E820 entry, since its tables sit inside the existing ROM reservation.
        Assert.Equal(fxMap.Count + 1, txMap.Count);
    }

    // ---- ACPI tables absent on the i430FX board -------------------------------

    [Fact]
    public void NoAcpiTablesOnTheI430FxBoard()
    {
        using var machine = new Machine(chipset: Chipset.I430Fx, initialCpuModel: CpuModel.PentiumMmx);
        machine.RunInstructions(200);

        for (uint offset = BiosRom.Entry.AcpiRegion;
             offset < BiosRom.Entry.AcpiRegion + BiosRom.Entry.AcpiRegionLength;
             offset += 16)
        {
            string signature = Encoding.ASCII.GetString(ReadRom(machine, BiosRom.Base + offset, 8));
            Assert.NotEqual("RSD PTR ", signature);
        }
    }

    // ---- test case 19: the namespace matches the intended device list --------

    [Fact]
    public void NamespaceMatchesTheIntendedDeviceList()
    {
        using Machine machine = NewTxMachine();
        AmlNode dsdt = ReadDsdtNamespace(machine);

        AmlNode isa = dsdt.Find("_SB.PCI0.ISA")!;
        string[] expectedChildren =
        [
            "DMAC", "PIC", "TIMR", "RTC", "KBD", "MOU", "FDC", "COM1", "MATH",
            "LNKA", "LNKB", "LNKC", "LNKD",
        ];
        string[] actualDevices = isa.Children
            .Where(child => child.Kind == "Device")
            .Select(child => child.Name)
            .OrderBy(name => name)
            .ToArray();
        Assert.Equal(expectedChildren.OrderBy(name => name), actualDevices);

        AmlNode pwrb = dsdt.Find("_SB.PWRB")!;
        Assert.Equal("Device", pwrb.Kind);

        // COM2 and LPT are never present: this codebase fits neither.
        Assert.DoesNotContain(isa.Children, child => child.Name is "COM2" or "LPT1");
    }

    // ---- test case 20: \_S0 and \_PR.CPU0 are well formed ---------------------

    [Fact]
    public void S0AndProcessorObjectAreWellFormed()
    {
        using Machine machine = NewTxMachine();
        AmlNode dsdt = ReadDsdtNamespace(machine);

        AmlNode s0 = dsdt.Children.Single(c => c.Name == "_S0");
        Assert.Equal(new List<object> { 0UL, 0UL, 0UL, 0UL }, s0.Value);

        AmlNode processor = dsdt.Find("_PR.CPU0")!;
        Assert.Equal("Processor", processor.Kind);
    }
}
