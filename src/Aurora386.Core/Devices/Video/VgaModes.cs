namespace Aurora386.Core.Devices.Video;

/// <summary>
/// How video memory is organised in a given mode.
///
/// The VGA is really three different machines wearing one nameplate, and the
/// memory organisation is what separates them:
///
/// <list type="bullet">
/// <item><b>Text</b> -- memory holds character/attribute pairs and the
/// hardware expands them through a font ROM. Two bytes describe a whole 8x16
/// cell, which is why an 80x25 screen fits in 4000 bytes.</item>
///
/// <item><b>Planar</b> -- the 16-colour EGA-compatible modes. Each pixel needs
/// four bits, but the CPU bus is eight bits wide, so IBM did not pack two
/// pixels per byte. Instead the four bits of a pixel live in four *separate*
/// 64 KB memory chips ("planes"), all addressed by the same CPU address. One
/// CPU byte therefore addresses eight consecutive pixels in one plane, and a
/// single CPU write can be made to update all four planes at once. The payoff
/// is bandwidth: 640x480 at 16 colours is 150 KB of pixels, and the CPU only
/// has to move 37.5 KB of them per plane -- and, in the common case of solid
/// colour fills, one write updates eight pixels across all four planes.</item>
///
/// <item><b>Linear ("chained", mode 13h)</b> -- 256 colours, one byte per
/// pixel. The four planes still exist physically; the hardware just chains
/// them so consecutive CPU addresses walk plane 0, 1, 2, 3, 0, 1... and the
/// programmer never sees them. Simplicity costs resolution: 320x200 is all
/// that fits in the 64 KB window this way.</item>
/// </list>
/// </summary>
public enum VgaMemoryLayout
{
    Text,
    Planar4,
    Linear8,
}

/// <summary>
/// The geometry and memory organisation of one BIOS video mode.
///
/// Real hardware derives all of this from the CRTC, sequencer and graphics
/// controller registers, which the BIOS loads from a table when INT 10h
/// AH=00 runs. We keep the same table, use it to program plausible register
/// values, and also consult it directly -- that keeps the renderer honest
/// about which mode it is drawing without having to reverse-engineer the
/// geometry back out of a dozen registers on every frame.
/// </summary>
public readonly record struct VgaModeInfo(
    byte Mode,
    VgaMemoryLayout Layout,
    int PixelWidth,
    int PixelHeight,
    int TextColumns,
    int TextRows,
    int CharHeight)
{
    /// <summary>Bytes per scan line: one bit per pixel per plane in planar modes.</summary>
    public int BytesPerRow => Layout switch
    {
        VgaMemoryLayout.Planar4 => PixelWidth / 8,
        VgaMemoryLayout.Linear8 => PixelWidth,
        _ => TextColumns * 2,
    };

    public bool IsGraphics => Layout != VgaMemoryLayout.Text;
}

/// <summary>The BIOS mode table: everything INT 10h AH=00 needs to know.</summary>
public static class VgaModes
{
    /// <summary>
    /// Modes we implement. Widths and heights are the real VGA ones.
    ///
    /// Note 0Dh and 0Eh are 200 lines: on a real VGA those scanlines are
    /// doubled to fill a 400-line raster, so pixels come out twice as tall as
    /// they are wide. We render the true 320x200 / 640x200 pixel grid and let
    /// the frontend handle aspect ratio, exactly as mode 13h already does.
    /// </summary>
    private static readonly VgaModeInfo[] Table =
    [
        // Text modes. The 40-column ones are the CGA legacy: the same 8x16
        // cell, half as many of them across.
        new(0x00, VgaMemoryLayout.Text, 320, 400, 40, 25, 16),
        new(0x01, VgaMemoryLayout.Text, 320, 400, 40, 25, 16),
        new(0x02, VgaMemoryLayout.Text, 640, 400, 80, 25, 16),
        new(0x03, VgaMemoryLayout.Text, 640, 400, 80, 25, 16),
        new(0x07, VgaMemoryLayout.Text, 640, 400, 80, 25, 16),   // MDA monochrome

        // Planar 16-colour graphics: the EGA family the VGA inherited.
        new(0x0D, VgaMemoryLayout.Planar4, 320, 200, 40, 25, 8),
        new(0x0E, VgaMemoryLayout.Planar4, 640, 200, 80, 25, 8),
        new(0x0F, VgaMemoryLayout.Planar4, 640, 350, 80, 25, 14),  // monochrome 640x350
        new(0x10, VgaMemoryLayout.Planar4, 640, 350, 80, 25, 14),
        new(0x11, VgaMemoryLayout.Planar4, 640, 480, 80, 30, 16),  // 2 colours, but planar
        new(0x12, VgaMemoryLayout.Planar4, 640, 480, 80, 30, 16),

        // The one linear mode.
        new(0x13, VgaMemoryLayout.Linear8, 320, 200, 40, 25, 8),
    ];

    /// <summary>The default when a program asks for a mode we do not know.</summary>
    public static VgaModeInfo Text80x25 => Table[3];

    public static bool TryGet(byte mode, out VgaModeInfo info)
    {
        foreach (VgaModeInfo candidate in Table)
        {
            if (candidate.Mode == mode) { info = candidate; return true; }
        }
        info = Text80x25;
        return false;
    }

    /// <summary>Mode information, falling back to 80x25 text for unknown modes.</summary>
    public static VgaModeInfo Get(byte mode)
    {
        TryGet(mode, out VgaModeInfo info);
        return info;
    }
}
