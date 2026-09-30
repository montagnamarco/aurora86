using Aurora86.Core.Devices.Video;

namespace Aurora86.Tests;

/// <summary>
/// A filled 256-colour screen has to reach the framebuffer.
///
/// WHERE THIS CAME FROM. Aurora OS ran Monkey Island 2 to its title screen,
/// which fills the screen with one colour -- 160, a dark purple -- and the
/// display stayed black. Everything measurable from inside the guest was
/// correct and was measured: video memory held 594 of 660 sampled bytes as
/// colour 160, palette entry 160 read back as rgb 32,0,44, the PEL mask was
/// 0xFF, the attribute controller's video-enable bit was set, the CRTC start
/// address was zero, the sequencer's chain-four bit was set, and graphics
/// register 5 held 0x40 for the 256-colour shift.
///
/// A stripe the guest repainted every turn in colour 15 WAS visible at the
/// same time. Two colours in the same memory, with correct palette entries
/// and every register in order, behaving differently on screen is not
/// something the guest can cause -- so the question moved here.
///
/// These tests are deliberately about the plainest thing that could be
/// wrong: that a byte written into the mode 13h window becomes a pixel of
/// the colour its palette entry names.
/// </summary>
public class VgaLinearFillTests
{
    /// <summary>
    /// The first row whose left-hand pixel is not the expected colour, or -1.
    /// A boundary says far more than a single failing pixel: a picture that
    /// stops part way names the limit it stopped at.
    /// </summary>
    private static int FirstBlackRow(VgaRenderer renderer, uint expected)
    {
        for (int y = 0; y < renderer.Height; y++)
            if (renderer.Framebuffer[y * renderer.Width] != expected)
                return y;

        return -1;
    }

    private static (VgaRegisters Registers, VgaMemory Memory, VgaRenderer Renderer) NewMode13()
    {
        var registers = new VgaRegisters { VideoMode = 0x13 };
        var memory = new VgaMemory(registers);
        var renderer = new VgaRenderer(memory, registers);

        VgaModeInfo info = VgaModes.Get(0x13);

        registers.TextColumns = info.TextColumns;
        registers.TextRows = info.TextRows;
        registers.CharacterHeight = info.CharHeight;
        registers.SetSequencer(VgaRegisters.SeqMapMask, 0x0F);
        registers.SetGraphics(VgaRegisters.GcBitMask, 0xFF);
        /*
         * THE OFFSET REGISTER IS 40 FOR MODE 13h, NOT 160, AND THE ARITHMETIC
         * IS WHY.
         *
         * RowOffsetBytes multiplies this by 8 when chain-four is on and by 2
         * when it is not, so mode 13h's 320-byte row is 40 * 8. Setting it to
         * BytesPerRow / 2 -- which is right for the planar modes and is what
         * the neighbouring tests do -- gives 160 * 8 = 1280, and the renderer
         * then draws 64000 / 1280 = 50 rows of a 200-row picture.
         *
         * That wrong value was in the first version of this test and produced
         * exactly the symptom being investigated: the top of the screen drawn
         * and the rest black. It was the test that was wrong; Aurora OS sets
         * 0x28 here, which is 40, and is correct.
         */
        registers.SetSequencer(0x04, 0x0E);      /* chain four, as 13h needs */
        registers.SetCrtc(0x13, 40);

        return (registers, memory, renderer);
    }

    /// <summary>
    /// The exact case that was on the screen: every byte set to 160, with
    /// palette entry 160 a dark purple.
    /// </summary>
    [Fact]
    public void AScreenFilledWithColour160IsPurple()
    {
        var (registers, memory, renderer) = NewMode13();

        registers.SetPaletteEntry(160, 32, 0, 44);

        for (uint i = 0; i < 320 * 200; i++)
            memory.WriteByte(i, 160);

        renderer.Render(force: true);

        (byte r, byte g, byte b) = registers.GetPaletteColor(160);

        /*
         * WHAT THE CARD THINKS THE COLOUR IS, before asking what it drew.
         * If this is already black the palette write did not take, and the
         * renderer is innocent; if it is purple and the pixel is black, the
         * loss is between the two.
         */
        Assert.True(r + g + b != 0,
                    $"palette entry 160 reads back as {r},{g},{b} -- " +
                    $"raw {registers.GetRawPaletteEntry(160)}, " +
                    $"memory[0] = {memory[0]}");

        /*
         * THE CHANNEL ORDER IS THE RENDERER'S, NOT THE OBVIOUS ONE.
         * ToRgba packs red in the LOW byte -- r | g<<8 | b<<16 | alpha<<24 --
         * and a test that assumed the other order would fail against a
         * perfectly correct renderer, which is what the first version of
         * this test did.
         */
        uint expected = (uint)(r | (g << 8) | (b << 16) | (0xFF << 24));

        /*
         * WHAT THE RENDERER READ, before what it drew. The palette is
         * correct, so if the byte it fetched is not 160 the loss is in the
         * addressing rather than in the colour.
         */
        Assert.True(memory[0] == 160,
                    $"memory[0] is {memory[0]}, not 160");

        Assert.True(expected == renderer.Framebuffer[0],
                    $"pixel 0 is {renderer.Framebuffer[0]:X8}, wanted " +
                    $"{expected:X8}; memory[0]={memory[0]}, " +
                    $"mode layout={registers.EffectiveMode.Layout}, " +
                    $"rowOffset={registers.RowOffsetBytes}");

        /*
         * AND THE MIDDLE OF THE SCREEN, which is what actually failed and is
         * the whole finding: pixel 0 is correct and the middle is black, so
         * the top of the picture is drawn and the rest is not.
         */
        Assert.True(expected == renderer.Framebuffer[320 * 100 + 160],
                    $"the middle pixel is " +
                    $"{renderer.Framebuffer[320 * 100 + 160]:X8}, wanted " +
                    $"{expected:X8}; renderer says width={renderer.Width} " +
                    $"height={renderer.Height}, mode says " +
                    $"layout={registers.EffectiveMode.Layout}, " +
                    $"first black row={FirstBlackRow(renderer, expected)}");
    }

    /// <summary>
    /// And the control the guest could see: colour 15, white, in the same
    /// memory. If this passes while the one above fails, the difference is
    /// in the colour rather than in the fill.
    /// </summary>
    [Fact]
    public void AScreenFilledWithColour15IsWhite()
    {
        var (registers, memory, renderer) = NewMode13();

        registers.SetPaletteEntry(15, 63, 63, 63);

        for (uint i = 0; i < 320 * 200; i++)
            memory.WriteByte(i, 15);

        renderer.Render(force: true);

        (byte r, byte g, byte b) = registers.GetPaletteColor(15);
        /*
         * THE CHANNEL ORDER IS THE RENDERER'S, NOT THE OBVIOUS ONE.
         * ToRgba packs red in the LOW byte -- r | g<<8 | b<<16 | alpha<<24 --
         * and a test that assumed the other order would fail against a
         * perfectly correct renderer, which is what the first version of
         * this test did.
         */
        uint expected = (uint)(r | (g << 8) | (b << 16) | (0xFF << 24));

        Assert.Equal(expected, renderer.Framebuffer[0]);
    }

    /// <summary>
    /// A PICTURE THAT NOBODY IS REDRAWING MUST STAY ON THE SCREEN.
    ///
    /// WHERE THIS COMES FROM. Monkey Island 2 fills its title screen and then
    /// STOPS: it polls for a keypress and touches video memory no further.
    /// Marco sees the picture for a moment and then black. Everything
    /// measurable inside the guest is correct and stays correct -- the row
    /// offset reads 40, the start address 0, the mode is graphics, the
    /// palette is bright, and video memory holds 635 of 660 sampled bytes
    /// throughout.
    ///
    /// Render() returns early when nothing is dirty, which is right: there is
    /// nothing to draw. What this checks is that the framebuffer still HOLDS
    /// the last picture afterwards, because a host presenting it every frame
    /// would otherwise show whatever the buffer became.
    /// </summary>
    [Fact]
    public void AStillPictureSurvivesRendersThatDrawNothing()
    {
        var (registers, memory, renderer) = NewMode13();

        registers.SetPaletteEntry(160, 32, 0, 44);

        for (uint i = 0; i < 320 * 200; i++)
            memory.WriteByte(i, 160);

        renderer.Render(force: true);

        (byte r, byte g, byte b) = registers.GetPaletteColor(160);
        uint expected = (uint)(r | (g << 8) | (b << 16) | (0xFF << 24));

        Assert.Equal(expected, renderer.Framebuffer[320 * 100 + 160]);

        /*
         * AND NOW NOBODY WRITES ANYTHING, which is the game waiting for a
         * key. Several renders, because once is not a soak: a buffer that is
         * cleared on some later pass would survive a single one.
         */
        for (int i = 0; i < 10; i++)
            renderer.Render();

        Assert.Equal(expected, renderer.Framebuffer[0]);
        Assert.Equal(expected, renderer.Framebuffer[320 * 100 + 160]);
        Assert.Equal(expected, renderer.Framebuffer[320 * 199]);
    }

    /// <summary>
    /// THE SECOND RENDER AFTER A MODE CHANGE MUST NOT LOSE THE PICTURE.
    ///
    /// THE SEQUENCE BEING REPRODUCED, from what Marco sees: the picture
    /// appears for a moment and then goes black. The first frame after a mode
    /// change is drawn with force, because the resize sets it -- so it is
    /// correct, and that is the moment he sees. What this checks is the frame
    /// after: the renderer returns early when nothing is dirty, and the
    /// question is whether the framebuffer it leaves behind still holds the
    /// picture or has been replaced by the resize's empty one.
    /// </summary>
    [Fact]
    public void TheFrameAfterAModeChangeStillHasThePicture()
    {
        var (registers, memory, renderer) = NewMode13();

        registers.SetPaletteEntry(160, 32, 0, 44);

        /*
         * A RENDER IN THE OLD GEOMETRY FIRST, so the one below is genuinely a
         * change rather than the first render of all.
         */
        renderer.Render(force: true);

        for (uint i = 0; i < 320 * 200; i++)
            memory.WriteByte(i, 160);

        /* The frame that resizes and draws: this is the one that is seen. */
        renderer.Render();

        (byte r, byte g, byte b) = registers.GetPaletteColor(160);
        uint expected = (uint)(r | (g << 8) | (b << 16) | (0xFF << 24));

        Assert.Equal(expected, renderer.Framebuffer[320 * 100 + 160]);

        /* And the frames after it, where nothing is dirty any more. */
        renderer.Render();
        renderer.Render();

        Assert.Equal(expected, renderer.Framebuffer[320 * 100 + 160]);
    }

    /// <summary>
    /// THE WINDOW'S OWN SEQUENCE: render, then copy only when something
    /// changed.
    ///
    /// WHY THIS IS DIFFERENT FROM THE TESTS ABOVE. Those check the
    /// RENDERER'S framebuffer, and it keeps the picture faithfully. The SDL
    /// window keeps a SECOND copy -- an SDL texture -- and refreshes it only
    /// when Render() reports a change. Everything that reaches the screen
    /// goes through that second copy, and nothing had ever measured it.
    ///
    /// This reproduces the window's loop without SDL: render, ask whether
    /// anything changed, and copy only if it did. If the emulated texture
    /// ends up holding the picture, the loop is sound and the fault is in
    /// SDL itself; if it ends up blank, the loop loses the frame.
    /// </summary>
    [Fact]
    public void TheWindowsCopyKeepsThePictureAcrossQuietFrames()
    {
        var (registers, memory, renderer) = NewMode13();

        registers.SetPaletteEntry(160, 32, 0, 44);

        for (uint i = 0; i < 320 * 200; i++)
            memory.WriteByte(i, 160);

        /* The window's texture, which starts empty as SDL's does. */
        var texture = new uint[320 * 200];

        /*
         * TEN FRAMES OF THE WINDOW'S LOOP. The guest writes nothing more --
         * the game is waiting for a key -- so only the first can report a
         * change.
         */
        for (int frame = 0; frame < 10; frame++)
        {
            bool changed = renderer.Render();

            if (changed)
            {
                renderer.Framebuffer[..texture.Length].CopyTo(texture);
            }
        }

        (byte r, byte g, byte b) = registers.GetPaletteColor(160);
        uint expected = (uint)(r | (g << 8) | (b << 16) | (0xFF << 24));

        Assert.Equal(expected, texture[0]);
        Assert.Equal(expected, texture[320 * 100 + 160]);
        Assert.Equal(expected, texture[320 * 199]);
    }

    /// <summary>
    /// WITHOUT force, which is how the machine actually renders.
    ///
    /// Render(force: true) repaints every row whatever happened; the real
    /// path repaints only rows marked dirty by a write. A fill that does not
    /// mark its rows would draw under force and stay black in practice --
    /// which is exactly the shape of "the guest filled the screen and nothing
    /// appeared", and is why this is a separate test rather than a parameter.
    /// </summary>
    [Fact]
    public void AFillMarksItsRowsSoAnOrdinaryRenderDrawsIt()
    {
        var (registers, memory, renderer) = NewMode13();

        registers.SetPaletteEntry(160, 32, 0, 44);

        /* One render first, so the fill below is the only thing outstanding. */
        renderer.Render(force: true);

        for (uint i = 0; i < 320 * 200; i++)
            memory.WriteByte(i, 160);

        renderer.Render();

        (byte r, byte g, byte b) = registers.GetPaletteColor(160);
        /*
         * THE CHANNEL ORDER IS THE RENDERER'S, NOT THE OBVIOUS ONE.
         * ToRgba packs red in the LOW byte -- r | g<<8 | b<<16 | alpha<<24 --
         * and a test that assumed the other order would fail against a
         * perfectly correct renderer, which is what the first version of
         * this test did.
         */
        uint expected = (uint)(r | (g << 8) | (b << 16) | (0xFF << 24));

        Assert.Equal(expected, renderer.Framebuffer[0]);
        Assert.Equal(expected, renderer.Framebuffer[320 * 199]);
    }
}
