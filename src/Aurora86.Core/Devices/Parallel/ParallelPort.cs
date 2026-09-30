using System.IO;

using Aurora86.Core.Devices.Interrupts;
using Aurora86.Core.IO;

namespace Aurora86.Core.Devices.Parallel;

/// <summary>
/// LPT1, the IBM PC's Standard Parallel Port, as the 1984 AT Technical
/// Reference's "Parallel Printer Adapter" chapter defines it: three
/// registers at 378h-37Ah, one IRQ line, docs/PARALLEL-PORT.md section 1.
///
/// DOS's INT 17h and every driver that goes around it (Windows 3.x's and
/// Windows 95's own port drivers) work the same three registers: DATA holds
/// the byte on pins 2-9; STATUS reports back what the printer says about
/// itself; CONTROL carries the handshake and the printer's own reset line.
/// A driver hands a byte to the printer by writing it to DATA, then pulsing
/// STROBE (CONTROL bit 0) high and low again -- the printer is defined here
/// to take the byte on the falling edge, matching QEMU's parallel port
/// model, since the emulated printer is never actually busy and so has
/// nothing of its own to time the pulse against.
/// </summary>
public sealed class ParallelPort : IIoDevice
{
    /// <summary>DATA: the byte on pins 2-9. Read/write; reads return the last byte written.</summary>
    public const ushort DataPort = 0x378;

    /// <summary>STATUS: what the printer reports about itself. Read only.</summary>
    public const ushort StatusPort = 0x379;

    /// <summary>CONTROL: the handshake lines and /INIT. Read/write.</summary>
    public const ushort ControlPort = 0x37A;

    // CONTROL register bits (docs/PARALLEL-PORT.md section 1).
    public const byte ControlStrobe = 0x01;
    public const byte ControlAutoFeed = 0x02;
    public const byte ControlInitialize = 0x04;    // /INIT, active low: 0 = initialize the printer
    public const byte ControlSelectIn = 0x08;
    public const byte ControlIrqEnable = 0x10;

    /// <summary>The bits CONTROL actually stores; the rest are read back as 1 (see <see cref="ControlReadOnesMask"/>).</summary>
    private const byte ControlWritableMask = 0x1F;

    /// <summary>
    /// Bits 5-7 of CONTROL read back as 1: this adapter offers no
    /// bidirectional mode (*choice*, docs/PARALLEL-PORT.md section 1).
    /// </summary>
    private const byte ControlReadOnesMask = 0xE0;

    /// <summary>
    /// CONTROL's power-on value: /INIT not asserted (bit 2 set, the printer
    /// is not being reset) and SELECT IN asserted (bit 3 set), matching what
    /// the IBM BIOS's POST leaves LPT1 at (*choice*: docs/PARALLEL-PORT.md
    /// does not fix a reset value, only the bit meanings).
    /// </summary>
    private const byte DefaultControl = ControlInitialize | ControlSelectIn;

    /// <summary>
    /// An idle, ready printer's STATUS: /BUSY, SELECT and /ERROR asserted
    /// (bits 7, 4, 3), PE clear (no paper out), /ACK not mid-pulse (bit 6),
    /// bits 2-0 reading 1 (*choice*, docs/PARALLEL-PORT.md section 1). The
    /// emulated printer is never busy, so this is also the only value STATUS
    /// ever reads.
    /// </summary>
    private const byte StatusReady = 0xDF;

    /// <summary>LPT1's interrupt: IRQ 7, the AT Technical Reference's fixed assignment.</summary>
    private const int Irq = 7;

    /// <summary>Version one stores exactly DATA and CONTROL; see <see cref="Capture"/>.</summary>
    public const int SnapshotVersion = 1;

    /// <summary>Two bytes: DATA, then CONTROL.</summary>
    public const int SnapshotLength = 2;

    private readonly InterruptController _interrupts;
    private readonly IParallelPrinter _printer;

    private byte _data;
    private byte _control;

    public ParallelPort(InterruptController interrupts, IParallelPrinter printer)
    {
        _interrupts = interrupts;
        _printer = printer;
        Reset();
    }

    public void Register(IoBus io) => io.Register(DataPort, 3, this);

    /// <summary>Restores power-on register state. Does not touch the printer or the IRQ line.</summary>
    public void Reset()
    {
        _data = 0x00;
        _control = DefaultControl;
    }

    public byte ReadByte(ushort port) => port switch
    {
        DataPort => _data,
        StatusPort => StatusReady,
        ControlPort => (byte)((_control & ControlWritableMask) | ControlReadOnesMask),
        _ => 0xFF,
    };

    public void WriteByte(ushort port, byte value)
    {
        switch (port)
        {
            case DataPort:
                _data = value;
                return;

            case StatusPort:
                // Read only: a real STATUS register has nowhere for a write to go.
                return;

            case ControlPort:
                WriteControl((byte)(value & ControlWritableMask));
                return;
        }
    }

    /// <summary>
    /// Applies a new CONTROL value and fires the two edges the handshake
    /// depends on: STROBE falling delivers the byte in DATA to the printer,
    /// and /INIT falling resets it. Both are edges, not levels -- writing
    /// the same value twice in a row (STROBE already low, /INIT already
    /// high) does nothing a second time, exactly as toggling a real pin that
    /// was already where it is asked to go would not.
    /// </summary>
    private void WriteControl(byte next)
    {
        byte previous = _control;
        _control = next;

        bool strobeFell = (previous & ControlStrobe) != 0 && (next & ControlStrobe) == 0;
        if (strobeFell)
        {
            _printer.Receive(_data);

            // The printer's acknowledge pulse is instantaneous in this
            // model (docs/PARALLEL-PORT.md section 1: "the interrupt is
            // raised at once"), so the edge that would announce it is
            // raised here, gated by whether the guest asked to hear it.
            if ((next & ControlIrqEnable) != 0)
            {
                _interrupts.RaiseIrq(Irq);
            }
        }

        bool initializeFell = (previous & ControlInitialize) != 0 && (next & ControlInitialize) == 0;
        if (initializeFell)
        {
            _printer.Initialize();
        }
    }

    /// <summary>Captures DATA and CONTROL. A job in progress at the printer is not part of this section.</summary>
    public byte[] Capture() => [_data, _control];

    /// <summary>Checks the payload shape before any field is touched.</summary>
    public static void ValidateSnapshot(ReadOnlySpan<byte> snapshot)
    {
        if (snapshot.Length != SnapshotLength)
        {
            throw new InvalidDataException(
                $"LPT1 snapshot must be {SnapshotLength} bytes, was {snapshot.Length}.");
        }
    }

    /// <summary>
    /// Installs a validated capture. Deliberately does not call the printer
    /// or touch the IRQ line: docs/PARALLEL-PORT.md section 4 says restoring
    /// closes nothing in progress and starts the next byte on a fresh job,
    /// which is exactly what leaving <see cref="_printer"/> untouched gives.
    /// </summary>
    public void Restore(ReadOnlySpan<byte> snapshot)
    {
        ValidateSnapshot(snapshot);
        _data = snapshot[0];
        _control = (byte)(snapshot[1] & ControlWritableMask);
    }
}
