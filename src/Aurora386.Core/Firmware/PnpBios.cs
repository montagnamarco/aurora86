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

    public void Handle(CpuNs.Cpu cpu)
    {
        // The far call left four bytes of return address on the stack; the
        // arguments start above them, function number first.
        uint sp = cpu.State.Ss.Big ? cpu.State.Esp : cpu.State.Sp;
        ushort Arg(int index) => cpu.ReadMemoryWord(CpuNs.SegReg.Ss, sp + 4 + (uint)(2 * index));
        uint Far(int index) => cpu.FarPointerToLinear(Arg(index + 1), Arg(index));

        ushort function = Arg(0);
        cpu.State.Ax = function switch
        {
            0x00 => GetNumberOfNodes(cpu, Far(1), Far(3)),
            0x01 => GetNode(cpu, Far(1), Far(3), Arg(5)),
            0x02 => Success,                     // set node: accepted, nothing to change
            0x03 => FunctionNotSupported,        // get event: no event notification
            0x04 => FunctionNotSupported,        // send message
            0x05 => SystemNotDocked,
            0x09 => Success,                     // set statically allocated resources
            0x0A => GetStaticResources(cpu, Far(1)),
            0x40 => GetIsaConfiguration(cpu, Far(1)),
            _ => UnknownFunction,
        };
    }

    private ushort GetNumberOfNodes(CpuNs.Cpu cpu, uint countPointer, uint sizePointer)
    {
        cpu.WriteLinearByte(countPointer, (byte)_nodes.Count);
        cpu.WriteLinearWord(sizePointer, (ushort)_nodes.Max(n => n.Length));
        return Success;
    }

    private ushort GetNode(CpuNs.Cpu cpu, uint numberPointer, uint buffer, ushort control)
    {
        // Control 1 asks for the current configuration, 2 for the one at
        // next boot; on a board where nothing moves they are the same.
        byte number = cpu.ReadLinearByte(numberPointer);
        if (number >= _nodes.Count) return InvalidHandle;

        byte[] node = _nodes[number];
        for (int i = 0; i < node.Length; i++) cpu.WriteLinearByte(buffer + (uint)i, node[i]);

        // The caller walks the list by following this: the next node's
        // number, or FFh after the last.
        cpu.WriteLinearByte(numberPointer, number + 1 < _nodes.Count ? (byte)(number + 1) : (byte)0xFF);
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
        for (int i = 0; i < bytes.Length; i++) cpu.WriteLinearByte(buffer + (uint)i, bytes[i]);
        return Success;
    }

    private ushort GetIsaConfiguration(CpuNs.Cpu cpu, uint buffer)
    {
        cpu.WriteLinearByte(buffer + 0, 0x01);               // structure revision
        cpu.WriteLinearByte(buffer + 1, (byte)_machine.IsaPlugAndPlay.AssignedCount);   // cards given a number so far
        cpu.WriteLinearWord(buffer + 2, IsaReadDataPort);
        cpu.WriteLinearWord(buffer + 4, 0x0000);             // reserved
        return Success;
    }

    // --- the nodes -----------------------------------------------------------------

    private List<byte[]> BuildNodes()
    {
        var nodes = new List<byte[]>();
        byte handle = 0;

        // Attributes: bit 0 says the device cannot be disabled, bit 3 that
        // it can be booted from, bit 4 that it is an input device. Nothing
        // here can be configured (bit 1 clear), so a request to change a
        // node is refused before it is made.
        const ushort Fixed = 0x0001;
        const ushort Boot = 0x0008;
        const ushort Input = 0x0010;

        void Add(string id, byte type, byte subtype, byte iface, ushort attributes, Action<ResourceBlock> resources)
        {
            var block = new ResourceBlock();
            resources(block);
            nodes.Add(BuildNode(handle++, id, type, subtype, iface, attributes, block.End()));
        }

        Add("PNP0A00", 0x06, 0x01, 0x00, Fixed, _ => { });                       // ISA bus
        Add("PNP0000", 0x08, 0x00, 0x01, Fixed, r => { r.Io(0x20, 2); r.Io(0xA0, 2); r.Irq(2); });
        Add("PNP0200", 0x08, 0x01, 0x01, Fixed, r => { r.Io(0x00, 16); r.Io(0x80, 16); r.Io(0xC0, 32); r.Dma(4); });
        Add("PNP0100", 0x08, 0x02, 0x01, Fixed, r => { r.Io(0x40, 4); r.Irq(0); });
        Add("PNP0B00", 0x08, 0x03, 0x01, Fixed, r => { r.Io(0x70, 2); r.Irq(8); });
        Add("PNP0800", 0x08, 0x04, 0x00, Fixed, r => r.Io(0x61, 1));
        Add("PNP0C04", 0x08, 0x80, 0x00, Fixed, r => { r.Io(0xF0, 16); r.Irq(13); });
        Add("PNP0303", 0x09, 0x00, 0x00, (ushort)(Fixed | Input), r => { r.Io(0x60, 1); r.Io(0x64, 1); r.Irq(1); });
        Add("PNP0F13", 0x0F, 0x00, 0x00, (ushort)(Fixed | Input), r => r.Irq(12));
        Add("PNP0501", 0x07, 0x00, 0x02, Fixed, r => { r.Io(0x3F8, 8); r.Irq(4); });
        Add("PNP0600", 0x01, 0x01, 0x00, (ushort)(Fixed | Boot), r => { r.Io(0x1F0, 8); r.Io(0x3F6, 1); r.Irq(14); });
        Add("PNP0600", 0x01, 0x01, 0x00, Fixed, r => { r.Io(0x170, 8); r.Io(0x376, 1); r.Irq(15); });
        Add("PNP0900", 0x03, 0x00, 0x00, Fixed, r =>
        {
            r.Io(0x3B0, 12);
            r.Io(0x3C0, 32);
            r.Memory(0xA0000, 0x20000);
        });
        Add("PNP0C01", 0x05, 0x00, 0x00, Fixed, r =>
        {
            r.Memory(0x00000, 0xA0000);
            uint extended = _machine.Memory.RamSize - 0x100000;
            if (extended > 0) r.Memory(0x100000, extended);
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
    private static byte[] BuildNode(byte handle, string id, byte type, byte subtype, byte iface,
                                    ushort attributes, byte[] resources)
    {
        byte[] end = new ResourceBlock().End();
        var node = new List<byte>();
        node.Add(0); node.Add(0);                 // size, filled in below
        node.Add(handle);
        node.AddRange(CompressId(id));
        node.Add(type); node.Add(subtype); node.Add(iface);
        node.Add((byte)attributes); node.Add((byte)(attributes >> 8));
        node.AddRange(resources);                 // allocated
        node.AddRange(resources);                 // possible
        node.AddRange(end);                       // compatible ids: none
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

    /// <summary>
    /// Writes resource descriptors in the ISA Plug and Play tag format,
    /// which the PnP BIOS borrows unchanged: a tag byte giving the item and
    /// its length, then the item, and an end tag with a checksum over the
    /// whole block.
    /// </summary>
    public sealed class ResourceBlock
    {
        private readonly List<byte> _bytes = new();

        public void Irq(int line)
        {
            _bytes.Add(0x22);                     // IRQ, 2 bytes
            _bytes.Add((byte)(1 << line));
            _bytes.Add((byte)((1 << line) >> 8));
        }

        public void Dma(int channel)
        {
            _bytes.Add(0x2A);                     // DMA, 2 bytes
            _bytes.Add((byte)(1 << channel));
            _bytes.Add(0x00);                     // 8-bit, not a bus master, compatibility timing
        }

        public void Io(ushort baseAddress, byte length)
        {
            _bytes.Add(0x47);                     // I/O port, 7 bytes
            _bytes.Add(0x01);                     // 16-bit decode
            _bytes.Add((byte)baseAddress); _bytes.Add((byte)(baseAddress >> 8));
            _bytes.Add((byte)baseAddress); _bytes.Add((byte)(baseAddress >> 8));
            _bytes.Add(0x01);                     // alignment
            _bytes.Add(length);
        }

        public void Memory(uint baseAddress, uint length)
        {
            _bytes.Add(0x86);                     // 32-bit fixed memory, large item
            _bytes.Add(0x09); _bytes.Add(0x00);   // 9 bytes follow
            _bytes.Add(0x00);                     // information: not writeable
            foreach (byte b in BitConverter.GetBytes(baseAddress)) _bytes.Add(b);
            foreach (byte b in BitConverter.GetBytes(length)) _bytes.Add(b);
        }

        /// <summary>The block closed: end tag plus a checksum that makes the whole sum to zero.</summary>
        public byte[] End()
        {
            var block = new List<byte>(_bytes) { 0x79 };
            int sum = 0;
            foreach (byte b in block) sum += b;
            block.Add((byte)(-sum));
            return block.ToArray();
        }
    }
}
