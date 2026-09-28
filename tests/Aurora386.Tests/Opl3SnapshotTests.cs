using System.Buffers.Binary;
using Aurora386.Core.Devices.Audio;
using Aurora386.Core.IO;

namespace Aurora386.Tests;

/// <summary>
/// Replays YMF262 bank/index writes, FM envelopes and timer detection sequences
/// used by 1990s DOS drivers across a stopped synthesis snapshot boundary.
/// </summary>
public sealed class Opl3SnapshotTests
{
    private const int OperatorRecordsOffset = 549;
    private const int OperatorRecordSize = 27;
    private const int VoiceRecordsOffset = 1521;
    private const int VoiceRecordSize = 8;

    private static Opl3 Create(out IoBus io, int rate = 44100)
    {
        io = new IoBus();
        var opl = new Opl3(rate);
        opl.Register(io);
        return opl;
    }

    private static void Write(IoBus io, int bank, int register, byte value)
    {
        ushort addressPort = (ushort)(0x388 + bank * 2);
        io.WriteByte(addressPort, (byte)register);
        io.WriteByte((ushort)(addressPort + 1), value);
    }

    private static void Play(IoBus io, int bank, byte attackDecay = 0xF8, byte panning = 0x31)
    {
        Write(io, 1, 0x05, 1); // Enable OPL3 bank and stereo routing.
        foreach (int slot in new[] { 0, 3 })
        {
            Write(io, bank, 0x20 + slot, 0x21); // Sustain enabled, multiplier 1.
            Write(io, bank, 0x40 + slot, 0x08);
            Write(io, bank, 0x60 + slot, attackDecay);
            Write(io, bank, 0x80 + slot, 0x88); // Mid-level sustain, audible release.
            Write(io, bank, 0xE0 + slot, 0);
        }
        Write(io, bank, 0xC0, panning);
        Write(io, bank, 0xA0, 0x98);
        Write(io, bank, 0xB0, 0x31); // Block 4, key-on, high frequency bits 1.
    }

    private static short[] Render(Opl3 opl, int frames)
    {
        short[] samples = new short[frames * 2];
        opl.Render(samples);
        return samples;
    }

    [Theory]
    [InlineData(1, 0, false)] // Attack, new target.
    [InlineData(1, 1, true)]
    [InlineData(2, 0, true)]  // Decay.
    [InlineData(2, 1, false)]
    [InlineData(3, 0, false)] // Sustain.
    [InlineData(3, 1, true)]
    [InlineData(4, 0, true)]  // Release.
    [InlineData(4, 1, false)]
    public void B0KeyOnAndOffResumeExactPcmInEveryActiveEnvelopePhase(int phase, int bank, bool dirtyTarget)
    {
        Opl3 source = Create(out IoBus io);
        Opl3 target = Create(out IoBus targetIo);
        Play(io, bank, phase == 1 ? (byte)0xA8 : (byte)0xF8);
        Render(source, phase <= 2 ? 100 : 5000);
        if (phase == 4)
        {
            Write(io, bank, 0xB0, 0x11); // Key-off starts release without resetting oscillator phase.
            Render(source, 100);
        }
        if (dirtyTarget)
        {
            Play(targetIo, 1 - bank, 0xD5, 0x10);
            Render(target, 417);
        }
        byte[] saved = source.Capture();
        int operatorOffset = OperatorRecordsOffset + bank * 18 * OperatorRecordSize;
        Assert.Equal(phase, saved[operatorOffset + 26]);
        Assert.InRange(BinaryPrimitives.ReadDoubleLittleEndian(saved.AsSpan(operatorOffset + 18)), 0.01, 0.99);
        target.Restore(saved);
        Assert.Equal(saved, target.Capture()); // No render or key edge during restore.
        short[] expected = Render(source, 1024);
        Assert.Contains(expected, sample => sample != 0);
        Assert.Equal(expected, Render(target, 1024));
        Assert.Equal(source.Capture(), target.Capture());
        target.Restore(saved);
        Assert.Equal(expected, Render(target, 1024)); // Capture remains immutable.
    }

    [Theory]
    [InlineData(0x10, true)]
    [InlineData(0x20, false)]
    public void Bank105AndC0PanningKeepTheSelectedStereoChannel(byte panning, bool left)
    {
        Opl3 source = Create(out IoBus io);
        Play(io, 1, 0xF8, panning);
        Render(source, 311);
        Opl3 target = Create(out _);
        target.Restore(source.Capture());
        Assert.True(target.Opl3ModeEnabled);
        short[] actual = Render(target, 500);
        Assert.Equal(Render(source, 500), actual);
        Assert.Contains(actual.Where((_, index) => index % 2 == (left ? 0 : 1)), sample => sample != 0);
        Assert.All(actual.Where((_, index) => index % 2 == (left ? 1 : 0)), sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void Ports388And38APreserveBothPendingDataWrites()
    {
        Opl3 source = Create(out IoBus io);
        Play(io, 0);
        Play(io, 1);
        Render(source, 233);
        io.WriteByte(0x388, 0xB0);
        io.WriteByte(0x38A, 0xC0);
        Opl3 target = Create(out IoBus targetIo);
        target.Restore(source.Capture());
        io.WriteByte(0x389, 0x11);
        targetIo.WriteByte(0x389, 0x11);
        io.WriteByte(0x38B, 0x20);
        targetIo.WriteByte(0x38B, 0x20);
        Assert.Equal(0x11, target.ReadRegister(0, 0xB0));
        Assert.Equal(0x20, target.ReadRegister(1, 0xC0));
        Assert.Equal(source.Capture(), target.Capture());
        Assert.Equal(Render(source, 1024), Render(target, 1024));
    }

    [Fact]
    public void Bank105ModeChangesDoNotReinterpretEarlierC0OrE0Writes()
    {
        Opl3 source = Create(out IoBus io);
        Play(io, 0);
        Write(io, 1, 0x05, 0);
        Write(io, 0, 0xC0, 0); // In OPL2 mode this still enables both outputs.
        Write(io, 0, 0xE0, 7); // In OPL2 mode only low two waveform bits latch.
        Write(io, 1, 0x05, 1);
        Render(source, 123);
        Opl3 target = Create(out _);
        target.Restore(source.Capture());
        Assert.Equal(7, target.ReadRegister(0, 0xE0));
        Assert.Equal(source.Capture(), target.Capture());
        short[] expected = Render(source, 400);
        Assert.Contains(expected, sample => sample != 0);
        Assert.Equal(expected, Render(target, 400));
    }

    [Fact]
    public void Timer04RestoresBothFractionsAndLatchedStatusWithoutAdvancing()
    {
        Opl3 source = Create(out IoBus io);
        Write(io, 0, 0x02, 0xFE); // Timer 1 expires at 160 us.
        Write(io, 0, 0x03, 0xFE); // Timer 2 expires at 640 us.
        Write(io, 0, 0x04, 3);
        source.AdvanceTimers(120);
        Opl3 target = Create(out IoBus targetIo);
        target.Restore(source.Capture());
        Assert.Equal(0, targetIo.ReadByte(0x388));
        foreach (double step in new[] { 39.0, 1.0, 479.0, 1.0 })
        {
            source.AdvanceTimers(step);
            target.AdvanceTimers(step);
            Assert.Equal(source.Status, target.Status);
            Assert.Equal(source.Capture(), target.Capture());
            if (step == 39)
            {
                Assert.Equal(0, target.Status);
            }
        }
        Assert.Equal(0xE0, targetIo.ReadByte(0x388));
        target.ResetSnapshotState();
        target.Restore(source.Capture());
        Assert.Equal(0xE0, targetIo.ReadByte(0x388)); // Reading status is nondestructive.
        Assert.Equal(0xE0, targetIo.ReadByte(0x388));
        Write(io, 0, 0x04, 0x80);
        Write(targetIo, 0, 0x04, 0x80);
        Assert.Equal(0, target.Status);
        Assert.Equal(source.Capture(), target.Capture());
    }

    [Fact]
    public void Timer02PreloadRewritePreservesFractionsBeyondTheNewPeriod()
    {
        Opl3 source = Create(out IoBus io);
        Write(io, 0, 0x04, 1);
        source.AdvanceTimers(16000); // 200 ticks with original 256-tick period.
        Write(io, 1, 0x02, 0xFF); // Current model accepts this bank's timer preload too.
        Opl3 target = Create(out _);
        target.Restore(source.Capture());
        Assert.Equal(0, target.ReadRegister(0, 0x02));
        Assert.Equal(0xFF, target.ReadRegister(1, 0x02));
        source.AdvanceTimers(1);
        target.AdvanceTimers(1);
        Assert.Equal(0xC0, target.Status);
        Assert.Equal(source.Capture(), target.Capture());
    }

    [Fact]
    public void Timer04MaskAndStoppedFractionSurviveRestore()
    {
        Opl3 source = Create(out IoBus io);
        Write(io, 0, 0x02, 0xFF);
        Write(io, 0, 0x04, 0x41); // Timer 1 running but masked.
        source.AdvanceTimers(40);
        Write(io, 0, 0x04, 0x40); // Stop halfway through the period.
        Opl3 target = Create(out IoBus targetIo);
        target.Restore(source.Capture());
        source.AdvanceTimers(1000);
        target.AdvanceTimers(1000);
        Assert.Equal(source.Capture(), target.Capture());
        Write(io, 0, 0x04, 0x41);
        Write(targetIo, 0, 0x04, 0x41);
        source.AdvanceTimers(40);
        target.AdvanceTimers(40);
        Assert.Equal(0, target.Status); // Mask suppresses the overflow flag.
        Write(io, 0, 0x04, 1);
        Write(targetIo, 0, 0x04, 1);
        source.AdvanceTimers(80);
        target.AdvanceTimers(80);
        Assert.Equal(0xC0, target.Status);
        Assert.Equal(source.Capture(), target.Capture());
    }

    [Fact]
    public void OutputRateMismatchRejectsBeforeChangingAnActiveVoice()
    {
        Opl3 source = Create(out _, 44100);
        Opl3 target = Create(out IoBus io, 48000);
        Play(io, 1);
        Render(target, 57);
        byte[] before = target.Capture();
        byte[] saved = source.Capture();
        Opl3.ValidateSnapshot(saved);
        Assert.Throws<InvalidDataException>(() => target.ValidateSnapshotForRestore(saved));
        Assert.Throws<InvalidDataException>(() => target.Restore(saved));
        Assert.Equal(before, target.Capture());
        target.ResetSnapshotState();
        Assert.Equal(new Opl3(48000).Capture(), target.Capture());
        Assert.All(Render(target, 100), sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void BothBanksPreserveAllRegistersOperatorsAndEighteenVoicesAtNativeRate()
    {
        Opl3 source = Create(out IoBus io, Opl3.NativeSampleRate);
        for (int bank = 0; bank < 2; bank++)
        {
            for (int register = 0; register < 256; register++)
            {
                Write(io, bank, register, (byte)(register * 7 + bank));
            }
        }
        Write(io, 1, 0x05, 1);
        for (int bank = 0; bank < 2; bank++)
        {
            for (int voice = 0; voice < 9; voice++)
            {
                int slot = voice / 3 * 8 + voice % 3;
                foreach (int op in new[] { slot, slot + 3 })
                {
                    byte envelopeFlags = voice % 2 == 0 ? (byte)0x30 : (byte)0;
                    Write(io, bank, 0x20 + op, (byte)(envelopeFlags | (op & 15)));
                    Write(io, bank, 0x60 + op, (byte)(0xF0 | (voice + 1)));
                    Write(io, bank, 0x80 + op, (byte)(0x40 | voice));
                    Write(io, bank, 0xE0 + op, (byte)((voice + bank) & 7));
                }
                Write(io, bank, 0xA0 + voice, (byte)(0x60 + voice * 3 + bank));
                Write(io, bank, 0xB0 + voice, 0x11);
                Write(io, bank, 0xB0 + voice, 0x31);
                Write(io, bank, 0xC0 + voice, (byte)(0x30 | voice));
            }
        }
        Render(source, 333);
        Opl3 target = Create(out _, Opl3.NativeSampleRate);
        byte[] saved = source.Capture();
        // These cached fields are currently unused by synthesis, so PCM alone
        // cannot prove that they survive the serialized boundary.
        Assert.Equal(1, saved[OperatorRecordsOffset + 8]); // Key-scale rate.
        Assert.Equal(0, saved[OperatorRecordsOffset + OperatorRecordSize + 7]); // Sustain disabled.
        Assert.Equal(3, saved[OperatorRecordsOffset + 2]); // Key-scale level from register40=0xC0.
        Assert.Equal(4, saved[VoiceRecordsOffset + 8 * VoiceRecordSize + 4]); // Feedback in C8.
        target.Restore(saved);
        Assert.Equal(saved, target.Capture());
        for (int bank = 0; bank < 2; bank++)
        {
            for (int register = 0; register < 256; register++)
            {
                Assert.Equal(source.ReadRegister(bank, register), target.ReadRegister(bank, register));
            }
        }
        short[] expected = Render(source, 1000);
        Assert.Contains(expected, sample => sample != 0);
        Assert.Equal(expected, Render(target, 1000));
        Assert.Equal(source.Capture(), target.Capture());
    }

    [Fact]
    public void Opl1RejectsBadHeadersRangesEnumsAndNonfiniteDoublesBeforeMutation()
    {
        Opl3 target = Create(out IoBus io);
        Play(io, 1);
        Render(target, 123);
        byte[] saved = target.Capture();
        List<byte[]> malformed = [[], saved[..^1], [.. saved, 0]];
        void AddByte(int offset, byte value)
        {
            byte[] invalid = (byte[])saved.Clone();
            invalid[offset] = value;
            malformed.Add(invalid);
        }
        void AddDouble(int offset, double value)
        {
            byte[] invalid = (byte[])saved.Clone();
            BinaryPrimitives.WriteDoubleLittleEndian(invalid.AsSpan(offset), value);
            malformed.Add(invalid);
        }
        foreach (int offset in new[] { 0, 4, 6, 8 })
        {
            AddByte(offset, 0xFF);
        }
        byte[] badRate = (byte[])saved.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(badRate.AsSpan(10), 0);
        malformed.Add(badRate);
        AddByte(528, 2); // OPL3-mode boolean.
        AddByte(531, 0x80); // Reset command cannot remain in the timer control latch.
        AddByte(532, 0x40); // Timer flag must include the summary bit.
        foreach (int offset in new[] { 533, 541 })
        {
            foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1, 256 })
            {
                AddDouble(offset, invalid);
            }
        }
        for (int index = 0; index < 36; index++)
        {
            int offset = OperatorRecordsOffset + index * OperatorRecordSize;
            byte[] invalidValues = [16, 64, 4, 16, 16, 16, 16, 2, 2, 8];
            for (int field = 0; field < invalidValues.Length; field++)
            {
                AddByte(offset + field, invalidValues[field]);
            }
            AddByte(offset + 26, 5); // Envelope enum ends at Release=4.
            foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, -0.01, 1.01 })
            {
                AddDouble(offset + 10, invalid);
                AddDouble(offset + 18, invalid);
            }
            AddDouble(offset + 10, 1); // Oscillator phase is strictly below one.
        }
        for (int index = 0; index < 18; index++)
        {
            int offset = VoiceRecordsOffset + index * VoiceRecordSize;
            AddByte(offset + 1, 4); // F-number is ten bits.
            byte[] invalidValues = [8, 2, 8, 2, 2, 2];
            for (int field = 0; field < invalidValues.Length; field++)
            {
                AddByte(offset + 2 + field, invalidValues[field]);
            }
        }
        foreach (byte[] invalid in malformed)
        {
            Assert.Throws<InvalidDataException>(() => Opl3.ValidateSnapshot(invalid));
            Assert.Throws<InvalidDataException>(() => target.Restore(invalid));
            Assert.Equal(saved, target.Capture());
        }
    }
}
