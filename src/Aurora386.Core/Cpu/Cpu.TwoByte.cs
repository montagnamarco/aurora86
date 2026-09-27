// The 0F-prefixed opcodes: the 386's conditional sets and jumps, bit
// operations, the system instructions (LGDT, LIDT, LMSW, the control and
// debug registers), the 486's BSWAP, CMPXCHG, XADD, INVLPG and CPUID, and
// one opcode that is not an instruction at all -- 0F FF, the hook through
// which the BIOS written in C# is reached, recognised only inside the ROM.

namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>
    /// The two-byte opcodes, introduced by a leading 0x0F. These are the
    /// instructions added by the 286 and the 386: long conditional jumps,
    /// SETcc, double-precision shifts, bit manipulation and access to the
    /// control registers.
    /// </summary>
    private void ExecuteTwoByte(byte opcode)
    {
        switch (opcode)
        {
            // --- conditional jumps with a long displacement (0x80-0x8F) -----
            case >= 0x80 and <= 0x8F:
            {
                int offset = FetchDisplacement();
                if (TestCondition((byte)(opcode & 0x0F)))
                    JumpRelative(offset);
                return;
            }

            // --- SETcc (0x90-0x9F): writes 1 or 0 into a byte ---------------
            case >= 0x90 and <= 0x9F:
            {
                var m = DecodeModRm();
                WriteRm8(m, TestCondition((byte)(opcode & 0x0F)) ? (byte)1 : (byte)0);
                return;
            }

            // --- CMOVcc (0x40-0x4F): Cpu.P6.cs -------------------------------
            case >= 0x40 and <= 0x4F:
                ExecuteCmovcc((byte)(opcode & 0x0F));
                return;

            // --- PUSH/POP of FS and GS, both new in the 386 -----------------
            case 0xA0: PushOperand(State.Fs.Selector); return;
            case 0xA1: PopSegment(SegReg.Fs); return;
            case 0xA8: PushOperand(State.Gs.Selector); return;
            case 0xA9: PopSegment(SegReg.Gs); return;

            // --- zero and sign extension (0xB6/0xB7/0xBE/0xBF) --------------
            // These are what make it cheap to work with bytes and words in
            // 32-bit registers; DOS's 16-bit code never uses them, but 32-bit
            // compilers emit them constantly, since without them widening a
            // value takes a mask or a pair of shifts.
            case 0xB6: { var m = DecodeModRm(); WriteRegOperand(m, ReadRm8(m)); return; }
            case 0xB7: { var m = DecodeModRm(); WriteRegOperand(m, ReadRm16(m)); return; }
            case 0xBE: { var m = DecodeModRm(); WriteRegOperand(m, (uint)(sbyte)ReadRm8(m)); return; }
            case 0xBF: { var m = DecodeModRm(); WriteRegOperand(m, (uint)(short)ReadRm16(m)); return; }

            // --- double-precision shifts (386) ------------------------------
            case 0xA4: { var m = DecodeModRm(); ShiftDouble(m, ReadRegOperand(m), FetchByte(), left: true); return; }
            case 0xA5: { var m = DecodeModRm(); ShiftDouble(m, ReadRegOperand(m), State.GetReg8(1), left: true); return; }
            case 0xAC: { var m = DecodeModRm(); ShiftDouble(m, ReadRegOperand(m), FetchByte(), left: false); return; }
            case 0xAD: { var m = DecodeModRm(); ShiftDouble(m, ReadRegOperand(m), State.GetReg8(1), left: false); return; }

            // --- BSF/BSR: find the first bit that is set --------------------
            case 0xBC: BitScan(forward: true); return;
            case 0xBD: BitScan(forward: false); return;

            // --- group 0F 01: descriptor tables and the machine status word --
            // Needed by anything that prepares the switch into protected mode.
            // HIMEM.SYS already uses it while still in real mode: without this
            // group, loading DOS stops dead.
            // The descriptor-table group. LLDT and LTR install the local
            // descriptor table and the task state segment, which is how an
            // operating system arranges for privilege changes to have
            // somewhere to go: without a task state segment the processor has
            // no kernel stack to switch to.
            case 0x00:
            {
                var m = DecodeModRm();
                switch (m.Reg)
                {
                    case 0:   // SLDT: read back the local descriptor table selector
                        if (m.IsRegister) State.SetReg32(m.Register, State.Ldtr);
                        else WriteMem16(m.Segment, m.Offset, State.Ldtr);
                        return;

                    case 1:   // STR: read back the task register
                        if (m.IsRegister) State.SetReg32(m.Register, State.TaskRegister);
                        else WriteMem16(m.Segment, m.Offset, State.TaskRegister);
                        return;

                    case 2:   // LLDT
                        RequireProtectedMode();
                        RequireSupervisor();
                        LoadLocalDescriptorTable(m.IsRegister
                            ? (ushort)State.GetReg32(m.Register)
                            : ReadMem16(m.Segment, m.Offset));
                        return;

                    case 3:   // LTR
                        RequireProtectedMode();
                        RequireSupervisor();
                        LoadTaskRegister(m.IsRegister
                            ? (ushort)State.GetReg32(m.Register)
                            : ReadMem16(m.Segment, m.Offset));
                        return;

                    // VERR and VERW ask whether a segment could be read or
                    // written from here, without the trouble of loading it
                    // and catching the fault. The answer goes in the zero
                    // flag.
                    case 4:
                    case 5:
                    {
                        RequireProtectedMode();
                        ushort selector = m.IsRegister
                            ? (ushort)State.GetReg32(m.Register)
                            : ReadMem16(m.Segment, m.Offset);
                        Flags.Zero = CanAccessSegment(selector, forWrite: m.Reg == 5);
                        return;
                    }

                    default:
                        throw new CpuException(6);
                }
            }

            case 0x01:
            {
                var m = DecodeModRm();
                switch (m.Reg)
                {
                    case 0:   // SGDT: stores limit and base to memory
                        if (m.IsRegister) throw new CpuException(6);
                        WriteMem16(m.Segment, m.Offset, State.GdtLimit);
                        WriteMem32(m.Segment, m.Offset + 2, State.GdtBase);
                        return;

                    case 1:   // SIDT
                        if (m.IsRegister) throw new CpuException(6);
                        WriteMem16(m.Segment, m.Offset, State.IdtLimit);
                        WriteMem32(m.Segment, m.Offset + 2, State.IdtBase);
                        return;

                    case 2:   // LGDT
                        RequireSupervisor();
                    {
                        if (m.IsRegister) throw new CpuException(6);
                        State.GdtLimit = ReadMem16(m.Segment, m.Offset);
                        uint baseValue = ReadMem32(m.Segment, m.Offset + 2);
                        // With 16-bit operands the top byte of the base is
                        // discarded: the 286-compatible form assumes the table
                        // lives inside the first 16 MB.
                        State.GdtBase = OperandSize32 ? baseValue : baseValue & 0x00FFFFFF;
                        return;
                    }

                    case 3:   // LIDT
                        RequireSupervisor();
                    {
                        if (m.IsRegister) throw new CpuException(6);
                        State.IdtLimit = ReadMem16(m.Segment, m.Offset);
                        uint baseValue = ReadMem32(m.Segment, m.Offset + 2);
                        State.IdtBase = OperandSize32 ? baseValue : baseValue & 0x00FFFFFF;
                        return;
                    }

                    case 4:   // SMSW: the low sixteen bits of CR0
                        if (m.IsRegister) State.SetReg16(m.Register, (ushort)State.Cr0);
                        else WriteMem16(m.Segment, m.Offset, (ushort)State.Cr0);
                        return;

                    case 6:   // LMSW: writes the low CR0 bits, but PE cannot be cleared
                        RequireSupervisor();
                    {
                        ushort valore = ReadRm16(m);
                        uint nuovo = (State.Cr0 & 0xFFFFFFF0) | (uint)(valore & 0x0F);
                        // Once the protection enable bit is on, LMSW cannot
                        // turn it back off: leaving protected mode requires a
                        // reset (or, on later chips, clearing PE through CR0).
                        // The one-way door is deliberate -- it stops ring 3
                        // code from demoting the machine out from under the
                        // operating system.
                        if ((State.Cr0 & CpuState.Cr0ProtectionEnable) != 0)
                            nuovo |= CpuState.Cr0ProtectionEnable;
                        WriteCr0(nuovo);
                        return;
                    }

                    case 7:   // INVLPG: drops one page's cached translation
                    {
                        RequireFeature(CpuFeature.Isa486);
                        RequireSupervisor();

                        // The operand is an address, not a value read from
                        // it: the instruction names a page rather than
                        // touching one, so it must not fault on a page that
                        // is absent -- which is very often exactly the page
                        // being invalidated.
                        if (m.IsRegister) throw new CpuException(6);
                        InvalidatePage(LinearAddress(m.Segment, m.Offset));
                        return;
                    }

                    default:
                        throw new CpuException(6);
                }
            }

            // LAR and LSL ask about a descriptor without loading it: LAR hands
            // back its access rights, LSL the limit it describes. Windows uses
            // them constantly to vet a selector handed in from elsewhere before
            // trusting it. Both report success in the zero flag and leave the
            // destination untouched when the selector is not readable, so a
            // failed query has to write nothing at all.
            case 0x02:
            case 0x03:
            {
                RequireProtectedMode();
                var m = DecodeModRm();
                ushort selector = m.IsRegister
                    ? (ushort)State.GetReg32(m.Register)
                    : ReadMem16(m.Segment, m.Offset);

                if (!DescriptorVisible(selector, out ulong descriptor))
                {
                    Flags.Zero = false;
                    return;
                }

                if (opcode == 0x02)
                {
                    // The access-rights doubleword is the descriptor's second
                    // word with the low byte cleared -- the byte that holds the
                    // limit, which is no business of LAR.
                    // Bits 8..23 of the descriptor's upper doubleword: the
                    // access byte and, above it, the four flag bits together
                    // with the top nibble of the limit. Keeping only the flags
                    // and discarding that nibble -- masking F0 where FF
                    // belongs -- loses part of what the instruction reports.
                    uint rights = (uint)(descriptor >> 32) & 0x00FFFF00;

                    // The whole 32-bit register is written whatever the
                    // operand size says. This is peculiar to LAR and LSL: the
                    // access-rights and limit values they produce are 32-bit
                    // quantities, and the processor stores all of it rather
                    // than leaving the top half of the destination holding
                    // whatever it held before. Masking to 16 bits here leaves
                    // stale bits above the answer, which reads as a plausible
                    // but wrong set of rights.
                    State.SetReg32(m.Reg, rights);
                }
                else
                {
                    uint limit = (uint)(descriptor & 0xFFFF)
                               | ((uint)(descriptor >> 32) & 0x000F0000);

                    // Granularity turns the limit into a count of 4K pages, and
                    // the last page is included whole.
                    if ((descriptor & (1UL << 55)) != 0) limit = (limit << 12) | 0xFFF;

                    // As above: the full register, regardless of operand size.
                    State.SetReg32(m.Reg, limit);
                }

                Flags.Zero = true;
                return;
            }

            // --- CLTS: clears the "task switched" bit of CR0 ----------------
            case 0x06: RequireSupervisor(); State.Cr0 &= ~CpuState.Cr0TaskSwitched; return;

            // --- UD2: guaranteed-undefined since the Pentium Pro ------------
            // Recognised explicitly, like RSM further down, so a deliberate
            // #UD shows in the trace as "UD2" rather than an unknown opcode.
            // Unconditional on every model: the byte was already undefined
            // before Intel gave it a name (plans/specs/p6.md item 3).
            case 0x0B: throw new CpuException(6);

            // --- P6 hint NOPs (plans/specs/p6.md item 3, coordinator's own
            // decision) -----------------------------------------------------
            // 0F 18 /0-7 is the multi-byte PREFETCHh space: a memory operand
            // is a NOP, mod=11 (nothing to prefetch) is #UD. 0F 19-0F 1F are
            // NOP unconditionally, register or memory alike -- the reserved-
            // NOP-space Intel documents from the Pentium Pro onward. Neither
            // is a CPUID bit (CpuFeature.HintNops), so earlier silicon simply
            // does not have this: the feature check alone gives #UD there.
            case 0x18:
            {
                RequireFeature(CpuFeature.HintNops);
                var m = DecodeModRm();
                if (m.IsRegister) throw new CpuException(6);
                return;
            }

            case 0x19: case 0x1A: case 0x1B: case 0x1C:
            case 0x1D: case 0x1E: case 0x1F:
                RequireFeature(CpuFeature.HintNops);
                DecodeModRm();
                return;

            // --- the Pentium's own instructions (Cpu.Pentium.cs) -------------
            case 0x30: ExecuteWrmsr(); return;
            case 0x31: ExecuteRdtsc(); return;
            case 0x32: ExecuteRdmsr(); return;

            // --- the Pentium Pro/Pentium II's own instructions (Cpu.P6.cs) ---
            case 0x33: ExecuteRdpmc(); return;
            case 0x34: ExecuteSysenter(); return;
            case 0x35: ExecuteSysexit(); return;

            // RSM: returns from system-management mode. There is no SMM here
            // -- no SMRAM, no SMI line -- so this is exactly the #UD a real
            // processor without SMM gives; handled explicitly rather than
            // falling through to the default case so the intent (a
            // recognised but absent instruction, not a decode failure) is on
            // the record.
            case 0xAA: throw new CpuException(6);

            case 0xC7:
            {
                var m = DecodeModRm();
                // Only /1 (CMPXCHG8B) exists at this opcode on the models
                // Aurora emulates; every other reg field -- including the
                // VMPTRLD/VMPTRST encodings later chips added, which need
                // VMX this emulator does not have -- is undefined here.
                if (m.Reg != 1) throw new CpuException(6);
                RequireFeature(CpuFeature.Cx8);
                ExecuteCmpXchg8B(m);
                return;
            }

            // --- FXSAVE / FXRSTOR (Cpu.P6.cs), LDMXCSR / STMXCSR / SFENCE
            // (Cpu.Sse.cs), LFENCE / MFENCE / CLFLUSH (Cpu.Sse2.cs) -----------
            // Group 15. The ModRM byte is decoded once, here, and each reg
            // field checks its own gating feature: /0 and /1 need CPUID.FXSR,
            // /2/3 need CPUID.SSE (LDMXCSR/STMXCSR), /5 and /6 need CPUID.SSE2
            // (LFENCE/MFENCE, mod=11 only -- coordinator decision (d),
            // plans/specs/sse2.md, "any rm value accepted"), and /7 splits on
            // the operand's own shape: mod=11 is SFENCE (CPUID.SSE), a memory
            // operand is CLFLUSH (CPUID.CLFSH, work package P4.2). Every other
            // reg field is undefined.
            case 0xAE:
            {
                var m = DecodeModRm();
                switch (m.Reg)
                {
                    case 0: RequireFeature(CpuFeature.Fxsr); ExecuteFxsave(m); return;
                    case 1: RequireFeature(CpuFeature.Fxsr); ExecuteFxrstor(m); return;
                    case 2: ExecuteLdmxcsr(m); return;
                    case 3: ExecuteStmxcsr(m); return;
                    case 5: ExecuteLfence(m); return;
                    case 6: ExecuteMfence(m); return;
                    case 7:
                        if (m.IsRegister) ExecuteSfence(m);
                        else ExecuteClflush(m);
                        return;
                    default: throw new CpuException(6);
                }
            }

            // --- MOVNTI (Cpu.Sse2.cs) ----------------------------------------
            case 0xC3:
            {
                var m = DecodeModRm();
                ExecuteMovnti(m);
                return;
            }

            // --- MMX (Pentium MMX), the SSE-gated MMX-register instructions
            // (Katmai New Instructions) and SSE2's own 128-bit widened integer
            // set (Northwood): Cpu.Mmx.cs / Cpu.Sse2.Integer.cs --------------
            // Every opcode below is dispatched to ExecuteMmx, which decodes
            // its own ModRM/immediate bytes -- the feature check (Mmx, Sse or
            // Sse2 depending on the opcode -- plans/specs/sse.md §2,
            // plans/specs/sse2.md §1/§4), the CR0.EM/TS checks and the
            // 66/F2/F3-prefix routing (to Cpu.Sse2.Integer.cs's own
            // ExecuteSse2Integer, on a Pentium 4 or later) all live there,
            // ahead of any operand decoding, exactly like the 486 opcodes
            // above check RequireFeature before touching ModRM. 0x6C, 0x6D
            // and 0xD6 have no meaning at all without a prefix (SSE2's own
            // PUNPCKLQDQ/HQDQ and MOVQ/MOVDQ2Q/MOVQ2DQ); ExecuteMmx's own
            // HasNoUnprefixedForm rejects the bare byte outright.
            case 0x60: case 0x61: case 0x62: case 0x63:
            case 0x64: case 0x65: case 0x66: case 0x67:
            case 0x68: case 0x69: case 0x6A: case 0x6B:
            case 0x6C: case 0x6D:
            case 0x6E: case 0x6F: case 0x70:
            case 0x71: case 0x72: case 0x73:
            case 0x74: case 0x75: case 0x76: case 0x77:
            case 0x7E: case 0x7F:
            case 0xC4: case 0xC5:
            case 0xD1: case 0xD2: case 0xD3: case 0xD4: case 0xD5: case 0xD6: case 0xD7:
            case 0xD8: case 0xD9: case 0xDA: case 0xDB: case 0xDC: case 0xDD: case 0xDE: case 0xDF:
            case 0xE0: case 0xE1: case 0xE2: case 0xE3: case 0xE4: case 0xE5:
            case 0xE7: case 0xE8: case 0xE9:
            case 0xEA: case 0xEB: case 0xEC: case 0xED: case 0xEE: case 0xEF:
            case 0xF1: case 0xF2: case 0xF3: case 0xF4: case 0xF5: case 0xF6: case 0xF7:
            case 0xF8: case 0xF9: case 0xFA: case 0xFB:
            case 0xFC: case 0xFD: case 0xFE:
                ExecuteMmx(opcode);
                return;

            // --- SSE (Pentium III) and SSE2's packed/scalar double
            // precision (Northwood): Cpu.Sse.cs / Cpu.Sse.Arithmetic.cs /
            // Cpu.Sse2.cs / Cpu.Sse2.Arithmetic.cs -----------------------------
            // Every opcode below is dispatched to ExecuteSse, which checks
            // CPUID.SSE/CR0.EM/CR4.OSFXSR (#UD), CR0.TS (#NM), and either
            // proceeds with the PS/SS logic (no prefix or F3) or -- with a 66
            // or F2 prefix, on a model with CPUID.SSE2 -- hands the same
            // opcode byte to Cpu.Sse2.cs's own ExecuteSse2Float for its PD/SD
            // meaning. 0x5A, 0x5B and 0xE6 have no PS/SS meaning to share an
            // opcode byte with at all (every one of their four prefix
            // variants is SSE2-only), so they are dispatched directly to
            // Cpu.Sse2.Arithmetic.cs's own ExecuteSse2Conversions instead.
            case 0x10: case 0x11: case 0x12: case 0x13:
            case 0x14: case 0x15: case 0x16: case 0x17:
            case 0x28: case 0x29: case 0x2A: case 0x2B: case 0x2C: case 0x2D: case 0x2E: case 0x2F:
            case 0x50: case 0x51: case 0x52: case 0x53: case 0x54: case 0x55: case 0x56: case 0x57:
            case 0x58: case 0x59: case 0x5C: case 0x5D: case 0x5E: case 0x5F:
            case 0xC2: case 0xC6:
                ExecuteSse(opcode);
                return;

            case 0x5A: case 0x5B: case 0xE6:
                ExecuteSse2Conversions(opcode);
                return;

            // SSE3's horizontal arithmetic, alternating add/subtract and
            // unaligned load occupy previously unused two-byte opcodes.
            case 0x7C: case 0x7D: case 0xD0: case 0xF0:
                ExecuteSse3(opcode);
                return;

            // --- bit instructions -------------------------------------------
            // In the /r form the bit index is a signed register value, and on a
            // memory operand it may point well outside the addressed word: the
            // 386 means it to index long bit vectors, so the effective address
            // has to be displaced by index/word-size.
            case 0xA3: BitOperation(BitOp.Test, register: true); return;
            case 0xAB: BitOperation(BitOp.Set, register: true); return;
            case 0xB3: BitOperation(BitOp.Reset, register: true); return;
            case 0xBB: BitOperation(BitOp.Complement, register: true); return;

            // Group 0F BA: the same four operations with an immediate index.
            // Here the index is unsigned and is reduced modulo the operand
            // size, so it can never escape the operand itself.
            case 0xBA:
            {
                var m = DecodeModRm();
                if (m.Reg < 4) throw new CpuException(6);
                BitOperationImmediate(m, (BitOp)(m.Reg - 4));
                return;
            }

            // --- two-operand IMUL: reg = reg * r/m (386) --------------------
            // Same logic as the immediate form; the second factor is simply the
            // destination register instead of a constant.
            case 0xAF:
            {
                var m = DecodeModRm();
                ImulImmediate(m, ReadRegOperand(m));
                return;
            }

            // --- the 486's new instructions ---------------------------------
            //
            // Three instructions arrived with the 486, and all three exist for
            // the same reason: a multiprocessor system needs to change memory
            // and find out what was there in one indivisible step. On a 386 a
            // program had to bracket such a sequence with a lock prefix and
            // hope; from the 486 the hardware offers the whole operation.

            // BSWAP: reverses the four bytes of a 32-bit register.
            //
            // Nothing to do with atomicity: it turns a little-endian value
            // into a big-endian one and back, which every network stack needs
            // for every address and every port number it touches. Before it,
            // that was three instructions.
            case >= 0xC8 and <= 0xCF:
            {
                RequireFeature(CpuFeature.Isa486);
                int register = opcode & 7;

                // Only defined for a 32-bit operand. With a 16-bit one Intel
                // documents the result as undefined, and real 486s leave the
                // register zeroed; anything that relies on it is broken
                // already, so the simplest faithful answer is that zero.
                if (!OperandSize32) { State.SetReg16(register, 0); return; }

                uint value = State.Gpr[register];
                State.Gpr[register] = (value >> 24)
                                    | ((value >> 8) & 0x0000FF00)
                                    | ((value << 8) & 0x00FF0000)
                                    | (value << 24);
                return;
            }

            // XADD: exchange and add. The destination gets the sum, the source
            // register gets the destination's old value, and the flags come
            // from the addition. One instruction where a lock-prefixed
            // sequence used to be needed, which is why every spinlock written
            // after 1989 uses it.
            case 0xC0:
            {
                RequireFeature(CpuFeature.Isa486);
                var m = DecodeModRm();
                byte destination = ReadRm8(m);
                byte source = State.GetReg8(m.Reg);
                byte sum = (byte)(destination + source);

                State.SetReg8(m.Reg, destination);
                WriteRm8(m, sum);
                Flags.Set(FlagOp.Add, destination, source, sum, 8);
                return;
            }
            case 0xC1:
            {
                RequireFeature(CpuFeature.Isa486);
                var m = DecodeModRm();
                uint destination = ReadRm(m);
                uint source = ReadRegOperand(m);
                uint sum = destination + source;

                WriteRegOperand(m, destination);
                WriteRm(m, sum);
                Flags.Set(FlagOp.Add, destination, source, sum, OperandBits);
                return;
            }

            // CMPXCHG: compare the accumulator with the destination and, only
            // if they match, store the source there. Either way the flags come
            // from the comparison, and on a mismatch the accumulator is loaded
            // with what was actually found -- so a caller that loses the race
            // already holds the value it needs for the next attempt. This is
            // the instruction every lock-free algorithm is built on.
            case 0xB0:
            {
                RequireFeature(CpuFeature.Isa486);
                var m = DecodeModRm();
                byte destination = ReadRm8(m);
                byte accumulator = State.GetReg8(0);
                byte difference = (byte)(accumulator - destination);
                Flags.Set(FlagOp.Sub, accumulator, destination, difference, 8);

                if (accumulator == destination) WriteRm8(m, State.GetReg8(m.Reg));
                else State.SetReg8(0, destination);
                return;
            }
            case 0xB1:
            {
                RequireFeature(CpuFeature.Isa486);
                var m = DecodeModRm();
                uint destination = ReadRm(m);
                uint accumulator = OperandSize32 ? State.Eax : State.Ax;
                uint difference = accumulator - destination;
                Flags.Set(FlagOp.Sub, accumulator, destination, difference, OperandBits);

                if (accumulator == destination)
                {
                    WriteRm(m, ReadRegOperand(m));
                }
                else
                {
                    if (OperandSize32) State.Eax = destination;
                    else State.Ax = (ushort)destination;
                }
                return;
            }

            // CPUID: the processor says what it is. See Cpu.Identification.cs
            // for what each function returns and CpuModelCatalog for the
            // per-model signatures, brand strings and feature bits it reads
            // from. Gated on Identification alone, not a model check: a
            // processor whose silicon lacks CPUID (every 386, and any 486
            // model built before the instruction existed) has nothing behind
            // this opcode at all, which is exactly what Identification means.
            case 0xA2:
                RequireFeature(CpuFeature.Identification);
                ExecuteCpuId();
                return;

            // INVD and WBINVD: throw the caches away, with and without writing
            // dirty lines back first. This processor has no cache to speak of,
            // so both are checked for privilege and then do nothing -- which
            // is exactly what they do on a 486 with caching disabled.
            case 0x08:
            case 0x09:
                RequireFeature(CpuFeature.Isa486);
                RequireSupervisor();
                return;

            // --- far pointer loads into the 386's new segment registers -----
            case 0xB2: LoadFarPointer(SegReg.Ss); return;
            case 0xB4: LoadFarPointer(SegReg.Fs); return;
            case 0xB5: LoadFarPointer(SegReg.Gs); return;

            // --- control register access ------------------------------------
            // CR4 (reg field 4) exists only from the Pentium onward: naming
            // it on a 386/486 is exactly as undefined as naming the
            // perpetually reserved CR1/CR5/CR6/CR7, which is why the feature
            // check happens before the privilege one -- decode-time faults
            // outrank execute-time ones (SDM Vol.3 Table 6-1's exception
            // priority), and "this control register does not exist on this
            // processor" is a decode-time question.
            case 0x20:
            {
                var m = DecodeModRm();
                if (m.Reg == 4) RequireFeature(CpuFeature.ControlRegister4);
                RequireSupervisor();
                State.SetReg32(m.Register, m.Reg switch
                {
                    0 => State.Cr0,
                    2 => State.Cr2,
                    3 => State.Cr3,
                    4 => State.Cr4,
                    _ => 0,
                });
                return;
            }
            case 0x22:
            {
                var m = DecodeModRm();
                if (m.Reg == 4) RequireFeature(CpuFeature.ControlRegister4);
                RequireSupervisor();
                uint value = State.GetReg32(m.Register);
                switch (m.Reg)
                {
                    case 0: WriteCr0(value); break;
                    case 2: State.Cr2 = value; break;
                    case 3:
                        // Loading CR3 points the processor at a different set
                        // of page tables, so every cached translation is now
                        // about the wrong address space. With PAE paging
                        // active -- CR4.PAE and CR0.PG both set (SDM Vol.3A
                        // §4.4.1's own condition; coordinator decision D1) --
                        // it also reloads the four PDPT registers from the
                        // new tables: a reserved bit in a present one is
                        // #GP(0) right here, before CR3 changes, not a page
                        // fault deferred to first use. With CR0.PG clear, CR3
                        // is simply stored -- there is no paging, PAE or
                        // otherwise, for it to be part of yet.
                        if ((State.Cr4 & CpuState.Cr4Pae) != 0 && (State.Cr0 & CpuState.Cr0Paging) != 0)
                            LoadPdptRegisters(value);

                        State.Cr3 = value;
                        FlushTlbForCr3Load();
                        break;
                    case 4: WriteCr4(value); break;
                }
                return;
            }

            // --- debug register access --------------------------------------
            // There is no breakpoint hardware behind these, but the values
            // still have to be kept: whoever writes them expects to read the
            // same values back, and debuggers probe exactly that way. (This
            // emulator has no data/I/O breakpoint hardware at all -- DR0-DR3
            // and the length/type fields of DR7 are storage only, on every
            // model -- so CR4.DE changes nothing about what a breakpoint
            // does here, only whether DR4/DR5 may be named.)
            //
            // On 386/486 DR4 and DR5 always alias DR6 and DR7: there is no
            // CR4 on those models to say otherwise. From the Pentium, CR4.DE
            // (Debug Extensions) turns that alias off -- naming DR4 or DR5
            // with DE set raises #UD. That check depends on CR4 at run time,
            // so it comes after the privilege check, as in Bochs and QEMU:
            // ring-3 and V86 code get #GP(0) whatever DE says, which is what
            // a V86 monitor emulating MOV DRx from its #GP handler expects.
            case 0x21:
            {
                var m = DecodeModRm();
                RequireSupervisor();
                RequireDebugRegister(m.Reg);
                State.SetReg32(m.Register, State.Dr[m.Reg is 4 or 5 ? m.Reg + 2 : m.Reg]);
                return;
            }
            case 0x23:
            {
                var m = DecodeModRm();
                RequireSupervisor();
                RequireDebugRegister(m.Reg);
                State.Dr[m.Reg is 4 or 5 ? m.Reg + 2 : m.Reg] = State.GetReg32(m.Register);
                return;
            }

            // --- the test registers, TR6 and TR7 -----------------------------
            // These drive the 386's TLB test hardware, and only TR6 and TR7
            // exist. Nothing in DOS or Windows uses them for their real purpose,
            // but memory sizers and CPU probes do read them to tell a 386 from
            // a 486, so they have to exist and hold what was written rather than
            // fault. The TLB here is a plain cache with no test path, so the
            // registers are storage and nothing more. The Pentium removed them:
            // there both encodings are #UD (see CpuFeature.TestRegisters).
            case 0x24:
            {
                RequireFeature(CpuFeature.TestRegisters);
                RequireSupervisor();
                var m = DecodeModRm();
                State.SetReg32(m.Register, m.Reg >= 6 ? State.Tr[m.Reg - 6] : 0);
                return;
            }

            case 0x26:
            {
                RequireFeature(CpuFeature.TestRegisters);
                RequireSupervisor();
                var m = DecodeModRm();
                if (m.Reg >= 6) State.Tr[m.Reg - 6] = State.GetReg32(m.Register);
                return;
            }

            // --- hook for the native handlers --------------------------------
            // 0x0F 0xFF is not a 386 opcode at all: it is used here as the
            // entry point for the BIOS functions implemented in C#. The byte
            // that follows says which handler to call. It is the same trick
            // DOSBox uses for its callbacks -- far faster to run, and far
            // easier to fix, than writing the BIOS in assembler.
            case 0xFF:
            {
                // Only code in the BIOS ROM may use the hook. Anywhere else
                // 0F FF is what it is on the real processor, an undefined
                // opcode -- and software relies on that: Windows' kernel
                // executes it on purpose to provoke #UD and get a look at the
                // exception frame its DPMI host builds. Treating it as a hook
                // there swallowed the byte after it as a handler number and
                // ran on with the stack two bytes short of what the code
                // expected.
                uint physical = TranslateLinear(State.Cs.Base + _instructionStart, false);
                bool inRom = physical is >= 0xF0000 and < 0x100000 || physical >= 0xFFFF0000;
                if (!inRom || NativeHandler is null) throw new CpuException(6);
                byte handlerId = FetchByte();
                NativeHandler(this, handlerId);
                return;
            }

            default:
                // An unrecognised two-byte opcode. It is reported separately
                // from the one-byte ones so the two cannot be confused when
                // reading the log.
                UnknownTwoByteOpcode?.Invoke(opcode, _instructionStart, State.Cs.Selector);
                throw new CpuException(6);
        }
    }

    /// <summary>Raised for unrecognised 0x0F opcodes.</summary>
    public event Action<byte, uint, ushort>? UnknownTwoByteOpcode;

    /// <summary>
    /// The native call handler, installed by the emulated machine. It receives
    /// the CPU and the identifier encoded after the opcode.
    /// </summary>
    public Action<Cpu, byte>? NativeHandler { get; set; }

    /// <summary>
    /// BSF/BSR: find the position of the first set bit, scanning from the
    /// bottom or from the top. With a zero operand the result is undefined on
    /// real silicon and ZF is set instead; the destination register is left
    /// unchanged, which is the behaviour software actually relies on.
    /// </summary>
    private void BitScan(bool forward)
    {
        var m = DecodeModRm();
        uint value = ReadRm(m);

        if (value == 0)
        {
            Flags.Zero = true;
            return;
        }

        int position = forward
            ? System.Numerics.BitOperations.TrailingZeroCount(value)
            : 31 - System.Numerics.BitOperations.LeadingZeroCount(value);

        WriteRegOperand(m, (uint)position);
        Flags.Zero = false;
    }

    /// <summary>The four operations of the bit group, in reg-field order.</summary>
    private enum BitOp { Test = 0, Set = 1, Reset = 2, Complement = 3 }

    /// <summary>
    /// BT/BTS/BTR/BTC with the index in a register. On a memory operand the
    /// index is signed and is not clamped to the operand size: the effective
    /// address slides by index/16 words (or /32 dwords), so that a long bit
    /// vector can be addressed with a single instruction and no pointer
    /// arithmetic of its own.
    /// </summary>
    private void BitOperation(BitOp operation, bool register)
    {
        var m = DecodeModRm();
        int width = OperandSize32 ? 32 : 16;
        // The index is signed, so at 16 bits it has to be sign-extended:
        // otherwise a value such as 0xC386 would be read as +50054 instead of
        // -15482 and the effective address would slide the wrong way entirely.
        int index = OperandSize32 ? (int)ReadRegOperand(m)
                                  : (short)ReadRegOperand(m);

        if (m.IsRegister)
        {
            // On a register there is no address to displace: the index is
            // simply reduced modulo the operand width.
            uint value = ReadRm(m);
            int bit = index & (width - 1);
            ApplyBit(m, operation, value, bit);
            return;
        }

        // The division has to round towards negative infinity, not towards
        // zero: with a negative index the address must move downwards. The
        // arithmetic is done in long because with an index of 0x80000000 the
        // subtraction would otherwise overflow.
        long indexAsLong = index;
        long word = indexAsLong >= 0 ? indexAsLong / width
                                     : (indexAsLong - width + 1) / width;
        int bitInWord = (int)(indexAsLong - word * width);

        uint offset = (uint)(m.Offset + word * (width / 8));
        // The address wraps within 16 bits only when the address calculation
        // itself is 16-bit: with the 0x67 prefix it stays 32-bit.
        if (!AddressSize32) offset &= 0xFFFF;

        ModRm target = m;
        target.Offset = offset;
        ApplyBit(target, operation, ReadRm(target), bitInWord);
    }

    /// <summary>Group 0F BA: the immediate index can never escape the operand.</summary>
    private void BitOperationImmediate(in ModRm m, BitOp operation)
    {
        int width = OperandSize32 ? 32 : 16;
        int bit = FetchByte() & (width - 1);
        ApplyBit(m, operation, ReadRm(m), bit);
    }

    /// <summary>
    /// Copies the selected bit into CF and, for everything except BT, writes
    /// the operand back. The other arithmetic flags keep whatever they held.
    /// </summary>
    private void ApplyBit(in ModRm m, BitOp operation, uint value, int bit)
    {
        uint mask = 1u << bit;
        Flags.Carry = (value & mask) != 0;

        uint updated = operation switch
        {
            BitOp.Set => value | mask,
            BitOp.Reset => value & ~mask,
            BitOp.Complement => value ^ mask,
            _ => value,
        };

        if (operation != BitOp.Test) WriteRm(m, updated);
    }

    /// <summary>
    /// Writes CR0. This is the register that turns on protection and paging:
    /// taking PE from 0 to 1 *is* the switch into protected mode, the one
    /// Windows 3.1 performs as it starts up.
    /// </summary>
    /// <summary>
    /// These instructions exist only under protection: in real mode there are
    /// no descriptors for them to name, and the processor rejects them.
    /// </summary>
    private void RequireProtectedMode()
    {
        if (!State.ProtectedMode) throw new CpuException(6);
    }

    /// <summary>
    /// Whether a segment could be read from, or written to, at the current
    /// privilege level -- the question VERR and VERW answer.
    ///
    /// The point of asking is to avoid the fault: a kernel handed a selector
    /// by a program has to know whether it can use it before it tries, since
    /// finding out the hard way would mean taking a fault in the middle of
    /// its own code.
    /// </summary>
    private bool CanAccessSegment(ushort selector, bool forWrite)
    {
        if ((selector & 0xFFFC) == 0) return false;

        ulong descriptor;
        try { descriptor = ReadDescriptor(selector); }
        catch (CpuException) { return false; }

        byte access = (byte)(descriptor >> 40);

        // Bit 4 clear marks a system descriptor -- a gate or a task state
        // segment -- and those are never readable or writable as data.
        if ((access & 0x10) == 0) return false;

        bool isCode = (access & 0x08) != 0;
        bool readableOrWritable = (access & 0x02) != 0;

        // Code segments can never be written, and can only be read when the
        // descriptor says so. Data segments can always be read.
        if (forWrite && isCode) return false;
        if (!readableOrWritable && (forWrite || isCode)) return false;

        // The privilege check: a conforming code segment may be read from
        // anywhere, but everything else has to be at least as privileged as
        // the caller and the selector it arrived in.
        byte descriptorPrivilege = (byte)((descriptor >> 45) & 3);
        bool conforming = isCode && (access & 0x04) != 0;
        if (conforming) return true;

        byte requestorPrivilege = (byte)(selector & 3);
        byte effective = Math.Max(State.Cpl, requestorPrivilege);

        return descriptorPrivilege >= effective;
    }

    /// <summary>
    /// Whether a selector names a descriptor this code is allowed to ask about,
    /// which is what LAR and LSL both have to establish first. Conforming code
    /// segments answer to anyone; everything else has to be at least as
    /// privileged as the caller and the selector it arrived in. System
    /// descriptors are visible only in the kinds the 386 defines -- the gaps in
    /// that table are invalid, and reserved types answer no.
    /// </summary>
    private bool DescriptorVisible(ushort selector, out ulong descriptor)
    {
        descriptor = 0;
        if ((selector & 0xFFFC) == 0) return false;

        try { descriptor = ReadDescriptor(selector); }
        catch (CpuException) { return false; }

        byte access = (byte)(descriptor >> 40);

        // The present bit is deliberately not checked. LAR and LSL ask what a
        // descriptor says, not whether the segment it names is in memory, and
        // a virtual memory system needs exactly that: it has to read the type
        // and privilege of a segment it has paged out in order to decide
        // whether bringing it back is even allowed. Only an actual segment
        // load requires P, and it raises #NP when the bit is clear.

        bool isCode = (access & 0x08) != 0;
        bool isSystem = (access & 0x10) == 0;

        if (isSystem)
        {
            // Of the sixteen system types only these are ones LAR and LSL will
            // describe; the rest are invalid or reserved on a 386.
            switch (access & 0x0F)
            {
                case 0x01: case 0x02: case 0x03: case 0x04:
                case 0x05: case 0x0B: case 0x0C:
                    break;
                default:
                    return false;
            }
        }

        bool conforming = !isSystem && isCode && (access & 0x04) != 0;
        if (conforming) return true;

        byte descriptorPrivilege = (byte)((descriptor >> 45) & 3);
        byte requestorPrivilege = (byte)(selector & 3);
        byte effective = Math.Max(State.Cpl, requestorPrivilege);

        return descriptorPrivilege >= effective;
    }

    private void WriteCr0(uint value)
    {
        // Bits the processor does not implement do not become storage: they
        // read back as zero however hard software writes them. This is how a
        // program tells a 386 from a 486 without CPUID -- it writes the
        // alignment mask bit and sees whether it stuck.
        value &= State.Cr0Writable;

        // On a 486DX, and everything after it, the coprocessor is on the die
        // and the bit that says so is not writable: clearing it would be
        // claiming the chip does not contain a part of itself.
        if (State.Features.Has(CpuFeature.Isa486)) value |= CpuState.Cr0ExtensionType;

        // Paging has no meaning without protected mode to run it in: setting
        // PG while PE is (or would be left) clear is #GP(0), CR0 left
        // exactly as it was (SDM Vol.3 "MOV--Move to/from Control
        // Registers"; coordinator decision U5).
        if ((value & CpuState.Cr0Paging) != 0 && (value & CpuState.Cr0ProtectionEnable) == 0)
            throw new CpuException(13, 0);

        // While PAE paging will be active once this write commits -- PAE
        // already set in CR4, and PG about to be either already set or set
        // by this very write -- a change to PG, CD or NW reloads the four
        // PDPT registers from the current CR3 (SDM Vol.3A §4.4.1's own
        // narrower rule names only PG's 0-to-1 transition; the wider one
        // here also covers CD and NW, which change what a cached
        // translation's write-protect decision means exactly as PG and WP
        // already do below -- coordinator decision U1). This has to happen
        // before State.Cr0 is written: a reserved bit in a present PDPTE is
        // #GP(0) at this instruction, leaving CR0 exactly as it was, not
        // paging (or its cache-control bits) changed over an invalid
        // address space.
        bool paePagingActiveAfterWrite = (value & CpuState.Cr0Paging) != 0 && (State.Cr4 & CpuState.Cr4Pae) != 0;
        const uint PdptReloadTriggers = CpuState.Cr0Paging | CpuState.Cr0CacheDisable | CpuState.Cr0NotWriteThrough;
        if (paePagingActiveAfterWrite && ((State.Cr0 ^ value) & PdptReloadTriggers) != 0)
            LoadPdptRegisters(State.Cr3);

        // Turning paging on or off changes what every linear address means,
        // so nothing cached under the old arrangement is still true. So does
        // the write-protect bit, more subtly: the TLB caches the *decision*
        // about whether a page may be written, and that decision changes
        // meaning underneath it when WP moves.
        const uint Invalidating = CpuState.Cr0Paging | CpuState.Cr0WriteProtect;
        if (((State.Cr0 ^ value) & Invalidating) != 0) FlushTlb();

        State.Cr0 = value;
    }

    /// <summary>
    /// #UD for DR4 or DR5 while CR4.DE is set: with the Debug Extensions on
    /// those two names no longer alias DR6 and DR7, and naming them is an
    /// invalid opcode (SDM vol. 3, "Debug Registers").
    /// </summary>
    private void RequireDebugRegister(int index)
    {
        bool debugExtensions = (State.Cr4 & CpuState.Cr4De) != 0;
        bool aliasedName = index is 4 or 5;

        if (debugExtensions && aliasedName)
        {
            throw new CpuException(6);
        }
    }

    /// <summary>
    /// Writes CR4. Unlike CR0, a bit this processor does not carry is not
    /// silently dropped: writing a 1 to it is <c>#GP(0)</c> (SDM Vol.3 §2.5,
    /// cpu-models.md §2), so the whole write is rejected and CR4 is left
    /// exactly as it was rather than being clipped to the writable subset.
    /// </summary>
    private void WriteCr4(uint value)
    {
        if ((value & ~State.Cr4Writable) != 0) throw new CpuException(13, 0);

        // While PAE paging will be active once this write commits -- paging
        // already on, and PAE about to be either already set or set by this
        // very write -- a change to PAE, PGE or PSE reloads the four PDPT
        // registers from the current CR3 (see WriteCr0's equivalent check,
        // and LoadPdptRegisters' remarks). SDM Vol.3A §4.4.1 names only
        // PAE's own 0-to-1 transition; the wider rule here also covers PGE
        // and PSE, which -- like PAE itself -- change how a cached
        // translation's permissions are decided or what a linear address
        // even means (coordinator decision U1). Also before State.Cr4 is
        // written, for the same reason: a bad PDPTE must leave CR4
        // unchanged, whichever of the three bits triggered the reload.
        bool paePagingActiveAfterWrite = (value & CpuState.Cr4Pae) != 0 && (State.Cr0 & CpuState.Cr0Paging) != 0;
        const uint PdptReloadTriggers = CpuState.Cr4Pae | CpuState.Cr4Pge | CpuState.Cr4Pse;
        if (paePagingActiveAfterWrite && ((State.Cr4 ^ value) & PdptReloadTriggers) != 0)
            LoadPdptRegisters(State.Cr3);

        // PSE, PAE and PGE all change what a linear address means or how its
        // permissions are decided, exactly like CR0's own Paging and
        // WriteProtect bits -- so a change to any of them invalidates every
        // cached translation. PSE is reachable since work package P2.2
        // (CpuFeatures.Implemented); PAE and PGE are P3.1's own. Collins
        // measured that a real P5 does *not* flush its TLB on a bare
        // CR4.PSE toggle, but flushing unconditionally is what later
        // processors are documented to do and is always safe here -- it
        // only costs a re-walk, never a wrong answer (plans/specs/pse-vme.md's
        // P2.2 section, coordinator note). This is the unconditional
        // FlushTlb a CR3 reload does not use -- see FlushTlbForCr3Load in
        // Cpu.Paging.cs -- because here it is what a linear address means or
        // how rights are decided that changed, not merely which address
        // space is current, so even a page marked global must go.
        const uint Invalidating = CpuState.Cr4Pse | CpuState.Cr4Pae | CpuState.Cr4Pge;
        if (((State.Cr4 ^ value) & Invalidating) != 0) FlushTlb();

        State.Cr4 = value;
    }
}
