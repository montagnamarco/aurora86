namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>
    /// Errore di divisione. Come per AAM, il 386 azzera i flag aritmetici
    /// prima di accorgersi che il risultato non ci sta: il gestore di #DE
    /// trova quello stato nei flag impilati.
    /// </summary>
    private CpuException ErroreDivisione()
    {
        // Solo overflow e zero risultano azzerati in modo ripetibile sul
        // ferro: carry, parita', ausiliario e segno dipendono da quanti passi
        // la divisione aveva gia' compiuto, e non sono riproducibili.
        Flags.Overflow = false;
        Flags.Zero = false;
        return new CpuException(0);
    }

    // I "gruppi" sono opcode il cui campo reg del ModR/M non seleziona un
    // registro ma estende l'opcode stesso: un solo byte copre otto istruzioni
    // diverse. E' il modo in cui x86 ha fatto stare piu' istruzioni di quante
    // ne entrassero in 256 opcode.

    /// <summary>
    /// Gruppo 3 a 8 bit (0xF6): TEST, NOT, NEG, MUL, IMUL, DIV, IDIV.
    /// </summary>
    private void Group3Byte()
    {
        var m = DecodeModRm();
        byte value = ReadRm8(m);

        switch (m.Reg)
        {
            case 0:
            case 1:   // TEST r/m8, imm8 -- il reg 1 e' un alias non documentato
                SetLogicFlags8((byte)(value & FetchByte()));
                return;

            case 2:   // NOT: nessun flag toccato
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
                // CF e OF segnalano che la meta' alta non e' vuota, cioe' che
                // il risultato non sta in 8 bit.
                Flags.Set(FlagOp.Mul, 0, (uint)(result > 0xFF ? 1 : 0), result, 8);
                return;
            }

            case 5:   // IMUL con segno
            {
                short result = (short)((sbyte)State.GetReg8(0) * (sbyte)value);
                State.Ax = (ushort)result;
                // Per IMUL il criterio e' se il risultato entra nella meta'
                // bassa una volta esteso di segno.
                bool overflow = result != (sbyte)result;
                Flags.Set(FlagOp.Imul, 0, (uint)(overflow ? 1 : 0), (uint)result, 8);
                return;
            }

            case 6:   // DIV senza segno
            {
                if (value == 0) throw ErroreDivisione();
                ushort dividend = State.Ax;
                uint quotient = (uint)(dividend / value);
                // Un quoziente che non entra in 8 bit e' un errore di divisione,
                // non un risultato troncato: sul ferro reale genera l'eccezione 0.
                if (quotient > 0xFF) throw ErroreDivisione();
                State.SetReg8(0, (byte)quotient);            // AL = quoziente
                State.SetReg8(4, (byte)(dividend % value));   // AH = resto
                return;
            }

            default:  // IDIV con segno
            {
                if (value == 0) throw ErroreDivisione();
                short dividend = (short)State.Ax;
                int quotient = dividend / (sbyte)value;
                if (quotient > sbyte.MaxValue || quotient < sbyte.MinValue)
                    throw ErroreDivisione();
                State.SetReg8(0, (byte)quotient);
                State.SetReg8(4, (byte)(dividend % (sbyte)value));
                return;
            }
        }
    }

    /// <summary>
    /// Gruppo 3 alla dimensione corrente (0xF7). Le versioni a 16 e 32 bit
    /// usano DX:AX e EDX:EAX come coppia per prodotto e dividendo.
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
                if (value == 0) throw ErroreDivisione();
                if (wide)
                {
                    ulong dividend = ((ulong)State.Edx << 32) | State.Eax;
                    ulong quotient = dividend / value;
                    if (quotient > uint.MaxValue) throw ErroreDivisione();
                    State.Eax = (uint)quotient;
                    State.Edx = (uint)(dividend % value);
                }
                else
                {
                    uint dividend = ((uint)State.Dx << 16) | State.Ax;
                    uint quotient = dividend / (ushort)value;
                    if (quotient > 0xFFFF) throw ErroreDivisione();
                    State.Ax = (ushort)quotient;
                    State.Dx = (ushort)(dividend % (ushort)value);
                }
                return;
            }

            default:  // IDIV
            {
                if (value == 0) throw ErroreDivisione();
                if (wide)
                {
                    long dividend = (long)(((ulong)State.Edx << 32) | State.Eax);
                    long quotient = dividend / (int)value;
                    if (quotient > int.MaxValue || quotient < int.MinValue)
                        throw ErroreDivisione();
                    State.Eax = (uint)quotient;
                    State.Edx = (uint)(dividend % (int)value);
                }
                else
                {
                    int dividend = (int)(((uint)State.Dx << 16) | State.Ax);
                    int quotient = dividend / (short)value;
                    if (quotient > short.MaxValue || quotient < short.MinValue)
                        throw ErroreDivisione();
                    State.Ax = (ushort)quotient;
                    State.Dx = (ushort)(dividend % (short)value);
                }
                return;
            }
        }
    }

    /// <summary>Gruppo 4 (0xFE): INC/DEC su un byte.</summary>
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
    /// Gruppo 5 (0xFF): INC, DEC, CALL, CALL far, JMP, JMP far, PUSH.
    /// E' il gruppo che rende possibili le chiamate indirette, quindi tutto il
    /// codice che passa per puntatori a funzione e tabelle di salto.
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
            case 2:   // CALL indiretta vicina
            {
                uint target = ReadRm(m);
                PushOperand(State.Eip);
                State.Eip = OperandSize32 ? target : target & 0xFFFF;
                return;
            }
            case 3:   // CALL indiretta lontana: l'operando e' offset:segmento
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
            case 4:   // JMP indiretto vicino
            {
                uint target = ReadRm(m);
                State.Eip = OperandSize32 ? target : target & 0xFFFF;
                return;
            }
            case 5:   // JMP indiretto lontano
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
