using Aurora386.Core.IO;

namespace Aurora386.Core.Devices;

/// <summary>
/// Porta di sistema 0x61, eredita' del PPI 8255 del PC originale.
///
/// Raccoglie funzioni senza relazione fra loro:
///  - bit 0-1 in scrittura: abilitano l'altoparlante e il suo collegamento
///    al canale 2 del timer;
///  - bit 4 in lettura: il segnale di rinfresco della DRAM, che oscilla
///    continuamente sul ferro reale;
///  - bit 5 in lettura: l'uscita del canale 2 del timer.
///
/// Il bit di rinfresco e' quello che conta di piu' per un emulatore: DOS e
/// molti programmi lo usano come base dei tempi, aspettando in un ciclo che
/// cambi stato. Restituendo un valore fisso quel ciclo non termina mai, e la
/// macchina sembra bloccata senza alcun errore.
/// </summary>
public sealed class SystemPort61 : IIoDevice
{
    private readonly Pit8253 _timer;

    /// <summary>Bit scrivibili: altoparlante e abilitazione del canale 2.</summary>
    private byte _outputBits;

    /// <summary>
    /// Contatore che genera l'oscillazione del bit di rinfresco. Sul ferro
    /// reale il segnale ha un periodo di circa 15 microsecondi; qui basta che
    /// cambi a ogni lettura, perche' il software lo usa solo per accorgersi
    /// che il tempo scorre.
    /// </summary>
    private int _refreshCounter;

    public SystemPort61(Pit8253 timer) => _timer = timer;

    public void Register(IoBus io) => io.Register(0x61, this);

    /// <summary>true se l'altoparlante e' collegato al timer (bit 0 e 1 attivi).</summary>
    public bool SpeakerEnabled => (_outputBits & 0x03) == 0x03;

    public byte ReadByte(ushort port)
    {
        // Il bit 4 alterna a ogni lettura: e' cio' che permette ai cicli di
        // attesa di terminare. Alternare a ogni lettura invece di legarlo al
        // tempo reale rende anche le prove riproducibili.
        _refreshCounter++;
        byte refreshBit = (byte)((_refreshCounter & 1) != 0 ? 0x10 : 0x00);

        // Il bit 5 riporta l'uscita del canale 2, che i programmi leggono per
        // misurare intervalli brevi contando le oscillazioni.
        byte timerBit = (byte)(_timer.Channel2Output ? 0x20 : 0x00);

        return (byte)((_outputBits & 0x0F) | refreshBit | timerBit);
    }

    public void WriteByte(ushort port, byte value)
    {
        // Solo i quattro bit bassi sono scrivibili; gli altri sono di stato.
        _outputBits = (byte)(value & 0x0F);
    }
}
