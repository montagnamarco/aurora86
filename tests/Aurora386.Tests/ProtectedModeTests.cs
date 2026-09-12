using Aurora386.Core;
using Aurora386.Core.Cpu;

namespace Aurora386.Tests;

/// <summary>
/// Tests for protected mode: descriptors, interrupt gates and IRET.
///
/// Each test builds the tables by hand rather than through a helper, because
/// the layout of a descriptor is the thing being tested. A descriptor with a
/// field in the wrong place is the classic protected-mode bug, and it hides
/// well behind a builder function.
/// </summary>
public class ProtectedModeTests
{
    private const uint GdtBase = 0x1000;
    private const uint IdtBase = 0x2000;

    /// <summary>
    /// Writes a segment descriptor.
    ///
    /// The 286 laid out base and limit contiguously; the 386 needed four more
    /// base bits and four more limit bits and had nowhere to put them, so they
    /// were scattered into the top two bytes. Every descriptor since carries
    /// that scar.
    /// </summary>
    private static void WriteDescriptor(Machine machine, uint tableBase, int index,
                                        uint baseAddress, uint limit,
                                        byte access, byte granularity = 0x40)
    {
        uint address = tableBase + (uint)(index * 8);

        machine.Memory.WriteWord(address, (ushort)(limit & 0xFFFF));
        machine.Memory.WriteWord(address + 2, (ushort)(baseAddress & 0xFFFF));
        machine.Memory.WriteByte(address + 4, (byte)((baseAddress >> 16) & 0xFF));
        machine.Memory.WriteByte(address + 5, access);
        machine.Memory.WriteByte(address + 6,
            (byte)((granularity & 0xF0) | ((limit >> 16) & 0x0F)));
        machine.Memory.WriteByte(address + 7, (byte)((baseAddress >> 24) & 0xFF));
    }

    /// <summary>
    /// Writes an interrupt or trap gate. Unlike a segment descriptor, a gate
    /// holds an offset rather than a base: it points at a place *inside* a
    /// segment, not at the segment itself.
    /// </summary>
    private static void WriteGate(Machine machine, uint tableBase, int vector,
                                  ushort selector, uint offset, byte type, byte dpl = 0)
    {
        uint address = tableBase + (uint)(vector * 8);
        byte access = (byte)(0x80 | (dpl << 5) | type);

        machine.Memory.WriteWord(address, (ushort)(offset & 0xFFFF));
        machine.Memory.WriteWord(address + 2, selector);
        machine.Memory.WriteByte(address + 4, 0);
        machine.Memory.WriteByte(address + 5, access);
        machine.Memory.WriteWord(address + 6, (ushort)(offset >> 16));
    }

    /// <summary>
    /// A machine in protected mode with a flat code segment at selector 8 and
    /// a flat data segment at 16, which is the arrangement almost every
    /// protected-mode program sets up first.
    /// </summary>
    private static Machine BuildProtectedMachine()
    {
        var machine = new Machine();
        machine.RunInstructions(200);   // let the BIOS finish its POST

        // 0x9A: present, code, readable. 0x92: present, data, writable.
        WriteDescriptor(machine, GdtBase, 1, 0, 0xFFFFF, 0x9A, 0xC0);
        WriteDescriptor(machine, GdtBase, 2, 0, 0xFFFFF, 0x92, 0xC0);

        machine.Cpu.State.GdtBase = GdtBase;
        machine.Cpu.State.GdtLimit = 0x17;
        machine.Cpu.State.IdtBase = IdtBase;
        machine.Cpu.State.IdtLimit = 0x7FF;
        machine.Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable;

        // The BIOS parks the CPU on a HLT once the POST is done, waiting for
        // a boot device. Step() honours that and does nothing, so tests that
        // want to execute an instruction have to wake the machine up first.
        machine.Cpu.State.Halted = false;

        return machine;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PopDestinationPageFaultCanRetryWithoutConsumingTheStack(bool word, bool acrossPage)
    {
        using Machine machine = BuildProtectedMachine();
        var cpu = machine.Cpu;
        cpu.SetSegment(SegReg.Cs, 8);
        cpu.SetSegment(SegReg.Ss, 16);
        cpu.SetSegment(SegReg.Ds, 16);
        // Identity-map the low 64K for the code, tables, stack, and handler.
        const uint directory = 0x10000, table = 0x11000;
        machine.Memory.WriteDword(directory, table | 3);
        for (uint page = 0; page < 16; page++)
            machine.Memory.WriteDword(table + page * 4, (page << 12) | 3);
        uint target = acrossPage ? 0xFFFfu : 0x10087u;
        cpu.State.Cr3 = directory;
        cpu.State.Cr0 |= CpuState.Cr0Paging;
        cpu.State.Eip = 0x3000;
        cpu.State.Eax = target;
        cpu.State.Esp = 0x9000;
        machine.Memory.WriteDword(0x9000, 0x12345678);
        machine.Memory.WriteDword(0x9004, 0xDEADBEEF);
        machine.Memory.WriteByte(0xFFFF, 0xAA);
        uint code = 0x3000;
        if (word) machine.Memory.WriteByte(code++, 0x66);
        machine.Memory.WriteByte(code++, 0x8F); // POP [EAX]
        machine.Memory.WriteByte(code++, 0x00);
        WriteGate(machine, IdtBase, 14, 8, 0x4000, 0x0E);
        // Discard the #PF error code, then retry through IRETD.
        foreach (var (value, index) in new byte[] { 0x83, 0xC4, 0x04, 0xCF }.Select((v,i)=>(v,i)))
            machine.Memory.WriteByte(0x4000u + (uint)index, value);

        cpu.Step();
        Assert.Equal(0x4000u, cpu.State.Eip);
        Assert.Equal(0x8FF0u, cpu.State.Esp);
        Assert.Equal(acrossPage ? 0x10000u : target, cpu.State.Cr2);
        Assert.Equal(2u, machine.Memory.ReadDword(0x8FF0));
        Assert.Equal(0x3000u, machine.Memory.ReadDword(0x8FF4));
        Assert.Equal((byte)0xAA, machine.Memory.ReadByte(0xFFFF));

        // Page in the missing destination, as Windows does, and retry.
        machine.Memory.WriteDword(table + 16 * 4, 0x12000 | 3);
        cpu.FlushTlb();
        cpu.Step();
        cpu.Step();
        Assert.Equal(0x9000u, cpu.State.Esp);
        Assert.Equal(0x3000u, cpu.State.Eip);
        cpu.Step();
        Assert.Equal(code, cpu.State.Eip);
        Assert.Equal(word ? 0x9002u : 0x9004u, cpu.State.Esp);
        Assert.Equal(word ? 0x5678u : 0x12345678u, word
            ? cpu.ReadMemoryWord(SegReg.Ds, target)
            : cpu.ReadMemoryDword(SegReg.Ds, target));
        Assert.Equal(0xDEADBEEFu, machine.Memory.ReadDword(0x9004));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PushPageFaultRetriesWithTheOriginalStackPointer(bool word, bool wideStack)
    {
        using Machine machine = BuildProtectedMachine();
        var cpu = machine.Cpu;
        WriteDescriptor(machine, GdtBase, 2, 0, 0xFFFF, 0x92,
            wideStack ? (byte)0x40 : (byte)0);
        cpu.SetSegment(SegReg.Cs, 8);
        cpu.SetSegment(SegReg.Ss, 16);
        cpu.SetSegment(SegReg.Ds, 16);
        const uint directory = 0x10000;
        const uint table = 0x11000;
        machine.Memory.WriteDword(directory, table | 3);
        for (uint page = 0; page < 16; page++)
        {
            if (page != 8)
            {
                machine.Memory.WriteDword(table + page * 4, (page << 12) | 3);
            }
        }
        uint originalStackPointer = wideStack ? 0x9000u : 0xABCD9000u;
        cpu.State.Esp = originalStackPointer;
        cpu.State.Eip = 0x3000;
        cpu.State.Cr3 = directory;
        cpu.State.Cr0 |= CpuState.Cr0Paging;
        machine.Memory.WriteDword(0x900C, 0x12345678);
        uint code = 0x3000;
        if (word)
        {
            machine.Memory.WriteByte(code++, 0x66);
        }
        // PUSH must re-read the same argument on retry. The 32-bit case uses
        // ESP-relative addressing, as in the Windows fault that exposed this.
        if (!wideStack)
        {
            cpu.State.Bx = 0x900C;
            machine.Memory.WriteByte(code++, 0x67);
            machine.Memory.WriteByte(code++, 0xFF);
            machine.Memory.WriteByte(code++, 0x37); // PUSH [BX]
        }
        else
        {
            foreach (byte value in new byte[] { 0xFF, 0x74, 0x24, 0x0C })
            {
                machine.Memory.WriteByte(code++, value);
            }
        }
        WriteGate(machine, IdtBase, 14, 8, 0x4000, 0x0E);
        byte[] handler = [0x83, 0xC4, 0x04, 0xCF];
        for (int index = 0; index < handler.Length; index++)
        {
            machine.Memory.WriteByte(0x4000 + (uint)index, handler[index]);
        }
        uint? stackAtFault = null;
        cpu.ProcessorException += (vector, _, _, _) =>
        {
            if (vector == 14)
            {
                stackAtFault = cpu.State.Esp;
                // Make the guard page available before the same-ring handler
                // writes its frame. IRETD then retries the original PUSH.
                machine.Memory.WriteDword(table + 8 * 4, 0x8000 | 3);
                cpu.FlushTlb();
            }
        };

        cpu.Step();
        Assert.Equal(originalStackPointer, stackAtFault);
        Assert.Equal(0x4000u, cpu.State.Eip);
        Assert.Equal(originalStackPointer - 16, cpu.State.Esp);
        Assert.Equal(word ? 0x8FFEu : 0x8FFCu, cpu.State.Cr2);
        cpu.Step();
        cpu.Step();
        Assert.Equal(originalStackPointer, cpu.State.Esp);
        Assert.Equal(0x3000u, cpu.State.Eip);
        cpu.Step();
        Assert.Equal(code, cpu.State.Eip);
        Assert.Equal(originalStackPointer - (word ? 2u : 4u), cpu.State.Esp);
        Assert.Equal(word ? 0x5678u : 0x12345678u, word
            ? machine.Memory.ReadWord(0x8FFE)
            : machine.Memory.ReadDword(0x8FFC));
    }

    [Fact]
    public void LoadingASelectorReadsTheDescriptorsBaseAndLimit()
    {
        using Machine machine = BuildProtectedMachine();
        WriteDescriptor(machine, GdtBase, 3, 0x00120000, 0x0FFF, 0x92, 0x40);
        machine.Cpu.State.GdtLimit = 0x1F;

        machine.Cpu.SetSegment(SegReg.Es, 0x18);

        Assert.Equal(0x00120000u, machine.Cpu.State.Es.Base);
        Assert.Equal(0x0FFFu, machine.Cpu.State.Es.Limit);
    }

    [Fact]
    public void TheGranularityBitScalesTheLimitByFourKilobytes()
    {
        using Machine machine = BuildProtectedMachine();

        // A 20-bit limit could only describe 1 MB. The granularity bit makes
        // each unit a page instead of a byte, which is how a 4 GB segment fits
        // in the same field.
        machine.Cpu.SetSegment(SegReg.Ds, 0x10);

        Assert.Equal(0xFFFFFFFFu, machine.Cpu.State.Ds.Limit);
    }

    [Fact]
    public void ASelectorPastTheEndOfTheTableFaults()
    {
        using Machine machine = BuildProtectedMachine();

        // The table holds three descriptors; selector 0x40 asks for the ninth.
        CpuException fault = Assert.Throws<CpuException>(
            () => machine.Cpu.SetSegment(SegReg.Es, 0x40));

        Assert.Equal(13, fault.Vector);   // #GP
    }

    [Fact]
    public void ANotPresentDescriptorRaisesSegmentNotPresent()
    {
        using Machine machine = BuildProtectedMachine();

        // Access byte without bit 7: the operating system uses this to trap
        // the first use of a segment it has swapped out.
        WriteDescriptor(machine, GdtBase, 3, 0, 0xFFFF, 0x12);
        machine.Cpu.State.GdtLimit = 0x1F;

        CpuException fault = Assert.Throws<CpuException>(
            () => machine.Cpu.SetSegment(SegReg.Es, 0x18));

        Assert.Equal(11, fault.Vector);   // #NP
    }

    [Fact]
    public void TheNullSelectorLoadsIntoADataSegmentWithoutFaulting()
    {
        using Machine machine = BuildProtectedMachine();

        // Selector zero makes the register unusable but is not itself an
        // error: it is how software marks a segment it is not using.
        machine.Cpu.SetSegment(SegReg.Es, 0);
        Assert.Equal(0, machine.Cpu.State.Es.Selector);
    }

    [Fact]
    public void AnInterruptGateJumpsToItsHandler()
    {
        using Machine machine = BuildProtectedMachine();
        WriteGate(machine, IdtBase, 0x20, 0x08, 0x00030000, 0x0E);

        machine.Cpu.SetSegment(SegReg.Cs, 0x08);
        machine.Cpu.SetSegment(SegReg.Ss, 0x10);
        machine.Cpu.State.Esp = 0x8000;

        machine.Cpu.RaiseInterruptForTest(0x20);

        Assert.Equal(0x08, machine.Cpu.State.Cs.Selector);
        Assert.Equal(0x00030000u, machine.Cpu.State.Eip);
    }

    [Fact]
    public void AnInterruptGateClearsTheInterruptFlag()
    {
        using Machine machine = BuildProtectedMachine();
        WriteGate(machine, IdtBase, 0x20, 0x08, 0x1000, 0x0E);

        machine.Cpu.SetSegment(SegReg.Cs, 0x08);
        machine.Cpu.SetSegment(SegReg.Ss, 0x10);
        machine.Cpu.State.Esp = 0x8000;
        machine.Cpu.Flags.Interrupt = true;

        machine.Cpu.RaiseInterruptForTest(0x20);

        // A device handler must not be re-entered by the same device midway.
        Assert.False(machine.Cpu.Flags.Interrupt);
    }

    [Fact]
    public void ATrapGateLeavesTheInterruptFlagAlone()
    {
        using Machine machine = BuildProtectedMachine();
        WriteGate(machine, IdtBase, 0x21, 0x08, 0x1000, 0x0F);   // trap gate

        machine.Cpu.SetSegment(SegReg.Cs, 0x08);
        machine.Cpu.SetSegment(SegReg.Ss, 0x10);
        machine.Cpu.State.Esp = 0x8000;
        machine.Cpu.Flags.Interrupt = true;

        machine.Cpu.RaiseInterruptForTest(0x21);

        // This is the whole difference between the two gate types, and it is
        // what lets a debugger's breakpoint handler keep the clock running.
        Assert.True(machine.Cpu.Flags.Interrupt);
    }

    [Fact]
    public void AGateWithNoPresentBitFaults()
    {
        using Machine machine = BuildProtectedMachine();

        uint address = IdtBase + 0x20 * 8;
        machine.Memory.WriteWord(address, 0x1000);
        machine.Memory.WriteWord(address + 2, 0x08);
        machine.Memory.WriteByte(address + 5, 0x0E);   // type, but not present

        machine.Cpu.SetSegment(SegReg.Cs, 0x08);
        machine.Cpu.SetSegment(SegReg.Ss, 0x10);
        machine.Cpu.State.Esp = 0x8000;

        CpuException fault = Assert.Throws<CpuException>(
            () => machine.Cpu.RaiseInterruptForTest(0x20));

        Assert.Equal(11, fault.Vector);
    }

    [Fact]
    public void AVectorPastTheEndOfTheIdtFaults()
    {
        using Machine machine = BuildProtectedMachine();
        machine.Cpu.State.IdtLimit = 0x0F;   // room for two vectors only

        machine.Cpu.SetSegment(SegReg.Cs, 0x08);
        machine.Cpu.SetSegment(SegReg.Ss, 0x10);
        machine.Cpu.State.Esp = 0x8000;

        CpuException fault = Assert.Throws<CpuException>(
            () => machine.Cpu.RaiseInterruptForTest(0x20));

        Assert.Equal(13, fault.Vector);
    }

    [Fact]
    public void TheInterruptFramePutsTheReturnAddressOnTheStack()
    {
        using Machine machine = BuildProtectedMachine();
        WriteGate(machine, IdtBase, 0x20, 0x08, 0x1000, 0x0E);

        machine.Cpu.SetSegment(SegReg.Cs, 0x08);
        machine.Cpu.SetSegment(SegReg.Ss, 0x10);
        machine.Cpu.State.Esp = 0x8000;
        machine.Cpu.State.Eip = 0x4321;

        machine.Cpu.RaiseInterruptForTest(0x20);

        // A 32-bit gate pushes three dwords: EIP is on top, then CS, then
        // EFLAGS. IRET pops them in that order.
        Assert.Equal(0x8000u - 12, machine.Cpu.State.Esp);
        Assert.Equal(0x4321u, machine.Memory.ReadDword(machine.Cpu.State.Esp));
    }

    [Fact]
    public void FaultsThatNameADescriptorPushAnErrorCode()
    {
        using Machine machine = BuildProtectedMachine();
        WriteGate(machine, IdtBase, 13, 0x08, 0x1000, 0x0E);

        machine.Cpu.SetSegment(SegReg.Cs, 0x08);
        machine.Cpu.SetSegment(SegReg.Ss, 0x10);
        machine.Cpu.State.Esp = 0x8000;

        machine.Cpu.RaiseInterruptForTest(13, 0x1234);

        // Four dwords rather than three: the extra one names the descriptor
        // that caused the fault, so the handler can report which.
        Assert.Equal(0x8000u - 16, machine.Cpu.State.Esp);
        Assert.Equal(0x1234u, machine.Memory.ReadDword(machine.Cpu.State.Esp));
    }

    [Fact]
    public void FaultsWithNothingToNamePushNoErrorCode()
    {
        using Machine machine = BuildProtectedMachine();
        WriteGate(machine, IdtBase, 0, 0x08, 0x1000, 0x0E);

        machine.Cpu.SetSegment(SegReg.Cs, 0x08);
        machine.Cpu.SetSegment(SegReg.Ss, 0x10);
        machine.Cpu.State.Esp = 0x8000;

        machine.Cpu.RaiseInterruptForTest(0);   // divide error

        // A division by zero has no descriptor to blame. A handler that popped
        // a code that was never pushed would return to the wrong address.
        Assert.Equal(0x8000u - 12, machine.Cpu.State.Esp);
    }

    [Fact]
    public void IretUnwindsExactlyWhatTheGatePushed()
    {
        using Machine machine = BuildProtectedMachine();
        WriteGate(machine, IdtBase, 0x20, 0x08, 0x1000, 0x0E);

        machine.Cpu.SetSegment(SegReg.Cs, 0x08);
        machine.Cpu.SetSegment(SegReg.Ss, 0x10);
        machine.Cpu.State.Esp = 0x8000;
        machine.Cpu.State.Eip = 0x4321;
        machine.Cpu.Flags.Carry = true;

        machine.Cpu.RaiseInterruptForTest(0x20);

        // The code segment is 32-bit (the descriptor's D bit is set), so a
        // bare CF is already IRETD and pops dwords -- which is exactly what
        // the 32-bit gate pushed. Adding a 66 prefix here would ask for the
        // 16-bit form and unwind the wrong number of bytes.
        machine.Memory.WriteByte(0x1000, 0xCF);
        machine.Cpu.Step();

        Assert.Equal(0x4321u, machine.Cpu.State.Eip);
        Assert.Equal(0x8000u, machine.Cpu.State.Esp);
        Assert.True(machine.Cpu.Flags.Carry);
    }

    [Fact]
    public void IretAtRingThreeLeavesIoplAloneButLoadsIfWithinIopl()
    {
        // DOSX runs Windows at ring 3 with IOPL 3 and lets its real-mode side
        // build IRET frames whose flags image carries IOPL 0. A ring 3 IRET
        // may not touch IOPL, so the image must not demote the client; IF,
        // being within IOPL, does follow the image.
        using Machine machine = BuildProtectedMachine();
        AddUserModeAndTss(machine);
        machine.Cpu.SetSegment(SegReg.Cs, 0x23);
        machine.Cpu.SetSegment(SegReg.Ss, 0x2B);
        machine.Cpu.State.Cpl = 3;
        machine.Cpu.State.Esp = 0x3000;
        machine.Cpu.Flags.SetValue(0x3002);          // IOPL 3, IF clear

        machine.Memory.WriteDword(0x3000, 0x2000);   // EIP
        machine.Memory.WriteDword(0x3004, 0x23);     // CS
        machine.Memory.WriteDword(0x3008, 0x0202);   // EFLAGS: IOPL 0, IF set
        machine.Memory.WriteByte(0x1000, 0xCF);
        machine.Cpu.State.Eip = 0x1000;
        machine.Cpu.Step();

        Assert.Equal(0x2000u, machine.Cpu.State.Eip);
        Assert.Equal(3u, (machine.Cpu.Flags.Value >> 12) & 3);
        Assert.True(machine.Cpu.Flags.Interrupt);
    }

    [Fact]
    public void IretAtRingThreeBelowIoplChangesNeitherIoplNorIf()
    {
        using Machine machine = BuildProtectedMachine();
        AddUserModeAndTss(machine);
        machine.Cpu.SetSegment(SegReg.Cs, 0x23);
        machine.Cpu.SetSegment(SegReg.Ss, 0x2B);
        machine.Cpu.State.Cpl = 3;
        machine.Cpu.State.Esp = 0x3000;
        machine.Cpu.Flags.SetValue(0x0002);          // IOPL 0, IF clear

        machine.Memory.WriteDword(0x3000, 0x2000);
        machine.Memory.WriteDword(0x3004, 0x23);
        machine.Memory.WriteDword(0x3008, 0x3202);   // asks for IOPL 3 and IF
        machine.Memory.WriteByte(0x1000, 0xCF);
        machine.Cpu.State.Eip = 0x1000;
        machine.Cpu.Step();

        Assert.Equal(0u, (machine.Cpu.Flags.Value >> 12) & 3);
        Assert.False(machine.Cpu.Flags.Interrupt);
    }

    [Fact]
    public void IretFromRingZeroToRingThreeSetsIoplFromTheFrame()
    {
        // This is how DOSX starts its client: an IRET from ring 0 whose flags
        // image carries IOPL 3, so that the client's CLI and STI do not fault.
        using Machine machine = BuildProtectedMachine();
        AddUserModeAndTss(machine);
        machine.Cpu.SetSegment(SegReg.Cs, 0x08);
        machine.Cpu.SetSegment(SegReg.Ss, 0x10);
        machine.Cpu.State.Cpl = 0;
        machine.Cpu.State.Esp = 0x6000;
        machine.Cpu.Flags.SetValue(0x0002);

        machine.Memory.WriteDword(0x6000, 0x2000);   // EIP
        machine.Memory.WriteDword(0x6004, 0x23);     // CS, ring 3
        machine.Memory.WriteDword(0x6008, 0x3202);   // EFLAGS: IOPL 3, IF
        machine.Memory.WriteDword(0x600C, 0x3000);   // ESP
        machine.Memory.WriteDword(0x6010, 0x2B);     // SS
        machine.Memory.WriteByte(0x1000, 0xCF);
        machine.Cpu.State.Eip = 0x1000;
        machine.Cpu.Step();

        Assert.Equal(3, machine.Cpu.State.Cpl);
        Assert.Equal(3u, (machine.Cpu.Flags.Value >> 12) & 3);
        Assert.True(machine.Cpu.Flags.Interrupt);
        Assert.Equal(0x3000u, machine.Cpu.State.Esp);
    }

    [Fact]
    public void ProtectedModeDoesNotDisturbRealModeSegmentLoading()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);

        // Still in real mode: a selector is a paragraph address, not an index.
        machine.Cpu.SetSegment(SegReg.Es, 0x1234);

        Assert.Equal(0x12340u, machine.Cpu.State.Es.Base);
        Assert.Equal(0xFFFFu, machine.Cpu.State.Es.Limit);
    }

    // --- task state segment and privilege changes ---------------------------

    /// <summary>
    /// Runs one instruction from the flat code segment, at a fixed address
    /// well clear of the descriptor tables.
    /// </summary>
    private static void RunInstruction(Machine machine, params byte[] instruction)
    {
        const uint CodeAddress = 0x1000;

        machine.Cpu.SetSegment(SegReg.Cs, 0x08);
        machine.Cpu.SetSegment(SegReg.Ss, 0x10);
        machine.Cpu.State.Esp = 0x8000;
        machine.Cpu.State.Eip = CodeAddress;

        for (int i = 0; i < instruction.Length; i++)
            machine.Memory.WriteByte(CodeAddress + (uint)i, instruction[i]);

        machine.Cpu.Step();
    }

    private const uint TssBase = 0x4000;
    private const uint KernelStackTop = 0x8000;

    /// <summary>
    /// Adds a task state segment holding a ring 0 stack, and the ring 3 code
    /// and data segments to go with it.
    ///
    /// Selector 0x18 is the TSS, 0x23 ring 3 code, 0x2B ring 3 data. The low
    /// two bits of the last two are the privilege the selector is used at,
    /// which is why they are not round numbers.
    /// </summary>
    private static void AddUserModeAndTss(Machine machine)
    {
        // 0x89: present, 32-bit task state segment, available.
        WriteDescriptor(machine, GdtBase, 3, TssBase, 0x67, 0x89, 0x00);

        // 0xFA and 0xF2 are the ring 3 versions of the flat code and data
        // descriptors: same as ring 0's but with the privilege bits set.
        WriteDescriptor(machine, GdtBase, 4, 0, 0xFFFFF, 0xFA, 0xC0);
        WriteDescriptor(machine, GdtBase, 5, 0, 0xFFFFF, 0xF2, 0xC0);

        machine.Cpu.State.GdtLimit = 0x2F;

        // The ring 0 stack lives at offset 4 (ESP) and 8 (SS) of the TSS.
        machine.Memory.WriteDword(TssBase + 4, KernelStackTop);
        machine.Memory.WriteDword(TssBase + 8, 0x10);

        machine.Cpu.State.TaskRegister = 0x18;
    }

    [Fact]
    public void AnInterruptFromUserCodeSwitchesToTheKernelStack()
    {
        // Leaving the kernel on a stack the user controls would let user code
        // read what the kernel pushes, or move the stack pointer somewhere
        // harmful and wait for the kernel to write through it. The task state
        // segment exists almost entirely to prevent this.
        using Machine machine = BuildProtectedMachine();
        AddUserModeAndTss(machine);
        WriteGate(machine, IdtBase, 0x40, 0x08, 0x7000, type: 0x0E, dpl: 3);

        // Pretend we were running user code on a user stack.
        machine.Cpu.SetSegment(SegReg.Ss, 0x2B);
        machine.Cpu.State.Esp = 0x3000;
        machine.Cpu.State.Cpl = 3;

        machine.Cpu.RaiseInterruptForTest(0x40);

        // The handler is on the kernel's stack, not the user's.
        Assert.Equal((ushort)0x10, machine.Cpu.State.Ss.Selector);
        Assert.Equal(0, (int)machine.Cpu.State.Cpl);
        Assert.True(machine.Cpu.State.Esp <= KernelStackTop);
        Assert.True(machine.Cpu.State.Esp > KernelStackTop - 0x100);
    }

    [Fact]
    public void TheUsersStackIsPushedSoIretCanRestoreIt()
    {
        // The interrupted stack has to be recorded somewhere, and the only
        // place is the new stack, underneath the rest of the frame.
        using Machine machine = BuildProtectedMachine();
        AddUserModeAndTss(machine);
        WriteGate(machine, IdtBase, 0x40, 0x08, 0x7000, type: 0x0E, dpl: 3);

        const uint UserEsp = 0x3000;
        machine.Cpu.SetSegment(SegReg.Ss, 0x2B);
        machine.Cpu.State.Esp = UserEsp;
        machine.Cpu.State.Cpl = 3;

        machine.Cpu.RaiseInterruptForTest(0x40);

        // Frame from the top down: SS, ESP, EFLAGS, CS, EIP. The outermost
        // two are the user's stack, at the highest addresses.
        Assert.Equal(0x2Bu, machine.Memory.ReadDword(KernelStackTop - 4));
        Assert.Equal(UserEsp, machine.Memory.ReadDword(KernelStackTop - 8));
    }

    [Fact]
    public void AnInterruptWithinTheKernelKeepsTheStackItIsOn()
    {
        // No privilege change means no switch: the kernel interrupting itself
        // simply pushes onto the stack it is already using, and IRET must not
        // then try to pop a stack that was never pushed.
        using Machine machine = BuildProtectedMachine();
        AddUserModeAndTss(machine);
        WriteGate(machine, IdtBase, 0x40, 0x08, 0x7000, type: 0x0E);

        machine.Cpu.SetSegment(SegReg.Ss, 0x10);
        machine.Cpu.State.Esp = 0x6000;
        machine.Cpu.State.Cpl = 0;

        machine.Cpu.RaiseInterruptForTest(0x40);

        Assert.Equal((ushort)0x10, machine.Cpu.State.Ss.Selector);

        // Three dwords for EFLAGS, CS and EIP, and nothing more.
        Assert.Equal(0x6000u - 12, machine.Cpu.State.Esp);
    }

    [Fact]
    public void RaisingPrivilegeWithoutATaskStateSegmentFaults()
    {
        // Without a TSS there is no kernel stack to switch to. Guessing one
        // would be worse than faulting: the processor would be writing the
        // interrupt frame to an address nobody chose.
        using Machine machine = BuildProtectedMachine();
        AddUserModeAndTss(machine);
        WriteGate(machine, IdtBase, 0x40, 0x08, 0x7000, type: 0x0E, dpl: 3);

        machine.Cpu.State.TaskRegister = 0;   // no task state segment loaded

        machine.Cpu.SetSegment(SegReg.Ss, 0x2B);
        machine.Cpu.State.Esp = 0x3000;
        machine.Cpu.State.Cpl = 3;

        var fault = Assert.Throws<CpuException>(() => machine.Cpu.RaiseInterruptForTest(0x40));
        Assert.Equal(10, (int)fault.Vector);   // #TS, invalid task state segment
    }

    [Fact]
    public void TheTaskRegisterCanBeLoadedAndReadBack()
    {
        // LTR installs the task state segment and STR reads it back. An
        // operating system does the first once at startup; the second exists
        // so that it can find out what it did.
        using Machine machine = BuildProtectedMachine();
        AddUserModeAndTss(machine);

        machine.Cpu.State.TaskRegister = 0;

        // 0F 00 /3 with mod=11 and rm=0: LTR AX.
        machine.Cpu.State.Ax = 0x18;
        RunInstruction(machine, 0x0F, 0x00, 0xD8);

        Assert.Equal((ushort)0x18, machine.Cpu.State.TaskRegister);
    }

    [Fact]
    public void VerrReportsWhetherASegmentCouldBeRead()
    {
        // VERR asks the question without taking the fault, which is what a
        // kernel needs when a program hands it a selector: finding out the
        // hard way would mean faulting inside its own code.
        using Machine machine = BuildProtectedMachine();

        // The flat data segment at 0x10 is readable.
        machine.Cpu.State.Ax = 0x10;
        RunInstruction(machine, 0x0F, 0x00, 0xE0);   // VERR AX
        Assert.True(machine.Cpu.Flags.Zero);

        // The null selector is not.
        machine.Cpu.State.Ax = 0x00;
        RunInstruction(machine, 0x0F, 0x00, 0xE0);
        Assert.False(machine.Cpu.Flags.Zero);
    }

    [Fact]
    public void VerwRefusesACodeSegment()
    {
        // Code is never writable, whatever its descriptor says about being
        // readable.
        using Machine machine = BuildProtectedMachine();

        machine.Cpu.State.Ax = 0x08;                 // the flat code segment
        RunInstruction(machine, 0x0F, 0x00, 0xE8);   // VERW AX

        Assert.False(machine.Cpu.Flags.Zero);
    }

    // ---- LAR and LSL -------------------------------------------------------
    //
    // Windows leans on these two constantly: every selector handed across an
    // API boundary is vetted with them before it is trusted. Both answer in
    // the zero flag, and -- the part that is easy to get wrong -- both must
    // leave the destination register alone when the answer is no.

    [Fact]
    public void LarReadsTheAccessRightsOfADescriptor()
    {
        using Machine machine = BuildProtectedMachine();
        WriteDescriptor(machine, GdtBase, 3, 0, 0xFFFFF, 0x92, 0xC0);
        machine.Cpu.State.GdtLimit = 0x1F;
        machine.Cpu.State.Eax = 0x18;

        // 0F 02 D8: LAR EBX, EAX
        RunInstruction(machine, 0x0F, 0x02, 0xD8);

        Assert.True(machine.Cpu.Flags.Zero);

        // The access byte sits in bits 8..15, and the granularity nibble that
        // LAR also reports in bits 20..23. The limit is none of its business.
        Assert.Equal(0x92u, (machine.Cpu.State.Ebx >> 8) & 0xFF);
        Assert.Equal(0u, machine.Cpu.State.Ebx & 0xFF);
    }

    [Fact]
    public void LslReadsTheLimitOfADescriptor()
    {
        using Machine machine = BuildProtectedMachine();
        WriteDescriptor(machine, GdtBase, 3, 0, 0x01234, 0x92, 0x40);
        machine.Cpu.State.GdtLimit = 0x1F;
        machine.Cpu.State.Eax = 0x18;

        // 0F 03 D8: LSL EBX, EAX
        RunInstruction(machine, 0x0F, 0x03, 0xD8);

        Assert.True(machine.Cpu.Flags.Zero);
        Assert.Equal(0x01234u, machine.Cpu.State.Ebx);
    }

    [Fact]
    public void LslScalesTheLimitWhenTheGranularityBitIsSet()
    {
        using Machine machine = BuildProtectedMachine();
        WriteDescriptor(machine, GdtBase, 3, 0, 0x00010, 0x92, 0xC0);
        machine.Cpu.State.GdtLimit = 0x1F;
        machine.Cpu.State.Eax = 0x18;

        RunInstruction(machine, 0x0F, 0x03, 0xD8);

        Assert.True(machine.Cpu.Flags.Zero);

        // Sixteen pages, and the last page counts to its final byte.
        Assert.Equal(0x00010FFFu, machine.Cpu.State.Ebx);
    }

    /// <summary>
    /// A descriptor that is not present is still readable by LAR.
    ///
    /// This test asserted the opposite until the SingleStepTests suite --
    /// captured from a real 386 -- showed the hardware succeeding here. The
    /// reason is that LAR reports what a descriptor says rather than whether
    /// its segment is in memory, and a virtual memory system depends on that:
    /// it has to read the type and privilege of a segment it has paged out in
    /// order to decide whether bringing it back is permitted at all. Only a
    /// genuine segment load requires the present bit.
    /// </summary>
    [Fact]
    public void LarReadsADescriptorEvenWhenItsSegmentIsNotPresent()
    {
        using Machine machine = BuildProtectedMachine();

        // 0x12: the same data descriptor with the present bit clear.
        WriteDescriptor(machine, GdtBase, 3, 0, 0xFFFFF, 0x12, 0xC0);
        machine.Cpu.State.GdtLimit = 0x1F;
        machine.Cpu.State.Eax = 0x18;

        RunInstruction(machine, 0x0F, 0x02, 0xD8);

        Assert.True(machine.Cpu.Flags.Zero);
        Assert.Equal(0x12u, (machine.Cpu.State.Ebx >> 8) & 0xFF);
    }

    [Fact]
    public void LslLeavesTheDestinationAloneForTheNullSelector()
    {
        using Machine machine = BuildProtectedMachine();
        machine.Cpu.State.Eax = 0;
        machine.Cpu.State.Ebx = 0xDEADBEEF;

        RunInstruction(machine, 0x0F, 0x03, 0xD8);

        Assert.False(machine.Cpu.Flags.Zero);
        Assert.Equal(0xDEADBEEFu, machine.Cpu.State.Ebx);
    }

    [Fact]
    public void LarRefusesADescriptorThatIsLessPrivilegedThanTheSelectorAsksFor()
    {
        using Machine machine = BuildProtectedMachine();

        // A ring 0 data segment, queried through a selector requesting ring 3.
        WriteDescriptor(machine, GdtBase, 3, 0, 0xFFFFF, 0x92, 0xC0);
        machine.Cpu.State.GdtLimit = 0x1F;
        machine.Cpu.State.Eax = 0x18 | 3;
        machine.Cpu.State.Ebx = 0xDEADBEEF;

        RunInstruction(machine, 0x0F, 0x02, 0xD8);

        Assert.False(machine.Cpu.Flags.Zero);
        Assert.Equal(0xDEADBEEFu, machine.Cpu.State.Ebx);
    }

    [Fact]
    public void LarAcceptsAConformingCodeSegmentFromAnyPrivilege()
    {
        using Machine machine = BuildProtectedMachine();

        // 0x9C: present, code, conforming. A conforming segment answers to
        // anyone, which is the whole point of the conforming bit.
        WriteDescriptor(machine, GdtBase, 3, 0, 0xFFFFF, 0x9C, 0xC0);
        machine.Cpu.State.GdtLimit = 0x1F;
        machine.Cpu.State.Eax = 0x18 | 3;

        RunInstruction(machine, 0x0F, 0x02, 0xD8);

        Assert.True(machine.Cpu.Flags.Zero);
        Assert.Equal(0x9Cu, (machine.Cpu.State.Ebx >> 8) & 0xFF);
    }

    // ---- the instructions a 386 has that this machine was missing ----------

    [Theory]
    [InlineData(0xD8)] [InlineData(0xD9)] [InlineData(0xDA)] [InlineData(0xDB)]
    [InlineData(0xDC)] [InlineData(0xDD)] [InlineData(0xDE)] [InlineData(0xDF)]
    public void AFloatingPointEscapeAsksForVectorSevenWhenThereIsNoCoprocessor(int opcode)
    {
        using Machine machine = BuildProtectedMachine();

        // CR0.EM is the arrangement of a 386 sold without its 80387: every
        // escape faults to vector 7 and a software library finishes the work.
        machine.Cpu.State.Cr0 |= CpuState.Cr0Emulation;

        // Vector 7 needs somewhere to go: without a gate the fault escalates
        // and the test would be measuring the escalation, not the escape.
        WriteGate(machine, IdtBase, 7, 0x08, 0x3000, type: 0x0E);

        byte vector = 0;
        machine.Cpu.ProcessorException += (v, _, _, _) => vector = v;

        RunInstruction(machine, (byte)opcode, 0xC0);

        // 7 is "device not available", which leaves software somewhere to
        // recover to. 6 -- invalid opcode -- would not: the handler would find
        // nothing to do and the instruction would be retried for ever.
        Assert.Equal(7, vector);
    }

    [Fact]
    public void ATaskSwitchMakesTheNextEscapeTrapEvenWithACoprocessorFitted()
    {
        using Machine machine = BuildProtectedMachine();
        WriteGate(machine, IdtBase, 7, 0x08, 0x3000, type: 0x0E);

        // CR0.TS says the floating-point registers belong to a task other than
        // the running one. The handler saves them, restores this task's, and
        // clears the bit -- which is how an operating system avoids saving
        // eighty bytes of state for the many tasks that never touch the unit.
        machine.Cpu.State.Cr0 |= CpuState.Cr0TaskSwitched;

        byte vector = 0;
        machine.Cpu.ProcessorException += (v, _, _, _) => vector = v;

        RunInstruction(machine, 0xD9, 0xE8);        // fld1

        Assert.Equal(7, vector);
    }

    [Fact]
    public void ThePostReportsTheCoprocessorThatTheChosenProcessorHas()
    {
        // A 486DX carries its floating-point unit on the die, so the escapes
        // are executed rather than trapped: EM stays clear, and bit 1 of the
        // equipment word tells the software that asks the BIOS instead of the
        // processor.
        using var fitted = new Machine();
        fitted.RunInstructions(3000);

        Assert.Equal(0u, fitted.Cpu.State.Cr0 & CpuState.Cr0Emulation);
        Assert.Equal(0x0002, fitted.Bda.EquipmentWord & 0x0002);

        // A 386 is the older arrangement: a second chip in a second socket
        // that this machine was sold without.
        using var bare = new Machine();
        bare.CpuModel = CpuModel.Intel386;
        bare.RunInstructions(3000);

        Assert.Equal(CpuState.Cr0Emulation, bare.Cpu.State.Cr0 & CpuState.Cr0Emulation);
        Assert.Equal(0, bare.Bda.EquipmentWord & 0x0002);
    }

    [Fact]
    public void TheTestRegistersHoldWhatWasWrittenToThem()
    {
        using Machine machine = BuildProtectedMachine();
        machine.Cpu.State.Eax = 0x12345678;

        // 0F 26 F0: MOV TR6, EAX
        RunInstruction(machine, 0x0F, 0x26, 0xF0);
        // 0F 24 F3: MOV EBX, TR6
        RunInstruction(machine, 0x0F, 0x24, 0xF3);

        Assert.Equal(0x12345678u, machine.Cpu.State.Ebx);
    }

    [Fact]
    public void TheTestRegistersAreSeparateFromOneAnother()
    {
        using Machine machine = BuildProtectedMachine();

        machine.Cpu.State.Eax = 0xAAAAAAAA;
        RunInstruction(machine, 0x0F, 0x26, 0xF0);   // TR6 = EAX
        machine.Cpu.State.Eax = 0xBBBBBBBB;
        RunInstruction(machine, 0x0F, 0x26, 0xF8);   // TR7 = EAX

        RunInstruction(machine, 0x0F, 0x24, 0xF3);   // EBX = TR6
        Assert.Equal(0xAAAAAAAAu, machine.Cpu.State.Ebx);

        RunInstruction(machine, 0x0F, 0x24, 0xFB);   // EBX = TR7
        Assert.Equal(0xBBBBBBBBu, machine.Cpu.State.Ebx);
    }

    // ---- expand-down segments ----------------------------------------------
    //
    // These describe a stack. The limit marks the *bottom* of what is valid
    // rather than the top, so the addressable range runs from limit+1 up to
    // the segment's maximum offset and the hole is underneath. Growing such a
    // stack means lowering its limit, which is the whole reason the encoding
    // exists -- and a limit check that ignores the direction bit rejects every
    // ordinary stack access while admitting the ones that should fault.

    [Fact]
    public void AnExpandDownSegmentAcceptsOffsetsAboveItsLimit()
    {
        using Machine machine = BuildProtectedMachine();

        // 0x96: present, data, writable, expand-down. Limit 0x7FFF with byte
        // granularity: valid from 0x8000 up to 0xFFFF.
        WriteDescriptor(machine, GdtBase, 3, 0, 0x7FFF, 0x96, 0x00);
        machine.Cpu.State.GdtLimit = 0x1F;

        machine.Cpu.SetSegment(SegReg.Es, 0x18);

        // Writing at 0x9000 is inside the valid range and must not fault.
        machine.Cpu.WriteMemoryWord(SegReg.Es, 0x9000, 0x1234);
        Assert.Equal(0x1234, machine.Cpu.ReadMemoryWord(SegReg.Es, 0x9000));
    }

    [Fact]
    public void AnExpandDownSegmentRejectsOffsetsBelowItsLimit()
    {
        using Machine machine = BuildProtectedMachine();
        WriteDescriptor(machine, GdtBase, 3, 0, 0x7FFF, 0x96, 0x00);
        machine.Cpu.State.GdtLimit = 0x1F;

        machine.Cpu.SetSegment(SegReg.Es, 0x18);

        // 0x1000 is in the hole below the limit: this is the access the
        // direction bit exists to forbid.
        CpuException fault = Assert.Throws<CpuException>(
            () => machine.Cpu.WriteMemoryWord(SegReg.Es, 0x1000, 0x1234));

        Assert.Equal(13, fault.Vector);
    }

    [Fact]
    public void AnExpandDownSegmentStopsAtItsMaximumOffset()
    {
        using Machine machine = BuildProtectedMachine();
        WriteDescriptor(machine, GdtBase, 3, 0, 0x7FFF, 0x96, 0x00);
        machine.Cpu.State.GdtLimit = 0x1F;

        machine.Cpu.SetSegment(SegReg.Es, 0x18);

        // The B bit is clear, so the ceiling is 0xFFFF; a word at 0xFFFF would
        // run one byte past it. The ceiling comes from that bit and not from
        // the limit, which is what keeps the range from collapsing to nothing.
        Assert.Throws<CpuException>(
            () => machine.Cpu.WriteMemoryWord(SegReg.Es, 0xFFFF, 0x1234));
    }

    [Fact]
    public void AnOrdinarySegmentIsUnaffectedByTheDirectionBit()
    {
        using Machine machine = BuildProtectedMachine();

        // 0x92 is the same descriptor without the expand-down bit: now the
        // limit means what it usually does.
        WriteDescriptor(machine, GdtBase, 3, 0, 0x7FFF, 0x92, 0x00);
        machine.Cpu.State.GdtLimit = 0x1F;

        machine.Cpu.SetSegment(SegReg.Es, 0x18);

        machine.Cpu.WriteMemoryWord(SegReg.Es, 0x1000, 0x5678);
        Assert.Equal(0x5678, machine.Cpu.ReadMemoryWord(SegReg.Es, 0x1000));

        Assert.Throws<CpuException>(
            () => machine.Cpu.WriteMemoryWord(SegReg.Es, 0x9000, 0x1234));
    }

    // ---- far transfers between segments of different width -----------------
    //
    // A far JMP or CALL takes its operand size from the segment it leaves, not
    // the one it enters. The distinction only shows when the two differ -- and
    // they differ in exactly the sequence every DOS extender uses to return to
    // real mode, which is a 32-bit segment jumping to a 16-bit one. Reading the
    // size after CS has moved keeps sixteen bits that should have been dropped,
    // and control lands somewhere nothing put it.

    [Fact]
    public void AFarJumpFromAWideSegmentToANarrowOneKeepsTheWholeOffset()
    {
        using Machine machine = BuildProtectedMachine();

        // Selector 8 is the 32-bit code segment the machine boots into; add a
        // 16-bit one at 0x18 for it to jump to.
        WriteDescriptor(machine, GdtBase, 3, 0, 0xFFFFF, 0x9A, 0x80);   // B clear
        machine.Cpu.State.GdtLimit = 0x1F;

        // EA 78 56 34 12 18 00 : JMP FAR 0018:12345678
        RunInstruction(machine, 0xEA, 0x78, 0x56, 0x34, 0x12, 0x18, 0x00);

        Assert.Equal(0x18, machine.Cpu.State.Cs.Selector);

        // The offset was fetched as 32 bits because the *source* was 32-bit.
        // Masking it to 16 here -- asking the destination instead -- would
        // leave 0x5678 and send control four gigabytes away from the target.
        Assert.Equal(0x12345678u, machine.Cpu.State.Eip);
    }

    [Fact]
    public void AFarCallFromAWideSegmentToANarrowOneKeepsTheWholeOffset()
    {
        using Machine machine = BuildProtectedMachine();
        WriteDescriptor(machine, GdtBase, 3, 0, 0xFFFFF, 0x9A, 0x80);
        machine.Cpu.State.GdtLimit = 0x1F;

        // 9A 78 56 34 12 18 00 : CALL FAR 0018:12345678
        RunInstruction(machine, 0x9A, 0x78, 0x56, 0x34, 0x12, 0x18, 0x00);

        Assert.Equal(0x18, machine.Cpu.State.Cs.Selector);
        Assert.Equal(0x12345678u, machine.Cpu.State.Eip);
    }

    [Fact]
    public void AFarJumpBetweenTwoWideSegmentsIsUnchanged()
    {
        using Machine machine = BuildProtectedMachine();

        // Both 32-bit: the fix must not disturb the ordinary case.
        WriteDescriptor(machine, GdtBase, 3, 0, 0xFFFFF, 0x9A, 0xC0);
        machine.Cpu.State.GdtLimit = 0x1F;

        RunInstruction(machine, 0xEA, 0x78, 0x56, 0x34, 0x12, 0x18, 0x00);

        Assert.Equal(0x12345678u, machine.Cpu.State.Eip);
    }

    // ---- privileged instructions -------------------------------------------
    //
    // HLT, CLTS, LGDT, LIDT, LMSW and the control-register moves are reserved
    // to ring 0: an application that could execute them would be able to stop
    // the processor or describe memory to itself however it liked, and the
    // privilege rings would stop meaning anything. Windows runs applications
    // at ring 3 for exactly this protection.
    //
    // The ring 3 cases are covered by the SingleStepTests suite captured from
    // real hardware (HLT_R3, CLTS_R3, CALL_PRIV), which exercises them far
    // more thoroughly than a hand-built descriptor table here could. What is
    // worth asserting in this file is the other half: that the check does not
    // refuse the kernel as well.

    [Fact]
    public void TheKernelMayStillExecutePrivilegedInstructions()
    {
        using Machine machine = BuildProtectedMachine();

        machine.Cpu.State.Cr0 |= CpuState.Cr0TaskSwitched;
        RunInstruction(machine, 0x0F, 0x06);       // CLTS

        Assert.Equal(0u, machine.Cpu.State.Cr0 & CpuState.Cr0TaskSwitched);
    }

    [Fact]
    public void TheKernelMayStillLoadTheDescriptorTable()
    {
        using Machine machine = BuildProtectedMachine();

        // A six-byte pseudo-descriptor: limit 0x3F, base 0x9000.
        machine.Memory.WriteWord(0x5000, 0x3F);
        machine.Memory.WriteDword(0x5002, 0x9000);

        // 0F 01 /2 with a 32-bit displacement: LGDT [0x5000].
        RunInstruction(machine, 0x0F, 0x01, 0x15, 0x00, 0x50, 0x00, 0x00);

        Assert.Equal(0x9000u, machine.Cpu.State.GdtBase);
        Assert.Equal(0x3F, machine.Cpu.State.GdtLimit);
    }

    /// <summary>
    /// The indirect form of the far jump, through a pointer in memory. This is
    /// the instruction with which WIN386 enters its 32-bit kernel: a 16-bit
    /// segment, a 66h prefix, and a six-byte pointer whose offset lies above
    /// 0x80000000. Reading the operand size after CS had already moved to the
    /// 32-bit destination truncated that offset to sixteen bits, and Windows
    /// jumped into DOS's data instead of into itself.
    /// </summary>
    [Fact]
    public void AnIndirectFarJumpFromANarrowSegmentKeepsAWideOffset()
    {
        using Machine machine = BuildProtectedMachine();

        // Selector 0x18: a 16-bit code segment to jump from. Selector 0x20: a
        // 32-bit flat one to land in.
        WriteDescriptor(machine, GdtBase, 3, 0, 0xFFFFF, 0x9A, 0x80);   // B clear
        WriteDescriptor(machine, GdtBase, 4, 0, 0xFFFFF, 0x9A, 0xC0);   // B set
        machine.Cpu.State.GdtLimit = 0x27;

        // The six-byte pointer at 0x6000: offset 0x8028242C, selector 0x20.
        machine.Memory.WriteDword(0x6000, 0x8028242C);
        machine.Memory.WriteWord(0x6004, 0x0020);

        machine.Cpu.SetSegment(SegReg.Cs, 0x18);
        machine.Cpu.SetSegment(SegReg.Ds, 0x10);
        machine.Cpu.State.Eip = 0x1000;

        // 66 FF 2E 00 60 : JMP FAR dword ptr [0x6000], 16-bit addressing.
        byte[] code = { 0x66, 0xFF, 0x2E, 0x00, 0x60 };
        for (int i = 0; i < code.Length; i++) machine.Memory.WriteByte(0x1000 + (uint)i, code[i]);

        machine.Cpu.Step();

        Assert.Equal(0x20, machine.Cpu.State.Cs.Selector);
        Assert.Equal(0x8028242Cu, machine.Cpu.State.Eip);
    }

    /// <summary>
    /// A far return to a less privileged caller: the caller's stack pointer
    /// follows the return address on the kernel stack, the privilege level
    /// changes, and any data segment the kernel left in a register that the
    /// caller may not touch is dropped. This is how KRNL386 hands control back
    /// to an application after a call through a gate, and the hardware suite
    /// has no case for it -- its far returns are all same-privilege.
    /// </summary>
    [Fact]
    public void AFarReturnToALessPrivilegedCallerSwitchesStackAndDropsTheKernelsDataSegments()
    {
        using Machine machine = BuildProtectedMachine();
        AddUserModeAndTss(machine);

        // The frame the gate entry left: EIP, CS, then the caller's ESP and
        // SS, each a dword because the kernel segment is 32-bit.
        machine.Memory.WriteDword(0x8000, 0x1200);
        machine.Memory.WriteDword(0x8004, 0x23);      // ring 3 code
        machine.Memory.WriteDword(0x8008, 0x7000);
        machine.Memory.WriteDword(0x800C, 0x2B);      // ring 3 data

        machine.Cpu.SetSegment(SegReg.Ds, 0x10);      // the kernel's own data segment

        RunInstruction(machine, 0xCB);                // RETF

        Assert.Equal(3, machine.Cpu.State.Cpl);
        Assert.Equal(0x23, machine.Cpu.State.Cs.Selector);
        Assert.Equal(0x1200u, machine.Cpu.State.Eip);
        Assert.Equal(0x2B, machine.Cpu.State.Ss.Selector);
        Assert.Equal(0x7000u, machine.Cpu.State.Esp);
        Assert.Equal(0, machine.Cpu.State.Ds.Selector);   // DPL 0: not for an application
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FarReturnDiscardsCopiedParametersBeforeRestoringTheOuterStack(bool wide)
    {
        using Machine machine = BuildProtectedMachine();
        AddUserModeAndTss(machine);
        WriteGate(machine, IdtBase, 12, 0x08, 0x6000, type: 0x0E);
        WriteGate(machine, IdtBase, 13, 0x08, 0x6100, type: 0x0E);
        uint slotSize = wide ? 4u : 2u;
        void WriteSlot(uint address, uint value)
        {
            if (wide)
            {
                machine.Memory.WriteDword(address, value);
            }
            else
            {
                machine.Memory.WriteWord(address, (ushort)value);
            }
        }
        WriteSlot(0x8000, 0x4309);
        WriteSlot(0x8000 + slotSize, 0x23);
        // The IDE probe in SYSDETMG passes port 0170h in four copied bytes.
        // These are arguments, not the saved outer SP and SS.
        machine.Memory.WriteDword(0x8000 + 2 * slotSize, 0x170);
        WriteSlot(0x8004 + 2 * slotSize, 0x7000);
        WriteSlot(0x8004 + 3 * slotSize, 0x2B);
        byte? raisedException = null;
        machine.Cpu.ProcessorException += (vector, _, _, _) => raisedException = vector;

        RunInstruction(machine, wide ? [0xCA, 4, 0] : [0x66, 0xCA, 4, 0]);

        Assert.Null(raisedException);
        Assert.Equal(3, machine.Cpu.State.Cpl);
        Assert.Equal(0x23, machine.Cpu.State.Cs.Selector);
        Assert.Equal(0x4309u, machine.Cpu.State.Eip);
        Assert.Equal(0x2B, machine.Cpu.State.Ss.Selector);
        Assert.Equal(0x7004u, machine.Cpu.State.Esp);
    }

    [Fact]
    public void ARejectedOuterStackLeavesTheFarReturnInItsOriginalCodeSegment()
    {
        using Machine machine = BuildProtectedMachine();
        AddUserModeAndTss(machine);
        WriteGate(machine, IdtBase, 13, 0x08, 0x6000, type: 0x0E);
        machine.Memory.WriteDword(0x8000, 0x4309);
        machine.Memory.WriteDword(0x8004, 0x23);
        machine.Memory.WriteDword(0x8008, 0x7000);
        machine.Memory.WriteDword(0x800C, 0x23); // code cannot be used as SS
        ushort? faultCodeSegment = null;
        byte? faultPrivilege = null;
        machine.Cpu.ProcessorException += (_, _, _, _) =>
        {
            faultCodeSegment = machine.Cpu.State.Cs.Selector;
            faultPrivilege = machine.Cpu.State.Cpl;
        };

        RunInstruction(machine, 0xCB);

        Assert.Equal((ushort)8, faultCodeSegment);
        Assert.Equal((byte)0, faultPrivilege);
        Assert.Equal(0x6000u, machine.Cpu.State.Eip);
        Assert.Equal(0x7FF0u, machine.Cpu.State.Esp);
        Assert.Equal(8u, machine.Memory.ReadDword(0x7FF8));
        Assert.Equal(0x4309u, machine.Memory.ReadDword(0x8000));
    }

    [Theory]
    [InlineData(false, 0x1A, 11, 0x18)]
    [InlineData(true, 0x1A, 11, 0x18)]
    [InlineData(false, 0x12, 13, 0x18)]
    [InlineData(true, 0x12, 13, 0x18)]
    [InlineData(false, 0x9A, 13, 0)]
    [InlineData(true, 0x9A, 13, 0)]
    public void FarReturnValidatesTheDescriptorBeforeItsLimit(
        bool wide, byte access, byte expectedVector, uint expectedError)
    {
        using Machine machine = BuildProtectedMachine();
        // SETUPX returns to a discarded segment. Windows leaves a small
        // placeholder limit in its absent descriptor and relies on #NP to
        // reload the code. Checking that limit first incorrectly delivers #GP.
        WriteDescriptor(machine, GdtBase, 3, 0, 0x0D97, access);
        machine.Cpu.State.GdtLimit = 0x1F;
        WriteGate(machine, IdtBase, 11, 0x08, 0x6000, type: 0x0E);
        WriteGate(machine, IdtBase, 13, 0x08, 0x6100, type: 0x0E);
        if (wide)
        {
            machine.Memory.WriteDword(0x8000, 0x1EF4);
            machine.Memory.WriteDword(0x8004, 0x18);
        }
        else
        {
            machine.Memory.WriteWord(0x8000, 0x1EF4);
            machine.Memory.WriteWord(0x8002, 0x18);
        }
        byte? vector = null;
        machine.Cpu.ProcessorException += (raisedVector, _, _, _) => vector = raisedVector;

        RunInstruction(machine, wide ? [0xCA, 8, 0] : [0x66, 0xCA, 8, 0]);

        Assert.Equal(expectedVector, vector);
        Assert.Equal(expectedVector == 11 ? 0x6000u : 0x6100u, machine.Cpu.State.Eip);
        Assert.Equal(0x7FF0u, machine.Cpu.State.Esp);
        Assert.Equal(expectedError, machine.Memory.ReadDword(0x7FF0));
        Assert.Equal(0x1000u, machine.Memory.ReadDword(0x7FF4)); // faulting RETF
        Assert.Equal(8u, machine.Memory.ReadDword(0x7FF8));      // original CS
        Assert.Equal(0x1EF4, machine.Memory.ReadWord(0x8000));   // frame is retryable
    }

    // ---- faults during delivery ----------------------------------------------
    //
    // Reaching a handler can itself fault. The architecture has an answer for
    // every case, and an emulator that lets the second fault escape as a host
    // exception dies on conditions a real machine survives -- which is what
    // Windows' VMM did to this one, through a vector whose gate was not
    // present.

    [Fact]
    public void ABenignFaultDuringDeliveryIsDeliveredInstead()
    {
        using Machine machine = BuildProtectedMachine();
        WriteGate(machine, IdtBase, 11, 0x08, 0x6000, type: 0x0E);    // #NP is served
        WriteGate(machine, IdtBase, 0x40, 0x08, 0x6100, type: 0x0E);
        machine.Memory.WriteByte(IdtBase + 0x40 * 8 + 5, 0x0E); // valid type, P=0

        // INT 40h: its gate is absent, so delivering it raises #NP. A software
        // interrupt is benign, and the #NP is simply what arrives.
        RunInstruction(machine, 0xCD, 0x40);

        Assert.Equal(0x6000u, machine.Cpu.State.Eip);
        Assert.Equal(0x40u * 8 + 2, machine.Memory.ReadDword(machine.Cpu.State.Esp));   // IDT bit, no EXT
    }

    [Fact]
    public void AContributoryFaultWhoseGateIsMissingBecomesADoubleFault()
    {
        using Machine machine = BuildProtectedMachine();
        WriteGate(machine, IdtBase, 8, 0x08, 0x5000, type: 0x0E);     // #DF is served, #GP is not

        // MOV DS,AX with a selector past the table: #GP. Delivering it finds
        // an invalid gate: #GP. Two contributory faults in a row are a double fault.
        machine.Cpu.State.Eax = 0x40;
        RunInstruction(machine, 0x8E, 0xD8);

        Assert.Equal(0x5000u, machine.Cpu.State.Eip);
        Assert.Equal(0x08, machine.Cpu.State.Cs.Selector);
        Assert.Equal(0u, machine.Memory.ReadDword(machine.Cpu.State.Esp));   // #DF always pushes zero
    }

    [Fact]
    public void AFaultWhoseDoubleFaultGateIsAlsoMissingResetsTheProcessor()
    {
        using Machine machine = BuildProtectedMachine();
        (byte first, byte second) seen = (0, 0);
        machine.Cpu.TripleFault += (a, b) => seen = (a, b);

        machine.Cpu.State.Eax = 0x40;
        RunInstruction(machine, 0x8E, 0xD8);          // #GP, no gate for it, none for #DF either

        Assert.Equal(((byte)13, (byte)13), seen);
        // Back at the reset vector, the way a real machine comes back from a shutdown.
        Assert.Equal(0xF000, machine.Cpu.State.Cs.Selector);
        Assert.Equal(0xFFF0u, machine.Cpu.State.Eip);
    }
}
