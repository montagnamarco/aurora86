using Aurora86.Core.Memory;

namespace Aurora86.Tests;

public class PhysicalMemoryBusTests
{
    /// <summary>A stub device: serves a buffer and records how it was accessed.</summary>
    private sealed class FakeDevice : IMemoryDevice
    {
        public readonly byte[] Data = new byte[PhysicalMemoryBus.PageSize * 2];
        public int ByteReads, WordReads, DwordReads;

        public byte ReadByte(uint offset) { ByteReads++; return Data[offset]; }
        public void WriteByte(uint offset, byte value) => Data[offset] = value;
        public ushort ReadWord(uint offset)
        {
            WordReads++;
            return (ushort)(Data[offset] | (Data[offset + 1] << 8));
        }
        public uint ReadDword(uint offset)
        {
            DwordReads++;
            return (uint)(Data[offset] | (Data[offset + 1] << 8)
                        | (Data[offset + 2] << 16) | (Data[offset + 3] << 24));
        }
    }

    [Fact]
    public void FreshlyCreatedRamIsZeroed()
    {
        using var bus = new PhysicalMemoryBus();
        Assert.Equal(0, bus.ReadByte(0));
        Assert.Equal(0, bus.ReadByte(0x100000));
        Assert.Equal(0u, bus.ReadDword(0xFFFFC));
    }

    [Fact]
    public void ReadsAndWritesAreLittleEndian()
    {
        using var bus = new PhysicalMemoryBus();
        bus.WriteDword(0x1000, 0xDEADBEEF);

        Assert.Equal(0xEF, bus.ReadByte(0x1000));
        Assert.Equal(0xBE, bus.ReadByte(0x1001));
        Assert.Equal(0xAD, bus.ReadByte(0x1002));
        Assert.Equal(0xDE, bus.ReadByte(0x1003));
        Assert.Equal(0xBEEF, bus.ReadWord(0x1000));
        Assert.Equal(0xDEADBEEF, bus.ReadDword(0x1000));
    }

    [Fact]
    public void UnalignedAccessAcrossAPageBoundaryWorks()
    {
        using var bus = new PhysicalMemoryBus();
        // The last two bytes of page 0 and the first two of page 1.
        uint address = PhysicalMemoryBus.PageSize - 2;
        bus.WriteDword(address, 0x11223344);

        Assert.Equal(0x11223344u, bus.ReadDword(address));
        Assert.Equal(0x3344, bus.ReadWord(address));
        Assert.Equal(0x1122, bus.ReadWord(address + 2));
        Assert.Equal(0x22, bus.ReadByte(PhysicalMemoryBus.PageSize));      // third byte
        Assert.Equal(0x11, bus.ReadByte(PhysicalMemoryBus.PageSize + 1));  // fourth byte
    }

    [Fact]
    public void WritesToRomAreDiscardedButReadsWork()
    {
        using var bus = new PhysicalMemoryBus();
        byte[] rom = [0xAA, 0xBB, 0xCC, 0xDD];
        bus.MapRom(0xF0000, rom);

        Assert.Equal(0xAA, bus.ReadByte(0xF0000));
        Assert.Equal(0xDDCCBBAA, bus.ReadDword(0xF0000));

        bus.WriteByte(0xF0000, 0x00);
        bus.WriteDword(0xF0000, 0);
        Assert.Equal(0xAA, bus.ReadByte(0xF0000));   // unchanged
    }

    [Fact]
    public void MappedDeviceReceivesARelativeOffset()
    {
        using var bus = new PhysicalMemoryBus();
        var device = new FakeDevice();
        bus.MapDevice(0xA0000, PhysicalMemoryBus.PageSize * 2, device);

        bus.WriteByte(0xA0000, 0x42);
        Assert.Equal(0x42, device.Data[0]);          // offset 0, not 0xA0000

        bus.WriteByte(0xA1234, 0x99);
        Assert.Equal(0x99, device.Data[0x1234]);

        Assert.Equal(0x42, bus.ReadByte(0xA0000));
    }

    [Fact]
    public void WideMmioReadUsesASingleCall()
    {
        // This matters: registers with side effects on read must not be
        // triggered several times over by one 32-bit access.
        using var bus = new PhysicalMemoryBus();
        var device = new FakeDevice();
        bus.MapDevice(0xA0000, PhysicalMemoryBus.PageSize * 2, device);

        bus.ReadDword(0xA0000);
        Assert.Equal(1, device.DwordReads);
        Assert.Equal(0, device.ByteReads);

        bus.ReadWord(0xA0010);
        Assert.Equal(1, device.WordReads);
        Assert.Equal(0, device.ByteReads);
    }

    [Fact]
    public void MappingADeviceReplacesRamAndMapRamRestoresIt()
    {
        using var bus = new PhysicalMemoryBus();
        bus.WriteByte(0xA0000, 0x11);
        Assert.Equal(0x11, bus.ReadByte(0xA0000));

        var device = new FakeDevice();
        bus.MapDevice(0xA0000, PhysicalMemoryBus.PageSize, device);
        Assert.Equal(0x00, bus.ReadByte(0xA0000));   // the device answers now

        bus.MapRam(0xA0000, PhysicalMemoryBus.PageSize);
        Assert.Equal(0x11, bus.ReadByte(0xA0000));   // the RAM underneath survived
    }

    [Fact]
    public void AccessesBeyondTheAddressSpaceAreHarmless()
    {
        using var bus = new PhysicalMemoryBus();
        Assert.Equal(0xFF, bus.ReadByte(0xFFFFFFFF));
        bus.WriteByte(0xFFFFFFFF, 0x42);            // must not throw
        Assert.Equal(0xFF, bus.ReadByte(0xF0000000));
    }

    [Fact]
    public void UnalignedMappingIsRejected()
    {
        using var bus = new PhysicalMemoryBus();
        var device = new FakeDevice();
        Assert.Throws<ArgumentException>(() => bus.MapDevice(0xA0001, 4096, device));
        Assert.Throws<ArgumentException>(() => bus.MapDevice(0xA0000, 100, device));
    }
}
