using Aurora86.Core.Cpu;

namespace Aurora86.Tests;

/// <summary>
/// Every conditional branch in existence rests on the flags. A mistake here
/// does not crash anything: it produces a program that takes the wrong branch
/// much later, somewhere entirely unrelated. That is why ADD/SUB/ADC/SBB are
/// checked exhaustively across all 65536 8-bit operand combinations, against
/// expected flags computed independently of the implementation under test.
/// </summary>
public class LazyFlagsTests
{
    private static bool ExpectedParity(uint result)
    {
        int bits = 0;
        for (int i = 0; i < 8; i++) bits += (int)((result >> i) & 1);
        return (bits & 1) == 0;      // PF is set when the number of set bits is even
    }

    [Fact]
    public void ExhaustiveEightBitAdd()
    {
        var f = new LazyFlags();
        for (uint a = 0; a <= 0xFF; a++)
        for (uint b = 0; b <= 0xFF; b++)
        {
            uint result = (a + b) & 0xFF;
            f.Set(FlagOp.Add, a, b, result, 8);

            Assert.Equal(a + b > 0xFF, f.Carry);
            Assert.Equal(result == 0, f.Zero);
            Assert.Equal((result & 0x80) != 0, f.Sign);
            Assert.Equal(ExpectedParity(result), f.Parity);
            Assert.Equal(((a & 0xF) + (b & 0xF)) > 0xF, f.Adjust);

            // Signed overflow: both operands share a sign and the result
            // came out with the opposite one.
            bool overflow = ((sbyte)a > 0 && (sbyte)b > 0 && (sbyte)result < 0)
                         || ((sbyte)a < 0 && (sbyte)b < 0 && (sbyte)result >= 0);
            Assert.Equal(overflow, f.Overflow);
        }
    }

    [Fact]
    public void ExhaustiveEightBitSub()
    {
        var f = new LazyFlags();
        for (uint a = 0; a <= 0xFF; a++)
        for (uint b = 0; b <= 0xFF; b++)
        {
            uint result = (a - b) & 0xFF;
            f.Set(FlagOp.Sub, a, b, result, 8);

            Assert.Equal(a < b, f.Carry);            // carry acts as borrow for subtraction
            Assert.Equal(result == 0, f.Zero);
            Assert.Equal((result & 0x80) != 0, f.Sign);
            Assert.Equal(ExpectedParity(result), f.Parity);
            Assert.Equal((a & 0xF) < (b & 0xF), f.Adjust);

            bool overflow = ((sbyte)a >= 0 && (sbyte)b < 0 && (sbyte)result < 0)
                         || ((sbyte)a < 0 && (sbyte)b > 0 && (sbyte)result >= 0);
            Assert.Equal(overflow, f.Overflow);
        }
    }

    [Fact]
    public void ExhaustiveEightBitAdcWithBothIncomingCarries()
    {
        var f = new LazyFlags();
        for (uint carryIn = 0; carryIn <= 1; carryIn++)
        for (uint a = 0; a <= 0xFF; a++)
        for (uint b = 0; b <= 0xFF; b++)
        {
            uint full = a + b + carryIn;
            uint result = full & 0xFF;
            f.Set(FlagOp.Adc, a, b, result, 8, carryIn != 0);

            Assert.Equal(full > 0xFF, f.Carry);
            Assert.Equal(result == 0, f.Zero);
            Assert.Equal(((a & 0xF) + (b & 0xF) + carryIn) > 0xF, f.Adjust);
        }
    }

    [Fact]
    public void ExhaustiveEightBitSbbWithBothIncomingBorrows()
    {
        var f = new LazyFlags();
        for (uint borrowIn = 0; borrowIn <= 1; borrowIn++)
        for (uint a = 0; a <= 0xFF; a++)
        for (uint b = 0; b <= 0xFF; b++)
        {
            uint result = (a - b - borrowIn) & 0xFF;
            f.Set(FlagOp.Sbb, a, b, result, 8, borrowIn != 0);

            Assert.Equal(a < b + borrowIn, f.Carry);
            Assert.Equal(result == 0, f.Zero);
        }
    }

    [Fact]
    public void IncAndDecPreserveCarry()
    {
        // This is what makes them usable inside multi-precision addition
        // loops: the running carry survives the loop counter's own INC/DEC.
        var f = new LazyFlags();
        f.Carry = true;
        f.Set(FlagOp.Inc, 5, 1, 6, 8);
        Assert.True(f.Carry);
        Assert.False(f.Zero);

        f.Carry = false;
        f.Set(FlagOp.Dec, 1, 1, 0, 8);
        Assert.False(f.Carry);
        Assert.True(f.Zero);
    }

    [Fact]
    public void IncSignalsOverflowOnlyAtThePositiveLimit()
    {
        var f = new LazyFlags();
        f.Set(FlagOp.Inc, 0x7F, 1, 0x80, 8);
        Assert.True(f.Overflow);        // 127 -> -128
        Assert.True(f.Sign);

        f.Set(FlagOp.Inc, 0x10, 1, 0x11, 8);
        Assert.False(f.Overflow);
    }

    [Fact]
    public void DecSignalsOverflowOnlyAtTheNegativeLimit()
    {
        var f = new LazyFlags();
        f.Set(FlagOp.Dec, 0x80, 1, 0x7F, 8);
        Assert.True(f.Overflow);        // -128 -> 127
        Assert.False(f.Sign);
    }

    [Fact]
    public void LogicOperationsClearCarryAndOverflow()
    {
        var f = new LazyFlags();
        f.Carry = true;
        f.Overflow = true;
        f.Set(FlagOp.Logic, 0xF0, 0x0F, 0x00, 8);

        Assert.False(f.Carry);
        Assert.False(f.Overflow);
        Assert.True(f.Zero);
        Assert.True(f.Parity);
    }

    [Fact]
    public void ParityLooksOnlyAtTheLowEightBits()
    {
        // True for 16- and 32-bit operands as well: PF is defined over the
        // low byte only, a quirk inherited straight from the 8080.
        var f = new LazyFlags();
        f.Set(FlagOp.Logic, 0, 0, 0xFF00, 16);
        Assert.True(f.Parity);          // low byte 0x00: zero set bits, even

        f.Set(FlagOp.Logic, 0, 0, 0xFF01, 16);
        Assert.False(f.Parity);         // low byte 0x01: one set bit, odd
    }

    [Fact]
    public void ControlFlagsAreIndependentOfTheArithmeticOnes()
    {
        var f = new LazyFlags();
        f.Interrupt = true;
        f.Direction = true;

        // An arithmetic operation must not disturb IF or DF.
        f.Set(FlagOp.Add, 0xFF, 0xFF, 0xFE, 8);
        Assert.True(f.Carry);
        Assert.True(f.Interrupt);
        Assert.True(f.Direction);
    }

    [Fact]
    public void SetValueDiscardsThePendingOperation()
    {
        var f = new LazyFlags();
        f.Set(FlagOp.Add, 0xFF, 0xFF, 0xFE, 8);   // would evaluate to carry set
        f.SetValue((uint)Flags.Zero);              // POPF discards the pending op

        Assert.False(f.Carry);
        Assert.True(f.Zero);
    }

    [Fact]
    public void ThirtyTwoBitOperationsComputeCarryAndOverflow()
    {
        var f = new LazyFlags();
        f.Set(FlagOp.Add, 0xFFFFFFFF, 1, 0, 32);
        Assert.True(f.Carry);
        Assert.True(f.Zero);
        Assert.False(f.Overflow);       // -1 + 1 = 0: no signed overflow

        f.Set(FlagOp.Add, 0x7FFFFFFF, 1, 0x80000000, 32);
        Assert.False(f.Carry);
        Assert.True(f.Overflow);        // largest positive value plus one
        Assert.True(f.Sign);
    }

    [Fact]
    public void ShlTakesCarryFromTheLastBitShiftedOut()
    {
        var f = new LazyFlags();
        f.Set(FlagOp.Shl, 0x80, 1, 0x00, 8);
        Assert.True(f.Carry);           // bit 7 was shifted out
        Assert.True(f.Zero);

        f.Set(FlagOp.Shl, 0x40, 1, 0x80, 8);
        Assert.False(f.Carry);
        Assert.True(f.Overflow);        // the sign bit changed
    }

    [Fact]
    public void ShrAndSarDifferOnTheSignBit()
    {
        var f = new LazyFlags();
        f.Set(FlagOp.Shr, 0x81, 1, 0x40, 8);
        Assert.True(f.Carry);           // bit 0 was shifted out
        Assert.True(f.Overflow);        // for SHR by 1, OF is the original sign bit

        f.Set(FlagOp.Sar, 0x81, 1, 0xC0, 8);
        Assert.True(f.Carry);
        Assert.False(f.Overflow);       // SAR can never overflow: the sign is preserved
    }
}
