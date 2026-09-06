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
