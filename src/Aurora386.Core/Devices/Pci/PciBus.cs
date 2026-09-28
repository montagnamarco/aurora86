using Aurora386.Core.IO;

namespace Aurora386.Core.Devices.Pci;

/// <summary>
/// PCI configuration mechanism 1. CF8 is a DWORD-only address latch; CFC-CFF
/// are byte lanes into the selected function. A disabled cycle or absent
/// function reads as all ones. Configuration accesses never relocate the
/// address latch themselves, including accesses made through the PCI BIOS.
/// </summary>
public sealed class PciBus : IIoDwordDevice
{
    public PciInterruptRouter? InterruptRouter { get; set; }

    /// <summary>
    /// What a bus-master PCI function reaches to move bytes without going
    /// through the CPU. Null on a bus with no bus-master device fitted at
    /// all -- nothing needs it, and constructing a machine with none stays
    /// exactly as cheap as before this existed.
    /// </summary>
    public IDmaMemory? DmaMemory { get; set; }

    private uint _address;
    private readonly SortedDictionary<ushort, PciFunction> _functions = new();
    public IEnumerable<PciFunction> Functions => _functions.Values;
    public void Add(PciFunction function) => _functions.Add(function.Address, function);
    public void Register(IoBus io) => io.Register(0xCF8, 8, this);
    public uint ReadConfiguration(ushort function, int offset, int width)
        => _functions.TryGetValue(function, out var device) ? device.Read(offset, width)
            : width == 4 ? uint.MaxValue : (1u << (8 * width)) - 1;
    public void WriteConfiguration(ushort function, int offset, int width, uint value)
    {
        if (_functions.TryGetValue(function, out var device))
        {
            device.Write(offset, width, value);
        }
    }
    private bool Selected(ushort port, int width) => (_address & 0x80000000) != 0 &&
        port >= 0xCFC && port <= 0xD00 - width && (port & (width - 1)) == 0;
    private uint ReadData(ushort port, int width) => Selected(port, width)
        ? ReadConfiguration((ushort)(_address >> 8), (int)(_address & 0xFC) + port - 0xCFC, width)
        : width == 4 ? uint.MaxValue : (1u << (8 * width)) - 1;
    private void WriteData(ushort port, int width, uint value)
    {
        if (Selected(port, width))
            WriteConfiguration((ushort)(_address >> 8), (int)(_address & 0xFC) + port - 0xCFC, width, value);
    }
    public byte ReadByte(ushort port) => (byte)ReadData(port, 1);
    public ushort ReadWord(ushort port) => (ushort)ReadData(port, 2);
    public uint ReadDword(ushort port) => port == 0xCF8 ? _address : ReadData(port, 4);
    public void WriteByte(ushort port, byte value) => WriteData(port, 1, value);
    public void WriteWord(ushort port, ushort value) => WriteData(port, 2, value);
    public void WriteDword(ushort port, uint value)
    {
        if (port == 0xCF8)
        {
            _address = value & 0x80FFFFFC;
        }
        else WriteData(port, 4, value);
    }
    public void Reset()
    {
        InterruptRouter?.ResetSources();
        _address = 0;
        foreach (var function in Functions) function.Reset();

        // The BIOS's own default PCI interrupt routing, applied fresh every
        // POST: see PciInterruptRouter.RouteDefaults. Functions were just
        // reset above, so this always starts from the same Interrupt Line
        // reset value (unrouted, 0xFFh by PCI convention) rather than
        // compounding whatever a previous boot, or the guest itself, wrote.
        InterruptRouter?.RouteDefaults(Functions);
    }
    public byte[] Capture()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(_address);
        writer.Write(_functions.Count);
        foreach (var function in Functions)
        {
            writer.Write(function.Address);
            writer.Write(function.Capture());
        }
        if (InterruptRouter is not null)
        {
            byte[] interrupts = InterruptRouter.Capture();
            writer.Write(interrupts.Length);
            writer.Write(interrupts);
        }
        return stream.ToArray();
    }
    /// <summary>
    /// The function keys a captured "PCI " payload names, without applying
    /// any of it -- for checking a snapshot's topology against a machine's
    /// before <see cref="Restore"/> touches anything. Mirrors the header
    /// <see cref="Restore"/> itself reads (the address latch, then a key and
    /// a fixed 256-byte block per function), but is a plain read: it neither
    /// requires nor changes anything about this bus.
    /// </summary>
    public static IReadOnlyList<ushort> ReadFunctionKeys(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return [];
        }

        using var reader = new BinaryReader(new MemoryStream(data.ToArray()));
        reader.ReadUInt32();   // the address latch; irrelevant to topology
        int count = reader.ReadInt32();
        var keys = new List<ushort>(Math.Max(count, 0));
        for (int i = 0; i < count; i++)
        {
            keys.Add(reader.ReadUInt16());
            reader.ReadBytes(256);
        }
        return keys;
    }

    /// <summary>
    /// Parses and validates a complete PCI snapshot against this bus without
    /// changing configuration registers, interrupt sources, or device maps.
    /// </summary>
    internal SnapshotState ValidateSnapshot(ReadOnlySpan<byte> data)
    {
        return ParseSnapshot(data);
    }

    public void Restore(ReadOnlySpan<byte> data)
    {
        SnapshotState snapshot = ParseSnapshot(data);
        if (snapshot.IsLegacyReset)
        {
            Reset();
            return;
        }

        InterruptRouter?.ResetSources();
        foreach ((PciFunction function, byte[] state) in snapshot.Functions)
        {
            function.Restore(state);
        }

        _address = snapshot.Address & 0x80FFFFFC;
        InterruptRouter?.Restore(snapshot.InterruptState);
    }

    private SnapshotState ParseSnapshot(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return SnapshotState.LegacyReset;
        }

        using var reader = new BinaryReader(new MemoryStream(data.ToArray()));
        if (reader.BaseStream.Length < 2 * sizeof(int))
        {
            throw new InvalidDataException("PCI snapshot header is truncated.");
        }

        uint address = reader.ReadUInt32();
        int count = reader.ReadInt32();
        if (count != _functions.Count)
        {
            throw new InvalidDataException("PCI snapshot topology differs from the machine.");
        }

        var blocks = new List<(PciFunction Function, byte[] Data)>();
        for (int i = 0; i < count; i++)
        {
            const int functionBlockLength = sizeof(ushort) + 256;
            if (reader.BaseStream.Length - reader.BaseStream.Position < functionBlockLength)
            {
                throw new InvalidDataException("PCI snapshot function is truncated.");
            }

            ushort key = reader.ReadUInt16();
            byte[] state = reader.ReadBytes(256);
            if (!_functions.TryGetValue(key, out PciFunction? function)
                || state.Length != 256
                || blocks.Any(block => block.Function == function))
            {
                throw new InvalidDataException("Invalid PCI snapshot function.");
            }

            blocks.Add((function, state));
        }

        byte[] interruptState = [];
        if (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            if (reader.BaseStream.Length - reader.BaseStream.Position < sizeof(int))
            {
                throw new InvalidDataException("PCI interrupt snapshot length is truncated.");
            }

            int length = reader.ReadInt32();
            if (length < 0 || length != reader.BaseStream.Length - reader.BaseStream.Position)
            {
                throw new InvalidDataException("Invalid PCI interrupt snapshot length.");
            }

            interruptState = reader.ReadBytes(length);
        }

        if (InterruptRouter is null)
        {
            if (interruptState.Length > 0)
            {
                throw new InvalidDataException("PCI snapshot requires an interrupt router.");
            }
        }
        else
        {
            PciInterruptRouter.ValidateSnapshot(interruptState);
        }

        return new SnapshotState(address, blocks, interruptState, false);
    }

    /// <summary>A validated PCI snapshot, retained so restore does not parse a second time.</summary>
    internal sealed record SnapshotState(
        uint Address,
        IReadOnlyList<(PciFunction Function, byte[] Data)> Functions,
        byte[] InterruptState,
        bool IsLegacyReset)
    {
        internal static SnapshotState LegacyReset { get; } = new(0, [], [], true);

        internal ReadOnlySpan<byte> FunctionState(ushort address)
        {
            foreach ((PciFunction function, byte[] state) in Functions)
            {
                if (function.Address == address)
                {
                    return state;
                }
            }

            return [];
        }
    }
}
