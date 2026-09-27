using Aurora386.Core.Devices.Pci;
using Aurora386.Core.IO;

namespace Aurora386.Core.Devices.Audio;

/// <summary>
/// Ensoniq's 1997 AudioPCI ES1371: a small bus-mastering DMA engine (two
/// playback channels, DAC1 and DAC2, and one recording channel, ADC) sitting
/// in front of a generic <see cref="Ac97Codec"/>, the PCI sound card an
/// in-box Windows 98/2000/XP driver already knows. See
/// <c>plans/specs/es1371.md</c> for the register map this implements and the
/// coordinator's decisions (E1-E7) that override its uncertain points; see
/// <c>docs/ES1371.md</c> for the model's own summary.
///
/// <b>Two clocks, three channels.</b> DAC1 keeps the ES1370 chip's own fixed
/// four-rate clock (5512/11025/22050/44100 Hz, selected from
/// <see cref="_control"/>); DAC2 and the ADC are driven by a small on-chip
/// sample-rate converter whose 128-word RAM a driver programs indirectly
/// through the SRC port (offset 10h) -- see <see cref="DecodeConverterRate"/>
/// for the coordinator's decode formula (E4). Each channel walks its own
/// ring in guest RAM (FRAME base, SIZE length in 32-bit words) and raises its
/// own interrupt every time an independent period counter (COUNT, in sample
/// frames) reaches zero -- the ring's own geometry and the interrupt period
/// are deliberately separate, exactly as the coordinator's units decision
/// (E3) describes, which is what lets a driver ask for a half-buffer
/// interrupt by setting COUNT to half of SIZE.
///
/// <b>Interrupt acknowledgement (E2).</b> Unlike the RTL8139's ISR, nothing
/// here is write-1-to-clear: a channel's STATUS bit clears the instant its
/// own SERIAL interrupt-enable bit goes from 1 to 0, which is why the SERIAL
/// register's own <see cref="WriteSerial"/> path is where acknowledgement
/// lives, not a dedicated STATUS write.
///
/// <b>Audio path (E6).</b> DAC1 and DAC2 are two independent producers
/// resampled (by simple repetition, the same technique
/// <see cref="SoundBlaster16"/> already uses for its own DSP-to-mixer rate
/// conversion) into two small rings at this class's own fixed
/// <see cref="MixSampleRate"/>, then summed and clipped once in
/// <see cref="ReadSamples"/> -- an enabled channel with nothing yet rendered
/// never blocks the other's delivery, and a disabled one contributes nothing
/// at all. The ADC has no host capture (E6): it stores silence into its own
/// ring in guest RAM, at the programmed rate, raising its own interrupt
/// exactly as a real recording would.
/// </summary>
public sealed class Es1371 : IIoDwordDevice, IAudioSource
{
    // --- identity ------------------------------------------------------------

    private const ushort VendorId = 0x1274; // Ensoniq
    private const ushort DeviceId = 0x1371; // ES1371/ES1373 AudioPCI
    private const uint ClassCode = 0x040100; // multimedia audio device

    /// <summary>
    /// The earliest ES1371 stepping named in <c>plans/specs/es1371.md</c>
    /// section 1's revision table (02h/03h ES1371, as opposed to ES1373's
    /// 04h/06h, CT5880's 07h or the "B" step's 09h) -- picked because this
    /// model is a plain ES1371, not any of those later variants, and no test
    /// or driver behaviour this project has run depends on the exact byte.
    /// </summary>
    private const uint Revision = 0x02;

    private const uint CommandWritableMask = 0x5; // I/O space (bit 0) + bus master (bit 2); there is no memory BAR

    // --- BAR0 geometry ---------------------------------------------------------

    /// <summary>The real chip's register file: 64 bytes, no memory BAR (spec section 1).</summary>
    private const int IoWindowLength = 0x40;

    /// <summary>Full 32 address bits kept significant, exactly as <see cref="Cards.Rtl8139Card"/>'s own BAR0 keeps them: a base above the 64 KB port space must refuse to decode, not alias down into it.</summary>
    private const uint IoBarAddressMask = 0xFFFFFFC0u;

    // --- register offsets, byte 0 of each field -------------------------------

    private const int RegControl = 0x00;
    private const int RegStatus = 0x04; // read-only, computed
    private const int RegUart = 0x08; // dumb UART, no intelligent mode: plain storage (out of scope, spec section 6)
    private const int RegMemPage = 0x0C;
    private const int RegSrc = 0x10;
    private const int RegCodec = 0x14;
    private const int RegLegacy = 0x18; // SB/game-port bridging scratch (out of scope, spec section 6)
    private const int RegSpdif = 0x1C; // ES1373 channel status (out of scope, spec section 6)
    private const int RegSerial = 0x20;
    private const int RegDac1Count = 0x24;
    private const int RegDac2Count = 0x28;
    private const int RegAdcCount = 0x2C;
    private const int RegPagedFrame = 0x30; // DAC1 or ADC, selected by RegMemPage's low nibble
    private const int RegPagedSize = 0x34;
    private const int RegDac2Frame = 0x38; // DAC2 alone is not paged
    private const int RegDac2Size = 0x3C;

    // --- CONTROL (00h) ---------------------------------------------------------

    private const uint ControlAdcStopBit = 1u << 31;
    private const uint ControlDac1EnableBit = 1u << 6;
    private const uint ControlDac2EnableBit = 1u << 5;
    private const uint ControlAdcEnableBit = 1u << 4;

    /// <summary>
    /// DAC1's own fixed-rate clock select. The ES1371 datasheet's PDF text
    /// layer would not extract (<c>plans/specs/es1371.md</c>'s own sources
    /// note), so this bit position is this project's own choice from
    /// CONTROL's otherwise-undefined bits rather than an independently
    /// verified one; the four rates themselves are the well-documented
    /// ES1370 legacy set the spec's body names.
    /// </summary>
    private const int ControlDac1RateShift = 8;

    private const uint ControlDac1RateMask = 0x3u << ControlDac1RateShift;
    private static readonly int[] Dac1FixedRates = [5512, 11025, 22050, 44100];

    // --- STATUS (04h), computed from per-channel pending bits -----------------

    private const uint StatusIntrBit = 1u << 31;
    private const uint StatusUartBit = 1u << 3; // never set: the UART is out of scope
    private const uint StatusDac1Bit = 1u << 2;
    private const uint StatusDac2Bit = 1u << 1;
    private const uint StatusAdcBit = 1u << 0;

    // --- MEMPAGE (0Ch) -----------------------------------------------------

    private const uint MemPagePageMask = 0x0F;
    private const uint MemPageAdcFramePage = 0x0D;

    // --- SRC (10h) -----------------------------------------------------------

    private const uint SrcAddressMask = 0xFE000000u;
    private const int SrcAddressShift = 25;
    private const uint SrcWriteEnableBit = 1u << 24;
    private const uint SrcBusyBit = 1u << 23; // read-only, always 0 (E4)
    private const uint SrcDataMask = 0xFFFFu;

    /// <summary>128 addressable words: rate control and volume for each of the three channels (spec section 3).</summary>
    private const int SrcRamLength = 128;

    private const int SrcDac2RateBase = 0x74;
    private const int SrcAdcRateBase = 0x78;
    private const int SrcIntRegsOffset = 1; // base + 1
    private const int SrcVfreqFracOffset = 3; // base + 3
    private const uint SrcIntRegsFrequencyMask = 0xFC00;

    // --- CODEC (14h) -----------------------------------------------------------

    private const uint CodecReadyBit = 1u << 31; // read-only, always 1 (E5)
    private const uint CodecPirdBit = 1u << 23;
    private const uint CodecRegisterMask = 0x7Fu << 16;
    private const int CodecRegisterShift = 16;
    private const uint CodecDataMask = 0xFFFFu;

    // --- SERIAL (20h), coordinator decision E1 --------------------------------

    private const int SerialP1ModeShift = 0;
    private const int SerialP2ModeShift = 2;
    private const int SerialR1ModeShift = 4;
    private const uint SerialModeMask = 0x3;
    private const uint SerialP1IntEnBit = 1u << 8;
    private const uint SerialP2IntEnBit = 1u << 9;
    private const uint SerialR1IntEnBit = 1u << 10;
    private const uint SerialP1PauseBit = 1u << 11;
    private const uint SerialP2PauseBit = 1u << 12;
    private const uint SerialP1LoopSelBit = 1u << 13;
    private const uint SerialP2LoopSelBit = 1u << 14;
    private const uint SerialR1LoopSelBit = 1u << 15;

    /// <summary>A 2-bit mode field's own bit 0: stereo (E1).</summary>
    private const uint ModeStereoBit = 0x1;

    /// <summary>A 2-bit mode field's own bit 1: 16-bit samples (E1).</summary>
    private const uint Mode16BitBit = 0x2;

    // --- channels --------------------------------------------------------------

    private const int ChannelDac1 = 0;
    private const int ChannelDac2 = 1;
    private const int ChannelAdc = 2;
    private const int ChannelCount = 3;

    /// <summary>The fixed rate this card hands frames to the host mix at -- see the class summary's audio path note (E6).</summary>
    private const int MixSampleRate = 48000;

    /// <summary>Half a second of stereo headroom per DAC ring, the same sizing <see cref="SoundBlaster16"/> uses for its own output ring.</summary>
    private const int MixRingCapacityFrames = MixSampleRate / 2;

    /// <summary>
    /// One DMA channel's own register-visible state (FRAME/SIZE/COUNT) plus
    /// the internal cursor a real chip would keep in silicon: which byte of
    /// the currently fetched (or, for the ADC, currently filled) 32-bit word
    /// is next, and the fractional accumulators that pace both the channel's
    /// own native rate and its resampling into the host mix.
    /// </summary>
    private sealed class DmaChannel
    {
        public uint Frame;
        public ushort SizeDwordsMinusOne;
        public ushort PositionDwords;
        public ushort CountReloadMinusOne;
        public ushort CountCurrent;
        public int ByteOffsetInDword;
        public uint CachedDword; // DAC only: the last dword fetched from guest RAM
        public bool Active = true; // cleared by a stop-mode (LOOP_SEL=1) channel once it reaches the end of its ring
        public double SampleAccumulator; // fractional native-rate frames owed, from Advance's own microseconds
        public double ResampleAccumulator; // fractional MixSampleRate frames owed, DAC channels only
    }

    private readonly PciIoBarWindow _ioWindow;
    private readonly IDmaMemory _dma;
    private readonly Action<bool> _interruptSink;
    private readonly Ac97Codec _codec = new();
    private readonly ushort[] _srcRam = new ushort[SrcRamLength];
    private readonly DmaChannel[] _channels = [new DmaChannel(), new DmaChannel(), new DmaChannel()];
    private readonly bool[] _channelStatus = new bool[ChannelCount];

    private uint _control;
    private uint _uart;
    private uint _memPage;
    private uint _srcRegister;
    private uint _codecRegisterValue;
    private uint _legacy;
    private uint _spdif;
    private uint _serial;

    // --- DAC1/DAC2 output, resampled to MixSampleRate and summed in ReadSamples ---

    private readonly short[] _dac1Ring = new short[MixRingCapacityFrames * 2];
    private int _dac1RingWrite;
    private int _dac1RingRead;
    private int _dac1RingCount;

    private readonly short[] _dac2Ring = new short[MixRingCapacityFrames * 2];
    private int _dac2RingWrite;
    private int _dac2RingRead;
    private int _dac2RingCount;

    public PciFunction Function { get; }

    /// <summary>Direct access to the embedded codec, for a test that wants to check its own register file without going through the indirect CODEC port.</summary>
    public Ac97Codec Codec => _codec;

    // --- IAudioSource ------------------------------------------------------

    string IAudioSource.Name => "ES1371";

    int IAudioSource.NativeSampleRate => MixSampleRate;

    /// <summary>
    /// Builds one ES1371 PCI function and attaches its I/O BAR.
    /// <paramref name="pci"/> must already carry bus-master DMA memory and
    /// interrupt routing, exactly as <see cref="Machine"/>'s chipset sets up
    /// before any expansion card is built.
    /// </summary>
    public Es1371(PciBus pci, IoBus io, ushort functionAddress, ushort ioAddress)
    {
        PciInterruptRouter router = pci.InterruptRouter
            ?? throw new InvalidOperationException("ES1371 requires PCI interrupt routing.");
        if (PciInterruptRouter.Link(functionAddress, 1) < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(functionAddress));
        }

        _dma = pci.DmaMemory
            ?? throw new InvalidOperationException("ES1371 requires the chipset's bus-master DMA memory.");

        Function = new PciFunction(functionAddress, VendorId, DeviceId, 0, commandMask: (ushort)CommandWritableMask);
        Function.Define(8, (ClassCode << 8) | Revision, 4); // overrides the base constructor's zeroed revision byte
        Function.Define(0x10, (ioAddress & IoBarAddressMask) | 1u, 4, IoBarAddressMask);
        Function.Define(0x3C, 0, 1, 0xFF); // Interrupt Line bookkeeping only
        Function.Define(0x3D, 1, 1); // INTA wired to the motherboard router
        pci.Add(Function);

        _ioWindow = new PciIoBarWindow(Function, 0, IoWindowLength, io, this);
        _interruptSink = asserted => router.SetInterrupt(functionAddress, 1, asserted);

        Reset();
    }

    /// <summary>Returns the chip to its power-on state: every register zero, every channel idle, the embedded codec at its own defaults.</summary>
    public void Reset()
    {
        _control = 0;
        _uart = 0;
        _memPage = 0;
        _srcRegister = 0;
        Array.Clear(_srcRam);
        _codecRegisterValue = 0;
        _codec.Reset();
        _legacy = 0;
        _spdif = 0;
        _serial = 0;
        Array.Clear(_channelStatus);

        foreach (DmaChannel channel in _channels)
        {
            channel.Frame = 0;
            channel.SizeDwordsMinusOne = 0;
            channel.PositionDwords = 0;
            channel.CountReloadMinusOne = 0;
            channel.CountCurrent = 0;
            channel.ByteOffsetInDword = 0;
            channel.CachedDword = 0;
            channel.Active = true;
            channel.SampleAccumulator = 0;
            channel.ResampleAccumulator = 0;
        }

        Array.Clear(_dac1Ring);
        _dac1RingWrite = 0;
        _dac1RingRead = 0;
        _dac1RingCount = 0;
        Array.Clear(_dac2Ring);
        _dac2RingWrite = 0;
        _dac2RingRead = 0;
        _dac2RingCount = 0;

        UpdateInterruptLine();
    }

    /// <summary>Advances every DMA channel by a span of emulated time, pacing each at its own programmed rate. The caller drives this from the same device tick every other card uses.</summary>
    public void Advance(double microseconds)
    {
        AdvanceDac1(microseconds);
        AdvanceDac2(microseconds);
        AdvanceAdc(microseconds);
    }

    // --- IIoDwordDevice: the I/O BAR -----------------------------------------

    public byte ReadByte(ushort port)
    {
        int offset = IoOffset(port);
        if (offset < 0)
        {
            return 0xFF;
        }

        uint whole = ReadRegisterDword(offset & ~3);
        return (byte)(whole >> (8 * (offset & 3)));
    }

    public void WriteByte(ushort port, byte value)
    {
        int offset = IoOffset(port);
        if (offset < 0)
        {
            return;
        }

        // Every register here answers a full dword's worth of side effects
        // even for a byte-granular access: a driver's own single-byte OUT
        // still has to see the same effect a dword OUT would have, merged
        // with whatever the other three bytes already held.
        int aligned = offset & ~3;
        int lane = offset & 3;
        uint whole = ReadRegisterDword(aligned);
        whole = (whole & ~(0xFFu << (8 * lane))) | ((uint)value << (8 * lane));
        WriteRegisterDword(aligned, whole);
    }

    public uint ReadDword(ushort port)
    {
        int offset = IoOffset(port);
        return offset < 0 ? uint.MaxValue : ReadRegisterDword(offset & ~3);
    }

    public void WriteDword(ushort port, uint value)
    {
        int offset = IoOffset(port);
        if (offset >= 0)
        {
            WriteRegisterDword(offset & ~3, value);
        }
    }

    /// <summary>The port relative to the current BAR0 base, or -1 while I/O decode is off or out of range.</summary>
    private int IoOffset(ushort port)
    {
        if (_ioWindow.MappedBase is not { } mapped)
        {
            return -1;
        }

        int offset = port - mapped;
        return offset is >= 0 and < IoWindowLength ? offset : -1;
    }

    // --- register dispatch, one aligned dword at a time -----------------------

    private uint ReadRegisterDword(int offset) => offset switch
    {
        RegControl => _control,
        RegStatus => ReadStatus(),
        RegUart => _uart,
        RegMemPage => _memPage,
        RegSrc => _srcRegister & ~SrcBusyBit,
        RegCodec => CodecReadyBit | _codecRegisterValue,
        RegLegacy => _legacy,
        RegSpdif => _spdif,
        RegSerial => _serial,
        RegDac1Count => ReadCount(ChannelDac1),
        RegDac2Count => ReadCount(ChannelDac2),
        RegAdcCount => ReadCount(ChannelAdc),
        RegPagedFrame => _channels[PagedChannelIndex].Frame,
        RegPagedSize => ReadSize(PagedChannelIndex),
        RegDac2Frame => _channels[ChannelDac2].Frame,
        RegDac2Size => ReadSize(ChannelDac2),
        _ => 0xFFFFFFFFu, // past the real 64-byte decode: an empty bus
    };

    private void WriteRegisterDword(int offset, uint value)
    {
        switch (offset)
        {
            case RegControl:
                WriteControl(value);
                break;
            case RegStatus:
                break; // read-only
            case RegUart:
                _uart = value;
                break;
            case RegMemPage:
                _memPage = value;
                break;
            case RegSrc:
                WriteSrc(value);
                break;
            case RegCodec:
                WriteCodec(value);
                break;
            case RegLegacy:
                _legacy = value;
                break;
            case RegSpdif:
                _spdif = value;
                break;
            case RegSerial:
                WriteSerial(value);
                break;
            case RegDac1Count:
                WriteCount(ChannelDac1, value);
                break;
            case RegDac2Count:
                WriteCount(ChannelDac2, value);
                break;
            case RegAdcCount:
                WriteCount(ChannelAdc, value);
                break;
            case RegPagedFrame:
                _channels[PagedChannelIndex].Frame = value;
                break;
            case RegPagedSize:
                WriteSize(PagedChannelIndex, value);
                break;
            case RegDac2Frame:
                _channels[ChannelDac2].Frame = value;
                break;
            case RegDac2Size:
                WriteSize(ChannelDac2, value);
                break;
        }
    }

    /// <summary>Which channel offsets 30h/34h currently reach: DAC1's own frame, or the ADC's, selected by MEMPAGE's low nibble (spec section 2).</summary>
    private int PagedChannelIndex => (_memPage & MemPagePageMask) == MemPageAdcFramePage ? ChannelAdc : ChannelDac1;

    private static uint ReadCount(DmaChannel channel) => ((uint)channel.CountCurrent << 16) | channel.CountReloadMinusOne;

    private uint ReadCount(int channelIndex) => ReadCount(_channels[channelIndex]);

    /// <summary>
    /// A write only ever supplies the low 16 bits (E3's "reload" half); the
    /// high 16 bits are read-only, computed from the channel's own countdown.
    /// The new reload also arms the countdown immediately, so a channel
    /// programmed and then started (CONTROL's own enable bit, see
    /// <see cref="WriteControl"/>) begins counting down a full period rather
    /// than firing on its very first frame.
    /// </summary>
    private void WriteCount(int channelIndex, uint value)
    {
        DmaChannel channel = _channels[channelIndex];
        channel.CountReloadMinusOne = (ushort)value;
        channel.CountCurrent = channel.CountReloadMinusOne;
    }

    private uint ReadSize(int channelIndex)
    {
        DmaChannel channel = _channels[channelIndex];
        return ((uint)channel.PositionDwords << 16) | channel.SizeDwordsMinusOne;
    }

    private void WriteSize(int channelIndex, uint value)
    {
        DmaChannel channel = _channels[channelIndex];
        channel.SizeDwordsMinusOne = (ushort)value;
        channel.PositionDwords = (ushort)(value >> 16);
    }

    private uint ReadStatus()
    {
        uint status = 0;
        if (_channelStatus[ChannelDac1])
        {
            status |= StatusDac1Bit;
        }

        if (_channelStatus[ChannelDac2])
        {
            status |= StatusDac2Bit;
        }

        if (_channelStatus[ChannelAdc])
        {
            status |= StatusAdcBit;
        }

        if (status != 0)
        {
            status |= StatusIntrBit;
        }

        return status;
    }

    /// <summary>A channel's own CONTROL enable bit just went from off to on: arm it for a fresh run, as a driver toggling it means to start one.</summary>
    private void WriteControl(uint value)
    {
        uint previous = _control;
        _control = value;

        if (BitJustSet(previous, value, ControlDac1EnableBit))
        {
            ArmChannel(ChannelDac1);
        }

        if (BitJustSet(previous, value, ControlDac2EnableBit))
        {
            ArmChannel(ChannelDac2);
        }

        if (BitJustSet(previous, value, ControlAdcEnableBit))
        {
            ArmChannel(ChannelAdc);
        }
    }

    private static bool BitJustSet(uint previous, uint next, uint bit) => (previous & bit) == 0 && (next & bit) != 0;

    private static bool BitJustCleared(uint previous, uint next, uint bit) => (previous & bit) != 0 && (next & bit) == 0;

    private void ArmChannel(int channelIndex)
    {
        DmaChannel channel = _channels[channelIndex];
        channel.Active = true;
        channel.ByteOffsetInDword = 0;
        channel.SampleAccumulator = 0;
    }

    /// <summary>The SRC RAM read/write protocol (spec section 3): a write with WE set stores; a write with WE clear stages a read, available at once since BUSY always reads 0 (E4).</summary>
    private void WriteSrc(uint value)
    {
        int address = (int)((value & SrcAddressMask) >> SrcAddressShift);
        bool writeEnable = (value & SrcWriteEnableBit) != 0;
        ushort data = (ushort)(value & SrcDataMask);

        if (writeEnable)
        {
            _srcRam[address] = data;
        }
        else
        {
            data = _srcRam[address];
        }

        _srcRegister = (value & ~SrcDataMask) | data;
    }

    /// <summary>The indirect CODEC port protocol (spec section 4): PIRD set stages a read from the embedded codec, otherwise the write goes straight to it. RDY always reads 1 and WIP always reads 0 (E5) -- there is no wait to model.</summary>
    private void WriteCodec(uint value)
    {
        byte registerOffset = (byte)((value & CodecRegisterMask) >> CodecRegisterShift);
        ushort data = (ushort)(value & CodecDataMask);

        if ((value & CodecPirdBit) != 0)
        {
            data = _codec.ReadRegister(registerOffset);
        }
        else
        {
            _codec.WriteRegister(registerOffset, data);
        }

        _codecRegisterValue = ((uint)registerOffset << CodecRegisterShift) | data;
    }

    /// <summary>E2: acknowledgement lives here, not on STATUS itself -- a channel's pending bit clears the instant its own interrupt-enable bit goes from 1 to 0.</summary>
    private void WriteSerial(uint value)
    {
        uint previous = _serial;
        _serial = value;

        if (BitJustCleared(previous, value, SerialP1IntEnBit))
        {
            ClearChannelStatus(ChannelDac1);
        }

        if (BitJustCleared(previous, value, SerialP2IntEnBit))
        {
            ClearChannelStatus(ChannelDac2);
        }

        if (BitJustCleared(previous, value, SerialR1IntEnBit))
        {
            ClearChannelStatus(ChannelAdc);
        }
    }

    private void ClearChannelStatus(int channelIndex)
    {
        if (!_channelStatus[channelIndex])
        {
            return;
        }

        _channelStatus[channelIndex] = false;
        UpdateInterruptLine();
    }

    private void RaiseChannelStatus(int channelIndex, bool interruptEnabled)
    {
        if (!interruptEnabled)
        {
            return;
        }

        _channelStatus[channelIndex] = true;
        UpdateInterruptLine();
    }

    private void UpdateInterruptLine()
    {
        bool asserted = _channelStatus[ChannelDac1] || _channelStatus[ChannelDac2] || _channelStatus[ChannelAdc];
        _interruptSink(asserted);
    }

    // --- DAC1 --------------------------------------------------------------

    private void AdvanceDac1(double microseconds)
    {
        DmaChannel channel = _channels[ChannelDac1];
        bool enabled = (_control & ControlDac1EnableBit) != 0;
        bool paused = (_serial & SerialP1PauseBit) != 0;

        if (!enabled || paused || !Function.BusMasterEnabled || !channel.Active)
        {
            return;
        }

        int rate = Dac1FixedRates[(_control & ControlDac1RateMask) >> ControlDac1RateShift];
        uint modeBits = (_serial >> SerialP1ModeShift) & SerialModeMask;
        bool loopSel = (_serial & SerialP1LoopSelBit) != 0;
        bool intEnabled = (_serial & SerialP1IntEnBit) != 0;
        int bytesPerFrame = BytesPerFrame(modeBits);

        channel.SampleAccumulator += microseconds * rate / 1_000_000.0;
        int wanted = (int)channel.SampleAccumulator;
        channel.SampleAccumulator -= wanted;

        for (int i = 0; i < wanted && channel.Active; i++)
        {
            (short left, short right) = FetchDacSamples(channel, modeBits);
            PushDac1Sample(rate, left, right);
            AdvanceFrameCursor(channel, bytesPerFrame, loopSel);
            ConsumeChannelFrame(channel, ChannelDac1, intEnabled);
        }
    }

    // --- DAC2 --------------------------------------------------------------

    private void AdvanceDac2(double microseconds)
    {
        DmaChannel channel = _channels[ChannelDac2];
        bool enabled = (_control & ControlDac2EnableBit) != 0;
        bool paused = (_serial & SerialP2PauseBit) != 0;

        if (!enabled || paused || !Function.BusMasterEnabled || !channel.Active)
        {
            return;
        }

        int rate = DecodeConverterRate(SrcDac2RateBase);
        uint modeBits = (_serial >> SerialP2ModeShift) & SerialModeMask;
        bool loopSel = (_serial & SerialP2LoopSelBit) != 0;
        bool intEnabled = (_serial & SerialP2IntEnBit) != 0;
        int bytesPerFrame = BytesPerFrame(modeBits);

        channel.SampleAccumulator += microseconds * rate / 1_000_000.0;
        int wanted = (int)channel.SampleAccumulator;
        channel.SampleAccumulator -= wanted;

        for (int i = 0; i < wanted && channel.Active; i++)
        {
            (short left, short right) = FetchDacSamples(channel, modeBits);
            PushDac2Sample(rate, left, right);
            AdvanceFrameCursor(channel, bytesPerFrame, loopSel);
            ConsumeChannelFrame(channel, ChannelDac2, intEnabled);
        }
    }

    // --- ADC -----------------------------------------------------------------

    private void AdvanceAdc(double microseconds)
    {
        DmaChannel channel = _channels[ChannelAdc];
        bool enabled = (_control & ControlAdcEnableBit) != 0 && (_control & ControlAdcStopBit) == 0;

        if (!enabled || !Function.BusMasterEnabled || !channel.Active)
        {
            return;
        }

        int rate = DecodeConverterRate(SrcAdcRateBase);
        uint modeBits = (_serial >> SerialR1ModeShift) & SerialModeMask;
        bool loopSel = (_serial & SerialR1LoopSelBit) != 0;
        bool intEnabled = (_serial & SerialR1IntEnBit) != 0;
        int bytesPerFrame = BytesPerFrame(modeBits);

        channel.SampleAccumulator += microseconds * rate / 1_000_000.0;
        int wanted = (int)channel.SampleAccumulator;
        channel.SampleAccumulator -= wanted;

        for (int i = 0; i < wanted && channel.Active; i++)
        {
            StoreAdcSilence(channel, modeBits, bytesPerFrame);
            AdvanceFrameCursor(channel, bytesPerFrame, loopSel);
            ConsumeChannelFrame(channel, ChannelAdc, intEnabled);
        }
    }

    // --- shared DMA mechanics --------------------------------------------------

    /// <summary>Bytes one sample frame occupies, from a SERIAL mode field's own two bits (E1): bit 0 stereo, bit 1 16-bit.</summary>
    private static int BytesPerFrame(uint modeBits) => modeBits switch
    {
        0 => 1, // 8-bit mono
        ModeStereoBit => 2, // 8-bit stereo
        Mode16BitBit => 2, // 16-bit mono
        _ => 4, // 16-bit stereo
    };

    private static short ConvertUnsigned8(byte value) => (short)((value - 128) << 8);

    /// <summary>Fetches (caching a fresh dword only when a previous one has been fully consumed) and decodes one sample frame from DAC1 or DAC2's own ring.</summary>
    private (short Left, short Right) FetchDacSamples(DmaChannel channel, uint modeBits)
    {
        if (channel.ByteOffsetInDword == 0)
        {
            channel.CachedDword = _dma.ReadDword(channel.Frame + (uint)channel.PositionDwords * 4);
        }

        int shift = channel.ByteOffsetInDword * 8;
        bool stereo = (modeBits & ModeStereoBit) != 0;
        bool sixteenBit = (modeBits & Mode16BitBit) != 0;

        if (sixteenBit)
        {
            short left = (short)(channel.CachedDword >> shift);
            short right = stereo ? (short)(channel.CachedDword >> (shift + 16)) : left;
            return (left, right);
        }

        short leftSample = ConvertUnsigned8((byte)(channel.CachedDword >> shift));
        short rightSample = stereo ? ConvertUnsigned8((byte)(channel.CachedDword >> (shift + 8))) : leftSample;
        return (leftSample, rightSample);
    }

    /// <summary>Writes one frame of silence into the ADC's own ring -- there is no host capture (E6).</summary>
    private void StoreAdcSilence(DmaChannel channel, uint modeBits, int bytesPerFrame)
    {
        bool sixteenBit = (modeBits & Mode16BitBit) != 0;
        byte fill = sixteenBit ? (byte)0x00 : (byte)0x80; // 16-bit silence is 0; unsigned 8-bit silence is the unipolar midpoint

        uint address = channel.Frame + (uint)channel.PositionDwords * 4 + (uint)channel.ByteOffsetInDword;
        for (int i = 0; i < bytesPerFrame; i++)
        {
            _dma.WriteByte(address + (uint)i, fill);
        }
    }

    /// <summary>Advances the DMA cursor by one frame's worth of bytes, wrapping (loop mode) or halting (stop mode, E1's LOOP_SEL=1) at the ring's own end.</summary>
    private static void AdvanceFrameCursor(DmaChannel channel, int bytesPerFrame, bool loopSel)
    {
        channel.ByteOffsetInDword += bytesPerFrame;
        if (channel.ByteOffsetInDword < 4)
        {
            return;
        }

        channel.ByteOffsetInDword = 0;

        if (channel.PositionDwords >= channel.SizeDwordsMinusOne)
        {
            if (loopSel)
            {
                channel.Active = false;
            }
            else
            {
                channel.PositionDwords = 0;
            }

            return;
        }

        channel.PositionDwords++;
    }

    /// <summary>One sample frame's worth of the channel's own period counter (E3): reaching zero raises the channel's STATUS bit (gated on its own interrupt-enable bit) and reloads for the next period.</summary>
    private void ConsumeChannelFrame(DmaChannel channel, int channelIndex, bool intEnabled)
    {
        if (channel.CountCurrent == 0)
        {
            RaiseChannelStatus(channelIndex, intEnabled);
            channel.CountCurrent = channel.CountReloadMinusOne;
            return;
        }

        channel.CountCurrent--;
    }

    /// <summary>The coordinator's SRC decode formula (E4), for DAC2 (base 74h) and the ADC (base 78h). DAC1 never calls this: it uses CONTROL's own fixed-rate field.</summary>
    private int DecodeConverterRate(int rateBase)
    {
        ushort intRegs = _srcRam[rateBase + SrcIntRegsOffset];
        ushort vfreqFrac = _srcRam[rateBase + SrcVfreqFracOffset];
        uint freq = ((uint)(intRegs & SrcIntRegsFrequencyMask) << 5) | vfreqFrac;
        int rate = (int)Math.Round(freq * 3000.0 / 65536.0);

        // A never-programmed converter (freq = 0, at reset) must not stall
        // Advance's own accumulator arithmetic on a division-adjacent zero.
        return rate > 0 ? rate : 1;
    }

    // --- DAC1/DAC2 output rings, resampled by repetition like SoundBlaster16's own DSP path ---

    private void PushDac1Sample(int nativeRate, short left, short right)
    {
        DmaChannel channel = _channels[ChannelDac1];
        channel.ResampleAccumulator += (double)MixSampleRate / nativeRate;
        int repeats = (int)channel.ResampleAccumulator;
        channel.ResampleAccumulator -= repeats;

        for (int i = 0; i < repeats; i++)
        {
            EnqueueDac1(left, right);
        }
    }

    private void PushDac2Sample(int nativeRate, short left, short right)
    {
        DmaChannel channel = _channels[ChannelDac2];
        channel.ResampleAccumulator += (double)MixSampleRate / nativeRate;
        int repeats = (int)channel.ResampleAccumulator;
        channel.ResampleAccumulator -= repeats;

        for (int i = 0; i < repeats; i++)
        {
            EnqueueDac2(left, right);
        }
    }

    private void EnqueueDac1(short left, short right)
    {
        if (_dac1RingCount + 2 > _dac1Ring.Length)
        {
            _dac1RingRead = (_dac1RingRead + 2) % _dac1Ring.Length;
            _dac1RingCount -= 2;
        }

        _dac1Ring[_dac1RingWrite] = left;
        _dac1RingWrite = (_dac1RingWrite + 1) % _dac1Ring.Length;
        _dac1Ring[_dac1RingWrite] = right;
        _dac1RingWrite = (_dac1RingWrite + 1) % _dac1Ring.Length;
        _dac1RingCount += 2;
    }

    private void EnqueueDac2(short left, short right)
    {
        if (_dac2RingCount + 2 > _dac2Ring.Length)
        {
            _dac2RingRead = (_dac2RingRead + 2) % _dac2Ring.Length;
            _dac2RingCount -= 2;
        }

        _dac2Ring[_dac2RingWrite] = left;
        _dac2RingWrite = (_dac2RingWrite + 1) % _dac2Ring.Length;
        _dac2Ring[_dac2RingWrite] = right;
        _dac2RingWrite = (_dac2RingWrite + 1) % _dac2Ring.Length;
        _dac2RingCount += 2;
    }

    /// <summary>
    /// Sums DAC1 and DAC2's own rings into <paramref name="destination"/>,
    /// clipping once at the end (the same reasoning
    /// <see cref="AudioMixer"/>'s own summary gives for its cross-source
    /// mix). A disabled channel contributes silence without ever limiting
    /// how many frames the other one delivers; an enabled channel that has
    /// not yet rendered enough frames does limit it, exactly the underrun
    /// policy <see cref="AudioMixer"/> itself applies to every source.
    /// </summary>
    public int ReadSamples(Span<short> destination)
    {
        int frameCount = destination.Length / 2;
        bool dac1Enabled = (_control & ControlDac1EnableBit) != 0;
        bool dac2Enabled = (_control & ControlDac2EnableBit) != 0;

        if (dac1Enabled)
        {
            frameCount = Math.Min(frameCount, _dac1RingCount / 2);
        }

        if (dac2Enabled)
        {
            frameCount = Math.Min(frameCount, _dac2RingCount / 2);
        }

        for (int frame = 0; frame < frameCount; frame++)
        {
            int left = 0;
            int right = 0;

            if (dac1Enabled)
            {
                left += _dac1Ring[_dac1RingRead];
                right += _dac1Ring[(_dac1RingRead + 1) % _dac1Ring.Length];
                _dac1RingRead = (_dac1RingRead + 2) % _dac1Ring.Length;
                _dac1RingCount -= 2;
            }

            if (dac2Enabled)
            {
                left += _dac2Ring[_dac2RingRead];
                right += _dac2Ring[(_dac2RingRead + 1) % _dac2Ring.Length];
                _dac2RingRead = (_dac2RingRead + 2) % _dac2Ring.Length;
                _dac2RingCount -= 2;
            }

            destination[frame * 2] = (short)Math.Clamp(left, short.MinValue, short.MaxValue);
            destination[frame * 2 + 1] = (short)Math.Clamp(right, short.MinValue, short.MaxValue);
        }

        return frameCount * 2;
    }

    // --- snapshot --------------------------------------------------------------

    /// <summary>The version of the payload <see cref="Capture"/> produces today.</summary>
    public const int SnapshotVersion = 1;

    private const int ChannelPayloadLength =
        sizeof(uint) // Frame
        + sizeof(ushort) * 4 // SizeDwordsMinusOne, PositionDwords, CountReloadMinusOne, CountCurrent
        + sizeof(int) // ByteOffsetInDword
        + sizeof(uint) // CachedDword
        + sizeof(bool) // Active
        + sizeof(double) * 2; // SampleAccumulator, ResampleAccumulator

    /// <summary>
    /// The fixed size of a version-1 payload: CONTROL, MEMPAGE, the UART,
    /// SERIAL, LEGACY and SPDIF scratch, the SRC port and its 128-word RAM,
    /// the indirect CODEC port's own last value, the embedded AC'97
    /// register file, the three channels' own FRAME/SIZE/COUNT and internal
    /// cursors, and the three channels' pending STATUS bits -- everything a
    /// driver could read back and notice was wrong. PCI configuration
    /// (including the BAR) is captured separately by <see cref="Pci.PciBus"/>;
    /// the DAC1/DAC2 output rings are host presentation state, not captured,
    /// exactly as <see cref="SoundBlaster16"/>'s own summary explains for its
    /// equivalent ring.
    /// </summary>
    public const int SnapshotLength =
        sizeof(uint) // CONTROL
        + sizeof(byte) // STATUS: the three pending bits, packed
        + sizeof(uint) // MEMPAGE
        + sizeof(uint) // UART
        + sizeof(uint) // SERIAL
        + sizeof(uint) // LEGACY
        + sizeof(uint) // SPDIF
        + sizeof(uint) // SRC port
        + sizeof(ushort) * SrcRamLength // SRC RAM
        + sizeof(uint) // CODEC port's own last value
        + sizeof(ushort) * Ac97Codec.RegisterCount // AC'97 register file
        + ChannelPayloadLength * ChannelCount;

    public byte[] Capture()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(_control);
        byte statusBits = 0;
        if (_channelStatus[ChannelDac1])
        {
            statusBits |= 0x04;
        }

        if (_channelStatus[ChannelDac2])
        {
            statusBits |= 0x02;
        }

        if (_channelStatus[ChannelAdc])
        {
            statusBits |= 0x01;
        }

        writer.Write(statusBits);
        writer.Write(_memPage);
        writer.Write(_uart);
        writer.Write(_serial);
        writer.Write(_legacy);
        writer.Write(_spdif);
        writer.Write(_srcRegister);
        foreach (ushort word in _srcRam)
        {
            writer.Write(word);
        }

        writer.Write(_codecRegisterValue);
        _codec.Capture(writer);

        foreach (DmaChannel channel in _channels)
        {
            writer.Write(channel.Frame);
            writer.Write(channel.SizeDwordsMinusOne);
            writer.Write(channel.PositionDwords);
            writer.Write(channel.CountReloadMinusOne);
            writer.Write(channel.CountCurrent);
            writer.Write(channel.ByteOffsetInDword);
            writer.Write(channel.CachedDword);
            writer.Write(channel.Active);
            writer.Write(channel.SampleAccumulator);
            writer.Write(channel.ResampleAccumulator);
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Restores what <see cref="Capture"/> wrote. <see cref="Cards.CardCatalog.NewestSnapshotVersions"/> and <see cref="Cards.CardCatalog.FixedSnapshotLengths"/> already reject a wrong version or a malformed length before this runs.</summary>
    public void Restore(byte[] payload, int version)
    {
        if (version != SnapshotVersion || payload.Length != SnapshotLength)
        {
            throw new InvalidDataException("Invalid ES1371 snapshot envelope.");
        }

        using var reader = new BinaryReader(new MemoryStream(payload));

        _control = reader.ReadUInt32();
        byte statusBits = reader.ReadByte();
        _channelStatus[ChannelDac1] = (statusBits & 0x04) != 0;
        _channelStatus[ChannelDac2] = (statusBits & 0x02) != 0;
        _channelStatus[ChannelAdc] = (statusBits & 0x01) != 0;
        _memPage = reader.ReadUInt32();
        _uart = reader.ReadUInt32();
        _serial = reader.ReadUInt32();
        _legacy = reader.ReadUInt32();
        _spdif = reader.ReadUInt32();
        _srcRegister = reader.ReadUInt32();
        for (int i = 0; i < _srcRam.Length; i++)
        {
            _srcRam[i] = reader.ReadUInt16();
        }

        _codecRegisterValue = reader.ReadUInt32();
        _codec.Restore(reader);

        foreach (DmaChannel channel in _channels)
        {
            channel.Frame = reader.ReadUInt32();
            channel.SizeDwordsMinusOne = reader.ReadUInt16();
            channel.PositionDwords = reader.ReadUInt16();
            channel.CountReloadMinusOne = reader.ReadUInt16();
            channel.CountCurrent = reader.ReadUInt16();
            channel.ByteOffsetInDword = reader.ReadInt32();
            channel.CachedDword = reader.ReadUInt32();
            channel.Active = reader.ReadBoolean();
            channel.SampleAccumulator = reader.ReadDouble();
            channel.ResampleAccumulator = reader.ReadDouble();
        }

        // The output rings are host presentation state and start empty
        // again, exactly as a freshly constructed card's do; nothing a
        // guest can observe through any register depends on them.
        Array.Clear(_dac1Ring);
        _dac1RingWrite = 0;
        _dac1RingRead = 0;
        _dac1RingCount = 0;
        Array.Clear(_dac2Ring);
        _dac2RingWrite = 0;
        _dac2RingRead = 0;
        _dac2RingCount = 0;

        UpdateInterruptLine();
    }
}
