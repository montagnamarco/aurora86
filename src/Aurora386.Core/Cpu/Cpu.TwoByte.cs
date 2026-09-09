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
                        RequireCpuAtLeast486(0x01);
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
                RequireCpuAtLeast486(opcode);
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
                RequireCpuAtLeast486(opcode);
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
                RequireCpuAtLeast486(opcode);
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
                RequireCpuAtLeast486(opcode);
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
                RequireCpuAtLeast486(opcode);
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

            // CPUID: the processor says what it is.
            //
            // Before it, software identified the processor by the differences
            // between the models -- which flags a shift left alone, whether an
            // instruction existed -- a collection of tricks that got less
            // reliable with each generation. CPUID replaced all of it with a
            // question, and from Windows 95 onwards everything asks.
            //
            // The function number is in EAX. Zero returns the highest function
            // supported and the vendor string, in a register order that looks
            // arbitrary and is not: EBX, EDX, ECX spells "GenuineIntel" when
            // the twelve bytes are laid out in that sequence.
            case 0xA2:
            {
                RequireCpuAtLeast486(opcode);
                if (!State.HasIdentification) throw new CpuException(6);

                switch (State.Eax)
                {
                    case 0:
                        State.Eax = 1;                 // highest function we answer
                        State.Ebx = 0x756E6547;        // "Genu"
                        State.Edx = 0x49656E69;        // "ineI"
                        State.Ecx = 0x6C65746E;        // "ntel"
                        return;

                    default:
                        // Family 4, model 8, stepping 0: an Intel486 DX4. The
                        // feature bits say what the processor can do; ours has
                        // an on-chip floating point unit and nothing else that
                        // needs announcing.
                        State.Eax = 0x0480;
                        State.Ebx = 0;
                        State.Ecx = 0;
                        State.Edx = 0x00000001;        // bit 0: floating point on chip
                        return;
                }
            }

            // INVD and WBINVD: throw the caches away, with and without writing
            // dirty lines back first. This processor has no cache to speak of,
            // so both are checked for privilege and then do nothing -- which
            // is exactly what they do on a 486 with caching disabled.
            case 0x08:
            case 0x09:
                RequireCpuAtLeast486(opcode);
                RequireSupervisor();
                return;

            // --- far pointer loads into the 386's new segment registers -----
            case 0xB2: LoadFarPointer(SegReg.Ss); return;
            case 0xB4: LoadFarPointer(SegReg.Fs); return;
            case 0xB5: LoadFarPointer(SegReg.Gs); return;

            // --- control register access ------------------------------------
            case 0x20:
            {
                RequireSupervisor();
                var m = DecodeModRm();
                State.SetReg32(m.Register, m.Reg switch
                {
                    0 => State.Cr0,
                    2 => State.Cr2,
                    3 => State.Cr3,
                    _ => 0,
                });
                return;
            }
            case 0x22:
            {
                RequireSupervisor();
                var m = DecodeModRm();
                uint value = State.GetReg32(m.Register);
                switch (m.Reg)
                {
                    case 0: WriteCr0(value); break;
                    case 2: State.Cr2 = value; break;
                    case 3:
                        // Loading CR3 points the processor at a different set
                        // of page tables, so every cached translation is now
                        // about the wrong address space.
                        State.Cr3 = value;
                        FlushTlb();
                        break;
                }
                return;
            }

            // --- debug register access --------------------------------------
            // There is no breakpoint hardware behind these, but the values
            // still have to be kept: whoever writes them expects to read the
            // same values back, and debuggers probe exactly that way.
            case 0x21:
            {
                RequireSupervisor();
                var m = DecodeModRm();
                State.SetReg32(m.Register, State.Dr[m.Reg]);
                return;
            }
            case 0x23:
            {
                RequireSupervisor();
                var m = DecodeModRm();
                State.Dr[m.Reg] = State.GetReg32(m.Register);
                return;
            }

            // --- the test registers, TR6 and TR7 -----------------------------
            // These drive the 386's TLB test hardware, and only TR6 and TR7
            // exist. Nothing in DOS or Windows uses them for their real purpose,
            // but memory sizers and CPU probes do read them to tell a 386 from
            // a 486, so they have to exist and hold what was written rather than
            // fault. The TLB here is a plain cache with no test path, so the
            // registers are storage and nothing more.
            case 0x24:
            {
                RequireSupervisor();
                var m = DecodeModRm();
                State.SetReg32(m.Register, m.Reg >= 6 ? State.Tr[m.Reg - 6] : 0);
                return;
            }

            case 0x26:
            {
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

        // On a 486DX the coprocessor is on the die and the bit that says so
        // is not writable: clearing it would be claiming the chip does not
        // contain a part of itself.
        if (State.Model >= CpuModel.Intel486) value |= CpuState.Cr0ExtensionType;

        // Turning paging on or off changes what every linear address means,
        // so nothing cached under the old arrangement is still true. So does
        // the write-protect bit, more subtly: the TLB caches the *decision*
        // about whether a page may be written, and that decision changes
        // meaning underneath it when WP moves.
        const uint Invalidating = CpuState.Cr0Paging | CpuState.Cr0WriteProtect;
        if (((State.Cr0 ^ value) & Invalidating) != 0) FlushTlb();

        State.Cr0 = value;
    }
}
