namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>
    /// Group 2: ROL, ROR, RCL, RCR, SHL, SHR, SAL, SAR.
    ///
    /// From the 286 onwards the count is masked to 5 bits before use, so a
    /// shift by 32 does not clear the register -- it is a shift by 0 and leaves
    /// both the value and the flags untouched. This is an observable difference
    /// from the 8086, which happily shifted all 255 times, and some programs of
    /// the era used precisely this to tell the processor generations apart.
    /// </summary>
    private void ExecuteShift(in ModRm m, int count, bool byteOperand)
    {
        count &= 0x1F;

        // The operand is read even when the count is zero: the instruction has
        // no effect, but the memory access still happens, so an address outside
        // the segment must still raise #GP. Reading before the early-out is
        // what makes that fall out naturally.
        byte size = byteOperand ? (byte)8 : OperandBits;
        uint value = byteOperand ? ReadRm8(m) : ReadRm(m);
        if (count == 0) return;   // no effect at all, flags included
        uint mask = size == 8 ? 0xFFu : size == 16 ? 0xFFFFu : 0xFFFFFFFFu;
        uint signBit = 1u << (size - 1);
        uint result;

        switch (m.Reg)
        {
            case 0:   // ROL
            {
                int rotate = count % size;
                result = rotate == 0 ? value
                       : ((value << rotate) | (value >> (size - rotate))) & mask;
                Flags.Carry = (result & 1) != 0;
                if (count == 1) Flags.Overflow = ((result & signBit) != 0) != Flags.Carry;
                break;
            }

            case 1:   // ROR
            {
                int rotate = count % size;
                result = rotate == 0 ? value
                       : ((value >> rotate) | (value << (size - rotate))) & mask;
                Flags.Carry = (result & signBit) != 0;
                if (count == 1)
                {
                    // OF compares the two most significant bits of the result:
                    // it reports whether the rotate changed the sign.
                    bool topBit = (result & signBit) != 0;
                    bool nextBit = (result & (signBit >> 1)) != 0;
                    Flags.Overflow = topBit != nextBit;
                }
                break;
            }

            case 2:   // RCL: the rotation runs through the carry flag, so the
                      // cycle is size+1 bits long, not size.
            {
                int rotate = count % (size + 1);
                result = value;
                for (int i = 0; i < rotate; i++)
                {
                    bool newCarry = (result & signBit) != 0;
                    result = ((result << 1) | (Flags.Carry ? 1u : 0u)) & mask;
                    Flags.Carry = newCarry;
                }
                if (count == 1) Flags.Overflow = ((result & signBit) != 0) != Flags.Carry;
                break;
            }

            case 3:   // RCR
            {
                int rotate = count % (size + 1);
                result = value;
                if (count == 1)
                    Flags.Overflow = ((value & signBit) != 0) != Flags.Carry;
                for (int i = 0; i < rotate; i++)
                {
                    bool newCarry = (result & 1) != 0;
                    result = (result >> 1) | (Flags.Carry ? signBit : 0);
                    Flags.Carry = newCarry;
                }
                break;
            }

            case 4:
            case 6:   // SHL and SAL are the same instruction, two encodings
            {
                result = (value << count) & mask;
                Flags.Set(FlagOp.Shl, value, (uint)count, result, size);
                break;
            }

            case 5:   // SHR: logical shift, zeroes come in from the left
            {
                result = count >= size ? 0 : (value & mask) >> count;
                Flags.Set(FlagOp.Shr, value, (uint)count, result, size);
                break;
            }

            default:  // SAR: arithmetic shift, the sign bit replicates
            {
                // The sign extension has to happen at the operand's own width,
                // not at 32 bits: an 8- or 16-bit value with its top bit set
                // would otherwise be treated as positive and shift in zeroes
                // instead of ones.
                int signed = size switch
                {
                    8 => (sbyte)value,
                    16 => (short)value,
                    _ => (int)value,
                };
                int shift = Math.Min(count, size - 1);
                result = (uint)(signed >> shift) & mask;
                Flags.Set(FlagOp.Sar, value, (uint)count, result, size);
                break;
            }
        }

        if (byteOperand) WriteRm8(m, (byte)result);
        else WriteRm(m, result);
    }

    /// <summary>
    /// Double-precision shift (SHLD/SHRD), new in the 386: it shifts the
    /// destination while feeding in bits from a second operand instead of
    /// zeroes. It is what makes shifting a 64-bit quantity cheap on a 32-bit
    /// machine, and compilers emit it for exactly that.
    /// </summary>
    private void ShiftDouble(in ModRm m, uint source, int count, bool left)
    {
        count &= 0x1F;

        byte size = OperandBits;
        // The read comes before the zero-count check: even with a count of zero
        // the 386 touches the operand, so an address past the segment limit
        // raises #GP rather than slipping by unnoticed.
        uint destination = ReadRm(m);
        if (count == 0) return;
        uint mask = size == 16 ? 0xFFFFu : 0xFFFFFFFFu;
        uint result;

        if (size == 16 && count > 16)
        {
            // Intel documents this case as undefined, but the 386 resolves it
            // the same way every time, and the reason is visible in the
            // hardware: the barrel shifter is 32 bits wide, so with a count
            // above 16 the destination has already been shifted out entirely
            // and both halves end up filled with the source operand.
            // Verified against every case in the hardware-generated suite.
            uint doppia = (source & 0xFFFF) << 16 | (source & 0xFFFF);
            uint esito = left ? (doppia << (count - 16)) >> 16
                              : (doppia >> (count - 16)) & 0xFFFF;
            esito &= 0xFFFF;

            Flags.Carry = left
                ? ((source >> (32 - count)) & 1) != 0
                : ((source >> (count - 17)) & 1) != 0;
            bool cf = Flags.Carry;
            Flags.Set(FlagOp.Logic, 0, 0, esito, size);
            Flags.Carry = cf;

            WriteRm(m, esito);
            return;
        }

        if (left)
        {
            result = ((destination << count) | (source >> (size - count))) & mask;
            Flags.Carry = ((destination >> (size - count)) & 1) != 0;
        }
        else
        {
            result = ((destination >> count) | (source << (size - count))) & mask;
            Flags.Carry = ((destination >> (count - 1)) & 1) != 0;
        }

        // ZF/SF/PF follow from the result; CF was already set above, so it has
        // to be saved across the Set call and restored afterwards.
        bool carry = Flags.Carry;
        Flags.Set(FlagOp.Logic, 0, 0, result, size);
        Flags.Carry = carry;

        WriteRm(m, result);
    }
}
