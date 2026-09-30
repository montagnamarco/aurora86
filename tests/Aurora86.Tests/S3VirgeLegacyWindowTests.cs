using Aurora86.Core.Cpu;
using Aurora86.Core.Devices.Pci;
using Aurora86.Core.Devices.Video;
using Aurora86.Core.IO;
using Aurora86.Core.Memory;

namespace Aurora86.Tests;

/// <summary>
/// Drives the ViRGE's standard VGA window through PCI and VGA ports. Legacy
/// memory addresses must retain their A0000-relative plane layout, including
/// the character/attribute pair at B8000, while released windows reveal RAM.
/// </summary>
public class S3VirgeLegacyWindowTests
{
    [Fact]
    public void PciMemoryEnableControlsTextWindowThroughCpuAndDisplay()
    {
        using var fixture = new Fixture();
        fixture.Memory.WriteWord(0xB8000, 0x8877);
        fixture.Command(3);
        fixture.Card.Display.CursorBlinkOn = false;
        var cpu = new Cpu(fixture.Memory, fixture.Io);
        cpu.State.LoadSegmentRealMode(SegReg.Cs, 0);
        cpu.State.LoadSegmentRealMode(SegReg.Ds, 0);
        cpu.State.Segments[(int)SegReg.Ds].Limit = uint.MaxValue;
        cpu.State.Eip = 0x1000;
        cpu.State.Halted = false;

        // MOV word [000B8000],1420h: space, red foreground, blue background.
        byte[] code = [0x67, 0xC7, 0x05, 0x00, 0x80, 0x0B, 0x00, 0x20, 0x14];
        code.CopyTo(fixture.Memory.GetRamSpan(0x1000, code.Length));
        cpu.Step();

        Assert.Equal(0x1009u, cpu.State.Eip);
        Assert.Equal(0x1420, fixture.Memory.ReadWord(0xB8000));
        Assert.Equal(0x20, fixture.Card.VideoMemory.ReadLinear(0x8000));
        Assert.Equal(0x14, fixture.Card.VideoMemory.ReadLinear(0x18000));
        Assert.True(fixture.Card.Display.Render(true));
        // Mode 03h resets SR1 bit 0 to zero: 80 nine-dot character cells.
        Assert.Equal(720, fixture.Card.Display.Width);
        Assert.Equal(400, fixture.Card.Display.Height);
        Assert.Equal(0xFFAA0000u, fixture.Card.Display.Framebuffer[0]);
        fixture.Command(1);
        Assert.Equal(0x8877, fixture.Memory.ReadWord(0xB8000));
    }

    [Theory]
    [InlineData(0, 0xA0000u, 0xC0000u)]
    [InlineData(1, 0xA0000u, 0xB0000u)]
    [InlineData(2, 0xB0000u, 0xB8000u)]
    [InlineData(3, 0xB8000u, 0xA0000u)]
    public void GraphicsMapSelectClaimsOnlyTheSelectedWindow(byte select, uint selected, uint outside)
    {
        using var fixture = new Fixture();
        fixture.Memory.WriteByte(selected, 0x55);
        fixture.Memory.WriteByte(outside, 0x66);
        fixture.Command(3);
        fixture.Io.WriteWord(0x3CE, (ushort)(6 | (select << 10)));
        fixture.Memory.WriteByte(selected, 0x42);
        Assert.Equal(0x42, fixture.Memory.ReadByte(selected));
        Assert.Equal(0x66, fixture.Memory.ReadByte(outside));
        fixture.Command(0);
        Assert.Equal(0x55, fixture.Memory.ReadByte(selected));
    }

    [Fact]
    public void MmioGraphicsWritesRelocateTheSameLegacyDecoder()
    {
        using var fixture = new Fixture();
        fixture.Memory.WriteByte(0xB8000, 0x77);
        fixture.Memory.WriteByte(0xA0000, 0x88);
        fixture.Command(2); // PIO disabled, MMIO remains available.
        fixture.Memory.WriteWord(0x710083CE, 0x0406);
        fixture.Memory.WriteByte(0xA0000, 0x44);
        Assert.Equal(0x44, fixture.Card.VideoMemory.ReadLinear(0));
        Assert.Equal(0x77, fixture.Memory.ReadByte(0xB8000));
        fixture.Io.WriteWord(0x3CE, 0x0C06); // Rejected by PCI IO gate.
        Assert.Equal(0x44, fixture.Memory.ReadByte(0xA0000));
        fixture.Memory.WriteWord(0x710083CE, 0x0C06);
        Assert.Equal(0x88, fixture.Memory.ReadByte(0xA0000));
        fixture.Memory.WriteWord(0xB8000, 0x0758);
        Assert.Equal(0x0758, fixture.Memory.ReadWord(0xB8000));
    }

    [Fact]
    public void BarAtZeroWinsOverLegacyAndRelocationRevealsItsCurrentWindow()
    {
        using var fixture = new Fixture();
        fixture.Command(3);
        fixture.Memory.WriteWord(0xB8000, 0x0758);
        fixture.Pci.WriteConfiguration(0x0020, 0x10, 4, 0);
        Assert.Equal(0xFFFF, fixture.Memory.ReadWord(0xB8000));
        fixture.Io.WriteWord(0x3CE, 0x0406);
        fixture.Io.WriteWord(0x3CE, 0x0C06);
        fixture.Memory.WriteWord(0xB8000, 0x1234);
        Assert.Equal(0x58, fixture.Card.VideoMemory.ReadLinear(0x8000));
        fixture.Pci.WriteConfiguration(0x0020, 0x10, 4, 0x74000000);
        Assert.Equal(0x0758, fixture.Memory.ReadWord(0xB8000));
    }

    [Fact]
    public void UnsupportedEnhancedLegacyDoesNotCallTrioEngine()
    {
        using var fixture = new Fixture();
        fixture.Memory.WriteByte(0xB8000, 0x88);
        fixture.Command(3);
        var reports = new List<S3VirgeUnsupportedAccess>();
        fixture.Card.UnsupportedAccess += reports.Add;
        fixture.Io.WriteWord(0x3D4, 0x4838);
        fixture.Io.WriteWord(0x3D4, 0x0831);
        fixture.Io.WriteWord(0x3D4, 0x0831);
        Assert.Single(reports);
        Assert.Equal(S3VirgeUnsupportedReason.EnhancedLegacy, reports[0].Reason);
        Assert.Equal(0x88, fixture.Memory.ReadByte(0xB8000));
        Assert.Null(fixture.Card.VideoMemory.Engine);
        Assert.Equal(0x3000u, fixture.Memory.ReadDword(0x71008504));
        fixture.Io.WriteWord(0x3D4, 0x0031);
        fixture.Memory.WriteByte(0xB8000, 0x55);
        Assert.Equal(0x55, fixture.Card.VideoMemory.ReadLinear(0x8000));
    }

    [Fact]
    public void OldMmioDisablesLegacyWithoutClaimingItsUnsupportedProtocol()
    {
        using var fixture = new Fixture();
        fixture.Memory.WriteByte(0xB8000, 0x88);
        fixture.Command(3);
        fixture.Io.WriteWord(0x3D4, 0xA039);
        fixture.Io.WriteWord(0x3D4, 0x1853);
        Assert.Equal(0x88, fixture.Memory.ReadByte(0xB8000));
        fixture.Io.WriteWord(0x3D4, 0x0853);
        fixture.Memory.WriteByte(0xB8000, 0x55);
        Assert.Equal(0x55, fixture.Card.VideoMemory.ReadLinear(0x8000));
    }

    [Fact]
    public void HostSynchronizationAndModeChangesRebuildLegacyDecoding()
    {
        using var fixture = new Fixture();
        fixture.Memory.WriteByte(0xB8000, 0x88);
        fixture.Command(3);
        fixture.Card.Registers.SetGraphics(6, 4);
        fixture.Card.SynchronizeMappings();
        Assert.Equal(0x88, fixture.Memory.ReadByte(0xB8000));
        fixture.Card.Registers.VideoMode = 3;
        fixture.Card.Registers.NotifyModeChanged();
        fixture.Memory.WriteByte(0xB8000, 0x55);
        Assert.Equal(0x55, fixture.Card.VideoMemory.ReadLinear(0x8000));
    }

    [Fact]
    public void DisposeRestoresLegacyRamAndUnsubscribesModeChanges()
    {
        using var fixture = new Fixture();
        fixture.Memory.WriteWord(0xB8000, 0x8877);
        fixture.Command(3);
        fixture.Memory.WriteWord(0xB8000, 0x0758);
        fixture.Card.Dispose();
        fixture.Card.Dispose();
        fixture.Card.Registers.VideoMode = 3;
        fixture.Card.Registers.NotifyModeChanged();
        Assert.Equal(0x8877, fixture.Memory.ReadWord(0xB8000));
        Assert.Equal(uint.MaxValue, fixture.Memory.ReadDword(0x71008504));
        Assert.Throws<ObjectDisposedException>(fixture.Card.SynchronizeMappings);
    }

    [Fact]
    public void DisposingTheMemoryBusFirstLeavesBothDecoderHandlesSafe()
    {
        var fixture = new Fixture();
        fixture.Command(3);
        fixture.Memory.Dispose();
        fixture.Card.Dispose();
        fixture.Card.Dispose();
        fixture.Card.Registers.NotifyModeChanged();
        fixture.Dispose();
    }

    /// <summary>Uses standard buses with no Machine or firmware side effects.</summary>
    private sealed class Fixture : IDisposable
    {
        public PhysicalMemoryBus Memory { get; } = new(0x200000, 1UL << 32) { A20Enabled = true };
        public IoBus Io { get; } = new();
        public PciBus Pci { get; } = new();
        public S3VirgeCard Card { get; }

        public Fixture()
        {
            Pci.Register(Io);
            Card = new S3VirgeCard(Memory, Io, Pci);
        }

        public void Command(uint value)
        {
            Pci.WriteConfiguration(0x0020, 4, 2, value);
        }

        public void Dispose()
        {
            Card.Dispose();
            Memory.Dispose();
        }
    }
}
