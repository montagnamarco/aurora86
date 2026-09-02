namespace Aurora386.Core.Bios;

/// <summary>
/// Immagine di un disco, floppy o fisso.
///
/// Il BIOS indirizza i settori in geometria CHS (cilindro, testina, settore),
/// eredita' del funzionamento meccanico dei dischi: la numerazione dei settori
/// parte da 1, non da 0, mentre testine e cilindri partono da 0.
/// </summary>
public sealed class DiskImage : IDisposable
{
    public const int SectorSize = 512;

    private readonly Stream _stream;
    private readonly bool _ownsStream;

    public int Cylinders { get; }
    public int Heads { get; }
    public int SectorsPerTrack { get; }
    public bool IsHardDisk { get; }
    public bool ReadOnly { get; }

    /// <summary>Numero di unita' BIOS: 0x00 primo floppy, 0x80 primo disco fisso.</summary>
    public byte DriveNumber { get; }

    public long TotalSectors => (long)Cylinders * Heads * SectorsPerTrack;

    public DiskImage(Stream stream, byte driveNumber, int cylinders, int heads,
                     int sectorsPerTrack, bool readOnly = false, bool ownsStream = true)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        DriveNumber = driveNumber;
        Cylinders = cylinders;
        Heads = heads;
        SectorsPerTrack = sectorsPerTrack;
        IsHardDisk = driveNumber >= 0x80;
        ReadOnly = readOnly || !stream.CanWrite;
    }

    /// <summary>
    /// Apre un'immagine deducendone la geometria dalla dimensione. I formati
    /// dei floppy sono standardizzati, quindi la dimensione li identifica
    /// senza ambiguita'.
    /// </summary>
    public static DiskImage OpenFloppy(string path, byte driveNumber = 0x00, bool readOnly = false)
    {
        var info = new FileInfo(path);
        (int cylinders, int heads, int sectors) = info.Length switch
        {
            368_640 => (40, 2, 9),      // 360 KB
            737_280 => (80, 2, 9),      // 720 KB
            1_228_800 => (80, 2, 15),   // 1.2 MB
            1_474_560 => (80, 2, 18),   // 1.44 MB
            2_949_120 => (80, 2, 36),   // 2.88 MB
            _ => throw new InvalidDataException(
                $"Dimensione {info.Length} non corrisponde a un formato floppy noto."),
        };

        FileStream stream = OpenShared(path, readOnly);
        return new DiskImage(stream, driveNumber, cylinders, heads, sectors, readOnly);
    }

    /// <summary>
    /// Apre un disco fisso. La geometria non e' deducibile dalla dimensione,
    /// quindi si usa la convenzione piu' diffusa (63 settori, 16 testine) e si
    /// ricavano i cilindri.
    /// </summary>
    public static DiskImage OpenHardDisk(string path, byte driveNumber = 0x80, bool readOnly = false)
    {
        var info = new FileInfo(path);
        HardDiskImage.Geometry geometry = HardDiskImage.Geometry.ForSize(info.Length);

        if (geometry.Cylinders == 0)
            throw new InvalidDataException("L'immagine e' troppo piccola per essere un disco fisso.");

        FileStream stream = OpenShared(path, readOnly);
        return new DiskImage(stream, driveNumber, geometry.Cylinders, geometry.Heads,
                             geometry.SectorsPerTrack, readOnly);
    }

    /// <summary>
    /// Crea un'immagine di disco fisso partizionata e formattata, poi la apre.
    /// E' cio' che serve per installarci sopra un sistema operativo.
    /// </summary>
    public static DiskImage CreateHardDisk(string path, long sizeBytes, byte driveNumber = 0x80)
    {
        HardDiskImage.Create(path, sizeBytes);
        return OpenHardDisk(path, driveNumber);
    }

    /// <summary>
    /// Apre un'immagine condividendola con gli altri processi.
    ///
    /// Un'immagine aperta in sola lettura non deve impedire a nessun altro di
    /// usarla: capita spesso di ispezionare lo stesso file con piu' strumenti
    /// contemporaneamente. Anche in lettura e scrittura si consente la
    /// lettura altrui, cosi' un errore si manifesta solo quando due processi
    /// vogliono davvero scrivere.
    /// </summary>
    private static FileStream OpenShared(string path, bool readOnly)
    {
        FileAccess access = readOnly ? FileAccess.Read : FileAccess.ReadWrite;
        FileShare share = readOnly ? FileShare.ReadWrite : FileShare.Read;

        try
        {
            return new FileStream(path, FileMode.Open, access, share);
        }
        catch (IOException ex)
        {
            // L'eccezione originale dice solo "file in uso": aggiungiamo
            // l'informazione che serve davvero per risolvere.
            throw new IOException(
                $"Impossibile aprire {Path.GetFileName(path)}: il file e' in uso da un altro " +
                $"programma (un emulatore rimasto aperto, per esempio). " +
                $"Chiudilo e riprova.", ex);
        }
    }

    /// <summary>Crea un'immagine in memoria, per i test.</summary>
    public static DiskImage CreateInMemory(byte driveNumber, int cylinders, int heads, int sectorsPerTrack)
    {
        long size = (long)cylinders * heads * sectorsPerTrack * SectorSize;
        return new DiskImage(new MemoryStream(new byte[size]), driveNumber,
                             cylinders, heads, sectorsPerTrack);
    }

    /// <summary>
    /// Converte CHS in numero di settore lineare. I settori partono da 1:
    /// il settore 0 non esiste, ed e' una fonte classica di errori.
    /// </summary>
    public long ToLinearSector(int cylinder, int head, int sector)
        => ((long)cylinder * Heads + head) * SectorsPerTrack + (sector - 1);

    public bool IsValidChs(int cylinder, int head, int sector)
        => cylinder >= 0 && cylinder < Cylinders
        && head >= 0 && head < Heads
        && sector >= 1 && sector <= SectorsPerTrack;

    public bool ReadSectors(int cylinder, int head, int sector, int count, Span<byte> destination)
    {
        if (!IsValidChs(cylinder, head, sector)) return false;

        long linear = ToLinearSector(cylinder, head, sector);
        if (linear + count > TotalSectors) return false;

        _stream.Seek(linear * SectorSize, SeekOrigin.Begin);
        int wanted = count * SectorSize;
        int read = _stream.ReadAtLeast(destination[..wanted], wanted, throwOnEndOfStream: false);
        return read == wanted;
    }

    public bool WriteSectors(int cylinder, int head, int sector, int count, ReadOnlySpan<byte> source)
    {
        if (ReadOnly) return false;
        if (!IsValidChs(cylinder, head, sector)) return false;

        long linear = ToLinearSector(cylinder, head, sector);
        if (linear + count > TotalSectors) return false;

        _stream.Seek(linear * SectorSize, SeekOrigin.Begin);
        _stream.Write(source[..(count * SectorSize)]);
        _stream.Flush();
        return true;
    }

    public void Dispose()
    {
        if (_ownsStream) _stream.Dispose();
    }
}
