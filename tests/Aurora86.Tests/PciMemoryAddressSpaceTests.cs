using Aurora86.Core.Memory;

namespace Aurora86.Tests;

/// <summary>
/// PCI BARs reserve address space, not installed RAM. These cases place a
/// ViRGE-sized aperture and the final 32-bit page on a bus with only 1 MB of
/// guest RAM, exercising range arithmetic and wide device accesses directly.
/// </summary>
public class PciMemoryAddressSpaceTests
{
    private const uint InstalledRam = 1024 * 1024;
    private const ulong FourGigabytes = 1UL << 32;
    private const uint VirgeApertureSize = 64 * 1024 * 1024;

    [Fact]
    public void VirgeBarDoesNotAllocateGuestRam()
    {
        using var bus = new PhysicalMemoryBus(InstalledRam, FourGigabytes);
        bus.A20Enabled = true;
        var device = new RegisterDevice();
        bus.MapDevice(0x70000000, VirgeApertureSize, device);

        bus.WriteDword(0x7100B500, 0x83000008);

        Assert.Equal(InstalledRam, bus.RamSize);
        Assert.Equal((int)InstalledRam, bus.CaptureRam().Length);
        Assert.Equal(1, device.DwordWrites);
        Assert.Equal(0, device.ByteWrites);
        Assert.Equal(0x0100B500u, device.LastWriteOffset);
        Assert.Equal(0x83000008u, bus.ReadDword(0x7100B500));
        Assert.Equal(1, device.DwordReads);
        Assert.Equal(0x0100B500u, device.LastReadOffset);
        Assert.Equal(uint.MaxValue, bus.ReadDword(0x6FFFFFFC));
    }

    [Fact]
    public void LastPciWindowEndsAtFourGigabytes()
    {
        using var bus = new PhysicalMemoryBus(InstalledRam, FourGigabytes);
        bus.A20Enabled = true;
        var device = new RegisterDevice();
        bus.MapDevice(0xFC000000, VirgeApertureSize, device);

        bus.WriteDword(0xFFFFFFFC, 0xA1B2C3D4);

        Assert.Equal(1, device.DwordWrites);
        Assert.Equal(0x03FFFFFCu, device.LastWriteOffset);
        Assert.Equal(0xA1B2C3D4u, bus.ReadDword(0xFFFFFFFC));
        Assert.Equal(1, device.DwordReads);
        Assert.Equal(0x03FFFFFCu, device.LastReadOffset);
    }

    [Fact]
    public void MappingPastFourGigabytesIsRejectedBeforeChangingExistingPages()
    {
        using var bus = new PhysicalMemoryBus(InstalledRam, FourGigabytes);
        bus.A20Enabled = true;
        var existing = new RegisterDevice();
        bus.MapDevice(0xFFFFF000, 0x1000, existing);
        bus.WriteDword(0xFFFFFFFC, 0x11223344);
        var replacement = new RegisterDevice();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            bus.MapDevice(0xFFFFF000, 0x2000, replacement));

        Assert.Equal(0x11223344u, bus.ReadDword(0xFFFFFFFC));
        Assert.Equal(1, existing.DwordReads);
        Assert.Equal(0, replacement.DwordReads);
        Assert.Equal(0, replacement.DwordWrites);
    }

    [Fact]
    public void LegacyBusStillRejectsOutOfRangeMappings()
    {
        using var bus = new PhysicalMemoryBus();
        var device = new RegisterDevice();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            bus.MapDevice(0x70000000, VirgeApertureSize, device));
        Assert.Equal(16u * 1024 * 1024, bus.RamSize);
    }

    [Fact]
    public void HighBiosAliasStillPrecedesPciAddressLookup()
    {
        using var bus = new PhysicalMemoryBus(InstalledRam, FourGigabytes);
        var device = new RegisterDevice();
        bus.MapDevice(0xFFFF0000, 0x10000, device);
        bus.MapHighAlias(0xFFFF0000, 0xF0000);
        bus.WriteDword(0xFFFF0, 0x44332211);

        // Reset-vector decoding precedes A20 even when a high PCI mapping
        // exists. Otherwise power-on instruction fetch reaches the wrong chip.
        Assert.False(bus.A20Enabled);
        Assert.Equal(0x44332211u, bus.ReadDword(0xFFFFFFF0));
        Assert.Equal(0, device.DwordReads);
        Assert.Equal(0, device.ByteReads);
    }

    [Fact]
    public void A20StillMasksHighPciAddressesOutsideTheBiosAlias()
    {
        using var bus = new PhysicalMemoryBus(InstalledRam, FourGigabytes);
        var low = new RegisterDevice();
        var high = new RegisterDevice();
        bus.MapDevice(0x70000000, 0x1000, low);
        bus.MapDevice(0x70100000, 0x1000, high);

        bus.WriteDword(0x70100000, 0x11223344);
        Assert.Equal(1, low.DwordWrites);
        Assert.Equal(0, high.DwordWrites);

        bus.A20Enabled = true;
        bus.WriteDword(0x70100000, 0x55667788);
        Assert.Equal(1, low.DwordWrites);
        Assert.Equal(1, high.DwordWrites);
        Assert.Equal(0x11223344u, bus.ReadDword(0x70000000));
        Assert.Equal(0x55667788u, bus.ReadDword(0x70100000));
    }

    [Theory]
    [InlineData(0x00100001UL)] // Addressable space must be page aligned.
    [InlineData(0x000FF000UL)] // Cannot hide part of installed RAM.
    [InlineData(0x100001000UL)] // The processor has only 32 address bits.
    public void InvalidAddressSpaceSizesAreRejected(ulong addressableBytes)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
        {
            using var bus = new PhysicalMemoryBus(InstalledRam, addressableBytes);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HighRamMappingsRejectAddressesWithoutHostBacking(bool readOnly)
    {
        using var bus = new PhysicalMemoryBus(InstalledRam, FourGigabytes);
        bus.A20Enabled = true;
        bus.WriteByte(0x100, 0x5A);
        var device = new RegisterDevice();
        bus.MapDevice(0xFFFFF000, 0x1000, device);
        bus.WriteDword(0xFFFFFFFC, 0x11223344);

        // The physical range is valid, but it has no backing guest RAM.
        // Reject before copying ROM bytes or creating native page pointers.
        Assert.Throws<ArgumentException>(() =>
        {
            if (readOnly)
            {
                bus.MapRom(0xFFFFF000, new byte[0x1000]);
            }
            else
            {
                bus.MapRam(0xFFFFF000, 0x1000);
            }
        });

        Assert.Equal(0x5A, bus.ReadByte(0x100));
        Assert.Equal(0x11223344u, bus.ReadDword(0xFFFFFFFC));
        Assert.Equal(1, device.DwordReads);
    }

    [Fact]
    public void DirectRomWritesRejectWrappedPhysicalAddresses()
    {
        using var bus = new PhysicalMemoryBus(InstalledRam, FourGigabytes);
        bus.WriteByte(0, 0x5A);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            bus.WriteRomWord(uint.MaxValue, 0x1122));

        Assert.Equal(0x5A, bus.ReadByte(0));
    }

    [Theory]
    [InlineData(0xFFFFF000u, 0x1000)]
    [InlineData(0xFFFFFFFFu, 2)]
    [InlineData(0x1000u, -1)]
    public void DirectRamSpansRejectWrappedOrNegativeRanges(uint physicalBase, int length)
    {
        using var bus = new PhysicalMemoryBus(InstalledRam, FourGigabytes);
        bus.WriteByte(0, 0x5A);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            bus.GetRamSpan(physicalBase, length);
        });

        Assert.Equal(0x5A, bus.ReadByte(0));
    }

    /// <summary>A device that distinguishes one dword cycle from four byte cycles.</summary>
    private sealed class RegisterDevice : IMemoryDevice
    {
        private uint _value;

        public int ByteWrites { get; private set; }
        public int ByteReads { get; private set; }
        public int DwordWrites { get; private set; }
        public int DwordReads { get; private set; }
        public uint LastWriteOffset { get; private set; }
        public uint LastReadOffset { get; private set; }

        public byte ReadByte(uint offset)
        {
            ByteReads++;
            LastReadOffset = offset;
            return (byte)(_value >> ((int)(offset & 3) * 8));
        }

        public uint ReadDword(uint offset)
        {
            DwordReads++;
            LastReadOffset = offset;
            return _value;
        }

        public void WriteByte(uint offset, byte value)
        {
            ByteWrites++;
            LastWriteOffset = offset;
            _value = value;
        }

        public void WriteDword(uint offset, uint value)
        {
            DwordWrites++;
            LastWriteOffset = offset;
            _value = value;
        }
    }
}
