using Aurora86.Core;
using Aurora86.Core.Firmware;
using Aurora86.Core.Firmware.Fpu;
using Aurora86.Core.Devices.Storage;
using Aurora86.Core.Diagnostics;
using Aurora86.Core.Cpu;

namespace Aurora86.Tests;

/// <summary>
/// Tests for the DOS call log, which records the calls that move files about.
/// </summary>
public class DosCallLogTests
{
    /// <summary>
    /// Runs one INT 21h with the registers set up as DOS expects, and returns
    /// what the log recorded.
    /// </summary>
    private static string[] Issue(Action<Machine> prepare)
    {
        string path = Path.GetTempFileName();
        try
        {
            using var machine = new Machine();
            machine.RunInstructions(200);
            machine.Cpu.State.Halted = false;

            using (DosCallLog.Attach(machine, path))
            {
                machine.Cpu.SetSegment(SegReg.Cs, 0);
                machine.Cpu.SetSegment(SegReg.Ss, 0);
                machine.Cpu.State.Esp = 0x7000;
                machine.Cpu.State.Eip = 0x2000;

                prepare(machine);

                machine.Memory.WriteByte(0x2000, 0xCD);   // INT
                machine.Memory.WriteByte(0x2001, 0x21);
                machine.Cpu.Step();
            }

            return File.ReadAllLines(path).Where(l => !l.StartsWith('#')).ToArray();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void PutString(Machine machine, uint address, string text)
    {
        for (int i = 0; i < text.Length; i++)
            machine.Memory.WriteByte(address + (uint)i, (byte)text[i]);
        machine.Memory.WriteByte(address + (uint)text.Length, 0);
    }

    [Fact]
    public void ARenameIsRecordedWithBothNames()
    {
        string[] lines = Issue(machine =>
        {
            PutString(machine, 0x5000, @"C:\WINDOWS\W8D4OP2T.A4G");
            PutString(machine, 0x5100, @"C:\WINDOWS\WIN.COM");

            machine.Cpu.SetSegment(SegReg.Ds, 0x0500);   // DS:0 is linear 0x5000
            machine.Cpu.SetSegment(SegReg.Es, 0x0510);   // ES:0 is linear 0x5100
            machine.Cpu.State.Dx = 0;
            machine.Cpu.State.Di = 0;
            machine.Cpu.State.SetReg8(4, 0x56);          // AH: rename
        });

        Assert.Single(lines);
        Assert.Equal(@"rename   C:\WINDOWS\W8D4OP2T.A4G -> C:\WINDOWS\WIN.COM", lines[0]);
    }

    [Fact]
    public void LaunchingAProgramIsRecorded()
    {
        string[] lines = Issue(machine =>
        {
            PutString(machine, 0x5000, @"C:\WINDOWS\WIN.COM");
            machine.Cpu.SetSegment(SegReg.Ds, 0x0500);
            machine.Cpu.State.Dx = 0;
            machine.Cpu.State.SetReg8(4, 0x4B);          // AH: exec
            machine.Cpu.State.SetReg8(0, 0x00);          // AL: load and run
        });

        Assert.Single(lines);
        Assert.StartsWith(@"exec     C:\WINDOWS\WIN.COM", lines[0]);
    }

    [Fact]
    public void CallsThatDoNotMoveFilesAreNotRecorded()
    {
        // AH=30 asks the DOS version and says nothing about files; recording
        // it would bury the calls that matter under thousands that do not.
        string[] lines = Issue(machine => machine.Cpu.State.SetReg8(4, 0x30));

        Assert.Empty(lines);
    }

    /// <summary>
    /// A log with no file still hands every line to whoever is listening.
    ///
    /// This is what a debugger panel needs: the calls as they happen, without
    /// a file being written somewhere nobody chose. The exit call is used
    /// because it is the simplest to provoke -- one register, no memory.
    /// </summary>
    [Fact]
    public void ALogWithoutAFileStillRaisesEveryLine()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);
        machine.Cpu.State.Halted = false;

        var seen = new List<string>();
        using DosCallLog log = DosCallLog.Attach(machine);
        log.LineWritten += seen.Add;

        machine.Cpu.SetSegment(SegReg.Cs, 0);
        machine.Cpu.SetSegment(SegReg.Ss, 0);
        machine.Cpu.State.Esp = 0x7000;
        machine.Cpu.State.Eip = 0x2000;
        machine.Cpu.State.Eax = 0x4C07;             // exit, code 7
        machine.Memory.WriteByte(0x2000, 0xCD);
        machine.Memory.WriteByte(0x2001, 0x21);
        machine.Cpu.Step();

        string line = Assert.Single(seen);
        Assert.StartsWith("exit", line);
        Assert.Contains("7", line);
    }
}
