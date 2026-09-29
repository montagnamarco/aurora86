namespace Aurora386.Core.Devices.Storage;

/// <summary>
/// The command-execution half of <see cref="AhciPort"/>: reading a command
/// header and its command table out of guest RAM (AHCI 1.3.1 section 4.2),
/// running the ATA command the table's H2D Register FIS names against the
/// attached <see cref="DiskImage"/>, moving data through the PRDT, and
/// posting the completion FIS into the port's Received FIS area (section
/// 4.2.1). See <c>docs/AHCI.md</c> for the byte layouts this follows.
/// </summary>
internal sealed partial class AhciPort
{
    // --- command header (32 bytes at CLB + slot*32h, section 4.2.2) ------------

    private const int CommandHeaderLength = 0x20;
    private const uint CommandHeaderPrdtlShift = 16;
    private const uint CommandHeaderPrdbcOffset = 0x04;
    private const uint CommandHeaderCtbaOffset = 0x08;

    // --- command FIS (H2D Register FIS at the command table's own offset 0, section 10.3.4) ---

    private const uint CfisCommandOffset = 0x02;
    private const uint CfisFeaturesOffset = 0x03;
    private const uint CfisLba0Offset = 0x04;
    private const uint CfisLba1Offset = 0x05;
    private const uint CfisLba2Offset = 0x06;
    private const uint CfisDeviceOffset = 0x07;
    private const uint CfisLba3Offset = 0x08;
    private const uint CfisLba4Offset = 0x09;
    private const uint CfisLba5Offset = 0x0A;
    private const uint CfisCountLowOffset = 0x0C;
    private const uint CfisCountHighOffset = 0x0D;

    /// <summary>Device register bit 6, "L": set means the LBA0-27 fields name an LBA28 sector; clear means legacy CHS (A5).</summary>
    private const byte DeviceLbaBit = 0x40;

    // --- PRDT (16-byte entries starting at the command table's own offset 80h, section 4.2.3.3) ---

    private const uint PrdtOffset = 0x80;
    private const uint PrdtEntryLength = 0x10;
    private const uint PrdtDbcOffset = 0x0C;
    private const uint PrdtByteCountMask = 0x003F_FFFF; // 22 bits, byte count minus one
    private const uint PrdtInterruptBit = 1u << 31;

    // --- Received FIS area (256 bytes at FB, section 4.2.1) ---------------------

    private const uint ReceivedFisPioSetupOffset = 0x20;
    private const uint ReceivedFisRegisterD2HOffset = 0x40;

    private const byte FisTypeRegisterD2H = 0x34;
    private const byte FisTypePioSetup = 0x5F;
    private const byte D2HInterruptBit = 1 << 6;
    private const byte PioSetupInterruptBit = 1 << 6;
    private const byte PioSetupDeviceToHostBit = 1 << 5;

    private const int D2HFisLength = 20;
    private const int PioSetupFisLength = 20;

    // --- ATA commands this disk accepts (ATA/ATAPI-6 command codes) ------------

    private const byte AtaIdentifyDevice = 0xEC;
    private const byte AtaIdentifyPacketDevice = 0xA1;
    private const byte AtaReadDmaExt = 0x25;
    private const byte AtaReadDma = 0xC8;
    private const byte AtaWriteDmaExt = 0x35;
    private const byte AtaWriteDma = 0xCA;
    private const byte AtaReadSectorsExt = 0x24;
    private const byte AtaReadSectors = 0x20;
    private const byte AtaWriteSectorsExt = 0x34;
    private const byte AtaWriteSectors = 0x30;
    private const byte AtaReadVerifySectors = 0x40;
    private const byte AtaReadVerifySectorsExt = 0x42;
    private const byte AtaFlushCache = 0xE7;
    private const byte AtaFlushCacheExt = 0xEA;
    private const byte AtaSetFeatures = 0xEF;
    private const byte AtaSetMultipleMode = 0xC6;
    private const byte AtaExecuteDeviceDiagnostic = 0x90;
    private const byte AtaCheckPowerMode = 0xE5;
    private const byte AtaIdleImmediate = 0xE1;
    private const byte AtaStandbyImmediate = 0xE0;

    private const byte SetFeaturesTransferMode = 0x03;
    private const byte SetFeaturesEnableWriteCache = 0x02;
    private const byte SetFeaturesDisableWriteCache = 0x82;

    /// <summary>CHECK POWER MODE's success response: the drive reports itself spun up (count = FFh).</summary>
    private const ushort CheckPowerModeActiveCount = 0x00FF;

    /// <summary>Runs the command named by slot's own header and table, returning false when PxTFD.STS.ERR ends up set.</summary>
    private bool ExecuteCommand(int slot)
    {
        uint headerAddress = _clb + (uint)(slot * CommandHeaderLength);
        uint dw0 = _dma.ReadDword(headerAddress);
        int prdtl = (int)(dw0 >> (int)CommandHeaderPrdtlShift);
        uint commandTable = _dma.ReadDword(headerAddress + CommandHeaderCtbaOffset);
        // The command table's own high dword (CTBAU) is ignored, exactly like
        // CLBU and FBU: CAP.S64A is 1 so a driver may legally program it, but
        // this machine's physical address space never needs the extra 32
        // bits, and IDmaMemory already turns a wild high dword's low-address
        // alias into either RAM it does not own or the quiet FFh/no-op an
        // address past RAM gets -- never a crash.

        byte command = _dma.ReadByte(commandTable + CfisCommandOffset);
        byte features = _dma.ReadByte(commandTable + CfisFeaturesOffset);
        byte device = _dma.ReadByte(commandTable + CfisDeviceOffset);
        byte countLow = _dma.ReadByte(commandTable + CfisCountLowOffset);
        byte countHigh = _dma.ReadByte(commandTable + CfisCountHighOffset);

        return command switch
        {
            AtaIdentifyDevice => DoIdentify(headerAddress, commandTable, prdtl),
            AtaIdentifyPacketDevice => Abort(), // this port carries a disk, not an ATAPI device

            AtaReadDmaExt => DoRead(headerAddress, commandTable, prdtl, ReadLba48(commandTable), DecodeCount48(countLow, countHigh), pio: false),
            AtaReadDma => DoRead(headerAddress, commandTable, prdtl, ReadLba28(commandTable, device), DecodeCount28(countLow), pio: false),
            AtaReadSectorsExt => DoRead(headerAddress, commandTable, prdtl, ReadLba48(commandTable), DecodeCount48(countLow, countHigh), pio: true),
            AtaReadSectors => DoRead(headerAddress, commandTable, prdtl, ReadLba28(commandTable, device), DecodeCount28(countLow), pio: true),

            AtaWriteDmaExt => DoWrite(headerAddress, commandTable, prdtl, ReadLba48(commandTable), DecodeCount48(countLow, countHigh)),
            AtaWriteDma => DoWrite(headerAddress, commandTable, prdtl, ReadLba28(commandTable, device), DecodeCount28(countLow)),
            AtaWriteSectorsExt => DoWrite(headerAddress, commandTable, prdtl, ReadLba48(commandTable), DecodeCount48(countLow, countHigh)),
            AtaWriteSectors => DoWrite(headerAddress, commandTable, prdtl, ReadLba28(commandTable, device), DecodeCount28(countLow)),

            AtaReadVerifySectorsExt => DoVerify(ReadLba48(commandTable), DecodeCount48(countLow, countHigh)),
            AtaReadVerifySectors => DoVerify(ReadLba28(commandTable, device), DecodeCount28(countLow)),

            AtaFlushCache or AtaFlushCacheExt => CompleteNonData(),
            AtaSetFeatures => DoSetFeatures(features, countLow),
            AtaSetMultipleMode => CompleteNonData(), // accepted; this model has no per-block interrupt pacing to configure (*choice*)
            AtaExecuteDeviceDiagnostic => DoDiagnostic(),
            AtaCheckPowerMode => DoCheckPowerMode(),
            AtaIdleImmediate or AtaStandbyImmediate => CompleteNonData(),

            _ => Abort(),
        };
    }

    private static int DecodeCount48(byte countLow, byte countHigh)
    {
        int raw = countLow | (countHigh << 8);
        return raw == 0 ? 65536 : raw;
    }

    private static int DecodeCount28(byte countLow) => countLow == 0 ? 256 : countLow;

    private long ReadLba48(uint commandTable)
    {
        long lba0 = _dma.ReadByte(commandTable + CfisLba0Offset);
        long lba1 = _dma.ReadByte(commandTable + CfisLba1Offset);
        long lba2 = _dma.ReadByte(commandTable + CfisLba2Offset);
        long lba3 = _dma.ReadByte(commandTable + CfisLba3Offset);
        long lba4 = _dma.ReadByte(commandTable + CfisLba4Offset);
        long lba5 = _dma.ReadByte(commandTable + CfisLba5Offset);
        return lba0 | (lba1 << 8) | (lba2 << 16) | (lba3 << 24) | (lba4 << 32) | (lba5 << 40);
    }

    /// <summary>
    /// A 28-bit command's address, honouring the device register's own LBA
    /// bit (A5): with it set, bytes 4-6 and the device register's low
    /// nibble spell an LBA28 sector exactly as before. With it clear, the
    /// same three bytes are legacy CHS -- byte 4 the sector number, bytes
    /// 5-6 the cylinder, the device register's low nibble the head -- and
    /// this model resolves it against the attached disk's own physical
    /// geometry, the same fields <see cref="DiskImage.ToLinearSector"/>
    /// already exists to convert: AHCI has no INITIALIZE DEVICE
    /// PARAMETERS-style logical geometry to consult instead, unlike
    /// <see cref="IdeController"/>'s own CHS branch. An invalid CHS address
    /// (out of the disk's own range) reports the same as an out-of-range
    /// LBA would: -1, which <see cref="RangeIsValid"/> turns into IDNF.
    /// </summary>
    private long ReadLba28(uint commandTable, byte device)
    {
        byte lba0 = _dma.ReadByte(commandTable + CfisLba0Offset);
        byte lba1 = _dma.ReadByte(commandTable + CfisLba1Offset);
        byte lba2 = _dma.ReadByte(commandTable + CfisLba2Offset);

        if ((device & DeviceLbaBit) != 0)
        {
            long lba3 = device & 0x0F; // legacy LBA27:24, carried in the device byte's low nibble
            return lba0 | ((long)lba1 << 8) | ((long)lba2 << 16) | (lba3 << 24);
        }

        if (_disk is null)
        {
            return -1;
        }

        int cylinder = (lba2 << 8) | lba1;
        int head = device & 0x0F;
        int sector = lba0;
        return _disk.IsValidChs(cylinder, head, sector) ? _disk.ToLinearSector(cylinder, head, sector) : -1;
    }

    private bool DoIdentify(uint headerAddress, uint commandTable, int prdtl)
    {
        if (_disk is null)
        {
            return Abort();
        }

        byte[] identify = BuildIdentifyBuffer(_disk, _selectedTransferMode);
        int bytesMoved = ScatterToPrdt(commandTable, prdtl, identify);
        _dma.WriteDword(headerAddress + CommandHeaderPrdbcOffset, (uint)bytesMoved);

        if (bytesMoved < identify.Length)
        {
            return PrdtTooShort();
        }

        _tfdStatus = TfdStatusNormal;
        _tfdError = 0;
        PostPioSetupFis(deviceToHost: true, transferByteCount: bytesMoved);
        _isBits |= IsPssBit;
        PostD2HRegisterFis(interrupt: true, status: TfdStatusNormal, error: 0, sectorCount: 0);
        _isBits |= IsDhrsBit;
        return true;
    }

    private bool DoRead(uint headerAddress, uint commandTable, int prdtl, long lba, int sectorCount, bool pio)
    {
        if (_disk is null || !RangeIsValid(lba, sectorCount))
        {
            return Idnf();
        }

        int requiredBytes = sectorCount * DiskImage.SectorSize;
        var sectorData = new byte[requiredBytes];
        if (!_disk.ReadLinear(lba, sectorCount, sectorData))
        {
            return Idnf();
        }

        int bytesMoved = ScatterToPrdt(commandTable, prdtl, sectorData);
        _dma.WriteDword(headerAddress + CommandHeaderPrdbcOffset, (uint)bytesMoved);

        if (bytesMoved < requiredBytes)
        {
            // A2: a PRD table -- including an empty one, PRDTL=0 -- that
            // cannot hold the whole sectorCount*512 transfer is a task-file
            // error, not a silently short one. The bytes that did fit are
            // already in guest RAM (PRDBC above says how many, never more
            // than the table's own entries allowed); the command itself is
            // reported as failed, exactly like an unknown command.
            return PrdtTooShort();
        }

        _tfdStatus = TfdStatusNormal;
        _tfdError = 0;
        if (pio)
        {
            PostPioSetupFis(deviceToHost: true, transferByteCount: bytesMoved);
            _isBits |= IsPssBit;
        }
        PostD2HRegisterFis(interrupt: true, status: TfdStatusNormal, error: 0, sectorCount: (ushort)sectorCount);
        _isBits |= IsDhrsBit;
        return true;
    }

    private bool DoWrite(uint headerAddress, uint commandTable, int prdtl, long lba, int sectorCount)
    {
        if (_disk is null || _disk.ReadOnly)
        {
            return Abort();
        }

        if (!RangeIsValid(lba, sectorCount))
        {
            return Idnf();
        }

        int requiredBytes = sectorCount * DiskImage.SectorSize;
        var buffer = new byte[requiredBytes];
        int bytesGathered = GatherFromPrdt(commandTable, prdtl, buffer);
        _dma.WriteDword(headerAddress + CommandHeaderPrdbcOffset, (uint)bytesGathered);

        if (bytesGathered < requiredBytes)
        {
            // A2, disk integrity: an undersized PRD table -- including an
            // empty one, PRDTL=0 -- never reaches WriteLinear at all. The
            // image is committed only once every byte the command promised
            // has actually been gathered from guest RAM; a driver that
            // mis-described its own PRDT must not leave a half-written
            // sector behind.
            return PrdtTooShort();
        }

        _disk.WriteLinear(lba, sectorCount, buffer);

        _tfdStatus = TfdStatusNormal;
        _tfdError = 0;
        PostD2HRegisterFis(interrupt: true, status: TfdStatusNormal, error: 0, sectorCount: (ushort)sectorCount);
        _isBits |= IsDhrsBit;
        return true;
    }

    private bool DoVerify(long lba, int sectorCount)
    {
        if (_disk is null || !RangeIsValid(lba, sectorCount))
        {
            return Idnf();
        }

        return CompleteNonData();
    }

    /// <summary>
    /// SET FEATURES 03h negotiates a transfer mode, tracked in
    /// <see cref="_selectedTransferMode"/> and reported back through
    /// IDENTIFY words 63 (multiword DMA) and 88 (Ultra DMA, A4). Multiword
    /// DMA only ever goes up to mode 2, matching IDENTIFY word 63's own
    /// advertised range; Ultra DMA goes up to mode 6, matching word 88's.
    /// </summary>
    private bool DoSetFeatures(byte features, byte countLow)
    {
        if (features is SetFeaturesEnableWriteCache or SetFeaturesDisableWriteCache)
        {
            return CompleteNonData(); // every write here is already synchronous to disk; nothing to toggle
        }

        if (features != SetFeaturesTransferMode)
        {
            return Abort();
        }

        if (countLow is 0x00 or 0x01 or (>= 0x08 and <= 0x0C))
        {
            _selectedTransferMode = null; // PIO default/flow-control/PIO3-4: no DMA mode selected
            return CompleteNonData();
        }

        if (countLow is >= 0x20 and <= 0x22)
        {
            _selectedTransferMode = (Ultra: false, Level: countLow - 0x20);
            return CompleteNonData();
        }

        if (countLow is >= 0x40 and <= 0x46)
        {
            _selectedTransferMode = (Ultra: true, Level: countLow - 0x40);
            return CompleteNonData();
        }

        return Abort();
    }

    private bool DoDiagnostic()
    {
        _tfdStatus = TfdStatusNormal;
        _tfdError = AtaDiagnosticPassed;
        PostD2HRegisterFis(interrupt: true, status: TfdStatusNormal, error: AtaDiagnosticPassed, sectorCount: 0);
        _isBits |= IsDhrsBit;
        return true;
    }

    private bool DoCheckPowerMode()
    {
        _tfdStatus = TfdStatusNormal;
        _tfdError = 0;
        PostD2HRegisterFis(interrupt: true, status: TfdStatusNormal, error: 0, sectorCount: CheckPowerModeActiveCount);
        _isBits |= IsDhrsBit;
        return true;
    }

    private bool CompleteNonData()
    {
        _tfdStatus = TfdStatusNormal;
        _tfdError = 0;
        PostD2HRegisterFis(interrupt: true, status: TfdStatusNormal, error: 0, sectorCount: 0);
        _isBits |= IsDhrsBit;
        return true;
    }

    private bool Abort()
    {
        _tfdStatus = TfdStatusError;
        _tfdError = AtaErrorAbort;
        PostD2HRegisterFis(interrupt: true, status: TfdStatusError, error: AtaErrorAbort, sectorCount: 0);
        _isBits |= IsDhrsBit;
        return false;
    }

    private bool Idnf()
    {
        _tfdStatus = TfdStatusError;
        _tfdError = AtaErrorIdNotFound;
        PostD2HRegisterFis(interrupt: true, status: TfdStatusError, error: AtaErrorIdNotFound, sectorCount: 0);
        _isBits |= IsDhrsBit;
        return false;
    }

    /// <summary>
    /// A2: the PRD table could not supply (or receive) the whole transfer.
    /// AHCI 1.3.1 section 6.2.2.1 names PxTFD.STS.ERR / ERR = ABRT (04h) as
    /// the completion status a command the HBA could not carry out reports,
    /// and ProcessCommandList treats any such completion as a task-file
    /// error: PxIS.TFES is set and the command list walk stops, exactly as
    /// for an unrecognised command or an out-of-range LBA. See docs/AHCI.md,
    /// A2, for why this path (rather than PxIS.OFS, the interface's other
    /// AHCI 1.3.1-defined overflow signal) is the one this model uses.
    /// </summary>
    private bool PrdtTooShort() => Abort();

    private bool RangeIsValid(long lba, int sectorCount) =>
        _disk is not null && lba >= 0 && sectorCount > 0 && lba <= _disk.TotalSectors - sectorCount;

    /// <summary>Scatters <paramref name="source"/> into the command table's PRDT entries, in order, until either runs out.</summary>
    private int ScatterToPrdt(uint commandTable, int prdtl, ReadOnlySpan<byte> source)
    {
        int remaining = source.Length;
        int sourceOffset = 0;

        for (int i = 0; i < prdtl && remaining > 0; i++)
        {
            (uint dataBase, int byteCount, bool interruptOnCompletion) = ReadPrdtEntry(commandTable, i);
            int moveNow = Math.Min(byteCount, remaining);

            for (int b = 0; b < moveNow; b++)
            {
                _dma.WriteByte(dataBase + (uint)b, source[sourceOffset + b]);
            }

            sourceOffset += moveNow;
            remaining -= moveNow;

            if (interruptOnCompletion)
            {
                _isBits |= IsDpsBit;
            }
        }

        return sourceOffset;
    }

    /// <summary>Gathers up to <paramref name="destination"/>'s length out of the command table's PRDT entries, in order.</summary>
    private int GatherFromPrdt(uint commandTable, int prdtl, Span<byte> destination)
    {
        int remaining = destination.Length;
        int destinationOffset = 0;

        for (int i = 0; i < prdtl && remaining > 0; i++)
        {
            (uint dataBase, int byteCount, bool interruptOnCompletion) = ReadPrdtEntry(commandTable, i);
            int moveNow = Math.Min(byteCount, remaining);

            for (int b = 0; b < moveNow; b++)
            {
                destination[destinationOffset + b] = _dma.ReadByte(dataBase + (uint)b);
            }

            destinationOffset += moveNow;
            remaining -= moveNow;

            if (interruptOnCompletion)
            {
                _isBits |= IsDpsBit;
            }
        }

        return destinationOffset;
    }

    private (uint DataBase, int ByteCount, bool InterruptOnCompletion) ReadPrdtEntry(uint commandTable, int index)
    {
        uint entryAddress = commandTable + PrdtOffset + (uint)(index * PrdtEntryLength);
        uint dataBase = _dma.ReadDword(entryAddress);
        // The entry's own high dword (DBAU) is ignored, for the same reason CTBAU is: see ExecuteCommand.
        uint dw3 = _dma.ReadDword(entryAddress + PrdtDbcOffset);
        int byteCount = (int)(dw3 & PrdtByteCountMask) + 1;
        bool interruptOnCompletion = (dw3 & PrdtInterruptBit) != 0;
        return (dataBase, byteCount, interruptOnCompletion);
    }

    /// <summary>
    /// Writes a D2H Register FIS (type 34h, section 10.3.4) into the
    /// Received FIS area. Gated on PxCMD.FRE like every FIS this model
    /// posts: without it software never told the HBA it may write there.
    /// <paramref name="lbaLow"/> defaults to 0 for an ordinary command
    /// completion; the COMRESET link-up path in <see cref="WriteSctl"/>
    /// passes 1 alongside sectorCount 1 to spell the ATA disk signature.
    /// </summary>
    private void PostD2HRegisterFis(bool interrupt, byte status, byte error, ushort sectorCount, byte lbaLow = 0)
    {
        if (!_fre)
        {
            return;
        }

        Span<byte> fis = stackalloc byte[D2HFisLength];
        fis[0] = FisTypeRegisterD2H;
        fis[1] = interrupt ? D2HInterruptBit : (byte)0;
        fis[2] = status;
        fis[3] = error;
        fis[4] = lbaLow;
        fis[12] = (byte)sectorCount;
        fis[13] = (byte)(sectorCount >> 8);

        WriteFisBytes(_fb + ReceivedFisRegisterD2HOffset, fis);
    }

    /// <summary>Writes a PIO Setup FIS (type 5Fh, section 10.3.6) ahead of the D2H FIS for a PIO data-in command -- see the class summary for what triggers this.</summary>
    private void PostPioSetupFis(bool deviceToHost, int transferByteCount)
    {
        if (!_fre)
        {
            return;
        }

        Span<byte> fis = stackalloc byte[PioSetupFisLength];
        fis[0] = FisTypePioSetup;
        fis[1] = (byte)(PioSetupInterruptBit | (deviceToHost ? PioSetupDeviceToHostBit : 0));
        fis[2] = _tfdStatus;
        fis[3] = _tfdError;
        fis[15] = _tfdStatus; // E_Status: the status after the PIO transfer, same value since this engine is synchronous
        fis[16] = (byte)transferByteCount;
        fis[17] = (byte)(transferByteCount >> 8);

        WriteFisBytes(_fb + ReceivedFisPioSetupOffset, fis);
    }

    private void WriteFisBytes(uint address, ReadOnlySpan<byte> fis)
    {
        for (int i = 0; i < fis.Length; i++)
        {
            _dma.WriteByte(address + (uint)i, fis[i]);
        }
    }

    /// <summary>Multiword DMA modes 0-2 supported (IDENTIFY word 63, A4): the highest legacy multiword mode this drive names.</summary>
    private const ushort MultiwordDmaModesSupported = 0x0007;

    /// <summary>Ultra DMA modes 0-6 supported (IDENTIFY word 88, A4).</summary>
    private const ushort UltraDmaModesSupported = 0x007F;

    /// <summary>The highest sector count IDENTIFY words 57-58/60-61 (LBA28 total sectors) may report; AHCI 1.3.1 leaves this ATA-6 convention as the signal that LBA48 is needed for the rest (A4).</summary>
    private const uint Lba28MaxSectors = 0x0FFF_FFFF;

    /// <summary>
    /// IDENTIFY DEVICE's 512-byte word array. Follows
    /// <see cref="IdeController.IdentifyDisk"/>'s field choices for the words
    /// shared with the legacy ATA disk, and adds what a SATA/AHCI disk needs
    /// beyond it: word 49 bit 8 (DMA), words 63/88 (multiword/Ultra DMA modes
    /// supported and selected, A4), words 76-79 (SATA capabilities: Gen1,
    /// Gen2 and Gen3 support, matching CAP.ISS=3, A4), word 80 through ATA-6
    /// (needed to claim LBA48), words 83/86 bit 10 with their own validity
    /// bit 14 (LBA48 supported and enabled) and words 100-103 (the 48-bit
    /// total sector count).
    /// </summary>
    private static byte[] BuildIdentifyBuffer(DiskImage disk, (bool Ultra, int Level)? selectedTransferMode)
    {
        const int MaxMultiple = 16;
        var words = new ushort[256];

        words[0] = 0x0040; // a fixed disk
        words[1] = (ushort)Math.Min(disk.Cylinders, 16383);
        words[3] = (ushort)disk.Heads;
        words[6] = (ushort)disk.SectorsPerTrack;
        WriteIdentifyText(words, 10, "AURORA386AHCI0000000", 10);
        WriteIdentifyText(words, 23, "1.0     ", 4);
        WriteIdentifyText(words, 27, "Aurora386 Virtual SATA Disk", 20);
        words[47] = 0x8000 | MaxMultiple;
        words[49] = 0x0300; // LBA (bit 9) and DMA (bit 8) supported
        words[51] = 0x0200; // PIO timing mode 2
        words[53] = 0x0006; // words 64-70 and 88 are valid

        long totalSectors = disk.TotalSectors;
        // ATA-6 convention (A4): words 57-58 and 60-61 cap at the LBA28
        // maximum rather than wrapping at 32 bits -- a driver sees this
        // capped value and knows to fall back to LBA48 (words 100-103,
        // below) for the rest of the disk's own real capacity.
        uint capacity28 = (uint)Math.Min(totalSectors, Lba28MaxSectors);
        words[54] = (ushort)Math.Min(totalSectors / ((long)disk.Heads * disk.SectorsPerTrack), 16383);
        words[55] = (ushort)disk.Heads;
        words[56] = (ushort)disk.SectorsPerTrack;
        words[57] = (ushort)capacity28;
        words[58] = (ushort)(capacity28 >> 16);
        words[60] = (ushort)capacity28;
        words[61] = (ushort)(capacity28 >> 16);
        words[63] = (ushort)(MultiwordDmaModesSupported | SelectedTransferModeBit(selectedTransferMode, ultra: false));
        words[64] = 0x0003; // PIO modes 3 and 4

        words[76] = 0x000E; // SATA Gen1 (bit 1) + Gen2 (bit 2) + Gen3 (bit 3) supported, matching CAP.ISS=3
        words[80] = 0x007E; // ATA/ATAPI-1 through -6 (LBA48 needs -6)
        words[88] = (ushort)(UltraDmaModesSupported | SelectedTransferModeBit(selectedTransferMode, ultra: true));

        const ushort ValidBit14 = 1 << 14;
        const ushort Lba48Bit = 1 << 10;
        words[83] = ValidBit14 | Lba48Bit;
        words[86] = ValidBit14 | Lba48Bit;
        words[87] = ValidBit14;

        words[100] = (ushort)totalSectors;
        words[101] = (ushort)(totalSectors >> 16);
        words[102] = (ushort)(totalSectors >> 32);
        words[103] = (ushort)(totalSectors >> 48);

        var bytes = new byte[512];
        for (int i = 0; i < words.Length; i++)
        {
            bytes[i * 2] = (byte)words[i];
            bytes[i * 2 + 1] = (byte)(words[i] >> 8);
        }
        return bytes;
    }

    /// <summary>Bit 8+level of IDENTIFY word 63 or 88: which multiword or Ultra DMA mode SET FEATURES 03h last selected, if any of that same kind (A4).</summary>
    private static ushort SelectedTransferModeBit((bool Ultra, int Level)? selectedTransferMode, bool ultra)
    {
        if (selectedTransferMode is not { } mode || mode.Ultra != ultra)
        {
            return 0;
        }
        return (ushort)(1 << (8 + mode.Level));
    }

    /// <summary>Word-swapped ASCII, exactly as <see cref="IdeController"/>'s own IDENTIFY text fields are: see that class for why the bytes are swapped within each word.</summary>
    private static void WriteIdentifyText(ushort[] words, int firstWord, string text, int wordCount)
    {
        string padded = text.PadRight(wordCount * 2)[..(wordCount * 2)];
        for (int i = 0; i < wordCount; i++)
        {
            words[firstWord + i] = (ushort)((padded[i * 2] << 8) | padded[i * 2 + 1]);
        }
    }
}
