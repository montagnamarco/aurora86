namespace Aurora86.Core.Cpu.Fpu;

/// <summary>
/// One x87 register exactly as the silicon holds it: a 64-bit significand
/// and a 16-bit word of sign and biased exponent, the same ten bytes FSAVE
/// writes and FLD TBYTE reads.
///
/// The register file keeps this form, not the normalised
/// <see cref="Extended80"/> the arithmetic works in, for two reasons that
/// both come down to "what goes in must come out". The Pentium MMX aliases
/// its eight 64-bit MMX registers onto the significands of these eight
/// registers and writes all ones into the exponent, which read as an x87
/// value is a NaN with an arbitrary payload; and operating systems save and
/// restore that state with FSAVE and FRSTOR on every task switch. Held in a
/// normalised form, the NaN would come back as the one canonical NaN and the
/// MMX value would be gone. Held raw, it survives any number of saves, loads
/// and register-to-register moves, as it does on the chip.
/// </summary>
public readonly struct Raw80
{
    public readonly ulong Significand;
    public readonly ushort SignExponent;

    public Raw80(ulong significand, ushort signExponent)
    {
        Significand = significand;
        SignExponent = signExponent;
    }

    /// <summary>The biased exponent: 0 for zeros and denormals, 7FFF for infinities and NaNs.</summary>
    public int BiasedExponent => SignExponent & 0x7FFF;

    /// <summary>The value the arithmetic works with.</summary>
    public Extended80 ToExtended() => Extended80.FromBits(Significand, SignExponent);

    /// <summary>The bits an arithmetic result is stored as.</summary>
    public static Raw80 FromExtended(Extended80 value)
    {
        (ulong significand, ushort signExponent) = value.ToBits();
        return new Raw80(significand, signExponent);
    }

    /// <summary>
    /// The two-bit tag FSTENV and FSAVE report for a register that holds
    /// something: 1 for a true zero, 2 for anything special -- an infinity, a
    /// NaN, a denormal or an unnormal (a nonzero exponent whose significand
    /// lacks the explicit integer bit) -- and 0 for an ordinary number. An MMX
    /// value, with its all-ones exponent, is special.
    /// </summary>
    public int Tag
    {
        get
        {
            const ulong integerBit = 1UL << 63;
            const int maximumExponent = 0x7FFF;

            int exponent = BiasedExponent;

            if (exponent == 0)
            {
                return Significand == 0 ? 1 : 2;
            }

            if (exponent == maximumExponent)
            {
                return 2;
            }

            return (Significand & integerBit) != 0 ? 0 : 2;
        }
    }
}
