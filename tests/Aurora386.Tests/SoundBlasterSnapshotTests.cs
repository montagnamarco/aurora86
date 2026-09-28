using System.Buffers.Binary;
using Aurora386.Core.Devices.Audio;
using Aurora386.Core.Devices.Interrupts;
using Aurora386.Core.Devices.System;
using Aurora386.Core.IO;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// Replays the CT1740 DSP byte protocol and DMA audio used by 1990s DOS drivers
/// across stopped snapshots, with independently programmed memory and ports.
/// </summary>
public sealed class SoundBlasterSnapshotTests
{
    private const ushort CommandPort = 0x22C;
    private const ushort DataPort = 0x22A;
    private const int HeaderLength = 108;

    private static SoundBlaster16 Create(out IoBus io, Dma8237? dma = null,
        InterruptController? interrupts = null, int rate = 44100)
    {
        io = new IoBus();
        dma?.Register(io);
        interrupts?.Register(io);
        var card = new SoundBlaster16(interrupts, dma, outputSampleRate: rate);
        card.Register(io);
        return card;
    }

    private static void Command(IoBus io, params byte[] bytes)
    {
        foreach (byte value in bytes)
        {
            io.WriteByte(CommandPort, value);
        }
    }

    private static short[] Drain(SoundBlaster16 card)
    {
        short[] samples = new short[card.AvailableSamples];
        Assert.Equal(samples.Length, card.ReadSamples(samples));
        return samples;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Command41At22CResumesPartialRateArguments(int received)
    {
        SoundBlaster16 source = Create(out IoBus sourceIo);
        SoundBlaster16 target = Create(out IoBus targetIo);
        Command(sourceIo, 0xE4, 0xA7, 0xD1, 0x41);
        if (received == 1)
        {
            Command(sourceIo, 0x7D);
        }
        Command(targetIo, 0xE3, 0x10, 0xFF, 0xB6, 0x30);
        byte[] saved = source.Capture();
        target.Restore(saved);
        Assert.Equal(saved, target.Capture());
        Assert.Equal(22050, target.PlaybackRate);
        if (received == 0)
        {
            Command(sourceIo, 0x7D);
            Command(targetIo, 0x7D);
        }
        Command(sourceIo, 0x00, 0xE8, 0xD8);
        Command(targetIo, 0x00, 0xE8, 0xD8);
        Assert.Equal(32000, target.PlaybackRate);
        Assert.Equal(0xA7, targetIo.ReadByte(DataPort));
        Assert.Equal(0xFF, targetIo.ReadByte(DataPort));
        Assert.Equal(0xA7, sourceIo.ReadByte(DataPort));
        Assert.Equal(0xFF, sourceIo.ReadByte(DataPort));
        Assert.Equal(source.Capture(), target.Capture());
    }

    [Fact]
    public void Data22AKeepsUnreadFifoOrderAndLastOutputLatch()
    {
        SoundBlaster16 source = Create(out IoBus sourceIo);
        SoundBlaster16 target = Create(out IoBus targetIo);
        Command(sourceIo, 0xE1, 0xE0, 0x37);
        Assert.Equal(4, sourceIo.ReadByte(DataPort));
        target.Restore(source.Capture());
        Assert.Equal(0x80, targetIo.ReadByte(0x22E) & 0x80);
        Assert.Equal(5, targetIo.ReadByte(DataPort));
        Assert.Equal(0xC8, targetIo.ReadByte(DataPort));
        Assert.Equal(0, targetIo.ReadByte(0x22E) & 0x80);
        Assert.Equal(0xC8, targetIo.ReadByte(DataPort));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Reset226Bit0PreservesReleaseEdgeWithoutInventingAa(byte resetValue)
    {
        SoundBlaster16 source = Create(out IoBus sourceIo);
        SoundBlaster16 target = Create(out IoBus targetIo);
        sourceIo.WriteByte(0x226, resetValue);
        while ((sourceIo.ReadByte(0x22E) & 0x80) != 0)
        {
            sourceIo.ReadByte(DataPort);
        }
        target.Restore(source.Capture());
        Assert.Equal(0, targetIo.ReadByte(0x22E) & 0x80);
        targetIo.WriteByte(0x226, 0);
        Assert.Equal(resetValue == 1 ? 0x80 : 0, targetIo.ReadByte(0x22E) & 0x80);
        if (resetValue == 1)
        {
            Assert.Equal(0xAA, targetIo.ReadByte(DataPort));
        }
    }

    /// <summary>Programs an eight-transfer auto-init DMA buffer through AT ports.</summary>
    private static void ProgramDma(IoBus io, bool word)
    {
        ushort addressPort = word ? (ushort)0xC4 : (ushort)0x02;
        ushort countPort = word ? (ushort)0xC6 : (ushort)0x03;
        io.WriteByte(word ? (ushort)0xD8 : (ushort)0x0C, 0);
        io.WriteByte(addressPort, 0);
        io.WriteByte(addressPort, 0);
        io.WriteByte(countPort, 7);
        io.WriteByte(countPort, 0);
        io.WriteByte(word ? (ushort)0x8B : (ushort)0x83, 2);
        io.WriteByte(word ? (ushort)0xD6 : (ushort)0x0B, 0x59);
        io.WriteByte(word ? (ushort)0xD4 : (ushort)0x0A, 1);
    }

    [Theory]
    [InlineData(false, false, false, 93.75)]
    [InlineData(false, false, false, 125)]
    [InlineData(true, false, false, 93.75)]
    [InlineData(true, false, false, 125)]
    [InlineData(false, true, false, 31.25)]
    [InlineData(true, true, false, 62.5)]
    [InlineData(false, false, true, 109)]
    [InlineData(true, true, true, 47)]
    [InlineData(false, false, false, 93.75, true)]
    [InlineData(true, true, false, 62.5, true)]
    public void CommandsC4B4ResumeAutoInitPcmDmaAndIrq82AcrossBlockBoundary(
        bool word, bool stereo, bool paused, double beforeCapture, bool reverseSigned = false)
    {
        using var memory = new PhysicalMemoryBus(1024 * 1024);
        for (int index = 0; index < 16; index++)
        {
            memory.WriteByte(0x20000u + (uint)index, (byte)(19 + index * 13));
        }
        var sourceDma = new Dma8237(memory);
        var targetDma = new Dma8237(memory);
        var sourcePic = new InterruptController();
        var targetPic = new InterruptController();
        SoundBlaster16 source = Create(out IoBus sourceIo, sourceDma, sourcePic);
        SoundBlaster16 target = Create(out IoBus targetIo, targetDma, targetPic);
        ProgramDma(sourceIo, word);
        Command(sourceIo, 0x41, 0x7D, 0x00); // 32 kHz leaves a resampling fraction.
        byte mode = (byte)((word != reverseSigned ? 0x10 : 0) | (stereo ? 0x20 : 0));
        Command(sourceIo, word ? (byte)0xB4 : (byte)0xC4, mode, 3, 0);
        source.Advance(beforeCapture);
        if (paused)
        {
            Command(sourceIo, word ? (byte)0xD5 : (byte)0xD0);
        }
        Command(targetIo, 0xF2, 0xF3, 0x10, 0xFF, 0x41, 0x12);
        targetDma.Restore(sourceDma.Capture());
        targetPic.Restore(sourcePic.Capture());
        byte[] picBeforeRestore = targetPic.Capture();
        byte[] dmaBeforeRestore = targetDma.Capture();
        int sourceIrqs = source.InterruptCount;
        int targetIrqs = target.InterruptCount;
        target.Restore(source.Capture());
        Assert.Equal(picBeforeRestore, targetPic.Capture());
        Assert.Equal(dmaBeforeRestore, targetDma.Capture());
        Assert.Equal(targetIrqs, target.InterruptCount);
        Assert.Equal(source.Capture(), target.Capture());
        Assert.Equal(!paused, target.IsPlaying);
        Assert.Equal(source.Mixer.InterruptStatus, target.Mixer.InterruptStatus);
        short[] queued = Drain(source);
        Assert.Contains(queued, sample => sample != 0);
        Assert.Equal(queued, Drain(target));
        if (paused)
        {
            source.Advance(27);
            target.Advance(27);
            Assert.Equal(dmaBeforeRestore, targetDma.Capture());
            Assert.Equal(Drain(source), Drain(target));
            Command(sourceIo, word ? (byte)0xD6 : (byte)0xD4);
            Command(targetIo, word ? (byte)0xD6 : (byte)0xD4);
        }
        foreach (double duration in new[] { 19.0, 43.0, 67.0, 121.0 })
        {
            source.Advance(duration);
            target.Advance(duration);
            Assert.Equal(Drain(source), Drain(target));
            Assert.Equal(sourceDma.Capture(), targetDma.Capture());
            Assert.Equal(sourcePic.Capture(), targetPic.Capture());
            Assert.Equal(source.InterruptCount - sourceIrqs, target.InterruptCount - targetIrqs);
            sourceIo.WriteByte(0x224, 0x82);
            targetIo.WriteByte(0x224, 0x82);
            Assert.Equal(sourceIo.ReadByte(0x225), targetIo.ReadByte(0x225));
            ushort acknowledgePort = word ? (ushort)0x22F : (ushort)0x22E;
            Assert.Equal(sourceIo.ReadByte(acknowledgePort), targetIo.ReadByte(acknowledgePort));
            Assert.Equal(0, target.Mixer.InterruptStatus);
            Assert.Equal(source.Capture(), target.Capture());
        }
        Assert.True(source.InterruptCount > sourceIrqs);
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(49715)]
    public void DirectDac10PreservesWrappedQueueAfterOddHostRead(int rate)
    {
        SoundBlaster16 source = Create(out IoBus sourceIo, rate: rate);
        SoundBlaster16 target = Create(out IoBus targetIo, rate: rate);
        for (int index = 0; index < rate; index++)
        {
            Command(sourceIo, 0x10, (byte)index);
        }
        Assert.Equal(3, source.ReadSamples(new short[3]));
        byte[] saved = source.Capture();
        target.Restore(saved);
        Assert.Equal(saved, target.Capture());
        for (int index = 0; index < 17; index++)
        {
            Command(sourceIo, 0x10, (byte)(index * 11));
            Command(targetIo, 0x10, (byte)(index * 11));
        }
        short[] expected = Drain(source);
        Assert.Contains(expected, sample => sample != 0);
        Assert.Equal(expected, Drain(target));
        target.Restore(saved);
        Assert.Equal(saved, target.Capture());
    }

    [Fact]
    public void MixerIndex224AndOplBank1B0ContinueNestedFmAndTimerFractions()
    {
        SoundBlaster16 source = Create(out IoBus sourceIo);
        SoundBlaster16 target = Create(out IoBus targetIo);
        void WriteFm(int bank, int register, byte value)
        {
            ushort port = (ushort)(0x388 + bank * 2);
            sourceIo.WriteByte(port, (byte)register);
            sourceIo.WriteByte((ushort)(port + 1), value);
        }
        WriteFm(1, 0x05, 1);
        foreach (int slot in new[] { 0, 3 })
        {
            WriteFm(1, 0x20 + slot, 0x21);
            WriteFm(1, 0x40 + slot, 0);
            WriteFm(1, 0x60 + slot, 0xF8);
            WriteFm(1, 0x80 + slot, 0x88);
        }
        WriteFm(1, 0xC0, 0x31);
        WriteFm(1, 0xA0, 0x98);
        WriteFm(1, 0xB0, 0x31);
        WriteFm(0, 0x02, 0xFE);
        WriteFm(0, 0x04, 1);
        source.Advance(111);
        sourceIo.WriteByte(0x224, 0x34);
        sourceIo.WriteByte(0x38A, 0xB0);
        target.Restore(source.Capture());
        Assert.Equal(Drain(source), Drain(target));
        foreach (IoBus io in new[] { sourceIo, targetIo })
        {
            io.WriteByte(0x225, 0xA8);
            io.WriteByte(0x38B, 0x11); // Key off through the saved bank-1 selector.
        }
        source.Advance(1234.5);
        target.Advance(1234.5);
        short[] expected = Drain(source);
        Assert.Contains(expected, sample => sample != 0);
        Assert.Equal(expected, Drain(target));
        Assert.Equal(0xC0, targetIo.ReadByte(0x388) & 0xC0);
        Assert.Equal(source.Capture(), target.Capture());
    }

    [Fact]
    public void ResourceStateAndDisabledLatchRestoreWithoutChangingPicOrMixerAliases()
    {
        SoundBlaster16 source = Create(out _);
        var pic = new InterruptController();
        SoundBlaster16 target = Create(out _, interrupts: pic);
        source.Relocate(0x240, 7, 3, 6, false);
        source.Mixer.Irq = 10; // Preserve independent owner values until machine preflight.
        source.WriteByte(0x244, 0x80);
        byte[] before = pic.Capture();
        target.Restore(source.Capture());
        Assert.Equal((ushort)0x240, target.BasePort);
        Assert.False(target.Enabled);
        Assert.Equal(7, target.Irq);
        Assert.Equal(3, target.Dma8);
        Assert.Equal(6, target.Dma16);
        Assert.Equal(0x08, target.ReadByte(0x245));
        Assert.Equal(before, pic.Capture());
        Assert.Equal(source.Capture(), target.Capture());
    }

    [Fact]
    public void ResetSnapshotStateClearsDspPcmAndNestedStateToConstructedDefaults()
    {
        var fresh = new SoundBlaster16(basePort: 0x240, outputSampleRate: 48000);
        var target = new SoundBlaster16(basePort: 0x240, outputSampleRate: 48000);
        target.WriteByte(0x24C, 0x10);
        target.WriteByte(0x24C, 0xFF);
        target.WriteByte(0x24C, 0xE3);
        target.Relocate(0x260, 7, 3, 6, false);
        target.Fm.WriteRegister(1, 0x05, 1);
        target.Advance(13);
        target.ResetSnapshotState();
        Assert.Equal(fresh.Capture(), target.Capture());
        target.ResetSnapshotState();
        Assert.Equal(fresh.Capture(), target.Capture());
    }

    [Fact]
    public void Command48CanReplaceBlockLengthBeforeCurrentDmaBlockCompletes()
    {
        SoundBlaster16 source = Create(out IoBus io);
        SoundBlaster16 target = Create(out _);
        Command(io, 0xC4, 0, 0xFF, 0, 0x48, 1, 0);
        byte[] saved = source.Capture();
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(saved.AsSpan(60)));
        Assert.Equal(256, BinaryPrimitives.ReadInt32LittleEndian(saved.AsSpan(64)));
        target.Restore(saved);
        Assert.Equal(saved, target.Capture());
        Command(io, 0x40, 0xFF, 0x48, 0xFF, 0xFF, 0x1C, 0x1C);
        saved = source.Capture();
        Assert.Equal(1_000_000, source.PlaybackRate);
        Assert.Equal(65538, BinaryPrimitives.ReadInt32LittleEndian(saved.AsSpan(60)));
        target.Restore(saved);
        Assert.Equal(saved, target.Capture());
    }

    [Fact]
    public void Dsp1MalformedHeaderFractionsCountsAndNestedPayloadsRejectAtomically()
    {
        SoundBlaster16 target = Create(out IoBus io);
        Command(io, 0xE1, 0x10, 0xFE, 0xF2, 0x41, 0xAC);
        byte[] saved = target.Capture();
        byte[] extended = [.. saved, 0];
        var invalid = new List<byte[]> { saved[..^1], extended };
        foreach (int offset in new[] { 0, 4, 6, 18, 19, 38, 39, 40, 41, 42, 43 })
        {
            byte[] corrupt = (byte[])saved.Clone();
            corrupt[offset] = 0xFF;
            invalid.Add(corrupt);
        }
        foreach (int offset in new[] { 8, 12, 44, 48, 56, 60, 64, 92, 96, 100, 104 })
        {
            byte[] corrupt = (byte[])saved.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(corrupt.AsSpan(offset), -1);
            invalid.Add(corrupt);
        }
        foreach (int offset in new[] { 68, 76, 84 })
        {
            foreach (double value in new[] { double.NaN, double.PositiveInfinity, -0.5, 1.0 })
            {
                byte[] corrupt = (byte[])saved.Clone();
                BinaryPrimitives.WriteDoubleLittleEndian(corrupt.AsSpan(offset), value);
                invalid.Add(corrupt);
            }
        }
        int mixerOffset = HeaderLength + BinaryPrimitives.ReadInt32LittleEndian(saved.AsSpan(92))
            + 2 * BinaryPrimitives.ReadInt32LittleEndian(saved.AsSpan(96));
        int oplOffset = mixerOffset + BinaryPrimitives.ReadInt32LittleEndian(saved.AsSpan(100));
        foreach (int offset in new[] { mixerOffset, oplOffset })
        {
            byte[] corrupt = (byte[])saved.Clone();
            corrupt[offset] ^= 0xFF;
            invalid.Add(corrupt);
        }
        foreach (byte[] corrupt in invalid)
        {
            Assert.Throws<InvalidDataException>(() => target.Restore(corrupt));
            Assert.Equal(saved, target.Capture());
        }
        SoundBlaster16 otherRate = Create(out _, rate: 48000);
        byte[] otherBefore = otherRate.Capture();
        Assert.Throws<InvalidDataException>(() => otherRate.Restore(saved));
        Assert.Equal(otherBefore, otherRate.Capture());
    }
}
