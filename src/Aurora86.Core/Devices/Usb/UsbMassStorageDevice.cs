using System.Buffers.Binary;
using System.Text;

namespace Aurora86.Core.Devices.Usb;

/// <summary>
/// A single-LUN removable disk using USB bulk-only transport. The backing
/// stream is an image, never an IDE attachment. Commands validate their LBA
/// and length before allocating buffers or touching the image; incomplete
/// WRITE commands are not committed to the host file.
/// </summary>
public sealed class UsbMassStorageDevice : IUsbDevice, IDisposable
{
    // Endpoints this device answers on; every other endpoint STALLs.
    private const byte ControlEndpoint = 0;
    private const byte BulkInEndpoint = 1;
    private const byte BulkOutEndpoint = 2;
    private const byte BulkInEndpointAddress = 0x80 | BulkInEndpoint; // 0x81, the address form used in descriptors and GET_STATUS's wIndex

    // USB packet IDs, as they appear on the wire (USB 2.0 section 8.3.1).
    private const byte PidSetup = 0x2D;
    private const byte PidIn = 0x69;
    private const byte PidOut = 0xE1;

    // bmRequestType bytes this device recognises for the control endpoint.
    private const byte RequestTypeStandardDeviceOut = 0x00;
    private const byte RequestTypeStandardInterfaceOut = 0x01;
    private const byte RequestTypeStandardEndpointOut = 0x02;
    private const byte RequestTypeStandardDeviceIn = 0x80;
    private const byte RequestTypeStandardInterfaceIn = 0x81;
    private const byte RequestTypeStandardEndpointIn = 0x82;
    private const byte RequestTypeBulkOnlyClassOut = 0x21;
    private const byte RequestTypeBulkOnlyClassIn = 0xA1;

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

    // Bulk-Only Transport class-specific requests (USB Mass Storage Bulk-Only
    // Transport 1.0, section 3): this single-LUN device always answers
    // GET_MAX_LUN with zero and treats a reset as a fresh bulk-only session.
    private const byte BulkOnlyRequestGetMaxLun = 0xFE;
    private const byte BulkOnlyRequestReset = 0xFF;

    // GET_DESCRIPTOR's wValue: descriptor type in the high byte, index in
    // the low byte.
    private const ushort DescriptorValueDevice = 0x100;
    private const ushort DescriptorValueConfiguration = 0x200;
    private const ushort DescriptorValueLanguageIds = 0x300;
    private const ushort DescriptorValueManufacturerString = 0x301;
    private const ushort DescriptorValueProductString = 0x302;
    private const ushort DescriptorValueSerialString = 0x303;
    private const byte DescriptorTypeString = 3;

    // USB 2.0 section 9.6.2 (Device Qualifier) and 9.6.4 (Other Speed
    // Configuration): a high-speed-capable device answers both, describing
    // what its device/configuration descriptors would read at the speed it
    // is not currently running at.
    private const ushort DescriptorValueDeviceQualifier = 0x600;
    private const ushort DescriptorValueOtherSpeedConfiguration = 0x700;
    private const byte DescriptorTypeDeviceQualifier = 6;
    private const byte DescriptorTypeOtherSpeedConfiguration = 7;
    private const byte DescriptorTypeConfiguration = 2;

    // Interface descriptor fields identifying this as a Bulk-Only Transport
    // SCSI mass storage function (USB Mass Storage Class specification).
    private const byte InterfaceClassMassStorage = 8;
    private const byte InterfaceSubClassScsiTransparent = 6;
    private const byte InterfaceProtocolBulkOnlyTransport = 0x50;
    private const byte EndpointAttributesBulk = 2;

    // Endpoint 0's packet size never depends on speed (USB 2.0 section 5.5.3
    // fixes it at 64 bytes for every device, high speed included); the bulk
    // endpoints step up from full speed's 64 bytes to high speed's 512
    // (USB 2.0 section 5.8.3) once SelectSpeed tells this device which port
    // it negotiated on.
    private const int ControlEndpointMaxPacketSize = 64;
    private const int FullSpeedBulkMaxPacketSize = 64;
    private const int HighSpeedBulkMaxPacketSize = 512;

    // Command Block Wrapper / Command Status Wrapper (Bulk-Only Transport
    // 1.0, sections 5.1 and 5.2).
    private const uint CbwSignature = 0x43425355; // "USBC"
    private const uint CswSignature = 0x53425355; // "USBS"
    private const int CbwLength = 31;
    private const int CswLength = 13;
    private const byte CbwDirectionInBit = 0x80;   // bit 7 of bmCBWFlags
    private const byte CbwFlagsReservedMask = 0x7F; // bits 6:0 of bmCBWFlags must read back zero
    private const byte SupportedLogicalUnit = 0;    // this device answers only LUN 0
    private const byte MinCommandBlockLength = 1;
    private const byte MaxCommandBlockLength = 16;
    private const byte CommandStatusPass = 0;
    private const byte CommandStatusFail = 1;
    private const byte CommandStatusPhaseError = 2;
    private const int SixByteCdbLength = 6;
    private const int TenByteCdbLength = 10;

    // SCSI primary and block command op codes (SPC/SBC) this disk answers.
    private const byte ScsiTestUnitReady = 0x00;
    private const byte ScsiRequestSense = 0x03;
    private const byte ScsiInquiry = 0x12;
    private const byte ScsiModeSense6 = 0x1A;
    private const byte ScsiStartStopUnit = 0x1B;
    private const byte ScsiPreventAllowMediumRemoval = 0x1E;
    private const byte ScsiReadFormatCapacities = 0x23;
    private const byte ScsiReadCapacity10 = 0x25;
    private const byte ScsiRead10 = 0x28;
    private const byte ScsiWrite10 = 0x2A;
    private const byte ScsiSynchronizeCache10 = 0x35;
    private const byte ScsiModeSense10 = 0x5A;

    // START STOP UNIT's control byte (SBC-2): LoEj asserted makes Start
    // decide whether the medium becomes present or is removed.
    private const byte StartStopUnitStartBit = 1 << 0;
    private const byte StartStopUnitLoEjBit = 1 << 1;

    // INQUIRY's byte 1: EVPD (Enable Vital Product Data) is not supported.
    private const byte InquiryEvpdBit = 1 << 0;

    // Sense keys and additional sense codes (SPC REQUEST SENSE, fixed format).
    private const byte SenseKeyNotReady = 2;
    private const byte SenseKeyMediumError = 3;
    private const byte SenseKeyIllegalRequest = 5;
    private const byte SenseKeyDataProtect = 7;
    private const byte AscWriteError = 0x0C;
    private const byte AscUnrecoveredReadError = 0x11;
    private const byte AscInvalidCommandOperationCode = 0x20;
    private const byte AscLogicalBlockAddressOutOfRange = 0x21;
    private const byte AscInvalidFieldInCdb = 0x24;
    private const byte AscWriteProtected = 0x27;
    private const byte AscMediumNotPresent = 0x3A;

    private readonly Stream _image;
    public bool ReadOnly { get; }
    public string? SourcePath { get; }
    public byte Address { get; private set; }
    public byte Configuration { get; private set; }
    public bool LowSpeed => false;

    /// <summary>This device answers the high-speed chirp during port reset; see <see cref="SelectSpeed"/>.</summary>
    public bool HighSpeedCapable => true;
    public long SectorCount => _image.Length / 512;

    /// <summary>The bulk endpoints' current packet size: 512 bytes at high speed, 64 at full speed (USB 2.0 section 5.8.3).</summary>
    private int BulkPacketSize => _highSpeed ? HighSpeedBulkMaxPacketSize : FullSpeedBulkMaxPacketSize;

    private bool _highSpeed;
    private bool _medium = true;
    private bool _haltIn;
    private bool _haltOut;
    private bool _needsReset;
    private bool _inToggle;
    private bool _outToggle;
    private byte[] _lastIn = [];
    private byte[] _data = [];
    private byte[] _csw = [];
    private byte[] _setup = [];
    private byte[] _controlReply = [];
    private int _position;
    private int _phase;
    private int _cswPosition;
    private int _controlPosition;
    private int _controlStage;
    private bool _controlToggle = true;
    private bool _hostIn;
    private bool _zeroPacket;
    private uint _tag;
    private uint _hostLength;
    private uint _processed;
    private uint _writeLba;
    private int _writeLength;
    private byte _commandStatus;
    private byte _senseKey;
    private byte _asc;

    public UsbMassStorageDevice(Stream image, bool readOnly = false, string? sourcePath = null)
    {
        if (!image.CanRead || !image.CanSeek || image.Length < 512 || image.Length % 512 != 0)
        {
            throw new ArgumentException("USB disk images must contain complete 512-byte sectors.", nameof(image));
        }

        _image = image;
        ReadOnly = readOnly || !image.CanWrite;
        SourcePath = sourcePath;
    }

    public static UsbMassStorageDevice Open(string path, bool readOnly = false)
    {
        var stream = new FileStream(path, FileMode.Open, readOnly ? FileAccess.Read : FileAccess.ReadWrite, FileShare.Read);
        try
        {
            return new(stream, readOnly, Path.GetFullPath(path));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose() => _image.Dispose();

    public void Reset()
    {
        Address = Configuration = 0;
        _haltIn = _haltOut = _needsReset = _inToggle = _outToggle = false;
        _highSpeed = false; // renegotiated by SelectSpeed once the port that issued this reset completes it
        ResetTransport();
        _controlStage = 0;
        _setup = _controlReply = [];
        _controlPosition = 0;
    }

    /// <summary>
    /// Told which speed the port reset that just completed negotiated: true
    /// on an EHCI port (this device answered the high-speed chirp), false on
    /// a full-speed UHCI one. Changes the bulk endpoints' packet size the
    /// configuration descriptor reports from here on; endpoint 0 is
    /// unaffected (USB 2.0 section 5.5.3 fixes it at 64 bytes regardless).
    /// </summary>
    public void SelectSpeed(bool highSpeed) => _highSpeed = highSpeed;

    private void ResetTransport()
    {
        _phase = _position = _cswPosition = _writeLength = 0;
        _data = _csw = [];
        _zeroPacket = false;
        _needsReset = false;
    }

    private void Fail(byte key, byte asc)
    {
        _senseKey = key;
        _asc = asc;
        _commandStatus = CommandStatusFail;
    }

    private bool Range(uint lba, int blocks) => lba <= SectorCount && blocks <= SectorCount - lba;

    private void FinishData()
    {
        _csw = new byte[CswLength];
        _cswPosition = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(_csw, CswSignature);
        BinaryPrimitives.WriteUInt32LittleEndian(_csw.AsSpan(4), _tag);
        BinaryPrimitives.WriteUInt32LittleEndian(_csw.AsSpan(8), _hostLength - _processed);
        _csw[12] = _commandStatus;
        _phase = 3;
    }

    private bool Command(ReadOnlySpan<byte> cbw)
    {
        if (cbw.Length != CbwLength || BinaryPrimitives.ReadUInt32LittleEndian(cbw) != CbwSignature
            || (cbw[12] & CbwFlagsReservedMask) != 0 || cbw[13] != SupportedLogicalUnit
            || cbw[14] is < MinCommandBlockLength or > MaxCommandBlockLength)
        {
            // A malformed wrapper leaves both bulk endpoints halted until
            // the driver issues a Bulk-Only Mass Storage Reset, exactly as
            // the transport spec requires.
            _needsReset = _haltIn = _haltOut = true;
            return false;
        }

        _tag = BinaryPrimitives.ReadUInt32LittleEndian(cbw[4..]);
        _hostLength = BinaryPrimitives.ReadUInt32LittleEndian(cbw[8..]);
        _hostIn = (cbw[12] & CbwDirectionInBit) != 0;
        _processed = 0;
        _commandStatus = CommandStatusPass;
        _writeLength = _position = 0;
        _data = [];
        _zeroPacket = false;

        ReadOnlySpan<byte> cdb = cbw.Slice(15, cbw[14]);
        byte op = cdb[0];
        int required = op is ScsiReadCapacity10 or ScsiRead10 or ScsiWrite10 or ScsiSynchronizeCache10
            or ScsiReadFormatCapacities or ScsiModeSense10 ? TenByteCdbLength : SixByteCdbLength;
        if (cdb.Length < required)
        {
            Fail(SenseKeyIllegalRequest, AscInvalidFieldInCdb);
            FinishData();
            return true;
        }

        bool output = op == ScsiWrite10;
        if (!_medium && op is not (ScsiInquiry or ScsiRequestSense or ScsiStartStopUnit))
        {
            Fail(SenseKeyNotReady, AscMediumNotPresent);
        }
        else
        {
            try
            {
                switch (op)
                {
                    case ScsiTestUnitReady:
                    case ScsiPreventAllowMediumRemoval:
                        break;

                    case ScsiSynchronizeCache10:
                        _image.Flush();
                        break;

                    case ScsiStartStopUnit:
                        if ((cdb[4] & StartStopUnitLoEjBit) != 0)
                        {
                            _medium = (cdb[4] & StartStopUnitStartBit) != 0;
                        }
                        break;

                    case ScsiInquiry:
                        if ((cdb[1] & InquiryEvpdBit) != 0)
                        {
                            Fail(SenseKeyIllegalRequest, AscInvalidFieldInCdb);
                            break;
                        }
                        // Standard INQUIRY data (SPC): peripheral type 0
                        // (direct-access block device), RMB (removable) set,
                        // ANSI version 2, response format 2, then the
                        // vendor/product/revision identification string.
                        _data = new byte[36];
                        _data[1] = 0x80;
                        _data[2] = 2;
                        _data[3] = 2;
                        _data[4] = 31;
                        Encoding.ASCII.GetBytes("AURORA  USB DISK        0001").CopyTo(_data, 8);
                        _data = _data[..Math.Min(_data.Length, cdb[4])];
                        break;

                    case ScsiRequestSense:
                        // Fixed-format sense data (SPC): current error,
                        // sense key, additional sense length 10, then ASC.
                        _data = new byte[18];
                        _data[0] = 0x70;
                        _data[2] = _senseKey;
                        _data[7] = 10;
                        _data[12] = _asc;
                        _data = _data[..Math.Min(_data.Length, cdb[4])];
                        _senseKey = _asc = 0;
                        break;

                    case ScsiReadCapacity10:
                        // Last LBA, then block length in bytes (SBC).
                        _data = new byte[8];
                        BinaryPrimitives.WriteUInt32BigEndian(_data, (uint)Math.Min(SectorCount - 1, uint.MaxValue));
                        BinaryPrimitives.WriteUInt32BigEndian(_data.AsSpan(4), 512);
                        break;

                    case ScsiReadFormatCapacities:
                        // Capacity list header (length 8) followed by one
                        // Current/Maximum Capacity Descriptor: block count,
                        // descriptor code 2 (formatted media), block length.
                        _data = new byte[12];
                        _data[3] = 8;
                        BinaryPrimitives.WriteUInt32BigEndian(_data.AsSpan(4), (uint)Math.Min(SectorCount, uint.MaxValue));
                        _data[8] = 2;
                        _data[10] = 2;
                        _data = _data[..Math.Min(_data.Length, BinaryPrimitives.ReadUInt16BigEndian(cdb[7..]))];
                        break;

                    case ScsiModeSense6:
                        // Mode parameter header (6): mode data length 3, no
                        // block descriptor, device-specific parameter's
                        // write-protect bit mirrors ReadOnly.
                        _data = [3, 0, (byte)(ReadOnly ? 128 : 0), 0];
                        _data = _data[..Math.Min(4, (int)cdb[4])];
                        break;

                    case ScsiModeSense10:
                        // Mode parameter header (10): same fields as (6),
                        // widened to a two-byte mode data length.
                        _data = [0, 6, 0, (byte)(ReadOnly ? 128 : 0), 0, 0, 0, 0];
                        _data = _data[..Math.Min(8, (int)BinaryPrimitives.ReadUInt16BigEndian(cdb[7..]))];
                        break;

                    case ScsiRead10:
                    case ScsiWrite10:
                    {
                        uint lba = BinaryPrimitives.ReadUInt32BigEndian(cdb[2..]);
                        int blocks = BinaryPrimitives.ReadUInt16BigEndian(cdb[7..]);
                        if (!Range(lba, blocks))
                        {
                            Fail(SenseKeyIllegalRequest, AscLogicalBlockAddressOutOfRange);
                            break;
                        }
                        if (output && ReadOnly)
                        {
                            Fail(SenseKeyDataProtect, AscWriteProtected);
                            break;
                        }

                        _data = new byte[blocks * 512];
                        if (output)
                        {
                            _writeLba = lba;
                            _writeLength = _data.Length;
                        }
                        else
                        {
                            _image.Position = (long)lba * 512;
                            _image.ReadExactly(_data);
                        }
                        break;
                    }

                    default:
                        Fail(SenseKeyIllegalRequest, AscInvalidCommandOperationCode);
                        break;
                }
            }
            catch (IOException)
            {
                _data = [];
                Fail(SenseKeyMediumError, op == ScsiSynchronizeCache10 ? AscWriteError : AscUnrecoveredReadError);
            }
        }

        int deviceLength = _data.Length;
        if (_hostLength == 0)
        {
            if (deviceLength != 0)
            {
                _commandStatus = CommandStatusPhaseError;
            }
            FinishData();
            return true;
        }
        if (deviceLength == 0 || _hostIn == output)
        {
            if (deviceLength != 0)
            {
                _commandStatus = CommandStatusPhaseError;
            }
            if (_hostIn)
            {
                _haltIn = true;
            }
            else
            {
                _haltOut = true;
            }
            FinishData();
            return true;
        }
        if (_hostLength < deviceLength)
        {
            _data = _data[..(int)_hostLength];
            _commandStatus = CommandStatusPhaseError;
        }

        // A device reply that exactly fills whole packets, but is shorter
        // than the host expected, must still end in a short packet so the
        // host driver notices completion; a synthetic zero-length one does
        // that without changing what was already transferred.
        _zeroPacket = _hostIn && _hostLength > _data.Length && _data.Length % BulkPacketSize == 0;
        _phase = output ? 2 : 1;
        return true;
    }

    public UsbTransactionResult Transfer(byte pid, byte endpoint, bool data1, Span<byte> data)
    {
        if (endpoint == ControlEndpoint)
        {
            return Control(pid, data1, data);
        }
        if (Configuration == 0)
        {
            return new(UsbHandshake.Nak);
        }
        if (_needsReset)
        {
            return new(UsbHandshake.Stall);
        }

        if (endpoint == BulkInEndpoint && pid == PidIn)
        {
            if (_haltIn)
            {
                return new(UsbHandshake.Stall);
            }
            if (data1 != _inToggle)
            {
                if (_lastIn.Length > data.Length)
                {
                    return new(UsbHandshake.Stall);
                }
                _lastIn.CopyTo(data);
                return new(UsbHandshake.Ack, _lastIn.Length);
            }

            int count;
            if (_phase == 1)
            {
                count = Math.Min(Math.Min(data.Length, BulkPacketSize), _data.Length - _position);
                _data.AsSpan(_position, count).CopyTo(data);
                _position += count;
                _processed += (uint)count;
                if (_position == _data.Length)
                {
                    if (count == 0 || !_zeroPacket)
                    {
                        FinishData();
                    }
                    else
                    {
                        _zeroPacket = false;
                    }
                }
            }
            else if (_phase == 3)
            {
                count = Math.Min(data.Length, _csw.Length - _cswPosition);
                _csw.AsSpan(_cswPosition, count).CopyTo(data);
                _cswPosition += count;
                if (_cswPosition == _csw.Length)
                {
                    _phase = 0;
                    if (_commandStatus == CommandStatusPhaseError)
                    {
                        _needsReset = true;
                    }
                }
            }
            else
            {
                return new(UsbHandshake.Nak);
            }
            _lastIn = data[..count].ToArray();
            _inToggle = !_inToggle;
            return new(UsbHandshake.Ack, count);
        }

        if (endpoint == BulkOutEndpoint && pid == PidOut)
        {
            if (_haltOut || data.Length > BulkPacketSize)
            {
                return new(UsbHandshake.Stall);
            }
            if (data1 != _outToggle)
            {
                return new(UsbHandshake.Ack, data.Length);
            }

            if (_phase == 0)
            {
                if (!Command(data))
                {
                    return new(UsbHandshake.Stall);
                }
            }
            else if (_phase == 2)
            {
                int count = Math.Min(data.Length, _data.Length - _position);
                data[..count].CopyTo(_data.AsSpan(_position));
                _position += count;
                if (_position == _data.Length)
                {
                    if (_commandStatus == CommandStatusPass && _position == _writeLength)
                    {
                        try
                        {
                            _image.Position = (long)_writeLba * 512;
                            _image.Write(_data);
                            _image.Flush();
                            _processed = (uint)_position;
                        }
                        catch (IOException)
                        {
                            Fail(SenseKeyMediumError, AscWriteError);
                        }
                    }
                    if (_hostLength > _data.Length)
                    {
                        _haltOut = true;
                    }
                    FinishData();
                }
            }
            else
            {
                _needsReset = _haltOut = _haltIn = true;
                return new(UsbHandshake.Stall);
            }
            _outToggle = !_outToggle;
            return new(UsbHandshake.Ack, data.Length);
        }

        return new(UsbHandshake.Stall);
    }

    /// <summary>
    /// One of the descriptors GET_DESCRIPTOR answers: the device descriptor
    /// (idVendor/idProduct 0xCAFE/0x0003, a fictitious pair reserved for
    /// this emulator; bcdUSB 0200h regardless of which controller answers,
    /// since this device is high-speed capable even where it currently sits
    /// at full speed -- owner decision, matching how a real USB 2.0 stick
    /// answers on a full-speed-only port), the configuration descriptor
    /// (one Mass Storage/SCSI/Bulk-Only interface with a bulk IN and a bulk
    /// OUT endpoint sized for whichever speed <see cref="SelectSpeed"/> last
    /// reported), the device qualifier and other-speed configuration
    /// descriptors USB 2.0 section 9.6.2/9.6.4 require of a high-speed-
    /// capable device (also answered regardless of current speed, for the
    /// same reason), the language ID string descriptor, and the three
    /// string descriptors it names.
    /// </summary>
    private byte[] Descriptor(ushort value)
    {
        if (value == DescriptorValueDevice)
        {
            return [18, 1, 0, 2, 0, 0, 0, ControlEndpointMaxPacketSize, 0xFE, 0xCA, 3, 0, 0, 1, 1, 2, 3, 1];
        }
        if (value == DescriptorValueConfiguration)
        {
            return ConfigurationDescriptor(DescriptorTypeConfiguration, BulkPacketSize);
        }
        if (value == DescriptorValueDeviceQualifier)
        {
            return [10, DescriptorTypeDeviceQualifier, 0, 2, 0, 0, 0, ControlEndpointMaxPacketSize, 1, 0];
        }
        if (value == DescriptorValueOtherSpeedConfiguration)
        {
            int otherSpeedBulkPacketSize = _highSpeed ? FullSpeedBulkMaxPacketSize : HighSpeedBulkMaxPacketSize;
            return ConfigurationDescriptor(DescriptorTypeOtherSpeedConfiguration, otherSpeedBulkPacketSize);
        }
        if (value == DescriptorValueLanguageIds)
        {
            return [4, 3, 9, 4]; // 0x0409: English (United States)
        }

        string text = value switch
        {
            DescriptorValueManufacturerString => "Aurora86",
            DescriptorValueProductString => "USB Disk",
            DescriptorValueSerialString => "000000000001",
            _ => "",
        };
        byte[] chars = Encoding.Unicode.GetBytes(text);
        return text.Length == 0 ? [] : [(byte)(chars.Length + 2), DescriptorTypeString, .. chars];
    }

    /// <summary>
    /// The configuration descriptor's bytes, shared between GET_DESCRIPTOR's
    /// own configuration type (2) and its other-speed counterpart (7): the
    /// two differ only in <paramref name="descriptorType"/> and in the bulk
    /// endpoints' packet size, which the caller supplies for whichever speed
    /// -- current or other -- it is describing.
    /// </summary>
    private byte[] ConfigurationDescriptor(byte descriptorType, int bulkPacketSize)
    {
        byte packetSizeLow = (byte)bulkPacketSize;
        byte packetSizeHigh = (byte)(bulkPacketSize >> 8);
        return [9, descriptorType, 32, 0, 1, 1, 0, 0x80, 50,
                9, 4, 0, 0, 2, InterfaceClassMassStorage, InterfaceSubClassScsiTransparent, InterfaceProtocolBulkOnlyTransport, 0,
                7, 5, BulkInEndpointAddress, EndpointAttributesBulk, packetSizeLow, packetSizeHigh, 0,
                7, 5, BulkOutEndpoint, EndpointAttributesBulk, packetSizeLow, packetSizeHigh, 0];
    }

    private UsbTransactionResult Control(byte pid, bool data1, Span<byte> packet)
    {
        if (pid == PidSetup && !data1 && packet.Length == 8)
        {
            _setup = packet.ToArray();
            _controlReply = [];
            _controlPosition = 0;
            _controlToggle = true;
            _controlStage = 0;
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(packet[2..]);
            ushort index = BinaryPrimitives.ReadUInt16LittleEndian(packet[4..]);
            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(packet[6..]);
            switch ((packet[0], packet[1]))
            {
                case (RequestTypeStandardDeviceIn, RequestGetDescriptor):
                    _controlReply = Descriptor(value);
                    if (_controlReply.Length == 0)
                    {
                        return new(UsbHandshake.Stall);
                    }
                    break;
                case (RequestTypeStandardDeviceOut, RequestSetAddress) when value < 128 && index == 0 && length == 0: break;
                case (RequestTypeStandardDeviceOut, RequestSetConfiguration) when value <= 1 && index == 0 && length == 0: break;
                case (RequestTypeStandardDeviceIn, RequestGetConfiguration) when index == 0 && value == 0: _controlReply = [Configuration]; break;
                case (RequestTypeStandardDeviceIn, RequestGetStatus) when index == 0 && value == 0: _controlReply = [0, 0]; break;
                case (RequestTypeStandardInterfaceIn, RequestGetStatus or RequestGetInterface) when index == 0 && value == 0:
                    _controlReply = packet[1] == RequestGetInterface ? [0] : [0, 0];
                    break;
                case (RequestTypeStandardInterfaceOut, RequestSetInterface) when index == 0 && value == 0 && length == 0: break;
                case (RequestTypeStandardEndpointIn, RequestGetStatus) when value == 0 && index is BulkInEndpointAddress or BulkOutEndpoint:
                    _controlReply = [(byte)((index == BulkInEndpointAddress ? _haltIn : _haltOut) ? 1 : 0), 0];
                    break;
                case (RequestTypeStandardEndpointOut, RequestClearFeature or RequestSetFeature)
                    when value == 0 && index is BulkInEndpointAddress or BulkOutEndpoint && length == 0: break;
                case (RequestTypeBulkOnlyClassIn, BulkOnlyRequestGetMaxLun) when value == 0 && index == 0 && length == 1:
                    _controlReply = [0];
                    break;
                case (RequestTypeBulkOnlyClassOut, BulkOnlyRequestReset) when value == 0 && index == 0 && length == 0: break;
                default: return new(UsbHandshake.Stall);
            }
            if (_controlReply.Length > length)
            {
                _controlReply = _controlReply[..length];
            }
            _controlStage = length == 0 ? 2 : 1;
            return new(UsbHandshake.Ack, 8);
        }

        if (_controlStage == 1 && pid == PidIn && data1 == _controlToggle)
        {
            int count = Math.Min(Math.Min(packet.Length, ControlEndpointMaxPacketSize), _controlReply.Length - _controlPosition);
            _controlReply.AsSpan(_controlPosition, count).CopyTo(packet);
            _controlPosition += count;
            _controlToggle = !_controlToggle;
            if (count < ControlEndpointMaxPacketSize || _controlPosition >= BinaryPrimitives.ReadUInt16LittleEndian(_setup.AsSpan(6)))
            {
                _controlStage = 3;
            }
            return new(UsbHandshake.Ack, count);
        }

        if ((_controlStage == 2 && pid == PidIn || _controlStage == 3 && pid == PidOut) && data1 && packet.Length == 0)
        {
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_setup.AsSpan(2));
            ushort index = BinaryPrimitives.ReadUInt16LittleEndian(_setup.AsSpan(4));
            switch ((_setup[0], _setup[1]))
            {
                case (RequestTypeStandardDeviceOut, RequestSetAddress): Address = (byte)value; break;
                case (RequestTypeStandardDeviceOut, RequestSetConfiguration):
                    Configuration = (byte)value;
                    ResetTransport();
                    _inToggle = _outToggle = _haltIn = _haltOut = false;
                    _lastIn = [];
                    break;
                case (RequestTypeStandardEndpointOut, RequestClearFeature):
                    if (index == BulkInEndpointAddress)
                    {
                        _haltIn = _inToggle = false;
                        _lastIn = [];
                    }
                    else
                    {
                        _haltOut = _outToggle = false;
                    }
                    break;
                case (RequestTypeStandardEndpointOut, RequestSetFeature):
                    if (index == BulkInEndpointAddress)
                    {
                        _haltIn = true;
                    }
                    else
                    {
                        _haltOut = true;
                    }
                    break;
                case (RequestTypeBulkOnlyClassOut, BulkOnlyRequestReset): ResetTransport(); break;
            }
            _controlStage = 0;
            return new(UsbHandshake.Ack);
        }

        return new(UsbHandshake.Stall);
    }

    public byte[] Capture()
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(SectorCount);
        w.Write(ReadOnly);
        w.Write(Address);
        w.Write(Configuration);
        foreach (bool value in new[] { _medium, _haltIn, _haltOut, _needsReset, _inToggle, _outToggle, _controlToggle, _hostIn, _zeroPacket })
        {
            w.Write(value);
        }
        foreach (int value in new[] { _position, _phase, _cswPosition, _controlPosition, _controlStage, _writeLength })
        {
            w.Write(value);
        }
        w.Write(_tag);
        w.Write(_hostLength);
        w.Write(_processed);
        w.Write(_writeLba);
        w.Write(_commandStatus);
        w.Write(_senseKey);
        w.Write(_asc);
        foreach (byte[] bytes in new[] { _lastIn, _data, _csw, _setup, _controlReply })
        {
            w.Write(bytes.Length);
            w.Write(bytes);
        }
        // Appended after every field an older build already saved (EHCI support added this
        // one later): a snapshot captured before high speed existed has nothing to read here,
        // and Restore below treats that absence as full speed, its only possible value then.
        w.Write(_highSpeed);
        return stream.ToArray();
    }

    public void Restore(ReadOnlySpan<byte> state)
    {
        using var r = new BinaryReader(new MemoryStream(state.ToArray()));
        if (r.ReadInt64() != SectorCount || r.ReadBoolean() != ReadOnly)
        {
            throw new InvalidDataException("USB disk snapshot media differs.");
        }

        Address = r.ReadByte();
        Configuration = r.ReadByte();
        _medium = r.ReadBoolean();
        _haltIn = r.ReadBoolean();
        _haltOut = r.ReadBoolean();
        _needsReset = r.ReadBoolean();
        _inToggle = r.ReadBoolean();
        _outToggle = r.ReadBoolean();
        _controlToggle = r.ReadBoolean();
        _hostIn = r.ReadBoolean();
        _zeroPacket = r.ReadBoolean();
        _position = r.ReadInt32();
        _phase = r.ReadInt32();
        _cswPosition = r.ReadInt32();
        _controlPosition = r.ReadInt32();
        _controlStage = r.ReadInt32();
        _writeLength = r.ReadInt32();
        _tag = r.ReadUInt32();
        _hostLength = r.ReadUInt32();
        _processed = r.ReadUInt32();
        _writeLba = r.ReadUInt32();
        _commandStatus = r.ReadByte();
        _senseKey = r.ReadByte();
        _asc = r.ReadByte();

        byte[] ReadBlock()
        {
            int length = r.ReadInt32();
            if (length < 0 || length > r.BaseStream.Length - r.BaseStream.Position)
            {
                throw new InvalidDataException("Invalid USB disk snapshot.");
            }
            return r.ReadBytes(length);
        }

        _lastIn = ReadBlock();
        _data = ReadBlock();
        _csw = ReadBlock();
        _setup = ReadBlock();
        _controlReply = ReadBlock();

        // Appended by Capture after every other field, so a snapshot from before EHCI
        // support existed simply has nothing left to read here; the device it came from
        // could then only ever have been full speed, so that is exactly what an absent
        // flag means.
        bool highSpeed = false;
        if (r.BaseStream.Position < r.BaseStream.Length)
        {
            highSpeed = r.ReadBoolean();
        }
        if (r.BaseStream.Position != r.BaseStream.Length || _phase is < 0 or > 3 || _processed > _hostLength)
        {
            throw new InvalidDataException("Invalid USB disk snapshot.");
        }
        _highSpeed = highSpeed;
    }
}
