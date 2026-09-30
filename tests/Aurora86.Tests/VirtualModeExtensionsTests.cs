using Aurora86.Core;
using Aurora86.Core.Cpu;

namespace Aurora86.Tests;

/// <summary>
/// Work package P2.3: Virtual-8086 Mode Extensions (CR4.VME) and
/// Protected-mode Virtual Interrupts (CR4.PVI). Each fact's summary names
/// the test case it exercises from plans/specs/pse-vme.md's "P2.3 VME / PVI"
/// section, numbered there 9-19.
///
/// The V86 harness mirrors VirtualModeTests.cs's BuildMonitor/EnterTask, with
/// its own copy rather than a shared one -- every test class in this suite
/// keeps its table-writing helpers local, because a descriptor with a field
/// in the wrong place is the classic protected-mode bug and hides well
/// behind a shared builder. The one addition here is selecting a Pentium
/// (whose silicon carries CpuFeature.Vme) and setting CR4.VME/PVI, neither
/// of which the older V86 tests needed.
/// </summary>
public class VirtualModeExtensionsTests
{
    private const uint GdtBase = 0x1000;
    private const uint IdtBase = 0x2000;
    private const uint TssBase = 0x4000;
    private const uint MonitorStackTop = 0x8000;
    private const uint TaskStackTop = 0x9000;
    private const uint KernelStackTop = 0x8000;

    // --- shared table-writing helpers (see VirtualModeTests.cs/ProtectedModeTests.cs) ---

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

    private static void Push(Machine machine, uint value)
    {
        machine.Cpu.State.Esp -= 4;
        machine.Memory.WriteDword(machine.Cpu.State.Ss.Base + machine.Cpu.State.Esp, value);
    }

    /// <summary>
    /// A monitor: flat ring 0 segments, a task state segment holding its
    /// stack, an IDT, and -- unlike VirtualModeTests.cs's own BuildMonitor --
    /// a Pentium selected so that CpuFeature.Vme (and hence CR4.VME/PVI's
    /// writability) is real, with CR4.VME on by default so that every test
    /// exercises the extension unless it explicitly asks for the <c>vme:
    /// false</c> regression instead.
    /// </summary>
    private static Machine BuildMonitor(bool vme = true)
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

        machine.Cpu.State.SelectModel(CpuModel.Pentium);
        if (vme) machine.Cpu.State.Cr4 |= CpuState.Cr4Vme;

        machine.Cpu.SetSegment(SegReg.Cs, 0x08);
        machine.Cpu.SetSegment(SegReg.Ss, 0x10);
        machine.Cpu.State.Esp = MonitorStackTop;

        return machine;
    }

    /// <summary>
    /// Builds the IRETD frame that starts a V86 task and executes it, the
    /// way a monitor launches a DOS box. <paramref name="eflags"/> must
    /// carry the VM bit (1 &lt;&lt; 17) itself, or this IRETD returns to
    /// ordinary protected-mode code instead of entering virtual-8086 mode at
    /// all.
    /// </summary>
    private static void EnterTask(Machine machine, ushort cs, uint eip, uint eflags)
    {
        Push(machine, 0);              // GS
        Push(machine, 0);              // FS
        Push(machine, 0);              // DS
        Push(machine, 0);              // ES
        Push(machine, 0x0900);         // SS
        Push(machine, TaskStackTop & 0xFFFF);
        Push(machine, eflags);
        Push(machine, cs);
        Push(machine, eip);

        uint address = machine.Cpu.State.Cs.Base + machine.Cpu.State.Eip;
        machine.Memory.WriteByte(address, 0xCF);   // IRETD
        machine.Cpu.Step();
    }

    /// <summary>
    /// A TSS whose I/O map base leaves exactly the 32 bytes below it for the
    /// interrupt redirection bitmap and nothing else: the descriptor's limit
    /// reaches the last of those bytes and no further, so the port
    /// permission bitmap -- irrelevant to every test in this file -- simply
    /// is not there. Every byte starts clear, i.e. every vector redirected;
    /// <see cref="ForbidRedirect"/> sets one bit at a time.
    /// </summary>
    private static void GiveTaskARedirectionBitmap(Machine machine)
    {
        WriteDescriptor(machine, 3, TssBase, 0x87, 0x89, 0x00);
        machine.Memory.WriteWord(TssBase + 0x66, 0x88);   // I/O map base
        for (uint i = 0x68; i <= 0x87; i++) machine.Memory.WriteByte(TssBase + i, 0);
    }

    private static void ForbidRedirect(Machine machine, int vector)
    {
        uint address = TssBase + 0x68 + (uint)(vector >> 3);
        machine.Memory.WriteByte(address, (byte)(machine.Memory.ReadByte(address) | (1 << (vector & 7))));
    }

    // --- CLI/STI below IOPL 3 (test cases 9, 10) ------------------------------

    [Fact]
    public void StiBelowIoplSetsVifButLeavesTheRealIfAlone()
    {
        // Test 9: VME, V86, IOPL 0, VIF 0, VIP 0 -> STI -> VIF 1, IF unchanged.
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        EnterTask(machine, cs: 0x0800, eip: 0x0100, eflags: 0x00020002);   // IOPL 0, IF 0

        machine.Memory.WriteByte(0x8000 + 0x0100, 0xFB);   // STI
        machine.Cpu.Step();

        Assert.True(machine.Cpu.State.Virtual8086Mode);   // never trapped to the monitor
        Assert.True(machine.Cpu.Flags.VirtualInterrupt);
        Assert.False(machine.Cpu.Flags.Interrupt);        // the real flag never moved
    }

    [Fact]
    public void StiWithVirtualInterruptPendingFaultsAndLeavesVifAlone()
    {
        // Test 10: as 9 with VIP 1 -> #GP(0), VIF unchanged.
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        WriteGate(machine, 13, 0x08, 0x5000, type: 0x0E, dpl: 0);
        EnterTask(machine, cs: 0x0800, eip: 0x0100, eflags: 0x00120002);   // IOPL 0, VIP 1

        machine.Memory.WriteByte(0x8000 + 0x0100, 0xFB);   // STI
        machine.Cpu.Step();

        Assert.False(machine.Cpu.State.Virtual8086Mode);   // trapped to the monitor
        Assert.Equal(0x5000u, machine.Cpu.State.Eip);
        Assert.False(machine.Cpu.Flags.VirtualInterrupt);
        Assert.Equal(0u, machine.Memory.ReadDword(machine.Cpu.State.Ss.Base + machine.Cpu.State.Esp));   // #GP(0)
    }

    // --- PUSHF/POPF/IRET below IOPL 3 (test cases 11, 12, 13) -----------------

    [Fact]
    public void PushfBelowIoplImagesVifAsIfAndIoplAsThree()
    {
        // Test 11: VME, V86, IOPL 0, VIF 1 -> PUSHF -> image IF = 1, IOPL = 3.
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        EnterTask(machine, cs: 0x0800, eip: 0x0100, eflags: 0x000A0002);   // IOPL 0, VIF 1

        machine.Memory.WriteByte(0x8000 + 0x0100, 0x9C);   // PUSHF
        machine.Cpu.Step();

        Assert.True(machine.Cpu.State.Virtual8086Mode);   // never trapped
        ushort pushed = machine.Memory.ReadWord(machine.Cpu.State.Ss.Base + machine.Cpu.State.Esp);
        Assert.Equal(0x0200, pushed & 0x0200);   // IF position carries VIF's value: 1
        Assert.Equal(0x3000, pushed & 0x3000);   // IOPL reads back as 3
    }

    [Fact]
    public void PopfBelowIoplFaultsOnTrapFlagWithTheStackUntouched()
    {
        // Test 12: VME, V86, IOPL 0 -> POPF of an image with TF = 1 ->
        // #GP(0), SP unchanged.
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        WriteGate(machine, 13, 0x08, 0x5000, type: 0x0E, dpl: 0);
        EnterTask(machine, cs: 0x0800, eip: 0x0100, eflags: 0x00020002);   // IOPL 0

        machine.Cpu.State.Esp = TaskStackTop - 2;
        uint frame = machine.Cpu.State.Ss.Base + machine.Cpu.State.Esp;
        machine.Memory.WriteWord(frame, 0x0100);   // TF set, IF clear
        uint taskEspBefore = machine.Cpu.State.Esp;

        machine.Memory.WriteByte(0x8000 + 0x0100, 0x9D);   // POPF
        machine.Cpu.Step();

        Assert.False(machine.Cpu.State.Virtual8086Mode);   // trapped to the monitor
        Assert.Equal(0x5000u, machine.Cpu.State.Eip);
        // The saved frame is 32-bit (the gate is a 32-bit one); #GP pushes
        // an error code, which lands on top of everything else, so the
        // layout from the bottom up is: error code, EIP, CS, EFLAGS, ESP,
        // then SS and the four data segments (#GP(0) itself is asserted
        // elsewhere in this file at frame+0).
        uint monitorFrame = machine.Cpu.State.Ss.Base + machine.Cpu.State.Esp;
        Assert.Equal(taskEspBefore, machine.Memory.ReadDword(monitorFrame + 16));
    }

    [Fact]
    public void IretBelowIoplFaultsOnTheTrapFlagLikePopf()
    {
        // Test 13: the same image as test 12, through IRET instead of POPF.
        // The SDM IRET pseudocode (and Bochs) fault on TF = 1 here exactly as
        // POPF does; the P2.3 review overturned the note's reading of Collins.
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        EnterTask(machine, cs: 0x0800, eip: 0x0100, eflags: 0x00020002);   // IOPL 0, IF 0, VIF 0

        machine.Cpu.State.Esp = TaskStackTop - 6;
        uint frame = machine.Cpu.State.Ss.Base + machine.Cpu.State.Esp;
        machine.Memory.WriteWord(frame, 0x0200);       // IP
        machine.Memory.WriteWord(frame + 2, 0x0800);   // CS
        machine.Memory.WriteWord(frame + 4, 0x0300);   // FLAGS image: TF = 1, IF = 1

        machine.Memory.WriteByte(0x8000 + 0x0100, 0xCF);   // IRET

        byte vector = 0xFF;
        machine.Cpu.ProcessorException += (number, _, _, _) => vector = vector == 0xFF ? number : vector;
        machine.Cpu.Step();

        Assert.Equal(13, vector);
        Assert.False(machine.Cpu.Flags.VirtualInterrupt);   // nothing loaded from the image
    }

    [Fact]
    public void PushfdBelowIoplTrapsEvenWithVme()
    {
        // Only the 16-bit PUSHF is virtualised: 66 9C below IOPL 3 raises
        // #GP(0) to the monitor with CR4.VME on (SDM PUSHF).
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        EnterTask(machine, cs: 0x0800, eip: 0x0100, eflags: 0x000A0002);   // IOPL 0, VIF 1

        machine.Memory.WriteByte(0x8000 + 0x0100, 0x66);
        machine.Memory.WriteByte(0x8000 + 0x0101, 0x9C);

        byte vector = 0xFF;
        machine.Cpu.ProcessorException += (number, _, _, _) => vector = vector == 0xFF ? number : vector;
        machine.Cpu.Step();

        Assert.Equal(13, vector);
    }

    [Fact]
    public void PopfdBelowIoplTrapsEvenWithVme()
    {
        // 66 9D below IOPL 3 likewise traps instead of loading VIF.
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        EnterTask(machine, cs: 0x0800, eip: 0x0100, eflags: 0x00020002);   // IOPL 0, VIF 0

        machine.Cpu.State.Esp = TaskStackTop - 4;
        uint frame = machine.Cpu.State.Ss.Base + machine.Cpu.State.Esp;
        machine.Memory.WriteDword(frame, 0x00000202);   // IF = 1

        machine.Memory.WriteByte(0x8000 + 0x0100, 0x66);
        machine.Memory.WriteByte(0x8000 + 0x0101, 0x9D);

        byte vector = 0xFF;
        machine.Cpu.ProcessorException += (number, _, _, _) => vector = vector == 0xFF ? number : vector;
        machine.Cpu.Step();

        Assert.Equal(13, vector);
        Assert.False(machine.Cpu.Flags.VirtualInterrupt);
    }

    // --- INT n redirection (test cases 14, 15, 16, 17) ------------------------

    [Fact]
    public void RedirectedIntBelowIoplGoesToTheV86IvtAndClearsVif()
    {
        // Test 14: VME, V86, IOPL < 3, bit 21h clear -> INT 21h -> IVT[21h]
        // in V86, VIF cleared.
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        GiveTaskARedirectionBitmap(machine);   // every vector redirected by default
        machine.Memory.WriteWord(0x21 * 4, 0x0300);
        machine.Memory.WriteWord(0x21 * 4 + 2, 0x0800);

        EnterTask(machine, cs: 0x0800, eip: 0x0100, eflags: 0x000A0002);   // IOPL 0, VIF 1
        machine.Memory.WriteByte(0x8000 + 0x0100, 0xCD);   // INT 21h
        machine.Memory.WriteByte(0x8000 + 0x0101, 0x21);
        machine.Cpu.Step();

        Assert.True(machine.Cpu.State.Virtual8086Mode);       // never left V86 at all
        Assert.Equal((ushort)0x0800, machine.Cpu.State.Cs.Selector);
        Assert.Equal(0x0300u, machine.Cpu.State.Eip);
        Assert.False(machine.Cpu.Flags.VirtualInterrupt);     // method 5: VIF cleared
        Assert.False(machine.Cpu.Flags.Interrupt);            // the real flag was, and stays, 0
    }

    [Fact]
    public void RedirectedIntPushesTheExactSixteenBitFrameWithVifInTheImage()
    {
        // Test 14 again, but checking the frame's exact bytes and SP: the
        // pushed FLAGS carries VIF in the IF position (with IOPL forced to
        // 3), exactly as a virtualized PUSHF would, not the raw register;
        // CS and IP are the task's own return address; and the whole frame
        // is three 16-bit words, because the V86 task never left a
        // real-mode-shaped stack at all.
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        GiveTaskARedirectionBitmap(machine);
        machine.Memory.WriteWord(0x21 * 4, 0x0300);
        machine.Memory.WriteWord(0x21 * 4 + 2, 0x0800);

        EnterTask(machine, cs: 0x0800, eip: 0x0100, eflags: 0x000A0002);   // IOPL 0, VIF 1
        uint spBefore = machine.Cpu.State.Esp;
        machine.Memory.WriteByte(0x8000 + 0x0100, 0xCD);   // INT 21h (two bytes)
        machine.Memory.WriteByte(0x8000 + 0x0101, 0x21);
        machine.Cpu.Step();

        Assert.Equal(spBefore - 6, machine.Cpu.State.Esp);
        uint frame = machine.Cpu.State.Ss.Base + machine.Cpu.State.Esp;
        Assert.Equal((ushort)0x0102, machine.Memory.ReadWord(frame));       // return IP, past the 2-byte INT
        Assert.Equal((ushort)0x0800, machine.Memory.ReadWord(frame + 2));   // return CS
        Assert.Equal((ushort)0x3202, machine.Memory.ReadWord(frame + 4));  // FLAGS: VIF->IF(0x200), IOPL->3(0x3000), reserved bit1
    }

    [Fact]
    public void NonRedirectedIntBelowIoplFaultsToTheMonitor()
    {
        // Test 15: same as 14, bit set -> #GP(0) to the monitor.
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        GiveTaskARedirectionBitmap(machine);
        ForbidRedirect(machine, 0x21);   // bit set: not redirected
        WriteGate(machine, 13, 0x08, 0x5000, type: 0x0E, dpl: 0);

        EnterTask(machine, cs: 0x0800, eip: 0x0100, eflags: 0x00020002);   // IOPL 0
        machine.Memory.WriteByte(0x8000 + 0x0100, 0xCD);
        machine.Memory.WriteByte(0x8000 + 0x0101, 0x21);
        machine.Cpu.Step();

        Assert.False(machine.Cpu.State.Virtual8086Mode);
        Assert.Equal(0x5000u, machine.Cpu.State.Eip);
        Assert.Equal(0u, machine.Memory.ReadDword(machine.Cpu.State.Ss.Base + machine.Cpu.State.Esp));   // #GP(0)
    }

    [Fact]
    public void RedirectedIntAtIoplThreeClearsTheRealIf()
    {
        // Test 16, the coordinator's [C] correction: at IOPL 3 with VME on,
        // INT n is *not* simply unchanged the way CLI/STI/PUSHF/POPF/IRET
        // are -- the redirection bitmap is still consulted, and a clear bit
        // still redirects, using the real IF (method 4).
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        GiveTaskARedirectionBitmap(machine);   // every vector redirected by default
        machine.Memory.WriteWord(0x21 * 4, 0x0300);
        machine.Memory.WriteWord(0x21 * 4 + 2, 0x0800);

        EnterTask(machine, cs: 0x0800, eip: 0x0100, eflags: 0x00023202);   // IOPL 3, IF 1
        machine.Memory.WriteByte(0x8000 + 0x0100, 0xCD);
        machine.Memory.WriteByte(0x8000 + 0x0101, 0x21);
        machine.Cpu.Step();

        Assert.True(machine.Cpu.State.Virtual8086Mode);   // never left V86
        Assert.Equal(0x0300u, machine.Cpu.State.Eip);
        Assert.False(machine.Cpu.Flags.Interrupt);         // the real flag, cleared
    }

    [Fact]
    public void Int3AndIntoAreNeverRedirectedEvenWhenTheBitmapWouldAllowIt()
    {
        // Test 17: INT3/INTO (INT1 is not implemented at all here, and
        // faults #UD regardless) always reach the protected-mode handler,
        // whatever the redirection bitmap says.
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        GiveTaskARedirectionBitmap(machine);   // every vector redirected, 3 and 4 included
        WriteGate(machine, 3, 0x08, 0x5000, type: 0x0E, dpl: 3);
        WriteGate(machine, 4, 0x08, 0x5100, type: 0x0E, dpl: 3);
        // If either were ever redirected, execution would land here instead.
        machine.Memory.WriteWord(3 * 4, 0x9999);
        machine.Memory.WriteWord(3 * 4 + 2, 0x1234);
        machine.Memory.WriteWord(4 * 4, 0x9999);
        machine.Memory.WriteWord(4 * 4 + 2, 0x1234);

        EnterTask(machine, cs: 0x0800, eip: 0x0100, eflags: 0x00020002);   // IOPL 0
        machine.Memory.WriteByte(0x8000 + 0x0100, 0xCC);   // INT3
        machine.Cpu.Step();

        Assert.False(machine.Cpu.State.Virtual8086Mode);
        Assert.Equal(0x5000u, machine.Cpu.State.Eip);

        using Machine into = BuildMonitor();
        into.Cpu.State.Eip = 0x1000;
        GiveTaskARedirectionBitmap(into);
        WriteGate(into, 4, 0x08, 0x5100, type: 0x0E, dpl: 3);
        EnterTask(into, cs: 0x0800, eip: 0x0100, eflags: 0x00020002);
        into.Cpu.Flags.Overflow = true;   // so INTO actually fires
        into.Memory.WriteByte(0x8000 + 0x0100, 0xCE);   // INTO
        into.Cpu.Step();

        Assert.False(into.Cpu.State.Virtual8086Mode);
        Assert.Equal(0x5100u, into.Cpu.State.Eip);
    }

    [Fact]
    public void ATssTooShortForARedirectionBitmapNeverRedirects()
    {
        // pse-vme.md marks this "uncertain": treated by analogy with the I/O
        // bitmap, as though the missing bitmap's bits were all set, i.e.
        // never redirected. BuildMonitor's default TSS ends at offset 0x67,
        // with no room for either bitmap at all.
        using Machine machine = BuildMonitor();
        machine.Cpu.State.Eip = 0x1000;
        WriteGate(machine, 13, 0x08, 0x5000, type: 0x0E, dpl: 0);
        EnterTask(machine, cs: 0x0800, eip: 0x0100, eflags: 0x00020002);   // IOPL 0

        machine.Memory.WriteByte(0x8000 + 0x0100, 0xCD);   // INT 21h
        machine.Memory.WriteByte(0x8000 + 0x0101, 0x21);
        machine.Cpu.Step();

        Assert.False(machine.Cpu.State.Virtual8086Mode);   // the ordinary #GP(0) trap, not a redirect
        Assert.Equal(0x5000u, machine.Cpu.State.Eip);
    }

    // --- VME = 0 regression ----------------------------------------------------

    [Fact]
    public void WithVmeOffStiInV86BelowIoplStillTraps()
    {
        // CR4.VME = 0 must leave V86's CLI/STI/PUSHF/POPF/IRET/INT n
        // bit-identical to a processor that never had the extension: the
        // ordinary #GP(0) trap below IOPL 3, exactly as before this work
        // package.
        using Machine machine = BuildMonitor(vme: false);
        machine.Cpu.State.Eip = 0x1000;
        WriteGate(machine, 13, 0x08, 0x5000, type: 0x0E, dpl: 0);
        EnterTask(machine, cs: 0x0800, eip: 0x0100, eflags: 0x00020002);   // IOPL 0

        machine.Memory.WriteByte(0x8000 + 0x0100, 0xFB);   // STI
        machine.Cpu.Step();

        Assert.False(machine.Cpu.State.Virtual8086Mode);
        Assert.Equal(0x5000u, machine.Cpu.State.Eip);
        Assert.False(machine.Cpu.Flags.VirtualInterrupt);
    }

    [Fact]
    public void WithVmeOffRedirectionBitmapIsNeverConsultedEvenWhenItWouldRedirect()
    {
        using Machine machine = BuildMonitor(vme: false);
        machine.Cpu.State.Eip = 0x1000;
        GiveTaskARedirectionBitmap(machine);   // would redirect every vector, were VME on
        WriteGate(machine, 13, 0x08, 0x5000, type: 0x0E, dpl: 0);
        machine.Memory.WriteWord(0x21 * 4, 0x0300);
        machine.Memory.WriteWord(0x21 * 4 + 2, 0x0800);

        EnterTask(machine, cs: 0x0800, eip: 0x0100, eflags: 0x00020002);   // IOPL 0
        machine.Memory.WriteByte(0x8000 + 0x0100, 0xCD);
        machine.Memory.WriteByte(0x8000 + 0x0101, 0x21);
        machine.Cpu.Step();

        Assert.False(machine.Cpu.State.Virtual8086Mode);   // the ordinary #GP(0) trap
        Assert.Equal(0x5000u, machine.Cpu.State.Eip);
    }

    // --- PVI: protected mode, CPL 3 (test cases 18, 19) -----------------------

    private static Machine BuildPviMachine()
    {
        var machine = new Machine();
        machine.RunInstructions(200);

        WriteDescriptor(machine, 1, 0, 0xFFFFF, 0x9A, 0xC0);   // ring 0 code
        WriteDescriptor(machine, 2, 0, 0xFFFFF, 0x92, 0xC0);   // ring 0 data
        WriteDescriptor(machine, 3, TssBase, 0x67, 0x89, 0x00);
        WriteDescriptor(machine, 4, 0, 0xFFFFF, 0xFA, 0xC0);   // ring 3 code
        WriteDescriptor(machine, 5, 0, 0xFFFFF, 0xF2, 0xC0);   // ring 3 data

        machine.Cpu.State.GdtBase = GdtBase;
        machine.Cpu.State.GdtLimit = 0x2F;
        machine.Cpu.State.IdtBase = IdtBase;
        machine.Cpu.State.IdtLimit = 0x7FF;

        machine.Memory.WriteDword(TssBase + 4, KernelStackTop);
        machine.Memory.WriteDword(TssBase + 8, 0x10);

        machine.Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable;
        machine.Cpu.State.TaskRegister = 0x18;
        machine.Cpu.State.Halted = false;

        machine.Cpu.State.SelectModel(CpuModel.Pentium);
        machine.Cpu.State.Cr4 |= CpuState.Cr4Pvi;

        machine.Cpu.SetSegment(SegReg.Cs, 0x23);   // ring 3 code, RPL 3
        machine.Cpu.SetSegment(SegReg.Ss, 0x2B);   // ring 3 data, RPL 3
        machine.Cpu.State.Cpl = 3;
        machine.Cpu.State.Esp = 0x9000;

        return machine;
    }

    [Fact]
    public void PviCliClearsVifAtCplThreeAndLeavesTheRealIfAlone()
    {
        // Test 18 (CLI half): PVI, CPL 3, IOPL 0 -> CLI -> VIF 0.
        using Machine machine = BuildPviMachine();
        machine.Cpu.Flags.SetValue(0x00080002);   // IOPL 0, VIF 1
        machine.Cpu.State.Eip = 0x1000;
        machine.Memory.WriteByte(0x1000, 0xFA);   // CLI

        machine.Cpu.Step();

        Assert.False(machine.Cpu.Flags.VirtualInterrupt);
        Assert.Equal(0x1001u, machine.Cpu.State.Eip);   // executed in place, no trap
        Assert.Equal(3, (int)machine.Cpu.State.Cpl);    // never left ring 3
    }

    [Fact]
    public void PviDoesNotVirtualizePushf()
    {
        // Test 18 (PUSHF half): PUSHF pushes the ordinary EFLAGS -- PVI
        // touches CLI/STI only, never PUSHF/POPF.
        using Machine machine = BuildPviMachine();
        machine.Cpu.Flags.SetValue(0x00080002);   // IOPL 0, VIF 1, real IF 0
        machine.Cpu.State.Eip = 0x1000;
        machine.Memory.WriteByte(0x1000, 0x9C);   // PUSHF

        machine.Cpu.Step();

        ushort pushed = machine.Memory.ReadWord(machine.Cpu.State.Ss.Base + machine.Cpu.State.Esp);
        Assert.Equal(0, pushed & 0x0200);   // the real IF (0), not VIF (1)
        Assert.Equal(0, pushed & 0x3000);   // IOPL really is 0, not spoofed to 3
    }

    [Fact]
    public void PviStiFaultsWhenAVirtualInterruptIsPending()
    {
        // Test 19: PVI, CPL 3, IOPL 0, VIP 1 -> STI -> #GP(0).
        using Machine machine = BuildPviMachine();
        WriteGate(machine, 13, 0x08, 0x5000, type: 0x0E, dpl: 0);
        machine.Cpu.Flags.SetValue(0x00100002);   // IOPL 0, VIP 1
        machine.Cpu.State.Eip = 0x1000;
        machine.Memory.WriteByte(0x1000, 0xFB);   // STI

        machine.Cpu.Step();

        Assert.Equal(0x5000u, machine.Cpu.State.Eip);
        Assert.Equal(0, (int)machine.Cpu.State.Cpl);   // the fault raised privilege to ring 0
        Assert.False(machine.Cpu.Flags.VirtualInterrupt);
    }

    // --- POPFD/IRETD at CPL 0 in ordinary protected mode ----------------------

    private static Machine BuildFlatProtectedMachine()
    {
        var machine = new Machine();
        machine.RunInstructions(200);

        WriteDescriptor(machine, 1, 0, 0xFFFFF, 0x9A, 0xC0);
        WriteDescriptor(machine, 2, 0, 0xFFFFF, 0x92, 0xC0);

        machine.Cpu.State.GdtBase = GdtBase;
        machine.Cpu.State.GdtLimit = 0x17;
        machine.Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable;
        machine.Cpu.State.Halted = false;

        machine.Cpu.SetSegment(SegReg.Cs, 0x08);
        machine.Cpu.SetSegment(SegReg.Ss, 0x10);
        machine.Cpu.State.Esp = 0x9000;

        return machine;
    }

    [Fact]
    public void Cpl0PopfdLeavesVifAndVipAloneEvenOnVmeSilicon()
    {
        // POPF never loads VIF or VIP, at any privilege: "VIP, VIF and VM
        // remain unaffected" (SDM POPF). Only a 32-bit IRET at CPL 0, a task
        // switch and the return to V86 load them.
        using Machine machine = BuildFlatProtectedMachine();
        machine.Cpu.State.SelectModel(CpuModel.Pentium);
        machine.Cpu.State.Eip = 0x1000;
        // The flat code segment defaults to 32-bit operands (granularity
        // 0xC0's B bit), so plain 0x9D is already POPFD here -- a 0x66
        // prefix would instead toggle it down to a 16-bit POPF.
        machine.Memory.WriteByte(0x1000, 0x9D);   // POPFD
        machine.Memory.WriteDword(0x9000, 0x00180002);   // VIF | VIP | reserved

        machine.Cpu.Step();

        Assert.False(machine.Cpu.Flags.VirtualInterrupt);
        Assert.False(machine.Cpu.Flags.VirtualInterruptPending);
    }

    [Fact]
    public void PushfdShowsTheAlignmentCheckBitOnA486()
    {
        // The probe that tells a 486 from a 386: POPFD with AC (bit 18) set,
        // then PUSHFD, and AC must read back as 1. PUSHFD used to mask the
        // image to 16 bits, so no guest could see AC or ID stick.
        using Machine machine = BuildFlatProtectedMachine();
        machine.Cpu.State.SelectModel(CpuModel.Intel486);
        machine.Cpu.State.Eip = 0x1000;
        machine.Memory.WriteByte(0x1000, 0x9D);   // POPFD (32-bit segment)
        machine.Memory.WriteByte(0x1001, 0x9C);   // PUSHFD
        machine.Memory.WriteDword(0x9000, 0x00040002);   // AC

        machine.Cpu.Step();
        machine.Cpu.Step();

        uint pushed = machine.Memory.ReadDword(machine.Cpu.State.Ss.Base + machine.Cpu.State.Esp);
        Assert.Equal(0x00040000u, pushed & 0x00040000u);
    }

    [Fact]
    public void Cpl0PopfdLeavesVifAndVipAloneWithoutVmeSilicon()
    {
        // "On processors without Vme they stay as today": a model whose
        // silicon never carries CR4.VME/PVI at all treats bits 19/20 as
        // reserved, exactly like the Identification bit on a pre-CPUID part.
        using Machine machine = BuildFlatProtectedMachine();
        machine.Cpu.State.SelectModel(CpuModel.Intel486);
        machine.Cpu.State.Eip = 0x1000;
        machine.Memory.WriteByte(0x1000, 0x9D);   // POPFD (see the note above)
        machine.Memory.WriteDword(0x9000, 0x00180002);   // VIF | VIP | reserved

        machine.Cpu.Step();

        Assert.False(machine.Cpu.Flags.VirtualInterrupt);
        Assert.False(machine.Cpu.Flags.VirtualInterruptPending);
    }
}
