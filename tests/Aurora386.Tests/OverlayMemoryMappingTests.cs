using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>Checks relocatable device windows against RAM, ROM, watches, and disposal.</summary>
public class OverlayMemoryMappingTests
{
    /// <summary>
    /// Records bus transaction widths and offsets independently of backing RAM.
    /// Sparse bytes allow the final register of a 64 MB aperture to be exercised.
    /// </summary>
    private sealed class RegisterDevice : IMemoryDevice
    {
        public readonly byte[] Data = new byte[PhysicalMemoryBus.PageSize * 2];
        private readonly Dictionary<uint, byte> _sparse = new();
        public uint LastOffset;
        public int DwordWrites;
        public int DwordReads;

        public byte ReadByte(uint offset)
        {
            LastOffset = offset;
            return offset < Data.Length ? Data[offset] : _sparse.GetValueOrDefault(offset);
        }

        public void WriteByte(uint offset, byte value)
        {
            LastOffset = offset;
            if (offset < Data.Length)
            {
                Data[offset] = value;
            }
            else
            {
                _sparse[offset] = value;
            }
        }

        public void WriteDword(uint offset, uint value)
        {
            DwordWrites++;
            for (int index = 0; index < 4; index++)
            {
                WriteByte(offset + (uint)index, (byte)(value >> (index * 8)));
            }

            LastOffset = offset;
        }

        public uint ReadDword(uint offset)
        {
            DwordReads++;
            return (uint)(ReadByte(offset)
                | (ReadByte(offset + 1) << 8)
                | (ReadByte(offset + 2) << 16)
                | (ReadByte(offset + 3) << 24));
        }
    }

    [Fact]
    public void RelocationRestoresRamAndUsesRelativeOffset()
    {
        using var bus = NewBus();
        bus.WriteByte(0xA0000, 0x11);
        var device = new RegisterDevice();
        using PhysicalMemoryBus.DeviceMapping mapping = bus.CreateDeviceMapping(device);
        mapping.Update(0xA0000, PhysicalMemoryBus.PageSize);
        bus.WriteByte(0xA0234, 0x42);
        Assert.Equal(0x42, device.Data[0x234]);
        mapping.Update(0xB0000, PhysicalMemoryBus.PageSize);

        Assert.Equal(0x11, bus.ReadByte(0xA0000));
        bus.WriteByte(0xB0234, 0x55);
        Assert.Equal(0x234u, device.LastOffset);
    }

    [Fact]
    public void LaterOverlayWinsAndDisablingRevealsCurrentUnderlay()
    {
        using var bus = NewBus();
        var first = new RegisterDevice();
        var second = new RegisterDevice();
        using PhysicalMemoryBus.DeviceMapping firstMapping = bus.CreateDeviceMapping(first);
        using PhysicalMemoryBus.DeviceMapping secondMapping = bus.CreateDeviceMapping(second);
        firstMapping.Update(0xA0000, PhysicalMemoryBus.PageSize);
        secondMapping.Update(0xA0000, PhysicalMemoryBus.PageSize);
        bus.WriteByte(0xA0000, 0x22);
        Assert.Equal(0x22, second.Data[0]);
        firstMapping.Update(0xB0000, PhysicalMemoryBus.PageSize);
        firstMapping.Update(0xA0000, PhysicalMemoryBus.PageSize);
        bus.WriteByte(0xA0000, 0x24);
        Assert.Equal(0x24, second.Data[0]);
        Assert.Equal(0, first.Data[0]);
        firstMapping.Update(0xB0000, PhysicalMemoryBus.PageSize);
        secondMapping.Disable();
        Assert.Equal(0, bus.ReadByte(0xA0000));
        bus.WriteByte(0xB0000, 0x33);
        Assert.Equal(0x33, first.Data[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void BaseMappingChangesBecomeVisibleAfterOverlayDisable(int operation)
    {
        using var bus = NewBus();
        var overlay = new RegisterDevice();
        using PhysicalMemoryBus.DeviceMapping mapping = bus.CreateDeviceMapping(overlay);
        mapping.Update(0xA0000, PhysicalMemoryBus.PageSize);
        var replacement = new RegisterDevice();
        switch (operation)
        {
            case 0:
                bus.MapDevice(0xA0000, PhysicalMemoryBus.PageSize, replacement);
                break;
            case 1:
                bus.MapRam(0xA0000, PhysicalMemoryBus.PageSize);
                break;
            case 2:
                bus.MapRom(0xA0000, new byte[] { 0xAA });
                break;
            case 3:
                bus.Unmap(0xA0000, PhysicalMemoryBus.PageSize);
                break;
        }

        bus.WriteByte(0xA0000, 0x33);
        Assert.Equal(0x33, overlay.Data[0]);
        mapping.Disable();

        if (operation == 0)
        {
            bus.WriteByte(0xA0000, 0x44);
            Assert.Equal(0x44, replacement.Data[0]);
        }
        else if (operation == 1)
        {
            bus.WriteByte(0xA0000, 0x45);
            Assert.Equal(0x45, bus.ReadByte(0xA0000));
        }
        else if (operation == 2)
        {
            Assert.Equal(0xAA, bus.ReadByte(0xA0000));
        }
        else
        {
            bus.WriteByte(0xA0000, 0x99);
            Assert.Equal(0xFF, bus.ReadByte(0xA0000));
        }
    }

    [Fact]
    public void RomUnderlaySurvivesRamSnapshotRestore()
    {
        using var bus = NewBus();
        bus.MapRom(0xA0000, new byte[] { 0xAA });
        byte[] captured = bus.CaptureRam();
        captured[0xA0000] = 0x55;
        var overlay = new RegisterDevice();
        using PhysicalMemoryBus.DeviceMapping mapping = bus.CreateDeviceMapping(overlay);
        mapping.Update(0xA0000, PhysicalMemoryBus.PageSize);
        bus.WriteByte(0xA0000, 0x55);
        bus.RestoreRam(captured);
        mapping.Disable();

        Assert.Equal(0xAA, bus.ReadByte(0xA0000));
        bus.WriteByte(0xA0000, 0x77);
        Assert.Equal(0xAA, bus.ReadByte(0xA0000));
    }

    [Fact]
    public void PermanentDeviceSupersedesWatchedRamEvenAfterWatchesAreCleared()
    {
        using var bus = NewBus();
        bus.WriteByte(0xA0000, 0x11);
        bus.SetReadWatchRanges([(0xA0000, PhysicalMemoryBus.PageSize)]);
        int reads = 0;
        bus.ReadWatched += _ => reads++;
        var device = new RegisterDevice();
        device.Data[0] = 0x88;
        bus.MapDevice(0xA0000, PhysicalMemoryBus.PageSize, device);
        Assert.Equal(0x88, bus.ReadByte(0xA0000));
        Assert.Equal(0, reads);
        bus.SetReadWatchRanges([]);
        Assert.Equal(0x88, bus.ReadByte(0xA0000));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MappingRamOrRomReappliesActiveReadWatches(bool rom)
    {
        using var bus = NewBus();
        bus.MapDevice(0xA0000, PhysicalMemoryBus.PageSize, new RegisterDevice());
        bus.SetReadWatchRanges([(0xA0000, PhysicalMemoryBus.PageSize)]);
        int reads = 0;
        bus.ReadWatched += address =>
        {
            Assert.Equal(0xA0000u, address);
            reads++;
        };
        if (rom)
        {
            bus.MapRom(0xA0000, new byte[] { 0xAA });
        }
        else
        {
            bus.MapRam(0xA0000, PhysicalMemoryBus.PageSize);
            bus.WriteByte(0xA0000, 0xAA);
        }

        Assert.Equal(0xAA, bus.ReadByte(0xA0000));
        Assert.Equal(1, reads);
        bus.WriteByte(0xA0000, 0x55);
        Assert.Equal(rom ? 0xAA : 0x55, bus.ReadByte(0xA0000));
        Assert.Equal(2, reads);
    }

    [Fact]
    public void OverlaySupersedesReadWatchAndClearDoesNotRestoreRam()
    {
        using var bus = NewBus();
        int reads = 0;
        bus.SetReadWatchRanges([(0xA0000, PhysicalMemoryBus.PageSize)]);
        bus.ReadWatched += _ => reads++;
        var device = new RegisterDevice();
        using PhysicalMemoryBus.DeviceMapping mapping = bus.CreateDeviceMapping(device);
        mapping.Update(0xA0000, PhysicalMemoryBus.PageSize);
        bus.ReadByte(0xA0000);
        bus.SetReadWatchRanges([]);

        Assert.Equal(0, reads);
        Assert.Equal(0u, device.LastOffset);
        bus.MapRam(0xA0000, PhysicalMemoryBus.PageSize);
        bus.MapRom(0xA0000, new byte[] { 0xAA });
        mapping.Disable();
        Assert.Equal(0xAA, bus.ReadByte(0xA0000));
    }

    [Fact]
    public void WatchChangesWhileOverlayIsActiveApplyAfterDisable()
    {
        using var bus = NewBus();
        var device = new RegisterDevice();
        using PhysicalMemoryBus.DeviceMapping mapping = bus.CreateDeviceMapping(device);
        mapping.Update(0xA0000, PhysicalMemoryBus.PageSize);
        bus.SetReadWatchRanges([(0xA0000, PhysicalMemoryBus.PageSize)]);
        mapping.Disable();
        int reads = 0;
        bus.ReadWatched += _ => reads++;
        bus.ReadByte(0xA0000);

        Assert.Equal(1, reads);
    }

    [Fact]
    public void InvalidRelocationPreservesMappingAndFourGigabyteEndIsValid()
    {
        using var bus = new PhysicalMemoryBus(0x100000, 1UL << 32) { A20Enabled = true };
        var device = new RegisterDevice();
        using PhysicalMemoryBus.DeviceMapping mapping = bus.CreateDeviceMapping(device);
        mapping.Update(0x70000000, 0x1000);
        Assert.Throws<ArgumentOutOfRangeException>(() => mapping.Update(0xFFFFF000, 0x2000));
        Assert.True(mapping.Enabled);
        Assert.Equal(0x70000000u, mapping.PhysicalBase);
        Assert.Equal(0x1000u, mapping.Length);
        bus.WriteByte(0x70000000, 0x66);
        Assert.Equal(0x66, device.Data[0]);
        mapping.Update(0xFC000000, 0x04000000);
        bus.WriteDword(0xFFFFFFFF - 3, 0xAABBCCDD);
        Assert.Equal(1, device.DwordWrites);
        Assert.Equal(0x03FFFFFCu, device.LastOffset);
        Assert.Equal(0xAABBCCDDu, bus.ReadDword(0xFFFFFFFC));
        Assert.Equal(1, device.DwordReads);
    }

    [Fact]
    public void DisposalIsIdempotentAndBusDisposalIsSafe()
    {
        var bus = NewBus();
        var device = new RegisterDevice();
        PhysicalMemoryBus.DeviceMapping mapping = bus.CreateDeviceMapping(device);
        mapping.Update(0xA0000, PhysicalMemoryBus.PageSize);
        mapping.Dispose();
        mapping.Dispose();
        Assert.Throws<ObjectDisposedException>(() => mapping.Update(0xA0000, PhysicalMemoryBus.PageSize));
        PhysicalMemoryBus.DeviceMapping liveMapping = bus.CreateDeviceMapping(device);
        liveMapping.Update(0xB0000, PhysicalMemoryBus.PageSize);
        bus.Dispose();
        Assert.Throws<ObjectDisposedException>(() => liveMapping.Update(0xA0000, PhysicalMemoryBus.PageSize));
        liveMapping.Dispose();
        liveMapping.Dispose();
        Assert.False(liveMapping.Enabled);
        mapping.Dispose();
        Assert.Throws<ObjectDisposedException>(() => mapping.Update(0xA0000, PhysicalMemoryBus.PageSize));
    }

    private static PhysicalMemoryBus NewBus()
    {
        return new PhysicalMemoryBus(2 * 1024 * 1024) { A20Enabled = true };
    }
}
