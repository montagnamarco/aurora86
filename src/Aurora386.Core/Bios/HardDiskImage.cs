namespace Aurora386.Core.Bios;

/// <summary>
/// Creazione e preparazione di immagini di disco fisso.
///
/// Un disco fisso, a differenza di un floppy, non ha un file system che parte
/// dal primo settore: comincia con il Master Boot Record, che contiene la
/// tabella delle partizioni e il codice che carica il settore di avvio della
/// partizione attiva. E' la struttura che FDISK crea e che DOS si aspetta.
/// </summary>
public static class HardDiskImage
{
    public const int SectorSize = 512;

    /// <summary>
    /// Geometria di un disco fisso. Il BIOS indirizza in CHS, quindi la
    /// dimensione da sola non basta: servono cilindri, testine e settori.
    /// La convenzione piu' diffusa all'epoca era 16 testine e 63 settori per
    /// traccia, con i cilindri a completare.
    /// </summary>
    public readonly record struct Geometry(int Cylinders, int Heads, int SectorsPerTrack)
    {
        public long TotalSectors => (long)Cylinders * Heads * SectorsPerTrack;
        public long TotalBytes => TotalSectors * SectorSize;

        /// <summary>
        /// Ricava una geometria plausibile per la dimensione richiesta.
        /// Il limite dei 1024 cilindri e' quello dell'interfaccia CHS del
        /// BIOS: oltre, i dischi dell'epoca usavano geometrie "tradotte".
        /// </summary>
        public static Geometry ForSize(long bytes)
        {
            const int heads = 16;
            const int sectorsPerTrack = 63;
            int cylinders = (int)(bytes / (heads * sectorsPerTrack * SectorSize));

            if (cylinders < 1) cylinders = 1;
            if (cylinders > 1024) cylinders = 1024;   // limite del CHS classico

            return new Geometry(cylinders, heads, sectorsPerTrack);
        }
    }

    /// <summary>
    /// Tipi di partizione riconosciuti da DOS. Il tipo dice al sistema quale
    /// file system aspettarsi e quanto grande puo' essere la partizione.
    /// </summary>
    public static class PartitionType
    {
        public const byte Fat12 = 0x01;
        /// <summary>FAT16 fino a 32 MB, l'unico che DOS 3.x-4.x gestisce.</summary>
        public const byte Fat16Small = 0x04;
        public const byte Extended = 0x05;
        /// <summary>FAT16 oltre 32 MB, introdotto con DOS 4.0.</summary>
        public const byte Fat16 = 0x06;
    }

    /// <summary>
    /// Crea un'immagine vuota della dimensione richiesta, con MBR e una sola
    /// partizione primaria attiva formattata FAT16 -- cioe' un disco pronto
    /// per ricevere l'installazione di DOS.
    /// </summary>
    public static Geometry Create(string path, long sizeBytes, bool bootable = true)
    {
        Geometry geometry = Geometry.ForSize(sizeBytes);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        stream.SetLength(geometry.TotalBytes);

        // La partizione comincia alla traccia 1: il primo settore della testina
        // 0 e' il MBR, e per convenzione la partizione parte dalla traccia
        // successiva, lasciando spazio ai boot manager.
        int partitionStartSector = geometry.SectorsPerTrack;
        long partitionSectors = geometry.TotalSectors - partitionStartSector;

        byte[] mbr = BuildMasterBootRecord(geometry, partitionStartSector,
                                           partitionSectors, bootable);
        stream.Seek(0, SeekOrigin.Begin);
        stream.Write(mbr);

        // Formatta la partizione come FAT16, cosi' e' subito utilizzabile.
        byte[] bootSector = BuildFat16BootSector(geometry, partitionSectors);
        stream.Seek((long)partitionStartSector * SectorSize, SeekOrigin.Begin);
        stream.Write(bootSector);

        WriteEmptyFatTables(stream, partitionStartSector, bootSector);

        return geometry;
    }

    /// <summary>
    /// Costruisce il Master Boot Record: tabella delle partizioni piu' il
    /// codice che carica e avvia la partizione attiva.
    /// </summary>
    private static byte[] BuildMasterBootRecord(Geometry geometry, int startSector,
                                                long partitionSectors, bool bootable)
    {
        var mbr = new byte[SectorSize];

        // Codice di avvio: si sposta in basso, cerca la partizione attiva,
        // ne carica il primo settore a 0000:7C00 e vi salta. E' quello che fa
        // il MBR scritto da FDISK.
        //
        // Lo spostamento serve perche' il settore di avvio della partizione
        // vuole stare proprio a 0x7C00, dove ora sta il MBR stesso.
        int p = 0;
        void Emit(params byte[] bytes) { foreach (byte b in bytes) mbr[p++] = b; }

        Emit(0xFA);                          // CLI
        Emit(0x33, 0xC0);                    // XOR AX,AX
        Emit(0x8E, 0xD0);                    // MOV SS,AX
        Emit(0xBC, 0x00, 0x7C);              // MOV SP,0x7C00
        Emit(0x8E, 0xD8);                    // MOV DS,AX
        Emit(0x8E, 0xC0);                    // MOV ES,AX
        Emit(0xFB);                          // STI
        Emit(0xFC);                          // CLD (la copia va in avanti)

        Emit(0xBE, 0x00, 0x7C);              // MOV SI,0x7C00
        Emit(0xBF, 0x00, 0x06);              // MOV DI,0x0600
        Emit(0xB9, 0x00, 0x02);              // MOV CX,512
        Emit(0xF3, 0xA4);                    // REP MOVSB

        // Salta alla copia. L'indirizzo va calcolato ora che si conosce la
        // lunghezza del preambolo: il codice prosegue allo stesso offset ma
        // nel blocco spostato.
        int afterJump = p + 5;
        Emit(0xEA);                                        // JMP FAR
        Emit((byte)(0x0600 + afterJump), (byte)((0x0600 + afterJump) >> 8));
        Emit(0x00, 0x00);                                  // segmento 0

        // Da qui in poi l'esecuzione avviene a 0x0600 + offset.
        // La tabella delle partizioni, ricopiata, sta a 0x0600 + 0x1BE = 0x7BE.
        Emit(0xBE, 0xBE, 0x07);              // MOV SI,0x07BE
        Emit(0xB9, 0x04, 0x00);              // MOV CX,4  (quattro voci)

        int searchLoop = 0x0600 + p;
        Emit(0x80, 0x3C, 0x80);              // CMP byte [SI],0x80  (attiva?)
        Emit(0x74, 0x0E);                    // JZ trovata  (+14: salta le 4 istruzioni sotto)
        Emit(0x83, 0xC6, 0x10);              // ADD SI,16   (voce successiva)
        int loopTarget = searchLoop - (0x0600 + p + 2);
        Emit(0xE2, (byte)loopTarget);        // LOOP indietro

        // Nessuna partizione attiva: messaggio e arresto.
        Emit(0xBE, 0x00, 0x07);              // MOV SI,messaggio (0x0700)
        Emit(0xEB, 0x1A);                    // JMP stampa

        // trovata: carica il settore di avvio della partizione.
        Emit(0x8A, 0x14);                    // MOV DL,[SI]      (byte di stato, non l'unita')
        Emit(0xB2, 0x80);                    // MOV DL,0x80      (unita' fissa)
        Emit(0x8B, 0x4C, 0x02);              // MOV CX,[SI+2]    (cilindro e settore)
        Emit(0x8A, 0x74, 0x01);              // MOV DH,[SI+1]    (testina)
        Emit(0xBB, 0x00, 0x7C);              // MOV BX,0x7C00
        Emit(0xB8, 0x01, 0x02);              // MOV AX,0x0201    (leggi 1 settore)
        Emit(0xCD, 0x13);                    // INT 13h
        Emit(0x72, 0x07);                    // JC errore (+7)
        Emit(0xEA, 0x00, 0x7C, 0x00, 0x00);  // JMP FAR 0000:7C00

        // errore di lettura: mostra il messaggio.
        Emit(0xBE, 0x20, 0x07);              // MOV SI,0x0720

        // stampa: scrive la stringa a SI e si ferma.
        Emit(0xAC);                          // LODSB
        Emit(0x08, 0xC0);                    // OR AL,AL
        Emit(0x74, 0x06);                    // JZ fine
        Emit(0xB4, 0x0E);                    // MOV AH,0x0E
        Emit(0xCD, 0x10);                    // INT 10h
        Emit(0xEB, 0xF5);                    // JMP stampa
        Emit(0xFA);                          // CLI
        Emit(0xF4);                          // HLT
        Emit(0xEB, 0xFD);                    // JMP -3

        // I messaggi stanno a offset fissi, cosi' il codice puo' puntarli.
        "Nessuna partizione avviabile\r\n\0"u8.CopyTo(mbr.AsSpan(0x100));
        "Errore di lettura del disco\r\n\0"u8.CopyTo(mbr.AsSpan(0x120));

        // Tabella delle partizioni: quattro voci da 16 byte a partire da 0x1BE.
        WritePartitionEntry(mbr, 0x1BE, geometry, startSector, partitionSectors,
                            PartitionType.Fat16, bootable);

        mbr[0x1FE] = 0x55;
        mbr[0x1FF] = 0xAA;
        return mbr;
    }

    /// <summary>
    /// Scrive una voce della tabella delle partizioni. Gli indirizzi CHS sono
    /// codificati in modo compatto: i due bit alti del cilindro finiscono nei
    /// bit alti del byte del settore.
    /// </summary>
    private static void WritePartitionEntry(byte[] sector, int offset, Geometry geometry,
                                            long startSector, long sectorCount,
                                            byte type, bool active)
    {
        sector[offset + 0] = (byte)(active ? 0x80 : 0x00);

        (int startCyl, int startHead, int startSec) = ToChs(geometry, startSector);
        sector[offset + 1] = (byte)startHead;
        sector[offset + 2] = (byte)((startSec & 0x3F) | ((startCyl >> 2) & 0xC0));
        sector[offset + 3] = (byte)(startCyl & 0xFF);

        sector[offset + 4] = type;

        (int endCyl, int endHead, int endSec) = ToChs(geometry, startSector + sectorCount - 1);
        sector[offset + 5] = (byte)endHead;
        sector[offset + 6] = (byte)((endSec & 0x3F) | ((endCyl >> 2) & 0xC0));
        sector[offset + 7] = (byte)(endCyl & 0xFF);

        BitConverter.GetBytes((uint)startSector).CopyTo(sector, offset + 8);
        BitConverter.GetBytes((uint)sectorCount).CopyTo(sector, offset + 12);
    }

    /// <summary>Converte un settore lineare in cilindro, testina e settore.</summary>
    public static (int Cylinder, int Head, int Sector) ToChs(Geometry geometry, long linear)
    {
        int sector = (int)(linear % geometry.SectorsPerTrack) + 1;   // i settori partono da 1
        long track = linear / geometry.SectorsPerTrack;
        int head = (int)(track % geometry.Heads);
        int cylinder = (int)(track / geometry.Heads);

        // Oltre il limite CHS si riporta il valore massimo, come fanno i
        // dischi reali quando la geometria non basta piu' a descriverli.
        if (cylinder > 1023) { cylinder = 1023; head = geometry.Heads - 1; sector = geometry.SectorsPerTrack; }

        return (cylinder, head, sector);
    }

    /// <summary>
    /// Costruisce il settore di avvio di una partizione FAT16, con il BPB che
    /// descrive il file system. DOS legge questi valori per sapere dove
    /// trovare FAT, directory radice e dati.
    /// </summary>
    private static byte[] BuildFat16BootSector(Geometry geometry, long partitionSectors)
    {
        var boot = new byte[SectorSize];

        boot[0] = 0xEB; boot[1] = 0x3C; boot[2] = 0x90;      // JMP corto + NOP
        "MSDOS5.0"u8.CopyTo(boot.AsSpan(3));

        // La dimensione del cluster determina quanti cluster servono: la FAT16
        // ne gestisce al massimo 65524, quindi su dischi grandi il cluster
        // deve crescere.
        int sectorsPerCluster = ChooseClusterSize(partitionSectors);

        BitConverter.GetBytes((ushort)SectorSize).CopyTo(boot, 11);
        boot[13] = (byte)sectorsPerCluster;
        BitConverter.GetBytes((ushort)1).CopyTo(boot, 14);     // settori riservati
        boot[16] = 2;                                          // due copie della FAT
        BitConverter.GetBytes((ushort)512).CopyTo(boot, 17);   // voci nella radice
        BitConverter.GetBytes((ushort)0).CopyTo(boot, 19);     // settori totali: nel campo a 32 bit
        boot[21] = 0xF8;                                       // disco fisso

        long dataSectors = partitionSectors;
        long clusters = dataSectors / sectorsPerCluster;
        int sectorsPerFat = (int)((clusters * 2 + SectorSize - 1) / SectorSize) + 1;

        BitConverter.GetBytes((ushort)sectorsPerFat).CopyTo(boot, 22);
        BitConverter.GetBytes((ushort)geometry.SectorsPerTrack).CopyTo(boot, 24);
        BitConverter.GetBytes((ushort)geometry.Heads).CopyTo(boot, 26);
        BitConverter.GetBytes((uint)geometry.SectorsPerTrack).CopyTo(boot, 28);  // settori nascosti
        BitConverter.GetBytes((uint)partitionSectors).CopyTo(boot, 32);

        boot[36] = 0x80;                                       // unita' BIOS
        boot[38] = 0x29;                                       // firma del blocco esteso
        BitConverter.GetBytes((uint)0x12345678).CopyTo(boot, 39);
        "NO NAME    "u8.CopyTo(boot.AsSpan(43));
        "FAT16   "u8.CopyTo(boot.AsSpan(54));

        // Messaggio e codice minimo: un disco appena partizionato non e'
        // avviabile finche' SYS non ci copia il sistema.
        int p = 62;
        boot[p++] = 0xBE; boot[p++] = 0x80; boot[p++] = 0x7D;  // MOV SI,messaggio
        boot[p++] = 0xAC;                                       // LODSB
        boot[p++] = 0x08; boot[p++] = 0xC0;                     // OR AL,AL
        boot[p++] = 0x74; boot[p++] = 0x06;                     // JZ fine
        boot[p++] = 0xB4; boot[p++] = 0x0E;                     // MOV AH,0x0E
        boot[p++] = 0xCD; boot[p++] = 0x10;                     // INT 10h
        boot[p++] = 0xEB; boot[p++] = 0xF5;                     // JMP ciclo
        boot[p++] = 0xF4;                                       // HLT
        boot[p++] = 0xEB; boot[p++] = 0xFD;                     // JMP -3

        "Disco non di sistema\r\n\0"u8.CopyTo(boot.AsSpan(0x180));

        boot[0x1FE] = 0x55;
        boot[0x1FF] = 0xAA;
        return boot;
    }

    /// <summary>
    /// Sceglie la dimensione del cluster. La FAT16 ha 65524 cluster utili al
    /// massimo: superandoli servirebbe la FAT32, che DOS 6.22 non conosce.
    /// </summary>
    private static int ChooseClusterSize(long partitionSectors)
    {
        foreach (int candidate in new[] { 4, 8, 16, 32, 64, 128 })
        {
            if (partitionSectors / candidate <= 65524) return candidate;
        }
        return 128;
    }

    /// <summary>
    /// Prepara FAT e directory radice vuote. I primi due elementi della FAT
    /// sono riservati e contengono il descrittore del supporto.
    /// </summary>
    private static void WriteEmptyFatTables(Stream stream, int partitionStart, byte[] bootSector)
    {
        int reservedSectors = BitConverter.ToUInt16(bootSector, 14);
        int fatCount = bootSector[16];
        int sectorsPerFat = BitConverter.ToUInt16(bootSector, 22);
        int rootEntries = BitConverter.ToUInt16(bootSector, 17);

        var emptyFat = new byte[SectorSize];
        emptyFat[0] = 0xF8; emptyFat[1] = 0xFF;    // descrittore del supporto
        emptyFat[2] = 0xFF; emptyFat[3] = 0xFF;    // fine catena

        for (int copy = 0; copy < fatCount; copy++)
        {
            long fatStart = (long)(partitionStart + reservedSectors + copy * sectorsPerFat) * SectorSize;
            stream.Seek(fatStart, SeekOrigin.Begin);
            stream.Write(emptyFat);                 // solo il primo settore ha contenuto

            // Il resto della FAT resta a zero: e' gia' cosi' nel file appena creato.
        }

        // La directory radice azzerata significa "nessun file": anche questa
        // e' gia' a zero nel file appena creato.
        long rootStart = (long)(partitionStart + reservedSectors + fatCount * sectorsPerFat) * SectorSize;
        stream.Seek(rootStart + (long)rootEntries * 32 - 1, SeekOrigin.Begin);
        stream.WriteByte(0);
    }
}
