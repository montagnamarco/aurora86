namespace Aurora386.Core.Devices.Usb;

/// <summary>
/// PORTSC (op+44h/48h) and CONFIGFLAG (op+40h): the two root ports (plan
/// decision 3, N_PORTS=2) and the routing handshake that hands each one to
/// this controller or to its companion UHCI. Register bit layout follows
/// EHCI 1.0 section 2.3.8 (CONFIGFLAG) and 2.3.9 (PORTSC).
/// </summary>
public sealed partial class EhciController
{
    public const int PortCount = 2;

    // --- PORTSC bits ---------------------------------------------------------------

    private const uint PortConnectStatus = 1u << 0;        // CCS, RO: derived from whether a device is attached
    private const uint PortConnectStatusChange = 1u << 1;  // CSC, RW1C
    private const uint PortEnabled = 1u << 2;               // PE: hardware sets it; software may only clear it by writing 0 -- writing 1 has no effect (EHCI 1.0 section 2.3.9)
    private const uint PortEnableChange = 1u << 3;          // PEC, RW1C
    private const uint PortOverCurrentActive = 1u << 4;     // OCA, RO: never asserted, no over-current condition is modelled
    private const uint PortOverCurrentChange = 1u << 5;     // OCC, RW1C
    private const uint PortForcePortResume = 1u << 6;       // FPR, RW
    private const uint PortSuspend = 1u << 7;                // RW
    private const uint PortReset = 1u << 8;                  // PR, RW
    private const int PortLineStatusShift = 10;
    private const uint PortLineStatusKState = 1u << PortLineStatusShift; // 01b: a low-speed device, released to the companion without ever being reset here
    private const uint PortPower = 1u << 12;                 // PP, RO hardwired 1: HCSPARAMS.PPC is 0, so every port is always powered
    private const uint PortOwnerBit = 1u << 13;              // PortOwner, RW while CONFIGFLAG is 1

    private struct PortState
    {
        public IUsbDevice? Device;
        public bool Enabled;           // PE
        public bool ConnectChange;     // CSC
        public bool EnableChange;      // PEC
        public bool OverCurrentChange; // OCC
        public bool ForceResume;       // FPR
        public bool Suspend;
        public bool Reset;             // PR
        public bool Owner;             // PortOwner, meaningful only while CONFIGFLAG is 1
    }

    private readonly PortState[] _ports = new PortState[PortCount];
    private bool _configFlag;

    /// <summary>Raised when CONFIGFLAG or a port's PortOwner bit changes which controller owns <paramref name="port"/>.</summary>
    public event Action<int>? PortRoutingChanged;

    /// <summary>
    /// A device is physically present at <paramref name="port"/>. The card
    /// that owns port routing (<c>UhciCard</c>) calls this only while the
    /// port is currently routed to EHCI; a device attached here is found by
    /// address exactly as <see cref="UhciController"/> finds its own.
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
        state.ConnectChange = true;
        if (wasEnabled)
        {
            state.EnableChange = true;
        }
        _status |= StatusPortChangeDetect;
        UpdateInterrupt();
    }

    /// <summary>True while CONFIGFLAG is 0 (every port starts owned by the companion) or the port's own PortOwner bit is set.</summary>
    public bool PortOwnedByCompanion(int port)
    {
        ValidatePort(port);
        return !_configFlag || _ports[port].Owner;
    }

    private static void ValidatePort(int port)
    {
        if ((uint)port >= PortCount)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
    }

    /// <summary>
    /// HCRESET's port half: every register bit returns to its power-on
    /// value, but the physical connection is untouched by an internal
    /// register reset, so a port that was already occupied reads clean --
    /// CCS still reflects the attached device, but CSC/PEC do not spuriously
    /// claim a change that never happened. CONFIGFLAG going back to 0 can
    /// still change who owns a port that CONFIGFLAG=1 had reclaimed for
    /// EHCI, so <see cref="PortRoutingChanged"/> fires for every port whose
    /// effective owner actually changes, exactly as <see cref="WriteConfigFlag"/>
    /// already does for an ordinary CONFIGFLAG write.
    /// </summary>
    private void ResetPorts()
    {
        Span<bool> ownedByCompanionBefore = stackalloc bool[PortCount];
        for (int i = 0; i < PortCount; i++)
        {
            ownedByCompanionBefore[i] = PortOwnedByCompanion(i);
        }

        _configFlag = false;
        for (int i = 0; i < PortCount; i++)
        {
            ref PortState state = ref _ports[i];
            state.Enabled = state.EnableChange = state.OverCurrentChange = false;
            state.ForceResume = state.Suspend = state.Reset = state.Owner = false;
            state.ConnectChange = false;
        }

        for (int i = 0; i < PortCount; i++)
        {
            if (PortOwnedByCompanion(i) != ownedByCompanionBefore[i])
            {
                PortRoutingChanged?.Invoke(i);
            }
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
        uint value = state.Device is not null ? PortConnectStatus : 0;
        value |= state.ConnectChange ? PortConnectStatusChange : 0;
        value |= state.Enabled ? PortEnabled : 0;
        value |= state.EnableChange ? PortEnableChange : 0;
        value |= state.OverCurrentChange ? PortOverCurrentChange : 0;
        value |= state.ForceResume ? PortForcePortResume : 0;
        value |= state.Suspend ? PortSuspend : 0;
        value |= state.Reset ? PortReset : 0;
        value |= state.Device is { LowSpeed: true } ? PortLineStatusKState : 0;
        value |= PortPower;
        value |= PortOwnedByCompanion(port) ? PortOwnerBit : 0;
        return value;
    }

    private void WritePortStatus(int port, uint value, uint writeMask)
    {
        ref PortState state = ref _ports[port];

        if ((writeMask & PortConnectStatusChange) != 0 && (value & PortConnectStatusChange) != 0)
        {
            state.ConnectChange = false;
        }
        if ((writeMask & PortEnableChange) != 0 && (value & PortEnableChange) != 0)
        {
            state.EnableChange = false;
        }
        if ((writeMask & PortOverCurrentChange) != 0 && (value & PortOverCurrentChange) != 0)
        {
            state.OverCurrentChange = false;
        }
        if ((writeMask & PortEnabled) != 0 && (value & PortEnabled) == 0)
        {
            // Software can only clear PE, by writing a 0; writing a 1 has no effect,
            // so a driver's ordinary read-modify-write of PORTSC (to touch FPR or
            // Suspend, say) never disables an already-enabled port by accident.
            state.Enabled = false;
        }
        if ((writeMask & PortForcePortResume) != 0)
        {
            state.ForceResume = (value & PortForcePortResume) != 0;
        }
        if ((writeMask & PortSuspend) != 0)
        {
            state.Suspend = (value & PortSuspend) != 0;
        }

        if ((writeMask & PortReset) != 0)
        {
            bool wasReset = state.Reset;
            state.Reset = (value & PortReset) != 0;
            if (state.Reset && !wasReset)
            {
                state.Device?.Reset();
                state.Enabled = false; // PE reads 0 for as long as reset is held
            }
            else if (!state.Reset && wasReset)
            {
                CompletePortReset(port);
            }
        }

        if ((writeMask & PortOwnerBit) != 0 && _configFlag)
        {
            bool ownedByCompanionBefore = PortOwnedByCompanion(port);
            state.Owner = (value & PortOwnerBit) != 0;
            if (PortOwnedByCompanion(port) != ownedByCompanionBefore)
            {
                PortRoutingChanged?.Invoke(port);
            }
        }
    }

    /// <summary>
    /// Software clearing PR after holding it: a device that answered the
    /// high-speed chirp during reset (<see cref="IUsbDevice.HighSpeedCapable"/>)
    /// stays enabled here and is told which speed it negotiated; any other
    /// device (or none) leaves PE clear, so the driver hands the port back
    /// to the companion by setting PortOwner (plan decision 3).
    /// </summary>
    private void CompletePortReset(int port)
    {
        ref PortState state = ref _ports[port];
        if (state.Device is { HighSpeedCapable: true } device)
        {
            state.Enabled = true;
            device.SelectSpeed(true);
        }
        else
        {
            state.Enabled = false;
        }
    }

    private uint ReadConfigFlag() => _configFlag ? 1u : 0u;

    /// <summary>
    /// Setting CF to 1 reclaims every port for EHCI (PortOwner forced to 0);
    /// clearing it hands every port back to the companion regardless of the
    /// stored PortOwner bit (<see cref="PortOwnedByCompanion"/>). Either
    /// transition fires <see cref="PortRoutingChanged"/> for each port whose
    /// effective owner actually changes.
    /// </summary>
    private void WriteConfigFlag(uint value, uint writeMask)
    {
        if ((writeMask & 1) == 0)
        {
            return;
        }

        bool newFlag = (value & 1) != 0;
        if (newFlag == _configFlag)
        {
            return;
        }

        Span<bool> ownedByCompanionBefore = stackalloc bool[PortCount];
        for (int i = 0; i < PortCount; i++)
        {
            ownedByCompanionBefore[i] = PortOwnedByCompanion(i);
        }

        _configFlag = newFlag;
        for (int i = 0; i < PortCount; i++)
        {
            _ports[i].Owner = false;
        }

        for (int i = 0; i < PortCount; i++)
        {
            if (PortOwnedByCompanion(i) != ownedByCompanionBefore[i])
            {
                PortRoutingChanged?.Invoke(i);
            }
        }
    }

    private void CapturePorts(BinaryWriter writer)
    {
        writer.Write(_configFlag);
        for (int i = 0; i < PortCount; i++)
        {
            PortState state = _ports[i];
            writer.Write(state.Enabled);
            writer.Write(state.ConnectChange);
            writer.Write(state.EnableChange);
            writer.Write(state.OverCurrentChange);
            writer.Write(state.ForceResume);
            writer.Write(state.Suspend);
            writer.Write(state.Reset);
            writer.Write(state.Owner);
            writer.Write(state.Device?.GetType().FullName ?? "");
            byte[] deviceState = state.Device?.Capture() ?? [];
            writer.Write(deviceState.Length);
            writer.Write(deviceState);
        }
    }

    private void RestorePorts(BinaryReader reader)
    {
        bool configFlag = reader.ReadBoolean();
        var restored = new PortState[PortCount];
        var deviceStates = new byte[PortCount][];
        for (int i = 0; i < PortCount; i++)
        {
            restored[i].Enabled = reader.ReadBoolean();
            restored[i].ConnectChange = reader.ReadBoolean();
            restored[i].EnableChange = reader.ReadBoolean();
            restored[i].OverCurrentChange = reader.ReadBoolean();
            restored[i].ForceResume = reader.ReadBoolean();
            restored[i].Suspend = reader.ReadBoolean();
            restored[i].Reset = reader.ReadBoolean();
            restored[i].Owner = reader.ReadBoolean();
            if (reader.ReadString() != (_ports[i].Device?.GetType().FullName ?? ""))
            {
                throw new InvalidDataException("EHCI snapshot device topology differs from the machine.");
            }
            int length = reader.ReadInt32();
            if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
            {
                throw new InvalidDataException("Invalid EHCI snapshot payload.");
            }
            deviceStates[i] = reader.ReadBytes(length);
        }

        _configFlag = configFlag;
        for (int i = 0; i < PortCount; i++)
        {
            _ports[i].Enabled = restored[i].Enabled;
            _ports[i].ConnectChange = restored[i].ConnectChange;
            _ports[i].EnableChange = restored[i].EnableChange;
            _ports[i].OverCurrentChange = restored[i].OverCurrentChange;
            _ports[i].ForceResume = restored[i].ForceResume;
            _ports[i].Suspend = restored[i].Suspend;
            _ports[i].Reset = restored[i].Reset;
            _ports[i].Owner = restored[i].Owner;
            _ports[i].Device?.Restore(deviceStates[i]);
        }
    }
}
