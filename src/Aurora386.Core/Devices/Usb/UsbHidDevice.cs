using System.Buffers.Binary;

namespace Aurora386.Core.Devices.Usb;

/// <summary>
/// A low-speed boot HID device with an eight-byte control endpoint and one
/// interrupt IN endpoint. Address/configuration changes commit only after
/// the status packet, so the SET_ADDRESS status still uses the old address.
/// </summary>
public sealed class UsbHidDevice : IUsbDevice
{
    public bool Keyboard { get; }
    public byte Address { get; private set; }
    public byte Configuration { get; private set; }
    public bool LowSpeed => true;
    public byte Leds { get; private set; }
    private byte _protocol = 1, _idle;
    private bool _dirty = true, _halted, _interruptData1;
    private byte[] _lastReport = [];
    private bool _extendedScan;
    private int _remainingPauseScanBytes;
    private readonly HashSet<byte> _keys = [];
    private byte _buttons;
    private int _pendingDeltaX, _pendingDeltaY, _pendingWheelDelta;
    private double _idleMicroseconds;
    private byte[] _reply = [], _setup = [];
    private int _controlOffset;
    private ControlStage _controlStage;

    // Explicit values preserve the existing snapshot representation.
    private enum ControlStage
    {
        Idle = 0,
        DataIn = 1,
        DataOut = 2,
        StatusIn = 3,
        StatusOut = 4,
    }
    private bool _data1 = true;
    private byte[] _output = [];

    private static readonly byte[] KeyboardReport = [
        0x05,1, 0x09,6, 0xA1,1, 0x05,7, 0x19,0xE0, 0x29,0xE7,
        0x15,0, 0x25,1, 0x75,1, 0x95,8, 0x81,2,
        0x95,1, 0x75,8, 0x81,1,
        0x95,5, 0x75,1, 0x05,8, 0x19,1, 0x29,5, 0x91,2,
        0x95,1, 0x75,3, 0x91,1,
        0x95,6, 0x75,8, 0x15,0, 0x25,0x65, 0x05,7, 0x19,0, 0x29,0x65, 0x81,0, 0xC0];
    private static readonly byte[] MouseReport = [
        0x05,1, 0x09,2, 0xA1,1, 0x09,1, 0xA1,0,
        0x05,9, 0x19,1, 0x29,3, 0x15,0, 0x25,1, 0x95,3, 0x75,1, 0x81,2,
        0x95,1, 0x75,5, 0x81,1,
        0x05,1, 0x09,0x30, 0x09,0x31, 0x09,0x38, 0x15,0x81, 0x25,0x7F,
        0x75,8, 0x95,3, 0x81,6, 0xC0, 0xC0];
    private byte[] ReportDescriptor => Keyboard ? KeyboardReport : MouseReport;
    public UsbHidDevice(bool keyboard) => Keyboard = keyboard;

    // Host frontends already submit AT set-1 scan codes. Translate their
    // physical key identities, rather than characters from a host layout.
    private static readonly byte[] Set1Usages = [
        0,41,30,31,32,33,34,35,36,37,38,39,45,46,42,43,
        20,26,8,21,23,28,24,12,18,19,47,48,40,224,4,22,
        7,9,10,11,13,14,15,51,52,53,225,49,29,27,6,25,
        5,17,16,54,55,56,229,85,226,44,57,58,59,60,61,62,
        63,64,65,66,67,83,71,95,96,97,86,92,93,94,87,89,
        90,91,98,99,0,0,100,68,69];
    public void PushScanCode(byte code)
    {
        if (_remainingPauseScanBytes > 0) { if (--_remainingPauseScanBytes == 0) SetKey(72, true); return; }
        if (code == 0xE1) { _remainingPauseScanBytes = 5; _extendedScan = false; return; }
        if (code == 0xE0) { _extendedScan = true; return; }
        byte scan = (byte)(code & 127);
        byte usage = (byte)(_extendedScan ? scan switch
        {
            0x1C => 88, 0x1D => 228, 0x35 => 84, 0x37 => 70, 0x38 => 230,
            0x47 => 74, 0x48 => 82, 0x49 => 75, 0x4B => 80, 0x4D => 79,
            0x4F => 77, 0x50 => 81, 0x51 => 78, 0x52 => 73, 0x53 => 76,
            0x5B => 227, 0x5C => 231, 0x5D => 101, _ => (byte)0,
        } : scan < Set1Usages.Length ? Set1Usages[scan] : (byte)0);
        _extendedScan = false;
        SetKey(usage, (code & 128) == 0);
    }

    public void SetKey(byte usage, bool pressed)
    {
        if (!Keyboard || usage < 4) return;
        if (pressed ? _keys.Add(usage) : _keys.Remove(usage)) _dirty = true;
    }
    public void Move(int x, int y, int wheel = 0)
    {
        if (Keyboard) return;
        _pendingDeltaX = (int)Math.Clamp((long)_pendingDeltaX + x, -32767, 32767);
        _pendingDeltaY = (int)Math.Clamp((long)_pendingDeltaY + y, -32767, 32767);
        _pendingWheelDelta = (int)Math.Clamp((long)_pendingWheelDelta + wheel, -32767, 32767);
        if (x != 0 || y != 0 || wheel != 0) _dirty = true;
    }
    public void SetButton(byte button, bool pressed) => SetButtons(pressed ? (byte)(_buttons | button) : (byte)(_buttons & ~button));

    public void SetButtons(byte buttons)
    {
        buttons &= 7;
        if (_buttons != buttons) { _buttons = buttons; _dirty = true; }
    }
    public void Advance(double microseconds) => _idleMicroseconds += microseconds;
    public void Reset()
    {
        Address = Configuration = Leds = _idle = _buttons = 0;
        _protocol = 1; _dirty = true; _halted = _interruptData1 = _extendedScan = false; _remainingPauseScanBytes = 0;
        _lastReport = []; _keys.Clear();
        _pendingDeltaX = _pendingDeltaY = _pendingWheelDelta = _controlOffset = 0;
        _controlStage = ControlStage.Idle; _idleMicroseconds = 0;
        _setup = _reply = _output = []; _data1 = true;
    }

    private byte[] Report(bool consume)
    {
        if (Keyboard)
        {
            byte[] report = new byte[8];
            foreach (byte key in _keys.Where(k => k is >= 0xE0 and <= 0xE7)) report[0] |= (byte)(1 << (key - 0xE0));
            byte[] keys = _keys.Where(k => k < 0xE0).Order().ToArray();
            for (int i = 0; i < 6; i++) report[i + 2] = keys.Length > 6 ? (byte)1 : i < keys.Length ? keys[i] : (byte)0;
            return report;
        }
        int x = Math.Clamp(_pendingDeltaX, -127, 127), y = Math.Clamp(_pendingDeltaY, -127, 127), wheel = Math.Clamp(_pendingWheelDelta, -127, 127);
        byte[] mouse = _protocol == 0 ? [_buttons, unchecked((byte)x), unchecked((byte)y)]
            : [_buttons, unchecked((byte)x), unchecked((byte)y), unchecked((byte)wheel)];
        if (consume) { _pendingDeltaX -= x; _pendingDeltaY -= y; _pendingWheelDelta = _protocol == 0 ? 0 : _pendingWheelDelta - wheel; }
        return mouse;
    }

    private byte[] Descriptor(byte type)
    {
        byte protocol = Keyboard ? (byte)1 : (byte)2;
        byte[] hid = [9, 0x21, 0x11, 1, 0, 1, 0x22, (byte)ReportDescriptor.Length, 0];
        return type switch
        {
            1 => [18,1,0x10,1,0,0,0,8,0xFE,0xCA,protocol,0,0,1,0,0,0,1],
            2 => [9,2,34,0,1,1,0,0x80,25, 9,4,0,0,1,3,1,protocol,0,
                  ..hid, 7,5,0x81,3,8,0,10],
            3 => [4,3,9,4],
            0x21 => hid,
            0x22 => ReportDescriptor,
            _ => [],
        };
    }
    private bool BeginControl(ReadOnlySpan<byte> setup)
    {
        _setup = setup.ToArray(); _controlOffset = 0; _data1 = true; _reply = []; _output = [];
        byte type = setup[0], request = setup[1];
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(setup[2..]);
        ushort index = BinaryPrimitives.ReadUInt16LittleEndian(setup[4..]);
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(setup[6..]);
        switch ((type, request))
        {
            case (0x80, 6) when (value & 255) == 0:
            case (0x81, 6) when (value & 255) == 0 && index == 0:
                _reply = Descriptor((byte)(value >> 8));
                if (_reply.Length == 0) return false;
                break;
            case (0, 5) when value < 128 && index == 0 && length == 0: break;
            case (0, 9) when value <= 1 && index == 0 && length == 0: break;
            case (0x80, 8) when value == 0 && index == 0: _reply = [Configuration]; break;
            case (0x80, 0) when value == 0 && index == 0: _reply = [0,0]; break;
            case (0x81, 0) when value == 0 && index == 0: _reply = [0,0]; break;
            case (0x82, 0) when value == 0 && index is 0 or 0x81: _reply = [(byte)(index == 0x81 && _halted ? 1 : 0),0]; break;
            case (2, 1 or 3) when value == 0 && index == 0x81 && length == 0: break;
            case (0x81, 10) when index == 0 && value == 0: _reply = [0]; break;
            case (1, 11) when index == 0 && value == 0 && length == 0: break;
            case (0xA1, 1) when value == 0x100 && index == 0: _reply = Report(false); break;
            case (0xA1, 2) when value == 0 && index == 0: _reply = [_idle]; break;
            case (0xA1, 3) when value == 0 && index == 0: _reply = [_protocol]; break;
            case (0x21, 9) when Keyboard && value == 0x200 && index == 0 && length == 1: _output = new byte[1]; break;
            case (0x21, 10) when (value & 255) == 0 && index == 0 && length == 0: break;
            case (0x21, 11) when value <= 1 && index == 0 && length == 0: break;
            default: return false;
        }
        if (_reply.Length > length) _reply = _reply[..length];
        if (length == 0)
        {
            _controlStage = ControlStage.StatusIn;
        }
        else if ((type & 0x80) != 0)
        {
            _controlStage = ControlStage.DataIn;
        }
        else
        {
            _controlStage = ControlStage.DataOut;
        }
        return true;
    }
    private void CommitControl()
    {
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_setup.AsSpan(2));
        switch ((_setup[0], _setup[1]))
        {
            case (0, 5): Address = (byte)value; break;
            case (0, 9): Configuration = (byte)value; _dirty = true; _halted = _interruptData1 = false; _lastReport = []; break;
            case (2, 1): _halted = _interruptData1 = false; _lastReport = []; break;
            case (2, 3): _halted = true; break;
            case (0x21, 9): Leds = (byte)(_output[0] & 31); break;
            case (0x21, 10): _idle = (byte)(value >> 8); _idleMicroseconds = 0; break;
            case (0x21, 11): _protocol = (byte)value; _dirty = true; break;
        }
        _controlStage = ControlStage.Idle;
    }

    public UsbTransactionResult Transfer(byte pid, byte endpoint, bool data1, Span<byte> data)
    {
        if (endpoint == 1 && pid == 0x69)
        {
            if (Configuration == 0) return new(UsbHandshake.Nak);
            if (_halted) return new(UsbHandshake.Stall);
            if (data1 != _interruptData1)
            {
                if (_lastReport.Length == 0 || data.Length < _lastReport.Length) return new(UsbHandshake.Nak);
                _lastReport.CopyTo(data);
                return new(UsbHandshake.Ack, _lastReport.Length);
            }
            if (!_dirty && (_idle == 0 || _idleMicroseconds < _idle * 4000.0)) return new(UsbHandshake.Nak);
            byte[] report = Report(false);
            if (data.Length < report.Length) return new(UsbHandshake.Stall);
            _lastReport = Report(true); _lastReport.CopyTo(data); _idleMicroseconds = 0;
            _interruptData1 = !_interruptData1;
            _dirty = Keyboard ? _keys.Remove(72) : (_pendingDeltaX != 0 || _pendingDeltaY != 0 || _pendingWheelDelta != 0);
            return new(UsbHandshake.Ack, report.Length);
        }
        if (endpoint != 0) return new(UsbHandshake.Stall);
        if (pid == 0x2D)
        {
            _controlStage = ControlStage.Idle;
            return data.Length == 8 && !data1 && BeginControl(data)
                ? new(UsbHandshake.Ack, 8) : new(UsbHandshake.Stall);
        }
        if (_controlStage == ControlStage.StatusIn && pid == 0x69 && data1 && data.Length == 0)
        { CommitControl(); return new(UsbHandshake.Ack); }
        if (_controlStage == ControlStage.StatusOut && pid == 0xE1 && data1 && data.Length == 0)
        { CommitControl(); return new(UsbHandshake.Ack); }
        if (data1 != _data1) return new(UsbHandshake.Nak);
        if (_controlStage == ControlStage.DataIn && pid == 0x69)
        {
            int count = Math.Min(Math.Min(data.Length, 8), _reply.Length - _controlOffset);
            _reply.AsSpan(_controlOffset, count).CopyTo(data); _controlOffset += count; _data1 = !_data1;
            int requested = BinaryPrimitives.ReadUInt16LittleEndian(_setup.AsSpan(6));
            if (count < 8 || _controlOffset >= requested) _controlStage = ControlStage.StatusOut;
            return new(UsbHandshake.Ack, count);
        }
        if (_controlStage == ControlStage.DataOut && pid == 0xE1 && data.Length <= _output.Length - _controlOffset)
        {
            data.CopyTo(_output.AsSpan(_controlOffset)); _controlOffset += data.Length; _data1 = !_data1;
            if (_controlOffset == _output.Length) _controlStage = ControlStage.StatusIn;
            return new(UsbHandshake.Ack, data.Length);
        }
        return new(UsbHandshake.Stall);
    }

    public byte[] Capture()
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write(Keyboard); w.Write(Address); w.Write(Configuration); w.Write(Leds);
        w.Write(_protocol); w.Write(_idle); w.Write(_dirty); w.Write(_halted); w.Write(_buttons);
        w.Write(_pendingDeltaX); w.Write(_pendingDeltaY); w.Write(_pendingWheelDelta); w.Write(_idleMicroseconds);
        w.Write(_controlOffset); w.Write((int)_controlStage); w.Write(_data1);
        w.Write(_interruptData1); w.Write(_extendedScan); w.Write(_remainingPauseScanBytes);
        foreach (byte[] bytes in new[] { _keys.Order().ToArray(), _reply, _setup, _output, _lastReport })
        { w.Write(bytes.Length); w.Write(bytes); }
        return stream.ToArray();
    }
    public void Restore(ReadOnlySpan<byte> data)
    {
        using var r = new BinaryReader(new MemoryStream(data.ToArray()));
        if (r.ReadBoolean() != Keyboard) throw new InvalidDataException("USB HID snapshot kind differs.");
        Address = r.ReadByte(); Configuration = r.ReadByte(); Leds = r.ReadByte();
        _protocol = r.ReadByte(); _idle = r.ReadByte(); _dirty = r.ReadBoolean(); _halted = r.ReadBoolean(); _buttons = r.ReadByte();
        _pendingDeltaX = r.ReadInt32(); _pendingDeltaY = r.ReadInt32(); _pendingWheelDelta = r.ReadInt32(); _idleMicroseconds = r.ReadDouble();
        _controlOffset = r.ReadInt32(); _controlStage = (ControlStage)r.ReadInt32(); _data1 = r.ReadBoolean();
        _interruptData1 = r.ReadBoolean(); _extendedScan = r.ReadBoolean(); _remainingPauseScanBytes = r.ReadInt32();
        byte[] ReadBlock()
        {
            int length = r.ReadInt32();
            if (length < 0 || length > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("Invalid HID snapshot.");
            return r.ReadBytes(length);
        }
        _keys.Clear(); _keys.UnionWith(ReadBlock()); _reply = ReadBlock(); _setup = ReadBlock(); _output = ReadBlock(); _lastReport = ReadBlock();
        if (r.BaseStream.Position != r.BaseStream.Length || _controlOffset < 0 || (int)_controlStage is < 0 or > 4)
            throw new InvalidDataException("Invalid HID snapshot.");
    }
}
