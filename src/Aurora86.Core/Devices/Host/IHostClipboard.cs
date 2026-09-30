namespace Aurora86.Core.Devices.Host;

/// <summary>
/// The host's clipboard as the emulator sees it: plain text in, plain text
/// out. A front end implements it over its own toolkit's clipboard; the
/// VMware-compatible backdoor reads it when a guest asks for the host's
/// selection and writes it when a guest hands one over
/// (docs/VMWARE-BACKDOOR.md section 8).
///
/// Both calls come from the emulation thread. Toolkits that allow clipboard
/// access only on their UI thread keep a copy of the text there and hand it
/// over, rather than blocking the emulation on the UI.
/// </summary>
public interface IHostClipboard
{
    /// <summary>The host's current text, or null when it holds none.</summary>
    string? GetText();

    /// <summary>Replaces the host's clipboard with <paramref name="text"/>.</summary>
    void SetText(string text);
}
