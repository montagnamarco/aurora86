using Aurora386.Core.Cpu;

namespace Aurora386.Core.Diagnostics;

/// <summary>
/// Decodes the tables that define protected mode: the global and local
/// descriptor tables, the interrupt descriptor table, and the task state
/// segment.
///
/// These are the structures that turn a selector into an address and a vector
/// into a handler, and reading them by hand is miserable work: a descriptor
/// is eight bytes with the base scattered across three fields and the limit
/// across two, in an order that made sense to the 286 and has been kept for
/// compatibility ever since. Getting one wrong is invisible until the guest
/// faults somewhere unrelated, which is exactly when you want to look.
///
/// Everything here reads memory and nothing writes it: a debugger must never
/// disturb what it is looking at.
/// </summary>
public sealed class DescriptorTables
{
    private readonly Machine _machine;

    public DescriptorTables(Machine machine) => _machine = machine;

    /// <summary>One descriptor, with the fields already put back together.</summary>
    public readonly record struct Descriptor(
        int Index, ushort Selector, uint Base, uint Limit, byte Access, byte Flags)
    {
        public bool Present => (Access & 0x80) != 0;
        public int Dpl => (Access >> 5) & 3;

        /// <summary>
        /// Bit 4 of the access byte separates the two families. Set means a
        /// code or data segment, the ordinary kind; clear means a system
        /// descriptor -- a task state segment, an LDT, or a gate -- and then
        /// the low four bits are a type rather than a set of flags.
        /// </summary>
        public bool IsSystem => (Access & 0x10) == 0;

        public bool IsCode => !IsSystem && (Access & 0x08) != 0;

        /// <summary>The B/D bit: 32-bit operands and addresses by default.</summary>
        public bool Is32Bit => (Flags & 0x40) != 0;

        /// <summary>The granularity bit: the limit counts pages instead of bytes.</summary>
        public bool Granular => (Flags & 0x80) != 0;

        public string TypeName
        {
            get
            {
                if (!IsSystem)
                {
                    string kind = IsCode ? "code" : "data";
                    string extra = IsCode
                        ? ((Access & 0x04) != 0 ? " conforming" : "") + ((Access & 0x02) != 0 ? " readable" : "")
                        : ((Access & 0x04) != 0 ? " expand-down" : "") + ((Access & 0x02) != 0 ? " writable" : "");
                    return kind + extra;
                }

                return (Access & 0x0F) switch
                {
                    0x01 => "286 TSS (available)",
                    0x02 => "LDT",
                    0x03 => "286 TSS (busy)",
                    0x04 => "286 call gate",
                    0x05 => "task gate",
                    0x06 => "286 interrupt gate",
                    0x07 => "286 trap gate",
                    0x09 => "386 TSS (available)",
                    0x0B => "386 TSS (busy)",
                    0x0C => "386 call gate",
                    0x0E => "386 interrupt gate",
                    0x0F => "386 trap gate",
                    _ => $"reserved type {Access & 0x0F:X}",
                };
            }
        }

        public override string ToString()
            => $"{Selector:X4}  base {Base:X8}  limit {Limit:X8}  dpl {Dpl}  "
             + $"{(Present ? " " : "not present, ")}{TypeName}"
             + (IsSystem ? "" : Is32Bit ? "  32-bit" : "  16-bit");
    }

    /// <summary>The global descriptor table, as far as its limit says it goes.</summary>
    public IReadOnlyList<Descriptor> ReadGlobalTable()
        => ReadTable(_machine.Cpu.State.GdtBase, _machine.Cpu.State.GdtLimit, localTable: false);

    /// <summary>The local descriptor table of the current task, empty when none is loaded.</summary>
    public IReadOnlyList<Descriptor> ReadLocalTable()
    {
        CpuState state = _machine.Cpu.State;
        if ((state.Ldtr & 0xFFFC) == 0) return [];
        return ReadTable(state.LdtBase, (ushort)Math.Min(state.LdtLimit, 0xFFFF), localTable: true);
    }

    private List<Descriptor> ReadTable(uint tableBase, ushort limit, bool localTable)
    {
        var result = new List<Descriptor>();

        // The limit is the offset of the last valid byte, so a table of one
        // descriptor has a limit of 7. Entry zero of the GDT is the null
        // descriptor and is never a segment, but it is worth showing: guests
        // sometimes hide data there, and a non-zero null descriptor is a sign
        // that something has scribbled on the table.
        for (int index = 0; (index + 1) * 8 - 1 <= limit && index < 8192; index++)
        {
            uint at = tableBase + (uint)(index * 8);
            uint low = ReadDword(at);
            uint high = ReadDword(at + 4);

            uint segmentBase = (low >> 16) | ((high & 0xFF) << 16) | (high & 0xFF000000);
            uint segmentLimit = (low & 0xFFFF) | (high & 0x000F0000);
            byte access = (byte)(high >> 8);
            byte flags = (byte)((high >> 16) & 0xF0);

            if ((flags & 0x80) != 0) segmentLimit = (segmentLimit << 12) | 0xFFF;

            ushort selector = (ushort)((index << 3) | (localTable ? 4 : 0));
            result.Add(new Descriptor(index, selector, segmentBase, segmentLimit, access, flags));
        }

        return result;
    }

    /// <summary>One entry of the interrupt descriptor table.</summary>
    public readonly record struct Gate(
        int Vector, ushort Selector, uint Offset, byte Access)
    {
        public bool Present => (Access & 0x80) != 0;
        public int Dpl => (Access >> 5) & 3;

        public string TypeName => (Access & 0x0F) switch
        {
            0x05 => "task gate",
            0x06 => "286 interrupt gate",
            0x07 => "286 trap gate",
            0x0E => "386 interrupt gate",
            0x0F => "386 trap gate",
            _ => $"type {Access & 0x0F:X}",
        };

        public override string ToString()
            => $"{Vector:X2}  {Selector:X4}:{Offset:X8}  dpl {Dpl}  "
             + $"{(Present ? "" : "not present, ")}{TypeName}";
    }

    /// <summary>
    /// The interrupt descriptor table. Unlike the real-mode vector table this
    /// is not a list of addresses: each entry says which ring may reach it
    /// with a software INT, and whether interrupts stay enabled inside it.
    /// </summary>
    public IReadOnlyList<Gate> ReadInterruptTable()
    {
        CpuState state = _machine.Cpu.State;
        var result = new List<Gate>();

        for (int vector = 0; (vector + 1) * 8 - 1 <= state.IdtLimit && vector < 256; vector++)
        {
            uint at = state.IdtBase + (uint)(vector * 8);
            uint low = ReadDword(at);
            uint high = ReadDword(at + 4);

            result.Add(new Gate(
                vector,
                (ushort)(low >> 16),
                (low & 0xFFFF) | (high & 0xFFFF0000),
                (byte)(high >> 8)));
        }

        return result;
    }

    /// <summary>
    /// The ring 0 stack pointers held in the current task state segment, which
    /// is where the processor switches to when an interrupt raises privilege.
    /// A wrong value here shows up as a guest that dies on its first fault.
    /// </summary>
    public readonly record struct TaskState(
        ushort Selector, uint Base, uint Limit, bool Is386,
        uint Esp0, ushort Ss0, uint Cr3, ushort IoMapBase);

    public TaskState? ReadTaskState()
    {
        CpuState state = _machine.Cpu.State;
        if ((state.TaskRegister & 0xFFFC) == 0) return null;

        IReadOnlyList<Descriptor> gdt = ReadGlobalTable();
        int index = state.TaskRegister >> 3;
        if (index >= gdt.Count) return null;

        Descriptor descriptor = gdt[index];
        bool is386 = (descriptor.Access & 0x0F) is 0x09 or 0x0B;

        // The 286 and 386 layouts put the ring 0 stack in different places:
        // sixteen-bit fields at offset 2 and 4, or thirty-two-bit ones at 4
        // and 8. Reading the wrong pair is how a guest ends up with a stack
        // pointer made of two unrelated halves.
        uint esp0 = is386 ? ReadDword(descriptor.Base + 4) : ReadWord(descriptor.Base + 2);
        ushort ss0 = is386 ? (ushort)ReadDword(descriptor.Base + 8) : ReadWord(descriptor.Base + 4);

        return new TaskState(
            state.TaskRegister, descriptor.Base, descriptor.Limit, is386,
            esp0, ss0,
            is386 ? ReadDword(descriptor.Base + 0x1C) : 0,
            is386 ? ReadWord(descriptor.Base + 0x66) : (ushort)0);
    }

    /// <summary>
    /// The ports the current task may not touch directly, as ranges.
    ///
    /// A 386 task state ends in a bitmap with one bit per port: set means an
    /// IN or OUT on that port faults instead of reaching the hardware, and
    /// every port beyond the bitmap's end faults too. It is how a monitor
    /// lets a program in ring 3 drive most of the machine itself while
    /// keeping the few devices it virtualises for its own -- Windows in
    /// enhanced mode traps the video registers this way and little else,
    /// which is why the list is short and why it is worth reading.
    ///
    /// Reported as trapped ranges rather than raw bits, because the question
    /// is always "which ports does the monitor catch", and a few ranges
    /// answer it where eight kilobytes of bits do not.
    /// </summary>
    public IReadOnlyList<(ushort First, ushort Last)> ReadTrappedPorts()
    {
        if (ReadTaskState() is not { Is386: true } task) return [];

        // A base inside the fixed fields points at nothing meaningful; a
        // base at or past the limit leaves no bitmap, and then every port
        // faults -- which Intel documents and which a guest can rely on to
        // trap everything cheaply.
        if (task.IoMapBase < 0x68) return [];
        long bitmapBytes = (long)task.Limit + 1 - task.IoMapBase;
        if (bitmapBytes <= 0) return [(0, 0xFFFF)];
        if (bitmapBytes > 8192) bitmapBytes = 8192;

        var ranges = new List<(ushort, ushort)>();
        int start = -1;

        for (int port = 0; port <= 0xFFFF; port++)
        {
            int byteIndex = port >> 3;
            bool trapped = byteIndex >= bitmapBytes
                || (_machine.Memory.ReadByte(task.Base + task.IoMapBase + (uint)byteIndex)
                    & (1 << (port & 7))) != 0;

            if (trapped && start < 0) start = port;
            if (!trapped && start >= 0)
            {
                ranges.Add(((ushort)start, (ushort)(port - 1)));
                start = -1;
            }
        }

        if (start >= 0) ranges.Add(((ushort)start, 0xFFFF));
        return ranges;
    }

    /// <summary>
    /// Translates a linear address through the guest's page tables, the way
    /// the processor would, and says where it stops when it cannot.
    /// </summary>
    public string DescribeTranslation(uint linear)
    {
        CpuState state = _machine.Cpu.State;
        if ((state.Cr0 & CpuState.Cr0Paging) == 0)
            return $"{linear:X8} -> {linear:X8} (paging off)";

        uint directoryEntry = ReadDword((state.Cr3 & 0xFFFFF000) + ((linear >> 22) << 2));
        if ((directoryEntry & 1) == 0)
            return $"{linear:X8} -> page directory entry not present ({directoryEntry:X8})";

        uint tableEntry = ReadDword((directoryEntry & 0xFFFFF000) + (((linear >> 12) & 0x3FF) << 2));
        if ((tableEntry & 1) == 0)
            return $"{linear:X8} -> page table entry not present ({tableEntry:X8})";

        uint physical = (tableEntry & 0xFFFFF000) | (linear & 0xFFF);
        string writable = (directoryEntry & tableEntry & 2) != 0 ? "writable" : "read-only";
        string user = (directoryEntry & tableEntry & 4) != 0 ? "user" : "supervisor";
        string dirty = (tableEntry & 0x40) != 0 ? ", dirty" : "";

        return $"{linear:X8} -> {physical:X8}  {writable}, {user}{dirty}";
    }

    private uint ReadDword(uint physical) => _machine.Memory.ReadDword(physical);
    private ushort ReadWord(uint physical) => _machine.Memory.ReadWord(physical);
}
