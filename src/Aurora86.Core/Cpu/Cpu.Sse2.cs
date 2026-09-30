// SSE2 (Pentium 4 Northwood, work package P4.2): the packed/scalar
// double-precision data-movement instructions (MOVUPD/MOVSD/MOVLPD/MOVHPD/
// MOVAPD/MOVNTPD/MOVMSKPD/UNPCKLPD/UNPCKHPD/SHUFPD), MOVNTI, and the four
// instructions gated on their own feature bits rather than on SSE2's XMM
// state at all (LFENCE/MFENCE/CLFLUSH/PAUSE -- PAUSE already runs as a plain
// NOP, Cpu.Execute.cs's opcode 0x90, needing no change here).
//
// The IEEE double arithmetic, comparisons and every CVT* conversion live in
// Cpu.Sse2.Arithmetic.cs; the 128-bit widened integer set (every MMX
// instruction widened to XMM, plus the handful SSE2 itself added) lives in
// Cpu.Sse2.Integer.cs. Dispatched from Cpu.Sse.cs's ExecuteSse (the 66/F2
// branch, for every opcode PS/SS and PD/SD share), from Cpu.Mmx.cs's
// ExecuteMmx (the 66/F2/F3 branch, for the widened integer opcodes) and
// directly from Cpu.TwoByte.cs (0F C3, 0F 5A/5B/E6, and group 15's /5, /6
// and /7-memory). Sources: plans/specs/sse2.md (the hardware-researcher's
// report and the coordinator's decisions on it), Intel SDM Vol.2A/2B and
// Vol.1 ch.11.

using Aurora86.Core.Cpu.Sse;

namespace Aurora86.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>The r/m operand of a scalar double (SD) instruction: only the low 64 bits, and a memory operand need not be aligned (plans/specs/sse2.md §1 lists every SD form as "m64", never "A").</summary>
    private ulong ReadXmmSourceScalarQword(in ModRm m) => m.IsRegister ? Sse.Xmm(m.Register).Low : ReadMemQword(m.Segment, m.Offset);

    /// <summary>
    /// The packed/scalar-double dispatch, reached from Cpu.Sse.cs's
    /// ExecuteSse once RequireSseInstruction and CPUID.SSE2 have both
    /// already been confirmed there. <paramref name="opcode"/> is the same
    /// second opcode byte ExecuteSse itself switched on; <c>_prefixes.RepNe</c>
    /// (F2) says scalar, exactly like ExecuteSse's own <c>_prefixes.Rep</c>
    /// (F3) does for PS/SS.
    /// </summary>
    private void ExecuteSse2Float(byte opcode)
    {
        bool scalar = _prefixes.RepNe;

        switch (opcode)
        {
            case 0x10: ExecuteMovupdOrMovsd(store: false, scalar); return;
            case 0x11: ExecuteMovupdOrMovsd(store: true, scalar); return;
            case 0x12: ExecuteMovlpdLoad(scalar); return;
            case 0x13: ExecuteMovlpdStore(scalar); return;
            case 0x14: ExecuteUnpckpd(low: true, scalar); return;
            case 0x15: ExecuteUnpckpd(low: false, scalar); return;
            case 0x16: ExecuteMovhpdLoad(scalar); return;
            case 0x17: ExecuteMovhpdStore(scalar); return;
            case 0x28: ExecuteMovapd(store: false, scalar); return;
            case 0x29: ExecuteMovapd(store: true, scalar); return;
            case 0x2A: ExecuteCvtToPd(scalar); return;
            case 0x2B: ExecuteMovntpd(scalar); return;
            case 0x2C: ExecuteCvtFromPd(scalar, truncate: true); return;
            case 0x2D: ExecuteCvtFromPd(scalar, truncate: false); return;
            case 0x2E: ExecuteComisd(scalar, signalOnQuietNaN: false); return;   // UCOMISD
            case 0x2F: ExecuteComisd(scalar, signalOnQuietNaN: true); return;   // COMISD
            case 0x50: ExecuteMovmskpd(scalar); return;
            case 0x51: ExecuteUnaryArithPd(scalar); return;
            case 0x54: ExecuteLogicalPd(scalar, SseLogicalOp.And); return;
            case 0x55: ExecuteLogicalPd(scalar, SseLogicalOp.AndNot); return;
            case 0x56: ExecuteLogicalPd(scalar, SseLogicalOp.Or); return;
            case 0x57: ExecuteLogicalPd(scalar, SseLogicalOp.Xor); return;
            case 0x58: ExecuteBinaryArithPd(scalar, SseBinaryOp.Add); return;
            case 0x59: ExecuteBinaryArithPd(scalar, SseBinaryOp.Mul); return;
            case 0x5C: ExecuteBinaryArithPd(scalar, SseBinaryOp.Sub); return;
            case 0x5D: ExecuteBinaryArithPd(scalar, SseBinaryOp.Min); return;
            case 0x5E: ExecuteBinaryArithPd(scalar, SseBinaryOp.Div); return;
            case 0x5F: ExecuteBinaryArithPd(scalar, SseBinaryOp.Max); return;
            case 0xC2: ExecuteCmpPd(scalar); return;
            case 0xC6: ExecuteShufpd(scalar); return;
            default: throw new CpuException(6);
        }
    }

    // --- MOVUPD / MOVSD (0F 10/11) ------------------------------------------

    /// <summary>MOVUPD: whole 128 bits, unaligned. MOVSD (F2): only the low 64 bits move in a register-to-register move; loading from memory instead zeroes bits 127:64 of the destination -- the same shape MOVSS's own remark documents, one lane wider.</summary>
    private void ExecuteMovupdOrMovsd(bool store, bool scalar)
    {
        var m = DecodeModRm();
        if (SseTaskSwitched()) return;

        if (scalar)
        {
            if (store)
            {
                ulong value = Sse.Xmm(m.Reg).Low;
                if (m.IsRegister)
                {
                    ref XmmValue destination = ref Sse.Xmm(m.Register);
                    destination.D0 = (uint)value;
                    destination.D1 = (uint)(value >> 32);
                }
                else
                {
                    WriteMemQword(m.Segment, m.Offset, value);
                }
            }
            else if (m.IsRegister)
            {
                Sse.Xmm(m.Reg).D0 = Sse.Xmm(m.Register).D0;
                Sse.Xmm(m.Reg).D1 = Sse.Xmm(m.Register).D1;
            }
            else
            {
                ulong value = ReadMemQword(m.Segment, m.Offset);
                Sse.Xmm(m.Reg) = new XmmValue { D0 = (uint)value, D1 = (uint)(value >> 32) };
            }
            return;
        }

        if (store)
        {
            XmmValue value = Sse.Xmm(m.Reg);
            if (m.IsRegister) { Sse.Xmm(m.Register) = value; return; }
            Validate128BitStore(m.Segment, m.Offset);
            WriteMemQword(m.Segment, m.Offset, value.Low);
            WriteMemQword(m.Segment, m.Offset + 8, value.High);
        }
        else
        {
            Sse.Xmm(m.Reg) = m.IsRegister
                ? Sse.Xmm(m.Register)
                : XmmValue.FromHalves(ReadMemQword(m.Segment, m.Offset), ReadMemQword(m.Segment, m.Offset + 8));
        }
    }

    // --- MOVLPD (0F 12/13) -----------------------------------------------------

    /// <summary>MOVLPD: memory only, both directions -- unlike MOVLPS/MOVHPS, SSE2 defines no register-register (MOVHLPS/MOVLHPS-style) form at all for either half-move (plans/specs/sse2.md §1: "m64 only").</summary>
    private void ExecuteMovlpdLoad(bool scalar)
    {
        var m = DecodeModRm();
        if (scalar || m.IsRegister) throw new CpuException(6);
        if (SseTaskSwitched()) return;

        ulong low = ReadMemQword(m.Segment, m.Offset);
        Sse.Xmm(m.Reg).D0 = (uint)low;
        Sse.Xmm(m.Reg).D1 = (uint)(low >> 32);
    }

    private void ExecuteMovlpdStore(bool scalar)
    {
        var m = DecodeModRm();
        if (scalar || m.IsRegister) throw new CpuException(6);
        if (SseTaskSwitched()) return;

        WriteMemQword(m.Segment, m.Offset, Sse.Xmm(m.Reg).Low);
    }

    // --- MOVHPD (0F 16/17) -----------------------------------------------------

    private void ExecuteMovhpdLoad(bool scalar)
    {
        var m = DecodeModRm();
        if (scalar || m.IsRegister) throw new CpuException(6);
        if (SseTaskSwitched()) return;

        ulong high = ReadMemQword(m.Segment, m.Offset);
        Sse.Xmm(m.Reg).D2 = (uint)high;
        Sse.Xmm(m.Reg).D3 = (uint)(high >> 32);
    }

    private void ExecuteMovhpdStore(bool scalar)
    {
        var m = DecodeModRm();
        if (scalar || m.IsRegister) throw new CpuException(6);
        if (SseTaskSwitched()) return;

        WriteMemQword(m.Segment, m.Offset, Sse.Xmm(m.Reg).High);
    }

    // --- UNPCKLPD / UNPCKHPD (0F 14/15) ----------------------------------------

    /// <summary>Interleaves the destination's and the source's two 64-bit (double) lanes; a memory source needs 16-byte alignment, like every packed m128 SSE2 form.</summary>
    private void ExecuteUnpckpd(bool low, bool scalar)
    {
        var m = DecodeModRm();
        if (scalar) throw new CpuException(6);
        if (SseTaskSwitched()) return;

        XmmValue destination = Sse.Xmm(m.Reg);
        XmmValue source = ReadXmmSourceAligned(m);

        Sse.Xmm(m.Reg) = low
            ? XmmValue.FromHalves(destination.Low, source.Low)
            : XmmValue.FromHalves(destination.High, source.High);
    }

    // --- MOVAPD (0F 28/29) ------------------------------------------------------

    private void ExecuteMovapd(bool store, bool scalar)
    {
        var m = DecodeModRm();
        if (scalar) throw new CpuException(6);
        if (SseTaskSwitched()) return;

        if (store)
        {
            XmmValue value = Sse.Xmm(m.Reg);
            if (m.IsRegister) { Sse.Xmm(m.Register) = value; return; }
            RequireAligned16(m.Segment, m.Offset);
            Validate128BitStore(m.Segment, m.Offset);
            WriteMemQword(m.Segment, m.Offset, value.Low);
            WriteMemQword(m.Segment, m.Offset + 8, value.High);
        }
        else
        {
            Sse.Xmm(m.Reg) = ReadXmmSourceAligned(m);
        }
    }

    // --- MOVNTPD (0F 2B) -----------------------------------------------------------

    private void ExecuteMovntpd(bool scalar)
    {
        var m = DecodeModRm();
        if (scalar || m.IsRegister) throw new CpuException(6);
        if (SseTaskSwitched()) return;
        RequireAligned16(m.Segment, m.Offset);
        Validate128BitStore(m.Segment, m.Offset);

        XmmValue value = Sse.Xmm(m.Reg);
        WriteMemQword(m.Segment, m.Offset, value.Low);
        WriteMemQword(m.Segment, m.Offset + 8, value.High);
    }

    // --- MOVMSKPD (0F 50) ------------------------------------------------------------
    //
    // Not itemized in plans/specs/sse2.md's own decode table, but entailed
    // by the same general rule as MOVSD (the note's §1 opening line, "66 =
    // packed double... F2 = scalar double") applied to MOVMSKPS's own 66
    // sibling: a well-known, unambiguous SSE2 instruction (SDM Vol.2B),
    // register only, two bits (one per double lane) rather than MOVMSKPS's
    // four.

    private void ExecuteMovmskpd(bool scalar)
    {
        var m = DecodeModRm();
        if (scalar || !m.IsRegister) throw new CpuException(6);
        if (SseTaskSwitched()) return;

        XmmValue value = Sse.Xmm(m.Register);
        uint mask = 0;
        if (DoubleFloat.SignBit(value.Low)) mask |= 1;
        if (DoubleFloat.SignBit(value.High)) mask |= 2;
        State.SetReg32(m.Reg, mask);
    }

    // --- SHUFPD (0F C6) --------------------------------------------------------------

    /// <summary>SHUFPD imm8: result lane 0 is the destination's lane (imm8 bit 0), lane 1 is the (16-byte aligned, when memory) source's lane (imm8 bit 1).</summary>
    private void ExecuteShufpd(bool scalar)
    {
        var m = DecodeModRm();
        if (scalar) throw new CpuException(6);
        if (SseTaskSwitched()) return;

        XmmValue destination = Sse.Xmm(m.Reg);
        XmmValue source = ReadXmmSourceAligned(m);
        byte imm8 = FetchByte();

        ulong low = (imm8 & 1) != 0 ? destination.High : destination.Low;
        ulong high = (imm8 & 2) != 0 ? source.High : source.Low;
        Sse.Xmm(m.Reg) = XmmValue.FromHalves(low, high);
    }

    // --- MOVNTI (0F C3) ----------------------------------------------------------------

    /// <summary>
    /// MOVNTI m32, r32: a non-temporal store hint with nothing behind it to
    /// cache, so an ordinary dword store -- gated on CPUID.SSE2 alone, with
    /// none of the CR0.EM/TS/OSFXSR checks every XMM-touching instruction
    /// carries (plans/specs/sse2.md §5: "LFENCE, MFENCE, CLFLUSH, PAUSE,
    /// MOVNTI: no EM/TS/OSFXSR check"), since it never touches the FPU/MMX/
    /// XMM state at all. Measured directly on the host (tools/NativeOracle
    /// --probe): 66, F2 and F3 0F C3 each raise a real #UD on real silicon
    /// (the harness process itself takes SIGILL/STATUS_ILLEGAL_INSTRUCTION
    /// running them, with no handler to catch it) -- confirming the SDM's
    /// silence on any prefixed form of this opcode means exactly that, not
    /// "prefix silently ignored".
    /// </summary>
    private void ExecuteMovnti(in ModRm m)
    {
        RequireFeature(CpuFeature.Sse2);
        if (_prefixes.OperandSize || _prefixes.Rep || _prefixes.RepNe) throw new CpuException(6);
        if (m.IsRegister) throw new CpuException(6);
        // The source register is the reg field (m.Reg): m.Register is only
        // ever populated when the r/m operand itself is a register (mod=3),
        // which MOVNTI's own memory-only r/m never is -- a review finding,
        // reading it here read whatever stale/zero value m.Register happened
        // to hold instead of the actual source register.
        WriteMem32(m.Segment, m.Offset, State.GetReg32(m.Reg));
    }

    // --- LFENCE / MFENCE / CLFLUSH (0F AE /5, /6, /7) ---------------------------------
    //
    // Reached directly from Cpu.TwoByte.cs's group-15 switch, like LDMXCSR/
    // STMXCSR/SFENCE already are. None of the three touches the FPU/MMX/XMM
    // state either, so -- like MOVNTI above -- none needs the EM/TS/OSFXSR
    // gating (plans/specs/sse2.md §5).

    /// <summary>0F AE /5: LFENCE. mod=11 required; any rm value is accepted there (the reg field alone selects the instruction, coordinator decision (d), plans/specs/sse2.md's own "uncertain" item -- rm's low bits are simply ignored, the same latitude SFENCE's own /7 already has). Aurora's memory model has no load reordering to fence, so this is a pure no-op once gated.</summary>
    private void ExecuteLfence(in ModRm m)
    {
        RequireFeature(CpuFeature.Sse2);
        if (!m.IsRegister) throw new CpuException(6);
    }

    /// <summary>0F AE /6: MFENCE, the same shape as LFENCE.</summary>
    private void ExecuteMfence(in ModRm m)
    {
        RequireFeature(CpuFeature.Sse2);
        if (!m.IsRegister) throw new CpuException(6);
    }

    /// <summary>
    /// 0F AE /7, memory operand: CLFLUSH m8, gated on CPUID.CLFSH (bit 19)
    /// rather than CPUID.SSE2 (plans/specs/sse2.md §1/§5) -- the mod=11
    /// encoding of reg field 7 is SFENCE (Cpu.Sse.cs's ExecuteSfence,
    /// gated on CPUID.SSE instead), dispatched separately by
    /// Cpu.TwoByte.cs's own group-15 switch on <c>m.IsRegister</c>. Unlike
    /// PREFETCHh (Cpu.TwoByte.cs's opcode 0x18, a true hint that never
    /// faults), CLFLUSH is an ordinary memory access for fault purposes: a
    /// segment-limit or page fault on its one byte is exactly as real as
    /// any other memory operand's.
    /// </summary>
    private void ExecuteClflush(in ModRm m)
    {
        RequireFeature(CpuFeature.Clfsh);
        if (m.IsRegister) throw new CpuException(6);

        CheckLimit(m.Segment, m.Offset, 1);
        TranslateLinear(LinearAddress(m.Segment, m.Offset), false);
    }
}
