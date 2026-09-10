namespace Aurora386.Core.IO;

/// <summary>
/// A device attached to the I/O address space, which on x86 is entirely
/// separate from the memory address space and reached with IN and OUT rather
/// than ordinary loads and stores. The PC talks to its timer, keyboard, video
/// adapter and sound cards through these ports.
/// </summary>
public interface IIoDevice
{
    byte ReadByte(ushort port);
    void WriteByte(ushort port, byte value);

    /// <summary>
    /// 16-bit accesses. Most devices expose pairs of 8-bit registers and are
    /// correctly served as two byte accesses; those with genuinely 16-bit
    /// registers override these.
    /// </summary>
    ushort ReadWord(ushort port) => (ushort)(ReadByte(port) | (ReadByte((ushort)(port + 1)) << 8));

    void WriteWord(ushort port, ushort value)
    {
        WriteByte(port, (byte)value);
        WriteByte((ushort)(port + 1), (byte)(value >> 8));
    }
}

/// <summary>A device whose 32-bit registers cannot be split into two word accesses.</summary>
public interface IIoDwordDevice : IIoDevice
{
    uint ReadDword(ushort port);
    void WriteDword(ushort port, uint value);
}

/// <summary>
/// The I/O address space: 65536 ports, each assignable to a device.
/// Unassigned ports read back 0xFF, which is what an idle bus with nothing
/// driving it looks like -- and precisely the value period software probes
/// for when it wants to know whether a card is fitted.
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

    /// <summary>
    /// Takes a device off a range of ports, which then read as an empty
    /// bus. A Plug and Play card moved to a new address must stop answering
    /// at the old one, or two cards end up at the address it left.
    /// </summary>
    public void Unregister(ushort firstPort, int count, IIoDevice device)
    {
        for (int i = 0; i < count; i++)
            if (ReferenceEquals(_devices[firstPort + i], device)) _devices[firstPort + i] = null;
    }

    /// <summary>
    /// Raised on every port access, for diagnostics.
    ///
    /// It exists because a driver looking for its card does it by reading and
    /// writing registers, and nothing else shows that: the guest makes no
    /// BIOS call, raises no interrupt and touches no memory the emulator
    /// watches. Which ports it pokes, and in what order, is the whole of the
    /// evidence.
    ///
    /// Null when nothing is listening, which is the normal case: the check
    /// costs one comparison on a path that runs many times a second.
    /// </summary>
    public event Action<ushort, bool, byte>? PortAccessed;

    public byte ReadByte(ushort port)
    {
        byte value = _devices[port]?.ReadByte(port) ?? 0xFF;
        PortAccessed?.Invoke(port, false, value);
        return value;
    }

    public ushort ReadWord(ushort port) => _devices[port]?.ReadWord(port) ?? 0xFFFF;

    public uint ReadDword(ushort port)
        => _devices[port] is IIoDwordDevice wide ? wide.ReadDword(port)
            : (uint)(ReadWord(port) | (ReadWord((ushort)(port + 2)) << 16));

    public void WriteByte(ushort port, byte value)
    {
        PortAccessed?.Invoke(port, true, value);
        _devices[port]?.WriteByte(port, value);
    }

    public void WriteWord(ushort port, ushort value) => _devices[port]?.WriteWord(port, value);

    public void WriteDword(ushort port, uint value)
    {
        if (_devices[port] is IIoDwordDevice wide) { wide.WriteDword(port, value); return; }
        WriteWord(port, (ushort)value);
        WriteWord((ushort)(port + 2), (ushort)(value >> 16));
    }
}
