using System.Text;
using CpuNs = Aurora386.Core.Cpu;

namespace Aurora386.Core.Bios;

/// <summary>
/// Records the DOS calls a guest makes that move files about: renaming,
/// creating, deleting, and launching another program.
///
/// These are the calls that say what an installer is actually doing, as
/// opposed to how much disk it is touching. An installer that has stopped
/// making progress has usually stopped between two of them, and the last one
/// recorded says which two.
///
/// The file names are read out of the guest's own memory at the moment of the
/// call, because that is the only place they exist: DOS is handed a pointer,
/// not a name.
/// </summary>
public sealed class DosCallLog : IDisposable
{
    private readonly StreamWriter _output;

    public DosCallLog(string path)
    {
        _output = new StreamWriter(path) { AutoFlush = true };
        _output.WriteLine("# DOS calls that move files about");
    }

    /// <summary>Watch a machine and record the calls as they go by.</summary>
    public static DosCallLog Attach(Machine machine, string path)
    {
        var log = new DosCallLog(path);

        machine.Cpu.SoftwareInterruptIssued += vector =>
        {
            if (vector == 0x21) log.Note(machine.Cpu);
        };

        // A DOS extender does its work in protected mode, and when it gives up
        // it does so quietly -- the process simply exits. The fault that drove
        // it there is the useful part, so processor exceptions are recorded
        // alongside the file calls, in the same order.
        machine.Cpu.ProcessorException += (vector, errorCode, address, protectedMode) =>
        {
            // Vectors 0 to 16 are the processor's own; anything else here is a
            // hardware interrupt that happens to share the numbering.
            if (vector > 16) return;
            log.NoteFault(machine.Cpu, vector, errorCode, address, protectedMode);
        };

        return log;
    }

    private string? _lastFault;
    private int _faultRepeats;

    private void NoteFault(CpuNs.Cpu cpu, byte vector, uint errorCode, uint address,
                           bool protectedMode)
    {
        // An offset alone cannot be looked up in anything: the segment is what
        // says which program the fault is in. The instruction's own bytes are
        // recorded with it, because for an invalid-opcode fault they are the
        // whole question.
        ushort segment = cpu.State.Cs.Selector;
        var bytes = new StringBuilder();
        for (uint i = 0; i < 8; i++)
            bytes.Append($"{cpu.Memory.ReadByte(cpu.State.Cs.Base + address + i):x2} ");

        // For a limit violation the segments themselves are the evidence: the
        // fault says an access ran past the end of one of them, and only their
        // limits say which.
        string line = $"fault    vector {vector:X2} error {errorCode:X4}"
                    + $" at {segment:X4}:{address:X4} ({(protectedMode ? "protected" : "real")})"
                    + $"  bytes {bytes.ToString().TrimEnd()}"
                    + $"  ds={cpu.State.Ds.Selector:X4}/{cpu.State.Ds.Limit:X8}"
                    + $" es={cpu.State.Es.Selector:X4}/{cpu.State.Es.Limit:X8}"
                    + $" si={cpu.State.Esi:X8} di={cpu.State.Edi:X8} cx={cpu.State.Ecx:X8}";

        // A fault that repeats is a loop, and printing every repetition buries
        // everything else; the count says more than the repetitions would.
        if (line == _lastFault)
        {
            if (++_faultRepeats is 10 or 1000 or 100000)
                _output.WriteLine($"         ... same fault × {_faultRepeats}");
            return;
        }

        _lastFault = line;
        _faultRepeats = 1;
        _output.WriteLine(line);
    }

    private void Note(CpuNs.Cpu cpu)
    {
        byte function = cpu.State.GetReg8(4);   // AH

        switch (function)
        {
            case 0x56:   // rename: DS:DX is the old name, ES:DI the new one
                _output.WriteLine($"rename   {ReadString(cpu, cpu.State.Ds.Selector, cpu.State.Dx)}"
                                + $" -> {ReadString(cpu, cpu.State.Es.Selector, cpu.State.Di)}");
                break;

            case 0x4B:   // exec: the one call that hands control to another program
                _output.WriteLine($"exec     {ReadString(cpu, cpu.State.Ds.Selector, cpu.State.Dx)}"
                                + $" (subfunction {cpu.State.GetReg8(0):X2})");
                break;

            case 0x3C:
                _output.WriteLine($"create   {ReadString(cpu, cpu.State.Ds.Selector, cpu.State.Dx)}");
                break;

            case 0x41:
                _output.WriteLine($"delete   {ReadString(cpu, cpu.State.Ds.Selector, cpu.State.Dx)}");
                break;

            case 0x4C:
                _output.WriteLine($"exit     code {cpu.State.GetReg8(0)}");
                break;
        }
    }

    /// <summary>
    /// Reads a null-terminated name from the guest. The length is capped
    /// because a bad pointer would otherwise walk the whole address space.
    /// </summary>
    private static string ReadString(CpuNs.Cpu cpu, ushort segment, ushort offset)
    {
        var text = new StringBuilder();

        for (int i = 0; i < 128; i++)
        {
            byte c = cpu.Memory.ReadByte((uint)(segment * 16 + offset + i));
            if (c == 0) break;
            text.Append(c is >= 32 and < 127 ? (char)c : '.');
        }

        return text.ToString();
    }

    public void Dispose() => _output.Dispose();
}
