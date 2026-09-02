using Aurora386.Core.IO;
using Aurora386.Core.Memory;

namespace Aurora386.Core.Devices;

/// <summary>
/// Porta di controllo di sistema (0x92), introdotta con il PS/2.
///
/// Controllare A20 attraverso il controller di tastiera richiede di attendere
/// che il buffer si svuoti e costa migliaia di cicli; questa porta fa la
/// stessa cosa con una singola scrittura, ed e' la via che DOS 6.22 e Windows
/// usano davvero. Il bit 1 e' A20, il bit 0 provoca un reset del processore.
/// </summary>
public sealed class SystemControlPort : IIoDevice
{
    private readonly PhysicalMemoryBus _memory;
    // Il valore riflette lo stato della linea, che all'accensione e' spenta.
    private byte _value;

    /// <summary>Richiesta di reset veloce del processore.</summary>
    public bool ResetRequested { get; set; }

    public SystemControlPort(PhysicalMemoryBus memory) => _memory = memory;

    public void Register(IoBus io) => io.Register(0x92, this);

    public byte ReadByte(ushort port)
    {
        // Il bit 1 riporta lo stato effettivo della linea, che potrebbe essere
        // stato cambiato dal controller di tastiera.
        return (byte)((_value & ~0x02) | (_memory.A20Enabled ? 0x02 : 0x00));
    }

    public void WriteByte(ushort port, byte value)
    {
        _value = value;

        _memory.A20Enabled = (value & 0x02) != 0;

        // Il bit 0 e' il reset veloce: si attiva scrivendo 1, non 0 come nel
        // controller di tastiera.
        if ((value & 0x01) != 0) ResetRequested = true;
    }
}
