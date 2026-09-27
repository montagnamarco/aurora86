namespace RealModeSuite;

/// <summary>
/// The EFLAGS bits a test may not be judged on, beyond the masks the vector
/// files carry themselves.
///
/// Many files ship without a top-level RM32 mask, so every flag the Intel SDM
/// declares undefined for the instruction is compared as if it were
/// specified, and the 386EX's value for it -- which is whatever its microcode
/// happens to leave -- counts as a failure. The rules below are the SDM's own
/// tables (volume 2, each instruction's "Flags Affected"), keyed on the
/// opcode after its prefixes and, for the shifts, on the masked count, since
/// a shift by zero leaves every flag defined (unchanged).
///
/// One further thing is never compared: bits 18-31 of EFLAGS are reserved
/// on the 386: the 386EX keeps whatever LOADALL put there, so after POPFD or
/// IRETD they still hold the test's initial pattern, while a 386DX -- and
/// this emulator -- read them as zero. They are not state any program can
/// use.
/// </summary>
internal static class UndefinedFlags
{
    private const uint Carry = 1u << 0;
    private const uint Parity = 1u << 2;
    private const uint Auxiliary = 1u << 4;
    private const uint Zero = 1u << 6;
    private const uint Sign = 1u << 7;
    private const uint Overflow = 1u << 11;

    private const uint AllArithmetic = Carry | Parity | Auxiliary | Zero | Sign | Overflow;

    /// <summary>Bits 18-31: reserved on the 386 (AC, VIF, VIP and ID came later).</summary>
    public const uint Reserved386 = 0xFFFC0000;

    /// <summary>The EFLAGS bits to ignore for this instruction and initial state.</summary>
    public static uint For(byte[] instruction, uint initialEcx)
    {
        int index = 0;
        bool operandSize32 = false;

        while (index < instruction.Length && IsPrefix(instruction[index]))
        {
            if (instruction[index] == 0x66)
            {
                operandSize32 = !operandSize32;
            }

            index++;
        }

        if (index >= instruction.Length)
        {
            return 0;
        }

        byte opcode = instruction[index];

        if (opcode == 0x0F && index + 1 < instruction.Length)
        {
            return ForTwoByte(instruction, index + 1, operandSize32, initialEcx);
        }

        // D2/D3: shift or rotate by CL. The count is masked to five bits
        // before anything happens; a masked count of zero changes no flag.
        if ((opcode == 0xD2 || opcode == 0xD3) && index + 1 < instruction.Length)
        {
            int operation = (instruction[index + 1] >> 3) & 7;
            uint count = initialEcx & 0x1F;
            int operandBits = opcode == 0xD2 ? 8 : (operandSize32 ? 32 : 16);

            return ForShift(operation, count, operandBits);
        }

        return 0;
    }

    private static uint ForTwoByte(byte[] instruction, int index, bool operandSize32, uint initialEcx)
    {
        byte opcode = instruction[index];

        switch (opcode)
        {
            // BSF, BSR: only ZF is defined.
            case 0xBC:
            case 0xBD:
                return AllArithmetic & ~Zero;

            // IMUL r, r/m: CF and OF are defined, the rest are not.
            case 0xAF:
                return Sign | Zero | Auxiliary | Parity;

            // BT, BTS, BTR, BTC (and the immediate group BA): only CF is defined.
            case 0xA3:
            case 0xAB:
            case 0xB3:
            case 0xBB:
            case 0xBA:
                return AllArithmetic & ~Carry;

            // SHLD and SHRD, with an immediate count (A4, AC) or CL (A5, AD).
            case 0xA4:
            case 0xAC:
            case 0xA5:
            case 0xAD:
            {
                uint count = opcode is 0xA4 or 0xAC
                    ? ImmediateAfterModRm(instruction, index + 1)
                    : initialEcx;

                return ForDoubleShift(count & 0x1F, operandSize32);
            }
        }

        return 0;
    }

    /// <summary>
    /// Group 2 by CL: rotates (0-3) leave OF undefined for counts above one;
    /// shifts (4-7) also leave AF undefined for any nonzero count, and CF
    /// undefined once the count reaches the operand width, where the last
    /// bit shifted out is no longer a bit of the operand.
    /// </summary>
    private static uint ForShift(int operation, uint count, int operandBits)
    {
        if (count == 0)
        {
            return 0;
        }

        uint undefined = count > 1 ? Overflow : 0;
        bool isShift = operation >= 4;

        if (isShift)
        {
            undefined |= Auxiliary;

            if (count >= operandBits)
            {
                undefined |= Carry;
            }
        }

        return undefined;
    }

    /// <summary>
    /// SHLD/SHRD: AF is always undefined, OF above a count of one, and a count
    /// larger than a 16-bit operand leaves every flag (and the result)
    /// undefined.
    /// </summary>
    private static uint ForDoubleShift(uint count, bool operandSize32)
    {
        if (count == 0)
        {
            return 0;
        }

        if (!operandSize32 && count > 16)
        {
            return AllArithmetic;
        }

        uint undefined = Auxiliary;

        if (count > 1)
        {
            undefined |= Overflow;
        }

        return undefined;
    }

    /// <summary>
    /// The byte after a 16-bit-addressing ModRM and its displacement, or a
    /// 32-bit one's SIB and displacement. The vectors put the address-size
    /// prefix before the opcode, so it is re-read from the instruction.
    /// </summary>
    private static uint ImmediateAfterModRm(byte[] instruction, int modRmIndex)
    {
        bool addressSize32 = false;

        for (int i = 0; i < modRmIndex && IsPrefix(instruction[i]); i++)
        {
            if (instruction[i] == 0x67)
            {
                addressSize32 = !addressSize32;
            }
        }

        byte modRm = instruction[modRmIndex];
        int mod = modRm >> 6;
        int rm = modRm & 7;
        int length = 1;

        if (addressSize32)
        {
            if (mod != 3 && rm == 4)
            {
                byte sib = instruction[modRmIndex + 1];
                length++;

                if (mod == 0 && (sib & 7) == 5)
                {
                    length += 4;
                }
            }

            if (mod == 0 && rm == 5)
            {
                length += 4;
            }
            else if (mod == 1)
            {
                length += 1;
            }
            else if (mod == 2)
            {
                length += 4;
            }
        }
        else
        {
            if (mod == 0 && rm == 6)
            {
                length += 2;
            }
            else if (mod == 1)
            {
                length += 1;
            }
            else if (mod == 2)
            {
                length += 2;
            }
        }

        return instruction[modRmIndex + length];
    }

    private static bool IsPrefix(byte value)
        => value is 0x66 or 0x67 or 0x26 or 0x2E or 0x36 or 0x3E or 0x64 or 0x65 or 0xF0 or 0xF2 or 0xF3;
}
