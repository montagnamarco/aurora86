using Aurora386.Core;
using Aurora386.Core.Cpu;

namespace Aurora386.Tests;

/// <summary>
/// Tests for machine snapshots.
///
/// The property that matters is not that the fields come back — it is that the
/// machine goes on to do the same thing. A snapshot that restored every
/// register but lost a byte of RAM would pass a field-by-field comparison and
/// still diverge on the next instruction, so these tests run the machine on
/// after restoring and compare where it ends up.
/// </summary>
public class MachineSnapshotTests
{
    /// <summary>A machine parked on a small program in real mode.</summary>
    private static Machine Build()
    {
        var machine = new Machine();
        machine.RunInstructions(200);
        machine.Cpu.State.Halted = false;

        machine.Cpu.SetSegment(SegReg.Cs, 0);
        machine.Cpu.SetSegment(SegReg.Ds, 0);
        machine.Cpu.SetSegment(SegReg.Ss, 0);
        machine.Cpu.State.Esp = 0x7000;
        machine.Cpu.State.Eip = 0x2000;

        // INC AX, written into memory eight times: a program whose progress is
        // visible in a single register.
        for (uint i = 0; i < 8; i++) machine.Memory.WriteByte(0x2000 + i, 0x40);

        return machine;
    }

    [Fact]
    public void ExecutionCarriesOnIdenticallyAfterARestore()
    {
        using Machine machine = Build();

        machine.Cpu.Step();
        machine.Cpu.Step();
        MachineSnapshot snapshot = MachineSnapshot.Capture(machine);

        // Run on, then come back and run the same instructions again.
        machine.Cpu.Step();
        machine.Cpu.Step();
        uint firstTime = machine.Cpu.State.Eax;
        uint eipFirstTime = machine.Cpu.State.Eip;

        snapshot.RestoreTo(machine);
        machine.Cpu.Step();
        machine.Cpu.Step();

        Assert.Equal(firstTime, machine.Cpu.State.Eax);
        Assert.Equal(eipFirstTime, machine.Cpu.State.Eip);
    }

    [Fact]
    public void MemoryWrittenAfterTheSnapshotIsUndoneByRestoring()
    {
        using Machine machine = Build();
        machine.Memory.WriteByte(0x5000, 0xAA);

        MachineSnapshot snapshot = MachineSnapshot.Capture(machine);

        machine.Memory.WriteByte(0x5000, 0xBB);
        Assert.Equal(0xBB, machine.Memory.ReadByte(0x5000));

        snapshot.RestoreTo(machine);
        Assert.Equal(0xAA, machine.Memory.ReadByte(0x5000));
    }

    [Fact]
    public void TheProcessorsWholeStateComesBack()
    {
        using Machine machine = Build();

        machine.Cpu.State.Eax = 0x11223344;
        machine.Cpu.State.Ecx = 0x55667788;
        machine.Cpu.State.GdtBase = 0x9000;
        machine.Cpu.State.GdtLimit = 0x3F;

        MachineSnapshot snapshot = MachineSnapshot.Capture(machine);

        machine.Cpu.State.Eax = 0;
        machine.Cpu.State.Ecx = 0;
        machine.Cpu.State.GdtBase = 0;
        machine.Cpu.State.GdtLimit = 0;

        snapshot.RestoreTo(machine);

        Assert.Equal(0x11223344u, machine.Cpu.State.Eax);
        Assert.Equal(0x55667788u, machine.Cpu.State.Ecx);
        Assert.Equal(0x9000u, machine.Cpu.State.GdtBase);
        Assert.Equal(0x3F, machine.Cpu.State.GdtLimit);
    }

    /// <summary>
    /// A segment's cached limit is the part of the state that a naive snapshot
    /// forgets, and forgetting it silently breaks unreal mode: the descriptor
    /// is gone, so the wide limit has no way back.
    /// </summary>
    [Fact]
    public void ASegmentsHiddenLimitSurvives()
    {
        using Machine machine = Build();
        machine.Cpu.State.Segments[(int)SegReg.Es].Limit = 0xFFFFFFFF;

        MachineSnapshot snapshot = MachineSnapshot.Capture(machine);
        machine.Cpu.State.Segments[(int)SegReg.Es].Limit = 0xFFFF;

        snapshot.RestoreTo(machine);

        Assert.Equal(0xFFFFFFFFu, machine.Cpu.State.Es.Limit);
    }

    [Fact]
    public void ASnapshotSurvivesTheTripThroughAFile()
    {
        string path = Path.GetTempFileName();
        try
        {
            using Machine machine = Build();
            machine.Cpu.State.Eax = 0xDEADBEEF;
            machine.Memory.WriteByte(0x5000, 0x77);
            machine.Cpu.State.Segments[(int)SegReg.Es].Limit = 0xFFFFFFFF;

            MachineSnapshot.Capture(machine).Save(path);

            machine.Cpu.State.Eax = 0;
            machine.Memory.WriteByte(0x5000, 0);
            machine.Cpu.State.Segments[(int)SegReg.Es].Limit = 0xFFFF;

            MachineSnapshot.Load(path).RestoreTo(machine);

            Assert.Equal(0xDEADBEEFu, machine.Cpu.State.Eax);
            Assert.Equal(0x77, machine.Memory.ReadByte(0x5000));
            Assert.Equal(0xFFFFFFFFu, machine.Cpu.State.Es.Limit);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The interrupt masks survive a snapshot.
    ///
    /// This is the test that would have saved a long investigation. A
    /// snapshot that omits the interrupt controllers restores them to their
    /// power-on state, which is every line masked -- and a guest resumed from
    /// it never receives another timer tick. What that looks like from
    /// outside is a guest that has stopped part-way through whatever it was
    /// doing, which invites the conclusion that whatever it was doing is
    /// broken. It was the snapshot.
    /// </summary>
    [Fact]
    public void TheInterruptMasksSurviveASnapshot()
    {
        using Machine machine = Build();

        // The masks a guest sets when it wants the timer and the keyboard
        // and nothing else.
        machine.Io.WriteByte(0x21, 0xFC);
        machine.Io.WriteByte(0xA1, 0xFF);

        MachineSnapshot snapshot = MachineSnapshot.Capture(machine);

        // Mask everything, the way a reset would.
        machine.Io.WriteByte(0x21, 0xFF);
        Assert.Equal(0xFF, machine.Interrupts.Master.Registers.Mask);

        snapshot.RestoreTo(machine);

        Assert.Equal(0xFC, machine.Interrupts.Master.Registers.Mask);
    }

    /// <summary>
    /// And a machine restored from a snapshot still gets its timer ticks.
    ///
    /// The masks coming back is the mechanism; this is the thing that
    /// actually matters, and it is checked separately because a snapshot
    /// could restore every byte of the controller and still leave the timer
    /// counting from a divisor the guest never asked for.
    /// </summary>
    [Fact]
    public void ARestoredMachineStillReceivesTimerInterrupts()
    {
        using Machine machine = Build();

        machine.Io.WriteByte(0x21, 0xFE);      // IRQ 0 through, the rest masked

        MachineSnapshot snapshot = MachineSnapshot.Capture(machine);
        machine.Io.WriteByte(0x21, 0xFF);
        snapshot.RestoreTo(machine);

        machine.Timer.Advance(100_000);

        Assert.True(machine.Interrupts.HasPendingInterrupt,
            "a restored machine must still hear its timer");
    }
}

public class MachineSnapshotRedirectorTests
{
    /// <summary>
    /// The redirector's attachment to DOS travels with the snapshot. Without
    /// it a restored guest at the DOS prompt has a hooked INT 2Fh vector in
    /// memory and a redirector on this side that knows nothing, so every
    /// call about a shared folder is declined: DIR says "invalid function"
    /// on a drive that was working when the snapshot was taken.
    /// </summary>
    [Fact]
    public void TheRedirectorsAttachmentSurvivesASnapshot()
    {
        string root = Path.Combine(Path.GetTempPath(), "aurora-snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "stato.bin");
        try
        {
            using var source = new Aurora386.Core.Machine();
            source.RunInstructions(200);
            source.Redirector.Mount(4, new Aurora386.Core.Dos.HostDirectoryFileSystem(root));
            source.Redirector.AttachToDos(0x1480, 0xE1E0, 26);

            // A vector to displace, standing in for DOS's own handler.
            source.Memory.WriteWord(0x2F * 4, 0x1234);
            source.Memory.WriteWord(0x2F * 4 + 2, 0x0116);
            source.HookMultiplexForRedirector();

            Aurora386.Core.MachineSnapshot.Capture(source).Save(file);

            using var target = new Aurora386.Core.Machine();
            target.RunInstructions(200);
            target.Redirector.Mount(4, new Aurora386.Core.Dos.HostDirectoryFileSystem(root));
            Aurora386.Core.MachineSnapshot.Load(file).RestoreTo(target);

            Assert.True(target.Redirector.IsInstalled);
            Assert.Equal(0x1480u, target.Redirector.SwappableDataArea);
            Assert.Equal(0xE1E0u, target.Redirector.CurrentDirectoryStructure);
            Assert.Equal(26, target.Redirector.DriveCount);

            // The vector in memory is ours, and the jump behind it still
            // knows where DOS's handler was.
            Assert.Equal(Aurora386.Core.Firmware.BiosRom.Entry.RedirectorMultiplex, target.Memory.ReadWord(0x2F * 4));
            Assert.Equal(0x1234, target.Memory.ReadWord(Aurora386.Core.Firmware.BiosRom.Base + Aurora386.Core.Firmware.BiosRom.Entry.PreviousMultiplexVector));
            Assert.Equal(0x0116, target.Memory.ReadWord(Aurora386.Core.Firmware.BiosRom.Base + Aurora386.Core.Firmware.BiosRom.Entry.PreviousMultiplexVector + 2));

            // And the drive is marked in the restored CDS.
            ushort flags = target.Memory.ReadWord(0xE1E0 + 4 * 88 + 0x43);
            Assert.Equal(0xC000, flags & 0xC000);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
