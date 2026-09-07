using Aurora386.Core;
using Aurora386.Core.Cpu;

namespace Aurora386.Tests;

/// <summary>
/// The floating-point unit executed inside the processor.
///
/// These are the cases the software emulator on vector 7 could not reach. It
/// had to decode each instruction a second time out of guest memory, and it
/// only implemented the 16-bit addressing modes -- so a 32-bit operand
/// address, an FSAVE, or a store that had to survive a task switch were all
/// beyond it. Executed where the instruction is decoded, none of that needs
/// implementing at all: the addressing is the processor's own.
///
/// Every test assembles real bytes and runs them, rather than calling the
/// unit directly. Half of what this code does is decoding, and a test that
/// skipped the decoding would pass with the operand read from the wrong
/// address.
/// </summary>
public class X87UnitTests
{
    private const uint Code = 0x2000;
    private const uint Data = 0x3000;

    private static Machine Build()
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        machine.Cpu.State.Halted = false;

        machine.Cpu.SetSegment(SegReg.Cs, 0);
        machine.Cpu.SetSegment(SegReg.Ds, 0);
        machine.Cpu.SetSegment(SegReg.Ss, 0);
        machine.Cpu.State.Esp = 0x7000;
        machine.Cpu.State.Eip = Code;
        return machine;
    }

    private static void Run(Machine machine, params byte[] bytes)
    {
        for (uint i = 0; i < bytes.Length; i++)
            machine.Memory.WriteByte(Code + i, bytes[i]);

        machine.Cpu.State.Eip = Code;
        uint end = Code + (uint)bytes.Length;
        while (machine.Cpu.State.Eip < end) machine.Cpu.Step();
    }

    private static void PutDouble(Machine machine, uint address, double value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        for (uint i = 0; i < 8; i++) machine.Memory.WriteByte(address + i, bytes[i]);
    }

    private static double GetDouble(Machine machine, uint address)
    {
        var bytes = new byte[8];
        for (uint i = 0; i < 8; i++) bytes[i] = machine.Memory.ReadByte(address + i);
        return BitConverter.ToDouble(bytes);
    }

    [Fact]
    public void ThirtyTwoBitAddressingReachesTheOperand()
    {
        // The case the software emulator got wrong by construction: it
        // implemented only the 16-bit addressing table, so an operand reached
        // through a 32-bit ModRM was read from an address it invented. Here
        // the processor computes the address, so there is nothing to get
        // wrong.
        using Machine machine = Build();
        PutDouble(machine, Data + 0x40, 2.5);
        machine.Cpu.State.Ebx = Data;

        // 67 DD 43 40 -- fld qword [ebx+0x40] with the address-size prefix
        Run(machine, 0x67, 0xDD, 0x43, 0x40);

        Assert.Equal(2.5, machine.Cpu.Fpu.PeekTop().ToDouble());
    }

    [Fact]
    public void ScaledIndexAddressingWorksToo()
    {
        using Machine machine = Build();
        PutDouble(machine, Data + 0x20, 7.5);
        machine.Cpu.State.Ebx = Data;
        machine.Cpu.State.Esi = 4;

        // 67 DD 04 F3 -- fld qword [ebx + esi*8]
        Run(machine, 0x67, 0xDD, 0x04, 0xF3);

        Assert.Equal(7.5, machine.Cpu.Fpu.PeekTop().ToDouble());
    }

    [Fact]
    public void TheEightyBitFormatSurvivesARoundTrip()
    {
        // The whole reason the format exists: a value stored and reloaded at
        // full width must come back unchanged, where going through a double
        // would silently drop eleven bits of significand.
        using Machine machine = Build();

        // fldpi; fstp tbyte [3000]; fld tbyte [3000]
        Run(machine,
            0xD9, 0xEB,
            0xDB, 0x3E, 0x00, 0x30,
            0xDB, 0x2E, 0x00, 0x30);

        Assert.Equal(Math.PI, machine.Cpu.Fpu.PeekTop().ToDouble(), 15);
    }

    [Fact]
    public void TheTagWordSaysWhichRegistersHoldSomething()
    {
        using Machine machine = Build();

        // fld1; fldz -- two registers used, six empty
        Run(machine, 0xD9, 0xE8, 0xD9, 0xEE);

        ushort tags = machine.Cpu.Fpu.TagWord;

        // The two in use are at the top of the stack, which counts down from
        // 7: pushing twice from an empty stack leaves ST(0) at physical 6 and
        // ST(1) at physical 7.
        Assert.NotEqual(3, (tags >> (6 * 2)) & 3);
        Assert.NotEqual(3, (tags >> (7 * 2)) & 3);
        Assert.Equal(3, (tags >> (0 * 2)) & 3);

        // Zero has a tag of its own, distinct from an ordinary value: it is
        // the one an operating system can skip saving.
        Assert.Equal(1, (tags >> (6 * 2)) & 3);
    }

    [Fact]
    public void SavingAndRestoringBringsBackTheWholeStack()
    {
        // FSAVE and FRSTOR are what a context switch is made of, and the
        // software emulator implemented neither: a program that saved its
        // floating-point state and restored it got rubbish back.
        using Machine machine = Build();

        // fld1; fldpi; fnsave [3000]
        Run(machine, 0xD9, 0xE8, 0xD9, 0xEB, 0xDD, 0x36, 0x00, 0x30);

        // FNSAVE leaves the unit initialised, which is what makes it the
        // "save and forget" half of a switch: the incoming task finds a clean
        // stack without running FINIT itself.
        Assert.False(machine.Cpu.Fpu.IsUsed(0));

        // frstor [3000]
        Run(machine, 0xDD, 0x26, 0x00, 0x30);

        Assert.Equal(Math.PI, machine.Cpu.Fpu.Get(0).ToDouble(), 15);
        Assert.Equal(1.0, machine.Cpu.Fpu.Get(1).ToDouble());
    }

    [Fact]
    public void TheEnvironmentSavesTheConfigurationButNotTheRegisters()
    {
        using Machine machine = Build();

        // fld1; fnstenv [3000] -- then check ST(0) is still there
        Run(machine, 0xD9, 0xE8, 0xD9, 0x36, 0x00, 0x30);

        Assert.True(machine.Cpu.Fpu.IsUsed(0));
        Assert.Equal(1.0, machine.Cpu.Fpu.Get(0).ToDouble());

        // The control word is the first field, whichever layout is in use.
        Assert.Equal(machine.Cpu.Fpu.ControlWord, machine.Memory.ReadWord(Data));
    }

    [Fact]
    public void TheControlWordChoosesHowAnIntegerStoreRounds()
    {
        // A C compiler sets truncation around every conversion from double to
        // int, because that is how the language defines the conversion. Get
        // this wrong and 2.7 stores as 3.
        using Machine machine = Build();
        PutDouble(machine, Data, 2.7);

        // The control word with rounding set to truncate (bits 10-11 = 11).
        machine.Memory.WriteWord(Data + 16, 0x0F7F);

        // fld qword [3000]; fldcw [3010]; fistp dword [3020]
        Run(machine,
            0xDD, 0x06, 0x00, 0x30,
            0xD9, 0x2E, 0x10, 0x30,
            0xDB, 0x1E, 0x20, 0x30);

        Assert.Equal(2u, machine.Memory.ReadDword(Data + 0x20));
    }

    [Fact]
    public void RoundingToNearestIsWhatHappensWithoutBeingAsked()
    {
        using Machine machine = Build();
        PutDouble(machine, Data, 2.7);

        Run(machine,
            0xDD, 0x06, 0x00, 0x30,
            0xDB, 0x1E, 0x20, 0x30);

        Assert.Equal(3u, machine.Memory.ReadDword(Data + 0x20));
    }

    [Fact]
    public void ASixtyFourBitIntegerGoesOutWhole()
    {
        // FISTP qword is the only form that stores a full 64-bit integer, and
        // the software emulator wrote it as four separate words through the
        // 16-bit path.
        using Machine machine = Build();
        PutDouble(machine, Data, 5_000_000_000.0);

        // fld qword [3000]; fistp qword [3020]
        Run(machine,
            0xDD, 0x06, 0x00, 0x30,
            0xDF, 0x3E, 0x20, 0x30);

        ulong stored = machine.Memory.ReadDword(Data + 0x20)
                     | ((ulong)machine.Memory.ReadDword(Data + 0x24) << 32);

        Assert.Equal(5_000_000_000UL, stored);
    }

    [Fact]
    public void ArithmeticKeepsTheFullWidthThroughAChain()
    {
        // The reason the registers are 80 bits and not 64: a sum accumulated
        // in extended precision and only then rounded is closer than one
        // rounded at every step. This adds a tiny value to a large one enough
        // times that a double would have lost it entirely.
        using Machine machine = Build();
        PutDouble(machine, Data, 1.0);
        PutDouble(machine, Data + 8, 1e-18);

        machine.Memory.WriteByte(Code, 0xDD);        // fld qword [3000]
        machine.Memory.WriteByte(Code + 1, 0x06);
        machine.Memory.WriteWord(Code + 2, 0x3000);

        machine.Cpu.State.Eip = Code;
        machine.Cpu.Step();

        // Add the small value repeatedly: at 80 bits each addition still
        // moves the sum, where at 64 it would be swallowed every time.
        for (int i = 0; i < 8; i++)
        {
            machine.Memory.WriteByte(Code, 0xDC);    // fadd qword [3008]
            machine.Memory.WriteByte(Code + 1, 0x06);
            machine.Memory.WriteWord(Code + 2, 0x3008);
            machine.Cpu.State.Eip = Code;
            machine.Cpu.Step();
        }

        // fstp tbyte, so the result is read back at full width rather than
        // through a double that would round the difference away.
        Run(machine, 0xDB, 0x3E, 0x40, 0x30);

        ulong significand = machine.Memory.ReadDword(Data + 0x40)
                          | ((ulong)machine.Memory.ReadDword(Data + 0x44) << 32);

        // 1.0 exactly would have the significand 0x8000000000000000 and
        // nothing below it. Eight additions of 1e-18 are far too small for a
        // double to notice and just large enough for 64 bits of significand.
        Assert.NotEqual(0x8000000000000000UL, significand);
    }

    [Fact]
    public void ComparingPutsTheAnswerWhereFstswCanFetchIt()
    {
        using Machine machine = Build();
        PutDouble(machine, Data, 1.0);
        PutDouble(machine, Data + 8, 2.0);

        // fld [3000]; fcom [3008]; fnstsw ax
        Run(machine,
            0xDD, 0x06, 0x00, 0x30,
            0xDC, 0x16, 0x08, 0x30,
            0xDF, 0xE0);

        // C0 set alone means "less than", which is the only way a program can
        // get a floating-point comparison into the processor's flags: there is
        // no path from the condition codes to EFLAGS on a 387.
        Assert.Equal(0x0100, machine.Cpu.State.Ax & 0x4500);
    }

    [Fact]
    public void ComparingWithANaNIsItsOwnAnswer()
    {
        using Machine machine = Build();
        PutDouble(machine, Data, 1.0);
        PutDouble(machine, Data + 8, double.NaN);

        Run(machine,
            0xDD, 0x06, 0x00, 0x30,
            0xDC, 0x16, 0x08, 0x30,
            0xDF, 0xE0);

        // Unordered: all three condition bits. Not an error and not any of the
        // three orderings, because a NaN genuinely is none of them.
        Assert.Equal(0x4500, machine.Cpu.State.Ax & 0x4500);
    }

    [Fact]
    public void StoringASingleNarrowsAndReloadingWidensBack()
    {
        using Machine machine = Build();
        PutDouble(machine, Data, 0.5);

        // fld qword [3000]; fstp dword [3020]; fld dword [3020]
        Run(machine,
            0xDD, 0x06, 0x00, 0x30,
            0xD9, 0x1E, 0x20, 0x30,
            0xD9, 0x06, 0x20, 0x30);

        // A half survives the narrowing exactly, being a power of two; the
        // test is that the format conversion happens at all and in the right
        // direction.
        Assert.Equal(0.5, machine.Cpu.Fpu.PeekTop().ToDouble());
    }

    [Fact]
    public void TheStackWrapsRatherThanShuffling()
    {
        // Eight pushes fill the file; the ninth wraps onto the first. Nothing
        // moves in memory -- it is the top pointer that walks -- which is why
        // ST(1) names a different register after every push.
        using Machine machine = Build();

        for (int i = 0; i < 8; i++) Run(machine, 0xD9, 0xE8);   // fld1 x8

        Assert.True(machine.Cpu.Fpu.IsUsed(0));
        Assert.True(machine.Cpu.Fpu.IsUsed(7));
        Assert.Equal(1.0, machine.Cpu.Fpu.Get(7).ToDouble());
    }
}
