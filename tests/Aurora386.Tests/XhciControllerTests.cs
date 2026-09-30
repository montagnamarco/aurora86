using Aurora386.Core.Devices.Interrupts;
using Aurora386.Core.Devices.Pci;
using Aurora386.Core.Devices.Usb;
using Aurora386.Core.IO;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// qemu-xhci-compatible USB 3.0 host controller (PCI 1B36:000D): PCI
/// identity, class, revision and the 16 KB 64-bit BAR0's sizing, every fixed
/// capability register and the one Supported Protocol capability, USBCMD's
/// HCRST/RS/INTE, USBSTS's HCH and RW1C bits, MFINDEX's advance, the four
/// PORTSC root ports (attach/detach, port reset, LWS-gated link state
/// writes, RW1C change bits), interrupter 0's event ring (segment table
/// walk, cycle bit and its wrap, ERDP.EHB/IMAN.IP driving the PCI line,
/// USBSTS.HCE on a full ring, the bus-master gate) and a snapshot round
/// trip. See <c>plans/usb3-xhci.md</c>'s "Costanti" section for the register
/// map as implemented.
/// </summary>
public class XhciControllerTests
{
    private const ushort BridgeAddress = 8; // device 1, function 0 (00:01.0): the ISA bridge PciInterruptRouter needs
    private const ushort FunctionAddress = 0x40; // device 8, function 0 (00:08.0): plan decision 2
    private const uint RamSize = 4 * 1024 * 1024;
    private const uint AddressableBytes = 8 * 1024 * 1024;
    private const uint Bar0Base = 0x700000; // above RamSize, inside AddressableBytes

    // --- capability/extended-capability offsets, mirroring XhciController's own private constants ---

    private const uint RegCapabilityLengthAndVersion = 0x00;
    private const uint RegHcsParams1 = 0x04;
    private const uint RegHcsParams2 = 0x08;
    private const uint RegHcsParams3 = 0x0C;
    private const uint RegHccParams1 = 0x10;
    private const uint RegDboff = 0x14;
    private const uint RegRtsoff = 0x18;
    private const uint RegSupportedProtocolDword0 = 0x20;
    private const uint RegSupportedProtocolDword1 = 0x24;
    private const uint RegSupportedProtocolDword2 = 0x28;
    private const uint RegSupportedProtocolDword3 = 0x2C;

    private const uint OperationalBase = 0x40;

    // --- operational register offsets ------------------------------------------------

    private const uint RegUsbCommand = 0x40;
    private const uint RegUsbStatus = 0x44;
    private const uint RegPageSize = 0x48;
    private const uint RegDeviceNotificationControl = 0x54;
    private const uint RegCommandRingControlLow = 0x58;
    private const uint RegCommandRingControlHigh = 0x5C;
    private const uint RegDcbaapLow = 0x68;
    private const uint RegDcbaapHigh = 0x6C;
    private const uint RegConfig = 0x70;

    private const uint PortRegisterSetBase = OperationalBase + 0x400; // 0x440
    private const uint PortRegisterStride = 0x10;

    // --- runtime / interrupter 0 register offsets -------------------------------------

    private const uint RegMicroframeIndex = 0x1000;
    private const uint Interrupter0Base = 0x1020; // RTSOFF (1000h) + 20h
    private const uint RegIman = Interrupter0Base + 0x00;
    private const uint RegImod = Interrupter0Base + 0x04;
    private const uint RegErstsz = Interrupter0Base + 0x08;
    private const uint RegErstbaLow = Interrupter0Base + 0x10;
    private const uint RegErstbaHigh = Interrupter0Base + 0x14;
    private const uint RegErdpLow = Interrupter0Base + 0x18;
    private const uint RegErdpHigh = Interrupter0Base + 0x1C;

    private const uint DoorbellBase = 0x2000;

    private const uint Bar0Length = 0x4000;

    // --- USBCMD bits -------------------------------------------------------------------

    private const uint CommandRunStop = 1u << 0;
    private const uint CommandHostControllerReset = 1u << 1;
    private const uint CommandInterrupterEnable = 1u << 2;
    private const uint CommandHostSystemErrorEnable = 1u << 3;

    // --- USBSTS bits -------------------------------------------------------------------

    private const uint StatusHostControllerHalted = 1u << 0;
    private const uint StatusHostSystemError = 1u << 2;
    private const uint StatusEventInterrupt = 1u << 3;
    private const uint StatusPortChangeDetect = 1u << 4;
    private const uint StatusControllerNotReady = 1u << 11;
    private const uint StatusHostControllerError = 1u << 12;

    // --- PORTSC bits ---------------------------------------------------------------------

    private const uint PortCurrentConnectStatus = 1u << 0;
    private const uint PortEnabled = 1u << 1;
    private const uint PortReset = 1u << 4;
    private const int PortLinkStateShift = 5;
    private const uint PortPower = 1u << 9;
    private const int PortSpeedShift = 10;
    private const uint PortLinkStateWriteStrobe = 1u << 16;
    private const uint PortConnectStatusChange = 1u << 17;
    private const uint PortEnabledChange = 1u << 18;
    private const uint PortResetChange = 1u << 21;
    private const uint PortLinkStateChange = 1u << 22;

    private const uint LinkStateU0 = 0;
    private const uint LinkStateU3 = 3;
    private const uint LinkStateRxDetect = 5;

    private const uint SpeedFullSpeed = 1;
    private const uint SpeedLowSpeed = 2;
    private const uint SpeedHighSpeed = 3;

    // --- IMAN / ERDP bits ------------------------------------------------------------------

    private const uint InterrupterManagementInterruptPending = 1u << 0;
    private const uint InterrupterManagementInterruptEnable = 1u << 1;
    private const uint EventRingDequeuePointerEventHandlerBusy = 1u << 3;

    // --- event TRB layout --------------------------------------------------------------

    private const uint PortStatusChangeEventTrbType = 34;
    private const int TrbTypeShift = 10;
    private const uint TrbCycleBit = 1u << 0;
    private const uint EventCompletionCodeSuccess = 1;

    // --- command ring / command TRBs (stage 3) ------------------------------------------

    private const uint CrcrRingCycleState = 1u << 0;
    private const uint CrcrPointerMask = 0xFFFF_FFC0u;

    private const uint TrbTypeLink = 6;
    private const uint TrbTypeEnableSlot = 9;
    private const uint TrbTypeDisableSlot = 10;
    private const uint TrbTypeAddressDevice = 11;
    private const uint TrbTypeConfigureEndpoint = 12;
    private const uint TrbTypeEvaluateContext = 13;
    private const uint TrbTypeResetEndpoint = 14;
    private const uint TrbTypeStopEndpoint = 15;
    private const uint TrbTypeSetTrDequeuePointer = 16;
    private const uint TrbTypeNoOpCommand = 23;
    private const uint TrbTypeCommandCompletionEvent = 33;

    private const uint TrbTypeNormal = 1;
    private const uint TrbTypeSetupStage = 2;
    private const uint TrbTypeDataStage = 3;
    private const uint TrbTypeStatusStage = 4;
    private const uint TrbTypeEventDataTransfer = 7;
    private const uint TrbTypeTransferEvent = 32;

    private const uint TrbFlagToggleCycle = 1u << 1;
    private const uint TrbFlagInterruptOnShortPacket = 1u << 2;
    private const uint TrbFlagChain = 1u << 4;
    private const uint TrbFlagInterruptOnCompletion = 1u << 5;
    private const uint TrbFlagImmediateData = 1u << 6;
    private const uint TrbDataStageDirectionBit = 1u << 16; // DIR: 0 = OUT, 1 = IN
    private const uint CommandTrbBsrOrDcBit = 1u << 9;
    private const int CommandTrbSlotIdShift = 24;
    private const int TransferEventEndpointIdShift = 16;

    private const uint CompletionCodeUsbTransactionError = 4;
    private const uint CompletionCodeStallError = 6;
    private const uint CompletionCodeTrbError = 5;
    private const uint CompletionCodeNoSlotsAvailable = 9;
    private const uint CompletionCodeSlotNotEnabled = 11;
    private const uint CompletionCodeShortPacket = 13;
    private const int CompletionCodeShift = 24;

    // --- Slot / Endpoint / Input Control contexts (32 bytes, CSZ=0) --------------------

    private const int ContextSize = 32;
    private const uint SlotContextPortInfoOffset = 0x04;
    private const uint SlotContextAddressAndStateOffset = 0x0C;
    private const int SlotContextRootHubPortShift = 16;
    private const uint SlotContextDeviceAddressMask = 0xFF;
    private const int SlotContextSlotStateShift = 27;
    private const uint SlotContextSlotStateMask = 0x1Fu << SlotContextSlotStateShift;

    private const byte SlotStateDefault = 1;
    private const byte SlotStateAddressed = 2;
    private const byte SlotStateConfigured = 3;

    private const uint EndpointContextStateOffset = 0x00;
    private const uint EndpointContextTypeOffset = 0x04;
    private const uint EndpointContextDequeueLowOffset = 0x08;
    private const uint EndpointContextDequeueHighOffset = 0x0C;
    private const uint EndpointStateMask = 0x7;
    private const byte EndpointStateRunning = 1;
    private const byte EndpointStateHalted = 2;
    private const byte EndpointStateStopped = 3;
    private const int EndpointTypeShift = 3;
    private const byte EndpointTypeInterruptOut = 3;
    private const byte EndpointTypeBulkOut = 2;
    private const byte EndpointTypeControl = 4;
    private const byte EndpointTypeInterruptIn = 7;
    private const byte EndpointTypeBulkIn = 6;
    private const int EndpointMaxPacketSizeShift = 16;
    private const int EndpointIntervalShift = 16; // dword0 bits 23:16
    private const uint EndpointDequeueCycleStateBit = 1u << 0;

    private const uint InputControlContextDropFlagsOffset = 0x00;
    private const uint InputControlContextAddFlagsOffset = 0x04;
    private const int CommandTrbEndpointIdShift = 16;

    private sealed class TestDevice : IUsbDevice
    {
        public byte Address { get; set; } = 1;
        public bool LowSpeed { get; set; }
        public bool HighSpeedCapable { get; set; } = true;
        public bool? SelectedHighSpeed { get; private set; }
        public int ResetCalls;
        public int TransferCalls;

        public void Reset() => ResetCalls++;
        public void SelectSpeed(bool highSpeed) => SelectedHighSpeed = highSpeed;
        public void Advance(double microseconds) { }
        public UsbTransactionResult Transfer(byte pid, byte endpoint, bool data1, Span<byte> data)
        {
            TransferCalls++;
            return new(UsbHandshake.Ack);
        }
        public byte[] Capture() => BitConverter.GetBytes(ResetCalls);
        public void Restore(ReadOnlySpan<byte> data) => ResetCalls = BitConverter.ToInt32(data);
    }

    private static (XhciController Controller, PciBus Pci, PhysicalMemoryBus Memory, PciInterruptRouter Router, InterruptController Pic) Build()
    {
        var pci = new PciBus();
        var io = new IoBus();
        var memory = new PhysicalMemoryBus(RamSize, AddressableBytes);
        memory.A20Enabled = true;
        var pic = new InterruptController();
        pic.Register(io);
        InitializePic(io);

        var bridge = new PciFunction(BridgeAddress, 0x8086, 0x122E, 0x060100);
        pci.Add(bridge);
        var router = new PciInterruptRouter(pic, bridge);
        pci.InterruptRouter = router;
        pci.DmaMemory = new PhysicalDmaMemory(memory);

        var controller = new XhciController(pci, memory, FunctionAddress);
        pci.WriteConfiguration(FunctionAddress, 0x10, 4, Bar0Base); // BAR0 low dword
        pci.WriteConfiguration(FunctionAddress, 0x14, 4, 0); // BAR0 upper dword
        return (controller, pci, memory, router, pic);
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

    private static void EnableMemory(PciBus pci) => pci.WriteConfiguration(FunctionAddress, 4, 2, 0x2);
    private static void EnableMemoryAndBusMaster(PciBus pci) => pci.WriteConfiguration(FunctionAddress, 4, 2, 0x6);

    private static uint ReadBar(PhysicalMemoryBus memory, uint offset) => memory.ReadDword(Bar0Base + offset);
    private static void WriteBar(PhysicalMemoryBus memory, uint offset, uint value) => memory.WriteDword(Bar0Base + offset, value);

    private static uint PortOffset(int port) => PortRegisterSetBase + (uint)port * PortRegisterStride;

    /// <summary>A port reset as Linux performs it: PR written once (RW1S), never written back to 0.</summary>
    private static void ResetPort(PhysicalMemoryBus memory, int port)
    {
        WriteBar(memory, PortOffset(port), PortReset);
    }

    /// <summary>Writes one 16-byte event-ring segment table entry (base qword, size dword, reserved dword) into guest RAM.</summary>
    private static void WriteEventRingSegment(PhysicalMemoryBus memory, uint tableAddress, uint segmentBase, uint trbCount)
    {
        memory.WriteDword(tableAddress, segmentBase);
        memory.WriteDword(tableAddress + 4, 0);
        memory.WriteDword(tableAddress + 8, trbCount);
        memory.WriteDword(tableAddress + 12, 0);
    }

    private static void ConfigureEventRing(PhysicalMemoryBus memory, uint tableAddress, uint ringAddress, uint trbCount)
    {
        WriteEventRingSegment(memory, tableAddress, ringAddress, trbCount);
        WriteBar(memory, RegErstsz, 1);
        WriteBar(memory, RegErstbaHigh, 0);
        WriteBar(memory, RegErstbaLow, tableAddress);

        // ERDP at the first TRB, as every driver leaves it after init: the
        // ring is empty, and the first event must be written, not dropped.
        WriteBar(memory, RegErdpLow, ringAddress);
        WriteBar(memory, RegErdpHigh, 0);
    }

    private static (uint Dword0, uint Dword1, uint Dword2, uint Dword3) ReadEventTrb(PhysicalMemoryBus memory, uint ringAddress, int index)
    {
        uint address = ringAddress + (uint)index * 16;
        return (memory.ReadDword(address), memory.ReadDword(address + 4), memory.ReadDword(address + 8), memory.ReadDword(address + 12));
    }

    /// <summary>Points CRCR at a fresh command ring with RCS=1, the state <see cref="RunCommandRing"/>-equivalent code starts every doorbell 0 from.</summary>
    private static void ConfigureCommandRing(PhysicalMemoryBus memory, uint ringAddress)
    {
        WriteBar(memory, RegCommandRingControlLow, (ringAddress & CrcrPointerMask) | CrcrRingCycleState);
        WriteBar(memory, RegCommandRingControlHigh, 0);
    }

    private static void WriteCommandTrb(PhysicalMemoryBus memory, uint address, uint dword0, uint dword1, uint dword2, uint type, bool cycle, uint extraFlags = 0)
    {
        memory.WriteDword(address, dword0);
        memory.WriteDword(address + 4, dword1);
        memory.WriteDword(address + 8, dword2);
        memory.WriteDword(address + 12, (type << TrbTypeShift) | extraFlags | (cycle ? TrbCycleBit : 0u));
    }

    private static void RingCommandDoorbell(PhysicalMemoryBus memory) => WriteBar(memory, DoorbellBase, 0); // doorbell 0, target 0

    /// <summary>Rings the transfer-ring doorbell for <paramref name="slotId"/>'s endpoint <paramref name="endpointId"/> (1 = EP0, 2..31 = EP1 OUT .. EP15 IN).</summary>
    private static void RingTransferDoorbell(PhysicalMemoryBus memory, byte slotId, byte endpointId)
        => WriteBar(memory, DoorbellBase + (uint)slotId * 4, endpointId);

    private static void WriteTransferTrb(PhysicalMemoryBus memory, uint address, uint dword0, uint dword1, uint dword2, uint type, bool cycle, uint extraFlags = 0)
        => WriteCommandTrb(memory, address, dword0, dword1, dword2, type, cycle, extraFlags);

    private static uint SlotContextBase(uint deviceContext) => deviceContext;
    private static uint EndpointContextBase(uint deviceContext, int epId) => deviceContext + (uint)epId * ContextSize;
    private static uint InputSlotContextBase(uint inputContext) => inputContext + ContextSize;
    private static uint InputEndpointContextBase(uint inputContext, int epId) => inputContext + (uint)(epId + 1) * ContextSize;

    // ===== Stage 1: identity, capability registers, USBCMD/USBSTS, MFINDEX =====

    [Fact]
    public void PciIdentityClassAndRevisionMatchQemuXhci()
    {
        (_, PciBus pci, _, _, _) = Build();
        Assert.Equal(0x1B36u, pci.ReadConfiguration(FunctionAddress, 0x00, 2));
        Assert.Equal(0x000Du, pci.ReadConfiguration(FunctionAddress, 0x02, 2));
        Assert.Equal(0x01u, pci.ReadConfiguration(FunctionAddress, 0x08, 1)); // revision ID
        Assert.Equal(0x30u, pci.ReadConfiguration(FunctionAddress, 0x09, 1)); // programming interface: xHCI (30h)
        Assert.Equal(0x03u, pci.ReadConfiguration(FunctionAddress, 0x0A, 1)); // subclass: USB
        Assert.Equal(0x0Cu, pci.ReadConfiguration(FunctionAddress, 0x0B, 1)); // base class: serial bus controller
        Assert.Equal(1u, pci.ReadConfiguration(FunctionAddress, 0x3D, 1)); // INTA
    }

    [Fact]
    public void Bar0IsA16KilobyteSixtyFourBitMemoryBar()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();

        pci.WriteConfiguration(FunctionAddress, 0x10, 4, 0xFFFFFFFF);
        pci.WriteConfiguration(FunctionAddress, 0x14, 4, 0xFFFFFFFF);
        uint low = pci.ReadConfiguration(FunctionAddress, 0x10, 4);
        uint high = pci.ReadConfiguration(FunctionAddress, 0x14, 4);

        Assert.Equal(0x4u, low & 0xF); // memory space, 64-bit type, non-prefetchable
        Assert.Equal(0xFFFFC000u, low & 0xFFFFFFF0u); // 16 KB alignment
        Assert.Equal(0xFFFFFFFFu, high); // the upper dword is fully writable

        // Restoring a real base (below the emulator's own 4 GB memory bus, per the
        // upper dword's own documented reach) re-enables decode at that address.
        pci.WriteConfiguration(FunctionAddress, 0x10, 4, Bar0Base);
        pci.WriteConfiguration(FunctionAddress, 0x14, 4, 0);
        EnableMemory(pci);
        Assert.Equal(0x01000040u, ReadBar(memory, RegCapabilityLengthAndVersion));
    }

    [Fact]
    public void CapabilityRegistersMatchThePlan()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemory(pci);

        Assert.Equal(0x01000040u, ReadBar(memory, RegCapabilityLengthAndVersion)); // CAPLENGTH=40h, HCIVERSION=0100h
        Assert.Equal((4u << 24) | (1u << 8) | 8u, ReadBar(memory, RegHcsParams1)); // 4 ports, 1 interrupter, 8 slots
        Assert.Equal(0x0000000Fu, ReadBar(memory, RegHcsParams2));
        Assert.Equal(0u, ReadBar(memory, RegHcsParams3));
        Assert.Equal(0x00080001u, ReadBar(memory, RegHccParams1));
        Assert.Equal(0x2000u, ReadBar(memory, RegDboff));
        Assert.Equal(0x1000u, ReadBar(memory, RegRtsoff));
    }

    [Fact]
    public void SupportedProtocolCapabilityIsTheOnlyExtendedCapability()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemory(pci);

        Assert.Equal(0x02000002u, ReadBar(memory, RegSupportedProtocolDword0)); // Cap ID=02h, Next=0 (the only capability)
        Assert.Equal(0x20425355u, ReadBar(memory, RegSupportedProtocolDword1)); // "USB "
        Assert.Equal((4u << 8) | 1u, ReadBar(memory, RegSupportedProtocolDword2)); // 4 compatible ports, offset 1
        Assert.Equal(0u, ReadBar(memory, RegSupportedProtocolDword3));
    }

    [Fact]
    public void HostControllerResetReturnsEveryRegisterToItsPowerOnValue()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemory(pci);

        WriteBar(memory, RegUsbCommand, CommandRunStop | CommandInterrupterEnable);
        WriteBar(memory, RegDeviceNotificationControl, 0x1234);
        WriteBar(memory, RegConfig, 0x05);
        WriteBar(memory, RegDcbaapLow, 0x40000);
        Assert.Equal(0u, ReadBar(memory, RegUsbStatus) & StatusHostControllerHalted);

        WriteBar(memory, RegUsbCommand, CommandHostControllerReset);

        Assert.Equal(0u, ReadBar(memory, RegUsbCommand));
        Assert.Equal(StatusHostControllerHalted, ReadBar(memory, RegUsbStatus));
        Assert.Equal(0u, ReadBar(memory, RegDeviceNotificationControl));
        Assert.Equal(0u, ReadBar(memory, RegConfig));
        Assert.Equal(0u, ReadBar(memory, RegDcbaapLow));
    }

    [Fact]
    public void RunStopGatesHostControllerHalted()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemory(pci);

        Assert.Equal(StatusHostControllerHalted, ReadBar(memory, RegUsbStatus) & StatusHostControllerHalted);

        WriteBar(memory, RegUsbCommand, CommandRunStop);
        Assert.Equal(0u, ReadBar(memory, RegUsbStatus) & StatusHostControllerHalted);
        Assert.Equal(0u, ReadBar(memory, RegUsbStatus) & StatusControllerNotReady); // CNR always reads 0

        WriteBar(memory, RegUsbCommand, 0);
        Assert.Equal(StatusHostControllerHalted, ReadBar(memory, RegUsbStatus) & StatusHostControllerHalted);
    }

    [Fact]
    public void UsbStatusHostSystemErrorEventInterruptAndPortChangeDetectAreWriteOneToClear()
    {
        (XhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);
        WriteBar(memory, RegUsbCommand, CommandRunStop);
        controller.Attach(0, new TestDevice()); // sets PCD

        Assert.NotEqual(0u, ReadBar(memory, RegUsbStatus) & StatusPortChangeDetect);

        WriteBar(memory, RegUsbStatus, StatusHostSystemError | StatusEventInterrupt); // clearing unrelated bits leaves PCD alone
        Assert.NotEqual(0u, ReadBar(memory, RegUsbStatus) & StatusPortChangeDetect);

        WriteBar(memory, RegUsbStatus, StatusPortChangeDetect);
        Assert.Equal(0u, ReadBar(memory, RegUsbStatus) & StatusPortChangeDetect);
    }

    [Fact]
    public void MicroframeIndexAdvancesOneStepPer125MicrosecondsWhileRunningAndWrapsAt14Bits()
    {
        (XhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemory(pci);

        controller.Advance(1000); // RS=0: MFINDEX must not move
        Assert.Equal(0u, ReadBar(memory, RegMicroframeIndex));

        WriteBar(memory, RegUsbCommand, CommandRunStop);
        controller.Advance(125.0 * 5);
        Assert.Equal(5u, ReadBar(memory, RegMicroframeIndex));

        controller.Advance(125.0 * 16384); // wraps repeatedly past the 14-bit field
        Assert.Equal(5u, ReadBar(memory, RegMicroframeIndex));
    }

    // ===== Stage 2: ports and the event ring =====

    [Fact]
    public void AttachSetsCurrentConnectStatusAndConnectStatusChangeAndPostsAnEvent()
    {
        (XhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);
        WriteBar(memory, RegUsbCommand, CommandRunStop);
        const uint tableAddress = 0x10000;
        const uint ringAddress = 0x11000;
        ConfigureEventRing(memory, tableAddress, ringAddress, 4);

        controller.Attach(2, new TestDevice());

        uint portsc = ReadBar(memory, PortOffset(2));
        Assert.NotEqual(0u, portsc & PortCurrentConnectStatus);
        Assert.NotEqual(0u, portsc & PortConnectStatusChange);
        Assert.NotEqual(0u, ReadBar(memory, RegUsbStatus) & StatusPortChangeDetect);

        (uint dword0, uint dword1, uint dword2, uint dword3) = ReadEventTrb(memory, ringAddress, 0);
        Assert.Equal(3u << 24, dword0); // Port ID is 1-based: port 2 -> 3
        Assert.Equal(0u, dword1);
        Assert.Equal(EventCompletionCodeSuccess << 24, dword2);
        Assert.Equal((PortStatusChangeEventTrbType << TrbTypeShift) | TrbCycleBit, dword3); // producer cycle state starts at 1

        controller.Attach(2, null);
        portsc = ReadBar(memory, PortOffset(2));
        Assert.Equal(0u, portsc & PortCurrentConnectStatus);
        Assert.NotEqual(0u, portsc & PortConnectStatusChange); // still set: software never cleared it
    }

    [Theory]
    [InlineData(false, false, SpeedFullSpeed)]
    [InlineData(true, false, SpeedLowSpeed)]
    [InlineData(false, true, SpeedHighSpeed)]
    public void PortResetEnablesThePortAndReportsTheNegotiatedSpeed(bool lowSpeed, bool highSpeedCapable, uint expectedSpeed)
    {
        (XhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemory(pci);
        var device = new TestDevice { LowSpeed = lowSpeed, HighSpeedCapable = highSpeedCapable };
        controller.Attach(1, device);

        // PR is RW1S: one write starts the reset, the controller finishes it
        // and clears PR itself (xHCI 1.2 section 5.4.8), as Linux expects.
        WriteBar(memory, PortOffset(1), PortReset);

        uint portsc = ReadBar(memory, PortOffset(1));
        Assert.Equal(0u, portsc & PortReset);
        Assert.NotEqual(0u, portsc & PortEnabled);
        Assert.NotEqual(0u, portsc & PortResetChange);
        Assert.Equal(expectedSpeed, (portsc >> PortSpeedShift) & 0xF);
        Assert.Equal(LinkStateU0, (portsc >> PortLinkStateShift) & 0xF);
        Assert.Equal(highSpeedCapable, device.SelectedHighSpeed == true);
    }

    [Fact]
    public void PortLinkStateReadsRxDetectWhenThePortIsEmpty()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemory(pci);

        uint portsc = ReadBar(memory, PortOffset(0));
        Assert.Equal(LinkStateRxDetect, (portsc >> PortLinkStateShift) & 0xF);
    }

    [Fact]
    public void LinkStateWriteStrobeGatesPortLinkStateWritesToU0AndU3Only()
    {
        (XhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemory(pci);
        controller.Attach(0, new TestDevice());
        ResetPort(memory, 0);
        Assert.Equal(0u, (ReadBar(memory, PortOffset(0)) >> PortLinkStateShift) & 0xF); // U0 after reset

        // Without LWS, a link-state write is ignored.
        WriteBar(memory, PortOffset(0), 1u << PortLinkStateShift);
        Assert.Equal(0u, (ReadBar(memory, PortOffset(0)) >> PortLinkStateShift) & 0xF);

        // LWS set, but targeting U1 (not U0/U3): still ignored.
        WriteBar(memory, PortOffset(0), PortLinkStateWriteStrobe | (1u << PortLinkStateShift));
        Assert.Equal(0u, (ReadBar(memory, PortOffset(0)) >> PortLinkStateShift) & 0xF);

        WriteBar(memory, PortOffset(0), PortLinkStateWriteStrobe | (LinkStateU3 << PortLinkStateShift));
        uint portsc = ReadBar(memory, PortOffset(0));
        Assert.Equal(LinkStateU3, (portsc >> PortLinkStateShift) & 0xF);
        Assert.NotEqual(0u, portsc & PortLinkStateChange);
    }

    [Fact]
    public void PortChangeBitsAreWriteOneToClear()
    {
        (XhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemory(pci);
        controller.Attach(3, new TestDevice());
        Assert.NotEqual(0u, ReadBar(memory, PortOffset(3)) & PortConnectStatusChange);

        WriteBar(memory, PortOffset(3), 0); // writing 0 has no effect
        Assert.NotEqual(0u, ReadBar(memory, PortOffset(3)) & PortConnectStatusChange);

        WriteBar(memory, PortOffset(3), PortConnectStatusChange);
        Assert.Equal(0u, ReadBar(memory, PortOffset(3)) & PortConnectStatusChange);
    }

    [Fact]
    public void PortEnabledIsClearedByWritingOneAndNeverSetBySoftware()
    {
        (XhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemory(pci);
        controller.Attach(0, new TestDevice());
        ResetPort(memory, 0);
        Assert.NotEqual(0u, ReadBar(memory, PortOffset(0)) & PortEnabled);

        WriteBar(memory, PortOffset(0), 0); // writing 0 must not disable the port
        Assert.NotEqual(0u, ReadBar(memory, PortOffset(0)) & PortEnabled);

        WriteBar(memory, PortOffset(0), PortEnabled); // writing 1 clears it (RW1CS)
        Assert.Equal(0u, ReadBar(memory, PortOffset(0)) & PortEnabled);

        WriteBar(memory, PortOffset(0), PortEnabled); // writing 1 again cannot set it back
        Assert.Equal(0u, ReadBar(memory, PortOffset(0)) & PortEnabled);
    }

    [Fact]
    public void TheEventRingProducerWrapsItsCycleBitAfterALapThroughTheSegment()
    {
        (XhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);
        WriteBar(memory, RegUsbCommand, CommandRunStop);
        const uint tableAddress = 0x10000;
        const uint ringAddress = 0x11000;
        ConfigureEventRing(memory, tableAddress, ringAddress, 2); // a two-TRB segment: the third event wraps

        // The driver consumes each event and moves ERDP past it, as Linux's
        // xhci_update_erst_dequeue does; a two-TRB ring holds one unread event.
        controller.Attach(0, new TestDevice());
        WriteBar(memory, RegErdpLow, ringAddress + 16);
        controller.Attach(1, new TestDevice());
        WriteBar(memory, RegErdpLow, ringAddress);
        (uint dword0First, _, _, uint dword3First) = ReadEventTrb(memory, ringAddress, 0);
        (_, _, _, uint dword3Second) = ReadEventTrb(memory, ringAddress, 1);

        controller.Attach(2, new TestDevice()); // the third event wraps back to index 0 and flips the cycle bit
        (uint dword0Third, _, _, uint dword3Third) = ReadEventTrb(memory, ringAddress, 0);

        Assert.Equal(1u << 24, dword0First); // port 0 -> ID 1
        Assert.NotEqual(0u, dword3First & TrbCycleBit);
        Assert.NotEqual(0u, dword3Second & TrbCycleBit);
        Assert.Equal(3u << 24, dword0Third); // port 2 -> ID 3, the third event
        Assert.Equal(0u, dword3Third & TrbCycleBit); // the cycle bit flipped after the lap
    }

    [Fact]
    public void EventRingFullDropsTheEventAndSetsHostControllerError()
    {
        (XhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);
        WriteBar(memory, RegUsbCommand, CommandRunStop);
        const uint tableAddress = 0x10000;
        const uint ringAddress = 0x11000;
        ConfigureEventRing(memory, tableAddress, ringAddress, 4); // ERDP at the ring's start: empty
        const uint lastSlot = ringAddress + 3 * 16;
        memory.WriteDword(lastSlot, 0xDEADBEEF); // a sentinel the dropped event must not overwrite

        // A four-TRB ring the consumer never reads holds three events: a
        // fourth would put the producer on top of ERDP, indistinguishable
        // from an empty ring (QEMU: full when (ep_idx + 1) % size == dp_idx).
        controller.Attach(0, new TestDevice());
        controller.Attach(1, new TestDevice());
        controller.Attach(2, new TestDevice());
        Assert.Equal(0u, ReadBar(memory, RegUsbStatus) & StatusHostControllerError);

        controller.Attach(3, new TestDevice());

        Assert.Equal(0xDEADBEEFu, memory.ReadDword(lastSlot));
        Assert.NotEqual(0u, ReadBar(memory, RegUsbStatus) & StatusHostControllerError);
    }

    [Fact]
    public void EveryEventSetsEventHandlerBusyAndInterruptPendingAndErdpWriteWithEhbClearsIt()
    {
        (XhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);
        WriteBar(memory, RegUsbCommand, CommandRunStop);
        const uint tableAddress = 0x10000;
        const uint ringAddress = 0x11000;
        ConfigureEventRing(memory, tableAddress, ringAddress, 4);

        controller.Attach(0, new TestDevice());

        Assert.NotEqual(0u, ReadBar(memory, RegErdpLow) & EventRingDequeuePointerEventHandlerBusy);
        Assert.NotEqual(0u, ReadBar(memory, RegIman) & InterrupterManagementInterruptPending);
        Assert.NotEqual(0u, ReadBar(memory, RegUsbStatus) & StatusEventInterrupt);

        WriteBar(memory, RegErdpLow, EventRingDequeuePointerEventHandlerBusy);
        Assert.Equal(0u, ReadBar(memory, RegErdpLow) & EventRingDequeuePointerEventHandlerBusy);
    }

    [Fact]
    public void ThePciLineFollowsInterruptPendingInterruptEnableAndUsbCommandInte()
    {
        (XhciController controller, PciBus pci, PhysicalMemoryBus memory, PciInterruptRouter router, InterruptController pic) = Build();
        EnableMemoryAndBusMaster(pci);
        Assert.True(router.Route(FunctionAddress, 1, 11));
        const uint tableAddress = 0x10000;
        const uint ringAddress = 0x11000;
        ConfigureEventRing(memory, tableAddress, ringAddress, 4);
        WriteBar(memory, RegIman, InterrupterManagementInterruptEnable);
        WriteBar(memory, RegUsbCommand, CommandRunStop | CommandInterrupterEnable);

        controller.Attach(0, new TestDevice());
        Assert.True(pic.HasPendingInterrupt);

        // IMAN.IP is RW1C but IE is plain RW, sharing the same dword: a driver clearing
        // IP always does a read-modify-write so its own IE setting survives, exactly as
        // it must here to keep testing the same IE=1 configuration.
        WriteBar(memory, RegIman, ReadBar(memory, RegIman) | InterrupterManagementInterruptPending);
        Assert.False(pic.HasPendingInterrupt);

        controller.Attach(1, new TestDevice()); // a fresh event raises the line again
        Assert.True(pic.HasPendingInterrupt);

        WriteBar(memory, RegUsbCommand, CommandRunStop); // dropping USBCMD.INTE alone also drops the line
        Assert.False(pic.HasPendingInterrupt);
    }

    [Fact]
    public void BusMasterOffMeansNoEventRingWrite()
    {
        (XhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemory(pci); // memory decode on, bus master left off
        WriteBar(memory, RegUsbCommand, CommandRunStop);
        const uint tableAddress = 0x10000;
        const uint ringAddress = 0x11000;
        ConfigureEventRing(memory, tableAddress, ringAddress, 4);
        memory.WriteDword(ringAddress, 0xDEADBEEF);

        controller.Attach(0, new TestDevice());

        Assert.NotEqual(0u, ReadBar(memory, PortOffset(0)) & PortCurrentConnectStatus); // the register itself still updates
        Assert.Equal(0xDEADBEEFu, memory.ReadDword(ringAddress)); // but no DMA write happened
        Assert.Equal(0u, ReadBar(memory, RegIman) & InterrupterManagementInterruptPending);
    }

    [Fact]
    public void SnapshotRoundTripRestoresRegistersPortsAndTheEventRingProducer()
    {
        (XhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);
        WriteBar(memory, RegUsbCommand, CommandRunStop | CommandInterrupterEnable);
        const uint tableAddress = 0x10000;
        const uint ringAddress = 0x11000;
        ConfigureEventRing(memory, tableAddress, ringAddress, 2);
        WriteBar(memory, RegIman, InterrupterManagementInterruptEnable);
        var device = new TestDevice();
        controller.Attach(0, device);
        controller.Attach(1, device: new TestDevice());
        controller.Advance(125.0 * 7);

        byte[] snapshot = controller.Capture();

        // Only register-level state is perturbed here: the snapshot's own device
        // topology check (mirroring EhciController's) requires every port's attached
        // device to be exactly what it was when Capture() ran, so nothing calls
        // Attach between capture and restore.
        WriteBar(memory, RegUsbStatus, StatusPortChangeDetect); // clears PCD (RW1C)
        controller.Advance(125.0 * 3);

        controller.Restore(snapshot, XhciController.SnapshotVersion);

        Assert.Equal(7u, ReadBar(memory, RegMicroframeIndex));
        Assert.NotEqual(0u, ReadBar(memory, RegUsbStatus) & StatusPortChangeDetect);
        Assert.NotEqual(0u, ReadBar(memory, PortOffset(0)) & PortCurrentConnectStatus);
        Assert.NotEqual(0u, ReadBar(memory, PortOffset(1)) & PortCurrentConnectStatus);
        Assert.Equal(0u, ReadBar(memory, PortOffset(2)) & PortCurrentConnectStatus);
    }

    // ===== Stage 3: the command ring =====

    private const uint CommandRingAddress = 0x20000;
    private const uint EventTableAddress = 0x10000;
    private const uint EventRingAddress = 0x11000;
    private const uint DcbaapAddress = 0x50000;
    private const uint InputContextAddress = 0x30000;

    private static (XhciController Controller, PciBus Pci, PhysicalMemoryBus Memory, TestDevice Device) BuildWithCommandRing()
    {
        var device = new TestDevice();
        (XhciController controller, PciBus pci, PhysicalMemoryBus memory) = BuildWithCommandRingAndDevice(device, eventRingTrbCount: 8);
        return (controller, pci, memory, device);
    }

    private static (XhciController Controller, PciBus Pci, PhysicalMemoryBus Memory) BuildWithCommandRingAndDevice(IUsbDevice device, uint eventRingTrbCount = 32, bool resetPort = false)
    {
        (XhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);
        ConfigureEventRing(memory, EventTableAddress, EventRingAddress, eventRingTrbCount);
        ConfigureCommandRing(memory, CommandRingAddress);
        WriteBar(memory, RegDcbaapLow, DcbaapAddress);
        WriteBar(memory, RegDcbaapHigh, 0);
        controller.Attach(0, device); // before RS=1, so no Port Status Change Event lands in the event ring first
        if (resetPort)
        {
            ResetPort(memory, 0); // negotiates speed (SelectSpeed) before RS=1, so this too posts no event
        }
        WriteBar(memory, RegUsbCommand, CommandRunStop);
        return (controller, pci, memory);
    }

    /// <summary>
    /// Enable Slot then Address Device (BSR=0) at command-ring slots 0 and
    /// 1 (event-ring slots 0 and 1): the shared setup every endpoint-scoped
    /// command test below builds on, addressing <paramref name="device"/>
    /// behind root hub port 1 (0-based port 0) with a plain 64-byte EP0.
    /// </summary>
    private static byte EnableAndAddressSlot(PhysicalMemoryBus memory, uint deviceContext)
    {
        WriteCommandTrb(memory, CommandRingAddress + 0 * 16, 0, 0, 0, TrbTypeEnableSlot, cycle: true);
        RingCommandDoorbell(memory);
        (_, _, _, uint enableDword3) = ReadEventTrb(memory, EventRingAddress, 0);
        byte slotId = (byte)(enableDword3 >> CommandTrbSlotIdShift);

        memory.WriteDword(DcbaapAddress + (uint)slotId * 8, deviceContext);
        memory.WriteDword(DcbaapAddress + (uint)slotId * 8 + 4, 0);

        memory.WriteDword(InputContextAddress + InputControlContextDropFlagsOffset, 0);
        memory.WriteDword(InputContextAddress + InputControlContextAddFlagsOffset, 0x3);
        memory.WriteDword(InputSlotContextBase(InputContextAddress) + SlotContextPortInfoOffset, 1u << SlotContextRootHubPortShift);
        memory.WriteDword(InputEndpointContextBase(InputContextAddress, 1) + EndpointContextTypeOffset, ((uint)EndpointTypeControl << EndpointTypeShift) | (64u << EndpointMaxPacketSizeShift));
        memory.WriteDword(InputEndpointContextBase(InputContextAddress, 1) + EndpointContextDequeueLowOffset, 0x40000 | EndpointDequeueCycleStateBit);
        memory.WriteDword(InputEndpointContextBase(InputContextAddress, 1) + EndpointContextDequeueHighOffset, 0);

        WriteCommandTrb(memory, CommandRingAddress + 1 * 16, InputContextAddress, 0, 0, TrbTypeAddressDevice, cycle: true,
            extraFlags: (uint)slotId << CommandTrbSlotIdShift);
        RingCommandDoorbell(memory);
        return slotId;
    }

    /// <summary>Configure Endpoint, adding one or more bulk/interrupt endpoints at the given command-ring slot.</summary>
    private static void ConfigureEndpoints(PhysicalMemoryBus memory, byte slotId, int commandSlotIndex, params (int EpId, byte Type, int MaxPacket, uint Dequeue)[] endpoints)
    {
        memory.WriteDword(InputContextAddress + InputControlContextDropFlagsOffset, 0);
        uint addFlags = 0x1u; // A0: the slot is always re-evaluated (QEMU's own xhci_config_ep rule)
        foreach ((int epId, byte type, int maxPacket, uint dequeue) in endpoints)
        {
            addFlags |= 1u << epId;
            memory.WriteDword(InputEndpointContextBase(InputContextAddress, epId) + EndpointContextTypeOffset, ((uint)type << EndpointTypeShift) | ((uint)maxPacket << EndpointMaxPacketSizeShift));
            memory.WriteDword(InputEndpointContextBase(InputContextAddress, epId) + EndpointContextDequeueLowOffset, dequeue | EndpointDequeueCycleStateBit);
            memory.WriteDword(InputEndpointContextBase(InputContextAddress, epId) + EndpointContextDequeueHighOffset, 0);
        }
        memory.WriteDword(InputContextAddress + InputControlContextAddFlagsOffset, addFlags);
        WriteCommandTrb(memory, CommandRingAddress + (uint)commandSlotIndex * 16, InputContextAddress, 0, 0, TrbTypeConfigureEndpoint, cycle: true,
            extraFlags: (uint)slotId << CommandTrbSlotIdShift);
        RingCommandDoorbell(memory);
    }

    [Fact]
    public void EnableSlotAddressDeviceWithBsrThenWithoutThenConfigureEndpoint()
    {
        (XhciController controller, _, PhysicalMemoryBus memory, TestDevice device) = BuildWithCommandRing();

        // --- Enable Slot -------------------------------------------------------------
        WriteCommandTrb(memory, CommandRingAddress + 0 * 16, 0, 0, 0, TrbTypeEnableSlot, cycle: true);
        RingCommandDoorbell(memory);
        (uint enableDword0, uint enableDword1, uint enableDword2, uint enableDword3) = ReadEventTrb(memory, EventRingAddress, 0);
        Assert.Equal((TrbTypeCommandCompletionEvent << TrbTypeShift) | TrbCycleBit, enableDword3 & ~((uint)0xFF << CommandTrbSlotIdShift));
        Assert.Equal(EventCompletionCodeSuccess << CompletionCodeShift, enableDword2);
        byte slotId = (byte)(enableDword3 >> CommandTrbSlotIdShift);
        Assert.Equal(1, slotId);
        Assert.Equal(CommandRingAddress, enableDword0); // the Command TRB pointer: the Enable Slot TRB itself
        Assert.Equal(0u, enableDword1);

        // --- give the slot a Device Context before addressing it --------------------
        uint deviceContext = 0x60000;
        memory.WriteDword(DcbaapAddress + (uint)slotId * 8, deviceContext);
        memory.WriteDword(DcbaapAddress + (uint)slotId * 8 + 4, 0);

        // --- Input Context: Slot Context (root hub port 1) and EP0 ------------------
        memory.WriteDword(InputContextAddress + InputControlContextDropFlagsOffset, 0);
        memory.WriteDword(InputContextAddress + InputControlContextAddFlagsOffset, 0x3); // A0 (slot) + A1 (EP0)
        memory.WriteDword(InputSlotContextBase(InputContextAddress) + SlotContextPortInfoOffset, 1u << SlotContextRootHubPortShift); // port 0 -> field 1
        memory.WriteDword(InputEndpointContextBase(InputContextAddress, 1) + EndpointContextTypeOffset, ((uint)EndpointTypeControl << EndpointTypeShift) | (64u << EndpointMaxPacketSizeShift));
        memory.WriteDword(InputEndpointContextBase(InputContextAddress, 1) + EndpointContextDequeueLowOffset, 0x40000 | EndpointDequeueCycleStateBit);
        memory.WriteDword(InputEndpointContextBase(InputContextAddress, 1) + EndpointContextDequeueHighOffset, 0);

        // --- Address Device, BSR=1: Default state only, no SET_ADDRESS --------------
        WriteCommandTrb(memory, CommandRingAddress + 1 * 16, InputContextAddress, 0, 0, TrbTypeAddressDevice, cycle: true,
            extraFlags: CommandTrbBsrOrDcBit | ((uint)slotId << CommandTrbSlotIdShift));
        RingCommandDoorbell(memory);
        (_, _, uint bsrDword2, uint bsrDword3) = ReadEventTrb(memory, EventRingAddress, 1);
        Assert.Equal(EventCompletionCodeSuccess << CompletionCodeShift, bsrDword2);
        Assert.Equal(slotId, (byte)(bsrDword3 >> CommandTrbSlotIdShift));
        uint slotAddressAndState = memory.ReadDword(SlotContextBase(deviceContext) + SlotContextAddressAndStateOffset);
        Assert.Equal(SlotStateDefault, (byte)((slotAddressAndState & SlotContextSlotStateMask) >> SlotContextSlotStateShift));
        Assert.Equal(0, device.TransferCalls); // BSR: no SET_ADDRESS ever goes out on the wire
        Assert.Equal(EndpointStateRunning, (byte)(memory.ReadDword(EndpointContextBase(deviceContext, 1) + EndpointContextStateOffset) & EndpointStateMask));

        // --- Address Device, BSR=0: Addressed, SET_ADDRESS(slotId) on the wire ------
        WriteCommandTrb(memory, CommandRingAddress + 2 * 16, InputContextAddress, 0, 0, TrbTypeAddressDevice, cycle: true,
            extraFlags: (uint)slotId << CommandTrbSlotIdShift);
        RingCommandDoorbell(memory);
        (_, _, uint realDword2, _) = ReadEventTrb(memory, EventRingAddress, 2);
        Assert.Equal(EventCompletionCodeSuccess << CompletionCodeShift, realDword2);
        slotAddressAndState = memory.ReadDword(SlotContextBase(deviceContext) + SlotContextAddressAndStateOffset);
        Assert.Equal(SlotStateAddressed, (byte)((slotAddressAndState & SlotContextSlotStateMask) >> SlotContextSlotStateShift));
        Assert.Equal(slotId, (byte)(slotAddressAndState & SlotContextDeviceAddressMask));
        Assert.Equal(2, device.TransferCalls); // SETUP(SET_ADDRESS) then the zero-length status IN

        // --- Configure Endpoint: the slot plus one Bulk IN endpoint (id 3) -----------
        memory.WriteDword(InputContextAddress + InputControlContextDropFlagsOffset, 0);
        memory.WriteDword(InputContextAddress + InputControlContextAddFlagsOffset, 0x1 | (1u << 3)); // A0 (slot) + A3 (EP1 IN)
        memory.WriteDword(InputEndpointContextBase(InputContextAddress, 3) + EndpointContextTypeOffset, ((uint)EndpointTypeBulkIn << EndpointTypeShift) | (512u << EndpointMaxPacketSizeShift));
        memory.WriteDword(InputEndpointContextBase(InputContextAddress, 3) + EndpointContextDequeueLowOffset, 0x41000 | EndpointDequeueCycleStateBit);
        memory.WriteDword(InputEndpointContextBase(InputContextAddress, 3) + EndpointContextDequeueHighOffset, 0);

        WriteCommandTrb(memory, CommandRingAddress + 3 * 16, InputContextAddress, 0, 0, TrbTypeConfigureEndpoint, cycle: true,
            extraFlags: (uint)slotId << CommandTrbSlotIdShift);
        RingCommandDoorbell(memory);
        (_, _, uint configureDword2, _) = ReadEventTrb(memory, EventRingAddress, 3);
        Assert.Equal(EventCompletionCodeSuccess << CompletionCodeShift, configureDword2);

        slotAddressAndState = memory.ReadDword(SlotContextBase(deviceContext) + SlotContextAddressAndStateOffset);
        Assert.Equal(SlotStateConfigured, (byte)((slotAddressAndState & SlotContextSlotStateMask) >> SlotContextSlotStateShift));
        uint epType = memory.ReadDword(EndpointContextBase(deviceContext, 3) + EndpointContextTypeOffset);
        Assert.Equal(EndpointTypeBulkIn, (byte)((epType >> EndpointTypeShift) & 0x7));
        Assert.Equal(512u, epType >> EndpointMaxPacketSizeShift);
        Assert.Equal(EndpointStateRunning, (byte)(memory.ReadDword(EndpointContextBase(deviceContext, 3) + EndpointContextStateOffset) & EndpointStateMask));
    }

    [Fact]
    public void ALinkTrbWithToggleCycleFlipsTheConsumerCycleStateAndStopsTheRing()
    {
        (_, _, PhysicalMemoryBus memory, _) = BuildWithCommandRing();

        WriteCommandTrb(memory, CommandRingAddress + 0 * 16, 0, 0, 0, TrbTypeNoOpCommand, cycle: true);
        WriteCommandTrb(memory, CommandRingAddress + 1 * 16, CommandRingAddress, 0, 0, TrbTypeLink, cycle: true, extraFlags: TrbFlagToggleCycle);
        RingCommandDoorbell(memory);

        (_, _, uint dword2, uint dword3) = ReadEventTrb(memory, EventRingAddress, 0);
        Assert.Equal(EventCompletionCodeSuccess << CompletionCodeShift, dword2);
        Assert.Equal((uint)TrbTypeCommandCompletionEvent, (dword3 >> TrbTypeShift) & 0x3F);

        // Only the No-Op ran: after the Link toggled the consumer cycle state, TRB 0's
        // own still-cycle-1 bit no longer matches, so the ring reads as empty and no
        // second completion event is posted -- even though CRCR's own register, which
        // real hardware never rewrites while walking the ring (xHCI 1.2 section
        // 4.6.1.1), still reads back exactly what software wrote it.
        (uint secondDword0, uint secondDword1, uint secondDword2, uint secondDword3) = ReadEventTrb(memory, EventRingAddress, 1);
        Assert.Equal(0u, secondDword0);
        Assert.Equal(0u, secondDword1);
        Assert.Equal(0u, secondDword2);
        Assert.Equal(0u, secondDword3);

        uint crcrLow = ReadBar(memory, RegCommandRingControlLow);
        Assert.Equal(CommandRingAddress, crcrLow & CrcrPointerMask);
        Assert.Equal(CrcrRingCycleState, crcrLow & CrcrRingCycleState); // CRCR itself is untouched by the engine

        // Proving the toggle actually happened internally (XhciController's own
        // _commandRingCycleState, not CRCR): a fresh No-Op written with cycle 0 at
        // the same slot -- what a producer that has wrapped the ring writes next --
        // now matches and runs.
        WriteCommandTrb(memory, CommandRingAddress + 0 * 16, 0, 0, 0, TrbTypeNoOpCommand, cycle: false);
        RingCommandDoorbell(memory);
        (uint thirdDword0, _, uint thirdDword2, uint thirdDword3) = ReadEventTrb(memory, EventRingAddress, 1);
        Assert.Equal(CommandRingAddress, thirdDword0);
        Assert.Equal(EventCompletionCodeSuccess << CompletionCodeShift, thirdDword2);
        Assert.NotEqual(0u, thirdDword3 & TrbCycleBit); // the event ring's own producer cycle is unrelated and has not wrapped yet
    }

    [Fact]
    public void NoOpCommandPostsASuccessfulCompletionEvent()
    {
        (_, _, PhysicalMemoryBus memory, _) = BuildWithCommandRing();

        WriteCommandTrb(memory, CommandRingAddress, 0, 0, 0, TrbTypeNoOpCommand, cycle: true);
        RingCommandDoorbell(memory);

        (uint dword0, uint dword1, uint dword2, uint dword3) = ReadEventTrb(memory, EventRingAddress, 0);
        Assert.Equal(CommandRingAddress, dword0);
        Assert.Equal(0u, dword1);
        Assert.Equal(EventCompletionCodeSuccess << CompletionCodeShift, dword2);
        Assert.Equal((uint)TrbTypeCommandCompletionEvent, (dword3 >> TrbTypeShift) & 0x3F);
        Assert.Equal(0u, dword3 >> CommandTrbSlotIdShift); // No-Op names no slot
    }

    [Fact]
    public void ASlotThatWasNeverEnabledGivesSlotNotEnabled()
    {
        (_, _, PhysicalMemoryBus memory, _) = BuildWithCommandRing();

        WriteCommandTrb(memory, CommandRingAddress, 0, 0, 0, TrbTypeDisableSlot, cycle: true, extraFlags: 5u << CommandTrbSlotIdShift);
        RingCommandDoorbell(memory);

        (_, _, uint dword2, uint dword3) = ReadEventTrb(memory, EventRingAddress, 0);
        Assert.Equal(CompletionCodeSlotNotEnabled << CompletionCodeShift, dword2);
        Assert.Equal(5u, dword3 >> CommandTrbSlotIdShift);
    }

    [Fact]
    public void ResetEndpointSetsStoppedOnAHaltedEndpoint()
    {
        (_, _, PhysicalMemoryBus memory, _) = BuildWithCommandRing();
        uint deviceContext = 0x60000;
        byte slotId = EnableAndAddressSlot(memory, deviceContext);
        memory.WriteDword(EndpointContextBase(deviceContext, 1) + EndpointContextStateOffset, EndpointStateHalted);

        WriteCommandTrb(memory, CommandRingAddress + 2 * 16, 0, 0, 0, TrbTypeResetEndpoint, cycle: true,
            extraFlags: ((uint)slotId << CommandTrbSlotIdShift) | (1u << CommandTrbEndpointIdShift));
        RingCommandDoorbell(memory);

        (_, _, uint dword2, _) = ReadEventTrb(memory, EventRingAddress, 2);
        Assert.Equal(EventCompletionCodeSuccess << CompletionCodeShift, dword2);
        Assert.Equal(EndpointStateStopped, (byte)(memory.ReadDword(EndpointContextBase(deviceContext, 1) + EndpointContextStateOffset) & EndpointStateMask));
    }

    [Fact]
    public void StopEndpointSetsStoppedOnARunningEndpoint()
    {
        (_, _, PhysicalMemoryBus memory, _) = BuildWithCommandRing();
        uint deviceContext = 0x60000;
        byte slotId = EnableAndAddressSlot(memory, deviceContext);
        Assert.Equal(EndpointStateRunning, (byte)(memory.ReadDword(EndpointContextBase(deviceContext, 1) + EndpointContextStateOffset) & EndpointStateMask));

        WriteCommandTrb(memory, CommandRingAddress + 2 * 16, 0, 0, 0, TrbTypeStopEndpoint, cycle: true,
            extraFlags: ((uint)slotId << CommandTrbSlotIdShift) | (1u << CommandTrbEndpointIdShift));
        RingCommandDoorbell(memory);

        (_, _, uint dword2, _) = ReadEventTrb(memory, EventRingAddress, 2);
        Assert.Equal(EventCompletionCodeSuccess << CompletionCodeShift, dword2);
        Assert.Equal(EndpointStateStopped, (byte)(memory.ReadDword(EndpointContextBase(deviceContext, 1) + EndpointContextStateOffset) & EndpointStateMask));
    }

    [Fact]
    public void SetTrDequeuePointerWritesTheNewPointerAndDcsIntoTheEndpointContext()
    {
        (_, _, PhysicalMemoryBus memory, _) = BuildWithCommandRing();
        uint deviceContext = 0x60000;
        byte slotId = EnableAndAddressSlot(memory, deviceContext);
        const uint newDequeue = 0x48000;

        WriteCommandTrb(memory, CommandRingAddress + 2 * 16, newDequeue | EndpointDequeueCycleStateBit, 0, 0, TrbTypeSetTrDequeuePointer, cycle: true,
            extraFlags: ((uint)slotId << CommandTrbSlotIdShift) | (1u << CommandTrbEndpointIdShift));
        RingCommandDoorbell(memory);

        (_, _, uint dword2, _) = ReadEventTrb(memory, EventRingAddress, 2);
        Assert.Equal(EventCompletionCodeSuccess << CompletionCodeShift, dword2);
        uint dequeueLow = memory.ReadDword(EndpointContextBase(deviceContext, 1) + EndpointContextDequeueLowOffset);
        Assert.Equal(newDequeue, dequeueLow & 0xFFFF_FFF0u);
        Assert.NotEqual(0u, dequeueLow & EndpointDequeueCycleStateBit);
        Assert.Equal(0u, memory.ReadDword(EndpointContextBase(deviceContext, 1) + EndpointContextDequeueHighOffset));
    }

    [Fact]
    public void EvaluateContextUpdatesEp0MaxPacketSizeAndSlotFieldsOnly()
    {
        (_, _, PhysicalMemoryBus memory, _) = BuildWithCommandRing();
        uint deviceContext = 0x60000;
        byte slotId = EnableAndAddressSlot(memory, deviceContext);

        memory.WriteDword(InputContextAddress + InputControlContextDropFlagsOffset, 0);
        memory.WriteDword(InputContextAddress + InputControlContextAddFlagsOffset, 0x3); // A0 (slot) + A1 (EP0)
        memory.WriteDword(InputSlotContextBase(InputContextAddress) + SlotContextPortInfoOffset, 1u << SlotContextRootHubPortShift);
        memory.WriteDword(InputEndpointContextBase(InputContextAddress, 1) + EndpointContextTypeOffset, ((uint)EndpointTypeControl << EndpointTypeShift) | (8u << EndpointMaxPacketSizeShift));

        WriteCommandTrb(memory, CommandRingAddress + 2 * 16, InputContextAddress, 0, 0, TrbTypeEvaluateContext, cycle: true,
            extraFlags: (uint)slotId << CommandTrbSlotIdShift);
        RingCommandDoorbell(memory);

        (_, _, uint dword2, _) = ReadEventTrb(memory, EventRingAddress, 2);
        Assert.Equal(EventCompletionCodeSuccess << CompletionCodeShift, dword2);
        uint ep0Type = memory.ReadDword(EndpointContextBase(deviceContext, 1) + EndpointContextTypeOffset);
        Assert.Equal(8u, ep0Type >> EndpointMaxPacketSizeShift); // updated
        Assert.Equal(EndpointTypeControl, (byte)((ep0Type >> EndpointTypeShift) & 0x7)); // untouched
        Assert.Equal(EndpointStateRunning, (byte)(memory.ReadDword(EndpointContextBase(deviceContext, 1) + EndpointContextStateOffset) & EndpointStateMask)); // Evaluate Context never touches EP state
        uint slotState = memory.ReadDword(SlotContextBase(deviceContext) + SlotContextAddressAndStateOffset) & SlotContextSlotStateMask;
        Assert.Equal(SlotStateAddressed, (byte)(slotState >> SlotContextSlotStateShift)); // Evaluate Context never touches slot state
    }

    [Fact]
    public void DisableSlotFreesTheSlotSoALaterCommandGivesSlotNotEnabled()
    {
        (_, _, PhysicalMemoryBus memory, _) = BuildWithCommandRing();
        uint deviceContext = 0x60000;
        byte slotId = EnableAndAddressSlot(memory, deviceContext);

        WriteCommandTrb(memory, CommandRingAddress + 2 * 16, 0, 0, 0, TrbTypeDisableSlot, cycle: true,
            extraFlags: (uint)slotId << CommandTrbSlotIdShift);
        RingCommandDoorbell(memory);
        (_, _, uint disableDword2, _) = ReadEventTrb(memory, EventRingAddress, 2);
        Assert.Equal(EventCompletionCodeSuccess << CompletionCodeShift, disableDword2);

        WriteCommandTrb(memory, CommandRingAddress + 3 * 16, 0, 0, 0, TrbTypeStopEndpoint, cycle: true,
            extraFlags: ((uint)slotId << CommandTrbSlotIdShift) | (1u << CommandTrbEndpointIdShift));
        RingCommandDoorbell(memory);
        (_, _, uint afterDword2, _) = ReadEventTrb(memory, EventRingAddress, 3);
        Assert.Equal(CompletionCodeSlotNotEnabled << CompletionCodeShift, afterDword2);
    }

    // ===== Stage 4: transfer rings =====

    private const uint Ep0RingAddress = 0x40000; // matches EnableAndAddressSlot's own EP0 dequeue pointer
    private const byte Ep0EndpointId = 1;

    [Fact]
    public void ControlGetDescriptorDeviceFromUsbMassStorageDevice()
    {
        using var image = new MemoryStream(new byte[512]);
        var device = new UsbMassStorageDevice(image);
        (XhciController controller, _, PhysicalMemoryBus memory) = BuildWithCommandRingAndDevice(device, resetPort: true); // negotiates the port's speed before the slot is ever addressed
        uint deviceContext = 0x60000;
        EnableAndAddressSlot(memory, deviceContext);

        // SETUP GET_DESCRIPTOR(DEVICE, 18), IDT: bmRequestType=80h, bRequest=6, wValue=0100h, wIndex=0, wLength=18.
        WriteTransferTrb(memory, Ep0RingAddress + 0 * 16, 0x0100_0680, 0x0012_0000, 8, TrbTypeSetupStage, cycle: true, extraFlags: TrbFlagImmediateData);
        const uint dataBuffer = 0x70000;
        WriteTransferTrb(memory, Ep0RingAddress + 1 * 16, dataBuffer, 0, 18, TrbTypeDataStage, cycle: true, extraFlags: TrbDataStageDirectionBit);
        WriteTransferTrb(memory, Ep0RingAddress + 2 * 16, 0, 0, 0, TrbTypeStatusStage, cycle: true, extraFlags: TrbFlagInterruptOnCompletion); // OUT (DIR=0), status for an IN data stage

        RingTransferDoorbell(memory, 1, Ep0EndpointId);

        (_, _, uint dword2, uint dword3) = ReadEventTrb(memory, EventRingAddress, 2);
        Assert.Equal(EventCompletionCodeSuccess << CompletionCodeShift, dword2);
        Assert.Equal((uint)TrbTypeTransferEvent, (dword3 >> TrbTypeShift) & 0x3F);
        Assert.Equal(1u, dword3 >> CommandTrbSlotIdShift);
        Assert.Equal((uint)Ep0EndpointId, (dword3 >> TransferEventEndpointIdShift) & 0x1F);

        byte[] expected = [18, 1, 0, 2, 0, 0, 0, 64, 0xFE, 0xCA, 3, 0, 0, 1, 1, 2, 3, 1];
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], memory.ReadByte(dataBuffer + (uint)i));
        }
    }

    [Fact]
    public void BulkOnlyTransportReadTenLandsOneSectorInRam()
    {
        byte[] sector = new byte[512];
        for (int i = 0; i < sector.Length; i++)
        {
            sector[i] = (byte)i;
        }
        using var image = new MemoryStream(sector);
        var device = new UsbMassStorageDevice(image);
        (XhciController controller, _, PhysicalMemoryBus memory) = BuildWithCommandRingAndDevice(device, resetPort: true); // high speed: both bulk endpoints negotiate 512-byte packets
        uint deviceContext = 0x60000;
        byte slotId = EnableAndAddressSlot(memory, deviceContext);

        // SET_CONFIGURATION(1) on EP0: UsbMassStorageDevice NAKs every bulk transfer
        // until Configuration != 0.
        WriteTransferTrb(memory, Ep0RingAddress + 0 * 16, 0x0001_0900, 0, 8, TrbTypeSetupStage, cycle: true, extraFlags: TrbFlagImmediateData);
        WriteTransferTrb(memory, Ep0RingAddress + 1 * 16, 0, 0, 0, TrbTypeStatusStage, cycle: true, extraFlags: TrbDataStageDirectionBit | TrbFlagInterruptOnCompletion);
        RingTransferDoorbell(memory, slotId, Ep0EndpointId);
        (_, _, uint setConfigDword2, _) = ReadEventTrb(memory, EventRingAddress, 2);
        Assert.Equal(EventCompletionCodeSuccess << CompletionCodeShift, setConfigDword2);

        const uint bulkOutDequeue = 0x41000;
        const uint bulkInDequeue = 0x42000;
        // Enable Slot and Address Device took command slots 0 and 1; this is the third command.
        ConfigureEndpoints(memory, slotId, commandSlotIndex: 2,
            (EpId: 4, Type: EndpointTypeBulkOut, MaxPacket: 512, Dequeue: bulkOutDequeue), // EP1 OUT
            (EpId: 3, Type: EndpointTypeBulkIn, MaxPacket: 512, Dequeue: bulkInDequeue));  // EP1 IN

        const uint cbwBuffer = 0x71000;
        const uint dataBuffer = 0x72000;
        const uint cswBuffer = 0x73000;
        byte[] cbw = new byte[31];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(cbw, 0x43425355); // "USBC"
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(cbw.AsSpan(4), 0xABCDEF01); // tag
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(cbw.AsSpan(8), 512);
        cbw[12] = 0x80; // data-in
        cbw[14] = 10;   // CDB length
        cbw[15] = 0x28; // READ(10)
        cbw[15 + 7] = 0; cbw[15 + 8] = 1; // transfer length: one block
        for (int i = 0; i < cbw.Length; i++)
        {
            memory.WriteByte(cbwBuffer + (uint)i, cbw[i]);
        }

        WriteTransferTrb(memory, bulkOutDequeue, cbwBuffer, 0, (uint)cbw.Length, TrbTypeNormal, cycle: true, extraFlags: TrbFlagInterruptOnCompletion);
        RingTransferDoorbell(memory, slotId, 4);
        (_, _, uint cbwDword2, _) = ReadEventTrb(memory, EventRingAddress, 4);
        Assert.Equal(EventCompletionCodeSuccess << CompletionCodeShift, cbwDword2);

        WriteTransferTrb(memory, bulkInDequeue + 0 * 16, dataBuffer, 0, 512, TrbTypeNormal, cycle: true);
        WriteTransferTrb(memory, bulkInDequeue + 1 * 16, cswBuffer, 0, 13, TrbTypeNormal, cycle: true, extraFlags: TrbFlagInterruptOnCompletion);
        RingTransferDoorbell(memory, slotId, 3);
        (_, _, uint cswDword2, _) = ReadEventTrb(memory, EventRingAddress, 5);
        Assert.Equal(EventCompletionCodeSuccess << CompletionCodeShift, cswDword2);

        for (int i = 0; i < sector.Length; i++)
        {
            Assert.Equal(sector[i], memory.ReadByte(dataBuffer + (uint)i));
        }
        Assert.Equal(0x53425355u, memory.ReadDword(cswBuffer)); // "USBS"
        Assert.Equal(0xABCDEF01u, memory.ReadDword(cswBuffer + 4)); // the same tag
        Assert.Equal(0, memory.ReadByte(cswBuffer + 12)); // command status: pass
    }

    [Fact]
    public void InterruptInFromUsbHidDeviceIsRetriedAfterANak()
    {
        var device = new UsbHidDevice(keyboard: false);
        (XhciController controller, _, PhysicalMemoryBus memory) = BuildWithCommandRingAndDevice(device, resetPort: true); // low speed: UsbHidDevice.LowSpeed is always true
        uint deviceContext = 0x60000;
        byte slotId = EnableAndAddressSlot(memory, deviceContext);
        const uint interruptInDequeue = 0x44000;
        ConfigureEndpoints(memory, slotId, commandSlotIndex: 2,
            (EpId: 3, Type: EndpointTypeInterruptIn, MaxPacket: 8, Dequeue: interruptInDequeue)); // EP1 IN

        // SET_CONFIGURATION(1) on EP0, so the interrupt endpoint stops NAKing on "not configured".
        WriteTransferTrb(memory, Ep0RingAddress + 0 * 16, 0x0001_0900, 0, 8, TrbTypeSetupStage, cycle: true, extraFlags: TrbFlagImmediateData);
        WriteTransferTrb(memory, Ep0RingAddress + 1 * 16, 0, 0, 0, TrbTypeStatusStage, cycle: true, extraFlags: TrbDataStageDirectionBit | TrbFlagInterruptOnCompletion);
        RingTransferDoorbell(memory, slotId, Ep0EndpointId);
        (_, _, uint setConfigDword2, _) = ReadEventTrb(memory, EventRingAddress, 3);
        Assert.Equal(EventCompletionCodeSuccess << CompletionCodeShift, setConfigDword2);

        const uint reportBuffer = 0x75000;
        WriteTransferTrb(memory, interruptInDequeue + 0 * 16, reportBuffer, 0, 4, TrbTypeNormal, cycle: true, extraFlags: TrbFlagInterruptOnCompletion);
        RingTransferDoorbell(memory, slotId, 3); // the very first poll: the device starts dirty, so it ACKs
        (_, _, uint firstDword2, _) = ReadEventTrb(memory, EventRingAddress, 4);
        Assert.Equal(EventCompletionCodeSuccess << CompletionCodeShift, firstDword2);

        // A second TRB, no new input yet: the device NAKs (idle rate 0, nothing changed),
        // so the TD is left pending -- no event, and the dequeue pointer never moves past it.
        WriteTransferTrb(memory, interruptInDequeue + 1 * 16, reportBuffer, 0, 4, TrbTypeNormal, cycle: true, extraFlags: TrbFlagInterruptOnCompletion);
        RingTransferDoorbell(memory, slotId, 3);
        (uint pendingDword0, _, _, _) = ReadEventTrb(memory, EventRingAddress, 5);
        Assert.Equal(0u, pendingDword0);

        // A real input event makes the device dirty again; ringing the same doorbell
        // retries the still-pending TRB, which now succeeds.
        device.Move(5, 0);
        RingTransferDoorbell(memory, slotId, 3);
        (uint retriedDword0, _, uint retriedDword2, _) = ReadEventTrb(memory, EventRingAddress, 5);
        Assert.Equal(interruptInDequeue + 1 * 16, retriedDword0);
        Assert.Equal(EventCompletionCodeSuccess << CompletionCodeShift, retriedDword2);
    }

    [Fact]
    public void StallHaltsTheEndpointThenResetEndpointAndSetTrDequeueRecoverIt()
    {
        uint deviceContext = 0x60000;

        // TestDevice always ACKs, so STALL is exercised against a device built to
        // report it unconditionally -- exactly the "halted pipe" condition Reset
        // Endpoint and Set TR Dequeue Pointer exist to recover from.
        var stallingDevice = new StallingDevice();
        (XhciController stallController, _, PhysicalMemoryBus stallMemory) = BuildWithCommandRingAndDevice(stallingDevice);
        byte stallSlotId = EnableAndAddressSlot(stallMemory, deviceContext);
        const uint bulkInDequeue = 0x45000;
        ConfigureEndpoints(stallMemory, stallSlotId, commandSlotIndex: 2,
            (EpId: 3, Type: EndpointTypeBulkIn, MaxPacket: 64, Dequeue: bulkInDequeue));

        const uint dataBuffer = 0x76000;
        WriteTransferTrb(stallMemory, bulkInDequeue, dataBuffer, 0, 8, TrbTypeNormal, cycle: true, extraFlags: TrbFlagInterruptOnCompletion);
        RingTransferDoorbell(stallMemory, stallSlotId, 3);

        (_, _, uint dword2, _) = ReadEventTrb(stallMemory, EventRingAddress, 3);
        Assert.Equal(CompletionCodeStallError, dword2 >> CompletionCodeShift);
        Assert.Equal(8u, dword2 & 0xFFFFFFu); // the residual: none of the 8 requested bytes ever moved
        uint epState = ReadEndpointState(stallMemory, deviceContext, 3);
        Assert.Equal(EndpointStateHalted, (byte)epState);

        // Reset Endpoint: Halted -> Stopped.
        WriteCommandTrb(stallMemory, CommandRingAddress + 3 * 16, 0, 0, 0, TrbTypeResetEndpoint, cycle: true,
            extraFlags: ((uint)stallSlotId << CommandTrbSlotIdShift) | (3u << CommandTrbEndpointIdShift));
        RingCommandDoorbell(stallMemory);
        Assert.Equal(EndpointStateStopped, (byte)ReadEndpointState(stallMemory, deviceContext, 3));

        // Set TR Dequeue Pointer: point the ring back at TRB 0 so the same transfer can be retried.
        WriteCommandTrb(stallMemory, CommandRingAddress + 4 * 16, bulkInDequeue | EndpointDequeueCycleStateBit, 0, 0, TrbTypeSetTrDequeuePointer, cycle: true,
            extraFlags: ((uint)stallSlotId << CommandTrbSlotIdShift) | (3u << CommandTrbEndpointIdShift));
        RingCommandDoorbell(stallMemory);

        uint dequeueLow = stallMemory.ReadDword(EndpointContextBase(deviceContext, 3) + EndpointContextDequeueLowOffset);
        Assert.Equal(bulkInDequeue, dequeueLow & 0xFFFF_FFF0u);

        RingTransferDoorbell(stallMemory, stallSlotId, 3); // Stopped runs again once the doorbell rings
        Assert.Equal(EndpointStateRunning, (byte)ReadEndpointState(stallMemory, deviceContext, 3));

        // The retried TRB now completes: the device ACKs, so the event is a Success with no residual.
        (uint retryDword0, _, uint retryDword2, _) = ReadEventTrb(stallMemory, EventRingAddress, 6);
        Assert.Equal(bulkInDequeue, retryDword0);
        Assert.Equal(EventCompletionCodeSuccess << CompletionCodeShift, retryDword2);
    }

    private static uint ReadEndpointState(PhysicalMemoryBus memory, uint deviceContext, int epId)
        => memory.ReadDword(EndpointContextBase(deviceContext, epId) + EndpointContextStateOffset) & EndpointStateMask;

    /// <summary>STALLs its first transfer, then ACKs -- so a test can prove Reset Endpoint and Set TR Dequeue Pointer actually restore normal operation, not merely accept the commands.</summary>
    private sealed class StallingDevice : IUsbDevice
    {
        public byte Address { get; set; }
        public bool LowSpeed => false;
        public bool HighSpeedCapable => true;
        private bool _stalled;
        public void Reset() { }
        public UsbTransactionResult Transfer(byte pid, byte endpoint, bool data1, Span<byte> data)
        {
            // Only the bulk endpoint stalls, once: Address Device's own
            // SET_ADDRESS on endpoint 0 must go through.
            if (endpoint != 0 && !_stalled)
            {
                _stalled = true;
                return new(UsbHandshake.Stall);
            }
            return new(UsbHandshake.Ack, data.Length);
        }
        public byte[] Capture() => [];
        public void Restore(ReadOnlySpan<byte> data) { }
    }

    [Fact]
    public void ASelfLinkedTransferRingDoesNotHang()
    {
        (_, _, PhysicalMemoryBus memory, _) = BuildWithCommandRing();
        uint deviceContext = 0x60000;
        byte slotId = EnableAndAddressSlot(memory, deviceContext);
        const uint bulkInDequeue = 0x46000;
        ConfigureEndpoints(memory, slotId, commandSlotIndex: 2,
            (EpId: 3, Type: EndpointTypeBulkIn, MaxPacket: 64, Dequeue: bulkInDequeue));

        // A Link TRB pointing at itself, cycle bit matching, TC unset: the ring never
        // produces or consumes a real transfer TRB, only bounded by the loop's own cap.
        WriteTransferTrb(memory, bulkInDequeue, bulkInDequeue, 0, 0, TrbTypeLink, cycle: true);

        RingTransferDoorbell(memory, slotId, 3); // must return, not hang
        Assert.Equal(EndpointStateRunning, (byte)ReadEndpointState(memory, deviceContext, 3));
    }

    [Fact]
    public void BusMasterOffMeansNoTransferProgress()
    {
        (XhciController controller, PciBus pci, PhysicalMemoryBus memory, _) = BuildWithCommandRing();
        uint deviceContext = 0x60000;
        byte slotId = EnableAndAddressSlot(memory, deviceContext);
        const uint bulkInDequeue = 0x47000;
        ConfigureEndpoints(memory, slotId, commandSlotIndex: 2,
            (EpId: 3, Type: EndpointTypeBulkIn, MaxPacket: 64, Dequeue: bulkInDequeue));

        const uint dataBuffer = 0x77000;
        memory.WriteDword(dataBuffer, 0xDEADBEEF);
        WriteTransferTrb(memory, bulkInDequeue, dataBuffer, 0, 8, TrbTypeNormal, cycle: true, extraFlags: TrbFlagInterruptOnCompletion);

        pci.WriteConfiguration(FunctionAddress, 4, 2, 0x2); // memory decode stays on, bus master turned back off
        WriteBar(memory, RegUsbStatus, StatusEventInterrupt); // clears the sticky EINT the earlier setup commands already raised
        RingTransferDoorbell(memory, slotId, 3);

        Assert.Equal(0xDEADBEEFu, memory.ReadDword(dataBuffer)); // no DMA write happened
        uint dequeueLow = memory.ReadDword(EndpointContextBase(deviceContext, 3) + EndpointContextDequeueLowOffset);
        Assert.Equal(bulkInDequeue, dequeueLow & 0xFFFF_FFF0u); // the ring never advanced
        Assert.Equal(0u, ReadBar(memory, RegUsbStatus) & StatusEventInterrupt); // no new event was posted
    }

    /// <summary>Records every transaction (PID, endpoint, DATA0/1, payload) and ACKs it.</summary>
    private sealed class RecordingDevice : IUsbDevice
    {
        public byte Address { get; set; }
        public bool LowSpeed => false;
        public bool HighSpeedCapable => true;
        public List<(byte Pid, byte Endpoint, bool Data1, byte[] Payload)> Transactions { get; } = [];
        public void Reset() { }
        public UsbTransactionResult Transfer(byte pid, byte endpoint, bool data1, Span<byte> data)
        {
            Transactions.Add((pid, endpoint, data1, data.ToArray()));
            return new(UsbHandshake.Ack, data.Length);
        }
        public byte[] Capture() => [];
        public void Restore(ReadOnlySpan<byte> data) { }
    }

    private const byte WirePidOut = 0xE1;
    private const uint CompletionCodeCommandRingStopped = 24;
    private const uint CrcrCommandAbort = 1u << 2;
    private const uint CrcrCommandRingRunning = 1u << 3;
    private const uint CommandTrbTransferStatePreserve = 1u << 9; // TSP on Reset Endpoint, the same bit as BSR/DC

    [Fact]
    public void ANormalTrbWithImmediateDataSendsTheInlineBytesOut()
    {
        var device = new RecordingDevice();
        (_, _, PhysicalMemoryBus memory) = BuildWithCommandRingAndDevice(device);
        uint deviceContext = 0x60000;
        byte slotId = EnableAndAddressSlot(memory, deviceContext);
        const uint bulkOutDequeue = 0x48000;
        ConfigureEndpoints(memory, slotId, commandSlotIndex: 2,
            (EpId: 2, Type: EndpointTypeBulkOut, MaxPacket: 64, Dequeue: bulkOutDequeue)); // EP1 OUT
        device.Transactions.Clear();

        // IDT: dword0/dword1 hold the payload itself, not a buffer pointer (xHCI 1.2 section 6.4.1.1).
        WriteTransferTrb(memory, bulkOutDequeue, 0x44332211, 0x88776655, 5, TrbTypeNormal, cycle: true,
            extraFlags: TrbFlagImmediateData | TrbFlagInterruptOnCompletion);
        RingTransferDoorbell(memory, slotId, 2);

        (byte pid, byte endpoint, bool data1, byte[] payload) = Assert.Single(device.Transactions);
        Assert.Equal(WirePidOut, pid);
        Assert.Equal(1, endpoint);
        Assert.False(data1);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55 }, payload);
        (uint dword0, _, uint dword2, _) = ReadEventTrb(memory, EventRingAddress, 3);
        Assert.Equal(bulkOutDequeue, dword0);
        Assert.Equal(EventCompletionCodeSuccess << CompletionCodeShift, dword2); // residual 0: all 5 bytes went out
    }

    [Theory]
    [InlineData(false, false)] // Reset Endpoint without TSP: the host toggle restarts at DATA0
    [InlineData(true, true)] // TSP set: the toggle the endpoint had reached is preserved
    public void ResetEndpointClearsTheDataToggleUnlessTransferStatePreserveIsSet(bool preserve, bool expectedData1)
    {
        var device = new RecordingDevice();
        (_, _, PhysicalMemoryBus memory) = BuildWithCommandRingAndDevice(device);
        uint deviceContext = 0x60000;
        byte slotId = EnableAndAddressSlot(memory, deviceContext);
        const uint bulkOutDequeue = 0x48000;
        const uint dataBuffer = 0x78000;
        ConfigureEndpoints(memory, slotId, commandSlotIndex: 2,
            (EpId: 2, Type: EndpointTypeBulkOut, MaxPacket: 64, Dequeue: bulkOutDequeue));

        WriteTransferTrb(memory, bulkOutDequeue + 0 * 16, dataBuffer, 0, 4, TrbTypeNormal, cycle: true);
        RingTransferDoorbell(memory, slotId, 2); // one ACKed packet: the toggle is now DATA1
        memory.WriteDword(EndpointContextBase(deviceContext, 2) + EndpointContextStateOffset, EndpointStateHalted);

        uint preserveFlag = preserve ? CommandTrbTransferStatePreserve : 0;
        WriteCommandTrb(memory, CommandRingAddress + 3 * 16, 0, 0, 0, TrbTypeResetEndpoint, cycle: true,
            extraFlags: preserveFlag | ((uint)slotId << CommandTrbSlotIdShift) | (2u << CommandTrbEndpointIdShift));
        RingCommandDoorbell(memory);
        device.Transactions.Clear();

        WriteTransferTrb(memory, bulkOutDequeue + 1 * 16, dataBuffer, 0, 4, TrbTypeNormal, cycle: true);
        RingTransferDoorbell(memory, slotId, 2);

        Assert.Equal(expectedData1, Assert.Single(device.Transactions).Data1);
    }

    [Fact]
    public void ConfigureEndpointStartsAReaddedEndpointAtData0()
    {
        var device = new RecordingDevice();
        (_, _, PhysicalMemoryBus memory) = BuildWithCommandRingAndDevice(device);
        uint deviceContext = 0x60000;
        byte slotId = EnableAndAddressSlot(memory, deviceContext);
        const uint bulkOutDequeue = 0x48000;
        const uint dataBuffer = 0x78000;
        ConfigureEndpoints(memory, slotId, commandSlotIndex: 2,
            (EpId: 2, Type: EndpointTypeBulkOut, MaxPacket: 64, Dequeue: bulkOutDequeue));
        WriteTransferTrb(memory, bulkOutDequeue, dataBuffer, 0, 4, TrbTypeNormal, cycle: true);
        RingTransferDoorbell(memory, slotId, 2); // toggle now DATA1

        // SET_CONFIGURATION or SET_INTERFACE makes the device restart at DATA0 too (USB 2.0 section 9.1.1.5).
        const uint freshDequeue = 0x49000;
        ConfigureEndpoints(memory, slotId, commandSlotIndex: 3,
            (EpId: 2, Type: EndpointTypeBulkOut, MaxPacket: 64, Dequeue: freshDequeue));
        device.Transactions.Clear();
        WriteTransferTrb(memory, freshDequeue, dataBuffer, 0, 4, TrbTypeNormal, cycle: true);
        RingTransferDoorbell(memory, slotId, 2);

        Assert.False(Assert.Single(device.Transactions).Data1);
    }

    [Fact]
    public void CommandAbortWhileTheRingRunsPostsCommandRingStoppedAndClearsCommandRingRunning()
    {
        (_, _, PhysicalMemoryBus memory, _) = BuildWithCommandRing();
        WriteCommandTrb(memory, CommandRingAddress, 0, 0, 0, TrbTypeNoOpCommand, cycle: true);
        RingCommandDoorbell(memory);
        Assert.NotEqual(0u, ReadBar(memory, RegCommandRingControlLow) & CrcrCommandRingRunning);

        // While running, a pointer rewrite is ignored (xHCI 1.2 section 5.4.5)...
        WriteBar(memory, RegCommandRingControlLow, 0x90000 | CrcrRingCycleState);
        Assert.Equal(CommandRingAddress, ReadBar(memory, RegCommandRingControlLow) & CrcrPointerMask);

        // ...and CA stops the ring with a Command Ring Stopped completion at the next command's position.
        WriteBar(memory, RegCommandRingControlLow, CrcrCommandAbort);
        Assert.Equal(0u, ReadBar(memory, RegCommandRingControlLow) & CrcrCommandRingRunning);
        (uint dword0, _, uint dword2, uint dword3) = ReadEventTrb(memory, EventRingAddress, 1);
        Assert.Equal(CommandRingAddress + 16, dword0);
        Assert.Equal(CompletionCodeCommandRingStopped << CompletionCodeShift, dword2);
        Assert.Equal((uint)TrbTypeCommandCompletionEvent, (dword3 >> TrbTypeShift) & 0x3F);

        // Stopped, the ring accepts a new pointer again.
        WriteBar(memory, RegCommandRingControlLow, 0x90000 | CrcrRingCycleState);
        Assert.Equal(0x90000u, ReadBar(memory, RegCommandRingControlLow) & CrcrPointerMask);
    }

    [Fact]
    public void ClearingRunStopStopsTheCommandRing()
    {
        (_, _, PhysicalMemoryBus memory, _) = BuildWithCommandRing();
        WriteCommandTrb(memory, CommandRingAddress, 0, 0, 0, TrbTypeNoOpCommand, cycle: true);
        RingCommandDoorbell(memory);
        Assert.NotEqual(0u, ReadBar(memory, RegCommandRingControlLow) & CrcrCommandRingRunning);

        WriteBar(memory, RegUsbCommand, 0);

        Assert.Equal(0u, ReadBar(memory, RegCommandRingControlLow) & CrcrCommandRingRunning);
    }

    [Fact]
    public void AnUnknownCommandTypeCompletesWithTrbError()
    {
        (_, _, PhysicalMemoryBus memory, _) = BuildWithCommandRing();
        const uint vendorDefinedType = 48; // outside every command this subset (or xHCI 1.2) defines
        WriteCommandTrb(memory, CommandRingAddress, 0, 0, 0, vendorDefinedType, cycle: true);
        RingCommandDoorbell(memory);

        (uint dword0, _, uint dword2, _) = ReadEventTrb(memory, EventRingAddress, 0);
        Assert.Equal(CommandRingAddress, dword0);
        Assert.Equal(CompletionCodeTrbError << CompletionCodeShift, dword2);
    }

    [Fact]
    public void ResetEndpointNamingASlotAboveMaxSlotsDoesNotCrash()
    {
        (_, _, PhysicalMemoryBus memory, _) = BuildWithCommandRing();
        WriteCommandTrb(memory, CommandRingAddress, 0, 0, 0, TrbTypeResetEndpoint, cycle: true,
            extraFlags: (200u << CommandTrbSlotIdShift) | (2u << CommandTrbEndpointIdShift));
        RingCommandDoorbell(memory);

        (_, _, uint dword2, _) = ReadEventTrb(memory, EventRingAddress, 0);
        Assert.Equal(CompletionCodeSlotNotEnabled << CompletionCodeShift, dword2);
    }

    [Fact]
    public void OnlyPortscIsDecodedInsideEachPortRegisterSet()
    {
        (XhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemory(pci);
        controller.Attach(0, new TestDevice());

        // PORTPMSC (+4), PORTLI (+8) and PORTHLPMC (+Ch) read 0 and ignore writes in this subset.
        for (uint offset = 4; offset < PortRegisterStride; offset += 4)
        {
            WriteBar(memory, PortOffset(0) + offset, 0xFFFF_FFFF);
            Assert.Equal(0u, ReadBar(memory, PortOffset(0) + offset));
        }
        Assert.NotEqual(0u, ReadBar(memory, PortOffset(0)) & PortCurrentConnectStatus);
    }

    [Fact]
    public void ASixtyFourBitAbortWriteStopsTheRingWithoutRewindingIt()
    {
        (_, _, PhysicalMemoryBus memory, _) = BuildWithCommandRing();
        WriteCommandTrb(memory, CommandRingAddress + 0 * 16, 0, 0, 0, TrbTypeNoOpCommand, cycle: true);
        RingCommandDoorbell(memory);

        // Linux's xhci_abort_cmd_ring: all of CRCR, low dword first, pointer = its dequeue.
        WriteBar(memory, RegCommandRingControlLow, (CommandRingAddress + 16) | CrcrCommandAbort | CrcrRingCycleState);
        WriteBar(memory, RegCommandRingControlHigh, 0);
        (_, _, uint stoppedDword2, _) = ReadEventTrb(memory, EventRingAddress, 1);
        Assert.Equal(CompletionCodeCommandRingStopped << CompletionCodeShift, stoppedDword2);

        // The next doorbell resumes after the No-Op: the event names TRB 1, not a replayed TRB 0.
        WriteCommandTrb(memory, CommandRingAddress + 1 * 16, 0, 0, 0, TrbTypeNoOpCommand, cycle: true);
        RingCommandDoorbell(memory);
        (uint resumedDword0, _, uint resumedDword2, _) = ReadEventTrb(memory, EventRingAddress, 2);
        Assert.Equal(CommandRingAddress + 16, resumedDword0);
        Assert.Equal(EventCompletionCodeSuccess << CompletionCodeShift, resumedDword2);
        Assert.Equal(0u, ReadEventTrb(memory, EventRingAddress, 3).Dword3); // nothing else ran
    }

    [Fact]
    public void ResetDeviceReturnsTheSlotToDefaultWithAddressZeroAndOneContextEntry()
    {
        const uint trbTypeResetDevice = 17;
        const int contextEntriesShift = 27; // slot context dword0 bits 31:27
        (_, _, PhysicalMemoryBus memory, _) = BuildWithCommandRing();
        uint deviceContext = 0x60000;
        byte slotId = EnableAndAddressSlot(memory, deviceContext);
        ConfigureEndpoints(memory, slotId, commandSlotIndex: 2,
            (EpId: 3, Type: EndpointTypeBulkIn, MaxPacket: 512, Dequeue: 0x41000));
        memory.WriteDword(SlotContextBase(deviceContext), 3u << contextEntriesShift);

        WriteCommandTrb(memory, CommandRingAddress + 3 * 16, 0, 0, 0, trbTypeResetDevice, cycle: true,
            extraFlags: (uint)slotId << CommandTrbSlotIdShift);
        RingCommandDoorbell(memory);

        (_, _, uint dword2, _) = ReadEventTrb(memory, EventRingAddress, 3);
        Assert.Equal(EventCompletionCodeSuccess << CompletionCodeShift, dword2);
        uint addressAndState = memory.ReadDword(SlotContextBase(deviceContext) + SlotContextAddressAndStateOffset);
        Assert.Equal(SlotStateDefault, (byte)((addressAndState & SlotContextSlotStateMask) >> SlotContextSlotStateShift));
        Assert.Equal(0u, addressAndState & SlotContextDeviceAddressMask);
        Assert.Equal(1u, memory.ReadDword(SlotContextBase(deviceContext)) >> contextEntriesShift);
        Assert.Equal(0u, ReadEndpointState(memory, deviceContext, 3)); // Disabled
    }
}
