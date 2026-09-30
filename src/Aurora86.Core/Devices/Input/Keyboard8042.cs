using Aurora86.Core.IO;
using Aurora86.Core.Memory;

using Aurora86.Core.Devices.Interrupts;
namespace Aurora86.Core.Devices.Input;

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
    private readonly Queue<byte> _pendingScanCodes = new();

    /// <summary>
    /// The mouse on the auxiliary port, if one is fitted.
    ///
    /// It lives behind the same controller because that is how the hardware
    /// is built: one chip, one data port, one output register, and a single
    /// status bit to say which of the two devices the byte in that register
    /// came from. Everything awkward about PS/2 mice follows from that
    /// sharing.
    /// </summary>
    public Ps2Mouse? Mouse { get; private set; }

    /// <summary>
    /// True when the byte currently in the output register came from the
    /// mouse rather than the keyboard. The guest reads it as bit 5 of the
    /// status register, and it is the <b>only</b> thing distinguishing a
    /// movement packet from a scancode: a driver that finds this bit clear
    /// hands the byte to the keyboard handler, so getting it wrong turns
    /// mouse movement into a stream of phantom keystrokes.
    /// </summary>
    private bool _outputIsFromMouse;

    /// <summary>
    /// True when the byte currently in the output register is a command
    /// response rather than a keystroke. Since there is only one register,
    /// something has to give when a new byte arrives, and this flag decides
    /// what is expendable: a stale response may be dropped, a real keystroke
    /// may not -- the key was genuinely pressed and the software is entitled
    /// to see it.
    /// </summary>
    private bool _outputIsResponse;

    /// <summary>
    /// The byte the output register last held. Reading port 0x60 with nothing
    /// new waiting hands this back, because the register latches its contents
    /// rather than emptying.
    /// </summary>
    private byte _lastOutputByte;

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

    /// <summary>
    /// The controller's configuration, for a snapshot: the command byte and
    /// the A20 line.
    ///
    /// The queues are deliberately left out. They hold keystrokes in flight,
    /// and a snapshot taken mid-keystroke is better restored with an empty
    /// buffer than with half a scan code the guest will misread. The command
    /// byte is another matter: bit 0 is what enables the keyboard interrupt
    /// at all, and losing it restores a machine that never reports a key.
    /// </summary>
    public byte[] Capture() => [_commandByte, (byte)(_memory.A20Enabled ? 1 : 0)];

    /// <summary>Puts back what <see cref="Capture"/> took.</summary>
    public void Restore(ReadOnlySpan<byte> state)
    {
        if (state.Length < 2)
        {
            return;
        }
        _commandByte = state[0];
        _memory.A20Enabled = state[1] != 0;
    }

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
    /// Plugs a mouse into the auxiliary port and arranges for its packets to
    /// be delivered as they are produced.
    /// </summary>
    public void AttachMouse(Ps2Mouse mouse)
    {
        Mouse = mouse;
    }

    /// <summary>
    /// Offers the guest whatever the mouse has to say, building a movement
    /// packet first if one is due.
    ///
    /// The machine calls this as it advances the devices rather than the
    /// mouse pushing directly, because a packet must not be assembled in the
    /// middle of the guest reading the previous one, and the controller is
    /// the only thing that knows whether that is happening.
    /// </summary>
    public void PollMouse()
    {
        if (Mouse is null)
        {
            return;
        }

        if (Mouse.PendingCount == 0)
        {
            Mouse.TryBuildPacket();
        }
        DeliverMouseByte();
    }

    /// <summary>
    /// Moves one byte from the mouse into the output register and raises
    /// IRQ 12.
    ///
    /// Two gates stand in front of it. Bit 5 of the command byte disables the
    /// auxiliary port entirely -- system software sets it while it
    /// interrogates the controller, exactly as it does with the keyboard --
    /// and the output register has to be free, since there is only one and a
    /// byte already in it has a reader waiting for it.
    /// </summary>
    private void DeliverMouseByte()
    {
        if (Mouse is null)
        {
            return;
        }

        bool auxDisabled = (_commandByte & 0x20) != 0;
        if (auxDisabled)
        {
            return;
        }

        // Anything already in the register wins, whatever it is. A scancode
        // is owed to the software; a command response is being waited for;
        // and an earlier mouse byte is part of a packet that must not be
        // interleaved with anything.
        if (_outputBuffer.Count > 0)
        {
            return;
        }

        if (!Mouse.TryDequeue(out byte value))
        {
            return;
        }

        _outputBuffer.Enqueue(value);
        _outputIsResponse = false;
        _outputIsFromMouse = true;

        // Bit 1 of the command byte is the auxiliary interrupt enable, the
        // mouse's counterpart to bit 0. A driver that polls leaves it clear,
        // and an IRQ 12 it never asked for would send it into an interrupt
        // handler it has not installed.
        if ((_commandByte & 0x02) != 0)
        {
            _interrupts.RaiseIrq(12);
        }
    }

    /// <summary>
    /// Queues a scancode as though a key had been pressed or released, and
    /// raises IRQ 1 if the controller is currently allowed to interrupt.
    /// </summary>
    public Func<byte, bool>? InputInterceptor { get; set; }

    public void PushScanCode(byte scanCode)
    {
        if (InputInterceptor?.Invoke(scanCode) == true) { ClearHeldKeys(); return; }
        if (_pendingScanCodes.Count >= 16)
        {
            return;       // buffer full, just like real hardware
        }
        _pendingScanCodes.Enqueue(scanCode);
        DeliverScanCode();

        // A prefix is not a key. It announces the one that follows, and the
        // pair repeats together, so remember it and wait for its partner.
        //
        // This has to be tested on the raw byte, before bit 7 is stripped:
        // 0xE0 already has that bit set, so masking it first would turn it
        // into 0x60 and the prefix would be mistaken for an ordinary key --
        // one that is never released, since no break code for it ever comes.
        if (scanCode == 0xE0 || scanCode == 0xE1) { _repeatPrefix = scanCode; return; }

        // Track what is being held down, so that the key can start repeating
        // on its own. Bit 7 marks a break code: the release of whatever key
        // its low seven bits name.
        bool release = (scanCode & 0x80) != 0;
        byte code = (byte)(scanCode & 0x7F);

        if (release)
        {
            // Only the key that is actually repeating stops the repeat:
            // letting go of a modifier pressed afterwards must not cancel it.
            if (_repeatingCode == code)
            {
                _repeatingCode = 0;
            }
        }
        else if (!IsModifier(code))
        {
            // The newest key wins. Hold A, then press B without letting go of
            // A, and a real keyboard repeats B -- typists rely on this when
            // they roll from one key onto the next.
            _repeatingCode = code;
            _repeatingPrefix = _repeatPrefix;
            _repeatCountdownMs = RepeatDelayMs;
        }

        _repeatPrefix = 0;
    }

    /// <summary>
    /// Modifiers do not repeat: holding Shift must not flood the machine with
    /// shift presses. The keyboard knows which codes these are and simply
    /// leaves them out of the repeat logic.
    /// </summary>
    private static bool IsModifier(byte code) => code is
        0x2A or 0x36 or   // left and right shift
        0x1D or           // control (either, the right one behind an E0 prefix)
        0x38 or           // alt
        0x3A or           // caps lock
        0x45 or 0x46;     // num lock and scroll lock

    // --- typematic repeat -----------------------------------------------------
    //
    // Repeat belongs to the keyboard, not to the operating system: hold a key
    // and the keyboard itself keeps sending the make code, roughly half a
    // second later and then about ten times a second. DOS has no timer of its
    // own for this -- it simply reads whatever keystrokes arrive -- so a
    // keyboard that does not repeat leaves the guest unable to repeat a
    // character at all, however long the key is held.
    //
    // These are the power-on defaults of the AT keyboard. Software can change
    // them with command 0xF3; nothing we run does, so far.
    private const double RepeatDelayMs = 500;
    private const double RepeatIntervalMs = 100;

    private byte _repeatingCode;
    private byte _repeatingPrefix;
    private byte _repeatPrefix;
    private double _repeatCountdownMs;

    /// <summary>
    /// Advances the keyboard's own clock, re-sending the held key once the
    /// initial delay has passed and then at the repeat rate.
    /// </summary>
    public void Advance(double milliseconds)
    {
        if (_repeatingCode == 0)
        {
            return;
        }

        _repeatCountdownMs -= milliseconds;
        if (_repeatCountdownMs > 0)
        {
            return;
        }

        // Don't pile repeats onto a queue the guest has stopped draining: a
        // machine busy with something else should find one pending keystroke
        // when it looks again, not a hundred.
        if (_pendingScanCodes.Count == 0)
        {
            if (_repeatingPrefix != 0)
            {
                _pendingScanCodes.Enqueue(_repeatingPrefix);
            }
            _pendingScanCodes.Enqueue(_repeatingCode);
            DeliverScanCode();
        }

        _repeatCountdownMs = RepeatIntervalMs;
    }

    /// <summary>
    /// Forgets any held key. The frontend calls this when the window loses
    /// focus, where the release event for a held key never arrives.
    /// </summary>
    public void ClearHeldKeys() => _repeatingCode = 0;

    /// <summary>
    /// Moves the next scancode into the output register, if the keyboard is
    /// enabled and the register is not already holding something that must
    /// not be lost.
    /// </summary>
    private void DeliverScanCode()
    {
        bool disabled = (_commandByte & 0x10) != 0;
        if (disabled || _pendingScanCodes.Count == 0)
        {
            return;
        }

        // A response nobody bothered to read must not dam up the keyboard.
        // There is a single output register, so the newer byte simply takes
        // the older one's place; waiting for the response to be collected
        // would turn a reply the software never wanted into a permanent plug
        // and the machine would look like it had stopped accepting input.
        if (_outputBuffer.Count > 0)
        {
            if (!_outputIsResponse)
            {
                return;   // a keystroke is already queued: wait our turn
            }
            _outputBuffer.Clear();
        }

        _outputBuffer.Enqueue(_pendingScanCodes.Dequeue());
        _outputIsResponse = false;
        _outputIsFromMouse = false;
        // Bit 0 of the command byte is the keyboard interrupt enable.
        if ((_commandByte & 0x01) != 0)
        {
            _interrupts.RaiseIrq(1);
        }
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
            if (_outputBuffer.Count > 0)
            {
                status |= 0x01;
            }

            // Bit 5: the waiting byte came from the auxiliary port. On a real
            // AT this bit meant "transmit timeout"; the PS/2 redefined it,
            // and every mouse driver since has used it to decide whether the
            // byte it is about to read is a movement packet or a keystroke.
            if (_outputBuffer.Count > 0 && _outputIsFromMouse)
            {
                status |= 0x20;
            }

            return status;
        }

        // Port 0x60 is a latch, not a queue that empties into nothing.
        //
        // Reading it when no new byte has arrived hands back the last byte it
        // held: the register keeps its contents until something replaces
        // them. Returning zero instead would be reporting a keystroke that
        // never happened -- and software does read the port twice.
        //
        // QBasic is one such program. It reads the scancode, disables the
        // keyboard with command 0xAD so that nothing can arrive underneath
        // it, reads the port again to confirm what it saw, and re-enables
        // with 0xAE. Handing it a zero on that second read told it the key
        // had gone away, so it discarded the keystroke -- which is why no
        // key at all reached QBasic, not even Escape.
        if (_outputBuffer.Count == 0)
        {
            return _lastOutputByte;
        }

        byte value = _outputBuffer.Dequeue();
        _lastOutputByte = value;
        bool wasFromMouse = _outputIsFromMouse;

        if (_outputBuffer.Count == 0)
        {
            _interrupts.LowerIrq(wasFromMouse ? 12 : 1);
            _outputIsFromMouse = false;
        }

        // The register is free again, so the next queued byte can move up.
        //
        // The mouse goes first when it is partway through a packet. Its three
        // bytes carry no framing, so a scancode allowed in between them would
        // be counted as one of the packet's own and the driver would be
        // permanently out of step -- reading buttons where the Y count should
        // be, for the rest of the session.
        if (wasFromMouse && Mouse is { PendingCount: > 0 })
        {
            DeliverMouseByte();
        }
        else
        {
            DeliverScanCode();
            if (_outputBuffer.Count == 0)
            {
                DeliverMouseByte();
            }
        }

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
                    EnqueueResponse(0xFA);
                    SignalIfEnabled();
                    break;

                case 0x60:
                    _commandByte = value;
                    DeliverScanCode();
                    // Enabling either port with this write may release bytes
                    // that were held back while it was disabled. The mouse
                    // half matters in practice: a driver's usual opening move
                    // is to enable the auxiliary interrupt here, and anything
                    // the mouse queued beforehand would otherwise sit there
                    // unseen until the user happened to move it again.
                    if (_outputBuffer.Count == 0)
                    {
                        DeliverMouseByte();
                    }
                    break;

                case 0xD4:
                    // Write to the auxiliary port: the byte is a command for
                    // the mouse, not for the keyboard. Everything a driver
                    // says to the mouse arrives this way, which is why a
                    // controller that ignores 0xD4 makes the mouse look
                    // absent -- the enable command never reaches it and it
                    // never sends a packet.
                    if (Mouse is null)
                    {
                        // No mouse fitted. The controller answers for it with
                        // a resend, so the driver gets a definite answer
                        // rather than a timeout.
                        EnqueueResponse(0xFE);
                        SignalIfEnabled();
                        break;
                    }
                    Mouse.Command(value);
                    EnqueueMouseResponse();
                    break;
                case 0xD1:
                    // Write output port: bit 1 is the A20 line, and driving
                    // bit 0 low pulses the CPU's reset pin.
                    _memory.A20Enabled = (value & 0x02) != 0;
                    if ((value & 0x01) == 0)
                    {
                        ResetRequested = true;
                    }
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
                EnqueueResponse(0xFA);  // ACK
                EnqueueResponse(0xAA);  // basic assurance test passed
                SignalIfEnabled();
                break;

            case 0xF2:
                // Identify: an ACK followed by the two ID bytes. 0xAB 0x41 is
                // an MF2 keyboard with translation done by the controller,
                // which is what essentially every PC of the era reports.
                EnqueueResponse(0xFA);
                EnqueueResponse(0xAB);
                EnqueueResponse(0x41);
                SignalIfEnabled();
                break;

            case 0xF3:                        // set typematic rate/delay
                // The next byte is the parameter. It has to be swallowed as
                // data, otherwise it would be mistaken for a command and the
                // keyboard would appear to answer nonsense.
                _expectingData = true;
                _pendingCommand = 0xF3;
                EnqueueResponse(0xFA);
                SignalIfEnabled();
                break;

            case 0xEE:                        // echo: the keyboard answers with itself
                EnqueueResponse(0xEE);
                SignalIfEnabled();
                break;

            case 0xF0:                        // select scancode set
                _expectingData = true;
                _pendingCommand = 0xF0;
                EnqueueResponse(0xFA);
                SignalIfEnabled();
                break;

            case 0xF4:                        // enable scanning
            case 0xF5:                        // disable scanning and restore defaults
            case 0xF6:                        // restore defaults
            case 0xED:                        // set the indicator LEDs
                EnqueueResponse(0xFA);
                SignalIfEnabled();
                break;

            default:
                // An unknown command still gets an answer. On real hardware
                // the keyboard refuses explicitly with RESEND, and every
                // driver knows how to cope with that. Staying silent instead
                // leaves the software spinning on a timeout, which is far
                // worse than a clean rejection.
                EnqueueResponse(0xFE);   // RESEND
                SignalIfEnabled();
                break;
        }
    }

    /// <summary>Places a controller response in the output register.</summary>
    private void EnqueueResponse(byte value)
    {
        _outputBuffer.Enqueue(value);
        _outputIsResponse = true;
        _outputIsFromMouse = false;
    }

    /// <summary>
    /// Places a byte the <b>mouse</b> produced in the output register, in
    /// answer to a command the guest sent it.
    ///
    /// This is separate from <see cref="EnqueueResponse"/> for one reason
    /// that matters enormously: the acknowledgement to a mouse command comes
    /// back flagged as auxiliary data and raises IRQ 12, because the driver
    /// that sent the command is waiting on the mouse interrupt for its reply.
    /// Delivering it as a keyboard byte would send the acknowledgement to the
    /// keyboard handler, and the mouse driver would time out waiting for a
    /// reply that had already been eaten by something else.
    /// </summary>
    private void EnqueueMouseResponse()
    {
        if (_outputBuffer.Count > 0)
        {
            return;
        }
        DeliverMouseByte();
    }

    /// <summary>
    /// Signals that something is waiting on port 0x60. Bit 0 of the command
    /// byte decides whether the controller is allowed to interrupt at all:
    /// with it clear the software polls the status register instead, and an
    /// uninvited IRQ 1 would make it run its keyboard handler for a key that
    /// was never pressed.
    /// </summary>
    private void SignalIfEnabled()
    {
        if ((_commandByte & 0x01) != 0)
        {
            _interrupts.RaiseIrq(1);
        }
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
        bool producesResponse = command is 0x20 or 0xAA or 0xAB or 0xA9 or 0xA1 or 0xD0;
        if (_outputIsResponse && producesResponse)
        {
            _outputBuffer.Clear();
            _outputIsResponse = false;
            _interrupts.LowerIrq(1);
        }

        switch (command)
        {
            case 0x20:   // read the command byte
                EnqueueResponse(_commandByte);
                break;
            case 0x60:   // write the command byte: the data follows on 0x60
            case 0xD1:   // write the output port
            case 0xD4:   // send the following byte to the auxiliary port
                _pendingCommand = command;
                _expectingData = true;
                break;
            case 0xA7: _commandByte |= 0x20; break;    // disable the auxiliary (mouse) port
            case 0xA8:                                  // enable the auxiliary (mouse) port
                _commandByte &= 0xDF;
                // Anything the mouse queued while the port was shut can go
                // out now, the same way enabling the keyboard releases the
                // scancodes it was holding.
                if (_outputBuffer.Count == 0)
                {
                    DeliverMouseByte();
                }
                break;
            case 0xAA:                                  // controller self-test
                EnqueueResponse(0x55);            // passed
                break;
            case 0xAB:                                  // interface test
                // Zero means "no fault on the clock or data lines". Anything
                // that fails to answer here convinces the BIOS there is no
                // keyboard at all, and it stops reading the port entirely.
                EnqueueResponse(0x00);
                break;
            case 0xA9:
                // Auxiliary port test. Zero means the clock and data lines
                // are healthy; 0x02 means the clock line is stuck low, which
                // is how a controller with nothing plugged into that port
                // reports itself. Some BIOSes stop looking for a mouse
                // entirely on anything but zero, so this has to follow
                // whether a mouse is actually attached.
                EnqueueResponse(Mouse is null ? (byte)0x02 : (byte)0x00);
                break;
            case 0xA1:                                  // controller firmware version
                EnqueueResponse(0x00);
                break;
            case 0xAE: _commandByte &= 0xEF; DeliverScanCode(); break;   // enable keyboard
            case 0xAD: _commandByte |= 0x10; break;    // disable keyboard
            case 0xD0:                                  // read the output port
                // Bit 0 is always set, meaning "reset is not being asserted";
                // bit 1 reports the current state of A20.
                EnqueueResponse((byte)(0x01 | (A20Enabled ? 0x02 : 0x00)));
                break;
            case 0xFE:                                  // pulse the CPU reset line
                ResetRequested = true;
                break;
        }
    }
}
