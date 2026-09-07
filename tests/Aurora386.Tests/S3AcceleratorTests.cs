using Aurora386.Core;

namespace Aurora386.Tests;

/// <summary>
/// The S3's 2D drawing engine.
///
/// The idea predates the S3 and comes from IBM's 8514/A: instead of the
/// processor writing every pixel of a rectangle through a 64 KB window, it
/// tells the card where the rectangle is and the card fills it. At 1024x768
/// that is the difference between three quarters of a megabyte crossing the
/// bus and about a dozen register writes.
///
/// Every graphical system of the period is built on three operations, and
/// Windows' display driver is a thin layer over them: scrolling a window is a
/// copy, clearing it is a fill, and its border is four lines. Once the card
/// does those, a 486 can move windows about smoothly on a screen it could not
/// possibly repaint pixel by pixel.
/// </summary>
public class S3AcceleratorTests
{
    private static Machine InSvgaMode()
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        machine.Cpu.State.Halted = false;

        machine.Cpu.State.Ax = 0x4F02;
        machine.Cpu.State.Bx = 0x101;            // 640x480 at 256 colours
        machine.VideoServices.Handle(machine.Cpu);

        machine.Accelerator.ResetClip(640, 480);
        return machine;
    }

    /// <summary>
    /// Writes one of the engine's sixteen-bit registers. The high byte goes
    /// first: the low one is what completes the pair and, for the command
    /// register, what starts the operation.
    /// </summary>
    private static void Write(Machine machine, ushort port, ushort value)
    {
        machine.Io.WriteByte((ushort)(port + 1), (byte)(value >> 8));
        machine.Io.WriteByte(port, (byte)value);
    }

    private static byte Pixel(Machine machine, int x, int y)
        => machine.VideoMemory.ReadLinear((uint)(y * 640 + x));

    [Fact]
    public void FillingARectangleColoursExactlyIt()
    {
        using Machine machine = InSvgaMode();

        Write(machine, 0x86E8, 10);        // x
        Write(machine, 0x82E8, 20);        // y
        Write(machine, 0x96E8, 99);        // width - 1
        Write(machine, 0x92E8, 49);        // height - 1
        Write(machine, 0xA6E8, 0x0F);      // colour
        Write(machine, 0x9AE8, 0x6000);    // fill

        Assert.Equal(0x0F, Pixel(machine, 50, 40));      // inside

        // The corners, because an off-by-one here is the classic bug: the
        // width register holds width minus one, so 99 means a hundred pixels.
        Assert.Equal(0x0F, Pixel(machine, 10, 20));
        Assert.Equal(0x0F, Pixel(machine, 109, 69));
        Assert.Equal(0x00, Pixel(machine, 110, 70));
        Assert.Equal(0x00, Pixel(machine, 9, 19));
    }

    [Fact]
    public void CopyingARectangleMovesThePicture()
    {
        // What scrolling a window is.
        using Machine machine = InSvgaMode();

        Write(machine, 0x86E8, 10);
        Write(machine, 0x82E8, 20);
        Write(machine, 0x96E8, 99);
        Write(machine, 0x92E8, 49);
        Write(machine, 0xA6E8, 0x0F);
        Write(machine, 0x9AE8, 0x6000);

        Write(machine, 0x86E8, 10);        // source
        Write(machine, 0x82E8, 20);
        Write(machine, 0x8EE8, 210);       // destination
        Write(machine, 0x8AE8, 20);
        Write(machine, 0x96E8, 99);
        Write(machine, 0x92E8, 49);
        Write(machine, 0x9AE8, 0xC0A0);    // copy, both directions increasing

        Assert.Equal(0x0F, Pixel(machine, 250, 40));
    }

    [Fact]
    public void AnOverlappingCopyGoesTheDirectionTheDriverAsksFor()
    {
        // The direction bits matter here and nowhere else. Scrolling is an
        // overlapping copy, and going the wrong way overwrites rows that have
        // not been read yet -- which is why the driver chooses and the card
        // obeys rather than deciding for itself.
        using Machine machine = InSvgaMode();

        // A single lit row at y=100.
        Write(machine, 0x86E8, 0);
        Write(machine, 0x82E8, 100);
        Write(machine, 0x96E8, 99);
        Write(machine, 0x92E8, 0);
        Write(machine, 0xA6E8, 0x0F);
        Write(machine, 0x9AE8, 0x6000);

        // Copy the ten rows from 100 up to 99: overlapping, and the driver
        // asks for increasing Y so each row is read before it is written.
        Write(machine, 0x86E8, 0);
        Write(machine, 0x82E8, 100);
        Write(machine, 0x8EE8, 0);
        Write(machine, 0x8AE8, 99);
        Write(machine, 0x96E8, 99);
        Write(machine, 0x92E8, 9);
        Write(machine, 0x9AE8, 0xC0A0);

        Assert.Equal(0x0F, Pixel(machine, 50, 99));
    }

    [Fact]
    public void ALineStopsWhereItsLengthSays()
    {
        using Machine machine = InSvgaMode();

        Write(machine, 0x86E8, 300);
        Write(machine, 0x82E8, 100);
        Write(machine, 0x96E8, 50);        // length
        Write(machine, 0xA6E8, 0x0C);
        Write(machine, 0x9AE8, 0x40A0);    // line, horizontal, increasing

        Assert.Equal(0x0C, Pixel(machine, 300, 100));
        Assert.Equal(0x0C, Pixel(machine, 350, 100));
        Assert.Equal(0x00, Pixel(machine, 351, 100));
    }

    [Fact]
    public void ClippingCutsTheRectangleRatherThanRefusingIt()
    {
        // Clipping is tested per pixel, as the hardware does it, rather than
        // by trimming the rectangle first. Half of a rectangle is drawn and
        // half is not, which is what a driver painting a window partly behind
        // another one needs.
        using Machine machine = InSvgaMode();

        Write(machine, 0xBEE8, 0x2000 | 400);      // clip left at 400

        Write(machine, 0x86E8, 380);
        Write(machine, 0x82E8, 200);
        Write(machine, 0x96E8, 49);
        Write(machine, 0x92E8, 9);
        Write(machine, 0xA6E8, 0x0A);
        Write(machine, 0x9AE8, 0x6000);

        Assert.Equal(0x00, Pixel(machine, 390, 205));   // outside the clip
        Assert.Equal(0x0A, Pixel(machine, 410, 205));   // inside
    }

    [Fact]
    public void TheEngineReportsItselfIdle()
    {
        // A driver polls the status register and waits for the engine to
        // finish. Ours finished before the write that started it returned, so
        // the wait loop finds it idle immediately -- the fastest possible
        // card.
        using Machine machine = InSvgaMode();

        Write(machine, 0x9AE8, 0x6000);

        Assert.Equal(0x00, machine.Io.ReadByte(0x9AE8) & 0x02);
    }

    [Fact]
    public void EachCommandCountsAsOneOperation()
    {
        using Machine machine = InSvgaMode();
        long before = machine.Accelerator.OperationsPerformed;

        Write(machine, 0x96E8, 9);
        Write(machine, 0x92E8, 9);
        Write(machine, 0x9AE8, 0x6000);
        Write(machine, 0x9AE8, 0x6000);

        Assert.Equal(before + 2, machine.Accelerator.OperationsPerformed);
    }
}
