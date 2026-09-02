namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>
    /// Traduzione lineare -> fisico attraverso la page directory del 386.
    ///
    /// Struttura a due livelli: i 10 bit alti indicizzano la directory, i 10
    /// successivi la tabella, i 12 bassi sono l'offset nella pagina.
    ///
    /// Non c'e' ancora un TLB: ogni accesso costa due letture di memoria. Va
    /// bene finche' il paging non e' il percorso caldo (in real mode non viene
    /// mai chiamata), ma andra' aggiunto insieme al protected mode, perche'
    /// Windows 3.1 in enhanced mode passa da qui a ogni singolo accesso.
    /// </summary>
    private uint TranslatePaged(uint linear, bool write)
    {
        uint directoryIndex = linear >> 22;
        uint tableIndex = (linear >> 12) & 0x3FF;
        uint offset = linear & 0xFFF;

        uint directoryEntry = _memory.ReadDword((State.Cr3 & 0xFFFFF000) + directoryIndex * 4);
        if ((directoryEntry & PagePresent) == 0)
            throw new PageFaultException(linear, write, present: false);

        uint tableEntry = _memory.ReadDword((directoryEntry & 0xFFFFF000) + tableIndex * 4);
        if ((tableEntry & PagePresent) == 0)
            throw new PageFaultException(linear, write, present: false);

        // I bit di accesso e scrittura vanno aggiornati sul ferro reale: i
        // sistemi operativi li usano per decidere cosa scaricare su disco.
        uint updated = tableEntry | PageAccessed | (write ? PageDirty : 0);
        if (updated != tableEntry)
            _memory.WriteDword((directoryEntry & 0xFFFFF000) + tableIndex * 4, updated);

        return (tableEntry & 0xFFFFF000) | offset;
    }

    private const uint PagePresent = 1u << 0;
    private const uint PageWritable = 1u << 1;
    private const uint PageUser = 1u << 2;
    private const uint PageAccessed = 1u << 5;
    private const uint PageDirty = 1u << 6;
}

/// <summary>
/// Page fault (eccezione 14). Porta con se' l'indirizzo che l'ha causata, che
/// il gestore leggera' da CR2.
/// </summary>
public sealed class PageFaultException(uint linearAddress, bool write, bool present)
    : Exception($"Page fault a 0x{linearAddress:X8}")
{
    public uint LinearAddress { get; } = linearAddress;
    public bool Write { get; } = write;
    public bool Present { get; } = present;
}
