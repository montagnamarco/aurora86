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

    private static void RunInt10(Machine machine, ushort ax, ushort? bx = null)
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
        if (bx is { } requestedMode)
        {
            machine.Cpu.State.Bx = requestedMode;
        }
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
        // 25 CRTC values behind their indices, the unlock of register 11h,
        // and the cursor address written by the ROM text completion.
        var indices = crtc.Writes.Where(w => w.port == 0x3D4).Select(w => w.value).ToList();
        var data = crtc.Writes.Where(w => w.port == 0x3D5).Select(w => w.value).ToList();
        Assert.Equal(28, indices.Count);
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
    [InlineData(0x112, 1920)]
    [InlineData(0x117, 2048)]
    [InlineData(0x115, 2400)]
    [InlineData(0x220, 2560)]
    [InlineData(0x221, 3200)]
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

    [Theory]
    [InlineData(0x8220, 0x220, 0x40, 2560)]
    [InlineData(0x8221, 0x221, 0x90, 3200)]
    public void S3OemModeRequestsPreserveVideoMemoryAndReportTheSelectedMode(
        ushort request,
        ushort expectedMode,
        byte expectedCrtc13,
        int expectedPitch)
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);

        // Bit 15 is the driver's request to preserve its existing surface.
        machine.VideoMemory.WriteLinear(0, 0x5A);

        RunInt10(machine, 0x4F02, request);

        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        Assert.Equal(expectedMode, machine.VideoRegisters.VesaMode!.Value.Number);
        Assert.Equal(0x5A, machine.VideoMemory.ReadLinear(0));

        // Read the three S3 CRTC registers just as the Windows driver does.
        machine.Io.WriteByte(0x3D4, 0x13);
        byte lowPitch = machine.Io.ReadByte(0x3D5);
        machine.Io.WriteByte(0x3D4, 0x51);
        byte highPitch = machine.Io.ReadByte(0x3D5);
        machine.Io.WriteByte(0x3D4, 0x43);
        byte alternatePitch = machine.Io.ReadByte(0x3D5);

        int pitchOffset = lowPitch | ((highPitch & 0x30) << 4);
        Assert.Equal(expectedCrtc13, lowPitch);
        Assert.Equal(0x10, highPitch);
        Assert.Equal(0, alternatePitch & 0x04);
        Assert.Equal(expectedPitch, pitchOffset * 8);

        RunInt10(machine, 0x4F03);

        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        Assert.Equal(expectedMode, machine.Cpu.State.Bx);
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
