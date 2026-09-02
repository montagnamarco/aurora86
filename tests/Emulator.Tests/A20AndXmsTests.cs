using Emulator.Core;
using Emulator.Core.Memory;

namespace Emulator.Tests;

/// <summary>
/// La linea A20 e il wrapping a 1 MB. Il comportamento e' contro-intuitivo:
/// A20 <b>disattivata</b> e' lo stato di compatibilita' in cui gli indirizzi
/// avvolgono come sull'8086, e la HMA risulta irraggiungibile.
/// </summary>
public class A20Tests
{
    [Fact]
    public void ConA20SpentaGliIndirizziAvvolgonoAUnMegabyte()
    {
        using var bus = new PhysicalMemoryBus();
        bus.A20Enabled = false;

        bus.WriteDword(0x000000, 0x12345678);

        // 0x100000 ha il bit 20 acceso: spento A20, punta a 0x000000.
        Assert.Equal(0x12345678u, bus.ReadDword(0x100000));
    }

    [Fact]
    public void ConA20AccesaGliIndirizziSonoDistinti()
    {
        using var bus = new PhysicalMemoryBus();
        bus.A20Enabled = true;

        bus.WriteDword(0x000000, 0x11111111);
        bus.WriteDword(0x100000, 0x22222222);

        Assert.Equal(0x11111111u, bus.ReadDword(0x000000));
        Assert.Equal(0x22222222u, bus.ReadDword(0x100000));
    }

    [Fact]
    public void IlWrappingValeAncheInScrittura()
    {
        // Una scrittura sopra il megabyte con A20 spenta deve finire in basso,
        // altrimenti letture e scritture userebbero indirizzi diversi.
        using var bus = new PhysicalMemoryBus();
        bus.A20Enabled = false;

        bus.WriteWord(0x100000, 0xBEEF);
        Assert.Equal(0xBEEF, bus.ReadWord(0x000000));

        bus.WriteByte(0x100010, 0x42);
        Assert.Equal(0x42, bus.ReadByte(0x000010));

        bus.WriteDword(0x100020, 0xCAFEBABE);
        Assert.Equal(0xCAFEBABEu, bus.ReadDword(0x000020));
    }

    [Fact]
    public void SoloIlBitVentiVieneEscluso()
    {
        // Il wrapping azzera il bit 20, non tronca a 20 bit: la memoria oltre
        // i 2 MB deve restare raggiungibile anche con A20 spenta.
        using var bus = new PhysicalMemoryBus();
        bus.A20Enabled = true;
        bus.WriteDword(0x200000, 0xAAAAAAAA);   // 2 MB, bit 21

        bus.A20Enabled = false;
        Assert.Equal(0xAAAAAAAAu, bus.ReadDword(0x200000));
    }

    [Fact]
    public void LaProvaDiWrappingUsataDalSoftwareDaLEsitoAtteso()
    {
        // E' il modo canonico in cui un programma verifica lo stato di A20:
        // scrive in basso e rilegge un megabyte piu' su.
        using var bus = new PhysicalMemoryBus();

        bus.A20Enabled = false;
        bus.WriteDword(0x000000, 0xDEADBEEF);
        bus.WriteDword(0x100000, 0x00000000);
        Assert.Equal(0u, bus.ReadDword(0x000000));      // sono lo stesso indirizzo

        bus.A20Enabled = true;
        bus.WriteDword(0x000000, 0xDEADBEEF);
        bus.WriteDword(0x100000, 0x00000000);
        Assert.Equal(0xDEADBEEFu, bus.ReadDword(0x000000));   // indirizzi distinti
    }

    [Fact]
    public void AccessoACavalloDelMegabyteConA20Spenta()
    {
        // Caso limite: un accesso a 32 bit che comincia poco sotto il confine.
        // Ogni byte va mascherato coerentemente.
        using var bus = new PhysicalMemoryBus();
        bus.A20Enabled = true;
        bus.WriteDword(0x0FFFFE, 0x11223344);
        bus.A20Enabled = false;

        // Con A20 spenta l'indirizzo resta sotto il megabyte e non cambia.
        Assert.Equal(0x11223344u, bus.ReadDword(0x0FFFFE));
    }
}

public class A20HardwareTests
{
    [Fact]
    public void LaPortaNoveDueControllaA20()
    {
        using var machine = new Machine();
        machine.RunInstructions(300);

        // Bit 1 azzerato: A20 spenta.
        machine.Io.WriteByte(0x92, 0x00);
        Assert.False(machine.Memory.A20Enabled);

        machine.Io.WriteByte(0x92, 0x02);
        Assert.True(machine.Memory.A20Enabled);
    }

    [Fact]
    public void LaPortaNoveDueRiportaLoStatoEffettivo()
    {
        using var machine = new Machine();
        machine.RunInstructions(300);

        // Anche se la linea e' stata cambiata altrove, la lettura deve dire
        // la verita': c'e' una sola linea A20, non una copia per controller.
        machine.Memory.A20Enabled = false;
        Assert.Equal(0x00, machine.Io.ReadByte(0x92) & 0x02);

        machine.Memory.A20Enabled = true;
        Assert.Equal(0x02, machine.Io.ReadByte(0x92) & 0x02);
    }

    [Fact]
    public void IlControllerDiTastieraControllaA20()
    {
        using var machine = new Machine();
        machine.RunInstructions(300);

        // Comando 0xD1 (scrivi porta di uscita) seguito dal dato.
        machine.Io.WriteByte(0x64, 0xD1);
        machine.Io.WriteByte(0x60, 0x00);      // bit 1 spento
        Assert.False(machine.Memory.A20Enabled);

        machine.Io.WriteByte(0x64, 0xD1);
        machine.Io.WriteByte(0x60, 0x02);
        Assert.True(machine.Memory.A20Enabled);
    }

    [Fact]
    public void LeDueVieDiControlloVedonoLaStessaLinea()
    {
        // Il controller di tastiera e la porta 0x92 comandano lo stesso filo:
        // tenere due stati separati li farebbe divergere.
        using var machine = new Machine();
        machine.RunInstructions(300);

        machine.Io.WriteByte(0x92, 0x00);                  // spenta dalla porta veloce
        Assert.False(machine.Keyboard.A20Enabled);         // il controller lo vede

        machine.Io.WriteByte(0x64, 0xD1);
        machine.Io.WriteByte(0x60, 0x02);                  // accesa dal controller
        Assert.Equal(0x02, machine.Io.ReadByte(0x92) & 0x02);
    }

    [Fact]
    public void Int15hFunzione24hGestisceA20()
    {
        //   B8 01 24   MOV AX,0x2401  (abilita A20)
        //   CD 15      INT 15h
        //   F4         HLT
        var sector = new byte[512];
        new byte[] { 0xB8, 0x01, 0x24, 0xCD, 0x15, 0xF4 }.CopyTo(sector, 0);
        sector[510] = 0x55; sector[511] = 0xAA;

        using var machine = new Machine();
        var disk = Emulator.Core.Bios.DiskImage.CreateInMemory(0x00, 80, 2, 18);
        disk.WriteSectors(0, 0, 1, 1, sector);
        machine.AttachDisk(disk);

        machine.Memory.A20Enabled = false;
        machine.RunInstructions(2000);

        Assert.True(machine.Memory.A20Enabled);
        Assert.Equal(0, machine.Cpu.State.GetReg8(4));   // AH = 0: riuscito
    }
}

/// <summary>
/// Il gestore XMS, cioe' il servizio che su un PC reale fornisce HIMEM.SYS.
/// </summary>
public class XmsTests
{
    private static Machine CreateBootedMachine()
    {
        var machine = new Machine();
        machine.RunInstructions(300);
        return machine;
    }

    private static void CallXms(Machine machine, byte function, Action<Emulator.Core.Cpu.Cpu>? setup = null)
    {
        machine.Cpu.State.SetReg8(4, function);
        setup?.Invoke(machine.Cpu);
        machine.Xms.Handle(machine.Cpu);
    }

    [Fact]
    public void LaVersioneRiportaLaPresenzaDellaHma()
    {
        using Machine machine = CreateBootedMachine();
        CallXms(machine, 0x00);

        Assert.Equal(0x0300, machine.Cpu.State.Ax);   // XMS 3.0
        Assert.Equal(1, machine.Cpu.State.Dx);        // la HMA esiste
    }

    [Fact]
    public void LaRichiestaDellaHmaRiesceEAccendeA20()
    {
        using Machine machine = CreateBootedMachine();
        machine.Memory.A20Enabled = false;

        // DX=0xFFFF e' cio' che passa DOS con DOS=HIGH: "prendo tutta la HMA".
        CallXms(machine, 0x01, c => c.State.Dx = 0xFFFF);

        Assert.Equal(1, machine.Cpu.State.Ax);
        Assert.True(machine.Xms.HmaAllocated);
        Assert.True(machine.Memory.A20Enabled);
    }

    [Fact]
    public void LaHmaNonPuoEssereAssegnataDueVolte()
    {
        using Machine machine = CreateBootedMachine();

        CallXms(machine, 0x01, c => c.State.Dx = 0xFFFF);
        CallXms(machine, 0x01, c => c.State.Dx = 0xFFFF);

        Assert.Equal(0, machine.Cpu.State.Ax);
        Assert.Equal(0x91, machine.Cpu.State.GetReg8(3));   // gia' assegnata
    }

    [Fact]
    public void SpegnereA20ConLaHmaInUsoVieneRifiutato()
    {
        // Spegnerla renderebbe irraggiungibile il codice che ci sta girando:
        // e' una protezione, non una limitazione.
        using Machine machine = CreateBootedMachine();
        CallXms(machine, 0x01, c => c.State.Dx = 0xFFFF);

        CallXms(machine, 0x04);   // disabilitazione globale di A20

        Assert.Equal(0, machine.Cpu.State.Ax);
        Assert.Equal(0x94, machine.Cpu.State.GetReg8(3));
        Assert.True(machine.Memory.A20Enabled);
    }

    [Fact]
    public void UnaRichiestaTroppoGrandeVieneRifiutata()
    {
        using Machine machine = CreateBootedMachine();
        CallXms(machine, 0x01, c => c.State.Dx = 0xFFF1);   // oltre la HMA

        Assert.Equal(0, machine.Cpu.State.Ax);
        Assert.Equal(0xA1, machine.Cpu.State.GetReg8(3));
    }

    [Fact]
    public void LaMemoriaEstesaDisponibileRispecchiaLaRam()
    {
        using Machine machine = CreateBootedMachine();
        CallXms(machine, 0x08);

        // 16 MB meno il primo megabyte e meno la HMA.
        Assert.True(machine.Cpu.State.Dx > 15000);
        Assert.True(machine.Cpu.State.Dx < 16000);
    }

    [Fact]
    public void AllocazioneELiberazioneDiUnBlocco()
    {
        using Machine machine = CreateBootedMachine();

        CallXms(machine, 0x08);
        ushort freeBefore = machine.Cpu.State.Dx;

        CallXms(machine, 0x09, c => c.State.Dx = 64);
        Assert.Equal(1, machine.Cpu.State.Ax);
        ushort handle = machine.Cpu.State.Dx;
        Assert.NotEqual(0, handle);

        CallXms(machine, 0x0E, c => c.State.Dx = handle);
        Assert.Equal(64, machine.Cpu.State.Dx);      // dimensione richiesta

        CallXms(machine, 0x0A, c => c.State.Dx = handle);
        Assert.Equal(1, machine.Cpu.State.Ax);
    }

    [Fact]
    public void UnHandleNonValidoVieneRifiutato()
    {
        using Machine machine = CreateBootedMachine();
        CallXms(machine, 0x0A, c => c.State.Dx = 999);

        Assert.Equal(0, machine.Cpu.State.Ax);
        Assert.Equal(0xA2, machine.Cpu.State.GetReg8(3));
    }

    [Fact]
    public void UnBloccoBloccatoNonPuoEssereLiberato()
    {
        using Machine machine = CreateBootedMachine();

        CallXms(machine, 0x09, c => c.State.Dx = 32);
        ushort handle = machine.Cpu.State.Dx;

        CallXms(machine, 0x0C, c => c.State.Dx = handle);   // blocca
        Assert.Equal(1, machine.Cpu.State.Ax);

        CallXms(machine, 0x0A, c => c.State.Dx = handle);   // prova a liberare
        Assert.Equal(0, machine.Cpu.State.Ax);
        Assert.Equal(0xAB, machine.Cpu.State.GetReg8(3));

        CallXms(machine, 0x0D, c => c.State.Dx = handle);   // sblocca
        CallXms(machine, 0x0A, c => c.State.Dx = handle);
        Assert.Equal(1, machine.Cpu.State.Ax);
    }

    [Fact]
    public void CopiaDallaMemoriaConvenzionaleAQuellaEstesa()
    {
        using Machine machine = CreateBootedMachine();

        CallXms(machine, 0x09, c => c.State.Dx = 64);
        ushort handle = machine.Cpu.State.Dx;

        const uint source = 0x2000;
        const uint parameters = 0x3000;
        byte[] payload = "DATI-VERSO-LA-MEMORIA-ESTESA"u8.ToArray();
        payload.CopyTo(machine.Memory.GetRamSpan(source, payload.Length));

        machine.Cpu.SetSegment(Emulator.Core.Cpu.SegReg.Ds, 0);
        machine.Memory.WriteDword(parameters, (uint)payload.Length);
        machine.Memory.WriteWord(parameters + 4, 0);            // origine: convenzionale
        machine.Memory.WriteDword(parameters + 6, source);
        machine.Memory.WriteWord(parameters + 10, handle);      // destinazione: il blocco
        machine.Memory.WriteDword(parameters + 12, 0);

        CallXms(machine, 0x0B, c => c.State.Si = (ushort)parameters);
        Assert.Equal(1, machine.Cpu.State.Ax);

        // Rilegge dall'indirizzo fisico del blocco.
        CallXms(machine, 0x0C, c => c.State.Dx = handle);
        uint physical = ((uint)machine.Cpu.State.Dx << 16) | machine.Cpu.State.Bx;

        machine.Memory.A20Enabled = true;
        string readBack = System.Text.Encoding.ASCII.GetString(
            machine.Memory.GetRamSpan(physical, payload.Length).ToArray());
        Assert.Equal("DATI-VERSO-LA-MEMORIA-ESTESA", readBack);
    }

    [Fact]
    public void UnaLunghezzaDispariVieneRifiutata()
    {
        // La specifica XMS richiede lunghezze pari.
        using Machine machine = CreateBootedMachine();
        CallXms(machine, 0x09, c => c.State.Dx = 16);
        ushort handle = machine.Cpu.State.Dx;

        const uint parameters = 0x3000;
        machine.Cpu.SetSegment(Emulator.Core.Cpu.SegReg.Ds, 0);
        machine.Memory.WriteDword(parameters, 7);               // dispari
        machine.Memory.WriteWord(parameters + 4, 0);
        machine.Memory.WriteDword(parameters + 6, 0x2000);
        machine.Memory.WriteWord(parameters + 10, handle);
        machine.Memory.WriteDword(parameters + 12, 0);

        CallXms(machine, 0x0B, c => c.State.Si = (ushort)parameters);
        Assert.Equal(0, machine.Cpu.State.Ax);
        Assert.Equal(0xA7, machine.Cpu.State.GetReg8(3));
    }
}

public class MultiplexTests
{
    [Fact]
    public void Int2FhAnnunciaIlGestoreXms()
    {
        //   B8 00 43   MOV AX,0x4300
        //   CD 2F      INT 2Fh
        //   A2 00 60   MOV [0x6000],AL
        //   F4         HLT
        var sector = new byte[512];
        new byte[] { 0xB8, 0x00, 0x43, 0xCD, 0x2F, 0xA2, 0x00, 0x60, 0xF4 }.CopyTo(sector, 0);
        sector[510] = 0x55; sector[511] = 0xAA;

        using var machine = new Machine();
        var disk = Emulator.Core.Bios.DiskImage.CreateInMemory(0x00, 80, 2, 18);
        disk.WriteSectors(0, 0, 1, 1, sector);
        machine.AttachDisk(disk);
        machine.RunInstructions(2000);

        // 0x80 e' la risposta con cui un gestore XMS dichiara di esserci.
        Assert.Equal(0x80, machine.Memory.ReadByte(0x6000));
    }

    [Fact]
    public void Int2FhFornisceIlPuntoDiIngresso()
    {
        //   B8 10 43   MOV AX,0x4310
        //   CD 2F      INT 2Fh
        //   89 1E 00 60  MOV [0x6000],BX
        //   8C 06 02 60  MOV [0x6002],ES
        //   F4         HLT
        var sector = new byte[512];
        new byte[] {
            0xB8, 0x10, 0x43, 0xCD, 0x2F,
            0x89, 0x1E, 0x00, 0x60,
            0x8C, 0x06, 0x02, 0x60,
            0xF4,
        }.CopyTo(sector, 0);
        sector[510] = 0x55; sector[511] = 0xAA;

        using var machine = new Machine();
        var disk = Emulator.Core.Bios.DiskImage.CreateInMemory(0x00, 80, 2, 18);
        disk.WriteSectors(0, 0, 1, 1, sector);
        machine.AttachDisk(disk);
        machine.RunInstructions(2000);

        Assert.Equal(0xF000, machine.Memory.ReadWord(0x6002));   // segmento del BIOS
        Assert.NotEqual(0, machine.Memory.ReadWord(0x6000));     // offset valido
    }
}
