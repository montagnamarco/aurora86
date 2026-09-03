using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Aurora386.Gui.Emulation;
using Aurora386.Gui.ViewModels;

namespace Aurora386.Gui.Views;

/// <summary>
/// The main window. Its job is the part a view model cannot do: turning
/// keystrokes into scancodes, and putting the platform's file picker and
/// screenshot facilities behind the callbacks the view model asks for.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // The screen must have focus for the guest to receive keystrokes, and
        // it should get it back whenever the user clicks anywhere in the
        // window -- a toolbar button stealing focus permanently would be a
        // nasty surprise mid-typing.
        Opened += (_, _) => Screen.Focus();
        PointerPressed += (_, _) => Screen.Focus();

        // Give the guest the keyboard back after any toolbar command.
        //
        // Clicking a button moves the focus to that button, and it stays
        // there: the next thing typed goes to the toolbar, not the machine,
        // and pressing space would helpfully press the button again. This is
        // what a virtual machine does -- the moment the machine is running,
        // the keyboard belongs to it -- so the focus is handed back once the
        // click has been dealt with.
        AddHandler(Button.ClickEvent, (_, _) => ReturnFocusToGuest(), handledEventsToo: true);

        DataContextChanged += OnDataContextChanged;

        // Deactivating the window is the moment the host stops sending us key
        // events, so anything still down has to be let go now.
        Deactivated += (_, _) => ReleaseHeldKeys();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    /// <summary>
    /// Hands the keyboard back to the emulated machine, after whatever piece
    /// of the interface the user just used has finished with it.
    ///
    /// The focus is restored on the dispatcher rather than immediately: a
    /// button that opens a menu or a file picker is still settling its own
    /// focus when the click is reported, and taking it away in the middle of
    /// that would either be undone a moment later or close the menu that was
    /// just opened.
    /// </summary>
    private void ReturnFocusToGuest() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // A dialog or a flyout is on screen and is entitled to the keys.
            if (!IsActive) return;
            Screen.Focus();
        }, Avalonia.Threading.DispatcherPriority.Background);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (ViewModel is null) return;

        ViewModel.PickFileAsync = PickFileAsync;
        ViewModel.SaveScreenshotAsync = SaveScreenshotAsync;
        ViewModel.ShowDebugger = ShowDebugger;
    }

    /// <summary>
    /// Shows the debugger, reusing the window if it is already open rather
    /// than piling up copies.
    /// </summary>
    private DebuggerWindow? _debuggerWindow;

    private void ShowDebugger(DebuggerViewModel model)
    {
        if (_debuggerWindow is null)
        {
            _debuggerWindow = new DebuggerWindow { DataContext = model };
            _debuggerWindow.Closed += (_, _) => _debuggerWindow = null;
            _debuggerWindow.Show(this);
        }
        else
        {
            _debuggerWindow.DataContext = model;
            _debuggerWindow.Activate();
        }
    }

    // --- keyboard -----------------------------------------------------------

    /// <summary>
    /// Keys the guest currently believes are held down.
    ///
    /// The host repeats key-down events while a key is held, but a PC keyboard
    /// does its own repeating -- the BIOS decides the rate from its own timer.
    /// Forwarding the host's repeats as well would push a fresh scancode into
    /// the controller's sixteen-entry buffer many times a second: the buffer
    /// fills, and DOS is left with a queue of characters it will keep echoing
    /// long after the key was let go. That is the runaway prompt.
    /// </summary>
    private readonly HashSet<Key> _keysHeld = new();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // A repeat of a key we already told the guest about: swallow it. The
        // emulated keyboard generates its own repeats, on its own clock, the
        // way the hardware does -- letting the host's repeats through as well
        // would give the guest two of them at two different rates.
        if (!_keysHeld.Add(e.Key)) { e.Handled = true; return; }

        byte code = AvaloniaKeyMap.ToScanCode(e.Key, out _);

        // A key that produces a character is decided by the text event that
        // follows, not here.
        //
        // Marking the event handled would stop that text event from ever
        // being generated, and then a keyboard whose layout differs from the
        // guest's could never be corrected: the position would always win,
        // which is exactly how "C:" came out as something else. So the
        // printable keys are deliberately left to fall through, and only the
        // ones that produce no character at all -- Enter, the arrows, the
        // function keys, the modifiers -- are settled by position.
        if (code != 0 && !ProducesCharacter(e.Key))
        {
            SendKey(e.Key, released: false);
            _lastPositionalCode = 0;
            e.Handled = true;
            return;
        }

        // Remember the position, so the text event can compare it against
        // what the host says was typed.
        _lastPositionalCode = code;
        _lastPositionalKey = e.Key;

        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        _keysHeld.Remove(e.Key);

        if (SendKey(e.Key, released: true)) e.Handled = true;
        else base.OnKeyUp(e);
    }

    /// <summary>
    /// Releases every held key when the window loses focus.
    ///
    /// Without this, switching away mid-keystroke leaves the guest convinced
    /// the key is still down: the host delivers the key-up to whichever window
    /// took the focus, and ours never hears about it. The guest then repeats
    /// that character indefinitely, and even Ctrl-C cannot get through because
    /// the guest thinks a key is already stuck.
    /// </summary>
    private void ReleaseHeldKeys()
    {
        foreach (Key key in _keysHeld.ToArray())
            SendKey(key, released: true);

        _keysHeld.Clear();

        // Tell the keyboard itself to let go too. It repeats the held key on
        // its own clock, and it has no other way of learning that the key is
        // no longer down.
        ViewModel?.Session.WithMachine(machine => machine.Keyboard.ClearHeldKeys());
    }

    /// <summary>
    /// Types a character the host produced but whose physical key we cannot
    /// place, by pressing whichever PC keys would have produced it.
    ///
    /// Scancodes name key positions, not characters, and that is normally the
    /// right thing: the guest's own keyboard layout decides what each position
    /// means. It breaks down when the host layout puts a character somewhere
    /// the PC keyboard has no key at all. On an Italian Mac the backslash is
    /// one of these -- it is typed with Alt+Shift+/ , a combination the guest
    /// would read as three unrelated keypresses -- so `cd \` could not be
    /// typed at all.
    ///
    /// For those few characters we go the other way round and send the key
    /// position that produces the character on the guest's US layout.
    /// </summary>
    private bool SendCharacter(char character)
    {
        (byte code, bool shift) = UsKeyboardLayout.KeyForCharacter(character);

        if (code == 0) return false;

        MainWindowViewModel? model = ViewModel;
        if (model is null) return false;

        model.Session.WithMachine(machine =>
        {
            const byte LeftShift = 0x2A;
            if (shift) machine.Keyboard.PushScanCode(LeftShift);
            machine.Keyboard.PushScanCode(code);
            machine.Keyboard.PushScanCode((byte)(code | 0x80));
            if (shift) machine.Keyboard.PushScanCode(LeftShift | 0x80);
        });

        return true;
    }



    /// <summary>
    /// Last resort for characters no key position of ours produced: the host
    /// tells us what was typed, and we type it into the guest.
    /// </summary>
    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text))
        {
            base.OnTextInput(e);
            return;
        }

        // The character the host says was typed is the authority here. It
        // knows the user's keyboard layout; we know only which key moved, and
        // the two disagree on every layout that is not American -- an Italian
        // keyboard puts the colon on the full stop's key, which the guest
        // reads as a greater-than sign.
        //
        // So the key is sent as whatever position produces this character on
        // the guest's own layout, and the position the user physically
        // pressed is not used at all.
        bool sent = false;
        foreach (char character in e.Text) sent |= SendCharacter(character);

        if (sent)
        {
            e.Handled = true;
            _lastPositionalCode = 0;
            return;
        }

        // No key position produces it -- an accented letter, say, which the
        // guest's layout has no way of typing. Fall back to the position that
        // was actually pressed, which at least gives the guest something.
        if (_lastPositionalCode != 0)
        {
            SendKey(_lastPositionalKey, released: false);
            e.Handled = true;
            return;
        }

        base.OnTextInput(e);
    }



    private byte _lastPositionalCode;
    private Key _lastPositionalKey;

    /// <summary>
    /// Whether a key is one that types a character, as opposed to one that
    /// commands something.
    ///
    /// The distinction decides who settles the keystroke. A character key is
    /// settled by the character the host says it produced, because the host
    /// knows the user's layout and we do not. Everything else has no
    /// character to go by and is settled by its position, which is the same
    /// on every keyboard.
    /// </summary>
    private static bool ProducesCharacter(Key key) => key switch
    {
        >= Key.A and <= Key.Z => true,
        >= Key.D0 and <= Key.D9 => true,
        >= Key.NumPad0 and <= Key.NumPad9 => true,
        Key.Space => true,
        Key.OemPlus or Key.OemComma or Key.OemMinus or Key.OemPeriod => true,
        Key.OemQuestion or Key.OemSemicolon or Key.OemQuotes or Key.OemTilde => true,
        Key.OemOpenBrackets or Key.OemCloseBrackets => true,
        Key.OemBackslash or Key.OemPipe => true,
        Key.Add or Key.Subtract or Key.Multiply or Key.Divide or Key.Decimal => true,
        _ => false,
    };


    /// <summary>
    /// Pushes a key into the emulated keyboard controller.
    ///
    /// A PC keyboard reports presses and releases separately: the release is
    /// the same scancode with bit 7 set. Software needs both, because that is
    /// how it tracks whether Shift is being held.
    /// </summary>
    private bool SendKey(Key key, bool released)
    {
        MainWindowViewModel? model = ViewModel;
        if (model is null) return false;

        byte code = AvaloniaKeyMap.ToScanCode(key, out bool extended);
        if (code == 0) return false;

        model.Session.WithMachine(machine =>
        {
            // Extended keys are announced by a 0xE0 byte ahead of the code,
            // which is how the controller distinguishes the arrow keys from
            // the numeric keypad keys that share their scancodes.
            if (extended) machine.Keyboard.PushScanCode(0xE0);
            machine.Keyboard.PushScanCode(released ? (byte)(code | 0x80) : code);
        });

        return true;
    }

    // --- platform services the view model asks for --------------------------

    private async Task<string?> PickFileAsync(string title, IReadOnlyList<string> extensions)
    {
        IStorageProvider? storage = StorageProvider;
        if (storage is null) return null;

        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Disk images")
                {
                    Patterns = extensions.Select(extension => $"*.{extension}").ToArray(),
                },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        };

        IReadOnlyList<IStorageFile> chosen = await storage.OpenFilePickerAsync(options);
        return chosen.Count > 0 ? chosen[0].Path.LocalPath : null;
    }

    private Task SaveScreenshotAsync(string path)
    {
        // Render the control into a bitmap at its natural size. This captures
        // what is actually on screen, aspect correction included.
        var size = new Avalonia.PixelSize(
            Math.Max(1, (int)Screen.Bounds.Width),
            Math.Max(1, (int)Screen.Bounds.Height));

        using var target = new RenderTargetBitmap(size);
        target.Render(Screen);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        target.Save(path);

        return Task.CompletedTask;
    }
}
