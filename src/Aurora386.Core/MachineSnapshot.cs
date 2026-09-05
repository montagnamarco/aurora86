using Aurora386.Core.Cpu;

namespace Aurora386.Core;

/// <summary>
/// A copy of the machine's state, taken at an instant and put back later.
///
/// This exists for the sake of investigation rather than for the user. Reaching
/// an interesting moment in a guest can take twenty minutes of emulated boot
/// and installation; a defect that needs twenty attempts to corner is a day's
/// work at that rate and a few minutes with a snapshot taken just before it.
///
/// What is captured is RAM and the processor. Devices are deliberately left
/// out: their state is timers, latches and half-finished transfers, and
/// restoring those correctly is a much larger problem than the one this is
/// meant to solve. A snapshot is therefore faithful for what the guest's own
/// code does next, and not for a device transfer that was in flight when it
/// was taken -- which is why it belongs in diagnostics and not in a save-game.
/// </summary>
public sealed class MachineSnapshot
{
    private readonly byte[] _ram;
    private readonly uint[] _gpr;
    private readonly SegmentCache[] _segments;
    private readonly LazyFlags _flags;

    private readonly uint _eip, _cr0, _cr2, _cr3;
    private readonly uint _gdtBase, _idtBase, _ldtBase, _ldtLimit, _tssBase, _tssLimit;
    private readonly ushort _gdtLimit, _idtLimit, _ldtr, _taskRegister;
    private readonly Flags _otherFlags;
    private readonly byte _cpl;
    private readonly bool _halted, _virtual8086;

    /// <summary>
    /// The A20 gate. It belongs in a snapshot even though it is a chipset
    /// latch rather than processor state: with it closed every address above
    /// the first megabyte silently wraps, so a snapshot restored with the
    /// wrong A20 reads different memory than the one it captured -- the page
    /// tables most of all, and then nothing resolves.
    /// </summary>
    private readonly bool _a20;

    private MachineSnapshot(Machine machine)
    {
        CpuState state = machine.Cpu.State;

        _ram = machine.Memory.CaptureRam();
        _gpr = (uint[])state.Gpr.Clone();
        _segments = (SegmentCache[])state.Segments.Clone();
        _flags = machine.Cpu.Flags;

        _eip = state.Eip;
        _cr0 = state.Cr0; _cr2 = state.Cr2; _cr3 = state.Cr3;
        _gdtBase = state.GdtBase; _gdtLimit = state.GdtLimit;
        _idtBase = state.IdtBase; _idtLimit = state.IdtLimit;
        _ldtr = state.Ldtr; _ldtBase = state.LdtBase; _ldtLimit = state.LdtLimit;
        _taskRegister = state.TaskRegister; _tssBase = state.TssBase; _tssLimit = state.TssLimit;
        _otherFlags = state.OtherFlags;
        _cpl = state.CplRaw;
        _halted = state.Halted;
        _virtual8086 = state.Virtual8086Mode;
        _a20 = machine.Memory.A20Enabled;
    }

    /// <summary>Takes a snapshot of the machine as it stands.</summary>
    public static MachineSnapshot Capture(Machine machine) => new(machine);

    /// <summary>Puts this state back into a machine.</summary>
    public void RestoreTo(Machine machine)
    {
        CpuState state = machine.Cpu.State;

        machine.Memory.A20Enabled = _a20;
        machine.Memory.RestoreRam(_ram);
        _gpr.CopyTo(state.Gpr, 0);
        _segments.CopyTo(state.Segments, 0);
        machine.Cpu.Flags = _flags;

        state.Eip = _eip;
        state.Cr0 = _cr0; state.Cr2 = _cr2; state.Cr3 = _cr3;
        state.GdtBase = _gdtBase; state.GdtLimit = _gdtLimit;
        state.IdtBase = _idtBase; state.IdtLimit = _idtLimit;
        state.Ldtr = _ldtr; state.LdtBase = _ldtBase; state.LdtLimit = _ldtLimit;
        state.TaskRegister = _taskRegister; state.TssBase = _tssBase; state.TssLimit = _tssLimit;
        state.OtherFlags = _otherFlags;
        state.CplRaw = _cpl;
        state.Halted = _halted;
        state.Virtual8086Mode = _virtual8086;

        // Paging translations cached from the old contents of memory describe
        // an address space that no longer exists.
        machine.Cpu.FlushTlb();
    }

    /// <summary>Writes a snapshot to a file.</summary>
    public void Save(string path)
    {
        using var file = new BinaryWriter(File.Create(path));

        file.Write(_ram.Length);
        file.Write(_ram);

        foreach (uint value in _gpr) file.Write(value);

        foreach (SegmentCache segment in _segments)
        {
            file.Write(segment.Selector); file.Write(segment.Base); file.Write(segment.Limit);
            file.Write(segment.Big); file.Write(segment.Readable);
            file.Write(segment.Writable); file.Write(segment.ExpandDown); file.Write(segment.Dpl);
        }

        file.Write((byte)_flags.Op); file.Write((uint)_flags.Stored);
        file.Write(_flags.Left); file.Write(_flags.Right);
        file.Write(_flags.Result); file.Write(_flags.Size);

        file.Write(_eip); file.Write(_cr0); file.Write(_cr2); file.Write(_cr3);
        file.Write(_gdtBase); file.Write(_gdtLimit);
        file.Write(_idtBase); file.Write(_idtLimit);
        file.Write(_ldtr); file.Write(_ldtBase); file.Write(_ldtLimit);
        file.Write(_taskRegister); file.Write(_tssBase); file.Write(_tssLimit);
        file.Write((uint)_otherFlags); file.Write(_cpl);
        file.Write(_halted); file.Write(_virtual8086); file.Write(_a20);
    }

    private MachineSnapshot(BinaryReader file)
    {
        _ram = file.ReadBytes(file.ReadInt32());

        _gpr = new uint[8];
        for (int i = 0; i < 8; i++) _gpr[i] = file.ReadUInt32();

        _segments = new SegmentCache[6];
        for (int i = 0; i < 6; i++)
        {
            _segments[i].Selector = file.ReadUInt16();
            _segments[i].Base = file.ReadUInt32();
            _segments[i].Limit = file.ReadUInt32();
            _segments[i].Big = file.ReadBoolean();
            _segments[i].Readable = file.ReadBoolean();
            _segments[i].Writable = file.ReadBoolean();
            _segments[i].ExpandDown = file.ReadBoolean();
            _segments[i].Dpl = file.ReadByte();
        }

        _flags.Op = (FlagOp)file.ReadByte();
        _flags.Stored = (Flags)file.ReadUInt32();
        _flags.Left = file.ReadUInt32();
        _flags.Right = file.ReadUInt32();
        _flags.Result = file.ReadUInt32();
        _flags.Size = file.ReadByte();

        _eip = file.ReadUInt32(); _cr0 = file.ReadUInt32();
        _cr2 = file.ReadUInt32(); _cr3 = file.ReadUInt32();
        _gdtBase = file.ReadUInt32(); _gdtLimit = file.ReadUInt16();
        _idtBase = file.ReadUInt32(); _idtLimit = file.ReadUInt16();
        _ldtr = file.ReadUInt16(); _ldtBase = file.ReadUInt32(); _ldtLimit = file.ReadUInt32();
        _taskRegister = file.ReadUInt16(); _tssBase = file.ReadUInt32(); _tssLimit = file.ReadUInt32();
        _otherFlags = (Flags)file.ReadUInt32(); _cpl = file.ReadByte();
        _halted = file.ReadBoolean(); _virtual8086 = file.ReadBoolean();
        _a20 = file.ReadBoolean();
    }

    /// <summary>Reads a snapshot back from a file.</summary>
    public static MachineSnapshot Load(string path)
    {
        using var file = new BinaryReader(File.OpenRead(path));
        return new MachineSnapshot(file);
    }
}
