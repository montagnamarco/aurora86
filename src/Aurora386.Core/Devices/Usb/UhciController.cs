using Aurora386.Core.Devices.Pci;
using Aurora386.Core.IO;
using Aurora386.Core.Memory;

namespace Aurora386.Core.Devices.Usb;

/// <summary>
/// UHCI's software-owned frame schedule, fetched from physical guest RAM.
/// A frame contains TDs and queue heads; each transaction writes its result
/// back to the same descriptor the guest driver submitted. Ports expose
/// attach/reset/enable separately so address-zero enumeration is unambiguous.
/// </summary>
public sealed class UhciController : IIoDwordDevice
{
    private readonly PhysicalMemoryBus _memory;
    private readonly IoBus _io;
    private readonly PciInterruptRouter _router;
    public PciFunction Function { get; }
    private readonly IUsbDevice?[] _devices = new IUsbDevice?[2];
    private readonly ushort[] _ports = [0x80, 0x80];
    private ushort? _mapped;
    private ushort _command, _status = 0x20, _interruptEnable, _frame;
    private uint _frameBase;
    private byte _sof = 64;
    private double _elapsed;
    private bool _ioc, _shortPacket;
    private const uint Active = 1u << 23, Stalled = 1u << 22, Nak = 1u << 19, Timeout = 1u << 18;

    public UhciController(PhysicalMemoryBus memory, IoBus io, PciBus bus, ushort functionAddress = 0x10, ushort ioAddress = 0xC100)
    {
        _memory = memory; _io = io;
        _router = bus.InterruptRouter ?? throw new ArgumentException("USB requires PCI routing.", nameof(bus));
        Function = new PciFunction(functionAddress, 0x8086, 0x7020, 0x0C0300, commandMask: 5);
        Function.DefineBar(4, 32, true, ioAddress);
        Function.Define(4, 1, 2, 5);
        Function.Define(0x3C, 0xFF, 1, 0xFF);
        Function.Define(0x3D, 1, 1);
        Function.Define(0x60, 0x10, 1); // USB 1.0 serial bus release number
        Function.ConfigurationChanged += MapPorts;
        bus.Add(Function);
        MapPorts();
    }

    private void MapPorts()
    {
        if (_mapped is { } old) _io.Unregister(old, 32, this);
        uint address = Function.Read(0x20, 4) & 0xFFFFFFE0;
        _mapped = null;
        if ((Function.Command & 1) != 0 && address <= 0xFFE0)
        {
            _mapped = (ushort)address;
            _io.Register((ushort)address, 32, this);
        }
    }

    public void Attach(int port, IUsbDevice? device)
    {
        if ((uint)port >= 2) throw new ArgumentOutOfRangeException(nameof(port));
        if (ReferenceEquals(_devices[port], device)) return;
        bool enabled = (_ports[port] & 4) != 0;
        _devices[port] = device;
        device?.Reset();
        _ports[port] = (ushort)(PortPresence(port) | 2 | (enabled ? 8 : 0));
        if ((_command & 8) != 0) { _status |= 4; _command |= 16; UpdateInterrupt(); }
    }

    private ushort PortPresence(int port) => _devices[port] is { } device
        ? (ushort)(0x81 | (device.LowSpeed ? 0x120 : 0x10)) : (ushort)0x80;

    public void Reset()
    {
        _command = _interruptEnable = _frame = 0; _status = 0x20;
        _frameBase = 0; _sof = 64; _elapsed = 0; _ioc = _shortPacket = false;
        for (int i = 0; i < 2; i++) { _devices[i]?.Reset(); _ports[i] = (ushort)(PortPresence(i) | (_devices[i] is null ? 0 : 2)); }
        UpdateInterrupt();
    }

    private int Offset(ushort port) => _mapped is { } address ? port - address : -1;
    private ushort ReadRegister(int offset) => offset switch
    {
        0 => _command, 2 => _status, 4 => _interruptEnable, 6 => _frame,
        8 => (ushort)_frameBase, 10 => (ushort)(_frameBase >> 16), 12 => _sof,
        16 => _ports[0], 18 => _ports[1], _ => 0,
    };
    public byte ReadByte(ushort port)
    {
        int offset = Offset(port);
        return (byte)(ReadRegister(offset & ~1) >> ((offset & 1) * 8));
    }
    public ushort ReadWord(ushort port) => ReadRegister(Offset(port));
    public uint ReadDword(ushort port) => (uint)ReadWord(port) | ((uint)ReadWord((ushort)(port + 2)) << 16);
    public void WriteByte(ushort port, byte value)
    {
        int offset = Offset(port);
        if (offset is 6 or 7 or >= 16) return; // FRNUM/PORTSC require word writes
        int shift = (offset & 1) * 8;
        if ((offset & ~1) == 2) { WriteWord((ushort)(port & ~1), (ushort)(value << shift)); return; }
        ushort combined = (ushort)((ReadRegister(offset & ~1) & ~(255 << shift)) | value << shift);
        WriteWord((ushort)(port & ~1), combined);
    }
    public void WriteDword(ushort port, uint value)
    {
        WriteWord(port, (ushort)value);
        WriteWord((ushort)(port + 2), (ushort)(value >> 16));
    }
    public void WriteWord(ushort port, ushort value)
    {
        switch (Offset(port))
        {
            case 0:
                if ((value & 6) != 0) { Reset(); _command = (ushort)(value & 4); break; }
                _command = (ushort)(value & 0xF9);
                _status = (ushort)((_status & ~0x20) | ((_command & 1) == 0 ? 0x20 : 0));
                if ((_command & 1) == 0) _elapsed = 0;
                break;
            case 2:
                _status &= (ushort)~(value & 0x1F);
                if ((value & 1) != 0) _ioc = _shortPacket = false;
                break;
            case 4: _interruptEnable = (ushort)(value & 15); break;
            case 6: if ((_command & 1) == 0) _frame = (ushort)(value & 0x7FF); break;
            case 8: _frameBase = (_frameBase & 0xFFFF0000) | (uint)(value & 0xF000); break;
            case 10: _frameBase = (_frameBase & 0xFFFF) | (uint)value << 16; break;
            case 12: _sof = (byte)(value & 127); break;
            case 16: case 18:
                int index = (Offset(port) - 16) / 2;
                ushort old = _ports[index];
                ushort control = (ushort)(value & 0x1244);
                if (_devices[index] is null || (control & 0x200) != 0) control &= unchecked((ushort)~4);
                if ((control & 0x200) != 0 && (old & 0x200) == 0) _devices[index]?.Reset();
                _ports[index] = (ushort)(PortPresence(index) | control | (old & 0xA & ~value));
                break;
        }
        UpdateInterrupt();
    }

    private void UpdateInterrupt() => _router.SetInterrupt(Function.Address, 1,
        (_status & 0x18) != 0 || ((_status & 2) != 0 && (_interruptEnable & 1) != 0)
        || ((_status & 4) != 0 && (_interruptEnable & 2) != 0)
        || (_ioc && (_interruptEnable & 4) != 0) || (_shortPacket && (_interruptEnable & 8) != 0));

    public void Advance(double microseconds)
    {
        foreach (var device in _devices) device?.Advance(microseconds);
        if ((_command & 0x0D) != 1) return;
        _elapsed += microseconds;
        double duration = (11936 + _sof) / 12.0;
        while (_elapsed >= duration && (_command & 1) != 0)
        {
            _elapsed -= duration;
            if ((Function.Command & 4) != 0) ExecuteFrame();
            _frame = (ushort)((_frame + 1) & 0x7FF);
            UpdateInterrupt();
        }
    }

    private bool InRam(uint address, int length) => address <= _memory.RamSize && (uint)length <= _memory.RamSize - address;
    private void Fatal(bool busError = false) { _status |= busError ? (ushort)0x28 : (ushort)0x30; _command &= 0xFFFE; }
    private void ExecuteFrame()
    {
        uint entry = _frameBase + (uint)(_frame & 1023) * 4;
        if (!InRam(entry, 4)) { Fatal(true); return; }
        uint link = _memory.ReadDword(entry), horizontal = 1, queue = 0;
        // Guest lists may be circular for bandwidth reclamation. A bounded
        // walk represents the finite frame budget and never hangs the host.
        for (int count = 0; count < 1024 && (link & 1) == 0; count++)
        {
            uint address = link & 0xFFFFFFF0;
            if (!InRam(address, 16)) { Fatal(true); return; }
            if ((link & 2) != 0)
            {
                queue = address;
                horizontal = _memory.ReadDword(queue);
                link = _memory.ReadDword(queue + 4);
                if ((link & 1) != 0) { link = horizontal; queue = 0; }
                continue;
            }
            uint next = _memory.ReadDword(address);
            bool advance = ExecuteTd(address, queue != 0);
            if ((_command & 1) == 0) return;
            if (queue != 0)
            {
                if (advance) _memory.WriteDword(queue + 4, next);
                if (!advance || (next & 5) != 4) { link = horizontal; queue = 0; }
                else link = next;
            }
            else link = next;
            if ((_command & 0x20) != 0) { _command &= 0xFFFE; _status |= 0x20; return; }
        }
    }

    private bool ExecuteTd(uint address, bool queued)
    {
        uint control = _memory.ReadDword(address + 4);
        if ((control & Active) == 0)
        {
            if ((control & (1u << 24)) != 0) { _ioc = true; _status |= 1; }
            return false;
        }
        uint token = _memory.ReadDword(address + 8), buffer = _memory.ReadDword(address + 12);
        byte pid = (byte)token, deviceAddress = (byte)((token >> 8) & 127), endpoint = (byte)((token >> 15) & 15);
        int length = (int)(((token >> 21) + 1) & 0x7FF);
        if (pid is not (0x69 or 0xE1 or 0x2D)) { Fatal(); return false; }
        if (!InRam(buffer, length)) { Fatal(true); return false; }
        Span<byte> packet = stackalloc byte[2048];
        packet = packet[..length];
        if (pid != 0x69) for (int i = 0; i < length; i++) packet[i] = _memory.ReadByte(buffer + (uint)i);
        IUsbDevice? device = null;
        for (int i = 0; i < 2; i++)
            if ((_ports[i] & 0x1204) == 4 && _devices[i]?.Address == deviceAddress
                && _devices[i]!.LowSpeed == ((control & (1u << 26)) != 0)) device = _devices[i];
        var result = device?.Transfer(pid, endpoint, (token & (1u << 19)) != 0, packet)
            ?? new UsbTransactionResult(UsbHandshake.Timeout);
        uint updated = control & ~0x007F0000u;
        bool advance = false;
        switch (result.Handshake)
        {
            case UsbHandshake.Ack:
                if (result.Length < 0 || result.Length > length) { updated = (updated & ~Active) | Stalled | (1u << 20); _status |= 2; break; }
                if (pid == 0x69) for (int i = 0; i < result.Length; i++) _memory.WriteByte(buffer + (uint)i, packet[i]);
                updated = (updated & ~(Active | 0x7FFu)) | ((uint)(result.Length - 1) & 0x7FF);
                advance = true;
                if (queued && pid == 0x69 && result.Length < length && (control & (1u << 29)) != 0)
                { _shortPacket = true; _status |= 1; advance = false; }
                break;
            case UsbHandshake.Nak: updated |= Nak; break;
            case UsbHandshake.Stall: updated = (updated & ~Active) | Stalled; _status |= 2; break;
            case UsbHandshake.Timeout:
                updated |= Timeout;
                uint retries = (control >> 27) & 3;
                if (retries != 0)
                {
                    updated = (updated & ~(3u << 27)) | ((retries - 1) << 27);
                    if (retries == 1) { updated = (updated & ~Active) | Stalled; _status |= 2; }
                }
                break;
        }
        if ((updated & Active) == 0 && (control & (1u << 24)) != 0) { _ioc = true; _status |= 1; }
        _memory.WriteDword(address + 4, updated);
        return advance;
    }
    public byte[] Capture()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(_command); writer.Write(_status); writer.Write(_interruptEnable);
        writer.Write(_frame); writer.Write(_frameBase); writer.Write(_sof); writer.Write(_elapsed);
        writer.Write(_ioc); writer.Write(_shortPacket);
        for (int i = 0; i < 2; i++)
        {
            writer.Write(_ports[i]);
            writer.Write(_devices[i]?.GetType().FullName ?? "");
            byte[] state = _devices[i]?.Capture() ?? [];
            writer.Write(state.Length); writer.Write(state);
        }
        return stream.ToArray();
    }

    public void Restore(ReadOnlySpan<byte> state)
    {
        if (state.IsEmpty) { Reset(); return; }
        using var reader = new BinaryReader(new MemoryStream(state.ToArray()));
        ushort command = reader.ReadUInt16(), status = reader.ReadUInt16(), interrupts = reader.ReadUInt16();
        ushort frame = reader.ReadUInt16(); uint frameBase = reader.ReadUInt32();
        byte sof = reader.ReadByte(); double elapsed = reader.ReadDouble();
        bool ioc = reader.ReadBoolean(), shortPacket = reader.ReadBoolean();
        var ports = new ushort[2]; var blocks = new byte[2][];
        for (int i = 0; i < 2; i++)
        {
            ports[i] = reader.ReadUInt16();
            if (reader.ReadString() != (_devices[i]?.GetType().FullName ?? ""))
                throw new InvalidDataException("USB snapshot device topology differs from the machine.");
            int length = reader.ReadInt32();
            if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Invalid USB snapshot payload.");
            blocks[i] = reader.ReadBytes(length);
        }
        if (reader.BaseStream.Position != reader.BaseStream.Length || !double.IsFinite(elapsed) || elapsed < 0)
            throw new InvalidDataException("Invalid USB controller snapshot.");
        _command = command; _status = status; _interruptEnable = interrupts;
        _frame = frame; _frameBase = frameBase; _sof = sof; _elapsed = elapsed;
        _ioc = ioc; _shortPacket = shortPacket;
        for (int i = 0; i < 2; i++) { _ports[i] = ports[i]; _devices[i]?.Restore(blocks[i]); }
        UpdateInterrupt();
    }

}
