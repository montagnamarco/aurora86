using Emulator.Core.Memory;

namespace Emulator.Core.Bios;

/// <summary>
/// BIOS Data Area: la zona a 0x400 dove il BIOS pubblica lo stato della
/// macchina. DOS e i programmi la leggono direttamente, quindi i campi devono
/// stare esattamente agli offset storici -- sono parte dell'interfaccia
/// quanto le chiamate INT.
/// </summary>
public sealed class BiosDataArea
{
    public const uint Base = 0x400;

    private readonly PhysicalMemoryBus _memory;

    public BiosDataArea(PhysicalMemoryBus memory) => _memory = memory;

    private ushort ReadWord(uint offset) => _memory.ReadWord(Base + offset);
    private void WriteWord(uint offset, ushort value) => _memory.WriteWord(Base + offset, value);
    private byte ReadByte(uint offset) => _memory.ReadByte(Base + offset);
    private void WriteByte(uint offset, byte value) => _memory.WriteByte(Base + offset, value);

    /// <summary>Porta base della prima seriale (0x400).</summary>
    public ushort Com1Port { get => ReadWord(0x00); set => WriteWord(0x00, value); }

    /// <summary>
    /// Parola di configurazione (0x410): quanti floppy, che tipo di video,
    /// quanta memoria. E' la prima cosa che il POST scrive.
    /// </summary>
    public ushort EquipmentWord { get => ReadWord(0x10); set => WriteWord(0x10, value); }

    /// <summary>Memoria convenzionale in KB (0x413), tipicamente 640.</summary>
    public ushort MemorySizeKb { get => ReadWord(0x13); set => WriteWord(0x13, value); }

    /// <summary>
    /// Stato dei modificatori della tastiera (0x417): shift, ctrl, alt,
    /// bloc maiusc. I programmi lo leggono per sapere se shift e' premuto.
    /// </summary>
    public byte KeyboardFlags { get => ReadByte(0x17); set => WriteByte(0x17, value); }
    public byte KeyboardFlags2 { get => ReadByte(0x18); set => WriteByte(0x18, value); }

    /// <summary>
    /// Buffer circolare della tastiera (0x41E-0x43D): 16 voci da due byte,
    /// codice ASCII e scancode. Testa e coda stanno a 0x41A e 0x41C.
    /// </summary>
    public ushort KeyboardBufferHead { get => ReadWord(0x1A); set => WriteWord(0x1A, value); }
    public ushort KeyboardBufferTail { get => ReadWord(0x1C); set => WriteWord(0x1C, value); }

    /// <summary>
    /// Esito dell'ultima operazione su disco (0x441). La funzione 0x01 di
    /// INT 13h non fa altro che restituire questo byte, e alcuni programmi lo
    /// leggono direttamente invece di chiamarla.
    /// </summary>
    public byte DiskStatus { get => ReadByte(0x41); set => WriteByte(0x41, value); }

    /// <summary>Esito dell'ultima operazione su disco fisso (0x474).</summary>
    public byte HardDiskStatus { get => ReadByte(0x74); set => WriteByte(0x74, value); }

    /// <summary>Numero di dischi fissi installati (0x475).</summary>
    public byte HardDiskCount { get => ReadByte(0x75); set => WriteByte(0x75, value); }

    public const uint KeyboardBufferStart = 0x1E;
    public const uint KeyboardBufferEnd = 0x3E;

    /// <summary>Modalita' video corrente (0x449).</summary>
    public byte VideoMode { get => ReadByte(0x49); set => WriteByte(0x49, value); }

    /// <summary>Colonne dello schermo (0x44A).</summary>
    public ushort VideoColumns { get => ReadWord(0x4A); set => WriteWord(0x4A, value); }

    /// <summary>Dimensione della pagina video in byte (0x44C).</summary>
    public ushort VideoPageSize { get => ReadWord(0x4C); set => WriteWord(0x4C, value); }

    /// <summary>Offset della pagina video attiva (0x44E).</summary>
    public ushort VideoPageOffset { get => ReadWord(0x4E); set => WriteWord(0x4E, value); }

    /// <summary>Posizione del cursore per la pagina 0 (0x450): colonna e riga.</summary>
    public byte CursorColumn { get => ReadByte(0x50); set => WriteByte(0x50, value); }
    public byte CursorRow { get => ReadByte(0x51); set => WriteByte(0x51, value); }

    /// <summary>Forma del cursore (0x460): riga finale e iniziale.</summary>
    public ushort CursorShape { get => ReadWord(0x60); set => WriteWord(0x60, value); }

    /// <summary>Pagina video attiva (0x462).</summary>
    public byte ActivePage { get => ReadByte(0x62); set => WriteByte(0x62, value); }

    /// <summary>Porta base del CRTC (0x463): 0x3D4 a colori, 0x3B4 monocromatico.</summary>
    public ushort CrtcPort { get => ReadWord(0x63); set => WriteWord(0x63, value); }

    /// <summary>
    /// Contatore dei tick dall'avvio (0x46C), incrementato 18,2 volte al
    /// secondo dal gestore di IRQ 0. DOS ci ricava l'ora corrente.
    /// </summary>
    public uint TimerTicks
    {
        get => _memory.ReadDword(Base + 0x6C);
        set => _memory.WriteDword(Base + 0x6C, value);
    }

    /// <summary>Flag di mezzanotte (0x470): il contatore ha superato le 24 ore.</summary>
    public byte MidnightFlag { get => ReadByte(0x70); set => WriteByte(0x70, value); }

    /// <summary>Righe dello schermo meno una (0x484): 24 per il modo 80x25.</summary>
    public byte VideoRowsMinusOne { get => ReadByte(0x84); set => WriteByte(0x84, value); }

    /// <summary>Altezza dei caratteri in pixel (0x485).</summary>
    public ushort CharacterHeight { get => ReadWord(0x85); set => WriteWord(0x85, value); }

    /// <summary>
    /// Prepara la BDA come farebbe il POST su una macchina con VGA a colori,
    /// un floppy e la quantita' di memoria indicata.
    /// </summary>
    public void Initialize(ushort conventionalMemoryKb, int hardDisks)
    {
        Com1Port = 0;

        // Bit 0: floppy presente. Bit 4-5: 10 = video a colori 80 colonne.
        // Bit 6-7: numero di floppy meno uno.
        EquipmentWord = 0b0000_0000_0010_0001;

        MemorySizeKb = conventionalMemoryKb;

        KeyboardFlags = 0;
        KeyboardFlags2 = 0;
        // Testa e coda sono offset dentro il segmento 0x40, non indirizzi
        // assoluti: i programmi che leggono il buffer da soli calcolano
        // 0040:[041A], e con un valore assoluto finirebbero 0x400 byte piu'
        // avanti. E' la convenzione del PC, non una scelta.
        KeyboardBufferHead = (ushort)KeyboardBufferStart;
        KeyboardBufferTail = (ushort)KeyboardBufferStart;

        VideoMode = 0x03;              // testo 80x25 a colori
        VideoColumns = 80;
        VideoPageSize = 4000;          // 80 * 25 * 2 byte
        VideoPageOffset = 0;
        CursorColumn = 0;
        CursorRow = 0;
        CursorShape = 0x0607;          // cursore a sottolineatura
        ActivePage = 0;
        CrtcPort = 0x3D4;
        VideoRowsMinusOne = 24;
        CharacterHeight = 16;

        TimerTicks = 0;
        MidnightFlag = 0;
        HardDiskCount = (byte)hardDisks;
    }
}
