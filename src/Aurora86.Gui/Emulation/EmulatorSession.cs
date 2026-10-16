using System;
using System.Diagnostics;
using System.Threading;
using Aurora86.Core;
using Aurora86.Core.Firmware;
using Aurora86.Core.Firmware.Fpu;
using Aurora86.Core.Devices.Storage;
using Aurora86.Core.Diagnostics;
using Aurora86.Graphics;
namespace Aurora86.Gui.Emulation;

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
    private int _machineAccessRequests;
    private AuroraGpuBackendConnection _graphicsBackend;

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

    /// <summary>
    /// Run without holding the guest to its nominal speed.
    ///
    /// Useful for getting through a long install or a slow boot, but it has a
    /// real cost: everything the guest paces by the timer -- key repeat,
    /// animations, anything that measures time by counting ticks -- runs at
    /// the same multiple. Off by default for that reason.
    /// </summary>
    public bool Unthrottled { get; set; }

    /// <summary>
    /// How a machine is made, used at start and again at every reset.
    ///
    /// A reset rebuilds the machine from nothing, and "nothing" has to mean
    /// the same thing each time: the same memory size, the same processor,
    /// the same disks. Keeping that recipe here rather than in whoever
    /// pressed reset is what lets a saved configuration describe a machine
    /// once and have every reset honour it.
    /// </summary>
    public Func<Machine> Factory { get; set; } = () => new Machine();

    public EmulatorSession()
    {
        Machine = Factory();
        _graphicsBackend = AuroraGpuBackendConnection.Attach(Machine);
    }

    /// <summary>
    /// Runs an action with exclusive access to the machine. The emulation
    /// thread holds the same lock around each batch, so this blocks only for
    /// the length of one batch -- a fraction of a millisecond.
    /// </summary>
    public void WithMachine(Action<Machine> action)
    {
        Interlocked.Increment(ref _machineAccessRequests);
        try
        {
            lock (_gate) action(Machine);
        }
        finally
        {
            Interlocked.Decrement(ref _machineAccessRequests);
        }
    }

    public T WithMachine<T>(Func<Machine, T> action)
    {
        Interlocked.Increment(ref _machineAccessRequests);
        try
        {
            lock (_gate) return action(Machine);
        }
        finally
        {
            Interlocked.Decrement(ref _machineAccessRequests);
        }
    }

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _stopRequested = false;
        _paused = false;
        _running = true;

        _thread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = "Aurora86 emulation",
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
            _graphicsBackend.Dispose();
            Machine.Dispose();
            Machine = Factory();
            _graphicsBackend = AuroraGpuBackendConnection.Attach(Machine);
            if (Environment.GetEnvironmentVariable("AURORA_GPU_BACKEND") is not null)
            {
                Console.WriteLine($"graphics backend: {_graphicsBackend.Description}");
            }
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
        // How long a slice of emulated time each pass covers. Small enough
        // that the guest feels responsive, large enough that the per-batch
        // overhead stays negligible.
        TimeSpan slice = TimeSpan.FromMilliseconds(2);
        // A 200 MHz clock otherwise holds the input/render lock for 400,000
        // instructions. Keep the original 25 MHz quantum at every clock.
        const long MaximumBatchInstructions = 50_000;

        // Half a second lit, half a second dark.
        const double CursorBlinkMs = 500;

        // Sixty repaints a second is as much as any display will show, and as
        // much as an eye will notice.
        TimeSpan FrameInterval = TimeSpan.FromMilliseconds(1000.0 / 60);

        var clock = Stopwatch.StartNew();
        long executed = 0;
        long lastReport = 0;
        var lastReportTime = TimeSpan.Zero;
        var lastFrameTime = TimeSpan.Zero;

        // How much guest time has been simulated. Compared against the wall
        // clock to decide whether to wait.
        var emulatedTime = TimeSpan.Zero;

        while (!_stopRequested)
        {
            if (_paused)
            {
                Thread.Sleep(10);
                // Time spent paused counts as neither slow emulation nor a
                // backlog to catch up on.
                lastReportTime = clock.Elapsed;
                lastReport = executed;
                emulatedTime = clock.Elapsed;
                continue;
            }

            long thisBatch;
            double batchSeconds;
            lock (_gate)
            {
                thisBatch = Math.Min(MaximumBatchInstructions,
                    (long)(Machine.CpuFrequencyHz * slice.TotalSeconds));
                batchSeconds = thisBatch / (double)Machine.CpuFrequencyHz;
                try
                {
                    Machine.RunInstructions(thisBatch);
                }
                catch (Exception)
                {
                    // A guest that executes something we do not implement
                    // should stop the machine, not take down the window. The
                    // debugger can then be used to see where it happened.
                    _paused = true;
                }
            }

            // Monitor does not guarantee fairness: when the guest runs behind
            // real time, immediate reacquisition can starve mouse and render
            // requests for over 100 ms. Yield only when such a request exists.
            if (Volatile.Read(ref _machineAccessRequests) != 0)
            {
                Thread.Sleep(1);
            }

            executed += thisBatch;
            emulatedTime += TimeSpan.FromSeconds(batchSeconds);

            TimeSpan now = clock.Elapsed;

            // Hold the guest to the speed of the machine it is pretending to
            // be.
            //
            // Left to run flat out this emulator retires around 58 million
            // instructions a second -- fifteen times what a 25 MHz 386 managed.
            // The devices are all clocked off CpuFrequencyHz, so emulated time
            // then races ahead of real time by the same factor: the timer
            // interrupt fires fifteen times too often, and anything the guest
            // paces by it goes haywire. The keyboard's auto-repeat is the most
            // visible casualty -- hold a key for an instant and DOS receives a
            // burst of characters, which is what makes the prompt run away.
            TimeSpan ahead = emulatedTime - now;
            if (Unthrottled)
            {
                emulatedTime = now;
            }
            else if (ahead > TimeSpan.FromMilliseconds(2))
                Thread.Sleep(ahead - TimeSpan.FromMilliseconds(1));
            else if (ahead < TimeSpan.FromMilliseconds(-250))
                // Far behind -- the host was busy elsewhere. Give up on the
                // lost time rather than sprinting to catch up, which would
                // produce exactly the burst we are trying to avoid.
                emulatedTime = now;

            // Only ask for a repaint about sixty times a second.
            //
            // A batch takes a few milliseconds, so signalling after every one
            // would post hundreds of invalidations per second at the UI
            // thread. The compositor cannot draw that fast, so the queue grows
            // and the dispatcher spends its time on work it will throw away --
            // which shows up as the whole emulator feeling sluggish even
            // though the CPU core itself is running at full speed.
            if (now - lastFrameTime >= FrameInterval)
            {
                lastFrameTime = now;

                // The text cursor blinks about twice a second, which is what
                // the hardware does: the CRT controller counts frames and
                // inverts the cursor cell every sixteen of them. Nobody would
                // notice the exact rate, but a cursor that does not blink at
                // all reads as a frozen machine.
                //
                // It is driven from real time rather than emulated time so
                // that it keeps a steady rhythm in Turbo, where emulated time
                // races ahead and the cursor would otherwise flicker.
                bool blink = (long)(now.TotalMilliseconds / CursorBlinkMs) % 2 == 0;
                lock (_gate) Machine.Display.CursorBlinkOn = blink;

                FrameReady?.Invoke();
            }
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
        lock (_gate)
        {
            _graphicsBackend.Dispose();
            Machine.Dispose();
        }
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
