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
        _requestRegister |= (byte)(1 << line);
    }

    public void LowerIrq(int line)
    {
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
        byte pending = (byte)(_requestRegister & ~_maskRegister);
        if (pending == 0) return -1;

        for (int line = 0; line < 8; line++)
        {
            byte bit = (byte)(1 << line);
            if ((pending & bit) == 0) continue;

            // An interrupt of equal or lower priority than one already in
            // service has to wait. This is what stops a handler from being
            // re-entered by its own line before it has sent its EOI.
            if (_serviceRegister != 0 && (_serviceRegister & ~(bit - 1)) != 0)
                return -1;

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

        return -1;
    }

    public byte ReadByte(ushort port)
    {
        bool commandPort = (port & 1) == 0;
        if (commandPort)
            return _readIsr ? _serviceRegister : _requestRegister;
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
    public bool HasPendingInterrupt => (_requestRegister & ~_maskRegister) != 0;

    /// <summary>
    /// True while a hardware interrupt is being serviced and is still waiting
    /// for its EOI. This is how we tell a genuine IRQ apart from a software
    /// INT that happened to land on the same handler -- the latter must not
    /// send an EOI, or it would close an interrupt that was never open.
    /// </summary>
    public bool HasInterruptInService => _serviceRegister != 0;
}

/// <summary>
/// The pair of cascaded 8259s exactly as a PC wires them, with the routing
/// logic for all 16 IRQ lines.
/// </summary>
public sealed class InterruptController
{
    public Pic8259 Master { get; }
    public Pic8259 Slave { get; }

    public InterruptController()
    {
        Slave = new Pic8259(0xA0, isMaster: false);
        Master = new Pic8259(0x20, isMaster: true, Slave);
    }

    public void Register(IoBus io)
    {
        io.Register(0x20, 2, Master);
        io.Register(0xA0, 2, Slave);
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
    }

    public void LowerIrq(int irq)
    {
        if (irq < 8) Master.LowerIrq(irq);
        else Slave.LowerIrq(irq - 8);
    }

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
}
