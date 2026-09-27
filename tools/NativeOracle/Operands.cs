using Aurora386.Core.Cpu;

namespace NativeOracle;

/// <summary>
/// The 8-bit register space (AL,CL,DL,BL,AH,CH,DH,BH) is a separate pool
/// from the 16/32-bit one <see cref="InstructionEncoding.AddressableRegisters"/>
/// draws from: without a REX prefix there is no 8-bit form of ESP, EBP, ESI
/// or EDI at all, and the four that do have one also have a *second* one (the
/// high byte), which is why a byte-width pick cannot simply reuse the
/// 32-bit picker with a narrower name.
/// </summary>
public static class Operands
{
    /// <summary>The 32-bit register a Reg8 value is part of: AL/AH -> EAX, CL/CH -> ECX, and so on.</summary>
    public static Reg Underlying32(Reg8 register) => (Reg)((int)register & 3);

    public static Reg8 PickByteRegister(Random rng) => (Reg8)rng.Next(8);

    /// <summary>A byte register that is not part of <paramref name="exclude"/>'s own 32-bit register.</summary>
    public static Reg8 PickByteRegister(Random rng, Reg exclude)
    {
        Reg8 candidate;
        do
        {
            candidate = PickByteRegister(rng);
        }
        while (Underlying32(candidate) == exclude);

        return candidate;
    }

    /// <summary>A byte register distinct from another already-chosen byte register.</summary>
    public static Reg8 PickByteRegister(Random rng, Reg8 exclude)
    {
        Reg8 candidate;
        do
        {
            candidate = PickByteRegister(rng);
        }
        while (candidate == exclude);

        return candidate;
    }

    /// <summary>
    /// Writes <paramref name="value"/> into the low or high byte of
    /// <paramref name="currentRegisterValue"/>, whichever <paramref name="destination"/>
    /// names, leaving the rest of the 32-bit register untouched.
    /// </summary>
    public static uint WithByte(uint currentRegisterValue, Reg8 destination, byte value)
        => (int)destination < 4
            ? (currentRegisterValue & 0xFFFFFF00u) | value
            : (currentRegisterValue & 0xFFFF00FFu) | ((uint)value << 8);

    /// <summary>The register's name at a given width, e.g. Ebx at Width.Word is "BX".</summary>
    public static string Name(Reg register, Width width) => width switch
    {
        Width.Word => register switch
        {
            Reg.Eax => "AX", Reg.Ecx => "CX", Reg.Edx => "DX", Reg.Ebx => "BX",
            Reg.Ebp => "BP", Reg.Esi => "SI", Reg.Edi => "DI", _ => register.ToString(),
        },
        _ => register.ToString(),
    };
}
