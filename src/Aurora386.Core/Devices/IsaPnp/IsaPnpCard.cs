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

    /// <summary>Four bytes of compressed vendor id, four of serial number, one of checksum.</summary>
    private const int SerialIdentifierLength = 9;

    private const int VendorIdOffset = 0;
    private const int SerialNumberOffset = 4;
    private const int ChecksumOffset = 8;

    /// <summary>The bytes the checksum covers: vendor id and serial number.</summary>
    private const int ChecksummedBytes = 8;

    /// <summary>The initial state of the linear feedback shift register the initiation key comes from.</summary>
    private const byte ChecksumSeed = 0x6A;

    /// <summary>What the Resource Data register returns once everything has been read.</summary>
    private const byte EndOfResources = 0xFF;

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
        foreach (IsaPnpLogicalDevice device in logicalDevices)
        {
            device.WriteResources(resources);
        }

        ResourceData = resources.End();
    }

    /// <summary>The identifier the configuration software reads bit by bit during isolation.</summary>
    internal int SerialBitAt(int index)
    {
        int byteIndex = index >> 3;
        int bitIndex = index & 7;
        return (SerialIdentifier[byteIndex] >> bitIndex) & 1;
    }

    /// <summary>
    /// The byte the Resource Data register hands out next: the nine bytes
    /// of the identifier first, then the resource data, then 0xFF forever.
    /// </summary>
    internal byte NextResourceByte()
    {
        int index = ResourcePointer;
        ResourcePointer++;
        if (index < SerialIdentifier.Length)
        {
            return SerialIdentifier[index];
        }

        index -= SerialIdentifier.Length;
        return index < ResourceData.Length ? ResourceData[index] : EndOfResources;
    }

    private static byte[] BuildSerialIdentifier(string vendorId, uint serialNumber)
    {
        byte[] identifier = new byte[SerialIdentifierLength];
        CompressedId(vendorId).CopyTo(identifier, VendorIdOffset);
        BitConverter.GetBytes(serialNumber).CopyTo(identifier, SerialNumberOffset);

        // The checksum is the state of the same feedback shift register the
        // initiation key comes from, after the 64 bits of identifier have
        // been fed through it, least significant bit of each byte first.
        byte checksum = ChecksumSeed;
        for (int i = 0; i < ChecksummedBytes; i++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                int input = (identifier[i] >> bit) & 1;
                int feedback = ((checksum ^ (checksum >> 1)) & 1) ^ input;
                checksum = (byte)((feedback << 7) | (checksum >> 1));
            }
        }

        identifier[ChecksumOffset] = checksum;
        return identifier;
    }

    /// <summary>
    /// An EISA identifier in its four-byte compressed form: three letters of
    /// five bits each ('A' = 1) packed into the first two bytes, then four
    /// hexadecimal digits of product number and revision.
    /// </summary>
    public static byte[] CompressedId(string id)
    {
        int first = id[0] - '@';
        int second = id[1] - '@';
        int third = id[2] - '@';
        int digits = Convert.ToInt32(id[3..7], 16);
        return
        [
            (byte)((first << 2) | (second >> 3)),
            (byte)(((second & 7) << 5) | third),
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
    private const int ActivateRegister = 0x30;
    private const byte ActivateBit = 0x01;

    /// <summary>I/O base descriptors: two bytes each, high byte first.</summary>
    private const int IoBaseRegister = 0x60;

    /// <summary>Interrupt descriptors: the line, then its type, two bytes each.</summary>
    private const int IrqRegister = 0x70;
    private const int IrqTypeRegister = 0x71;

    /// <summary>Type register value for an edge-triggered, active-high line: the ISA way.</summary>
    private const byte IrqTypeEdgeHigh = 0x02;

    private const int DmaRegister = 0x74;
    private const int DmaDescriptorCount = 2;

    /// <summary>DMA channel 4 is the cascade, so the specification uses it to mean "none".</summary>
    private const byte NoDmaChannel = 4;

    private const int RegisterFileSize = 0x80;

    public string Id { get; }
    public string? CompatibleId { get; }

    /// <summary>Registers 0x30 to 0x7F, indexed by register number.</summary>
    public byte[] Registers { get; } = new byte[RegisterFileSize];

    private readonly Action<ResourceWriter> _describe;
    private byte[]? _defaults;

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

    public bool Active => (Registers[ActivateRegister] & ActivateBit) != 0;

    public ushort IoBase(int index)
    {
        int high = Registers[IoBaseRegister + 2 * index];
        int low = Registers[IoBaseRegister + 2 * index + 1];
        return (ushort)((high << 8) | low);
    }

    public int Irq(int index) => Registers[IrqRegister + 2 * index];

    public int Dma(int index) => Registers[DmaRegister + index];

    /// <summary>Sets the power-up configuration: what the card answers at before anybody configures it.</summary>
    public void SetDefaults(bool active, ushort[] ioBases, int[] irqs, int[] dmas)
    {
        Array.Clear(Registers);
        Registers[ActivateRegister] = active ? ActivateBit : (byte)0;
        for (int i = 0; i < ioBases.Length; i++)
        {
            Registers[IoBaseRegister + 2 * i] = (byte)(ioBases[i] >> 8);
            Registers[IoBaseRegister + 2 * i + 1] = (byte)ioBases[i];
        }

        for (int i = 0; i < irqs.Length; i++)
        {
            Registers[IrqRegister + 2 * i] = (byte)irqs[i];
            Registers[IrqTypeRegister + 2 * i] = IrqTypeEdgeHigh;
        }

        for (int i = 0; i < DmaDescriptorCount; i++)
        {
            Registers[DmaRegister + i] = NoDmaChannel;
        }

        for (int i = 0; i < dmas.Length; i++)
        {
            Registers[DmaRegister + i] = (byte)dmas[i];
        }

        _defaults = (byte[])Registers.Clone();
    }

    internal void Reset()
    {
        if (_defaults is not null)
        {
            _defaults.CopyTo(Registers, 0);
        }
        else
        {
            Array.Clear(Registers);
        }
    }

    internal void WriteResources(ResourceWriter writer)
    {
        writer.LogicalDevice(Id);
        if (CompatibleId is not null)
        {
            writer.CompatibleDevice(CompatibleId);
        }

        _describe(writer);
    }
}

/// <summary>
/// Writes ISA Plug and Play resource data: small tags of up to seven
/// bytes, large tags with a two-byte length, an end tag with a checksum.
/// A small tag's byte is its item name shifted left by three, ORed with its
/// length.
/// </summary>
public sealed class ResourceWriter
{
    private const byte PnpVersionTag = 0x0A;          // small item 1, length 2
    private const byte PnpVersionOnePointZero = 0x10;  // BCD 1.0
    private const byte VendorVersion = 0x00;
    private const byte IdentifierAnsiTag = 0x82;       // large item 2: identifier string (ANSI)
    private const byte LogicalDeviceIdTag = 0x15;      // small item 2, length 5
    private const byte LogicalDeviceFlagsNone = 0x00;  // no boot device, no optional commands
    private const byte CompatibleDeviceIdTag = 0x1C;   // small item 3, length 4
    private const byte IrqFormatTag = 0x23;            // small item 4, length 3
    private const byte IrqHighTrueEdge = 0x01;
    private const byte DmaFormatTag = 0x2A;            // small item 5, length 2
    private const byte DmaSixteenBitOnly = 0x02;
    private const byte DmaEightBitOnly = 0x00;
    private const byte IoPortTag = 0x47;               // small item 8, length 7
    private const byte IoSixteenBitDecode = 0x01;
    private const byte FixedIoPortTag = 0x4B;          // small item 9, length 3
    private const byte EndTag = 0x79;                  // small item 15, length 1

    private readonly List<byte> _bytes = new();

    public void PnpVersion()
    {
        _bytes.Add(PnpVersionTag);
        _bytes.Add(PnpVersionOnePointZero);
        _bytes.Add(VendorVersion);
    }

    public void Identifier(string text)
    {
        _bytes.Add(IdentifierAnsiTag);
        AddWord(text.Length);
        foreach (char c in text)
        {
            _bytes.Add((byte)c);
        }
    }

    public void LogicalDevice(string id)
    {
        _bytes.Add(LogicalDeviceIdTag);
        _bytes.AddRange(IsaPnpCard.CompressedId(id));
        _bytes.Add(LogicalDeviceFlagsNone);
    }

    public void CompatibleDevice(string id)
    {
        _bytes.Add(CompatibleDeviceIdTag);
        _bytes.AddRange(IsaPnpCard.CompressedId(id));
    }

    /// <summary>Interrupts the device could use, as a set of lines.</summary>
    public void Irq(params int[] lines)
    {
        int mask = 0;
        foreach (int line in lines)
        {
            mask |= 1 << line;
        }

        _bytes.Add(IrqFormatTag);
        AddWord(mask);
        _bytes.Add(IrqHighTrueEdge);
    }

    public void Dma(int[] channels, bool sixteenBit)
    {
        int mask = 0;
        foreach (int channel in channels)
        {
            mask |= 1 << channel;
        }

        _bytes.Add(DmaFormatTag);
        _bytes.Add((byte)mask);
        _bytes.Add(sixteenBit ? DmaSixteenBitOnly : DmaEightBitOnly);
    }

    /// <summary>An I/O range the base can be placed in: from min to max in steps of align, of the given length.</summary>
    public void Io(ushort minimum, ushort maximum, byte alignment, byte length)
    {
        _bytes.Add(IoPortTag);
        _bytes.Add(IoSixteenBitDecode);
        AddWord(minimum);
        AddWord(maximum);
        _bytes.Add(alignment);
        _bytes.Add(length);
    }

    public void FixedIo(ushort baseAddress, byte length)
    {
        _bytes.Add(FixedIoPortTag);
        AddWord(baseAddress);
        _bytes.Add(length);
    }

    /// <summary>
    /// Closes the data with the end tag and a checksum byte that makes the
    /// sum of every byte, the checksum included, zero modulo 256.
    /// </summary>
    public byte[] End()
    {
        var block = new List<byte>(_bytes) { EndTag };
        int sum = 0;
        foreach (byte value in block)
        {
            sum += value;
        }

        block.Add((byte)(-sum));
        return block.ToArray();
    }

    /// <summary>Appends a 16-bit value low byte first, as every multi-byte resource field is stored.</summary>
    private void AddWord(int value)
    {
        _bytes.Add((byte)value);
        _bytes.Add((byte)(value >> 8));
    }
}
