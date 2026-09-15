using System.Text;

namespace Aurora386.Core.Devices.IsaPnp;

/// <summary>
/// Saves the 1990s ISA PnP configuration protocol between driver port accesses.
/// Restore changes raw latches and READ_DATA mapping without configuring chips.
/// </summary>
public sealed partial class IsaPnpBus
{
    private const uint SnapshotMagic = 0x31504E50; // "PNP1"
    private const ushort SnapshotVersion = 1;
    private const int MaximumSnapshotCards = 255;
    private const int MaximumSnapshotLogicalDevices = 256;
    private const int MaximumSnapshotResourceLength = 1024 * 1024;
    private const int ConfigurationRegisterCount = 0x80;
    private const int SerialIdentifierLength = 9;
    private const int SerialIdentifierBits = SerialIdentifierLength * 8;

    /// <summary>Captures all attached cards and the bus's partial protocol state.</summary>
    public byte[] Capture()
    {
        return CaptureCore(resetState: false);
    }

    /// <summary>Captures constructed defaults for legacy restore preflight without mutation.</summary>
    public byte[] CaptureResetSnapshot()
    {
        return CaptureCore(resetState: true);
    }

    private byte[] CaptureCore(bool resetState)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(SnapshotMagic);
        writer.Write(SnapshotVersion);
        writer.Write((ushort)_cards.Count);
        writer.Write(resetState ? DefaultReadDataPort : _readDataPort);
        writer.Write(resetState ? 0 : _keyPosition);
        writer.Write(resetState ? (byte)0 : _address);
        writer.Write(resetState ? (byte)0 : (byte)_isolationPhase);
        writer.Write(resetState ? 0 : _isolationBit);
        writer.Write((byte)(!resetState && _isolationAnyOne ? 1 : 0));

        foreach (IsaPnpCard card in _cards)
        {
            writer.Write(card.SerialIdentifier);
            writer.Write(card.ResourceData.Length);
            writer.Write(card.ResourceData);
            writer.Write(resetState ? (byte)0 : card.Csn);
            writer.Write((byte)(resetState ? IsaPnpCard.CardState.WaitForKey : card.State));
            writer.Write(resetState ? 0 : card.SerialBit);
            writer.Write(resetState ? 0 : card.ResourcePointer);
            writer.Write(resetState ? 0 : card.CurrentDevice);
            writer.Write((ushort)card.LogicalDevices.Count);
            foreach (IsaPnpLogicalDevice device in card.LogicalDevices)
            {
                writer.Write(Encoding.ASCII.GetBytes(device.Id));
                writer.Write((byte)(device.CompatibleId is null ? 0 : 1));
                if (device.CompatibleId is not null)
                {
                    writer.Write(Encoding.ASCII.GetBytes(device.CompatibleId));
                }

                if (resetState)
                {
                    byte[] defaults = device.CaptureResetRegisters();
                    writer.Write(defaults);
                    writer.Write(defaults);
                }
                else
                {
                    writer.Write(device.Registers);
                    writer.Write(device.AppliedRegisters);
                }
            }
        }

        writer.Flush();
        byte[] data = stream.ToArray();
        ValidateSnapshot(data);
        return data;
    }

    /// <summary>Checks the format and raw field ranges without touching a bus.</summary>
    public static void ValidateSnapshot(byte[] data)
    {
        ParseSnapshot(data);
    }

    /// <summary>Checks that every captured card and logical device matches this bus.</summary>
    public void ValidateSnapshotForRestore(byte[] data)
    {
        ValidateTopology(ParseSnapshot(data));
    }

    /// <summary>Returns the saved READ_DATA address for machine mapping preflight.</summary>
    public static ushort GetSnapshotReadDataPort(byte[] data)
    {
        return ParseSnapshot(data).ReadDataPort;
    }

    /// <summary>
    /// Returns pending and last-applied resource images for machine preflight.
    /// Inactive writes can differ from the chip's last relocation configuration.
    /// </summary>
    public static IReadOnlyList<LogicalDeviceSnapshot> GetSnapshotLogicalDevices(byte[] data)
    {
        BusSnapshot snapshot = ParseSnapshot(data);
        var devices = new List<LogicalDeviceSnapshot>();
        for (int cardIndex = 0; cardIndex < snapshot.Cards.Length; cardIndex++)
        {
            devices.AddRange(snapshot.Cards[cardIndex].Devices);
        }

        return devices;
    }

    /// <summary>Removes the old READ_DATA mapping before a machine resource swap.</summary>
    public void DetachSnapshotPortMapping()
    {
        _io?.Unregister(_readDataPort, 1, this);
        _snapshotPortMappingDetached = true;
    }

    /// <summary>Reinstalls the current READ_DATA mapping without guest port writes.</summary>
    public void RebuildSnapshotPortMapping()
    {
        _io?.Register(_readDataPort, this);
        _snapshotPortMappingDetached = false;
    }

    /// <summary>
    /// Validates completely, then installs raw state without configuration
    /// callbacks, guest I/O, interrupt edges or resource cursor advancement.
    /// </summary>
    public void Restore(byte[] data)
    {
        BusSnapshot snapshot = ParseSnapshot(data);
        ValidateTopology(snapshot);

        _keyPosition = snapshot.KeyPosition;
        _address = snapshot.Address;
        _isolationPhase = snapshot.IsolationPhase;
        _isolationBit = snapshot.IsolationBit;
        _isolationAnyOne = snapshot.IsolationAnyOne;
        for (int cardIndex = 0; cardIndex < _cards.Count; cardIndex++)
        {
            IsaPnpCard card = _cards[cardIndex];
            CardSnapshot savedCard = snapshot.Cards[cardIndex];
            card.Csn = savedCard.Csn;
            card.State = savedCard.State;
            card.SerialBit = savedCard.SerialBit;
            card.ResourcePointer = savedCard.ResourcePointer;
            card.CurrentDevice = savedCard.CurrentDevice;
            for (int deviceIndex = 0; deviceIndex < card.LogicalDevices.Count; deviceIndex++)
            {
                IsaPnpLogicalDevice device = card.LogicalDevices[deviceIndex];
                LogicalDeviceSnapshot savedDevice = savedCard.Devices[deviceIndex];
                savedDevice.Registers.CopyTo(device.Registers, 0);
                savedDevice.AppliedRegisters.CopyTo(device.AppliedRegisters, 0);
            }
        }

        MoveReadDataPort(snapshot.ReadDataPort);
    }

    /// <summary>Restores constructed card defaults without invoking chip callbacks.</summary>
    public void ResetSnapshotState()
    {
        _keyPosition = 0;
        _address = 0;
        _isolationPhase = 0;
        _isolationBit = 0;
        _isolationAnyOne = false;
        foreach (IsaPnpCard card in _cards)
        {
            card.Csn = 0;
            card.State = IsaPnpCard.CardState.WaitForKey;
            card.SerialBit = 0;
            card.ResourcePointer = 0;
            card.CurrentDevice = 0;
            foreach (IsaPnpLogicalDevice device in card.LogicalDevices)
            {
                device.Reset();
                device.Registers.CopyTo(device.AppliedRegisters, 0);
            }
        }

        MoveReadDataPort(DefaultReadDataPort);
    }

    private void ValidateTopology(BusSnapshot snapshot)
    {
        if (snapshot.Cards.Length != _cards.Count)
        {
            throw new InvalidDataException("ISA PnP snapshot card topology does not match the target.");
        }

        for (int cardIndex = 0; cardIndex < _cards.Count; cardIndex++)
        {
            IsaPnpCard card = _cards[cardIndex];
            CardSnapshot savedCard = snapshot.Cards[cardIndex];
            if (!savedCard.SerialIdentifier.AsSpan().SequenceEqual(card.SerialIdentifier)
                || !savedCard.ResourceData.AsSpan().SequenceEqual(card.ResourceData)
                || savedCard.Devices.Length != card.LogicalDevices.Count)
            {
                throw new InvalidDataException("ISA PnP snapshot card identity does not match the target.");
            }

            for (int deviceIndex = 0; deviceIndex < card.LogicalDevices.Count; deviceIndex++)
            {
                IsaPnpLogicalDevice device = card.LogicalDevices[deviceIndex];
                LogicalDeviceSnapshot savedDevice = savedCard.Devices[deviceIndex];
                if (savedDevice.Id != device.Id || savedDevice.CompatibleId != device.CompatibleId)
                {
                    throw new InvalidDataException("ISA PnP snapshot logical device identity does not match the target.");
                }
            }
        }
    }

    private static BusSnapshot ParseSnapshot(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var stream = new MemoryStream(data, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        try
        {
            if (reader.ReadUInt32() != SnapshotMagic || reader.ReadUInt16() != SnapshotVersion)
            {
                throw new InvalidDataException("ISA PnP snapshot header is invalid.");
            }

            int cardCount = reader.ReadUInt16();
            if (cardCount > MaximumSnapshotCards)
            {
                throw new InvalidDataException("ISA PnP snapshot card count is invalid.");
            }

            ushort readDataPort = reader.ReadUInt16();
            int keyPosition = reader.ReadInt32();
            byte address = reader.ReadByte();
            int isolationPhase = reader.ReadByte();
            int isolationBit = reader.ReadInt32();
            bool isolationAnyOne = ReadBoolean(reader);
            // The existing port model accepts any encoded ten-bit address;
            // machine integration separately rejects supported resource conflicts.
            if (readDataPort > 0x3FF || (readDataPort & 3) != 3
                || keyPosition < 0 || keyPosition >= InitiationKey.Length
                || isolationPhase > 1 || isolationBit < 0 || isolationBit > SerialIdentifierBits
                || (isolationBit == SerialIdentifierBits && isolationPhase != 0))
            {
                throw new InvalidDataException("ISA PnP snapshot bus state is invalid.");
            }

            var cards = new CardSnapshot[cardCount];
            for (int cardIndex = 0; cardIndex < cardCount; cardIndex++)
            {
                byte[] serialIdentifier = ReadBytes(reader, SerialIdentifierLength);
                int resourceLength = reader.ReadInt32();
                if (resourceLength < 0 || resourceLength > MaximumSnapshotResourceLength)
                {
                    throw new InvalidDataException("ISA PnP snapshot resource length is invalid.");
                }

                byte[] resourceData = ReadBytes(reader, resourceLength);
                byte csn = reader.ReadByte();
                var state = (IsaPnpCard.CardState)reader.ReadByte();
                int serialBit = reader.ReadInt32();
                int resourcePointer = reader.ReadInt32();
                int currentDevice = reader.ReadInt32();
                int deviceCount = reader.ReadUInt16();
                if (!Enum.IsDefined(state) || serialBit < 0 || serialBit > SerialIdentifierBits
                    || resourcePointer < 0 || deviceCount > MaximumSnapshotLogicalDevices
                    || currentDevice < 0 || (deviceCount == 0 ? currentDevice != 0 : currentDevice >= deviceCount))
                {
                    throw new InvalidDataException("ISA PnP snapshot card state is invalid.");
                }

                var devices = new LogicalDeviceSnapshot[deviceCount];
                for (int deviceIndex = 0; deviceIndex < deviceCount; deviceIndex++)
                {
                    string id = ReadId(reader);
                    string? compatibleId = ReadBoolean(reader) ? ReadId(reader) : null;
                    byte[] registers = ReadBytes(reader, ConfigurationRegisterCount);
                    byte[] appliedRegisters = ReadBytes(reader, ConfigurationRegisterCount);
                    if (registers[0x30] > 1 || appliedRegisters[0x30] > 1)
                    {
                        throw new InvalidDataException("ISA PnP snapshot activation register 30 is invalid.");
                    }

                    devices[deviceIndex] = new LogicalDeviceSnapshot(
                        cardIndex, deviceIndex, id, compatibleId, registers, appliedRegisters);
                }

                cards[cardIndex] = new CardSnapshot(serialIdentifier, resourceData, csn, state,
                    serialBit, resourcePointer, currentDevice, devices);
            }

            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException("ISA PnP snapshot contains trailing data.");
            }

            return new BusSnapshot(readDataPort, keyPosition, address, isolationPhase,
                isolationBit, isolationAnyOne, cards);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("ISA PnP snapshot is truncated.", exception);
        }
    }

    private static byte[] ReadBytes(BinaryReader reader, int length)
    {
        if (length > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new InvalidDataException("ISA PnP snapshot is truncated.");
        }

        return reader.ReadBytes(length);
    }

    private static bool ReadBoolean(BinaryReader reader)
    {
        byte value = reader.ReadByte();
        if (value > 1)
        {
            throw new InvalidDataException("ISA PnP snapshot boolean is invalid.");
        }

        return value != 0;
    }

    private static string ReadId(BinaryReader reader)
    {
        byte[] bytes = ReadBytes(reader, 7);
        for (int index = 0; index < bytes.Length; index++)
        {
            bool valid = index < 3
                ? bytes[index] is >= (byte)'A' and <= (byte)'Z'
                : bytes[index] is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'F';
            if (!valid)
            {
                throw new InvalidDataException("ISA PnP snapshot logical device identifier is invalid.");
            }
        }

        return Encoding.ASCII.GetString(bytes);
    }

    /// <summary>
    /// A detached configuration image for one ISA PnP function. Machine restore
    /// uses the applied image to preflight chip resources without guest writes.
    /// </summary>
    public sealed record LogicalDeviceSnapshot(
        int CardIndex,
        int DeviceIndex,
        string Id,
        string? CompatibleId,
        byte[] Registers,
        byte[] AppliedRegisters)
    {
        public bool Active => (AppliedRegisters[0x30] & 1) != 0;
        public ushort IoBase(int index) => (ushort)((AppliedRegisters[0x60 + index * 2] << 8)
            | AppliedRegisters[0x61 + index * 2]);
        public int Irq(int index) => AppliedRegisters[0x70 + index * 2];
        public int Dma(int index) => AppliedRegisters[0x74 + index];
    }

    /// <summary>Validated raw bus latches owned by the ISA PnP protocol.</summary>
    private sealed record BusSnapshot(ushort ReadDataPort, int KeyPosition, byte Address,
        int IsolationPhase, int IsolationBit, bool IsolationAnyOne, CardSnapshot[] Cards);

    /// <summary>Validated identity and cursor state for one attached PnP card.</summary>
    private sealed record CardSnapshot(byte[] SerialIdentifier, byte[] ResourceData,
        byte Csn, IsaPnpCard.CardState State, int SerialBit, int ResourcePointer,
        int CurrentDevice, LogicalDeviceSnapshot[] Devices);
}
