using Aurora386.Core;
using Aurora386.Core.Cpu;

namespace Aurora386.Tests;

/// <summary>
/// Hardware task switching.
///
/// The 386 can hold an entire execution context in memory and swap one for
/// another as a single operation. Almost no operating system used it for
/// multitasking -- swapping a hundred bytes through memory is slower than
/// swapping the dozen registers software actually needs -- but it survives
/// as the only workable way to handle a double fault, because the incoming
/// task arrives with its own stack and its own page directory rather than
/// being pushed onto whatever was broken. Windows in enhanced mode puts
/// vector 8 behind a task gate for exactly that reason.
///
/// The tests build the descriptors by hand. A TSS is a structure whose fields
/// are at fixed offsets that differ between the 286 and 386 layouts, and a
/// field read from the wrong offset is the classic bug here: it produces a
/// task that resumes with a stack pointer made of two unrelated halves, and
/// it hides very well behind a helper.
/// </summary>
public class TaskSwitchTests
{
    private const uint GdtBase = 0x1000;
    private const uint IdtBase = 0x2000;

    // The two task state segments live well clear of the tables and of the
    // code, so that a stray write shows up as a wrong value rather than as
    // silent corruption of something else.
    private const uint TssA = 0x3000;
    private const uint TssB = 0x4000;

    private static void WriteDescriptor(Machine machine, int index,
                                        uint baseAddress, uint limit,
                                        byte access, byte granularity = 0x40)
    {
        uint address = GdtBase + (uint)(index * 8);

        machine.Memory.WriteWord(address, (ushort)(limit & 0xFFFF));
        machine.Memory.WriteWord(address + 2, (ushort)(baseAddress & 0xFFFF));
        machine.Memory.WriteByte(address + 4, (byte)((baseAddress >> 16) & 0xFF));
        machine.Memory.WriteByte(address + 5, access);
        machine.Memory.WriteByte(address + 6,
            (byte)((granularity & 0xF0) | ((limit >> 16) & 0x0F)));
        machine.Memory.WriteByte(address + 7, (byte)((baseAddress >> 24) & 0xFF));
    }

    /// <summary>
    /// A task gate: it holds no address of its own, only the selector of the
    /// task state segment to switch to. That indirection is the point --
    /// an IDT entry can name a task without being one.
    /// </summary>
    private static void WriteTaskGate(Machine machine, uint tableBase, int index,
                                      ushort taskSelector, byte dpl = 0)
    {
        uint address = tableBase + (uint)(index * 8);
        machine.Memory.WriteWord(address, 0);
        machine.Memory.WriteWord(address + 2, taskSelector);
        machine.Memory.WriteByte(address + 4, 0);
        machine.Memory.WriteByte(address + 5, (byte)(0x80 | (dpl << 5) | 0x05));
        machine.Memory.WriteWord(address + 6, 0);
    }

    /// <summary>
    /// Fills in a 386 task state segment: where the task starts, what it
    /// starts with, and which selectors it runs under.
    /// </summary>
    private static void WriteTaskState(Machine machine, uint tssBase,
                                       uint eip, uint esp, ushort cs, ushort ss,
                                       uint eax = 0, uint ldtr = 0)
    {
        for (uint i = 0; i < 0x68; i += 4) machine.Memory.WriteDword(tssBase + i, 0);

        machine.Memory.WriteDword(tssBase + 0x20, eip);
        machine.Memory.WriteDword(tssBase + 0x24, 0x0002);       // the reserved bit, nothing else
        machine.Memory.WriteDword(tssBase + 0x28, eax);          // EAX
        machine.Memory.WriteDword(tssBase + 0x38, esp);          // ESP
        machine.Memory.WriteWord(tssBase + 0x48, 0x10);          // ES
        machine.Memory.WriteWord(tssBase + 0x4C, cs);
        machine.Memory.WriteWord(tssBase + 0x50, ss);
        machine.Memory.WriteWord(tssBase + 0x54, 0x10);          // DS
        machine.Memory.WriteWord(tssBase + 0x60, (ushort)ldtr);
    }

    /// <summary>
    /// A machine in protected mode with a flat code segment at 8, a flat data
    /// segment at 16, and two task state segments at selectors 0x18 and 0x20.
    /// Task A is the one running; task B is the one to switch into.
    /// </summary>
    private static Machine BuildMachineWithTwoTasks()
    {
        var machine = new Machine();
        machine.RunInstructions(200);

        WriteDescriptor(machine, 1, 0, 0xFFFFF, 0x9A, 0xC0);      // code
        WriteDescriptor(machine, 2, 0, 0xFFFFF, 0x92, 0xC0);      // data

        // 0x89 is a present, available 386 TSS. The busy form, 0x8B, is what
        // the processor writes back once the task is entered.
        WriteDescriptor(machine, 3, TssA, 0x67, 0x89, 0x00);
        WriteDescriptor(machine, 4, TssB, 0x67, 0x89, 0x00);

        machine.Cpu.State.GdtBase = GdtBase;
        machine.Cpu.State.GdtLimit = 0x2F;
        machine.Cpu.State.IdtBase = IdtBase;
        machine.Cpu.State.IdtLimit = 0x7FF;
        machine.Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable;
        machine.Cpu.State.Halted = false;

        machine.Cpu.SetSegment(SegReg.Cs, 0x08);
        machine.Cpu.SetSegment(SegReg.Ds, 0x10);
        machine.Cpu.SetSegment(SegReg.Ss, 0x10);
        machine.Cpu.State.Esp = 0x8000;

        // Task A is the running one, so the processor has to know which TSS
        // to save into. LTR is what an operating system executes once, at
        // startup, for precisely this reason -- and executing the real
        // instruction here also proves the descriptor is well formed enough
        // for it to accept.
        machine.Memory.WriteByte(0x0600, 0x0F);      // ltr ax
        machine.Memory.WriteByte(0x0601, 0x00);
        machine.Memory.WriteByte(0x0602, 0xD8);
        machine.Cpu.State.Eip = 0x0600;
        machine.Cpu.State.Eax = 0x18;
        machine.Cpu.Step();

        return machine;
    }

    /// <summary>Assembles a far JMP to a selector, offset ignored.</summary>
    private static void PlaceFarJump(Machine machine, uint at, ushort selector)
    {
        machine.Memory.WriteByte(at, 0xEA);
        machine.Memory.WriteDword(at + 1, 0);
        machine.Memory.WriteWord(at + 5, selector);
    }

    private static void PlaceFarCall(Machine machine, uint at, ushort selector)
    {
        machine.Memory.WriteByte(at, 0x9A);
        machine.Memory.WriteDword(at + 1, 0);
        machine.Memory.WriteWord(at + 5, selector);
    }

    [Fact]
    public void AJumpToATaskLoadsThatTasksContext()
    {
        using Machine machine = BuildMachineWithTwoTasks();

        // Task B starts at 0x5000 with EAX already holding something, which
        // is how the test tells "the context was loaded" from "the jump went
        // to the right address and nothing else moved".
        WriteTaskState(machine, TssB, eip: 0x5000, esp: 0x9000,
                       cs: 0x08, ss: 0x10, eax: 0xCAFEBABE);

        PlaceFarJump(machine, 0x0700, 0x20);
        machine.Cpu.State.Eip = 0x0700;

        machine.Cpu.Step();

        Assert.Equal(0x5000u, machine.Cpu.State.Eip);
        Assert.Equal(0xCAFEBABEu, machine.Cpu.State.Eax);
        Assert.Equal(0x9000u, machine.Cpu.State.Esp);
        Assert.Equal(0x20, machine.Cpu.State.TaskRegister);
    }

    [Fact]
    public void TheOutgoingTaskIsSavedWhereItCanBeResumed()
    {
        using Machine machine = BuildMachineWithTwoTasks();
        WriteTaskState(machine, TssB, 0x5000, 0x9000, 0x08, 0x10);

        PlaceFarJump(machine, 0x0700, 0x20);
        machine.Cpu.State.Eip = 0x0700;
        machine.Cpu.State.Eax = 0x11223344;

        machine.Cpu.Step();

        // EIP is saved as it stood after the jump was decoded -- the
        // instruction *after* the one that switched away. A task resumed
        // later must carry on past its own jump rather than execute it again
        // for ever.
        Assert.Equal(0x0707u, machine.Memory.ReadDword(TssA + 0x20));
        Assert.Equal(0x11223344u, machine.Memory.ReadDword(TssA + 0x28));
        Assert.Equal(0x08, machine.Memory.ReadWord(TssA + 0x4C));
    }

    [Fact]
    public void AJumpMarksTheIncomingTaskBusyAndReleasesTheOutgoingOne()
    {
        using Machine machine = BuildMachineWithTwoTasks();
        WriteTaskState(machine, TssB, 0x5000, 0x9000, 0x08, 0x10);

        PlaceFarJump(machine, 0x0700, 0x20);
        machine.Cpu.State.Eip = 0x0700;

        machine.Cpu.Step();

        // The busy bit is the processor's own re-entrancy guard, and it lives
        // in the descriptor table rather than in any register: another task
        // examining the table has to be able to see it.
        Assert.Equal(0x89, machine.Memory.ReadByte(GdtBase + 3 * 8 + 5));   // A: available
        Assert.Equal(0x8B, machine.Memory.ReadByte(GdtBase + 4 * 8 + 5));   // B: busy
    }

    [Fact]
    public void ATaskThatIsAlreadyRunningCannotBeEnteredAgain()
    {
        using Machine machine = BuildMachineWithTwoTasks();
        WriteTaskState(machine, TssB, 0x5000, 0x9000, 0x08, 0x10);

        // Mark B busy by hand: it stands for a task suspended partway
        // through, whose TSS still holds the state of that activation.
        // Entering it again would overwrite exactly that.
        machine.Memory.WriteByte(GdtBase + 4 * 8 + 5, 0x8B);

        PlaceFarJump(machine, 0x0700, 0x20);
        machine.Cpu.State.Eip = 0x0700;

        var raised = new List<byte>();
        machine.Cpu.ProcessorException += (vector, _, _, _) => raised.Add(vector);

        machine.Cpu.Step();

        Assert.Equal(13, raised[0]);
    }

    [Fact]
    public void ACallToATaskKeepsTheWayBack()
    {
        using Machine machine = BuildMachineWithTwoTasks();
        WriteTaskState(machine, TssB, 0x5000, 0x9000, 0x08, 0x10);

        PlaceFarCall(machine, 0x0700, 0x20);
        machine.Cpu.State.Eip = 0x0700;

        machine.Cpu.Step();

        // Two things record the way back, and both are needed: the link field
        // says which task, and NT in the flags is what makes the new task's
        // IRET follow it rather than pop a frame nobody pushed.
        Assert.Equal(0x18, machine.Memory.ReadWord(TssB + 0));
        Assert.True((machine.Cpu.Flags.Value & (uint)Flags.NestedTask) != 0);

        // The calling task stays busy: it is still on the chain, suspended
        // rather than finished.
        Assert.Equal(0x8B, machine.Memory.ReadByte(GdtBase + 3 * 8 + 5));
    }

    [Fact]
    public void IretWithNestedTaskReturnsToTheCallingTask()
    {
        using Machine machine = BuildMachineWithTwoTasks();

        // Task B's first instruction is an IRET, so the call and the return
        // are a single round trip: into B, straight back out.
        machine.Memory.WriteByte(0x5000, 0xCF);
        WriteTaskState(machine, TssB, 0x5000, 0x9000, 0x08, 0x10);

        PlaceFarCall(machine, 0x0700, 0x20);
        machine.Cpu.State.Eip = 0x0700;
        machine.Cpu.State.Eax = 0x55667788;

        machine.Cpu.Step();                 // call: into task B
        Assert.Equal(0x5000u, machine.Cpu.State.Eip);

        machine.Cpu.Step();                 // iret: back into task A

        // Back where the call left off, with A's registers restored from A's
        // own TSS rather than whatever B happened to leave behind.
        Assert.Equal(0x18, machine.Cpu.State.TaskRegister);
        Assert.Equal(0x0707u, machine.Cpu.State.Eip);
        Assert.Equal(0x55667788u, machine.Cpu.State.Eax);

        // And the nested flag is gone: the link it named has been followed,
        // and a task resumed later with NT still set would follow it again.
        Assert.False((machine.Cpu.Flags.Value & (uint)Flags.NestedTask) != 0);
    }

    [Fact]
    public void AnExceptionThroughATaskGateSwitchesTasks()
    {
        using Machine machine = BuildMachineWithTwoTasks();
        WriteTaskState(machine, TssB, 0x5000, 0x9000, 0x08, 0x10);

        // Vector 13 behind a task gate. This is the arrangement that matters
        // in practice: a fault handler that does not have to be reachable
        // through the faulting task's own stack.
        WriteTaskGate(machine, IdtBase, 13, 0x20);

        // A far jump to a selector past the end of the table: a #GP with an
        // error code, which the handler task should find on its own stack.
        PlaceFarJump(machine, 0x0700, 0x80);
        machine.Cpu.State.Eip = 0x0700;

        machine.Cpu.Step();

        Assert.Equal(0x5000u, machine.Cpu.State.Eip);
        Assert.Equal(0x20, machine.Cpu.State.TaskRegister);

        // The error code went onto the *handler's* stack -- which is the
        // whole point, since the faulting task's stack may be exactly what
        // was wrong.
        Assert.Equal(0x8FFCu, machine.Cpu.State.Esp);
        Assert.Equal(0x80u, machine.Memory.ReadDword(0x8FFC));
    }

    [Fact]
    public void ATssShorterThanItsOwnFieldsIsRejected()
    {
        using Machine machine = BuildMachineWithTwoTasks();
        WriteTaskState(machine, TssB, 0x5000, 0x9000, 0x08, 0x10);

        // A 386 TSS needs 0x67 bytes. A limit below that means the operating
        // system built the structure wrong, and reading past it would produce
        // a task assembled out of whatever followed in memory.
        WriteDescriptor(machine, 4, TssB, 0x40, 0x89, 0x00);

        PlaceFarJump(machine, 0x0700, 0x20);
        machine.Cpu.State.Eip = 0x0700;

        var raised = new List<byte>();
        machine.Cpu.ProcessorException += (vector, _, _, _) => raised.Add(vector);

        machine.Cpu.Step();

        Assert.Equal(10, raised[0]);        // #TS: invalid task state segment
    }

    [Fact]
    public void SwitchingTasksSetsTheCoprocessorFlag()
    {
        using Machine machine = BuildMachineWithTwoTasks();
        WriteTaskState(machine, TssB, 0x5000, 0x9000, 0x08, 0x10);

        PlaceFarJump(machine, 0x0700, 0x20);
        machine.Cpu.State.Eip = 0x0700;

        machine.Cpu.Step();

        // TS says the floating point registers belong to somebody else now.
        // The next FPU instruction traps, and the handler saves the old
        // task's registers only if there were any -- which is how a system
        // avoids saving eighty bytes of state for the many tasks that never
        // touch the coprocessor.
        Assert.Equal(CpuState.Cr0TaskSwitched,
                     machine.Cpu.State.Cr0 & CpuState.Cr0TaskSwitched);
    }

    [Fact]
    public void ATaskCarriesItsOwnLocalDescriptorTable()
    {
        using Machine machine = BuildMachineWithTwoTasks();

        // An LDT of one entry at selector 0x28, and task B configured to use
        // it. This is what makes tasks genuinely separate: two tasks can hold
        // the same selector and reach different memory through it.
        WriteDescriptor(machine, 5, 0x6000, 0x0F, 0x82, 0x00);   // type 2: LDT
        machine.Cpu.State.GdtLimit = 0x37;
        WriteTaskState(machine, TssB, 0x5000, 0x9000, 0x08, 0x10, ldtr: 0x28);

        PlaceFarJump(machine, 0x0700, 0x20);
        machine.Cpu.State.Eip = 0x0700;

        machine.Cpu.Step();

        Assert.Equal(0x28, machine.Cpu.State.Ldtr);
        Assert.Equal(0x6000u, machine.Cpu.State.LdtBase);
    }
}

/// <summary>
/// What entering a handler through a gate does to the nested-task flag.
///
/// NT is what makes an IRET return to another task instead of popping a
/// return frame: it follows the link field in the current TSS. A handler
/// entered through an interrupt gate has no such link -- it was reached by a
/// frame on a stack, and that frame is exactly what its IRET must pop.
///
/// So the processor clears NT on the way in, and the reason is not tidiness.
/// Windows 3.11 in enhanced mode leaves NT set in its own monitor; with the
/// bit surviving into the handler, closing a program produced a #GP on the
/// monitor's IRET, then on the handler's IRET, then again, until the machine
/// gave up and fell out of protected mode into the BIOS and spun there for
/// ever. One bit, fourteen faults, a frozen desktop.
/// </summary>
public class NestedTaskFlagTests
{
    private const uint GdtBase = 0x1000;
    private const uint IdtBase = 0x2000;

    private static void WriteDescriptor(Machine machine, int index,
                                        uint baseAddress, uint limit,
                                        byte access, byte granularity)
    {
        uint address = GdtBase + (uint)(index * 8);
        machine.Memory.WriteWord(address, (ushort)(limit & 0xFFFF));
        machine.Memory.WriteWord(address + 2, (ushort)(baseAddress & 0xFFFF));
        machine.Memory.WriteByte(address + 4, (byte)((baseAddress >> 16) & 0xFF));
        machine.Memory.WriteByte(address + 5, access);
        machine.Memory.WriteByte(address + 6,
            (byte)((granularity & 0xF0) | ((limit >> 16) & 0x0F)));
        machine.Memory.WriteByte(address + 7, (byte)((baseAddress >> 24) & 0xFF));
    }

    private static Machine Build()
    {
        var machine = new Machine();
        machine.RunInstructions(200);

        WriteDescriptor(machine, 1, 0, 0xFFFFF, 0x9A, 0xC0);
        WriteDescriptor(machine, 2, 0, 0xFFFFF, 0x92, 0xC0);

        machine.Cpu.State.GdtBase = GdtBase;
        machine.Cpu.State.GdtLimit = 0x17;
        machine.Cpu.State.IdtBase = IdtBase;
        machine.Cpu.State.IdtLimit = 0x7FF;
        machine.Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable;
        machine.Cpu.State.Halted = false;

        machine.Cpu.SetSegment(SegReg.Cs, 0x08);
        machine.Cpu.SetSegment(SegReg.Ds, 0x10);
        machine.Cpu.SetSegment(SegReg.Ss, 0x10);
        machine.Cpu.State.Esp = 0x8000;

        // An interrupt gate on vector 0x30, pointing at 0x5000.
        uint gate = IdtBase + 0x30 * 8;
        machine.Memory.WriteWord(gate, 0x5000);
        machine.Memory.WriteWord(gate + 2, 0x08);
        machine.Memory.WriteByte(gate + 4, 0);
        machine.Memory.WriteByte(gate + 5, 0x8E);
        machine.Memory.WriteWord(gate + 6, 0);

        return machine;
    }

    [Fact]
    public void AGateClearsTheNestedTaskFlagOnTheWayIn()
    {
        using Machine machine = Build();

        // NT set on the way in, as Windows' monitor leaves it.
        machine.Cpu.Flags.SetValue(machine.Cpu.Flags.Value | (uint)Flags.NestedTask);

        machine.Memory.WriteByte(0x0700, 0xCD);      // int 0x30
        machine.Memory.WriteByte(0x0701, 0x30);
        machine.Cpu.State.Eip = 0x0700;

        machine.Cpu.Step();

        Assert.Equal(0x5000u, machine.Cpu.State.Eip);
        Assert.Equal(0u, machine.Cpu.Flags.Value & (uint)Flags.NestedTask);
    }

    [Fact]
    public void TheHandlersIretPopsItsFrameRatherThanChasingALink()
    {
        // The whole consequence of the bit being cleared: the IRET at the end
        // of the handler takes the ordinary path back. With NT still set it
        // would look for a task to return to, find the link field never
        // written, and fault -- inside the fault handler.
        using Machine machine = Build();
        machine.Cpu.Flags.SetValue(machine.Cpu.Flags.Value | (uint)Flags.NestedTask);

        machine.Memory.WriteByte(0x0700, 0xCD);
        machine.Memory.WriteByte(0x0701, 0x30);
        machine.Memory.WriteByte(0x5000, 0xCF);      // iret
        machine.Cpu.State.Eip = 0x0700;

        var raised = new List<byte>();
        machine.Cpu.ProcessorException += (vector, _, _, _) => raised.Add(vector);

        machine.Cpu.Step();     // into the handler
        machine.Cpu.Step();     // and back out

        Assert.Empty(raised);
        Assert.Equal(0x0702u, machine.Cpu.State.Eip);
    }

    [Fact]
    public void TheFlagIsRestoredFromTheStackOnTheWayBack()
    {
        // Clearing NT on entry does not lose it: it went onto the stack with
        // the rest of EFLAGS, and the IRET puts it back. The interrupted code
        // is entitled to find its own flags as it left them.
        using Machine machine = Build();
        machine.Cpu.Flags.SetValue(machine.Cpu.Flags.Value | (uint)Flags.NestedTask);

        machine.Memory.WriteByte(0x0700, 0xCD);
        machine.Memory.WriteByte(0x0701, 0x30);
        machine.Memory.WriteByte(0x5000, 0xCF);
        machine.Cpu.State.Eip = 0x0700;

        machine.Cpu.Step();
        machine.Cpu.Step();

        Assert.Equal((uint)Flags.NestedTask,
                     machine.Cpu.Flags.Value & (uint)Flags.NestedTask);
    }
}
