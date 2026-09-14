using Aurora386.Core;
using Aurora386.Core.Devices.Video;

namespace Aurora386.Tests;

/// <summary>
/// Programs the ViRGE 86C325's measured 80C00006h Gouraud command through
/// its real MMIO latches. Exact integer-edge vectors expose S8.7 RGB and
/// S16.15 Z interpolation, clipping, strict depth ordering and VRAM bounds
/// without claiming undocumented fractional-edge rounding behavior.
/// </summary>
public class S3VirgeGouraudTests
{
    private const uint MmioBase = 0x01200000;
    private const uint GouraudZRgb1555Command = 0x80C00006;
    private const uint DestinationBase = 0x100;
    private const uint DepthBase = 0x300;
    private const uint Stride = 32;
    private const byte Sentinel = 0xCC;

    [Fact]
    public void Command80C00006InterpolatesS87RgbAndS1615DepthFromPointA()
    {
        using Machine machine = CreateGradientMachine();

        Write(machine, 0xB500, GouraudZRgb1555Command);

        AssertPixel(machine, 1, 3, 0x0480, 100);
        AssertPixel(machine, 2, 3, 0x0864, 102);
        AssertPixel(machine, 3, 3, 0x0C48, 104);
        AssertPixel(machine, 4, 3, 0x102C, 106);
        AssertPixel(machine, 1, 2, 0x0CA0, 110);
        AssertPixel(machine, 2, 2, 0x1084, 112);
        AssertPixel(machine, 3, 2, 0x1468, 114);
        AssertPixel(machine, 1, 1, 0x14C0, 120);
        AssertPixel(machine, 2, 1, 0x18A4, 122);
        AssertUntouched(machine, 0, 3);
        AssertUntouched(machine, 5, 3);
        AssertUntouched(machine, 3, 1);
    }

    [Fact]
    public void B4DCB4E0HardwareClipKeepsUnclippedAttributeAndDepthOrigins()
    {
        using Machine machine = CreateGradientMachine();
        Write(machine, 0xB4DC, 0x00030003);
        Write(machine, 0xB4E0, 0x00020002);

        Write(machine, 0xB500, GouraudZRgb1555Command);

        AssertPixel(machine, 3, 2, 0x1468, 114);
        AssertUntouched(machine, 1, 3);
        AssertUntouched(machine, 2, 2);
        AssertUntouched(machine, 1, 1);
    }

    [Fact]
    public void B500Bits2320DepthLessRejectsEqualAndFarThenUpdatesNearerPixel()
    {
        using Machine machine = CreateOnePixelMachine();
        uint colourAddress = PixelAddress(DestinationBase, 1, 1);
        uint depthAddress = PixelAddress(DepthBase, 1, 1);
        WriteWord(machine, depthAddress, 200);

        Write(machine, 0xB550, 0x00007F80); // Red 255.
        Write(machine, 0xB55C, 100u << 15);
        Write(machine, 0xB500, GouraudZRgb1555Command);
        Assert.Equal(0x7C00, ReadWord(machine, colourAddress));
        Assert.Equal(100, ReadWord(machine, depthAddress));

        Write(machine, 0xB54C, 0x00007F80); // Blue 255.
        Write(machine, 0xB550, 0);
        Write(machine, 0xB55C, 100u << 15);
        Write(machine, 0xB500, GouraudZRgb1555Command);
        Assert.Equal(0x7C00, ReadWord(machine, colourAddress));
        Assert.Equal(100, ReadWord(machine, depthAddress));

        Write(machine, 0xB54C, 0x7F800000); // Green 255.
        Write(machine, 0xB55C, 101u << 15);
        Write(machine, 0xB500, GouraudZRgb1555Command);
        Assert.Equal(0x7C00, ReadWord(machine, colourAddress));
        Assert.Equal(100, ReadWord(machine, depthAddress));

        Write(machine, 0xB55C, 99u << 15);
        Write(machine, 0xB500, GouraudZRgb1555Command);
        Assert.Equal(0x03E0, ReadWord(machine, colourAddress));
        Assert.Equal(99, ReadWord(machine, depthAddress));
    }

    [Fact]
    public void B57CBit31RightToLeftUsesLongEdgeOriginWithSignedNegativeXGradients()
    {
        using Machine machine = CreateOnePixelMachine();
        Write(machine, 0xB574, 5u << 20);
        Write(machine, 0xB56C, 1u << 20);
        Write(machine, 0xB57C, 0x00010000);
        Write(machine, 0xB550, 40u << 7);
        Write(machine, 0xB540, unchecked((uint)(-8 << 7)));
        Write(machine, 0xB55C, 100u << 15);
        Write(machine, 0xB554, unchecked((uint)(-2 << 15)));

        Write(machine, 0xB500, GouraudZRgb1555Command);

        AssertPixel(machine, 1, 1, 0x0400, 92);
        AssertPixel(machine, 2, 1, 0x0800, 94);
        AssertPixel(machine, 3, 1, 0x0C00, 96);
        AssertPixel(machine, 4, 1, 0x1000, 98);
        AssertUntouched(machine, 5, 1);
    }

    [Fact]
    public void CapturedTriangle3B57CBit31ClearAdvancesSignedXGradientsTowardGreenVertex()
    {
        using Machine machine = CreateOnePixelMachine();
        const uint destinationBase = 0x96000;
        const uint depthBase = 0x12C000;
        const uint capturedStride = 1280;
        machine.VideoMemory.GetSpan(depthBase, 640 * 480 * sizeof(ushort)).Fill(0xFF);
        Write(machine, 0xB4D4, depthBase);
        Write(machine, 0xB4D8, destinationBase);
        Write(machine, 0xB4DC, 0x0000027F);
        Write(machine, 0xB4E0, 0x000001DF);
        Write(machine, 0xB4E4, capturedStride << 16);
        Write(machine, 0xB4E8, capturedStride);
        Write(machine, 0xB568, 0xFFF00000);
        Write(machine, 0xB570, 0);
        Write(machine, 0xB560, 0);
        Write(machine, 0xB574, 0x1B800000);
        Write(machine, 0xB56C, 0x1B800000);
        Write(machine, 0xB564, 0x0C800000);
        Write(machine, 0xB57C, 0x00F00001);
        Write(machine, 0xB544, 0xFF780000);
        Write(machine, 0xB54C, 0x7F807F80);
        Write(machine, 0xB53C, 0x0088FF78);
        Write(machine, 0xB548, 0);
        Write(machine, 0xB550, 0x00007F80);
        Write(machine, 0xB540, 0x0000FF78);
        Write(machine, 0xB554, 0);
        Write(machine, 0xB558, 0);
        Write(machine, 0xB55C, 0x00C80000);
        Write(machine, 0xB578, 0x168);

        Write(machine, 0xB500, GouraudZRgb1555Command);

        Assert.Equal(0x03E0, ReadWord(machine,
            CapturedPixelAddress(destinationBase, capturedStride, 200, 120)));
        Assert.Equal(0x3DEF, ReadWord(machine,
            CapturedPixelAddress(destinationBase, capturedStride, 320, 120)));
        Assert.Equal(0x7C1F, ReadWord(machine,
            CapturedPixelAddress(destinationBase, capturedStride, 439, 120)));
        Assert.Equal(0x7FFF, ReadWord(machine,
            CapturedPixelAddress(destinationBase, capturedStride, 439, 359)));
        Assert.Equal(400, ReadWord(machine,
            CapturedPixelAddress(depthBase, capturedStride, 200, 120)));
    }

    [Fact]
    public void B57CTwoHalvesKeepB574LongEdgeAndB558SignedYDepthContinuous()
    {
        using Machine machine = CreateOnePixelMachine();
        Write(machine, 0xB574, 1u << 20);
        Write(machine, 0xB570, 1u << 20);
        Write(machine, 0xB56C, 5u << 20);
        Write(machine, 0xB568, 0);
        Write(machine, 0xB564, 5u << 20);
        Write(machine, 0xB560, unchecked((uint)(-1 << 20)));
        Write(machine, 0xB578, 4);
        Write(machine, 0xB57C, 0x80020002);
        Write(machine, 0xB550, 40u << 7);
        Write(machine, 0xB548, unchecked((uint)(-8 << 7)));
        Write(machine, 0xB55C, 100u << 15);
        Write(machine, 0xB558, unchecked((uint)(-10 << 15)));

        Write(machine, 0xB500, GouraudZRgb1555Command);

        AssertPixel(machine, 1, 4, 0x1400, 100);
        AssertPixel(machine, 2, 3, 0x1000, 90);
        AssertPixel(machine, 3, 2, 0x0C00, 80);
        AssertUntouched(machine, 1, 3);
        AssertUntouched(machine, 2, 2);
        AssertUntouched(machine, 4, 1);
    }

    [Fact]
    public void B4D8DestinationBoundsSkipOnlyIncompleteFragments()
    {
        using Machine machine = CreateOnePixelMachine();
        uint destinationBase = VgaMemory.TotalSize - 8;
        uint depthBase = VgaMemory.TotalSize - 16;
        Write(machine, 0xB4D8, destinationBase);
        Write(machine, 0xB4D4, depthBase);
        Write(machine, 0xB4E4, 8u << 16);
        Write(machine, 0xB4E8, 8);
        Write(machine, 0xB574, 3u << 20);
        Write(machine, 0xB56C, 5u << 20);
        Write(machine, 0xB578, 0);
        Write(machine, 0xB57C, 0x80010000);
        Write(machine, 0xB4DC, 0x00030004);
        Write(machine, 0xB4E0, 0);
        Write(machine, 0xB550, 0x00007F80);
        Write(machine, 0xB55C, 7u << 15);
        WriteWord(machine, depthBase + 6, ushort.MaxValue);
        WriteWord(machine, depthBase + 8, ushort.MaxValue);

        Write(machine, 0xB500, GouraudZRgb1555Command);

        Assert.Equal(0x7C00, ReadWord(machine, destinationBase + 6));
        Assert.Equal(7, ReadWord(machine, depthBase + 6));
        Assert.Equal(ushort.MaxValue, ReadWord(machine, depthBase + 8));
    }

    [Fact]
    public void B4D4DepthBoundsDoNotWriteOtherwiseValidDestination()
    {
        using Machine machine = CreateOnePixelMachine();
        uint depthBase = VgaMemory.TotalSize - 8;
        Write(machine, 0xB4D4, depthBase);
        Write(machine, 0xB4E8, 8);
        Write(machine, 0xB574, 3u << 20);
        Write(machine, 0xB56C, 5u << 20);
        Write(machine, 0xB578, 0);
        Write(machine, 0xB57C, 0x80010000);
        Write(machine, 0xB4DC, 0x00030004);
        Write(machine, 0xB4E0, 0);
        Write(machine, 0xB550, 0x00007F80);
        Write(machine, 0xB55C, 7u << 15);
        WriteWord(machine, depthBase + 6, ushort.MaxValue);

        Write(machine, 0xB500, GouraudZRgb1555Command);

        Assert.Equal(0x7C00, ReadWord(machine, PixelAddress(DestinationBase, 3, 0)));
        Assert.Equal(7, ReadWord(machine, depthBase + 6));
        Assert.Equal(0xCCCC, ReadWord(machine, PixelAddress(DestinationBase, 4, 0)));
    }

    /// <summary>Creates a three-row triangle with independent X and Y gradients.</summary>
    private static Machine CreateGradientMachine()
    {
        Machine machine = CreateOnePixelMachine();
        Write(machine, 0xB574, 1u << 20);
        Write(machine, 0xB56C, 5u << 20);
        Write(machine, 0xB568, unchecked((uint)(-1 << 20)));
        Write(machine, 0xB578, 3);
        Write(machine, 0xB57C, 0x80030000);

        // Point A is R=8, G=32, B=0. Per X: +8, -8, +32.
        // Per upward scan line: +16, +8, 0.
        Write(machine, 0xB54C, 0x10000000);
        Write(machine, 0xB550, 0x00000400);
        Write(machine, 0xB53C, 0xFC001000);
        Write(machine, 0xB540, 0x00000400);
        Write(machine, 0xB544, 0x04000000);
        Write(machine, 0xB548, 0x00000800);
        Write(machine, 0xB554, 2u << 15);
        Write(machine, 0xB558, 10u << 15);
        Write(machine, 0xB55C, 100u << 15);
        return machine;
    }

    /// <summary>Creates the smallest integer-edge fragment accepted by the measured command.</summary>
    private static Machine CreateOnePixelMachine()
    {
        var machine = new Machine();
        var engine = new S3VirgeEngine(machine.VideoMemory);
        machine.Memory.MapDevice(MmioBase, 0x10000, engine);
        machine.VideoMemory.GetSpan(0, 0x500).Fill(Sentinel);
        Write(machine, 0xB4D4, DepthBase);
        Write(machine, 0xB4D8, DestinationBase);
        Write(machine, 0xB4DC, 0x0000000F);
        Write(machine, 0xB4E0, 0x0000000F);
        Write(machine, 0xB4E4, Stride << 16);
        Write(machine, 0xB4E8, Stride);
        Write(machine, 0xB574, 1u << 20);
        Write(machine, 0xB56C, 2u << 20);
        Write(machine, 0xB578, 1);
        Write(machine, 0xB57C, 0x80010000);

        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                WriteWord(machine, PixelAddress(DepthBase, x, y), ushort.MaxValue);
            }
        }

        return machine;
    }

    private static void AssertPixel(Machine machine, int x, int y, ushort colour, ushort depth)
    {
        Assert.Equal(colour, ReadWord(machine, PixelAddress(DestinationBase, x, y)));
        Assert.Equal(depth, ReadWord(machine, PixelAddress(DepthBase, x, y)));
    }

    private static void AssertUntouched(Machine machine, int x, int y)
    {
        Assert.Equal(0xCCCC, ReadWord(machine, PixelAddress(DestinationBase, x, y)));
        Assert.Equal(ushort.MaxValue, ReadWord(machine, PixelAddress(DepthBase, x, y)));
    }

    private static uint PixelAddress(uint baseAddress, int x, int y)
    {
        return baseAddress + (uint)y * Stride + (uint)x * 2;
    }

    private static uint CapturedPixelAddress(uint baseAddress, uint stride, int x, int y)
    {
        return baseAddress + (uint)y * stride + (uint)x * 2;
    }

    private static ushort ReadWord(Machine machine, uint address)
    {
        return (ushort)(machine.VideoMemory.ReadLinear(address)
            | (machine.VideoMemory.ReadLinear(address + 1) << 8));
    }

    private static void WriteWord(Machine machine, uint address, ushort value)
    {
        machine.VideoMemory.WriteLinear(address, (byte)value);
        machine.VideoMemory.WriteLinear(address + 1, (byte)(value >> 8));
    }

    private static void Write(Machine machine, uint offset, uint value)
    {
        machine.Memory.WriteDword(MmioBase + offset, value);
    }
}
