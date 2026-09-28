using Aurora386.Core;
using Aurora386.Core.Cpu;

namespace Aurora386.Tests;

/// <summary>
/// Virtual 8086 mode while paging is enabled and the kernel's own memory is
/// mapped the way a kernel maps it: supervisor-only.
///
/// WHY THIS FILE EXISTS SEPARATELY from Ring3WithPagingTests. That file covers
/// ordinary ring 3, where the privilege is a stored number, and it found the
/// first half of this defect class. Virtual 8086 mode is the other half, and
/// it differs in a way that defeated the first fix: there ring 3 is a
/// CONSEQUENCE OF THE MODE rather than a stored number, so State.Cpl is a
/// computed property whose getter returns 3 without consulting the field its
/// setter writes.
///
/// TranslateSupervisor used to borrow ring 0 by assigning State.Cpl = 0. That
/// assignment lands in the backing field and the very next read still returns
/// 3, so the borrow was a NO-OP in exactly the mode that needs it -- a V86
/// task takes hardware interrupts like anything else, and every delivery reads
/// a gate out of the IDT. On a guest that maps the IDT supervisor-only the
/// read faulted, the page fault's own delivery faulted identically, and the
/// machine reset before the task executed one instruction.
///
/// The guest that found it was Aurora OS running Monkey Island 2: it entered
/// virtual 8086 mode correctly at 101F:2688 and reset delivering vector 0x90
/// with CR2 naming IDT entry 0x90. The reproducer and the measurement are in
/// the guest's own docs/DIFETTI-AURORA386.md.
///
/// These tests measure the ARRANGEMENT that triggers it -- the user bit absent
/// from the page holding the IDT -- because that bit is the whole difference
/// between a guest that works and one that cannot run a single instruction.
/// </summary>
public class Virtual8086WithPagingTests
{
    private const uint GdtBase = 0x00020000;
    private const uint IdtBase = 0x00021000;
    private const uint TssBase = 0x00022000;

    private const uint DirectoryBase = 0x00030000;
    private const uint TableBase = 0x00031000;

    private const uint KernelStackTop = 0x00008000;

    /// <summary>Where the kernel's IRET into virtual 8086 mode sits.</summary>
    private const uint KernelEntryPoint = 0x00001000;

    /// <summary>
    /// Where the virtual 8086 task's code and stack live.
    ///
    /// CHOSEN NOT TO COLLIDE with anything else this file uses, and the point
    /// is worth a comment because the first version of this test did collide:
    /// paragraph 0x100 is linear 0x1000, which is where the kernel's IRET
    /// sits, so writing the task's first instruction overwrote the very IRET
    /// that was supposed to start it -- and marking the task's page reachable
    /// from ring 3 quietly made the kernel's page reachable too, which is the
    /// exact arrangement this file exists to rule out.
    /// </summary>
    private const ushort TaskCodeSegment = 0x0A00;   // linear 0xA000
    private const ushort TaskStackSegment = 0x0B00;  // linear 0xB000

    /// <summary>
    /// The handler the interrupt gates point at. Anything reachable will do:
    /// these tests ask whether the gate could be READ, not what it ran.
    /// </summary>
    private const uint HandlerOffset = 0x00004000;

    /// <summary>
    /// Bit 17 of EFLAGS, the one that selects virtual 8086 mode. Named rather
    /// than written as a number because every frame below carries it and a
    /// frame without it is an ordinary ring 3 return that would pass a test
    /// measuring only where execution ended up.
    /// </summary>
    private const uint VirtualModeFlag = 0x00020000;

    /// <summary>Bit 2 of a page table entry: the page is reachable from ring 3.</summary>
    private const uint PageUser = 0x04;

    private const uint PagePresent = 0x01;
    private const uint PageWritable = 0x02;

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
    /// A 32-bit interrupt gate at ring 0, which is how a kernel installs every
    /// vector it does not deliberately open to user code.
    /// </summary>
    private static void WriteInterruptGate(Machine machine, byte vector, ushort selector, uint offset)
    {
        uint entry = IdtBase + (uint)vector * 8;

        machine.Memory.WriteWord(entry, (ushort)(offset & 0xFFFF));
        machine.Memory.WriteWord(entry + 2, selector);
        machine.Memory.WriteByte(entry + 4, 0);
        machine.Memory.WriteByte(entry + 5, 0x8E);              // present, ring 0, 32-bit interrupt gate
        machine.Memory.WriteWord(entry + 6, (ushort)(offset >> 16));
    }

    /// <summary>
    /// Identity-maps the first four megabytes the way a kernel does: every
    /// page present and writable, and NOT ONE of them user-accessible.
    ///
    /// That last part is the entire point of this file. A test that marks
    /// everything user-accessible -- as the first ring 3 tests did -- passes
    /// whether or not the processor's own table reads are treated as
    /// supervisor, because the distinction never bites.
    /// </summary>
    private static void BuildSupervisorOnlyMapping(Machine machine)
    {
        for (uint i = 0; i < 1024; i++)
        {
            machine.Memory.WriteDword(TableBase + i * 4,
                                      (i * 0x1000u) | PagePresent | PageWritable);
        }

        for (uint i = 0; i < 1024; i++)
        {
            machine.Memory.WriteDword(DirectoryBase + i * 4, 0);
        }

        // The directory entry must be at least as permissive as the pages
        // below it, because the processor takes the stricter of the two.
        machine.Memory.WriteDword(DirectoryBase,
                                  TableBase | PagePresent | PageWritable | PageUser);

        // The task's own code and stack pages ARE reachable from ring 3, as a
        // kernel would mark the memory it hands a program. Nothing else is.
        MarkUserAccessible(machine, TaskCodeSegment * 16u);
        MarkUserAccessible(machine, TaskStackSegment * 16u);
    }

    private static void MarkUserAccessible(Machine machine, uint linear)
    {
        uint index = (linear >> 12) & 0x3FF;
        uint entry = machine.Memory.ReadDword(TableBase + index * 4);

        machine.Memory.WriteDword(TableBase + index * 4, entry | PageUser);
    }

    private static uint PageEntryFor(Machine machine, uint linear)
    {
        uint index = (linear >> 12) & 0x3FF;
        return machine.Memory.ReadDword(TableBase + index * 4);
    }

    /// <summary>
    /// A machine in protected mode with paging on, a task state segment
    /// naming a ring 0 stack, and a frame on the kernel stack that an IRET
    /// will consume to enter virtual 8086 mode.
    /// </summary>
    private static Machine BuildPagedVirtual8086Machine(CpuModel? model = null)
    {
        var machine = new Machine();
        if (model is { } selected)
        {
            machine.CpuModel = selected;
        }

        machine.RunInstructions(200);

        WriteDescriptor(machine, GdtBase, 1, 0, 0xFFFFF, 0x9A, 0xC0);   // kernel code
        WriteDescriptor(machine, GdtBase, 2, 0, 0xFFFFF, 0x92, 0xC0);   // kernel data
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

        BuildSupervisorOnlyMapping(machine);
        machine.Cpu.State.Cr3 = DirectoryBase;
        machine.Cpu.State.Cr0 |= 0x80000000u;      // paging enable

        machine.Cpu.State.Halted = false;

        machine.Cpu.SetSegment(SegReg.Cs, 0x08);
        machine.Cpu.SetSegment(SegReg.Ss, 0x10);
        machine.Cpu.State.Cpl = 0;
        machine.Cpu.State.Esp = 0x7000;
        machine.Cpu.Flags.SetValue(0x0002);

        // THE NINE-WORD FRAME, which is four words longer than an ordinary
        // ring 3 return: in this mode ES, DS, FS and GS are paragraph
        // addresses rather than selectors and cannot be loaded by MOV, so the
        // processor takes them off the frame.
        machine.Memory.WriteDword(0x7000, 0x00000000);              // eip
        machine.Memory.WriteDword(0x7004, TaskCodeSegment);         // cs
        machine.Memory.WriteDword(0x7008, 0x0202 | VirtualModeFlag);// eflags, bit 17 set
        machine.Memory.WriteDword(0x700C, 0x00000800);              // esp
        machine.Memory.WriteDword(0x7010, TaskStackSegment);        // ss
        machine.Memory.WriteDword(0x7014, TaskCodeSegment);         // es
        machine.Memory.WriteDword(0x7018, TaskCodeSegment);         // ds
        machine.Memory.WriteDword(0x701C, TaskCodeSegment);         // fs
        machine.Memory.WriteDword(0x7020, TaskCodeSegment);         // gs

        // The IRET that lies, and a NOP for the task to execute afterwards.
        machine.Memory.WriteByte(KernelEntryPoint, 0xCF);
        machine.Cpu.State.Eip = KernelEntryPoint;

        machine.Memory.WriteByte(TaskCodeSegment * 16u, 0x90);

        return machine;
    }

    /// <summary>
    /// The arrangement itself, asserted before anything is executed.
    ///
    /// Worth its own check because every other test in this file is
    /// meaningless if it does not hold: a page holding the IDT that happens
    /// to be user-accessible would let all of them pass with the defect
    /// present, which is precisely how the defect survived the first fix.
    /// </summary>
    [Fact]
    public void ThePageHoldingTheInterruptTableIsNotUserAccessible()
    {
        using Machine machine = BuildPagedVirtual8086Machine();

        uint entry = PageEntryFor(machine, IdtBase);

        Assert.Equal(PagePresent, entry & PagePresent);
        Assert.Equal(0u, entry & PageUser);

        // And the task's own pages are, so that a failure below is about the
        // processor's table reads rather than about the task's own memory.
        Assert.Equal(PageUser, PageEntryFor(machine, TaskCodeSegment * 16u) & PageUser);
        Assert.Equal(PageUser, PageEntryFor(machine, TaskStackSegment * 16u) & PageUser);
    }

    /// <summary>
    /// Entering the mode still works: the IRET reads its frame from the kernel
    /// stack, which is supervisor-only, and that read is the processor's own.
    /// </summary>
    [Fact]
    public void IretEntersVirtualModeWithTheKernelMappedSupervisorOnly()
    {
        using Machine machine = BuildPagedVirtual8086Machine();

        machine.Cpu.Step();

        Assert.True(machine.Cpu.State.Virtual8086Mode);
        Assert.Equal(TaskCodeSegment, machine.Cpu.State.Cs.Selector);
        Assert.Equal(0u, machine.Cpu.State.Eip);
        Assert.Equal(3, (int)machine.Cpu.State.Cpl);
    }

    /// <summary>
    /// THE DEFECT ITSELF: a hardware interrupt delivered to a virtual 8086
    /// task must be able to read its gate out of a supervisor-only IDT.
    ///
    /// Vector 0x90 rather than a low one because that is where Aurora OS
    /// remaps the 8259s -- MS-DOS owns INT 0x21, so a kernel running DOS
    /// programs cannot leave the chips at 0x20 where the timer would collide
    /// with it. The vector number does not change the mechanism; it is the
    /// one the failing guest actually used.
    ///
    /// Before the fix this raised a page fault on IdtBase + 0x90 * 8 with
    /// error code 0x5 -- present, user -- and the page fault's own delivery
    /// failed identically, which is a double fault and then a reset.
    /// </summary>
    [Fact]
    public void AHardwareInterruptReachesItsGateFromVirtualMode()
    {
        using Machine machine = BuildPagedVirtual8086Machine();

        WriteInterruptGate(machine, 0x90, 0x08, HandlerOffset);
        machine.Memory.WriteByte(HandlerOffset, 0x90);              // the handler: a NOP

        machine.Cpu.Step();
        Assert.True(machine.Cpu.State.Virtual8086Mode);

        machine.Cpu.RaiseInterruptForTest(0x90);

        // The handler is running: the gate was read, the stack was switched
        // and the frame was pushed, all through supervisor-only pages.
        Assert.False(machine.Cpu.State.Virtual8086Mode);
        Assert.Equal(0, (int)machine.Cpu.State.Cpl);
        Assert.Equal((ushort)0x08, machine.Cpu.State.Cs.Selector);
        Assert.Equal(HandlerOffset, machine.Cpu.State.Eip);
    }

    /// <summary>
    /// The same for a software interrupt, which reaches a gate by a different
    /// path -- and the path is worth stating precisely, because it is easy to
    /// assume the wrong one.
    ///
    /// INT 0x21 from a virtual 8086 task with IOPL below 3 does NOT dispatch
    /// through the vector the task named. It raises a GENERAL PROTECTION
    /// FAULT, and the monitor decides what the task meant. That is the whole
    /// mechanism by which a monitor keeps MS-DOS services for itself, and it
    /// is why IOPL is left below 3 in the first place.
    ///
    /// So what this measures is the gate for vector 13, fetched out of the
    /// same supervisor-only IDT, on the delivery path a DOS program takes
    /// several times before it draws anything.
    /// </summary>
    [Fact]
    public void ASoftwareInterruptFaultsToTheMonitorThroughASupervisorOnlyTable()
    {
        using Machine machine = BuildPagedVirtual8086Machine();

        // The general protection gate, which is where an INT from a virtual
        // 8086 task actually lands, and the vector the task names as well --
        // so that a dispatch to the wrong one would be visible rather than
        // silently landing on an absent gate.
        WriteInterruptGate(machine, 13, 0x08, HandlerOffset);
        WriteInterruptGate(machine, 0x21, 0x08, HandlerOffset + 0x100);
        machine.Memory.WriteByte(HandlerOffset, 0x90);
        machine.Memory.WriteByte(HandlerOffset + 0x100, 0x90);

        // INT 0x21, which is every service MS-DOS ever offered.
        machine.Memory.WriteByte(TaskCodeSegment * 16u, 0xCD);
        machine.Memory.WriteByte(TaskCodeSegment * 16u + 1, 0x21);

        machine.Cpu.Step();
        Assert.True(machine.Cpu.State.Virtual8086Mode);

        machine.Cpu.Step();

        // The monitor is running, reached through the fault rather than
        // through the vector the task named.
        Assert.False(machine.Cpu.State.Virtual8086Mode);
        Assert.Equal(0, (int)machine.Cpu.State.Cpl);
        Assert.Equal(HandlerOffset, machine.Cpu.State.Eip);
    }

    /// <summary>
    /// The same class once more, on the path the Pentium's virtual-mode
    /// extensions add. With CR4.VME set and IOPL below 3, INT n consults the
    /// interrupt redirection bitmap -- the 32 bytes below the I/O permission
    /// bitmap in the task state segment -- and a clear bit sends the
    /// interrupt through the task's own real-mode vector table instead of
    /// faulting to the monitor. Reading that bitmap is the processor
    /// consulting its own TSS, so it must succeed although the TSS page is
    /// supervisor-only; read with the task's CPL 3 it page-faulted instead.
    /// </summary>
    [Fact]
    public void AVirtualModeExtensionsRedirectionReadsTheBitmapFromASupervisorOnlyTss()
    {
        const uint IoMapBaseOffset = 0x66;
        const ushort IoMapBase = 0x88;          // the redirection bitmap sits at 0x68-0x87, all clear
        const uint TssLimit = 0xFF;
        const ushort RedirectedSegment = 0x0C00;   // linear 0xC000, the task's own handler
        const byte DosVector = 0x21;

        using Machine machine = BuildPagedVirtual8086Machine(CpuModel.Pentium);

        // A TSS long enough to hold the I/O map base and the 32 bytes below it.
        WriteDescriptor(machine, GdtBase, 5, TssBase, TssLimit, 0x89, 0x00);
        machine.Memory.WriteWord(TssBase + IoMapBaseOffset, IoMapBase);
        machine.Cpu.State.Cr4 |= CpuState.Cr4Vme;

        // The task's own vector table at linear 0, reachable from ring 3 as a
        // monitor hands it to the task, and the handler it names.
        machine.Memory.WriteWord(DosVector * 4u, 0x0000);
        machine.Memory.WriteWord(DosVector * 4u + 2, RedirectedSegment);
        MarkUserAccessible(machine, 0);
        MarkUserAccessible(machine, RedirectedSegment * 16u);
        machine.Memory.WriteByte(RedirectedSegment * 16u, 0x90);

        // The GP gate, so a fault would be visible rather than a reset.
        WriteInterruptGate(machine, 13, 0x08, HandlerOffset);
        WriteInterruptGate(machine, 14, 0x08, HandlerOffset);
        machine.Memory.WriteByte(HandlerOffset, 0x90);

        machine.Memory.WriteByte(TaskCodeSegment * 16u, 0xCD);
        machine.Memory.WriteByte(TaskCodeSegment * 16u + 1, DosVector);

        Assert.Equal(0u, PageEntryFor(machine, TssBase) & PageUser);

        machine.Cpu.Step();
        Assert.True(machine.Cpu.State.Virtual8086Mode);

        machine.Cpu.Step();

        // Still the task, now in its own handler: neither #GP nor #PF.
        Assert.True(machine.Cpu.State.Virtual8086Mode);
        Assert.Equal(RedirectedSegment, machine.Cpu.State.Cs.Selector);
        Assert.Equal(0u, machine.Cpu.State.Eip);
    }

    // ---- the same class on large pages and under PAE ----------------------------

    /// <summary>Where the large-page tests put the IDT: the first byte of the second 4 MB.</summary>
    private const uint LargePageIdtBase = 0x00400000;

    /// <summary>Bit 7 of a page directory entry with CR4.PSE (or PAE) set: the entry maps a large page.</summary>
    private const uint PageSizeBit = 0x80;

    private const uint PaeDirectoryPointerTable = 0x00032000;
    private const uint PaeDirectory = 0x00033000;
    private const uint PaeTable = 0x00034000;
    private const uint PaeEntrySize = 8;
    private const uint PaeTwoMegabytes = 0x00200000;

    private static void WriteInterruptGateAt(Machine machine, uint idtBase, byte vector, ushort selector, uint offset)
    {
        uint entry = idtBase + (uint)vector * 8;

        machine.Memory.WriteWord(entry, (ushort)(offset & 0xFFFF));
        machine.Memory.WriteWord(entry + 2, selector);
        machine.Memory.WriteByte(entry + 4, 0);
        machine.Memory.WriteByte(entry + 5, 0x8E);
        machine.Memory.WriteWord(entry + 6, (ushort)(offset >> 16));
    }

    /// <summary>
    /// Delivers vector 0x90 from the virtual 8086 task through an IDT that
    /// sits at <see cref="LargePageIdtBase"/>, and checks the ring 0 handler
    /// is running: the gate read must have succeeded through a supervisor-only
    /// large page.
    /// </summary>
    private static void AssertHardwareInterruptReachesTheLargePageGate(Machine machine)
    {
        machine.Cpu.State.IdtBase = LargePageIdtBase;
        machine.Cpu.State.IdtLimit = 0x7FF;
        WriteInterruptGateAt(machine, LargePageIdtBase, 0x90, 0x08, HandlerOffset);
        machine.Memory.WriteByte(HandlerOffset, 0x90);
        machine.Cpu.FlushTlb();

        machine.Cpu.Step();
        Assert.True(machine.Cpu.State.Virtual8086Mode);

        machine.Cpu.RaiseInterruptForTest(0x90);

        Assert.False(machine.Cpu.State.Virtual8086Mode);
        Assert.Equal(0, (int)machine.Cpu.State.Cpl);
        Assert.Equal(HandlerOffset, machine.Cpu.State.Eip);
    }

    /// <summary>
    /// CR4.PSE: the kernel maps the IDT with a 4 MB page directory entry,
    /// supervisor-only. The large-page walk must honour the processor's own
    /// supervisor authority exactly as the 4 KB walk does; asking with the
    /// task's CPL 3 page-faulted with U/S set, the fault's own delivery
    /// faulted the same way, and the machine reset.
    /// </summary>
    [Fact]
    public void AHardwareInterruptReadsItsGateFromASupervisorOnlyFourMegabytePage()
    {
        using Machine machine = BuildPagedVirtual8086Machine(CpuModel.PentiumII);

        machine.Memory.WriteDword(DirectoryBase + 4, LargePageIdtBase | PagePresent | PageWritable | PageSizeBit);
        machine.Cpu.State.Cr4 |= CpuState.Cr4Pse;

        AssertHardwareInterruptReachesTheLargePageGate(machine);
    }

    /// <summary>
    /// CR4.PAE: CR3 names a page-directory-pointer table, and every access --
    /// the processor's own table reads included -- must take the three-level
    /// walk. Here the IDT sits in a supervisor-only 2 MB page and everything
    /// else in supervisor-only 4 KB pages, the task's two pages excepted.
    /// Walking CR3 as a two-level directory for the gate read found a wrong
    /// frame; a 2 MB walk asking with CPL 3 page-faulted.
    /// </summary>
    [Fact]
    public void AHardwareInterruptReadsItsGateThroughThePaeWalk()
    {
        using Machine machine = BuildPagedVirtual8086Machine(CpuModel.PentiumII);

        // The first 2 MB through one page table, identity-mapped and
        // supervisor-only except the task's own code and stack pages.
        for (uint index = 0; index < 512; index++)
        {
            uint linear = index * 0x1000;
            bool taskPage = linear == TaskCodeSegment * 16u || linear == TaskStackSegment * 16u;
            uint flags = PagePresent | PageWritable | (taskPage ? PageUser : 0);
            machine.Memory.WriteDword(PaeTable + index * PaeEntrySize, linear | flags);
            machine.Memory.WriteDword(PaeTable + index * PaeEntrySize + 4, 0);
        }

        for (uint index = 0; index < 512; index++)
        {
            machine.Memory.WriteDword(PaeDirectory + index * PaeEntrySize, 0);
            machine.Memory.WriteDword(PaeDirectory + index * PaeEntrySize + 4, 0);
        }

        uint idtDirectoryIndex = LargePageIdtBase / PaeTwoMegabytes;
        machine.Memory.WriteDword(PaeDirectory, PaeTable | PagePresent | PageWritable | PageUser);
        machine.Memory.WriteDword(PaeDirectory + idtDirectoryIndex * PaeEntrySize,
                                  LargePageIdtBase | PagePresent | PageWritable | PageSizeBit);

        machine.Memory.WriteDword(PaeDirectoryPointerTable, PaeDirectory | PagePresent);
        machine.Cpu.State.Cr3 = PaeDirectoryPointerTable;
        Array.Clear(machine.Cpu.Pdpte);
        machine.Cpu.Pdpte[0] = PaeDirectory | PagePresent;
        machine.Cpu.State.Cr4 |= CpuState.Cr4Pae;

        AssertHardwareInterruptReachesTheLargePageGate(machine);
    }

    /// <summary>
    /// And the protection that must NOT be given away by the fix.
    ///
    /// Treating the processor's own reads as supervisor is correct; treating
    /// the TASK's reads that way would hand a DOS program the kernel. This
    /// asserts the distinction directly: the same supervisor-only page the
    /// interrupt gate was read from is unreachable when the task itself
    /// reaches for it.
    /// </summary>
    [Fact]
    public void TheTaskItselfStillCannotReadSupervisorOnlyMemory()
    {
        using Machine machine = BuildPagedVirtual8086Machine();

        WriteInterruptGate(machine, 14, 0x08, HandlerOffset);
        machine.Memory.WriteByte(HandlerOffset, 0x90);

        // MOV AL, [0x1000] with DS pointing at the IDT's paragraph, so the
        // task reads the very bytes the gate fetch reads.
        machine.Memory.WriteDword(0x7018, (ushort)(IdtBase / 16));   // ds
        machine.Memory.WriteByte(TaskCodeSegment * 16u, 0xA0);
        machine.Memory.WriteByte(TaskCodeSegment * 16u + 1, 0x00);
        machine.Memory.WriteByte(TaskCodeSegment * 16u + 2, 0x00);

        machine.Cpu.Step();
        Assert.True(machine.Cpu.State.Virtual8086Mode);

        machine.Cpu.Step();

        // The read faulted: the page fault handler is running instead.
        Assert.False(machine.Cpu.State.Virtual8086Mode);
        Assert.Equal(IdtBase, machine.Cpu.State.Cr2);
        Assert.Equal(HandlerOffset, machine.Cpu.State.Eip);
    }
}
