using Aurora386.Core;
using Aurora386.Core.Cpu;
using Aurora386.Core.Firmware;

namespace Aurora386.Tests;

/// <summary>
/// Tests for the Plug and Play BIOS, called the way an operating system
/// calls it: a far call with arguments on the stack, from real mode and
/// from 16-bit protected mode, and the answers read back out of memory.
/// </summary>
public class PnpBiosTests
{
    private const uint Program = 0x2000;
    private const uint Scratch = 0x3000;
    private const uint Buffer = 0x4000;
    private const uint GdtBase = 0x1000;

    private static Machine Boot()
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        machine.Cpu.State.Halted = false;
        return machine;
    }

    /// <summary>
    /// Writes and runs a program that calls the entry point with the given
    /// arguments (function first, then the rest as the specification lists
    /// them), cleans the stack and halts. Returns AX.
    /// </summary>
    private static ushort Call(Machine machine, ushort entrySegment, ushort function, params ushort[] arguments)
    {
        var code = new List<byte>();
        for (int i = arguments.Length - 1; i >= 0; i--)
        {
            code.Add(0x68); code.Add((byte)arguments[i]); code.Add((byte)(arguments[i] >> 8));
        }
        code.Add(0x68); code.Add((byte)function); code.Add((byte)(function >> 8));
        code.Add(0x9A);                                     // CALL FAR
        code.Add(unchecked((byte)BiosRom.Entry.PlugAndPlay)); code.Add((byte)(BiosRom.Entry.PlugAndPlay >> 8));
        code.Add((byte)entrySegment); code.Add((byte)(entrySegment >> 8));
        code.Add(0x83); code.Add(0xC4); code.Add((byte)(2 * (arguments.Length + 1)));   // ADD SP, n
        code.Add(0xF4);                                     // HLT

        for (int i = 0; i < code.Count; i++) machine.Memory.WriteByte(Program + (uint)i, code[i]);
        machine.Cpu.State.Eip = Program;
        machine.Cpu.State.Esp = 0x7000;
        machine.Cpu.State.Halted = false;

        for (int i = 0; i < 200 && !machine.Cpu.State.Halted; i++) machine.Cpu.Step();
        Assert.True(machine.Cpu.State.Halted, "the program did not reach its HLT");
        Assert.Equal(0x7000u, machine.Cpu.State.Esp);       // the stack came back level
        return machine.Cpu.State.Ax;
    }

    private static Machine RealMode()
    {
        Machine machine = Boot();
        machine.Cpu.SetSegment(SegReg.Cs, 0);
        machine.Cpu.SetSegment(SegReg.Ss, 0);
        machine.Cpu.SetSegment(SegReg.Ds, 0);
        return machine;
    }

    private static void WriteDescriptor(Machine machine, int index, uint baseAddress, uint limit, byte access)
    {
        uint address = GdtBase + (uint)(index * 8);
        machine.Memory.WriteWord(address, (ushort)(limit & 0xFFFF));
        machine.Memory.WriteWord(address + 2, (ushort)(baseAddress & 0xFFFF));
        machine.Memory.WriteByte(address + 4, (byte)((baseAddress >> 16) & 0xFF));
        machine.Memory.WriteByte(address + 5, access);
        machine.Memory.WriteByte(address + 6, (byte)((limit >> 16) & 0x0F));   // 16-bit, byte granular
        machine.Memory.WriteByte(address + 7, (byte)((baseAddress >> 24) & 0xFF));
    }

    /// <summary>
    /// Protected mode as Windows 95 sets it up for the BIOS: a 16-bit code
    /// selector based where the header says, and 16-bit data selectors for
    /// the buffers. Here selector 8 is the BIOS code, 16 flat data, 24 the
    /// test program's own code.
    /// </summary>
    private static Machine ProtectedMode()
    {
        Machine machine = Boot();
        WriteDescriptor(machine, 1, BiosRom.Base, 0xFFFF, 0x9A);
        WriteDescriptor(machine, 2, 0, 0xFFFF, 0x92);
        WriteDescriptor(machine, 3, 0, 0xFFFF, 0x9A);
        machine.Cpu.State.GdtBase = GdtBase;
        machine.Cpu.State.GdtLimit = 0x1F;
        machine.Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable;
        machine.Cpu.SetSegment(SegReg.Cs, 24);
        machine.Cpu.SetSegment(SegReg.Ss, 16);
        machine.Cpu.SetSegment(SegReg.Ds, 16);
        return machine;
    }

    // --- the header ---------------------------------------------------------------

    [Fact]
    public void TheInstallationCheckStructureIsFoundByScanningTheRom()
    {
        using Machine machine = Boot();
        uint? found = null;
        for (uint address = 0xF0000; address < 0x100000 && found is null; address += 16)
        {
            if (machine.Memory.ReadByte(address) == '$' && machine.Memory.ReadByte(address + 1) == 'P'
                && machine.Memory.ReadByte(address + 2) == 'n' && machine.Memory.ReadByte(address + 3) == 'P')
                found = address;
        }
        Assert.NotNull(found);
        uint header = found.Value;

        Assert.Equal(0x10, machine.Memory.ReadByte(header + 4));   // version 1.0
        Assert.Equal(0x21, machine.Memory.ReadByte(header + 5));
        int sum = 0;
        for (uint i = 0; i < 0x21; i++) sum += machine.Memory.ReadByte(header + i);
        Assert.Equal(0, sum & 0xFF);

        ushort realOffset = machine.Memory.ReadWord(header + 0x0D);
        ushort realSegment = machine.Memory.ReadWord(header + 0x0F);
        Assert.Equal(0xF000, realSegment);
        Assert.Equal(realOffset, machine.Memory.ReadWord(header + 0x11));     // same entry in protected mode
        Assert.Equal(0xF0000u, machine.Memory.ReadDword(header + 0x13));      // its code segment base

        // The entry point is a hook followed by RETF: a far call comes back.
        uint entry = ((uint)realSegment << 4) + realOffset;
        Assert.Equal(0x0F, machine.Memory.ReadByte(entry));
        Assert.Equal(0xFF, machine.Memory.ReadByte(entry + 1));
        Assert.Equal(0xCB, machine.Memory.ReadByte(entry + 3));
    }

    // --- functions, real mode ---------------------------------------------------------

    [Fact]
    public void FunctionZeroReportsTheNodeCountAndTheLargestNode()
    {
        using Machine machine = RealMode();
        ushort status = Call(machine, 0xF000, 0x00, (ushort)Scratch, 0, (ushort)(Scratch + 2), 0, 0xF000);

        Assert.Equal(0, status);
        Assert.Equal(machine.PlugAndPlay.Nodes.Count, machine.Memory.ReadByte(Scratch));
        Assert.Equal(machine.PlugAndPlay.Nodes.Max(n => n.Length), machine.Memory.ReadWord(Scratch + 2));
        Assert.True(machine.PlugAndPlay.Nodes.Count >= 10);
    }

    [Fact]
    public void FunctionOneWalksEveryNodeAndEndsWithFF()
    {
        using Machine machine = RealMode();
        machine.Memory.WriteByte(Scratch, 0);
        var seen = new List<byte[]>();

        for (int guard = 0; guard < 64; guard++)
        {
            byte number = machine.Memory.ReadByte(Scratch);
            if (number == 0xFF) break;
            ushort status = Call(machine, 0xF000, 0x01, (ushort)Scratch, 0, (ushort)Buffer, 0, 1, 0xF000);
            Assert.Equal(0, status);

            ushort size = machine.Memory.ReadWord(Buffer);
            byte[] node = new byte[size];
            for (uint i = 0; i < size; i++) node[i] = machine.Memory.ReadByte(Buffer + i);
            Assert.Equal(number, node[2]);                 // the handle is the number asked for
            seen.Add(node);
        }

        Assert.Equal(machine.PlugAndPlay.Nodes.Count, seen.Count);
        for (int i = 0; i < seen.Count; i++) Assert.Equal(machine.PlugAndPlay.Nodes[i], seen[i]);
    }

    [Fact]
    public void AskingForANodeThatDoesNotExistIsAnInvalidHandle()
    {
        using Machine machine = RealMode();
        machine.Memory.WriteByte(Scratch, 0xFE);
        Assert.Equal(0x83, Call(machine, 0xF000, 0x01, (ushort)Scratch, 0, (ushort)Buffer, 0, 1, 0xF000));
    }

    [Fact]
    public void SettingANodeIsAcceptedAndChangesNothing()
    {
        using Machine machine = RealMode();
        byte[] before = machine.PlugAndPlay.Nodes[1];
        Assert.Equal(0, Call(machine, 0xF000, 0x02, 1, (ushort)Buffer, 0, 1, 0xF000));
        Assert.Equal(before, machine.PlugAndPlay.Nodes[1]);
    }

    [Fact]
    public void UnknownFunctionIsRefusedWithStatus81()
    {
        using Machine machine = RealMode();
        Assert.Equal(0x81, Call(machine, 0xF000, 0x7F, 0xF000));
    }

    [Fact]
    public void IsaConfigurationReportsTheReadPortAndNoCards()
    {
        using Machine machine = RealMode();
        Assert.Equal(0, Call(machine, 0xF000, 0x40, (ushort)Buffer, 0, 0xF000));
        Assert.Equal(1, machine.Memory.ReadByte(Buffer));
        Assert.Equal(0, machine.Memory.ReadByte(Buffer + 1));
        Assert.Equal(PnpBios.IsaReadDataPort, machine.Memory.ReadWord(Buffer + 2));
    }

    [Fact]
    public void StaticResourcesAreAnEmptyBlockWithAValidEndTag()
    {
        using Machine machine = RealMode();
        Assert.Equal(0, Call(machine, 0xF000, 0x0A, (ushort)Buffer, 0, 0xF000));
        Assert.Equal(0x79, machine.Memory.ReadByte(Buffer));
        Assert.Equal(0, (0x79 + machine.Memory.ReadByte(Buffer + 1)) & 0xFF);
    }

    // --- functions, protected mode ---------------------------------------------------------

    [Fact]
    public void TheSameEntryAnswersFromSixteenBitProtectedMode()
    {
        using Machine machine = ProtectedMode();
        // Selector 8 is the code, 16 the data the buffers live in.
        ushort status = Call(machine, 8, 0x00, (ushort)Scratch, 16, (ushort)(Scratch + 2), 16, 16);

        Assert.Equal(0, status);
        Assert.True(machine.Cpu.State.ProtectedMode);
        Assert.Equal(machine.PlugAndPlay.Nodes.Count, machine.Memory.ReadByte(Scratch));

        machine.Memory.WriteByte(Scratch, 0);
        Assert.Equal(0, Call(machine, 8, 0x01, (ushort)Scratch, 16, (ushort)Buffer, 16, 1, 16));
        Assert.Equal(machine.PlugAndPlay.Nodes[0][0], machine.Memory.ReadByte(Buffer));
        Assert.Equal(1, machine.Memory.ReadByte(Scratch));
    }

    // --- the nodes themselves ---------------------------------------------------------

    [Fact]
    public void EisaIdentifiersCompressAsTheSpecificationSays()
    {
        Assert.Equal(new byte[] { 0x41, 0xD0, 0x00, 0x00 }, PnpBios.CompressId("PNP0000"));
        Assert.Equal(new byte[] { 0x41, 0xD0, 0x06, 0x00 }, PnpBios.CompressId("PNP0600"));
        Assert.Equal(new byte[] { 0x41, 0xD0, 0x0F, 0x13 }, PnpBios.CompressId("PNP0F13"));
    }

    /// <summary>Splits a node into its three tag blocks, checking each end tag's checksum on the way.</summary>
    private static List<List<(byte Tag, byte[] Data)>> ParseBlocks(byte[] node)
    {
        var blocks = new List<List<(byte, byte[])>>();
        int i = 12;
        while (blocks.Count < 3)
        {
            var items = new List<(byte, byte[])>();
            int start = i;
            while (true)
            {
                byte tag = node[i];
                int length;
                byte[] data;
                if ((tag & 0x80) != 0)
                {
                    length = node[i + 1] | (node[i + 2] << 8);
                    data = node[(i + 3)..(i + 3 + length)];
                    i += 3 + length;
                }
                else
                {
                    length = tag & 7;
                    data = node[(i + 1)..(i + 1 + length)];
                    i += 1 + length;
                }
                items.Add((tag, data));
                if (tag == 0x79)
                {
                    int sum = 0;
                    for (int j = start; j < i; j++) sum += node[j];
                    Assert.Equal(0, sum & 0xFF);
                    break;
                }
            }
            blocks.Add(items);
        }
        Assert.Equal(node.Length, i);
        return blocks;
    }

    [Fact]
    public void EveryNodeIsWellFormed()
    {
        using Machine machine = Boot();
        foreach (byte[] node in machine.PlugAndPlay.Nodes)
        {
            Assert.Equal(node.Length, node[0] | (node[1] << 8));
            var blocks = ParseBlocks(node);
            Assert.Equal(3, blocks.Count);
            // possible = allocated: nothing moves
            Assert.Equal(blocks[0].Select(b => (b.Tag, Convert.ToHexString(b.Data))),
                         blocks[1].Select(b => (b.Tag, Convert.ToHexString(b.Data))));
            Assert.Single(blocks[2]);                             // no compatible ids, just the end tag
        }
    }

    [Fact]
    public void ThePrimaryIdeNodeClaimsItsPortsAndInterrupt()
    {
        using Machine machine = Boot();
        byte[] ide = machine.PlugAndPlay.Nodes.First(n =>
            n[3..7].SequenceEqual(PnpBios.CompressId("PNP0600")));
        var allocated = ParseBlocks(ide)[0];

        var io = allocated.Where(item => item.Tag == 0x47).Select(item => item.Data[1] | (item.Data[2] << 8)).ToList();
        Assert.Contains(0x1F0, io);
        Assert.Contains(0x3F6, io);
        var irq = allocated.Single(item => item.Tag == 0x22);
        Assert.Equal(1 << 14, irq.Data[0] | (irq.Data[1] << 8));
        Assert.Equal(0x08, ide[10] & 0x08);                        // bootable
    }

    [Fact]
    public void TheMemoryNodeDescribesTheInstalledRam()
    {
        using var machine = new Machine(8 * 1024 * 1024);
        machine.RunInstructions(100);
        byte[] memory = machine.PlugAndPlay.Nodes.First(n =>
            n[3..7].SequenceEqual(PnpBios.CompressId("PNP0C01")));
        var ranges = ParseBlocks(memory)[0].Where(item => item.Tag == 0x86)
            .Select(item => (Base: BitConverter.ToUInt32(item.Data, 1), Length: BitConverter.ToUInt32(item.Data, 5)))
            .ToList();
        Assert.Contains((0u, 0xA0000u), ranges);
        Assert.Contains((0x100000u, 7u * 1024 * 1024), ranges);
    }
}
