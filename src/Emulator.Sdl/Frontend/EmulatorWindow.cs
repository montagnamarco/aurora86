using System.Diagnostics;
using Emulator.Core;
using Silk.NET.Maths;
using Silk.NET.SDL;

namespace Emulator.Sdl.Frontend;

/// <summary>
/// Finestra dell'emulatore: mostra il framebuffer, raccoglie i tasti e manda
/// avanti la macchina in tempo reale.
///
/// Il ciclo lavora a fotogrammi: a ogni giro esegue le istruzioni
/// corrispondenti al tempo trascorso, poi ridisegna. Legare l'esecuzione al
/// tempo reale invece che alla velocita' dell'ospite e' cio' che fa scorrere
/// l'orologio del sistema emulato alla velocita' giusta.
/// </summary>
public sealed unsafe class EmulatorWindow : IDisposable
{
    private readonly Silk.NET.SDL.Sdl _sdl;
    private readonly Machine _machine;

    private Window* _window;
    private Renderer* _renderer;
    private Texture* _texture;

    private int _textureWidth;
    private int _textureHeight;

    /// <summary>Fattore di ingrandimento della finestra rispetto al framebuffer.</summary>
    private int _scale = 2;

    private bool _running;
    private bool _paused;

    /// <summary>Statistiche mostrate nel titolo.</summary>
    private long _instructionsExecuted;
    private int _framesDrawn;
    private readonly Stopwatch _statsTimer = Stopwatch.StartNew();

    public EmulatorWindow(Machine machine)
    {
        _machine = machine;
        _sdl = Silk.NET.SDL.Sdl.GetApi();
    }

    public void Run()
    {
        Initialize();
        _running = true;

        var frameTimer = Stopwatch.StartNew();
        double accumulatedSeconds = 0;

        // 60 fotogrammi al secondo: piu' che sufficiente per una macchina che
        // ne produceva 70 in modo testo, e un buon compromesso fra fluidita'
        // e granularita' dell'esecuzione.
        const double TargetFrameSeconds = 1.0 / 60.0;

        while (_running)
        {
            PumpEvents();

            double elapsed = frameTimer.Elapsed.TotalSeconds;
            frameTimer.Restart();

            // Un limite sul tempo accumulato evita che, dopo una pausa lunga
            // (finestra ridotta a icona, sistema sotto carico), la macchina
            // tenti di recuperare eseguendo milioni di istruzioni in un colpo.
            accumulatedSeconds = Math.Min(accumulatedSeconds + elapsed, 0.25);

            while (accumulatedSeconds >= TargetFrameSeconds)
            {
                if (!_paused) RunOneFrame(TargetFrameSeconds);
                accumulatedSeconds -= TargetFrameSeconds;
            }

            Render();
            UpdateTitle();
        }
    }

    private void Initialize()
    {
        if (_sdl.Init(Silk.NET.SDL.Sdl.InitVideo | Silk.NET.SDL.Sdl.InitEvents) < 0)
            throw new InvalidOperationException($"SDL non inizializzato: {_sdl.GetErrorS()}");

        _textureWidth = _machine.Video.Width;
        _textureHeight = _machine.Video.Height;

        _window = _sdl.CreateWindow(
            "Emulatore 386",
            Silk.NET.SDL.Sdl.WindowposCentered, Silk.NET.SDL.Sdl.WindowposCentered,
            _textureWidth * _scale, _textureHeight * _scale,
            (uint)(WindowFlags.Shown | WindowFlags.Resizable));

        if (_window is null)
            throw new InvalidOperationException($"finestra non creata: {_sdl.GetErrorS()}");

        _renderer = _sdl.CreateRenderer(_window, -1,
            (uint)(RendererFlags.Accelerated | RendererFlags.Presentvsync));

        if (_renderer is null)
            throw new InvalidOperationException($"renderer non creato: {_sdl.GetErrorS()}");

        CreateTexture();
    }

    private void CreateTexture()
    {
        if (_texture is not null) _sdl.DestroyTexture(_texture);

        _textureWidth = _machine.Video.Width;
        _textureHeight = _machine.Video.Height;

        // Streaming: la texture viene riscritta a ogni fotogramma che cambia.
        _texture = _sdl.CreateTexture(_renderer,
            (uint)PixelFormatEnum.Abgr8888,
            (int)TextureAccess.Streaming,
            _textureWidth, _textureHeight);

        // Filtro a pixel netti: ingrandire un'immagine a 640x400 con
        // interpolazione la renderebbe sfocata invece che fedele.
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
        // Il cursore lampeggia due volte al secondo, come su un PC reale.
        _machine.Video.CursorBlinkOn = (_statsTimer.ElapsedMilliseconds / 500) % 2 == 0;

        bool changed = _machine.Video.Render();

        // Un cambio di modalita' video puo' aver ridimensionato il framebuffer.
        if (_machine.Video.Width != _textureWidth || _machine.Video.Height != _textureHeight)
        {
            CreateTexture();
            changed = true;
        }

        if (changed)
        {
            void* pixels;
            int pitch;
            if (_sdl.LockTexture(_texture, null, &pixels, &pitch) == 0)
            {
                ReadOnlySpan<uint> source = _machine.Video.Framebuffer;
                var destination = new Span<uint>(pixels, _textureWidth * _textureHeight);
                source[..Math.Min(source.Length, destination.Length)].CopyTo(destination);
                _sdl.UnlockTexture(_texture);
            }
        }

        _sdl.RenderClear(_renderer);

        // Mantiene le proporzioni originali quando la finestra viene
        // ridimensionata: un 640x400 stirato a caso e' peggio di uno con
        // bande nere ai lati.
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

        // I comandi dell'emulatore usano il tasto Meta (Command su Mac), che
        // il software emulato non puo' intercettare.
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

        // Ripetizione automatica di SDL: il BIOS emulato ha la propria, quindi
        // le pressioni ripetute vanno ignorate per non riempire il buffer.
        if (pressed && key.Repeat != 0) return;

        byte scanCode = KeyMap.ToScanCode(code);
        if (scanCode == 0) return;

        if (KeyMap.IsExtended(code)) _machine.Keyboard.PushScanCode(0xE0);

        if (pressed) _machine.PressKey(scanCode);
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
        if (_statsTimer.ElapsedMilliseconds < 1000) return;

        double seconds = _statsTimer.Elapsed.TotalSeconds;
        double mips = _instructionsExecuted / seconds / 1e6;
        int fps = (int)(_framesDrawn / seconds);

        string state = _paused ? " — in pausa" : "";
        _sdl.SetWindowTitle(_window,
            $"Emulatore 386 — {mips:F1} MIPS, {fps} fps{state}");

        _instructionsExecuted = 0;
        _framesDrawn = 0;
        _statsTimer.Restart();
    }

    public void Dispose()
    {
        if (_texture is not null) _sdl.DestroyTexture(_texture);
        if (_renderer is not null) _sdl.DestroyRenderer(_renderer);
        if (_window is not null) _sdl.DestroyWindow(_window);
        _sdl.Quit();
        _sdl.Dispose();
    }
}
