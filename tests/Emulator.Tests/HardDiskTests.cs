using Emulator.Core;
using Emulator.Core.Bios;
using CpuNs = Emulator.Core.Cpu;

namespace Emulator.Tests;

/// <summary>
/// Immagini di disco fisso: struttura del MBR, geometria e visibilita' dal
/// BIOS. Un disco fisso, a differenza di un floppy, comincia con il Master
/// Boot Record e non con un file system.
/// </summary>
public class HardDiskTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(),
        $"emu-test-{Guid.NewGuid():N}.img");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void LaGeometriaRispettaLaDimensioneRichiesta()
    {
        // 40 MB: una dimensione tipica per un disco dell'epoca.
        HardDiskImage.Geometry g = HardDiskImage.Create(_path, 40 * 1024 * 1024);

        Assert.Equal(16, g.Heads);
        Assert.Equal(63, g.SectorsPerTrack);
        Assert.True(g.Cylinders > 0);
        // Il file deve corrispondere alla geometria, non alla richiesta grezza.
        Assert.Equal(g.TotalBytes, new FileInfo(_path).Length);
    }

    [Fact]
    public void LaGeometriaNonSuperaIlLimiteCrs()
    {
        // Oltre 1024 cilindri l'indirizzamento CHS del BIOS non arriva piu':
        // la geometria va limitata invece di produrne una irraggiungibile.
        HardDiskImage.Geometry g = HardDiskImage.Geometry.ForSize(2L * 1024 * 1024 * 1024);
        Assert.True(g.Cylinders <= 1024);
    }

    [Fact]
    public void IlMbrHaLaFirmaEUnaPartizioneAttiva()
    {
        HardDiskImage.Create(_path, 20 * 1024 * 1024);
        byte[] mbr = new byte[512];
        using (var fs = File.OpenRead(_path)) fs.ReadExactly(mbr);

        Assert.Equal(0x55, mbr[0x1FE]);
        Assert.Equal(0xAA, mbr[0x1FF]);

        // Prima voce della tabella: attiva, di tipo FAT16.
        Assert.Equal(0x80, mbr[0x1BE]);
        Assert.Equal(HardDiskImage.PartitionType.Fat16, mbr[0x1BE + 4]);

        // La partizione comincia dopo la prima traccia e ha estensione utile.
        uint start = BitConverter.ToUInt32(mbr, 0x1BE + 8);
        uint count = BitConverter.ToUInt32(mbr, 0x1BE + 12);
        Assert.Equal(63u, start);
        Assert.True(count > 0);
    }

    [Fact]
    public void LeAltreVociDellaTabellaSonoVuote()
    {
        HardDiskImage.Create(_path, 20 * 1024 * 1024);
        byte[] mbr = new byte[512];
        using (var fs = File.OpenRead(_path)) fs.ReadExactly(mbr);

        for (int entry = 1; entry < 4; entry++)
            for (int i = 0; i < 16; i++)
                Assert.Equal(0, mbr[0x1BE + entry * 16 + i]);
    }

    [Fact]
    public void LaPartizioneContieneUnBpbFat16Valido()
    {
        HardDiskImage.Create(_path, 40 * 1024 * 1024);

        byte[] boot = new byte[512];
        using (var fs = File.OpenRead(_path))
        {
            fs.Seek(63 * 512, SeekOrigin.Begin);   // inizio della partizione
            fs.ReadExactly(boot);
        }

        Assert.Equal(0xEB, boot[0]);                                   // salto iniziale
        Assert.Equal(512, BitConverter.ToUInt16(boot, 11));            // byte per settore
        Assert.Equal(2, boot[16]);                                     // due FAT
        Assert.Equal(512, BitConverter.ToUInt16(boot, 17));            // voci di radice
        Assert.Equal(0xF8, boot[21]);                                  // disco fisso
        Assert.Equal(0x55, boot[0x1FE]);
        Assert.Equal(0xAA, boot[0x1FF]);

        // Il numero di cluster deve stare nei limiti della FAT16.
        int sectorsPerCluster = boot[13];
        uint totalSectors = BitConverter.ToUInt32(boot, 32);
        Assert.True(totalSectors / sectorsPerCluster <= 65524,
            "troppi cluster per una FAT16");
    }

    [Fact]
    public void LaConversioneChsEReversibile()
    {
        var g = new HardDiskImage.Geometry(100, 16, 63);

        foreach (long linear in new long[] { 0, 1, 62, 63, 1007, 1008, 50000 })
        {
            (int c, int h, int s) = HardDiskImage.ToChs(g, linear);
            long back = ((long)c * g.Heads + h) * g.SectorsPerTrack + (s - 1);
            Assert.Equal(linear, back);
        }
    }

    [Fact]
    public void IlDiscoCreatoSiApreConLaGeometriaGiusta()
    {
        HardDiskImage.Geometry expected = HardDiskImage.Create(_path, 30 * 1024 * 1024);

        using DiskImage disk = DiskImage.OpenHardDisk(_path);
        Assert.Equal(expected.Cylinders, disk.Cylinders);
        Assert.Equal(expected.Heads, disk.Heads);
        Assert.Equal(expected.SectorsPerTrack, disk.SectorsPerTrack);
        Assert.True(disk.IsHardDisk);
        Assert.Equal(0x80, disk.DriveNumber);
    }

    [Fact]
    public void LettureEScrittureSuDiscoFissoFunzionano()
    {
        using DiskImage disk = DiskImage.CreateHardDisk(_path, 20 * 1024 * 1024);

        byte[] payload = new byte[512];
        "CONTENUTO DEL SETTORE"u8.CopyTo(payload);

        // Un settore in mezzo al disco, per verificare anche la geometria.
        Assert.True(disk.WriteSectors(5, 3, 10, 1, payload));

        byte[] readBack = new byte[512];
        Assert.True(disk.ReadSectors(5, 3, 10, 1, readBack));
        Assert.Equal(payload, readBack);
    }

    [Fact]
    public void IlBiosVedeIlDiscoFisso()
    {
        using var machine = new Machine();
        machine.AttachDisk(DiskImage.CreateHardDisk(_path, 20 * 1024 * 1024));
        machine.RunInstructions(300);

        // La BDA deve dichiarare il disco installato.
        Assert.Equal(1, machine.Bda.HardDiskCount);

        // INT 13h AH=08 deve riportarne la geometria.
        var cpu = machine.Cpu;
        cpu.State.Esp = 0x7000;
        cpu.SetSegment(CpuNs.SegReg.Ss, 0);
        machine.Memory.WriteWord(0x7004, 0);

        cpu.State.SetReg8(4, 0x08);
        cpu.State.SetReg8(2, 0x80);
        machine.DiskServices.Handle(cpu);

        Assert.Equal(0, machine.Memory.ReadWord(0x7004) & 1);   // nessun errore
        // Negli indici dei registri a 8 bit DH e' il 6 e DL il 2: la testina
        // finale sta nel primo, il numero di unita' nel secondo. Il test
        // controllava gli indici scambiati, e passava contro un BIOS che li
        // scambiava a sua volta -- con quella geometria SETUP di MS-DOS
        // scriveva i file nel posto sbagliato e il disco non si avviava.
        Assert.Equal(15, cpu.State.GetReg8(6));                 // DH: 16 testine meno una
        Assert.Equal(1, cpu.State.GetReg8(2));                  // DL: un disco fisso
    }

    [Fact]
    public void ILettureBiosDalDiscoFissoConsegnanoIlMbr()
    {
        // Legge il primo settore via INT 13h e verifica che sia il MBR.
        //   B8 00 00  MOV AX,0 ; 8E C0 MOV ES,AX ; BB 00 60 MOV BX,0x6000
        //   B4 02 MOV AH,2 ; B0 01 MOV AL,1
        //   B5 00 MOV CH,0 ; B1 01 MOV CL,1 ; B6 00 MOV DH,0 ; B2 80 MOV DL,0x80
        //   CD 13 INT 13h ; F4 HLT
        var sector = new byte[512];
        new byte[] {
            0xB8, 0x00, 0x00, 0x8E, 0xC0, 0xBB, 0x00, 0x60,
            0xB4, 0x02, 0xB0, 0x01, 0xB5, 0x00, 0xB1, 0x01,
            0xB6, 0x00, 0xB2, 0x80, 0xCD, 0x13, 0xF4,
        }.CopyTo(sector, 0);
        sector[510] = 0x55; sector[511] = 0xAA;

        using var machine = new Machine();
        DiskImage floppy = DiskImage.CreateInMemory(0x00, 80, 2, 18);
        floppy.WriteSectors(0, 0, 1, 1, sector);
        machine.AttachDisk(floppy);
        machine.AttachDisk(DiskImage.CreateHardDisk(_path, 20 * 1024 * 1024));

        machine.RunInstructions(3000);

        // Il MBR letto deve avere la firma alla fine.
        Assert.Equal(0x55, machine.Memory.ReadByte(0x6000 + 510));
        Assert.Equal(0xAA, machine.Memory.ReadByte(0x6000 + 511));
        Assert.Equal(0x80, machine.Memory.ReadByte(0x6000 + 0x1BE));   // partizione attiva
    }

    [Fact]
    public void UnImmagineInSolaLetturaPuoEssereApertaPiuVolte()
    {
        // Ispezionare la stessa immagine con piu' strumenti insieme e'
        // normale: aprirla in sola lettura non deve impedirlo. Con una
        // condivisione troppo stretta la seconda apertura fallisce con un
        // "file in uso" che sembra un problema dell'immagine.
        HardDiskImage.Create(_path, 20 * 1024 * 1024);

        using DiskImage first = DiskImage.OpenHardDisk(_path, 0x80, readOnly: true);
        using DiskImage second = DiskImage.OpenHardDisk(_path, 0x81, readOnly: true);

        var buffer = new byte[512];
        Assert.True(first.ReadSectors(0, 0, 1, 1, buffer));
        Assert.Equal(0x55, buffer[510]);

        Assert.True(second.ReadSectors(0, 0, 1, 1, buffer));
        Assert.Equal(0xAA, buffer[511]);
    }

    [Fact]
    public void UnImmagineInScritturaRestaLeggibileDaAltri()
    {
        // Chi scrive esclude altri scrittori, ma non i lettori: cosi' si puo'
        // ispezionare un'immagine mentre l'emulatore la sta usando.
        HardDiskImage.Create(_path, 20 * 1024 * 1024);

        using DiskImage writer = DiskImage.OpenHardDisk(_path);
        using DiskImage reader = DiskImage.OpenHardDisk(_path, 0x81, readOnly: true);

        var payload = new byte[512];
        "SCRITTO DAL PRIMO"u8.CopyTo(payload);
        Assert.True(writer.WriteSectors(2, 0, 1, 1, payload));

        var readBack = new byte[512];
        Assert.True(reader.ReadSectors(2, 0, 1, 1, readBack));
        Assert.Equal(payload, readBack);
    }

    [Fact]
    public void IlMbrTrovaLaPartizioneAttivaECaricaIlSuoSettore()
    {
        // Il MBR deve: spostarsi in basso, cercare la voce con il byte 0x80,
        // leggerne il settore di avvio a 0x7C00 e saltarci. Un errore in
        // questa catena non produce alcun messaggio: lo schermo resta nero,
        // che e' il sintomo piu' difficile da diagnosticare.
        HardDiskImage.Create(_path, 20 * 1024 * 1024);

        using var machine = new Machine();
        machine.AttachDisk(DiskImage.OpenHardDisk(_path));

        for (int i = 0; i < 20; i++) machine.RunInstructions(200_000);

        // La tabella delle partizioni deve essere stata ricopiata a 0x7BE.
        Assert.Equal(0x80, machine.Memory.ReadByte(0x7BE));
        Assert.Equal(HardDiskImage.PartitionType.Fat16, machine.Memory.ReadByte(0x7BE + 4));

        // Il settore di avvio della partizione deve essere finito a 0x7C00.
        Assert.Equal(0xEB, machine.Memory.ReadByte(0x7C00));
        string oem = System.Text.Encoding.ASCII.GetString(
            machine.Memory.GetRamSpan(0x7C03, 8).ToArray());
        Assert.Equal("MSDOS5.0", oem);
    }

    [Fact]
    public void UnDiscoVuotoMostraDiscoNonDiSistema()
    {
        // Un disco partizionato ma senza sistema operativo deve dirlo, non
        // restare muto: lo schermo nero non distingue "non avviabile" da
        // "emulatore rotto".
        HardDiskImage.Create(_path, 20 * 1024 * 1024);

        using var machine = new Machine();
        machine.AttachDisk(DiskImage.OpenHardDisk(_path));

        for (int i = 0; i < 20; i++) machine.RunInstructions(200_000);

        string screen = machine.GetScreenText();
        Assert.Contains("Disco non di sistema", screen);
        Assert.True(machine.Cpu.State.Halted, "la macchina deve fermarsi dopo il messaggio");
    }

    [Fact]
    public void UnDiscoFissoAvviabileVieneScelto()
    {
        // Con solo il disco fisso collegato, il BIOS deve avviarsi da quello.
        using var machine = new Machine();
        DiskImage disk = DiskImage.CreateHardDisk(_path, 20 * 1024 * 1024);

        // Sostituisce il MBR con codice riconoscibile.
        var mbr = new byte[512];
        new byte[] { 0xB8, 0x0D, 0xF0, 0xA3, 0x00, 0x60, 0xF4 }.CopyTo(mbr, 0);
        mbr[510] = 0x55; mbr[511] = 0xAA;
        disk.WriteSectors(0, 0, 1, 1, mbr);
        machine.AttachDisk(disk);

        machine.RunInstructions(2000);

        Assert.True(machine.Booted);
        Assert.Equal(0xF00D, machine.Memory.ReadWord(0x6000));
        Assert.Equal(0x80, machine.Cpu.State.GetReg8(2));   // DL: unita' di avvio
    }
}
