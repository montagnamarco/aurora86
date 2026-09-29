using Aurora386.Core.Devices.Parallel;
using Aurora386.Core.Timing;

namespace Aurora386.Tests;

/// <summary>
/// The print job collector: where one job ends and the next begins,
/// measured in emulated time rather than the host's own clock
/// (docs/PARALLEL-PORT.md section 3).
/// </summary>
public class PrintJobCollectorTests
{
    /// <summary>Remembers every job handed to it, in order.</summary>
    private sealed class RecordingSink : IPrintJobSink
    {
        public readonly List<byte[]> Jobs = [];
        public void JobCompleted(ReadOnlySpan<byte> job) => Jobs.Add(job.ToArray());
    }

    private sealed class Rig
    {
        public Scheduler Scheduler { get; } = new();
        public RecordingSink Sink { get; } = new();
        public PrintJobCollector Collector { get; }

        /// <summary>A 25 MHz clock, so one emulated second is 25,000,000 cycles.</summary>
        public const int CpuFrequencyHz = 25_000_000;

        public long Now { get; set; }

        public Rig()
        {
            Collector = new PrintJobCollector(Scheduler, () => Now, () => CpuFrequencyHz, Sink);
        }

        /// <summary>Advances the emulated clock and runs whatever the scheduler owes at the new cycle.</summary>
        public void AdvanceSeconds(double seconds)
        {
            Now += (long)(seconds * CpuFrequencyHz);
            Scheduler.RunDue(Now);
        }
    }

    [Fact]
    public void TwoBytesOneSecondApartFormOneJob()
    {
        var rig = new Rig();

        rig.Collector.Receive(0x41);
        rig.AdvanceSeconds(1.0);
        rig.Collector.Receive(0x42);
        rig.AdvanceSeconds(1.9);   // still under the 2-second quiet period

        Assert.Empty(rig.Sink.Jobs);

        rig.AdvanceSeconds(0.2);   // now past 2 seconds since the second byte

        Assert.Single(rig.Sink.Jobs);
        Assert.Equal(new byte[] { 0x41, 0x42 }, rig.Sink.Jobs[0]);
    }

    [Fact]
    public void AThreeSecondGapSplitsIntoTwoJobs()
    {
        var rig = new Rig();

        rig.Collector.Receive(0x41);
        rig.AdvanceSeconds(3.0);   // past the quiet period: the first job closes
        Assert.Single(rig.Sink.Jobs);
        Assert.Equal(new byte[] { 0x41 }, rig.Sink.Jobs[0]);

        rig.Collector.Receive(0x42);
        rig.AdvanceSeconds(3.0);

        Assert.Equal(2, rig.Sink.Jobs.Count);
        Assert.Equal(new byte[] { 0x42 }, rig.Sink.Jobs[1]);
    }

    [Fact]
    public void InitializeClosesTheJobInProgress()
    {
        var rig = new Rig();

        rig.Collector.Receive(0x41);
        rig.Collector.Receive(0x42);
        rig.Collector.Initialize();

        Assert.Single(rig.Sink.Jobs);
        Assert.Equal(new byte[] { 0x41, 0x42 }, rig.Sink.Jobs[0]);

        // The quiet timer that was pending for those two bytes must not
        // fire a second, empty close.
        rig.AdvanceSeconds(3.0);
        Assert.Single(rig.Sink.Jobs);
    }

    [Fact]
    public void FlushClosesTheJobInProgressAtShutdown()
    {
        var rig = new Rig();

        rig.Collector.Receive(0x58);
        rig.Collector.Flush();

        Assert.Single(rig.Sink.Jobs);
        Assert.Equal(new byte[] { 0x58 }, rig.Sink.Jobs[0]);
    }

    [Fact]
    public void ResetClosesTheJobInProgressLikeFlush()
    {
        var rig = new Rig();

        rig.Collector.Receive(0x58);
        rig.Collector.Reset();

        Assert.Single(rig.Sink.Jobs);
        Assert.Equal(new byte[] { 0x58 }, rig.Sink.Jobs[0]);
    }

    [Fact]
    public void AGuestResetThatClearsTheSchedulerStillSplitsAcrossTheGap()
    {
        var rig = new Rig();

        rig.Collector.Receive(0x41);

        // Machine.Run.cs's own reset path calls Scheduler.Clear() directly,
        // dropping every scheduled event -- including the quiet timer this
        // collector armed for that byte -- without running its callback.
        rig.Scheduler.Clear();

        rig.AdvanceSeconds(3.0);   // no timer survives to close the job on its own
        Assert.Empty(rig.Sink.Jobs);

        // The next byte notices the timer it relied on never fired even
        // though its due cycle has long passed, and closes the first job
        // before folding itself into a fresh one.
        rig.Collector.Receive(0x42);
        Assert.Single(rig.Sink.Jobs);
        Assert.Equal(new byte[] { 0x41 }, rig.Sink.Jobs[0]);

        rig.Collector.Flush();
        Assert.Equal(2, rig.Sink.Jobs.Count);
        Assert.Equal(new byte[] { 0x42 }, rig.Sink.Jobs[1]);
    }

    [Fact]
    public void InitializeAndFlushWithNothingReceivedProduceNoJob()
    {
        var rig = new Rig();

        rig.Collector.Initialize();
        rig.Collector.Flush();

        Assert.Empty(rig.Sink.Jobs);
    }

    [Fact]
    public void AJobIsClosedAndANewOneStartedAtTheSixteenMegabyteBound()
    {
        var rig = new Rig();

        for (int i = 0; i < PrintJobCollector.JobSizeBoundBytes; i++)
        {
            rig.Collector.Receive((byte)i);
        }

        // The bound byte itself closes the job.
        Assert.Single(rig.Sink.Jobs);
        Assert.Equal(PrintJobCollector.JobSizeBoundBytes, rig.Sink.Jobs[0].Length);

        rig.Collector.Receive(0xAA);
        rig.Collector.Flush();

        Assert.Equal(2, rig.Sink.Jobs.Count);
        Assert.Equal(new byte[] { 0xAA }, rig.Sink.Jobs[1]);
    }
}
