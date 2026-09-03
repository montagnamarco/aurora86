using Aurora386.Core;
using Aurora386.Core.Bios;
using CpuNs = Aurora386.Core.Cpu;

namespace Aurora386.Tests;

/// <summary>
/// Hard disk images: MBR layout, geometry, and how the BIOS sees them.
/// Unlike a floppy, a hard disk starts with a Master Boot Record rather than
/// with a file system, so there is an extra layer of indirection before any
/// file can be found.
/// </summary>
public class HardDiskTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(),
        $"emu-test-{Guid.NewGuid():N}.img");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void GeometryMatchesTheRequestedSize()
    {
        // 40 MB: a typical size for a disk of the period.
        HardDiskImage.Geometry g = HardDiskImage.Create(_path, 40 * 1024 * 1024);

        Assert.Equal(16, g.Heads);
        Assert.Equal(63, g.SectorsPerTrack);
        Assert.True(g.Cylinders > 0);
        // The file must match the rounded geometry, not the raw request.
        Assert.Equal(g.TotalBytes, new FileInfo(_path).Length);
    }

    [Fact]
    public void GeometryStaysWithinTheChsLimits()
    {
        // Beyond 1024 cylinders the BIOS's CHS addressing simply cannot
        // reach: the cylinder field is ten bits wide. Cap the geometry rather
        // than producing one that describes sectors nothing can address.
        HardDiskImage.Geometry g = HardDiskImage.Geometry.ForSize(2L * 1024 * 1024 * 1024);
        Assert.True(g.Cylinders <= 1024);
    }

    [Fact]
    public void MbrHasTheSignatureAndAnActivePartition()
    {
        HardDiskImage.Create(_path, 20 * 1024 * 1024);
        byte[] mbr = new byte[512];
        using (var fs = File.OpenRead(_path)) fs.ReadExactly(mbr);

        Assert.Equal(0x55, mbr[0x1FE]);
        Assert.Equal(0xAA, mbr[0x1FF]);

        // First partition table entry: active, type FAT16.
        Assert.Equal(0x80, mbr[0x1BE]);
        Assert.Equal(HardDiskImage.PartitionType.Fat16, mbr[0x1BE + 4]);

        // The partition starts after the first track -- the MBR itself lives
        // there -- and spans a useful number of sectors.
        uint start = BitConverter.ToUInt32(mbr, 0x1BE + 8);
        uint count = BitConverter.ToUInt32(mbr, 0x1BE + 12);
        Assert.Equal(63u, start);
        Assert.True(count > 0);
    }

    [Fact]
    public void TheOtherPartitionEntriesAreEmpty()
    {
        HardDiskImage.Create(_path, 20 * 1024 * 1024);
        byte[] mbr = new byte[512];
        using (var fs = File.OpenRead(_path)) fs.ReadExactly(mbr);

        for (int entry = 1; entry < 4; entry++)
            for (int i = 0; i < 16; i++)
                Assert.Equal(0, mbr[0x1BE + entry * 16 + i]);
    }

    [Fact]
    public void PartitionContainsAValidFat16Bpb()
    {
        HardDiskImage.Create(_path, 40 * 1024 * 1024);

        byte[] boot = new byte[512];
        using (var fs = File.OpenRead(_path))
        {
            fs.Seek(63 * 512, SeekOrigin.Begin);   // start of the partition
            fs.ReadExactly(boot);
        }

        Assert.Equal(0xEB, boot[0]);                                   // leading jump over the BPB
        Assert.Equal(512, BitConverter.ToUInt16(boot, 11));            // bytes per sector
        Assert.Equal(2, boot[16]);                                     // two FATs
        Assert.Equal(512, BitConverter.ToUInt16(boot, 17));            // root directory entries
        Assert.Equal(0xF8, boot[21]);                                  // media descriptor: fixed disk
        Assert.Equal(0x55, boot[0x1FE]);
        Assert.Equal(0xAA, boot[0x1FF]);

        // The cluster count has to stay within FAT16's limits, or DOS will
        // read the volume as FAT12 and see garbage.
        int sectorsPerCluster = boot[13];
        uint totalSectors = BitConverter.ToUInt32(boot, 32);
        Assert.True(totalSectors / sectorsPerCluster <= 65524,
            "too many clusters for FAT16");
    }

    [Fact]
    public void ChsConversionRoundTrips()
    {
        var g = new HardDiskImage.Geometry(100, 16, 63);

        foreach (long linear in new long[] { 0, 1, 62, 63, 1007, 1008, 50000 })
        {
            (int c, int h, int s) = HardDiskImage.ToChs(g, linear);
            long back = ((long)c * g.Heads + h) * g.SectorsPerTrack + (s - 1);
            Assert.Equal(linear, back);
        }
    }

    [Fact]
    public void CreatedImageOpensWithTheRightGeometry()
    {
        HardDiskImage.Geometry expected = HardDiskImage.Create(_path, 30 * 1024 * 1024);

        using DiskImage disk = DiskImage.OpenHardDisk(_path);
        Assert.Equal(expected.Cylinders, disk.Cylinders);
        Assert.Equal(expected.Heads, disk.Heads);
        Assert.Equal(expected.SectorsPerTrack, disk.SectorsPerTrack);
        Assert.True(disk.IsHardDisk);
        Assert.Equal(0x80, disk.DriveNumber);
    }

    [Fact]
    public void HardDiskReadsAndWritesRoundTrip()
    {
        using DiskImage disk = DiskImage.CreateHardDisk(_path, 20 * 1024 * 1024);

        byte[] payload = new byte[512];
        "CONTENUTO DEL SETTORE"u8.CopyTo(payload);

        // A sector in the middle of the disk, which exercises the CHS
        // conversion as well as the read/write path.
        Assert.True(disk.WriteSectors(5, 3, 10, 1, payload));

        byte[] readBack = new byte[512];
        Assert.True(disk.ReadSectors(5, 3, 10, 1, readBack));
        Assert.Equal(payload, readBack);
    }

    [Fact]
    public void TheBiosSeesTheHardDisk()
    {
        using var machine = new Machine();
        machine.AttachDisk(DiskImage.CreateHardDisk(_path, 20 * 1024 * 1024));
        machine.RunInstructions(300);

        // The BIOS data area must report the disk as installed.
        Assert.Equal(1, machine.Bda.HardDiskCount);

        // INT 13h AH=08 must report its geometry.
        var cpu = machine.Cpu;
        cpu.State.Esp = 0x7000;
        cpu.SetSegment(CpuNs.SegReg.Ss, 0);
        machine.Memory.WriteWord(0x7004, 0);

        cpu.State.SetReg8(4, 0x08);
        cpu.State.SetReg8(2, 0x80);
        machine.DiskServices.Handle(cpu);

        Assert.Equal(0, machine.Memory.ReadWord(0x7004) & 1);   // no error
        // In the 8-bit register index scheme DH is 6 and DL is 2: the last
        // head number goes in the former, the drive count in the latter. This
        // test used to check the indices the other way round, and passed
        // against a BIOS that had them swapped too -- with that geometry
        // MS-DOS SETUP wrote its files to the wrong place and the resulting
        // disk would not boot.
        Assert.Equal(15, cpu.State.GetReg8(6));                 // DH: 16 heads, less one
        Assert.Equal(1, cpu.State.GetReg8(2));                  // DL: one hard disk
    }

    [Fact]
    public void BiosReadsFromTheHardDiskDeliverTheMbr()
    {
        // Reads the first sector through INT 13h and checks it is the MBR.
        //   B8 00 00  MOV AX,0 ; 8E C0 MOV ES,AX ; BB 00 60 MOV BX,0x6000
        //   B4 02 MOV AH,2 ; B0 01 MOV AL,1
        //   B5 00 MOV CH,0 ; B1 01 MOV CL,1 ; B6 00 MOV DH,0 ; B2 80 MOV DL,0x80
        //   CD 13 INT 13h ; F4 HLT
        var sector = new byte[512];
        new byte[] {
            0xB8, 0x00, 0x00, 0x8E, 0xC0, 0xBB, 0x00, 0x60,
            0xB4, 0x02, 0xB0, 0x01, 0xB5, 0x00, 0xB1, 0x01,
            0xB6, 0x00, 0xB2, 0x80, 0xCD, 0x13, 0xF4,
        }.CopyTo(sector, 0);
        sector[510] = 0x55; sector[511] = 0xAA;

        using var machine = new Machine();
        DiskImage floppy = DiskImage.CreateInMemory(0x00, 80, 2, 18);
        floppy.WriteSectors(0, 0, 1, 1, sector);
        machine.AttachDisk(floppy);
        machine.AttachDisk(DiskImage.CreateHardDisk(_path, 20 * 1024 * 1024));

        machine.RunInstructions(3000);

        // The MBR that was read must carry the boot signature at the end.
        Assert.Equal(0x55, machine.Memory.ReadByte(0x6000 + 510));
        Assert.Equal(0xAA, machine.Memory.ReadByte(0x6000 + 511));
        Assert.Equal(0x80, machine.Memory.ReadByte(0x6000 + 0x1BE));   // active partition flag
    }

    [Fact]
    public void ReadOnlyImageCanBeOpenedSeveralTimes()
    {
        // Inspecting the same image with several tools at once is entirely
        // normal, so opening it read-only must not prevent it. With overly
        // strict file sharing the second open fails with a "file in use"
        // error that looks like a problem with the image itself.
        HardDiskImage.Create(_path, 20 * 1024 * 1024);

        using DiskImage first = DiskImage.OpenHardDisk(_path, 0x80, readOnly: true);
        using DiskImage second = DiskImage.OpenHardDisk(_path, 0x81, readOnly: true);

        var buffer = new byte[512];
        Assert.True(first.ReadSectors(0, 0, 1, 1, buffer));
        Assert.Equal(0x55, buffer[510]);

        Assert.True(second.ReadSectors(0, 0, 1, 1, buffer));
        Assert.Equal(0xAA, buffer[511]);
    }

    [Fact]
    public void WritableImageStaysReadableByOthers()
    {
        // A writer excludes other writers but not readers, so an image can be
        // inspected while the emulator is running against it.
        HardDiskImage.Create(_path, 20 * 1024 * 1024);

        using DiskImage writer = DiskImage.OpenHardDisk(_path);
        using DiskImage reader = DiskImage.OpenHardDisk(_path, 0x81, readOnly: true);

        var payload = new byte[512];
        "SCRITTO DAL PRIMO"u8.CopyTo(payload);
        Assert.True(writer.WriteSectors(2, 0, 1, 1, payload));

        var readBack = new byte[512];
        Assert.True(reader.ReadSectors(2, 0, 1, 1, readBack));
        Assert.Equal(payload, readBack);
    }

    [Fact]
    public void MbrFindsTheActivePartitionAndLoadsItsBootSector()
    {
        // The MBR has to relocate itself downwards, find the table entry
        // marked 0x80, read that partition's boot sector to 0x7C00 and jump
        // into it. A mistake anywhere in that chain produces no message at
        // all: the screen simply stays black, which is the hardest possible
        // symptom to diagnose.
        HardDiskImage.Create(_path, 20 * 1024 * 1024);

        using var machine = new Machine();
        machine.AttachDisk(DiskImage.OpenHardDisk(_path));

        for (int i = 0; i < 20; i++) machine.RunInstructions(200_000);

        // The partition table must have been copied along to 0x7BE.
        Assert.Equal(0x80, machine.Memory.ReadByte(0x7BE));
        Assert.Equal(HardDiskImage.PartitionType.Fat16, machine.Memory.ReadByte(0x7BE + 4));

        // The partition's boot sector must have landed at 0x7C00.
        Assert.Equal(0xEB, machine.Memory.ReadByte(0x7C00));
        string oem = System.Text.Encoding.ASCII.GetString(
            machine.Memory.GetRamSpan(0x7C03, 8).ToArray());
        Assert.Equal("MSDOS5.0", oem);
    }

    [Fact]
    public void EmptyDiskReportsNonSystemDisk()
    {
        // A disk that is partitioned but carries no operating system has to
        // say so rather than sitting silent: a black screen gives no way to
        // tell "not bootable" apart from "the emulator is broken".
        HardDiskImage.Create(_path, 20 * 1024 * 1024);

        using var machine = new Machine();
        machine.AttachDisk(DiskImage.OpenHardDisk(_path));

        for (int i = 0; i < 20; i++) machine.RunInstructions(200_000);

        string screen = machine.GetScreenText();
        Assert.Contains("Non-system disk", screen);
        Assert.True(machine.Cpu.State.Halted, "the machine must halt after printing the message");
    }

    [Fact]
    public void BootableHardDiskIsChosenToBootFrom()
    {
        // With only the hard disk attached, the BIOS must boot from it.
        using var machine = new Machine();
        DiskImage disk = DiskImage.CreateHardDisk(_path, 20 * 1024 * 1024);

        // Replace the MBR with code we can recognise once it runs.
        var mbr = new byte[512];
        new byte[] { 0xB8, 0x0D, 0xF0, 0xA3, 0x00, 0x60, 0xF4 }.CopyTo(mbr, 0);
        mbr[510] = 0x55; mbr[511] = 0xAA;
        disk.WriteSectors(0, 0, 1, 1, mbr);
        machine.AttachDisk(disk);

        machine.RunInstructions(2000);

        Assert.True(machine.Booted);
        Assert.Equal(0xF00D, machine.Memory.ReadWord(0x6000));
        Assert.Equal(0x80, machine.Cpu.State.GetReg8(2));   // DL: the boot drive
    }
}

/// <summary>
/// The floppy change line tells software that the disk in the drive is not
/// the one it last read.
///
/// On real hardware it is a latch tripped by the drive door, and it stays
/// raised until the drive reads something -- it is not a message consumed by
/// being read once. Software depends on that: it polls, and expects the same
/// answer each time until it has actually looked at the new disk.
/// </summary>
public class FloppyChangeLineTests
{
    private static Machine WithFloppy()
    {
        var machine = new Machine();
        machine.RunInstructions(200);
        machine.AttachDisk(new DiskImage(new MemoryStream(new byte[1440 * 1024]),
                                         0x00, 80, 2, 18));
        return machine;
    }

    /// <summary>Asks INT 13h AH=16 whether the disk has been changed.</summary>
    private static bool ReportsChange(Machine machine)
    {
        machine.Cpu.State.SetReg8(4, 0x16);   // AH
        machine.Cpu.State.SetReg8(2, 0x00);   // DL: drive A:
        machine.DiskServices.Handle(machine.Cpu);

        return machine.Cpu.State.GetReg8(4) == 0x06;
    }

    /// <summary>Reads the first sector and reports whether it succeeded.</summary>
    private static bool ReadFirstSector(Machine machine)
    {
        machine.Cpu.State.SetReg8(4, 0x02);   // AH: read
        machine.Cpu.State.SetReg8(0, 1);      // AL: one sector
        machine.Cpu.State.Cx = 0x0001;        // cylinder 0, sector 1
        machine.Cpu.State.SetReg8(6, 0x00);   // DH: head 0
        machine.Cpu.State.SetReg8(2, 0x00);   // DL: drive A:
        machine.Cpu.State.Bx = 0x1000;
        machine.Cpu.SetSegment(Aurora386.Core.Cpu.SegReg.Es, 0x2000);

        machine.DiskServices.Handle(machine.Cpu);

        return machine.Cpu.State.GetReg8(4) == 0;
    }

    [Fact]
    public void NoChangeIsReportedForADiskThatHasNotBeenSwapped()
    {
        using Machine machine = WithFloppy();

        Assert.False(ReportsChange(machine));
    }

    [Fact]
    public void TheChangeIsStillReportedOnTheSecondAsking()
    {
        // Reporting it once and then forgetting looks reasonable and is
        // wrong: software polls. Windows 3.1's installer asks several times
        // before it reads, and an answer given only to the first poll left it
        // convinced the old floppy was still in the drive -- so it went on
        // asking for a disk the user had already inserted.
        using Machine machine = WithFloppy();
        machine.DiskServices.NotifyMediaChanged(0);

        Assert.True(ReportsChange(machine));
        Assert.True(ReportsChange(machine));
        Assert.True(ReportsChange(machine));
    }

    [Fact]
    public void TheFirstReadAfterAChangeIsRefused()
    {
        // The change line is still raised when that read arrives, and the
        // controller refuses rather than reading a disk whose layout it has
        // not looked at yet. DOS depends on the error: it is what makes it
        // throw away the file allocation table and directory it cached from
        // the previous disk.
        //
        // Letting the read succeed leaves DOS using the old disk's map. It
        // then finds the new disk's files where the old one's were, copies
        // nothing, and believes it copied everything -- which is how
        // installing Windows reached a hundred per cent with an almost empty
        // directory.
        using Machine machine = WithFloppy();
        machine.DiskServices.NotifyMediaChanged(0);

        Assert.False(ReadFirstSector(machine));
    }

    [Fact]
    public void TheReadAfterThatSucceeds()
    {
        // One refusal, not a drive that has stopped working. Having been told
        // once, DOS re-reads the disk's layout and carries on.
        using Machine machine = WithFloppy();
        machine.DiskServices.NotifyMediaChanged(0);

        Assert.False(ReadFirstSector(machine));
        Assert.True(ReadFirstSector(machine));
        Assert.False(ReportsChange(machine));
    }

    [Fact]
    public void TheWholeSequenceDosPerformsOnASwapWorks()
    {
        // What DOS actually does, in order, when the user swaps a disk: ask
        // whether it changed, try to read, get refused, reset the controller,
        // read again. Every step has to answer the way the hardware does, or
        // the installer that depends on it goes wrong in some way that is
        // hard to trace back here.
        using Machine machine = WithFloppy();
        machine.DiskServices.NotifyMediaChanged(0);

        Assert.True(ReportsChange(machine));
        Assert.False(ReadFirstSector(machine));

        // Resetting the controller is not the same as having looked at the
        // new disk, so it must not clear anything by itself.
        machine.Cpu.State.SetReg8(4, 0x00);
        machine.Cpu.State.SetReg8(2, 0x00);
        machine.DiskServices.Handle(machine.Cpu);
        Assert.Equal(0, machine.Cpu.State.GetReg8(4));

        Assert.True(ReadFirstSector(machine));
        Assert.False(ReportsChange(machine));
    }

    [Fact]
    public void EachDriveKeepsItsOwnLine()
    {
        // Swapping the disk in A: says nothing about B:, and a shared flag
        // would have the second drive claiming a change that never happened.
        using Machine machine = WithFloppy();
        machine.AttachDisk(new DiskImage(new MemoryStream(new byte[1440 * 1024]),
                                         0x01, 80, 2, 18));

        machine.DiskServices.NotifyMediaChanged(0);

        machine.Cpu.State.SetReg8(4, 0x16);
        machine.Cpu.State.SetReg8(2, 0x01);   // drive B:
        machine.DiskServices.Handle(machine.Cpu);

        Assert.NotEqual(0x06, machine.Cpu.State.GetReg8(4));
    }
}
