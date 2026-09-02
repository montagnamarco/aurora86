using Emulator.Core;
using Emulator.Core.Bios;

namespace Emulator.Tests;

/// <summary>
/// Prove sulla macchina completa: accensione, POST, caricamento del settore di
/// avvio ed esecuzione di codice che usa le funzioni BIOS.
/// </summary>
public class MachineTests
{
    /// <summary>
    /// Costruisce un settore di avvio con il codice indicato, completo della
    /// firma 0xAA55 senza la quale il BIOS non lo considera avviabile.
    /// </summary>
    private static byte[] BuildBootSector(params byte[] code)
    {
        var sector = new byte[512];
        code.CopyTo(sector, 0);
        sector[510] = 0x55;
        sector[511] = 0xAA;
        return sector;
    }

    private static DiskImage CreateBootableFloppy(byte[] bootSector)
    {
        DiskImage image = DiskImage.CreateInMemory(0x00, 80, 2, 18);   // 1.44 MB
        image.WriteSectors(0, 0, 1, 1, bootSector);
        return image;
    }

    [Fact]
    public void AccensioneEseguePostEPreparaLaBda()
    {
        using var machine = new Machine();

        // Il processore parte dal vettore di reset e raggiunge il POST.
        machine.RunInstructions(50);

        Assert.Equal(640, machine.Bda.MemorySizeKb);
        Assert.Equal(0x03, machine.Bda.VideoMode);
        Assert.Equal(80, machine.Bda.VideoColumns);
        Assert.Equal(24, machine.Bda.VideoRowsMinusOne);
        Assert.Equal(0x3D4, machine.Bda.CrtcPort);
    }

    [Fact]
    public void PostInstallaITabellaDeiVettori()
    {
        using var machine = new Machine();
        machine.RunInstructions(50);

        // Ogni vettore deve puntare nel segmento del BIOS.
        foreach (byte vector in new byte[] { 0x08, 0x09, 0x10, 0x13, 0x16, 0x1A })
        {
            ushort segment = machine.Memory.ReadWord((uint)vector * 4 + 2);
            Assert.Equal(0xF000, segment);
        }
    }

    [Fact]
    public void SenzaDiscoAvviabileLaMacchinaSiFerma()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);

        Assert.False(machine.Booted);
        Assert.True(machine.Cpu.State.Halted);
    }

    [Fact]
    public void SettoreSenzaFirmaNonVieneAvviato()
    {
        using var machine = new Machine();

        // Settore valido come contenuto ma senza la firma finale.
        var sector = new byte[512];
        sector[0] = 0xF4;   // HLT
        DiskImage image = DiskImage.CreateInMemory(0x00, 80, 2, 18);
        image.WriteSectors(0, 0, 1, 1, sector);
        machine.AttachDisk(image);

        machine.RunInstructions(200);
        Assert.False(machine.Booted);
    }

    [Fact]
    public void SettoreDiAvvioVieneCaricatoEdEseguito()
    {
        // Il settore scrive un valore riconoscibile in memoria e si ferma.
        //   B8 34 12    MOV AX,0x1234
        //   A3 00 60    MOV [0x6000],AX
        //   F4          HLT
        byte[] boot = BuildBootSector(0xB8, 0x34, 0x12, 0xA3, 0x00, 0x60, 0xF4);

        using var machine = new Machine();
        machine.AttachDisk(CreateBootableFloppy(boot));
        machine.RunInstructions(500);

        Assert.True(machine.Booted);
        Assert.Equal(0x1234, machine.Memory.ReadWord(0x6000));
    }

    [Fact]
    public void SettoreDiAvvioRiceveLUnitaInDl()
    {
        // Il codice del settore salva DL, che il BIOS deve aver impostato con
        // il numero dell'unita' da cui e' stato caricato.
        //   88 16 00 60   MOV [0x6000],DL
        //   F4            HLT
        byte[] boot = BuildBootSector(0x88, 0x16, 0x00, 0x60, 0xF4);

        using var machine = new Machine();
        machine.AttachDisk(CreateBootableFloppy(boot));
        machine.RunInstructions(500);

        Assert.Equal(0x00, machine.Memory.ReadByte(0x6000));   // primo floppy
    }

    [Fact]
    public void ScritturaSulloSchermoConInt10h()
    {
        // Scrive "OK" con la funzione telescrivente, che e' quella che DOS usa
        // per quasi tutto l'output.
        //   B4 0E       MOV AH,0x0E
        //   B0 4F       MOV AL,'O'
        //   CD 10       INT 10h
        //   B0 4B       MOV AL,'K'
        //   CD 10       INT 10h
        //   F4          HLT
        byte[] boot = BuildBootSector(
            0xB4, 0x0E, 0xB0, 0x4F, 0xCD, 0x10,
            0xB0, 0x4B, 0xCD, 0x10, 0xF4);

        using var machine = new Machine();
        machine.AttachDisk(CreateBootableFloppy(boot));
        machine.RunInstructions(1000);

        string screen = machine.GetScreenText();
        Assert.StartsWith("OK", screen);
    }

    [Fact]
    public void ScritturaDiUnaRigaConRitornoACapo()
    {
        // "AB" seguito da CR LF, poi "C": la C deve finire sulla riga sotto.
        var code = new List<byte> { 0xB4, 0x0E };   // MOV AH,0x0E
        foreach (char ch in "AB\r\nC")
        {
            code.Add(0xB0); code.Add((byte)ch);     // MOV AL,ch
            code.Add(0xCD); code.Add(0x10);         // INT 10h
        }
        code.Add(0xF4);

        using var machine = new Machine();
        machine.AttachDisk(CreateBootableFloppy(BuildBootSector([.. code])));
        machine.RunInstructions(2000);

        string[] lines = machine.GetScreenText().Split(Environment.NewLine);
        Assert.StartsWith("AB", lines[0]);
        Assert.StartsWith("C", lines[1]);
    }

    [Fact]
    public void LetturaDiSettoriConInt13h()
    {
        // Legge il secondo settore del disco a 0x6000 e si ferma.
        //   B8 00 00    MOV AX,0
        //   8E C0       MOV ES,AX
        //   BB 00 60    MOV BX,0x6000
        //   B4 02       MOV AH,2       (leggi settori)
        //   B0 01       MOV AL,1       (un settore)
        //   B5 00       MOV CH,0       (cilindro 0)
        //   B1 02       MOV CL,2       (settore 2, si numera da 1)
        //   B6 00       MOV DH,0       (testina 0)
        //   B2 00       MOV DL,0       (primo floppy)
        //   CD 13       INT 13h
        //   F4          HLT
        byte[] boot = BuildBootSector(
            0xB8, 0x00, 0x00, 0x8E, 0xC0, 0xBB, 0x00, 0x60,
            0xB4, 0x02, 0xB0, 0x01, 0xB5, 0x00, 0xB1, 0x02,
            0xB6, 0x00, 0xB2, 0x00, 0xCD, 0x13, 0xF4);

        using var machine = new Machine();
        DiskImage image = CreateBootableFloppy(boot);

        // Contenuto riconoscibile nel secondo settore.
        var payload = new byte[512];
        "SETTORE-DUE"u8.CopyTo(payload);
        image.WriteSectors(0, 0, 2, 1, payload);
        machine.AttachDisk(image);

        machine.RunInstructions(2000);

        string loaded = System.Text.Encoding.ASCII.GetString(
            machine.Memory.GetRamSpan(0x6000, 11).ToArray());
        Assert.Equal("SETTORE-DUE", loaded);
    }

    [Fact]
    public void InterruzioneDelTimerIncrementaIlContatore()
    {
        // Il settore abilita le interruzioni e attende.
        //   FB          STI
        //   F4          HLT
        //   EB FD       JMP -3  (torna al HLT)
        byte[] boot = BuildBootSector(0xFB, 0xF4, 0xEB, 0xFD);

        using var machine = new Machine();
        machine.AttachDisk(CreateBootableFloppy(boot));

        machine.RunInstructions(500);          // arriva all'attesa
        uint before = machine.Bda.TimerTicks;

        // Tempo sufficiente perche' il timer scatti piu' volte.
        machine.RunFor(TimeSpan.FromMilliseconds(500));

        Assert.True(machine.Bda.TimerTicks > before,
            $"il contatore non e' avanzato: {before} -> {machine.Bda.TimerTicks}");
    }

    [Fact]
    public void PressioneDiUnTastoArrivaAInt16h()
    {
        // Attende un tasto e ne salva il codice ASCII.
        //   B4 00       MOV AH,0
        //   CD 16       INT 16h
        //   A2 00 60    MOV [0x6000],AL
        //   F4          HLT
        byte[] boot = BuildBootSector(0xFB, 0xB4, 0x00, 0xCD, 0x16, 0xA2, 0x00, 0x60, 0xF4);

        using var machine = new Machine();
        machine.AttachDisk(CreateBootableFloppy(boot));

        machine.RunInstructions(500);       // raggiunge l'attesa del tasto

        machine.PressKey(0x1E);             // scancode della A
        machine.RunInstructions(3000);

        Assert.Equal((byte)'a', machine.Memory.ReadByte(0x6000));
    }

    [Fact]
    public void MemoriaEstesaRiportataDaInt15h()
    {
        //   B4 88       MOV AH,0x88
        //   CD 15       INT 15h
        //   A3 00 60    MOV [0x6000],AX
        //   F4          HLT
        byte[] boot = BuildBootSector(0xB4, 0x88, 0xCD, 0x15, 0xA3, 0x00, 0x60, 0xF4);

        using var machine = new Machine(16 * 1024 * 1024);
        machine.AttachDisk(CreateBootableFloppy(boot));
        machine.RunInstructions(1000);

        // 16 MB meno il primo megabyte = 15360 KB.
        Assert.Equal(15360, machine.Memory.ReadWord(0x6000));
    }

    [Fact]
    public void DimensioneMemoriaConvenzionaleDaInt12h()
    {
        byte[] boot = BuildBootSector(0xCD, 0x12, 0xA3, 0x00, 0x60, 0xF4);

        using var machine = new Machine();
        machine.AttachDisk(CreateBootableFloppy(boot));
        machine.RunInstructions(1000);

        Assert.Equal(640, machine.Memory.ReadWord(0x6000));
    }
}
