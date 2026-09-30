using System.Buffers.Binary;

namespace Aurora86.Core.Devices.Usb;

/// <summary>
/// A low-speed boot HID device with an eight-byte control endpoint and one
/// interrupt IN endpoint. Address/configuration changes commit only after
/// the status packet, so the SET_ADDRESS status still uses the old address.
/// </summary>
public sealed class UsbHidDevice : IUsbDevice
{
    // Endpoints this device answers on; every other endpoint STALLs.
    private const byte ControlEndpoint = 0;
    private const byte InterruptInEndpoint = 1;

    // USB packet IDs, as they appear on the wire (USB 2.0 section 8.3.1).
    private const byte PidSetup = 0x2D;
    private const byte PidIn = 0x69;
    private const byte PidOut = 0xE1;

    // bmRequestType bytes this device recognises: direction, standard/class
    // type and recipient combined into the one byte a SETUP packet carries.
    private const byte RequestTypeStandardDeviceOut = 0x00;
    private const byte RequestTypeStandardInterfaceOut = 0x01;
    private const byte RequestTypeStandardEndpointOut = 0x02;
    private const byte RequestTypeStandardDeviceIn = 0x80;
    private const byte RequestTypeStandardInterfaceIn = 0x81;
    private const byte RequestTypeStandardEndpointIn = 0x82;
    private const byte RequestTypeClassInterfaceOut = 0x21;
    private const byte RequestTypeClassInterfaceIn = 0xA1;
    private const byte RequestDirectionDeviceToHostBit = 0x80; // bit 7 of bmRequestType

    // Standard (USB 2.0 chapter 9) bRequest values.
    private const byte RequestGetStatus = 0;
    private const byte RequestClearFeature = 1;
    private const byte RequestSetFeature = 3;
    private const byte RequestSetAddress = 5;
    private const byte RequestGetDescriptor = 6;
    private const byte RequestGetConfiguration = 8;
    private const byte RequestSetConfiguration = 9;
    private const byte RequestGetInterface = 10;
    private const byte RequestSetInterface = 11;

    // HID class-specific bRequest values (USB HID 1.11 section 7.2). These
    // share numeric space with the standard requests above but never the
    // same bmRequestType, so the (type, request) pair stays unambiguous.
    private const byte HidGetReport = 1;
    private const byte HidGetIdle = 2;
    private const byte HidGetProtocol = 3;
    private const byte HidSetReport = 9;
    private const byte HidSetIdle = 10;
    private const byte HidSetProtocol = 11;

    // GET_DESCRIPTOR's wValue high byte.
    private const byte DescriptorTypeDevice = 1;
    private const byte DescriptorTypeConfiguration = 2;
    private const byte DescriptorTypeString = 3;
    private const byte DescriptorTypeHid = 0x21;
    private const byte DescriptorTypeReport = 0x22;
    private const int DescriptorTypeShift = 8;

    // bInterfaceProtocol, the boot-device protocol code (HID 1.11 table 4).
    private const byte InterfaceProtocolKeyboard = 1;
    private const byte InterfaceProtocolMouse = 2;

    // SET_PROTOCOL/GET_PROTOCOL values (HID 1.11 section 7.2.5/7.2.6).
    private const byte BootProtocol = 0;
    private const byte ReportProtocol = 1;

    // Boot keyboard report layout (HID 1.11 appendix B.1): byte 0 holds the
    // eight modifier keys as one bit each, byte 1 is reserved, and up to six
    // key usages follow. ErrorRollOver fills every slot once more than six
    // keys are down at once, exactly as a real boot keyboard reports it.
    private const byte ModifierUsageLow = 0xE0;
    private const byte ModifierUsageHigh = 0xE7;
    private const int KeyboardReportKeyOffset = 2;
    private const int KeyboardReportKeySlots = 6;
    private const byte ErrorRollOverUsage = 1;

    // Boot mouse report layout (HID 1.11 appendix B.2): a button byte
    // (3 buttons used) followed by X/Y, and a wheel byte once the host has
    // selected report protocol instead of the boot protocol.
    private const byte MouseButtonMask = 0x07;
    private const int MaxPendingDelta = 32767; // keeps host-side accumulation from ever overflowing a report byte many times over
    private const int MaxReportDelta = 127;    // a report byte's signed range

    public bool Keyboard { get; }
    public byte Address { get; private set; }
    public byte Configuration { get; private set; }
    public bool LowSpeed => true;
    public byte Leds { get; private set; }

    private byte _protocol = ReportProtocol;
    private byte _idle;
    private bool _dirty = true;
    private bool _halted;
    private bool _interruptData1;
    private byte[] _lastReport = [];
    private bool _extendedScan;
    private int _remainingPauseScanBytes;
    private readonly HashSet<byte> _keys = [];
    private byte _buttons;
    private int _pendingDeltaX;
    private int _pendingDeltaY;
    private int _pendingWheelDelta;
    private double _idleMicroseconds;
    private byte[] _reply = [];
    private byte[] _setup = [];
    private int _controlOffset;
    private ControlStage _controlStage;
    private bool _data1 = true;
    private byte[] _output = [];

    // Explicit values preserve the existing snapshot representation.
    private enum ControlStage
    {
        Idle = 0,
        DataIn = 1,
        DataOut = 2,
        StatusIn = 3,
        StatusOut = 4,
    }

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

    // Host frontends already submit AT set-1 scan codes. Translate their
    // physical key identities, rather than characters from a host layout.
    private static readonly byte[] Set1Usages = [
        0,41,30,31,32,33,34,35,36,37,38,39,45,46,42,43,
        20,26,8,21,23,28,24,12,18,19,47,48,40,224,4,22,
        7,9,10,11,13,14,15,51,52,53,225,49,29,27,6,25,
        5,17,16,54,55,56,229,85,226,44,57,58,59,60,61,62,
        63,64,65,66,67,83,71,95,96,97,86,92,93,94,87,89,
        90,91,98,99,0,0,100,68,69];

    public UsbHidDevice(bool keyboard) => Keyboard = keyboard;

    public void PushScanCode(byte code)
    {
        if (_remainingPauseScanBytes > 0)
        {
            if (--_remainingPauseScanBytes == 0)
            {
                SetKey(72, true);
            }
            return;
        }
        if (code == 0xE1)
        {
            _remainingPauseScanBytes = 5;
            _extendedScan = false;
            return;
        }
        if (code == 0xE0)
        {
            _extendedScan = true;
            return;
        }

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
        if (!Keyboard || usage < 4)
        {
            return;
        }

        if (pressed ? _keys.Add(usage) : _keys.Remove(usage))
        {
            _dirty = true;
        }
    }

    public void Move(int x, int y, int wheel = 0)
    {
        if (Keyboard)
        {
            return;
        }

        _pendingDeltaX = (int)Math.Clamp((long)_pendingDeltaX + x, -MaxPendingDelta, MaxPendingDelta);
        _pendingDeltaY = (int)Math.Clamp((long)_pendingDeltaY + y, -MaxPendingDelta, MaxPendingDelta);
        _pendingWheelDelta = (int)Math.Clamp((long)_pendingWheelDelta + wheel, -MaxPendingDelta, MaxPendingDelta);
        if (x != 0 || y != 0 || wheel != 0)
        {
            _dirty = true;
        }
    }

    public void SetButton(byte button, bool pressed) => SetButtons(pressed ? (byte)(_buttons | button) : (byte)(_buttons & ~button));

    public void SetButtons(byte buttons)
    {
        buttons &= MouseButtonMask;
        if (_buttons != buttons)
        {
            _buttons = buttons;
            _dirty = true;
        }
    }

    public void Advance(double microseconds) => _idleMicroseconds += microseconds;

    public void Reset()
    {
        Address = Configuration = Leds = _idle = _buttons = 0;
        _protocol = ReportProtocol;
        _dirty = true;
        _halted = _interruptData1 = _extendedScan = false;
        _remainingPauseScanBytes = 0;
        _lastReport = [];
        _keys.Clear();
        _pendingDeltaX = _pendingDeltaY = _pendingWheelDelta = _controlOffset = 0;
        _controlStage = ControlStage.Idle;
        _idleMicroseconds = 0;
        _setup = _reply = _output = [];
        _data1 = true;
    }

    private byte[] Report(bool consume)
    {
        if (Keyboard)
        {
            byte[] report = new byte[8];
            foreach (byte key in _keys.Where(k => k is >= ModifierUsageLow and <= ModifierUsageHigh))
            {
                report[0] |= (byte)(1 << (key - ModifierUsageLow));
            }
            byte[] keys = _keys.Where(k => k < ModifierUsageLow).Order().ToArray();
            for (int i = 0; i < KeyboardReportKeySlots; i++)
            {
                report[i + KeyboardReportKeyOffset] = keys.Length > KeyboardReportKeySlots ? ErrorRollOverUsage
                    : i < keys.Length ? keys[i] : (byte)0;
            }
            return report;
        }

        int x = Math.Clamp(_pendingDeltaX, -MaxReportDelta, MaxReportDelta);
        int y = Math.Clamp(_pendingDeltaY, -MaxReportDelta, MaxReportDelta);
        int wheel = Math.Clamp(_pendingWheelDelta, -MaxReportDelta, MaxReportDelta);
        byte[] mouse = _protocol == BootProtocol ? [_buttons, unchecked((byte)x), unchecked((byte)y)]
            : [_buttons, unchecked((byte)x), unchecked((byte)y), unchecked((byte)wheel)];
        if (consume)
        {
            _pendingDeltaX -= x;
            _pendingDeltaY -= y;
            _pendingWheelDelta = _protocol == BootProtocol ? 0 : _pendingWheelDelta - wheel;
        }
        return mouse;
    }

    /// <summary>
    /// One of the four descriptors this device answers GET_DESCRIPTOR with:
    /// the device descriptor (idVendor/idProduct 0xCAFE/0x0001-2, a fictitious
    /// pair reserved for this emulator), the configuration descriptor (one
    /// interface, one interrupt IN endpoint, with the HID descriptor embedded
    /// between them as the spec requires), the language ID string descriptor,
    /// the HID descriptor alone, and the report descriptor itself.
    /// </summary>
    private byte[] Descriptor(byte type)
    {
        byte interfaceProtocol = Keyboard ? InterfaceProtocolKeyboard : InterfaceProtocolMouse;
        byte[] hid = [9, DescriptorTypeHid, 0x11, 1, 0, 1, DescriptorTypeReport, (byte)ReportDescriptor.Length, 0];
        return type switch
        {
            DescriptorTypeDevice => [18, 1, 0x10, 1, 0, 0, 0, 8, 0xFE, 0xCA, interfaceProtocol, 0, 0, 1, 0, 0, 0, 1],
            DescriptorTypeConfiguration => [9, 2, 34, 0, 1, 1, 0, 0x80, 25, 9, 4, 0, 0, 1, 3, 1, interfaceProtocol, 0,
                  ..hid, 7, 5, 0x80 | InterruptInEndpoint, 3, 8, 0, 10],
            DescriptorTypeString => [4, 3, 9, 4],
            DescriptorTypeHid => hid,
            DescriptorTypeReport => ReportDescriptor,
            _ => [],
        };
    }

    private bool BeginControl(ReadOnlySpan<byte> setup)
    {
        _setup = setup.ToArray();
        _controlOffset = 0;
        _data1 = true;
        _reply = [];
        _output = [];
        byte type = setup[0], request = setup[1];
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(setup[2..]);
        ushort index = BinaryPrimitives.ReadUInt16LittleEndian(setup[4..]);
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(setup[6..]);
        switch ((type, request))
        {
            case (RequestTypeStandardDeviceIn, RequestGetDescriptor) when (value & 0xFF) == 0:
            case (RequestTypeStandardInterfaceIn, RequestGetDescriptor) when (value & 0xFF) == 0 && index == 0:
                _reply = Descriptor((byte)(value >> DescriptorTypeShift));
                if (_reply.Length == 0)
                {
                    return false;
                }
                break;
            case (RequestTypeStandardDeviceOut, RequestSetAddress) when value < 128 && index == 0 && length == 0: break;
            case (RequestTypeStandardDeviceOut, RequestSetConfiguration) when value <= 1 && index == 0 && length == 0: break;
            case (RequestTypeStandardDeviceIn, RequestGetConfiguration) when value == 0 && index == 0: _reply = [Configuration]; break;
            case (RequestTypeStandardDeviceIn, RequestGetStatus) when value == 0 && index == 0: _reply = [0, 0]; break;
            case (RequestTypeStandardInterfaceIn, RequestGetStatus) when value == 0 && index == 0: _reply = [0, 0]; break;
            case (RequestTypeStandardEndpointIn, RequestGetStatus) when value == 0 && index is 0 or (0x80 | InterruptInEndpoint):
                _reply = [(byte)(index == (0x80 | InterruptInEndpoint) && _halted ? 1 : 0), 0];
                break;
            case (RequestTypeStandardEndpointOut, RequestClearFeature or RequestSetFeature)
                when value == 0 && index == (0x80 | InterruptInEndpoint) && length == 0: break;
            case (RequestTypeStandardInterfaceIn, RequestGetInterface) when index == 0 && value == 0: _reply = [0]; break;
            case (RequestTypeStandardInterfaceOut, RequestSetInterface) when index == 0 && value == 0 && length == 0: break;
            case (RequestTypeClassInterfaceIn, HidGetReport) when value == 0x100 && index == 0: _reply = Report(false); break;
            case (RequestTypeClassInterfaceIn, HidGetIdle) when value == 0 && index == 0: _reply = [_idle]; break;
            case (RequestTypeClassInterfaceIn, HidGetProtocol) when value == 0 && index == 0: _reply = [_protocol]; break;
            case (RequestTypeClassInterfaceOut, HidSetReport) when Keyboard && value == 0x200 && index == 0 && length == 1:
                _output = new byte[1];
                break;
            case (RequestTypeClassInterfaceOut, HidSetIdle) when (value & 0xFF) == 0 && index == 0 && length == 0: break;
            case (RequestTypeClassInterfaceOut, HidSetProtocol) when value <= 1 && index == 0 && length == 0: break;
            default: return false;
        }
        if (_reply.Length > length)
        {
            _reply = _reply[..length];
        }
        if (length == 0)
        {
            _controlStage = ControlStage.StatusIn;
        }
        else if ((type & RequestDirectionDeviceToHostBit) != 0)
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
            case (RequestTypeStandardDeviceOut, RequestSetAddress): Address = (byte)value; break;
            case (RequestTypeStandardDeviceOut, RequestSetConfiguration):
                Configuration = (byte)value;
                _dirty = true;
                _halted = _interruptData1 = false;
                _lastReport = [];
                break;
            case (RequestTypeStandardEndpointOut, RequestClearFeature): _halted = _interruptData1 = false; _lastReport = []; break;
            case (RequestTypeStandardEndpointOut, RequestSetFeature): _halted = true; break;
            case (RequestTypeClassInterfaceOut, HidSetReport): Leds = (byte)(_output[0] & 31); break;
            case (RequestTypeClassInterfaceOut, HidSetIdle): _idle = (byte)(value >> 8); _idleMicroseconds = 0; break;
            case (RequestTypeClassInterfaceOut, HidSetProtocol): _protocol = (byte)value; _dirty = true; break;
        }
        _controlStage = ControlStage.Idle;
    }

    public UsbTransactionResult Transfer(byte pid, byte endpoint, bool data1, Span<byte> data)
    {
        if (endpoint == InterruptInEndpoint && pid == PidIn)
        {
            if (Configuration == 0)
            {
                return new(UsbHandshake.Nak);
            }
            if (_halted)
            {
                return new(UsbHandshake.Stall);
            }
            if (data1 != _interruptData1)
            {
                if (_lastReport.Length == 0 || data.Length < _lastReport.Length)
                {
                    return new(UsbHandshake.Nak);
                }
                _lastReport.CopyTo(data);
                return new(UsbHandshake.Ack, _lastReport.Length);
            }
            if (!_dirty && (_idle == 0 || _idleMicroseconds < _idle * 4000.0))
            {
                return new(UsbHandshake.Nak);
            }
            byte[] report = Report(false);
            if (data.Length < report.Length)
            {
                return new(UsbHandshake.Stall);
            }
            _lastReport = Report(true);
            _lastReport.CopyTo(data);
            _idleMicroseconds = 0;
            _interruptData1 = !_interruptData1;
            _dirty = Keyboard ? _keys.Remove(72) : (_pendingDeltaX != 0 || _pendingDeltaY != 0 || _pendingWheelDelta != 0);
            return new(UsbHandshake.Ack, report.Length);
        }

        if (endpoint != ControlEndpoint)
        {
            return new(UsbHandshake.Stall);
        }
        if (pid == PidSetup)
        {
            _controlStage = ControlStage.Idle;
            return data.Length == 8 && !data1 && BeginControl(data)
                ? new(UsbHandshake.Ack, 8) : new(UsbHandshake.Stall);
        }
        if (_controlStage == ControlStage.StatusIn && pid == PidIn && data1 && data.Length == 0)
        {
            CommitControl();
            return new(UsbHandshake.Ack);
        }
        if (_controlStage == ControlStage.StatusOut && pid == PidOut && data1 && data.Length == 0)
        {
            CommitControl();
            return new(UsbHandshake.Ack);
        }
        if (data1 != _data1)
        {
            return new(UsbHandshake.Nak);
        }
        if (_controlStage == ControlStage.DataIn && pid == PidIn)
        {
            int count = Math.Min(Math.Min(data.Length, 8), _reply.Length - _controlOffset);
            _reply.AsSpan(_controlOffset, count).CopyTo(data);
            _controlOffset += count;
            _data1 = !_data1;
            int requested = BinaryPrimitives.ReadUInt16LittleEndian(_setup.AsSpan(6));
            if (count < 8 || _controlOffset >= requested)
            {
                _controlStage = ControlStage.StatusOut;
            }
            return new(UsbHandshake.Ack, count);
        }
        if (_controlStage == ControlStage.DataOut && pid == PidOut && data.Length <= _output.Length - _controlOffset)
        {
            data.CopyTo(_output.AsSpan(_controlOffset));
            _controlOffset += data.Length;
            _data1 = !_data1;
            if (_controlOffset == _output.Length)
            {
                _controlStage = ControlStage.StatusIn;
            }
            return new(UsbHandshake.Ack, data.Length);
        }
        return new(UsbHandshake.Stall);
    }

    public byte[] Capture()
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(Keyboard);
        w.Write(Address);
        w.Write(Configuration);
        w.Write(Leds);
        w.Write(_protocol);
        w.Write(_idle);
        w.Write(_dirty);
        w.Write(_halted);
        w.Write(_buttons);
        w.Write(_pendingDeltaX);
        w.Write(_pendingDeltaY);
        w.Write(_pendingWheelDelta);
        w.Write(_idleMicroseconds);
        w.Write(_controlOffset);
        w.Write((int)_controlStage);
        w.Write(_data1);
        w.Write(_interruptData1);
        w.Write(_extendedScan);
        w.Write(_remainingPauseScanBytes);
        foreach (byte[] bytes in new[] { _keys.Order().ToArray(), _reply, _setup, _output, _lastReport })
        {
            w.Write(bytes.Length);
            w.Write(bytes);
        }
        return stream.ToArray();
    }

    public void Restore(ReadOnlySpan<byte> data)
    {
        using var r = new BinaryReader(new MemoryStream(data.ToArray()));
        if (r.ReadBoolean() != Keyboard)
        {
            throw new InvalidDataException("USB HID snapshot kind differs.");
        }

        Address = r.ReadByte();
        Configuration = r.ReadByte();
        Leds = r.ReadByte();
        _protocol = r.ReadByte();
        _idle = r.ReadByte();
        _dirty = r.ReadBoolean();
        _halted = r.ReadBoolean();
        _buttons = r.ReadByte();
        _pendingDeltaX = r.ReadInt32();
        _pendingDeltaY = r.ReadInt32();
        _pendingWheelDelta = r.ReadInt32();
        _idleMicroseconds = r.ReadDouble();
        _controlOffset = r.ReadInt32();
        _controlStage = (ControlStage)r.ReadInt32();
        _data1 = r.ReadBoolean();
        _interruptData1 = r.ReadBoolean();
        _extendedScan = r.ReadBoolean();
        _remainingPauseScanBytes = r.ReadInt32();

        byte[] ReadBlock()
        {
            int length = r.ReadInt32();
            if (length < 0 || length > r.BaseStream.Length - r.BaseStream.Position)
            {
                throw new InvalidDataException("Invalid HID snapshot.");
            }
            return r.ReadBytes(length);
        }

        _keys.Clear();
        _keys.UnionWith(ReadBlock());
        _reply = ReadBlock();
        _setup = ReadBlock();
        _output = ReadBlock();
        _lastReport = ReadBlock();
        if (r.BaseStream.Position != r.BaseStream.Length || _controlOffset < 0 || (int)_controlStage is < 0 or > 4)
        {
            throw new InvalidDataException("Invalid HID snapshot.");
        }
    }
}
