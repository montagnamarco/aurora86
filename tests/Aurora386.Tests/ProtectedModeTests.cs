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
    public void ProtectedModeDoesNotDisturbRealModeSegmentLoading()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);

        // Still in real mode: a selector is a paragraph address, not an index.
        machine.Cpu.SetSegment(SegReg.Es, 0x1234);

        Assert.Equal(0x12340u, machine.Cpu.State.Es.Base);
        Assert.Equal(0xFFFFu, machine.Cpu.State.Es.Limit);
    }
}
