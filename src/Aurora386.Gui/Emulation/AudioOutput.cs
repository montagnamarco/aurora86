using System;
using System.Threading;
using Aurora386.Core.Devices.Audio;
using OpenTK.Audio.OpenAL;

namespace Aurora386.Gui.Emulation;

/// <summary>
/// Carries <see cref="Aurora386.Core.Machine.Audio"/>'s mixed output to the
/// host's speakers.
///
/// The emulator core deliberately knows nothing about audio hardware: every
/// sound source renders into its own buffer, <c>AudioMixer</c> resamples and
/// sums them, and stops there. That keeps the core headless and testable,
/// and it puts the awkward part -- staying ahead of a device that consumes
/// samples in real time, on a machine whose clock has nothing to do with the
/// guest's -- in one place, here. Pulling from the mixer rather than from
/// the Sound Blaster directly is what lets a second source (an ES1371, an
/// MPU-401 synth) join the mix later without this file changing at all.
///
/// The mechanism is a small queue of buffers. We hand OpenAL several short
/// buffers, and as it finishes each one we refill it and hand it back. The
/// queue is what absorbs jitter: if the emulation thread is briefly late,
/// there is still sound queued to play, and the listener hears nothing wrong.
/// Too short a queue and every hiccup becomes an audible click; too long and
/// sound effects lag behind the action.
/// </summary>
public sealed class AudioOutput : IDisposable
{
    /// <summary>
    /// Number of buffers in flight. Four is a common compromise: enough to
    /// ride out a scheduling delay, short enough that latency stays under
    /// about 40 ms, which is roughly where a person starts to notice that a
    /// sound arrived after the thing that caused it.
    /// </summary>
    private const int BufferCount = 4;

    /// <summary>Frames per buffer, per channel.</summary>
    private const int FramesPerBuffer = 512;

    private readonly EmulatorSession _session;
    private readonly short[] _staging;

    /// <summary>The mixer's output rate, fixed once <see cref="Start"/> reads it.</summary>
    private int _sampleRate = AudioMixer.DefaultOutputSampleRate;

    private ALDevice _device;
    private ALContext _context;
    private int _source;
    private int[] _buffers = [];

    private Thread? _thread;
    private volatile bool _running;

    /// <summary>True when the host has working audio; false is not an error.</summary>
    public bool IsAvailable { get; private set; }

    public AudioOutput(EmulatorSession session)
    {
        _session = session;
        _staging = new short[FramesPerBuffer * 2];   // stereo
    }

    /// <summary>
    /// Opens the host's audio device and starts feeding it.
    ///
    /// Failure here is not fatal. A machine with no sound card, a container
    /// with no audio server, or a CI runner should all still run the
    /// emulator -- just silently.
    /// </summary>
    public void Start()
    {
        if (_running)
        {
            return;
        }

        // No source, no sound -- exactly as a machine built with an empty
        // sound list looks to a real listener: silent, not broken. The
        // mixer itself always exists, even with nothing plugged into it, so
        // "no card fitted" now means "the mixer has no sources" rather than
        // "there is no mixer".
        bool hasSource = _session.WithMachine(m => m.Audio.Sources.Count > 0);
        if (!hasSource)
        {
            return;
        }
        _sampleRate = _session.WithMachine(m => m.Audio.OutputSampleRate);

        try
        {
            _device = ALC.OpenDevice(null);
            if (_device == ALDevice.Null)
            {
                return;
            }

            _context = ALC.CreateContext(_device, (int[]?)null);
            if (_context == ALContext.Null) { ALC.CloseDevice(_device); return; }

            ALC.MakeContextCurrent(_context);

            _source = AL.GenSource();
            _buffers = AL.GenBuffers(BufferCount);

            // Prime the queue with silence so playback can start immediately
            // rather than waiting for the first samples to be rendered.
            Array.Clear(_staging);
            foreach (int buffer in _buffers)
            {
                AL.BufferData(buffer, ALFormat.Stereo16, _staging, _sampleRate);
                AL.SourceQueueBuffer(_source, buffer);
            }

            AL.SourcePlay(_source);

            IsAvailable = true;
            _running = true;

            _thread = new Thread(FeedLoop)
            {
                IsBackground = true,
                Name = "Aurora386 audio",
            };
            _thread.Start();
        }
        catch (Exception)
        {
            // Any failure to reach the host's audio stack means no sound, not
            // a crash. The emulator is still perfectly usable without it.
            IsAvailable = false;
        }
    }

    /// <summary>
    /// Refills buffers as the device finishes with them.
    ///
    /// This runs on its own thread and blocks on nothing but a short sleep:
    /// OpenAL tells us how many buffers it has processed, and we only do work
    /// when there is work to do.
    /// </summary>
    private void FeedLoop()
    {
        int sampleRate = _sampleRate;

        while (_running)
        {
            AL.GetSource(_source, ALGetSourcei.BuffersProcessed, out int processed);

            if (processed == 0)
            {
                // Nothing to do yet. Sleeping for a fraction of one buffer's
                // duration keeps us responsive without spinning a core.
                Thread.Sleep(5);
                continue;
            }

            while (processed-- > 0)
            {
                int buffer = AL.SourceUnqueueBuffer(_source);

                // The mixer always fills the whole span itself -- a source
                // with nothing new to say contributes silence rather than
                // shortening the read (see AudioMixer's own summary) -- so
                // there is no short read here to pad, unlike pulling a
                // single card's buffer directly used to require.
                _session.WithMachine(machine => machine.Audio.ReadFrames(_staging));

                AL.BufferData(buffer, ALFormat.Stereo16, _staging, sampleRate);
                AL.SourceQueueBuffer(_source, buffer);
            }

            // If the device ran dry while we were behind, it will have stopped.
            // Starting it again is the only way to recover; the alternative is
            // silence for the rest of the session.
            AL.GetSource(_source, ALGetSourcei.SourceState, out int state);
            if ((ALSourceState)state != ALSourceState.Playing)
                AL.SourcePlay(_source);
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(1));

        if (!IsAvailable)
        {
            return;
        }

        try
        {
            AL.SourceStop(_source);
            AL.DeleteSource(_source);
            if (_buffers.Length > 0)
            {
                AL.DeleteBuffers(_buffers);
            }

            ALC.MakeContextCurrent(ALContext.Null);
            ALC.DestroyContext(_context);
            ALC.CloseDevice(_device);
        }
        catch (Exception)
        {
            // Tearing down audio must never stop the application from closing.
        }
    }
}
