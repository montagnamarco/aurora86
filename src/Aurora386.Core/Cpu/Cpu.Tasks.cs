// Hardware task switching: the TSS in its 286 and 386 shapes, saving the
// outgoing task and loading the incoming one, the busy bit, the back
// link for nested tasks. Windows 3.x uses it for the V86 monitor's stack
// switches rather than for multitasking, and the I/O permission bitmap
// in the TSS is how it traps a DOS program's port accesses.

namespace Aurora386.Core.Cpu;

/// <summary>
/// Hardware task switching: the 386's own idea of what a process is.
///
/// The processor can hold an entire execution context -- every register, the
/// segment selectors, the flags, the page directory, the local descriptor
/// table -- in a structure in memory called a task state segment, and swap
/// one for another as a single operation. A JMP or CALL whose selector names
/// a TSS does not jump anywhere: it saves the running context into the
/// current TSS, loads the new one, and carries on wherever that context had
/// left off.
///
/// It was meant to be the way multitasking was done, and almost nobody did it
/// that way. Saving and restoring a hundred bytes of state through memory is
/// slower than the dozen registers an operating system actually needs to
/// swap, so Unix, Windows and everything since keep one TSS and switch tasks
/// in software. The mechanism survives for two things it is still the only
/// way to do:
///
///   * The double fault. When the processor cannot deliver an exception --
///     typically because the stack it would push onto is unusable -- it has
///     nowhere to save anything. A task gate on vector 8 sidesteps the
///     problem entirely: the handler arrives with its own stack, its own
///     page directory and its own everything, none of it read through the
///     broken state. Windows in enhanced mode does exactly this.
///
///   * Virtual 8086 tasks entered from a task gate rather than an IRET.
///
/// A switch is one of the few operations that must be atomic in a way the
/// emulator has to arrange deliberately: it reads a great deal, writes a
/// great deal, and a fault partway through would leave two tasks each holding
/// half a context. So everything that can fault is done before anything is
/// written, and the new context is assembled in locals before a single
/// register moves.
/// </summary>
public sealed partial class Cpu
{
    // The four ways into a task switch. They differ in what the outgoing task
    // is told about the incoming one, and in whether the link back is kept.
    private enum TaskSwitchKind
    {
        /// <summary>JMP to a TSS: no link back, the old task is simply left.</summary>
        Jump,

        /// <summary>CALL to a TSS, or an interrupt through a task gate: the
        /// new task is linked back to the old, and NT is set so that its IRET
        /// returns to it.</summary>
        Call,

        /// <summary>IRET with NT set: returns to the task named in the link.</summary>
        Return,
    }

    /// <summary>
    /// Whether a selector names a task state segment, which is what turns a
    /// far JMP or CALL into a task switch rather than a jump.
    /// </summary>
    private bool IsTaskSegment(ushort selector)
    {
        if ((selector & 0xFFFC) == 0) return false;

        // A TSS always lives in the global table. A selector claiming to find
        // one in the LDT is malformed, and saying "not a TSS" here lets the
        // ordinary path raise the fault with the right error code.
        if ((selector & 0x04) != 0) return false;

        ulong descriptor;
        try { descriptor = ReadDescriptor(selector); }
        catch (CpuException) { return false; }

        byte access = (byte)(descriptor >> 40);
        if ((access & 0x10) != 0) return false;      // an ordinary segment

        // Types 1 and 3 are the 286's, 9 and B the 386's; the odd one of each
        // pair is the busy form, which means the task is already running
        // somewhere in the chain.
        return (access & 0x0F) is 0x01 or 0x03 or 0x09 or 0x0B;
    }

    /// <summary>
    /// Whether a selector names a task gate. A task gate is an indirection:
    /// it lives in the IDT or a descriptor table and holds the selector of
    /// the TSS to switch to, which lets an interrupt vector reach a task
    /// without the IDT entry having to be a TSS itself.
    /// </summary>
    private bool IsTaskGateSelector(ushort selector)
    {
        if ((selector & 0xFFFC) == 0) return false;

        ulong descriptor;
        try { descriptor = ReadDescriptor(selector); }
        catch (CpuException) { return false; }

        byte access = (byte)(descriptor >> 40);
        return (access & 0x10) == 0 && (access & 0x0F) == 0x05;
    }

    /// <summary>
    /// Switches to the task named by a selector, which may be a TSS or a task
    /// gate pointing at one.
    ///
    /// <paramref name="errorCode"/> is pushed onto the new task's stack after
    /// the switch, for the exceptions that carry one. It has to happen after
    /// rather than before: the whole point of a task gate on the double fault
    /// is that the old stack may be exactly what is broken.
    /// </summary>
    private void SwitchTask(ushort selector, TaskSwitchKind kind, uint? errorCode = null)
    {
        ushort taskSelector = selector;

        // A task gate is resolved first: it contributes nothing but the
        // selector it holds. Its own privilege was already checked by
        // whoever decided to come through it.
        if (IsTaskGateSelector(selector))
        {
            ulong gate = ReadDescriptor(selector);
            taskSelector = (ushort)(gate >> 16);
        }

        ulong incoming = ReadDescriptor(taskSelector);
        byte access = (byte)(incoming >> 40);
        byte type = (byte)(access & 0x0F);

        if ((access & 0x10) != 0 || type is not (0x01 or 0x03 or 0x09 or 0x0B))
            throw new CpuException(13, taskSelector);

        if ((access & 0x80) == 0)
            throw new CpuException(11, taskSelector);       // TSS not present

        // The busy bit is the processor's own re-entrancy guard. A task that
        // is already running cannot be entered again -- its TSS still holds
        // the state of the suspended activation, and a second entry would
        // overwrite it. The one exception is the return path, which is
        // *expected* to find the outer task busy: that is what busy means.
        bool incomingBusy = type is 0x03 or 0x0B;
        if (kind == TaskSwitchKind.Return)
        {
            if (!incomingBusy) throw new CpuException(13, taskSelector);
        }
        else if (incomingBusy)
        {
            throw new CpuException(13, taskSelector);
        }

        bool incomingIs386 = type is 0x09 or 0x0B;
        uint incomingBase = DescriptorBase(incoming);
        uint incomingLimit = DescriptorLimitOf(incoming);

        // A 386 TSS is 0x67 bytes at minimum, a 286 one 0x2B. A limit shorter
        // than the fields the processor is about to read is a #TS, not a
        // silent short read: the operating system has built the structure
        // wrong and needs to hear about it.
        uint required = incomingIs386 ? 0x67u : 0x2Bu;
        if (incomingLimit < required) throw new CpuException(10, taskSelector);

        // --- everything that can fault, before anything is written ---------
        //
        // The new context is read out in full here. If any of these reads
        // faults -- an absent page under the TSS, most likely -- nothing has
        // moved yet and the fault is reported against the old task, which can
        // still be resumed.
        TaskContext next = incomingIs386
            ? Read386TaskState(incomingBase)
            : Read286TaskState(incomingBase);

        // --- save the outgoing task ----------------------------------------
        //
        // Always, in every direction. A task that is being returned *from*
        // still has to leave its registers behind: it may be entered again
        // later, and it would resume with whatever the incoming task happened
        // to leave in them. SaveCurrentTask does nothing when the task
        // register names no task, which is the state of a machine that has
        // never executed LTR.
        //
        // The nested task flag is cleared out of the saved image on a return:
        // the link it refers to is being followed right now, and a task
        // resumed later with NT still set would try to follow it again.
        if (kind == TaskSwitchKind.Return)
            Flags.SetValue(Flags.Value & ~(uint)Aurora386.Core.Cpu.Flags.NestedTask);

        SaveCurrentTask();

        // --- the busy bits and the link ------------------------------------
        //
        // Ordering matters here in one specific way: on a return the outgoing
        // task is cleared to available, and on a jump too, but on a call the
        // outgoing task stays busy because it is still on the chain.
        if (kind is TaskSwitchKind.Jump or TaskSwitchKind.Return)
            SetTaskBusy(State.TaskRegister, busy: false);

        if (kind != TaskSwitchKind.Return)
            SetTaskBusy(taskSelector, busy: true);

        if (kind == TaskSwitchKind.Call)
        {
            // The link field at offset 0 of the incoming TSS records where to
            // come back to, and NT in the new task's flags is what makes its
            // IRET follow that link instead of popping a return frame.
            WriteTssWord(incomingBase, 0, State.TaskRegister);
            next.EFlags |= (uint)Aurora386.Core.Cpu.Flags.NestedTask;
        }
        else if (kind == TaskSwitchKind.Jump)
        {
            // A jump leaves no way back, so the incoming task must not claim
            // to have one.
            next.EFlags &= ~(uint)Aurora386.Core.Cpu.Flags.NestedTask;
        }

        // --- install the new context ---------------------------------------
        State.TaskRegister = taskSelector;
        State.TssBase = incomingBase;
        State.TssLimit = incomingLimit;

        // CR3 first: every address the segment loads are about to translate
        // belongs to the new task's address space, not the old one. Under
        // CR4.PAE this is also one of the points that reloads the four PDPT
        // registers (SDM Vol.3A §4.4.1; see LoadPdptRegisters in
        // Cpu.Paging.cs), from the incoming task's CR3 rather than the
        // outgoing one. With CR4.PGE set, the reload keeps whatever global
        // entries survive it instead of dropping everything, exactly as MOV
        // CR3 does (FlushTlbForCr3Load).
        if (incomingIs386 && State.PagingEnabled)
        {
            // Long mode has no PDPT registers (and no hardware task switch
            // at all: stage D of plans/long-mode.md makes it #GP).
            if ((State.Cr4 & CpuState.Cr4Pae) != 0 && !State.LongModeActive) LoadPdptRegisters(next.Cr3);

            State.Cr3 = next.Cr3;
            FlushTlbForCr3Load();
        }

        for (int i = 0; i < 8; i++) State.Gpr[i] = next.Gpr[i];
        State.Eip = next.Eip;
        Flags.SetValue(next.EFlags);

        // The task's own local descriptor table, before any selector that
        // might live in it is loaded.
        State.Virtual8086Mode = (next.EFlags & (uint)Aurora386.Core.Cpu.Flags.Virtual8086) != 0;
        LoadTaskLocalDescriptorTable(next.Ldtr);

        // CS carries the privilege the task runs at, so it decides CPL, and
        // the rest are loaded under that.
        if (State.Virtual8086Mode)
        {
            State.CplRaw = 3;
            LoadSegmentRealModeAll(next);
        }
        else
        {
            State.CplRaw = (byte)(next.Cs & 3);
            LoadSegment(SegReg.Cs, next.Cs);
            LoadSegment(SegReg.Ss, next.Ss);
            LoadSegment(SegReg.Ds, next.Ds);
            LoadSegment(SegReg.Es, next.Es);
            if (incomingIs386)
            {
                LoadSegment(SegReg.Fs, next.Fs);
                LoadSegment(SegReg.Gs, next.Gs);
            }
        }

        // TS tells the coprocessor's state that it belongs to somebody else
        // now. The next floating point instruction traps with #NM, and the
        // handler saves the old task's registers only if there were any --
        // which is how a system avoids saving eighty bytes of FPU state for
        // the many tasks that never touch it.
        State.Cr0 |= CpuState.Cr0TaskSwitched;

        // The error code goes onto the *new* task's stack, which is the whole
        // reason a task gate is worth having on a fault: whatever was wrong
        // with the old stack, this one is fresh.
        if (errorCode is { } code)
        {
            if (incomingIs386) Push32(code);
            else Push16((ushort)code);
        }

        InvalidateFetchPage();
    }

    /// <summary>
    /// Whether the nested-task flag is set, which is what makes an IRET
    /// return to a task rather than pop a return frame.
    /// </summary>
    private bool NestedTaskSet
        => (Flags.Stored & Aurora386.Core.Cpu.Flags.NestedTask) != 0;

    /// <summary>
    /// A task's context, read out of its TSS before anything is committed.
    /// </summary>
    private struct TaskContext
    {
        public uint Cr3;
        public uint Eip;
        public uint EFlags;
        public uint[] Gpr;
        public ushort Es, Cs, Ss, Ds, Fs, Gs, Ldtr;
    }

    private TaskContext Read386TaskState(uint tssBase)
    {
        var context = new TaskContext { Gpr = new uint[8] };

        context.Cr3 = ReadTssDword(tssBase, 0x1C);
        context.Eip = ReadTssDword(tssBase, 0x20);
        context.EFlags = ReadTssDword(tssBase, 0x24);

        // The general registers sit at 0x28 in the architectural order --
        // EAX ECX EDX EBX ESP EBP ESI EDI -- which is the same order the
        // register file uses, so the index doubles as the offset.
        for (int i = 0; i < 8; i++)
            context.Gpr[i] = ReadTssDword(tssBase, (uint)(0x28 + i * 4));

        context.Es = ReadTssWord(tssBase, 0x48);
        context.Cs = ReadTssWord(tssBase, 0x4C);
        context.Ss = ReadTssWord(tssBase, 0x50);
        context.Ds = ReadTssWord(tssBase, 0x54);
        context.Fs = ReadTssWord(tssBase, 0x58);
        context.Gs = ReadTssWord(tssBase, 0x5C);
        context.Ldtr = ReadTssWord(tssBase, 0x60);

        return context;
    }

    /// <summary>
    /// The 286 layout, which is not the 386's with the top halves cut off but
    /// a different structure: sixteen-bit fields in a tighter arrangement,
    /// no CR3 because the 286 had no paging, and no FS or GS because it had
    /// neither.
    /// </summary>
    private TaskContext Read286TaskState(uint tssBase)
    {
        var context = new TaskContext { Gpr = new uint[8] };

        context.Eip = ReadTssWord(tssBase, 0x0E);
        context.EFlags = ReadTssWord(tssBase, 0x10);

        for (int i = 0; i < 8; i++)
            context.Gpr[i] = ReadTssWord(tssBase, (uint)(0x12 + i * 2));

        context.Es = ReadTssWord(tssBase, 0x22);
        context.Cs = ReadTssWord(tssBase, 0x24);
        context.Ss = ReadTssWord(tssBase, 0x26);
        context.Ds = ReadTssWord(tssBase, 0x28);
        context.Ldtr = ReadTssWord(tssBase, 0x2A);

        return context;
    }

    /// <summary>
    /// Writes the running context back into the current task's TSS, so that
    /// switching away and later returning resumes exactly where it left off.
    ///
    /// EIP is saved as it stands, which is the instruction *after* the one
    /// that caused the switch: a task resumed by a later JMP carries on past
    /// its own jump rather than executing it again forever.
    /// </summary>
    private void SaveCurrentTask()
    {
        if ((State.TaskRegister & 0xFFFC) == 0) return;

        ulong current = ReadDescriptor(State.TaskRegister);
        bool is386 = ((byte)(current >> 40) & 0x0F) is 0x09 or 0x0B;
        uint tssBase = DescriptorBase(current);

        if (is386)
        {
            WriteTssDword(tssBase, 0x20, State.Eip);
            WriteTssDword(tssBase, 0x24, Flags.Value);
            for (int i = 0; i < 8; i++)
                WriteTssDword(tssBase, (uint)(0x28 + i * 4), State.GetReg32(i));

            WriteTssWord(tssBase, 0x48, State.Es.Selector);
            WriteTssWord(tssBase, 0x4C, State.Cs.Selector);
            WriteTssWord(tssBase, 0x50, State.Ss.Selector);
            WriteTssWord(tssBase, 0x54, State.Ds.Selector);
            WriteTssWord(tssBase, 0x58, State.Fs.Selector);
            WriteTssWord(tssBase, 0x5C, State.Gs.Selector);
            WriteTssWord(tssBase, 0x60, State.Ldtr);

            // CR3 is deliberately not written back. It is an input to the
            // task, not part of what the task does: an operating system sets
            // it once when it builds the TSS, and a task that rewrote it on
            // every switch could never have its address space changed from
            // outside.
        }
        else
        {
            WriteTssWord(tssBase, 0x0E, (ushort)State.Eip);
            WriteTssWord(tssBase, 0x10, (ushort)Flags.Value);
            for (int i = 0; i < 8; i++)
                WriteTssWord(tssBase, (uint)(0x12 + i * 2), (ushort)State.Gpr[i]);

            WriteTssWord(tssBase, 0x22, State.Es.Selector);
            WriteTssWord(tssBase, 0x24, State.Cs.Selector);
            WriteTssWord(tssBase, 0x26, State.Ss.Selector);
            WriteTssWord(tssBase, 0x28, State.Ds.Selector);
            WriteTssWord(tssBase, 0x2A, State.Ldtr);
        }
    }

    /// <summary>
    /// Sets or clears a task's busy bit, in the descriptor table rather than
    /// in any cached copy: the bit is shared state, and another task
    /// examining the table has to see it.
    /// </summary>
    private void SetTaskBusy(ushort selector, bool busy)
    {
        if ((selector & 0xFFFC) == 0) return;

        uint address = State.GdtBase + (uint)(selector & 0xFFF8) + 5;
        uint physical = TranslateSupervisor(address, true);
        byte access = _memory.ReadByte(physical);

        // Bit 1 of the type is the busy bit: 1 -> 3 and 9 -> B, and back.
        byte updated = busy ? (byte)(access | 0x02) : (byte)(access & ~0x02);
        _memory.WriteByte(physical, updated);
    }

    /// <summary>
    /// Loads the incoming task's local descriptor table. A task with no LDT
    /// of its own -- selector zero -- is perfectly ordinary: it simply has
    /// every one of its segments in the global table.
    /// </summary>
    private void LoadTaskLocalDescriptorTable(ushort selector)
    {
        if ((selector & 0xFFFC) == 0)
        {
            State.Ldtr = 0;
            State.LdtBase = 0;
            State.LdtLimit = 0;
            return;
        }

        ulong descriptor = ReadDescriptor(selector);
        byte access = (byte)(descriptor >> 40);

        if ((access & 0x10) != 0 || (access & 0x0F) != 0x02)
            throw new CpuException(10, selector);        // not an LDT: #TS
        if ((access & 0x80) == 0)
            throw new CpuException(10, selector);        // LDT not present

        State.Ldtr = selector;
        State.LdtBase = DescriptorBase(descriptor);
        State.LdtLimit = DescriptorLimitOf(descriptor);
    }

    /// <summary>
    /// Loads the six segments of a virtual 8086 task, which are not selectors
    /// at all but paragraph numbers: base = selector * 16, limit 64 KB, and
    /// no descriptor consulted anywhere.
    /// </summary>
    private void LoadSegmentRealModeAll(TaskContext context)
    {
        State.LoadSegmentRealMode(SegReg.Es, context.Es);
        State.LoadSegmentRealMode(SegReg.Cs, context.Cs);
        State.LoadSegmentRealMode(SegReg.Ss, context.Ss);
        State.LoadSegmentRealMode(SegReg.Ds, context.Ds);
        State.LoadSegmentRealMode(SegReg.Fs, context.Fs);
        State.LoadSegmentRealMode(SegReg.Gs, context.Gs);
    }

    // --- reads and writes inside a TSS ----------------------------------------
    //
    // These go through the linear translation because a TSS lives in the
    // address space like anything else, and an operating system is entitled
    // to page one out. What they must not do is go through the segment
    // machinery: the TSS is reached by its descriptor's base address, not
    // through any loaded segment.

    private ushort ReadTssWord(uint tssBase, uint offset)
        => _memory.ReadWord(TranslateSupervisor(tssBase + offset, false));

    private uint ReadTssDword(uint tssBase, uint offset)
        => _memory.ReadDword(TranslateSupervisor(tssBase + offset, false));

    private void WriteTssWord(uint tssBase, uint offset, ushort value)
        => _memory.WriteWord(TranslateSupervisor(tssBase + offset, true), value);

    private void WriteTssDword(uint tssBase, uint offset, uint value)
        => _memory.WriteDword(TranslateSupervisor(tssBase + offset, true), value);
}
