using Aurora386.Core.Bios;
using Aurora386.Core.Devices;
using Aurora386.Core.Devices.Video;
using Aurora386.Core.IO;
using Aurora386.Core.Memory;
using CpuNs = Aurora386.Core.Cpu;

namespace Aurora386.Core;

/// <summary>
/// Il PC emulato completo: processore, memoria, dispositivi e BIOS collegati
/// fra loro e pronti a eseguire.
/// </summary>
public sealed class Machine : IDisposable
{
    public PhysicalMemoryBus Memory { get; }
    public IoBus Io { get; }
    public CpuNs.Cpu Cpu { get; }

    public InterruptController Interrupts { get; }
    public Pit8253 Timer { get; }
    public Keyboard8042 Keyboard { get; }
    public SystemControlPort SystemControl { get; }
    public SystemPort61 SystemPort { get; }
    public Cmos Cmos { get; }

    public VgaRegisters VideoRegisters { get; }
    public VgaMemory VideoMemory { get; }
    public VgaRenderer Video { get; }

    public BiosDataArea Bda { get; }
    public VideoServices VideoServices { get; }
    public DiskServices DiskServices { get; }
    public KeyboardServices KeyboardServices { get; }
    public XmsManager Xms { get; }

    /// <summary>Frequenza a cui la CPU emulata dichiara di girare.</summary>
    public int CpuFrequencyHz { get; set; } = 25_000_000;   // 386 a 25 MHz

    /// <summary>true dopo che il settore di avvio ha preso il controllo.</summary>
    public bool Booted { get; private set; }

    private readonly List<DiskImage> _disks = [];

    public Machine(uint ramSize = 16 * 1024 * 1024)
    {
        Memory = new PhysicalMemoryBus(ramSize);
        Io = new IoBus();

        Interrupts = new InterruptController();
        Interrupts.Register(Io);

        Timer = new Pit8253(Interrupts);
        Timer.Register(Io);

        Keyboard = new Keyboard8042(Interrupts, Memory);
        Keyboard.Register(Io);

        SystemControl = new SystemControlPort(Memory);
        SystemControl.Register(Io);

        SystemPort = new SystemPort61(Timer);
        SystemPort.Register(Io);

        Cmos = new Cmos(ramSize);
        Cmos.Register(Io);

        VideoRegisters = new VgaRegisters();
        VideoRegisters.Register(Io);
        VideoMemory = new VgaMemory(VideoRegisters);
        Memory.MapDevice(VgaMemory.Base, VgaMemory.Size, VideoMemory);
        Video = new VgaRenderer(VideoMemory, VideoRegisters);

        Cpu = new CpuNs.Cpu(Memory, Io) { Interrupts = Interrupts };

        Bda = new BiosDataArea(Memory);
        VideoServices = new VideoServices(VideoMemory, VideoRegisters, Bda);
        DiskServices = new DiskServices(Bda);
        KeyboardServices = new KeyboardServices(Bda, Memory, Keyboard);
        Xms = new XmsManager(Memory);

        InstallBios();
    }

    private void InstallBios()
    {
        Memory.MapRom(BiosRom.Base, BiosRom.Build());

        // Il 386 parte da 0xFFFFFFF0: gli ultimi 64 KB dello spazio a 32 bit
        // devono mostrare la stessa ROM mappata a 0xF0000.
        Memory.MapHighAlias(0xFFFF0000, BiosRom.Base);
        Cpu.NativeHandler = HandleBiosCall;
        Cpu.Reset();
    }

    /// <summary>Collega un'immagine disco a una unita' BIOS.</summary>
    public void AttachDisk(DiskImage image)
    {
        _disks.Add(image);
        DiskServices.Attach(image);
    }

    /// <summary>
    /// Smista le chiamate BIOS agli handler. E' il punto in cui l'esecuzione
    /// esce dal codice emulato e passa al C#.
    /// </summary>
    private void HandleBiosCall(CpuNs.Cpu cpu, byte handlerId)
    {
        switch ((BiosRom.Handler)handlerId)
        {
            case BiosRom.Handler.Post: RunPost(); return;
            case BiosRom.Handler.Video: VideoServices.Handle(cpu); return;
            case BiosRom.Handler.Disk: DiskServices.Handle(cpu); return;
            case BiosRom.Handler.Keyboard: KeyboardServices.Handle(cpu); return;
            case BiosRom.Handler.KeyboardIrq: KeyboardServices.HandleIrq(cpu); return;
            case BiosRom.Handler.TimerTick: HandleTimerTick(cpu); return;
            case BiosRom.Handler.EquipmentList: cpu.State.Ax = Bda.EquipmentWord; return;
            case BiosRom.Handler.MemorySize: cpu.State.Ax = Bda.MemorySizeKb; return;
            case BiosRom.Handler.System: HandleSystemServices(cpu); return;
            case BiosRom.Handler.TimeOfDay: HandleTimeOfDay(cpu); return;
            case BiosRom.Handler.Bootstrap: Boot(cpu); return;
            case BiosRom.Handler.Multiplex: HandleMultiplex(cpu); return;
            case BiosRom.Handler.Xms: Xms.Handle(cpu); return;

            case BiosRom.Handler.Serial: HandleSerial(cpu); return;
            case BiosRom.Handler.Printer: HandlePrinter(cpu); return;

            case BiosRom.Handler.DummyIrq:
                // Gestore generico: serve sia per le IRQ hardware senza un
                // gestore proprio, sia per riempire i vettori che nessuno usa.
                // Nel primo caso va chiusa l'interruzione, altrimenti il PIC
                // resta bloccato e non ne consegna piu' nessuna; nel secondo
                // no, perche' un EOI non richiesto chiuderebbe l'interruzione
                // sbagliata. Non potendo distinguere i due casi dall'handler,
                // si guarda se il PIC ha davvero qualcosa in servizio.
                if (Interrupts.Master.HasInterruptInService)
                {
                    // Un'interruzione arrivata dal secondo controller va
                    // chiusa su entrambi: fermarsi al primo lascerebbe il
                    // secondo convinto di essere ancora in servizio, e non ne
                    // consegnerebbe piu' nessuna.
                    if (Interrupts.Slave.HasInterruptInService)
                        cpu.Io.WriteByte(0xA0, 0x20);
                    cpu.Io.WriteByte(0x20, 0x20);
                }
                return;
        }
    }

    /// <summary>
    /// Inizializzazione della macchina: prepara la BDA, la tabella dei
    /// vettori e i dispositivi, come farebbe il POST di un BIOS reale.
    /// </summary>
    private void RunPost()
    {
        Bda.Initialize(640, DiskServices.HardDiskCount);
        InstallInterruptVectors();
        InitializeInterruptController();

        // Modalita' testo 80x25, che e' come si presenta un PC all'accensione.
        VideoRegisters.VideoMode = 0x03;
        VideoRegisters.TextColumns = 80;
        VideoRegisters.TextRows = 25;
        ClearScreen();
        VideoRegisters.NotifyModeChanged();
    }

    private void InstallInterruptVectors()
    {
        // Nessun vettore deve restare a zero. Un BIOS reale li riempie tutti,
        // e il software ci conta: DOS legge parole dalla tabella e le usa come
        // segmenti anche per vettori che non gestisce, quindi uno zero la' non
        // e' "vettore libero" ma un puntatore che manda l'esecuzione a
        // 0000:0000. Prima si riempie tutto, poi si sovrascrive cio' che ha un
        // gestore vero.
        for (int vector = 0; vector < 256; vector++)
            SetVector((byte)vector, BiosRom.Entry.DummyIrq);

        // Ogni vettore e' offset:segmento, quattro byte, a partire da 0.
        SetVector(0x08, BiosRom.Entry.TimerTick);
        SetVector(0x09, BiosRom.Entry.KeyboardIrq);
        SetVector(0x10, BiosRom.Entry.Video);
        SetVector(0x11, BiosRom.Entry.EquipmentList);
        SetVector(0x12, BiosRom.Entry.MemorySize);
        SetVector(0x13, BiosRom.Entry.Disk);
        SetVector(0x14, BiosRom.Entry.Serial);
        SetVector(0x15, BiosRom.Entry.System);
        SetVector(0x16, BiosRom.Entry.Keyboard);
        SetVector(0x17, BiosRom.Entry.Printer);
        SetVector(0x19, BiosRom.Entry.Bootstrap);
        SetVector(0x1A, BiosRom.Entry.TimeOfDay);
        SetVector(0x2F, BiosRom.Entry.Multiplex);

        // Le IRQ 2-7 e 8-15 senza gestore proprio puntano a un handler che si
        // limita a chiudere l'interruzione.
        for (byte vector = 0x0A; vector <= 0x0F; vector++)
            SetVector(vector, BiosRom.Entry.DummyIrq);
        for (byte vector = 0x70; vector <= 0x77; vector++)
            SetVector(vector, BiosRom.Entry.DummyIrq);
    }

    private void SetVector(byte vector, ushort offset)
    {
        Memory.WriteWord((uint)vector * 4, offset);
        Memory.WriteWord((uint)vector * 4 + 2, 0xF000);
    }

    /// <summary>
    /// Programma i due PIC come fa il BIOS: IRQ 0-7 sui vettori 0x08-0x0F,
    /// IRQ 8-15 sui vettori 0x70-0x77.
    /// </summary>
    private void InitializeInterruptController()
    {
        // Master
        Io.WriteByte(0x20, 0x11);   // ICW1: inizializzazione, seguira' ICW4
        Io.WriteByte(0x21, 0x08);   // ICW2: base dei vettori
        Io.WriteByte(0x21, 0x04);   // ICW3: lo slave e' sulla linea 2
        Io.WriteByte(0x21, 0x01);   // ICW4: modo 8086
        Io.WriteByte(0x21, 0xB8);   // maschera: timer, tastiera e cascata attivi

        // Slave
        Io.WriteByte(0xA0, 0x11);
        Io.WriteByte(0xA1, 0x70);
        Io.WriteByte(0xA1, 0x02);
        Io.WriteByte(0xA1, 0x01);
        Io.WriteByte(0xA1, 0xFF);   // tutte mascherate: nessun dispositivo collegato
    }

    private void ClearScreen()
    {
        for (int i = 0; i < 80 * 25; i++)
        {
            uint offset = VgaMemory.TextWindowOffset + (uint)(i * 2);
            VideoMemory[offset] = (byte)' ';
            VideoMemory[offset + 1] = 0x07;
        }
    }

    /// <summary>
    /// Gestore del tick di sistema: incrementa il contatore della BDA e
    /// richiama INT 1Ch, che i programmi possono agganciare.
    /// </summary>
    private void HandleTimerTick(CpuNs.Cpu cpu)
    {
        uint ticks = Bda.TimerTicks + 1;

        // Il contatore si azzera a mezzanotte, dopo 1.573.040 tick, e alza un
        // flag che DOS usa per far avanzare la data.
        if (ticks >= 1_573_040)
        {
            ticks = 0;
            Bda.MidnightFlag = 1;
        }
        Bda.TimerTicks = ticks;

        cpu.Io.WriteByte(0x20, 0x20);   // fine interruzione
    }

    /// <summary>
    /// INT 2Fh, l'interruzione "multiplex" con cui i programmi residenti si
    /// annunciano. Qui serve a far trovare il gestore XMS: un programma
    /// chiede con AX=4300h se c'e', e con AX=4310h l'indirizzo da chiamare.
    /// </summary>
    private void HandleMultiplex(CpuNs.Cpu cpu)
    {
        // Il carry va sempre pulito prima di rispondere.
        //
        // INT 2Fh segnala "nessuno ha risposto" lasciando AX invariato, non
        // accendendo il carry: quest'ultimo significa errore. Poiche' il
        // chiamante puo' entrare con il carry gia' acceso -- DOS lo fa
        // deliberatamente con STC prima di certe chiamate -- lasciarlo com'e'
        // trasforma un "servizio assente" in un "operazione fallita", e chi
        // ha chiamato interpreta il risultato come errore.
        cpu.SetCarryInStackedFlags(false);

        ushort function = cpu.State.Ax;

        switch (function)
        {
            case 0x4300:
                // "XMS e' installato": la risposta attesa e' 0x80 in AL.
                cpu.State.SetReg8(0, 0x80);
                return;

            case 0x4310:
                // Indirizzo del punto di ingresso, in ES:BX. Da qui in avanti
                // il programma lo chiama direttamente, senza piu' passare da
                // questa interruzione.
                cpu.SetSegment(CpuNs.SegReg.Es, 0xF000);
                cpu.State.Bx = BiosRom.Entry.XmsEntry;
                return;

            default:
                // Nessun altro servizio residente e' installato: AX resta
                // invariato, che e' proprio il modo in cui INT 2Fh dice
                // "nessuno ha risposto".
                return;
        }
    }

    /// <summary>
    /// INT 14h: porte seriali. Non ce ne sono collegate.
    ///
    /// La distinzione che conta e' fra "non pronto adesso" e "non c'e'".
    /// Il codice 0x80 significa **timeout**, cioe' un guasto transitorio: chi
    /// lo riceve riprova, e su una porta inesistente riprova per sempre. Uno
    /// stato con la linea assente e' invece una risposta definitiva, che il
    /// software interpreta come "questa porta non esiste" e prosegue.
    /// </summary>
    private void HandleSerial(CpuNs.Cpu cpu)
    {
        // AH = stato della linea, AL = stato del modem. Il bit 5 di AH
        // (registro di trasmissione vuoto) e il bit 6 (trasmissione
        // completata) restano spenti: nulla puo' essere inviato. Nessun bit
        // di errore acceso: non e' un guasto, semplicemente non c'e' nulla.
        cpu.State.Ax = 0x0000;
    }

    /// <summary>
    /// INT 17h: porte parallele. Come per la seriale, la risposta dice
    /// "assente" invece di "occupata", cosi' il software non riprova.
    /// </summary>
    private void HandlePrinter(CpuNs.Cpu cpu)
    {
        // AH = stato della stampante. Il bit 3 segnala un errore di I/O, che
        // e' il modo in cui una porta senza nulla collegato si annuncia.
        cpu.State.SetReg8(4, 0x08);
    }

    private void HandleSystemServices(CpuNs.Cpu cpu)
    {
        byte function = cpu.State.GetReg8(4);   // AH

        switch (function)
        {
            case 0x88:
                // Memoria estesa in KB oltre il primo megabyte.
                uint extendedKb = Memory.RamSize > 0x100000
                    ? (Memory.RamSize - 0x100000) / 1024 : 0;
                cpu.State.Ax = (ushort)Math.Min(extendedKb, 0xFFFF);
                cpu.SetCarryInStackedFlags(false);
                return;

            case 0x4F:
                // Intercettazione degli scancode. Il BIOS la invoca per ogni
                // tasto: chi la sostituisce puo' cambiare o scartare il
                // codice. La versione di serie non tocca AL e lascia il carry
                // acceso, che e' il modo di dire "il tasto vale, elaboralo".
                cpu.SetCarryInStackedFlags(true);
                return;

            case 0xC0:
                // Tabella di configurazione del sistema. Non fornita: alcuni
                // programmi la interrogano ma sanno gestirne l'assenza.
                cpu.State.SetReg8(4, 0x86);
                cpu.SetCarryInStackedFlags(true);
                return;

            case 0x24:
            {
                // Interfaccia BIOS per A20 (INT 15h, AH=24h). DOS e i gestori
                // di memoria estesa la usano invece di parlare direttamente
                // con il controller di tastiera.
                byte subFunction = cpu.State.GetReg8(0);   // AL
                switch (subFunction)
                {
                    case 0x00:   // disabilita
                        Memory.A20Enabled = false;
                        cpu.State.SetReg8(4, 0);
                        cpu.SetCarryInStackedFlags(false);
                        return;
                    case 0x01:   // abilita
                        Memory.A20Enabled = true;
                        cpu.State.SetReg8(4, 0);
                        cpu.SetCarryInStackedFlags(false);
                        return;
                    case 0x02:   // interroga lo stato
                        cpu.State.SetReg8(4, 0);
                        cpu.State.SetReg8(0, (byte)(Memory.A20Enabled ? 1 : 0));
                        cpu.SetCarryInStackedFlags(false);
                        return;
                    case 0x03:   // quali meccanismi di controllo esistono
                        cpu.State.SetReg8(4, 0);
                        // Bit 0: controller di tastiera. Bit 1: porta 0x92.
                        cpu.State.Bx = 0x0003;
                        cpu.SetCarryInStackedFlags(false);
                        return;
                    default:
                        cpu.State.SetReg8(4, 0x86);
                        cpu.SetCarryInStackedFlags(true);
                        return;
                }
            }

            default:
                cpu.State.SetReg8(4, 0x86);   // funzione non supportata
                cpu.SetCarryInStackedFlags(true);
                return;
        }
    }

    private void HandleTimeOfDay(CpuNs.Cpu cpu)
    {
        byte function = cpu.State.GetReg8(4);

        switch (function)
        {
            case 0x00:
            {
                // Conteggio dei tick dall'avvio, in CX:DX.
                uint ticks = Bda.TimerTicks;
                cpu.State.Cx = (ushort)(ticks >> 16);
                cpu.State.Dx = (ushort)ticks;
                cpu.State.SetReg8(0, Bda.MidnightFlag);
                Bda.MidnightFlag = 0;      // la lettura azzera il flag
                return;
            }

            case 0x02:
            {
                // Ora corrente dal CMOS, in cifre BCD.
                cpu.Io.WriteByte(0x70, 0x04);
                cpu.State.SetReg8(5, cpu.Io.ReadByte(0x71));   // CH: ore
                cpu.Io.WriteByte(0x70, 0x02);
                cpu.State.SetReg8(1, cpu.Io.ReadByte(0x71));   // CL: minuti
                cpu.Io.WriteByte(0x70, 0x00);
                cpu.State.SetReg8(2, cpu.Io.ReadByte(0x71));   // DH: secondi
                cpu.State.SetReg8(6, 0);                        // DL: ora solare
                cpu.SetCarryInStackedFlags(false);
                return;
            }

            case 0x04:
            {
                // Data corrente dal CMOS.
                cpu.Io.WriteByte(0x70, 0x32);
                cpu.State.SetReg8(5, cpu.Io.ReadByte(0x71));   // CH: secolo
                cpu.Io.WriteByte(0x70, 0x09);
                cpu.State.SetReg8(1, cpu.Io.ReadByte(0x71));   // CL: anno
                cpu.Io.WriteByte(0x70, 0x08);
                cpu.State.SetReg8(2, cpu.Io.ReadByte(0x71));   // DH: mese
                cpu.Io.WriteByte(0x70, 0x07);
                cpu.State.SetReg8(6, cpu.Io.ReadByte(0x71));   // DL: giorno
                cpu.SetCarryInStackedFlags(false);
                return;
            }

            default:
                cpu.SetCarryInStackedFlags(true);
                return;
        }
    }

    /// <summary>
    /// INT 19h: carica il settore di avvio e gli passa il controllo.
    ///
    /// Il settore va letto a 0000:7C00, indirizzo fissato dall'architettura
    /// del PC, e deve terminare con la firma 0xAA55 -- altrimenti non e' un
    /// disco avviabile.
    /// </summary>
    private void Boot(CpuNs.Cpu cpu)
    {
        foreach (DiskImage disk in _disks.OrderBy(d => d.DriveNumber))
        {
            var sector = new byte[DiskImage.SectorSize];
            if (!disk.ReadSectors(0, 0, 1, 1, sector)) continue;

            if (sector[510] != 0x55 || sector[511] != 0xAA) continue;

            const uint bootAddress = 0x7C00;
            sector.CopyTo(Memory.GetRamSpan(bootAddress, sector.Length));

            // Il settore si aspetta DL con l'unita' da cui e' stato caricato:
            // e' cosi' che sa dove trovare il resto del sistema.
            cpu.State.SetReg8(2, disk.DriveNumber);

            cpu.SetSegment(CpuNs.SegReg.Cs, 0);
            cpu.State.Eip = bootAddress;
            cpu.SetSegment(CpuNs.SegReg.Ds, 0);
            cpu.SetSegment(CpuNs.SegReg.Es, 0);
            cpu.SetSegment(CpuNs.SegReg.Ss, 0);
            cpu.State.Esp = 0x7C00;      // stack appena sotto il settore

            Booted = true;
            return;
        }

        // Nessun disco avviabile: si ferma, come farebbe un PC reale che
        // mostra "No bootable device".
        cpu.State.Halted = true;
    }

    // --- ciclo di esecuzione ---------------------------------------------------

    /// <summary>
    /// Esegue le istruzioni corrispondenti al tempo indicato, facendo avanzare
    /// i dispositivi in proporzione.
    /// </summary>
    public void RunFor(TimeSpan duration)
    {
        long instructions = (long)(CpuFrequencyHz * duration.TotalSeconds);
        RunInstructions(instructions);
    }

    /// <summary>
    /// Esegue un numero fissato di istruzioni. I dispositivi vengono fatti
    /// avanzare a blocchi invece che a ogni istruzione: aggiornare un timer
    /// 25 milioni di volte al secondo costerebbe piu' dell'emulazione stessa.
    /// </summary>
    public void RunInstructions(long count)
    {
        const int chunk = 2000;
        long executed = 0;

        while (executed < count)
        {
            int steps = (int)Math.Min(chunk, count - executed);

            for (int i = 0; i < steps; i++)
            {
                if (Cpu.State.Halted && !Interrupts.HasPendingInterrupt)
                {
                    // In attesa di interruzione: ripetere HLT non serve, ma il
                    // blocco va comunque contato come tempo trascorso. E' la
                    // sola cosa che puo' far arrivare l'interruzione che
                    // risveglia la CPU: saltare l'avanzamento dei dispositivi
                    // qui bloccherebbe la macchina per sempre.
                    break;
                }
                Cpu.StepWithInterrupts();
            }

            executed += steps;
            AdvanceDevices(steps);

            if (Keyboard.ResetRequested || SystemControl.ResetRequested)
            {
                Keyboard.ResetRequested = false;
                SystemControl.ResetRequested = false;
                Cpu.Reset();
            }
        }
    }

    /// <summary>
    /// Fa avanzare i dispositivi del tempo corrispondente alle istruzioni
    /// eseguite. Il timer gira a 1,19 MHz contro i 25 MHz dichiarati dalla
    /// CPU: il rapporto converte le une nell'altro.
    /// </summary>
    private void AdvanceDevices(int instructions)
    {
        double pitCycles = (double)instructions * Pit8253.ClockHz / CpuFrequencyHz;
        Timer.Advance(pitCycles);
    }

    /// <summary>Notifica la pressione di un tasto alla macchina.</summary>
    public void PressKey(byte scanCode) => Keyboard.PushScanCode(scanCode);

    /// <summary>Notifica il rilascio di un tasto.</summary>
    public void ReleaseKey(byte scanCode) => Keyboard.PushScanCode((byte)(scanCode | 0x80));

    /// <summary>Contenuto dello schermo come testo, per test e diagnostica.</summary>
    public string GetScreenText()
    {
        var builder = new System.Text.StringBuilder();
        int columns = VideoRegisters.TextColumns;
        int rows = VideoRegisters.TextRows;

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                uint offset = VgaMemory.TextWindowOffset + (uint)((row * columns + column) * 2);
                byte ch = VideoMemory[offset];
                builder.Append(ch is >= 32 and < 127 ? (char)ch : ' ');
            }
            builder.AppendLine();
        }

        return builder.ToString();
    }

    public void Dispose()
    {
        foreach (DiskImage disk in _disks) disk.Dispose();
        Memory.Dispose();
    }
}
