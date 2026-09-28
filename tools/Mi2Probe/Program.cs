using System.Text;
using Aurora386.Core;
using Aurora386.Core.Cpu;
using Aurora386.Core.Devices.Storage;

// Run Monkey Island 2 under REAL MS-DOS 6.22 in this emulator, and watch the
// one word the game checks before it decides whether to run.
//
// WHY THIS EXISTS. The Aurora OS MS-DOS subsystem gets the game as far as a
// check of SS:[0x2296] against 0xD6D6, which fails, and the game exits with
// code 255 having drawn nothing. Nothing in the executable or its overlays
// ever writes that word, so either something outside the program writes it
// or the check is reached by a path a correct DOS does not take.
//
// GUESSING WHICH HAS COST SEVERAL HOURS. This answers it by measurement:
// the same file, the same emulator, a DOS that is known to run it. Whatever
// differs between the two runs is what Aurora OS is doing wrong.
//
// The source image is never written: everything happens on a copy.

if (args.Length < 2 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("usage: Mi2Probe <DOS image with MI2 installed> <output folder>");
    return 2;
}

string output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);

string copy = Path.Combine(Path.GetTempPath(), $"aurora-mi2-{Guid.NewGuid():N}.img");
File.Copy(args[0], copy);

try
{
    using var machine = new Machine();
    // THE DEFAULT PROCESSOR, not a Pentium.
    //
    // Aurora OS needs a Pentium because Limine uses RDTSC, and its harness
    // sets one. This image is a DOS 6.22 installation configured for the
    // emulator's default, and asking for a different processor stopped it
    // booting at all -- measured as a boot sector looping at 0000:7CF5.
    machine.AttachDisk(DiskImage.OpenHardDisk(copy));

    var typed = new StringBuilder();

    // EVERY DOS CALL THE GAME MAKES, once the game is the thing running.
    //
    // This is the comparison the whole probe exists for: Aurora OS's monitor
    // logs the calls it serves, and a call the real DOS answers that ours
    // never sees -- or answers differently -- is where the two diverge.
    //
    // Recorded only while CS is the game's, because DOS and COMMAND.COM make
    // their own calls constantly and burying the game's in theirs would make
    // the list unreadable.
    var calls = new List<string>();
    bool watching = false;

    machine.Cpu.SoftwareInterruptIssued += vector =>
    {
        var st = machine.Cpu.State;

        // FROM THE MOMENT THE TYPING STARTS, not from a segment learned
        // later: by the time the first sample is taken the game has already
        // been running for millions of instructions, so a filter that waits
        // to learn its segment misses exactly the startup calls that matter.
        //
        // COMMAND.COM's own calls come through too, and that is the price of
        // catching the game's first ones. They are told apart by CS.
        // ONLY THE GAME'S OWN CALLS. COMMAND.COM and the BIOS make hundreds
        // while the game is being loaded, and they drown the ones that
        // matter -- measured: four thousand recorded, none of them the
        // game's.
        //
        // The game loads high, above everything DOS keeps in low memory, so
        // its segment is the one test that separates them without knowing
        // the number in advance.
        if (!watching || st.Cs.Selector < 0x1500 || st.Cs.Selector >= 0xF000)
            return;
        if (calls.Count >= 2000) return;

        calls.Add($"cs={st.Cs.Selector:X4} int {vector:X2} ax={st.Eax & 0xFFFF:X4} "
                + $"bx={st.Ebx & 0xFFFF:X4} cx={st.Ecx & 0xFFFF:X4} "
                + $"dx={st.Edx & 0xFFFF:X4}");
    };

    void Run(long count)
    {
        // IN ONE CALL, not a loop of small ones.
        //
        // Measured: splitting the run into 500,000-instruction batches left
        // the guest looping in its boot sector after three billion, while a
        // single call boots the same image in four hundred million. The
        // emulator advances its devices per call, and a short call starves
        // the disk controller of the time its transfers need.
        machine.RunInstructions(count);
    }

    void Key(byte code)
    {
        // THE SAME TIMINGS THE WORKING PROBE USES, and they are not
        // arbitrary: DOS reads the keyboard through an interrupt handler and
        // its own buffer, and a press that is released too soon is a key the
        // guest never sees. Measured: at 400,000 instructions per step the
        // prompt stayed empty -- every keystroke was lost.
        machine.Keyboard.PushScanCode(code);
        Run(1_000_000);
        machine.Keyboard.PushScanCode((byte)(code | 0x80));
        Run(4_000_000);
    }

    // Scan codes for the characters this probe needs to type. Written out
    // rather than computed: a wrong entry types a different command and the
    // run looks like a game that refused to start.
    var codes = new Dictionary<char, byte>
    {
        ['a'] = 0x1E, ['b'] = 0x30, ['c'] = 0x2E, ['d'] = 0x20, ['e'] = 0x12,
        ['f'] = 0x21, ['g'] = 0x22, ['h'] = 0x23, ['i'] = 0x17, ['j'] = 0x24,
        ['k'] = 0x25, ['l'] = 0x26, ['m'] = 0x32, ['n'] = 0x31, ['o'] = 0x18,
        ['p'] = 0x19, ['q'] = 0x10, ['r'] = 0x13, ['s'] = 0x1F, ['t'] = 0x14,
        ['u'] = 0x16, ['v'] = 0x2F, ['w'] = 0x11, ['x'] = 0x2D, ['y'] = 0x15,
        ['z'] = 0x2C, ['0'] = 0x0B, ['1'] = 0x02, ['2'] = 0x03, ['3'] = 0x04,
        [' '] = 0x39, ['\\'] = 0x2B, ['.'] = 0x34, [':'] = 0x27, ['\r'] = 0x1C,
    };

    void Type(string text)
    {
        foreach (char c in text)
        {
            if (!codes.TryGetValue(char.ToLowerInvariant(c), out byte code))
                throw new InvalidOperationException($"no scan code for '{c}'");
            Key(code);
        }
        typed.Append(text);
    }

    void Screen(string when)
    {
        // WHAT THE SCREEN SAYS, which is the only way to find out whether the
        // typing arrived. A probe that types into a guest and never looks is
        // a probe that cannot tell "the command ran and failed" from "the
        // command was never typed".
        Console.WriteLine($"--- screen: {when} ---");
        for (int row = 0; row < 25; row++)
        {
            var line = new char[80];
            for (int column = 0; column < 80; column++)
            {
                byte value = machine.Memory.ReadByte(
                        (uint)(0xB8000 + (row * 80 + column) * 2));
                line[column] = value is >= 32 and < 127 ? (char)value : ' ';
            }
            string text = new string(line).TrimEnd();
            if (text.Length > 0) Console.WriteLine(text);
        }
    }

    void Report(string when)
    {
        var s = machine.Cpu.State;
        Console.WriteLine($"{when}: cs:ip={s.Cs.Selector:X4}:{s.Eip:X4} "
                        + $"ss={s.Ss.Selector:X4} ds={s.Ds.Selector:X4}");

        // The word the game checks, read through the CURRENT SS -- the game
        // changes SS during startup, so a fixed linear address watches two
        // different places and calls the difference a change.
        uint at = ((uint)s.Ss.Selector << 4) + 0x2296u;
        int lo = machine.Cpu.PeekLinear(at);
        int hi = machine.Cpu.PeekLinear(at + 1);
        if (lo >= 0 && hi >= 0)
            Console.WriteLine($"    ss:2296 = {(hi << 8) | lo:X4} (wanted D6D6) at linear {at:X5}");
    }

    // Let DOS boot and reach its prompt.
    // MS-DOS boots slowly on a 25 MHz 386: measured, 600 million
    // instructions leaves it still in the BIOS.
    Run(3_000_000_000);
    Report("at the prompt");
    Screen("at the prompt");

    // NO BACKSLASH AND NO COLON, which is not fussiness: this image has an
    // Italian keyboard layout, where both are shifted or AltGr keys with
    // scan codes that differ from the US layout. A wrong code types a
    // different command and the run reads as a game that refused to start.
    //
    // "cd mi2" from the root reaches the same place as "cd \mi2", and the
    // drive is already C.
    Type("cd mi2\r");
    watching = true;
    Type("monkey2 v\r");

    // And watch the word while the game starts.
    // SAMPLED CLOSE TOGETHER AND EARLY, because the game may run for a
    // short time and exit: samples taken every 300 million instructions
    // catch the machine after it has already finished, which reads as a game
    // that never ran.
    for (int i = 0; i < 20; i++)
    {
        Run(20_000_000);

        int drawn = 0;
        for (uint j = 0; j < 64000; j += 97)
            if (machine.Cpu.PeekLinear(0xA0000u + j) > 0) drawn++;

        var st = machine.Cpu.State;
        uint at = ((uint)st.Ss.Selector << 4) + 0x2296u;
        int lo = machine.Cpu.PeekLinear(at), hi = machine.Cpu.PeekLinear(at + 1);

        Console.WriteLine($"sample {i}: ss={st.Ss.Selector:X4} "
                        + $"cs:ip={st.Cs.Selector:X4}:{st.Eip:X4} "
                        + $"mode={machine.VideoRegisters.VideoMode:X2} "
                        + $"drawn={drawn} "
                        + $"ss:2296={(hi << 8) | lo:X4}");
    }

    Screen("at the end");

    // AND WHETHER ANYTHING WAS DRAWN, which the text screen cannot show once
    // the game has switched to mode 13h: video memory then lives at 0xA0000
    // and holds pixels, not characters.
    {
        int drawn = 0;
        for (uint i = 0; i < 64000; i += 97)
        {
            int b = machine.Cpu.PeekLinear(0xA0000u + i);
            if (b > 0) drawn++;
        }
        Console.WriteLine($"video samples not blank: {drawn} of 660");
        Console.WriteLine($"BIOS video mode = {machine.VideoRegisters.VideoMode:X2}");
    }

    Console.WriteLine($"--- {calls.Count} DOS calls from the game ---");
    foreach (var c in calls.Take(200)) Console.WriteLine(c);

    Console.WriteLine($"typed: {typed}");
    return 0;
}
finally
{
    File.Delete(copy);
}
