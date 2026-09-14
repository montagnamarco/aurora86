using Aurora386.Core.Devices.Pci;
using Aurora386.Core.IO;
using Aurora386.Core.Memory;

namespace Aurora386.Core.Devices.Video;

/// <summary>
/// An experimental original 1996 ViRGE 86C325 PCI card. PCI enumeration,
/// CRTC address latches, the BitBLT engine and the S3d triangle engine share
/// one VRAM and display.
/// Drivers reach registers through configuration mechanism 1 or the new MMIO
/// window described in the August 1996 databook, sections 15, 17, 18 and 25.
/// This subset has standard VGA and enhanced banked memory decoding but no
/// OEM option ROM, old MMIO, big-endian decoding, Trio accelerator or IRQ timing.
/// </summary>
public sealed partial class S3VirgeCard : IIoDevice, IMemoryDevice, IDisposable
{
    private const uint ApertureSize = 0x04000000;
    private const uint ResetBase = 0x70000000;
    private const int BarRegister = 0x10;
    private const ushort PciMemoryEnable = 0x02;
    private const ushort PciIoEnable = 0x01;
    private readonly PhysicalMemoryBus.DeviceMapping _legacyMapping;
    private readonly PhysicalMemoryBus.DeviceMapping _mapping;
    private bool _synchronizing;
    private bool _disposed;
    private bool _oldMmioRequested;
    private bool _enhancedLegacyRequested;
    private bool _writingVgaRegisters;
    private bool _apertureWritePending;
    private bool _snapshotRestoreSuspended;
    private uint _lastPciBase;

    /// <summary>
    /// Attaches to buses that live as long as this card. The memory bus must
    /// decode all 4 GB, independently of its installed RAM. Disposal releases
    /// memory decoding; PCI topology removal is not supported by PciBus.
    /// </summary>
    public S3VirgeCard(PhysicalMemoryBus memory, IoBus io, PciBus pci, ushort address = 0x0020)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(io);
        ArgumentNullException.ThrowIfNull(pci);
        if (memory.AddressableBytes != 1UL << 32)
        {
            throw new ArgumentException("ViRGE requires a four-gigabyte physical address space.", nameof(memory));
        }

        Registers = new VgaRegisters(S3VideoIdentity.Virge325);
        VideoMemory = new VgaMemory(Registers);
        Streams = new S3VirgeStreams(Registers);
        Renderer = new VgaRenderer(VideoMemory, Registers, Streams);
        Engine = new S3VirgeEngine(VideoMemory);
        Blitter = new S3Virge2DEngine(VideoMemory);
        Function = S3VideoIdentity.Virge325.CreatePciFunction(address, commandMask: 0x0027);
        Function.DefineBar(0, ApertureSize, io: false, address: ResetBase);
        _lastPciBase = ResetBase;

        // Validate both owners before touching ports. A disposed memory bus
        // must not leave a PCI function behind; duplicate topology must not
        // retain even a disabled decoder in the memory bus.
        // The legacy window has stable lower priority than BAR0, even if
        // software later relocates the BAR over the first megabyte.
        _legacyMapping = memory.CreateDeviceMapping(new LegacyWindow(this));
        try
        {
            _mapping = memory.CreateDeviceMapping(this);
            try
            {
                pci.Add(Function);
            }
            catch
            {
                _mapping.Dispose();
                throw;
            }
        }
        catch
        {
            _legacyMapping.Dispose();
            throw;
        }
        Function.ConfigurationChanged += OnPciChanged;
        Registers.VirgeAperture!.Changed += OnApertureChanged;
        Registers.ModeChanged += SynchronizeMappings;
        RegisterPorts(io);
    }

    public VgaRegisters Registers { get; }
    public VgaMemory VideoMemory { get; }
    public S3VirgeEngine Engine { get; }
    public S3Virge2DEngine Blitter { get; }
    public S3VirgeStreams Streams { get; }
    public PciFunction Function { get; }
    public IVideoOutput Display => Renderer;

    /// <summary>The concrete VGA renderer shared with the machine video pipeline.</summary>
    internal VgaRenderer Renderer { get; }

    /// <summary>
    /// One notification per unsupported access or unsupported legacy mode transition.
    /// No logging or retained history is allocated when nobody listens.
    /// </summary>
    public event Action<S3VirgeUnsupportedAccess>? UnsupportedAccess;

    /// <summary>
    /// Observes each decoded new-MMIO write before dispatch, including
    /// unimplemented registers. The original transaction is neither split
    /// nor replayed. Diagnostic listeners must not change guest state.
    /// </summary>
    public event Action<S3VirgeMmioWrite>? MmioWriteObserved;

    /// <summary>Reads VGA PIO subject to PCI IO and the SR9 MMIO-only gate.</summary>
    public byte ReadByte(ushort port)
    {
        return CanAccessPort(port) ? Registers.ReadByte(port) : (byte)0xFF;
    }

    /// <summary>Writes VGA PIO; MMIO aliases deliberately bypass this PIO gate.</summary>
    public void WriteByte(ushort port, byte value)
    {
        if (CanAccessPort(port))
        {
            WriteVgaPortGroup(port, 1, value);
        }
    }

    /// <summary>
    /// Reconciles host-side register edits after a complete mode change or
    /// restore. Guest port and MMIO writes do this automatically. Call after a
    /// group of direct SetGraphics/SetCrtc edits, not halfway through a restore.
    /// </summary>
    public void SynchronizeMappings()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        OnApertureChanged();
    }

    /// <summary>
    /// Suspends VGA/PCI cross-synchronization while a machine snapshot restores
    /// both banks. Disposing reconciles the already-matching latches once.
    /// </summary>
    internal IDisposable SuspendSnapshotRestore()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_snapshotRestoreSuspended)
        {
            throw new InvalidOperationException("ViRGE snapshot restore is already suspended.");
        }

        _snapshotRestoreSuspended = true;
        return new SnapshotRestoreScope(this);
    }

    /// <summary>Releases decoding while leaving the bus-owned PCI topology intact.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Function.ConfigurationChanged -= OnPciChanged;
        Registers.VirgeAperture!.Changed -= OnApertureChanged;
        Registers.ModeChanged -= SynchronizeMappings;
        _mapping.Dispose();
        _legacyMapping.Dispose();
        _disposed = true;
    }

    private bool CanAccessPort(ushort port)
    {
        if (_disposed || (Function.Command & PciIoEnable) == 0)
        {
            return false;
        }

        bool mmioOnly = (Registers.Sequencer(9) & 0x80) != 0
            && (Registers.Crtc(0x53) & 0x18) != 0;
        if (!mmioOnly)
        {
            return true;
        }

        // Databook 17-1 and 18-1 include SR08 and CR2D in the extended
        // register groups. Standard VGA and index selection stay accessible.
        if (port is 0x3D5 or 0x3B5)
        {
            return Registers.ReadByte((ushort)(port - 1)) < 0x2D;
        }

        return port != 0x3C5 || Registers.ReadByte(0x3C4) < 0x08;
    }

    private void OnPciChanged()
    {
        if (_snapshotRestoreSuspended)
        {
            return;
        }

        if (_synchronizing)
        {
            _lastPciBase = Function.Read(BarRegister, 4);
            return;
        }

        _synchronizing = true;
        try
        {
            uint pciBase = Function.Read(BarRegister, 4);
            if (pciBase != _lastPciBase)
            {
                Registers.VirgeAperture!.SetPciBaseAddress(pciBase);
                _lastPciBase = pciBase;
            }
        }
        finally
        {
            _synchronizing = false;
        }

        UpdateMapping();
    }

    private void OnApertureChanged()
    {
        if (_snapshotRestoreSuspended)
        {
            return;
        }

        if (_writingVgaRegisters)
        {
            _apertureWritePending = true;
            return;
        }

        if (_synchronizing)
        {
            return;
        }

        _synchronizing = true;
        try
        {
            Function.Write(BarRegister, 4, Registers.VirgeAperture!.PciBase);
        }
        finally
        {
            _synchronizing = false;
        }

        UpdateMapping();
    }

    private void UpdateMapping()
    {
        UpdateLegacyMapping();
        // A disabled PCI function must reveal RAM or another underlying map.
        // Reserved holes are handled only inside an actively decoded aperture.
        if ((Function.Command & PciMemoryEnable) != 0)
        {
            _mapping.Update(Registers.VirgeAperture!.PciBase, ApertureSize);
        }
        else
        {
            _mapping.Disable();
        }

        bool oldMmio = Registers.VirgeAperture!.OldMmioEnabled;
        if (oldMmio && !_oldMmioRequested)
        {
            ReportUnsupported(0x53, 1, true, S3VirgeUnsupportedReason.OldMmio);
        }

        _oldMmioRequested = oldMmio;
    }

    private void CompleteSnapshotRestore()
    {
        _snapshotRestoreSuspended = false;
        _lastPciBase = Function.Read(BarRegister, 4);
        _apertureWritePending = false;
        _enhancedLegacyRequested = (Registers.Crtc(0x31) & EnhancedMappingEnable) != 0
            && (Registers.Crtc(0x66) & EnhancedFunctionsEnable) == 0;
        _oldMmioRequested = Registers.VirgeAperture!.OldMmioEnabled;
        UpdateMapping();
    }

    private sealed class SnapshotRestoreScope(S3VirgeCard card) : IDisposable
    {
        private S3VirgeCard? _card = card;

        public void Dispose()
        {
            S3VirgeCard? cardToResume = Interlocked.Exchange(ref _card, null);
            cardToResume?.CompleteSnapshotRestore();
        }
    }

    /// <summary>
    /// Commits a whole indexed MMIO operation before rebuilding address maps.
    /// CR59 can change while the following byte still belongs to the old
    /// transaction, so the address decoder must not move between its bytes.
    /// </summary>
    private void WriteVgaPortGroup(ushort firstPort, int width, uint value)
    {
        _writingVgaRegisters = true;
        try
        {
            for (int index = 0; index < width; index++)
            {
                Registers.WriteByte((ushort)(firstPort + index), (byte)(value >> (index * 8)));
            }
        }
        finally
        {
            _writingVgaRegisters = false;
            if (_apertureWritePending)
            {
                _apertureWritePending = false;
                OnApertureChanged();
            }
            else
            {
                UpdateMapping();
            }
        }
    }

    private void RegisterPorts(IoBus io)
    {
        // The same standard port aliases as VgaRegisters.Register, through
        // this owner so PCI and SR9 gates cannot be bypassed by PIO callers.
        io.Register(0x3C0, 2, this);
        io.Register(0x3C4, 2, this);
        io.Register(0x3C6, 4, this);
        io.Register(0x3CE, 2, this);
        io.Register(0x3D4, 2, this);
        io.Register(0x3B4, 2, this);
        io.Register(0x3DA, this);
        io.Register(0x3BA, this);
        io.Register(0x3CC, this);
        io.Register(0x3C2, this);
        io.Register(0x3CA, this);
        io.Register(0x3C3, this);
    }

    private void ReportUnsupported(uint offset, int width, bool write, S3VirgeUnsupportedReason reason)
    {
        UnsupportedAccess?.Invoke(new S3VirgeUnsupportedAccess(offset, width, write, reason));
    }
}

/// <summary>Unimplemented decoder features, not claims about silicon responses.</summary>
public enum S3VirgeUnsupportedReason
{
    ReservedOrUnimplemented,
    BigEndian,
    OldMmio,
    EnhancedLegacy,
    MisalignedConfiguration
}

/// <summary>
/// A bounded diagnostic of one unsupported operation. Memory offsets are
/// relative to BAR0; legacy mode transitions report the triggering CRTC index.
/// </summary>
public readonly record struct S3VirgeUnsupportedAccess(
    uint Offset, int Width, bool Write, S3VirgeUnsupportedReason Reason);

/// <summary>
/// A write to the 1996 ViRGE new-MMIO window as submitted by guest software.
/// Register is relative to the 64 KB MMIO window; Width is in bytes and Value
/// contains the original little-endian transaction, before register masking.
/// </summary>
public readonly record struct S3VirgeMmioWrite(uint Register, int Width, uint Value);
