using Aurora386.Core;
using Aurora386.Core.Configuration;
using Aurora386.Core.Cpu;
using Aurora386.Core.Devices.Video;

namespace Aurora386.Tests;

/// <summary>
/// Calls the Aurora386 BIOS as a 1996 ViRGE DOS client would: real INT 10h
/// instructions program mode 0110h and its CR6A window before CPU pixel
/// writes, scanout, and a return to the standard VGA text mode.
/// </summary>
public sealed class VirgeVesaTests
{
    [Fact]
    public void Function4F00ListsOnlyMode0110AndDoesNotAdvertiseDac8()
    {
        using Machine machine = Started();
        Int10(machine, 0x4F00);
        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        Assert.Equal(0x41534556u, machine.Memory.ReadDword(0x50000));
        Assert.Equal(0u, machine.Memory.ReadDword(0x5000A));
        uint pointer = machine.Memory.ReadDword(0x5000E);
        uint modeList = (pointer >> 16) * 16 + (pointer & 0xFFFF);
        Assert.Equal(0x0110, machine.Memory.ReadWord(modeList));
        Assert.Equal(0xFFFF, machine.Memory.ReadWord(modeList + 2));
        Assert.Equal(32, machine.Memory.ReadWord(0x50012));
    }

    [Fact]
    public void Function4F01ReportsRgb1555PitchBankAndRelocatedBar()
    {
        using Machine machine = Started();
        machine.Pci.WriteConfiguration(0x20, 0x10, 4, 0x74000000);
        Int10(machine, 0x4F01, cx: 0x0110);
        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        Assert.Equal(0x009B, machine.Memory.ReadWord(0x50000));
        Assert.Equal(7, machine.Memory.ReadByte(0x50002));
        Assert.Equal(64, machine.Memory.ReadWord(0x50004));
        Assert.Equal(0xA000, machine.Memory.ReadWord(0x50008));
        Assert.Equal(1280, machine.Memory.ReadWord(0x50010));
        Assert.Equal(640, machine.Memory.ReadWord(0x50012));
        Assert.Equal(480, machine.Memory.ReadWord(0x50014));
        Assert.Equal(15, machine.Memory.ReadByte(0x50019));
        Assert.Equal(6, machine.Memory.ReadByte(0x5001B));
        Assert.Equal(5, machine.Memory.ReadByte(0x5001F));
        Assert.Equal(10, machine.Memory.ReadByte(0x50020));
        Assert.Equal(5, machine.Memory.ReadByte(0x50021));
        Assert.Equal(5, machine.Memory.ReadByte(0x50022));
        Assert.Equal(0x74000000u, machine.Memory.ReadDword(0x50028));
    }

    [Theory]
    [InlineData(0x0110)]
    [InlineData(0x4110)]
    public void Function4F02ProgramsCr31Cr58Cr66And1280BytePitch(int requested)
    {
        using Machine machine = Started();
        var portWrites = new List<ushort>();
        machine.Io.PortAccessed += (port, write, value) =>
        {
            if (write)
            {
                portWrites.Add(port);
            }
        };

        Int10(machine, 0x4F02, (ushort)requested);

        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        Assert.Contains((ushort)0x3D5, portWrites);
        Assert.Contains((ushort)0x3C5, portWrites);
        Assert.Equal(9, machine.VideoRegisters.Crtc(0x31) & 9);
        Assert.Equal(1, machine.VideoRegisters.Crtc(0x66) & 1);
        Assert.Equal(requested == 0x4110 ? 0x12 : 0, machine.VideoRegisters.Crtc(0x58) & 0x13);
        Assert.Equal(1280, machine.VideoRegisters.ExtendedRowOffset);
        Assert.Equal(0x40, machine.VideoRegisters.Crtc(0x13));
        Assert.Equal(0x10, machine.VideoRegisters.Crtc(0x51) & 0x30);
        Assert.Equal(0x30, machine.VideoRegisters.Crtc(0x67) & 0xFC);
        Assert.Equal(0x4F, machine.VideoRegisters.Crtc(0x01));
        Assert.Equal(0xDF, machine.VideoRegisters.Crtc(0x12));
        Assert.Equal(0x02, machine.VideoRegisters.Crtc(0x07) & 0x42);
        Assert.Equal(0, machine.VideoRegisters.Crtc(0x6A));
        Assert.Equal(0u, machine.VideoRegisters.ExtendedStartAddress);
        Int10(machine, 0x4F03);
        Assert.Equal(requested, machine.Cpu.State.Bx);
    }

    [Fact]
    public void Mode0110PreservesCallerDirectionFlagWhileProgrammingTheWholeTable()
    {
        using Machine machine = Started();
        machine.Cpu.State.OtherFlags |= Flags.Direction;
        Int10(machine, 0x4F02, 0x0110);
        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        Assert.True((machine.Cpu.State.OtherFlags & Flags.Direction) != 0);
        Assert.Equal(1280, machine.VideoRegisters.ExtendedRowOffset);
        Assert.Equal(0x30, machine.VideoRegisters.Crtc(0x67) & 0xF0);
    }

    [Fact]
    public void Cr31DoublewordPitchUsesCr51BeforeCr43AndIgnoresChain4()
    {
        using Machine machine = Started();
        Int10(machine, 0x4F02, 0x0110);
        machine.Io.WriteWord(0x3D4, 0x0051);
        machine.Io.WriteWord(0x3D4, 0x0443);
        Assert.Equal(1280, machine.VideoRegisters.ExtendedRowOffset);
        machine.Io.WriteWord(0x3D4, 0x2051);
        Assert.Equal(2304, machine.VideoRegisters.ExtendedRowOffset);
        machine.Io.WriteWord(0x3C4, 0x0804);
        Assert.Equal(2304, machine.VideoRegisters.ExtendedRowOffset);
    }

    [Fact]
    public void Function4F05WritesCr6AAndCpuPixelAppearsOnTheSelectedScanline()
    {
        using Machine machine = Started();
        Int10(machine, 0x4F02, 0x0110);
        Int10(machine, 0x4F05, dx: 1);
        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        Assert.Equal(1, machine.VideoRegisters.Crtc(0x6A));

        // Scanline 52 begins at 10400h in VRAM, 400h within bank one.
        CpuWriteWord(machine, 0xA0400, 0x7C00);

        Assert.Equal(0x7C, machine.VideoMemory.ReadLinear(0x10401));
        machine.Display.Render(true);
        Assert.Equal(640, machine.Display.Width);
        Assert.Equal(480, machine.Display.Height);
        Assert.Equal(0xFF0000FFu, machine.Display.Framebuffer[52 * 640]);
        Assert.Equal(0xFF000000u, machine.Display.Framebuffer[52 * 640 + 1]);
        Int10(machine, 0x4F05, bx: 0x0100);
        Assert.Equal(1, machine.Cpu.State.Dx);
    }

    [Fact]
    public void LinearModePixelUsesRelocatedBarAndLeavesA0000UnderlyingRam()
    {
        using Machine machine = Started();
        machine.Memory.WriteByte(0xA0000, 0x52);
        machine.Pci.WriteConfiguration(0x20, 0x10, 4, 0x74000000);
        Int10(machine, 0x4F02, 0x4110);
        CpuWriteWord(machine, 0x74000000 + 1280, 0x03E0);
        machine.Display.Render(true);
        Assert.Equal(0xFF00FF00u, machine.Display.Framebuffer[640]);
        Assert.Equal(0x52, machine.Memory.ReadByte(0xA0000));
        Assert.Equal(0xFFFFFFFFu, machine.Memory.ReadDword(0x70000000));
    }

    [Fact]
    public void BankZeroClearsLegacyCr35Cr51OffsetsWithoutChangingPitch()
    {
        using Machine machine = Started();
        Int10(machine, 0x4F02, 0x0110);
        machine.Io.WriteWord(0x3D4, 0x0335);
        machine.Io.WriteWord(0x3D4, 0x1451);
        Int10(machine, 0x4F05, dx: 0);
        CpuWriteWord(machine, 0xA0000, 0x7C00);
        Assert.Equal(0x7C, machine.VideoMemory.ReadLinear(1));
        Assert.Equal(1280, machine.VideoRegisters.ExtendedRowOffset);
        Int10(machine, 0x4F05, bx: 0x0100);
        Assert.Equal(0, machine.Cpu.State.Dx);
    }

    [Fact]
    public void Mode0110StartsOnBankZeroAfterDirectLegacyBankProgramming()
    {
        using Machine machine = Started();
        machine.Io.WriteWord(0x3D4, 0x4838);
        machine.Io.WriteWord(0x3D4, 0xA039);
        machine.Io.WriteWord(0x3D4, 0x0335);
        Int10(machine, 0x4F02, 0x0110);
        CpuWriteWord(machine, 0xA0000, 0x001F);
        Assert.Equal(0x1F, machine.VideoMemory.ReadLinear(0));
        Assert.Equal(0, machine.VideoRegisters.Crtc(0x35) & 0x0F);
    }

    [Fact]
    public void ModeBit15PreservesPixelsAndAnOrdinarySetClearsThem()
    {
        using Machine machine = Started();
        machine.VideoMemory.WriteLinear(0x1234, 0x6B);
        machine.VideoMemory.WriteLinear(614399, 0x4A);
        Int10(machine, 0x4F02, 0x8110);
        Assert.Equal(0x6B, machine.VideoMemory.ReadLinear(0x1234));
        Assert.Equal(0x4A, machine.VideoMemory.ReadLinear(614399));
        Int10(machine, 0x4F02, 0x0110);
        Assert.Equal(0, machine.VideoMemory.ReadLinear(0x1234));
        Assert.Equal(0, machine.VideoMemory.ReadLinear(614399));
    }

    [Theory]
    [InlineData(0x4F02, 0x0112, 0)]
    [InlineData(0x4F02, 0x0101, 0)]
    [InlineData(0x4F02, 0x0004, 0)]
    [InlineData(0x4F05, 0x0001, 0)]
    [InlineData(0x4F05, 0x0000, 32)]
    [InlineData(0x4F06, 0x0000, 0)]
    public void UnsupportedRequestsLeaveVideoRegistersAndVramUnchanged(int ax, int bx, int dx)
    {
        using Machine machine = Started();
        Int10(machine, 0x4F02, 0x0110);
        byte[] registers = machine.VideoRegisters.CaptureRegisters();
        byte[] vram = machine.VideoMemory.CaptureMemory();
        Int10(machine, (ushort)ax, (ushort)bx, dx: (ushort)dx);
        Assert.Equal(0x014F, machine.Cpu.State.Ax);
        Assert.Equal(registers, machine.VideoRegisters.CaptureRegisters());
        Assert.Equal(vram, machine.VideoMemory.CaptureMemory());
    }

    [Theory]
    [InlineData(0x0003, 0)]
    [InlineData(0x4F02, 3)]
    public void ReturningToMode03RestoresB8000TextAndDisablesEnhancedMapping(int ax, int bx)
    {
        using Machine machine = Started();
        Int10(machine, 0x4F02, 0x4110);
        Int10(machine, (ushort)ax, (ushort)bx);
        Assert.Null(machine.VideoRegisters.VesaMode);
        Assert.Equal(0, machine.VideoRegisters.Crtc(0x31) & 8);
        Assert.Equal(0, machine.VideoRegisters.Crtc(0x58) & 0x10);
        Assert.Equal(0, machine.VideoRegisters.Crtc(0x66) & 1);
        Assert.Equal(3, machine.Memory.ReadByte(0x449));
        CpuWriteWord(machine, 0xB8000, 0x0742);
        Assert.Equal(0x42, machine.VideoMemory.ReadLinear(0x8000));
        machine.Display.Render(true);
        Assert.Equal(720, machine.Display.Width);
        Assert.Equal(400, machine.Display.Height);
    }

    [Fact]
    public void SnapshotRestoresMode0110AndCr6ABankWithoutReprogrammingPorts()
    {
        using Machine machine = Started();
        Int10(machine, 0x4F02, 0x0110);
        Int10(machine, 0x4F05, dx: 9);
        CpuWriteWord(machine, 0xA0400, 0x001F);
        MachineSnapshot snapshot = MachineSnapshot.Capture(machine);
        Int10(machine, 0x0003);
        snapshot.RestoreTo(machine);
        Assert.Equal(9, machine.VideoRegisters.Crtc(0x6A));
        Assert.Equal(0x1F, machine.Memory.ReadWord(0xA0400));
        machine.Display.Render(true);
        // Bank 9 plus 400h is byte 90400h, with two bytes per RGB1555 pixel.
        Assert.Equal(0xFFFF0000u, machine.Display.Framebuffer[0x90400 / 2]);
    }

    private static Machine Started()
    {
        var machine = new Machine(videoModel: VmVideoModel.S3Virge325);
        machine.RunInstructions(200);
        return machine;
    }

    private static void Int10(Machine machine, ushort ax, ushort bx = 0, ushort cx = 0, ushort dx = 0)
    {
        machine.Cpu.State.Ax = ax;
        machine.Cpu.State.Bx = bx;
        machine.Cpu.State.Cx = cx;
        machine.Cpu.State.Dx = dx;
        machine.Cpu.SetSegment(SegReg.Es, 0x5000);
        machine.Cpu.State.Di = 0;
        Run(machine, [0xCD, 0x10]);
    }

    private static void CpuWriteWord(Machine machine, uint address, ushort value)
    {
        Run(machine, [0x67, 0xC7, 0x05, (byte)address, (byte)(address >> 8),
            (byte)(address >> 16), (byte)(address >> 24), (byte)value, (byte)(value >> 8)]);
    }

    private static void Run(Machine machine, byte[] code)
    {
        code.CopyTo(machine.Memory.GetRamSpan(0x20100, code.Length));
        machine.Memory.WriteByte(0x20100 + (uint)code.Length, 0xF4);
        machine.Cpu.SetSegment(SegReg.Cs, 0x2000);
        machine.Cpu.State.Eip = 0x100;
        machine.Cpu.SetSegment(SegReg.Ds, 0);
        machine.Cpu.State.Segments[(int)SegReg.Ds].Limit = uint.MaxValue;
        machine.Cpu.SetSegment(SegReg.Ss, 0x3000);
        machine.Cpu.State.Esp = 0xFFFE;
        machine.Cpu.State.Halted = false;
        for (int count = 0; count < 50_000 && !machine.Cpu.State.Halted; count++)
        {
            machine.RunInstructions(1);
        }
        Assert.True(machine.Cpu.State.Halted);
        Assert.Equal(0x101u + (uint)code.Length, machine.Cpu.State.Eip);
    }
}
