namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    // Protected mode. Per ora e' implementata la parte che serve a far girare
    // i gestori di memoria estesa come HIMEM.SYS: passano in protected mode
    // solo per copiare blocchi sopra il megabyte e tornano subito indietro,
    // quindi servono i descrittori di segmento ma non i cambi di privilegio,
    // i task gate o le porte di chiamata.

    /// <summary>
    /// Carica un registro di segmento leggendo il descrittore dalla GDT o
    /// dalla LDT. Il selettore e' diviso in indice (bit 15-3), tabella
    /// (bit 2) e livello di privilegio richiesto (bit 1-0).
    /// </summary>
    private void LoadSegmentProtected(SegReg segment, ushort selector)
    {
        ref SegmentCache cache = ref State.Segments[(int)segment];

        // Il selettore nullo si puo' caricare nei segmenti dati: rende il
        // registro inutilizzabile ma non e' un errore. In CS o SS invece si'.
        if ((selector & 0xFFFC) == 0)
        {
            if (segment is SegReg.Cs or SegReg.Ss)
                throw new CpuException(13, selector);

            cache.Selector = selector;
            cache.Base = 0;
            cache.Limit = 0;
            cache.Readable = cache.Writable = false;
            return;
        }

        ulong descrittore = LeggiDescrittore(selector);

        // Il descrittore ha i campi sparsi in modo da restare compatibile con
        // il 286: la base sta in tre pezzi, il limite in due.
        uint baseAddress = (uint)(((descrittore >> 16) & 0xFFFFFF)
                                | ((descrittore >> 32) & 0xFF000000));
        uint limite = (uint)((descrittore & 0xFFFF)
                           | ((descrittore >> 32) & 0x0F0000));

        byte accesso = (byte)(descrittore >> 40);
        byte granularita = (byte)(descrittore >> 52);

        // Il bit di granularita' moltiplica il limite per 4096: il resto della
        // pagina finale e' comunque accessibile, quindi si riempie di uno.
        if ((granularita & 0x08) != 0) limite = (limite << 12) | 0xFFF;

        // Bit 7 dell'accesso: il descrittore e' presente in memoria.
        if ((accesso & 0x80) == 0)
            throw new CpuException(segment == SegReg.Ss ? (byte)12 : (byte)11, selector);

        cache.Selector = selector;
        cache.Base = baseAddress;
        cache.Limit = limite;
        cache.Big = (granularita & 0x04) != 0;
        cache.Dpl = (byte)((accesso >> 5) & 3);

        // Bit 3 dell'accesso distingue codice da dati; per il codice il bit 1
        // dice se e' leggibile, per i dati se e' scrivibile.
        bool codice = (accesso & 0x08) != 0;
        cache.Readable = !codice || (accesso & 0x02) != 0;
        cache.Writable = !codice && (accesso & 0x02) != 0;
        cache.ExpandDown = !codice && (accesso & 0x04) != 0;
    }

    /// <summary>
    /// Legge gli otto byte del descrittore indicato dal selettore. L'indice
    /// deve stare dentro il limite della tabella, altrimenti il selettore non
    /// e' valido.
    /// </summary>
    private ulong LeggiDescrittore(ushort selector)
    {
        bool locale = (selector & 0x04) != 0;
        uint indice = (uint)(selector & 0xFFF8);

        uint tabellaBase = locale ? State.LdtBase : State.GdtBase;
        uint tabellaLimite = locale ? State.LdtLimit : State.GdtLimit;

        if (indice + 7 > tabellaLimite)
            throw new CpuException(13, selector);

        uint indirizzo = tabellaBase + indice;
        return _memory.ReadDword(TranslateLinear(indirizzo, false))
             | ((ulong)_memory.ReadDword(TranslateLinear(indirizzo + 4, false)) << 32);
    }

    private void EnterInterruptProtected(byte vector, uint errorCode)
        => throw new NotImplementedException(
            $"Interruzione {vector} in protected mode non ancora implementata.");

    private void IretProtected()
        => throw new NotImplementedException(
            "IRET in protected mode non ancora implementato.");
}
