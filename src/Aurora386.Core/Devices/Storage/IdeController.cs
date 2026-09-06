using Aurora386.Core.Firmware;
using Aurora386.Core.Firmware.Fpu;
using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Diagnostics;
using Aurora386.Core.IO;

using Aurora386.Core.Devices.Interrupts;
namespace Aurora386.Core.Devices.Storage;

/// <summary>
/// The IDE controller, and the ATAPI packet interface a CD-ROM speaks over it.
///
/// Everything the emulator did with disks before this went through the BIOS:
/// software asked INT 13h to read a sector and the emulator answered out of an
/// image file. That works for MS-DOS, which knows no other way, but it is not
/// how the hardware behaves and it leaves out everyone who talks to the drive
/// directly -- which is every real CD-ROM driver. OAKCDROM.SYS, the generic
/// driver of the period, looks for a drive on the IDE ports, finds nothing,
/// and gives up with "no drives found".
///
/// A CD-ROM on an IDE cable is not an IDE disk. It answers the ATA commands
/// only far enough to say what it is, and everything real arrives inside a
/// twelve-byte SCSI packet handed over with the PACKET command. That
/// arrangement is called ATAPI, and it exists because by 1994 the drives were
/// already speaking SCSI internally and putting them on the cheap cable was
/// mostly a matter of not translating.
/// </summary>
public sealed class IdeController : IIoDevice
{
    private readonly Machine _machine;
    private readonly InterruptController _interrupts;

    /// <summary>Which of the two drives on this cable is being addressed.</summary>
    private bool _slaveSelected;

    // --- the register file ----------------------------------------------------

    private byte _features;
    private byte _sectorCount;
    private byte _lbaLow;
    private byte _lbaMid;
    private byte _lbaHigh;
    private byte _driveHead;
    private byte _error;
    private byte _status = StatusReady | StatusSeekComplete;

    /// <summary>
    /// The buffer a transfer is read from or written into, and how far along
    /// it we are.
    ///
    /// A real drive moves data a word at a time as the host reads the data
    /// register, and reports itself busy in between. Assembling the whole
    /// transfer up front and handing it over word by word is
    /// indistinguishable from the outside, because the host cannot observe
    /// anything except through these registers.
    /// </summary>
    private byte[] _buffer = [];
    private int _bufferPosition;

    /// <summary>Set while the drive is waiting for the twelve bytes of a packet.</summary>
    private bool _expectingPacket;

    // Status bits, as software reads them at port 0x1F7.
    private const byte StatusError = 0x01;
    private const byte StatusDataRequest = 0x08;
    private const byte StatusSeekComplete = 0x10;
    private const byte StatusReady = 0x40;
    private const byte StatusBusy = 0x80;

    // Error bits. For ATAPI the top nibble carries a sense key instead.
    private const byte ErrorAbort = 0x04;

    public IdeController(Machine machine, InterruptController interrupts)
    {
        _machine = machine;
        _interrupts = interrupts;
    }

    /// <summary>
    /// Registers on the primary channel's ports.
    ///
    /// 0x1F0 to 0x1F7 are the command block; 0x3F6 is the control block, whose
    /// only interesting register is the one that lets software read the status
    /// without acknowledging the interrupt. The secondary channel at 0x170 is
    /// not claimed: nothing we emulate is on it, and answering for ports that
    /// have nothing behind them makes software wait on a drive that will never
    /// be ready.
    /// </summary>
    public void Register(IoBus io)
    {
        io.Register(0x1F0, 8, this);
        io.Register(0x3F6, 2, this);
    }

    /// <summary>
    /// Whether a CD-ROM is present on this cable.
    ///
    /// The disc is the emulator's mounted image; with nothing mounted the
    /// drive is still there but empty, which is what a real machine with an
    /// open tray looks like. That distinction matters to a driver: no drive at
    /// all means give up, an empty drive means ask again later.
    /// </summary>
    private bool HasDisc => _machine.CompactDisc is not null;

    /// <summary>
    /// The CD-ROM is the slave on the primary cable, which is where a machine
    /// of the period put it: the hard disk went on as master, and the drives
    /// shared the one cable the motherboard had.
    /// </summary>
    private bool AddressingCompactDisc => _slaveSelected;

    public byte ReadByte(ushort port)
    {
        switch (port)
        {
            case 0x1F0: return ReadData();
            case 0x1F1: return _error;
            case 0x1F2: return _sectorCount;
            case 0x1F3: return _lbaLow;
            case 0x1F4: return _lbaMid;
            case 0x1F5: return _lbaHigh;
            case 0x1F6: return _driveHead;

            case 0x1F7:
                // Reading the status register acknowledges the interrupt. This
                // is the register software polls, so the acknowledgement has
                // to happen here rather than anywhere more obvious.
                _interrupts.LowerIrq(14);
                return CurrentStatus;

            // The alternate status register: the same value, without the
            // acknowledgement. It exists so that a debugger, or an interrupt
            // handler that has not finished, can look without changing
            // anything.
            case 0x3F6: return CurrentStatus;

            default: return 0xFF;
        }
    }

    /// <summary>
    /// The status byte, with the drive-not-present case handled.
    ///
    /// An IDE cable with nothing on it reads back 0xFF, because the bus is
    /// pulled high and there is nobody to drive it low. Software relies on
    /// this: it is how a driver decides a channel is empty rather than busy,
    /// and returning a plausible-looking zero instead makes it wait for ever.
    /// </summary>
    private byte CurrentStatus
    {
        get
        {
            // Only the CD-ROM is emulated on this cable so far. Selecting the
            // master must therefore look like selecting a drive that is not
            // there.
            if (!AddressingCompactDisc) return 0x00;

            return _status;
        }
    }

    public void WriteByte(ushort port, byte value)
    {
        switch (port)
        {
            case 0x1F0: WriteData(value); return;
            case 0x1F1: _features = value; return;
            case 0x1F2: _sectorCount = value; return;
            case 0x1F3: _lbaLow = value; return;
            case 0x1F4: _lbaMid = value; return;
            case 0x1F5: _lbaHigh = value; return;

            case 0x1F6:
                // Bit 4 selects master or slave; the rest is the head number
                // in CHS mode and the top LBA bits otherwise.
                _driveHead = value;
                _slaveSelected = (value & 0x10) != 0;
                return;

            case 0x1F7: ExecuteCommand(value); return;

            case 0x3F6:
                // Bit 2 is the software reset. A driver pulses it to find out
                // what is on the cable, and expects the drive to come back
                // announcing itself.
                if ((value & 0x04) != 0) Reset();
                return;
        }
    }

    /// <summary>
    /// 16-bit access to the data register.
    ///
    /// The data port is genuinely sixteen bits wide -- an IDE cable carries
    /// sixteen data lines -- and drivers read it with a word instruction, or
    /// a whole sector at a time with a repeated one. Serving it as two byte
    /// accesses happens to work here because the buffer is a byte array, but
    /// the word form is what software actually uses.
    /// </summary>
    public ushort ReadWord(ushort port)
    {
        if (port != 0x1F0) return (ushort)(ReadByte(port) | (ReadByte((ushort)(port + 1)) << 8));

        return (ushort)(ReadData() | (ReadData() << 8));
    }

    public void WriteWord(ushort port, ushort value)
    {
        if (port != 0x1F0)
        {
            WriteByte(port, (byte)value);
            WriteByte((ushort)(port + 1), (byte)(value >> 8));
            return;
        }

        WriteData((byte)value);
        WriteData((byte)(value >> 8));
    }

    private void Reset()
    {
        _status = StatusReady | StatusSeekComplete;
        _error = 0x01;             // "no error", which for the reset diagnostic is 1
        _buffer = [];
        _bufferPosition = 0;
        _expectingPacket = false;

        // A drive announces what it is by leaving a signature in the address
        // registers after a reset. An ATAPI device writes 0xEB14 into the two
        // LBA bytes, and that is how a driver tells a CD-ROM from a hard disk
        // before it has asked either of them anything.
        _sectorCount = 0x01;
        _lbaLow = 0x01;
        _lbaMid = 0x14;
        _lbaHigh = 0xEB;
    }

    private byte ReadData()
    {
        if (_bufferPosition >= _buffer.Length)
        {
            _status &= unchecked((byte)~StatusDataRequest);
            return 0;
        }

        byte value = _buffer[_bufferPosition++];

        if (_bufferPosition >= _buffer.Length)
        {
            // The transfer is over: stop asking for data and let the driver
            // see a drive that is ready for the next command.
            _status &= unchecked((byte)~StatusDataRequest);
            _status |= StatusReady;

            // And interrupt again, which is the part that is easy to miss.
            //
            // An ATAPI drive interrupts twice for every command that returns
            // data: once when the data is ready to be collected, and again
            // when the last byte has gone. The second one is what tells the
            // driver the command is finished -- without it the driver has the
            // bytes but is still waiting to be told it may use them, and
            // eventually gives up. MSCDEX reports that as CDR101, "not ready
            // reading drive", which is a long way from what went wrong.
            //
            // The interrupt reason register says which of the two this is:
            // bit 0 set with bit 1 clear means the command is complete.
            _sectorCount = 0x03;
            RaiseInterrupt();
        }

        return value;
    }

    private void WriteData(byte value)
    {
        if (!_expectingPacket) return;

        if (_bufferPosition < _buffer.Length) _buffer[_bufferPosition++] = value;
        if (_bufferPosition < _buffer.Length) return;

        // The whole packet has arrived.
        _expectingPacket = false;
        _status &= unchecked((byte)~StatusDataRequest);

        ExecutePacket(_buffer);
    }

    private void ExecuteCommand(byte command)
    {
        if (!AddressingCompactDisc)
        {
            // Nothing there. Aborting rather than staying silent is what lets
            // a driver move on instead of waiting.
            _status = StatusReady | StatusError;
            _error = ErrorAbort;
            return;
        }

        switch (command)
        {
            case 0xA1: IdentifyPacketDevice(); return;
            case 0xA0: BeginPacket(); return;

            case 0xEC:
                // IDENTIFY DEVICE, the command for an ATA disk. An ATAPI
                // device must refuse it -- and the refusal is informative,
                // because it is how a driver written before ATAPI existed
                // discovers that this is not a disk it can use.
                _status = StatusReady | StatusError;
                _error = ErrorAbort;
                _lbaLow = 0x01;
                _lbaMid = 0x14;
                _lbaHigh = 0xEB;
                RaiseInterrupt();
                return;

            case 0x08:
                // DEVICE RESET, which an ATAPI drive supports and a disk does
                // not.
                Reset();
                RaiseInterrupt();
                return;

            case 0x00:      // NOP
            case 0xE7:      // FLUSH CACHE: nothing is cached
                _status = StatusReady | StatusSeekComplete;
                RaiseInterrupt();
                return;

            default:
                _status = StatusReady | StatusError;
                _error = ErrorAbort;
                RaiseInterrupt();
                return;
        }
    }

    /// <summary>
    /// IDENTIFY PACKET DEVICE: 256 words describing the drive.
    ///
    /// Most of the fields are about geometry and transfer modes that mean
    /// nothing to a CD-ROM. The ones that matter are the first word, whose
    /// top bits say "this is an ATAPI CD-ROM speaking twelve-byte packets",
    /// and the three text fields -- a driver shows those to the user, and
    /// some check them to decide whether they recognise the drive.
    /// </summary>
    private void IdentifyPacketDevice()
    {
        var words = new ushort[256];

        // Bit 15: not an ATA device. Bits 12-8: a CD-ROM (type 5). Bits 1-0:
        // twelve-byte packets. Bits 6-5: the drive interrupts when it wants
        // the packet, rather than expecting to be polled.
        words[0] = 0x85C0;

        WriteText(words, 10, "AURORA386CD00000000", 10);   // serial number
        WriteText(words, 23, "1.0     ", 4);               // firmware revision
        WriteText(words, 27, "Aurora386 Virtual CD-ROM", 20);

        words[49] = 0x0300;   // LBA and DMA are supported
        words[53] = 0x0006;   // words 64-70 and 88 are valid
        words[63] = 0x0007;   // multiword DMA modes 0-2
        words[64] = 0x0003;   // PIO modes 3 and 4
        words[71] = 30;       // microseconds from PACKET to the request
        words[72] = 30;
        words[80] = 0x001E;   // ATA/ATAPI-4

        var bytes = new byte[512];
        for (int i = 0; i < words.Length; i++)
        {
            bytes[i * 2] = (byte)words[i];
            bytes[i * 2 + 1] = (byte)(words[i] >> 8);
        }

        BeginTransfer(bytes);
    }

    /// <summary>
    /// Writes one of the identify block's text fields.
    ///
    /// The bytes are swapped within each word, which looks like a mistake and
    /// is not: the field is a sequence of words on a big-endian bus, so a
    /// little-endian host reading it word by word sees every pair the other
    /// way round. Every driver ever written un-swaps them, so a drive that
    /// does not swap them shows up with its name scrambled.
    /// </summary>
    private static void WriteText(ushort[] words, int firstWord, string text, int wordCount)
    {
        string padded = text.PadRight(wordCount * 2)[..(wordCount * 2)];

        for (int i = 0; i < wordCount; i++)
            words[firstWord + i] = (ushort)((padded[i * 2] << 8) | padded[i * 2 + 1]);
    }

    private void BeginPacket()
    {
        _buffer = new byte[12];
        _bufferPosition = 0;
        _expectingPacket = true;

        _status = StatusReady | StatusDataRequest;

        // The interrupt reason goes in the sector count register: bit 0 clear
        // means the drive wants a command, not data.
        _sectorCount = 0x01;
    }

    /// <summary>
    /// Carries out a SCSI command that arrived inside a packet.
    ///
    /// These are the commands a driver uses to find and read a disc. They are
    /// SCSI commands, not ATA ones, which is why the numbers look unrelated
    /// to everything above.
    /// </summary>
    private void ExecutePacket(byte[] packet)
    {
        // A packet arriving at all means the guest has a CD-ROM driver of its
        // own, talking to the drive directly. From here the emulator's
        // stand-in MSCDEX must keep quiet: the guest is running the real one,
        // and two of them answering the same calls contradict each other.
        // The same goes for the redirector's copy of the disc: the guest's
        // driver will give it a letter, and a second letter for the same
        // disc is how the drive ended up on E: with an empty D: beside it.
        if (!_machine.GuestHasItsOwnCompactDiscExtensions)
        {
            _machine.GuestHasItsOwnCompactDiscExtensions = true;
            _machine.UnmountRedirectedDrive(_machine.CompactDiscServices.DriveLetter);
        }

        // A disc that changed since the last command is reported once, as
        // unit attention with "medium may have changed", before anything
        // else is done. This is how a driver learns to throw away what it
        // cached about the old disc: OAKCDROM and MSCDEX re-read the volume
        // descriptor on seeing it, and without it a disc inserted after boot
        // shows the previous directory -- or, when the tray was empty at
        // boot, nothing at all.
        if (_mediaChanged && packet[0] is not (0x03 or 0x12))
        {
            _mediaChanged = false;
            FailWithSense(0x06, 0x28);
            return;
        }

        switch (packet[0])
        {
            case 0x00: TestUnitReady(); return;
            case 0x03: RequestSense(); return;
            case 0x12: Inquiry(packet); return;
            case 0x1B: StartStopUnit(); return;
            case 0x1E: Succeed(); return;              // prevent/allow removal
            case 0x25: ReadCapacity(); return;
            case 0x28: Read10(packet); return;
            case 0x42: ReadSubChannel(packet); return;
            case 0x43: ReadTableOfContents(packet); return;
            case 0x44: ReadHeader(packet); return;
            case 0x4E: Succeed(); return;              // stop play/scan
            case 0x5A: ModeSense(packet); return;
            case 0xBB: Succeed(); return;              // set CD speed
            case 0xBD: MechanismStatus(packet); return;

            default:
                // Illegal request, invalid command operation code.
                FailWithSense(0x05, 0x20);
                return;
        }
    }

    private void TestUnitReady()
    {
        if (!HasDisc)
        {
            // Not ready, medium not present. This is the answer that tells a
            // driver the drive exists but the tray is empty -- quite
            // different from no drive at all.
            FailWithSense(0x02, 0x3A);
            return;
        }

        Succeed();
    }

    /// <summary>
    /// Reports why the last command failed.
    ///
    /// A driver that gets an error asks this immediately, and until it does
    /// the drive is supposed to keep the answer. Eighteen bytes is the fixed
    /// format everything understands.
    /// </summary>
    private void RequestSense()
    {
        var sense = new byte[18];

        sense[0] = 0x70;              // current error, fixed format
        sense[2] = _senseKey;
        sense[7] = 10;                // ten more bytes follow
        sense[12] = _senseCode;

        BeginTransfer(sense);
    }

    private byte _senseKey;
    private byte _senseCode;

    private bool _mediaChanged;

    /// <summary>
    /// Tells the drive its tray was opened and closed: the next command from
    /// the guest is answered with unit attention so the driver re-reads the
    /// disc.
    /// </summary>
    public void NotifyMediaChanged() => _mediaChanged = true;

    /// <summary>
    /// Says what kind of device this is, in the words a driver prints.
    /// </summary>
    private void Inquiry(byte[] packet)
    {
        var data = new byte[36];

        data[0] = 0x05;   // a CD-ROM
        data[1] = 0x80;   // removable medium
        data[2] = 0x00;   // no claimed standard, which is what the drives did
        data[3] = 0x21;   // response format 1, with the ATAPI bit
        data[4] = 31;     // bytes that follow

        "AURORA  "u8.CopyTo(data.AsSpan(8, 8));
        "Virtual CD-ROM  "u8.CopyTo(data.AsSpan(16, 16));
        "1.0 "u8.CopyTo(data.AsSpan(32, 4));

        int wanted = Math.Min(packet[4] == 0 ? data.Length : packet[4], data.Length);
        BeginTransfer(data.AsSpan(0, wanted).ToArray());
    }

    private void StartStopUnit() => Succeed();

    /// <summary>
    /// How big the disc is: the last block's number, and the block size.
    ///
    /// The number reported is the last valid block, not the count -- an
    /// off-by-one that has confused implementers since 1986 and which a
    /// driver relies on when it works out where the disc ends.
    /// </summary>
    private void ReadCapacity()
    {
        if (!HasDisc) { FailWithSense(0x02, 0x3A); return; }

        long lastBlock = _machine.CompactDisc!.TotalSectors - 1;
        var data = new byte[8];

        // Big-endian, as everything in SCSI is.
        data[0] = (byte)(lastBlock >> 24);
        data[1] = (byte)(lastBlock >> 16);
        data[2] = (byte)(lastBlock >> 8);
        data[3] = (byte)lastBlock;

        data[4] = 0;
        data[5] = 0;
        data[6] = (byte)(IsoImage.SectorSize >> 8);
        data[7] = unchecked((byte)IsoImage.SectorSize);

        BeginTransfer(data);
    }

    /// <summary>
    /// Reads blocks off the disc. This is the command that carries every byte
    /// of a file the guest opens.
    /// </summary>
    private void Read10(byte[] packet)
    {
        if (!HasDisc) { FailWithSense(0x02, 0x3A); return; }

        long lba = ((long)packet[2] << 24) | ((long)packet[3] << 16)
                 | ((long)packet[4] << 8) | packet[5];
        int count = (packet[7] << 8) | packet[8];

        // A request for no blocks is a question, not a mistake.
        //
        // MSCDEX asks for zero blocks before every real read, and what it
        // wants back is the address registers: how much the drive would hand
        // over if asked. The command transfers nothing, which is why the
        // standard allows it, but the answer still has to describe the read
        // that would have happened.
        //
        // Reporting nothing looks harmless and is not. Traced against the
        // real MSCDEX, every read arrives twice -- once for zero blocks, once
        // for one -- except the root directory, which was asked for with zero
        // and then never read at all. It had been told the drive had nothing
        // to give, so it fell back to the path table, read that instead,
        // found no directory to enter, and went round again. From the outside
        // that is a "cd" that appears to work and then puts you back where
        // you started.
        if (count == 0)
        {
            uint available = IsoImage.SectorSize;

            _lbaMid = (byte)available;
            _lbaHigh = (byte)(available >> 8);

            Succeed();
            return;
        }

        IsoImage disc = _machine.CompactDisc!;

        if (lba < 0 || lba + count > disc.TotalSectors)
        {
            // Illegal request, logical block address out of range.
            FailWithSense(0x05, 0x21);
            return;
        }

        var data = new byte[count * IsoImage.SectorSize];
        disc.ReadSectors(lba, count, data);

        BeginTransfer(data);
    }

    /// <summary>
    /// The table of contents: where each track begins.
    ///
    /// A data disc has one track, and a driver asks for this before deciding
    /// whether the disc holds files or music. Refusing the command makes some
    /// drivers conclude the drive is broken.
    /// </summary>
    private void ReadTableOfContents(byte[] packet)
    {
        if (!HasDisc) { FailWithSense(0x02, 0x3A); return; }

        long leadOut = _machine.CompactDisc!.TotalSectors;

        // Header, then a descriptor for track 1 and one for the lead-out.
        // Bit 1 of byte 1 asks for addresses as minutes, seconds and frames
        // rather than as block numbers.
        //
        // This is not a formatting preference. MSCDEX asks in MSF, because
        // that is how an audio CD numbers its own contents -- 75 frames to
        // the second, and the first two seconds are the lead-in, so block
        // zero is at 00:02:00. Answering in blocks when MSF was asked for
        // hands back numbers that are wrong by a factor of a hundred or so,
        // and the driver concludes the disc is unreadable.
        bool msf = (packet[1] & 0x02) != 0;

        // Which table is wanted, and the answer is in two places.
        //
        // The format field moved. It began life in the top two bits of byte 9
        // and was later given a proper home in the low nibble of byte 2, and
        // both spellings remained in use -- drivers written before the change
        // still set byte 9, and a drive that reads only byte 2 sees every one
        // of those requests as a request for format 0.
        //
        // Measured with the driver of the period: it sends
        //
        //   43 02 00 00 00 00 00 00 0C 40 00 00
        //
        // -- byte 2 zero, byte 9 set to 0x40, twelve bytes wanted. Reading
        // byte 2 alone answers with the whole track listing when what was
        // asked for was the disc's extent, and MSCDEX gives up with "not
        // ready" over a drive that is perfectly ready.
        int format = (packet[2] & 0x0F) != 0
            ? packet[2] & 0x0F
            : (packet[9] >> 6) & 0x03;

        byte[] data = format == 1 ? SessionInformation(msf)
                                  : TrackListing(leadOut, msf);

        // Hand over as much as was asked for, and no more. The length in the
        // header still describes the whole table: a driver reads it to find
        // out whether it has everything, and asks again if not.
        int asked = (packet[7] << 8) | packet[8];
        int sending = asked == 0 ? data.Length : Math.Min(asked, data.Length);

        BeginTransfer(data.AsSpan(0, sending).ToArray());
    }

    /// <summary>
    /// Format 0: every track on the disc, then the lead-out.
    /// </summary>
    private static byte[] TrackListing(long leadOut, bool msf)
    {
        var data = new byte[20];

        data[0] = 0;
        data[1] = 18;     // the bytes that follow
        data[2] = 1;      // first track
        data[3] = 1;      // last track

        // Track 1: a data track, starting at the beginning of the disc.
        data[4] = 0;
        data[5] = 0x14;   // data track, digital copy permitted
        data[6] = 1;      // track number
        data[7] = 0;
        WriteAddress(data, 8, 0, msf);

        // The lead-out, which is reported as track 0xAA.
        data[12] = 0;
        data[13] = 0x14;
        data[14] = 0xAA;
        data[15] = 0;
        WriteAddress(data, 16, (uint)leadOut, msf);

        return data;
    }

    /// <summary>
    /// Format 1: where the last session's first track begins.
    ///
    /// A driver asks this when it is about to mount a file system and wants
    /// to know where to start reading. The descriptor names the first track
    /// of the last session, and the address is where that track <i>begins</i>
    /// -- not where the disc ends.
    ///
    /// The distinction is easy to get backwards, because format 0 finishes
    /// with a lead-out descriptor and this one looks like it. Put the
    /// lead-out here and the driver goes looking for the volume descriptor
    /// past the end of the disc, finds nothing, and reports the drive as not
    /// ready -- which is a long way from what actually went wrong.
    /// </summary>
    private static byte[] SessionInformation(bool msf)
    {
        var data = new byte[12];

        data[0] = 0;
        data[1] = 10;     // the bytes that follow
        data[2] = 1;      // first session
        data[3] = 1;      // last session

        data[4] = 0;
        data[5] = 0x14;   // a data track
        data[6] = 1;      // the first track of the last session
        data[7] = 0;

        // Where that track starts. A single-session data disc begins at the
        // very beginning.
        WriteAddress(data, 8, 0, msf);

        return data;
    }

    /// <summary>
    /// Where the drive is in the disc, and what it is doing there.
    ///
    /// This is a command for audio playback -- it reports the current track
    /// and the elapsed time -- and MSCDEX asks it about a data disc anyway,
    /// to find out whether the drive is playing music before it tries to
    /// treat the disc as a file system. Refusing it makes MSCDEX conclude the
    /// drive cannot be talked to, which arrives at the user as DOS's own
    /// "Abort, Retry, Ignore?" over a drive that plainly exists.
    ///
    /// A data disc is never playing, so the answer is short: audio status
    /// 0x15, meaning stopped.
    /// </summary>
    private void ReadSubChannel(byte[] packet)
    {
        if (!HasDisc) { FailWithSense(0x02, 0x3A); return; }

        var data = new byte[16];

        data[0] = 0;
        data[1] = 0x15;   // audio status: stopped, nothing playing
        data[2] = 0;
        data[3] = 12;     // length of what follows

        // The sub-channel data format asked for, echoed back, then track one
        // at the very beginning of the disc.
        data[4] = 0x01;   // current position
        data[5] = 0x14;   // a data track
        data[6] = 1;      // track number
        data[7] = 0;      // index

        BeginTransfer(data);
    }

    /// <summary>
    /// What kind of sector sits at a given address.
    ///
    /// A driver asks this to tell a data disc from an audio one without
    /// having to read anything: the answer says the sector holds computer
    /// data in mode 1, which is what every disc with files on it uses.
    /// </summary>
    private void ReadHeader(byte[] packet)
    {
        if (!HasDisc) { FailWithSense(0x02, 0x3A); return; }

        var data = new byte[8];
        data[0] = 0x01;   // mode 1: data, with error correction

        BeginTransfer(data);
    }

    /// <summary>
    /// Whether the tray is open, whether a disc is in it, and whether the
    /// drive is playing. A changer would report which slot is loaded; ours
    /// has one.
    /// </summary>
    private void MechanismStatus(byte[] packet)
    {
        var data = new byte[8];

        // Bit 5 of byte 1 says a disc is present and the tray is closed.
        data[1] = (byte)(HasDisc ? 0x20 : 0x00);
        data[5] = 1;      // one slot

        BeginTransfer(data);
    }

    /// <summary>
    /// Reports the drive's settings. Only the capabilities page is answered,
    /// which is the one a driver reads to find out what the drive can do.
    /// </summary>
    private void ModeSense(byte[] packet)
    {
        byte page = (byte)(packet[2] & 0x3F);

        var data = new byte[30];

        data[0] = 0;
        data[1] = 28;     // length of what follows
        data[2] = 0x01;   // 2048-byte blocks

        if (page is 0x2A or 0x3F)
        {
            data[8] = 0x2A;   // the CD capabilities page
            data[9] = 18;
            data[10] = 0x00;  // no CD-R, no CD-RW
            data[11] = 0x00;
            data[12] = 0x01;  // can read a plain data disc
            data[13] = 0x00;
            data[14] = 0x29;  // tray loader, can lock, tells when the disc changes
        }

        BeginTransfer(data);
    }

    /// <summary>
    /// Writes a disc address, as either a block number or minutes, seconds
    /// and frames.
    ///
    /// A CD counts in frames -- 75 to the second -- and reserves the first
    /// two seconds for the lead-in, so the block a driver calls zero is at
    /// two minutes zero seconds zero frames. Every drive does this
    /// conversion, and getting the two-second offset wrong puts every
    /// address 150 frames out, which shows up as a disc whose first file
    /// cannot be read.
    /// </summary>
    private static void WriteAddress(byte[] data, int offset, uint block, bool msf)
    {
        if (!msf)
        {
            WriteBigEndian(data, offset, block);
            return;
        }

        const uint FramesPerSecond = 75;
        const uint LeadInFrames = 2 * FramesPerSecond;

        uint frames = block + LeadInFrames;

        data[offset] = 0;
        data[offset + 1] = (byte)(frames / (FramesPerSecond * 60));
        data[offset + 2] = (byte)(frames / FramesPerSecond % 60);
        data[offset + 3] = (byte)(frames % FramesPerSecond);
    }

    private static void WriteBigEndian(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private void Succeed()
    {
        _senseKey = 0;
        _senseCode = 0;
        _error = 0;
        _status = StatusReady | StatusSeekComplete;

        // Say that the command is over, and not that a packet is awaited.
        //
        // The interrupt reason register is left holding whatever the last
        // step put there, and the last step for a command that returns no
        // data is BeginPacket, which set it to 1 -- "send me a command". A
        // driver reading that after its command has finished is being told
        // the drive is still waiting to be given one.
        //
        // Traced against the real MSCDEX: for a read that returns data it
        // reads the byte-count registers and collects the bytes; for one that
        // does not, it reads this register three times, sees 1 each time, and
        // asks nothing further. That is why the root directory was requested
        // and then never read, and why "cd" into a directory on the disc put
        // you straight back where you started.
        //
        // Bit 0 set with bit 1 set means: this is a completion, travelling
        // from the drive to the host.
        _sectorCount = 0x03;

        RaiseInterrupt();
    }

    private void FailWithSense(byte senseKey, byte code)
    {
        _senseKey = senseKey;
        _senseCode = code;

        // The sense key goes in the top nibble of the error register as well:
        // a driver reads it there before bothering with REQUEST SENSE.
        _error = (byte)((senseKey << 4) | ErrorAbort);
        _status = StatusReady | StatusError;

        _buffer = [];
        _bufferPosition = 0;

        RaiseInterrupt();
    }

    private void BeginTransfer(byte[] data)
    {
        _buffer = data;
        _bufferPosition = 0;

        _senseKey = 0;
        _senseCode = 0;
        _error = 0;
        _status = StatusReady | StatusDataRequest | StatusSeekComplete;

        // How much is on offer. A driver reads this to size its own buffer,
        // and one that trusts it will read exactly this many bytes.
        _lbaMid = (byte)data.Length;
        _lbaHigh = (byte)(data.Length >> 8);

        // Bit 1 set: what follows is data going to the host.
        _sectorCount = 0x02;

        RaiseInterrupt();
    }

    /// <summary>
    /// Signals that a command has finished.
    ///
    /// The line is dropped first, then raised. An IDE interrupt is an edge:
    /// the drive pulls the line and the host clears it by reading the status
    /// register. Raising a line that is already raised produces no new edge,
    /// so a driver that has not read the status yet -- because it polls
    /// instead, which many do -- would leave the line stuck high, and the
    /// interrupt controller would then re-enter the handler on every
    /// instruction. That does not look like a broken CD-ROM. It looks like an
    /// emulator that has become mysteriously slow.
    /// </summary>
    private void RaiseInterrupt()
    {
        _interrupts.LowerIrq(14);
        _interrupts.RaiseIrq(14);
    }
}
