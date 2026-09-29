using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Aurora386.Core.Devices.Host;

namespace Aurora386.Gui.Emulation;

/// <summary>
/// The thread-safe copy of the host clipboard's text behind <see
/// cref="AvaloniaHostClipboard"/>.
///
/// Split out from the Avalonia wiring so the one rule that matters --
/// whoever wrote last wins, and reading never blocks on writing -- can be
/// tested without a real window, a real platform clipboard, or a UI thread at
/// all.
/// </summary>
internal sealed class ClipboardTextCache
{
    private readonly object _gate = new();
    private string? _text;

    /// <summary>
    /// Counts guest writes. A host read captures it when it starts and is
    /// discarded if a guest write happened meanwhile: the read began before
    /// the guest's text reached the host, so what it returns is older than
    /// what the cache already holds.
    /// </summary>
    private long _guestWrites;

    /// <summary>
    /// Guest writes cached but not yet on the platform clipboard. While any
    /// is pending the platform still holds older text, so no host read that
    /// starts or ends in that window may be stored.
    /// </summary>
    private int _pendingPlatformWrites;

    /// <summary>The most recently known text, from either a host refresh or a guest write.</summary>
    public string? Read()
    {
        lock (_gate)
        {
            return _text;
        }
    }

    /// <summary>Replaces the cached text with the guest's selection.</summary>
    public void Write(string? text)
    {
        lock (_gate)
        {
            _text = text;
            _guestWrites++;
            _pendingPlatformWrites++;
        }
    }

    /// <summary>Records that one guest write has reached the platform clipboard, or failed to.</summary>
    public void PlatformWriteFinished()
    {
        lock (_gate)
        {
            if (_pendingPlatformWrites > 0)
            {
                _pendingPlatformWrites--;
            }
        }
    }

    /// <summary>
    /// The guest-write count a host read records when it starts, or -1 when a
    /// guest write is still on its way to the platform and the read would
    /// only see older text.
    /// </summary>
    public long BeginHostRead()
    {
        lock (_gate)
        {
            return _pendingPlatformWrites > 0 ? -1 : _guestWrites;
        }
    }

    /// <summary>
    /// Stores what a host read returned, unless a guest write landed after
    /// the read began (<paramref name="guestWritesAtStart"/>).
    /// </summary>
    public void CompleteHostRead(string? text, long guestWritesAtStart)
    {
        lock (_gate)
        {
            bool noGuestWriteMeanwhile = guestWritesAtStart >= 0 && _guestWrites == guestWritesAtStart;
            if (noGuestWriteMeanwhile && _pendingPlatformWrites == 0)
            {
                _text = text;
            }
        }
    }
}

/// <summary>
/// The host's clipboard, offered to the emulated machine through <see
/// cref="IHostClipboard"/> (docs/VMWARE-BACKDOOR.md section 8): a guest whose
/// VMware Tools has loaded the shared-clipboard channel reads the host's
/// selection through this, and writes its own selection back through it.
///
/// Avalonia's clipboard answers only on the UI thread, while <see
/// cref="IHostClipboard.GetText"/> and <see cref="IHostClipboard.SetText"/>
/// are called from the emulation thread -- see that interface's own summary.
/// This class never touches <see cref="TopLevel.Clipboard"/> from those
/// calls: <see cref="GetText"/> answers from a cache the UI thread keeps
/// fresh, and <see cref="SetText"/> stores into the same cache and posts the
/// actual platform write to the UI thread, never waiting for it.
///
/// The cache is refreshed on window activation -- the moment a paste from
/// another application is most likely to be wanted -- and on a timer that
/// only runs while the window is the active one, since nothing tells this
/// class that some other application changed the host clipboard in the
/// meantime. Polling a clipboard nobody can see is pointless, which is why
/// the timer stops the instant the window is deactivated.
/// </summary>
public sealed class AvaloniaHostClipboard : IHostClipboard, IDisposable
{
    /// <summary>
    /// How often the cache is refreshed while the window is active. Half a
    /// second is fast enough that a copy made in another application feels
    /// available immediately, and slow enough not to be a measurable cost.
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(500);

    private readonly Window _window;
    private readonly ClipboardTextCache _cache = new();
    private readonly DispatcherTimer _timer;

    /// <summary>
    /// Whether any machine can read the clipboard right now -- the backdoor
    /// enabled. The host clipboard is not polled for a machine that cannot
    /// ask for it: reading another application's clipboard is not free, and
    /// some systems note it as a privacy event.
    /// </summary>
    private readonly Func<bool> _wanted;

    public AvaloniaHostClipboard(Window window, Func<bool> wanted)
    {
        _window = window;
        _wanted = wanted;
        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += (_, _) => RefreshFromClipboard();

        _window.Activated += OnActivated;
        _window.Deactivated += OnDeactivated;

        // The window may already be the active one by the time a front end
        // gets around to building this -- the constructor is not
        // necessarily the first thing that runs after Show() -- so the
        // timer has to be started here rather than waiting for an
        // Activated event that may never come again.
        if (_window.IsActive)
        {
            OnActivated(_window, EventArgs.Empty);
        }
    }

    /// <summary>The cached copy of the host's clipboard text; never touches the UI thread.</summary>
    public string? GetText() => _cache.Read();

    /// <summary>
    /// Caches the guest's text at once, so a guest that writes and
    /// immediately reads back sees its own value, and posts the platform
    /// write to the UI thread without waiting for it to land.
    /// </summary>
    public void SetText(string text)
    {
        _cache.Write(text);
        Dispatcher.UIThread.Post(() => _ = WriteToClipboardAsync(text));
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        RefreshFromClipboard();
        _timer.Start();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        _timer.Stop();
    }

    private void RefreshFromClipboard() => _ = ReadFromClipboardAsync();

    /// <summary>
    /// Reads the platform clipboard and updates the cache. Runs fire-and-
    /// forget from a timer tick or an activation event, neither of which has
    /// anyone waiting on its result.
    /// </summary>
    private async Task ReadFromClipboardAsync()
    {
        IClipboard? clipboard = _window.Clipboard;
        if (clipboard is null || !_wanted())
        {
            return;
        }

        try
        {
            long guestWritesAtStart = _cache.BeginHostRead();
            string? text = await clipboard.TryGetTextAsync();
            _cache.CompleteHostRead(text, guestWritesAtStart);
        }
        catch (Exception)
        {
            // Some platforms refuse a clipboard read outside a user gesture
            // (a browser's Wasm host is one). Leaving the cache as it was is
            // closer to correct than propagating an exception out of a timer
            // tick, which the emulation thread that actually wants this text
            // never sees anyway.
        }
    }

    /// <summary>
    /// How long a platform write may take before it is given up on. Some
    /// clipboards never answer -- an X11 selection owner that hangs, for
    /// one -- and a write left pending would refuse every later host read.
    /// </summary>
    private static readonly TimeSpan PlatformWriteTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Puts the guest's text on the platform clipboard. However it ends --
    /// done, refused, timed out, or no clipboard at all -- the write stops
    /// counting as pending, so host reads resume.
    /// </summary>
    private async Task WriteToClipboardAsync(string text)
    {
        try
        {
            IClipboard? clipboard = _window.Clipboard;
            if (clipboard is null)
            {
                return;
            }

            Task write = clipboard.SetTextAsync(text);
            await Task.WhenAny(write, Task.Delay(PlatformWriteTimeout));
        }
        catch (Exception)
        {
            // As above: a platform that refuses the write leaves the guest's
            // own idea of the clipboard intact in the cache, which is the
            // best available answer to the next GetText.
        }
        finally
        {
            _cache.PlatformWriteFinished();
        }
    }

    /// <summary>Stops polling and releases the window's events. Safe to call more than once.</summary>
    public void Dispose()
    {
        _timer.Stop();
        _window.Activated -= OnActivated;
        _window.Deactivated -= OnDeactivated;
    }
}
