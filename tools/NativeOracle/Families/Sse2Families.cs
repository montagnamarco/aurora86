using Aurora86.Core.Cpu;
using Aurora86.Core.Cpu.Sse;

namespace NativeOracle.Families;

/// <summary>
/// The Pentium 4's own SSE2 instructions (Cpu.Sse2.cs/Cpu.Sse2.Arithmetic.cs/
/// Cpu.Sse2.Integer.cs), verified against the host CPU's own SSE2 unit
/// through the same FXSAVE/FXRSTOR path <see cref="SseFamilies"/> already
/// uses for SSE -- every case here selects <see cref="CpuModel.Pentium4"/>,
/// the first model in Aurora's catalog with CPUID.SSE2, and randomizes RC,
/// FZ and (work package P4.2's own addition, since Northwood's MXCSR_MASK
/// carries bit 6) DAZ, the same way <see cref="SseFamilies.RandomMxcsr"/>
/// randomizes RC and FZ for SSE. Every exception mask stays set, for the
/// same reason SseFamilies documents: an unmasked SIMD floating-point
/// exception has no handler wrapped around the host stub's raw machine
/// code.
/// </summary>
public static class Sse2Families
{
    public static IEnumerable<Family> All()
    {
        foreach ((string name, byte opcode) in BinaryArithmetic)
            yield return new Family(name + "PD_SD", context => GenerateBinaryArith(context, name, opcode));

        foreach ((string name, byte opcode) in Logical)
            yield return new Family(name + "PD", context => GenerateLogical(context, name, opcode));

        yield return new Family("SQRTPD_SD", context => GenerateUnary(context));
        yield return new Family("CMPPD_SD", GenerateCmp);
        yield return new Family("COMISD", context => GenerateComisd(context, "COMISD", 0x2F));
        yield return new Family("UCOMISD", context => GenerateComisd(context, "UCOMISD", 0x2E));
        yield return new Family("SHUFPD", GenerateShufpd);
        yield return new Family("MOVUPD_MOVSD", GenerateMovupdMovsd);
        yield return new Family("MOVLPD", GenerateMovlpd);
        yield return new Family("MOVHPD", GenerateMovhpd);
        yield return new Family("UNPCKPD", GenerateUnpckpd);
        yield return new Family("MOVAPD", GenerateMovapd);
        yield return new Family("MOVNTPD", GenerateMovntpd);
        yield return new Family("MOVMSKPD", GenerateMovmskpd);
        yield return new Family("CVTPI2PD_CVTSI2SD", GenerateCvtToPd);
        yield return new Family("CVT_PD2PI_SD2SI", GenerateCvtFromPd);
        yield return new Family("CVT_5A", GenerateCvt5A);
        yield return new Family("CVT_5B", GenerateCvt5B);
        yield return new Family("CVT_E6", GenerateCvtE6);
        yield return new Family("LDMXCSR_DAZ", GenerateMxcsrRoundTripDaz);

        // --- the 128-bit widened integer set --------------------------------------

        foreach ((string name, byte opcode) in TwoOperandXmm)
            yield return new Family(name + "_XMM", context => GenerateTwoOperandXmm(context, name, opcode));

        yield return new Family("MOVDQA_MOVDQU", GenerateMovdqaMovdqu);
        yield return new Family("PSHUFD_HW_LW", GeneratePshuf);
        yield return new Family("PSRLDQ_PSLLDQ", GeneratePsrldqPslldq);
        yield return new Family("SHIFT_IMM_XMM", GenerateShiftImmediateXmm);
        yield return new Family("SHIFT_REG_XMM", GenerateShiftRegisterXmm);
        yield return new Family("MOVD_MOVQ_XMM", GenerateMovdMovqXmm);
        yield return new Family("MOVDQ2Q_MOVQ2DQ", GenerateMovdq2qMovq2dq);
        yield return new Family("PINSRW_XMM", GeneratePinsrwXmm);
        yield return new Family("PEXTRW_XMM", GeneratePextrwXmm);
        yield return new Family("PMOVMSKB_XMM", GeneratePmovmskbXmm);
        yield return new Family("MOVNTDQ", GenerateMovntdq);
        yield return new Family("MASKMOVDQU", GenerateMaskmovdqu);
        yield return new Family("PADDQ_PSUBQ_PMULUDQ_MMX", GeneratePaddqPsubqPmuludqMmx);
        yield return new Family("MOVNTI", GenerateMovnti);
    }

    private static readonly (string Name, byte Opcode)[] BinaryArithmetic =
    [
        ("ADD", 0x58), ("MUL", 0x59), ("SUB", 0x5C), ("MIN", 0x5D), ("DIV", 0x5E), ("MAX", 0x5F),
    ];

    private static readonly (string Name, byte Opcode)[] Logical =
    [
        ("AND", 0x54), ("ANDN", 0x55), ("OR", 0x56), ("XOR", 0x57),
    ];

    /// <summary>Every widened MMX-integer opcode that decomposes per-half (Cpu.Sse2.Integer.cs's own remark) or combines both halves (PACK*/PUNPCK*) -- one family per opcode, register or aligned-memory source chosen at random.</summary>
    private static readonly (string Name, byte Opcode)[] TwoOperandXmm =
    [
        ("PACKSSWB", 0x63), ("PACKSSDW", 0x6B), ("PACKUSWB", 0x67),
        ("PUNPCKLBW", 0x60), ("PUNPCKLWD", 0x61), ("PUNPCKLDQ", 0x62), ("PUNPCKLQDQ", 0x6C),
        ("PUNPCKHBW", 0x68), ("PUNPCKHWD", 0x69), ("PUNPCKHDQ", 0x6A), ("PUNPCKHQDQ", 0x6D),
        ("PADDB", 0xFC), ("PADDW", 0xFD), ("PADDD", 0xFE), ("PADDQ", 0xD4),
        ("PADDSB", 0xEC), ("PADDSW", 0xED), ("PADDUSB", 0xDC), ("PADDUSW", 0xDD),
        ("PSUBB", 0xF8), ("PSUBW", 0xF9), ("PSUBD", 0xFA), ("PSUBQ", 0xFB),
        ("PSUBSB", 0xE8), ("PSUBSW", 0xE9), ("PSUBUSB", 0xD8), ("PSUBUSW", 0xD9),
        ("PMULLW", 0xD5), ("PMULHW", 0xE5), ("PMULHUW", 0xE4), ("PMULUDQ", 0xF4), ("PMADDWD", 0xF5),
        ("PCMPEQB", 0x74), ("PCMPEQW", 0x75), ("PCMPEQD", 0x76),
        ("PCMPGTB", 0x64), ("PCMPGTW", 0x65), ("PCMPGTD", 0x66),
        ("PAND", 0xDB), ("PANDN", 0xDF), ("POR", 0xEB), ("PXOR", 0xEF),
        ("PAVGB", 0xE0), ("PAVGW", 0xE3), ("PMINSW", 0xEA), ("PMAXSW", 0xEE),
        ("PMINUB", 0xDA), ("PMAXUB", 0xDE), ("PSADBW", 0xF6),
    ];

    // --- randomness -----------------------------------------------------------------

    private static ulong NextUlong(Random rng) => ((ulong)rng.NextUInt32() << 32) | rng.NextUInt32();

    private static ulong NonzeroLow51(Random rng)
    {
        ulong value = NextUlong(rng) & 0x0007_FFFF_FFFF_FFFFul;
        return value == 0 ? 1ul : value;
    }

    private static ulong NonzeroLow52(Random rng)
    {
        ulong value = NextUlong(rng) & 0x000F_FFFF_FFFF_FFFFul;
        return value == 0 ? 1ul : value;
    }

    /// <summary>
    /// One IEEE double, mixing the special values the contract names (+-0,
    /// +-infinity, QNaN/SNaN, denormal, DBL_MAX/smallest-normal and their
    /// neighbours) with plain random bits, the same shape
    /// <see cref="SseFamilies.RandomSingleBits"/> uses for single -- plus,
    /// a review finding's own hand-derived pair: 0x3FEFFFFFFFFFFFFF (just
    /// below 1.0 by exactly the smallest normal's own ULP) and
    /// 0x001FFFFFFFFFFFFF (just below twice the smallest normal), the two
    /// values whose product/quotient against the smallest normal itself
    /// lands exactly on the tie-to-normal rounding boundary that caught two
    /// real bugs in PackNormalized (see DoubleFloat.cs's own remarks) --
    /// mixing them into the general pool, not just the dedicated unit
    /// tests, so random pairings of them against everything else in the
    /// pool keep exercising that boundary under every operation and every
    /// rounding mode.
    /// </summary>
    private static ulong RandomDoubleBits(Random rng)
    {
        ulong sign = (ulong)rng.Next(2) << 63;
        return rng.Next(18) switch
        {
            0 => 0x0000_0000_0000_0000ul,
            1 => 0x8000_0000_0000_0000ul,
            2 => 0x7FF0_0000_0000_0000ul,
            3 => 0xFFF0_0000_0000_0000ul,
            4 => sign | 0x7FF8_0000_0000_0000ul | (NextUlong(rng) & 0x0007_FFFF_FFFF_FFFFul),
            5 => sign | 0x7FF0_0000_0000_0000ul | NonzeroLow51(rng),
            6 => sign | NonzeroLow52(rng),
            7 => sign | 0x7FEF_FFFF_FFFF_FFFFul,
            8 => sign | 0x0010_0000_0000_0000ul,
            9 => sign | 0x7FEF_FFFF_FFFF_FFFEul,
            10 => sign | 0x000F_FFFF_FFFF_FFFFul,
            11 => sign | 0x3FEF_FFFF_FFFF_FFFFul,   // tie-to-normal band: just below 1.0 by the smallest normal's own ULP.
            12 => sign | 0x001F_FFFF_FFFF_FFFFul,   // tie-to-normal band: just below twice the smallest normal.
            _ => NextUlong(rng),
        };
    }

    private static XmmValue RandomXmmDouble(Random rng)
    {
        ulong low = RandomDoubleBits(rng);
        ulong high = RandomDoubleBits(rng);
        return XmmValue.FromHalves(low, high);
    }

    private static XmmValue[] RandomXmmBank(Random rng)
    {
        var bank = new XmmValue[8];
        for (int i = 0; i < 8; i++) bank[i] = RandomXmmDouble(rng);
        return bank;
    }

    private static ulong[] RandomMmxBank(Random rng)
    {
        var mmx = new ulong[8];
        for (int i = 0; i < 8; i++) mmx[i] = NextUlong(rng);
        return mmx;
    }

    /// <summary>A random rounding mode, FZ bit and (Northwood's own addition) DAZ bit, every exception always masked.</summary>
    private static uint RandomMxcsr(Random rng)
    {
        uint rc = (uint)rng.Next(4);
        uint fz = (uint)rng.Next(2);
        uint daz = (uint)rng.Next(2);
        return 0x1F80u | (rc << MxcsrBits.RoundingControlShift) | (fz << 15) | (daz << 6);
    }

    private static void WriteXmmToBuffer(byte[] buffer, int offset, XmmValue value)
    {
        BitConverter.GetBytes(value.D0).CopyTo(buffer, offset);
        BitConverter.GetBytes(value.D1).CopyTo(buffer, offset + 4);
        BitConverter.GetBytes(value.D2).CopyTo(buffer, offset + 8);
        BitConverter.GetBytes(value.D3).CopyTo(buffer, offset + 12);
    }

    private static int RandomAlignedDisplacement(Random rng) => rng.Next(0, 24) * 16;

    // --- ADDPD/ADDSD, MULPD/MULSD, SUBPD/SUBSD, MINPD/MINSD, DIVPD/DIVSD, MAXPD/MAXSD ---

    private static TestCase GenerateBinaryArith(FamilyContext context, string name, byte opcode)
    {
        Random rng = context.Rng;
        bool scalar = rng.Next(2) == 0;
        bool memory = rng.Next(2) == 0;

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);

        var code = new List<byte> { scalar ? (byte)0xF2 : (byte)0x66, 0x0F, opcode };
        string mnemonic = name + (scalar ? "SD" : "PD");
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = scalar ? InstructionEncoding.RandomDisplacement(rng) : RandomAlignedDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);

            if (scalar)
            {
                BitConverter.GetBytes(RandomDoubleBits(rng)).CopyTo(buffer, displacement);
            }
            else WriteXmmToBuffer(buffer, displacement, RandomXmmDouble(rng));
            operand = $"XMM{destination}, [{baseRegister}+{displacement}]";
        }
        else
        {
            int source = rng.Next(8);
            code.Add(InstructionEncoding.ModRmRegister(destination, source));
            operand = $"XMM{destination}, XMM{source}";
        }

        return new TestCase
        {
            Family = name + "PD_SD",
            Description = $"{mnemonic} {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- SQRTPD/SQRTSD ----------------------------------------------------------------

    private static TestCase GenerateUnary(FamilyContext context)
    {
        Random rng = context.Rng;
        bool scalar = rng.Next(2) == 0;
        bool memory = rng.Next(2) == 0;

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);

        var code = new List<byte> { scalar ? (byte)0xF2 : (byte)0x66, 0x0F, 0x51 };
        string mnemonic = scalar ? "SQRTSD" : "SQRTPD";
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = scalar ? InstructionEncoding.RandomDisplacement(rng) : RandomAlignedDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);

            if (scalar)
            {
                BitConverter.GetBytes(RandomDoubleBits(rng)).CopyTo(buffer, displacement);
            }
            else WriteXmmToBuffer(buffer, displacement, RandomXmmDouble(rng));
            operand = $"XMM{destination}, [{baseRegister}+{displacement}]";
        }
        else
        {
            int source = rng.Next(8);
            code.Add(InstructionEncoding.ModRmRegister(destination, source));
            operand = $"XMM{destination}, XMM{source}";
        }

        return new TestCase
        {
            Family = "SQRTPD_SD",
            Description = $"{mnemonic} {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- ANDPD, ANDNPD, ORPD, XORPD ----------------------------------------------------

    private static TestCase GenerateLogical(FamilyContext context, string name, byte opcode)
    {
        Random rng = context.Rng;
        bool memory = rng.Next(2) == 0;

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);

        var code = new List<byte> { 0x66, 0x0F, opcode };
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = RandomAlignedDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            WriteXmmToBuffer(buffer, displacement, RandomXmmDouble(rng));
            operand = $"XMM{destination}, [{baseRegister}+{displacement}]";
        }
        else
        {
            int source = rng.Next(8);
            code.Add(InstructionEncoding.ModRmRegister(destination, source));
            operand = $"XMM{destination}, XMM{source}";
        }

        return new TestCase
        {
            Family = name + "PD",
            Description = $"{name}PD {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- CMPPD/CMPSD --------------------------------------------------------------------

    private static TestCase GenerateCmp(FamilyContext context)
    {
        Random rng = context.Rng;
        bool scalar = rng.Next(2) == 0;
        bool memory = rng.Next(2) == 0;
        byte predicate = (byte)rng.Next(8);

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);

        var code = new List<byte> { scalar ? (byte)0xF2 : (byte)0x66, 0x0F, 0xC2 };
        string mnemonic = scalar ? "CMPSD" : "CMPPD";
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = scalar ? InstructionEncoding.RandomDisplacement(rng) : RandomAlignedDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);

            if (scalar)
            {
                BitConverter.GetBytes(RandomDoubleBits(rng)).CopyTo(buffer, displacement);
            }
            else WriteXmmToBuffer(buffer, displacement, RandomXmmDouble(rng));
            operand = $"XMM{destination}, [{baseRegister}+{displacement}], {predicate}";
        }
        else
        {
            int source = rng.Next(8);
            code.Add(InstructionEncoding.ModRmRegister(destination, source));
            operand = $"XMM{destination}, XMM{source}, {predicate}";
        }

        code.Add(predicate);

        return new TestCase
        {
            Family = "CMPPD_SD",
            Description = $"{mnemonic} {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- COMISD/UCOMISD -------------------------------------------------------------------

    private static TestCase GenerateComisd(FamilyContext context, string name, byte opcode)
    {
        Random rng = context.Rng;
        bool memory = rng.Next(2) == 0;

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);

        var code = new List<byte> { 0x66, 0x0F, opcode };
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            BitConverter.GetBytes(RandomDoubleBits(rng)).CopyTo(buffer, displacement);
            operand = $"XMM{destination}, [{baseRegister}+{displacement}]";
        }
        else
        {
            int source = rng.Next(8);
            code.Add(InstructionEncoding.ModRmRegister(destination, source));
            operand = $"XMM{destination}, XMM{source}";
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
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- SHUFPD ------------------------------------------------------------------------

    private static TestCase GenerateShufpd(FamilyContext context)
    {
        Random rng = context.Rng;
        bool memory = rng.Next(2) == 0;
        byte imm8 = (byte)rng.Next(256);

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);

        var code = new List<byte> { 0x66, 0x0F, 0xC6 };
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = RandomAlignedDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            WriteXmmToBuffer(buffer, displacement, RandomXmmDouble(rng));
            operand = $"XMM{destination}, [{baseRegister}+{displacement}], {imm8}";
        }
        else
        {
            int source = rng.Next(8);
            code.Add(InstructionEncoding.ModRmRegister(destination, source));
            operand = $"XMM{destination}, XMM{source}, {imm8}";
        }

        code.Add(imm8);

        return new TestCase
        {
            Family = "SHUFPD",
            Description = $"SHUFPD {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- MOVUPD/MOVSD --------------------------------------------------------------------

    private static TestCase GenerateMovupdMovsd(FamilyContext context)
    {
        Random rng = context.Rng;
        bool scalar = rng.Next(2) == 0;
        bool store = rng.Next(2) == 0;
        bool memory = rng.Next(2) == 0;

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int regField = rng.Next(8);

        var code = new List<byte> { scalar ? (byte)0xF2 : (byte)0x66, 0x0F, store ? (byte)0x11 : (byte)0x10 };
        string mnemonic = scalar ? "MOVSD" : "MOVUPD";
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(regField, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);

            if (!store)
            {
                if (scalar)
                {
                    BitConverter.GetBytes(RandomDoubleBits(rng)).CopyTo(buffer, displacement);
                }
                else WriteXmmToBuffer(buffer, displacement, RandomXmmDouble(rng));
            }
            operand = store ? $"[{baseRegister}+{displacement}], XMM{regField}" : $"XMM{regField}, [{baseRegister}+{displacement}]";
        }
        else
        {
            int other = rng.Next(8);
            code.Add(InstructionEncoding.ModRmRegister(regField, other));
            operand = store ? $"XMM{other}, XMM{regField}" : $"XMM{regField}, XMM{other}";
        }

        return new TestCase
        {
            Family = "MOVUPD_MOVSD",
            Description = $"{mnemonic} {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- MOVLPD / MOVHPD (memory only, both directions) -----------------------------------

    private static TestCase GenerateHalfMoveMemoryOnly(FamilyContext context, string name, byte loadOpcode, byte storeOpcode, bool high)
    {
        Random rng = context.Rng;
        bool store = rng.Next(2) == 0;

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int regField = rng.Next(8);

        Reg baseRegister = InstructionEncoding.PickRegister(rng);
        int displacement = InstructionEncoding.RandomDisplacement(rng);
        gpr[(int)baseRegister] = context.BufferAddress;

        var code = new List<byte> { 0x66, 0x0F, store ? storeOpcode : loadOpcode };
        code.Add(InstructionEncoding.ModRmMemory(regField, baseRegister));
        InstructionEncoding.AppendDisplacement(code, displacement);

        if (!store)
        {
            BitConverter.GetBytes(RandomDoubleBits(rng)).CopyTo(buffer, displacement);
        }

        return new TestCase
        {
            Family = name,
            Description = $"{name} {(store ? $"[{baseRegister}+{displacement}], XMM{regField}" : $"XMM{regField}, [{baseRegister}+{displacement}]")}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    private static TestCase GenerateMovlpd(FamilyContext context) => GenerateHalfMoveMemoryOnly(context, "MOVLPD", 0x12, 0x13, high: false);
    private static TestCase GenerateMovhpd(FamilyContext context) => GenerateHalfMoveMemoryOnly(context, "MOVHPD", 0x16, 0x17, high: true);

    // --- UNPCKLPD/UNPCKHPD ----------------------------------------------------------------

    private static TestCase GenerateUnpckpd(FamilyContext context)
    {
        Random rng = context.Rng;
        bool low = rng.Next(2) == 0;
        bool memory = rng.Next(2) == 0;

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);

        var code = new List<byte> { 0x66, 0x0F, low ? (byte)0x14 : (byte)0x15 };
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = RandomAlignedDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            WriteXmmToBuffer(buffer, displacement, RandomXmmDouble(rng));
            operand = $"XMM{destination}, [{baseRegister}+{displacement}]";
        }
        else
        {
            int source = rng.Next(8);
            code.Add(InstructionEncoding.ModRmRegister(destination, source));
            operand = $"XMM{destination}, XMM{source}";
        }

        return new TestCase
        {
            Family = "UNPCKPD",
            Description = $"{(low ? "UNPCKLPD" : "UNPCKHPD")} {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- MOVAPD ------------------------------------------------------------------------

    private static TestCase GenerateMovapd(FamilyContext context)
    {
        Random rng = context.Rng;
        bool store = rng.Next(2) == 0;
        bool memory = rng.Next(2) == 0;

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int regField = rng.Next(8);

        var code = new List<byte> { 0x66, 0x0F, store ? (byte)0x29 : (byte)0x28 };
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = RandomAlignedDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(regField, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            if (!store)
            {
                WriteXmmToBuffer(buffer, displacement, RandomXmmDouble(rng));
            }
            operand = store ? $"[{baseRegister}+{displacement}], XMM{regField}" : $"XMM{regField}, [{baseRegister}+{displacement}]";
        }
        else
        {
            int other = rng.Next(8);
            code.Add(InstructionEncoding.ModRmRegister(regField, other));
            operand = store ? $"XMM{other}, XMM{regField}" : $"XMM{regField}, XMM{other}";
        }

        return new TestCase
        {
            Family = "MOVAPD",
            Description = $"MOVAPD {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- MOVNTPD -----------------------------------------------------------------------

    private static TestCase GenerateMovntpd(FamilyContext context)
    {
        Random rng = context.Rng;
        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int regField = rng.Next(8);

        Reg baseRegister = InstructionEncoding.PickRegister(rng);
        int displacement = RandomAlignedDisplacement(rng);
        gpr[(int)baseRegister] = context.BufferAddress;

        var code = new List<byte> { 0x66, 0x0F, 0x2B };
        code.Add(InstructionEncoding.ModRmMemory(regField, baseRegister));
        InstructionEncoding.AppendDisplacement(code, displacement);

        return new TestCase
        {
            Family = "MOVNTPD",
            Description = $"MOVNTPD [{baseRegister}+{displacement}], XMM{regField}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- MOVMSKPD ----------------------------------------------------------------------

    private static TestCase GenerateMovmskpd(FamilyContext context)
    {
        Random rng = context.Rng;
        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        Reg destination = InstructionEncoding.PickRegister(rng);
        int source = rng.Next(8);

        var code = new List<byte> { 0x66, 0x0F, 0x50, InstructionEncoding.ModRmRegister((int)destination, source) };

        return new TestCase
        {
            Family = "MOVMSKPD",
            Description = $"MOVMSKPD {destination}, XMM{source}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- CVTPI2PD/CVTSI2SD --------------------------------------------------------------

    private static TestCase GenerateCvtToPd(FamilyContext context)
    {
        Random rng = context.Rng;
        bool scalar = rng.Next(2) == 0;
        bool memory = rng.Next(2) == 0;

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);
        ulong[]? mmx = null;

        var code = new List<byte> { scalar ? (byte)0xF2 : (byte)0x66, 0x0F, 0x2A };
        string mnemonic = scalar ? "CVTSI2SD" : "CVTPI2PD";
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);

            BitConverter.GetBytes(rng.NextUInt32()).CopyTo(buffer, displacement);
            if (!scalar)
            {
                BitConverter.GetBytes(rng.NextUInt32()).CopyTo(buffer, displacement + 4);
            }
            operand = $"XMM{destination}, [{baseRegister}+{displacement}]";
        }
        else if (scalar)
        {
            Reg source = InstructionEncoding.PickRegister(rng);
            gpr[(int)source] = rng.NextUInt32();
            code.Add(InstructionEncoding.ModRmRegister(destination, source));
            operand = $"XMM{destination}, {source}";
        }
        else
        {
            int source = rng.Next(8);
            mmx = RandomMmxBank(rng);
            code.Add(InstructionEncoding.ModRmRegister(destination, source));
            operand = $"XMM{destination}, MM{source}";
        }

        return new TestCase
        {
            Family = "CVTPI2PD_CVTSI2SD",
            Description = $"{mnemonic} {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
            InitialMmx = mmx,
        };
    }

    // --- CVT(T)PD2PI/CVT(T)SD2SI ----------------------------------------------------------

    private static TestCase GenerateCvtFromPd(FamilyContext context)
    {
        Random rng = context.Rng;
        bool scalar = rng.Next(2) == 0;
        bool truncate = rng.Next(2) == 0;
        bool memory = rng.Next(2) == 0;

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int sourceRegister = rng.Next(8);
        ulong[]? mmx = null;

        var code = new List<byte> { scalar ? (byte)0xF2 : (byte)0x66, 0x0F, truncate ? (byte)0x2C : (byte)0x2D };
        string mnemonic = (truncate ? "CVTT" : "CVT") + (scalar ? "SD2SI" : "PD2PI");
        string operand;

        if (scalar)
        {
            Reg destination = InstructionEncoding.PickRegister(rng);
            gpr[(int)destination] = rng.NextUInt32();

            if (memory)
            {
                Reg baseRegister = InstructionEncoding.PickRegister(rng, destination);
                int displacement = InstructionEncoding.RandomDisplacement(rng);
                gpr[(int)baseRegister] = context.BufferAddress;
                code.Add(InstructionEncoding.ModRmMemory((int)destination, baseRegister));
                InstructionEncoding.AppendDisplacement(code, displacement);
                BitConverter.GetBytes(RandomDoubleBits(rng)).CopyTo(buffer, displacement);
                operand = $"{destination}, [{baseRegister}+{displacement}]";
            }
            else
            {
                code.Add(InstructionEncoding.ModRmRegister((int)destination, sourceRegister));
                operand = $"{destination}, XMM{sourceRegister}";
            }
        }
        else
        {
            int destination = rng.Next(8);
            mmx = RandomMmxBank(rng);

            if (memory)
            {
                Reg baseRegister = InstructionEncoding.PickRegister(rng);
                int displacement = RandomAlignedDisplacement(rng);
                gpr[(int)baseRegister] = context.BufferAddress;
                code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
                InstructionEncoding.AppendDisplacement(code, displacement);
                WriteXmmToBuffer(buffer, displacement, RandomXmmDouble(rng));
                operand = $"MM{destination}, [{baseRegister}+{displacement}]";
            }
            else
            {
                code.Add(InstructionEncoding.ModRmRegister(destination, sourceRegister));
                operand = $"MM{destination}, XMM{sourceRegister}";
            }
        }

        return new TestCase
        {
            Family = "CVT_PD2PI_SD2SI",
            Description = $"{mnemonic} {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
            InitialMmx = mmx,
        };
    }

    // --- 0F 5A: CVTPS2PD/CVTPD2PS/CVTSD2SS/CVTSS2SD ---------------------------------------

    private static TestCase GenerateCvt5A(FamilyContext context)
    {
        Random rng = context.Rng;
        byte[] prefixes = [0x00, 0x66, 0xF2, 0xF3];
        byte prefix = prefixes[rng.Next(4)];

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);

        var code = new List<byte>();
        if (prefix != 0)
        {
            code.Add(prefix);
        }
        code.Add(0x0F);
        code.Add(0x5A);

        bool memory = rng.Next(2) == 0;
        bool wideSource = prefix is 0x00 or 0x66;   // none: m64; 66: m128 aligned; F2/F3: scalar (m64/m32).

        string operand;
        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = wideSource ? (prefix == 0x66 ? RandomAlignedDisplacement(rng) : InstructionEncoding.RandomDisplacement(rng)) : InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);

            switch (prefix)
            {
                case 0x00: BitConverter.GetBytes(rng.NextUInt32()).CopyTo(buffer, displacement); BitConverter.GetBytes(rng.NextUInt32()).CopyTo(buffer, displacement + 4); break;
                case 0x66: WriteXmmToBuffer(buffer, displacement, RandomXmmDouble(rng)); break;
                case 0xF2: BitConverter.GetBytes(RandomDoubleBits(rng)).CopyTo(buffer, displacement); break;
                default: BitConverter.GetBytes(rng.NextUInt32()).CopyTo(buffer, displacement); break;
            }
            operand = $"XMM{destination}, [{baseRegister}+{displacement}]";
        }
        else
        {
            int source = rng.Next(8);
            code.Add(InstructionEncoding.ModRmRegister(destination, source));
            operand = $"XMM{destination}, XMM{source}";
        }

        return new TestCase
        {
            Family = "CVT_5A",
            Description = $"CVT5A[{prefix:X2}] {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- 0F 5B: CVTDQ2PS/CVTPS2DQ/CVTTPS2DQ ------------------------------------------------

    private static TestCase GenerateCvt5B(FamilyContext context)
    {
        Random rng = context.Rng;
        byte[] prefixes = [0x00, 0x66, 0xF3];
        byte prefix = prefixes[rng.Next(3)];

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);

        var code = new List<byte>();
        if (prefix != 0)
        {
            code.Add(prefix);
        }
        code.Add(0x0F);
        code.Add(0x5B);

        bool memory = rng.Next(2) == 0;
        string operand;
        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = RandomAlignedDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            WriteXmmToBuffer(buffer, displacement, RandomXmmDouble(rng));
            operand = $"XMM{destination}, [{baseRegister}+{displacement}]";
        }
        else
        {
            int source = rng.Next(8);
            code.Add(InstructionEncoding.ModRmRegister(destination, source));
            operand = $"XMM{destination}, XMM{source}";
        }

        return new TestCase
        {
            Family = "CVT_5B",
            Description = $"CVT5B[{prefix:X2}] {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- 0F E6: CVTTPD2DQ/CVTPD2DQ/CVTDQ2PD ------------------------------------------------

    private static TestCase GenerateCvtE6(FamilyContext context)
    {
        Random rng = context.Rng;
        byte[] prefixes = [0x66, 0xF2, 0xF3];
        byte prefix = prefixes[rng.Next(3)];

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);

        var code = new List<byte> { prefix, 0x0F, 0xE6 };

        bool memory = rng.Next(2) == 0;
        bool wideSource = prefix != 0xF3;
        string operand;
        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = wideSource ? RandomAlignedDisplacement(rng) : InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);

            if (wideSource)
            {
                WriteXmmToBuffer(buffer, displacement, RandomXmmDouble(rng));
            }
            else { BitConverter.GetBytes(rng.NextUInt32()).CopyTo(buffer, displacement); BitConverter.GetBytes(rng.NextUInt32()).CopyTo(buffer, displacement + 4); }
            operand = $"XMM{destination}, [{baseRegister}+{displacement}]";
        }
        else
        {
            int source = rng.Next(8);
            code.Add(InstructionEncoding.ModRmRegister(destination, source));
            operand = $"XMM{destination}, XMM{source}";
        }

        return new TestCase
        {
            Family = "CVT_E6",
            Description = $"CVTE6[{prefix:X2}] {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- LDMXCSR with DAZ (Northwood's own MXCSR_MASK bit 6) -------------------------------

    private static TestCase GenerateMxcsrRoundTripDaz(FamilyContext context)
    {
        Random rng = context.Rng;
        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();

        Reg baseRegister = InstructionEncoding.PickRegister(rng);
        int displacement = InstructionEncoding.RandomDisplacement(rng);
        gpr[(int)baseRegister] = context.BufferAddress;

        var code = new List<byte> { 0x0F, 0xAE };
        code.Add(InstructionEncoding.ModRmMemory(2, baseRegister));   // /2: LDMXCSR
        InstructionEncoding.AppendDisplacement(code, displacement);

        uint loadedMxcsr = RandomMxcsr(rng) | MxcsrBits.Daz;
        BitConverter.GetBytes(loadedMxcsr).CopyTo(buffer, displacement);

        return new TestCase
        {
            Family = "LDMXCSR_DAZ",
            Description = $"LDMXCSR [{baseRegister}+{displacement}] (DAZ set)",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // === the 128-bit widened integer set =================================================

    private static byte MmRegister(int reg, int rm) => (byte)(0xC0 | (reg << 3) | rm);

    private static TestCase GenerateTwoOperandXmm(FamilyContext context, string name, byte opcode)
    {
        Random rng = context.Rng;
        bool memory = rng.Next(2) == 0;

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);

        var code = new List<byte> { 0x66, 0x0F, opcode };
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = RandomAlignedDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            WriteXmmToBuffer(buffer, displacement, RandomXmmDouble(rng));
            operand = $"XMM{destination}, [{baseRegister}+{displacement}]";
        }
        else
        {
            int source = rng.Next(8);
            code.Add(InstructionEncoding.ModRmRegister(destination, source));
            operand = $"XMM{destination}, XMM{source}";
        }

        return new TestCase
        {
            Family = name + "_XMM",
            Description = $"{name} {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- MOVDQA / MOVDQU -----------------------------------------------------------------

    private static TestCase GenerateMovdqaMovdqu(FamilyContext context)
    {
        Random rng = context.Rng;
        bool aligned = rng.Next(2) == 0;
        bool store = rng.Next(2) == 0;
        bool memory = rng.Next(2) == 0;

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int regField = rng.Next(8);

        var code = new List<byte> { aligned ? (byte)0x66 : (byte)0xF3, 0x0F, store ? (byte)0x7F : (byte)0x6F };
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = aligned ? RandomAlignedDisplacement(rng) : InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(regField, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            if (!store)
            {
                WriteXmmToBuffer(buffer, displacement, RandomXmmDouble(rng));
            }
            operand = store ? $"[{baseRegister}+{displacement}], XMM{regField}" : $"XMM{regField}, [{baseRegister}+{displacement}]";
        }
        else
        {
            int other = rng.Next(8);
            code.Add(InstructionEncoding.ModRmRegister(regField, other));
            operand = store ? $"XMM{other}, XMM{regField}" : $"XMM{regField}, XMM{other}";
        }

        return new TestCase
        {
            Family = "MOVDQA_MOVDQU",
            Description = $"{(aligned ? "MOVDQA" : "MOVDQU")} {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- PSHUFD / PSHUFHW / PSHUFLW --------------------------------------------------------

    private static TestCase GeneratePshuf(FamilyContext context)
    {
        Random rng = context.Rng;
        byte[] prefixes = [0x66, 0xF2, 0xF3];
        byte prefix = prefixes[rng.Next(3)];
        bool memory = rng.Next(2) == 0;
        byte imm8 = (byte)rng.Next(256);

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);

        var code = new List<byte> { prefix, 0x0F, 0x70 };
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = RandomAlignedDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            WriteXmmToBuffer(buffer, displacement, RandomXmmDouble(rng));
            operand = $"XMM{destination}, [{baseRegister}+{displacement}], {imm8}";
        }
        else
        {
            int source = rng.Next(8);
            code.Add(InstructionEncoding.ModRmRegister(destination, source));
            operand = $"XMM{destination}, XMM{source}, {imm8}";
        }

        code.Add(imm8);

        return new TestCase
        {
            Family = "PSHUFD_HW_LW",
            Description = $"PSHUF[{prefix:X2}] {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- PSRLDQ / PSLLDQ ------------------------------------------------------------------

    private static TestCase GeneratePsrldqPslldq(FamilyContext context)
    {
        Random rng = context.Rng;
        bool left = rng.Next(2) == 0;
        byte count = (byte)rng.Next(20);   // occasionally past 15, to exercise the all-zero clamp.

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int register = rng.Next(8);

        var code = new List<byte> { 0x66, 0x0F, 0x73, MmRegister(left ? 7 : 3, register), count };

        return new TestCase
        {
            Family = "PSRLDQ_PSLLDQ",
            Description = $"{(left ? "PSLLDQ" : "PSRLDQ")} XMM{register}, {count}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- the immediate shift group (0F 71/72/73, reg /2/4/6) -------------------------------

    private static TestCase GenerateShiftImmediateXmm(FamilyContext context)
    {
        Random rng = context.Rng;
        (byte opcode, int[] regs)[] groups = [(0x71, [2, 4, 6]), (0x72, [2, 4, 6]), (0x73, [2, 6])];
        (byte opcode, int[] regs) = groups[rng.Next(groups.Length)];
        int reg = regs[rng.Next(regs.Length)];
        byte count = (byte)rng.Next(80);

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int register = rng.Next(8);

        var code = new List<byte> { 0x66, 0x0F, opcode, MmRegister(reg, register), count };

        return new TestCase
        {
            Family = "SHIFT_IMM_XMM",
            Description = $"SHIFT_IMM[{opcode:X2}/{reg}] XMM{register}, {count}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- the register-count shift group (D1-D3/E1-E2/F1-F3) --------------------------------

    private static TestCase GenerateShiftRegisterXmm(FamilyContext context)
    {
        Random rng = context.Rng;
        byte[] opcodes = [0xD1, 0xD2, 0xD3, 0xE1, 0xE2, 0xF1, 0xF2, 0xF3];
        byte opcode = opcodes[rng.Next(opcodes.Length)];
        bool memory = rng.Next(2) == 0;

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);

        var code = new List<byte> { 0x66, 0x0F, opcode };
        string operand;

        // A small, mostly-in-range count in the low 64 bits: real hardware
        // saturates a huge count identically, so this favours the more
        // informative near-boundary values (0, the lane width, one past it).
        ulong count = (ulong)rng.Next(0, 68);

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = RandomAlignedDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            BitConverter.GetBytes(count).CopyTo(buffer, displacement);
            BitConverter.GetBytes(0ul).CopyTo(buffer, displacement + 8);
            operand = $"XMM{destination}, [{baseRegister}+{displacement}]";
        }
        else
        {
            int source = rng.Next(8);
            xmm[source] = new XmmValue { D0 = (uint)count, D1 = (uint)(count >> 32), D2 = 0, D3 = 0 };
            code.Add(InstructionEncoding.ModRmRegister(destination, source));
            operand = $"XMM{destination}, XMM{source}";
        }

        return new TestCase
        {
            Family = "SHIFT_REG_XMM",
            Description = $"SHIFT_REG[{opcode:X2}] {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- MOVD/MOVQ's XMM forms (0F 6E, 7E, D6-66) --------------------------------------------

    private static TestCase GenerateMovdMovqXmm(FamilyContext context)
    {
        Random rng = context.Rng;
        int kind = rng.Next(4);   // 0: MOVD load, 1: MOVD store, 2: MOVQ load (F3 7E), 3: MOVQ store (66 D6)

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int regField = rng.Next(8);
        bool memory = rng.Next(2) == 0;

        var code = new List<byte>();
        string description;

        switch (kind)
        {
            case 0:
                code.Add(0x66); code.Add(0x0F); code.Add(0x6E);
                if (memory)
                {
                    Reg baseRegister = InstructionEncoding.PickRegister(rng);
                    int displacement = InstructionEncoding.RandomDisplacement(rng);
                    gpr[(int)baseRegister] = context.BufferAddress;
                    code.Add(InstructionEncoding.ModRmMemory(regField, baseRegister));
                    InstructionEncoding.AppendDisplacement(code, displacement);
                    BitConverter.GetBytes(rng.NextUInt32()).CopyTo(buffer, displacement);
                    description = $"MOVD XMM{regField}, [{baseRegister}+{displacement}]";
                }
                else
                {
                    Reg source = InstructionEncoding.PickRegister(rng);
                    gpr[(int)source] = rng.NextUInt32();
                    code.Add(InstructionEncoding.ModRmRegister(regField, source));
                    description = $"MOVD XMM{regField}, {source}";
                }
                break;

            case 1:
                code.Add(0x66); code.Add(0x0F); code.Add(0x7E);
                if (memory)
                {
                    Reg baseRegister = InstructionEncoding.PickRegister(rng);
                    int displacement = InstructionEncoding.RandomDisplacement(rng);
                    gpr[(int)baseRegister] = context.BufferAddress;
                    code.Add(InstructionEncoding.ModRmMemory(regField, baseRegister));
                    InstructionEncoding.AppendDisplacement(code, displacement);
                    description = $"MOVD [{baseRegister}+{displacement}], XMM{regField}";
                }
                else
                {
                    Reg destination = InstructionEncoding.PickRegister(rng);
                    code.Add(InstructionEncoding.ModRmRegister(regField, destination));
                    description = $"MOVD {destination}, XMM{regField}";
                }
                break;

            case 2:
                code.Add(0xF3); code.Add(0x0F); code.Add(0x7E);
                if (memory)
                {
                    Reg baseRegister = InstructionEncoding.PickRegister(rng);
                    int displacement = InstructionEncoding.RandomDisplacement(rng);
                    gpr[(int)baseRegister] = context.BufferAddress;
                    code.Add(InstructionEncoding.ModRmMemory(regField, baseRegister));
                    InstructionEncoding.AppendDisplacement(code, displacement);
                    BitConverter.GetBytes(RandomDoubleBits(rng)).CopyTo(buffer, displacement);
                    description = $"MOVQ XMM{regField}, [{baseRegister}+{displacement}]";
                }
                else
                {
                    int source = rng.Next(8);
                    code.Add(InstructionEncoding.ModRmRegister(regField, source));
                    description = $"MOVQ XMM{regField}, XMM{source}";
                }
                break;

            default:
                code.Add(0x66); code.Add(0x0F); code.Add(0xD6);
                if (memory)
                {
                    Reg baseRegister = InstructionEncoding.PickRegister(rng);
                    int displacement = InstructionEncoding.RandomDisplacement(rng);
                    gpr[(int)baseRegister] = context.BufferAddress;
                    code.Add(InstructionEncoding.ModRmMemory(regField, baseRegister));
                    InstructionEncoding.AppendDisplacement(code, displacement);
                    description = $"MOVQ [{baseRegister}+{displacement}], XMM{regField}";
                }
                else
                {
                    int destination = rng.Next(8);
                    code.Add(InstructionEncoding.ModRmRegister(regField, destination));
                    description = $"MOVQ XMM{destination}, XMM{regField}";
                }
                break;
        }

        return new TestCase
        {
            Family = "MOVD_MOVQ_XMM",
            Description = description,
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- MOVDQ2Q / MOVQ2DQ ----------------------------------------------------------------

    private static TestCase GenerateMovdq2qMovq2dq(FamilyContext context)
    {
        Random rng = context.Rng;
        bool toMmx = rng.Next(2) == 0;

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        ulong[] mmx = RandomMmxBank(rng);

        int reg = rng.Next(8);
        int rm = rng.Next(8);

        var code = toMmx
            ? new List<byte> { 0xF2, 0x0F, 0xD6, MmRegister(reg, rm) }
            : new List<byte> { 0xF3, 0x0F, 0xD6, MmRegister(reg, rm) };

        return new TestCase
        {
            Family = "MOVDQ2Q_MOVQ2DQ",
            Description = toMmx ? $"MOVDQ2Q MM{reg}, XMM{rm}" : $"MOVQ2DQ XMM{reg}, MM{rm}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
            InitialMmx = mmx,
        };
    }

    // --- PINSRW/PEXTRW, widened (0F C4/C5) --------------------------------------------------

    private static TestCase GeneratePinsrwXmm(FamilyContext context)
    {
        Random rng = context.Rng;
        bool memory = rng.Next(2) == 0;
        byte imm8 = (byte)rng.Next(256);

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);

        var code = new List<byte> { 0x66, 0x0F, 0xC4 };
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            BitConverter.GetBytes((ushort)rng.Next(65536)).CopyTo(buffer, displacement);
            operand = $"XMM{destination}, [{baseRegister}+{displacement}], {imm8}";
        }
        else
        {
            Reg source = InstructionEncoding.PickRegister(rng);
            gpr[(int)source] = rng.NextUInt32();
            code.Add(InstructionEncoding.ModRmRegister(destination, source));
            operand = $"XMM{destination}, {source}, {imm8}";
        }

        code.Add(imm8);

        return new TestCase
        {
            Family = "PINSRW_XMM",
            Description = $"PINSRW {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    private static TestCase GeneratePextrwXmm(FamilyContext context)
    {
        Random rng = context.Rng;
        byte imm8 = (byte)rng.Next(256);

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        Reg destination = InstructionEncoding.PickRegister(rng);
        int source = rng.Next(8);

        var code = new List<byte> { 0x66, 0x0F, 0xC5, InstructionEncoding.ModRmRegister((int)destination, source), imm8 };

        return new TestCase
        {
            Family = "PEXTRW_XMM",
            Description = $"PEXTRW {destination}, XMM{source}, {imm8}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- PMOVMSKB, widened (0F D7) ---------------------------------------------------------

    private static TestCase GeneratePmovmskbXmm(FamilyContext context)
    {
        Random rng = context.Rng;
        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        Reg destination = InstructionEncoding.PickRegister(rng);
        int source = rng.Next(8);

        var code = new List<byte> { 0x66, 0x0F, 0xD7, InstructionEncoding.ModRmRegister((int)destination, source) };

        return new TestCase
        {
            Family = "PMOVMSKB_XMM",
            Description = $"PMOVMSKB {destination}, XMM{source}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- MOVNTDQ -----------------------------------------------------------------------------

    private static TestCase GenerateMovntdq(FamilyContext context)
    {
        Random rng = context.Rng;
        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int regField = rng.Next(8);

        Reg baseRegister = InstructionEncoding.PickRegister(rng);
        int displacement = RandomAlignedDisplacement(rng);
        gpr[(int)baseRegister] = context.BufferAddress;

        var code = new List<byte> { 0x66, 0x0F, 0xE7 };
        code.Add(InstructionEncoding.ModRmMemory(regField, baseRegister));
        InstructionEncoding.AppendDisplacement(code, displacement);

        return new TestCase
        {
            Family = "MOVNTDQ",
            Description = $"MOVNTDQ [{baseRegister}+{displacement}], XMM{regField}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- MASKMOVDQU -------------------------------------------------------------------------

    private static TestCase GenerateMaskmovdqu(FamilyContext context)
    {
        Random rng = context.Rng;
        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();

        int data = rng.Next(8);
        int mask = rng.Next(8);
        while (mask == data) mask = rng.Next(8);

        // High mask bits set often, to exercise real stores; EDI points well
        // inside the shared buffer, comfortably clear of both ends.
        xmm[mask] = new XmmValue
        {
            D0 = rng.NextUInt32() | 0x8080_8080u,
            D1 = rng.NextUInt32() | 0x8080_8080u,
            D2 = rng.NextUInt32() | 0x8080_8080u,
            D3 = rng.NextUInt32() | 0x8080_8080u,
        };

        gpr[(int)Reg.Edi] = context.BufferAddress + 512;

        var code = new List<byte> { 0x66, 0x0F, 0xF7, MmRegister(data, mask) };

        return new TestCase
        {
            Family = "MASKMOVDQU",
            Description = $"MASKMOVDQU XMM{data}, XMM{mask}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- the MMX-register forms of PADDQ/PSUBQ/PMULUDQ (0F D4/FB/F4, unprefixed) -----------

    private static TestCase GeneratePaddqPsubqPmuludqMmx(FamilyContext context)
    {
        Random rng = context.Rng;
        (string Name, byte Opcode)[] ops = [("PADDQ", 0xD4), ("PSUBQ", 0xFB), ("PMULUDQ", 0xF4)];
        (string name, byte opcode) = ops[rng.Next(ops.Length)];
        bool memory = rng.Next(2) == 0;

        ulong[] mmx = RandomMmxBank(rng);
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
            BitConverter.GetBytes(((ulong)rng.NextUInt32() << 32) | rng.NextUInt32()).CopyTo(buffer, displacement);
            operand = $"MM{destination}, [{baseRegister}+{displacement}]";
        }
        else
        {
            int source = rng.Next(8);
            code.Add(InstructionEncoding.ModRmRegister(destination, source));
            operand = $"MM{destination}, MM{source}";
        }

        return new TestCase
        {
            Family = "PADDQ_PSUBQ_PMULUDQ_MMX",
            Description = $"{name} {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.Pentium4,
            InitialMmx = mmx,
        };
    }

    // --- MOVNTI (0F C3 m32, r32) ------------------------------------------------------------

    /// <summary>
    /// MOVNTI: no XMM/MMX state at all, no flags -- a review finding
    /// (the emulator's own reg-field/rm-field mix-up on this exact opcode)
    /// is exactly why this family exists: the reg field (the source
    /// register) is deliberately drawn independently of the base register a
    /// memory operand needs, so a bug that reads the wrong ModRM field
    /// cannot pass by silently storing the same value either way would
    /// produce.
    /// </summary>
    private static TestCase GenerateMovnti(FamilyContext context)
    {
        Random rng = context.Rng;
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();

        Reg baseRegister = InstructionEncoding.PickRegister(rng);
        Reg source = InstructionEncoding.PickRegister(rng, baseRegister);
        int displacement = InstructionEncoding.RandomDisplacement(rng);
        gpr[(int)baseRegister] = context.BufferAddress;

        var code = new List<byte> { 0x0F, 0xC3 };
        code.Add(InstructionEncoding.ModRmMemory((int)source, baseRegister));
        InstructionEncoding.AppendDisplacement(code, displacement);

        return new TestCase
        {
            Family = "MOVNTI",
            Description = $"MOVNTI [{baseRegister}+{displacement}], {source}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = 0u,
            Model = CpuModel.Pentium4,
        };
    }
}
