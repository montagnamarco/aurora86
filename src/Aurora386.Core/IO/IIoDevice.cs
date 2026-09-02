namespace Aurora386.Core.IO;

/// <summary>
/// Dispositivo collegato allo spazio di I/O, separato da quello di memoria.
/// Il PC usa le porte per parlare con timer, tastiera, video e schede sonore.
/// </summary>
public interface IIoDevice
{
    byte ReadByte(ushort port);
    void WriteByte(ushort port, byte value);

    /// <summary>
    /// Accessi a 16 bit. Molti dispositivi hanno registri a coppie e vanno
    /// serviti come due byte; quelli che hanno registri a 16 bit veri
    /// sovrascrivono questi metodi.
    /// </summary>
    ushort ReadWord(ushort port) => (ushort)(ReadByte(port) | (ReadByte((ushort)(port + 1)) << 8));

    void WriteWord(ushort port, ushort value)
    {
        WriteByte(port, (byte)value);
        WriteByte((ushort)(port + 1), (byte)(value >> 8));
    }
}

/// <summary>
/// Spazio di I/O: 65536 porte, ciascuna assegnabile a un dispositivo.
/// Le porte non assegnate leggono 0xFF, come un bus senza nulla collegato --
/// e' il valore che il software dell'epoca usa per accorgersi che una scheda
/// non c'e'.
/// </summary>
public sealed class IoBus
{
    private readonly IIoDevice?[] _devices = new IIoDevice?[0x10000];

    public void Register(ushort port, IIoDevice device) => _devices[port] = device;

    public void Register(ushort firstPort, int count, IIoDevice device)
    {
        for (int i = 0; i < count; i++)
            _devices[firstPort + i] = device;
    }

    public byte ReadByte(ushort port) => _devices[port]?.ReadByte(port) ?? 0xFF;

    public ushort ReadWord(ushort port) => _devices[port]?.ReadWord(port) ?? 0xFFFF;

    public uint ReadDword(ushort port)
        => (uint)(ReadWord(port) | (ReadWord((ushort)(port + 2)) << 16));

    public void WriteByte(ushort port, byte value) => _devices[port]?.WriteByte(port, value);

    public void WriteWord(ushort port, ushort value) => _devices[port]?.WriteWord(port, value);

    public void WriteDword(ushort port, uint value)
    {
        WriteWord(port, (ushort)value);
        WriteWord((ushort)(port + 2), (ushort)(value >> 16));
    }
}
