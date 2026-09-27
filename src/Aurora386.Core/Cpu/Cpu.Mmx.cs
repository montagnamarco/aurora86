// The MMX instruction set (Pentium MMX, "P55C"): eight packed 64-bit integer
// operations aliased onto the x87 register file rather than a file of their
// own -- see Fpu/X87Unit.cs's "MMX" section and Fpu/Raw80.cs for why one
// register file serves both, and why every instruction below (EMMS excepted)
// calls Fpu.EnterMmxState() when it is done: "TOP <- 0, every tag valid" is
// what an operating system's FSAVE/FRSTOR-based task switch and a mixed
// FPU/MMX program both rely on (SDM Vol.1 ch.8, "MMX Technology System
// Programming Model").
//
// Dispatched from Cpu.TwoByte.cs's 0F switch; decoding (DecodeModRm, the r/m
// operand) reuses the same helpers every other two-byte opcode does. A 64-bit
// memory operand is checked and translated as one 8-byte unit -- exactly the
// way CMPXCHG8B is in Cpu.Pentium.cs, and for the same reason: a segment-limit
// violation must never leave a half-written result behind, so the whole
// access is validated before either half is touched.
//
// The 66/F2/F3 prefixes turn these same opcodes into SSE2's own 128-bit
// packed-integer instructions on a Pentium 4 or later (Cpu.Sse2.Integer.cs's
// ExecuteSse2Integer, routed to from ExecuteMmx below once a prefix and the
// feature are both confirmed). On a processor without CPUID.SSE2 -- every
// model before the Pentium 4 -- a prefixed encoding of one of these opcodes
// stays <c>#UD</c>: the period SDM calls the operand-size (and other)
// prefixes on an MMX instruction "reserved", with unpredictable behaviour,
// not "ignored" or "defined to keep the unprefixed meaning", and Bochs's own
// interpreter agrees, rejecting them outright on a CPU without SSE2. This is
// the coordinator's own decision (plans/specs/sse2.md, work package P4.2),
// not a fact measured from silicon Aurora can run this specific case on.

namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>Which lane width a shift instruction works on.</summary>
    private enum ShiftWidth { Word, Dword, Qword }

    /// <summary>
    /// EMMS and the sixty-odd 0F-prefixed opcodes the Pentium MMX added.
    /// <paramref name="opcode"/> is the second opcode byte, already read by
    /// Cpu.TwoByte.cs's switch.
    /// </summary>
    /// <summary>
    /// Whether 0F 71/72/73 names a real instruction: a register operand, and
    /// reg /2 (logical right), /4 (arithmetic right, not for quadwords) or /6
    /// (left).
    /// </summary>
    private static bool IsValidShiftImmediateEncoding(byte opcode, in ModRm m)
    {
        if (!m.IsRegister)
        {
            return false;
        }

        bool quadword = opcode == 0x73;

        return m.Reg switch
        {
            2 or 6 => true,
            4 => !quadword,
            _ => false,
        };
    }

    /// <summary>
    /// Whether <paramref name="opcode"/>'s r/m operand shape is invalid at
    /// decode time, for the four SSE-gated instructions whose valid shape
    /// is fixed by the opcode alone: PEXTRW, PMOVMSKB and MASKMOVQ are
    /// register-register only (no memory form exists at all), and MOVNTQ
    /// is memory only (a register operand is undefined). Called ahead of
    /// the CR0.TS check in <see cref="ExecuteMmx"/> -- see that call site's
    /// own remark.
    /// </summary>
    private static bool IsInvalidRmShapeForOpcode(byte opcode, in ModRm m) => opcode switch
    {
        0xC5 or 0xD7 or 0xF7 => !m.IsRegister,
        0xE7 => m.IsRegister,
        _ => false,
    };

    /// <summary>
    /// The 66-prefixed dispatch's own version of <see cref="IsInvalidRmShapeForOpcode"/>:
    /// the same rule for every opcode that already has one (C5/D7/F7/E7,
    /// unaffected by which single prefix reached them, since only 66 ever
    /// widens those particular opcodes -- <see cref="IsValidSse2IntegerPrefixedForm"/>
    /// already rejects F2/F3 there), plus one the unprefixed table has no
    /// opcode for at all: 0F D6 is register-register only under F2
    /// (MOVDQ2Q) or F3 (MOVQ2DQ) -- neither has a memory form -- but still
    /// accepts memory under 66 (MOVQ store). A review finding: without this,
    /// F2/F3 0F D6 with a memory operand reached <see cref="SseTaskSwitched"/>
    /// before Cpu.Sse2.Integer.cs's own handlers ever got a chance to reject
    /// it, so CR0.TS = 1 wrongly won the race and turned what should be
    /// <c>#UD</c> into <c>#NM</c> -- the same class of bug the P4.1 review
    /// already fixed for the unprefixed MMX opcodes.
    /// </summary>
    private static bool IsInvalidRmShapeForSse2Integer(byte opcode, in ModRm m, Prefixes prefixes)
    {
        if (opcode == 0xD6 && (prefixes.RepNe || prefixes.Rep)) return !m.IsRegister;
        return IsInvalidRmShapeForOpcode(opcode, m);
    }

    /// <summary>
    /// Whether <paramref name="opcode"/> is one of the eight MMX-register
    /// instructions SSE itself introduced (plans/specs/sse.md §2: PSHUFW,
    /// PINSRW, PEXTRW, PMOVMSKB, PAVGB/W, PMULHUW, PMINSW/PMAXSW, PMINUB/
    /// PMAXUB, PSADBW, MOVNTQ, MASKMOVQ) -- gated on CPUID.SSE rather than
    /// CPUID.MMX, even though every one of them still works on the ordinary
    /// MM register file and follows the ordinary MMX EM/TS rules. Test case
    /// 19 (plans/specs/sse.md) is exactly this: PSHUFW on a Pentium II,
    /// which has MMX but not SSE, is <c>#UD</c>.
    /// </summary>
    private static bool IsSseGatedMmxOpcode(byte opcode) => opcode switch
    {
        0x70 or 0xC4 or 0xC5 or 0xD7 or 0xDA or 0xDE
            or 0xE0 or 0xE3 or 0xE4 or 0xE7 or 0xEA or 0xEE or 0xF6 or 0xF7 => true,
        _ => false,
    };

    /// <summary>
    /// Opcodes that were never assigned any meaning at all without a
    /// mandatory prefix -- 0F 6C/6D (PUNPCKLQDQ/HQDQ) and 0F D6 (MOVQ/
    /// MOVDQ2Q/MOVQ2DQ) exist only as SSE2's own 66/F2/F3-prefixed
    /// encodings, unlike every other opcode this dispatch reaches, which
    /// widens an MMX instruction that already existed unprefixed. Checked
    /// first, unconditionally: on any silicon, at any CR0.TS, the bare byte
    /// is simply not an instruction.
    /// </summary>
    private static bool HasNoUnprefixedForm(byte opcode) => opcode is 0x6C or 0x6D or 0xD6;

    /// <summary>0F D4/F4/FB (PADDQ/PMULUDQ/PSUBQ), unprefixed: SSE2's own three MMX-register additions (plans/specs/sse2.md §1), gated by CPUID.SSE2 rather than CPUID.MMX even though they still follow the ordinary MMX EM/TS rules -- exactly the same shape as <see cref="IsSseGatedMmxOpcode"/>'s own eight, but a newer feature bit.</summary>
    private static bool RequiresSse2FeatureUnprefixed(byte opcode) => opcode is 0xD4 or 0xF4 or 0xFB;

    /// <summary>
    /// Whether <paramref name="opcode"/> has a genuine SSE2 meaning under
    /// exactly the one mandatory prefix <see cref="_prefixes"/> carries
    /// (the caller has already rejected more than one at once): 66 widens
    /// every MMX-integer opcode this dispatch reaches except EMMS (no
    /// operand to widen) to its 128-bit XMM form (plans/specs/sse2.md §4);
    /// F2/F3 exist only for the handful of opcodes that carry a genuinely
    /// different instruction under those prefixes (PSHUFHW/PSHUFLW/MOVDQU/
    /// MOVQ-load/MOVDQ2Q/MOVQ2DQ).
    /// </summary>
    private static bool IsValidSse2IntegerPrefixedForm(byte opcode, Prefixes prefixes)
    {
        if (prefixes.OperandSize) return opcode != 0x77;
        if (prefixes.Rep) return opcode is 0x6F or 0x7F or 0x70 or 0x7E or 0xD6;
        if (prefixes.RepNe) return opcode is 0x70 or 0xD6;
        return false;
    }

    private void ExecuteMmx(byte opcode)
    {
        if (_prefixes.OperandSize || _prefixes.Rep || _prefixes.RepNe)
        {
            int prefixCount = (_prefixes.OperandSize ? 1 : 0) + (_prefixes.Rep ? 1 : 0) + (_prefixes.RepNe ? 1 : 0);
            if (prefixCount > 1 || !State.Features.Has(CpuFeature.Sse2) || !IsValidSse2IntegerPrefixedForm(opcode, _prefixes))
            {
                throw new CpuException(6);
            }

            // XMM-touching, exactly like every SSE opcode (plans/specs/sse2.md
            // §5): #UD for CR0.EM or CR4.OSFXSR=0, ahead of #NM for CR0.TS --
            // and, like the unprefixed path just below, an invalid shift-group
            // or fixed-rm-shape encoding is #UD ahead of TS too (the same
            // ordering the P4.1 review finding fixed there), which is why the
            // ModRM byte is decoded and validated here, before
            // <see cref="SseTaskSwitched"/> ever runs, and handed already
            // decoded to Cpu.Sse2.Integer.cs's own ExecuteSse2Integer.
            RequireSseInstruction(CpuFeature.Sse2);

            ModRm sse2IntegerModRm = DecodeModRm();
            if (opcode is 0x71 or 0x72 or 0x73 && !IsValidXmmShiftImmediateEncoding(opcode, sse2IntegerModRm))
            {
                throw new CpuException(6);
            }
            if (IsInvalidRmShapeForSse2Integer(opcode, sse2IntegerModRm, _prefixes))
            {
                throw new CpuException(6);
            }

            if (SseTaskSwitched()) return;
            ExecuteSse2Integer(opcode, sse2IntegerModRm);
            return;
        }

        // Unprefixed: the ordinary MMX opcode, or (D4/F4/FB) SSE2's own
        // three MMX-register additions -- see HasNoUnprefixedForm's own
        // remark for the three opcodes that reach neither branch here.
        if (HasNoUnprefixedForm(opcode)) throw new CpuException(6);

        // Absent silicon, or a CR0.EM ("no coprocessor at all") processor:
        // both are #UD, not #NM. Unlike an x87 escape -- always a defined
        // opcode whether or not a coprocessor exists -- these bytes simply
        // are not instructions at all without MMX or with EM set, since
        // there is no register file behind them to alias. (Every MMX
        // instruction's SDM Vol.2B reference page lists CPUID.MMX and
        // CR0.EM together under #UD, and CR0.TS alone under #NM.)
        RequireFeature(RequiresSse2FeatureUnprefixed(opcode) ? CpuFeature.Sse2
            : IsSseGatedMmxOpcode(opcode) ? CpuFeature.Sse : CpuFeature.Mmx);
        if ((State.Cr0 & CpuState.Cr0Emulation) != 0) throw new CpuException(6);

        if (opcode == 0x77)   // EMMS: no ModRM byte, no operand.
        {
            if ((State.Cr0 & CpuState.Cr0TaskSwitched) != 0)
            {
                FaultNow(7, 0);
                return;
            }

            if (DeliverPendingX87Exception())
            {
                return;
            }

            Fpu.EmptyMmxState();
            return;
        }

        ModRm m = DecodeModRm();

        // An invalid encoding of the immediate shift group is #UD, and #UD
        // outranks #NM (SDM vol. 3, exception priorities): it is checked
        // before TS, not inside the shift itself. The same ordering applies
        // to the four SSE-gated instructions below whose r/m operand shape
        // is fixed by the opcode alone (PEXTRW/PMOVMSKB/MASKMOVQ have no
        // memory form; MOVNTQ has no register form) -- a P4.1 review
        // finding: these checks used to live inside each instruction's own
        // handler, reached only after the TS check below had already
        // passed, so CR0.TS = 1 wrongly won the race and turned what should
        // be #UD into #NM.
        if (opcode is 0x71 or 0x72 or 0x73 && !IsValidShiftImmediateEncoding(opcode, m))
        {
            throw new CpuException(6);
        }

        if (IsInvalidRmShapeForOpcode(opcode, m))
        {
            throw new CpuException(6);
        }

        // Fetch the complete instruction before reporting an execution fault.
        // Data operands are still untouched; an instruction-fetch fault must
        // not be hidden by an old pending x87 exception.
        byte immediate = 0;
        if (opcode is 0x70 or 0x71 or 0x72 or 0x73 or 0xC4 or 0xC5)
        {
            immediate = FetchByte();
        }

        // TS says the register file belongs to another task. Check it before
        // pending x87 state and before touching the data operand or MMX tags.
        if ((State.Cr0 & CpuState.Cr0TaskSwitched) != 0)
        {
            FaultNow(7, 0);
            return;
        }

        if (DeliverPendingX87Exception())
        {
            return;
        }

        switch (opcode)
        {
            case 0x6E: ExecuteMovdLoad(m); return;
            case 0x7E: ExecuteMovdStore(m); return;
            case 0x6F: ExecuteMovqLoad(m); return;
            case 0x7F: ExecuteMovqStore(m); return;

            case 0x71: ExecuteShiftImmediate(m, ShiftWidth.Word, immediate); return;
            case 0x72: ExecuteShiftImmediate(m, ShiftWidth.Dword, immediate); return;
            case 0x73: ExecuteShiftImmediate(m, ShiftWidth.Qword, immediate); return;

            case 0xF1: ExecuteShiftRegister(m, ShiftWidth.Word, left: true, arithmetic: false); return;
            case 0xF2: ExecuteShiftRegister(m, ShiftWidth.Dword, left: true, arithmetic: false); return;
            case 0xF3: ExecuteShiftRegister(m, ShiftWidth.Qword, left: true, arithmetic: false); return;
            case 0xD1: ExecuteShiftRegister(m, ShiftWidth.Word, left: false, arithmetic: false); return;
            case 0xD2: ExecuteShiftRegister(m, ShiftWidth.Dword, left: false, arithmetic: false); return;
            case 0xD3: ExecuteShiftRegister(m, ShiftWidth.Qword, left: false, arithmetic: false); return;
            case 0xE1: ExecuteShiftRegister(m, ShiftWidth.Word, left: false, arithmetic: true); return;
            case 0xE2: ExecuteShiftRegister(m, ShiftWidth.Dword, left: false, arithmetic: true); return;

            // --- the SSE-gated MMX-register instructions (plans/specs/sse.md
            // §2): PAVGB/W, PMULHUW, PMINSW/PMAXSW, PMINUB/PMAXUB and PSADBW
            // are two-operand forms exactly like the ordinary MMX ALU above
            // and simply fall to the default case's ExecuteMmxAlu; the other
            // five need their own operand shapes.
            case 0x70: ExecutePshufw(m, immediate); return;
            case 0xC4: ExecutePinsrw(m, immediate); return;
            case 0xC5: ExecutePextrw(m, immediate); return;
            case 0xD7: ExecutePmovmskb(m); return;
            case 0xE7: ExecuteMovntq(m); return;
            case 0xF7: ExecuteMaskmovq(m); return;

            default: ExecuteMmxAlu(opcode, m); return;
        }
    }

    // --- the SSE-gated MMX-register instructions --------------------------------

    /// <summary>0F 70: PSHUFW mm1, mm2/m64, imm8 -- each of the four destination words is one of the source's four words, selected by a 2-bit field of the immediate.</summary>
    private void ExecutePshufw(in ModRm m, byte imm8)
    {
        ulong source = ReadMmxSource(m);

        ulong result = 0;
        for (int lane = 0; lane < 4; lane++)
        {
            int selector = (imm8 >> (lane * 2)) & 3;
            result |= (ulong)GetWordLane(source, selector) << (lane * 16);
        }

        Fpu.WriteMmx(m.Reg, result);
        Fpu.EnterMmxState();
    }

    /// <summary>0F C4: PINSRW mm, r32/m16, imm8 -- the low word of the source (a GPR, not another MM register, or a 16-bit memory operand) replaces one of the four words of the destination, named by the low two bits of the immediate.</summary>
    private void ExecutePinsrw(in ModRm m, byte imm8)
    {
        ushort value = m.IsRegister ? (ushort)State.GetReg32(m.Register) : ReadMem16(m.Segment, m.Offset);
        int lane = imm8 & 3;

        ulong destination = Fpu.ReadMmx(m.Reg);
        destination = (destination & ~((ulong)0xFFFF << (lane * 16))) | ((ulong)value << (lane * 16));
        Fpu.WriteMmx(m.Reg, destination);
        Fpu.EnterMmxState();
    }

    /// <summary>0F C5: PEXTRW r32, mm, imm8 -- register-register only (no memory form exists, already rejected with #UD by <see cref="IsInvalidRmShapeForOpcode"/> in <see cref="ExecuteMmx"/>): one of the source MM register's four words, zero-extended into a GPR.</summary>
    private void ExecutePextrw(in ModRm m, byte imm8)
    {
        int lane = imm8 & 3;
        ushort value = GetWordLane(Fpu.ReadMmx(m.Register), lane);
        State.SetReg32(m.Reg, value);
        Fpu.EnterMmxState();
    }

    /// <summary>0F D7: PMOVMSKB r32, mm -- register-register only (see <see cref="ExecutePextrw"/>'s own remark): a GPR gets the sign (high) bit of each of the source's eight bytes.</summary>
    private void ExecutePmovmskb(in ModRm m)
    {
        ulong value = Fpu.ReadMmx(m.Register);
        uint mask = 0;
        for (int lane = 0; lane < 8; lane++)
        {
            if ((GetByteLane(value, lane) & 0x80) != 0) mask |= 1u << lane;
        }

        State.SetReg32(m.Reg, mask);
        Fpu.EnterMmxState();
    }

    /// <summary>0F E7: MOVNTQ mm, m64 -- memory only (a register operand is undefined, already rejected with #UD by <see cref="IsInvalidRmShapeForOpcode"/> in <see cref="ExecuteMmx"/>): a non-temporal hint with nothing behind it to cache, so an ordinary quadword store.</summary>
    private void ExecuteMovntq(in ModRm m)
    {
        WriteMemQword(m.Segment, m.Offset, Fpu.ReadMmx(m.Reg));
        Fpu.EnterMmxState();
    }

    /// <summary>
    /// 0F F7: MASKMOVQ mm, mm -- register-register only (no memory form
    /// exists, already rejected with #UD by <see cref="IsInvalidRmShapeForOpcode"/>
    /// in <see cref="ExecuteMmx"/>). The reg-field register supplies the
    /// data, the rm-field register the mask: each byte whose mask byte has
    /// bit 7 set is stored to DS:(E)DI (the segment overridable, the
    /// address at the current address size, like every other string-style
    /// implicit memory operand). <see cref="ValidateMaskmovqDestination"/>
    /// checks the whole 8-byte destination before any byte is actually
    /// stored (a P4.1 review finding, that method's own remark).
    /// </summary>
    private void ExecuteMaskmovq(in ModRm m)
    {
        ulong data = Fpu.ReadMmx(m.Reg);
        ulong mask = Fpu.ReadMmx(m.Register);
        SegReg segment = EffectiveSegment(SegReg.Ds);
        uint baseOffset = AddressSize32 ? State.Edi : State.Di;

        ValidateMaskmovqDestination(segment, baseOffset);

        for (int lane = 0; lane < 8; lane++)
        {
            if ((GetByteLane(mask, lane) & 0x80) == 0) continue;

            uint offset = AddressSize32 ? baseOffset + (uint)lane : (ushort)(baseOffset + lane);
            WriteMem8(segment, offset, GetByteLane(data, lane));
        }

        Fpu.EnterMmxState();
    }

    /// <summary>
    /// MASKMOVQ's destination is one 8-byte unit, checked as a whole before
    /// any byte is actually stored (a P4.1 review finding): 16-bit
    /// addressing raises <c>#GP(0)</c> when the range would wrap past
    /// FFFFh -- an instruction-specific rule MASKMOVQ alone needs, since it
    /// is the only MMX memory operand addressed through DS:(E)DI rather
    /// than a ModRM byte, and so the only one whose address never goes
    /// through <see cref="CheckLimit"/>'s own 32-bit-terms wraparound
    /// handling by construction; the segment limit is then checked once
    /// for the whole 8 bytes, and both pages it can touch are translated
    /// for writing -- exactly the "validate the whole access, then write"
    /// discipline <see cref="ValidateFxsaveArea"/> (Cpu.P6.cs) already
    /// applies to its own 512-byte operand.
    /// </summary>
    private void ValidateMaskmovqDestination(SegReg segment, uint baseOffset)
    {
        const uint operandSize = 8;

        if (!AddressSize32 && baseOffset + operandSize - 1 > 0xFFFFu)
        {
            throw new CpuException(13, 0);
        }

        CheckLimit(segment, baseOffset, operandSize);

        uint linear = LinearAddress(segment, baseOffset);
        uint lastLinear = linear + operandSize - 1;
        TranslateLinear(linear, true);
        TranslateLinear(lastLinear, true);
    }

    // --- MOVD / MOVQ -----------------------------------------------------------

    /// <summary>0F 6E: MMreg &lt;- r/m32, zero-extended into the high dword.</summary>
    private void ExecuteMovdLoad(in ModRm m)
    {
        uint value = m.IsRegister ? State.GetReg32(m.Register) : ReadMem32(m.Segment, m.Offset);
        Fpu.WriteMmx(m.Reg, value);
        Fpu.EnterMmxState();
    }

    /// <summary>0F 7E: r/m32 &lt;- the low dword of MMreg.</summary>
    private void ExecuteMovdStore(in ModRm m)
    {
        uint value = (uint)Fpu.ReadMmx(m.Reg);
        if (m.IsRegister) State.SetReg32(m.Register, value);
        else WriteMem32(m.Segment, m.Offset, value);
        Fpu.EnterMmxState();
    }

    /// <summary>0F 6F: MMreg &lt;- MMreg/m64.</summary>
    private void ExecuteMovqLoad(in ModRm m)
    {
        ulong value = ReadMmxSource(m);
        Fpu.WriteMmx(m.Reg, value);
        Fpu.EnterMmxState();
    }

    /// <summary>0F 7F: MMreg/m64 &lt;- MMreg.</summary>
    private void ExecuteMovqStore(in ModRm m)
    {
        ulong value = Fpu.ReadMmx(m.Reg);
        if (m.IsRegister) Fpu.WriteMmx(m.Register, value);
        else WriteMemQword(m.Segment, m.Offset, value);
        Fpu.EnterMmxState();
    }

    /// <summary>The rm-side 64-bit operand every arithmetic, logical and MOVQ-load form reads: MMreg when mod==3, an 8-byte memory operand otherwise.</summary>
    private ulong ReadMmxSource(in ModRm m)
        => m.IsRegister ? Fpu.ReadMmx(m.Register) : ReadMemQword(m.Segment, m.Offset);

    // --- packed arithmetic, logic, pack and unpack ------------------------------

    /// <summary>
    /// Every two-operand MMX instruction except the shifts: the destination is
    /// always MMreg (the reg field), the source always MMreg/m64 (the rm
    /// field), and the whole 64-bit result replaces the destination.
    /// </summary>
    private void ExecuteMmxAlu(byte opcode, in ModRm m)
    {
        ulong destination = Fpu.ReadMmx(m.Reg);
        ulong source = ReadMmxSource(m);

        ulong result = MmxAluLane(opcode, destination, source);

        Fpu.WriteMmx(m.Reg, result);
        Fpu.EnterMmxState();
    }

    /// <summary>
    /// The one-64-bit-lane result every two-operand MMX ALU opcode (every
    /// case <see cref="ExecuteMmxAlu"/> itself does not special-case) computes
    /// from a destination and a source, factored out of that method (a P4.2
    /// change) so Cpu.Sse2.Integer.cs's own widened 128-bit forms
    /// (plans/specs/sse2.md §4: "every MMX integer instruction widened to
    /// XMM with the same per-lane rules") can call this exact same per-lane
    /// logic twice -- once for each 64-bit half of the wider XMM operands --
    /// rather than duplicating sixty-odd lambdas a second time. Every one of
    /// these opcodes' 128-bit SSE2 form genuinely does decompose into two
    /// independent 64-bit halves this way (PSADBW's own two-16-bit-sums
    /// shape, in particular, is exactly what applying this same 64-bit
    /// function to each half independently already produces); only PACK*/
    /// PUNPCK* (which combine both halves of both operands into one result)
    /// and the immediate/register shifts (whose count is shared, not
    /// per-half) need their own widened code, in Cpu.Sse2.Integer.cs.
    /// </summary>
    private static ulong MmxAluLane(byte opcode, ulong destination, ulong source)
    {
        return opcode switch
        {
            0x63 => PackWords(destination, source, value => SignedSaturateToByte(value)),      // PACKSSWB
            0x6B => PackSignedDwordsToWords(destination, source),                              // PACKSSDW
            0x67 => PackWords(destination, source, value => UnsignedSaturateToByte(value)),    // PACKUSWB

            0x60 => UnpackLowBytes(destination, source),                      // PUNPCKLBW
            0x61 => UnpackLowWords(destination, source),                      // PUNPCKLWD
            0x62 => UnpackLowDwords(destination, source),                     // PUNPCKLDQ
            0x68 => UnpackHighBytes(destination, source),                     // PUNPCKHBW
            0x69 => UnpackHighWords(destination, source),                     // PUNPCKHWD
            0x6A => UnpackHighDwords(destination, source),                    // PUNPCKHDQ

            0xFC => PerByte(destination, source, (a, b) => (byte)(a + b)),                                  // PADDB
            0xFD => PerWord(destination, source, (a, b) => (ushort)(a + b)),                                // PADDW
            0xFE => PerDword(destination, source, (a, b) => a + b),                                         // PADDD
            0xEC => PerByte(destination, source, (a, b) => SignedSaturateToByte((sbyte)a + (sbyte)b)),      // PADDSB
            0xED => PerWord(destination, source, (a, b) => SignedSaturateToWord((short)a + (short)b)),      // PADDSW
            0xDC => PerByte(destination, source, (a, b) => UnsignedSaturateToByte(a + b)),                  // PADDUSB
            0xDD => PerWord(destination, source, (a, b) => UnsignedSaturateToWord(a + b)),                  // PADDUSW

            0xF8 => PerByte(destination, source, (a, b) => (byte)(a - b)),                                  // PSUBB
            0xF9 => PerWord(destination, source, (a, b) => (ushort)(a - b)),                                // PSUBW
            0xFA => PerDword(destination, source, (a, b) => a - b),                                         // PSUBD
            0xE8 => PerByte(destination, source, (a, b) => SignedSaturateToByte((sbyte)a - (sbyte)b)),      // PSUBSB
            0xE9 => PerWord(destination, source, (a, b) => SignedSaturateToWord((short)a - (short)b)),      // PSUBSW
            0xD8 => PerByte(destination, source, (a, b) => UnsignedSaturateToByte(a - b)),                  // PSUBUSB
            0xD9 => PerWord(destination, source, (a, b) => UnsignedSaturateToWord(a - b)),                  // PSUBUSW

            0xD5 => PerWord(destination, source, (a, b) => (ushort)(a * b)),                                 // PMULLW: low 16 bits, sign-independent
            0xE5 => PerWord(destination, source, (a, b) => (ushort)(((short)a * (short)b) >> 16)),           // PMULHW: high 16 bits of the signed product
            0xF5 => MultiplyAddWords(destination, source),                                                   // PMADDWD

            0x74 => PerByte(destination, source, (a, b) => a == b ? (byte)0xFF : (byte)0),                   // PCMPEQB
            0x75 => PerWord(destination, source, (a, b) => a == b ? (ushort)0xFFFF : (ushort)0),             // PCMPEQW
            0x76 => PerDword(destination, source, (a, b) => a == b ? 0xFFFFFFFFu : 0u),                      // PCMPEQD
            0x64 => PerByte(destination, source, (a, b) => (sbyte)a > (sbyte)b ? (byte)0xFF : (byte)0),      // PCMPGTB
            0x65 => PerWord(destination, source, (a, b) => (short)a > (short)b ? (ushort)0xFFFF : (ushort)0),// PCMPGTW
            0x66 => PerDword(destination, source, (a, b) => (int)a > (int)b ? 0xFFFFFFFFu : 0u),             // PCMPGTD

            0xDB => destination & source,          // PAND
            0xDF => ~destination & source,         // PANDN: NOT(DEST) AND SRC
            0xEB => destination | source,          // POR
            0xEF => destination ^ source,          // PXOR

            // --- the SSE-gated MMX-register instructions (plans/specs/sse.md
            // §2) that share this instruction's two-operand shape.
            0xE0 => PerByte(destination, source, (a, b) => (byte)(((uint)a + b + 1) >> 1)),                  // PAVGB
            0xE3 => PerWord(destination, source, (a, b) => (ushort)(((uint)a + b + 1) >> 1)),                // PAVGW
            0xE4 => PerWord(destination, source, (a, b) => (ushort)(((uint)a * b) >> 16)),                   // PMULHUW: high 16 bits of the *unsigned* product
            0xEA => PerWord(destination, source, (a, b) => (ushort)Math.Min((short)a, (short)b)),            // PMINSW
            0xEE => PerWord(destination, source, (a, b) => (ushort)Math.Max((short)a, (short)b)),            // PMAXSW
            0xDA => PerByte(destination, source, Math.Min),                                                  // PMINUB
            0xDE => PerByte(destination, source, Math.Max),                                                  // PMAXUB
            0xF6 => SumOfAbsoluteDifferences(destination, source),                                           // PSADBW

            // --- SSE2's own three MMX-register additions (work package
            // P4.2, plans/specs/sse2.md §1: "Without prefix, SSE2 also adds
            // to MMX: 0F D4 PADDQ, 0F FB PSUBQ, 0F F4 PMULUDQ", gated by
            // CPUID.SSE2 -- ExecuteMmx's own opcode check requires that
            // feature for these three specifically, ahead of reaching here).
            0xD4 => destination + source,                                                                     // PADDQ: one 64-bit lane, wraps.
            0xFB => destination - source,                                                                     // PSUBQ: one 64-bit lane, wraps.
            0xF4 => (ulong)(uint)destination * (uint)source,                                                   // PMULUDQ: the low 32 bits of each operand, unsigned 32x32->64.

            _ => throw new CpuException(6),
        };
    }

    // --- shifts ------------------------------------------------------------------
    //
    // The count is the *entire* 64-bit source operand, register or memory, not
    // its low byte: a count at or past the element width yields zero for a
    // logical shift and the sign bit repeated for an arithmetic one, computed
    // from that full width so a huge count (say, the top 32 bits nonzero)
    // saturates exactly as a small out-of-range one does (SDM Vol.2B, e.g.
    // PSLLW/PSLLD/PSLLQ "Operation").

    /// <summary>0F D1/D2/D3 (right, logical), E1/E2 (right, arithmetic), F1/F2/F3 (left): count comes from MMreg/m64.</summary>
    private void ExecuteShiftRegister(in ModRm m, ShiftWidth width, bool left, bool arithmetic)
    {
        ulong destination = Fpu.ReadMmx(m.Reg);
        ulong count = ReadMmxSource(m);
        Fpu.WriteMmx(m.Reg, ApplyShift(destination, count, width, left, arithmetic));
        Fpu.EnterMmxState();
    }

    /// <summary>
    /// Group 0F 71/72/73: the rm field is the register shifted in place (a
    /// memory operand is undefined here, unlike every other MMX instruction),
    /// and the reg field is an opcode extension -- /6 left, /2 right logical,
    /// /4 right arithmetic (not defined for the quadword form, since there is
    /// no PSRAQ). Every other reg field is undefined.
    /// </summary>
    private void ExecuteShiftImmediate(in ModRm m, ShiftWidth width, byte count)
    {
        if (!m.IsRegister) throw new CpuException(6);

        bool left, arithmetic;
        switch (m.Reg)
        {
            case 6: left = true; arithmetic = false; break;
            case 2: left = false; arithmetic = false; break;
            case 4:
                if (width == ShiftWidth.Qword) throw new CpuException(6);
                left = false; arithmetic = true;
                break;
            default:
                throw new CpuException(6);
        }

        ulong destination = Fpu.ReadMmx(m.Register);
        Fpu.WriteMmx(m.Register, ApplyShift(destination, count, width, left, arithmetic));
        Fpu.EnterMmxState();
    }

    private static ulong ApplyShift(ulong value, ulong count, ShiftWidth width, bool left, bool arithmetic)
        => width switch
        {
            ShiftWidth.Word => ShiftWordLanes(value, count, left, arithmetic),
            ShiftWidth.Dword => ShiftDwordLanes(value, count, left, arithmetic),
            _ => ShiftQword(value, count, left),
        };

    private static ulong ShiftWordLanes(ulong value, ulong count, bool left, bool arithmetic)
    {
        ulong result = 0;
        for (int lane = 0; lane < 4; lane++)
            result |= (ulong)ShiftWord(GetWordLane(value, lane), count, left, arithmetic) << (lane * 16);
        return result;
    }

    private static ushort ShiftWord(ushort value, ulong count, bool left, bool arithmetic)
    {
        if (left) return count >= 16 ? (ushort)0 : (ushort)(value << (int)count);

        if (arithmetic)
        {
            short signed = (short)value;
            if (count >= 16) return signed < 0 ? (ushort)0xFFFF : (ushort)0;
            return (ushort)(signed >> (int)count);
        }

        return count >= 16 ? (ushort)0 : (ushort)(value >> (int)count);
    }

    private static ulong ShiftDwordLanes(ulong value, ulong count, bool left, bool arithmetic)
    {
        uint low = ShiftDword(GetDwordLane(value, 0), count, left, arithmetic);
        uint high = ShiftDword(GetDwordLane(value, 1), count, left, arithmetic);
        return low | ((ulong)high << 32);
    }

    private static uint ShiftDword(uint value, ulong count, bool left, bool arithmetic)
    {
        if (left) return count >= 32 ? 0u : value << (int)count;

        if (arithmetic)
        {
            int signed = (int)value;
            if (count >= 32) return signed < 0 ? 0xFFFFFFFFu : 0u;
            return (uint)(signed >> (int)count);
        }

        return count >= 32 ? 0u : value >> (int)count;
    }

    private static ulong ShiftQword(ulong value, ulong count, bool left)
        => count >= 64 ? 0ul : left ? value << (int)count : value >> (int)count;

    // --- packed lane helpers -----------------------------------------------------

    private static byte GetByteLane(ulong value, int lane) => (byte)(value >> (lane * 8));
    private static ushort GetWordLane(ulong value, int lane) => (ushort)(value >> (lane * 16));
    private static uint GetDwordLane(ulong value, int lane) => lane == 0 ? (uint)value : (uint)(value >> 32);

    private static ulong PerByte(ulong a, ulong b, Func<byte, byte, byte> op)
    {
        ulong result = 0;
        for (int lane = 0; lane < 8; lane++)
            result |= (ulong)op(GetByteLane(a, lane), GetByteLane(b, lane)) << (lane * 8);
        return result;
    }

    private static ulong PerWord(ulong a, ulong b, Func<ushort, ushort, ushort> op)
    {
        ulong result = 0;
        for (int lane = 0; lane < 4; lane++)
            result |= (ulong)op(GetWordLane(a, lane), GetWordLane(b, lane)) << (lane * 16);
        return result;
    }

    private static ulong PerDword(ulong a, ulong b, Func<uint, uint, uint> op)
        => op(GetDwordLane(a, 0), GetDwordLane(b, 0)) | ((ulong)op(GetDwordLane(a, 1), GetDwordLane(b, 1)) << 32);

    /// <summary>
    /// PACKSSWB/PACKUSWB: the destination's four words, saturated by
    /// <paramref name="saturate"/>, become the low four bytes; the source's
    /// four words become the high four, in the same low-to-high order.
    /// </summary>
    private static ulong PackWords(ulong destination, ulong source, Func<short, byte> saturate)
    {
        ulong result = 0;
        for (int i = 0; i < 4; i++)
            result |= (ulong)saturate((short)GetWordLane(destination, i)) << (i * 8);
        for (int i = 0; i < 4; i++)
            result |= (ulong)saturate((short)GetWordLane(source, i)) << ((i + 4) * 8);
        return result;
    }

    /// <summary>PACKSSDW: the destination's two dwords become the low two (signed-saturated) words, the source's the high two.</summary>
    private static ulong PackSignedDwordsToWords(ulong destination, ulong source)
    {
        ulong result = 0;
        result |= (ulong)SignedSaturateToWord((int)GetDwordLane(destination, 0)) << 0;
        result |= (ulong)SignedSaturateToWord((int)GetDwordLane(destination, 1)) << 16;
        result |= (ulong)SignedSaturateToWord((int)GetDwordLane(source, 0)) << 32;
        result |= (ulong)SignedSaturateToWord((int)GetDwordLane(source, 1)) << 48;
        return result;
    }

    /// <summary>PUNPCKLBW: interleaves the low four bytes of each operand, destination first in each pair.</summary>
    private static ulong UnpackLowBytes(ulong destination, ulong source)
    {
        ulong result = 0;
        for (int i = 0; i < 4; i++)
        {
            result |= (ulong)GetByteLane(destination, i) << (i * 16);
            result |= (ulong)GetByteLane(source, i) << (i * 16 + 8);
        }
        return result;
    }

    /// <summary>PUNPCKHBW: as <see cref="UnpackLowBytes"/>, but from the high four bytes of each operand.</summary>
    private static ulong UnpackHighBytes(ulong destination, ulong source)
    {
        ulong result = 0;
        for (int i = 0; i < 4; i++)
        {
            result |= (ulong)GetByteLane(destination, i + 4) << (i * 16);
            result |= (ulong)GetByteLane(source, i + 4) << (i * 16 + 8);
        }
        return result;
    }

    /// <summary>PUNPCKLWD: interleaves the low two words of each operand.</summary>
    private static ulong UnpackLowWords(ulong destination, ulong source)
    {
        ulong result = 0;
        for (int i = 0; i < 2; i++)
        {
            result |= (ulong)GetWordLane(destination, i) << (i * 32);
            result |= (ulong)GetWordLane(source, i) << (i * 32 + 16);
        }
        return result;
    }

    /// <summary>PUNPCKHWD: as <see cref="UnpackLowWords"/>, but from the high two words of each operand.</summary>
    private static ulong UnpackHighWords(ulong destination, ulong source)
    {
        ulong result = 0;
        for (int i = 0; i < 2; i++)
        {
            result |= (ulong)GetWordLane(destination, i + 2) << (i * 32);
            result |= (ulong)GetWordLane(source, i + 2) << (i * 32 + 16);
        }
        return result;
    }

    /// <summary>PUNPCKLDQ: the destination's low dword, then the source's low dword.</summary>
    private static ulong UnpackLowDwords(ulong destination, ulong source)
        => GetDwordLane(destination, 0) | ((ulong)GetDwordLane(source, 0) << 32);

    /// <summary>PUNPCKHDQ: the destination's high dword, then the source's high dword.</summary>
    private static ulong UnpackHighDwords(ulong destination, ulong source)
        => GetDwordLane(destination, 1) | ((ulong)GetDwordLane(source, 1) << 32);

    /// <summary>
    /// PMADDWD: each of the two result dwords is the sum of two signed
    /// word-by-word products, computed and summed at full 32-bit width with
    /// no saturation -- the SDM's own operation listing wraps rather than
    /// saturates on the one input that can overflow (every lane at
    /// -32768 x -32768, whose sum is exactly 0x80000000), and the plain
    /// <c>int</c> arithmetic below reproduces that wrap directly.
    /// </summary>
    private static ulong MultiplyAddWords(ulong destination, ulong source)
    {
        uint LowDwordOfPair(int pairIndex)
        {
            int a0 = (short)GetWordLane(destination, pairIndex * 2);
            int b0 = (short)GetWordLane(source, pairIndex * 2);
            int a1 = (short)GetWordLane(destination, pairIndex * 2 + 1);
            int b1 = (short)GetWordLane(source, pairIndex * 2 + 1);
            return (uint)(a0 * b0 + a1 * b1);
        }

        return LowDwordOfPair(0) | ((ulong)LowDwordOfPair(1) << 32);
    }

    /// <summary>
    /// PSADBW: the absolute difference of each of the eight unsigned-byte
    /// pairs, summed into a single 16-bit total in bits 15:0 -- unlike
    /// SSE2's 128-bit PSADBW, this 64-bit MMX-register form has only one
    /// lane, not two, so the whole quadword's difference collapses into one
    /// word and bits 63:16 of the result are zero.
    /// </summary>
    private static ulong SumOfAbsoluteDifferences(ulong destination, ulong source)
    {
        uint sum = 0;
        for (int lane = 0; lane < 8; lane++)
        {
            sum += (uint)Math.Abs(GetByteLane(destination, lane) - GetByteLane(source, lane));
        }

        return (ushort)sum;
    }

    // --- saturation ----------------------------------------------------------

    private static byte SignedSaturateToByte(int value) => (byte)Math.Clamp(value, sbyte.MinValue, sbyte.MaxValue);
    private static byte UnsignedSaturateToByte(int value) => (byte)Math.Clamp(value, 0, byte.MaxValue);
    private static ushort SignedSaturateToWord(int value) => (ushort)Math.Clamp(value, short.MinValue, short.MaxValue);
    private static ushort UnsignedSaturateToWord(int value) => (ushort)Math.Clamp(value, 0, ushort.MaxValue);

    // --- the 8-byte memory operand ---------------------------------------------
    //
    // Checked and translated as one unit, exactly as CMPXCHG8B is in
    // Cpu.Pentium.cs (whose physical-quadword helpers are reused directly
    // below): a segment-limit violation must fault before either half of the
    // access has touched memory, not after the first dword already has.

    private ulong ReadMemQword(SegReg segment, uint offset)
    {
        const uint operandSize = 8;
        CheckLimit(segment, offset, operandSize);
        if (State.AlignmentChecked) CheckAlignment(offset, operandSize);

        uint linear = LinearAddress(segment, offset);
        uint lastLinear = linear + operandSize - 1;
        uint firstPhysical = TranslateLinear(linear, false);
        uint lastPhysical = TranslateLinear(lastLinear, false);
        bool onePage = (linear & QuadwordPageMask) == (lastLinear & QuadwordPageMask);

        return onePage ? ReadPhysicalQword(firstPhysical) : ReadSplitQword(linear, firstPhysical, lastPhysical);
    }

    private void WriteMemQword(SegReg segment, uint offset, ulong value)
    {
        const uint operandSize = 8;
        CheckLimit(segment, offset, operandSize);
        if (State.AlignmentChecked) CheckAlignment(offset, operandSize);

        uint linear = LinearAddress(segment, offset);
        uint lastLinear = linear + operandSize - 1;
        uint firstPhysical = TranslateLinear(linear, true);
        uint lastPhysical = TranslateLinear(lastLinear, true);
        bool onePage = (linear & QuadwordPageMask) == (lastLinear & QuadwordPageMask);

        if (onePage) WritePhysicalQword(firstPhysical, value);
        else WriteSplitQword(linear, firstPhysical, lastPhysical, value);
    }
}
