using System.Buffers.Binary;
using Aurora86.Core.Devices.Interrupts;
using Aurora86.Core.IO;

namespace Aurora86.Core.Devices.Audio;

/// <summary>
/// A UART-only MPU-401-compatible MIDI interface, as clone sound cards of
/// the early 1990s exposed it to DOS and Windows drivers. Software selects
/// UART mode through the command port, writes raw MIDI bytes through the
/// data port, and polls the active-low status bits before each write.
///
/// This deliberately models no intelligent sequencer, serial timing, MIDI
/// input, or host API. A replaceable <see cref="IMidiSink"/> receives the
/// accepted raw byte stream outside the guest register state.
/// </summary>
public sealed class Mpu401Uart : IIoDevice
{
    public const int SnapshotVersion = 1;
    public const int SnapshotLength = 14;

    private const ushort DefaultBasePort = 0x0330;
    private const int DefaultIrq = 9;
    private const int PortCount = 2;
    private const int DataPortOffset = 0;
    private const int CommandStatusPortOffset = 1;
    private const byte ResetCommand = 0xFF;
    private const byte EnterUartCommand = 0x3F;
    private const byte AcknowledgeReply = 0xFE;
    private const byte EmptyReadValue = 0xFF;
    private const byte StatusReceiveEmpty = 0x80;
    private const byte StatusTransmitFull = 0x40;
    private const int TransmitFifoCapacity = 8;
    private const byte SnapshotBasePortOffset = 0;
    private const byte SnapshotIrqOffset = 2;
    private const byte SnapshotUartModeOffset = 3;
    private const byte SnapshotReplyCountOffset = 4;
    private const byte SnapshotTransmitCountOffset = 5;
    private const byte SnapshotTransmitOffset = 6;
    private const int MaximumReplyCount = 2;
    private const int CascadeIrq = 2;
    private const byte UartModeEnabled = 1;
    private const byte OneReply = 1;

    private readonly InterruptController _interrupts;
    private readonly Queue<byte> _transmitFifo = new(TransmitFifoCapacity);
    private IMidiSink _sink;
    private bool _uartMode;
    private byte _replyCount;

    /// <summary>
    /// Creates a standalone UART clone at its conventional 330h/IRQ9
    /// resources, or at the exact resources supplied by a configured card.
    /// </summary>
    public Mpu401Uart(
        InterruptController interrupts,
        ushort basePort = DefaultBasePort,
        int irq = DefaultIrq,
        IMidiSink? sink = null)
    {
        ArgumentNullException.ThrowIfNull(interrupts);
        ValidateConfiguration(basePort, irq);

        _interrupts = interrupts;
        BasePort = basePort;
        Irq = irq;
        _sink = sink ?? NullMidiSink.Instance;
        Reset();
    }

    /// <summary>The first of the UART's data and command/status ports.</summary>
    public ushort BasePort { get; }

    /// <summary>The PIC line wired to the UART's acknowledgement replies.</summary>
    public int Irq { get; }

    /// <summary>The current raw-byte destination. The UART never owns its disposal.</summary>
    public IMidiSink Sink => _sink;

    /// <summary>Claims the two-port UART window after confirming it is unoccupied.</summary>
    public void Register(IoBus io)
    {
        ArgumentNullException.ThrowIfNull(io);

        if (!io.IsFree(BasePort, PortCount, this))
        {
            throw new InvalidOperationException("MPU-401 UART requires a free two-port I/O window.");
        }

        io.Register(BasePort, PortCount, this);
    }

    /// <summary>Reads an acknowledgement from the data port or the UART's active-low status.</summary>
    public byte ReadByte(ushort port)
    {
        if (port == BasePort + DataPortOffset)
        {
            return ReadReply();
        }

        if (port == BasePort + CommandStatusPortOffset)
        {
            return ReadStatus();
        }

        return EmptyReadValue;
    }

    /// <summary>Accepts raw UART data or one of the two declared command bytes.</summary>
    public void WriteByte(ushort port, byte value)
    {
        if (port == BasePort + DataPortOffset)
        {
            WriteData(value);
            return;
        }

        if (port == BasePort + CommandStatusPortOffset)
        {
            WriteCommand(value);
        }
    }

    /// <summary>
    /// Retries bytes the sink previously declined. It makes at most one pass
    /// across the physical eight-byte guest FIFO and never spins on a sink.
    /// </summary>
    public void DrainOutput()
    {
        int attempts = 0;
        while (_transmitFifo.Count > 0 && attempts < TransmitFifoCapacity)
        {
            byte value = _transmitFifo.Peek();
            if (!_sink.TryWriteByte(value))
            {
                return;
            }

            _transmitFifo.Dequeue();
            attempts++;
        }
    }

    /// <summary>
    /// Replaces the host byte stream without altering guest UART state. Both
    /// streams are reset so incomplete host-side MIDI messages cannot leak.
    /// </summary>
    public void SetSink(IMidiSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        if (ReferenceEquals(_sink, sink))
        {
            return;
        }

        _sink.Reset();
        sink.Reset();
        _sink = sink;
    }

    /// <summary>Puts the clone into its power-on, non-UART state without an unsolicited reply.</summary>
    public void Reset()
    {
        _sink.Reset();
        _uartMode = false;
        _replyCount = 0;
        _transmitFifo.Clear();
        PublishInterruptLine();
    }

    /// <summary>Captures the fixed UART state without attempting host output.</summary>
    public byte[] Capture()
    {
        var payload = new byte[SnapshotLength];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(SnapshotBasePortOffset), BasePort);
        payload[SnapshotIrqOffset] = checked((byte)Irq);
        payload[SnapshotUartModeOffset] = _uartMode ? UartModeEnabled : (byte)0;
        payload[SnapshotReplyCountOffset] = _replyCount;
        payload[SnapshotTransmitCountOffset] = checked((byte)_transmitFifo.Count);

        int index = SnapshotTransmitOffset;
        foreach (byte value in _transmitFifo)
        {
            payload[index] = value;
            index++;
        }

        return payload;
    }

    /// <summary>Checks a fixed MPU4 payload before a snapshot mutates any machine state.</summary>
    public static void ValidateSnapshot(byte[] payload, int version)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (version != SnapshotVersion)
        {
            throw new InvalidDataException(
                $"MPU-401 snapshot is version {version}; this build reads version {SnapshotVersion}.");
        }

        if (payload.Length != SnapshotLength)
        {
            throw new InvalidDataException($"MPU-401 snapshot must be exactly {SnapshotLength} bytes.");
        }

        ushort basePort = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(SnapshotBasePortOffset));
        int irq = payload[SnapshotIrqOffset];
        ValidateSnapshotConfiguration(basePort, irq);

        byte uartMode = payload[SnapshotUartModeOffset];
        if (uartMode > 1)
        {
            throw new InvalidDataException("MPU-401 snapshot has a noncanonical UART mode.");
        }

        byte replyCount = payload[SnapshotReplyCountOffset];
        if (replyCount > MaximumReplyCount)
        {
            throw new InvalidDataException("MPU-401 snapshot has too many acknowledgement replies.");
        }

        byte transmitCount = payload[SnapshotTransmitCountOffset];
        if (transmitCount > TransmitFifoCapacity)
        {
            throw new InvalidDataException("MPU-401 snapshot has too many queued MIDI bytes.");
        }

        if (uartMode == 0 && (transmitCount != 0 || replyCount > 1))
        {
            throw new InvalidDataException("A non-UART MPU-401 snapshot has impossible queued state.");
        }

        int unusedStart = SnapshotTransmitOffset + transmitCount;
        for (int index = unusedStart; index < SnapshotLength; index++)
        {
            if (payload[index] != 0)
            {
                throw new InvalidDataException("MPU-401 snapshot has nonzero unused FIFO storage.");
            }
        }
    }

    /// <summary>Checks that a valid payload belongs to this card's immutable port and IRQ.</summary>
    public void ValidateSnapshotForDevice(byte[] payload, int version)
    {
        ValidateSnapshot(payload, version);

        ushort basePort = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(SnapshotBasePortOffset));
        if (basePort != BasePort || payload[SnapshotIrqOffset] != Irq)
        {
            throw new InvalidDataException("MPU-401 snapshot resources do not match the attached card.");
        }
    }

    /// <summary>Restores validated guest state and derives the reply IRQ from its reply count.</summary>
    public void Restore(byte[] payload, int version)
    {
        ValidateSnapshotForDevice(payload, version);

        _sink.Reset();
        _uartMode = payload[SnapshotUartModeOffset] != 0;
        _replyCount = payload[SnapshotReplyCountOffset];
        _transmitFifo.Clear();

        int transmitCount = payload[SnapshotTransmitCountOffset];
        for (int index = 0; index < transmitCount; index++)
        {
            _transmitFifo.Enqueue(payload[SnapshotTransmitOffset + index]);
        }

        PublishInterruptLine();
    }

    /// <summary>Returns the pending reply and makes another reply visible as a new IRQ edge.</summary>
    private byte ReadReply()
    {
        if (_replyCount == 0)
        {
            return EmptyReadValue;
        }

        _replyCount--;
        _interrupts.LowerIrq(Irq);
        PublishInterruptLine();
        return AcknowledgeReply;
    }

    /// <summary>Reports active-low receive-ready and transmit-ready status bits without draining output.</summary>
    private byte ReadStatus()
    {
        byte status = 0;
        if (_replyCount == 0)
        {
            status |= StatusReceiveEmpty;
        }

        if (_transmitFifo.Count == TransmitFifoCapacity)
        {
            status |= StatusTransmitFull;
        }

        return status;
    }

    /// <summary>Queues a raw MIDI byte only after the guest has entered UART mode.</summary>
    private void WriteData(byte value)
    {
        if (!_uartMode)
        {
            return;
        }

        if (_transmitFifo.Count < TransmitFifoCapacity)
        {
            _transmitFifo.Enqueue(value);
        }

        DrainOutput();
    }

    /// <summary>Implements the clone profile's reset and UART-entry command replies.</summary>
    private void WriteCommand(byte value)
    {
        if (value == ResetCommand)
        {
            _sink.Reset();
            _uartMode = false;
            _replyCount = OneReply;
            _transmitFifo.Clear();
            _interrupts.LowerIrq(Irq);
            PublishInterruptLine();
            return;
        }

        if (value == EnterUartCommand && !_uartMode)
        {
            _uartMode = true;
            bool replyQueueWasEmpty = _replyCount == 0;
            _replyCount++;
            if (replyQueueWasEmpty)
            {
                PublishInterruptLine();
            }
        }
    }

    /// <summary>Derives the PIC line level from pending replies without manufacturing an IRQ edge.</summary>
    private void PublishInterruptLine()
    {
        if (_replyCount != 0)
        {
            _interrupts.RaiseIrq(Irq);
            return;
        }

        _interrupts.LowerIrq(Irq);
    }

    private static void ValidateConfiguration(ushort basePort, int irq)
    {
        if (basePort == 0 || basePort == ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(basePort), "MPU-401 requires a two-port I/O window.");
        }

        if (irq < 0 || irq > 15 || irq == CascadeIrq)
        {
            throw new ArgumentOutOfRangeException(nameof(irq), "MPU-401 requires a non-cascade IRQ from 0 through 15.");
        }
    }

    private static void ValidateSnapshotConfiguration(ushort basePort, int irq)
    {
        if (basePort == 0 || basePort == ushort.MaxValue)
        {
            throw new InvalidDataException("MPU-401 snapshot has an invalid I/O port.");
        }

        if (irq < 0 || irq > 15 || irq == CascadeIrq)
        {
            throw new InvalidDataException("MPU-401 snapshot has an invalid IRQ.");
        }
    }
}
