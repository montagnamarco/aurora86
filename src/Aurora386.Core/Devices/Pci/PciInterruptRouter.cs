using System.Buffers.Binary;
using Aurora386.Core.Devices.Interrupts;

namespace Aurora386.Core.Devices.Pci;

/// <summary>
/// PIIX PIRQ[A:D] routing at configuration offsets 60h-63h. Four virtual
/// slots on bus zero (devices 2-5) rotate their INTA-D wires across these
/// links. Sources are combined before driving the PIC: releasing one device
/// must not silence a second device sharing the same wire or destination.
/// </summary>
public sealed class PciInterruptRouter
{
    public const ushort AllowedIrqs = 0xDEF8;
    private readonly InterruptController _pic;
    private readonly PciFunction _bridge;
    private readonly HashSet<uint> _asserted = [];

    public PciInterruptRouter(InterruptController pic, PciFunction bridge)
    {
        _pic = pic;
        _bridge = bridge;
        for (int link = 0; link < 4; link++) bridge.Define(0x60 + link, 0x80, 1, 0x8F);
        bridge.ConfigurationChanged += Update;
    }

    public static int Link(ushort address, byte pin)
    {
        int device = (address & 255) >> 3;
        if ((address >> 8) != 0 || device is < 2 or > 5 || pin is < 1 or > 4) return -1;
        return (device - 2 + pin - 1) & 3;
    }

    public bool Route(ushort address, byte pin, byte irq)
    {
        int link = Link(address, pin);
        if (link < 0 || irq >= 16 || (AllowedIrqs & (1 << irq)) == 0) return false;
        ushort elcr = irq < 8 ? (ushort)0x4D0 : (ushort)0x4D1;
        _pic.WriteByte(elcr, (byte)(_pic.ReadByte(elcr) | (1 << (irq & 7))));
        _bridge.Write(0x60 + link, 1, irq);
        return true;
    }

    public void SetInterrupt(ushort address, byte pin, bool asserted)
    {
        if (Link(address, pin) < 0) throw new ArgumentOutOfRangeException(nameof(address));
        uint source = ((uint)address << 2) | (uint)(pin - 1);
        bool changed = asserted ? _asserted.Add(source) : _asserted.Remove(source);
        if (changed) Update();
    }

    private void Update()
    {
        ushort levels = 0;
        foreach (uint source in _asserted)
        {
            int link = Link((ushort)(source >> 2), (byte)((source & 3) + 1));
            byte route = (byte)_bridge.Read(0x60 + link, 1);
            if ((route & 0x80) == 0 && (AllowedIrqs & (1 << (route & 15))) != 0)
                levels |= (ushort)(1 << (route & 15));
        }
        _pic.SetPciLevels(levels);
    }

    public void ResetSources() { _asserted.Clear(); Update(); }
    public byte[] Capture()
    {
        byte[] bytes = new byte[_asserted.Count * 4];
        int offset = 0;
        foreach (uint source in _asserted.Order())
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), source);
            offset += 4;
        }
        return bytes;
    }
    public void Restore(ReadOnlySpan<byte> bytes)
    {
        HashSet<uint> restored = ParseSnapshot(bytes);

        _asserted.Clear();
        _asserted.UnionWith(restored);
        Update();
    }

    /// <summary>Validates asserted PCI sources without changing the router or interrupt controller.</summary>
    public static void ValidateSnapshot(ReadOnlySpan<byte> bytes)
    {
        _ = ParseSnapshot(bytes);
    }

    private static HashSet<uint> ParseSnapshot(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % sizeof(uint) != 0)
        {
            throw new InvalidDataException("Invalid PCI interrupt state.");
        }

        var restored = new HashSet<uint>();
        for (int offset = 0; offset < bytes.Length; offset += sizeof(uint))
        {
            uint source = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
            int link = Link((ushort)(source >> 2), (byte)((source & 3) + 1));
            if (source > 0x3FFFF || link < 0 || !restored.Add(source))
            {
                throw new InvalidDataException("Invalid PCI interrupt source.");
            }
        }

        return restored;
    }

    /// <summary>The same slot wiring consumed by the OS and by SetInterrupt.</summary>
    public static byte[] BuildRoutingTable()
    {
        byte[] table = new byte[32 + 4 * 16];
        BinaryPrimitives.WriteUInt32LittleEndian(table, 0x52495024); // $PIR
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(4), 0x0100);
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(6), (ushort)table.Length);
        table[9] = 8; // router 00:01.0
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(12), 0x8086);
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(14), 0x122E);
        for (int slot = 0; slot < 4; slot++)
        {
            int entry = 32 + slot * 16;
            table[entry + 1] = (byte)((slot + 2) << 3);
            table[entry + 14] = (byte)(slot + 1);
            for (byte pin = 1; pin <= 4; pin++)
            {
                int at = entry + 2 + (pin - 1) * 3;
                table[at] = (byte)(0x60 + Link((ushort)((slot + 2) << 3), pin));
                BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(at + 1), AllowedIrqs);
            }
        }
        table[31] = unchecked((byte)-table.Sum(b => b));
        return table;
    }
}
