using System.Runtime.CompilerServices;
using Aurora386.Core.IO;
using Aurora386.Core.Memory;

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
    /// The interrupt controller. Without one the CPU never sees a hardware
    /// interrupt, which is fine for unit tests but not for a whole machine.
    /// </summary>
    public Devices.InterruptController? Interrupts { get; set; }

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
        Reset();
    }

    public void Reset()
    {
        State.Reset();
        Flags = default;
        Flags.SetValue((uint)Aurora386.Core.Cpu.Flags.Reserved1);
        _prefixes.Clear();
        Cycles = 0;
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
    /// it walks the page directory. A TLB will arrive together with full
    /// protected mode support: as long as we run in real mode this is just the
    /// identity function.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint TranslateLinear(uint linear, bool write)
    {
        if (!State.PagingEnabled) return linear;
        return TranslatePaged(linear, write);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort ReadMem16(SegReg segment, uint offset)
    {
        CheckLimit(segment, offset, 2);
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
        uint linear = LinearAddress(segment, offset);
        if (State.PagingEnabled && (linear & 0xFFF) > 0xFFE) { WriteSplit(linear, 2, value); return; }
        _memory.WriteWord(TranslateLinear(linear, true), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteMem32(SegReg segment, uint offset, uint value)
    {
        CheckLimit(segment, offset, 4);
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
            value = (value << 8) | _memory.ReadByte(TranslatePaged(linear + (uint)i, false));
        return value;
    }

    private void WriteSplit(uint linear, int size, uint value)
    {
        Span<uint> physical = stackalloc uint[4];
        for (int i = 0; i < size; i++) physical[i] = TranslatePaged(linear + (uint)i, true);
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
            _fetchPhysical = TranslatePaged(linear, false) & 0xFFFFF000;
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
        if (State.Ss.Big)
        {
            State.Esp -= 2;
            WriteMem16(SegReg.Ss, State.Esp, value);
        }
        else
        {
            State.Sp -= 2;
            WriteMem16(SegReg.Ss, State.Sp, value);
        }
    }

    private void Push32(uint value)
    {
        if (State.Ss.Big)
        {
            State.Esp -= 4;
            WriteMem32(SegReg.Ss, State.Esp, value);
        }
        else
        {
            // As with the pop, on a 16-bit stack the write splits into two
            // word accesses, each of which wraps within the segment rather
            // than running past its end.
            State.Sp -= 4;
            WriteMem16(SegReg.Ss, State.Sp, (ushort)value);
            WriteMem16(SegReg.Ss, (uint)(ushort)(State.Sp + 2), (ushort)(value >> 16));
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
