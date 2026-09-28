using System.Buffers.Binary;

using Aurora386.Core.IO;

using Aurora386.Core.Devices.Interrupts;
namespace Aurora386.Core.Devices.Input;

/// <summary>
/// A Microsoft serial mouse on COM1, together with just enough of the 8250
/// UART behind it for a driver to find and read it.
///
/// Before the PS/2 port became standard a mouse was simply a serial device
/// that talked and never listened, at 1200 baud, seven data bits, no parity,
/// one stop bit. Microsoft's protocol packs a report into three bytes:
///
///   byte 0:  1 1 LB RB Y7 Y6 X7 X6
///   byte 1:  1 0 X5 X4 X3 X2 X1 X0
///   byte 2:  1 0 Y5 Y4 Y3 Y2 Y1 Y0
///
/// Bit 6 of the first byte is the only framing there is: it is set on the
/// first byte of a packet and clear on the other two. That single bit is what
/// lets a driver that tunes in mid-packet resynchronise, and it is why the
/// two movement counts are split across bytes so awkwardly -- seven bits is
/// all a byte has left once the framing bits are spent.
///
/// The other half of the protocol is how the mouse announces itself. The
/// driver drops DTR and RTS, which cuts the mouse's power, then raises them
/// again; the mouse boots and sends a single letter 'M' saying what it is.
/// A driver that does not see that 'M' concludes the port is empty, so the
/// identification is not a nicety -- it is the whole of mouse detection on a
/// serial port.
/// </summary>
public sealed class SerialMouse : IIoDevice
{
    /// <summary>COM1. The BIOS publishes this address in the BIOS data area.</summary>
    public const ushort BasePort = 0x3F8;

    /// <summary>COM1 interrupts on IRQ 4, COM2 on IRQ 3.</summary>
    private const int Irq = 4;

    /// <summary>
    /// Version one stores one format byte, six UART bytes, two mouse-button
    /// bytes, two Int32 serial deltas, and the Int32 receive-queue count.
    /// </summary>
    private const int SnapshotHeaderLength = 21;
    private const byte SnapshotVersion = 1;

    private readonly MouseState _state;
    private readonly InterruptController _interrupts;

    /// <summary>
    /// Bytes the mouse has sent and the guest has not yet read. A real UART
    /// holds one byte at a time (sixteen with a FIFO); the queue models the
    /// mouse clocking out a packet byte by byte.
    /// </summary>
    private readonly Queue<byte> _receiveQueue = new();

    // UART registers that the guest can read back. Only the ones a mouse
    // driver actually touches are modelled.
    private byte _interruptEnable;
    private byte _lineControl = 0x03;      // 8N1 at power-on
    private byte _modemControl;
    private byte _divisorLow = 0x60, _divisorHigh;   // 1200 baud
    private byte _scratch;

    /// <summary>
    /// True while the guest has the divisor latch selected, which remaps the
    /// first two registers onto the baud rate divisor. Software sets this
    /// bit, writes the divisor, and clears it again; a device that forgets
    /// the bit hands back a data byte where the divisor was expected and the
    /// driver decides the port is broken.
    /// </summary>
    private bool DivisorLatch => (_lineControl & 0x80) != 0;

    public SerialMouse(MouseState state, InterruptController interrupts)
    {
        _state = state;
        _interrupts = interrupts;
    }

    public void Register(IoBus io) => io.Register(BasePort, 8, this);

    /// <summary>
    /// True once the driver has powered the mouse up by raising DTR and RTS.
    /// Until then the mouse is electrically dead and sends nothing at all.
    /// </summary>
    public bool PoweredOn { get; private set; }

    /// <summary>
    /// Captures UART registers, received data, and the serial input that has
    /// not yet reached a packet. Enumerating the queue copies it without
    /// consuming the next byte visible to the guest.
    /// </summary>
    public byte[] Capture()
    {
        (MouseButtons buttons, int deltaX, int deltaY) = _state.CaptureSerialState();
        byte[] snapshot = new byte[SnapshotHeaderLength + _receiveQueue.Count];
        Span<byte> data = snapshot;

        data[0] = SnapshotVersion;
        data[1] = _interruptEnable;
        data[2] = _lineControl;
        data[3] = _modemControl;
        data[4] = _divisorLow;
        data[5] = _divisorHigh;
        data[6] = _scratch;
        data[7] = (byte)_reportedButtons;
        data[8] = (byte)buttons;
        BinaryPrimitives.WriteInt32LittleEndian(data[9..], deltaX);
        BinaryPrimitives.WriteInt32LittleEndian(data[13..], deltaY);
        BinaryPrimitives.WriteInt32LittleEndian(data[17..], _receiveQueue.Count);

        int queueOffset = SnapshotHeaderLength;
        foreach (byte value in _receiveQueue)
        {
            data[queueOffset] = value;
            queueOffset++;
        }

        return snapshot;
    }

    /// <summary>
    /// Restores a complete serial snapshot without raising or lowering IRQ4.
    /// The interrupt controller snapshot already records that line, and
    /// replaying it here would turn restoration into a new device event.
    /// </summary>
    public void Restore(ReadOnlySpan<byte> snapshot)
    {
        SerialSnapshot state = ReadSnapshot(snapshot);

        _interruptEnable = state.InterruptEnable;
        _lineControl = state.LineControl;
        _modemControl = state.ModemControl;
        _divisorLow = state.DivisorLow;
        _divisorHigh = state.DivisorHigh;
        _scratch = state.Scratch;
        _reportedButtons = state.ReportedButtons;
        PoweredOn = (state.ModemControl & 0x03) == 0x03;

        _receiveQueue.Clear();
        foreach (byte value in state.ReceiveQueue)
        {
            _receiveQueue.Enqueue(value);
        }

        _state.RestoreSerialState(state.Buttons, state.DeltaX, state.DeltaY);
    }

    /// <summary>
    /// Verifies an optional snapshot payload before the containing machine
    /// has been restored. Parsing first ensures malformed files cannot leave
    /// the target UART half changed.
    /// </summary>
    internal static void ValidateSnapshot(ReadOnlySpan<byte> snapshot)
    {
        _ = ReadSnapshot(snapshot);
    }

    /// <summary>
    /// Builds a report if the mouse is powered and has something to report.
    /// The machine calls this as it advances the devices.
    /// </summary>
    public void Poll()
    {
        if (!PoweredOn)
        {
            return;
        }

        // Never interleave: the framing bit only tells the driver where a
        // packet starts, so a packet begun must be finished before another
        // is queued behind it.
        if (_receiveQueue.Count > 0) { SignalIfEnabled(); return; }

        if (!_state.HasSerialMotion && !ButtonsChanged())
        {
            return;
        }

        SendReport();
    }

    /// <summary>
    /// The buttons as of the last report. A click with the mouse held still
    /// produces no movement at all, so without remembering this there would
    /// be nothing to trigger a report and the button press would never be
    /// sent.
    /// </summary>
    private MouseButtons _reportedButtons;

    private bool ButtonsChanged() => _reportedButtons != _state.Buttons;

    private void SendReport()
    {
        (int x, int y) = _state.TakeSerialMotion();
        MouseButtons buttons = _state.Buttons;
        _reportedButtons = buttons;

        // Y is positive downwards here, unlike the PS/2 packet: the Microsoft
        // protocol agrees with the screen rather than with the desk.
        byte dx = (byte)x;
        byte dy = (byte)y;

        byte first = 0x40;                                    // the framing bit
        if ((buttons & MouseButtons.Left) != 0)
        {
            first |= 0x20;
        }
        if ((buttons & MouseButtons.Right) != 0)
        {
            first |= 0x10;
        }
        first |= (byte)((dy >> 4) & 0x0C);                    // Y bits 7 and 6
        first |= (byte)((dx >> 6) & 0x03);                    // X bits 7 and 6

        _receiveQueue.Enqueue(first);
        _receiveQueue.Enqueue((byte)(dx & 0x3F));
        _receiveQueue.Enqueue((byte)(dy & 0x3F));

        SignalIfEnabled();
    }

    public byte ReadByte(ushort port)
    {
        switch (port - BasePort)
        {
            case 0:
                if (DivisorLatch)
                {
                    return _divisorLow;
                }

                // Receive buffer. Reading it consumes the byte and, once the
                // queue empties, releases the interrupt: the UART asserts its
                // line for as long as data is waiting, so a driver that reads
                // one byte of a three-byte packet must still find the line
                // asserted for the other two.
                if (!_receiveQueue.TryDequeue(out byte value))
                {
                    return 0;
                }
                if (_receiveQueue.Count == 0)
                {
                    _interrupts.LowerIrq(Irq);
                }
                return value;

            case 1:
                return DivisorLatch ? _divisorHigh : _interruptEnable;

            case 2:
                // Interrupt identification. Bit 0 clear means "an interrupt is
                // pending", which is backwards from every other status bit on
                // the chip and a classic source of confusion. 0x04 in the
                // upper bits identifies it as received data available.
                return _receiveQueue.Count > 0 ? (byte)0x04 : (byte)0x01;

            case 3: return _lineControl;
            case 4: return _modemControl;

            case 5:
            {
                // Line status. Bit 0 says a byte is waiting; bits 5 and 6 say
                // the transmitter is idle, which must always be true here --
                // a driver that writes to the port waits for them, and a
                // transmitter that never reports itself empty hangs it
                // forever.
                byte status = 0x60;
                if (_receiveQueue.Count > 0)
                {
                    status |= 0x01;
                }
                return status;
            }

            case 6:
                // Modem status: the handshake lines coming back. A mouse has
                // no modem, but the driver checks these, so the standard
                // trick applies -- loop the outputs straight back, which is
                // what a three-wire cable does anyway. CTS follows RTS, DSR
                // and DCD follow DTR.
                byte modem = 0;
                if ((_modemControl & 0x02) != 0)
                {
                    modem |= 0x10;   // CTS
                }
                if ((_modemControl & 0x01) != 0)
                {
                    modem |= 0xA0;   // DSR and DCD
                }
                return modem;

            default: return _scratch;
        }
    }

    public void WriteByte(ushort port, byte value)
    {
        switch (port - BasePort)
        {
            case 0:
                if (DivisorLatch)
                {
                    _divisorLow = value;
                }
                // Anything else written is transmitted, and a mouse is not
                // listening. Dropping it is correct: the byte goes down a
                // wire that ends at a device with no receiver.
                return;

            case 1:
                if (DivisorLatch)
                {
                    _divisorHigh = value;
                }
                else _interruptEnable = value;
                return;

            case 3: _lineControl = value; return;

            case 4:
            {
                bool wasPowered = PoweredOn;
                _modemControl = value;

                // DTR is bit 0 and RTS is bit 1. Both high means the mouse
                // has power.
                PoweredOn = (value & 0x03) == 0x03;

                if (PoweredOn && !wasPowered)
                {
                    AnnounceItself();
                }
                else if (!PoweredOn)
                {
                    // Power has been cut. Everything in flight is lost, which
                    // is the point of the driver doing this: it wants the
                    // mouse to start from a known state rather than midway
                    // through a packet.
                    _receiveQueue.Clear();
                    _interrupts.LowerIrq(Irq);
                }
                return;
            }

            case 7: _scratch = value; return;
        }
    }

    /// <summary>
    /// The identification a mouse sends when it is powered up: the single
    /// letter 'M'.
    ///
    /// This is the entirety of serial mouse detection. The driver toggles the
    /// handshake lines and waits; a port that answers with 'M' has a
    /// two-button Microsoft mouse on it, one that answers 'M' followed by '3'
    /// has a three-button Logitech, and one that stays silent has nothing.
    /// We claim the two-button mouse, because the three-button protocol sends
    /// the middle button in a fourth byte that a driver expecting three would
    /// mistake for the start of the next packet.
    /// </summary>
    private void AnnounceItself()
    {
        // A port with no mouse on it stays silent, and silence is exactly
        // what a driver needs to hear: Windows' MOUSE.DRV probes COM1 before
        // it tries the PS/2 port, and a port that answered 'M' with the mouse
        // plugged in elsewhere made it adopt a serial mouse that would never
        // move -- the pointer froze on the PS/2 setting for that reason.
        if (_state.ConnectedInterface != MouseInterface.Serial)
        {
            return;
        }

        _receiveQueue.Enqueue((byte)'M');
        SignalIfEnabled();
    }

    /// <summary>
    /// Raises IRQ 4 if the guest asked to be interrupted on received data.
    /// Bit 0 of the interrupt enable register is that request; drivers that
    /// poll the line status instead leave it clear, and an interrupt they did
    /// not ask for would run a handler they never installed.
    /// </summary>
    private void SignalIfEnabled()
    {
        if (_receiveQueue.Count > 0 && (_interruptEnable & 0x01) != 0)
            _interrupts.RaiseIrq(Irq);
    }

    /// <summary>
    /// Parses a self-contained serial snapshot before assigning any device
    /// field. A caller can therefore reject a malformed payload without
    /// changing UART registers, input state, or the interrupt controller.
    /// </summary>
    private static SerialSnapshot ReadSnapshot(ReadOnlySpan<byte> snapshot)
    {
        if (snapshot.Length < SnapshotHeaderLength)
        {
            throw new InvalidDataException("The serial mouse snapshot header is truncated.");
        }

        if (snapshot[0] != SnapshotVersion)
        {
            throw new InvalidDataException("The serial mouse snapshot version is unknown.");
        }

        int receiveCount = BinaryPrimitives.ReadInt32LittleEndian(snapshot[17..]);
        int remainingBytes = snapshot.Length - SnapshotHeaderLength;
        if (receiveCount < 0 || receiveCount != remainingBytes)
        {
            throw new InvalidDataException("The serial mouse snapshot receive queue is invalid.");
        }

        byte[] receiveQueue = snapshot[SnapshotHeaderLength..].ToArray();
        return new SerialSnapshot(
            snapshot[1],
            snapshot[2],
            snapshot[3],
            snapshot[4],
            snapshot[5],
            snapshot[6],
            (MouseButtons)snapshot[7],
            (MouseButtons)snapshot[8],
            BinaryPrimitives.ReadInt32LittleEndian(snapshot[9..]),
            BinaryPrimitives.ReadInt32LittleEndian(snapshot[13..]),
            receiveQueue);
    }

    /// <summary>Parsed UART, mouse input, and FIFO state from one v1 payload.</summary>
    private readonly record struct SerialSnapshot(
        byte InterruptEnable,
        byte LineControl,
        byte ModemControl,
        byte DivisorLow,
        byte DivisorHigh,
        byte Scratch,
        MouseButtons ReportedButtons,
        MouseButtons Buttons,
        int DeltaX,
        int DeltaY,
        byte[] ReceiveQueue);
}
