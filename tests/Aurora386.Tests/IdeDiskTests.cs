using System.Text;
using Aurora386.Core;
using Aurora386.Core.Devices.Storage;

namespace Aurora386.Tests;

/// <summary>
/// The ATA hard disk on the IDE cable, driven through its registers the way
/// a 32-bit disk driver does, with the BIOS nowhere in the picture.
/// </summary>
public class IdeDiskTests
{
    private static (Machine Machine, DiskImage Disk) WithDisk()
    {
        var machine = new Machine();
        machine.RunInstructions(200);
        DiskImage disk = DiskImage.CreateInMemory(0x80, 20, 16, 63);
        machine.AttachDisk(disk);
        return (machine, disk);
    }

    private static void SelectMaster(Machine machine, bool lba = true) =>
        machine.Io.WriteByte(0x1F6, (byte)(0xA0 | (lba ? 0x40 : 0)));

    private static void SetAddress(Machine machine, long sector, int count)
    {
        machine.Io.WriteByte(0x1F2, (byte)count);
        machine.Io.WriteByte(0x1F3, (byte)sector);
        machine.Io.WriteByte(0x1F4, (byte)(sector >> 8));
        machine.Io.WriteByte(0x1F5, (byte)(sector >> 16));
        machine.Io.WriteByte(0x1F6, (byte)(0xE0 | ((sector >> 24) & 0x0F)));
    }

    private static byte[] Drain(Machine machine, int bytes)
    {
        var received = new byte[bytes];
        for (int i = 0; i < bytes; i += 2)
        {
            ushort word = machine.Io.ReadWord(0x1F0);
            received[i] = (byte)word;
            received[i + 1] = (byte)(word >> 8);
        }
        return received;
    }

    private static byte[] Sector(byte fill)
    {
        var sector = new byte[512];
        for (int i = 0; i < 512; i++) sector[i] = (byte)(fill + i);
        return sector;
    }

    [Fact]
    public void TheMasterAnswersReadyWhenADiskIsThereAndAsNothingWhenNot()
    {
        using var empty = new Machine();
        empty.RunInstructions(200);
        empty.Io.WriteByte(0x1F6, 0xA0);
        Assert.Equal(0x00, empty.Io.ReadByte(0x1F7));

        (Machine machine, _) = WithDisk();
        using (machine)
        {
            SelectMaster(machine);
            Assert.Equal(0x50, machine.Io.ReadByte(0x1F7) & 0x50);   // ready, seek complete
        }
    }

    [Fact]
    public void AfterAResetTheDiskLeavesItsSignatureAndTheCdRomItsOwn()
    {
        (Machine machine, _) = WithDisk();
        using (machine)
        {
            machine.Io.WriteByte(0x3F6, 0x04);
            machine.Io.WriteByte(0x3F6, 0x00);

            SelectMaster(machine, lba: false);
            Assert.Equal((1, 1, 0, 0), (machine.Io.ReadByte(0x1F2), machine.Io.ReadByte(0x1F3),
                                        machine.Io.ReadByte(0x1F4), machine.Io.ReadByte(0x1F5)));

            machine.Io.WriteByte(0x1F6, 0xB0);
            Assert.Equal((0x14, 0xEB), (machine.Io.ReadByte(0x1F4), machine.Io.ReadByte(0x1F5)));
        }
    }

    [Fact]
    public void IdentifyDescribesTheDiskWithItsNameUnscrambled()
    {
        (Machine machine, DiskImage disk) = WithDisk();
        using (machine)
        {
            SelectMaster(machine);
            machine.Io.WriteByte(0x1F7, 0xEC);
            Assert.Equal(0x08, machine.Io.ReadByte(0x1F7) & 0x08);
            byte[] block = Drain(machine, 512);
            Assert.Equal(0x00, machine.Io.ReadByte(0x1F7) & 0x08);

            Assert.Equal(0x0040, BitConverter.ToUInt16(block, 0));
            Assert.Equal(20, BitConverter.ToUInt16(block, 2));
            Assert.Equal(16, BitConverter.ToUInt16(block, 6));
            Assert.Equal(63, BitConverter.ToUInt16(block, 12));
            Assert.Equal((uint)disk.TotalSectors, BitConverter.ToUInt32(block, 120));
            Assert.NotEqual(0, BitConverter.ToUInt16(block, 98) & 0x0200);   // LBA

            var model = new StringBuilder();
            for (int i = 54; i < 94; i += 2) { model.Append((char)block[i + 1]); model.Append((char)block[i]); }
            Assert.StartsWith("Aurora386 Virtual Hard Disk", model.ToString());
        }
    }

    [Fact]
    public void ReadSectorsDeliversOneSectorPerInterruptByNumber()
    {
        (Machine machine, DiskImage disk) = WithDisk();
        using (machine)
        {
            disk.WriteLinear(5, 1, Sector(0x10));
            disk.WriteLinear(6, 1, Sector(0x20));

            SetAddress(machine, 5, 2);
            machine.Io.WriteByte(0x1F7, 0x20);

            Assert.Equal(Sector(0x10), Drain(machine, 512));
            Assert.Equal(0x08, machine.Io.ReadByte(0x1F7) & 0x08);   // the next sector is waiting
            Assert.Equal(Sector(0x20), Drain(machine, 512));
            Assert.Equal(0x00, machine.Io.ReadByte(0x1F7) & 0x09);   // done, no error
        }
    }

    [Fact]
    public void CylinderHeadSectorAddressesAreDecodedWithTheLogicalGeometry()
    {
        (Machine machine, DiskImage disk) = WithDisk();
        using (machine)
        {
            long linear = (1L * 16 + 2) * 63 + 2;       // cylinder 1, head 2, sector 3
            disk.WriteLinear(linear, 1, Sector(0x33));

            machine.Io.WriteByte(0x1F2, 1);
            machine.Io.WriteByte(0x1F3, 3);              // sector
            machine.Io.WriteByte(0x1F4, 1);              // cylinder low
            machine.Io.WriteByte(0x1F5, 0);
            machine.Io.WriteByte(0x1F6, 0xA2);           // master, head 2, CHS
            machine.Io.WriteByte(0x1F7, 0x20);
            Assert.Equal(Sector(0x33), Drain(machine, 512));
        }
    }

    [Fact]
    public void WriteSectorsStoresWhatArrivesThroughTheDataPort()
    {
        (Machine machine, DiskImage disk) = WithDisk();
        using (machine)
        {
            SetAddress(machine, 9, 1);
            machine.Io.WriteByte(0x1F7, 0x30);
            Assert.Equal(0x08, machine.Io.ReadByte(0x1F7) & 0x08);   // asking for data, no interrupt yet

            byte[] data = Sector(0x77);
            for (int i = 0; i < 512; i += 2)
                machine.Io.WriteWord(0x1F0, (ushort)(data[i] | (data[i + 1] << 8)));

            Assert.Equal(0x00, machine.Io.ReadByte(0x1F7) & 0x09);
            var back = new byte[512];
            Assert.True(disk.ReadLinear(9, 1, back));
            Assert.Equal(data, back);
        }
    }

    [Fact]
    public void ReadMultipleMovesABlockPerInterrupt()
    {
        (Machine machine, DiskImage disk) = WithDisk();
        using (machine)
        {
            for (int i = 0; i < 4; i++) disk.WriteLinear(10 + i, 1, Sector((byte)(i * 0x11)));

            SelectMaster(machine);
            machine.Io.WriteByte(0x1F2, 4);
            machine.Io.WriteByte(0x1F7, 0xC6);           // SET MULTIPLE MODE 4
            Assert.Equal(0x00, machine.Io.ReadByte(0x1F7) & 0x01);

            SetAddress(machine, 10, 4);
            machine.Io.WriteByte(0x1F7, 0xC4);
            byte[] block = Drain(machine, 2048);
            Assert.Equal(0x00, machine.Io.ReadByte(0x1F7) & 0x08);
            for (int i = 0; i < 4; i++)
                Assert.Equal(Sector((byte)(i * 0x11)), block[(i * 512)..((i + 1) * 512)]);
        }
    }

    [Fact]
    public void ASectorPastTheEndIsAnIdNotFoundError()
    {
        (Machine machine, DiskImage disk) = WithDisk();
        using (machine)
        {
            SetAddress(machine, disk.TotalSectors, 1);
            machine.Io.WriteByte(0x1F7, 0x20);
            Assert.Equal(0x01, machine.Io.ReadByte(0x1F7) & 0x01);
            Assert.Equal(0x10, machine.Io.ReadByte(0x1F1));
        }
    }

    [Fact]
    public void APacketCommandIsAbortedByADisk()
    {
        (Machine machine, _) = WithDisk();
        using (machine)
        {
            SelectMaster(machine);
            machine.Io.WriteByte(0x1F7, 0xA1);
            Assert.Equal(0x01, machine.Io.ReadByte(0x1F7) & 0x01);
            Assert.Equal(0x04, machine.Io.ReadByte(0x1F1));
        }
    }

    [Fact]
    public void TheCdRomOnTheSlaveStillAnswersWithADiskOnTheMaster()
    {
        (Machine machine, _) = WithDisk();
        using (machine)
        {
            var bytes = new byte[100 * IsoImage.SectorSize];
            int pvd = IsoImage.PrimaryVolumeDescriptorSector * IsoImage.SectorSize;
            bytes[pvd] = 1;
            Encoding.ASCII.GetBytes("CD001").CopyTo(bytes, pvd + 1);
            machine.AttachCompactDisc(new IsoImage(new MemoryStream(bytes)));

            machine.Io.WriteByte(0x1F6, 0xB0);           // the slave
            machine.Io.WriteByte(0x1F7, 0xA1);           // IDENTIFY PACKET DEVICE
            Assert.Equal(0x08, machine.Io.ReadByte(0x1F7) & 0x08);
            byte[] block = Drain(machine, 512);
            Assert.Equal(0x85C0, BitConverter.ToUInt16(block, 0));

            SelectMaster(machine);                       // and the disk is still itself
            machine.Io.WriteByte(0x1F7, 0xEC);
            Assert.Equal(0x0040, BitConverter.ToUInt16(Drain(machine, 512), 0));
        }
    }
}
