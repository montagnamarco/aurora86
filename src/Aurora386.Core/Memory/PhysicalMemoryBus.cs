using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aurora386.Core.Memory;

/// <summary>
/// Bus di memoria fisica del 386. Lo spazio di indirizzamento e' diviso in
/// pagine da 4 KB; ogni pagina e' o RAM diretta (puntatore non nullo, percorso
/// veloce senza indirezioni) o un dispositivo mappato (handler).
///
/// Il 386 ha 32 bit di indirizzamento, ma popolare una tabella per l'intero
/// spazio da 4 GB costerebbe 1M di voci. Poiche' la macchina emulata ha 16 MB
/// di RAM e non esistono dispositivi oltre, la tabella copre i primi 16 MB;
/// tutto cio' che sta sopra legge 0xFF e ignora le scritture, come un bus
/// senza nulla collegato.
/// </summary>
public sealed unsafe class PhysicalMemoryBus : IDisposable
{
    public const int PageShift = 12;
    public const int PageSize = 1 << PageShift;   // 4096
    public const uint PageMask = PageSize - 1;

    private readonly uint _addressableBytes;
    private readonly int _pageCount;

    /// <summary>Puntatore alla RAM della pagina, o null se e' MMIO/non mappata.</summary>
    private readonly byte*[] _pageRam;

    /// <summary>Dispositivo che serve la pagina, quando non e' RAM diretta.</summary>
    private readonly IMemoryDevice?[] _pageDevice;

    /// <summary>Base fisica del dispositivo, per convertire in offset relativo.</summary>
    private readonly uint[] _pageDeviceBase;

    /// <summary>Pagine di sola lettura (ROM): le scritture vengono scartate.</summary>
    private readonly bool[] _pageReadOnly;

    private readonly byte* _ram;
    private readonly uint _ramSize;
    private bool _disposed;

    /// <summary>
    /// Rimappatura degli indirizzi alti sulla ROM del BIOS.
    ///
    /// Dopo il reset il 386 fetcha da 0xFFFFFFF0, vicino al limite dei 4 GB,
    /// mentre la ROM sta nell'ultimo tratto del primo megabyte. Su una scheda
    /// reale e' un circuito del chipset a far coincidere le due cose; qui si
    /// ottiene lo stesso effetto traducendo gli indirizzi che ricadono
    /// nell'ultimo megabyte dello spazio a 32 bit, senza dover allocare una
    /// tabella di pagine per 4 GB.
    /// </summary>
    private uint _highAliasStart;
    private uint _highAliasTarget;
    private bool _highAliasEnabled;

    /// <summary>
    /// Stato della linea di indirizzo A20.
    ///
    /// Sull'8086 gli indirizzi erano a 20 bit: FFFF:0010 non arrivava a
    /// 0x100000 ma avvolgeva a 0x00000, e parecchio software ci contava.
    /// L'AT aggiunse la ventunesima linea ma la lascio' disattivata di
    /// default proprio per non rompere quel software; da allora abilitarla e'
    /// compito del sistema operativo.
    ///
    /// Con A20 disattivata il bit 20 dell'indirizzo viene forzato a zero, e
    /// i 64 KB appena sopra il megabyte (la HMA) risultano inaccessibili --
    /// e' esattamente lo spazio in cui DOS 6.22 si sposta con DOS=HIGH.
    /// </summary>
    /// <summary>
    /// Un PC reale accende con A20 <b>disattivata</b>, per compatibilita' con
    /// il software scritto per l'8086. Abilitarla e' compito del sistema
    /// operativo o del gestore di memoria estesa.
    /// </summary>
    private bool _a20Enabled;

    /// <summary>Maschera applicata a ogni indirizzo: esclude il bit 20 quando A20 e' spenta.</summary>
    private uint _addressMask = ~(1u << 20);

    /// <summary>
    /// Abilita o disabilita la linea A20. Cambiarla altera il significato di
    /// ogni indirizzo sopra il megabyte, quindi va fatto solo dal controller
    /// della tastiera o dalla porta 0x92, come sul ferro reale.
    /// </summary>
    public bool A20Enabled
    {
        get => _a20Enabled;
        set
        {
            _a20Enabled = value;
            // Il wrapping dell'8086 azzera il bit 20 lasciando intatti gli
            // altri: non tronca l'indirizzo a 20 bit, altrimenti nemmeno la
            // memoria estesa oltre i 2 MB sarebbe raggiungibile.
            _addressMask = value ? 0xFFFFFFFF : ~(1u << 20);
        }
    }

    public uint RamSize => _ramSize;

    public PhysicalMemoryBus(uint ramSize = 16 * 1024 * 1024, uint addressableBytes = 16 * 1024 * 1024)
    {
        if (ramSize == 0 || (ramSize & PageMask) != 0)
            throw new ArgumentException("La dimensione della RAM deve essere un multiplo di 4 KB.", nameof(ramSize));
        if (addressableBytes < ramSize)
            throw new ArgumentException("Lo spazio indirizzabile non puo' essere minore della RAM.", nameof(addressableBytes));

        _ramSize = ramSize;
        _addressableBytes = addressableBytes;
        _pageCount = (int)(addressableBytes >> PageShift);

        // Allocazione non gestita e azzerata: la RAM non deve essere spostata dal
        // GC ne' scansionata, e all'accensione una macchina reale ha memoria a zero.
        _ram = (byte*)NativeMemory.AllocZeroed(ramSize);

        _pageRam = new byte*[_pageCount];
        _pageDevice = new IMemoryDevice?[_pageCount];
        _pageDeviceBase = new uint[_pageCount];
        _pageReadOnly = new bool[_pageCount];

        // Di default ogni pagina entro la RAM installata punta alla RAM.
        for (int i = 0; i < _pageCount; i++)
        {
            uint physical = (uint)i << PageShift;
            _pageRam[i] = physical < ramSize ? _ram + physical : null;
        }
    }

    /// <summary>
    /// Mappa un dispositivo su un intervallo fisico, sostituendo la RAM che vi
    /// si trovava (es. il framebuffer VGA che copre 0xA0000-0xBFFFF).
    /// </summary>
    public void MapDevice(uint physicalBase, uint length, IMemoryDevice device)
    {
        ValidateRange(physicalBase, length);
        int first = (int)(physicalBase >> PageShift);
        int count = (int)(length >> PageShift);

        for (int i = 0; i < count; i++)
        {
            int page = first + i;
            _pageRam[page] = null;              // forza il percorso lento
            _pageDevice[page] = device;
            _pageDeviceBase[page] = physicalBase;
            _pageReadOnly[page] = false;
        }
    }

    /// <summary>
    /// Mappa una ROM: il contenuto viene copiato nella RAM sottostante e la
    /// pagina resa di sola lettura. Le letture restano cosi' sul percorso
    /// veloce (una ROM viene letta molto piu' spesso di quanto non venga
    /// scritta, cioe' mai), e le scritture vengono scartate silenziosamente.
    /// </summary>
    public void MapRom(uint physicalBase, ReadOnlySpan<byte> content)
    {
        uint length = AlignUp((uint)content.Length);
        ValidateRange(physicalBase, length);

        if (physicalBase + length > _ramSize)
            throw new ArgumentException("La ROM deve risiedere entro la RAM installata.", nameof(physicalBase));

        content.CopyTo(new Span<byte>(_ram + physicalBase, content.Length));

        int first = (int)(physicalBase >> PageShift);
        int count = (int)(length >> PageShift);
        for (int i = 0; i < count; i++)
        {
            int page = first + i;
            _pageRam[page] = _ram + ((uint)page << PageShift);
            _pageDevice[page] = null;
            _pageReadOnly[page] = true;
        }
    }

    /// <summary>
    /// Fa in modo che gli indirizzi da <paramref name="aliasStart"/> alla fine
    /// dello spazio a 32 bit vengano serviti a partire da
    /// <paramref name="target"/>. Serve a rendere la ROM raggiungibile
    /// all'indirizzo di reset del 386.
    /// </summary>
    public void MapHighAlias(uint aliasStart, uint target)
    {
        _highAliasStart = aliasStart;
        _highAliasTarget = target;
        _highAliasEnabled = true;
    }

    /// <summary>
    /// Traduce un indirizzo fisico prima di ogni accesso.
    ///
    /// L'ordine e' importante e non e' quello che verrebbe naturale. La
    /// finestra alta (l'accesso alla ROM vicino al limite dei 4 GB, da cui il
    /// 386 parte dopo il reset) viene decodificata dal chipset <b>prima</b>
    /// che A20 entri in gioco: quegli indirizzi hanno il bit 20 acceso, e se
    /// A20 li mascherasse nessun PC riuscirebbe ad avviarsi con la linea
    /// disattivata -- che e' proprio lo stato in cui un PC reale accende.
    ///
    /// Il mascheramento di A20 riguarda quindi solo gli indirizzi che il
    /// programma genera davvero, non la rimappatura del vettore di reset.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint Translate(uint address)
    {
        // Finestra alta: decodificata per prima, senza mascheramento.
        if (_highAliasEnabled && address >= _highAliasStart)
            return _highAliasTarget + (address - _highAliasStart);

        return address & _addressMask;
    }

    /// <summary>Ripristina un intervallo a RAM normale scrivibile.</summary>
    public void MapRam(uint physicalBase, uint length)
    {
        ValidateRange(physicalBase, length);
        if (physicalBase + length > _ramSize)
            throw new ArgumentException("L'intervallo eccede la RAM installata.", nameof(physicalBase));

        int first = (int)(physicalBase >> PageShift);
        int count = (int)(length >> PageShift);
        for (int i = 0; i < count; i++)
        {
            int page = first + i;
            _pageRam[page] = _ram + ((uint)page << PageShift);
            _pageDevice[page] = null;
            _pageReadOnly[page] = false;
        }
    }

    // ---------------------------------------------------------------- letture

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte(uint address)
    {
        address = Translate(address);
        if (address >= _addressableBytes) return 0xFF;

        byte* page = _pageRam[address >> PageShift];
        if (page is not null)
            return page[address & PageMask];

        return ReadByteSlow(address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadWord(uint address)
    {
        address = Translate(address);

        // Percorso veloce: l'accesso non attraversa il confine di pagina e la
        // pagina e' RAM. Gli accessi disallineati sono leciti su x86, quindi
        // va comunque verificato che i due byte stiano nella stessa pagina.
        if (address < _addressableBytes && (address & PageMask) <= PageMask - 1)
        {
            byte* page = _pageRam[address >> PageShift];
            if (page is not null)
                return Unsafe.ReadUnaligned<ushort>(page + (address & PageMask));
        }
        return ReadWordSlow(address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadDword(uint address)
    {
        address = Translate(address);

        if (address < _addressableBytes && (address & PageMask) <= PageMask - 3)
        {
            byte* page = _pageRam[address >> PageShift];
            if (page is not null)
                return Unsafe.ReadUnaligned<uint>(page + (address & PageMask));
        }
        return ReadDwordSlow(address);
    }

    private byte ReadByteSlow(uint address)
    {
        int index = (int)(address >> PageShift);
        IMemoryDevice? device = _pageDevice[index];
        return device is null ? (byte)0xFF : device.ReadByte(address - _pageDeviceBase[index]);
    }

    /// <summary>
    /// Legge un byte da un indirizzo <b>gia' tradotto</b>. Usata solo dai
    /// percorsi lenti, che ricevono l'indirizzo da Translate() e non devono
    /// applicarlo una seconda volta.
    /// </summary>
    private byte ReadByteTranslated(uint address)
    {
        if (address >= _addressableBytes) return 0xFF;

        byte* page = _pageRam[address >> PageShift];
        if (page is not null) return page[address & PageMask];

        return ReadByteSlow(address);
    }

    private void WriteByteTranslated(uint address, byte value)
    {
        if (address >= _addressableBytes) return;

        uint index = address >> PageShift;
        byte* page = _pageRam[index];
        if (page is not null)
        {
            if (_pageReadOnly[index]) return;
            page[address & PageMask] = value;
            return;
        }
        WriteByteSlow(address, value);
    }

    private ushort ReadWordSlow(uint address)
    {
        int index = (int)(address >> PageShift);
        // Un accesso interamente dentro una pagina MMIO va servito con una sola
        // chiamata: certi registri hanno effetti collaterali sulla lettura, e
        // spezzarla in due byte li innescherebbe due volte.
        if (address < _addressableBytes && (address & PageMask) <= PageMask - 1)
        {
            IMemoryDevice? device = _pageDevice[index];
            if (device is not null)
                return device.ReadWord(address - _pageDeviceBase[index]);
        }
        return (ushort)(ReadByteTranslated(address)
                      | (ReadByteTranslated(address + 1) << 8));
    }

    private uint ReadDwordSlow(uint address)
    {
        int index = (int)(address >> PageShift);
        if (address < _addressableBytes && (address & PageMask) <= PageMask - 3)
        {
            IMemoryDevice? device = _pageDevice[index];
            if (device is not null)
                return device.ReadDword(address - _pageDeviceBase[index]);
        }
        return (uint)(ReadWordSlow(address) | (ReadWordSlow(address + 2) << 16));
    }

    // -------------------------------------------------------------- scritture

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteByte(uint address, byte value)
    {
        address = Translate(address);
        if (address >= _addressableBytes) return;

        uint index = address >> PageShift;
        byte* page = _pageRam[index];
        if (page is not null)
        {
            if (_pageReadOnly[index]) return;   // scrittura su ROM: scartata
            page[address & PageMask] = value;
            return;
        }
        WriteByteSlow(address, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteWord(uint address, ushort value)
    {
        address = Translate(address);

        if (address < _addressableBytes && (address & PageMask) <= PageMask - 1)
        {
            uint index = address >> PageShift;
            byte* page = _pageRam[index];
            if (page is not null)
            {
                if (_pageReadOnly[index]) return;
                Unsafe.WriteUnaligned(page + (address & PageMask), value);
                return;
            }
        }
        WriteWordSlow(address, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteDword(uint address, uint value)
    {
        address = Translate(address);

        if (address < _addressableBytes && (address & PageMask) <= PageMask - 3)
        {
            uint index = address >> PageShift;
            byte* page = _pageRam[index];
            if (page is not null)
            {
                if (_pageReadOnly[index]) return;
                Unsafe.WriteUnaligned(page + (address & PageMask), value);
                return;
            }
        }
        WriteDwordSlow(address, value);
    }

    private void WriteByteSlow(uint address, byte value)
    {
        int index = (int)(address >> PageShift);
        _pageDevice[index]?.WriteByte(address - _pageDeviceBase[index], value);
    }

    private void WriteWordSlow(uint address, ushort value)
    {
        int index = (int)(address >> PageShift);
        if (address < _addressableBytes && (address & PageMask) <= PageMask - 1)
        {
            IMemoryDevice? device = _pageDevice[index];
            if (device is not null)
            {
                device.WriteWord(address - _pageDeviceBase[index], value);
                return;
            }
        }
        WriteByteTranslated(address, (byte)value);
        WriteByteTranslated(address + 1, (byte)(value >> 8));
    }

    private void WriteDwordSlow(uint address, uint value)
    {
        int index = (int)(address >> PageShift);
        if (address < _addressableBytes && (address & PageMask) <= PageMask - 3)
        {
            IMemoryDevice? device = _pageDevice[index];
            if (device is not null)
            {
                device.WriteDword(address - _pageDeviceBase[index], value);
                return;
            }
        }
        WriteWordSlow(address, (ushort)value);
        WriteWordSlow(address + 2, (ushort)(value >> 16));
    }

    // ----------------------------------------------------------------- utilita'

    /// <summary>
    /// Accesso diretto alla RAM per DMA e caricamento immagini disco, che non
    /// devono passare per la logica di mappatura.
    /// </summary>
    public Span<byte> GetRamSpan(uint physicalBase, int length)
    {
        if (physicalBase + (uint)length > _ramSize)
            throw new ArgumentOutOfRangeException(nameof(length), "L'intervallo eccede la RAM installata.");
        return new Span<byte>(_ram + physicalBase, length);
    }

    private static uint AlignUp(uint value) => (value + PageMask) & ~PageMask;

    private void ValidateRange(uint physicalBase, uint length)
    {
        if ((physicalBase & PageMask) != 0)
            throw new ArgumentException("La base deve essere allineata a 4 KB.", nameof(physicalBase));
        if (length == 0 || (length & PageMask) != 0)
            throw new ArgumentException("La lunghezza deve essere un multiplo di 4 KB.", nameof(length));
        if (physicalBase + length > _addressableBytes)
            throw new ArgumentOutOfRangeException(nameof(length), "L'intervallo eccede lo spazio indirizzabile.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NativeMemory.Free(_ram);
    }
}
