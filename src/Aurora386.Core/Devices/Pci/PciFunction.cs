namespace Aurora386.Core.Devices.Pci;

/// <summary>
/// One conventional PCI function: a 256-byte configuration space with explicit
/// write masks. BAR address bits double as the size-probe response; no separate
/// fake probe state is needed. Device owners react to ConfigurationChanged to
/// apply command decoding and BAR relocation to their real I/O or memory maps.
/// </summary>
public sealed class PciFunction
{
    private readonly byte[] _config = new byte[256];
    private readonly byte[] _writable = new byte[256];
    private readonly byte[] _reset = new byte[256];
    public ushort Address { get; }
    public ushort VendorId => (ushort)Read(0, 2);
    public ushort DeviceId => (ushort)Read(2, 2);
    public uint ClassCode => Read(8, 4) >> 8;
    public ushort Command => (ushort)Read(4, 2);
    public event Action? ConfigurationChanged;

    public PciFunction(ushort address, ushort vendor, ushort device, uint classCode,
                       byte headerType = 0, ushort commandMask = 0)
    {
        Address = address;
        Define(0, vendor, 2);
        Define(2, device, 2);
        Define(8, classCode << 8, 4);
        Define(14, headerType, 1);
        Define(4, 0, 2, commandMask);
        Define(12, 0, 1, 0xFF); // cache line size
        Define(13, 0, 1, 0xFF); // latency timer
    }

    /// <summary>Defines a register and its power-on value before attaching the function.</summary>
    public void Define(int offset, uint value, int width, uint writableMask = 0)
    {
        Validate(offset, width);
        for (int i = 0; i < width; i++)
        {
            _reset[offset + i] = _config[offset + i] = (byte)(value >> (8 * i));
            _writable[offset + i] = (byte)(writableMask >> (8 * i));
        }
    }

    /// <summary>A 32-bit memory BAR or an I/O BAR; the size must be a power of two.</summary>
    public void DefineBar(int index, uint size, bool io, uint address = 0)
    {
        if ((uint)index >= 6 || size < (io ? 4u : 16u) || (size & (size - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(size));
        uint mask = ~(size - 1) & (io ? 0xFFFFFFFCu : 0xFFFFFFF0u);
        Define(0x10 + index * 4, (address & mask) | (io ? 1u : 0u), 4, mask);
    }

    public uint Read(int offset, int width)
    {
        Validate(offset, width);
        uint value = 0;
        for (int i = 0; i < width; i++) value |= (uint)_config[offset + i] << (8 * i);
        return value;
    }

    public void Write(int offset, int width, uint value)
    {
        Validate(offset, width);
        bool changed = false;
        for (int i = 0; i < width; i++)
        {
            int at = offset + i;
            byte next = (byte)((uint)(_config[at] & ~_writable[at]) | ((value >> (8 * i)) & _writable[at]));
            changed |= next != _config[at];
            _config[at] = next;
        }
        if (changed) ConfigurationChanged?.Invoke();
    }

    internal byte[] Capture() => (byte[])_config.Clone();
    internal void Restore(ReadOnlySpan<byte> state)
    {
        if (state.Length != 256) throw new InvalidDataException("Invalid PCI configuration block.");
        for (int i = 0; i < 256; i++)
            _config[i] = (byte)((_reset[i] & ~_writable[i]) | (state[i] & _writable[i]));
        ConfigurationChanged?.Invoke();
    }
    public void Reset()
    {
        _reset.CopyTo(_config, 0);
        ConfigurationChanged?.Invoke();
    }
    private static void Validate(int offset, int width)
    {
        if (width is not (1 or 2 or 4) || offset < 0 || offset > 256 - width || (offset & (width - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
    }
}
