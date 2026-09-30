namespace Aurora86.Core.Memory;

/// <summary>
/// A memory-mapped device: the video framebuffer, a ROM, and so on.
/// Addresses handed to these methods are <b>relative to the start of the
/// device</b>, not physical: the bus has already subtracted the base, so a
/// device never needs to know where it happens to be mapped.
/// </summary>
public interface IMemoryDevice
{
    byte ReadByte(uint offset);
    void WriteByte(uint offset, byte value);

    /// <summary>
    /// 16- and 32-bit accesses. By default they are composed from byte
    /// accesses in little-endian order. Devices that can serve them more
    /// efficiently, or whose registers behave differently depending on the
    /// access width -- as the VGA does in some modes -- override these.
    /// </summary>
    ushort ReadWord(uint offset) => (ushort)(ReadByte(offset) | (ReadByte(offset + 1) << 8));

    uint ReadDword(uint offset) => (uint)(ReadWord(offset) | (ReadWord(offset + 2) << 16));

    void WriteWord(uint offset, ushort value)
    {
        WriteByte(offset, (byte)value);
        WriteByte(offset + 1, (byte)(value >> 8));
    }

    void WriteDword(uint offset, uint value)
    {
        WriteWord(offset, (ushort)value);
        WriteWord(offset + 2, (ushort)(value >> 16));
    }
}
