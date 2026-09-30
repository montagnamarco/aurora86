using Aurora86.Core;

namespace Aurora86.Tests;

/// <summary>
/// Register 0x0F of the graphics controller, which a VGA does not have.
///
/// A VGA's graphics controller ends at 0x08. Paradise put a register at 0x0F,
/// and Windows 3.1's own Super VGA driver identifies the card by testing it --
/// it makes no VESA call at all, so a card that fails this test is one the
/// driver refuses outright.
///
/// The test, disassembled from the driver itself, is cleverer than looking for
/// a signature. It reads the register, saves the top five bits unchanged, then
/// writes those five bits *inverted* with 101 in the low three. It reads back
/// and cancels out the five it saved, and what should be left is exactly the 5
/// it wrote.
///
/// Work through what that demands: for the top five to cancel they must come
/// back as they were, so the write to them has to be ignored; for the low
/// three to survive they must be real storage. The driver is not asking "are
/// you a Paradise" but "does this register have three writable bits under five
/// read-only ones" -- something no plain VGA does, and nothing else of the
/// period did by accident.
/// </summary>
public class ParadiseRegisterTests
{
    private static Machine Started()
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        return machine;
    }

    private static byte Read(Machine machine, int index)
    {
        machine.Io.WriteByte(0x3CE, (byte)index);
        return machine.Io.ReadByte(0x3CF);
    }

    private static void Write(Machine machine, int index, byte value)
    {
        machine.Io.WriteByte(0x3CE, (byte)index);
        machine.Io.WriteByte(0x3CF, value);
    }

    [Fact]
    public void TheDriversOwnTestPasses()
    {
        // The sequence exactly as the driver performs it, which is the only
        // check that really matters here.
        using Machine machine = Started();

        byte value = Read(machine, 0x0F);
        byte saved = (byte)(value & 0xF8);          // the top five, unchanged
        byte written = (byte)((saved ^ 0xF8) | 0x05);   // inverted, plus 101

        machine.Io.WriteByte(0x3CF, written);
        byte reread = machine.Io.ReadByte(0x3CF);

        Assert.Equal(0x05, reread ^ saved);
    }

    [Fact]
    public void TheLowThreeBitsAreStorage()
    {
        using Machine machine = Started();

        Write(machine, 0x0F, 0x05);
        Assert.Equal(0x05, Read(machine, 0x0F));

        Write(machine, 0x0F, 0x02);
        Assert.Equal(0x02, Read(machine, 0x0F));
    }

    [Fact]
    public void TheTopFiveBitsIgnoreWrites()
    {
        // The half that is easy to get wrong: a register that kept everything
        // written to it fails the driver's test, because the inverted top
        // bits come back and the cancellation leaves rubbish.
        using Machine machine = Started();

        Write(machine, 0x0F, 0xFD);

        Assert.Equal(0x05, Read(machine, 0x0F));
    }

    [Fact]
    public void TheOrdinaryGraphicsRegistersAreUntouched()
    {
        // Only 0x0F behaves this way. Everything the VGA itself defines has
        // to keep working exactly as before, or every graphics mode breaks.
        using Machine machine = Started();

        Write(machine, 0x08, 0xFF);          // bit mask
        Assert.Equal(0xFF, Read(machine, 0x08));

        Write(machine, 0x05, 0x40);          // mode register
        Assert.Equal(0x40, Read(machine, 0x05));
    }
}
