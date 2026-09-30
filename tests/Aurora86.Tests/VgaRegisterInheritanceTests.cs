using Aurora86.Core.Devices.Video;
using Aurora86.Core.IO;

namespace Aurora86.Tests;

/// <summary>
/// Verifies the 1987 VGA register boundary and the S3 extensions used by
/// mid-nineties display drivers, including the historical snapshot offsets.
/// All driver-facing register programming goes through indexed I/O ports.
/// </summary>
public class VgaRegisterInheritanceTests
{
    private const ushort CrtcIndexPort = 0x3D4;
    private const ushort SequencerIndexPort = 0x3C4;
    private const ushort GraphicsIndexPort = 0x3CE;
    private const int RegisterSnapshotLength = 1136;
    private const int SnapshotTailOffset = 1104;
    private const int SequencerSnapshotOffset = 256;

    [Fact]
    public void PlainVgaDoesNotDecodeS3CrtcIdentityOrUnlockRegisters()
    {
        var registers = new VgaRegisters();
        var io = Attach(registers);

        WriteIndexed(io, CrtcIndexPort, 0x38, 0x48);
        WriteIndexed(io, CrtcIndexPort, 0x39, 0xA0);
        WriteIndexed(io, CrtcIndexPort, 0x45, 0x01);

        foreach (byte index in new byte[] { 0x2D, 0x2E, 0x2F, 0x30, 0x38, 0x39, 0x45 })
        {
            Assert.Equal(0xFF, ReadIndexed(io, CrtcIndexPort, index));
            Assert.Equal(0, registers.CaptureRegisters()[index]);
        }

        Assert.False(registers.MemoryMappedEngine);
        Assert.False(registers.HasExtendedRowOffset);
    }

    [Fact]
    public void PlainVgaDoesNotDecodeSr08OrParadiseGc0F()
    {
        var registers = new VgaRegisters();
        var io = Attach(registers);

        WriteIndexed(io, SequencerIndexPort, 0x08, 0x06);
        WriteIndexed(io, SequencerIndexPort, 0x12, 0xAB);
        WriteIndexed(io, GraphicsIndexPort, 0x0F, 0xFD);

        Assert.Equal(0xFF, ReadIndexed(io, SequencerIndexPort, 0x08));
        Assert.Equal(0xFF, ReadIndexed(io, SequencerIndexPort, 0x12));
        Assert.Equal(0xFF, ReadIndexed(io, GraphicsIndexPort, 0x0F));
        Assert.Equal(0, registers.CaptureRegisters()[SequencerSnapshotOffset + 0x08]);
    }

    [Theory]
    [InlineData(false, 0x88, 0x11)]
    [InlineData(true, 0x56, 0x31)]
    public void S3Crtc2D2EAndSr08DispatchThroughVgaBase(bool virge, int deviceHigh, int deviceLow)
    {
        VgaRegisters registers = new S3VgaRegisters(
            virge ? S3VideoIdentity.Virge325 : S3VideoIdentity.Trio64VPlus);
        var io = Attach(registers);

        Assert.Equal(deviceHigh, ReadIndexed(io, CrtcIndexPort, 0x2D));
        Assert.Equal(deviceLow, ReadIndexed(io, CrtcIndexPort, 0x2E));
        Assert.Equal(0xE1, ReadIndexed(io, CrtcIndexPort, 0x30));
        WriteIndexed(io, SequencerIndexPort, 0x12, 0xAB);
        Assert.Equal(0, ReadIndexed(io, SequencerIndexPort, 0x12));
        WriteIndexed(io, SequencerIndexPort, 0x08, 0x06);
        WriteIndexed(io, SequencerIndexPort, 0x12, 0xAB);
        Assert.Equal(0x06, ReadIndexed(io, SequencerIndexPort, 0x08));
        Assert.Equal(0xAB, ReadIndexed(io, SequencerIndexPort, 0x12));
        WriteIndexed(io, GraphicsIndexPort, 0x0F, 0xFD);
        Assert.Equal(0x05, ReadIndexed(io, GraphicsIndexPort, 0x0F));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void S3SnapshotPreserves1136ByteLayoutCr45ColourStacksAndLockTail(bool virge)
    {
        var identity = virge ? S3VideoIdentity.Virge325 : S3VideoIdentity.Trio64VPlus;
        var registers = new S3VgaRegisters(identity);
        var io = Attach(registers);
        WriteIndexed(io, CrtcIndexPort, 0x38, 0x48);
        WriteIndexed(io, CrtcIndexPort, 0x39, 0xA0);
        WriteIndexed(io, SequencerIndexPort, 0x08, 0x06);
        WriteIndexed(io, CrtcIndexPort, 0x35, 0x02);
        ReadIndexed(io, CrtcIndexPort, 0x45);
        WriteIndexed(io, CrtcIndexPort, 0x4A, 0x11);
        WriteIndexed(io, CrtcIndexPort, 0x4A, 0x22);
        WriteIndexed(io, CrtcIndexPort, 0x4A, 0x33);
        ReadIndexed(io, CrtcIndexPort, 0x45);
        WriteIndexed(io, CrtcIndexPort, 0x4B, 0x44);
        WriteIndexed(io, CrtcIndexPort, 0x4B, 0x55);
        WriteIndexed(io, CrtcIndexPort, 0x4B, 0x66);

        byte[] snapshot = registers.CaptureRegisters();
        Assert.Equal(RegisterSnapshotLength, snapshot.Length);
        Assert.Equal(0x02, snapshot[0x35]);
        Assert.Equal(0x06, snapshot[SequencerSnapshotOffset + 0x08]);
        Assert.Equal(new byte[] { 1, 1, 1 }, snapshot[(SnapshotTailOffset + 3)..(SnapshotTailOffset + 6)]);
        Assert.Equal(new byte[] { 0xC3, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 3 },
            snapshot[(SnapshotTailOffset + 17)..(SnapshotTailOffset + 25)]);

        var restored = new S3VgaRegisters(identity);
        restored.RestoreRegisters(snapshot);
        Assert.Equal(snapshot, restored.CaptureRegisters());
        Assert.Equal((0x332211u, 0x665544u), restored.CursorColours);
        var restoredIo = Attach(restored);
        WriteIndexed(restoredIo, CrtcIndexPort, 0x35, 0x03);
        WriteIndexed(restoredIo, SequencerIndexPort, 0x12, 0xAB);
        Assert.Equal(0x03, ReadIndexed(restoredIo, CrtcIndexPort, 0x35));
        Assert.Equal(0xAB, ReadIndexed(restoredIo, SequencerIndexPort, 0x12));
    }

    [Fact]
    public void LegacySnapshotWithoutCr45MarkerRestoresCr4AAndCr4BLowBytes()
    {
        byte[] snapshot = new VgaRegisters().CaptureRegisters();
        snapshot[0x4A] = 0x27;
        snapshot[0x4B] = 0x51;
        Assert.Equal(0, snapshot[SnapshotTailOffset + 17]);

        var restored = new S3VgaRegisters();
        restored.RestoreRegisters(snapshot);

        Assert.Equal((0x27u, 0x51u), restored.CursorColours);
        byte[] upgraded = restored.CaptureRegisters();
        Assert.Equal(0xC3, upgraded[SnapshotTailOffset + 17]);
        Assert.Equal(0, upgraded[SnapshotTailOffset + 24]);
    }

    [Fact]
    public void PlainVgaStandardCr0C0DAndSr02RemainAvailableThroughPorts()
    {
        var registers = new VgaRegisters();
        var io = Attach(registers);
        WriteIndexed(io, CrtcIndexPort, 0x0C, 0x12);
        WriteIndexed(io, CrtcIndexPort, 0x0D, 0x34);
        WriteIndexed(io, SequencerIndexPort, 0x02, 0x05);

        Assert.Equal(0x1234u, registers.ExtendedStartAddress);
        Assert.Equal(0x05, registers.MapMask);
        Assert.Equal(0x12, ReadIndexed(io, CrtcIndexPort, 0x0C));
        Assert.Equal(0x34, ReadIndexed(io, CrtcIndexPort, 0x0D));
    }

    [Fact]
    public void GenericVgaRendererLeavesCr45CursorCompositionToS3Renderer()
    {
        var registers = new S3VgaRegisters { VideoMode = 0x13 };
        var memory = new VgaMemory(registers);
        var io = Attach(registers);
        registers.SetPaletteEntry(1, 0x3F, 0x3F, 0x3F);
        registers.SetPaletteEntry(2, 0x3F, 0, 0);
        memory.WriteLinear(0, 1);
        WriteIndexed(io, CrtcIndexPort, 0x38, 0x48);
        WriteIndexed(io, CrtcIndexPort, 0x39, 0xA0);
        // CR4C/CR4D select the zero-filled 1024-byte cursor pattern at 1 MB.
        WriteIndexed(io, CrtcIndexPort, 0x4C, 0x04);
        WriteIndexed(io, CrtcIndexPort, 0x4D, 0x00);
        ReadIndexed(io, CrtcIndexPort, 0x45);
        WriteIndexed(io, CrtcIndexPort, 0x4B, 2);
        WriteIndexed(io, CrtcIndexPort, 0x4B, 0);
        WriteIndexed(io, CrtcIndexPort, 0x4B, 0);
        WriteIndexed(io, CrtcIndexPort, 0x45, 1);

        var generic = new VgaRenderer(memory, registers);
        generic.Render(force: true);
        Assert.Equal(0xFFFFFFFFu, generic.Framebuffer[0]);

        VgaRenderer s3 = new S3VgaRenderer(memory, registers);
        s3.Render(force: true);
        Assert.Equal(0xFF0000FFu, s3.Framebuffer[0]);
    }

    private static IoBus Attach(VgaRegisters registers)
    {
        var io = new IoBus();
        registers.Register(io);
        return io;
    }

    private static void WriteIndexed(IoBus io, ushort port, byte index, byte value)
    {
        io.WriteWord(port, (ushort)(index | (value << 8)));
    }

    private static byte ReadIndexed(IoBus io, ushort port, byte index)
    {
        io.WriteByte(port, index);
        return io.ReadByte((ushort)(port + 1));
    }
}
