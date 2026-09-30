using Aurora86.Core;
using Aurora86.Core.Cpu;
using Aurora86.Core.Memory;

namespace Aurora86.Tests;

/// <summary>
/// Deferred x87 exceptions as the integer processor observes them.  A 486
/// with CR0.NE delivers an unmasked x87 flag at the next waiting boundary,
/// leaving the environment for the handler to inspect and repair.
/// </summary>
public class X87PendingExceptionTests
{
    private const uint Code = 0x1000;
    private const uint Data = 0x3000;
    private const ushort UnmaskedInvalidControlWord = 0x037E;
    private const ushort MaskedControlWord = 0x037F;
    private const ushort PendingInvalidStatus = 0x8081;
    private const uint ProtectedGdt = 0x5000;
    private const uint ProtectedIdt = 0x6000;

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

    private static byte StepAndReturnVector(Cpu cpu)
    {
        byte vector = 0xFF;
        cpu.ProcessorException += (number, _, _, _) => vector = number;
        cpu.Step();
        return vector;
    }

    private static void WriteEnvironment(PhysicalMemoryBus bus, ushort control, ushort status)
    {
        bus.WriteWord(Data, control);
        bus.WriteWord(Data + 2, status);
        bus.WriteWord(Data + 4, 0xFFFF);
    }

    private static void LoadEnvironment(Cpu cpu, PhysicalMemoryBus bus, ushort control, ushort status)
    {
        WriteEnvironment(bus, control, status);
        cpu.State.Eip = Code;
        cpu.Step();                         // FLDENV [3000]
    }

    private static void WriteDescriptor(Machine machine, int index, byte access)
    {
        uint address = ProtectedGdt + (uint)(index * 8);
        machine.Memory.WriteWord(address, 0xFFFF);
        machine.Memory.WriteWord(address + 2, 0);
        machine.Memory.WriteByte(address + 4, 0);
        machine.Memory.WriteByte(address + 5, access);
        machine.Memory.WriteByte(address + 6, 0xCF);
        machine.Memory.WriteByte(address + 7, 0);
    }

    private static void WriteInterruptGate(Machine machine, byte vector, uint offset)
    {
        uint address = ProtectedIdt + (uint)(vector * 8);
        machine.Memory.WriteWord(address, (ushort)offset);
        machine.Memory.WriteWord(address + 2, 8);
        machine.Memory.WriteByte(address + 4, 0);
        machine.Memory.WriteByte(address + 5, 0x8E);
        machine.Memory.WriteWord(address + 6, (ushort)(offset >> 16));
    }

    /// <summary>
    /// FLDENV, FRSTOR and FXRSTOR must recreate ES and B from the six sticky
    /// flags and FCW masks.  The image's old ES/B bits are not authoritative.
    /// </summary>
    [Theory]
    [InlineData(MaskedControlWord, 0x0001, 0x0001)]
    [InlineData(UnmaskedInvalidControlWord, 0x0001, PendingInvalidStatus)]
    [InlineData(MaskedControlWord, 0x8081, 0x0001)]
    [InlineData(UnmaskedInvalidControlWord, 0x8000, 0x0000)]
    [InlineData(UnmaskedInvalidControlWord, 0x0040, 0x0040)]
    public void FldenvReconcilesThePendingSummaryFromControlAndStickyFlags(
        ushort control,
        ushort inputStatus,
        ushort expectedStatus)
    {
        (Cpu cpu, PhysicalMemoryBus bus) = Setup(0xD9, 0x26, 0x00, 0x30); // FLDENV [3000]

        LoadEnvironment(cpu, bus, control, inputStatus);

        Assert.Equal(expectedStatus, cpu.Fpu.StatusWord);
    }

    /// <summary>
    /// Any one of the six x87 sticky flags has the same relationship to its
    /// FCW mask.  SF alone is deliberately excluded from that calculation.
    /// </summary>
    [Theory]
    [InlineData(0x0001)]
    [InlineData(0x0002)]
    [InlineData(0x0004)]
    [InlineData(0x0008)]
    [InlineData(0x0010)]
    [InlineData(0x0020)]
    public void EveryUnmaskedExceptionFlagSetsBothSummaryBits(ushort flag)
    {
        (Cpu cpu, PhysicalMemoryBus bus) = Setup(0xD9, 0x26, 0x00, 0x30); // FLDENV [3000]

        LoadEnvironment(cpu, bus, (ushort)(MaskedControlWord & ~flag), flag);

        Assert.Equal((ushort)(flag | 0x8080), cpu.Fpu.StatusWord);
    }

    /// <summary>
    /// FLDCW can create a deferred exception.  The integer instruction after
    /// it still runs; FWAIT is where vector 16 is delivered and where EIP is
    /// reported, with the x87 status unchanged for a retry.
    /// </summary>
    [Fact]
    public void FldcwCreatesPendingStateAndFwaitFaultsAtItsOwnEip()
    {
        (Cpu cpu, PhysicalMemoryBus bus) = Setup(
            0xD9, 0x26, 0x00, 0x30,             // FLDENV [3000], masked IE
            0xD9, 0x2E, 0x20, 0x30,             // FLDCW [3020], unmask IE
            0xB8, 0x34, 0x12,                   // MOV AX,1234
            0x9B);                              // FWAIT
        WriteEnvironment(bus, MaskedControlWord, 0x0001);
        bus.WriteWord(Data + 0x20, UnmaskedInvalidControlWord);

        cpu.Step();
        cpu.Step();
        cpu.Step();
        uint fwaitEip = cpu.State.Eip;

        Assert.Equal(PendingInvalidStatus, cpu.Fpu.StatusWord);
        Assert.Equal(0x1234, cpu.State.Ax);

        byte vector = 0xFF;
        uint faultEip = 0;
        cpu.ProcessorException += (number, _, instructionEip, _) =>
        {
            vector = number;
            faultEip = instructionEip;
        };
        cpu.Step();

        Assert.Equal(16, vector);
        Assert.Equal(PendingInvalidStatus, cpu.Fpu.StatusWord);
        Assert.Equal(fwaitEip, faultEip);
    }

    /// <summary>
    /// FNCLEX is deliberately a no-wait escape: it can clear the condition
    /// which would otherwise stop FWAIT, and clears SF as well as ES and B.
    /// </summary>
    [Fact]
    public void FnclexClearsPendingStateBeforeTheFollowingFwait()
    {
        (Cpu cpu, PhysicalMemoryBus bus) = Setup(
            0xD9, 0x26, 0x00, 0x30,             // FLDENV [3000]
            0xDB, 0xE2,                         // FNCLEX
            0x9B);                              // FWAIT
        WriteEnvironment(bus, UnmaskedInvalidControlWord, 0x80C1);

        cpu.Step();
        Assert.Equal(0x80C1, cpu.Fpu.StatusWord);

        Assert.Equal(0xFF, StepAndReturnVector(cpu));
        Assert.Equal(0, cpu.Fpu.StatusWord & 0x80FF);
        Assert.Equal(0xFF, StepAndReturnVector(cpu));
    }

    /// <summary>
    /// The FN spelling bypasses only deferred #MF.  CR0.EM and CR0.TS still
    /// make every x87 escape unavailable, including FNCLEX.
    /// </summary>
    [Theory]
    [InlineData(CpuState.Cr0Emulation)]
    [InlineData(CpuState.Cr0TaskSwitched)]
    public void NonwaitingX87EscapesStillRaiseDeviceNotAvailable(uint unavailableBit)
    {
        (Cpu cpu, _) = Setup(0xDB, 0xE2); // FNCLEX
        cpu.State.Cr0 |= unavailableBit;

        Assert.Equal(7, StepAndReturnVector(cpu));
    }

    /// <summary>
    /// FNSTENV records the pending image first, then masks the live FCW so
    /// that an exception handler can execute waiting x87 instructions.
    /// </summary>
    [Fact]
    public void FnstenvSavesPendingStateThenMasksTheLiveControlWord()
    {
        (Cpu cpu, PhysicalMemoryBus bus) = Setup(
            0xD9, 0x26, 0x00, 0x30,             // FLDENV [3000]
            0xD9, 0x36, 0x40, 0x30,             // FNSTENV [3040]
            0x9B);                              // FWAIT
        WriteEnvironment(bus, UnmaskedInvalidControlWord, 0x0001);

        cpu.Step();
        Assert.Equal(PendingInvalidStatus, cpu.Fpu.StatusWord);

        Assert.Equal(0xFF, StepAndReturnVector(cpu));
        Assert.Equal(UnmaskedInvalidControlWord, bus.ReadWord(Data + 0x40));
        Assert.Equal(PendingInvalidStatus, bus.ReadWord(Data + 0x42));
        Assert.Equal(MaskedControlWord, cpu.Fpu.ControlWord);
        Assert.Equal(0xFF, StepAndReturnVector(cpu));
    }

    /// <summary>
    /// The live control word changes only after FNSTENV has completed every
    /// write.  A segment failure leaves the handler's pending condition alone.
    /// </summary>
    [Fact]
    public void FailedFnstenvDoesNotMaskTheLivePendingState()
    {
        (Cpu cpu, PhysicalMemoryBus bus) = Setup(
            0xD9, 0x26, 0x00, 0x30,             // FLDENV [3000]
            0xD9, 0x36, 0x00, 0x30);            // FNSTENV [3000]
        WriteEnvironment(bus, UnmaskedInvalidControlWord, 0x0001);

        cpu.Step();
        cpu.State.Ds.Limit = Data - 1;

        Assert.Equal(13, StepAndReturnVector(cpu));
        Assert.Equal(UnmaskedInvalidControlWord, cpu.Fpu.ControlWord);
        Assert.Equal(PendingInvalidStatus, cpu.Fpu.StatusWord);
    }

    /// <summary>
    /// FWAIT's task-switch check precedes deferred #MF.  Its MP/TS rule is
    /// independent of EM, and a pending exception is relevant only with NE.
    /// </summary>
    [Theory]
    [InlineData(false, false, false, false, 0xFF)]
    [InlineData(false, false, true, false, 0xFF)]
    [InlineData(false, true, false, false, 0xFF)]
    [InlineData(false, true, true, false, 7)]
    [InlineData(true, false, false, false, 16)]
    [InlineData(true, false, true, false, 16)]
    [InlineData(true, true, false, false, 16)]
    [InlineData(true, true, true, false, 7)]
    [InlineData(true, false, false, true, 0xFF)]
    public void FwaitUsesMpAndTsBeforePendingNumericError(
        bool pending,
        bool monitorCoprocessor,
        bool taskSwitched,
        bool numericErrorClear,
        byte expectedVector)
    {
        (Cpu cpu, PhysicalMemoryBus bus) = Setup(
            0xD9, 0x26, 0x00, 0x30,             // FLDENV [3000]
            0x9B);                              // FWAIT

        if (pending)
        {
            WriteEnvironment(bus, UnmaskedInvalidControlWord, 0x0001);
            cpu.Step();
        }

        uint control = CpuState.Cr0ExtensionType;
        if (!numericErrorClear)
        {
            control |= CpuState.Cr0NumericError;
        }
        if (monitorCoprocessor)
        {
            control |= CpuState.Cr0MonitorCoprocessor;
        }
        if (taskSwitched)
        {
            control |= CpuState.Cr0TaskSwitched;
        }
        cpu.State.Cr0 = control;
        if (!pending)
        {
            cpu.State.Eip = Code + 4;
        }

        Assert.Equal(expectedVector, StepAndReturnVector(cpu));
    }

    /// <summary>
    /// A protected-mode #MF is a fault, not an interrupt: the 32-bit gate
    /// receives the prefixed FWAIT's EIP in its frame and no error-code word.
    /// </summary>
    [Fact]
    public void ProtectedModeFwaitBuildsAnErrorCodeFreeFrameAtThePrefixedInstruction()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);
        machine.Cpu.State.Halted = false;
        machine.Cpu.State.SelectModel(CpuModel.Core2);
        WriteDescriptor(machine, 1, 0x9A);
        WriteDescriptor(machine, 2, 0x92);
        WriteInterruptGate(machine, 16, 0x7000);

        Cpu cpu = machine.Cpu;
        cpu.State.GdtBase = ProtectedGdt;
        cpu.State.GdtLimit = 0x17;
        cpu.State.IdtBase = ProtectedIdt;
        cpu.State.IdtLimit = 0x7FF;
        cpu.State.Cr0 = CpuState.Cr0ProtectionEnable | CpuState.Cr0ExtensionType | CpuState.Cr0NumericError;
        cpu.SetSegment(SegReg.Cs, 8);
        cpu.SetSegment(SegReg.Ds, 16);
        cpu.SetSegment(SegReg.Ss, 16);
        cpu.State.Esp = 0x9000;
        cpu.State.Eip = 0x3000;
        machine.Memory.WriteWord(0x4000, UnmaskedInvalidControlWord);
        machine.Memory.WriteDword(0x4004, 0x0001); // 32-bit environment status slot.
        machine.Memory.WriteDword(0x4008, 0xFFFF); // 32-bit environment tag slot.
        machine.Memory.WriteByte(0x3000, 0x67); // address-size prefix: FLDENV [4000]
        machine.Memory.WriteByte(0x3001, 0xD9);
        machine.Memory.WriteByte(0x3002, 0x26);
        machine.Memory.WriteWord(0x3003, 0x4000);
        machine.Memory.WriteByte(0x3005, 0x66); // operand-size prefix, then FWAIT
        machine.Memory.WriteByte(0x3006, 0x9B);

        cpu.Step();
        cpu.Step();

        Assert.Equal(0x7000u, cpu.State.Eip);
        Assert.Equal(0x8FF4u, cpu.State.Esp);
        Assert.Equal(0x3005u, machine.Memory.ReadDword(cpu.State.Esp));
        Assert.Equal(8u, machine.Memory.ReadDword(cpu.State.Esp + 4));
    }

    /// <summary>
    /// MMX is a waiting boundary because it aliases the x87 file.  An ordinary
    /// XMM move is not: it continues without destroying the pending x87 image.
    /// </summary>
    [Fact]
    public void MmxWaitsForPendingX87ButPlainXmmDoesNot()
    {
        (Cpu cpu, PhysicalMemoryBus bus) = Setup(
            0xD9, 0x26, 0x00, 0x30,             // FLDENV [3000]
            0x0F, 0x6F, 0xC0,                   // MOVQ MM0,MM0
            0x0F, 0x28, 0xC0);                  // MOVAPS XMM0,XMM0
        WriteEnvironment(bus, UnmaskedInvalidControlWord, 0x0001);
        cpu.State.Cr4 |= CpuState.Cr4OsFxsr;

        cpu.Step();
        Assert.Equal(16, StepAndReturnVector(cpu));
        Assert.Equal(PendingInvalidStatus, cpu.Fpu.StatusWord);

        cpu.State.Eip = Code + 7;
        Assert.Equal(0xFF, StepAndReturnVector(cpu));
        Assert.Equal(PendingInvalidStatus, cpu.Fpu.StatusWord);
    }

    /// <summary>
    /// The conversion tables distinguish their memory forms: CVTPI2PS still
    /// synchronises with x87, while CVTPI2PD m64 is explicitly exempt.  Both
    /// use the same pending image and valid readable operand.
    /// </summary>
    [Fact]
    public void MixedMmxConversionsUseTheirOwnMemoryPendingRules()
    {
        (Cpu psCpu, PhysicalMemoryBus psBus) = Setup(
            0xD9, 0x26, 0x00, 0x30,             // FLDENV [3000]
            0x0F, 0x2A, 0x06, 0x40, 0x30);      // CVTPI2PS XMM0,[3040]
        WriteEnvironment(psBus, UnmaskedInvalidControlWord, 0x0001);
        psBus.WriteDword(Data + 0x40, 1);
        psBus.WriteDword(Data + 0x44, 2);
        psCpu.State.Cr4 |= CpuState.Cr4OsFxsr;

        psCpu.Step();
        Assert.Equal(16, StepAndReturnVector(psCpu));

        (Cpu pdCpu, PhysicalMemoryBus pdBus) = Setup(
            0xD9, 0x26, 0x00, 0x30,             // FLDENV [3000]
            0x66, 0x0F, 0x2A, 0x06, 0x40, 0x30); // CVTPI2PD XMM0,[3040]
        WriteEnvironment(pdBus, UnmaskedInvalidControlWord, 0x0001);
        pdBus.WriteDword(Data + 0x40, 1);
        pdBus.WriteDword(Data + 0x44, 2);
        pdCpu.State.Cr4 |= CpuState.Cr4OsFxsr;

        pdCpu.Step();
        Assert.Equal(0xFF, StepAndReturnVector(pdCpu));
        Assert.Equal(PendingInvalidStatus, pdCpu.Fpu.StatusWord);
    }
}
