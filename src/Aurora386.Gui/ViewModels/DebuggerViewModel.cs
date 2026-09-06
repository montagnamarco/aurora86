using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Aurora386.Core;
using Aurora386.Core.Cpu;
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
                    byte value = machine.Memory.ReadByte(rowAddress + (uint)column);
                    hex.Append(value.ToString("X2")).Append(' ');
                    text.Append(value >= 32 && value < 127 ? (char)value : '.');
                }

                MemoryLines.Add($"{rowAddress:X6}  {hex} {text}");
            }
        });
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
}

public sealed record DisassemblyLine(
    string Address, string Bytes, string Text, bool IsCurrent, bool HasBreakpoint);

public sealed record RegisterEntry(string Name, string Value);

public sealed record BreakpointEntry(uint LinearAddress, string Display);
