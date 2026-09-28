using System.Buffers.Binary;
using Aurora386.Core;
using Aurora386.Core.Configuration;
using Aurora386.Core.Cpu;
using Aurora386.Core.Devices.Video;

namespace Aurora386.Tests;

/// <summary>
/// Drives the 1996 ViRGE primary Streams page controls through CPU MMIO and
/// CRTC OUT instructions, including page flips and machine snapshot restore.
/// </summary>
public sealed class S3VirgeStreamsIntegrationTests
{
    private const uint Mmio = 0x75000000;
    private const uint SecondPage = 0x96000;
    private const uint VbePage = 0x12C000;
    private const uint Red = 0xFF0000FF;
    private const uint Green = 0xFF00FF00;
    private const uint Blue = 0xFFFF0000;

    [Fact]
    public void Cpu81C0And81CCPageFlipRedrawWithoutVramWritesAndCR67ReturnsToVga()
    {
        using Machine machine = Started();
        WritePixel(machine, 0, 0x7C00);
        WritePixel(machine, SecondPage, 0x001F);
        WritePixel(machine, VbePage, 0x03E0);
        machine.VideoRegisters.VesaDisplayStart = (int)VbePage;
        ProgramPrimary(machine, SecondPage);
        SetStreamsMode(machine, 0x3C);
        machine.Display.Render(true);
        Assert.Equal(Blue, machine.Display.Framebuffer[0]);

        byte[] pixels = machine.VideoMemory.CaptureMemory();
        CpuStores(machine, (0x81C0, 0));
        Assert.True(machine.Display.Render());
        Assert.Equal(Red, machine.Display.Framebuffer[0]);
        CpuStores(machine, (0x81C4, SecondPage), (0x81CC, 1));
        Assert.True(machine.Display.Render());
        Assert.Equal(Blue, machine.Display.Framebuffer[0]);
        Assert.Equal(pixels, machine.VideoMemory.CaptureMemory());

        SetStreamsMode(machine, 0x30);
        Assert.True(machine.Display.Render());
        Assert.Equal(Green, machine.Display.Framebuffer[0]);
    }

    [Fact]
    public void FileSnapshotPreserves81C481CCSelectedPageAnd81C8StrideWithoutDrawing()
    {
        using Machine source = Started();
        ProgramPrimary(source, 0);
        // Deliberately differ from the VBE mode's 1280-byte pitch.
        CpuStores(source, (0x81C8, 1296), (0x81C4, SecondPage), (0x81CC, 1));
        WritePixel(source, SecondPage, 0x001F);
        WritePixel(source, SecondPage + 1296, 0x03E0);
        SetStreamsMode(source, 0x3C);
        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(source).Save(path);
            byte[] pixels = source.VideoMemory.CaptureMemory();
            using Machine target = Started();
            MachineSnapshot.Load(path).RestoreTo(target);
            Assert.Equal(pixels, target.VideoMemory.CaptureMemory());
            Assert.Equal(SecondPage, target.Memory.ReadDword(Mmio + 0x81C4));
            Assert.Equal(1u, target.Memory.ReadDword(Mmio + 0x81CC));
            Assert.Equal(1296u, target.Memory.ReadDword(Mmio + 0x81C8));
            target.Display.Render(true);
            Assert.Equal(Blue, target.Display.Framebuffer[0]);
            Assert.Equal(Green, target.Display.Framebuffer[640]);
            Assert.Equal(pixels, target.VideoMemory.CaptureMemory());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PciCommandMemoryBitAndDwordWidthGate81C0Writes()
    {
        using Machine machine = Started();
        CpuStores(machine, (0x81C0, SecondPage));
        machine.Memory.WriteByte(Mmio + 0x81C0, 0);
        machine.Memory.WriteWord(Mmio + 0x81C0, 0);
        machine.Memory.WriteDword(Mmio + 0x81C1, 0);
        Assert.Equal(SecondPage, machine.Memory.ReadDword(Mmio + 0x81C0));

        machine.Pci.WriteConfiguration(0x20, 4, 2, 1);
        CpuStores(machine, (0x81C0, 0));
        Assert.Equal(uint.MaxValue, machine.Memory.ReadDword(Mmio + 0x81C0));
        machine.Pci.WriteConfiguration(0x20, 4, 2, 3);
        Assert.Equal(SecondPage, machine.Memory.ReadDword(Mmio + 0x81C0));
    }

    private static void ProgramPrimary(Machine machine, uint page)
    {
        CpuStores(machine,
            (0x8180, 0x03000000), (0x81C0, page), (0x81C8, 1280),
            (0x81CC, 0), (0x81F0, 0x00010001), (0x81F4, 0x027F01E0));
    }

    private static void SetStreamsMode(Machine machine, byte value)
    {
        // MOV DX,3D4; MOV AX,value:67; OUT DX,AX.
        Run(machine, [0xBA, 0xD4, 0x03, 0xB8, 0x67, value, 0xEF]);
    }

    private static void WritePixel(Machine machine, uint address, ushort pixel)
    {
        machine.VideoMemory.WriteLinear(address, (byte)pixel);
        machine.VideoMemory.WriteLinear(address + 1, (byte)(pixel >> 8));
    }

    private static Machine Started()
    {
        var machine = new Machine(videoModel: VmVideoModel.S3Virge325);
        machine.RunInstructions(200);
        machine.Pci.WriteConfiguration(0x20, 0x10, 4, 0x74000000);
        machine.Cpu.State.Ax = 0x4F02;
        machine.Cpu.State.Bx = 0x0110;
        Run(machine, [0xCD, 0x10]);
        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        return machine;
    }

    private static void CpuStores(Machine machine, params (uint Register, uint Value)[] writes)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach ((uint register, uint value) in writes)
        {
            writer.Write(new byte[] { 0x66, 0x67, 0xC7, 0x05 });
            writer.Write(Mmio + register);
            writer.Write(value);
        }
        Run(machine, stream.ToArray());
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
