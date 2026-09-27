using Aurora386.Core.Cpu;

namespace NativeOracle.Families;

/// <summary>
/// The eight MMX-register instructions SSE itself introduced (Katmai New
/// Instructions -- plans/specs/sse.md §2), gated on CPUID.SSE rather than
/// CPUID.MMX even though every one of them works on the ordinary MM
/// register file exactly like <see cref="MmxFamilies"/>' own opcodes:
/// PSHUFW, PINSRW, PEXTRW, PMOVMSKB, PAVGB/W, PMULHUW, PMINSW/PMAXSW,
/// PMINUB/PMAXUB, PSADBW, MOVNTQ, MASKMOVQ. Verified the same way, through
/// the FXSAVE/FXRSTOR path (<see cref="TestCase.InitialMmx"/>); every case
/// here selects <see cref="CpuModel.PentiumIII"/>, the only model in
/// Aurora's catalog with both CPUID.MMX and CPUID.SSE.
/// </summary>
public static class SseMmxRegisterFamilies
{
    public static IEnumerable<Family> All()
    {
        foreach ((string name, byte opcode) in TwoOperand)
            yield return new Family(name, context => GenerateTwoOperand(context, name, opcode));

        yield return new Family("PSHUFW", GeneratePshufw);
        yield return new Family("PINSRW", GeneratePinsrw);
        yield return new Family("PEXTRW", GeneratePextrw);
        yield return new Family("PMOVMSKB", GeneratePmovmskb);
        yield return new Family("MOVNTQ", GenerateMovntq);
        yield return new Family("MASKMOVQ", GenerateMaskmovq);
    }

    /// <summary>PAVGB/W, PMULHUW, PMINSW/PMAXSW, PMINUB/PMAXUB, PSADBW: the same mm, mm/m64 shape <see cref="MmxFamilies"/>'s own two-operand table uses.</summary>
    private static readonly (string Name, byte Opcode)[] TwoOperand =
    [
        ("PAVGB", 0xE0), ("PAVGW", 0xE3), ("PMULHUW", 0xE4),
        ("PMINSW", 0xEA), ("PMAXSW", 0xEE), ("PMINUB", 0xDA), ("PMAXUB", 0xDE),
        ("PSADBW", 0xF6),
    ];

    private static ulong NextUlong(Random rng) => ((ulong)rng.NextUInt32() << 32) | rng.NextUInt32();

    private static ulong[] RandomMmx(Random rng)
    {
        var mmx = new ulong[8];
        for (int i = 0; i < 8; i++) mmx[i] = NextUlong(rng);
        return mmx;
    }

    private static byte MmRegister(int reg, int rm) => (byte)(0xC0 | (reg << 3) | rm);

    private static TestCase GenerateTwoOperand(FamilyContext context, string name, byte opcode)
    {
        Random rng = context.Rng;
        bool memory = rng.Next(2) == 0;

        ulong[] mmx = RandomMmx(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);

        var code = new List<byte> { 0x0F, opcode };
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            operand = $"MM{destination}, [{baseRegister}+{displacement}]";
        }
        else
        {
            int source = rng.Next(8);
            code.Add(MmRegister(destination, source));
            operand = $"MM{destination}, MM{source}";
        }

        return new TestCase
        {
            Family = name,
            Description = $"{name} {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.PentiumIII,
            InitialMmx = mmx,
        };
    }

    /// <summary>0F 70: PSHUFW mm1, mm2/m64, imm8.</summary>
    private static TestCase GeneratePshufw(FamilyContext context)
    {
        Random rng = context.Rng;
        bool memory = rng.Next(2) == 0;
        byte imm8 = (byte)rng.Next(256);

        ulong[] mmx = RandomMmx(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);

        var code = new List<byte> { 0x0F, 0x70 };
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            operand = $"MM{destination}, [{baseRegister}+{displacement}], {imm8}";
        }
        else
        {
            int source = rng.Next(8);
            code.Add(MmRegister(destination, source));
            operand = $"MM{destination}, MM{source}, {imm8}";
        }
        code.Add(imm8);

        return new TestCase
        {
            Family = "PSHUFW",
            Description = $"PSHUFW {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.PentiumIII,
            InitialMmx = mmx,
        };
    }

    /// <summary>0F C4: PINSRW mm, r32/m16, imm8.</summary>
    private static TestCase GeneratePinsrw(FamilyContext context)
    {
        Random rng = context.Rng;
        bool memory = rng.Next(2) == 0;
        byte imm8 = (byte)rng.Next(256);

        ulong[] mmx = RandomMmx(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);

        var code = new List<byte> { 0x0F, 0xC4 };
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            BitConverter.GetBytes((ushort)rng.Next(0x10000)).CopyTo(buffer, displacement);
            operand = $"MM{destination}, [{baseRegister}+{displacement}], {imm8}";
        }
        else
        {
            Reg source = InstructionEncoding.PickRegister(rng);
            gpr[(int)source] = rng.NextUInt32();
            code.Add(InstructionEncoding.ModRmRegister(destination, source));
            operand = $"MM{destination}, {source}, {imm8}";
        }
        code.Add(imm8);

        return new TestCase
        {
            Family = "PINSRW",
            Description = $"PINSRW {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.PentiumIII,
            InitialMmx = mmx,
        };
    }

    /// <summary>0F C5: PEXTRW r32, mm, imm8 -- register-register only.</summary>
    private static TestCase GeneratePextrw(FamilyContext context)
    {
        Random rng = context.Rng;
        byte imm8 = (byte)rng.Next(256);

        ulong[] mmx = RandomMmx(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        Reg destination = InstructionEncoding.PickRegister(rng);
        int source = rng.Next(8);

        var code = new List<byte> { 0x0F, 0xC5, InstructionEncoding.ModRmRegister((int)destination, source), imm8 };

        return new TestCase
        {
            Family = "PEXTRW",
            Description = $"PEXTRW {destination}, MM{source}, {imm8}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.PentiumIII,
            InitialMmx = mmx,
        };
    }

    /// <summary>0F D7: PMOVMSKB r32, mm -- register-register only.</summary>
    private static TestCase GeneratePmovmskb(FamilyContext context)
    {
        Random rng = context.Rng;
        ulong[] mmx = RandomMmx(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        Reg destination = InstructionEncoding.PickRegister(rng);
        int source = rng.Next(8);

        var code = new List<byte> { 0x0F, 0xD7, InstructionEncoding.ModRmRegister((int)destination, source) };

        return new TestCase
        {
            Family = "PMOVMSKB",
            Description = $"PMOVMSKB {destination}, MM{source}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.PentiumIII,
            InitialMmx = mmx,
        };
    }

    /// <summary>0F E7: MOVNTQ mm, m64 -- memory only.</summary>
    private static TestCase GenerateMovntq(FamilyContext context)
    {
        Random rng = context.Rng;
        ulong[] mmx = RandomMmx(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int source = rng.Next(8);

        Reg baseRegister = InstructionEncoding.PickRegister(rng);
        int displacement = InstructionEncoding.RandomDisplacement(rng);
        gpr[(int)baseRegister] = context.BufferAddress;

        var code = new List<byte> { 0x0F, 0xE7 };
        code.Add(InstructionEncoding.ModRmMemory(source, baseRegister));
        InstructionEncoding.AppendDisplacement(code, displacement);

        return new TestCase
        {
            Family = "MOVNTQ",
            Description = $"MOVNTQ [{baseRegister}+{displacement}], MM{source}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.PentiumIII,
            InitialMmx = mmx,
        };
    }

    /// <summary>
    /// 0F F7: MASKMOVQ mm, mm -- register-register only. The reg-field
    /// register supplies the data, the rm-field register the mask; EDI is
    /// pointed well inside the shared buffer (no 0x67 prefix, so the
    /// address size is whichever this mode's default is -- see the host
    /// stub's own remark on why loading a 32-bit value already zero-extends
    /// the upper half of the 64-bit register the real host CPU addresses
    /// with), so all eight possible byte stores land inside it and the
    /// ordinary whole-buffer comparison already catches a wrong address or
    /// a wrong mask bit.
    /// </summary>
    private static TestCase GenerateMaskmovq(FamilyContext context)
    {
        Random rng = context.Rng;
        ulong[] mmx = RandomMmx(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int data = rng.Next(8);
        int mask = rng.Next(8);
        while (mask == data) mask = rng.Next(8);

        gpr[(int)Reg.Edi] = context.BufferAddress + (uint)InstructionEncoding.RandomDisplacement(rng);

        var code = new List<byte> { 0x0F, 0xF7, MmRegister(data, mask) };

        return new TestCase
        {
            Family = "MASKMOVQ",
            Description = $"MASKMOVQ MM{data}, MM{mask}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.PentiumIII,
            InitialMmx = mmx,
        };
    }
}
