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

    /// <summary>
    /// The video card: its memory and every register that decides what is on
    /// screen.
    ///
    /// Without these a restored machine has the guest's pixels and the BIOS's
    /// idea of the mode, which agree about nothing -- and the guest, having
    /// no reason to redraw a picture it believes is already there, never puts
    /// it right. It is the difference between a snapshot that resumes and one
    /// that resumes blind.
    /// </summary>
    private readonly byte[] _videoMemory;
    private readonly byte[] _videoRegisters;

    /// <summary>
    /// The chips that decide whether the guest is ever interrupted again.
    ///
    /// A snapshot without them restores the interrupt controllers to their
    /// power-on state, which is every line masked, and the timer to its
    /// power-on divisor. The guest then sits in whatever idle loop it was in,
    /// waiting for a tick that cannot arrive -- and the symptom, a picture
    /// half drawn and a machine that ignores the keyboard, looks exactly like
    /// a fault in whatever the guest was drawing at the time. This cost a
    /// good deal of time to find, so it is worth the forty bytes.
    /// </summary>
    private readonly byte[] _interruptControllers;
    private readonly byte[] _timer;
    private readonly byte[] _keyboard;

    /// <summary>The redirector's attachment to DOS; see <see cref="Machine.CaptureDosState"/>.</summary>
    private readonly byte[] _dosState;

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

        _videoMemory = machine.VideoMemory.CaptureMemory();
        _videoRegisters = machine.VideoRegisters.CaptureRegisters();

        _interruptControllers = machine.Interrupts.Capture();
        _timer = machine.Timer.Capture();
        _keyboard = machine.Keyboard.Capture();
        _dosState = machine.CaptureDosState();
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

        if (_videoMemory.Length > 0) machine.VideoMemory.RestoreMemory(_videoMemory);
        if (_videoRegisters.Length > 0) machine.VideoRegisters.RestoreRegisters(_videoRegisters);

        // The keyboard controller owns A20, so it is restored after the bus:
        // the other order would leave the line as the snapshot's own field
        // said rather than as the controller had it.
        if (_interruptControllers.Length > 0) machine.Interrupts.Restore(_interruptControllers);
        if (_timer.Length > 0) machine.Timer.Restore(_timer);
        if (_keyboard.Length > 0) machine.Keyboard.Restore(_keyboard);

        // After the memory, because attaching marks the CDS entries in it.
        machine.RestoreDosState(_dosState);

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

        // The video card last, so that a file written before it existed still
        // reads: the loader stops when the stream ends and leaves the fields
        // empty rather than failing.
        file.Write(_videoMemory.Length);
        file.Write(_videoMemory);
        file.Write(_videoRegisters.Length);
        file.Write(_videoRegisters);

        file.Write(_interruptControllers.Length);
        file.Write(_interruptControllers);
        file.Write(_timer.Length);
        file.Write(_timer);
        file.Write(_keyboard.Length);
        file.Write(_keyboard);

        file.Write(_dosState.Length);
        file.Write(_dosState);
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

        // A file written before the video card was part of a snapshot simply
        // ends here. Reading it is not an error: what comes back is a
        // machine that resumes with whatever the display happens to hold,
        // which is what those files always meant.
        if (file.BaseStream.Position < file.BaseStream.Length)
        {
            _videoMemory = file.ReadBytes(file.ReadInt32());
            _videoRegisters = file.ReadBytes(file.ReadInt32());
        }
        else
        {
            _videoMemory = [];
            _videoRegisters = [];
        }

        // Same again for the device state, which came later still: a file
        // that stops before it restores a machine whose interrupt
        // controllers are as they power up.
        if (file.BaseStream.Position < file.BaseStream.Length)
        {
            _interruptControllers = file.ReadBytes(file.ReadInt32());
            _timer = file.ReadBytes(file.ReadInt32());
            _keyboard = file.ReadBytes(file.ReadInt32());
        }
        else
        {
            _interruptControllers = [];
            _timer = [];
            _keyboard = [];
        }

        // And the redirector's attachment, later again. A file without it
        // restores a guest whose shared folders have to be found afresh,
        // which for a DOS already at its prompt never happens.
        _dosState = file.BaseStream.Position < file.BaseStream.Length
            ? file.ReadBytes(file.ReadInt32())
            : [];
    }

    /// <summary>Reads a snapshot back from a file.</summary>
    public static MachineSnapshot Load(string path)
    {
        using var file = new BinaryReader(File.OpenRead(path));
        return new MachineSnapshot(file);
    }
}
