using Aurora86.Core.Cpu;

namespace NativeOracle.Families;

/// <summary>
/// CMOVcc (0F 40-4F /r): the r/m source is always read regardless of the
/// condition, and only the destination register changes, only when the
/// condition holds -- no flag is ever affected (Cpu.P6.cs's ExecuteCmovcc).
/// One family covers all sixteen conditions (SDM Vol.2A, the condition-code
/// table shared with Jcc/SETcc), the condition chosen at random each case
/// exactly the way MiscFamilies.GenerateSetcc already does for SETcc, so a
/// long enough run draws every condition both taken and not-taken from the
/// same random-EFLAGS draw that already covers CF/PF/ZF/SF/OF in every
/// combination. Needs CpuModel.PentiumII, the first model this emulator
/// carries CpuFeature.Cmov on (CpuFeatures.cs).
/// </summary>
public static class CmovFamilies
{
    /// <summary>The sixteen condition codes 0F 40-4F test, named the way CMOVcc's own mnemonics are (SDM Vol.2A, Table 3-1's alternate forms).</summary>
    private static readonly string[] ConditionNames =
    [
        "O", "NO", "B", "AE", "E", "NE", "BE", "A",
        "S", "NS", "P", "NP", "L", "GE", "LE", "G",
    ];

    public static IEnumerable<Family> All()
    {
        yield return new Family("CMOVCC", Generate);
    }

    /// <summary>0F 40+cc /r: Gv, Ev -- a 16- or 32-bit register destination, a same-width register or memory source, always read.</summary>
    private static TestCase Generate(FamilyContext context)
    {
        Random rng = context.Rng;
        int condition = rng.Next(16);
        Width width = rng.Next(2) == 0 ? Width.Word : Width.Dword;
        bool memory = rng.Next(2) == 0;

        uint[] gpr = context.FreshGpr();
        var code = new List<byte>();
        InstructionEncoding.AppendOperandSizePrefix(code, width);
        code.Add(0x0F);
        code.Add((byte)(0x40 + condition));

        string operand;
        byte[] buffer = context.FreshBuffer();
        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            Reg destination = InstructionEncoding.PickRegister(rng, baseRegister);
            int displacement = InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory((int)destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            operand = $"{Operands.Name(destination, width)}, [{baseRegister}+{displacement}]";
        }
        else
        {
            Reg source = InstructionEncoding.PickRegister(rng);
            Reg destination = InstructionEncoding.PickRegister(rng, source);
            code.Add(InstructionEncoding.ModRmRegister((int)destination, source));
            operand = $"{Operands.Name(destination, width)}, {Operands.Name(source, width)}";
        }

        return new TestCase
        {
            Family = "CMOVCC",
            Description = $"CMOV{ConditionNames[condition]} {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.PentiumII,
        };
    }
}
