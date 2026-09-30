using Aurora86.Core;
using Aurora86.Core.Configuration;
using Aurora86.Core.Cpu;
using Aurora86.Core.Devices.Video;

namespace Aurora86.Tests;

/// <summary>
/// Drives the 1996 ViRGE's CR31/35/51/66/6A CPU page controls through ports.
/// DOS VBE firmware uses the enhanced 64 KB window before any renderer mode
/// metadata exists; the hardware page must also survive a machine snapshot.
/// </summary>
public sealed class S3VirgeBankingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    public void Cr31Bit3OverridesGr6WithExactly64Kb(int graphicsMap)
    {
        using Machine machine = CreateMachine();
        machine.Memory.WriteByte(0xB0000, 0x71);
        EnableBanking(machine);
        machine.Io.WriteWord(0x3CE, (ushort)((graphicsMap << 8) | 6));

        machine.Memory.WriteByte(0xA0000, 0x25);
        machine.Memory.WriteByte(0xAFFFF, 0x56);

        Assert.Equal(0x25, machine.VideoMemory.ReadLinear(0));
        Assert.Equal(0x56, machine.VideoMemory.ReadLinear(0xFFFF));
        Assert.Equal(0x71, machine.Memory.ReadByte(0xB0000));
        Assert.Null(machine.VideoRegisters.VesaMode);
        Assert.Null(machine.VideoMemory.Engine);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void Cr35AndCr51Select64KbPagesRegardlessOfChain4(int sequencerMemoryMode)
    {
        using Machine machine = CreateMachine();
        EnableBanking(machine);
        machine.Io.WriteWord(0x3C4, (ushort)((sequencerMemoryMode << 8) | 4));
        Crtc(machine, 0x35, 0x0A);
        Crtc(machine, 0x51, 0x04); // Page 1Ah, including bank bit four.

        machine.Memory.WriteDword(0xAB000, 0x76543210);

        Assert.Equal(0x76543210u, machine.Memory.ReadDword(0xAB000));
        Assert.Equal(0x10, machine.VideoMemory.ReadLinear(0x1AB000));
        Assert.Equal(0x76, machine.VideoMemory.ReadLinear(0x1AB003));
        Assert.Equal(0, machine.VideoMemory.ReadLinear(0x0B000));
    }

    [Fact]
    public void Cr6ANonzeroOverridesOldBankAndCr31Bit0DisablesAllOffsets()
    {
        using Machine machine = CreateMachine();
        EnableBanking(machine);
        Crtc(machine, 0x35, 0x0B);
        Crtc(machine, 0x51, 0x04);
        Crtc(machine, 0x6A, 0x04);
        machine.Memory.WriteByte(0xA0100, 0x12);
        Assert.Equal(0x12, machine.VideoMemory.ReadLinear(0x40100));

        Crtc(machine, 0x6A, 0);
        machine.Memory.WriteByte(0xA0100, 0x34);
        Assert.Equal(0x34, machine.VideoMemory.ReadLinear(0x1B0100));

        Crtc(machine, 0x31, 8);
        machine.VideoRegisters.VesaWindowBank = 7;
        machine.Memory.WriteByte(0xA0100, 0x56);
        Assert.Equal(0x56, machine.VideoMemory.ReadLinear(0x100));
        Assert.Equal(0, machine.VideoMemory.ReadLinear(0x70100));
        Assert.Equal(0x0B, machine.VideoRegisters.Crtc(0x35));
    }

    [Fact]
    public void Cr66Bit0IsRequiredForEnhancedWindow()
    {
        using Machine machine = CreateMachine();
        machine.Memory.WriteByte(0xA0000, 0x78);
        Crtc(machine, 0x31, 9);
        Assert.Equal(0x78, machine.Memory.ReadByte(0xA0000));
        Crtc(machine, 0x66, 1);
        machine.Memory.WriteByte(0xA0000, 0x9A);
        Assert.Equal(0x9A, machine.VideoMemory.ReadLinear(0));
        Crtc(machine, 0x66, 0);
        Assert.Equal(0x78, machine.Memory.ReadByte(0xA0000));
    }

    [Fact]
    public void Bank31EndsAtInstalledVramAndAffffDoesNotWrapIntoNextBank()
    {
        using Machine machine = CreateMachine();
        machine.Memory.WriteByte(0xB0000, 0x12);
        EnableBanking(machine);
        Crtc(machine, 0x6A, 31);
        machine.Memory.WriteWord(0xAFFFF, 0x5678);
        Assert.Equal(0x78, machine.VideoMemory.ReadLinear(0x1FFFFF));
        Assert.Equal(0x56, machine.Memory.ReadByte(0xB0000));
        Assert.Equal(0, machine.VideoMemory.ReadLinear(0));

        // Uninstalled pages use the existing VRAM bounds guard, not an
        // assumed hardware alias of the populated two megabytes.
        Crtc(machine, 0x6A, 32);
        machine.Memory.WriteByte(0xA0000, 0xAB);
        Assert.Equal(0, machine.VideoMemory.ReadLinear(0));
        Assert.Equal(0x78, machine.VideoMemory.ReadLinear(0x1FFFFF));
    }

    [Fact]
    public void Cr58Bit4ReplacesBankedAccessWithNewLinearAccess()
    {
        using Machine machine = CreateMachine();
        machine.Memory.WriteByte(0xA0000, 0x73);
        EnableBanking(machine);
        Crtc(machine, 0x35, 3);
        machine.Memory.WriteByte(0xA0000, 0x28);
        Crtc(machine, 0x58, 0x12);

        Assert.Equal(0x73, machine.Memory.ReadByte(0xA0000));
        Assert.Equal(0x28, machine.Memory.ReadByte(machine.LinearFrameBufferBase + 0x30000));
        machine.Memory.WriteByte(machine.LinearFrameBufferBase + 0x30000, 0x42);
        Crtc(machine, 0x58, 0);
        Assert.Equal(0x42, machine.Memory.ReadByte(0xA0000));
    }

    [Fact]
    public void NewMmioIsNotRequiredForBankingButOldMmioRemainsUnsupported()
    {
        using Machine machine = CreateMachine();
        machine.Memory.WriteByte(0xA0000, 0x73);
        EnableBanking(machine);
        Crtc(machine, 0x53, 0);
        machine.Memory.WriteByte(0xA0000, 0x28);
        Assert.Equal(0x28, machine.VideoMemory.ReadLinear(0));
        Crtc(machine, 0x53, 0x10);
        Assert.Equal(0x73, machine.Memory.ReadByte(0xA0000));
    }

    [Fact]
    public void Cr58Bit4DoesNotRemoveTheStandardB8000TextWindow()
    {
        using Machine machine = CreateMachine();
        Crtc(machine, 0x58, 0x12);
        machine.Memory.WriteWord(0xB8000, 0x0756);
        Assert.Equal(0x56, machine.VideoMemory.ReadLinear(0x8000));
        Assert.Equal(0x07, machine.VideoMemory.ReadLinear(0x18000));
    }

    [Fact]
    public void ClearingCr31Bit3RestoresTheSelectedTextWindow()
    {
        using Machine machine = CreateMachine();
        machine.Memory.WriteByte(0xA0000, 0x73);
        EnableBanking(machine);
        Crtc(machine, 0x31, 0);
        machine.Memory.WriteWord(0xB8000, 0x0742);
        Assert.Equal(0x42, machine.VideoMemory.ReadLinear(0x8000));
        Assert.Equal(0x07, machine.VideoMemory.ReadLinear(0x18000));
        Assert.Equal(0x73, machine.Memory.ReadByte(0xA0000));
    }

    [Fact]
    public void PciMemoryDisableRevealsRamAndSnapshotRestoresTheBank()
    {
        using Machine machine = CreateMachine();
        machine.Memory.WriteByte(0xA0000, 0x73);
        EnableBanking(machine);
        Crtc(machine, 0x6A, 0x1F);
        machine.Memory.WriteDword(0xAFFFC, 0x12345678);
        MachineSnapshot snapshot = MachineSnapshot.Capture(machine);
        machine.Pci.WriteConfiguration(0x20, 4, 2, 1);
        Assert.Equal(0x73, machine.Memory.ReadByte(0xA0000));
        Crtc(machine, 0x6A, 2);

        snapshot.RestoreTo(machine);

        Assert.Equal(0x1F, machine.VideoRegisters.Crtc(0x6A));
        Assert.Equal(0x12345678u, machine.Memory.ReadDword(0xAFFFC));
        Assert.Equal(0x78, machine.VideoMemory.ReadLinear(0x1FFFFC));
    }

    [Fact]
    public void CpuMovWritesTheBankSelectedThroughMmioVgaAliases()
    {
        using Machine machine = CreateMachine();
        EnableBanking(machine);
        machine.Memory.WriteWord(machine.MmioBase + 0x83D4, 0x036A);
        byte[] program = [0x67, 0xC7, 0x05, 0x10, 0x00, 0x0A, 0x00, 0x34, 0x12];
        program.CopyTo(machine.Memory.GetRamSpan(0x1000, program.Length));
        machine.Cpu.State.LoadSegmentRealMode(SegReg.Cs, 0);
        machine.Cpu.State.LoadSegmentRealMode(SegReg.Ds, 0);
        machine.Cpu.State.Segments[(int)SegReg.Ds].Limit = uint.MaxValue;
        machine.Cpu.State.Eip = 0x1000;
        machine.Cpu.State.Halted = false;

        machine.Cpu.Step();

        Assert.Equal(0x1009u, machine.Cpu.State.Eip);
        Assert.Equal(0x34, machine.VideoMemory.ReadLinear(0x30010));
        Assert.Equal(0x12, machine.VideoMemory.ReadLinear(0x30011));
    }

    private static Machine CreateMachine()
    {
        var machine = new Machine(videoModel: VmVideoModel.S3Virge325);
        machine.Memory.A20Enabled = true;
        machine.Pci.WriteConfiguration(0x20, 4, 2, 3);
        Crtc(machine, 0x38, 0x48);
        Crtc(machine, 0x39, 0xA0);
        return machine;
    }

    private static void EnableBanking(Machine machine)
    {
        Crtc(machine, 0x66, 1);
        Crtc(machine, 0x31, 9);
    }

    private static void Crtc(Machine machine, byte index, byte value)
    {
        machine.Io.WriteWord(0x3D4, (ushort)((value << 8) | index));
    }
}
