using Aurora386.Core.Cpu;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// Work package P2.5: the MMX instruction set (Cpu.Mmx.cs), aliased onto the
/// x87 register file the way the Pentium MMX's silicon does it (see
/// Fpu/X87Unit.cs's "MMX" section and X87RawRegisterTests.cs, which already
/// covers the register-file aliasing itself). These tests are about the
/// instructions: saturation boundaries, pack/unpack ordering, PMADDWD's one
/// documented overflow case, the full 64-bit shift count, and the CR0/CPUID
/// gating every one of them shares.
/// </summary>
public class MmxInstructionTests
{
    private const uint CodeBase = 0x1000;

    private static (Cpu Cpu, PhysicalMemoryBus Bus) Run(byte[] program, CpuModel model = CpuModel.PentiumMmx, Action<Cpu>? prepare = null)
    {
        var bus = new PhysicalMemoryBus();
        var cpu = new Cpu(bus);
        cpu.State.SelectModel(model);

        foreach (SegReg segment in Enum.GetValues<SegReg>())
        {
            cpu.State.LoadSegmentRealModeReset(segment, 0);
        }

        cpu.State.Esp = 0x8000;
        cpu.State.Eip = CodeBase;
        program.CopyTo(bus.GetRamSpan(CodeBase, program.Length));
        prepare?.Invoke(cpu);

        while (cpu.State.Eip < CodeBase + program.Length)
        {
            cpu.Step();
        }

        return (cpu, bus);
    }

    /// <summary>Runs one instruction and returns the fault vector raised, or 0xFF.</summary>
    private static byte RunExpectingFault(byte[] program, CpuModel model, Action<Cpu>? prepare = null)
    {
        var bus = new PhysicalMemoryBus();
        var cpu = new Cpu(bus);
        cpu.State.SelectModel(model);

        foreach (SegReg segment in Enum.GetValues<SegReg>())
        {
            cpu.State.LoadSegmentRealModeReset(segment, 0);
        }

        cpu.State.Esp = 0x8000;
        cpu.State.Eip = CodeBase;
        program.CopyTo(bus.GetRamSpan(CodeBase, program.Length));
        prepare?.Invoke(cpu);

        byte vector = 0xFF;
        cpu.ProcessorException += (number, _, _, _) => vector = number;
        cpu.Step();
        return vector;
    }

    // --- little encoders, so the byte arithmetic lives in one place ------------

    /// <summary>mod=11: an MMX register-to-register ModRM byte.</summary>
    private static byte MmRegister(int reg, int rm) => (byte)(0xC0 | (reg << 3) | rm);

    /// <summary>mod=00, rm=110: the 16-bit direct-address ModRM byte (no base register), reg is the opcode's own field.</summary>
    private static byte MmDirectAddress(int reg) => (byte)(0x06 | (reg << 3));

    private static byte[] DispLow16(ushort address) => [(byte)address, (byte)(address >> 8)];

    private static ulong Bytes(params byte[] lanes)
    {
        ulong value = 0;
        for (int i = 0; i < lanes.Length; i++) value |= (ulong)lanes[i] << (i * 8);
        return value;
    }

    private static ulong Words(params ushort[] lanes)
    {
        ulong value = 0;
        for (int i = 0; i < lanes.Length; i++) value |= (ulong)lanes[i] << (i * 16);
        return value;
    }

    private static ulong Dwords(uint low, uint high) => low | ((ulong)high << 32);

    // --- ordinary arithmetic: wraps, does not saturate --------------------------

    [Fact]
    public void PaddbWrapsInsteadOfSaturating()
    {
        // PADDB mm0, mm1 = 0F FC C1.
        byte[] program = [0x0F, 0xFC, MmRegister(0, 1)];
        (Cpu cpu, _) = Run(program, prepare: cpu =>
        {
            cpu.Fpu.WriteMmx(0, Bytes(0xFF, 0x01));
            cpu.Fpu.WriteMmx(1, Bytes(0x01, 0x01));
        });

        // Lane 0: FF + 01 wraps to 00. Lane 1: 01 + 01 = 02, unaffected.
        Assert.Equal(Bytes(0x00, 0x02), cpu.Fpu.ReadMmx(0));
    }

    // --- saturating arithmetic: both boundaries ----------------------------------

    [Fact]
    public void PaddsbSaturatesAtBothSignedBoundaries()
    {
        // PADDSB mm0, mm1 = 0F EC C1.
        byte[] program = [0x0F, 0xEC, MmRegister(0, 1)];
        (Cpu cpu, _) = Run(program, prepare: cpu =>
        {
            // Lane 0: +100 + +100 = +200, saturates to +127 (0x7F).
            // Lane 1: -100 + -100 = -200, saturates to -128 (0x80).
            cpu.Fpu.WriteMmx(0, Bytes(100, unchecked((byte)-100)));
            cpu.Fpu.WriteMmx(1, Bytes(100, unchecked((byte)-100)));
        });

        Assert.Equal(Bytes(0x7F, 0x80), cpu.Fpu.ReadMmx(0));
    }

    [Fact]
    public void PadduswSaturatesAtTheUnsignedMaximum()
    {
        // PADDUSW mm0, mm1 = 0F DD C1.
        byte[] program = [0x0F, 0xDD, MmRegister(0, 1)];
        (Cpu cpu, _) = Run(program, prepare: cpu =>
        {
            cpu.Fpu.WriteMmx(0, Words(0xFFFF, 0x0010));
            cpu.Fpu.WriteMmx(1, Words(0x0001, 0x0020));
        });

        // Lane 0: FFFF + 0001 saturates to FFFF. Lane 1: 0010 + 0020 = 0030, no saturation.
        Assert.Equal(Words(0xFFFF, 0x0030), cpu.Fpu.ReadMmx(0));
    }

    [Fact]
    public void PsubusbClampsToZeroInsteadOfWrapping()
    {
        // PSUBUSB mm0, mm1 = 0F D8 C1.
        byte[] program = [0x0F, 0xD8, MmRegister(0, 1)];
        (Cpu cpu, _) = Run(program, prepare: cpu =>
        {
            cpu.Fpu.WriteMmx(0, Bytes(0x05));
            cpu.Fpu.WriteMmx(1, Bytes(0x10));
        });

        // An ordinary unsigned subtraction would wrap to 0xF5; PSUBUSB clamps to 0.
        Assert.Equal(0ul, cpu.Fpu.ReadMmx(0));
    }

    // --- pack and unpack: saturation and lane ordering ---------------------------

    [Fact]
    public void PacksswbSaturatesAndOrdersDestinationBeforeSource()
    {
        // PACKSSWB mm0, mm1 = 0F 63 C1.
        byte[] program = [0x0F, 0x63, MmRegister(0, 1)];
        (Cpu cpu, _) = Run(program, prepare: cpu =>
        {
            cpu.Fpu.WriteMmx(0, Words(256));   // word 0 saturates to +127
            cpu.Fpu.WriteMmx(1, Words(240));   // word 0 saturates to +127 too
        });

        // The destination's (saturated) words come first, low to high, then the source's.
        Assert.Equal(Bytes(0x7F, 0x00, 0x00, 0x00, 0x7F, 0x00, 0x00, 0x00), cpu.Fpu.ReadMmx(0));
    }

    [Fact]
    public void PunpcklbwInterleavesLowBytesDestinationFirst()
    {
        // PUNPCKLBW mm0, mm1 = 0F 60 C1.
        byte[] program = [0x0F, 0x60, MmRegister(0, 1)];
        (Cpu cpu, _) = Run(program, prepare: cpu =>
        {
            cpu.Fpu.WriteMmx(0, Bytes(0x01, 0x02, 0x03, 0x04, 0xAA, 0xAA, 0xAA, 0xAA));
            cpu.Fpu.WriteMmx(1, Bytes(0x05, 0x06, 0x07, 0x08, 0xBB, 0xBB, 0xBB, 0xBB));
        });

        Assert.Equal(Bytes(0x01, 0x05, 0x02, 0x06, 0x03, 0x07, 0x04, 0x08), cpu.Fpu.ReadMmx(0));
    }

    [Fact]
    public void PunpckhdqTakesTheHighDwordOfEachOperand()
    {
        // PUNPCKHDQ mm0, mm1 = 0F 6A C1.
        byte[] program = [0x0F, 0x6A, MmRegister(0, 1)];
        (Cpu cpu, _) = Run(program, prepare: cpu =>
        {
            cpu.Fpu.WriteMmx(0, Dwords(0x11111111, 0x22222222));
            cpu.Fpu.WriteMmx(1, Dwords(0x33333333, 0x44444444));
        });

        Assert.Equal(Dwords(0x22222222, 0x44444444), cpu.Fpu.ReadMmx(0));
    }

    // --- PMADDWD: the one documented overflow case wraps rather than faults -----

    [Fact]
    public void PmaddwdWrapsOnMinimumTimesMinimum()
    {
        // PMADDWD mm0, mm1 = 0F F5 C1.
        byte[] program = [0x0F, 0xF5, MmRegister(0, 1)];
        (Cpu cpu, _) = Run(program, prepare: cpu =>
        {
            // Every word lane is -32768; each dword's two products both equal
            // +1,073,741,824, and their sum is exactly 0x80000000 -- the SDM's
            // own cited overflow case, which wraps instead of saturating.
            cpu.Fpu.WriteMmx(0, Words(0x8000, 0x8000, 0x8000, 0x8000));
            cpu.Fpu.WriteMmx(1, Words(0x8000, 0x8000, 0x8000, 0x8000));
        });

        Assert.Equal(Dwords(0x80000000, 0x80000000), cpu.Fpu.ReadMmx(0));
    }

    [Fact]
    public void PmaddwdOrdinaryProductsSumWithoutOverflow()
    {
        byte[] program = [0x0F, 0xF5, MmRegister(0, 1)];
        (Cpu cpu, _) = Run(program, prepare: cpu =>
        {
            // Dword 0: 3*4 + 5*6 = 42. Dword 1: -2*-2 + 1*1 = 5.
            cpu.Fpu.WriteMmx(0, Words(3, 5, unchecked((ushort)-2), 1));
            cpu.Fpu.WriteMmx(1, Words(4, 6, unchecked((ushort)-2), 1));
        });

        Assert.Equal(Dwords(42, 5), cpu.Fpu.ReadMmx(0));
    }

    // --- shifts: by 0, by width-1, by width, and by a count far past it ---------

    [Fact]
    public void PsllwByZeroLeavesTheValueUnchanged()
    {
        // Group 0F 71 /6, imm8: PSLLW mm0, 0.
        byte[] program = [0x0F, 0x71, MmRegister(6, 0), 0x00];
        (Cpu cpu, _) = Run(program, prepare: cpu => cpu.Fpu.WriteMmx(0, Words(0x1234, 0x5678, 0, 0)));

        Assert.Equal(Words(0x1234, 0x5678, 0, 0), cpu.Fpu.ReadMmx(0));
    }

    [Fact]
    public void PsrlwByWidthMinusOneKeepsOnlyTheTopBit()
    {
        // Group 0F 71 /2, imm8: PSRLW mm0, 15.
        byte[] program = [0x0F, 0x71, MmRegister(2, 0), 15];
        (Cpu cpu, _) = Run(program, prepare: cpu => cpu.Fpu.WriteMmx(0, Words(0x8000)));

        Assert.Equal(Words(0x0001), cpu.Fpu.ReadMmx(0));
    }

    [Fact]
    public void PsrawByWidthMinusOneFillsWithTheSignBit()
    {
        // Group 0F 71 /4, imm8: PSRAW mm0, 15, on a negative word.
        byte[] program = [0x0F, 0x71, MmRegister(4, 0), 15];
        (Cpu cpu, _) = Run(program, prepare: cpu => cpu.Fpu.WriteMmx(0, Words(0x8000)));

        // Logical PSRLW would give 1; the arithmetic shift instead fills every
        // bit with the sign, giving -1.
        Assert.Equal(Words(0xFFFF), cpu.Fpu.ReadMmx(0));
    }

    [Fact]
    public void PslldByExactlyTheWidthGivesZero()
    {
        // Group 0F 72 /6, imm8: PSLLD mm0, 32.
        byte[] program = [0x0F, 0x72, MmRegister(6, 0), 32];
        (Cpu cpu, _) = Run(program, prepare: cpu => cpu.Fpu.WriteMmx(0, Dwords(0xFFFFFFFF, 0x12345678)));

        Assert.Equal(0ul, cpu.Fpu.ReadMmx(0));
    }

    [Fact]
    public void PsradByExactlyTheWidthFillsEveryBitWithTheSign()
    {
        // Group 0F 72 /4, imm8: PSRAD mm0, 32, both dwords negative and positive.
        byte[] program = [0x0F, 0x72, MmRegister(4, 0), 32];
        (Cpu cpu, _) = Run(program, prepare: cpu => cpu.Fpu.WriteMmx(0, Dwords(0x80000000, 0x7FFFFFFF)));

        Assert.Equal(Dwords(0xFFFFFFFF, 0x00000000), cpu.Fpu.ReadMmx(0));
    }

    [Fact]
    public void ShiftCountIsTheWholeSixtyFourBitsNotJustTheLowByte()
    {
        // PSLLW mm0, mm1 -- the register form, count read from all 64 bits of
        // mm1. The low byte of mm1 is 0 here: an implementation that only
        // looked at the low byte (or the low 32 bits) would read that as "shift
        // by zero" and leave mm0 unchanged; the true count is far past 16, so
        // every word lane must come out zero.
        byte[] program = [0x0F, 0xF1, MmRegister(0, 1)];
        (Cpu cpu, _) = Run(program, prepare: cpu =>
        {
            cpu.Fpu.WriteMmx(0, Words(0x1234, 0x5678, 0x9ABC, 0xDEF0));
            cpu.Fpu.WriteMmx(1, Dwords(0x00000000, 0xFFFFFFFF));
        });

        Assert.Equal(0ul, cpu.Fpu.ReadMmx(0));
    }

    [Fact]
    public void PsrlqByExactlyTheWidthGivesZero()
    {
        // PSRLQ mm0, mm1 = 0F D3 C1, count 64 held in mm1.
        byte[] program = [0x0F, 0xD3, MmRegister(0, 1)];
        (Cpu cpu, _) = Run(program, prepare: cpu =>
        {
            cpu.Fpu.WriteMmx(0, 0xFFFFFFFFFFFFFFFFul);
            cpu.Fpu.WriteMmx(1, 64ul);
        });

        Assert.Equal(0ul, cpu.Fpu.ReadMmx(0));
    }

    // --- MOVD / MOVQ through memory ----------------------------------------------

    [Fact]
    public void MovdLoadZeroExtendsFromMemory()
    {
        // MOVD mm2, [4000] = 0F 6E ModRM(reg=2, [disp16]), disp16.
        byte[] program = [0x0F, 0x6E, MmDirectAddress(2), .. DispLow16(0x4000)];
        var bus = new PhysicalMemoryBus();
        var cpu = new Cpu(bus);
        cpu.State.SelectModel(CpuModel.PentiumMmx);
        foreach (SegReg segment in Enum.GetValues<SegReg>()) cpu.State.LoadSegmentRealModeReset(segment, 0);
        cpu.State.Esp = 0x8000;
        cpu.State.Eip = CodeBase;
        program.CopyTo(bus.GetRamSpan(CodeBase, program.Length));
        bus.WriteDword(0x4000, 0xDEADBEEF);

        cpu.Step();

        Assert.Equal(0x00000000DEADBEEFul, cpu.Fpu.ReadMmx(2));
    }

    [Fact]
    public void MovdStoreWritesOnlyTheLowDword()
    {
        // MOVD [4010], mm3 = 0F 7E ModRM(reg=3, [disp16]), disp16.
        byte[] program = [0x0F, 0x7E, MmDirectAddress(3), .. DispLow16(0x4010)];
        (Cpu cpu, PhysicalMemoryBus bus) = Run(program, prepare: cpu => cpu.Fpu.WriteMmx(3, 0x12345678AABBCCDDul));

        Assert.Equal(0xAABBCCDDu, bus.ReadDword(0x4010));
    }

    [Fact]
    public void MovqLoadReadsTheWholeQuadwordFromMemory()
    {
        // MOVQ mm1, [5000] = 0F 6F ModRM(reg=1, [disp16]), disp16.
        byte[] program = [0x0F, 0x6F, MmDirectAddress(1), .. DispLow16(0x5000)];
        var bus = new PhysicalMemoryBus();
        var cpu = new Cpu(bus);
        cpu.State.SelectModel(CpuModel.PentiumMmx);
        foreach (SegReg segment in Enum.GetValues<SegReg>()) cpu.State.LoadSegmentRealModeReset(segment, 0);
        cpu.State.Esp = 0x8000;
        cpu.State.Eip = CodeBase;
        program.CopyTo(bus.GetRamSpan(CodeBase, program.Length));
        bus.WriteDword(0x5000, 0x55667788);
        bus.WriteDword(0x5004, 0x11223344);

        cpu.Step();

        Assert.Equal(0x1122334455667788ul, cpu.Fpu.ReadMmx(1));
    }

    [Fact]
    public void MovqStoreWritesTheWholeQuadwordToMemory()
    {
        // MOVQ [5010], mm4 = 0F 7F ModRM(reg=4, [disp16]), disp16.
        byte[] program = [0x0F, 0x7F, MmDirectAddress(4), .. DispLow16(0x5010)];
        (Cpu cpu, PhysicalMemoryBus bus) = Run(program, prepare: cpu => cpu.Fpu.WriteMmx(4, 0xFEDCBA9876543210ul));

        Assert.Equal(0x76543210u, bus.ReadDword(0x5010));
        Assert.Equal(0xFEDCBA98u, bus.ReadDword(0x5014));
    }

    // --- TOP and tag effects ------------------------------------------------------

    [Fact]
    public void AnMmxInstructionSetsTopToZeroAndEveryTagValid()
    {
        // PXOR mm0, mm1 = 0F EF C1.
        byte[] program = [0x0F, 0xEF, MmRegister(0, 1)];
        (Cpu cpu, _) = Run(program);

        Assert.Equal(0, (cpu.Fpu.StatusWord >> 11) & 7);
        for (int i = 0; i < 8; i++)
        {
            Assert.NotEqual(3, (cpu.Fpu.TagWord >> (i * 2)) & 3);
        }
    }

    [Fact]
    public void EmmsTagsEveryRegisterEmpty()
    {
        byte[] program = [0x0F, 0x77];   // EMMS
        (Cpu cpu, _) = Run(program, prepare: cpu =>
        {
            cpu.Fpu.WriteMmx(0, 1);
            cpu.Fpu.EnterMmxState();
        });

        Assert.Equal(0xFFFF, cpu.Fpu.TagWord);
    }

    [Fact]
    public void FxamAfterMmxWithoutEmmsSeesTheMmxValueAsUnsupported()
    {
        // FXAM = D9 E5. MM0 = 1 has the all-ones exponent and the integer
        // bit clear, which the x87 classifies as unsupported (C3 C2 C0 = 000)
        // with the sign bit set (C1 = 1). (An FLD here would be a stack
        // overflow with every tag valid, which is not modelled, so the test
        // examines the register in place.)
        byte[] program = [0xD9, 0xE5];
        (Cpu cpu, _) = Run(program, prepare: cpu =>
        {
            cpu.Fpu.WriteMmx(0, 1);
            cpu.Fpu.EnterMmxState();   // TOP <- 0, so ST(0) is physical R0 == MM0.
        });

        const ushort conditionMask = 0x4700;   // C3, C2, C1, C0
        const ushort c1Only = 0x0200;
        Assert.Equal(c1Only, cpu.Fpu.StatusWord & conditionMask);
    }

    // --- gating: feature, CR0.EM, CR0.TS -----------------------------------------

    [Fact]
    public void MmxInstructionsAreUndefinedOnAPentiumWithoutMmx()
    {
        byte[] program = [0x0F, 0xFC, MmRegister(0, 1)];   // PADDB mm0, mm1
        byte vector = RunExpectingFault(program, CpuModel.Pentium);

        Assert.Equal(6, vector);
    }

    [Fact]
    public void MmxInstructionsAreUndefinedWithCr0EmulationSet()
    {
        byte[] program = [0x0F, 0xFC, MmRegister(0, 1)];
        byte vector = RunExpectingFault(program, CpuModel.PentiumMmx,
            cpu => cpu.State.Cr0 |= CpuState.Cr0Emulation);

        Assert.Equal(6, vector);
    }

    [Fact]
    public void AnInvalidShiftGroupEncodingIsUndefinedEvenWithTaskSwitchedSet()
    {
        // 0F 71 C0 ib is /0, not an instruction: #UD outranks #NM.
        byte[] program = [0x0F, 0x71, 0xC0, 0x01];
        byte vector = RunExpectingFault(program, CpuModel.PentiumMmx,
            cpu => cpu.State.Cr0 |= CpuState.Cr0TaskSwitched);

        Assert.Equal(6, vector);
    }

    [Fact]
    public void MmxInstructionsFaultDeviceNotAvailableWithCr0TaskSwitchedSet()
    {
        byte[] program = [0x0F, 0xFC, MmRegister(0, 1)];
        byte vector = RunExpectingFault(program, CpuModel.PentiumMmx,
            cpu => cpu.State.Cr0 |= CpuState.Cr0TaskSwitched);

        Assert.Equal(7, vector);
    }

    [Fact]
    public void EmmsIsUndefinedWithoutTheMmxFeatureToo()
    {
        byte[] program = [0x0F, 0x77];   // EMMS
        byte vector = RunExpectingFault(program, CpuModel.Pentium);

        Assert.Equal(6, vector);
    }

    [Fact]
    public void A66PrefixMakesTheSameOpcodeUndefined()
    {
        // 66 0F FC C1: the SSE2 form of PADDB does not exist here yet.
        byte[] program = [0x66, 0x0F, 0xFC, MmRegister(0, 1)];
        byte vector = RunExpectingFault(program, CpuModel.PentiumMmx);

        Assert.Equal(6, vector);
    }

    // --- the 0F 71/72/73 immediate-shift group: reg operand only, /2/4/6 only ---

    [Fact]
    public void TheImmediateShiftGroupIsUndefinedWithAMemoryOperand()
    {
        // 0F 71 /6, [disp16], imm8: PSLLW with a memory destination does not exist.
        byte[] program = [0x0F, 0x71, MmDirectAddress(6), .. DispLow16(0x4000), 0x01];
        byte vector = RunExpectingFault(program, CpuModel.PentiumMmx);

        Assert.Equal(6, vector);
    }

    [Fact]
    public void TheImmediateShiftGroupIsUndefinedOutsideItsThreeRegFields()
    {
        // 0F 71 /0, mm0, imm8: reg field 0 selects none of PSLLW/PSRLW/PSRAW.
        byte[] program = [0x0F, 0x71, MmRegister(0, 0), 0x01];
        byte vector = RunExpectingFault(program, CpuModel.PentiumMmx);

        Assert.Equal(6, vector);
    }

    [Fact]
    public void ThereIsNoArithmeticRightShiftForTheQuadwordForm()
    {
        // 0F 73 /4, mm0, imm8: there is no PSRAQ.
        byte[] program = [0x0F, 0x73, MmRegister(4, 0), 0x01];
        byte vector = RunExpectingFault(program, CpuModel.PentiumMmx);

        Assert.Equal(6, vector);
    }
}
