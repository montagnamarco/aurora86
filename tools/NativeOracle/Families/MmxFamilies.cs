using Aurora386.Core.Cpu;

namespace NativeOracle.Families;

/// <summary>
/// The Pentium MMX's own instructions (Cpu.Mmx.cs), verified against the host
/// CPU's own MMX unit through the FXSAVE/FXRSTOR path <see cref="HostRunner"/>
/// and <see cref="EmulatorRunner"/> both carry for exactly this purpose (see
/// <see cref="TestCase.InitialMmx"/>). Every family here is gated on
/// <see cref="CpuFeature.Mmx"/>, so every case selects
/// <see cref="CpuModel.PentiumMmx"/>.
///
/// One family per instruction group, each choosing at random among the forms
/// the SDM defines for it -- register or memory operand, and for the shifts,
/// the register/memory count form against the immediate-group encoding too.
/// A differential oracle needs no expected value of its own: whatever the
/// random operands produce on the real chip is what Aurora is checked
/// against.
/// </summary>
public static class MmxFamilies
{
    public static IEnumerable<Family> All()
    {
        foreach ((string name, byte opcode) in TwoOperand)
        {
            yield return new Family(name, context => GenerateTwoOperand(context, name, opcode));
        }

        foreach ((string name, byte regOpcode, byte immGroupOpcode, int immReg, int widthBits) in Shifts)
        {
            yield return new Family(name, context => GenerateShift(context, name, regOpcode, immGroupOpcode, immReg, widthBits));
        }

        yield return new Family("MOVD", GenerateMovd);
        yield return new Family("MOVQ", GenerateMovq);
        yield return new Family("EMMS", GenerateEmms);
    }

    /// <summary>Every MMX instruction whose ModRM reg field is the MM destination and rm field the MM/m64 source, no immediate.</summary>
    private static readonly (string Name, byte Opcode)[] TwoOperand =
    [
        ("PACKSSWB", 0x63), ("PACKSSDW", 0x6B), ("PACKUSWB", 0x67),
        ("PUNPCKLBW", 0x60), ("PUNPCKLWD", 0x61), ("PUNPCKLDQ", 0x62),
        ("PUNPCKHBW", 0x68), ("PUNPCKHWD", 0x69), ("PUNPCKHDQ", 0x6A),
        ("PADDB", 0xFC), ("PADDW", 0xFD), ("PADDD", 0xFE),
        ("PADDSB", 0xEC), ("PADDSW", 0xED),
        ("PADDUSB", 0xDC), ("PADDUSW", 0xDD),
        ("PSUBB", 0xF8), ("PSUBW", 0xF9), ("PSUBD", 0xFA),
        ("PSUBSB", 0xE8), ("PSUBSW", 0xE9),
        ("PSUBUSB", 0xD8), ("PSUBUSW", 0xD9),
        ("PMULLW", 0xD5), ("PMULHW", 0xE5), ("PMADDWD", 0xF5),
        ("PCMPEQB", 0x74), ("PCMPEQW", 0x75), ("PCMPEQD", 0x76),
        ("PCMPGTB", 0x64), ("PCMPGTW", 0x65), ("PCMPGTD", 0x66),
        ("PAND", 0xDB), ("PANDN", 0xDF), ("POR", 0xEB), ("PXOR", 0xEF),
    ];

    /// <summary>
    /// PSLLx/PSRLx/PSRAW/PSRAD: the register/memory opcode, the 0F 71/72/73
    /// immediate-group opcode and the reg field that selects this operation
    /// within it, and the element width in bits. There is no PSRAQ (no
    /// arithmetic 64-bit shift exists), so PSRLQ/PSLLQ simply have no
    /// arithmetic counterpart in this table.
    /// </summary>
    private static readonly (string Name, byte RegOpcode, byte ImmGroupOpcode, int ImmReg, int WidthBits)[] Shifts =
    [
        ("PSLLW", 0xF1, 0x71, 6, 16), ("PSLLD", 0xF2, 0x72, 6, 32), ("PSLLQ", 0xF3, 0x73, 6, 64),
        ("PSRLW", 0xD1, 0x71, 2, 16), ("PSRLD", 0xD2, 0x72, 2, 32), ("PSRLQ", 0xD3, 0x73, 2, 64),
        ("PSRAW", 0xE1, 0x71, 4, 16), ("PSRAD", 0xE2, 0x72, 4, 32),
    ];

    private static ulong NextUlong(Random rng) => ((ulong)rng.NextUInt32() << 32) | rng.NextUInt32();

    private static ulong[] RandomMmx(Random rng)
    {
        var mmx = new ulong[8];
        for (int i = 0; i < 8; i++) mmx[i] = NextUlong(rng);
        return mmx;
    }

    /// <summary>mod=11: an MMX register-to-register (or MMX-to-GPR, for MOVD) ModRM byte.</summary>
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
            DefinedFlagsMask = FlagsSpec.AllArithmetic,   // MMX touches no flag at all.
            Model = CpuModel.PentiumMmx,
            InitialMmx = mmx,
        };
    }

    /// <summary>
    /// The count is generated to land, about equally often, on 0, width-1,
    /// exactly width, and a plain random 64-bit value -- the boundaries the
    /// contract calls out plus the ordinary case, from all three legal count
    /// sources: an MM register, an m64 operand, and the immediate group.
    /// </summary>
    private static TestCase GenerateShift(FamilyContext context, string name, byte regOpcode, byte immGroupOpcode, int immReg, int widthBits)
    {
        Random rng = context.Rng;
        int mode = rng.Next(3);   // 0 = mm/mm, 1 = mm/m64, 2 = immediate group (register destination only)

        ulong[] mmx = RandomMmx(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);

        ulong count = rng.Next(4) switch
        {
            0 => 0ul,
            1 => (ulong)(widthBits - 1),
            2 => (ulong)widthBits,
            _ => NextUlong(rng),
        };

        var code = new List<byte> { 0x0F };
        string operand;

        if (mode == 2)
        {
            byte imm8 = (byte)Math.Min(count, 255ul);
            code.Add(immGroupOpcode);
            code.Add(MmRegister(immReg, destination));
            code.Add(imm8);
            operand = $"MM{destination}, {imm8} (immediate)";
        }
        else if (mode == 1)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            BitConverter.GetBytes(count).CopyTo(buffer, displacement);
            code.Add(regOpcode);
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            operand = $"MM{destination}, [{baseRegister}+{displacement}] (count {count:X16})";
        }
        else
        {
            int source = rng.Next(8);
            mmx[source] = count;
            code.Add(regOpcode);
            code.Add(MmRegister(destination, source));
            operand = $"MM{destination}, MM{source} (count {count:X16})";
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
            Model = CpuModel.PentiumMmx,
            InitialMmx = mmx,
        };
    }

    /// <summary>0F 6E (load, zero-extending) / 0F 7E (store, low dword only): MMreg, r/m32.</summary>
    private static TestCase GenerateMovd(FamilyContext context)
    {
        Random rng = context.Rng;
        bool store = rng.Next(2) == 0;
        bool memory = rng.Next(2) == 0;

        ulong[] mmx = RandomMmx(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int mmRegister = rng.Next(8);

        var code = new List<byte> { 0x0F, store ? (byte)0x7E : (byte)0x6E };
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(mmRegister, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            operand = store ? $"[{baseRegister}+{displacement}], MM{mmRegister}" : $"MM{mmRegister}, [{baseRegister}+{displacement}]";
        }
        else
        {
            Reg gp = InstructionEncoding.PickRegister(rng);
            code.Add(MmRegister(mmRegister, (int)gp));
            operand = store ? $"{gp}, MM{mmRegister}" : $"MM{mmRegister}, {gp}";
        }

        return new TestCase
        {
            Family = "MOVD",
            Description = $"MOVD {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.PentiumMmx,
            InitialMmx = mmx,
        };
    }

    /// <summary>0F 6F (load) / 0F 7F (store): MMreg, MMreg/m64, the whole quadword.</summary>
    private static TestCase GenerateMovq(FamilyContext context)
    {
        Random rng = context.Rng;
        bool store = rng.Next(2) == 0;
        bool memory = rng.Next(2) == 0;

        ulong[] mmx = RandomMmx(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int regField = rng.Next(8);   // the ModRM reg field: always the instruction's own "MMreg" operand.

        var code = new List<byte> { 0x0F, store ? (byte)0x7F : (byte)0x6F };
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(regField, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            operand = store ? $"[{baseRegister}+{displacement}], MM{regField}" : $"MM{regField}, [{baseRegister}+{displacement}]";
        }
        else
        {
            int other = rng.Next(8);
            code.Add(MmRegister(regField, other));
            operand = store ? $"MM{other}, MM{regField}" : $"MM{regField}, MM{other}";
        }

        return new TestCase
        {
            Family = "MOVQ",
            Description = $"MOVQ {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.PentiumMmx,
            InitialMmx = mmx,
        };
    }

    /// <summary>0F 77: no operand at all. Every check here is the TOP/tag-word comparison InitialMmx turns on.</summary>
    private static TestCase GenerateEmms(FamilyContext context)
    {
        Random rng = context.Rng;

        return new TestCase
        {
            Family = "EMMS",
            Description = "EMMS",
            Instruction = [0x0F, 0x77],
            InitialGpr = context.FreshGpr(),
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = context.FreshBuffer(),
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.PentiumMmx,
            InitialMmx = RandomMmx(rng),
        };
    }
}
