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

        if (SendKey(e.Key, released: false))
        {
            _lastKeyWasMapped = true;

            // Remember what was sent, so that the text event arriving next
            // can tell whether it means the same thing.
            _lastPositionalCode = AvaloniaKeyMap.ToScanCode(e.Key, out _);
            _lastPositionalShift = (e.KeyModifiers & KeyModifiers.Shift) != 0;

            e.Handled = true;
        }
        else
        {
            // Not a key position we know. Leave the event alone so that the
            // text input that follows can be handled by character instead.
            _lastKeyWasMapped = false;
            base.OnKeyDown(e);
        }
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
        (byte code, bool shift) = UsKeyForCharacter(character);

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
    /// The key position, and whether shift is held, that produces a character
    /// on the guest's US keyboard layout.
    ///
    /// Every printable ASCII character is here rather than a chosen few. The
    /// selection used to be a handful of symbols that had come up in
    /// practice, and the trouble with that is the ones nobody thought of: a
    /// colon on an Italian Mac is Shift and a key the guest reads as
    /// something else entirely, so typing "C:" produced "C@" -- and every
    /// layout in the world has its own version of that surprise. Covering the
    /// whole printable range costs a table and removes the category.
    /// </summary>
    private static (byte Code, bool Shift) UsKeyForCharacter(char character) => character switch
    {
        // Digits and the symbols above them.
        '1' => (0x02, false), '!' => (0x02, true),
        '2' => (0x03, false), '@' => (0x03, true),
        '3' => (0x04, false), '#' => (0x04, true),
        '4' => (0x05, false), '$' => (0x05, true),
        '5' => (0x06, false), '%' => (0x06, true),
        '6' => (0x07, false), '^' => (0x07, true),
        '7' => (0x08, false), '&' => (0x08, true),
        '8' => (0x09, false), '*' => (0x09, true),
        '9' => (0x0A, false), '(' => (0x0A, true),
        '0' => (0x0B, false), ')' => (0x0B, true),
        '-' => (0x0C, false), '_' => (0x0C, true),
        '=' => (0x0D, false), '+' => (0x0D, true),

        // The punctuation keys, each with its shifted twin.
        '[' => (0x1A, false), '{' => (0x1A, true),
        ']' => (0x1B, false), '}' => (0x1B, true),
        ';' => (0x27, false), ':' => (0x27, true),
        '\'' => (0x28, false), '"' => (0x28, true),
        '`' => (0x29, false), '~' => (0x29, true),
        '\\' => (0x2B, false), '|' => (0x2B, true),
        ',' => (0x33, false), '<' => (0x33, true),
        '.' => (0x34, false), '>' => (0x34, true),
        '/' => (0x35, false), '?' => (0x35, true),

        // Letters. Upper case is the same key with shift, which is also what
        // makes a capital arrive correctly when the host produced it with
        // caps lock rather than with the shift key.
        >= 'a' and <= 'z' => (LetterKey(character), false),
        >= 'A' and <= 'Z' => (LetterKey(char.ToLowerInvariant(character)), true),

        ' ' => (0x39, false),

        _ => (0, false),
    };

    /// <summary>
    /// The key position of a lower-case letter. The letters are not in
    /// alphabetical order on a keyboard, so this is a lookup rather than
    /// arithmetic -- the rows are QWERTYUIOP, ASDFGHJKL, ZXCVBNM.
    /// </summary>
    private static byte LetterKey(char letter)
    {
        const string Order = "qwertyuiopasdfghjklzxcvbnm";
        byte[] codes =
        [
            0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19,   // q-p
            0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26,         // a-l
            0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x32,                     // z-m
        ];

        int index = Order.IndexOf(letter);
        return index < 0 ? (byte)0 : codes[index];
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

        // If the key position we already sent produces this very character on
        // the guest's layout, we are done: the ordinary path handled it.
        if (_lastKeyWasMapped && PositionAlreadyProduced(e.Text))
        {
            base.OnTextInput(e);
            return;
        }

        // Otherwise the host and the guest disagree about what that key
        // means, and the character is the thing to trust.
        //
        // The two layouts only have to differ slightly for this to matter. On
        // an Italian keyboard the colon shares a key with the full stop; the
        // guest, reading a US layout, takes that same position with shift to
        // mean ">". Typing "C:" therefore produced something else entirely,
        // and no amount of correctness in the position mapping would fix it,
        // because the position is not what the user meant -- the character
        // is.
        bool sent = false;
        foreach (char character in e.Text) sent |= SendCharacter(character);

        if (sent)
        {
            e.Handled = true;

            // Take back the keypress the position mapping already sent, or
            // the guest sees both.
            ReleaseLastPositionalKey();
        }
        else
        {
            base.OnTextInput(e);
        }
    }

    /// <summary>
    /// Whether the key position just sent produces exactly this text on the
    /// guest's own layout, in which case there is nothing more to do.
    /// </summary>
    private bool PositionAlreadyProduced(string text)
    {
        if (text.Length != 1) return false;

        (byte code, bool shift) = UsKeyForCharacter(text[0]);
        if (code == 0) return false;

        return code == _lastPositionalCode && shift == _lastPositionalShift;
    }

    /// <summary>
    /// Undoes a keypress sent by position when the character turned out to
    /// mean something else.
    ///
    /// The make code has already reached the guest, so the break code has to
    /// follow it: a key that is pressed and never released would repeat for
    /// ever. The keystroke itself is harmless -- DOS reads characters from
    /// the BIOS buffer, and the one this produced is replaced by the correct
    /// one arriving immediately after.
    /// </summary>
    private void ReleaseLastPositionalKey()
    {
        if (_lastPositionalCode == 0) return;

        byte code = _lastPositionalCode;
        _lastPositionalCode = 0;

        ViewModel?.Session.WithMachine(machine =>
        {
            machine.Keyboard.PushScanCode((byte)(code | 0x80));
            machine.Keyboard.ClearHeldKeys();
        });
    }

    private byte _lastPositionalCode;
    private bool _lastPositionalShift;

    /// <summary>
    /// Whether the last key press was one we recognised by position. When it
    /// was, the text event that follows is the same keystroke arriving a
    /// second time and must be ignored.
    /// </summary>
    private bool _lastKeyWasMapped;

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
