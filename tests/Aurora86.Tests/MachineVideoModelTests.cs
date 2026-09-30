using Aurora86.Core;
using Aurora86.Core.Configuration;
using Aurora86.Core.Cpu;
using Aurora86.Core.Devices.Video;
using Aurora86.Core.Firmware;

namespace Aurora86.Tests;

/// <summary>
/// Exercises machine-level selection of the 1995 Trio64V+ and original 1996
/// ViRGE 86C325 through PCI, VGA ports, the real INT 10h ROM path and CPU MMIO.
/// ViRGE VBE modes are offered only when their register program exists.
/// </summary>
public sealed class MachineVideoModelTests
{
    private const uint TriangleCommand = 0x83000008;

    [Fact]
    public void VideoModelValuesAreStableAndUnknownModelsAreRejected()
    {
        Assert.Equal(0, (int)VmVideoModel.S3Trio64VPlus);
        Assert.Equal(1, (int)VmVideoModel.S3Virge325);
        Assert.Equal(2, (int)VmVideoModel.AuroraGpu);

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new Machine(videoModel: (VmVideoModel)3));

        Assert.Equal("videoModel", error.ParamName);
    }

    [Fact]
    public void DefaultMachineRetainsTheTrioIdentityAndAcceleratorPorts()
    {
        using var machine = new Machine();

        Assert.Equal(VmVideoModel.S3Trio64VPlus, machine.VideoModel);
        Assert.Null(machine.Virge);
        Assert.NotNull(machine.TrioAccelerator);
        Assert.Same(machine.TrioAccelerator, machine.Accelerator);
        Assert.Same(machine.Video, machine.Display);
        Assert.Equal(0x5333u, machine.Pci.ReadConfiguration(0x0020, 0, 2));
        Assert.Equal(0x8811u, machine.Pci.ReadConfiguration(0x0020, 2, 2));
        Assert.Equal(0u, machine.Io.ReadWord(0x9AE8));
        Assert.Equal(0u, machine.Pci.ReadConfiguration(0x0020, 4, 2));

        // Only I/O space, memory space and VGA palette snoop are implemented.
        machine.Pci.WriteConfiguration(0x0020, 4, 2, 0xFFFF);
        Assert.Equal(0x0023u, machine.Pci.ReadConfiguration(0x0020, 4, 2));

        // POST enables the two decoders like a system BIOS does.
        machine.RunInstructions(50);

        Assert.Equal(0x0003u, machine.Pci.ReadConfiguration(0x0020, 4, 2));
    }

    [Fact]
    public void VirgePostEnablesPciIoAndMemoryBeforeStandardVgaText()
    {
        using var machine = StartedVirge();

        Assert.Equal(VmVideoModel.S3Virge325, machine.VideoModel);
        Assert.NotNull(machine.Virge);
        Assert.Null(machine.TrioAccelerator);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => _ = machine.Accelerator);
        Assert.Contains("Trio64V+", error.Message);
        Assert.Contains("ViRGE", error.Message);
        Assert.Same(machine.Virge.VideoMemory, machine.VideoMemory);
        Assert.Same(machine.Virge.Registers, machine.VideoRegisters);
        Assert.Same(machine.Virge.Display, machine.Display);
        Assert.Equal(0x5631u, machine.Pci.ReadConfiguration(0x0020, 2, 2));
        Assert.Equal(3u, machine.Pci.ReadConfiguration(0x0020, 4, 2));
        Assert.Equal(0xFFFFu, machine.Io.ReadWord(0x9AE8));

        RunCpuProgram(machine,
            0xB4, 0x0E,             // MOV AH,0Eh: teletype output.
            0xB0, (byte)'V',
            0xCD, 0x10);

        Assert.Equal((byte)'V', machine.Memory.ReadByte(0xB8000));
        Assert.Equal((byte)'V', machine.VideoMemory.ReadLinear(0x8000));
        Assert.StartsWith("V", machine.GetScreenText());
        machine.Display.CursorBlinkOn = false;
        Assert.True(machine.Display.Render(force: true));
        Assert.Equal(720, machine.Display.Width);
        Assert.Equal(400, machine.Display.Height);
    }

    [Fact]
    public void VirgeVbeFailureDoesNotProgramVgaOrVideoMemoryThroughInt10h()
    {
        using var machine = StartedVirge();
        const uint callerBuffer = 0x50000;
        machine.Memory.WriteDword(callerBuffer, 0xA5A5A5A5);
        byte[] registersBefore = machine.VideoRegisters.CaptureRegisters();
        byte[] videoMemoryBefore = machine.VideoMemory.CaptureMemory();

        RunCpuProgram(machine,
            0xB8, 0x02, 0x4F,       // MOV AX,4F02h: set VBE mode.
            0xBB, 0x12, 0x41,       // MOV BX,4112h: 640x480 RGB888, linear.
            0xBF, 0x00, 0x00,       // MOV DI,0; buffer is irrelevant to 4F02h.
            0xCD, 0x10);

        Assert.Equal(0x014F, machine.Cpu.State.Ax);
        Assert.Equal(registersBefore, machine.VideoRegisters.CaptureRegisters());
        Assert.Equal(videoMemoryBefore, machine.VideoMemory.CaptureMemory());
        Assert.Equal(0xA5A5A5A5u, machine.Memory.ReadDword(callerBuffer));
    }

    [Fact]
    public void VirgeUnsupportedModeInfoDoesNotWriteTheCallersBuffer()
    {
        using var machine = StartedVirge();
        const uint callerBuffer = 0x50000;
        machine.Memory.WriteDword(callerBuffer, 0x32454256); // "VBE2".
        machine.Cpu.SetSegment(SegReg.Es, 0x5000);

        RunCpuProgram(machine,
            0xB8, 0x01, 0x4F,       // MOV AX,4F01h: unsupported RGB888 mode information.
            0xB9, 0x12, 0x01,       // MOV CX,0112h.
            0xBF, 0x00, 0x00,       // MOV DI,0.
            0xCD, 0x10);

        Assert.Equal(0x014F, machine.Cpu.State.Ax);
        Assert.Equal(0x32454256u, machine.Memory.ReadDword(callerBuffer));
    }

    [Fact]
    public void VirgeBarRelocationUpdatesMachineFirmwareAddresses()
    {
        using var machine = StartedVirge();

        Assert.Equal(0x70000000u, machine.LinearFrameBufferBase);
        Assert.Equal(0x71000000u, machine.MmioBase);

        machine.Pci.WriteConfiguration(0x0020, 0x10, 4, 0x74000000);

        Assert.Equal(0x74000000u, machine.LinearFrameBufferBase);
        Assert.Equal(0x75000000u, machine.MmioBase);
    }

    [Fact]
    public void CpuTriangleWritesTheVirgeMemoryRenderedByTheMachineDisplay()
    {
        using var machine = StartedVirge();

        // Fixture display timing only: ViRGE RGB888 VBE remains unsupported.
        // RGB888 makes the engine's BGR bytes visible without a palette oracle.
        for (uint offset = 0; offset < VgaMemory.TotalSize; offset++)
        {
            machine.VideoMemory.WriteLinear(offset, 0);
        }
        machine.VideoRegisters.SetVesaMode(VesaModes.Get(0x112, VgaMemory.TotalSize), true);

        WriteMmio(machine, 0xB4D8, 0);
        WriteMmio(machine, 0xB4E4, 1920u << 16);
        WriteMmio(machine, 0xB54C, 0x20001000);
        WriteMmio(machine, 0xB550, 0x00007000);
        WriteMmio(machine, 0xB574, 0x00100000);
        WriteMmio(machine, 0xB56C, 0x00500000);
        WriteMmio(machine, 0xB568, 0xFFF00000);
        WriteMmio(machine, 0xB578, 4);
        WriteMmio(machine, 0xB57C, 0x80040000);

        // Submit the command through the processor's real 32-bit MMIO path.
        uint commandAddress = machine.MmioBase + 0xB500;
        RunCpuProgram(machine,
            0x66, 0x67, 0xC7, 0x05,
            (byte)commandAddress,
            (byte)(commandAddress >> 8),
            (byte)(commandAddress >> 16),
            (byte)(commandAddress >> 24),
            unchecked((byte)TriangleCommand),
            unchecked((byte)(TriangleCommand >> 8)),
            unchecked((byte)(TriangleCommand >> 16)),
            unchecked((byte)(TriangleCommand >> 24)));

        var expected = new byte[VgaMemory.TotalSize];
        foreach ((int x, int y) in new[]
                 {
                     (1, 4), (2, 4), (3, 4), (4, 4),
                     (1, 3), (2, 3), (3, 3),
                     (1, 2), (2, 2),
                     (1, 1)
                 })
        {
            int address = y * 1920 + x * 3;
            expected[address] = 0x20;
            expected[address + 1] = 0x40;
            expected[address + 2] = 0xE0;
        }

        Assert.Equal(expected, machine.VideoMemory.CaptureMemory());
        Assert.True(machine.Display.Render(force: true));
        Assert.Equal(0xFF2040E0u, machine.Display.Framebuffer[4 * 640 + 1]);
        Assert.Equal(0xFF000000u, machine.Display.Framebuffer[4 * 640 + 5]);
    }

    [Fact]
    public void DisablingVesaChangesOnlyTheRomModeProgramEntry()
    {
        byte[] defaultRom = BiosRom.Build();
        byte[] explicitDefaultRom = BiosRom.Build(enableVesa: true);
        byte[] vgaOnlyRom = BiosRom.Build(enableVesa: false);

        Assert.Equal(defaultRom, explicitDefaultRom);
        // Captured before ViRGE VBE work; the Trio firmware is an established
        // guest dependency, not merely equal to another call of the new builder.
        // Recaptured when $PIR grew from four slots to six (PCI devices 6 and
        // 7: the ROMs differ only at F1AA6h-F1B1Fh) and again to seven
        // (device 8: only at F1AA6h-F1B2Fh), each time inside the routing
        // table, measured with a byte-for-byte compare. Recaptured once more
        // when the project was renamed Aurora86: only the nine bytes of the
        // fixed-width adapter name at F1806h-F180Eh differ.
        Assert.Equal("438531D9B9FABB621C85BDA8E91A605B5D6563141DC41D39567661A6A360F5A5",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(defaultRom)));
        Assert.Equal(0xC3, vgaOnlyRom[BiosRom.Entry.VesaProgram]);
        Assert.Equal(
            new[] { (int)BiosRom.Entry.VesaProgram },
            defaultRom.Select((value, index) => (value, index))
                .Where(item => item.value != vgaOnlyRom[item.index])
                .Select(item => item.index));
    }

    private static Machine StartedVirge()
    {
        var machine = new Machine(videoModel: VmVideoModel.S3Virge325);
        machine.RunInstructions(200);
        return machine;
    }

    /// <summary>Runs a short real-mode program after POST and stops at an appended HLT.</summary>
    private static void RunCpuProgram(Machine machine, params byte[] program)
    {
        const uint codeAddress = 0x20100;
        program.CopyTo(machine.Memory.GetRamSpan(codeAddress, program.Length));
        machine.Memory.WriteByte(codeAddress + (uint)program.Length, 0xF4);
        machine.Cpu.SetSegment(SegReg.Cs, 0x2000);
        machine.Cpu.State.Eip = 0x0100;
        machine.Cpu.SetSegment(SegReg.Ds, 0);
        machine.Cpu.State.Segments[(int)SegReg.Ds].Limit = uint.MaxValue;
        machine.Cpu.SetSegment(SegReg.Ss, 0x3000);
        machine.Cpu.State.Esp = 0xFFFE;
        machine.Cpu.State.Halted = false;

        for (int instruction = 0; instruction < 20_000 && !machine.Cpu.State.Halted; instruction++)
        {
            machine.RunInstructions(1);
        }

        Assert.True(machine.Cpu.State.Halted, "The CPU program did not reach its HLT.");
        Assert.Equal(0x0100u + (uint)program.Length + 1, machine.Cpu.State.Eip);
    }

    private static void WriteMmio(Machine machine, uint register, uint value)
    {
        machine.Memory.WriteDword(machine.MmioBase + register, value);
    }
}
