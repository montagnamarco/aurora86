using Aurora86.Core;
using Aurora86.Core.Configuration;
using Aurora86.Core.Devices.Cards;
using Aurora86.Core.Devices.Interrupts;
using Aurora86.Core.Devices.Network;
using Aurora86.Core.Devices.Pci;
using Aurora86.Core.Devices.Storage;
using Aurora86.Core.IO;
using Aurora86.Core.Memory;

namespace Aurora86.Tests;

/// <summary>
/// The RTL8139(C) PCI Fast Ethernet card: PCI identity and BAR sizing, the
/// bus-mastered receive ring and transmit descriptors, the coordinator's
/// ISR bit order (D3), and coexistence with the rest of the machine. See
/// <c>plans/specs/rtl8029-rtl8139.md</c> section 2 for the register map
/// this exercises and its six required RTL8139C test cases.
/// </summary>
public class Rtl8139Tests
{
    private const ushort FunctionAddress = 0x0028;
    private const ushort Base = 0x0300;
    private const int RamSize = 8 * 1024 * 1024;

    /// <summary>
    /// The bespoke harness's own addressable space -- bigger than
    /// <see cref="RamSize"/> so a test can place a PCI memory BAR (see
    /// <see cref="PciIdentityAndBothBarsDecodeTheSameRegisterFile"/>) above
    /// installed RAM, exactly as <c>PciBarTests</c> does.
    /// </summary>
    private const int AddressableBytes = 16 * 1024 * 1024;

    // ISR/IMR bit order, coordinator decision D3: RxOK=0, RxErr=1, TxOK=2, TxErr=3.
    private const ushort IsrReceiveOk = 0x0001;
    private const ushort IsrTransmitOk = 0x0004;
    private const ushort IsrReceiveBufferOverflow = 0x0010;

    private const int CommandOffset = 0x37;
    private const byte CommandBufferEmpty = 0x01;
    private const byte CommandReceiveEnable = 0x08;
    private const byte CommandTransmitEnable = 0x04;
    private const byte CommandReset = 0x10;
    private const int RcrOffset = 0x44;
    private const int CaprOffset = 0x38;
    private const int CbrOffset = 0x3A;
    private const int ImrOffset = 0x3C;
    private const int IsrOffset = 0x3E;
    private const int RbStartOffset = 0x30;
    private const int Tsad0Offset = 0x20;
    private const int Tsd0Offset = 0x10;

    private sealed class RecordingBackend : INetworkBackend
    {
        public List<byte[]> Sent { get; } = [];
        public Queue<byte[]> Incoming { get; } = [];

        public void Send(ReadOnlySpan<byte> frame)
        {
            Sent.Add(frame.ToArray());
        }

        public bool TryReceive(Span<byte> buffer, out int length)
        {
            if (Incoming.Count == 0)
            {
                length = 0;
                return false;
            }

            byte[] frame = Incoming.Dequeue();
            frame.CopyTo(buffer);
            length = frame.Length;
            return true;
        }
    }

    private static (Rtl8139 Device, PciBus Pci, IoBus Io, PhysicalMemoryBus Memory,
        PciInterruptRouter Router, InterruptController Pic, RecordingBackend Backend) Build()
    {
        var pci = new PciBus();
        var io = new IoBus();
        var memory = new PhysicalMemoryBus(RamSize, AddressableBytes);
        memory.A20Enabled = true; // this suite is about the register file and the ring, not the 8086 wrap
        var pic = new InterruptController();
        pic.Register(io);
        InitializePic(io);

        var bridge = new PciFunction(8, 0x8086, 0x122E, 0x060100);
        pci.Add(bridge);
        var router = new PciInterruptRouter(pic, bridge);
        pci.InterruptRouter = router;
        pci.DmaMemory = new PhysicalDmaMemory(memory);

        var backend = new RecordingBackend();
        var device = new Rtl8139(pci, io, memory, backend, FunctionAddress, Base);
        return (device, pci, io, memory, router, pic, backend);
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

    private static void EnableIoAndBusMaster(PciBus pci)
    {
        pci.WriteConfiguration(FunctionAddress, 4, 2, 0x05); // I/O space + bus master
    }

    // ------------------------------------------------------------ test case 1

    [Fact]
    public void PciIdentityAndBothBarsDecodeTheSameRegisterFile()
    {
        (Rtl8139 device, PciBus pci, IoBus io, PhysicalMemoryBus memory, _, _, _) = Build();

        Assert.Equal(0x10ECu, pci.ReadConfiguration(FunctionAddress, 0, 2));
        Assert.Equal(0x8139u, pci.ReadConfiguration(FunctionAddress, 2, 2));
        Assert.Equal(0x020000u, pci.ReadConfiguration(FunctionAddress, 8, 4) >> 8);
        Assert.Equal(0x10u, pci.ReadConfiguration(FunctionAddress, 8, 1)); // revision, 8139too's 8139C entry
        Assert.Equal(1u, pci.ReadConfiguration(FunctionAddress, 0x3D, 1)); // INTA

        // BAR0 sizes to 256 bytes.
        pci.WriteConfiguration(FunctionAddress, 0x10, 4, uint.MaxValue);
        uint bar0Mask = pci.ReadConfiguration(FunctionAddress, 0x10, 4) & 0xFFFFFFFEu;
        Assert.Equal(0x100u, ~bar0Mask + 1);
        pci.WriteConfiguration(FunctionAddress, 0x10, 4, Base | 1u);

        // BAR1 sizes to 4 KB -- the smallest this bus's page-granular
        // TryMapDevice can place, since the real chip's 256-byte BAR1
        // cannot decode on it. See Rtl8139's own summary and docs/RTL8139.md.
        pci.WriteConfiguration(FunctionAddress, 0x14, 4, uint.MaxValue);
        uint bar1Size = (~(pci.ReadConfiguration(FunctionAddress, 0x14, 4) & 0xFFFFFFF0u)) + 1;
        Assert.Equal(0x1000u, bar1Size);

        const uint memoryBase = 0x900000; // above RamSize, inside the harness's own AddressableBytes
        pci.WriteConfiguration(FunctionAddress, 0x14, 4, memoryBase);
        EnableIoAndBusMaster(pci);
        pci.WriteConfiguration(FunctionAddress, 4, 2, 0x07); // + memory space

        io.WriteByte((ushort)(Base + 0x08), 0x99); // MAR0: plain storage, no gating
        Assert.Equal(0x99, memory.ReadByte(memoryBase + 0x08)); // the same register through the memory BAR

        memory.WriteByte(memoryBase + 0x51, 0x77); // CONFIG0 offset -- still locked, 9346CR is not in config mode
        Assert.Equal(0, io.ReadByte((ushort)(Base + 0x51)));

        io.WriteByte((ushort)(Base + 0x50), 0xC0); // 9346CR = config-write-enable ("11")
        memory.WriteByte(memoryBase + 0x51, 0x77);
        Assert.Equal(0x77, io.ReadByte((ushort)(Base + 0x51))); // now unlocked, and visible through the I/O BAR too

        // Offsets past the real 256-byte decode are an empty bus on the memory BAR.
        Assert.Equal(0xFF, memory.ReadByte(memoryBase + 0x100));
    }

    // ------------------------------------------------------------ test case 2

    [Fact]
    public void ResetSelfClearsAndReloadsTheMac()
    {
        (Rtl8139 device, PciBus pci, IoBus io, _, _, _, _) = Build();
        EnableIoAndBusMaster(pci);

        byte[] mac = device.MacAddress.ToArray();
        for (int i = 0; i < mac.Length; i++)
        {
            Assert.Equal(mac[i], io.ReadByte((ushort)(Base + i)));
        }

        io.WriteByte((ushort)(Base + CommandOffset), CommandTransmitEnable);
        // BUFE (bit 0) is computed, not stored: an untouched ring reads empty
        // whether or not the driver has enabled the transmitter.
        Assert.Equal(CommandTransmitEnable | CommandBufferEmpty, io.ReadByte((ushort)(Base + CommandOffset)));

        io.WriteByte((ushort)(Base + CommandOffset), CommandReset);

        Assert.Equal(0, io.ReadByte((ushort)(Base + CommandOffset)) & CommandReset); // self-cleared
        Assert.Equal(0, io.ReadByte((ushort)(Base + CommandOffset)) & CommandTransmitEnable); // TE cleared too
        for (int i = 0; i < mac.Length; i++)
        {
            Assert.Equal(mac[i], io.ReadByte((ushort)(Base + i))); // IDR0-5 reloaded from the synthetic EEPROM
        }
    }

    // ------------------------------------------------------------ test case 3

    [Fact]
    public void ReceivedFrameHeaderSitsAtCaprPlusSixteenWithRokAndAnInterrupt()
    {
        (Rtl8139 device, PciBus pci, IoBus io, PhysicalMemoryBus memory, PciInterruptRouter router,
            InterruptController pic, RecordingBackend backend) = Build();
        EnableIoAndBusMaster(pci);
        Assert.True(router.Route(FunctionAddress, 1, 11));

        const uint rbStart = 0x100000;
        io.WriteDword((ushort)(Base + RbStartOffset), rbStart);
        io.WriteDword((ushort)(Base + RcrOffset), 0x0000000F); // AAP|APM|AM|AB, 8 KB ring, WRAP=0
        io.WriteWord((ushort)(Base + ImrOffset), IsrReceiveOk);
        io.WriteByte((ushort)(Base + CommandOffset), CommandReceiveEnable);

        byte[] frame = new byte[60];
        frame[0] = 0xFF; frame[1] = 0xFF; frame[2] = 0xFF; frame[3] = 0xFF; frame[4] = 0xFF; frame[5] = 0xFF; // broadcast
        frame[32] = 0x42;
        backend.Incoming.Enqueue(frame);

        device.Poll();

        Assert.Equal(1, device.FramesReceived);
        ushort headerStatus = memory.ReadWord(rbStart);
        ushort headerLength = memory.ReadWord(rbStart + 2);
        Assert.Equal(0x0001, headerStatus & 0x0001); // ROK
        Assert.Equal(frame.Length + 4, headerLength); // length counts the wire CRC our backend already stripped
        Assert.Equal(0x42, memory.ReadByte(rbStart + 4 + 32));

        int total = (frame.Length + 4 + 3) & ~3;
        Assert.Equal((ushort)total, io.ReadWord((ushort)(Base + CbrOffset)));
        Assert.Equal(IsrReceiveOk, io.ReadWord((ushort)(Base + IsrOffset)) & IsrReceiveOk);
        Assert.Equal(0x2B, pic.GetPendingVector());
    }

    // ------------------------------------------------------------ test case 4

    [Fact]
    public void TransmitViaTsad0Tsd0SetsOwnThenTok()
    {
        (Rtl8139 device, PciBus pci, IoBus io, PhysicalMemoryBus memory, _, _, RecordingBackend backend) = Build();
        EnableIoAndBusMaster(pci);
        io.WriteByte((ushort)(Base + CommandOffset), CommandTransmitEnable);

        const uint bufferAddress = 0x60000;
        byte[] frame = new byte[64];
        frame[12] = 0x08;
        frame[13] = 0x06;
        for (int i = 0; i < frame.Length; i++)
        {
            memory.WriteByte(bufferAddress + (uint)i, frame[i]);
        }

        io.WriteDword((ushort)(Base + Tsad0Offset), bufferAddress);

        uint tsdBefore = io.ReadDword((ushort)(Base + Tsd0Offset));
        Assert.NotEqual(0u, tsdBefore & 0x2000); // OWN set (idle) before the driver hands off the slot

        io.WriteDword((ushort)(Base + Tsd0Offset), (uint)frame.Length); // SIZE, OWN=0: hand off to the chip

        Assert.Single(backend.Sent);
        Assert.Equal(frame, backend.Sent[0]);

        uint tsdAfter = io.ReadDword((ushort)(Base + Tsd0Offset));
        Assert.NotEqual(0u, tsdAfter & 0x2000); // OWN set again
        Assert.NotEqual(0u, tsdAfter & 0x8000); // TOK set
        Assert.Equal(IsrTransmitOk, io.ReadWord((ushort)(Base + IsrOffset)) & IsrTransmitOk);
    }

    // ------------------------------------------------------------ test case 5

    [Fact]
    public void ReceiveOverflowSetsRxovwWithoutWritingPastTheRing()
    {
        (Rtl8139 device, PciBus pci, IoBus io, PhysicalMemoryBus memory, _, _, RecordingBackend backend) = Build();
        EnableIoAndBusMaster(pci);

        const uint rbStart = 0x200000;
        const int ringLength = 8 * 1024; // RBLEN=00
        io.WriteDword((ushort)(Base + RbStartOffset), rbStart);
        io.WriteDword((ushort)(Base + RcrOffset), 0x0000000F); // AAP|APM|AM|AB, WRAP=0
        io.WriteByte((ushort)(Base + CommandOffset), CommandReceiveEnable);

        for (int i = 0; i < ringLength; i++)
        {
            memory.WriteByte(rbStart + (uint)i, 0xCC); // a sentinel pattern the overflow must never disturb
        }

        // Nothing has been received yet, so the chip's own write cursor
        // sits at ring offset zero; telling it the driver has only freed the
        // first 32 bytes (CAPR = 32 - 16) leaves far less free space than
        // the 1500-byte frame below needs, exactly the "ring nearly full"
        // case an overflow must be caught in.
        io.WriteWord((ushort)(Base + CaprOffset), 16);

        byte[] frame = new byte[1500];
        frame[0] = 0xFF; frame[1] = 0xFF; frame[2] = 0xFF; frame[3] = 0xFF; frame[4] = 0xFF; frame[5] = 0xFF;
        backend.Incoming.Enqueue(frame);

        device.Poll();

        Assert.Equal(0, device.FramesReceived);
        Assert.Equal(1, device.FramesDropped);
        Assert.Equal(IsrReceiveBufferOverflow, io.ReadWord((ushort)(Base + IsrOffset)) & IsrReceiveBufferOverflow);
        for (int i = 0; i < ringLength; i++)
        {
            Assert.Equal(0xCC, memory.ReadByte(rbStart + (uint)i)); // untouched: nothing was written past the freed space
        }
    }

    // ------------------------------------------------------------ test case 6

    [Fact]
    public void ClearingBusMasterBlocksBothTransmitAndReceiveDma()
    {
        (Rtl8139 device, PciBus pci, IoBus io, PhysicalMemoryBus memory, _, _, RecordingBackend backend) = Build();
        pci.WriteConfiguration(FunctionAddress, 4, 2, 0x01); // I/O space only, no bus master
        io.WriteByte((ushort)(Base + CommandOffset), (byte)(CommandTransmitEnable | CommandReceiveEnable));

        const uint bufferAddress = 0x61000;
        memory.WriteByte(bufferAddress, 0x11);
        io.WriteDword((ushort)(Base + Tsad0Offset), bufferAddress);
        io.WriteDword((ushort)(Base + Tsd0Offset), 64);

        Assert.Empty(backend.Sent);
        uint tsd = io.ReadDword((ushort)(Base + Tsd0Offset));
        Assert.Equal(0u, tsd & 0x2000); // OWN stayed cleared: the descriptor was left exactly as the driver wrote it

        io.WriteDword((ushort)(Base + RbStartOffset), 0x300000);
        io.WriteDword((ushort)(Base + RcrOffset), 0x0000000F);
        backend.Incoming.Enqueue(new byte[64]);
        device.Poll();

        Assert.Equal(0, device.FramesReceived);
        Assert.Single(backend.Incoming); // left queued, not dequeued-and-lost: Poll never even asked the backend

        pci.WriteConfiguration(FunctionAddress, 4, 2, 0x05); // restore bus mastering
        device.Poll();
        Assert.Equal(1, device.FramesReceived); // the same still-queued frame now lands
    }

    // ------------------------------------------------------------ enumeration

    [Fact]
    public void ConfigurationSelectsRtl8139ThroughTheExistingNetworkEntry()
    {
        var configuration = new VmConfiguration();
        configuration.Hardware.Network =
        [
            new VmConfiguration.NetworkCardEntry { Card = "rtl8139-pci", Port = "C200" },
        ];
        using Machine machine = configuration.BuildMachine();

        Rtl8139Card card = Assert.Single(machine.Cards.OfType<Rtl8139Card>());
        Assert.Equal("network", CardCatalog.KindOf("rtl8139-pci"));
        machine.Pci.WriteConfiguration(FunctionAddress, 4, 2, 1);
        Assert.NotEqual(0xFF, machine.Io.ReadByte(0xC200)); // IDR0 answers, not an empty bus
        Assert.Same(card.Device.Backend, card.Backend);
    }

    [Fact]
    public void RtlAndIsaNetworkCardsCoexistOnDistinctWindows()
    {
        var hardware = new HardwareConfiguration
        {
            Network =
            {
                // C200h-C2FFh: clear of the bus-master IDE engine at C000h
                // and both UHCI controllers at C100h/C120h -- see
                // Rtl8139Card.DefaultPort for why 300h itself will not do.
                new CardSettings { Card = "rtl8139-pci", Port = 0xC200 },
                new CardSettings { Card = "ne2000-isa", Port = 0x300, Irq = 3 },
            },
        };
        using var machine = new Machine(RamSize, hardware);

        Assert.Single(machine.Cards.OfType<Rtl8139Card>());
        Assert.Single(machine.Cards.OfType<Ne2000Card>());
        machine.Pci.WriteConfiguration(FunctionAddress, 4, 2, 1);
        Assert.NotEqual(0xFF, machine.Io.ReadByte(0xC200));
        Assert.NotEqual(0xFF, machine.Io.ReadByte(0x300));
    }

    [Fact]
    public void OverlappingWindowsAreRejectedBeforeAnyHostSocketOpens()
    {
        var hardware = new HardwareConfiguration
        {
            Network =
            {
                new CardSettings { Card = "rtl8139-pci", Port = 0xC200 },
                new CardSettings { Card = "ne2000-isa", Port = 0xC210, Irq = 3 }, // inside the 256-byte RTL8139 window
            },
        };

        Assert.Throws<InvalidOperationException>(() => new Machine(RamSize, hardware));
    }

    // ------------------------------------------------------------ disposal

    private sealed class DisposableBackend : INetworkBackend, IDisposable
    {
        public int Disposals { get; private set; }

        public void Send(ReadOnlySpan<byte> frame)
        {
        }

        public bool TryReceive(Span<byte> buffer, out int length)
        {
            length = 0;
            return false;
        }

        public void Dispose()
        {
            Disposals++;
        }
    }

    [Fact]
    public void DisposingTheMachineClosesTheRtl8139SBackendEvenThoughItIsNotTheLegacyNetworkService()
    {
        var hardware = new HardwareConfiguration
        {
            Network = { new CardSettings { Card = "rtl8139-pci", Port = 0xC200 } },
        };
        var machine = new Machine(RamSize, hardware);
        var backend = new DisposableBackend();
        Assert.Single(machine.Cards.OfType<Rtl8139Card>()).Device.Backend = backend;

        machine.Dispose();

        Assert.Equal(1, backend.Disposals);
    }

    // ------------------------------------------------------------ snapshot

    [Fact]
    public void SnapshotRoundTripsMidReceptionAndTransmission()
    {
        (Rtl8139 device, PciBus pci, IoBus io, PhysicalMemoryBus memory, PciInterruptRouter router,
            _, RecordingBackend backend) = Build();
        EnableIoAndBusMaster(pci);
        Assert.True(router.Route(FunctionAddress, 1, 11));

        const uint rbStart = 0x400000;
        io.WriteDword((ushort)(Base + RbStartOffset), rbStart);
        io.WriteDword((ushort)(Base + RcrOffset), 0x0000000F);
        io.WriteByte((ushort)(Base + CommandOffset), (byte)(CommandReceiveEnable | CommandTransmitEnable));

        byte[] frame = new byte[64];
        frame[0] = 0xFF; frame[1] = 0xFF; frame[2] = 0xFF; frame[3] = 0xFF; frame[4] = 0xFF; frame[5] = 0xFF;
        backend.Incoming.Enqueue(frame);
        device.Poll(); // one frame in flight -- CBR advanced, ISR ROK pending

        const uint txBuffer = 0x410000;
        for (int i = 0; i < 32; i++)
        {
            memory.WriteByte(txBuffer + (uint)i, (byte)i);
        }

        io.WriteDword((ushort)(Base + Tsad0Offset), txBuffer);
        io.WriteDword((ushort)(Base + Tsd0Offset), 32); // slot 0 now shows OWN+TOK

        byte[] snapshot = device.Capture();

        (Rtl8139 restored, PciBus restoredPci, IoBus restoredIo, PhysicalMemoryBus restoredMemory,
            PciInterruptRouter restoredRouter, _, RecordingBackend restoredBackend) = Build();
        EnableIoAndBusMaster(restoredPci);
        Assert.True(restoredRouter.Route(FunctionAddress, 1, 11));
        restored.Restore(snapshot, Rtl8139.SnapshotVersion);

        Assert.Equal(io.ReadWord((ushort)(Base + CbrOffset)), restoredIo.ReadWord((ushort)(Base + CbrOffset)));
        Assert.Equal(IsrReceiveOk, restoredIo.ReadWord((ushort)(Base + IsrOffset)) & IsrReceiveOk);
        Assert.Equal(io.ReadDword((ushort)(Base + Tsd0Offset)), restoredIo.ReadDword((ushort)(Base + Tsd0Offset)));
        Assert.Equal(io.ReadDword((ushort)(Base + RbStartOffset)), restoredIo.ReadDword((ushort)(Base + RbStartOffset)));

        // Restoring did not, itself, replay any DMA: the restored device's
        // own memory bus is untouched until it receives or transmits again.
        Assert.Equal(0, restoredMemory.ReadByte(rbStart));
    }

    [Fact]
    public void SnapshotRejectsAWrongVersionOrLength()
    {
        (Rtl8139 device, _, _, _, _, _, _) = Build();
        byte[] snapshot = device.Capture();

        Assert.Throws<InvalidDataException>(() => device.Restore(snapshot, Rtl8139.SnapshotVersion + 1));
        Assert.Throws<InvalidDataException>(() => device.Restore(snapshot[..^1], Rtl8139.SnapshotVersion));
    }

    // -------------------------------------------------- filters and reset gating

    [Fact]
    public void ReceiveConfigurationFiltersGateAcceptanceByAapApmAmAb()
    {
        (Rtl8139 device, PciBus pci, IoBus io, PhysicalMemoryBus memory, _, _, RecordingBackend backend) = Build();
        EnableIoAndBusMaster(pci);

        const uint rbStart = 0x500000;
        io.WriteDword((ushort)(Base + RbStartOffset), rbStart);
        io.WriteByte((ushort)(Base + CommandOffset), CommandReceiveEnable);

        // Nothing accepted with every filter bit clear.
        io.WriteDword((ushort)(Base + RcrOffset), 0);
        byte[] unicast = new byte[64];
        device.MacAddress.CopyTo(unicast);
        backend.Incoming.Enqueue(unicast);
        device.Poll();
        Assert.Equal(0, device.FramesReceived);

        // APM alone accepts the physical match.
        io.WriteDword((ushort)(Base + RcrOffset), 0x0002);
        backend.Incoming.Enqueue(unicast);
        device.Poll();
        Assert.Equal(1, device.FramesReceived);

        // A frame to a different unicast address is still refused.
        byte[] otherUnicast = new byte[64];
        otherUnicast[0] = 0x00;
        otherUnicast[1] = 0x11;
        backend.Incoming.Enqueue(otherUnicast);
        device.Poll();
        Assert.Equal(1, device.FramesReceived);
    }

    [Fact]
    public void ReceiveAndTransmitStayOffUntilTheCommandRegisterEnablesThem()
    {
        (Rtl8139 device, PciBus pci, IoBus io, PhysicalMemoryBus memory, _, _, RecordingBackend backend) = Build();
        EnableIoAndBusMaster(pci);

        io.WriteDword((ushort)(Base + RcrOffset), 0x0000000F);
        backend.Incoming.Enqueue(new byte[64]);
        device.Poll(); // RE not set yet
        Assert.Equal(0, device.FramesReceived);

        io.WriteDword((ushort)(Base + Tsad0Offset), 0x520000);
        io.WriteDword((ushort)(Base + Tsd0Offset), 32); // TE not set yet
        Assert.Empty(backend.Sent);
    }

    // ------------------------------------------------------------ DOS boot

    /// <summary>
    /// A whole DOS boots to its prompt with the card fitted and no packet
    /// driver ever probing it -- proof that attaching an RTL8139 (a fixed
    /// port, a PCI function, a bus-master DMA path nothing in CONFIG.SYS
    /// asks for) does not disturb POST, the BIOS or the rest of boot. Skipped
    /// when the image is not present, the usual case on a fresh clone; see
    /// <c>DosApplicationTests</c> for the same pattern.
    /// </summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void AWholeDosBootsToItsPromptWithTheCardFitted()
    {
        string? image = FindImage();
        if (image is null)
        {
            return;
        }

        string copy = Path.Combine(Path.GetTempPath(), $"aurora-rtl8139-boot-{Guid.NewGuid():N}.img");
        File.Copy(image, copy);
        try
        {
            var hardware = HardwareConfiguration.Default;
            hardware.Network = [new CardSettings { Card = "rtl8139-pci" }]; // default port, see Rtl8139Card.DefaultPort
            using var machine = new Machine(16 * 1024 * 1024, hardware);
            machine.AttachDisk(DiskImage.OpenHardDisk(copy));

            for (long done = 0; done < 260_000_000; done += 10_000_000)
            {
                machine.RunInstructions(10_000_000);
            }

            Assert.Contains("C:\\>", machine.GetScreenText());
        }
        finally
        {
            File.Delete(copy);
        }
    }

    /// <summary>The vm-dos622 image, found by walking up from wherever the tests run.</summary>
    private static string? FindImage()
    {
        const string relativePath = "images/vm-dos622/hdd.img";
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
