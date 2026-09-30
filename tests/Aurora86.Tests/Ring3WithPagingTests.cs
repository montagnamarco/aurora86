using Aurora86.Core;
using Aurora86.Core.Cpu;

namespace Aurora86.Tests;

/// <summary>
/// Entering ring 3 while paging is enabled.
///
/// Why this file exists separately from ProtectedModeTests: every ring 3 test
/// there runs with paging OFF. That is a real gap rather than an oversight of
/// coverage, because the two mechanisms interact -- the stack switch on a
/// fault from ring 3 reads the task state segment through the page tables,
/// and the IRET that enters ring 3 fetches its frame through them too.
///
/// The gap was found by a guest: Aurora OS enters ring 3 with paging on,
/// which is what every real operating system does, and Aurora86 triple
/// faulted where QEMU ran the same image correctly. The reproducer is in
/// plans/aurora-os-ring3.md.
/// </summary>
public class Ring3WithPagingTests
{
    private const uint GdtBase = 0x00020000;
    private const uint IdtBase = 0x00021000;
    private const uint TssBase = 0x00022000;

    private const uint DirectoryBase = 0x00030000;
    private const uint TableBase = 0x00031000;

    private const uint KernelStackTop = 0x00008000;

    private static void WriteDescriptor(Machine machine, uint tableBase, int index,
                                        uint baseAddress, uint limit, byte access, byte flags)
    {
        uint entry = tableBase + (uint)index * 8;

        machine.Memory.WriteWord(entry, (ushort)(limit & 0xFFFF));
        machine.Memory.WriteWord(entry + 2, (ushort)(baseAddress & 0xFFFF));
        machine.Memory.WriteByte(entry + 4, (byte)((baseAddress >> 16) & 0xFF));
        machine.Memory.WriteByte(entry + 5, access);
        machine.Memory.WriteByte(entry + 6, (byte)(((limit >> 16) & 0x0F) | (flags & 0xF0)));
        machine.Memory.WriteByte(entry + 7, (byte)((baseAddress >> 24) & 0xFF));
    }

    /// <summary>
    /// An identity mapping of the first four megabytes, which is exactly what
    /// a kernel builds before it turns paging on for the first time.
    ///
    /// Every page is marked user-accessible here. A real kernel would not do
    /// that, but this test is about the privilege transition rather than
    /// about page permissions, and marking everything reachable removes a
    /// second variable from a test that is already about an interaction.
    /// </summary>
    private static void BuildIdentityMapping(Machine machine)
    {
        for (uint i = 0; i < 1024; i++)
        {
            machine.Memory.WriteDword(TableBase + i * 4, (i * 0x1000u) | 0x07u);
        }

        for (uint i = 0; i < 1024; i++)
        {
            machine.Memory.WriteDword(DirectoryBase + i * 4, 0);
        }

        machine.Memory.WriteDword(DirectoryBase, TableBase | 0x07u);
    }

    private static Machine BuildPagedRing3Machine()
    {
        var machine = new Machine();
        machine.RunInstructions(200);

        // Flat kernel code and data, then the task state segment and the
        // ring 3 pair, in the same numbering Aurora OS uses.
        WriteDescriptor(machine, GdtBase, 1, 0, 0xFFFFF, 0x9A, 0xC0);
        WriteDescriptor(machine, GdtBase, 2, 0, 0xFFFFF, 0x92, 0xC0);
        WriteDescriptor(machine, GdtBase, 3, 0, 0xFFFFF, 0xFA, 0xC0);   // ring 3 code
        WriteDescriptor(machine, GdtBase, 4, 0, 0xFFFFF, 0xF2, 0xC0);   // ring 3 data
        WriteDescriptor(machine, GdtBase, 5, TssBase, 0x67, 0x89, 0x00);

        machine.Cpu.State.GdtBase = GdtBase;
        machine.Cpu.State.GdtLimit = 0x2F;
        machine.Cpu.State.IdtBase = IdtBase;
        machine.Cpu.State.IdtLimit = 0x7FF;

        machine.Memory.WriteDword(TssBase + 4, KernelStackTop);
        machine.Memory.WriteDword(TssBase + 8, 0x10);

        machine.Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable;
        machine.Cpu.State.TaskRegister = 0x28;

        BuildIdentityMapping(machine);
        machine.Cpu.State.Cr3 = DirectoryBase;
        machine.Cpu.State.Cr0 |= 0x80000000u;      // paging enable

        machine.Cpu.State.Halted = false;

        return machine;
    }

    /// <summary>
    /// The guest's exact conditions, which differ from the simple case above
    /// in one way that turned out to matter: the pages the user program needs
    /// are marked user-accessible, and the ones it does NOT need are not.
    ///
    /// A kernel identity-maps its own memory without the user bit, precisely
    /// so ring 3 cannot reach it, and marks only the program's code and stack
    /// reachable. That is the realistic arrangement and it is what Aurora OS
    /// builds; the test above marks everything reachable, which hides any
    /// defect in how the user bit is consulted during the transition.
    /// </summary>
    [Fact]
    public void IretEntersRingThreeWhenOnlyTheProgramsPagesAreUserAccessible()
    {
        using Machine machine = BuildPagedRing3Machine();

        // Kernel-only everywhere, as a real kernel maps its own memory.
        for (uint i = 0; i < 1024; i++)
        {
            machine.Memory.WriteDword(TableBase + i * 4, (i * 0x1000u) | 0x03u);
        }

        // ...except the program's code page and its stack page.
        machine.Memory.WriteDword(TableBase + 2 * 4, 0x00002000u | 0x07u);
        machine.Memory.WriteDword(TableBase + 3 * 4, 0x00003000u | 0x07u);

        machine.Cpu.SetSegment(SegReg.Cs, 0x08);
        machine.Cpu.SetSegment(SegReg.Ss, 0x10);
        machine.Cpu.State.Cpl = 0;
        machine.Cpu.State.Esp = 0x7000;
        machine.Cpu.Flags.SetValue(0x0002);

        machine.Memory.WriteDword(0x7000, 0x00002000);
        machine.Memory.WriteDword(0x7004, 0x1B);
        machine.Memory.WriteDword(0x7008, 0x0202);
        machine.Memory.WriteDword(0x700C, 0x00003FF0);
        machine.Memory.WriteDword(0x7010, 0x23);

        machine.Memory.WriteByte(0x1000, 0xCF);
        machine.Cpu.State.Eip = 0x1000;

        machine.Cpu.Step();

        Assert.Equal(3, (int)machine.Cpu.State.Cpl);
        Assert.Equal((ushort)0x1B, machine.Cpu.State.Cs.Selector);
        Assert.Equal(0x00002000u, machine.Cpu.State.Eip);
    }

    /// <summary>
    /// And then the program actually executes, which the tests above stop
    /// short of. A transition that sets the registers correctly and then
    /// faults on the first instruction fetch is still a broken transition,
    /// and only running an instruction shows the difference.
    /// </summary>
    [Fact]
    public void TheProgramExecutesAfterTheTransition()
    {
        using Machine machine = BuildPagedRing3Machine();

        for (uint i = 0; i < 1024; i++)
        {
            machine.Memory.WriteDword(TableBase + i * 4, (i * 0x1000u) | 0x03u);
        }

        machine.Memory.WriteDword(TableBase + 2 * 4, 0x00002000u | 0x07u);
        machine.Memory.WriteDword(TableBase + 3 * 4, 0x00003000u | 0x07u);

        machine.Cpu.SetSegment(SegReg.Cs, 0x08);
        machine.Cpu.SetSegment(SegReg.Ss, 0x10);
        machine.Cpu.State.Cpl = 0;
        machine.Cpu.State.Esp = 0x7000;
        machine.Cpu.Flags.SetValue(0x0002);

        machine.Memory.WriteDword(0x7000, 0x00002000);
        machine.Memory.WriteDword(0x7004, 0x1B);
        machine.Memory.WriteDword(0x7008, 0x0202);
        machine.Memory.WriteDword(0x700C, 0x00003FF0);
        machine.Memory.WriteDword(0x7010, 0x23);

        machine.Memory.WriteByte(0x1000, 0xCF);

        // The program: increment EAX twice, so running it is observable.
        machine.Memory.WriteByte(0x2000, 0x40);   // inc eax
        machine.Memory.WriteByte(0x2001, 0x40);   // inc eax

        machine.Cpu.State.Eip = 0x1000;
        machine.Cpu.State.Eax = 0;

        machine.Cpu.Step();     // the IRET
        machine.Cpu.Step();     // the first user instruction
        machine.Cpu.Step();     // the second

        Assert.Equal(3, (int)machine.Cpu.State.Cpl);
        Assert.Equal(2u, machine.Cpu.State.Eax);
        Assert.Equal(0x00002002u, machine.Cpu.State.Eip);
    }

    /// <summary>
    /// A software interrupt from ring 3, with realistic page permissions.
    ///
    /// This is how a program makes a system call, and it exercises the other
    /// half of the same mechanism: the processor reads the gate out of the
    /// IDT and the kernel stack out of the task state segment, both of which
    /// live in kernel-only pages while ring 3 is running.
    /// </summary>
    [Fact]
    public void ASystemCallFromRingThreeReachesItsGate()
    {
        using Machine machine = BuildPagedRing3Machine();

        // Kernel-only everywhere but the program's two pages, as a real
        // kernel maps memory. The IDT at 0x21000 and the TSS at 0x22000 are
        // therefore unreachable from ring 3, which is the point.
        for (uint i = 0; i < 1024; i++)
        {
            machine.Memory.WriteDword(TableBase + i * 4, (i * 0x1000u) | 0x03u);
        }

        machine.Memory.WriteDword(TableBase + 2 * 4, 0x00002000u | 0x07u);
        machine.Memory.WriteDword(TableBase + 3 * 4, 0x00003000u | 0x07u);

        // A gate ring 3 may invoke, which is what a system call gate is.
        WriteGate(machine, IdtBase, 0x80, 0x08, 0x4000, type: 0x0E, dpl: 3);

        machine.Cpu.SetSegment(SegReg.Cs, 0x1B);
        machine.Cpu.SetSegment(SegReg.Ss, 0x23);
        machine.Cpu.State.Cpl = 3;
        machine.Cpu.State.Esp = 0x3FF0;
        machine.Cpu.Flags.SetValue(0x0202);

        machine.Memory.WriteByte(0x2000, 0xCD);   // int 0x80
        machine.Memory.WriteByte(0x2001, 0x80);
        machine.Cpu.State.Eip = 0x2000;

        machine.Cpu.Step();

        // The handler is running in ring 0, on the kernel stack from the TSS.
        Assert.Equal(0x4000u, machine.Cpu.State.Eip);
        Assert.Equal(0, (int)machine.Cpu.State.Cpl);
        Assert.Equal((ushort)0x10, machine.Cpu.State.Ss.Selector);
        Assert.True(machine.Cpu.State.Esp <= KernelStackTop);

        // And the frame records where it came from: ring 3.
        uint savedCs = machine.Memory.ReadDword(machine.Cpu.State.Esp + 4);
        Assert.Equal(0x1Bu, savedCs);
    }

    [Fact]
    public void IretEntersRingThreeWithPagingEnabled()
    {
        // The sequence every operating system uses to start a user program,
        // performed with paging on, which is the state a real kernel is in.
        using Machine machine = BuildPagedRing3Machine();

        machine.Cpu.SetSegment(SegReg.Cs, 0x08);
        machine.Cpu.SetSegment(SegReg.Ss, 0x10);
        machine.Cpu.State.Cpl = 0;
        machine.Cpu.State.Esp = 0x7000;
        machine.Cpu.Flags.SetValue(0x0002);

        machine.Memory.WriteDword(0x7000, 0x00002000);   // EIP
        machine.Memory.WriteDword(0x7004, 0x1B);         // CS, ring 3
        machine.Memory.WriteDword(0x7008, 0x0202);       // EFLAGS with IF
        machine.Memory.WriteDword(0x700C, 0x00003000);   // ESP
        machine.Memory.WriteDword(0x7010, 0x23);         // SS, ring 3

        machine.Memory.WriteByte(0x1000, 0xCF);          // IRET
        machine.Cpu.State.Eip = 0x1000;

        machine.Cpu.Step();

        Assert.Equal(3, (int)machine.Cpu.State.Cpl);
        Assert.Equal((ushort)0x1B, machine.Cpu.State.Cs.Selector);
        Assert.Equal((ushort)0x23, machine.Cpu.State.Ss.Selector);
        Assert.Equal(0x00002000u, machine.Cpu.State.Eip);
        Assert.Equal(0x00003000u, machine.Cpu.State.Esp);
        Assert.True(machine.Cpu.Flags.Interrupt);
    }

    [Fact]
    public void APrivilegedInstructionInRingThreeFaultsRatherThanExecuting()
    {
        // The whole point of ring 3: a user program that tries to disable
        // interrupts must be refused. If this executed, a user program could
        // keep the processor for ever by switching off preemption.
        using Machine machine = BuildPagedRing3Machine();

        // A general protection gate, so the fault has somewhere to go. Without
        // one the fault becomes a double and then a triple fault, and the
        // machine resets -- which is exactly what the guest saw.
        WriteGate(machine, IdtBase, 13, 0x08, 0x5000, type: 0x0E, dpl: 0);

        machine.Cpu.SetSegment(SegReg.Cs, 0x1B);
        machine.Cpu.SetSegment(SegReg.Ss, 0x23);
        machine.Cpu.State.Cpl = 3;
        machine.Cpu.State.Esp = 0x3000;
        machine.Cpu.Flags.SetValue(0x0202);

        machine.Memory.WriteByte(0x2000, 0xFA);          // CLI
        machine.Cpu.State.Eip = 0x2000;

        machine.Cpu.Step();

        // The handler is running, on the kernel's stack, at ring 0.
        Assert.Equal(0x5000u, machine.Cpu.State.Eip);
        Assert.Equal(0, (int)machine.Cpu.State.Cpl);
        Assert.Equal((ushort)0x10, machine.Cpu.State.Ss.Selector);

        // A fault from ring 3 pushes five words: SS, ESP, EFLAGS, CS, EIP,
        // with EIP lowest. So the saved CS is at ESP+4 and must name the ring
        // 3 code segment -- which is what says the fault genuinely came from
        // user mode rather than from the kernel.
        //
        // MIND that CLI has no error code, so nothing sits below EIP. The
        // exceptions that do push one shift every offset here by four, which
        // is why an offset like this is worth stating rather than assuming.
        // A general protection fault ALWAYS pushes an error code, even when
        // it is zero, so the frame is six words rather than five and every
        // offset above EIP shifts by four. Zero here means the fault was not
        // caused by a particular segment, which is right for CLI: nothing
        // about the instruction names a selector.
        //
        // Getting this wrong is not academic. An interrupt stub that assumes
        // five words reads the error code as EIP and EIP as CS, and then
        // reports a fault at a believable but entirely fictional address.
        uint errorCode = machine.Memory.ReadDword(machine.Cpu.State.Esp);
        uint savedEip = machine.Memory.ReadDword(machine.Cpu.State.Esp + 4);
        uint savedCs = machine.Memory.ReadDword(machine.Cpu.State.Esp + 8);
        uint savedFlags = machine.Memory.ReadDword(machine.Cpu.State.Esp + 12);
        uint savedEsp = machine.Memory.ReadDword(machine.Cpu.State.Esp + 16);
        uint savedSs = machine.Memory.ReadDword(machine.Cpu.State.Esp + 20);

        Assert.Equal(0u, errorCode);
        Assert.Equal(0x2000u, savedEip);

        // The saved CS names the ring 3 code segment, which is what says the
        // fault genuinely came from user mode rather than from the kernel.
        Assert.Equal(0x1Bu, savedCs);

        // And the user's own stack was recorded, because the fault crossed a
        // privilege boundary. Those last two words are present only in that
        // case, which is the asymmetry a handler has to know about.
        Assert.Equal(0x3000u, savedEsp);
        Assert.Equal(0x23u, savedSs);

        // The interrupt flag the user tried to clear is still set in the
        // saved image: CLI faulted rather than taking effect.
        Assert.True((savedFlags & 0x200u) != 0,
                    "the user's CLI must not have cleared the saved interrupt flag");
    }

    [Fact]
    public void AFaultFromRingThreeSwitchesToTheStackInTheTaskStateSegment()
    {
        // The stack switch reads the task state segment, and with paging on it
        // reads it through the page tables. That is the interaction this file
        // exists to cover.
        using Machine machine = BuildPagedRing3Machine();
        WriteGate(machine, IdtBase, 13, 0x08, 0x5000, type: 0x0E, dpl: 0);

        machine.Cpu.SetSegment(SegReg.Cs, 0x1B);
        machine.Cpu.SetSegment(SegReg.Ss, 0x23);
        machine.Cpu.State.Cpl = 3;
        machine.Cpu.State.Esp = 0x3000;

        machine.Memory.WriteByte(0x2000, 0xFA);          // CLI
        machine.Cpu.State.Eip = 0x2000;

        machine.Cpu.Step();

        Assert.True(machine.Cpu.State.Esp <= KernelStackTop,
                    "the handler must run on the kernel stack from the TSS");
        Assert.True(machine.Cpu.State.Esp > KernelStackTop - 0x100);
    }

    private static void WriteGate(Machine machine, uint idtBase, int vector,
                                  ushort selector, uint offset, byte type, byte dpl)
    {
        uint entry = idtBase + (uint)vector * 8;

        machine.Memory.WriteWord(entry, (ushort)(offset & 0xFFFF));
        machine.Memory.WriteWord(entry + 2, selector);
        machine.Memory.WriteByte(entry + 4, 0);
        machine.Memory.WriteByte(entry + 5, (byte)(0x80 | ((dpl & 3) << 5) | type));
        machine.Memory.WriteWord(entry + 6, (ushort)((offset >> 16) & 0xFFFF));
    }
}
