using Aurora86.Core.Devices.Interrupts;
using Aurora86.Core.Devices.Pci;
using Aurora86.Core.Devices.Usb;
using Aurora86.Core.IO;
using Aurora86.Core.Memory;

namespace Aurora86.Tests;

/// <summary>
/// Intel ICH9 EHCI (PCI 8086:293A): PCI identity, class, SBRN and BAR0
/// sizing, the fixed capability registers, USBCMD/USBSTS/USBINTR and
/// HCRESET (including the port-routing fallout of CONFIGFLAG resetting to
/// 0), FRINDEX advance and frame-list rollover on both of FRINDEX bit 13's
/// transitions, CONFIGFLAG/PORTSC port routing to the UHCI companion and
/// PORTSC.PE's write-0-only-clears semantics, the async schedule's QH+qTD
/// engine (control and bulk transfers with Data Toggle Control, IOC and
/// short-packet interrupts, STALL, Babble, a timeout's CERR countdown, the
/// IAAD doorbell, the qTD write-back to guest memory, and the bounds on a
/// halted queue head, an unarmed qTD and a circular qTD chain), one
/// periodic interrupt QH, the bus-master gate, and a snapshot round trip.
/// See <c>docs/USB.md</c>'s EHCI section for the register map as
/// implemented and <c>plans/usb2-ehci.md</c> for the coordinator's
/// decisions this class follows.
/// </summary>
public class EhciControllerTests
{
    private const ushort FunctionAddress = 0x1F; // device 3, function 7 (00:03.7): plan decision 2
    private const uint RamSize = 4 * 1024 * 1024;
    private const uint AddressableBytes = 8 * 1024 * 1024;
    private const uint Bar0Base = 0x700000; // above RamSize, inside AddressableBytes

    // --- capability/operational register offsets, mirroring EhciController's own private constants ---

    private const uint RegUsbCommand = 0x20;
    private const uint RegUsbStatus = 0x24;
    private const uint RegUsbInterruptEnable = 0x28;
    private const uint RegFrameIndex = 0x2C;
    private const uint RegPeriodicListBase = 0x34;
    private const uint RegAsyncListAddress = 0x38;
    private const uint RegConfigFlag = 0x60;
    private const uint RegPortStatusBase = 0x64;
    private const uint PortRegisterStride = 4;

    private const uint CommandRun = 1u << 0;
    private const uint CommandHostControllerReset = 1u << 1;
    private const uint CommandPeriodicScheduleEnable = 1u << 4;
    private const uint CommandAsyncScheduleEnable = 1u << 5;
    private const uint CommandInterruptOnAsyncAdvanceDoorbell = 1u << 6;
    private const uint CommandResetDefault = 0x08u << 16;

    private const uint StatusUsbInterrupt = 1u << 0;
    private const uint StatusUsbErrorInterrupt = 1u << 1;
    private const uint StatusPortChangeDetect = 1u << 2;
    private const uint StatusFrameListRollover = 1u << 3;
    private const uint StatusHostSystemError = 1u << 4;
    private const uint StatusInterruptOnAsyncAdvance = 1u << 5;
    private const uint StatusHalted = 1u << 12;

    private const uint PortConnectStatus = 1u << 0;
    private const uint PortConnectStatusChange = 1u << 1;
    private const uint PortEnabled = 1u << 2;
    private const uint PortReset = 1u << 8;
    private const uint PortOwnerBit = 1u << 13;

    // --- queue head layout (EHCI 1.0 section 3.6), mirroring EhciController.Schedule's own private constants ---

    private const uint QhHorizontalLinkOffset = 0x00;
    private const uint QhCharacteristicsOffset = 0x04;
    private const uint QhCapabilitiesOffset = 0x08;
    private const uint QhNextQtdOffset = 0x10;
    private const uint QhTokenOffset = 0x18;
    private const uint QhBufferPointerOffset = 0x1C;

    private const uint QtdNextOffset = 0x00;
    private const uint QtdAltNextOffset = 0x04;
    private const uint QtdTokenOffset = 0x08;
    private const uint QtdBufferPointerOffset = 0x0C;

    private const int EndpointNumberShift = 8;
    private const int MaxPacketLengthShift = 16;
    private const uint EndpointCharacteristicsDtcBit = 1u << 14; // Data Toggle Control

    private const uint LinkTerminate = 1u << 0;
    private const uint LinkTypeQueueHead = 1u << 1;

    private const uint TokenActive = 1u << 7;
    private const uint TokenHalted = 1u << 6;
    private const uint TokenBabble = 1u << 4;
    private const uint TokenXactErr = 1u << 3;
    private const int TokenPidShift = 8;
    private const int TokenCerrShift = 10;
    private const uint TokenIoc = 1u << 15;
    private const int TokenTotalBytesShift = 16;
    private const uint TokenTotalBytesMask = 0x7FFF;
    private const uint TokenDataToggle = 1u << 31;

    private const byte QtdPidOut = 0;
    private const byte QtdPidIn = 1;
    private const byte QtdPidSetup = 2;

    /// <summary>Mirrors EhciController.Schedule's own MaxQtdStepsPerQueueHead: how many qTDs one queue head can fetch-and-run within a single microframe.</summary>
    private const int MaxQtdStepsPerQueueHead = 16;

    private sealed class TestDevice : IUsbDevice
    {
        public byte Address { get; set; } = 1;
        public bool LowSpeed { get; set; }
        public bool HighSpeedCapable { get; set; } = true;
        public bool? SelectedHighSpeed { get; private set; }
        public UsbHandshake Handshake = UsbHandshake.Ack;
        public byte[] Data = [0x41, 0x42, 0x43];
        public int Calls;

        /// <summary>
        /// When set, an IN transfer reports this many bytes regardless of
        /// how much of <see cref="Data"/> actually fit the buffer offered --
        /// simulating a misbehaving device that claims more than the
        /// endpoint's own packet size, the condition EHCI calls babble. A
        /// real <see cref="Span{T}"/> can never actually be overfilled, so
        /// this is the only way to reach that handshake in a test.
        /// </summary>
        public int? ForcedInLength;

        public void Reset() { }

        public void SelectSpeed(bool highSpeed) => SelectedHighSpeed = highSpeed;

        public UsbTransactionResult Transfer(byte pid, byte endpoint, bool data1, Span<byte> data)
        {
            Calls++;
            if (Handshake != UsbHandshake.Ack)
            {
                return new(Handshake);
            }
            if (pid == 0x69) // IN
            {
                int count = Math.Min(Data.Length, data.Length);
                Data.AsSpan(0, count).CopyTo(data);
                return new(UsbHandshake.Ack, ForcedInLength ?? count);
            }
            Data = data.ToArray();
            return new(UsbHandshake.Ack, data.Length);
        }

        public byte[] Capture() => BitConverter.GetBytes(Calls);
        public void Restore(ReadOnlySpan<byte> data) => Calls = BitConverter.ToInt32(data);
    }

    private static (EhciController Controller, PciBus Pci, PhysicalMemoryBus Memory, PciInterruptRouter Router, InterruptController Pic) Build()
    {
        var pci = new PciBus();
        var io = new IoBus();
        var memory = new PhysicalMemoryBus(RamSize, AddressableBytes);
        memory.A20Enabled = true;
        var pic = new InterruptController();
        pic.Register(io);
        InitializePic(io);

        var bridge = new PciFunction(8, 0x8086, 0x122E, 0x060100);
        pci.Add(bridge);
        var router = new PciInterruptRouter(pic, bridge);
        pci.InterruptRouter = router;
        pci.DmaMemory = new PhysicalDmaMemory(memory); // the schedule and every payload byte move through here, not the CPU-path memory bus

        var controller = new EhciController(pci, memory, FunctionAddress);
        pci.WriteConfiguration(FunctionAddress, 0x10, 4, Bar0Base); // BAR0
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

    private static uint PortOffset(int port) => RegPortStatusBase + (uint)port * PortRegisterStride;

    /// <summary>Reset then release PR on <paramref name="port"/>: the sequence PORTSC.PR itself documents in <c>EhciController.Ports.cs</c>.</summary>
    private static void ResetPort(PhysicalMemoryBus memory, int port)
    {
        WriteBar(memory, PortOffset(port), PortReset);
        WriteBar(memory, PortOffset(port), 0);
    }

    private static void WriteQueueHead(PhysicalMemoryBus memory, uint address, uint horizontalLink, byte deviceAddress, byte endpoint,
        int maxPacketLength, byte interruptScheduleMask = 0, bool dataToggleControl = true)
    {
        memory.WriteDword(address + QhHorizontalLinkOffset, horizontalLink);
        uint characteristics = deviceAddress | ((uint)endpoint << EndpointNumberShift) | ((uint)maxPacketLength << MaxPacketLengthShift)
            | (dataToggleControl ? EndpointCharacteristicsDtcBit : 0);
        memory.WriteDword(address + QhCharacteristicsOffset, characteristics);
        memory.WriteDword(address + QhCapabilitiesOffset, interruptScheduleMask);
        memory.WriteDword(address + 0x0C, 0);
        memory.WriteDword(address + QhNextQtdOffset, LinkTerminate); // idle until a qTD is linked
        memory.WriteDword(address + 0x14, LinkTerminate);
        memory.WriteDword(address + QhTokenOffset, 0); // Active = 0
        for (int i = 0; i < 5; i++)
        {
            memory.WriteDword(address + QhBufferPointerOffset + (uint)(i * 4), 0);
        }
    }

    private static void WriteQtd(PhysicalMemoryBus memory, uint address, uint nextPointer, uint altNextPointer,
        byte pid, bool dataToggle, int totalBytes, uint bufferAddress, bool ioc = false, uint cerr = 3, bool active = true)
    {
        memory.WriteDword(address + QtdNextOffset, nextPointer);
        memory.WriteDword(address + QtdAltNextOffset, altNextPointer);
        uint token = (active ? TokenActive : 0) | ((uint)pid << TokenPidShift) | (cerr << TokenCerrShift)
            | ((uint)totalBytes << TokenTotalBytesShift) | (dataToggle ? TokenDataToggle : 0) | (ioc ? TokenIoc : 0);
        memory.WriteDword(address + QtdTokenOffset, token);
        memory.WriteDword(address + QtdBufferPointerOffset, bufferAddress);
        for (int i = 1; i < 5; i++)
        {
            memory.WriteDword(address + QtdBufferPointerOffset + (uint)(i * 4), (bufferAddress & 0xFFFF_F000u) + (uint)(i * 4096));
        }
    }

    /// <summary>
    /// Runs one control transfer to completion: writes SETUP, an optional
    /// data-stage qTD and the STATUS qTD (always DATA1, opposite direction
    /// from the data stage, or an IN status when there is none -- USB 2.0
    /// section 8.5.3), chains them onto <paramref name="qhAddress"/>, and
    /// advances one microframe, which is enough for the whole chain to
    /// retire when nothing NAKs (see EhciController.Schedule's own summary).
    /// This queue head keeps Data Toggle Control set, so each qTD's own
    /// toggle bit is exactly what the device sees.
    /// </summary>
    private static byte[] RunControlTransfer(EhciController controller, PhysicalMemoryBus memory, uint qhAddress,
        byte requestType, byte request, ushort value, ushort index, ushort length, uint scratch)
    {
        bool hostToDevice = (requestType & 0x80) == 0;
        uint setupBuffer = scratch;
        uint dataBuffer = scratch + 0x1000;
        uint setupQtd = scratch + 0x2000;
        uint dataQtd = scratch + 0x2040;
        uint statusQtd = scratch + 0x2080;

        byte[] setup = [requestType, request, (byte)value, (byte)(value >> 8), (byte)index, (byte)(index >> 8), (byte)length, (byte)(length >> 8)];
        for (int i = 0; i < setup.Length; i++)
        {
            memory.WriteByte(setupBuffer + (uint)i, setup[i]);
        }

        bool hasData = length > 0;
        uint afterSetup = hasData ? dataQtd : statusQtd;
        WriteQtd(memory, setupQtd, afterSetup, LinkTerminate, QtdPidSetup, false, 8, setupBuffer);
        if (hasData)
        {
            byte dataPid = hostToDevice ? QtdPidOut : QtdPidIn;
            WriteQtd(memory, dataQtd, statusQtd, LinkTerminate, dataPid, true, length, dataBuffer);
        }
        byte statusPid = hasData ? (hostToDevice ? QtdPidIn : QtdPidOut) : QtdPidIn;
        WriteQtd(memory, statusQtd, LinkTerminate, LinkTerminate, statusPid, true, 0, dataBuffer);

        memory.WriteDword(qhAddress + QhNextQtdOffset, setupQtd);
        controller.Advance(125);

        byte[] result = new byte[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = memory.ReadByte(dataBuffer + (uint)i);
        }
        return result;
    }

    /// <summary>
    /// Runs one bulk (or interrupt) transaction on <paramref name="qhAddress"/>
    /// using the standalone qTD at <paramref name="qtdAddress"/>, and
    /// returns that qTD's own remaining Total Bytes field (0 when the whole
    /// transfer completed) read back from its own guest memory -- proving
    /// the write-back EHCI 1.0 section 4.10.5 requires, not only the queue
    /// head's overlay (see EhciController.Schedule.WriteQtdToken).
    /// </summary>
    private static uint RunBulkTransfer(EhciController controller, PhysicalMemoryBus memory, uint qhAddress, uint qtdAddress,
        byte pid, bool dataToggle, int totalBytes, uint bufferAddress, bool ioc = false)
    {
        WriteQtd(memory, qtdAddress, LinkTerminate, LinkTerminate, pid, dataToggle, totalBytes, bufferAddress, ioc);
        memory.WriteDword(qhAddress + QhNextQtdOffset, qtdAddress);
        controller.Advance(125);
        uint token = memory.ReadDword(qtdAddress + QtdTokenOffset);
        return (token >> TokenTotalBytesShift) & TokenTotalBytesMask;
    }

    // ------------------------------------------------------------ PCI identity and capability registers

    [Fact]
    public void PciIdentityClassSbrnAndBar0SizeMatchTheIch9Ehci()
    {
        (EhciController controller, PciBus pci, _, _, _) = Build();

        Assert.Equal(0x293A_8086u, pci.ReadConfiguration(FunctionAddress, 0, 4));
        Assert.Equal(0x0C_0320u, controller.Function.ClassCode);
        Assert.Equal(0x20u, pci.ReadConfiguration(FunctionAddress, 0x60, 1)); // SBRN: USB 2.0

        pci.WriteConfiguration(FunctionAddress, 0x10, 4, uint.MaxValue);
        Assert.Equal(0xFFFF_F000u, pci.ReadConfiguration(FunctionAddress, 0x10, 4)); // 4 KB memory BAR0 (owner decision)
        pci.WriteConfiguration(FunctionAddress, 0x10, 4, Bar0Base);
    }

    [Fact]
    public void CapabilityRegistersReportCaplengthHciVersionAndFixedParameters()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemory(pci);

        Assert.Equal(0x0100_0020u, ReadBar(memory, 0x00)); // CAPLENGTH=20h, HCIVERSION=0100h
        Assert.Equal(0x0000_1202u, ReadBar(memory, 0x04)); // HCSPARAMS: N_PORTS=2, N_PCC=2, N_CC=1
        Assert.Equal(0x0000_0010u, ReadBar(memory, 0x08)); // HCCPARAMS: IST=1, EECP=0
        Assert.Equal(0u, ReadBar(memory, 0x800)); // reserved space inside the 4 KB BAR reads 0, not FFh
    }

    // ------------------------------------------------------------ USBCMD/USBSTS/FRINDEX

    [Fact]
    public void HcResetClearsOperationalStateAndRsGatesHcHalted()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemory(pci);

        WriteBar(memory, RegUsbCommand, CommandRun);
        Assert.Equal(0u, ReadBar(memory, RegUsbStatus) & StatusHalted);

        WriteBar(memory, RegUsbCommand, CommandHostControllerReset);
        Assert.Equal(CommandResetDefault, ReadBar(memory, RegUsbCommand)); // RS cleared, ITC back to its power-on value
        Assert.Equal(StatusHalted, ReadBar(memory, RegUsbStatus) & StatusHalted);
        Assert.Equal(0u, ReadBar(memory, RegAsyncListAddress));
        Assert.Equal(0u, ReadBar(memory, RegConfigFlag));
    }

    [Fact]
    public void HcResetRaisesPortRoutingChangedForEveryPortConfigFlagReclaimed()
    {
        (EhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemory(pci);
        WriteBar(memory, RegConfigFlag, 1); // reclaims both ports for EHCI, before any subscriber is listening

        var changed = new List<int>();
        controller.PortRoutingChanged += changed.Add;

        WriteBar(memory, RegUsbCommand, CommandHostControllerReset); // CONFIGFLAG goes back to 0: both ports return to the companion

        Assert.Equal([0, 1], changed.OrderBy(x => x).ToList());
        Assert.True(controller.PortOwnedByCompanion(0));
        Assert.True(controller.PortOwnedByCompanion(1));
    }

    [Fact]
    public void UsbStatusBitsAreWriteOneToClear()
    {
        (EhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemory(pci);

        controller.Attach(0, new TestDevice()); // CF=0 still routes it to the companion, but PCD/CSC are register-level effects regardless
        Assert.NotEqual(0u, ReadBar(memory, RegUsbStatus) & StatusPortChangeDetect);

        WriteBar(memory, RegUsbStatus, 0); // a zero write clears nothing
        Assert.NotEqual(0u, ReadBar(memory, RegUsbStatus) & StatusPortChangeDetect);

        WriteBar(memory, RegUsbStatus, StatusPortChangeDetect);
        Assert.Equal(0u, ReadBar(memory, RegUsbStatus) & StatusPortChangeDetect);
    }

    [Fact]
    public void FrameIndexAdvancesOneStepPerMicroframeAndRollsOverAtEachBit13Toggle()
    {
        (EhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);

        WriteBar(memory, RegUsbCommand, CommandRun);
        controller.Advance(1000); // 8 microframes = 1 ms
        Assert.Equal(8u, ReadBar(memory, RegFrameIndex));

        WriteBar(memory, RegUsbCommand, 0); // RS=0: FRINDEX becomes writable again
        WriteBar(memory, RegFrameIndex, 0x3FFF); // one microframe before the 14-bit register wraps: bit 13 is set
        WriteBar(memory, RegUsbCommand, CommandRun);
        controller.Advance(125);

        Assert.Equal(0u, ReadBar(memory, RegFrameIndex));
        Assert.NotEqual(0u, ReadBar(memory, RegUsbStatus) & StatusFrameListRollover); // bit 13 toggled 1 -> 0 at the wrap

        WriteBar(memory, RegUsbCommand, 0);
        WriteBar(memory, RegUsbStatus, StatusFrameListRollover); // clear the stray bit from the wrap above
        WriteBar(memory, RegFrameIndex, 0x1FFF); // one microframe before FRINDEX's own halfway point
        WriteBar(memory, RegUsbCommand, CommandRun);
        controller.Advance(125);

        Assert.Equal(0x2000u, ReadBar(memory, RegFrameIndex));
        Assert.NotEqual(0u, ReadBar(memory, RegUsbStatus) & StatusFrameListRollover); // bit 13 toggled 0 -> 1 halfway through the period too
    }

    // ------------------------------------------------------------ CONFIGFLAG / PORTSC routing

    [Fact]
    public void ConfigFlagZeroRoutesEveryPortToTheCompanion()
    {
        (EhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemory(pci);

        Assert.True(controller.PortOwnedByCompanion(0));
        Assert.True(controller.PortOwnedByCompanion(1));
        Assert.NotEqual(0u, ReadBar(memory, PortOffset(0)) & PortOwnerBit);

        WriteBar(memory, RegConfigFlag, 1);
        Assert.False(controller.PortOwnedByCompanion(0));
        Assert.False(controller.PortOwnedByCompanion(1));

        WriteBar(memory, RegConfigFlag, 0);
        Assert.True(controller.PortOwnedByCompanion(0));
        Assert.True(controller.PortOwnedByCompanion(1));
    }

    [Fact]
    public void PortResetEnablesAHighSpeedDeviceButNotAFullSpeedOne()
    {
        (EhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemory(pci);
        WriteBar(memory, RegConfigFlag, 1);

        var highSpeedDevice = new TestDevice { HighSpeedCapable = true };
        var fullSpeedDevice = new TestDevice { HighSpeedCapable = false, Address = 2 };
        controller.Attach(0, highSpeedDevice);
        controller.Attach(1, fullSpeedDevice);

        WriteBar(memory, PortOffset(0), PortReset);
        Assert.Equal(0u, ReadBar(memory, PortOffset(0)) & PortEnabled); // PE reads 0 while reset is held
        WriteBar(memory, PortOffset(0), 0);
        Assert.NotEqual(0u, ReadBar(memory, PortOffset(0)) & PortEnabled);
        Assert.Equal(true, highSpeedDevice.SelectedHighSpeed);

        ResetPort(memory, 1);
        Assert.Equal(0u, ReadBar(memory, PortOffset(1)) & PortEnabled);
        Assert.Null(fullSpeedDevice.SelectedHighSpeed);
    }

    [Fact]
    public void PortEnabledCanOnlyBeClearedByWritingZeroNeverSetByWritingOne()
    {
        (EhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemory(pci);
        WriteBar(memory, RegConfigFlag, 1);
        controller.Attach(0, new TestDevice());
        ResetPort(memory, 0);
        uint enabled = ReadBar(memory, PortOffset(0));
        Assert.NotEqual(0u, enabled & PortEnabled);

        // A driver's ordinary read-modify-write (to touch some other bit, leaving PE=1
        // in the value it writes back) must not disable an already-enabled port.
        WriteBar(memory, PortOffset(0), enabled);
        Assert.NotEqual(0u, ReadBar(memory, PortOffset(0)) & PortEnabled);

        // Only writing PE as a literal 0 clears it.
        WriteBar(memory, PortOffset(0), enabled & ~PortEnabled);
        Assert.Equal(0u, ReadBar(memory, PortOffset(0)) & PortEnabled);
    }

    [Fact]
    public void WritingPortOwnerWhileConfiguredRaisesPortRoutingChanged()
    {
        (EhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemory(pci);
        var changed = new List<int>();
        controller.PortRoutingChanged += changed.Add;

        WriteBar(memory, RegConfigFlag, 1); // reclaims both ports for EHCI: two routing changes
        Assert.Equal([0, 1], changed.OrderBy(x => x).ToList());

        changed.Clear();
        WriteBar(memory, PortOffset(1), PortOwnerBit); // hands port 1 back to the companion
        Assert.True(controller.PortOwnedByCompanion(1));
        Assert.False(controller.PortOwnedByCompanion(0));
        Assert.Equal([1], changed);
    }

    [Fact]
    public void AttachSetsConnectStatusChangeAndPortChangeDetect()
    {
        (EhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemory(pci);
        WriteBar(memory, RegConfigFlag, 1);

        controller.Attach(0, new TestDevice());
        uint portsc = ReadBar(memory, PortOffset(0));
        Assert.NotEqual(0u, portsc & PortConnectStatus);
        Assert.NotEqual(0u, portsc & PortConnectStatusChange);
        Assert.NotEqual(0u, ReadBar(memory, RegUsbStatus) & StatusPortChangeDetect);

        WriteBar(memory, PortOffset(0), PortConnectStatusChange);
        Assert.Equal(0u, ReadBar(memory, PortOffset(0)) & PortConnectStatusChange);
    }

    [Fact]
    public void AnAlreadyAttachedDeviceReadsCleanAfterAControllerReset()
    {
        (EhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemory(pci);
        controller.Attach(0, new TestDevice());
        WriteBar(memory, PortOffset(0), PortConnectStatusChange); // acknowledge the attach itself first

        controller.Reset();

        uint portsc = ReadBar(memory, PortOffset(0));
        Assert.NotEqual(0u, portsc & PortConnectStatus); // the device is still there
        Assert.Equal(0u, portsc & PortConnectStatusChange); // but a register reset alone is not a new connect event
        Assert.Equal(0u, ReadBar(memory, RegUsbStatus) & StatusPortChangeDetect);
    }

    // ------------------------------------------------------------ async schedule: control and bulk transfers

    [Fact]
    public void AsyncControlTransferReadsHighSpeedDescriptorsAndTwoBulkReadCyclesLandInRam()
    {
        (EhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);

        byte[] sector0 = Enumerable.Range(0, 512).Select(i => (byte)(i * 3 + 7)).ToArray();
        byte[] sector1 = Enumerable.Range(0, 512).Select(i => (byte)(i * 5 + 11)).ToArray();
        byte[] imageBytes = new byte[4096];
        sector0.CopyTo(imageBytes, 0);
        sector1.CopyTo(imageBytes, 512);
        using var disk = new UsbMassStorageDevice(new MemoryStream(imageBytes));

        WriteBar(memory, RegConfigFlag, 1);
        controller.Attach(0, disk);
        ResetPort(memory, 0);
        Assert.NotEqual(0u, ReadBar(memory, PortOffset(0)) & PortEnabled);

        WriteBar(memory, RegUsbCommand, CommandRun | CommandAsyncScheduleEnable);
        const uint controlQh = 0x10000;
        const uint bulkOutQh = 0x11000;
        const uint bulkInQh = 0x12000;
        WriteQueueHead(memory, controlQh, bulkOutQh | LinkTypeQueueHead, 0, 0, 64);
        // Bulk endpoints keep Data Toggle Control clear: exactly as Linux's ehci-hcd
        // does for every non-control queue head, every qTD below carries a fixed
        // placeholder toggle of 0 and trusts the controller's own self-maintained one.
        WriteQueueHead(memory, bulkOutQh, bulkInQh | LinkTypeQueueHead, 0, 2, 512, dataToggleControl: false);
        WriteQueueHead(memory, bulkInQh, controlQh | LinkTypeQueueHead, 0, 1, 512, dataToggleControl: false);
        WriteBar(memory, RegAsyncListAddress, controlQh);

        // GET_DESCRIPTOR(DEVICE): bcdUSB reads 0200h (bytes 2-3) once this high-speed-capable device is on an EHCI port.
        byte[] device = RunControlTransfer(controller, memory, controlQh, 0x80, 6, 0x0100, 0, 18, 0x20000);
        Assert.Equal(0x02, device[3]);
        Assert.Equal(0x00, device[2]);

        // GET_DESCRIPTOR(CONFIGURATION): the bulk endpoints report 512-byte packets at high speed.
        byte[] config = RunControlTransfer(controller, memory, controlQh, 0x80, 6, 0x0200, 0, 32, 0x21000);
        Assert.Equal(512, config[22] | (config[23] << 8));

        RunControlTransfer(controller, memory, controlQh, 0x00, 9, 1, 0, 0, 0x22000); // SET_CONFIGURATION(1)

        // Two full Bulk-Only Transport cycles back to back: CBW, the data stage, then
        // the CSW, each qTD carrying toggle 0 since Data Toggle Control is off above --
        // proving the controller's own self-maintained toggle survives across qTDs.
        RunReadCycle(controller, memory, bulkOutQh, bulkInQh, lba: 0, tag: 0xABCDEF01, expected: sector0, scratch: 0x23000);
        RunReadCycle(controller, memory, bulkOutQh, bulkInQh, lba: 1, tag: 0xABCDEF02, expected: sector1, scratch: 0x28000);
    }

    /// <summary>One Bulk-Only Transport READ(10) of a single 512-byte sector: CBW, the data stage, then the CSW.</summary>
    private static void RunReadCycle(EhciController controller, PhysicalMemoryBus memory, uint bulkOutQh, uint bulkInQh,
        uint lba, uint tag, byte[] expected, uint scratch)
    {
        byte[] cbw = new byte[31];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(cbw, 0x43425355);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(cbw.AsSpan(4), tag);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(cbw.AsSpan(8), 512);
        cbw[12] = 0x80; // data-in
        cbw[14] = 10;   // CDB length
        cbw[15] = 0x28; // READ(10), CDB byte 0 -- the CDB itself starts at cbw[15]
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(cbw.AsSpan(15 + 2), lba); // CDB bytes 2-5: LBA
        cbw[15 + 7] = 0; cbw[15 + 8] = 1; // CDB bytes 7-8: transfer length, big-endian, one block

        uint cbwBuffer = scratch;
        uint dataBuffer = scratch + 0x1000;
        uint cswBuffer = scratch + 0x2000;
        for (int i = 0; i < cbw.Length; i++)
        {
            memory.WriteByte(cbwBuffer + (uint)i, cbw[i]);
        }

        Assert.Equal(0u, RunBulkTransfer(controller, memory, bulkOutQh, scratch + 0x3000, QtdPidOut, false, cbw.Length, cbwBuffer));
        Assert.Equal(0u, RunBulkTransfer(controller, memory, bulkInQh, scratch + 0x3040, QtdPidIn, false, 512, dataBuffer));
        for (int i = 0; i < 512; i++)
        {
            Assert.Equal(expected[i], memory.ReadByte(dataBuffer + (uint)i));
        }
        Assert.Equal(0u, RunBulkTransfer(controller, memory, bulkInQh, scratch + 0x3080, QtdPidIn, false, 13, cswBuffer));
        Assert.Equal(0x53425355u, memory.ReadDword(cswBuffer));
        Assert.Equal(tag, memory.ReadDword(cswBuffer + 4));
        Assert.Equal(0, memory.ReadByte(cswBuffer + 12)); // command status: pass
    }

    [Fact]
    public void IocCompletionAssertsUsbInterruptAtThePicAndRw1cClearsIt()
    {
        (EhciController controller, PciBus pci, PhysicalMemoryBus memory, PciInterruptRouter router, InterruptController pic) = Build();
        EnableMemoryAndBusMaster(pci);
        Assert.True(router.Route(FunctionAddress, 1, 11));
        WriteBar(memory, RegConfigFlag, 1);
        var device = new TestDevice();
        controller.Attach(0, device);
        ResetPort(memory, 0);

        WriteBar(memory, RegUsbInterruptEnable, StatusUsbInterrupt);
        WriteBar(memory, RegUsbCommand, CommandRun | CommandAsyncScheduleEnable);
        const uint qh = 0x40000;
        WriteQueueHead(memory, qh, qh | LinkTypeQueueHead, device.Address, 1, 64);
        WriteBar(memory, RegAsyncListAddress, qh);

        RunBulkTransfer(controller, memory, qh, 0x41000, QtdPidIn, false, 3, 0x42000, ioc: true);

        Assert.NotEqual(0u, ReadBar(memory, RegUsbStatus) & StatusUsbInterrupt);
        Assert.True(pic.HasPendingInterrupt);

        WriteBar(memory, RegUsbStatus, StatusUsbInterrupt);
        Assert.False(pic.HasPendingInterrupt);
    }

    [Fact]
    public void ShortPacketWithoutIocStillSetsUsbInterrupt()
    {
        (EhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);
        WriteBar(memory, RegConfigFlag, 1);
        var device = new TestDevice(); // answers 3 bytes
        controller.Attach(0, device);
        ResetPort(memory, 0);

        WriteBar(memory, RegUsbCommand, CommandRun | CommandAsyncScheduleEnable);
        const uint qh = 0x43000;
        WriteQueueHead(memory, qh, qh | LinkTypeQueueHead, device.Address, 1, 64);
        WriteBar(memory, RegAsyncListAddress, qh);

        // The qTD asks for 64 bytes but the device answers only 3: a short packet, with IOC off.
        RunBulkTransfer(controller, memory, qh, 0x44000, QtdPidIn, false, 64, 0x45000, ioc: false);

        Assert.NotEqual(0u, ReadBar(memory, RegUsbStatus) & StatusUsbInterrupt);
    }

    [Fact]
    public void StallHaltsTheQueueHeadAndRaisesUsbErrorInterrupt()
    {
        (EhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);
        WriteBar(memory, RegConfigFlag, 1);
        var device = new TestDevice { Handshake = UsbHandshake.Stall };
        controller.Attach(0, device);
        ResetPort(memory, 0);

        WriteBar(memory, RegUsbCommand, CommandRun | CommandAsyncScheduleEnable);
        const uint qh = 0x50000;
        WriteQueueHead(memory, qh, qh | LinkTypeQueueHead, device.Address, 1, 64);
        WriteBar(memory, RegAsyncListAddress, qh);

        RunBulkTransfer(controller, memory, qh, 0x51000, QtdPidIn, false, 3, 0x52000);
        uint token = memory.ReadDword(0x51000 + QtdTokenOffset);
        Assert.NotEqual(0u, token & TokenHalted);
        Assert.Equal(0u, token & TokenActive);
        Assert.NotEqual(0u, ReadBar(memory, RegUsbStatus) & StatusUsbErrorInterrupt);
    }

    [Fact]
    public void StallLeavesAQueuedQtdUnrun()
    {
        (EhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);
        WriteBar(memory, RegConfigFlag, 1);
        var device = new TestDevice { Handshake = UsbHandshake.Stall };
        controller.Attach(0, device);
        ResetPort(memory, 0);

        WriteBar(memory, RegUsbCommand, CommandRun | CommandAsyncScheduleEnable);
        const uint qh = 0x5A000;
        const uint stallingQtd = 0x5A100;
        const uint queuedQtd = 0x5A200;
        WriteQueueHead(memory, qh, qh | LinkTypeQueueHead, device.Address, 1, 64);
        WriteBar(memory, RegAsyncListAddress, qh);
        WriteQtd(memory, queuedQtd, LinkTerminate, LinkTerminate, QtdPidIn, false, 3, 0x5A300);
        WriteQtd(memory, stallingQtd, queuedQtd, LinkTerminate, QtdPidIn, false, 3, 0x5A400);
        memory.WriteDword(qh + QhNextQtdOffset, stallingQtd);

        controller.Advance(125);

        Assert.Equal(1, device.Calls); // only the stalling qTD was ever attempted
        uint queuedToken = memory.ReadDword(queuedQtd + QtdTokenOffset);
        Assert.NotEqual(0u, queuedToken & TokenActive); // untouched: the halted queue head never reached it
    }

    [Fact]
    public void BabbleSetsBabbleDetectedAndHalts()
    {
        (EhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);
        WriteBar(memory, RegConfigFlag, 1);
        var device = new TestDevice { ForcedInLength = 5 }; // claims 5 bytes back from a 3-byte packet
        controller.Attach(0, device);
        ResetPort(memory, 0);

        WriteBar(memory, RegUsbCommand, CommandRun | CommandAsyncScheduleEnable);
        const uint qh = 0x53000;
        WriteQueueHead(memory, qh, qh | LinkTypeQueueHead, device.Address, 1, 3); // max packet length 3: the device's own reply babbles
        WriteBar(memory, RegAsyncListAddress, qh);

        RunBulkTransfer(controller, memory, qh, 0x54000, QtdPidIn, false, 3, 0x55000);

        uint token = memory.ReadDword(0x54000 + QtdTokenOffset);
        Assert.NotEqual(0u, token & TokenBabble);
        Assert.Equal(0u, token & TokenActive);
    }

    [Fact]
    public void TimeoutSetsXactErrOnEveryRetryThenHaltsOnceCerrIsExhausted()
    {
        (EhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);
        WriteBar(memory, RegConfigFlag, 1);
        var device = new TestDevice { Handshake = UsbHandshake.Timeout };
        controller.Attach(0, device);
        ResetPort(memory, 0);

        WriteBar(memory, RegUsbCommand, CommandRun | CommandAsyncScheduleEnable);
        const uint qh = 0x56000;
        const uint qtd = 0x57000;
        WriteQueueHead(memory, qh, qh | LinkTypeQueueHead, device.Address, 1, 64);
        WriteBar(memory, RegAsyncListAddress, qh);
        WriteQtd(memory, qtd, LinkTerminate, LinkTerminate, QtdPidIn, false, 3, 0x58000, cerr: 2);
        memory.WriteDword(qh + QhNextQtdOffset, qtd);

        controller.Advance(125); // one of two CERR credits spent: XactErr is set, but the qTD is still Active
        uint token = memory.ReadDword(qtd + QtdTokenOffset);
        Assert.NotEqual(0u, token & TokenXactErr);
        Assert.NotEqual(0u, token & TokenActive);
        Assert.Equal(0u, token & TokenHalted);

        controller.Advance(125); // the second and last credit: now it halts
        token = memory.ReadDword(qtd + QtdTokenOffset);
        Assert.NotEqual(0u, token & TokenXactErr);
        Assert.NotEqual(0u, token & TokenHalted);
        Assert.Equal(0u, token & TokenActive);
    }

    [Fact]
    public void IaadDoorbellSetsIaaImmediately()
    {
        (EhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);

        WriteBar(memory, RegUsbCommand, CommandRun | CommandAsyncScheduleEnable | CommandInterruptOnAsyncAdvanceDoorbell);

        Assert.NotEqual(0u, ReadBar(memory, RegUsbStatus) & StatusInterruptOnAsyncAdvance);
        Assert.Equal(0u, ReadBar(memory, RegUsbCommand) & CommandInterruptOnAsyncAdvanceDoorbell); // self-clearing
    }

    [Fact]
    public void PeriodicQueueHeadRunsAnInterruptInFromATestDevice()
    {
        (EhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);
        WriteBar(memory, RegConfigFlag, 1);
        var device = new TestDevice();
        controller.Attach(0, device);
        ResetPort(memory, 0);

        const uint periodicList = 0x60000;
        const uint qh = 0x61000;
        WriteQueueHead(memory, qh, LinkTerminate, device.Address, 1, 64, interruptScheduleMask: 0xFF);
        WriteQtd(memory, 0x62000, LinkTerminate, LinkTerminate, QtdPidIn, false, 3, 0x63000);
        memory.WriteDword(qh + QhNextQtdOffset, 0x62000);
        for (uint entry = 0; entry < 1024; entry++)
        {
            memory.WriteDword(periodicList + entry * 4, qh | LinkTypeQueueHead);
        }
        WriteBar(memory, RegPeriodicListBase, periodicList);
        WriteBar(memory, RegUsbCommand, CommandRun | CommandPeriodicScheduleEnable);

        controller.Advance(125);

        Assert.Equal(1, device.Calls);
        for (int i = 0; i < device.Data.Length; i++)
        {
            Assert.Equal(device.Data[i], memory.ReadByte(0x63000 + (uint)i));
        }
    }

    [Fact]
    public void BusMasterDisabledMeansNoScheduleProgress()
    {
        (EhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemory(pci); // memory space only: no bus master
        WriteBar(memory, RegConfigFlag, 1);
        var device = new TestDevice();
        controller.Attach(0, device);
        ResetPort(memory, 0);

        WriteBar(memory, RegUsbCommand, CommandRun | CommandAsyncScheduleEnable);
        const uint qh = 0x64000;
        WriteQueueHead(memory, qh, qh | LinkTypeQueueHead, device.Address, 1, 64);
        WriteBar(memory, RegAsyncListAddress, qh);
        WriteQtd(memory, 0x65000, LinkTerminate, LinkTerminate, QtdPidIn, false, 3, 0x66000);
        memory.WriteDword(qh + QhNextQtdOffset, 0x65000);

        controller.Advance(1000);

        Assert.Equal(0, device.Calls);
        Assert.Equal(8u, ReadBar(memory, RegFrameIndex)); // FRINDEX is a timer, independent of bus mastering
    }

    [Fact]
    public void MaxPacketLengthZeroWithAPendingQtdMakesNoProgressAndDoesNotHang()
    {
        (EhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);
        WriteBar(memory, RegConfigFlag, 1);
        var device = new TestDevice();
        controller.Attach(0, device);
        ResetPort(memory, 0);

        WriteBar(memory, RegUsbCommand, CommandRun | CommandAsyncScheduleEnable);
        const uint qh = 0x67000; // Linux's own permanent async head shape: Maximum Packet Length 0
        WriteQueueHead(memory, qh, qh | LinkTypeQueueHead, device.Address, 1, 0);
        WriteBar(memory, RegAsyncListAddress, qh);
        WriteQtd(memory, 0x67100, LinkTerminate, LinkTerminate, QtdPidOut, false, 8, 0x67200);
        memory.WriteDword(qh + QhNextQtdOffset, 0x67100);

        controller.Advance(1000); // several microframes: an OUT that can never move a byte must not spin forever

        Assert.Equal(0, device.Calls);
        uint token = memory.ReadDword(0x67100 + QtdTokenOffset);
        Assert.NotEqual(0u, token & TokenActive); // stuck exactly like a permanently NAKing endpoint would leave it
    }

    [Fact]
    public void ASelfLinkedInactiveQtdIsNeverLoadedAndStopsThePass()
    {
        (EhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);
        WriteBar(memory, RegConfigFlag, 1);
        var device = new TestDevice();
        controller.Attach(0, device);
        ResetPort(memory, 0);

        WriteBar(memory, RegUsbCommand, CommandRun | CommandAsyncScheduleEnable);
        const uint qh = 0x68000;
        const uint qtd = 0x68100;
        WriteQueueHead(memory, qh, qh | LinkTypeQueueHead, device.Address, 1, 64);
        WriteBar(memory, RegAsyncListAddress, qh);
        WriteQtd(memory, qtd, qtd, LinkTerminate, QtdPidIn, false, 3, 0x68200, active: false); // never armed, and links to itself
        memory.WriteDword(qh + QhNextQtdOffset, qtd);

        controller.Advance(125);

        Assert.Equal(0, device.Calls);
        Assert.Equal(qtd, memory.ReadDword(qh + QhNextQtdOffset));   // the queue head still points at it
    }

    /// <summary>
    /// A qTD linked before it is armed runs once the driver sets Active: the
    /// controller does not load an inactive qTD, so the queue head keeps
    /// pointing at it (EHCI 1.0 section 4.10.2), the pattern SeaBIOS's
    /// ehci_send_pipe relies on.
    /// </summary>
    [Fact]
    public void AQtdLinkedBeforeItIsArmedRunsOnceArmed()
    {
        (EhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);
        WriteBar(memory, RegConfigFlag, 1);
        var device = new TestDevice();
        controller.Attach(0, device);
        ResetPort(memory, 0);

        WriteBar(memory, RegUsbCommand, CommandRun | CommandAsyncScheduleEnable);
        const uint qh = 0x6A000;
        const uint qtd = 0x6A100;
        WriteQueueHead(memory, qh, qh | LinkTypeQueueHead, device.Address, 1, 64);
        WriteBar(memory, RegAsyncListAddress, qh);
        WriteQtd(memory, qtd, LinkTerminate, LinkTerminate, QtdPidIn, false, 3, 0x6A200, active: false);
        memory.WriteDword(qh + QhNextQtdOffset, qtd);

        controller.Advance(125);
        Assert.Equal(0, device.Calls);

        WriteQtd(memory, qtd, LinkTerminate, LinkTerminate, QtdPidIn, false, 3, 0x6A200, active: true);
        controller.Advance(125);

        Assert.True(device.Calls > 0);
    }

    [Fact]
    public void ARingOfActiveQtdsIsBoundedPerMicroframe()
    {
        (EhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);
        WriteBar(memory, RegConfigFlag, 1);
        var device = new TestDevice(); // Data.Length == 3, matched exactly below so every packet is a full, non-short completion
        controller.Attach(0, device);
        ResetPort(memory, 0);

        WriteBar(memory, RegUsbCommand, CommandRun | CommandAsyncScheduleEnable);
        const uint qh = 0x69000;
        const int ringLength = MaxQtdStepsPerQueueHead * 2; // well past the cap, so it never runs to completion in one pass
        WriteQueueHead(memory, qh, qh | LinkTypeQueueHead, device.Address, 1, 64);
        WriteBar(memory, RegAsyncListAddress, qh);

        uint RingQtd(int index) => 0x69100 + (uint)index * 0x40;
        for (int i = 0; i < ringLength; i++)
        {
            uint next = RingQtd((i + 1) % ringLength);
            WriteQtd(memory, RingQtd(i), next, LinkTerminate, QtdPidIn, false, 3, 0x6A000);
        }
        memory.WriteDword(qh + QhNextQtdOffset, RingQtd(0));

        controller.Advance(125);

        Assert.True(device.Calls <= MaxQtdStepsPerQueueHead, $"expected at most {MaxQtdStepsPerQueueHead} transactions, got {device.Calls}");
    }

    // ------------------------------------------------------------ snapshot

    [Fact]
    public void SnapshotRoundTripsEveryGuestVisibleRegister()
    {
        (EhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);
        WriteBar(memory, RegConfigFlag, 1);
        var device = new TestDevice();
        controller.Attach(0, device);
        ResetPort(memory, 0);
        WriteBar(memory, RegUsbInterruptEnable, StatusUsbInterrupt | StatusPortChangeDetect);
        WriteBar(memory, RegUsbCommand, CommandRun | CommandAsyncScheduleEnable);
        WriteBar(memory, RegAsyncListAddress, 0x70000);
        controller.Advance(1000);

        byte[] snapshot = controller.Capture();
        uint expectedCommand = ReadBar(memory, RegUsbCommand);
        uint expectedFrameIndex = ReadBar(memory, RegFrameIndex);
        uint expectedPortSc = ReadBar(memory, PortOffset(0));

        controller.Reset();
        Assert.Equal(0u, ReadBar(memory, RegAsyncListAddress));

        controller.Restore(snapshot, EhciController.SnapshotVersion);
        Assert.Equal(expectedCommand, ReadBar(memory, RegUsbCommand));
        Assert.Equal(expectedFrameIndex, ReadBar(memory, RegFrameIndex));
        Assert.Equal(expectedPortSc, ReadBar(memory, PortOffset(0)));
        Assert.Equal(0x70000u, ReadBar(memory, RegAsyncListAddress));
        Assert.False(controller.PortOwnedByCompanion(0));
    }
}
