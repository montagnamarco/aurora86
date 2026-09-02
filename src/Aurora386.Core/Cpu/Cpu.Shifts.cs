namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>
    /// Gruppo 2: ROL, ROR, RCL, RCR, SHL, SHR, SAL, SAR.
    ///
    /// Dal 286 in poi il conteggio viene mascherato a 5 bit prima dell'uso:
    /// uno scorrimento di 32 non azzera il registro, e' uno scorrimento di 0.
    /// E' una differenza osservabile rispetto all'8086 e alcuni programmi
    /// dell'epoca la usavano per distinguere i processori.
    /// </summary>
    private void ExecuteShift(in ModRm m, int count, bool byteOperand)
    {
        count &= 0x1F;

        // L'operando viene letto anche con conteggio nullo: l'istruzione non
        // ha effetto, ma l'accesso in memoria avviene lo stesso e un indirizzo
        // fuori dal segmento produce #GP.
        byte size = byteOperand ? (byte)8 : OperandBits;
        uint value = byteOperand ? ReadRm8(m) : ReadRm(m);
        if (count == 0) return;   // nessun effetto, flag inclusi
        uint mask = size == 8 ? 0xFFu : size == 16 ? 0xFFFFu : 0xFFFFFFFFu;
        uint signBit = 1u << (size - 1);
        uint result;

        switch (m.Reg)
        {
            case 0:   // ROL
            {
                int rotate = count % size;
                result = rotate == 0 ? value
                       : ((value << rotate) | (value >> (size - rotate))) & mask;
                Flags.Carry = (result & 1) != 0;
                if (count == 1) Flags.Overflow = ((result & signBit) != 0) != Flags.Carry;
                break;
            }

            case 1:   // ROR
            {
                int rotate = count % size;
                result = rotate == 0 ? value
                       : ((value >> rotate) | (value << (size - rotate))) & mask;
                Flags.Carry = (result & signBit) != 0;
                if (count == 1)
                {
                    // OF confronta i due bit piu' significativi del risultato.
                    bool topBit = (result & signBit) != 0;
                    bool nextBit = (result & (signBit >> 1)) != 0;
                    Flags.Overflow = topBit != nextBit;
                }
                break;
            }

            case 2:   // RCL: la rotazione passa attraverso il carry, quindi il
                      // ciclo e' lungo size+1 bit, non size.
            {
                int rotate = count % (size + 1);
                result = value;
                for (int i = 0; i < rotate; i++)
                {
                    bool newCarry = (result & signBit) != 0;
                    result = ((result << 1) | (Flags.Carry ? 1u : 0u)) & mask;
                    Flags.Carry = newCarry;
                }
                if (count == 1) Flags.Overflow = ((result & signBit) != 0) != Flags.Carry;
                break;
            }

            case 3:   // RCR
            {
                int rotate = count % (size + 1);
                result = value;
                if (count == 1)
                    Flags.Overflow = ((value & signBit) != 0) != Flags.Carry;
                for (int i = 0; i < rotate; i++)
                {
                    bool newCarry = (result & 1) != 0;
                    result = (result >> 1) | (Flags.Carry ? signBit : 0);
                    Flags.Carry = newCarry;
                }
                break;
            }

            case 4:
            case 6:   // SHL e SAL sono la stessa istruzione
            {
                result = (value << count) & mask;
                Flags.Set(FlagOp.Shl, value, (uint)count, result, size);
                break;
            }

            case 5:   // SHR: scorrimento logico, entra zero da sinistra
            {
                result = count >= size ? 0 : (value & mask) >> count;
                Flags.Set(FlagOp.Shr, value, (uint)count, result, size);
                break;
            }

            default:  // SAR: scorrimento aritmetico, il segno si replica
            {
                // L'estensione di segno va fatta sulla larghezza dell'operando,
                // non su 32 bit, altrimenti un valore a 8 o 16 bit con il bit
                // alto acceso non replicherebbe il segno giusto.
                int signed = size switch
                {
                    8 => (sbyte)value,
                    16 => (short)value,
                    _ => (int)value,
                };
                int shift = Math.Min(count, size - 1);
                result = (uint)(signed >> shift) & mask;
                Flags.Set(FlagOp.Sar, value, (uint)count, result, size);
                break;
            }
        }

        if (byteOperand) WriteRm8(m, (byte)result);
        else WriteRm(m, result);
    }

    /// <summary>
    /// Scorrimento a doppia precisione (SHLD/SHRD), aggiunto dal 386: sposta i
    /// bit facendoli entrare da un secondo operando invece che da zero.
    /// </summary>
    private void ShiftDouble(in ModRm m, uint source, int count, bool left)
    {
        count &= 0x1F;

        byte size = OperandBits;
        // La lettura precede il controllo sul conteggio: anche con conteggio
        // nullo il 386 accede all'operando, quindi un indirizzo oltre il
        // limite del segmento solleva #GP invece di passare inosservato.
        uint destination = ReadRm(m);
        if (count == 0) return;
        uint mask = size == 16 ? 0xFFFFu : 0xFFFFFFFFu;
        uint result;

        if (size == 16 && count > 16)
        {
            // Caso che Intel dichiara indefinito, ma il 386 lo risolve sempre
            // allo stesso modo: il barrel shifter lavora a 32 bit e con un
            // conteggio oltre i 16 la destinazione e' gia' uscita del tutto,
            // cosi' entrambe le meta' finiscono per contenere la sorgente.
            // Verificato su tutti i casi della suite generata da hardware.
            uint doppia = (source & 0xFFFF) << 16 | (source & 0xFFFF);
            uint esito = left ? (doppia << (count - 16)) >> 16
                              : (doppia >> (count - 16)) & 0xFFFF;
            esito &= 0xFFFF;

            Flags.Carry = left
                ? ((source >> (32 - count)) & 1) != 0
                : ((source >> (count - 17)) & 1) != 0;
            bool cf = Flags.Carry;
            Flags.Set(FlagOp.Logic, 0, 0, esito, size);
            Flags.Carry = cf;

            WriteRm(m, esito);
            return;
        }

        if (left)
        {
            result = ((destination << count) | (source >> (size - count))) & mask;
            Flags.Carry = ((destination >> (size - count)) & 1) != 0;
        }
        else
        {
            result = ((destination >> count) | (source << (size - count))) & mask;
            Flags.Carry = ((destination >> (count - 1)) & 1) != 0;
        }

        // ZF/SF/PF derivano dal risultato; il carry e' gia' stato posto sopra.
        bool carry = Flags.Carry;
        Flags.Set(FlagOp.Logic, 0, 0, result, size);
        Flags.Carry = carry;

        WriteRm(m, result);
    }
}
