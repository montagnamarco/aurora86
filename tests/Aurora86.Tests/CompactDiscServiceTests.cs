using System.Text;
using Aurora86.Core;
using Aurora86.Core.Firmware;
using Aurora86.Core.Firmware.Fpu;
using Aurora86.Core.Devices.Storage;
using Aurora86.Core.Diagnostics;
using Aurora86.Core.Cpu;

namespace Aurora86.Tests;

/// <summary>
/// MSCDEX is the interface DOS programs use to find a CD-ROM.
///
/// DOS itself never understood CDs: its file system expects a file allocation
/// table and 512-byte sectors, and a CD has neither. MSCDEX filled the gap by
/// answering INT 2Fh function 15h, and every installer and game that looks for
/// its own disc was written against exactly these calls.
/// </summary>
public class CompactDiscServiceTests
{
    /// <summary>
    /// A structurally valid disc whose sectors carry recognisable contents, so
    /// that a read can be checked against what should have been there.
    /// </summary>
    private static MemoryStream BuildDisc(int extraSectors = 8)
    {
        int sectors = IsoImage.PrimaryVolumeDescriptorSector + 1 + extraSectors;
        var bytes = new byte[sectors * IsoImage.SectorSize];

        int pvd = IsoImage.PrimaryVolumeDescriptorSector * IsoImage.SectorSize;
        bytes[pvd] = 1;
        Encoding.ASCII.GetBytes("CD001").CopyTo(bytes, pvd + 1);
        Encoding.ASCII.GetBytes("AURORA".PadRight(32)).CopyTo(bytes, pvd + 40);

        // Stamp each sector with its own number, so a misplaced read is
        // obvious rather than merely wrong.
        for (int sector = 0; sector < sectors; sector++)
            bytes[sector * IsoImage.SectorSize] = (byte)sector;

        var stream = new MemoryStream();
        stream.Write(bytes);
        stream.Position = 0;
        return stream;
    }

    private static Machine WithDisc()
    {
        var machine = new Machine();
        machine.RunInstructions(200);
        machine.AttachCompactDisc(new IsoImage(BuildDisc()));
        return machine;
    }

    /// <summary>Calls INT 2Fh with AH=15h and the given subfunction in AL.</summary>
    private static void Call(Machine machine, byte function)
    {
        machine.Cpu.State.Ax = (ushort)(0x1500 | function);
        machine.CompactDiscServices.Handle(machine.Cpu);
    }

    [Fact]
    public void ReportsOneDriveWhenADiscIsPresent()
    {
        using Machine machine = WithDisc();

        Call(machine, 0x00);

        Assert.Equal(1, machine.Cpu.State.Bx);
        Assert.Equal(machine.CompactDiscServices.DriveLetter, machine.Cpu.State.Cx);
    }

    [Fact]
    public void ReportsNoDrivesWhenTheTrayIsEmpty()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);

        Call(machine, 0x00);

        Assert.Equal(0, machine.Cpu.State.Bx);
    }

    [Fact]
    public void RecognisesItsOwnDriveLetterAndNoOther()
    {
        using Machine machine = WithDisc();
        byte letter = machine.CompactDiscServices.DriveLetter;

        machine.Cpu.State.Ax = 0x150B;
        machine.Cpu.State.SetReg8(2, letter);       // CL: the drive to ask about
        machine.CompactDiscServices.Handle(machine.Cpu);
        Assert.Equal(1, machine.Cpu.State.Ax);

        machine.Cpu.State.Ax = 0x150B;
        machine.Cpu.State.SetReg8(2, (byte)(letter + 1));
        machine.CompactDiscServices.Handle(machine.Cpu);
        Assert.Equal(0, machine.Cpu.State.Ax);
    }

    [Fact]
    public void TheSignatureTellsAProgramMscdexAnsweredAtAll()
    {
        // Without 0xADAD in BX a program cannot tell "not a CD drive" from
        // "MSCDEX is not loaded", and the two call for different behaviour:
        // the first means try another letter, the second means give up.
        using Machine machine = WithDisc();

        machine.Cpu.State.Ax = 0x150B;
        machine.Cpu.State.SetReg8(2, machine.CompactDiscServices.DriveLetter);
        machine.CompactDiscServices.Handle(machine.Cpu);

        Assert.Equal(0xADAD, machine.Cpu.State.Bx);
    }

    [Fact]
    public void ReportsAVersionProgramsWillAccept()
    {
        using Machine machine = WithDisc();

        Call(machine, 0x0C);

        // 2.25, the last release: major in BH, minor in BL.
        Assert.Equal(2, machine.Cpu.State.GetReg8(7));
        Assert.Equal(0x19, machine.Cpu.State.GetReg8(3));
    }

    [Fact]
    public void ReadsASectorIntoTheCallersBuffer()
    {
        // This is the call that matters: raw sector reads are how a program
        // checks that the original disc is in the drive.
        using Machine machine = WithDisc();

        const ushort RequestSegment = 0x3000;
        const ushort RequestOffset = 0x0100;
        const ushort BufferSegment = 0x4000;
        const ushort BufferOffset = 0x0000;
        const int Sector = 20;

        uint request = (uint)(RequestSegment << 4) + RequestOffset;

        machine.Memory.WriteByte(request, 30);        // header length
        machine.Memory.WriteByte(request + 2, 0x80);  // read long
        machine.Memory.WriteWord(request + 14, BufferOffset);
        machine.Memory.WriteWord(request + 16, BufferSegment);
        machine.Memory.WriteWord(request + 18, 1);    // one sector
        machine.Memory.WriteDword(request + 20, Sector);

        machine.Cpu.SetSegment(SegReg.Es, RequestSegment);
        machine.Cpu.State.Bx = RequestOffset;
        Call(machine, 0x10);

        // Each sector was stamped with its own number.
        Assert.Equal(Sector, machine.Memory.ReadByte((uint)(BufferSegment << 4)));

        // Status: done, no error.
        ushort status = machine.Memory.ReadWord(request + 3);
        Assert.Equal(0x0100, status);
    }

    [Fact]
    public void ReadingPastTheEndOfTheDiscFails()
    {
        // A program that asks for a sector that is not there must be told so,
        // not handed whatever happened to be in the buffer already.
        using Machine machine = WithDisc();

        const ushort RequestSegment = 0x3000;
        uint request = (uint)(RequestSegment << 4);

        machine.Memory.WriteByte(request + 2, 0x80);
        machine.Memory.WriteWord(request + 14, 0);
        machine.Memory.WriteWord(request + 16, 0x4000);
        machine.Memory.WriteWord(request + 18, 1);
        machine.Memory.WriteDword(request + 20, 100_000);   // far past the end

        machine.Cpu.SetSegment(SegReg.Es, RequestSegment);
        machine.Cpu.State.Bx = 0;
        Call(machine, 0x10);

        ushort status = machine.Memory.ReadWord(request + 3);
        Assert.Equal(0x8000, status & 0x8000);   // the error bit
    }

    [Fact]
    public void ARequestWithNoDiscReportsTheDriveIsNotReady()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);

        const ushort RequestSegment = 0x3000;
        uint request = (uint)(RequestSegment << 4);
        machine.Memory.WriteByte(request + 2, 0x80);

        machine.Cpu.SetSegment(SegReg.Es, RequestSegment);
        machine.Cpu.State.Bx = 0;
        Call(machine, 0x10);

        ushort status = machine.Memory.ReadWord(request + 3);
        Assert.Equal(0x8000, status & 0x8000);
        Assert.Equal(0x02, status & 0xFF);       // not ready
    }

    [Fact]
    public void TheDriveLetterListIsWrittenWhereAskedFor()
    {
        using Machine machine = WithDisc();

        const ushort Segment = 0x5000;
        machine.Cpu.SetSegment(SegReg.Es, Segment);
        machine.Cpu.State.Bx = 0x20;

        Call(machine, 0x0D);

        Assert.Equal(machine.CompactDiscServices.DriveLetter,
                     machine.Memory.ReadByte((uint)(Segment << 4) + 0x20));
    }

    /// <summary>
    /// The disc's letter moves along when there is a second hard disk.
    ///
    /// D: is a convention, not a rule: DOS hands out letters to the disks
    /// first and MSCDEX takes the next one. A CD-ROM that insisted on D:
    /// with two disks attached would be answering for a drive that is
    /// already a disk -- and the guest, asking that letter about a disc,
    /// gets "not ready reading drive D" over a perfectly good disk.
    /// </summary>
    [Fact]
    public void TheDiscTakesTheLetterAfterTheDisks()
    {
        string first = Path.Combine(Path.GetTempPath(), $"aurora-{Guid.NewGuid():N}.img");
        string second = Path.Combine(Path.GetTempPath(), $"aurora-{Guid.NewGuid():N}.img");
        try
        {
            Aurora86.Core.Devices.Storage.DiskImageBuilder.CreateFat16(first, 20_000_000);
            Aurora86.Core.Devices.Storage.DiskImageBuilder.CreateFat16(second, 20_000_000);

            using var machine = new Machine();

            machine.AttachDisk(
                Aurora86.Core.Devices.Storage.DiskImage.OpenHardDisk(first, 0x80));
            Assert.Equal(3, machine.CompactDiscServices.DriveLetter);    // D:

            machine.AttachDisk(
                Aurora86.Core.Devices.Storage.DiskImage.OpenHardDisk(second, 0x81));
            Assert.Equal(4, machine.CompactDiscServices.DriveLetter);    // E:

            // And an explicit choice still wins: somebody who has arranged
            // their drives a particular way knows better than the arithmetic.
            machine.CompactDiscServices.DriveLetter = 6;
            Assert.Equal(6, machine.CompactDiscServices.DriveLetter);
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }
}
