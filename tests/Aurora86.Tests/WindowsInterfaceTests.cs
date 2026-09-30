using Aurora86.Core;
using Aurora86.Core.Cpu;

namespace Aurora86.Tests;

/// <summary>
/// Tests for INT 2Fh AX=16xx, the interface Windows and DOS use to negotiate.
///
/// Windows announces itself through these calls and reads the answers to
/// decide whether it can start at all. The answers matter more than they look:
/// 1600h in particular is how Windows finds out whether a copy of itself is
/// already running, and any answer but zero stops it before it begins.
/// </summary>
public class WindowsInterfaceTests
{
    private static Machine Ask(ushort ax)
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        machine.Cpu.State.Halted = false;

        machine.Cpu.SetSegment(SegReg.Cs, 0);
        machine.Cpu.SetSegment(SegReg.Ss, 0);
        machine.Cpu.State.Esp = 0x7000;
        machine.Cpu.State.Eip = 0x2000;
        machine.Cpu.State.Ax = ax;

        machine.Memory.WriteByte(0x2000, 0xCD);   // INT 2Fh
        machine.Memory.WriteByte(0x2001, 0x2F);

        // Three steps: into the handler, through it, and back.
        machine.Cpu.Step();
        machine.Cpu.Step();
        machine.Cpu.Step();

        return machine;
    }

    [Fact]
    public void WindowsIsToldThatNoCopyOfItIsAlreadyRunning()
    {
        using Machine machine = Ask(0x1600);

        // AL=0 is "no Windows here". Windows refuses to start if it reads
        // anything else, taking it for a second instance of itself.
        Assert.Equal(0, machine.Cpu.State.GetReg8(0));
    }

    [Fact]
    public void TheStartupBroadcastIsConsentedTo()
    {
        using Machine machine = Ask(0x1605);
        machine.Cpu.State.Cx = 0;

        // A resident program refuses the startup by setting CX non-zero.
        // Nothing here objects, so CX must come back as it went in.
        Assert.Equal(0, machine.Cpu.State.Cx);
    }

    [Fact]
    public void EnumeratingResidentProgramsEndsImmediately()
    {
        using Machine machine = Ask(0x160B);

        // CX=0 ends the enumeration: no TSR registered an interest.
        Assert.Equal(0, machine.Cpu.State.Cx);
    }

    /// <summary>
    /// The calls have to be answered rather than fall through to the default,
    /// which would hand them to the redirector and the CD-ROM extensions.
    /// </summary>
    [Theory]
    [InlineData(0x1600)] [InlineData(0x1605)] [InlineData(0x1606)]
    [InlineData(0x1607)] [InlineData(0x160B)]
    public void EachCallReturnsWithoutFaulting(int ax)
    {
        using Machine machine = Ask((ushort)ax);

        // Back in the caller's code segment, past the INT: the handler ran and
        // returned rather than leaving the machine somewhere unexpected.
        Assert.Equal(0, machine.Cpu.State.Cs.Selector);
        Assert.Equal(0x2002u, machine.Cpu.State.Eip);
    }
}
