using Aurora386.Core;

namespace Aurora386.Tests;

/// <summary>
/// The S3 engine's 32-bit pixel path.
///
/// The Trio64V+ keeps its five colour registers as doublewords and transfers
/// one 32-bit pixel as either one doubleword or two words. These tests use
/// VBE 0140h so every byte is visible in the linear framebuffer.
/// </summary>
public sealed class S3TrueColourTests
{
    private const uint ForegroundColour = 0x12345678;

    private static Machine InTrueColourMode()
    {
        return InSvgaMode(0x140);
    }

    private static Machine InSvgaMode(ushort mode)
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        machine.Cpu.State.Halted = false;

        machine.Cpu.State.Ax = 0x4F02;
        machine.Cpu.State.Bx = mode;
        machine.VideoServices.Handle(machine.Cpu);
        machine.Accelerator.ResetClip(640, 480);

        EnableMmio(machine);
        Assert.True(machine.VideoRegisters.MemoryMappedEngine);
        return machine;
    }

    private static void EnableMmio(Machine machine)
    {
        machine.Io.WriteByte(0x3D4, 0x39);
        machine.Io.WriteByte(0x3D5, 0xA5);
        machine.Io.WriteByte(0x3D4, 0x53);
        machine.Io.WriteByte(0x3D5, 0x10);
    }

    private static uint Pixel(Machine machine, int x, int y)
    {
        uint address = (uint)(y * 2560 + x * 4);
        uint pixel = machine.VideoMemory.ReadLinear(address);
        pixel |= (uint)machine.VideoMemory.ReadLinear(address + 1) << 8;
        pixel |= (uint)machine.VideoMemory.ReadLinear(address + 2) << 16;
        pixel |= (uint)machine.VideoMemory.ReadLinear(address + 3) << 24;
        return pixel;
    }

    private static void SetPixel(Machine machine, int x, int y, uint pixel)
    {
        uint address = (uint)(y * 2560 + x * 4);
        machine.VideoMemory.WriteLinear(address, (byte)pixel);
        machine.VideoMemory.WriteLinear(address + 1, (byte)(pixel >> 8));
        machine.VideoMemory.WriteLinear(address + 2, (byte)(pixel >> 16));
        machine.VideoMemory.WriteLinear(address + 3, (byte)(pixel >> 24));
    }

    private static void SetCurrentPosition(Machine machine, int x, int y)
    {
        machine.Memory.WriteDword(0xA8100, (uint)((x << 16) | y));
    }

    private static void SetPixelCount(Machine machine, int width, int height)
    {
        machine.Memory.WriteDword(0xA8148, (uint)(((width - 1) << 16) | (height - 1)));
    }

    private static void FillOnePixel(Machine machine, ushort foreground)
    {
        machine.Io.WriteWord(0x86E8, 0);
        machine.Io.WriteWord(0x82E8, 0);
        machine.Io.WriteWord(0x96E8, 0);
        machine.Io.WriteWord(0x92E8, 0);
        machine.Io.WriteWord(0xA6E8, foreground);
        machine.Io.WriteWord(0x9AE8, 0x40A0);
    }

    private static ushort Pixel16(Machine machine)
    {
        ushort pixel = machine.VideoMemory.ReadLinear(0);
        pixel |= (ushort)(machine.VideoMemory.ReadLinear(1) << 8);
        return pixel;
    }

    [Fact]
    public void ADoublewordProcessorUploadKeepsAllFourPixelBytes()
    {
        using Machine machine = InTrueColourMode();
        SetPixel(machine, 0, 0, 0xD4C3B2A1);
        SetCurrentPosition(machine, 0, 0);
        SetPixelCount(machine, 1, 1);
        machine.Memory.WriteByte(0xABAE8, 0x47);
        machine.Memory.WriteWord(0xA8118, 0x55B1);

        machine.Memory.WriteDword(0xAE2E8, ForegroundColour);

        Assert.Equal(ForegroundColour, Pixel(machine, 0, 0));
        Assert.Equal(0u, Pixel(machine, 1, 0));
    }

    [Fact]
    public void AWordBusProcessorUploadAssemblesOne32BitPixel()
    {
        using Machine machine = InTrueColourMode();
        SetPixel(machine, 0, 0, 0xD4C3B2A1);
        SetCurrentPosition(machine, 0, 0);
        SetPixelCount(machine, 1, 1);
        machine.Memory.WriteByte(0xABAE8, 0x47);
        machine.Memory.WriteWord(0xA8118, 0x53B1);

        machine.Memory.WriteWord(0xAE2E8, 0x5678);
        machine.Memory.WriteWord(0xAE2E8, 0x1234);

        Assert.Equal(ForegroundColour, Pixel(machine, 0, 0));
    }

    [Fact]
    public void AFullPackedForegroundRegisterDrivesASolidFill()
    {
        using Machine machine = InTrueColourMode();
        SetPixel(machine, 0, 0, 0xD4C3B2A1);
        SetCurrentPosition(machine, 0, 0);
        SetPixelCount(machine, 1, 1);
        machine.Memory.WriteDword(0xA8124, ForegroundColour);
        machine.Memory.WriteWord(0xA8118, 0x40B1);

        Assert.Equal(ForegroundColour, Pixel(machine, 0, 0));
    }

    [Fact]
    public void PackedColourLanesPreserveTheOtherHalfOfTheRegister()
    {
        using Machine machine = InTrueColourMode();
        machine.Memory.WriteDword(0xA8124, 0x11223344);
        machine.Memory.WriteWord(0xA8126, 0x5566);
        machine.Memory.WriteByte(0xA8127, 0x77);

        Assert.Equal(0x77663344u, machine.Accelerator.Mmio.ReadDword(0x8124));
        Assert.Equal(0x77663344u, machine.Memory.ReadDword(0xA8124));
    }

    [Fact]
    public void ABitBltCopiesEveryChannelOfA32BitPixel()
    {
        using Machine machine = InTrueColourMode();
        SetPixel(machine, 10, 20, ForegroundColour);
        machine.Memory.WriteDword(0xA8100, (uint)((10 << 16) | 20));
        machine.Memory.WriteDword(0xA8108, (uint)((100 << 16) | 40));
        SetPixelCount(machine, 1, 1);
        machine.Memory.WriteWord(0xA8136, 0x0067);
        machine.Memory.WriteWord(0xA8118, 0xC0A0);

        Assert.Equal(ForegroundColour, Pixel(machine, 100, 40));
    }

    [Fact]
    public void AXorFillChangesEveryChannelOfA32BitPixel()
    {
        using Machine machine = InTrueColourMode();
        SetPixel(machine, 0, 0, 0xA1B2C3D4);
        SetCurrentPosition(machine, 0, 0);
        SetPixelCount(machine, 1, 1);
        machine.Memory.WriteDword(0xA8124, ForegroundColour);
        machine.Memory.WriteWord(0xA8136, 0x0025);
        machine.Memory.WriteWord(0xA8118, 0x40A0);

        Assert.Equal(0xB38695ACu, Pixel(machine, 0, 0));
    }

    [Fact]
    public void AWriteMaskCanSelectOnlyTheRedChannel()
    {
        using Machine machine = InTrueColourMode();
        SetPixel(machine, 0, 0, 0xA1B2C3D4);
        SetCurrentPosition(machine, 0, 0);
        SetPixelCount(machine, 1, 1);
        machine.Memory.WriteDword(0xA8124, ForegroundColour);
        machine.Memory.WriteDword(0xA8128, 0x00FF0000);
        machine.Memory.WriteWord(0xA8118, 0x40A0);

        Assert.Equal(0xA134C3D4u, Pixel(machine, 0, 0));
    }

    [Fact]
    public void AReadMaskCanSelectAHighChannelForTheBackgroundMix()
    {
        using Machine machine = InTrueColourMode();
        SetPixel(machine, 10, 20, 0x00000001);
        SetPixel(machine, 11, 20, 0x80000001);
        machine.Memory.WriteDword(0xA8100, (uint)((10 << 16) | 20));
        machine.Memory.WriteDword(0xA8108, (uint)((100 << 16) | 40));
        SetPixelCount(machine, 2, 1);
        machine.Memory.WriteDword(0xA8124, 0x11223344);
        machine.Memory.WriteDword(0xA8120, 0x55667788);
        machine.Memory.WriteDword(0xA812C, 0x80000000);
        machine.Memory.WriteWord(0xA8134, 0x0007);
        machine.Memory.WriteWord(0xA8136, 0x0027);
        machine.Memory.WriteWord(0xA8140, 0x00C0);
        machine.Memory.WriteWord(0xA8118, 0xC0A0);

        Assert.Equal(0x55667788u, Pixel(machine, 100, 40));
        Assert.Equal(0x11223344u, Pixel(machine, 101, 40));
    }

    [Fact]
    public void AMonochromeMaskUsesFullWidthForegroundAndBackgroundColours()
    {
        using Machine machine = InTrueColourMode();
        SetCurrentPosition(machine, 0, 10);
        SetPixelCount(machine, 2, 1);
        machine.Memory.WriteDword(0xA8124, 0x11223344);
        machine.Memory.WriteDword(0xA8120, 0x55667788);
        machine.Memory.WriteWord(0xA8134, 0x0007);
        machine.Memory.WriteWord(0xA8136, 0x0027);
        machine.Memory.WriteWord(0xA8140, 0x0080);
        machine.Memory.WriteWord(0xA8118, 0x51B3);

        machine.Memory.WriteByte(0xAE2E8, 0x80);

        Assert.Equal(0x11223344u, Pixel(machine, 0, 10));
        Assert.Equal(0x55667788u, Pixel(machine, 1, 10));
    }

    [Fact]
    public void PioWordsUseTheRsfToSelectEachHalfOfAColourRegister()
    {
        using Machine machine = InTrueColourMode();
        machine.Io.WriteWord(0xBEE8, 0xE000);
        machine.Io.WriteWord(0xA6E8, 0x5678);
        machine.Io.WriteWord(0xA6E8, 0x1234);

        machine.Io.WriteWord(0xBEE8, 0xE000);
        Assert.Equal(0x5678, machine.Io.ReadWord(0xA6E8));
        Assert.Equal(0x1234, machine.Io.ReadWord(0xA6E8));
    }

    [Fact]
    public void PioDoublewordModeWritesAllFourColourBytesAtOnce()
    {
        using Machine machine = InTrueColourMode();
        machine.Io.WriteWord(0xBEE8, 0xE200);
        machine.Io.WriteDword(0xA6E8, ForegroundColour);

        Assert.Equal(ForegroundColour, machine.Io.ReadDword(0xA6E8));
    }

    [Fact]
    public void ClassicMmioColourLanesPreserveTheOtherBytes()
    {
        using Machine machine = InTrueColourMode();
        machine.Memory.WriteDword(0xAA6E8, 0x11223344);
        machine.Memory.WriteWord(0xAA6EA, 0x5566);
        machine.Memory.WriteByte(0xAA6EB, 0x77);

        Assert.Equal(0x77663344u, machine.Memory.ReadDword(0xAA6E8));
    }

    [Fact]
    public void PioByteReadsAdvanceRsfOnlyAfterTheHighByte()
    {
        using Machine machine = InTrueColourMode();
        machine.Io.WriteWord(0xBEE8, 0xE000);
        machine.Io.WriteWord(0xA6E8, 0x5678);
        machine.Io.WriteWord(0xA6E8, 0x1234);
        machine.Io.WriteWord(0xBEE8, 0xE000);

        Assert.Equal(0x78, machine.Io.ReadByte(0xA6E8));
        Assert.Equal(0x56, machine.Io.ReadByte(0xA6E9));
        Assert.Equal(0x34, machine.Io.ReadByte(0xA6E8));
        Assert.Equal(0x12, machine.Io.ReadByte(0xA6E9));
    }

    [Fact]
    public void AnEightBitCompareIgnoresTheHighRegisterBits()
    {
        using Machine machine = InSvgaMode(0x101);
        FillOnePixel(machine, 0x0007);
        machine.Memory.WriteWord(0xAB2E8, 0x0707);
        machine.Memory.WriteWord(0xAB2EA, 0xBEEF);
        machine.Io.WriteWord(0xBEE8, 0xA008);

        FillOnePixel(machine, 0x0004);

        Assert.Equal(0x07, machine.VideoMemory.ReadLinear(0));
    }

    [Fact]
    public void A16BitCompareIgnoresTheHighRegisterBits()
    {
        using Machine machine = InSvgaMode(0x111);
        FillOnePixel(machine, 0x0707);
        machine.Memory.WriteWord(0xAB2E8, 0x0707);
        machine.Memory.WriteWord(0xAB2EA, 0xBEEF);
        machine.Io.WriteWord(0xBEE8, 0xA008);

        FillOnePixel(machine, 0x0404);

        Assert.Equal(0x0707, Pixel16(machine));
    }

    [Fact]
    public void A32BitCompareIncludesTheHighChannel()
    {
        using Machine machine = InTrueColourMode();
        SetPixel(machine, 0, 0, 0x12000000);
        SetCurrentPosition(machine, 0, 0);
        SetPixelCount(machine, 1, 1);
        machine.Memory.WriteDword(0xA8124, ForegroundColour);
        machine.Memory.WriteDword(0xA8130, 0x12000000);
        machine.Memory.WriteWord(0xA8140, 0x0008);
        machine.Memory.WriteWord(0xA8118, 0x40A0);

        Assert.Equal(0x12000000u, Pixel(machine, 0, 0));
    }
}
