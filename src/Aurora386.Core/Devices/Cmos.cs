using Aurora386.Core.IO;

namespace Aurora386.Core.Devices;

/// <summary>
/// Orologio di sistema e memoria di configurazione (MC146818).
///
/// Contiene data e ora in cifre BCD e un centinaio di byte che descrivono la
/// macchina: quanta memoria c'e', che dischi sono installati, che tipo di
/// video. Il BIOS li legge all'avvio, e sono l'unico modo che DOS ha di
/// sapere che la macchina ha 16 MB invece di 640 KB.
/// </summary>
public sealed class Cmos : IIoDevice
{
    private readonly byte[] _registers = new byte[128];
    private byte _selectedRegister;

    /// <summary>Ora usata dall'orologio. Iniettabile per rendere i test riproducibili.</summary>
    public Func<DateTime> TimeSource { get; set; } = () => DateTime.Now;

    public Cmos(uint totalMemoryBytes)
    {
        ConfigureMemory(totalMemoryBytes);

        // Byte di stato B: formato a 24 ore, cifre BCD.
        _registers[0x0B] = 0x02;

        // Byte di diagnostica: nessun errore rilevato all'ultimo avvio.
        _registers[0x0E] = 0x00;

        // Configurazione: floppy presente, coprocessore installato.
        _registers[0x14] = 0x27;

        // Tipo di floppy: 0x40 = un drive da 1.44 MB come primario.
        _registers[0x10] = 0x40;

        // Disco fisso: 0xF0 indica che il tipo e' descritto nei registri estesi.
        _registers[0x12] = 0xF0;
    }

    private void ConfigureMemory(uint totalBytes)
    {
        // Memoria convenzionale: sempre 640 KB, e' il limite architetturale
        // sotto il quale sta il framebuffer video.
        const ushort conventionalKb = 640;
        _registers[0x15] = unchecked((byte)conventionalKb);
        _registers[0x16] = (byte)(conventionalKb >> 8);

        // Memoria estesa oltre il primo megabyte, in KB. I registri sono a 16
        // bit e saturano a 65535 KB (64 MB): oltre quella soglia il BIOS usa
        // altri metodi, ma con 16 MB non e' un problema.
        uint extendedKb = totalBytes > 0x100000 ? (totalBytes - 0x100000) / 1024 : 0;
        ushort capped = (ushort)Math.Min(extendedKb, 0xFFFF);
        _registers[0x17] = (byte)capped;
        _registers[0x18] = (byte)(capped >> 8);

        // Copia negli alias che alcuni BIOS leggono al posto dei precedenti.
        _registers[0x30] = _registers[0x17];
        _registers[0x31] = _registers[0x18];
    }

    public void Register(IoBus io) => io.Register(0x70, 2, this);

    public byte ReadByte(ushort port)
    {
        // La porta 0x70 e' di sola scrittura: leggerla non ha senso e sul
        // ferro reale restituisce valori non definiti.
        if ((port & 1) == 0) return 0xFF;

        return _selectedRegister switch
        {
            0x00 => ToBcd(TimeSource().Second),
            0x02 => ToBcd(TimeSource().Minute),
            0x04 => ToBcd(TimeSource().Hour),
            0x06 => ToBcd((int)TimeSource().DayOfWeek + 1),
            0x07 => ToBcd(TimeSource().Day),
            0x08 => ToBcd(TimeSource().Month),
            0x09 => ToBcd(TimeSource().Year % 100),
            0x32 => ToBcd(TimeSource().Year / 100),
            // Registro di stato A: il bit 7 segnala un aggiornamento in corso.
            // Lo teniamo sempre a zero, cosi' chi lo interroga non attende mai.
            0x0A => 0x26,
            _ => _registers[_selectedRegister & 0x7F],
        };
    }

    public void WriteByte(ushort port, byte value)
    {
        if ((port & 1) == 0)
        {
            // Il bit 7 della porta 0x70 controlla la NMI, non fa parte
            // dell'indirizzo del registro.
            _selectedRegister = (byte)(value & 0x7F);
            return;
        }

        // Data e ora sono derivate dall'orologio ospite: le scritture su quei
        // registri vengono accettate ma non hanno effetto, per non dare
        // l'impressione che l'ora sia stata cambiata davvero.
        if (_selectedRegister <= 0x09 || _selectedRegister == 0x32) return;

        _registers[_selectedRegister & 0x7F] = value;
    }

    private static byte ToBcd(int value) => (byte)(((value / 10) << 4) | (value % 10));
}
