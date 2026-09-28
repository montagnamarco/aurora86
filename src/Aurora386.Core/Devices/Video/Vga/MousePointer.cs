namespace Aurora386.Core.Devices.Video;

/// <summary>
/// What the mouse driver wants drawn on top of the screen, and where.
///
/// A DOS mouse pointer is not hardware. The VGA has no sprite engine, so the
/// driver paints the pointer itself, straight into video memory, and repaints
/// whatever was underneath when it moves. In text mode it does not even draw:
/// it rewrites the attribute byte of one character cell, which is why the DOS
/// pointer is a block that swaps a cell's colours. In graphics modes it
/// composes a 16x16 shape out of two masks, one that punches a hole in the
/// screen (AND) and one that fills it (XOR), which is how a pointer manages
/// to be visible on both black and white without knowing the colours.
///
/// We do not modify video memory the way the driver did -- programs read the
/// screen back and would find the pointer in their text -- so the renderer
/// composes the pointer over the finished frame instead, from the state kept
/// here. The result on the monitor is the same; the memory stays clean.
/// </summary>
public sealed class MousePointer
{
    /// <summary>Whether the driver's show/hide counter says the pointer is shown.</summary>
    public bool Visible { get; set; }

    /// <summary>
    /// Position in the driver's virtual coordinates: pixels in graphics modes,
    /// eight per character cell in text mode.
    /// </summary>
    public int X { get; set; }
    public int Y { get; set; }

    /// <summary>
    /// The text-mode masks, one word each, character in the low byte and
    /// attribute in the high byte. The cell is first ANDed with the screen
    /// mask and then XORed with the cursor mask. The Microsoft defaults keep
    /// the character and invert the colours, which is the pointer everybody
    /// remembers.
    /// </summary>
    public ushort TextScreenMask { get; set; } = 0x77FF;
    public ushort TextCursorMask { get; set; } = 0x7700;

    /// <summary>
    /// Function 0Ah type 1 asks for the *hardware* text cursor to be used as
    /// the pointer instead, which moves the blinking underline around. Rare,
    /// and we honour it by drawing nothing extra.
    /// </summary>
    public bool UsesHardwareTextCursor { get; set; }

    /// <summary>The pixel within the 16x16 shape that sits on the reported position.</summary>
    public int HotSpotX { get; set; }
    public int HotSpotY { get; set; }

    /// <summary>Screen (AND) mask: a clear bit blanks the pixel underneath.</summary>
    public ushort[] AndMask { get; } = new ushort[16];

    /// <summary>Cursor (XOR) mask: a set bit inverts the pixel underneath.</summary>
    public ushort[] XorMask { get; } = new ushort[16];

    public MousePointer() => LoadDefaultArrow();

    /// <summary>
    /// The default arrow: a white body with a black outline, hot spot at the
    /// tip. AND clears the pixels of body and outline; XOR then sets the body
    /// bits, so body reads white (cleared then inverted) and outline black
    /// (cleared, left alone).
    /// </summary>
    public void LoadDefaultArrow()
    {
        ushort[] and =
        [
            0x3FFF, 0x1FFF, 0x0FFF, 0x07FF, 0x03FF, 0x01FF, 0x00FF, 0x007F,
            0x003F, 0x001F, 0x01FF, 0x10FF, 0x30FF, 0xF87F, 0xF87F, 0xFC3F,
        ];
        ushort[] xor =
        [
            0x0000, 0x4000, 0x6000, 0x7000, 0x7800, 0x7C00, 0x7E00, 0x7F00,
            0x7F80, 0x7C00, 0x6C00, 0x4600, 0x0600, 0x0300, 0x0300, 0x0000,
        ];
        and.CopyTo(AndMask, 0);
        xor.CopyTo(XorMask, 0);
        HotSpotX = 0;
        HotSpotY = 0;
    }
}
