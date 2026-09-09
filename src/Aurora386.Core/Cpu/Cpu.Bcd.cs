// The decimal adjust instructions: DAA, DAS, AAA, AAS, AAM, AAD. Relics of
// the 8080 that nothing from the nineties uses, kept because the hardware
// suite exercises them, undefined flags and all. AAM with a zero divisor
// raises the divide error, like a DIV.

namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    // The decimal adjust instructions. They exist to do arithmetic on BCD
    // digits (one decimal digit per nibble), which contemporary software used
    // for currency amounts -- where binary rounding is unacceptable -- and to
    // convert numbers to strings without dividing by ten.

    /// <summary>DAA: adjusts AL after adding two packed BCD values.</summary>
    private void Daa()
    {
        // Both thresholds are tested against the *original* AL: the +6 of the
        // first correction must not feed into the comparison made by the
        // second, or a value like 0x9A would be corrected twice.
        byte al = State.GetReg8(0);
        byte initialAl = al;
        bool initialCarry = Flags.Carry;
        bool carry = false;

        if ((al & 0x0F) > 9 || Flags.Adjust)
        {
            carry = initialCarry || initialAl + 6 > 0xFF;
            al += 6;
            Flags.Adjust = true;
        }
        else Flags.Adjust = false;

        if (initialAl > 0x99 || initialCarry)
        {
            al += 0x60;
            carry = true;
        }

        State.SetReg8(0, al);
        bool adjust = Flags.Adjust;
        Flags.Set(FlagOp.Logic, 0, 0, al, 8);
        Flags.Carry = carry;
        Flags.Adjust = adjust;
    }

    /// <summary>DAS: adjusts AL after a packed BCD subtraction.</summary>
    private void Das()
    {
        byte al = State.GetReg8(0);
        byte initialAl = al;
        bool initialCarry = Flags.Carry;
        bool carry = false;

        if ((al & 0x0F) > 9 || Flags.Adjust)
        {
            carry = initialCarry || initialAl < 6;
            al -= 6;
            Flags.Adjust = true;
        }
        else Flags.Adjust = false;

        if (initialAl > 0x99 || initialCarry)
        {
            al -= 0x60;
            carry = true;
        }

        State.SetReg8(0, al);
        bool adjust = Flags.Adjust;
        Flags.Set(FlagOp.Logic, 0, 0, al, 8);
        Flags.Carry = carry;
        Flags.Adjust = adjust;
    }

    /// <summary>AAA: adjusts AL after adding two unpacked BCD digits.</summary>
    private void Aaa()
    {
        byte al = State.GetReg8(0);
        if ((al & 0x0F) > 9 || Flags.Adjust)
        {
            // The addition happens in eight bits, and its carry out propagates
            // into AH on top of the fixed increment: with AL = 0xFF, AH grows
            // by two, not one. Easy to miss, and the hardware-generated tests
            // catch it immediately.
            int sum = al + 6;
            State.SetReg8(0, (byte)(sum & 0x0F));
            State.SetReg8(4, (byte)(State.GetReg8(4) + 1 + (sum >> 8)));
            Flags.Adjust = true;
            Flags.Carry = true;
        }
        else
        {
            State.SetReg8(0, (byte)(al & 0x0F));
            Flags.Adjust = false;
            Flags.Carry = false;
        }
    }

    /// <summary>AAS: adjusts AL after subtracting two unpacked BCD digits.</summary>
    private void Aas()
    {
        byte al = State.GetReg8(0);
        if ((al & 0x0F) > 9 || Flags.Adjust)
        {
            // As with AAA, the borrow out of the eight-bit subtraction adds to
            // the fixed decrement of AH.
            int difference = al - 6;
            State.SetReg8(0, (byte)(difference & 0x0F));
            State.SetReg8(4, (byte)(State.GetReg8(4) - 1 - ((difference >> 8) & 1)));
            Flags.Adjust = true;
            Flags.Carry = true;
        }
        else
        {
            State.SetReg8(0, (byte)(al & 0x0F));
            Flags.Adjust = false;
            Flags.Carry = false;
        }
    }

    /// <summary>
    /// AAM: divides AL by the base (10 by default), leaving the quotient in AH
    /// and the remainder in AL. The base arrives as an immediate byte, so
    /// assemblers can emit other bases too -- which makes AAM a compact way of
    /// dividing a byte by a small constant, and some code used it for exactly
    /// that rather than for BCD at all.
    /// </summary>
    private void Aam()
    {
        byte divisor = FetchByte();
        if (divisor == 0)
        {
            // The 386 clears the arithmetic flags before it notices the zero
            // divisor, so the #DE handler finds this state in the flags pushed
            // on its stack frame. PF is left indeterminate: it depends on how
            // far the internal division had already got, and is not
            // reproducible.
            Flags.Carry = false; Flags.Adjust = false; Flags.Zero = false;
            Flags.Sign = false; Flags.Overflow = false;
            throw new CpuException(0);
        }

        byte al = State.GetReg8(0);
        State.SetReg8(4, (byte)(al / divisor));
        State.SetReg8(0, (byte)(al % divisor));
        Flags.Set(FlagOp.Logic, 0, 0, State.GetReg8(0), 8);
    }

    /// <summary>AAD: the inverse of AAM, folding AH:AL back down into AL.</summary>
    private void Aad()
    {
        byte multiplier = FetchByte();
        byte result = (byte)(State.GetReg8(0) + State.GetReg8(4) * multiplier);
        State.SetReg8(0, result);
        State.SetReg8(4, 0);
        Flags.Set(FlagOp.Logic, 0, 0, result, 8);
    }
}
