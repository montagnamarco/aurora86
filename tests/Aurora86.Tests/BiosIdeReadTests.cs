using Aurora86.Core;
using Aurora86.Core.Cpu;
using Aurora86.Core.Devices.Storage;

namespace Aurora86.Tests;

/// <summary>BIOS disk reads must be visible to a protected-mode IDE monitor.</summary>
public sealed class BiosIdeReadTests
{
    [Theory]
    [InlineData(0x80, 0x1F0)]
    [InlineData(0x81, 0x170)]
    public void BiosReadsTransferThroughTheSelectedIdeCable(int driveNumber, int commandBase)
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);
        DiskImage disk = DiskImage.CreateInMemory((byte)driveNumber, 20, 16, 63);
        machine.AttachDisk(disk);
        byte[] expected = Enumerable.Range(0, 1024).Select(index => (byte)(index * 17 + 3)).ToArray();
        Assert.True(disk.WriteLinear(1136, 2, expected));
        var commands = new List<byte>();
        machine.Io.PortAccessed += (port, write, value) =>
        {
            if (write && port == commandBase + 7)
            {
                commands.Add(value);
            }
        };

        machine.Cpu.State.Halted = false;
        machine.Cpu.SetSegment(SegReg.Cs, 0);
        machine.Cpu.SetSegment(SegReg.Ss, 0);
        machine.Cpu.SetSegment(SegReg.Es, 0);
        machine.Cpu.State.Esp = 0x7000;
        machine.Cpu.State.Eip = 0x2000;
        machine.Cpu.State.Eax = 0x12340202;
        machine.Cpu.State.Ebx = 0x56783000;
        machine.Cpu.State.Cx = 0x0103;
        machine.Cpu.State.Dx = (ushort)(0x0200 | driveNumber);
        machine.Cpu.State.Esi = 0x11223344;
        machine.Cpu.State.Edi = 0x55667788;
        machine.Cpu.State.Ebp = 0x99AABBCC;
        machine.Bda.DiskStatus = 0x20;
        machine.Bda.HardDiskStatus = 0x20;
        machine.Memory.WriteByte(0x2000, 0xCD);
        machine.Memory.WriteByte(0x2001, 0x13);
        for (int step = 0; step < 10000; step++)
        {
            machine.Cpu.Step();
            if (machine.Cpu.State.Cs.Selector == 0 && machine.Cpu.State.Eip == 0x2002)
            {
                break;
            }
        }

        Assert.Equal(0x2002u, machine.Cpu.State.Eip);
        Assert.False(machine.Cpu.Flags.Carry);
        Assert.Contains((byte)0x20, commands);
        Assert.Equal(expected, Enumerable.Range(0, expected.Length)
            .Select(index => machine.Memory.ReadByte(0x3000 + (uint)index)).ToArray());
        Assert.Equal(0x12340002u, machine.Cpu.State.Eax);
        Assert.Equal(0x56783000u, machine.Cpu.State.Ebx);
        Assert.Equal(0x11223344u, machine.Cpu.State.Esi);
        Assert.Equal(0x55667788u, machine.Cpu.State.Edi);
        Assert.Equal(0x99AABBCCu, machine.Cpu.State.Ebp);
        Assert.Equal(0x7000u, machine.Cpu.State.Esp);
        Assert.Equal(0, machine.Bda.DiskStatus);
        Assert.Equal(0, machine.Bda.HardDiskStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisablingIdeInterruptsKeepsPolledDataFromReachingThePic(bool acknowledge)
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);
        machine.AttachDisk(DiskImage.CreateInMemory(0x80, 2, 16, 63));
        machine.Io.WriteByte(0x3F6, 2);
        machine.Io.WriteByte(0x1F6, 0xA0);
        machine.Io.WriteByte(0x1F7, 0xEC);
        machine.Io.WriteByte(0xA0, 0x0A);
        Assert.Equal(0, machine.Io.ReadByte(0xA0) & 0x40);
        Assert.Equal(0x58, machine.Io.ReadByte(0x3F6));
        if (acknowledge)
        {
            machine.Io.ReadByte(0x1F7);
        }
        machine.Io.WriteByte(0x3F6, 0);
        Assert.Equal(acknowledge ? 0 : 0x40, machine.Io.ReadByte(0xA0) & 0x40);
    }
}
