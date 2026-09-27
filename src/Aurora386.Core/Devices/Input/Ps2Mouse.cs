namespace Aurora386.Core.Devices.Input;

/// <summary>
/// The mouse hanging off the 8042's auxiliary port.
///
/// The PS/2 mouse is not a separate controller: it shares the keyboard's
/// chip, its data port and its output register, and the only thing that tells
/// the two apart is bit 5 of the status register at 0x64 and the fact that
/// the mouse interrupts on IRQ 12 instead of IRQ 1. That sharing is the whole
/// difficulty of the device -- see <see cref="Keyboard8042"/>, which owns the
/// arbitration. This class is only the mouse's own end of the wire: it
/// answers the commands addressed to it and produces movement packets.
///
/// The packet is three bytes. The first carries the buttons and the sign bits
/// of the two movement counts, the second the horizontal count and the third
/// the vertical one. Movement is signed and relative, and the vertical axis
/// points <b>up</b>: the mouse reports a positive Y when pushed away from the
/// user, which is the opposite of every screen coordinate system, so the
/// packet inverts what the host reported.
///
/// The one thing the guest absolutely must not see is a packet split by
/// something else. The three bytes have no framing of their own -- there is
/// no start bit, no length, nothing that says "this is byte one" -- so a
/// driver that loses sync stays lost, reading the buttons out of the Y count
/// forever after. That is why a packet is queued whole and the controller
/// hands it over one byte at a time without letting anything in between.
/// </summary>
public sealed class Ps2Mouse
{
    private readonly MouseState _state;

    /// <summary>
    /// Whether the mouse reports movement at all. It powers on <b>disabled</b>
    /// and stays that way until the driver sends 0xF4: an enabled mouse
    /// streaming packets into a machine whose driver has not finished
    /// initialising would be read as replies to the commands the driver is
    /// still sending.
    /// </summary>
    public bool Reporting { get; private set; }

    /// <summary>
    /// Resolution and sample rate, kept because the driver reads them back
    /// with 0xE9 and compares them with what it set. They change nothing
    /// here: we have no analogue mouse whose counts they could scale.
    /// </summary>
    private byte _resolution = 0x02;
    private byte _sampleRate = 100;
    private bool _scaling;

    /// <summary>
    /// Set when the previous byte was a command that takes a parameter, so
    /// the next byte is data rather than a command of its own. Without this
    /// the parameter to "set sample rate" -- 200, say -- would be read back
    /// as a command, and 200 is not one, so the mouse would answer with an
    /// error in the middle of a perfectly valid exchange.
    /// </summary>
    private byte _pendingCommand;

    /// <summary>
    /// The magic knock that turns a plain mouse into a wheel mouse: sample
    /// rates 200, 100, 80 in a row. We do not implement the wheel, so this is
    /// only tracked far enough to keep answering correctly; the identify
    /// command keeps returning 0x00, which tells the driver plainly that
    /// there is no wheel and stops it looking for a fourth packet byte that
    /// would never come.
    /// </summary>
    public Ps2Mouse(MouseState state) => _state = state;

    private MouseButtons _lastReportedButtons;

    /// <summary>
    /// Bytes the mouse wants to send: command acknowledgements and movement
    /// packets, in the order they were produced. The controller drains this.
    /// </summary>
    private readonly Queue<byte> _output = new();

    /// <summary>How many bytes are waiting to go to the guest.</summary>
    public int PendingCount => _output.Count;

    public bool TryDequeue(out byte value) => _output.TryDequeue(out value);

    /// <summary>
    /// Restores the power-on state. Both the 0xFF command and the machine
    /// reset land here.
    /// </summary>
    public void Reset()
    {
        _output.Clear();
        Reporting = false;
        _lastReportedButtons = MouseButtons.None;
        _resolution = 0x02;
        _sampleRate = 100;
        _scaling = false;
        _pendingCommand = 0;
    }

    /// <summary>
    /// Builds a movement packet from whatever the mouse has accumulated, if
    /// it is reporting and has anything to say.
    ///
    /// Returns false when no packet was produced, so the caller does not
    /// raise an interrupt for nothing -- an IRQ 12 with no data behind it
    /// makes the driver read the port anyway, and it reads whatever stale
    /// byte was last there.
    /// </summary>
    public bool TryBuildPacket()
    {
        if (!Reporting)
        {
            return false;
        }

        // Never build a packet on top of one still going out. The driver is
        // partway through reading three bytes; slipping a fresh packet in
        // between them is precisely the loss of sync the protocol cannot
        // recover from.
        if (_output.Count > 0)
        {
            return false;
        }

        // In stream mode -- the mode every driver uses -- the mouse speaks
        // only when it has news: some movement, or a button that changed
        // since the last packet. A mouse that reported on every poll would
        // hold IRQ 12 busy for ever and drown the driver in empty packets.
        MouseButtons buttons = _state.Buttons;
        if (!_state.HasPs2Motion && buttons == _lastReportedButtons)
        {
            return false;
        }

        (int x, int y) = _state.TakePs2Motion();
        _lastReportedButtons = buttons;

        // The vertical axis is inverted here and nowhere else: the host
        // counts downwards, the mouse counts upwards.
        int packetY = -y;

        byte header = (byte)((byte)buttons & 0x07);
        header |= 0x08;                                   // bit 3 always reads as one
        if (x < 0)
        {
            header |= 0x10;                        // X sign
        }
        if (packetY < 0)
        {
            header |= 0x20;                  // Y sign
        }

        _output.Enqueue(header);
        _output.Enqueue((byte)x);
        _output.Enqueue((byte)packetY);
        return true;
    }

    /// <summary>
    /// Handles a byte the guest sent to the auxiliary port with the 8042's
    /// 0xD4 command.
    ///
    /// Every command is acknowledged with 0xFA. That is not a courtesy: the
    /// driver waits for the acknowledgement with a timeout and concludes
    /// there is no mouse fitted if it never comes, so silence is the one
    /// answer guaranteed to make the mouse disappear.
    /// </summary>
    public void Command(byte value)
    {
        if (_pendingCommand != 0)
        {
            byte command = _pendingCommand;
            _pendingCommand = 0;

            switch (command)
            {
                case 0xF3: _sampleRate = value; break;
                case 0xE8: _resolution = value; break;
            }

            _output.Enqueue(0xFA);
            return;
        }

        switch (value)
        {
            case 0xFF:
                // Reset. The reply is three bytes: the acknowledgement, then
                // the self-test result, then the device ID. A driver reads
                // all three, and one that gets fewer decides the mouse failed
                // its test.
                Reset();
                _output.Enqueue(0xFA);
                _output.Enqueue(0xAA);   // self-test passed
                _output.Enqueue(0x00);   // device ID: a plain two-button mouse
                break;

            case 0xF2:
                // Identify. 0x00 is a standard mouse; 0x03 would claim a
                // wheel and 0x04 five buttons, and either would have the
                // driver expecting a four-byte packet from a device that
                // sends three.
                _output.Enqueue(0xFA);
                _output.Enqueue(0x00);
                break;

            case 0xF4:
                Reporting = true;
                _output.Enqueue(0xFA);
                break;

            case 0xF5:
                // Disable reporting. The mouse still answers commands; it
                // simply stops volunteering packets.
                Reporting = false;
                _output.Enqueue(0xFA);
                break;

            case 0xF6:
                // Set defaults, which includes switching reporting off.
                Reset();
                _output.Enqueue(0xFA);
                break;

            case 0xF3:   // set sample rate
            case 0xE8:   // set resolution
                _pendingCommand = value;
                _output.Enqueue(0xFA);
                break;

            case 0xE6: _scaling = false; _output.Enqueue(0xFA); break;   // 1:1 scaling
            case 0xE7: _scaling = true; _output.Enqueue(0xFA); break;    // 2:1 scaling

            case 0xE9:
            {
                // Status request: three bytes describing how the mouse is
                // configured. Drivers use it to confirm that the settings
                // they wrote actually took, so the values have to be the ones
                // that were set rather than fixed constants.
                byte status = (byte)((byte)_state.Buttons & 0x07);
                if (Reporting)
                {
                    status |= 0x20;
                }
                if (_scaling)
                {
                    status |= 0x10;
                }

                _output.Enqueue(0xFA);
                _output.Enqueue(status);
                _output.Enqueue(_resolution);
                _output.Enqueue(_sampleRate);
                break;
            }

            case 0xEB:
                // Read data: one packet on demand, for drivers that poll
                // rather than take the interrupt.
                _output.Enqueue(0xFA);
                ForcePacket();
                break;

            default:
                // An unknown command is refused explicitly. Drivers know how
                // to handle a refusal; they do not know how to handle
                // silence, and will sit in a timeout loop instead.
                _output.Enqueue(0xFE);   // RESEND
                break;
        }
    }

    /// <summary>
    /// Produces a packet regardless of whether the mouse is reporting, which
    /// is what the 0xEB "read data" command asks for: a poll gets an answer
    /// even from a mouse that has been told not to volunteer them.
    /// </summary>
    private void ForcePacket()
    {
        (int x, int y) = _state.TakePs2Motion();
        int packetY = -y;

        byte header = (byte)((byte)_state.Buttons & 0x07);
        header |= 0x08;
        if (x < 0)
        {
            header |= 0x10;
        }
        if (packetY < 0)
        {
            header |= 0x20;
        }

        _output.Enqueue(header);
        _output.Enqueue((byte)x);
        _output.Enqueue((byte)packetY);
    }
}
