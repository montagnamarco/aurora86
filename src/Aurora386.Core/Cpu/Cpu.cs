using System.Runtime.CompilerServices;
using Aurora386.Core.IO;
using Aurora386.Core.Memory;

using Aurora386.Core.Devices.Interrupts;
namespace Aurora386.Core.Cpu;

/// <summary>
/// The 80386 interpreter.
///
/// This part holds the architectural state, the instruction fetch and address
/// translation; ModR/M decoding and opcode execution live in the partial files
/// Cpu.ModRm.cs and Cpu.Execute.cs.
/// </summary>
public sealed partial class Cpu
{
    public readonly CpuState State = new();
    public LazyFlags Flags;

    private readonly PhysicalMemoryBus _memory;
    private readonly IoBus _io;

    /// <summary>Prefixes of the instruction currently being decoded.</summary>
    private Prefixes _prefixes;

    /// <summary>
    /// EIP of the current instruction, saved before the fetch. It lets us
    /// restart the instruction from the top when an exception cuts it short
    /// halfway: a page fault has to be serviceable and the instruction then
    /// replayed, which only works if we know where it began.
    /// </summary>
    private uint _instructionStart;

    /// <summary>Cycles consumed, used by the timers to keep in step.</summary>
    public long Cycles { get; private set; }

    /// <summary>
    /// Counts cycles for instructions that were never actually stepped: the
    /// batch loop (<see cref="Aurora386.Core.Machine.RunInstructions"/>) still
    /// has to advance the devices while the CPU sits in HLT, and if a
    /// pending interrupt is what will eventually wake it, that waiting time
    /// is exactly as real to the time-stamp counter as time spent executing.
    /// Without this, RDTSC on a machine that spends most of its life halted
    /// -- almost every guest, between keystrokes -- would report far fewer
    /// cycles than the wall-clock time the rest of the emulator advanced by.
    /// </summary>
    public void AccountIdleCycles(long steps) => Cycles += steps;

    /// <summary>
    /// The offset RDTSC's counter runs at ahead of or behind
    /// <see cref="Cycles"/>. Zero until a WRMSR to the time-stamp-counter MSR
    /// (index 0x10) moves it; see <see cref="WriteTsc"/>.
    /// </summary>
    private long _tscOffset;

    /// <summary>
    /// The 64-bit time-stamp counter RDTSC and RDMSR(0x10) both read: one
    /// cycle per instruction retired, the same approximation
    /// <see cref="Cycles"/> already makes, offset by whatever the last
    /// WRMSR to it asked for. Wraps the way real 64-bit hardware would
    /// rather than throwing, which matters once a guest deliberately writes
    /// a value near the top of the range to test wraparound handling.
    /// </summary>
    public ulong Tsc => unchecked((ulong)(Cycles + _tscOffset));

    /// <summary>
    /// WRMSR to the time-stamp-counter MSR on a P5: writes the low 32 bits
    /// and clears the high 32 (cpu-models.md §2, SDM Vol.3 §17 footnote on
    /// TSC write semantics) -- later cores allow a full 64-bit write, but
    /// that is out of scope for this work package's Pentium-only model.
    /// Implemented as an offset adjustment so <see cref="Cycles"/> itself
    /// never has to move backwards.
    /// </summary>
    public void WriteTsc(uint low) => _tscOffset = unchecked((long)low - (Cycles + 1));

    /// <summary>
    /// The model-specific registers this processor answers RDMSR/WRMSR
    /// against. Owned by the Cpu instance, not static, because the register
    /// values themselves (the TSC aside, which lives in <see cref="Cycles"/>
    /// and <see cref="_tscOffset"/>) are per-CPU state a snapshot has to
    /// carry.
    /// </summary>
    public ModelSpecificRegisters Msrs { get; }

    /// <summary>
    /// The maskable interrupt source: the plain 8259 pair on a machine with
    /// no on-die APIC engaged, or the local APIC wrapper (work package
    /// P4.4) on one that has. Without one the CPU never sees a hardware
    /// interrupt, which is fine for unit tests but not for a whole machine.
    /// See <see cref="Devices.Interrupts.IInterruptSource"/> for why this is
    /// an interface rather than the concrete 8259 pair it always was before.
    /// </summary>
    public Devices.Interrupts.IInterruptSource? Interrupts { get; set; }

    /// <summary>
    /// STI does not enable interrupts on the spot: the window opens only after
    /// the following instruction has retired. That one-instruction delay is
    /// what makes the "STI; HLT" idiom atomic -- without it an interrupt could
    /// slip in between the two, and the HLT would then never be woken up.
    /// </summary>
    private bool _interruptShadow;

    public Cpu(PhysicalMemoryBus memory, IoBus? io = null)
    {
        _memory = memory;
        // With no devices attached every port reads back 0xFF, which is also
        // what a real PC returns for an empty slot.
        _io = io ?? new IoBus();
        Msrs = new ModelSpecificRegisters(this);
        Reset();
    }

    public void Reset()
    {
        State.Reset();
        Flags = default;
        Flags.SetValue((uint)Aurora386.Core.Cpu.Flags.Reserved1);
        _prefixes.Clear();
        Cycles = 0;
        _tscOffset = 0;

        // RESET clears the performance-monitoring MSRs along with the TSC
        // (SDM vol. 3, table 9-1); only INIT would have kept them.
        Msrs.Reset();

        // And the SSE state: XMM0-7 zero and MXCSR 1F80h, its power-on value.
        Sse.Reset();

        // The four PAE PDPT registers are as much a part of RESET's initial
        // state as CR3 itself (State.Reset() already zeroes that): a stale
        // qword surviving a reset would translate through whatever the
        // previous guest's page tables happened to leave there the moment
        // CR4.PAE and CR0.PG are turned back on, before the new guest has
        // written a CR3 of its own at all.
        Array.Clear(Pdpte);

        // A local APIC's own registers go back to their power-on values on
        // RESET too (SDM 10.4.7), independently of whatever the APIC_BASE
        // MSR's hardware-enable bit is doing (Msrs.Reset() above already
        // fires that event, but only a 0-to-1 transition of the enable bit
        // resets the registers -- a plain RESET must do it unconditionally).
        if (Interrupts is Devices.Interrupts.IResettableInterruptSource resettableInterrupts)
        {
            resettableInterrupts.ResetOnCpuReset();
        }
    }

    // --- effective operand and address sizes ---------------------------------
    // The D bit of the CS descriptor sets the default (16-bit in real mode and
    // in 16-bit segments, 32-bit in 32-bit ones); the 0x66/0x67 prefixes flip
    // it for a single instruction.

    private bool OperandSize32
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => State.Cs.Big ^ _prefixes.OperandSize;
    }

    private bool AddressSize32
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => State.Cs.Big ^ _prefixes.AddressSize;
    }

    // --- address translation --------------------------------------------------

    /// <summary>
    /// Turns a logical address (segment:offset) into a linear one. When paging
    /// is on, the linear-to-physical step happens afterwards in
    /// <see cref="TranslateLinear"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint LinearAddress(SegReg segment, uint offset)
        => State.Segments[(int)segment].Base + offset;

    /// <summary>
    /// Linear -> physical. With paging off it is the identity; with paging on
    /// it walks the page directory -- the ordinary two-level one, or PAE's
    /// three-level one when CR4.PAE is set (<see cref="TranslateWhenPaging"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint TranslateLinear(uint linear, bool write)
    {
        if (!State.PagingEnabled) return linear;
        return TranslateWhenPaging(linear, write);
    }

    /// <summary>
    /// Translate a linear address for an access the PROCESSOR makes on its own
    /// behalf, rather than one the running program asked for.
    ///
    /// Reading a descriptor out of the GDT or LDT, reading a gate out of the
    /// IDT, reading the kernel stack pointer out of the task state segment:
    /// all of these are the processor consulting its own tables, and they are
    /// supervisor accesses however privileged the code that triggered them
    /// happens to be. The architecture is explicit about it, and the reason is
    /// plain once stated -- an operating system maps those tables
    /// kernel-only, exactly so that ring 3 cannot read them, and a processor
    /// that applied the running privilege to its own table walks could never
    /// reach them while user code was running.
    ///
    /// Getting this wrong produces a defect that only appears with a REALISTIC
    /// page layout, which is why it survived: a test that maps everything
    /// user-accessible, as our first ring 3 tests did, never triggers it. The
    /// guest that found it was an operating system mapping its own memory the
    /// way operating systems do.
    ///
    /// THE AUTHORITY IS PASSED DOWN, NOT BORROWED, and the distinction is the
    /// whole of a second defect found the same way.
    ///
    /// This method used to write ring 0 into State.Cpl for the duration of the
    /// walk and put the old value back afterwards. That reads as though it
    /// works, and in ordinary protected mode it does. It is a NO-OP in virtual
    /// 8086 mode: Cpl is a computed property whose getter returns 3 whenever
    /// the mode is on, without consulting the field the setter writes --
    /// because in that mode ring 3 is a consequence of the mode rather than a
    /// stored number, which is exactly why the property is computed.
    ///
    /// So the borrow silently failed in the one mode where every interrupt
    /// delivery needs it. A V86 task takes hardware interrupts like anything
    /// else, and every delivery reads a gate out of the IDT; on a guest that
    /// maps the IDT supervisor-only, that read faulted, the page fault's own
    /// delivery faulted the same way, and the machine reset before the task
    /// executed a single instruction.
    ///
    /// Passing the authority as an argument cannot fail that way: there is no
    /// state to write, nothing to restore, and no property in between that can
    /// disagree with what was asked for.
    /// </summary>
    private uint TranslateSupervisor(uint linear, bool write)
    {
        if (!State.PagingEnabled) return linear;

        // Through the same PAE-or-legacy choice every other access makes:
        // with CR4.PAE set, CR3 names a page-directory-pointer table, and a
        // two-level walk of it would read a wrong frame and cache it.
        return TranslateWhenPaging(linear, write, asSupervisor: true);
    }

    /// <summary>
    /// Reads one byte the way the processor would, from a linear address --
    /// through the page tables when paging is on, and without raising a fault
    /// when the page is absent.
    ///
    /// This exists for debugging, and it earns its place: under Windows in
    /// enhanced mode a segment base plus an offset is a linear address, not a
    /// physical one, and reading it straight off the bus returns whatever
    /// happens to sit at that physical address -- usually zeroes. Every
    /// inspection of what the guest is executing needs this walk, and doing it
    /// by hand in each debugging session is how one ends up reading the wrong
    /// memory and drawing conclusions from it.
    /// </summary>
    /// <returns>The byte, or 0xFF when the page is not present.</returns>
    public byte PeekLinear(uint linear)
    {
        if (!State.PagingEnabled) return _memory.ReadByte(linear);

        if ((State.Cr4 & CpuState.Cr4Pae) != 0) return PeekLinearPae(linear);

        // The walk by hand, because TranslatePaged raises a page fault on a
        // missing page and an inspection must never disturb the machine.
        uint directory = _memory.ReadDword((State.Cr3 & 0xFFFFF000) + ((linear >> 22) << 2));
        if ((directory & 1) == 0) return 0xFF;

        // A 4 MB PDE (CR4.PSE on, PDE.PS on) is the whole translation: no
        // page table underneath it to read. Same frame arithmetic as
        // TranslateLargePage, but this inspection never raises the reserved-
        // bit fault a real access would -- it just has nothing meaningful to
        // show for a malformed entry either, so 0xFF covers that too.
        if ((State.Cr4 & CpuState.Cr4Pse) != 0 && (directory & (1u << 7)) != 0)
        {
            if ((directory & LargePageReservedMask) != 0) return 0xFF;
            uint largeFrame = (directory & 0xFFC00000) | (linear & 0x003FF000);
            return _memory.ReadByte(largeFrame | (linear & 0xFFF));
        }

        uint table = _memory.ReadDword((directory & 0xFFFFF000) + (((linear >> 12) & 0x3FF) << 2));
        if ((table & 1) == 0) return 0xFF;

        return _memory.ReadByte((table & 0xFFFFF000) | (linear & 0xFFF));
    }

    /// <summary>
    /// Where a far pointer that is not in any segment register points: a
    /// paragraph in real and virtual-8086 mode, a descriptor's base in
    /// protected mode. BIOS services that take their arguments on the stack
    /// -- the Plug and Play BIOS -- receive buffers this way, and are called
    /// from both modes with the same code.
    /// </summary>
    public uint FarPointerToLinear(ushort selector, uint offset)
    {
        if (!State.ProtectedMode || State.Virtual8086Mode) return ((uint)selector << 4) + offset;
        return DescriptorBase(ReadDescriptor(selector)) + offset;
    }

    /// <summary>
    /// Memory at a linear address, through the page tables when they are on.
    /// Unlike <see cref="PeekLinear"/> these fault on an absent page, which
    /// is right for a service writing into a buffer the caller handed it.
    /// </summary>
    public byte ReadLinearByte(uint linear) => _memory.ReadByte(TranslateLinear(linear, false));
    public void WriteLinearByte(uint linear, byte value) => _memory.WriteByte(TranslateLinear(linear, true), value);

    public ushort ReadLinearWord(uint linear) =>
        (ushort)(ReadLinearByte(linear) | (ReadLinearByte(linear + 1) << 8));

    public void WriteLinearWord(uint linear, ushort value)
    {
        WriteLinearByte(linear, (byte)value);
        WriteLinearByte(linear + 1, (byte)(value >> 8));
    }

    public void WriteLinearDword(uint linear, uint value)
    {
        WriteLinearWord(linear, (ushort)value);
        WriteLinearWord(linear + 2, (ushort)(value >> 16));
    }

    // --- memory accesses ------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadMem8(SegReg segment, uint offset)
    {
        // Even a single-byte access has to fall inside the segment limit: with
        // 32-bit addressing a "negative" offset becomes a huge unsigned value,
        // and that must raise #GP rather than quietly read some unrelated part
        // of memory.
        CheckLimit(segment, offset, 1);
        return _memory.ReadByte(TranslateLinear(LinearAddress(segment, offset), false));
    }

    /// <summary>
    /// A multi-byte access may not straddle the segment limit: a word at offset
    /// FFFF does not wrap around to 0000, it raises a protection fault instead.
    /// Confirmed against the SingleStepTests suite generated on a real 386:
    /// every access crossing the limit produces #GP, never a wrap.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CheckLimit(SegReg segment, uint offset, uint size)
    {
        ref SegmentCache seg = ref State.Segments[(int)segment];

        // An expand-down segment turns the test inside out. These describe a
        // stack, which grows towards lower addresses, and the limit marks the
        // bottom of the valid range rather than the top: what is addressable
        // runs from limit+1 up to the segment's maximum offset, and the
        // untouchable hole is *below* the limit. Growing such a stack means
        // lowering its limit, which is why the encoding exists at all.
        //
        // The maximum offset is 0xFFFF or 0xFFFFFFFF according to the
        // descriptor's B bit, and not the limit -- reading it from the limit
        // would collapse the whole range to nothing.
        if (seg.ExpandDown)
        {
            ulong last = (ulong)offset + size - 1;
            uint ceiling = seg.Big ? 0xFFFFFFFF : 0xFFFF;

            if (offset > seg.Limit && last <= ceiling) return;
        }
        // The sum is deliberately computed in 64 bits. Done in 32 bits it
        // would wrap: an offset near 0xFFFFFFFF plus a size of 4 comes back
        // around to a small number, which compares below any plausible limit,
        // so an access far past the end of the segment would sail through as
        // valid. Widening to ulong is the cheapest way to make the overflow
        // impossible rather than merely unlikely.
        else if ((ulong)offset + size - 1 <= seg.Limit) return;

        // Running off the end of the stack is its own condition: the processor
        // reports it with #SS rather than a general protection fault, so that
        // the handler can tell an exhausted stack apart from a stray pointer
        // and, if it wants to, grow the stack and retry.
        throw new CpuException(segment == SegReg.Ss ? (byte)12 : (byte)13, 0);
    }

    /// <summary>
    /// The 486's alignment check, exception 17.
    ///
    /// A word at an odd address, or a dword not on a multiple of four, costs
    /// the processor a second memory cycle. The check exists so that a
    /// program can be told where it is paying that price rather than merely
    /// running slowly, and it is deliberately hard to turn on by accident:
    /// the operating system must set CR0.AM, the program must set EFLAGS.AC,
    /// and only then does it apply -- and only at ring 3. The kernel and the
    /// drivers are exempt, because a great deal of perfectly good systems
    /// code does unaligned accesses on purpose.
    ///
    /// Virtual 8086 code counts as ring 3 here even though CPL is 0 while the
    /// processor is in that mode, since from the monitor's point of view it
    /// is application code.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CheckAlignment(uint offset, uint size)
    {
        if ((offset & (size - 1)) == 0) return;
        if ((State.Cr0 & CpuState.Cr0AlignmentMask) == 0) return;

        // Read straight out of the stored flags rather than through the lazy
        // accessor: AC is never one of the pending arithmetic flags, and
        // materialising those here would be work done for nothing.
        if ((Flags.Stored & Aurora386.Core.Cpu.Flags.AlignmentCheck) == 0) return;

        if (State.Cpl != 3 && !State.Virtual8086Mode) return;

        // The error code is architecturally zero: the faulting address is
        // already recoverable from the pushed CS:EIP, so there is nothing
        // useful left to say.
        throw new CpuException(17, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort ReadMem16(SegReg segment, uint offset)
    {
        CheckLimit(segment, offset, 2);
        if (State.AlignmentChecked) CheckAlignment(offset, 2);
        uint linear = LinearAddress(segment, offset);
        if (State.PagingEnabled && (linear & 0xFFF) > 0xFFE) return (ushort)ReadSplit(linear, 2);
        return _memory.ReadWord(TranslateLinear(linear, false));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadMem32(SegReg segment, uint offset)
    {
        // A 32-bit access that overruns the segment limit does not wrap
        // either: the 386 rejects it with #GP exactly as it does for words.
        // The hardware-generated tests for the 0x66-prefixed opcodes confirm
        // this.
        CheckLimit(segment, offset, 4);
        if (State.AlignmentChecked) CheckAlignment(offset, 4);
        uint linear = LinearAddress(segment, offset);
        if (State.PagingEnabled && (linear & 0xFFF) > 0xFFC) return ReadSplit(linear, 4);
        return _memory.ReadDword(TranslateLinear(linear, false));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteMem8(SegReg segment, uint offset, byte value)
    {
        CheckLimit(segment, offset, 1);
        _memory.WriteByte(TranslateLinear(LinearAddress(segment, offset), true), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteMem16(SegReg segment, uint offset, ushort value)
    {
        CheckLimit(segment, offset, 2);
        if (State.AlignmentChecked) CheckAlignment(offset, 2);
        uint linear = LinearAddress(segment, offset);
        if (State.PagingEnabled && (linear & 0xFFF) > 0xFFE) { WriteSplit(linear, 2, value); return; }
        _memory.WriteWord(TranslateLinear(linear, true), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteMem32(SegReg segment, uint offset, uint value)
    {
        CheckLimit(segment, offset, 4);
        if (State.AlignmentChecked) CheckAlignment(offset, 4);
        uint linear = LinearAddress(segment, offset);
        if (State.PagingEnabled && (linear & 0xFFF) > 0xFFC) { WriteSplit(linear, 4, value); return; }
        _memory.WriteDword(TranslateLinear(linear, true), value);
    }

    /// <summary>
    /// A word or dword that straddles a page boundary lives in two pages that
    /// need not be adjacent in physical memory, and either may be absent. So
    /// each byte is translated on its own; for a write every page is
    /// translated -- and its dirty bit set -- before the first byte lands, so
    /// that a fault on the second page leaves memory untouched. The first
    /// symptom of getting this wrong was KRNL386 walking a fixup chain in
    /// GDI.EXE: a selector written at linear 40FFF lost its high byte.
    /// </summary>
    private uint ReadSplit(uint linear, int size)
    {
        uint value = 0;
        for (int i = size - 1; i >= 0; i--)
            value = (value << 8) | _memory.ReadByte(TranslateWhenPaging(linear + (uint)i, false));
        return value;
    }

    private void WriteSplit(uint linear, int size, uint value)
    {
        Span<uint> physical = stackalloc uint[4];
        for (int i = 0; i < size; i++) physical[i] = TranslateWhenPaging(linear + (uint)i, true);
        for (int i = 0; i < size; i++) _memory.WriteByte(physical[i], (byte)(value >> (8 * i)));
    }

    // --- instruction stream fetch ---------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte FetchByte()
    {
        uint eip = State.Eip;
        CheckLimit(SegReg.Cs, eip, 1);
        uint linear = State.Segments[(int)SegReg.Cs].Base + eip;
        State.Eip = eip + 1;

        if (!State.PagingEnabled) return _memory.ReadByte(linear);

        // Instruction bytes come from the same page almost every time, so
        // the page's translation is kept here and reused until the fetch
        // leaves the page, the tables are flushed, or the privilege level
        // changes -- which matters because the page walk also checks that
        // ring 3 is allowed to read the page. Without this every single
        // opcode byte in enhanced-mode Windows paid for a TLB lookup, and
        // fetching alone was a fifth of the emulator's time.
        uint page = linear & 0xFFFFF000;
        if (page != _fetchPage || State.Cpl != _fetchCpl)
        {
            _fetchPhysical = TranslateWhenPaging(linear, false) & 0xFFFFF000;
            _fetchPage = page;
            _fetchCpl = State.Cpl;
        }
        return _memory.ReadByte(_fetchPhysical | (linear & 0xFFF));
    }

    // The instruction fetch page cache; see FetchByte.
    private uint _fetchPage = 0xFFFFFFFF, _fetchPhysical;
    private byte _fetchCpl = 0xFF;

    /// <summary>Forgets the fetch page: called whenever a translation may have changed.</summary>
    private void InvalidateFetchPage() => _fetchPage = 0xFFFFFFFF;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort FetchWord()
    {
        ushort value = ReadMem16(SegReg.Cs, State.Eip);
        State.Eip += 2;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint FetchDword()
    {
        uint value = ReadMem32(SegReg.Cs, State.Eip);
        State.Eip += 4;
        return value;
    }

    /// <summary>Immediate at the current operand size.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint FetchImmediate() => OperandSize32 ? FetchDword() : FetchWord();

    // --- stack -----------------------------------------------------------------
    // The width of the stack follows the B bit of SS, not the operand size: a
    // 16-bit stack segment keeps using SP even while 32-bit instructions are
    // executing. Mixing the two up is a classic source of "the return address
    // landed two bytes off" bugs.

    private void Push16(ushort value)
    {
        // A page fault restarts PUSH. Commit SP only after the write succeeds,
        // otherwise Windows grows the stack and retries with SP already lower.
        if (State.Ss.Big)
        {
            uint newStackPointer = State.Esp - 2;
            WriteMem16(SegReg.Ss, newStackPointer, value);
            State.Esp = newStackPointer;
        }
        else
        {
            ushort newStackPointer = (ushort)(State.Sp - 2);
            WriteMem16(SegReg.Ss, newStackPointer, value);
            State.Sp = newStackPointer;
        }
    }

    private void Push32(uint value)
    {
        if (State.Ss.Big)
        {
            uint newStackPointer = State.Esp - 4;
            WriteMem32(SegReg.Ss, newStackPointer, value);
            State.Esp = newStackPointer;
        }
        else
        {
            // As with the pop, on a 16-bit stack the write splits into two
            // word accesses, each of which wraps within the segment rather
            // than running past its end.
            ushort newStackPointer = (ushort)(State.Sp - 4);
            WriteMem16(SegReg.Ss, newStackPointer, (ushort)value);
            WriteMem16(SegReg.Ss, (uint)(ushort)(newStackPointer + 2), (ushort)(value >> 16));
            State.Sp = newStackPointer;
        }
    }

    private ushort Pop16()
    {
        if (State.Ss.Big)
        {
            ushort value = ReadMem16(SegReg.Ss, State.Esp);
            State.Esp += 2;
            return value;
        }
        else
        {
            ushort value = ReadMem16(SegReg.Ss, State.Sp);
            State.Sp += 2;
            return value;
        }
    }

    private uint Pop32()
    {
        if (State.Ss.Big)
        {
            uint value = ReadMem32(SegReg.Ss, State.Esp);
            State.Esp += 4;
            return value;
        }
        else
        {
            uint value = ReadMem32(SegReg.Ss, State.Sp);
            State.Sp += 4;
            return value;
        }
    }

    /// <summary>
    /// A 32-bit pop that wraps instead of overrunning. Only the segment
    /// register POPs use it: of those four bytes the processor keeps just two,
    /// and the two word accesses each wrap inside the segment rather than
    /// faulting the instruction. Ordinary pops, and the instructions that read
    /// a whole block at once, reject the access like any other operand.
    /// </summary>
    private uint PopSegmentOperand()
    {
        if (!OperandSize32) return Pop16();
        if (State.Ss.Big) return Pop32();

        ushort sp = State.Sp;
        uint low = ReadMem16(SegReg.Ss, sp);
        uint high = ReadMem16(SegReg.Ss, (uint)(ushort)(sp + 2));
        State.Sp = (ushort)(sp + 4);
        return low | (high << 16);
    }

    // --- public access for the native handlers --------------------------------
    // The BIOS functions written in C# need to read and write memory the way
    // the calling program sees it, that is through the segments and the same
    // address translation.

    public byte ReadMemoryByte(SegReg segment, uint offset) => ReadMem8(segment, offset);
    public ushort ReadMemoryWord(SegReg segment, uint offset) => ReadMem16(segment, offset);
    public void WriteMemoryByte(SegReg segment, uint offset, byte value) => WriteMem8(segment, offset, value);
    public void WriteMemoryWord(SegReg segment, uint offset, ushort value) => WriteMem16(segment, offset, value);
    public uint ReadMemoryDword(SegReg segment, uint offset) => ReadMem32(segment, offset);
    public void WriteMemoryDword(SegReg segment, uint offset, uint value) => WriteMem32(segment, offset, value);

    /// <summary>Physical memory, for accesses that bypass the segments.</summary>
    public PhysicalMemoryBus Memory => _memory;

    /// <summary>The I/O space, for the devices.</summary>
    public IoBus Io => _io;

    /// <summary>
    /// Loads a segment register the way emulated code would. In real mode that
    /// means base = selector * 16.
    /// </summary>
    public void SetSegment(SegReg segment, ushort selector) => LoadSegment(segment, selector);

    /// <summary>
    /// Delivers an interrupt as if the hardware had raised it. Exposed so that
    /// tests can exercise the descriptor and gate machinery directly, without
    /// having to assemble a program that faults in exactly the right way.
    /// </summary>
    public void RaiseInterruptForTest(byte vector, uint errorCode = 0)
        => EnterInterrupt(vector, isSoftware: false, errorCode, isException: true);

    /// <summary>
    /// Returns from an interrupt, as IRET would. The native handlers call it to
    /// finish off a BIOS function.
    /// </summary>
    public void ReturnFromInterrupt() => Iret();

    /// <summary>
    /// Sets the carry flag in the FLAGS image that an INT pushed on the stack.
    /// BIOS functions report failure through CF, but writing the live flag
    /// would achieve nothing: the IRET is about to overwrite EFLAGS from the
    /// stack, so the copy on the stack is what has to be patched.
    /// </summary>
    public void SetCarryInStackedFlags(bool carry)
    {
        // After a real-mode INT the stack holds IP, CS, FLAGS in that order,
        // so the flags sit four bytes above the top of the stack.
        uint stackPointer = State.Ss.Big ? State.Esp : State.Sp;
        ushort flags = ReadMem16(SegReg.Ss, stackPointer + 4);
        flags = carry ? (ushort)(flags | 1) : (ushort)(flags & ~1);
        WriteMem16(SegReg.Ss, stackPointer + 4, flags);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PushOperand(uint value)
    {
        if (OperandSize32) Push32(value); else Push16((ushort)value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint PopOperand() => OperandSize32 ? Pop32() : Pop16();
}
