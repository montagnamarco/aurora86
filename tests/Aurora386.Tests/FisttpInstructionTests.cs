using Aurora386.Core.Cpu;
using Aurora386.Core.Cpu.Fpu;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// FISTTP as software uses it: a compiler truncates ST(0) into a signed
/// integer, then the x87 state records the deferred exception for FWAIT.
/// </summary>
public class FisttpInstructionTests
{
    private const uint Code = 0x1000;
    private const uint Data = 0x3000;
    private const ushort ValidTopOnly = 0xFFFC;

    private static (Cpu Cpu, PhysicalMemoryBus Bus) Setup(params byte[] code)
    {
        var bus = new PhysicalMemoryBus();
        var cpu = new Cpu(bus);
        cpu.State.SelectModel(CpuModel.Core2);
        foreach (SegReg segment in Enum.GetValues<SegReg>())
        {
            cpu.State.LoadSegmentRealModeReset(segment, 0);
        }

        cpu.State.Cr0 = CpuState.Cr0ExtensionType | CpuState.Cr0NumericError;
        cpu.State.Esp = 0x8000;
        cpu.State.Eip = Code;
        code.CopyTo(bus.GetRamSpan(Code, code.Length));
        return (cpu, bus);
    }

    private static Raw80 Value(ulong significand, int exponent, bool negative = false)
    {
        ushort signExponent = (ushort)(exponent + 16383);
        if (negative)
        {
            signExponent |= 0x8000;
        }

        return new Raw80(significand, signExponent);
    }

    private static void LoadTop(Cpu cpu, Raw80 value, ushort control = 0x037F, ushort status = 0)
    {
        var registers = new Raw80[8];
        registers[0] = value;
        cpu.Fpu.RestoreState(control, status, ValidTopOnly, registers);
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

    private static ulong ReadStored(PhysicalMemoryBus bus, int width) => width switch
    {
        2 => bus.ReadWord(Data),
        4 => bus.ReadDword(Data),
        _ => bus.ReadDword(Data) | ((ulong)bus.ReadDword(Data + 4) << 32),
    };

    [Theory]
    [InlineData(0xDF, 2)]
    [InlineData(0xDB, 4)]
    public void NarrowFisttpFormsTruncateInsteadOfUsingTheControlWordsRoundingMode(
        byte escape,
        int width)
    {
        (Cpu cpu, PhysicalMemoryBus bus) = Setup(escape, 0x0E, 0x00, 0x30);
        LoadTop(cpu, Value(0xC000000000000000, 0), control: 0x0B7F); // 1.5, FCW says round up.

        Assert.Equal(0xFF, StepAndReturnVector(cpu));
        Assert.Equal(1UL, ReadStored(bus, width));
        Assert.False(cpu.Fpu.IsUsed(0));
        Assert.Equal(0, cpu.Fpu.StatusWord & 0x0200); // C1 clears.
    }

    [Fact]
    public void FisttpQwordRetainsAnIntegerBeyondDoublePrecisionsExactRange()
    {
        (Cpu cpu, PhysicalMemoryBus bus) = Setup(0xDD, 0x0E, 0x00, 0x30);
        LoadTop(cpu, Value(0x8000000000000400, 53));

        Assert.Equal(0xFF, StepAndReturnVector(cpu));
        ulong stored = bus.ReadDword(Data) | ((ulong)bus.ReadDword(Data + 4) << 32);
        Assert.Equal(0x0020000000000001UL, stored);
        Assert.False(cpu.Fpu.IsUsed(0));
    }

    [Fact]
    public void MaskedEmptyStackStoresIndefinitePopsAndSetsInvalidAndStackFault()
    {
        (Cpu cpu, PhysicalMemoryBus bus) = Setup(0xDB, 0x0E, 0x00, 0x30); // FISTTP dword [3000]
        cpu.Fpu.RestoreState(0x037F, 0, 0xFFFF, new Raw80[8]);
        bus.WriteDword(Data, 0xDEADBEEF);

        Assert.Equal(0xFF, StepAndReturnVector(cpu));
        Assert.Equal(0x80000000u, bus.ReadDword(Data));
        Assert.Equal(0x0041, cpu.Fpu.StatusWord & 0x0041);
        Assert.False(cpu.Fpu.IsUsed(0));
    }

    [Fact]
    public void UnmaskedPrecisionStoresAndPopsBeforeItsFollowingFwaitFaults()
    {
        (Cpu cpu, PhysicalMemoryBus bus) = Setup(
            0xDB, 0x0E, 0x00, 0x30,             // FISTTP dword [3000]
            0x9B);                              // FWAIT
        LoadTop(cpu, Value(0xC000000000000000, 0), control: 0x035F); // 1.5, PE unmasked.

        Assert.Equal(0xFF, StepAndReturnVector(cpu));
        Assert.Equal(1u, bus.ReadDword(Data));
        Assert.False(cpu.Fpu.IsUsed(0));
        Assert.Equal(0x80A0, cpu.Fpu.StatusWord & 0x80A0);
        Assert.Equal(16, StepAndReturnVector(cpu));
    }

    [Fact]
    public void UnmaskedInvalidLeavesTheDestinationAndStackForTheHandler()
    {
        (Cpu cpu, PhysicalMemoryBus bus) = Setup(0xDB, 0x0E, 0x00, 0x30);
        LoadTop(cpu, new Raw80(0xC000000000000001, 0x7FFF), control: 0x037E);
        bus.WriteDword(Data, 0xDEADBEEF);

        Assert.Equal(0xFF, StepAndReturnVector(cpu));
        Assert.Equal(0xDEADBEEFu, bus.ReadDword(Data));
        Assert.True(cpu.Fpu.IsUsed(0));
        Assert.Equal(0x8081, cpu.Fpu.StatusWord & 0x8081);
    }

    [Theory]
    [InlineData(CpuState.Cr0Emulation)]
    [InlineData(CpuState.Cr0TaskSwitched)]
    public void FisttpUsesTheX87DeviceNotAvailableGate(uint unavailableBit)
    {
        (Cpu cpu, _) = Setup(0xDB, 0x0E, 0x00, 0x30);
        cpu.State.Cr0 |= unavailableBit;

        Assert.Equal(7, StepAndReturnVector(cpu));
    }

    [Fact]
    public void MissingSse3WinsOverTaskSwitchedAndOsFxsrIsIrrelevant()
    {
        (Cpu missingFeatureCpu, _) = Setup(0xDB, 0x0E, 0x00, 0x30);
        missingFeatureCpu.State.Features = missingFeatureCpu.State.Features.Without(CpuFeature.Sse3);
        missingFeatureCpu.State.Cr0 |= CpuState.Cr0TaskSwitched;
        Assert.Equal(6, StepAndReturnVector(missingFeatureCpu));

        (Cpu noOsFxsrCpu, PhysicalMemoryBus bus) = Setup(0xDB, 0x0E, 0x00, 0x30);
        noOsFxsrCpu.State.Cr4 = 0;
        LoadTop(noOsFxsrCpu, Value(0xC000000000000000, 0));
        Assert.Equal(0xFF, StepAndReturnVector(noOsFxsrCpu));
        Assert.Equal(1u, bus.ReadDword(Data));
    }

    [Theory]
    [InlineData("66", 0xDF, 2)]
    [InlineData("F2", 0xDB, 4)]
    [InlineData("F3", 0xDD, 8)]
    [InlineData("66F2", 0xDF, 2)]
    [InlineData("66F3", 0xDB, 4)]
    [InlineData("F2F3", 0xDD, 8)]
    public void RedundantPrefixesAreIgnoredWithoutChangingTheOpcodeSelectedWidth(
        string prefixes,
        byte escape,
        int width)
    {
        byte[] prefixBytes = Convert.FromHexString(prefixes);
        byte[] code = [.. prefixBytes, escape, 0x0E, 0x00, 0x30];
        (Cpu cpu, PhysicalMemoryBus bus) = Setup(code);
        LoadTop(cpu, Value(0xE000000000000000, 0)); // 1.75
        bus.WriteDword(Data, 0xDEADBEEF);
        bus.WriteDword(Data + 4, 0xC001C0DE);

        Assert.Equal(0xFF, StepAndReturnVector(cpu));
        Assert.Equal(1UL, ReadStored(bus, width));
        Assert.Equal(0x0820, cpu.Fpu.StatusWord & 0x0820);
        Assert.False(cpu.Fpu.IsUsed(0));
    }

    [Fact]
    public void OldPendingExceptionPreventsFisttpBeforeItTouchesTheDestination()
    {
        (Cpu cpu, PhysicalMemoryBus bus) = Setup(0xDB, 0x0E, 0x00, 0x30);
        LoadTop(cpu, Value(0xC000000000000000, 0), control: 0x037E, status: 0x8081);
        bus.WriteDword(Data, 0xDEADBEEF);

        Assert.Equal(16, StepAndReturnVector(cpu));
        Assert.Equal(0xDEADBEEFu, bus.ReadDword(Data));
        Assert.True(cpu.Fpu.IsUsed(0));
        Assert.Equal(0x8081, cpu.Fpu.StatusWord & 0x8081);
    }

    [Fact]
    public void DestinationSegmentFailureLeavesTheStackAndStatusUntouched()
    {
        (Cpu cpu, PhysicalMemoryBus bus) = Setup(0xDD, 0x0E, 0x00, 0x30);
        LoadTop(cpu, Value(0xC000000000000000, 0), status: 0x0200);
        cpu.State.Ds.Limit = Data + 3;
        bus.WriteDword(Data, 0xDEADBEEF);

        Assert.Equal(13, StepAndReturnVector(cpu));
        Assert.Equal(0xDEADBEEFu, bus.ReadDword(Data));
        Assert.True(cpu.Fpu.IsUsed(0));
        Assert.Equal(0x0200, cpu.Fpu.StatusWord);
    }
}
