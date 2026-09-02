using Aurora386.Core.Memory;

namespace Aurora386.Tests;

public class PhysicalMemoryBusTests
{
    /// <summary>Dispositivo di prova: registra gli accessi e serve un buffer.</summary>
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
    public void RamAppenaCreataEAzzerata()
    {
        using var bus = new PhysicalMemoryBus();
        Assert.Equal(0, bus.ReadByte(0));
        Assert.Equal(0, bus.ReadByte(0x100000));
        Assert.Equal(0u, bus.ReadDword(0xFFFFC));
    }

    [Fact]
    public void ScritturaELetturaRispettanoLittleEndian()
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
    public void AccessoDisallineatoAttraversoConfineDiPaginaFunziona()
    {
        using var bus = new PhysicalMemoryBus();
        // Ultimi due byte della pagina 0 e primi due della pagina 1.
        uint address = PhysicalMemoryBus.PageSize - 2;
        bus.WriteDword(address, 0x11223344);

        Assert.Equal(0x11223344u, bus.ReadDword(address));
        Assert.Equal(0x3344, bus.ReadWord(address));
        Assert.Equal(0x1122, bus.ReadWord(address + 2));
        Assert.Equal(0x22, bus.ReadByte(PhysicalMemoryBus.PageSize));      // 3o byte
        Assert.Equal(0x11, bus.ReadByte(PhysicalMemoryBus.PageSize + 1));  // 4o byte
    }

    [Fact]
    public void ScrittureSuRomVengonoScartateMaLettureFunzionano()
    {
        using var bus = new PhysicalMemoryBus();
        byte[] rom = [0xAA, 0xBB, 0xCC, 0xDD];
        bus.MapRom(0xF0000, rom);

        Assert.Equal(0xAA, bus.ReadByte(0xF0000));
        Assert.Equal(0xDDCCBBAA, bus.ReadDword(0xF0000));

        bus.WriteByte(0xF0000, 0x00);
        bus.WriteDword(0xF0000, 0);
        Assert.Equal(0xAA, bus.ReadByte(0xF0000));   // invariata
    }

    [Fact]
    public void DispositivoMappatoRiceveOffsetRelativo()
    {
        using var bus = new PhysicalMemoryBus();
        var device = new FakeDevice();
        bus.MapDevice(0xA0000, PhysicalMemoryBus.PageSize * 2, device);

        bus.WriteByte(0xA0000, 0x42);
        Assert.Equal(0x42, device.Data[0]);          // offset 0, non 0xA0000

        bus.WriteByte(0xA1234, 0x99);
        Assert.Equal(0x99, device.Data[0x1234]);

        Assert.Equal(0x42, bus.ReadByte(0xA0000));
    }

    [Fact]
    public void LetturaLargaSuMmioUsaUnaSolaChiamata()
    {
        // Importante: i registri con effetti collaterali sulla lettura non
        // devono essere innescati piu' volte per un singolo accesso a 32 bit.
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
    public void MappaturaDispositivoRimpiazzaLaRamEMapRamLaRipristina()
    {
        using var bus = new PhysicalMemoryBus();
        bus.WriteByte(0xA0000, 0x11);
        Assert.Equal(0x11, bus.ReadByte(0xA0000));

        var device = new FakeDevice();
        bus.MapDevice(0xA0000, PhysicalMemoryBus.PageSize, device);
        Assert.Equal(0x00, bus.ReadByte(0xA0000));   // ora risponde il dispositivo

        bus.MapRam(0xA0000, PhysicalMemoryBus.PageSize);
        Assert.Equal(0x11, bus.ReadByte(0xA0000));   // la RAM sottostante e' intatta
    }

    [Fact]
    public void AccessiOltreLoSpazioIndirizzabileSonoInnocui()
    {
        using var bus = new PhysicalMemoryBus();
        Assert.Equal(0xFF, bus.ReadByte(0xFFFFFFFF));
        bus.WriteByte(0xFFFFFFFF, 0x42);            // non deve lanciare
        Assert.Equal(0xFF, bus.ReadByte(0xF0000000));
    }

    [Fact]
    public void MappaturaDisallineataVieneRifiutata()
    {
        using var bus = new PhysicalMemoryBus();
        var device = new FakeDevice();
        Assert.Throws<ArgumentException>(() => bus.MapDevice(0xA0001, 4096, device));
        Assert.Throws<ArgumentException>(() => bus.MapDevice(0xA0000, 100, device));
    }
}
