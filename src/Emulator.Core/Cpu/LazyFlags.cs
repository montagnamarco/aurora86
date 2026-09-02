using System.Numerics;
using System.Runtime.CompilerServices;

namespace Emulator.Core.Cpu;

/// <summary>
/// Tipo di operazione che ha prodotto l'ultimo risultato aritmetico.
/// </summary>
public enum FlagOp : byte
{
    /// <summary>I flag sono gia' materializzati in <see cref="LazyFlags.Stored"/>.</summary>
    None = 0,
    Add, Adc, Sub, Sbb,
    /// <summary>AND/OR/XOR/TEST: azzerano sempre carry e overflow.</summary>
    Logic,
    Inc, Dec,
    Shl, Shr, Sar,
    Neg,
    Mul, Imul,
}

/// <summary>
/// Calcolo pigro dei flag aritmetici.
///
/// Un'istruzione come ADD dovrebbe aggiornare sei flag, ma il codice reale ne
/// legge quasi sempre al piu' uno (tipicamente ZF o CF subito dopo un CMP) e
/// spessissimo nessuno: il risultato viene sovrascritto dall'istruzione dopo.
/// Calcolarli tutti a ogni operazione e' quindi lavoro in gran parte sprecato.
///
/// Qui si memorizzano invece operandi, risultato e tipo di operazione; i flag
/// vengono derivati solo quando qualcuno li legge davvero. E' la stessa
/// strategia di DOSBox e QEMU, e sul percorso caldo fa una differenza netta.
/// </summary>
public struct LazyFlags
{
    /// <summary>Flag gia' calcolati o impostati esplicitamente (POPF, STI, ...).</summary>
    public Flags Stored;

    /// <summary>Operazione in sospeso, o None se Stored e' gia' valido.</summary>
    public FlagOp Op;

    public uint Left;
    public uint Right;
    public uint Result;
    /// <summary>Dimensione dell'operando in bit: 8, 16 o 32.</summary>
    public byte Size;

    /// <summary>
    /// Carry entrante di ADC/SBB. Non e' ricostruibile dal risultato -- ADC con
    /// carry 1 e ADC con carry 0 su operandi diversi possono dare lo stesso
    /// risultato -- quindi va memorizzato quando l'operazione viene registrata.
    /// </summary>
    public bool CarryIn;

    /// <summary>
    /// Registra un'operazione senza calcolarne i flag. I flag non aritmetici
    /// (IF, DF, TF...) restano in Stored e non vengono toccati.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(FlagOp op, uint left, uint right, uint result, byte size,
                    bool carryIn = false)
    {
        // INC e DEC ereditano il carry invece di calcolarlo: se l'operazione
        // precedente e' ancora pigra il suo carry vive in Op/Left/Right e non
        // in Stored, quindi va risolto adesso o andrebbe perso.
        if (op is FlagOp.Inc or FlagOp.Dec) Materialize();

        Op = op;
        Left = left;
        Right = right;
        Result = result;
        Size = size;
        CarryIn = carryIn;
    }

    /// <summary>Maschera dei bit significativi per la dimensione corrente.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint MaskFor(byte size) => size switch
    {
        8 => 0xFFu,
        16 => 0xFFFFu,
        _ => 0xFFFFFFFFu,
    };

    /// <summary>Bit di segno per la dimensione corrente.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint SignBit(byte size) => 1u << (size - 1);

    /// <summary>
    /// Materializza i flag aritmetici in Stored e azzera l'operazione pendente.
    /// Chiamata solo quando un flag viene effettivamente letto.
    /// </summary>
    public void Materialize()
    {
        if (Op == FlagOp.None) return;

        uint mask = MaskFor(Size);
        uint sign = SignBit(Size);
        uint result = Result & mask;
        uint left = Left & mask;
        uint right = Right & mask;

        // I flag derivati dal solo risultato sono comuni a tutte le operazioni.
        Flags flags = Stored & ~(Flags.Carry | Flags.Parity | Flags.Adjust
                               | Flags.Zero | Flags.Sign | Flags.Overflow);

        if (result == 0) flags |= Flags.Zero;
        if ((result & sign) != 0) flags |= Flags.Sign;

        // La parita' su x86 guarda solo gli 8 bit bassi, sempre, anche per
        // operandi a 16 e 32 bit.
        if ((BitOperations.PopCount(result & 0xFF) & 1) == 0) flags |= Flags.Parity;

        switch (Op)
        {
            case FlagOp.Add:
            case FlagOp.Adc:
            {
                // Con carry entrante 1 il risultato puo' essere uguale a left
                // pur avendo generato riporto (es. 0xFF + 0x00 + 1 = 0x00, ma
                // anche 0xFF + 0xFF + 1 = 0xFF): il confronto diventa <=.
                bool carry = CarryIn ? result <= left : result < left;
                if (carry) flags |= Flags.Carry;
                if (((left ^ result) & (right ^ result) & sign) != 0) flags |= Flags.Overflow;
                if (((left ^ right ^ result) & 0x10) != 0) flags |= Flags.Adjust;
                break;
            }

            case FlagOp.Sub:
            case FlagOp.Sbb:
            {
                bool carry = CarryIn ? left <= right : left < right;
                if (carry) flags |= Flags.Carry;
                if (((left ^ right) & (left ^ result) & sign) != 0) flags |= Flags.Overflow;
                if (((left ^ right ^ result) & 0x10) != 0) flags |= Flags.Adjust;
                break;
            }

            case FlagOp.Logic:
                // AND/OR/XOR azzerano CF e OF; AF resta indefinito sul ferro
                // reale, lo lasciamo a zero.
                break;

            case FlagOp.Inc:
                // INC e DEC preservano deliberatamente il carry: e' cio' che
                // permette di usarli dentro cicli di addizione multiprecisione.
                flags |= Stored & Flags.Carry;
                if (result == sign) flags |= Flags.Overflow;
                if ((result & 0x0F) == 0) flags |= Flags.Adjust;
                break;

            case FlagOp.Dec:
                flags |= Stored & Flags.Carry;
                if (result == (sign - 1)) flags |= Flags.Overflow;
                if ((result & 0x0F) == 0x0F) flags |= Flags.Adjust;
                break;

            case FlagOp.Neg:
                // NEG e' 0 - operando: il carry e' posto se l'operando non era zero.
                if (left != 0) flags |= Flags.Carry;
                if (result == sign) flags |= Flags.Overflow;
                if ((result & 0x0F) != 0) flags |= Flags.Adjust;
                break;

            case FlagOp.Shl:
                // Il carry e' l'ultimo bit uscito a sinistra; l'overflow ha
                // senso solo per scorrimenti di 1 ed e' CF xor bit di segno.
                if (Right != 0 && Right <= Size
                    && ((left >> (Size - (int)Right)) & 1) != 0) flags |= Flags.Carry;
                if (Right == 1 && (((result & sign) != 0) != ((flags & Flags.Carry) != 0)))
                    flags |= Flags.Overflow;
                break;

            case FlagOp.Shr:
                if (Right != 0 && Right <= Size
                    && ((left >> ((int)Right - 1)) & 1) != 0) flags |= Flags.Carry;
                if (Right == 1 && (left & sign) != 0) flags |= Flags.Overflow;
                break;

            case FlagOp.Sar:
                if (Right != 0 && ((left >> Math.Min((int)Right - 1, Size - 1)) & 1) != 0)
                    flags |= Flags.Carry;
                // SAR non puo' mai produrre overflow.
                break;

            case FlagOp.Mul:
            case FlagOp.Imul:
                // Per MUL/IMUL carry e overflow sono impostati insieme dal
                // chiamante in Right (0 o 1): indicano se il risultato eccede
                // la meta' bassa. ZF/SF/PF sono indefiniti sul ferro reale.
                if (Right != 0) flags |= Flags.Carry | Flags.Overflow;
                break;
        }

        Stored = flags;
        Op = FlagOp.None;
    }

    /// <summary>Valore completo di EFLAGS, materializzando se necessario.</summary>
    public uint Value
    {
        get
        {
            Materialize();
            return (uint)Stored | (uint)Flags.Reserved1;
        }
    }

    /// <summary>Imposta EFLAGS per intero (POPF, IRET), scartando la pigrizia.</summary>
    public void SetValue(uint value)
    {
        Stored = (Flags)value | Flags.Reserved1;
        Op = FlagOp.None;
        CarryIn = false;
    }

    // --- accesso ai singoli flag -------------------------------------------

    public bool Carry
    {
        get { Materialize(); return (Stored & Flags.Carry) != 0; }
        set { Materialize(); Stored = value ? Stored | Flags.Carry : Stored & ~Flags.Carry; }
    }

    public bool Parity
    {
        get { Materialize(); return (Stored & Flags.Parity) != 0; }
        set { Materialize(); Stored = value ? Stored | Flags.Parity : Stored & ~Flags.Parity; }
    }

    public bool Adjust
    {
        get { Materialize(); return (Stored & Flags.Adjust) != 0; }
        set { Materialize(); Stored = value ? Stored | Flags.Adjust : Stored & ~Flags.Adjust; }
    }

    public bool Zero
    {
        get { Materialize(); return (Stored & Flags.Zero) != 0; }
        set { Materialize(); Stored = value ? Stored | Flags.Zero : Stored & ~Flags.Zero; }
    }

    public bool Sign
    {
        get { Materialize(); return (Stored & Flags.Sign) != 0; }
        set { Materialize(); Stored = value ? Stored | Flags.Sign : Stored & ~Flags.Sign; }
    }

    public bool Overflow
    {
        get { Materialize(); return (Stored & Flags.Overflow) != 0; }
        set { Materialize(); Stored = value ? Stored | Flags.Overflow : Stored & ~Flags.Overflow; }
    }

    // I flag di controllo non sono mai pigri: si leggono e scrivono diretti.
    public bool Interrupt
    {
        readonly get => (Stored & Flags.Interrupt) != 0;
        set => Stored = value ? Stored | Flags.Interrupt : Stored & ~Flags.Interrupt;
    }

    public bool Direction
    {
        readonly get => (Stored & Flags.Direction) != 0;
        set => Stored = value ? Stored | Flags.Direction : Stored & ~Flags.Direction;
    }

    public bool Trap
    {
        readonly get => (Stored & Flags.Trap) != 0;
        set => Stored = value ? Stored | Flags.Trap : Stored & ~Flags.Trap;
    }
}
