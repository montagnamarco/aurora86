namespace Aurora386.Core.Devices.Audio;

/// <summary>
/// The eight stream descriptors (SD0-SD7 at 80h+n*20h) and the DMA position
/// buffer (DPLBASE/DPUBASE, 70h-77h): every byte a guest ever exchanges with
/// <see cref="HdaCodec"/>'s DAC passes through here. <see cref="GcapValue"/>
/// fixes SD0-SD3 as input streams and SD4-SD7 as output streams; this
/// controller's codec is output-only, so SD0-SD3 exist and answer their
/// registers (a driver enumerates all eight) but only ever write silence
/// into whatever ring a driver points them at (*choice*, no host capture).
///
/// Only one output stream at a time is ever mixed to the host: the one
/// whose own stream tag (Set Stream/Channel, verb 706h, bits 7:4 of the
/// descriptor's own CTL byte 2) matches <see cref="HdaCodec.DacStreamTag"/>
/// -- exactly the contract's own wiring, since the DAC has exactly one
/// stream binding at a time. See <see cref="HdaController"/>'s own class
/// summary and <c>docs/HD-AUDIO.md</c> for this file's *choice* marks.
/// </summary>
public sealed partial class HdaController
{
    // --- register offsets, relative to a stream's own 20h slot -------------

    private const int RegStreamsBase = 0x80;
    private const int StreamStride = 0x20;

    /// <summary>4 input (SD0-SD3) + 4 output (SD4-SD7), per <see cref="GcapValue"/>'s own ISS/OSS split.</summary>
    private const int StreamDescriptorCount = 8;

    private const int InputStreamCount = 4;

    private const int RelStreamCtl0 = 0x00;
    private const int RelStreamCtl1 = 0x01;
    private const int RelStreamCtl2 = 0x02;
    private const int RelStreamSts = 0x03;
    private const int RelStreamLpib = 0x04;
    private const int RelStreamCbl = 0x08;
    private const int RelStreamLvi = 0x0C;
    private const int RelStreamFifoW = 0x0E;
    private const int RelStreamFifoS = 0x10;
    private const int RelStreamFmt = 0x12;
    private const int RelStreamBdpl = 0x18;
    private const int RelStreamBdpu = 0x1C;

    // --- SDnCTL, byte 0 (offset +00h) ----------------------------------------

    private const byte StreamCtlSrstBit = 1 << 0;
    private const byte StreamCtlRunBit = 1 << 1;
    private const byte StreamCtlIoceBit = 1 << 2;
    private const byte StreamCtlFeieBit = 1 << 3;
    private const byte StreamCtlDeieBit = 1 << 4;

    // --- SDnSTS, byte 3 of the same dword (offset +03h) ----------------------

    private const byte StreamStatusBcisBit = 1 << 2;
    private const byte StreamStatusFifoeBit = 1 << 3;
    private const byte StreamStatusDeseBit = 1 << 4;
    private const byte StreamStatusFifoRdyBit = 1 << 5;
    private const byte StreamStatusRw1cMask = StreamStatusBcisBit | StreamStatusFifoeBit | StreamStatusDeseBit;

    /// <summary>16-bit, fixed (*choice*: not functionally used by any driver this project boots).</summary>
    private const ushort FifoWatermarkValue = 0x04;

    /// <summary>*choice*, the contract's own suggested constants: an output stream's FIFO is deeper than an input one's.</summary>
    private const ushort OutputFifoSizeValue = 0xFF;

    private const ushort InputFifoSizeValue = 0x3F;

    // --- SDnFMT (offset +12h): base rate, multiplier, divisor, bits, channels ---

    private const int FmtBaseBit = 1 << 14;
    private const int FmtMultiplierShift = 11;
    private const int FmtMultiplierMask = 0x7;
    private const int FmtDivisorShift = 8;
    private const int FmtDivisorMask = 0x7;
    private const int FmtBitsShift = 4;
    private const int FmtBitsMask = 0x7;
    private const int FmtChannelsMask = 0xF;

    // --- DPLBASE (70h) / DPUBASE (74h) ----------------------------------------

    private const uint DmaPositionEnableBit = 1u << 0;

    /// <summary>2084h+n*20h: an optional, read-only mirror of each stream's own LPIB (*choice*, offered because some drivers probe it to avoid an MMIO read of the stream area itself).</summary>
    private const int LpibAliasBase = 0x2084;

    /// <summary>48000 Hz, the same fixed host mix rate <see cref="Es1371"/> resamples its own two DACs to.</summary>
    private const int MixSampleRate = 48000;

    /// <summary>Half a second of stereo headroom, the same sizing <see cref="Es1371"/> uses for its own output rings.</summary>
    private const int MixRingCapacityFrames = MixSampleRate / 2;

    /// <summary>
    /// One stream descriptor's register-visible state plus the internal
    /// cursor a real chip would keep in silicon: which cached Buffer
    /// Descriptor List entry is current, how many bytes of it have already
    /// been moved, and the fractional native-rate frame this stream's own
    /// <see cref="HdaController.AdvanceStream"/> call still owes it.
    /// </summary>
    private sealed class StreamDescriptor
    {
        public byte ControlByte0;
        public byte ControlByte1; // reserved: no bit defined here for a non-bidirectional, non-striped stream
        public byte ControlByte2; // bits 7:4 stream tag; bit 3 (DIR) ignored, direction is fixed by this stream's own index
        public byte Status;
        public uint Lpib;
        public uint Cbl;
        public ushort Lvi;
        public ushort Fmt;
        public uint Bdpl;
        public uint Bdpu;

        public bool BdlLoaded;
        public (uint Address, uint Length, bool Ioc)[] BdlEntries = [];
        public int BdlEntryIndex;
        public uint BytesConsumedInEntry;
        public double SampleAccumulator;
    }

    private readonly StreamDescriptor[] _streams = new StreamDescriptor[StreamDescriptorCount];

    private uint _dplBase;
    private uint _dpuBase;

    /// <summary>Which physical stream is currently feeding <see cref="_mixRing"/>, or -1 while none is bound -- used only to reset <see cref="_hostMixResampleAccumulator"/> cleanly when the binding changes.</summary>
    private int _hostMixBoundStreamIndex = -1;

    private double _hostMixResampleAccumulator;
    private readonly short[] _mixRing = new short[MixRingCapacityFrames * 2];
    private int _mixRingWrite;
    private int _mixRingRead;
    private int _mixRingCount;

    private void InitializeStreams()
    {
        for (int i = 0; i < StreamDescriptorCount; i++)
        {
            _streams[i] = new StreamDescriptor();
        }
    }

    private void ResetStreams()
    {
        _dplBase = 0;
        _dpuBase = 0;
        _hostMixBoundStreamIndex = -1;
        _hostMixResampleAccumulator = 0;

        foreach (StreamDescriptor stream in _streams)
        {
            stream.ControlByte0 = 0;
            stream.ControlByte1 = 0;
            stream.ControlByte2 = 0;
            stream.Status = 0; // FIFORDY is not stored here; it is computed at read time, see ReadStreamStatusByte
            stream.Lpib = 0;
            stream.Cbl = 0;
            stream.Lvi = 0;
            stream.Fmt = 0;
            stream.Bdpl = 0;
            stream.Bdpu = 0;
            stream.BdlLoaded = false;
            stream.BdlEntries = [];
            stream.BdlEntryIndex = 0;
            stream.BytesConsumedInEntry = 0;
            stream.SampleAccumulator = 0;
        }

        Array.Clear(_mixRing);
        _mixRingWrite = 0;
        _mixRingRead = 0;
        _mixRingCount = 0;
    }

    private static bool IsOutputStream(int streamIndex) => streamIndex >= InputStreamCount;

    // --- register dispatch: 80h-17Fh (stream area) and 70h-7Fh (DMA position) ---

    private byte ReadStreamAreaByte(int relativeOffset)
    {
        int streamIndex = relativeOffset / StreamStride;
        int withinStream = relativeOffset % StreamStride;
        return streamIndex >= StreamDescriptorCount ? (byte)0 : ReadStreamRegisterByte(streamIndex, withinStream);
    }

    private void WriteStreamAreaByte(int relativeOffset, byte value)
    {
        int streamIndex = relativeOffset / StreamStride;
        int withinStream = relativeOffset % StreamStride;
        if (streamIndex < StreamDescriptorCount)
        {
            WriteStreamRegisterByte(streamIndex, withinStream, value);
        }
    }

    private byte ReadStreamRegisterByte(int streamIndex, int offset)
    {
        StreamDescriptor stream = _streams[streamIndex];
        return offset switch
        {
            RelStreamCtl0 => stream.ControlByte0,
            RelStreamCtl1 => stream.ControlByte1,
            RelStreamCtl2 => stream.ControlByte2,
            RelStreamSts => ReadStreamStatusByte(streamIndex),
            >= RelStreamLpib and < RelStreamLpib + 4 => (byte)(stream.Lpib >> (8 * (offset - RelStreamLpib))),
            >= RelStreamCbl and < RelStreamCbl + 4 => (byte)(stream.Cbl >> (8 * (offset - RelStreamCbl))),
            RelStreamLvi => (byte)stream.Lvi,
            RelStreamLvi + 1 => 0, // LVI is an 8-bit field; the high byte is reserved
            RelStreamFifoW => (byte)FifoWatermarkValue,
            RelStreamFifoW + 1 => (byte)(FifoWatermarkValue >> 8),
            RelStreamFifoS => (byte)FifoSizeFor(streamIndex),
            RelStreamFifoS + 1 => (byte)(FifoSizeFor(streamIndex) >> 8),
            RelStreamFmt => (byte)stream.Fmt,
            RelStreamFmt + 1 => (byte)(stream.Fmt >> 8),
            >= RelStreamBdpl and < RelStreamBdpl + 4 => (byte)(stream.Bdpl >> (8 * (offset - RelStreamBdpl))),
            >= RelStreamBdpu and < RelStreamBdpu + 4 => (byte)(stream.Bdpu >> (8 * (offset - RelStreamBdpu))),
            _ => 0,
        };
    }

    private void WriteStreamRegisterByte(int streamIndex, int offset, byte value)
    {
        StreamDescriptor stream = _streams[streamIndex];
        switch (offset)
        {
            case RelStreamCtl0:
                WriteStreamControlByte0(stream, value);
                break;
            case RelStreamCtl1:
                stream.ControlByte1 = value; // reserved, no defined bit
                break;
            case RelStreamCtl2:
                stream.ControlByte2 = value;
                break;
            case RelStreamSts:
                stream.Status &= (byte)~(value & StreamStatusRw1cMask);
                UpdateInterruptLine();
                break;
            case >= RelStreamLpib and < RelStreamLpib + 4:
                break; // RO
            case >= RelStreamCbl and < RelStreamCbl + 4:
                stream.Cbl = MergeLane(stream.Cbl, offset - RelStreamCbl, value);
                stream.BdlLoaded = false;
                break;
            case RelStreamLvi:
                stream.Lvi = value; // 8-bit field
                stream.BdlLoaded = false;
                break;
            case RelStreamLvi + 1:
                break; // reserved
            case RelStreamFifoW:
            case RelStreamFifoW + 1:
            case RelStreamFifoS:
            case RelStreamFifoS + 1:
                break; // RO
            case RelStreamFmt:
                stream.Fmt = (ushort)((stream.Fmt & 0xFF00) | value);
                break;
            case RelStreamFmt + 1:
                stream.Fmt = (ushort)((stream.Fmt & 0x00FF) | (value << 8));
                break;
            case >= RelStreamBdpl and < RelStreamBdpl + 4:
                stream.Bdpl = MergeLane(stream.Bdpl, offset - RelStreamBdpl, value);
                stream.BdlLoaded = false;
                break;
            case >= RelStreamBdpu and < RelStreamBdpu + 4:
                stream.Bdpu = MergeLane(stream.Bdpu, offset - RelStreamBdpu, value);
                stream.BdlLoaded = false;
                break;
            default:
                break;
        }
    }

    private static uint FifoSizeFor(int streamIndex) => IsOutputStream(streamIndex) ? OutputFifoSizeValue : InputFifoSizeValue;

    /// <summary>
    /// FIFORDY (bit 5 of SDnSTS): not stored -- SRST must not be able to
    /// clear it permanently (a real chip, and QEMU's own model, simply
    /// stop reporting a ready FIFO while a stream is held in reset and
    /// resume reporting one the instant it is set up again). Computed here
    /// instead, from the stream's own current SRST bit: 1 whenever the
    /// stream is out of reset, 0 while SRST is held.
    /// </summary>
    private byte ReadStreamStatusByte(int streamIndex)
    {
        StreamDescriptor stream = _streams[streamIndex];
        byte status = stream.Status;
        bool outOfReset = (stream.ControlByte0 & StreamCtlSrstBit) == 0;
        if (outOfReset)
        {
            status |= StreamStatusFifoRdyBit;
        }

        return status;
    }

    /// <summary>SRST (bit 0): entering reset clears the DMA engine's own moving parts -- LPIB, the RW1C status bits and the BDL cursor -- but not what the driver programmed (CBL/LVI/BDPL/BDPU/FMT). RUN (bit 1) going 0-&gt;1 forces a fresh BDL reload, since CBL/LVI/BDPL/BDPU may have changed since the stream last ran. Any bit here can change whether a pending condition is qualified to reach INTSTS (IOCE/FEIE/DEIE) or clear one outright (SRST), so the interrupt line is always recomputed afterward.</summary>
    private void WriteStreamControlByte0(StreamDescriptor stream, byte value)
    {
        bool previousSrst = (stream.ControlByte0 & StreamCtlSrstBit) != 0;
        bool previousRun = (stream.ControlByte0 & StreamCtlRunBit) != 0;

        stream.ControlByte0 = value;

        if (!previousSrst && (value & StreamCtlSrstBit) != 0)
        {
            stream.Lpib = 0;
            stream.Status = 0;
            stream.BdlLoaded = false;
            stream.BdlEntryIndex = 0;
            stream.BytesConsumedInEntry = 0;
            stream.SampleAccumulator = 0;
        }

        if (!previousRun && (value & StreamCtlRunBit) != 0)
        {
            stream.BdlLoaded = false;
        }

        UpdateInterruptLine();
    }

    private byte ReadDmaPositionByte(int relativeOffset) => relativeOffset switch
    {
        >= 0 and < 4 => (byte)(_dplBase >> (8 * relativeOffset)),
        >= 4 and < 8 => (byte)(_dpuBase >> (8 * (relativeOffset - 4))),
        _ => 0,
    };

    private void WriteDmaPositionByte(int relativeOffset, byte value)
    {
        switch (relativeOffset)
        {
            case >= 0 and < 4:
                _dplBase = MergeLane(_dplBase, relativeOffset, value);
                break;
            case >= 4 and < 8:
                _dpuBase = MergeLane(_dpuBase, relativeOffset - 4, value);
                break;
            default:
                break;
        }
    }

    /// <summary>The optional LPIB alias area (2084h+): read-only, 0 for the 28 bytes of each 20h slot that are not the LPIB dword itself.</summary>
    private bool TryReadLpibAlias(int offset, out byte value)
    {
        int relative = offset - LpibAliasBase;
        if (relative < 0 || relative >= StreamDescriptorCount * StreamStride)
        {
            value = 0;
            return false;
        }

        int streamIndex = relative / StreamStride;
        int withinStream = relative % StreamStride;
        value = withinStream < 4 ? (byte)(_streams[streamIndex].Lpib >> (8 * withinStream)) : (byte)0;
        return true;
    }

    // --- interrupts ------------------------------------------------------------

    /// <summary>
    /// This stream's own raw pending condition (BCIS/FIFOE/DESE, whichever
    /// is set): what INTSTS bit N itself reports, unqualified by SDnCTL's
    /// own enables. HDA spec 3.3.14 (Interrupt Status Register) does not
    /// describe this bit as gated by IOCE/FEIE/DEIE, and QEMU's own
    /// <c>intel-hda.c</c> does not gate BCIS by IOCE for it either -- SDnCTL's
    /// enables (3.3.13) instead gate whether the condition may assert the
    /// interrupt line, in <see cref="IsStreamInterruptQualified"/>.
    /// </summary>
    private bool IsStreamStatusPending(int streamIndex) => (_streams[streamIndex].Status & StreamStatusRw1cMask) != 0;

    /// <summary>Whether this stream's own pending condition is allowed to assert the interrupt line -- SDnCTL's own IOCE/FEIE/DEIE (3.3.13) gate each condition individually; INTCTL's own SIE bit gates it again in <see cref="HdaController.ReadIntstsDword"/>.</summary>
    private bool IsStreamInterruptQualified(int streamIndex)
    {
        StreamDescriptor stream = _streams[streamIndex];
        bool bufferCompleted = (stream.Status & StreamStatusBcisBit) != 0 && (stream.ControlByte0 & StreamCtlIoceBit) != 0;
        bool fifoError = (stream.Status & StreamStatusFifoeBit) != 0 && (stream.ControlByte0 & StreamCtlFeieBit) != 0;
        bool descriptorError = (stream.Status & StreamStatusDeseBit) != 0 && (stream.ControlByte0 & StreamCtlDeieBit) != 0;
        return bufferCompleted || fifoError || descriptorError;
    }

    // --- DMA: walking the Buffer Descriptor List -------------------------------

    /// <summary>Rebuilds a stream's own cached BDL entries from CBL/LVI/BDPL/BDPU in guest RAM. Called lazily, the first time <see cref="AdvanceStream"/> needs them after any register that describes the list has changed.</summary>
    private void LoadBdl(StreamDescriptor stream)
    {
        int entryCount = stream.Lvi + 1;
        var entries = new (uint Address, uint Length, bool Ioc)[entryCount];

        // Only the low base register addresses memory here, exactly like
        // CORB/RIRB above: see IDmaMemory's own summary about installed RAM
        // being reached through a 32-bit address (*choice*).
        uint bdlBase = stream.Bdpl & 0xFFFFFF80u; // 128-byte aligned

        for (int i = 0; i < entryCount; i++)
        {
            uint entryAddress = bdlBase + (uint)i * 16;
            uint address = _dma.ReadDword(entryAddress); // the entry's 64-bit address, low dword only
            uint length = _dma.ReadDword(entryAddress + 8);
            uint flags = _dma.ReadDword(entryAddress + 12);
            entries[i] = (address, length, (flags & 1) != 0);
        }

        stream.BdlEntries = entries;
        stream.BdlEntryIndex = Math.Clamp(stream.BdlEntryIndex, 0, entries.Length - 1);
        stream.BdlLoaded = true;
    }

    private static (int Rate, int Channels, int BytesPerSample) DecodeFormat(ushort fmt)
    {
        int baseRate = (fmt & FmtBaseBit) != 0 ? 44100 : 48000;
        int multiplier = ((fmt >> FmtMultiplierShift) & FmtMultiplierMask) + 1;
        int divisor = ((fmt >> FmtDivisorShift) & FmtDivisorMask) + 1;
        int rate = baseRate * multiplier / divisor;

        int bitsField = (fmt >> FmtBitsShift) & FmtBitsMask;
        int bytesPerSample = bitsField switch
        {
            0 => 1, // 8-bit
            1 => 2, // 16-bit
            _ => 4, // 20/24/32-bit, all carried in a 32-bit container (*choice*, see ReadOutputFrame)
        };

        int channels = (fmt & FmtChannelsMask) + 1;
        return (rate, channels, bytesPerSample);
    }

    private static int StreamTagOf(StreamDescriptor stream) => stream.ControlByte2 >> 4;

    /// <summary>Advances every stream descriptor by a span of emulated time, pacing each at its own programmed rate. Bus mastering off (<see cref="Pci.PciFunction.BusMasterEnabled"/>) stops every stream at once, exactly like every other bus master on this machine.</summary>
    private void AdvanceStreams(double microseconds)
    {
        if (!Function.BusMasterEnabled)
        {
            return;
        }

        for (int streamIndex = 0; streamIndex < StreamDescriptorCount; streamIndex++)
        {
            AdvanceStream(streamIndex, microseconds);
        }
    }

    private void AdvanceStream(int streamIndex, double microseconds)
    {
        StreamDescriptor stream = _streams[streamIndex];
        bool running = (stream.ControlByte0 & StreamCtlRunBit) != 0;
        bool inReset = (stream.ControlByte0 & StreamCtlSrstBit) != 0;
        if (!running || inReset)
        {
            return;
        }

        if (!stream.BdlLoaded)
        {
            LoadBdl(stream);
        }

        if (stream.BdlEntries.Length == 0 || stream.Cbl == 0)
        {
            return; // nothing programmed yet
        }

        (int rate, int channels, int bytesPerSample) = DecodeFormat(stream.Fmt);
        int bytesPerFrame = channels * bytesPerSample;
        if (bytesPerFrame == 0)
        {
            return;
        }

        stream.SampleAccumulator += microseconds * rate / 1_000_000.0;
        int framesWanted = (int)stream.SampleAccumulator;
        stream.SampleAccumulator -= framesWanted;

        bool isOutput = IsOutputStream(streamIndex);
        int dacStreamTag = _codec.DacStreamTag;
        bool boundToHostMix = isOutput && dacStreamTag != 0 && StreamTagOf(stream) == dacStreamTag;

        if (boundToHostMix && _hostMixBoundStreamIndex != streamIndex)
        {
            _hostMixBoundStreamIndex = streamIndex;
            _hostMixResampleAccumulator = 0;
        }

        for (int i = 0; i < framesWanted; i++)
        {
            if (isOutput)
            {
                (short left, short right) = ReadOutputFrame(stream, channels, bytesPerSample);
                if (boundToHostMix)
                {
                    PushHostMixFrame(rate, left, right);
                }
            }
            else
            {
                WriteInputSilenceFrame(stream, bytesPerFrame);
            }

            AdvanceStreamCursor(stream, bytesPerFrame);
            WriteDmaPositionIfEnabled(streamIndex, stream.Lpib);
        }
    }

    /// <summary>Reads and decodes one sample frame from an output stream's current BDL position. 8- and 16-bit samples decode exactly; wider containers are consumed at the right byte rate but their value is approximated from the container's own most significant 16 bits (*choice*, see <see cref="DecodeFormat"/>).</summary>
    private (short Left, short Right) ReadOutputFrame(StreamDescriptor stream, int channels, int bytesPerSample)
    {
        (uint entryAddress, _, _) = stream.BdlEntries[stream.BdlEntryIndex];
        uint frameAddress = entryAddress + stream.BytesConsumedInEntry;

        short DecodeChannel(int channelIndex)
        {
            uint sampleAddress = frameAddress + (uint)(channelIndex * bytesPerSample);
            return bytesPerSample switch
            {
                1 => ConvertUnsigned8(_dma.ReadByte(sampleAddress)),
                2 => (short)_dma.ReadWord(sampleAddress),
                _ => (short)(_dma.ReadDword(sampleAddress) >> 16),
            };
        }

        short left = DecodeChannel(0);
        short right = channels > 1 ? DecodeChannel(1) : left;
        return (left, right);
    }

    private static short ConvertUnsigned8(byte value) => (short)((value - 128) << 8);

    /// <summary>Writes silence into an input stream's current BDL position -- there is no host capture (*choice*). Never writes past the current entry's own buffer, even if a frame's byte count does not evenly divide the entry's length.</summary>
    private void WriteInputSilenceFrame(StreamDescriptor stream, int bytesPerFrame)
    {
        (uint entryAddress, uint entryLength, _) = stream.BdlEntries[stream.BdlEntryIndex];
        uint frameAddress = entryAddress + stream.BytesConsumedInEntry;
        uint remainingInEntry = entryLength > stream.BytesConsumedInEntry ? entryLength - stream.BytesConsumedInEntry : 0;
        int bytesToWrite = (int)Math.Min((uint)bytesPerFrame, remainingInEntry);

        for (int i = 0; i < bytesToWrite; i++)
        {
            _dma.WriteByte(frameAddress + (uint)i, 0);
        }
    }

    /// <summary>Advances LPIB by one frame's worth of bytes, wrapping at CBL, and moves to the next BDL entry once the current one is exhausted, setting BCIS unconditionally when that entry's own IOC flag is set and recomputing the interrupt line when it does.</summary>
    private void AdvanceStreamCursor(StreamDescriptor stream, int bytesPerFrame)
    {
        stream.BytesConsumedInEntry += (uint)bytesPerFrame;
        stream.Lpib += (uint)bytesPerFrame;
        if (stream.Lpib >= stream.Cbl)
        {
            stream.Lpib -= stream.Cbl;
        }

        (_, uint entryLength, bool entryIoc) = stream.BdlEntries[stream.BdlEntryIndex];
        if (stream.BytesConsumedInEntry < entryLength)
        {
            return; // still inside the current entry
        }

        stream.BytesConsumedInEntry = 0;
        stream.BdlEntryIndex = (stream.BdlEntryIndex + 1) % stream.BdlEntries.Length;

        if (entryIoc)
        {
            // Set unconditionally, regardless of IOCE: IOCE only gates
            // whether this also qualifies to assert the interrupt line
            // (IsStreamInterruptQualified); INTSTS bit N itself always
            // reflects this raw bit (IsStreamStatusPending).
            stream.Status |= StreamStatusBcisBit;
            UpdateInterruptLine();
        }
    }

    private void WriteDmaPositionIfEnabled(int streamIndex, uint lpib)
    {
        if ((_dplBase & DmaPositionEnableBit) == 0)
        {
            return;
        }

        uint baseAddress = _dplBase & 0xFFFFFF80u;
        _dma.WriteDword(baseAddress + (uint)streamIndex * 8, lpib);
    }

    // --- host mix: the DAC-bound output stream's own rendered audio -----------

    /// <summary>Applies the DAC's stereo output amplifier (gain and mute, independently per channel) and resamples by simple repetition -- the same technique <see cref="Es1371"/> uses -- into <see cref="_mixRing"/> at <see cref="MixSampleRate"/>.</summary>
    private void PushHostMixFrame(int nativeRate, short left, short right)
    {
        (double leftGain, bool leftMute) = _codec.DacLeftAmp;
        (double rightGain, bool rightMute) = _codec.DacRightAmp;
        short scaledLeft = leftMute ? (short)0 : ScaleSample(left, leftGain);
        short scaledRight = rightMute ? (short)0 : ScaleSample(right, rightGain);

        _hostMixResampleAccumulator += (double)MixSampleRate / nativeRate;
        int repeats = (int)_hostMixResampleAccumulator;
        _hostMixResampleAccumulator -= repeats;

        for (int i = 0; i < repeats; i++)
        {
            EnqueueMixFrame(scaledLeft, scaledRight);
        }
    }

    private static short ScaleSample(short sample, double linearGain)
        => (short)Math.Clamp(sample * linearGain, short.MinValue, short.MaxValue);

    private void EnqueueMixFrame(short left, short right)
    {
        if (_mixRingCount + 2 > _mixRing.Length)
        {
            _mixRingRead = (_mixRingRead + 2) % _mixRing.Length;
            _mixRingCount -= 2;
        }

        _mixRing[_mixRingWrite] = left;
        _mixRingWrite = (_mixRingWrite + 1) % _mixRing.Length;
        _mixRing[_mixRingWrite] = right;
        _mixRingWrite = (_mixRingWrite + 1) % _mixRing.Length;
        _mixRingCount += 2;
    }

    /// <summary>Drains <see cref="_mixRing"/> for <see cref="HdaController.ReadSamples"/>. Nothing bound or nothing rendered yet writes nothing and returns 0 -- silence, not an error, exactly as <see cref="IAudioSource"/>'s own contract calls for.</summary>
    private int ReadMixSamples(Span<short> destination)
    {
        int frameCount = Math.Min(destination.Length / 2, _mixRingCount / 2);
        for (int frame = 0; frame < frameCount; frame++)
        {
            destination[frame * 2] = _mixRing[_mixRingRead];
            _mixRingRead = (_mixRingRead + 1) % _mixRing.Length;
            destination[(frame * 2) + 1] = _mixRing[_mixRingRead];
            _mixRingRead = (_mixRingRead + 1) % _mixRing.Length;
            _mixRingCount -= 2;
        }

        return frameCount * 2;
    }

    // --- snapshot: embedded in HdaController.Capture/Restore -------------------

    private const int StreamPayloadLength =
        sizeof(byte) * 4 // ControlByte0, ControlByte1, ControlByte2, Status
        + sizeof(uint) // Lpib
        + sizeof(uint) // Cbl
        + sizeof(ushort) // Lvi
        + sizeof(ushort) // Fmt
        + sizeof(uint) // Bdpl
        + sizeof(uint) // Bdpu
        + sizeof(int) // BdlEntryIndex
        + sizeof(uint) // BytesConsumedInEntry
        + sizeof(double); // SampleAccumulator

    private const int StreamsPayloadLength =
        sizeof(uint) * 2 // DPLBASE, DPUBASE
        + sizeof(int) // the host mix's own bound stream index
        + sizeof(double) // the host mix's own resample accumulator
        + StreamPayloadLength * StreamDescriptorCount;

    private void CaptureStreams(BinaryWriter writer)
    {
        writer.Write(_dplBase);
        writer.Write(_dpuBase);
        writer.Write(_hostMixBoundStreamIndex);
        writer.Write(_hostMixResampleAccumulator);

        foreach (StreamDescriptor stream in _streams)
        {
            writer.Write(stream.ControlByte0);
            writer.Write(stream.ControlByte1);
            writer.Write(stream.ControlByte2);
            writer.Write(stream.Status);
            writer.Write(stream.Lpib);
            writer.Write(stream.Cbl);
            writer.Write(stream.Lvi);
            writer.Write(stream.Fmt);
            writer.Write(stream.Bdpl);
            writer.Write(stream.Bdpu);
            writer.Write(stream.BdlEntryIndex);
            writer.Write(stream.BytesConsumedInEntry);
            writer.Write(stream.SampleAccumulator);
        }
    }

    /// <summary>Restores what <see cref="CaptureStreams"/> wrote. Every stream's own cached BDL entries are host-side derived state, not captured: <see cref="StreamDescriptor.BdlLoaded"/> is left false so the next <see cref="AdvanceStream"/> rebuilds them from the restored CBL/LVI/BDPL/BDPU and guest RAM, which a full machine snapshot restores separately.</summary>
    private void RestoreStreams(BinaryReader reader)
    {
        _dplBase = reader.ReadUInt32();
        _dpuBase = reader.ReadUInt32();
        _hostMixBoundStreamIndex = reader.ReadInt32();
        _hostMixResampleAccumulator = reader.ReadDouble();

        foreach (StreamDescriptor stream in _streams)
        {
            stream.ControlByte0 = reader.ReadByte();
            stream.ControlByte1 = reader.ReadByte();
            stream.ControlByte2 = reader.ReadByte();
            stream.Status = reader.ReadByte();
            stream.Lpib = reader.ReadUInt32();
            stream.Cbl = reader.ReadUInt32();
            stream.Lvi = reader.ReadUInt16();
            stream.Fmt = reader.ReadUInt16();
            stream.Bdpl = reader.ReadUInt32();
            stream.Bdpu = reader.ReadUInt32();
            stream.BdlEntryIndex = reader.ReadInt32();
            stream.BytesConsumedInEntry = reader.ReadUInt32();
            stream.SampleAccumulator = reader.ReadDouble();
            stream.BdlLoaded = false;
            stream.BdlEntries = [];
        }

        Array.Clear(_mixRing);
        _mixRingWrite = 0;
        _mixRingRead = 0;
        _mixRingCount = 0;
    }
}
