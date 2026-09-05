namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>
    /// Executes a single instruction: collects the prefixes, reads the opcode
    /// and runs it. Returns the number of cycles consumed.
    /// </summary>
    public int Step()
    {
        if (State.Halted) return 1;

        _instructionStart = State.Eip;
        _prefixes.Clear();

        try
        {
            byte opcode = FetchPrefixesAndOpcode();
            Execute(opcode);
        }
        catch (PageFaultException fault)
        {
            // The instruction has to be restarted from the top once the handler
            // has brought the page in, so EIP is rewound to where it began.
            State.Eip = _instructionStart;
            State.Cr2 = fault.LinearAddress;
            DeliverException(14, fault.ErrorCode);
        }
        catch (CpuException ex)
        {
            State.Eip = _instructionStart;
            DeliverException(ex.Vector, ex.ErrorCode);
        }

        Cycles++;
        return 1;
    }

    /// <summary>
    /// Raised when a double fault cannot itself be delivered. A real 386 shuts
    /// down and is reset by the chipset; this machine resets the processor,
    /// which restarts the BIOS. The two vectors are the ones that could not be
    /// delivered, first and second, for whoever is watching.
    /// </summary>
    public event Action<byte, byte>? TripleFault;

    /// <summary>
    /// Delivers an exception, and copes with the delivery itself faulting.
    ///
    /// Reaching a handler means reading the gate, loading a code segment,
    /// perhaps switching stacks and reading a task state segment: every one
    /// of those can fault. The architecture says what happens then. A benign
    /// second fault -- the gate was simply not present, say -- is delivered
    /// in place of the first, which is how an operating system that marks
    /// vectors not-present to catch their first use expects to be told. Two
    /// contributory faults in a row, or two page faults, are a double fault,
    /// vector 8 with an error code of zero. And a double fault that cannot be
    /// delivered is a triple fault: the processor resets. Letting any of this
    /// escape as a host exception instead stops the emulator dead on a
    /// condition a real machine would have survived, or at least rebooted from.
    /// </summary>
    private void DeliverException(byte vector, uint errorCode)
    {
        var before = SaveTransferState();
        try
        {
            RaiseException(vector, errorCode);
        }
        catch (Exception ex) when (ex is CpuException or PageFaultException)
        {
            RestoreTransferState(before);
            (byte second, uint error) = Classify(ex);
            Escalate(vector, second, error, rewind: true);
        }
    }

    /// <summary>
    /// A hardware interrupt, delivered through the same safety net. Two
    /// things differ from an exception: the interrupted instruction has
    /// already finished, so EIP must not be rewound to it; and a fault that
    /// arises while delivering an external interrupt carries the EXT bit in
    /// its error code, which is how a monitor tells an interrupt it has to
    /// reflect from a software INT it has to emulate.
    /// </summary>
    private void DeliverExternalInterrupt(byte vector)
    {
        var before = SaveTransferState();
        try
        {
            EnterInterrupt(vector, isSoftware: false);
        }
        catch (Exception ex) when (ex is CpuException or PageFaultException)
        {
            RestoreTransferState(before);
            (byte second, uint error) = Classify(ex);
            Escalate(vector, second, error | 1, rewind: false);
        }
    }

    private (byte Vector, uint ErrorCode) Classify(Exception ex)
    {
        if (ex is PageFaultException fault)
        {
            State.Cr2 = fault.LinearAddress;
            return (14, fault.ErrorCode);
        }
        var cpu = (CpuException)ex;
        return (cpu.Vector, cpu.ErrorCode);
    }

    // A transfer that faults halfway may already have loaded the handler's CS
    // or switched stacks. The second fault has to be reported against the
    // state the first one found, so that much is put back.
    private (SegmentCache Cs, SegmentCache Ss, uint Esp, uint Eip) SaveTransferState()
        => (State.Segments[(int)SegReg.Cs], State.Segments[(int)SegReg.Ss], State.Esp, State.Eip);

    private void RestoreTransferState((SegmentCache Cs, SegmentCache Ss, uint Esp, uint Eip) s)
    {
        State.Segments[(int)SegReg.Cs] = s.Cs;
        State.Segments[(int)SegReg.Ss] = s.Ss;
        State.Esp = s.Esp;
        State.Eip = s.Eip;
    }

    private void Escalate(byte first, byte second, uint secondError, bool rewind)
    {
        if (rewind) State.Eip = _instructionStart;

        if (!IsDoubleFault(first, second))
        {
            try { RaiseException(second, secondError); return; }
            catch (PageFaultException fault) { State.Cr2 = fault.LinearAddress; }
            catch (CpuException) { }
        }

        try
        {
            RaiseException(8, 0);
        }
        catch (Exception ex) when (ex is CpuException or PageFaultException or NotImplementedException)
        {
            // Including the task-gate case: Windows installs its double-fault
            // handler behind a task gate, which this processor does not switch
            // to yet. Resetting is what the hardware would do one step later.
            TripleFault?.Invoke(first, second);
            Reset();
        }
    }

    /// <summary>
    /// The 386's table of which pairs of faults compound. Divide error and the
    /// segment faults (#TS, #NP, #SS, #GP) are "contributory"; a contributory
    /// fault while delivering another, or while delivering a page fault, is a
    /// double fault, as is a page fault while delivering a page fault. Anything
    /// else -- an interrupt, a software INT, a breakpoint -- is benign, and the
    /// second fault is simply the one that gets delivered.
    /// </summary>
    private static bool IsDoubleFault(byte first, byte second)
    {
        static bool Contributory(byte v) => v is 0 or 10 or 11 or 12 or 13;
        if (Contributory(second)) return Contributory(first) || first == 14;
        if (second == 14) return first == 14;
        return false;
    }

    /// <summary>
    /// Consumes the prefixes that precede an instruction and returns the first
    /// byte that is not one. There may be several prefixes, in any order.
    /// </summary>
    private byte FetchPrefixesAndOpcode()
    {
        while (true)
        {
            byte b = FetchByte();
            switch (b)
            {
                case 0x26: _prefixes.Segment = SegReg.Es; continue;
                case 0x2E: _prefixes.Segment = SegReg.Cs; continue;
                case 0x36: _prefixes.Segment = SegReg.Ss; continue;
                case 0x3E: _prefixes.Segment = SegReg.Ds; continue;
                case 0x64: _prefixes.Segment = SegReg.Fs; continue;
                case 0x65: _prefixes.Segment = SegReg.Gs; continue;
                case 0x66: _prefixes.OperandSize = true; continue;
                case 0x67: _prefixes.AddressSize = true; continue;
                case 0xF0: _prefixes.Lock = true; continue;
                case 0xF2: _prefixes.RepNe = true; continue;
                case 0xF3: _prefixes.Rep = true; continue;
                default: return b;
            }
        }
    }

    /// <summary>
    /// Decides whether the LOCK prefix is legal in front of this opcode. The
    /// list is the manual's: only the forms that write back to memory the
    /// result of a read from that same address. The ModR/M byte has not been
    /// consumed yet, so this filters by opcode and the "destination is a
    /// register" case is ruled out immediately afterwards.
    /// </summary>
    private bool LockAllowed(byte opcode)
    {
        // The eight ALU operations in their r/m-destination forms. CMP (the
        // 0x38 group) is excluded: it only compares and writes nothing back, so
        // there is nothing to make atomic.
        if (opcode < 0x38 && (opcode & 0x07) <= 1) return PeekModRmIsMemory();

        // 0x0F opens the two-byte opcodes: the decision depends on the second
        // byte, which has not been consumed here either.
        if (opcode == 0x0F) return LockAllowedTwoByte(ReadMem8(SegReg.Cs, State.Eip));

        return opcode switch
        {
            // ALU with an immediate: the ModR/M reg field picks the operation,
            // and the value 7 is CMP once again.
            // 0x82 is the undocumented alias of 0x80 and accepts LOCK just as
            // it does.
            0x80 or 0x81 or 0x82 or 0x83 => PeekModRmIsMemory() && PeekModRmReg() != 7,
            0x86 or 0x87 => PeekModRmIsMemory(),           // XCHG
            // Of the F6/F7 group only NOT (2) and NEG (3) write to memory;
            // TEST, MUL and DIV do not.
            0xF6 or 0xF7 => PeekModRmIsMemory()
                            && PeekModRmReg() is 2 or 3,
            // Of the FE/FF group only INC (0) and DEC (1): the other forms are
            // jumps and pushes, which never write the operand back.
            0xFE or 0xFF => PeekModRmIsMemory()
                            && PeekModRmReg() is 0 or 1,
            _ => false,
        };
    }

    /// <summary>
    /// Among the two-byte opcodes only the bit instructions that write the
    /// operand back accept LOCK: BT merely reads, so it is excluded just as CMP
    /// and TEST are among the one-byte opcodes.
    /// </summary>
    private bool LockAllowedTwoByte(byte opcode)
    {
        // The ModR/M byte comes after the second opcode byte.
        bool memory = (ReadMem8(SegReg.Cs, State.Eip + 1) >> 6) != 3;
        int reg = (ReadMem8(SegReg.Cs, State.Eip + 1) >> 3) & 7;

        return opcode switch
        {
            0xAB or 0xB3 or 0xBB => memory,          // BTS, BTR, BTC
            0xBA => memory && reg >= 5,              // the group, minus BT (4)
            _ => false,
        };
    }

    /// <summary>
    /// Peeks at the ModR/M byte without consuming it, to find out whether the
    /// r/m operand is in memory: the only case in which LOCK means anything.
    /// </summary>
    private bool PeekModRmIsMemory()
        => (ReadMem8(SegReg.Cs, State.Eip) >> 6) != 3;

    /// <summary>The ModR/M reg field, which in some groups selects the operation.</summary>
    private int PeekModRmReg()
        => (ReadMem8(SegReg.Cs, State.Eip) >> 3) & 7;

    /// <summary>
    /// Opcode dispatch.
    ///
    /// The x86 table is strongly regular: the first 0x40 bytes are eight ALU
    /// operations with six forms each, laid out so that the low bits of the
    /// opcode encode direction and size. Exploiting that regularity avoids
    /// hundreds of nearly identical cases and keeps the hot path small enough
    /// to stay resident in the instruction cache.
    /// </summary>
    private void Execute(byte opcode)
    {
        // LOCK makes a memory access atomic, so it only means anything on the
        // instructions that read an operand from memory and write it back. On
        // everything else -- a register destination, or an instruction that is
        // not read-modify-write -- the processor rejects the instruction rather
        // than ignoring the prefix.
        if (_prefixes.Lock && !LockAllowed(opcode)) throw new CpuException(6);

        // --- 0x00-0x3F: ADD OR ADC SBB AND SUB XOR CMP ---------------------
        // Each block of 8 opcodes is one operation; within the block bits 0-2
        // select the form. The two remaining opcodes of each block (0x06/0x07
        // and so on) are segment PUSH/POP, handled further down.
        if (opcode < 0x40)
        {
            int form = opcode & 7;
            if (form < 6)
            {
                ExecuteAluGroup((byte)(opcode >> 3), form);
                return;
            }
        }

        switch (opcode)
        {
            // --- segment PUSH/POP (the gaps in the ALU table) --------------
            case 0x06: PushOperand(State.Es.Selector); return;
            case 0x07: PopSegment(SegReg.Es); return;
            case 0x0E: PushOperand(State.Cs.Selector); return;
            case 0x16: PushOperand(State.Ss.Selector); return;
            case 0x17: PopSegment(SegReg.Ss); return;
            case 0x1E: PushOperand(State.Ds.Selector); return;
            case 0x1F: PopSegment(SegReg.Ds); return;

            // --- register INC/DEC (0x40-0x4F) ------------------------------
            case >= 0x40 and <= 0x47: IncReg(opcode - 0x40); return;
            case >= 0x48 and <= 0x4F: DecReg(opcode - 0x48); return;

            // --- register PUSH/POP (0x50-0x5F) -----------------------------
            case >= 0x50 and <= 0x57:
                PushOperand(State.Gpr[opcode - 0x50]);
                return;
            case >= 0x58 and <= 0x5F:
            {
                uint value = PopOperand();
                if (OperandSize32) State.SetReg32(opcode - 0x58, value);
                else State.SetReg16(opcode - 0x58, (ushort)value);
                return;
            }

            case 0x60: Pusha(); return;
            case 0x61: Popa(); return;

            // BOUND: checks that the register lies between the two limits read
            // from memory, endpoints included. Out of range raises #BR, vector
            // 5 -- it exists so that array bounds checks cost one instruction.
            case 0x62:
            {
                var m = DecodeModRm();
                if (m.IsRegister) throw new CpuException(6);
                if (OperandSize32)
                {
                    int valore = (int)State.GetReg32(m.Reg);
                    int minimo = (int)ReadMem32(m.Segment, m.Offset);
                    int massimo = (int)ReadMem32(m.Segment, m.Offset + 4);
                    if (valore < minimo || valore > massimo) throw new CpuException(5);
                }
                else
                {
                    short valore = (short)State.GetReg16(m.Reg);
                    short minimo = (short)ReadMem16(m.Segment, m.Offset);
                    short massimo = (short)ReadMem16(m.Segment, m.Offset + 2);
                    if (valore < minimo || valore > massimo) throw new CpuException(5);
                }
                return;
            }

            // ARPL: adjusts a selector's requested privilege level. Outside
            // protected mode it has no meaning and the 386 rejects it.
            case 0x63:
                // ARPL exists only under protection. In real mode and in a virtual
                // 8086 task the opcode is undefined and raises #UD -- and Windows'
                // monitor depends on exactly that: it plants 63h in its V86 stubs
                // as a breakpoint, and takes the #UD as its cue to step back in.
                // Executing it instead lets the stub run on into the bytes after
                // it, which are not code, and the DOS session ends up returning
                // through an empty frame into the interrupt vector table.
                if (!State.ProtectedMode || State.Virtual8086Mode) throw new CpuException(6);
            {
                if (!State.ProtectedMode) throw new CpuException(6);
                var m = DecodeModRm();
                ushort destination = ReadRm16(m);
                ushort source = State.GetReg16(m.Reg);
                if ((destination & 3) < (source & 3))
                {
                    WriteRm16(m, (ushort)((destination & ~3) | (source & 3)));
                    Flags.Zero = true;
                }
                else Flags.Zero = false;
                return;
            }

            // --- BCD (the "holes" every 8 opcodes in the ALU table) --------
            case 0x27: Daa(); return;
            case 0x2F: Das(); return;
            case 0x37: Aaa(); return;
            case 0x3F: Aas(); return;
            case 0xD4: Aam(); return;
            case 0xD5: Aad(); return;

            // SALC: undocumented by Intel but present in every chip. It fills
            // AL with the carry flag -- 0xFF when set, 0x00 when clear --
            // without disturbing the flags. It was a way of turning CF into a
            // bit mask in a single byte.
            case 0xD6: State.SetReg8(0, Flags.Carry ? (byte)0xFF : (byte)0x00); return;

            // --- IMUL with an immediate (the 186's three-operand forms) ----
            case 0x69:
            {
                var m = DecodeModRm();
                ImulImmediate(m, FetchImmediate());
                return;
            }
            case 0x6B:
            {
                var m = DecodeModRm();
                ImulImmediate(m, (uint)(sbyte)FetchByte());
                return;
            }

            case 0x68: PushOperand(FetchImmediate()); return;
            case 0x6A: PushOperand((uint)(sbyte)FetchByte()); return;

            // --- string instructions ----------------------------------------
            case 0x6C: case 0x6D:   // INS
            case 0x6E: case 0x6F:   // OUTS
            case 0xA4: case 0xA5:   // MOVS
            case 0xA6: case 0xA7:   // CMPS
            case 0xAA: case 0xAB:   // STOS
            case 0xAC: case 0xAD:   // LODS
            case 0xAE: case 0xAF:   // SCAS
                ExecuteString(opcode);
                return;

            // --- I/O ports ---------------------------------------------------
            case 0xE4: { ushort port = FetchByte(); CheckIoPermission(port, 1); State.SetReg8(0, _io.ReadByte(port)); return; }
            case 0xE5: ReadPortToAccumulator(FetchByte()); return;
            case 0xE6: { ushort port = FetchByte(); CheckIoPermission(port, 1); _io.WriteByte(port, State.GetReg8(0)); return; }
            case 0xE7: WriteAccumulatorToPort(FetchByte()); return;
            case 0xEC: CheckIoPermission(State.Dx, 1); State.SetReg8(0, _io.ReadByte(State.Dx)); return;
            case 0xED: ReadPortToAccumulator(State.Dx); return;
            case 0xEE: CheckIoPermission(State.Dx, 1); _io.WriteByte(State.Dx, State.GetReg8(0)); return;
            case 0xEF: WriteAccumulatorToPort(State.Dx); return;

            // --- shifts and rotates -------------------------------------------
            case 0xC0: { var m = DecodeModRm(); ExecuteShift(m, FetchByte(), true); return; }
            case 0xC1: { var m = DecodeModRm(); ExecuteShift(m, FetchByte(), false); return; }
            case 0xD0: { var m = DecodeModRm(); ExecuteShift(m, 1, true); return; }
            case 0xD1: { var m = DecodeModRm(); ExecuteShift(m, 1, false); return; }
            case 0xD2: { var m = DecodeModRm(); ExecuteShift(m, State.GetReg8(1), true); return; }
            case 0xD3: { var m = DecodeModRm(); ExecuteShift(m, State.GetReg8(1), false); return; }

            // --- groups that extend the opcode through the reg field ---------
            case 0xF6: Group3Byte(); return;
            case 0xF7: Group3Word(); return;
            case 0xFE: Group4(); return;
            case 0xFF: Group5(); return;

            // --- LES/LDS: load a far pointer into segment + register ---------
            case 0xC4: LoadFarPointer(SegReg.Es); return;
            case 0xC5: LoadFarPointer(SegReg.Ds); return;

            // --- ENTER/LEAVE ---------------------------------------------------
            // WAIT: synchronises with the coprocessor. With no 387 installed
            // there is nothing to wait for, but the instruction is still valid
            // and must not fault.
            case 0x9B: return;

            case 0xC8: Enter(); return;

            // --- loops and the jump on CX --------------------------------------
            case 0xE0: LoopCondition(zeroRequired: false, useZeroFlag: true); return;
            case 0xE1: LoopCondition(zeroRequired: true, useZeroFlag: true); return;
            case 0xE2: LoopCondition(zeroRequired: false, useZeroFlag: false); return;
            case 0xE3: JumpIfCxZero(); return;

            // --- short conditional jumps (0x70-0x7F) -----------------------
            case >= 0x70 and <= 0x7F:
            {
                sbyte offset = (sbyte)FetchByte();
                if (TestCondition((byte)(opcode & 0x0F)))
                    JumpRelative(offset);
                return;
            }

            // --- the immediate group (0x80-0x83) ---------------------------
            // 0x82 is an undocumented alias of 0x80: same encoding, same
            // effect. The 386 executes it, and software of the era does emit
            // it, so it has to be accepted rather than raising #UD.
            case 0x80 or 0x82: AluImmediate8(); return;
            case 0x81: AluImmediate(signExtendedByte: false); return;
            case 0x83: AluImmediate(signExtendedByte: true); return;

            // --- TEST and XCHG ---------------------------------------------
            case 0x84: { var m = DecodeModRm(); SetLogicFlags8((byte)(ReadRm8(m) & State.GetReg8(m.Reg))); return; }
            case 0x85: { var m = DecodeModRm(); SetLogicFlags(ReadRm(m) & ReadRegOperand(m)); return; }
            case 0x86: { var m = DecodeModRm(); byte t = ReadRm8(m); WriteRm8(m, State.GetReg8(m.Reg)); State.SetReg8(m.Reg, t); return; }
            case 0x87: { var m = DecodeModRm(); uint t = ReadRm(m); WriteRm(m, ReadRegOperand(m)); WriteRegOperand(m, t); return; }

            // --- MOV ---------------------------------------------------------
            case 0x88: { var m = DecodeModRm(); WriteRm8(m, State.GetReg8(m.Reg)); return; }
            case 0x89: { var m = DecodeModRm(); WriteRm(m, ReadRegOperand(m)); return; }
            case 0x8A: { var m = DecodeModRm(); State.SetReg8(m.Reg, ReadRm8(m)); return; }
            case 0x8B: { var m = DecodeModRm(); WriteRegOperand(m, ReadRm(m)); return; }
            case 0x8C:
            {
                var m = DecodeModRm();
                // Codes 6 and 7 name no segment register at all, so the 386
                // rejects the instruction.
                if (m.Reg > (int)SegReg.Gs) throw new CpuException(6);
                ushort selector = State.Segments[m.Reg].Selector;
                // Into a 32-bit register the selector is zero-extended rather
                // than merged into the low half, so the top half has to be
                // cleared. To memory, by contrast, only two bytes are written.
                if (m.IsRegister && OperandSize32) State.SetReg32(m.Register, selector);
                else WriteRm16(m, selector);
                return;
            }
            case 0x8D: Lea(); return;
            case 0x8E:
            {
                var m = DecodeModRm();
                // CS cannot be loaded this way: changing it without changing IP
                // at the same time would leave execution at a meaningless
                // address, so a far jump is required instead. Codes 6 and 7 are
                // not registers.
                if (m.Reg == (int)SegReg.Cs || m.Reg > (int)SegReg.Gs)
                    throw new CpuException(6);
                LoadSegment((SegReg)m.Reg, ReadRm16(m));
                return;
            }
            case 0x8F:
            {
                // The destination is validated before the stack moves: if the
                // write would overrun the segment limit, the exception finds
                // ESP still intact, and the handler sees the same stack pointer
                // the code executing the POP had.
                // This group, too, only has the /0 form.
                var m = DecodeModRm();
                if (m.Reg != 0) throw new CpuException(6);
                if (!m.IsRegister)
                    CheckLimit(m.Segment, m.Offset, OperandSize32 ? 4u : 2u);

                uint valore = PopOperand();

                // When the destination is computed from the stack pointer, the
                // effective address is the one *after* the pop: the ModR/M has
                // already been decoded, so it is enough to displace it.
                ModRm destination = m;
                if (!m.IsRegister && m.UsesStackPointer)
                    destination.Offset += OperandSize32 ? 4u : 2u;

                WriteRm(destination, valore);
                return;
            }

            case 0x90: return;   // NOP (which really is XCHG EAX, EAX)
            case >= 0x91 and <= 0x97:
            {
                int index = opcode - 0x90;
                if (OperandSize32)
                {
                    (State.Gpr[0], State.Gpr[index]) = (State.Gpr[index], State.Gpr[0]);
                }
                else
                {
                    ushort t = State.Ax;
                    State.Ax = State.GetReg16(index);
                    State.SetReg16(index, t);
                }
                return;
            }

            case 0x9A: CallFar(); return;
            case 0xD7: Xlat(); return;

            // --- FPU escapes (0xD8-0xDF) ------------------------------------
            // This machine has no coprocessor, which the BIOS already reports
            // in the equipment word. The processor still has to decode the
            // instruction and then refuse it as "device not available", so that
            // an emulator library installed on vector 7 can step in and finish
            // the work. Reporting an invalid opcode instead would be a lie, and
            // leaves software with no way to recover: it faults, the handler
            // finds nothing to do, and the instruction is retried for ever.
            case >= 0xD8 and <= 0xDF:
            {
                // The operand still has to be consumed so that the address is
                // computed and any segment fault is raised in the right order;
                // only then does the escape itself fail.
                DecodeModRm();
                throw new CpuException(7);
            }

            case 0x98: Cbw(); return;
            case 0x99: Cwd(); return;
            case 0x9C: Pushf(); return;
            case 0x9D: Popf(); return;
            case 0x9E: Sahf(); return;
            case 0x9F: Lahf(); return;

            // --- MOV with a direct address (moffs) --------------------------
            case 0xA0: State.SetReg8(0, ReadMem8(EffectiveSegment(SegReg.Ds), FetchAddress())); return;
            case 0xA1: WriteAccumulator(ReadMemOperand(EffectiveSegment(SegReg.Ds), FetchAddress())); return;
            case 0xA2: WriteMem8(EffectiveSegment(SegReg.Ds), FetchAddress(), State.GetReg8(0)); return;
            case 0xA3: WriteMemOperand(EffectiveSegment(SegReg.Ds), FetchAddress(), State.Eax); return;

            case 0xA8: SetLogicFlags8((byte)(State.GetReg8(0) & FetchByte())); return;
            case 0xA9: SetLogicFlags(ReadAccumulator() & FetchImmediate()); return;

            // --- MOV immediate into a register (0xB0-0xBF) ------------------
            case >= 0xB0 and <= 0xB7: State.SetReg8(opcode - 0xB0, FetchByte()); return;
            case >= 0xB8 and <= 0xBF:
            {
                uint value = FetchImmediate();
                if (OperandSize32) State.SetReg32(opcode - 0xB8, value);
                else State.SetReg16(opcode - 0xB8, (ushort)value);
                return;
            }

            // --- returns ------------------------------------------------------
            // The stack pointer has to be restored if the return address
            // overruns the code segment limit: the exception must find the
            // stack as it stood before the RET, not halfway through it.
            case 0xC2:
            {
                ushort pop = FetchWord();
                uint espBefore = State.Esp;
                if (OperandSize32) CheckStackBlock(4u);
                try { JumpTo(PopOperand()); }
                catch (CpuException) { State.Esp = espBefore; throw; }
                AdjustStack(pop);
                return;
            }
            case 0xC3:
            {
                uint espBefore = State.Esp;
                if (OperandSize32) CheckStackBlock(4u);
                try { JumpTo(PopOperand()); }
                catch (CpuException) { State.Esp = espBefore; throw; }
                return;
            }

            // Both of these groups only have the /0 form: the reg field carries
            // no meaning otherwise, and the 386 rejects the instruction.
            case 0xC6:
            {
                var m = DecodeModRm();
                if (m.Reg != 0) throw new CpuException(6);
                WriteRm8(m, FetchByte());
                return;
            }
            case 0xC7:
            {
                var m = DecodeModRm();
                if (m.Reg != 0) throw new CpuException(6);
                WriteRm(m, FetchImmediate());
                return;
            }

            case 0xC9: Leave(); return;
            case 0xCB: RetFar(0); return;
            case 0xCA: { ushort pop = FetchWord(); RetFar(pop); return; }

            case 0xCC: SoftwareInterrupt(3); return;
            case 0xCD:
            {
                byte vector = FetchByte();
                // INT n is IOPL-sensitive in virtual 8086 mode, and the check
                // comes before the gate is even looked at: below IOPL 3 the
                // task gets #GP(0) and the monitor reflects the interrupt
                // itself. Consulting the gate first turns a trap the monitor
                // is built to handle into #NP or #GP(selector), which it is
                // not -- and the DOS session dies on its first INT 21h.
                if (State.Virtual8086Mode) RequireIoPrivilege();
                SoftwareInterrupt(vector);
                return;
            }
            case 0xCE: if (Flags.Overflow) SoftwareInterrupt(4); return;
            case 0xCF: Iret(); return;

            // --- calls and jumps -----------------------------------------------
            case 0xE8:
            {
                int offset = FetchDisplacement();
                PushOperand(State.Eip);
                JumpRelative(offset);
                return;
            }
            case 0xE9: JumpRelative(FetchDisplacement()); return;
            case 0xEA: JumpFar(); return;
            case 0xEB: { sbyte offset = (sbyte)FetchByte(); JumpRelative(offset); return; }

            case 0xF4:
                // A V86 task must never stop the machine: it believes it owns
                // the computer, but it is one task among several and the
                // monitor has to stay alive to schedule the rest.
                if (State.Virtual8086Mode) throw new CpuException(13, 0);
                RequireSupervisor();
                State.Halted = true;
                return;
            case 0xF5: Flags.Carry = !Flags.Carry; return;
            case 0xF8: Flags.Carry = false; return;
            case 0xF9: Flags.Carry = true; return;
            case 0xFA: RequireIoPrivilege(); Flags.Interrupt = false; return;
            case 0xFB:
                RequireIoPrivilege();
                // Enabling takes effect only after the next instruction, not
                // immediately: see _interruptShadow.
                Flags.Interrupt = true;
                _interruptShadow = true;
                return;
            case 0xFC: Flags.Direction = false; return;
            case 0xFD: Flags.Direction = true; return;

            case 0x0F: ExecuteTwoByte(FetchByte()); return;

            default:
                // An unrecognised opcode is an invalid instruction: real
                // hardware raises exception 6, and some programs trigger it
                // deliberately to identify the processor. But since an opcode
                // that is merely unimplemented here produces exactly the same
                // symptom, the event is reported as well -- without it,
                // diagnosing "the system does not boot" would mean inspecting
                // every suspicious branch by hand.
                UnknownOpcode?.Invoke(opcode, _instructionStart, State.Cs.Selector);
                throw new CpuException(6);
        }
    }

    /// <summary>
    /// Raised when an unrecognised opcode is encountered: the opcode byte, the
    /// instruction's offset and the code segment.
    /// </summary>
    public event Action<byte, uint, ushort>? UnknownOpcode;

    /// <summary>Effective segment, taking any override prefix into account.</summary>
    private SegReg EffectiveSegment(SegReg dflt)
        => _prefixes.Segment ?? dflt;

    /// <summary>A direct address, at the address size currently in effect.</summary>
    private uint FetchAddress() => AddressSize32 ? FetchDword() : FetchWord();

    /// <summary>
    /// Moves EIP to a new value, checking that it stays inside the code
    /// segment limit. An address past the limit is never executed: the 386
    /// rejects it with #GP before it even fetches the instruction there.
    /// </summary>
    private void JumpTo(uint destination)
    {
        if (destination > State.Cs.Limit) throw new CpuException(13, 0);
        State.Eip = destination;
    }

    /// <summary>
    /// Checks up front that a block of stack fits entirely inside the limit.
    /// The instructions that read several values at once -- the returns, POPA
    /// -- validate the whole extent before starting, and so they fail outright
    /// rather than wrapping the way a single pop would.
    /// </summary>
    private void CheckStackBlock(uint dimensione)
    {
        if (!State.Ss.Big) CheckLimit(SegReg.Ss, State.Sp, dimensione);
    }

    /// <summary>
    /// The relative displacement of a jump or call, sign-extended from the
    /// operand size in effect. The extension has to start from the right type:
    /// at 16 bits 0xFFF3 means -13, not +65523.
    /// </summary>
    private int FetchDisplacement()
        => OperandSize32 ? (int)FetchDword() : (short)FetchWord();
}

/// <summary>
/// A processor exception (divide by zero, invalid opcode, ...). It is turned
/// into an interrupt vector by <see cref="Cpu.Step"/>.
/// </summary>
public class CpuException(byte vector, uint errorCode = 0)
    : Exception($"CPU exception {vector}")
{
    public byte Vector { get; } = vector;
    public uint ErrorCode { get; } = errorCode;
}
