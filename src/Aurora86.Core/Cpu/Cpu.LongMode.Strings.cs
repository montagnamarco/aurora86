// The string instructions in 64-bit mode (plans/long-mode.md, stage C2c):
// MOVS, CMPS, STOS, LODS, SCAS (A4h-A7h, AAh-AFh) and their REP/REPE/REPNE
// prefixes. INS/OUTS (6Ch-6Fh) touch I/O ports, which stage D adds; this
// file declines them, as it does every opcode outside its own range, so
// Cpu.LongMode.Execute.cs's default branch can raise #UD.
//
// One iteration runs per call, exactly as the legacy Cpu.Strings.cs does it:
// RIP is rewound to the start of the instruction while RCX (or ECX under
// 67h) still has iterations left, so the next Step64 call re-decodes the
// prefixes and runs the next one. A long REP therefore never becomes a
// closed loop inside a single Step64 call -- a pending interrupt gets a
// chance to land between any two iterations, on real hardware and here.
//
// Source: SDM Vol.2, MOVS/CMPS/SCAS/LODS/STOS and REP/REPE/REPNE/REPNZ/REPZ.

namespace Aurora86.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>
    /// A4h-A7h, AAh-AFh: MOVS, CMPS, STOS, LODS, SCAS. Returns false for
    /// anything else -- including 6Ch-6Fh (INS/OUTS, stage D) -- so the
    /// caller's default branch can raise #UD.
    /// </summary>
    private bool TryExecuteString64(byte opcode)
    {
        switch (opcode)
        {
            case 0xA4:
            case 0xA5:
            case 0xA6:
            case 0xA7:
            case 0xAA:
            case 0xAB:
            case 0xAC:
            case 0xAD:
            case 0xAE:
            case 0xAF:
                ExecuteString64(opcode);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Runs one iteration of a string instruction. The source segment is DS,
    /// overridable by FS or GS (the only overrides 64-bit mode keeps); the
    /// destination is always ES, with no override, per SDM Vol.3A §3.4.4.
    /// Every case reads before it writes, and advances RSI/RDI only after a
    /// write that could fault has gone through, so a page fault on the
    /// destination leaves both indices -- and RCX below -- exactly where the
    /// faulting element found them.
    /// </summary>
    private void ExecuteString64(byte opcode)
    {
        bool byteOperand = (opcode & 1) == 0;
        int size = byteOperand ? 1 : OperandBytes64;
        int delta = Flags.Direction ? -size : size;

        bool repeat = _longPrefixes.Rep || _longPrefixes.RepNe;
        if (repeat && StringCounter64 == 0)
        {
            // REP with the count already at zero does nothing at all, not
            // even one iteration (SDM Vol.2, REP/REPE/REPZ/REPNE/REPNZ).
            return;
        }

        SegReg source = _longPrefixes.Segment ?? SegReg.Ds;
        const int Accumulator = (int)Reg.Eax;
        const int SourceRegister = (int)Reg.Esi;
        const int DestinationRegister = (int)Reg.Edi;

        switch (opcode)
        {
            case 0xA4:   // MOVSB
            case 0xA5:   // MOVSW/MOVSD/MOVSQ
            {
                ulong value = ReadMemory64(source, SourceIndex64, size);
                WriteMemory64(SegReg.Es, DestinationIndex64, size, value);
                AdvanceIndex64(SourceRegister, delta);
                AdvanceIndex64(DestinationRegister, delta);
                break;
            }

            case 0xAA:   // STOSB
            case 0xAB:   // STOSW/STOSD/STOSQ
            {
                ulong value = ReadRegister64(Accumulator, size);
                WriteMemory64(SegReg.Es, DestinationIndex64, size, value);
                AdvanceIndex64(DestinationRegister, delta);
                break;
            }

            case 0xAC:   // LODSB
            case 0xAD:   // LODSW/LODSD/LODSQ
            {
                ulong value = ReadMemory64(source, SourceIndex64, size);
                WriteRegister64(Accumulator, size, value);
                AdvanceIndex64(SourceRegister, delta);
                break;
            }

            case 0xA6:   // CMPSB
            case 0xA7:   // CMPSW/CMPSD/CMPSQ
            {
                ulong left = ReadMemory64(source, SourceIndex64, size);
                ulong right = ReadMemory64(SegReg.Es, DestinationIndex64, size);
                SetArithmeticFlags64(FlagOp.Sub, left, right, left - right, size * 8);
                AdvanceIndex64(SourceRegister, delta);
                AdvanceIndex64(DestinationRegister, delta);
                break;
            }

            case 0xAE:   // SCASB
            case 0xAF:   // SCASW/SCASD/SCASQ
            {
                ulong left = ReadRegister64(Accumulator, size);
                ulong right = ReadMemory64(SegReg.Es, DestinationIndex64, size);
                SetArithmeticFlags64(FlagOp.Sub, left, right, left - right, size * 8);
                AdvanceIndex64(DestinationRegister, delta);
                break;
            }
        }

        if (!repeat)
        {
            return;
        }

        ulong remaining = DecrementStringCounter64();
        if (remaining == 0)
        {
            return;
        }

        // CMPS and SCAS compare, so REPE/REPNE also test ZF: REPE (F3) stops
        // once the comparison fails, REPNE (F2) once it succeeds. MOVS,
        // STOS and LODS ignore ZF and only ever count down.
        bool comparison = opcode is 0xA6 or 0xA7 or 0xAE or 0xAF;
        if (comparison)
        {
            if (_longPrefixes.Rep && !Flags.Zero) return;      // REPE: leave when ZF=0
            if (_longPrefixes.RepNe && Flags.Zero) return;     // REPNE: leave when ZF=1
        }

        // Rewind RIP to the start of the instruction: Step64 re-decodes the
        // prefixes and runs the next iteration on its next call, giving a
        // pending interrupt a chance to come through in between.
        State.Rip = _longInstructionStart;
    }

    /// <summary>RSI, or the low 32 bits of it (ESI) under 67h -- Cpu.LongMode.cs's own AddressRegister, which every other address-size-sensitive read in this decoder uses.</summary>
    private ulong SourceIndex64 => AddressRegister((int)Reg.Esi);

    /// <summary>RDI, or the low 32 bits of it (EDI) under 67h.</summary>
    private ulong DestinationIndex64 => AddressRegister((int)Reg.Edi);

    /// <summary>RCX, or the low 32 bits of it (ECX) under 67h -- the count REP tests and decrements; address size, not operand size, governs it (SDM Vol.2, REP).</summary>
    private ulong StringCounter64 => AddressRegister((int)Reg.Ecx);

    /// <summary>
    /// Advances an index register by delta (the operand size, signed by DF).
    /// Under 67h the new value is written through WriteRegister64 at 4
    /// bytes, which zero-extends into the register's upper half exactly as
    /// any other 32-bit write does (SDM Vol.1 §3.4.1.1); otherwise all 64
    /// bits are replaced.
    /// </summary>
    private void AdvanceIndex64(int registerIndex, int delta)
    {
        if (AddressSize32In64)
        {
            uint value = (uint)((long)(uint)State.Gpr[registerIndex] + delta);
            WriteRegister64(registerIndex, 4, value);
        }
        else
        {
            WriteRegister64(registerIndex, 8, (ulong)((long)State.Gpr[registerIndex] + delta));
        }
    }

    /// <summary>
    /// Decrements RCX (or ECX under 67h, zero-extended back into RCX) and
    /// returns the new count, the same operation LOOP's own
    /// CounterAfterDecrement64/CommitCounter64 pair performs (Cpu.LongMode.Control.cs)
    /// but folded into one step since nothing here needs the pre-decrement value.
    /// </summary>
    private ulong DecrementStringCounter64()
    {
        if (AddressSize32In64)
        {
            uint value = (uint)State.Gpr[(int)Reg.Ecx] - 1;
            State.Gpr[(int)Reg.Ecx] = value;   // zero-extends into RCX's upper half
            return value;
        }

        return --State.Gpr[(int)Reg.Ecx];
    }
}
