using Aurora386.Core.Cpu;
using CpuNs = Aurora386.Core.Cpu;

namespace Aurora386.Core.Bios;

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

    private void NoteAccess(byte driveNumber, bool write)
        => DriveAccessed?.Invoke(driveNumber, write);

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

            case 0x41:
                // Installation check for the extended (LBA) disk functions.
                // We do not implement them: DOS 6.22 only ever uses classic CHS.
                Fail(cpu, 0x01);
                return;

            default:
                Fail(cpu, 0x01);   // invalid function
                return;
        }
    }

    private void Reset(CpuNs.Cpu cpu)
    {
        // There is no controller hardware to reset, so nothing to do but say
        // it went well.
        //
        // The change line is deliberately left alone. Resetting the
        // controller is what DOS does after being told the disk changed, and
        // it is not the same as having looked at the new disk: clearing the
        // line here would let the read that follows through with the old
        // disk's layout still cached, which is the very thing the refusal
        // exists to prevent.
        _lastStatus = 0;
        Succeed(cpu);
    }

    private void GetStatus(CpuNs.Cpu cpu)
    {
        cpu.SetCarryInStackedFlags(false);
        cpu.State.SetReg8(4, _lastStatus);   // AH = the stored status, not an error code
    }

    private void ReadSectors(CpuNs.Cpu cpu, byte drive)
    {
        DiskImage? image = GetDrive(drive);
        if (image is null) { Fail(cpu, 0x80); return; }   // drive not ready

        (int cylinder, int head, int sector, int count) = DecodeChs(cpu);
        NoteAccess(drive, write: false);

        // The destination buffer is ES:BX in the caller's address space.
        var buffer = new byte[count * DiskImage.SectorSize];
        if (!image.ReadSectors(cylinder, head, sector, count, buffer))
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
        NoteAccess(drive, write: true);

        var buffer = new byte[count * DiskImage.SectorSize];
        ushort source = cpu.State.Bx;
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = cpu.ReadMemoryByte(SegReg.Es, (uint)(source + i));

        if (!image.WriteSectors(cylinder, head, sector, count, buffer))
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
        if (!image.IsValidChs(cylinder, head, sector)) { Fail(cpu, 0x04); return; }

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
        int lastCylinder = image.Cylinders - 1;
        byte cl = (byte)((image.SectorsPerTrack & 0x3F) | ((lastCylinder >> 2) & 0xC0));
        cpu.State.SetReg8(1, cl);                              // CL
        cpu.State.SetReg8(5, (byte)(lastCylinder & 0xFF));     // CH
        // Watch the 8-bit register indices here. The order is
        // 0=AL 1=CL 2=DL 3=BL 4=AH 5=CH 6=DH 7=BH, so DH is index 6 and DL is
        // index 2 -- the last head number goes in the former, the drive count
        // in the latter. Swapping the two makes software believe the disk has a
        // single head, and the MS-DOS setup then writes its files to the wrong
        // sectors, leaving a disk that will not boot.
        cpu.State.SetReg8(6, (byte)(image.Heads - 1));         // DH: last head number

        // DL reports how many drives of the same kind are installed: floppies
        // and hard disks are counted separately.
        bool wantHardDisks = drive >= 0x80;
        int sameKind = _drives.Keys.Count(d => (d >= 0x80) == wantHardDisks);
        cpu.State.SetReg8(2, (byte)sameKind);                  // DL

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
        if (!_mediaChanged.Remove(drive)) return false;

        Fail(cpu, 0x06);   // media changed
        return true;
    }

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
