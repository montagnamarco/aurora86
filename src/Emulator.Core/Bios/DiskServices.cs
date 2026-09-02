using Emulator.Core.Cpu;
using CpuNs = Emulator.Core.Cpu;

namespace Emulator.Core.Bios;

/// <summary>
/// Funzioni disco del BIOS (INT 13h). DOS le usa per ogni lettura e scrittura:
/// il file system e' costruito sopra queste chiamate.
///
/// Le funzioni segnalano l'esito con il carry -- attivo significa errore, e il
/// codice sta in AH. Poiche' il carry attuale verrebbe sovrascritto dall'IRET
/// del punto di ingresso, va modificato nei flag impilati.
/// </summary>
public sealed class DiskServices
{
    private readonly Dictionary<byte, DiskImage> _drives = new();
    private readonly BiosDataArea _bda;

    /// <summary>Codice di errore dell'ultima operazione, per la funzione 0x01.</summary>
    private byte _lastStatus;

    public DiskServices(BiosDataArea bda) => _bda = bda;

    public void Attach(DiskImage image) => _drives[image.DriveNumber] = image;

    public DiskImage? GetDrive(byte driveNumber)
        => _drives.TryGetValue(driveNumber, out DiskImage? image) ? image : null;

    public int HardDiskCount => _drives.Keys.Count(d => d >= 0x80);

    public void Handle(CpuNs.Cpu cpu)
    {
        byte function = cpu.State.GetReg8(4);   // AH
        byte drive = cpu.State.GetReg8(2);      // DL

        switch (function)
        {
            case 0x00: Reset(cpu); return;
            case 0x01: GetStatus(cpu); return;
            case 0x02: ReadSectors(cpu, drive); return;
            case 0x03: WriteSectors(cpu, drive); return;
            case 0x04: VerifySectors(cpu, drive); return;
            case 0x08: GetDriveParameters(cpu, drive); return;
            case 0x15: GetDiskType(cpu, drive); return;
            case 0x16: DetectDiskChange(cpu, drive); return;

            case 0x41:
                // Verifica del supporto alle estensioni per dischi grandi.
                // Non sono implementate: DOS 6.22 usa solo il CHS classico.
                Fail(cpu, 0x01);
                return;

            default:
                Fail(cpu, 0x01);   // funzione non valida
                return;
        }
    }

    private void Reset(CpuNs.Cpu cpu)
    {
        // Non c'e' hardware da reimpostare: si dichiara riuscito.
        _lastStatus = 0;
        Succeed(cpu);
    }

    private void GetStatus(CpuNs.Cpu cpu)
    {
        cpu.SetCarryInStackedFlags(false);
        cpu.State.SetReg8(4, _lastStatus);   // AH = stato, non codice di errore
    }

    private void ReadSectors(CpuNs.Cpu cpu, byte drive)
    {
        DiskImage? image = GetDrive(drive);
        if (image is null) { Fail(cpu, 0x80); return; }   // unita' non pronta

        (int cylinder, int head, int sector, int count) = DecodeChs(cpu);

        // Il buffer di destinazione e' ES:BX nello spazio del chiamante.
        var buffer = new byte[count * DiskImage.SectorSize];
        if (!image.ReadSectors(cylinder, head, sector, count, buffer))
        {
            Fail(cpu, 0x04);   // settore non trovato
            return;
        }

        ushort destination = cpu.State.Bx;
        for (int i = 0; i < buffer.Length; i++)
            cpu.WriteMemoryByte(SegReg.Es, (uint)(destination + i), buffer[i]);

        cpu.State.SetReg8(0, (byte)count);   // AL: settori effettivamente letti
        _lastStatus = 0;
        Succeed(cpu);
    }

    private void WriteSectors(CpuNs.Cpu cpu, byte drive)
    {
        DiskImage? image = GetDrive(drive);
        if (image is null) { Fail(cpu, 0x80); return; }
        if (image.ReadOnly) { Fail(cpu, 0x03); return; }   // protetto da scrittura

        (int cylinder, int head, int sector, int count) = DecodeChs(cpu);

        var buffer = new byte[count * DiskImage.SectorSize];
        ushort source = cpu.State.Bx;
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = cpu.ReadMemoryByte(SegReg.Es, (uint)(source + i));

        if (!image.WriteSectors(cylinder, head, sector, count, buffer))
        {
            Fail(cpu, 0x04);
            return;
        }

        cpu.State.SetReg8(0, (byte)count);
        _lastStatus = 0;
        Succeed(cpu);
    }

    private void VerifySectors(CpuNs.Cpu cpu, byte drive)
    {
        // La verifica confronterebbe il contenuto con un CRC; su un'immagine
        // su file non c'e' nulla che possa essere corrotto, quindi basta
        // controllare che i settori esistano.
        DiskImage? image = GetDrive(drive);
        if (image is null) { Fail(cpu, 0x80); return; }

        (int cylinder, int head, int sector, int count) = DecodeChs(cpu);
        if (!image.IsValidChs(cylinder, head, sector)) { Fail(cpu, 0x04); return; }

        cpu.State.SetReg8(0, (byte)count);
        Succeed(cpu);
    }

    private void GetDriveParameters(CpuNs.Cpu cpu, byte drive)
    {
        DiskImage? image = GetDrive(drive);
        if (image is null)
        {
            // Unita' assente. Non basta segnalare l'errore: i registri devono
            // essere azzerati, perche' DOS legge comunque DL per sapere quante
            // unita' esistono. Lasciandoci il valore di una chiamata
            // precedente, DOS proverebbe a enumerare unita' inesistenti.
            cpu.State.Cx = 0;
            cpu.State.Dx = 0;
            cpu.State.Bx = 0;
            cpu.SetSegment(CpuNs.SegReg.Es, 0);
            cpu.State.Di = 0;
            Fail(cpu, 0x07);
            return;
        }

        // La geometria viene restituita in una codifica compatta: i due bit
        // alti del numero di cilindri finiscono nei bit alti di CL.
        int lastCylinder = image.Cylinders - 1;
        byte cl = (byte)((image.SectorsPerTrack & 0x3F) | ((lastCylinder >> 2) & 0xC0));
        cpu.State.SetReg8(1, cl);                              // CL
        cpu.State.SetReg8(5, (byte)(lastCylinder & 0xFF));     // CH
        // Negli indici dei registri a 8 bit DH e' il 6 e DL il 2: l'ultima
        // testina va nel primo, il numero di unita' nel secondo. Scambiarli
        // fa credere al software che il disco abbia una testina sola, e i
        // file finiscono calcolati nel posto sbagliato.
        cpu.State.SetReg8(6, (byte)(image.Heads - 1));         // DH: ultima testina

        // DL riporta quante unita' dello stesso tipo esistono: i floppy e i
        // dischi fissi si contano separatamente.
        bool wantHardDisks = drive >= 0x80;
        int sameKind = _drives.Keys.Count(d => (d >= 0x80) == wantHardDisks);
        cpu.State.SetReg8(2, (byte)sameKind);                  // DL

        Succeed(cpu);
    }

    private void GetDiskType(CpuNs.Cpu cpu, byte drive)
    {
        DiskImage? image = GetDrive(drive);
        if (image is null)
        {
            // AH=0 significa "unita' non installata". Il carry resta pulito:
            // la funzione ha risposto correttamente, e' l'unita' a non esserci.
            cpu.State.Cx = 0;
            cpu.State.Dx = 0;
            cpu.SetCarryInStackedFlags(false);
            cpu.State.SetReg8(4, 0x00);   // AH = tipo: unita' non installata
            return;
        }

        // Per questa funzione AH non e' un codice di errore ma il tipo di
        // unita', quindi va impostato dopo aver pulito il carry.
        cpu.SetCarryInStackedFlags(false);

        if (image.IsHardDisk)
        {
            long sectors = image.TotalSectors;
            cpu.State.Cx = (ushort)(sectors >> 16);
            cpu.State.Dx = (ushort)sectors;
            cpu.State.SetReg8(4, 0x03);   // disco fisso
        }
        else
        {
            // Floppy con linea di rilevamento del cambio disco. E' la via
            // pulita: DOS interroga la funzione 0x16 quando vuole sapere se
            // il supporto e' stato sostituito, invece di dedurlo dal tempo
            // trascorso fra un accesso e l'altro.
            cpu.State.SetReg8(4, 0x02);
        }
    }

    /// <summary>
    /// Funzione 0x16: dice se il disco e' stato cambiato dall'ultimo accesso.
    /// Va offerta insieme alla 0x15 che dichiara la linea di rilevamento,
    /// altrimenti DOS resta in attesa di una risposta che non arriva mai.
    ///
    /// Le immagini su file non si sfilano dal lettore: la risposta e' sempre
    /// "nessun cambio", tranne la prima volta dopo che l'immagine e' stata
    /// sostituita a macchina accesa.
    /// </summary>
    private void DetectDiskChange(CpuNs.Cpu cpu, byte drive)
    {
        DiskImage? image = GetDrive(drive);
        if (image is null) { Fail(cpu, 0x80); return; }

        if (_mediaChanged.Remove(drive))
        {
            Fail(cpu, 0x06);   // supporto cambiato
            return;
        }

        cpu.State.SetReg8(4, 0x00);
        cpu.SetCarryInStackedFlags(false);
    }

    /// <summary>Unita' il cui supporto e' stato sostituito e non ancora segnalato.</summary>
    private readonly HashSet<byte> _mediaChanged = new();

    /// <summary>Segnala che l'immagine di una unita' e' stata sostituita.</summary>
    public void NotifyMediaChanged(byte driveNumber) => _mediaChanged.Add(driveNumber);

    /// <summary>
    /// Estrae cilindro, testina, settore e conteggio dai registri, sciogliendo
    /// la codifica compatta: i due bit alti del cilindro stanno nei bit 6-7 di CL.
    /// </summary>
    /// <summary>
    /// Pubblica l'esito nella BIOS Data Area. La funzione 0x01 lo restituisce,
    /// ma alcuni programmi leggono direttamente quei byte senza chiamarla:
    /// vanno tenuti allineati a ogni operazione.
    /// </summary>
    private void PublishStatus(byte status)
    {
        _bda.DiskStatus = status;
        _bda.HardDiskStatus = status;
    }

    private static (int Cylinder, int Head, int Sector, int Count) DecodeChs(CpuNs.Cpu cpu)
    {
        byte cl = cpu.State.GetReg8(1);
        byte ch = cpu.State.GetReg8(5);

        int sector = cl & 0x3F;
        int cylinder = ch | ((cl & 0xC0) << 2);
        int head = cpu.State.GetReg8(6);      // DH
        int count = cpu.State.GetReg8(0);     // AL

        return (cylinder, head, sector, count);
    }

    private void Succeed(CpuNs.Cpu cpu)
    {
        PublishStatus(0);
        // AH = 0 significa "nessun errore". Va scritto esplicitamente: al
        // momento della chiamata AH conteneva il numero della funzione, e
        // lasciarcelo verrebbe letto come codice di errore -- 0x02 e'
        // "indirizzo non trovato", che porta DOS a considerare corrotto un
        // file appena letto correttamente.
        cpu.State.SetReg8(4, 0);
        cpu.SetCarryInStackedFlags(false);
    }

    private void Fail(CpuNs.Cpu cpu, byte errorCode)
    {
        _lastStatus = errorCode;
        PublishStatus(errorCode);
        cpu.State.SetReg8(4, errorCode);   // AH
        cpu.SetCarryInStackedFlags(true);
    }
}
