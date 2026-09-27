// The Pentium III's SSE: the packed/scalar single-precision data-movement
// and control instructions (MOVUPS/MOVSS/MOVLPS/MOVHLPS/MOVHPS/MOVLHPS/
// MOVAPS/MOVNTPS/MOVMSKPS/UNPCKLPS/UNPCKHPS/SHUFPS, LDMXCSR/STMXCSR/SFENCE)
// and the gating and alignment rules every SSE opcode shares. The IEEE
// arithmetic, comparisons and CVT* conversions live in
// Cpu.Sse.Arithmetic.cs; the eight new MMX-register instructions SSE also
// introduced (PAVGB and friends) stay in Cpu.Mmx.cs, next to the MMX opcodes
// they are dispatched exactly like.
//
// Dispatched from Cpu.TwoByte.cs's 0F switch. Sources: plans/specs/sse.md
// (the hardware-researcher's report and the coordinator's decisions), Intel
// SDM Vol.2A/2B (each instruction's own reference page) and Vol.1 ch.10/§4.8
// (the architectural picture: MXCSR, the legacy-SSE 16-byte alignment rule,
// the exception-masking model).

using Aurora386.Core.Cpu.Sse;

namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>
    /// XMM0-7 and MXCSR: the architectural state SSE adds. See
    /// <see cref="Sse.SseUnit"/> for why this exists even on silicon without
    /// SSE (nothing ever reaches it there) and for the snapshot section the
    /// coordinator's machine-level container adds around it.
    /// </summary>
    public readonly SseUnit Sse = new();

    /// <summary>
    /// #UD ahead of anything else for every SSE opcode: no CPUID.SSE, CR0.EM
    /// set, or CR4.OSFXSR clear (plans/specs/sse.md §5 -- the same three
    /// conditions FXSAVE/FXRSTOR's own #UD/#NM split names, one bit
    /// (OSFXSR) further than Cpu.Mmx.cs's EM-only check, since MMX predates
    /// the FXSAVE-based context-switch convention SSE was designed
    /// alongside).
    /// </summary>
    /// <summary>
    /// <paramref name="feature"/> defaults to <see cref="CpuFeature.Sse"/>
    /// for every original SSE opcode; Cpu.Mmx.cs's own 66-prefixed dispatch
    /// (a P4.2 addition) calls this with <see cref="CpuFeature.Sse2"/>
    /// instead for the 128-bit widened integer forms, since those are
    /// XMM-touching too (plans/specs/sse2.md §5) but gated on the newer
    /// feature bit, not the older one every model with SSE2 also happens to
    /// carry.
    /// </summary>
    private void RequireSseInstruction(CpuFeature feature = CpuFeature.Sse)
    {
        if (!State.Features.Has(feature)) throw new CpuException(6);
        if ((State.Cr0 & CpuState.Cr0Emulation) != 0) throw new CpuException(6);
        if ((State.Cr4 & CpuState.Cr4OsFxsr) == 0) throw new CpuException(6);
    }

    /// <summary>
    /// True -- having already delivered #NM -- when CR0.TS blocks this SSE
    /// instruction, exactly like Cpu.Mmx.cs's and Cpu.P6.cs's own TS checks.
    /// The caller must return immediately when this is true.
    /// </summary>
    private bool SseTaskSwitched()
    {
        if ((State.Cr0 & CpuState.Cr0TaskSwitched) == 0) return false;
        FaultNow(7, 0);
        return true;
    }

    /// <summary>
    /// The mandatory 16-byte alignment every packed m128 SSE operand carries
    /// except MOVUPS and the scalar/m64 forms (plans/specs/sse.md §1, and
    /// the coordinator's decision extending it to UNPCKLPS/UNPCKHPS too):
    /// <c>#GP(0)</c>, checked after the #UD/#NM gating and before the access
    /// itself touches memory.
    /// </summary>
    private void RequireAligned16(SegReg segment, uint offset)
    {
        if ((LinearAddress(segment, offset) & 0xF) != 0) throw new CpuException(13, 0);
    }

    /// <summary>
    /// Validates a 128-bit store's whole 16 bytes -- the segment limit,
    /// and both pages it can touch for writing -- before either half is
    /// actually written (a P4.1 review finding): MOVUPS, MOVAPS and
    /// MOVNTPS all move a full XMM register to memory as two separate
    /// 8-byte <see cref="WriteMemQword"/> calls, and without this a page
    /// fault on the second half could leave the first half already written
    /// to memory the guest never asked to have touched. MOVHPS/MOVLPS
    /// store only 8 bytes each and so need no equivalent -- one
    /// <see cref="WriteMemQword"/> call already commits or faults as one
    /// unit, the same discipline <see cref="ValidateFxsaveArea"/>
    /// (Cpu.P6.cs) and <see cref="ValidateMaskmovqDestination"/>
    /// (Cpu.Mmx.cs) apply to their own wider memory operands.
    /// </summary>
    private void Validate128BitStore(SegReg segment, uint offset)
    {
        const uint operandSize = 16;
        CheckLimit(segment, offset, operandSize);

        uint linear = LinearAddress(segment, offset);
        uint lastLinear = linear + operandSize - 1;
        TranslateLinear(linear, true);
        TranslateLinear(lastLinear, true);
    }

    /// <summary>The r/m operand of a packed (PS) instruction: the whole 128 bits, 16-byte aligned when it is memory.</summary>
    private XmmValue ReadXmmSourceAligned(in ModRm m)
    {
        if (m.IsRegister) return Sse.Xmm(m.Register);
        RequireAligned16(m.Segment, m.Offset);
        return XmmValue.FromHalves(ReadMemQword(m.Segment, m.Offset), ReadMemQword(m.Segment, m.Offset + 8));
    }

    /// <summary>The r/m operand of a scalar (SS) instruction: only the low dword, and a memory operand need not be aligned.</summary>
    private uint ReadXmmSourceScalarDword(in ModRm m)
        => m.IsRegister ? Sse.Xmm(m.Register).D0 : ReadMem32(m.Segment, m.Offset);

    private RoundingMode CurrentRoundingMode() => (RoundingMode)((Sse.Mxcsr >> MxcsrBits.RoundingControlShift) & 3);
    private bool CurrentFlushToZero() => (Sse.Mxcsr & MxcsrBits.FlushToZero) != 0;

    /// <summary>
    /// MXCSR's own DAZ bit (work package P4.2, plans/specs/sse2.md's
    /// coordinator decision): only ever settable at all on a model whose
    /// <see cref="Sse.SseUnit.MxcsrMaskFor"/> has bit 6 (Pentium 4 and
    /// later) -- LDMXCSR/FXRSTOR's own #GP(0) already keeps that bit clear
    /// there for every guest-issued load. This reads the bit through that
    /// same per-model mask directly, rather than trusting the raw stored
    /// bit alone (a review finding): <c>Sse.Mxcsr</c> can carry a bit
    /// outside the current model's own mask through a path no guest
    /// instruction ever takes -- a snapshot restored across a model change,
    /// whose own <c>SseUnit.ReadSnapshot</c> has no model to mask against at
    /// all -- and without this, DAZ would then be live on a model whose
    /// silicon never had it, until the next explicit LDMXCSR/FXRSTOR
    /// happened to overwrite MXCSR. Threaded into every
    /// <c>SingleFloat</c>/<c>DoubleFloat</c> call this file and
    /// Cpu.Sse2.Arithmetic.cs make, single precision included: DAZ is a
    /// property of the whole floating-point unit, not of one instruction's
    /// operand width.
    /// </summary>
    private bool CurrentDaz() => (Sse.Mxcsr & MxcsrBits.Daz & SseUnit.MxcsrMaskFor(State.Model)) != 0;

    /// <summary>MXCSR's own UM bit: whether the Underflow exception is masked. SingleFloat needs to see this directly (a P4.1 review finding) since an unmasked UM changes both when Underflow is signalled and whether flush-to-zero even applies -- see <c>PackNormalized</c>'s and <c>RoundDoubleToSingle</c>'s own remarks in SingleFloat.cs.</summary>
    private bool CurrentUnderflowMasked() => (Sse.Mxcsr & MxcsrBits.UnderflowMask) != 0;

    /// <summary>
    /// IE, DE and ZE (SDM's own names for Invalid, Denormal and
    /// DivideByZero): the three exceptions the SDM's own operation listing
    /// evaluates over every lane <em>before</em> any actual arithmetic runs
    /// -- as opposed to OE, UE and PE, which only exist because some
    /// arithmetic was actually attempted. <see cref="RaiseIfUnmasked"/>
    /// uses this split to implement a P4.1 review finding: when one of
    /// these three is unmasked, the instruction never reaches its
    /// arithmetic at all, so only whichever of these three actually
    /// occurred is recorded -- never the OE/UE/PE a lane's would-be result
    /// might also have carried alongside it.
    /// </summary>
    private const uint PrecomputationFlags = MxcsrBits.InvalidFlag | MxcsrBits.DenormalFlag | MxcsrBits.DivideByZeroFlag;

    /// <summary>
    /// Sets the sticky MXCSR flag bits <paramref name="flags"/> one SSE
    /// floating-point operation raised -- always, masked or not (SDM Vol.1
    /// §11.6.6) -- and, if any of them is unmasked, raises the SIMD
    /// floating-point exception instead of letting the caller write its
    /// result: #XM (19) when CR4.OSXMMEXCPT is set, #UD otherwise
    /// (plans/specs/sse.md §5). Returns true exactly when the caller must
    /// return at once, its destination left exactly as it was.
    ///
    /// A P4.1 review finding, fixed here: an unmasked pre-computation
    /// exception (IE/DE/ZE) must record only itself, never the OE/UE/PE
    /// bit another, otherwise unrelated, lane's arithmetic would also have
    /// set -- SseReviewFindingsTests.cs's own ADDPS/ADDSS vectors (a
    /// signaling-NaN lane alongside an ordinary, merely-inexact one, IM
    /// unmasked; a denormal operand alongside an ordinary inexact one, DM
    /// unmasked) state the exact MXCSR the review expects. This one is not
    /// itself host-confirmable: an unmasked exception traps the host
    /// thread the moment it runs, with no handler wrapped around
    /// tools/NativeOracle's own raw machine code (HostRunner's own type
    /// summary), so it is implemented from the review's own architectural
    /// reasoning and checked at the emulator level instead. Aurora's
    /// per-lane <c>SingleFloat</c> calls already compute every lane's
    /// result and flags before this method ever runs (there is no cheaper
    /// place to stop early), so the fix is here: strip OE/UE/PE from
    /// <paramref name="flags"/> before it is recorded at all, whenever an
    /// unmasked <see cref="PrecomputationFlags"/> bit is present in it.
    /// </summary>
    private bool RaiseIfUnmasked(FpFlags flags)
    {
        uint maskedBits = (Sse.Mxcsr & MxcsrBits.AllMasks) >> 7;
        uint unmaskedPrecomputation = (uint)flags & PrecomputationFlags & ~maskedBits;
        if (unmaskedPrecomputation != 0)
        {
            flags &= (FpFlags)PrecomputationFlags;
        }

        Sse.Mxcsr |= (uint)flags & MxcsrBits.AllFlags;

        uint unmasked = (uint)flags & ~maskedBits & 0x3Fu;
        if (unmasked == 0) return false;

        if ((State.Cr4 & CpuState.Cr4Osxmmexcpt) != 0) throw new CpuException(19);
        throw new CpuException(6);
    }

    /// <summary>
    /// The SSE opcodes proper (everything but the eight MMX-register
    /// instructions of Cpu.Mmx.cs). <paramref name="opcode"/> is the second
    /// opcode byte, already read by Cpu.TwoByte.cs's switch; the mandatory
    /// prefix (none = packed single, F3 = scalar single) is read from
    /// <c>_prefixes</c> the same way Cpu.Mmx.cs already does for MMX's own
    /// prefix rejection.
    /// </summary>
    private void ExecuteSse(byte opcode)
    {
        RequireSseInstruction();

        // Two (or three) mandatory prefixes together -- 66+F2, 66+F3 or
        // F2+F3 -- name no instruction on any silicon this emulator
        // carries: the coordinator's own decision from P4.1
        // (plans/specs/sse.md), kept for every pairing, not just 66+F2 (a
        // review finding: the earlier version only ever rejected 66+F2
        // together, so 66+F3 fell through and ran as if F3 had never been
        // there, and F2+F3 likewise ran as plain F2 -- both silently
        // ignoring a prefix instead of raising the #UD this decision
        // requires).
        int prefixCount = (_prefixes.OperandSize ? 1 : 0) + (_prefixes.RepNe ? 1 : 0) + (_prefixes.Rep ? 1 : 0);
        if (prefixCount > 1)
        {
            throw new CpuException(6);
        }

        // 66 and F2 name SSE2's packed/scalar-double space (work package
        // P4.2, plans/specs/sse2.md §1): on a model that actually carries
        // SSE2, the same opcode byte instead means the double-precision
        // sibling this method's own switch dispatches for the single-
        // precision case below -- Cpu.Sse2.Arithmetic.cs's ExecuteSse2Float
        // decodes its own ModRM afresh, exactly as this method is about to.
        // Without SSE2, this stays #UD.
        if (_prefixes.OperandSize || _prefixes.RepNe)
        {
            if (State.Features.Has(CpuFeature.Sse2))
            {
                ExecuteSse2Float(opcode);
                return;
            }

            throw new CpuException(6);
        }

        bool scalar = _prefixes.Rep;

        switch (opcode)
        {
            case 0x10: ExecuteMovupsOrMovss(store: false, scalar); return;
            case 0x11: ExecuteMovupsOrMovss(store: true, scalar); return;
            case 0x12: ExecuteMovlpsLoad(scalar); return;
            case 0x13: ExecuteMovlpsStore(scalar); return;
            case 0x14: ExecuteUnpck(low: true, scalar); return;
            case 0x15: ExecuteUnpck(low: false, scalar); return;
            case 0x16: ExecuteMovhpsLoad(scalar); return;
            case 0x17: ExecuteMovhpsStore(scalar); return;
            case 0x28: ExecuteMovaps(store: false, scalar); return;
            case 0x29: ExecuteMovaps(store: true, scalar); return;
            case 0x2A: ExecuteCvtToPs(scalar); return;
            case 0x2B: ExecuteMovntps(scalar); return;
            case 0x2C: ExecuteCvtFromPs(scalar, truncate: true); return;
            case 0x2D: ExecuteCvtFromPs(scalar, truncate: false); return;
            case 0x2E: ExecuteComiss(scalar, signalOnQuietNaN: false); return;   // UCOMISS
            case 0x2F: ExecuteComiss(scalar, signalOnQuietNaN: true); return;    // COMISS
            case 0x50: ExecuteMovmskps(scalar); return;
            case 0x51: ExecuteUnaryArith(scalar, SseUnaryOp.Sqrt); return;
            case 0x52: ExecuteUnaryArith(scalar, SseUnaryOp.Rsqrt); return;
            case 0x53: ExecuteUnaryArith(scalar, SseUnaryOp.Rcp); return;
            case 0x54: ExecuteLogical(scalar, SseLogicalOp.And); return;
            case 0x55: ExecuteLogical(scalar, SseLogicalOp.AndNot); return;
            case 0x56: ExecuteLogical(scalar, SseLogicalOp.Or); return;
            case 0x57: ExecuteLogical(scalar, SseLogicalOp.Xor); return;
            case 0x58: ExecuteBinaryArith(scalar, SseBinaryOp.Add); return;
            case 0x59: ExecuteBinaryArith(scalar, SseBinaryOp.Mul); return;
            case 0x5C: ExecuteBinaryArith(scalar, SseBinaryOp.Sub); return;
            case 0x5D: ExecuteBinaryArith(scalar, SseBinaryOp.Min); return;
            case 0x5E: ExecuteBinaryArith(scalar, SseBinaryOp.Div); return;
            case 0x5F: ExecuteBinaryArith(scalar, SseBinaryOp.Max); return;
            case 0xC2: ExecuteCmp(scalar); return;
            case 0xC6: ExecuteShufps(scalar); return;
            default: throw new CpuException(6);
        }
    }

    // --- MOVUPS / MOVSS (0F 10/11) ------------------------------------------

    /// <summary>
    /// MOVUPS: the whole 128 bits, unaligned allowed -- the one m128 form
    /// the alignment rule exempts. MOVSS (F3): only the low dword moves in
    /// a register-to-register move; loading from memory instead zeroes
    /// bits 127:32 of the destination (plans/specs/sse.md §1).
    /// </summary>
    private void ExecuteMovupsOrMovss(bool store, bool scalar)
    {
        var m = DecodeModRm();
        if (SseTaskSwitched()) return;

        if (scalar)
        {
            if (store)
            {
                uint value = Sse.Xmm(m.Reg).D0;
                if (m.IsRegister) Sse.Xmm(m.Register).D0 = value;
                else WriteMem32(m.Segment, m.Offset, value);
            }
            else if (m.IsRegister)
            {
                Sse.Xmm(m.Reg).D0 = Sse.Xmm(m.Register).D0;
            }
            else
            {
                Sse.Xmm(m.Reg) = new XmmValue { D0 = ReadMem32(m.Segment, m.Offset) };
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

    // --- MOVLPS / MOVHLPS (0F 12), MOVLPS store (0F 13) ----------------------

    /// <summary>0F 12: MOVLPS (mem, dest.Low &lt;- m64) or, mod=11, MOVHLPS (dest.Low &lt;- src.High). Neither has an F3 (scalar) form.</summary>
    private void ExecuteMovlpsLoad(bool scalar)
    {
        var m = DecodeModRm();
        if (scalar) throw new CpuException(6);
        if (SseTaskSwitched()) return;

        ulong low = m.IsRegister ? Sse.Xmm(m.Register).High : ReadMemQword(m.Segment, m.Offset);
        Sse.Xmm(m.Reg).D0 = (uint)low;
        Sse.Xmm(m.Reg).D1 = (uint)(low >> 32);
    }

    /// <summary>0F 13: MOVLPS store, memory only.</summary>
    private void ExecuteMovlpsStore(bool scalar)
    {
        var m = DecodeModRm();
        if (scalar || m.IsRegister) throw new CpuException(6);
        if (SseTaskSwitched()) return;

        WriteMemQword(m.Segment, m.Offset, Sse.Xmm(m.Reg).Low);
    }

    // --- MOVHPS / MOVLHPS (0F 16), MOVHPS store (0F 17) ----------------------

    /// <summary>0F 16: MOVHPS (mem, dest.High &lt;- m64) or, mod=11, MOVLHPS (dest.High &lt;- src.Low).</summary>
    private void ExecuteMovhpsLoad(bool scalar)
    {
        var m = DecodeModRm();
        if (scalar) throw new CpuException(6);
        if (SseTaskSwitched()) return;

        ulong high = m.IsRegister ? Sse.Xmm(m.Register).Low : ReadMemQword(m.Segment, m.Offset);
        Sse.Xmm(m.Reg).D2 = (uint)high;
        Sse.Xmm(m.Reg).D3 = (uint)(high >> 32);
    }

    /// <summary>0F 17: MOVHPS store, memory only.</summary>
    private void ExecuteMovhpsStore(bool scalar)
    {
        var m = DecodeModRm();
        if (scalar || m.IsRegister) throw new CpuException(6);
        if (SseTaskSwitched()) return;

        WriteMemQword(m.Segment, m.Offset, Sse.Xmm(m.Reg).High);
    }

    // --- UNPCKLPS / UNPCKHPS (0F 14/15) --------------------------------------

    /// <summary>Interleaves the destination's and the source's low (or high) two lanes; a memory source needs 16-byte alignment (the coordinator's own extension of the general legacy-SSE rule to UNPCK).</summary>
    private void ExecuteUnpck(bool low, bool scalar)
    {
        var m = DecodeModRm();
        if (scalar) throw new CpuException(6);
        if (SseTaskSwitched()) return;

        XmmValue destination = Sse.Xmm(m.Reg);
        XmmValue source = ReadXmmSourceAligned(m);

        Sse.Xmm(m.Reg) = low
            ? new XmmValue { D0 = destination.D0, D1 = source.D0, D2 = destination.D1, D3 = source.D1 }
            : new XmmValue { D0 = destination.D2, D1 = source.D2, D2 = destination.D3, D3 = source.D3 };
    }

    // --- MOVAPS (0F 28/29) ----------------------------------------------------

    private void ExecuteMovaps(bool store, bool scalar)
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

    // --- MOVNTPS (0F 2B) -------------------------------------------------------

    /// <summary>MOVNTPS: memory only, 16-byte aligned; a non-temporal hint with nothing behind it to cache, so this is an ordinary aligned store.</summary>
    private void ExecuteMovntps(bool scalar)
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

    // --- MOVMSKPS (0F 50) -------------------------------------------------------

    /// <summary>MOVMSKPS: register only. r32 &lt;- the sign bit of each of the source's four packed singles.</summary>
    private void ExecuteMovmskps(bool scalar)
    {
        var m = DecodeModRm();
        if (scalar || !m.IsRegister) throw new CpuException(6);
        if (SseTaskSwitched()) return;

        XmmValue value = Sse.Xmm(m.Register);
        uint mask = 0;
        if (SingleFloat.SignBit(value.D0)) mask |= 1;
        if (SingleFloat.SignBit(value.D1)) mask |= 2;
        if (SingleFloat.SignBit(value.D2)) mask |= 4;
        if (SingleFloat.SignBit(value.D3)) mask |= 8;
        State.SetReg32(m.Reg, mask);
    }

    // --- SHUFPS (0F C6) ----------------------------------------------------------

    /// <summary>SHUFPS imm8: result lanes 0-1 come from the destination, lanes 2-3 from the (16-byte aligned, when memory) source, each selected by a 2-bit field of the immediate.</summary>
    private void ExecuteShufps(bool scalar)
    {
        var m = DecodeModRm();
        if (scalar) throw new CpuException(6);
        if (SseTaskSwitched()) return;

        XmmValue destination = Sse.Xmm(m.Reg);
        XmmValue source = ReadXmmSourceAligned(m);
        byte imm8 = FetchByte();

        Sse.Xmm(m.Reg) = new XmmValue
        {
            D0 = destination.Lane(imm8 & 3),
            D1 = destination.Lane((imm8 >> 2) & 3),
            D2 = source.Lane((imm8 >> 4) & 3),
            D3 = source.Lane((imm8 >> 6) & 3),
        };
    }

    // --- LDMXCSR / STMXCSR / SFENCE (0F AE /2, /3, /7) ------------------------
    //
    // Reached directly from Cpu.TwoByte.cs's group-15 switch, not through
    // ExecuteSse above (FXSAVE/FXRSTOR, the group's other two members, are
    // dispatched the same direct way in Cpu.P6.cs): each checks its own
    // gating rather than sharing ExecuteSse's, since the ModRM byte is
    // already decoded by the caller.

    /// <summary>0F AE /2: LDMXCSR, memory only. A reserved bit outside MXCSR_MASK is <c>#GP(0)</c>, before MXCSR itself changes (plans/specs/sse.md §3).</summary>
    private void ExecuteLdmxcsr(in ModRm m)
    {
        RequireSseInstruction();
        if (m.IsRegister) throw new CpuException(6);
        if (SseTaskSwitched()) return;

        uint value = ReadMem32(m.Segment, m.Offset);
        if ((value & ~SseUnit.MxcsrMaskFor(State.Model)) != 0) throw new CpuException(13, 0);

        Sse.Mxcsr = value;
    }

    /// <summary>
    /// 0F AE /3: STMXCSR, memory only. Masked by the current model's own
    /// <see cref="SseUnit.MxcsrMaskFor"/> before it is stored (a review
    /// finding, the same one <see cref="CurrentDaz"/> now guards against):
    /// on ordinary, guest-only-reachable state <c>Sse.Mxcsr</c> already sits
    /// inside this mask (LDMXCSR and FXRSTOR both enforce it on the way
    /// in), so this is a no-op there -- it only matters for the one state a
    /// guest instruction never produces, a snapshot restored across a model
    /// change, where it stops a stray out-of-mask bit from round-tripping
    /// back out through STMXCSR into a value a guest's own subsequent
    /// LDMXCSR of that same value would then wrongly reject.
    /// </summary>
    private void ExecuteStmxcsr(in ModRm m)
    {
        RequireSseInstruction();
        if (m.IsRegister) throw new CpuException(6);
        if (SseTaskSwitched()) return;

        WriteMem32(m.Segment, m.Offset, Sse.Mxcsr & SseUnit.MxcsrMaskFor(State.Model));
    }

    /// <summary>
    /// 0F AE /7, mod=11: SFENCE. Aurora's memory model has no store
    /// reordering to fence, so once the gating checks pass this is a pure
    /// no-op. The memory form of /7 is CLFLUSH (SSE2), not implemented
    /// here: <c>#UD</c>.
    ///
    /// Unlike every other SSE opcode's <see cref="RequireSseInstruction"/>
    /// and <see cref="SseTaskSwitched"/> gating, SFENCE checks neither
    /// CR0.EM, CR0.TS nor CR4.OSFXSR (a review finding on this work
    /// package, corrected here): it moves no FPU/XMM state and needs none
    /// of that context to be valid, so only CPUID.SSE gates it. LOCK is
    /// already <c>#UD</c> before this method is ever reached -- group 15 has
    /// no case in <c>LockAllowedTwoByte</c> (Cpu.Execute.cs), so the
    /// prefix's own generic check rejects it ahead of the opcode dispatch.
    /// </summary>
    private void ExecuteSfence(in ModRm m)
    {
        RequireFeature(CpuFeature.Sse);
        if (!m.IsRegister) throw new CpuException(6);
    }
}
