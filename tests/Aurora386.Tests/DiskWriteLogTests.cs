using Aurora386.Core;
using Aurora386.Core.Firmware;
using Aurora386.Core.Firmware.Fpu;
using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Diagnostics;
using Aurora386.Core.Cpu;

namespace Aurora386.Tests;

/// <summary>
/// Tests for the write log, which turns written sectors back into the names of
/// the files that own them.
///
/// The test writes to a sector whose owner is known from the volume's own
/// tables, so a log that resolved it wrongly -- or resolved everything to the
/// same name -- would fail rather than look plausible.
/// </summary>
public class DiskWriteLogTests
{
    private static string TestImage =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                     "images", "hdd.img.prima-di-win386");

    /// <summary>
    /// Writes one sector at the given place and returns what the log recorded.
    /// </summary>
    private static string[] WriteAt(int cylinder, int head, int sector)
    {
        string logPath = Path.GetTempFileName();
        string copy = Path.GetTempFileName();

        try
        {
            File.Copy(TestImage, copy, overwrite: true);

            using var machine = new Machine();
            machine.AttachDisk(DiskImage.OpenHardDisk(copy));
            machine.RunInstructions(200);

            DiskImage disk = machine.DiskServices.GetDrive(0x80)!;
            using (DiskWriteLog.Attach(machine, disk, logPath))
            {
                machine.Cpu.State.Halted = false;
                machine.Cpu.SetSegment(SegReg.Es, 0);
                machine.Cpu.State.Bx = 0x6000;
                machine.Cpu.State.SetReg8(4, 0x03);            // AH: write
                machine.Cpu.State.SetReg8(0, 1);               // AL: one sector
                machine.Cpu.State.SetReg8(5, (byte)cylinder);  // CH
                machine.Cpu.State.SetReg8(1, (byte)sector);    // CL
                machine.Cpu.State.SetReg8(6, (byte)head);      // DH
                machine.Cpu.State.SetReg8(2, 0x80);            // DL: first hard disk

                machine.DiskServices.Handle(machine.Cpu);
            }

            return File.ReadAllLines(logPath).Where(l => !l.StartsWith('#')).ToArray();
        }
        finally
        {
            File.Delete(logPath);
            File.Delete(copy);
        }
    }

    [Fact]
    public void AWriteIsReportedUnderTheNameOfTheFileThatOwnsThoseSectors()
    {
        // The reference image is a build artefact rather than a checked-in
        // fixture, so where none has been built there is nothing to test.
        if (!File.Exists(TestImage)) return;

        // C:\COMMAND.COM starts at cylinder 0, head 10, sector 45.
        Assert.Equal(new[] { @"C:\COMMAND.COM" }, WriteAt(0, 10, 45));
    }

    /// <summary>
    /// CONFIG.SYS and AUTOEXEC.BAT sit in adjacent clusters, so a resolver
    /// that is off by one -- or that reports whatever file it looked at last
    /// -- names the wrong one here rather than merely being imprecise.
    /// </summary>
    [Fact]
    public void AdjacentFilesAreToldApart()
    {
        if (!File.Exists(TestImage)) return;

        Assert.Equal(new[] { @"C:\CONFIG.SYS" }, WriteAt(13, 0, 51));
        Assert.Equal(new[] { @"C:\AUTOEXEC.BAT" }, WriteAt(13, 0, 59));
    }

    [Fact]
    public void AWriteToTheVolumesOwnTablesIsNotReportedAsAFile()
    {
        if (!File.Exists(TestImage)) return;

        // Sector 1 of the volume is inside the reserved area and belongs to no
        // file; reporting a name for it would be an invention.
        Assert.Empty(WriteAt(0, 0, 2));
    }

    /// <summary>
    /// A log with no file still hands every line to whoever is listening,
    /// which is what a debugger panel needs. The sector is one the tests
    /// above already know belongs to COMMAND.COM, so the line is checked in
    /// full rather than merely counted.
    /// </summary>
    [Fact]
    public void ALogWithoutAFileStillRaisesEveryLine()
    {
        string copy = Path.GetTempFileName();
        try
        {
            File.Copy(TestImage, copy, overwrite: true);

            using var machine = new Machine();
            machine.AttachDisk(DiskImage.OpenHardDisk(copy));
            machine.RunInstructions(200);

            DiskImage disk = machine.DiskServices.GetDrive(0x80)!;
            var seen = new List<string>();
            using DiskWriteLog log = DiskWriteLog.Attach(machine, disk);
            log.LineWritten += seen.Add;

            machine.Cpu.State.Halted = false;
            machine.Cpu.SetSegment(SegReg.Es, 0);
            machine.Cpu.State.Bx = 0x6000;
            machine.Cpu.State.SetReg8(4, 0x03);            // AH: write
            machine.Cpu.State.SetReg8(0, 1);               // AL: one sector
            machine.Cpu.State.SetReg8(5, 0);               // CH: cylinder 0
            machine.Cpu.State.SetReg8(1, 45);              // CL: sector 45
            machine.Cpu.State.SetReg8(6, 10);              // DH: head 10 -- COMMAND.COM lives here
            machine.Cpu.State.SetReg8(2, 0x80);            // DL: first hard disk
            machine.DiskServices.Handle(machine.Cpu);

            Assert.Equal(@"C:\COMMAND.COM", Assert.Single(seen));
        }
        finally { File.Delete(copy); }
    }
}
