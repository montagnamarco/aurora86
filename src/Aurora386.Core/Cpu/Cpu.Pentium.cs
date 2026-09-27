// The Pentium's (P5) own instructions: RDTSC, RDMSR/WRMSR and CMPXCHG8B.
// Dispatched from the 0F opcode switch in Cpu.TwoByte.cs; the bodies live
// here because none of the three exists on any earlier model this emulator
// pretends to be. Sources: plans/specs/cpu-models.md §2 (Intel SDM Vol.2A/3A,
// AP-485, rcollins.org "p5msr"/"cx8", Geoff Chappell's F00F write-up).

namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    // --- RDTSC -----------------------------------------------------------------

    /// <summary>
    /// RDTSC (0F 31): EDX:EAX &lt;- the 64-bit time-stamp counter.
    ///
    /// Gated purely on CR4.TSD and the current privilege level, independent
    /// of paging mode (cpu-models.md §2): with TSD clear it runs anywhere,
    /// V86 included; with TSD set only CPL0 may read it -- and that already
    /// covers real mode, since <see cref="CpuState.Cpl"/> reads as 0 there.
    /// </summary>
    private void ExecuteRdtsc()
    {
        RequireFeature(CpuFeature.Tsc);
        if ((State.Cr4 & CpuState.Cr4Tsd) != 0 && State.Cpl != 0)
            throw new CpuException(13, 0);

        ulong tsc = Tsc;
        State.Eax = (uint)tsc;
        State.Edx = (uint)(tsc >> 32);
    }

    // --- RDMSR / WRMSR -----------------------------------------------------------

    /// <summary>
    /// RDMSR (0F 32): ECX names the MSR, EDX:EAX receives its 64-bit value.
    /// Privileged unconditionally -- CPL0 or real mode, never V86 -- and an
    /// index <see cref="Msrs"/> does not recognise is <c>#GP(0)</c> exactly
    /// as an unrecognised privilege level is, which is how a guest discovers
    /// an MSR does not exist (cpu-models.md §2).
    /// </summary>
    private void ExecuteRdmsr()
    {
        RequireFeature(CpuFeature.Msr);
        if (State.Cpl != 0) throw new CpuException(13, 0);
        if (!Msrs.TryRead(State.Ecx, out ulong value)) throw new CpuException(13, 0);
        State.Eax = (uint)value;
        State.Edx = (uint)(value >> 32);
    }

    /// <summary>WRMSR (0F 30): the write side of <see cref="ExecuteRdmsr"/>.</summary>
    private void ExecuteWrmsr()
    {
        RequireFeature(CpuFeature.Msr);
        if (State.Cpl != 0) throw new CpuException(13, 0);
        ulong value = State.Eax | ((ulong)State.Edx << 32);
        if (!Msrs.TryWrite(State.Ecx, value)) throw new CpuException(13, 0);
    }

    /// <summary>
    /// WRMSR to the time-stamp-counter MSR on a family-six-or-later
    /// processor: <see cref="ModelSpecificRegisters"/>'s own family-six
    /// write path calls this instead of <see cref="WriteTsc"/>, because
    /// there the P6 accepts the value's full 64 bits rather than clearing
    /// the high half the way a P5 does (SDM Vol.3 §17's TSC-write footnote;
    /// plans/specs/p6.md item 10). Internal rather than public: it exists
    /// only for that one caller, which lives in the same assembly.
    /// </summary>
    internal void WriteTscFull(ulong value) => _tscOffset = unchecked((long)value - (Cycles + 1));

    // --- CMPXCHG8B ---------------------------------------------------------------

    /// <summary>
    /// CMPXCHG8B (0F C7 /1), memory operand only: compares EDX:EAX with the
    /// 64-bit destination. Equal -&gt; ZF=1 and ECX:EBX is stored; not equal
    /// -&gt; ZF=0 and EDX:EAX is loaded with the destination's actual value.
    ///
    /// Unlike ordinary CMPXCHG, the destination is written in <i>both</i>
    /// branches -- the SDM's own operation listing for CMPXCHG8B assigns
    /// <c>DEST</c> in the not-equal branch too (with the value it just read,
    /// so nothing about it changes), because the real chip performs this as
    /// a single locked read-modify-write cycle regardless of outcome. That is
    /// exactly why LOCK is legal, if redundant, on the memory form and why a
    /// read-only page or non-writable segment faults even when the
    /// comparison fails (cpu-models.md §2). So every one of the eight bytes
    /// is validated -- segment limit, then translated for a write, which
    /// also covers the page's present/user checks -- before anything in this
    /// method changes: a fault here must find memory and every register
    /// exactly as it was.
    ///
    /// Only <c>/1</c> ever reaches here (see the dispatch in
    /// Cpu.TwoByte.cs); a register operand, LOCK-prefixed or not, is
    /// <c>#UD</c> on every fixed P5 stepping and every modern host alike --
    /// the register form is the exact trigger byte of the F00F erratum on
    /// unpatched early P5 silicon, which Aurora deliberately does not
    /// reproduce (cpu-models.md §2, citing Geoff Chappell's F00F write-up).
    /// </summary>
    private void ExecuteCmpXchg8B(in ModRm m)
    {
        if (m.IsRegister)
        {
            throw new CpuException(6);
        }

        // One eight-byte operand, checked as one: a quadword that runs past
        // the segment limit faults as a whole, the rule every other access
        // here follows and the 386 vectors confirm for words and dwords. It
        // never wraps inside a 16-bit segment. Then the 486 alignment check,
        // which for a quadword wants eight-byte alignment (SDM vol. 3,
        // table 6-7).
        const uint operandSize = 8;
        uint offset = m.Offset;

        CheckLimit(m.Segment, offset, operandSize);

        if (State.AlignmentChecked)
        {
            CheckAlignment(offset, operandSize);
        }

        // Both pages the quadword may touch are translated for a write
        // before anything is read, so that a fault on either leaves memory
        // and registers as they were.
        uint linear = LinearAddress(m.Segment, offset);
        uint lastLinear = linear + operandSize - 1;
        uint firstPhysical = TranslateLinear(linear, true);
        uint lastPhysical = TranslateLinear(lastLinear, true);
        bool onePage = (linear & QuadwordPageMask) == (lastLinear & QuadwordPageMask);

        ulong destination = onePage
            ? ReadPhysicalQword(firstPhysical)
            : ReadSplitQword(linear, firstPhysical, lastPhysical);

        ulong compare = State.Eax | ((ulong)State.Edx << 32);
        bool equal = destination == compare;
        ulong stored = equal ? (State.Ebx | ((ulong)State.Ecx << 32)) : destination;

        if (onePage)
        {
            WritePhysicalQword(firstPhysical, stored);
        }
        else
        {
            WriteSplitQword(linear, firstPhysical, lastPhysical, stored);
        }

        if (!equal)
        {
            State.Eax = (uint)destination;
            State.Edx = (uint)(destination >> 32);
        }

        // Only ZF is written. The SDM lists CF, PF, AF, SF and OF as
        // unaffected, and they are left exactly as they were.
        Flags.Zero = equal;
    }

    /// <summary>The page-offset bits of a linear address, cleared.</summary>
    private const uint QuadwordPageMask = 0xFFFFF000;

    /// <summary>
    /// A quadword inside one page, read as two dword bus accesses. Keeping
    /// them whole matters for memory-mapped registers, which the bus contract
    /// requires to see one call per access: byte by byte, a register with a
    /// read side effect would be read several times.
    /// </summary>
    private ulong ReadPhysicalQword(uint physical)
    {
        uint low = _memory.ReadDword(physical);
        uint high = _memory.ReadDword(physical + 4);

        return low | ((ulong)high << 32);
    }

    private void WritePhysicalQword(uint physical, ulong value)
    {
        _memory.WriteDword(physical, (uint)value);
        _memory.WriteDword(physical + 4, (uint)(value >> 32));
    }

    /// <summary>
    /// A quadword that crosses into the next page, whose frame need not
    /// follow the first: the bytes before the boundary come from the first
    /// translation and the rest from the second.
    /// </summary>
    private ulong ReadSplitQword(uint linear, uint firstPhysical, uint lastPhysical)
    {
        ulong value = 0;

        for (int i = 7; i >= 0; i--)
        {
            uint physical = SplitQwordByte(linear, (uint)i, firstPhysical, lastPhysical);
            value = (value << 8) | _memory.ReadByte(physical);
        }

        return value;
    }

    private void WriteSplitQword(uint linear, uint firstPhysical, uint lastPhysical, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            uint physical = SplitQwordByte(linear, (uint)i, firstPhysical, lastPhysical);
            _memory.WriteByte(physical, (byte)(value >> (8 * i)));
        }
    }

    private static uint SplitQwordByte(uint linear, uint index, uint firstPhysical, uint lastPhysical)
    {
        uint byteLinear = linear + index;
        bool inFirstPage = (byteLinear & QuadwordPageMask) == (linear & QuadwordPageMask);

        if (inFirstPage)
        {
            return firstPhysical + index;
        }

        uint lastLinear = linear + 7;
        return lastPhysical - (lastLinear - byteLinear);
    }
}
