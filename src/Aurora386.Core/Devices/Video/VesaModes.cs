namespace Aurora386.Core.Devices.Video;

/// <summary>
/// The VESA BIOS Extension modes: the resolutions a VGA cannot reach.
///
/// The VGA's own modes stop at 640x480 in sixteen colours, and its memory is
/// four planes of 64 KB that the planar addressing cannot see past. Anything
/// larger needs both more memory and a different way of reaching it, and
/// every card maker solved that differently -- which is exactly the problem
/// VESA existed to fix.
///
/// Before VBE, a program wanting 800x600 had to know which card it was
/// running on and drive its particular registers. There were dozens, and a
/// game shipped with a menu of them: pick your card from the list and hope.
/// VBE replaced the list with a question -- INT 10h AX=4F00 asks the card
/// what it can do, and the answer is the same shape whoever made it.
///
/// Two ways of reaching the memory are described here, and the difference
/// matters more than the resolutions:
///
///   * Banking, from VBE 1.2: a 64 KB window at 0xA0000 shows one slice of
///     video memory at a time, and the program moves the window. It works on
///     a real-mode machine with a 1 MB address space, which is why it exists,
///     and it makes drawing anything that crosses a bank boundary tedious.
///
///   * A linear frame buffer, from VBE 2.0: the whole of video memory mapped
///     at one physical address above the ISA hole, addressed as one flat
///     array. It needs protected mode to reach, and it is what every
///     Windows display driver uses.
/// </summary>
public static class VesaModes
{
    /// <summary>
    /// One VBE mode. The colour depth is in bits per pixel, and the bytes per
    /// pixel that follow from it decide almost everything else.
    /// </summary>
    public readonly record struct VesaModeInfo(
        ushort Number, int Width, int Height, int BitsPerPixel)
    {
        /// <summary>
        /// Bytes per pixel. 15-bit colour occupies two bytes like 16-bit
        /// does -- the missing bit is simply unused, which is why the two are
        /// separate modes rather than one.
        /// </summary>
        public int BytesPerPixel => BitsPerPixel switch
        {
            8 => 1,
            15 or 16 => 2,
            24 => 3,
            _ => 4,
        };

        /// <summary>
        /// Whether the mode is planar: four bits per pixel spread across four
        /// planes, as a VGA does it, rather than whole bytes per pixel.
        ///
        /// The distinction is not the colour count but how the memory is
        /// reached, and it changes everything downstream -- the renderer, the
        /// window, and what a byte written by the processor means.
        /// </summary>
        public bool IsPlanar => BitsPerPixel == 4;

        public int BytesPerRow => IsPlanar ? Width / 8 : Width * BytesPerPixel;

        /// <summary>
        /// Video memory the mode needs, which decides whether it can be
        /// offered. A planar mode needs four times its bytes per row, one
        /// plane each -- which is still half of what the same resolution
        /// costs at 256 colours.
        /// </summary>
        public int MemoryNeeded => BytesPerRow * Height * (IsPlanar ? 4 : 1);
    }

    /// <summary>
    /// The modes this card offers, in the numbering VBE fixed so that a
    /// program need not ask what 0x101 means on this particular card.
    ///
    /// The numbers look arbitrary and are not: 0x100 upwards were assigned by
    /// VESA in the order the resolutions were standardised, which is why
    /// 640x400 comes before 640x480 and the 16-bit modes are scattered among
    /// the 8-bit ones rather than grouped.
    /// </summary>
    public static readonly VesaModeInfo[] Table =
    [
        new(0x100, 640, 400, 8),
        new(0x101, 640, 480, 8),
        new(0x103, 800, 600, 8),
        new(0x105, 1024, 768, 8),

        // The sixteen-colour mode, which is the VGA's own planar arrangement
        // at a larger size rather than anything new: four bits per pixel
        // spread across four planes, eight pixels to a byte in each. It looks
        // like a curiosity next to the 256-colour modes and it is not --
        // Windows 3.1's own Super VGA driver wants this one and nothing else,
        // because sixteen colours at 800x600 fits in the 256 KB a plain VGA
        // has while 256 colours does not.
        //
        // 1024x768 in sixteen colours (mode 0x104) is deliberately absent. A
        // plane is 64 KB and that mode needs 96 KB of one, so a real card
        // served it by banking within the planes -- a second layer of window
        // moving on top of the planar one, which nothing here does. Offering
        // it and then drawing the top two thirds of the screen would be worse
        // than not offering it.
        new(0x102, 800, 600, 4),

        new(0x110, 640, 480, 15),
        new(0x111, 640, 480, 16),
        new(0x112, 640, 480, 24),
        new(0x113, 800, 600, 15),
        new(0x114, 800, 600, 16),
        new(0x115, 800, 600, 24),
        new(0x116, 1024, 768, 15),
        new(0x117, 1024, 768, 16),
        new(0x118, 1024, 768, 24),
    ];

    /// <summary>
    /// The mode with this number, or null when the card does not offer it --
    /// either because it does not exist or because there is not enough video
    /// memory for it.
    ///
    /// Reporting a mode and then failing to set it is worse than not
    /// reporting it: a program that asked the card what it could do is
    /// entitled to believe the answer.
    /// </summary>
    public static VesaModeInfo? Get(ushort number, int videoMemory)
    {
        // Bits 14 and 15 are requests rather than part of the number: bit 14
        // asks for the linear frame buffer, bit 15 to preserve the memory
        // contents across the mode set.
        ushort plain = (ushort)(number & 0x01FF);

        foreach (VesaModeInfo mode in Table)
            if (mode.Number == plain && mode.MemoryNeeded <= videoMemory)
                return mode;

        return null;
    }

    /// <summary>Every mode this card can actually offer, given its memory.</summary>
    public static IEnumerable<VesaModeInfo> Available(int videoMemory)
    {
        foreach (VesaModeInfo mode in Table)
            if (mode.MemoryNeeded <= videoMemory) yield return mode;
    }
}
