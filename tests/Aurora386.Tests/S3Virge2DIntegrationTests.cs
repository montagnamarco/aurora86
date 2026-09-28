using System.Buffers.Binary;
using Aurora386.Core;
using Aurora386.Core.Configuration;
using Aurora386.Core.Cpu;
using Aurora386.Core.Devices.Video;

namespace Aurora386.Tests;

/// <summary>
/// Drives the 1996 ViRGE BitBLT ports through real CPU stores, like the DOS
/// S3d client, and resumes an interrupted image upload through MachineSnapshot.
/// These vectors cover the physical card decoder and displayed pixel format.
/// </summary>
public sealed class S3Virge2DIntegrationTests
{
    private const uint Mmio = 0x75000000;
    private const uint ImageCommand = 0x079808A6;

    [Fact]
    public void CpuA500PatcopyAndSrccopyProduceRgb1555PixelsAtRelocatedBar()
    {
        using Machine machine = Started();
        CpuStores(machine,
            (0xA4D8, 0x100000),
            (0xA4F4, 0x7C00),
            (0xA4E4, 8u << 16),
            (0xA504, 1u << 16 | 1), // Two pixels, one row.
            (0xA50C, 0),
            (0xA500, 0x17E00124));
        Assert.Equal(0x7C, machine.VideoMemory.ReadLinear(0x100001));
        CpuStores(machine,
            (0xA4D4, 0x100000),
            (0xA4D8, 0),
            (0xA4E4, 1280u << 16 | 8),
            (0xA508, 0),
            (0xA50C, 1), // Destination row one.
            (0xA500, 0x07980224));
        machine.Display.Render(true);
        Assert.Equal(640, machine.Display.Width);
        Assert.Equal(0xFF0000FFu, machine.Display.Framebuffer[640]);
        Assert.Equal(0xFF0000FFu, machine.Display.Framebuffer[641]);
        Assert.Equal(0xFF000000u, machine.Display.Framebuffer[642]);
        Assert.Equal(uint.MaxValue, machine.Memory.ReadDword(0x7100A500));
    }

    [Fact]
    public void CpuImageDwordsResumeAfterFileSnapshotWithoutReplayingFirstPixels()
    {
        using Machine source = Started();
        BeginImage(source);
        CpuStores(source, (0, 0x03E07C00)); // Red, green; row still needs blue.
        Assert.True(source.Virge!.Blitter.IsBusy);
        Assert.Equal(0u, source.Memory.ReadDword(Mmio + 0x8504) & 0x2000);
        byte[] before = source.VideoMemory.CaptureMemory();
        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(source).Save(path);
            using Machine target = Started();
            MachineSnapshot.Load(path).RestoreTo(target);
            Assert.Equal(before, target.VideoMemory.CaptureMemory());
            Assert.True(target.Virge!.Blitter.IsBusy);
            Assert.Equal(0u, target.Memory.ReadDword(Mmio + 0x8504) & 0x2000);
            // Odd row widths discard the upper padding word at each row end.
            (uint, uint)[] remainder = [(4, 0xFFFF001F), (8, 0x03E07C00), (12, 0xFFFF001F)];
            CpuStores(source, remainder);
            CpuStores(target, remainder);
            Assert.Equal(source.VideoMemory.CaptureMemory(), target.VideoMemory.CaptureMemory());
            Assert.False(target.Virge.Blitter.IsBusy);
            Assert.Equal(0x2000u, target.Memory.ReadDword(Mmio + 0x8504) & 0x2000);
            target.Display.Render(true);
            foreach (int row in new[] { 0, 1 })
            {
                Assert.Equal(0xFF0000FFu, target.Display.Framebuffer[row * 640]);
                Assert.Equal(0xFF00FF00u, target.Display.Framebuffer[row * 640 + 1]);
                Assert.Equal(0xFFFF0000u, target.Display.Framebuffer[row * 640 + 2]);
                Assert.Equal(0xFF000000u, target.Display.Framebuffer[row * 640 + 3]);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PciMemoryGateAndByteWritesDoNotConsumePendingImageDword()
    {
        using Machine machine = Started();
        BeginImage(machine);
        byte[] pending = machine.Virge!.Blitter.Capture();
        machine.Memory.WriteByte(Mmio, 0x77);
        machine.Memory.WriteWord(Mmio, 0x7777);
        machine.Memory.WriteDword(Mmio + 1, 0x77777777);
        Assert.Equal(pending, machine.Virge.Blitter.Capture());
        machine.Pci.WriteConfiguration(0x20, 4, 2, 1);
        CpuStores(machine, (0, 0x7C007C00));
        Assert.Equal(pending, machine.Virge.Blitter.Capture());
        machine.Pci.WriteConfiguration(0x20, 4, 2, 3);
        CpuStores(machine, (0, 0x7C007C00));
        Assert.Equal(0x7C, machine.VideoMemory.ReadLinear(1));
        Assert.NotEqual(pending, machine.Virge.Blitter.Capture());
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void LegacyVrg1MachineSnapshotResavesUnchangedAndResetsBlitterAnd81C0(
        bool bundled, ushort triangleVersion)
    {
        using Machine source = Started();
        MachineSnapshot legacy = MachineSnapshot.Capture(source);
        byte[] legacyTriangle = source.Virge!.Engine.Capture();
        if (triangleVersion == 1)
        {
            Array.Resize(ref legacyTriangle, 84);
            BinaryPrimitives.WriteUInt16LittleEndian(legacyTriangle.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(legacyTriangle.AsSpan(6), 19);
        }
        byte[] legacyPayload = legacyTriangle;
        if (bundled)
        {
            // VG-03C2 files wrapped a v1 triangle and a BitBLT block in VGE1.
            byte[] blitter = source.Virge.Blitter.Capture();
            legacyPayload = new byte[16 + legacyTriangle.Length + blitter.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(legacyPayload, 0x31454756);
            BinaryPrimitives.WriteUInt16LittleEndian(legacyPayload.AsSpan(4), 1);
            BinaryPrimitives.WriteInt32LittleEndian(legacyPayload.AsSpan(8), legacyTriangle.Length);
            BinaryPrimitives.WriteInt32LittleEndian(legacyPayload.AsSpan(12), blitter.Length);
            legacyTriangle.CopyTo(legacyPayload, 16);
            blitter.CopyTo(legacyPayload, 16 + legacyTriangle.Length);
        }
        SnapshotSectionFile.SetPayload(legacy, "VENG", legacyPayload);
        string original = Path.GetTempFileName();
        string resaved = Path.GetTempFileName();
        try
        {
            legacy.Save(original);
            MachineSnapshot loaded = MachineSnapshot.Load(original);
            loaded.Save(resaved);
            Assert.Equal(File.ReadAllBytes(original), File.ReadAllBytes(resaved));
            using Machine target = Started();
            BeginImage(target);
            CpuStores(target, (0x81C0, 0x96000), (0x81CC, 1));
            Assert.True(target.Virge!.Blitter.IsBusy);
            loaded.RestoreTo(target);
            Assert.False(target.Virge.Blitter.IsBusy);
            Assert.Equal(0u, target.Virge.Blitter.ReadDword(0xA500));
            Assert.Equal(0u, target.Memory.ReadDword(Mmio + 0x81C0));
            Assert.Equal(0u, target.Memory.ReadDword(Mmio + 0x81CC));
            Assert.Equal(source.Virge.Engine.Capture(), target.Virge.Engine.Capture());
            Assert.Equal(source.VideoMemory.CaptureMemory(), target.VideoMemory.CaptureMemory());
        }
        finally
        {
            File.Delete(original);
            File.Delete(resaved);
        }
    }

    [Theory]
    [InlineData(0)] // Envelope magic.
    [InlineData(4)] // Envelope version.
    [InlineData(6)] // Reserved envelope bits.
    [InlineData(8)] // Triangle length.
    [InlineData(12)] // Blitter length.
    [InlineData(16)] // Streams length.
    [InlineData(20)] // Triangle header.
    [InlineData(124)] // Blitter header, after 20-byte envelope and 104-byte VRG1 v2.
    [InlineData(236)] // Streams header, after the 112-byte V2D1 block.
    [InlineData(244)] // Reserved low bits in the primary-control latch.
    public void InvalidEngineBundlePreflightLeavesPendingUploadAndRamUntouched(int corruptOffset)
    {
        using Machine source = Started();
        MachineSnapshot snapshot = MachineSnapshot.Capture(source);
        byte[] payload = SnapshotSectionFile.GetPayload(snapshot, "VENG");
        Assert.Equal(0x31454756u, BinaryPrimitives.ReadUInt32LittleEndian(payload));
        Assert.Equal(104, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8)));
        payload[corruptOffset] ^= 0xFF;
        using Machine target = Started();
        BeginImage(target);
        CpuStores(target, (0x81C0, 0x96000), (0x81CC, 1));
        target.Memory.WriteByte(0x1234, 0x66);
        byte[] engines = target.Virge!.CaptureEngines();
        byte[] pixels = target.VideoMemory.CaptureMemory();
        Assert.Throws<InvalidDataException>(() => snapshot.RestoreTo(target));
        Assert.Equal(0x66, target.Memory.ReadByte(0x1234));
        Assert.Equal(engines, target.Virge.CaptureEngines());
        Assert.Equal(pixels, target.VideoMemory.CaptureMemory());
    }

    private static void BeginImage(Machine machine)
    {
        CpuStores(machine,
            (0xA4D8, 0), (0xA4DC, 639), (0xA4E0, 479),
            (0xA4E4, 1280u << 16), (0xA504, 2u << 16 | 2),
            (0xA50C, 0), (0xA500, ImageCommand));
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
