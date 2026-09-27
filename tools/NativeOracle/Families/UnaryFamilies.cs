using Aurora386.Core.Cpu;

namespace NativeOracle.Families;

/// <summary>
/// INC/DEC (group 5/FF, never the one-byte 40-4F short forms, which do not
/// exist in 64-bit mode) and NEG/NOT (group 3/F6-F7). All four take one
/// register-or-memory operand and no immediate.
/// </summary>
public static class UnaryFamilies
{
    private static readonly Width[] Widths = [Width.Byte, Width.Word, Width.Dword];

    public static IEnumerable<Family> All()
    {
        yield return new Family("INC", context => GenerateIncDec(context, "INC", reg: 0, definedMask: FlagsSpec.AllArithmetic));
        yield return new Family("DEC", context => GenerateIncDec(context, "DEC", reg: 1, definedMask: FlagsSpec.AllArithmetic));
        yield return new Family("NEG", context => GenerateGroup3(context, "NEG", reg: 3, definedMask: FlagsSpec.AllArithmetic));
        yield return new Family("NOT", context => GenerateGroup3(context, "NOT", reg: 2, definedMask: FlagsSpec.AllArithmetic));
    }

    /// <summary>FF /0 (INC) or FF /1 (DEC), FE for the byte form: 40-4F is not used, per the contract's 64-bit-mode constraint.</summary>
    private static TestCase GenerateIncDec(FamilyContext context, string name, int reg, uint definedMask)
    {
        Random rng = context.Rng;
        Width width = Widths[rng.Next(Widths.Length)];
        bool memory = rng.Next(2) == 0;

        uint[] gpr = context.FreshGpr();
        var code = new List<byte>();
        InstructionEncoding.AppendOperandSizePrefix(code, width);
        byte opcode = width == Width.Byte ? (byte)0xFE : (byte)0xFF;
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
            Reg8 destination = Operands.PickByteRegister(rng);
            code.Add(InstructionEncoding.ModRmRegister(reg, (int)destination));
            description = $"{name} {destination}";
        }
        else
        {
            Reg destination = InstructionEncoding.PickRegister(rng);
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
            InitialBuffer = context.FreshBuffer(),
            DefinedFlagsMask = definedMask,
        };
    }

    /// <summary>F6/F7 group 3, reg=2 (NOT) or 3 (NEG).</summary>
    private static TestCase GenerateGroup3(FamilyContext context, string name, int reg, uint definedMask)
    {
        Random rng = context.Rng;
        Width width = Widths[rng.Next(Widths.Length)];
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
            Reg8 destination = Operands.PickByteRegister(rng);
            code.Add(InstructionEncoding.ModRmRegister(reg, (int)destination));
            description = $"{name} {destination}";
        }
        else
        {
            Reg destination = InstructionEncoding.PickRegister(rng);
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
            InitialBuffer = context.FreshBuffer(),
            DefinedFlagsMask = definedMask,
        };
    }
}
