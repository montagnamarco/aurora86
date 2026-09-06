using Aurora386.Core;
using Aurora386.Core.Firmware;
using Aurora386.Core.Firmware.Fpu;
using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Diagnostics;
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
    public void AnArrowKeyProducesOneKeystrokeMarkedAsExtended()
    {
        using var machine = PoweredOn();

        Deliver(machine, 0xE0, 0x48);   // up arrow

        Assert.True(machine.KeyboardServices.TryDequeue(out ushort key));

        // Scancode in the high byte, and 0xE0 in the low one. That byte is
        // how a program tells the dedicated cursor keys from the numeric
        // keypad, which shares their scancodes -- INT 16h's plain functions
        // replace it with zero for software that predates the distinction,
        // and the extended ones pass it through.
        Assert.Equal(0x48, key >> 8);
        Assert.Equal(0xE0, key & 0xFF);

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

/// <summary>
/// Port 0x60 is a latch. It holds the byte the controller last put there and
/// keeps holding it until something replaces it, so reading twice returns the
/// same byte twice. Only bit 0 of the status port says whether the byte is
/// new.
/// </summary>
public class KeyboardOutputLatchTests
{
    private static bool ByteWaiting(Machine machine) => (machine.Io.ReadByte(0x64) & 0x01) != 0;

    [Fact]
    public void ReadingTwiceReturnsTheSameByte()
    {
        // Software that wants to be sure of what it read looks again, and
        // must see the same thing. Returning zero on the second read would
        // report a keystroke that never happened -- worse, one whose scancode
        // means nothing at all.
        using var machine = new Machine();
        const byte Escape = 0x01;

        machine.Keyboard.PushScanCode(Escape);

        Assert.True(ByteWaiting(machine));
        Assert.Equal(Escape, machine.Io.ReadByte(0x60));

        // The byte has been collected, so the status port stops advertising
        // it -- but the register still holds it.
        Assert.False(ByteWaiting(machine));
        Assert.Equal(Escape, machine.Io.ReadByte(0x60));
        Assert.Equal(Escape, machine.Io.ReadByte(0x60));
    }

    [Fact]
    public void TheLatchSurvivesDisablingAndReenablingTheKeyboard()
    {
        // This is the sequence QBasic uses for every keystroke: read the
        // scancode, disable the keyboard so that nothing can arrive
        // underneath it, read again to confirm, then re-enable. All of it has
        // to yield the same scancode, or the keystroke is discarded and the
        // program appears to ignore the keyboard entirely.
        using var machine = new Machine();
        const byte Escape = 0x01;

        machine.Keyboard.PushScanCode(Escape);
        Assert.Equal(Escape, machine.Io.ReadByte(0x60));

        machine.Io.WriteByte(0x64, 0xAD);            // disable keyboard
        Assert.Equal(Escape, machine.Io.ReadByte(0x60));
        machine.Io.WriteByte(0x64, 0xAE);            // enable keyboard

        Assert.Equal(Escape, machine.Io.ReadByte(0x60));
    }

    [Fact]
    public void ANewKeystrokeReplacesTheLatchedByte()
    {
        // The latch is stale data, not a second copy of the keystroke: the
        // moment a real key arrives it takes the register over.
        using var machine = new Machine();

        machine.Keyboard.PushScanCode(0x01);
        Assert.Equal(0x01, machine.Io.ReadByte(0x60));

        machine.Keyboard.PushScanCode(0x1E);
        Assert.True(ByteWaiting(machine));
        Assert.Equal(0x1E, machine.Io.ReadByte(0x60));
        Assert.Equal(0x1E, machine.Io.ReadByte(0x60));
    }
}

/// <summary>
/// A 101-key keyboard has keys the original PC keyboard had not: F11 and F12,
/// and a cursor block separate from the numeric keypad. Their scancodes had to
/// go somewhere, and where they went is on top of meanings the older keyboard
/// already used.
///
/// So INT 16h grew a second set of functions. The plain ones behave as they
/// always did, hiding what the old keyboard could not produce; the extended
/// ones return everything. Software picks which to call by first asking
/// whether the enhanced keyboard is there at all.
/// </summary>
public class EnhancedKeyboardTests
{
    private static Machine PoweredOn()
    {
        var machine = new Machine();
        machine.RunInstructions(200);
        return machine;
    }

    private static void Deliver(Machine machine, params byte[] scanCodes)
    {
        foreach (byte code in scanCodes)
        {
            machine.Keyboard.PushScanCode(code);
            machine.KeyboardServices.HandleIrq(machine.Cpu);
        }
    }

    /// <summary>Calls INT 16h and returns AX.</summary>
    private static ushort Call(Machine machine, byte function)
    {
        machine.Cpu.State.SetReg8(4, function);
        machine.KeyboardServices.Handle(machine.Cpu);

        return machine.Cpu.State.Ax;
    }

    [Fact]
    public void TheEnhancedKeyboardIsDeclaredInTheDataArea()
    {
        // Bit 4 of the third status byte. Software reads it to decide whether
        // the extended functions are worth calling: told nothing, it assumes
        // the 84-key keyboard and never asks about F11, F12 or the separate
        // cursor keys, because on that keyboard they do not exist.
        using Machine machine = PoweredOn();

        Assert.Equal(BiosDataArea.EnhancedKeyboardInstalled,
                     machine.Bda.KeyboardFlags3 & BiosDataArea.EnhancedKeyboardInstalled);
    }

    [Fact]
    public void TheExtendedCallReturnsTheCursorKeysMarker()
    {
        // 0xE0 in the low byte is what tells the dedicated up arrow from the
        // keypad's 8, which sends the same scancode.
        using Machine machine = PoweredOn();
        Deliver(machine, 0xE0, 0x48);

        ushort key = Call(machine, 0x10);

        Assert.Equal(0x48, key >> 8);
        Assert.Equal(0xE0, key & 0xFF);
    }

    [Fact]
    public void ThePlainCallHidesTheMarkerInstead()
    {
        // A program written before the 101-key keyboard existed reads the low
        // byte as a character. Handing it 0xE0 would have it print a Greek
        // alpha every time the user pressed an arrow, so the plain call
        // reports zero -- which is what the old keyboard sent.
        using Machine machine = PoweredOn();
        Deliver(machine, 0xE0, 0x48);

        ushort key = Call(machine, 0x00);

        Assert.Equal(0x48, key >> 8);
        Assert.Equal(0, key & 0xFF);
    }

    [Fact]
    public void ThePlainCallSkipsTheKeysItCannotDescribe()
    {
        // F11 has no meaning at all on the older keyboard, so there is
        // nothing sensible to report: the plain call passes over it and
        // returns whatever was typed next.
        using Machine machine = PoweredOn();

        Deliver(machine, 0x57);         // F11
        Deliver(machine, 0x1E);         // A

        ushort key = Call(machine, 0x00);

        Assert.Equal((int)'a', key & 0xFF);
    }

    [Fact]
    public void TheExtendedCallReturnsF11()
    {
        using Machine machine = PoweredOn();
        Deliver(machine, 0x57);

        ushort key = Call(machine, 0x10);

        Assert.Equal(0x85, key >> 8);
    }

    [Fact]
    public void PollingWithThePlainCallDoesNotPromiseAKeyItWillNotGive()
    {
        // The check and the read have to agree. Told a key is waiting and
        // then handed nothing, a program that loops on the pair spins for
        // ever -- and it is a loop DOS itself runs.
        using Machine machine = PoweredOn();

        Deliver(machine, 0x57);   // F11: invisible to the plain call

        machine.Cpu.State.SetReg8(4, 0x01);
        machine.KeyboardServices.Handle(machine.Cpu);

        // Nothing the plain call can report, so it must say the buffer is
        // empty rather than pointing at a key it would then skip.
        Assert.False(machine.KeyboardServices.TryPeek(out _));
    }

    [Fact]
    public void TheBiosSaysWhichExtendedFunctionsItHas()
    {
        // Software asks before using them, and a BIOS that does not answer is
        // taken for one too old to have them. KEYB asks exactly this: getting
        // no answer it decides it is dealing with an 84-key keyboard, and
        // then wipes the byte that said otherwise.
        using Machine machine = PoweredOn();

        machine.Cpu.State.SetReg8(4, 0x09);
        machine.KeyboardServices.Handle(machine.Cpu);

        byte supported = machine.Cpu.State.GetReg8(0);

        // Bits 0-3: functions 10h, 11h, 12h and the typematic call.
        Assert.Equal(0x0F, supported & 0x0F);
    }

    [Fact]
    public void TheKeyboardIdentifiesItselfAsTheHundredAndOneKeyModel()
    {
        // 0xAB41 is what every PC keyboard since the AT reports. Software
        // reads it to tell the models apart.
        using Machine machine = PoweredOn();

        machine.Cpu.State.SetReg8(4, 0x0A);
        machine.KeyboardServices.Handle(machine.Cpu);

        Assert.Equal(0xAB41, machine.Cpu.State.Bx);
    }

    [Fact]
    public void TheExtendedStatusCallReportsBothBytes()
    {
        // The second byte distinguishes right Alt from left Alt and right
        // Ctrl from left Ctrl, which the single-byte call cannot express.
        using Machine machine = PoweredOn();

        machine.Bda.KeyboardFlags = 0x0F;
        machine.Bda.KeyboardFlags2 = 0x0A;

        machine.Cpu.State.SetReg8(4, 0x12);
        machine.KeyboardServices.Handle(machine.Cpu);

        Assert.Equal(0x0F, machine.Cpu.State.GetReg8(0));   // AL
        Assert.Equal(0x0A, machine.Cpu.State.GetReg8(4));   // AH
    }
}
