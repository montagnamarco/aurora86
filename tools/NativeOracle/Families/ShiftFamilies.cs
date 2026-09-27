using Aurora386.Core.Cpu;

namespace NativeOracle.Families;

/// <summary>
/// Group 2 (ROL/ROR/RCL/RCR/SHL/SHR/SAR, reg field 0-7 skipping the
/// undocumented SAL alias at 6) by 1, by CL or by an imm8, plus SHLD/SHRD.
/// The processor masks every count to 5 bits before using it, so a raw CL or
/// immediate anywhere in 0-255 is generated and the mask in
/// <see cref="FlagsSpec"/> is computed from the masked value, exactly as the
/// hardware sees it.
/// </summary>
public static class ShiftFamilies
{
    private static readonly (string Name, int OperationField)[] Group2 =
    [
        ("ROL", 0), ("ROR", 1), ("RCL", 2), ("RCR", 3), ("SHL", 4), ("SHR", 5), ("SAR", 7),
    ];

    private static readonly Width[] Widths = [Width.Byte, Width.Word, Width.Dword];

    /// <summary>By 1 (D0/D1), by CL (D2/D3), or by an imm8 (C0/C1).</summary>
    private enum CountForm { One, Cl, Immediate8 }

    public static IEnumerable<Family> All()
    {
        foreach ((string name, int operationField) in Group2)
        {
            yield return new Family(name, context => Generate(context, name, operationField));
        }

        yield return new Family("SHLD", context => GenerateDouble(context, "SHLD", left: true));
        yield return new Family("SHRD", context => GenerateDouble(context, "SHRD", left: false));
    }

    private static TestCase Generate(FamilyContext context, string name, int operationField)
    {
        Random rng = context.Rng;
        Width width = Widths[rng.Next(Widths.Length)];
        bool memory = rng.Next(2) == 0;
        var countForm = (CountForm)rng.Next(3);
        bool countInCl = countForm == CountForm.Cl;

        uint[] gpr = context.FreshGpr();
        byte count = (byte)rng.Next(0, 256);
        if (countInCl)
        {
            gpr[(int)Reg.Ecx] = (gpr[(int)Reg.Ecx] & 0xFFFFFF00u) | count;
        }

        var code = new List<byte>();
        InstructionEncoding.AppendOperandSizePrefix(code, width);
        byte opcode = countForm switch
        {
            CountForm.One => width == Width.Byte ? (byte)0xD0 : (byte)0xD1,
            CountForm.Cl => width == Width.Byte ? (byte)0xD2 : (byte)0xD3,
            _ => width == Width.Byte ? (byte)0xC0 : (byte)0xC1,
        };
        code.Add(opcode);

        string operand;
        if (memory)
        {
            // The count lives in CL, so the address base cannot also be ECX.
            Reg baseRegister = countInCl ? InstructionEncoding.PickRegister(rng, Reg.Ecx) : InstructionEncoding.PickRegister(rng);
            int displacement = InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(operationField, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            operand = $"[{baseRegister}+{displacement}] ({width})";
        }
        else if (width == Width.Byte)
        {
            Reg8 destination = countInCl ? Operands.PickByteRegister(rng, Reg.Ecx) : Operands.PickByteRegister(rng);
            code.Add(InstructionEncoding.ModRmRegister(operationField, (int)destination));
            operand = destination.ToString();
        }
        else
        {
            Reg destination = countInCl ? InstructionEncoding.PickRegister(rng, Reg.Ecx) : InstructionEncoding.PickRegister(rng);
            code.Add(InstructionEncoding.ModRmRegister(operationField, destination));
            operand = Operands.Name(destination, width);
        }

        if (countForm == CountForm.Immediate8)
        {
            code.Add(count);
        }

        string countDescription = countForm switch { CountForm.One => "1", CountForm.Cl => "CL", _ => count.ToString() };
        // D0/D1 always shift by exactly 1, whatever the unused "count" byte
        // above happens to be -- only the CL and imm8 forms actually use it.
        uint maskedCount = countForm == CountForm.One ? 1u : (uint)(count & 0x1F);
        uint definedMask = FlagsSpec.ForShift(operationField, maskedCount, (int)width);

        return new TestCase
        {
            Family = name,
            Description = $"{name} {operand}, {countDescription}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = context.FreshBuffer(),
            DefinedFlagsMask = definedMask,
        };
    }

    /// <summary>0F A4/AC (imm8 count) or 0F A5/AD (CL count): SHLD/SHRD r/m, r, count.</summary>
    private static TestCase GenerateDouble(FamilyContext context, string name, bool left)
    {
        Random rng = context.Rng;
        Width width = rng.Next(2) == 0 ? Width.Word : Width.Dword;
        bool memory = rng.Next(2) == 0;
        bool countInCl = rng.Next(2) == 0;

        // A count past the operand's own width leaves the result, not only
        // the flags, undefined (see FlagsSpec.ForDoubleShift): kept out of
        // range entirely rather than compared away.
        int maxCount = width == Width.Word ? 16 : 31;
        byte count = (byte)rng.Next(0, maxCount + 1);

        uint[] gpr = context.FreshGpr();
        if (countInCl)
        {
            gpr[(int)Reg.Ecx] = (gpr[(int)Reg.Ecx] & 0xFFFFFF00u) | count;
        }

        var code = new List<byte>();
        InstructionEncoding.AppendOperandSizePrefix(code, width);
        code.Add(0x0F);
        code.Add(left ? (countInCl ? (byte)0xA5 : (byte)0xA4) : (countInCl ? (byte)0xAD : (byte)0xAC));

        string operand;
        if (memory)
        {
            Reg baseRegister = countInCl ? InstructionEncoding.PickRegister(rng, Reg.Ecx) : InstructionEncoding.PickRegister(rng);
            Reg sourceRegister = countInCl
                ? InstructionEncoding.PickRegister(rng, baseRegister, Reg.Ecx)
                : InstructionEncoding.PickRegister(rng, baseRegister);
            int displacement = InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory((int)sourceRegister, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            operand = $"[{baseRegister}+{displacement}] ({width}), {Operands.Name(sourceRegister, width)}";
        }
        else
        {
            Reg destination = countInCl ? InstructionEncoding.PickRegister(rng, Reg.Ecx) : InstructionEncoding.PickRegister(rng);
            Reg sourceRegister = countInCl
                ? InstructionEncoding.PickRegister(rng, destination, Reg.Ecx)
                : InstructionEncoding.PickRegister(rng, destination);
            code.Add(InstructionEncoding.ModRmRegister((int)sourceRegister, destination));
            operand = $"{Operands.Name(destination, width)}, {Operands.Name(sourceRegister, width)}";
        }

        if (!countInCl)
        {
            code.Add(count);
        }

        uint definedMask = FlagsSpec.ForDoubleShift((uint)count, width == Width.Dword);

        return new TestCase
        {
            Family = name,
            Description = $"{name} {operand}, {(countInCl ? "CL" : count.ToString())}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = context.FreshBuffer(),
            DefinedFlagsMask = definedMask,
        };
    }
}
