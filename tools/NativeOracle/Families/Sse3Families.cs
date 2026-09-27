using Aurora386.Core.Cpu;
using Aurora386.Core.Cpu.Sse;

namespace NativeOracle.Families;

/// <summary>
/// SSE3's XMM arithmetic and duplication instructions, generated for a Core2
/// host state.  Each case randomises all MXCSR rounding and denormal controls
/// while retaining masked exceptions, and mixes ordinary float bits with NaNs,
/// infinities and denormals so the oracle records silicon's lane and payload
/// choices rather than a managed floating-point approximation.
/// </summary>
public static class Sse3Families
{
    private const uint MaskedMxcsr = 0x1F80;

    private static readonly (string Name, byte Prefix, byte Opcode)[] Arithmetic =
    [
        ("HADDPS", 0xF2, 0x7C), ("HADDPD", 0x66, 0x7C),
        ("HSUBPS", 0xF2, 0x7D), ("HSUBPD", 0x66, 0x7D),
        ("ADDSUBPS", 0xF2, 0xD0), ("ADDSUBPD", 0x66, 0xD0),
    ];

    public static IEnumerable<Family> All()
    {
        foreach ((string name, byte prefix, byte opcode) in Arithmetic)
        {
            yield return new Family(
                name,
                context => GenerateTwoOperand(context, name, prefix, opcode, aligned: true));
        }

        yield return new Family(
            "MOVSLDUP",
            context => GenerateTwoOperand(context, "MOVSLDUP", 0xF3, 0x12, aligned: true));
        yield return new Family(
            "MOVSHDUP",
            context => GenerateTwoOperand(context, "MOVSHDUP", 0xF3, 0x16, aligned: true));
        yield return new Family("MOVDDUP", GenerateMovddup);
        yield return new Family("LDDQU", GenerateLddqu);
    }

    private static TestCase GenerateTwoOperand(
        FamilyContext context,
        string name,
        byte prefix,
        byte opcode,
        bool aligned)
    {
        Random rng = context.Rng;
        bool packedDouble = prefix == 0x66;
        XmmValue[] xmm = RandomXmmBank(rng, packedDouble);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);
        bool memory = rng.Next(2) == 0;
        var code = new List<byte> { prefix, 0x0F, opcode };
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = aligned ? rng.Next(0, 24) * 16 : InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            WriteXmm(buffer, displacement, RandomXmm(rng, packedDouble));
            operand = $"XMM{destination}, [{baseRegister}+{displacement}]";
        }
        else
        {
            int source = rng.Next(8);
            code.Add(InstructionEncoding.ModRmRegister(destination, source));
            operand = $"XMM{destination}, XMM{source}";
        }

        return NewCase(name, operand, code, gpr, buffer, xmm, context);
    }

    private static TestCase GenerateMovddup(FamilyContext context)
    {
        Random rng = context.Rng;
        XmmValue[] xmm = RandomXmmBank(rng, packedDouble: false);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);
        var code = new List<byte> { 0xF2, 0x0F, 0x12 };
        string operand;
        if (rng.Next(2) == 0)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            BitConverter.GetBytes(RandomUlong(rng)).CopyTo(buffer, displacement);
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            operand = $"XMM{destination}, qword [{baseRegister}+{displacement}]";
        }
        else
        {
            int source = rng.Next(8);
            code.Add(InstructionEncoding.ModRmRegister(destination, source));
            operand = $"XMM{destination}, XMM{source}";
        }

        return NewCase("MOVDDUP", operand, code, gpr, buffer, xmm, context);
    }

    private static TestCase GenerateLddqu(FamilyContext context)
    {
        Random rng = context.Rng;
        XmmValue[] xmm = RandomXmmBank(rng, packedDouble: false);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);
        Reg baseRegister = InstructionEncoding.PickRegister(rng);
        int displacement = InstructionEncoding.RandomDisplacement(rng);
        gpr[(int)baseRegister] = context.BufferAddress;
        WriteXmm(buffer, displacement, RandomXmm(rng, packedDouble: false));
        var code = new List<byte> { 0xF2, 0x0F, 0xF0, InstructionEncoding.ModRmMemory(destination, baseRegister) };
        InstructionEncoding.AppendDisplacement(code, displacement);
        return NewCase("LDDQU", $"XMM{destination}, [{baseRegister}+{displacement}]", code, gpr, buffer, xmm, context);
    }

    private static TestCase NewCase(string family, string operand, List<byte> code, uint[] gpr, byte[] buffer, XmmValue[] xmm, FamilyContext context)
    {
        return new TestCase
        {
            Family = family,
            Description = family + " " + operand,
            Instruction = [.. code],
            InitialGpr = gpr,
            InitialEflags = context.Rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = 0,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(context.Rng),
            Model = CpuModel.Core2,
        };
    }

    private static uint RandomMxcsr(Random rng)
    {
        uint rounding = (uint)rng.Next(4) << MxcsrBits.RoundingControlShift;
        uint flushToZero = (uint)rng.Next(2) << 15;
        uint denormalsAreZero = (uint)rng.Next(2) << 6;
        return MaskedMxcsr | rounding | flushToZero | denormalsAreZero;
    }

    private static XmmValue[] RandomXmmBank(Random rng, bool packedDouble)
    {
        var values = new XmmValue[8];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = RandomXmm(rng, packedDouble);
        }

        return values;
    }

    private static XmmValue RandomXmm(Random rng, bool packedDouble)
    {
        if (packedDouble)
        {
            return XmmValue.FromHalves(RandomDoubleBits(rng), RandomDoubleBits(rng));
        }

        return new XmmValue
        {
            D0 = RandomFloatBits(rng),
            D1 = RandomFloatBits(rng),
            D2 = RandomFloatBits(rng),
            D3 = RandomFloatBits(rng),
        };
    }

    private static uint RandomFloatBits(Random rng)
    {
        uint sign = (uint)rng.Next(2) << 31;
        switch (rng.Next(10))
        {
            case 0: return 0;
            case 1: return 0x8000_0000;
            case 2: return sign | 0x7F80_0000;
            case 3: return sign | 0x7FC0_0000 | (rng.NextUInt32() & 0x003F_FFFF);
            case 4: return sign | 0x7F80_0001 | (rng.NextUInt32() & 0x003F_FFFF);
            case 5: return sign | (rng.NextUInt32() & 0x007F_FFFF);
            case 6: return sign | 0x7F7F_FFFF;
            case 7: return sign | 0x0080_0000;
            default: return rng.NextUInt32();
        }
    }

    private static ulong RandomDoubleBits(Random rng)
    {
        ulong sign = (ulong)rng.Next(2) << 63;
        switch (rng.Next(10))
        {
            case 0: return 0;
            case 1: return 0x8000_0000_0000_0000;
            case 2: return sign | 0x7FF0_0000_0000_0000;
            case 3: return sign | 0x7FF8_0000_0000_0000 | (RandomUlong(rng) & 0x0007_FFFF_FFFF_FFFF);
            case 4: return sign | 0x7FF0_0000_0000_0001 | (RandomUlong(rng) & 0x0007_FFFF_FFFF_FFFF);
            case 5: return sign | (RandomUlong(rng) & 0x000F_FFFF_FFFF_FFFF);
            case 6: return sign | 0x7FEF_FFFF_FFFF_FFFF;
            case 7: return sign | 0x0010_0000_0000_0000;
            default: return RandomUlong(rng);
        }
    }

    private static ulong RandomUlong(Random rng)
    {
        return ((ulong)rng.NextUInt32() << 32) | rng.NextUInt32();
    }

    private static void WriteXmm(byte[] buffer, int offset, XmmValue value)
    {
        BitConverter.GetBytes(value.D0).CopyTo(buffer, offset);
        BitConverter.GetBytes(value.D1).CopyTo(buffer, offset + 4);
        BitConverter.GetBytes(value.D2).CopyTo(buffer, offset + 8);
        BitConverter.GetBytes(value.D3).CopyTo(buffer, offset + 12);
    }
}
