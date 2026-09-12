using System.Buffers.Binary;
using System.Text;

namespace Aurora386.Core.Devices.Usb;

/// <summary>
/// A single-LUN removable disk using USB bulk-only transport. The backing
/// stream is an image, never an IDE attachment. Commands validate their LBA
/// and length before allocating buffers or touching the image; incomplete
/// WRITE commands are not committed to the host file.
/// </summary>
public sealed class UsbMassStorageDevice : IUsbDevice, IDisposable
{
    private readonly Stream _image;
    public bool ReadOnly { get; }
    public string? SourcePath { get; }
    public byte Address { get; private set; }
    public byte Configuration { get; private set; }
    public bool LowSpeed => false;
    public long SectorCount => _image.Length / 512;
    private bool _medium = true, _haltIn, _haltOut, _needsReset, _inToggle, _outToggle;
    private byte[] _lastIn = [], _data = [], _csw = [], _setup = [], _controlReply = [];
    private int _position, _phase, _cswPosition, _controlPosition, _controlStage;
    private bool _controlToggle = true, _hostIn, _zeroPacket;
    private uint _tag, _hostLength, _processed, _writeLba;
    private int _writeLength;
    private byte _commandStatus, _senseKey, _asc;

    public UsbMassStorageDevice(Stream image, bool readOnly = false, string? sourcePath = null)
    {
        if (!image.CanRead || !image.CanSeek || image.Length < 512 || image.Length % 512 != 0)
            throw new ArgumentException("USB disk images must contain complete 512-byte sectors.", nameof(image));
        _image = image; ReadOnly = readOnly || !image.CanWrite; SourcePath = sourcePath;
    }
    public static UsbMassStorageDevice Open(string path, bool readOnly = false)
    {
        var stream = new FileStream(path, FileMode.Open, readOnly ? FileAccess.Read : FileAccess.ReadWrite, FileShare.Read);
        try { return new(stream, readOnly, Path.GetFullPath(path)); }
        catch { stream.Dispose(); throw; }
    }
    public void Dispose() => _image.Dispose();
    public void Reset()
    {
        Address = Configuration = 0; _haltIn = _haltOut = _needsReset = _inToggle = _outToggle = false;
        ResetTransport(); _controlStage = 0; _setup = _controlReply = []; _controlPosition = 0;
    }
    private void ResetTransport()
    {
        _phase = _position = _cswPosition = _writeLength = 0;
        _data = _csw = []; _zeroPacket = false; _needsReset = false;
    }
    private void Fail(byte key, byte asc) { _senseKey = key; _asc = asc; _commandStatus = 1; }
    private bool Range(uint lba, int blocks) => lba <= SectorCount && blocks <= SectorCount - lba;
    private void FinishData()
    {
        _csw = new byte[13]; _cswPosition = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(_csw, 0x53425355);
        BinaryPrimitives.WriteUInt32LittleEndian(_csw.AsSpan(4), _tag);
        BinaryPrimitives.WriteUInt32LittleEndian(_csw.AsSpan(8), _hostLength - _processed);
        _csw[12] = _commandStatus;
        _phase = 3;
    }
    private bool Command(ReadOnlySpan<byte> cbw)
    {
        if (cbw.Length != 31 || BinaryPrimitives.ReadUInt32LittleEndian(cbw) != 0x43425355
            || (cbw[12] & 127) != 0 || cbw[13] != 0 || cbw[14] is < 1 or > 16)
        { _needsReset = _haltIn = _haltOut = true; return false; }
        _tag = BinaryPrimitives.ReadUInt32LittleEndian(cbw[4..]);
        _hostLength = BinaryPrimitives.ReadUInt32LittleEndian(cbw[8..]); _hostIn = (cbw[12] & 128) != 0;
        _processed = 0; _commandStatus = 0; _writeLength = _position = 0; _data = []; _zeroPacket = false;
        ReadOnlySpan<byte> cdb = cbw.Slice(15, cbw[14]); byte op = cdb[0];
        int required = op is 0x25 or 0x28 or 0x2A or 0x35 or 0x23 or 0x5A ? 10 : 6;
        if (cdb.Length < required) { Fail(5, 0x24); FinishData(); return true; }
        bool output = op == 0x2A;
        if (!_medium && op is not (0x12 or 3 or 0x1B)) Fail(2, 0x3A);
        else try { switch (op)
        {
            case 0: case 0x1E: break;
            case 0x35: _image.Flush(); break;
            case 0x1B: if ((cdb[4] & 2) != 0) _medium = (cdb[4] & 1) != 0; break;
            case 0x12:
                if ((cdb[1] & 1) != 0) { Fail(5, 0x24); break; }
                _data = new byte[36]; _data[1] = 0x80; _data[2] = 2; _data[3] = 2; _data[4] = 31;
                Encoding.ASCII.GetBytes("AURORA  USB DISK        0001").CopyTo(_data, 8);
                _data = _data[..Math.Min(_data.Length, cdb[4])]; break;
            case 3:
                _data = new byte[18]; _data[0] = 0x70; _data[2] = _senseKey; _data[7] = 10; _data[12] = _asc;
                _data = _data[..Math.Min(_data.Length, cdb[4])]; _senseKey = _asc = 0; break;
            case 0x25:
                _data = new byte[8];
                BinaryPrimitives.WriteUInt32BigEndian(_data, (uint)Math.Min(SectorCount - 1, uint.MaxValue));
                BinaryPrimitives.WriteUInt32BigEndian(_data.AsSpan(4), 512); break;
            case 0x23:
                _data = new byte[12]; _data[3] = 8;
                BinaryPrimitives.WriteUInt32BigEndian(_data.AsSpan(4), (uint)Math.Min(SectorCount, uint.MaxValue));
                _data[8] = 2; _data[10] = 2;
                _data = _data[..Math.Min(_data.Length, BinaryPrimitives.ReadUInt16BigEndian(cdb[7..]))]; break;
            case 0x1A:
                _data = [3,0,(byte)(ReadOnly ? 128 : 0),0];
                _data = _data[..Math.Min(4, (int)cdb[4])]; break;
            case 0x5A:
                _data = [0,6,0,(byte)(ReadOnly ? 128 : 0),0,0,0,0];
                _data = _data[..Math.Min(8, (int)BinaryPrimitives.ReadUInt16BigEndian(cdb[7..]))]; break;
            case 0x28: case 0x2A:
            {
                uint lba = BinaryPrimitives.ReadUInt32BigEndian(cdb[2..]);
                int blocks = BinaryPrimitives.ReadUInt16BigEndian(cdb[7..]);
                if (!Range(lba, blocks)) { Fail(5, 0x21); break; }
                if (output && ReadOnly) { Fail(7, 0x27); break; }
                _data = new byte[blocks * 512];
                if (output) { _writeLba = lba; _writeLength = _data.Length; }
                else { _image.Position = (long)lba * 512; _image.ReadExactly(_data); }
                break;
            }
            default: Fail(5, 0x20); break;
        } }
        catch (IOException) { _data = []; Fail(3, op == 0x35 ? (byte)0x0C : (byte)0x11); }
        int deviceLength = _data.Length;
        if (_hostLength == 0)
        {
            if (deviceLength != 0) _commandStatus = 2;
            FinishData(); return true;
        }
        if (deviceLength == 0 || _hostIn == output)
        {
            if (deviceLength != 0) _commandStatus = 2;
            if (_hostIn) _haltIn = true; else _haltOut = true;
            FinishData(); return true;
        }
        if (_hostLength < deviceLength) { _data = _data[..(int)_hostLength]; _commandStatus = 2; }
        _zeroPacket = _hostIn && _hostLength > _data.Length && _data.Length % 64 == 0;
        _phase = output ? 2 : 1;
        return true;
    }

    public UsbTransactionResult Transfer(byte pid, byte endpoint, bool data1, Span<byte> data)
    {
        if (endpoint == 0) return Control(pid, data1, data);
        if (Configuration == 0) return new(UsbHandshake.Nak);
        if (_needsReset) return new(UsbHandshake.Stall);
        if (endpoint == 1 && pid == 0x69)
        {
            if (_haltIn) return new(UsbHandshake.Stall);
            if (data1 != _inToggle)
            {
                if (_lastIn.Length > data.Length) return new(UsbHandshake.Stall);
                _lastIn.CopyTo(data); return new(UsbHandshake.Ack, _lastIn.Length);
            }
            int count;
            if (_phase == 1)
            {
                count = Math.Min(Math.Min(data.Length, 64), _data.Length - _position);
                _data.AsSpan(_position, count).CopyTo(data); _position += count; _processed += (uint)count;
                if (_position == _data.Length)
                {
                    if (count == 0 || !_zeroPacket) FinishData();
                    else _zeroPacket = false;
                }
            }
            else if (_phase == 3)
            {
                count = Math.Min(data.Length, _csw.Length - _cswPosition);
                _csw.AsSpan(_cswPosition, count).CopyTo(data); _cswPosition += count;
                if (_cswPosition == _csw.Length) { _phase = 0; if (_commandStatus == 2) _needsReset = true; }
            }
            else return new(UsbHandshake.Nak);
            _lastIn = data[..count].ToArray(); _inToggle = !_inToggle;
            return new(UsbHandshake.Ack, count);
        }
        if (endpoint == 2 && pid == 0xE1)
        {
            if (_haltOut || data.Length > 64) return new(UsbHandshake.Stall);
            if (data1 != _outToggle) return new(UsbHandshake.Ack, data.Length);
            if (_phase == 0)
            {
                if (!Command(data)) return new(UsbHandshake.Stall);
            }
            else if (_phase == 2)
            {
                int count = Math.Min(data.Length, _data.Length - _position);
                data[..count].CopyTo(_data.AsSpan(_position)); _position += count;
                if (_position == _data.Length)
                {
                    if (_commandStatus == 0 && _position == _writeLength)
                    {
                        try
                        {
                            _image.Position = (long)_writeLba * 512; _image.Write(_data); _image.Flush();
                            _processed = (uint)_position;
                        }
                        catch (IOException) { Fail(3, 0x0C); }
                    }
                    if (_hostLength > _data.Length) _haltOut = true;
                    FinishData();
                }
            }
            else { _needsReset = _haltOut = _haltIn = true; return new(UsbHandshake.Stall); }
            _outToggle = !_outToggle;
            return new(UsbHandshake.Ack, data.Length);
        }
        return new(UsbHandshake.Stall);
    }

    private byte[] Descriptor(ushort value)
    {
        if (value == 0x100) return [18,1,0x10,1,0,0,0,64,0xFE,0xCA,3,0,0,1,1,2,3,1];
        if (value == 0x200) return [9,2,32,0,1,1,0,0x80,50,9,4,0,0,2,8,6,0x50,0,7,5,0x81,2,64,0,0,7,5,2,2,64,0,0];
        if (value == 0x300) return [4,3,9,4];
        string text = value switch { 0x301 => "Aurora386", 0x302 => "USB Disk", 0x303 => "000000000001", _ => "" };
        byte[] chars = Encoding.Unicode.GetBytes(text);
        return text.Length == 0 ? [] : [(byte)(chars.Length + 2),3,..chars];
    }
    private UsbTransactionResult Control(byte pid, bool data1, Span<byte> packet)
    {
        if (pid == 0x2D && !data1 && packet.Length == 8)
        {
            _setup = packet.ToArray(); _controlReply = []; _controlPosition = 0; _controlToggle = true; _controlStage = 0;
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(packet[2..]), index = BinaryPrimitives.ReadUInt16LittleEndian(packet[4..]);
            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(packet[6..]);
            switch ((packet[0], packet[1]))
            {
                case (0x80,6): _controlReply = Descriptor(value); if (_controlReply.Length == 0) return new(UsbHandshake.Stall); break;
                case (0,5) when value < 128 && index == 0 && length == 0: break;
                case (0,9) when value <= 1 && index == 0 && length == 0: break;
                case (0x80,8) when index == 0 && value == 0: _controlReply = [Configuration]; break;
                case (0x80,0) when index == 0 && value == 0: _controlReply = [0,0]; break;
                case (0x81,0 or 10) when index == 0 && value == 0: _controlReply = packet[1] == 10 ? [0] : [0,0]; break;
                case (1,11) when index == 0 && value == 0 && length == 0: break;
                case (0x82,0) when value == 0 && index is 0x81 or 2: _controlReply = [(byte)((index == 0x81 ? _haltIn : _haltOut) ? 1 : 0),0]; break;
                case (2,1 or 3) when value == 0 && index is 0x81 or 2 && length == 0: break;
                case (0xA1,0xFE) when value == 0 && index == 0 && length == 1: _controlReply = [0]; break;
                case (0x21,0xFF) when value == 0 && index == 0 && length == 0: break;
                default: return new(UsbHandshake.Stall);
            }
            if (_controlReply.Length > length) _controlReply = _controlReply[..length];
            _controlStage = length == 0 ? 2 : 1;
            return new(UsbHandshake.Ack, 8);
        }
        if (_controlStage == 1 && pid == 0x69 && data1 == _controlToggle)
        {
            int count = Math.Min(Math.Min(packet.Length, 64), _controlReply.Length - _controlPosition);
            _controlReply.AsSpan(_controlPosition, count).CopyTo(packet); _controlPosition += count; _controlToggle = !_controlToggle;
            if (count < 64 || _controlPosition >= BinaryPrimitives.ReadUInt16LittleEndian(_setup.AsSpan(6))) _controlStage = 3;
            return new(UsbHandshake.Ack, count);
        }
        if ((_controlStage == 2 && pid == 0x69 || _controlStage == 3 && pid == 0xE1) && data1 && packet.Length == 0)
        {
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_setup.AsSpan(2)), index = BinaryPrimitives.ReadUInt16LittleEndian(_setup.AsSpan(4));
            switch ((_setup[0], _setup[1]))
            {
                case (0,5): Address = (byte)value; break;
                case (0,9): Configuration = (byte)value; ResetTransport(); _inToggle = _outToggle = _haltIn = _haltOut = false; _lastIn = []; break;
                case (2,1):
                    if (index == 0x81) { _haltIn = _inToggle = false; _lastIn = []; }
                    else _haltOut = _outToggle = false;
                    break;
                case (2,3): if (index == 0x81) _haltIn = true; else _haltOut = true; break;
                case (0x21,0xFF): ResetTransport(); break;
            }
            _controlStage = 0; return new(UsbHandshake.Ack);
        }
        return new(UsbHandshake.Stall);
    }

    public byte[] Capture()
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write(SectorCount); w.Write(ReadOnly); w.Write(Address); w.Write(Configuration);
        foreach (bool value in new[] {_medium,_haltIn,_haltOut,_needsReset,_inToggle,_outToggle,_controlToggle,_hostIn,_zeroPacket}) w.Write(value);
        foreach (int value in new[] {_position,_phase,_cswPosition,_controlPosition,_controlStage,_writeLength}) w.Write(value);
        w.Write(_tag); w.Write(_hostLength); w.Write(_processed); w.Write(_writeLba);
        w.Write(_commandStatus); w.Write(_senseKey); w.Write(_asc);
        foreach (byte[] bytes in new[] {_lastIn,_data,_csw,_setup,_controlReply}) { w.Write(bytes.Length); w.Write(bytes); }
        return stream.ToArray();
    }
    public void Restore(ReadOnlySpan<byte> state)
    {
        using var r = new BinaryReader(new MemoryStream(state.ToArray()));
        if (r.ReadInt64() != SectorCount || r.ReadBoolean() != ReadOnly) throw new InvalidDataException("USB disk snapshot media differs.");
        Address = r.ReadByte(); Configuration = r.ReadByte();
        _medium = r.ReadBoolean(); _haltIn = r.ReadBoolean(); _haltOut = r.ReadBoolean(); _needsReset = r.ReadBoolean();
        _inToggle = r.ReadBoolean(); _outToggle = r.ReadBoolean(); _controlToggle = r.ReadBoolean(); _hostIn = r.ReadBoolean(); _zeroPacket = r.ReadBoolean();
        _position = r.ReadInt32(); _phase = r.ReadInt32(); _cswPosition = r.ReadInt32(); _controlPosition = r.ReadInt32(); _controlStage = r.ReadInt32(); _writeLength = r.ReadInt32();
        _tag = r.ReadUInt32(); _hostLength = r.ReadUInt32(); _processed = r.ReadUInt32(); _writeLba = r.ReadUInt32();
        _commandStatus = r.ReadByte(); _senseKey = r.ReadByte(); _asc = r.ReadByte();
        byte[] ReadBlock()
        {
            int length = r.ReadInt32();
            if (length < 0 || length > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("Invalid USB disk snapshot.");
            return r.ReadBytes(length);
        }
        _lastIn = ReadBlock(); _data = ReadBlock(); _csw = ReadBlock(); _setup = ReadBlock(); _controlReply = ReadBlock();
        if (r.BaseStream.Position != r.BaseStream.Length || _phase is < 0 or > 3 || _processed > _hostLength)
            throw new InvalidDataException("Invalid USB disk snapshot.");
    }
}
