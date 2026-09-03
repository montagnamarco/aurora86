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

        DataContextChanged += OnDataContextChanged;

        // Deactivating the window is the moment the host stops sending us key
        // events, so anything still down has to be let go now.
        Deactivated += (_, _) => ReleaseHeldKeys();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

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
        // guest's own auto-repeat is what should drive this.
        if (!_keysHeld.Add(e.Key)) { e.Handled = true; return; }

        if (SendKey(e.Key, released: false)) e.Handled = true;
        else base.OnKeyDown(e);
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
    }

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
