using System.Buffers.Binary;
using Aurora386.Core;
using Aurora386.Core.Configuration;
using Aurora386.Core.Cpu;
using Aurora386.Core.Devices.Video;

namespace Aurora386.Tests;

/// <summary>
/// Submits the 1996 ViRGE Gouraud/Z command through real CPU MMIO stores,
/// preserving the pending latches and both surfaces across a machine snapshot.
/// </summary>
public sealed class S3VirgeGouraudIntegrationTests
{
    private const uint Mmio = 0x75000000;

    [Fact]
    public void CpuB500GouraudDepthLessResumesB554B558B55CAfterFileSnapshot()
    {
        using Machine source = Started();
        const uint colourBase = 0x96000;
        const uint depthBase = 0x12C000;
        source.VideoMemory.GetSpan(depthBase, 64).Fill(0xFF);
        CpuStores(source,
            (0xB4D4, depthBase), (0xB4D8, colourBase),
            (0xB4DC, 3), (0xB4E0, 3),
            (0xB4E4, 8u << 16), (0xB4E8, 8),
            (0xB54C, 0), (0xB550, 128u << 7),
            (0xB540, 8u << 7),
            (0xB554, 1u << 15), (0xB558, 2u << 15), (0xB55C, 100u << 15),
            (0xB574, 0), (0xB56C, 3u << 20),
            (0xB578, 1), (0xB57C, 0x80020000));
        string path = Path.GetTempFileName();
        try
        {
            byte[] before = source.VideoMemory.CaptureMemory();
            MachineSnapshot.Capture(source).Save(path);
            using Machine target = Started();
            MachineSnapshot.Load(path).RestoreTo(target);
            Assert.Equal(before, target.VideoMemory.CaptureMemory());
            CpuStores(source, (0xB500, 0x80C00006));
            CpuStores(target, (0xB500, 0x80C00006));
            Assert.Equal(source.VideoMemory.CaptureMemory(), target.VideoMemory.CaptureMemory());
            Assert.Equal(0x4000, Pixel(target, colourBase + 8));
            Assert.Equal(0x4400, Pixel(target, colourBase + 10));
            Assert.Equal(0x4800, Pixel(target, colourBase + 12));
            Assert.Equal(100, Pixel(target, depthBase + 8));
            Assert.Equal(101, Pixel(target, depthBase + 10));
            Assert.Equal(102, Pixel(target, depthBase));
            Assert.Equal(0, Pixel(target, colourBase + 14));
            Assert.Equal(0xFFFF, Pixel(target, depthBase + 14));

            // Equal and farther Z must retain the first triangle's colour.
            byte[] firstTriangle = target.VideoMemory.CaptureMemory();
            CpuStores(target, (0xB550, 255u << 7), (0xB500, 0x80C00006));
            Assert.Equal(firstTriangle, target.VideoMemory.CaptureMemory());
            CpuStores(target, (0xB55C, 200u << 15), (0xB500, 0x80C00006));
            Assert.Equal(firstTriangle, target.VideoMemory.CaptureMemory());
            CpuStores(target, (0xB55C, 50u << 15), (0xB500, 0x80C00006));
            Assert.Equal(0x7C00, Pixel(target, colourBase + 8));
            Assert.Equal(50, Pixel(target, depthBase + 8));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ushort Pixel(Machine machine, uint address)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(machine.VideoMemory.GetSpan(address, sizeof(ushort)));
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
