using System.Text;
using Aurora86.Core;
using Aurora86.Core.Cpu;

namespace Aurora86.Tests;

/// <summary>
/// DOS starts its resident command interpreter through EXEC. Recognizing
/// that request lets the redirector wait for the final DOS drive table;
/// Windows 95 keeps COMMAND.COM below WINDOWS rather than at the root.
/// </summary>
public sealed class CommandProcessorStartupTests
{
    /// <summary>Runs an actual guest software interrupt with its EXEC pathname.</summary>
    private static bool RunRequest(string path, bool terminated = true,
        byte vector = 0x21, ushort ax = 0x4B00)
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);
        const uint caller = 0x20000;
        const uint fileName = 0x40000;
        byte[] bytes = Encoding.ASCII.GetBytes(path);
        for (uint offset = 0; offset < bytes.Length; offset++)
        {
            machine.Memory.WriteByte(fileName + offset, bytes[offset]);
        }

        if (terminated)
        {
            machine.Memory.WriteByte(fileName + (uint)bytes.Length, 0);
        }

        // The interrupt handler merely returns. The machine observes the
        // guest's real EXEC request before DOS receives it.
        machine.Memory.WriteWord((uint)vector * 4, 0x20);
        machine.Memory.WriteWord((uint)vector * 4 + 2, 0x2000);
        machine.Memory.WriteByte(caller + 0x20, 0xCF);
        machine.Memory.WriteByte(caller, 0xB8);
        machine.Memory.WriteWord(caller + 1, ax);
        machine.Memory.WriteByte(caller + 3, 0xCD);
        machine.Memory.WriteByte(caller + 4, vector);
        machine.Memory.WriteByte(caller + 5, 0xF4);
        machine.Cpu.SetSegment(SegReg.Cs, 0x2000);
        machine.Cpu.SetSegment(SegReg.Ds, 0x4000);
        machine.Cpu.SetSegment(SegReg.Ss, 0x3000);
        machine.Cpu.State.Eip = 0;
        machine.Cpu.State.Esp = 0xFFFE;
        machine.Cpu.State.Dx = 0;
        machine.Cpu.State.Halted = false;
        for (int instruction = 0; instruction < 30 && !machine.Cpu.State.Halted; instruction++)
        {
            machine.Cpu.Step();
        }

        Assert.True(machine.Cpu.State.Halted);
        Assert.Equal(6u, machine.Cpu.State.Eip);
        return machine.CommandProcessorStarted;
    }

    [Theory]
    [InlineData("COMMAND.COM")]
    [InlineData(@"C:\COMMAND.COM")]
    [InlineData(@"C:\WINDOWS\COMMAND.COM")]
    [InlineData(@"c:\windows\command.com")]
    public void ExecRecognizesTheCommandInterpreterAtItsActualDosPath(string path)
    {
        Assert.True(RunRequest(path));
    }

    [Fact]
    public void TheLastValidDosPathByteCanTerminateTheCommandInterpreterName()
    {
        string path = @"C:\" + new string('A', 48) + @"\COMMAND.COM";
        Assert.Equal(63, path.Length);
        Assert.True(RunRequest(path));
    }

    [Theory]
    [InlineData(@"C:\WINDOWS\MODE.COM")]
    [InlineData(@"C:\COMMAND.COM.EXE")]
    [InlineData(@"C:\WINDOWS\COMMAND.COM\OTHER.EXE")]
    public void AnotherExecutableDoesNotSignalThatDosStartupHasFinished(string path)
    {
        Assert.False(RunRequest(path));
    }

    [Fact]
    public void AnUnterminatedPathCannotBeRecognizedAtTheReadLimit()
    {
        string path = @"C:\" + new string('A', 49) + @"\COMMAND.COM";
        Assert.Equal(64, path.Length);
        Assert.False(RunRequest(path, terminated: false));
    }

    [Theory]
    [InlineData(0x20, 0x4B00)]
    [InlineData(0x21, 0x4A00)]
    public void OnlyDosExecRequestsSignalCommandProcessorStartup(int vector, int ax)
    {
        Assert.False(RunRequest(@"C:\COMMAND.COM", vector: (byte)vector, ax: (ushort)ax));
    }
}
