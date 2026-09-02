using Emulator.Core.IO;

namespace Emulator.Core.Devices;

/// <summary>
/// Timer programmabile 8253/8254.
///
/// Ha tre contatori indipendenti alimentati da un clock di 1.193.182 Hz --
/// un terzo della frequenza del cristallo NTSC da cui derivava il clock del
/// PC originale. Con il divisore di default (65536) il canale 0 produce
/// 18,2 interruzioni al secondo, ed e' la base con cui DOS misura il tempo.
///
/// Canale 0: IRQ 0, il tick di sistema.
/// Canale 1: rinfresco della DRAM, non serve emularlo davvero.
/// Canale 2: altoparlante interno.
/// </summary>
public sealed class Pit8253 : IIoDevice
{
    public const int ClockHz = 1_193_182;

    private sealed class Channel
    {
        public ushort ReloadValue = 0xFFFF;
        public ushort Counter;
        public byte AccessMode = 3;    // 3 = byte basso poi alto
        public byte OperatingMode;
        public bool WaitingHighByte;
        public bool ReadingHighByte;
        public bool OutputHigh;
        public ushort LatchedValue;
        public bool Latched;

        /// <summary>Frazione di ciclo accumulata, per non perdere tempo nei resti.</summary>
        public double Accumulator;
    }

    private readonly Channel[] _channels = [new(), new(), new()];
    private readonly InterruptController _interrupts;

    /// <summary>Numero di tick del canale 0 dall'avvio, per il conteggio BIOS.</summary>
    public long Channel0Ticks { get; private set; }

    public Pit8253(InterruptController interrupts) => _interrupts = interrupts;

    public void Register(IoBus io) => io.Register(0x40, 4, this);

    /// <summary>
    /// Fa avanzare i contatori del tempo trascorso. Riceve cicli di CPU
    /// emulata e li converte nel clock del timer: e' cio' che tiene il tempo
    /// del PC emulato agganciato alla velocita' a cui gira l'emulatore.
    /// </summary>
    public void Advance(double pitCycles)
    {
        AdvanceChannel2(pitCycles);

        Channel channel0 = _channels[0];
        channel0.Accumulator += pitCycles;

        int whole = (int)channel0.Accumulator;
        if (whole <= 0) return;
        channel0.Accumulator -= whole;

        int reload = channel0.ReloadValue == 0 ? 65536 : channel0.ReloadValue;

        // Un solo tick per chiamata anche se il tempo trascorso ne coprirebbe
        // piu' d'uno: accumulare interruzioni arretrate farebbe correre
        // l'orologio del sistema ospite invece di rallentarlo.
        while (whole > 0)
        {
            int step = Math.Min(whole, channel0.Counter == 0 ? reload : channel0.Counter);
            channel0.Counter = (ushort)(channel0.Counter - step);
            whole -= step;

            if (channel0.Counter == 0)
            {
                channel0.Counter = (ushort)reload;
                Channel0Ticks++;
                _interrupts.RaiseIrq(0);
            }
        }
    }

    public byte ReadByte(ushort port)
    {
        int index = port & 3;
        if (index == 3) return 0;      // la porta di comando non si legge

        Channel channel = _channels[index];
        ushort value = channel.Latched ? channel.LatchedValue : channel.Counter;

        switch (channel.AccessMode)
        {
            case 1: return (byte)value;             // solo byte basso
            case 2: return (byte)(value >> 8);      // solo byte alto
            default:
                // Modo a due byte: prima il basso, poi l'alto.
                if (!channel.ReadingHighByte)
                {
                    channel.ReadingHighByte = true;
                    return (byte)value;
                }
                channel.ReadingHighByte = false;
                channel.Latched = false;
                return (byte)(value >> 8);
        }
    }

    public void WriteByte(ushort port, byte value)
    {
        int index = port & 3;

        if (index == 3)
        {
            WriteControlWord(value);
            return;
        }

        Channel channel = _channels[index];

        switch (channel.AccessMode)
        {
            case 1:
                channel.ReloadValue = value;
                StartCounting(channel);
                return;
            case 2:
                channel.ReloadValue = (ushort)(value << 8);
                StartCounting(channel);
                return;
            default:
                if (!channel.WaitingHighByte)
                {
                    channel.ReloadValue = (ushort)((channel.ReloadValue & 0xFF00) | value);
                    channel.WaitingHighByte = true;
                }
                else
                {
                    channel.ReloadValue = (ushort)((channel.ReloadValue & 0x00FF) | (value << 8));
                    channel.WaitingHighByte = false;
                    StartCounting(channel);
                }
                return;
        }
    }

    private static void StartCounting(Channel channel)
    {
        channel.Counter = channel.ReloadValue;
        channel.Accumulator = 0;
    }

    private void WriteControlWord(byte value)
    {
        int index = (value >> 6) & 3;
        if (index == 3) return;        // comando di lettura multipla, non emulato

        Channel channel = _channels[index];
        byte access = (byte)((value >> 4) & 3);

        if (access == 0)
        {
            // Comando di cattura: congela il contatore per una lettura
            // coerente, senza fermare il conteggio.
            channel.LatchedValue = channel.Counter;
            channel.Latched = true;
            return;
        }

        channel.AccessMode = access;
        channel.OperatingMode = (byte)((value >> 1) & 7);
        channel.WaitingHighByte = false;
        channel.ReadingHighByte = false;
        channel.Latched = false;
    }

    /// <summary>
    /// Uscita del canale 2, letta dal bit 5 della porta 0x61. E' il segnale
    /// che pilota l'altoparlante, e i programmi lo campionano per misurare
    /// intervalli piu' brevi di un tick di sistema.
    /// </summary>
    public bool Channel2Output
    {
        get
        {
            Channel channel = _channels[2];
            int reload = channel.ReloadValue == 0 ? 65536 : channel.ReloadValue;
            // Onda quadra: alta nella prima meta' del conteggio.
            return channel.Counter > reload / 2;
        }
    }

    /// <summary>
    /// Fa avanzare anche il canale 2, che a differenza del canale 0 non genera
    /// interruzioni ma deve comunque contare perche' la sua uscita e'
    /// osservabile dalla porta 0x61.
    /// </summary>
    private void AdvanceChannel2(double pitCycles)
    {
        Channel channel = _channels[2];
        channel.Accumulator += pitCycles;

        int whole = (int)channel.Accumulator;
        if (whole <= 0) return;
        channel.Accumulator -= whole;

        int reload = channel.ReloadValue == 0 ? 65536 : channel.ReloadValue;
        int position = channel.Counter - (whole % reload);
        channel.Counter = (ushort)(position < 0 ? position + reload : position);
    }

    /// <summary>Frequenza attuale del canale 0, per diagnostica.</summary>
    public double Channel0Frequency
    {
        get
        {
            int reload = _channels[0].ReloadValue == 0 ? 65536 : _channels[0].ReloadValue;
            return (double)ClockHz / reload;
        }
    }
}
