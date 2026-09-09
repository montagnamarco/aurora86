using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using Aurora386.Core;
using Aurora386.Core.Cpu;
using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Diagnostics;
using Aurora386.Gui.Emulation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aurora386.Gui.ViewModels;

/// <summary>
/// The debugger: registers, disassembly, memory and breakpoints.
///
/// Everything here reads the machine while it is paused. Stepping is the one
/// operation that runs the CPU, and it runs it for exactly one instruction,
/// so the state on screen always corresponds to the instruction about to be
/// executed rather than one that already went past.
/// </summary>
public sealed partial class DebuggerViewModel : ObservableObject
{
    private readonly EmulatorSession _session;

    /// <summary>
    /// Breakpoints as linear addresses. Linear rather than segment:offset
    /// because the same physical instruction can be reached through different
    /// segment values, and a breakpoint should catch all of them.
    /// </summary>
    private readonly HashSet<uint> _breakpoints = new();

    public ObservableCollection<DisassemblyLine> Disassembly { get; } = new();
    public ObservableCollection<RegisterEntry> Registers { get; } = new();
    public ObservableCollection<string> MemoryLines { get; } = new();
    public ObservableCollection<string> StackLines { get; } = new();
    public ObservableCollection<BreakpointEntry> Breakpoints { get; } = new();

    [ObservableProperty] private string _memoryAddressText = "0000:0000";
    [ObservableProperty] private string _memoryEditAddressText = "";
    [ObservableProperty] private string _memoryEditValueText = "";
    [ObservableProperty] private string _memorySearchText = "";

    /// <summary>
    /// Whether the memory view goes through the page tables. A linear address
    /// and a physical one are the same thing until paging is on; once it is,
    /// the difference is everything -- the guest's own pointers are linear,
    /// while the RAM the emulator holds is physical.
    /// </summary>
    [ObservableProperty] private bool _memoryFollowsPaging;

    // --- disk editor --------------------------------------------------------

    public ObservableCollection<string> DiskLines { get; } = new();
    public ObservableCollection<string> DiskStructure { get; } = new();

    [ObservableProperty] private string _diskSectorText = "0";
    [ObservableProperty] private string _diskEditOffsetText = "";
    [ObservableProperty] private string _diskEditValueText = "";
    [ObservableProperty] private string _diskStatusText = "";

    /// <summary>Which attached drive the disk view is looking at, 0 for the first.</summary>
    [ObservableProperty] private int _diskIndex;

    private byte[] _diskSector = new byte[512];
    private long _diskSectorNumber;

    // --- instruction trace ---------------------------------------------------

    public ObservableCollection<string> TraceLines { get; } = new();

    /// <summary>
    /// Whether the processor is recording what it executes. It is off by
    /// default and says so: recording costs about half the emulator's speed,
    /// which is a fair price while hunting something and an unfair one the
    /// rest of the time.
    /// </summary>
    public bool TraceEnabled
    {
        get => _traceEnabled;
        set
        {
            if (!SetProperty(ref _traceEnabled, value)) return;
            _session.WithMachine(machine =>
            {
                machine.Cpu.Trace ??= new InstructionTrace();
                machine.Cpu.Trace.Enabled = value;
            });
            RefreshTrace();
        }
    }

    private bool _traceEnabled;
    [ObservableProperty] private string _breakpointAddressText = "";
    [ObservableProperty] private string _flagsText = "";
    [ObservableProperty] private string _statusText = "Paused";

    public DebuggerViewModel(EmulatorSession session)
    {
        _session = session;
        Refresh();
    }

    /// <summary>Reads the whole visible state out of the machine in one pass.</summary>
    public void Refresh()
    {
        _session.WithMachine(machine =>
        {
            RefreshRegisters(machine);
            RefreshDisassembly(machine);
            RefreshStack(machine);
        });

        RefreshMemory();
        RefreshDisk();
        RefreshTrace();
    }

    private void RefreshRegisters(Machine machine)
    {
        CpuState state = machine.Cpu.State;

        Registers.Clear();
        void Add(string name, uint value, int digits = 8)
            => Registers.Add(new RegisterEntry(name, value.ToString($"X{digits}")));

        Add("EAX", state.Eax); Add("EBX", state.Ebx);
        Add("ECX", state.Ecx); Add("EDX", state.Edx);
        Add("ESI", state.Esi); Add("EDI", state.Edi);
        Add("EBP", state.Ebp); Add("ESP", state.Esp);
        Add("EIP", state.Eip);

        Add("CS", state.Cs.Selector, 4); Add("DS", state.Ds.Selector, 4);
        Add("ES", state.Es.Selector, 4); Add("SS", state.Ss.Selector, 4);
        Add("FS", state.Fs.Selector, 4); Add("GS", state.Gs.Selector, 4);

        Add("CR0", state.Cr0);
        Add("CR2", state.Cr2);
        Add("CR3", state.Cr3);

        // What mode the processor is in, which is the first thing to know
        // and the hardest to work out from the registers alone: it is spread
        // across a bit of CR0, a bit of EFLAGS, and the low two bits of CS.
        //
        // Getting it wrong sends an investigation off in the wrong direction
        // entirely -- an address means something different in each mode, and
        // a disassembly at the wrong width is not obviously wrong, it is just
        // different instructions.
        ModeText = DescribeMode(state, machine.Cpu.Flags);

        // The flag letters follow the convention every x86 debugger uses: an
        // upper-case letter when the flag is set, a dot when it is clear.
        LazyFlags f = machine.Cpu.Flags;
        FlagsText = string.Concat(
            f.Overflow ? "O" : ".", f.Direction ? "D" : ".",
            f.Interrupt ? "I" : ".", f.Sign ? "S" : ".",
            f.Zero ? "Z" : ".", f.Adjust ? "A" : ".",
            f.Parity ? "P" : ".", f.Carry ? "C" : ".");
    }

    /// <summary>What mode the processor is in, in one line.</summary>
    [ObservableProperty] private string _modeText = "";

    private static string DescribeMode(CpuState state, LazyFlags flags)
    {
        string mode = state.Virtual8086Mode ? "virtual 8086"
                    : state.ProtectedMode ? $"protected, ring {state.Cs.Selector & 3}"
                    : "real";

        // Paging is a property of protected mode, and saying so only when it
        // applies keeps the line from claiming something impossible: a real
        // mode machine with paging on is not a state that exists.
        if (state.ProtectedMode && state.PagingEnabled) mode += ", paged";

        // IOPL matters exactly when it is not 3, because at 3 nothing is
        // privileged and it explains nothing. Below that it is often the
        // whole reason an instruction faulted.
        var iopl = (int)(((uint)flags.Stored >> 12) & 3);
        string privilege = iopl < 3 ? $"  IOPL {iopl}" : "";

        return $"{mode}{privilege}   IF {(flags.Interrupt ? "on" : "off")}";
    }

    private void RefreshDisassembly(Machine machine)
    {
        Disassembly.Clear();

        CpuState state = machine.Cpu.State;
        uint segmentBase = state.Cs.Base;
        uint offset = state.Eip;

        // Sixteen instructions is about a screenful and cheap to decode. The
        // first one is highlighted: it is the one that will run next.
        for (int i = 0; i < 16; i++)
        {
            uint linear = segmentBase + offset;

            Core.Diagnostics.DecodedInstruction decoded;
            try
            {
                decoded = Core.Diagnostics.Disassembler.Decode(
                    address => machine.Memory.ReadByte(address),
                    linear,
                    bits32: state.Cs.Big);
            }
            catch
            {
                break;
            }

            // The first line is the one about to run, and only there are the
            // registers the ones the instruction will see. An annotation on
            // any other line would be describing a call with whatever
            // happens to be in AH now, which is worse than no annotation:
            // it looks like knowledge.
            string text = decoded.Text;
            if (i == 0 && Annotate(decoded.Text, state) is { } note)
                text += $"      ; {note}";
            if (i == 0 && RomRoutine(state) is { } routine)
                text += $"      ; {routine}";

            Disassembly.Add(new DisassemblyLine(
                $"{state.Cs.Selector:X4}:{offset:X4}",
                decoded.Bytes,
                text,
                IsCurrent: i == 0,
                HasBreakpoint: _breakpoints.Contains(linear)));

            offset += (uint)Math.Max(1, decoded.Length);
        }
    }

    /// <summary>
    /// What a software interrupt is about to ask for, when it can be worked
    /// out from the registers.
    ///
    /// "int 21" says nothing; "int 21 ; open file" says what the guest is
    /// doing. Reading a trace without this means looking up AH by hand for
    /// every line, which is exactly the sort of work that stops an
    /// investigation being worth starting.
    ///
    /// Only the calls worth naming are here. A table of all 21h would be
    /// mostly noise -- what matters is recognising the handful a guest is
    /// doing over and over when something is wrong.
    /// </summary>
    private static string? Annotate(string text, CpuState state)
    {
        if (!text.StartsWith("int ")) return null;
        if (!byte.TryParse(text[4..].Trim(), System.Globalization.NumberStyles.HexNumber,
                           null, out byte vector))
            return null;

        var ah = (byte)(state.Eax >> 8);
        var ax = (ushort)state.Eax;

        return vector switch
        {
            0x10 => ax switch
            {
                0x4F00 => "VESA: card information",
                0x4F01 => "VESA: mode information",
                0x4F02 => $"VESA: set mode {state.Bx & 0x1FF:X3}",
                0x4F05 => "VESA: window control",
                0x4F06 => "VESA: scan line length",
                0x4F07 => "VESA: display start",
                _ => ah switch
                {
                    0x00 => $"video: set mode {(byte)state.Eax:X2}",
                    0x0E => "video: write character",
                    0x13 => "video: write string",
                    _ => "video",
                },
            },

            0x13 => ah switch
            {
                0x02 => "disk: read sectors",
                0x03 => "disk: write sectors",
                0x08 => "disk: drive parameters",
                _ => "disk",
            },

            0x21 => ah switch
            {
                0x09 => "DOS: print string",
                0x25 => $"DOS: set vector {(byte)state.Eax:X2}",
                0x30 => "DOS: version",
                0x35 => $"DOS: get vector {(byte)state.Eax:X2}",
                0x3C => "DOS: create file",
                0x3D => "DOS: open file",
                0x3E => "DOS: close file",
                0x3F => "DOS: read file",
                0x40 => "DOS: write file",
                0x41 => "DOS: delete file",
                0x42 => "DOS: seek",
                0x43 => "DOS: file attributes",
                0x44 => "DOS: device control",
                0x47 => "DOS: current directory",
                0x48 => "DOS: allocate memory",
                0x4B => "DOS: run program",
                0x4C => "DOS: exit",
                0x4E => "DOS: find first",
                0x4F => "DOS: find next",
                0x52 => "DOS: list of lists",
                _ => $"DOS: function {ah:X2}",
            },

            0x2F => ax switch
            {
                0x1123 => "redirector: qualify name",
                0x111B => "redirector: find first",
                0x111C => "redirector: find next",
                0x1600 => "Windows: installation check",
                0x1689 => "Windows: about to idle",
                _ => (ax >> 8) == 0x11 ? $"redirector: {(byte)ax:X2}" : "multiplex",
            },

            0x31 => ax switch
            {
                0x0500 => "DPMI: free memory information",
                0x0501 => "DPMI: allocate memory",
                0x0703 => "DPMI: discardable pages",
                _ => $"DPMI: {ax:X4}",
            },

            0x28 => "DOS: idle",
            0x33 => "mouse",
            _ => null,
        };
    }

    /// <summary>
    /// The ROM's entry points by offset, read off the constants themselves
    /// so that this list cannot drift from the ROM it describes. Two entries
    /// that share an offset are shown together.
    /// </summary>
    private static readonly (ushort Offset, string Name)[] RomEntries =
        typeof(Core.Firmware.BiosRom.Entry)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(ushort))
            .Select(f => (Offset: (ushort)f.GetRawConstantValue()!, f.Name))
            .GroupBy(e => e.Offset)
            .Select(g => (g.Key, string.Join("/", g.Select(e => e.Name).OrderBy(n => n))))
            .OrderBy(e => e.Key)
            .ToArray();

    /// <summary>
    /// Which ROM routine the processor is in, when it is in the BIOS at
    /// all: the nearest entry point at or before EIP, with the distance
    /// past it.
    ///
    /// A stop inside the ROM is otherwise the least informative place to
    /// stop -- F000:10B3 says nothing, "KeyboardIrq+3" says the machine is
    /// three bytes into the keyboard interrupt, which is usually the whole
    /// answer to why it is there.
    /// </summary>
    private static string? RomRoutine(CpuState state)
    {
        if (state.Cs.Base != 0xF0000) return null;

        uint offset = state.Eip;
        ushort at = 0;
        string? name = null;

        foreach ((ushort start, string entry) in RomEntries)
        {
            if (start > offset) break;
            (at, name) = (start, entry);
        }

        if (name is null) return null;
        return offset == at ? $"ROM {name}" : $"ROM {name}+{offset - at:X}";
    }

    private void RefreshStack(Machine machine)
    {
        StackLines.Clear();

        CpuState state = machine.Cpu.State;
        uint stackBase = state.Ss.Base;
        uint pointer = state.Ss.Big ? state.Esp : (ushort)state.Esp;

        for (int i = 0; i < 12; i++)
        {
            uint address = stackBase + pointer + (uint)(i * 2);
            ushort value = machine.Memory.ReadWord(address);
            StackLines.Add($"{state.Ss.Selector:X4}:{pointer + i * 2:X4}  {value:X4}");
        }
    }

    /// <summary>
    /// What the memory view showed last time, so that the next refresh can
    /// say what changed. Kept only while the view stays on one address:
    /// moving elsewhere and back is a new view, not a change.
    /// </summary>
    private uint _viewedAddress = uint.MaxValue;
    private readonly byte[] _viewedBytes = new byte[256];

    private void RefreshMemory()
    {
        MemoryLines.Clear();

        if (!TryParseAddress(MemoryAddressText, out uint linear)) return;

        bool sameView = linear == _viewedAddress;
        var current = new byte[256];

        _session.WithMachine(machine =>
        {
            for (int i = 0; i < current.Length; i++)
                current[i] = ReadForView(machine, linear + (uint)i);
        });

        // Sixteen bytes per line, hex on the left and printable characters
        // on the right: the layout every hex editor has used since DEBUG.
        //
        // A byte that changed since the last look is marked with a star in
        // place of the space after it. The mark lives in the separator so
        // that nothing moves: a column of hex that shifts when something
        // changes is unreadable at exactly the moment it matters.
        for (int row = 0; row < 16; row++)
        {
            uint rowAddress = linear + (uint)(row * 16);
            var hex = new System.Text.StringBuilder();
            var text = new System.Text.StringBuilder();

            for (int column = 0; column < 16; column++)
            {
                int i = row * 16 + column;
                byte value = current[i];
                bool changed = sameView && value != _viewedBytes[i];

                hex.Append(value.ToString("X2")).Append(changed ? '*' : ' ');
                text.Append(value >= 32 && value < 127 ? (char)value : '.');
            }

            MemoryLines.Add($"{rowAddress:X6}  {hex} {text}");
        }

        current.CopyTo(_viewedBytes, 0);
        _viewedAddress = linear;
    }

    /// <summary>
    /// Shows the last few hundred instructions, oldest first, so that reading
    /// downwards follows the guest forwards.
    /// </summary>
    [RelayCommand]
    public void RefreshTrace()
    {
        TraceLines.Clear();

        InstructionTrace? trace = _session.WithMachine(machine => machine.Cpu.Trace);
        if (trace is null || !trace.Enabled)
        {
            TraceLines.Add("not recording -- tick the box to start");
            return;
        }

        foreach (InstructionTrace.Entry entry in trace.Last(400))
            TraceLines.Add(entry.ToString());

        if (TraceLines.Count == 0) TraceLines.Add("nothing recorded yet");
    }

    [RelayCommand]
    private void ClearTrace()
    {
        _session.WithMachine(machine => machine.Cpu.Trace?.Clear());
        RefreshTrace();
    }

    /// <summary>Writes the trace to a file next to the emulator, for a long look.</summary>
    [RelayCommand]
    private void SaveTrace()
    {
        InstructionTrace? trace = _session.WithMachine(machine => machine.Cpu.Trace);
        if (trace is null) { StatusText = "Nothing to save"; return; }

        string path = Path.Combine(Path.GetTempPath(),
            $"aurora-trace-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllLines(path, trace.Entries().Select(e => e.ToString()));
        StatusText = $"Trace written to {path}";
    }

    /// <summary>
    /// Reads the sector the disk view is showing, and works out what it is:
    /// a partition table, a boot sector, a FAT, the root directory, or a
    /// cluster of file data. Seeing that label next to the bytes is most of
    /// the value -- a sector of a FAT and a sector of a file look identical
    /// until something tells you which is which.
    /// </summary>
    public void RefreshDisk()
    {
        DiskLines.Clear();
        DiskStructure.Clear();

        DiskInspector? inspector = OpenInspector();
        if (inspector is null)
        {
            DiskStructure.Add("no disk attached");
            return;
        }

        if (!long.TryParse(DiskSectorText.Trim(), out long sector)) sector = 0;
        _diskSectorNumber = Math.Clamp(sector, 0, Math.Max(0, inspector.TotalSectors - 1));
        _diskSector = inspector.ReadSector(_diskSectorNumber);

        (int cylinder, int head, int number) = inspector.ToChs(_diskSectorNumber);
        DiskStructure.Add($"sector {_diskSectorNumber} of {inspector.TotalSectors}"
                        + $"  chs {cylinder}/{head}/{number}"
                        + (inspector.ReadOnly ? "  (read-only)" : ""));
        DiskStructure.Add(inspector.DescribeSector(_diskSectorNumber));

        foreach (DiskInspector.Partition partition in inspector.ReadPartitions())
        {
            DiskStructure.Add($"partition {partition.Index + 1}: {partition.TypeName}"
                            + $"  start {partition.FirstSector}  size {partition.SectorCount}"
                            + (partition.Bootable ? "  bootable" : ""));

            if (inspector.ReadBootParameters(partition.FirstSector) is not { } bpb) continue;

            DiskStructure.Add($"   FAT{bpb.FatBits}  cluster {bpb.SectorsPerCluster} sectors"
                            + $"  fats {bpb.FatCount}x{bpb.SectorsPerFat}"
                            + $"  root {bpb.RootEntries} entries"
                            + $"  label {bpb.Label.Trim()}");
            DiskStructure.Add($"   fat at +{bpb.FirstFatSector}  root at +{bpb.RootDirectorySector}"
                            + $"  data at +{bpb.FirstDataSector}  clusters {bpb.ClusterCount}");
        }

        for (int row = 0; row < _diskSector.Length; row += 16)
        {
            var hex = new System.Text.StringBuilder();
            var text = new System.Text.StringBuilder();
            for (int column = 0; column < 16; column++)
            {
                byte value = _diskSector[row + column];
                hex.Append(value.ToString("X2")).Append(' ');
                text.Append(value >= 32 && value < 127 ? (char)value : '.');
            }
            DiskLines.Add($"{row:X3}  {hex} {text}");
        }
    }

    /// <summary>An inspector over the drive the view is pointed at, or null.</summary>
    private DiskInspector? OpenInspector() => _session.WithMachine(machine =>
    {
        List<DiskImage> drives = machine.DiskServices.AttachedDrives.ToList();
        if (DiskIndex < 0 || DiskIndex >= drives.Count) return null;
        return new DiskInspector(drives[DiskIndex]);
    });

    [RelayCommand]
    private void GoToSector()
    {
        RefreshDisk();
        DiskStatusText = $"Sector {_diskSectorNumber}";
    }

    [RelayCommand]
    private void NextSector()
    {
        DiskSectorText = (_diskSectorNumber + 1).ToString();
        RefreshDisk();
    }

    [RelayCommand]
    private void PreviousSector()
    {
        DiskSectorText = Math.Max(0, _diskSectorNumber - 1).ToString();
        RefreshDisk();
    }

    /// <summary>
    /// Patches bytes into the sector on screen and writes the whole sector
    /// back. A disk has no smaller unit than a sector: there is no way to
    /// change one byte on the medium, and pretending otherwise would hide
    /// the read-modify-write that is really happening.
    /// </summary>
    [RelayCommand]
    private void WriteDisk()
    {
        DiskInspector? inspector = OpenInspector();
        if (inspector is null) { DiskStatusText = "No disk"; return; }
        if (inspector.ReadOnly) { DiskStatusText = "Disk is read-only"; return; }

        if (!int.TryParse(DiskEditOffsetText.Trim(), NumberStyles.HexNumber, null, out int offset)
            || offset < 0 || offset >= _diskSector.Length)
        {
            DiskStatusText = "Offset not understood";
            return;
        }

        if (!TryParseBytes(DiskEditValueText, out byte[] bytes) || bytes.Length == 0)
        {
            DiskStatusText = "Bytes not understood";
            return;
        }

        if (offset + bytes.Length > _diskSector.Length)
        {
            DiskStatusText = "That would run past the end of the sector";
            return;
        }

        bytes.CopyTo(_diskSector, offset);
        if (!inspector.WriteSector(_diskSectorNumber, _diskSector))
        {
            DiskStatusText = "Write refused";
            return;
        }

        RefreshDisk();
        DiskStatusText = $"Wrote {bytes.Length} byte(s) at +{offset:X3}";
    }

    /// <summary>
    /// Reads one byte the way the memory view is configured: straight out of
    /// physical memory, or through the guest's page tables when the view is
    /// following them. A page that is not present reads as 0xFF, which is
    /// what the bus gives for nothing at all.
    /// </summary>
    private byte ReadForView(Machine machine, uint address)
    {
        if (!MemoryFollowsPaging) return machine.Memory.ReadByte(address);

        uint physical = TranslateForView(machine, address);
        return physical == uint.MaxValue ? (byte)0xFF : machine.Memory.ReadByte(physical);
    }

    /// <summary>
    /// Walks the page directory by hand rather than asking the CPU, because
    /// looking at memory in a debugger must never fault the guest, set an
    /// accessed bit, or disturb the TLB.
    /// </summary>
    private static uint TranslateForView(Machine machine, uint linear)
    {
        CpuState state = machine.Cpu.State;
        if ((state.Cr0 & CpuState.Cr0Paging) == 0) return linear;

        uint directory = machine.Memory.ReadDword((state.Cr3 & 0xFFFFF000) + ((linear >> 22) << 2));
        if ((directory & 1) == 0) return uint.MaxValue;

        uint table = machine.Memory.ReadDword((directory & 0xFFFFF000) + (((linear >> 12) & 0x3FF) << 2));
        if ((table & 1) == 0) return uint.MaxValue;

        return (table & 0xFFFFF000) | (linear & 0xFFF);
    }

    /// <summary>
    /// Writes bytes into guest memory from the editor: an address and a run
    /// of hex bytes ("B8 00 4C" or "B8004C"), or a quoted string.
    ///
    /// This is a debugger, so the write is deliberately unchecked -- no
    /// segment limit, no privilege, no page fault. Patching a byte in the
    /// middle of a running guest is exactly what the feature is for.
    /// </summary>
    [RelayCommand]
    private void WriteMemory()
    {
        if (!TryParseAddress(MemoryEditAddressText, out uint address))
        {
            StatusText = "Address not understood";
            return;
        }

        if (!TryParseBytes(MemoryEditValueText, out byte[] bytes) || bytes.Length == 0)
        {
            StatusText = "Bytes not understood";
            return;
        }

        _session.WithMachine(machine =>
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                uint target = address + (uint)i;
                if (MemoryFollowsPaging)
                {
                    target = TranslateForView(machine, target);
                    if (target == uint.MaxValue) continue;
                }
                machine.Memory.WriteByte(target, bytes[i]);
            }
        });

        MemoryAddressText = MemoryEditAddressText;
        Refresh();
        StatusText = $"Wrote {bytes.Length} byte(s) at {address:X6}";
    }

    /// <summary>
    /// Finds the next occurrence of a byte pattern or string, starting just
    /// after the address the view is showing, and moves the view there.
    /// </summary>
    [RelayCommand]
    private void SearchMemory()
    {
        if (!TryParseBytes(MemorySearchText, out byte[] pattern) || pattern.Length == 0)
        {
            StatusText = "Nothing to search for";
            return;
        }

        TryParseAddress(MemoryAddressText, out uint start);

        uint found = _session.WithMachine(machine =>
        {
            uint limit = machine.Memory.RamSize;
            for (uint address = start + 1; address + (uint)pattern.Length <= limit; address++)
            {
                int i = 0;
                while (i < pattern.Length && ReadForView(machine, address + (uint)i) == pattern[i]) i++;
                if (i == pattern.Length) return address;
            }
            return uint.MaxValue;
        });

        if (found == uint.MaxValue)
        {
            StatusText = "Not found";
            return;
        }

        MemoryAddressText = found.ToString("X6");
        Refresh();
        StatusText = $"Found at {found:X6}";
    }

    /// <summary>
    /// Hex bytes with or without separators, or "a quoted string" taken as
    /// its characters. Both forms turn up when hunting through a guest: one
    /// for opcodes, the other for messages.
    /// </summary>
    private static bool TryParseBytes(string text, out byte[] bytes)
    {
        bytes = [];
        text = text.Trim();
        if (text.Length == 0) return false;

        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            bytes = System.Text.Encoding.ASCII.GetBytes(text[1..^1]);
            return true;
        }

        string digits = text.Replace(" ", "").Replace("-", "");
        if (digits.Length % 2 != 0) return false;

        var result = new byte[digits.Length / 2];
        for (int i = 0; i < result.Length; i++)
            if (!byte.TryParse(digits.AsSpan(i * 2, 2), NumberStyles.HexNumber, null, out result[i]))
                return false;

        bytes = result;
        return true;
    }

    /// <summary>
    /// Accepts either a linear address ("100000") or the segment:offset form
    /// ("0040:001E") that DOS-era documentation always uses.
    /// </summary>
    private static bool TryParseAddress(string text, out uint linear)
    {
        linear = 0;
        text = text.Trim();
        if (text.Length == 0) return false;

        int colon = text.IndexOf(':');
        if (colon < 0)
            return uint.TryParse(text, NumberStyles.HexNumber, null, out linear);

        if (!ushort.TryParse(text[..colon], NumberStyles.HexNumber, null, out ushort segment)
            || !ushort.TryParse(text[(colon + 1)..], NumberStyles.HexNumber, null, out ushort offset))
            return false;

        linear = (uint)(segment << 4) + offset;
        return true;
    }

    // --- commands -----------------------------------------------------------

    [RelayCommand]
    private void StepInstruction()
    {
        _session.Pause();
        _session.StepOne();
        Refresh();
        StatusText = "Stepped";
    }

    /// <summary>
    /// Runs until the instruction after the current one is reached, so a CALL
    /// executes the whole subroutine instead of descending into it. The step
    /// budget stops runaway code from hanging the debugger.
    /// </summary>
    [RelayCommand]
    private void StepOver()
    {
        _session.Pause();

        uint target = _session.WithMachine(machine =>
        {
            CpuState state = machine.Cpu.State;
            Core.Diagnostics.DecodedInstruction decoded = Core.Diagnostics.Disassembler.Decode(
                address => machine.Memory.ReadByte(address),
                state.Cs.Base + state.Eip,
                bits32: state.Cs.Big);

            return state.Eip + (uint)Math.Max(1, decoded.Length);
        });

        RunUntil(machine => machine.Cpu.State.Eip == target, budget: 50_000_000);
        Refresh();
        StatusText = "Stepped over";
    }

    /// <summary>
    /// Runs until the current routine returns to whoever called it.
    ///
    /// There is no need to find the return address. A routine returns when
    /// its RET pops that address, and that is the moment the stack pointer
    /// rises above where it stood on entry: everything a routine pushes it
    /// pops again before returning, so nothing else takes the pointer past
    /// that line. An interrupt arriving meanwhile pushes and pops on the same
    /// stack and lands back exactly at the line, never above it.
    ///
    /// This is what makes it work for code that never kept a frame pointer,
    /// which is all of DOS and most of Windows -- a debugger that walked BP
    /// chains would have nothing to walk.
    /// </summary>
    [RelayCommand]
    private void StepOut()
    {
        _session.Pause();

        (ushort ss, uint esp) = _session.WithMachine(machine =>
        {
            CpuState state = machine.Cpu.State;
            return (state.Ss.Selector, state.Ss.Big ? state.Esp : (ushort)state.Esp);
        });

        RunUntil(machine =>
        {
            CpuState state = machine.Cpu.State;
            if (state.Ss.Selector != ss) return false;     // another stack: a task switch, or a ring change
            uint now = state.Ss.Big ? state.Esp : (ushort)state.Esp;
            return now > esp;
        }, budget: 200_000_000);

        Refresh();
        StatusText = "Returned to caller";
    }

    /// <summary>
    /// A condition a breakpoint must also satisfy, such as "AH=4B" or
    /// "CPL=3, MODE=V86". With no address breakpoints set, the condition
    /// alone is the breakpoint: "stop wherever AH becomes 4B".
    /// </summary>
    [ObservableProperty] private string _breakConditionText = "";

    /// <summary>
    /// Turns "AH=4B", "CPL=3" or "MODE=V86" into a test on the processor,
    /// several clauses separated by commas all having to hold.
    ///
    /// Values are hexadecimal without a prefix, because that is how every
    /// number in this window is written and a condition that read them
    /// differently would be a trap. The registers are the ones worth asking
    /// about: an address breakpoint says where, and the usual question on
    /// top of it is "but only when the call is this one".
    /// </summary>
    private static Func<CpuState, bool>? ParseCondition(string text)
    {
        var clauses = new List<Func<CpuState, bool>>();

        foreach (string raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = raw.IndexOf('=');
            if (eq < 0) return null;

            string name = raw[..eq].Trim().ToUpperInvariant();
            string valueText = raw[(eq + 1)..].Trim().ToUpperInvariant();

            if (name == "MODE")
            {
                Func<CpuState, bool>? mode = valueText switch
                {
                    "REAL" => s => !s.ProtectedMode,
                    "PROT" or "PROTECTED" => s => s.ProtectedMode && !s.Virtual8086Mode,
                    "V86" => s => s.Virtual8086Mode,
                    _ => null,
                };
                if (mode is null) return null;
                clauses.Add(mode);
                continue;
            }

            if (!uint.TryParse(valueText, System.Globalization.NumberStyles.HexNumber, null, out uint value))
                return null;

            Func<CpuState, uint>? read = name switch
            {
                "EAX" => s => s.Eax, "EBX" => s => s.Ebx, "ECX" => s => s.Ecx, "EDX" => s => s.Edx,
                "ESI" => s => s.Esi, "EDI" => s => s.Edi, "EBP" => s => s.Ebp, "ESP" => s => s.Esp,
                "EIP" => s => s.Eip,
                "AX" => s => s.Eax & 0xFFFF, "BX" => s => s.Ebx & 0xFFFF,
                "CX" => s => s.Ecx & 0xFFFF, "DX" => s => s.Edx & 0xFFFF,
                "AH" => s => (s.Eax >> 8) & 0xFF, "AL" => s => s.Eax & 0xFF,
                "BH" => s => (s.Ebx >> 8) & 0xFF, "BL" => s => s.Ebx & 0xFF,
                "CH" => s => (s.Ecx >> 8) & 0xFF, "CL" => s => s.Ecx & 0xFF,
                "DH" => s => (s.Edx >> 8) & 0xFF, "DL" => s => s.Edx & 0xFF,
                "CS" => s => s.Cs.Selector, "DS" => s => s.Ds.Selector,
                "ES" => s => s.Es.Selector, "SS" => s => s.Ss.Selector,
                "CPL" => s => (uint)(s.Cs.Selector & 3),
                _ => null,
            };
            if (read is null) return null;

            clauses.Add(s => read(s) == value);
        }

        return clauses.Count == 0 ? null : s => clauses.All(c => c(s));
    }

    [RelayCommand]
    private void RunToBreakpoint()
    {
        Func<CpuState, bool>? condition = ParseCondition(BreakConditionText);
        if (BreakConditionText.Trim().Length > 0 && condition is null)
        {
            StatusText = "Condition not understood -- try AH=4B, CPL=3, MODE=V86";
            return;
        }

        if (_breakpoints.Count == 0 && condition is null)
        {
            _session.Resume();
            StatusText = "Running (no breakpoints set)";
            return;
        }

        RunUntil(machine =>
        {
            CpuState state = machine.Cpu.State;

            // With no addresses, the condition is the whole breakpoint.
            bool atAddress = _breakpoints.Count == 0
                          || _breakpoints.Contains(state.Cs.Base + state.Eip);
            return atAddress && (condition is null || condition(state));
        }, budget: 200_000_000);

        Refresh();
        StatusText = condition is null ? "Stopped at breakpoint" : "Stopped: condition met";
    }

    /// <summary>
    /// Single-steps until a condition holds or the budget runs out. Stepping
    /// one instruction at a time is slow, but it is the only way to test a
    /// condition between every instruction.
    /// </summary>
    private void RunUntil(Func<Machine, bool> condition, long budget)
    {
        _session.WithMachine(machine =>
        {
            for (long i = 0; i < budget; i++)
            {
                machine.RunInstructions(1);
                if (condition(machine)) return;
            }
        });
    }

    [RelayCommand]
    private void AddBreakpoint()
    {
        if (!TryParseAddress(BreakpointAddressText, out uint linear)) return;
        if (!_breakpoints.Add(linear)) return;

        Breakpoints.Add(new BreakpointEntry(linear, BreakpointAddressText.Trim()));
        BreakpointAddressText = "";
        Refresh();
    }

    [RelayCommand]
    private void RemoveBreakpoint(BreakpointEntry? entry)
    {
        if (entry is null) return;

        _breakpoints.Remove(entry.LinearAddress);
        Breakpoints.Remove(entry);
        Refresh();
    }

    [RelayCommand]
    private void GoToMemoryAddress() => RefreshMemory();

    // --- system tables -------------------------------------------------------

    public ObservableCollection<string> TableLines { get; } = new();

    [ObservableProperty] private string _translateAddressText = "";

    /// <summary>
    /// Shows the tables that define protected mode: where they are, what the
    /// descriptors say, and the ring 0 stack the processor will switch to on
    /// the next fault. In real mode it says so and stops, because none of it
    /// means anything yet.
    /// </summary>
    [RelayCommand]
    public void RefreshTables()
    {
        TableLines.Clear();

        _session.WithMachine(machine =>
        {
            CpuState state = machine.Cpu.State;
            var tables = new DescriptorTables(machine);

            TableLines.Add($"CR0 {state.Cr0:X8}   CR3 {state.Cr3:X8}   "
                         + (state.ProtectedMode ? "protected" : "real")
                         + (state.Virtual8086Mode ? ", virtual 8086" : "")
                         + ((state.Cr0 & CpuState.Cr0Paging) != 0 ? ", paging" : ""));

            if (!state.ProtectedMode)
            {
                TableLines.Add("");
                TableLines.Add("real mode: the vector table at 0000:0000 is all there is");
                return;
            }

            TableLines.Add($"GDT {state.GdtBase:X8} limit {state.GdtLimit:X4}   "
                         + $"IDT {state.IdtBase:X8} limit {state.IdtLimit:X4}   "
                         + $"LDTR {state.Ldtr:X4}   TR {state.TaskRegister:X4}");

            if (tables.ReadTaskState() is { } task)
            {
                TableLines.Add($"TSS {task.Selector:X4} at {task.Base:X8}  "
                             + $"{(task.Is386 ? "386" : "286")}  "
                             + $"ring 0 stack {task.Ss0:X4}:{task.Esp0:X8}"
                             + (task.Is386 && task.IoMapBase != 0 ? $"  I/O map at +{task.IoMapBase:X}" : ""));

                // The ports the monitor intercepts. Usually a short list: a
                // monitor picks the devices it must virtualise and leaves
                // the rest to the program, because every trapped port costs
                // a fault and a round trip through ring 0.
                IReadOnlyList<(ushort First, ushort Last)> trapped = tables.ReadTrappedPorts();
                if (trapped.Count > 0)
                {
                    static string Show((ushort First, ushort Last) r)
                        => r.First == r.Last ? $"{r.First:X3}" : $"{r.First:X3}-{r.Last:X3}";

                    string list = trapped.Count > 24
                        ? string.Join(" ", trapped.Take(24).Select(Show)) + $"  ... {trapped.Count} ranges in all"
                        : string.Join(" ", trapped.Select(Show));
                    TableLines.Add($"  ports trapped by the I/O bitmap: {list}");
                }
            }

            TableLines.Add("");
            TableLines.Add("GDT");
            foreach (DescriptorTables.Descriptor entry in tables.ReadGlobalTable())
                if (entry.Selector != 0 || entry.Access != 0)
                    TableLines.Add("  " + entry);

            IReadOnlyList<DescriptorTables.Descriptor> ldt = tables.ReadLocalTable();
            if (ldt.Count > 0)
            {
                TableLines.Add("");
                TableLines.Add("LDT");
                foreach (DescriptorTables.Descriptor entry in ldt)
                    if (entry.Access != 0) TableLines.Add("  " + entry);
            }

            TableLines.Add("");
            TableLines.Add("IDT (present gates only)");
            foreach (DescriptorTables.Gate gate in tables.ReadInterruptTable())
                if (gate.Present) TableLines.Add("  " + gate);
        });
    }

    /// <summary>Walks the page tables for one address, without disturbing them.</summary>
    [RelayCommand]
    private void TranslateAddress()
    {
        if (!TryParseAddress(TranslateAddressText, out uint linear))
        {
            StatusText = "Address not understood";
            return;
        }

        StatusText = _session.WithMachine(machine =>
            new DescriptorTables(machine).DescribeTranslation(linear));
    }

    // --- watchpoints ---------------------------------------------------------

    public ObservableCollection<WatchpointEntry> Watchpoints { get; } = new();

    [ObservableProperty] private string _watchAddressText = "";
    [ObservableProperty] private string _watchLengthText = "1";

    /// <summary>The most recent writes seen by a watchpoint, newest last.</summary>
    public ObservableCollection<string> WatchLog { get; } = new();

    private readonly List<WatchpointEntry> _watches = new();
    private readonly List<WatchpointEntry> _readWatches = new();
    private bool _watchHooked;

    /// <summary>
    /// Whether the next watchpoint reports reads rather than writes. "Who
    /// consults this table" is a different question from "who overwrote
    /// it", and a driver that walks away often does so because of
    /// something it read.
    /// </summary>
    [ObservableProperty] private bool _watchReads;

    /// <summary>
    /// Watches a range of physical memory for writes.
    ///
    /// A breakpoint says where the processor is; a watchpoint says who
    /// touched something, which is a different and often better question --
    /// "what overwrote this vector" is answerable in seconds with one, and
    /// only by reading a lot of code without.
    ///
    /// The address is physical, deliberately: the guest's own idea of an
    /// address changes with every segment load and every page table, while
    /// the byte in RAM stays where it is.
    /// </summary>
    [RelayCommand]
    private void AddWatchpoint()
    {
        if (!TryParseAddress(WatchAddressText, out uint address))
        {
            StatusText = "Address not understood";
            return;
        }

        if (!uint.TryParse(WatchLengthText.Trim(), out uint length) || length == 0) length = 1;

        var entry = new WatchpointEntry(address, length,
            WatchReads ? $"{address:X6}+{length} (reads)" : $"{address:X6}+{length}");
        (WatchReads ? _readWatches : _watches).Add(entry);
        Watchpoints.Add(entry);
        ApplyWatchpoints();

        WatchAddressText = "";
        StatusText = $"Watching {length} byte(s) at {address:X6} for {(WatchReads ? "reads" : "writes")}";
    }

    [RelayCommand]
    private void RemoveWatchpoint(WatchpointEntry? entry)
    {
        if (entry is null) return;

        _watches.Remove(entry);
        _readWatches.Remove(entry);
        Watchpoints.Remove(entry);
        ApplyWatchpoints();
    }

    [RelayCommand]
    private void ClearWatchLog() => WatchLog.Clear();

    private void ApplyWatchpoints() => _session.WithMachine(machine =>
    {
        machine.Memory.SetWatchRanges(_watches.Select(w => (w.Address, w.Length)));
        machine.Memory.SetReadWatchRanges(_readWatches.Select(w => (w.Address, w.Length)));

        // Hooked once and left: the handlers do nothing while the lists are
        // empty, and subscribing twice would log every access twice.
        if (_watchHooked) return;
        _watchHooked = true;
        machine.Memory.Watched += OnWatchedWrite;
        machine.Memory.ReadWatched += OnWatchedRead;
    });

    /// <summary>
    /// Records a watched write. This runs on the emulation thread, inside the
    /// write itself, so it does the least possible: format a line and keep the
    /// last few hundred. Anything heavier here changes the timing of the guest
    /// being watched.
    /// </summary>
    private void OnWatchedWrite(uint address, byte value)
    {
        if (_watches.Count == 0) return;

        string where = _session.WithMachine(machine =>
        {
            CpuState state = machine.Cpu.State;
            return $"{state.Cs.Selector:X4}:{state.Eip:X8}";
        });

        WatchLog.Add($"{address:X6} <- {value:X2}   from {where}");
        while (WatchLog.Count > 400) WatchLog.RemoveAt(0);
    }

    private void OnWatchedRead(uint address)
    {
        if (_readWatches.Count == 0) return;

        string where = _session.WithMachine(machine =>
        {
            CpuState state = machine.Cpu.State;
            return $"{state.Cs.Selector:X4}:{state.Eip:X8}";
        });

        WatchLog.Add($"{address:X6} -> read    from {where}");
        while (WatchLog.Count > 400) WatchLog.RemoveAt(0);
    }

    // --- port watchpoints ------------------------------------------------------

    [ObservableProperty] private string _portWatchText = "";

    /// <summary>Ports being watched, as a first and last pair.</summary>
    private readonly List<(ushort First, ushort Last)> _portWatches = new();

    public ObservableCollection<string> PortWatches { get; } = new();

    /// <summary>The most recent port accesses seen, newest last.</summary>
    public ObservableCollection<string> PortLog { get; } = new();

    private bool _portHooked;

    /// <summary>
    /// Watches an I/O port, or a range of them written "3C0-3CF".
    ///
    /// This answers a question no memory watchpoint can: a device is driven
    /// by writes that leave nothing behind to inspect afterwards. What order
    /// a driver programmed the registers in, what it read back before
    /// deciding the card was not one it knew -- those exist only as traffic,
    /// and are gone the moment they happen.
    ///
    /// Reads are recorded as well as writes, which is the difference from the
    /// memory watchpoints. A write says what a driver decided; a read is what
    /// it decided it on, and a driver that walks away usually does so because
    /// of something it read.
    /// </summary>
    [RelayCommand]
    private void AddPortWatch()
    {
        string text = PortWatchText.Trim();
        if (text.Length == 0) return;

        int dash = text.IndexOf('-');
        string firstText = dash < 0 ? text : text[..dash];
        string lastText = dash < 0 ? text : text[(dash + 1)..];

        if (!ushort.TryParse(firstText.Trim(), System.Globalization.NumberStyles.HexNumber,
                             null, out ushort first)
            || !ushort.TryParse(lastText.Trim(), System.Globalization.NumberStyles.HexNumber,
                                null, out ushort last))
        {
            StatusText = "Port not understood -- try 3C0 or 3C0-3CF";
            return;
        }

        if (last < first) (first, last) = (last, first);

        _portWatches.Add((first, last));
        PortWatches.Add(first == last ? $"{first:X3}" : $"{first:X3}-{last:X3}");
        HookPorts();

        PortWatchText = "";
        StatusText = first == last
            ? $"Watching port {first:X3}"
            : $"Watching ports {first:X3} to {last:X3}";
    }

    [RelayCommand]
    private void RemovePortWatch(string? entry)
    {
        int index = entry is null ? -1 : PortWatches.IndexOf(entry);
        if (index < 0) return;

        PortWatches.RemoveAt(index);
        _portWatches.RemoveAt(index);
    }

    [RelayCommand]
    private void ClearPortLog() => PortLog.Clear();

    private void HookPorts() => _session.WithMachine(machine =>
    {
        if (_portHooked) return;
        _portHooked = true;
        machine.Io.PortAccessed += OnPortAccessed;
    });

    /// <summary>
    /// Records a watched port access. Like the memory handler this runs on the
    /// emulation thread and does as little as it can: the ports being watched
    /// are usually the busiest in the machine.
    /// </summary>
    private void OnPortAccessed(ushort port, bool write, byte value)
    {
        if (_portWatches.Count == 0) return;

        bool watched = false;
        foreach ((ushort first, ushort last) in _portWatches)
        {
            if (port < first || port > last) continue;
            watched = true;
            break;
        }

        if (!watched) return;

        string where = _session.WithMachine(machine =>
        {
            CpuState state = machine.Cpu.State;
            return $"{state.Cs.Selector:X4}:{state.Eip:X8}";
        });

        PortLog.Add($"{(write ? "OUT" : "IN ")} {port:X3} {(write ? "<-" : "->")} {value:X2}   from {where}");
        while (PortLog.Count > 400) PortLog.RemoveAt(0);
    }

    // --- log panel ---------------------------------------------------------------

    /// <summary>The lines the logs produced that pass the filter, newest last.</summary>
    public ObservableCollection<string> LogLines { get; } = new();

    [ObservableProperty] private string _logFilterText = "";
    [ObservableProperty] private bool _logRunning;

    private readonly List<string> _allLogLines = new();
    private Core.Diagnostics.DosCallLog? _dosLog;
    private Core.Diagnostics.DiskWriteLog? _diskLog;

    /// <summary>
    /// Starts collecting the DOS calls that move files about, the faults the
    /// processor raises, and the files the guest writes to disk -- one list,
    /// in the order they happened.
    ///
    /// The three used to be reachable only through environment variables
    /// naming files, which suits a long headless run and is useless for the
    /// question a debugger is asked, which is "what did the guest just do".
    /// Here they go to a list instead of a file, and the list can be
    /// filtered.
    ///
    /// Started on request rather than always: resolving a sector to a file
    /// name reads the volume's own tables on every write, and that is too
    /// much to do to a machine that is merely running.
    /// </summary>
    [RelayCommand]
    private void StartLog()
    {
        if (LogRunning) return;

        _session.WithMachine(machine =>
        {
            _dosLog = Core.Diagnostics.DosCallLog.Attach(machine);
            _dosLog.LineWritten += OnLogLine;

            // The write log follows the first hard disk, where DOS lives. A
            // second disk's writes go unlogged, and saying so beats a log
            // that quietly shows half of them.
            if (machine.DiskServices.GetDrive(0x80) is { } disk)
            {
                _diskLog = Core.Diagnostics.DiskWriteLog.Attach(machine, disk);
                _diskLog.LineWritten += OnLogLine;
            }
        });

        LogRunning = true;
        StatusText = _diskLog is null
            ? "Logging DOS calls and faults (no hard disk to watch writes on)"
            : "Logging DOS calls, faults and disk writes";
    }

    [RelayCommand]
    private void ClearLog()
    {
        _allLogLines.Clear();
        LogLines.Clear();
    }

    /// <summary>
    /// Keeps the lines that contain the filter, case-insensitively. "exec"
    /// shows what ran, "fault" what went wrong, a file name who touched it.
    /// </summary>
    partial void OnLogFilterTextChanged(string value)
    {
        LogLines.Clear();
        foreach (string line in _allLogLines)
            if (PassesLogFilter(line)) LogLines.Add(line);
    }

    private bool PassesLogFilter(string line)
        => LogFilterText.Length == 0
        || line.Contains(LogFilterText, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs on the emulation thread, inside the call being logged, so it
    /// keeps the line and does nothing else.
    /// </summary>
    private void OnLogLine(string line)
    {
        _allLogLines.Add(line);
        while (_allLogLines.Count > 2000) _allLogLines.RemoveAt(0);

        if (!PassesLogFilter(line)) return;
        LogLines.Add(line);
        while (LogLines.Count > 2000) LogLines.RemoveAt(0);
    }
}

public sealed record DisassemblyLine(
    string Address, string Bytes, string Text, bool IsCurrent, bool HasBreakpoint);

public sealed record RegisterEntry(string Name, string Value);

public sealed record BreakpointEntry(uint LinearAddress, string Display);

public sealed record WatchpointEntry(uint Address, uint Length, string Display);
