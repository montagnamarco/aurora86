using Aurora386.Gui.Emulation;

namespace Aurora386.Tests;

/// <summary>
/// The cache behind <see cref="AvaloniaHostClipboard"/>, tested on its own
/// because the class it backs needs a real Avalonia window and a real
/// platform clipboard to exercise otherwise -- see that class's own summary
/// for why the emulation thread never touches either directly.
/// </summary>
public class AvaloniaHostClipboardTests
{
    [Fact]
    public void ANewCacheHoldsNoText()
    {
        var cache = new ClipboardTextCache();

        Assert.Null(cache.Read());
    }

    [Fact]
    public void WhatWasWrittenIsWhatComesBack()
    {
        var cache = new ClipboardTextCache();

        cache.Write("hello from the host");

        Assert.Equal("hello from the host", cache.Read());
    }

    [Fact]
    public void TheLatestWriteWinsWhicheverSideMadeIt()
    {
        // GetText and SetText run on the emulation thread; the periodic
        // refresh and the platform write both run on the UI thread later.
        // Whichever wrote last is what the next read must answer, since
        // neither side has any other way of knowing which of them is stale.
        var cache = new ClipboardTextCache();

        cache.Write("from the guest");
        cache.Write("from the host");

        Assert.Equal("from the host", cache.Read());
    }

    [Fact]
    public void WritingNullClearsTheCachedText()
    {
        // A host refresh finding no text on the platform clipboard writes
        // null, exactly what IHostClipboard.GetText's own contract promises
        // for "the host holds none" -- not the empty string, which would
        // claim there is a selection when there is not.
        var cache = new ClipboardTextCache();

        cache.Write("something");
        cache.Write(null);

        Assert.Null(cache.Read());
    }

    /// <summary>
    /// A host read that began before the guest wrote returns the host's
    /// older text; storing it would hand the guest its previous selection
    /// back. It is discarded.
    /// </summary>
    [Fact]
    public void AHostReadStartedBeforeAGuestWriteIsDiscarded()
    {
        var cache = new ClipboardTextCache();

        long started = cache.BeginHostRead();
        cache.Write("from the guest");
        cache.CompleteHostRead("older host text", started);

        Assert.Equal("from the guest", cache.Read());
    }

    /// <summary>
    /// A host read that starts after the guest's text is cached but before
    /// it has reached the platform clipboard would read the older platform
    /// text: it is discarded too.
    /// </summary>
    [Fact]
    public void AHostReadStartedWhileAGuestWriteIsOnItsWayIsDiscarded()
    {
        var cache = new ClipboardTextCache();
        cache.Write("from the guest");

        long started = cache.BeginHostRead();
        cache.PlatformWriteFinished();
        cache.CompleteHostRead("older host text", started);

        Assert.Equal("from the guest", cache.Read());
    }

    [Fact]
    public void AHostReadWithNoGuestWriteMeanwhileIsKept()
    {
        var cache = new ClipboardTextCache();
        cache.Write("from the guest");
        cache.PlatformWriteFinished();

        long started = cache.BeginHostRead();
        cache.CompleteHostRead("copied on the host", started);

        Assert.Equal("copied on the host", cache.Read());
    }
}
