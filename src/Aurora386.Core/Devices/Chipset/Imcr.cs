using Aurora386.Core.IO;

namespace Aurora386.Core.Devices.Chipset;

/// <summary>
/// The PIIX3/PIIX4 Interrupt Mode Configuration Register: two ports, an
/// index at 22h and data at 23h, present only on the i430TX/PIIX4 board
/// (coordinator decision A2 in plans/specs/apic.md).
///
/// Independent review, item 10: only index 70h is decoded at all. Port 22h
/// itself is write-only, reading back FFh always -- the same answer an
/// empty, unassigned bus gives, which is what this port was before this
/// package existed on every machine, i430TX included, and what a probe
/// distinguishing this chipset from a Cyrix CPU's own use of these same two
/// ports at 22h/23h needs to keep seeing. Port 23h reads and writes the IMCR
/// bit only while 70h is selected; every other index reads FFh too, and a
/// write to it is simply ignored -- there is nothing else behind this
/// window to model.
///
/// Bit 0 of the IMCR data byte is the whole of it: 0 (the power-on value)
/// wires the 8259 pair's INTR and NMI lines straight to the BSP -- PIC mode,
/// exactly what every machine before this package always did -- and 1
/// switches them to the I/O APIC instead. <see cref="Reset"/> clears it, as
/// the datasheet requires on INIT/RESET.
/// </summary>
public sealed class Imcr : IIoDevice
{
    private const ushort IndexPort = 0x22;
    private const ushort DataPort = 0x23;
    private const byte ImcrSelectIndex = 0x70;
    private const byte ApicModeBit = 0x01;

    /// <summary>What an unmodelled index, and the index port itself, both read as -- an idle bus (item 10).</summary>
    private const byte UnassignedByte = 0xFF;

    private byte _selectedIndex;

    /// <summary>True once the guest has written bit 0 of the IMCR data byte, meaning the 8259 pair no longer drives the CPU directly.</summary>
    public bool ApicMode { get; private set; }

    /// <summary>Raised whenever <see cref="ApicMode"/> changes, including on <see cref="Reset"/>. Machine wires this straight to the local APIC's own <c>ImcrApicMode</c> property.</summary>
    public event Action<bool>? ApicModeChanged;

    public void Register(IoBus io)
    {
        io.Register(IndexPort, this);
        io.Register(DataPort, this);
    }

    public void Reset()
    {
        _selectedIndex = 0;
        SetApicMode(false);
    }

    public byte ReadByte(ushort port)
    {
        if (port == IndexPort) return UnassignedByte;
        return _selectedIndex == ImcrSelectIndex ? (byte)(ApicMode ? 1 : 0) : UnassignedByte;
    }

    public void WriteByte(ushort port, byte value)
    {
        if (port == IndexPort)
        {
            _selectedIndex = value;
            return;
        }

        // Every index but 70h is unmodelled and simply ignored: there is
        // nothing behind it to remember (item 10).
        if (_selectedIndex != ImcrSelectIndex) return;

        SetApicMode((value & ApicModeBit) != 0);
    }

    private void SetApicMode(bool apicMode)
    {
        if (ApicMode == apicMode) return;
        ApicMode = apicMode;
        ApicModeChanged?.Invoke(apicMode);
    }

    /// <summary>The single byte a snapshot needs: <see cref="ApicMode"/>.</summary>
    public const int SnapshotLength = 1;

    public byte[] Capture() => [(byte)(ApicMode ? 1 : 0)];

    public void Restore(ReadOnlySpan<byte> state)
    {
        if (state.Length != SnapshotLength) throw new InvalidDataException("Invalid IMCR state length.");
        SetApicMode(state[0] != 0);
    }
}
