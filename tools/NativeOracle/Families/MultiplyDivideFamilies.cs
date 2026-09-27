using Aurora386.Core.Cpu;

namespace NativeOracle.Families;

/// <summary>
/// MUL and every form of IMUL never fault, so their operands are as free as
/// any ALU instruction's. DIV/IDIV do fault -- divide error, vector 0 -- the
/// moment the quotient does not fit the destination or the divisor is zero,
/// so their dividends are constructed backwards from a quotient and
/// remainder chosen to fit, with a margin against the one case a
/// differential generator cannot itself reproduce: a real IDIV's remainder
/// takes the dividend's sign, and a quotient picked at the very edge of its
/// range could come back one off if this generator's own remainder guess
/// disagreed with that sign.
/// </summary>
public static class MultiplyDivideFamilies
{
    private static readonly Width[] WideWidths = [Width.Byte, Width.Word, Width.Dword];
    private static readonly Width[] TwoAndThreeOperandWidths = [Width.Word, Width.Dword];

    public static IEnumerable<Family> All()
    {
        yield return new Family("MUL", context => GenerateOneOperand(context, "MUL", reg: 4, definedMask: FlagsSpec.Multiply));
        yield return new Family("IMUL", GenerateImul);
        yield return new Family("DIV", context => GenerateDivide(context, "DIV", signed: false));
        yield return new Family("IDIV", context => GenerateDivide(context, "IDIV", signed: true));
    }

    /// <summary>F6/F7 group 3, reg=4 (MUL) or 5 (IMUL): AX=AL*r/m8, DX:AX=AX*r/m16, EDX:EAX=EAX*r/m32.</summary>
    private static TestCase GenerateOneOperand(FamilyContext context, string name, int reg, uint definedMask)
    {
        Random rng = context.Rng;
        Width width = WideWidths[rng.Next(WideWidths.Length)];
        bool memory = rng.Next(2) == 0;

        uint[] gpr = context.FreshGpr();
        var code = new List<byte>();
        InstructionEncoding.AppendOperandSizePrefix(code, width);
        byte opcode = width == Width.Byte ? (byte)0xF6 : (byte)0xF7;
        code.Add(opcode);

        string description;
        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(reg, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            description = $"{name} [{baseRegister}+{displacement}] ({width})";
        }
        else if (width == Width.Byte)
        {
            Reg8 source = Operands.PickByteRegister(rng);
            code.Add(InstructionEncoding.ModRmRegister(reg, (int)source));
            description = $"{name} AL, {source}";
        }
        else
        {
            Reg source = InstructionEncoding.PickRegister(rng);
            code.Add(InstructionEncoding.ModRmRegister(reg, source));
            description = $"{name} {Operands.Name(Reg.Eax, width)}, {Operands.Name(source, width)}";
        }

        return new TestCase
        {
            Family = name,
            Description = description,
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = context.FreshBuffer(),
            DefinedFlagsMask = definedMask,
        };
    }

    /// <summary>Picks among the 1-, 2- and 3-operand forms of IMUL.</summary>
    private static TestCase GenerateImul(FamilyContext context)
    {
        int form = context.Rng.Next(3);
        if (form == 0)
        {
            return GenerateOneOperand(context, "IMUL", reg: 5, definedMask: FlagsSpec.Multiply);
        }

        Random rng = context.Rng;
        Width width = TwoAndThreeOperandWidths[rng.Next(TwoAndThreeOperandWidths.Length)];
        bool memory = rng.Next(2) == 0;
        bool signExtendedByte = form == 2 && rng.Next(2) == 0;
        uint[] gpr = context.FreshGpr();
        var code = new List<byte>();
        InstructionEncoding.AppendOperandSizePrefix(code, width);

        if (form == 1)
        {
            code.Add(0x0F);
            code.Add(0xAF);   // IMUL Gv, Ev
        }
        else
        {
            code.Add(signExtendedByte ? (byte)0x6B : (byte)0x69);   // IMUL Gv, Ev, Ib/Iz
        }

        Reg destination;
        string sourceDescription;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            destination = InstructionEncoding.PickRegister(rng, baseRegister);
            int displacement = InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory((int)destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            sourceDescription = $"[{baseRegister}+{displacement}] ({width})";
        }
        else
        {
            Reg source = InstructionEncoding.PickRegister(rng);
            destination = InstructionEncoding.PickRegister(rng, source);
            code.Add(InstructionEncoding.ModRmRegister((int)destination, source));
            sourceDescription = Operands.Name(source, width);
        }

        string description = $"IMUL {Operands.Name(destination, width)}, {sourceDescription}";
        if (form == 2)
        {
            long immediate = signExtendedByte
                ? (sbyte)rng.Next(-128, 128)
                : rng.NextUInt32() & (width == Width.Word ? 0xFFFFu : 0xFFFFFFFFu);
            InstructionEncoding.AppendImmediate(code, signExtendedByte ? Width.Byte : width, immediate);
            description += $", {immediate}";
        }

        return new TestCase
        {
            Family = "IMUL",
            Description = description,
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = context.FreshBuffer(),
            DefinedFlagsMask = FlagsSpec.Multiply,
        };
    }

    /// <summary>F6/F7 group 3, reg=6 (DIV) or 7 (IDIV).</summary>
    private static TestCase GenerateDivide(FamilyContext context, string name, bool signed)
    {
        Random rng = context.Rng;
        Width width = WideWidths[rng.Next(WideWidths.Length)];
        bool memory = rng.Next(2) == 0;
        int reg = signed ? 7 : 6;

        (uint divisor, uint eax, uint edx) = BuildNonFaultingDividend(rng, width, signed);

        uint[] gpr = context.FreshGpr();
        // A byte divide's dividend is all of AX (16 bits), not just AL: DIV
        // r/m8 divides AX by the operand, leaving AL the quotient and AH the
        // remainder, so the byte and word cases both only replace AX here.
        gpr[(int)Reg.Eax] = width == Width.Dword
            ? eax
            : (gpr[(int)Reg.Eax] & 0xFFFF0000u) | (eax & 0xFFFF);
        if (width != Width.Byte)
        {
            gpr[(int)Reg.Edx] = width == Width.Word ? (gpr[(int)Reg.Edx] & 0xFFFF0000u) | (edx & 0xFFFF) : edx;
        }

        byte[] buffer = context.FreshBuffer();
        var code = new List<byte>();
        InstructionEncoding.AppendOperandSizePrefix(code, width);
        byte opcode = width == Width.Byte ? (byte)0xF6 : (byte)0xF7;
        code.Add(opcode);

        string description;
        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng, Reg.Eax, Reg.Edx);
            int displacement = InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(reg, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            description = $"{name} [{baseRegister}+{displacement}] ({width})";

            byte[] divisorBytes = BitConverter.GetBytes(divisor);
            int byteCount = width == Width.Byte ? 1 : width == Width.Word ? 2 : 4;
            Array.Copy(divisorBytes, 0, buffer, displacement, byteCount);
        }
        else if (width == Width.Byte)
        {
            // The divisor register must not be part of EAX: EAX already holds the dividend's low half.
            Reg8 destination = Operands.PickByteRegister(rng, Reg.Eax);
            gpr[(int)Operands.Underlying32(destination)] = Operands.WithByte(gpr[(int)Operands.Underlying32(destination)], destination, (byte)divisor);
            code.Add(InstructionEncoding.ModRmRegister(reg, (int)destination));
            description = $"{name} {destination}";
        }
        else
        {
            Reg destination = InstructionEncoding.PickRegister(rng, Reg.Eax, Reg.Edx);
            gpr[(int)destination] = width == Width.Word ? (gpr[(int)destination] & 0xFFFF0000u) | (divisor & 0xFFFF) : divisor;
            code.Add(InstructionEncoding.ModRmRegister(reg, destination));
            description = $"{name} {Operands.Name(destination, width)}";
        }

        return new TestCase
        {
            Family = name,
            Description = description,
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.Divide,
        };
    }

    /// <summary>
    /// Picks a divisor and a dividend (as EAX/EDX bit patterns) such that the
    /// true quotient fits the destination width, leaving a margin so an
    /// IDIV's sign-of-the-dividend remainder rule cannot push a quotient
    /// picked at the very edge of its range out of bounds.
    /// </summary>
    private static (uint Divisor, uint Eax, uint Edx) BuildNonFaultingDividend(Random rng, Width width, bool signed)
    {
        int bits = (int)width;
        long quotientMargin = 1L << Math.Max(1, bits - 4);   // a sixteenth of the range, at least 2.

        if (!signed)
        {
            ulong maxQuotient = bits == 8 ? 0xFFu : bits == 16 ? 0xFFFFu : 0xFFFFFFFFu;
            ulong divisor = (ulong)rng.NextInt64(1, (long)maxQuotient + 1);
            ulong quotient = (ulong)rng.NextInt64(0, (long)(maxQuotient - (ulong)quotientMargin) + 1);
            ulong remainder = divisor > 1 ? (ulong)rng.NextInt64(0, (long)(divisor - 1) + 1) : 0;
            ulong dividend = quotient * divisor + remainder;
            return ((uint)divisor, (uint)dividend, (uint)(dividend >> 32));
        }

        long minQuotient = bits == 8 ? sbyte.MinValue : bits == 16 ? short.MinValue : int.MinValue;
        long maxQuotient2 = bits == 8 ? sbyte.MaxValue : bits == 16 ? short.MaxValue : int.MaxValue;
        long divisorSigned;
        do { divisorSigned = rng.NextInt64(minQuotient, maxQuotient2 + 1); } while (divisorSigned == 0);
        long quotientSigned = rng.NextInt64(minQuotient + quotientMargin, maxQuotient2 - quotientMargin + 1);
        long remainderBound = Math.Abs(divisorSigned) - 1;
        long remainderSigned = remainderBound > 0 ? rng.NextInt64(-remainderBound, remainderBound + 1) : 0;
        long dividendSigned = quotientSigned * divisorSigned + remainderSigned;
        return (unchecked((uint)divisorSigned), unchecked((uint)dividendSigned), unchecked((uint)(dividendSigned >> 32)));
    }
}
