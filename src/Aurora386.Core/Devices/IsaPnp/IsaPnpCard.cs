namespace Aurora386.Core.Devices.IsaPnp;

/// <summary>
/// One ISA Plug and Play card: a serial identifier that makes it unique on
/// the bus, resource data describing what it can be given, and one or more
/// logical devices with configuration registers of their own.
///
/// The card knows nothing about the protocol; <see cref="IsaPnpBus"/>
/// drives it through the states the specification names -- Wait for Key,
/// Sleep, Isolation, Config -- and calls back when a logical device is
/// activated with an address and interrupt, which is when the emulated
/// chip behind it moves.
/// </summary>
public sealed class IsaPnpCard
{
    public enum CardState { WaitForKey, Sleep, Isolation, Config }

    /// <summary>The 72-bit serial identifier: vendor id, serial number, checksum.</summary>
    public byte[] SerialIdentifier { get; }

    /// <summary>The resource data read after the identifier, tag by tag.</summary>
    public byte[] ResourceData { get; }

    public IReadOnlyList<IsaPnpLogicalDevice> LogicalDevices { get; }

    public CardState State { get; internal set; } = CardState.WaitForKey;

    /// <summary>The card select number, zero until the configuration software assigns one.</summary>
    public byte Csn { get; internal set; }

    internal int SerialBit;
    internal int ResourcePointer;
    internal int CurrentDevice;

    public IsaPnpCard(string vendorId, uint serialNumber, string description,
                      IReadOnlyList<IsaPnpLogicalDevice> logicalDevices)
    {
        LogicalDevices = logicalDevices;
        SerialIdentifier = BuildSerialIdentifier(vendorId, serialNumber);

        var resources = new ResourceWriter();
        resources.PnpVersion();
        resources.Identifier(description);
        foreach (IsaPnpLogicalDevice device in logicalDevices) device.WriteResources(resources);
        ResourceData = resources.End();
    }

    /// <summary>The identifier the configuration software reads bit by bit during isolation.</summary>
    internal int SerialBitAt(int index)
    {
        return (SerialIdentifier[index >> 3] >> (index & 7)) & 1;
    }

    /// <summary>
    /// The byte the Resource Data register hands out next: the nine bytes
    /// of the identifier first, then the resource data, then 0xFF forever.
    /// </summary>
    internal byte NextResourceByte()
    {
        int index = ResourcePointer++;
        if (index < SerialIdentifier.Length) return SerialIdentifier[index];
        index -= SerialIdentifier.Length;
        return index < ResourceData.Length ? ResourceData[index] : (byte)0xFF;
    }

    private static byte[] BuildSerialIdentifier(string vendorId, uint serialNumber)
    {
        byte[] id = new byte[9];
        CompressedId(vendorId).CopyTo(id, 0);
        BitConverter.GetBytes(serialNumber).CopyTo(id, 4);

        // The checksum is the state of the same feedback shift register the
        // initiation key comes from, after the 64 bits of identifier have
        // been fed through it, least significant bit of each byte first.
        byte checksum = 0x6A;
        for (int i = 0; i < 8; i++)
            for (int bit = 0; bit < 8; bit++)
            {
                int b = (id[i] >> bit) & 1;
                checksum = (byte)(((((checksum ^ (checksum >> 1)) & 1) ^ b) << 7) | (checksum >> 1));
            }
        id[8] = checksum;
        return id;
    }

    /// <summary>An EISA identifier in its four-byte compressed form.</summary>
    public static byte[] CompressedId(string id)
    {
        int a = id[0] - '@', b = id[1] - '@', c = id[2] - '@';
        int digits = Convert.ToInt32(id[3..7], 16);
        return
        [
            (byte)((a << 2) | (b >> 3)),
            (byte)(((b & 7) << 5) | c),
            (byte)(digits >> 8),
            (byte)digits,
        ];
    }
}

/// <summary>
/// One function of a card -- audio, game port, network -- with the
/// configuration registers of the specification: activate at 0x30, I/O
/// bases from 0x60, interrupts from 0x70, DMA channels from 0x74.
/// </summary>
public sealed class IsaPnpLogicalDevice
{
    public string Id { get; }
    public string? CompatibleId { get; }

    /// <summary>Registers 0x30 to 0x7F, indexed by register number.</summary>
    public byte[] Registers { get; } = new byte[0x80];

    // Inactive resource writes remain pending until activation. The attached
    // chip keeps the last configuration passed to its relocation callback.
    internal byte[] AppliedRegisters { get; } = new byte[0x80];

    private readonly Action<ResourceWriter> _describe;

    /// <summary>
    /// Called when the device is activated, deactivated, or reconfigured
    /// while active: the moment the chip behind it should move.
    /// </summary>
    public Action<IsaPnpLogicalDevice>? Configured { get; set; }

    /// <param name="describe">Writes the resources the device can take, in the tag format.</param>
    public IsaPnpLogicalDevice(string id, string? compatibleId, Action<ResourceWriter> describe)
    {
        Id = id;
        CompatibleId = compatibleId;
        _describe = describe;
    }

    public bool Active => (Registers[0x30] & 1) != 0;
    public ushort IoBase(int index) => (ushort)((Registers[0x60 + 2 * index] << 8) | Registers[0x61 + 2 * index]);
    public int Irq(int index) => Registers[0x70 + 2 * index];
    public int Dma(int index) => Registers[0x74 + index];

    /// <summary>Sets the power-up configuration: what the card answers at before anybody configures it.</summary>
    public void SetDefaults(bool active, ushort[] ioBases, int[] irqs, int[] dmas)
    {
        Array.Clear(Registers);
        Registers[0x30] = (byte)(active ? 1 : 0);
        for (int i = 0; i < ioBases.Length; i++)
        {
            Registers[0x60 + 2 * i] = (byte)(ioBases[i] >> 8);
            Registers[0x61 + 2 * i] = (byte)ioBases[i];
        }
        for (int i = 0; i < irqs.Length; i++)
        {
            Registers[0x70 + 2 * i] = (byte)irqs[i];
            Registers[0x71 + 2 * i] = 0x02;     // edge-triggered, active high: the ISA way
        }
        for (int i = 0; i < 2; i++) Registers[0x74 + i] = 4;   // channel 4 means "none"
        for (int i = 0; i < dmas.Length; i++) Registers[0x74 + i] = (byte)dmas[i];
        _defaults = (byte[])Registers.Clone();
        Registers.CopyTo(AppliedRegisters, 0);
    }

    private byte[]? _defaults;

    internal void Reset()
    {
        if (_defaults is not null) _defaults.CopyTo(Registers, 0);
        else Array.Clear(Registers);
    }

    /// <summary>Returns detached constructed defaults for legacy snapshot preflight.</summary>
    internal byte[] CaptureResetRegisters()
    {
        return _defaults is null ? new byte[Registers.Length] : (byte[])_defaults.Clone();
    }

    /// <summary>Records the resource image applied to the chip by a guest write.</summary>
    internal void NotifyConfigured()
    {
        Configured?.Invoke(this);
        Registers.CopyTo(AppliedRegisters, 0);
    }

    internal void WriteResources(ResourceWriter writer)
    {
        writer.LogicalDevice(Id);
        if (CompatibleId is not null) writer.CompatibleDevice(CompatibleId);
        _describe(writer);
    }
}

/// <summary>
/// Writes ISA Plug and Play resource data: small tags of up to seven
/// bytes, large tags with a two-byte length, an end tag with a checksum.
/// </summary>
public sealed class ResourceWriter
{
    private readonly List<byte> _bytes = new();

    public void PnpVersion() { _bytes.Add(0x0A); _bytes.Add(0x10); _bytes.Add(0x00); }

    public void Identifier(string text)
    {
        _bytes.Add(0x82);
        _bytes.Add((byte)text.Length); _bytes.Add((byte)(text.Length >> 8));
        foreach (char c in text) _bytes.Add((byte)c);
    }

    public void LogicalDevice(string id)
    {
        _bytes.Add(0x15);
        _bytes.AddRange(IsaPnpCard.CompressedId(id));
        _bytes.Add(0x00);                         // flags: no boot device, no commands
    }

    public void CompatibleDevice(string id)
    {
        _bytes.Add(0x1C);
        _bytes.AddRange(IsaPnpCard.CompressedId(id));
    }

    /// <summary>Interrupts the device could use, as a set of lines.</summary>
    public void Irq(params int[] lines)
    {
        int mask = 0;
        foreach (int line in lines) mask |= 1 << line;
        _bytes.Add(0x23);
        _bytes.Add((byte)mask); _bytes.Add((byte)(mask >> 8));
        _bytes.Add(0x01);                         // high true, edge sensitive
    }

    public void Dma(int[] channels, bool sixteenBit)
    {
        int mask = 0;
        foreach (int channel in channels) mask |= 1 << channel;
        _bytes.Add(0x2A);
        _bytes.Add((byte)mask);
        _bytes.Add((byte)(sixteenBit ? 0x02 : 0x00));   // transfer width: 16 or 8 bit
    }

    /// <summary>An I/O range the base can be placed in: from min to max in steps of align, of the given length.</summary>
    public void Io(ushort minimum, ushort maximum, byte alignment, byte length)
    {
        _bytes.Add(0x47);
        _bytes.Add(0x01);                         // 16-bit decode
        _bytes.Add((byte)minimum); _bytes.Add((byte)(minimum >> 8));
        _bytes.Add((byte)maximum); _bytes.Add((byte)(maximum >> 8));
        _bytes.Add(alignment);
        _bytes.Add(length);
    }

    public void FixedIo(ushort baseAddress, byte length)
    {
        _bytes.Add(0x4B);
        _bytes.Add((byte)baseAddress); _bytes.Add((byte)(baseAddress >> 8));
        _bytes.Add(length);
    }

    public byte[] End()
    {
        var block = new List<byte>(_bytes) { 0x79 };
        int sum = 0;
        foreach (byte b in block) sum += b;
        block.Add((byte)(-sum));
        return block.ToArray();
    }
}
