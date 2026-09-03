using Avalonia.Input;

namespace Aurora386.Gui.Emulation;

/// <summary>
/// Translates Avalonia key events into IBM PC scancodes (set 1), the codes
/// the keyboard controller hands to software.
///
/// The mapping is by <b>physical position</b>, not by the character the key
/// produces. That is how a real PC works: the keyboard reports "the key in
/// the third position of the top letter row was pressed", and it is the BIOS
/// and the loaded keyboard layout that decide whether that means Q, A or
/// something else. Mapping by character instead would break every non-US
/// layout in a way that is hard to diagnose.
///
/// Keys that appeared later than the PC/AT keyboard (arrows, Insert, the
/// numeric keypad's Enter) are "extended": the controller sends a 0xE0
/// prefix byte, then the base scancode. Software that predates those keys
/// simply ignores the prefixed pair.
/// </summary>
public static class AvaloniaKeyMap
{
    /// <summary>
    /// Scancode for the key, or 0 when the key has no PC equivalent.
    /// <paramref name="extended"/> is set for keys that need the 0xE0 prefix.
    /// </summary>
    public static byte ToScanCode(Key key, out bool extended)
    {
        extended = false;

        switch (key)
        {
            case Key.Escape: return 0x01;

            // --- number row -------------------------------------------------
            case Key.D1: return 0x02;
            case Key.D2: return 0x03;
            case Key.D3: return 0x04;
            case Key.D4: return 0x05;
            case Key.D5: return 0x06;
            case Key.D6: return 0x07;
            case Key.D7: return 0x08;
            case Key.D8: return 0x09;
            case Key.D9: return 0x0A;
            case Key.D0: return 0x0B;
            case Key.OemMinus: return 0x0C;
            case Key.OemPlus: return 0x0D;
            case Key.Back: return 0x0E;
            case Key.Tab: return 0x0F;

            // --- top letter row ---------------------------------------------
            case Key.Q: return 0x10;
            case Key.W: return 0x11;
            case Key.E: return 0x12;
            case Key.R: return 0x13;
            case Key.T: return 0x14;
            case Key.Y: return 0x15;
            case Key.U: return 0x16;
            case Key.I: return 0x17;
            case Key.O: return 0x18;
            case Key.P: return 0x19;
            case Key.OemOpenBrackets: return 0x1A;
            case Key.OemCloseBrackets: return 0x1B;
            case Key.Enter: return 0x1C;
            case Key.LeftCtrl: return 0x1D;

            // --- home row ---------------------------------------------------
            case Key.A: return 0x1E;
            case Key.S: return 0x1F;
            case Key.D: return 0x20;
            case Key.F: return 0x21;
            case Key.G: return 0x22;
            case Key.H: return 0x23;
            case Key.J: return 0x24;
            case Key.K: return 0x25;
            case Key.L: return 0x26;
            case Key.OemSemicolon: return 0x27;
            case Key.OemQuotes: return 0x28;
            case Key.OemTilde: return 0x29;
            case Key.LeftShift: return 0x2A;
            case Key.OemBackslash or Key.OemPipe: return 0x2B;

            // --- bottom letter row ------------------------------------------
            case Key.Z: return 0x2C;
            case Key.X: return 0x2D;
            case Key.C: return 0x2E;
            case Key.V: return 0x2F;
            case Key.B: return 0x30;
            case Key.N: return 0x31;
            case Key.M: return 0x32;
            case Key.OemComma: return 0x33;
            case Key.OemPeriod: return 0x34;
            case Key.OemQuestion: return 0x35;
            case Key.RightShift: return 0x36;
            case Key.Multiply: return 0x37;
            case Key.LeftAlt: return 0x38;

            // The Command keys stand in for Alt as well.
            //
            // On a Mac, Option is a character composer: the system swallows
            // many Option combinations to produce accented letters, so
            // Alt+F -- the way every DOS program opens its File menu --
            // frequently never reaches us at all. Command is passed through
            // far more readily, and it sits where a PC keyboard has Alt, so
            // it is both the reliable choice and the one under the thumb.
            case Key.LWin: return 0x38;
            case Key.Space: return 0x39;
            case Key.CapsLock: return 0x3A;

            // --- function keys ----------------------------------------------
            case Key.F1: return 0x3B;
            case Key.F2: return 0x3C;
            case Key.F3: return 0x3D;
            case Key.F4: return 0x3E;
            case Key.F5: return 0x3F;
            case Key.F6: return 0x40;
            case Key.F7: return 0x41;
            case Key.F8: return 0x42;
            case Key.F9: return 0x43;
            case Key.F10: return 0x44;
            case Key.NumLock: return 0x45;
            case Key.Scroll: return 0x46;
            case Key.F11: return 0x57;
            case Key.F12: return 0x58;

            // --- numeric keypad ---------------------------------------------
            // The keypad predates the separate cursor block, so these are the
            // unprefixed codes; the dedicated arrow keys below reuse them with
            // the 0xE0 prefix.
            case Key.NumPad7: return 0x47;
            case Key.NumPad8: return 0x48;
            case Key.NumPad9: return 0x49;
            case Key.Subtract: return 0x4A;
            case Key.NumPad4: return 0x4B;
            case Key.NumPad5: return 0x4C;
            case Key.NumPad6: return 0x4D;
            case Key.Add: return 0x4E;
            case Key.NumPad1: return 0x4F;
            case Key.NumPad2: return 0x50;
            case Key.NumPad3: return 0x51;
            case Key.NumPad0: return 0x52;
            case Key.Decimal: return 0x53;

            // --- extended keys ----------------------------------------------
            // These arrived with the 101-key keyboard and are distinguished
            // from their keypad equivalents by the 0xE0 prefix.
            case Key.Up: extended = true; return 0x48;
            case Key.Left: extended = true; return 0x4B;
            case Key.Right: extended = true; return 0x4D;
            case Key.Down: extended = true; return 0x50;
            case Key.Home: extended = true; return 0x47;
            case Key.End: extended = true; return 0x4F;
            case Key.PageUp: extended = true; return 0x49;
            case Key.PageDown: extended = true; return 0x51;
            case Key.Insert: extended = true; return 0x52;
            case Key.Delete: extended = true; return 0x53;
            case Key.RightCtrl: extended = true; return 0x1D;
            case Key.RightAlt: extended = true; return 0x38;
            case Key.RWin: extended = true; return 0x38;
            case Key.Divide: extended = true; return 0x35;

            default: return 0;
        }
    }
}
