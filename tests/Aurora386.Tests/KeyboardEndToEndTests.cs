using Aurora386.Core;
using Aurora386.Gui;
using Aurora386.Gui.ViewModels;
using Aurora386.Gui.Views;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;

namespace Aurora386.Tests;

/// <summary>
/// Starts the real application once, without a screen, so that tests can press
/// keys on the actual window.
/// </summary>
public sealed class HeadlessApp : IDisposable
{
    private readonly CancellationTokenSource _stop = new();

    public HeadlessApp()
    {
        var started = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                .SetupWithoutStarting();

            started.Set();
            Dispatcher.UIThread.MainLoop(_stop.Token);
        })
        { IsBackground = true };

        // No apartment state: that is a Windows notion, and asking for it
        // elsewhere throws.
        thread.Start();

        started.Wait(TimeSpan.FromSeconds(30));
    }

    /// <summary>Runs work on the UI thread and waits for it.</summary>
    public T Invoke<T>(Func<T> work) => Dispatcher.UIThread.InvokeAsync(work).GetTask().GetAwaiter().GetResult();

    public void Invoke(Action work) => Dispatcher.UIThread.InvokeAsync(work).GetTask().GetAwaiter().GetResult();

    public void Dispose() => _stop.Cancel();
}

/// <summary>
/// The whole keyboard path, driven the way a user drives it.
///
/// Every other keyboard test in this project starts partway along: it pushes
/// scancodes into the controller, or calls the translation table directly.
/// Those tests all passed while typing was still broken, because the fault lay
/// in the part they skipped -- how the window turns an Avalonia key event into
/// a scancode at all.
///
/// So these press keys on the real window and read what the guest received.
/// It is the only arrangement that could have caught what was wrong.
/// </summary>
public class KeyboardEndToEndTests : IClassFixture<HeadlessApp>
{
    private readonly HeadlessApp _app;

    public KeyboardEndToEndTests(HeadlessApp app) => _app = app;

    /// <summary>A window with a machine behind it, ready to be typed at.</summary>
    private (MainWindow Window, Machine Machine) Open() => _app.Invoke(() =>
    {
        var model = new MainWindowViewModel();
        var window = new MainWindow { DataContext = model };
        window.Show();

        Machine? machine = null;
        model.Session.WithMachine(m => machine = m);
        Assert.NotNull(machine);

        return (window, machine!);
    });

    /// <summary>
    /// Presses a key on the window, then reads every scancode the emulated
    /// controller was given.
    ///
    /// Port 0x64 bit 0 says a byte is waiting; reading port 0x60 takes it and
    /// lets the next one through, which is what the guest's own interrupt
    /// handler does.
    /// </summary>
    private List<byte> Press(MainWindow window, Machine machine, Key key,
                             KeyModifiers modifiers = KeyModifiers.None) => _app.Invoke(() =>
    {
        Drain(machine);

        window.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
        });

        return Drain(machine);
    });

    private static List<byte> Drain(Machine machine)
    {
        var codes = new List<byte>();

        while ((machine.Io.ReadByte(0x64) & 0x01) != 0)
            codes.Add(machine.Io.ReadByte(0x60));

        return codes;
    }

    [Fact]
    public void APlainLetterReachesTheGuestAsItsOwnKeyPosition()
    {
        (MainWindow window, Machine machine) = Open();

        // 0x2E is where C sits on every PC keyboard. What the guest makes of
        // that position is the guest's business: with an Italian layout
        // loaded it is still C, because the letters do not move.
        Assert.Contains((byte)0x2E, Press(window, machine, Key.C));
    }

    [Fact]
    public void EscapeReachesTheGuestAsEscape()
    {
        // Escape produces no character at all, so a path that decided things
        // by character had nothing to go on and sent something else entirely.
        // That is how pressing Escape came out as a backslash.
        (MainWindow window, Machine machine) = Open();

        Assert.Contains((byte)0x01, Press(window, machine, Key.Escape));
    }

    [Fact]
    public void ThePositionIsSentUntranslated()
    {
        // The key beside the full stop. On an Italian keyboard it carries the
        // comma and semicolon; on an American one the comma and less-than.
        // The frontend must not care -- it sends the position, and whatever
        // layout the guest has loaded decides. Translating here is what made
        // a comma arrive as an at sign: the guest's own KEYB then translated
        // it a second time.
        (MainWindow window, Machine machine) = Open();

        Assert.Contains((byte)0x33, Press(window, machine, Key.OemComma));
    }

    [Fact]
    public void ThePunctuationKeysKeepTheirOwnPositions()
    {
        // Each of these carries different characters on an Italian keyboard
        // than on an American one, which is exactly why the position has to
        // travel untouched.
        (MainWindow window, Machine machine) = Open();

        Assert.Contains((byte)0x34, Press(window, machine, Key.OemPeriod));
        Assert.Contains((byte)0x27, Press(window, machine, Key.OemSemicolon));
        Assert.Contains((byte)0x2B, Press(window, machine, Key.OemBackslash));
        Assert.Contains((byte)0x35, Press(window, machine, Key.OemQuestion));
    }

    [Fact]
    public void EnterReachesTheGuest()
    {
        // The one key without which nothing can be done at all.
        (MainWindow window, Machine machine) = Open();

        Assert.Contains((byte)0x1C, Press(window, machine, Key.Enter));
    }

    [Fact]
    public void AnArrowKeyCarriesTheExtendedPrefix()
    {
        // The arrows share their scancodes with the numeric keypad and are
        // told apart only by a 0xE0 byte in front. A bare 0x48 means the
        // keypad's 8, which is a digit rather than a movement.
        (MainWindow window, Machine machine) = Open();

        List<byte> codes = Press(window, machine, Key.Up);

        int prefix = codes.IndexOf(0xE0);
        Assert.True(prefix >= 0, "the extended prefix never reached the guest");
        Assert.Equal(0x48, codes[prefix + 1]);
    }

    [Fact]
    public void ReleasingAKeySendsItsBreakCode()
    {
        // Without the release the guest believes the key is still held, and
        // the keyboard's own repeat starts filling the buffer with it.
        (MainWindow window, Machine machine) = Open();

        Press(window, machine, Key.A);

        List<byte> codes = _app.Invoke(() =>
        {
            Drain(machine);

            window.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyUpEvent,
                Key = Key.A,
                KeyModifiers = KeyModifiers.None,
            });

            return Drain(machine);
        });

        Assert.Contains((byte)(0x1E | 0x80), codes);
    }
}
