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
    AfterIdeTiming,
    AfterSerialMouse,
    AfterVideoTopology,
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

        if (end == LegacySnapshotEnd.AfterIdeTiming)
        {
            return;
        }

        byte[] serialMouse = machine.SerialMouse.Capture();
        file.Write(serialMouse.Length);
        file.Write(serialMouse);

        if (end == LegacySnapshotEnd.AfterSerialMouse)
        {
            return;
        }

        WriteVideoTopology(file, machine);

        if (end == LegacySnapshotEnd.AfterVideoTopology)
        {
            return;
        }

        WriteDeviceStateTrailer(file, machine);
    }

    /// <summary>
    /// Returns the "VID1" record the last unsectioned builds wrote after the
    /// serial mouse state: its own length, the magic word, a version, the
    /// video model, and the ViRGE engine block (empty on a Trio). Exposed so
    /// a test can corrupt one field of a legacy file's video tail without
    /// reaching into the emulator, which no longer writes this format.
    /// </summary>
    public static byte[] VideoTopology(Machine machine)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteVideoTopology(writer, machine);
        return stream.ToArray();
    }

    /// <summary>
    /// Returns the "DVS1" trailer the last unsectioned builds wrote after the
    /// video topology: a header naming how many records follow, then each
    /// record's kind, model, schema version, reserved flags and payload.
    /// Exposed for the same reason as <see cref="VideoTopology"/>.
    /// </summary>
    public static byte[] DeviceStateTrailer(Machine machine)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteDeviceStateTrailer(writer, machine);
        return stream.ToArray();
    }

    /// <summary>"VID1": magic 0x31444956, header length 12 (magic, version, model, engine length).</summary>
    private static void WriteVideoTopology(BinaryWriter file, Machine machine)
    {
        const int headerLength = 12;
        byte[] engine = machine.Virge?.CaptureEngines() ?? [];

        file.Write(headerLength + engine.Length);
        file.Write(0x31444956u);
        file.Write((ushort)1);
        file.Write((ushort)machine.VideoModel);
        file.Write(engine.Length);
        file.Write(engine);
    }

    /// <summary>
    /// "DVS1": magic 0x31535644, header length 8 (magic, version, record
    /// count). Each record is kind (Trio 1, DMA 2, Sound Blaster 3, ISA PnP
    /// 4), a device model (always 0), a record schema version (always 1),
    /// reserved flags (always 0) and a length-prefixed payload. A record
    /// whose payload is empty -- the Trio accelerator on a ViRGE machine --
    /// is omitted entirely, exactly as the last unsectioned builds wrote it.
    /// </summary>
    private static void WriteDeviceStateTrailer(BinaryWriter file, Machine machine)
    {
        const int headerLength = 8;
        const int recordHeaderLength = 12;

        byte[] trioAccelerator = machine.TrioAccelerator?.Capture() ?? [];
        byte[] dma = machine.Dma.Capture();
        byte[] soundBlaster = machine.SoundBlaster?.Capture() ?? [];
        byte[] isaPnp = machine.IsaPlugAndPlay.Capture();
        byte[][] payloadsByKind = [trioAccelerator, dma, soundBlaster, isaPnp];

        int length = headerLength;
        ushort count = 0;
        foreach (byte[] payload in payloadsByKind)
        {
            if (payload.Length != 0)
            {
                length += recordHeaderLength + payload.Length;
                count++;
            }
        }

        file.Write(length);
        file.Write(0x31535644u);
        file.Write((ushort)1);
        file.Write(count);

        for (int kindIndex = 0; kindIndex < payloadsByKind.Length; kindIndex++)
        {
            byte[] payload = payloadsByKind[kindIndex];
            if (payload.Length == 0)
            {
                continue;
            }

            file.Write((ushort)(kindIndex + 1));
            file.Write((ushort)0);
            file.Write((ushort)1);
            file.Write((ushort)0);
            file.Write(payload.Length);
            file.Write(payload);
        }
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
