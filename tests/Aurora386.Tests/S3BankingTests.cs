using Aurora386.Core;

namespace Aurora386.Tests;

/// <summary>
/// The S3 CPU-base registers which bank the 64 KB VGA aperture.
///
/// VBE has a software bank number of its own, but an S3 Windows driver writes
/// the hardware CRTC registers and then uploads cursor and icon pixels through
/// A0000h. CR35 supplies four legacy bits and CR51 extends them; the later
/// CR6A register is an independent six-bit override when it is non-zero.
/// </summary>
public class S3BankingTests
{
    private static Machine InVesaMode()
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        machine.Cpu.State.Halted = false;
        machine.Cpu.State.Ax = 0x4F02;
        machine.Cpu.State.Bx = 0x101;
        machine.VideoServices.Handle(machine.Cpu);
        return machine;
    }

    private static void WriteCrtc(Machine machine, byte index, byte value)
    {
        machine.Io.WriteByte(0x3D4, index);
        machine.Io.WriteByte(0x3D5, value);
    }

    private static byte ReadCrtc(Machine machine, byte index)
    {
        machine.Io.WriteByte(0x3D4, index);
        return machine.Io.ReadByte(0x3D5);
    }

    private static void Unlock(Machine machine)
    {
        WriteCrtc(machine, 0x38, 0x48);
        WriteCrtc(machine, 0x39, 0xA5);
    }

    [Fact]
    public void Cr35SelectsA64KPageForChainedCpuAccesses()
    {
        using Machine machine = InVesaMode();
        Unlock(machine);
        machine.VideoRegisters.SetSequencer(0x04, 0x08);   // Chain-4
        WriteCrtc(machine, 0x31, 0x01);                     // use S3 CPU base
        WriteCrtc(machine, 0x35, 0x04);

        machine.Memory.WriteByte(0xAB000, 0x5A);

        Assert.Equal(0x5A, machine.VideoMemory.ReadLinear(0x4B000));
        Assert.Equal(0x00, machine.VideoMemory.ReadLinear(0x0B000));
        Assert.Equal(0, machine.VideoRegisters.VesaWindowBank);
    }

    [Fact]
    public void Cr51ExtendsTheSameCpuBaseLatch()
    {
        using Machine machine = InVesaMode();
        Unlock(machine);
        machine.VideoRegisters.SetSequencer(0x04, 0x08);
        WriteCrtc(machine, 0x31, 0x01);
        WriteCrtc(machine, 0x35, 0x0A);
        WriteCrtc(machine, 0x51, 0x04);                     // bank bit four

        machine.Memory.WriteByte(0xAB000, 0x3C);

        Assert.Equal(0x3C, machine.VideoMemory.ReadLinear(0x1AB000));
        Assert.Equal(0x00, ReadCrtc(machine, 0x6A));
        Assert.Equal(0x0A, ReadCrtc(machine, 0x35) & 0x0F);
        Assert.Equal(0x04, ReadCrtc(machine, 0x51) & 0x0C);
    }

    [Fact]
    public void ANonZeroCr6AOverridesButDoesNotAlterTheOlderRegisters()
    {
        using Machine machine = InVesaMode();
        Unlock(machine);
        machine.VideoRegisters.SetSequencer(0x04, 0x08);
        WriteCrtc(machine, 0x31, 0x01);
        WriteCrtc(machine, 0x6A, 0x04);

        machine.Memory.WriteByte(0xAB000, 0x7E);
        Assert.Equal(0x7E, machine.VideoMemory.ReadLinear(0x4B000));
        Assert.Equal(0x00, ReadCrtc(machine, 0x35) & 0x0F);

        // CR6A's non-zero value makes the legacy fields ignored. The manual
        // explicitly gives it priority, so this write is retained but does
        // not move the CPU window yet.
        WriteCrtc(machine, 0x35, 0x0B);
        machine.Memory.WriteByte(0xAB000, 0x6D);

        Assert.Equal(0x6D, machine.VideoMemory.ReadLinear(0x4B000));
        Assert.Equal(0x0B, ReadCrtc(machine, 0x35) & 0x0F);
        Assert.Equal(0x04, ReadCrtc(machine, 0x6A) & 0x3F);

        // Clearing CR6A makes the stored legacy bank effective again.
        WriteCrtc(machine, 0x6A, 0x00);
        machine.Memory.WriteByte(0xAB000, 0x52);

        Assert.Equal(0x52, machine.VideoMemory.ReadLinear(0xBB000));
    }

    [Fact]
    public void UnchainedCpuAccessesUseSixteenKilobytePages()
    {
        using Machine machine = InVesaMode();
        Unlock(machine);
        machine.VideoRegisters.SetSequencer(0x04, 0x00);
        WriteCrtc(machine, 0x31, 0x01);
        WriteCrtc(machine, 0x35, 0x04);

        machine.Memory.WriteByte(0xAB000, 0x31);

        Assert.Equal(0x31, machine.VideoMemory.ReadLinear(0x1B000));
        Assert.Equal(0x00, machine.VideoMemory.ReadLinear(0x4B000));
    }

    [Fact]
    public void ASnapshotRestoresTheSelectedS3Page()
    {
        using Machine machine = InVesaMode();
        Unlock(machine);
        machine.VideoRegisters.SetSequencer(0x04, 0x08);
        WriteCrtc(machine, 0x31, 0x01);
        WriteCrtc(machine, 0x35, 0x04);
        MachineSnapshot snapshot = MachineSnapshot.Capture(machine);

        WriteCrtc(machine, 0x35, 0x01);
        snapshot.RestoreTo(machine);
        machine.Memory.WriteByte(0xAB000, 0xD2);

        Assert.Equal(0xD2, machine.VideoMemory.ReadLinear(0x4B000));
        Assert.Equal(0x00, machine.VideoMemory.ReadLinear(0x1B000));
    }
}
