using System;
using System.Diagnostics;
using System.Threading;
using Aurora386.Core;
using Aurora386.Core.Bios;

namespace Aurora386.Gui.Emulation;

/// <summary>
/// Owns the emulated machine and the thread that drives it.
///
/// The emulator core is deliberately headless: it exposes a framebuffer and
/// accepts scancodes, and knows nothing about windows. This class is the
/// bridge. It runs the machine on its own thread so that a slow guest -- or
/// one stuck in a tight loop -- can never freeze the user interface, and it
/// publishes just enough state for the UI to render indicators.
///
/// Everything the UI touches is either immutable or guarded, because the
/// emulation thread writes it continuously while the UI thread reads it.
/// </summary>
public sealed class EmulatorSession : IDisposable
{
    private readonly object _gate = new();
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _paused;
    private volatile bool _stopRequested;

    /// <summary>
    /// The machine itself. Access from the UI thread is only safe while the
    /// emulation thread is paused or stopped; use <see cref="WithMachine"/>
    /// for anything that reads more than a single field.
    /// </summary>
    public Machine Machine { get; private set; }

    /// <summary>Raised after each batch of instructions, on the emulation thread.</summary>
    public event Action? FrameReady;

    /// <summary>Instructions executed per second, refreshed about once a second.</summary>
    public double InstructionsPerSecond { get; private set; }

    /// <summary>True while the machine is running and not paused.</summary>
    public bool IsRunning => _running && !_paused;

    public bool IsPaused => _paused;

    /// <summary>Activity indicators, decayed by the UI so they blink rather than latch.</summary>
    public DiskActivity Activity { get; } = new();

    public EmulatorSession()
    {
        Machine = new Machine();
    }

    /// <summary>
    /// Runs an action with exclusive access to the machine. The emulation
    /// thread holds the same lock around each batch, so this blocks only for
    /// the length of one batch -- a fraction of a millisecond.
    /// </summary>
    public void WithMachine(Action<Machine> action)
    {
        lock (_gate) action(Machine);
    }

    public T WithMachine<T>(Func<Machine, T> action)
    {
        lock (_gate) return action(Machine);
    }

    public void Start()
    {
        if (_running) return;

        _stopRequested = false;
        _paused = false;
        _running = true;

        _thread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = "Aurora386 emulation",
            // The guest is latency sensitive: a late timer tick shows up as a
            // stuttering cursor or a dropped keystroke.
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public void Pause() => _paused = true;

    public void Resume() => _paused = false;

    public void TogglePause() => _paused = !_paused;

    /// <summary>
    /// Executes a single instruction and stops. Used by the debugger; only
    /// meaningful while paused.
    /// </summary>
    public void StepOne()
    {
        lock (_gate) Machine.RunInstructions(1);
        FrameReady?.Invoke();
    }

    /// <summary>
    /// Rebuilds the machine from scratch, as if the power had been cycled.
    /// Attached images are re-attached by the caller.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            Machine.Dispose();
            Machine = new Machine();
        }
    }

    public void Stop()
    {
        _stopRequested = true;
        _thread?.Join(TimeSpan.FromSeconds(2));
        _running = false;
    }

    private void RunLoop()
    {
        // A 386 at 25 MHz retires very roughly this many instructions per
        // second. Running in batches keeps the lock cheap: taking it once per
        // instruction would cost more than the instruction itself.
        const int batchSize = 200_000;

        var clock = Stopwatch.StartNew();
        long executed = 0;
        long lastReport = 0;
        var lastReportTime = TimeSpan.Zero;

        while (!_stopRequested)
        {
            if (_paused)
            {
                Thread.Sleep(10);
                // Time spent paused must not count as slow emulation.
                lastReportTime = clock.Elapsed;
                lastReport = executed;
                continue;
            }

            lock (_gate)
            {
                try
                {
                    Machine.RunInstructions(batchSize);
                }
                catch (Exception)
                {
                    // A guest that executes something we do not implement
                    // should stop the machine, not take down the window. The
                    // debugger can then be used to see where it happened.
                    _paused = true;
                }
            }

            executed += batchSize;
            FrameReady?.Invoke();

            TimeSpan now = clock.Elapsed;
            if (now - lastReportTime >= TimeSpan.FromSeconds(1))
            {
                InstructionsPerSecond =
                    (executed - lastReport) / (now - lastReportTime).TotalSeconds;
                lastReport = executed;
                lastReportTime = now;
            }
        }
    }

    public void Dispose()
    {
        Stop();
        Machine.Dispose();
    }
}

/// <summary>
/// Activity counters for the status bar LEDs. The emulation thread bumps
/// these; the UI reads them and lights the corresponding indicator, letting
/// it fade so that a burst of accesses looks like a flicker rather than a
/// permanently lit lamp.
/// </summary>
public sealed class DiskActivity
{
    private long _floppyReads, _floppyWrites, _hardDiskReads, _hardDiskWrites;

    public long FloppyReads => Interlocked.Read(ref _floppyReads);
    public long FloppyWrites => Interlocked.Read(ref _floppyWrites);
    public long HardDiskReads => Interlocked.Read(ref _hardDiskReads);
    public long HardDiskWrites => Interlocked.Read(ref _hardDiskWrites);

    public void NoteFloppyRead() => Interlocked.Increment(ref _floppyReads);
    public void NoteFloppyWrite() => Interlocked.Increment(ref _floppyWrites);
    public void NoteHardDiskRead() => Interlocked.Increment(ref _hardDiskReads);
    public void NoteHardDiskWrite() => Interlocked.Increment(ref _hardDiskWrites);
}
