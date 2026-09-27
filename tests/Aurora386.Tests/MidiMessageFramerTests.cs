using Aurora386.Core.Host.Midi;

namespace Aurora386.Tests;

/// <summary>
/// MIDI 1.0 wire framing as the MPU UART's host sink supplies it: raw bytes
/// become normalized short messages or complete SysEx spans, without an
/// endpoint, worker, or guest device involved.
/// </summary>
public sealed class MidiMessageFramerTests
{
    private const byte SystemExclusive = 0xF0;
    private const byte EndOfExclusive = 0xF7;
    private const byte SystemReset = 0xFF;
    private const byte TimingClock = 0xF8;
    private const byte ReservedRealtime = 0xF9;
    private const byte ActiveSensing = 0xFE;
    private const byte ReservedSystemCommon = 0xF4;
    private const byte ReservedSystemRealtime = 0xFD;
    private const string ShortCallback = "short";
    private const string SysExCallback = "sysex";

    /// <summary>Exercises every channel family, channel number, and legal data boundary.</summary>
    [Fact]
    public void EveryChannelMessageFamilyPreservesAllChannelsAndDataBoundaries()
    {
        byte[] threeByteFamilies = [0x80, 0x90, 0xA0, 0xB0, 0xE0];
        byte[] oneDataByteFamilies = [0xC0, 0xD0];
        byte[] dataBoundaries = [0x00, 0x7F];

        foreach (byte statusBase in threeByteFamilies)
        {
            foreach (byte channel in Enumerable.Range(0, 16).Select(value => (byte)value))
            {
                foreach (byte firstData in dataBoundaries)
                {
                    foreach (byte secondData in dataBoundaries)
                    {
                        var sink = new RecordingSink();
                        var framer = new MidiMessageFramer(sink);
                        byte status = (byte)(statusBase | channel);

                        Write(framer, status, firstData, secondData);

                        Assert.Equal(new uint[] { Pack(status, firstData, secondData) }, sink.ShortMessages);
                        Assert.Empty(sink.SysExMessages);
                    }
                }
            }
        }

        foreach (byte statusBase in oneDataByteFamilies)
        {
            foreach (byte channel in Enumerable.Range(0, 16).Select(value => (byte)value))
            {
                foreach (byte data in dataBoundaries)
                {
                    var sink = new RecordingSink();
                    var framer = new MidiMessageFramer(sink);
                    byte status = (byte)(statusBase | channel);

                    Write(framer, status, data);

                    Assert.Equal(new uint[] { Pack(status, data) }, sink.ShortMessages);
                    Assert.Empty(sink.SysExMessages);
                }
            }
        }
    }

    /// <summary>Checks the independently listed framing vectors from the measured adapter contract.</summary>
    [Theory]
    [MemberData(nameof(RepresentativeVectors))]
    public void RepresentativeVectorsProduceTheSpecifiedCallbacks(
        byte[] input,
        uint[] expectedShortMessages,
        byte[][] expectedSysExMessages,
        string[] expectedCallbackOrder)
    {
        var sink = new RecordingSink();
        var framer = new MidiMessageFramer(sink);

        Write(framer, input);

        Assert.Equal(expectedShortMessages, sink.ShortMessages);
        Assert.Equal(expectedSysExMessages, sink.SysExMessages);
        Assert.Equal(expectedCallbackOrder, sink.CallbackOrder);
    }

    /// <summary>Eight literal input/output vectors prevent a length table from becoming its own oracle.</summary>
    public static IEnumerable<object[]> RepresentativeVectors()
    {
        yield return
        [
            new byte[] { 0x90, 0x3C, TimingClock, 0x40, 0x3D, 0x41 },
            new uint[] { 0x000000F8, 0x00403C90, 0x00413D90 },
            Array.Empty<byte[]>(),
            new[] { ShortCallback, ShortCallback, ShortCallback },
        ];
        yield return
        [
            new byte[] { 0xC2, 0x05, 0x06 },
            new uint[] { 0x000005C2, 0x000006C2 },
            Array.Empty<byte[]>(),
            new[] { ShortCallback, ShortCallback },
        ];
        yield return
        [
            new byte[] { 0x90, 0x3C, ReservedRealtime, 0x40, ReservedSystemRealtime, 0x3D, 0x41 },
            new uint[] { 0x00403C90, 0x00413D90 },
            Array.Empty<byte[]>(),
            new[] { ShortCallback, ShortCallback },
        ];
        yield return
        [
            new byte[] { 0x90, 0x3C, ReservedSystemCommon, 0x40, 0x3D, 0x41 },
            Array.Empty<uint>(),
            Array.Empty<byte[]>(),
            Array.Empty<string>(),
        ];
        yield return
        [
            new byte[] { 0xF2, 0x01, TimingClock, 0x02, 0x03, 0x04 },
            new uint[] { 0x000000F8, 0x000201F2 },
            Array.Empty<byte[]>(),
            new[] { ShortCallback, ShortCallback },
        ];
        yield return
        [
            new byte[] { SystemExclusive, 0x7D, 0x01, TimingClock, 0x02, EndOfExclusive },
            new uint[] { 0x000000F8 },
            new byte[][] { [SystemExclusive, 0x7D, 0x01, 0x02, EndOfExclusive] },
            new[] { ShortCallback, SysExCallback },
        ];
        yield return
        [
            new byte[] { SystemExclusive, 0x7D, 0x01, 0xF6 },
            new uint[] { 0x000000F6 },
            new byte[][] { [SystemExclusive, 0x7D, 0x01, EndOfExclusive] },
            new[] { SysExCallback, ShortCallback },
        ];
        yield return
        [
            new byte[] { 0x90, 0x3C, SystemReset, 0x40, 0x3D, 0x41, 0xC0, 0x05 },
            new uint[] { 0x000000FF, 0x000005C0 },
            Array.Empty<byte[]>(),
            new[] { ShortCallback, ShortCallback },
        ];
    }

    /// <summary>System-common lengths cancel channel running status while real-time bytes preserve it.</summary>
    [Fact]
    public void SystemCommonReservedAndRealtimeStatusesHaveTheirDeclaredStateEffects()
    {
        var sink = new RecordingSink();
        var framer = new MidiMessageFramer(sink);

        Write(framer, 0x91, 0x10, 0x20);
        Write(framer, 0xF1, 0x7F);
        Write(framer, 0x30, 0x40);
        Write(framer, 0xF2, 0x01, 0x02);
        Write(framer, 0xF3, 0x03);
        Write(framer, 0xF6);
        Write(framer, 0x92, 0x11, TimingClock, 0x21);
        Write(framer, ReservedRealtime, ReservedSystemRealtime);
        Write(framer, 0x12, 0x22);
        Write(framer, ReservedSystemCommon, 0x13, 0x23);
        Write(framer, ActiveSensing);

        Assert.Equal(
            new uint[]
            {
                0x00201091,
                0x00007FF1,
                0x000201F2,
                0x000003F3,
                0x000000F6,
                0x000000F8,
                0x00211192,
                0x00221292,
                0x000000FE,
            },
            sink.ShortMessages);
        Assert.Empty(sink.SysExMessages);
    }

    /// <summary>Every emitted real-time status interrupts a short message without discarding its data.</summary>
    [Fact]
    public void EveryEmittedRealtimeStatusPreservesAnInterruptedChannelMessage()
    {
        byte[] emittedRealtime = [TimingClock, 0xFA, 0xFB, 0xFC, ActiveSensing];

        foreach (byte realtimeStatus in emittedRealtime)
        {
            var sink = new RecordingSink();
            var framer = new MidiMessageFramer(sink);

            Write(framer, 0x90, 0x10, realtimeStatus, 0x20);

            Assert.Equal(
                new uint[] { realtimeStatus, 0x00201090 },
                sink.ShortMessages);
            Assert.Empty(sink.SysExMessages);
        }
    }

    /// <summary>Normalizes every implicit SysEx termination to a complete synthetic EOX message.</summary>
    [Fact]
    public void SysExTerminationByStatusNestedStartAndChannelDataIsNormalized()
    {
        var sink = new RecordingSink();
        var framer = new MidiMessageFramer(sink);

        Write(framer, SystemExclusive, 0x7D, 0x01, 0x90, 0x3C, 0x40);
        Write(framer, SystemExclusive, 0x43, 0x12, SystemExclusive, 0x7D, 0x02, EndOfExclusive);

        Assert.Equal(new uint[] { 0x00403C90 }, sink.ShortMessages);
        Assert.Equal(
            new byte[][]
            {
                [SystemExclusive, 0x7D, 0x01, EndOfExclusive],
                [SystemExclusive, 0x43, 0x12, EndOfExclusive],
                [SystemExclusive, 0x7D, 0x02, EndOfExclusive],
            },
            sink.SysExMessages);
        Assert.Equal(
            new[] { SysExCallback, ShortCallback, SysExCallback, SysExCallback },
            sink.CallbackOrder);
    }

    /// <summary>System Reset emits once and discards running, buffered, and overflow SysEx state.</summary>
    [Fact]
    public void SystemResetDiscardsEveryPartialStateWithoutCompletingOldMessages()
    {
        var sink = new RecordingSink();
        var framer = new MidiMessageFramer(sink);

        Write(framer, SystemExclusive, 0x7D, 0x01, SystemReset, 0x02, EndOfExclusive);
        Assert.Equal(new uint[] { 0x000000FF }, sink.ShortMessages);
        Assert.Empty(sink.SysExMessages);

        framer.WriteByte(SystemExclusive);
        for (int index = 0; index < MidiMessageFramer.MaximumSysExLength - 1; index++)
        {
            framer.WriteByte(0x33);
        }

        Assert.Equal(1UL, framer.DroppedSysExCount);
        framer.WriteByte(SystemReset);
        Write(framer, 0xC5, 0x12);

        Assert.Equal(new uint[] { 0x000000FF, 0x000000FF, 0x000012C5 }, sink.ShortMessages);
        Assert.Empty(sink.SysExMessages);
        Assert.Equal(1UL, framer.DroppedSysExCount);
    }

    /// <summary>Accepts exactly 64 KiB including F0/F7, then drops one overlong message and recovers.</summary>
    [Fact]
    public void SysExLimitDropsWholeMessageAndRecoversAfterTermination()
    {
        var sink = new RecordingSink();
        var framer = new MidiMessageFramer(sink);

        framer.WriteByte(SystemExclusive);
        for (int index = 0; index < MidiMessageFramer.MaximumSysExLength - 2; index++)
        {
            framer.WriteByte(0x01);
        }

        framer.WriteByte(EndOfExclusive);
        Assert.Single(sink.SysExMessages);
        Assert.Equal(MidiMessageFramer.MaximumSysExLength, sink.SysExMessages[0].Length);
        Assert.Equal(SystemExclusive, sink.SysExMessages[0][0]);
        Assert.Equal(EndOfExclusive, sink.SysExMessages[0][^1]);
        Assert.Equal(0UL, framer.DroppedSysExCount);

        framer.WriteByte(SystemExclusive);
        for (int index = 0; index < MidiMessageFramer.MaximumSysExLength - 1; index++)
        {
            framer.WriteByte(0x02);
        }

        Assert.Equal(1UL, framer.DroppedSysExCount);
        framer.WriteByte(TimingClock);
        framer.WriteByte(EndOfExclusive);
        Write(framer, 0xC1, 0x7F);

        Assert.Single(sink.SysExMessages);
        Assert.Equal(new uint[] { 0x000000F8, 0x00007FC1 }, sink.ShortMessages);
        Assert.Equal(
            new[] { SysExCallback, ShortCallback, ShortCallback },
            sink.CallbackOrder);
    }

    /// <summary>Reset abandons every partial parser state while retaining the overflow lifetime diagnostic.</summary>
    [Fact]
    public void ResetDropsPartialStateAndPreservesTheDroppedSysExLifetimeCount()
    {
        var sink = new RecordingSink();
        var framer = new MidiMessageFramer(sink);

        Write(framer, 0x90, 0x3C);
        framer.WriteByte(SystemExclusive);
        for (int index = 0; index < MidiMessageFramer.MaximumSysExLength - 1; index++)
        {
            framer.WriteByte(0x55);
        }

        Assert.Equal(1UL, framer.DroppedSysExCount);
        framer.Reset();
        Write(framer, 0x40, 0x41, EndOfExclusive);
        Assert.Empty(sink.ShortMessages);
        Assert.Empty(sink.SysExMessages);
        Assert.Equal(1UL, framer.DroppedSysExCount);

        Write(framer, 0xD4, 0x7F);
        Assert.Equal(new uint[] { 0x00007FD4 }, sink.ShortMessages);
    }

    /// <summary>Drives a heterogeneous byte stream and compares a hand-written callback sequence.</summary>
    [Fact]
    public void MixedStreamHasDeterministicCallbackOrdering()
    {
        var sink = new RecordingSink();
        var framer = new MidiMessageFramer(sink);

        Write(
            framer,
            0x93, 0x20, TimingClock, 0x7F,
            0x21, 0x00,
            SystemExclusive, 0x7D, 0x10, ActiveSensing, 0x11, EndOfExclusive,
            0xF2, 0x01, 0x02,
            0xC3, 0x07, 0x08,
            SystemReset,
            0x09,
            0xD3, 0x7E);

        Assert.Equal(
            new uint[]
            {
                0x000000F8,
                0x007F2093,
                0x00002193,
                0x000000FE,
                0x000201F2,
                0x000007C3,
                0x000008C3,
                0x000000FF,
                0x00007ED3,
            },
            sink.ShortMessages);
        Assert.Equal(
            new byte[][]
            {
                [SystemExclusive, 0x7D, 0x10, 0x11, EndOfExclusive],
            },
            sink.SysExMessages);
        Assert.Equal(
            new[]
            {
                ShortCallback,
                ShortCallback,
                ShortCallback,
                ShortCallback,
                SysExCallback,
                ShortCallback,
                ShortCallback,
                ShortCallback,
                ShortCallback,
                ShortCallback,
            },
            sink.CallbackOrder);
    }

    private static uint Pack(byte status, byte firstData = 0, byte secondData = 0)
    {
        return (uint)(status | (firstData << 8) | (secondData << 16));
    }

    private static void Write(MidiMessageFramer framer, params byte[] bytes)
    {
        foreach (byte value in bytes)
        {
            framer.WriteByte(value);
        }
    }

    private sealed class RecordingSink : IMidiMessageSink
    {
        public List<uint> ShortMessages { get; } = [];
        public List<byte[]> SysExMessages { get; } = [];
        public List<string> CallbackOrder { get; } = [];

        public void WriteShort(uint message)
        {
            ShortMessages.Add(message);
            CallbackOrder.Add(ShortCallback);
        }

        public void WriteSysEx(ReadOnlySpan<byte> message)
        {
            SysExMessages.Add(message.ToArray());
            CallbackOrder.Add(SysExCallback);
        }
    }
}
