namespace Aurora86.Core.Devices.Usb;

/// <summary>
/// PORTSC, one per root port (xHCI 1.2 section 5.4.8). Four ports, all USB
/// 2.0 speeds only in this stage (coordinator decision 4): SuperSpeed and
/// its own 30h protocol capability are a later milestone. Bit layout follows
/// the plan's own "Costanti" section, itself taken from QEMU's
/// <c>hw/usb/hcd-xhci.c</c> and Linux's <c>xhci.h</c>.
/// </summary>
public sealed partial class XhciController
{
    public const int PortCount = 4;

    /// <summary>Where the first port register set starts, relative to <see cref="OperationalBase"/> (plan's "PORTSC a 400h+10h*n").</summary>
    private const uint PortRegisterSetBase = 0x400;
    private const uint PortRegisterStride = 0x10;

    // --- PORTSC bits -----------------------------------------------------------------

    private const uint PortCurrentConnectStatus = 1u << 0; // CCS, RO: derived from whether a device is attached
    private const uint PortEnabled = 1u << 1; // PED: hardware sets it on a successful reset; software may only clear it by writing 1 (RW1CS)
    private const uint PortReset = 1u << 4; // PR, RW
    private const int PortLinkStateShift = 5;
    private const uint PortLinkStateMask = 0xFu << PortLinkStateShift; // bits 8:5
    private const uint PortPower = 1u << 9; // PP, RO hardwired 1: every port is always powered
    private const int PortSpeedShift = 10;
    private const uint PortSpeedMask = 0xFu << PortSpeedShift; // bits 13:10
    private const uint PortLinkStateWriteStrobe = 1u << 16; // LWS: gates a software PLS write
    private const uint PortConnectStatusChange = 1u << 17; // CSC, RW1C
    private const uint PortEnabledChange = 1u << 18; // PEC, RW1C
    private const uint PortWarmResetChange = 1u << 19; // WRC, RW1C: stored for completeness, never set (no warm reset is modelled)
    private const uint PortOverCurrentChange = 1u << 20; // OCC, RW1C: stored for completeness, never set (no over-current condition is modelled)
    private const uint PortResetChange = 1u << 21; // PRC, RW1C
    private const uint PortLinkStateChange = 1u << 22; // PLC, RW1C
    private const uint PortConfigErrorChange = 1u << 23; // CEC, RW1C: stored for completeness, never set (no configuration error is modelled)
    private const uint PortWarmPortReset = 1u << 31; // WPR, write-only trigger: discarded, no warm reset is modelled

    private const uint PortChangeMask = PortConnectStatusChange | PortEnabledChange | PortWarmResetChange
        | PortOverCurrentChange | PortResetChange | PortLinkStateChange | PortConfigErrorChange;

    // --- Port Link State values (bits 8:5) --------------------------------------------

    private const uint LinkStateU0 = 0;
    private const uint LinkStateU3 = 3;
    private const uint LinkStateRxDetect = 5;

    // --- Port Speed values (bits 13:10), plan's "Costanti": FS 1, LS 2, HS 3, SS 4 ------

    private const uint SpeedFullSpeed = 1;
    private const uint SpeedLowSpeed = 2;
    private const uint SpeedHighSpeed = 3;

    private struct PortState
    {
        public IUsbDevice? Device;
        public bool Enabled; // PED
        public bool Reset; // PR
        public uint LinkState; // PLS, bits 8:5
        public uint Speed; // bits 13:10
        public bool ConnectChange; // CSC
        public bool EnabledChange; // PEC
        public bool ResetChange; // PRC
        public bool LinkStateChange; // PLC
    }

    private readonly PortState[] _ports = new PortState[PortCount];

    /// <summary>
    /// A device is physically present at, or removed from, <paramref name="port"/>
    /// (0-based). CCS and CSC follow immediately (plan's "Costanti"); a
    /// port that was enabled is disabled and PEC set, exactly as a real root
    /// hub reports on an unplug (xHCI 1.2 section 4.19.4). USBSTS.PCD is set
    /// either way, and a Port Status Change Event is queued while running.
    /// </summary>
    public void Attach(int port, IUsbDevice? device)
    {
        ValidatePort(port);
        ref PortState state = ref _ports[port];
        if (ReferenceEquals(state.Device, device))
        {
            return;
        }

        bool wasEnabled = state.Enabled;
        state.Device = device;
        device?.Reset();
        state.Enabled = false;
        state.Reset = false;
        state.LinkState = device is null ? LinkStateRxDetect : state.LinkState;
        if (wasEnabled)
        {
            state.EnabledChange = true;
        }
        RaisePortChange(port, ref state.ConnectChange);
    }

    private static void ValidatePort(int port)
    {
        if ((uint)port >= PortCount)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
    }

    /// <summary>
    /// HCRST's port half: every register bit returns to its power-on value,
    /// but the physical connection is untouched by an internal register
    /// reset, exactly as <see cref="EhciController.ResetPorts"/> leaves CCS
    /// alone across a reset.
    /// </summary>
    private void ResetPorts()
    {
        for (int i = 0; i < PortCount; i++)
        {
            ref PortState state = ref _ports[i];
            state.Enabled = false;
            state.Reset = false;
            state.LinkState = state.Device is null ? LinkStateRxDetect : state.LinkState;
            state.Speed = 0;
            state.ConnectChange = false;
            state.EnabledChange = false;
            state.ResetChange = false;
            state.LinkStateChange = false;
        }
    }

    private void AdvancePorts(double microseconds)
    {
        for (int i = 0; i < PortCount; i++)
        {
            _ports[i].Device?.Advance(microseconds);
        }
    }

    private uint ReadPortStatus(int port)
    {
        PortState state = _ports[port];
        uint linkState = state.Device is null ? LinkStateRxDetect : state.LinkState;
        uint value = state.Device is not null ? PortCurrentConnectStatus : 0;
        value |= state.Enabled ? PortEnabled : 0;
        value |= state.Reset ? PortReset : 0;
        value |= linkState << PortLinkStateShift;
        value |= PortPower;
        value |= state.Speed << PortSpeedShift;
        value |= state.ConnectChange ? PortConnectStatusChange : 0;
        value |= state.EnabledChange ? PortEnabledChange : 0;
        value |= state.ResetChange ? PortResetChange : 0;
        value |= state.LinkStateChange ? PortLinkStateChange : 0;
        return value;
    }

    private void WritePortStatus(int port, uint value, uint writeMask)
    {
        ref PortState state = ref _ports[port];

        if ((writeMask & PortEnabled) != 0 && (value & PortEnabled) != 0)
        {
            // PED is RW1CS (xHCI 1.2 section 5.4.8): software clears it by writing a
            // 1, and can never set it by writing a 1 -- only a completed port reset does.
            state.Enabled = false;
        }

        // PR is RW1S (xHCI 1.2 section 5.4.8): software writes 1 to start a
        // port reset and the controller clears it itself when the reset is
        // done, setting PRC and PED. Drivers never write it back to 0 --
        // Linux's xhci_port_state_to_neutral leaves PR out and waits for it
        // to clear. An emulated reset takes no time, so it completes within
        // the write, as QEMU's xhci_port_reset does. Writing 0 does nothing.
        // Review of the xHCI package, finding B2.
        if ((writeMask & PortReset) != 0 && (value & PortReset) != 0)
        {
            state.Device?.Reset();
            state.Enabled = false;
            CompletePortReset(port);
        }

        if ((writeMask & PortLinkStateWriteStrobe) != 0 && (value & PortLinkStateWriteStrobe) != 0
            && (writeMask & PortLinkStateMask) != 0)
        {
            // LWS-gated: only U0 and U3 are accepted targets in this stage (no link
            // power management beyond that is modelled).
            uint requested = (value & PortLinkStateMask) >> PortLinkStateShift;
            if (requested == LinkStateU0 || requested == LinkStateU3)
            {
                bool changed = state.LinkState != requested;
                state.LinkState = requested;
                if (changed)
                {
                    RaisePortChange(port, ref state.LinkStateChange);
                }
            }
        }

        if ((writeMask & PortConnectStatusChange) != 0 && (value & PortConnectStatusChange) != 0)
        {
            state.ConnectChange = false;
        }
        if ((writeMask & PortEnabledChange) != 0 && (value & PortEnabledChange) != 0)
        {
            state.EnabledChange = false;
        }
        if ((writeMask & PortResetChange) != 0 && (value & PortResetChange) != 0)
        {
            state.ResetChange = false;
        }
        if ((writeMask & PortLinkStateChange) != 0 && (value & PortLinkStateChange) != 0)
        {
            state.LinkStateChange = false;
        }
        // WRC, OCC and CEC (write-one-to-clear) are accepted but never set, so writing
        // them back is a no-op; WPR is discarded, no warm reset is modelled.
    }

    /// <summary>
    /// The end of a port reset: PR reads 0 again. A device answers with its
    /// negotiated speed (plan's "Costanti": low speed -> 2, a high-speed
    /// capable device -> 3 after <see cref="IUsbDevice.SelectSpeed"/>,
    /// anything else -> full speed 1) and the port comes up enabled in U0;
    /// any other outcome (no device) leaves PED clear and the link state at
    /// RxDetect.
    /// </summary>
    private void CompletePortReset(int port)
    {
        ref PortState state = ref _ports[port];
        state.Reset = false;
        if (state.Device is { } device)
        {
            state.Enabled = true;
            if (device.LowSpeed)
            {
                state.Speed = SpeedLowSpeed;
            }
            else if (device.HighSpeedCapable)
            {
                device.SelectSpeed(true);
                state.Speed = SpeedHighSpeed;
            }
            else
            {
                state.Speed = SpeedFullSpeed;
            }
            state.LinkState = LinkStateU0;
        }
        RaisePortChange(port, ref state.ResetChange);
    }

    /// <summary>
    /// USBSTS.PCD is set on every port change (plan's "Costanti"); a Port
    /// Status Change Event is queued only for the 0-to-1 transition of the
    /// RW1C bit itself, and only while the controller is running (RS=1),
    /// exactly as real hardware never reports a port event while halted.
    /// </summary>
    private void RaisePortChange(int port, ref bool changeBit)
    {
        bool wasSet = changeBit;
        changeBit = true;
        _status |= StatusPortChangeDetect;
        if (!wasSet && (_command & CommandRunStop) != 0)
        {
            PostPortStatusChangeEvent(port);
        }
    }

    private void CapturePorts(BinaryWriter writer)
    {
        for (int i = 0; i < PortCount; i++)
        {
            PortState state = _ports[i];
            writer.Write(state.Enabled);
            writer.Write(state.Reset);
            writer.Write(state.LinkState);
            writer.Write(state.Speed);
            writer.Write(state.ConnectChange);
            writer.Write(state.EnabledChange);
            writer.Write(state.ResetChange);
            writer.Write(state.LinkStateChange);
            writer.Write(state.Device?.GetType().FullName ?? "");
            byte[] deviceState = state.Device?.Capture() ?? [];
            writer.Write(deviceState.Length);
            writer.Write(deviceState);
        }
    }

    private void RestorePorts(BinaryReader reader)
    {
        var restored = new PortState[PortCount];
        var deviceStates = new byte[PortCount][];
        for (int i = 0; i < PortCount; i++)
        {
            restored[i].Enabled = reader.ReadBoolean();
            restored[i].Reset = reader.ReadBoolean();
            restored[i].LinkState = reader.ReadUInt32();
            restored[i].Speed = reader.ReadUInt32();
            restored[i].ConnectChange = reader.ReadBoolean();
            restored[i].EnabledChange = reader.ReadBoolean();
            restored[i].ResetChange = reader.ReadBoolean();
            restored[i].LinkStateChange = reader.ReadBoolean();
            if (reader.ReadString() != (_ports[i].Device?.GetType().FullName ?? ""))
            {
                throw new InvalidDataException("xHCI snapshot device topology differs from the machine.");
            }
            int length = reader.ReadInt32();
            if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
            {
                throw new InvalidDataException("Invalid xHCI snapshot payload.");
            }
            deviceStates[i] = reader.ReadBytes(length);
        }

        for (int i = 0; i < PortCount; i++)
        {
            _ports[i].Enabled = restored[i].Enabled;
            _ports[i].Reset = restored[i].Reset;
            _ports[i].LinkState = restored[i].LinkState;
            _ports[i].Speed = restored[i].Speed;
            _ports[i].ConnectChange = restored[i].ConnectChange;
            _ports[i].EnabledChange = restored[i].EnabledChange;
            _ports[i].ResetChange = restored[i].ResetChange;
            _ports[i].LinkStateChange = restored[i].LinkStateChange;
            _ports[i].Device?.Restore(deviceStates[i]);
        }
    }
}
