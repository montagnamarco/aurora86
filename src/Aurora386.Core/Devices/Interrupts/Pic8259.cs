using Aurora386.Core.Devices.Apic;
using Aurora386.Core.IO;

namespace Aurora386.Core.Devices.Interrupts;

/// <summary>
/// 8259A programmable interrupt controller.
///
/// The PC carries two of them in cascade: the master serves IRQ 0-7 at ports
/// 0x20/0x21, the slave serves IRQ 8-15 at ports 0xA0/0xA1 and reports to the
/// master through IRQ 2. That cascade is why IRQ 2 and IRQ 9 are effectively
/// the same pin on a real PC -- the AT stole the XT's IRQ 2 line to hang the
/// second controller off, and rerouted anything that used to sit there to the
/// slave's IRQ 9.
///
/// The chip is programmed with a sequence of initialisation command words
/// (ICW1-ICW4) that the BIOS sends at boot. Once that sequence is finished,
/// writes to the odd port mean something completely different: they are the
/// interrupt mask, one bit per line.
/// </summary>
public sealed class Pic8259 : IIoDevice
{
    private readonly ushort _basePort;
    private readonly bool _isMaster;
    private readonly Pic8259? _slave;

    /// <summary>Interrupt request register: lines asking to be serviced.</summary>
    private byte _requestRegister;
    private byte _inputLevels;
    internal byte LevelMask { get; set; }
    private byte _externalLevels;

    internal void SetExternalLevels(byte levels)
    {
        _requestRegister |= (byte)(levels & ~_externalLevels & ~LevelMask);
        _externalLevels = levels;
    }

    private byte Requests
    {
        get
        {
            byte requests = (byte)(_requestRegister | ((_inputLevels | _externalLevels) & LevelMask));
            if (_isMaster && _slave is not null)
                requests = (byte)((requests & ~4) | (_slave.HasPendingInterrupt ? 4 : 0));
            return requests;
        }
    }

    private int PendingLine()
    {
        byte pending = (byte)(Requests & ~_maskRegister);
        for (int line = 0; line < 8; line++)
        {
            int bit = 1 << line;
            if ((pending & bit) == 0) continue;
            // Lower numbered inputs have higher priority, including the
            // request already in service at this same priority.
            return (_serviceRegister & ((bit << 1) - 1)) == 0 ? line : -1;
        }
        return -1;
    }

    /// <summary>In-service register: lines being handled, not yet closed by an EOI.</summary>
    private byte _serviceRegister;

    /// <summary>Interrupt mask: a set bit disables the corresponding IRQ line.</summary>
    private byte _maskRegister = 0xFF;

    /// <summary>Interrupt vector assigned to this controller's line 0; the rest follow it.</summary>
    private byte _vectorBase;

    // Progress through the ICW initialisation sequence.
    private int _initStep;
    private bool _expectIcw4;
    private bool _readIsr;      // even port reads the ISR instead of the IRR

    public Pic8259(ushort basePort, bool isMaster, Pic8259? slave = null)
    {
        _basePort = basePort;
        _isMaster = isMaster;
        _slave = slave;
    }

    /// <summary>Asserts a hardware interrupt request on this line.</summary>
    public void RaiseIrq(int line)
    {
        _inputLevels |= (byte)(1 << line);
        if ((LevelMask & (1 << line)) == 0) _requestRegister |= (byte)(1 << line);
    }

    public void LowerIrq(int line)
    {
        _inputLevels &= (byte)~(1 << line);
        _requestRegister &= (byte)~(1 << line);
    }

    /// <summary>
    /// Returns the vector of the interrupt to service, or -1 if there is
    /// nothing to do. Priority is fixed and ordered by line number: line 0
    /// beats line 1, and so on down to line 7. That is why the timer sits on
    /// IRQ 0 and the keyboard on IRQ 1 -- the timekeeping must never be
    /// starved by anything else.
    /// </summary>
    public int GetPendingVector()
    {
        int line = PendingLine();
        if (line < 0) return -1;
        byte bit = (byte)(1 << line);

        // The cascade: line 2 on the master is not a device at all, it is
        // the slave controller asking to be serviced. The vector comes
        // from the slave, but the master still marks line 2 in service so
        // that both controllers need an EOI when the handler finishes.
        if (_isMaster && line == 2 && _slave is not null)
        {
            int slaveVector = _slave.GetPendingVector();
            if (slaveVector < 0) return -1;
            _requestRegister &= (byte)~bit;
            _serviceRegister |= bit;
            return slaveVector;
        }

        _requestRegister &= (byte)~bit;
        _serviceRegister |= bit;
        return _vectorBase + line;
    }

    public byte ReadByte(ushort port)
    {
        bool commandPort = (port & 1) == 0;
        if (commandPort)
            return _readIsr ? _serviceRegister : Requests;
        return _maskRegister;
    }

    public void WriteByte(ushort port, byte value)
    {
        bool commandPort = (port & 1) == 0;

        if (commandPort)
        {
            if ((value & 0x10) != 0)
            {
                // ICW1: bit 4 set means "start the initialisation sequence",
                // whatever else the controller was doing.
                _expectIcw4 = (value & 0x01) != 0;
                _initStep = 1;
                _maskRegister = 0;
                _serviceRegister = 0;
                return;
            }

            if ((value & 0x08) != 0)
            {
                // OCW3: chooses which of the two internal registers the even
                // port will return on the next read.
                if ((value & 0x02) != 0) _readIsr = (value & 0x01) != 0;
                return;
            }

            // OCW2: end-of-interrupt and priority rotation commands.
            HandleEndOfInterrupt(value);
            return;
        }

        // Odd port: ICW2-ICW4 while initialisation is in progress, and the
        // interrupt mask from then on.
        switch (_initStep)
        {
            case 1:
                // ICW2: vector base. The low three bits are ignored because
                // the controller substitutes the line number there; the
                // effective vector is base + line.
                _vectorBase = (byte)(value & 0xF8);
                _initStep = 2;
                return;
            case 2:
                // ICW3: which line carries the cascade. Nothing to emulate,
                // since our wiring is fixed at IRQ 2.
                _initStep = _expectIcw4 ? 3 : 0;
                return;
            case 3:
                // ICW4: 8086 mode versus MCS-80/85 mode, among other options.
                // The PC always selects 8086 mode.
                _initStep = 0;
                return;
            default:
                _maskRegister = value;
                return;
        }
    }

    private void HandleEndOfInterrupt(byte command)
    {
        int mode = (command >> 5) & 0x07;

        switch (mode)
        {
            case 0b001:   // non-specific EOI: closes the highest-priority line in service
            {
                for (int line = 0; line < 8; line++)
                {
                    byte bit = (byte)(1 << line);
                    if ((_serviceRegister & bit) != 0)
                    {
                        _serviceRegister &= (byte)~bit;
                        return;
                    }
                }
                return;
            }
            case 0b011:   // specific EOI: closes the line named in the low three bits
            {
                _serviceRegister &= (byte)~(1 << (command & 7));
                return;
            }
        }
    }

    /// <summary>True when at least one unmasked interrupt is waiting to be serviced.</summary>
    public bool HasPendingInterrupt => PendingLine() >= 0;

    /// <summary>
    /// True while a hardware interrupt is being serviced and is still waiting
    /// for its EOI. This is how we tell a genuine IRQ apart from a software
    /// INT that happened to land on the same handler -- the latter must not
    /// send an EOI, or it would close an interrupt that was never open.
    /// </summary>
    public bool HasInterruptInService => _serviceRegister != 0;

    /// <summary>
    /// The three registers, for a debugger: what is requested, what is
    /// masked, and what is being serviced.
    ///
    /// A guest that has stopped responding is usually waiting for an
    /// interrupt that never arrives, and there are only three reasons: the
    /// line was never raised, it is masked, or an earlier one is still in
    /// service because its end-of-interrupt was lost. Being able to read
    /// these three bytes tells the three apart in one look.
    /// </summary>
    public (byte Request, byte Mask, byte InService) Registers
        => (Requests, _maskRegister, _serviceRegister);

    /// <summary>
    /// The whole of this controller's state, for a snapshot.
    ///
    /// A snapshot that leaves the interrupt controllers out restores a
    /// machine whose masks are all ones -- the state a PIC powers up in --
    /// and a guest waiting for its timer tick then waits forever. That is
    /// indistinguishable from a hang caused by anything else, so it is worth
    /// the eight bytes.
    /// </summary>
    public byte[] Capture() =>
    [
        _requestRegister, _serviceRegister, _maskRegister, _vectorBase,
        (byte)_initStep, (byte)(_expectIcw4 ? 1 : 0), (byte)(_readIsr ? 1 : 0), _inputLevels,
    ];

    /// <summary>Puts back what <see cref="Capture"/> took.</summary>
    public void Restore(ReadOnlySpan<byte> state)
    {
        if (state.Length < 8) return;

        _requestRegister = state[0];
        _serviceRegister = state[1];
        _maskRegister = state[2];
        _vectorBase = state[3];
        _initStep = state[4];
        _expectIcw4 = state[5] != 0;
        _readIsr = state[6] != 0;
        _inputLevels = state[7];
        _externalLevels = 0; // PCI owners restore their asserted sources separately.
    }
}

/// <summary>
/// The pair of cascaded 8259s exactly as a PC wires them, with the routing
/// logic for all 16 IRQ lines.
///
/// Implements <see cref="IInterruptSource"/> (work package P4.4) so that a
/// machine whose processor has an on-die local APIC can wrap this same
/// instance in <c>Devices.Apic.LocalApic</c> without changing anything
/// about how the many devices in this file's own namespace raise and lower
/// their IRQ lines: they still hold this concrete type and still call
/// <see cref="RaiseIrq"/>/<see cref="LowerIrq"/> directly, exactly as before.
/// </summary>
public sealed class InterruptController : IIoDevice, IInterruptSource
{
    public Pic8259 Master { get; }
    public Pic8259 Slave { get; }

    /// <summary>
    /// The i430TX/PIIX4 board's I/O APIC, wired in addition to this pair
    /// (work package P4.4): every ISA IRQ keeps driving its 8259 input
    /// exactly as before, and now also pulses the matching INTIN pin (<see
    /// cref="RaiseIrq"/>), so whichever one the local APIC is currently
    /// listening to (see <see cref="Devices.Apic.LocalApic.ImcrApicMode"/>)
    /// sees the interrupt. PIRQ levels are driven directly by <see
    /// cref="Devices.Pci.PciInterruptRouter"/> through <see
    /// cref="SetPirqLevels"/>, independent of this pair's own 60h-63h
    /// steering (coordinator ruling, item 4): the PIIX wires the four PIRQ
    /// pins straight to the I/O APIC regardless of which legacy IRQ each is
    /// currently routed to. Null on an i430FX board, where nothing here
    /// changes.
    /// </summary>
    private IoApic? _ioApic;

    public InterruptController()
    {
        Slave = new Pic8259(0xA0, isMaster: false);
        Master = new Pic8259(0x20, isMaster: true, Slave);
    }

    public void Register(IoBus io)
    {
        io.Register(0x20, 2, Master);
        io.Register(0xA0, 2, Slave);
        io.Register(0x4D0, 2, this);
    }

    /// <summary>Wires the i430TX board's I/O APIC alongside this pair; see <see cref="_ioApic"/>.</summary>
    public void AttachIoApic(IoApic ioApic) => _ioApic = ioApic;

    // PIIX ELCR: IRQ0/1/2/8/13 remain edge triggered.
    public byte ReadByte(ushort port) => port == 0x4D0 ? Master.LevelMask : Slave.LevelMask;
    public void WriteByte(ushort port, byte value)
    {
        if (port == 0x4D0) Master.LevelMask = (byte)(value & 0xF8);
        else Slave.LevelMask = (byte)(value & 0xDE);
    }

    /// <summary>Combined PCI inputs to the legacy 8259 pair, kept separate from the ISA device pins.</summary>
    public void SetPciLevels(ushort levels)
    {
        Master.SetExternalLevels((byte)levels);
        Slave.SetExternalLevels((byte)(levels >> 8));
    }

    /// <summary>
    /// Drives INTIN16-19 directly, one bit per PIRQ link, independent of
    /// which legacy IRQ (if any) 60h-63h currently steers that link to
    /// (coordinator ruling, item 4): <see
    /// cref="Devices.Pci.PciInterruptRouter"/> computes this mask from its
    /// own per-link assertion set, the same one it folds into <see
    /// cref="SetPciLevels"/>'s aggregate, so a disabled or non-allowed
    /// routing still reaches the I/O APIC exactly as it does on real PIIX
    /// silicon.
    /// </summary>
    public void SetPirqLevels(byte levels)
    {
        if (_ioApic is null) return;
        for (int link = 0; link < 4; link++)
        {
            _ioApic.SetLevel(16 + link, (levels & (1 << link)) != 0);
        }
    }

    public void ResetTriggerModes()
    {
        Master.LevelMask = 0;
        Slave.LevelMask = 0;
    }

    /// <summary>
    /// Raises one of the PC's 16 IRQ lines. Lines 8-15 belong to the slave,
    /// which in turn has to signal the master on line 2 for the CPU to ever
    /// hear about it.
    /// </summary>
    public void RaiseIrq(int irq)
    {
        if (irq < 8) Master.RaiseIrq(irq);
        else
        {
            Slave.RaiseIrq(irq - 8);
            Master.RaiseIrq(2);
        }

        // Every call is a fresh edge on the matching INTIN pin (coordinator
        // ruling, item 2): the ISA sources this codebase has -- the PIT
        // above all -- call RaiseIrq repeatedly without ever lowering
        // anything, and the I/O APIC must see each tick, not just the first.
        _ioApic?.PulseLine(IsaPin(irq));
    }

    public void LowerIrq(int irq)
    {
        if (irq < 8) Master.LowerIrq(irq);
        else Slave.LowerIrq(irq - 8);
    }

    /// <summary>
    /// The I/O APIC pin an ISA IRQ line lands on. IRQ 0 is the one override
    /// in the PIIX3/4 wiring table (plans/specs/apic.md §2): the timer sits
    /// on INTIN2, not INTIN0, which the PIIX instead gives the 8259's own
    /// INTR output for the "virtual wire via the I/O APIC" case. Every other
    /// line's pin number matches its IRQ number exactly.
    /// </summary>
    private static int IsaPin(int irq) => irq == 0 ? 2 : irq;

    /// <summary>The vector to service, or -1 if no interrupt is ready.</summary>
    public int GetPendingVector() => Master.GetPendingVector();

    public bool HasPendingInterrupt => Master.HasPendingInterrupt;

    /// <summary>
    /// True while the guest is inside an interrupt handler that has not yet
    /// sent its end-of-interrupt. Anything that wants to redirect the guest
    /// into a routine of its own has to wait for this to clear: building a
    /// call on top of a handler's frame returns into the middle of it.
    /// </summary>
    public bool HasInterruptInService => Master.HasInterruptInService || Slave.HasInterruptInService;

    /// <summary>Both controllers' state, master first.</summary>
    public byte[] Capture()
    {
        byte[] state = new byte[18];
        Master.Capture().CopyTo(state, 0);
        Slave.Capture().CopyTo(state, 8);
        state[16] = Master.LevelMask;
        state[17] = Slave.LevelMask;
        return state;
    }

    /// <summary>Puts back what <see cref="Capture"/> took.</summary>
    public void Restore(ReadOnlySpan<byte> state)
    {
        if (state.Length < 16) return;
        Master.Restore(state[..8]);
        Slave.Restore(state[8..16]);
        Master.LevelMask = state.Length >= 18 ? (byte)(state[16] & 0xF8) : (byte)0;
        Slave.LevelMask = state.Length >= 18 ? (byte)(state[17] & 0xDE) : (byte)0;
    }
}
