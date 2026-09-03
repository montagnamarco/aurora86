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

/// <summary>
/// A held key repeats by itself, because on a real PC the keyboard is what
/// repeats it: it waits about half a second and then re-sends the make code
/// roughly ten times a second. DOS has no timer for this of its own, so a
/// keyboard that does not repeat leaves a held key producing one character
/// however long it is held.
/// </summary>
public class KeyboardRepeatTests
{
    private const byte D = 0x20;          // the 'D' key
    private const byte LeftShift = 0x2A;

    /// <summary>Reads out every scancode the controller has ready.</summary>
    private static List<byte> Drain(Machine machine)
    {
        var codes = new List<byte>();

        // Bit 0 of the status port reports a byte waiting in the output
        // register; reading port 0x60 takes it and lets the next one through.
        while ((machine.Io.ReadByte(0x64) & 0x01) != 0)
            codes.Add(machine.Io.ReadByte(0x60));

        return codes;
    }

    [Fact]
    public void AHeldKeyRepeatsAfterTheInitialDelay()
    {
        using var machine = new Machine();
        machine.Keyboard.PushScanCode(D);
        Assert.Equal(new byte[] { D }, Drain(machine));

        // Well short of the half-second delay: still nothing.
        machine.Keyboard.Advance(300);
        Assert.Empty(Drain(machine));

        // Past it: the key starts repeating.
        machine.Keyboard.Advance(300);
        Assert.Equal(new byte[] { D }, Drain(machine));

        // And keeps going, about ten times a second.
        machine.Keyboard.Advance(120);
        Assert.Equal(new byte[] { D }, Drain(machine));
    }

    [Fact]
    public void ReleasingTheKeyStopsTheRepeat()
    {
        using var machine = new Machine();
        machine.Keyboard.PushScanCode(D);
        machine.Keyboard.PushScanCode(D | 0x80);
        Drain(machine);

        machine.Keyboard.Advance(2000);
        Assert.Empty(Drain(machine));
    }

    [Fact]
    public void ModifiersDoNotRepeat()
    {
        // Holding Shift must not flood the guest with shift presses: it would
        // do nothing useful and would crowd out the key being shifted.
        using var machine = new Machine();
        machine.Keyboard.PushScanCode(LeftShift);
        Drain(machine);

        machine.Keyboard.Advance(2000);
        Assert.Empty(Drain(machine));
    }

    [Fact]
    public void TheMostRecentKeyIsTheOneThatRepeats()
    {
        // Rolling from one key onto the next without letting the first go is
        // ordinary typing, and a real keyboard repeats whichever key was
        // pressed last.
        using var machine = new Machine();
        const byte K = 0x25;

        machine.Keyboard.PushScanCode(D);
        machine.Keyboard.PushScanCode(K);
        Drain(machine);

        machine.Keyboard.Advance(600);
        Assert.Equal(new byte[] { K }, Drain(machine));
    }

    [Fact]
    public void ArrowKeysRepeatWithTheirPrefix()
    {
        // The arrows and the other keys added after the original PC keyboard
        // are announced by a 0xE0 byte before the code proper. The prefix is
        // not a key of its own: it must not be mistaken for one, and when the
        // key repeats the whole pair has to repeat, or the guest sees a bare
        // 0x48 -- which on the original keyboard means the numeric keypad's 8.
        using var machine = new Machine();
        const byte Up = 0x48;

        machine.Keyboard.PushScanCode(0xE0);
        machine.Keyboard.PushScanCode(Up);
        Assert.Equal(new byte[] { 0xE0, Up }, Drain(machine));

        machine.Keyboard.Advance(600);
        Assert.Equal(new byte[] { 0xE0, Up }, Drain(machine));
    }

    [Fact]
    public void ReleasingAnArrowKeyStopsItsRepeat()
    {
        // The release arrives prefixed too, and has to be recognised as the
        // release of the key that is repeating.
        using var machine = new Machine();
        const byte Up = 0x48;

        machine.Keyboard.PushScanCode(0xE0);
        machine.Keyboard.PushScanCode(Up);
        machine.Keyboard.PushScanCode(0xE0);
        machine.Keyboard.PushScanCode(Up | 0x80);
        Drain(machine);

        machine.Keyboard.Advance(2000);
        Assert.Empty(Drain(machine));
    }

    [Fact]
    public void ARepeatedArrowIsNeverSentWithoutItsPrefix()
    {
        // A bare 0x48 does not mean "up arrow". It is what the original
        // keyboard sent for the numeric keypad's 8, and that is how software
        // reads it: with Num Lock on, the digit 8. So a repeat that dropped
        // the prefix would type digits into the document instead of moving
        // the cursor -- which is how this showed up in EDIT and QBasic.
        using var machine = new Machine();
        const byte Up = 0x48;

        machine.Keyboard.PushScanCode(0xE0);
        machine.Keyboard.PushScanCode(Up);
        Drain(machine);

        // Let it repeat several times over; every single repeat must carry
        // the prefix, not just the first.
        for (int repeat = 0; repeat < 5; repeat++)
        {
            machine.Keyboard.Advance(600);
            Assert.Equal(new byte[] { 0xE0, Up }, Drain(machine));
        }
    }

    [Fact]
    public void LosingFocusStopsTheRepeat()
    {
        // When the window loses focus the release never arrives, so the
        // frontend says so explicitly. Without this the guest would be left
        // with a key held down forever.
        using var machine = new Machine();
        machine.Keyboard.PushScanCode(D);
        Drain(machine);

        machine.Keyboard.ClearHeldKeys();
        machine.Keyboard.Advance(2000);
        Assert.Empty(Drain(machine));
    }
}

/// <summary>
/// The BIOS turns scancodes into the (scancode, ASCII) pairs DOS reads. The
/// keys added after the original PC keyboard arrive behind a 0xE0 prefix and
/// share their scancodes with the numeric keypad, so the prefix is the only
/// thing telling the up arrow from the digit 8.
/// </summary>
public class ExtendedKeyTranslationTests
{
    private static Machine PoweredOn()
    {
        var machine = new Machine();
        machine.RunInstructions(200);
        return machine;
    }

    /// <summary>
    /// Delivers scancodes the way the hardware does: every byte the keyboard
    /// sends raises IRQ 1 once, and the handler reads exactly one byte per
    /// interrupt.
    /// </summary>
    private static void Deliver(Machine machine, params byte[] scanCodes)
    {
        foreach (byte code in scanCodes)
        {
            machine.Keyboard.PushScanCode(code);
            machine.KeyboardServices.HandleIrq(machine.Cpu);
        }
    }

    [Fact]
    public void AnArrowKeyProducesOneKeystrokeWithNoCharacter()
    {
        using var machine = PoweredOn();

        Deliver(machine, 0xE0, 0x48);   // up arrow

        Assert.True(machine.KeyboardServices.TryDequeue(out ushort key));

        // Scancode in the high byte, and an ASCII byte of zero: that zero is
        // how a program knows to look at the scancode instead.
        Assert.Equal(0x48, key >> 8);
        Assert.Equal(0, key & 0xFF);

        // And nothing else: the prefix must not have queued a keystroke of
        // its own.
        Assert.False(machine.KeyboardServices.TryDequeue(out _));
    }

    [Fact]
    public void EscapeStillProducesItsCharacter()
    {
        using var machine = PoweredOn();

        Deliver(machine, 0x01);

        Assert.True(machine.KeyboardServices.TryDequeue(out ushort key));
        Assert.Equal(0x01, key >> 8);
        Assert.Equal(0x1B, key & 0xFF);   // ESC
    }

    [Fact]
    public void PauseIsNotMistakenForNumLock()
    {
        // Pause arrives as an extended 0x45; Num Lock is the same code
        // without the prefix. Toggling Num Lock by accident would turn the
        // keypad's arrows into digits, which is the sort of fault a user
        // reports as "the keyboard went strange" and nobody can reproduce.
        //
        // This is the one case where the prefix genuinely changes what the
        // BIOS must do, and it fails if the prefix is not tracked.
        using var machine = PoweredOn();
        byte before = machine.Bda.KeyboardFlags;

        Deliver(machine, 0xE1, 0x45);

        Assert.Equal(before, machine.Bda.KeyboardFlags);
    }

    [Fact]
    public void RightControlSetsTheSameFlagAsTheLeftOne()
    {
        // The two Ctrl keys are one modifier as far as software is concerned,
        // so the prefixed one has to set the same status bit.
        using var machine = PoweredOn();

        Deliver(machine, 0xE0, 0x1D);
        Assert.Equal(0x04, machine.Bda.KeyboardFlags & 0x04);

        Deliver(machine, 0xE0, 0x1D | 0x80);
        Assert.Equal(0, machine.Bda.KeyboardFlags & 0x04);
    }
}
