using Aurora386.Core.Devices.Storage;

using CpuNs = Aurora386.Core.Cpu;

namespace Aurora386.Core.Firmware;

/// <summary>
/// INT 13h for a CD booted in El Torito's no-emulation mode.
///
/// WHY THIS IS SEPARATE FROM THE ORDINARY DISK SERVICES
/// -----------------------------------------------------
/// A no-emulation boot does not present the disc as a floppy or a hard disk.
/// The firmware loads the boot image, jumps to it, and the loaded program is
/// then expected to read the rest of the disc itself -- which it does through
/// INT 13h on the drive number it was handed in DL, conventionally 0xE0.
///
/// Two things differ from every other drive this BIOS serves, and they are
/// the reason for a file of its own rather than another branch in the main
/// dispatch:
///
///   1. THE BLOCK SIZE IS 2048 BYTES, not 512. An address packet asking for
///      four blocks from a CD wants 8192 bytes, where the same packet aimed
///      at a hard disk would want 2048. Getting this wrong does not fail
///      loudly: it returns a quarter of the data the loader expected, and
///      the loader reports a corrupt image, which is a long way from the
///      cause.
///
///   2. ONLY THE EXTENDED FUNCTIONS APPLY. There is no cylinder, head and
///      sector geometry for a disc, so functions 0x02 and 0x08 have no
///      sensible answer. Every loader that boots this way -- GRUB, Limine,
///      isolinux -- checks for the extensions with function 0x41 and then
///      uses 0x42, which is precisely why the mode is usable at all.
///
/// Historical note on the drive number. El Torito reserves 0xE0 to 0xEF for
/// CD drives, chosen to sit above the hard disks at 0x80 and clear of the
/// floppies at 0x00. The specification does not oblige the firmware to use
/// 0xE0, but every implementation does and every loader assumes it.
/// </summary>
public sealed partial class DiskServices
{
    /// <summary>
    /// The size of a CD block, which is <see cref="IsoImage.SectorSize"/>
    /// and not <see cref="DiskImage.SectorSize"/>. Aliased here under a name
    /// that says which of the two it is, because confusing them is exactly
    /// the defect this file exists to prevent.
    /// </summary>
    public const int CompactDiscBlockSize = IsoImage.SectorSize;

    /// <summary>
    /// The disc a no-emulation boot is reading from, or null when the
    /// machine did not boot that way -- which is the ordinary case and the
    /// reason every entry point here checks first.
    /// </summary>
    private IsoImage? _bootCompactDisc;

    /// <summary>The drive number the booting program was given in DL.</summary>
    private byte _bootCompactDiscDrive;

    /// <summary>
    /// Makes the disc answer INT 13h on the given drive number. Called only
    /// by the no-emulation boot path, once, before the loaded image runs.
    /// </summary>
    public void AttachBootCompactDisc(IsoImage disc, byte driveNumber)
    {
        _bootCompactDisc = disc;
        _bootCompactDiscDrive = driveNumber;
    }

    /// <summary>True when this drive number is the booting disc.</summary>
    private bool IsBootCompactDisc(byte drive)
        => _bootCompactDisc is not null && drive == _bootCompactDiscDrive;

    /// <summary>
    /// Function 0x41: does this drive support the extended functions?
    ///
    /// The caller proves it means the question by putting 0x55AA in BX, and
    /// the answer inverts it to 0xAA55 -- a handshake that stops an old BIOS
    /// without these functions from appearing to answer yes by accident.
    /// </summary>
    private void BootCompactDiscCheckExtensions(CpuNs.Cpu cpu)
    {
        if ((cpu.State.Ebx & 0xFFFF) != 0x55AA) { Fail(cpu, 0x01); return; }

        cpu.State.Bx = 0xAA55;
        cpu.State.Ax = 0x0130;   // version 1.x of the specification
        cpu.State.Cx = 0x0005;   // packet access, and removable media support

        _lastStatus = 0;
        Succeed(cpu);
    }

    /// <summary>
    /// Function 0x42: read blocks addressed by number.
    ///
    /// The packet is the same structure the disk services read, so it is
    /// parsed by the same code; only the block size differs, and that
    /// difference is the whole of this method.
    /// </summary>
    private void BootCompactDiscExtendedRead(CpuNs.Cpu cpu)
    {
        if (_bootCompactDisc is null) { Fail(cpu, 0x80); return; }
        if (!ReadPacket(cpu, out AddressPacket packet) || packet.Count > 127)
        {
            Fail(cpu, 0x01);
            return;
        }

        if (packet.Count == 0)
        {
            // A request for nothing succeeds, having transferred nothing.
            WritePacketCount(cpu, 0);
            _lastStatus = 0;
            Succeed(cpu);
            return;
        }

        var buffer = new byte[packet.Count * CompactDiscBlockSize];

        // Reading past the end of the disc leaves zeroes rather than
        // failing, which matches what the drive does and what a loader
        // reading a padded final block expects.
        _bootCompactDisc.ReadSectors(packet.Sector, packet.Count, buffer);

        uint destination = cpu.FarPointerToLinear(packet.BufferSegment, packet.BufferOffset);
        for (int i = 0; i < buffer.Length; i++)
        {
            cpu.WriteLinearByte(destination + (uint)i, buffer[i]);
        }

        DriveAccessed?.Invoke(_bootCompactDiscDrive, false);
        WritePacketCount(cpu, packet.Count);
        _lastStatus = 0;
        Succeed(cpu);
    }

    /// <summary>
    /// Function 0x48: report the drive's parameters.
    ///
    /// The fields that matter for a disc are the block count and the block
    /// size. The geometry fields are filled with the values the
    /// specification prescribes when there is no geometry to report, rather
    /// than left as whatever the caller's buffer held.
    /// </summary>
    private void BootCompactDiscExtendedGetParameters(CpuNs.Cpu cpu)
    {
        if (_bootCompactDisc is null) { Fail(cpu, 0x80); return; }

        uint at = cpu.State.Si;
        ushort size = cpu.ReadMemoryWord(CpuNs.SegReg.Ds, at);
        if (size < 0x1A) { Fail(cpu, 0x01); return; }

        long blocks = _bootCompactDisc.TotalSectors;

        bool extended = size >= 0x1E;
        cpu.WriteMemoryWord(CpuNs.SegReg.Ds, at + 0, (ushort)(extended ? 0x1E : 0x1A));

        // Bit 1: the geometry below is NOT valid, which is the honest answer
        // for a disc. Bit 2: the medium is removable.
        cpu.WriteMemoryWord(CpuNs.SegReg.Ds, at + 2, 0x0004);

        cpu.WriteMemoryDword(CpuNs.SegReg.Ds, at + 4, 0);
        cpu.WriteMemoryDword(CpuNs.SegReg.Ds, at + 8, 0);
        cpu.WriteMemoryDword(CpuNs.SegReg.Ds, at + 12, 0);
        cpu.WriteMemoryDword(CpuNs.SegReg.Ds, at + 16, (uint)blocks);
        cpu.WriteMemoryDword(CpuNs.SegReg.Ds, at + 20, (uint)(blocks >> 32));
        cpu.WriteMemoryWord(CpuNs.SegReg.Ds, at + 24, CompactDiscBlockSize);

        if (extended) cpu.WriteMemoryDword(CpuNs.SegReg.Ds, at + 26, 0xFFFFFFFF);

        _lastStatus = 0;
        Succeed(cpu);
    }

    /// <summary>
    /// The functions a booting disc answers, tried before the ordinary disk
    /// services see the call. Returns false for anything else, so an
    /// unrelated call on an unrelated drive is unaffected.
    /// </summary>
    private bool HandleBootCompactDisc(CpuNs.Cpu cpu, byte function, byte drive)
    {
        if (!IsBootCompactDisc(drive)) return false;

        switch (function)
        {
            case 0x00:   // reset: nothing to do for a disc, but it must succeed
            case 0x0D:
                _lastStatus = 0;
                Succeed(cpu);
                return true;

            case 0x01:
                cpu.State.SetReg8(4, _lastStatus);
                Succeed(cpu);
                return true;

            case 0x15:
                // Drive type 3 is "fixed disk" and is what a CD reports here
                // once it is being read by blocks rather than as a floppy.
                cpu.State.SetReg8(4, 0x03);
                _lastStatus = 0;
                Succeed(cpu);
                return true;

            case 0x41: BootCompactDiscCheckExtensions(cpu); return true;
            case 0x42: BootCompactDiscExtendedRead(cpu); return true;
            case 0x48: BootCompactDiscExtendedGetParameters(cpu); return true;

            // Writing to a disc, and seeking on one, are refused rather than
            // silently succeeding: a loader that believes a write worked
            // would be harder to diagnose than one told plainly it cannot.
            case 0x03:
            case 0x43:
                Fail(cpu, 0x03);   // write protected
                return true;

            default:
                Fail(cpu, 0x01);   // invalid function
                return true;
        }
    }
}
