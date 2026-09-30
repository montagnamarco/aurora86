namespace Aurora86.Core.Devices.Video;

/// <summary>
/// The VGA's character generator fonts, in the three cell heights the adapter
/// carries in ROM.
///
/// A real VGA holds three bitmap fonts and the BIOS points the character
/// generator at whichever one the mode wants:
///
/// <list type="bullet">
/// <item><b>8x16</b> -- the VGA's own font, used by the ordinary 25-row
/// screen. 80x25 cells of 8x16 pixels comes to 640x400.</item>
/// <item><b>8x14</b> -- inherited from the EGA, used by the 350-line graphics
/// modes (0Fh, 10h) and by the 28-row text layout.</item>
/// <item><b>8x8</b> -- inherited from the CGA, used by the 43- and 50-row text
/// modes and by the 200-line graphics modes.</item>
/// </list>
///
/// The important thing to understand is that switching to 50-row text does
/// <i>not</i> make the screen taller. The raster stays at 400 scanlines; the
/// BIOS simply loads the 8-line font and tells the CRTC that a character cell
/// is now 8 scanlines instead of 16, so twice as many rows fit in the same
/// picture and every character comes out half as tall. This is also why 43-row
/// mode on an EGA (350 lines / 8) and 50-row mode on a VGA (400 lines / 8)
/// differ: same font, different raster.
///
/// <see cref="VgaFont"/> owns the glyph data itself, in its native 8x8 shape
/// doubled up to 8x16. This class derives the other two heights from it so
/// there is exactly one place where a glyph's pixels are defined.
/// </summary>
public static class VgaFonts
{
    private static readonly Lazy<byte[]> Font8 = new(() => Build(8));
    private static readonly Lazy<byte[]> Font14 = new(() => Build(14));

    /// <summary>Cell heights we can supply a font for.</summary>
    public static bool HasFont(int height) => height is 8 or 14 or 16;

    /// <summary>
    /// One glyph's scanlines, top to bottom, at the requested cell height.
    /// Any unsupported height falls back to 8x16, which is never wrong-looking,
    /// only wrong-sized.
    /// </summary>
    public static ReadOnlySpan<byte> GetGlyph(byte character, int height) => height switch
    {
        8 => Font8.Value.AsSpan(character * 8, 8),
        14 => Font14.Value.AsSpan(character * 14, 14),
        _ => VgaFont.GetGlyph(character),
    };

    private static byte[] Build(int height)
    {
        var font = new byte[256 * height];

        for (int ch = 0; ch < 256; ch++)
        {
            // VgaFont stores each glyph as eight source rows doubled to
            // sixteen, so every even row of the 8x16 font is one original 8x8
            // scanline. Reading them back out recovers the native 8x8 shape.
            ReadOnlySpan<byte> tall = VgaFont.GetGlyph((byte)ch);

            for (int row = 0; row < height; row++)
            {
                // Proportional sampling rather than dropping or doubling a
                // fixed subset of rows: at height 8 this picks every other row
                // of the doubled font, recovering the original exactly, and at
                // height 14 it spreads the eight source rows evenly instead of
                // squashing the top of every glyph.
                int source = height == 8
                    ? row * 2                        // exact inverse of the doubling
                    : row * VgaFont.CharHeight / height;

                font[ch * height + row] = tall[Math.Min(source, VgaFont.CharHeight - 1)];
            }
        }

        return font;
    }
}
