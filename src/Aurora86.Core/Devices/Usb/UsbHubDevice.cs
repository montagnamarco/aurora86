using System.Buffers.Binary;

namespace Aurora86.Core.Devices.Usb;

/// <summary>
/// A USB 1.1 hub (device class 09h, USB 1.1 chapter 11): the ordinary way a
/// period system reaches more downstream ports than a UHCI root hub's own
/// two, whether that is an external four-port desk hub or the pass-through
/// port on a keyboard. It enumerates like any other device on its control
/// endpoint, reports which of its ports changed on an interrupt IN endpoint,
/// and forwards every other transaction downstream by address through <see
/// cref="Find"/> -- the mechanism <see cref="UhciController.ExecuteTd"/>
/// already uses for its own two root ports, extended one hop at a time so a
/// device several tiers deep is reached the same way a directly attached one
/// is.
///
/// Ports are individually power switched and individually over-current
/// protected (USB 1.1 table 11-13's wHubCharacteristics), which is why
/// SET_FEATURE(PORT_POWER) and CLEAR_FEATURE(PORT_POWER) are per-port here
/// rather than ganged. A port reset completes after a short emulated delay
/// (<see cref="PortResetRecoveryMicroseconds"/>, driven from <see
/// cref="Advance"/>) rather than synchronously on the SET_FEATURE that
/// starts it, so that a driver polling GET_PORT_STATUS immediately
/// afterwards still finds PORT_RESET set, exactly as real hardware -- whose
/// downstream transceivers take real time to settle -- would make it wait.
/// </summary>
public sealed class UsbHubDevice : IUsbDevice
{
    /// <summary>The port count a hub gets when none is requested -- a common size for the period's add-on hubs.</summary>
    public const int DefaultPortCount = 4;

    // USB 1.1 section 4.1.1 allows at most five tiers of hubs between the
    // root hub and a device. Find() enforces the same bound defensively, so
    // a front end that mistakenly plugs a hub into its own downstream port
    // (or into a loop of hubs) gets a clean lookup failure instead of a
    // stack overflow.
    private const int MaxHubTiers = 5;

    // Endpoints this device answers on; every other endpoint STALLs.
    private const byte ControlEndpoint = 0;
    private const byte InterruptInEndpoint = 1;
    private const byte InterruptInEndpointAddress = 0x80 | InterruptInEndpoint;
    private const byte ControlEndpointMaxPacketSize = 8;

    // USB packet IDs, as they appear on the wire (USB 2.0 section 8.3.1).
    private const byte PidSetup = 0x2D;
    private const byte PidIn = 0x69;
    private const byte PidOut = 0xE1;

    // bmRequestType bytes this device recognises: direction, standard/class
    // type and recipient combined into the one byte a SETUP packet carries.
    // Hub class requests (USB 1.1 section 11.16) reuse GET_STATUS's,
    // CLEAR_FEATURE's, SET_FEATURE's and GET_DESCRIPTOR's own bRequest
    // numbers; only the recipient bits tell "the hub itself" (Device, here
    // "ClassDevice") apart from "one of its ports" (Other, here "ClassOther").
    private const byte RequestTypeStandardDeviceOut = 0x00;
    private const byte RequestTypeStandardInterfaceOut = 0x01;
    private const byte RequestTypeStandardEndpointOut = 0x02;
    private const byte RequestTypeStandardDeviceIn = 0x80;
    private const byte RequestTypeStandardInterfaceIn = 0x81;
    private const byte RequestTypeStandardEndpointIn = 0x82;
    private const byte RequestTypeClassDeviceOut = 0x20;
    private const byte RequestTypeClassDeviceIn = 0xA0;
    private const byte RequestTypeClassOtherOut = 0x23;
    private const byte RequestTypeClassOtherIn = 0xA3;
    private const byte RequestDirectionDeviceToHostBit = 0x80; // bit 7 of bmRequestType

    // Standard (USB 2.0 chapter 9) bRequest values.
    private const byte RequestGetStatus = 0;
    private const byte RequestClearFeature = 1;
    private const byte RequestSetFeature = 3;
    private const byte RequestSetAddress = 5;
    private const byte RequestGetDescriptor = 6;
    private const byte RequestGetConfiguration = 8;
    private const byte RequestSetConfiguration = 9;
    private const byte RequestGetInterface = 10;
    private const byte RequestSetInterface = 11;

    // GET_DESCRIPTOR's wValue high byte.
    private const byte DescriptorTypeDevice = 1;
    private const byte DescriptorTypeConfiguration = 2;
    private const byte DescriptorTypeString = 3;
    private const byte DescriptorTypeHub = 0x29; // USB 1.1 table 11-12, class descriptor type "HUB"
    private const int DescriptorTypeShift = 8;

    // USB defines the same class code, 09h ("Hub"), for both the device
    // descriptor's bDeviceClass and the one interface's bInterfaceClass.
    private const byte HubClassCode = 0x09;

    // Feature selectors a port (bmRequestType recipient "Other") accepts in
    // SET_PORT_FEATURE, CLEAR_PORT_FEATURE and GET_PORT_STATUS (USB 1.1
    // table 11-17). The "Change" ones name a wPortChange bit rather than a
    // wPortStatus one; PORT_CONNECTION, PORT_OVER_CURRENT and
    // PORT_LOW_SPEED are status the hub reports but the host cannot set or
    // clear directly, so no case below ever matches them.
    private const ushort PortFeatureEnable = 1;
    private const ushort PortFeatureSuspend = 2;
    private const ushort PortFeatureReset = 4;
    private const ushort PortFeaturePower = 8;
    private const ushort PortFeatureChangeConnection = 16;
    private const ushort PortFeatureChangeEnable = 17;
    private const ushort PortFeatureChangeSuspend = 18;
    private const ushort PortFeatureChangeOverCurrent = 19;
    private const ushort PortFeatureChangeReset = 20;

    // The two feature selectors CLEAR_HUB_FEATURE (recipient "Device") may
    // clear (USB 1.1 table 11-17); this emulation never sets either one, so
    // clearing them is always accepted and never does anything observable.
    private const ushort HubFeatureLocalPowerChange = 0;
    private const ushort HubFeatureOverCurrentChange = 1;

    // wPortStatus bits (USB 1.1 table 11-15).
    private const ushort PortStatusConnection = 1 << 0;
    private const ushort PortStatusEnable = 1 << 1;
    private const ushort PortStatusSuspend = 1 << 2;
    private const ushort PortStatusReset = 1 << 4;
    private const ushort PortStatusPower = 1 << 8;
    private const ushort PortStatusLowSpeed = 1 << 9;

    // wPortChange bits (USB 1.1 table 11-16).
    private const ushort PortChangeConnection = 1 << 0;
    private const ushort PortChangeEnable = 1 << 1;
    private const ushort PortChangeSuspend = 1 << 2;
    private const ushort PortChangeReset = 1 << 4;

    // Hub class descriptor fields (USB 1.1 table 11-13).
    private const ushort HubCharacteristicsIndividualPowerSwitching = 1 << 0; // bits 1:0 = 01b, "individual"
    private const ushort HubCharacteristicsIndividualOverCurrent = 1 << 3;    // bits 4:3 = 01b, "individual"
    private const byte HubPowerOnToPowerGoodUnits = 50; // 100 ms in 2 ms units: advisory only, this emulation needs no matching delay
    private const byte HubControllerCurrentMilliamps = 50;
    private const byte PortPwrCtrlMaskByte = 0xFF; // legacy ganged-power compatibility field the spec still requires present

    // The reset recovery time a real hub holds a downstream port in reset
    // (USB 1.1 section 7.1.7.5, TRSTRCY) before the port comes up enabled: a
    // guest polling GET_PORT_STATUS right after SET_FEATURE(PORT_RESET)
    // still finds PORT_RESET set until this much emulated time has passed.
    private const double PortResetRecoveryMicroseconds = 10_000;

    // Explicit values preserve the snapshot representation across a rebuild.
    private enum ControlStage
    {
        Idle = 0,
        DataIn = 1,
        DataOut = 2,
        StatusIn = 3,
        StatusOut = 4,
    }

    /// <summary>
    /// One downstream port: what is plugged in, whether the port is
    /// powered, enabled, suspended or mid-reset, and which status-change
    /// bits are pending for the interrupt endpoint to report.
    /// </summary>
    private sealed class Port
    {
        public IUsbDevice? Device;
        public bool Powered;
        public bool Enabled;
        public bool Suspended;
        public bool Resetting;
        public double ResetRemainingMicroseconds;
        public bool ChangeConnection;
        public bool ChangeEnable;
        public bool ChangeSuspend;
        public bool ChangeReset;

        public bool HasPendingChange => ChangeConnection || ChangeEnable || ChangeSuspend || ChangeReset;
    }

    private readonly Port[] _ports;
    private readonly int _changeBitmapLength; // ceil((PortCount + 1) / 8) bytes: bit 0 is the hub itself, bit n is port n

    public byte Address { get; private set; }
    public byte Configuration { get; private set; }
    public bool LowSpeed => false; // every hub this emulator builds is full speed
    public int PortCount => _ports.Length;

    private bool _interruptHalted;
    private bool _interruptToggle;
    private byte[] _lastChangeBitmap = [];
    private byte[] _reply = [];
    private byte[] _setup = [];
    private int _controlOffset;
    private ControlStage _controlStage;
    private bool _controlToggle = true;

    public UsbHubDevice(int ports = DefaultPortCount)
    {
        if (ports is < 1 or > 255)
        {
            // bNbrPorts in the hub descriptor is one byte (USB 1.1 table
            // 11-13); nothing this emulator builds needs more than that.
            throw new ArgumentOutOfRangeException(nameof(ports), "A USB 1.1 hub descriptor's port count is one byte.");
        }

        _ports = new Port[ports];
        for (int i = 0; i < ports; i++)
        {
            _ports[i] = new Port();
        }
        _changeBitmapLength = (ports + 1 + 7) / 8;
    }

    /// <summary>
    /// Plugs (or, with <paramref name="device"/> null, unplugs) whatever
    /// sits behind downstream port <paramref name="port"/> (zero-based),
    /// live -- the same hot attach/detach <see cref="UhciController.Attach"/>
    /// offers for a root port, usable from a front end holding this hub, or
    /// from <c>vm.json</c> wiring at machine-build time. The change looks
    /// like a fresh connect or disconnect to the guest driver, and a port
    /// that was enabled loses that enable the instant the wire changes,
    /// exactly as a real one would.
    /// </summary>
    public void Attach(int port, IUsbDevice? device)
    {
        Port state = PortAt(port);
        if (ReferenceEquals(state.Device, device))
        {
            return;
        }

        bool wasEnabled = state.Enabled;
        state.Device = device;
        state.Enabled = false;
        state.Resetting = false;
        device?.Reset();

        if (state.Powered)
        {
            // Nothing is observable while unpowered: the driver only learns
            // about the change once the port is powered, which raises the
            // same connect change fresh at that point (see SetPortPower).
            state.ChangeConnection = true;
            if (wasEnabled)
            {
                state.ChangeEnable = true;
            }
        }
    }

    public IUsbDevice? Find(byte address, int depth = 0)
    {
        if (Address == address)
        {
            return this;
        }
        if (depth >= MaxHubTiers)
        {
            return null;
        }

        foreach (Port port in _ports)
        {
            // A disabled, suspended or resetting port forwards nothing,
            // the same rule UhciController applies to its own root ports.
            if (!port.Enabled || port.Device is null)
            {
                continue;
            }
            if (port.Device.Find(address, depth + 1) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    public void Reset()
    {
        // A bus reset delivered to this hub (an upstream port resetting it)
        // resets only the hub's own enumeration state, the same as any
        // other device's Reset(). A real hub keeps driving power to its
        // downstream ports across its own reset, so a device already
        // enumerated behind it is not rediscovered every time the host
        // resets the hub; BeginPortReset is the separate, per-port reset a
        // driver issues explicitly to a downstream device.
        Address = Configuration = 0;
        _interruptHalted = false;
        _interruptToggle = false;
        _controlToggle = true;
        _lastChangeBitmap = [];
        _reply = _setup = [];
        _controlOffset = 0;
        _controlStage = ControlStage.Idle;
    }

    public void Advance(double microseconds)
    {
        foreach (Port port in _ports)
        {
            port.Device?.Advance(microseconds);
            if (!port.Resetting)
            {
                continue;
            }

            port.ResetRemainingMicroseconds -= microseconds;
            if (port.ResetRemainingMicroseconds > 0)
            {
                continue;
            }

            port.Resetting = false;
            port.Enabled = port.Device is not null;
            port.ChangeReset = true;
        }
    }

    public UsbTransactionResult Transfer(byte pid, byte endpoint, bool data1, Span<byte> data)
    {
        if (endpoint == InterruptInEndpoint && pid == PidIn)
        {
            return TransferInterrupt(data1, data);
        }

        if (endpoint != ControlEndpoint)
        {
            return new(UsbHandshake.Stall);
        }
        if (pid == PidSetup)
        {
            _controlStage = ControlStage.Idle;
            return data.Length == 8 && !data1 && BeginControl(data)
                ? new(UsbHandshake.Ack, 8) : new(UsbHandshake.Stall);
        }
        if (_controlStage == ControlStage.StatusIn && pid == PidIn && data1 && data.Length == 0)
        {
            CommitControl();
            return new(UsbHandshake.Ack);
        }
        if (_controlStage == ControlStage.StatusOut && pid == PidOut && data1 && data.Length == 0)
        {
            CommitControl();
            return new(UsbHandshake.Ack);
        }
        if (data1 != _controlToggle)
        {
            return new(UsbHandshake.Nak);
        }
        if (_controlStage == ControlStage.DataIn && pid == PidIn)
        {
            int count = Math.Min(Math.Min(data.Length, ControlEndpointMaxPacketSize), _reply.Length - _controlOffset);
            _reply.AsSpan(_controlOffset, count).CopyTo(data);
            _controlOffset += count;
            _controlToggle = !_controlToggle;
            int requested = BinaryPrimitives.ReadUInt16LittleEndian(_setup.AsSpan(6));
            if (count < ControlEndpointMaxPacketSize || _controlOffset >= requested)
            {
                _controlStage = ControlStage.StatusOut;
            }
            return new(UsbHandshake.Ack, count);
        }
        return new(UsbHandshake.Stall);
    }

    /// <summary>
    /// The interrupt IN endpoint's one packet: a bitmap with bit 0 for the
    /// hub's own status change (never raised: this emulation never loses
    /// local power or reports an over-current condition at the hub level)
    /// and bit n for port n, or NAK while nothing has changed. A retransmit
    /// (the data toggle not having advanced) hands back the same bitmap
    /// rather than a freshly computed one, so a change already reported is
    /// never lost to a dropped ACK.
    /// </summary>
    private UsbTransactionResult TransferInterrupt(bool data1, Span<byte> data)
    {
        if (Configuration == 0)
        {
            return new(UsbHandshake.Nak);
        }
        if (_interruptHalted)
        {
            return new(UsbHandshake.Stall);
        }
        if (data1 != _interruptToggle)
        {
            if (_lastChangeBitmap.Length == 0 || data.Length < _lastChangeBitmap.Length)
            {
                return new(UsbHandshake.Nak);
            }
            _lastChangeBitmap.CopyTo(data);
            return new(UsbHandshake.Ack, _lastChangeBitmap.Length);
        }

        byte[] bitmap = PendingChangeBitmap();
        if (bitmap.Length == 0)
        {
            return new(UsbHandshake.Nak);
        }
        if (data.Length < bitmap.Length)
        {
            return new(UsbHandshake.Stall);
        }
        _lastChangeBitmap = bitmap;
        bitmap.CopyTo(data);
        _interruptToggle = !_interruptToggle;
        return new(UsbHandshake.Ack, bitmap.Length);
    }

    private byte[] PendingChangeBitmap()
    {
        byte[] bitmap = new byte[_changeBitmapLength];
        bool any = false;
        for (int i = 0; i < _ports.Length; i++)
        {
            if (!_ports[i].HasPendingChange)
            {
                continue;
            }
            int bit = i + 1; // bit 0 is reserved for the hub's own status change
            bitmap[bit / 8] |= (byte)(1 << (bit % 8));
            any = true;
        }
        return any ? bitmap : [];
    }

    /// <summary>
    /// One of the standard descriptors GET_DESCRIPTOR answers: the device
    /// descriptor (idVendor/idProduct 0xCAFE/0x0004, a fictitious pair
    /// reserved for this emulator, distinct from the HID and mass-storage
    /// devices' own), the configuration descriptor (one Hub-class interface
    /// with a single interrupt IN endpoint), and the language ID string
    /// descriptor. The hub class descriptor itself is <see
    /// cref="HubDescriptor"/>, fetched through a different bmRequestType.
    /// </summary>
    private byte[] Descriptor(byte type) => type switch
    {
        DescriptorTypeDevice =>
        [
            18, DescriptorTypeDevice, 0x10, 1, HubClassCode, 0, 0, ControlEndpointMaxPacketSize,
            0xFE, 0xCA, 4, 0, 0, 1, 0, 0, 0, 1,
        ],
        DescriptorTypeConfiguration =>
        [
            9, DescriptorTypeConfiguration, 25, 0, 1, 1, 0, 0x80, HubControllerCurrentMilliamps,
            9, 4, 0, 0, 1, HubClassCode, 0, 0, 0,
            7, 5, InterruptInEndpointAddress, 3, (byte)_changeBitmapLength, 0, 255,
        ],
        DescriptorTypeString => [4, 3, 9, 4], // 0x0409: English (United States)
        _ => [],
    };

    /// <summary>
    /// The class-specific hub descriptor (USB 1.1 table 11-13): port count,
    /// individual power switching and over-current reporting, the
    /// power-on-to-power-good advisory delay, and the two legacy per-port
    /// bitmaps (DeviceRemovable -- every port here is user-removable, so it
    /// stays all zero -- and PortPwrCtrlMask, which the spec still requires
    /// present and set to all ones).
    /// </summary>
    private byte[] HubDescriptor()
    {
        byte[] descriptor = new byte[7 + 2 * _changeBitmapLength];
        descriptor[0] = (byte)descriptor.Length;
        descriptor[1] = DescriptorTypeHub;
        descriptor[2] = (byte)_ports.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(3),
            (ushort)(HubCharacteristicsIndividualPowerSwitching | HubCharacteristicsIndividualOverCurrent));
        descriptor[5] = HubPowerOnToPowerGoodUnits;
        descriptor[6] = HubControllerCurrentMilliamps;
        for (int i = 0; i < _changeBitmapLength; i++)
        {
            descriptor[7 + _changeBitmapLength + i] = PortPwrCtrlMaskByte;
        }
        return descriptor;
    }

    private Port PortAt(int port)
    {
        if ((uint)port >= (uint)_ports.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
        return _ports[port];
    }

    private ushort PortStatusWord(Port port)
    {
        if (!port.Powered)
        {
            return 0;
        }

        ushort status = PortStatusPower;
        if (port.Device is not null)
        {
            status |= PortStatusConnection;
            if (port.Device.LowSpeed)
            {
                status |= PortStatusLowSpeed;
            }
        }
        if (port.Enabled)
        {
            status |= PortStatusEnable;
        }
        if (port.Suspended)
        {
            status |= PortStatusSuspend;
        }
        if (port.Resetting)
        {
            status |= PortStatusReset;
        }
        return status;
    }

    private static ushort PortChangeWord(Port port)
    {
        ushort change = 0;
        if (port.ChangeConnection)
        {
            change |= PortChangeConnection;
        }
        if (port.ChangeEnable)
        {
            change |= PortChangeEnable;
        }
        if (port.ChangeSuspend)
        {
            change |= PortChangeSuspend;
        }
        if (port.ChangeReset)
        {
            change |= PortChangeReset;
        }
        return change;
    }

    /// <summary>
    /// A port whose power is individually switched (USB 1.1 section
    /// 11.11): turning it off makes an attached device read as gone -- VBUS
    /// is removed, so the wire looks unplugged -- without touching <see
    /// cref="Port.Device"/> itself, since the physical connection is a
    /// separate concern <see cref="Attach"/> owns. Turning it back on makes
    /// an already-attached device look like a fresh connect, the same way
    /// real bus power ramping up does.
    /// </summary>
    private static void SetPortPower(Port port, bool powered)
    {
        if (port.Powered == powered)
        {
            return;
        }

        port.Powered = powered;
        if (!powered)
        {
            port.Enabled = false;
            port.Suspended = false;
            port.Resetting = false;
            return;
        }

        if (port.Device is not null)
        {
            port.ChangeConnection = true;
        }
    }

    /// <summary>
    /// Starts the reset sequence SET_FEATURE(PORT_RESET) begins: the
    /// downstream device sees a fresh bus reset immediately (so its address
    /// and configuration are gone the moment a driver could next address
    /// it), the port reads PORT_RESET and not PORT_ENABLE for <see
    /// cref="PortResetRecoveryMicroseconds"/>, and <see cref="Advance"/>
    /// completes it: PORT_RESET clears, PORT_ENABLE sets, and C_PORT_RESET
    /// is raised for the interrupt endpoint to report.
    /// </summary>
    private static void BeginPortReset(Port port)
    {
        if (port.Device is null || !port.Powered)
        {
            return; // nothing there to reset
        }

        port.Device.Reset();
        port.Enabled = false;
        port.Resetting = true;
        port.ResetRemainingMicroseconds = PortResetRecoveryMicroseconds;
    }

    private bool BeginControl(ReadOnlySpan<byte> setup)
    {
        _setup = setup.ToArray();
        _controlOffset = 0;
        _controlToggle = true;
        _reply = [];
        byte type = setup[0], request = setup[1];
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(setup[2..]);
        ushort index = BinaryPrimitives.ReadUInt16LittleEndian(setup[4..]);
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(setup[6..]);
        int port = index - 1; // port-recipient requests number ports from one

        switch ((type, request))
        {
            case (RequestTypeStandardDeviceIn, RequestGetDescriptor) when (value & 0xFF) == 0:
                _reply = Descriptor((byte)(value >> DescriptorTypeShift));
                if (_reply.Length == 0)
                {
                    return false;
                }
                break;
            case (RequestTypeClassDeviceIn, RequestGetDescriptor)
                when (value & 0xFF) == 0 && (value >> DescriptorTypeShift) == DescriptorTypeHub:
                _reply = HubDescriptor();
                break;
            case (RequestTypeStandardDeviceOut, RequestSetAddress) when value < 128 && index == 0 && length == 0: break;
            case (RequestTypeStandardDeviceOut, RequestSetConfiguration) when value <= 1 && index == 0 && length == 0: break;
            case (RequestTypeStandardDeviceIn, RequestGetConfiguration) when value == 0 && index == 0: _reply = [Configuration]; break;
            case (RequestTypeStandardDeviceIn, RequestGetStatus) when value == 0 && index == 0: _reply = [0, 0]; break;
            case (RequestTypeStandardInterfaceIn, RequestGetStatus) when value == 0 && index == 0: _reply = [0, 0]; break;
            case (RequestTypeStandardInterfaceIn, RequestGetInterface) when value == 0 && index == 0: _reply = [0]; break;
            case (RequestTypeStandardInterfaceOut, RequestSetInterface) when value == 0 && index == 0 && length == 0: break;
            case (RequestTypeStandardEndpointIn, RequestGetStatus) when value == 0 && index is 0 or InterruptInEndpointAddress:
                _reply = [(byte)(index == InterruptInEndpointAddress && _interruptHalted ? 1 : 0), 0];
                break;
            case (RequestTypeStandardEndpointOut, RequestClearFeature or RequestSetFeature)
                when value == 0 && index == InterruptInEndpointAddress && length == 0: break;
            case (RequestTypeClassDeviceIn, RequestGetStatus) when value == 0 && index == 0:
                _reply = [0, 0, 0, 0]; // wHubStatus/wHubChange: local power good, no over-current, never changing
                break;
            case (RequestTypeClassDeviceOut, RequestClearFeature)
                when index == 0 && length == 0 && value is HubFeatureLocalPowerChange or HubFeatureOverCurrentChange: break;
            case (RequestTypeClassOtherIn, RequestGetStatus) when value == 0 && port >= 0 && port < _ports.Length:
            {
                Port target = _ports[port];
                _reply = new byte[4];
                BinaryPrimitives.WriteUInt16LittleEndian(_reply, PortStatusWord(target));
                BinaryPrimitives.WriteUInt16LittleEndian(_reply.AsSpan(2), PortChangeWord(target));
                break;
            }
            case (RequestTypeClassOtherOut, RequestSetFeature)
                when length == 0 && port >= 0 && port < _ports.Length
                     && value is PortFeatureSuspend or PortFeatureReset or PortFeaturePower: break;
            case (RequestTypeClassOtherOut, RequestClearFeature)
                when length == 0 && port >= 0 && port < _ports.Length
                     && value is PortFeatureEnable or PortFeatureSuspend or PortFeaturePower
                        or PortFeatureChangeConnection or PortFeatureChangeEnable or PortFeatureChangeSuspend
                        or PortFeatureChangeOverCurrent or PortFeatureChangeReset: break;
            default: return false;
        }

        if (_reply.Length > length)
        {
            _reply = _reply[..length];
        }
        _controlStage = length == 0 ? ControlStage.StatusIn
            : (type & RequestDirectionDeviceToHostBit) != 0 ? ControlStage.DataIn
            : ControlStage.DataOut;
        return true;
    }

    private void CommitControl()
    {
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_setup.AsSpan(2));
        ushort index = BinaryPrimitives.ReadUInt16LittleEndian(_setup.AsSpan(4));
        int port = index - 1;
        switch ((_setup[0], _setup[1]))
        {
            case (RequestTypeStandardDeviceOut, RequestSetAddress): Address = (byte)value; break;
            case (RequestTypeStandardDeviceOut, RequestSetConfiguration):
                Configuration = (byte)value;
                _interruptHalted = false;
                _interruptToggle = false;
                _lastChangeBitmap = [];
                break;
            case (RequestTypeStandardEndpointOut, RequestClearFeature):
                _interruptHalted = false;
                _interruptToggle = false;
                _lastChangeBitmap = [];
                break;
            case (RequestTypeStandardEndpointOut, RequestSetFeature): _interruptHalted = true; break;
            case (RequestTypeClassOtherOut, RequestSetFeature) when port >= 0 && port < _ports.Length:
                CommitSetPortFeature(_ports[port], value);
                break;
            case (RequestTypeClassOtherOut, RequestClearFeature) when port >= 0 && port < _ports.Length:
                CommitClearPortFeature(_ports[port], value);
                break;
        }
        _controlStage = ControlStage.Idle;
    }

    private static void CommitSetPortFeature(Port target, ushort feature)
    {
        switch (feature)
        {
            case PortFeatureSuspend: target.Suspended = true; break;
            case PortFeatureReset: BeginPortReset(target); break;
            case PortFeaturePower: SetPortPower(target, true); break;
        }
    }

    private static void CommitClearPortFeature(Port target, ushort feature)
    {
        switch (feature)
        {
            case PortFeatureEnable: target.Enabled = false; break;
            case PortFeatureSuspend: target.Suspended = false; break;
            case PortFeaturePower: SetPortPower(target, false); break;
            case PortFeatureChangeConnection: target.ChangeConnection = false; break;
            case PortFeatureChangeEnable: target.ChangeEnable = false; break;
            case PortFeatureChangeSuspend: target.ChangeSuspend = false; break;
            case PortFeatureChangeOverCurrent: /* never raised; accepted for a driver that clears it anyway */ break;
            case PortFeatureChangeReset: target.ChangeReset = false; break;
        }
    }

    public byte[] Capture()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(_ports.Length);
        writer.Write(Address);
        writer.Write(Configuration);
        writer.Write(_interruptHalted);
        writer.Write(_interruptToggle);
        writer.Write((int)_controlStage);
        writer.Write(_controlOffset);
        writer.Write(_controlToggle);
        foreach (byte[] bytes in new[] { _lastChangeBitmap, _reply, _setup })
        {
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }
        foreach (Port port in _ports)
        {
            writer.Write(port.Powered);
            writer.Write(port.Enabled);
            writer.Write(port.Suspended);
            writer.Write(port.Resetting);
            writer.Write(port.ResetRemainingMicroseconds);
            writer.Write(port.ChangeConnection);
            writer.Write(port.ChangeEnable);
            writer.Write(port.ChangeSuspend);
            writer.Write(port.ChangeReset);
            writer.Write(port.Device?.GetType().FullName ?? "");
            byte[] state = port.Device?.Capture() ?? [];
            writer.Write(state.Length);
            writer.Write(state);
        }
        return stream.ToArray();
    }

    public void Restore(ReadOnlySpan<byte> data)
    {
        using var reader = new BinaryReader(new MemoryStream(data.ToArray()));
        if (reader.ReadInt32() != _ports.Length)
        {
            // Port count is fixed at construction (from vm.json or the
            // front end that built this hub), the same as UhciController's
            // own PortCount: a mismatch means the live tree and the
            // snapshot disagree about the hardware, not just its state.
            throw new InvalidDataException("USB hub snapshot has a different port count than this hub.");
        }

        Address = reader.ReadByte();
        Configuration = reader.ReadByte();
        _interruptHalted = reader.ReadBoolean();
        _interruptToggle = reader.ReadBoolean();
        _controlStage = (ControlStage)reader.ReadInt32();
        _controlOffset = reader.ReadInt32();
        _controlToggle = reader.ReadBoolean();

        byte[] ReadBlock()
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
            {
                throw new InvalidDataException("Invalid USB hub snapshot.");
            }
            return reader.ReadBytes(length);
        }

        _lastChangeBitmap = ReadBlock();
        _reply = ReadBlock();
        _setup = ReadBlock();

        foreach (Port port in _ports)
        {
            port.Powered = reader.ReadBoolean();
            port.Enabled = reader.ReadBoolean();
            port.Suspended = reader.ReadBoolean();
            port.Resetting = reader.ReadBoolean();
            port.ResetRemainingMicroseconds = reader.ReadDouble();
            port.ChangeConnection = reader.ReadBoolean();
            port.ChangeEnable = reader.ReadBoolean();
            port.ChangeSuspend = reader.ReadBoolean();
            port.ChangeReset = reader.ReadBoolean();
            if (reader.ReadString() != (port.Device?.GetType().FullName ?? ""))
            {
                throw new InvalidDataException("USB hub snapshot device topology differs from the machine.");
            }

            // ReadBlock() must run unconditionally: port.Device?.Restore(...)
            // would otherwise short-circuit the call -- including the
            // length-prefixed read that advances the stream -- whenever this
            // port is empty, leaving every byte after it misaligned.
            byte[] state = ReadBlock();
            port.Device?.Restore(state);
            if (!double.IsFinite(port.ResetRemainingMicroseconds))
            {
                throw new InvalidDataException("Invalid USB hub snapshot.");
            }
        }

        if (reader.BaseStream.Position != reader.BaseStream.Length)
        {
            throw new InvalidDataException("Invalid USB hub snapshot tail.");
        }
        if ((int)_controlStage is < 0 or > 4 || _controlOffset < 0)
        {
            throw new InvalidDataException("Invalid USB hub snapshot.");
        }
    }

    // --- Read-only introspection for tests and front ends, mirroring the
    // way UhciController exposes its own PORTSC bits through I/O reads
    // rather than requiring every caller to walk a real control transfer. ---

    public bool IsPortPowered(int port) => PortAt(port).Powered;

    public ushort PortStatus(int port) => PortStatusWord(PortAt(port));

    public ushort PortChange(int port) => PortChangeWord(PortAt(port));
}
