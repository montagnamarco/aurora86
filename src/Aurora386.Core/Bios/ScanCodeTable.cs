namespace Aurora386.Core.Bios;

/// <summary>
/// Traduzione degli scancode del set 1 (quello che il controller consegna per
/// default) nei caratteri corrispondenti, per una tastiera US.
///
/// Gli scancode non hanno relazione con i codici ASCII: sono numeri di
/// posizione fisica sulla tastiera. E' il BIOS a fare la traduzione tenendo
/// conto dello stato di shift.
/// </summary>
public static class ScanCodeTable
{
    /// <summary>Caratteri prodotti senza shift, indicizzati per scancode.</summary>
    private static readonly char[] Unshifted = BuildUnshifted();

    /// <summary>Caratteri prodotti con shift premuto.</summary>
    private static readonly char[] Shifted = BuildShifted();

    public static char ToChar(byte scanCode, bool shift)
    {
        char[] table = shift ? Shifted : Unshifted;
        return scanCode < table.Length ? table[scanCode] : '\0';
    }

    private static char[] BuildUnshifted()
    {
        var table = new char[128];

        // Prima riga: escape, cifre, simboli, backspace, tab.
        Assign(table, 0x01, '\x1B');                       // ESC
        AssignRange(table, 0x02, "1234567890-=");
        Assign(table, 0x0E, '\b');
        Assign(table, 0x0F, '\t');

        // Righe alfabetiche, nella disposizione fisica QWERTY.
        AssignRange(table, 0x10, "qwertyuiop[]");
        Assign(table, 0x1C, '\r');                          // invio
        AssignRange(table, 0x1E, "asdfghjkl;'`");
        Assign(table, 0x2B, '\\');
        AssignRange(table, 0x2C, "zxcvbnm,./");

        Assign(table, 0x37, '*');                           // asterisco del tastierino
        Assign(table, 0x39, ' ');                           // barra spaziatrice

        // Tastierino numerico con bloc num attivo.
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

    // --- scancode dei tasti che non producono caratteri --------------------------
    // I programmi li riconoscono dal byte alto della coppia restituita da INT 16h.

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
