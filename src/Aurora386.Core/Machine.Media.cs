using Aurora386.Core.Firmware;
using Aurora386.Core.Firmware.Fpu;
using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Diagnostics;
using Aurora386.Core.Devices.Input;
using Aurora386.Core.Devices.Interrupts;
using Aurora386.Core.Devices.Timing;
using Aurora386.Core.Devices.System;
using Aurora386.Core.Devices.Network;
using Aurora386.Core.Devices.Audio;
using Aurora386.Core.Devices.Video;
using Aurora386.Core.Dos;
using Aurora386.Core.IO;
using Aurora386.Core.Memory;
using CpuNs = Aurora386.Core.Cpu;

namespace Aurora386.Core;

/// <summary>
/// The complete emulated PC: CPU, memory, devices and BIOS wired together and
/// ready to execute. This is the class that decides what kind of machine we
/// are pretending to be.
/// </summary>
/// <summary>
/// The removable and fixed media: hard disk images on the IDE cable, the
/// CD-ROM, the redirector's drive letters, and the diskette parameter table.
/// </summary>
public sealed partial class Machine
{

    /// <summary>The MSCDEX interface, through which DOS reaches the CD-ROM.</summary>
    public CompactDiscServices CompactDiscServices { get; }

    /// <summary>
    /// Whether the guest has loaded MSCDEX itself.
    ///
    /// Set once the guest's own extensions answer for themselves, after which
    /// the emulator's stand-in keeps out of the way. There is no flag in
    /// memory to read for this: what betrays it is that something else has
    /// claimed the drive-letter question, so we watch for our own answer
    /// being contradicted.
    /// </summary>
    public bool GuestHasItsOwnCompactDiscExtensions { get; set; }

    /// <summary>
    /// The CD-ROM currently in the drive, if any.
    ///
    /// A compact disc is kept apart from the disk drives on purpose. Its
    /// sectors are 2048 bytes rather than 512 and it is addressed by block
    /// number rather than by cylinder/head/sector, so none of the INT 13h
    /// services apply: DOS reaches a CD through MSCDEX and a device driver.
    /// Pretending it were another <see cref="DiskImage"/> would leak
    /// immediately.
    /// </summary>
    public IsoImage? CompactDisc { get; private set; }

    /// <summary>
    /// Inserts a disc, replacing whatever was in the drive, and gives it a
    /// drive letter through the redirector.
    ///
    /// The two halves are both necessary and do different jobs. Attaching the
    /// image is what lets a program read raw sectors through MSCDEX's device
    /// request interface, which is how copy protection and CD audio worked.
    /// Mounting it on the redirector is what makes "DIR D:" list the disc --
    /// two entirely separate paths through DOS that happen to reach the same
    /// piece of plastic.
    /// </summary>
    public void AttachCompactDisc(IsoImage disc)
    {
        CompactDisc?.Dispose();
        CompactDisc = disc;
        Ide.NotifyMediaChanged();

        // The redirector's letter is only for a guest without a CD-ROM
        // driver of its own. Once OAKCDROM or any ATAPI driver has spoken to
        // the drive, the disc has the letter that driver and MSCDEX gave it,
        // and a second one would only confuse: the same disc twice, and the
        // driver's letter the one that goes empty.
        if (!GuestHasItsOwnCompactDiscExtensions)
            MountRedirectedDrive(CompactDiscServices.DriveLetter, new IsoFileSystem(disc));
    }

    /// <summary>Ejects the disc, leaving the drive empty.</summary>
    public void EjectCompactDisc()
    {
        UnmountRedirectedDrive(CompactDiscServices.DriveLetter);
        Ide.NotifyMediaChanged();

        CompactDisc?.Dispose();
        CompactDisc = null;
    }

    /// <summary>Attaches a disk image to a BIOS drive number.</summary>
    public void AttachDisk(DiskImage image)
    {
        // Replacing the disk in a drive means letting go of the one that was
        // there. Keeping it would hold its file open for as long as the
        // machine runs, and every swap during an installation would add
        // another -- eleven floppies, eleven open files, none of them
        // reachable.
        DiskImage? previous = DiskServices.GetDrive(image.DriveNumber);
        if (previous is not null)
        {
            _disks.Remove(previous);
            previous.Dispose();
        }

        _disks.Add(image);
        DiskServices.Attach(image);

        // The first hard disk is also the master on the IDE cable, where
        // Windows looks for it directly. Same image, two doors.
        if (image.DriveNumber == 0x80) Ide.AttachHardDisk(image);
        if (image.DriveNumber == 0x81) IdeSecondary.AttachHardDisk(image);

        // The parameter table describes whichever floppy is in drive A:, so
        // it has to follow the disk that is actually there.
        if (image.DriveNumber == 0x00)
            WriteDiskParameterTable(image.SectorsPerTrack);
    }

    /// <summary>
    /// Attaches a filesystem to a drive letter through the redirector, and
    /// installs the redirector at once if DOS is already up.
    ///
    /// The letter is a number with 0 meaning A:, the convention DOS uses
    /// everywhere below the surface. Returns false when DOS is running but
    /// has no room for that letter, which means LASTDRIVE in CONFIG.SYS is
    /// too low -- the only symptom otherwise is "invalid drive" with no
    /// explanation.
    /// </summary>
    public bool MountRedirectedDrive(byte drive, IRedirectedFileSystem fileSystem)
    {
        Redirector.Mount(drive, fileSystem);
        // Force the re-check: without it the installer sees that it already
        // has addresses and returns at once, so a set captured too early --
        // before DOS existed at all -- would never be replaced.
        _redirectorInstaller.TryInstall(Redirector, force: true);

        return !Redirector.IsInstalled || drive < Redirector.DriveCount;
    }

    /// <summary>Hands a drive letter back to DOS.</summary>
    public void UnmountRedirectedDrive(byte drive) => Redirector.Unmount(drive);

    /// <summary>
    /// Fills in the diskette parameter table that INT 1Eh points at.
    ///
    /// Most of the eleven bytes are timings for a drive we do not simulate --
    /// how long the head takes to settle, how long the motor needs to come up
    /// to speed. The one that matters is the sectors per track, which is how
    /// DOS knows where one track ends and the next begins. Give it 18 for a
    /// disk that has 36 and it reads half of every track, finds half a
    /// directory, and goes looking for the rest where a smaller disk would
    /// have kept it.
    /// </summary>
    private void WriteDiskParameterTable(int sectorsPerTrack)
    {
        uint address = ((uint)DiskParameterSegment << 4) + DiskParameterOffset;

        // The values a PC/AT BIOS publishes, with the gap lengths that suit
        // the density: a 2.88 MB disk packs its sectors more tightly.
        bool dense = sectorsPerTrack >= 36;

        Memory.WriteByte(address + 0, 0xAF);   // step rate and head unload time
        Memory.WriteByte(address + 1, 0x02);   // head load time; DMA in use
        Memory.WriteByte(address + 2, 0x25);   // ticks before the motor stops
        Memory.WriteByte(address + 3, 0x02);   // 512 bytes a sector
        Memory.WriteByte(address + 4, (byte)sectorsPerTrack);
        Memory.WriteByte(address + 5, dense ? (byte)0x1B : (byte)0x1B);   // gap length
        Memory.WriteByte(address + 6, 0xFF);   // data length, unused at 512 bytes
        Memory.WriteByte(address + 7, dense ? (byte)0x54 : (byte)0x6C);   // format gap
        Memory.WriteByte(address + 8, 0xF6);   // the byte a format writes
        Memory.WriteByte(address + 9, 0x0F);   // head settle time
        Memory.WriteByte(address + 10, 0x08);  // motor start time
    }

    /// <summary>
    /// Where the diskette parameter table lives.
    ///
    /// It has to be in RAM, not in the ROM: the values change with the disk
    /// that is in the drive, and DOS itself may write to it -- some versions
    /// point the vector at a copy of their own. It sits just above the BIOS
    /// data area, in the space the interrupt vectors leave free.
    /// </summary>
    private const ushort DiskParameterSegment = 0x0000;
    private const ushort DiskParameterOffset = 0x0522;
}
