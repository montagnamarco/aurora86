using Aurora386.Core;
using Aurora386.Core.Configuration;

namespace Aurora386.Tests;

/// <summary>
/// What the ViRGE answers on its VGA ports once a driver has set SR9 bit 7,
/// the "MMIO-only" bit.
///
/// The Windows 95 ViRGE driver (S3V.DRV 4.03.00.2030) writes SR9=80h right
/// after a VBE mode set and then reads CR40, CR59, CR5A and CR51 through
/// ports 3D4h/3D5h to locate the linear aperture. An earlier model answered
/// FFh to those reads, following databook section 17-1 to the letter; the
/// driver concluded the adapter type was incorrect and Windows fell back to
/// VGA. A real card keeps answering, and so does the 86Box ViRGE model. The
/// bit is storage only: the one thing that silences the ports is PCI I/O
/// space being disabled.
/// </summary>
public sealed class S3VirgePortGateTests
{
    private const ushort VirgePciFunction = 0x20;
    private const uint FrameBufferBar = 0x74000000;

    [Fact]
    public void ExtendedRegistersStillAnswerThroughPortsWithMmioOnlySet()
    {
        using Machine machine = Started();
        EnableNewMmio(machine);

        // The driver's sequence: unlock the extended sequencer, set MMIO-only.
        WriteSequencer(machine, 0x08, 0x06);
        WriteSequencer(machine, 0x09, 0x80);
        Assert.Equal(0x80, ReadSequencer(machine, 0x09));

        // The chip identifier, the aperture base and the extended registers
        // the driver reads next all keep answering with their values.
        Assert.Equal(0xE1, ReadCrtc(machine, 0x30));
        Assert.Equal(FrameBufferBar >> 24, ReadCrtc(machine, 0x59));
        Assert.Equal((FrameBufferBar >> 16) & 0xFF, ReadCrtc(machine, 0x5A));
        Assert.NotEqual(0xFF, ReadCrtc(machine, 0x40));
        Assert.NotEqual(0xFF, ReadCrtc(machine, 0x51));

        // And the bit can be cleared again through the same port.
        WriteSequencer(machine, 0x09, 0x00);
        Assert.Equal(0x00, ReadSequencer(machine, 0x09));
    }

    [Fact]
    public void DisablingPciIoSpaceIsWhatSilencesThePorts()
    {
        using Machine machine = Started();

        ushort command = (ushort)machine.Pci.ReadConfiguration(VirgePciFunction, 4, 2);
        machine.Pci.WriteConfiguration(VirgePciFunction, 4, 2, (uint)(command & ~1));

        Assert.Equal(0xFF, ReadCrtc(machine, 0x30));

        machine.Pci.WriteConfiguration(VirgePciFunction, 4, 2, command);

        Assert.Equal(0xE1, ReadCrtc(machine, 0x30));
    }

    private static Machine Started()
    {
        var machine = new Machine(videoModel: VmVideoModel.S3Virge325);
        machine.RunInstructions(200);
        machine.Pci.WriteConfiguration(VirgePciFunction, 0x10, 4, FrameBufferBar);
        return machine;
    }

    /// <summary>Unlocks the extended CRTC range and selects the new MMIO in CR53.</summary>
    private static void EnableNewMmio(Machine machine)
    {
        WriteCrtc(machine, 0x38, 0x48);
        WriteCrtc(machine, 0x39, 0xA0);
        WriteCrtc(machine, 0x53, 0x08);
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

    private static void WriteSequencer(Machine machine, byte index, byte value)
    {
        machine.Io.WriteByte(0x3C4, index);
        machine.Io.WriteByte(0x3C5, value);
    }

    private static byte ReadSequencer(Machine machine, byte index)
    {
        machine.Io.WriteByte(0x3C4, index);
        return machine.Io.ReadByte(0x3C5);
    }
}
