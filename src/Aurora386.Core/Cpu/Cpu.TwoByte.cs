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
            case 0xA1: LoadSegment(SegReg.Fs, (ushort)PopSegmentOperand()); return;
            case 0xA8: PushOperand(State.Gs.Selector); return;
            case 0xA9: LoadSegment(SegReg.Gs, (ushort)PopSegmentOperand()); return;

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

                    default:
                        throw new CpuException(6);
                }
            }

            // --- CLTS: clears the "task switched" bit of CR0 ----------------
            case 0x06: State.Cr0 &= ~CpuState.Cr0TaskSwitched; return;

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

            // --- far pointer loads into the 386's new segment registers -----
            case 0xB2: LoadFarPointer(SegReg.Ss); return;
            case 0xB4: LoadFarPointer(SegReg.Fs); return;
            case 0xB5: LoadFarPointer(SegReg.Gs); return;

            // --- control register access ------------------------------------
            case 0x20:
            {
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
                var m = DecodeModRm();
                uint value = State.GetReg32(m.Register);
                switch (m.Reg)
                {
                    case 0: WriteCr0(value); break;
                    case 2: State.Cr2 = value; break;
                    case 3: State.Cr3 = value; break;
                }
                return;
            }

            // --- debug register access --------------------------------------
            // There is no breakpoint hardware behind these, but the values
            // still have to be kept: whoever writes them expects to read the
            // same values back, and debuggers probe exactly that way.
            case 0x21:
            {
                var m = DecodeModRm();
                State.SetReg32(m.Register, State.Dr[m.Reg]);
                return;
            }
            case 0x23:
            {
                var m = DecodeModRm();
                State.Dr[m.Reg] = State.GetReg32(m.Register);
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
                byte handlerId = FetchByte();
                if (NativeHandler is null) throw new CpuException(6);
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
        long indiceLungo = index;
        long word = indiceLungo >= 0 ? indiceLungo / width
                                     : (indiceLungo - width + 1) / width;
        int bitInWord = (int)(indiceLungo - word * width);

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
    private void WriteCr0(uint value)
    {
        State.Cr0 = value;
    }
}
