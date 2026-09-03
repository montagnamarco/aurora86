using Aurora386.Core;
using Aurora386.Gui.Emulation;

namespace Aurora386.Tests;

/// <summary>
/// Typing a character has to produce that character in the guest, whatever
/// keyboard the user is sitting at.
///
/// Scancodes name key positions, not characters, and the guest decides what a
/// position means using the layout it has loaded -- the American one, unless
/// something changed it. So on any other layout the position the user pressed
/// and the character they meant come apart: an Italian keyboard puts the colon
/// on the full stop's key, which the guest reads with shift as a greater-than
/// sign. Typing "C:" produced "C&gt;".
///
/// These tests go the whole way round: character in, scancodes through the
/// keyboard controller and the BIOS, character out.
/// </summary>
public class KeyboardLayoutTests
{
    /// <summary>
    /// Types a character the way the frontend does -- press shift if the
    /// guest's layout needs it, press and release the key, let shift go --
    /// and returns what the BIOS made of it.
    /// </summary>
    private static char TypeAndRead(Machine machine, char character)
    {
        (byte code, bool shift) = UsKeyboardLayout.KeyForCharacter(character);
        Assert.NotEqual(0, code);

        const byte LeftShift = 0x2A;

        void Deliver(byte scanCode)
        {
            machine.Keyboard.PushScanCode(scanCode);
            machine.KeyboardServices.HandleIrq(machine.Cpu);
        }

        if (shift) Deliver(LeftShift);
        Deliver(code);
        Deliver((byte)(code | 0x80));
        if (shift) Deliver(LeftShift | 0x80);

        // Take the first keystroke that carries a character. Shift produces
        // none, so on a shifted key it queues nothing and the letter follows;
        // reading blindly would hand back the modifier instead.
        while (machine.KeyboardServices.TryDequeue(out ushort key))
        {
            char produced = (char)(key & 0xFF);
            if (produced != '\0') return produced;
        }

        Assert.Fail($"nothing that carries a character was queued for '{character}'");
        return '\0';
    }

    private static Machine PoweredOn()
    {
        var machine = new Machine();
        machine.RunInstructions(200);
        return machine;
    }

    [Fact]
    public void TheColonArrivesAsAColon()
    {
        // The one that started this: on an Italian keyboard the colon shares
        // a key with the full stop, and sending that position gave the guest
        // a greater-than sign instead. Typing a drive letter was impossible.
        using Machine machine = PoweredOn();

        Assert.Equal(':', TypeAndRead(machine, ':'));
    }

    [Fact]
    public void TheCharactersAPathNeedsAllArrive()
    {
        // Between them these are every character in "C:\DOS\FILE.TXT", which
        // is the smallest thing a user has to be able to type.
        using Machine machine = PoweredOn();

        foreach (char expected in @"C:\DOS\FILE.TXT")
            Assert.Equal(expected, TypeAndRead(machine, expected));
    }

    [Theory]
    [InlineData('a')]
    [InlineData('z')]
    [InlineData('A')]
    [InlineData('Z')]
    [InlineData('0')]
    [InlineData('9')]
    [InlineData(' ')]
    [InlineData('.')]
    [InlineData(',')]
    [InlineData(';')]
    [InlineData(':')]
    [InlineData('-')]
    [InlineData('_')]
    [InlineData('=')]
    [InlineData('+')]
    [InlineData('/')]
    [InlineData('?')]
    [InlineData('\\')]
    [InlineData('|')]
    [InlineData('\'')]
    [InlineData('"')]
    [InlineData('[')]
    [InlineData(']')]
    [InlineData('{')]
    [InlineData('}')]
    [InlineData('(')]
    [InlineData(')')]
    [InlineData('!')]
    [InlineData('@')]
    [InlineData('#')]
    [InlineData('$')]
    [InlineData('%')]
    [InlineData('^')]
    [InlineData('&')]
    [InlineData('*')]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData('~')]
    [InlineData('`')]
    public void EveryPrintableCharacterMakesTheRoundTrip(char character)
    {
        // The table used to hold a handful of symbols somebody had run into.
        // The trouble with that is the ones nobody ran into: each was a
        // separate surprise waiting for whoever typed it first. Covering the
        // whole printable range is what removes the category, and this is
        // where that claim is checked.
        using Machine machine = PoweredOn();

        Assert.Equal(character, TypeAndRead(machine, character));
    }

    [Fact]
    public void EveryPrintableAsciiCharacterHasAKey()
    {
        // Nothing printable may be left without a way of typing it.
        for (char c = ' '; c < (char)127; c++)
        {
            (byte code, _) = UsKeyboardLayout.KeyForCharacter(c);
            Assert.True(code != 0, $"no key produces '{c}' (0x{(int)c:X2})");
        }
    }

    [Fact]
    public void ACharacterWithNoKeyIsRefusedRatherThanGuessed()
    {
        // An accented letter cannot be typed on a US layout at all. Returning
        // some nearby key would type the wrong thing silently; saying so lets
        // the caller fall back to the position that was actually pressed.
        (byte code, _) = UsKeyboardLayout.KeyForCharacter('è');

        Assert.Equal(0, code);
    }
}
