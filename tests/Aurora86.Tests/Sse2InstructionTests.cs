using Aurora86.Core.Cpu;
using Aurora86.Core.Cpu.Sse;
using Aurora86.Core.Memory;

namespace Aurora86.Tests;

/// <summary>
/// Work package P4.2: SSE2 (plans/specs/sse2.md). The note's fifteen
/// register-level test cases, in the same numbered order, plus the items
/// the outcome names beyond them: #XM delivery for a double operation, DAZ
/// on single and double precision alike, MXCSR_MASK per model in FXSAVE,
/// and a Pentium III still refusing every SSE2 encoding. Each test drives
/// the real interpreter -- decode, dispatch, fault delivery -- the same way
/// SseInstructionTests.cs does for the Pentium III's own SSE.
/// </summary>
public class Sse2InstructionTests
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

    /// <summary>A Pentium 4 (Northwood) with CR4.OSFXSR set unless <paramref name="osFxsr"/> says otherwise, and CR4.OSXMMEXCPT set only when <paramref name="osXmmExcpt"/> asks for it.</summary>
    private static (Cpu cpu, PhysicalMemoryBus bus) SetupSse2(bool osFxsr = true, bool osXmmExcpt = false, CpuModel model = CpuModel.Pentium4, params byte[] code)
    {
        var (cpu, bus) = Setup(code);
        cpu.State.SelectModel(model);
        if (osFxsr) cpu.State.Cr4 |= CpuState.Cr4OsFxsr;
        if (osXmmExcpt) cpu.State.Cr4 |= CpuState.Cr4Osxmmexcpt;
        return (cpu, bus);
    }

    private static byte FaultVector(Cpu cpu, Action step)
    {
        byte vector = 0xFF;
        cpu.ProcessorException += (v, _, _, _) => { if (vector == 0xFF) vector = v; };
        step();
        return vector;
    }

    /// <summary>A 0x67-prefixed memory instruction addressing a direct 32-bit displacement (mod=00, rm=101), the reg field free to name a destination register or a group-15 sub-opcode. <paramref name="prefix"/> is the mandatory 66/F2/F3 byte, or 0 for none.</summary>
    private static byte[] Mem32Instruction(byte prefix, byte[] opcodeBytes, int reg, uint address)
    {
        List<byte> code = [];
        if (prefix != 0) code.Add(prefix);
        code.Add(0x67);
        code.AddRange(opcodeBytes);
        code.Add((byte)(0x05 | ((reg & 7) << 3)));
        code.AddRange(BitConverter.GetBytes(address));
        return code.ToArray();
    }

    private static byte ModRegRm(int reg, int rm) => (byte)(0xC0 | ((reg & 7) << 3) | (rm & 7));

    // === 1: no SSE2 -> ADDPD #UD =================================================

    [Fact]
    public void AddpdFaultsWithUdWithoutCpuidSse2()
    {
        // ADDPD XMM0, XMM1 on a Pentium III, which has SSE but not SSE2.
        var (cpu, bus) = Setup(0x66, 0x0F, 0x58, ModRegRm(0, 1));
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.PentiumIII);
            cpu.State.Cr4 |= CpuState.Cr4OsFxsr;
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    // === 2: EM -> MOVAPD #UD before alignment ====================================

    [Fact]
    public void MovapdFaultsWithUdOnEmBeforeCheckingAlignment()
    {
        const uint misaligned = 0x4001;   // not 16-byte aligned: would otherwise be #GP(0).
        var (cpu, bus) = SetupSse2(code: Mem32Instruction(0x66, [0x0F, 0x28], 0, misaligned));
        using (bus)
        {
            cpu.State.Cr0 |= CpuState.Cr0Emulation;
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    // === 3: TS -> MOVUPD #NM, LFENCE runs =========================================

    [Fact]
    public void MovupdFaultsWithDeviceNotAvailableWhenTsIsSet()
    {
        var (cpu, bus) = SetupSse2(code: [0x66, 0x0F, 0x10, ModRegRm(0, 1)]);   // MOVUPD XMM0, XMM1
        using (bus)
        {
            cpu.State.Cr0 |= CpuState.Cr0TaskSwitched;
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(7, vector);
        }
    }

    [Fact]
    public void LfenceStillRunsWithTsSet()
    {
        var (cpu, bus) = SetupSse2(code: [0x0F, 0xAE, ModRegRm(5, 0)]);   // LFENCE
        using (bus)
        {
            cpu.State.Cr0 |= CpuState.Cr0TaskSwitched;
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(0xFF, vector);
        }
    }

    // === 4: OSFXSR = 0 -> 66 PADDQ #UD, unprefixed MMX PADDQ runs =================

    [Fact]
    public void OsFxsrClearFaultsTheWidenedPaddqWithUd()
    {
        var (cpu, bus) = SetupSse2(osFxsr: false, code: [0x66, 0x0F, 0xD4, ModRegRm(0, 1)]);   // PADDQ XMM0, XMM1
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    [Fact]
    public void OsFxsrClearDoesNotFaultTheMmxFormOfPaddq()
    {
        var (cpu, bus) = SetupSse2(osFxsr: false, code: [0x0F, 0xD4, ModRegRm(0, 1)]);   // PADDQ MM0, MM1
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(0xFF, vector);
        }
    }

    // === 5: MOVAPD misaligned #GP, MOVUPD fine ===================================

    [Fact]
    public void MovapdFaultsWithGpOnAMisalignedAddress()
    {
        const uint misaligned = 0x4001;
        var (cpu, bus) = SetupSse2(code: Mem32Instruction(0x66, [0x0F, 0x28], 0, misaligned));
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(13, vector);
        }
    }

    [Fact]
    public void MovupdToleratesAMisalignedAddress()
    {
        const uint misaligned = 0x4001;
        var (cpu, bus) = SetupSse2(code: Mem32Instruction(0x66, [0x0F, 0x10], 0, misaligned));
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(0xFF, vector);
        }
    }

    // === 6: MASKMOVDQU memory form #UD ============================================

    [Fact]
    public void MaskmovdquFaultsWithUdOnAMemoryOperand()
    {
        var (cpu, bus) = SetupSse2(code: Mem32Instruction(0x66, [0x0F, 0xF7], 0, 0x4000));
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    // === 7: CLFLUSH without CLFSH #UD =============================================

    [Fact]
    public void ClflushFaultsWithUdWithoutCpuidClfsh()
    {
        const uint address = 0x4000;
        var (cpu, bus) = Setup(Mem32Instruction(0, [0x0F, 0xAE], 7, address));   // CLFLUSH [address]
        using (bus)
        {
            // PentiumMmx has CPUID.SSE2 through neither this feature nor
            // CLFSH -- select a custom feature set with SSE2 but not CLFSH,
            // so this is really testing CLFSH's own independent gate, not
            // just "SSE2 missing" a second time.
            cpu.State.SelectModel(CpuModel.Pentium4);
            cpu.State.Features = cpu.State.Features.Without(CpuFeature.Clfsh);
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    [Fact]
    public void ClflushRunsWithCpuidClfsh()
    {
        const uint address = 0x4000;
        var (cpu, bus) = SetupSse2(code: Mem32Instruction(0, [0x0F, 0xAE], 7, address));
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(0xFF, vector);
        }
    }

    // === 8: LFENCE/MFENCE with ModR/M C1h (mod=11, rm=001) ========================

    [Fact]
    public void LfenceAcceptsAnyRmBitsWithMod11()
    {
        var (cpu, bus) = SetupSse2(code: [0x0F, 0xAE, 0xE9]);   // mod=11, reg=5 (LFENCE), rm=001.
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(0xFF, vector);
        }
    }

    [Fact]
    public void MfenceAcceptsAnyRmBitsWithMod11()
    {
        var (cpu, bus) = SetupSse2(code: [0x0F, 0xAE, 0xF1]);   // mod=11, reg=6 (MFENCE), rm=001.
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(0xFF, vector);
        }
    }

    // === 9: a 66-prefixed MMX opcode on a Pentium III -> #UD ======================

    [Fact]
    public void APrefixedMmxOpcodeFaultsWithUdOnAPentiumIii()
    {
        var (cpu, bus) = Setup(0x66, 0x0F, 0xFC, ModRegRm(0, 1));   // 66 PADDB, i.e. the widened form.
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.PentiumIII);
            cpu.State.Cr4 |= CpuState.Cr4OsFxsr;
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    [Fact]
    public void EveryPrefixOfTheSameMmxOpcodeFaultsWithUdOnAPentiumIii()
    {
        foreach (byte prefix in new byte[] { 0x66, 0xF2, 0xF3 })
        {
            var (cpu, bus) = Setup(prefix, 0x0F, 0xFC, ModRegRm(0, 1));
            using (bus)
            {
                cpu.State.SelectModel(CpuModel.PentiumIII);
                cpu.State.Cr4 |= CpuState.Cr4OsFxsr;
                byte vector = FaultVector(cpu, () => cpu.Step());
                Assert.Equal(6, vector);
            }
        }
    }

    // === 10: LDMXCSR with DAZ on Northwood succeeds ================================

    [Fact]
    public void LdmxcsrAcceptsDazOnThePentium4()
    {
        const uint address = 0x4000;
        var (cpu, bus) = SetupSse2(code: Mem32Instruction(0, [0x0F, 0xAE], 2, address));   // LDMXCSR [address]
        using (bus)
        {
            bus.WriteDword(address, SseUnit.MxcsrPowerOnDefault | MxcsrBits.Daz);
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(0xFF, vector);
            Assert.Equal(SseUnit.MxcsrPowerOnDefault | MxcsrBits.Daz, cpu.Sse.Mxcsr);
        }
    }

    [Fact]
    public void LdmxcsrStillRejectsDazOnThePentiumIii()
    {
        const uint address = 0x4000;
        var (cpu, bus) = SetupSse2(model: CpuModel.PentiumIII, code: Mem32Instruction(0, [0x0F, 0xAE], 2, address));
        using (bus)
        {
            bus.WriteDword(address, SseUnit.MxcsrPowerOnDefault | MxcsrBits.Daz);
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(13, vector);
        }
    }

    // === 11: CVTSI2SD exact in all RC ==============================================

    [Theory]
    [InlineData(0u)] [InlineData(1u)] [InlineData(2u)] [InlineData(3u)]
    public void Cvtsi2sdIsExactInEveryRoundingMode(uint rc)
    {
        var (cpu, bus) = SetupSse2(code: [0xF2, 0x0F, 0x2A, ModRegRm(0, 1)]);   // CVTSI2SD XMM0, ECX
        using (bus)
        {
            cpu.Sse.Mxcsr = (cpu.Sse.Mxcsr & ~MxcsrBits.RoundingControlMask) | (rc << MxcsrBits.RoundingControlShift);
            cpu.State.Ecx = unchecked((uint)(-123_456_789));

            cpu.Step();

            ulong expected = DoubleFloat.FromDouble(-123_456_789.0);
            ulong actual = cpu.Sse.Xmm(0).Low;
            Assert.Equal(expected, actual);
        }
    }

    // === 12: CVTPS2DQ just past 2^31-1 -> 80000000h + IE ===========================

    [Fact]
    public void Cvtps2dqOfJustPast2Pow31Minus1IsIntegerIndefiniteWithInvalidMasked()
    {
        var (cpu, bus) = SetupSse2(code: [0x66, 0x0F, 0x5B, ModRegRm(0, 1)]);   // CVTPS2DQ XMM0, XMM1
        using (bus)
        {
            // 2^31 exactly, the first single that rounds outside int32 range.
            cpu.Sse.Xmm(1).D0 = SingleFloat.FromFloat(2147483648.0f);

            cpu.Step();

            Assert.Equal(unchecked((uint)0x8000_0000), cpu.Sse.Xmm(0).D0);
            Assert.Equal(MxcsrBits.InvalidFlag, cpu.Sse.Mxcsr & MxcsrBits.AllFlags);
        }
    }

    [Fact]
    public void Cvtps2dqOfJustPast2Pow31Minus1SignalsXmWithInvalidAloneWhenUnmasked()
    {
        var (cpu, bus) = SetupSse2(osXmmExcpt: true, code: [0x66, 0x0F, 0x5B, ModRegRm(0, 1)]);
        using (bus)
        {
            cpu.Sse.Xmm(1).D0 = SingleFloat.FromFloat(2147483648.0f);
            cpu.Sse.Mxcsr &= ~MxcsrBits.InvalidMask;   // unmask IE.

            byte vector = FaultVector(cpu, () => cpu.Step());

            Assert.Equal(19, vector);
            Assert.Equal(MxcsrBits.InvalidFlag, cpu.Sse.Mxcsr & MxcsrBits.AllFlags);
        }
    }

    // === 13: CVTPS2PD of a denormal: DE, measured on the host ======================

    [Fact]
    public void Cvtps2pdOfADenormalSourceSetsDenormal()
    {
        var (cpu, bus) = SetupSse2(code: [0x0F, 0x5A, ModRegRm(0, 1)]);   // CVTPS2PD XMM0, XMM1
        using (bus)
        {
            cpu.Sse.Xmm(1) = new XmmValue { D0 = 0x0000_0001u, D1 = 0 };   // smallest positive single denormal, 2^-149.

            cpu.Step();

            Assert.Equal(MxcsrBits.DenormalFlag, cpu.Sse.Mxcsr & MxcsrBits.AllFlags);
            Assert.Equal(DoubleFloat.FromDouble(Math.ScaleB(1.0, -149)), cpu.Sse.Xmm(0).Low);   // the conversion itself is exact.
        }
    }

    // === 14: PADDQ/PMULUDQ XMM = two MMX lanes =====================================

    [Fact]
    public void PaddqXmmIsTwoIndependentMmxLanes()
    {
        var (cpu, bus) = SetupSse2(code: [0x66, 0x0F, 0xD4, ModRegRm(0, 1)]);   // PADDQ XMM0, XMM1
        using (bus)
        {
            cpu.Sse.Xmm(0) = XmmValue.FromHalves(0x1111_1111_2222_2222ul, 0x3333_3333_4444_4444ul);
            cpu.Sse.Xmm(1) = XmmValue.FromHalves(0x0000_0000_0000_0001ul, 0xFFFF_FFFF_FFFF_FFFFul);

            cpu.Step();

            XmmValue result = cpu.Sse.Xmm(0);
            Assert.Equal(0x1111_1111_2222_2223ul, result.Low);     // low lane: ordinary 64-bit add.
            Assert.Equal(0x3333_3333_4444_4443ul, result.High);    // high lane: wraps independently.
        }
    }

    [Fact]
    public void PmuludqXmmIsTwoIndependentMmxLanes()
    {
        var (cpu, bus) = SetupSse2(code: [0x66, 0x0F, 0xF4, ModRegRm(0, 1)]);   // PMULUDQ XMM0, XMM1
        using (bus)
        {
            cpu.Sse.Xmm(0) = new XmmValue { D0 = 3, D1 = 0xFFFF_FFFF, D2 = 5, D3 = 0xFFFF_FFFF };
            cpu.Sse.Xmm(1) = new XmmValue { D0 = 4, D1 = 0xFFFF_FFFF, D2 = 6, D3 = 0xFFFF_FFFF };

            cpu.Step();

            XmmValue result = cpu.Sse.Xmm(0);
            Assert.Equal(12ul, result.Low);     // 3 * 4 = 12, from the low dwords only -- the upper dwords are ignored.
            Assert.Equal(30ul, result.High);    // 5 * 6 = 30.
        }
    }

    // === 15: MAXSD/MINSD NaN order, measured on the host ===========================

    [Fact]
    public void MaxsdReturnsTheSourceWheneverEitherOperandIsNaN()
    {
        var (cpu, bus) = SetupSse2(code: [0xF2, 0x0F, 0x5F, ModRegRm(0, 1)]);   // MAXSD XMM0, XMM1
        using (bus)
        {
            cpu.Sse.Xmm(0).D0 = 0; cpu.Sse.Xmm(0).D1 = 0x3FF0_0000;   // dest = 1.0
            cpu.Sse.Xmm(1) = new XmmValue { D0 = 3, D1 = 0x7FF8_0000 };   // src = QNaN(payload 3)

            cpu.Step();

            Assert.Equal(0x7FF8_0000_0000_0003ul, cpu.Sse.Xmm(0).Low);
            Assert.Equal(MxcsrBits.InvalidFlag, cpu.Sse.Mxcsr & MxcsrBits.AllFlags);
        }
    }

    [Fact]
    public void MinsdReturnsTheSourceOnEqualOperandsSignedZeroIncluded()
    {
        var (cpu, bus) = SetupSse2(code: [0xF2, 0x0F, 0x5D, ModRegRm(0, 1)]);   // MINSD XMM0, XMM1
        using (bus)
        {
            cpu.Sse.Xmm(0) = new XmmValue { D0 = 0, D1 = 0 };            // dest = +0.0
            cpu.Sse.Xmm(1) = new XmmValue { D0 = 0, D1 = 0x8000_0000 };  // src = -0.0

            cpu.Step();

            Assert.Equal(DoubleFloat.NegativeZero, cpu.Sse.Xmm(0).Low);
        }
    }

    // === #XM delivery for a double operation ========================================

    [Fact]
    public void AddsdSignalsXmWithOverflowWhenUnmasked()
    {
        var (cpu, bus) = SetupSse2(osXmmExcpt: true, code: [0xF2, 0x0F, 0x58, ModRegRm(0, 1)]);   // ADDSD XMM0, XMM1
        using (bus)
        {
            cpu.Sse.Xmm(0).D0 = 0xFFFF_FFFF; cpu.Sse.Xmm(0).D1 = 0x7FEF_FFFF;   // DBL_MAX
            cpu.Sse.Xmm(1).D0 = 0xFFFF_FFFF; cpu.Sse.Xmm(1).D1 = 0x7FEF_FFFF;   // DBL_MAX
            cpu.Sse.Mxcsr &= ~MxcsrBits.OverflowMask;

            byte vector = FaultVector(cpu, () => cpu.Step());

            Assert.Equal(19, vector);
            // The destination is left untouched by an unmasked exception.
            Assert.Equal(0x7FEF_FFFF_FFFF_FFFFul, cpu.Sse.Xmm(0).Low);
        }
    }

    [Fact]
    public void AddsdSignalsUdInsteadOfXmWithoutOsXmmExcpt()
    {
        var (cpu, bus) = SetupSse2(osXmmExcpt: false, code: [0xF2, 0x0F, 0x58, ModRegRm(0, 1)]);
        using (bus)
        {
            cpu.Sse.Xmm(0).D0 = 0xFFFF_FFFF; cpu.Sse.Xmm(0).D1 = 0x7FEF_FFFF;
            cpu.Sse.Xmm(1).D0 = 0xFFFF_FFFF; cpu.Sse.Xmm(1).D1 = 0x7FEF_FFFF;
            cpu.Sse.Mxcsr &= ~MxcsrBits.OverflowMask;

            byte vector = FaultVector(cpu, () => cpu.Step());

            Assert.Equal(6, vector);
        }
    }

    // === DAZ on single and double =====================================================

    [Fact]
    public void DazTreatsADenormalSingleOperandAsZero()
    {
        var (cpu, bus) = SetupSse2(code: [0x0F, 0x58, ModRegRm(0, 1)]);   // ADDSS XMM0, XMM1
        using (bus)
        {
            cpu.Sse.Mxcsr |= MxcsrBits.Daz;
            cpu.Sse.Xmm(0).D0 = SingleFloat.FromFloat(1.0f);
            cpu.Sse.Xmm(1).D0 = 0x0000_0001u;   // smallest positive single denormal.

            cpu.Step();

            Assert.Equal(SingleFloat.FromFloat(1.0f), cpu.Sse.Xmm(0).D0);   // 1.0 + 0 (DAZ'd) = 1.0 exactly.
            Assert.Equal(0u, cpu.Sse.Mxcsr & MxcsrBits.AllFlags);           // no DE: DAZ replaced it with zero before classification.
        }
    }

    [Fact]
    public void DazTreatsADenormalDoubleOperandAsZero()
    {
        var (cpu, bus) = SetupSse2(code: [0xF2, 0x0F, 0x58, ModRegRm(0, 1)]);   // ADDSD XMM0, XMM1
        using (bus)
        {
            cpu.Sse.Mxcsr |= MxcsrBits.Daz;
            cpu.Sse.Xmm(0) = new XmmValue { D0 = 0, D1 = 0x3FF0_0000 };   // 1.0
            cpu.Sse.Xmm(1) = new XmmValue { D0 = 1, D1 = 0 };             // smallest positive double denormal.

            cpu.Step();

            Assert.Equal(0x3FF0_0000_0000_0000ul, cpu.Sse.Xmm(0).Low);   // 1.0 + 0 (DAZ'd) = 1.0 exactly.
            Assert.Equal(0u, cpu.Sse.Mxcsr & MxcsrBits.AllFlags);
        }
    }

    // === MXCSR_MASK per model in FXSAVE ================================================

    [Theory]
    [InlineData(CpuModel.PentiumIII, 0x0000_FFBFu)]
    [InlineData(CpuModel.Pentium4, 0x0000_FFFFu)]
    public void FxsaveReportsTheModelsOwnMxcsrMask(CpuModel model, uint expectedMask)
    {
        const uint area = 0x6000;
        var (cpu, bus) = SetupSse2(model: model, code: Mem32Instruction(0, [0x0F, 0xAE], 0, area));   // FXSAVE [area]
        using (bus)
        {
            cpu.Step();
            Assert.Equal(expectedMask, bus.ReadDword(area + 28));
        }
    }

    [Fact]
    public void FxrstorAcceptsDazOnlyWithinThePentium4sOwnMask()
    {
        const uint area = 0x6000;
        var (cpu, bus) = SetupSse2(code: Mem32Instruction(0, [0x0F, 0xAE], 1, area));   // FXRSTOR [area]
        using (bus)
        {
            for (int i = 0; i < 512; i += 4) bus.WriteDword(area + (uint)i, 0);
            bus.WriteDword(area + 24, SseUnit.MxcsrPowerOnDefault | MxcsrBits.Daz);   // MXCSR with DAZ set.

            byte vector = FaultVector(cpu, () => cpu.Step());

            Assert.Equal(0xFF, vector);
            Assert.Equal(SseUnit.MxcsrPowerOnDefault | MxcsrBits.Daz, cpu.Sse.Mxcsr);
        }
    }

    // === a Pentium III still refuses every SSE2 encoding ===============================

    [Theory]
    [InlineData(0x00)]   // CVTPS2PD / CVTDQ2PS (no prefix, still SSE2-only)
    [InlineData(0x66)]   // packed double / widened integer
    [InlineData(0xF2)]   // scalar double
    public void APentiumIiiFaultsOnOpcode5AWithUdWhateverThePrefix(byte prefix)
    {
        List<byte> code = [];
        if (prefix != 0) code.Add(prefix);
        code.Add(0x0F);
        code.Add(0x5A);
        code.Add(ModRegRm(0, 1));

        var (cpu, bus) = Setup(code.ToArray());
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.PentiumIII);
            cpu.State.Cr4 |= CpuState.Cr4OsFxsr;
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    [Fact]
    public void APentiumIiiFaultsOnMovntiWithUd()
    {
        var (cpu, bus) = Setup(Mem32Instruction(0, [0x0F, 0xC3], 0, 0x4000));   // MOVNTI [addr], EAX
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.PentiumIII);
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    // === review finding 1: MOVNTI's memory form must read the reg field, not m.Register ===

    [Fact]
    public void MovntiStoresTheRegFieldsRegisterNotTheRmField()
    {
        // MOVNTI [address], EDX: reg=2 (EDX), rm=101 (disp32, mod=00) -- a
        // non-EAX source, so a bug that reads m.Register (0, unset for a
        // memory r/m) instead of m.Reg (2) cannot pass by accident.
        const uint address = 0x4000;
        var (cpu, bus) = Setup(Mem32Instruction(0, [0x0F, 0xC3], 2, address));
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.Pentium4);
            cpu.State.Edx = 0x1234_5678;
            cpu.State.Eax = 0xAAAA_AAAA;   // a different value: EAX must not be what gets stored.

            cpu.Step();

            Assert.Equal(0x1234_5678u, bus.ReadDword(address));
        }
    }

    // === review finding 2: tininess decided on the natural (unbounded-exponent), not the
    // === denormal-grid, rounding -- confirmed against the host (tools/NativeOracle --probe) ===

    private const ulong JustBelowOneUlpFromDblMin = 0x3FEF_FFFF_FFFF_FFFFul;   // just below 1.0, by exactly the smallest normal's own ULP.
    private const ulong DblMin = 0x0010_0000_0000_0000ul;
    private const ulong LargestDenormal = 0x000F_FFFF_FFFF_FFFFul;

    [Fact]
    public void MulsdTieToNormalSetsUnderflowAndPrecisionInRoundToNearest()
    {
        var (cpu, bus) = SetupSse2(code: [0xF2, 0x0F, 0x59, ModRegRm(0, 1)]);   // MULSD XMM0, XMM1
        using (bus)
        {
            cpu.Sse.Xmm(0) = new XmmValue { D0 = unchecked((uint)JustBelowOneUlpFromDblMin), D1 = unchecked((uint)(JustBelowOneUlpFromDblMin >> 32)) };
            cpu.Sse.Xmm(1) = new XmmValue { D0 = unchecked((uint)DblMin), D1 = unchecked((uint)(DblMin >> 32)) };

            cpu.Step();

            Assert.Equal(DblMin, cpu.Sse.Xmm(0).Low);
            Assert.Equal((uint)(MxcsrBits.UnderflowFlag | MxcsrBits.PrecisionFlag), cpu.Sse.Mxcsr & MxcsrBits.AllFlags);
        }
    }

    [Theory]
    [InlineData(1u, LargestDenormal)]     // RC=Down (toward -infinity): a positive inexact result rounds down.
    [InlineData(3u, LargestDenormal)]     // RC=TowardZero: likewise rounds down (toward 0).
    public void MulsdTieToNormalRoundsDownInRdAndRz(uint rc, ulong expected)
    {
        var (cpu, bus) = SetupSse2(code: [0xF2, 0x0F, 0x59, ModRegRm(0, 1)]);
        using (bus)
        {
            cpu.Sse.Mxcsr = (cpu.Sse.Mxcsr & ~MxcsrBits.RoundingControlMask) | (rc << MxcsrBits.RoundingControlShift);
            cpu.Sse.Xmm(0) = new XmmValue { D0 = unchecked((uint)JustBelowOneUlpFromDblMin), D1 = unchecked((uint)(JustBelowOneUlpFromDblMin >> 32)) };
            cpu.Sse.Xmm(1) = new XmmValue { D0 = unchecked((uint)DblMin), D1 = unchecked((uint)(DblMin >> 32)) };

            cpu.Step();

            Assert.Equal(expected, cpu.Sse.Xmm(0).Low);
            Assert.Equal((uint)(MxcsrBits.UnderflowFlag | MxcsrBits.PrecisionFlag), cpu.Sse.Mxcsr & MxcsrBits.AllFlags);
        }
    }

    [Fact]
    public void MulsdTieToNormalFlushesToZeroWithUnderflowMaskedAndFtzSet()
    {
        var (cpu, bus) = SetupSse2(code: [0xF2, 0x0F, 0x59, ModRegRm(0, 1)]);
        using (bus)
        {
            cpu.Sse.Mxcsr |= MxcsrBits.FlushToZero;
            cpu.Sse.Xmm(0) = new XmmValue { D0 = unchecked((uint)JustBelowOneUlpFromDblMin), D1 = unchecked((uint)(JustBelowOneUlpFromDblMin >> 32)) };
            cpu.Sse.Xmm(1) = new XmmValue { D0 = unchecked((uint)DblMin), D1 = unchecked((uint)(DblMin >> 32)) };

            cpu.Step();

            Assert.Equal(DoubleFloat.PositiveZero, cpu.Sse.Xmm(0).Low);
            Assert.Equal((uint)(MxcsrBits.UnderflowFlag | MxcsrBits.PrecisionFlag), cpu.Sse.Mxcsr & MxcsrBits.AllFlags);
        }
    }

    [Fact]
    public void MulsdTieToNormalSignalsXmWithUnderflowWhenUnmaskedAndLeavesTheDestinationUntouched()
    {
        var (cpu, bus) = SetupSse2(osXmmExcpt: true, code: [0xF2, 0x0F, 0x59, ModRegRm(0, 1)]);
        using (bus)
        {
            cpu.Sse.Mxcsr &= ~MxcsrBits.UnderflowMask;
            cpu.Sse.Xmm(0) = new XmmValue { D0 = unchecked((uint)JustBelowOneUlpFromDblMin), D1 = unchecked((uint)(JustBelowOneUlpFromDblMin >> 32)) };
            cpu.Sse.Xmm(1) = new XmmValue { D0 = unchecked((uint)DblMin), D1 = unchecked((uint)(DblMin >> 32)) };
            ulong before = cpu.Sse.Xmm(0).Low;

            byte vector = FaultVector(cpu, () => cpu.Step());

            Assert.Equal(19, vector);
            // Precision alongside Underflow: the computation itself still
            // determines the tie-to-normal result is inexact at the
            // denormal grid's own reduced precision whether or not the
            // trap fires -- RaiseIfUnmasked only ever strips OE/UE/PE for an
            // unmasked *pre-computation* exception (IE/DE/ZE), never for an
            // unmasked Underflow itself.
            Assert.Equal((uint)(MxcsrBits.UnderflowFlag | MxcsrBits.PrecisionFlag), cpu.Sse.Mxcsr & MxcsrBits.AllFlags);
            Assert.Equal(before, cpu.Sse.Xmm(0).Low);
        }
    }

    [Fact]
    public void DivsdTieToNormalSetsUnderflowAndPrecisionInRoundToNearest()
    {
        // 0x001FFFFFFFFFFFFF / 2.0: the same tie-to-normal shape one level
        // up (a value just below twice the smallest normal, halved),
        // confirmed on the host the same way.
        const ulong dividend = 0x001F_FFFF_FFFF_FFFFul;
        var (cpu, bus) = SetupSse2(code: [0xF2, 0x0F, 0x5E, ModRegRm(0, 1)]);   // DIVSD XMM0, XMM1
        using (bus)
        {
            cpu.Sse.Xmm(0) = new XmmValue { D0 = unchecked((uint)dividend), D1 = unchecked((uint)(dividend >> 32)) };
            cpu.Sse.Xmm(1) = new XmmValue { D0 = 0, D1 = 0x4000_0000 };   // 2.0

            cpu.Step();

            Assert.Equal(DblMin, cpu.Sse.Xmm(0).Low);
            Assert.Equal((uint)(MxcsrBits.UnderflowFlag | MxcsrBits.PrecisionFlag), cpu.Sse.Mxcsr & MxcsrBits.AllFlags);
        }
    }

    // === review finding 3: F2 0F 5B and unprefixed 0F E6 are undefined ==============

    [Fact]
    public void F2Opcode5BFaultsWithUd()
    {
        var (cpu, bus) = SetupSse2(code: [0xF2, 0x0F, 0x5B, ModRegRm(0, 1)]);
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    [Fact]
    public void UnprefixedOpcodeE6FaultsWithUd()
    {
        var (cpu, bus) = SetupSse2(code: [0x0F, 0xE6, ModRegRm(0, 1)]);
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    // === review finding 4: conflicting mandatory prefixes beyond 66+F2 are #UD too ====

    [Theory]
    [InlineData(0x66, 0xF3)]   // 66 then F3
    [InlineData(0xF3, 0x66)]   // F3 then 66
    [InlineData(0xF2, 0xF3)]   // F2 then F3
    [InlineData(0xF3, 0xF2)]   // F3 then F2
    public void ConflictingMandatoryPrefixesFaultWithUd(byte first, byte second)
    {
        var (cpu, bus) = SetupSse2(code: [first, second, 0x0F, 0x58, ModRegRm(0, 1)]);   // ADD*S*, whichever the prefixes pick.
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    // === review finding 5: DAZ can never be live, or reported, outside the current model's mask ===

    [Fact]
    public void DazBitSetOutsideTheModelsOwnMaskIsNeverLive()
    {
        // Simulates the one path that can leave a stray DAZ bit in
        // Sse.Mxcsr without a guest ever asking for it: a snapshot restored
        // across a model change (SseUnit.ReadSnapshot has no model to mask
        // against). CurrentDaz() must still refuse to honour it on a model
        // whose own MXCSR_MASK excludes bit 6.
        var (cpu, bus) = SetupSse2(model: CpuModel.PentiumIII, code: [0x0F, 0x58, ModRegRm(0, 1)]);   // ADDSS XMM0, XMM1
        using (bus)
        {
            cpu.Sse.Mxcsr |= MxcsrBits.Daz;   // poked directly, bypassing LDMXCSR's own #GP(0) -- exactly what a cross-model snapshot restore would leave behind.
            cpu.Sse.Xmm(0).D0 = SingleFloat.FromFloat(1.0f);
            cpu.Sse.Xmm(1).D0 = 0x0000_0001u;   // smallest positive single denormal.

            cpu.Step();

            // DAZ not honoured: the denormal is added for real, rounding
            // back to exactly 1.0 (the true sum is inexact at single
            // precision, hence Precision alongside Denormal -- confirmed on
            // the host, tools/NativeOracle --probe) rather than the exact,
            // no-flags-at-all result DAZ's own zero-substitution would give.
            Assert.Equal(SingleFloat.FromFloat(1.0f), cpu.Sse.Xmm(0).D0);
            Assert.Equal((uint)(MxcsrBits.DenormalFlag | MxcsrBits.PrecisionFlag), cpu.Sse.Mxcsr & MxcsrBits.AllFlags);
        }
    }

    [Fact]
    public void StmxcsrNeverReportsABitOutsideTheModelsOwnMask()
    {
        const uint address = 0x4000;
        var (cpu, bus) = SetupSse2(model: CpuModel.PentiumIII, code: Mem32Instruction(0, [0x0F, 0xAE], 3, address));   // STMXCSR [address]
        using (bus)
        {
            cpu.Sse.Mxcsr |= MxcsrBits.Daz;   // poked directly, as above.

            cpu.Step();

            Assert.Equal(0u, bus.ReadDword(address) & MxcsrBits.Daz);
        }
    }

    [Fact]
    public void FxsaveNeverReportsABitOutsideTheModelsOwnMask()
    {
        const uint area = 0x6000;
        var (cpu, bus) = SetupSse2(model: CpuModel.PentiumIII, code: Mem32Instruction(0, [0x0F, 0xAE], 0, area));   // FXSAVE [area]
        using (bus)
        {
            cpu.Sse.Mxcsr |= MxcsrBits.Daz;

            cpu.Step();

            Assert.Equal(0u, bus.ReadDword(area + 24) & MxcsrBits.Daz);
        }
    }

    // === review finding 6: F2/F3 0F D6 with a memory operand is #UD before #NM =========

    [Theory]
    [InlineData((byte)0xF2)]   // MOVDQ2Q: register-register only.
    [InlineData((byte)0xF3)]   // MOVQ2DQ: register-register only.
    public void D6WithAMemoryOperandFaultsWithUdEvenWithCr0TsSet(byte prefix)
    {
        var (cpu, bus) = SetupSse2(code: Mem32Instruction(prefix, [0x0F, 0xD6], 0, 0x4000));
        using (bus)
        {
            cpu.State.Cr0 |= CpuState.Cr0TaskSwitched;
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    [Fact]
    public void D6With66AndAMemoryOperandStillOnlyFaultsWithDeviceNotAvailable()
    {
        // 66 0F D6 (MOVQ store) genuinely does have a memory form -- this
        // is the control case confirming the fix above is prefix-specific,
        // not a blanket rejection of every memory operand at this opcode.
        var (cpu, bus) = SetupSse2(code: Mem32Instruction(0x66, [0x0F, 0xD6], 0, 0x4000));
        using (bus)
        {
            cpu.State.Cr0 |= CpuState.Cr0TaskSwitched;
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(7, vector);
        }
    }

    // === 66-prefixed MOVNTI: per the SDM, no 66 form of MOVNTI exists at all ============

    [Fact]
    public void MovntiWith66PrefixFaultsWithUd()
    {
        var (cpu, bus) = SetupSse2(code: Mem32Instruction(0x66, [0x0F, 0xC3], 0, 0x4000));
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }
}
