using Aurora386.Core.Timing;

namespace Aurora386.Core.Devices.Parallel;

/// <summary>
/// The printer plugged into LPT1: it has no paper jam, no ribbon and no
/// buffer overrun, only a question of where one print job ends and the next
/// begins (docs/PARALLEL-PORT.md section 3).
///
/// A dot-matrix or laser printer of the period gives no signal for "the
/// document is finished" -- DOS's PRINT.COM and a program's own print
/// routine simply stop sending bytes when they are done, and the printer
/// itself has no way to tell a paragraph break from the end of the whole
/// report. The rule this class applies instead is a quiet period: a job is
/// whatever bytes arrive between two gaps of at least two seconds of
/// emulated time with nothing sent, using <see cref="Scheduler"/> rather
/// than the host's own clock so that a fast-forwarded or single-stepped
/// emulator still closes jobs at the same simulated moment a real printer
/// would. The same close also happens when the guest pulses /INIT --
/// <see cref="Initialize"/> -- and when the owning <see cref="Machine"/>
/// shuts down -- <see cref="Flush"/>.
/// </summary>
public sealed class PrintJobCollector : IParallelPrinter
{
    /// <summary>How long LPT1 may sit silent before the job in progress is considered finished.</summary>
    private const int QuietPeriodSeconds = 2;

    /// <summary>
    /// A ceiling on one job's size, so a guest that never stops printing --
    /// a runaway loop, a device driver stuck retrying -- cannot grow a
    /// single in-memory buffer without bound (*choice*: no period printer
    /// held anything like 16 MB of ribbon, this exists purely as a host-side
    /// safety valve). Reaching it closes the job exactly as a quiet period
    /// would; the next byte starts a fresh one.
    /// </summary>
    public const int JobSizeBoundBytes = 16 * 1024 * 1024;

    private readonly Scheduler _scheduler;
    private readonly Func<long> _currentCycle;
    private readonly Func<int> _cpuFrequencyHz;
    private readonly IPrintJobSink _sink;

    private readonly List<byte> _job = new();

    /// <summary>The pending "close on silence" event, or null when nothing is scheduled.</summary>
    private ScheduledEvent? _quietTimer;

    public PrintJobCollector(Scheduler scheduler, Func<long> currentCycle, Func<int> cpuFrequencyHz, IPrintJobSink sink)
    {
        _scheduler = scheduler;
        _currentCycle = currentCycle;
        _cpuFrequencyHz = cpuFrequencyHz;
        _sink = sink;
    }

    /// <summary>A byte the port's handshake has just delivered.</summary>
    public void Receive(byte value)
    {
        CloseIfQuietPeriodWasMissed();

        _job.Add(value);
        RearmQuietTimer();

        if (_job.Count >= JobSizeBoundBytes)
        {
            CloseJob();
        }
    }

    /// <summary>/INIT falling: the job in progress, if any, is finished.</summary>
    public void Initialize() => CloseJob();

    /// <summary>Called by the owner as the machine shuts down, so a job left open is not lost.</summary>
    public void Flush() => CloseJob();

    /// <summary>
    /// Closes the job in progress, exactly as <see cref="Flush"/> does. The
    /// owner calls this from the same reset path that clears
    /// <see cref="Timing.Scheduler"/> -- POST, and before a snapshot restore
    /// -- because that clear silently drops whatever quiet timer was
    /// pending. Without an explicit close here, a job open at the moment of
    /// a guest reset would sit forever waiting for a timer that no longer
    /// exists, since <see cref="Timing.Scheduler.Clear"/> discards every
    /// handle without running its callback.
    /// </summary>
    public void Reset() => CloseJob();

    /// <summary>
    /// Cancels whatever close timer was pending and schedules a fresh one
    /// two seconds of emulated time out from now, so that as long as bytes
    /// keep arriving the job never closes on its own.
    ///
    /// A guest can send bytes faster than the emulated clock advances
    /// between them -- the whole point of the parallel port's own bound in
    /// <see cref="JobSizeBoundBytes"/> is that this can run for millions of
    /// bytes at what is, in cycle terms, the same instant. When that
    /// happens the timer already due is due at exactly the moment a fresh
    /// one would be, and cancelling it just to schedule an identical
    /// replacement would leave the scheduler's queue holding one stale,
    /// cancelled entry per byte for no observable effect -- so this skips
    /// the round trip when nothing would actually change.
    /// </summary>
    private void RearmQuietTimer()
    {
        long dueCycle = _currentCycle() + (long)QuietPeriodSeconds * _cpuFrequencyHz();

        if (_quietTimer is not null && _quietTimer.DueCycle == dueCycle && _scheduler.IsPending(_quietTimer))
        {
            return;
        }

        if (_quietTimer is not null)
        {
            _scheduler.Cancel(_quietTimer);
        }

        _quietTimer = _scheduler.Schedule(dueCycle, "LPT1 print job quiet timeout", _ => CloseJob());
    }

    /// <summary>
    /// Catches up when the timer this collector was relying on to notice
    /// silence has been discarded out from under it -- the one case being
    /// <see cref="Timing.Scheduler.Clear"/> on a guest reset that does not
    /// also call <see cref="Reset"/>. If the quiet timer's own due cycle has
    /// already passed by the time the next byte arrives, that byte belongs
    /// to a new job, not the one before the gap, even though nothing ever
    /// ran the callback that would ordinarily have said so.
    /// </summary>
    private void CloseIfQuietPeriodWasMissed()
    {
        if (_quietTimer is null)
        {
            return;
        }

        if (_scheduler.IsPending(_quietTimer))
        {
            // The timer this collector armed is still going to fire on its
            // own; nothing has been lost.
            return;
        }

        if (_currentCycle() < _quietTimer.DueCycle)
        {
            // Not pending because it already fired and closed the job
            // normally (the common case, in which _quietTimer would already
            // be null -- see CloseJob) or because it was cancelled by this
            // same class. Either way, the due cycle has not been reached,
            // so there is nothing to catch up on.
            return;
        }

        CloseJob();
    }

    /// <summary>
    /// Hands whatever has been collected to the sink and starts fresh.
    /// Cancelling an already-fired timer (the common case when this runs
    /// from the timer's own callback) is a harmless no-op, per
    /// <see cref="Scheduler.Cancel"/>.
    /// </summary>
    private void CloseJob()
    {
        if (_quietTimer is not null)
        {
            _scheduler.Cancel(_quietTimer);
            _quietTimer = null;
        }

        if (_job.Count == 0)
        {
            return;
        }

        byte[] bytes = _job.ToArray();
        _job.Clear();
        _sink.JobCompleted(bytes);
    }
}
