using Aurora386.Core;
using Silk.NET.SDL;

namespace Aurora386.Sdl.Frontend;

/// <summary>
/// Carries <see cref="Machine.Audio"/>'s mixed output to the host's speakers
/// through SDL's queued-audio API.
///
/// SDL offers two ways to feed an audio device: a callback the driver calls
/// from its own thread whenever it wants more data, or a queue the
/// application tops up whenever convenient and <c>SDL_QueueAudio</c> drains
/// in the background. The queue is the simpler fit here: <see
/// cref="EmulatorWindow"/> already runs a frame loop, so a `Pump` call once
/// per frame -- alongside rendering the screen and pumping input events --
/// is enough, with no extra thread and no callback marshalled across the
/// native boundary.
///
/// The emulator core knows nothing of this: it renders into <see
/// cref="AudioMixer"/>'s per-source buffers and stops there (see that
/// type's own summary for the resampling and underrun/overrun policy), so
/// this class only ever asks the mixer for however many frames keep SDL's
/// queue near its target depth.
/// </summary>
public sealed unsafe class AudioOutput : IDisposable
{
    /// <summary>
    /// How many milliseconds of audio <see cref="Pump"/> tries to keep
    /// queued in SDL. Long enough to ride out a frame or two of jitter from
    /// the host running behind, short enough that a change in the mix (a
    /// paused Sound Blaster, say) is heard within about a tenth of a second.
    /// </summary>
    private const int TargetQueuedMilliseconds = 100;

    /// <summary>
    /// The most a single <see cref="Pump"/> call ever asks the mixer for, no
    /// matter how far behind SDL's queue has fallen. Without this cap, a
    /// long stall (the window minimised, a debugger breakpoint) would ask
    /// for a huge catch-up buffer the moment the loop resumed, which is
    /// exactly the "millions of instructions in one go" problem <see
    /// cref="EmulatorWindow.Run"/> already caps on the CPU side.
    /// </summary>
    private const int MaxPumpMilliseconds = 250;

    /// <summary>SDL's own frame count per audio callback/queue chunk, a middling buffer size.</summary>
    private const ushort DeviceBufferFrames = 1024;

    private readonly Silk.NET.SDL.Sdl _sdl;
    private readonly Machine _machine;

    private uint _device;
    private int _sampleRate;
    private short[] _staging = [];

    /// <summary>True when the host has a working audio device; false is not an error.</summary>
    public bool IsAvailable { get; private set; }

    public AudioOutput(Silk.NET.SDL.Sdl sdl, Machine machine)
    {
        _sdl = sdl;
        _machine = machine;
    }

    /// <summary>
    /// Opens the host's audio device at the mixer's own rate.
    ///
    /// Failure here is not fatal, the same way a missing sound card in the
    /// GUI front end is not: a machine built with no sound source fitted, a
    /// container with no audio server, or a CI runner should all still run
    /// the emulator, just silently.
    /// </summary>
    public void Start()
    {
        if (IsAvailable)
        {
            return;
        }

        // No source, no sound: opening a device nobody will ever feed
        // buys nothing and would only add a spurious dependency on the
        // host's audio stack to a headless run.
        if (_machine.Audio.Sources.Count == 0)
        {
            return;
        }

        if (_sdl.InitSubSystem(Silk.NET.SDL.Sdl.InitAudio) < 0)
        {
            return;
        }

        _sampleRate = _machine.Audio.OutputSampleRate;

        var desired = new AudioSpec
        {
            Freq = _sampleRate,
            Format = (ushort)Silk.NET.SDL.Sdl.AudioS16Sys,
            Channels = 2,
            Samples = DeviceBufferFrames,
        };
        AudioSpec obtained = default;

        // allowed_changes = 0: refuse any format SDL would otherwise
        // substitute. QueueAudio below assumes exactly what was asked for --
        // 16-bit signed stereo at the mixer's rate -- and silently accepting
        // a different one would resample nothing and just play noise.
        _device = _sdl.OpenAudioDevice((byte*)null, 0, &desired, &obtained, 0);
        if (_device == 0)
        {
            return;
        }

        int stagingFrames = _sampleRate * MaxPumpMilliseconds / 1000;
        _staging = new short[stagingFrames * 2];

        // Unpauses the device: SDL opens every device paused so the
        // application gets a chance to queue some audio first.
        _sdl.PauseAudioDevice(_device, 0);

        IsAvailable = true;
    }

    /// <summary>
    /// Tops up SDL's queue towards <see cref="TargetQueuedMilliseconds"/>.
    /// Called once per window frame; a no-op when <see cref="Start"/> found
    /// no host device or no source to play.
    /// </summary>
    public void Pump()
    {
        if (!IsAvailable)
        {
            return;
        }

        const int BytesPerFrame = 2 * sizeof(short); // stereo, 16-bit

        uint queuedBytes = _sdl.GetQueuedAudioSize(_device);
        int queuedFrames = (int)(queuedBytes / BytesPerFrame);

        int targetFrames = _sampleRate * TargetQueuedMilliseconds / 1000;
        int wantedFrames = targetFrames - queuedFrames;
        if (wantedFrames <= 0)
        {
            return;
        }

        int maxFrames = _staging.Length / 2;
        if (wantedFrames > maxFrames)
        {
            wantedFrames = maxFrames;
        }

        Span<short> destination = _staging.AsSpan(0, wantedFrames * 2);
        _machine.Audio.ReadFrames(destination);

        fixed (short* samples = destination)
        {
            _sdl.QueueAudio(_device, samples, (uint)(destination.Length * sizeof(short)));
        }
    }

    public void Dispose()
    {
        if (!IsAvailable)
        {
            return;
        }

        _sdl.CloseAudioDevice(_device);
        IsAvailable = false;
    }
}
