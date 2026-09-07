using Aurora386.Core;
using Aurora386.Core.Cpu;

namespace Aurora386.Tests;

/// <summary>
/// The instructions the 486 added to the 386's set.
///
/// Three of them exist for one reason: a system with more than one processor
/// needs to change a value in memory and learn what was there before in a
/// single indivisible step. On a 386 that meant a lock prefix around a
/// sequence and a certain amount of hope; from the 486 the hardware does the
/// whole operation. Every lock-free algorithm written since is built on
/// CMPXCHG, and every spinlock on XADD.
///
/// They are guarded by the emulated model, because executing one and catching
/// the fault is exactly how software works out what it is running on.
/// </summary>
public class Intel486InstructionTests
{
    /// <summary>A machine past its POST, with the given code at 0900:0000.</summary>
    private static Machine Running(params byte[] code)
    {
        var machine = new Machine();
        machine.RunInstructions(2000);

        for (int i = 0; i < code.Length; i++)
            machine.Memory.WriteByte(0x9000 + (uint)i, code[i]);

        machine.Cpu.State.Halted = false;
        machine.Cpu.SetSegment(SegReg.Cs, 0x0900);
        machine.Cpu.State.Eip = 0;
        machine.Cpu.SetSegment(SegReg.Ss, 0x0000);
        machine.Cpu.State.Esp = 0x7000;
        return machine;
    }

    [Fact]
    public void BswapTurnsAValueInsideOut()
    {
        // The instruction every network stack needs: a little-endian value
        // read from memory becomes the big-endian one the wire uses.
        using Machine machine = Running(0x66, 0x0F, 0xC8);   // bswap eax
        machine.Cpu.State.Eax = 0x11223344;

        machine.Cpu.Step();

        Assert.Equal(0x44332211u, machine.Cpu.State.Eax);
    }

    [Fact]
    public void BswapReachesEveryRegister()
    {
        // The register is in the low three bits of the opcode, not in a
        // ModRM byte, so getting it wrong swaps somebody else's register.
        using Machine machine = Running(0x66, 0x0F, 0xCB);   // bswap ebx
        machine.Cpu.State.Ebx = 0x0000FF00;
        machine.Cpu.State.Eax = 0xDEADBEEF;

        machine.Cpu.Step();

        Assert.Equal(0x00FF0000u, machine.Cpu.State.Ebx);
        Assert.Equal(0xDEADBEEFu, machine.Cpu.State.Eax);   // untouched
    }

    [Fact]
    public void XaddLeavesTheSumBehindAndHandsBackWhatWasThere()
    {
        // ebx gets the sum, eax gets ebx's old value: one instruction where a
        // read, an add and a write used to race with everybody else.
        using Machine machine = Running(0x66, 0x0F, 0xC1, 0xC3);   // xadd ebx, eax
        machine.Cpu.State.Ebx = 100;
        machine.Cpu.State.Eax = 5;

        machine.Cpu.Step();

        Assert.Equal(105u, machine.Cpu.State.Ebx);
        Assert.Equal(100u, machine.Cpu.State.Eax);
    }

    [Fact]
    public void XaddSetsTheFlagsFromTheAddition()
    {
        using Machine machine = Running(0x66, 0x0F, 0xC1, 0xC3);   // xadd ebx, eax
        machine.Cpu.State.Ebx = 0xFFFFFFFF;
        machine.Cpu.State.Eax = 1;

        machine.Cpu.Step();

        Assert.Equal(0u, machine.Cpu.State.Ebx);
        Assert.True(machine.Cpu.Flags.Carry);
        Assert.True(machine.Cpu.Flags.Zero);
    }

    [Fact]
    public void CmpxchgStoresOnlyWhenTheAccumulatorMatches()
    {
        // The winning case: the destination held what the caller expected, so
        // the new value goes in and the zero flag says the exchange happened.
        using Machine machine = Running(0x66, 0x0F, 0xB1, 0xCB);   // cmpxchg ebx, ecx
        machine.Cpu.State.Ebx = 42;
        machine.Cpu.State.Eax = 42;      // what we think is there
        machine.Cpu.State.Ecx = 99;      // what we want to put there

        machine.Cpu.Step();

        Assert.Equal(99u, machine.Cpu.State.Ebx);
        Assert.True(machine.Cpu.Flags.Zero);
    }

    [Fact]
    public void CmpxchgHandsBackTheActualValueWhenItLosesTheRace()
    {
        // The losing case, and the one that makes the instruction useful: the
        // destination is left alone and the accumulator is loaded with what
        // was really there, so the caller can retry without reading again.
        using Machine machine = Running(0x66, 0x0F, 0xB1, 0xCB);   // cmpxchg ebx, ecx
        machine.Cpu.State.Ebx = 7;
        machine.Cpu.State.Eax = 42;
        machine.Cpu.State.Ecx = 99;

        machine.Cpu.Step();

        Assert.Equal(7u, machine.Cpu.State.Ebx);     // unchanged
        Assert.Equal(7u, machine.Cpu.State.Eax);     // what was found
        Assert.False(machine.Cpu.Flags.Zero);
    }

    [Fact]
    public void TheByteFormsWorkOnTheirOwnRegisters()
    {
        using Machine machine = Running(0x0F, 0xB0, 0xCB);   // cmpxchg bl, cl
        machine.Cpu.State.SetReg8(3, 0x10);   // bl
        machine.Cpu.State.SetReg8(0, 0x10);   // al: matches
        machine.Cpu.State.SetReg8(1, 0x55);   // cl

        machine.Cpu.Step();

        Assert.Equal(0x55, machine.Cpu.State.GetReg8(3));
        Assert.True(machine.Cpu.Flags.Zero);
    }

    [Fact]
    public void OnA386TheyAreNotThereAtAll()
    {
        // This is how a program tells the processors apart, so the fault has
        // to be real rather than a quiet no-op.
        using Machine machine = Running(0x66, 0x0F, 0xC8);   // bswap eax
        machine.Cpu.State.Model = CpuModel.Intel386;

        byte faultVector = 0;
        machine.Cpu.ProcessorException += (vector, _, _, _) => faultVector = vector;

        machine.Cpu.Step();

        Assert.Equal(6, faultVector);        // invalid opcode
    }

    [Fact]
    public void OnA486TheyRunWithoutFaulting()
    {
        using Machine machine = Running(0x66, 0x0F, 0xC8);   // bswap eax
        machine.Cpu.State.Model = CpuModel.Intel486;
        machine.Cpu.State.Eax = 0x000000FF;

        bool faulted = false;
        machine.Cpu.ProcessorException += (_, _, _, _) => faulted = true;

        machine.Cpu.Step();

        Assert.False(faulted);
        Assert.Equal(0xFF000000u, machine.Cpu.State.Eax);
    }

    [Fact]
    public void CacheInvalidationIsPrivilegedAndOtherwiseDoesNothing()
    {
        // There is no cache to invalidate here, and on a 486 with caching
        // disabled these do nothing either. What must survive is the
        // privilege check: user code may not throw the caches away.
        using Machine machine = Running(0x0F, 0x09);   // wbinvd
        machine.Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable;
        machine.Cpu.State.Cpl = 3;

        // The first fault raised is what is being tested. Delivering it needs
        // an interrupt table this bare machine has not got, so it escalates to
        // a double fault straight afterwards; only the first one is ours.
        var raised = new List<byte>();
        machine.Cpu.ProcessorException += (vector, _, _, _) => raised.Add(vector);

        machine.Cpu.Step();

        Assert.Equal(13, raised[0]);        // general protection
    }
}

/// <summary>
/// CPUID, and the odd little dance software does to find out whether it is
/// there. Before it, a program identified the processor by its quirks -- which
/// flags a shift left alone, whether an instruction existed -- a collection of
/// tricks that got less reliable with every generation. From Windows 95
/// onwards everything simply asks.
/// </summary>
public class CpuIdentificationTests
{
    private static Machine Running(params byte[] code)
    {
        var machine = new Machine();
        machine.RunInstructions(2000);

        for (int i = 0; i < code.Length; i++)
            machine.Memory.WriteByte(0x9000 + (uint)i, code[i]);

        machine.Cpu.State.Halted = false;
        machine.Cpu.SetSegment(SegReg.Cs, 0x0900);
        machine.Cpu.State.Eip = 0;
        machine.Cpu.SetSegment(SegReg.Ss, 0x0000);
        machine.Cpu.State.Esp = 0x7000;
        return machine;
    }

    [Fact]
    public void FunctionZeroNamesTheMakerInThePeculiarRegisterOrder()
    {
        // EBX, EDX, ECX spells the twelve bytes in order. The sequence looks
        // arbitrary and is not: it is what the silicon does, and software
        // copies the registers into a buffer in exactly that order.
        using Machine machine = Running(0x0F, 0xA2);
        machine.Cpu.State.Eax = 0;

        machine.Cpu.Step();

        string vendor = System.Text.Encoding.ASCII.GetString(
        [
            .. BitConverter.GetBytes(machine.Cpu.State.Ebx),
            .. BitConverter.GetBytes(machine.Cpu.State.Edx),
            .. BitConverter.GetBytes(machine.Cpu.State.Ecx),
        ]);

        Assert.Equal("GenuineIntel", vendor);
        Assert.Equal(1u, machine.Cpu.State.Eax);      // highest function answered
    }

    [Fact]
    public void FunctionOneGivesTheFamilyAndSaysThereIsAFloatingPointUnit()
    {
        using Machine machine = Running(0x0F, 0xA2);
        machine.Cpu.State.Eax = 1;

        machine.Cpu.Step();

        Assert.Equal(4u, (machine.Cpu.State.Eax >> 8) & 0xF);    // family 4: a 486
        Assert.Equal(1u, machine.Cpu.State.Edx & 1);             // floating point on chip
    }

    [Fact]
    public void TheIdentificationBitMovesOnlyWhereCpuidExists()
    {
        // The whole detection method rests on this. A processor that let the
        // bit move without having CPUID would be asked a question it cannot
        // answer; one that refused to move it while having CPUID would never
        // be asked at all.
        using Machine machine = Running(0x90);
        machine.Cpu.State.SelectModel(CpuModel.Intel486);

        machine.Cpu.Flags.SetValue(machine.Cpu.Flags.Value | (1u << 21));
        Assert.True((machine.Cpu.Flags.Value & (1u << 21)) != 0);

        machine.Cpu.State.SelectModel(CpuModel.Intel386);
        machine.Cpu.Flags.SetValue(machine.Cpu.Flags.Value & ~(1u << 21));
        // On a 386 the bit is stuck wherever it was: what matters is that the
        // guest cannot change it, so a POPF must not move it either.
        uint before = machine.Cpu.Flags.Value;
        machine.Cpu.Step();
        Assert.Equal(before & (1u << 21), machine.Cpu.Flags.Value & (1u << 21));
    }

    [Fact]
    public void OnA386TheInstructionItselfIsNotThere()
    {
        using Machine machine = Running(0x0F, 0xA2);
        machine.Cpu.State.SelectModel(CpuModel.Intel386);

        var raised = new List<byte>();
        machine.Cpu.ProcessorException += (vector, _, _, _) => raised.Add(vector);

        machine.Cpu.Step();

        Assert.Equal(6, raised[0]);      // invalid opcode
    }
}

/// <summary>
/// The four bits the 486 added to CR0, and the exception one of them turns on.
///
/// None of them change what an instruction computes: they change what the
/// processor is willing to do behind the program's back. Write protection
/// stops the kernel from ignoring its own page permissions, the alignment
/// mask makes a misaligned access into an error rather than merely a slow
/// one, and the two cache bits configure hardware this emulator does not
/// have but firmware still insists on writing to.
///
/// They matter here because a program that cannot find them concludes it is
/// running on a 386, and from Windows 95 onwards that conclusion is fatal.
/// </summary>
public class Intel486ControlRegisterTests
{
    private static Machine Running(params byte[] code)
    {
        var machine = new Machine();
        machine.RunInstructions(2000);

        for (int i = 0; i < code.Length; i++)
            machine.Memory.WriteByte(0x9000 + (uint)i, code[i]);

        machine.Cpu.State.Halted = false;
        machine.Cpu.SetSegment(SegReg.Cs, 0x0900);
        machine.Cpu.State.Eip = 0;
        machine.Cpu.SetSegment(SegReg.Ss, 0x0000);
        machine.Cpu.State.Esp = 0x7000;
        return machine;
    }

    [Fact]
    public void TheLateBitsAreStorageOnA486()
    {
        // mov cr0, eax
        using Machine machine = Running(0x0F, 0x22, 0xC0);
        machine.Cpu.State.SelectModel(CpuModel.Intel486);
        machine.Cpu.State.Eax = CpuState.Cr0WriteProtect | CpuState.Cr0AlignmentMask
                              | CpuState.Cr0NotWriteThrough | CpuState.Cr0CacheDisable;

        machine.Cpu.Step();

        Assert.Equal(CpuState.Cr0WriteProtect, machine.Cpu.State.Cr0 & CpuState.Cr0WriteProtect);
        Assert.Equal(CpuState.Cr0AlignmentMask, machine.Cpu.State.Cr0 & CpuState.Cr0AlignmentMask);
        Assert.Equal(CpuState.Cr0NotWriteThrough, machine.Cpu.State.Cr0 & CpuState.Cr0NotWriteThrough);
        Assert.Equal(CpuState.Cr0CacheDisable, machine.Cpu.State.Cr0 & CpuState.Cr0CacheDisable);
    }

    [Fact]
    public void OnA386TheSameBitsRefuseToStick()
    {
        // This is the detection method that predates CPUID, and it has to
        // fail on a 386 for the right reason: the bits are not there, so
        // they read back as zero however hard the write tried.
        using Machine machine = Running(0x0F, 0x22, 0xC0);
        machine.Cpu.State.SelectModel(CpuModel.Intel386);
        machine.Cpu.State.Eax = CpuState.Cr0AlignmentMask | CpuState.Cr0WriteProtect;

        machine.Cpu.Step();

        Assert.Equal(0u, machine.Cpu.State.Cr0 & CpuState.Cr0AlignmentMask);
        Assert.Equal(0u, machine.Cpu.State.Cr0 & CpuState.Cr0WriteProtect);
    }

    [Fact]
    public void TheCoprocessorBitCannotBeClearedOnA486()
    {
        // A 486DX carries its floating point unit on the die. Software may
        // write a zero there, but it is claiming the chip does not contain
        // part of itself, and the processor does not agree.
        using Machine machine = Running(0x0F, 0x22, 0xC0);
        machine.Cpu.State.SelectModel(CpuModel.Intel486);
        machine.Cpu.State.Eax = 0;

        machine.Cpu.Step();

        Assert.Equal(CpuState.Cr0ExtensionType,
                     machine.Cpu.State.Cr0 & CpuState.Cr0ExtensionType);
    }

    [Fact]
    public void ResetLeavesTheProcessorSignatureInEdx()
    {
        // Before any instruction has run there is no CPUID to ask, so the
        // processor answers in advance: EDX holds family, model and stepping
        // laid out exactly as CPUID function 1 will later return them.
        var state = new CpuState { Model = CpuModel.Intel486 };
        state.Reset();
        Assert.Equal(4u, (state.Edx >> 8) & 0xF);

        var older = new CpuState { Model = CpuModel.Intel386 };
        older.Reset();
        Assert.Equal(3u, (older.Edx >> 8) & 0xF);
    }
}

/// <summary>
/// Exception 17, the alignment check.
///
/// A word at an odd address costs the processor a second memory cycle, and
/// this is the mechanism that tells a program where it is paying that price.
/// It is deliberately hard to switch on: the operating system must set
/// CR0.AM, the program must set EFLAGS.AC, and the code must be running at
/// ring 3. All three, or nothing happens -- which is why almost no software
/// of the period ever saw it fire, and why the kernel is free to keep doing
/// the misaligned accesses that systems code is full of.
/// </summary>
public class AlignmentCheckTests
{
    /// <summary>
    /// A machine in protected mode at ring 3, with a flat data segment and a
    /// stack, ready to execute the given code. Alignment checking is the only
    /// exception here that cares about the current ring, so the test has to
    /// actually get down to ring 3 rather than assert about a field.
    /// </summary>
    private static Machine AtRingThree(params byte[] code)
    {
        var machine = new Machine();
        machine.RunInstructions(2000);
        machine.Cpu.State.Halted = false;

        for (int i = 0; i < code.Length; i++)
            machine.Memory.WriteByte(0x9000 + (uint)i, code[i]);

        machine.Cpu.SetSegment(SegReg.Cs, 0x0900);
        machine.Cpu.State.Eip = 0;
        machine.Cpu.SetSegment(SegReg.Ds, 0x0000);
        machine.Cpu.SetSegment(SegReg.Ss, 0x0000);
        machine.Cpu.State.Esp = 0x7000;
        return machine;
    }

    private static List<byte> Watch(Machine machine)
    {
        var raised = new List<byte>();
        machine.Cpu.ProcessorException += (vector, _, _, _) => raised.Add(vector);
        return raised;
    }

    [Fact]
    public void AMisalignedWordIsFineWhenNobodyAskedToBeTold()
    {
        // The ordinary case, and the one that matters for speed: with the
        // mask clear the check is not even looked at.
        using Machine machine = AtRingThree(0x8B, 0x1E, 0x01, 0x50);   // mov bx, [5001]
        machine.Memory.WriteWord(0x5001, 0xBEEF);
        List<byte> raised = Watch(machine);

        machine.Cpu.Step();

        Assert.Empty(raised);
        Assert.Equal(0xBEEF, machine.Cpu.State.Bx);
    }

    [Fact]
    public void TheMaskAloneIsNotEnough()
    {
        // CR0.AM says the check is permitted, not that it is wanted: without
        // the program's own EFLAGS.AC nothing fires. An operating system can
        // therefore turn the mask on globally without breaking every program
        // that was written before the 486 existed.
        using Machine machine = AtRingThree(0x8B, 0x1E, 0x01, 0x50);
        machine.Cpu.State.Cr0 |= CpuState.Cr0AlignmentMask;
        List<byte> raised = Watch(machine);

        machine.Cpu.Step();

        Assert.Empty(raised);
    }

    [Fact]
    public void SupervisorCodeIsExempt()
    {
        // Both switches are on, but ring 0 is not subject to the check: a
        // great deal of correct kernel code does unaligned accesses on
        // purpose, and the 486 does not break it.
        using Machine machine = AtRingThree(0x8B, 0x1E, 0x01, 0x50);
        machine.Cpu.State.Cr0 |= CpuState.Cr0AlignmentMask;
        machine.Cpu.Flags.SetValue(machine.Cpu.Flags.Value | (1u << 18));
        List<byte> raised = Watch(machine);

        machine.Cpu.Step();

        Assert.Empty(raised);
    }

    [Fact]
    public void AnAlignedAccessNeverFaultsHoweverItIsConfigured()
    {
        using Machine machine = AtRingThree(0x8B, 0x1E, 0x00, 0x50);   // mov bx, [5000]
        machine.Memory.WriteWord(0x5000, 0x1234);
        machine.Cpu.State.Cr0 |= CpuState.Cr0AlignmentMask;
        machine.Cpu.Flags.SetValue(machine.Cpu.Flags.Value | (1u << 18));
        List<byte> raised = Watch(machine);

        machine.Cpu.Step();

        Assert.Empty(raised);
        Assert.Equal(0x1234, machine.Cpu.State.Bx);
    }

    [Fact]
    public void ADwordWantsFourByteAlignmentNotTwo()
    {
        // The rule is by operand size, not a blanket "even address": a dword
        // at an even but not multiple-of-four address is still misaligned,
        // and this is the case that catches a naive implementation.
        using Machine machine = AtRingThree(0x66, 0x8B, 0x1E, 0x02, 0x50);  // mov ebx, [5002]
        machine.Cpu.State.Cr0 |= CpuState.Cr0AlignmentMask;
        machine.Cpu.Flags.SetValue(machine.Cpu.Flags.Value | (1u << 18));
        machine.Cpu.State.Virtual8086Mode = true;   // counts as ring 3 for this check
        List<byte> raised = Watch(machine);

        machine.Cpu.Step();

        Assert.Equal(17, raised[0]);
    }

    [Fact]
    public void WithEverythingSetTheMisalignedAccessFaults()
    {
        using Machine machine = AtRingThree(0x8B, 0x1E, 0x01, 0x50);
        machine.Cpu.State.Cr0 |= CpuState.Cr0AlignmentMask;
        machine.Cpu.Flags.SetValue(machine.Cpu.Flags.Value | (1u << 18));
        machine.Cpu.State.Virtual8086Mode = true;
        List<byte> raised = Watch(machine);

        machine.Cpu.Step();

        Assert.Equal(17, raised[0]);
    }
}
