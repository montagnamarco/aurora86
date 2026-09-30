using Aurora86.Core.Cpu;
using Aurora86.Core.Cpu.Sse;
using Aurora86.Core.Memory;

namespace Aurora86.Tests;

/// <summary>
/// SSE3's XMM instructions, driven through their real encodings on Core2,
/// the first processor in the catalog with Prescott's 2004 extensions.
/// </summary>
public class Sse3InstructionTests
{
    private const uint Code = 0x1000;

    private static (Cpu Cpu, PhysicalMemoryBus Bus) Setup(bool enableSse3 = true, params byte[] code)
    {
        var bus = new PhysicalMemoryBus();
        var cpu = new Cpu(bus);
        cpu.State.SelectModel(CpuModel.Core2);
        if (!enableSse3)
        {
            cpu.State.Features = cpu.State.Features.Without(CpuFeature.Sse3);
        }

        foreach (SegReg segment in Enum.GetValues<SegReg>())
        {
            cpu.State.LoadSegmentRealModeReset(segment, 0);
        }

        cpu.State.Cr4 = CpuState.Cr4OsFxsr;
        cpu.State.Esp = 0x8000;
        cpu.State.Eip = Code;
        code.CopyTo(bus.GetRamSpan(Code, code.Length));
        return (cpu, bus);
    }

    private static byte StepAndReturnVector(Cpu cpu)
    {
        byte vector = 0xFF;
        cpu.ProcessorException += (number, _, _, _) =>
        {
            if (vector == 0xFF)
            {
                vector = number;
            }
        };
        cpu.Step();
        return vector;
    }

    private static byte[] Mem32Instruction(byte prefix, byte opcode, int destination, uint address)
    {
        return [prefix, 0x67, 0x0F, opcode, (byte)(0x05 | (destination << 3)),
            (byte)address, (byte)(address >> 8), (byte)(address >> 16), (byte)(address >> 24)];
    }

    [Fact]
    public void ADisabledSse3FeatureRejectsTheEncoding()
    {
        (Cpu cpu, _) = Setup(false, 0xF2, 0x0F, 0x7C, 0xC1); // HADDPS XMM0,XMM1

        Assert.False(cpu.State.Features.Has(CpuFeature.Sse3));
        Assert.Equal(6, StepAndReturnVector(cpu));
    }

    [Theory]
    [InlineData(CpuModel.Pentium4, false)]
    [InlineData(CpuModel.Core2, true)]
    [InlineData(CpuModel.Haswell, true)]
    public void CpuidAdvertisesSse3OnlyOnModelsWithTheSilicon(CpuModel model, bool expected)
    {
        (Cpu cpu, _) = Setup(true, 0x0F, 0xA2);
        cpu.State.SelectModel(model);
        cpu.State.Eax = 1;
        Assert.Equal(0xFF, StepAndReturnVector(cpu));
        Assert.Equal(expected, (cpu.State.Ecx & 1) != 0);
        Assert.Equal(0u, cpu.State.Ecx & 8); // MONITOR/MWAIT remain a separate unsupported bit.
    }

    [Theory]
    [InlineData(CpuModel.PentiumIII)]
    [InlineData(CpuModel.Pentium4)]
    public void PreSse3ModelsRejectTheEncodingEvenWithOsFxsr(CpuModel model)
    {
        (Cpu cpu, _) = Setup(false, 0xF2, 0x0F, 0x7C, 0xC1);
        cpu.State.SelectModel(model);
        cpu.State.Cr4 |= CpuState.Cr4OsFxsr;

        Assert.Equal(6, StepAndReturnVector(cpu));
    }

    [Theory]
    [InlineData(CpuState.Cr0Emulation, 6)]
    [InlineData(CpuState.Cr0TaskSwitched, 7)]
    public void Sse3UsesTheOrdinaryEmAndTsGates(uint controlBit, byte expectedVector)
    {
        (Cpu cpu, _) = Setup(true, 0xF2, 0x0F, 0x7C, 0xC1);
        cpu.State.Cr0 |= controlBit;

        Assert.Equal(expectedVector, StepAndReturnVector(cpu));
    }

    [Fact]
    public void Sse3NeedsOsFxsrAndRejectsConflictingMandatoryPrefixes()
    {
        (Cpu noOsFxsr, _) = Setup(true, 0xF2, 0x0F, 0x7C, 0xC1);
        noOsFxsr.State.Cr4 = 0;
        Assert.Equal(6, StepAndReturnVector(noOsFxsr));

        (Cpu conflictingPrefixes, _) = Setup(true, 0x66, 0xF2, 0x0F, 0x7C, 0xC1);
        Assert.Equal(6, StepAndReturnVector(conflictingPrefixes));
    }

    [Fact]
    public void HorizontalSingleAndAlternatingArithmeticUseTheDocumentedLaneOrder()
    {
        (Cpu haddCpu, _) = Setup(true, 0xF2, 0x0F, 0x7C, 0xC1); // HADDPS XMM0,XMM1
        haddCpu.Sse.Xmm(0) = new XmmValue { D0 = 0x3F800000, D1 = 0x40000000, D2 = 0x40800000, D3 = 0x41000000 };
        haddCpu.Sse.Xmm(1) = new XmmValue { D0 = 0x41800000, D1 = 0x42000000, D2 = 0x42800000, D3 = 0x43000000 };

        Assert.Equal(0xFF, StepAndReturnVector(haddCpu));
        Assert.Equal(new XmmValue { D0 = 0x40400000, D1 = 0x41400000, D2 = 0x42400000, D3 = 0x43400000 }, haddCpu.Sse.Xmm(0));

        (Cpu addsubCpu, _) = Setup(true, 0xF2, 0x0F, 0xD0, 0xC1); // ADDSUBPS XMM0,XMM1
        addsubCpu.Sse.Xmm(0) = new XmmValue { D0 = 0x3F800000, D1 = 0x40000000, D2 = 0x40800000, D3 = 0x41000000 };
        addsubCpu.Sse.Xmm(1) = new XmmValue { D0 = 0x41800000, D1 = 0x42000000, D2 = 0x42800000, D3 = 0x43000000 };

        Assert.Equal(0xFF, StepAndReturnVector(addsubCpu));
        Assert.Equal(new XmmValue { D0 = 0xC1700000, D1 = 0x42080000, D2 = 0xC2700000, D3 = 0x43080000 }, addsubCpu.Sse.Xmm(0));
    }

    [Fact]
    public void HorizontalDoubleAndSubtractUseAdjacentQwordPairs()
    {
        (Cpu cpu, _) = Setup(true, 0x66, 0x0F, 0x7D, 0xC1); // HSUBPD XMM0,XMM1
        cpu.Sse.Xmm(0) = XmmValue.FromHalves(0x4010000000000000, 0x4020000000000000); // 4, 8
        cpu.Sse.Xmm(1) = XmmValue.FromHalves(0x4030000000000000, 0x4040000000000000); // 16, 32

        Assert.Equal(0xFF, StepAndReturnVector(cpu));
        Assert.Equal(XmmValue.FromHalves(0xC010000000000000, 0xC030000000000000), cpu.Sse.Xmm(0)); // -4, -16
    }

    [Fact]
    public void DuplicateMovesCopyBitsWithoutChangingMxcsr()
    {
        (Cpu ddupCpu, _) = Setup(true, 0xF2, 0x0F, 0x12, 0xC1); // MOVDDUP XMM0,XMM1
        ddupCpu.Sse.Mxcsr = 0x9FC0;
        ddupCpu.Sse.Xmm(1) = new XmmValue { D0 = 0x12345678, D1 = 0x7FF00001, D2 = 0xDEADBEEF, D3 = 0xC0000000 };

        Assert.Equal(0xFF, StepAndReturnVector(ddupCpu));
        Assert.Equal(XmmValue.FromHalves(0x7FF0000112345678, 0x7FF0000112345678), ddupCpu.Sse.Xmm(0));
        Assert.Equal(0x9FC0u, ddupCpu.Sse.Mxcsr);

        (Cpu sldupCpu, _) = Setup(true, 0xF3, 0x0F, 0x12, 0xC1); // MOVSLDUP XMM0,XMM1
        sldupCpu.Sse.Xmm(1) = new XmmValue { D0 = 0x7F800001, D1 = 2, D2 = 3, D3 = 4 };

        Assert.Equal(0xFF, StepAndReturnVector(sldupCpu));
        Assert.Equal(new XmmValue { D0 = 0x7F800001, D1 = 0x7F800001, D2 = 3, D3 = 3 }, sldupCpu.Sse.Xmm(0));

        (Cpu shdupCpu, _) = Setup(true, 0xF3, 0x0F, 0x16, 0xC1); // MOVSHDUP XMM0,XMM1
        shdupCpu.Sse.Xmm(1) = new XmmValue { D0 = 0x7F800001, D1 = 0xFF800002, D2 = 1, D3 = 0x80000000 };

        Assert.Equal(0xFF, StepAndReturnVector(shdupCpu));
        Assert.Equal(new XmmValue { D0 = 0xFF800002, D1 = 0xFF800002, D2 = 0x80000000, D3 = 0x80000000 }, shdupCpu.Sse.Xmm(0));
    }

    [Fact]
    public void LddquReadsExactlySixteenUnalignedBytesAndRejectsARegisterOperand()
    {
        (Cpu cpu, PhysicalMemoryBus bus) = Setup(true,
            0xF2, 0x67, 0x0F, 0xF0, 0x05, 0x01, 0x40, 0x00, 0x00); // LDDQU XMM0,[4001]
        for (uint offset = 0; offset < 16; offset++)
        {
            bus.WriteByte(0x4001 + offset, (byte)(0xA0 + offset));
        }

        Assert.Equal(0xFF, StepAndReturnVector(cpu));
        Assert.Equal(0xA7A6A5A4A3A2A1A0ul, cpu.Sse.Xmm(0).Low);
        Assert.Equal(0xAFAEADACABAAA9A8ul, cpu.Sse.Xmm(0).High);

        (Cpu invalidCpu, _) = Setup(true, 0xF2, 0x0F, 0xF0, 0xC1); // LDDQU XMM0,XMM1
        invalidCpu.State.Cr0 |= CpuState.Cr0TaskSwitched;
        Assert.Equal(6, StepAndReturnVector(invalidCpu));
    }

    [Fact]
    public void AlignedSse3FormsFaultButMovddupReadsOnlyItsQwordAtTheSegmentEnd()
    {
        (Cpu alignedCpu, _) = Setup(true, Mem32Instruction(0xF2, 0x7C, 0, 0x4001)); // HADDPS XMM0,[4001]
        Assert.Equal(13, StepAndReturnVector(alignedCpu));

        (Cpu ddupCpu, PhysicalMemoryBus bus) = Setup(true, Mem32Instruction(0xF2, 0x12, 0, 0x4000));
        ddupCpu.State.Ds.Limit = 0x4007;
        bus.WriteDword(0x4000, 0x12345678);
        bus.WriteDword(0x4004, 0x9ABCDEF0);

        Assert.Equal(0xFF, StepAndReturnVector(ddupCpu));
        Assert.Equal(0x9ABCDEF012345678ul, ddupCpu.Sse.Xmm(0).Low);
        Assert.Equal(0x9ABCDEF012345678ul, ddupCpu.Sse.Xmm(0).High);
    }

    [Fact]
    public void LddquSegmentFailuresLeaveTheDestinationUntouched()
    {
        (Cpu generalProtectionCpu, _) = Setup(true, Mem32Instruction(0xF2, 0xF0, 0, 0x4001));
        XmmValue original = new() { D0 = 1, D1 = 2, D2 = 3, D3 = 4 };
        generalProtectionCpu.Sse.Xmm(0) = original;
        generalProtectionCpu.State.Ds.Limit = 0x400F;

        Assert.Equal(13, StepAndReturnVector(generalProtectionCpu));
        Assert.Equal(original, generalProtectionCpu.Sse.Xmm(0));

        (Cpu stackFaultCpu, _) = Setup(true,
            0x36, 0xF2, 0x67, 0x0F, 0xF0, 0x05, 0x01, 0x40, 0x00, 0x00);
        stackFaultCpu.Sse.Xmm(0) = original;
        stackFaultCpu.State.Ss.Limit = 0x400F;

        Assert.Equal(12, StepAndReturnVector(stackFaultCpu));
    }

    [Fact]
    public void UnmaskedHorizontalInvalidFaultLeavesTheWholeDestinationUntouched()
    {
        (Cpu cpu, _) = Setup(true, 0xF2, 0x0F, 0x7C, 0xC1); // HADDPS XMM0,XMM1
        XmmValue original = new() { D0 = 1, D1 = 2, D2 = 3, D3 = 4 };
        cpu.Sse.Xmm(0) = original;
        cpu.Sse.Xmm(1) = new XmmValue { D0 = 0x7F800001, D1 = 0x3F800000, D2 = 0x3F800000, D3 = 0x3F800000 };
        cpu.Sse.Mxcsr &= ~MxcsrBits.InvalidMask;
        cpu.State.Cr4 |= CpuState.Cr4Osxmmexcpt;

        Assert.Equal(19, StepAndReturnVector(cpu));
        Assert.Equal(original, cpu.Sse.Xmm(0));
        Assert.NotEqual(0u, cpu.Sse.Mxcsr & MxcsrBits.InvalidFlag);
    }
}
