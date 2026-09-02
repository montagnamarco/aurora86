using Silk.NET.SDL;

namespace Aurora386.Sdl.Frontend;

/// <summary>
/// Traduzione dai codici tasto di SDL agli scancode del set 1, quelli che il
/// controller di tastiera del PC consegna.
///
/// La corrispondenza e' per <b>posizione fisica</b>, non per carattere: e' il
/// BIOS emulato a decidere quale carattere produce un tasto, esattamente come
/// su una macchina reale. Cosi' anche le disposizioni non americane si
/// comportano in modo prevedibile.
/// </summary>
public static class KeyMap
{
    /// <summary>Scancode del tasto, o 0 se non ha corrispondenza sul PC.</summary>
    public static byte ToScanCode(Scancode code) => code switch
    {
        Scancode.ScancodeEscape => 0x01,

        Scancode.Scancode1 => 0x02,
        Scancode.Scancode2 => 0x03,
        Scancode.Scancode3 => 0x04,
        Scancode.Scancode4 => 0x05,
        Scancode.Scancode5 => 0x06,
        Scancode.Scancode6 => 0x07,
        Scancode.Scancode7 => 0x08,
        Scancode.Scancode8 => 0x09,
        Scancode.Scancode9 => 0x0A,
        Scancode.Scancode0 => 0x0B,
        Scancode.ScancodeMinus => 0x0C,
        Scancode.ScancodeEquals => 0x0D,
        Scancode.ScancodeBackspace => 0x0E,
        Scancode.ScancodeTab => 0x0F,

        Scancode.ScancodeQ => 0x10,
        Scancode.ScancodeW => 0x11,
        Scancode.ScancodeE => 0x12,
        Scancode.ScancodeR => 0x13,
        Scancode.ScancodeT => 0x14,
        Scancode.ScancodeY => 0x15,
        Scancode.ScancodeU => 0x16,
        Scancode.ScancodeI => 0x17,
        Scancode.ScancodeO => 0x18,
        Scancode.ScancodeP => 0x19,
        Scancode.ScancodeLeftbracket => 0x1A,
        Scancode.ScancodeRightbracket => 0x1B,
        Scancode.ScancodeReturn => 0x1C,
        Scancode.ScancodeLctrl => 0x1D,

        Scancode.ScancodeA => 0x1E,
        Scancode.ScancodeS => 0x1F,
        Scancode.ScancodeD => 0x20,
        Scancode.ScancodeF => 0x21,
        Scancode.ScancodeG => 0x22,
        Scancode.ScancodeH => 0x23,
        Scancode.ScancodeJ => 0x24,
        Scancode.ScancodeK => 0x25,
        Scancode.ScancodeL => 0x26,
        Scancode.ScancodeSemicolon => 0x27,
        Scancode.ScancodeApostrophe => 0x28,
        Scancode.ScancodeGrave => 0x29,
        Scancode.ScancodeLshift => 0x2A,
        Scancode.ScancodeBackslash => 0x2B,

        Scancode.ScancodeZ => 0x2C,
        Scancode.ScancodeX => 0x2D,
        Scancode.ScancodeC => 0x2E,
        Scancode.ScancodeV => 0x2F,
        Scancode.ScancodeB => 0x30,
        Scancode.ScancodeN => 0x31,
        Scancode.ScancodeM => 0x32,
        Scancode.ScancodeComma => 0x33,
        Scancode.ScancodePeriod => 0x34,
        Scancode.ScancodeSlash => 0x35,
        Scancode.ScancodeRshift => 0x36,

        Scancode.ScancodeKPMultiply => 0x37,
        Scancode.ScancodeLalt => 0x38,
        Scancode.ScancodeSpace => 0x39,
        Scancode.ScancodeCapslock => 0x3A,

        Scancode.ScancodeF1 => 0x3B,
        Scancode.ScancodeF2 => 0x3C,
        Scancode.ScancodeF3 => 0x3D,
        Scancode.ScancodeF4 => 0x3E,
        Scancode.ScancodeF5 => 0x3F,
        Scancode.ScancodeF6 => 0x40,
        Scancode.ScancodeF7 => 0x41,
        Scancode.ScancodeF8 => 0x42,
        Scancode.ScancodeF9 => 0x43,
        Scancode.ScancodeF10 => 0x44,

        Scancode.ScancodeNumlockclear => 0x45,
        Scancode.ScancodeScrolllock => 0x46,

        // Tastierino numerico.
        Scancode.ScancodeKP7 => 0x47,
        Scancode.ScancodeKP8 => 0x48,
        Scancode.ScancodeKP9 => 0x49,
        Scancode.ScancodeKPMinus => 0x4A,
        Scancode.ScancodeKP4 => 0x4B,
        Scancode.ScancodeKP5 => 0x4C,
        Scancode.ScancodeKP6 => 0x4D,
        Scancode.ScancodeKPPlus => 0x4E,
        Scancode.ScancodeKP1 => 0x4F,
        Scancode.ScancodeKP2 => 0x50,
        Scancode.ScancodeKP3 => 0x51,
        Scancode.ScancodeKP0 => 0x52,
        Scancode.ScancodeKPPeriod => 0x53,

        Scancode.ScancodeF11 => 0x57,
        Scancode.ScancodeF12 => 0x58,

        // I tasti cursore separati condividono lo scancode con il tastierino:
        // sulle tastiere estese vengono preceduti dal prefisso 0xE0.
        Scancode.ScancodeUp => 0x48,
        Scancode.ScancodeDown => 0x50,
        Scancode.ScancodeLeft => 0x4B,
        Scancode.ScancodeRight => 0x4D,
        Scancode.ScancodeHome => 0x47,
        Scancode.ScancodeEnd => 0x4F,
        Scancode.ScancodePageup => 0x49,
        Scancode.ScancodePagedown => 0x51,
        Scancode.ScancodeInsert => 0x52,
        Scancode.ScancodeDelete => 0x53,

        Scancode.ScancodeKPEnter => 0x1C,
        Scancode.ScancodeKPDivide => 0x35,
        Scancode.ScancodeRctrl => 0x1D,
        Scancode.ScancodeRalt => 0x38,

        _ => 0,
    };

    /// <summary>
    /// true per i tasti che sulle tastiere estese mandano il prefisso 0xE0
    /// prima dello scancode, per distinguerli dai gemelli sul tastierino.
    /// </summary>
    public static bool IsExtended(Scancode code) => code switch
    {
        Scancode.ScancodeUp or Scancode.ScancodeDown
            or Scancode.ScancodeLeft or Scancode.ScancodeRight
            or Scancode.ScancodeHome or Scancode.ScancodeEnd
            or Scancode.ScancodePageup or Scancode.ScancodePagedown
            or Scancode.ScancodeInsert or Scancode.ScancodeDelete
            or Scancode.ScancodeKPEnter or Scancode.ScancodeKPDivide
            or Scancode.ScancodeRctrl or Scancode.ScancodeRalt => true,
        _ => false,
    };
}
