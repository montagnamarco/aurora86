namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    // Protected mode. What is implemented so far is the slice needed to run
    // extended memory managers such as HIMEM.SYS: they enter protected mode
    // only to copy blocks above the one-megabyte line and drop straight back
    // out, so they need segment descriptors but never privilege transitions,
    // task gates or call gates.

    /// <summary>
    /// Loads a segment register by reading its descriptor out of the GDT or the
    /// LDT. The selector splits into an index (bits 15-3), a table bit (bit 2)
    /// and the requested privilege level (bits 1-0).
    /// </summary>
    private void LoadSegmentProtected(SegReg segment, ushort selector)
    {
        ref SegmentCache cache = ref State.Segments[(int)segment];

        // The null selector may legitimately be loaded into a data segment
        // register: it makes the register unusable, but loading it is not an
        // error -- operating systems use it to make sure stale descriptors
        // cannot be dereferenced by accident. Into CS or SS it is an error.
        if ((selector & 0xFFFC) == 0)
        {
            if (segment is SegReg.Cs or SegReg.Ss)
                throw new CpuException(13, selector);

            cache.Selector = selector;
            cache.Base = 0;
            cache.Limit = 0;
            cache.Readable = cache.Writable = false;
            return;
        }

        ulong descriptor = ReadDescriptor(selector);

        // The descriptor's fields are scattered across the eight bytes so that
        // the layout stays backwards compatible with the 286, which only had
        // 24-bit bases and 16-bit limits: the base arrives in three pieces and
        // the limit in two, with the 386's extra bits bolted on at the top.
        uint baseAddress = (uint)(((descriptor >> 16) & 0xFFFFFF)
                                | ((descriptor >> 32) & 0xFF000000));
        uint limit = (uint)((descriptor & 0xFFFF)
                           | ((descriptor >> 32) & 0x0F0000));

        byte access = (byte)(descriptor >> 40);
        byte granularity = (byte)(descriptor >> 52);

        // The granularity bit scales the limit by 4096. The remainder of the
        // final page stays accessible, so the low twelve bits are filled with
        // ones rather than zeroed -- a 4 GB segment is encoded as a limit of
        // 0xFFFFF pages, which has to expand to 0xFFFFFFFF.
        if ((granularity & 0x08) != 0) limit = (limit << 12) | 0xFFF;

        // Bit 7 of the access byte: the descriptor is present in memory. A
        // not-present segment raises #NP (or #SS when it is the stack), which
        // is the hook a virtual memory system uses to load it on demand.
        if ((access & 0x80) == 0)
            throw new CpuException(segment == SegReg.Ss ? (byte)12 : (byte)11, selector);

        cache.Selector = selector;
        cache.Base = baseAddress;
        cache.Limit = limit;
        cache.Big = (granularity & 0x04) != 0;
        cache.Dpl = (byte)((access >> 5) & 3);

        // Bit 3 of the access byte separates code from data; for code, bit 1
        // says whether it is readable, for data whether it is writable. Code is
        // never writable, which is how the architecture keeps a program from
        // rewriting itself through its own CS.
        bool isCode = (access & 0x08) != 0;
        cache.Readable = !isCode || (access & 0x02) != 0;
        cache.Writable = !isCode && (access & 0x02) != 0;
        cache.ExpandDown = !isCode && (access & 0x04) != 0;
    }

    /// <summary>
    /// Reads the eight bytes of the descriptor named by the selector. The index
    /// has to fall inside the table's limit, otherwise the selector simply is
    /// not valid and the load raises #GP with the selector as error code.
    /// </summary>
    private ulong ReadDescriptor(ushort selector)
    {
        bool isLocalTable = (selector & 0x04) != 0;
        uint index = (uint)(selector & 0xFFF8);

        uint tableBase = isLocalTable ? State.LdtBase : State.GdtBase;
        uint tableLimit = isLocalTable ? State.LdtLimit : State.GdtLimit;

        if (index + 7 > tableLimit)
            throw new CpuException(13, selector);

        uint address = tableBase + index;
        return _memory.ReadDword(TranslateLinear(address, false))
             | ((ulong)_memory.ReadDword(TranslateLinear(address + 4, false)) << 32);
    }

    /// <summary>
    /// Which exceptions push an error code, and which do not.
    ///
    /// The error code names the descriptor that caused the fault, which is
    /// only meaningful for faults that involve one. A divide by zero has
    /// nothing to name, so it pushes nothing -- and a handler that popped a
    /// code that was never pushed would return to the wrong address, which is
    /// why this distinction has to be exact.
    /// </summary>
    private static bool ExceptionPushesErrorCode(byte vector)
        => vector is 8 or 10 or 11 or 12 or 13 or 14 or 17;

    /// <summary>
    /// Delivers an interrupt or exception through the Interrupt Descriptor
    /// Table.
    ///
    /// In real mode the vector table is 256 far pointers and there is nothing
    /// to decide. Protected mode replaces it with a table of *gates*, and a
    /// gate carries permission as well as an address: it says which privilege
    /// level may reach the handler through a software INT, and whether
    /// interrupts stay enabled once inside.
    ///
    /// That second field is the only difference between an interrupt gate and
    /// a trap gate, and it matters more than it sounds. An interrupt gate
    /// clears IF, so a device handler cannot be interrupted by the same device
    /// again halfway through. A trap gate leaves IF alone, which is what a
    /// debugger's breakpoint handler wants: the machine must keep servicing
    /// the clock and the keyboard while a human stares at the screen.
    /// </summary>
    private void EnterInterruptProtected(byte vector, uint errorCode, bool isSoftware = false)
    {
        // Each IDT entry is eight bytes, exactly like a GDT descriptor, so the
        // same limit check applies: an entry that does not fit inside the
        // table is not an entry.
        uint offsetInTable = (uint)vector * 8;
        if (offsetInTable + 7 > State.IdtLimit)
            throw new CpuException(13, (uint)(vector * 8 + 2));

        uint gateAddress = State.IdtBase + offsetInTable;
        uint low = _memory.ReadDword(TranslateLinear(gateAddress, false));
        uint high = _memory.ReadDword(TranslateLinear(gateAddress + 4, false));

        byte access = (byte)(high >> 8);
        byte gateType = (byte)(access & 0x1F);
        byte gateDpl = (byte)((access >> 5) & 3);
        bool present = (access & 0x80) != 0;

        // A gate that is not present is how an operating system traps the
        // first use of a vector it has not set up yet.
        if (!present) throw new CpuException(11, (uint)(vector * 8 + 2));

        // 32-bit gates carry the offset's upper half in the high dword; 16-bit
        // gates leave it zero, which is exactly what a 286-era handler wants.
        bool is32Bit = gateType is 0x0E or 0x0F;
        bool isTrapGate = gateType is 0x07 or 0x0F;

        if (gateType is not (0x05 or 0x06 or 0x07 or 0x0E or 0x0F))
            throw new CpuException(13, (uint)(vector * 8 + 2));

        // Task gates would switch the whole context through a TSS. Nothing we
        // run needs them, and pretending otherwise would be worse than saying
        // so plainly.
        if (gateType == 0x05)
            throw new NotImplementedException(
                $"Task gate on vector {vector} is not implemented.");

        ushort selector = (ushort)(low >> 16);
        uint handlerOffset = is32Bit
            ? (low & 0xFFFF) | (high & 0xFFFF0000)
            : low & 0xFFFF;

        // The privilege we are running at. CPL is the authority rather than
        // the selector's low bits: in virtual 8086 mode the code is ring 3
        // however its selector reads, and the two must not disagree.
        byte currentPrivilege = State.Cpl;

        // The gate's privilege is checked only for interrupts the running
        // code asked for with an INT instruction. It says who is allowed to
        // *call* the handler deliberately, which is why a monitor can keep a
        // task from invoking its private services.
        //
        // Hardware interrupts and exceptions are not subject to it. A device
        // has no privilege level, and a timer that user code could block
        // would be no timer at all; and an exception is precisely the case
        // where unprivileged code has done something that must reach the
        // handler -- checking here would turn every fault in a V86 task into
        // a second fault, and then a third.
        if (isSoftware && gateDpl < currentPrivilege)
            throw new CpuException(13, (uint)(vector * 8 + 2));

        // The VM flag lives outside EFLAGS in our state, so it has to be put
        // back into the value that goes on the stack: it is what tells the
        // monitor's IRET to return into virtual 8086 mode rather than to
        // ordinary protected-mode code.
        uint savedFlags = Flags.Value;
        if (State.Virtual8086Mode) savedFlags |= VirtualModeFlag;

        ushort savedCs = State.Cs.Selector;
        uint savedEip = State.Eip;
        ushort savedSs = State.Ss.Selector;
        uint savedEsp = State.Esp;
        ushort savedEs = 0, savedDs = 0, savedFs = 0, savedGs = 0;

        // Where the handler will run. The descriptor's low two bits are the
        // privilege it runs at, and an interrupt that raises the privilege
        // has to move to a stack belonging to that level.
        byte targetPrivilege = (byte)(ReadDescriptorPrivilege(selector));
        bool changingPrivilege = targetPrivilege < currentPrivilege;

        // Load the handler's code segment first: if the selector is bad we
        // want to fault before anything has been pushed, so the frame the
        // fault handler sees describes the original instruction.
        LoadSegmentProtected(SegReg.Cs, selector);

        if (handlerOffset > State.Cs.Limit)
            throw new CpuException(13, 0);

        // Moving to a more privileged level means moving to that level's
        // stack, taken from the task state segment.
        //
        // This is not a detail: leaving the kernel to run on a stack the user
        // controls would let user code read whatever the kernel pushes, or
        // move the stack pointer somewhere harmful and wait for the kernel to
        // write through it. The TSS exists almost entirely to hold these
        // stack pointers, one per privilege level, out of the reach of the
        // code being protected against.
        // Leaving virtual 8086 mode is always a privilege change: the task
        // runs at ring 3 and the monitor that handles its interrupts is at
        // ring 0.
        bool leavingVirtualMode = State.Virtual8086Mode;

        if (changingPrivilege || leavingVirtualMode)
        {
            (ushort newSs, uint newEsp) = ReadStackFromTss(0);

            // The task's segments have to be captured before the switch, and
            // cleared afterwards: they hold real-mode values that mean
            // nothing under protection, and leaving them loaded would let the
            // monitor dereference them by accident.
            if (leavingVirtualMode)
            {
                savedEs = State.Es.Selector;
                savedDs = State.Ds.Selector;
                savedFs = State.Fs.Selector;
                savedGs = State.Gs.Selector;
            }

            State.Virtual8086Mode = false;
            LoadSegmentProtected(SegReg.Ss, newSs);
            State.Esp = newEsp;
            State.Cpl = leavingVirtualMode ? (byte)0 : targetPrivilege;

            if (leavingVirtualMode)
            {
                State.LoadSegmentRealModeReset(SegReg.Es, 0);
                State.LoadSegmentRealModeReset(SegReg.Ds, 0);
                State.LoadSegmentRealModeReset(SegReg.Fs, 0);
                State.LoadSegmentRealModeReset(SegReg.Gs, 0);
            }
        }

        // The stack frame. Order matters: the handler's IRET pops it in
        // reverse, so flags must go on first and the return address last.
        //
        // When the privilege changed, the interrupted code's stack goes on
        // first of all, underneath everything else: IRET pops it back and
        // returns the processor to the stack it came from.
        if (is32Bit)
        {
            // From a V86 task the frame carries the four data segments as
            // well, at the very bottom. The monitor reads them to find out
            // what the task had loaded, and IRET puts them back on the way
            // in.
            if (leavingVirtualMode)
            {
                Push32(savedGs);
                Push32(savedFs);
                Push32(savedDs);
                Push32(savedEs);
            }

            if (changingPrivilege || leavingVirtualMode)
            {
                Push32(savedSs);
                Push32(savedEsp);
            }

            Push32(savedFlags);
            Push32(savedCs);
            Push32(savedEip);
            if (ExceptionPushesErrorCode(vector)) Push32(errorCode);
        }
        else
        {
            if (changingPrivilege)
            {
                Push16(savedSs);
                Push16((ushort)savedEsp);
            }

            Push16((ushort)savedFlags);
            Push16(savedCs);
            Push16((ushort)savedEip);
            if (ExceptionPushesErrorCode(vector)) Push16((ushort)errorCode);
        }

        State.Eip = handlerOffset;

        // An interrupt gate closes the door behind it; a trap gate leaves it
        // open. Both clear TF, so single-stepping does not step through the
        // handler as well as the code being debugged.
        if (!isTrapGate) Flags.Interrupt = false;
        Flags.Trap = false;
    }

    /// <summary>
    /// Whether a selector names a call gate rather than an ordinary segment.
    /// </summary>
    private bool IsCallGate(ushort selector)
    {
        if ((selector & 0xFFFC) == 0) return false;

        ulong descriptor;
        try { descriptor = ReadDescriptor(selector); }
        catch (CpuException) { return false; }

        byte access = (byte)(descriptor >> 40);

        // Bit 4 set marks an ordinary code or data segment; clear marks a
        // system descriptor, and type 4 or 12 within that is a call gate --
        // the 286's 16-bit form and the 386's 32-bit one.
        if ((access & 0x10) != 0) return false;

        byte type = (byte)(access & 0x0F);
        return type is 0x04 or 0x0C;
    }

    /// <summary>
    /// Calls through a call gate.
    ///
    /// A gate is how a program reaches a routine it is not allowed to know
    /// the address of. It names the code segment and the offset itself, so
    /// the caller supplies only the gate's selector -- the offset in the
    /// instruction is discarded. The gate's own privilege says who may call
    /// it, and the code segment's says what privilege the routine then runs
    /// at, which is how a kernel publishes an entry point that anybody may
    /// call but nobody may enter anywhere except at the top.
    ///
    /// This is the mechanism OS/2 and the 286-era protected-mode systems used
    /// for their system calls, and it is the ancestor of every trap-based
    /// system call since.
    /// </summary>
    private void CallThroughGate(ushort gateSelector)
    {
        ulong gate = ReadDescriptor(gateSelector);

        byte access = (byte)(gate >> 40);
        byte gateDpl = (byte)((gate >> 45) & 3);
        bool is32Bit = (access & 0x0F) == 0x0C;

        // Who may call it: the caller's privilege and the privilege it asked
        // for both have to be at least as trusted as the gate demands.
        //
        // This comes before the presence check, and the order is deliberate.
        // A caller that may not use the gate gets the same answer whether the
        // gate is there or not, so it cannot go probing the descriptor table
        // to find out what services exist.
        byte requestorPrivilege = (byte)(gateSelector & 3);
        if (State.Cpl > gateDpl || requestorPrivilege > gateDpl)
            throw new CpuException(13, gateSelector);

        if ((access & 0x80) == 0)
            throw new CpuException(11, gateSelector);   // #NP: gate not present

        ushort targetSelector = (ushort)(gate >> 16);
        uint targetOffset = is32Bit
            ? (uint)((gate & 0xFFFF) | ((gate >> 32) & 0xFFFF0000))
            : (uint)(gate & 0xFFFF);

        // How many arguments to copy across if the stack changes. The caller
        // pushed them on its own stack, and the routine has to find them on
        // the one it is about to run on.
        int parameterCount = (int)((gate >> 32) & 0x1F);

        byte targetPrivilege = ReadDescriptorPrivilege(targetSelector);
        bool changingPrivilege = targetPrivilege < State.Cpl;

        ushort callerSs = State.Ss.Selector;
        uint callerEsp = State.Esp;
        ushort callerCs = State.Cs.Selector;
        uint callerEip = State.Eip;

        if (changingPrivilege)
        {
            // Read the arguments off the caller's stack before switching away
            // from it.
            var parameters = new uint[parameterCount];
            for (int i = 0; i < parameterCount; i++)
                parameters[i] = is32Bit
                    ? ReadMem32(SegReg.Ss, callerEsp + (uint)(i * 4))
                    : ReadMem16(SegReg.Ss, callerEsp + (uint)(i * 2));

            (ushort newSs, uint newEsp) = ReadStackFromTss(targetPrivilege);

            LoadSegmentProtected(SegReg.Cs, targetSelector);
            LoadSegmentProtected(SegReg.Ss, newSs);
            State.Esp = newEsp;
            State.Cpl = targetPrivilege;

            // The caller's stack goes on first, so that RETF can restore it.
            if (is32Bit) { Push32(callerSs); Push32(callerEsp); }
            else { Push16(callerSs); Push16((ushort)callerEsp); }

            // Then the arguments, in the order the caller had them.
            for (int i = parameterCount - 1; i >= 0; i--)
            {
                if (is32Bit) Push32(parameters[i]);
                else Push16((ushort)parameters[i]);
            }
        }
        else
        {
            LoadSegmentProtected(SegReg.Cs, targetSelector);
        }

        // The return address, last, so RETF finds it on top.
        if (is32Bit) { Push32(callerCs); Push32(callerEip); }
        else { Push16(callerCs); Push16((ushort)callerEip); }

        State.Eip = is32Bit ? targetOffset : targetOffset & 0xFFFF;
    }

    /// <summary>Bit 17 of EFLAGS: the processor is in virtual 8086 mode.</summary>
    private const uint VirtualModeFlag = 1u << 17;

    /// <summary>
    /// Enters virtual 8086 mode from an IRET frame.
    ///
    /// The frame is larger than an ordinary one: on top of EIP, CS and
    /// EFLAGS come ESP and SS, then the four remaining segment registers.
    /// The processor pops the lot, because a task entering V86 arrives with
    /// no usable segment state of its own.
    ///
    /// Everything lands through the real-mode rules, so the task sees the
    /// flat 8086 world it expects while the monitor keeps protection,
    /// paging and the interrupt vectors under its own control.
    /// </summary>
    private void EnterVirtual8086(uint returnEip, ushort returnCs, uint returnFlags)
    {
        uint newEsp = Pop32();
        ushort newSs = (ushort)Pop32();
        ushort newEs = (ushort)Pop32();
        ushort newDs = (ushort)Pop32();
        ushort newFs = (ushort)Pop32();
        ushort newGs = (ushort)Pop32();

        // The mode has to be set before the segments are loaded: it is what
        // decides that they load the real-mode way.
        State.Virtual8086Mode = true;
        Flags.SetValue(returnFlags);

        // The reset variant, because entering the mode must impose the 64 KB
        // limit rather than inherit whatever was there. The ordinary
        // real-mode load deliberately leaves the limit alone -- that is what
        // makes unreal mode work -- and a task that kept a four-gigabyte
        // limit could reach straight out of the megabyte it is confined to.
        State.LoadSegmentRealModeReset(SegReg.Cs, returnCs);
        State.LoadSegmentRealModeReset(SegReg.Ss, newSs);
        State.LoadSegmentRealModeReset(SegReg.Es, newEs);
        State.LoadSegmentRealModeReset(SegReg.Ds, newDs);
        State.LoadSegmentRealModeReset(SegReg.Fs, newFs);
        State.LoadSegmentRealModeReset(SegReg.Gs, newGs);

        State.Eip = returnEip & 0xFFFF;
        State.Esp = newEsp;

        // A V86 task runs at ring 3 whatever its selectors say. That is what
        // makes every privileged instruction trap to the monitor.
        State.Cpl = 3;
    }

    /// <summary>
    /// The privilege level a code segment runs at, from its descriptor.
    /// </summary>
    private byte ReadDescriptorPrivilege(ushort selector)
    {
        ulong descriptor = ReadDescriptor(selector);
        return (byte)((descriptor >> 45) & 3);
    }

    /// <summary>
    /// Fetches the stack for a privilege level out of the task state segment.
    ///
    /// A 32-bit TSS holds three of these pairs, at fixed offsets: ring 0's at
    /// 4, ring 1's at 12, ring 2's at 20. There is no entry for ring 3,
    /// because nothing is ever less privileged than ring 3 and so nothing
    /// ever switches *to* it this way.
    ///
    /// The task register has to have been loaded first, with LTR. An
    /// operating system that raises privilege without a TSS in place is
    /// asking for a stack that does not exist, and the processor faults
    /// rather than guessing.
    /// </summary>
    private (ushort Selector, uint StackPointer) ReadStackFromTss(byte privilege)
    {
        if ((State.TaskRegister & 0xFFFC) == 0)
            throw new CpuException(10, State.TaskRegister);   // #TS: no task state segment

        ulong descriptor = ReadDescriptor(State.TaskRegister);

        uint tssBase = (uint)(((descriptor >> 16) & 0xFFFFFF)
                            | ((descriptor >> 32) & 0xFF000000));
        uint tssLimit = (uint)((descriptor & 0xFFFF)
                             | ((descriptor >> 32) & 0x0F0000));

        // Offsets 4, 12, 20 for rings 0, 1 and 2: each entry is ESP followed
        // by SS, and SS occupies a whole dword even though only its low half
        // means anything.
        uint offset = 4u + privilege * 8u;

        // The pair must lie inside the segment. A TSS too short to hold the
        // entry is a corrupt one, and reading past it would hand the kernel a
        // stack pointer made of whatever happened to follow in memory.
        if (offset + 6 > tssLimit)
            throw new CpuException(10, State.TaskRegister);

        uint stackPointer = _memory.ReadDword(TranslateLinear(tssBase + offset, false));
        ushort selector = (ushort)_memory.ReadDword(TranslateLinear(tssBase + offset + 4, false));

        return (selector, stackPointer);
    }

    /// <summary>
    /// Returns from an interrupt in protected mode.
    ///
    /// The simple case mirrors the entry: pop EIP, CS and EFLAGS. The
    /// interesting case is a return to *less* privileged code, which needs the
    /// stack pointer restored as well.
    ///
    /// The reason is worth spelling out. When an interrupt arrives while user
    /// code is running, the processor switches to the kernel's stack, because
    /// leaving the kernel to run on a stack the user controls would let user
    /// code read -- or corrupt -- whatever the kernel pushed. On the way back
    /// out, that switch has to be undone, so the entry sequence pushed the old
    /// SS and ESP underneath everything else, and IRET pops them again.
    ///
    /// The processor knows which case it is looking at by comparing the
    /// privilege in the popped CS selector against the current one.
    /// </summary>
    private void IretProtected()
    {
        uint espBefore = State.Esp;

        try
        {
            uint returnEip;
            ushort returnCs;
            uint returnFlags;

            if (OperandSize32)
            {
                returnEip = Pop32();
                returnCs = (ushort)Pop32();
                returnFlags = Pop32();
            }
            else
            {
                returnEip = Pop16();
                returnCs = Pop16();
                returnFlags = (Flags.Value & 0xFFFF0000) | Pop16();
            }

            // Returning into virtual 8086 mode is a different sequence again.
            //
            // This is how a monitor starts a DOS task: it builds a stack frame
            // describing where the task should begin, with the VM flag set in
            // the saved EFLAGS, and executes IRET. The processor then pops all
            // six segment registers rather than just CS, because a V86 task
            // needs its own DS, ES, FS and GS as well, and there is no
            // descriptor table to fetch them from.
            //
            // Only ring 0 may do this. Anywhere else the VM bit in the popped
            // flags is simply ignored, which is what stops a task from
            // promoting itself.
            if ((returnFlags & VirtualModeFlag) != 0 && State.Cpl == 0)
            {
                EnterVirtual8086(returnEip, returnCs, returnFlags);
                return;
            }

            byte currentPrivilege = (byte)(State.Cs.Selector & 3);
            byte returnPrivilege = (byte)(returnCs & 3);

            if (returnPrivilege > currentPrivilege)
            {
                // Going out to less privileged code: the entry sequence left
                // the caller's stack underneath ours.
                uint returnEsp;
                ushort returnSs;

                if (OperandSize32)
                {
                    returnEsp = Pop32();
                    returnSs = (ushort)Pop32();
                }
                else
                {
                    returnEsp = Pop16();
                    returnSs = Pop16();
                }

                LoadSegmentProtected(SegReg.Cs, returnCs);
                if (returnEip > State.Cs.Limit) throw new CpuException(13, 0);

                LoadSegmentProtected(SegReg.Ss, returnSs);
                State.Esp = returnEsp;
                State.Eip = returnEip;
            }
            else
            {
                LoadSegmentProtected(SegReg.Cs, returnCs);
                if (returnEip > State.Cs.Limit) throw new CpuException(13, 0);
                State.Eip = returnEip;
            }

            Flags.SetValue(returnFlags);
        }
        catch (CpuException)
        {
            // A fault partway through must leave the stack as it was, so the
            // handler sees the IRET that failed rather than a half-unwound
            // frame.
            State.Esp = espBefore;
            throw;
        }
    }
}
