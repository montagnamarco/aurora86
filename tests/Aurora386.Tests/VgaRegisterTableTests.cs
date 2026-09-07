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

/// <summary>
/// The colour text window and its odd/even mapping.
///
/// The CPU sees characters and attributes alternating at 0xB8000, but that is
/// not how they are stored: even addresses go to plane 0 and odd ones to
/// plane 1, both at the same offset. So a screenful of characters is
/// contiguous in one plane and a screenful of attributes contiguous in
/// another, which is exactly what the character generator needs -- it fetches
/// both from the same address in one memory cycle.
///
/// It matters because a program can see through the illusion: period code
/// reads plane 0 directly to scan the screen at twice the speed, and storing
/// the interleaved bytes as the CPU wrote them would hand it an alternating
/// string of characters and colour bytes.
/// </summary>
public class TextWindowMappingTests
{
    [Fact]
    public void CharactersAndAttributesLandInDifferentPlanes()
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);

        machine.VideoMemory.WriteByte(0x18000, (byte)'A');
        machine.VideoMemory.WriteByte(0x18001, 0x1F);

        Assert.Equal((byte)'A', machine.VideoMemory.ReadPlane(0, 0x8000));
        Assert.Equal(0x1F, machine.VideoMemory.ReadPlane(1, 0x8000));
    }

    [Fact]
    public void TheCpuStillSeesThemInterleaved()
    {
        // The mapping is invisible from the address bus, which is the whole
        // point: every program written since 1981 writes a character and then
        // its attribute at the next address.
        using var machine = new Machine();
        machine.RunInstructions(3000);

        machine.VideoMemory.WriteByte(0x18000, (byte)'X');
        machine.VideoMemory.WriteByte(0x18001, 0x4E);

        Assert.Equal((byte)'X', machine.VideoMemory.ReadByte(0x18000));
        Assert.Equal(0x4E, machine.VideoMemory.ReadByte(0x18001));
    }

    [Fact]
    public void ConsecutiveCharactersAreContiguousInPlaneZero()
    {
        // What the character generator wants, and what a program scanning the
        // screen through plane 0 relies on: no attribute bytes in between.
        using var machine = new Machine();
        machine.RunInstructions(3000);

        machine.VideoMemory.WriteByte(0x18000, (byte)'H');
        machine.VideoMemory.WriteByte(0x18002, (byte)'i');

        Assert.Equal((byte)'H', machine.VideoMemory.ReadPlane(0, 0x8000));
        Assert.Equal((byte)'i', machine.VideoMemory.ReadPlane(0, 0x8001));
    }

    [Fact]
    public void TheBiosHelpersUseTheSameMapping()
    {
        // The indexer bypasses the write modes -- the BIOS is writing
        // characters, not driving the graphics controller -- but it must not
        // bypass where the byte lives, or the screen would look right through
        // that one path and wrong to everything else.
        using var machine = new Machine();
        machine.RunInstructions(3000);

        machine.VideoMemory[0x18000] = (byte)'Q';
        machine.VideoMemory[0x18001] = 0x70;

        Assert.Equal((byte)'Q', machine.VideoMemory.ReadPlane(0, 0x8000));
        Assert.Equal(0x70, machine.VideoMemory.ReadPlane(1, 0x8000));
        Assert.Equal((byte)'Q', machine.VideoMemory[0x18000]);
    }
}
