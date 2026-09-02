using Emulator.Core.IO;

namespace Emulator.Core.Devices;

/// <summary>
/// Controller di interruzioni 8259A.
///
/// Il PC ne ha due in cascata: il master serve IRQ 0-7 alle porte 0x20/0x21,
/// lo slave IRQ 8-15 alle porte 0xA0/0xA1, collegato al master tramite IRQ 2.
/// E' il motivo per cui IRQ 2 e IRQ 9 sono lo stesso piedino sui PC reali.
///
/// Il chip si programma con una sequenza di "parole di inizializzazione"
/// (ICW1-ICW4) che il BIOS invia all'avvio; da quel momento in poi le
/// scritture sulla porta dispari sono la maschera delle interruzioni abilitate.
/// </summary>
public sealed class Pic8259 : IIoDevice
{
    private readonly ushort _basePort;
    private readonly bool _isMaster;
    private readonly Pic8259? _slave;

    /// <summary>Interruzioni in attesa di essere servite.</summary>
    private byte _requestRegister;

    /// <summary>Interruzioni attualmente in servizio, non ancora concluse da EOI.</summary>
    private byte _serviceRegister;

    /// <summary>Maschera: un bit a 1 disabilita la IRQ corrispondente.</summary>
    private byte _maskRegister = 0xFF;

    /// <summary>Vettore di interruzione della IRQ 0 di questo controller.</summary>
    private byte _vectorBase;

    // Stato della sequenza di inizializzazione.
    private int _initStep;
    private bool _expectIcw4;
    private bool _readIsr;      // la porta pari legge ISR invece di IRR

    public Pic8259(ushort basePort, bool isMaster, Pic8259? slave = null)
    {
        _basePort = basePort;
        _isMaster = isMaster;
        _slave = slave;
    }

    /// <summary>Segnala l'arrivo di una interruzione hardware su questa linea.</summary>
    public void RaiseIrq(int line)
    {
        _requestRegister |= (byte)(1 << line);
    }

    public void LowerIrq(int line)
    {
        _requestRegister &= (byte)~(1 << line);
    }

    /// <summary>
    /// Vettore della interruzione da servire, o -1 se non ce n'e' nessuna.
    /// La priorita' e' fissa: la linea 0 batte la 1, e cosi' via.
    /// </summary>
    public int GetPendingVector()
    {
        byte pending = (byte)(_requestRegister & ~_maskRegister);
        if (pending == 0) return -1;

        for (int line = 0; line < 8; line++)
        {
            byte bit = (byte)(1 << line);
            if ((pending & bit) == 0) continue;

            // Una interruzione di priorita' pari o inferiore a una gia' in
            // servizio deve attendere: e' cio' che impedisce a un gestore di
            // essere interrotto da se' stesso.
            if (_serviceRegister != 0 && (_serviceRegister & ~(bit - 1)) != 0)
                return -1;

            // La cascata: la linea 2 del master non e' una IRQ vera, e' lo
            // slave che chiede di essere servito.
            if (_isMaster && line == 2 && _slave is not null)
            {
                int slaveVector = _slave.GetPendingVector();
                if (slaveVector < 0) return -1;
                _requestRegister &= (byte)~bit;
                _serviceRegister |= bit;
                return slaveVector;
            }

            _requestRegister &= (byte)~bit;
            _serviceRegister |= bit;
            return _vectorBase + line;
        }

        return -1;
    }

    public byte ReadByte(ushort port)
    {
        bool commandPort = (port & 1) == 0;
        if (commandPort)
            return _readIsr ? _serviceRegister : _requestRegister;
        return _maskRegister;
    }

    public void WriteByte(ushort port, byte value)
    {
        bool commandPort = (port & 1) == 0;

        if (commandPort)
        {
            if ((value & 0x10) != 0)
            {
                // ICW1: inizia la sequenza di inizializzazione.
                _expectIcw4 = (value & 0x01) != 0;
                _initStep = 1;
                _maskRegister = 0;
                _serviceRegister = 0;
                return;
            }

            if ((value & 0x08) != 0)
            {
                // OCW3: seleziona quale registro legge la porta pari.
                if ((value & 0x02) != 0) _readIsr = (value & 0x01) != 0;
                return;
            }

            // OCW2: comandi di fine interruzione.
            HandleEndOfInterrupt(value);
            return;
        }

        // Porta dispari: durante l'inizializzazione sono ICW2-ICW4, dopo e'
        // la maschera.
        switch (_initStep)
        {
            case 1:
                // ICW2: base dei vettori. I tre bit bassi sono ignorati, il
                // vettore effettivo e' base + numero di linea.
                _vectorBase = (byte)(value & 0xF8);
                _initStep = 2;
                return;
            case 2:
                // ICW3: quale linea porta la cascata. Non serve emularlo, il
                // collegamento e' fisso.
                _initStep = _expectIcw4 ? 3 : 0;
                return;
            case 3:
                // ICW4: modo 8086 contro modo MCS-80. Il PC usa sempre 8086.
                _initStep = 0;
                return;
            default:
                _maskRegister = value;
                return;
        }
    }

    private void HandleEndOfInterrupt(byte command)
    {
        int mode = (command >> 5) & 0x07;

        switch (mode)
        {
            case 0b001:   // EOI non specifico: chiude la piu' prioritaria in servizio
            {
                for (int line = 0; line < 8; line++)
                {
                    byte bit = (byte)(1 << line);
                    if ((_serviceRegister & bit) != 0)
                    {
                        _serviceRegister &= (byte)~bit;
                        return;
                    }
                }
                return;
            }
            case 0b011:   // EOI specifico: chiude la linea indicata
            {
                _serviceRegister &= (byte)~(1 << (command & 7));
                return;
            }
        }
    }

    /// <summary>true se c'e' almeno una interruzione non mascherata in attesa.</summary>
    public bool HasPendingInterrupt => (_requestRegister & ~_maskRegister) != 0;

    /// <summary>
    /// true se una interruzione hardware e' in corso di servizio e attende
    /// l'EOI. Distingue una IRQ vera da una interruzione software finita sullo
    /// stesso gestore.
    /// </summary>
    public bool HasInterruptInService => _serviceRegister != 0;
}

/// <summary>
/// I due 8259 in cascata come li monta il PC, con l'instradamento delle 16 IRQ.
/// </summary>
public sealed class InterruptController
{
    public Pic8259 Master { get; }
    public Pic8259 Slave { get; }

    public InterruptController()
    {
        Slave = new Pic8259(0xA0, isMaster: false);
        Master = new Pic8259(0x20, isMaster: true, Slave);
    }

    public void Register(IoBus io)
    {
        io.Register(0x20, 2, Master);
        io.Register(0xA0, 2, Slave);
    }

    /// <summary>
    /// Solleva una delle 16 IRQ del PC. Le linee 8-15 passano dallo slave, che
    /// a sua volta segnala al master sulla linea 2.
    /// </summary>
    public void RaiseIrq(int irq)
    {
        if (irq < 8) Master.RaiseIrq(irq);
        else
        {
            Slave.RaiseIrq(irq - 8);
            Master.RaiseIrq(2);
        }
    }

    public void LowerIrq(int irq)
    {
        if (irq < 8) Master.LowerIrq(irq);
        else Slave.LowerIrq(irq - 8);
    }

    /// <summary>Vettore da servire, o -1 se nessuna interruzione e' pronta.</summary>
    public int GetPendingVector() => Master.GetPendingVector();

    public bool HasPendingInterrupt => Master.HasPendingInterrupt;
}
