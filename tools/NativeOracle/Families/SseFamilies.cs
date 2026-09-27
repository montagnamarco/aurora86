using Aurora386.Core.Cpu;
using Aurora386.Core.Cpu.Sse;

namespace NativeOracle.Families;

/// <summary>
/// The Pentium III's own SSE instructions (Cpu.Sse.cs/Cpu.Sse.Arithmetic.cs),
/// verified against the host CPU's own SSE unit through the same
/// FXSAVE/FXRSTOR path <see cref="HostRunner"/> and <see cref="EmulatorRunner"/>
/// already carry for MMX (<see cref="TestCase.InitialXmm"/>,
/// <see cref="TestCase.InitialMxcsr"/>): the reserved image already holds
/// MXCSR (offset 24) and XMM0-7 (offset 160) at exactly the field a real
/// FXSAVE lays them out at, so no new host machine code is needed at all --
/// only the offsets HostState.cs and HostRunner.cs now read and write.
///
/// Every case here selects <see cref="CpuModel.PentiumIII"/> and a MXCSR
/// value with every exception mask bit set (<see cref="RandomMxcsr"/>):
/// letting an unmasked SIMD floating-point exception reach the host's own
/// raw machine code -- which has no structured exception handler wrapped
/// around it -- would deliver a real hardware #XM into the harness process
/// and bring the whole tool down, so only the rounding-control field and
/// the flush-to-zero bit are ever randomized. RCPPS/RSQRTPS are excluded
/// (coordinator decision, plans/specs/sse.md): their approximation is
/// Aurora's own choice, tested for its documented error bound and special
/// values in SseInstructionTests.cs instead. The eight MMX-register
/// instructions SSE also introduced live in <see cref="SseMmxRegisterFamilies"/>.
/// </summary>
public static class SseFamilies
{
    public static IEnumerable<Family> All()
    {
        foreach ((string name, byte opcode) in BinaryArithmetic)
            yield return new Family(name, context => GenerateBinaryArith(context, name, opcode));

        foreach ((string name, byte opcode) in Logical)
            yield return new Family(name, context => GenerateLogical(context, name, opcode));

        yield return new Family("UNPCKLPS", context => GenerateUnpck(context, "UNPCKLPS"));
        yield return new Family("UNPCKHPS", context => GenerateUnpck(context, "UNPCKHPS"));
        yield return new Family("SQRT", context => GenerateUnary(context, "SQRT", 0x51));
        yield return new Family("CMP", GenerateCmp);
        yield return new Family("COMISS", context => GenerateComiss(context, "COMISS", 0x2F));
        yield return new Family("UCOMISS", context => GenerateComiss(context, "UCOMISS", 0x2E));
        yield return new Family("SHUFPS", GenerateShufps);
        yield return new Family("MOVUPS_MOVSS", GenerateMovupsMovss);
        yield return new Family("MOVLPS_MOVHLPS", context => GenerateHalfMove(context, loadOpcode: 0x12, storeOpcode: 0x13, high: false));
        yield return new Family("MOVHPS_MOVLHPS", context => GenerateHalfMove(context, loadOpcode: 0x16, storeOpcode: 0x17, high: true));
        yield return new Family("MOVAPS", GenerateMovaps);
        yield return new Family("MOVNTPS", GenerateMovntps);
        yield return new Family("MOVMSKPS", GenerateMovmskps);
        yield return new Family("CVTPI2PS_CVTSI2SS", GenerateCvtToPs);
        yield return new Family("CVT_PS2PI_SS2SI", GenerateCvtFromPs);
        yield return new Family("LDMXCSR_STMXCSR", GenerateMxcsrRoundTrip);
    }

    private static readonly (string Name, byte Opcode)[] BinaryArithmetic =
    [
        ("ADD", 0x58), ("MUL", 0x59), ("SUB", 0x5C), ("MIN", 0x5D), ("DIV", 0x5E), ("MAX", 0x5F),
    ];

    private static readonly (string Name, byte Opcode)[] Logical =
    [
        ("AND", 0x54), ("ANDN", 0x55), ("OR", 0x56), ("XOR", 0x57),
    ];

    // --- randomness ------------------------------------------------------------

    /// <summary>
    /// One IEEE single, drawn from a pool that mixes the special values the
    /// contract names (plans/specs/sse.md's own oracle requirement) with
    /// plain random bits: +-0, +-infinity, a quiet NaN with a random
    /// payload, a signaling NaN, a denormal, the largest and smallest
    /// positive normals (either sign), the overflow band (values close
    /// enough to sqrt(FLT_MAX) that MULSS/DIVSS of two of them lands right
    /// on the FLT_MAX/infinity rounding boundary a P4.1 review finding
    /// measured -- <c>5FB504F3h</c> and <c>5F3504F3h</c> are the exact pair
    /// SseReviewFindingsTests.cs confirms on the host), the tininess band
    /// (<c>3F7FFFFFh</c> next to the smallest normal already in the pool,
    /// and DIVSS's own <c>00FFFFFFh</c>/<c>40000000h</c> pair, likewise
    /// measured there), and -- the rest of the time -- an entirely random
    /// 32-bit pattern, itself occasionally landing on one of those same
    /// special cases by chance.
    /// </summary>
    private static uint RandomSingleBits(Random rng)
    {
        uint sign = (uint)rng.Next(2) << 31;
        return rng.Next(20) switch
        {
            0 => 0x0000_0000u,
            1 => 0x8000_0000u,
            2 => 0x7F80_0000u,
            3 => 0xFF80_0000u,
            4 => sign | 0x7FC0_0000u | (rng.NextUInt32() & 0x003F_FFFFu),   // QNaN, random payload.
            5 => sign | 0x7F80_0000u | NonzeroLow22(rng),                  // SNaN: quiet bit (0x400000) forced clear.
            6 => sign | NonzeroLow23(rng),                                  // denormal: exponent 0, mantissa nonzero.
            7 => sign | 0x7F7F_FFFFu,                                       // largest finite normal (FLT_MAX).
            8 => sign | 0x0080_0000u,                                       // smallest positive normal.
            9 => sign | 0x7F7F_FFFEu,                                       // FLT_MAX, one ULP down.
            10 => sign | 0x007F_FFFFu,                                      // largest denormal, one ULP below the smallest normal.
            11 => sign | 0x5FB5_04F3u,                                      // overflow band: MULSS of two of these straddles FLT_MAX/infinity.
            12 => sign | 0x5F35_04F3u,                                      // overflow band: the fix's own second factor.
            13 => sign | 0x3F7F_FFFFu,                                      // tininess band: just below 1.0, times the smallest normal lands one ULP under it.
            14 => sign | 0x00FF_FFFFu,                                      // tininess band: DIVSS's own dividend.
            15 => sign | 0x4000_0000u,                                      // tininess band: DIVSS's own divisor (2.0).
            _ => rng.NextUInt32(),
        };
    }

    private static uint NonzeroLow22(Random rng)
    {
        uint value = rng.NextUInt32() & 0x003F_FFFFu;
        return value == 0 ? 1u : value;
    }

    private static uint NonzeroLow23(Random rng)
    {
        uint value = rng.NextUInt32() & 0x007F_FFFFu;
        return value == 0 ? 1u : value;
    }

    private static XmmValue RandomXmm(Random rng) => new()
    {
        D0 = RandomSingleBits(rng),
        D1 = RandomSingleBits(rng),
        D2 = RandomSingleBits(rng),
        D3 = RandomSingleBits(rng),
    };

    private static XmmValue[] RandomXmmBank(Random rng)
    {
        var bank = new XmmValue[8];
        for (int i = 0; i < 8; i++) bank[i] = RandomXmm(rng);
        return bank;
    }

    private static ulong[] RandomMmxBank(Random rng)
    {
        var mmx = new ulong[8];
        for (int i = 0; i < 8; i++) mmx[i] = ((ulong)rng.NextUInt32() << 32) | rng.NextUInt32();
        return mmx;
    }

    /// <summary>A random rounding mode and FZ bit, every exception always masked (see the type summary).</summary>
    private static uint RandomMxcsr(Random rng)
    {
        uint rc = (uint)rng.Next(4);
        uint fz = (uint)rng.Next(2);
        return 0x1F80u | (rc << MxcsrBits.RoundingControlShift) | (fz << 15);
    }

    private static void WriteXmmToBuffer(byte[] buffer, int offset, XmmValue value)
    {
        BitConverter.GetBytes(value.D0).CopyTo(buffer, offset);
        BitConverter.GetBytes(value.D1).CopyTo(buffer, offset + 4);
        BitConverter.GetBytes(value.D2).CopyTo(buffer, offset + 8);
        BitConverter.GetBytes(value.D3).CopyTo(buffer, offset + 12);
    }

    /// <summary>An aligned displacement into the shared buffer: a multiple of 16, comfortably inside it.</summary>
    private static int RandomAlignedDisplacement(Random rng) => rng.Next(0, 24) * 16;

    // --- ADDPS/ADDSS, MULPS/MULSS, SUBPS/SUBSS, MINPS/MINSS, DIVPS/DIVSS, MAXPS/MAXSS ---

    private static TestCase GenerateBinaryArith(FamilyContext context, string name, byte opcode)
    {
        Random rng = context.Rng;
        bool scalar = rng.Next(2) == 0;
        bool memory = rng.Next(2) == 0;

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);

        var code = new List<byte>();
        if (scalar) code.Add(0xF3);
        code.Add(0x0F);
        code.Add(opcode);

        string mnemonic = name + (scalar ? "SS" : "PS");
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = scalar ? InstructionEncoding.RandomDisplacement(rng) : RandomAlignedDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);

            if (scalar) BitConverter.GetBytes(RandomSingleBits(rng)).CopyTo(buffer, displacement);
            else WriteXmmToBuffer(buffer, displacement, RandomXmm(rng));
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
            Description = $"{mnemonic} {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.PentiumIII,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- SQRTPS/SQRTSS ------------------------------------------------------------

    private static TestCase GenerateUnary(FamilyContext context, string name, byte opcode)
    {
        Random rng = context.Rng;
        bool scalar = rng.Next(2) == 0;
        bool memory = rng.Next(2) == 0;

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);

        var code = new List<byte>();
        if (scalar) code.Add(0xF3);
        code.Add(0x0F);
        code.Add(opcode);

        string mnemonic = name + (scalar ? "SS" : "PS");
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = scalar ? InstructionEncoding.RandomDisplacement(rng) : RandomAlignedDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);

            if (scalar) BitConverter.GetBytes(RandomSingleBits(rng)).CopyTo(buffer, displacement);
            else WriteXmmToBuffer(buffer, displacement, RandomXmm(rng));
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
            Description = $"{mnemonic} {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.PentiumIII,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- ANDPS, ANDNPS, ORPS, XORPS ------------------------------------------------

    private static TestCase GenerateLogical(FamilyContext context, string name, byte opcode)
    {
        Random rng = context.Rng;
        bool memory = rng.Next(2) == 0;

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);

        var code = new List<byte> { 0x0F, opcode };
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = RandomAlignedDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            WriteXmmToBuffer(buffer, displacement, RandomXmm(rng));
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
            Description = $"{name}PS {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.PentiumIII,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- UNPCKLPS/UNPCKHPS ---------------------------------------------------------

    private static TestCase GenerateUnpck(FamilyContext context, string name)
    {
        Random rng = context.Rng;
        bool memory = rng.Next(2) == 0;
        byte opcode = name == "UNPCKLPS" ? (byte)0x14 : (byte)0x15;

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);

        var code = new List<byte> { 0x0F, opcode };
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = RandomAlignedDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            WriteXmmToBuffer(buffer, displacement, RandomXmm(rng));
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
            Model = CpuModel.PentiumIII,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- CMPPS/CMPSS ---------------------------------------------------------------

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

        var code = new List<byte>();
        if (scalar) code.Add(0xF3);
        code.Add(0x0F);
        code.Add(0xC2);

        string mnemonic = scalar ? "CMPSS" : "CMPPS";
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = scalar ? InstructionEncoding.RandomDisplacement(rng) : RandomAlignedDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);

            if (scalar) BitConverter.GetBytes(RandomSingleBits(rng)).CopyTo(buffer, displacement);
            else WriteXmmToBuffer(buffer, displacement, RandomXmm(rng));
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
            Family = "CMP",
            Description = $"{mnemonic} {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.PentiumIII,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- COMISS/UCOMISS -------------------------------------------------------------

    private static TestCase GenerateComiss(FamilyContext context, string name, byte opcode)
    {
        Random rng = context.Rng;
        bool memory = rng.Next(2) == 0;

        XmmValue[] xmm = RandomXmmBank(rng);
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
            BitConverter.GetBytes(RandomSingleBits(rng)).CopyTo(buffer, displacement);
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
            // COMISS/UCOMISS define all six arithmetic flags: ZF/PF/CF from
            // the comparison, OF/SF/AF explicitly cleared.
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.PentiumIII,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- SHUFPS ----------------------------------------------------------------------

    private static TestCase GenerateShufps(FamilyContext context)
    {
        Random rng = context.Rng;
        bool memory = rng.Next(2) == 0;
        byte imm8 = (byte)rng.Next(256);

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);

        var code = new List<byte> { 0x0F, 0xC6 };
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = RandomAlignedDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            WriteXmmToBuffer(buffer, displacement, RandomXmm(rng));
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
            Family = "SHUFPS",
            Description = $"SHUFPS {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.PentiumIII,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- MOVUPS/MOVSS ------------------------------------------------------------------

    private static TestCase GenerateMovupsMovss(FamilyContext context)
    {
        Random rng = context.Rng;
        bool scalar = rng.Next(2) == 0;
        bool store = rng.Next(2) == 0;
        bool memory = rng.Next(2) == 0;

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int regField = rng.Next(8);

        var code = new List<byte>();
        if (scalar) code.Add(0xF3);
        code.Add(0x0F);
        code.Add(store ? (byte)0x11 : (byte)0x10);

        string mnemonic = scalar ? "MOVSS" : "MOVUPS";
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
                if (scalar) BitConverter.GetBytes(RandomSingleBits(rng)).CopyTo(buffer, displacement);
                else WriteXmmToBuffer(buffer, displacement, RandomXmm(rng));
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
            Family = "MOVUPS_MOVSS",
            Description = $"{mnemonic} {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.PentiumIII,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- MOVLPS/MOVHLPS, MOVHPS/MOVLHPS --------------------------------------------

    private static TestCase GenerateHalfMove(FamilyContext context, byte loadOpcode, byte storeOpcode, bool high)
    {
        Random rng = context.Rng;
        bool store = rng.Next(2) == 0;
        // A store is memory only; a load is either a memory m64 or, mod=11,
        // the register-register MOVHLPS/MOVLHPS form.
        bool memory = store || rng.Next(2) == 0;

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int regField = rng.Next(8);

        var code = new List<byte> { 0x0F, store ? storeOpcode : loadOpcode };
        string name = !store && !memory ? (high ? "MOVLHPS" : "MOVHLPS") : (high ? "MOVHPS" : "MOVLPS");
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
                ulong halfValue = ((ulong)RandomSingleBits(rng) << 32) | RandomSingleBits(rng);
                BitConverter.GetBytes(halfValue).CopyTo(buffer, displacement);
            }
            operand = store ? $"[{baseRegister}+{displacement}], XMM{regField}" : $"XMM{regField}, [{baseRegister}+{displacement}]";
        }
        else
        {
            int other = rng.Next(8);
            code.Add(InstructionEncoding.ModRmRegister(regField, other));
            operand = $"XMM{regField}, XMM{other}";
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
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- MOVAPS ------------------------------------------------------------------------

    private static TestCase GenerateMovaps(FamilyContext context)
    {
        Random rng = context.Rng;
        bool store = rng.Next(2) == 0;
        bool memory = rng.Next(2) == 0;

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int regField = rng.Next(8);

        var code = new List<byte> { 0x0F, store ? (byte)0x29 : (byte)0x28 };
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = RandomAlignedDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(regField, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            if (!store) WriteXmmToBuffer(buffer, displacement, RandomXmm(rng));
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
            Family = "MOVAPS",
            Description = $"MOVAPS {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.PentiumIII,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- MOVNTPS -----------------------------------------------------------------------

    private static TestCase GenerateMovntps(FamilyContext context)
    {
        Random rng = context.Rng;
        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int regField = rng.Next(8);

        Reg baseRegister = InstructionEncoding.PickRegister(rng);
        int displacement = RandomAlignedDisplacement(rng);
        gpr[(int)baseRegister] = context.BufferAddress;

        var code = new List<byte> { 0x0F, 0x2B };
        code.Add(InstructionEncoding.ModRmMemory(regField, baseRegister));
        InstructionEncoding.AppendDisplacement(code, displacement);

        return new TestCase
        {
            Family = "MOVNTPS",
            Description = $"MOVNTPS [{baseRegister}+{displacement}], XMM{regField}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.PentiumIII,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- MOVMSKPS ----------------------------------------------------------------------

    private static TestCase GenerateMovmskps(FamilyContext context)
    {
        Random rng = context.Rng;
        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        Reg destination = InstructionEncoding.PickRegister(rng);
        int source = rng.Next(8);

        var code = new List<byte> { 0x0F, 0x50, InstructionEncoding.ModRmRegister((int)destination, source) };

        return new TestCase
        {
            Family = "MOVMSKPS",
            Description = $"MOVMSKPS {destination}, XMM{source}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.PentiumIII,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
        };
    }

    // --- CVTPI2PS/CVTSI2SS ---------------------------------------------------------------

    private static TestCase GenerateCvtToPs(FamilyContext context)
    {
        Random rng = context.Rng;
        bool scalar = rng.Next(2) == 0;
        bool memory = rng.Next(2) == 0;

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();
        int destination = rng.Next(8);
        ulong[]? mmx = null;

        var code = new List<byte>();
        if (scalar) code.Add(0xF3);
        code.Add(0x0F);
        code.Add(0x2A);

        string mnemonic = scalar ? "CVTSI2SS" : "CVTPI2PS";
        string operand;

        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            int displacement = InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);

            BitConverter.GetBytes(rng.NextUInt32()).CopyTo(buffer, displacement);
            if (!scalar) BitConverter.GetBytes(rng.NextUInt32()).CopyTo(buffer, displacement + 4);
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
            Family = "CVTPI2PS_CVTSI2SS",
            Description = $"{mnemonic} {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.PentiumIII,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
            InitialMmx = mmx,
        };
    }

    // --- CVT(T)PS2PI/CVT(T)SS2SI -----------------------------------------------------------

    private static TestCase GenerateCvtFromPs(FamilyContext context)
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

        var code = new List<byte>();
        if (scalar) code.Add(0xF3);
        code.Add(0x0F);
        code.Add(truncate ? (byte)0x2C : (byte)0x2D);

        string mnemonic = (truncate ? "CVTT" : "CVT") + (scalar ? "SS2SI" : "PS2PI");
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
                BitConverter.GetBytes(RandomSingleBits(rng)).CopyTo(buffer, displacement);
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
                int displacement = InstructionEncoding.RandomDisplacement(rng);
                gpr[(int)baseRegister] = context.BufferAddress;
                code.Add(InstructionEncoding.ModRmMemory(destination, baseRegister));
                InstructionEncoding.AppendDisplacement(code, displacement);
                BitConverter.GetBytes(RandomSingleBits(rng)).CopyTo(buffer, displacement);
                BitConverter.GetBytes(RandomSingleBits(rng)).CopyTo(buffer, displacement + 4);
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
            Family = "CVT_PS2PI_SS2SI",
            Description = $"{mnemonic} {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.PentiumIII,
            InitialXmm = xmm,
            InitialMxcsr = RandomMxcsr(rng),
            InitialMmx = mmx,
        };
    }

    // --- LDMXCSR/STMXCSR -----------------------------------------------------------------

    private static TestCase GenerateMxcsrRoundTrip(FamilyContext context)
    {
        Random rng = context.Rng;
        bool store = rng.Next(2) == 0;

        XmmValue[] xmm = RandomXmmBank(rng);
        uint[] gpr = context.FreshGpr();
        byte[] buffer = context.FreshBuffer();

        Reg baseRegister = InstructionEncoding.PickRegister(rng);
        int displacement = InstructionEncoding.RandomDisplacement(rng);
        gpr[(int)baseRegister] = context.BufferAddress;

        var code = new List<byte> { 0x0F, 0xAE };
        code.Add(InstructionEncoding.ModRmMemory(store ? 3 : 2, baseRegister));
        InstructionEncoding.AppendDisplacement(code, displacement);

        uint initialMxcsr = RandomMxcsr(rng);
        if (!store)
        {
            // A value distinct from the one MXCSR already holds, so a
            // successful load is actually observable.
            uint loadedMxcsr = RandomMxcsr(rng);
            BitConverter.GetBytes(loadedMxcsr).CopyTo(buffer, displacement);
        }

        return new TestCase
        {
            Family = "LDMXCSR_STMXCSR",
            Description = store ? $"STMXCSR [{baseRegister}+{displacement}]" : $"LDMXCSR [{baseRegister}+{displacement}]",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = buffer,
            DefinedFlagsMask = FlagsSpec.AllArithmetic,
            Model = CpuModel.PentiumIII,
            InitialXmm = xmm,
            InitialMxcsr = initialMxcsr,
        };
    }
}
