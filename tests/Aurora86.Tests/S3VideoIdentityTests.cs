using Aurora86.Core;
using Aurora86.Core.Devices.Pci;
using Aurora86.Core.Devices.Video;
using Aurora86.Core.IO;

namespace Aurora86.Tests;

/// <summary>
/// Checks the two hardware identification paths a mid-nineties display driver
/// reads, keeping chip selection separate from the drawing protocol and BARs.
/// </summary>
public class S3VideoIdentityTests
{
    [Fact]
    public void TrioKeepsItsVerifiedPciAndCrtcIdentity()
    {
        using var machine = new Machine();
        Assert.Equal(0x88115333u, machine.Pci.ReadConfiguration(0x0020, 0, 4));
        Assert.Equal(0x54u, machine.Pci.ReadConfiguration(0x0020, 8, 1));
        Assert.Equal(0x88, ReadCrtc(machine.Io, 0x2D));
        Assert.Equal(0x11, ReadCrtc(machine.Io, 0x2E));
        Assert.Equal(0x00, ReadCrtc(machine.Io, 0x2F));
        Assert.Equal(0xE1, ReadCrtc(machine.Io, 0x30));
        Assert.Equal(machine.LinearFrameBufferBase | 8,
            machine.Pci.ReadConfiguration(0x0020, 0x10, 4));
        Assert.Equal(16u * 1024 * 1024, machine.Memory.RamSize);

        machine.Pci.WriteConfiguration(0x0020, 0x10, 4, uint.MaxValue);
        Assert.Equal(0xFFE00008u, machine.Pci.ReadConfiguration(0x0020, 0x10, 4));
    }

    [Fact]
    public void VirgeIdentityMatchesPciAndCrtc()
    {
        var registers = new S3VgaRegisters(S3VideoIdentity.Virge325);
        var io = new IoBus();
        registers.Register(io);
        PciFunction function = S3VideoIdentity.Virge325.CreatePciFunction(0x0020);

        Assert.Equal(0x56315333u, function.Read(0, 4));
        Assert.Equal(0x03000000u, function.Read(8, 4));
        Assert.Equal(0x56, ReadCrtc(io, 0x2D));
        Assert.Equal(0x31, ReadCrtc(io, 0x2E));
        Assert.Equal(0x00, ReadCrtc(io, 0x2F));
        Assert.Equal(0xE1, ReadCrtc(io, 0x30));

        // No aperture is inferred from an identity-only profile.
        Assert.Equal(0u, function.Read(0x10, 4));
    }

    [Fact]
    public void IdentitiesCannotChangeDuringRegisterProgramming()
    {
        var trioRegisters = new S3VgaRegisters();
        var virgeRegisters = new S3VgaRegisters(S3VideoIdentity.Virge325);
        var trioIo = new IoBus();
        var virgeIo = new IoBus();
        trioRegisters.Register(trioIo);
        virgeRegisters.Register(virgeIo);

        // Try changing both profiles' read-only identity registers after
        // unlocking the extended registers, as diagnostic utilities can do.
        foreach (IoBus io in new[] { trioIo, virgeIo })
        {
            io.WriteWord(0x3D4, 0x4838);
            for (byte index = 0x2D; index <= 0x30; index++)
            {
                io.WriteWord(0x3D4, index);
            }
        }

        Assert.Equal(0x88, ReadCrtc(trioIo, 0x2D));
        Assert.Equal(0x11, ReadCrtc(trioIo, 0x2E));
        Assert.Equal(0x56, ReadCrtc(virgeIo, 0x2D));
        Assert.Equal(0x31, ReadCrtc(virgeIo, 0x2E));
        Assert.Equal(0xE1, ReadCrtc(trioIo, 0x30));
        Assert.Equal(0xE1, ReadCrtc(virgeIo, 0x30));
    }

    private static byte ReadCrtc(IoBus io, byte index)
    {
        io.WriteByte(0x3D4, index);
        return io.ReadByte(0x3D5);
    }
}
