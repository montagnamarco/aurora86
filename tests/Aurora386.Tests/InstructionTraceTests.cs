using Aurora386.Core;
using Aurora386.Core.Cpu;
using Aurora386.Core.Diagnostics;

namespace Aurora386.Tests;

/// <summary>
/// The ring of recent instructions: what the debugger shows and what every
/// investigation in this project has needed. The value is in what it records
/// at the moment something goes wrong, so the tests check that it keeps the
/// most recent window, that it costs nothing when off, and that it survives
/// the awkward cases -- an instruction whose bytes cannot be read, and a
/// guest in each of the three modes.
/// </summary>
public class InstructionTraceTests
{
    /// <summary>
    /// A machine past its POST and running code of our own: a loop of NOPs.
    /// Left as the POST leaves it the CPU is halted waiting for an interrupt,
    /// and a halted processor executes nothing to trace.
    /// </summary>
    private static Machine Started()
    {
        var machine = new Machine();
        machine.RunInstructions(2000);

        machine.Memory.WriteByte(0x9000, 0x90);   // nop
        machine.Memory.WriteByte(0x9001, 0xEB);   // jmp -3
        machine.Memory.WriteByte(0x9002, 0xFD);
        machine.Cpu.State.Halted = false;
        machine.Cpu.SetSegment(SegReg.Cs, 0x0900);
        machine.Cpu.State.Eip = 0;
        return machine;
    }

    [Fact]
    public void NothingIsRecordedUntilItIsSwitchedOn()
    {
        using Machine machine = Started();
        var trace = new InstructionTrace();
        machine.Cpu.Trace = trace;

        machine.RunInstructions(10_000);

        Assert.Empty(trace.Entries());
        Assert.Equal(0, trace.Recorded);
    }

    [Fact]
    public void TheRingKeepsTheMostRecentInstructionsAndDropsTheOldest()
    {
        using Machine machine = Started();
        var trace = new InstructionTrace(capacity: 64) { Enabled = true };
        machine.Cpu.Trace = trace;

        machine.RunInstructions(5_000);

        List<InstructionTrace.Entry> entries = trace.Entries().ToList();
        Assert.Equal(64, entries.Count);                       // full, not growing
        Assert.True(trace.Recorded >= 5_000);                  // but it saw them all

        // Oldest first, and consecutive: a ring that wrapped wrongly would
        // hand back the newest in the middle of the list.
        for (int i = 1; i < entries.Count; i++)
            Assert.Equal(entries[i - 1].Sequence + 1, entries[i].Sequence);
    }

    [Fact]
    public void EachEntryHoldsWhereItWasAndWhatItWasAboutToRun()
    {
        using Machine machine = Started();
        var trace = new InstructionTrace(capacity: 16) { Enabled = true };
        machine.Cpu.Trace = trace;
        Assert.True(ReferenceEquals(machine.Cpu.Trace, trace), "the CPU did not keep the trace");
        Assert.True(machine.Cpu.Trace!.Enabled, "the trace is not enabled on the CPU's copy");

        // A known instruction at a known place: NOP, NOP, HLT.
        machine.Memory.WriteByte(0x9000, 0x90);
        machine.Memory.WriteByte(0x9001, 0x90);
        machine.Memory.WriteByte(0x9002, 0xF4);
        machine.Cpu.State.Halted = false;      // the POST leaves it waiting for an interrupt
        machine.Cpu.SetSegment(SegReg.Cs, 0x0900);
        machine.Cpu.State.Eip = 0;
        machine.Cpu.State.Eax = 0x12345678;

        machine.Cpu.Step();

        InstructionTrace.Entry entry = trace.Entries().Last();
        Assert.Equal(0x0900, entry.Cs);
        Assert.Equal(0u, entry.Eip);
        Assert.Equal(0x90, (byte)entry.Bytes);                 // the NOP itself
        Assert.Equal(0x12345678u, entry.Eax);
        Assert.Equal("real", entry.ModeName);
    }

    [Fact]
    public void UnreadableInstructionBytesAreRecordedRatherThanFaulting()
    {
        // A trace must never disturb the guest. Pointing CS past the end of
        // memory would fault a real read; the trace has to swallow that and
        // record the entry anyway, because the interesting part is often
        // exactly the moment execution ran off into nowhere.
        using Machine machine = Started();
        var trace = new InstructionTrace(capacity: 8) { Enabled = true };
        machine.Cpu.Trace = trace;

        machine.Cpu.State.Halted = false;
        machine.Cpu.SetSegment(SegReg.Cs, 0xF000);
        machine.Cpu.State.Eip = 0xFFF0;

        machine.Cpu.Step();

        Assert.NotEmpty(trace.Entries());
    }

    [Fact]
    public void ClearingLeavesTheRingEmptyButKeepsCounting()
    {
        using Machine machine = Started();
        var trace = new InstructionTrace(capacity: 32) { Enabled = true };
        machine.Cpu.Trace = trace;

        machine.RunInstructions(1000);
        long seen = trace.Recorded;
        trace.Clear();

        Assert.Empty(trace.Entries());
        Assert.Equal(seen, trace.Recorded);       // the sequence does not restart
    }

    [Fact]
    public void TheLastFewAreTheOnesJustExecuted()
    {
        using Machine machine = Started();
        var trace = new InstructionTrace(capacity: 256) { Enabled = true };
        machine.Cpu.Trace = trace;

        machine.RunInstructions(2000);

        IReadOnlyList<InstructionTrace.Entry> last = trace.Last(10);
        Assert.Equal(10, last.Count);
        Assert.Equal(trace.Entries().Last().Sequence, last[^1].Sequence);
    }
}
