using Avalonia.Input;

namespace Aurora386.Gui.Emulation;

/// <summary>
/// Turns the physical position of a key into the scancode a PC keyboard sends
/// for that position.
///
/// This maps <see cref="PhysicalKey"/> rather than <see cref="Key"/>, and the
/// distinction is the whole point. <c>Key</c> is what the host thinks the key
/// means, arrived at through the user's own keyboard layout: on an Italian
/// Mac the key beside L reports as something quite different from the
/// semicolon an American keyboard has there. <c>PhysicalKey</c> is where the
/// key physically sits, which never changes.
///
/// A scancode names a position, so a position is what has to be mapped. Going
/// through <c>Key</c> means the host's layout has already been applied once,
/// and the guest -- which has a layout of its own, loaded by KEYB -- then
/// applies a second one. Two translations where there should be none is how
/// the backslash came out as an accented U.
///
/// The names here are the physical ones, so <c>Semicolon</c> means "the key
/// where an American keyboard has a semicolon", whatever is printed on it.
/// </summary>
public static class PhysicalKeyMap
{
    /// <summary>
    /// Scancode for the position, or 0 when a PC keyboard has no key there.
    /// <paramref name="extended"/> is set for the keys that arrived after the
    /// original PC keyboard and are announced by a 0xE0 prefix.
    /// </summary>
    public static byte ToScanCode(PhysicalKey key, out bool extended)
    {
        extended = false;

        switch (key)
        {
            case PhysicalKey.Escape: return 0x01;

            // --- number row -------------------------------------------------
            case PhysicalKey.Digit1: return 0x02;
            case PhysicalKey.Digit2: return 0x03;
            case PhysicalKey.Digit3: return 0x04;
            case PhysicalKey.Digit4: return 0x05;
            case PhysicalKey.Digit5: return 0x06;
            case PhysicalKey.Digit6: return 0x07;
            case PhysicalKey.Digit7: return 0x08;
            case PhysicalKey.Digit8: return 0x09;
            case PhysicalKey.Digit9: return 0x0A;
            case PhysicalKey.Digit0: return 0x0B;
            case PhysicalKey.Minus: return 0x0C;
            case PhysicalKey.Equal: return 0x0D;
            case PhysicalKey.Backspace: return 0x0E;

            // --- top letter row ---------------------------------------------
            case PhysicalKey.Tab: return 0x0F;
            case PhysicalKey.Q: return 0x10;
            case PhysicalKey.W: return 0x11;
            case PhysicalKey.E: return 0x12;
            case PhysicalKey.R: return 0x13;
            case PhysicalKey.T: return 0x14;
            case PhysicalKey.Y: return 0x15;
            case PhysicalKey.U: return 0x16;
            case PhysicalKey.I: return 0x17;
            case PhysicalKey.O: return 0x18;
            case PhysicalKey.P: return 0x19;
            case PhysicalKey.BracketLeft: return 0x1A;
            case PhysicalKey.BracketRight: return 0x1B;
            case PhysicalKey.Enter: return 0x1C;

            // --- home row ---------------------------------------------------
            case PhysicalKey.ControlLeft: return 0x1D;
            case PhysicalKey.A: return 0x1E;
            case PhysicalKey.S: return 0x1F;
            case PhysicalKey.D: return 0x20;
            case PhysicalKey.F: return 0x21;
            case PhysicalKey.G: return 0x22;
            case PhysicalKey.H: return 0x23;
            case PhysicalKey.J: return 0x24;
            case PhysicalKey.K: return 0x25;
            case PhysicalKey.L: return 0x26;
            case PhysicalKey.Semicolon: return 0x27;
            case PhysicalKey.Quote: return 0x28;
            case PhysicalKey.Backquote: return 0x29;

            // --- bottom letter row ------------------------------------------
            case PhysicalKey.ShiftLeft: return 0x2A;
            case PhysicalKey.Backslash: return 0x2B;
            case PhysicalKey.Z: return 0x2C;
            case PhysicalKey.X: return 0x2D;
            case PhysicalKey.C: return 0x2E;
            case PhysicalKey.V: return 0x2F;
            case PhysicalKey.B: return 0x30;
            case PhysicalKey.N: return 0x31;
            case PhysicalKey.M: return 0x32;
            case PhysicalKey.Comma: return 0x33;
            case PhysicalKey.Period: return 0x34;
            case PhysicalKey.Slash: return 0x35;
            case PhysicalKey.ShiftRight: return 0x36;

            // The extra key European keyboards have beside the left shift,
            // which an American keyboard does not: on an Italian one it
            // carries the angle brackets. A PC keyboard calls it 0x56.
            case PhysicalKey.IntlBackslash: return 0x56;

            // --- bottom edge and the function keys --------------------------
            case PhysicalKey.NumPadMultiply: return 0x37;
            case PhysicalKey.AltLeft: return 0x38;
            case PhysicalKey.Space: return 0x39;
            case PhysicalKey.CapsLock: return 0x3A;

            case PhysicalKey.F1: return 0x3B;
            case PhysicalKey.F2: return 0x3C;
            case PhysicalKey.F3: return 0x3D;
            case PhysicalKey.F4: return 0x3E;
            case PhysicalKey.F5: return 0x3F;
            case PhysicalKey.F6: return 0x40;
            case PhysicalKey.F7: return 0x41;
            case PhysicalKey.F8: return 0x42;
            case PhysicalKey.F9: return 0x43;
            case PhysicalKey.F10: return 0x44;
            case PhysicalKey.F11: return 0x57;
            case PhysicalKey.F12: return 0x58;

            case PhysicalKey.NumLock: return 0x45;
            case PhysicalKey.ScrollLock: return 0x46;

            // --- numeric keypad ---------------------------------------------
            case PhysicalKey.NumPad7: return 0x47;
            case PhysicalKey.NumPad8: return 0x48;
            case PhysicalKey.NumPad9: return 0x49;
            case PhysicalKey.NumPadSubtract: return 0x4A;
            case PhysicalKey.NumPad4: return 0x4B;
            case PhysicalKey.NumPad5: return 0x4C;
            case PhysicalKey.NumPad6: return 0x4D;
            case PhysicalKey.NumPadAdd: return 0x4E;
            case PhysicalKey.NumPad1: return 0x4F;
            case PhysicalKey.NumPad2: return 0x50;
            case PhysicalKey.NumPad3: return 0x51;
            case PhysicalKey.NumPad0: return 0x52;
            case PhysicalKey.NumPadDecimal: return 0x53;

            // --- the keys that arrived later, behind a 0xE0 prefix -----------
            //
            // These share their scancodes with the keypad above, and the
            // prefix is the only thing telling them apart: software written
            // before they existed simply ignores the prefixed pair.
            case PhysicalKey.ArrowUp: extended = true; return 0x48;
            case PhysicalKey.ArrowLeft: extended = true; return 0x4B;
            case PhysicalKey.ArrowRight: extended = true; return 0x4D;
            case PhysicalKey.ArrowDown: extended = true; return 0x50;
            case PhysicalKey.Home: extended = true; return 0x47;
            case PhysicalKey.End: extended = true; return 0x4F;
            case PhysicalKey.PageUp: extended = true; return 0x49;
            case PhysicalKey.PageDown: extended = true; return 0x51;
            case PhysicalKey.Insert: extended = true; return 0x52;
            case PhysicalKey.Delete: extended = true; return 0x53;
            case PhysicalKey.NumPadEnter: extended = true; return 0x1C;
            case PhysicalKey.NumPadDivide: extended = true; return 0x35;
            case PhysicalKey.ControlRight: extended = true; return 0x1D;
            case PhysicalKey.AltRight: extended = true; return 0x38;

            // The Command keys stand in for Alt as well. macOS keeps many
            // Option combinations for composing accented letters, so Alt+F --
            // the way every DOS program opens its File menu -- often never
            // arrives; Command is passed through, and sits where a PC
            // keyboard has Alt.
            case PhysicalKey.MetaLeft: return 0x38;
            case PhysicalKey.MetaRight: extended = true; return 0x38;

            default: return 0;
        }
    }
}
