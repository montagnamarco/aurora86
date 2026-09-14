using Aurora386.Core;
using Aurora386.Core.Configuration;

namespace Aurora386.Tests;

/// <summary>
/// The experimental 1996 ViRGE must not use the older Trio machine snapshot
/// format, which omits both card identity and the S3d engine registers.
/// Rejection must leave the running guest untouched.
/// </summary>
public sealed class ExperimentalVideoSnapshotTests
{
    [Fact]
    public void CapturingVirgeDoesNotProduceAnIncompleteMachineSnapshot()
    {
        using var machine = new Machine(videoModel: VmVideoModel.S3Virge325);
        machine.Memory.WriteByte(0x1000, 0xA5);
        machine.Cpu.State.Eip = 0x1234;
        machine.GuestHasItsOwnCompactDiscExtensions = true;

        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => MachineSnapshot.Capture(machine));

        Assert.Contains("ViRGE", error.Message);
        Assert.Equal(0xA5, machine.Memory.ReadByte(0x1000));
        Assert.Equal(0x1234u, machine.Cpu.State.Eip);
        Assert.True(machine.GuestHasItsOwnCompactDiscExtensions);
    }

    [Fact]
    public void RestoringTrioIntoVirgeIsRejectedBeforeGuestStateChanges()
    {
        using var source = new Machine();
        source.Memory.WriteByte(0x1000, 0x11);
        source.Cpu.State.Eip = 0x5678;
        MachineSnapshot snapshot = MachineSnapshot.Capture(source);

        using var target = new Machine(videoModel: VmVideoModel.S3Virge325);
        target.Memory.A20Enabled = true;
        target.Memory.WriteByte(0x1000, 0xA5);
        target.Cpu.State.Eip = 0x1234;
        target.GuestHasItsOwnCompactDiscExtensions = true;
        target.VideoMemory.WriteLinear(0, 0xCC);
        target.Pci.WriteConfiguration(0x0020, 0x10, 4, 0x74000000);
        byte[] videoRegisters = target.VideoRegisters.CaptureRegisters();
        byte[] pciState = target.Pci.Capture();

        Assert.Throws<NotSupportedException>(() => snapshot.RestoreTo(target));

        Assert.True(target.GuestHasItsOwnCompactDiscExtensions);
        Assert.True(target.Memory.A20Enabled);
        Assert.Equal(0xA5, target.Memory.ReadByte(0x1000));
        Assert.Equal(0x1234u, target.Cpu.State.Eip);
        Assert.Equal(0xCC, target.VideoMemory.ReadLinear(0));
        Assert.Equal(videoRegisters, target.VideoRegisters.CaptureRegisters());
        Assert.Equal(pciState, target.Pci.Capture());
    }
}
