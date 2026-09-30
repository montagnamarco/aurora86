using System.Buffers.Binary;
using Aurora86.Core;
using Aurora86.Core.Devices.Video;

namespace Aurora86.Tests;

/// <summary>
/// Verifies both versions of the fixed ViRGE triangle-latch snapshot payload
/// without involving MachineSnapshot, PCI topology, or external video memory.
/// </summary>
public class S3VirgeSnapshotTests
{
    private const uint MmioBase = 0x01200000;
    private const uint Command = 0x83000008;
    private const uint AutoCommand = Command | 1;
    private const int LegacySnapshotSize = 84;
    private const int SnapshotSize = 104;
    private static readonly uint[] RegisterOffsets =
    [
        0xB4D8, 0xB4DC, 0xB4E0, 0xB4E4, 0xB500, 0xB53C, 0xB540,
        0xB544, 0xB548, 0xB54C, 0xB550, 0xB560, 0xB564, 0xB568,
        0xB56C, 0xB570, 0xB574, 0xB578, 0xB57C, 0xB4D4, 0xB4E8,
        0xB554, 0xB558, 0xB55C
    ];

    [Fact]
    public void SnapshotUsesDocumentedLittleEndianHeaderAndRegisterOrder()
    {
        using Machine machine = CreateMachine(out S3VirgeEngine engine);
        byte[] snapshot = engine.Capture();

        Assert.Equal(SnapshotSize, snapshot.Length);
        Assert.Equal(0x31475256u, BinaryPrimitives.ReadUInt32LittleEndian(snapshot));
        Assert.Equal((byte)'V', snapshot[0]);
        Assert.Equal((byte)'R', snapshot[1]);
        Assert.Equal((byte)'G', snapshot[2]);
        Assert.Equal((byte)'1', snapshot[3]);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(snapshot.AsSpan(4)));
        Assert.Equal(24, BinaryPrimitives.ReadUInt16LittleEndian(snapshot.AsSpan(6)));
        Assert.Equal(0x100u, BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(8)));
        Assert.Equal(AutoCommand, BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(24)));
        Assert.Equal(0x300u, BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(84)));
        Assert.Equal(32u, BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(88)));
        Assert.Equal(0x00018000u, BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(92)));
        Assert.Equal(0xFFFE8000u, BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(96)));
        Assert.Equal(0x00328000u, BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(100)));
    }

    [Fact]
    public void RestoreReplaysLatchedTriangleThroughLaterMmioTrigger()
    {
        using Machine machine = CreateMachine(out S3VirgeEngine engine);
        byte[] snapshot = engine.Capture();
        machine.VideoMemory.GetSpan(0, VgaMemory.TotalSize).Fill(0xCC);
        Write(machine, 0xB500, 0xFB000008);
        for (int index = 0; index < RegisterOffsets.Length; index++)
        {
            Write(machine, RegisterOffsets[index], 0xA5A5A5A5);
        }

        int events = 0;
        engine.UnsupportedCommandIssued += _ => events++;

        engine.Restore(snapshot);

        Assert.Equal(0, events);
        Assert.Equal(0xCC, machine.VideoMemory.ReadLinear(0x100));
        for (int index = 0; index < RegisterOffsets.Length; index++)
        {
            uint expected = BinaryPrimitives.ReadUInt32LittleEndian(
                snapshot.AsSpan(8 + index * sizeof(uint)));
            Assert.Equal(expected, engine.ReadDword(RegisterOffsets[index]));
        }

        machine.Memory.WriteDword(MmioBase + 0xB57C, 0x80040000);

        Assert.Equal(0, events);
        AssertTrianglePixels(machine);
        Write(machine, 0xB500, 0x08000008);
        Assert.Equal(1, events);
    }

    [Fact]
    public void LegacyVersionOneRestoresOriginalLatchesAndResetsDepthLatches()
    {
        using Machine machine = CreateMachine(out S3VirgeEngine engine);
        byte[] currentSnapshot = engine.Capture();
        byte[] legacySnapshot = currentSnapshot[..LegacySnapshotSize];
        BinaryPrimitives.WriteUInt16LittleEndian(legacySnapshot.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(legacySnapshot.AsSpan(6), 19);
        for (int index = 0; index < RegisterOffsets.Length; index++)
        {
            Write(machine, RegisterOffsets[index], 0xA5A5A5A5);
        }

        engine.Restore(legacySnapshot);

        for (int index = 0; index < 19; index++)
        {
            uint expected = BinaryPrimitives.ReadUInt32LittleEndian(
                legacySnapshot.AsSpan(8 + index * sizeof(uint)));
            Assert.Equal(expected, engine.ReadDword(RegisterOffsets[index]));
        }

        for (int index = 19; index < RegisterOffsets.Length; index++)
        {
            Assert.Equal(0u, engine.ReadDword(RegisterOffsets[index]));
        }

        byte[] resaved = engine.Capture();
        Assert.Equal(SnapshotSize, resaved.Length);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(resaved.AsSpan(4)));
        Assert.Equal(24, BinaryPrimitives.ReadUInt16LittleEndian(resaved.AsSpan(6)));
    }

    [Theory]
    [InlineData(SnapshotSize - 1)]
    [InlineData(SnapshotSize + 1)]
    public void InvalidLengthLeavesRegistersAndVideoMemoryUnchanged(int length)
    {
        using Machine machine = CreateMachine(out S3VirgeEngine engine);
        byte[] snapshot = engine.Capture();
        uint beforeRegister = engine.ReadDword(0xB4D8);
        byte beforePixel = machine.VideoMemory.ReadLinear(0x100);
        byte[] invalidSnapshot = new byte[length];
        snapshot.AsSpan(0, Math.Min(length, snapshot.Length)).CopyTo(invalidSnapshot);

        Assert.Throws<ArgumentException>(() => engine.Restore(invalidSnapshot));

        Assert.Equal(beforeRegister, engine.ReadDword(0xB4D8));
        Assert.Equal(beforePixel, machine.VideoMemory.ReadLinear(0x100));
    }

    [Theory]
    [InlineData(LegacySnapshotSize - 1)]
    [InlineData(LegacySnapshotSize + 1)]
    public void InvalidLegacyLengthLeavesRegistersAndVideoMemoryUnchanged(int length)
    {
        using Machine machine = CreateMachine(out S3VirgeEngine engine);
        byte[] currentSnapshot = engine.Capture();
        byte[] legacySnapshot = currentSnapshot[..LegacySnapshotSize];
        BinaryPrimitives.WriteUInt16LittleEndian(legacySnapshot.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(legacySnapshot.AsSpan(6), 19);
        uint beforeRegister = engine.ReadDword(0xB4D4);
        byte beforePixel = machine.VideoMemory.ReadLinear(0x100);
        byte[] invalidSnapshot = new byte[length];
        legacySnapshot.AsSpan(0, Math.Min(length, legacySnapshot.Length)).CopyTo(invalidSnapshot);

        Assert.Throws<ArgumentException>(() => engine.Restore(invalidSnapshot));

        Assert.Equal(beforeRegister, engine.ReadDword(0xB4D4));
        Assert.Equal(beforePixel, machine.VideoMemory.ReadLinear(0x100));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(6)]
    public void InvalidHeaderLeavesRegistersAndVideoMemoryUnchanged(int fieldOffset)
    {
        using Machine machine = CreateMachine(out S3VirgeEngine engine);
        byte[] snapshot = engine.Capture();
        uint beforeRegister = engine.ReadDword(0xB500);
        byte beforePixel = machine.VideoMemory.ReadLinear(0x100);
        if (fieldOffset == 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(snapshot, 0x31475257u);
        }
        else if (fieldOffset == 4)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(snapshot.AsSpan(4), 3);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(snapshot.AsSpan(6), 23);
        }

        Assert.Throws<ArgumentException>(() => engine.Restore(snapshot));

        Assert.Equal(beforeRegister, engine.ReadDword(0xB500));
        Assert.Equal(beforePixel, machine.VideoMemory.ReadLinear(0x100));
    }

    [Fact]
    public void TruncatedHeaderIsRejectedBeforeReadingFields()
    {
        Assert.Throws<ArgumentException>(() => S3VirgeEngine.ValidateSnapshot(new byte[7]));
    }

    private static void AssertTrianglePixels(Machine machine)
    {
        int[] pixels =
        [
            0x100 + 4 * 32 + 1 * 3,
            0x100 + 4 * 32 + 2 * 3,
            0x100 + 4 * 32 + 3 * 3,
            0x100 + 4 * 32 + 4 * 3,
            0x100 + 3 * 32 + 1 * 3,
            0x100 + 3 * 32 + 2 * 3,
            0x100 + 3 * 32 + 3 * 3,
            0x100 + 2 * 32 + 1 * 3,
            0x100 + 2 * 32 + 2 * 3,
            0x100 + 1 * 32 + 1 * 3
        ];

        byte[] expectedVideoMemory = new byte[VgaMemory.TotalSize];
        expectedVideoMemory.AsSpan().Fill(0xCC);
        for (int index = 0; index < pixels.Length; index++)
        {
            expectedVideoMemory[pixels[index]] = 0x20;
            expectedVideoMemory[pixels[index] + 1] = 0x40;
            expectedVideoMemory[pixels[index] + 2] = 0xE0;
        }

        Assert.Equal(expectedVideoMemory,
            machine.VideoMemory.GetSpan(0, VgaMemory.TotalSize).ToArray());
    }

    private static Machine CreateMachine(out S3VirgeEngine engine)
    {
        var machine = new Machine();
        engine = new S3VirgeEngine(machine.VideoMemory);
        machine.Memory.MapDevice(MmioBase, 0x10000, engine);
        Write(machine, 0xB4D8, 0x100);
        Write(machine, 0xB4E4, 32u << 16);
        Write(machine, 0xB54C, 0x20001000);
        Write(machine, 0xB550, 0x00007000);
        Write(machine, 0xB574, 0x00100000);
        Write(machine, 0xB56C, 0x00500000);
        Write(machine, 0xB568, 0xFFF00000);
        Write(machine, 0xB578, 4);
        Write(machine, 0xB4D4, 0x300);
        Write(machine, 0xB4E8, 32);
        Write(machine, 0xB554, 0x00018000);
        Write(machine, 0xB558, 0xFFFE8000);
        Write(machine, 0xB55C, 0x00328000);
        Write(machine, 0xB500, AutoCommand);
        Write(machine, 0xB57C, 0x80040000);
        machine.VideoMemory.GetSpan(0, VgaMemory.TotalSize).Fill(0xCC);
        return machine;
    }

    private static void Write(Machine machine, uint offset, uint value)
    {
        machine.Memory.WriteDword(MmioBase + offset, value);
    }
}
