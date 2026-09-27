using Aurora386.Core.IO;
using Aurora386.Core.Memory;

namespace Aurora386.Core.Devices.Pci;

/// <summary>
/// One base address register bound to the real device handler it moves: an
/// I/O BAR bound to an <see cref="IIoDevice"/> and an <see cref="IoBus"/>, or
/// a memory BAR bound to an <see cref="IMemoryDevice"/> and a
/// <see cref="PhysicalMemoryBus"/>. See <see cref="PciIoBarWindow"/> and
/// <see cref="PciMemoryBarWindow"/> below.
///
/// Both listen to <see cref="PciFunction.ConfigurationChanged"/> and recompute
/// their decode on every configuration write, whether it moved the base,
/// flipped the command register's I/O or memory enable bit, or -- on a
/// snapshot restore -- replaced the whole register file at once: the old
/// range stops answering first, then the new one starts, so a guest never
/// sees both decode at once, and restoring a snapshot leaves the same
/// windows open the machine had when it was captured.
///
/// Neither ever maps the BAR's transient all-ones sizing probe. Software
/// sizes a region by writing all ones and reading back the mask; if it left
/// decode enabled while doing so -- the PCI specification expects it not to,
/// but does not require it -- a naive implementation would briefly claim
/// whatever address the mask itself spells out, which is not a base any
/// driver asked for. That state needs no bookkeeping of its own: it is
/// exactly the raw register value whose bits, wherever
/// <see cref="PciFunction.BarAddressMask"/> allows an address bit to be set,
/// are every one of them set.
/// </summary>
public sealed class PciIoBarWindow
{
    private readonly PciFunction _function;
    private readonly int _offset;
    private readonly uint _mask;
    private readonly int _length;
    private readonly IoBus _io;
    private readonly IIoDevice _device;
    private ushort? _mapped;

    /// <summary>The port the window currently answers at, or null while decode is off or out of range.</summary>
    public ushort? MappedBase => _mapped;

    public PciIoBarWindow(PciFunction function, int barIndex, int length, IoBus io, IIoDevice device)
    {
        _function = function;
        _offset = 0x10 + barIndex * 4;
        _mask = function.BarAddressMask(barIndex);
        _length = length;
        _io = io;
        _device = device;
        function.ConfigurationChanged += Update;
        Update();
    }

    private void Update()
    {
        if (_mapped is { } old) _io.Unregister(old, _length, _device);
        _mapped = null;

        uint raw = _function.Read(_offset, 4);
        bool decodeEnabled = (_function.Command & 1) != 0;
        bool isSizingProbe = _mask != 0 && (raw & _mask) == _mask;
        if (!decodeEnabled || isSizingProbe) return;

        uint address = raw & _mask;
        if (address > 0x10000u - (uint)_length) return;   // the 64 KB port space cannot reach it

        _mapped = (ushort)address;
        _io.Register(_mapped.Value, _length, _device);
    }
}

/// <summary>
/// The memory-BAR counterpart of <see cref="PciIoBarWindow"/>: see that
/// class's summary for the shared reasoning about relocation, restore and
/// the sizing probe.
///
/// A range that does not fit the bus -- misaligned to a 4 KB page, or
/// reaching past the addressable space -- decodes nothing rather than
/// throwing: the base came from the guest, and a wild descriptor must not
/// bring the emulator down any more than it would bring down real hardware.
/// </summary>
public sealed class PciMemoryBarWindow
{
    private readonly PciFunction _function;
    private readonly int _offset;
    private readonly uint _mask;
    private readonly uint _length;
    private readonly PhysicalMemoryBus _memory;
    private readonly IMemoryDevice _device;
    private uint? _mapped;

    /// <summary>The physical base the window currently answers at, or null while decode is off or out of range.</summary>
    public uint? MappedBase => _mapped;

    public PciMemoryBarWindow(PciFunction function, int barIndex, uint length, PhysicalMemoryBus memory, IMemoryDevice device)
    {
        _function = function;
        _offset = 0x10 + barIndex * 4;
        _mask = function.BarAddressMask(barIndex);
        _length = length;
        _memory = memory;
        _device = device;
        function.ConfigurationChanged += Update;
        Update();
    }

    private void Update()
    {
        if (_mapped is { } old) _memory.Unmap(old, _length);
        _mapped = null;

        uint raw = _function.Read(_offset, 4);
        bool decodeEnabled = (_function.Command & 2) != 0;
        bool isSizingProbe = _mask != 0 && (raw & _mask) == _mask;
        if (!decodeEnabled || isSizingProbe) return;

        uint address = raw & _mask;
        if (!_memory.TryMapDevice(address, _length, _device)) return;

        _mapped = address;
    }
}
