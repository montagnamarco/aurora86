using Aurora86.Core.Cpu;

namespace NativeOracle.Families;

/// <summary>
/// SETcc, MOVZX/MOVSX, LEA, the sign-extending accumulator instructions
/// CBW/CWDE/CWD/CDQ, and the three flag-only instructions CLC/STC/CMC. None
/// of these can fault on the operands generated here, and none leaves any
/// arithmetic flag at an architecturally undefined value: SETcc and LEA
/// touch none, MOVZX/MOVSX touch none, CBW/CWDE/CWD/CDQ touch none, and
/// CLC/STC/CMC only ever change CF.
/// </summary>
public static class MiscFamilies
{
    /// <summary>The sixteen condition codes 0F 90-9F test (SDM Vol.2A, Table on Jcc/SETcc/CMOVcc).</summary>
    private static readonly string[] ConditionNames =
    [
        "O", "NO", "B", "NB", "E", "NE", "BE", "NBE",
        "S", "NS", "P", "NP", "L", "NL", "LE", "NLE",
    ];

    public static IEnumerable<Family> All()
    {
        yield return new Family("SETCC", GenerateSetcc);
        yield return new Family("MOVZX", context => GenerateExtend(context, "MOVZX", signed: false));
        yield return new Family("MOVSX", context => GenerateExtend(context, "MOVSX", signed: true));
        yield return new Family("LEA", GenerateLea);
        yield return new Family("CBW", context => GenerateAccumulatorExtend(context, "CBW", opcode: 0x98, prefixed: true));
        yield return new Family("CWDE", context => GenerateAccumulatorExtend(context, "CWDE", opcode: 0x98, prefixed: false));
        yield return new Family("CWD", context => GenerateAccumulatorExtend(context, "CWD", opcode: 0x99, prefixed: true));
        yield return new Family("CDQ", context => GenerateAccumulatorExtend(context, "CDQ", opcode: 0x99, prefixed: false));
        yield return new Family("CLC", context => GenerateFlagOnly(context, "CLC", 0xF8));
        yield return new Family("STC", context => GenerateFlagOnly(context, "STC", 0xF9));
        yield return new Family("CMC", context => GenerateFlagOnly(context, "CMC", 0xF5));
    }

    /// <summary>0F 90-9F /0: writes 0 or 1, a byte, never affecting a flag.</summary>
    private static TestCase GenerateSetcc(FamilyContext context)
    {
        Random rng = context.Rng;
        int condition = rng.Next(16);
        bool memory = rng.Next(2) == 0;

        uint[] gpr = context.FreshGpr();
        var code = new List<byte> { 0x0F, (byte)(0x90 + condition) };

        string operand;
        byte[] buffer = context.FreshBuffer();
        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(0, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            operand = $"[{baseRegister}+{displacement}]";
        }
        else
        {
            Reg8 destination = Operands.PickByteRegister(rng);
            code.Add(InstructionEncoding.ModRmRegister(0, (int)destination));
            operand = destination.ToString();
        }

        return new TestCase
        {
            Family = "SETCC",
            Description = $"SET{ConditionNames[condition]} {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
        };
    }

    /// <summary>0F B6/B7 (MOVZX) or 0F BE/BF (MOVSX): Gv, Eb or Gv, Ew.</summary>
    private static TestCase GenerateExtend(FamilyContext context, string name, bool signed)
    {
        Random rng = context.Rng;
        bool sourceIsByte = rng.Next(2) == 0;
        Width destinationWidth = rng.Next(2) == 0 ? Width.Word : Width.Dword;
        bool memory = rng.Next(2) == 0;

        uint[] gpr = context.FreshGpr();
        var code = new List<byte>();
        InstructionEncoding.AppendOperandSizePrefix(code, destinationWidth);
        code.Add(0x0F);
        byte opcode = signed
            ? (sourceIsByte ? (byte)0xBE : (byte)0xBF)
            : (sourceIsByte ? (byte)0xB6 : (byte)0xB7);
        code.Add(opcode);

        Width sourceWidth = sourceIsByte ? Width.Byte : Width.Word;
        string operand;
        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            Reg destination = InstructionEncoding.PickRegister(rng, baseRegister);
            int displacement = InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory((int)destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            operand = $"{Operands.Name(destination, destinationWidth)}, [{baseRegister}+{displacement}] ({sourceWidth})";
        }
        else if (sourceIsByte)
        {
            Reg8 source = Operands.PickByteRegister(rng);
            Reg destination = InstructionEncoding.PickRegister(rng, Operands.Underlying32(source));
            code.Add(InstructionEncoding.ModRmRegister((int)destination, (int)source));
            operand = $"{Operands.Name(destination, destinationWidth)}, {source}";
        }
        else
        {
            Reg source = InstructionEncoding.PickRegister(rng);
            Reg destination = InstructionEncoding.PickRegister(rng, source);
            code.Add(InstructionEncoding.ModRmRegister((int)destination, source));
            operand = $"{Operands.Name(destination, destinationWidth)}, {Operands.Name(source, sourceWidth)}";
        }

        return new TestCase
        {
            Family = name,
            Description = $"{name} {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = context.FreshBuffer(),
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
        };
    }

    /// <summary>8D /r: the destination gets an address, never a dereference, so the buffer is generated but never has to match anything in particular.</summary>
    private static TestCase GenerateLea(FamilyContext context)
    {
        Random rng = context.Rng;
        Width width = rng.Next(2) == 0 ? Width.Word : Width.Dword;
        Reg baseRegister = InstructionEncoding.PickRegister(rng);
        Reg destination = InstructionEncoding.PickRegister(rng, baseRegister);
        int displacement = InstructionEncoding.RandomDisplacement(rng);

        uint[] gpr = context.FreshGpr();
        gpr[(int)baseRegister] = context.BufferAddress;

        var code = new List<byte>();
        InstructionEncoding.AppendOperandSizePrefix(code, width);
        code.Add(0x8D);
        code.Add(InstructionEncoding.ModRmMemory((int)destination, baseRegister));
        InstructionEncoding.AppendDisplacement(code, displacement);

        return new TestCase
        {
            Family = "LEA",
            Description = $"LEA {Operands.Name(destination, width)}, [{baseRegister}+{displacement}]",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = context.FreshBuffer(),
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
        };
    }

    /// <summary>
    /// 0x98 (CBW/CWDE) or 0x99 (CWD/CDQ), with a leading 0x66 for the
    /// narrower of each pair: sign-extends the accumulator into itself or
    /// into DX:AX/EDX:EAX. No operand to choose beyond the accumulator's own
    /// initial value, already random from <see cref="FamilyContext.FreshGpr"/>.
    /// </summary>
    private static TestCase GenerateAccumulatorExtend(FamilyContext context, string name, byte opcode, bool prefixed)
    {
        Random rng = context.Rng;
        uint[] gpr = context.FreshGpr();

        var code = new List<byte>();
        if (prefixed)
        {
            code.Add(0x66);
        }
        code.Add(opcode);

        return new TestCase
        {
            Family = name,
            Description = name,
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = context.FreshBuffer(),
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
        };
    }

    private static TestCase GenerateFlagOnly(FamilyContext context, string name, byte opcode)
    {
        Random rng = context.Rng;
        uint[] gpr = context.FreshGpr();

        return new TestCase
        {
            Family = name,
            Description = name,
            Instruction = [opcode],
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = context.FreshBuffer(),
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
        };
    }
}
