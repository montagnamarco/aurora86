using Aurora386.Core.Cpu;
using Aurora386.Core.Devices.Pci;
using Aurora386.Core.Devices.Video;
using Aurora386.Core.IO;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// Configures an original ViRGE through PCI mechanism 1 and its MMIO aliases.
/// The CPU triangle fixture attaches the card, never an engine test mapping.
/// </summary>
public class S3VirgeCardTests
{
    private const uint ResetMmio = 0x71000000;

    [Fact]
    public void PciConfigurationPortsExposeResetIdentityAndSizing()
    {
        using var fixture = new CardFixture();
        Assert.Equal(0x56315333u, fixture.ReadPci(0));
        Assert.Equal(0u, fixture.ReadPci(4));
        Assert.Equal(0x70000000u, fixture.ReadPci(0x10));
        Assert.Equal(0u, fixture.ReadPci(0x30));
        Assert.Equal(0xFFFFFFFFu, fixture.Memory.ReadDword(ResetMmio + 0x8504));
        fixture.WritePci(0x10, uint.MaxValue);
        Assert.Equal(0xFC000000u, fixture.ReadPci(0x10));
        Assert.Equal(0xFC000000u, fixture.Card.Registers.VirgeAperture!.PciBase);
        Assert.Equal(2u * 1024 * 1024, fixture.Memory.RamSize);
    }

    [Fact]
    public void MemoryCommandAndCrtcGatesControlDecoding()
    {
        using var fixture = new CardFixture();
        fixture.Memory.WriteByte(0x10000, 0xAA);
        fixture.WritePci(0x10, 0);
        fixture.WritePci(4, 2);
        Assert.Equal(0x3000u, fixture.Memory.ReadDword(0x01008504));
        Assert.Equal(0xFF, fixture.Memory.ReadByte(0x10000));
        fixture.WriteCrtc(0x38, 0x48, 0x01000000);
        fixture.WriteCrtc(0x39, 0xA0, 0x01000000);
        fixture.WriteCrtc(0x58, 0x12, 0x01000000);
        fixture.WriteCrtc(0x31, 8, 0x01000000);
        fixture.Memory.WriteByte(0x10000, 0x33);
        Assert.Equal(0, fixture.Card.VideoMemory.ReadLinear(0x10000));
        fixture.WriteCrtc(0x66, 1, 0x01000000);
        fixture.Memory.WriteByte(0x10000, 0x44);
        Assert.Equal(0x44, fixture.Card.VideoMemory.ReadLinear(0x10000));
        fixture.WriteCrtc(0x58, 2, 0x01000000);
        fixture.Memory.WriteByte(0x10000, 0x55);
        Assert.Equal(0x44, fixture.Card.VideoMemory.ReadLinear(0x10000));
        fixture.WriteCrtc(0x53, 0, 0x01000000);
        Assert.Equal(uint.MaxValue, fixture.Memory.ReadDword(0x01008504));
        fixture.WritePci(4, 0);
        Assert.Equal(0xAA, fixture.Memory.ReadByte(0x10000));
    }

    [Fact]
    public void LinearWindowHonoursEachEnhancedGateAndInstalledVideoMemoryLimit()
    {
        using var fixture = new CardFixture();
        fixture.WritePci(4, 2);
        fixture.WriteCrtc(0x38, 0x48);
        fixture.WriteCrtc(0x39, 0xA0);
        fixture.WriteCrtc(0x58, 0x13); // A 4 MB window on a 2 MB card.
        fixture.WriteCrtc(0x66, 1);
        fixture.Memory.WriteByte(0x70000000, 0x44);
        Assert.Equal(0, fixture.Card.VideoMemory.ReadLinear(0));
        fixture.WriteCrtc(0x31, 8);
        fixture.Memory.WriteByte(0x70000000, 0x55);
        Assert.Equal(0x55, fixture.Card.VideoMemory.ReadLinear(0));
        fixture.Memory.WriteDword(0x701FFFFC, 0xAABBCCDD);
        Assert.Equal(0xAABBCCDDu, fixture.Memory.ReadDword(0x701FFFFC));
        fixture.Memory.WriteByte(0x70200000, 0x99);
        Assert.Equal(0xFF, fixture.Memory.ReadByte(0x70200000));
        Assert.Equal(0x55, fixture.Card.VideoMemory.ReadLinear(0));
        fixture.WriteCrtc(0x58, 0x10); // Restrict the same base to 64 KB.
        fixture.Memory.WriteByte(0x7000FFFF, 0x66);
        fixture.Memory.WriteByte(0x70010000, 0x77);
        Assert.Equal(0x66, fixture.Memory.ReadByte(0x7000FFFF));
        Assert.Equal(0xFF, fixture.Memory.ReadByte(0x70010000));
        Assert.Equal(0, fixture.Card.VideoMemory.ReadLinear(0x10000));
    }

    [Fact]
    public void CrtcLowAddressBitsDoNotMoveNewMmio()
    {
        using var fixture = new CardFixture();
        fixture.WritePci(4, 2);
        fixture.WriteCrtc(0x39, 0xA0);
        fixture.WriteCrtc(0x59, 0x71);
        fixture.WriteCrtc(0x5A, 0x23);
        Assert.Equal(0x71230000u, fixture.Card.Registers.ApertureBase);
        Assert.Equal(0x70000000u, fixture.ReadPci(0x10));
        Assert.Equal(0x3000u, fixture.Memory.ReadDword(ResetMmio + 0x8504));
        Assert.Equal(uint.MaxValue, fixture.Memory.ReadDword(0x72238504));
    }

    [Fact]
    public void PciAndCrtcRelocationShareUpperBitsWithoutRecursion()
    {
        using var fixture = new CardFixture();
        fixture.WritePci(4, 2);
        fixture.Card.Registers.ApertureBase = 0x71230000;
        fixture.WritePci(0x10, 0x74000000);
        Assert.Equal(0x75230000u, fixture.Card.Registers.ApertureBase);
        Assert.Equal(uint.MaxValue, fixture.Memory.ReadDword(ResetMmio + 0x8504));
        Assert.Equal(0x3000u, fixture.Memory.ReadDword(0x75008504));
        fixture.WriteCrtc(0x39, 0xA0, 0x75000000);
        fixture.WriteCrtc(0x59, 0x79, 0x75000000);
        Assert.Equal(0x78000000u, fixture.ReadPci(0x10));
        Assert.Equal(0x79230000u, fixture.Card.Registers.ApertureBase);
        Assert.Equal(uint.MaxValue, fixture.Memory.ReadDword(0x75008504));
        Assert.Equal(0x3000u, fixture.Memory.ReadDword(0x79008504));
    }

    [Fact]
    public void MmioConfigurationAndVgaAliasesReachTheSameLatches()
    {
        using var fixture = new CardFixture();
        fixture.WritePci(4, 2);
        Assert.Equal(0x56315333u, fixture.Memory.ReadDword(ResetMmio + 0x8000));
        fixture.WriteCrtc(0x39, 0xA0);
        fixture.WriteCrtc(0x58, 0x12);
        Assert.Equal(0x12, fixture.Memory.ReadByte(ResetMmio + 0x83D5));
        fixture.Memory.WriteDword(ResetMmio + 0x8010, 0x74000000);
        Assert.Equal(0x74000000u, fixture.ReadPci(0x10));
        Assert.Equal(0x56315333u, fixture.Memory.ReadDword(0x75008000));
        Assert.Equal(uint.MaxValue, fixture.Memory.ReadDword(ResetMmio + 0x8000));
    }

    [Fact]
    public void PciIoDisableSilencesThePortsButMmioOnlyDoesNot()
    {
        using var fixture = new CardFixture();

        // PCI I/O space off: the MMIO alias answers, the port does not.
        fixture.WritePci(4, 2);
        fixture.WriteCrtc(0x2E, 0);
        Assert.Equal(0x31, fixture.Memory.ReadByte(ResetMmio + 0x83D5));
        Assert.Equal(0xFF, fixture.Io.ReadByte(0x3D5));
        fixture.WritePci(4, 3);
        Assert.Equal(0x31, fixture.Io.ReadByte(0x3D5));

        // SR9 bit 7 ("MMIO-only") does not silence the ports. The Windows 95
        // ViRGE driver sets it and then reads CR40/CR59/CR5A/CR51 through
        // 3D5h to find the aperture; a card answering FFh there was taken
        // for the wrong adapter. Reads and writes keep working through both
        // doors, as they do on the 86Box ViRGE model.
        fixture.Memory.WriteWord(ResetMmio + 0x83C4, 0x0608);
        fixture.Memory.WriteWord(ResetMmio + 0x83C4, 0x8009);
        Assert.Equal(0x31, fixture.Io.ReadByte(0x3D5));
        Assert.Equal(0x31, fixture.Memory.ReadByte(ResetMmio + 0x83D5));
        fixture.Io.WriteByte(0x3C4, 0x09);
        Assert.Equal(0x80, fixture.Io.ReadByte(0x3C5));

        // A port write to an extended sequencer register takes effect: relocking
        // SR8 through the port is visible through the MMIO alias.
        fixture.Io.WriteByte(0x3C4, 0x08);
        fixture.Io.WriteByte(0x3C5, 0);
        Assert.Equal(0, fixture.Memory.ReadByte(ResetMmio + 0x83C5));
        fixture.Io.WriteByte(0x3C5, 6);
        Assert.Equal(6, fixture.Memory.ReadByte(ResetMmio + 0x83C5));

        // Standard VGA registers were never in question.
        fixture.Io.WriteByte(0x3C4, 0);
        fixture.Io.WriteByte(0x3C5, 3);
        Assert.Equal(3, fixture.Io.ReadByte(0x3C5));

        // Clearing the bit changes nothing either.
        fixture.Memory.WriteWord(ResetMmio + 0x83C4, 0x0009);
        Assert.Equal(0x31, fixture.Io.ReadByte(0x3D5));
    }

    [Fact]
    public void UnsupportedWindowsDoNotDrawOrExposeRam()
    {
        using var fixture = new CardFixture();
        fixture.WritePci(0x10, 0);
        fixture.Memory.WriteDword(0x1000, 0x11223344);
        fixture.WritePci(4, 2);
        var accesses = new List<S3VirgeUnsupportedAccess>();
        var commands = new List<uint>();
        fixture.Card.UnsupportedAccess += accesses.Add;
        fixture.Card.Blitter.UnsupportedCommandIssued += commands.Add;
        Assert.Equal(uint.MaxValue, fixture.Memory.ReadDword(0x1000));
        fixture.Memory.WriteDword(0x0300B500, 0x83000008);
        fixture.Memory.WriteDword(0x0100A500, 0x83000008);
        fixture.Memory.WriteDword(0x01000000, 0xFFFFFFFF);
        Assert.Equal(3, accesses.Count);
        Assert.Equal(0x83000008u, Assert.Single(commands)); // A500 now reaches the 2D decoder.
        Assert.Equal(S3VirgeUnsupportedReason.BigEndian, accesses[1].Reason);
        Assert.Equal(4, accesses[1].Width);
        Assert.True(accesses[1].Write);
        Assert.Equal(0x0300B500u, accesses[1].Offset);
        for (uint offset = 0; offset < VgaMemory.TotalSize; offset++)
        {
            Assert.Equal(0, fixture.Card.VideoMemory.ReadLinear(offset));
        }

        fixture.WritePci(4, 0);
        Assert.Equal(0x11223344u, fixture.Memory.ReadDword(0x1000));
    }

    [Fact]
    public void OldMmioRequestIsDiagnosedWithoutMappingLegacyRam()
    {
        using var fixture = new CardFixture();
        fixture.Memory.WriteByte(0xA0000, 0x55);
        fixture.WritePci(4, 2);
        var accesses = new List<S3VirgeUnsupportedAccess>();
        fixture.Card.UnsupportedAccess += accesses.Add;
        fixture.WriteCrtc(0x39, 0xA0);
        fixture.WriteCrtc(0x53, 0x18);
        fixture.WriteCrtc(0x53, 0x18);
        Assert.Single(accesses);
        Assert.Equal(S3VirgeUnsupportedReason.OldMmio, accesses[0].Reason);
        Assert.Equal(0x55, fixture.Memory.ReadByte(0xA0000));
        Assert.Equal(0x3000u, fixture.Memory.ReadDword(ResetMmio + 0x8504));
    }

    [Fact]
    public void MisalignedConfigurationAccessDoesNotChangePciState()
    {
        using var fixture = new CardFixture();
        fixture.WritePci(4, 2);
        var accesses = new List<S3VirgeUnsupportedAccess>();
        fixture.Card.UnsupportedAccess += accesses.Add;
        fixture.Memory.WriteDword(ResetMmio + 0x8011, 0x78000000);
        Assert.Equal(uint.MaxValue, fixture.Memory.ReadDword(ResetMmio + 0x8011));
        Assert.Equal(0x70000000u, fixture.ReadPci(0x10));
        Assert.Equal(2, accesses.Count);
        Assert.All(accesses, access => Assert.Equal(
            S3VirgeUnsupportedReason.MisalignedConfiguration, access.Reason));
    }

    [Fact]
    public void ConstructorRejectsSmallOrDisposedBusBeforeRegisteringDevices()
    {
        var io = new IoBus();
        var pci = new PciBus();
        var sentinel = new VgaRegisters();
        io.Register(0x3C4, sentinel);
        io.WriteByte(0x3C4, 3);
        using var small = new PhysicalMemoryBus(0x100000);
        Assert.Throws<ArgumentException>(() => new S3VirgeCard(small, io, pci));
        var disposed = new PhysicalMemoryBus(0x100000, 1UL << 32);
        disposed.Dispose();
        Assert.Throws<ObjectDisposedException>(() => new S3VirgeCard(disposed, io, pci));
        Assert.Empty(pci.Functions);
        Assert.Equal(3, io.ReadByte(0x3C4));
    }

    [Fact]
    public void DuplicatePciFunctionLeavesExistingPortsAndMappingsIntact()
    {
        using var fixture = new CardFixture();
        fixture.WritePci(4, 3);
        fixture.Io.WriteByte(0x3C4, 3);
        Assert.Throws<ArgumentException>(() => new S3VirgeCard(fixture.Memory, fixture.Io, fixture.Pci));
        Assert.Single(fixture.Pci.Functions);
        Assert.Equal(3, fixture.Io.ReadByte(0x3C4));
        Assert.Equal(0x3000u, fixture.Memory.ReadDword(ResetMmio + 0x8504));
    }

    [Fact]
    public void DisposeReleasesDecodeAndDisconnectsConfigurationCallbacks()
    {
        using var fixture = new CardFixture();
        fixture.Memory.WriteByte(0x1000, 0x77);
        fixture.WritePci(0x10, 0);
        fixture.WritePci(4, 3);
        fixture.Card.Dispose();
        fixture.Card.Dispose();
        Assert.Equal(0x77, fixture.Memory.ReadByte(0x1000));
        Assert.Equal(0xFF, fixture.Io.ReadByte(0x3C4));
        fixture.WritePci(0x10, 0x74000000);
        Assert.Equal(uint.MaxValue, fixture.Memory.ReadDword(0x75008504));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CpuB500DwordDrawsTheSameTriangleWithOrWithoutMmioObserver(bool observe)
    {
        using var fixture = new CardFixture();
        var writes = new List<S3VirgeMmioWrite>();
        if (observe)
        {
            fixture.Card.MmioWriteObserved += writes.Add;
        }
        fixture.Card.Registers.SetVesaMode(VesaModes.Get(0x112, VgaMemory.TotalSize), true);
        const uint mmio = 0x75000000;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WritePciCode(writer, 0x10, 0x74000000);
        WritePciCode(writer, 4, 2);
        WriteMovCode(writer, mmio + 0xB4D8, 0);
        WriteMovCode(writer, mmio + 0xB4E4, 1920u << 16);
        WriteMovCode(writer, mmio + 0xB54C, 0x20001000);
        WriteMovCode(writer, mmio + 0xB550, 0x7000);
        WriteMovCode(writer, mmio + 0xB574, 1u << 20);
        WriteMovCode(writer, mmio + 0xB56C, 5u << 20);
        WriteMovCode(writer, mmio + 0xB568, 0xFFF00000);
        WriteMovCode(writer, mmio + 0xB578, 4);
        WriteMovCode(writer, mmio + 0xB57C, 0x80040000);
        WriteMovCode(writer, mmio + 0xB500, 0x83000008);
        byte[] code = stream.ToArray();
        code.CopyTo(fixture.Memory.GetRamSpan(0x1000, code.Length));
        var cpu = new Cpu(fixture.Memory, fixture.Io);
        cpu.State.LoadSegmentRealMode(SegReg.Cs, 0);
        cpu.State.LoadSegmentRealMode(SegReg.Ds, 0);
        cpu.State.Segments[(int)SegReg.Ds].Limit = uint.MaxValue;
        cpu.State.Eip = 0x1000;
        cpu.State.Halted = false;
        int steps = 0;
        while (cpu.State.Eip < 0x1000 + code.Length && steps++ < 100)
        {
            cpu.Step();
        }

        Assert.Equal(0x1000u + (uint)code.Length, cpu.State.Eip);
        Assert.Equal(0x74000000u, fixture.ReadPci(0x10));
        Assert.Equal(0x3000u, fixture.Memory.ReadDword(mmio + 0x8504));
        if (observe)
        {
            Assert.Equal(10, writes.Count);
            Assert.Equal(new S3VirgeMmioWrite(0xB500, 4, 0x83000008), writes[^1]);
            Assert.All(writes, write => Assert.Equal(4, write.Width));
        }

        var expected = new byte[VgaMemory.TotalSize];
        (int X, int Y)[] pixels = [(1, 4), (2, 4), (3, 4), (4, 4),
            (1, 3), (2, 3), (3, 3), (1, 2), (2, 2), (1, 1)];
        foreach ((int x, int y) in pixels)
        {
            int address = y * 1920 + x * 3;
            expected[address] = 0x20;
            expected[address + 1] = 0x40;
            expected[address + 2] = 0xE0;
        }

        for (uint offset = 0; offset < VgaMemory.TotalSize; offset++)
        {
            Assert.Equal(expected[offset], fixture.Card.VideoMemory.ReadLinear(offset));
        }

        Assert.True(fixture.Card.Display.Render(true));
        Assert.Equal(0xFF2040E0u, fixture.Card.Display.Framebuffer[4 * 640 + 1]);
        Assert.Equal(0xFF000000u, fixture.Card.Display.Framebuffer[4 * 640 + 5]);
    }

    [Fact]
    public void MmioObserverPreserves8208WidthsAndHonoursPciAndCr53DecodeGates()
    {
        using var fixture = new CardFixture();
        var writes = new List<S3VirgeMmioWrite>();
        fixture.Card.MmioWriteObserved += writes.Add;
        fixture.Memory.WriteDword(ResetMmio + 0x8208, 0x11223344);
        Assert.Empty(writes); // PCI memory decoding is still disabled.

        fixture.WritePci(4, 2);
        fixture.Memory.WriteByte(ResetMmio + 0x8208, 0x12);
        fixture.Memory.WriteWord(ResetMmio + 0x8208, 0x3456);
        fixture.Memory.WriteDword(ResetMmio + 0x8208, 0x789ABCDE);
        Assert.Equal(new S3VirgeMmioWrite[]
        {
            new(0x8208, 1, 0x12),
            new(0x8208, 2, 0x3456),
            new(0x8208, 4, 0x789ABCDE)
        }, writes);

        fixture.WritePci(0x10, 0x74000000);
        writes.Clear();
        fixture.Memory.WriteDword(ResetMmio + 0x8208, 1);
        fixture.Memory.WriteDword(0x75008208, 2);
        Assert.Equal(new S3VirgeMmioWrite(0x8208, 4, 2), Assert.Single(writes));
        fixture.WriteCrtc(0x39, 0xA0, 0x75000000);
        fixture.WriteCrtc(0x53, 0, 0x75000000);
        writes.Clear();
        fixture.Memory.WriteDword(0x75008208, 3);
        Assert.Empty(writes);
    }

    [Fact]
    public void MmioObserverReportsB57CAutoexecuteOnceWithoutSplittingOrReplaying()
    {
        using var fixture = new CardFixture();
        fixture.WritePci(4, 2);
        var writes = new List<S3VirgeMmioWrite>();
        var commands = new List<uint>();
        fixture.Card.MmioWriteObserved += writes.Add;
        fixture.Card.Engine.UnsupportedCommandIssued += commands.Add;
        fixture.Memory.WriteDword(ResetMmio + 0xB500, 0x80C00007);
        Assert.Empty(commands); // CMD bit 0 defers submission to Y_COUNT.
        fixture.Memory.WriteDword(ResetMmio + 0xB57C, 0x80010000);
        Assert.Equal(0x80C00007u, Assert.Single(commands));
        Assert.Equal(2, writes.Count);
        Assert.Equal(new S3VirgeMmioWrite(0xB57C, 4, 0x80010000), writes[1]);
        Assert.Equal(0x80010000u, fixture.Card.Engine.ReadDword(0xB57C));
    }

    /// <summary>Emits MOV DX,port; MOV EAX,value; OUT DX,EAX from a 16-bit CS.</summary>
    private static void WriteOutCode(BinaryWriter writer, ushort port, uint value)
    {
        writer.Write((byte)0xBA);
        writer.Write(port);
        writer.Write(new byte[] { 0x66, 0xB8 });
        writer.Write(value);
        writer.Write(new byte[] { 0x66, 0xEF });
    }

    private static void WritePciCode(BinaryWriter writer, byte register, uint value)
    {
        WriteOutCode(writer, 0xCF8, 0x80002000u | register);
        WriteOutCode(writer, 0xCFC, value);
    }

    private static void WriteMovCode(BinaryWriter writer, uint address, uint value)
    {
        writer.Write(new byte[] { 0x66, 0x67, 0xC7, 0x05 });
        writer.Write(address);
        writer.Write(value);
    }

    /// <summary>Provides a real four-gigabyte bus with only two megabytes of RAM.</summary>
    private sealed class CardFixture : IDisposable
    {
        public PhysicalMemoryBus Memory { get; } = new(0x200000, 1UL << 32) { A20Enabled = true };
        public IoBus Io { get; } = new();
        public PciBus Pci { get; } = new();
        public S3VirgeCard Card { get; }

        public CardFixture()
        {
            Pci.Register(Io);
            Card = new S3VirgeCard(Memory, Io, Pci);
        }

        public uint ReadPci(byte register)
        {
            Io.WriteDword(0xCF8, 0x80002000u | register);
            return Io.ReadDword(0xCFC);
        }

        public void WritePci(byte register, uint value)
        {
            Io.WriteDword(0xCF8, 0x80002000u | register);
            Io.WriteDword(0xCFC, value);
        }

        public void WriteCrtc(byte index, byte value, uint mmio = ResetMmio)
        {
            Memory.WriteWord(mmio + 0x83D4, (ushort)(index | (value << 8)));
        }

        public void Dispose()
        {
            Card.Dispose();
            Memory.Dispose();
        }
    }
}
