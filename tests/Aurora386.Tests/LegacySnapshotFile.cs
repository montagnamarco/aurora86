using Aurora386.Core;
using Aurora386.Core.Cpu;

namespace Aurora386.Tests;

/// <summary>
/// Where a file in the unsectioned snapshot format ends. That format grew by
/// appending, so each value names the last group a build of that time wrote.
/// </summary>
public enum LegacySnapshotEnd
{
    AfterA20,
    AfterDos,
    AfterUsb,
    AfterCompactDisc,
    Complete,
}

/// <summary>
/// Writes a snapshot in the layout used before snapshots had sections, so the
/// tests can prove such files still load. The layout is written out here field
/// by field rather than produced by the emulator, because the emulator no
/// longer writes it: this is the specification of those files.
/// </summary>
public static class LegacySnapshotFile
{
    public static void Write(Machine machine, string path, LegacySnapshotEnd end)
    {
        using var file = new BinaryWriter(File.Create(path));

        WriteCore(file, machine);

        if (end == LegacySnapshotEnd.AfterA20)
        {
            return;
        }

        WriteCounted(file, machine.VideoMemory.CaptureMemory());
        WriteCounted(file, machine.VideoRegisters.CaptureRegisters());
        WriteCounted(file, machine.Interrupts.Capture());
        WriteCounted(file, machine.Timer.Capture());
        WriteCounted(file, machine.Keyboard.Capture());
        WriteCounted(file, machine.CaptureDosState());

        if (end == LegacySnapshotEnd.AfterDos)
        {
            return;
        }

        WriteCounted(file, machine.Pci.Capture());
        WriteCounted(file, CaptureUsbState(machine));

        if (end == LegacySnapshotEnd.AfterUsb)
        {
            return;
        }

        file.Write(machine.GuestHasItsOwnCompactDiscExtensions);

        if (end == LegacySnapshotEnd.AfterCompactDisc)
        {
            return;
        }

        // The flag that said IDETIM had been recorded as it really was.
        file.Write(true);
    }

    /// <summary>RAM with its length, the processor fields, then A20.</summary>
    private static void WriteCore(BinaryWriter file, Machine machine)
    {
        CpuState state = machine.Cpu.State;
        LazyFlags flags = machine.Cpu.Flags;
        byte[] ram = machine.Memory.CaptureRam();

        file.Write(ram.Length);
        file.Write(ram);

        foreach (uint value in state.Gpr)
        {
            file.Write(value);
        }

        foreach (SegmentCache segment in state.Segments)
        {
            file.Write(segment.Selector);
            file.Write(segment.Base);
            file.Write(segment.Limit);
            file.Write(segment.Big);
            file.Write(segment.Readable);
            file.Write(segment.Writable);
            file.Write(segment.ExpandDown);
            file.Write(segment.Dpl);
        }

        file.Write((byte)flags.Op);
        file.Write((uint)flags.Stored);
        file.Write(flags.Left);
        file.Write(flags.Right);
        file.Write(flags.Result);
        file.Write(flags.Size);

        file.Write(state.Eip);
        file.Write(state.Cr0);
        file.Write(state.Cr2);
        file.Write(state.Cr3);
        file.Write(state.GdtBase);
        file.Write(state.GdtLimit);
        file.Write(state.IdtBase);
        file.Write(state.IdtLimit);
        file.Write(state.Ldtr);
        file.Write(state.LdtBase);
        file.Write(state.LdtLimit);
        file.Write(state.TaskRegister);
        file.Write(state.TssBase);
        file.Write(state.TssLimit);
        file.Write((uint)state.OtherFlags);
        file.Write(state.CplRaw);
        file.Write(state.Halted);
        file.Write(state.Virtual8086Mode);
        file.Write(machine.Memory.A20Enabled);
    }

    private static void WriteCounted(BinaryWriter file, byte[] block)
    {
        file.Write(block.Length);
        file.Write(block);
    }

    /// <summary>
    /// The container's own USB section, exactly as it was written before
    /// USB became a card: the two controllers' state, length-prefixed, one
    /// after the other, or nothing at all with no USB card fitted. Kept here
    /// rather than on <see cref="Machine"/> because the emulator no longer
    /// writes this shape either -- this file is the specification of it.
    /// </summary>
    private static byte[] CaptureUsbState(Machine machine)
    {
        if (machine.Usb is null)
        {
            return [];
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (var controller in new[] { machine.Usb, machine.UsbStorageController! })
        {
            byte[] state = controller.Capture();
            writer.Write(state.Length);
            writer.Write(state);
        }
        return stream.ToArray();
    }
}
