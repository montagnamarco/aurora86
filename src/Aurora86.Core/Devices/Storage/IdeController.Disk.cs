namespace Aurora86.Core.Devices.Storage;

/// <summary>
/// The hard disk on the IDE cable: an ATA drive as the master, in front of
/// the CD-ROM that is the slave.
///
/// DOS never talks to it -- the BIOS does, out of the same image -- but
/// Windows does. The 32-bit disk access of Windows 3.1 and the
/// protected-mode driver of Windows 95 both bypass the BIOS and drive these
/// registers directly, and without a disk answering here Windows 95 runs
/// every disk access through DOS in "compatibility mode", which is slow and
/// which it complains about. The registers are the ones of 1989: a sector
/// count, a 28-bit address spread over four registers that also spell a
/// cylinder, head and sector, and a data port through which every sector
/// passes a word at a time.
/// </summary>
public sealed partial class IdeController
{
    /// <summary>
    /// Raised after READ DMA or WRITE DMA has captured the ATA task-file
    /// range. The bus-master engine may already have its Start bit set: the
    /// SFF sequence permits either programming order, so it must retry when
    /// the second half arrives.
    /// </summary>
    internal event Action? BusMasterTransferArmed;

    private DiskImage? _disk;

    private byte _diskStatus = StatusReady | StatusSeekComplete;
    private byte _diskError;
    private byte[] _diskBuffer = [];
    private int _diskPosition;
    private bool _diskSignaturePending;

    // A transfer in progress: where the next block comes from or goes to,
    // how many sectors are left, and how many make a block. A block is one
    // sector for READ/WRITE SECTORS and the number set by SET MULTIPLE MODE
    // for READ/WRITE MULTIPLE; the drive interrupts once per block.
    private long _diskNextSector;
    private int _diskRemaining;
    private int _diskBlock = 1;
    private bool _diskWriting;
    private int _diskMultiple;

    // The geometry the host last set with INITIALIZE DEVICE PARAMETERS,
    // which is what CHS addresses are decoded with. It starts as the
    // disk's own.
    private int _logicalHeads;
    private int _logicalSectors;

    private const byte ErrorIdNotFound = 0x10;
    private const int MaxMultiple = 16;

    // ---- bus-master DMA (P2.4, i430TX only: see _hasBusMasterDma) ------------

    /// <summary>
    /// READ DMA/WRITE DMA (0xC8/0xCA) armed but not yet moved: the sector
    /// range the command named, waiting for <see
    /// cref="BusMasterIdeController"/> to actually walk the PRD table and
    /// call back in. SFF-8038i keeps the ATA command and the bus-master
    /// engine's start bit as two independent steps -- see <see
    /// cref="BeginBusMasterCommand"/> -- so this is what remembers the first
    /// one until the second arrives, however long the guest waits between
    /// them, including across a snapshot restore.
    /// </summary>
    private BusMasterTransferRequest? _pendingBusMasterTransfer;

    /// <summary>The last transfer mode a SET FEATURES 03h selected, or null before any has been (PIO only).</summary>
    private (bool Ultra, int Level)? _selectedTransferMode;

    /// <summary>The DMA mode currently reported through IDENTIFY DEVICE.</summary>
    internal (bool Ultra, int Level)? SelectedBusMasterTransferMode => _selectedTransferMode;

    /// <summary>Restores the negotiated DMA mode without issuing an ATA command.</summary>
    internal void RestoreSelectedBusMasterTransferMode((bool Ultra, int Level)? mode)
    {
        _selectedTransferMode = mode;
    }

    /// <summary>A READ/WRITE DMA command's sector range, recorded until the bus-master engine starts moving it.</summary>
    internal readonly record struct BusMasterTransferRequest(long Lba, int SectorCount, bool WriteToDisk);

    /// <summary>The request a READ/WRITE DMA command armed, for <see cref="MachineSnapshot"/> to carry across a restore.</summary>
    internal BusMasterTransferRequest? PendingBusMasterTransfer => _pendingBusMasterTransfer;

    /// <summary>Puts back a request a snapshot captured -- the disk-side half of restoring a configured-but-not-started bus-master transfer.</summary>
    internal void RestorePendingBusMasterTransfer(BusMasterTransferRequest? request)
    {
        _pendingBusMasterTransfer = request;
    }

    /// <summary>Discards a DMA command a controller reset interrupted before it moved data.</summary>
    internal void ClearPendingBusMasterTransfer()
    {
        _pendingBusMasterTransfer = null;
    }

    /// <summary>
    /// Takes the armed request away, for <see
    /// cref="BusMasterIdeController"/> to consume the instant the guest sets
    /// BMICOM's start bit. A second start with nothing armed -- no command
    /// issued since the last one ran, or none at all -- finds null and moves
    /// no bytes, exactly as a real engine walking a schedule the drive never
    /// asked for would move none.
    /// </summary>
    internal BusMasterTransferRequest? TakePendingBusMasterTransfer()
    {
        BusMasterTransferRequest? request = _pendingBusMasterTransfer;
        _pendingBusMasterTransfer = null;
        return request;
    }

    /// <summary>
    /// The disk-side half of a completed bus-master READ DMA: the sectors
    /// the request named are read straight into the buffer the engine is
    /// about to hand to the PRD table, and the drive reports the same way a
    /// PIO READ SECTORS command's last block does -- ready, no error,
    /// INTRQ raised. A disk failure (the range went bad between the command
    /// and the engine starting, which nothing in this emulator actually
    /// causes but a real drive could hit) aborts with ID-not-found instead,
    /// recording completion even if nIEN suppresses physical INTRQ. The
    /// engine handles completion even when the cable remains low.
    /// </summary>
    internal bool FinishBusMasterRead(BusMasterTransferRequest request, byte[] buffer, Action beforeInterrupt)
    {
        if (_disk is null || request.Lba > _disk.TotalSectors - request.SectorCount ||
            !_disk.ReadLinear(request.Lba, request.SectorCount, buffer))
        {
            FailDiskCommand(ErrorIdNotFound);
            return false;
        }

        beforeInterrupt();
        _diskStatus = StatusReady | StatusSeekComplete;
        RaiseInterrupt();
        return true;
    }

    /// <summary>The write counterpart of <see cref="FinishBusMasterRead"/>: the engine's own buffer, already filled from RAM, is committed to disk.</summary>
    internal bool FinishBusMasterWrite(BusMasterTransferRequest request, byte[] buffer)
    {
        if (_disk is null || request.Lba > _disk.TotalSectors - request.SectorCount ||
            !_disk.WriteLinear(request.Lba, request.SectorCount, buffer))
        {
            FailDiskCommand(ErrorIdNotFound);
            return false;
        }

        _diskStatus = StatusReady | StatusSeekComplete;
        RaiseInterrupt();
        return true;
    }

    /// <summary>
    /// Commits whole sectors supplied before a short PRD table ends. An
    /// incomplete sector is never written, and this is not ATA completion:
    /// the driver receives no interrupt and must recover the stopped command.
    /// </summary>
    internal void WriteShortBusMasterPrefix(BusMasterTransferRequest request, byte[] buffer)
    {
        int completeSectors = buffer.Length / DiskImage.SectorSize;
        if (completeSectors == 0 || _disk is null)
        {
            return;
        }

        if (request.Lba <= _disk.TotalSectors - completeSectors)
        {
            _disk.WriteLinear(request.Lba, completeSectors, buffer);
        }
    }

    /// <summary>The disk on the master slot, or null for an empty slot.</summary>
    public DiskImage? HardDisk => _disk;

    /// <summary>Puts a disk in the master slot, or takes it out with null.</summary>
    public void AttachHardDisk(DiskImage? image)
    {
        _disk = image;
        ResetDisk();
    }

    private bool AddressingDisk => !_slaveSelected && _disk is not null;

    private void ResetDisk()
    {
        _diskStatus = StatusReady | StatusSeekComplete;
        _diskError = 0x01;                 // the diagnostic code for "passed"
        _diskBuffer = [];
        _diskPosition = 0;
        _diskRemaining = 0;
        _diskWriting = false;
        _diskMultiple = 0;
        _pendingBusMasterTransfer = null;
        _selectedTransferMode = null;
        _diskSignaturePending = _disk is not null;
        _logicalHeads = _disk?.Heads ?? 16;
        _logicalSectors = _disk?.SectorsPerTrack ?? 63;
    }

    private void ExecuteDiskCommand(byte command)
    {
        _diskError = 0;

        switch (command)
        {
            case 0xEC: IdentifyDisk(); return;

            case 0x20 or 0x21: BeginDiskRead(multiple: false); return;
            case 0xC4: BeginDiskRead(multiple: true); return;
            case 0x30 or 0x31: BeginDiskWrite(multiple: false); return;
            case 0xC5: BeginDiskWrite(multiple: true); return;

            case 0xC8 or 0xC9:
                BeginBusMasterCommand(writeToDisk: false);
                return;

            case 0xCA or 0xCB:
                BeginBusMasterCommand(writeToDisk: true);
                return;

            case 0xC6:
                // SET MULTIPLE MODE: how many sectors move per interrupt
                // from now on. Zero switches the multiple commands off.
                if (_sectorCount > MaxMultiple) { AbortDiskCommand(); return; }
                _diskMultiple = _sectorCount;
                CompleteDiskCommand();
                return;

            case 0x40 or 0x41:
                // READ VERIFY SECTORS: nothing is transferred, only checked.
                if (!DiskRangeIsValid(DiskAddress(), DiskCount())) { FailDiskCommand(ErrorIdNotFound); return; }
                CompleteDiskCommand();
                return;

            case 0x91:
                // INITIALIZE DEVICE PARAMETERS: the geometry the host wants
                // CHS addresses read in, which need not be the disk's own.
                _logicalHeads = (_driveHead & 0x0F) + 1;
                _logicalSectors = _sectorCount == 0 ? _disk!.SectorsPerTrack : _sectorCount;
                CompleteDiskCommand();
                return;

            case 0xEF:
                SetTransferMode();
                return;

            case >= 0x10 and <= 0x1F:   // RECALIBRATE
            case >= 0x70 and <= 0x7F:   // SEEK
            case 0xE0 or 0xE1 or 0xE2 or 0xE3 or 0xE6:   // power management
            case 0xE7:                  // FLUSH CACHE
                CompleteDiskCommand();
                return;

            case 0xE5:                  // CHECK POWER MODE: always spun up
                _sectorCount = 0xFF;
                CompleteDiskCommand();
                return;

            case 0x90:                  // EXECUTE DEVICE DIAGNOSTIC
                _diskError = 0x01;
                CompleteDiskCommand();
                return;

            default:
                // PACKET, IDENTIFY PACKET DEVICE, DEVICE RESET, NOP and
                // anything unknown: a disk aborts them.
                AbortDiskCommand();
                return;
        }
    }

    private int DiskCount() => _sectorCount == 0 ? 256 : _sectorCount;

    /// <summary>
    /// Captures an ATA DMA request without making the data port ready. DMA
    /// commands are accepted only on the PIIX4 path; the i430FX controller
    /// retains its historic PIO-only abort for every one of these opcodes.
    /// </summary>
    private void BeginBusMasterCommand(bool writeToDisk)
    {
        if (!_hasBusMasterDma)
        {
            AbortDiskCommand();
            return;
        }

        long first = DiskAddress();
        int count = DiskCount();
        if (!DiskRangeIsValid(first, count) || writeToDisk && _disk!.ReadOnly)
        {
            AbortDiskCommand();
            return;
        }

        _diskWriting = false;
        _diskRemaining = 0;
        _diskBuffer = [];
        _diskStatus = StatusReady | StatusSeekComplete;
        _pendingBusMasterTransfer = new BusMasterTransferRequest(first, count, writeToDisk);
        BusMasterTransferArmed?.Invoke();
    }

    /// <summary>
    /// Implements SET FEATURES subcommand 03h for the modes PIIX4 can use.
    /// ATA-4 gives the requested transfer mode in the sector-count register:
    /// 20h-22h name multiword DMA 0-2 and 40h-42h name Ultra DMA 0-2.
    /// Unsupported requests abort, which is what makes a driver fall back
    /// rather than believing it negotiated a timing this controller lacks.
    /// </summary>
    private void SetTransferMode()
    {
        if (!_hasBusMasterDma || _features != 0x03)
        {
            CompleteDiskCommand();
            return;
        }

        byte requested = _sectorCount;
        if (requested is 0x00 or 0x01 or >= 0x08 and <= 0x0C)
        {
            // PIO default, flow-control and PIO 3/4 are valid SET FEATURES
            // requests. This disk advertises PIO 3/4 and performs every PIO
            // transfer through the same port engine, so no separate timing
            // state is observable; selecting one ends any DMA selection.
            _selectedTransferMode = null;
            CompleteDiskCommand();
            return;
        }

        if (requested is >= 0x20 and <= 0x22)
        {
            _selectedTransferMode = (false, requested - 0x20);
            CompleteDiskCommand();
            return;
        }

        if (requested is >= 0x40 and <= 0x42)
        {
            _selectedTransferMode = (true, requested - 0x40);
            CompleteDiskCommand();
            return;
        }

        AbortDiskCommand();
    }

    /// <summary>
    /// The sector the address registers name. Bit 6 of the drive/head
    /// register chooses between a 28-bit sector number and a cylinder, head
    /// and sector, and the same four registers spell both.
    /// </summary>
    private long DiskAddress()
    {
        if ((_driveHead & 0x40) != 0)
            return ((long)(_driveHead & 0x0F) << 24) | ((long)_lbaHigh << 16) | ((long)_lbaMid << 8) | _lbaLow;

        int cylinder = (_lbaHigh << 8) | _lbaMid;
        int head = _driveHead & 0x0F;
        int sector = _lbaLow;
        if (sector < 1)
        {
            return -1;
        }
        return ((long)cylinder * _logicalHeads + head) * _logicalSectors + (sector - 1);
    }

    private bool DiskRangeIsValid(long first, int count) =>
        first >= 0 && count > 0 && first + count <= _disk!.TotalSectors;

    private void BeginDiskRead(bool multiple)
    {
        long first = DiskAddress();
        int count = DiskCount();
        if (!DiskRangeIsValid(first, count)) { FailDiskCommand(ErrorIdNotFound); return; }
        if (multiple && _diskMultiple == 0) { AbortDiskCommand(); return; }

        _diskWriting = false;
        _diskNextSector = first;
        _diskRemaining = count;
        _diskBlock = multiple ? _diskMultiple : 1;
        LoadNextReadBlock();
    }

    private void LoadNextReadBlock()
    {
        int sectors = Math.Min(_diskBlock, _diskRemaining);
        _diskBuffer = new byte[sectors * DiskImage.SectorSize];
        if (!_disk!.ReadLinear(_diskNextSector, sectors, _diskBuffer))
        {
            FailDiskCommand(ErrorIdNotFound);
            return;
        }
        _diskPosition = 0;
        _diskNextSector += sectors;
        _diskRemaining -= sectors;
        _diskStatus = StatusReady | StatusSeekComplete | StatusDataRequest;
        RaiseInterrupt();
    }

    private byte ReadDiskData()
    {
        if (_diskWriting || _diskPosition >= _diskBuffer.Length)
        {
            return 0;
        }

        byte value = _diskBuffer[_diskPosition++];
        if (_diskPosition >= _diskBuffer.Length)
        {
            if (_diskRemaining > 0)
            {
                LoadNextReadBlock();
            }
            else _diskStatus = StatusReady | StatusSeekComplete;
        }
        return value;
    }

    private void BeginDiskWrite(bool multiple)
    {
        long first = DiskAddress();
        int count = DiskCount();
        if (!DiskRangeIsValid(first, count)) { FailDiskCommand(ErrorIdNotFound); return; }
        if (multiple && _diskMultiple == 0) { AbortDiskCommand(); return; }
        if (_disk!.ReadOnly) { AbortDiskCommand(); return; }

        _diskWriting = true;
        _diskNextSector = first;
        _diskRemaining = count;
        _diskBlock = multiple ? _diskMultiple : 1;

        // A write asks for data without interrupting first: the interrupt
        // comes after each block has been taken.
        PrepareNextWriteBlock();
    }

    private void PrepareNextWriteBlock()
    {
        int sectors = Math.Min(_diskBlock, _diskRemaining);
        _diskBuffer = new byte[sectors * DiskImage.SectorSize];
        _diskPosition = 0;
        _diskStatus = StatusReady | StatusSeekComplete | StatusDataRequest;
    }

    private void WriteDiskData(byte value)
    {
        if (!_diskWriting || _diskPosition >= _diskBuffer.Length)
        {
            return;
        }

        _diskBuffer[_diskPosition++] = value;
        if (_diskPosition < _diskBuffer.Length)
        {
            return;
        }

        int sectors = _diskBuffer.Length / DiskImage.SectorSize;
        if (!_disk!.WriteLinear(_diskNextSector, sectors, _diskBuffer))
        {
            FailDiskCommand(ErrorIdNotFound);
            return;
        }
        _diskNextSector += sectors;
        _diskRemaining -= sectors;

        if (_diskRemaining > 0)
        {
            PrepareNextWriteBlock();
        }
        else
        {
            _diskWriting = false;
            _diskStatus = StatusReady | StatusSeekComplete;
        }
        RaiseInterrupt();
    }

    private void CompleteDiskCommand()
    {
        _diskStatus = StatusReady | StatusSeekComplete;
        RaiseInterrupt();
    }

    private void AbortDiskCommand()
    {
        _diskStatus = StatusReady | StatusSeekComplete | StatusError;
        _diskError = ErrorAbort;
        RaiseInterrupt();
    }

    private void FailDiskCommand(byte error)
    {
        _diskWriting = false;
        _diskBuffer = [];
        _diskStatus = StatusReady | StatusSeekComplete | StatusError;
        _diskError = error;
        RaiseInterrupt();
    }

    /// <summary>
    /// IDENTIFY DEVICE: 256 words about the drive. A driver reads the
    /// geometry, the sector count and whether the drive takes sector
    /// numbers, and shows the model name to the user.
    /// </summary>
    private void IdentifyDisk()
    {
        DiskImage disk = _disk!;
        var words = new ushort[256];

        words[0] = 0x0040;                                     // a fixed disk
        words[1] = (ushort)Math.Min(disk.Cylinders, 16383);
        words[3] = (ushort)disk.Heads;
        words[6] = (ushort)disk.SectorsPerTrack;
        WriteText(words, 10, "AURORA86HD000000000", 10);
        WriteText(words, 23, "1.0     ", 4);
        WriteText(words, 27, "Aurora86 Virtual Hard Disk", 20);
        words[47] = 0x8000 | MaxMultiple;                      // sectors per interrupt, at most
        words[49] = (ushort)(0x0200 | (_hasBusMasterDma ? 0x0100 : 0)); // LBA and PIIX4 DMA
        words[51] = 0x0200;                                    // PIO timing mode 2
        words[53] = (ushort)(0x0003 | (_hasBusMasterDma ? 0x0004 : 0)); // 54-58, 64-70 and 88
        words[54] = (ushort)Math.Min(disk.TotalSectors / ((long)_logicalHeads * _logicalSectors), 16383);
        words[55] = (ushort)_logicalHeads;
        words[56] = (ushort)_logicalSectors;
        uint capacity = (uint)Math.Min(disk.TotalSectors, uint.MaxValue);
        words[57] = (ushort)capacity;
        words[58] = (ushort)(capacity >> 16);
        words[59] = (ushort)(_diskMultiple != 0 ? 0x0100 | _diskMultiple : 0);
        words[60] = (ushort)capacity;
        words[61] = (ushort)(capacity >> 16);
        words[64] = 0x0003;                                    // PIO modes 3 and 4
        if (_hasBusMasterDma)
        {
            words[63] = SelectedModeWord(ultra: false);         // multiword DMA 0-2
            words[88] = SelectedModeWord(ultra: true);          // Ultra DMA 0-2
        }
        words[80] = 0x001E;                                    // ATA-1 to ATA-4

        var bytes = new byte[512];
        for (int i = 0; i < words.Length; i++)
        {
            bytes[i * 2] = (byte)words[i];
            bytes[i * 2 + 1] = (byte)(words[i] >> 8);
        }

        _diskWriting = false;
        _diskRemaining = 0;
        _diskBuffer = bytes;
        _diskPosition = 0;
        _diskStatus = StatusReady | StatusSeekComplete | StatusDataRequest;
        RaiseInterrupt();
    }

    /// <summary>Builds ATA IDENTIFY's supported and selected DMA mode word.</summary>
    private ushort SelectedModeWord(bool ultra)
    {
        const ushort ModesZeroToTwo = 0x0007;
        if (_selectedTransferMode is not { } selected || selected.Ultra != ultra)
        {
            return ModesZeroToTwo;
        }

        return (ushort)(ModesZeroToTwo | (1 << (8 + selected.Level)));
    }
}
