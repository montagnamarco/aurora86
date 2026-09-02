using System.Runtime.CompilerServices;
using Aurora386.Core.IO;
using Aurora386.Core.Memory;

namespace Aurora386.Core.Cpu;

/// <summary>
/// Interprete del 80386.
///
/// Questa parte contiene lo stato, il fetch e la traduzione degli indirizzi;
/// la decodifica ModR/M e l'esecuzione degli opcode stanno nei file parziali
/// Cpu.ModRm.cs e Cpu.Execute.cs.
/// </summary>
public sealed partial class Cpu
{
    public readonly CpuState State = new();
    public LazyFlags Flags;

    private readonly PhysicalMemoryBus _memory;
    private readonly IoBus _io;

    /// <summary>Prefissi dell'istruzione in corso di decodifica.</summary>
    private Prefixes _prefixes;

    /// <summary>
    /// EIP dell'istruzione corrente, salvato prima del fetch. Serve a
    /// riavviarla da capo quando un'eccezione la interrompe a meta' (una page
    /// fault deve poter essere gestita e l'istruzione ripetuta).
    /// </summary>
    private uint _instructionStart;

    /// <summary>Cicli consumati, usati dai timer per restare in passo.</summary>
    public long Cycles { get; private set; }

    /// <summary>
    /// Controller di interruzioni. Se assente la CPU non riceve interruzioni
    /// hardware, il che va bene per i test ma non per una macchina completa.
    /// </summary>
    public Devices.InterruptController? Interrupts { get; set; }

    /// <summary>
    /// STI non abilita le interruzioni sull'istante: la finestra si apre dopo
    /// l'istruzione successiva. E' cio' che rende atomica la sequenza
    /// "STI; HLT" -- senza questo ritardo un'interruzione potrebbe arrivare
    /// fra le due e il HLT non verrebbe mai risvegliato.
    /// </summary>
    private bool _interruptShadow;

    public Cpu(PhysicalMemoryBus memory, IoBus? io = null)
    {
        _memory = memory;
        // Senza dispositivi collegati ogni porta legge 0xFF, che e' anche il
        // comportamento di un PC reale con uno slot vuoto.
        _io = io ?? new IoBus();
        Reset();
    }

    public void Reset()
    {
        State.Reset();
        Flags = default;
        Flags.SetValue((uint)Aurora386.Core.Cpu.Flags.Reserved1);
        _prefixes.Clear();
        Cycles = 0;
    }

    // --- dimensioni effettive di operandi e indirizzi ------------------------
    // Il bit D del descrittore di CS fissa il default (16 bit in real mode e nei
    // segmenti a 16 bit, 32 nei segmenti a 32); i prefissi 0x66/0x67 lo invertono
    // per la singola istruzione.

    private bool OperandSize32
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => State.Cs.Big ^ _prefixes.OperandSize;
    }

    private bool AddressSize32
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => State.Cs.Big ^ _prefixes.AddressSize;
    }

    // --- traduzione degli indirizzi -----------------------------------------

    /// <summary>
    /// Converte un indirizzo logico (segmento:offset) in lineare. Con il paging
    /// attivo la traduzione da lineare a fisico avviene poi in
    /// <see cref="TranslateLinear"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint LinearAddress(SegReg segment, uint offset)
        => State.Segments[(int)segment].Base + offset;

    /// <summary>
    /// Traduce lineare -> fisico. Senza paging e' l'identita'; con il paging
    /// attivo passa per la page directory. Il TLB verra' aggiunto insieme al
    /// protected mode: finche' si esegue in real mode questa e' una identita'.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint TranslateLinear(uint linear, bool write)
    {
        if (!State.PagingEnabled) return linear;
        return TranslatePaged(linear, write);
    }

    // --- accessi alla memoria ------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadMem8(SegReg segment, uint offset)
    {
        // Anche l'accesso a un solo byte deve stare dentro il limite: con
        // indirizzi a 32 bit un offset negativo diventa un valore enorme e
        // deve produrre #GP invece di leggere memoria a caso.
        CheckLimit(segment, offset, 1);
        return _memory.ReadByte(TranslateLinear(LinearAddress(segment, offset), false));
    }

    /// <summary>
    /// Un accesso multi-byte non puo' oltrepassare il limite del segmento:
    /// una word a offset FFFF non avvolge a 0000, genera una eccezione di
    /// protezione. Verificato sui test SingleStepTests generati da un 386
    /// reale: ogni accesso a cavallo del limite produce #GP, mai un wrap.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CheckLimit(SegReg segment, uint offset, uint size)
    {
        ref SegmentCache seg = ref State.Segments[(int)segment];
        // La somma va fatta a 64 bit: con offset prossimo a FFFFFFFF
        // traboccherebbe tornando a un valore piccolo, e un accesso ben oltre
        // il limite passerebbe per valido.
        if ((ulong)offset + size - 1 <= seg.Limit) return;

        // Sforare il limite dello stack e' una condizione a se': il processore
        // la segnala con #SS invece che con la protezione generale, perche' il
        // gestore deve poter distinguere uno stack esaurito da un puntatore
        // sbagliato.
        throw new CpuException(segment == SegReg.Ss ? (byte)12 : (byte)13, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort ReadMem16(SegReg segment, uint offset)
    {
        CheckLimit(segment, offset, 2);
        return _memory.ReadWord(TranslateLinear(LinearAddress(segment, offset), false));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadMem32(SegReg segment, uint offset)
    {
        // Un accesso a 32 bit che sfora il limite del segmento non avvolge:
        // il 386 lo rifiuta con #GP esattamente come fa per le word. Lo
        // confermano i test generati da hardware sugli opcode con prefisso 66.
        CheckLimit(segment, offset, 4);
        return _memory.ReadDword(TranslateLinear(LinearAddress(segment, offset), false));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteMem8(SegReg segment, uint offset, byte value)
    {
        CheckLimit(segment, offset, 1);
        _memory.WriteByte(TranslateLinear(LinearAddress(segment, offset), true), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteMem16(SegReg segment, uint offset, ushort value)
    {
        CheckLimit(segment, offset, 2);
        _memory.WriteWord(TranslateLinear(LinearAddress(segment, offset), true), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteMem32(SegReg segment, uint offset, uint value)
    {
        CheckLimit(segment, offset, 4);
        _memory.WriteDword(TranslateLinear(LinearAddress(segment, offset), true), value);
    }

    // --- fetch del flusso di istruzioni --------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte FetchByte()
    {
        byte value = ReadMem8(SegReg.Cs, State.Eip);
        State.Eip++;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort FetchWord()
    {
        ushort value = ReadMem16(SegReg.Cs, State.Eip);
        State.Eip += 2;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint FetchDword()
    {
        uint value = ReadMem32(SegReg.Cs, State.Eip);
        State.Eip += 4;
        return value;
    }

    /// <summary>Immediato della dimensione corrente degli operandi.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint FetchImmediate() => OperandSize32 ? FetchDword() : FetchWord();

    // --- stack ----------------------------------------------------------------
    // La larghezza dello stack dipende dal bit B di SS, non dalla dimensione
    // degli operandi: un segmento di stack a 16 bit usa SP anche mentre si
    // eseguono istruzioni a 32 bit.

    private void Push16(ushort value)
    {
        if (State.Ss.Big)
        {
            State.Esp -= 2;
            WriteMem16(SegReg.Ss, State.Esp, value);
        }
        else
        {
            State.Sp -= 2;
            WriteMem16(SegReg.Ss, State.Sp, value);
        }
    }

    private void Push32(uint value)
    {
        if (State.Ss.Big)
        {
            State.Esp -= 4;
            WriteMem32(SegReg.Ss, State.Esp, value);
        }
        else
        {
            // Come per la pop, con stack a 16 bit la scrittura si spezza in
            // due word che avvolgono ciascuna dentro il segmento.
            State.Sp -= 4;
            WriteMem16(SegReg.Ss, State.Sp, (ushort)value);
            WriteMem16(SegReg.Ss, (uint)(ushort)(State.Sp + 2), (ushort)(value >> 16));
        }
    }

    private ushort Pop16()
    {
        if (State.Ss.Big)
        {
            ushort value = ReadMem16(SegReg.Ss, State.Esp);
            State.Esp += 2;
            return value;
        }
        else
        {
            ushort value = ReadMem16(SegReg.Ss, State.Sp);
            State.Sp += 2;
            return value;
        }
    }

    private uint Pop32()
    {
        if (State.Ss.Big)
        {
            uint value = ReadMem32(SegReg.Ss, State.Esp);
            State.Esp += 4;
            return value;
        }
        else
        {
            uint value = ReadMem32(SegReg.Ss, State.Sp);
            State.Sp += 4;
            return value;
        }
    }

    /// <summary>
    /// Pop a 32 bit che avvolge invece di sforare. La usano solo le POP di un
    /// registro di segmento: di quei quattro byte il processore ne consuma due
    /// soli, e i due accessi avvolgono ciascuno dentro il segmento invece di
    /// far fallire l'istruzione. Le pop ordinarie e le istruzioni che leggono
    /// un blocco intero rifiutano l'accesso, come tutti gli altri operandi.
    /// </summary>
    private uint PopSegmentOperand()
    {
        if (!OperandSize32) return Pop16();
        if (State.Ss.Big) return Pop32();

        ushort sp = State.Sp;
        uint low = ReadMem16(SegReg.Ss, sp);
        uint high = ReadMem16(SegReg.Ss, (uint)(ushort)(sp + 2));
        State.Sp = (ushort)(sp + 4);
        return low | (high << 16);
    }

    // --- accesso pubblico per gli handler nativi -----------------------------
    // Le funzioni BIOS realizzate in C# hanno bisogno di leggere e scrivere
    // memoria come le vedrebbe il programma chiamante, cioe' attraverso i
    // segmenti e la traduzione degli indirizzi.

    public byte ReadMemoryByte(SegReg segment, uint offset) => ReadMem8(segment, offset);
    public ushort ReadMemoryWord(SegReg segment, uint offset) => ReadMem16(segment, offset);
    public void WriteMemoryByte(SegReg segment, uint offset, byte value) => WriteMem8(segment, offset, value);
    public void WriteMemoryWord(SegReg segment, uint offset, ushort value) => WriteMem16(segment, offset, value);

    /// <summary>Memoria fisica, per gli accessi che non passano per i segmenti.</summary>
    public PhysicalMemoryBus Memory => _memory;

    /// <summary>Spazio di I/O, per i dispositivi.</summary>
    public IoBus Io => _io;

    /// <summary>
    /// Imposta un segmento come farebbe il codice emulato. In real mode e'
    /// base = selettore * 16.
    /// </summary>
    public void SetSegment(SegReg segment, ushort selector) => LoadSegment(segment, selector);

    /// <summary>
    /// Ritorna da una interruzione, come farebbe IRET. Gli handler nativi la
    /// usano per concludere una funzione BIOS.
    /// </summary>
    public void ReturnFromInterrupt() => Iret();

    /// <summary>
    /// Imposta il carry nei flag salvati sullo stack da una INT. Le funzioni
    /// BIOS segnalano gli errori con il carry, ma quello attuale verrebbe
    /// sovrascritto dall'IRET: va modificato dove e' stato impilato.
    /// </summary>
    public void SetCarryInStackedFlags(bool carry)
    {
        // Dopo una INT in real mode lo stack contiene IP, CS, FLAGS in
        // quest'ordine: i flag stanno quattro byte sopra la cima.
        uint stackPointer = State.Ss.Big ? State.Esp : State.Sp;
        ushort flags = ReadMem16(SegReg.Ss, stackPointer + 4);
        flags = carry ? (ushort)(flags | 1) : (ushort)(flags & ~1);
        WriteMem16(SegReg.Ss, stackPointer + 4, flags);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PushOperand(uint value)
    {
        if (OperandSize32) Push32(value); else Push16((ushort)value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint PopOperand() => OperandSize32 ? Pop32() : Pop16();
}
