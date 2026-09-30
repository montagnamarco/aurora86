using Aurora86.Core;

namespace Aurora86.Tests;

/// <summary>
/// Changing the text font height by programming the CRTC directly.
///
/// Why this file exists: an operating system kernel does not call the BIOS.
/// Aurora OS forbids it outright after boot -- calling BIOS services from
/// protected mode would mean dropping back to real mode -- and every other
/// protected-mode system avoids it for the same reason. So a kernel that
/// wants the 8x8 font and its fifty rows programmes CRTC register 0x09
/// itself, exactly as it programmes every other piece of hardware.
///
/// The character cell height IS that register: bits 0-4 hold the height minus
/// one, which is how a real VGA knows how tall a character is. A card that
/// took the height only from a BIOS call would be a card that cannot be
/// driven by a kernel, which is the one thing this emulator exists to do.
/// </summary>
public class TextFontHeightTests
{
    private const int CrtcIndexPort = 0x3D4;
    private const int CrtcDataPort = 0x3D5;
    private const byte CrtcMaximumScanLine = 0x09;

    private static Machine BuildTextModeMachine()
    {
        var machine = new Machine();
        machine.RunInstructions(200);   // let the BIOS finish its POST
        machine.VideoServices.SetVideoMode(3);
        return machine;
    }

    private static void WriteCrtc(Machine machine, byte index, byte value)
    {
        machine.Io.WriteByte(CrtcIndexPort, index);
        machine.Io.WriteByte(CrtcDataPort, value);
    }

    [Fact]
    public void TheDefaultTextModeIsTwentyFiveRowsOfSixteenPixelCells()
    {
        // The state every PC powers up in, and the baseline the change below
        // is measured against.
        using Machine machine = BuildTextModeMachine();

        Assert.Equal(16, machine.VideoRegisters.CharacterHeight);
        Assert.Equal(25, machine.VideoRegisters.TextRows);
        Assert.Equal(80, machine.VideoRegisters.TextColumns);
    }

    [Fact]
    public void WritingTheMaximumScanLineRegisterChangesTheCellHeight()
    {
        // The register holds the height MINUS ONE, which is the detail most
        // easily got wrong: writing 8 gives nine-pixel cells, not eight.
        using Machine machine = BuildTextModeMachine();

        WriteCrtc(machine, CrtcMaximumScanLine, 7);

        Assert.Equal(8, machine.VideoRegisters.CharacterHeight);
    }

    [Fact]
    public void EightPixelCellsGiveFiftyRows()
    {
        // The whole point of the exercise: the display is 400 scan lines
        // whatever the font, so halving the cell height doubles the rows. A
        // kernel that set the register and still got 25 rows would draw fifty
        // rows of text into a screen showing half of them.
        using Machine machine = BuildTextModeMachine();

        WriteCrtc(machine, CrtcMaximumScanLine, 7);

        Assert.Equal(50, machine.VideoRegisters.TextRows);
        Assert.Equal(80, machine.VideoRegisters.TextColumns);
    }

    [Fact]
    public void FourteenPixelCellsGiveTwentyEightRows()
    {
        // The EGA's font, which some software still asks for. Included because
        // a fix that special-cased eight would pass the test above and fail
        // here, and a special case is not an implementation.
        using Machine machine = BuildTextModeMachine();

        WriteCrtc(machine, CrtcMaximumScanLine, 13);

        Assert.Equal(14, machine.VideoRegisters.CharacterHeight);
        Assert.Equal(28, machine.VideoRegisters.TextRows);
    }

    [Fact]
    public void GoingBackToSixteenRestoresTwentyFiveRows()
    {
        // The change is not one-way. A kernel that switched to the small font
        // for its boot messages and back for a program must get its rows back.
        using Machine machine = BuildTextModeMachine();

        WriteCrtc(machine, CrtcMaximumScanLine, 7);
        Assert.Equal(50, machine.VideoRegisters.TextRows);

        WriteCrtc(machine, CrtcMaximumScanLine, 15);

        Assert.Equal(16, machine.VideoRegisters.CharacterHeight);
        Assert.Equal(25, machine.VideoRegisters.TextRows);
    }

    [Fact]
    public void OnlyTheLowFiveBitsAreTheHeight()
    {
        // Bit 5 is the start of the vertical blank's high bit and bit 7
        // doubles the scan line count; neither is part of the height. A
        // renderer that masked the whole byte would read a height of 135 from
        // a perfectly ordinary register write.
        using Machine machine = BuildTextModeMachine();

        WriteCrtc(machine, CrtcMaximumScanLine, 0x80 | 7);

        Assert.Equal(8, machine.VideoRegisters.CharacterHeight);
    }
}
