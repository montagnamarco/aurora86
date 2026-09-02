namespace Emulator.Core.Bios;

/// <summary>
/// Costruisce la ROM del BIOS.
///
/// La ROM contiene pochissimo codice x86 vero: per ogni interruzione un punto
/// di ingresso di tre byte che invoca l'handler nativo e ritorna. Tutto il
/// lavoro -- disegnare caratteri, leggere settori, gestire la tastiera -- e'
/// scritto in C#.
///
/// La scelta e' deliberata. Un BIOS reale non e' ridistribuibile e va
/// procurato dall'utente; ma soprattutto farebbe un POST completo sondando
/// chipset che non emuliamo. Realizzare le funzioni in C# le rende piu'
/// veloci di un'emulazione istruzione per istruzione e molto piu' facili da
/// correggere: un errore si vede in un debugger normale invece che in una
/// traccia di codice macchina.
/// </summary>
public static class BiosRom
{
    /// <summary>Indirizzo fisico dove la ROM viene mappata (ultimi 64 KB del primo MB).</summary>
    public const uint Base = 0xF0000;
    public const int Size = 0x10000;

    /// <summary>Opcode di aggancio agli handler nativi.</summary>
    private const byte HookByte1 = 0x0F;
    private const byte HookByte2 = 0xFF;

    /// <summary>
    /// Offset dei punti di ingresso dentro la ROM. Sono scelti arbitrariamente
    /// ma devono restare stabili: la tabella dei vettori vi punta.
    /// </summary>
    public static class Entry
    {
        public const ushort Video = 0x1000;          // INT 10h
        public const ushort EquipmentList = 0x1010;  // INT 11h
        public const ushort MemorySize = 0x1020;     // INT 12h
        public const ushort Disk = 0x1030;           // INT 13h
        public const ushort Serial = 0x1040;         // INT 14h
        public const ushort System = 0x1050;         // INT 15h
        public const ushort Keyboard = 0x1060;       // INT 16h
        public const ushort Printer = 0x1070;        // INT 17h
        public const ushort Bootstrap = 0x1080;      // INT 19h
        public const ushort TimeOfDay = 0x1090;      // INT 1Ah
        public const ushort TimerTick = 0x10A0;      // INT 08h, IRQ 0
        public const ushort KeyboardIrq = 0x10B0;    // INT 09h, IRQ 1
        public const ushort DummyIrq = 0x10C0;       // IRQ non gestite
        public const ushort Multiplex = 0x10D0;      // INT 2Fh
        public const ushort XmsEntry = 0x10E0;       // punto di ingresso XMS
        public const ushort Reset = 0xFFF0;          // punto di partenza
        public const ushort PostEntry = 0x1100;      // inizializzazione
    }

    /// <summary>
    /// Identificativi passati all'handler nativo dopo l'opcode di aggancio.
    /// </summary>
    public enum Handler : byte
    {
        Video = 0x10,
        EquipmentList = 0x11,
        MemorySize = 0x12,
        Disk = 0x13,
        Serial = 0x14,
        System = 0x15,
        Keyboard = 0x16,
        Printer = 0x17,
        Bootstrap = 0x19,
        TimeOfDay = 0x1A,
        TimerTick = 0x08,
        KeyboardIrq = 0x09,
        DummyIrq = 0xFE,
        Multiplex = 0x2F,
        Xms = 0x43,
        Post = 0xFF,
    }

    public static byte[] Build()
    {
        var rom = new byte[Size];

        // Riempimento con un opcode non valido: se l'esecuzione finisce dove
        // non dovrebbe, si ferma subito invece di vagare per la ROM.
        Array.Fill(rom, (byte)0xCC);

        // Punti di ingresso delle interruzioni: chiamano l'handler nativo e
        // ritornano con IRET, che ripristina anche i flag.
        WriteInterruptEntry(rom, Entry.Video, Handler.Video);
        WriteInterruptEntry(rom, Entry.EquipmentList, Handler.EquipmentList);
        WriteInterruptEntry(rom, Entry.MemorySize, Handler.MemorySize);
        WriteInterruptEntry(rom, Entry.Disk, Handler.Disk);
        WriteInterruptEntry(rom, Entry.Serial, Handler.Serial);
        WriteInterruptEntry(rom, Entry.System, Handler.System);
        WriteInterruptEntry(rom, Entry.Keyboard, Handler.Keyboard);
        WriteInterruptEntry(rom, Entry.Printer, Handler.Printer);
        WriteInterruptEntry(rom, Entry.Bootstrap, Handler.Bootstrap);
        WriteInterruptEntry(rom, Entry.TimeOfDay, Handler.TimeOfDay);
        WriteInterruptEntry(rom, Entry.TimerTick, Handler.TimerTick);
        WriteInterruptEntry(rom, Entry.KeyboardIrq, Handler.KeyboardIrq);
        WriteInterruptEntry(rom, Entry.DummyIrq, Handler.DummyIrq);
        WriteInterruptEntry(rom, Entry.Multiplex, Handler.Multiplex);

        // Il punto di ingresso XMS non e' un gestore di interruzione: i
        // programmi lo raggiungono con una CALL lontana, quindi deve tornare
        // con RETF e non con IRET.
        {
            int xms = Entry.XmsEntry;
            rom[xms + 0] = HookByte1;
            rom[xms + 1] = HookByte2;
            rom[xms + 2] = (byte)Handler.Xms;
            rom[xms + 3] = 0xCB;      // RETF
        }

        // Sequenza di inizializzazione: chiama l'handler di POST e poi salta
        // al caricamento del sistema operativo.
        int offset = Entry.PostEntry;
        rom[offset++] = HookByte1;
        rom[offset++] = HookByte2;
        rom[offset++] = (byte)Handler.Post;
        rom[offset++] = 0xCD;          // INT 19h: carica il settore di avvio
        rom[offset++] = 0x19;
        rom[offset++] = 0xF4;          // HLT, se INT 19h ritorna non c'e' altro da fare

        // Vettore di reset a F000:FFF0. Il processore parte da qui, e il salto
        // porta alla sequenza di inizializzazione.
        offset = Entry.Reset;
        rom[offset++] = 0xEA;                                     // JMP far
        rom[offset++] = unchecked((byte)Entry.PostEntry);          // offset basso
        rom[offset++] = unchecked((byte)(Entry.PostEntry >> 8));   // offset alto
        rom[offset++] = 0x00;                          // segmento F000
        rom[offset++] = 0xF0;

        // Data di rilascio a F000:FFF5, nel formato che alcuni programmi
        // leggono per identificare il BIOS.
        "01/01/93"u8.CopyTo(rom.AsSpan(0xFFF5));

        // Byte di identificazione del modello: 0xFC = AT e compatibili.
        rom[0xFFFE] = 0xFC;

        return rom;
    }

    private static void WriteInterruptEntry(byte[] rom, ushort offset, Handler handler)
    {
        rom[offset + 0] = HookByte1;
        rom[offset + 1] = HookByte2;
        rom[offset + 2] = (byte)handler;
        rom[offset + 3] = 0xCF;        // IRET
    }
}
