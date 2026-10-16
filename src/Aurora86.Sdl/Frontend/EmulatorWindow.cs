using Aurora86.Graphics;
using System.Diagnostics;
using Aurora86.Core;
using Aurora86.Core.Devices.Video;
using Aurora86.Core.Host.Midi;
using Silk.NET.Maths;
using Silk.NET.SDL;

namespace Aurora86.Sdl.Frontend;

/// <summary>
/// The emulator window: presents the framebuffer, collects keystrokes and
/// drives the machine in real time.
///
/// The loop is frame-based: each pass executes the instructions corresponding
/// to the time that has actually elapsed, then redraws. Pacing execution
/// against real time rather than against how fast the host happens to be is
/// what makes the guest's own clock run at the right speed.
/// </summary>
public sealed unsafe class EmulatorWindow : IDisposable
{
    private readonly Silk.NET.SDL.Sdl _sdl;
    private readonly Machine _machine;
    private readonly AudioOutput _audio;

    private Window* _window;
    private Renderer* _renderer;
    private Texture* _texture;

    private int _textureWidth;
    private int _textureHeight;
    private readonly FramebufferUploadTracker _frameUploads = new();
    private bool _textureUploadPending;

    /// <summary>How many screen pixels the window uses per framebuffer pixel.</summary>
    private int _scale = 2;

    private bool _running;
    private bool _paused;
    private string? _lastMidiDiagnostic;

    /// <summary>Statistics reported in the window title.</summary>
    private long _instructionsExecuted;
    private int _framesDrawn;
    private readonly Stopwatch _statsTimer = Stopwatch.StartNew();

    public EmulatorWindow(Machine machine)
    {
        _machine = machine;
        _sdl = Silk.NET.SDL.Sdl.GetApi();
        _audio = new AudioOutput(_sdl, machine);

        // APM power-off from the guest ends the run, as it would on a real
        // machine with a power supply the BIOS can switch.
        _machine.PowerOffRequested += () =>
        {
            Console.WriteLine("the guest turned the power off");
            _running = false;
        };
    }

    public void Run()
    {
        Initialize();
        _audio.Start();
        _running = true;

        var frameTimer = Stopwatch.StartNew();
        double accumulatedSeconds = 0;

        // 60 frames per second: plenty for a machine that generated 70 in
        // text mode, and a good balance between smoothness and how coarsely
        // execution gets chopped up between frames.
        const double TargetFrameSeconds = 1.0 / 60.0;

        while (_running)
        {
            PumpEvents();

            double elapsed = frameTimer.Elapsed.TotalSeconds;
            frameTimer.Restart();

            // Capping the accumulated time stops the machine from trying to
            // catch up after a long stall -- window minimised, host under
            // load -- by executing millions of instructions in one go, which
            // would freeze the UI and make the guest's timing lurch.
            accumulatedSeconds = Math.Min(accumulatedSeconds + elapsed, 0.25);

            while (accumulatedSeconds >= TargetFrameSeconds)
            {
                if (!_paused)
                {
                    RunOneFrame(TargetFrameSeconds);
                }
                accumulatedSeconds -= TargetFrameSeconds;
            }

            Render();
            _audio.Pump();
            UpdateTitle();
        }
    }

    private void Initialize()
    {
        if (_sdl.Init(Silk.NET.SDL.Sdl.InitVideo | Silk.NET.SDL.Sdl.InitEvents) < 0)
            throw new InvalidOperationException($"SDL failed to initialise: {_sdl.GetErrorS()}");

        _textureWidth = _machine.Display.Width;
        _textureHeight = _machine.Display.Height;

        _window = _sdl.CreateWindow(
            "Aurora86",
            Silk.NET.SDL.Sdl.WindowposCentered, Silk.NET.SDL.Sdl.WindowposCentered,
            _textureWidth * _scale, _textureHeight * _scale,
            (uint)(WindowFlags.Shown | WindowFlags.Resizable));

        if (_window is null)
            throw new InvalidOperationException($"could not create the window: {_sdl.GetErrorS()}");

        _renderer = _sdl.CreateRenderer(_window, -1,
            (uint)(RendererFlags.Accelerated | RendererFlags.Presentvsync));

        if (_renderer is null)
            throw new InvalidOperationException($"could not create the renderer: {_sdl.GetErrorS()}");

        CreateTexture();
    }

    private void CreateTexture()
    {
        if (_texture is not null)
        {
            _sdl.DestroyTexture(_texture);
        }

        _textureWidth = _machine.Display.Width;
        _textureHeight = _machine.Display.Height;

        // Streaming access: the texture is rewritten on every frame that
        // actually changes.
        _texture = _sdl.CreateTexture(_renderer,
            (uint)PixelFormatEnum.Abgr8888,
            (int)TextureAccess.Streaming,
            _textureWidth, _textureHeight);

        if (_texture is null)
        {
            throw new InvalidOperationException($"could not create the texture: {_sdl.GetErrorS()}");
        }

        _frameUploads.Invalidate();
        _textureUploadPending = true;

        // Nearest-neighbour filtering: scaling a 640x400 image up with
        // interpolation would produce a blurry approximation rather than a
        // faithful picture of what the hardware emitted.
        _sdl.SetHint(Silk.NET.SDL.Sdl.HintRenderScaleQuality, "0");
    }

    private void RunOneFrame(double seconds)
    {
        long before = _machine.Cpu.Cycles;
        _machine.RunFor(TimeSpan.FromSeconds(seconds));
        _instructionsExecuted += _machine.Cpu.Cycles - before;
    }

    private void Render()
    {
        // Publish the client area as an advisory mode, never a direct register
        // write. Ignore minimised and transiently tiny windows during a drag.
        int preferredWidth, preferredHeight;
        _sdl.GetWindowSize(_window, &preferredWidth, &preferredHeight);
        bool minimized = (_sdl.GetWindowFlags(_window) & (uint)WindowFlags.Minimized) != 0;
        if (HostPreferredSize.TryFromLogical(preferredWidth, preferredHeight, minimized,
            out int boundedWidth, out int boundedHeight))
        {
            _machine.AuroraGpu?.SetHostPreferredSize(boundedWidth, boundedHeight);
        }

        // The cursor blinks twice a second, as it does on a real PC.
        _machine.Display.CursorBlinkOn = (_statsTimer.ElapsedMilliseconds / 500) % 2 == 0;

        bool changed = _machine.Display.Render();

        // A video mode change may have resized the framebuffer underneath us.
        if (_machine.Display.Width != _textureWidth || _machine.Display.Height != _textureHeight)
        {
            CreateTexture();
            changed = true;
        }

        if (changed)
        {
            _textureUploadPending = true;
        }

        if (_textureUploadPending)
        {
            ReadOnlySpan<uint> source = _machine.Display.Framebuffer;
            FramebufferRowRange rows =
                _frameUploads.FindChangedRows(source, _textureWidth, _textureHeight);
            if (rows.RowCount == 0)
            {
                _textureUploadPending = false;
            }
            else
            {
                var rectangle = new Rectangle<int>(0, rows.FirstRow, _textureWidth, rows.RowCount);
                void* pixels;
                int pitch;
                if (_sdl.LockTexture(_texture, &rectangle, &pixels, &pitch) == 0)
                {
                    try
                    {
                        var destination = new Span<byte>(pixels, checked(pitch * rows.RowCount));
                        _frameUploads.CopyRows(source, rows, destination, pitch);
                    }
                    finally
                    {
                        _sdl.UnlockTexture(_texture);
                    }

                    _frameUploads.ConfirmUpload(source, rows);
                    _textureUploadPending = false;
                }
            }
        }

        _sdl.RenderClear(_renderer);

        // Preserve the original proportions when the window is resized:
        // arbitrarily stretched 640x400 output looks worse than the same
        // picture letterboxed with black bars.
        int windowWidth, windowHeight;
        _sdl.GetWindowSize(_window, &windowWidth, &windowHeight);

        double scale = Math.Min((double)windowWidth / _textureWidth,
                                (double)windowHeight / _textureHeight);
        var target = new Rectangle<int>(
            (int)((windowWidth - _textureWidth * scale) / 2),
            (int)((windowHeight - _textureHeight * scale) / 2),
            (int)(_textureWidth * scale),
            (int)(_textureHeight * scale));

        _sdl.RenderCopy(_renderer, _texture, null, &target);
        _sdl.RenderPresent(_renderer);
        _framesDrawn++;
    }

    private void PumpEvents()
    {
        Event e = default;
        while (_sdl.PollEvent(ref e) != 0)
        {
            switch ((EventType)e.Type)
            {
                case EventType.Quit:
                    _running = false;
                    break;

                case EventType.Keydown:
                    HandleKey(e.Key, pressed: true);
                    break;

                case EventType.Keyup:
                    HandleKey(e.Key, pressed: false);
                    break;
            }
        }
    }

    private void HandleKey(KeyboardEvent key, bool pressed)
    {
        var code = (Scancode)key.Keysym.Scancode;

        // The emulator's own shortcuts hang off the Meta key (Command on a
        // Mac), which the guest software has no way of intercepting -- a PC
        // keyboard has no such key, so nothing running inside can claim it.
        bool commandHeld = (key.Keysym.Mod & (ushort)Keymod.Gui) != 0;
        if (pressed && commandHeld)
        {
            switch (code)
            {
                case Scancode.ScancodeQ: _running = false; return;
                case Scancode.ScancodeP: _paused = !_paused; return;
                case Scancode.ScancodeR: _machine.Cpu.Reset(); return;
                case Scancode.ScancodeF: ToggleFullscreen(); return;
            }
        }

        // SDL's own key auto-repeat: the emulated BIOS implements typematic
        // repeat itself, so repeated press events must be dropped or the
        // keyboard buffer fills with duplicates.
        if (pressed && key.Repeat != 0)
        {
            return;
        }

        byte scanCode = KeyMap.ToScanCode(code);
        if (scanCode == 0)
        {
            return;
        }

        if (KeyMap.IsExtended(code))
        {
            _machine.Keyboard.PushScanCode(0xE0);
        }

        if (pressed)
        {
            _machine.PressKey(scanCode);
        }
        else _machine.ReleaseKey(scanCode);
    }

    private void ToggleFullscreen()
    {
        uint flags = _sdl.GetWindowFlags(_window);
        bool isFullscreen = (flags & (uint)WindowFlags.FullscreenDesktop) != 0;
        _sdl.SetWindowFullscreen(_window,
            isFullscreen ? 0u : (uint)WindowFlags.FullscreenDesktop);
    }

    private void UpdateTitle()
    {
        if (_statsTimer.ElapsedMilliseconds < 1000)
        {
            return;
        }

        ReportMidiStatus();

        double seconds = _statsTimer.Elapsed.TotalSeconds;
        double mips = _instructionsExecuted / seconds / 1e6;
        int fps = (int)(_framesDrawn / seconds);

        string state = _paused ? " — in pausa" : "";
        _sdl.SetWindowTitle(_window,
            $"Aurora86 — {mips:F1} MIPS, {fps} fps{state}");

        _instructionsExecuted = 0;
        _framesDrawn = 0;
        _statsTimer.Restart();
    }

    /// <summary>Reports changes in worker diagnostics without repeatedly filling the console.</summary>
    private void ReportMidiStatus()
    {
        string? diagnostic = null;
        if (_machine.Midi?.Sink is WinMmMidiSink output)
        {
            diagnostic = output.LastError;
            if (diagnostic is null && output.DroppedSysExCount != 0)
            {
                diagnostic = $"{output.DroppedSysExCount} oversized SysEx messages discarded";
            }
        }

        if (diagnostic is not null && diagnostic != _lastMidiDiagnostic)
        {
            Console.Error.WriteLine($"MIDI: {diagnostic}");
        }

        _lastMidiDiagnostic = diagnostic;
    }

    public void Dispose()
    {
        _audio.Dispose();
        if (_texture is not null)
        {
            _sdl.DestroyTexture(_texture);
        }
        if (_renderer is not null)
        {
            _sdl.DestroyRenderer(_renderer);
        }
        if (_window is not null)
        {
            _sdl.DestroyWindow(_window);
        }
        _sdl.Quit();
        _sdl.Dispose();
    }
}
