using Aurora386.Core;
using Aurora386.Core.Cpu;

namespace Aurora386.Tests;

/// <summary>
/// Tests for the software coprocessor on interrupt 7.
///
/// Each test assembles real instruction bytes and lets the machine fault into
/// the emulator, rather than calling it directly: the decoding is half of what
/// this code does, and a test that skipped it would pass with the operand read
/// from the wrong address.
/// </summary>
public class FpuEmulatorTests
{
    private const uint Code = 0x2000;
    private const uint Data = 0x3000;

    private static Machine Build()
    {
        var machine = new Machine();
        machine.RunInstructions(3000);        // let the POST install the vectors
        machine.Cpu.State.Halted = false;

        machine.Cpu.SetSegment(SegReg.Cs, 0);
        machine.Cpu.SetSegment(SegReg.Ds, 0);
        machine.Cpu.SetSegment(SegReg.Ss, 0);
        machine.Cpu.State.Esp = 0x7000;
        machine.Cpu.State.Eip = Code;
        return machine;
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

    /// <summary>Runs the bytes given, stopping once they have all been executed.</summary>
    private static void Run(Machine machine, params byte[] program)
    {
        for (uint i = 0; i < program.Length; i++)
            machine.Memory.WriteByte(Code + i, program[i]);

        uint end = Code + (uint)program.Length;
        for (int step = 0; step < 400 && machine.Cpu.State.Eip < end; step++)
            machine.Cpu.Step();
    }

    /// <summary>FLD qword [0x3000] -- loads the first operand onto the stack.</summary>
    private static readonly byte[] LoadFirst = { 0xDD, 0x06, 0x00, 0x30 };

    /// <summary>FSTP qword [0x3010] -- stores the result and pops.</summary>
    private static readonly byte[] StoreResult = { 0xDD, 0x1E, 0x10, 0x30 };

    private static double Arithmetic(byte modrm, double a, double b)
    {
        using Machine machine = Build();
        PutDouble(machine, Data, a);
        PutDouble(machine, Data + 8, b);

        Run(machine,
            LoadFirst[0], LoadFirst[1], LoadFirst[2], LoadFirst[3],
            0xDC, modrm, 0x08, 0x30,
            StoreResult[0], StoreResult[1], StoreResult[2], StoreResult[3]);

        return GetDouble(machine, Data + 0x10);
    }

    [Fact]
    public void AdditionOfTwoDoublesInMemory()
        => Assert.Equal(6.5, Arithmetic(0x06, 2.5, 4.0));

    [Fact]
    public void Multiplication()
        => Assert.Equal(10.0, Arithmetic(0x0E, 2.5, 4.0));

    [Fact]
    public void Subtraction()
        => Assert.Equal(-1.5, Arithmetic(0x26, 2.5, 4.0));

    [Fact]
    public void Division()
        => Assert.Equal(0.625, Arithmetic(0x36, 2.5, 4.0));

    /// <summary>
    /// The reversed forms are not decoration: subtraction and division do not
    /// commute, and a mix-up between the two directions is the classic way to
    /// get a sign wrong everywhere without any instruction appearing to fail.
    /// </summary>
    [Fact]
    public void ReverseSubtractionTakesTheOperandsTheOtherWayRound()
        => Assert.Equal(1.5, Arithmetic(0x2E, 2.5, 4.0));

    [Fact]
    public void ReverseDivisionTakesTheOperandsTheOtherWayRound()
        => Assert.Equal(1.6, Arithmetic(0x3E, 2.5, 4.0));

    [Fact]
    public void TheInstructionPointerStepsOverWhatWasExecuted()
    {
        using Machine machine = Build();
        PutDouble(machine, Data, 1.0);

        Run(machine, 0xDD, 0x06, 0x00, 0x30);

        // Four bytes of instruction. This mattered acutely when the escapes
        // were served by a fault handler: the handler had to step the saved
        // IP over the instruction itself, and a machine that forgot sat on
        // the same instruction for ever. Executed in the processor there is
        // nothing to step -- EIP has already moved past the operand -- which
        // is one whole class of bug that stops existing.
        Assert.Equal(Code + 4, machine.Cpu.State.Eip);
        Assert.Equal(1, machine.Cpu.Fpu.Executed);
    }

    [Fact]
    public void ComparingReportsTheOrderingInTheStatusWord()
    {
        using Machine machine = Build();
        PutDouble(machine, Data, 1.0);
        PutDouble(machine, Data + 8, 2.0);

        // FLD [3000]; FCOM [3008]; FSTSW AX
        Run(machine,
            0xDD, 0x06, 0x00, 0x30,
            0xDC, 0x16, 0x08, 0x30,
            0xDF, 0xE0);

        // C0 is bit 8, and it is the bit that says "less than".
        Assert.Equal(0x0100, machine.Cpu.State.Ax & 0x4500);
    }

    [Fact]
    public void ComparingEqualValuesSetsTheEqualBitInstead()
    {
        using Machine machine = Build();
        PutDouble(machine, Data, 3.0);
        PutDouble(machine, Data + 8, 3.0);

        Run(machine,
            0xDD, 0x06, 0x00, 0x30,
            0xDC, 0x16, 0x08, 0x30,
            0xDF, 0xE0);

        // C3 is bit 14.
        Assert.Equal(0x4000, machine.Cpu.State.Ax & 0x4500);
    }

    [Fact]
    public void AnIntegerIsLoadedAndStoredBackAsOne()
    {
        using Machine machine = Build();
        machine.Memory.WriteWord(Data, 7);
        machine.Memory.WriteWord(Data + 2, 0);

        // FILD dword [3000]; FISTP dword [3010]
        Run(machine, 0xDB, 0x06, 0x00, 0x30,
                     0xDB, 0x1E, 0x10, 0x30);

        Assert.Equal(7, machine.Memory.ReadWord(Data + 0x10));
    }

    [Fact]
    public void TheStackHoldsSeveralValuesAtOnce()
    {
        using Machine machine = Build();
        PutDouble(machine, Data, 10.0);
        PutDouble(machine, Data + 8, 3.0);

        // Load both, then subtract the top from the one beneath and pop:
        // FLD [3000]; FLD [3008]; FSUBP ST(1),ST -- leaving 10 - 3.
        Run(machine,
            0xDD, 0x06, 0x00, 0x30,
            0xDD, 0x06, 0x08, 0x30,
            0xDE, 0xE9,
            StoreResult[0], StoreResult[1], StoreResult[2], StoreResult[3]);

        Assert.Equal(7.0, GetDouble(machine, Data + 0x10));
    }

    [Fact]
    public void TheConstantsAreTheOnesTheCoprocessorProvides()
    {
        using Machine machine = Build();

        // FLDPI; FSTP qword [3010]
        Run(machine, 0xD9, 0xEB,
                     StoreResult[0], StoreResult[1], StoreResult[2], StoreResult[3]);

        Assert.Equal(Math.PI, GetDouble(machine, Data + 0x10), 12);
    }

    [Fact]
    public void SquareRoot()
    {
        using Machine machine = Build();
        PutDouble(machine, Data, 81.0);

        Run(machine, 0xDD, 0x06, 0x00, 0x30,
                     0xD9, 0xFA,
                     StoreResult[0], StoreResult[1], StoreResult[2], StoreResult[3]);

        Assert.Equal(9.0, GetDouble(machine, Data + 0x10));
    }

    [Fact]
    public void ASegmentOverridePrefixIsHonouredWhenFindingTheOperand()
    {
        using Machine machine = Build();

        // Put the value somewhere ES reaches and DS does not.
        machine.Cpu.SetSegment(SegReg.Es, 0x0400);   // ES:0 is linear 0x4000
        PutDouble(machine, 0x4000, 12.5);
        PutDouble(machine, Data, -1.0);              // what DS would have found

        // ES: FLD qword [0x0000]; FSTP qword [3010]
        Run(machine, 0x26, 0xDD, 0x06, 0x00, 0x00,
                     StoreResult[0], StoreResult[1], StoreResult[2], StoreResult[3]);

        Assert.Equal(12.5, GetDouble(machine, Data + 0x10));
    }

    // ---- what makes it a 387 rather than an 8087 ---------------------------
    //
    // The transcendentals were the 387's headline addition. Each is checked
    // through its real encoding, because the reg and rm fields of the D9 group
    // are easy to transpose and a transposition puts a cosine where a square
    // root belongs -- both run, and only the answer is wrong.

    /// <summary>Loads one double, runs the given D9 form, stores the result.</summary>
    private static double OneOperand(byte modrm, double input)
    {
        using Machine machine = Build();
        PutDouble(machine, Data, input);

        Run(machine,
            0xDD, 0x06, 0x00, 0x30,
            0xD9, modrm,
            0xDD, 0x1E, 0x10, 0x30);

        return GetDouble(machine, Data + 0x10);
    }

    [Fact]
    public void Sine() => Assert.Equal(1.0, OneOperand(0xFE, Math.PI / 2), 12);

    [Fact]
    public void Cosine() => Assert.Equal(1.0, OneOperand(0xFF, 0.0), 12);

    [Fact]
    public void Tangent()
    {
        // FPTAN leaves the tangent under a pushed 1.0, so the value wanted is
        // ST(1). Popping the 1 first is what the trailing FSTP does.
        using Machine machine = Build();
        PutDouble(machine, Data, Math.PI / 4);

        Run(machine,
            0xDD, 0x06, 0x00, 0x30,
            0xD9, 0xF2,                 // FPTAN
            0xDD, 0xD8,                 // FSTP ST(0) -- drop the pushed 1.0
            0xDD, 0x1E, 0x10, 0x30);

        Assert.Equal(1.0, GetDouble(machine, Data + 0x10), 12);
    }

    [Fact]
    public void RoundingToTheNearestInteger()
        => Assert.Equal(3.0, OneOperand(0xFC, 2.6));

    [Fact]
    public void TwoRaisedToThePowerMinusOne()
        => Assert.Equal(1.0, OneOperand(0xF0, 1.0), 12);   // 2^1 - 1

    [Fact]
    public void ArcTangentTakesTwoOperandsAndPops()
    {
        using Machine machine = Build();
        PutDouble(machine, Data, 1.0);          // becomes ST(1)
        PutDouble(machine, Data + 8, 1.0);      // becomes ST(0)

        Run(machine,
            0xDD, 0x06, 0x00, 0x30,
            0xDD, 0x06, 0x08, 0x30,
            0xD9, 0xF3,                          // FPATAN
            0xDD, 0x1E, 0x10, 0x30);

        Assert.Equal(Math.PI / 4, GetDouble(machine, Data + 0x10), 12);
    }

    [Fact]
    public void LogarithmScaledByTheSecondOperand()
    {
        using Machine machine = Build();
        PutDouble(machine, Data, 3.0);          // ST(1), the multiplier
        PutDouble(machine, Data + 8, 8.0);      // ST(0), whose log2 is 3

        Run(machine,
            0xDD, 0x06, 0x00, 0x30,
            0xDD, 0x06, 0x08, 0x30,
            0xD9, 0xF1,                          // FYL2X
            0xDD, 0x1E, 0x10, 0x30);

        Assert.Equal(9.0, GetDouble(machine, Data + 0x10), 12);
    }

    [Fact]
    public void ScalingByAPowerOfTwo()
    {
        using Machine machine = Build();
        PutDouble(machine, Data, 3.0);          // ST(1), the exponent
        PutDouble(machine, Data + 8, 5.0);      // ST(0), the value

        Run(machine,
            0xDD, 0x06, 0x00, 0x30,
            0xDD, 0x06, 0x08, 0x30,
            0xD9, 0xFD,                          // FSCALE
            0xDD, 0x1E, 0x10, 0x30);

        Assert.Equal(40.0, GetDouble(machine, Data + 0x10));   // 5 * 2^3
    }

    [Fact]
    public void SineAndCosineTogetherLeaveBothOnTheStack()
    {
        using Machine machine = Build();
        PutDouble(machine, Data, 0.0);

        // FSINCOS on zero gives sine 0 and cosine 1; the cosine is on top.
        Run(machine,
            0xDD, 0x06, 0x00, 0x30,
            0xD9, 0xFB,
            0xDD, 0x1E, 0x10, 0x30,     // store cosine
            0xDD, 0x1E, 0x18, 0x30);    // store sine

        Assert.Equal(1.0, GetDouble(machine, Data + 0x10), 12);
        Assert.Equal(0.0, GetDouble(machine, Data + 0x18), 12);
    }

    [Fact]
    public void TheStackPointerCanBeMovedByHand()
    {
        using Machine machine = Build();
        PutDouble(machine, Data, 5.0);

        // Load 5, step the top back and forth, and it is still ST(0).
        Run(machine,
            0xDD, 0x06, 0x00, 0x30,
            0xD9, 0xF6,                 // FDECSTP
            0xD9, 0xF7,                 // FINCSTP
            0xDD, 0x1E, 0x10, 0x30);

        Assert.Equal(5.0, GetDouble(machine, Data + 0x10));
    }

    [Fact]
    public void TheEightyBitFormatSurvivesARoundTrip()
    {
        using Machine machine = Build();
        PutDouble(machine, Data, 1234.5625);

        // FLD qword; FSTP tbyte; FLD tbyte; FSTP qword
        Run(machine,
            0xDD, 0x06, 0x00, 0x30,
            0xDB, 0x3E, 0x20, 0x30,
            0xDB, 0x2E, 0x20, 0x30,
            0xDD, 0x1E, 0x10, 0x30);

        Assert.Equal(1234.5625, GetDouble(machine, Data + 0x10));
    }

    [Fact]
    public void TheEightyBitFormatCarriesNegativeValues()
    {
        using Machine machine = Build();
        PutDouble(machine, Data, -0.15625);

        Run(machine,
            0xDD, 0x06, 0x00, 0x30,
            0xDB, 0x3E, 0x20, 0x30,
            0xDB, 0x2E, 0x20, 0x30,
            0xDD, 0x1E, 0x10, 0x30);

        Assert.Equal(-0.15625, GetDouble(machine, Data + 0x10));
    }

    [Fact]
    public void ExaminingAValueReportsWhatKindItIs()
    {
        using Machine machine = Build();
        PutDouble(machine, Data, 0.0);

        // FLD 0.0; FXAM; FSTSW AX -- C3 alone means zero.
        Run(machine,
            0xDD, 0x06, 0x00, 0x30,
            0xD9, 0xE5,
            0xDF, 0xE0);

        Assert.Equal(0x4000, machine.Cpu.State.Ax & 0x4500);
    }

    [Fact]
    public void TheRemainderInstructionsAgreeOnAnExactCase()
    {
        using Machine machine = Build();
        PutDouble(machine, Data, 3.0);           // ST(1), the divisor
        PutDouble(machine, Data + 8, 17.0);      // ST(0)

        Run(machine,
            0xDD, 0x06, 0x00, 0x30,
            0xDD, 0x06, 0x08, 0x30,
            0xD9, 0xF8,                          // FPREM
            0xDD, 0x1E, 0x10, 0x30);

        Assert.Equal(2.0, GetDouble(machine, Data + 0x10), 12);
    }

    // ---- the width the registers really carry ------------------------------

    [Fact]
    public void TheRegisterStackHoldsMoreThanADoubleCan()
    {
        using Machine machine = Build();

        // Build 1 + 2^-60 in the registers: FLD1, then load the small value
        // and add. A double cannot represent the sum -- it rounds back to 1 --
        // so if the stack were double-backed, subtracting the small value
        // again would leave exactly 1 and the difference below would be zero.
        PutDouble(machine, Data, Math.Pow(2.0, -60));

        Run(machine,
            0xD9, 0xE8,                  // FLD1
            0xDD, 0x06, 0x00, 0x30,      // FLD qword [3000]
            0xDE, 0xC1,                  // FADDP ST(1),ST  -> 1 + 2^-60
            0xD9, 0xE8,                  // FLD1
            0xDE, 0xE9,                  // FSUBP ST(1),ST  -> (1+2^-60) - 1
            0xDD, 0x1E, 0x10, 0x30);     // FSTP qword [3010]

        // The answer is the small value itself, which only survives if the
        // register held all 64 bits of significand.
        double result = GetDouble(machine, Data + 0x10);
        Assert.Equal(Math.Pow(2.0, -60), result);
    }

    [Fact]
    public void AnEightyBitValueFromMemoryKeepsEveryBit()
    {
        using Machine machine = Build();

        // A significand with its lowest bit set: that bit is below what a
        // double can hold, so a round trip through one would clear it.
        for (uint i = 0; i < 4; i++)
            machine.Memory.WriteWord(Data + 0x20 + i * 2, i == 0 ? (ushort)0x0001 : (ushort)0x0000);
        machine.Memory.WriteWord(Data + 0x20, 0x0001);
        machine.Memory.WriteWord(Data + 0x22, 0x0000);
        machine.Memory.WriteWord(Data + 0x24, 0x0000);
        machine.Memory.WriteWord(Data + 0x26, 0x8000);   // top bit set
        machine.Memory.WriteWord(Data + 0x28, 16383);    // exponent for [1,2)

        // FLD tbyte [3020]; FSTP tbyte [3030]
        Run(machine, 0xDB, 0x2E, 0x20, 0x30,
                     0xDB, 0x3E, 0x30, 0x30);

        Assert.Equal(0x0001, machine.Memory.ReadWord(Data + 0x30));
        Assert.Equal(0x8000, machine.Memory.ReadWord(Data + 0x36));
        Assert.Equal(16383, machine.Memory.ReadWord(Data + 0x38));
    }
}
