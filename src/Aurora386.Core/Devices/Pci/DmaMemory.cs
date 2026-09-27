using Aurora386.Core.Memory;

namespace Aurora386.Core.Devices.Pci;

/// <summary>
/// What a PCI bus master reaches to move bytes without going through the
/// CPU: the bus-master IDE engine's PRD-driven transfers, and later a NIC's
/// descriptor rings. A device must check
/// <see cref="PciFunction.BusMasterEnabled"/> itself before calling any of
/// these -- this interface only moves bytes, it does not gate them.
///
/// Real PCI bus mastering can present any physical address, including one
/// that lands on a memory-mapped device rather than RAM: a misprogrammed
/// disk descriptor pointing at the framebuffer, say. This machine does not
/// arbitrate that. Every bus master here or planned -- bus-master IDE, a
/// future NIC -- only ever targets buffers its driver allocated out of
/// conventional or extended RAM, so a transfer that reaches outside
/// installed RAM decodes nothing: reads answer 0xFF and writes disappear,
/// the same as a physical address with nothing wired to it. That is a
/// deliberate simplification, not an oversight -- it keeps a wild
/// descriptor from corrupting another device's registers instead of merely
/// losing its own data, at no cost to any driver that only ever DMAs into
/// the memory it was given.
/// </summary>
public interface IDmaMemory
{
    byte ReadByte(uint address);
    ushort ReadWord(uint address);
    uint ReadDword(uint address);
    void WriteByte(uint address, byte value);
    void WriteWord(uint address, ushort value);
    void WriteDword(uint address, uint value);

    /// <summary>
    /// Raised after a write that actually reached RAM, with the physical
    /// address and the number of bytes written. Nothing consumes this yet;
    /// it is the hook a future decode cache will use to invalidate a
    /// translation of a page a bus master just changed, the same way the
    /// CPU already has to notice when it overwrites code it might have
    /// cached a translation of -- except here the write is not the CPU's
    /// own store, so nothing else would otherwise learn of it at all.
    /// </summary>
    event Action<uint, uint>? Written;
}

/// <summary>The concrete <see cref="IDmaMemory"/>, backed by the machine's physical memory bus.</summary>
public sealed class PhysicalDmaMemory(PhysicalMemoryBus memory) : IDmaMemory
{
    public event Action<uint, uint>? Written;

    public byte ReadByte(uint address) => InRam(address, 1) ? memory.ReadByte(address) : (byte)0xFF;
    public ushort ReadWord(uint address) => InRam(address, 2) ? memory.ReadWord(address) : (ushort)0xFFFF;
    public uint ReadDword(uint address) => InRam(address, 4) ? memory.ReadDword(address) : uint.MaxValue;

    public void WriteByte(uint address, byte value)
    {
        if (!InRam(address, 1)) return;
        memory.WriteByte(address, value);
        Written?.Invoke(address, 1);
    }

    public void WriteWord(uint address, ushort value)
    {
        if (!InRam(address, 2)) return;
        memory.WriteWord(address, value);
        Written?.Invoke(address, 2);
    }

    public void WriteDword(uint address, uint value)
    {
        if (!InRam(address, 4)) return;
        memory.WriteDword(address, value);
        Written?.Invoke(address, 4);
    }

    private bool InRam(uint address, uint length) => address <= memory.RamSize && length <= memory.RamSize - address;
}
