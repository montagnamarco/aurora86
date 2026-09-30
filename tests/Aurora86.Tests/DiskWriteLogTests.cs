using Aurora86.Core;
using Aurora86.Core.Devices.Storage;
using Aurora86.Core.Diagnostics;
using Aurora86.Core.Cpu;

namespace Aurora86.Tests;

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
    /// <summary>
    /// Writes one absolute sector and returns what the log recorded.
    /// </summary>
    private static string[] WriteAt(SyntheticFatDisk fixture, long linear)
    {
        string logPath = Path.GetTempFileName();
        try
        {
            using var machine = new Machine();
            machine.AttachDisk(fixture.OpenImage());
            machine.RunInstructions(200);

            DiskImage disk = machine.DiskServices.GetDrive(0x80)!;
            using (DiskWriteLog.Attach(machine, disk, logPath))
            {
                machine.Cpu.State.Halted = false;
                machine.Cpu.SetSegment(SegReg.Es, 0);
                machine.Cpu.State.Bx = 0x6000;
                machine.Cpu.State.SetReg8(4, 0x03);            // AH: write
                machine.Cpu.State.SetReg8(0, 1);               // AL: one sector
                (int cylinder, int head, int sector) = fixture.ToChs(linear);
                machine.Cpu.State.SetReg8(5, (byte)cylinder);  // CH
                machine.Cpu.State.SetReg8(1, (byte)sector);    // CL
                machine.Cpu.State.SetReg8(6, (byte)head);      // DH
                machine.Cpu.State.SetReg8(2, 0x80);            // DL: first hard disk

                machine.DiskServices.Handle(machine.Cpu);
            }

            return File.ReadAllLines(logPath).Where(line => !line.StartsWith('#')).ToArray();
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public void AWriteIsReportedUnderTheNameOfTheFileThatOwnsThoseSectors()
    {
        using var fixture = new SyntheticFatDisk();

        Assert.Equal(new[] { @"C:\COMMAND.COM" },
                     WriteAt(fixture, fixture.ClusterToSector(2)));
    }

    /// <summary>
    /// CONFIG.SYS and AUTOEXEC.BAT sit in adjacent clusters, so a resolver
    /// that is off by one -- or that reports whatever file it looked at last
    /// -- names the wrong one here rather than merely being imprecise.
    /// </summary>
    [Fact]
    public void AdjacentFilesAreToldApart()
    {
        using var fixture = new SyntheticFatDisk();

        Assert.Equal(new[] { @"C:\CONFIG.SYS" }, WriteAt(fixture, fixture.ClusterToSector(6)));
        Assert.Equal(new[] { @"C:\AUTOEXEC.BAT" }, WriteAt(fixture, fixture.ClusterToSector(7)));
    }

    [Fact]
    public void AWriteToTheVolumesOwnTablesIsNotReportedAsAFile()
    {
        using var fixture = new SyntheticFatDisk();

        // The first FAT sector belongs to the volume's metadata and to no
        // file; reporting a name for it would be an invention.
        Assert.Empty(WriteAt(fixture,
            fixture.VolumeStart + fixture.BootParameters.FirstFatSector));
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
        using var fixture = new SyntheticFatDisk();

        using var machine = new Machine();
        machine.AttachDisk(fixture.OpenImage());
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
        (int cylinder, int head, int sector) = fixture.ToChs(fixture.ClusterToSector(2));
        machine.Cpu.State.SetReg8(5, (byte)cylinder);  // CH
        machine.Cpu.State.SetReg8(1, (byte)sector);    // CL
        machine.Cpu.State.SetReg8(6, (byte)head);      // DH
        machine.Cpu.State.SetReg8(2, 0x80);            // DL: first hard disk
        machine.DiskServices.Handle(machine.Cpu);

        Assert.Equal(@"C:\COMMAND.COM", Assert.Single(seen));
    }
}
