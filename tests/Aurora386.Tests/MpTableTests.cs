using System.Buffers.Binary;
using Aurora386.Core;
using Aurora386.Core.Cpu;
using Aurora386.Core.Devices.Chipset;
using Aurora386.Core.Devices.Pci;
using Aurora386.Core.Firmware;

namespace Aurora386.Tests;

/// <summary>
/// Work package P4.4: the MP 1.4 floating pointer and PCMP configuration
/// table the BIOS places in the F0000h segment next to $PIR (coordinator
/// decision A5), and the IMCR (ports 22h/23h) the floating pointer's own
/// feature byte says is present. Only ever built for a board with both an
/// I/O APIC to describe and a CPU whose silicon has an on-die local APIC
/// (coordinator decision A2).
/// </summary>
public class MpTableTests
{
    private static byte[] ReadRom(Machine machine, int offset, int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = machine.Memory.ReadByte(BiosRom.Base + (uint)(offset + i));
        }

        return bytes;
    }

    private static byte Sum(byte[] bytes) => unchecked((byte)bytes.Sum(value => value));

    // ---- test case 10: the _MP_ floating pointer's own checksum -------------

    [Fact]
    public void FloatingPointerSignatureAndChecksumAreCorrect()
    {
        using var machine = new Machine(chipset: Chipset.I430Tx, initialCpuModel: CpuModel.PentiumMmx);
        machine.RunInstructions(200);

        byte[] pointer = ReadRom(machine, BiosRom.Entry.MpFloatingPointer, 16);

        Assert.Equal("_MP_", System.Text.Encoding.ASCII.GetString(pointer, 0, 4));
        Assert.Equal(0, Sum(pointer));

        uint tableAddress = BinaryPrimitives.ReadUInt32LittleEndian(pointer.AsSpan(4));
        Assert.Equal((uint)(BiosRom.Base + BiosRom.Entry.MpConfigurationTable), tableAddress);
        Assert.Equal(1, pointer[8]);   // length: one 16-byte paragraph
        Assert.Equal(4, pointer[9]);   // MP specification revision 1.4

        // Test case 18: a default configuration always needs two CPUs, so a
        // board with one must describe a real table (feature 1 = 0) rather
        // than pick one of the seven canned layouts.
        Assert.Equal(0, pointer[11]);

        // Test case 12 (restated): feature 2 bit 7 says an IMCR exists and
        // starts in PIC mode.
        Assert.Equal(0x80, pointer[12]);
    }

    // ---- test case 11: the PCMP base table's own checksum --------------------

    [Fact]
    public void ConfigurationTableSignatureEntryCountAndChecksumAreCorrect()
    {
        using var machine = new Machine(chipset: Chipset.I430Tx, initialCpuModel: CpuModel.PentiumMmx);
        machine.RunInstructions(200);

        byte[] header = ReadRom(machine, BiosRom.Entry.MpConfigurationTable, 44);
        Assert.Equal("PCMP", System.Text.Encoding.ASCII.GetString(header, 0, 4));

        ushort baseLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
        Assert.Equal(4, header[6]);   // revision 1.4

        byte[] wholeTable = ReadRom(machine, BiosRom.Entry.MpConfigurationTable, baseLength);
        Assert.Equal(0, Sum(wholeTable));

        // One processor, two buses (ISA, PCI), one I/O APIC, fifteen wired
        // ISA lines (every IRQ but the cascade, 2), one entry per wired
        // (device, pin) on the four virtual slots (4 devices * 4 pins,
        // independent review item 5) plus PIIX4's own USB function, and the
        // two local (LINT0/LINT1) entries -- plans/specs/apic.md §3.
        ushort entryCount = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(34));
        Assert.Equal(1 + 2 + 1 + 15 + PciInterruptRouter.SlotCount * 4 + 1 + 2, entryCount);

        uint lapicAddress = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(36));
        Assert.Equal(0xFEE00000u, lapicAddress);
    }

    /// <summary>
    /// Independent review, item 5: each type-3 PCI entry names one wired
    /// (device, pin), with source bus IRQ = (device &lt;&lt; 2) | (pin-1)
    /// and destination INTIN = 16 + PciInterruptRouter.Link(device, pin) --
    /// decoded here exactly as an OS reading this table would, and checked
    /// against the same Link the running I/O APIC answers to.
    /// </summary>
    [Fact]
    public void PciEntriesNameEveryWiredDeviceAndPinIncludingUsb()
    {
        using var machine = new Machine(chipset: Chipset.I430Tx, initialCpuModel: CpuModel.PentiumMmx);
        machine.RunInstructions(200);

        byte[] header = ReadRom(machine, BiosRom.Entry.MpConfigurationTable, 44);
        ushort baseLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
        byte[] wholeTable = ReadRom(machine, BiosRom.Entry.MpConfigurationTable, baseLength);

        // Walk every entry, collecting the type-3 (I/O interrupt) ones whose
        // source bus is PCI (the bus-1 entries the bus-entry list names "PCI").
        var pciEntries = new List<(byte SourceBusIrq, byte DestinationIntin)>();
        int at = 44;
        while (at < baseLength)
        {
            byte entryType = wholeTable[at];
            int entryLength = entryType switch
            {
                0 => 20,
                1 => 8,
                2 => 8,
                3 => 8,
                4 => 8,
                _ => throw new InvalidOperationException($"Unknown MP entry type {entryType}."),
            };

            if (entryType == 3 && wholeTable[at + 4] == 1)   // type 3, source bus id 1 (PCI)
            {
                pciEntries.Add((wholeTable[at + 5], wholeTable[at + 7]));
            }

            at += entryLength;
        }

        Assert.Equal(PciInterruptRouter.SlotCount * 4 + 1, pciEntries.Count);   // every slot's four pins, plus USB

        for (byte device = PciInterruptRouter.FirstSlotDevice; device <= PciInterruptRouter.LastSlotDevice; device++)
        {
            for (byte pin = 1; pin <= 4; pin++)
            {
                byte expectedSourceIrq = (byte)((device << 2) | (pin - 1));
                int expectedIntin = 16 + PciInterruptRouter.Link((ushort)(device << 3), pin);

                Assert.Contains(pciEntries, entry =>
                    entry.SourceBusIrq == expectedSourceIrq && entry.DestinationIntin == expectedIntin);
            }
        }

        // PIIX4's USB, 00:01.2 (address 000Ah), pin A: device 1, pin 1.
        byte usbSourceIrq = (byte)((1 << 2) | 0);
        int usbIntin = 16 + PciInterruptRouter.Link(0x000A, 1);
        Assert.Contains(pciEntries, entry =>
            entry.SourceBusIrq == usbSourceIrq && entry.DestinationIntin == usbIntin);
    }

    [Fact]
    public void ProcessorEntryNamesTheBootstrapProcessorEnabledWithTheCpuidSignature()
    {
        using var machine = new Machine(chipset: Chipset.I430Tx, initialCpuModel: CpuModel.PentiumMmx);
        machine.RunInstructions(200);

        const int ProcessorEntryOffset = BiosRom.Entry.MpConfigurationTable + 44;
        byte[] entry = ReadRom(machine, ProcessorEntryOffset, 20);

        Assert.Equal(0, entry[0]);   // entry type 0: processor
        Assert.Equal(3, entry[3] & 0x3);   // EN (bit 0) and BP (bit 1) both set
        uint signature = BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(4));
        Assert.Equal(machine.Cpu.State.Gpr[(int)Reg.Edx], signature);
    }

    /// <summary>Test case 16: STARTUP IPIs need an xAPIC, version 1xh; the processor entry reports the same version the MMIO register does.</summary>
    [Fact]
    public void ProcessorEntryReportsAnXApicVersion()
    {
        using var machine = new Machine(chipset: Chipset.I430Tx, initialCpuModel: CpuModel.PentiumMmx);
        machine.RunInstructions(200);

        const int ProcessorEntryOffset = BiosRom.Entry.MpConfigurationTable + 44;
        byte version = machine.Memory.ReadByte(BiosRom.Base + (uint)ProcessorEntryOffset + 2);

        Assert.InRange(version, (byte)0x10, (byte)0x1F);
    }

    // ---- decision A2: no MP tables without both an I/O APIC and an on-die local APIC ----

    [Fact]
    public void NoMpTablesWithoutAnApicCapableCpuEvenOnTheTxBoard()
    {
        // The default constructed model (Intel386) has no on-die APIC at
        // all -- an I/O APIC and an IMCR are still fitted, inert, but the
        // BIOS has no CPU to describe in an MP table.
        using var machine = new Machine(chipset: Chipset.I430Tx);
        machine.RunInstructions(200);

        Assert.NotNull(machine.IoApic);
        Assert.NotNull(machine.Imcr);

        byte[] pointer = ReadRom(machine, BiosRom.Entry.MpFloatingPointer, 4);
        Assert.NotEqual("_MP_", System.Text.Encoding.ASCII.GetString(pointer));
    }

    [Fact]
    public void NoMpTablesOnTheI430FxBoardEvenWithAnApicCapableCpu()
    {
        using var machine = new Machine(chipset: Chipset.I430Fx, initialCpuModel: CpuModel.PentiumMmx);
        machine.RunInstructions(200);

        Assert.Null(machine.IoApic);
        Assert.Null(machine.Imcr);

        byte[] pointer = ReadRom(machine, BiosRom.Entry.MpFloatingPointer, 4);
        Assert.NotEqual("_MP_", System.Text.Encoding.ASCII.GetString(pointer));
    }

    // ---- test case 12: the IMCR itself ---------------------------------------

    [Fact]
    public void ImcrResetsToZeroAndIsReachedThrough22HAnd23H()
    {
        using var machine = new Machine(chipset: Chipset.I430Tx, initialCpuModel: CpuModel.PentiumMmx);
        machine.RunInstructions(200);

        Assert.False(machine.Imcr!.ApicMode);
        Assert.False(machine.Apic.ImcrApicMode);

        machine.Io.WriteByte(0x22, 0x70);
        machine.Io.WriteByte(0x23, 1);
        Assert.True(machine.Imcr.ApicMode);
        Assert.True(machine.Apic.ImcrApicMode);

        // A write to the data port while a different index is selected does
        // not touch the mode: 22h/23h is the PIIX's general configuration
        // window, and 70h is only the one convention every cited source
        // agrees IMCR programming uses.
        machine.Io.WriteByte(0x22, 0x00);
        machine.Io.WriteByte(0x23, 0);
        Assert.True(machine.Imcr.ApicMode);

        machine.Io.WriteByte(0x22, 0x70);
        machine.Io.WriteByte(0x23, 0);
        Assert.False(machine.Imcr.ApicMode);

        // Item 10: 22h is write-only (a Cyrix CPU detection probe must see
        // the same idle bus this port always was), and 23h only answers
        // through index 70h -- every other index reads FFh, as if nothing
        // else behind this window existed, because nothing does.
        Assert.Equal(0xFF, machine.Io.ReadByte(0x22));
        machine.Io.WriteByte(0x22, 0x70);
        machine.Io.WriteByte(0x23, 1);
        Assert.Equal(0xFF, machine.Io.ReadByte(0x22));
        Assert.Equal(1, machine.Io.ReadByte(0x23));
        machine.Io.WriteByte(0x22, 0x71);
        Assert.Equal(0xFF, machine.Io.ReadByte(0x23));
    }

    [Fact]
    public void ImcrIsAbsentOnTheI430FxBoard()
    {
        using var machine = new Machine();
        Assert.Null(machine.Imcr);
        Assert.Equal(0xFF, machine.Io.ReadByte(0x22));   // an empty bus, exactly as before this package
    }

    // ---- E820 (independent review, "Low" item) -------------------------------

    [Fact]
    public void E820ReportsTheIoApicAndLocalApicPagesAsReservedWhenTheMachineHasThem()
    {
        using var machine = new Machine(chipset: Chipset.I430Tx, initialCpuModel: CpuModel.PentiumMmx);

        var ranges = machine.MemoryMap();

        Assert.Contains(ranges, r => r.Start == 0xFEC00000 && r.Length == 0x1000 && r.Type == 2);
        Assert.Contains(ranges, r => r.Start == 0xFEE00000 && r.Length == 0x1000 && r.Type == 2);
    }

    [Fact]
    public void E820OmitsBothApicPagesOnAMachineWithNeither()
    {
        using var machine = new Machine();   // i430FX, default Intel386: no I/O APIC, no local APIC

        var ranges = machine.MemoryMap();

        Assert.DoesNotContain(ranges, r => r.Start == 0xFEC00000);
        Assert.DoesNotContain(ranges, r => r.Start == 0xFEE00000);
    }

    [Fact]
    public void E820ReportsOnlyTheLocalApicPageOnAnI430FxBoardWithAnApicCapableCpu()
    {
        using var machine = new Machine(chipset: Chipset.I430Fx, initialCpuModel: CpuModel.PentiumMmx);

        var ranges = machine.MemoryMap();

        Assert.DoesNotContain(ranges, r => r.Start == 0xFEC00000);   // no I/O APIC on this board
        Assert.Contains(ranges, r => r.Start == 0xFEE00000 && r.Length == 0x1000 && r.Type == 2);
    }
}
