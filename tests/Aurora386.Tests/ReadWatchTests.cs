using Aurora386.Core;

namespace Aurora386.Tests;

/// <summary>
/// Read watchpoints, which take a page out of the bus's fast table so that
/// reads of it can be looked at.
///
/// The property that matters is that the page goes on being ordinary memory
/// in every other respect. A watch that made a page unwritable, or unreadable
/// past the watched byte, or that stayed in force after being cleared, would
/// change the machine being watched -- and a debugger that alters what it
/// observes is worse than none.
/// </summary>
public class ReadWatchTests
{
    private static Machine Fresh()
    {
        var machine = new Machine();
        machine.RunInstructions(200);
        return machine;
    }

    [Fact]
    public void AReadInsideTheRangeIsReported()
    {
        using Machine machine = Fresh();
        machine.Memory.WriteByte(0x5000, 0x42);

        var seen = new List<uint>();
        machine.Memory.ReadWatched += seen.Add;
        machine.Memory.SetReadWatchRanges([(0x5000u, 4u)]);

        Assert.Equal(0x42, machine.Memory.ReadByte(0x5000));
        Assert.Equal(new uint[] { 0x5000 }, seen);

        // The same page, outside the range: the page is slow now, but only
        // the range is reported.
        machine.Memory.ReadByte(0x5010);
        Assert.Single(seen);
    }

    [Fact]
    public void AWatchedPageIsStillWritable()
    {
        using Machine machine = Fresh();
        machine.Memory.SetReadWatchRanges([(0x5000u, 1u)]);

        machine.Memory.WriteByte(0x5001, 0x77);        // the page, not the byte
        machine.Memory.WriteByte(0x5000, 0x99);        // the watched byte itself

        Assert.Equal(0x77, machine.Memory.ReadByte(0x5001));
        Assert.Equal(0x99, machine.Memory.ReadByte(0x5000));
    }

    /// <summary>
    /// A wide read that merely crosses the watched byte still reports it,
    /// and still returns the right value -- which means the slow path has to
    /// assemble the word from bytes rather than give up on a page it does
    /// not recognise.
    /// </summary>
    [Fact]
    public void ADwordCrossingTheWatchedByteIsReportedAndCorrect()
    {
        using Machine machine = Fresh();
        machine.Memory.WriteDword(0x5000, 0x04030201);

        var seen = new List<uint>();
        machine.Memory.ReadWatched += seen.Add;
        machine.Memory.SetReadWatchRanges([(0x5002u, 1u)]);

        Assert.Equal(0x04030201u, machine.Memory.ReadDword(0x5000));
        Assert.Equal(new uint[] { 0x5002 }, seen);
    }

    /// <summary>
    /// ROM under a watch stays ROM: the watch changes which path an access
    /// takes, not what the page is.
    /// </summary>
    [Fact]
    public void AWatchedRomPageStaysReadOnly()
    {
        using Machine machine = Fresh();
        byte before = machine.Memory.ReadByte(0xF0000);

        machine.Memory.SetReadWatchRanges([(0xF0000u, 1u)]);
        machine.Memory.WriteByte(0xF0000, (byte)(before ^ 0xFF));

        Assert.Equal(before, machine.Memory.ReadByte(0xF0000));
    }

    [Fact]
    public void ClearingTheWatchGivesThePageBack()
    {
        using Machine machine = Fresh();
        machine.Memory.WriteByte(0x5000, 0x42);

        var seen = new List<uint>();
        machine.Memory.ReadWatched += seen.Add;
        machine.Memory.SetReadWatchRanges([(0x5000u, 1u)]);
        machine.Memory.ReadByte(0x5000);
        Assert.Single(seen);

        machine.Memory.SetReadWatchRanges([]);
        Assert.Equal(0x42, machine.Memory.ReadByte(0x5000));
        Assert.Single(seen);                              // no longer reported
    }
}
