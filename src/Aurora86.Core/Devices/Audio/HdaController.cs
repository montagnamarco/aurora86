using Aurora86.Core.Devices.Pci;
using Aurora86.Core.Memory;

namespace Aurora86.Core.Devices.Audio;

/// <summary>
/// An Intel High Definition Audio controller with the ICH9's own PCI identity
/// (8086:293Eh, revision 03h) and one <see cref="HdaCodec"/>: the audio
/// front end every in-box Windows XP-and-later HD Audio class driver and
/// every Linux <c>snd-hda-intel</c> build already knows, unlike the ES1371's
/// driver-specific AC'97 protocol.
///
/// <b>Three DMA engines a driver walks in order.</b> First the CORB/RIRB
/// command ring (this class's <c>HdaController.Corb.cs</c> partial):
/// 32-bit verbs out, 64-bit responses back, the only way software ever
/// reaches <see cref="HdaCodec"/>'s node registers, alongside the immediate
/// command registers (IC/IR/ICS) for the same protocol one verb at a time
/// without a ring. Then eight stream descriptors (<c>HdaController.Streams.cs</c>),
/// each walking its own guest-built Buffer Descriptor List; this
/// implementation's <see cref="HdaCodec"/> is output-only, so only the four
/// output descriptors (SD4-SD7, per <see cref="GcapValue"/>'s own ISS/OSS
/// split) ever carry audio a guest would notice, but all eight exist and
/// answer their registers because <see cref="Devices.Interrupts.InterruptController"/>-aware
/// driver code always probes them.
///
/// <b>The controller can be programmed only once CRST is set (GCTL bit 0).</b>
/// While clear -- the register file's own power-on state -- every register
/// but GCTL itself reads and stays at its reset value, and every write to
/// them is dropped: the real chip is held in reset and cannot be programmed
/// until software brings it out, exactly the handshake a real HD Audio BIOS
/// and driver both perform before touching CORB, RIRB or any stream.
///
/// See <c>docs/HD-AUDIO.md</c> for the register map as implemented and every
/// *choice* this class, <c>HdaController.Corb.cs</c>, <c>HdaController.Streams.cs</c>
/// and <see cref="HdaCodec"/> made where the Intel HDA specification left
/// room, with their sources.
/// </summary>
public sealed partial class HdaController : IMemoryDevice, IAudioSource
{
    // --- identity --------------------------------------------------------------

    private const ushort VendorId = 0x8086; // Intel
    private const ushort DeviceId = 0x293E; // ICH9 High Definition Audio Controller
    private const uint ClassCode = 0x040300; // multimedia audio device, HD Audio programming interface
    private const uint Revision = 0x03;

    /// <summary>Memory space (bit 1) and bus master (bit 2) are writable; there is no I/O BAR to gate with bit 0.</summary>
    private const uint CommandWritableMask = 0x6;

    /// <summary>BAR0: a 16 KB, 32-bit, non-prefetchable memory window (*choice*: the spec only requires 16 KB be reachable; a 64-bit-capable BAR would add nothing this project's guests read).</summary>
    private const uint Bar0Length = 0x4000;

    /// <summary>Documented register span within BAR0 (0h-1FFh); every other offset up to <see cref="Bar0Length"/> reads 0 and discards writes, exactly as an empty region of a real chip's decode would.</summary>
    private const int DocumentedRegisterSpan = 0x200;

    /// <summary>Config 44h, TCSEL (Traffic Class Select): bits 2:0 writable, no effect on this model (*choice*, matches the contract's own instruction).</summary>
    private const int ConfigTcselOffset = 0x44;

    private readonly PciMemoryBarWindow _memoryWindow;
    private readonly IDmaMemory _dma;
    private readonly Action<bool> _interruptSink;
    private readonly HdaCodec _codec = new();

    public PciFunction Function { get; }

    /// <summary>Direct access to the embedded codec, for a test that wants to check its own node registers without going through CORB/RIRB or the immediate command interface.</summary>
    public HdaCodec Codec => _codec;

    // --- global registers, offsets 00h-3Fh --------------------------------------

    private const int RegGcap = 0x00;
    private const int RegVmin = 0x02;
    private const int RegVmaj = 0x03;
    private const int RegOutpay = 0x04;
    private const int RegInpay = 0x06;
    private const int RegGctl = 0x08;
    private const int RegWakeen = 0x0C;
    private const int RegStatests = 0x0E;
    private const int RegGsts = 0x10;
    private const int RegIntctl = 0x20;
    private const int RegIntsts = 0x24;
    private const int RegWalclk = 0x30;
    private const int RegSsyncLegacyAlias = 0x34; // *choice*: some drivers still probe the pre-1.0a SSYNC offset
    private const int RegSsync = 0x38;

    /// <summary>4401h: 64OK (bit 0) set, 4 input streams (bits 11:8), 4 output streams (bits 15:12), no bidirectional streams.</summary>
    private const ushort GcapValue = 0x4401;

    private const byte VminValue = 0x00;
    private const byte VmajValue = 0x01;
    private const ushort OutpayValue = 0x3C;
    private const ushort InpayValue = 0x1D;

    private const uint GctlCrstBit = 1u << 0;
    private const uint GctlFcntrlBit = 1u << 1; // plain RW scratch: nothing here models the flush this bit requests
    private const uint GctlUnsolBit = 1u << 8; // plain RW scratch: unsolicited responses are out of scope

    /// <summary>Codec address 0 -- the only SDIN line this controller ever wires up -- announced in STATESTS.</summary>
    public const uint StatestsCodecPresentBit = 1u << 0;

    private const uint GstsFstsBit = 1u << 1;

    private const uint IntctlGieBit = 1u << 31;
    private const uint IntctlCieBit = 1u << 30;
    private const uint IntctlSieMask = 0xFF;

    /// <summary>24 MHz, the HD Audio link's own fixed wall-clock rate.</summary>
    private const double WallClockTicksPerMicrosecond = 24.0;

    private uint _gctl;
    private uint _wakeen;
    private uint _statests;
    private uint _gsts;
    private uint _intctl;
    private uint _ssync;
    private uint _walClock;
    private double _walClockAccumulator;

    /// <summary>Whether the register file is out of reset (GCTL bit 0). While false every register but GCTL itself reads and stays at its power-on default.</summary>
    private bool Crst => (_gctl & GctlCrstBit) != 0;

    /// <summary>
    /// Builds one HD Audio controller, attaches its memory BAR and, once
    /// <see cref="PciBus.DmaMemory"/> exists, is ready for CORB/RIRB and
    /// stream descriptors to move bytes the instant a driver sets them
    /// running. <paramref name="pci"/> must already carry bus-master DMA
    /// memory and interrupt routing, exactly what <see cref="Machine"/>'s
    /// chipset sets up before any expansion card is built.
    /// </summary>
    public HdaController(PciBus pci, PhysicalMemoryBus memory, ushort functionAddress)
    {
        PciInterruptRouter router = pci.InterruptRouter
            ?? throw new InvalidOperationException("The HD Audio controller requires PCI interrupt routing.");
        if (PciInterruptRouter.Link(functionAddress, 1) < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(functionAddress));
        }

        _dma = pci.DmaMemory
            ?? throw new InvalidOperationException("The HD Audio controller requires the chipset's bus-master DMA memory.");

        Function = new PciFunction(functionAddress, VendorId, DeviceId, 0, commandMask: (ushort)CommandWritableMask);
        Function.Define(8, (ClassCode << 8) | Revision, 4); // overrides the base constructor's zeroed revision byte
        Function.Define(0x0C, 0, 1, 0xFF); // cache line size
        Function.Define(0x0D, 0, 1, 0xFF); // latency timer
        Function.DefineBar(0, Bar0Length, io: false);
        Function.Define(ConfigTcselOffset, 0, 1, 0x07);
        Function.Define(0x3C, 0, 1, 0xFF); // Interrupt Line bookkeeping only
        Function.Define(0x3D, 1, 1); // INTA wired to the motherboard router
        pci.Add(Function);

        _memoryWindow = new PciMemoryBarWindow(Function, 0, Bar0Length, memory, this);
        _interruptSink = asserted => router.SetInterrupt(functionAddress, 1, asserted);

        InitializeStreams();
        Reset();
    }

    /// <summary>Returns the controller to its power-on state: GCTL clear (the register file held in reset, per <see cref="Crst"/>), every ring, stream and the embedded codec back to their own defaults.</summary>
    public void Reset()
    {
        _gctl = 0;
        ResetRegisters();
    }

    /// <summary>
    /// Everything GCTL's own CRST bit gates: every other global register,
    /// the command ring, every stream descriptor and the embedded codec.
    /// Run once by <see cref="Reset"/> and again every time a driver writes
    /// GCTL with CRST clear -- see <see cref="WriteGctlByte"/>.
    /// </summary>
    private void ResetRegisters()
    {
        _wakeen = 0;
        _statests = 0;
        _gsts = 0;
        _intctl = 0;
        _ssync = 0;
        _walClock = 0;
        _walClockAccumulator = 0;

        ResetCommandInterface();
        ResetStreams();
        _codec.Reset();

        UpdateInterruptLine();
    }

    /// <summary>Advances the wall clock and every stream descriptor by a span of emulated time. The caller drives this from the same device tick every other card uses.</summary>
    public void Advance(double microseconds)
    {
        if (!Crst)
        {
            return; // the controller is held in reset: nothing runs
        }

        _walClockAccumulator += microseconds * WallClockTicksPerMicrosecond;
        uint ticks = (uint)_walClockAccumulator;
        _walClockAccumulator -= ticks;
        _walClock += ticks;

        AdvanceStreams(microseconds);
    }

    // --- IMemoryDevice: the memory BAR -----------------------------------------

    public byte ReadByte(uint offset)
    {
        if (offset >= Bar0Length)
        {
            return 0; // past the real chip's own decode
        }

        int off = (int)offset;
        return off < DocumentedRegisterSpan ? ReadRegisterByte(off) : ReadExtendedRegisterByte(off);
    }

    public void WriteByte(uint offset, byte value)
    {
        if (offset >= Bar0Length)
        {
            return;
        }

        int off = (int)offset;
        if (off is >= RegGctl and < RegGctl + 4)
        {
            WriteGctlByte(off - RegGctl, value);
            return;
        }

        if (!Crst)
        {
            return; // held in reset: every register but GCTL discards writes
        }

        if (off < DocumentedRegisterSpan)
        {
            WriteRegisterByte(off, value);
        }
        else
        {
            WriteExtendedRegisterByte(off, value);
        }
    }

    /// <summary>Dispatches one byte inside the documented 0h-1FFh register span.</summary>
    private byte ReadRegisterByte(int offset)
    {
        if (offset is >= RegCommandInterfaceBase and < RegDmaPositionBase)
        {
            return ReadCommandRegisterByte(offset);
        }

        if (offset is >= RegDmaPositionBase and < RegStreamsBase)
        {
            return ReadDmaPositionByte(offset - RegDmaPositionBase);
        }

        if (offset >= RegStreamsBase)
        {
            return ReadStreamAreaByte(offset - RegStreamsBase);
        }

        return ReadGlobalRegisterByte(offset);
    }

    /// <summary>Dispatches one byte inside the documented 0h-1FFh register span. Only reached with <see cref="Crst"/> already true.</summary>
    private void WriteRegisterByte(int offset, byte value)
    {
        if (offset is >= RegCommandInterfaceBase and < RegDmaPositionBase)
        {
            WriteCommandRegisterByte(offset, value);
            return;
        }

        if (offset is >= RegDmaPositionBase and < RegStreamsBase)
        {
            WriteDmaPositionByte(offset - RegDmaPositionBase, value);
            return;
        }

        if (offset >= RegStreamsBase)
        {
            WriteStreamAreaByte(offset - RegStreamsBase, value);
            return;
        }

        WriteGlobalRegisterByte(offset, value);
    }

    private byte ReadGlobalRegisterByte(int offset) => offset switch
    {
        RegGcap => (byte)(GcapValue & 0xFF),
        RegGcap + 1 => (byte)(GcapValue >> 8),
        RegVmin => VminValue,
        RegVmaj => VmajValue,
        RegOutpay => (byte)(OutpayValue & 0xFF),
        RegOutpay + 1 => (byte)(OutpayValue >> 8),
        RegInpay => (byte)(InpayValue & 0xFF),
        RegInpay + 1 => (byte)(InpayValue >> 8),
        >= RegGctl and < RegGctl + 4 => (byte)(_gctl >> (8 * (offset - RegGctl))),
        RegWakeen => (byte)_wakeen,
        RegWakeen + 1 => (byte)(_wakeen >> 8),
        RegStatests => (byte)_statests,
        RegStatests + 1 => (byte)(_statests >> 8),
        RegGsts => (byte)_gsts,
        RegGsts + 1 => (byte)(_gsts >> 8),
        >= RegIntctl and < RegIntctl + 4 => (byte)(_intctl >> (8 * (offset - RegIntctl))),
        >= RegIntsts and < RegIntsts + 4 => (byte)(ReadIntstsDword() >> (8 * (offset - RegIntsts))),
        >= RegWalclk and < RegWalclk + 4 => (byte)(_walClock >> (8 * (offset - RegWalclk))),
        >= RegSsyncLegacyAlias and < RegSsyncLegacyAlias + 4 => (byte)(_ssync >> (8 * (offset - RegSsyncLegacyAlias))),
        >= RegSsync and < RegSsync + 4 => (byte)(_ssync >> (8 * (offset - RegSsync))),
        _ => 0,
    };

    private void WriteGlobalRegisterByte(int offset, byte value)
    {
        switch (offset)
        {
            case RegWakeen:
                _wakeen = (_wakeen & 0xFF00u) | value;
                break;
            case RegWakeen + 1:
                _wakeen = (uint)((_wakeen & 0x00FFu) | ((value & 0x7Fu) << 8)); // bit 15 reserved
                break;
            case RegStatests:
                _statests &= ~(uint)value; // RW1C
                break;
            case RegStatests + 1:
                _statests &= ~((uint)value << 8); // RW1C
                break;
            case RegGsts:
                _gsts &= ~((uint)value & GstsFstsBit); // RW1C, bit 1 only
                break;
            case RegGsts + 1:
                break; // reserved
            case >= RegIntctl and < RegIntctl + 4:
                {
                    int lane = offset - RegIntctl;
                    _intctl = (_intctl & ~(0xFFu << (8 * lane))) | ((uint)value << (8 * lane));
                    UpdateInterruptLine();
                    break;
                }
            case >= RegSsyncLegacyAlias and < RegSsyncLegacyAlias + 4:
                {
                    int lane = offset - RegSsyncLegacyAlias;
                    _ssync = (_ssync & ~(0xFFu << (8 * lane))) | ((uint)value << (8 * lane));
                    break;
                }
            case >= RegSsync and < RegSsync + 4:
                {
                    int lane = offset - RegSsync;
                    _ssync = (_ssync & ~(0xFFu << (8 * lane))) | ((uint)value << (8 * lane));
                    break;
                }
            default:
                break; // RO (GCAP, VMIN, VMAJ, OUTPAY, INPAY, INTSTS, WALCLK) or reserved
        }
    }

    /// <summary>Beyond the documented span, the BAR reads 0 and discards writes -- an empty region of the real chip's decode.</summary>
    private byte ReadExtendedRegisterByte(int offset) => TryReadLpibAlias(offset, out byte value) ? value : (byte)0;

    private void WriteExtendedRegisterByte(int offset, byte value)
    {
        // The LPIB aliases at 2084h+ are read-only; every other offset past
        // the documented span is simply unimplemented decode.
    }

    /// <summary>GCTL (08h-0Bh) is the one register writable regardless of <see cref="Crst"/> -- it is what brings the controller out of reset in the first place.</summary>
    private void WriteGctlByte(int lane, byte value)
    {
        if (lane == 0)
        {
            WriteGctlByte0(value);
            return;
        }

        if (lane == 1)
        {
            _gctl = (_gctl & 0xFFFF00FFu) | ((uint)(value & 0x01) << 8); // only bit 8 (UNSOL) is defined
        }

        // Lanes 2 and 3 (bits 31:16) are reserved: no defined bit, no effect.
    }

    private void WriteGctlByte0(byte value)
    {
        bool previousCrst = Crst;
        bool nextCrst = (value & GctlCrstBit) != 0;

        if (!nextCrst)
        {
            // Entering, or remaining in, reset: the whole register file --
            // this byte of GCTL included -- goes back to its power-on
            // defaults, exactly as a real chip held in reset would read.
            _gctl = 0;
            ResetRegisters();
            return;
        }

        _gctl = (_gctl & 0xFFFFFF00u) | value;
        if (!previousCrst)
        {
            // Leaving reset: the codec wired at address 0 announces itself,
            // exactly as a real link's first frame after reset does.
            _statests |= StatestsCodecPresentBit;
            UpdateInterruptLine();
        }
    }

    /// <summary>
    /// INTSTS (24h), computed live rather than stored. Bits 7:0 each mirror
    /// a stream's own <em>raw</em> pending condition (<see
    /// cref="IsStreamStatusPending"/>) -- HDA spec 3.3.14 does not describe
    /// these bits as gated by SDnCTL's own IOCE/FEIE/DEIE, and QEMU's own
    /// <c>intel-hda.c</c> does not gate BCIS by IOCE for them either. Bit 30
    /// is the command interface's own qualified pending condition (CORB/RIRB
    /// status ANDed with CORBCTL/RIRBCTL's own enables -- a different pair of
    /// registers HDA spec 3.3.13/3.3.14 does not cover, so this project keeps
    /// that local qualification). Bit 31 is the aggregate that actually
    /// drives the interrupt pin: true only once INTCTL's own GIE is set and
    /// at least one SDnCTL-qualified, SIE-gated stream (<see
    /// cref="IsStreamInterruptQualified"/>) or CIE-gated bit 30 is asserted.
    /// See <c>docs/HD-AUDIO.md</c> for this gating's own sourcing.
    /// </summary>
    private uint ReadIntstsDword()
    {
        uint streamBits = 0;
        uint qualifiedStreamBits = 0;
        for (int stream = 0; stream < StreamDescriptorCount; stream++)
        {
            if (IsStreamStatusPending(stream))
            {
                streamBits |= 1u << stream;
            }

            if (IsStreamInterruptQualified(stream))
            {
                qualifiedStreamBits |= 1u << stream;
            }
        }

        bool controllerPending = IsCommandInterfaceInterruptPending();
        uint value = streamBits;
        if (controllerPending)
        {
            value |= IntctlCieBit;
        }

        bool globalInterruptEnabled = (_intctl & IntctlGieBit) != 0;
        bool anyStreamAsserts = (qualifiedStreamBits & _intctl & IntctlSieMask) != 0;
        bool controllerAsserts = controllerPending && (_intctl & IntctlCieBit) != 0;
        if (globalInterruptEnabled && (anyStreamAsserts || controllerAsserts))
        {
            value |= IntctlGieBit;
        }

        return value;
    }

    /// <summary>Recomputes the PCI interrupt line from <see cref="ReadIntstsDword"/>'s own bit 31 -- called after any write that can change what is pending or enabled.</summary>
    private void UpdateInterruptLine()
    {
        bool asserted = (ReadIntstsDword() & IntctlGieBit) != 0;
        _interruptSink(asserted);
    }

    // --- IAudioSource ------------------------------------------------------

    string IAudioSource.Name => "Intel HD Audio";

    int IAudioSource.NativeSampleRate => MixSampleRate;

    int IAudioSource.ReadSamples(Span<short> destination) => ReadMixSamples(destination);

    // --- snapshot ----------------------------------------------------------

    /// <summary>The version of the payload <see cref="Capture"/> produces today.</summary>
    public const int SnapshotVersion = 1;

    private const int GlobalPayloadLength =
        sizeof(uint) // GCTL
        + sizeof(uint) // WAKEEN
        + sizeof(uint) // STATESTS
        + sizeof(uint) // GSTS
        + sizeof(uint) // INTCTL
        + sizeof(uint) // SSYNC
        + sizeof(uint) // WALCLK
        + sizeof(double); // the wall clock's own fractional tick accumulator

    /// <summary>
    /// The fixed size of a version-1 payload: the global registers above,
    /// the command interface's own state (<c>HdaController.Corb.cs</c>),
    /// every stream descriptor's registers and internal cursor
    /// (<c>HdaController.Streams.cs</c>), and the embedded codec's node
    /// registers (<see cref="HdaCodec.PayloadLength"/>). PCI configuration
    /// (including the BAR) is captured separately by <see cref="Pci.PciBus"/>;
    /// the host mix ring is presentation state, not captured, exactly as
    /// <see cref="Es1371"/>'s own equivalent rings are not.
    /// </summary>
    public const int SnapshotLength =
        GlobalPayloadLength + CommandInterfacePayloadLength + StreamsPayloadLength + HdaCodec.PayloadLength;

    public byte[] Capture()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(_gctl);
        writer.Write(_wakeen);
        writer.Write(_statests);
        writer.Write(_gsts);
        writer.Write(_intctl);
        writer.Write(_ssync);
        writer.Write(_walClock);
        writer.Write(_walClockAccumulator);

        CaptureCommandInterface(writer);
        CaptureStreams(writer);
        _codec.Capture(writer);

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Restores what <see cref="Capture"/> wrote.</summary>
    public void Restore(byte[] payload, int version)
    {
        if (version != SnapshotVersion || payload.Length != SnapshotLength)
        {
            throw new InvalidDataException("Invalid HD Audio controller snapshot envelope.");
        }

        using var reader = new BinaryReader(new MemoryStream(payload));

        _gctl = reader.ReadUInt32();
        _wakeen = reader.ReadUInt32();
        _statests = reader.ReadUInt32();
        _gsts = reader.ReadUInt32();
        _intctl = reader.ReadUInt32();
        _ssync = reader.ReadUInt32();
        _walClock = reader.ReadUInt32();
        _walClockAccumulator = reader.ReadDouble();

        RestoreCommandInterface(reader);
        RestoreStreams(reader);
        _codec.Restore(reader);

        UpdateInterruptLine();
    }
}
