using Aurora386.Core;
using Aurora386.Core.Cpu;

namespace Aurora386.Tests;

/// <summary>
/// Virtual 8086 mode runs an unmodified DOS program inside protection.
///
/// The program sees the flat 8086 world it was written for -- segment times
/// sixteen, 64 KB limits, interrupt vectors at the bottom of memory -- while
/// the monitor around it keeps paging and privilege. Every instruction that
/// would let the program notice, or escape, traps back to ring 0. EMM386 uses
/// this to fabricate expanded memory; Windows 3.1 uses it to run several DOS
/// boxes at once.
/// </summary>
public class VirtualModeTests
{
    private const uint GdtBase = 0x1000;
    private const uint IdtBase = 0x2000;
    private const uint TssBase = 0x4000;
    private const uint MonitorStackTop = 0x8000;
    private const uint TaskStackTop = 0x9000;

    private static void WriteDescriptor(Machine machine, int index, uint baseAddress,
                                        uint limit, byte access, byte granularity)
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

    private static void WriteGate(Machine machine, int vector, ushort selector,
                                  uint offset, byte type, byte dpl)
    {
        uint address = IdtBase + (uint)(vector * 8);

        machine.Memory.WriteWord(address, (ushort)(offset & 0xFFFF));
        machine.Memory.WriteWord(address + 2, selector);
        machine.Memory.WriteByte(address + 4, 0);
        machine.Memory.WriteByte(address + 5, (byte)(0x80 | (dpl << 5) | type));
        machine.Memory.WriteWord(address + 6, (ushort)(offset >> 16));
    }

    /// <summary>
    /// A machine set up the way a monitor sets one up: flat ring 0 segments,
    /// a task state segment holding the monitor's stack, and an IDT.
    /// </summary>
    private static Machine BuildMonitor()
    {
        var machine = new Machine();
        machine.RunInstructions(200);

        WriteDescriptor(machine, 1, 0, 0xFFFFF, 0x9A, 0xC0);   // ring 0 code
        WriteDescriptor(machine, 2, 0, 0xFFFFF, 0x92, 0xC0);   // ring 0 data
        WriteDescriptor(machine, 3, TssBase, 0x67, 0x89, 0x00);

        machine.Cpu.State.GdtBase = GdtBase;
        machine.Cpu.State.GdtLimit = 0x1F;
        machine.Cpu.State.IdtBase = IdtBase;
        machine.Cpu.State.IdtLimit = 0x7FF;

        machine.Memory.WriteDword(TssBase + 4, MonitorStackTop);
        machine.Memory.WriteDword(TssBase + 8, 0x10);

        machine.Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable;
        machine.Cpu.State.TaskRegister = 0x18;
        machine.Cpu.State.Halted = false;

        machine.Cpu.SetSegment(SegReg.Cs, 0x08);
        machine.Cpu.SetSegment(SegReg.Ss, 0x10);
        machine.Cpu.State.Esp = MonitorStackTop;

        return machine;
    }

    /// <summary>
    /// Builds the stack frame that IRET turns into a running V86 task, and
    /// executes the IRET. This is exactly how a monitor launches a DOS box.
    /// </summary>
    private static void EnterTask(Machine machine, ushort cs, uint eip,
                                  uint eflags = 0x00020202)
    {
        // Pushed in the order IRET pops them: GS, FS, DS, ES, SS, ESP,
        // EFLAGS, CS, EIP -- so the last pushed is the first popped.
        Push(machine, 0);              // GS
        Push(machine, 0);              // FS
        Push(machine, 0);              // DS
        Push(machine, 0);              // ES
        Push(machine, 0x0900);         // SS
        Push(machine, TaskStackTop & 0xFFFF);
        Push(machine, eflags);
        Push(machine, cs);
        Push(machine, eip);

        // IRETD from the flat 32-bit code segment.
        uint address = machine.Cpu.State.Cs.Base + machine.Cpu.State.Eip;
        machine.Memory.WriteByte(address, 0xCF);
        machine.Cpu.Step();
    }

    private static void Push(Machine machine, uint value)
    {
        machine.Cpu.State.Esp -= 4;
        machine.Memory.WriteDword(machine.Cpu.State.Ss.Base + machine.Cpu.State.Esp, value);
    }

    [Fact]
    public void IretWithTheVirtualModeFlagStartsATask()
    {
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;

        EnterTask(machine, cs: 0x0800, eip: 0x0100);

        Assert.True(machine.Cpu.State.Virtual8086Mode);
        Assert.Equal(0x0100u, machine.Cpu.State.Eip);
        Assert.Equal((ushort)0x0800, machine.Cpu.State.Cs.Selector);
    }

    [Fact]
    public void SegmentsInATaskAreAddressedTheRealModeWay()
    {
        // This is the whole reason virtual 8086 mode exists: the program
        // computes the addresses it has always computed.
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;

        EnterTask(machine, cs: 0x0800, eip: 0x0100);

        Assert.Equal(0x8000u, machine.Cpu.State.Cs.Base);      // 0x0800 * 16
        Assert.Equal(0x9000u, machine.Cpu.State.Ss.Base);      // 0x0900 * 16
        Assert.Equal(0xFFFFu, machine.Cpu.State.Cs.Limit);
    }

    [Fact]
    public void ATaskRunsAtRingThreeWhateverItsSelectorsSay()
    {
        // A V86 task's selectors are 8086 segment numbers and their low two
        // bits mean nothing. The privilege has to come from the mode itself,
        // or a task whose code segment happened to be even would find itself
        // running as the kernel.
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;

        EnterTask(machine, cs: 0x0800, eip: 0x0100);   // selector's low bits are 0

        Assert.Equal(3, (int)machine.Cpu.State.Cpl);
    }

    [Fact]
    public void ATaskCannotStopTheMachine()
    {
        // The task believes it owns the computer. It does not: the monitor
        // has other tasks to run, and must stay alive to schedule them.
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        WriteGate(machine, 13, 0x08, 0x5000, type: 0x0E, dpl: 0);

        EnterTask(machine, cs: 0x0800, eip: 0x0100);

        // HLT at the task's next instruction.
        machine.Memory.WriteByte(0x8000 + 0x0100, 0xF4);
        machine.Cpu.Step();

        // Trapped into the monitor instead of halting.
        Assert.False(machine.Cpu.State.Halted);
        Assert.False(machine.Cpu.State.Virtual8086Mode);
        Assert.Equal(0x5000u, machine.Cpu.State.Eip);
    }

    [Fact]
    public void PortAccessTrapsWhenTheTaskIsNotTrustedWithIt()
    {
        // A monitor marks a port in the task state segment's I/O permission
        // bitmap so that it can watch what the task does with the hardware --
        // and pretend, where it wants to. This is how EMM386 lets a program
        // think it is programming a memory board that is not there, and how
        // Windows keeps its own copy of the display adapter's registers. In a
        // virtual 8086 task the bitmap decides, whatever IOPL says.
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        WriteGate(machine, 13, 0x08, 0x5000, type: 0x0E, dpl: 0);
        GiveTaskAnIoMap(machine);
        ForbidPort(machine, 0x60);

        EnterTask(machine, cs: 0x0800, eip: 0x0100, eflags: 0x00020002);

        machine.Memory.WriteByte(0x8000 + 0x0100, 0xE4);   // IN AL, imm8
        machine.Memory.WriteByte(0x8000 + 0x0101, 0x60);
        machine.Cpu.Step();

        Assert.False(machine.Cpu.State.Virtual8086Mode);
        Assert.Equal(0x5000u, machine.Cpu.State.Eip);
    }

    [Fact]
    public void PortAccessIsAllowedWhenTheMonitorPermitsIt()
    {
        // With the I/O privilege level at 3 the task may talk to the hardware
        // directly. A monitor grants this to a task it trusts, because
        // trapping every port access is expensive.
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        WriteGate(machine, 13, 0x08, 0x5000, type: 0x0E, dpl: 0);
        GiveTaskAnIoMap(machine);   // a map with every port allowed

        EnterTask(machine, cs: 0x0800, eip: 0x0100, eflags: 0x00023202);

        machine.Memory.WriteByte(0x8000 + 0x0100, 0xE4);
        machine.Memory.WriteByte(0x8000 + 0x0101, 0x60);
        machine.Cpu.Step();

        // Still in the task: no trap happened.
        Assert.True(machine.Cpu.State.Virtual8086Mode);
    }

    [Fact]
    public void AnInterruptFromATaskReachesTheMonitorOnItsOwnStack()
    {
        // The task's stack is a real-mode one somewhere in the first
        // megabyte, and the monitor must not run on it.
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        WriteGate(machine, 0x21, 0x08, 0x6000, type: 0x0E, dpl: 3);

        EnterTask(machine, cs: 0x0800, eip: 0x0100);
        machine.Cpu.RaiseInterruptForTest(0x21);

        Assert.False(machine.Cpu.State.Virtual8086Mode);
        Assert.Equal(0, (int)machine.Cpu.State.Cpl);
        Assert.Equal((ushort)0x10, machine.Cpu.State.Ss.Selector);
        Assert.Equal(0x6000u, machine.Cpu.State.Eip);
    }

    [Fact]
    public void TheTasksFlagsOnTheStackRememberItWasVirtual()
    {
        // Without the VM bit in the saved flags the monitor's IRET would
        // return to ordinary protected-mode code and the task would never run
        // again.
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        WriteGate(machine, 0x21, 0x08, 0x6000, type: 0x0E, dpl: 3);

        EnterTask(machine, cs: 0x0800, eip: 0x0100);
        machine.Cpu.RaiseInterruptForTest(0x21);

        // EFLAGS sits third from the top of the frame, above CS and EIP.
        uint savedFlags = machine.Memory.ReadDword(
            machine.Cpu.State.Ss.Base + machine.Cpu.State.Esp + 8);

        Assert.Equal(1u << 17, savedFlags & (1u << 17));
    }

    [Fact]
    public void TheTasksDataSegmentsAreHandedToTheMonitor()
    {
        // The monitor needs to know what the task had loaded -- to work out
        // what an instruction was trying to reach, it has to resolve the same
        // segments the task was using.
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        WriteGate(machine, 0x21, 0x08, 0x6000, type: 0x0E, dpl: 3);

        EnterTask(machine, cs: 0x0800, eip: 0x0100);

        machine.Cpu.SetSegment(SegReg.Ds, 0x1234);
        machine.Cpu.SetSegment(SegReg.Es, 0x5678);
        machine.Cpu.RaiseInterruptForTest(0x21);

        // The four data segments are at the bottom of the frame: ES, DS, FS,
        // GS reading upwards from just under the saved SS.
        uint frame = machine.Cpu.State.Ss.Base + machine.Cpu.State.Esp;
        // Frame from the top: EIP, CS, EFLAGS, ESP, SS, then ES, DS, FS, GS.
        Assert.Equal(0x5678u, machine.Memory.ReadDword(frame + 20));   // ES
        Assert.Equal(0x1234u, machine.Memory.ReadDword(frame + 24));   // DS
    }

    [Fact]
    public void TheMonitorsSegmentsAreNotLeftHoldingTaskValues()
    {
        // A V86 task's DS is a segment number, not a selector. Left loaded,
        // the monitor would be one careless instruction away from
        // dereferencing something meaningless.
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        WriteGate(machine, 0x21, 0x08, 0x6000, type: 0x0E, dpl: 3);

        EnterTask(machine, cs: 0x0800, eip: 0x0100);
        machine.Cpu.SetSegment(SegReg.Ds, 0x1234);
        machine.Cpu.RaiseInterruptForTest(0x21);

        Assert.Equal((ushort)0, machine.Cpu.State.Ds.Selector);
    }

    [Fact]
    public void ATaskCannotPromoteItselfByReturningIntoVirtualMode()
    {
        // Only ring 0 may start a V86 task. If ring 3 could, any program
        // could grant itself a mode where it chooses its own privileges.
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        WriteGate(machine, 13, 0x08, 0x5000, type: 0x0E, dpl: 0);
        machine.Cpu.State.Cpl = 3;

        Push(machine, 0x00020202);   // EFLAGS with the VM bit
        Push(machine, 0x0800);
        Push(machine, 0x0100);

        uint address = machine.Cpu.State.Cs.Base + machine.Cpu.State.Eip;
        machine.Memory.WriteByte(address, 0xCF);
        machine.Cpu.Step();

        Assert.False(machine.Cpu.State.Virtual8086Mode);
    }
    // ---- IRET inside the task --------------------------------------------
    //
    // IRET is IOPL-sensitive in virtual 8086 mode like PUSHF, POPF and INT n.
    // The hardware suite covers those three; it has no IRET case, and IRET is
    // the one WIN386 actually tripped over: the monitor ran DOS's INT 1Ah
    // handler in the task, the handler's IRET was taken down the protected
    // path, and a real-mode paragraph was loaded as a selector -- #GP with
    // that paragraph as the error code, which no monitor is written to expect.

    [Fact]
    public void AnInterruptReturnInATaskBelowIoplThreeTrapsToTheMonitorWithNoErrorCode()
    {
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        WriteGate(machine, 13, 0x08, 0x5000, type: 0x0E, dpl: 0);

        // The default flags carry IOPL 0.
        EnterTask(machine, cs: 0x0800, eip: 0x0100);

        // A plausible return frame on the task's stack. It matters that CS in
        // it is a non-zero paragraph: were IRET to take the protected-mode
        // path by mistake it would load 0x0800 as a selector and fault with
        // 0x0800 as the error code -- an empty frame would give it a null
        // selector, whose fault code is also zero, and hide the difference.
        machine.Cpu.State.Esp = 0x9000 - 6;
        uint frame = machine.Cpu.State.Ss.Base + machine.Cpu.State.Esp;
        machine.Memory.WriteWord(frame, 0x0200);
        machine.Memory.WriteWord(frame + 2, 0x0800);
        machine.Memory.WriteWord(frame + 4, 0x0202);

        machine.Memory.WriteByte(0x8000 + 0x0100, 0xCF);   // IRET
        machine.Cpu.Step();

        // Into the monitor's #GP handler, with the error code the monitor
        // uses to recognise an IOPL trap: zero, not a segment value.
        Assert.False(machine.Cpu.State.Virtual8086Mode);
        Assert.Equal(0x5000u, machine.Cpu.State.Eip);
        Assert.Equal(0u, machine.Memory.ReadDword(machine.Cpu.State.Ss.Base + machine.Cpu.State.Esp));
    }

    [Fact]
    public void AnInterruptReturnInATaskAtIoplThreeStaysInTheTaskAndCannotLowerIopl()
    {
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;

        // IOPL 3: bits 12 and 13 set, so the instruction runs inside the task.
        EnterTask(machine, cs: 0x0800, eip: 0x0100, eflags: 0x00023202);

        // A 16-bit return frame on the task's own stack: IP, CS, FLAGS. The
        // flags word asks for IOPL 0, which a ring 3 task may not have.
        machine.Cpu.State.Esp = 0x9000 - 6;
        uint frame = machine.Cpu.State.Ss.Base + machine.Cpu.State.Esp;
        machine.Memory.WriteWord(frame, 0x0200);        // IP
        machine.Memory.WriteWord(frame + 2, 0x0800);    // CS
        machine.Memory.WriteWord(frame + 4, 0x0202);    // FLAGS, IOPL 0

        machine.Memory.WriteByte(0x8000 + 0x0100, 0xCF);   // IRET
        machine.Cpu.Step();

        Assert.True(machine.Cpu.State.Virtual8086Mode);
        Assert.Equal(0x0800, machine.Cpu.State.Cs.Selector);
        Assert.Equal(0x8000u, machine.Cpu.State.Cs.Base);      // a paragraph, not a descriptor
        Assert.Equal(0x0200u, machine.Cpu.State.Eip);
        Assert.Equal(0x9000u, machine.Cpu.State.Esp);
        Assert.Equal(3u, (machine.Cpu.Flags.Value >> 12) & 3);   // IOPL kept
    }

    [Fact]
    public void ArplInATaskIsAnUndefinedOpcodeThatTrapsToTheMonitor()
    {
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        WriteGate(machine, 6, 0x08, 0x6000, type: 0x0E, dpl: 0);

        // Windows' monitor plants 63h in its V86 stubs as a breakpoint and
        // takes the #UD as its cue to step back in. Executed as ARPL instead,
        // the stub would run on into bytes that are not code.
        EnterTask(machine, cs: 0x0800, eip: 0x0100, eflags: 0x00023202);
        machine.Cpu.State.Esp = 0x9000;
        machine.Memory.WriteByte(0x8000 + 0x0100, 0x63);   // ARPL [BX+SI],AX
        machine.Memory.WriteByte(0x8000 + 0x0101, 0x00);
        machine.Cpu.Step();

        Assert.False(machine.Cpu.State.Virtual8086Mode);
        Assert.Equal(0x6000u, machine.Cpu.State.Eip);
        // #UD carries no error code: the task's return address is on top.
        uint top = machine.Cpu.State.Ss.Base + machine.Cpu.State.Esp;
        Assert.Equal(0x0100u, machine.Memory.ReadDword(top));
        Assert.Equal(0x0800u, machine.Memory.ReadDword(top + 4));
    }

    [Fact]
    public void FlagsPushedByAnInterruptInsideTheMonitorNoLongerCarryTheTaskVmBit()
    {
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        WriteGate(machine, 6, 0x08, 0x6000, type: 0x0E, dpl: 0);
        WriteGate(machine, 0x21, 0x08, 0x7000, type: 0x0E, dpl: 0);

        // Into the task, and straight back out through a #UD.
        EnterTask(machine, cs: 0x0800, eip: 0x0100, eflags: 0x00023202);
        machine.Memory.WriteByte(0x8000 + 0x0100, 0x63);   // ARPL
        machine.Memory.WriteByte(0x8000 + 0x0101, 0x00);
        machine.Cpu.Step();
        Assert.False(machine.Cpu.State.Virtual8086Mode);

        // The image pushed on the way out says VM: that is how the monitor's
        // IRET knows where to go back to.
        uint trap = machine.Cpu.State.Ss.Base + machine.Cpu.State.Esp;
        Assert.NotEqual(0u, machine.Memory.ReadDword(trap + 8) & (1u << 17));

        // Now the monitor's own code raises a software interrupt. Its flags
        // image must say protected mode: Windows' monitor reads this bit to
        // tell a service call of its own from a fault inside the task.
        machine.Memory.WriteByte(0x6000, 0xCD);   // INT 21h
        machine.Memory.WriteByte(0x6001, 0x21);
        machine.Cpu.Step();

        Assert.Equal(0x7000u, machine.Cpu.State.Eip);
        uint call = machine.Cpu.State.Ss.Base + machine.Cpu.State.Esp;
        Assert.Equal(0x6002u, machine.Memory.ReadDword(call));
        Assert.Equal(0u, machine.Memory.ReadDword(call + 8) & (1u << 17));
    }

    // --- the I/O permission bitmap -------------------------------------------

    private static void GiveTaskAnIoMap(Machine machine)
    {
        // A bigger task state segment with the map right after the fixed part.
        WriteDescriptor(machine, 3, TssBase, 0x2067, 0x89, 0x00);
        machine.Memory.WriteWord(TssBase + 0x66, 0x68);
        for (uint i = 0; i < 0x2000; i++) machine.Memory.WriteByte(TssBase + 0x68 + i, 0);
    }

    private static void ForbidPort(Machine machine, int port)
    {
        uint address = TssBase + 0x68 + (uint)(port >> 3);
        machine.Memory.WriteByte(address, (byte)(machine.Memory.ReadByte(address) | (1 << (port & 7))));
    }

    private static void RunOutInTask(Machine machine, uint eflags)
    {
        WriteGate(machine, 13, 0x08, 0x5000, type: 0x0E, dpl: 0);
        EnterTask(machine, cs: 0x0800, eip: 0x0100, eflags: eflags);
        machine.Cpu.State.Esp = 0x9000;
        machine.Cpu.State.Edx = 0x03C4;
        machine.Cpu.State.Eax = 0x0002;
        machine.Memory.WriteByte(0x8000 + 0x0100, 0xEE);   // OUT DX,AL
        machine.Cpu.Step();
    }

    [Fact]
    public void ATaskAtIoplThreeStillFaultsOnAPortTheMapForbids()
    {
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        GiveTaskAnIoMap(machine);
        ForbidPort(machine, 0x3C4);

        RunOutInTask(machine, eflags: 0x00023202);   // IOPL 3

        Assert.False(machine.Cpu.State.Virtual8086Mode);
        Assert.Equal(0x5000u, machine.Cpu.State.Eip);
        Assert.Equal(0u, machine.Memory.ReadDword(machine.Cpu.State.Ss.Base + machine.Cpu.State.Esp));   // #GP(0)
    }

    [Fact]
    public void ATaskBelowIoplThreeMayUseAPortTheMapAllows()
    {
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        GiveTaskAnIoMap(machine);
        ForbidPort(machine, 0x3C5);   // the neighbour, not the port used

        RunOutInTask(machine, eflags: 0x00020202);   // IOPL 0

        Assert.True(machine.Cpu.State.Virtual8086Mode);
        Assert.Equal(0x0101u, machine.Cpu.State.Eip);
        Assert.Equal(0x02, machine.Io.ReadByte(0x3C4));   // the write went through
    }

    [Fact]
    public void AMapTooShortForThePortForbidsIt()
    {
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        // The default segment ends at 0x67: no room for a map at all.

        RunOutInTask(machine, eflags: 0x00023202);   // IOPL 3

        Assert.False(machine.Cpu.State.Virtual8086Mode);
        Assert.Equal(0x5000u, machine.Cpu.State.Eip);
    }

    [Fact]
    public void AWordAccessNeedsBothBitsClear()
    {
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        GiveTaskAnIoMap(machine);
        ForbidPort(machine, 0x3C5);   // second byte of a word at 3C4

        WriteGate(machine, 13, 0x08, 0x5000, type: 0x0E, dpl: 0);
        EnterTask(machine, cs: 0x0800, eip: 0x0100, eflags: 0x00023202);
        machine.Cpu.State.Esp = 0x9000;
        machine.Cpu.State.Edx = 0x03C4;
        machine.Cpu.State.Eax = 0x0302;
        machine.Memory.WriteByte(0x8000 + 0x0100, 0xEF);   // OUT DX,AX
        machine.Cpu.Step();

        Assert.False(machine.Cpu.State.Virtual8086Mode);
        Assert.Equal(0x5000u, machine.Cpu.State.Eip);
    }

    // --- far calls into segments that are not there ---------------------------

    [Fact]
    public void AFarCallToASegmentNotPresentFaultsWithTheStackUntouched()
    {
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        WriteGate(machine, 11, 0x08, 0x5000, type: 0x0E, dpl: 0);
        machine.Cpu.State.GdtLimit = 0x2F;   // room for the fifth descriptor
        WriteDescriptor(machine, 4, 0x7000, 0xFFFF, 0x1A, 0x40);   // ring 0 code, P = 0

        // CALL FAR 0020:00000000 at the monitor's current position; the code
        // segment is 32-bit, so the offset in the instruction is a dword.
        machine.Memory.WriteByte(0x1000, 0x9A);
        machine.Memory.WriteDword(0x1001, 0x00000000);
        machine.Memory.WriteWord(0x1005, 0x0020);
        uint before = machine.Cpu.State.Esp;
        machine.Cpu.Step();

        Assert.Equal(0x5000u, machine.Cpu.State.Eip);
        // The #NP frame sits directly on the caller's stack: error code, then
        // EIP, CS and EFLAGS of the faulting instruction -- nothing else.
        Assert.Equal(before - 16, machine.Cpu.State.Esp);
        Assert.Equal(0x20u, machine.Memory.ReadDword(machine.Cpu.State.Ss.Base + machine.Cpu.State.Esp));
        Assert.Equal(0x1000u, machine.Memory.ReadDword(machine.Cpu.State.Ss.Base + machine.Cpu.State.Esp + 4));
    }

    [Fact]
    public void AnIndirectFarCallToASegmentNotPresentFaultsWithTheStackUntouched()
    {
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        WriteGate(machine, 11, 0x08, 0x5000, type: 0x0E, dpl: 0);
        machine.Cpu.State.GdtLimit = 0x2F;   // room for the fifth descriptor
        WriteDescriptor(machine, 4, 0x7000, 0xFFFF, 0x1A, 0x40);

        // CALL FAR [0x6000], the pointer 0020:00000000 stored there.
        machine.Memory.WriteByte(0x1000, 0xFF);
        machine.Memory.WriteByte(0x1001, 0x1D);
        machine.Memory.WriteDword(0x1002, 0x6000);
        machine.Memory.WriteDword(0x6000, 0);
        machine.Memory.WriteWord(0x6004, 0x0020);
        uint before = machine.Cpu.State.Esp;
        machine.Cpu.Step();

        Assert.Equal(0x5000u, machine.Cpu.State.Eip);
        Assert.Equal(before - 16, machine.Cpu.State.Esp);
        Assert.Equal(0x20u, machine.Memory.ReadDword(machine.Cpu.State.Ss.Base + machine.Cpu.State.Esp));
    }

    [Fact]
    public void ATrapIntoTheMonitorReadsAStackFromA286TaskStateSegment()
    {
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        WriteGate(machine, 6, 0x08, 0x6000, type: 0x0E, dpl: 0);

        // Replace the 386 TSS with a 286 one: type 1, SP0 at +2 and SS0 at +4.
        WriteDescriptor(machine, 3, TssBase, 0x2B, 0x81, 0x00);
        machine.Memory.WriteWord(TssBase + 2, 0x7F00);
        machine.Memory.WriteWord(TssBase + 4, 0x0010);
        machine.Memory.WriteDword(TssBase + 8, 0xDEADBEEF);   // where the 386 layout would look for SS0

        EnterTask(machine, cs: 0x0800, eip: 0x0100, eflags: 0x00023202);
        machine.Memory.WriteByte(0x8000 + 0x0100, 0x63);   // ARPL: #UD in a task
        machine.Memory.WriteByte(0x8000 + 0x0101, 0x00);
        machine.Cpu.Step();

        Assert.False(machine.Cpu.State.Virtual8086Mode);
        Assert.Equal(0x6000u, machine.Cpu.State.Eip);
        Assert.Equal(0x0010, machine.Cpu.State.Ss.Selector);
        // The V86 frame is nine dwords below SP0 (GS, FS, DS, ES, SS, ESP, EFLAGS, CS, EIP).
        Assert.Equal(0x7F00u - 36, machine.Cpu.State.Esp);
    }
}



/// <summary>
/// A call gate is how a program reaches a routine it is not allowed to know
/// the address of. The caller names the gate, not the destination: the gate
/// holds the code segment and the offset, and the offset in the instruction
/// is discarded. Its privilege says who may call it; the code segment's says
/// what the routine runs at.
///
/// This is how the 286-era protected-mode systems made their system calls,
/// and the ancestor of every trap-based system call since.
/// </summary>
public class CallGateTests
{
    private const uint GdtBase = 0x1000;
    private const uint TssBase = 0x4000;
    private const uint KernelStackTop = 0x8000;
    private const uint UserStackTop = 0x9000;

    private static void WriteDescriptor(Machine machine, int index, uint baseAddress,
                                        uint limit, byte access, byte granularity)
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
    /// Writes a call gate: the code segment it leads to, the offset within
    /// it, how many arguments to copy across a stack switch, and who may use
    /// it.
    /// </summary>
    private static void WriteCallGate(Machine machine, int index, ushort targetSelector,
                                      uint offset, byte dpl, int parameterCount = 0)
    {
        uint address = GdtBase + (uint)(index * 8);

        machine.Memory.WriteWord(address, (ushort)(offset & 0xFFFF));
        machine.Memory.WriteWord(address + 2, targetSelector);
        machine.Memory.WriteByte(address + 4, (byte)(parameterCount & 0x1F));

        // 0x8C: present, system descriptor, type 12 -- a 32-bit call gate.
        machine.Memory.WriteByte(address + 5, (byte)(0x8C | (dpl << 5)));
        machine.Memory.WriteWord(address + 6, (ushort)(offset >> 16));
    }

    private static Machine BuildMachine()
    {
        var machine = new Machine();
        machine.RunInstructions(200);

        WriteDescriptor(machine, 1, 0, 0xFFFFF, 0x9A, 0xC0);   // ring 0 code
        WriteDescriptor(machine, 2, 0, 0xFFFFF, 0x92, 0xC0);   // ring 0 data
        WriteDescriptor(machine, 3, TssBase, 0x67, 0x89, 0x00);
        WriteDescriptor(machine, 4, 0, 0xFFFFF, 0xFA, 0xC0);   // ring 3 code
        WriteDescriptor(machine, 5, 0, 0xFFFFF, 0xF2, 0xC0);   // ring 3 data

        machine.Cpu.State.GdtBase = GdtBase;
        machine.Cpu.State.GdtLimit = 0x4F;

        machine.Memory.WriteDword(TssBase + 4, KernelStackTop);
        machine.Memory.WriteDword(TssBase + 8, 0x10);

        machine.Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable;
        machine.Cpu.State.TaskRegister = 0x18;
        machine.Cpu.State.Halted = false;

        return machine;
    }

    /// <summary>Places a far call to the given selector at EIP and runs it.</summary>
    private static void CallThrough(Machine machine, ushort selector)
    {
        uint address = machine.Cpu.State.Cs.Base + machine.Cpu.State.Eip;

        machine.Memory.WriteByte(address, 0x9A);              // far CALL
        machine.Memory.WriteDword(address + 1, 0xDEADBEEF);   // offset: ignored
        machine.Memory.WriteWord(address + 5, selector);

        machine.Cpu.Step();
    }

    [Fact]
    public void TheGateSuppliesTheDestinationAndTheOffsetIsIgnored()
    {
        // The caller does not know where the routine is, and the address it
        // writes into the instruction counts for nothing. That is the point:
        // a program cannot enter the kernel anywhere except where the kernel
        // said.
        using Machine machine = BuildMachine();
        WriteCallGate(machine, 6, targetSelector: 0x08, offset: 0x7000, dpl: 3);

        machine.Cpu.SetSegment(SegReg.Cs, 0x08);
        machine.Cpu.SetSegment(SegReg.Ss, 0x10);
        machine.Cpu.State.Esp = KernelStackTop;
        machine.Cpu.State.Eip = 0x1000;

        CallThrough(machine, 0x30);   // the gate, not a code segment

        Assert.Equal(0x7000u, machine.Cpu.State.Eip);
        Assert.Equal((ushort)0x08, machine.Cpu.State.Cs.Selector);
    }

    [Fact]
    public void CallingFromRingThreeSwitchesToTheKernelStack()
    {
        // Raising privilege means changing stack, for the same reason an
        // interrupt does: the kernel must not run on memory the caller can
        // reach.
        using Machine machine = BuildMachine();
        WriteCallGate(machine, 6, targetSelector: 0x08, offset: 0x7000, dpl: 3);

        machine.Cpu.SetSegment(SegReg.Cs, 0x23);
        machine.Cpu.SetSegment(SegReg.Ss, 0x2B);
        machine.Cpu.State.Esp = UserStackTop;
        machine.Cpu.State.Eip = 0x1000;
        machine.Cpu.State.Cpl = 3;

        CallThrough(machine, 0x33);

        Assert.Equal(0, (int)machine.Cpu.State.Cpl);
        Assert.Equal((ushort)0x10, machine.Cpu.State.Ss.Selector);
        Assert.Equal(0x7000u, machine.Cpu.State.Eip);
    }

    [Fact]
    public void TheCallersStackIsRecordedSoItCanBeReturnedTo()
    {
        using Machine machine = BuildMachine();
        WriteCallGate(machine, 6, targetSelector: 0x08, offset: 0x7000, dpl: 3);

        machine.Cpu.SetSegment(SegReg.Cs, 0x23);
        machine.Cpu.SetSegment(SegReg.Ss, 0x2B);
        machine.Cpu.State.Esp = UserStackTop;
        machine.Cpu.State.Eip = 0x1000;
        machine.Cpu.State.Cpl = 3;

        CallThrough(machine, 0x33);

        // Under the return address: the caller's ESP and SS.
        Assert.Equal(UserStackTop, machine.Memory.ReadDword(KernelStackTop - 8));
        Assert.Equal(0x2Bu, machine.Memory.ReadDword(KernelStackTop - 4));
    }

    [Fact]
    public void ArgumentsAreCopiedOntoTheNewStack()
    {
        // The caller pushed its arguments on its own stack, and the routine
        // has to find them on the one it is about to run on. The gate says
        // how many to bring across -- the kernel decides that, not the
        // caller, so a program cannot make it copy an unbounded amount.
        using Machine machine = BuildMachine();
        WriteCallGate(machine, 6, targetSelector: 0x08, offset: 0x7000,
                      dpl: 3, parameterCount: 2);

        machine.Cpu.SetSegment(SegReg.Cs, 0x23);
        machine.Cpu.SetSegment(SegReg.Ss, 0x2B);
        machine.Cpu.State.Esp = UserStackTop;
        machine.Cpu.State.Eip = 0x1000;
        machine.Cpu.State.Cpl = 3;

        machine.Memory.WriteDword(UserStackTop, 0x11111111);
        machine.Memory.WriteDword(UserStackTop + 4, 0x22222222);

        CallThrough(machine, 0x33);

        // Above the return address, in the order the caller had them.
        Assert.Equal(0x11111111u, machine.Memory.ReadDword(machine.Cpu.State.Esp + 8));
        Assert.Equal(0x22222222u, machine.Memory.ReadDword(machine.Cpu.State.Esp + 12));
    }

    [Fact]
    public void AGateTooPrivilegedForTheCallerIsRefused()
    {
        // A gate with DPL 0 is one the kernel keeps to itself. Ring 3 asking
        // for it is exactly the case the check exists for.
        using Machine machine = BuildMachine();
        WriteCallGate(machine, 6, targetSelector: 0x08, offset: 0x7000, dpl: 0);

        machine.Cpu.SetSegment(SegReg.Cs, 0x23);
        machine.Cpu.SetSegment(SegReg.Ss, 0x2B);
        machine.Cpu.State.Esp = UserStackTop;
        machine.Cpu.State.Eip = 0x1000;
        machine.Cpu.State.Cpl = 3;

        uint address = machine.Cpu.State.Cs.Base + machine.Cpu.State.Eip;
        machine.Memory.WriteByte(address, 0x9A);
        machine.Memory.WriteDword(address + 1, 0);
        machine.Memory.WriteWord(address + 5, 0x33);

        // The refusal shows up as a general protection fault. With a handler
        // installed for it, the processor ends up there instead of in the
        // routine the gate names -- which is the observable difference
        // between refusing the call and allowing it.
        WriteDescriptor(machine, 7, 0, 0xFFFFF, 0x9A, 0xC0);
        uint idtBase = 0x2000;
        machine.Cpu.State.IdtBase = idtBase;
        machine.Cpu.State.IdtLimit = 0x7FF;

        uint gateAddress = idtBase + 13 * 8;
        machine.Memory.WriteWord(gateAddress, 0x0500);       // handler offset
        machine.Memory.WriteWord(gateAddress + 2, 0x08);
        machine.Memory.WriteByte(gateAddress + 4, 0);
        machine.Memory.WriteByte(gateAddress + 5, 0x8E);     // present, interrupt gate
        machine.Memory.WriteWord(gateAddress + 6, 0);

        machine.Cpu.Step();

        // In the fault handler, not at the gate's destination.
        Assert.Equal(0x0500u, machine.Cpu.State.Eip);
        Assert.NotEqual(0x7000u, machine.Cpu.State.Eip);
    }

    [Fact]
    public void AnOrdinaryFarCallStillGoesWhereItIsTold()
    {
        // Only a gate selector is redirected. A call to a real code segment
        // has to keep using the offset in the instruction, or every far call
        // in protected mode would break.
        using Machine machine = BuildMachine();

        machine.Cpu.SetSegment(SegReg.Cs, 0x08);
        machine.Cpu.SetSegment(SegReg.Ss, 0x10);
        machine.Cpu.State.Esp = KernelStackTop;
        machine.Cpu.State.Eip = 0x1000;

        uint address = machine.Cpu.State.Cs.Base + machine.Cpu.State.Eip;
        machine.Memory.WriteByte(address, 0x9A);
        machine.Memory.WriteDword(address + 1, 0x00005000);
        machine.Memory.WriteWord(address + 5, 0x08);

        machine.Cpu.Step();

        Assert.Equal(0x5000u, machine.Cpu.State.Eip);
    }
}
