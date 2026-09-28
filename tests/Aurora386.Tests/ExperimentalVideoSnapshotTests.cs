using Aurora386.Core;
using Aurora386.Core.Configuration;

namespace Aurora386.Tests;

/// <summary>
/// The experimental 1996 ViRGE participates in machine snapshots only when
/// topology and drawing-engine state identify that card explicitly.
/// </summary>
public sealed class ExperimentalVideoSnapshotTests
{
    [Fact]
    public void CapturingVirgeProducesARestorableMachineSnapshot()
    {
        using var source = new Machine(videoModel: VmVideoModel.S3Virge325);
        source.Memory.WriteByte(0x1000, 0xA5);
        source.Cpu.State.Eip = 0x1234;
        source.VideoMemory.WriteLinear(0, 0xCC);
        MachineSnapshot snapshot = MachineSnapshot.Capture(source);

        using var target = new Machine(videoModel: VmVideoModel.S3Virge325);
        snapshot.RestoreTo(target);

        Assert.Equal(0xA5, target.Memory.ReadByte(0x1000));
        Assert.Equal(0x1234u, target.Cpu.State.Eip);
        Assert.Equal(0xCC, target.VideoMemory.ReadLinear(0));
    }
}
