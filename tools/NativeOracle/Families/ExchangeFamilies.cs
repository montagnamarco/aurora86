using Aurora86.Core.Cpu;

namespace NativeOracle.Families;

/// <summary>
/// The 486's own read-modify-write instructions -- BSWAP, XADD, CMPXCHG --
/// plus CMPXCHG8B, which needs a Pentium (CX8), and plain XCHG. Aurora gates
/// BSWAP/XADD/CMPXCHG on CpuFeature.Isa486 and CMPXCHG8B on CpuFeature.Cx8;
/// see <see cref="TestCase.Model"/>.
/// </summary>
public static class ExchangeFamilies
{
    public static IEnumerable<Family> All()
    {
        yield return new Family("BSWAP", GenerateBswap);
        yield return new Family("XADD", GenerateXadd);
        yield return new Family("CMPXCHG", GenerateCmpxchg);
        yield return new Family("CMPXCHG8B", GenerateCmpxchg8b);
        yield return new Family("XCHG", GenerateXchg);
    }

    /// <summary>0F C8+reg: register only. 16-bit BSWAP is architecturally undefined (SDM Vol.2A), so only the 32-bit form is generated.</summary>
    private static TestCase GenerateBswap(FamilyContext context)
    {
        Random rng = context.Rng;
        Reg destination = InstructionEncoding.PickRegister(rng);
        uint[] gpr = context.FreshGpr();

        var code = new List<byte> { 0x0F, (byte)(0xC8 + (int)destination) };

        return new TestCase
        {
            Family = "BSWAP",
            Description = $"BSWAP {destination}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = context.FreshBuffer(),
            DefinedFlagsMask = FlagsSpec.AllArithmetic,   // BSWAP touches no flag at all.
        };
    }

    private static readonly Width[] Widths = [Width.Byte, Width.Word, Width.Dword];

    /// <summary>0F C0 (byte) / C1 (word/dword): Ev, Gv. Adds like ADD: xchg then add, all arithmetic flags defined.</summary>
    private static TestCase GenerateXadd(FamilyContext context)
    {
        Random rng = context.Rng;
        Width width = Widths[rng.Next(Widths.Length)];
        bool memory = rng.Next(2) == 0;

        uint[] gpr = context.FreshGpr();
        var code = new List<byte>();
        InstructionEncoding.AppendOperandSizePrefix(code, width);
        code.Add(0x0F);
        code.Add(width == Width.Byte ? (byte)0xC0 : (byte)0xC1);

        string operand;
        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;

            if (width == Width.Byte)
            {
                Reg8 source = Operands.PickByteRegister(rng, baseRegister);
                code.Add(InstructionEncoding.ModRmMemory((int)source, baseRegister));
                operand = $"[{baseRegister}+{displacement}] ({width}), {source}";
            }
            else
            {
                Reg source = InstructionEncoding.PickRegister(rng, baseRegister);
                code.Add(InstructionEncoding.ModRmMemory((int)source, baseRegister));
                operand = $"[{baseRegister}+{displacement}] ({width}), {Operands.Name(source, width)}";
            }
            InstructionEncoding.AppendDisplacement(code, displacement);
        }
        else if (width == Width.Byte)
        {
            Reg8 destination = Operands.PickByteRegister(rng);
            Reg8 source = Operands.PickByteRegister(rng, exclude: destination);
            code.Add(InstructionEncoding.ModRmRegister((int)source, (int)destination));
            operand = $"{destination}, {source}";
        }
        else
        {
            Reg destination = InstructionEncoding.PickRegister(rng);
            Reg source = InstructionEncoding.PickRegister(rng, destination);
            code.Add(InstructionEncoding.ModRmRegister((int)source, destination));
            operand = $"{Operands.Name(destination, width)}, {Operands.Name(source, width)}";
        }

        return new TestCase
        {
            Family = "XADD",
            Description = $"XADD {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = context.FreshBuffer(),
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
        };
    }

    /// <summary>0F B0 (byte) / B1 (word/dword): Ev, Gv, comparing against AL/AX/EAX. Half the cases are made to match, so both the exchange and the plain compare are exercised.</summary>
    private static TestCase GenerateCmpxchg(FamilyContext context)
    {
        Random rng = context.Rng;
        Width width = Widths[rng.Next(Widths.Length)];
        bool memory = rng.Next(2) == 0;
        bool matches = rng.Next(2) == 0;

        uint[] gpr = context.FreshGpr();
        var code = new List<byte>();
        InstructionEncoding.AppendOperandSizePrefix(code, width);
        code.Add(0x0F);
        code.Add(width == Width.Byte ? (byte)0xB0 : (byte)0xB1);

        uint mask = width == Width.Byte ? 0xFFu : width == Width.Word ? 0xFFFFu : 0xFFFFFFFFu;
        uint destinationValue = rng.NextUInt32() & mask;
        uint accumulatorValue = matches ? destinationValue : destinationValue ^ 1;   // guaranteed different by exactly one bit when it must not match.
        gpr[(int)Reg.Eax] = width == Width.Dword ? accumulatorValue : (gpr[(int)Reg.Eax] & ~mask) | accumulatorValue;

        string operand;
        byte[] buffer = context.FreshBuffer();
        if (memory)
        {
            // EAX is the implicit comparator; the address base cannot be it.
            Reg baseRegister = InstructionEncoding.PickRegister(rng, Reg.Eax);
            int displacement = InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            byte[] valueBytes = BitConverter.GetBytes(destinationValue);
            Array.Copy(valueBytes, 0, buffer, displacement, (int)width / 8);

            if (width == Width.Byte)
            {
                Reg8 newValue = Operands.PickByteRegister(rng, baseRegister);
                code.Add(InstructionEncoding.ModRmMemory((int)newValue, baseRegister));
                operand = $"[{baseRegister}+{displacement}] ({width}), {newValue}";
            }
            else
            {
                Reg newValue = InstructionEncoding.PickRegister(rng, baseRegister);
                code.Add(InstructionEncoding.ModRmMemory((int)newValue, baseRegister));
                operand = $"[{baseRegister}+{displacement}] ({width}), {Operands.Name(newValue, width)}";
            }
            InstructionEncoding.AppendDisplacement(code, displacement);
        }
        else if (width == Width.Byte)
        {
            Reg8 destination = Operands.PickByteRegister(rng, Reg.Eax);
            Reg8 newValue = Operands.PickByteRegister(rng, exclude: destination);
            gpr[(int)Operands.Underlying32(destination)] = Operands.WithByte(gpr[(int)Operands.Underlying32(destination)], destination, (byte)destinationValue);
            code.Add(InstructionEncoding.ModRmRegister((int)newValue, (int)destination));
            operand = $"{destination}, {newValue}";
        }
        else
        {
            Reg destination = InstructionEncoding.PickRegister(rng, Reg.Eax);
            Reg newValue = InstructionEncoding.PickRegister(rng, destination, Reg.Eax);
            gpr[(int)destination] = width == Width.Word ? (gpr[(int)destination] & 0xFFFF0000u) | destinationValue : destinationValue;
            code.Add(InstructionEncoding.ModRmRegister((int)newValue, destination));
            operand = $"{Operands.Name(destination, width)}, {Operands.Name(newValue, width)}";
        }

        return new TestCase
        {
            Family = "CMPXCHG",
            Description = $"CMPXCHG {operand}" + (matches ? " (matches)" : " (differs)"),
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
        };
    }

    /// <summary>0F C7 /1, memory only: compares EDX:EAX with the 64-bit destination; on a match, loads ECX:EBX into it.</summary>
    private static TestCase GenerateCmpxchg8b(FamilyContext context)
    {
        Random rng = context.Rng;
        bool matches = rng.Next(2) == 0;

        uint[] gpr = context.FreshGpr();
        ulong destinationValue = ((ulong)rng.NextUInt32() << 32) | rng.NextUInt32();
        ulong comparator = matches ? destinationValue : destinationValue ^ 1;
        gpr[(int)Reg.Eax] = (uint)comparator;
        gpr[(int)Reg.Edx] = (uint)(comparator >> 32);
        gpr[(int)Reg.Ebx] = rng.NextUInt32();
        gpr[(int)Reg.Ecx] = rng.NextUInt32();

        // EAX, EDX, EBX and ECX are all implicit operands; the address base must be one of the rest.
        Reg baseRegister = rng.Next(3) switch { 0 => Reg.Ebp, 1 => Reg.Esi, _ => Reg.Edi };
        int displacement = InstructionEncoding.RandomDisplacement(rng);
        gpr[(int)baseRegister] = context.BufferAddress;

        byte[] buffer = context.FreshBuffer();
        Array.Copy(BitConverter.GetBytes(destinationValue), 0, buffer, displacement, 8);

        var code = new List<byte> { 0x0F, 0xC7 };
        code.Add(InstructionEncoding.ModRmMemory(1, baseRegister));
        InstructionEncoding.AppendDisplacement(code, displacement);

        return new TestCase
        {
            Family = "CMPXCHG8B",
            Description = $"CMPXCHG8B [{baseRegister}+{displacement}]" + (matches ? " (matches)" : " (differs)"),
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,   // only ZF changes; the rest are simply preserved, on both engines alike.
            Model = CpuModel.Pentium,
        };
    }

    /// <summary>87 /r (word/dword) or 86 /r (byte): XCHG r/m, r.</summary>
    private static TestCase GenerateXchg(FamilyContext context)
    {
        Random rng = context.Rng;
        Width width = Widths[rng.Next(Widths.Length)];
        bool memory = rng.Next(2) == 0;

        uint[] gpr = context.FreshGpr();
        var code = new List<byte>();
        InstructionEncoding.AppendOperandSizePrefix(code, width);
        code.Add(width == Width.Byte ? (byte)0x86 : (byte)0x87);

        string operand;
        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;

            if (width == Width.Byte)
            {
                Reg8 other = Operands.PickByteRegister(rng, baseRegister);
                code.Add(InstructionEncoding.ModRmMemory((int)other, baseRegister));
                operand = $"[{baseRegister}+{displacement}] ({width}), {other}";
            }
            else
            {
                Reg other = InstructionEncoding.PickRegister(rng, baseRegister);
                code.Add(InstructionEncoding.ModRmMemory((int)other, baseRegister));
                operand = $"[{baseRegister}+{displacement}] ({width}), {Operands.Name(other, width)}";
            }
            InstructionEncoding.AppendDisplacement(code, displacement);
        }
        else if (width == Width.Byte)
        {
            Reg8 left = Operands.PickByteRegister(rng);
            Reg8 right = Operands.PickByteRegister(rng, exclude: left);
            code.Add(InstructionEncoding.ModRmRegister((int)right, (int)left));
            operand = $"{left}, {right}";
        }
        else
        {
            Reg left = InstructionEncoding.PickRegister(rng);
            Reg right = InstructionEncoding.PickRegister(rng, left);
            code.Add(InstructionEncoding.ModRmRegister((int)right, left));
            operand = $"{Operands.Name(left, width)}, {Operands.Name(right, width)}";
        }

        return new TestCase
        {
            Family = "XCHG",
            Description = $"XCHG {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = context.FreshBuffer(),
            DefinedFlagsMask = FlagsSpec.AllArithmetic,   // XCHG touches no flag at all.
        };
    }
}
