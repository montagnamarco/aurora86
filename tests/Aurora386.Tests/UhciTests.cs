using Aurora386.Core;
using Aurora386.Core.Configuration;
using Aurora386.Core.Devices.Usb;

namespace Aurora386.Tests;

public class UhciTests
{
    private const ushort Base = 0xC100;
    private const uint FrameList = 0x10000, Queue = 0x11000, Td = 0x12000, Buffer = 0x13000;
    private const uint Active = 1u << 23, Ioc = 1u << 24;
    private sealed class PacketDevice : IUsbDevice
    {
        public byte Address => 1;
        public bool LowSpeed { get; set; }
        public UsbHandshake Handshake = UsbHandshake.Ack;
        public byte[] Data = [0x41, 0x42, 0x43];
        public int Calls;
        public void Reset() { }
        public UsbTransactionResult Transfer(byte pid, byte endpoint, bool data1, Span<byte> data)
        {
            Calls++;
            if (Handshake != UsbHandshake.Ack) return new(Handshake);
            if (pid == 0x69) Data.AsSpan(0, Math.Min(Data.Length, data.Length)).CopyTo(data);
            else Data = data.ToArray();
            return new(UsbHandshake.Ack, pid == 0x69 ? Math.Min(Data.Length, data.Length) : data.Length);
        }
        public byte[] Capture() => BitConverter.GetBytes(Calls);
        public void Restore(ReadOnlySpan<byte> data) => Calls = BitConverter.ToInt32(data);
    }
    private static Machine Ready(IUsbDevice? device = null)
    {
        var m = new Machine(); m.EnableUsb(); m.RunInstructions(3000);
        m.Usb!.Attach(0, null); m.Usb.Attach(1, null);
        m.Io.WriteWord(Base + 16, 0xA); m.Io.WriteWord(Base + 18, 0xA);
        m.Pci.WriteConfiguration(0x10, 4, 2, 5);
        m.Pci.InterruptRouter!.Route(0x10, 1, 11);
        m.Io.WriteByte(0x21, 0); m.Io.WriteByte(0xA1, 0);
        if (device is not null) { m.Usb!.Attach(0, device); m.Io.WriteWord(Base + 16, 14); }
        m.Io.WriteDword(Base + 8, FrameList);
        for (uint i = 0; i < 1024; i++) m.Memory.WriteDword(FrameList + i * 4, Queue | 2);
        m.Memory.WriteDword(Queue, 1); m.Memory.WriteDword(Queue + 4, Td);
        m.Memory.WriteDword(Td, 1); m.Memory.WriteDword(Td + 12, Buffer);
        return m;
    }
    private static void Submit(Machine m, int length = 8, uint flags = 0, byte pid = 0x69)
    {
        m.Memory.WriteDword(Td + 4, Active | (3u << 27) | flags);
        m.Memory.WriteDword(Td + 8, pid | (1u << 8) | (1u << 15) | ((uint)(length - 1) & 0x7FF) << 21);
        m.Io.WriteWord(Base, 1);
    }

    [Fact]
    public void PciBarRelocationAndCommandActuallyControlTheIoDecoder()
    {
        using var m = Ready();
        Assert.Equal(0x70208086u, m.Pci.ReadConfiguration(0x10, 0, 4));
        Assert.Equal(0x20, m.Io.ReadWord(Base + 2));
        m.Pci.WriteConfiguration(0x10, 4, 2, 0);
        Assert.Equal(0xFFFF, m.Io.ReadWord(Base + 2));
        m.Pci.WriteConfiguration(0x10, 0x20, 4, uint.MaxValue);
        Assert.Equal(0xFFFFFFE1u, m.Pci.ReadConfiguration(0x10, 0x20, 4));
        m.Pci.WriteConfiguration(0x10, 0x20, 4, 0xC200);
        m.Pci.WriteConfiguration(0x10, 4, 2, 1);
        Assert.Equal(0xFFFF, m.Io.ReadWord(Base + 2));
        Assert.Equal(0x20, m.Io.ReadWord(0xC202));
    }

    [Fact]
    public void PortsExposeHotplugWordOnlyControlAndWriteOneToClearChanges()
    {
        using var m = Ready();
        Assert.Equal(0x80, m.Io.ReadWord(Base + 16));
        m.Usb!.Attach(0, new PacketDevice { LowSpeed = true });
        Assert.Equal(0x1A3, m.Io.ReadWord(Base + 16));
        m.Io.WriteByte(Base + 16, 6);
        Assert.Equal(0x1A3, m.Io.ReadWord(Base + 16));
        m.Io.WriteWord(Base + 16, 6);
        Assert.Equal(0x1A5, m.Io.ReadWord(Base + 16));
        m.Usb.Attach(0, null);
        Assert.Equal(0x8A, m.Io.ReadWord(Base + 16));
        m.Io.WriteWord(Base + 16, 0xA);
        Assert.Equal(0x80, m.Io.ReadWord(Base + 16));
    }

    [Fact]
    public void AnInTransferWritesDmaAdvancesTheQueueAndInterruptsUntilAcknowledged()
    {
        var device = new PacketDevice(); using var m = Ready(device);
        m.Io.WriteWord(Base + 4, 4);
        Submit(m, flags: Ioc); m.Usb!.Advance(1000);
        Assert.Equal(1, device.Calls);
        Assert.Equal(0x41, m.Memory.ReadByte(Buffer));
        Assert.Equal(2u, m.Memory.ReadDword(Td + 4) & 0x7FF);
        Assert.Equal(0u, m.Memory.ReadDword(Td + 4) & Active);
        Assert.Equal(1u, m.Memory.ReadDword(Queue + 4));
        Assert.Equal(0x73, m.Interrupts.GetPendingVector());
        m.Io.WriteWord(Base + 2, 1);
        m.Io.WriteByte(0xA0, 0x20); m.Io.WriteByte(0x20, 0x20);
        Assert.False(m.Interrupts.HasPendingInterrupt);
    }

    [Fact]
    public void NakRetainsTheQueueAndDoesNotCompleteOrInterrupt()
    {
        var device = new PacketDevice { Handshake = UsbHandshake.Nak }; using var m = Ready(device);
        m.Io.WriteWord(Base + 4, 4);
        Submit(m, flags: Ioc); m.Usb!.Advance(1000);
        Assert.NotEqual(0u, m.Memory.ReadDword(Td + 4) & Active);
        Assert.Equal(3u, (m.Memory.ReadDword(Td + 4) >> 27) & 3);
        Assert.Equal(Td, m.Memory.ReadDword(Queue + 4));
        Assert.False(m.Interrupts.HasPendingInterrupt);
        device.Handshake = UsbHandshake.Ack;
        m.Usb.Advance(1000);
        Assert.Equal(1u, m.Memory.ReadDword(Queue + 4));
        Assert.Equal(0x73, m.Interrupts.GetPendingVector());
    }

    [Fact]
    public void ShortPacketCompletesTheTdButLeavesTheQueueForSoftware()
    {
        using var m = Ready(new PacketDevice());
        m.Io.WriteWord(Base + 4, 8);
        Submit(m, flags: 1u << 29); m.Usb!.Advance(1000);
        Assert.Equal(0u, m.Memory.ReadDword(Td + 4) & Active);
        Assert.Equal(Td, m.Memory.ReadDword(Queue + 4));
        Assert.Equal(0x73, m.Interrupts.GetPendingVector());
    }

    [Fact]
    public void MissingDeviceExhaustsTheProgrammedTimeoutRetries()
    {
        using var m = Ready(); m.Io.WriteWord(Base + 4, 1);
        Submit(m); m.Usb!.Advance(2000);
        Assert.NotEqual(0u, m.Memory.ReadDword(Td + 4) & Active);
        Assert.Equal(1u, (m.Memory.ReadDword(Td + 4) >> 27) & 3);
        m.Usb.Advance(1000);
        Assert.Equal(0u, m.Memory.ReadDword(Td + 4) & Active);
        Assert.NotEqual(0u, m.Memory.ReadDword(Td + 4) & (1u << 22));
        Assert.Equal(0x73, m.Interrupts.GetPendingVector());
    }

    [Fact]
    public void BusMasterDisablePreventsAllDmaAndOutTransfersDeliverGuestBytes()
    {
        var device = new PacketDevice(); using var m = Ready(device);
        m.Memory.WriteByte(Buffer, 0x77);
        Submit(m, 1, pid: 0xE1);
        m.Pci.WriteConfiguration(0x10, 4, 2, 1);
        m.Usb!.Advance(1000);
        Assert.Equal(0, device.Calls);
        Assert.Equal(Td, m.Memory.ReadDword(Queue + 4));
        m.Pci.WriteConfiguration(0x10, 4, 2, 5);
        m.Usb.Advance(1000);
        Assert.Equal(new byte[] { 0x77 }, device.Data);
    }

    [Fact]
    public void FrameNumberWrapsAndCannotBeChangedWhileRunningOrByByteWrites()
    {
        using var m = Ready();
        m.Io.WriteWord(Base + 6, 0x7FF); m.Io.WriteByte(Base + 6, 0);
        Assert.Equal(0x7FF, m.Io.ReadWord(Base + 6));
        m.Io.WriteWord(Base, 1); m.Io.WriteWord(Base + 6, 7);
        m.Usb!.Advance(1000);
        Assert.Equal(0, m.Io.ReadWord(Base + 6));
        m.Io.WriteWord(Base, 2);
        Assert.Equal(0, m.Io.ReadWord(Base));
        Assert.Equal(0x20, m.Io.ReadWord(Base + 2));
    }

    [Fact]
    public void SnapshotRestoresPartialFrameAndDeviceState()
    {
        var device = new PacketDevice(); using var m = Ready(device);
        Submit(m); m.Usb!.Advance(400);
        var saved = MachineSnapshot.Capture(m);
        m.Usb.Advance(600); Assert.Equal(1, device.Calls);
        string path = Path.GetTempFileName();
        try { saved.Save(path); MachineSnapshot.Load(path).RestoreTo(m); }
        finally { File.Delete(path); }
        Assert.Equal(0, device.Calls);
        m.Usb.Advance(599); Assert.Equal(0, device.Calls);
        m.Usb.Advance(1); Assert.Equal(1, device.Calls);
    }

    [Fact]
    public void VmSettingEnablesUsbAndEmulatedTimeAdvancesIt()
    {
        using var m = new VmConfiguration { UsbEnabled = true }.BuildMachine();
        m.RunInstructions(3000);
        Assert.NotNull(m.Usb);
        m.Io.WriteWord(Base, 1);
        m.RunFor(TimeSpan.FromMilliseconds(3));
        Assert.Equal(3, m.Io.ReadWord(Base + 6));
        using var old = new VmConfiguration().BuildMachine();
        Assert.Null(old.Usb);
    }
    [Fact]
    public void InvalidDmaStopsTheControllerAndSignalsHostSystemError()
    {
        using var m = Ready();
        m.Io.WriteDword(Base + 8, 0xFFFFF000);
        m.Io.WriteWord(Base, 1); m.Usb!.Advance(1000);
        Assert.Equal(0, m.Io.ReadWord(Base) & 1);
        Assert.Equal(0x28, m.Io.ReadWord(Base + 2) & 0x38);
        Assert.Equal(0x73, m.Interrupts.GetPendingVector());
    }

    [Fact]
    public void StallCompletesTheTdWithErrorAndRetainsTheQueue()
    {
        using var m = Ready(new PacketDevice { Handshake = UsbHandshake.Stall });
        Submit(m); m.Usb!.Advance(1000);
        Assert.Equal(0u, m.Memory.ReadDword(Td + 4) & Active);
        Assert.NotEqual(0u, m.Memory.ReadDword(Td + 4) & (1u << 22));
        Assert.Equal(Td, m.Memory.ReadDword(Queue + 4));
        Assert.Equal(2, m.Io.ReadWord(Base + 2) & 2);
    }

    [Fact]
    public void ARealHidDeviceEnumeratesThroughTheDmaSchedule()
    {
        var keyboard = new UsbHidDevice(true); using var m = Ready(keyboard);
        void Packet(byte pid, bool toggle, byte address, byte[] output, int length)
        {
            for (int i = 0; i < output.Length; i++) m.Memory.WriteByte(Buffer + (uint)i, output[i]);
            m.Memory.WriteDword(Queue + 4, Td);
            m.Memory.WriteDword(Td + 4, Active | (3u << 27) | (1u << 26));
            m.Memory.WriteDword(Td + 8, pid | (uint)address << 8 | (toggle ? 1u << 19 : 0)
                | (((uint)(length - 1) & 0x7FF) << 21));
            m.Io.WriteWord(Base, 1); m.Usb!.Advance(1000);
            Assert.Equal(0u, m.Memory.ReadDword(Td + 4) & (Active | (1u << 22)));
        }
        Packet(0x2D, false, 0, [0x80,6,0,1,0,0,18,0], 8);
        Packet(0x69, true, 0, [], 8); Assert.Equal(18, m.Memory.ReadByte(Buffer));
        Packet(0x69, false, 0, [], 8); Assert.Equal(0xFE, m.Memory.ReadByte(Buffer));
        Packet(0x69, true, 0, [], 8);
        Packet(0xE1, true, 0, [], 0);
        Packet(0x2D, false, 0, [0,5,3,0,0,0,0,0], 8);
        Assert.Equal(0, keyboard.Address);
        Packet(0x69, true, 0, [], 0); Assert.Equal(3, keyboard.Address);
        Packet(0x2D, false, 3, [0,9,1,0,0,0,0,0], 8);
        Packet(0x69, true, 3, [], 0); Assert.Equal(1, keyboard.Configuration);
    }

}
