using Aurora386.Core.Memory;

namespace Aurora386.Core.Devices.Pci;

/// <summary>
/// What a PCI bus master reaches to move bytes without going through the
/// CPU: the bus-master IDE engine's PRD-driven transfers, and later a NIC's
/// descriptor rings. A device must check
/// <see cref="PciFunction.BusMasterEnabled"/> itself before calling any of
/// these -- this interface only moves bytes, it does not gate them.
///
/// Addresses are physical and bypass A20 masking, which applies only to the
/// CPU's own addresses. Anything below the installed RAM size is reached as
/// the CPU would see it, including the legacy A0000h-FFFFFh window: VGA
/// memory answers there, ROM discards writes and an empty option-ROM socket
/// reads FFh. Above the installed RAM this machine does not arbitrate
/// peer-to-peer transfers: reads answer FFh and writes disappear, so a wild
/// descriptor loses its own data instead of reaching another device's
/// registers. Every bus master here or planned only ever targets buffers its
/// driver allocated out of RAM, so no driver depends on more.
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
    /// How much installed RAM a bus master can actually reach -- the same
    /// bound every read and write above already enforces internally. A
    /// device that walks its own descriptors (UHCI's frame list, the
    /// bus-master IDE engine's PRD table) uses this to raise a host-system
    /// error on a wild pointer itself, rather than reading back the quiet
    /// 0xFF this interface gives for an address past the end.
    /// </summary>
    uint RamSize { get; }

    /// <summary>
    /// Raised after every write issued below the installed RAM size, with
    /// the physical address and the number of bytes, whether the bytes landed
    /// in RAM or were discarded by ROM. Nothing consumes this yet; it is the
    /// hook a future decode cache will use to invalidate a translation of a
    /// page a bus master just changed, since the write is not the CPU's own
    /// store and nothing else would otherwise learn of it.
    /// </summary>
    event Action<uint, uint>? Written;
}

/// <summary>The concrete <see cref="IDmaMemory"/>, backed by the machine's physical memory bus.</summary>
public sealed class PhysicalDmaMemory(PhysicalMemoryBus memory) : IDmaMemory
{
    public event Action<uint, uint>? Written;

    public uint RamSize => memory.RamSize;

    public byte ReadByte(uint address) => InRam(address, 1) ? memory.ReadByteFromBusMaster(address) : (byte)0xFF;

    public ushort ReadWord(uint address)
    {
        if (!InRam(address, 2))
        {
            return 0xFFFF;
        }
        return (ushort)ReadBytes(address, 2);
    }

    public uint ReadDword(uint address)
    {
        if (!InRam(address, 4))
        {
            return uint.MaxValue;
        }
        return ReadBytes(address, 4);
    }

    public void WriteByte(uint address, byte value) => WriteBytes(address, value, 1);

    public void WriteWord(uint address, ushort value) => WriteBytes(address, value, 2);

    public void WriteDword(uint address, uint value) => WriteBytes(address, value, 4);

    private uint ReadBytes(uint address, uint length)
    {
        uint value = 0;
        for (uint i = 0; i < length; i++)
        {
            value |= (uint)memory.ReadByteFromBusMaster(address + i) << (int)(8 * i);
        }
        return value;
    }

    private void WriteBytes(uint address, uint value, uint length)
    {
        if (!InRam(address, length))
        {
            return;
        }
        for (uint i = 0; i < length; i++)
        {
            memory.WriteByteFromBusMaster(address + i, (byte)(value >> (int)(8 * i)));
        }
        Written?.Invoke(address, length);
    }

    private bool InRam(uint address, uint length) => address <= memory.RamSize && length <= memory.RamSize - address;
}
