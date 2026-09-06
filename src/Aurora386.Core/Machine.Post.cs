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
/// Power-on self test: what the BIOS does between reset and the boot sector
/// -- interrupt vectors, the interrupt controllers, the BIOS data area, and
/// the handover to the boot device.
/// </summary>
public sealed partial class Machine
{

    private void InstallBios()
    {
        Memory.MapRom(BiosRom.Base, BiosRom.Build());

        // The 386 begins fetching at 0xFFFFFFF0, so the top 64 KB of the
        // 32-bit space must show the same ROM that is mapped at 0xF0000.
        Memory.MapHighAlias(0xFFFF0000, BiosRom.Base);
        Cpu.NativeHandler = HandleBiosCall;
        Cpu.Reset();
    }

    /// <summary>
    /// Machine initialisation: sets up the BIOS data area, the interrupt
    /// vector table and the devices, exactly as a real BIOS's POST would.
    /// </summary>
    /// <summary>The EBDA sits in the last kilobyte below 640 KB.</summary>
    public const ushort ExtendedBiosDataSegment = 0x9FC0;

    private void RunPost()
    {
        // A reboot starts with no CD-ROM driver loaded; the guest reveals one
        // again by sending its first packet.
        GuestHasItsOwnCompactDiscExtensions = false;
        // The extended memory count comes first, as on a real board, and it
        // leaves nothing behind above the first megabyte.
        Memory.ClearExtendedMemory();

        // 639 KB for DOS, and the last kilobyte is the Extended BIOS Data
        // Area: its first byte is its size in KB, and INT 12h stops short of
        // it so that no program overwrites the BIOS's own state.
        Bda.Initialize(639, DiskServices.HardDiskCount);
        Bda.ExtendedBiosDataSegment = ExtendedBiosDataSegment;
        Memory.WriteByte((uint)ExtendedBiosDataSegment << 4, 1);
        PublishPointingDeviceInEquipment();

        InstallInterruptVectors();
        InitializeInterruptController();

        // IRQ 12 is left open from here on when a PS/2 mouse is fitted, as a
        // real PS/2 BIOS does. See PointingDeviceServices.OpenMouseInterrupt
        // for why the moment matters: a driver that saves the mask before the
        // line is open restores a closed one.
        if (Mouse.ConnectedInterface == MouseInterface.Ps2)
            PointingDeviceServices.OpenMouseInterrupt(this);

        // There is no coprocessor in this machine -- the equipment word says so
        // -- and CR0's emulation bit is how the processor is told. With it set,
        // a floating-point instruction faults to vector 7 and a software
        // emulator can finish the work; with it clear the instruction would be
        // attempted against a coprocessor that is not there. A real BIOS sets
        // this during POST after failing to find an 80387.
        Cpu.State.Cr0 |= Aurora386.Core.Cpu.CpuState.Cr0Emulation;

        // 80x25 text mode, which is how a PC presents itself at power-on.
        VideoRegisters.VideoMode = 0x03;
        VideoRegisters.TextColumns = 80;
        VideoRegisters.TextRows = 25;
        ClearScreen();
        VideoRegisters.NotifyModeChanged();
    }

    private void InstallInterruptVectors()
    {
        // No vector may be left at zero. A real BIOS fills every one of them,
        // and software relies on it: DOS reads words out of this table and
        // uses them as segment values even for vectors it does not handle, so
        // a zero there is not "free vector" but a pointer that sends execution
        // to 0000:0000. Fill everything first, then overwrite the entries that
        // have a real handler.
        for (int vector = 0; vector < 256; vector++)
            SetVector((byte)vector, BiosRom.Entry.DummyIrq);

        // Each vector is four bytes -- offset then segment -- starting at
        // physical address 0.
        SetVector(0x08, BiosRom.Entry.TimerTick);
        SetVector(0x09, BiosRom.Entry.KeyboardIrq);
        SetVector(0x10, BiosRom.Entry.Video);
        SetVector(0x11, BiosRom.Entry.EquipmentList);
        SetVector(0x12, BiosRom.Entry.MemorySize);
        SetVector(0x13, BiosRom.Entry.Disk);
        SetVector(0x14, BiosRom.Entry.Serial);
        SetVector(0x15, BiosRom.Entry.System);
        SetVector(0x16, BiosRom.Entry.Keyboard);
        SetVector(0x17, BiosRom.Entry.Printer);
        SetVector(0x19, BiosRom.Entry.Bootstrap);
        SetVector(0x1A, BiosRom.Entry.TimeOfDay);

        // INT 1Eh is a pointer, not a handler: it addresses eleven bytes
        // describing the floppy drive. DOS reads it to find out the geometry,
        // above all the sectors per track, which is what tells a 1.44 MB
        // floppy from a 2.88 MB one.
        SetVectorFar(0x1E, DiskParameterSegment, DiskParameterOffset);
        WriteDiskParameterTable(18);
        SetVector(0x2F, BiosRom.Entry.Multiplex);

        // INT 33h is not a BIOS service at all -- it belongs to MOUSE.COM,
        // which is why a real machine has this vector pointing at nothing
        // until a driver is loaded. Providing it here gives the guest a
        // working mouse with no driver, which is the whole point.
        SetVector(0x33, BiosRom.Entry.Mouse);

        // IRQ 12's vector, so that a mouse packet nobody claimed still gets
        // read out of the controller rather than jamming it.
        SetVector(0x74, BiosRom.Entry.MouseIrq);

        // Vector 7 is where a floating-point instruction lands when there is
        // no coprocessor. Without a handler there the fault would return to
        // the very instruction that raised it and the machine would make no
        // further progress.
        SetVector(0x07, BiosRom.Entry.Coprocessor);

        // IRQs 2-7 and 8-15 with no handler of their own point at a stub that
        // does nothing but acknowledge the interrupt.
        for (byte vector = 0x0A; vector <= 0x0F; vector++)
            SetVector(vector, BiosRom.Entry.DummyIrq);
        for (byte vector = 0x70; vector <= 0x77; vector++)
            if (vector != 0x74) SetVector(vector, BiosRom.Entry.DummyIrq);   // IRQ 12 keeps the mouse entry set above
    }

    private void SetVector(byte vector, ushort offset)
    {
        Memory.WriteWord((uint)vector * 4, offset);
        Memory.WriteWord((uint)vector * 4 + 2, 0xF000);
    }

    /// <summary>
    /// Programs the two PICs the way the BIOS does: IRQ 0-7 onto vectors
    /// 0x08-0x0F, IRQ 8-15 onto vectors 0x70-0x77. Note that the master's
    /// range collides with the CPU's own exception vectors, a design mistake
    /// IBM made in 1981 and which nobody could ever fix without breaking every
    /// program in existence.
    /// </summary>
    private void InitializeInterruptController()
    {
        // Master
        Io.WriteByte(0x20, 0x11);   // ICW1: begin initialisation, ICW4 will follow
        Io.WriteByte(0x21, 0x08);   // ICW2: vector base
        Io.WriteByte(0x21, 0x04);   // ICW3: the slave hangs off line 2
        Io.WriteByte(0x21, 0x01);   // ICW4: 8086 mode
        Io.WriteByte(0x21, 0xB8);   // mask: leave timer, keyboard and cascade enabled

        // Slave
        Io.WriteByte(0xA0, 0x11);
        Io.WriteByte(0xA1, 0x70);
        Io.WriteByte(0xA1, 0x02);
        Io.WriteByte(0xA1, 0x01);
        // Two slave lines are wired up: IRQ 12 for the mouse and IRQ 14 for
        // the IDE controller, so those two bits are the ones left clear. IRQ 2
        // on the master is already open, which it must be: the slave reaches
        // the CPU only through it, and a masked cascade silently swallows
        // every interrupt from 8 to 15.
        //
        // Forgetting one of these bits is a mistake that hides itself. The
        // device works perfectly -- it answers every register, it completes
        // every command -- and the driver waiting for its interrupt simply
        // never hears from it, which reads as broken hardware rather than as
        // a masked line.
        const byte SlaveMask = 0xEF & 0xBF;   // IRQ 12 and IRQ 14 enabled
        Io.WriteByte(0xA1, SlaveMask);
    }

    private void ClearScreen()
    {
        for (int i = 0; i < 80 * 25; i++)
        {
            uint offset = VgaMemory.TextWindowOffset + (uint)(i * 2);
            VideoMemory[offset] = (byte)' ';
            VideoMemory[offset + 1] = 0x07;
        }
    }

    /// <summary>
    /// INT 19h: loads the boot sector and hands control to it.
    ///
    /// The sector is read to 0000:7C00, an address fixed by the PC
    /// architecture, and must end with the 0xAA55 signature -- without it the
    /// disk is not considered bootable.
    /// </summary>
    private void Boot(CpuNs.Cpu cpu)
    {
        foreach (DiskImage disk in _disks.OrderBy(d => d.DriveNumber))
        {
            var sector = new byte[DiskImage.SectorSize];
            if (!disk.ReadSectors(0, 0, 1, 1, sector)) continue;

            if (sector[510] != 0x55 || sector[511] != 0xAA) continue;

            const uint bootAddress = 0x7C00;
            sector.CopyTo(Memory.GetRamSpan(bootAddress, sector.Length));

            // The boot sector expects DL to hold the drive it was loaded
            // from: that is how it knows where to find the rest of the system.
            cpu.State.SetReg8(2, disk.DriveNumber);

            cpu.SetSegment(CpuNs.SegReg.Cs, 0);
            cpu.State.Eip = bootAddress;
            cpu.SetSegment(CpuNs.SegReg.Ds, 0);
            cpu.SetSegment(CpuNs.SegReg.Es, 0);
            cpu.SetSegment(CpuNs.SegReg.Ss, 0);
            cpu.State.Esp = 0x7C00;      // stack grows down from just below the sector

            Booted = true;
            return;
        }

        // No bootable disk: halt, which is what a real PC does after printing
        // "No bootable device".
        cpu.State.Halted = true;
    }

    /// <summary>Writes a vector that points at data rather than at code.</summary>
    private void SetVectorFar(byte vector, ushort segment, ushort offset)
    {
        Memory.WriteWord((uint)(vector * 4), offset);
        Memory.WriteWord((uint)(vector * 4 + 2), segment);
    }
}
