namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    // Istruzioni di aggiustamento decimale. Servono a fare aritmetica su cifre
    // BCD (una cifra decimale per nibble), che il software dell'epoca usa per
    // i calcoli su valuta e per la conversione in stringa senza divisioni.

    /// <summary>DAA: aggiusta AL dopo una somma di due valori BCD impacchettati.</summary>
    private void Daa()
    {
        // Entrambe le soglie si valutano sul valore di partenza: il +6 della
        // prima correzione non deve influenzare il confronto della seconda.
        byte al = State.GetReg8(0);
        byte alIniziale = al;
        bool carryIniziale = Flags.Carry;
        bool carry = false;

        if ((al & 0x0F) > 9 || Flags.Adjust)
        {
            carry = carryIniziale || alIniziale + 6 > 0xFF;
            al += 6;
            Flags.Adjust = true;
        }
        else Flags.Adjust = false;

        if (alIniziale > 0x99 || carryIniziale)
        {
            al += 0x60;
            carry = true;
        }

        State.SetReg8(0, al);
        bool adjust = Flags.Adjust;
        Flags.Set(FlagOp.Logic, 0, 0, al, 8);
        Flags.Carry = carry;
        Flags.Adjust = adjust;
    }

    /// <summary>DAS: aggiusta AL dopo una sottrazione BCD.</summary>
    private void Das()
    {
        byte al = State.GetReg8(0);
        byte alIniziale = al;
        bool carryIniziale = Flags.Carry;
        bool carry = false;

        if ((al & 0x0F) > 9 || Flags.Adjust)
        {
            carry = carryIniziale || alIniziale < 6;
            al -= 6;
            Flags.Adjust = true;
        }
        else Flags.Adjust = false;

        if (alIniziale > 0x99 || carryIniziale)
        {
            al -= 0x60;
            carry = true;
        }

        State.SetReg8(0, al);
        bool adjust = Flags.Adjust;
        Flags.Set(FlagOp.Logic, 0, 0, al, 8);
        Flags.Carry = carry;
        Flags.Adjust = adjust;
    }

    /// <summary>AAA: aggiusta AL dopo una somma di BCD non impacchettati.</summary>
    private void Aaa()
    {
        byte al = State.GetReg8(0);
        if ((al & 0x0F) > 9 || Flags.Adjust)
        {
            // La somma avviene su otto bit e il suo riporto si propaga in AH
            // oltre all'incremento fisso: con AL = FF, AH cresce di due.
            int somma = al + 6;
            State.SetReg8(0, (byte)(somma & 0x0F));
            State.SetReg8(4, (byte)(State.GetReg8(4) + 1 + (somma >> 8)));
            Flags.Adjust = true;
            Flags.Carry = true;
        }
        else
        {
            State.SetReg8(0, (byte)(al & 0x0F));
            Flags.Adjust = false;
            Flags.Carry = false;
        }
    }

    /// <summary>AAS: aggiusta AL dopo una sottrazione di BCD non impacchettati.</summary>
    private void Aas()
    {
        byte al = State.GetReg8(0);
        if ((al & 0x0F) > 9 || Flags.Adjust)
        {
            // Come per AAA, il prestito della sottrazione a otto bit si
            // somma al decremento fisso di AH.
            int differenza = al - 6;
            State.SetReg8(0, (byte)(differenza & 0x0F));
            State.SetReg8(4, (byte)(State.GetReg8(4) - 1 - ((differenza >> 8) & 1)));
            Flags.Adjust = true;
            Flags.Carry = true;
        }
        else
        {
            State.SetReg8(0, (byte)(al & 0x0F));
            Flags.Adjust = false;
            Flags.Carry = false;
        }
    }

    /// <summary>
    /// AAM: divide AL per la base (10 di default) mettendo quoziente in AH e
    /// resto in AL. La base arriva come byte immediato: assemblatori diversi
    /// permettono basi diverse da 10, ed e' un modo compatto di dividere.
    /// </summary>
    private void Aam()
    {
        byte divisor = FetchByte();
        if (divisor == 0)
        {
            // Il 386 azzera i flag aritmetici prima di accorgersi del divisore
            // nullo, quindi il gestore di #DE trova questo stato impilato.
            // PF resta indeterminato: dipende dai passi gia' compiuti dalla
            // divisione interrotta e non e' riproducibile.
            Flags.Carry = false; Flags.Adjust = false; Flags.Zero = false;
            Flags.Sign = false; Flags.Overflow = false;
            throw new CpuException(0);
        }

        byte al = State.GetReg8(0);
        State.SetReg8(4, (byte)(al / divisor));
        State.SetReg8(0, (byte)(al % divisor));
        Flags.Set(FlagOp.Logic, 0, 0, State.GetReg8(0), 8);
    }

    /// <summary>AAD: l'inverso di AAM, comprime AH:AL in AL.</summary>
    private void Aad()
    {
        byte multiplier = FetchByte();
        byte result = (byte)(State.GetReg8(0) + State.GetReg8(4) * multiplier);
        State.SetReg8(0, result);
        State.SetReg8(4, 0);
        Flags.Set(FlagOp.Logic, 0, 0, result, 8);
    }
}
