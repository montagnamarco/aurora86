using Aurora86.Core.Devices.Storage;
using CpuNs = Aurora86.Core.Cpu;

namespace Aurora86.Core.Firmware;

/// <summary>
/// The MSCDEX interface: INT 2Fh function 15h.
///
/// A CD-ROM is not a disk as far as DOS is concerned. DOS understands drives
/// with a file allocation table and 512-byte sectors, and a CD has neither --
/// its sectors are 2048 bytes and its directories are laid out to the ISO 9660
/// standard. So Microsoft did not teach DOS about CDs at all. Instead MSCDEX
/// installed itself as a network redirector, the same machinery that made a
/// file server appear as a drive letter, and answered the file system's
/// questions itself.
///
/// A program asks whether MSCDEX is there through this interrupt, learns which
/// drive letters are CDs, and can then read raw sectors. That is enough for
/// the installers and games that check for their own disc, and it is the
/// interface every one of them was written against.
///
/// The part that makes files on the disc openable with the ordinary DOS calls
/// is not here but in <see cref="Dos.RedirectorServices"/>: MSCDEX was two
/// things at once, an announcement interface for programs that wanted to know
/// about the drive, and a redirector answering INT 2Fh function 11h for DOS
/// itself. This class is the first; the second is a general mechanism that
/// serves a shared host folder just as well.
/// </summary>
public sealed class CompactDiscServices(Machine machine)
{
    private readonly Machine _machine = machine;

    /// <summary>
    /// The drive letter the disc appears as, counting from zero for A:.
    ///
    /// D: by convention, because C: is the hard disk and MSCDEX takes the
    /// first letter after the ones DOS has already given out. That is a
    /// convention and not a rule, which matters as soon as there is a second
    /// hard disk: DOS gives it D:, MSCDEX would then have to take E:, and a
    /// CD-ROM that insisted on D: would be answering for a drive that is
    /// already a disk.
    ///
    /// The default is therefore computed rather than fixed. Setting it
    /// explicitly still wins, because somebody who has arranged their drives
    /// a particular way knows better than this arithmetic does.
    /// </summary>
    public byte DriveLetter
    {
        get => _driveLetter ?? DefaultDriveLetter();
        set => _driveLetter = value;
    }

    private byte? _driveLetter;

    /// <summary>
    /// The first letter after the hard disks: C: plus one for each disk past
    /// the first.
    ///
    /// This counts disks and not partitions, and that is a real limitation
    /// rather than an oversight -- a disk carrying two partitions takes two
    /// letters and pushes the disc along one more. Reading the partition
    /// tables here would be the honest answer; counting disks is right for
    /// every arrangement this emulator can currently produce, since the
    /// images it makes carry one partition each.
    /// </summary>
    private byte DefaultDriveLetter()
    {
        int disks = _machine.DiskServices.HardDiskCount;
        return (byte)(2 + Math.Max(disks, 1));   // 2 = C:, so one disk gives D:
    }

    /// <summary>True when a disc is in the drive.</summary>
    private bool HasDisc => _machine.CompactDisc is not null;

    /// <summary>
    /// Handles a function 15h call. Returns false when the function is not one
    /// we answer, so the caller can fall through to "nobody is listening".
    /// </summary>
    public bool Handle(CpuNs.Cpu cpu)
    {
        byte function = cpu.State.GetReg8(0);   // AL

        switch (function)
        {
            case 0x00: return ReportInstalled(cpu);
            case 0x0B: return CheckDrive(cpu);
            case 0x0C: return ReportVersion(cpu);
            case 0x0D: return ReportDriveLetters(cpu);
            case 0x10: return SendDeviceRequest(cpu);
            default: return false;
        }
    }

    /// <summary>
    /// Function 00: how many CD drives there are and where their letters
    /// start. BX gets the count, CX the first letter.
    /// </summary>
    private bool ReportInstalled(CpuNs.Cpu cpu)
    {
        cpu.State.Bx = (ushort)(HasDisc ? 1 : 0);
        cpu.State.Cx = DriveLetter;
        return true;
    }

    /// <summary>
    /// Function 0B: is this particular drive letter a CD? BX comes back with
    /// 0xADAD, which is MSCDEX's way of saying it understood the question --
    /// a program that gets anything else knows MSCDEX is not there at all and
    /// must not trust AX either.
    /// </summary>
    private bool CheckDrive(CpuNs.Cpu cpu)
    {
        byte drive = cpu.State.GetReg8(2);   // CL
        cpu.State.Ax = (ushort)(HasDisc && drive == DriveLetter ? 1 : 0);
        cpu.State.Bx = 0xADAD;
        return true;
    }

    /// <summary>
    /// Function 0C: the MSCDEX version, major in BH and minor in BL. We report
    /// 2.25, the last version and the one everything was tested against.
    /// </summary>
    private bool ReportVersion(CpuNs.Cpu cpu)
    {
        cpu.State.Bx = 0x0219;   // 2.25
        return true;
    }

    /// <summary>
    /// Function 0D: writes the list of CD drive letters into the buffer at
    /// ES:BX, one byte each.
    /// </summary>
    private bool ReportDriveLetters(CpuNs.Cpu cpu)
    {
        if (HasDisc)
            cpu.WriteMemoryByte(CpuNs.SegReg.Es, cpu.State.Bx, DriveLetter);

        return true;
    }

    /// <summary>
    /// Function 10: passes a request straight to the device driver.
    ///
    /// The request is a control block at ES:BX in the format DOS uses for
    /// every device driver: a header saying how long it is and what is being
    /// asked, then the arguments. This is the path a program takes to read raw
    /// sectors off the disc, which is how copy protection checked that the
    /// original was in the drive and how CD audio was played.
    /// </summary>
    private bool SendDeviceRequest(CpuNs.Cpu cpu)
    {
        uint request = cpu.State.Bx;

        byte command = cpu.ReadMemoryByte(CpuNs.SegReg.Es, request + 2);

        // The status word, at offset 3. Bit 8 says the request is finished;
        // bit 15 says it failed and the low byte then carries the reason.
        const ushort Done = 0x0100;
        const ushort Error = 0x8000;
        const ushort UnknownCommand = 0x0003;
        const ushort NotReady = 0x0002;

        if (!HasDisc)
        {
            WriteStatus(cpu, request, (ushort)(Done | Error | NotReady));
            return true;
        }

        switch (command)
        {
            case 0x80:   // read long: the one command that matters
                ReadSectors(cpu, request);
                return true;

            case 0x00:   // init
            case 0x0D:   // open
            case 0x0E:   // close
                WriteStatus(cpu, request, Done);
                return true;

            default:
                WriteStatus(cpu, request, (ushort)(Done | Error | UnknownCommand));
                return true;
        }
    }

    /// <summary>
    /// Reads sectors off the disc into the caller's buffer.
    ///
    /// The control block gives the address to read into, how many sectors and
    /// where to start. Sectors are 2048 bytes here, not the 512 a disk uses:
    /// the whole reason MSCDEX exists is that DOS cannot cope with that.
    /// </summary>
    private void ReadSectors(CpuNs.Cpu cpu, uint request)
    {
        const ushort Done = 0x0100;
        const ushort Error = 0x8000;
        const ushort SectorNotFound = 0x0008;

        // Offsets within the read-long control block.
        uint bufferOffset = ReadWord(cpu, request + 14);
        uint bufferSegment = ReadWord(cpu, request + 16);
        int count = (int)ReadWord(cpu, request + 18);
        long startSector = ReadDword(cpu, request + 20);

        IsoImage disc = _machine.CompactDisc!;

        if (startSector < 0 || startSector + count > disc.TotalSectors)
        {
            WriteStatus(cpu, request, (ushort)(Done | Error | SectorNotFound));
            return;
        }

        var buffer = new byte[IsoImage.SectorSize];
        uint destination = (bufferSegment << 4) + bufferOffset;

        for (int i = 0; i < count; i++)
        {
            disc.ReadSector(startSector + i, buffer);

            for (int b = 0; b < buffer.Length; b++)
                _machine.Memory.WriteByte(destination + (uint)(i * IsoImage.SectorSize + b),
                                          buffer[b]);
        }

        WriteStatus(cpu, request, Done);
    }

    private static void WriteStatus(CpuNs.Cpu cpu, uint request, ushort status)
    {
        cpu.WriteMemoryByte(CpuNs.SegReg.Es, request + 3, (byte)status);
        cpu.WriteMemoryByte(CpuNs.SegReg.Es, request + 4, (byte)(status >> 8));
    }

    private static uint ReadWord(CpuNs.Cpu cpu, uint offset)
        => (uint)(cpu.ReadMemoryByte(CpuNs.SegReg.Es, offset)
                | (cpu.ReadMemoryByte(CpuNs.SegReg.Es, offset + 1) << 8));

    private static uint ReadDword(CpuNs.Cpu cpu, uint offset)
        => ReadWord(cpu, offset) | (ReadWord(cpu, offset + 2) << 16);
}
