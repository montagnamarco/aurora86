using Aurora386.Core;
using Xunit;

namespace Aurora386.Tests;

/// <summary>
/// A reset through the keyboard controller starts the POST over, and the
/// POST's count of extended memory destroys what was above the first
/// megabyte -- HIMEM depends on that to find the memory unclaimed on the
/// next boot.
/// </summary>
public class WarmResetTests
{
    [Fact]
    public void ThePostWipesExtendedMemoryOnAWarmReset()
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);   // through the POST

        // What HIMEM leaves at the one-megabyte mark while it is installed.
        // The gate has to be open to reach it, as HIMEM opens it.
        machine.Memory.A20Enabled = true;
        uint header = 0x100003;
        foreach (char c in "VDISK3.3")
            machine.Memory.WriteByte(header++, (byte)c);
        machine.Memory.WriteByte(0x400000, 0x5A);
        Assert.Equal((byte)'V', machine.Memory.ReadByte(0x100003));

        machine.Io.WriteByte(0x64, 0xFE);   // pulse the reset line
        machine.RunInstructions(3000);

        machine.Memory.A20Enabled = true;
        Assert.Equal(0, machine.Memory.ReadByte(0x100003));
        Assert.Equal(0, machine.Memory.ReadByte(0x400000));
    }

    [Fact]
    public void ConventionalMemorySurvivesAWarmReset()
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);
        machine.Memory.WriteByte(0x80000, 0x77);

        machine.Io.WriteByte(0x64, 0xFE);
        machine.RunInstructions(3000);

        Assert.Equal(0x77, machine.Memory.ReadByte(0x80000));
    }
}
