using Aurora386.Core;
using Aurora386.Core.Cpu;

namespace Aurora386.Tests;

/// <summary>
/// Tests for INT 15h AH=C0, the system configuration table.
///
/// The table matters less for what it says than for existing at all. Callers
/// detect its absence inconsistently: the documented way is the carry flag,
/// but Windows' DOS extender tests ES:BX for zero and reads the table from
/// wherever those registers point. A BIOS that refuses the call without
/// clearing them hands the caller stale register contents to read as feature
/// bits, and nothing about the failure looks like a failure.
/// </summary>
public class SystemConfigurationTests
{
    private static Machine Ask()
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        machine.Cpu.State.Halted = false;

        machine.Cpu.SetSegment(SegReg.Cs, 0);
        machine.Cpu.SetSegment(SegReg.Ss, 0);
        machine.Cpu.State.Esp = 0x7000;
        machine.Cpu.State.Eip = 0x2000;

        // Point ES:BX somewhere recognisable first, so a handler that fails to
        // write them can be told from one that writes the right thing.
        machine.Cpu.SetSegment(SegReg.Es, 0xDEAD);
        machine.Cpu.State.Bx = 0xBEEF;
        machine.Cpu.State.SetReg8(4, 0xC0);        // AH

        machine.Memory.WriteByte(0x2000, 0xCD);    // INT 15h
        machine.Memory.WriteByte(0x2001, 0x15);

        // Three steps, not one: the INT only reaches the handler's first
        // instruction, and the answer is not in the registers until the
        // handler has run and returned.
        machine.Cpu.Step();
        machine.Cpu.Step();
        machine.Cpu.Step();

        return machine;
    }

    [Fact]
    public void TheCallIsAnsweredRatherThanRefused()
    {
        using Machine machine = Ask();

        Assert.Equal(0, machine.Cpu.State.GetReg8(4));   // AH: no error
    }

    [Fact]
    public void ThePointerNoLongerHoldsWhateverTheCallerLeftThere()
    {
        using Machine machine = Ask();

        Assert.NotEqual(0xDEAD, machine.Cpu.State.Es.Selector);
        Assert.NotEqual(0xBEEF, machine.Cpu.State.Bx);
    }

    [Fact]
    public void TheTableSaysHowLongItIsAndWhatMachineThisIs()
    {
        using Machine machine = Ask();

        uint address = machine.Cpu.State.Es.Base + machine.Cpu.State.Bx;

        // The first word is the length of what follows, and the byte after it
        // is the model: FC is the PC/AT, which is what a 386 clone reports.
        Assert.Equal(8, machine.Memory.ReadWord(address));
        Assert.Equal(0xFC, machine.Memory.ReadByte(address + 2));
    }

    /// <summary>
    /// Bit 6 of the feature byte says a second interrupt controller is
    /// cascaded onto the first. This machine has one -- IRQ 8 upwards go
    /// through it -- so a caller that reads the bit and believes it is right.
    /// </summary>
    [Fact]
    public void TheFeatureBitsDescribeTheHardwareThatIsActuallyHere()
    {
        using Machine machine = Ask();

        uint address = machine.Cpu.State.Es.Base + machine.Cpu.State.Bx;
        byte features = machine.Memory.ReadByte(address + 5);

        Assert.Equal(0x40, features & 0x40);   // second interrupt controller
        Assert.Equal(0x20, features & 0x20);   // real-time clock
        Assert.Equal(0x10, features & 0x10);   // INT 15h AH=4F scan-code filter
    }

    /// <summary>
    /// The check Windows' extender actually performs: MOV AX,ES / OR AX,BX /
    /// JZ. A zero pointer means "no table"; anything else is read as one.
    /// </summary>
    [Fact]
    public void ThePointerPassesTheTestARealCallerMakes()
    {
        using Machine machine = Ask();

        int combined = machine.Cpu.State.Es.Selector | machine.Cpu.State.Bx;

        Assert.NotEqual(0, combined);
    }
}
