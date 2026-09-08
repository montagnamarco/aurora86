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
        // events, so anything still down has to be let go now -- the pointer
        // included. A hidden cursor that survives the switch to another
        // application is the worst thing this feature can do to a user: the
        // key that would release it now goes to somebody else's window.
        Deactivated += (_, _) =>
        {
            ReleaseHeldKeys();
            Screen.Capture.Release();
        };

        // The status bar has to say when the guest has the pointer, and by
        // which key to get it back, because there is no other clue: the
        // cursor has vanished and nothing on screen has changed.
        Screen.Capture.CapturedChanged += captured =>
            ViewModel?.ReportMouseCapture(captured, Screen.Capture.IsUnbounded);

        // Closing the window while the guest holds the pointer would leave
        // the cursor hidden after the process is gone -- hidden for the whole
        // desktop, not just for us, because the platform call underneath is
        // system-wide and counts its hides. Nothing would then bring it back
        // short of logging out.
        Closed += (_, _) => Screen.Capture.Release();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    /// <summary>
    /// Whether to print every key the host reports, for diagnosing a keyboard
    /// layout that behaves differently from the one the developer has.
    /// </summary>
    private static readonly bool KeyLogging =
        Environment.GetEnvironmentVariable("AURORA_KEYLOG") is not null;

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
        ViewModel.PickFolderAsync = PickFolderAsync;
        ViewModel.SaveScreenshotAsync = SaveScreenshotAsync;
        ViewModel.ShowDebugger = ShowDebugger;
        ViewModel.ShowVideoInspector = ShowVideoInspector;
    }

    /// <summary>
    /// Shows the debugger, reusing the window if it is already open rather
    /// than piling up copies.
    /// </summary>
    private DebuggerWindow? _debuggerWindow;

    /// <summary>The video memory window, kept the same way as the debugger.</summary>
    private VideoInspectorWindow? _videoWindow;

    private void ShowVideoInspector(VideoInspectorViewModel model)
    {
        if (_videoWindow is null)
        {
            _videoWindow = new VideoInspectorWindow { DataContext = model };
            _videoWindow.Closed += (_, _) => _videoWindow = null;
            _videoWindow.Show(this);
        }
        else
        {
            _videoWindow.DataContext = model;
            _videoWindow.Activate();
        }
    }

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
    private readonly HashSet<PhysicalKey> _keysHeld = new();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // A repeat of a key we already told the guest about: swallow it. The
        // emulated keyboard generates its own repeats, on its own clock, the
        // way the hardware does -- letting the host's repeats through as well
        // would give the guest two of them at two different rates.
        if (!_keysHeld.Add(e.PhysicalKey)) { e.Handled = true; return; }

        // Watch for the release gesture. This only observes -- the key still
        // travels to the guest below, because a Control the guest never saw
        // pressed is a Control it will never see released either, and it
        // would go on believing the modifier is held.
        Screen.Capture.NotifyKeyDown(e.PhysicalKey);

        // Set AURORA_KEYLOG=1 to print what the host reports for each key.
        //
        // This is the one link a test cannot check: what macOS hands Avalonia
        // before any of our code sees it. When typing goes wrong on a layout
        // nobody here can reproduce, this is what settles it.
        if (KeyLogging)
        {
            byte mapped = PhysicalKeyMap.ToScanCode(e.PhysicalKey, out bool ext);
            Console.WriteLine(
                $"[tasto] Key={e.Key} PhysicalKey={e.PhysicalKey} " +
                $"KeySymbol='{e.KeySymbol}' modificatori={e.KeyModifiers} " +
                $"-> scancode={mapped:X2}{(ext ? " (esteso)" : "")}" +
                (mapped == 0 ? "  NESSUNA POSIZIONE" : ""));
        }

        // Send the key position, and let the guest decide what it means.
        //
        // This is what a real keyboard does, and it is right whenever the two
        // machines agree about the layout -- including the case that matters
        // most here, where they agree because both are Italian: MS-DOS loads
        // KEYB from AUTOEXEC.BAT and translates the positions itself. Sending
        // a character translated to a US layout would then be translated a
        // second time, which is how a comma came out as an at sign.
        if (SendKey(e.PhysicalKey, released: false))
        {
            e.Handled = true;
            return;
        }

        // No position we know. The text event that follows can still salvage
        // it by character -- this is the path for keys the host has but a PC
        // keyboard does not.
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        _keysHeld.Remove(e.PhysicalKey);

        // The release gesture is recognised on the way up rather than the way
        // down, which is what lets the same key serve two masters: Ctrl-C is
        // a combination and reaches the guest untouched, while a Control
        // tapped on its own gives the pointer back. Either way the guest gets
        // the key -- the gesture is read from the traffic, not taken out of
        // it.
        Screen.Capture.NotifyKeyUp(e.PhysicalKey);

        if (SendKey(e.PhysicalKey, released: true)) e.Handled = true;
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
        foreach (PhysicalKey key in _keysHeld.ToArray())
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
        // Only reached when the key had no position we recognised, since a
        // recognised one marks the event handled. So this is the last resort:
        // ask which key of the guest's own layout would produce the character
        // the host says was typed.
        //
        // It is worth being clear about when this helps and when it cannot.
        // If the guest has loaded a keyboard layout of its own -- KEYB IT,
        // say -- then positions are already being translated there and this
        // path should stay out of the way, which it does by never being
        // reached for an ordinary key. It earns its keep for the keys a PC
        // keyboard simply does not have.
        if (string.IsNullOrEmpty(e.Text))
        {
            base.OnTextInput(e);
            return;
        }

        bool sent = false;
        foreach (char character in e.Text) sent |= SendCharacter(character);

        if (sent) e.Handled = true;
        else base.OnTextInput(e);
    }



    /// <summary>
    /// Pushes a key into the emulated keyboard controller.
    ///
    /// A PC keyboard reports presses and releases separately: the release is
    /// the same scancode with bit 7 set. Software needs both, because that is
    /// how it tracks whether Shift is being held.
    /// </summary>
    private bool SendKey(PhysicalKey key, bool released)
    {
        MainWindowViewModel? model = ViewModel;
        if (model is null) return false;

        byte code = PhysicalKeyMap.ToScanCode(key, out bool extended);
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

    /// <summary>
    /// Asks the user for a folder, for sharing with the guest.
    /// </summary>
    private async Task<string?> PickFolderAsync(string title)
    {
        IStorageProvider? storage = StorageProvider;
        if (storage is null) return null;

        IReadOnlyList<IStorageFolder> chosen = await storage.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title, AllowMultiple = false });

        return chosen.Count > 0 ? chosen[0].Path.LocalPath : null;
    }

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
