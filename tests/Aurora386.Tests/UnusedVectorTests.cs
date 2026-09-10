using Aurora386.Core;

namespace Aurora386.Tests;

/// <summary>
/// What a vector with no handler of its own points at.
///
/// A real BIOS fills all 256 of them, because software reads words out of
/// that table and uses them as segment values even for vectors nobody
/// handles. But the filler has to be a bare IRET, not the stub that
/// acknowledges the interrupt controllers: an end-of-interrupt sent by a
/// software interrupt clears whichever hardware line really was in service,
/// and a vector that answers like a working handler tells a driver that the
/// service it wants to provide is already there.
/// </summary>
public class UnusedVectorTests
{
    private static Machine Started()
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        machine.Cpu.State.Halted = false;
        return machine;
    }

    private static (ushort Segment, ushort Offset) Vector(Machine machine, byte vector)
        => (machine.Memory.ReadWord((uint)(vector * 4 + 2)),
            machine.Memory.ReadWord((uint)(vector * 4)));

    /// <summary>
    /// INT 67h is the case that showed this up. EMM386 calls it to find out
    /// whether an EMS manager is already installed; a filler that behaved
    /// like a handler was enough to confuse it.
    /// </summary>
    [Fact]
    public void AnUnclaimedVectorIsAPlainIret()
    {
        using Machine machine = Started();

        (ushort segment, ushort offset) = Vector(machine, 0x67);
        Assert.Equal(0xF000, segment);

        // The byte it points at is IRET and nothing else.
        Assert.Equal(0xCF, machine.Memory.ReadByte((uint)((segment << 4) + offset)));
    }

    [Fact]
    public void AnUnclaimedHardwareLineStillAcknowledgesTheControllers()
    {
        // The dummy IRQ entry is a different address, and it is not an
        // immediate IRET: it has the end-of-interrupt writes to make first.
        using Machine machine = Started();

        (ushort unusedSegment, ushort unusedOffset) = Vector(machine, 0x67);
        (ushort irqSegment, ushort irqOffset) = Vector(machine, 0x0B);   // IRQ 3, unclaimed

        Assert.NotEqual(unusedOffset, irqOffset);
        Assert.Equal(unusedSegment, irqSegment);
        Assert.NotEqual(0xCF, machine.Memory.ReadByte((uint)((irqSegment << 4) + irqOffset)));
    }

    [Fact]
    public void NoVectorIsLeftAtZero()
    {
        // DOS reads words out of this table and uses them as segments even
        // for vectors it does not handle; a zero there is not "free vector"
        // but a pointer into the interrupt table itself.
        using Machine machine = Started();

        for (int vector = 0; vector < 256; vector++)
        {
            (ushort segment, ushort offset) = Vector(machine, (byte)vector);
            Assert.True(segment != 0 || offset != 0, $"vector {vector:X2} is zero");
        }
    }
}
