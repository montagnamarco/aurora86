namespace Emulator.Core.Memory;

/// <summary>
/// Dispositivo mappato in memoria (framebuffer video, ROM, ...).
/// Gli indirizzi passati sono <b>relativi all'inizio del dispositivo</b>,
/// non fisici: e' il bus a sottrarre la base.
/// </summary>
public interface IMemoryDevice
{
    byte ReadByte(uint offset);
    void WriteByte(uint offset, byte value);

    /// <summary>
    /// Letture a 16/32 bit. Default: composizione little-endian di byte.
    /// I dispositivi che possono servirle piu' efficientemente (o che hanno
    /// semantica di larghezza particolare, come il VGA in certe modalita')
    /// sovrascrivono questi metodi.
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
