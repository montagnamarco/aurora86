using Aurora386.Core.Devices.Interrupts;
using Aurora386.Core.Devices.Network;
using Aurora386.Core.Devices.Pci;
using Aurora386.Core.IO;

namespace Aurora386.Tests;

/// <summary>
/// The RTL8029's PCI aperture as a driver sees it: PCI assigns the I/O BAR,
/// then the NE2000-compatible registers and Realtek page three share it.
/// </summary>
public class Rtl8029Tests
{
    private const ushort FunctionAddress = 0x0028;
    private const ushort Base = 0x0300;
    private const ushort RelocatedBase = 0xC400;
    private const byte ReceiveInterrupt = 0x01;

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

    private sealed class OccupiedPort : IIoDevice
    {
        public byte ReadByte(ushort port)
        {
            return 0xA5;
        }

        public void WriteByte(ushort port, byte value)
        {
        }
    }

    private static (Rtl8029 Device, PciBus Pci, IoBus Io, PciInterruptRouter Router,
        InterruptController Pic, RecordingBackend Backend) Build()
    {
        var pci = new PciBus();
        var io = new IoBus();
        var pic = new InterruptController();
        pic.Register(io);
        InitializePic(io);

        var bridge = new PciFunction(8, 0x8086, 0x122E, 0x060100);
        pci.Add(bridge);
        var router = new PciInterruptRouter(pic, bridge);
        pci.InterruptRouter = router;

        var backend = new RecordingBackend();
        var device = new Rtl8029(pci, io, backend, FunctionAddress, Base);
        return (device, pci, io, router, pic, backend);
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

    private static void EnableIo(PciBus pci)
    {
        pci.WriteConfiguration(FunctionAddress, 4, 2, 1);
    }

    private static void SelectPage(IoBus io, ushort basePort, byte page)
    {
        io.WriteByte(basePort, (byte)(0x21 | (page << 6)));
    }

    private static void SetWordTransferMode(IoBus io, ushort basePort)
    {
        SelectPage(io, basePort, 0);
        io.WriteByte((ushort)(basePort + 0x0E), 0x49);
    }

    private static void BeginRemoteRead(IoBus io, ushort basePort, ushort address, ushort count)
    {
        SelectPage(io, basePort, 0);
        io.WriteByte((ushort)(basePort + 8), (byte)address);
        io.WriteByte((ushort)(basePort + 9), (byte)(address >> 8));
        io.WriteByte((ushort)(basePort + 10), (byte)count);
        io.WriteByte((ushort)(basePort + 11), (byte)(count >> 8));
        io.WriteByte(basePort, 0x0A);
    }

    private static void BeginRemoteWrite(IoBus io, ushort basePort, ushort address, ushort count)
    {
        SelectPage(io, basePort, 0);
        io.WriteByte((ushort)(basePort + 8), (byte)address);
        io.WriteByte((ushort)(basePort + 9), (byte)(address >> 8));
        io.WriteByte((ushort)(basePort + 10), (byte)count);
        io.WriteByte((ushort)(basePort + 11), (byte)(count >> 8));
        io.WriteByte(basePort, 0x12);
    }

    private static void ConfigureForReceive(IoBus io, ushort basePort)
    {
        SelectPage(io, basePort, 0);
        io.WriteByte((ushort)(basePort + 0x0C), 0x10);
        io.WriteByte((ushort)(basePort + 0x0F), ReceiveInterrupt);
        io.WriteByte(basePort, 0x22);
    }

    [Fact]
    public void PciIdentityBarSizingRelocationDecodeAndCollisionFollowTheBusContract()
    {
        (_, PciBus pci, IoBus io, _, _, _) = Build();

        Assert.Equal(0x10ECu, pci.ReadConfiguration(FunctionAddress, 0, 2));
        Assert.Equal(0x8029u, pci.ReadConfiguration(FunctionAddress, 2, 2));
        Assert.Equal(0x020000u, pci.ReadConfiguration(FunctionAddress, 8, 4) >> 8);
        Assert.Equal(0x0200u, pci.ReadConfiguration(FunctionAddress, 6, 2));
        Assert.Equal(1u, pci.ReadConfiguration(FunctionAddress, 0x3D, 1));
        Assert.Equal(0x21, io.ReadByte(Base));

        pci.WriteConfiguration(FunctionAddress, 0x10, 4, uint.MaxValue);
        Assert.Equal(0xFFFFFFE1u, pci.ReadConfiguration(FunctionAddress, 0x10, 4));
        Assert.Equal(0xFF, io.ReadByte(Base));

        pci.WriteConfiguration(FunctionAddress, 0x10, 4, RelocatedBase | 1u);
        Assert.Equal(0xFF, io.ReadByte(Base));
        Assert.Equal(0x21, io.ReadByte(RelocatedBase));
        Assert.Equal(0xFF, io.ReadByte((ushort)(RelocatedBase + 0x20)));

        pci.WriteConfiguration(FunctionAddress, 4, 2, 0);
        Assert.Equal(0xFF, io.ReadByte(RelocatedBase));
        EnableIo(pci);
        Assert.Equal(0x21, io.ReadByte(RelocatedBase));

        var occupied = new OccupiedPort();
        io.Register(0xD000, 32, occupied);
        pci.WriteConfiguration(FunctionAddress, 0x10, 4, 0xD001);
        Assert.Equal(0xA5, io.ReadByte(0xD000));
    }

    [Fact]
    public void PageThreeMasksReloadAndPowerProgrammingAreDriverVisibleStateOnly()
    {
        (Rtl8029 device, PciBus pci, IoBus io, _, _, _) = Build();
        EnableIo(pci);
        SelectPage(io, Base, 3);

        Assert.Equal(0, io.ReadByte((ushort)(Base + 3)));
        Assert.Equal(0x29, io.ReadByte((ushort)(Base + 0x0E)));
        Assert.Equal(0x80, io.ReadByte((ushort)(Base + 0x0F)));
        io.WriteByte((ushort)(Base + 5), 0xE0);
        Assert.Equal(0, io.ReadByte((ushort)(Base + 5)));

        io.WriteByte((ushort)(Base + 1), 0xC0);
        io.WriteByte((ushort)(Base + 5), 0xFF);
        io.WriteByte((ushort)(Base + 6), 0xFF);
        Assert.Equal(0xE0, io.ReadByte((ushort)(Base + 5)));
        Assert.Equal(0x46, io.ReadByte((ushort)(Base + 6)));
        Assert.Equal(0x04, io.ReadByte((ushort)(Base + 3)));

        io.WriteByte((ushort)(Base + 1), 0x40);
        Assert.Equal(0x21, io.ReadByte(Base));
        SelectPage(io, Base, 3);
        Assert.Equal(0, io.ReadByte((ushort)(Base + 1)));
        Assert.Equal(0, io.ReadByte((ushort)(Base + 5)));
        Assert.Equal(0, io.ReadByte((ushort)(Base + 6)));
        Assert.Equal(Base | 1u, pci.ReadConfiguration(FunctionAddress, 0x10, 4));

        io.WriteByte((ushort)(Base + 1), 0xC0);
        io.WriteByte((ushort)(Base + 6), 0x04); // SLEEP alone cannot unlock HLTCLK.
        io.WriteByte((ushort)(Base + 9), 0x48);
        Assert.Equal(0xFF, device.Capture()[^1]);
        io.WriteByte((ushort)(Base + 6), 0x02);
        io.WriteByte((ushort)(Base + 9), 0x48);
        io.WriteByte((ushort)(Base + 9), 0x42);
        Assert.Equal(0, io.ReadByte((ushort)(Base + 9)));
        Assert.Equal(0x48, device.Capture()[^1]);
        io.WriteByte((ushort)(Base + 9), 0x52);
        Assert.Equal(0x52, device.Capture()[^1]);
    }

    [Fact]
    public void EveryResetAliasPreservesConfigurationButFullResetRestoresTheFactoryValues()
    {
        (Rtl8029 device, PciBus pci, IoBus io, _, _, _) = Build();
        EnableIo(pci);
        SelectPage(io, Base, 3);
        io.WriteByte((ushort)(Base + 1), 0xC0);
        io.WriteByte((ushort)(Base + 5), 0xE0);

        for (ushort offset = 0x18; offset <= 0x1F; offset++)
        {
            SelectPage(io, Base, 3);
            Assert.Equal(0, io.ReadByte((ushort)(Base + offset)));
            SelectPage(io, Base, 3);
            io.WriteByte((ushort)(Base + offset), 0xA5);
        }

        SelectPage(io, Base, 3);
        Assert.Equal(0xE0, io.ReadByte((ushort)(Base + 5)));
        Assert.Equal(Base | 1u, pci.ReadConfiguration(FunctionAddress, 0x10, 4));

        device.Reset();
        SelectPage(io, Base, 3);
        Assert.Equal(0, io.ReadByte((ushort)(Base + 5)));
        Assert.Equal(0xFF, device.Capture()[^1]);
        Assert.Equal(Base | 1u, pci.ReadConfiguration(FunctionAddress, 0x10, 4));
    }

    [Fact]
    public void PromAndRamDmaWidthsUseTheIoBusWithoutLosingTheRemoteCursor()
    {
        (Rtl8029 device, PciBus pci, IoBus io, _, _, _) = Build();
        EnableIo(pci);

        BeginRemoteRead(io, Base, 0, 32);
        byte[] prom = new byte[32];
        for (int index = 0; index < prom.Length; index++)
        {
            prom[index] = io.ReadByte((ushort)(Base + 0x10));
        }

        for (int index = 0; index < 6; index++)
        {
            Assert.Equal(device.Core.MacAddress[index], prom[index * 2]);
            Assert.Equal(device.Core.MacAddress[index], prom[index * 2 + 1]);
        }

        Assert.All(prom[28..], value => Assert.Equal(0x57, value));

        // Linux uses WTS=1 and byte I/O, then takes the first six bytes directly.
        SetWordTransferMode(io, Base);
        BeginRemoteRead(io, Base, 0, 32);
        for (int index = 0; index < 6; index++)
        {
            Assert.Equal(device.Core.MacAddress[index], io.ReadByte((ushort)(Base + 0x10)));
        }

        SetWordTransferMode(io, Base);
        BeginRemoteRead(io, Base, 0, 32);
        for (int i = 0; i < 6; i++)
        {
            ushort word = io.ReadWord((ushort)(Base + 0x10));
            Assert.Equal(device.Core.MacAddress[i], (byte)word);
            Assert.Equal(device.Core.MacAddress[i], (byte)(word >> 8));
        }

        SetWordTransferMode(io, Base);
        BeginRemoteWrite(io, Base, 0x4000, 4);
        io.WriteDword((ushort)(Base + 0x10), 0x78563412);
        Assert.Equal(0x12, device.Core.ReadCardMemory(0x4000));
        Assert.Equal(0x34, device.Core.ReadCardMemory(0x4001));
        Assert.Equal(0x56, device.Core.ReadCardMemory(0x4002));
        Assert.Equal(0x78, device.Core.ReadCardMemory(0x4003));
        BeginRemoteRead(io, Base, 0x4000, 4);
        Assert.Equal(0x78563412u, io.ReadDword((ushort)(Base + 0x10)));
    }

    [Fact]
    public void RelocatedWindowCarriesTransmitAndReceiveRingDriverSequences()
    {
        (Rtl8029 device, PciBus pci, IoBus io, _, _, RecordingBackend backend) = Build();
        EnableIo(pci);
        pci.WriteConfiguration(FunctionAddress, 0x10, 4, RelocatedBase | 1u);

        byte[] frame = new byte[64];
        frame[12] = 0x08;
        frame[13] = 0x06;
        for (int i = 0; i < frame.Length; i++)
        {
            device.Core.WriteCardMemory(0x4000 + i, frame[i]);
        }

        io.WriteByte((ushort)(RelocatedBase + 4), 0x40);
        io.WriteByte((ushort)(RelocatedBase + 5), (byte)frame.Length);
        io.WriteByte((ushort)(RelocatedBase + 6), 0);
        io.WriteByte(RelocatedBase, 0x06);
        Assert.Single(backend.Sent);
        Assert.Equal(frame, backend.Sent[0]);

        ConfigureForReceive(io, RelocatedBase);
        frame[14] = 0x77;
        backend.Incoming.Enqueue(frame);
        device.Poll();
        Assert.Equal(1, device.Core.FramesReceived);
        Assert.Equal(ReceiveInterrupt, io.ReadByte((ushort)(RelocatedBase + 7)) & ReceiveInterrupt);
        Assert.Equal(0x77, device.Core.ReadCardMemory(0x4700 + 4 + 14));
    }

    [Fact]
    public void RtlInterruptAcknowledgementReroutingAndSharedSourcesUsePirqLevels()
    {
        (Rtl8029 device, PciBus pci, IoBus io, PciInterruptRouter router, InterruptController pic,
            RecordingBackend backend) = Build();
        EnableIo(pci);
        Assert.True(router.Route(FunctionAddress, 1, 11));
        Assert.True(router.Route(0x0020, 1, 11));
        ConfigureForReceive(io, Base);
        backend.Incoming.Enqueue(new byte[64]);
        device.Poll();
        Assert.Equal(0x2B, pic.GetPendingVector());

        router.SetInterrupt(0x0020, 1, true);
        io.WriteByte((ushort)(Base + 7), ReceiveInterrupt);
        io.WriteByte(0xA0, 0x20);
        io.WriteByte(0x20, 0x20);
        Assert.Equal(0x2B, pic.GetPendingVector());

        Assert.True(router.Route(0x0020, 1, 10));
        io.WriteByte(0xA0, 0x20);
        io.WriteByte(0x20, 0x20);
        Assert.Equal(0x2A, pic.GetPendingVector());
        router.SetInterrupt(0x0020, 1, false);
    }

    [Fact]
    public void DisabledIoDecodeDoesNotStopReceptionOrAnAlreadyAssertedInterrupt()
    {
        (Rtl8029 device, PciBus pci, IoBus io, PciInterruptRouter router, InterruptController pic,
            RecordingBackend backend) = Build();
        EnableIo(pci);
        Assert.True(router.Route(FunctionAddress, 1, 11));
        ConfigureForReceive(io, Base);
        backend.Incoming.Enqueue(new byte[64]);
        pci.WriteConfiguration(FunctionAddress, 4, 2, 0);
        device.Poll();

        Assert.Equal(1, device.Core.FramesReceived);
        Assert.Equal(0xFF, io.ReadByte(Base));
        Assert.Equal(0x2B, pic.GetPendingVector());
    }

    [Fact]
    public void SnapshotRestoresPartialDmaPageThreeAndAssertedInterruptWithoutChangingTheBar()
    {
        (Rtl8029 device, PciBus pci, IoBus io, PciInterruptRouter router, _, RecordingBackend backend) = Build();
        EnableIo(pci);
        pci.WriteConfiguration(FunctionAddress, 0x10, 4, RelocatedBase | 1u);
        Assert.True(router.Route(FunctionAddress, 1, 11));

        ConfigureForReceive(io, RelocatedBase);
        backend.Incoming.Enqueue(new byte[64]);
        device.Poll();

        SetWordTransferMode(io, RelocatedBase);
        BeginRemoteRead(io, RelocatedBase, 0, 32);
        Assert.Equal(device.Core.MacAddress[0], io.ReadByte((ushort)(RelocatedBase + 0x10)));
        SelectPage(io, RelocatedBase, 3);
        io.WriteByte((ushort)(RelocatedBase + 1), 0xC0);
        io.WriteByte((ushort)(RelocatedBase + 5), 0xE0);
        byte[] snapshot = device.Capture();

        (Rtl8029 restored, PciBus restoredPci, IoBus restoredIo, PciInterruptRouter restoredRouter,
            InterruptController restoredPic, _) = Build();
        EnableIo(restoredPci);
        restoredPci.WriteConfiguration(FunctionAddress, 0x10, 4, RelocatedBase | 1u);
        Assert.True(restoredRouter.Route(FunctionAddress, 1, 11));
        restored.Restore(snapshot, Rtl8029.SnapshotVersion);

        Assert.Equal(RelocatedBase | 1u, restoredPci.ReadConfiguration(FunctionAddress, 0x10, 4));
        Assert.Equal(0x2B, restoredPic.GetPendingVector());
        SelectPage(restoredIo, RelocatedBase, 3);
        Assert.Equal(0xE0, restoredIo.ReadByte((ushort)(RelocatedBase + 5)));
        SelectPage(restoredIo, RelocatedBase, 0);
        Assert.Equal(restored.Core.MacAddress[1], restoredIo.ReadByte((ushort)(RelocatedBase + 0x10)));
    }

    [Fact]
    public void SnapshotValidationRejectsWrongEnvelopeBeforeRestore()
    {
        (Rtl8029 device, PciBus pci, IoBus io, _, _, _) = Build();
        EnableIo(pci);
        SelectPage(io, Base, 3);
        io.WriteByte((ushort)(Base + 1), 0xC0);
        io.WriteByte((ushort)(Base + 5), 0xE0);
        byte[] saved = device.Capture();

        Assert.Throws<InvalidDataException>(() => Rtl8029.ValidateSnapshot(saved[..^1], Rtl8029.SnapshotVersion));
        Assert.Throws<InvalidDataException>(() => Rtl8029.ValidateSnapshot(saved, Rtl8029.SnapshotVersion + 1));
        saved[^2] = 0x01;
        Assert.Throws<InvalidDataException>(() => device.Restore(saved, Rtl8029.SnapshotVersion));
        Assert.Equal(0xE0, io.ReadByte((ushort)(Base + 5)));
        Assert.Equal(Base | 1u, pci.ReadConfiguration(FunctionAddress, 0x10, 4));
    }
}
