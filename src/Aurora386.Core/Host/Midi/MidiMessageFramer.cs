namespace Aurora386.Core.Host.Midi;

/// <summary>
/// Frames raw bytes from an MPU-401 UART into complete MIDI wire messages
/// for a host output sink. It preserves channel running status, permits
/// real-time bytes inside other messages, and bounds one buffered SysEx so a
/// malformed guest stream cannot grow host memory without limit.
/// Call it from one thread, without reentering from a sink callback. Sink
/// exceptions propagate; reset or discard the framer before accepting more bytes.
/// </summary>
public sealed class MidiMessageFramer
{
    public const int MaximumSysExLength = 65536;

    private const byte SystemExclusive = 0xF0;
    private const byte EndOfExclusive = 0xF7;
    private const byte SystemReset = 0xFF;
    private const byte FirstChannelStatus = 0x80;
    private const byte LastChannelStatus = 0xEF;
    private const byte ProgramChange = 0xC0;
    private const byte ChannelPressure = 0xD0;
    private const byte TimeCodeQuarterFrame = 0xF1;
    private const byte SongPositionPointer = 0xF2;
    private const byte SongSelect = 0xF3;
    private const byte TuneRequest = 0xF6;
    private const byte UndefinedSystemCommonFirst = 0xF4;
    private const byte UndefinedSystemCommonLast = 0xF5;
    private const byte FirstRealTime = 0xF8;
    private const byte UndefinedRealTimeFirst = 0xF9;
    private const byte UndefinedRealTimeLast = 0xFD;
    private const int OneDataByte = 1;
    private const int TwoDataBytes = 2;

    private readonly IMidiMessageSink _sink;
    private readonly byte[] _sysEx = new byte[MaximumSysExLength];
    private byte _runningStatus;
    private byte _partialStatus;
    private byte _firstDataByte;
    private int _partialDataCount;
    private int _sysExLength;
    private bool _discardingSysEx;

    /// <summary>Creates a framer that delivers each complete message to <paramref name="sink"/> immediately.</summary>
    public MidiMessageFramer(IMidiMessageSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
    }

    /// <summary>The number of overlong SysEx messages discarded during this framer's lifetime.</summary>
    public ulong DroppedSysExCount { get; private set; }

    /// <summary>Accepts one raw MIDI byte and synchronously emits any message it completes.</summary>
    public void WriteByte(byte value)
    {
        if (IsEmittedRealTime(value))
        {
            WriteShort(value);
            return;
        }

        if (IsIgnoredRealTime(value))
        {
            return;
        }

        if (value == SystemReset)
        {
            WriteShort(value);
            Reset();
            return;
        }

        if (_discardingSysEx)
        {
            HandleDiscardingSysEx(value);
            return;
        }

        if (_sysExLength != 0)
        {
            HandleActiveSysEx(value);
            return;
        }

        HandleOrdinaryByte(value);
    }

    /// <summary>Drops incomplete framing state without emitting a message or clearing the lifetime overflow count.</summary>
    public void Reset()
    {
        _runningStatus = 0;
        _partialStatus = 0;
        _firstDataByte = 0;
        _partialDataCount = 0;
        _sysExLength = 0;
        _discardingSysEx = false;
    }

    /// <summary>Handles a byte after an overlong SysEx until its terminating status arrives.</summary>
    private void HandleDiscardingSysEx(byte value)
    {
        if (value < FirstChannelStatus)
        {
            return;
        }

        _discardingSysEx = false;
        if (value == EndOfExclusive)
        {
            return;
        }

        HandleOrdinaryByte(value);
    }

    /// <summary>Appends data to a live SysEx or closes it before a new non-real-time status.</summary>
    private void HandleActiveSysEx(byte value)
    {
        if (value < FirstChannelStatus)
        {
            AppendSysExData(value);
            return;
        }

        if (value == EndOfExclusive)
        {
            CompleteSysEx();
            return;
        }

        CompleteSysEx();
        HandleOrdinaryByte(value);
    }

    /// <summary>Processes channel statuses, system-common statuses, and data outside SysEx.</summary>
    private void HandleOrdinaryByte(byte value)
    {
        if (value < FirstChannelStatus)
        {
            HandleDataByte(value);
            return;
        }

        if (value <= LastChannelStatus)
        {
            _runningStatus = value;
            StartPartialMessage(value);
            return;
        }

        switch (value)
        {
            case SystemExclusive:
                StartSysEx();
                return;

            case EndOfExclusive:
                Reset();
                return;

            case TimeCodeQuarterFrame:
                StartSystemMessage(value);
                return;

            case SongPositionPointer:
                StartSystemMessage(value);
                return;

            case SongSelect:
                StartSystemMessage(value);
                return;

            case TuneRequest:
                ResetShortState();
                WriteShort(value);
                return;

            case UndefinedSystemCommonFirst:
            case UndefinedSystemCommonLast:
                ResetShortState();
                return;
        }
    }

    /// <summary>Collects data for the current short message or begins one from running channel status.</summary>
    private void HandleDataByte(byte value)
    {
        if (_partialStatus == 0)
        {
            if (_runningStatus == 0)
            {
                return;
            }

            StartPartialMessage(_runningStatus);
        }

        if (_partialDataCount == 0)
        {
            _firstDataByte = value;
        }

        _partialDataCount++;
        int requiredDataBytes = RequiredDataBytes(_partialStatus);
        if (_partialDataCount != requiredDataBytes)
        {
            return;
        }

        byte status = _partialStatus;
        byte secondDataByte = requiredDataBytes == TwoDataBytes ? value : (byte)0;
        WriteShort(status, _firstDataByte, secondDataByte);
        _partialStatus = 0;
        _firstDataByte = 0;
        _partialDataCount = 0;
    }

    /// <summary>Starts a channel message while retaining its status for following running-status messages.</summary>
    private void StartPartialMessage(byte status)
    {
        _partialStatus = status;
        _firstDataByte = 0;
        _partialDataCount = 0;
    }

    /// <summary>Starts a system-common message, which deliberately cannot establish running status.</summary>
    private void StartSystemMessage(byte status)
    {
        _runningStatus = 0;
        StartPartialMessage(status);
    }

    /// <summary>Begins a bounded SysEx and discards any partial short message it supersedes.</summary>
    private void StartSysEx()
    {
        ResetShortState();
        _sysEx[0] = SystemExclusive;
        _sysExLength = 1;
    }

    /// <summary>Appends a payload byte while reserving the final slot for the required EOX byte.</summary>
    private void AppendSysExData(byte value)
    {
        if (_sysExLength >= MaximumSysExLength - 1)
        {
            _sysExLength = 0;
            _discardingSysEx = true;
            if (DroppedSysExCount != ulong.MaxValue)
            {
                DroppedSysExCount++;
            }

            return;
        }

        _sysEx[_sysExLength] = value;
        _sysExLength++;
    }

    /// <summary>Writes an explicit or synthetic EOX and borrows the completed message for one synchronous callback.</summary>
    private void CompleteSysEx()
    {
        _sysEx[_sysExLength] = EndOfExclusive;
        _sysExLength++;
        _sink.WriteSysEx(_sysEx.AsSpan(0, _sysExLength));
        _sysExLength = 0;
    }

    /// <summary>Forgets a partial short message and its channel running status.</summary>
    private void ResetShortState()
    {
        _runningStatus = 0;
        _partialStatus = 0;
        _firstDataByte = 0;
        _partialDataCount = 0;
    }

    /// <summary>Returns the MIDI wire data-byte count for a short-message status.</summary>
    private static int RequiredDataBytes(byte status)
    {
        byte family = (byte)(status & 0xF0);
        if (family is ProgramChange or ChannelPressure)
        {
            return OneDataByte;
        }

        if (status is TimeCodeQuarterFrame or SongSelect)
        {
            return OneDataByte;
        }

        return TwoDataBytes;
    }

    /// <summary>Returns whether this byte is an emitted MIDI real-time message.</summary>
    private static bool IsEmittedRealTime(byte value)
    {
        return value is FirstRealTime or 0xFA or 0xFB or 0xFC or 0xFE;
    }

    /// <summary>Returns whether this reserved MIDI real-time byte preserves all framing state.</summary>
    private static bool IsIgnoredRealTime(byte value)
    {
        return value is UndefinedRealTimeFirst or UndefinedRealTimeLast;
    }

    /// <summary>Packs a short message in the WinMM-compatible low three bytes.</summary>
    private void WriteShort(byte status, byte firstDataByte = 0, byte secondDataByte = 0)
    {
        uint message = status | ((uint)firstDataByte << 8) | ((uint)secondDataByte << 16);
        _sink.WriteShort(message);
    }
}
