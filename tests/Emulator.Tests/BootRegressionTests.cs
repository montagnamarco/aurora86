using Emulator.Core;
using Emulator.Core.Bios;
using Emulator.Core.Memory;
using CpuNs = Emulator.Core.Cpu;

namespace Emulator.Tests;

/// <summary>
/// Regressioni emerse facendo avviare MS-DOS 6.22 da un'immagine reale.
/// Ognuno di questi difetti si manifestava come "il sistema non parte", molto
/// lontano dalla causa: senza un test dedicato tornerebbero inosservati.
/// </summary>
public class BootRegressionTests
{
    private const uint CodeBase = 0x1000;

    private static (CpuNs.Cpu cpu, PhysicalMemoryBus bus) Setup(params byte[] code)
    {
        var bus = new PhysicalMemoryBus();
        var cpu = new CpuNs.Cpu(bus);
        foreach (CpuNs.SegReg s in Enum.GetValues<CpuNs.SegReg>())
            cpu.State.LoadSegmentRealMode(s, 0);
        cpu.State.Esp = 0x8000;
        cpu.State.Eip = CodeBase;
        code.CopyTo(bus.GetRamSpan(CodeBase, code.Length));
        return (cpu, bus);
    }

    [Fact]
    public void CallFarDirettaImpilaCsEIpESalta()
    {
        // 0x9A: senza questa istruzione DOS non riesce a fare una sola
        // chiamata fra segmenti, e ogni tentativo finiva come opcode non
        // valido -- cioe' come una INT, con l'esecuzione dirottata altrove.
        //   9A 00 20 00 30   CALL FAR 3000:2000
        var (cpu, bus) = Setup(0x9A, 0x00, 0x20, 0x00, 0x30);
        using (bus)
        {
            cpu.Step();

            Assert.Equal(0x3000, cpu.State.Cs.Selector);
            Assert.Equal(0x2000u, cpu.State.Eip);

            // Sullo stack: prima il segmento di ritorno, poi l'offset.
            Assert.Equal(0x7FFCu, cpu.State.Esp);
            Assert.Equal(CodeBase + 5, bus.ReadWord(0x7FFC));   // IP di ritorno
            Assert.Equal(0x0000, bus.ReadWord(0x7FFE));         // CS di ritorno
        }
    }

    [Fact]
    public void CallFarERetfSonoSimmetriche()
    {
        // La prova che conta: andata e ritorno devono riportare esattamente
        // dove si era.
        //   9A 00 20 00 00   CALL FAR 0000:2000
        //   90               NOP  (punto di ritorno)
        var (cpu, bus) = Setup(0x9A, 0x00, 0x20, 0x00, 0x00);
        using (bus)
        {
            bus.WriteByte(0x2000, 0xCB);      // RETF nella routine chiamata

            cpu.Step();                        // CALL FAR
            Assert.Equal(0x2000u, cpu.State.Eip);

            cpu.Step();                        // RETF
            Assert.Equal(CodeBase + 5, cpu.State.Eip);
            Assert.Equal(0x0000, cpu.State.Cs.Selector);
            Assert.Equal(0x8000u, cpu.State.Esp);   // stack bilanciato
        }
    }

    [Fact]
    public void XlatTraduceTramiteTabella()
    {
        // 0xD7: DOS la chiama milioni di volte per convertire caratteri.
        //   D7   XLAT  ->  AL = DS:[BX + AL]
        var (cpu, bus) = Setup(0xD7);
        using (bus)
        {
            // Tabella a 0x3000: converte l'indice nel suo doppio.
            for (uint i = 0; i < 256; i++) bus.WriteByte(0x3000 + i, (byte)(i * 2));

            cpu.State.Bx = 0x3000;
            cpu.State.SetReg8(0, 5);      // AL = indice
            cpu.Step();

            Assert.Equal(10, cpu.State.GetReg8(0));
        }
    }

    [Fact]
    public void XlatRispettaLOverrideDiSegmento()
    {
        //   2E D7   XLAT con prefisso CS
        var (cpu, bus) = Setup(0x2E, 0xD7);
        using (bus)
        {
            cpu.State.LoadSegmentRealMode(CpuNs.SegReg.Cs, 0);
            cpu.State.LoadSegmentRealMode(CpuNs.SegReg.Ds, 0x1000);   // DS diverso
            bus.WriteByte(0x4000, 0xAA);      // via CS
            bus.WriteByte(0x14000, 0xBB);     // via DS

            cpu.State.Bx = 0x4000;
            cpu.State.SetReg8(0, 0);
            cpu.Step();

            Assert.Equal(0xAA, cpu.State.GetReg8(0));   // ha usato CS
        }
    }

    [Fact]
    public void NessunVettoreDiInterruzioneRestaAZero()
    {
        // DOS legge parole dalla tabella dei vettori e le usa come segmenti,
        // anche per vettori che non gestisce. Uno zero non significa "libero":
        // significa un puntatore a 0000:0000, dove non c'e' codice.
        using var machine = new Machine();
        machine.RunInstructions(300);

        for (int vector = 0; vector < 256; vector++)
        {
            ushort offset = machine.Memory.ReadWord((uint)vector * 4);
            ushort segment = machine.Memory.ReadWord((uint)vector * 4 + 2);

            Assert.True(segment != 0 || offset != 0,
                $"il vettore {vector:X2}h e' nullo: DOS lo userebbe come puntatore");
        }
    }

    [Fact]
    public void OgniVettorePuntaACodiceEseguibile()
    {
        // Non basta che sia diverso da zero: deve portare a codice vero.
        using var machine = new Machine();
        machine.RunInstructions(300);

        for (int vector = 0; vector < 256; vector++)
        {
            ushort offset = machine.Memory.ReadWord((uint)vector * 4);
            ushort segment = machine.Memory.ReadWord((uint)vector * 4 + 2);
            uint linear = (uint)segment * 16 + offset;

            byte first = machine.Memory.ReadByte(linear);
            // Ogni punto di ingresso comincia con l'aggancio nativo (0F FF)
            // oppure e' un IRET immediato.
            Assert.True(first == 0x0F || first == 0xCF,
                $"il vettore {vector:X2}h punta a {segment:X4}:{offset:X4}, " +
                $"dove il primo byte e' {first:X2} e non e' codice di gestore");
        }
    }

    [Fact]
    public void Int13hAzzeraAhSuOperazioneRiuscita()
    {
        // AH e' il codice di errore al ritorno: lasciarci il numero di
        // funzione fa credere a DOS che la lettura sia fallita. Con AH=02
        // ("indirizzo non trovato") DOS considera corrotto un file appena
        // letto correttamente.
        //   B4 02  MOV AH,2      (leggi settori)
        //   B0 01  MOV AL,1
        //   B5 00  MOV CH,0
        //   B1 01  MOV CL,1
        //   B6 00  MOV DH,0
        //   B2 00  MOV DL,0
        //   BB 00 60  MOV BX,0x6000
        //   CD 13  INT 13h
        //   F4     HLT
        var sector = new byte[512];
        new byte[] {
            0xB4, 0x02, 0xB0, 0x01, 0xB5, 0x00, 0xB1, 0x01,
            0xB6, 0x00, 0xB2, 0x00, 0xBB, 0x00, 0x60, 0xCD, 0x13, 0xF4,
        }.CopyTo(sector, 0);
        sector[510] = 0x55; sector[511] = 0xAA;

        using var machine = new Machine();
        DiskImage disk = DiskImage.CreateInMemory(0x00, 80, 2, 18);
        disk.WriteSectors(0, 0, 1, 1, sector);
        machine.AttachDisk(disk);
        machine.RunInstructions(3000);

        Assert.Equal(0, machine.Cpu.State.GetReg8(4));   // AH = 0: nessun errore
        Assert.Equal(1, machine.Cpu.State.GetReg8(0));   // AL = settori letti
    }

    [Fact]
    public void Int13hSuUnitaAssenteAzzeraIRegistri()
    {
        // Un'unita' che non c'e' non deve lasciare in DL il conteggio di una
        // chiamata precedente: DOS lo legge per sapere quante unita' esistono
        // e proverebbe a enumerarne di inesistenti.
        using var machine = new Machine();
        machine.RunInstructions(300);
        var cpu = machine.Cpu;

        // Prepara lo stack come dopo una INT.
        cpu.State.Esp = 0x7000;
        cpu.SetSegment(CpuNs.SegReg.Ss, 0);
        machine.Memory.WriteWord(0x7004, 0);

        // Prima interroga un'unita' esistente, poi una assente.
        DiskImage floppy = DiskImage.CreateInMemory(0x00, 80, 2, 18);
        machine.AttachDisk(floppy);

        cpu.State.SetReg8(4, 0x08);
        cpu.State.SetReg8(2, 0x00);
        machine.DiskServices.Handle(cpu);
        Assert.NotEqual(0, cpu.State.Dx);      // l'unita' esiste

        cpu.State.SetReg8(4, 0x08);
        cpu.State.SetReg8(2, 0x80);            // disco fisso assente
        machine.DiskServices.Handle(cpu);

        Assert.Equal(0, cpu.State.Dx);         // niente residui
        Assert.Equal(0, cpu.State.Cx);
        ushort flags = machine.Memory.ReadWord(0x7004);
        Assert.Equal(1, flags & 1);            // carry: errore segnalato
    }

    [Fact]
    public void Int13hAh15RiportaIlTipoInAhSenzaCarry()
    {
        // Per questa funzione AH non e' un codice di errore ma il tipo di
        // unita': azzerarlo come si fa per le altre la renderebbe muta.
        using var machine = new Machine();
        machine.RunInstructions(300);
        var cpu = machine.Cpu;

        cpu.State.Esp = 0x7000;
        cpu.SetSegment(CpuNs.SegReg.Ss, 0);
        machine.Memory.WriteWord(0x7004, 0);

        machine.AttachDisk(DiskImage.CreateInMemory(0x00, 80, 2, 18));

        cpu.State.SetReg8(4, 0x15);
        cpu.State.SetReg8(2, 0x00);
        machine.DiskServices.Handle(cpu);

        // AH=02 significa "floppy con linea di rilevamento del cambio disco".
        // Il valore 01 sarebbe un floppy senza quella linea, e allora DOS
        // dedurrebbe il cambio dal tempo trascorso invece di interrogare la
        // funzione 0x16 -- che e' proprio cio' che serve per lo swap.
        Assert.Equal(0x02, cpu.State.GetReg8(4));           // floppy con change-line
        Assert.Equal(0, machine.Memory.ReadWord(0x7004) & 1);   // nessun errore
    }

    [Fact]
    public void CaricareSsInibisceLeInterruzioniPerUnIstruzione()
    {
        // Lo stack si cambia con due istruzioni (MOV SS,.. poi MOV SP,..) e
        // fra le due SS:SP indica un'area che non esiste. L'architettura
        // garantisce che nessuna interruzione possa intromettersi: senza
        // questa garanzia il gestore impilerebbe il proprio contesto in un
        // punto arbitrario della memoria.
        var bus = new PhysicalMemoryBus();
        var io = new Emulator.Core.IO.IoBus();
        var interrupts = new Emulator.Core.Devices.InterruptController();
        interrupts.Register(io);
        var cpu = new CpuNs.Cpu(bus, io) { Interrupts = interrupts };

        using (bus)
        {
            foreach (CpuNs.SegReg s in Enum.GetValues<CpuNs.SegReg>())
                cpu.State.LoadSegmentRealMode(s, 0);
            cpu.State.Esp = 0x9000;
            cpu.State.Eip = CodeBase;

            // Gestore di IRQ 0 che segna il proprio passaggio.
            bus.WriteWord(0x08 * 4, 0x0500);
            bus.WriteWord(0x08 * 4 + 2, 0x0000);
            new byte[] {
                0xC6, 0x06, 0x00, 0x70, 0x01,   // MOV byte [0x7000],1
                0xB0, 0x20, 0xE6, 0x20,          // EOI
                0xCF,                            // IRET
            }.CopyTo(bus.GetRamSpan(0x0500, 10));

            //   B8 00 20   MOV AX,0x2000
            //   8E D0      MOV SS,AX      <- da qui le interruzioni sono inibite
            //   BC 00 10   MOV SP,0x1000  <- lo stack e' coerente solo da qui
            //   90         NOP
            new byte[] { 0xB8, 0x00, 0x20, 0x8E, 0xD0, 0xBC, 0x00, 0x10, 0x90 }
                .CopyTo(bus.GetRamSpan(CodeBase, 9));

            cpu.Flags.Interrupt = true;
            io.WriteByte(0x20, 0x11); io.WriteByte(0x21, 0x08);
            io.WriteByte(0x21, 0x04); io.WriteByte(0x21, 0x01); io.WriteByte(0x21, 0xFE);

            cpu.StepWithInterrupts();          // MOV AX
            interrupts.RaiseIrq(0);            // l'interruzione arriva ORA
            cpu.StepWithInterrupts();          // MOV SS: deve inibire

            // Il gestore non deve essere entrato: siamo fra MOV SS e MOV SP.
            Assert.Equal(0, bus.ReadByte(0x7000));
            Assert.Equal(0x2000, cpu.State.Ss.Selector);

            // MOV SP completa il cambio di stack; subito dopo l'interruzione
            // viene servita, e il gestore impila IP, CS e FLAGS sul nuovo
            // stack -- che ora e' valido. SP scende quindi di sei byte.
            cpu.StepWithInterrupts();
            Assert.Equal(0x1000u - 6, cpu.State.Esp);
            Assert.Equal(0x0500u, cpu.State.Eip);   // dentro il gestore

            // Il gestore lascia la propria traccia.
            cpu.StepWithInterrupts();
            Assert.Equal(1, bus.ReadByte(0x7000));

            // La prova che conta: il contesto e' finito sul nuovo stack,
            // non in un'area arbitraria.
            Assert.Equal(0x2000u * 16 + 0x1000 - 6, cpu.State.Ss.Base + cpu.State.Esp);
        }
    }

    [Fact]
    public void OpcodeSconosciutoVieneSegnalato()
    {
        // La diagnostica che ha permesso di trovare XLAT: senza, un opcode
        // mancante e' indistinguibile da un salto sbagliato.
        var (cpu, bus) = Setup(0x0F, 0x0B);   // opcode a due byte inesistente
        using (bus)
        {
            byte reported = 0;
            bool called = false;
            cpu.UnknownTwoByteOpcode += (op, ip, cs) => { reported = op; called = true; };

            bus.WriteWord(6 * 4, 0x0500);     // vettore dell'eccezione 6
            bus.WriteWord(6 * 4 + 2, 0x0000);
            cpu.Step();

            Assert.True(called, "l'opcode sconosciuto non e' stato segnalato");
            Assert.Equal(0x0B, reported);
        }
    }
}
