using Aurora386.Core.Cpu;
using Aurora386.Core.Cpu.Fpu;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// Waiting and nonwaiting boundaries introduced for the 387 and the 486's
/// native numeric-error delivery. Handlers must be able to save and clear
/// pending state, while subsequent x87 and MMX work must leave it intact.
/// </summary>
public class X87SynchronizationTests
{
    private const ushort PendingControl = 0x037E;
    private const ushort PendingStatus = 0x9881; // IE, ES, B and TOP=3.

    /// <summary>Places a pending environment on an otherwise idle real-mode CPU.</summary>
    private static Cpu Prepare(PhysicalMemoryBus bus, string instruction)
    {
        var cpu = new Cpu(bus);
        cpu.State.SelectModel(CpuModel.Core2);
        foreach (SegReg segment in Enum.GetValues<SegReg>())
        {
            cpu.State.LoadSegmentRealModeReset(segment, 0);
        }

        cpu.State.Cr0 = CpuState.Cr0ExtensionType | CpuState.Cr0NumericError;
        cpu.State.Cr4 = CpuState.Cr4OsFxsr;
        cpu.State.Eip = 0x1000;
        cpu.State.Esp = 0x8000;
        byte[] bytes = Convert.FromHexString(instruction);
        bytes.CopyTo(bus.GetRamSpan(0x1000, bytes.Length));
        cpu.Fpu.RestoreState(PendingControl, PendingStatus, 0xFFFF, new Raw80[8]);
        return cpu;
    }

    private static byte Step(Cpu cpu)
    {
        byte observed = 0xFF;
        cpu.ProcessorException += (vector, _, _, _) => observed = vector;
        cpu.Step();
        return observed;
    }

    [Theory]
    [InlineData("DBE2", false)] // FNCLEX.
    [InlineData("DBE3", false)] // FNINIT.
    [InlineData("DFE0", true)] // FNSTSW AX.
    [InlineData("DD3E0030", true)] // FNSTSW [3000].
    [InlineData("D93E0030", true)] // FNSTCW [3000].
    [InlineData("D9360030", false)] // FNSTENV [3000].
    [InlineData("DD360030", false)] // FNSAVE [3000].
    public void EachNonwaitingEncodingCanHandleAnOldPendingException(string instruction, bool staysPending)
    {
        using var bus = new PhysicalMemoryBus();
        Cpu cpu = Prepare(bus, instruction);
        Assert.Equal(0xFF, Step(cpu));
        Assert.Equal(staysPending, cpu.Fpu.HasPendingException);
    }

    [Theory]
    [InlineData("D9D0")] // FNOP is waiting.
    [InlineData("D9E8")] // FLD1.
    [InlineData("D9E0")] // FCHS.
    [InlineData("DAC1")] // FCMOVB.
    [InlineData("DBF1")] // FCOMI.
    [InlineData("D92E0030")] // FLDCW.
    [InlineData("D9260030")] // FLDENV.
    [InlineData("DD260030")] // FRSTOR.
    [InlineData("0F77")] // EMMS.
    [InlineData("0FFCC1")] // PADDB.
    [InlineData("0F71F001")] // PSLLW, immediate form.
    [InlineData("660F2AC1")] // CVTPI2PD with an MMX source.
    [InlineData("0F2CC1")] // CVTTPS2PI.
    [InlineData("0F2DC1")] // CVTPS2PI.
    [InlineData("660F2CC1")] // CVTTPD2PI.
    [InlineData("660F2DC1")] // CVTPD2PI.
    [InlineData("F20FD6C1")] // MOVDQ2Q.
    [InlineData("F30FD6C1")] // MOVQ2DQ.
    public void WaitingInstructionsFaultBeforeChangingTheSavedEnvironment(string instruction)
    {
        using var bus = new PhysicalMemoryBus();
        Cpu cpu = Prepare(bus, instruction);
        cpu.State.Ds.Limit = 0x2FFF; // Memory forms would otherwise raise #GP.
        Assert.Equal(16, Step(cpu));
        Assert.Equal(PendingStatus, cpu.Fpu.StatusWord);
        Assert.Equal(PendingControl, cpu.Fpu.ControlWord);
        Assert.Equal(0xFFFF, cpu.Fpu.TagWord);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void EmDoesNotChangeFwaitAvailability(bool pending, bool monitor, bool switched)
    {
        using var bus = new PhysicalMemoryBus();
        Cpu cpu = Prepare(bus, "9B");
        if (!pending)
        {
            cpu.Fpu.Reset();
        }

        cpu.State.Cr0 |= CpuState.Cr0Emulation;
        if (monitor)
        {
            cpu.State.Cr0 |= CpuState.Cr0MonitorCoprocessor;
        }
        if (switched)
        {
            cpu.State.Cr0 |= CpuState.Cr0TaskSwitched;
        }

        byte expected = 0xFF;
        if (monitor && switched)
        {
            expected = 7;
        }
        else if (pending)
        {
            expected = 16;
        }

        Assert.Equal(expected, Step(cpu));
    }

    [Theory]
    [InlineData("DD360030")] // FNSAVE.
    [InlineData("D9360030")] // FNSTENV.
    public void AFailureLateInTheSaveDoesNotClearOrMaskPendingState(string instruction)
    {
        using var bus = new PhysicalMemoryBus();
        Cpu cpu = Prepare(bus, instruction);
        cpu.State.Ds.Limit = 0x300B; // Fail after the first environment fields were written.
        Assert.Equal(13, Step(cpu));
        Assert.Equal(PendingControl, cpu.Fpu.ControlWord);
        Assert.Equal(PendingStatus, cpu.Fpu.StatusWord);
        Assert.Equal(PendingControl, bus.ReadWord(0x3000));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrstorAndFxrstorReconstructPendingState(bool extended)
    {
        using var bus = new PhysicalMemoryBus();
        Cpu cpu = Prepare(bus, extended ? "0FAE0E0030" : "DD260030");
        if (!extended)
        {
            cpu.Fpu.Reset(); // FRSTOR itself waits; FXRSTOR can replace pending state.
        }

        bus.WriteWord(0x3000, PendingControl);
        bus.WriteWord(0x3002, 0x1801); // TOP and IE, no supplied summary bits.
        bus.WriteWord(0x3004, extended ? (ushort)0 : (ushort)0xFFFF);
        bus.WriteDword(0x3018, 0x1F80); // Valid MXCSR for FXRSTOR.
        Assert.Equal(0xFF, Step(cpu));
        Assert.Equal(PendingStatus, cpu.Fpu.StatusWord);
        Assert.Equal(PendingControl, cpu.Fpu.ControlWord);
    }

    [Fact]
    public void FxsavePreservesPendingStateInMemoryAndInTheUnit()
    {
        using var bus = new PhysicalMemoryBus();
        Cpu cpu = Prepare(bus, "0FAE060030");
        Assert.Equal(0xFF, Step(cpu));
        Assert.Equal(PendingStatus, cpu.Fpu.StatusWord);
        Assert.Equal(PendingStatus, bus.ReadWord(0x3002));
        Assert.Equal(PendingControl, bus.ReadWord(0x3000));
    }

    [Fact]
    public void AnAbsentMmxImmediatePageFaultsBeforeTheOldNumericException()
    {
        using var bus = new PhysicalMemoryBus();
        Cpu cpu = Prepare(bus, "90");
        bus.WriteDword(0x9000, 0xA003); // Directory entry, writable page table.
        bus.WriteDword(0xA000, 0x0003); // Interrupt vectors and stack.
        bus.WriteDword(0xA004, 0x1003); // Instruction page; the next page is absent.
        bus.WriteByte(0x1FFD, 0x0F);
        bus.WriteByte(0x1FFE, 0x71);
        bus.WriteByte(0x1FFF, 0xF0); // PSLLW MM0, imm8, with imm8 at 2000.
        cpu.State.Cr3 = 0x9000;
        cpu.State.Cr0 |= CpuState.Cr0Paging;
        cpu.State.Eip = 0x1FFD;
        cpu.State.Esp = 0x0800;

        Assert.Equal(14, Step(cpu));
        Assert.Equal(0x2000u, cpu.State.Cr2);
        Assert.Equal(PendingStatus, cpu.Fpu.StatusWord);
    }
}
