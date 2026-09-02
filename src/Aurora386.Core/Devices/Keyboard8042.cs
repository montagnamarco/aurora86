using Aurora386.Core.IO;
using Aurora386.Core.Memory;

namespace Aurora386.Core.Devices;

/// <summary>
/// Controller di tastiera 8042.
///
/// La tastiera del PC non manda caratteri ma "scancode": un codice quando il
/// tasto scende e lo stesso codice con il bit 7 acceso quando risale. E' il
/// BIOS a tradurli in caratteri ASCII tenendo conto di shift e maiuscole.
///
/// Il controller ha anche un compito che non c'entra nulla con la tastiera:
/// comanda la linea A20 e il reset del processore, eredita' del progetto
/// dell'AT che i sistemi operativi dell'epoca sfruttano per uscire dal
/// protected mode.
/// </summary>
public sealed class Keyboard8042 : IIoDevice
{
    private readonly InterruptController _interrupts;

    /// <summary>
    /// Byte pronti per la porta 0x60: risposte del controller e scancode gia'
    /// ammessi. Il controller ne consegna uno per volta.
    /// </summary>
    private readonly Queue<byte> _outputBuffer = new();

    /// <summary>
    /// Scancode ancora nella tastiera. Restano qui finche' il bit 4 del byte
    /// di comando la tiene disabilitata: il software spegne la tastiera
    /// mentre interroga il controller, e i tasti premuti nel frattempo non
    /// devono infilarsi fra una domanda e la sua risposta.
    /// </summary>
    private readonly Queue<byte> _scanCodes = new();

    /// <summary>
    /// Vero quando il byte pronto in uscita e' la risposta a un comando e non
    /// un tasto. Serve a distinguere cosa si puo' buttare via quando arriva un
    /// comando nuovo: la risposta vecchia si perde, il tasto no.
    /// </summary>
    private bool _uscitaERisposta;

    private byte _commandByte = 0x45;   // interruzioni tastiera abilitate
    private byte _pendingCommand;
    private bool _expectingData;

    /// <summary>
    /// Bus su cui agisce la linea A20. Lo stato non e' tenuto qui: la linea e'
    /// una sola e puo' essere comandata anche dalla porta 0x92, quindi la
    /// verita' sta nel bus e non in uno dei due controller.
    /// </summary>
    private readonly PhysicalMemoryBus _memory;

    public bool A20Enabled => _memory.A20Enabled;

    /// <summary>Byte di comando del controller, per diagnostica.</summary>
    public byte CommandByte => _commandByte;

    /// <summary>Richiesta di reset della CPU dal controller (Ctrl+Alt+Canc).</summary>
    public bool ResetRequested { get; set; }

    public Keyboard8042(InterruptController interrupts, PhysicalMemoryBus memory)
    {
        _interrupts = interrupts;
        _memory = memory;
    }

    public void Register(IoBus io)
    {
        io.Register(0x60, this);
        io.Register(0x64, this);
    }

    /// <summary>
    /// Accoda uno scancode come se un tasto fosse stato premuto o rilasciato,
    /// e segnala IRQ 1.
    /// </summary>
    public void PushScanCode(byte scanCode)
    {
        if (_scanCodes.Count >= 16) return;       // buffer pieno, come sul ferro
        _scanCodes.Enqueue(scanCode);
        ConsegnaScanCode();
    }

    /// <summary>
    /// Sposta il prossimo scancode nel buffer di uscita, se la tastiera e'
    /// abilitata e il controller non sta gia' tenendo una risposta.
    /// </summary>
    private void ConsegnaScanCode()
    {
        bool disabilitata = (_commandByte & 0x10) != 0;
        if (disabilitata || _scanCodes.Count == 0) return;

        // Una risposta che nessuno ha letto non blocca il tasto: il registro
        // di uscita e' uno solo e il dato nuovo prende il posto del vecchio.
        // Restare in attesa la trasformerebbe in un tappo permanente.
        if (_outputBuffer.Count > 0)
        {
            if (!_uscitaERisposta) return;   // c'e' gia' un tasto: aspetta
            _outputBuffer.Clear();
        }

        _outputBuffer.Enqueue(_scanCodes.Dequeue());
        _uscitaERisposta = false;
        // Il bit 0 del byte di comando abilita l'interruzione della tastiera.
        if ((_commandByte & 0x01) != 0) _interrupts.RaiseIrq(1);
    }

    public byte ReadByte(ushort port)
    {
        if (port == 0x64)
        {
            // Registro di stato. La base 0x1C accende il flag di sistema
            // (bit 2, impostato dal POST), il bit 3 che distingue comandi da
            // dati e il bit 4 che dichiara la tastiera non inibita: e' la
            // stessa combinazione usata da DOSBox-X. Il bit 0 si aggiunge
            // quando c'e' davvero un byte da leggere.
            byte status = 0x1C;
            if (_outputBuffer.Count > 0) status |= 0x01;
            return status;
        }

        // Porta 0x60: il byte in cima al buffer.
        if (_outputBuffer.Count == 0) return 0;

        byte value = _outputBuffer.Dequeue();
        if (_outputBuffer.Count == 0) _interrupts.LowerIrq(1);
        // Liberato il registro, il prossimo tasto puo' salire.
        ConsegnaScanCode();
        return value;
    }

    public void WriteByte(ushort port, byte value)
    {
        if (port == 0x64)
        {
            HandleCommand(value);
            return;
        }

        // Porta 0x60: dato che segue un comando, oppure comando alla tastiera.
        if (_expectingData)
        {
            _expectingData = false;
            switch (_pendingCommand)
            {
                case 0xF3:   // parametro della velocita' di ripetizione
                case 0xF0:   // numero del set di scancode
                    // Il valore non ha effetto qui, ma va confermato: il
                    // software attende l'ACK anche per il parametro.
                    EnqueueRisposta(0xFA);
                    SegnalaSeConsentito();
                    break;

                case 0x60: _commandByte = value; ConsegnaScanCode(); break;
                case 0xD1:
                    // Scrittura della porta di uscita: il bit 1 e' A20, il bit
                    // 0 azzerato richiede il reset del processore.
                    _memory.A20Enabled = (value & 0x02) != 0;
                    if ((value & 0x01) == 0) ResetRequested = true;
                    break;
            }
            return;
        }

        // Comandi diretti alla tastiera. Ogni comando riconosciuto deve
        // ricevere una risposta: il software attende con un timeout, e il
        // silenzio viene interpretato come tastiera assente o guasta.
        switch (value)
        {
            case 0xFF:                        // reset
                EnqueueRisposta(0xFA);  // ACK
                EnqueueRisposta(0xAA);  // test superato
                SegnalaSeConsentito();
                break;

            case 0xF2:
                // Identificazione: ACK seguito dai due byte che descrivono il
                // modello. 0xAB 0x41 e' la tastiera MF2 tradotta dal
                // controller, quella che ogni PC dell'epoca dichiara.
                EnqueueRisposta(0xFA);
                EnqueueRisposta(0xAB);
                EnqueueRisposta(0x41);
                SegnalaSeConsentito();
                break;

            case 0xF3:                        // velocita' di ripetizione
                // Il byte che segue e' il parametro: va consumato senza
                // interpretarlo, altrimenti verrebbe scambiato per un comando.
                _expectingData = true;
                _pendingCommand = 0xF3;
                EnqueueRisposta(0xFA);
                SegnalaSeConsentito();
                break;

            case 0xEE:                        // eco: risponde con se' stesso
                EnqueueRisposta(0xEE);
                SegnalaSeConsentito();
                break;

            case 0xF0:                        // seleziona set di scancode
                _expectingData = true;
                _pendingCommand = 0xF0;
                EnqueueRisposta(0xFA);
                SegnalaSeConsentito();
                break;

            case 0xF4:                        // abilita scansione
            case 0xF5:                        // disabilita e imposta default
            case 0xF6:                        // imposta default
            case 0xED:                        // imposta i LED
                EnqueueRisposta(0xFA);
                SegnalaSeConsentito();
                break;

            default:
                // Un comando sconosciuto riceve comunque una risposta: sul
                // ferro reale la tastiera segnala il rifiuto con RESEND, e il
                // software sa gestirlo. Il silenzio invece lo blocca.
                EnqueueRisposta(0xFE);   // RESEND
                SegnalaSeConsentito();
                break;
        }
    }

    /// <summary>Mette in uscita una risposta del controller.</summary>
    private void EnqueueRisposta(byte value)
    {
        _outputBuffer.Enqueue(value);
        _uscitaERisposta = true;
    }

    /// <summary>
    /// Segnala che c'e' qualcosa da leggere sulla porta 0x60. Il bit 0 del
    /// byte di comando decide se il controller puo' interrompere: quando e'
    /// spento il software interroga lo stato invece di aspettare l'IRQ, e
    /// un'interruzione non richiesta lo farebbe reagire a un tasto mai premuto.
    /// </summary>
    private void SegnalaSeConsentito()
    {
        if ((_commandByte & 0x01) != 0) _interrupts.RaiseIrq(1);
    }

    private void HandleCommand(byte command)
    {
        // Un comando nuovo annulla la risposta non ancora letta: il
        // controller ha un solo registro di uscita, e chi scrive su 0x64
        // senza aver svuotato 0x60 rinuncia a quel byte. Senza questo, una
        // risposta lunga mai consumata -- come i due byte di identificazione
        // della tastiera -- resterebbe davanti a ogni tasto successivo.
        // Un tasto gia' salito in uscita invece resta: la pressione e'
        // avvenuta e il software se l'aspetta.
        // Solo i comandi che producono a loro volta una risposta prendono il
        // posto di quella non ancora letta: il registro di uscita e' uno solo.
        // I comandi che non rispondono -- abilita, disabilita, reset --
        // lasciano intatto quello che c'e', perche' il software li usa proprio
        // mentre sta aspettando la risposta a un comando precedente.
        bool produceRisposta = command is 0x20 or 0xAA or 0xAB or 0xA9 or 0xA1 or 0xD0;
        if (_uscitaERisposta && produceRisposta)
        {
            _outputBuffer.Clear();
            _uscitaERisposta = false;
            _interrupts.LowerIrq(1);
        }

        switch (command)
        {
            case 0x20:   // legge il byte di comando
                EnqueueRisposta(_commandByte);
                break;
            case 0x60:   // scrive il byte di comando: il dato arriva dopo
            case 0xD1:   // scrive la porta di uscita
                _pendingCommand = command;
                _expectingData = true;
                break;
            case 0xA7: _commandByte |= 0x20; break;    // disabilita il mouse
            case 0xA8: _commandByte &= 0xDF; break;    // abilita il mouse
            case 0xAA:                                  // autotest
                EnqueueRisposta(0x55);            // superato
                break;
            case 0xAB:                                  // test dell'interfaccia
                // Zero significa "nessun errore sulle linee": chi non riceve
                // risposta conclude che la tastiera non c'e' e smette di
                // leggerla.
                EnqueueRisposta(0x00);
                break;
            case 0xA9:                                  // test della porta mouse
                EnqueueRisposta(0x00);
                break;
            case 0xA1:                                  // versione del firmware
                EnqueueRisposta(0x00);
                break;
            case 0xAE: _commandByte &= 0xEF; ConsegnaScanCode(); break;   // abilita
            case 0xAD: _commandByte |= 0x10; break;    // disabilita la tastiera
            case 0xD0:                                  // legge la porta di uscita
                // Il bit 0 sempre acceso indica "reset non in corso"; il bit 1
                // riporta A20.
                EnqueueRisposta((byte)(0x01 | (A20Enabled ? 0x02 : 0x00)));
                break;
            case 0xFE:                                  // reset del processore
                ResetRequested = true;
                break;
        }
    }
}
