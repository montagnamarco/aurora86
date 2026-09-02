namespace Aurora386.Core.Bios;

/// <summary>
/// Translation of set 1 scancodes -- the set the keyboard controller delivers
/// by default -- into the characters they produce on a US keyboard.
///
/// Scancodes bear no relation to ASCII codes: they identify a physical key
/// position on the keyboard. Turning them into characters, taking the state of
/// the shift keys into account, is the BIOS's job.
/// </summary>
public static class ScanCodeTable
{
    /// <summary>Characters produced without shift, indexed by scancode.</summary>
    private static readonly char[] Unshifted = BuildUnshifted();

    /// <summary>Characters produced with shift held.</summary>
    private static readonly char[] Shifted = BuildShifted();

    public static char ToChar(byte scanCode, bool shift)
    {
        char[] table = shift ? Shifted : Unshifted;
        return scanCode < table.Length ? table[scanCode] : '\0';
    }

    private static char[] BuildUnshifted()
    {
        var table = new char[128];

        // Top row: escape, digits, symbols, backspace, tab.
        Assign(table, 0x01, '\x1B');                       // ESC
        AssignRange(table, 0x02, "1234567890-=");
        Assign(table, 0x0E, '\b');
        Assign(table, 0x0F, '\t');

        // Letter rows, in physical QWERTY order.
        AssignRange(table, 0x10, "qwertyuiop[]");
        Assign(table, 0x1C, '\r');                          // enter
        AssignRange(table, 0x1E, "asdfghjkl;'`");
        Assign(table, 0x2B, '\\');
        AssignRange(table, 0x2C, "zxcvbnm,./");

        Assign(table, 0x37, '*');                           // keypad asterisk
        Assign(table, 0x39, ' ');                           // space bar

        // Numeric keypad with num lock on.
        Assign(table, 0x4A, '-');
        Assign(table, 0x4E, '+');

        return table;
    }

    private static char[] BuildShifted()
    {
        var table = new char[128];

        Assign(table, 0x01, '\x1B');
        AssignRange(table, 0x02, "!@#$%^&*()_+");
        Assign(table, 0x0E, '\b');
        Assign(table, 0x0F, '\t');

        AssignRange(table, 0x10, "QWERTYUIOP{}");
        Assign(table, 0x1C, '\r');
        AssignRange(table, 0x1E, "ASDFGHJKL:\"~");
        Assign(table, 0x2B, '|');
        AssignRange(table, 0x2C, "ZXCVBNM<>?");

        Assign(table, 0x37, '*');
        Assign(table, 0x39, ' ');
        Assign(table, 0x4A, '-');
        Assign(table, 0x4E, '+');

        return table;
    }

    private static void Assign(char[] table, int scanCode, char character)
        => table[scanCode] = character;

    private static void AssignRange(char[] table, int firstScanCode, string characters)
    {
        for (int i = 0; i < characters.Length; i++)
            table[firstScanCode + i] = characters[i];
    }

    // --- scancodes of the keys that produce no character -------------------------
    // Programs recognize these from the high byte of the pair INT 16h returns.

    public const byte Escape = 0x01;
    public const byte Backspace = 0x0E;
    public const byte Tab = 0x0F;
    public const byte Enter = 0x1C;
    public const byte LeftControl = 0x1D;
    public const byte LeftShift = 0x2A;
    public const byte RightShift = 0x36;
    public const byte LeftAlt = 0x38;
    public const byte Space = 0x39;
    public const byte CapsLock = 0x3A;
    public const byte F1 = 0x3B;
    public const byte F10 = 0x44;
    public const byte NumLock = 0x45;
    public const byte ScrollLock = 0x46;
    public const byte Home = 0x47;
    public const byte Up = 0x48;
    public const byte PageUp = 0x49;
    public const byte Left = 0x4B;
    public const byte Right = 0x4D;
    public const byte End = 0x4F;
    public const byte Down = 0x50;
    public const byte PageDown = 0x51;
    public const byte Insert = 0x52;
    public const byte Delete = 0x53;
}
