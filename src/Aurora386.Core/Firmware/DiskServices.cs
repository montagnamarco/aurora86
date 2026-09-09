using Aurora386.Core.Cpu;
using Aurora386.Core.Devices.Storage;
using CpuNs = Aurora386.Core.Cpu;

namespace Aurora386.Core.Firmware;

/// <summary>
/// BIOS disk services (INT 13h). DOS goes through them for every read and
/// write: the whole file system is built on top of these calls.
///
/// The functions report their outcome in the carry flag -- set means failure,
/// with the error code in AH. Since the entry point returns with IRET, which
/// reloads the flags from the stack, the carry has to be modified in the
/// stacked flags rather than in the live CPU register.
/// </summary>
public sealed class DiskServices
{
    private readonly Dictionary<byte, DiskImage> _drives = new();
    private readonly BiosDataArea _bda;

    /// <summary>Error code of the last operation, returned by function 0x01.</summary>
    private byte _lastStatus;

    public DiskServices(BiosDataArea bda) => _bda = bda;

    public void Attach(DiskImage image) => _drives[image.DriveNumber] = image;

    /// <summary>Removes a drive entirely, as if the cable had been unplugged.</summary>
    public void Detach(byte driveNumber) => _drives.Remove(driveNumber);

    /// <summary>
    /// Every drive currently attached, ordered by BIOS drive number: floppies
    /// (0x00, 0x01) first, then hard disks (0x80 upwards). The user interface
    /// walks this to decide how many activity lights to show.
    /// </summary>
    public IEnumerable<DiskImage> AttachedDrives
        => _drives.OrderBy(pair => pair.Key).Select(pair => pair.Value);

    /// <summary>
    /// Raised on every sector transfer, with the BIOS drive number and
    /// whether it was a write. The user interface uses this to blink the
    /// activity lights the way a real drive's LED behaves -- which is also a
    /// genuinely useful diagnostic: a machine that has quietly wedged looks
    /// different from one that is busy loading.
    /// </summary>
    public event Action<byte, bool>? DriveAccessed;

    /// <summary>
    /// The same transfers, with the cylinder, head, sector and count that were
    /// asked for. Where <see cref="DriveAccessed"/> is enough to blink a light,
    /// this is enough to reconstruct what was read or written and in what
    /// order -- which is how a stalled guest is told apart from a slow one.
    /// </summary>
    public event Action<byte, bool, int, int, int, int>? SectorTransferred;

    private void NoteAccess(byte driveNumber, bool write)
        => DriveAccessed?.Invoke(driveNumber, write);

    private void NoteTransfer(byte driveNumber, bool write,
                              int cylinder, int head, int sector, int count)
    {
        DriveAccessed?.Invoke(driveNumber, write);
        SectorTransferred?.Invoke(driveNumber, write, cylinder, head, sector, count);
    }

    public DiskImage? GetDrive(byte driveNumber)
        => _drives.TryGetValue(driveNumber, out DiskImage? image) ? image : null;

    public int HardDiskCount => _drives.Keys.Count(d => d >= 0x80);

    public void Handle(CpuNs.Cpu cpu)
    {
        byte function = cpu.State.GetReg8(4);   // AH
        byte drive = cpu.State.GetReg8(2);      // DL

        switch (function)
        {
            case 0x00: Reset(cpu); return;
            case 0x01: GetStatus(cpu); return;
            // Anything that touches the medium has to be turned away while
            // the change line is up, not just reading. DOS verifies sectors
            // as well as reading them, and SMARTDRV uses verify to decide
            // whether what it has cached is still good -- so a verify that
            // succeeds tells it the old disk's sectors are still valid, and
            // it goes on serving them from memory. That is what let a file
            // "copy" successfully after a few tries: the reads never reached
            // the new disk at all.
            case 0x02 or 0x03 or 0x04 when MediaChangedFor(cpu, drive): return;

            case 0x02: ReadSectors(cpu, drive); return;
            case 0x03: WriteSectors(cpu, drive); return;
            case 0x04: VerifySectors(cpu, drive); return;
            case 0x08: GetDriveParameters(cpu, drive); return;
            case 0x15: GetDiskType(cpu, drive); return;
            case 0x16: DetectDiskChange(cpu, drive); return;

            // The extended functions of the Enhanced Disk Drive
            // specification, which address sectors by number rather than by
            // cylinder, head and sector. Everything from Windows 95 on uses
            // them when they exist; DOS 6.22 never asks. Hard disks only:
            // nothing ever addressed a floppy this way.
            case 0x41 when drive >= 0x80: CheckExtensions(cpu, drive); return;
            case 0x42 when drive >= 0x80: ExtendedRead(cpu, drive); return;
            case 0x43 when drive >= 0x80: ExtendedWrite(cpu, drive); return;
            case 0x44 when drive >= 0x80: ExtendedVerify(cpu, drive); return;
            case 0x47 when drive >= 0x80: ExtendedSeek(cpu, drive); return;
            case 0x48 when drive >= 0x80: ExtendedGetParameters(cpu, drive); return;

            default:
                Fail(cpu, 0x01);   // invalid function
                return;
        }
    }

    private void Reset(CpuNs.Cpu cpu)
    {
        // Resetting the controller is what lowers the change line.
        //
        // This is the step that recalibrates the drive -- it seeks back to
        // track zero, which is a real look at the disk that is now in there.
        // It is also exactly what software does when told the disk changed:
        // the error comes back, it resets the drive, and it reads again. So
        // the reset is both the natural place to clear the line and the one
        // that keeps the sequence moving.
        //
        // Clearing it on the refusal instead would let only the first asker
        // hear about the swap. DOS and SMARTDRV cache different things and
        // ask separately, so whichever got there first would learn of the new
        // disk while the other went on serving the old one -- a directory
        // from one floppy and sectors from another, which is how the
        // installer ended up asking for disk after disk and getting nowhere.
        byte drive = cpu.State.GetReg8(2);   // DL
        _mediaChanged.Remove(drive);

        _lastStatus = 0;
        Succeed(cpu);
    }

    private void GetStatus(CpuNs.Cpu cpu)
    {
        cpu.SetCarryInStackedFlags(false);
        cpu.State.SetReg8(4, _lastStatus);   // AH = the stored status, not an error code
    }

    /// <summary>
    /// The geometry the BIOS presents for a disk, which is not always the
    /// disk's own. The CHS interface has ten bits for the cylinder, so a
    /// disk with more than 1024 is shown with the cylinders halved and the
    /// heads doubled, as many times as it takes, up to 255 heads -- the
    /// "bit shift" translation every BIOS of the mid-nineties did, and the
    /// reason a 2 GB disk had 64 heads in the setup screen. Below the limit
    /// the geometry is the disk's own, so nothing changes for small disks.
    /// </summary>
    public static (int Cylinders, int Heads, int SectorsPerTrack) PresentedGeometry(DiskImage image)
    {
        if (image.DriveNumber < 0x80) return (image.Cylinders, image.Heads, image.SectorsPerTrack);
        HardDiskImage.Geometry presented =
            new HardDiskImage.Geometry(image.Cylinders, image.Heads, image.SectorsPerTrack).Presented();
        return (presented.Cylinders, presented.Heads, presented.SectorsPerTrack);
    }

    /// <summary>The sector number a CHS address names under the presented geometry, or -1 if it is off the disk.</summary>
    private static long LinearOf(DiskImage image, int cylinder, int head, int sector)
    {
        (int cylinders, int heads, int sectorsPerTrack) = PresentedGeometry(image);
        if (cylinder < 0 || cylinder >= cylinders || head < 0 || head >= heads
            || sector < 1 || sector > sectorsPerTrack) return -1;
        return ((long)cylinder * heads + head) * sectorsPerTrack + (sector - 1);
    }

    private void ReadSectors(CpuNs.Cpu cpu, byte drive)
    {
        DiskImage? image = GetDrive(drive);
        if (image is null) { Fail(cpu, 0x80); return; }   // drive not ready

        (int cylinder, int head, int sector, int count) = DecodeChs(cpu);
        NoteTransfer(drive, write: false, cylinder, head, sector, count);

        // The destination buffer is ES:BX in the caller's address space.
        var buffer = new byte[count * DiskImage.SectorSize];
        long linear = LinearOf(image, cylinder, head, sector);
        if (linear < 0 || !image.ReadLinear(linear, count, buffer))
        {
            Fail(cpu, 0x04);   // sector not found
            return;
        }

        ushort destination = cpu.State.Bx;
        for (int i = 0; i < buffer.Length; i++)
            cpu.WriteMemoryByte(SegReg.Es, (uint)(destination + i), buffer[i]);

        cpu.State.SetReg8(0, (byte)count);   // AL: sectors actually transferred
        _lastStatus = 0;
        Succeed(cpu);
    }

    private void WriteSectors(CpuNs.Cpu cpu, byte drive)
    {
        DiskImage? image = GetDrive(drive);
        if (image is null) { Fail(cpu, 0x80); return; }
        if (image.ReadOnly) { Fail(cpu, 0x03); return; }   // write protected

        (int cylinder, int head, int sector, int count) = DecodeChs(cpu);
        NoteTransfer(drive, write: true, cylinder, head, sector, count);

        var buffer = new byte[count * DiskImage.SectorSize];
        ushort source = cpu.State.Bx;
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = cpu.ReadMemoryByte(SegReg.Es, (uint)(source + i));

        long linear = LinearOf(image, cylinder, head, sector);
        if (linear < 0 || !image.WriteLinear(linear, count, buffer))
        {
            Fail(cpu, 0x04);
            return;
        }

        cpu.State.SetReg8(0, (byte)count);
        _lastStatus = 0;
        Succeed(cpu);
    }

    private void VerifySectors(CpuNs.Cpu cpu, byte drive)
    {
        // On real hardware verify re-reads the sectors and checks their CRC.
        // In a file-backed image there is nothing that can rot, so it is enough
        // to confirm that the requested sectors exist.
        DiskImage? image = GetDrive(drive);
        if (image is null) { Fail(cpu, 0x80); return; }

        (int cylinder, int head, int sector, int count) = DecodeChs(cpu);
        if (LinearOf(image, cylinder, head, sector) < 0) { Fail(cpu, 0x04); return; }

        cpu.State.SetReg8(0, (byte)count);
        Succeed(cpu);
    }

    private void GetDriveParameters(CpuNs.Cpu cpu, byte drive)
    {
        DiskImage? image = GetDrive(drive);
        if (image is null)
        {
            // No such drive. Reporting the error is not enough: the registers
            // must also be cleared, because DOS reads DL regardless to learn
            // how many drives exist. Leaving a value behind from an earlier
            // call would send it off enumerating drives that are not there.
            cpu.State.Cx = 0;
            cpu.State.Dx = 0;
            cpu.State.Bx = 0;
            cpu.SetSegment(CpuNs.SegReg.Es, 0);
            cpu.State.Di = 0;
            Fail(cpu, 0x07);
            return;
        }

        // The geometry comes back in a packed encoding: the two high bits of
        // the cylinder count are folded into the top bits of CL.
        (int cylinders, int heads, int sectorsPerTrack) = PresentedGeometry(image);
        int lastCylinder = cylinders - 1;
        byte cl = (byte)((sectorsPerTrack & 0x3F) | ((lastCylinder >> 2) & 0xC0));
        cpu.State.SetReg8(1, cl);                              // CL
        cpu.State.SetReg8(5, (byte)(lastCylinder & 0xFF));     // CH
        // Watch the 8-bit register indices here. The order is
        // 0=AL 1=CL 2=DL 3=BL 4=AH 5=CH 6=DH 7=BH, so DH is index 6 and DL is
        // index 2 -- the last head number goes in the former, the drive count
        // in the latter. Swapping the two makes software believe the disk has a
        // single head, and the MS-DOS setup then writes its files to the wrong
        // sectors, leaving a disk that will not boot.
        cpu.State.SetReg8(6, (byte)(heads - 1));               // DH: last head number

        // DL reports how many drives of the same kind are installed: floppies
        // and hard disks are counted separately.
        bool wantHardDisks = drive >= 0x80;
        int sameKind = _drives.Keys.Count(d => (d >= 0x80) == wantHardDisks);
        cpu.State.SetReg8(2, (byte)sameKind);                  // DL

        // BL names the kind of drive, and only floppies have one. Software
        // reads it to find out what the drive is capable of, as opposed to
        // what happens to be in it at the moment: a 2.88 MB drive will take a
        // 1.44 MB disk, but a 1.44 MB drive will not take a 2.88 MB one.
        //
        // Leaving it unset handed back whatever the caller had in BL, and a
        // program that believed the drive too small for the disk in it would
        // stop partway through -- which is what installing Windows from 2.88
        // MB images did, always on the same file.
        if (!wantHardDisks)
        {
            byte driveType = image.SectorsPerTrack switch
            {
                9 when image.Cylinders == 40 => 0x01,   // 360 KB
                15 => 0x02,                             // 1.2 MB
                9 => 0x03,                              // 720 KB
                18 => 0x04,                             // 1.44 MB
                36 => 0x06,                             // 2.88 MB
                _ => 0x04,                              // anything else: treat as 1.44 MB
            };

            cpu.State.SetReg8(3, driveType);                   // BL
        }

        Succeed(cpu);
    }

    private void GetDiskType(CpuNs.Cpu cpu, byte drive)
    {
        DiskImage? image = GetDrive(drive);
        if (image is null)
        {
            // AH=0 means "drive not installed". Carry stays clear: the call
            // itself answered correctly, it is the drive that is missing.
            cpu.State.Cx = 0;
            cpu.State.Dx = 0;
            cpu.SetCarryInStackedFlags(false);
            cpu.State.SetReg8(4, 0x00);   // AH = drive type: not installed
            return;
        }

        // For this function AH is not an error code but the drive type, so it
        // has to be written after clearing the carry.
        cpu.SetCarryInStackedFlags(false);

        if (image.IsHardDisk)
        {
            long sectors = image.TotalSectors;
            cpu.State.Cx = (ushort)(sectors >> 16);
            cpu.State.Dx = (ushort)sectors;
            cpu.State.SetReg8(4, 0x03);   // fixed disk
        }
        else
        {
            // 0x02 means "floppy drive with a change line"; 0x01 would mean a
            // drive without one. The distinction matters: with 0x01 DOS cannot
            // ask the hardware whether the media was swapped, so it falls back
            // to guessing from how much time passed between accesses, and
            // swapping the floppy image under a running guest stops working.
            // Reporting the change line is the clean path -- DOS then queries
            // function 0x16 whenever it wants to know if the disk changed.
            cpu.State.SetReg8(4, 0x02);
        }
    }

    /// <summary>
    /// Function 0x16: reports whether the disk has been changed since the last
    /// access. It has to be offered together with 0x15 declaring the change
    /// line, otherwise DOS waits for an answer that never comes.
    ///
    /// File-backed images do not physically leave the drive, so the answer is
    /// always "no change" -- except the first time after the image has been
    /// swapped while the machine was running.
    /// </summary>
    private void DetectDiskChange(CpuNs.Cpu cpu, byte drive)
    {
        DiskImage? image = GetDrive(drive);
        if (image is null) { Fail(cpu, 0x80); return; }

        // The change line stays raised until the drive actually reads
        // something, which is what clears it on real hardware -- it is a
        // physical latch tripped by the door, not a message that is consumed
        // by being read.
        //
        // Reporting the change only once looks reasonable and is wrong in a
        // way that matters: software asks more than once. Windows 3.1's
        // installer polls the drive several times before it reads, so a
        // change reported to the first poll alone left it convinced the old
        // floppy was still in, and it went on asking for a disk that was
        // already there.
        if (_mediaChanged.Contains(drive))
        {
            Fail(cpu, 0x06);   // media changed
            return;
        }

        cpu.State.SetReg8(4, 0x00);
        cpu.SetCarryInStackedFlags(false);
    }

    /// <summary>Drives whose media has been swapped and not yet reported.</summary>
    private readonly HashSet<byte> _mediaChanged = new();

    /// <summary>Signals that the image behind a drive has been replaced.</summary>
    public void NotifyMediaChanged(byte driveNumber) => _mediaChanged.Add(driveNumber);

    /// <summary>
    /// Refuses an access to a drive whose disk has been swapped and not yet
    /// looked at, reporting the change to the caller.
    ///
    /// The refusal happens once: having been told, DOS re-reads the disk's
    /// layout and everything after that goes through. It is the telling that
    /// matters -- the error is what makes DOS discard the file allocation
    /// table and directory it cached from the previous disk, and what makes
    /// SMARTDRV drop the sectors it is holding.
    /// </summary>
    private bool MediaChangedFor(CpuNs.Cpu cpu, byte drive)
    {
        if (GetDrive(drive) is null) return false;
        if (!_mediaChanged.Contains(drive)) return false;

        Fail(cpu, 0x06);   // media changed
        return true;
    }

    /// <summary>
    /// Lowers the change line, which only a completed read does.
    ///
    /// It is deliberately not lowered by the refusal itself. More than one
    /// piece of software has to hear about the swap: DOS keeps a cached file
    /// allocation table and directory, SMARTDRV keeps the sectors themselves,
    /// and they ask independently. Clearing the line on the first refusal
    /// tells whichever of them happened to ask first and leaves the other
    /// still holding the previous disk -- so the installer would read a
    /// directory belonging to one floppy while the sectors came from another,
    /// and go round asking for disk after disk without ever getting past.
    ///
    /// A completed read means the drive really has looked at the new disk,
    /// and that is the point at which there is nothing left to report.
    /// </summary>
    private void ClearMediaChanged(byte drive) => _mediaChanged.Remove(drive);

    /// <summary>
    /// Extracts cylinder, head, sector and count from the registers, unpacking
    /// the compact encoding: the two high bits of the cylinder live in bits 6-7
    /// of CL.
    /// </summary>
    /// <summary>
    /// Publishes the outcome in the BIOS Data Area. Function 0x01 hands it
    /// back, but some programs read those bytes directly instead of calling it,
    /// so they have to be kept in step with every operation.
    /// </summary>
    private void PublishStatus(byte status)
    {
        _bda.DiskStatus = status;
        _bda.HardDiskStatus = status;
    }

    // --- the extended functions -------------------------------------------------

    private static void CheckExtensions(CpuNs.Cpu cpu, byte drive)
    {
        if (cpu.State.Bx != 0x55AA) { FailPlain(cpu, 0x01); return; }
        cpu.State.Bx = 0xAA55;
        cpu.State.SetReg8(4, 0x01);        // AH: version 1.x of the specification
        cpu.State.Cx = 0x0001;             // bit 0: the fixed disk access functions 42h-44h, 47h, 48h
        cpu.SetCarryInStackedFlags(false);
    }

    /// <summary>
    /// The disk address packet at DS:SI: its size, a reserved byte, the
    /// number of sectors, the buffer as offset then segment, and the sector
    /// number as a 64-bit quantity of which anything past 32 bits is beyond
    /// the disks here.
    /// </summary>
    private readonly record struct AddressPacket(int Count, ushort BufferOffset, ushort BufferSegment, long Sector);

    private static bool ReadPacket(CpuNs.Cpu cpu, out AddressPacket packet)
    {
        uint at = cpu.State.Si;
        byte size = cpu.ReadMemoryByte(SegReg.Ds, at);
        packet = default;
        if (size < 0x10) return false;

        int count = cpu.ReadMemoryWord(SegReg.Ds, at + 2);
        ushort offset = cpu.ReadMemoryWord(SegReg.Ds, at + 4);
        ushort segment = cpu.ReadMemoryWord(SegReg.Ds, at + 6);
        long sector = cpu.ReadMemoryDword(SegReg.Ds, at + 8) | ((long)cpu.ReadMemoryDword(SegReg.Ds, at + 12) << 32);
        packet = new AddressPacket(count, offset, segment, sector);
        return true;
    }

    private static void WritePacketCount(CpuNs.Cpu cpu, int transferred) =>
        cpu.WriteMemoryWord(SegReg.Ds, cpu.State.Si + 2u, (ushort)transferred);

    private void ExtendedRead(CpuNs.Cpu cpu, byte drive)
    {
        DiskImage? image = GetDrive(drive);
        if (image is null) { Fail(cpu, 0x80); return; }
        if (!ReadPacket(cpu, out AddressPacket packet) || packet.Count > 127) { Fail(cpu, 0x01); return; }

        var buffer = new byte[packet.Count * DiskImage.SectorSize];
        if (!image.ReadLinear(packet.Sector, packet.Count, buffer))
        {
            WritePacketCount(cpu, 0);
            Fail(cpu, 0x04);
            return;
        }

        uint destination = cpu.FarPointerToLinear(packet.BufferSegment, packet.BufferOffset);
        for (int i = 0; i < buffer.Length; i++) cpu.WriteLinearByte(destination + (uint)i, buffer[i]);

        DriveAccessed?.Invoke(drive, false);
        WritePacketCount(cpu, packet.Count);
        _lastStatus = 0;
        Succeed(cpu);
    }

    private void ExtendedWrite(CpuNs.Cpu cpu, byte drive)
    {
        DiskImage? image = GetDrive(drive);
        if (image is null) { Fail(cpu, 0x80); return; }
        if (image.ReadOnly) { Fail(cpu, 0x03); return; }
        if (!ReadPacket(cpu, out AddressPacket packet) || packet.Count > 127) { Fail(cpu, 0x01); return; }

        var buffer = new byte[packet.Count * DiskImage.SectorSize];
        uint source = cpu.FarPointerToLinear(packet.BufferSegment, packet.BufferOffset);
        for (int i = 0; i < buffer.Length; i++) buffer[i] = cpu.ReadLinearByte(source + (uint)i);

        if (!image.WriteLinear(packet.Sector, packet.Count, buffer))
        {
            WritePacketCount(cpu, 0);
            Fail(cpu, 0x04);
            return;
        }

        DriveAccessed?.Invoke(drive, true);
        WritePacketCount(cpu, packet.Count);
        _lastStatus = 0;
        Succeed(cpu);
    }

    private void ExtendedVerify(CpuNs.Cpu cpu, byte drive)
    {
        DiskImage? image = GetDrive(drive);
        if (image is null) { Fail(cpu, 0x80); return; }
        if (!ReadPacket(cpu, out AddressPacket packet)) { Fail(cpu, 0x01); return; }
        if (packet.Sector < 0 || packet.Sector + packet.Count > image.TotalSectors)
        {
            WritePacketCount(cpu, 0);
            Fail(cpu, 0x04);
            return;
        }
        Succeed(cpu);
    }

    private void ExtendedSeek(CpuNs.Cpu cpu, byte drive)
    {
        // There is no head to move; the sector is either on the disk or not.
        DiskImage? image = GetDrive(drive);
        if (image is null) { Fail(cpu, 0x80); return; }
        if (!ReadPacket(cpu, out AddressPacket packet)) { Fail(cpu, 0x01); return; }
        if (packet.Sector < 0 || packet.Sector >= image.TotalSectors) { Fail(cpu, 0x04); return; }
        Succeed(cpu);
    }

    /// <summary>
    /// The drive parameters in the extended form: the buffer at DS:SI says
    /// how big it is, and gets the size actually written back, then flags,
    /// the physical geometry, the total number of sectors and the bytes per
    /// sector. Version 1.1 callers leave room for a pointer to the
    /// controller's configuration table, and get "none".
    /// </summary>
    private void ExtendedGetParameters(CpuNs.Cpu cpu, byte drive)
    {
        DiskImage? image = GetDrive(drive);
        if (image is null) { Fail(cpu, 0x80); return; }

        uint at = cpu.State.Si;
        ushort size = cpu.ReadMemoryWord(SegReg.Ds, at);
        if (size < 0x1A) { Fail(cpu, 0x01); return; }

        bool extended = size >= 0x1E;
        cpu.WriteMemoryWord(SegReg.Ds, at + 0, (ushort)(extended ? 0x1E : 0x1A));
        cpu.WriteMemoryWord(SegReg.Ds, at + 2, 0x0002);              // the geometry below is valid
        cpu.WriteMemoryDword(SegReg.Ds, at + 4, (uint)image.Cylinders);
        cpu.WriteMemoryDword(SegReg.Ds, at + 8, (uint)image.Heads);
        cpu.WriteMemoryDword(SegReg.Ds, at + 12, (uint)image.SectorsPerTrack);
        cpu.WriteMemoryDword(SegReg.Ds, at + 16, (uint)image.TotalSectors);
        cpu.WriteMemoryDword(SegReg.Ds, at + 20, 0);
        cpu.WriteMemoryWord(SegReg.Ds, at + 24, DiskImage.SectorSize);
        if (extended) cpu.WriteMemoryDword(SegReg.Ds, at + 26, 0xFFFFFFFF);   // no configuration table
        Succeed(cpu);
    }

    /// <summary>A refusal that does not disturb the stored status, for calls that were never about a transfer.</summary>
    private static void FailPlain(CpuNs.Cpu cpu, byte errorCode)
    {
        cpu.State.SetReg8(4, errorCode);
        cpu.SetCarryInStackedFlags(true);
    }

    private static (int Cylinder, int Head, int Sector, int Count) DecodeChs(CpuNs.Cpu cpu)
    {
        byte cl = cpu.State.GetReg8(1);
        byte ch = cpu.State.GetReg8(5);

        int sector = cl & 0x3F;
        int cylinder = ch | ((cl & 0xC0) << 2);
        int head = cpu.State.GetReg8(6);      // DH
        int count = cpu.State.GetReg8(0);     // AL

        return (cylinder, head, sector, count);
    }

    private void Succeed(CpuNs.Cpu cpu)
    {
        PublishStatus(0);
        // AH = 0 means "no error". It has to be written explicitly: on entry AH
        // held the function number, and leaving it there would be read back as
        // an error code -- 0x02 is "address mark not found", which makes DOS
        // treat a file it just read correctly as corrupt.
        cpu.State.SetReg8(4, 0);
        cpu.SetCarryInStackedFlags(false);
    }

    private void Fail(CpuNs.Cpu cpu, byte errorCode)
    {
        _lastStatus = errorCode;
        PublishStatus(errorCode);
        cpu.State.SetReg8(4, errorCode);   // AH
        cpu.SetCarryInStackedFlags(true);
    }
}
