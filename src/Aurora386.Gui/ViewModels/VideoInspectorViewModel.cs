using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Aurora386.Core;
using Aurora386.Core.Devices.Video;
using Aurora386.Gui.Emulation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aurora386.Gui.ViewModels;

/// <summary>
/// A window onto the video card: its registers, and a picture of what the
/// guest has actually written into video memory.
///
/// The picture is the point. Counting non-zero bytes tells you something is
/// wrong; seeing the memory drawn tells you what. A driver that paints the
/// desktop in colour zero, one that writes to the wrong bank, one whose rows
/// are the wrong length -- each of those produces an unmistakable shape, and
/// none of them is obvious from a byte count.
///
/// It shows the memory as memory rather than as the screen: the whole two
/// megabytes at once, at whatever stride is asked for, so that off-screen
/// scratch, a second buffer, or a picture drawn at the wrong offset are all
/// visible. What the monitor would show is only the part of it the display
/// registers happen to point at.
/// </summary>
public sealed partial class VideoInspectorViewModel : ViewModelBase
{
    private readonly EmulatorSession _session;

    public VideoInspectorViewModel(EmulatorSession session)
    {
        _session = session;
        Refresh();
    }

    // --- what the card is doing -----------------------------------------------

    [ObservableProperty] private string _mode = "";
    [ObservableProperty] private string _geometry = "";
    [ObservableProperty] private string _memoryLayout = "";
    [ObservableProperty] private string _apertures = "";

    /// <summary>The register groups, each as a block of hex.</summary>
    public ObservableCollection<RegisterGroup> Registers { get; } = [];

    /// <summary>The palette, as 256 swatches.</summary>
    public ObservableCollection<PaletteEntry> Palette { get; } = [];

    public sealed record RegisterGroup(string Name, string Values, string Note);
    public sealed record PaletteEntry(int Index, string Colour, string Tooltip);

    // --- the picture ----------------------------------------------------------

    /// <summary>
    /// Which slice of video memory to draw, in bytes. The whole two megabytes
    /// at 800 bytes a row is 2621 rows, which is more than a window shows, so
    /// the view starts at the display's own origin and can be moved.
    /// </summary>
    [ObservableProperty] private uint _viewOffset;

    /// <summary>
    /// Bytes per row in the picture. Defaults to what the mode says, but it
    /// is settable on purpose: a picture drawn with the wrong stride shears
    /// diagonally, and being able to try another value is how you find the
    /// right one.
    /// </summary>
    [ObservableProperty] private int _viewStride = 800;

    /// <summary>How the bytes are turned into colours.</summary>
    [ObservableProperty] private VideoViewKind _viewKind = VideoViewKind.Palette256;

    [ObservableProperty] private string _viewDescription = "";

    /// <summary>The rendered picture, RGBA, as wide as the stride.</summary>
    public uint[] Picture { get; private set; } = [];
    public int PictureWidth { get; private set; }
    public int PictureHeight { get; private set; }

    /// <summary>Raised when the picture has been redrawn.</summary>
    public event Action? PictureChanged;

    /// <summary>
    /// How many screen pixels one byte of video memory occupies.
    ///
    /// Whole numbers only, and nearest-neighbour rather than smoothing: this
    /// window exists to show what individual bytes hold, and an interpolated
    /// magnification invents pixels that are not there. At four times, a
    /// single wrong byte in a field of thousands is visible; smoothed, it
    /// would be a faint blur indistinguishable from the ones around it.
    /// </summary>
    [ObservableProperty] private int _zoom = 1;

    /// <summary>Raised when the magnification changes but the picture has not.</summary>
    public event Action? ZoomChanged;

    partial void OnZoomChanged(int value) => ZoomChanged?.Invoke();

    [RelayCommand]
    private void ZoomIn() => Zoom = Math.Min(Zoom * 2, 16);

    [RelayCommand]
    private void ZoomOut() => Zoom = Math.Max(Zoom / 2, 1);

    [RelayCommand]
    private void ZoomReset() => Zoom = 1;

    /// <summary>The views, for the menu that chooses between them.</summary>
    public static VideoViewKind[] ViewKinds => Enum.GetValues<VideoViewKind>();

    public enum VideoViewKind
    {
        /// <summary>One byte per pixel through the card's own palette.</summary>
        Palette256,

        /// <summary>Four planes combined, as a sixteen-colour mode shows them.</summary>
        Planar16,

        /// <summary>Two bytes per pixel, five bits each and green given the spare.</summary>
        HighColour16,

        /// <summary>Every non-zero byte white: shows *where* something is written.</summary>
        Occupancy,

        /// <summary>The text window as characters, drawn with the ROM font.</summary>
        Text,
    }

    // --- reading the machine --------------------------------------------------

    [RelayCommand]
    public void Refresh()
    {
        _session.WithMachine(machine =>
        {
            ReadState(machine);
            ReadRegisters(machine);
            ReadPalette(machine);
            DrawPicture(machine);
        });

        PictureChanged?.Invoke();
    }

    private void ReadState(Machine machine)
    {
        VgaRegisters registers = machine.VideoRegisters;
        VgaModeInfo effective = registers.EffectiveMode;

        Mode = registers.VesaMode is { } vesa
            ? $"VBE {vesa.Number:X3} — {vesa.Width}×{vesa.Height} at {vesa.BitsPerPixel} bpp"
            : $"BIOS mode {registers.VideoMode:X2}h — {effective.Layout}";

        Geometry = $"{machine.Video.Width}×{machine.Video.Height}, "
                 + $"{effective.BytesPerRow} bytes per row, "
                 + $"cell {registers.CharacterWidth}×{registers.CharacterHeight}";

        // Where the picture is read from, which is not always where a driver
        // thinks it wrote: the commonest cause of a screen that is blank
        // while memory is full.
        uint origin = registers.VesaDisplayStart > 0
            ? (uint)registers.VesaDisplayStart
            : registers.ExtendedStartAddress * 4;

        MemoryLayout = $"origin {origin:X6}, "
                     + $"stride {(registers.VesaScanLineBytes > 0 ? registers.VesaScanLineBytes : effective.BytesPerRow)}, "
                     + $"bank {registers.VesaWindowBank}, "
                     + $"window {registers.MemoryWindow.Base:X5}+{registers.MemoryWindow.Size:X5}";

        Apertures = $"linear frame buffer at {machine.LinearFrameBufferBase:X8}, "
                  + $"registers at {machine.MmioBase:X8}";
    }

    private void ReadRegisters(Machine machine)
    {
        Registers.Clear();
        VgaRegisters r = machine.VideoRegisters;

        Registers.Add(new RegisterGroup("CRTC 00–18",
            Hex(i => r.Crtc(i), 0x00, 0x19),
            "geometry: totals, sync, start address, cursor"));

        Registers.Add(new RegisterGroup("CRTC 30–3F (S3)",
            Hex(i => r.Crtc(i), 0x30, 0x10),
            "identification and configuration; 38h unlocks"));

        Registers.Add(new RegisterGroup("CRTC 40–5F (S3)",
            Hex(i => r.Crtc(i), 0x40, 0x20),
            "hardware cursor, linear aperture; 39h unlocks"));

        Registers.Add(new RegisterGroup("Sequencer",
            Hex(i => r.Sequencer(i), 0x00, 0x14),
            "plane masks, clocking; 08h unlocks the extended ones"));

        Registers.Add(new RegisterGroup("Graphics controller",
            Hex(i => r.Graphics(i), 0x00, 0x10),
            "write modes, bit mask, memory map select"));

        Registers.Add(new RegisterGroup("Attribute controller",
            Hex(i => r.Attribute(i), 0x00, 0x15),
            "the sixteen colour registers, mode, panning"));
    }

    private static string Hex(Func<int, byte> read, int first, int count)
    {
        var text = new System.Text.StringBuilder();

        for (int row = 0; row < (count + 15) / 16; row++)
        {
            text.Append($"{first + row * 16:X2}: ");
            for (int i = 0; i < 16 && row * 16 + i < count; i++)
                text.Append($"{read(first + row * 16 + i):X2} ");
            text.AppendLine();
        }

        return text.ToString().TrimEnd();
    }

    private void ReadPalette(Machine machine)
    {
        Palette.Clear();

        for (int i = 0; i < 256; i++)
        {
            (byte red, byte green, byte blue) = machine.VideoRegisters.GetPaletteColor(i);
            Palette.Add(new PaletteEntry(
                i,
                $"#{red:X2}{green:X2}{blue:X2}",
                $"{i} ({i:X2}h) — {red}, {green}, {blue}"));
        }
    }

    /// <summary>
    /// Turns a slice of video memory into a picture, the way the chosen view
    /// says to.
    /// </summary>
    private void DrawPicture(Machine machine)
    {
        int stride = Math.Max(1, ViewStride);
        int rows = Math.Min(600, (int)((VgaMemory.TotalSize - ViewOffset) / (uint)stride));
        if (rows <= 0) { Picture = []; PictureWidth = PictureHeight = 0; return; }

        int pixelsPerRow = ViewKind switch
        {
            VideoViewKind.HighColour16 => stride / 2,
            VideoViewKind.Planar16 => stride * 8,
            VideoViewKind.Text => stride / 2,
            _ => stride,
        };

        pixelsPerRow = Math.Min(pixelsPerRow, 1600);

        Picture = new uint[pixelsPerRow * rows];
        PictureWidth = pixelsPerRow;
        PictureHeight = rows;

        for (int y = 0; y < rows; y++)
        {
            uint rowStart = ViewOffset + (uint)(y * stride);

            for (int x = 0; x < pixelsPerRow; x++)
                Picture[y * pixelsPerRow + x] = PixelAt(machine, rowStart, x);
        }

        ViewDescription = $"{pixelsPerRow}×{rows} from {ViewOffset:X6}, "
                        + $"{stride} bytes per row, {ViewKind}"
                        + (Zoom > 1 ? $", ×{Zoom}" : "");
    }

    private uint PixelAt(Machine machine, uint rowStart, int x)
    {
        VgaMemory memory = machine.VideoMemory;

        switch (ViewKind)
        {
            case VideoViewKind.Occupancy:
            {
                // The view that answers "is anything there at all", which is
                // the first question when a screen is blank: a driver that
                // wrote nothing and one that wrote zeros look identical on
                // screen and completely different here.
                byte value = memory.ReadLinear(rowStart + (uint)x);
                return value == 0 ? 0xFF101010 : 0xFFFFFFFF;
            }

            case VideoViewKind.HighColour16:
            {
                uint at = rowStart + (uint)(x * 2);
                int pixel = memory.ReadLinear(at) | (memory.ReadLinear(at + 1) << 8);
                byte red = (byte)(((pixel >> 11) & 0x1F) << 3);
                byte green = (byte)(((pixel >> 5) & 0x3F) << 2);
                byte blue = (byte)((pixel & 0x1F) << 3);
                return Rgba(red, green, blue);
            }

            case VideoViewKind.Planar16:
            {
                // Four planes, one bit each, at the same offset -- the VGA's
                // own arrangement, and the one that looks like noise when
                // read as bytes.
                uint at = rowStart + (uint)(x / 8);
                int bit = 7 - (x % 8);
                int colour = ((memory.ReadPlane(0, at) >> bit) & 1)
                           | (((memory.ReadPlane(1, at) >> bit) & 1) << 1)
                           | (((memory.ReadPlane(2, at) >> bit) & 1) << 2)
                           | (((memory.ReadPlane(3, at) >> bit) & 1) << 3);

                (byte r, byte g, byte b) = machine.VideoRegisters.GetPaletteColor(colour);
                return Rgba(r, g, b);
            }

            case VideoViewKind.Text:
            {
                // Character cells drawn as coloured blocks: not the glyphs,
                // but enough to see the shape of a text screen and where its
                // attributes change.
                uint at = rowStart + (uint)(x * 2);
                byte attribute = memory.ReadLinear(at + 1);
                (byte r, byte g, byte b) = machine.VideoRegisters.GetPaletteColor(attribute & 0x0F);
                return Rgba(r, g, b);
            }

            default:
            {
                byte index = memory.ReadLinear(rowStart + (uint)x);
                (byte r, byte g, byte b) = machine.VideoRegisters.GetPaletteColor(index);
                return Rgba(r, g, b);
            }
        }
    }

    private static uint Rgba(byte r, byte g, byte b)
        => 0xFF000000u | ((uint)b << 16) | ((uint)g << 8) | r;

    // --- moving about ---------------------------------------------------------

    [RelayCommand]
    private void ShowDisplayStart()
    {
        _session.WithMachine(machine =>
        {
            VgaRegisters r = machine.VideoRegisters;
            ViewOffset = r.VesaDisplayStart > 0
                ? (uint)r.VesaDisplayStart
                : r.ExtendedStartAddress * 4;
            ViewStride = r.VesaScanLineBytes > 0
                ? r.VesaScanLineBytes
                : r.EffectiveMode.BytesPerRow;
        });

        Refresh();
    }

    /// <summary>The 0xA0000 window, which is where a real-mode program writes.</summary>
    [RelayCommand]
    private void ShowLegacyWindow()
    {
        _session.WithMachine(machine =>
        {
            ViewOffset = (uint)(machine.VideoRegisters.VesaWindowBank * 0x10000);
            ViewStride = 320;
        });

        Refresh();
    }

    /// <summary>The text window at 0xB8000, which lives in planes 0 and 1.</summary>
    [RelayCommand]
    private void ShowTextWindow()
    {
        ViewOffset = 0x8000;
        ViewStride = 160;
        ViewKind = VideoViewKind.Text;
        Refresh();
    }

    [RelayCommand]
    private void PageDown()
    {
        uint step = (uint)(Math.Max(1, ViewStride) * 100);
        ViewOffset = Math.Min(ViewOffset + step, VgaMemory.TotalSize - 1);
        Refresh();
    }

    [RelayCommand]
    private void PageUp()
    {
        uint step = (uint)(Math.Max(1, ViewStride) * 100);
        ViewOffset = ViewOffset > step ? ViewOffset - step : 0;
        Refresh();
    }

    [RelayCommand]
    private void Home()
    {
        ViewOffset = 0;
        Refresh();
    }
}
