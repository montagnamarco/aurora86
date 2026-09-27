using Aurora386.Core;
using Aurora386.Core.Devices.Chipset;
using Aurora386.Core.Devices.Storage;

namespace Aurora386.Tests;

/// <summary>
/// The PIIX4 SFF-8038i window, driven from its PCI BAR and ATA task file as
/// ESDI_506.PDR and IDE drivers do rather than by calling the disk directly.
/// </summary>
public class BusMasterIdeTests
{
    private const ushort Bmiba = 0xC000;
    private const uint DescriptorTable = 0x1000;
    private const uint Buffer = 0x2000;

    private static (Machine Machine, DiskImage Disk) WithDisk()
    {
        var machine = new Machine(chipset: Chipset.I430Tx);
        machine.RunInstructions(200);
        DiskImage disk = DiskImage.CreateInMemory(0x80, 20, 16, 63);
        machine.AttachDisk(disk);
        machine.Pci.WriteConfiguration(0x0009, 4, 2, 0x0005);
        return (machine, disk);
    }

    private static byte[] Sector(byte first)
    {
        var bytes = new byte[DiskImage.SectorSize];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(first + i);
        }

        return bytes;
    }

    private static void WriteDescriptor(Machine machine, uint address, uint byteCount, bool end)
    {
        WriteDescriptorAt(machine, DescriptorTable, address, byteCount, end);
    }

    private static void WriteDescriptorAt(Machine machine, uint table, uint address, uint byteCount, bool end)
    {
        machine.Memory.WriteByte(table + 0, (byte)address);
        machine.Memory.WriteByte(table + 1, (byte)(address >> 8));
        machine.Memory.WriteByte(table + 2, (byte)(address >> 16));
        machine.Memory.WriteByte(table + 3, (byte)(address >> 24));
        uint countAndEnd = byteCount | (end ? 0x8000_0000u : 0);
        machine.Memory.WriteByte(table + 4, (byte)countAndEnd);
        machine.Memory.WriteByte(table + 5, (byte)(countAndEnd >> 8));
        machine.Memory.WriteByte(table + 6, (byte)(countAndEnd >> 16));
        machine.Memory.WriteByte(table + 7, (byte)(countAndEnd >> 24));
    }

    private static void SetLba(Machine machine, long lba, int sectors)
    {
        machine.Io.WriteByte(0x1F2, (byte)sectors);
        machine.Io.WriteByte(0x1F3, (byte)lba);
        machine.Io.WriteByte(0x1F4, (byte)(lba >> 8));
        machine.Io.WriteByte(0x1F5, (byte)(lba >> 16));
        machine.Io.WriteByte(0x1F6, (byte)(0xE0 | ((lba >> 24) & 0x0F)));
    }

    private static void SetDescriptorTable(Machine machine)
    {
        machine.Io.WriteDword(Bmiba + 4, DescriptorTable);
    }

    [Fact]
    public void ReadDmaWritesTheDiskSectorBeforeLatchingItsInterrupt()
    {
        (Machine machine, DiskImage disk) = WithDisk();
        using (machine)
        {
            byte[] expected = Sector(0x21);
            disk.WriteLinear(7, 1, expected);
            WriteDescriptor(machine, Buffer, DiskImage.SectorSize, end: true);
            SetDescriptorTable(machine);
            SetLba(machine, 7, 1);

            machine.Io.WriteByte(0x1F7, 0xC8);
            machine.Io.WriteByte(Bmiba, 0x09);       // bit 3 set: PCI writes memory

            byte[] actual = Enumerable.Range(0, DiskImage.SectorSize)
                .Select(i => machine.Memory.ReadByte(Buffer + (uint)i)).ToArray();
            Assert.Equal(expected, actual);
            Assert.Equal(0x04, machine.Io.ReadByte(Bmiba + 2) & 0x05);
            machine.Io.WriteByte(Bmiba + 2, 0x04);
            Assert.Equal(0, machine.Io.ReadByte(Bmiba + 2) & 0x04);
        }
    }

    [Fact]
    public void WriteDmaReadsMemoryWhenBitThreeIsClear()
    {
        (Machine machine, DiskImage disk) = WithDisk();
        using (machine)
        {
            byte[] expected = Sector(0x52);
            for (int i = 0; i < expected.Length; i++)
            {
                machine.Memory.WriteByte(Buffer + (uint)i, expected[i]);
            }

            WriteDescriptor(machine, Buffer, DiskImage.SectorSize, end: true);
            SetDescriptorTable(machine);
            SetLba(machine, 9, 1);
            machine.Io.WriteByte(0x1F7, 0xCA);
            machine.Io.WriteByte(Bmiba, 0x01);       // bit 3 clear: PCI reads memory

            var actual = new byte[DiskImage.SectorSize];
            Assert.True(disk.ReadLinear(9, 1, actual));
            Assert.Equal(expected, actual);
            Assert.Equal(0x04, machine.Io.ReadByte(Bmiba + 2) & 0x04);
        }
    }

    [Fact]
    public void ALongPrdTableLeavesActiveSetUntilTheDriverStopsIt()
    {
        (Machine machine, DiskImage disk) = WithDisk();
        using (machine)
        {
            disk.WriteLinear(2, 1, Sector(0x19));
            WriteDescriptor(machine, Buffer, DiskImage.SectorSize * 2, end: true);
            SetDescriptorTable(machine);
            SetLba(machine, 2, 1);
            machine.Io.WriteByte(0x1F7, 0xC8);
            machine.Io.WriteByte(Bmiba, 0x09);

            Assert.Equal(0x05, machine.Io.ReadByte(Bmiba + 2) & 0x05);
            machine.Io.WriteByte(Bmiba, 0x00);
            Assert.Equal(0, machine.Io.ReadByte(Bmiba + 2) & 0x01);
        }
    }

    [Fact]
    public void StartBeforeTheRetryReadCommandRunsWhenTheAtaRequestArrives()
    {
        (Machine machine, DiskImage disk) = WithDisk();
        using (machine)
        {
            byte[] expected = Sector(0x71);
            disk.WriteLinear(3, 1, expected);
            WriteDescriptor(machine, Buffer, DiskImage.SectorSize, end: true);
            SetDescriptorTable(machine);

            // The SFF sequence normally puts the ATA command first, but the
            // two latches are independent. A driver that starts first must
            // still receive a READ DMA RETRY (C9h) when it arms the request.
            machine.Io.WriteByte(Bmiba, 0x09);
            Assert.Equal(0x01, machine.Io.ReadByte(Bmiba + 2) & 0x01);
            SetLba(machine, 3, 1);
            machine.Io.WriteByte(0x1F7, 0xC9);

            Assert.Equal(expected[0], machine.Memory.ReadByte(Buffer));
            Assert.Equal(0x04, machine.Io.ReadByte(Bmiba + 2) & 0x05);
        }
    }

    [Fact]
    public void MultiplePrdsAndAZeroCountPrdMoveTheirSpecifiedBytes()
    {
        (Machine machine, DiskImage disk) = WithDisk();
        using (machine)
        {
            byte[] expected = Sector(0x64);
            disk.WriteLinear(4, 1, expected);
            WriteDescriptorAt(machine, DescriptorTable, Buffer, 256, end: false);
            WriteDescriptorAt(machine, DescriptorTable + 8, Buffer + 256, 256, end: true);
            SetDescriptorTable(machine);
            SetLba(machine, 4, 1);
            machine.Io.WriteByte(0x1F7, 0xC8);
            machine.Io.WriteByte(Bmiba, 0x09);
            Assert.Equal(expected, Enumerable.Range(0, DiskImage.SectorSize)
                .Select(i => machine.Memory.ReadByte(Buffer + (uint)i)).ToArray());

            machine.BusMasterIde!.Reset();
            WriteDescriptor(machine, 0, 0, end: true); // zero means a 64 KB region
            SetDescriptorTable(machine);
            SetLba(machine, 4, 1);
            machine.Io.WriteByte(0x1F7, 0xC8);
            machine.Io.WriteByte(Bmiba, 0x09);
            Assert.Equal(expected[0], machine.Memory.ReadByte(0));
            Assert.Equal(0x05, machine.Io.ReadByte(Bmiba + 2) & 0x05);
        }
    }

    [Fact]
    public void CapturingALongCompletedTablePreservesItsActiveAndInterruptLatches()
    {
        (Machine machine, DiskImage disk) = WithDisk();
        using (machine)
        {
            disk.WriteLinear(5, 1, Sector(0x3A));
            WriteDescriptor(machine, Buffer, DiskImage.SectorSize * 2, end: true);
            SetDescriptorTable(machine);
            SetLba(machine, 5, 1);
            machine.Io.WriteByte(0x1F7, 0xC8);
            machine.Io.WriteByte(Bmiba, 0x09);
            byte[] state = machine.BusMasterIde!.Capture();

            machine.BusMasterIde.Reset();
            machine.BusMasterIde.Restore(state);

            Assert.Equal(0x05, machine.Io.ReadByte(Bmiba + 2) & 0x05);
        }
    }

    [Fact]
    public void ResetDropsAPendingAtaCommandAndSnapshotRestoresItWithoutStartingIt()
    {
        (Machine machine, DiskImage disk) = WithDisk();
        using (machine)
        {
            byte[] expected = Sector(0x44);
            disk.WriteLinear(6, 1, expected);
            WriteDescriptor(machine, Buffer, DiskImage.SectorSize, end: true);
            SetDescriptorTable(machine);
            SetLba(machine, 6, 1);
            machine.Io.WriteByte(0x1F7, 0xC8);
            byte[] pending = machine.BusMasterIde!.Capture();

            machine.BusMasterIde.Reset();
            machine.Io.WriteByte(Bmiba, 0x09);
            Assert.Equal(0, machine.Memory.ReadByte(Buffer));

            machine.BusMasterIde.Restore(pending);
            Assert.Equal(0, machine.Memory.ReadByte(Buffer));
            machine.Io.WriteByte(Bmiba, 0x09);
            Assert.Equal(expected[0], machine.Memory.ReadByte(Buffer));
        }
    }

    [Fact]
    public void SnapshotWithPendingDmaRefusesATargetWhoseDiskWasRemovedBeforeRamChanges()
    {
        (Machine source, _) = WithDisk();
        using (source)
        {
            SetLba(source, 1, 1);
            source.Io.WriteByte(0x1F7, 0xC8);
            MachineSnapshot snapshot = MachineSnapshot.Capture(source);

            using var target = new Machine(chipset: Chipset.I430Tx);
            target.RunInstructions(200);
            target.Memory.WriteByte(0x5000, 0xA5);

            Assert.Throws<InvalidDataException>(() => snapshot.RestoreTo(target));
            Assert.Equal(0xA5, target.Memory.ReadByte(0x5000));
        }
    }

    [Fact]
    public void AShortTableStopsQuietlyAndMalformedPrdsReportAnError()
    {
        (Machine machine, DiskImage disk) = WithDisk();
        using (machine)
        {
            disk.WriteLinear(2, 1, Sector(0x19));
            WriteDescriptor(machine, Buffer, DiskImage.SectorSize / 2, end: true);
            SetDescriptorTable(machine);
            SetLba(machine, 2, 1);
            machine.Io.WriteByte(0x1F7, 0xC8);
            machine.Io.WriteByte(Bmiba, 0x09);

            Assert.Equal(0x00, machine.Io.ReadByte(Bmiba + 2) & 0x07);
            Assert.Equal(0x19, machine.Memory.ReadByte(Buffer));

            machine.BusMasterIde!.Reset();
            WriteDescriptor(machine, 0x0000_FF00, DiskImage.SectorSize, end: true);
            SetDescriptorTable(machine);
            SetLba(machine, 2, 1);
            machine.Io.WriteByte(0x1F7, 0xC8);
            machine.Io.WriteByte(Bmiba, 0x09);
            Assert.Equal(0x02, machine.Io.ReadByte(Bmiba + 2) & 0x07);

            machine.BusMasterIde.Reset();
            WriteDescriptorAt(machine, 0x1FF8, Buffer, 256, end: false);
            machine.Io.WriteDword(Bmiba + 4, 0x1FF8);
            SetLba(machine, 2, 1);
            machine.Io.WriteByte(0x1F7, 0xC8);
            machine.Io.WriteByte(Bmiba, 0x09);
            Assert.Equal(0x02, machine.Io.ReadByte(Bmiba + 2) & 0x07);
        }
    }

    [Fact]
    public void BusMasterDisableAndIoDecodeOffPreventTransfers()
    {
        (Machine machine, DiskImage disk) = WithDisk();
        using (machine)
        {
            disk.WriteLinear(2, 1, Sector(0x19));
            WriteDescriptor(machine, Buffer, DiskImage.SectorSize, end: true);
            SetDescriptorTable(machine);
            SetLba(machine, 2, 1);
            machine.Pci.WriteConfiguration(0x0009, 4, 2, 0x0001);
            machine.Io.WriteByte(0x1F7, 0xC8);
            machine.Io.WriteByte(Bmiba, 0x09);
            Assert.Equal(0, machine.Io.ReadByte(Bmiba + 2) & 0x01);
            Assert.Equal(0, machine.Memory.ReadByte(Buffer));

            machine.Pci.WriteConfiguration(0x0009, 4, 2, 0x0004);
            Assert.Equal(0xFF, machine.Io.ReadByte(Bmiba));
        }
    }

    [Fact]
    public void IdentifyAndSetFeaturesAdvertiseOnlyPiix4DmaModes()
    {
        (Machine machine, _) = WithDisk();
        using (machine)
        {
            machine.Io.WriteByte(0x1F1, 0x03);
            machine.Io.WriteByte(0x1F2, 0x42);
            machine.Io.WriteByte(0x1F7, 0xEF);
            Assert.Equal(0, machine.Io.ReadByte(0x1F7) & 0x01);

            machine.Io.WriteByte(0x1F1, 0x03);
            machine.Io.WriteByte(0x1F2, 0x43);
            machine.Io.WriteByte(0x1F7, 0xEF);
            Assert.Equal(0x01, machine.Io.ReadByte(0x1F7) & 0x01);

            machine.Io.WriteByte(0x1F7, 0xEC);
            var identify = new byte[512];
            for (int i = 0; i < identify.Length; i++)
            {
                identify[i] = machine.Io.ReadByte(0x1F0);
            }

            Assert.NotEqual(0, BitConverter.ToUInt16(identify, 98) & 0x0100);
            Assert.Equal(0x0007, BitConverter.ToUInt16(identify, 176) & 0x0007);
        }
    }

    [Fact]
    public void StatusCapabilityBitsClearAndEveryAtaInterruptLatchesBmista()
    {
        (Machine machine, _) = WithDisk();
        using (machine)
        {
            machine.Io.WriteByte(Bmiba + 2, 0x60);
            Assert.Equal(0x60, machine.Io.ReadByte(Bmiba + 2) & 0x60);
            machine.Io.WriteByte(Bmiba + 2, 0x00);
            Assert.Equal(0, machine.Io.ReadByte(Bmiba + 2) & 0x60);

            machine.Io.WriteByte(0x1F7, 0xE7); // PIO FLUSH CACHE raises INTRQ
            Assert.Equal(0x04, machine.Io.ReadByte(Bmiba + 2) & 0x04);
            machine.Io.WriteByte(Bmiba + 2, 0x04);

            machine.Io.WriteByte(0x3F6, 0x02); // nIEN suppresses the drive cable edge
            machine.Io.WriteByte(0x1F7, 0xE7);
            Assert.Equal(0, machine.Io.ReadByte(Bmiba + 2) & 0x04);
            machine.Io.WriteByte(0x3F6, 0x00);
            machine.Io.WriteByte(0x21, 0xFF);  // PIC masking does not suppress the cable edge
            machine.Io.WriteByte(0x1F7, 0xE7);
            Assert.Equal(0x04, machine.Io.ReadByte(Bmiba + 2) & 0x04);

            machine.Io.WriteByte(Bmiba + 2, 0x04);
            machine.Io.WriteByte(0x1F1, 0x03);
            machine.Io.WriteByte(0x1F2, 0x0C); // advertised PIO mode 4
            machine.Io.WriteByte(0x1F7, 0xEF);
            Assert.Equal(0, machine.Io.ReadByte(0x1F7) & 0x01);
            Assert.Equal(0x04, machine.Io.ReadByte(Bmiba + 2) & 0x04);
        }
    }
    [Fact]
    public void ShortWriteCommitsOnlyWholeSectorsWithoutCompletionOrError()
    {
        (Machine machine, DiskImage disk) = WithDisk();
        using (machine)
        {
            disk.WriteLinear(7, 3, Enumerable.Repeat((byte)0x19, 1536).ToArray());
            for (int i = 0; i < 768; i++)
            {
                machine.Memory.WriteByte(Buffer + (uint)i, 0xA5);
            }
            WriteDescriptor(machine, Buffer, 768, end: true);
            SetDescriptorTable(machine);
            SetLba(machine, 7, 3);
            machine.Io.WriteByte(0x1F7, 0xCA);
            machine.Io.WriteByte(Bmiba, 1);

            var actual = new byte[1536];
            Assert.True(disk.ReadLinear(7, 3, actual));
            Assert.All(actual.Take(512), value => Assert.Equal(0xA5, value));
            Assert.All(actual.Skip(512), value => Assert.Equal(0x19, value));
            Assert.Equal(0, machine.Io.ReadByte(Bmiba + 2) & 7);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NienUnmaskLatchesOneEdgeAndAcknowledgmentsAreIndependent(bool ataFirst)
    {
        (Machine machine, _) = WithDisk();
        using (machine)
        {
            machine.Io.WriteByte(0x3F6, 2);
            machine.Io.WriteByte(0x1F7, 0xE7);
            Assert.Equal(0, machine.Io.ReadByte(Bmiba + 2) & 4);
            machine.Io.WriteByte(0x3F6, 0);
            Assert.Equal(4, machine.Io.ReadByte(Bmiba + 2) & 4);
            if (ataFirst)
            {
                machine.Io.ReadByte(0x1F7);
                Assert.Equal(4, machine.Io.ReadByte(Bmiba + 2) & 4);
            }
            machine.Io.WriteByte(Bmiba + 2, 4);
            machine.Io.WriteByte(0x3F6, 0);
            Assert.Equal(0, machine.Io.ReadByte(Bmiba + 2) & 4);
            machine.Io.ReadByte(0x1F7);
            machine.Io.WriteByte(0x1F7, 0xE7);
            Assert.Equal(4, machine.Io.ReadByte(Bmiba + 2) & 4);
        }
    }

    [Fact]
    public void SnapshotPreservesNienAndDoesNotInventAnEdgeForARestoredHighLine()
    {
        (Machine machine, DiskImage disk) = WithDisk();
        using (machine)
        {
            disk.WriteLinear(7, 1, Sector(0x35));
            WriteDescriptor(machine, Buffer, 512, end: true);
            SetDescriptorTable(machine);
            SetLba(machine, 7, 1);
            machine.Io.WriteByte(0x3F6, 2);
            machine.Io.WriteByte(0x1F7, 0xC8);
            MachineSnapshot pending = MachineSnapshot.Capture(machine);
            machine.Io.WriteByte(0x3F6, 0);
            machine.Io.WriteByte(Bmiba, 9);
            pending.RestoreTo(machine);
            Assert.Equal(2, machine.Ide.DeviceControl);
            machine.Io.WriteByte(Bmiba, 9);
            Assert.Equal(0x35, machine.Memory.ReadByte(Buffer));
            Assert.Equal(0, machine.Io.ReadByte(Bmiba + 2) & 7);
            machine.Io.WriteByte(0x3F6, 0);
            Assert.Equal(4, machine.Io.ReadByte(Bmiba + 2) & 4);
            machine.Io.WriteByte(Bmiba + 2, 4);
            byte[] completed = machine.BusMasterIde!.Capture();
            machine.Io.ReadByte(0x1F7);
            machine.BusMasterIde.Restore(completed);
            Assert.Equal(0, machine.Io.ReadByte(Bmiba + 2) & 4);
            machine.Io.WriteByte(0x3F6, 0);
            Assert.Equal(0, machine.Io.ReadByte(Bmiba + 2) & 4);
        }
    }

    [Fact]
    public void SecondaryDmaUsesTheRelocatedBarAndIndependentStatus()
    {
        (Machine machine, _) = WithDisk();
        using (machine)
        {
            using DiskImage disk = DiskImage.CreateInMemory(0x81, 20, 16, 63);
            machine.IdeSecondary.AttachHardDisk(disk);
            machine.Pci.WriteConfiguration(0x0009, 0x20, 4, 0xD001);
            Assert.Equal(0xFF, machine.Io.ReadByte(Bmiba));
            WriteDescriptor(machine, Buffer, 512, end: true);
            machine.Io.WriteDword(0xD00C, DescriptorTable);
            disk.WriteLinear(3, 1, Sector(0x48));
            machine.Io.WriteByte(0x172, 1);
            machine.Io.WriteByte(0x173, 3);
            machine.Io.WriteByte(0x174, 0);
            machine.Io.WriteByte(0x175, 0);
            machine.Io.WriteByte(0x176, 0xE0);
            machine.Io.WriteByte(0x177, 0xC8);
            machine.Io.WriteByte(0xD008, 9);
            Assert.Equal(0x48, machine.Memory.ReadByte(Buffer));
            Assert.Equal(4, machine.Io.ReadByte(0xD00A) & 7);
            Assert.Equal(0, machine.Io.ReadByte(0xD002) & 7);
            machine.Io.ReadByte(0x177);
            machine.Io.WriteByte(0xD00A, 4);
            machine.Io.WriteByte(0xD008, 0);
            machine.Memory.WriteByte(Buffer, 0xA7);
            machine.Io.WriteByte(0x177, 0xCA);
            machine.Io.WriteByte(0xD008, 1);
            var actual = new byte[512];
            Assert.True(disk.ReadLinear(3, 1, actual));
            Assert.Equal(0xA7, actual[0]);
        }
    }

    [Fact]
    public void WideAccessAcrossTheBarEndUsesOpenBusForUnmappedBytes()
    {
        (Machine machine, _) = WithDisk();
        using (machine)
        {
            Assert.Equal(0xFFFF0000u, machine.Io.ReadDword(Bmiba + 14));
            Assert.Equal(0xFF00, machine.Io.ReadWord(Bmiba + 15));
            machine.Io.WriteDword(Bmiba + 14, 0xAABBCCDD);
            machine.Io.WriteWord(Bmiba + 15, 0x1234);
            Assert.Equal(0x34DD0000u, machine.Io.ReadDword(Bmiba + 12));
        }
    }
    [Fact]
    public void ZeroPrdCountMovesAllSixtyFourKilobytes()
    {
        (Machine machine, DiskImage disk) = WithDisk();
        using (machine)
        {
            byte[] expected = Enumerable.Range(0, 65536).Select(i => (byte)(i * 17 + 5)).ToArray();
            disk.WriteLinear(10, 128, expected);
            WriteDescriptor(machine, 0x10000, 0, end: true);
            SetDescriptorTable(machine);
            SetLba(machine, 10, 128);
            machine.Io.WriteByte(0x1F7, 0xC8);
            machine.Io.WriteByte(Bmiba, 9);
            byte[] actual = Enumerable.Range(0, 65536)
                .Select(i => machine.Memory.ReadByte(0x10000 + (uint)i)).ToArray();
            Assert.Equal(expected, actual);
            Assert.Equal(4, machine.Io.ReadByte(Bmiba + 2) & 7);
        }
    }

    [Fact]
    public void SnapshotRestoresAtaErrorAndIndependentBmistaAcknowledgment()
    {
        (Machine machine, _) = WithDisk();
        using (machine)
        {
            machine.Io.WriteByte(0x1F7, 0xFF);
            Assert.Equal(1, machine.Io.ReadByte(0x3F6) & 1);
            byte error = machine.Io.ReadByte(0x1F1);
            machine.Io.WriteByte(Bmiba + 2, 4);
            byte[] state = machine.BusMasterIde!.Capture();
            machine.Io.ReadByte(0x1F7);
            machine.Io.WriteByte(0x1F7, 0xE7);
            machine.BusMasterIde.Restore(state);
            Assert.Equal(1, machine.Io.ReadByte(0x3F6) & 1);
            Assert.Equal(error, machine.Io.ReadByte(0x1F1));
            Assert.Equal(0, machine.Io.ReadByte(Bmiba + 2) & 4);
        }
    }
}
