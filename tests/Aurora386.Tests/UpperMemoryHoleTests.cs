using Aurora386.Core;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

public class UpperMemoryHoleTests
{
    [Fact]
    public void EmptyOptionRomSocketsDoNotLookLikeRam()
    {
        using var machine = new Machine();
        machine.RunInstructions(300);
        foreach (uint address in new uint[] { 0xC0000, 0xCFFFF, 0xD0000, 0xEFFFF })
        {
            machine.Memory.WriteByte(address, 0x5A);
            Assert.Equal(0xFF, machine.Memory.ReadByte(address));
        }
        machine.Memory.A20Enabled = true;
        machine.Memory.WriteByte(0x100000, 0x5A);
        Assert.Equal(0x5A, machine.Memory.ReadByte(0x100000));
        byte rom = machine.Memory.ReadByte(0xF0000);
        machine.Memory.WriteByte(0xF0000, (byte)~rom);
        Assert.Equal(rom, machine.Memory.ReadByte(0xF0000));
    }

    [Fact]
    public void UnmappingWatchedRamDoesNotLeaveAHiddenReadableCopy()
    {
        using var memory = new PhysicalMemoryBus();
        memory.WriteByte(0xC0000, 0x5A);
        memory.SetReadWatchRanges([(0xC0000u, 1u)]);
        memory.Unmap(0xC0000, 0x1000);
        Assert.Equal(0xFF, memory.ReadByte(0xC0000));
        memory.SetReadWatchRanges([]);
        Assert.Equal(0xFF, memory.ReadByte(0xC0000));
        memory.WriteByte(0xC0000, 0);
        Assert.Equal(0xFF, memory.ReadByte(0xC0000));
    }
}
