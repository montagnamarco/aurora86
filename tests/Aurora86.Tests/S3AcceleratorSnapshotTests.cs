using System.Buffers.Binary;
using Aurora86.Core;
using Aurora86.Core.Devices.Video;

namespace Aurora86.Tests;

/// <summary>
/// Exercises the Trio64V+ processor-data and register sequences used by 1990s
/// display drivers across a stopped machine snapshot boundary.
/// </summary>
public sealed class S3AcceleratorSnapshotTests
{
    private static Machine Prepared(bool packed24 = false)
    {
        var machine = new Machine();
        machine.VideoRegisters.SetVesaMode(VesaModes.Get(packed24 ? (ushort)0x112 : (ushort)0x101,
            VgaMemory.TotalSize), true);
        machine.Accelerator.ResetClip(640, 480);
        machine.Io.WriteWord(0xBAE8, 0x47); // Processor source, replace.
        return machine;
    }

    private static void Start(Machine machine, ushort command, ushort width, ushort height)
    {
        machine.Io.WriteWord(0x86E8, 2);
        machine.Io.WriteWord(0x82E8, 3);
        machine.Io.WriteWord(0x96E8, (ushort)(width - 1));
        machine.Io.WriteWord(0x92E8, (ushort)(height - 1));
        machine.Io.WriteWord(0x9AE8, command);
    }

    [Theory]
    [InlineData(false, false, 1)] // One byte of a 16-bit processor unit.
    [InlineData(false, true, 2)]  // Midway through monochrome expansion.
    [InlineData(true, false, 4)]  // One pixel and one byte of the next 24-bit pixel.
    public void E2E8PartialUploadResumesWithoutExecuting9AE8(bool packed24, bool mask, int prefix)
    {
        using Machine source = Prepared(packed24);
        using Machine target = Prepared(packed24);
        if (mask)
        {
            source.Io.WriteWord(0xBEE8, 0xA080);
            source.Io.WriteWord(0xBAE8, 0x27);
            source.Io.WriteWord(0xA6E8, 0xA5);
            source.Io.WriteWord(0xA2E8, 0x3C);
        }
        ushort command = packed24 ? (ushort)0x57A0 : (ushort)0x53A0;
        Start(source, command, mask ? (ushort)20 : (ushort)4, 2);
        byte[] data = Enumerable.Range(1, 32).Select(value => (byte)(value * 7)).ToArray();
        foreach (byte value in data[..prefix]) source.Accelerator.Mmio.WriteByte(0xE2E8, value);
        byte[] before = source.VideoMemory.CaptureMemory();
        byte[] engine = source.Accelerator.Capture();
        if (packed24)
        {
            // Four source bytes produce one pixel and leave byte 28 pending.
            Assert.Equal(28u, BinaryPrimitives.ReadUInt32LittleEndian(engine.AsSpan(8 + 28 * 4)));
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(engine.AsSpan(8 + 29 * 4)));
        }
        Assert.Equal(engine, RoundTripEngine(engine, target));

        // A dirty destination must lose its different in-flight upload.
        Start(target, 0x53A0, 40, 3);
        target.Accelerator.Mmio.WriteByte(0xE2E8, 0xEE);
        long operations = target.Accelerator.OperationsPerformed;
        int callbacks = 0;
        target.Accelerator.CommandTrace = (_, _, _, _, _, _, _, _) => callbacks++;
        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(source).Save(path);
            MachineSnapshot.Load(path).RestoreTo(target);
            Assert.Equal(before, target.VideoMemory.CaptureMemory());
            Assert.Equal(engine, target.Accelerator.Capture());
            Assert.Equal(operations, target.Accelerator.OperationsPerformed);
            Assert.Equal(0, callbacks);
            foreach (byte value in data[prefix..])
            {
                source.Accelerator.Mmio.WriteByte(0xE2E8, value);
                target.Accelerator.Mmio.WriteByte(0xE2E8, value);
            }
            Assert.Equal(source.VideoMemory.CaptureMemory(), target.VideoMemory.CaptureMemory());
            Assert.Equal(source.Accelerator.Capture(), target.Accelerator.Capture());
            Assert.NotEqual(before, target.VideoMemory.CaptureMemory());
            uint firstPixel = (uint)((3 * 640 + 2) * (packed24 ? 3 : 1));
            Assert.Equal(mask ? (byte)0x3C : (byte)7, target.VideoMemory.ReadLinear(firstPixel));
            if (packed24)
            {
                Assert.Equal(14, target.VideoMemory.ReadLinear(firstPixel + 1));
                // Existing Trio PutPixel does not yet write the third channel in
                // 24-bpp modes. This replay test does not certify that rendering path.
                Assert.Equal(source.VideoMemory.ReadLinear(firstPixel + 2),
                    target.VideoMemory.ReadLinear(firstPixel + 2));
            }
            Assert.Equal(0, callbacks);
        }
        finally { File.Delete(path); }
    }

    private static byte[] RoundTripEngine(byte[] state, Machine target)
    {
        target.Accelerator.Restore(state);
        return target.Accelerator.Capture();
    }

    [Fact]
    public void Pending9AE8LowByteCommitsExactlyOnceAfterRestore()
    {
        using Machine source = Prepared();
        using Machine target = Prepared();
        source.Io.WriteWord(0xBAE8, 0x27);
        source.Io.WriteWord(0xA6E8, 0xAB);
        source.Io.WriteByte(0x9AE8, 0xA0);
        MachineSnapshot.Capture(source).RestoreTo(target);
        Assert.Equal(0, target.Accelerator.OperationsPerformed);
        target.Io.WriteByte(0x9AE9, 0x40);
        Assert.Equal(1, target.Accelerator.OperationsPerformed);
        Assert.Equal(0xAB, target.VideoMemory.ReadLinear(0));
    }

    [Fact]
    public void LegacySnapshotDiscardsPendingE2E8DataOnUsedTarget()
    {
        using Machine source = Prepared();
        using Machine target = Prepared();
        Start(target, 0x53A0, 4, 2);
        target.Accelerator.Mmio.WriteByte(0xE2E8, 0xA5);
        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(source).Save(path);
            byte[] bytes = File.ReadAllBytes(path);
            // A file from before the Trio accelerator's own section existed:
            // its engine restores to its power-on state rather than the
            // pending upload the source's snapshot actually holds.
            File.WriteAllBytes(path, SnapshotSectionFile.WithoutSection(bytes, "TRIO"));
            MachineSnapshot.Load(path).RestoreTo(target);
            byte[] before = target.VideoMemory.CaptureMemory();
            target.Accelerator.Mmio.WriteByte(0xE2E8, 0x5A);
            Assert.Equal(before, target.VideoMemory.CaptureMemory());
            using var defaults = new Machine();
            Assert.Equal(defaults.Accelerator.Capture(), target.Accelerator.Capture());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A6E8ColourHalfAndBEE8ReadSelectorResumeAcrossSnapshot()
    {
        using Machine source = Prepared();
        using Machine target = Prepared();
        source.VideoRegisters.SetVesaMode(VesaModes.Get(0x140, VgaMemory.TotalSize), true);
        source.Io.WriteWord(0xA6E8, 0x5678);
        MachineSnapshot.Capture(source).RestoreTo(target);
        source.Io.WriteWord(0xA6E8, 0x1234);
        target.Io.WriteWord(0xA6E8, 0x1234);
        source.Io.WriteWord(0xBAE8, 0x27);
        target.Io.WriteWord(0xBAE8, 0x27);
        source.Io.WriteWord(0x9AE8, 0x40A0);
        target.Io.WriteWord(0x9AE8, 0x40A0);
        Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12 },
            Enumerable.Range(0, 4).Select(index => target.VideoMemory.ReadLinear((uint)index)).ToArray());

        source.Io.WriteWord(0xBEE8, 0xF003);
        MachineSnapshot.Capture(source).RestoreTo(target);
        for (int index = 0; index < 16; index++)
            Assert.Equal(source.Io.ReadWord(0xBEE8), target.Io.ReadWord(0xBEE8));
    }

    [Fact]
    public void Completed9AE8XorCommandIsNotRepeatedByRestore()
    {
        using Machine source = Prepared();
        using Machine target = Prepared();
        source.Io.WriteWord(0xA6E8, 0xAB);
        source.Io.WriteWord(0xBAE8, 0x25); // Foreground XOR destination.
        source.Io.WriteWord(0x9AE8, 0x40A0);
        Assert.Equal(0xAB, source.VideoMemory.ReadLinear(0));
        MachineSnapshot.Capture(source).RestoreTo(target);
        Assert.Equal(0xAB, target.VideoMemory.ReadLinear(0));
        Assert.Equal(0, target.Accelerator.OperationsPerformed);
    }

    [Fact]
    public void InvalidT3D1InMemorySnapshotFailsBeforeRamAndPicRestore()
    {
        using Machine source = Prepared();
        using Machine target = Prepared();
        MachineSnapshot snapshot = MachineSnapshot.Capture(source);
        byte[] payload = SnapshotSectionFile.GetPayload(snapshot, "TRIO");
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8 + 27 * 4), 4);
        target.Memory.WriteByte(0x1000, 0xA5);
        target.Cpu.State.Eip = 0x1234;
        byte[] pic = target.Interrupts.Capture();
        byte[] engine = target.Accelerator.Capture();
        Assert.Throws<InvalidDataException>(() => snapshot.RestoreTo(target));
        Assert.Equal(0xA5, target.Memory.ReadByte(0x1000));
        Assert.Equal(0x1234u, target.Cpu.State.Eip);
        Assert.Equal(pic, target.Interrupts.Capture());
        Assert.Equal(engine, target.Accelerator.Capture());
    }

    /// <summary>
    /// The "TRIO" section's own header has only a version and a length --
    /// no per-record kind, model or reserved-flags fields the way the last
    /// unsectioned builds' "DVS1" trailer did, so only those two faults have
    /// a section-format equivalent to test here.
    /// </summary>
    [Theory]
    [InlineData("version")] // This build reads Trio accelerator sections up to version 0.
    [InlineData("length")]  // A negative declared length is a truncated file.
    public void TrioSectionRejectsUnsupportedHeader(string fault)
    {
        using Machine machine = Prepared();
        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(machine).Save(path);
            byte[] bytes = File.ReadAllBytes(path);
            bytes = fault switch
            {
                "version" => SnapshotSectionFile.WithVersion(bytes, "TRIO", 2),
                "length" => SnapshotSectionFile.WithLengthField(bytes, "TRIO", -1),
                _ => throw new ArgumentOutOfRangeException(nameof(fault)),
            };
            File.WriteAllBytes(path, bytes);
            Assert.Throws<InvalidDataException>(() => MachineSnapshot.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void T3D1DuplicatePendingPortIsRejected()
    {
        using Machine machine = Prepared();
        machine.Io.WriteByte(0xA6E8, 0xAB);
        machine.Io.WriteByte(0x9AE8, 0xA0);
        byte[] bytes = machine.Accelerator.Capture();
        const int pendingEntries = 8 + 40 * 4 + 4;
        bytes.AsSpan(pendingEntries, 2).CopyTo(bytes.AsSpan(pendingEntries + 3));
        Assert.Throws<InvalidDataException>(() => machine.Accelerator.Restore(bytes));
    }

    [Theory]
    [InlineData(6, 0)]   // Header slot count.
    [InlineData(8 + 20 * 4, 16)] // Read-select cursor.
    [InlineData(8 + 27 * 4, 4)]  // Partial transfer byte count.
    [InlineData(8 + 30 * 4, 2)]  // Transfer-present boolean.
    [InlineData(8 + 33 * 4, 0)]  // Active rectangle width.
    [InlineData(8 + 38 * 4, 4)]  // Column outside rectangle.
    public void InvalidT3D1CursorIsRejectedWithoutChangingDevice(int offset, uint value)
    {
        using Machine machine = Prepared();
        Start(machine, 0x53A0, 4, 2);
        byte[] before = machine.Accelerator.Capture();
        byte[] invalid = (byte[])before.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(offset), value);
        byte[] memory = machine.VideoMemory.CaptureMemory();
        Assert.Throws<InvalidDataException>(() => machine.Accelerator.Restore(invalid));
        Assert.Equal(before, machine.Accelerator.Capture());
        Assert.Equal(memory, machine.VideoMemory.CaptureMemory());
    }
}
