using Aurora86.Core;

namespace Aurora86.Tests;

public class VgaFeatureControlTests
{
    [Theory]
    [InlineData(0x03, 0x3DA, 0x3BA)]
    [InlineData(0x07, 0x3BA, 0x3DA)]
    public void FeatureControlUsesTheSelectedIoAddressAndOnlyVsslIsWritable(byte mode, ushort active, ushort inactive)
    {
        using var machine = new Machine();
        machine.VideoServices.SetVideoMode(mode);
        Assert.Equal(0, machine.Io.ReadByte(0x3CA));
        machine.Io.WriteByte(inactive, 0xFF);
        Assert.Equal(0, machine.Io.ReadByte(0x3CA));
        machine.Io.WriteByte(active, 0xFF);
        Assert.Equal(8, machine.Io.ReadByte(0x3CA));
        machine.Io.WriteByte(0x3CA, 0); // read-only address
        Assert.Equal(8, machine.Io.ReadByte(0x3CA));
        machine.Io.WriteByte(active, 0xF7);
        Assert.Equal(0, machine.Io.ReadByte(0x3CA));
    }

    [Fact]
    public void AFeatureControlWriteDoesNotResetTheAttributeFlipFlopOrChangeRetraceStatus()
    {
        using var machine = new Machine();
        byte status = machine.Io.ReadByte(0x3DA);
        machine.Io.WriteByte(0x3C0, 0x12);
        machine.Io.WriteByte(0x3DA, 8);
        machine.Io.WriteByte(0x3C0, 5);
        Assert.Equal(5, machine.Io.ReadByte(0x3C1));
        Assert.Equal(status, machine.Io.ReadByte(0x3DA));
    }

    [Fact]
    public void FeatureControlSurvivesRegisterSnapshotsIncludingLegacyZeroPadding()
    {
        using var machine = new Machine();
        byte[] legacy = machine.VideoRegisters.CaptureRegisters();
        machine.Io.WriteByte(0x3DA, 8);
        byte[] saved = machine.VideoRegisters.CaptureRegisters();
        machine.Io.WriteByte(0x3DA, 0);
        machine.VideoRegisters.RestoreRegisters(saved);
        Assert.Equal(8, machine.Io.ReadByte(0x3CA));
        machine.VideoRegisters.RestoreRegisters(legacy);
        Assert.Equal(0, machine.Io.ReadByte(0x3CA));
    }
}
