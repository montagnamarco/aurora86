using Aurora86.Core;
using Aurora86.Core.Cpu;
using Aurora86.Core.Devices.Storage;
using Aurora86.Core.Firmware;

namespace Aurora86.Tests;

/// <summary>
/// The extended INT 13h functions -- sector numbers instead of cylinder,
/// head and sector -- and the geometry translation that lets the classic
/// functions reach a disk with more than 1024 cylinders.
/// </summary>
public class DiskExtensionsTests : IDisposable
{
    private readonly Machine _machine = new();
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "aurora-ext-" + Guid.NewGuid().ToString("N"));

    private const uint Packet = 0x3000;
    private const uint Buffer = 0x4000;

    public DiskExtensionsTests()
    {
        Directory.CreateDirectory(_scratch);
        _machine.RunInstructions(3000);
        _machine.Cpu.State.Halted = false;
        _machine.Cpu.SetSegment(SegReg.Cs, 0);
        _machine.Cpu.SetSegment(SegReg.Ss, 0);
        _machine.Cpu.SetSegment(SegReg.Ds, 0);
        _machine.Cpu.SetSegment(SegReg.Es, 0);
        _machine.Cpu.State.Esp = 0x7000;
    }

    public void Dispose()
    {
        _machine.Dispose();
        try { Directory.Delete(_scratch, true); } catch (IOException) { }
    }

    private DiskImage SmallDisk()
    {
        DiskImage image = DiskImage.CreateInMemory(0x80, 20, 16, 63);
        _machine.AttachDisk(image);
        return image;
    }

    /// <summary>Runs INT 13h with AH set and the other registers as given; reports the carry flag.</summary>
    private bool Int13(byte ah, Action<CpuState>? setup = null)
    {
        _machine.Memory.WriteByte(0x2000, 0xCD);
        _machine.Memory.WriteByte(0x2001, 0x13);
        _machine.Memory.WriteByte(0x2002, 0xF4);
        CpuState state = _machine.Cpu.State;
        state.Eip = 0x2000;
        state.Halted = false;
        state.SetReg8(4, ah);
        state.SetReg8(2, 0x80);      // DL: the first hard disk
        setup?.Invoke(state);
        // A PIO BIOS read executes INSW for each word rather than completing
        // in the native hook. Keep a bound while allowing the actual transfer.
        for (int instruction = 0; instruction < 100_000 && !state.Halted; instruction++)
        {
            _machine.Cpu.Step();
        }
        Assert.True(state.Halted);
        return _machine.Cpu.Flags.Carry;
    }

    private void WritePacket(int count, long sector, uint buffer = Buffer)
    {
        _machine.Memory.WriteByte(Packet, 0x10);
        _machine.Memory.WriteByte(Packet + 1, 0);
        _machine.Memory.WriteWord(Packet + 2, (ushort)count);
        _machine.Memory.WriteWord(Packet + 4, (ushort)(buffer & 0xF));
        _machine.Memory.WriteWord(Packet + 6, (ushort)(buffer >> 4));
        _machine.Memory.WriteDword(Packet + 8, (uint)sector);
        _machine.Memory.WriteDword(Packet + 12, (uint)(sector >> 32));
        _machine.Cpu.State.Si = (ushort)Packet;
    }

    [Fact]
    public void TheInstallationCheckAnswersForAHardDiskAndNotForAFloppy()
    {
        SmallDisk();
        Assert.False(Int13(0x41, s => s.Bx = 0x55AA));
        Assert.Equal(0xAA55, _machine.Cpu.State.Bx);
        Assert.Equal(1, _machine.Cpu.State.Cx & 1);

        _machine.AttachDisk(DiskImage.CreateInMemory(0x00, 80, 2, 18));
        Assert.True(Int13(0x41, s => { s.Bx = 0x55AA; s.SetReg8(2, 0x00); }));
    }

    [Fact]
    public void ExtendedReadFetchesTheSectorNamedByNumber()
    {
        DiskImage image = SmallDisk();
        byte[] marker = new byte[512];
        for (int i = 0; i < marker.Length; i++) marker[i] = (byte)(i * 7);
        Assert.True(image.WriteLinear(100, 1, marker));

        WritePacket(1, 100);
        Assert.False(Int13(0x42));
        Assert.Equal(0, _machine.Cpu.State.GetReg8(4));
        Assert.Equal(1, _machine.Memory.ReadWord(Packet + 2));
        for (int i = 0; i < 512; i++) Assert.Equal(marker[i], _machine.Memory.ReadByte(Buffer + (uint)i));
    }

    [Fact]
    public void ExtendedWriteStoresTheSectorNamedByNumber()
    {
        DiskImage image = SmallDisk();
        for (uint i = 0; i < 1024; i++) _machine.Memory.WriteByte(Buffer + i, (byte)(i ^ 0x5A));

        WritePacket(2, 200);
        Assert.False(Int13(0x43));
        Assert.Equal(2, _machine.Memory.ReadWord(Packet + 2));

        byte[] back = new byte[1024];
        Assert.True(image.ReadLinear(200, 2, back));
        for (int i = 0; i < 1024; i++) Assert.Equal((byte)(i ^ 0x5A), back[i]);
    }

    [Fact]
    public void ReadingPastTheEndFailsWithSectorNotFoundAndNothingTransferred()
    {
        DiskImage image = SmallDisk();
        WritePacket(1, image.TotalSectors);
        Assert.True(Int13(0x42));
        Assert.Equal(0x04, _machine.Cpu.State.GetReg8(4));
        Assert.Equal(0, _machine.Memory.ReadWord(Packet + 2));

        WritePacket(1, image.TotalSectors);
        Assert.True(Int13(0x44));
        WritePacket(1, image.TotalSectors - 1);
        Assert.False(Int13(0x44));
        Assert.False(Int13(0x47));
    }

    [Fact]
    public void ExtendedParametersDescribeTheDisk()
    {
        DiskImage image = SmallDisk();
        _machine.Memory.WriteWord(Packet, 0x1E);
        _machine.Cpu.State.Si = (ushort)Packet;
        Assert.False(Int13(0x48));

        Assert.Equal(0x1E, _machine.Memory.ReadWord(Packet));
        Assert.Equal(0x0002, _machine.Memory.ReadWord(Packet + 2));
        Assert.Equal(20u, _machine.Memory.ReadDword(Packet + 4));
        Assert.Equal(16u, _machine.Memory.ReadDword(Packet + 8));
        Assert.Equal(63u, _machine.Memory.ReadDword(Packet + 12));
        Assert.Equal((uint)image.TotalSectors, _machine.Memory.ReadDword(Packet + 16));
        Assert.Equal(512, _machine.Memory.ReadWord(Packet + 24));
        Assert.Equal(0xFFFFFFFFu, _machine.Memory.ReadDword(Packet + 26));
    }

    [Fact]
    public void ABufferTooSmallForTheParametersIsRefused()
    {
        SmallDisk();
        _machine.Memory.WriteWord(Packet, 0x10);
        _machine.Cpu.State.Si = (ushort)Packet;
        Assert.True(Int13(0x48));
        Assert.Equal(0x01, _machine.Cpu.State.GetReg8(4));
    }

    [Fact]
    public void ADiskWithTooManyCylindersIsPresentedWithMoreHeadsInstead()
    {
        // 4096 cylinders of 16 heads: 2 GB, shown as 1024 cylinders of 64.
        string path = Path.Combine(_scratch, "big.img");
        DiskImage image = DiskImage.CreateSparseFile(path, 0x80, 4096, 16, 63);
        _machine.AttachDisk(image);

        Assert.Equal((1024, 64, 63), DiskServices.PresentedGeometry(image));

        Assert.False(Int13(0x08));
        byte cl = _machine.Cpu.State.GetReg8(1), ch = _machine.Cpu.State.GetReg8(5);
        int lastCylinder = ch | ((cl & 0xC0) << 2);
        Assert.Equal(1023, lastCylinder);
        Assert.Equal(63, cl & 0x3F);
        Assert.Equal(63, _machine.Cpu.State.GetReg8(6));   // DH: 64 heads
    }

    [Fact]
    public void TheClassicFunctionsReachTheWholeDiskThroughTheTranslation()
    {
        string path = Path.Combine(_scratch, "big.img");
        DiskImage image = DiskImage.CreateSparseFile(path, 0x80, 4096, 16, 63);
        _machine.AttachDisk(image);

        // Well past the first 1024 physical cylinders.
        long sector = 3000L * 16 * 63 + 5 * 63 + 7;
        byte[] marker = new byte[512];
        marker[0] = 0xAB; marker[511] = 0xCD;
        WritePacket(1, sector);
        for (int i = 0; i < 512; i++) _machine.Memory.WriteByte(Buffer + (uint)i, marker[i]);
        Assert.False(Int13(0x43));

        // The same sector under the 64-head geometry: track = sector / 63.
        long track = sector / 63;
        int chsSector = (int)(sector % 63) + 1;
        int head = (int)(track % 64);
        int cylinder = (int)(track / 64);
        for (uint i = 0; i < 512; i++) _machine.Memory.WriteByte(0x5000 + i, 0);
        Assert.False(Int13(0x02, s =>
        {
            s.SetReg8(0, 1);
            s.SetReg8(5, (byte)cylinder);
            s.SetReg8(1, (byte)(chsSector | ((cylinder >> 2) & 0xC0)));
            s.SetReg8(6, (byte)head);
            s.Bx = 0x5000;
        }));
        Assert.Equal(0xAB, _machine.Memory.ReadByte(0x5000));
        Assert.Equal(0xCD, _machine.Memory.ReadByte(0x5000 + 511));
    }

    [Fact]
    public void ImagesLargerThanTwoGigabytesAreRefusedUntilThereIsFat32()
    {
        string path = Path.Combine(_scratch, "huge.img");
        Assert.Throws<ArgumentException>(() => HardDiskImage.Create(path, 3L * 1024 * 1024 * 1024));
    }
}

public class LargeDiskCreationTests
{
    [Fact]
    public void ACreatedDiskPastTheCylinderLimitDescribesItselfAsTheBiosPresentsIt()
    {
        string path = Path.Combine(Path.GetTempPath(), "aurora-1g-" + Guid.NewGuid().ToString("N") + ".img");
        try
        {
            // 1 GB: 2080 physical cylinders of 16 heads, presented as 520 of 64.
            HardDiskImage.Geometry physical = HardDiskImage.Create(path, 1024L * 1024 * 1024);
            Assert.Equal(2080, physical.Cylinders);
            HardDiskImage.Geometry presented = physical.Presented();
            Assert.Equal((520, 64), (presented.Cylinders, presented.Heads));

            using DiskImage image = DiskImage.OpenHardDisk(path);
            Assert.Equal(2080, image.Cylinders);

            byte[] sector = new byte[512];
            Assert.True(image.ReadLinear(0, 1, sector));
            Assert.Equal(63, sector[0x1BE + 5]);                       // last head of the partition: 64 heads
            int lastCylinder = sector[0x1BE + 7] | ((sector[0x1BE + 6] & 0xC0) << 2);
            Assert.Equal(519, lastCylinder);
            uint partitionSectors = BitConverter.ToUInt32(sector, 0x1BE + 12);
            Assert.Equal((uint)(presented.TotalSectors - 63), partitionSectors);

            Assert.True(image.ReadLinear(63, 1, sector));
            Assert.Equal(64, BitConverter.ToUInt16(sector, 26));       // BPB heads
            Assert.Equal(63, BitConverter.ToUInt16(sector, 24));       // BPB sectors per track
            Assert.Equal(partitionSectors, BitConverter.ToUInt32(sector, 32));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheBuilderDoesTheSameForItsFat16Images()
    {
        string path = Path.Combine(Path.GetTempPath(), "aurora-1g-" + Guid.NewGuid().ToString("N") + ".img");
        try
        {
            DiskImageBuilder.CreateFat16(path, 600L * 1024 * 1024, "GRANDE");
            using DiskImage image = DiskImage.OpenHardDisk(path);
            // 600 MB: 1219 cylinders, presented as 609 of 32 heads.
            Assert.Equal((609, 32, 63), DiskServices.PresentedGeometry(image));

            byte[] sector = new byte[512];
            Assert.True(image.ReadLinear(0, 1, sector));
            Assert.Equal(31, sector[0x1BE + 5]);
            Assert.True(image.ReadLinear(63, 1, sector));
            Assert.Equal(32, BitConverter.ToUInt16(sector, 26));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
