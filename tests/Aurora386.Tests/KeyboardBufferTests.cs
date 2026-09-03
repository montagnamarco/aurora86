using Aurora386.Core;
using Aurora386.Core.Bios;

namespace Aurora386.Tests;

/// <summary>
/// The BIOS keyboard buffer is a ring of sixteen keystrokes living between
/// 0x41E and 0x43E in the BIOS data area. Everything here guards the moment
/// the ring wraps, which is where it is easiest to get wrong and hardest to
/// notice: a buffer that never wraps looks perfectly healthy until the
/// seventeenth keystroke, and then misbehaves forever after.
/// </summary>
public class KeyboardBufferTests
{
    /// <summary>
    /// Builds a machine that has been through its POST. Constructing a
    /// Machine is not enough: the BIOS data area is filled in by the POST
    /// handler, which only runs once the CPU reaches it, so a few
    /// instructions have to be executed first.
    /// </summary>
    private static Machine PoweredOn()
    {
        var machine = new Machine();
        machine.RunInstructions(200);
        return machine;
    }

    [Fact]
    public void BufferBoundsArePublishedForSoftwareThatReadsThem()
    {
        // 0x480 and 0x482 declare where the ring begins and ends. They exist
        // so that software can find (or enlarge) the buffer instead of
        // assuming its address. DOS is one such reader, so leaving them at
        // zero sends it to the bottom of the BIOS data area.
        using var machine = PoweredOn();

        Assert.Equal(BiosDataArea.KeyboardBufferStart, machine.Bda.KeyboardBufferStartPointer);
        Assert.Equal(BiosDataArea.KeyboardBufferEnd, machine.Bda.KeyboardBufferEndPointer);
    }

    [Fact]
    public void TailWrapsBackToTheStartAndNeverLeavesTheRing()
    {
        // Enqueue and drain far more keystrokes than the ring holds. Whatever
        // the pointers do along the way, they must stay inside the ring: this
        // is the failure that let a stray tail pointer scribble over the BIOS
        // data area past 0x43E and sent the DOS prompt into an endless loop.
        using var machine = PoweredOn();
        KeyboardServices keyboard = machine.KeyboardServices;

        for (int i = 0; i < 100; i++)
        {
            Assert.True(keyboard.Enqueue((ushort)(0x1E00 | ('a' + i % 26))));

            Assert.InRange(machine.Bda.KeyboardBufferTail,
                (ushort)BiosDataArea.KeyboardBufferStart,
                (ushort)(BiosDataArea.KeyboardBufferEnd - 2));
            Assert.InRange(machine.Bda.KeyboardBufferHead,
                (ushort)BiosDataArea.KeyboardBufferStart,
                (ushort)(BiosDataArea.KeyboardBufferEnd - 2));

            Assert.True(keyboard.TryDequeue(out _));
        }
    }

    [Fact]
    public void KeystrokesComeBackInTheOrderTheyWereTyped()
    {
        // A ring that wraps correctly still has to preserve order across the
        // seam. Fill past the wrap point and read the whole lot back.
        using var machine = PoweredOn();
        KeyboardServices keyboard = machine.KeyboardServices;

        // Fifteen is the most the ring can hold: one slot is always left
        // empty, because a completely full ring is indistinguishable from an
        // empty one when head and tail are the only evidence.
        var typed = new ushort[15];
        for (int i = 0; i < typed.Length; i++)
        {
            typed[i] = (ushort)(0x1E00 | ('a' + i));
            Assert.True(keyboard.Enqueue(typed[i]));
        }

        Assert.False(keyboard.Enqueue(0xFFFF));

        foreach (ushort expected in typed)
        {
            Assert.True(keyboard.TryDequeue(out ushort actual));
            Assert.Equal(expected, actual);
        }

        // And once drained, the ring reports empty rather than handing back
        // whatever bytes happen to be sitting in the buffer.
        Assert.False(keyboard.TryDequeue(out _));
    }
}
