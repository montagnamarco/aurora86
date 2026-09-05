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
}
