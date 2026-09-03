using Avalonia.Input;

namespace Aurora386.Gui.Emulation;

/// <summary>
/// Which key of the guest's keyboard produces a given character.
///
/// This is the reverse of <see cref="PhysicalKeyMap"/>, and it exists because
/// the two keyboards need not agree. Scancodes name key positions, and the
/// guest decides what each position means using the layout it has loaded --
/// which is the American one unless something changed it. The host, meanwhile,
/// knows the user's real layout and reports the character that was actually
/// typed.
///
/// When the two disagree, the character is what the user meant. On an Italian
/// keyboard the colon shares a key with the full stop; the guest reads that
/// position with shift as a greater-than sign. Sending the position would type
/// the wrong thing, however faithfully it was mapped, so instead we look up
/// which position would produce a colon on the guest's own layout and send
/// that.
/// </summary>
public static class UsKeyboardLayout
{
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
    public static (byte Code, bool Shift) KeyForCharacter(char character) => character switch
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
}
