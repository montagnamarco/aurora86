using Aurora386.Core.Cpu;

namespace NativeOracle;

/// <summary>
/// Which EFLAGS bits an operation defines, straight from the Intel SDM's own
/// "Flags Affected" tables (see plans/specs/cpu-models.md §4 for the summary
/// this was checked against). A flag that is architecturally undefined for
/// given operands is left out of the mask a <see cref="TestCase"/> carries,
/// so the comparer never fails a case over a bit the SDM itself does not
/// promise -- whether the host's undefined value differs from Aurora's or
/// not is then simply not a question this oracle asks.
///
/// This mirrors tools/RealModeSuite/UndefinedFlags.cs, but computes the mask
/// from the operation and the operand count a generator already knows,
/// rather than re-deriving them by re-parsing the instruction's bytes: here
/// the oracle is the one that chose them.
/// </summary>
public static class FlagsSpec
{
    public const uint Carry = (uint)Flags.Carry;
    public const uint Parity = (uint)Flags.Parity;
    public const uint Adjust = (uint)Flags.Adjust;
    public const uint Zero = (uint)Flags.Zero;
    public const uint Sign = (uint)Flags.Sign;
    public const uint Overflow = (uint)Flags.Overflow;

    /// <summary>Every flag ADD/SUB/CMP/INC/... arithmetic touches or, equally usably, always leaves alone.</summary>
    public const uint AllArithmetic = Carry | Parity | Adjust | Zero | Sign | Overflow;

    /// <summary>AND/OR/XOR/TEST: CF and OF are cleared, SF/ZF/PF computed, AF left at an undefined value.</summary>
    public const uint Logic = AllArithmetic & ~Adjust;

    /// <summary>MUL and every form of IMUL: only CF/OF (did the result overflow the narrow half) are defined.</summary>
    public const uint Multiply = Carry | Overflow;

    /// <summary>DIV/IDIV: every arithmetic flag is left at an undefined value.</summary>
    public const uint Divide = 0;

    /// <summary>BSF/BSR: only ZF (source was zero) is defined; the destination itself is also undefined then -- see <see cref="BitScanIgnoresDestination"/>.</summary>
    public const uint BitScan = Zero;

    /// <summary>BT/BTS/BTR/BTC: only CF (the selected bit's old value) is defined.</summary>
    public const uint BitTest = Carry;

    public static bool BitScanIgnoresDestination(bool sourceWasZero) => sourceWasZero;

    /// <summary>
    /// Group 2 (ROL/ROR/RCL/RCR/SHL/SHR/SAR) by an immediate or CL, after the
    /// count has already been masked to 5 bits: a masked count of zero
    /// changes no flag at all. <paramref name="operationField"/> is the
    /// ModR/M reg field (0-3 rotates, 4-7 shifts); <paramref name="operandBits"/>
    /// is the operand's width.
    /// </summary>
    public static uint ForShift(int operationField, uint maskedCount, int operandBits)
    {
        if (maskedCount == 0)
        {
            return AllArithmetic;
        }

        uint undefined = maskedCount > 1 ? Overflow : 0;
        bool isShift = operationField >= 4;

        if (isShift)
        {
            undefined |= Adjust;
            if (maskedCount >= operandBits)
            {
                undefined |= Carry;
            }
        }

        return AllArithmetic & ~undefined;
    }

    /// <summary>
    /// SHLD/SHRD: AF is always undefined, OF only above a count of one, and a
    /// count past a 16-bit operand's width leaves everything -- flags and
    /// the result -- undefined.
    /// </summary>
    public static uint ForDoubleShift(uint maskedCount, bool operandSize32)
    {
        if (maskedCount == 0)
        {
            return AllArithmetic;
        }
        if (!operandSize32 && maskedCount > 16)
        {
            return 0;
        }

        uint undefined = Adjust;
        if (maskedCount > 1)
        {
            undefined |= Overflow;
        }

        return AllArithmetic & ~undefined;
    }
}
