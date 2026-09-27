using Aurora386.Core.Devices.Pci;
using Aurora386.Core.IO;

namespace Aurora386.Core.Devices.Storage;

/// <summary>
/// The PIIX4's SFF-8038i bus-master IDE block in Intel's 1997
/// 82371AB south bridge. A protected-mode disk driver fills a physical-region
/// descriptor table in RAM, gives its address to this 16-byte I/O window and
/// then lets READ DMA or WRITE DMA move ATA sectors without using port 1F0h.
///
/// The transfer itself is synchronous because this machine has no PCI DMA
/// scheduler. It models the register outcomes a driver relies on, including
/// independent ATA-command and Start steps, the INTRQ latch, and short/long
/// table ends, but it does not model PCI arbitration or time passing while a
/// transfer is active.
/// </summary>
public sealed class BusMasterIdeController : IIoDwordDevice
{
    /// <summary>The SFF-8038i BAR contains two eight-byte channel blocks.</summary>
    public const int WindowLength = 16;

    /// <summary>The version of the BMID payload carried in a machine snapshot.</summary>
    public const int SnapshotVersion = 3;

    /// <summary>The IDE function's BMIBA is BAR4 in PCI configuration space.</summary>
    private const int BarIndex = 4;
    private const byte Start = 0x01;
    // Bit 3 chooses PCI bus direction, not ATA data direction. A set bit
    // makes PCI write system memory, hence disk -> memory for READ DMA.
    private const byte DirectionDiskToMemory = 0x08;
    private const byte StatusActive = 0x01;
    private const byte StatusError = 0x02;
    private const byte StatusInterrupt = 0x04;
    private const byte DriveZeroDmaCapable = 0x20;
    private const byte DriveOneDmaCapable = 0x40;
    private const byte StatusWritable = StatusError | StatusInterrupt |
                                        DriveZeroDmaCapable | DriveOneDmaCapable;
    private const int DescriptorLength = 8;
    private const int MaximumDescriptors = 512;
    private const uint DescriptorEndOfTable = 0x8000_0000;
    private const uint DescriptorByteCountMask = 0x0000_FFFF;
    private const uint RegionBoundary64KMask = 0x0000_FFFF;
    private const int RegionBoundary64KLength = 0x1_0000;
    private const uint TableBoundary4KMask = 0x0000_0FFF;
    private const int TableBoundary4KLength = 0x1000;
    private const long MaximumAta28Lba = 0x0FFF_FFFF;

    private readonly IDmaMemory _dmaMemory;
    private readonly PciIoBarWindow _window;
    private readonly ChannelState[] _channels;
    private bool _restoring;

    /// <summary>The PCI IDE function which gates this engine's I/O BAR and DMA.</summary>
    public PciFunction Function { get; }

    /// <summary>
    /// Builds the two-channel PIIX4 BMIDE function. The owner defines BMIBA
    /// on <paramref name="function"/> before this constructor; this class
    /// attaches that BAR to <paramref name="io"/> and does not alter PCI
    /// topology or configuration policy.
    /// </summary>
    public BusMasterIdeController(PciFunction function, IoBus io, IDmaMemory dmaMemory,
                                  IdeController primary, IdeController secondary)
    {
        Function = function;
        _dmaMemory = dmaMemory;
        _channels = [new ChannelState(primary), new ChannelState(secondary)];
        _window = new PciIoBarWindow(Function, BarIndex, WindowLength, io, this);

        primary.BusMasterTransferArmed += () => TryStartTransfer(0);
        secondary.BusMasterTransferArmed += () => TryStartTransfer(1);
        primary.DeviceInterruptRaised += () => LatchAtaInterrupt(0);
        secondary.DeviceInterruptRaised += () => LatchAtaInterrupt(1);
        Function.ConfigurationChanged += StopWhenBusMasterIsDisabled;
    }

    /// <summary>Clears volatile engine state while leaving drive DMA-capable bits for firmware to initialise again.</summary>
    public void Reset()
    {
        foreach (ChannelState channel in _channels)
        {
            channel.Command = 0;
            channel.Status = 0;
            channel.DescriptorTable = 0;
            channel.AwaitingAtaInterrupt = false;
            channel.TableWasLongerThanTransfer = false;
            channel.Ide.ClearPendingBusMasterTransfer();
        }
    }

    /// <summary>
    /// Supplies the BIOS-owned BMISTA capability flags after POST reset.
    /// Only each cable's master disk supports DMA here; the primary slave
    /// ATAPI device remains PIO-only. Drivers may subsequently change these flags.
    /// </summary>
    internal void InitializeDriveCapabilities()
    {
        foreach (ChannelState channel in _channels)
        {
            channel.Status &= unchecked((byte)~(DriveZeroDmaCapable | DriveOneDmaCapable));
            if (channel.Ide.HardDisk is not null)
            {
                channel.Status |= DriveZeroDmaCapable;
            }
        }
    }

    public byte ReadByte(ushort port)
    {
        int offset = Offset(port);
        if (offset < 0)
        {
            return 0xFF;
        }

        int channelIndex = offset / 8;
        int register = offset % 8;
        ChannelState channel = _channels[channelIndex];
        return register switch
        {
            0 => channel.Command,
            2 => channel.Status,
            >= 4 and <= 7 => (byte)(channel.DescriptorTable >> ((register - 4) * 8)),
            _ => 0,
        };
    }

    public ushort ReadWord(ushort port)
    {
        return (ushort)(ReadByte(port) | (ReadByte((ushort)(port + 1)) << 8));
    }

    public uint ReadDword(ushort port)
    {
        return (uint)(ReadByte(port) | (ReadByte((ushort)(port + 1)) << 8) |
                      (ReadByte((ushort)(port + 2)) << 16) | (ReadByte((ushort)(port + 3)) << 24));
    }

    public void WriteByte(ushort port, byte value)
    {
        int offset = Offset(port);
        if (offset < 0)
        {
            return;
        }

        int channelIndex = offset / 8;
        int register = offset % 8;
        ChannelState channel = _channels[channelIndex];
        switch (register)
        {
            case 0:
                WriteCommand(channelIndex, value);
                return;

            case 2:
                // SFF-8038i's Error and Interrupt bits are write-one-to-clear.
                // Bits 5 and 6 are firmware/driver storage for each drive.
                byte clearedLatches = (byte)(channel.Status & ~(value & (StatusError | StatusInterrupt)));
                channel.Status = (byte)((clearedLatches & ~(DriveZeroDmaCapable | DriveOneDmaCapable)) |
                                        (value & (DriveZeroDmaCapable | DriveOneDmaCapable)));
                return;

            case >= 4 and <= 7:
                int shift = (register - 4) * 8;
                channel.DescriptorTable = (channel.DescriptorTable & ~(0xFFu << shift)) |
                                          ((uint)value << shift);
                channel.DescriptorTable &= 0xFFFF_FFFCu;
                return;
        }
    }

    public void WriteWord(ushort port, ushort value)
    {
        WriteByte(port, (byte)value);
        WriteByte((ushort)(port + 1), (byte)(value >> 8));
    }

    public void WriteDword(ushort port, uint value)
    {
        WriteByte(port, (byte)value);
        WriteByte((ushort)(port + 1), (byte)(value >> 8));
        WriteByte((ushort)(port + 2), (byte)(value >> 16));
        WriteByte((ushort)(port + 3), (byte)(value >> 24));
    }

    /// <summary>
    /// Serialises the engine and the ATA request/mode state it owns. PCI
    /// configuration is captured separately by <see cref="PciBus"/>; no
    /// restore here performs a transfer, even if Start was set at capture.
    /// </summary>
    public byte[] Capture()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)SnapshotVersion);

        foreach (ChannelState channel in _channels)
        {
            writer.Write(channel.Command);
            writer.Write(channel.Status);
            writer.Write(channel.DescriptorTable);
            writer.Write(channel.TableWasLongerThanTransfer);
            WriteAtaLatches(writer, channel.Ide.CaptureBusMasterAtaLatches());
            WriteAtaState(writer, channel.Ide);
        }

        return stream.ToArray();
    }

    /// <summary>Checks a BMIDE snapshot payload without changing the machine.</summary>
    public static void ValidateSnapshot(ReadOnlySpan<byte> data)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(data.ToArray()));
            if (reader.ReadByte() != SnapshotVersion)
            {
                throw new InvalidDataException("Unsupported bus-master IDE snapshot version.");
            }

            for (int i = 0; i < 2; i++)
            {
                byte command = reader.ReadByte();
                byte status = reader.ReadByte();
                uint descriptorTable = reader.ReadUInt32();
                reader.ReadBoolean();
                ReadAtaLatches(reader);
                if ((command & ~(Start | DirectionDiskToMemory)) != 0 ||
                    (status & ~(StatusActive | StatusWritable)) != 0 ||
                    (descriptorTable & 3) != 0)
                {
                    throw new InvalidDataException("Invalid bus-master IDE register snapshot.");
                }
                ValidateAtaState(reader);
            }

            if (reader.BaseStream.Position != reader.BaseStream.Length)
            {
                throw new InvalidDataException("Invalid bus-master IDE snapshot length.");
            }
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("Truncated bus-master IDE snapshot.", exception);
        }
    }

    /// <summary>Restores a validated engine payload without restarting a DMA command.</summary>
    public void Restore(ReadOnlySpan<byte> data)
    {
        ValidateSnapshot(data);
        using var reader = new BinaryReader(new MemoryStream(data.ToArray()));
        reader.ReadByte();
        _restoring = true;
        try
        {
            foreach (ChannelState channel in _channels)
            {
                channel.Command = reader.ReadByte();
                channel.Status = reader.ReadByte();
                channel.DescriptorTable = reader.ReadUInt32();
                channel.TableWasLongerThanTransfer = reader.ReadBoolean();
                channel.Ide.RestoreBusMasterAtaLatches(ReadAtaLatches(reader));
                channel.AwaitingAtaInterrupt = false;
                ReadAtaState(reader, channel.Ide);
            }
        }
        finally
        {
            _restoring = false;
        }
    }

    /// <summary>
    /// Verifies that pending ATA requests in a valid payload still name a
    /// mounted, suitable disk. Snapshot validation calls this before memory
    /// changes, so a removed disk cannot leave a later Start write dereference
    /// a null drive.
    /// </summary>
    public void ValidateSnapshotAgainstDrives(ReadOnlySpan<byte> data)
    {
        ValidateSnapshot(data);
        using var reader = new BinaryReader(new MemoryStream(data.ToArray()));
        reader.ReadByte();
        foreach (ChannelState channel in _channels)
        {
            reader.ReadByte();
            reader.ReadByte();
            reader.ReadUInt32();
            reader.ReadBoolean();
            ReadAtaLatches(reader);
            ValidateAtaStateAgainstDrive(reader, channel.Ide);
        }
    }

    /// <summary>Converts a decoded BAR port into its 16-byte register offset.</summary>
    private int Offset(ushort port)
    {
        if (_window.MappedBase is not { } address)
        {
            return -1;
        }

        int offset = port - address;
        return offset < WindowLength ? offset : -1;
    }

    /// <summary>Applies Start and direction writes while preserving an active transfer direction.</summary>
    private void WriteCommand(int channelIndex, byte value)
    {
        ChannelState channel = _channels[channelIndex];
        bool wasStarted = (channel.Command & Start) != 0;
        bool wantsStart = (value & Start) != 0;

        if (!Function.BusMasterEnabled && wantsStart)
        {
            return;
        }

        bool active = (channel.Status & StatusActive) != 0;
        bool directionChanged = (value & DirectionDiskToMemory) != (channel.Command & DirectionDiskToMemory);
        if (active && directionChanged)
        {
            // SFF-8038i forbids changing direction while active. Preserve it.
            value = (byte)((value & ~DirectionDiskToMemory) | (channel.Command & DirectionDiskToMemory));
        }

        channel.Command = (byte)(value & (Start | DirectionDiskToMemory));
        if (!wantsStart)
        {
            channel.Status &= unchecked((byte)~StatusActive);
            channel.AwaitingAtaInterrupt = false;
            channel.TableWasLongerThanTransfer = false;
            return;
        }

        if (!wasStarted)
        {
            channel.Status |= StatusActive;
        }

        TryStartTransfer(channelIndex);
    }

    /// <summary>Consumes an armed ATA command once Start and PCI bus mastering permit it.</summary>
    private void TryStartTransfer(int channelIndex)
    {
        if (_restoring || !Function.BusMasterEnabled)
        {
            return;
        }

        ChannelState channel = _channels[channelIndex];
        if ((channel.Command & Start) == 0 || channel.AwaitingAtaInterrupt || channel.TableWasLongerThanTransfer)
        {
            return;
        }

        IdeController.BusMasterTransferRequest? request = channel.Ide.TakePendingBusMasterTransfer();
        if (request is null)
        {
            return;
        }

        bool diskToMemory = (channel.Command & DirectionDiskToMemory) != 0;
        DescriptorResult descriptorResult = TryReadDescriptors(
            channel,
            request.Value.SectorCount * DiskImage.SectorSize,
            out List<Region> regions,
            out bool tableLonger);
        if (diskToMemory == request.Value.WriteToDisk || descriptorResult == DescriptorResult.Invalid)
        {
            FailTransfer(channel);
            return;
        }

        if (descriptorResult == DescriptorResult.Short)
        {
            // PIIX4 Table 15 calls this a quiet exhaustion: it has already
            // accepted the PRDs before EOT, so a READ DMA exposes that prefix
            // in RAM, but it neither raises ATA completion nor sets Error.
            if (diskToMemory && channel.Ide.HardDisk is { } disk)
            {
                int requestedByteCount = request.Value.SectorCount * DiskImage.SectorSize;
                var diskData = new byte[requestedByteCount];
                if (request.Value.Lba <= disk.TotalSectors - request.Value.SectorCount &&
                    disk.ReadLinear(request.Value.Lba, request.Value.SectorCount, diskData))
                {
                    WriteMemory(regions, diskData);
                }
            }
            else if (!diskToMemory)
            {
                int suppliedBytes = regions.Sum(region => region.Length);
                var suppliedData = new byte[suppliedBytes];
                ReadMemory(regions, suppliedData);
                channel.Ide.WriteShortBusMasterPrefix(request.Value, suppliedData);
            }

            // No asynchronous continuation is scheduled. The consumed request
            // is stopped quietly; the driver must stop/reset and issue a new
            // command to recover from an insufficient descriptor table.
            channel.Status &= unchecked((byte)~StatusActive);
            return;
        }

        int byteCount = request.Value.SectorCount * DiskImage.SectorSize;
        byte[] buffer = new byte[byteCount];
        if (!diskToMemory)
        {
            ReadMemory(regions, buffer);
        }

        channel.TableWasLongerThanTransfer = tableLonger;
        channel.AwaitingAtaInterrupt = true;
        // SFF-8038i requires the first BMISTA read after INTRQ to observe all
        // drive data in memory. Copy before finishing READ DMA, because that
        // finishing call raises the ATA interrupt synchronously in this model.
        if (diskToMemory)
        {
            byte[] diskData = new byte[byteCount];
            bool readCompleted = channel.Ide.FinishBusMasterRead(
                request.Value,
                diskData,
                () => WriteMemory(regions, diskData));

            if (!readCompleted)
            {
                channel.Status |= StatusError;
            }

            CompleteTransferWithoutInterrupt(channel);

            return;
        }

        bool completed = channel.Ide.FinishBusMasterWrite(request.Value, buffer);

        if (!completed)
        {
            channel.Status |= StatusError;
        }

        CompleteTransferWithoutInterrupt(channel);

    }

    /// <summary>Validates the used PRD prefix within one table page and bounded RAM regions.</summary>
    private DescriptorResult TryReadDescriptors(
        ChannelState channel,
        int requiredBytes,
        out List<Region> regions,
        out bool tableLonger)
    {
        regions = [];
        tableLonger = false;
        uint table = channel.DescriptorTable;
        if ((table & 3) != 0 || table >= _dmaMemory.RamSize ||
            (table & TableBoundary4KMask) > TableBoundary4KLength - DescriptorLength)
        {
            return DescriptorResult.Invalid;
        }

        int availableBytes = 0;
        for (int descriptorIndex = 0; descriptorIndex < MaximumDescriptors; descriptorIndex++)
        {
            uint descriptorAddress = table + (uint)(descriptorIndex * DescriptorLength);
            if (descriptorAddress < table || descriptorAddress > _dmaMemory.RamSize - DescriptorLength ||
                (descriptorAddress & TableBoundary4KMask) > TableBoundary4KLength - DescriptorLength ||
                (descriptorAddress & ~TableBoundary4KMask) != (table & ~TableBoundary4KMask))
            {
                return DescriptorResult.Invalid;
            }

            uint address = _dmaMemory.ReadDword(descriptorAddress);
            uint countAndEnd = _dmaMemory.ReadDword(descriptorAddress + 4);
            int count = (int)(countAndEnd & DescriptorByteCountMask);
            if (count == 0)
            {
                count = RegionBoundary64KLength;
            }

            if ((address & 1) != 0 || (count & 1) != 0 ||
                (address & RegionBoundary64KMask) > RegionBoundary64KLength - count ||
                address > _dmaMemory.RamSize || (uint)count > _dmaMemory.RamSize - address)
            {
                return DescriptorResult.Invalid;
            }

            if (availableBytes < requiredBytes)
            {
                int bytesUsed = Math.Min(count, requiredBytes - availableBytes);
                regions.Add(new Region(address, bytesUsed));
            }
            availableBytes += count;

            bool endOfTable = (countAndEnd & DescriptorEndOfTable) != 0;
            if (availableBytes < requiredBytes)
            {
                if (endOfTable)
                {
                    return DescriptorResult.Short;
                }

                continue;
            }

            // The disk command bounds DMA, not an arbitrary unused PRD tail.
            // Reaching its requested byte count ends this synchronous transfer;
            // an oversized final region or a missing EOT leaves Active set for
            // the driver's mandatory Stop write without fetching another PRD.
            tableLonger = availableBytes > requiredBytes || !endOfTable;
            return DescriptorResult.Complete;
        }

        return DescriptorResult.Invalid;
    }

    /// <summary>Gathers the validated PRD regions into the ATA buffer.</summary>
    private void ReadMemory(IEnumerable<Region> regions, Span<byte> destination)
    {
        int destinationOffset = 0;
        foreach (Region region in regions)
        {
            for (int i = 0; i < region.Length; i++)
            {
                destination[destinationOffset++] = _dmaMemory.ReadByte(region.Address + (uint)i);
            }
        }
    }

    /// <summary>Scatters drive bytes into validated PRD regions before ATA completion.</summary>
    private void WriteMemory(IEnumerable<Region> regions, ReadOnlySpan<byte> source)
    {
        int sourceOffset = 0;
        foreach (Region region in regions)
        {
            for (int i = 0; i < region.Length; i++)
            {
                _dmaMemory.WriteByte(region.Address + (uint)i, source[sourceOffset++]);
            }
        }
    }

    /// <summary>Latches a physical INTRQ rising edge independently of ATA acknowledgment.</summary>
    private void LatchAtaInterrupt(int channelIndex)
    {
        if (_restoring)
        {
            return;
        }

        ChannelState channel = _channels[channelIndex];
        channel.Status |= StatusInterrupt;
        if (!channel.AwaitingAtaInterrupt)
        {
            return;
        }

        channel.AwaitingAtaInterrupt = false;
        if (!channel.TableWasLongerThanTransfer)
        {
            channel.Status &= unchecked((byte)~StatusActive);
        }
    }

    /// <summary>
    /// Completes a synchronous transfer when nIEN suppressed the ATA INTRQ
    /// edge. The engine has still moved all bytes, but BMISTA must not claim
    /// an interrupt that the drive did not put on its cable.
    /// </summary>
    private static void CompleteTransferWithoutInterrupt(ChannelState channel)
    {
        if (!channel.AwaitingAtaInterrupt)
        {
            return;
        }

        channel.AwaitingAtaInterrupt = false;
        if (!channel.TableWasLongerThanTransfer)
        {
            channel.Status &= unchecked((byte)~StatusActive);
        }
    }

    /// <summary>Stops an invalid transfer with the PCI error latch set.</summary>
    private static void FailTransfer(ChannelState channel)
    {
        channel.Status |= StatusError;
        channel.Status &= unchecked((byte)~StatusActive);
        channel.AwaitingAtaInterrupt = false;
    }

    /// <summary>Stops both engines when PCI removes permission to master the bus.</summary>
    private void StopWhenBusMasterIsDisabled()
    {
        if (Function.BusMasterEnabled)
        {
            return;
        }

        foreach (ChannelState channel in _channels)
        {
            channel.Command &= unchecked((byte)~Start);
            channel.Status &= unchecked((byte)~StatusActive);
            channel.AwaitingAtaInterrupt = false;
        }
    }

    /// <summary>Writes ATA task-file completion latches before the pending request.</summary>
    private static void WriteAtaLatches(BinaryWriter writer, IdeController.BusMasterAtaLatches latches)
    {
        writer.Write(latches.AtapiStatus);
        writer.Write(latches.AtapiError);
        writer.Write(latches.DiskStatus);
        writer.Write(latches.DiskError);
        writer.Write(latches.InterruptPending);
        writer.Write(latches.DeviceControl);
    }

    /// <summary>Reads ATA task-file completion latches from a validated payload.</summary>
    private static IdeController.BusMasterAtaLatches ReadAtaLatches(BinaryReader reader)
    {
        return new IdeController.BusMasterAtaLatches(
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadBoolean(),
            reader.ReadByte());
    }

    /// <summary>Writes the ATA half of a pending DMA command and its negotiated mode.</summary>
    private static void WriteAtaState(BinaryWriter writer, IdeController ide)
    {
        IdeController.BusMasterTransferRequest? request = ide.PendingBusMasterTransfer;
        writer.Write(request.HasValue);
        if (request is { } transfer)
        {
            writer.Write(transfer.Lba);
            writer.Write(transfer.SectorCount);
            writer.Write(transfer.WriteToDisk);
        }

        (bool Ultra, int Level)? mode = ide.SelectedBusMasterTransferMode;
        writer.Write(mode.HasValue);
        if (mode is { } selected)
        {
            writer.Write(selected.Ultra);
            writer.Write((byte)selected.Level);
        }
    }

    /// <summary>Rejects an ATA state record whose request or mode is impossible.</summary>
    private static void ValidateAtaState(BinaryReader reader)
    {
        if (reader.ReadBoolean())
        {
            long lba = reader.ReadInt64();
            int sectors = reader.ReadInt32();
            reader.ReadBoolean();
            if (lba < 0 || lba > MaximumAta28Lba || sectors is < 1 or > 256)
            {
                throw new InvalidDataException("Invalid bus-master IDE ATA request.");
            }
        }

        if (reader.ReadBoolean())
        {
            reader.ReadBoolean();
            if (reader.ReadByte() > 2)
            {
                throw new InvalidDataException("Invalid bus-master IDE transfer mode.");
            }
        }
    }

    /// <summary>Validates a pending request against the disk currently fitted to its channel.</summary>
    private static void ValidateAtaStateAgainstDrive(BinaryReader reader, IdeController ide)
    {
        if (!reader.ReadBoolean())
        {
            SkipTransferMode(reader);
            return;
        }

        long lba = reader.ReadInt64();
        int sectors = reader.ReadInt32();
        bool writeToDisk = reader.ReadBoolean();
        DiskImage? disk = ide.HardDisk;
        if (disk is null || lba < 0 || lba > MaximumAta28Lba || sectors is < 1 or > 256 ||
            lba > disk.TotalSectors - sectors ||
            writeToDisk && disk.ReadOnly)
        {
            throw new InvalidDataException("Bus-master IDE snapshot request does not match its attached disk.");
        }

        SkipTransferMode(reader);
    }

    /// <summary>Consumes the optional selected DMA mode after a request record.</summary>
    private static void SkipTransferMode(BinaryReader reader)
    {
        if (reader.ReadBoolean())
        {
            reader.ReadBoolean();
            reader.ReadByte();
        }
    }

    /// <summary>Restores ATA DMA state without raising the armed event.</summary>
    private static void ReadAtaState(BinaryReader reader, IdeController ide)
    {
        if (!reader.ReadBoolean())
        {
            ide.RestorePendingBusMasterTransfer(null);
        }
        else
        {
            long lba = reader.ReadInt64();
            int sectors = reader.ReadInt32();
            bool writeToDisk = reader.ReadBoolean();
            ide.RestorePendingBusMasterTransfer(new IdeController.BusMasterTransferRequest(lba, sectors, writeToDisk));
        }

        if (!reader.ReadBoolean())
        {
            ide.RestoreSelectedBusMasterTransferMode(null);
            return;
        }

        bool ultra = reader.ReadBoolean();
        int level = reader.ReadByte();
        ide.RestoreSelectedBusMasterTransferMode((ultra, level));
    }

    /// <summary>Keeps one cable's engine registers and synchronous completion state.</summary>
    private sealed class ChannelState(IdeController ide)
    {
        public IdeController Ide { get; } = ide;
        public byte Command;
        public byte Status;
        public uint DescriptorTable;
        public bool AwaitingAtaInterrupt;
        public bool TableWasLongerThanTransfer;
    }

    /// <summary>A validated physical RAM span consumed by the PRD walker.</summary>
    private readonly record struct Region(uint Address, int Length);

    /// <summary>Separates completion, quiet EOT exhaustion and invalid PCI regions.</summary>
    private enum DescriptorResult
    {
        Complete,
        Short,
        Invalid,
    }
}
