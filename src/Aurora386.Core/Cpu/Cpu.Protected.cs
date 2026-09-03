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
    private void EnterInterruptProtected(byte vector, uint errorCode)
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

        // The privilege check applies only to software interrupts. Hardware
        // must always get through: a device does not have a privilege level,
        // and a timer that user code could block would be no timer at all.
        // (Our INT instruction path is the only caller that can be user code,
        // and in real terms everything we run is at ring 0 today -- but the
        // check belongs here so it is right when that changes.)
        // The privilege we are running at. CPL is the authority rather than
        // the selector's low bits: in virtual 8086 mode the code is ring 3
        // however its selector reads, and the two must not disagree.
        byte currentPrivilege = State.Cpl;
        if (gateDpl < currentPrivilege)
            throw new CpuException(13, (uint)(vector * 8 + 2));

        uint savedFlags = Flags.Value;
        ushort savedCs = State.Cs.Selector;
        uint savedEip = State.Eip;
        ushort savedSs = State.Ss.Selector;
        uint savedEsp = State.Esp;

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
        if (changingPrivilege)
        {
            (ushort newSs, uint newEsp) = ReadStackFromTss(targetPrivilege);

            LoadSegmentProtected(SegReg.Ss, newSs);
            State.Esp = newEsp;
            State.Cpl = targetPrivilege;
        }

        // The stack frame. Order matters: the handler's IRET pops it in
        // reverse, so flags must go on first and the return address last.
        //
        // When the privilege changed, the interrupted code's stack goes on
        // first of all, underneath everything else: IRET pops it back and
        // returns the processor to the stack it came from.
        if (is32Bit)
        {
            if (changingPrivilege)
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

            // Returning into virtual 8086 mode is a different sequence again:
            // the processor pops all six segment registers. Nothing we run
            // does this yet, so refuse clearly rather than half-doing it.
            if ((returnFlags & 0x00020000) != 0)
                throw new NotImplementedException(
                    "IRET into virtual 8086 mode is not implemented.");

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
