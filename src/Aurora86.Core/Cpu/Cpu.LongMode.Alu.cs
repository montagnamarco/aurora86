// 64-bit mode's integer arithmetic and logic instructions (plans/long-mode.md,
// stage C2a): the eight ALU operations, TEST, the F6/F7, FE and FF groups,
// the immediate and register-to-register forms of IMUL, the shift and
// rotate group, SHLD/SHRD, the bit-test and bit-scan instructions, and the
// 486's read-modify-write pair XADD/CMPXCHG together with CMPXCHG8B and
// CMPXCHG16B.
//
// Cpu.LongMode.Execute.cs (the owner's dispatch tables) calls into
// TryExecuteAlu64 and TryExecuteAluTwoByte64 for whatever opcode its own
// switch does not recognise, and into LockAllowedAlu64 to decide whether a
// LOCK prefix is legal in front of it. This file never touches Execute64 or
// ExecuteTwoByte64 itself.
//
// Flags are computed eagerly rather than through LazyFlags' pending-operation
// scheme: LazyFlags.Left/Right/Result are only 32 bits wide, and stage C's
// own design note (plans/long-mode.md) chose not to widen them for the sake
// of a mode DOS and Windows 3.x never run. SetArithmeticFlags64 below
// reproduces the same formulas LazyFlags.Materialize computes -- CF, PF, AF,
// ZF, SF, OF, including the SDM-undefined ones this emulator has already
// picked a repeatable answer for -- generalised from 32 to 64 bits with
// ulong/Int128/UInt128 arithmetic, and leaves LazyFlags' own record in its
// "already materialized" (Op == None) state.

using System.Numerics;

namespace Aurora86.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>The four operations of the 64-bit bit-test group, in reg-field order (mirrors Cpu.TwoByte.cs's own BitOp).</summary>
    private enum BitOp64 { Test = 0, Set = 1, Reset = 2, Complement = 3 }

    // === dispatch entry points, called from Cpu.LongMode.Execute.cs =========

    /// <summary>
    /// The one-byte ALU opcodes of 64-bit mode. Returns false for anything it
    /// does not handle, so the caller's default branch can raise #UD -- which
    /// is also the right answer for 82h (ARPL/the undocumented CMP alias),
    /// absent from 64-bit mode entirely.
    /// </summary>
    private bool TryExecuteAlu64(byte opcode)
    {
        // 00h-3Dh: eight ALU operations (bits 3-5 of the opcode) in six forms
        // each (the low three bits) -- the same layout Cpu.Execute.cs uses for
        // the legacy decoder. Forms 6 and 7 of each block are segment
        // push/pop or a BCD adjust, none of which exist in 64-bit mode, so
        // they fall through to "not handled" here.
        if (opcode < 0x3E)
        {
            int form = opcode & 0x07;
            if (form <= 5)
            {
                int operation = (opcode >> 3) & 0x07;
                ExecuteAluForm64(operation, form);
                return true;
            }

            return false;
        }

        switch (opcode)
        {
            case 0x80: ExecuteGroup1Alu64(operandBytes: 1, signExtendImmediate: false); return true;
            case 0x81: ExecuteGroup1Alu64(operandBytes: OperandBytes64, signExtendImmediate: false); return true;
            case 0x83: ExecuteGroup1Alu64(operandBytes: OperandBytes64, signExtendImmediate: true); return true;

            case 0x84: ExecuteTest64(operandBytes: 1); return true;
            case 0x85: ExecuteTest64(operandBytes: OperandBytes64); return true;
            case 0xA8: ExecuteTestAccumulator64(operandBytes: 1); return true;
            case 0xA9: ExecuteTestAccumulator64(operandBytes: OperandBytes64); return true;

            case 0xF6: ExecuteGroup3_64(operandBytes: 1); return true;
            case 0xF7: ExecuteGroup3_64(operandBytes: OperandBytes64); return true;

            case 0xFE: ExecuteGroup4_64(); return true;
            case 0xFF: return TryExecuteGroup5IncDec64();

            case 0x69: ExecuteImulImmediate64(immediateIsByte: false); return true;
            case 0x6B: ExecuteImulImmediate64(immediateIsByte: true); return true;

            case 0xC0: ExecuteShift64(operandBytes: 1, countIsImmediate: true, countIsOne: false); return true;
            case 0xC1: ExecuteShift64(operandBytes: OperandBytes64, countIsImmediate: true, countIsOne: false); return true;
            case 0xD0: ExecuteShift64(operandBytes: 1, countIsImmediate: false, countIsOne: true); return true;
            case 0xD1: ExecuteShift64(operandBytes: OperandBytes64, countIsImmediate: false, countIsOne: true); return true;
            case 0xD2: ExecuteShift64(operandBytes: 1, countIsImmediate: false, countIsOne: false); return true;
            case 0xD3: ExecuteShift64(operandBytes: OperandBytes64, countIsImmediate: false, countIsOne: false); return true;

            default: return false;
        }
    }

    /// <summary>The 0Fh ALU opcodes of 64-bit mode: double shifts, bit test/scan, IMUL r,r/m, XADD, CMPXCHG and CMPXCHG8B/16B.</summary>
    private bool TryExecuteAluTwoByte64(byte opcode)
    {
        switch (opcode)
        {
            case 0xA4: ExecuteShiftDouble64(left: true, countIsImmediate: true); return true;
            case 0xA5: ExecuteShiftDouble64(left: true, countIsImmediate: false); return true;
            case 0xAC: ExecuteShiftDouble64(left: false, countIsImmediate: true); return true;
            case 0xAD: ExecuteShiftDouble64(left: false, countIsImmediate: false); return true;

            case 0xA3: ExecuteBitOperation64(BitOp64.Test); return true;
            case 0xAB: ExecuteBitOperation64(BitOp64.Set); return true;
            case 0xB3: ExecuteBitOperation64(BitOp64.Reset); return true;
            case 0xBB: ExecuteBitOperation64(BitOp64.Complement); return true;

            case 0xBA:
            {
                // The immediate form's reg field selects the operation the
                // same way the /r form's opcode byte does; values 0-3 are
                // undefined (BT itself has no immediate encoding), so this
                // returns false and lets the caller's default branch raise
                // #UD rather than picking a meaning for them.
                int reg = PeekModRmReg64();
                if (reg < 4) return false;
                ExecuteBitOperationImmediate64((BitOp64)(reg - 4));
                return true;
            }

            case 0xBC: ExecuteBitScan64(forward: true); return true;
            case 0xBD: ExecuteBitScan64(forward: false); return true;

            case 0xAF: ExecuteImulRegisterOperand64(); return true;

            case 0xC0: RequireFeature(CpuFeature.Isa486); ExecuteXadd64(operandBytes: 1); return true;
            case 0xC1: RequireFeature(CpuFeature.Isa486); ExecuteXadd64(operandBytes: OperandBytes64); return true;

            case 0xB0: RequireFeature(CpuFeature.Isa486); ExecuteCmpxchg64(operandBytes: 1); return true;
            case 0xB1: RequireFeature(CpuFeature.Isa486); ExecuteCmpxchg64(operandBytes: OperandBytes64); return true;

            case 0xC7:
            {
                // Only /1 exists at this opcode (CMPXCHG8B/16B); every other
                // reg field, including the register form (the F00F erratum's
                // trigger byte on real P5 silicon), is left to the caller's
                // default #UD, exactly like the legacy path's own C7 case
                // (Cpu.TwoByte.cs).
                int reg = PeekModRmReg64();
                if (reg != 1) return false;
                if (_longPrefixes.RexW)
                {
                    RequireFeature(CpuFeature.Cx16);
                    ExecuteCmpxchg16B64();
                }
                else
                {
                    RequireFeature(CpuFeature.Cx8);
                    ExecuteCmpxchg8B64();
                }

                return true;
            }

            default: return false;
        }
    }

    /// <summary>
    /// Whether LOCK is legal in front of this opcode, the 64-bit counterpart
    /// of Cpu.Execute.cs's LockAllowed/LockAllowedTwoByte: only the
    /// instructions that write back to the memory they just read, on the
    /// SDM's own list. By the time this runs the opcode (and, for the 0Fh
    /// table, the second opcode byte) is already consumed, so the ModR/M
    /// byte -- not yet read -- sits at the current RIP and is only peeked.
    /// </summary>
    private bool LockAllowedAlu64(byte opcode, bool twoByte)
    {
        if (!twoByte)
        {
            // The ALU operations' r/m-destination forms (0 and 1 of each
            // eight-opcode block), CMP excluded because it writes nothing
            // back: opcodes 38h-3Dh fall outside this range already.
            if (opcode < 0x38 && (opcode & 0x07) <= 1) return PeekModRmIsMemory64();

            return opcode switch
            {
                0x80 or 0x81 or 0x83 => PeekModRmIsMemory64() && PeekModRmReg64() != 7,   // ALU imm, CMP excluded
                0xF6 or 0xF7 => PeekModRmIsMemory64() && PeekModRmReg64() is 2 or 3,       // NOT, NEG only
                0xFE or 0xFF => PeekModRmIsMemory64() && PeekModRmReg64() is 0 or 1,       // INC, DEC only
                _ => false,
            };
        }

        return opcode switch
        {
            0xAB or 0xB3 or 0xBB => PeekModRmIsMemory64(),               // BTS, BTR, BTC (not BT)
            0xBA => PeekModRmIsMemory64() && PeekModRmReg64() >= 5,      // the group, minus BT (4)
            0xB0 or 0xB1 or 0xC0 or 0xC1 => PeekModRmIsMemory64(),       // CMPXCHG, XADD
            0xC7 => PeekModRmIsMemory64() && PeekModRmReg64() == 1,      // CMPXCHG8B/16B
            _ => false,
        };
    }

    // === peeking the ModR/M byte without consuming it =========================

    /// <summary>
    /// Reads the byte at the current RIP as an instruction fetch would --
    /// canonical check, execute-permission page walk -- but without advancing
    /// RIP, so LOCK's legality (and, for the F6/F7 group, whether an
    /// immediate follows) can be decided before the real decode consumes the
    /// ModR/M byte.
    /// </summary>
    private byte PeekModRmByte64()
    {
        ulong rip = State.Rip;
        if (rip - _longInstructionStart >= MaximumInstructionLength || !IsCanonical(rip))
        {
            throw new CpuException(13, 0);
        }

        uint physical = TranslateLongMode(rip, write: false, asSupervisor: false, execute: true);
        return _memory.ReadByte(physical);
    }

    private bool PeekModRmIsMemory64() => (PeekModRmByte64() >> 6) != 3;

    private int PeekModRmReg64() => (PeekModRmByte64() >> 3) & 0x07;

    // === the eight ALU operations (ADD, OR, ADC, SBB, AND, SUB, XOR, CMP) =====

    /// <summary>
    /// One of the six forms an ALU opcode can take, the same layout as
    /// Cpu.Ops.cs's ExecuteAluGroup: 0 r/m8,r8  1 r/m,r  2 r8,r/m8  3 r,r/m
    /// 4 AL,imm8  5 rAX,imm. Every form computes the result, writes it (a
    /// memory destination first, so a fault there leaves the flags exactly
    /// as they were -- the state a faulting instruction has to leave, per
    /// AGENTS.md's critical invariants), and only then commits the flags.
    /// </summary>
    private void ExecuteAluForm64(int operation, int form)
    {
        bool isCompare = operation == 7;

        switch (form)
        {
            case 0:
            {
                LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
                ulong left = ReadOperand64(operand, 1);
                ulong right = ReadRegister64(operand.Reg, 1);
                ulong result = AluOperate64(operation, left, right, out FlagOp flagOp, out bool carryIn);
                if (!isCompare) WriteOperand64(operand, 1, result);
                SetArithmeticFlags64(flagOp, left, right, result, 8, carryIn);
                return;
            }

            case 1:
            {
                int operandBytes = OperandBytes64;
                LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
                ulong left = ReadOperand64(operand, operandBytes);
                ulong right = ReadRegister64(operand.Reg, operandBytes);
                ulong result = AluOperate64(operation, left, right, out FlagOp flagOp, out bool carryIn);
                if (!isCompare) WriteOperand64(operand, operandBytes, result);
                SetArithmeticFlags64(flagOp, left, right, result, operandBytes * 8, carryIn);
                return;
            }

            case 2:
            {
                LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
                ulong left = ReadRegister64(operand.Reg, 1);
                ulong right = ReadOperand64(operand, 1);
                ulong result = AluOperate64(operation, left, right, out FlagOp flagOp, out bool carryIn);
                if (!isCompare) WriteRegister64(operand.Reg, 1, result);
                SetArithmeticFlags64(flagOp, left, right, result, 8, carryIn);
                return;
            }

            case 3:
            {
                int operandBytes = OperandBytes64;
                LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
                ulong left = ReadRegister64(operand.Reg, operandBytes);
                ulong right = ReadOperand64(operand, operandBytes);
                ulong result = AluOperate64(operation, left, right, out FlagOp flagOp, out bool carryIn);
                if (!isCompare) WriteRegister64(operand.Reg, operandBytes, result);
                SetArithmeticFlags64(flagOp, left, right, result, operandBytes * 8, carryIn);
                return;
            }

            case 4:
            {
                ulong left = ReadRegister64(0, 1);
                ulong right = FetchByte64();
                ulong result = AluOperate64(operation, left, right, out FlagOp flagOp, out bool carryIn);
                if (!isCompare) WriteRegister64(0, 1, result);
                SetArithmeticFlags64(flagOp, left, right, result, 8, carryIn);
                return;
            }

            default:
            {
                int operandBytes = OperandBytes64;
                ulong left = ReadRegister64(0, operandBytes);
                ulong right = FetchImmediate64(operandBytes);   // sign-extended imm32 for a 64-bit operand
                ulong result = AluOperate64(operation, left, right, out FlagOp flagOp, out bool carryIn);
                if (!isCompare) WriteRegister64(0, operandBytes, result);
                SetArithmeticFlags64(flagOp, left, right, result, operandBytes * 8, carryIn);
                return;
            }
        }
    }

    /// <summary>Groups 80h/81h/83h: an ALU operation, chosen by the ModR/M reg field, against an immediate. The write (if any) happens before the flags are committed, for the same fault-safety reason as ExecuteAluForm64.</summary>
    private void ExecuteGroup1Alu64(int operandBytes, bool signExtendImmediate)
    {
        int immediateBytes = signExtendImmediate ? 1 : operandBytes switch { 1 => 1, 2 => 2, _ => 4 };
        LongModeOperand operand = DecodeModRm64(immediateBytes);

        // The reg field is an opcode extension here, not a register: REX.R
        // has no defined meaning on it (SDM Vol.2 §2.2.1.2), so only its
        // three raw bits are read, exactly as the owner's MOV imm/8 group
        // (Cpu.LongMode.Execute.cs's MoveImmediateToOperand64) already does.
        int operation = operand.Reg & 0x07;
        bool isCompare = operation == 7;

        ulong immediate = signExtendImmediate
            ? (ulong)(long)(sbyte)FetchByte64()
            : FetchImmediate64(operandBytes);

        ulong left = ReadOperand64(operand, operandBytes);
        ulong result = AluOperate64(operation, left, immediate, out FlagOp flagOp, out bool carryIn);
        if (!isCompare) WriteOperand64(operand, operandBytes, result);
        SetArithmeticFlags64(flagOp, left, immediate, result, operandBytes * 8, carryIn);
    }

    /// <summary>84h/85h: TEST r/m, reg -- like AND, but the result is thrown away.</summary>
    private void ExecuteTest64(int operandBytes)
    {
        LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
        ulong left = ReadOperand64(operand, operandBytes);
        ulong right = ReadRegister64(operand.Reg, operandBytes);
        SetLogicFlags64(left & right, operandBytes * 8);
    }

    /// <summary>A8h/A9h: TEST AL/rAX, imm.</summary>
    private void ExecuteTestAccumulator64(int operandBytes)
    {
        ulong left = ReadRegister64(0, operandBytes);
        ulong right = operandBytes == 1 ? FetchByte64() : FetchImmediate64(operandBytes);
        SetLogicFlags64(left & right, operandBytes * 8);
    }

    /// <summary>
    /// The ALU core: computes the result and reports back which flag formula
    /// applies and what the incoming carry was, without touching the stored
    /// flags itself. The caller commits them with SetArithmeticFlags64 only
    /// after any write the result feeds has already succeeded, so a fault on
    /// that write leaves the flags exactly as they were.
    /// </summary>
    private ulong AluOperate64(int operation, ulong left, ulong right, out FlagOp flagOp, out bool carryIn)
    {
        carryIn = false;
        ulong result;

        switch (operation)
        {
            case 0: result = left + right; flagOp = FlagOp.Add; break;
            case 1: result = left | right; flagOp = FlagOp.Logic; break;
            case 2:
                carryIn = Flags.Carry;
                result = left + right + (carryIn ? 1UL : 0UL);
                flagOp = FlagOp.Adc;
                break;
            case 3:
                carryIn = Flags.Carry;
                result = left - right - (carryIn ? 1UL : 0UL);
                flagOp = FlagOp.Sbb;
                break;
            case 4: result = left & right; flagOp = FlagOp.Logic; break;
            case 5: result = left - right; flagOp = FlagOp.Sub; break;
            case 6: result = left ^ right; flagOp = FlagOp.Logic; break;
            default: result = left - right; flagOp = FlagOp.Sub; break;   // CMP
        }

        return result;
    }

    // === F6h/F7h (group 3), FEh (group 4), FFh /0 /1 (group 5) ================

    /// <summary>Group 3: TEST imm, NOT, NEG, MUL, IMUL, DIV, IDIV.</summary>
    private void ExecuteGroup3_64(int operandBytes)
    {
        // Only TEST (reg 0 or its undocumented alias 1) carries an immediate;
        // the ModR/M has to know that before it resolves a RIP-relative
        // operand, so the reg field is peeked first.
        int peekedReg = PeekModRmReg64();
        bool hasImmediate = peekedReg is 0 or 1;
        int immediateBytes = operandBytes switch { 1 => 1, 2 => 2, _ => 4 };
        int sizeBits = operandBytes * 8;

        LongModeOperand operand = DecodeModRm64(hasImmediate ? immediateBytes : 0);

        if (hasImmediate)
        {
            // The immediate is part of the instruction's own encoding, fetched
            // from code during decode; the r/m operand is a data read that
            // belongs to execution and has to come after it, exactly as real
            // hardware decodes the whole instruction before touching data
            // memory.
            ulong immediate = operandBytes == 1 ? FetchByte64() : FetchImmediate64(operandBytes);
            ulong testValue = ReadOperand64(operand, operandBytes);
            SetLogicFlags64(testValue & immediate, sizeBits);
            return;
        }

        int reg = operand.Reg & 0x07;
        ulong value = ReadOperand64(operand, operandBytes);

        switch (reg)
        {
            case 2:   // NOT: touches no flags at all
                WriteOperand64(operand, operandBytes, ~value);
                return;

            case 3:   // NEG: write before the flags are committed (fault safety)
            {
                ulong result = 0UL - value;
                WriteOperand64(operand, operandBytes, result);
                SetArithmeticFlags64(FlagOp.Neg, value, 0, result, sizeBits);
                return;
            }

            case 4: ExecuteMul64(operandBytes, value, signed: false); return;
            case 5: ExecuteMul64(operandBytes, value, signed: true); return;
            case 6: ExecuteDiv64(operandBytes, value, signed: false); return;
            default: ExecuteDiv64(operandBytes, value, signed: true); return;
        }
    }

    /// <summary>Group 4 (FEh): INC/DEC on a byte. Any other reg field is #UD.</summary>
    private void ExecuteGroup4_64()
    {
        LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
        int reg = operand.Reg & 0x07;
        if (reg is not (0 or 1)) throw new CpuException(6);

        ulong value = ReadOperand64(operand, 1);
        ulong result = reg == 0 ? value + 1 : value - 1;
        WriteOperand64(operand, 1, result);
        SetArithmeticFlags64(reg == 0 ? FlagOp.Inc : FlagOp.Dec, value, 1, result, 8);
    }

    /// <summary>
    /// FFh /0 /1: INC/DEC r/m16/32/64. The other FFh forms (near/far CALL,
    /// near/far JMP, PUSH) belong to the owner's dispatch table, so this
    /// peeks the reg field and returns false rather than decoding anything
    /// when it does not name INC or DEC.
    /// </summary>
    private bool TryExecuteGroup5IncDec64()
    {
        int reg = PeekModRmReg64();
        if (reg != 0 && reg != 1) return false;

        int operandBytes = OperandBytes64;
        LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
        ulong value = ReadOperand64(operand, operandBytes);
        ulong result = reg == 0 ? value + 1 : value - 1;
        WriteOperand64(operand, operandBytes, result);
        SetArithmeticFlags64(reg == 0 ? FlagOp.Inc : FlagOp.Dec, value, 1, result, operandBytes * 8);
        return true;
    }

    // === IMUL: 69h/6Bh (immediate), 0F AFh (register) ==========================

    /// <summary>69h (imm16/32) and 6Bh (imm8, sign-extended): IMUL reg, r/m, imm. DX:AX/EDX:EAX/RDX:RAX are left alone -- the whole result goes into reg, and CF/OF report a truncation.</summary>
    private void ExecuteImulImmediate64(bool immediateIsByte)
    {
        int operandBytes = OperandBytes64;
        int immediateBytes = immediateIsByte ? 1 : operandBytes switch { 2 => 2, _ => 4 };
        LongModeOperand operand = DecodeModRm64(immediateBytes);

        // The immediate is decoded from code, ahead of the data read from
        // the r/m operand, matching how real hardware finishes decoding an
        // instruction before it touches data memory.
        ulong immediate = immediateIsByte ? (ulong)(long)(sbyte)FetchByte64() : FetchImmediate64(operandBytes);
        ulong source = ReadOperand64(operand, operandBytes);

        ExecuteImulCore64(operand.Reg, source, immediate, operandBytes);
    }

    /// <summary>0F AFh: IMUL reg, r/m -- the same three-operand form, with the destination register standing in for the immediate.</summary>
    private void ExecuteImulRegisterOperand64()
    {
        int operandBytes = OperandBytes64;
        LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
        ulong source = ReadOperand64(operand, operandBytes);
        ulong destination = ReadRegister64(operand.Reg, operandBytes);

        ExecuteImulCore64(operand.Reg, source, destination, operandBytes);
    }

    private void ExecuteImulCore64(int destinationRegister, ulong left, ulong right, int operandBytes)
    {
        int sizeBits = operandBytes * 8;
        long signedLeft = SignExtendToLong(left, sizeBits);
        long signedRight = SignExtendToLong(right, sizeBits);
        Int128 product = (Int128)signedLeft * signedRight;

        ulong mask = MaskForBits64(sizeBits);
        ulong result = (ulong)((UInt128)product & mask);
        Int128 signExtendedResult = SignExtendToLong(result, sizeBits);
        bool overflow = product != signExtendedResult;

        WriteRegister64(destinationRegister, operandBytes, result);
        SetArithmeticFlags64(FlagOp.Imul, 0, overflow ? 1UL : 0UL, result, sizeBits);
    }

    // === shifts, rotates and the double-precision shifts =======================

    /// <summary>
    /// Group 2: ROL, ROR, RCL, RCR, SHL, SHR, SAL, SAR. The count is masked
    /// before use -- to five bits for an 8/16/32-bit operand, six for a
    /// 64-bit one, so a 64-bit register can be rotated or shifted by up to
    /// 63 (SDM Vol.2 Table 2-4) -- and a count of zero leaves the operand and
    /// every flag untouched, though the read still happens so a bad address
    /// still faults.
    /// </summary>
    private void ExecuteShift64(int operandBytes, bool countIsImmediate, bool countIsOne)
    {
        int immediateBytes = countIsImmediate ? 1 : 0;
        LongModeOperand operand = DecodeModRm64(immediateBytes);
        int sizeBits = operandBytes * 8;

        int count;
        if (countIsOne) count = 1;
        else if (countIsImmediate) count = FetchByte64();
        else count = (int)ReadRegister64(1, 1);   // CL

        count &= sizeBits == 64 ? 0x3F : 0x1F;

        ulong value = ReadOperand64(operand, operandBytes);
        if (count == 0) return;

        ulong mask = MaskForBits64(sizeBits);
        ulong signBit = SignBitFor64(sizeBits);
        ulong result;
        bool carry;
        bool? overflow = null;   // null: OF is left exactly as it was, the rule for every count other than 1
        int reg = operand.Reg & 0x07;
        bool isRotate = reg <= 3;

        switch (reg)
        {
            case 0:   // ROL
            {
                int rotate = count % sizeBits;
                result = rotate == 0 ? value : ((value << rotate) | (value >> (sizeBits - rotate))) & mask;
                carry = (result & 1) != 0;
                if (count == 1) overflow = ((result & signBit) != 0) != carry;
                break;
            }

            case 1:   // ROR
            {
                int rotate = count % sizeBits;
                result = rotate == 0 ? value : ((value >> rotate) | (value << (sizeBits - rotate))) & mask;
                carry = (result & signBit) != 0;
                if (count == 1)
                {
                    bool topBit = (result & signBit) != 0;
                    bool nextBit = (result & (signBit >> 1)) != 0;
                    overflow = topBit != nextBit;
                }
                break;
            }

            case 2:   // RCL: the rotation runs through the carry, so the cycle is sizeBits+1 long
            {
                int rotate = count % (sizeBits + 1);
                result = value;
                // The rotation reads and updates the carry on every step, but
                // only as a local value: CF itself must not change until the
                // write below has succeeded, so a fault on a memory
                // destination leaves it (and every other flag) untouched.
                bool runningCarry = Flags.Carry;
                for (int i = 0; i < rotate; i++)
                {
                    bool newCarry = (result & signBit) != 0;
                    result = ((result << 1) | (runningCarry ? 1UL : 0UL)) & mask;
                    runningCarry = newCarry;
                }

                carry = runningCarry;
                if (count == 1) overflow = ((result & signBit) != 0) != carry;
                break;
            }

            case 3:   // RCR
            {
                int rotate = count % (sizeBits + 1);
                result = value;
                bool runningCarry = Flags.Carry;
                if (count == 1) overflow = ((value & signBit) != 0) != runningCarry;
                for (int i = 0; i < rotate; i++)
                {
                    bool newCarry = (result & 1) != 0;
                    result = (result >> 1) | (runningCarry ? signBit : 0);
                    runningCarry = newCarry;
                }

                carry = runningCarry;
                break;
            }

            case 4:
            case 6:   // SHL and SAL are the same instruction, two encodings
            {
                result = (value << count) & mask;
                carry = count <= sizeBits && ((value >> (sizeBits - count)) & 1) != 0;
                if (count == 1) overflow = ((result & signBit) != 0) != carry;
                break;
            }

            case 5:   // SHR: logical shift, zeroes come in from the left
            {
                result = count >= sizeBits ? 0 : (value & mask) >> count;
                carry = count <= sizeBits && ((value >> (count - 1)) & 1) != 0;
                if (count == 1) overflow = (value & signBit) != 0;
                break;
            }

            default:  // SAR: arithmetic shift, the sign bit replicates; OF is always cleared
            {
                long signed = SignExtendToLong(value, sizeBits);
                int shift = Math.Min(count, sizeBits - 1);
                result = (ulong)(signed >> shift) & mask;
                carry = ((value >> Math.Min(count - 1, sizeBits - 1)) & 1) != 0;
                break;
            }
        }

        // The write happens before any flag is touched: on a faulting memory
        // destination, CF/OF (and, for a shift, ZF/SF/PF) must be exactly
        // what they were before this instruction, not partway updated.
        WriteOperand64(operand, operandBytes, result);

        // Rotates only ever touch CF (and OF at count == 1); shifts also
        // touch ZF/SF/PF, which SetLogicFlags64 computes and which clears CF
        // and OF as its baseline -- so it has to run before the shift's own
        // CF/OF are written over that baseline.
        if (!isRotate) SetLogicFlags64(result, sizeBits);
        Flags.Carry = carry;
        if (overflow.HasValue) Flags.Overflow = overflow.Value;
    }

    /// <summary>
    /// SHLD/SHRD (0F A4h/A5h/ACh/ADh), new with the 386: shifts the
    /// destination while feeding in bits from the reg operand instead of
    /// zeroes.
    /// </summary>
    private void ExecuteShiftDouble64(bool left, bool countIsImmediate)
    {
        int immediateBytes = countIsImmediate ? 1 : 0;
        LongModeOperand operand = DecodeModRm64(immediateBytes);
        int operandBytes = OperandBytes64;
        int sizeBits = operandBytes * 8;
        ulong source = ReadRegister64(operand.Reg, operandBytes);

        int count = countIsImmediate ? FetchByte64() : (int)ReadRegister64(1, 1);
        count &= sizeBits == 64 ? 0x3F : 0x1F;

        // The destination is read even with a count of zero: the operand is
        // still touched, so a bad address still faults.
        ulong destination = ReadOperand64(operand, operandBytes);
        if (count == 0) return;

        ulong mask = MaskForBits64(sizeBits);
        ulong signBit = SignBitFor64(sizeBits);
        ulong result;
        bool carry;
        bool overflow;

        if (sizeBits == 16 && count > 16)
        {
            // The only width whose 5-bit count mask can exceed the operand's
            // own size: on real 386 silicon the barrel shifter is 32 bits
            // wide, so with a count above 16 the destination has already
            // been shifted out entirely and both halves fill with the
            // source operand (Cpu.Shifts.cs's ShiftDouble reproduces the
            // same case for the legacy path; no 32- or 64-bit form can reach
            // here, since their own count masks never exceed their width).
            // The legacy path returns before ever computing OF in this
            // branch, so Logic's own baseline (OF cleared) is what stands --
            // verified against the 386EX hardware vectors.
            ulong doubled = ((source & 0xFFFF) << 16) | (source & 0xFFFF);
            ulong shifted = left ? (doubled << (count - 16)) >> 16
                                  : (doubled >> (count - 16)) & 0xFFFF;
            result = shifted & 0xFFFF;
            carry = left ? ((source >> (32 - count)) & 1) != 0
                         : ((source >> (count - 17)) & 1) != 0;
            overflow = false;
        }
        else
        {
            if (left)
            {
                result = ((destination << count) | (source >> (sizeBits - count))) & mask;
                carry = ((destination >> (sizeBits - count)) & 1) != 0;
            }
            else
            {
                result = ((destination >> count) | (source << (sizeBits - count))) & mask;
                carry = ((destination >> (count - 1)) & 1) != 0;
            }

            // OF is only SDM-defined for a one-bit shift; the legacy path's
            // own measurement against the 386EX hardware vectors
            // (Cpu.Shifts.cs) found it set on a sign change whatever the
            // count, so the same formula is applied here unconditionally
            // rather than only at count == 1 -- but only in this branch, not
            // the count-above-16 one above.
            overflow = ((result ^ destination) & signBit) != 0;
        }

        // The write happens before any flag is touched, so a faulting
        // memory destination leaves every flag exactly as it was.
        WriteOperand64(operand, operandBytes, result);

        SetLogicFlags64(result, sizeBits);
        Flags.Carry = carry;
        Flags.Overflow = overflow;
    }

    // === bit test (BT/BTS/BTR/BTC) and bit scan (BSF/BSR) ======================

    /// <summary>
    /// BT/BTS/BTR/BTC with the index in a register (0F A3h/ABh/B3h/BBh). On a
    /// memory operand the index is signed and not clamped to the operand
    /// width: it displaces the effective address by index/width words, so a
    /// long bit vector can be addressed with a single instruction.
    /// </summary>
    private void ExecuteBitOperation64(BitOp64 operation)
    {
        LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
        int operandBytes = OperandBytes64;
        int width = operandBytes * 8;
        long index = SignExtendToLong(ReadRegister64(operand.Reg, operandBytes), width);

        if (operand.IsRegister)
        {
            ulong value = ReadOperand64(operand, operandBytes);
            int bit = (int)(index & (width - 1));
            ApplyBit64(operand, operation, value, bit, operandBytes);
            return;
        }

        // width is always a power of two (16, 32 or 64), so an arithmetic
        // shift computes the same floor-towards-negative-infinity division a
        // negative index needs -- unlike index - width + 1, which silently
        // wraps for an index near long.MinValue at width 64 rather than
        // faulting or throwing, because C#'s integer arithmetic is unchecked
        // by default.
        int shift = width switch { 16 => 4, 32 => 5, _ => 6 };
        long word = index >> shift;
        int bitInWord = (int)(index & (width - 1));

        ulong displacedAddress = operand.Address + (ulong)(word * (width / 8));
        if (AddressSize32In64) displacedAddress = (uint)displacedAddress;

        LongModeOperand target = operand;
        target.Address = displacedAddress;

        ulong targetValue = ReadOperand64(target, operandBytes);
        ApplyBit64(target, operation, targetValue, bitInWord, operandBytes);
    }

    /// <summary>Group 0F BAh: the same four operations with an immediate index, which can never escape the operand.</summary>
    private void ExecuteBitOperationImmediate64(BitOp64 operation)
    {
        LongModeOperand operand = DecodeModRm64(immediateBytes: 1);
        int operandBytes = OperandBytes64;
        int width = operandBytes * 8;
        int bit = FetchByte64() & (width - 1);
        ulong value = ReadOperand64(operand, operandBytes);
        ApplyBit64(operand, operation, value, bit, operandBytes);
    }

    /// <summary>
    /// Copies the selected bit into CF and, for everything except Test,
    /// writes the operand back. The write (when there is one) happens before
    /// CF is committed, so a fault on a memory destination leaves CF alone.
    /// The other arithmetic flags are untouched either way.
    /// </summary>
    private void ApplyBit64(in LongModeOperand operand, BitOp64 operation, ulong value, int bit, int operandBytes)
    {
        ulong mask = 1UL << bit;
        bool carry = (value & mask) != 0;

        ulong updated = operation switch
        {
            BitOp64.Set => value | mask,
            BitOp64.Reset => value & ~mask,
            BitOp64.Complement => value ^ mask,
            _ => value,
        };

        if (operation != BitOp64.Test) WriteOperand64(operand, operandBytes, updated);
        Flags.Carry = carry;
    }

    /// <summary>
    /// BSF/BSR (0F BCh/BDh): the position of the first set bit, scanning
    /// from the bottom or the top. A zero operand leaves the destination
    /// unchanged and sets ZF instead of a position.
    /// </summary>
    private void ExecuteBitScan64(bool forward)
    {
        LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
        int operandBytes = OperandBytes64;
        ulong value = ReadOperand64(operand, operandBytes);

        if (value == 0)
        {
            // The SDM leaves the destination undefined here. Intel processors
            // still perform the 32-bit register write, so bits 63:32 of a
            // 32-bit destination are cleared while the low half keeps its
            // value; AMD leaves the register whole. This follows Intel, as
            // the note on BSF/BSR in Linux's arch/x86 bitops describes --
            // not measured in this repository. A 16- or 64-bit destination
            // has nothing further to clear.
            if (operandBytes == 4) WriteRegister64(operand.Reg, 4, ReadRegister64(operand.Reg, 4));

            Flags.Zero = true;
            return;
        }

        // ReadOperand64 already zero-extends the value to its own width, so
        // scanning it as a full 64-bit quantity gives the same position the
        // legacy 32-bit-specific formula computes (Cpu.TwoByte.cs's
        // BitScan): the extra leading zeros above the operand's real width
        // count for nothing, whether the scan reads from the bottom
        // (TrailingZeroCount) or is measured down from bit 63
        // (LeadingZeroCount).
        int position = forward
            ? BitOperations.TrailingZeroCount(value)
            : 63 - BitOperations.LeadingZeroCount(value);

        WriteRegister64(operand.Reg, operandBytes, (ulong)position);
        Flags.Zero = false;
    }

    // === MUL/IMUL/DIV/IDIV (group 3, reg fields 4-7) ============================

    /// <summary>
    /// MUL/IMUL r/m: multiplies the accumulator by the operand into a
    /// double-width register pair (AX; DX:AX; EDX:EAX; RDX:RAX). The wider
    /// forms use Int128/UInt128 so the full product is exact at every
    /// operand size, including 64 bits, where a plain ulong multiply would
    /// overflow silently.
    /// </summary>
    private void ExecuteMul64(int operandBytes, ulong value, bool signed)
    {
        if (operandBytes == 1)
        {
            ulong accumulator = ReadRegister64(0, 1);
            if (signed)
            {
                int result = (sbyte)accumulator * (sbyte)value;
                WriteRegister64(0, 2, (ulong)(ushort)result);
                bool overflow = result != (sbyte)result;
                SetArithmeticFlags64(FlagOp.Imul, 0, overflow ? 1UL : 0UL, (ulong)(ushort)result, 8);
            }
            else
            {
                uint result = (uint)(accumulator * value);
                WriteRegister64(0, 2, result);
                SetArithmeticFlags64(FlagOp.Mul, 0, result > 0xFF ? 1UL : 0UL, result, 8);
            }

            return;
        }

        int sizeBits = operandBytes * 8;
        ulong accumulatorValue = ReadRegister64(0, operandBytes);
        ulong mask = MaskForBits64(sizeBits);
        ulong resultLow;
        ulong resultHigh;
        bool overflow2;
        FlagOp flagOp;

        if (signed)
        {
            long signedLeft = SignExtendToLong(accumulatorValue, sizeBits);
            long signedRight = SignExtendToLong(value, sizeBits);
            Int128 product = (Int128)signedLeft * signedRight;
            UInt128 productBits = (UInt128)product;
            resultLow = (ulong)(productBits & mask);
            resultHigh = (ulong)((productBits >> sizeBits) & mask);
            Int128 signExtendedLow = SignExtendToLong(resultLow, sizeBits);
            overflow2 = product != signExtendedLow;
            flagOp = FlagOp.Imul;
        }
        else
        {
            UInt128 product = (UInt128)accumulatorValue * value;
            resultLow = (ulong)(product & mask);
            resultHigh = (ulong)((product >> sizeBits) & mask);
            overflow2 = resultHigh != 0;
            flagOp = FlagOp.Mul;
        }

        WriteRegister64(0, operandBytes, resultLow);
        WriteRegister64(2, operandBytes, resultHigh);
        SetArithmeticFlags64(flagOp, 0, overflow2 ? 1UL : 0UL, resultLow, sizeBits);
    }

    /// <summary>
    /// DIV/IDIV r/m: divides the register pair by the operand. A divisor of
    /// zero, or a quotient that overflows the destination, is #DE with no
    /// register written (SDM Vol.2 DIV/IDIV) -- DivideError (Cpu.Groups.cs)
    /// also leaves OF and ZF cleared, matching the legacy path's own
    /// measured behaviour.
    /// </summary>
    private void ExecuteDiv64(int operandBytes, ulong divisor, bool signed)
    {
        if (divisor == 0) throw DivideError();

        if (operandBytes == 1)
        {
            // AL and AH are implicit here -- register indices 0 and 4 of the
            // 8086's own accumulator, not a ModR/M-encoded r8 -- so they are
            // always AL/AH, whatever REX says. WriteRegister64's byte path
            // follows REX (SPL/BPL/SIL/DIL when one is present, matching the
            // r/m operand this instruction reads separately), which would
            // turn register 4 into SPL here; State.SetReg8 bypasses that and
            // always means AH.
            ushort dividendWord = (ushort)ReadRegister64(0, 2);
            if (signed)
            {
                short signedDividendWord = (short)dividendWord;
                sbyte signedDivisorByte = (sbyte)divisor;
                int quotient = signedDividendWord / signedDivisorByte;
                if (quotient > sbyte.MaxValue || quotient < sbyte.MinValue) throw DivideError();
                State.SetReg8(0, (byte)quotient);
                State.SetReg8(4, (byte)(signedDividendWord % signedDivisorByte));
            }
            else
            {
                byte unsignedDivisorByte = (byte)divisor;
                uint quotient = (uint)(dividendWord / unsignedDivisorByte);
                if (quotient > 0xFF) throw DivideError();
                State.SetReg8(0, (byte)quotient);
                State.SetReg8(4, (byte)(dividendWord % unsignedDivisorByte));
            }

            return;
        }

        int sizeBits = operandBytes * 8;
        ulong low = ReadRegister64(0, operandBytes);
        ulong high = ReadRegister64(2, operandBytes);
        UInt128 combined = ((UInt128)high << sizeBits) | low;

        if (!signed)
        {
            UInt128 unsignedDivisor = divisor & MaskForBits64(sizeBits);
            UInt128 quotient = combined / unsignedDivisor;
            if (quotient > MaskForBits64(sizeBits)) throw DivideError();
            UInt128 remainder = combined % unsignedDivisor;
            WriteRegister64(0, operandBytes, (ulong)quotient);
            WriteRegister64(2, operandBytes, (ulong)remainder);
            return;
        }

        int totalBits = sizeBits * 2;
        Int128 dividend = SignExtendInt128(combined, totalBits);
        long signedDivisor = SignExtendToLong(divisor, sizeBits);

        // dividend / -1 overflows Int128 arithmetic itself when dividend is
        // exactly Int128.MinValue -- only reachable when sizeBits == 64,
        // since a smaller sizeBits sign-extends into a strictly narrower
        // range. .NET's Int128 division throws OverflowException for that
        // one pair rather than producing a value, which would otherwise
        // escape Step64 uncaught instead of the #DE the SDM asks for; the
        // mathematical quotient (-Int128.MinValue) is out of every
        // sizeBits' signed range anyway, so #DE is the right answer.
        if (signedDivisor == -1 && dividend == Int128.MinValue) throw DivideError();

        Int128 quotient128 = dividend / signedDivisor;
        Int128 remainder128 = dividend % signedDivisor;

        Int128 minQuotient = sizeBits == 64 ? long.MinValue : -((Int128)1 << (sizeBits - 1));
        Int128 maxQuotient = sizeBits == 64 ? long.MaxValue : ((Int128)1 << (sizeBits - 1)) - 1;
        if (quotient128 < minQuotient || quotient128 > maxQuotient) throw DivideError();

        WriteRegister64(0, operandBytes, (ulong)(long)quotient128);
        WriteRegister64(2, operandBytes, (ulong)(long)remainder128);
    }

    // === the 486's read-modify-write pair: XADD and CMPXCHG =====================

    /// <summary>
    /// 0F C0h/C1h: exchange and add. The destination gets the sum, the
    /// source register the destination's old value, and the flags come from
    /// the addition. A memory destination is written before the source
    /// register, so a fault there leaves the register untouched; a register
    /// destination follows the SDM's own order instead (SRC = DEST, then
    /// DEST = TEMP), which is what gets XADD reg, reg right when both
    /// operands name the same register -- the sum has to be the value left
    /// standing.
    /// </summary>
    private void ExecuteXadd64(int operandBytes)
    {
        LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
        int sizeBits = operandBytes * 8;
        ulong destination = ReadOperand64(operand, operandBytes);
        ulong source = ReadRegister64(operand.Reg, operandBytes);
        ulong sum = destination + source;

        if (operand.IsRegister)
        {
            WriteRegister64(operand.Reg, operandBytes, destination);
            WriteRegister64(operand.Register, operandBytes, sum);
        }
        else
        {
            WriteOperand64(operand, operandBytes, sum);
            WriteRegister64(operand.Reg, operandBytes, destination);
        }

        SetArithmeticFlags64(FlagOp.Add, destination, source, sum, sizeBits);
    }

    /// <summary>
    /// 0F B0h/B1h: compares the accumulator with the destination and, only
    /// on a match, stores the source there; on a mismatch the accumulator is
    /// loaded with what was actually found. The SDM's own operation listing
    /// writes the destination back either way (DEST = TEMP in the mismatch
    /// branch too), because real silicon performs this as one locked
    /// read-modify-write regardless of the outcome -- which is why a
    /// read-only page faults on a losing CMPXCHG exactly as it does on a
    /// winning one, and why the write happens before the flags are
    /// committed.
    /// </summary>
    private void ExecuteCmpxchg64(int operandBytes)
    {
        LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
        int sizeBits = operandBytes * 8;
        ulong destination = ReadOperand64(operand, operandBytes);
        ulong accumulator = ReadRegister64(0, operandBytes);
        ulong difference = accumulator - destination;
        bool equal = accumulator == destination;

        // The write-back on a mismatch is a memory bus cycle; a register
        // destination is left untouched then, not even zero-extended at 32
        // bits (QEMU's "fix cmpxchg with 32-bit register destination" and its
        // tests/tcg/x86_64 cmpxchg test; independent review of C2a).
        if (equal || !operand.IsRegister)
        {
            ulong stored = equal ? ReadRegister64(operand.Reg, operandBytes) : destination;
            WriteOperand64(operand, operandBytes, stored);
        }

        if (!equal)
        {
            WriteRegister64(0, operandBytes, destination);
        }

        SetArithmeticFlags64(FlagOp.Sub, accumulator, destination, difference, sizeBits);
    }

    /// <summary>
    /// 0F C7h /1, without REX.W: CMPXCHG8B. Compares EDX:EAX with the 64-bit
    /// destination; equal stores ECX:EBX and sets ZF, not equal loads
    /// EDX:EAX with the value found and clears ZF. The destination is
    /// written in both branches, matching the SDM's own operation listing
    /// (Cpu.Pentium.cs's ExecuteCmpXchg8B carries the same rule for the
    /// legacy path), which is why a read-only page faults even on a
    /// mismatch. No alignment check applies here yet: on real silicon it is
    /// gated by CR0.AM and CPL 3, and 64-bit mode's CPL is stage D's
    /// (plans/long-mode.md, the C1 review notes).
    /// </summary>
    private void ExecuteCmpxchg8B64()
    {
        LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
        if (operand.IsRegister) throw new CpuException(6);

        ulong destination = ReadMemory64(operand.Segment, EffectiveOffset(operand), 8);
        ulong compare = ReadRegister64(0, 4) | (ReadRegister64(2, 4) << 32);
        bool equal = destination == compare;
        ulong stored = equal ? ReadRegister64(3, 4) | (ReadRegister64(1, 4) << 32) : destination;

        WriteMemory64(operand.Segment, EffectiveOffset(operand), 8, stored);
        Flags.Zero = equal;

        if (!equal)
        {
            WriteRegister64(0, 4, (uint)destination);
            WriteRegister64(2, 4, (uint)(destination >> 32));
        }
    }

    /// <summary>
    /// 0F C7h /1, with REX.W: CMPXCHG16B. The same comparison at 128 bits,
    /// against RDX:RAX, storing RCX:RBX on a match. The SDM requires the
    /// destination to be 16-byte aligned unconditionally -- unlike
    /// CMPXCHG8B's alignment check, this one is not gated by CR0.AM -- so a
    /// misaligned address is #GP(0) before anything is read; a 16-byte
    /// aligned access within a 4 KB page never crosses into a second page
    /// (4096 is a multiple of 16), so the two qword halves can be read and
    /// written independently without the manual page-splitting CMPXCHG8B's
    /// legacy implementation needs for an unaligned access.
    /// </summary>
    private void ExecuteCmpxchg16B64()
    {
        LongModeOperand operand = DecodeModRm64(immediateBytes: 0);
        if (operand.IsRegister) throw new CpuException(6);

        ulong linear = LinearAddress64(operand.Segment, EffectiveOffset(operand), 16);
        if ((linear & 0xF) != 0) throw new CpuException(13, 0);

        ulong low = ReadMemory64(operand.Segment, EffectiveOffset(operand), 8);
        ulong high = ReadMemory64(operand.Segment, EffectiveOffset(operand) + 8, 8);
        UInt128 destination = ((UInt128)high << 64) | low;
        UInt128 compare = ((UInt128)ReadRegister64(2, 8) << 64) | ReadRegister64(0, 8);
        bool equal = destination == compare;
        UInt128 stored = equal
            ? ((UInt128)ReadRegister64(1, 8) << 64) | ReadRegister64(3, 8)
            : destination;

        WriteMemory64(operand.Segment, EffectiveOffset(operand), 8, (ulong)stored);
        WriteMemory64(operand.Segment, EffectiveOffset(operand) + 8, 8, (ulong)(stored >> 64));
        Flags.Zero = equal;

        if (!equal)
        {
            WriteRegister64(0, 8, (ulong)destination);
            WriteRegister64(2, 8, (ulong)(destination >> 64));
        }
    }

    // === eager flags and small arithmetic helpers ===============================

    /// <summary>
    /// Computes CF, PF, AF, ZF, SF and OF for one arithmetic or logic result
    /// and writes them straight into the stored flags, reproducing the
    /// formulas LazyFlags.Materialize uses (LazyFlags.cs) at whatever width
    /// this instruction actually has -- including 64 bits, which
    /// LazyFlags' own 32-bit Left/Right/Result fields cannot hold.
    /// Materialize runs first so any flags a legacy-style operation left
    /// pending are resolved before the mask below touches Stored, and the
    /// lazy record is left in its "already materialized" state throughout,
    /// per the plan's choice not to widen LazyFlags itself.
    /// </summary>
    private void SetArithmeticFlags64(FlagOp op, ulong left, ulong right, ulong result, int sizeBits, bool carryIn = false)
    {
        Flags.Materialize();

        ulong mask = MaskForBits64(sizeBits);
        ulong sign = SignBitFor64(sizeBits);
        ulong maskedResult = result & mask;
        ulong maskedLeft = left & mask;
        ulong maskedRight = right & mask;

        Flags flags = Flags.Stored & ~(Aurora86.Core.Cpu.Flags.Carry | Aurora86.Core.Cpu.Flags.Parity
            | Aurora86.Core.Cpu.Flags.Adjust | Aurora86.Core.Cpu.Flags.Zero | Aurora86.Core.Cpu.Flags.Sign
            | Aurora86.Core.Cpu.Flags.Overflow);

        if (maskedResult == 0) flags |= Aurora86.Core.Cpu.Flags.Zero;
        if ((maskedResult & sign) != 0) flags |= Aurora86.Core.Cpu.Flags.Sign;
        if ((BitOperations.PopCount(maskedResult & 0xFF) & 1) == 0) flags |= Aurora86.Core.Cpu.Flags.Parity;

        switch (op)
        {
            case FlagOp.Add:
            case FlagOp.Adc:
            {
                bool carry = carryIn ? maskedResult <= maskedLeft : maskedResult < maskedLeft;
                if (carry) flags |= Aurora86.Core.Cpu.Flags.Carry;
                if (((maskedLeft ^ maskedResult) & (maskedRight ^ maskedResult) & sign) != 0) flags |= Aurora86.Core.Cpu.Flags.Overflow;
                if (((maskedLeft ^ maskedRight ^ maskedResult) & 0x10) != 0) flags |= Aurora86.Core.Cpu.Flags.Adjust;
                break;
            }

            case FlagOp.Sub:
            case FlagOp.Sbb:
            {
                bool carry = carryIn ? maskedLeft <= maskedRight : maskedLeft < maskedRight;
                if (carry) flags |= Aurora86.Core.Cpu.Flags.Carry;
                if (((maskedLeft ^ maskedRight) & (maskedLeft ^ maskedResult) & sign) != 0) flags |= Aurora86.Core.Cpu.Flags.Overflow;
                if (((maskedLeft ^ maskedRight ^ maskedResult) & 0x10) != 0) flags |= Aurora86.Core.Cpu.Flags.Adjust;
                break;
            }

            case FlagOp.Logic:
                // AND/OR/XOR/TEST clear CF and OF, which the mask above
                // already did; AF is architecturally undefined here and left
                // at zero, matching LazyFlags.Materialize's own choice.
                break;

            case FlagOp.Inc:
                flags |= Flags.Stored & Aurora86.Core.Cpu.Flags.Carry;   // INC preserves CF
                if (maskedResult == sign) flags |= Aurora86.Core.Cpu.Flags.Overflow;
                if ((maskedResult & 0x0F) == 0) flags |= Aurora86.Core.Cpu.Flags.Adjust;
                break;

            case FlagOp.Dec:
                flags |= Flags.Stored & Aurora86.Core.Cpu.Flags.Carry;   // DEC preserves CF
                if (maskedResult == (sign - 1)) flags |= Aurora86.Core.Cpu.Flags.Overflow;
                if ((maskedResult & 0x0F) == 0x0F) flags |= Aurora86.Core.Cpu.Flags.Adjust;
                break;

            case FlagOp.Neg:
                if (maskedLeft != 0) flags |= Aurora86.Core.Cpu.Flags.Carry;
                if (maskedResult == sign) flags |= Aurora86.Core.Cpu.Flags.Overflow;
                if ((maskedResult & 0x0F) != 0) flags |= Aurora86.Core.Cpu.Flags.Adjust;
                break;

            case FlagOp.Mul:
            case FlagOp.Imul:
                // CF and OF are set together; the caller has already decided,
                // passing 0 or 1 in right, whether the result overflowed the
                // low half. ZF/SF/PF are undefined on real silicon after a
                // multiply -- the common code above computes them from
                // result regardless, matching what LazyFlags.Materialize
                // does for the legacy path.
                if (right != 0) flags |= Aurora86.Core.Cpu.Flags.Carry | Aurora86.Core.Cpu.Flags.Overflow;
                break;
        }

        Flags.Stored = flags;
    }

    private void SetLogicFlags64(ulong result, int sizeBits) => SetArithmeticFlags64(FlagOp.Logic, 0, 0, result, sizeBits);

    private static ulong MaskForBits64(int sizeBits) => sizeBits switch
    {
        8 => 0xFFUL,
        16 => 0xFFFFUL,
        32 => 0xFFFFFFFFUL,
        _ => ulong.MaxValue,
    };

    private static ulong SignBitFor64(int sizeBits) => sizeBits == 64 ? 0x8000000000000000UL : 1UL << (sizeBits - 1);

    /// <summary>Sign-extends a value already truncated to sizeBits into a full 64-bit signed integer.</summary>
    private static long SignExtendToLong(ulong value, int sizeBits) => sizeBits switch
    {
        8 => (sbyte)value,
        16 => (short)value,
        32 => (int)value,
        _ => (long)value,
    };

    /// <summary>Sign-extends the low <paramref name="bits"/> bits of an unsigned 128-bit value into a signed one, for a dividend built from two same-size registers.</summary>
    private static Int128 SignExtendInt128(UInt128 value, int bits)
    {
        if (bits >= 128) return (Int128)value;

        UInt128 signBit = (UInt128)1 << (bits - 1);
        UInt128 span = (UInt128)1 << bits;
        UInt128 masked = value & (span - 1);
        return (masked & signBit) != 0 ? (Int128)masked - (Int128)span : (Int128)masked;
    }
}
