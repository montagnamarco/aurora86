using System.Text;
using Aurora386.Core;
using Aurora386.Core.Devices.Storage;

namespace Aurora386.Tests;

/// <summary>
/// Two hard disks and a CD-ROM on the two IDE cables, and the BIOS booting
/// from whichever disk it is told to.
/// </summary>
public class TwoDisksTests
{
    private static DiskImage Bootable(byte drive, ushort marker)
    {
        DiskImage disk = DiskImage.CreateInMemory(drive, 20, 16, 63);
        var mbr = new byte[512];
        // MOV AX,marker ; MOV [6000h],AX ; HLT
        new byte[] { 0xB8, (byte)marker, (byte)(marker >> 8), 0xA3, 0x00, 0x60, 0xF4 }.CopyTo(mbr, 0);
        mbr[510] = 0x55; mbr[511] = 0xAA;
        disk.WriteLinear(0, 1, mbr);
        return disk;
    }

    private static byte[] Identify(Machine machine, ushort basePort, byte select)
    {
        machine.Io.WriteByte((ushort)(basePort + 6), select);
        machine.Io.WriteByte((ushort)(basePort + 7), 0xEC);
        var block = new byte[512];
        for (int i = 0; i < 512; i += 2)
        {
            ushort word = machine.Io.ReadWord(basePort);
            block[i] = (byte)word; block[i + 1] = (byte)(word >> 8);
        }
        return block;
    }

    [Fact]
    public void EachDiskSitsAsMasterOfItsOwnCableAndTheCdRomStaysOnTheFirst()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);
        machine.AttachDisk(DiskImage.CreateInMemory(0x80, 20, 16, 63));
        machine.AttachDisk(DiskImage.CreateInMemory(0x81, 40, 16, 63));

        var bytes = new byte[100 * IsoImage.SectorSize];
        int pvd = IsoImage.PrimaryVolumeDescriptorSector * IsoImage.SectorSize;
        bytes[pvd] = 1;
        Encoding.ASCII.GetBytes("CD001").CopyTo(bytes, pvd + 1);
        machine.AttachCompactDisc(new IsoImage(new MemoryStream(bytes)));

        Assert.Equal(20, BitConverter.ToUInt16(Identify(machine, 0x1F0, 0xA0), 2));   // primary master
        Assert.Equal(40, BitConverter.ToUInt16(Identify(machine, 0x170, 0xA0), 2));   // secondary master

        machine.Io.WriteByte(0x1F6, 0xB0);                     // primary slave: the CD-ROM
        machine.Io.WriteByte(0x1F7, 0xA1);
        Assert.Equal(0x08, machine.Io.ReadByte(0x1F7) & 0x08);

        machine.Io.WriteByte(0x176, 0xB0);                     // secondary slave: nothing
        Assert.Equal(0x00, machine.Io.ReadByte(0x177));
    }

    [Fact]
    public void TheBiosBootsFromTheSecondDiskWhenToldTo()
    {
        using var machine = new Machine();
        machine.AttachDisk(Bootable(0x80, 0xAAAA));
        machine.AttachDisk(Bootable(0x81, 0xBBBB));
        machine.BootDrives = [0x81];

        machine.RunInstructions(3000);

        Assert.True(machine.Booted);
        Assert.Equal(0xBBBB, machine.Memory.ReadWord(0x6000));
        Assert.Equal(0x81, machine.Cpu.State.GetReg8(2));     // DL: the drive it booted from
    }

    [Fact]
    public void TheBiosBootsFromTheFirstDiskByDefault()
    {
        using var machine = new Machine();
        machine.AttachDisk(Bootable(0x80, 0xAAAA));
        machine.AttachDisk(Bootable(0x81, 0xBBBB));

        machine.RunInstructions(3000);

        Assert.True(machine.Booted);
        Assert.Equal(0xAAAA, machine.Memory.ReadWord(0x6000));
        Assert.Equal(0x80, machine.Cpu.State.GetReg8(2));
    }
}
