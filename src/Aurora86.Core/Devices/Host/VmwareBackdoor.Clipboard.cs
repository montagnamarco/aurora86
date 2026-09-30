using System.Text;
using Aurora86.Core.Cpu;

namespace Aurora86.Core.Devices.Host;

/// <summary>
/// The four legacy clipboard commands -- GETSELLENGTH, GETNEXTPIECE,
/// SETSELLENGTH, SETNEXTPIECE -- that predate the RPC channel's clipboard
/// exchange and still work standalone: open-vm-tools' compatibility shim
/// (<c>services/plugins/dndcp/copyPasteCompat.c</c>) drives exactly these
/// four when the guest tools are old enough to know nothing about RPCI.
/// Unlike MESSAGE, there is no open/close step and no length cap published
/// by VMware itself; every bound named below (docs/VMWARE-BACKDOOR.md
/// section 8) is Aurora86's own choice, not a fact taken from a reference
/// implementation.
///
/// Each direction is a length announcement followed by the payload four
/// bytes at a time, the same shape MESSAGE's SENDSIZE/SENDPAYLOAD pair
/// uses (docs/VMWARE-BACKDOOR.md section 5), which is why the two field
/// groups below buffer a transfer the same way that channel does.
/// </summary>
public sealed partial class VmwareBackdoor
{
    // Command numbers (ECX low word, docs/VMWARE-BACKDOOR.md section 3),
    // interleaved with the newer commands above 10 because that is the
    // numbering VMware itself assigned them.
    private const ushort CommandGetSelLength = 6;
    private const ushort CommandGetNextPiece = 7;
    private const ushort CommandSetSelLength = 8;
    private const ushort CommandSetNextPiece = 9;

    /// <summary>
    /// The cap Aurora86 places on a clipboard transfer in either direction:
    /// not a VMware constant, but a bound against a guest or a host
    /// clipboard holding something unbounded. A host selection longer than
    /// this is truncated at a UTF-8 character boundary; a guest
    /// SETSELLENGTH longer than this is ignored outright, discarding every
    /// SETNEXTPIECE that follows it until the next SETSELLENGTH.
    /// </summary>
    /// <remarks>
    /// 65436 rather than a round 64 KB: the independent review recalled a
    /// limit of (1 &lt;&lt; 16) - 100 in open-vm-tools' copy-paste code, above
    /// which a guest drops the selection whole. Not confirmed against the
    /// source; staying under it costs nothing.
    /// </remarks>
    private const int MaxClipboardBytes = (1 << 16) - 100;

    /// <summary>
    /// The host selection's snapshot, taken by GETSELLENGTH and walked by
    /// GETNEXTPIECE, so that a host clipboard change mid-transfer cannot
    /// tear what the guest reads: GETNEXTPIECE never re-reads
    /// <see cref="Clipboard"/>, only this array. Empty until the first
    /// GETSELLENGTH, which is also what a guest that calls GETNEXTPIECE
    /// first sees -- zero bytes, exactly as past the end of a real transfer.
    /// </summary>
    private byte[] _hostSelectionSnapshot = Array.Empty<byte>();

    /// <summary>How far GETNEXTPIECE has walked into <see cref="_hostSelectionSnapshot"/>.</summary>
    private int _hostSelectionReadOffset;

    /// <summary>
    /// True between a non-empty SETSELLENGTH and the SETNEXTPIECE that
    /// completes it. False both before the first SETSELLENGTH and again
    /// once the buffer has been handed to <see cref="Clipboard"/>, so a
    /// stray SETNEXTPIECE outside a transfer is simply discarded.
    /// </summary>
    private bool _guestSelectionPending;

    /// <summary>The byte length SETSELLENGTH announced for the transfer in progress.</summary>
    private int _guestSelectionDeclaredLength;

    /// <summary>The guest selection's bytes, assembled four at a time by SETNEXTPIECE.</summary>
    private readonly List<byte> _guestSelectionBuffer = new();

    private void HandleGetSelLength(CpuState state)
    {
        string hostText = Clipboard?.GetText() ?? string.Empty;
        _hostSelectionSnapshot = EncodeAndCapUtf8(hostText, MaxClipboardBytes);
        _hostSelectionReadOffset = 0;

        // Never negative: an absent front end or an empty host clipboard is
        // a selection of length zero, not an error code. VMware's own error
        // constants are not published, and guests already treat "no
        // selection" as anything not greater than zero, so zero is the
        // unambiguous answer for both cases.
        state.Eax = (uint)_hostSelectionSnapshot.Length;
    }

    private void HandleGetNextPiece(CpuState state)
    {
        uint piece = 0;
        for (int byteIndex = 0; byteIndex < 4; byteIndex++)
        {
            if (_hostSelectionReadOffset >= _hostSelectionSnapshot.Length) break;
            piece |= (uint)_hostSelectionSnapshot[_hostSelectionReadOffset] << (byteIndex * 8);
            _hostSelectionReadOffset++;
        }

        state.Eax = piece;
    }

    private void HandleSetSelLength(CpuState state)
    {
        uint declaredLength = state.Ebx;

        if (declaredLength > MaxClipboardBytes)
        {
            // Oversized: ignore the announcement outright rather than
            // truncate it, so every SETNEXTPIECE that follows is discarded
            // until the guest announces a length we are willing to hold.
            _guestSelectionPending = false;
            _guestSelectionDeclaredLength = 0;
            _guestSelectionBuffer.Clear();
            return;
        }

        _guestSelectionDeclaredLength = (int)declaredLength;
        _guestSelectionBuffer.Clear();

        // A zero-length selection has nothing for SETNEXTPIECE to append,
        // so it is complete the instant it is announced -- but it hands
        // nothing to Clipboard.SetText, because "the guest selection is
        // empty" is not the same claim as "replace the host clipboard with
        // an empty string", and only the former is true here.
        _guestSelectionPending = declaredLength > 0;
    }

    private void HandleSetNextPiece(CpuState state)
    {
        if (!_guestSelectionPending) return;

        uint piece = state.Ebx;
        int stillWanted = _guestSelectionDeclaredLength - _guestSelectionBuffer.Count;
        int bytesFromThisDword = Math.Min(4, stillWanted);
        for (int byteIndex = 0; byteIndex < bytesFromThisDword; byteIndex++)
        {
            _guestSelectionBuffer.Add((byte)(piece >> (byteIndex * 8)));
        }

        if (_guestSelectionBuffer.Count < _guestSelectionDeclaredLength) return;

        string guestText = Encoding.UTF8.GetString(_guestSelectionBuffer.ToArray());
        Clipboard?.SetText(guestText);
        _guestSelectionPending = false;
        _guestSelectionBuffer.Clear();
    }

    /// <summary>
    /// Encodes <paramref name="text"/> as UTF-8 and, if that exceeds
    /// <paramref name="maxBytes"/>, cuts it back to the last character
    /// boundary at or before the cap. A UTF-8 continuation byte always has
    /// its top two bits set to 10, so backing away from the cut point while
    /// the byte immediately after it looks like a continuation byte finds
    /// that boundary without decoding a single character.
    /// </summary>
    private static byte[] EncodeAndCapUtf8(string text, int maxBytes)
    {
        // Every character is at least one byte, so no more than maxBytes of
        // them can survive; cutting the string first keeps a huge host
        // clipboard from being encoded whole on every GETSELLENGTH.
        if (text.Length > maxBytes)
        {
            int keep = maxBytes;
            if (char.IsHighSurrogate(text[keep - 1]))
            {
                keep--;
            }

            text = text[..keep];
        }

        byte[] fullBytes = Encoding.UTF8.GetBytes(text);
        if (fullBytes.Length <= maxBytes) return fullBytes;

        int cutLength = maxBytes;
        while (cutLength > 0 && (fullBytes[cutLength] & 0xC0) == 0x80)
        {
            cutLength--;
        }

        return fullBytes[..cutLength];
    }

    /// <summary>Drops any clipboard transfer in progress, in either direction.</summary>
    private void ResetClipboardTransfers()
    {
        _hostSelectionSnapshot = Array.Empty<byte>();
        _hostSelectionReadOffset = 0;
        _guestSelectionPending = false;
        _guestSelectionDeclaredLength = 0;
        _guestSelectionBuffer.Clear();
    }
}
