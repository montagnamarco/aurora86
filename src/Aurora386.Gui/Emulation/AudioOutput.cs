using System;
using System.Threading;
using OpenTK.Audio.OpenAL;

namespace Aurora386.Gui.Emulation;

/// <summary>
/// Carries the sound card's output to the host's speakers.
///
/// The emulator core deliberately knows nothing about audio hardware: the
/// Sound Blaster renders into a buffer and stops there. That keeps the core
/// headless and testable, and it puts the awkward part -- staying ahead of a
/// device that consumes samples in real time, on a machine whose clock has
/// nothing to do with the guest's -- in one place, here.
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
        if (_running) return;

        try
        {
            _device = ALC.OpenDevice(null);
            if (_device == ALDevice.Null) return;

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
                AL.BufferData(buffer, ALFormat.Stereo16, _staging,
                              _session.WithMachine(m => m.SoundBlaster.SampleRate));
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
        int sampleRate = _session.WithMachine(m => m.SoundBlaster.SampleRate);

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

                int written = _session.WithMachine(
                    machine => machine.SoundBlaster.ReadSamples(_staging));

                // The card may not have produced a full buffer's worth. Pad
                // with silence rather than shortening the buffer: a short
                // buffer would make the stream run faster than real time and
                // the pitch would drift upwards.
                if (written < _staging.Length)
                    Array.Clear(_staging, written, _staging.Length - written);

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

        if (!IsAvailable) return;

        try
        {
            AL.SourceStop(_source);
            AL.DeleteSource(_source);
            if (_buffers.Length > 0) AL.DeleteBuffers(_buffers);

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
