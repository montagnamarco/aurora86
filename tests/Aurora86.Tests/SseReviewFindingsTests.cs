using Aurora86.Core.Cpu;
using Aurora86.Core.Cpu.Sse;
using Aurora86.Core.Memory;

namespace Aurora86.Tests;

/// <summary>
/// Work package P4.1 (SSE): the eleven review findings on the original
/// implementation, each with the register-level vector that shows it.
/// SseInstructionTests.cs's own twenty cases are unaffected by any of these
/// fixes and are left there; this file is only the corrections.
///
/// Every masked floating-point vector here (the overflow band, the
/// tininess band, RCPSS/RSQRTSS's special values) was read off the real
/// CPU through <c>tools/NativeOracle --probe</c> before being written down
/// as an expected value -- see each test's own comment for the exact
/// invocation.
/// </summary>
public class SseReviewFindingsTests
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

    /// <summary>A Pentium III with CR4.OSFXSR set unless <paramref name="osFxsr"/> says otherwise, and CR4.OSXMMEXCPT set only when <paramref name="osXmmExcpt"/> asks for it.</summary>
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

    // === 1: SFENCE checks only CPUID.SSE, LOCK and a memory operand =============

    [Fact]
    public void SfenceFaultsWithUdWithoutCpuidSse()
    {
        var (cpu, bus) = Setup(0x0F, 0xAE, ModRegRm(7, 0));
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.PentiumII);
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    [Fact]
    public void SfenceFaultsWithUdOnLock()
    {
        var (cpu, bus) = SetupSse(code: [0xF0, 0x0F, 0xAE, ModRegRm(7, 0)]);   // LOCK SFENCE
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    [Fact]
    public void SfenceFaultsWithUdOnAMemoryOperand()
    {
        var (cpu, bus) = SetupSse(code: Mem32Instruction([0x0F, 0xAE], 7, 0x4000));
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    [Fact]
    public void SfenceIgnoresCr0EmCr0TsAndCr4OsFxsr()
    {
        var (cpu, bus) = SetupSse(osFxsr: false, code: [0x0F, 0xAE, ModRegRm(7, 0)]);
        using (bus)
        {
            cpu.State.Cr0 |= CpuState.Cr0Emulation | CpuState.Cr0TaskSwitched;
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(0xFF, vector);
        }
    }

    // === 2: overflow after rounding is decided per RC, not from the exact value =

    [Theory]
    // MULSS 5FB504F3h x 5F3504F3h -- read off the host oracle at each RC
    // (tools/NativeOracle --probe F30F59C1 <5FB504F3 padded to 32 hex>
    // <5F3504F3 padded to 32 hex> <mxcsr>): round-to-nearest, toward zero
    // and toward -infinity all round the exact (over-FLT_MAX) product back
    // down to exactly FLT_MAX with only Precision set; only round toward
    // +infinity carries it into Overflow, rounding up to infinity instead.
    [InlineData(0x0000_1F80u, 0x7F7F_FFFFu, 0x0000_1FA0u)]   // RN
    [InlineData(0x0000_7F80u, 0x7F7F_FFFFu, 0x0000_7FA0u)]   // toward zero
    [InlineData(0x0000_3F80u, 0x7F7F_FFFFu, 0x0000_3FA0u)]   // toward -infinity
    [InlineData(0x0000_5F80u, 0x7F80_0000u, 0x0000_5FA8u)]   // toward +infinity
    public void MulssOverflowIsDecidedByTheRoundedResultNotTheExactOne(uint initialMxcsr, uint expectedResult, uint expectedMxcsr)
    {
        var (cpu, bus) = SetupSse(code: [0xF3, 0x0F, 0x59, ModRegRm(0, 1)]);   // MULSS XMM0, XMM1
        using (bus)
        {
            cpu.Sse.Mxcsr = initialMxcsr;
            cpu.Sse.Xmm(0).D0 = 0x5FB5_04F3u;
            cpu.Sse.Xmm(1).D0 = 0x5F35_04F3u;

            cpu.Step();

            Assert.Equal(expectedResult, cpu.Sse.Xmm(0).D0);
            Assert.Equal(expectedMxcsr, cpu.Sse.Mxcsr);
        }
    }

    // === 3: tininess is decided before the denormal-storage rounding runs =======

    [Fact]
    public void MulssTininessIsDecidedBeforeTheDenormalRoundingCarriesItBackToNormal()
    {
        // MULSS 3F7FFFFFh x 00800000h, RN, all masked -- read off the host
        // oracle (tools/NativeOracle --probe F30F59C1 ...): the natural,
        // unbounded-exponent 24-bit rounding lands one exponent below the
        // smallest normal (tiny), even though the *second* rounding needed
        // to fit the reduced denormal-grid precision then ties back up to
        // exactly the smallest normal -- Underflow is still set.
        var (cpu, bus) = SetupSse(code: [0xF3, 0x0F, 0x59, ModRegRm(0, 1)]);
        using (bus)
        {
            cpu.Sse.Mxcsr = 0x1F80;
            cpu.Sse.Xmm(0).D0 = 0x3F7F_FFFFu;
            cpu.Sse.Xmm(1).D0 = 0x0080_0000u;

            cpu.Step();

            Assert.Equal(0x0080_0000u, cpu.Sse.Xmm(0).D0);
            Assert.Equal(0x1FB0u, cpu.Sse.Mxcsr);
        }
    }

    [Fact]
    public void MulssTininessFlushesToZeroWhenFzIsSet()
    {
        var (cpu, bus) = SetupSse(code: [0xF3, 0x0F, 0x59, ModRegRm(0, 1)]);
        using (bus)
        {
            cpu.Sse.Mxcsr = 0x9F80;   // FZ (bit15) set, RN, all masked.
            cpu.Sse.Xmm(0).D0 = 0x3F7F_FFFFu;
            cpu.Sse.Xmm(1).D0 = 0x0080_0000u;

            cpu.Step();

            Assert.Equal(SingleFloat.PositiveZero, cpu.Sse.Xmm(0).D0);
            Assert.Equal(0x9FB0u, cpu.Sse.Mxcsr);
        }
    }

    [Fact]
    public void DivssTininessBoundaryTieRoundsToTheSmallestNormalAndSetsUnderflow()
    {
        // DIVSS 00FFFFFFh / 40000000h, RN, all masked (the same host
        // invocation shape as the MULSS case above, opcode F30F5EC1).
        var (cpu, bus) = SetupSse(code: [0xF3, 0x0F, 0x5E, ModRegRm(0, 1)]);
        using (bus)
        {
            cpu.Sse.Mxcsr = 0x1F80;
            cpu.Sse.Xmm(0).D0 = 0x00FF_FFFFu;
            cpu.Sse.Xmm(1).D0 = 0x4000_0000u;

            cpu.Step();

            Assert.Equal(0x0080_0000u, cpu.Sse.Xmm(0).D0);
            Assert.Equal(0x1FB0u, cpu.Sse.Mxcsr);
        }
    }

    [Fact]
    public void DivssTininessFlushesToZeroWhenFzIsSet()
    {
        var (cpu, bus) = SetupSse(code: [0xF3, 0x0F, 0x5E, ModRegRm(0, 1)]);
        using (bus)
        {
            cpu.Sse.Mxcsr = 0x9F80;
            cpu.Sse.Xmm(0).D0 = 0x00FF_FFFFu;
            cpu.Sse.Xmm(1).D0 = 0x4000_0000u;

            cpu.Step();

            Assert.Equal(SingleFloat.PositiveZero, cpu.Sse.Xmm(0).D0);
            Assert.Equal(0x9FB0u, cpu.Sse.Mxcsr);
        }
    }

    // === 4: Underflow with UM unmasked fires on an exact tiny result too =======

    [Fact]
    public void MulssUnderflowUnmaskedSignalsXmForAnExactTinyResult()
    {
        // MXCSR 1780h (UM, bit11, unmasked; everything else masked),
        // OSXMMEXCPT=1: 00800000h x 3F000000h = 2^-127 exactly -- tiny, but
        // not inexact at all -- still traps, because UM is unmasked.
        var (cpu, bus) = SetupSse(osXmmExcpt: true, code: [0xF3, 0x0F, 0x59, ModRegRm(0, 1)]);
        using (bus)
        {
            cpu.Sse.Mxcsr = 0x1780;
            uint original = 0x0080_0000u;
            cpu.Sse.Xmm(0).D0 = original;
            cpu.Sse.Xmm(1).D0 = 0x3F00_0000u;

            byte vector = FaultVector(cpu, () => cpu.Step());

            Assert.Equal(19, vector);
            Assert.Equal(original, cpu.Sse.Xmm(0).D0);
            Assert.NotEqual(0u, cpu.Sse.Mxcsr & MxcsrBits.UnderflowFlag);
        }
    }

    // === 5: an unmasked pre-computation exception discards OE/UE/PE ============

    [Fact]
    public void AddpsUnmaskedInvalidDiscardsAnotherLanesPrecisionFlag()
    {
        // MXCSR 1F00h (IM, bit7, unmasked; everything else masked),
        // OSXMMEXCPT=1: lane 0 is a signaling NaN (unmasked Invalid); lane
        // 1 (1.0 + 2^-30) would independently set Precision if computed --
        // it never gets the chance.
        var (cpu, bus) = SetupSse(osXmmExcpt: true, code: [0x0F, 0x58, ModRegRm(0, 1)]);   // ADDPS XMM0, XMM1
        using (bus)
        {
            cpu.Sse.Mxcsr = 0x1F00;
            var destinationBefore = new XmmValue { D0 = 0x7F80_0001u, D1 = 0x3F80_0000u, D2 = 0, D3 = 0 };
            cpu.Sse.Xmm(0) = destinationBefore;
            cpu.Sse.Xmm(1) = new XmmValue { D0 = 0x3F80_0000u, D1 = 0x3080_0000u, D2 = 0, D3 = 0 };

            byte vector = FaultVector(cpu, () => cpu.Step());

            Assert.Equal(19, vector);
            Assert.Equal(0x1F01u, cpu.Sse.Mxcsr);
            Assert.Equal(destinationBefore, cpu.Sse.Xmm(0));
        }
    }

    [Fact]
    public void AddssUnmaskedDenormalDiscardsThePrecisionFlag()
    {
        // MXCSR 1E80h (DM, bit8, unmasked; everything else masked): the
        // source is the smallest denormal, an unmasked Denormal condition;
        // 1.0 + that denormal would independently set Precision (the
        // denormal is far too small to survive rounding) -- discarded.
        var (cpu, bus) = SetupSse(osXmmExcpt: true, code: [0xF3, 0x0F, 0x58, ModRegRm(0, 1)]);   // ADDSS XMM0, XMM1
        using (bus)
        {
            cpu.Sse.Mxcsr = 0x1E80;
            uint original = SingleFloat.FromFloat(1.0f);
            cpu.Sse.Xmm(0).D0 = original;
            cpu.Sse.Xmm(1).D0 = 0x0000_0001u;

            byte vector = FaultVector(cpu, () => cpu.Step());

            Assert.Equal(19, vector);
            Assert.Equal(0x1E82u, cpu.Sse.Mxcsr);
            Assert.Equal(original, cpu.Sse.Xmm(0).D0);
        }
    }

    // === 6: RCPPS/RSQRTPS treat a denormal source as zero, and flush tiny results

    [Fact]
    public void RcpssTreatsADenormalSourceAsAZeroOfTheSameSign()
    {
        // RCPSS 00400000h -> 7F800000h (rcp(+0)), confirmed on the host
        // (tools/NativeOracle --probe F30F53C1 ...), not the finite ~2^127
        // that 1/x of that denormal's own value would give.
        var (cpu, bus) = SetupSse(code: [0xF3, 0x0F, 0x53, ModRegRm(0, 1)]);   // RCPSS XMM0, XMM1
        using (bus)
        {
            cpu.Sse.Xmm(1).D0 = 0x0040_0000u;
            cpu.Step();
            Assert.Equal(0x7F80_0000u, cpu.Sse.Xmm(0).D0);
        }
    }

    [Fact]
    public void RcpssFlushesATinyResultToZero()
    {
        // RCPSS 7F000000h -> +0, confirmed on the host, whatever MXCSR.FZ
        // says (RCPPS/RSQRTPS consult no MXCSR control bit at all).
        var (cpu, bus) = SetupSse(code: [0xF3, 0x0F, 0x53, ModRegRm(0, 1)]);
        using (bus)
        {
            cpu.Sse.Xmm(1).D0 = 0x7F00_0000u;
            cpu.Step();
            Assert.Equal(SingleFloat.PositiveZero, cpu.Sse.Xmm(0).D0);
        }
    }

    [Theory]
    [InlineData(0x0000_0001u, 0x7F80_0000u)]   // RSQRTSS: +denormal treated as +0 -> +infinity.
    [InlineData(0x8000_0001u, 0xFF80_0000u)]   // RSQRTSS: -denormal treated as -0 -> -infinity.
    public void RsqrtssTreatsADenormalSourceAsAZeroOfTheSameSign(uint source, uint expected)
    {
        // Confirmed on the host (tools/NativeOracle --probe F30F52C1 ...).
        var (cpu, bus) = SetupSse(code: [0xF3, 0x0F, 0x52, ModRegRm(0, 1)]);   // RSQRTSS XMM0, XMM1
        using (bus)
        {
            cpu.Sse.Xmm(1).D0 = source;
            cpu.Step();
            Assert.Equal(expected, cpu.Sse.Xmm(0).D0);
        }
    }

    // === 7: MASKMOVQ validates its whole 8-byte destination first ==============

    [Fact]
    public void MaskmovqFaultsOnA16BitAddressWraparoundBeforeWritingAnyByte()
    {
        var (cpu, bus) = SetupSse(code: [0x0F, 0xF7, ModRegRm(0, 1)]);   // MASKMOVQ MM0, MM1
        using (bus)
        {
            cpu.State.Di = 0xFFFC;   // DI+7 = 0x10003: wraps past FFFFh.
            cpu.Fpu.WriteMmx(0, 0x1122_3344_5566_7788ul);
            cpu.Fpu.WriteMmx(1, 0x8080_8080_8080_8080ul);   // every byte selected.

            bus.WriteByte(0x0000, 0xCC);
            bus.WriteByte(0x0001, 0xCC);
            bus.WriteByte(0x0002, 0xCC);
            bus.WriteByte(0xFFFC, 0xCC);
            bus.WriteByte(0xFFFF, 0xCC);

            byte vector = FaultVector(cpu, () => cpu.Step());

            Assert.Equal(13, vector);
            Assert.Equal((byte)0xCC, bus.ReadByte(0x0000));
            Assert.Equal((byte)0xCC, bus.ReadByte(0xFFFC));
            Assert.Equal((byte)0xCC, bus.ReadByte(0xFFFF));
        }
    }

    // === 8: a 128-bit store validates the whole 16 bytes before either half ====

    [Fact]
    public void MovupsStoreValidatesTheWholeSixteenBytesBeforeWritingEitherHalf()
    {
        const uint address = 0x2000;
        var (cpu, bus) = SetupSse(code: Mem32Instruction([0x0F, 0x11], 0, address));   // MOVUPS [address], XMM0
        using (bus)
        {
            cpu.State.Ds.Limit = address + 10;   // the store's last byte (address+15) is past the limit.

            for (uint i = 0; i < 16; i++) bus.WriteByte(address + i, 0xCC);
            cpu.Sse.Xmm(0) = new XmmValue { D0 = 0x1111_1111, D1 = 0x2222_2222, D2 = 0x3333_3333, D3 = 0x4444_4444 };

            byte vector = FaultVector(cpu, () => cpu.Step());

            Assert.Equal(13, vector);
            for (uint i = 0; i < 16; i++) Assert.Equal((byte)0xCC, bus.ReadByte(address + i));
        }
    }

    [Fact]
    public void MovntpsStoreValidatesTheWholeSixteenBytesBeforeWritingEitherHalf()
    {
        const uint address = 0x2000;   // 16-byte aligned.
        var (cpu, bus) = SetupSse(code: Mem32Instruction([0x0F, 0x2B], 0, address));   // MOVNTPS [address], XMM0
        using (bus)
        {
            cpu.State.Ds.Limit = address + 10;

            for (uint i = 0; i < 16; i++) bus.WriteByte(address + i, 0xCC);
            cpu.Sse.Xmm(0) = new XmmValue { D0 = 1, D1 = 2, D2 = 3, D3 = 4 };

            byte vector = FaultVector(cpu, () => cpu.Step());

            Assert.Equal(13, vector);
            for (uint i = 0; i < 16; i++) Assert.Equal((byte)0xCC, bus.ReadByte(address + i));
        }
    }

    // === 9: decode-time #UD outranks #NM for four fixed-shape encodings ========

    [Fact]
    public void PextrwMemoryFormFaultsWithUdEvenWithCr0TsSet()
    {
        var (cpu, bus) = SetupSse(code: [.. Mem32Instruction([0x0F, 0xC5], 0, 0x4000), 0x00]);   // PEXTRW EAX, [mem], 0
        using (bus)
        {
            cpu.State.Cr0 |= CpuState.Cr0TaskSwitched;
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    [Fact]
    public void PmovmskbMemoryFormFaultsWithUdEvenWithCr0TsSet()
    {
        var (cpu, bus) = SetupSse(code: Mem32Instruction([0x0F, 0xD7], 0, 0x4000));
        using (bus)
        {
            cpu.State.Cr0 |= CpuState.Cr0TaskSwitched;
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    [Fact]
    public void MovntqRegisterFormFaultsWithUdEvenWithCr0TsSet()
    {
        var (cpu, bus) = SetupSse(code: [0x0F, 0xE7, ModRegRm(0, 1)]);   // MOVNTQ with a register operand: undefined.
        using (bus)
        {
            cpu.State.Cr0 |= CpuState.Cr0TaskSwitched;
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    [Fact]
    public void MaskmovqMemoryFormFaultsWithUdEvenWithCr0TsSet()
    {
        var (cpu, bus) = SetupSse(code: Mem32Instruction([0x0F, 0xF7], 0, 0x4000));
        using (bus)
        {
            cpu.State.Cr0 |= CpuState.Cr0TaskSwitched;
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    // === 10: SseUnit.ReadSnapshot masks MXCSR's reserved bits alone =============
    //
    // Updated for work package P4.2 (plans/specs/sse2.md): MXCSR_MASK is now
    // per-model (SseUnit.MxcsrMaskFor), and ReadSnapshot's own delegate
    // signature (MachineSnapshot.cs) carries no model to consult -- so it
    // stays a fixed, structural cleanup of the reserved bits 16-31 alone,
    // no longer the Pentium III's own (narrower) MXCSR_MASK. DAZ (bit 6) is
    // therefore preserved by a snapshot round-trip even for a model that
    // could never have set it live through LDMXCSR/FXRSTOR's own #GP(0) --
    // documented as a known, narrow edge case in SseUnit.cs's own remark on
    // ReservedBitsMask.

    [Fact]
    public void SseUnitReadSnapshotMasksMxcsrsReservedBitsAlone()
    {
        var unit = new SseUnit();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            for (int i = 0; i < SseUnit.RegisterCount; i++)
            {
                writer.Write(0u);
                writer.Write(0u);
                writer.Write(0u);
                writer.Write(0u);
            }
            writer.Write(0xFFFF_FFFFu);   // every bit set, DAZ and the reserved bits included.
        }
        stream.Position = 0;
        using var reader = new BinaryReader(stream);

        unit.ReadSnapshot(reader, SseUnit.SnapshotVersion);

        Assert.Equal(0x0000_FFFFu, unit.Mxcsr);
    }
}
