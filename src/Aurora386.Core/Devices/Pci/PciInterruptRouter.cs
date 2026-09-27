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

    /// <summary>
    /// The BIOS's own fixed choice of IRQ for each PIRQ link, applied by
    /// <see cref="RouteDefaults"/> every POST -- link A gets the first entry,
    /// D the last. A real BIOS's table comes from the board's wiring; this
    /// one has none to read, so it picks a stable assignment from the
    /// allowed set and keeps it across reboots, which is what lets a guest
    /// that never calls INT 1Ah function 0Fh -- most DOS software -- still
    /// find a working interrupt already routed the first time it looks.
    ///
    /// The four stay clear of every IRQ something else on this machine
    /// already owns: 3 and 5 are the default NE2000 and Sound Blaster 16
    /// lines, 12 the PS/2 mouse, 14 and 15 the two IDE channels. Links C and
    /// D share IRQs with A and B, which PCI's level-triggered interrupts
    /// allow; an earlier table that gave link C IRQ 5 put the S3 (device 4,
    /// pin A, link C) on the Sound Blaster's line.
    /// </summary>
    private static readonly byte[] DefaultLinkIrqs = [11, 10, 11, 10];

    private readonly InterruptController _pic;
    private readonly PciFunction _bridge;
    private readonly HashSet<uint> _asserted = [];

    /// <summary>
    /// The device ID <see cref="BuildRoutingTable()"/>'s compatible-router
    /// entry should name, read live from the ISA bridge function this router
    /// was built with: PIIX's 122Eh on an i430FX machine, PIIX4's 7110h on an
    /// i430TX one. Test case 9 of <c>plans/specs/pci-piix4.md</c> is this
    /// value changing (and the table's checksum with it), not any change to
    /// the link math itself.
    /// </summary>
    public ushort CompatibleRouterDeviceId => _bridge.DeviceId;

    public PciInterruptRouter(InterruptController pic, PciFunction bridge)
    {
        _pic = pic;
        _bridge = bridge;
        for (int link = 0; link < 4; link++) bridge.Define(0x60 + link, 0x80, 1, 0x8F);
        bridge.ConfigurationChanged += Update;
    }

    public static int Link(ushort address, byte pin)
    {
        // PIIX4's integrated USB INTA is wired to PIRQD, independently of
        // the slot rotation. The other southbridge functions use legacy IRQs.
        if (address == 0x000A && pin == 1)
        {
            return 3;
        }

        int device = (address & 255) >> 3;
        if ((address >> 8) != 0 || device is < 2 or > 5 || pin is < 1 or > 4) return -1;
        return (device - 2 + pin - 1) & 3;
    }

    public bool Route(ushort address, byte pin, byte irq)
    {
        int link = Link(address, pin);
        if (link < 0 || irq >= 16 || (AllowedIrqs & (1 << irq)) == 0) return false;
        SetLevelTriggered(irq);
        _bridge.Write(0x60 + link, 1, irq);
        return true;
    }

    private void SetLevelTriggered(byte irq)
    {
        ushort elcr = irq < 8 ? (ushort)0x4D0 : (ushort)0x4D1;
        _pic.WriteByte(elcr, (byte)(_pic.ReadByte(elcr) | (1 << (irq & 7))));
    }

    /// <summary>
    /// The BIOS's default routing, run every POST just as a real BIOS
    /// reprograms 60h-63h after every reset: every function that wired an
    /// interrupt pin (offset 3Dh non-zero) gets its link assigned a fixed
    /// IRQ and its Interrupt Line (offset 3Ch, scratch for the OS but set by
    /// convention to what the BIOS actually routed) written to match.
    ///
    /// A function outside the four PIRQ slots, or one whose pin register
    /// reads zero -- no interrupt pin wired, as the S3 and the ISA and IDE
    /// bridge functions all report -- is left alone.
    ///
    /// Each routed IRQ is also made level-triggered in the ELCR, as
    /// <see cref="Route"/> does and as a real BIOS does for the IRQs it hands
    /// to PCI: the links share IRQs 10 and 11, and on an edge-triggered line
    /// a second source asserting while the first still holds it would be
    /// lost. <c>Machine.RunPost</c> therefore resets the trigger modes
    /// before <see cref="PciBus.Reset"/>, not after.
    /// </summary>
    public void RouteDefaults(IEnumerable<PciFunction> functions)
    {
        foreach (PciFunction function in functions)
        {
            byte pin = (byte)function.Read(0x3D, 1);
            if (pin is < 1 or > 4) continue;

            int link = Link(function.Address, pin);
            if (link < 0) continue;

            byte irq = DefaultLinkIrqs[link];
            SetLevelTriggered(irq);
            _bridge.Write(0x60 + link, 1, irq);
            function.Write(0x3C, 1, irq);
        }
    }

    public void SetInterrupt(ushort address, byte pin, bool asserted)
    {
        if (Link(address, pin) < 0) throw new ArgumentOutOfRangeException(nameof(address));
        uint source = ((uint)address << 2) | (uint)(pin - 1);
        bool changed = asserted ? _asserted.Add(source) : _asserted.Remove(source);
        if (changed) Update();
    }

    /// <summary>
    /// Recomputes both what the legacy 8259 pair sees and what the I/O APIC
    /// sees, from the same set of asserted (device, pin) sources.
    ///
    /// The two are independent (coordinator ruling, item 4): the legacy
    /// aggregate only ever reflects a link whose 60h-63h routing register is
    /// enabled and names an allowed IRQ, exactly as before, but the four
    /// PIRQ pins on the I/O APIC (INTIN16-19) are wired straight to their
    /// links on real PIIX silicon, so a link with its routing disabled, or
    /// steered to an IRQ number the legacy side would refuse, still reaches
    /// the I/O APIC. <see cref="Devices.Interrupts.InterruptController.SetPirqLevels"/>
    /// is what turns this per-link mask into the matching pin state.
    /// </summary>
    private void Update()
    {
        ushort levels = 0;
        byte pirqLevels = 0;
        foreach (uint source in _asserted)
        {
            int link = Link((ushort)(source >> 2), (byte)((source & 3) + 1));
            pirqLevels |= (byte)(1 << link);

            byte route = (byte)_bridge.Read(0x60 + link, 1);
            if ((route & 0x80) == 0 && (AllowedIrqs & (1 << (route & 15))) != 0)
                levels |= (ushort)(1 << (route & 15));
        }
        _pic.SetPciLevels(levels);
        _pic.SetPirqLevels(pirqLevels);
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
        if (bytes.Length % 4 != 0) throw new InvalidDataException("Invalid PCI interrupt state.");
        var restored = new HashSet<uint>();
        for (int offset = 0; offset < bytes.Length; offset += 4)
        {
            uint source = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
            if (source > 0x3FFFF || Link((ushort)(source >> 2), (byte)((source & 3) + 1)) < 0 || !restored.Add(source))
                throw new InvalidDataException("Invalid PCI interrupt source.");
        }
        _asserted.Clear();
        _asserted.UnionWith(restored);
        Update();
    }

    /// <summary>
    /// The router ID used by callers building the default i430FX BIOS.
    /// </summary>
    private const ushort DefaultRouterDeviceId = 0x122E;

    /// <summary>The same slot wiring consumed by the OS and by SetInterrupt, for the default (i430FX/PIIX) router ID.</summary>
    public static byte[] BuildRoutingTable()
    {
        return BuildRoutingTable(DefaultRouterDeviceId);
    }

    /// <summary>
    /// The routing table for a specific compatible-router device ID -- see
    /// <see cref="CompatibleRouterDeviceId"/>. PIIX4 adds the motherboard
    /// USB function; the four expansion slots keep their existing wiring.
    /// </summary>
    public static byte[] BuildRoutingTable(ushort routerDeviceId)
    {
        bool integratedUsb = routerDeviceId == 0x7110;
        int entryCount = integratedUsb ? 5 : 4;
        byte[] table = new byte[32 + entryCount * 16];
        BinaryPrimitives.WriteUInt32LittleEndian(table, 0x52495024); // $PIR
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(4), 0x0100);
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(6), (ushort)table.Length);
        table[9] = 8; // router 00:01.0
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(12), 0x8086);
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(14), routerDeviceId);
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
        if (integratedUsb)
        {
            const int integratedEntry = 32 + 4 * 16;
            table[integratedEntry + 1] = 8; // Motherboard device 1, slot 0.
            table[integratedEntry + 2] = 0x63; // INTA -> PIRQD.
            BinaryPrimitives.WriteUInt16LittleEndian(
                table.AsSpan(integratedEntry + 3), AllowedIrqs);
        }

        table[31] = unchecked((byte)-table.Sum(value => value));
        return table;
    }
}
