using Aurora386.Core.Devices.System;
using Aurora386.Core.IO;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// Replays the AT's paired 8237 register programming and sample transfers used
/// by DOS sound drivers across a stopped device snapshot boundary.
/// </summary>
public sealed class DmaSnapshotTests
{
    private static Dma8237 Create(PhysicalMemoryBus memory, out IoBus io)
    {
        io = new IoBus();
        var dma = new Dma8237(memory);
        dma.Register(io);
        return dma;
    }

    private static void Program(IoBus io, bool word, ushort address, byte mode)
    {
        ushort addressPort = word ? (ushort)0xC4 : (ushort)0x02;
        ushort countPort = word ? (ushort)0xC6 : (ushort)0x03;
        io.WriteByte(word ? (ushort)0xD8 : (ushort)0x0C, 0); // Clear byte pointer.
        io.WriteByte(addressPort, (byte)address);
        io.WriteByte(addressPort, (byte)(address >> 8));
        io.WriteByte(countPort, 2); // Three transfers.
        io.WriteByte(countPort, 0);
        io.WriteByte(word ? (ushort)0x8B : (ushort)0x83, 3);
        io.WriteByte(word ? (ushort)0xD6 : (ushort)0x0B, mode);
        io.WriteByte(word ? (ushort)0xD4 : (ushort)0x0A, 1); // Unmask channel 1/5.
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void ModeBits4And5ResumeTransfersAndStatus08D0(bool word, bool decrement, bool autoInit)
    {
        using var memory = new PhysicalMemoryBus(1024 * 1024);
        Dma8237 source = Create(memory, out IoBus sourceIo);
        Dma8237 target = Create(memory, out IoBus targetIo);
        int channel = word ? 5 : 1;
        byte mode = (byte)(0x49 | (decrement ? 0x20 : 0) | (autoInit ? 0x10 : 0));
        Program(sourceIo, word, 0x1234, mode);
        Program(targetIo, word, 0x8888, 0x49);
        uint initialAddress = word ? 0x22468u : 0x31234u;
        Assert.Equal(initialAddress, source.GetPhysicalAddress(channel));
        for (int index = 0; index < 3; index++)
        {
            int distance = index * (word ? 2 : 1) * (decrement ? -1 : 1);
            uint address = (uint)(initialAddress + distance);
            if (word)
            {
                memory.WriteWord(address, (ushort)(0xA130 + index));
            }
            else
            {
                memory.WriteByte(address, (byte)(0x30 + index));
            }
        }

        ushort? Transfer(Dma8237 dma) => word ? dma.TransferWord(channel) : dma.TransferByte(channel);
        Assert.Equal(word ? (ushort)0xA130 : (ushort)0x30, Transfer(source));
        byte[] saved = source.Capture();
        Dma8237.Channel retainedChannel = target[channel];
        target.Restore(saved);
        Assert.Same(retainedChannel, target[channel]);
        Assert.Equal(saved, target.Capture()); // Restore did not step a transfer.
        for (int index = 1; index < 3; index++)
        {
            ushort expected = (ushort)((word ? 0xA130 : 0x30) + index);
            Assert.Equal(expected, Transfer(source));
            Assert.Equal(expected, Transfer(target));
        }
        Assert.Equal(source.Capture(), target.Capture());
        Assert.Equal(autoInit ? (ushort)2 : ushort.MaxValue, target[channel].CurrentCount);
        Assert.Equal(!autoInit, target[channel].Masked);
        ushort statusPort = word ? (ushort)0xD0 : (ushort)0x08;
        byte expectedStatus = autoInit ? (byte)0x22 : (byte)0x02;
        Assert.Equal(expectedStatus, targetIo.ReadByte(statusPort));
        Assert.Equal(expectedStatus, sourceIo.ReadByte(statusPort));
        byte[] afterStatus = target.Capture();
        target.Restore(saved);
        Assert.Equal(saved, target.Capture()); // The capture owns its bytes.
        target.Restore(afterStatus);
        Assert.Equal(autoInit ? (byte)0x20 : (byte)0, targetIo.ReadByte(statusPort));
        if (autoInit)
        {
            Assert.Equal(initialAddress, target.GetPhysicalAddress(channel));
            Assert.Equal(Transfer(source), Transfer(target));
        }
        else
        {
            Assert.Null(Transfer(target));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ports02C4And03C6PreserveIndependentBytePointers(bool readFirst)
    {
        using var memory = new PhysicalMemoryBus(1024 * 1024);
        Dma8237 source = Create(memory, out IoBus sourceIo);
        Dma8237 target = Create(memory, out IoBus targetIo);
        Program(sourceIo, false, 0x1234, 0x49);
        Program(sourceIo, true, 0x5678, 0x49);
        if (readFirst)
        {
            Assert.Equal(0x34, sourceIo.ReadByte(0x02));
            Assert.Equal(0x78, sourceIo.ReadByte(0xC4));
        }
        else
        {
            sourceIo.WriteByte(0x02, 0xAB);
            sourceIo.WriteByte(0xC4, 0xCD);
        }
        target.Restore(source.Capture());
        if (readFirst)
        {
            Assert.Equal(0x12, targetIo.ReadByte(0x02));
            Assert.Equal(0x56, targetIo.ReadByte(0xC4));
            sourceIo.ReadByte(0x02);
            sourceIo.ReadByte(0xC4);
        }
        else
        {
            // Each pointer is controller-wide: finish a different register.
            sourceIo.WriteByte(0x03, 0x9A);
            targetIo.WriteByte(0x03, 0x9A);
            sourceIo.WriteByte(0xC6, 0xBC);
            targetIo.WriteByte(0xC6, 0xBC);
            Assert.Equal(0x9A02, target[1].CurrentCount);
            Assert.Equal(0xBC02, target[5].CurrentCount);
        }
        Assert.Equal(source.Capture(), target.Capture());
    }

    [Fact]
    public void TerminalCountBit1SurvivesCaptureUntilStatusRead()
    {
        using var memory = new PhysicalMemoryBus(1024 * 1024);
        Dma8237 source = Create(memory, out IoBus io);
        Dma8237 target = Create(memory, out IoBus targetIo);
        Program(io, false, 0, 0x49);
        for (int index = 0; index < 3; index++)
        {
            source.TransferByte(1);
        }
        target.Restore(source.Capture());
        Assert.Equal(2, targetIo.ReadByte(0x08));
        Assert.Equal(0, targetIo.ReadByte(0x08));
        Assert.True(source.ConsumeTerminalCount(1));
    }

    [Fact]
    public void ResetClearsAllChannelsAndBytePointersToConstructedDefaults()
    {
        using var memory = new PhysicalMemoryBus(1024 * 1024);
        Dma8237 dma = Create(memory, out IoBus io);
        Program(io, false, 0xFFFF, 0x79);
        Program(io, true, 0xABCD, 0x59);
        io.WriteByte(0x02, 0xFF);
        io.WriteByte(0xC4, 0xFF);
        Dma8237.Channel channel = dma[1];
        dma.ResetSnapshotState();
        Assert.Same(channel, dma[1]);
        Assert.Equal(new Dma8237().Capture(), dma.Capture());
    }
    [Fact]
    public void Dma1RejectsMalformedHeaderLengthAndBooleanBytesBeforeMutation()
    {
        using var memory = new PhysicalMemoryBus(1024 * 1024);
        Dma8237 dma = Create(memory, out IoBus io);
        Program(io, false, 0x1234, 0x79);
        byte[] saved = dma.Capture();
        List<byte[]> malformed = [saved[..^1], [.. saved, 0], []];
        foreach (int offset in new[] { 0, 4, 6, 104, 105 })
        {
            byte[] changed = (byte[])saved.Clone();
            changed[offset] = 0xFF;
            malformed.Add(changed);
        }
        for (int channel = 0; channel < 8; channel++)
        {
            foreach (int field in new[] { 10, 11 })
            {
                byte[] changed = (byte[])saved.Clone();
                changed[8 + 12 * channel + field] = 2;
                malformed.Add(changed);
            }
        }
        foreach (byte[] invalid in malformed)
        {
            Assert.Throws<InvalidDataException>(() => Dma8237.ValidateSnapshot(invalid));
            Assert.Throws<InvalidDataException>(() => dma.Restore(invalid));
            Assert.Equal(saved, dma.Capture());
        }
    }

    [Fact]
    public void AddressCountAndPagePortsRoundTripAllEightChannels()
    {
        using var memory = new PhysicalMemoryBus(1024 * 1024);
        Dma8237 source = Create(memory, out IoBus io);
        Dma8237 target = Create(memory, out IoBus targetIo);
        ushort[] pagePorts = [0x87, 0x83, 0x81, 0x82, 0x8F, 0x8B, 0x89, 0x8A];
        for (int channel = 0; channel < 8; channel++)
        {
            ushort addressPort = (ushort)(channel < 4 ? channel * 2 : 0xC0 + (channel - 4) * 4);
            ushort countPort = (ushort)(addressPort + (channel < 4 ? 1 : 2));
            io.WriteByte(addressPort, (byte)(0x10 + channel));
            io.WriteByte(addressPort, (byte)(0x20 + channel));
            io.WriteByte(countPort, (byte)(0x30 + channel));
            io.WriteByte(countPort, (byte)(0x40 + channel));
            io.WriteByte(pagePorts[channel], (byte)(0x50 + channel));
        }
        byte[] saved = source.Capture();
        target.Restore(saved);
        Assert.Equal(saved, target.Capture());
        for (int channel = 0; channel < 8; channel++)
        {
            Assert.Equal((ushort)(0x2010 + channel * 0x101), target[channel].BaseAddress);
            Assert.Equal((ushort)(0x4030 + channel * 0x101), target[channel].BaseCount);
            Assert.Equal(target[channel].BaseAddress, target[channel].CurrentAddress);
            Assert.Equal(target[channel].BaseCount, target[channel].CurrentCount);
            Assert.Equal(0x50 + channel, targetIo.ReadByte(pagePorts[channel]));
        }
    }

}
