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

        // The flag letters follow the convention every x86 debugger uses: an
        // upper-case letter when the flag is set, a dot when it is clear.
        LazyFlags f = machine.Cpu.Flags;
        FlagsText = string.Concat(
            f.Overflow ? "O" : ".", f.Direction ? "D" : ".",
            f.Interrupt ? "I" : ".", f.Sign ? "S" : ".",
            f.Zero ? "Z" : ".", f.Adjust ? "A" : ".",
            f.Parity ? "P" : ".", f.Carry ? "C" : ".");
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

            Disassembly.Add(new DisassemblyLine(
                $"{state.Cs.Selector:X4}:{offset:X4}",
                decoded.Bytes,
                decoded.Text,
                IsCurrent: i == 0,
                HasBreakpoint: _breakpoints.Contains(linear)));

            offset += (uint)Math.Max(1, decoded.Length);
        }
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

    private void RefreshMemory()
    {
        MemoryLines.Clear();

        if (!TryParseAddress(MemoryAddressText, out uint linear)) return;

        _session.WithMachine(machine =>
        {
            // Sixteen bytes per line, hex on the left and printable characters
            // on the right: the layout every hex editor has used since DEBUG.
            for (int row = 0; row < 16; row++)
            {
                uint rowAddress = linear + (uint)(row * 16);
                var hex = new System.Text.StringBuilder();
                var text = new System.Text.StringBuilder();

                for (int column = 0; column < 16; column++)
                {
                    byte value = ReadForView(machine, rowAddress + (uint)column);
                    hex.Append(value.ToString("X2")).Append(' ');
                    text.Append(value >= 32 && value < 127 ? (char)value : '.');
                }

                MemoryLines.Add($"{rowAddress:X6}  {hex} {text}");
            }
        });
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

    [RelayCommand]
    private void RunToBreakpoint()
    {
        if (_breakpoints.Count == 0)
        {
            _session.Resume();
            StatusText = "Running (no breakpoints set)";
            return;
        }

        RunUntil(machine =>
        {
            CpuState state = machine.Cpu.State;
            return _breakpoints.Contains(state.Cs.Base + state.Eip);
        }, budget: 200_000_000);

        Refresh();
        StatusText = "Stopped at breakpoint";
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

    // --- watchpoints ---------------------------------------------------------

    public ObservableCollection<WatchpointEntry> Watchpoints { get; } = new();

    [ObservableProperty] private string _watchAddressText = "";
    [ObservableProperty] private string _watchLengthText = "1";

    /// <summary>The most recent writes seen by a watchpoint, newest last.</summary>
    public ObservableCollection<string> WatchLog { get; } = new();

    private readonly List<WatchpointEntry> _watches = new();
    private bool _watchHooked;

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

        var entry = new WatchpointEntry(address, length, $"{address:X6}+{length}");
        _watches.Add(entry);
        Watchpoints.Add(entry);
        ApplyWatchpoints();

        WatchAddressText = "";
        StatusText = $"Watching {length} byte(s) at {address:X6}";
    }

    [RelayCommand]
    private void RemoveWatchpoint(WatchpointEntry? entry)
    {
        if (entry is null) return;

        _watches.Remove(entry);
        Watchpoints.Remove(entry);
        ApplyWatchpoints();
    }

    [RelayCommand]
    private void ClearWatchLog() => WatchLog.Clear();

    private void ApplyWatchpoints() => _session.WithMachine(machine =>
    {
        machine.Memory.SetWatchRanges(_watches.Select(w => (w.Address, w.Length)));

        // Hooked once and left: the handler does nothing while the list is
        // empty, and subscribing twice would log every write twice.
        if (_watchHooked) return;
        _watchHooked = true;
        machine.Memory.Watched += OnWatchedWrite;
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
}

public sealed record DisassemblyLine(
    string Address, string Bytes, string Text, bool IsCurrent, bool HasBreakpoint);

public sealed record RegisterEntry(string Name, string Value);

public sealed record BreakpointEntry(uint LinearAddress, string Display);

public sealed record WatchpointEntry(uint Address, uint Length, string Display);
