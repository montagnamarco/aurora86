using Aurora86.Core;
using Aurora86.Gui.Emulation;

namespace Aurora86.Tests;

/// <summary>
/// The fallback that types a character on a guest using the US layout.
///
/// Scancodes name key positions, and what a position means is decided by the
/// guest, using whatever keyboard layout it has loaded. These tests cover the
/// case where it has loaded none, so the BIOS's built-in US layout applies --
/// which is what the machine looks like before AUTOEXEC.BAT runs, and what it
/// stays like on an English system.
///
/// This path is deliberately not what an ordinary keypress takes. When the
/// guest has loaded a layout of its own, as an Italian MS-DOS does with KEYB,
/// it translates positions itself and the frontend must send the position and
/// keep out of the way -- translating to US first would have the character
/// translated twice. So this covers the keys a PC keyboard has no position
/// for, not everyday typing.
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
