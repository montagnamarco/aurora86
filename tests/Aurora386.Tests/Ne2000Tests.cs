using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Devices.Input;
using Aurora386.Core.Devices.Interrupts;
using Aurora386.Core.Devices.Timing;
using Aurora386.Core.Devices.System;
using Aurora386.Core.Devices.Network;

namespace Aurora386.Tests;

/// <summary>
/// Tests for the NE2000 Ethernet card.
///
/// The register interface is what DOS drivers actually observe, so these
/// tests exercise it the way a driver would: select a page, poke registers,
/// move bytes through the remote DMA window.
/// </summary>
public class Ne2000Tests
{
    private const ushort Base = 0x300;

    /// <summary>A backend that records what was sent and replays what it is given.</summary>
    private sealed class RecordingBackend : INetworkBackend
    {
        public List<byte[]> Sent { get; } = new();
        public Queue<byte[]> Incoming { get; } = new();

        public void Send(ReadOnlySpan<byte> frame) => Sent.Add(frame.ToArray());

        public bool TryReceive(Span<byte> buffer, out int length)
        {
            if (Incoming.Count == 0) { length = 0; return false; }

            byte[] frame = Incoming.Dequeue();
            frame.CopyTo(buffer);
            length = frame.Length;
            return true;
        }
    }

    private static (Ne2000 Card, RecordingBackend Backend) Build()
    {
        var interrupts = new InterruptController();
        var backend = new RecordingBackend();
        return (new Ne2000(interrupts, backend, Base), backend);
    }

    /// <summary>Selects a register page through the command register.</summary>
    private static void SelectPage(Ne2000 card, int page)
        => card.WriteByte(Base + 0x00, (byte)((page << 6) | 0x02));   // page | START

    [Fact]
    public void ReadingTheResetPortReturnsTheCardToAKnownState()
    {
        (Ne2000 card, _) = Build();

        // Scribble on a register, then reset and check it went back.
        SelectPage(card, 0);
        card.WriteByte(Base + 0x0C, 0x55);          // receive configuration
        card.ReadByte(Base + 0x1F);                 // the reset port

        SelectPage(card, 0);
        Assert.Equal(0x00, card.ReadByte(Base + 0x0C));
    }

    [Fact]
    public void ResetAnnouncesItselfInTheInterruptStatus()
    {
        (Ne2000 card, _) = Build();
        card.ReadByte(Base + 0x1F);

        SelectPage(card, 0);
        // Bit 7 is the reset bit: a polling driver waits for it.
        Assert.Equal(0x80, card.ReadByte(Base + 0x07) & 0x80);
    }

    [Fact]
    public void ThePromAtAddressZeroCarriesTheMacDoubledAndTheNe2000Mark()
    {
        var (card, _) = Build();
        // Remote DMA read of 32 bytes from address 0, as every driver does.
        card.WriteByte(Base + 0x00, 0x21);       // stop, page 0
        card.WriteByte(Base + 0x08, 0x00);       // RSAR = 0
        card.WriteByte(Base + 0x09, 0x00);
        card.WriteByte(Base + 0x0A, 32);         // RBCR = 32
        card.WriteByte(Base + 0x0B, 0);
        card.WriteByte(Base + 0x00, 0x0A);       // remote read, start
        byte[] prom = new byte[32];
        for (int i = 0; i < 32; i++) prom[i] = card.ReadByte(Base + 0x10);

        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(card.MacAddress[i], prom[2 * i]);
            Assert.Equal(card.MacAddress[i], prom[2 * i + 1]);
        }
        Assert.Equal(0x57, prom[28]);
        Assert.Equal(0x57, prom[29]);
    }

    [Fact]
    public void AWordReadOfTheDataPortMovesTwoBytes()
    {
        var (card, _) = Build();
        card.WriteByte(Base + 0x00, 0x21);
        card.WriteByte(Base + 0x08, 0x00);
        card.WriteByte(Base + 0x09, 0x00);
        card.WriteByte(Base + 0x0A, 32);
        card.WriteByte(Base + 0x0B, 0);
        card.WriteByte(Base + 0x00, 0x0A);

        // Read as words, the way a driver does with rep insw: the low byte
        // of word i is byte 2i of the PROM, which is MAC byte i.
        for (int i = 0; i < 6; i++)
        {
            ushort word = card.ReadWord(Base + 0x10);
            Assert.Equal(card.MacAddress[i], (byte)word);
            Assert.Equal(card.MacAddress[i], (byte)(word >> 8));
        }
    }

    [Fact]
    public void InWordModeAByteReadOfTheDataPortConsumesAWord()
    {
        var (card, _) = Build();
        card.WriteByte(Base + 0x00, 0x21);
        card.WriteByte(Base + 0x0E, 0x49);       // DCR: word mode
        card.WriteByte(Base + 0x08, 0x00);
        card.WriteByte(Base + 0x09, 0x00);
        card.WriteByte(Base + 0x0A, 32);
        card.WriteByte(Base + 0x0B, 0);
        card.WriteByte(Base + 0x00, 0x0A);

        // The Crynwr driver's way: sixteen byte reads yield the address once.
        for (int i = 0; i < 6; i++)
            Assert.Equal(card.MacAddress[i], card.ReadByte(Base + 0x10));
    }

    [Fact]
    public void AWordWriteOfTheDataPortStoresTwoBytes()
    {
        var (card, _) = Build();
        card.WriteByte(Base + 0x00, 0x21);
        card.WriteByte(Base + 0x08, 0x00);
        card.WriteByte(Base + 0x09, 0x40);       // RSAR = 0x4000
        card.WriteByte(Base + 0x0A, 4);
        card.WriteByte(Base + 0x0B, 0);
        card.WriteByte(Base + 0x00, 0x12);       // remote write, start
        card.WriteWord(Base + 0x10, 0x3412);
        card.WriteWord(Base + 0x10, 0x7856);
        Assert.Equal(0x12, card.ReadCardMemory(0x4000));
        Assert.Equal(0x34, card.ReadCardMemory(0x4001));
        Assert.Equal(0x56, card.ReadCardMemory(0x4002));
        Assert.Equal(0x78, card.ReadCardMemory(0x4003));
    }

    [Fact]
    public void TheMacAddressIsReadableFromPageOne()
    {
        (Ne2000 card, _) = Build();

        SelectPage(card, 1);
        for (int i = 0; i < 6; i++)
            Assert.Equal(card.MacAddress[i], card.ReadByte((ushort)(Base + 0x01 + i)));
    }

    [Fact]
    public void WritingTheMacAddressChangesWhatTheCardAccepts()
    {
        (Ne2000 card, _) = Build();

        SelectPage(card, 1);
        byte[] mac = [0x02, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE];
        for (int i = 0; i < 6; i++)
            card.WriteByte((ushort)(Base + 0x01 + i), mac[i]);

        Assert.Equal(mac, card.MacAddress.ToArray());
    }

    [Fact]
    public void ThePageBitsOfTheCommandRegisterSelectDifferentRegisters()
    {
        (Ne2000 card, _) = Build();

        // Offset 1 is PSTART on page 0 and the first MAC byte on page 1: the
        // same port, two different registers.
        SelectPage(card, 0);
        card.WriteByte(Base + 0x01, 0x46);

        SelectPage(card, 1);
        card.WriteByte(Base + 0x01, 0x99);

        SelectPage(card, 0);
        Assert.Equal(0x46, card.ReadByte(Base + 0x01));

        SelectPage(card, 1);
        Assert.Equal(0x99, card.ReadByte(Base + 0x01));
    }

    [Fact]
    public void RemoteDmaWritesAndReadsBackTheCardsPrivateMemory()
    {
        (Ne2000 card, _) = Build();
        SelectPage(card, 0);

        // Point the DMA at page 0x50 and hand it four bytes.
        card.WriteByte(Base + 0x08, 0x00);   // start address low
        card.WriteByte(Base + 0x09, 0x50);   // start address high
        card.WriteByte(Base + 0x0A, 0x04);   // byte count low
        card.WriteByte(Base + 0x0B, 0x00);   // byte count high

        foreach (byte value in new byte[] { 0xDE, 0xAD, 0xBE, 0xEF })
            card.WriteByte(Base + 0x10, value);

        // Now read them straight back.
        card.WriteByte(Base + 0x08, 0x00);
        card.WriteByte(Base + 0x09, 0x50);
        card.WriteByte(Base + 0x0A, 0x04);
        card.WriteByte(Base + 0x0B, 0x00);

        Assert.Equal(0xDE, card.ReadByte(Base + 0x10));
        Assert.Equal(0xAD, card.ReadByte(Base + 0x10));
        Assert.Equal(0xBE, card.ReadByte(Base + 0x10));
        Assert.Equal(0xEF, card.ReadByte(Base + 0x10));
    }

    [Fact]
    public void RemoteDmaStopsWhenTheByteCountRunsOut()
    {
        (Ne2000 card, _) = Build();
        SelectPage(card, 0);

        card.WriteByte(Base + 0x08, 0x00);
        card.WriteByte(Base + 0x09, 0x50);
        card.WriteByte(Base + 0x0A, 0x01);   // exactly one byte
        card.WriteByte(Base + 0x0B, 0x00);

        card.WriteByte(Base + 0x10, 0x42);
        card.WriteByte(Base + 0x10, 0x99);   // beyond the count: ignored

        Assert.Equal(0x42, card.ReadCardMemory(0x5000));
        Assert.NotEqual(0x99, card.ReadCardMemory(0x5001));
    }

    [Fact]
    public void FinishingARemoteDmaRaisesTheCompletionInterrupt()
    {
        (Ne2000 card, _) = Build();
        SelectPage(card, 0);

        card.WriteByte(Base + 0x08, 0x00);
        card.WriteByte(Base + 0x09, 0x50);
        card.WriteByte(Base + 0x0A, 0x01);
        card.WriteByte(Base + 0x0B, 0x00);
        card.WriteByte(Base + 0x10, 0x42);

        // Bit 6 of the interrupt status is "remote DMA complete".
        Assert.Equal(0x40, card.ReadByte(Base + 0x07) & 0x40);
    }

    [Fact]
    public void TheTransmitCommandHandsTheFrameToTheBackend()
    {
        (Ne2000 card, RecordingBackend backend) = Build();
        SelectPage(card, 0);

        // Put a frame in the transmit buffer, which starts at page 0x40.
        byte[] frame = new byte[60];
        for (int i = 0; i < 6; i++) frame[i] = 0xFF;    // broadcast
        frame[12] = 0x08; frame[13] = 0x06;             // ARP

        for (int i = 0; i < frame.Length; i++)
            card.WriteCardMemory(0x4000 + i, frame[i]);

        card.WriteByte(Base + 0x04, 0x40);              // transmit page start
        card.WriteByte(Base + 0x05, (byte)frame.Length);
        card.WriteByte(Base + 0x06, 0x00);
        card.WriteByte(Base + 0x00, 0x06);              // START | TXP

        Assert.Single(backend.Sent);
        Assert.Equal(frame.Length, backend.Sent[0].Length);
        Assert.Equal(0x08, backend.Sent[0][12]);
    }

    [Fact]
    public void AReceivedFrameLandsInTheRingBehindItsHeader()
    {
        (Ne2000 card, RecordingBackend backend) = Build();
        SelectPage(card, 0);
        card.WriteByte(Base + 0x0C, 0x10);   // promiscuous: accept everything
        card.WriteByte(Base + 0x00, 0x22);   // page 0, START

        byte[] frame = new byte[64];
        frame[14] = 0x77;
        backend.Incoming.Enqueue(frame);
        card.Poll();

        // CURR started at 0x47, so the frame begins there.
        int address = 0x47 * 256;
        Assert.Equal(0x01, card.ReadCardMemory(address));         // received intact
        Assert.Equal(68, card.ReadCardMemory(address + 2));       // 64 + 4 header
        Assert.Equal(0x77, card.ReadCardMemory(address + 4 + 14));
    }

    [Fact]
    public void CurrentAdvancesByAWholeNumberOfPages()
    {
        (Ne2000 card, RecordingBackend backend) = Build();
        SelectPage(card, 0);
        card.WriteByte(Base + 0x0C, 0x10);
        card.WriteByte(Base + 0x00, 0x22);

        backend.Incoming.Enqueue(new byte[64]);
        card.Poll();

        SelectPage(card, 1);
        // 64 + 4 bytes fits in one 256-byte page, so CURR moves by one.
        Assert.Equal(0x48, card.ReadByte(Base + 0x07));
    }

    [Fact]
    public void ReceivingAFrameRaisesTheReceiveInterrupt()
    {
        (Ne2000 card, RecordingBackend backend) = Build();
        SelectPage(card, 0);
        card.WriteByte(Base + 0x0C, 0x10);
        card.WriteByte(Base + 0x00, 0x22);

        backend.Incoming.Enqueue(new byte[64]);
        card.Poll();

        SelectPage(card, 0);
        Assert.Equal(0x01, card.ReadByte(Base + 0x07) & 0x01);
    }

    [Fact]
    public void FramesAddressedElsewhereAreIgnored()
    {
        (Ne2000 card, RecordingBackend backend) = Build();
        SelectPage(card, 0);
        card.WriteByte(Base + 0x0C, 0x00);   // not promiscuous
        card.WriteByte(Base + 0x00, 0x22);

        // A unicast frame for somebody else's card.
        byte[] frame = new byte[64];
        for (int i = 0; i < 6; i++) frame[i] = 0x11;
        backend.Incoming.Enqueue(frame);
        card.Poll();

        Assert.Equal(0, card.FramesReceived);
    }

    [Fact]
    public void BroadcastFramesAreAcceptedWhenTheDriverAsksForThem()
    {
        (Ne2000 card, RecordingBackend backend) = Build();
        SelectPage(card, 0);
        card.WriteByte(Base + 0x0C, 0x04);   // accept broadcast
        card.WriteByte(Base + 0x00, 0x22);

        byte[] frame = new byte[64];
        for (int i = 0; i < 6; i++) frame[i] = 0xFF;
        backend.Incoming.Enqueue(frame);
        card.Poll();

        // ARP would not work without this, so every driver enables it.
        Assert.Equal(1, card.FramesReceived);
    }

    [Fact]
    public void AStoppedCardDoesNotReceive()
    {
        (Ne2000 card, RecordingBackend backend) = Build();
        SelectPage(card, 0);
        card.WriteByte(Base + 0x0C, 0x10);
        card.WriteByte(Base + 0x00, 0x01);   // STOP

        backend.Incoming.Enqueue(new byte[64]);
        card.Poll();

        // The driver stops the card precisely so it can reconfigure the ring
        // without frames landing underneath it.
        Assert.Equal(0, card.FramesReceived);
    }

    [Fact]
    public void AFullRingDropsFramesInsteadOfOverwriting()
    {
        (Ne2000 card, RecordingBackend backend) = Build();
        SelectPage(card, 0);
        card.WriteByte(Base + 0x0C, 0x10);
        card.WriteByte(Base + 0x00, 0x22);

        // Feed it far more than the 58-page ring can hold without the driver
        // ever advancing BNRY.
        for (int i = 0; i < 200; i++)
            backend.Incoming.Enqueue(new byte[1500]);
        card.Poll();

        Assert.True(card.FramesDropped > 0,
                    "a ring with nowhere left to write must drop frames");
    }

    [Fact]
    public void WritingOneToAnInterruptStatusBitClearsIt()
    {
        (Ne2000 card, _) = Build();
        SelectPage(card, 0);

        // The reset bit is set after construction; acknowledge it.
        Assert.NotEqual(0, card.ReadByte(Base + 0x07) & 0x80);
        card.WriteByte(Base + 0x07, 0x80);
        Assert.Equal(0, card.ReadByte(Base + 0x07) & 0x80);
    }
}
