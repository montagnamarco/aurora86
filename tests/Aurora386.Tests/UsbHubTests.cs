using System.Buffers.Binary;
using Aurora386.Core;
using Aurora386.Core.Devices.Usb;

namespace Aurora386.Tests;

/// <summary>
/// A USB 1.1 hub: standard and hub-class enumeration, the per-port power
/// and reset sequence, hot attach/detach, forwarding a transaction to
/// whatever sits behind an enabled port -- HID, mass storage, or another
/// hub -- and the hub's own snapshot round trip. See <see
/// cref="UsbHubDevice"/>'s own summary for the design this states, and
/// <c>UhciTests.cs</c> for the same DMA schedule this reuses to prove the
/// hub is reachable through a real UHCI controller, not just directly.
/// </summary>
public class UsbHubTests
{
    private const ushort Base = 0xC100;
    private const uint FrameList = 0x10000, Queue = 0x11000, Td = 0x12000, Buffer = 0x13000;
    private const uint Active = 1u << 23;

    // --- Control transfers driven directly against an IUsbDevice, exactly
    // as UsbHidTests and UsbMassStorageTests already drive their own
    // devices: SETUP, then a data stage sized to what is actually expected,
    // then the matching status stage. A hub's own control endpoint never
    // carries an OUT data stage (every OUT request it accepts is
    // zero-length), so only GET requests need the IN-data helper. ---

    private static void Setup(IUsbDevice d, byte type, byte request, ushort value = 0, ushort index = 0, ushort length = 0)
    {
        byte[] setup = [type, request, (byte)value, (byte)(value >> 8), (byte)index, (byte)(index >> 8), (byte)length, (byte)(length >> 8)];
        Assert.Equal(UsbHandshake.Ack, d.Transfer(0x2D, 0, false, setup).Handshake);
    }

    private static void StatusIn(IUsbDevice d) => Assert.Equal(UsbHandshake.Ack, d.Transfer(0x69, 0, true, []).Handshake);

    private static void StatusOut(IUsbDevice d) => Assert.Equal(UsbHandshake.Ack, d.Transfer(0xE1, 0, true, []).Handshake);

    private static void ControlOut(IUsbDevice d, byte type, byte request, ushort value = 0, ushort index = 0)
    {
        Setup(d, type, request, value, index, 0);
        StatusIn(d);
    }

    private static byte[] GetDescriptor(IUsbDevice d, byte requestType, byte descriptorType, int length)
    {
        Setup(d, requestType, 6, (ushort)(descriptorType << 8), 0, (ushort)length);
        var data = new List<byte>();
        bool toggle = true;
        for (int i = 0; i < (length + 7) / 8; i++)
        {
            byte[] packet = new byte[8];
            var result = d.Transfer(0x69, 0, toggle, packet);
            Assert.Equal(UsbHandshake.Ack, result.Handshake);
            data.AddRange(packet[..result.Length]);
            toggle = !toggle;
        }
        StatusOut(d);
        return data.ToArray();
    }

    private static byte[] GetPortStatus(UsbHubDevice hub, int oneBasedPort)
    {
        Setup(hub, 0xA3, 0, 0, (ushort)oneBasedPort, 4); // bmRequestType Class/Other-in, GET_STATUS
        byte[] reply = new byte[4];
        var result = hub.Transfer(0x69, 0, true, reply);
        Assert.Equal(UsbHandshake.Ack, result.Handshake);
        StatusOut(hub);
        return reply;
    }

    [Fact]
    public void HubEnumeratesStandardAndClassDescriptorsThenAddressesAndConfigures()
    {
        var hub = new UsbHubDevice(4);

        byte[] device = GetDescriptor(hub, 0x80, 1, 18);
        Assert.Equal(new byte[] { 18, 1, 0x10, 1, 9, 0, 0, 8, 0xFE, 0xCA, 4, 0, 0, 1, 0, 0, 0, 1 }, device);

        byte[] configuration = GetDescriptor(hub, 0x80, 2, 25);
        Assert.Equal(
            new byte[]
            {
                9, 2, 25, 0, 1, 1, 0, 0x80, 50,
                9, 4, 0, 0, 1, 9, 0, 0, 0,
                7, 5, 0x81, 3, 1, 0, 255,
            },
            configuration);

        ControlOut(hub, 0, 5, 7); // SET_ADDRESS 7
        Assert.Equal(7, hub.Address);
        ControlOut(hub, 0, 9, 1); // SET_CONFIGURATION 1
        Assert.Equal(1, hub.Configuration);

        // GET_HUB_DESCRIPTOR: bmRequestType 0xA0 (class, device), wValue high
        // byte 0x29 -- a four-port hub's descriptor is 9 bytes (7 fixed plus
        // one byte each for DeviceRemovable and PortPwrCtrlMask).
        byte[] hubDescriptor = GetDescriptor(hub, 0xA0, 0x29, 9);
        Assert.Equal(new byte[] { 9, 0x29, 4, 9, 0, 50, 50, 0, 0xFF }, hubDescriptor);
    }

    [Fact]
    public void PortPowerAndResetSequenceEnablesAKeyboardBehindTheHubWithChangeBits()
    {
        var hub = new UsbHubDevice(4);
        var keyboard = new UsbHidDevice(true);

        // Physically plugged in before the port is even powered: nothing is
        // observable yet, exactly as a real bus-powered port behaves before
        // its driver has powered it.
        hub.Attach(0, keyboard);
        Assert.Equal(0, hub.PortStatus(0));
        Assert.Equal(0, hub.PortChange(0));

        ControlOut(hub, 0x23, 3, 8, 1); // SET_FEATURE(PORT_POWER), port 1 (one-based)
        Assert.True(hub.IsPortPowered(0));
        Assert.Equal(0x301, hub.PortStatus(0)); // POWER | CONNECTION | LOW_SPEED
        Assert.Equal(1, hub.PortChange(0));     // C_PORT_CONNECTION

        ControlOut(hub, 0x23, 1, 16, 1); // CLEAR_FEATURE(C_PORT_CONNECTION)
        Assert.Equal(0, hub.PortChange(0));

        ControlOut(hub, 0x23, 3, 4, 1); // SET_FEATURE(PORT_RESET)
        Assert.Equal(0x311, hub.PortStatus(0)); // ... | RESET, not yet ENABLE
        Assert.Equal(0, hub.PortChange(0));

        hub.Advance(10_000); // TRSTRCY, the reset recovery time
        Assert.Equal(0x303, hub.PortStatus(0)); // RESET clears, ENABLE sets
        Assert.Equal(0x10, hub.PortChange(0));  // C_PORT_RESET

        byte[] status = GetPortStatus(hub, 1);
        Assert.Equal(0x303, BinaryPrimitives.ReadUInt16LittleEndian(status));
        Assert.Equal(0x10, BinaryPrimitives.ReadUInt16LittleEndian(status.AsSpan(2)));
    }

    [Fact]
    public void HotUnplugSetsConnectionChangeAndTheStatusChangeBitmap()
    {
        var hub = new UsbHubDevice(4);
        var keyboard = new UsbHidDevice(true);
        hub.Attach(0, keyboard);
        ControlOut(hub, 0x23, 3, 8, 1);  // power
        ControlOut(hub, 0x23, 1, 16, 1); // clear C_PORT_CONNECTION
        ControlOut(hub, 0x23, 3, 4, 1);  // reset
        hub.Advance(10_000);
        ControlOut(hub, 0x23, 1, 20, 1); // clear C_PORT_RESET
        ControlOut(hub, 0, 5, 9);        // SET_ADDRESS 9, so the interrupt endpoint answers at all
        ControlOut(hub, 0, 9, 1);        // SET_CONFIGURATION 1

        Assert.Equal(UsbHandshake.Nak, hub.Transfer(0x69, 1, false, new byte[1]).Handshake);

        hub.Attach(0, null); // hot unplug

        Assert.Equal(0x100, hub.PortStatus(0));    // POWER only: no device, no enable
        Assert.Equal(0x3, hub.PortChange(0));      // C_PORT_CONNECTION | C_PORT_ENABLE

        byte[] bitmap = new byte[1];
        var result = hub.Transfer(0x69, 1, false, bitmap);
        Assert.Equal(UsbHandshake.Ack, result.Handshake);
        Assert.Equal(0b0000_0010, bitmap[0]); // bit 1: port 1 (one-based) changed, bit 0 (the hub itself) did not

        // The interrupt endpoint only reports that something changed; the
        // change bits themselves stay set -- and so does the bitmap -- until
        // a driver clears each one explicitly, exactly as GET_PORT_STATUS
        // and CLEAR_FEATURE(C_PORT_*) would on real hardware.
        Assert.Equal(UsbHandshake.Ack, hub.Transfer(0x69, 1, true, new byte[1]).Handshake);
        ControlOut(hub, 0x23, 1, 16, 1); // clear C_PORT_CONNECTION
        ControlOut(hub, 0x23, 1, 17, 1); // clear C_PORT_ENABLE
        Assert.Equal(UsbHandshake.Nak, hub.Transfer(0x69, 1, false, new byte[1]).Handshake);
    }

    private static Machine ReadyMachine()
    {
        var m = new Machine();
        m.EnableUsb();
        m.RunInstructions(3000);
        m.Usb!.Attach(0, null);
        m.Usb.Attach(1, null);
        m.Io.WriteWord(Base + 16, 0xA);
        m.Io.WriteWord(Base + 18, 0xA);
        m.Pci.WriteConfiguration(0x10, 4, 2, 5);
        m.Pci.InterruptRouter!.Route(0x10, 1, 11);
        m.Io.WriteByte(0x21, 0);
        m.Io.WriteByte(0xA1, 0);
        m.Io.WriteDword(Base + 8, FrameList);
        for (uint i = 0; i < 1024; i++)
        {
            m.Memory.WriteDword(FrameList + i * 4, Queue | 2);
        }
        m.Memory.WriteDword(Queue, 1);
        m.Memory.WriteDword(Queue + 4, Td);
        m.Memory.WriteDword(Td, 1);
        m.Memory.WriteDword(Td + 12, Buffer);
        return m;
    }

    /// <summary>
    /// One TD submitted through the real DMA schedule, exactly the shape
    /// <c>UhciTests</c>' own local <c>Packet</c> helper uses.
    /// <paramref name="lowSpeed"/> matches the TD's own LS bit against
    /// whichever device the address actually resolves to -- the hub itself
    /// is full speed, but the boot keyboard behind it is not, and both are
    /// addressed with the same helper in the same test.
    /// </summary>
    private static byte[] Packet(Machine m, byte pid, bool toggle, byte address, byte endpoint, byte[] output, int length, bool lowSpeed = false)
    {
        for (int i = 0; i < output.Length; i++)
        {
            m.Memory.WriteByte(Buffer + (uint)i, output[i]);
        }
        m.Memory.WriteDword(Queue + 4, Td);
        m.Memory.WriteDword(Td + 4, Active | (3u << 27) | (lowSpeed ? 1u << 26 : 0));
        m.Memory.WriteDword(Td + 8, pid | (uint)address << 8 | (uint)endpoint << 15 | (toggle ? 1u << 19 : 0)
            | (((uint)(length - 1) & 0x7FF) << 21));
        m.Io.WriteWord(Base, 1);
        m.Usb!.Advance(1000);
        Assert.Equal(0u, m.Memory.ReadDword(Td + 4) & (Active | (1u << 22)));
        byte[] received = new byte[length];
        for (int i = 0; i < length; i++)
        {
            received[i] = m.Memory.ReadByte(Buffer + (uint)i);
        }
        return received;
    }

    [Fact]
    public void AKeyboardBehindAHubEnumeratesAndReportsThroughARealUhciSchedule()
    {
        using var m = ReadyMachine();
        var hub = new UsbHubDevice(4);
        m.Usb!.Attach(0, hub);
        m.Io.WriteWord(Base + 16, 14); // this controller's own PORTSC bits: connect, low speed, enable

        // Enumerate the hub itself on the root port, exactly as a device
        // plugged in directly would be.
        Packet(m, 0x2D, false, 0, 0, [0x80, 6, 0, 1, 0, 0, 18, 0], 8);
        Packet(m, 0x69, true, 0, 0, [], 8);
        Assert.Equal(18, m.Memory.ReadByte(Buffer));     // bLength
        Assert.Equal(9, m.Memory.ReadByte(Buffer + 4));  // bDeviceClass: HubClassCode
        Packet(m, 0x69, false, 0, 0, [], 8);
        Assert.Equal(0xFE, m.Memory.ReadByte(Buffer));   // idVendor low byte, the synthetic 0xCAFE pair
        Packet(m, 0x69, true, 0, 0, [], 2);
        Packet(m, 0xE1, true, 0, 0, [], 0);
        Packet(m, 0x2D, false, 0, 0, [0, 5, 5, 0, 0, 0, 0, 0], 8); // SET_ADDRESS 5
        Packet(m, 0x69, true, 0, 0, [], 0);
        Assert.Equal(5, hub.Address);
        Packet(m, 0x2D, false, 5, 0, [0, 9, 1, 0, 0, 0, 0, 0], 8); // SET_CONFIGURATION 1
        Packet(m, 0x69, true, 5, 0, [], 0);
        Assert.Equal(1, hub.Configuration);

        // Physically plug the keyboard into the hub's own port 0, then power
        // and reset that port so it starts forwarding.
        var keyboard = new UsbHidDevice(true);
        hub.Attach(0, keyboard);
        Packet(m, 0x2D, false, 5, 0, [0x23, 3, 8, 0, 1, 0, 0, 0], 8); // SET_FEATURE(PORT_POWER)
        Packet(m, 0x69, true, 5, 0, [], 0);
        Packet(m, 0x2D, false, 5, 0, [0x23, 3, 4, 0, 1, 0, 0, 0], 8); // SET_FEATURE(PORT_RESET)
        Packet(m, 0x69, true, 5, 0, [], 0);
        m.Usb.Advance(10_000);
        Assert.NotEqual(0, hub.PortStatus(0) & 0x2); // ENABLE

        // The keyboard, still unaddressed, is now reachable through the hub.
        // Every packet from here on is low speed, matching the boot
        // keyboard's own LowSpeed, which the TD's own LS bit must honour
        // for UhciController.ExecuteTd's device lookup to match it (see
        // that method's own comment on the point).
        Packet(m, 0x2D, false, 0, 0, [0x80, 6, 0, 1, 0, 0, 18, 0], 8, lowSpeed: true);
        byte[] descriptor = Packet(m, 0x69, true, 0, 0, [], 8, lowSpeed: true);
        Assert.Equal(8, descriptor[7]); // bMaxPacketSize0
        Packet(m, 0x69, false, 0, 0, [], 8, lowSpeed: true);
        Packet(m, 0x69, true, 0, 0, [], 8, lowSpeed: true);
        Packet(m, 0xE1, true, 0, 0, [], 0, lowSpeed: true);
        Packet(m, 0x2D, false, 0, 0, [0, 5, 3, 0, 0, 0, 0, 0], 8, lowSpeed: true); // SET_ADDRESS 3
        Packet(m, 0x69, true, 0, 0, [], 0, lowSpeed: true);
        Assert.Equal(3, keyboard.Address);
        Packet(m, 0x2D, false, 3, 0, [0, 9, 1, 0, 0, 0, 0, 0], 8, lowSpeed: true); // SET_CONFIGURATION 1
        Packet(m, 0x69, true, 3, 0, [], 0, lowSpeed: true);
        Assert.Equal(1, keyboard.Configuration);

        keyboard.SetKey(4, true); // usage 4: the "A" key
        byte[] report = Packet(m, 0x69, false, 3, 1, [], 8, lowSpeed: true); // the interrupt toggle starts at DATA0
        Assert.Equal(4, report[2]);
    }

    [Fact]
    public void AMassStorageDeviceBehindAHubReadsThroughBulk()
    {
        var hub = new UsbHubDevice(4);
        var storage = new UsbMassStorageDevice(new MemoryStream(new byte[4096]));
        hub.Attach(0, storage);

        ControlOut(hub, 0x23, 3, 8, 1); // power
        ControlOut(hub, 0x23, 3, 4, 1); // reset
        hub.Advance(10_000);
        Assert.NotEqual(0, hub.PortStatus(0) & 0x2); // ENABLE

        // The storage device's own control endpoint does not care how the
        // SETUP packet reached it; what Find() would have used to route a
        // real one is the port-enable state already checked above.
        ControlOut(storage, 0, 5, 3); // SET_ADDRESS 3
        Assert.Equal(3, storage.Address);
        ControlOut(storage, 0, 9, 1); // SET_CONFIGURATION 1
        Assert.Equal(1, storage.Configuration);
        Assert.Same(storage, hub.Find(3));
        Assert.Null(hub.Find(4)); // no such address anywhere in the tree

        bool input = false, output = false;
        void Write(byte[] data)
        {
            Assert.Equal(UsbHandshake.Ack, storage.Transfer(0xE1, 2, output, data).Handshake);
            output = !output;
        }
        byte[] Read(int capacity)
        {
            byte[] packet = new byte[capacity];
            var result = storage.Transfer(0x69, 1, input, packet);
            Assert.Equal(UsbHandshake.Ack, result.Handshake);
            input = !input;
            return packet[..result.Length];
        }

        byte[] cbw = new byte[31];
        BinaryPrimitives.WriteUInt32LittleEndian(cbw, 0x43425355);
        BinaryPrimitives.WriteUInt32LittleEndian(cbw.AsSpan(4), 0xA5A5A5A5);
        BinaryPrimitives.WriteUInt32LittleEndian(cbw.AsSpan(8), 512);
        cbw[12] = 128; // data-in
        cbw[14] = 10;  // CDB length
        cbw[15] = 0x28; // READ(10)
        BinaryPrimitives.WriteUInt16BigEndian(cbw.AsSpan(15 + 7), 1); // one block
        Write(cbw);

        // The bulk endpoint's own wMaxPacketSize is 64, so the 512-byte
        // sector comes back eight packets at a time, exactly as
        // UsbMassStorageTests' own Read() helper already established.
        var sector = new List<byte>();
        for (int i = 0; i < 8; i++)
        {
            sector.AddRange(Read(64));
        }
        Assert.Equal(new byte[512], sector); // a fresh image reads back as zero

        byte[] csw = Read(64);
        Assert.Equal(13, csw.Length);
        Assert.Equal(0x53425355u, BinaryPrimitives.ReadUInt32LittleEndian(csw));
        Assert.Equal(0xA5A5A5A5u, BinaryPrimitives.ReadUInt32LittleEndian(csw.AsSpan(4)));
        Assert.Equal(0, csw[12]); // good status
    }

    [Fact]
    public void NestedHubsTwoTiersDeepRouteOnlyThroughEnabledPorts()
    {
        var outer = new UsbHubDevice(2);
        var inner = new UsbHubDevice(2);
        var keyboard = new UsbHidDevice(true);

        ControlOut(outer, 0, 5, 1); // address the outer hub so it never matches address 0 itself

        // Plug the inner hub in and address it, exactly like a real driver
        // enumerating one tier at a time: outer.Attach() delivers inner its
        // own bus reset, so inner still answers at address 0 until this.
        outer.Attach(0, inner);
        Assert.Null(outer.Find(0)); // outer's own port 0 is not enabled yet: inner is unreachable

        ControlOut(outer, 0x23, 3, 8, 1); // power outer port 1 (one-based)
        ControlOut(outer, 0x23, 3, 4, 1); // reset it
        outer.Advance(10_000);
        Assert.Same(inner, outer.Find(0)); // inner itself answers at 0, the only device reachable so far

        ControlOut(inner, 0, 5, 2); // address the inner hub
        Assert.Null(outer.Find(0)); // inner moved off 0; nothing behind it is enabled yet

        // Plug the keyboard into the inner hub's port, then power and reset
        // that port, exactly as the outer tier was.
        inner.Attach(0, keyboard); // keyboard stays at address 0 -- the target Find() must reach
        ControlOut(inner, 0x23, 3, 8, 1); // power inner port 1
        ControlOut(inner, 0x23, 3, 4, 1); // reset it
        inner.Advance(10_000);
        Assert.Same(keyboard, outer.Find(0)); // both tiers now forward
    }

    [Fact]
    public void SnapshotRoundTripsTheHubsPortsAndTheDownstreamKeyboard()
    {
        using var m = new Machine();
        m.EnableUsb();
        UsbHubDevice hub = m.InstallUsbHub(4);
        UsbHidDevice keyboard = m.UsbKeyboard!;

        ControlOut(hub, 0, 5, 6); // address the hub at 6
        ControlOut(hub, 0, 9, 1); // configure it
        ControlOut(hub, 0x23, 3, 8, 1); // power port 1 (hub port 0, where the keyboard sits)
        ControlOut(hub, 0x23, 3, 4, 1); // reset it
        hub.Advance(10_000);
        ControlOut(keyboard, 0, 5, 4); // address the keyboard directly at 4
        Assert.Equal(4, keyboard.Address);

        var saved = MachineSnapshot.Capture(m);

        // Disturb every piece of state the restore below should put back.
        ControlOut(hub, 0x23, 1, 20, 1); // clear C_PORT_RESET
        ControlOut(keyboard, 0, 5, 9);   // re-address the keyboard

        string path = Path.GetTempFileName();
        try
        {
            saved.Save(path);
            MachineSnapshot.Load(path).RestoreTo(m);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Equal(6, hub.Address);
        Assert.Equal(1, hub.Configuration);
        Assert.NotEqual(0, hub.PortChange(0) & 0x10); // C_PORT_RESET, restored
        Assert.Equal(4, keyboard.Address);            // the downstream device's own state, restored too
    }

    /// <summary>
    /// The topology every <c>vm.json</c> before this package described --
    /// keyboard and mouse directly on the controller's two root ports, no
    /// hub anywhere -- still round-trips exactly as it always did: this
    /// package changed how a device is found by address, not the shape of
    /// what <see cref="UhciController"/> or <see cref="UhciCard"/> capture,
    /// so a snapshot with no hub in it needs nothing new to restore.
    /// </summary>
    [Fact]
    public void ASnapshotWithNoHubConfiguredStillRestoresExactlyAsBefore()
    {
        using var m = new Machine();
        m.EnableUsb();
        m.RunInstructions(3000);
        m.Io.WriteWord(Base + 6, 0x77); // FRNUM, while the controller is stopped

        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(m).Save(path);
            m.Io.WriteWord(Base + 6, 0);
            MachineSnapshot.Load(path).RestoreTo(m);
            Assert.Equal(0x77, m.Io.ReadWord(Base + 6));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Null(m.UsbHub);
    }
}
