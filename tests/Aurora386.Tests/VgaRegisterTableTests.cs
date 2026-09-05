using Aurora386.Core;
using Xunit;

namespace Aurora386.Tests;

/// <summary>
/// After a mode set the adapter's registers, read back through the ports,
/// must describe the mode the way a VGA ROM leaves them. Windows' virtual
/// display device reads the whole set before it starts and refuses to go on
/// when what it finds does not look like a working adapter.
/// </summary>
public class VgaRegisterTableTests
{
    private static byte Indexed(Machine machine, ushort indexPort, byte index)
    {
        machine.Io.WriteByte(indexPort, index);
        return machine.Io.ReadByte((ushort)(indexPort + 1));
    }

    private static byte Attribute(Machine machine, byte index)
    {
        machine.Io.ReadByte(0x3DA);              // reset the flip-flop
        machine.Io.WriteByte(0x3C0, index);
        return machine.Io.ReadByte(0x3C1);
    }

    private static void SetMode(Machine machine, byte mode) => machine.VideoServices.SetVideoMode(mode);

    [Fact]
    public void ColourTextModeReadsBackTheRomValues()
    {
        using var machine = new Machine();
        SetMode(machine, 0x03);

        Assert.Equal(0x67, machine.Io.ReadByte(0x3CC));
        Assert.Equal(0x5F, Indexed(machine, 0x3D4, 0x00));   // horizontal total
        Assert.Equal(0x50, Indexed(machine, 0x3D4, 0x02));   // horizontal blanking start
        Assert.Equal(0xBF, Indexed(machine, 0x3D4, 0x06));   // vertical total
        Assert.Equal(0x1F, Indexed(machine, 0x3D4, 0x07));   // overflow
        Assert.Equal(0x28, Indexed(machine, 0x3D4, 0x13));   // offset: 80 columns
        Assert.Equal(0xA3, Indexed(machine, 0x3D4, 0x17));   // mode control
        Assert.Equal(0x02, Indexed(machine, 0x3C4, 0x04));   // memory mode: odd/even
        Assert.Equal(0x10, Indexed(machine, 0x3CE, 0x05));   // graphics mode: odd/even
        Assert.Equal(0x0E, Indexed(machine, 0x3CE, 0x06));   // memory map at B8000
        Assert.Equal(0x0C, Attribute(machine, 0x10));        // attribute mode: text, blink
        Assert.Equal(0x08, Attribute(machine, 0x13));        // pixel panning for 9-dot cells
    }

    [Fact]
    public void TheMonochromeCrtcAddressesAnswerToo()
    {
        using var machine = new Machine();
        SetMode(machine, 0x07);

        Assert.Equal(0x66, machine.Io.ReadByte(0x3CC));
        Assert.Equal(0x5F, Indexed(machine, 0x3B4, 0x00));
        Assert.Equal(0x0A, Indexed(machine, 0x3CE, 0x06));   // memory map at B0000
    }

    [Fact]
    public void TheLinearModeReadsBackChainFourAndTheShiftMode()
    {
        using var machine = new Machine();
        SetMode(machine, 0x13);

        Assert.Equal(0x63, machine.Io.ReadByte(0x3CC));
        Assert.Equal(0x0E, Indexed(machine, 0x3C4, 0x04));   // chain-4
        Assert.Equal(0x40, Indexed(machine, 0x3CE, 0x05));   // 256-colour shift
        Assert.Equal(0x41, Attribute(machine, 0x10));        // 8-bit colour
        Assert.Equal(0x9C, Indexed(machine, 0x3D4, 0x10));   // vertical retrace start
    }

    [Fact]
    public void GoingBackToTextClearsWhatTheGraphicsModeSet()
    {
        using var machine = new Machine();
        SetMode(machine, 0x12);
        Assert.Equal(0xE3, machine.Io.ReadByte(0x3CC));
        Assert.Equal(0xDF, Indexed(machine, 0x3D4, 0x12));   // 480 lines

        SetMode(machine, 0x03);
        Assert.Equal(0x67, machine.Io.ReadByte(0x3CC));
        Assert.Equal(0x8F, Indexed(machine, 0x3D4, 0x12));   // 400 lines
        Assert.Equal(0x02, Indexed(machine, 0x3C4, 0x04));
    }

    [Fact]
    public void FeatureControlAndSubsystemEnableReadLikeARealAdapter()
    {
        using var machine = new Machine();
        Assert.Equal(0x00, machine.Io.ReadByte(0x3CA));
        Assert.Equal(0x01, machine.Io.ReadByte(0x3C3));
    }

    [Fact]
    public void TheSwitchSenseIdentifiesAColourMonitor()
    {
        using var machine = new Machine();
        var seen = new List<bool>();
        foreach (byte clock in new byte[] { 0xE3, 0xE7, 0xEB, 0xEF })
        {
            machine.Io.WriteByte(0x3C2, clock);
            seen.Add((machine.Io.ReadByte(0x3C2) & 0x10) != 0);
        }
        Assert.Equal(new[] { true, false, false, true }, seen);
        Assert.Equal(0xEF, machine.Io.ReadByte(0x3CC));   // the write went to misc output
        Assert.NotEqual(0xFF, machine.Io.ReadByte(0x3C2) | 0x80);
    }

    [Fact]
    public void PixelsReachTheDacThroughTheAttributePalette()
    {
        using var machine = new Machine();
        var regs = machine.VideoRegisters;
        machine.VideoServices.SetVideoMode(0x03);

        // Text colour 6 is brown (entry 14h), the bright colours sit at 38h+.
        Assert.Equal(0x14, regs.PaletteIndexFor(6));
        Assert.Equal(0x39, regs.PaletteIndexFor(9));
        Assert.Equal((0xAA, 0x55, 0x00), regs.GetPaletteColor(0x14));
        Assert.Equal((0x55, 0x55, 0xFF), regs.GetPaletteColor(0x39));
        Assert.Equal((0x00, 0x00, 0xAA), regs.GetPaletteColor(regs.PaletteIndexFor(1)));

        // A driver that remaps palette register 4 to DAC entry 1 gets blue
        // for pixel value 4, as Windows' VGA driver does.
        machine.VideoServices.SetVideoMode(0x12);
        machine.Io.ReadByte(0x3DA);
        machine.Io.WriteByte(0x3C0, 0x04);
        machine.Io.WriteByte(0x3C0, 0x01);
        machine.Io.WriteByte(0x3C0, 0x20);
        Assert.Equal(1, regs.PaletteIndexFor(4));
        Assert.Equal((0x00, 0x00, 0xAA), regs.GetPaletteColor(regs.PaletteIndexFor(4)));
    }
}
