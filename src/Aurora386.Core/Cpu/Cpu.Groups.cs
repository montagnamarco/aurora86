namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>
    /// Divide error. As with AAM, the 386 clears part of the arithmetic flags
    /// before it discovers that the result will not fit, so the #DE handler
    /// finds that state in the flags pushed onto its stack frame.
    /// </summary>
    private CpuException DivideError()
    {
        // Only overflow and zero come out cleared repeatably on real silicon.
        // Carry, parity, auxiliary and sign depend on how many steps of the
        // internal division had already been performed when the error was
        // detected, and are not reproducible, so we leave them alone rather
        // than inventing values.
        Flags.Overflow = false;
        Flags.Zero = false;
        return new CpuException(0);
    }

    // The "groups" are opcodes whose ModR/M reg field does not select a
    // register but extends the opcode itself: a single byte then covers eight
    // different instructions. It is how x86 fitted more instructions into the
    // encoding than 256 opcodes could ever hold.

    /// <summary>
    /// Group 3 at 8 bits (0xF6): TEST, NOT, NEG, MUL, IMUL, DIV, IDIV.
    /// </summary>
    private void Group3Byte()
    {
        var m = DecodeModRm();
        byte value = ReadRm8(m);

        switch (m.Reg)
        {
            case 0:
            case 1:   // TEST r/m8, imm8 -- reg 1 is an undocumented alias
                SetLogicFlags8((byte)(value & FetchByte()));
                return;

            case 2:   // NOT: touches no flags at all
                WriteRm8(m, (byte)~value);
                return;

            case 3:   // NEG
            {
                byte result = (byte)(0 - value);
                Flags.Set(FlagOp.Neg, value, 0, result, 8);
                WriteRm8(m, result);
                return;
            }

            case 4:   // MUL: AX = AL * r/m8
            {
                ushort result = (ushort)(State.GetReg8(0) * value);
                State.Ax = result;
                // CF and OF report that the high half is not empty, i.e. that
                // the result did not fit in 8 bits and AH must be looked at.
                Flags.Set(FlagOp.Mul, 0, (uint)(result > 0xFF ? 1 : 0), result, 8);
                return;
            }

            case 5:   // signed IMUL
            {
                short result = (short)((sbyte)State.GetReg8(0) * (sbyte)value);
                State.Ax = (ushort)result;
                // For IMUL the test is whether the result still fits in the low
                // half once sign-extended -- the high half being non-zero is
                // perfectly normal for a negative product.
                bool overflow = result != (sbyte)result;
                Flags.Set(FlagOp.Imul, 0, (uint)(overflow ? 1 : 0), (uint)result, 8);
                return;
            }

            case 6:   // unsigned DIV
            {
                if (value == 0) throw DivideError();
                ushort dividend = State.Ax;
                uint quotient = (uint)(dividend / value);
                // A quotient that will not fit in 8 bits is a divide error, not
                // a truncated result: real hardware raises exception 0.
                if (quotient > 0xFF) throw DivideError();
                State.SetReg8(0, (byte)quotient);            // AL = quotient
                State.SetReg8(4, (byte)(dividend % value));   // AH = remainder
                return;
            }

            default:  // signed IDIV
            {
                if (value == 0) throw DivideError();
                short dividend = (short)State.Ax;
                int quotient = dividend / (sbyte)value;
                if (quotient > sbyte.MaxValue || quotient < sbyte.MinValue)
                    throw DivideError();
                State.SetReg8(0, (byte)quotient);
                State.SetReg8(4, (byte)(dividend % (sbyte)value));
                return;
            }
        }
    }

    /// <summary>
    /// Group 3 at the current operand size (0xF7). The 16- and 32-bit forms use
    /// DX:AX and EDX:EAX as a register pair for the product and the dividend.
    /// </summary>
    private void Group3Word()
    {
        var m = DecodeModRm();
        uint value = ReadRm(m);
        bool wide = OperandSize32;

        switch (m.Reg)
        {
            case 0:
            case 1:
                SetLogicFlags(value & FetchImmediate());
                return;

            case 2:
                WriteRm(m, ~value);
                return;

            case 3:
            {
                uint result = 0 - value;
                Flags.Set(FlagOp.Neg, value, 0, result, OperandBits);
                WriteRm(m, result);
                return;
            }

            case 4:   // MUL
            {
                if (wide)
                {
                    ulong result = (ulong)State.Eax * value;
                    State.Eax = (uint)result;
                    State.Edx = (uint)(result >> 32);
                    Flags.Set(FlagOp.Mul, 0, (uint)(State.Edx != 0 ? 1 : 0), (uint)result, 32);
                }
                else
                {
                    uint result = (uint)(State.Ax * (ushort)value);
                    State.Ax = (ushort)result;
                    State.Dx = (ushort)(result >> 16);
                    Flags.Set(FlagOp.Mul, 0, (uint)(State.Dx != 0 ? 1 : 0), result, 16);
                }
                return;
            }

            case 5:   // IMUL
            {
                if (wide)
                {
                    long result = (long)(int)State.Eax * (int)value;
                    State.Eax = (uint)result;
                    State.Edx = (uint)((ulong)result >> 32);
                    bool overflow = result != (int)result;
                    Flags.Set(FlagOp.Imul, 0, (uint)(overflow ? 1 : 0), (uint)result, 32);
                }
                else
                {
                    int result = (short)State.Ax * (short)value;
                    State.Ax = (ushort)result;
                    State.Dx = (ushort)(result >> 16);
                    bool overflow = result != (short)result;
                    Flags.Set(FlagOp.Imul, 0, (uint)(overflow ? 1 : 0), (uint)result, 16);
                }
                return;
            }

            case 6:   // DIV
            {
                if (value == 0) throw DivideError();
                if (wide)
                {
                    ulong dividend = ((ulong)State.Edx << 32) | State.Eax;
                    ulong quotient = dividend / value;
                    if (quotient > uint.MaxValue) throw DivideError();
                    State.Eax = (uint)quotient;
                    State.Edx = (uint)(dividend % value);
                }
                else
                {
                    uint dividend = ((uint)State.Dx << 16) | State.Ax;
                    uint quotient = dividend / (ushort)value;
                    if (quotient > 0xFFFF) throw DivideError();
                    State.Ax = (ushort)quotient;
                    State.Dx = (ushort)(dividend % (ushort)value);
                }
                return;
            }

            default:  // IDIV
            {
                if (value == 0) throw DivideError();
                if (wide)
                {
                    long dividend = (long)(((ulong)State.Edx << 32) | State.Eax);
                    long quotient = dividend / (int)value;
                    if (quotient > int.MaxValue || quotient < int.MinValue)
                        throw DivideError();
                    State.Eax = (uint)quotient;
                    State.Edx = (uint)(dividend % (int)value);
                }
                else
                {
                    int dividend = (int)(((uint)State.Dx << 16) | State.Ax);
                    int quotient = dividend / (short)value;
                    if (quotient > short.MaxValue || quotient < short.MinValue)
                        throw DivideError();
                    State.Ax = (ushort)quotient;
                    State.Dx = (ushort)(dividend % (short)value);
                }
                return;
            }
        }
    }

    /// <summary>Group 4 (0xFE): INC/DEC on a byte.</summary>
    private void Group4()
    {
        var m = DecodeModRm();
        byte value = ReadRm8(m);

        switch (m.Reg)
        {
            case 0:
            {
                byte result = (byte)(value + 1);
                Flags.Set(FlagOp.Inc, value, 1, result, 8);
                WriteRm8(m, result);
                return;
            }
            case 1:
            {
                byte result = (byte)(value - 1);
                Flags.Set(FlagOp.Dec, value, 1, result, 8);
                WriteRm8(m, result);
                return;
            }
            default:
                throw new CpuException(6);
        }
    }

    /// <summary>
    /// Group 5 (0xFF): INC, DEC, CALL, far CALL, JMP, far JMP, PUSH.
    /// This is the group that makes indirect calls possible, and therefore all
    /// the code that goes through function pointers and jump tables.
    /// </summary>
    private void Group5()
    {
        var m = DecodeModRm();

        switch (m.Reg)
        {
            case 0:
            {
                uint value = ReadRm(m);
                uint result = value + 1;
                Flags.Set(FlagOp.Inc, value, 1, result, OperandBits);
                WriteRm(m, result);
                return;
            }
            case 1:
            {
                uint value = ReadRm(m);
                uint result = value - 1;
                Flags.Set(FlagOp.Dec, value, 1, result, OperandBits);
                WriteRm(m, result);
                return;
            }
            case 2:   // indirect near CALL
            {
                uint target = ReadRm(m);
                PushOperand(State.Eip);
                State.Eip = OperandSize32 ? target : target & 0xFFFF;
                return;
            }
            case 3:   // indirect far CALL: the operand is offset:segment
            {
                if (m.IsRegister) throw new CpuException(6);
                uint offset = ReadMemOperand(m.Segment, m.Offset);
                ushort selector = ReadMem16(m.Segment, m.Offset + (OperandSize32 ? 4u : 2u));
                PushOperand(State.Cs.Selector);
                PushOperand(State.Eip);
                LoadSegment(SegReg.Cs, selector);
                State.Eip = OperandSize32 ? offset : offset & 0xFFFF;
                return;
            }
            case 4:   // indirect near JMP
            {
                uint target = ReadRm(m);
                State.Eip = OperandSize32 ? target : target & 0xFFFF;
                return;
            }
            case 5:   // indirect far JMP
            {
                if (m.IsRegister) throw new CpuException(6);
                uint offset = ReadMemOperand(m.Segment, m.Offset);
                ushort selector = ReadMem16(m.Segment, m.Offset + (OperandSize32 ? 4u : 2u));
                LoadSegment(SegReg.Cs, selector);
                State.Eip = OperandSize32 ? offset : offset & 0xFFFF;
                return;
            }
            case 6:
                PushOperand(ReadRm(m));
                return;
            default:
                throw new CpuException(6);
        }
    }
}
