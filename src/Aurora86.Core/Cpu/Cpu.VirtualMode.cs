// Virtual-8086 Mode Extensions (CR4.VME) and Protected-mode Virtual
// Interrupts (CR4.PVI) -- work package P2.3.
//
// Both are the same idea applied in two places: a task that is not trusted
// with the real interrupt flag gets a *virtual* one instead (EFLAGS.VIF),
// with a companion bit (EFLAGS.VIP) the monitor uses to remember that a
// virtual interrupt is queued behind it. Before this file existed, Aurora's
// only response to a V86 task touching CLI, STI, PUSHF, POPF, IRET or INT n
// below IOPL 3 was to trap to the monitor every single time; that is exactly
// what made Windows 95 read CPUID.VME before turning CR4.VME/PVI on
// (cpu-models.md §3) -- the whole point of the bit is to remove most of
// those traps from the hot path DOS boxes run on.
//
// Sources: Intel SDM Vol.3 ch.20 §20.3.3 and Table 20-2 ("Interrupt and
// exception handling in V86 mode using VME"); the EFLAGS/CLI/STI/PUSHF/
// POPF/IRET/INT n pseudocode in Vol.2; plans/specs/pse-vme.md's "P2.3 VME /
// PVI" section, which is this file's working spec and the source of the
// [C]-marked corrections cited below; R. Collins, rcollins.org, "Virtual
// Mode Extensions", "Details of Intel's VME" and "VME: Coming Out of the
// Cold", cited by name where a specific claim comes from them rather than
// the SDM.

namespace Aurora86.Core.Cpu;

public sealed partial class Cpu
{
    // The uint mirrors of the two new EFLAGS bits, in the same style as the
    // existing private consts in Cpu.Ops.cs (InterruptFlag, IoplMask, ...)
    // that this file reuses rather than duplicates.
    private const uint VifFlag = 1u << 19;
    private const uint VipFlag = 1u << 20;
    private const uint VirtualInterruptBits = VifFlag | VipFlag;
    private const uint TrapFlagBit = 1u << 8;

    /// <summary>The current IOPL, read out of the live EFLAGS.</summary>
    private byte CurrentIopl() => (byte)((Flags.Value & IoplMask) >> 12);

    /// <summary>
    /// Whether CLI/STI at the current privilege change EFLAGS.VIF instead of
    /// the real IF: a V86 task with CR4.VME on, or ring-3 protected-mode code
    /// with CR4.PVI on, and in both cases only below IOPL 3 -- at IOPL 3 the
    /// code is trusted with the real flag and neither extension applies
    /// (pse-vme.md: "IOPL = 3: CLI/STI/PUSHF/POPF/IRET act on the real IF, as
    /// without VME"). With both CR4 bits clear this is always false, which is
    /// what keeps CLI/STI bit-identical to a processor without either
    /// extension.
    /// </summary>
    private bool InterruptFlagIsVirtualized()
    {
        if (CurrentIopl() == 3) return false;
        if (State.Virtual8086Mode) return (State.Cr4 & CpuState.Cr4Vme) != 0;
        return State.ProtectedMode && State.Cpl == 3 && (State.Cr4 & CpuState.Cr4Pvi) != 0;
    }

    /// <summary>CLI, virtualized where <see cref="InterruptFlagIsVirtualized"/> says so.</summary>
    private void Cli()
    {
        if (InterruptFlagIsVirtualized())
        {
            Flags.VirtualInterrupt = false;
            return;
        }

        if (!IoPrivileged()) { FaultNow(13, 0); return; }
        Flags.Interrupt = false;
    }

    /// <summary>
    /// STI, virtualized where <see cref="InterruptFlagIsVirtualized"/> says
    /// so. Setting VIF while VIP is already 1 is refused with #GP(0) rather
    /// than honoured: the monitor has a virtual interrupt queued that the
    /// task has not been given a chance to see yet, and the fault is what
    /// hands control back to the monitor the instant the task says it is
    /// ready for one.
    /// </summary>
    private void Sti()
    {
        if (InterruptFlagIsVirtualized())
        {
            if (Flags.VirtualInterruptPending) throw new CpuException(13, 0);
            Flags.VirtualInterrupt = true;
            return;
        }

        if (!IoPrivileged()) { FaultNow(13, 0); return; }
        Flags.Interrupt = true;
        // Enabling takes effect only after the next instruction, not
        // immediately: see _interruptShadow. This is the real IF only --
        // the virtualized STI above never opens the real interrupt window,
        // since it never touches the real flag CheckInterrupts reads.
        _interruptShadow = true;
    }

    /// <summary>
    /// Whether CR4.VME is on and the current V86 code is below IOPL 3 --
    /// the condition every one of PUSHF/POPF/IRET's virtualized paths below
    /// shares. Callers already know they are in virtual-8086 mode; this only
    /// adds the CR4 and IOPL half of the test.
    /// </summary>
    private bool VmeBelowIopl3() => (State.Cr4 & CpuState.Cr4Vme) != 0 && CurrentIopl() != 3;

    /// <summary>
    /// The PUSHF-shaped image both PUSHF's own virtualized path and a
    /// redirected INT n's method-5 frame push below IOPL 3: VIF stands in
    /// for IF and IOPL reads back as 3, so a task looking at its own flags
    /// sees exactly what a fully privileged 8086 program would -- unaware
    /// that IF is not the flag actually gating interrupt delivery.
    /// </summary>
    private ushort VirtualizedFlagsImage()
    {
        uint image = Flags.Value & 0xFFFFu;
        image = Flags.VirtualInterrupt ? image | InterruptFlag : image & ~InterruptFlag;
        image |= IoplMask;   // the image claims full privilege, hiding the split
        return (ushort)image;
    }

    /// <summary>
    /// PUSHF's V86+VME path (Table 20-2, IOPL &lt; 3): instead of trapping to
    /// the monitor the way IOPL &lt; 3 always used to, the image goes out
    /// through <see cref="VirtualizedFlagsImage"/>. Returns false (having
    /// pushed nothing) when VME is off or IOPL is 3, so the caller falls
    /// through to the trap PUSHF has always taken there.
    /// </summary>
    private bool TryPushVirtualizedFlags()
    {
        // Only PUSHF is virtualised; PUSHFD below IOPL 3 is #GP(0) with VME
        // on as without it (SDM PUSHF: "IF (CR4.VME = 0) OR (OperandSize = 32)
        // THEN #GP(0)"), so it falls back to the caller's trap.
        if (!VmeBelowIopl3() || OperandSize32) return false;

        PushOperand(VirtualizedFlagsImage());
        return true;
    }

    /// <summary>
    /// POPF's (and IRET's) V86+VME path (Table 20-2, IOPL &lt; 3): the image
    /// is peeked off the stack -- not popped -- so that every fault below can
    /// be raised with the stack exactly as it was found, then applied through
    /// <see cref="ApplyVirtualInterruptImage"/>, and only then actually
    /// popped. Returns false, having touched neither the stack nor the flags,
    /// when VME is off or IOPL is 3.
    /// </summary>
    private bool TryPopVirtualizedFlags(bool iret)
    {
        // Only the 16-bit POPF is virtualised; POPFD below IOPL 3 traps.
        if (!VmeBelowIopl3() || OperandSize32) return false;

        uint peeked = PeekStackOperand();
        uint image = OperandSize32 ? peeked : (Flags.Value & 0xFFFF0000) | (peeked & 0xFFFF);

        uint next = ApplyVirtualInterruptImage(image, trapFlagFaults: !iret);

        PopOperand();   // safe now: every fault above already happened
        Flags.SetValue(next);
        return true;
    }

    /// <summary>
    /// Reads the next stack slot at the current operand size without moving
    /// the stack pointer -- the "peek before you pop" POPF's virtualized path
    /// needs so that a faulting image leaves the stack untouched.
    /// </summary>
    private uint PeekStackOperand()
    {
        if (OperandSize32)
            return State.Ss.Big ? ReadMem32(SegReg.Ss, State.Esp) : ReadMem32(SegReg.Ss, State.Sp);
        return State.Ss.Big ? ReadMem16(SegReg.Ss, State.Esp) : ReadMem16(SegReg.Ss, State.Sp);
    }

    /// <summary>
    /// Applies a PUSHF-shaped image to the live flags the way POPF and IRET
    /// do below IOPL 3 with CR4.VME on (Table 20-2 methods for POPF/IRET):
    /// the image's IF bit becomes VIF, not IF; the real IF, the real IOPL and
    /// VIP are the monitor's alone and this instruction does not move any of
    /// them; and two conditions fault before anything changes at all --
    /// <paramref name="trapFlagFaults"/> is what tells POPF and IRET apart,
    /// since POPF faults on TF = 1 in the image and IRET does not (Collins,
    /// "VME: Coming Out of the Cold" -- unverified against silicon here, but
    /// matched to the coordinator's note in pse-vme.md).
    /// </summary>
    private uint ApplyVirtualInterruptImage(uint image, bool trapFlagFaults)
    {
        if (trapFlagFaults && (image & TrapFlagBit) != 0) throw new CpuException(13, 0);

        // The image's IF bit is the task's request, not a fact: below IOPL 3
        // it lands on VIF, and asking for it while VIP is already 1 is
        // refused exactly as STI refuses it -- see Sti() above.
        bool wantsInterrupt = (image & InterruptFlag) != 0;
        if (wantsInterrupt && Flags.VirtualInterruptPending) throw new CpuException(13, 0);

        uint current = Flags.Value;
        const uint keptFromCurrent = InterruptFlag | IoplMask | VipFlag | ResumeFlag;
        uint next = (image & ~keptFromCurrent) | (current & keptFromCurrent);
        next = wantsInterrupt ? (next | VifFlag) : (next & ~VifFlag);

        // The identification bit only moves on a processor that has CPUID,
        // exactly as GuardFlagsFromStack enforces for the ordinary path.
        if (!State.Features.Has(CpuFeature.Identification))
            next = (next & ~IdentificationFlag) | (current & IdentificationFlag);

        return next;
    }

    // --- INT n redirection (Table 20-2 methods 2-5) ---------------------------

    /// <summary>
    /// Whether CR4.VME redirects this <c>INT n</c> (opcode CD) to the V86
    /// task's own real-mode handler instead of taking it through the IDT.
    /// INT3/INTO/INT1 (opcodes CC/CE/F1) are never asked this question at
    /// all -- pse-vme.md test 17 and SDM Table 20-2 both exclude them from
    /// redirection outright, and the caller in Cpu.Execute.cs only calls this
    /// from the CD dispatch, never from CC or CE's.
    /// </summary>
    private bool RedirectsSoftwareInterrupt(byte vector)
    {
        if ((State.Cr4 & CpuState.Cr4Vme) == 0) return false;
        return !InterruptRedirectionBitIsSet(vector);
    }

    /// <summary>
    /// Reads bit <paramref name="vector"/> of the interrupt redirection
    /// bitmap, which sits 32 bytes below the I/O permission bitmap's own base
    /// (TSS offset 0x66, exactly as CheckIoPermission/IoPermitted already
    /// read it in Cpu.Ops.cs). Set means "not redirected" -- the same sense
    /// the I/O bitmap uses for "not permitted" -- and, by the same analogy, a
    /// bitmap that does not reach this vector at all -- no task register, a
    /// TSS too short to hold the I/O map base word, or a map base that
    /// leaves no room for the 32 bytes below it -- is treated as if the bit
    /// were set (pse-vme.md marks this "uncertain"; unverified against
    /// silicon, this is the stated default).
    /// </summary>
    private bool InterruptRedirectionBitIsSet(byte vector)
    {
        if ((State.TaskRegister & 0xFFFC) == 0) return true;

        ulong descriptor = ReadDescriptor(State.TaskRegister);
        uint tssBase = (uint)(((descriptor >> 16) & 0xFFFFFF) | ((descriptor >> 32) & 0xFF000000));
        uint tssLimit = (uint)((descriptor & 0xFFFF) | ((descriptor >> 32) & 0x0F0000));
        if (((descriptor >> 55) & 1) != 0) tssLimit = (tssLimit << 12) | 0xFFF;

        if (tssLimit < 0x67) return true;

        // The processor consulting its own task state segment: a supervisor
        // access even though the task runs at CPL 3, exactly like the I/O
        // permission bitmap beside it (see TranslateSupervisor). An OS maps
        // its TSS without the user bit, and reading it with the task's own
        // privilege would page-fault on every software interrupt.
        uint mapBase = _memory.ReadWord(TranslateSupervisor(tssBase + 0x66, false));

        if (mapBase < 32) return true;   // no room for the 32 bytes below it
        uint byteOffset = mapBase - 32 + (uint)(vector >> 3);
        if (byteOffset > tssLimit) return true;

        int bits = _memory.ReadByte(TranslateSupervisor(tssBase + byteOffset, false));
        return (bits & (1 << (vector & 7))) != 0;
    }

    /// <summary>
    /// Delivers a redirected <c>INT n</c> (Table 20-2 method 4 at IOPL 3,
    /// method 5 below it): the interrupt never leaves virtual-8086 mode at
    /// all. It is built exactly the way a real 8086 builds one -- a 16-bit
    /// FLAGS/CS/IP frame on the task's own stack, vectored through the
    /// task's own IVT at linear <c>vector*4</c> -- except that below IOPL 3
    /// the pushed FLAGS word is method 5's PUSHF-shaped image
    /// (<see cref="VirtualizedFlagsImage"/>: VIF standing in for IF, IOPL
    /// read back as 3), not the raw register -- the handler must not learn
    /// it is not actually running with real interrupts under it, any more
    /// than an ordinary virtualized PUSHF would let it learn that. At
    /// IOPL 3, method 4 pushes the plain register, exactly as a real-mode
    /// INT always has. What closes behind either frame is whichever of
    /// IF/VIF the task was trusted with, the same split CLI/STI/PUSHF/POPF
    /// already make. AC's fate on this path is unverified against silicon
    /// (pse-vme.md marks it "uncertain"); this leaves AC alone, i.e. treats
    /// the redirected entry as an ordinary real-mode INT, which is the
    /// note's stated default.
    ///
    /// The vector is read before anything is pushed, not after, so that a
    /// page fault while fetching it -- routine under a monitor that pages
    /// its V86 tasks -- leaves the stack exactly as it was rather than
    /// stranding an extra FLAGS/CS/IP frame that a retry of this same
    /// instruction would then push a second copy of.
    /// </summary>
    private void RedirectV86SoftwareInterrupt(byte vector)
    {
        bool iopl3 = CurrentIopl() == 3;

        uint tableEntry = (uint)vector * 4;
        ushort offset = ReadLinearWord(tableEntry);
        ushort segment = ReadLinearWord(tableEntry + 2);

        ushort pushedFlags = iopl3 ? (ushort)(Flags.Value & 0xFFFFu) : VirtualizedFlagsImage();
        // Each push commits SP on its own, so a fault on the second or third
        // (a stack page not present) would leave a partial frame, and the
        // restarted INT would push a whole one below it. SP is put back, as
        // the fault's restart semantics require.
        uint espBefore = State.Esp;
        try
        {
            Push16(pushedFlags);
            Push16(State.Cs.Selector);
            Push16((ushort)State.Eip);
        }
        catch (Exception exception) when (exception is CpuException or PageFaultException)
        {
            State.Esp = espBefore;
            throw;
        }

        Flags.Trap = false;
        if (iopl3)
        {
            // Method 4: the task was trusted with the real flag, so INT n
            // clears it exactly as a real-mode INT always has (pse-vme.md's
            // coordinator correction: at IOPL 3 with VME on, INT n is *not*
            // unchanged from the no-VME case the way CLI/STI/PUSHF/POPF/IRET
            // are -- the redirection bitmap is still consulted).
            Flags.Interrupt = false;
        }
        else
        {
            // Method 5: the task never had the real flag, so what closes
            // behind it is the virtual one; the monitor's own IF is
            // untouched.
            Flags.VirtualInterrupt = false;
        }

        LoadSegment(SegReg.Cs, segment);
        State.Eip = offset;
    }
}
