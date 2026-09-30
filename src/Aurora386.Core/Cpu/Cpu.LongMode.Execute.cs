// The one-byte opcode table of 64-bit mode (plans/long-mode.md, stage C).
// Stage C1 carries the data-movement instructions the decoder's plumbing is
// proved with -- MOV in its forms, LEA, MOVSXD, XCHG with the accumulator,
// NOP, HLT and MOV to and from the control registers; the arithmetic, stack,
// branch and string groups join it in stage C2. Until an opcode is listed here it raises #UD in 64-bit mode,
// which is also the right answer for the opcodes 64-bit mode removed (SDM
// Vol.2 Appendix A, the "i64" entries).

namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    private void Execute64(byte opcode)
    {
        // LOCK is legal only in front of the read-modify-write forms with a
        // memory destination (SDM Vol.2, LOCK); in front of anything else,
        // MOV CRn included (AMD's CR8 alias is not declared), it is #UD. A
        // two-byte opcode is judged once its second byte is known, in
        // ExecuteTwoByte64.
        if (opcode != 0x0F && _longPrefixes.Lock && !LockAllowedAlu64(opcode, twoByte: false))
        {
            throw new CpuException(6);
        }

        switch (opcode)
        {
            case 0x0F:
                ExecuteTwoByte64(FetchByte64());
                return;

            case 0x63:
                ExecuteMovsxd();
                return;

            case 0x88:
                MoveRegisterToOperand64(operandBytes: 1);
                return;
            case 0x89:
                MoveRegisterToOperand64(OperandBytes64);
                return;
            case 0x8A:
                MoveOperandToRegister64(operandBytes: 1);
                return;
            case 0x8B:
                MoveOperandToRegister64(OperandBytes64);
                return;

            case 0x8D:
                ExecuteLea64();
                return;

            case 0x90:
                // 90h is XCHG eAX, eAX -- a NOP -- except with REX.B, where
                // it exchanges R8 with the accumulator (SDM Vol.2 NOP/XCHG).
                if (_longPrefixes.RexB)
                {
                    ExchangeWithAccumulator64(opcode);
                }

                return;

            case >= 0x91 and <= 0x97:
                ExchangeWithAccumulator64(opcode);
                return;

            case >= 0xB0 and <= 0xB7:
                MoveImmediateToByteRegister64(opcode);
                return;

            case >= 0xB8 and <= 0xBF:
                MoveImmediateToRegister64(opcode);
                return;

            case 0xC6:
                MoveImmediateToOperand64(operandBytes: 1);
                return;
            case 0xC7:
                MoveImmediateToOperand64(OperandBytes64);
                return;

            case 0xF4:
                RequireSupervisor();
                State.Halted = true;
                return;

            default:
                // The arithmetic and logic groups (Cpu.LongMode.Alu.cs), then
                // the stack, control-flow and miscellaneous ones
                // (Cpu.LongMode.Control.cs).
                if (TryExecuteAlu64(opcode))
                {
                    return;
                }

                if (TryExecuteControl64(opcode))
                {
                    return;
                }

                // The string instructions (Cpu.LongMode.Strings.cs).
                if (TryExecuteString64(opcode))
                {
                    return;
                }

                // The system instructions (Cpu.LongMode.System.cs).
                if (TryExecuteSystem64(opcode))
                {
                    return;
                }

                throw new CpuException(6);
        }
    }

    /// <summary>The 0Fh opcode table of 64-bit mode; see <see cref="Execute64"/> for what is not there yet.</summary>
    private void ExecuteTwoByte64(byte opcode)
    {
        if (_longPrefixes.Lock && !LockAllowedAlu64(opcode, twoByte: true))
        {
            throw new CpuException(6);
        }

        switch (opcode)
        {
            case 0x20:
                MoveFromControlRegister64();
                return;
            case 0x22:
                MoveToControlRegister64();
                return;
            default:
                if (TryExecuteAluTwoByte64(opcode))
                {
                    return;
                }

                if (TryExecuteControlTwoByte64(opcode))
                {
                    return;
                }

                if (TryExecuteSystemTwoByte64(opcode))
                {
                    return;
                }

                throw new CpuException(6);
        }
    }

    // --- MOV CRn ----------------------------------------------------------------------

    /// <summary>
    /// The control register a MOV CRn names: the reg field extended by REX.R.
    /// In 64-bit mode the operand is always 64 bits and the mod field is
    /// ignored (SDM Vol.2 MOV--Move to/from Control Registers). CR0, CR2, CR3,
    /// CR4 and CR8 (the task priority register, reached with REX.R) exist;
    /// any other number is #UD.
    /// </summary>
    private (int ControlRegister, int GeneralRegister) DecodeControlRegisterMove64()
    {
        byte modrm = FetchByte64();
        int controlRegister = ((modrm >> 3) & 7) | (_longPrefixes.RexR ? 8 : 0);
        int generalRegister = (modrm & 7) | (_longPrefixes.RexB ? 8 : 0);

        if (controlRegister is not (0 or 2 or 3 or 4 or TaskPriorityControlRegister))
        {
            throw new CpuException(6);
        }

        RequireSupervisor();
        return (controlRegister, generalRegister);
    }

    /// <summary>CR8, the task priority register (SDM Vol.3A §10.8.6.1).</summary>
    private const int TaskPriorityControlRegister = 8;

    /// <summary>
    /// A processor without an interrupt source that owns a TPR still keeps
    /// what CR8 was given, so a kernel reads back what it wrote.
    /// </summary>
    private int _taskPriorityClassWithoutApic;

    private int ReadTaskPriorityClass()
    {
        if (Interrupts is Devices.Interrupts.ITaskPriority taskPriority)
        {
            return taskPriority.TaskPriorityClass;
        }

        return _taskPriorityClassWithoutApic;
    }

    private void WriteTaskPriorityClass(int priorityClass)
    {
        if (Interrupts is Devices.Interrupts.ITaskPriority taskPriority)
        {
            taskPriority.TaskPriorityClass = priorityClass;
            return;
        }

        _taskPriorityClassWithoutApic = priorityClass;
    }

    /// <summary>0Fh 20h: MOV r64, CRn.</summary>
    private void MoveFromControlRegister64()
    {
        (int controlRegister, int generalRegister) = DecodeControlRegisterMove64();
        ulong value = controlRegister switch
        {
            0 => State.Cr0,
            2 => State.Cr2Full,
            3 => State.Cr3,
            4 => State.Cr4,
            _ => (ulong)ReadTaskPriorityClass(),
        };

        State.Gpr[generalRegister] = value;
    }

    /// <summary>
    /// 0Fh 22h: MOV CRn, r64. Bits 63:32 of CR0 and CR4 are reserved, so a 1
    /// there is #GP(0). CR2 takes all 64 bits. CR3 keeps 32: this machine has
    /// no memory above 4 GB for a PML4 to live in, so a CR3 naming one is
    /// refused with #GP(0) rather than silently truncated.
    /// </summary>
    private void MoveToControlRegister64()
    {
        (int controlRegister, int generalRegister) = DecodeControlRegisterMove64();
        ulong value = State.Gpr[generalRegister];

        if (controlRegister == TaskPriorityControlRegister)
        {
            // CR8 holds a priority class 0-15; bits 63:4 are reserved.
            const ulong TaskPriorityClassMask = 0xF;
            if ((value & ~TaskPriorityClassMask) != 0)
            {
                throw new CpuException(13, 0);
            }

            WriteTaskPriorityClass((int)value);
            return;
        }

        if (controlRegister != 2 && (value >> 32) != 0)
        {
            throw new CpuException(13, 0);
        }

        switch (controlRegister)
        {
            case 0:
                WriteCr0((uint)value);
                break;
            case 2:
                State.SetCr2(value);
                break;
            case 3:
                State.Cr3 = (uint)value;
                FlushTlbForCr3Load();
                break;
            default:
                WriteCr4((uint)value);
                break;
        }
    }

    // --- MOV ------------------------------------------------------------------------

    /// <summary>88h/89h: MOV r/m, reg.</summary>
    private void MoveRegisterToOperand64(int operandBytes)
    {
        LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
        ulong value = ReadRegister64(operand.Reg, operandBytes);
        WriteOperand64(operand, operandBytes, value);
    }

    /// <summary>8Ah/8Bh: MOV reg, r/m.</summary>
    private void MoveOperandToRegister64(int operandBytes)
    {
        LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
        ulong value = ReadOperand64(operand, operandBytes);
        WriteRegister64(operand.Reg, operandBytes, value);
    }

    /// <summary>B0h-B7h: MOV r8, imm8; REX.B reaches R8B-R15B.</summary>
    private void MoveImmediateToByteRegister64(byte opcode)
    {
        int register = (opcode & 7) | (_longPrefixes.RexB ? 8 : 0);
        byte value = FetchByte64();
        WriteRegister64(register, 1, value);
    }

    /// <summary>
    /// B8h-BFh: MOV reg, imm. The only instruction with a full 64-bit
    /// immediate: with REX.W the immediate is eight bytes (MOV r64, imm64);
    /// otherwise 32 bits, zero-extended by the register write, or 16 with 66h.
    /// </summary>
    private void MoveImmediateToRegister64(byte opcode)
    {
        int register = (opcode & 7) | (_longPrefixes.RexB ? 8 : 0);
        int operandBytes = OperandBytes64;
        ulong value = operandBytes == 8 ? FetchQword64() : FetchImmediate64(operandBytes);
        WriteRegister64(register, operandBytes, value);
    }

    /// <summary>
    /// C6h/C7h /0: MOV r/m, imm. A 64-bit operand takes a sign-extended
    /// 32-bit immediate. Any reg field other than 0 is #UD (the Intel group
    /// 11 encodings other than /0 are reserved outside RTM, which is not
    /// declared here).
    /// </summary>
    private void MoveImmediateToOperand64(int operandBytes)
    {
        int immediateBytes = Math.Min(operandBytes, 4);   // a 64-bit operand takes an imm32
        LongModeOperand operand = DecodeModRm64(immediateBytes);
        if ((operand.Reg & 7) != 0)
        {
            throw new CpuException(6);
        }

        ulong value = FetchImmediate64(operandBytes);
        WriteOperand64(operand, operandBytes, value);
    }

    /// <summary>
    /// 8Dh: LEA reg, m -- the effective address itself, truncated to the
    /// operand size. A register operand is #UD.
    /// </summary>
    private void ExecuteLea64()
    {
        LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
        if (operand.IsRegister)
        {
            throw new CpuException(6);
        }

        int operandBytes = OperandBytes64;
        WriteRegister64(operand.Reg, operandBytes, EffectiveOffset(operand));
    }

    /// <summary>
    /// 63h with REX.W: MOVSXD r64, r/m32, sign-extending a doubleword. 63h was
    /// ARPL outside 64-bit mode. Without REX.W it moves 32 (or 16) bits
    /// unextended, which the SDM discourages but defines.
    /// </summary>
    private void ExecuteMovsxd()
    {
        LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
        int operandBytes = OperandBytes64;

        if (operandBytes == 8)
        {
            uint source = (uint)ReadOperand64(operand, 4);
            WriteRegister64(operand.Reg, 8, (ulong)(long)(int)source);
            return;
        }

        ulong value = ReadOperand64(operand, operandBytes);
        WriteRegister64(operand.Reg, operandBytes, value);
    }

    /// <summary>90h-97h: XCHG reg, rAX, with REX.B reaching R8-R15.</summary>
    private void ExchangeWithAccumulator64(byte opcode)
    {
        const int Accumulator = 0;
        int register = (opcode & 7) | (_longPrefixes.RexB ? 8 : 0);
        int operandBytes = OperandBytes64;

        ulong accumulatorValue = ReadRegister64(Accumulator, operandBytes);
        ulong registerValue = ReadRegister64(register, operandBytes);
        WriteRegister64(Accumulator, operandBytes, registerValue);
        WriteRegister64(register, operandBytes, accumulatorValue);
    }
}
