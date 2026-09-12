using Aurora386.Core;
using Aurora386.Core.IO;
using Xunit;

namespace Aurora386.Tests;

/// <summary>
/// A mode set through INT 10h has to reach the adapter through the ports,
/// with OUT instructions the processor executes: a monitor that virtualises
/// the display traps those and keeps its own copy of the registers from
/// them. Windows' display device never reads the hardware back.
/// </summary>
public class VgaRomProgramTests
{
    private sealed class Spy(IIoDevice? inner) : IIoDevice
    {
        public readonly List<(ushort port, byte value)> Writes = [];
        public byte ReadByte(ushort port) => inner?.ReadByte(port) ?? 0xFF;
        public void WriteByte(ushort port, byte value) { Writes.Add((port, value)); inner?.WriteByte(port, value); }
    }

    private static Spy Watch(Machine machine, ushort port)
    {
        var spy = new Spy(machine.VideoRegisters);
        machine.Io.Register(port, spy);
        return spy;
    }

    private static void RunInt10(Machine machine, ushort ax)
    {
        // A little real-mode program at 2000:0100: MOV AX,imm / INT 10h / HLT.
        uint code = 0x20100;
        machine.Memory.WriteByte(code + 0, 0xB8);
        machine.Memory.WriteWord(code + 1, ax);
        machine.Memory.WriteByte(code + 3, 0xCD);
        machine.Memory.WriteByte(code + 4, 0x10);
        machine.Memory.WriteByte(code + 5, 0xF4);
        machine.Cpu.SetSegment(Aurora386.Core.Cpu.SegReg.Cs, 0x2000);
        machine.Cpu.State.Eip = 0x0100;
        machine.Cpu.SetSegment(Aurora386.Core.Cpu.SegReg.Ss, 0x3000);
        machine.Cpu.State.Esp = 0xFFFE;
        machine.Cpu.State.Halted = false;   // the POST halts when there is nothing to boot
        for (int i = 0; i < 5000 && !machine.Cpu.State.Halted; i++) machine.RunInstructions(1);
        Assert.True(machine.Cpu.State.Halted, "the program did not run to its HLT");
    }

    [Fact]
    public void ATextModeSetWritesTheRomValuesThroughThePorts()
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);
        Spy misc = Watch(machine, 0x3C2), attr = Watch(machine, 0x3C0);
        Spy crtc = Watch(machine, 0x3D4);   // OUT DX,AX lands here as index then data

        RunInt10(machine, 0x0003);

        Assert.Equal(0x67, misc.Writes.Single().value);
        // 25 CRTC values behind their indices, after the unlock of register 11h.
        var indices = crtc.Writes.Where(w => w.port == 0x3D4).Select(w => w.value).ToList();
        var data = crtc.Writes.Where(w => w.port == 0x3D5).Select(w => w.value).ToList();
        Assert.Equal(26, indices.Count);
        Assert.Equal(0x11, indices[0]);
        Assert.Equal(0x5F, data[1]);          // horizontal total, first table value
        Assert.Equal(0x28, data[0x13 + 1]);
        // Attribute controller: 21 index/data pairs, then 20h to turn the display back on.
        Assert.Equal(43, attr.Writes.Count);
        Assert.Equal(0x20, attr.Writes[^1].value);
        Assert.Equal(0x0C, attr.Writes[2 * 0x10 + 1].value);
    }

    [Fact]
    public void AGraphicsModeSetWritesItsOwnMiscellaneousOutput()
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);
        Spy misc = Watch(machine, 0x3C2);

        RunInt10(machine, 0x0012);

        Assert.Equal(0xE3, misc.Writes.Single().value);
        Assert.Equal(0x12, machine.VideoRegisters.VideoMode);
        Assert.Equal(0xE3, machine.Io.ReadByte(0x3CC));
    }

    [Theory]
    [InlineData(0x101, 640)]
    [InlineData(0x103, 800)]
    [InlineData(0x105, 1024)]
    [InlineData(0x111, 1280)]
    [InlineData(0x117, 2048)]
    public void VesaModeProgramsThePitchThatTheS3DriverReads(int mode, int expectedBytes)
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);
        Spy crtc = Watch(machine, 0x3D4);
        machine.Cpu.State.Bx = (ushort)(mode | 0x8000);

        RunInt10(machine, 0x4F02);

        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        machine.Io.WriteByte(0x3D4, 0x13);
        int offset = machine.Io.ReadByte(0x3D5);
        machine.Io.WriteByte(0x3D4, 0x51);
        offset |= (machine.Io.ReadByte(0x3D5) & 0x30) << 4;
        Assert.Equal(expectedBytes, offset * 8);
        Assert.Contains(crtc.Writes, write => write.port == 0x3D5 && write.value == (byte)(expectedBytes / 8));
    }

    [Fact]
    public void VesaModeSetClearsDirectionForItsTableScanAndRestoresTheCallersFlag()
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);
        machine.Cpu.State.Bx = 0x8101;
        machine.Cpu.Flags.Direction = true;

        RunInt10(machine, 0x4F02);

        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        machine.Io.WriteByte(0x3D4, 0x13);
        Assert.Equal(80, machine.Io.ReadByte(0x3D5));
        Assert.True(machine.Cpu.Flags.Direction);
    }

    [Fact]
    public void OtherVideoFunctionsDoNotTouchThePorts()
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);
        Spy misc = Watch(machine, 0x3C2), seq = Watch(machine, 0x3C4);

        RunInt10(machine, 0x0F00);   // get current mode

        Assert.Empty(misc.Writes);
        Assert.Empty(seq.Writes);
        Assert.Equal(0x03, machine.Cpu.State.GetReg8(0));
    }
}
