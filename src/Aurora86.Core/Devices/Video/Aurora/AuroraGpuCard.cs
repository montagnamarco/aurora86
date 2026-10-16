using System.Buffers.Binary;
using Aurora86.Core.Devices.Pci;
using Aurora86.Core.IO;
using Aurora86.Core.Memory;

namespace Aurora86.Core.Devices.Video;

/// <summary>
/// The Aurora Display Adapter: a VGA-compatible PCI card designed in 2026 for
/// Aurora86 and the Aurora operating system, with 32 MB of video memory, a
/// scanout of any size up to 4096x4096 at 32 bits per pixel, and a 2D engine
/// fed by a command ring in guest memory. docs/AURORA-GPU.md is its
/// specification; this class implements it.
///
/// Software of any period can drive it. Firmware, DOS and a boot loader see
/// an ordinary VGA plus VESA modes up to 1920x1080x32, programmed by the
/// BIOS through <see cref="SetDisplayMode"/>. An operating system with a
/// driver programs the display registers in BAR1 directly and queues drawing
/// commands, which the card executes natively: in an interpreted emulator a
/// rectangle filled by the guest CPU costs tens of emulated instructions per
/// pixel, and one filled by the card costs a single ring entry.
/// </summary>
public sealed partial class AuroraGpuCard : VgaCard
{
    /// <summary>PCI command bits the card implements: I/O space, memory space, bus master.</summary>
    private const ushort PciCommandWritableBits = 0x0007;

    /// <summary>BAR0 is prefetchable memory: bit 3 of the base address register.</summary>
    private const uint PrefetchableMemoryBar = 0x08;

    private const int PciInterruptLineRegister = 0x3C;
    private const int PciInterruptPinRegister = 0x3D;
    private const byte PciInterruptPinA = 1;
    private const int PciRevisionAndClassRegister = 0x08;
    private const int Bar0Register = 0x10;
    private const int Bar1Register = 0x14;

    private const int BytesPerPixel = 4;

    private readonly byte[] _videoMemoryBytes = new byte[AuroraGpuInterface.VideoMemorySize];
    private readonly PciBus _pci;
    private readonly VideoMemoryWindow _videoMemoryWindow;
    private readonly RegisterWindow _registerWindow;
    private readonly PciMemoryBarWindow _videoMemoryDecoder;
    private readonly PciMemoryBarWindow _registerDecoder;
    private readonly AuroraGpuDisplay _display;
    private bool _interruptAsserted;
    // Guest-reported attachment is snapshot state; the host provider is not.
    private uint _reportedBackendKind;
    private uint _reportedBackendOperations;

    // Host preference is environmental state, independent of guest registers and snapshots.
    private readonly object _hostPreferredSizeGate = new();
    private uint _hostPreferredWidth;
    private uint _hostPreferredHeight;
    private uint _hostPreferredGeneration;

    // ---- display registers ---------------------------------------------------------

    private uint _displayControl;
    private uint _displayWidth;
    private uint _displayHeight;
    private uint _displayPitch;
    private uint _displayOffset;

    // ---- interrupt registers -------------------------------------------------------

    private uint _interruptStatus;
    private uint _interruptMask;

    /// <summary>
    /// Installs the card: standard VGA ports and the A0000h window, and a PCI
    /// function whose two BARs decode video memory and registers once the
    /// firmware or a driver enables memory space. The memory bus must decode
    /// all 4 GB, where the BARs' reset bases lie.
    /// </summary>
    public AuroraGpuCard(PhysicalMemoryBus memory, IoBus io, PciBus pci, ushort address = 0x0020)
        : base(new VgaRegisters())
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(io);
        ArgumentNullException.ThrowIfNull(pci);
        if (memory.AddressableBytes != 1UL << 32)
        {
            throw new ArgumentException("The Aurora Display Adapter requires a four-gigabyte physical address space.", nameof(memory));
        }

        _pci = pci;
        _videoMemoryWindow = new VideoMemoryWindow(this);
        _registerWindow = new RegisterWindow(this);
        Engine = new AuroraGpu2DEngine(_videoMemoryBytes, ReadGuestDword, ReadGuestByte, MarkDirty);

        // The legacy VGA, decoded unconditionally as the boot display's must
        // be: the BIOS writes text there before any PCI configuration.
        Registers.Register(io);
        memory.MapDevice(VgaMemory.Base, VgaMemory.Size, VideoMemory);
        Renderer = new VgaRenderer(VideoMemory, Registers);
        _display = new AuroraGpuDisplay(this, Renderer);

        Function = new PciFunction(
            address,
            AuroraGpuInterface.VendorId,
            AuroraGpuInterface.DeviceId,
            AuroraGpuInterface.ClassCode,
            commandMask: PciCommandWritableBits);
        Function.Define(PciRevisionAndClassRegister, (AuroraGpuInterface.ClassCode << 8) | AuroraGpuInterface.Revision, 4);
        DefineMemoryBar(Bar0Register, AuroraGpuInterface.VideoMemorySize, AuroraGpuInterface.VideoMemoryResetBase | PrefetchableMemoryBar);
        DefineMemoryBar(Bar1Register, AuroraGpuInterface.RegisterWindowSize, AuroraGpuInterface.RegisterWindowResetBase);
        Function.Define(PciInterruptLineRegister, 0, 1, 0xFF);
        Function.Define(PciInterruptPinRegister, PciInterruptPinA, 1);
        pci.Add(Function);

        _videoMemoryDecoder = new PciMemoryBarWindow(Function, 0, AuroraGpuInterface.VideoMemorySize, memory, _videoMemoryWindow);
        _registerDecoder = new PciMemoryBarWindow(Function, 1, AuroraGpuInterface.RegisterWindowSize, memory, _registerWindow);

        Reset();
    }

    public PciFunction Function { get; }
    public AuroraGpu2DEngine Engine { get; }

    /// <summary>Host-owned optional backend; null keeps the original software path.</summary>
    public IAuroraGpuBackend? Backend
    {
        get => Engine.Backend;
        set
        {
            Engine.Backend = value;
            UpdateReportedBackend();
        }
    }
    public override VgaRenderer Renderer { get; }

    /// <summary>What the front end shows: the adapter's own scanout when enabled, the VGA otherwise.</summary>
    public override IVideoOutput Display => _display;

    /// <summary>The whole of video memory, for diagnostics and tests.</summary>
    public Span<byte> VideoMemoryBytes => _videoMemoryBytes;

    /// <summary>The physical address BAR0 currently holds, whether or not it decodes.</summary>
    public uint FrameBufferBase => Function.Read(Bar0Register, 4) & Function.BarAddressMask(0);

    /// <summary>The physical address BAR1 currently holds.</summary>
    public uint RegisterBase => Function.Read(Bar1Register, 4) & Function.BarAddressMask(1);

    /// <summary>
    /// Publishes the host window's suggested guest size as one transaction.
    /// Both zeroes clear the suggestion; invalid pairs leave it unchanged.
    /// </summary>
    public void SetHostPreferredSize(int width, int height)
    {
        bool clear = width == 0 && height == 0;
        bool valid = width is >= 1 and <= AuroraGpuInterface.MaximumHostPreferredWidth
            && height is >= 1 and <= AuroraGpuInterface.MaximumHostPreferredHeight;
        if (!clear && !valid)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The host preferred size is out of range.");
        }

        lock (_hostPreferredSizeGate)
        {
            if (_hostPreferredWidth == (uint)width && _hostPreferredHeight == (uint)height)
            {
                return;
            }

            _hostPreferredWidth = (uint)width;
            _hostPreferredHeight = (uint)height;
            _hostPreferredGeneration = unchecked(_hostPreferredGeneration + 1);
        }
    }

    /// <summary>
    /// Drives the function's INTA through the motherboard's PCI interrupt
    /// router. The machine connects it once the router exists; until then an
    /// interrupt is remembered and delivered when it does.
    /// </summary>
    public Action<bool>? InterruptSink
    {
        get => _interruptSink;
        set
        {
            _interruptSink = value;
            _interruptSink?.Invoke(_interruptAsserted);
        }
    }

    private Action<bool>? _interruptSink;

    /// <summary>True while the function asserts INTA.</summary>
    public bool InterruptAsserted => _interruptAsserted;

    // ---- display state read by the scanout -------------------------------------------

    public bool ScanoutEnabled => (_displayControl & AuroraGpuInterface.DisplayControlScanoutEnable) != 0;

    /// <summary>True when scanout is enabled and the programmed geometry fits video memory.</summary>
    public bool ScanoutActive => ScanoutEnabled && DisplayGeometryValid;

    public int DisplayWidth => (int)_displayWidth;
    public int DisplayHeight => (int)_displayHeight;
    public uint DisplayPitch => _displayPitch;
    public uint DisplayOffset => _displayOffset;

    /// <summary>
    /// Whether width, height, pitch and offset describe a frame inside video
    /// memory (docs/AURORA-GPU.md section 4, "Geometry").
    /// </summary>
    public bool DisplayGeometryValid
    {
        get
        {
            bool dimensionsValid = _displayWidth is >= 1 and <= AuroraGpuInterface.MaximumDisplayDimension
                && _displayHeight is >= 1 and <= AuroraGpuInterface.MaximumDisplayDimension;
            if (!dimensionsValid)
            {
                return false;
            }

            bool pitchValid = _displayPitch % BytesPerPixel == 0 && _displayPitch >= _displayWidth * BytesPerPixel;
            bool offsetAligned = _displayOffset % BytesPerPixel == 0;
            long frameEnd = _displayOffset + (long)(_displayHeight - 1) * _displayPitch + (long)_displayWidth * BytesPerPixel;
            return pitchValid && offsetAligned && frameEnd <= _videoMemoryBytes.Length;
        }
    }

    /// <summary>
    /// The displayed frame as surface 0, or an undefined surface while the
    /// geometry is invalid.
    /// </summary>
    public AuroraGpuSurface DisplaySurface => DisplayGeometryValid
        ? new AuroraGpuSurface(true, _displayOffset, _displayPitch, (int)_displayWidth, (int)_displayHeight)
        : default;

    // ---- firmware entry points -----------------------------------------------------

    /// <summary>
    /// What the VBE BIOS does for a mode set: the display registers, scanout
    /// at offset 0, and the frame cleared unless the caller asked to keep it.
    /// </summary>
    public void SetDisplayMode(int width, int height, bool clearMemory)
    {
        _displayWidth = (uint)width;
        _displayHeight = (uint)height;
        _displayPitch = (uint)(width * BytesPerPixel);
        _displayOffset = 0;
        _displayControl |= AuroraGpuInterface.DisplayControlScanoutEnable;

        if (clearMemory)
        {
            int frameBytes = width * height * BytesPerPixel;
            Array.Clear(_videoMemoryBytes, 0, frameBytes);
        }

        MarkDisplayChanged();
    }

    /// <summary>
    /// A VGA mode set: the adapter's scanout gives the screen back to the
    /// VGA, as an INT 10h AH=00h call on any VESA card does.
    /// </summary>
    public void ReturnToVga()
    {
        _displayControl &= ~AuroraGpuInterface.DisplayControlScanoutEnable;
        MarkDisplayChanged();
    }

    private void UpdateReportedBackend()
    {
        _reportedBackendKind = Backend?.Kind ?? AuroraGpuInterface.BackendKindSoftware;
        _reportedBackendOperations = (Backend?.Operations ?? 0u) & AuroraGpuInterface.BackendOperationsKnown;
    }

    /// <summary>
    /// Power-on state: scanout off, no geometry, ring disabled and empty, no
    /// surfaces, cursor hidden, interrupts clear, video memory zero. The PCI
    /// configuration is reset by the bus itself.
    /// </summary>
    public void Reset()
    {
        UpdateReportedBackend();
        _displayControl = 0;
        _displayWidth = 0;
        _displayHeight = 0;
        _displayPitch = 0;
        _displayOffset = 0;
        _interruptStatus = 0;
        _interruptMask = 0;
        ResetRing();
        ResetDrawingState();
        Array.Clear(_videoMemoryBytes);
        UpdateInterrupt();
        MarkDisplayChanged();
    }

    // ---- change tracking for the scanout ------------------------------------------------

    /// <summary>Incremented whenever anything the scanout shows may have changed.</summary>
    internal int DisplayVersion { get; private set; }

    /// <summary>The byte range of video memory written since the scanout last consumed it.</summary>
    internal uint DirtyStart { get; private set; } = uint.MaxValue;
    internal uint DirtyEnd { get; private set; }

    /// <summary>True when the whole frame must be repainted, not only the dirty range.</summary>
    internal bool FullRepaintPending { get; private set; } = true;

    internal void ConsumeDirtyRange()
    {
        DirtyStart = uint.MaxValue;
        DirtyEnd = 0;
        FullRepaintPending = false;
    }

    private void MarkDirty(uint offset, uint length)
    {
        if (length == 0)
        {
            return;
        }

        DirtyStart = Math.Min(DirtyStart, offset);
        DirtyEnd = Math.Max(DirtyEnd, offset + length);
        DisplayVersion++;
    }

    private void MarkDisplayChanged()
    {
        FullRepaintPending = true;
        DisplayVersion++;
    }

    // ---- interrupts ---------------------------------------------------------------------

    private void RaiseInterrupt(uint bits)
    {
        _interruptStatus |= bits;
        UpdateInterrupt();
    }

    private void UpdateInterrupt()
    {
        bool asserted = (_interruptStatus & _interruptMask & AuroraGpuInterface.InterruptAll) != 0;
        if (asserted == _interruptAsserted)
        {
            return;
        }

        _interruptAsserted = asserted;
        _interruptSink?.Invoke(asserted);
    }

    // ---- registers ----------------------------------------------------------------------

    private uint ReadRegister(uint offset)
    {
        return offset switch
        {
            AuroraGpuInterface.IdRegister => AuroraGpuInterface.IdValue,
            AuroraGpuInterface.VersionRegister => AuroraGpuInterface.VersionValue,
            AuroraGpuInterface.VideoMemorySizeRegister => AuroraGpuInterface.VideoMemorySize,
            AuroraGpuInterface.CapabilitiesRegister => SupportedCapabilities |
                (_reportedBackendKind == AuroraGpuInterface.BackendKindSoftware
                    ? 0u : AuroraGpuInterface.CapabilityNativeBackend),
            AuroraGpuInterface.BackendInfoRegister => _reportedBackendKind,
            AuroraGpuInterface.BackendOperationsRegister => _reportedBackendOperations,
            AuroraGpuInterface.HostPreferredWidthRegister => ReadHostPreferredRegister(offset),
            AuroraGpuInterface.HostPreferredHeightRegister => ReadHostPreferredRegister(offset),
            AuroraGpuInterface.HostPreferredGenerationRegister => ReadHostPreferredRegister(offset),
            AuroraGpuInterface.DisplayControlRegister => _displayControl,
            AuroraGpuInterface.DisplayWidthRegister => _displayWidth,
            AuroraGpuInterface.DisplayHeightRegister => _displayHeight,
            AuroraGpuInterface.DisplayPitchRegister => _displayPitch,
            AuroraGpuInterface.DisplayOffsetRegister => _displayOffset,
            AuroraGpuInterface.DisplayStatusRegister => ScanoutActive ? AuroraGpuInterface.DisplayStatusActive : 0,
            AuroraGpuInterface.InterruptStatusRegister => _interruptStatus,
            AuroraGpuInterface.InterruptMaskRegister => _interruptMask,
            AuroraGpuInterface.RingBaseRegister => _ringBase,
            AuroraGpuInterface.RingSizeRegister => _ringSize,
            AuroraGpuInterface.RingHeadRegister => _ringHead,
            AuroraGpuInterface.RingTailRegister => _ringTail,
            AuroraGpuInterface.RingControlRegister => _ringControl,
            AuroraGpuInterface.FenceRegister => _fence,
            AuroraGpuInterface.ErrorRegister => _error,
            AuroraGpuInterface.ErrorOffsetRegister => _errorOffset,
            _ => 0,
        };
    }

    private const uint SupportedCapabilities = AuroraGpuInterface.CapabilityCommandRing
        | AuroraGpuInterface.CapabilityAlphaBlending
        | AuroraGpuInterface.CapabilityHardwareCursor
        | AuroraGpuInterface.CapabilityInterrupts
        | AuroraGpuInterface.CapabilityHostPreferredSize;

    private uint ReadHostPreferredRegister(uint offset)
    {
        lock (_hostPreferredSizeGate)
        {
            return offset switch
            {
                AuroraGpuInterface.HostPreferredWidthRegister => _hostPreferredWidth,
                AuroraGpuInterface.HostPreferredHeightRegister => _hostPreferredHeight,
                AuroraGpuInterface.HostPreferredGenerationRegister => _hostPreferredGeneration,
                _ => 0,
            };
        }
    }

    private void WriteRegister(uint offset, uint value)
    {
        switch (offset)
        {
            case AuroraGpuInterface.DisplayControlRegister:
                _displayControl = value & AuroraGpuInterface.DisplayControlScanoutEnable;
                MarkDisplayChanged();
                break;

            case AuroraGpuInterface.DisplayWidthRegister:
                _displayWidth = value;
                MarkDisplayChanged();
                break;

            case AuroraGpuInterface.DisplayHeightRegister:
                _displayHeight = value;
                MarkDisplayChanged();
                break;

            case AuroraGpuInterface.DisplayPitchRegister:
                _displayPitch = value;
                MarkDisplayChanged();
                break;

            case AuroraGpuInterface.DisplayOffsetRegister:
                _displayOffset = value;
                MarkDisplayChanged();
                break;

            case AuroraGpuInterface.InterruptStatusRegister:
                _interruptStatus &= ~value;
                UpdateInterrupt();
                break;

            case AuroraGpuInterface.InterruptMaskRegister:
                _interruptMask = value & AuroraGpuInterface.InterruptAll;
                UpdateInterrupt();
                break;

            case AuroraGpuInterface.RingBaseRegister:
                _ringBase = value;
                break;

            case AuroraGpuInterface.RingSizeRegister:
                _ringSize = value;
                break;

            case AuroraGpuInterface.RingTailRegister:
                _ringTail = value;
                ProcessRing();
                break;

            case AuroraGpuInterface.RingControlRegister:
                WriteRingControl(value);
                break;
        }
    }

    /// <summary>
    /// A register write made by the card's own VBE BIOS, which programs the
    /// same registers a driver does rather than a private copy of them.
    /// </summary>
    internal void WriteRegisterFromFirmware(uint register, uint value)
    {
        WriteRegister(register, value);
    }

    // ---- bus-master reads of guest memory ------------------------------------------------

    private uint ReadGuestDword(uint address)
    {
        IDmaMemory? memory = _pci.DmaMemory;
        return memory is null ? uint.MaxValue : memory.ReadDword(address);
    }

    private byte ReadGuestByte(uint address)
    {
        IDmaMemory? memory = _pci.DmaMemory;
        return memory is null ? (byte)0xFF : memory.ReadByte(address);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private void DefineMemoryBar(int register, uint size, uint resetValue)
    {
        uint addressMask = ~(size - 1) & 0xFFFFFFF0u;
        Function.Define(register, resetValue, 4, addressMask);
    }

    /// <summary>BAR0: the whole of video memory, as the guest CPU reaches it.</summary>
    private sealed class VideoMemoryWindow(AuroraGpuCard card) : IMemoryDevice
    {
        public byte ReadByte(uint offset)
        {
            return offset < card._videoMemoryBytes.Length ? card._videoMemoryBytes[offset] : (byte)0xFF;
        }

        public void WriteByte(uint offset, byte value)
        {
            if (offset >= card._videoMemoryBytes.Length)
            {
                return;
            }

            card._videoMemoryBytes[offset] = value;
            card.MarkDirty(offset, 1);
        }

        public uint ReadDword(uint offset)
        {
            if (offset > card._videoMemoryBytes.Length - sizeof(uint))
            {
                return uint.MaxValue;
            }

            return BinaryPrimitives.ReadUInt32LittleEndian(card._videoMemoryBytes.AsSpan((int)offset));
        }

        public void WriteDword(uint offset, uint value)
        {
            if (offset > card._videoMemoryBytes.Length - sizeof(uint))
            {
                return;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(card._videoMemoryBytes.AsSpan((int)offset), value);
            card.MarkDirty(offset, sizeof(uint));
        }
    }

    /// <summary>
    /// BAR1: the register file. Registers are defined for aligned 32-bit
    /// accesses; a narrower read returns the bytes it covers, and a narrower
    /// write is ignored, so a stray byte store cannot ring the doorbell with
    /// a half-written tail.
    /// </summary>
    private sealed class RegisterWindow(AuroraGpuCard card) : IMemoryDevice
    {
        private const uint RegisterAlignmentMask = 3;

        public byte ReadByte(uint offset)
        {
            uint register = card.ReadRegister(offset & ~RegisterAlignmentMask);
            return (byte)(register >> (int)((offset & RegisterAlignmentMask) * 8));
        }

        public void WriteByte(uint offset, byte value)
        {
        }

        public ushort ReadWord(uint offset)
        {
            return (ushort)(ReadByte(offset) | (ReadByte(offset + 1) << 8));
        }

        public void WriteWord(uint offset, ushort value)
        {
        }

        public uint ReadDword(uint offset)
        {
            if ((offset & RegisterAlignmentMask) != 0)
            {
                return (uint)(ReadWord(offset) | (ReadWord(offset + 2) << 16));
            }

            return card.ReadRegister(offset);
        }

        public void WriteDword(uint offset, uint value)
        {
            if ((offset & RegisterAlignmentMask) != 0)
            {
                return;
            }

            card.WriteRegister(offset, value);
        }
    }
}
