using Aurora86.Core;
using Aurora86.Core.Cpu;
using Aurora86.Core.Diagnostics;

namespace Aurora86.Tests;

/// <summary>
/// The I/O permission bitmap, read back as the ranges it traps.
///
/// This is the mechanism Windows in enhanced mode uses to catch a program's
/// port accesses: the video registers are trapped, the rest let through.
/// Reading it wrong -- off by one bit, or from the wrong end of the bitmap
/// -- points an investigation at the wrong ports, and nothing else in the
/// machine would say so.
/// </summary>
public class IoBitmapTests
{
    private const uint GdtBase = 0x1000;
    private const uint TssBase = 0x3000;
    private const ushort IoMapBase = 0x68;

    private static void WriteDescriptor(Machine machine, int index,
                                        uint baseAddress, uint limit, byte access)
    {
        uint address = GdtBase + (uint)(index * 8);
        machine.Memory.WriteWord(address, (ushort)(limit & 0xFFFF));
        machine.Memory.WriteWord(address + 2, (ushort)(baseAddress & 0xFFFF));
        machine.Memory.WriteByte(address + 4, (byte)((baseAddress >> 16) & 0xFF));
        machine.Memory.WriteByte(address + 5, access);
        machine.Memory.WriteByte(address + 6, (byte)((limit >> 16) & 0x0F));   // byte granular
        machine.Memory.WriteByte(address + 7, (byte)((baseAddress >> 24) & 0xFF));
    }

    /// <summary>
    /// A protected-mode machine whose current task carries a bitmap of the
    /// given length, initially trapping nothing, which the caller then marks.
    /// </summary>
    private static Machine WithBitmap(Action<Machine, uint> mark, int bitmapBytes = 8192)
    {
        var machine = new Machine();
        machine.RunInstructions(200);

        for (uint i = 0; i < IoMapBase; i += 4) machine.Memory.WriteDword(TssBase + i, 0);
        machine.Memory.WriteWord(TssBase + 0x66, IoMapBase);
        for (uint i = 0; i < bitmapBytes; i++) machine.Memory.WriteByte(TssBase + IoMapBase + i, 0);
        mark(machine, TssBase + IoMapBase);

        WriteDescriptor(machine, 1, TssBase, (uint)(IoMapBase + bitmapBytes - 1), 0x89);   // available 386 TSS
        machine.Cpu.State.GdtBase = GdtBase;
        machine.Cpu.State.GdtLimit = 0x17;
        machine.Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable;
        machine.Cpu.State.TaskRegister = 0x08;
        return machine;
    }

    private static void Trap(Machine machine, uint bitmap, int port)
    {
        uint at = bitmap + (uint)(port >> 3);
        machine.Memory.WriteByte(at, (byte)(machine.Memory.ReadByte(at) | (1 << (port & 7))));
    }

    [Fact]
    public void TrappedPortsComeBackAsRanges()
    {
        using Machine machine = WithBitmap((m, bitmap) =>
        {
            for (int port = 0x3C0; port <= 0x3CF; port++) Trap(m, bitmap, port);
            Trap(m, bitmap, 0x60);
        });

        IReadOnlyList<(ushort First, ushort Last)> trapped =
            new DescriptorTables(machine).ReadTrappedPorts();

        Assert.Equal(2, trapped.Count);
        Assert.Equal(((ushort)0x060, (ushort)0x060), trapped[0]);
        Assert.Equal(((ushort)0x3C0, (ushort)0x3CF), trapped[1]);
    }

    /// <summary>
    /// Ports past the end of a short bitmap fault, and a reader that stopped
    /// at the bitmap's end would call them open.
    /// </summary>
    [Fact]
    public void PortsBeyondAShortBitmapAreTrapped()
    {
        using Machine machine = WithBitmap((m, bitmap) => { }, bitmapBytes: 128);   // ports 0-3FF only

        IReadOnlyList<(ushort First, ushort Last)> trapped =
            new DescriptorTables(machine).ReadTrappedPorts();

        Assert.Single(trapped);
        Assert.Equal(((ushort)0x400, (ushort)0xFFFF), trapped[0]);
    }

    /// <summary>
    /// A bitmap base at or past the segment limit means no bitmap, and Intel
    /// documents that as "every port faults" -- the cheap way for a guest to
    /// trap everything.
    /// </summary>
    [Fact]
    public void ABitmapPastTheLimitTrapsEverything()
    {
        using Machine machine = WithBitmap((m, bitmap) => { });
        WriteDescriptor(machine, 1, TssBase, IoMapBase - 1, 0x89);   // the segment ends before the bitmap

        IReadOnlyList<(ushort First, ushort Last)> trapped =
            new DescriptorTables(machine).ReadTrappedPorts();

        Assert.Single(trapped);
        Assert.Equal(((ushort)0x000, (ushort)0xFFFF), trapped[0]);
    }
}
