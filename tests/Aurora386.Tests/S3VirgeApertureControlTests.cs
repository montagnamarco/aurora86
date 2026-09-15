using Aurora386.Core.Devices.Video;

namespace Aurora386.Tests;

/// <summary>
/// Exercises the 1996 ViRGE's CRTC aperture controls through the VGA ports
/// used by drivers, including the shared PCI address bits and snapshot latches.
/// </summary>
public class S3VirgeApertureControlTests
{
    [Fact]
    public void PciResetReportsTwoMegabytesAndDisabledLinearAccess()
    {
        var registers = CreateVirge();
        var aperture = Assert.IsType<S3VirgeApertureControls>(registers.VirgeAperture);
        Assert.Equal(0x82, Read(registers, 0x36));
        Assert.Equal(0x08, Read(registers, 0x53));
        Assert.Equal(0, Read(registers, 0x58));
        Assert.Equal(0x70, Read(registers, 0x59));
        Assert.Equal(0, Read(registers, 0x5A));
        Assert.Equal(0x70000000u, aperture.RawLinearBase);
        Assert.Equal(0x10000u, aperture.LinearWindowSize);
        Assert.False(aperture.LinearEnabled);
        Assert.True(aperture.NewMmioEnabled);
        Assert.False(aperture.OldMmioEnabled);
        Assert.False(aperture.OldMmioAtB8000);
    }

    [Fact]
    public void LockedWritesDoNotRelocateTheAperture()
    {
        var registers = CreateVirge();
        int changes = 0;
        registers.VirgeAperture!.Changed += () => changes++;
        Write(registers, 0x59, 0x74);
        Assert.Equal(0x70000000u, registers.ApertureBase);
        Assert.Equal(0, changes);

        Write(registers, 0x39, 0xA0);
        Write(registers, 0x59, 0x74);
        Write(registers, 0x59, 0x74);
        Assert.Equal(0x74000000u, registers.ApertureBase);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void PciWritesPreserveTheLowerCrtcAddressBits()
    {
        var registers = CreateVirge();
        var aperture = registers.VirgeAperture!;
        int changes = 0;
        aperture.Changed += () => changes++;
        registers.ApertureBase = 0x71234567;
        Assert.Equal(0x71230000u, registers.ApertureBase);
        aperture.SetPciBaseAddress(0x74000000);
        aperture.SetPciBaseAddress(0x74000000);
        Assert.Equal(0x75230000u, aperture.RawLinearBase);
        Assert.Equal(0x74000000u, aperture.PciBase);
        Assert.Equal(2, changes);
    }

    [Theory]
    [InlineData(0, 0x10000u)]
    [InlineData(1, 0x100000u)]
    [InlineData(2, 0x200000u)]
    [InlineData(3, 0x400000u)]
    public void WindowSizeUsesCrtc58AndRetainsEnable(byte size, uint bytes)
    {
        var registers = CreateVirge();
        Write(registers, 0x39, 0xA0);
        Write(registers, 0x58, (byte)(0x10 | size));
        Assert.Equal(bytes, registers.VirgeAperture!.LinearWindowSize);
        Assert.True(registers.VirgeAperture.LinearEnabled);
        Assert.Equal((byte)(0x10 | size), Read(registers, 0x58));
    }

    [Fact]
    public void ConfigurationStrapsNeedTheA5Key()
    {
        var registers = CreateVirge();
        Write(registers, 0x39, 0xA5);
        Write(registers, 0x36, 0);
        Assert.Equal(0x82, Read(registers, 0x36));
        Write(registers, 0x38, 0x48);
        Write(registers, 0x39, 0xA0);
        Write(registers, 0x36, 0);
        Assert.Equal(0x82, Read(registers, 0x36));
        Write(registers, 0x39, 0xA5);
        Write(registers, 0x36, 0);
        Assert.Equal(0x02, Read(registers, 0x36));
        Write(registers, 0x36, 0xFF);
        Assert.Equal(0xFE, Read(registers, 0x36));
    }

    [Fact]
    public void ReservedBitsAreMaskedAndMmioSelectorsRemainIndependent()
    {
        var registers = CreateVirge();
        Write(registers, 0x39, 0xA0);
        Write(registers, 0x53, 0xFF);
        Write(registers, 0x58, 0xFF);
        Assert.Equal(0x7E, Read(registers, 0x53));
        Assert.Equal(0x9B, Read(registers, 0x58));
        Assert.True(registers.VirgeAperture!.NewMmioEnabled);
        Assert.True(registers.VirgeAperture.OldMmioEnabled);
        Assert.True(registers.VirgeAperture.OldMmioAtB8000);
        Write(registers, 0x53, 0x10);
        Assert.False(registers.VirgeAperture.NewMmioEnabled);
        Assert.True(registers.VirgeAperture.OldMmioEnabled);
        Assert.False(registers.VirgeAperture.OldMmioAtB8000);
    }

    [Fact]
    public void RestoreReportsOneCompleteApertureState()
    {
        var registers = CreateVirge();
        Write(registers, 0x39, 0xA0);
        Write(registers, 0x53, 0x38);
        Write(registers, 0x58, 0x12);
        registers.ApertureBase = 0x75230000;
        byte[] snapshot = registers.CaptureRegisters();
        Write(registers, 0x53, 0);
        Write(registers, 0x58, 0);
        registers.ApertureBase = 0x78000000;

        int changes = 0;
        registers.VirgeAperture!.Changed += () =>
        {
            changes++;
            Assert.Equal(0x38, Read(registers, 0x53));
            Assert.Equal(0x12, Read(registers, 0x58));
            Assert.Equal(0x75230000u, registers.ApertureBase);
            Assert.Equal(0x200000u, registers.VirgeAperture.LinearWindowSize);
        };
        registers.RestoreRegisters(snapshot);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void TrioKeepsItsFixedAperture()
    {
        var registers = new S3VgaRegisters { ApertureBase = 0x01000000 };
        Assert.Null(registers.VirgeAperture);
        Write(registers, 0x39, 0xA0);
        Write(registers, 0x58, 0);
        Write(registers, 0x59, 0x74);
        Assert.Equal(0x12, Read(registers, 0x58));
        Assert.Equal(1, Read(registers, 0x59));
        Assert.Equal(0x01000000u, registers.ApertureBase);
    }

    private static S3VgaRegisters CreateVirge()
    {
        return new S3VgaRegisters(S3VideoIdentity.Virge325);
    }

    private static byte Read(VgaRegisters registers, byte index)
    {
        registers.WriteByte(0x3D4, index);
        return registers.ReadByte(0x3D5);
    }

    private static void Write(VgaRegisters registers, byte index, byte value)
    {
        registers.WriteByte(0x3D4, index);
        registers.WriteByte(0x3D5, value);
    }
}
