using Aurora86.Core.Devices.Audio;
using Aurora86.Core.IO;

namespace Aurora86.Tests;

/// <summary>
/// Exercises CT1745 index/data programming and DSP-owned IRQ/resource state
/// across stopped snapshots, as used by DOS-era Sound Blaster drivers.
/// </summary>
public sealed class SbMixerSnapshotTests
{
    private static SbMixer Create(out IoBus io)
    {
        io = new IoBus();
        var mixer = new SbMixer();
        mixer.Register(io);
        return mixer;
    }

    private static void Write(IoBus io, byte index, byte value)
    {
        io.WriteByte(0x224, index);
        io.WriteByte(0x225, value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ports224225ResumeSelectedVolumeWithoutReplayingAliases(bool dirtyTarget)
    {
        SbMixer source = Create(out IoBus io);
        SbMixer target = Create(out IoBus targetIo);
        Write(io, 0x22, 0xC3); // Pro master alias expands into 30h/31h.
        Write(io, 0x30, 0x18); // Raw stereo value now differs from the alias.
        Write(io, 0x26, 0x95);
        Write(io, 0x04, 0xB7);
        io.WriteByte(0x224, 0x35); // Pending FM-right data write.
        if (dirtyTarget)
        {
            Write(targetIo, 0x22, 0);
            Write(targetIo, 0x04, 0);
            target.InterruptStatus = 3;
        }
        byte[] saved = source.Capture();
        target.Restore(saved);
        Assert.Equal(saved, target.Capture());
        Assert.Equal(0x35, targetIo.ReadByte(0x224));
        Assert.Equal(0xC3, target.GetRegister(0x22));
        Assert.Equal(0x18, target.GetRegister(0x30));
        Assert.Equal(source.MasterLevel, target.MasterLevel);
        Assert.Equal(source.VoiceLevel, target.VoiceLevel);
        Assert.Equal(source.FmLevel, target.FmLevel);
        io.WriteByte(0x225, 0xF8);
        targetIo.WriteByte(0x225, 0xF8);
        Assert.Equal(0xF8, target.GetRegister(0x35));
        Assert.Equal(source.Capture(), target.Capture());
        target.Restore(saved);
        Assert.Equal(saved, target.Capture()); // Captured bytes survive later writes.
    }

    [Fact]
    public void Registers808182PreserveOwnerResourcesAndPendingInterrupts()
    {
        SbMixer source = Create(out IoBus io);
        SbMixer target = Create(out IoBus targetIo);
        source.Irq = 10;
        source.Dma8 = 3;
        source.Dma16 = 7;
        source.InterruptStatus = 3;
        Write(io, 0x80, 0xAA); // Stored bytes differ from resource readbacks.
        Write(io, 0x81, 0xBB);
        Write(io, 0x82, 0xCC);
        target.Restore(source.Capture());
        byte[] expected = [0x08, 0x88, 0x03];
        for (int index = 0; index < expected.Length; index++)
        {
            targetIo.WriteByte(0x224, (byte)(0x80 + index));
            Assert.Equal(expected[index], targetIo.ReadByte(0x225));
            Assert.Equal(0xAA + index * 0x11, target.GetRegister(0x80 + index));
        }
        Assert.Equal(3, target.InterruptStatus);
        target.InterruptStatus &= 0xFE;
        Assert.Equal(2, targetIo.ReadByte(0x225));
        Assert.Equal(3, source.InterruptStatus);
    }

    [Fact]
    public void Mix1PreservesEveryRawRegisterAndPublicOwnerValue()
    {
        SbMixer source = Create(out IoBus io);
        for (int index = 1; index < 256; index++)
        {
            Write(io, (byte)index, (byte)(index ^ 0xAD));
        }
        // These public properties accept any int today. Their cross-device
        // validity is a later machine-preflight concern, not mixer port logic.
        source.Irq = int.MinValue;
        source.Dma8 = -1;
        source.Dma16 = int.MaxValue;
        source.InterruptStatus = 0xFF;
        SbMixer target = Create(out _);
        target.Restore(source.Capture());
        Assert.Equal(source.Capture(), target.Capture());
        for (int index = 0; index < 256; index++)
        {
            Assert.Equal(source.GetRegister(index), target.GetRegister(index));
        }
    }

    [Fact]
    public void SnapshotResetRestoresConstructedDefaultsIncludingIndexAndIrq82()
    {
        SbMixer mixer = Create(out IoBus io);
        mixer.Irq = 7;
        mixer.Dma8 = 3;
        mixer.Dma16 = 6;
        mixer.InterruptStatus = 3;
        Write(io, 0x22, 0);
        mixer.ResetSnapshotState();
        Assert.Equal(new SbMixer().Capture(), mixer.Capture());
        Assert.True(mixer.MasterLevel > 0);
    }

    [Fact]
    public void Mix1RejectsBadHeaderAndLengthBeforeChangingVolumeOrIrq82()
    {
        SbMixer mixer = Create(out IoBus io);
        Write(io, 0x22, 0xA4);
        mixer.InterruptStatus = 3;
        byte[] saved = mixer.Capture();
        List<byte[]> malformed = [[], saved[..^1], [.. saved, 0]];
        foreach (int offset in new[] { 0, 4, 6 })
        {
            byte[] invalid = (byte[])saved.Clone();
            invalid[offset] ^= 0xFF;
            malformed.Add(invalid);
        }
        foreach (byte[] invalid in malformed)
        {
            Assert.Throws<InvalidDataException>(() => SbMixer.ValidateSnapshot(invalid));
            Assert.Throws<InvalidDataException>(() => mixer.Restore(invalid));
            Assert.Equal(saved, mixer.Capture());
        }
    }
}
