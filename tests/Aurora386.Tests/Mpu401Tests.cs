using Aurora386.Core.Devices.Audio;
using Aurora386.Core.Devices.Interrupts;
using Aurora386.Core.IO;

namespace Aurora386.Tests;

/// <summary>
/// The MPU-401 UART port as an old DOS driver uses it: commands arrive at
/// base plus one, raw MIDI bytes arrive at base, and ACKs drive IRQ 9.
/// </summary>
public class Mpu401Tests
{
    private const ushort BasePort = 0x0330;
    private const ushort DataPort = BasePort;
    private const ushort CommandAndStatusPort = BasePort + 1;
    private const byte ResetCommand = 0xFF;
    private const byte EnterUartCommand = 0x3F;
    private const byte Acknowledge = 0xFE;
    private const byte EmptyStatus = 0x80;
    private const byte TransmitBusyStatus = 0x40;
    private const int Irq = 9;

    private sealed class RecordingSink : IMidiSink
    {
        public List<byte> Bytes { get; } = [];
        public int ResetCount { get; private set; }
        public int RemainingAcceptances { get; set; } = int.MaxValue;

        public bool TryWriteByte(byte value)
        {
            if (RemainingAcceptances == 0)
            {
                return false;
            }

            RemainingAcceptances--;
            Bytes.Add(value);
            return true;
        }

        public void Reset()
        {
            ResetCount++;
        }
    }

    private static (Mpu401Uart Device, IoBus Io, InterruptController Pic, RecordingSink Sink) Build(
        ushort basePort = BasePort,
        int irq = Irq,
        RecordingSink? sink = null)
    {
        var io = new IoBus();
        var pic = new InterruptController();
        pic.Register(io);
        InitializePic(io);

        sink ??= new RecordingSink();
        var device = new Mpu401Uart(pic, basePort, irq, sink);
        device.Register(io);
        return (device, io, pic, sink);
    }

    private static void InitializePic(IoBus io)
    {
        foreach (ushort port in new ushort[] { 0x20, 0xA0 })
        {
            io.WriteByte(port, 0x11);
            io.WriteByte((ushort)(port + 1), port == 0x20 ? (byte)0x20 : (byte)0x28);
            io.WriteByte((ushort)(port + 1), port == 0x20 ? (byte)4 : (byte)2);
            io.WriteByte((ushort)(port + 1), 1);
            io.WriteByte((ushort)(port + 1), 0);
        }
    }

    private static void EnterUart(IoBus io)
    {
        io.WriteByte(CommandAndStatusPort, EnterUartCommand);
        Assert.Equal(Acknowledge, io.ReadByte(DataPort));
    }

    [Fact]
    public void IoBusReportsActiveLowStatusAndTheResetUartAckSequence()
    {
        (_, IoBus io, InterruptController pic, RecordingSink sink) = Build();

        Assert.Equal(EmptyStatus, io.ReadByte(CommandAndStatusPort));
        Assert.Equal(0xFF, io.ReadByte(DataPort));
        Assert.Equal(0xFF, io.ReadByte((ushort)(BasePort - 1)));

        io.WriteByte(CommandAndStatusPort, ResetCommand);
        Assert.Equal(0, io.ReadByte(CommandAndStatusPort));
        Assert.Equal(0x29, pic.GetPendingVector());
        io.WriteByte(0xA0, 0x20);
        io.WriteByte(0x20, 0x20);

        io.WriteByte(CommandAndStatusPort, EnterUartCommand);
        io.WriteByte(CommandAndStatusPort, EnterUartCommand);
        Assert.Equal(Acknowledge, io.ReadByte(DataPort));
        Assert.Equal(0x29, pic.GetPendingVector());
        Assert.Equal(Acknowledge, io.ReadByte(DataPort));
        Assert.Equal(EmptyStatus, io.ReadByte(CommandAndStatusPort));

        io.WriteByte(CommandAndStatusPort, 0x01);
        Assert.Equal(0xFF, io.ReadByte(DataPort));
        io.WriteByte(DataPort, 0xFF);
        Assert.Equal(new byte[] { 0xFF }, sink.Bytes);
    }

    [Fact]
    public void ResetReplacesUnreadRepliesAndDropsTheOldGuestMidiStream()
    {
        (_, IoBus io, InterruptController pic, RecordingSink sink) = Build();
        EnterUart(io);
        sink.RemainingAcceptances = 0;
        io.WriteByte(DataPort, 0x90);
        io.WriteByte(CommandAndStatusPort, EnterUartCommand);
        io.WriteByte(CommandAndStatusPort, ResetCommand);

        Assert.Equal(Acknowledge, io.ReadByte(DataPort));
        Assert.Equal(EmptyStatus, io.ReadByte(CommandAndStatusPort));
        Assert.Empty(sink.Bytes);
        Assert.Equal(2, sink.ResetCount);
        Assert.Equal(-1, pic.GetPendingVector());
        io.WriteByte(DataPort, 0x91);
        Assert.Empty(sink.Bytes);
    }

    [Fact]
    public void AFullFifoRejectsTheNewByteButStillDrainsOlderBytesWhenTheSinkRecovers()
    {
        (_, IoBus io, _, RecordingSink sink) = Build();
        EnterUart(io);
        sink.RemainingAcceptances = 0;

        for (byte value = 0x30; value < 0x38; value++)
        {
            io.WriteByte(DataPort, value);
        }

        Assert.Equal(TransmitBusyStatus | EmptyStatus, io.ReadByte(CommandAndStatusPort));
        sink.RemainingAcceptances = int.MaxValue;
        io.WriteByte(DataPort, 0x38);
        Assert.Equal(new byte[] { 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37 }, sink.Bytes);
        Assert.Equal(EmptyStatus, io.ReadByte(CommandAndStatusPort));
    }

    [Fact]
    public void PartialSinkAcceptanceResumesAtTheFirstUnacceptedByte()
    {
        (Mpu401Uart device, IoBus io, _, RecordingSink sink) = Build();
        EnterUart(io);
        sink.RemainingAcceptances = 0;
        byte[] message = [0x90, 60, 100, 0x80, 60, 0];
        foreach (byte value in message)
        {
            io.WriteByte(DataPort, value);
        }

        sink.RemainingAcceptances = 2;
        device.DrainOutput();
        device.DrainOutput(); // A repeated rejected attempt cannot consume the head.
        Assert.Equal(message[..2], sink.Bytes);
        sink.RemainingAcceptances = 1;
        device.DrainOutput();
        Assert.Equal(message[..3], sink.Bytes);
        sink.RemainingAcceptances = int.MaxValue;
        device.DrainOutput();
        device.DrainOutput();
        Assert.Equal(message, sink.Bytes);
        Assert.Equal(EmptyStatus, io.ReadByte(CommandAndStatusPort));
    }

    [Fact]
    public void StatusAndCaptureDoNotWriteToTheSinkAndReplacementKeepsGuestFifoUndrained()
    {
        (Mpu401Uart device, IoBus io, _, RecordingSink oldSink) = Build();
        EnterUart(io);
        oldSink.RemainingAcceptances = 0;
        io.WriteByte(DataPort, 0x90);
        io.ReadByte(CommandAndStatusPort);
        byte[] beforeCapture = device.Capture();
        Assert.Empty(oldSink.Bytes);

        var newSink = new RecordingSink();
        device.SetSink(newSink);
        Assert.Equal(2, oldSink.ResetCount);
        Assert.Equal(1, newSink.ResetCount);
        Assert.Empty(newSink.Bytes);

        Assert.Equal(beforeCapture, device.Capture());
        device.DrainOutput();
        Assert.Equal(new byte[] { 0x90 }, newSink.Bytes);
    }

    [Fact]
    public void UartEntryBehindAnUnreadResetAckDoesNotCreateAnotherPicEdge()
    {
        (_, IoBus io, InterruptController pic, _) = Build();
        io.WriteByte(CommandAndStatusPort, ResetCommand);
        Assert.Equal(0x29, pic.GetPendingVector());

        io.WriteByte(CommandAndStatusPort, EnterUartCommand);
        io.WriteByte(0xA0, 0x20);
        io.WriteByte(0x20, 0x20);
        Assert.False(pic.HasPendingInterrupt);

        Assert.Equal(Acknowledge, io.ReadByte(DataPort));
        Assert.Equal(0x29, pic.GetPendingVector());
    }

    [Fact]
    public void SnapshotIsCanonicalAndRestoresThePartialFifoWithoutDrainingIt()
    {
        (Mpu401Uart device, IoBus io, _, RecordingSink sourceSink) = Build();
        EnterUart(io);
        sourceSink.RemainingAcceptances = 0;
        io.WriteByte(DataPort, 0x90);
        io.WriteByte(DataPort, 0x45);
        byte[] payload = device.Capture();

        Assert.Equal(Mpu401Uart.SnapshotLength, payload.Length);
        Assert.Equal(BasePort, BitConverter.ToUInt16(payload, 0));
        Assert.Equal((byte)Irq, payload[2]);
        Assert.Equal(1, payload[3]);
        Assert.Equal(0, payload[4]);
        Assert.Equal(2, payload[5]);
        Assert.Equal(0x90, payload[6]);
        Assert.Equal(0x45, payload[7]);
        Assert.All(payload[8..], value => Assert.Equal(0, value));

        var targetSink = new RecordingSink();
        (Mpu401Uart restored, _, _, _) = Build(sink: targetSink);
        restored.Restore(payload, Mpu401Uart.SnapshotVersion);
        Assert.Equal(2, targetSink.ResetCount);
        Assert.Empty(targetSink.Bytes);
        restored.DrainOutput();
        Assert.Equal(new byte[] { 0x90, 0x45 }, targetSink.Bytes);
    }

    [Fact]
    public void InvalidSnapshotsDoNotMutateTheDeviceSinkOrAckInterrupt()
    {
        (Mpu401Uart device, IoBus io, InterruptController pic, RecordingSink sink) = Build();
        io.WriteByte(CommandAndStatusPort, ResetCommand);
        byte[] before = device.Capture();
        int resetCount = sink.ResetCount;

        foreach (byte[] invalid in InvalidSnapshots(before))
        {
            Assert.Throws<InvalidDataException>(() => Mpu401Uart.ValidateSnapshot(invalid, Mpu401Uart.SnapshotVersion));
            Assert.Throws<InvalidDataException>(() => device.Restore(invalid, Mpu401Uart.SnapshotVersion));
            Assert.Equal(before, device.Capture());
            Assert.Equal(resetCount, sink.ResetCount);
            Assert.True(pic.HasPendingInterrupt);
        }

        Assert.Throws<InvalidDataException>(() => device.Restore(before, Mpu401Uart.SnapshotVersion + 1));
        Assert.Equal(before, device.Capture());
        Assert.Equal(resetCount, sink.ResetCount);
    }

    [Fact]
    public void DirectRestoreRejectsAValidSnapshotForDifferentImmutableResources()
    {
        (Mpu401Uart source, IoBus sourceIo, _, RecordingSink sourceSink) = Build();
        EnterUart(sourceIo);
        sourceSink.RemainingAcceptances = 0;
        sourceIo.WriteByte(DataPort, 0x90);
        byte[] payload = source.Capture();

        (Mpu401Uart target, _, _, RecordingSink targetSink) = Build(0x0332, 5);
        byte[] before = target.Capture();
        Assert.Throws<InvalidDataException>(() => target.Restore(payload, Mpu401Uart.SnapshotVersion));
        Assert.Equal(before, target.Capture());
        Assert.Equal(1, targetSink.ResetCount);
    }

    private static IEnumerable<byte[]> InvalidSnapshots(byte[] valid)
    {
        yield return valid[..^1];
        yield return ReplacePort(valid, 0);
        yield return Replace(valid, 2, 2);
        yield return Replace(valid, 3, 2);
        yield return Replace(valid, 4, 3);
        yield return Replace(valid, 5, 9);
        yield return Replace(valid, 6, 1);
    }

    private static byte[] Replace(byte[] bytes, int offset, byte value)
    {
        byte[] replacement = bytes.ToArray();
        replacement[offset] = value;
        return replacement;
    }

    private static byte[] ReplacePort(byte[] bytes, ushort value)
    {
        byte[] replacement = bytes.ToArray();
        replacement[0] = (byte)value;
        replacement[1] = (byte)(value >> 8);
        return replacement;
    }
}
