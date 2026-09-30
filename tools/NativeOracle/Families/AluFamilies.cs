using Aurora86.Core.Cpu;

namespace NativeOracle.Families;

/// <summary>
/// The eight "group 1" ALU operations (Intel SDM Vol.2A, opcode maps
/// 00-3D and the 80/81/83 immediate group) plus TEST, which shares their
/// encoding shape but not their opcode group. Each mnemonic is one family;
/// internally it varies the form (register/register, register/memory,
/// memory/register, register-or-memory/immediate), the operand width
/// (8/16/32 bits) and which registers and displacement are used.
/// </summary>
public static class AluFamilies
{
    private static readonly (string Name, int Operation)[] Operations =
    [
        ("ADD", 0), ("OR", 1), ("ADC", 2), ("SBB", 3),
        ("AND", 4), ("SUB", 5), ("XOR", 6), ("CMP", 7),
    ];

    private static readonly Width[] Widths = [Width.Byte, Width.Word, Width.Dword];

    public static IEnumerable<Family> All()
    {
        foreach ((string name, int operation) in Operations)
        {
            bool isLogic = operation is 1 or 4 or 6;   // OR, AND, XOR
            uint definedMask = isLogic ? FlagsSpec.Logic : FlagsSpec.AllArithmetic;
            yield return new Family(name, context => Generate(context, name, operation, definedMask));
        }

        yield return new Family("TEST", GenerateTest);
    }

    /// <summary>One of the three forms the contract names: r/m,r; r,r/m; r/m,imm.</summary>
    private enum Form { RmDestinationRegisterSource, RegisterDestinationRmSource, RmDestinationImmediate }

    private static TestCase Generate(FamilyContext context, string name, int operation, uint definedMask)
    {
        Random rng = context.Rng;
        Width width = Widths[rng.Next(Widths.Length)];
        var form = (Form)rng.Next(3);
        bool memory = rng.Next(2) == 0;

        uint[] gpr = context.FreshGpr();
        var code = new List<byte>();
        InstructionEncoding.AppendOperandSizePrefix(code, width);
        string description;

        if (form == Form.RmDestinationImmediate)
        {
            byte opcode = width == Width.Byte ? (byte)0x80 : rng.Next(2) == 0 ? (byte)0x81 : (byte)0x83;
            bool signExtendedByte = opcode == 0x83;
            long immediate = signExtendedByte
                ? (sbyte)rng.Next(-128, 128)
                : rng.NextUInt32() & (width == Width.Word ? 0xFFFFu : 0xFFFFFFFFu);
            Width immediateWidth = signExtendedByte ? Width.Byte : width;

            if (memory)
            {
                Reg baseRegister = InstructionEncoding.PickRegister(rng);
                int displacement = InstructionEncoding.RandomDisplacement(rng);
                gpr[(int)baseRegister] = context.BufferAddress;
                code.Add(opcode);
                code.Add(InstructionEncoding.ModRmMemory(operation, baseRegister));
                InstructionEncoding.AppendDisplacement(code, displacement);
                InstructionEncoding.AppendImmediate(code, immediateWidth, immediate);
                description = $"{name} [{baseRegister}+{displacement}] ({width}), {immediate}";
            }
            else if (width == Width.Byte)
            {
                Reg8 destination = Operands.PickByteRegister(rng);
                code.Add(opcode);
                code.Add(InstructionEncoding.ModRmRegister(operation, (int)destination));
                InstructionEncoding.AppendImmediate(code, immediateWidth, immediate);
                description = $"{name} {destination}, {immediate}";
            }
            else
            {
                Reg destination = InstructionEncoding.PickRegister(rng);
                code.Add(opcode);
                code.Add(InstructionEncoding.ModRmRegister(operation, destination));
                InstructionEncoding.AppendImmediate(code, immediateWidth, immediate);
                description = $"{name} {Operands.Name(destination, width)}, {immediate}";
            }
        }
        else
        {
            bool rmIsDestination = form == Form.RmDestinationRegisterSource;
            byte opcode = (byte)(operation * 8 + (width == Width.Byte ? 0 : 1) + (rmIsDestination ? 0 : 2));
            code.Add(opcode);

            if (memory)
            {
                Reg baseRegister = InstructionEncoding.PickRegister(rng);
                int displacement = InstructionEncoding.RandomDisplacement(rng);
                gpr[(int)baseRegister] = context.BufferAddress;
                int regField = width == Width.Byte
                    ? (int)Operands.PickByteRegister(rng, baseRegister)
                    : (int)InstructionEncoding.PickRegister(rng, baseRegister);

                code.Add(InstructionEncoding.ModRmMemory(regField, baseRegister));
                InstructionEncoding.AppendDisplacement(code, displacement);
                string registerName = width == Width.Byte ? ((Reg8)regField).ToString() : Operands.Name((Reg)regField, width);
                description = rmIsDestination
                    ? $"{name} [{baseRegister}+{displacement}] ({width}), {registerName}"
                    : $"{name} {registerName}, [{baseRegister}+{displacement}] ({width})";
            }
            else if (width == Width.Byte)
            {
                Reg8 left = Operands.PickByteRegister(rng);
                Reg8 right = Operands.PickByteRegister(rng, exclude: left);
                (Reg8 rm, Reg8 reg) = rmIsDestination ? (left, right) : (right, left);
                code.Add(InstructionEncoding.ModRmRegister((int)reg, (int)rm));
                description = rmIsDestination ? $"{name} {rm}, {reg}" : $"{name} {reg}, {rm}";
            }
            else
            {
                Reg left = InstructionEncoding.PickRegister(rng);
                Reg right = InstructionEncoding.PickRegister(rng, left);
                (Reg rm, Reg reg) = rmIsDestination ? (left, right) : (right, left);
                code.Add(InstructionEncoding.ModRmRegister((int)reg, rm));
                description = rmIsDestination
                    ? $"{name} {Operands.Name(rm, width)}, {Operands.Name(reg, width)}"
                    : $"{name} {Operands.Name(reg, width)}, {Operands.Name(rm, width)}";
            }
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

    private static TestCase GenerateTest(FamilyContext context)
    {
        Random rng = context.Rng;
        Width width = Widths[rng.Next(Widths.Length)];
        bool memory = rng.Next(2) == 0;
        bool immediateForm = rng.Next(2) == 0;

        uint[] gpr = context.FreshGpr();
        var code = new List<byte>();
        InstructionEncoding.AppendOperandSizePrefix(code, width);
        string description;

        if (immediateForm)
        {
            byte opcode = width == Width.Byte ? (byte)0xF6 : (byte)0xF7;
            long immediate = rng.NextUInt32() & (width == Width.Word ? 0xFFFFu : 0xFFFFFFFFu);

            if (memory)
            {
                Reg baseRegister = InstructionEncoding.PickRegister(rng);
                int displacement = InstructionEncoding.RandomDisplacement(rng);
                gpr[(int)baseRegister] = context.BufferAddress;
                code.Add(opcode);
                code.Add(InstructionEncoding.ModRmMemory(0, baseRegister));
                InstructionEncoding.AppendDisplacement(code, displacement);
                InstructionEncoding.AppendImmediate(code, width, immediate);
                description = $"TEST [{baseRegister}+{displacement}] ({width}), {immediate}";
            }
            else if (width == Width.Byte)
            {
                Reg8 destination = Operands.PickByteRegister(rng);
                code.Add(opcode);
                code.Add(InstructionEncoding.ModRmRegister(0, (int)destination));
                InstructionEncoding.AppendImmediate(code, width, immediate);
                description = $"TEST {destination}, {immediate}";
            }
            else
            {
                Reg destination = InstructionEncoding.PickRegister(rng);
                code.Add(opcode);
                code.Add(InstructionEncoding.ModRmRegister(0, destination));
                InstructionEncoding.AppendImmediate(code, width, immediate);
                description = $"TEST {Operands.Name(destination, width)}, {immediate}";
            }
        }
        else
        {
            byte opcode = width == Width.Byte ? (byte)0x84 : (byte)0x85;
            code.Add(opcode);

            if (memory)
            {
                Reg baseRegister = InstructionEncoding.PickRegister(rng);
                int displacement = InstructionEncoding.RandomDisplacement(rng);
                gpr[(int)baseRegister] = context.BufferAddress;
                int regField = width == Width.Byte
                    ? (int)Operands.PickByteRegister(rng, baseRegister)
                    : (int)InstructionEncoding.PickRegister(rng, baseRegister);

                code.Add(InstructionEncoding.ModRmMemory(regField, baseRegister));
                InstructionEncoding.AppendDisplacement(code, displacement);
                string registerName = width == Width.Byte ? ((Reg8)regField).ToString() : Operands.Name((Reg)regField, width);
                description = $"TEST [{baseRegister}+{displacement}] ({width}), {registerName}";
            }
            else if (width == Width.Byte)
            {
                Reg8 left = Operands.PickByteRegister(rng);
                Reg8 right = Operands.PickByteRegister(rng, exclude: left);
                code.Add(InstructionEncoding.ModRmRegister((int)right, (int)left));
                description = $"TEST {left}, {right}";
            }
            else
            {
                Reg left = InstructionEncoding.PickRegister(rng);
                Reg right = InstructionEncoding.PickRegister(rng, left);
                code.Add(InstructionEncoding.ModRmRegister((int)right, left));
                description = $"TEST {Operands.Name(left, width)}, {Operands.Name(right, width)}";
            }
        }

        return new TestCase
        {
            Family = "TEST",
            Description = description,
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = context.FreshBuffer(),
            DefinedFlagsMask = FlagsSpec.Logic,
        };
    }
}
