using Aurora386.Core.Cpu;
using Aurora386.Core.Cpu.Sse;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// Work package P4.1: SSE (plans/specs/sse.md). The twenty register-level
/// test cases the hardware-researcher's note names, in the same numbered
/// order, plus the RCPPS/RSQRTPS error-bound check and an FXSAVE/FXRSTOR
/// round trip of XMM/MXCSR the note's own item 18 implies but does not spell
/// out as a round trip. Each test drives the real interpreter -- decode,
/// dispatch, fault delivery -- the same way P6InstructionTests.cs does.
/// </summary>
public class SseInstructionTests
{
    private const uint CodeBase = 0x1000;

    private static (Cpu cpu, PhysicalMemoryBus bus) Setup(params byte[] code)
    {
        var bus = new PhysicalMemoryBus();
        var cpu = new Cpu(bus);
        foreach (SegReg s in Enum.GetValues<SegReg>())
            cpu.State.LoadSegmentRealMode(s, 0);
        cpu.State.Esp = 0x8000;
        cpu.State.Eip = CodeBase;
        code.CopyTo(bus.GetRamSpan(CodeBase, code.Length));
        return (cpu, bus);
    }

    /// <summary>
    /// A Pentium III with CR4.OSFXSR set (the ordinary case) unless
    /// <paramref name="osFxsr"/> says otherwise, and CR4.OSXMMEXCPT set only
    /// when <paramref name="osXmmExcpt"/> asks for it -- both bits are
    /// storage this test sets directly, the same way every other CPU test
    /// in this suite pokes CR0/CR4 without going through MOV CRn.
    /// </summary>
    private static (Cpu cpu, PhysicalMemoryBus bus) SetupSse(bool osFxsr = true, bool osXmmExcpt = false, params byte[] code)
    {
        var (cpu, bus) = Setup(code);
        cpu.State.SelectModel(CpuModel.PentiumIII);
        if (osFxsr) cpu.State.Cr4 |= CpuState.Cr4OsFxsr;
        if (osXmmExcpt) cpu.State.Cr4 |= CpuState.Cr4Osxmmexcpt;
        return (cpu, bus);
    }

    /// <summary>The vector of the first exception the step raises, or 0xFF for none.</summary>
    private static byte FaultVector(Cpu cpu, Action step)
    {
        byte vector = 0xFF;
        cpu.ProcessorException += (v, _, _, _) => { if (vector == 0xFF) vector = v; };
        step();
        return vector;
    }

    /// <summary>A 0x67-prefixed memory instruction addressing a direct 32-bit displacement (mod=00, rm=101), the reg field free to name a destination register or a group-15 sub-opcode.</summary>
    private static byte[] Mem32Instruction(byte[] opcodeBytes, int reg, uint address) =>
    [
        0x67, .. opcodeBytes, (byte)(0x05 | ((reg & 7) << 3)),
        (byte)address, (byte)(address >> 8), (byte)(address >> 16), (byte)(address >> 24),
    ];

    private static byte ModRegRm(int reg, int rm) => (byte)(0xC0 | ((reg & 7) << 3) | (rm & 7));

    // === 1: CPUID-gated #UD =====================================================

    [Fact]
    public void SseInstructionFaultsWithUdWithoutCpuidSse()
    {
        // ADDPS XMM0, XMM1 on a Pentium II, which has no CPUID.SSE.
        var (cpu, bus) = Setup(0x0F, 0x58, ModRegRm(0, 1));
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.PentiumII);
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    // === 2: EM -> #UD ============================================================

    [Fact]
    public void SseInstructionFaultsWithUdWhenEmIsSet()
    {
        var (cpu, bus) = SetupSse(code: [0x0F, 0x10, ModRegRm(0, 1)]);   // MOVUPS XMM0, XMM1
        using (bus)
        {
            cpu.State.Cr0 |= CpuState.Cr0Emulation;
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    // === 3: TS -> #NM ============================================================

    [Fact]
    public void SseInstructionFaultsWithDeviceNotAvailableWhenTsIsSet()
    {
        var (cpu, bus) = SetupSse(code: [0x0F, 0x10, ModRegRm(0, 1)]);
        using (bus)
        {
            cpu.State.Cr0 |= CpuState.Cr0TaskSwitched;
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(7, vector);
        }
    }

    // === 4: OSFXSR = 0 -> #UD for XMM but not for PAVGB =========================

    [Fact]
    public void OsFxsrClearFaultsAnXmmInstructionWithUd()
    {
        var (cpu, bus) = SetupSse(osFxsr: false, code: [0x0F, 0x10, ModRegRm(0, 1)]);
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    [Fact]
    public void OsFxsrClearDoesNotFaultPavgb()
    {
        var (cpu, bus) = SetupSse(osFxsr: false, code: [0x0F, 0xE0, ModRegRm(0, 1)]);   // PAVGB MM0, MM1
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(0xFF, vector);
        }
    }

    // === 5: MOVAPS alignment vs MOVUPS ==========================================

    [Fact]
    public void MovapsFaultsOnAnUnalignedMemoryOperand()
    {
        const uint address = 0x4001;
        var (cpu, bus) = SetupSse(code: Mem32Instruction([0x0F, 0x28], 0, address));   // MOVAPS XMM0, [address]
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(13, vector);
        }
    }

    [Fact]
    public void MovupsDoesNotFaultOnAnUnalignedMemoryOperand()
    {
        const uint address = 0x4001;
        var (cpu, bus) = SetupSse(code: Mem32Instruction([0x0F, 0x10], 0, address));   // MOVUPS XMM0, [address]
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(0xFF, vector);
        }
    }

    // === 6: MASKMOVQ memory form -> #UD =========================================

    [Fact]
    public void MaskmovqFaultsWithUdOnAMemoryOperand()
    {
        var (cpu, bus) = SetupSse(code: Mem32Instruction([0x0F, 0xF7], 0, 0x4000));
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    // === 7: LDMXCSR DAZ with MXCSR_MASK FFBFh -> #GP ============================

    [Fact]
    public void LdmxcsrFaultsOnDazWithTheMxcsrMaskFfbf()
    {
        const uint address = 0x4000;
        var (cpu, bus) = SetupSse(code: Mem32Instruction([0x0F, 0xAE], 2, address));   // LDMXCSR [address]
        using (bus)
        {
            bus.WriteDword(address, SseUnit.MxcsrPowerOnDefault | MxcsrBits.Daz);
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(13, vector);
        }
    }

    // === 8: reserved bit 16 -> #GP ================================================

    [Fact]
    public void LdmxcsrFaultsOnAReservedBitAbove15()
    {
        const uint address = 0x4000;
        var (cpu, bus) = SetupSse(code: Mem32Instruction([0x0F, 0xAE], 2, address));
        using (bus)
        {
            bus.WriteDword(address, SseUnit.MxcsrPowerOnDefault | (1u << 16));
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(13, vector);
        }
    }

    // === 9: FXRSTOR with MXCSR FFFFFFFFh -> #GP, nothing loaded =================

    [Fact]
    public void FxrstorFaultsWithGpOnAReservedMxcsrBitAndLoadsNothing()
    {
        const uint area = 0x5000;   // 16-byte aligned
        var (cpu, bus) = SetupSse(code: Mem32Instruction([0x0F, 0xAE], 1, area));   // FXRSTOR [area]
        using (bus)
        {
            for (uint i = 0; i < 512; i += 4) bus.WriteDword(area + i, 0);
            bus.WriteDword(area + 24, 0xFFFFFFFF);   // MXCSR: every bit set.

            cpu.Sse.Xmm(0) = new XmmValue { D0 = 0x1234_5678, D1 = 1, D2 = 2, D3 = 3 };
            cpu.Sse.Mxcsr = 0x1F80;

            byte vector = FaultVector(cpu, () => cpu.Step());

            Assert.Equal(13, vector);
            Assert.Equal(0x1F80u, cpu.Sse.Mxcsr);
            Assert.Equal(new XmmValue { D0 = 0x1234_5678, D1 = 1, D2 = 2, D3 = 3 }, cpu.Sse.Xmm(0));
        }
    }

    // === 10 & 11: DIVSS by zero, masked and unmasked =============================

    [Fact]
    public void DivssByZeroMaskedProducesInfinityAndSetsTheZeFlag()
    {
        var (cpu, bus) = SetupSse(code: [0xF3, 0x0F, 0x5E, ModRegRm(0, 1)]);   // DIVSS XMM0, XMM1
        using (bus)
        {
            cpu.Sse.Xmm(0).D0 = SingleFloat.FromFloat(1.0f);
            cpu.Sse.Xmm(1).D0 = SingleFloat.PositiveZero;

            byte vector = FaultVector(cpu, () => cpu.Step());

            Assert.Equal(0xFF, vector);
            Assert.Equal(SingleFloat.PositiveInfinity, cpu.Sse.Xmm(0).D0);
            Assert.NotEqual(0u, cpu.Sse.Mxcsr & MxcsrBits.DivideByZeroFlag);
        }
    }

    [Fact]
    public void DivssByZeroUnmaskedWithOsXmmExcptRaisesXmAndLeavesTheDestinationUnchanged()
    {
        var (cpu, bus) = SetupSse(osXmmExcpt: true, code: [0xF3, 0x0F, 0x5E, ModRegRm(0, 1)]);
        using (bus)
        {
            cpu.Sse.Mxcsr &= ~MxcsrBits.DivideByZeroMask;
            uint original = SingleFloat.FromFloat(1.0f);
            cpu.Sse.Xmm(0).D0 = original;
            cpu.Sse.Xmm(1).D0 = SingleFloat.PositiveZero;

            byte vector = FaultVector(cpu, () => cpu.Step());

            Assert.Equal(19, vector);
            Assert.Equal(original, cpu.Sse.Xmm(0).D0);
            Assert.NotEqual(0u, cpu.Sse.Mxcsr & MxcsrBits.DivideByZeroFlag);
        }
    }

    [Fact]
    public void DivssByZeroUnmaskedWithoutOsXmmExcptFaultsWithUd()
    {
        var (cpu, bus) = SetupSse(code: [0xF3, 0x0F, 0x5E, ModRegRm(0, 1)]);   // osXmmExcpt defaults to false
        using (bus)
        {
            cpu.Sse.Mxcsr &= ~MxcsrBits.DivideByZeroMask;
            cpu.Sse.Xmm(0).D0 = SingleFloat.FromFloat(1.0f);
            cpu.Sse.Xmm(1).D0 = SingleFloat.PositiveZero;

            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    // === 12: CMPPS LT vs EQ on QNaN and IE ========================================

    private const uint QuietNaN = 0x7FC0_0000u;
    private const uint SignalingNaN = 0x7FA0_0000u;

    [Fact]
    public void CmppsLtSignalsInvalidOnAQuietNaNButEqDoesNot()
    {
        var (cpuLt, busLt) = SetupSse(code: [0x0F, 0xC2, ModRegRm(0, 1), 0x01]);   // CMPPS XMM0, XMM1, LT
        using (busLt)
        {
            cpuLt.Sse.Xmm(0).D0 = SingleFloat.FromFloat(1.0f);
            cpuLt.Sse.Xmm(1).D0 = QuietNaN;
            cpuLt.Step();
            Assert.NotEqual(0u, cpuLt.Sse.Mxcsr & MxcsrBits.InvalidFlag);
        }

        var (cpuEq, busEq) = SetupSse(code: [0x0F, 0xC2, ModRegRm(0, 1), 0x00]);   // CMPPS XMM0, XMM1, EQ
        using (busEq)
        {
            cpuEq.Sse.Xmm(0).D0 = SingleFloat.FromFloat(1.0f);
            cpuEq.Sse.Xmm(1).D0 = QuietNaN;
            cpuEq.Step();
            Assert.Equal(0u, cpuEq.Sse.Mxcsr & MxcsrBits.InvalidFlag);
            Assert.Equal(0u, cpuEq.Sse.Xmm(0).D0);   // unordered: EQ is false.
        }
    }

    // === 13: COMISS/UCOMISS SNaN/QNaN and IE ======================================

    [Fact]
    public void ComissSignalsInvalidOnAQuietNaNButUcomissDoesNot()
    {
        var (cpuC, busC) = SetupSse(code: [0x0F, 0x2F, ModRegRm(0, 1)]);   // COMISS XMM0, XMM1
        using (busC)
        {
            cpuC.Sse.Xmm(0).D0 = SingleFloat.FromFloat(1.0f);
            cpuC.Sse.Xmm(1).D0 = QuietNaN;
            cpuC.Step();
            Assert.NotEqual(0u, cpuC.Sse.Mxcsr & MxcsrBits.InvalidFlag);
            Assert.True(cpuC.Flags.Zero && cpuC.Flags.Parity && cpuC.Flags.Carry);
        }

        var (cpuU, busU) = SetupSse(code: [0x0F, 0x2E, ModRegRm(0, 1)]);   // UCOMISS XMM0, XMM1
        using (busU)
        {
            cpuU.Sse.Xmm(0).D0 = SingleFloat.FromFloat(1.0f);
            cpuU.Sse.Xmm(1).D0 = QuietNaN;
            cpuU.Step();
            Assert.Equal(0u, cpuU.Sse.Mxcsr & MxcsrBits.InvalidFlag);
            Assert.True(cpuU.Flags.Zero && cpuU.Flags.Parity && cpuU.Flags.Carry);
        }
    }

    [Fact]
    public void UcomissSignalsInvalidOnASignalingNaNToo()
    {
        var (cpu, bus) = SetupSse(code: [0x0F, 0x2E, ModRegRm(0, 1)]);
        using (bus)
        {
            cpu.Sse.Xmm(0).D0 = SingleFloat.FromFloat(1.0f);
            cpu.Sse.Xmm(1).D0 = SignalingNaN;
            cpu.Step();
            Assert.NotEqual(0u, cpu.Sse.Mxcsr & MxcsrBits.InvalidFlag);
        }
    }

    // === 14: CVTTSS2SI overflow -> indefinite, unmasked -> #XM ====================

    [Fact]
    public void CvttssToSiOverflowProducesTheIntegerIndefiniteWhenMasked()
    {
        var (cpu, bus) = SetupSse(code: [0xF3, 0x0F, 0x2C, ModRegRm(0, 1)]);   // CVTTSS2SI EAX, XMM1
        using (bus)
        {
            cpu.Sse.Xmm(1).D0 = SingleFloat.FromFloat(1e30f);
            cpu.State.Eax = 0x1111_1111;
            cpu.Step();
            Assert.Equal(0x8000_0000u, cpu.State.Eax);
            Assert.NotEqual(0u, cpu.Sse.Mxcsr & MxcsrBits.InvalidFlag);
        }
    }

    [Fact]
    public void CvttssToSiOverflowUnmaskedRaisesXmAndLeavesTheDestinationUnchanged()
    {
        var (cpu, bus) = SetupSse(osXmmExcpt: true, code: [0xF3, 0x0F, 0x2C, ModRegRm(0, 1)]);
        using (bus)
        {
            cpu.Sse.Mxcsr &= ~MxcsrBits.InvalidMask;
            cpu.Sse.Xmm(1).D0 = SingleFloat.FromFloat(1e30f);
            cpu.State.Eax = 0x2222_2222;

            byte vector = FaultVector(cpu, () => cpu.Step());

            Assert.Equal(19, vector);
            Assert.Equal(0x2222_2222u, cpu.State.Eax);
        }
    }

    // === 15: MAXPS +-0 order =======================================================

    [Fact]
    public void MaxpsPicksTheSourceWhenOperandsAreEqualSignedZeroIncluded()
    {
        var (cpu, bus) = SetupSse(code: [0x0F, 0x5F, ModRegRm(0, 1)]);   // MAXPS XMM0, XMM1
        using (bus)
        {
            cpu.Sse.Xmm(0).D0 = SingleFloat.PositiveZero;
            cpu.Sse.Xmm(1).D0 = SingleFloat.NegativeZero;
            cpu.Step();
            Assert.Equal(SingleFloat.NegativeZero, cpu.Sse.Xmm(0).D0);
        }
    }

    // === 16: MAXPS NaN order =======================================================

    [Fact]
    public void MaxpsPicksTheSourceWhenEitherOperandIsNaN()
    {
        var (cpuDestNaN, busDestNaN) = SetupSse(code: [0x0F, 0x5F, ModRegRm(0, 1)]);
        using (busDestNaN)
        {
            cpuDestNaN.Sse.Xmm(0).D0 = QuietNaN;
            cpuDestNaN.Sse.Xmm(1).D0 = SingleFloat.FromFloat(2.0f);
            cpuDestNaN.Step();
            Assert.Equal(SingleFloat.FromFloat(2.0f), cpuDestNaN.Sse.Xmm(0).D0);
        }

        var (cpuSrcNaN, busSrcNaN) = SetupSse(code: [0x0F, 0x5F, ModRegRm(0, 1)]);
        using (busSrcNaN)
        {
            cpuSrcNaN.Sse.Xmm(0).D0 = SingleFloat.FromFloat(2.0f);
            cpuSrcNaN.Sse.Xmm(1).D0 = QuietNaN;
            cpuSrcNaN.Step();
            Assert.Equal(QuietNaN, cpuSrcNaN.Sse.Xmm(0).D0);
        }
    }

    // === 17: RCPPS/RSQRTPS error bound and special values =========================

    [Fact]
    public void RcppsStaysWithinTheDocumentedErrorBound()
    {
        var rng = new Random(12345);
        for (int i = 0; i < 5000; i++)
        {
            float value = (float)(rng.NextDouble() * 1000.0 + 1e-6);
            uint approxBits = SingleFloat.ReciprocalApprox(SingleFloat.FromFloat(value)).Result;
            double approx = SingleFloat.ToFloat(approxBits);
            double exact = 1.0 / value;
            double relativeError = Math.Abs((exact - approx) / exact);
            Assert.True(relativeError <= 1.5 * Math.Pow(2, -12), $"value={value} relativeError={relativeError}");
        }
    }

    [Fact]
    public void RsqrtpsStaysWithinTheDocumentedErrorBound()
    {
        var rng = new Random(54321);
        for (int i = 0; i < 5000; i++)
        {
            float value = (float)(rng.NextDouble() * 1000.0 + 1e-6);
            uint approxBits = SingleFloat.ReciprocalSqrtApprox(SingleFloat.FromFloat(value)).Result;
            double approx = SingleFloat.ToFloat(approxBits);
            double exact = 1.0 / Math.Sqrt(value);
            double relativeError = Math.Abs((exact - approx) / exact);
            Assert.True(relativeError <= 1.5 * Math.Pow(2, -12), $"value={value} relativeError={relativeError}");
        }
    }

    [Fact]
    public void RcpAndRsqrtApproximationsMatchTheirDocumentedSpecialValues()
    {
        Assert.Equal(SingleFloat.PositiveInfinity, SingleFloat.ReciprocalApprox(SingleFloat.PositiveZero).Result);
        Assert.Equal(SingleFloat.NegativeInfinity, SingleFloat.ReciprocalApprox(SingleFloat.NegativeZero).Result);
        Assert.Equal(SingleFloat.PositiveZero, SingleFloat.ReciprocalApprox(SingleFloat.PositiveInfinity).Result);
        Assert.Equal(SingleFloat.NegativeZero, SingleFloat.ReciprocalApprox(SingleFloat.NegativeInfinity).Result);

        Assert.Equal(SingleFloat.PositiveInfinity, SingleFloat.ReciprocalSqrtApprox(SingleFloat.PositiveZero).Result);
        Assert.Equal(SingleFloat.NegativeInfinity, SingleFloat.ReciprocalSqrtApprox(SingleFloat.NegativeZero).Result);
        Assert.Equal(SingleFloat.DefaultQNaN, SingleFloat.ReciprocalSqrtApprox(SingleFloat.FromFloat(-4.0f)).Result);
    }

    // === 18: FXSAVE carries XMM/MXCSR/MXCSR_MASK whatever OSFXSR says ============

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FxsaveCarriesXmmAndMxcsrRegardlessOfOsFxsr(bool osFxsr)
    {
        const uint area = 0x6000;   // 16-byte aligned
        var (cpu, bus) = SetupSse(osFxsr: osFxsr, code: Mem32Instruction([0x0F, 0xAE], 0, area));   // FXSAVE [area]
        using (bus)
        {
            cpu.Sse.Xmm(3) = new XmmValue { D0 = 0xAAAA_AAAA, D1 = 0xBBBB_BBBB, D2 = 0xCCCC_CCCC, D3 = 0xDDDD_DDDD };
            cpu.Sse.Mxcsr = 0x1FA8;

            cpu.Step();

            Assert.Equal(0x1FA8u, bus.ReadDword(area + 24));
            Assert.Equal(SseUnit.MxcsrMask, bus.ReadDword(area + 28));
            Assert.Equal(0xAAAA_AAAAu, bus.ReadDword(area + 160 + 3 * 16));
            Assert.Equal(0xBBBB_BBBBu, bus.ReadDword(area + 160 + 3 * 16 + 4));
            Assert.Equal(0xCCCC_CCCCu, bus.ReadDword(area + 160 + 3 * 16 + 8));
            Assert.Equal(0xDDDD_DDDDu, bus.ReadDword(area + 160 + 3 * 16 + 12));
        }
    }

    // === 19: PSHUFW on Pentium II -> #UD ===========================================

    [Fact]
    public void PshufwOnAPentiumIiFaultsWithUd()
    {
        var (cpu, bus) = Setup(0x0F, 0x70, ModRegRm(0, 1), 0x00);   // PSHUFW MM0, MM1, 0
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.PentiumII);
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    // === 20: PREFETCH register form ================================================

    [Fact]
    public void PrefetchRegisterFormFaultsWithUd()
    {
        var (cpu, bus) = SetupSse(code: [0x0F, 0x18, ModRegRm(0, 0)]);   // PREFETCH0 EAX (mod=11)
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    // === FXSAVE/FXRSTOR round trip of XMM/MXCSR ===================================

    [Fact]
    public void FxsaveThenFxrstorRoundTripsXmmAndMxcsr()
    {
        const uint area = 0x7000;
        byte[] code = [.. Mem32Instruction([0x0F, 0xAE], 0, area), .. Mem32Instruction([0x0F, 0xAE], 1, area)];
        var (cpu, bus) = SetupSse(code: code);
        using (bus)
        {
            for (int i = 0; i < 8; i++)
            {
                cpu.Sse.Xmm(i) = new XmmValue
                {
                    D0 = (uint)(i * 4), D1 = (uint)(i * 4 + 1), D2 = (uint)(i * 4 + 2), D3 = (uint)(i * 4 + 3),
                };
            }
            cpu.Sse.Mxcsr = 0x1FA8;

            cpu.Step();   // FXSAVE

            var before = new XmmValue[8];
            for (int i = 0; i < 8; i++) before[i] = cpu.Sse.Xmm(i);
            uint mxcsrBefore = cpu.Sse.Mxcsr;

            for (int i = 0; i < 8; i++) cpu.Sse.Xmm(i) = default;
            cpu.Sse.Mxcsr = SseUnit.MxcsrPowerOnDefault;

            cpu.Step();   // FXRSTOR

            for (int i = 0; i < 8; i++) Assert.Equal(before[i], cpu.Sse.Xmm(i));
            Assert.Equal(mxcsrBefore, cpu.Sse.Mxcsr);
        }
    }
}
