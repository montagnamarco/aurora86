using Aurora386.Core.IO;
using Aurora386.Core.Memory;

namespace Aurora386.Core.Devices;

/// <summary>
/// 8042 keyboard controller.
///
/// A PC keyboard does not send characters, it sends scancodes: one code when
/// a key goes down and the same code with bit 7 set when it comes back up.
/// Turning those into ASCII, honouring shift and caps lock, is the BIOS's job.
///
/// The controller also owns a pair of jobs that have nothing to do with
/// typing: it drives the A20 address line and it can pulse the CPU's reset
/// pin. Both are AT-era hacks bolted onto the only spare output pins on the
/// board, and operating systems of the day leaned on them heavily -- the
/// reset pulse was the standard way of getting back out of protected mode on
/// a 286, which had no other way down.
///
/// The single most important thing to get right in this device is that the
/// controller has exactly ONE output register. Command responses and
/// scancodes both have to squeeze through it, one byte at a time, and the
/// rules for who wins are what the buffer bookkeeping below is all about.
/// </summary>
public sealed class Keyboard8042 : IIoDevice
{
    private readonly InterruptController _interrupts;

    /// <summary>
    /// Bytes ready to be read from port 0x60: controller responses and
    /// scancodes that have already been let through. Real hardware has a
    /// single output register; the queue here just models a multi-byte reply
    /// being clocked out one byte per read.
    /// </summary>
    private readonly Queue<byte> _outputBuffer = new();

    /// <summary>
    /// Scancodes still sitting inside the keyboard itself. They stay here as
    /// long as bit 4 of the command byte keeps the keyboard disabled: system
    /// software routinely shuts the keyboard off while it interrogates the
    /// controller, precisely so that a keystroke cannot wedge itself between
    /// a command and its reply.
    /// </summary>
    private readonly Queue<byte> _scanCodes = new();

    /// <summary>
    /// True when the byte waiting in the output register is a command
    /// response rather than a keystroke. Since there is only one register,
    /// something has to give when a new byte arrives, and this flag decides
    /// what is expendable: a stale response may be dropped, a real keystroke
    /// may not -- the key was genuinely pressed and the software is entitled
    /// to see it.
    /// </summary>
    private bool _uscitaERisposta;

    private byte _commandByte = 0x45;   // keyboard interrupts enabled
    private byte _pendingCommand;
    private bool _expectingData;

    /// <summary>
    /// The bus whose A20 line this controller drives. The state deliberately
    /// does not live here: there is only one physical line, and port 0x92 can
    /// drive it too, so the bus is the single source of truth rather than
    /// either of the two devices wired to it.
    /// </summary>
    private readonly PhysicalMemoryBus _memory;

    public bool A20Enabled => _memory.A20Enabled;

    /// <summary>The controller's command byte, exposed for diagnostics.</summary>
    public byte CommandByte => _commandByte;

    /// <summary>Set when the controller has been asked to pulse CPU reset (Ctrl+Alt+Del).</summary>
    public bool ResetRequested { get; set; }

    public Keyboard8042(InterruptController interrupts, PhysicalMemoryBus memory)
    {
        _interrupts = interrupts;
        _memory = memory;
    }

    public void Register(IoBus io)
    {
        io.Register(0x60, this);
        io.Register(0x64, this);
    }

    /// <summary>
    /// Queues a scancode as though a key had been pressed or released, and
    /// raises IRQ 1 if the controller is currently allowed to interrupt.
    /// </summary>
    public void PushScanCode(byte scanCode)
    {
        if (_scanCodes.Count >= 16) return;       // buffer full, just like real hardware
        _scanCodes.Enqueue(scanCode);
        ConsegnaScanCode();
    }

    /// <summary>
    /// Moves the next scancode into the output register, if the keyboard is
    /// enabled and the register is not already holding something that must
    /// not be lost.
    /// </summary>
    private void ConsegnaScanCode()
    {
        bool disabilitata = (_commandByte & 0x10) != 0;
        if (disabilitata || _scanCodes.Count == 0) return;

        // A response nobody bothered to read must not dam up the keyboard.
        // There is a single output register, so the newer byte simply takes
        // the older one's place; waiting for the response to be collected
        // would turn a reply the software never wanted into a permanent plug
        // and the machine would look like it had stopped accepting input.
        if (_outputBuffer.Count > 0)
        {
            if (!_uscitaERisposta) return;   // a keystroke is already queued: wait our turn
            _outputBuffer.Clear();
        }

        _outputBuffer.Enqueue(_scanCodes.Dequeue());
        _uscitaERisposta = false;
        // Bit 0 of the command byte is the keyboard interrupt enable.
        if ((_commandByte & 0x01) != 0) _interrupts.RaiseIrq(1);
    }

    public byte ReadByte(ushort port)
    {
        if (port == 0x64)
        {
            // Status register. The 0x1C base sets the system flag (bit 2,
            // which POST leaves set), bit 3 which says the last byte written
            // went to the command port rather than the data port, and bit 4
            // which declares the keyboard is not inhibited -- the same
            // combination DOSBox-X reports. Bit 0, "output buffer full", is
            // added only when there really is a byte waiting.
            byte status = 0x1C;
            if (_outputBuffer.Count > 0) status |= 0x01;
            return status;
        }

        // Port 0x60: hand over the byte at the head of the output register.
        if (_outputBuffer.Count == 0) return 0;

        byte value = _outputBuffer.Dequeue();
        if (_outputBuffer.Count == 0) _interrupts.LowerIrq(1);
        // The register is free again, so the next queued key can move up.
        ConsegnaScanCode();
        return value;
    }

    public void WriteByte(ushort port, byte value)
    {
        if (port == 0x64)
        {
            HandleCommand(value);
            return;
        }

        // Port 0x60: either the data byte a previous command was waiting for,
        // or a command aimed at the keyboard itself.
        if (_expectingData)
        {
            _expectingData = false;
            switch (_pendingCommand)
            {
                case 0xF3:   // typematic rate/delay parameter
                case 0xF0:   // scancode set number
                    // The value has no effect for us, but it still has to be
                    // acknowledged: the software waits for an ACK after the
                    // parameter byte just as it did after the command byte.
                    EnqueueRisposta(0xFA);
                    SegnalaSeConsentito();
                    break;

                case 0x60: _commandByte = value; ConsegnaScanCode(); break;
                case 0xD1:
                    // Write output port: bit 1 is the A20 line, and driving
                    // bit 0 low pulses the CPU's reset pin.
                    _memory.A20Enabled = (value & 0x02) != 0;
                    if ((value & 0x01) == 0) ResetRequested = true;
                    break;
            }
            return;
        }

        // Commands addressed to the keyboard itself. Every one of them must
        // produce a reply: the software waits with a timeout, and silence is
        // read as "no keyboard fitted, or a broken one".
        switch (value)
        {
            case 0xFF:                        // reset
                EnqueueRisposta(0xFA);  // ACK
                EnqueueRisposta(0xAA);  // basic assurance test passed
                SegnalaSeConsentito();
                break;

            case 0xF2:
                // Identify: an ACK followed by the two ID bytes. 0xAB 0x41 is
                // an MF2 keyboard with translation done by the controller,
                // which is what essentially every PC of the era reports.
                EnqueueRisposta(0xFA);
                EnqueueRisposta(0xAB);
                EnqueueRisposta(0x41);
                SegnalaSeConsentito();
                break;

            case 0xF3:                        // set typematic rate/delay
                // The next byte is the parameter. It has to be swallowed as
                // data, otherwise it would be mistaken for a command and the
                // keyboard would appear to answer nonsense.
                _expectingData = true;
                _pendingCommand = 0xF3;
                EnqueueRisposta(0xFA);
                SegnalaSeConsentito();
                break;

            case 0xEE:                        // echo: the keyboard answers with itself
                EnqueueRisposta(0xEE);
                SegnalaSeConsentito();
                break;

            case 0xF0:                        // select scancode set
                _expectingData = true;
                _pendingCommand = 0xF0;
                EnqueueRisposta(0xFA);
                SegnalaSeConsentito();
                break;

            case 0xF4:                        // enable scanning
            case 0xF5:                        // disable scanning and restore defaults
            case 0xF6:                        // restore defaults
            case 0xED:                        // set the indicator LEDs
                EnqueueRisposta(0xFA);
                SegnalaSeConsentito();
                break;

            default:
                // An unknown command still gets an answer. On real hardware
                // the keyboard refuses explicitly with RESEND, and every
                // driver knows how to cope with that. Staying silent instead
                // leaves the software spinning on a timeout, which is far
                // worse than a clean rejection.
                EnqueueRisposta(0xFE);   // RESEND
                SegnalaSeConsentito();
                break;
        }
    }

    /// <summary>Places a controller response in the output register.</summary>
    private void EnqueueRisposta(byte value)
    {
        _outputBuffer.Enqueue(value);
        _uscitaERisposta = true;
    }

    /// <summary>
    /// Signals that something is waiting on port 0x60. Bit 0 of the command
    /// byte decides whether the controller is allowed to interrupt at all:
    /// with it clear the software polls the status register instead, and an
    /// uninvited IRQ 1 would make it run its keyboard handler for a key that
    /// was never pressed.
    /// </summary>
    private void SegnalaSeConsentito()
    {
        if ((_commandByte & 0x01) != 0) _interrupts.RaiseIrq(1);
    }

    private void HandleCommand(byte command)
    {
        // Arbitration for the single output register, which is the subtlest
        // part of this device.
        //
        // A command that produces a reply of its own displaces whatever
        // response is still sitting there unread: whoever writes to 0x64
        // without first draining 0x60 has given up on that byte. Without
        // this, a long reply nobody consumed -- the two identification bytes
        // are the classic case -- would sit in front of every subsequent
        // keystroke forever.
        //
        // Commands that produce no reply, such as enable and disable, leave
        // the register untouched. That is not an optimisation: software
        // issues exactly those commands *while* it is waiting for the answer
        // to an earlier command, and clearing the register there would throw
        // away the reply it is about to read.
        //
        // A keystroke already promoted into the register is never discarded
        // either way -- the key really was pressed and the software is owed
        // it. Getting either half of this wrong produces one of two very
        // confusing failures: phantom keys the user never typed, or real
        // keys stuck forever behind a stale command response.
        bool produceRisposta = command is 0x20 or 0xAA or 0xAB or 0xA9 or 0xA1 or 0xD0;
        if (_uscitaERisposta && produceRisposta)
        {
            _outputBuffer.Clear();
            _uscitaERisposta = false;
            _interrupts.LowerIrq(1);
        }

        switch (command)
        {
            case 0x20:   // read the command byte
                EnqueueRisposta(_commandByte);
                break;
            case 0x60:   // write the command byte: the data follows on 0x60
            case 0xD1:   // write the output port
                _pendingCommand = command;
                _expectingData = true;
                break;
            case 0xA7: _commandByte |= 0x20; break;    // disable the auxiliary (mouse) port
            case 0xA8: _commandByte &= 0xDF; break;    // enable the auxiliary (mouse) port
            case 0xAA:                                  // controller self-test
                EnqueueRisposta(0x55);            // passed
                break;
            case 0xAB:                                  // interface test
                // Zero means "no fault on the clock or data lines". Anything
                // that fails to answer here convinces the BIOS there is no
                // keyboard at all, and it stops reading the port entirely.
                EnqueueRisposta(0x00);
                break;
            case 0xA9:                                  // auxiliary port test
                EnqueueRisposta(0x00);
                break;
            case 0xA1:                                  // controller firmware version
                EnqueueRisposta(0x00);
                break;
            case 0xAE: _commandByte &= 0xEF; ConsegnaScanCode(); break;   // enable keyboard
            case 0xAD: _commandByte |= 0x10; break;    // disable keyboard
            case 0xD0:                                  // read the output port
                // Bit 0 is always set, meaning "reset is not being asserted";
                // bit 1 reports the current state of A20.
                EnqueueRisposta((byte)(0x01 | (A20Enabled ? 0x02 : 0x00)));
                break;
            case 0xFE:                                  // pulse the CPU reset line
                ResetRequested = true;
                break;
        }
    }
}
