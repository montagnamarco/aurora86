using CpuNs = Aurora386.Core.Cpu;

namespace Aurora386.Core.Firmware;

/// <summary>
/// The Plug and Play BIOS (specification 1.0A): a list of what is on the
/// system board and which resources each piece uses, handed out through a
/// far-called entry point.
///
/// Windows 95 asks this first. Without it, it has to find the timer, the
/// interrupt controllers, the keyboard and the IDE ports by probing, which
/// is slow and occasionally wrong; with it, the Device Manager shows every
/// device on the board with the resources listed here and no exclamation
/// marks. Nothing here can be configured -- the addresses are the ones the
/// emulated chips answer at -- so every node says so, and a request to
/// change one is accepted and ignored, which is what a BIOS does for a
/// device soldered to the board.
///
/// The entry point takes its arguments on the stack in the C convention:
/// the function number first, then the rest, with the caller cleaning up.
/// Far pointers arrive as offset then selector, and the selector is a real
/// segment or a protected-mode descriptor depending on who is calling; the
/// processor resolves either.
/// </summary>
public sealed class PnpBios
{
    private const ushort Success = 0x00;
    private const ushort UnknownFunction = 0x81;
    private const ushort FunctionNotSupported = 0x82;
    private const ushort InvalidHandle = 0x83;
    private const ushort SystemNotDocked = 0x87;
    private const ushort GetNumberOfNodesFunction = 0x00;
    private const ushort GetNodeFunction = 0x01;
    private const ushort SetNodeFunction = 0x02;
    private const ushort GetEventFunction = 0x03;
    private const ushort SendMessageFunction = 0x04;
    private const ushort GetDockingInformationFunction = 0x05;
    private const ushort SetStaticResourcesFunction = 0x09;
    private const ushort GetStaticResourcesFunction = 0x0A;
    private const ushort GetIsaConfigurationFunction = 0x40;
    private const uint ReturnAddressSize = 4;
    private const uint ArgumentSize = 2;
    private const byte LastNodeHandle = 0xFF;

    // Node attributes specified by the Plug and Play BIOS 1.0A layout.
    private const ushort CannotDisable = 0x0001;
    private const ushort CannotConfigure = 0x0002;
    private const ushort InputDevice = 0x0010;

    /// <summary>The read data port ISA Plug and Play cards answer on.</summary>
    public const ushort IsaReadDataPort = Devices.IsaPnp.IsaPnpBus.DefaultReadDataPort;

    private readonly Machine _machine;
    private readonly List<byte[]> _nodes;

    public PnpBios(Machine machine)
    {
        _machine = machine;
        _nodes = BuildNodes();
    }

    /// <summary>The device nodes as the guest sees them, one byte array each, for tests and the debugger.</summary>
    public IReadOnlyList<byte[]> Nodes => _nodes;

    // --- the entry point --------------------------------------------------------

    /// <summary>Dispatches a far-called Plug and Play BIOS function from the guest stack.</summary>
    public void Handle(CpuNs.Cpu cpu)
    {
        // The far call left four bytes of return address on the stack; the
        // arguments start above them, function number first.
        uint sp = cpu.State.Ss.Big ? cpu.State.Esp : cpu.State.Sp;
        ushort ReadArgument(int index)
        {
            uint address = sp + ReturnAddressSize + ArgumentSize * (uint)index;
            return cpu.ReadMemoryWord(CpuNs.SegReg.Ss, address);
        }

        uint ReadFarPointer(int index)
        {
            ushort segmentOrSelector = ReadArgument(index + 1);
            ushort offset = ReadArgument(index);
            return cpu.FarPointerToLinear(segmentOrSelector, offset);
        }

        ushort function = ReadArgument(0);
        switch (function)
        {
            case GetNumberOfNodesFunction:
                cpu.State.Ax = GetNumberOfNodes(cpu, ReadFarPointer(1), ReadFarPointer(3));
                return;
            case GetNodeFunction:
                cpu.State.Ax = GetNode(cpu, ReadFarPointer(1), ReadFarPointer(3), ReadArgument(5));
                return;
            case SetNodeFunction:
                cpu.State.Ax = Success; // Accepted; motherboard resources cannot move.
                return;
            case GetEventFunction:
            case SendMessageFunction:
                cpu.State.Ax = FunctionNotSupported;
                return;
            case GetDockingInformationFunction:
                cpu.State.Ax = SystemNotDocked;
                return;
            case SetStaticResourcesFunction:
                cpu.State.Ax = Success; // The fixed motherboard has no mutable allocation.
                return;
            case GetStaticResourcesFunction:
                cpu.State.Ax = GetStaticResources(cpu, ReadFarPointer(1));
                return;
            case GetIsaConfigurationFunction:
                cpu.State.Ax = GetIsaConfiguration(cpu, ReadFarPointer(1));
                return;
            default:
                cpu.State.Ax = UnknownFunction;
                return;
        }
    }

    /// <summary>Reports the stable BIOS node count and the longest serialized node.</summary>
    private ushort GetNumberOfNodes(CpuNs.Cpu cpu, uint countPointer, uint sizePointer)
    {
        cpu.WriteLinearByte(countPointer, (byte)_nodes.Count);
        cpu.WriteLinearWord(sizePointer, (ushort)_nodes.Max(n => n.Length));
        return Success;
    }

    /// <summary>Copies the requested node and advances the caller's enumeration handle.</summary>
    private ushort GetNode(CpuNs.Cpu cpu, uint numberPointer, uint buffer, ushort control)
    {
        // Control 1 asks for the current configuration, 2 for the one at
        // next boot; on a board where nothing moves they are the same.
        byte number = cpu.ReadLinearByte(numberPointer);
        int nodeIndex = _nodes.FindIndex(node => node[2] == number);
        if (nodeIndex < 0)
        {
            return InvalidHandle;
        }

        byte[] node = _nodes[nodeIndex];
        for (int i = 0; i < node.Length; i++)
        {
            cpu.WriteLinearByte(buffer + (uint)i, node[i]);
        }

        // The caller walks the list by following this: the next node's
        // number, or FFh after the last.
        int nextIndex = nodeIndex + 1;
        byte nextHandle = nextIndex < _nodes.Count ? _nodes[nextIndex][2] : LastNodeHandle;
        cpu.WriteLinearByte(numberPointer, nextHandle);
        return Success;
    }

    /// <summary>
    /// Resources reserved for devices the nodes do not describe. There are
    /// none, so the block is an end tag and nothing else.
    /// </summary>
    private static ushort GetStaticResources(CpuNs.Cpu cpu, uint buffer)
    {
        var block = new ResourceBlock();
        byte[] bytes = block.End();
        for (int i = 0; i < bytes.Length; i++)
        {
            cpu.WriteLinearByte(buffer + (uint)i, bytes[i]);
        }
        return Success;
    }

    /// <summary>Writes the ISA configuration structure supplied by the PnP BIOS service.</summary>
    private ushort GetIsaConfiguration(CpuNs.Cpu cpu, uint buffer)
    {
        const uint StructureRevisionOffset = 0;
        const uint AssignedCardCountOffset = 1;
        const uint ReadDataPortOffset = 2;
        const uint ReservedOffset = 4;
        const byte StructureRevision = 0x01;

        cpu.WriteLinearByte(buffer + StructureRevisionOffset, StructureRevision);
        cpu.WriteLinearByte(
            buffer + AssignedCardCountOffset,
            (byte)_machine.IsaPlugAndPlay.AssignedCount);
        cpu.WriteLinearWord(buffer + ReadDataPortOffset, IsaReadDataPort);
        cpu.WriteLinearWord(buffer + ReservedOffset, 0x0000);
        return Success;
    }

    // --- the nodes -----------------------------------------------------------------

    private List<byte[]> BuildNodes()
    {
        var nodes = new List<byte[]>();
        byte handle = 0;

        void Add(
            string id,
            byte type,
            byte subtype,
            byte iface,
            ushort attributes,
            Action<ResourceBlock> resources)
        {
            var block = new ResourceBlock();
            resources(block);
            nodes.Add(BuildNode(handle++, id, type, subtype, iface, attributes, block.End()));
        }

        void NoResources(ResourceBlock resourceBlock)
        {
        }

        Add("PNP0A00", 0x06, 0x01, 0x00, CannotDisable, NoResources); // ISA bus
        Add("PNP0000", 0x08, 0x00, 0x01, CannotDisable, resources =>
        {
            resources.Io(0x20, 2); // Master 8259.
            resources.Io(0xA0, 2); // Slave 8259.
            resources.Irq(2); // Cascade input.
        });
        Add("PNP0200", 0x08, 0x01, 0x01, CannotDisable, resources =>
        {
            resources.Io(0x00, 16); // Primary 8237 channels.
            resources.Io(0x80, 16); // Page registers.
            resources.Io(0xC0, 32); // Secondary 8237 channels.
            resources.Dma(4); // Cascade channel.
        });
        Add("PNP0100", 0x08, 0x02, 0x01, CannotDisable, resources =>
        {
            resources.Io(0x40, 4); // PIT8253 channels and command.
            resources.Irq(0);
        });
        Add("PNP0B00", 0x08, 0x03, 0x01, CannotDisable, resources =>
        {
            resources.Io(0x70, 2); // CMOS index and data ports.
            resources.Irq(8);
        });
        Add("PNP0800", 0x08, 0x04, 0x00, CannotDisable, resources =>
        {
            resources.Io(0x61, 1); // System control port B.
        });
        Add("PNP0C04", 0x08, 0x80, 0x00, CannotDisable, resources =>
        {
            resources.Io(0xF0, 16); // Numeric coprocessor ports.
            resources.Irq(13);
        });
        ushort fixedInputDevice = CannotDisable | InputDevice;
        Add("PNP0303", 0x09, 0x00, 0x00, fixedInputDevice, resources =>
        {
            resources.Io(0x60, 1); // 8042 data port.
            resources.Io(0x64, 1); // 8042 command and status port.
            resources.Irq(1);
        });
        Add("PNP0F13", 0x0F, 0x00, 0x00, fixedInputDevice, resources =>
        {
            resources.Irq(12); // PS/2 auxiliary device.
        });
        Add("PNP0501", 0x07, 0x00, 0x02, CannotDisable, resources =>
        {
            resources.Io(0x3F8, 8); // COM1.
            resources.Irq(4);
        });
        // PCI owns both PIIX channels. Separate legacy IDE nodes reserve
        // the same ports and IRQs a second time. Retire their handles without
        // changing the identities of the video and memory nodes after them.
        handle += 2;

        // The display adapter is a PCI device and announces itself there, so
        // a PNP0900 node here would reserve the VGA ports and the A0000
        // window a second time. Windows 95 resolves that the same way it did
        // for the duplicated IDE channels: it finds the PCI card, matches the
        // S3 driver to it, and then marks the card as not working because
        // another device already holds its resources. Retire the handle
        // without adding the node, so the memory and bus nodes after it keep
        // the identities a guest may already have stored.
        handle++;
        Add("PNP0C01", 0x05, 0x00, 0x00, CannotDisable, resources =>
        {
            resources.Memory(0x00000, 0xA0000);
            uint extended = _machine.Memory.RamSize - 0x100000;
            if (extended > 0)
            {
                resources.Memory(0x100000, extended);
            }
        });

        // Windows 95 needs a system-bus node before it can enumerate PCI
        // children. Append it so existing BIOS device handles stay stable.
        ushort cannotDisableOrConfigure = CannotDisable | CannotConfigure;
        Add("PNP0A03", 0x06, 0x04, 0x00, cannotDisableOrConfigure, NoResources);

        // LPT1: communication device, parallel, unidirectional. Appended
        // after every existing node, like the system bus above, so the
        // handles a guest has already stored keep their meaning.
        Add("PNP0400", 0x07, 0x01, 0x00, CannotDisable, resources =>
        {
            resources.Io(Devices.Parallel.ParallelPort.DataPort, 3); // Data, status and control.
            if (_machine.ParallelPortInterruptReported)
            {
                resources.Irq(7);
            }
        });

        return nodes;
    }

    /// <summary>
    /// One node in the layout the specification gives: size, handle, the
    /// compressed EISA identifier, three type bytes, attributes, then three
    /// blocks -- allocated resources, possible resources, compatible
    /// identifiers -- each closed by an end tag. Nothing can move, so the
    /// possible resources are the allocated ones again.
    /// </summary>
    private static byte[] BuildNode(
        byte handle,
        string id,
        byte type,
        byte subtype,
        byte iface,
        ushort attributes,
        byte[] resources)
    {
        byte[] end = new ResourceBlock().End();
        var node = new List<byte>();
        node.Add(0); // Size, filled in below.
        node.Add(0);
        node.Add(handle);
        node.AddRange(CompressId(id));
        node.Add(type);
        node.Add(subtype);
        node.Add(iface);
        node.Add((byte)attributes);
        node.Add((byte)(attributes >> 8));
        node.AddRange(resources); // Allocated resources.
        node.AddRange(resources); // Possible resources.
        node.AddRange(end); // Compatible IDs: none.
        node[0] = (byte)node.Count;
        node[1] = (byte)(node.Count >> 8);
        return node.ToArray();
    }

    /// <summary>
    /// The EISA identifier in its four-byte form: three letters in five bits
    /// each, then four hexadecimal digits. "PNP0000" becomes 41 D0 00 00.
    /// </summary>
    public static byte[] CompressId(string id)
    {
        int firstLetter = id[0] - '@';
        int secondLetter = id[1] - '@';
        int thirdLetter = id[2] - '@';
        int digits = Convert.ToInt32(id[3..7], 16);
        return
        [
            (byte)((firstLetter << 2) | (secondLetter >> 3)),
            (byte)(((secondLetter & 7) << 5) | thirdLetter),
            (byte)(digits >> 8),
            (byte)digits,
        ];
    }

    /// <summary>
    /// Writes resource descriptors in the ISA Plug and Play tag format,
    /// which the PnP BIOS borrows unchanged: a tag byte giving the item and
    /// its length, then the item, and an end tag with a checksum over the
    /// whole block.
    /// </summary>
    public sealed class ResourceBlock
    {
        private const byte IrqTag = 0x22;
        private const byte DmaTag = 0x2A;
        private const byte IoPortTag = 0x47;
        private const byte FixedMemory32Tag = 0x86;
        private const byte FixedMemory32PayloadLength = 0x09;
        private const byte EndTag = 0x79;
        private const byte IoPort16BitDecode = 0x01;
        private const byte IoPortAlignment = 0x01;
        private const byte DmaCompatibilityTiming = 0x00;
        private const byte ReadOnlyMemory = 0x00;

        private readonly List<byte> _bytes = new();

        /// <summary>Adds the two-byte small-resource IRQ mask for one fixed interrupt line.</summary>
        public void Irq(int line)
        {
            _bytes.Add(IrqTag);
            _bytes.Add((byte)(1 << line));
            _bytes.Add((byte)((1 << line) >> 8));
        }

        /// <summary>Adds the two-byte small-resource DMA mask for one fixed channel.</summary>
        public void Dma(int channel)
        {
            _bytes.Add(DmaTag);
            _bytes.Add((byte)(1 << channel));
            _bytes.Add(DmaCompatibilityTiming); // 8-bit, not bus-master, compatibility timing.
        }

        /// <summary>Adds an ISA 16-bit-decode I/O range with its fixed alignment and length.</summary>
        public void Io(ushort baseAddress, byte length)
        {
            _bytes.Add(IoPortTag);
            _bytes.Add(IoPort16BitDecode);
            _bytes.Add((byte)baseAddress);
            _bytes.Add((byte)(baseAddress >> 8));
            _bytes.Add((byte)baseAddress);
            _bytes.Add((byte)(baseAddress >> 8));
            _bytes.Add(IoPortAlignment);
            _bytes.Add(length);
        }

        /// <summary>Adds a read-only 32-bit fixed-memory large resource descriptor.</summary>
        public void Memory(uint baseAddress, uint length)
        {
            _bytes.Add(FixedMemory32Tag);
            _bytes.Add(FixedMemory32PayloadLength);
            _bytes.Add(0x00); // High byte of the little-endian payload length.
            _bytes.Add(ReadOnlyMemory);
            foreach (byte value in BitConverter.GetBytes(baseAddress))
            {
                _bytes.Add(value);
            }

            foreach (byte value in BitConverter.GetBytes(length))
            {
                _bytes.Add(value);
            }
        }

        /// <summary>The block closed: end tag plus a checksum that makes the whole sum to zero.</summary>
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
    }
}
