using Aurora86.Core;
using Aurora86.Core.Cpu;
using Aurora86.Core.Devices.Video;

namespace Aurora86.Tests;

/// <summary>
/// Programs the 1996 ViRGE triangle engine through its real MMIO offsets.
/// The constant-colour vectors make each covered byte independently visible:
/// edge coverage, clipping, pitch and command triggers cannot hide behind a
/// screenshot comparison or an implementation-shaped rasterizer oracle.
/// </summary>
public class S3VirgeTriangleTests
{
    // The bounded Machine bus already has a 64 KB engine slot here. This
    // test replaces that mapping; it does not emulate a ViRGE PCI BAR yet.
    private const uint MmioBase = 0x01200000;
    private const uint Command = 0x83000008;
    private const uint TargetBase = 0x100;
    private const uint Stride = 32;
    private const byte Sentinel = 0xCC;

    /// <summary>Attaches only the engine under test, leaving the default Trio identity intact.</summary>
    private static Machine CreateMachine(out S3VirgeEngine engine)
    {
        var machine = new Machine();
        engine = new S3VirgeEngine(machine.VideoMemory);
        machine.Memory.MapDevice(MmioBase, 0x10000, engine);
        for (uint offset = 0; offset < 0x200; offset++)
        {
            machine.VideoMemory.WriteLinear(offset, Sentinel);
        }

        // Four bottom-up rows with a vertical long edge and a -1 short slope.
        Write(machine, 0xB4D8, TargetBase);
        Write(machine, 0xB4E4, Stride << 16);
        Write(machine, 0xB54C, 0x20001000); // Green 64 and blue 32 in S8.7.
        Write(machine, 0xB550, 0x00007000); // Red 224 in S8.7.
        Write(machine, 0xB574, 0x00100000);
        Write(machine, 0xB56C, 0x00500000);
        Write(machine, 0xB568, 0xFFF00000);
        Write(machine, 0xB578, 4);
        Write(machine, 0xB57C, 0x80040000);
        return machine;
    }

    private static void Write(Machine machine, uint offset, uint value)
    {
        machine.Memory.WriteDword(MmioBase + offset, value);
    }

    /// <summary>Checks the whole target and its guards against an explicit coordinate list.</summary>
    private static void AssertPixels(Machine machine, params (int X, int Y)[] pixels)
    {
        var expected = new byte[0x200];
        Array.Fill(expected, Sentinel);
        foreach ((int x, int y) in pixels)
        {
            int address = (int)TargetBase + y * (int)Stride + x * 3;
            expected[address] = 0x20;
            expected[address + 1] = 0x40;
            expected[address + 2] = 0xE0;
        }

        for (uint offset = 0; offset < expected.Length; offset++)
        {
            Assert.Equal(expected[offset], machine.VideoMemory.ReadLinear(offset));
        }
    }

    private static void AssertLeftToRightTriangle(Machine machine)
    {
        AssertPixels(machine,
            (1, 4), (2, 4), (3, 4), (4, 4),
            (1, 3), (2, 3), (3, 3),
            (1, 2), (2, 2),
            (1, 1));
    }

    [Fact]
    public void CommandWriteDrawsExpectedRgb888Triangle()
    {
        using Machine machine = CreateMachine(out _);
        Assert.Equal(0x3000u, machine.Memory.ReadDword(MmioBase + 0x8504));

        Write(machine, 0xB500, Command);

        AssertLeftToRightTriangle(machine);
        Assert.Equal(0x3000u, machine.Memory.ReadDword(MmioBase + 0x8504));
        Assert.Equal(0x00100000u, machine.Memory.ReadDword(MmioBase + 0xB574));
    }

    [Fact]
    public void AutoExecuteWaitsForYCount()
    {
        using Machine machine = CreateMachine(out _);
        Write(machine, 0xB500, Command | 1);
        AssertPixels(machine);

        Write(machine, 0xB57C, 0x80040000);
        AssertLeftToRightTriangle(machine);

        // Reusing the count trigger must consume the newly programmed colour.
        Write(machine, 0xB550, 0x00002000);
        Write(machine, 0xB57C, 0x80040000);
        Assert.Equal(0x40, machine.VideoMemory.ReadLinear(TargetBase + 4 * Stride + 5));
    }

    [Fact]
    public void NopDisablesAutoExecuteWithoutDrawing()
    {
        using Machine machine = CreateMachine(out S3VirgeEngine engine);
        int unsupported = 0;
        engine.UnsupportedCommandIssued += _ => unsupported++;
        Write(machine, 0xB500, Command | 1);

        Write(machine, 0xB500, 0xFB000008);
        Write(machine, 0xB57C, 0x80040000);

        AssertPixels(machine);
        Assert.Equal(0, unsupported);
    }

    [Fact]
    public void TriangleClipsAgainstInclusiveBounds()
    {
        using Machine machine = CreateMachine(out _);
        Write(machine, 0xB4DC, 0x00020003);
        Write(machine, 0xB4E0, 0x00020003);

        Write(machine, 0xB500, Command | 2);

        AssertPixels(machine, (2, 3), (3, 3), (2, 2));
    }

    [Fact]
    public void TwoTriangleHalvesContinueTheLongEdge()
    {
        using Machine machine = CreateMachine(out _);
        Write(machine, 0xB564, 0x00300000);
        Write(machine, 0xB560, 0xFFF00000);
        Write(machine, 0xB57C, 0x80020002);

        Write(machine, 0xB500, Command);

        AssertLeftToRightTriangle(machine);
    }

    [Fact]
    public void RightToLeftTriangleExcludesTheOppositeEdge()
    {
        using Machine machine = CreateMachine(out _);
        Write(machine, 0xB574, 0x00500000);
        Write(machine, 0xB56C, 0x00100000);
        Write(machine, 0xB568, 0x00100000);
        Write(machine, 0xB57C, 0x00040000);

        Write(machine, 0xB500, Command);

        AssertPixels(machine,
            (1, 4), (2, 4), (3, 4), (4, 4),
            (2, 3), (3, 3), (4, 3),
            (3, 2), (4, 2),
            (4, 1));
    }

    [Fact]
    public void CpuMovSubmitsTriangleCommand()
    {
        using Machine machine = CreateMachine(out _);
        CpuState state = machine.Cpu.State;
        state.LoadSegmentRealMode(SegReg.Cs, 0);
        state.LoadSegmentRealMode(SegReg.Ds, 0);
        state.Segments[(int)SegReg.Ds].Limit = uint.MaxValue;
        state.Eip = 0x1000;
        state.Halted = false;

        // 66 67 C7 /0: MOV dword [0120B500],83000008 using 32-bit operand
        // and address sizes from a 16-bit code segment, as in unreal mode.
        byte[] code = [0x66, 0x67, 0xC7, 0x05, 0x00, 0xB5, 0x20, 0x01,
            0x08, 0x00, 0x00, 0x83];
        code.CopyTo(machine.Memory.GetRamSpan(0x1000, code.Length));

        machine.Cpu.Step();

        Assert.Equal(0x100Cu, state.Eip);
        AssertLeftToRightTriangle(machine);
    }

    [Theory]
    [InlineData(0x08000000u)] // Textured triangle opcode.
    [InlineData(0x00020000u)] // Fog.
    [InlineData(0x000C0000u)] // Source alpha blending.
    public void UnsupportedTriangleDoesNotPretendToRender(uint unsupportedBits)
    {
        using Machine machine = CreateMachine(out S3VirgeEngine engine);
        var requests = new List<uint>();
        engine.UnsupportedCommandIssued += requests.Add;

        Write(machine, 0xB500, Command | unsupportedBits);

        Assert.Equal(new[] { Command | unsupportedBits }, requests);
        AssertPixels(machine);
    }

    [Fact]
    public void InterpolatedColoursAreReportedUntilTheirRoundingIsImplemented()
    {
        using Machine machine = CreateMachine(out S3VirgeEngine engine);
        int unsupported = 0;
        engine.UnsupportedCommandIssued += _ => unsupported++;
        Write(machine, 0xB540, 0x80);

        Write(machine, 0xB500, Command);

        Assert.Equal(1, unsupported);
        AssertPixels(machine);
    }

    [Fact]
    public void ByteWordAndMisalignedWritesDoNotAccidentallyTrigger()
    {
        using Machine machine = CreateMachine(out _);
        machine.Memory.WriteByte(MmioBase + 0xB500, 8);
        machine.Memory.WriteWord(MmioBase + 0xB502, 0x8300);
        machine.Memory.WriteDword(MmioBase + 0xB501, Command);

        AssertPixels(machine);
        Assert.Equal(0u, machine.Memory.ReadDword(MmioBase + 0xB500));
    }

    [Fact]
    public void TriangleWritesDirtyVramForTheExistingRenderer()
    {
        using Machine machine = CreateMachine(out _);
        machine.RunInstructions(200);
        machine.Cpu.State.Ax = 0x4F02;
        machine.Cpu.State.Bx = 0x112; // VBE 640x480 RGB888.
        machine.VideoServices.Handle(machine.Cpu);
        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        Write(machine, 0xB4D8, 0);
        Write(machine, 0xB4E4, 1920u << 16);
        machine.Display.Render(force: true);

        Write(machine, 0xB500, Command);

        Assert.True(machine.Display.Render());
        Assert.Equal(0xFF2040E0u, machine.Display.Framebuffer[4 * 640 + 1]);
        Assert.Equal(0xFF000000u, machine.Display.Framebuffer[4 * 640 + 5]);
    }

    [Fact]
    public void OutOfRangeDestinationCannotWritePastHostVideoMemory()
    {
        using Machine machine = CreateMachine(out _);
        uint lastPixel = VgaMemory.TotalSize - 3;
        machine.VideoMemory.WriteLinear(lastPixel, Sentinel);
        machine.VideoMemory.WriteLinear(lastPixel + 1, Sentinel);
        machine.VideoMemory.WriteLinear(lastPixel + 2, Sentinel);

        // With the last quadword as the base, each nonempty scan line lies
        // beyond VRAM. This pins a host safety guard, not silicon wrapping.
        Write(machine, 0xB4D8, VgaMemory.TotalSize - 8);
        Write(machine, 0xB500, Command);

        AssertPixels(machine);
        Assert.Equal(Sentinel, machine.VideoMemory.ReadLinear(lastPixel));
        Assert.Equal(Sentinel, machine.VideoMemory.ReadLinear(lastPixel + 1));
        Assert.Equal(Sentinel, machine.VideoMemory.ReadLinear(lastPixel + 2));
    }

    [Fact]
    public void DefaultTrioDoesNotAttachTheViRgeEngine()
    {
        using var machine = new Machine();
        machine.Memory.WriteDword(machine.LinearFrameBufferBase + 0x0100B500, Command);

        Assert.Equal(0x8811u, machine.Pci.ReadConfiguration(0x0020, 2, 2));
        Assert.Equal(0, machine.VideoMemory.ReadLinear(TargetBase + 4 * Stride + 3));
    }
}
