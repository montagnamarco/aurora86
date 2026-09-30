using System.Buffers.Binary;

namespace Aurora86.Core.Devices.Audio;

/// <summary>
/// Saves the early-1990s YMF262 state between DOS FM driver port writes and
/// rendered samples. Callers stop synthesis and timer advancement during capture/restore.
/// </summary>
public sealed partial class Opl3
{
    private const uint SnapshotMagic = 0x314C504F; // "OPL1"
    private const ushort SnapshotVersion = 1;
    private const int SnapshotOperatorCount = OperatorCount * 2;
    private const int SnapshotVoiceCount = 18;
    private const int SnapshotOutputRateOffset = 10;
    private const int SnapshotRegistersOffset = 14;
    private const int SnapshotSelectorsOffset = 526;
    private const int SnapshotModeOffset = 528;
    private const int SnapshotTimerOffset = 529;
    private const int SnapshotTimerAccumulatorOffset = 533;
    private const int SnapshotOperatorsOffset = 549;
    // Ten parameter bytes, two lossless IEEE-754 doubles, and one envelope enum.
    private const int SnapshotOperatorSize = 27;
    private const int SnapshotVoicesOffset =
        SnapshotOperatorsOffset + SnapshotOperatorCount * SnapshotOperatorSize;
    // F-number WORD, block/key/feedback/connection/left/right bytes.
    private const int SnapshotVoiceSize = 8;
    private const int SnapshotSize = SnapshotVoicesOffset + SnapshotVoiceCount * SnapshotVoiceSize;

    /// <summary>Captures raw registers, cached synthesis parameters and timer fractions.</summary>
    public byte[] Capture()
    {
        byte[] result = new byte[SnapshotSize];
        Span<byte> data = result;
        BinaryPrimitives.WriteUInt32LittleEndian(data, SnapshotMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(data[4..], SnapshotVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(data[6..], SnapshotOperatorCount);
        BinaryPrimitives.WriteUInt16LittleEndian(data[8..], SnapshotVoiceCount);
        BinaryPrimitives.WriteInt32LittleEndian(data[SnapshotOutputRateOffset..], _outputRate);

        for (int bank = 0; bank < 2; bank++)
        {
            for (int register = 0; register < 256; register++)
            {
                data[SnapshotRegistersOffset + bank * 256 + register] = _registers[bank, register];
            }

            data[SnapshotSelectorsOffset + bank] = (byte)_selected[bank];
        }

        data[SnapshotModeOffset] = SnapshotBoolean(_opl3Mode);
        data[SnapshotTimerOffset] = _timer1;
        data[SnapshotTimerOffset + 1] = _timer2;
        data[SnapshotTimerOffset + 2] = _timerControl;
        data[SnapshotTimerOffset + 3] = _status;
        BinaryPrimitives.WriteDoubleLittleEndian(
            data[SnapshotTimerAccumulatorOffset..], _timer1Accumulator);
        BinaryPrimitives.WriteDoubleLittleEndian(
            data[(SnapshotTimerAccumulatorOffset + 8)..], _timer2Accumulator);

        for (int index = 0; index < _operators.Length; index++)
        {
            int recordOffset = SnapshotOperatorsOffset + index * SnapshotOperatorSize;
            Span<byte> record = data.Slice(recordOffset, SnapshotOperatorSize);
            WriteSnapshotOperator(record, _operators[index]);
        }

        for (int index = 0; index < _voices.Length; index++)
        {
            int recordOffset = SnapshotVoicesOffset + index * SnapshotVoiceSize;
            Span<byte> record = data.Slice(recordOffset, SnapshotVoiceSize);
            WriteSnapshotVoice(record, _voices[index]);
        }

        ValidateSnapshot(result);
        return result;
    }

    /// <summary>
    /// Validates exact layout and meaningful current-model ranges before mutation.
    /// </summary>
    public static void ValidateSnapshot(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length != SnapshotSize)
        {
            throw new InvalidDataException("OPL snapshot length is invalid.");
        }

        ReadOnlySpan<byte> snapshot = data;
        if (BinaryPrimitives.ReadUInt32LittleEndian(snapshot) != SnapshotMagic
            || BinaryPrimitives.ReadUInt16LittleEndian(snapshot[4..]) != SnapshotVersion
            || BinaryPrimitives.ReadUInt16LittleEndian(snapshot[6..]) != SnapshotOperatorCount
            || BinaryPrimitives.ReadUInt16LittleEndian(snapshot[8..]) != SnapshotVoiceCount)
        {
            throw new InvalidDataException("OPL snapshot header is invalid.");
        }

        if (BinaryPrimitives.ReadInt32LittleEndian(snapshot[SnapshotOutputRateOffset..]) <= 0)
        {
            throw new InvalidDataException("OPL snapshot output rate is invalid.");
        }

        ValidateSnapshotBoolean(snapshot[SnapshotModeOffset]);
        byte control = snapshot[SnapshotTimerOffset + 2];
        byte status = snapshot[SnapshotTimerOffset + 3];
        if ((control & 0x80) != 0
            || (status != 0 && status != 0xA0 && status != 0xC0 && status != 0xE0))
        {
            throw new InvalidDataException("OPL snapshot timer control or status is invalid.");
        }

        // A preload rewrite does not renormalize an existing fraction. Its bound
        // is the largest possible timer period, independent of the current preload.
        double timer1Accumulator = BinaryPrimitives.ReadDoubleLittleEndian(
            snapshot[SnapshotTimerAccumulatorOffset..]);
        double timer2Accumulator = BinaryPrimitives.ReadDoubleLittleEndian(
            snapshot[(SnapshotTimerAccumulatorOffset + 8)..]);
        ValidateSnapshotDouble(timer1Accumulator, 0, 256, false);
        ValidateSnapshotDouble(timer2Accumulator, 0, 256, false);

        for (int index = 0; index < SnapshotOperatorCount; index++)
        {
            int recordOffset = SnapshotOperatorsOffset + index * SnapshotOperatorSize;
            ReadOnlySpan<byte> op = snapshot.Slice(recordOffset, SnapshotOperatorSize);
            ValidateSnapshotByte(op[0], 15);
            ValidateSnapshotByte(op[1], 63);
            ValidateSnapshotByte(op[2], 3);

            for (int rate = 3; rate <= 6; rate++)
            {
                ValidateSnapshotByte(op[rate], 15);
            }

            ValidateSnapshotBoolean(op[7]);
            ValidateSnapshotBoolean(op[8]);
            ValidateSnapshotByte(op[9], 7);

            ValidateSnapshotDouble(BinaryPrimitives.ReadDoubleLittleEndian(op[10..]), 0, 1, false);
            ValidateSnapshotDouble(BinaryPrimitives.ReadDoubleLittleEndian(op[18..]), 0, 1, true);
            ValidateSnapshotByte(op[26], (byte)EnvelopePhase.Release);
        }

        for (int index = 0; index < SnapshotVoiceCount; index++)
        {
            int recordOffset = SnapshotVoicesOffset + index * SnapshotVoiceSize;
            ReadOnlySpan<byte> voice = snapshot.Slice(recordOffset, SnapshotVoiceSize);
            if (BinaryPrimitives.ReadUInt16LittleEndian(voice) > 1023)
            {
                throw new InvalidDataException("OPL snapshot F-number is invalid.");
            }

            ValidateSnapshotByte(voice[2], 7);
            ValidateSnapshotBoolean(voice[3]);
            ValidateSnapshotByte(voice[4], 7);
            ValidateSnapshotBoolean(voice[5]);
            ValidateSnapshotBoolean(voice[6]);
            ValidateSnapshotBoolean(voice[7]);
        }
    }

    /// <summary>Also requires the target sample rate to match for deterministic replay.</summary>
    public void ValidateSnapshotForRestore(byte[] data)
    {
        ValidateSnapshot(data);
        int outputRate = BinaryPrimitives.ReadInt32LittleEndian(
            data.AsSpan(SnapshotOutputRateOffset));
        if (outputRate != _outputRate)
        {
            throw new InvalidDataException("OPL snapshot output rate does not match the target.");
        }
    }

    /// <summary>
    /// Installs raw state without register writes, key edges, rendering or timers.
    /// </summary>
    public void Restore(byte[] data)
    {
        ValidateSnapshotForRestore(data);

        ReadOnlySpan<byte> snapshot = data;

        for (int bank = 0; bank < 2; bank++)
        {
            for (int register = 0; register < 256; register++)
            {
                int registerOffset = SnapshotRegistersOffset + bank * 256 + register;
                _registers[bank, register] = snapshot[registerOffset];
            }

            _selected[bank] = snapshot[SnapshotSelectorsOffset + bank];
        }

        _opl3Mode = snapshot[SnapshotModeOffset] != 0;
        _timer1 = snapshot[SnapshotTimerOffset];
        _timer2 = snapshot[SnapshotTimerOffset + 1];
        _timerControl = snapshot[SnapshotTimerOffset + 2];
        _status = snapshot[SnapshotTimerOffset + 3];
        _timer1Accumulator = BinaryPrimitives.ReadDoubleLittleEndian(
            snapshot[SnapshotTimerAccumulatorOffset..]);
        _timer2Accumulator = BinaryPrimitives.ReadDoubleLittleEndian(
            snapshot[(SnapshotTimerAccumulatorOffset + 8)..]);

        for (int index = 0; index < _operators.Length; index++)
        {
            int recordOffset = SnapshotOperatorsOffset + index * SnapshotOperatorSize;
            ReadOnlySpan<byte> record = snapshot.Slice(recordOffset, SnapshotOperatorSize);
            ReadSnapshotOperator(record, _operators[index]);
        }

        for (int index = 0; index < _voices.Length; index++)
        {
            int recordOffset = SnapshotVoicesOffset + index * SnapshotVoiceSize;
            ReadOnlySpan<byte> record = snapshot.Slice(recordOffset, SnapshotVoiceSize);
            ReadSnapshotVoice(record, _voices[index]);
        }
    }

    /// <summary>Returns saved state to constructed defaults, preserving the output rate.</summary>
    public void ResetSnapshotState()
    {
        Array.Clear(_registers);
        Array.Clear(_selected);
        _opl3Mode = false;
        _timer1 = 0;
        _timer2 = 0;
        _timerControl = 0;
        _status = 0;
        _timer1Accumulator = 0;
        _timer2Accumulator = 0;

        // Operators/voices are private; no bus or host resource references are replaced.

        for (int index = 0; index < _operators.Length; index++)
        {
            _operators[index] = new Operator();
        }

        for (int index = 0; index < _voices.Length; index++)
        {
            _voices[index] = new Voice();
        }
    }

    // Operator record offsets: 0 multiple, 1 total level, 2 key-scale level,
    // 3 attack, 4 decay, 5 sustain level, 6 release, 7 sustain enabled,
    // 8 key-scale rate, 9 waveform, 10 phase DOUBLE, 18 envelope DOUBLE,
    // 26 envelope phase enum. Cached parameters remain independent of registers.
    private static void WriteSnapshotOperator(Span<byte> data, Operator op)
    {
        data[0] = (byte)op.Multiple;
        data[1] = (byte)op.TotalLevel;
        data[2] = (byte)op.KeyScaleLevel;
        data[3] = (byte)op.AttackRate;
        data[4] = (byte)op.DecayRate;
        data[5] = (byte)op.SustainLevel;
        data[6] = (byte)op.ReleaseRate;
        data[7] = SnapshotBoolean(op.SustainEnabled);
        data[8] = SnapshotBoolean(op.KeyScaleRate);
        data[9] = (byte)op.Waveform;

        BinaryPrimitives.WriteDoubleLittleEndian(data[10..], op.Phase);
        BinaryPrimitives.WriteDoubleLittleEndian(data[18..], op.EnvelopeLevel);
        data[26] = (byte)op.Phase_;
    }

    private static void ReadSnapshotOperator(ReadOnlySpan<byte> data, Operator op)
    {
        op.Multiple = data[0];
        op.TotalLevel = data[1];
        op.KeyScaleLevel = data[2];
        op.AttackRate = data[3];
        op.DecayRate = data[4];
        op.SustainLevel = data[5];
        op.ReleaseRate = data[6];
        op.SustainEnabled = data[7] != 0;
        op.KeyScaleRate = data[8] != 0;
        op.Waveform = data[9];

        op.Phase = BinaryPrimitives.ReadDoubleLittleEndian(data[10..]);
        op.EnvelopeLevel = BinaryPrimitives.ReadDoubleLittleEndian(data[18..]);
        op.Phase_ = (EnvelopePhase)data[26];
    }

    // Voice record offsets: 0 F-number WORD, 2 block, 3 key-on, 4 feedback,
    // 5 additive connection, 6 left enabled, 7 right enabled.
    private static void WriteSnapshotVoice(Span<byte> data, Voice voice)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)voice.FNumber);
        data[2] = (byte)voice.Block;
        data[3] = SnapshotBoolean(voice.KeyOn);
        data[4] = (byte)voice.Feedback;
        data[5] = SnapshotBoolean(voice.Additive);
        data[6] = SnapshotBoolean(voice.Left);
        data[7] = SnapshotBoolean(voice.Right);
    }

    private static void ReadSnapshotVoice(ReadOnlySpan<byte> data, Voice voice)
    {
        voice.FNumber = BinaryPrimitives.ReadUInt16LittleEndian(data);
        voice.Block = data[2];
        voice.KeyOn = data[3] != 0;
        voice.Feedback = data[4];
        voice.Additive = data[5] != 0;
        voice.Left = data[6] != 0;
        voice.Right = data[7] != 0;
    }

    private static byte SnapshotBoolean(bool value) => value ? (byte)1 : (byte)0;

    private static void ValidateSnapshotBoolean(byte value) => ValidateSnapshotByte(value, 1);

    private static void ValidateSnapshotByte(byte value, byte maximum)
    {
        if (value > maximum)
        {
            throw new InvalidDataException("OPL snapshot field is outside its valid range.");
        }
    }

    private static void ValidateSnapshotDouble(
        double value, double minimum, double maximum, bool maximumInclusive)
    {
        if (!double.IsFinite(value) || value < minimum
            || (maximumInclusive ? value > maximum : value >= maximum))
        {
            throw new InvalidDataException("OPL snapshot floating-point state is invalid.");
        }
    }
}
