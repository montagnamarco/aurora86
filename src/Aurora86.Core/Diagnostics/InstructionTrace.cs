namespace Aurora86.Core.Diagnostics;

/// <summary>
/// A ring of the last few thousand instructions the processor executed.
///
/// When something goes wrong in a guest, the question is almost never "what
/// is it doing now" -- by the time you notice, it is spinning in an error
/// handler or halted. The question is what it was doing a moment before, and
/// that is gone unless somebody wrote it down. Every defect found in this
/// project during the Windows work was found by keeping a ring like this and
/// dumping it at the moment of the fault.
///
/// A ring rather than a log: the interesting window is always the most recent
/// one, and a file would be gigabytes within a second. Nothing is allocated
/// while recording -- the entries are a pre-sized array of structs -- because
/// a tracer that triggers garbage collection changes the timing of the very
/// thing it is watching.
/// </summary>
public sealed class InstructionTrace
{
    /// <summary>One instruction, as it was about to be executed.</summary>
    public readonly record struct Entry(
        long Sequence, ushort Cs, uint Eip, uint Eax, uint Ebx, uint Ecx, uint Edx,
        uint Esi, uint Edi, uint Ebp, ushort Ss, uint Esp, ushort Ds, ushort Es,
        uint Flags, byte Mode, ulong Bytes)
    {
        /// <summary>Real mode, protected mode, or a virtual 8086 task.</summary>
        public string ModeName => Mode switch { 2 => "v86", 1 => "pm", _ => "real" };

        public override string ToString()
        {
            var bytes = new System.Text.StringBuilder();
            for (int i = 0; i < 8; i++) bytes.Append($"{(byte)(Bytes >> (8 * i)):x2} ");

            return $"{Sequence,10}  {ModeName,-4} {Cs:X4}:{Eip:X8}  {bytes} "
                 + $"ax={Eax:X8} bx={Ebx:X8} cx={Ecx:X8} dx={Edx:X8} "
                 + $"si={Esi:X8} di={Edi:X8} bp={Ebp:X8} sp={Ss:X4}:{Esp:X8} "
                 + $"ds={Ds:X4} es={Es:X4} fl={Flags:X4}";
        }
    }

    private readonly Entry[] _entries;
    private int _next;
    private long _sequence;

    public InstructionTrace(int capacity = 4096)
    {
        if (capacity < 1) capacity = 1;
        _entries = new Entry[capacity];
    }

    /// <summary>Whether anything is being recorded. Off by default: it halves the speed.</summary>
    public bool Enabled { get; set; }

    public int Capacity => _entries.Length;

    /// <summary>How many instructions have been recorded since the trace was created.</summary>
    public long Recorded => _sequence;

    public void Record(in Entry entry)
    {
        _entries[_next] = entry with { Sequence = _sequence++ };
        _next = (_next + 1) % _entries.Length;
    }

    public void Clear()
    {
        Array.Clear(_entries);
        _next = 0;
    }

    /// <summary>
    /// The recorded instructions, oldest first. Entries never written are
    /// skipped, so a trace that has only just been switched on returns what
    /// little it has rather than a screenful of zeroes.
    /// </summary>
    public IEnumerable<Entry> Entries()
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            Entry entry = _entries[(_next + i) % _entries.Length];
            if (entry.Cs != 0 || entry.Eip != 0 || entry.Sequence != 0) yield return entry;
        }
    }

    /// <summary>The most recent <paramref name="count"/> instructions, oldest first.</summary>
    public IReadOnlyList<Entry> Last(int count)
    {
        List<Entry> all = Entries().ToList();
        return all.Count <= count ? all : all[^count..];
    }
}
