using Aurora86.Core.Cpu;
using Aurora86.Core.Devices.Storage;
using CpuNs = Aurora86.Core.Cpu;

namespace Aurora86.Core.Firmware;

public sealed partial class DiskServices
{
    /// <summary>
    /// Routes supported CHS reads to the ROM's PIO routine. Windows 95 watches
    /// the BIOS's port instructions to associate a BIOS disk with an IDE cable;
    /// reading the image directly bypasses that monitor and rejects the channel.
    /// </summary>
    internal bool TryRouteIdeRead(CpuNs.Cpu cpu)
    {
        byte drive = cpu.State.GetReg8(2);
        if (cpu.State.GetReg8(4) != 2 || drive is < 0x80 or > 0x81)
        {
            return false;
        }
        DiskImage? disk = GetDrive(drive);
        if (disk is null)
        {
            return false;
        }
        (int cylinder, int head, int sector, int count) = DecodeChs(cpu);
        long firstSector = LinearOf(disk, cylinder, head, sector);
        bool validRange = count > 0 && firstSector >= 0
            && firstSector + count <= disk.TotalSectors
            && firstSector + count <= 0x10000000;
        bool bufferFits = cpu.State.Bx + count * DiskImage.SectorSize <= 0x10000;
        if (!validRange || !bufferFits)
        {
            return false;
        }
        cpu.State.Eip = BiosRom.Entry.DiskPioRead;
        return true;
    }

    /// <summary>
    /// Supplies the PIO routine's registers after PUSHAD has saved the caller.
    /// No mutable request is kept in the firmware object: nested BIOS calls
    /// and checkpoints carry the request entirely in the guest's registers.
    /// </summary>
    internal void PrepareIdeRead(CpuNs.Cpu cpu, IdeController primary, IdeController secondary)
    {
        byte drive = cpu.State.GetReg8(2);
        DiskImage disk = GetDrive(drive)!;
        (int cylinder, int head, int sector, int count) = DecodeChs(cpu);
        long firstSector = LinearOf(disk, cylinder, head, sector);
        IdeController controller = drive == 0x80 ? primary : secondary;
        uint commandBase = drive == 0x80 ? 0x1F0u : 0x170u;
        uint destination = cpu.State.Bx;
        NoteTransfer(drive, write: false, cylinder, head, sector, count);
        cpu.State.Eax = (uint)firstSector;
        cpu.State.Ebx = commandBase | ((uint)controller.DeviceControl << 16);
        cpu.State.Ecx = (uint)count;
        cpu.State.Edi = destination;
    }

    /// <summary>Returns the PIO status through the saved AX and interrupt flags.</summary>
    internal void CompleteIdeRead(CpuNs.Cpu cpu)
    {
        byte status = cpu.State.GetReg8(0);
        uint stack = cpu.State.Ss.Big ? cpu.State.Esp : cpu.State.Sp;
        uint SavedOffset(uint offset)
        {
            uint address = stack + offset;
            return cpu.State.Ss.Big ? address : (ushort)address;
        }
        // PUSHAD saved EAX last in memory; the original interrupt frame
        // starts 32 bytes above the current stack pointer.
        uint savedAx = SavedOffset(28);
        uint savedFlags = SavedOffset(36);
        ushort originalAx = cpu.ReadMemoryWord(SegReg.Ss, savedAx);
        ushort flags = cpu.ReadMemoryWord(SegReg.Ss, savedFlags);
        cpu.WriteMemoryWord(SegReg.Ss, savedAx, (ushort)((originalAx & 0xFF) | (status << 8)));
        flags = status == 0 ? (ushort)(flags & ~1) : (ushort)(flags | 1);
        cpu.WriteMemoryWord(SegReg.Ss, savedFlags, flags);
        _lastStatus = status;
        PublishStatus(status);
    }
}
