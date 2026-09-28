using System.Buffers.Binary;

namespace Aurora386.Core.Devices.Audio;

/// <summary>
/// Preserves the early-1990s SB16 DSP byte protocol and DMA playback between
/// driver writes, including already rendered core PCM and nested CT1745/YMF262 state.
/// </summary>
public sealed partial class SoundBlaster16
{
    private const uint SnapshotMagic = 0x31505344; // "DSP1"
    private const ushort PayloadFormatVersion = 1;
    private const ushort SnapshotHeaderSize = 108;
    private const int SnapshotOutputRateOffset = 8;
    private const int SnapshotCapacityOffset = 12;
    private const int SnapshotBasePortOffset = 16;
    private const int SnapshotEnabledOffset = 18;
    private const int SnapshotSpeakerOffset = 19;
    private const int SnapshotIrqOffset = 20;
    private const int SnapshotDma8Offset = 24;
    private const int SnapshotDma16Offset = 28;
    private const int SnapshotCommandOffset = 32;
    private const int SnapshotResetOffset = 33;
    private const int SnapshotLastOutputOffset = 34;
    private const int SnapshotIdentificationOffset = 35;
    private const int SnapshotTestOffset = 36;
    private const int SnapshotTimeConstantOffset = 37;
    private const int SnapshotAutoInitOffset = 38;
    private const int Snapshot16BitOffset = 39;
    private const int SnapshotStereoOffset = 40;
    private const int SnapshotSignedOffset = 41;
    private const int SnapshotPausedOffset = 42;
    private const int SnapshotPlayingOffset = 43;
    private const int SnapshotArgumentCountOffset = 44;
    private const int SnapshotArgumentsReceivedOffset = 48;
    private const int SnapshotArgumentsOffset = 52;
    private const int SnapshotPlaybackRateOffset = 56;
    private const int SnapshotBlockLengthOffset = 60;
    private const int SnapshotBlockRemainingOffset = 64;
    private const int SnapshotSampleFractionOffset = 68;
    private const int SnapshotResampleFractionOffset = 76;
    private const int SnapshotFmFractionOffset = 84;
    private const int SnapshotFifoCountOffset = 92;
    private const int SnapshotRingCountOffset = 96;
    private const int SnapshotMixerLengthOffset = 100;
    private const int SnapshotOplLengthOffset = 104;
    private const int SnapshotNestedOplOutputRateOffset = 10; // OPL1 v1 signed output rate.

    /// <summary>
    /// Captures owned bytes while device execution and the audio consumer are stopped.
    /// Ring shorts are saved in dequeue order, including an odd remaining count.
    /// </summary>
    public byte[] Capture()
    {
        byte[] mixer = Mixer.Capture();
        byte[] opl = Fm.Capture();
        int length = checked(SnapshotHeaderSize + _outputFifo.Count + _ringCount * 2
            + mixer.Length + opl.Length);
        byte[] data = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, SnapshotMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), PayloadFormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), SnapshotHeaderSize);
        WriteSnapshotInt(data, SnapshotOutputRateOffset, SampleRate);
        WriteSnapshotInt(data, SnapshotCapacityOffset, _ring.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(SnapshotBasePortOffset), BasePort);
        data[SnapshotEnabledOffset] = SnapshotBoolean(Enabled);
        data[SnapshotSpeakerOffset] = SnapshotBoolean(SpeakerEnabled);
        WriteSnapshotInt(data, SnapshotIrqOffset, Irq);
        WriteSnapshotInt(data, SnapshotDma8Offset, Dma8);
        WriteSnapshotInt(data, SnapshotDma16Offset, Dma16);
        data[SnapshotCommandOffset] = _pendingCommand;
        data[SnapshotResetOffset] = _lastResetWrite;
        data[SnapshotLastOutputOffset] = _lastOutputByte;
        data[SnapshotIdentificationOffset] = _identificationValue;
        data[SnapshotTestOffset] = _testRegister;
        data[SnapshotTimeConstantOffset] = _timeConstant;
        data[SnapshotAutoInitOffset] = SnapshotBoolean(_autoInit);
        data[Snapshot16BitOffset] = SnapshotBoolean(_is16Bit);
        data[SnapshotStereoOffset] = SnapshotBoolean(_isStereo);
        data[SnapshotSignedOffset] = SnapshotBoolean(_isSigned);
        data[SnapshotPausedOffset] = SnapshotBoolean(_paused);
        data[SnapshotPlayingOffset] = SnapshotBoolean(_playing);
        WriteSnapshotInt(data, SnapshotArgumentCountOffset, _pendingArgumentCount);
        WriteSnapshotInt(data, SnapshotArgumentsReceivedOffset, _argumentsReceived);
        _arguments.CopyTo(data, SnapshotArgumentsOffset);
        WriteSnapshotInt(data, SnapshotPlaybackRateOffset, _sampleRate);
        WriteSnapshotInt(data, SnapshotBlockLengthOffset, _blockLength);
        WriteSnapshotInt(data, SnapshotBlockRemainingOffset, _blockRemaining);
        BinaryPrimitives.WriteDoubleLittleEndian(
            data.AsSpan(SnapshotSampleFractionOffset), _sampleAccumulator);
        BinaryPrimitives.WriteDoubleLittleEndian(
            data.AsSpan(SnapshotResampleFractionOffset), _resampleAccumulator);
        BinaryPrimitives.WriteDoubleLittleEndian(
            data.AsSpan(SnapshotFmFractionOffset), _fmAccumulator);
        WriteSnapshotInt(data, SnapshotFifoCountOffset, _outputFifo.Count);
        WriteSnapshotInt(data, SnapshotRingCountOffset, _ringCount);
        WriteSnapshotInt(data, SnapshotMixerLengthOffset, mixer.Length);
        WriteSnapshotInt(data, SnapshotOplLengthOffset, opl.Length);

        int cursor = SnapshotHeaderSize;
        foreach (byte value in _outputFifo)
        {
            data[cursor++] = value;
        }

        for (int index = 0; index < _ringCount; index++)
        {
            short value = _ring[(_ringRead + index) % _ring.Length];
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(cursor), value);
            cursor += 2;
        }

        mixer.CopyTo(data, cursor);
        opl.CopyTo(data, cursor + mixer.Length);
        ValidateSnapshot(data);
        return data;
    }

    /// <summary>
    /// Validates the entire DSP and nested payload before any mutation or allocation
    /// based on its counts. Public resource integers remain unrestricted; cross-owner
    /// consistency and bus mapping preflight belong to machine integration.
    /// </summary>
    public static void ValidateSnapshot(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < SnapshotHeaderSize
            || BinaryPrimitives.ReadUInt32LittleEndian(data) != SnapshotMagic
            || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)) != PayloadFormatVersion
            || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6)) != SnapshotHeaderSize)
        {
            throw new InvalidDataException("DSP snapshot header is invalid.");
        }

        int outputRate = ReadSnapshotInt(data, SnapshotOutputRateOffset);
        int capacity = ReadSnapshotInt(data, SnapshotCapacityOffset);
        int fifoCount = ReadSnapshotInt(data, SnapshotFifoCountOffset);
        int ringCount = ReadSnapshotInt(data, SnapshotRingCountOffset);
        int mixerLength = ReadSnapshotInt(data, SnapshotMixerLengthOffset);
        int oplLength = ReadSnapshotInt(data, SnapshotOplLengthOffset);
        long expectedLength = SnapshotHeaderSize + (long)fifoCount + (long)ringCount * 2
            + mixerLength + oplLength;
        if (outputRate <= 0 || capacity != outputRate || fifoCount < 0
            || ringCount < 0 || ringCount > capacity || mixerLength < 0 || oplLength < 0
            || expectedLength != data.Length)
        {
            throw new InvalidDataException("DSP snapshot length, rate or queue count is invalid.");
        }

        ValidateSnapshotBoolean(data[SnapshotEnabledOffset]);
        ValidateSnapshotBoolean(data[SnapshotSpeakerOffset]);
        for (int offset = SnapshotAutoInitOffset; offset <= SnapshotPlayingOffset; offset++)
        {
            ValidateSnapshotBoolean(data[offset]);
        }

        int argumentCount = ReadSnapshotInt(data, SnapshotArgumentCountOffset);
        int received = ReadSnapshotInt(data, SnapshotArgumentsReceivedOffset);
        int commandArguments = ArgumentCount(data[SnapshotCommandOffset]);
        // Completion clears pending count but retains received bytes; reset clears
        // both counts, while argument storage intentionally keeps stale trailing bytes.
        bool validArguments = argumentCount == 0
            ? received == 0 || received == commandArguments
            : argumentCount == commandArguments && received >= 0 && received < argumentCount;
        int playbackRate = ReadSnapshotInt(data, SnapshotPlaybackRateOffset);
        int blockLength = ReadSnapshotInt(data, SnapshotBlockLengthOffset);
        int remaining = ReadSnapshotInt(data, SnapshotBlockRemainingOffset);
        // Command 48h can rewrite the next block length during playback without
        // changing the current countdown, so these counters have independent bounds.
        if (!validArguments || playbackRate <= 0 || playbackRate > 1_000_000
            || blockLength < 0 || remaining < 0)
        {
            throw new InvalidDataException("DSP snapshot parser or playback state is invalid.");
        }

        ValidateSnapshotFraction(data, SnapshotSampleFractionOffset);
        ValidateSnapshotFraction(data, SnapshotResampleFractionOffset);
        ValidateSnapshotFraction(data, SnapshotFmFractionOffset);
        int mixerOffset = checked(SnapshotHeaderSize + fifoCount + ringCount * 2);
        SbMixer.ValidateSnapshot(data.AsSpan(mixerOffset, mixerLength).ToArray());
        byte[] opl = data.AsSpan(mixerOffset + mixerLength, oplLength).ToArray();
        Opl3.ValidateSnapshot(opl);
        if (ReadSnapshotInt(opl, SnapshotNestedOplOutputRateOffset) != outputRate)
        {
            throw new InvalidDataException("DSP and nested OPL snapshot output rates differ.");
        }
    }

    /// <summary>Checks DSP and mixer resource owners against applied PnP configuration.</summary>
    internal static void ValidateMachineSnapshotResources(byte[] data, ushort basePort,
        bool enabled, int irq, int dma8, int dma16)
    {
        ValidateSnapshot(data);
        int mixerOffset = SnapshotHeaderSize + ReadSnapshotInt(data, SnapshotFifoCountOffset)
            + 2 * ReadSnapshotInt(data, SnapshotRingCountOffset);
        var mixer = new SbMixer();
        mixer.Restore(data.AsSpan(mixerOffset, ReadSnapshotInt(data, SnapshotMixerLengthOffset)).ToArray());
        if (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(SnapshotBasePortOffset)) != basePort
            || (data[SnapshotEnabledOffset] != 0) != enabled
            || ReadSnapshotInt(data, SnapshotIrqOffset) != irq
            || ReadSnapshotInt(data, SnapshotDma8Offset) != dma8
            || ReadSnapshotInt(data, SnapshotDma16Offset) != dma16
            || mixer.Irq != irq || mixer.Dma8 != dma8 || mixer.Dma16 != dma16)
        {
            throw new InvalidDataException("DSP, mixer and applied ISA PnP resources differ.");
        }
    }

    /// <summary>Requires the target's fixed output rate and PCM capacity to match.</summary>
    public void ValidateSnapshotForRestore(byte[] data)
    {
        ValidateSnapshot(data);
        if (ReadSnapshotInt(data, SnapshotOutputRateOffset) != SampleRate
            || ReadSnapshotInt(data, SnapshotCapacityOffset) != _ring.Length)
        {
            throw new InvalidDataException("DSP snapshot output rate or ring capacity does not match the target.");
        }
    }

    /// <summary>
    /// Installs validated raw latches without commands, DMA, rendering or IRQ edges.
    /// Existing buses, chip objects and diagnostics remain target-owned. This does
    /// not rebuild port mappings; enclosing machine integration owns that step.
    /// </summary>
    public void Restore(byte[] data)
    {
        ValidateSnapshotForRestore(data);
        int fifoCount = ReadSnapshotInt(data, SnapshotFifoCountOffset);
        int ringCount = ReadSnapshotInt(data, SnapshotRingCountOffset);
        int mixerLength = ReadSnapshotInt(data, SnapshotMixerLengthOffset);
        int oplLength = ReadSnapshotInt(data, SnapshotOplLengthOffset);
        int mixerOffset = SnapshotHeaderSize + fifoCount + ringCount * 2;
        byte[] mixer = data.AsSpan(mixerOffset, mixerLength).ToArray();
        byte[] opl = data.AsSpan(mixerOffset + mixerLength, oplLength).ToArray();
        Fm.ValidateSnapshotForRestore(opl);
        _outputFifo.EnsureCapacity(fifoCount);

        // All nested validation and queue allocation precedes raw state installation.
        Mixer.Restore(mixer);
        Fm.Restore(opl);
        BasePort = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(SnapshotBasePortOffset));
        Enabled = data[SnapshotEnabledOffset] != 0;
        SpeakerEnabled = data[SnapshotSpeakerOffset] != 0;
        Irq = ReadSnapshotInt(data, SnapshotIrqOffset);
        Dma8 = ReadSnapshotInt(data, SnapshotDma8Offset);
        Dma16 = ReadSnapshotInt(data, SnapshotDma16Offset);
        _pendingCommand = data[SnapshotCommandOffset];
        _lastResetWrite = data[SnapshotResetOffset];
        _lastOutputByte = data[SnapshotLastOutputOffset];
        _identificationValue = data[SnapshotIdentificationOffset];
        _testRegister = data[SnapshotTestOffset];
        _timeConstant = data[SnapshotTimeConstantOffset];
        _autoInit = data[SnapshotAutoInitOffset] != 0;
        _is16Bit = data[Snapshot16BitOffset] != 0;
        _isStereo = data[SnapshotStereoOffset] != 0;
        _isSigned = data[SnapshotSignedOffset] != 0;
        _paused = data[SnapshotPausedOffset] != 0;
        _playing = data[SnapshotPlayingOffset] != 0;
        _pendingArgumentCount = ReadSnapshotInt(data, SnapshotArgumentCountOffset);
        _argumentsReceived = ReadSnapshotInt(data, SnapshotArgumentsReceivedOffset);
        data.AsSpan(SnapshotArgumentsOffset, _arguments.Length).CopyTo(_arguments);
        _sampleRate = ReadSnapshotInt(data, SnapshotPlaybackRateOffset);
        _blockLength = ReadSnapshotInt(data, SnapshotBlockLengthOffset);
        _blockRemaining = ReadSnapshotInt(data, SnapshotBlockRemainingOffset);
        _sampleAccumulator = BinaryPrimitives.ReadDoubleLittleEndian(
            data.AsSpan(SnapshotSampleFractionOffset));
        _resampleAccumulator = BinaryPrimitives.ReadDoubleLittleEndian(
            data.AsSpan(SnapshotResampleFractionOffset));
        _fmAccumulator = BinaryPrimitives.ReadDoubleLittleEndian(
            data.AsSpan(SnapshotFmFractionOffset));
        _outputFifo.Clear();
        int cursor = SnapshotHeaderSize;
        for (int index = 0; index < fifoCount; index++)
        {
            _outputFifo.Enqueue(data[cursor++]);
        }

        Array.Clear(_ring);
        for (int index = 0; index < ringCount; index++)
        {
            _ring[index] = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(cursor));
            cursor += 2;
        }

        _ringRead = 0;
        _ringWrite = ringCount % _ring.Length;
        _ringCount = ringCount;
    }

    /// <summary>
    /// Returns saved state to constructed defaults without guest reset commands or
    /// bus callbacks. Preserves constructor rate/base, dependencies and diagnostics.
    /// </summary>
    public void ResetSnapshotState()
    {
        Mixer.ResetSnapshotState();
        Fm.ResetSnapshotState();
        BasePort = _constructedBasePort;
        Enabled = true;
        Irq = 5;
        Dma8 = 1;
        Dma16 = 5;
        _lastResetWrite = 0xFF;
        _lastOutputByte = 0xFF;
        _identificationValue = 0;
        _testRegister = 0;
        _timeConstant = 0;
        _sampleRate = 22050;
        _blockLength = 0;
        _is16Bit = false;
        _isStereo = false;
        _isSigned = false;
        _sampleAccumulator = 0;
        _resampleAccumulator = 0;
        _fmAccumulator = 0;
        Array.Clear(_arguments);
        Array.Clear(_ring);
        _ringRead = 0;
        _ringWrite = 0;
        _ringCount = 0;
        ResetDsp();
    }

    private static byte SnapshotBoolean(bool value) => value ? (byte)1 : (byte)0;

    private static int ReadSnapshotInt(byte[] data, int offset)
        => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));

    private static void WriteSnapshotInt(byte[] data, int offset, int value)
        => BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), value);

    private static void ValidateSnapshotBoolean(byte value)
    {
        if (value > 1)
        {
            throw new InvalidDataException("DSP snapshot boolean is invalid.");
        }
    }

    private static void ValidateSnapshotFraction(byte[] data, int offset)
    {
        double value = BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(offset));
        if (!double.IsFinite(value) || value < 0 || value >= 1)
        {
            throw new InvalidDataException("DSP snapshot timing fraction is invalid.");
        }
    }
}
