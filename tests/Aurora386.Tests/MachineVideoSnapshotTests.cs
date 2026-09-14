using System.Buffers.Binary;
using System.Reflection;
using Aurora386.Core;
using Aurora386.Core.Configuration;
using Aurora386.Core.Devices.Video;

namespace Aurora386.Tests;

/// <summary>
/// Verifies machine-level video snapshot topology, atomic preflight, and the
/// ViRGE mapping, triangle latches and BitBLT state that must resume together.
/// </summary>
public sealed class MachineVideoSnapshotTests
{
    private const uint ResetAperture = 0x70000000;
    private const uint VirgeMmioOffset = 0x01000000;
    private const uint Command = 0x83000008;
    private const int TrioVideoTailLength = sizeof(int) + 12;
    // VGE1 header + VRG1 triangle block + V2D1 (15 latches and 11 upload fields).
    private const int VirgeBundleLength = 16 + 104 + 112;

    [Theory]
    [InlineData(VmVideoModel.S3Trio64VPlus)]
    [InlineData(VmVideoModel.S3Virge325)]
    public void VideoTailUsesDocumentedVid1Format(
        VmVideoModel model)
    {
        using var machine = new Machine(videoModel: model);
        int expectedEngineLength = model == VmVideoModel.S3Virge325 ? VirgeBundleLength : 0;
        int expectedPayloadLength = 12 + expectedEngineLength;
        string path = Path.GetTempFileName();

        try
        {
            MachineSnapshot.Capture(machine).Save(path);
            byte[] bytes = File.ReadAllBytes(path);
            int tail = bytes.Length - sizeof(int) - expectedPayloadLength;

            Assert.Equal(expectedPayloadLength, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(tail)));
            Assert.Equal(0x31444956u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(tail + 4)));
            Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(tail + 8)));
            Assert.Equal((ushort)model, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(tail + 10)));
            Assert.Equal(expectedEngineLength, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(tail + 12)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(VideoTailFault.Magic)]
    [InlineData(VideoTailFault.Version)]
    [InlineData(VideoTailFault.Model)]
    [InlineData(VideoTailFault.PayloadLength)]
    [InlineData(VideoTailFault.PayloadLengthMismatch)]
    [InlineData(VideoTailFault.EngineLength)]
    [InlineData(VideoTailFault.NegativeEngineLength)]
    [InlineData(VideoTailFault.Truncated)]
    [InlineData(VideoTailFault.TrailingByte)]
    public void InvalidVideoTailIsRejected(VideoTailFault fault)
    {
        byte[] bytes = SavedSnapshot(VmVideoModel.S3Virge325);
        int payloadLength = 12 + VirgeBundleLength;
        int tail = bytes.Length - sizeof(int) - payloadLength;

        switch (fault)
        {
            case VideoTailFault.Magic:
                bytes[tail + 4] ^= 0xFF;
                break;
            case VideoTailFault.Version:
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(tail + 8), 2);
                break;
            case VideoTailFault.Model:
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(tail + 10), 2);
                break;
            case VideoTailFault.PayloadLength:
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(tail), -1);
                break;
            case VideoTailFault.PayloadLengthMismatch:
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(tail), payloadLength - 1);
                break;
            case VideoTailFault.EngineLength:
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(tail + 12), 83);
                break;
            case VideoTailFault.NegativeEngineLength:
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(tail + 12), -1);
                break;
            case VideoTailFault.Truncated:
                Array.Resize(ref bytes, bytes.Length - 1);
                break;
            case VideoTailFault.TrailingByte:
                Array.Resize(ref bytes, bytes.Length + 1);
                break;
        }

        AssertInvalidFile(bytes);
    }

    [Fact]
    public void EnginePayloadMustMatchVideoModel()
    {
        byte[] trio = SavedSnapshot(VmVideoModel.S3Trio64VPlus);
        int trioTail = trio.Length - TrioVideoTailLength;
        Array.Resize(ref trio, trio.Length + sizeof(uint));
        BinaryPrimitives.WriteInt32LittleEndian(trio.AsSpan(trioTail), 16);
        BinaryPrimitives.WriteInt32LittleEndian(trio.AsSpan(trioTail + 12), sizeof(uint));
        AssertInvalidFile(trio);

        byte[] virge = SavedSnapshot(VmVideoModel.S3Virge325);
        int virgeTail = virge.Length - sizeof(int) - 12 - VirgeBundleLength;
        Array.Resize(ref virge, virge.Length - VirgeBundleLength);
        BinaryPrimitives.WriteInt32LittleEndian(virge.AsSpan(virgeTail), 12);
        BinaryPrimitives.WriteInt32LittleEndian(virge.AsSpan(virgeTail + 12), 0);
        AssertInvalidFile(virge);
    }

    [Fact]
    public void LegacySnapshotRequiresTrio()
    {
        using var source = new Machine();
        source.Memory.WriteByte(0x1000, 0x5A);
        string currentPath = Path.GetTempFileName();
        string legacyPath = Path.GetTempFileName();
        string resavedPath = Path.GetTempFileName();

        try
        {
            MachineSnapshot.Capture(source).Save(currentPath);
            File.Copy(currentPath, legacyPath, overwrite: true);
            using (FileStream file = File.OpenWrite(legacyPath))
            {
                file.SetLength(file.Length - TrioVideoTailLength);
            }

            MachineSnapshot legacy = MachineSnapshot.Load(legacyPath);
            legacy.Save(resavedPath);
            Assert.Equal(File.ReadAllBytes(legacyPath), File.ReadAllBytes(resavedPath));

            using var trio = new Machine();
            legacy.RestoreTo(trio);
            Assert.Equal(0x5A, trio.Memory.ReadByte(0x1000));

            using var virge = PreparedTarget();
            MachineState before = CaptureState(virge);
            Assert.Throws<InvalidDataException>(() => legacy.RestoreTo(virge));
            AssertState(before, virge);
        }
        finally
        {
            File.Delete(currentPath);
            File.Delete(legacyPath);
            File.Delete(resavedPath);
        }
    }

    [Theory]
    [InlineData(VmVideoModel.S3Trio64VPlus, VmVideoModel.S3Virge325)]
    [InlineData(VmVideoModel.S3Virge325, VmVideoModel.S3Trio64VPlus)]
    public void ModelMismatchIsAtomic(VmVideoModel sourceModel, VmVideoModel targetModel)
    {
        using var source = new Machine(videoModel: sourceModel);
        MachineSnapshot snapshot = MachineSnapshot.Capture(source);
        using Machine target = PreparedTarget(targetModel);
        MachineState before = CaptureState(target);

        Assert.Throws<InvalidDataException>(() => snapshot.RestoreTo(target));

        AssertState(before, target);
    }

    [Fact]
    public void InvalidEnginePayloadLeavesCdRamCpuVideoPciUnchanged()
    {
        using var source = new Machine(videoModel: VmVideoModel.S3Virge325);
        MachineSnapshot snapshot = MachineSnapshot.Capture(source);
        byte[] engine = SnapshotField<byte[]>(snapshot, "_videoEngine");
        engine[0] ^= 0xFF;

        using Machine target = PreparedTarget();
        MachineState before = CaptureState(target);

        Assert.Throws<InvalidDataException>(() => snapshot.RestoreTo(target));

        AssertState(before, target);
    }

    [Fact]
    public void CrtcPciBaseMismatchIsAtomic()
    {
        using var source = new Machine(videoModel: VmVideoModel.S3Virge325);
        MachineSnapshot snapshot = MachineSnapshot.Capture(source);
        byte[] registers = SnapshotField<byte[]>(snapshot, "_videoRegisters");
        registers[0x59] = 0x74;

        using Machine target = PreparedTarget();
        MachineState before = CaptureState(target);

        Assert.Throws<InvalidDataException>(() => snapshot.RestoreTo(target));

        AssertState(before, target);
    }

    [Theory]
    [InlineData(PciInterruptFault.Unaligned)]
    [InlineData(PciInterruptFault.SourceOutOfRange)]
    [InlineData(PciInterruptFault.InvalidLink)]
    [InlineData(PciInterruptFault.Duplicate)]
    public void InvalidPciInterruptPayloadIsAtomic(PciInterruptFault fault)
    {
        using var source = new Machine(videoModel: VmVideoModel.S3Virge325);
        MachineSnapshot snapshot = MachineSnapshot.Capture(source);
        byte[] pci = SnapshotField<byte[]>(snapshot, "_pci");
        int interruptLengthOffset = pci.Length - sizeof(int);
        byte[] invalidInterruptState = fault switch
        {
            PciInterruptFault.Unaligned => [0],
            PciInterruptFault.SourceOutOfRange => LittleEndian(0x40000),
            PciInterruptFault.InvalidLink => LittleEndian(0),
            PciInterruptFault.Duplicate => LittleEndian(0x40, 0x40),
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        Array.Resize(ref pci, pci.Length + invalidInterruptState.Length);
        BinaryPrimitives.WriteInt32LittleEndian(
            pci.AsSpan(interruptLengthOffset),
            invalidInterruptState.Length);
        invalidInterruptState.CopyTo(pci, interruptLengthOffset + sizeof(int));
        SetSnapshotField(snapshot, "_pci", pci);

        using Machine target = PreparedTarget();
        MachineState before = CaptureState(target);

        Assert.Throws<InvalidDataException>(() => snapshot.RestoreTo(target));

        AssertState(before, target);
    }

    [Fact]
    public void InvalidPciTopologyIsAtomic()
    {
        using var source = new Machine(videoModel: VmVideoModel.S3Virge325);
        MachineSnapshot snapshot = MachineSnapshot.Capture(source);
        byte[] pci = SnapshotField<byte[]>(snapshot, "_pci");
        int functionCount = BinaryPrimitives.ReadInt32LittleEndian(pci.AsSpan(sizeof(uint)));
        BinaryPrimitives.WriteInt32LittleEndian(pci.AsSpan(sizeof(uint)), functionCount - 1);

        using Machine target = PreparedTarget();
        MachineState before = CaptureState(target);

        Assert.Throws<InvalidDataException>(() => snapshot.RestoreTo(target));

        AssertState(before, target);
    }

    [Fact]
    public void InvalidVirgePciIdentityIsAtomic()
    {
        using var source = new Machine(videoModel: VmVideoModel.S3Virge325);
        MachineSnapshot snapshot = MachineSnapshot.Capture(source);
        byte[] pci = SnapshotField<byte[]>(snapshot, "_pci");
        int videoFunction = FindPciFunctionState(pci, 0x0020);
        pci[videoFunction + 2] ^= 0x01;

        using Machine target = PreparedTarget();
        MachineState before = CaptureState(target);

        Assert.Throws<InvalidDataException>(() => snapshot.RestoreTo(target));

        AssertState(before, target);
    }

    [Fact]
    public void RamSizeMismatchIsAtomic()
    {
        using var source = new Machine(videoModel: VmVideoModel.S3Virge325);
        MachineSnapshot snapshot = MachineSnapshot.Capture(source);
        byte[] ram = SnapshotField<byte[]>(snapshot, "_ram");
        SetSnapshotField(snapshot, "_ram", ram[..^1]);

        using Machine target = PreparedTarget();
        MachineState before = CaptureState(target);

        Assert.Throws<InvalidDataException>(() => snapshot.RestoreTo(target));

        AssertState(before, target);
    }

    [Fact]
    public void VramSizeMismatchIsAtomic()
    {
        using var source = new Machine(videoModel: VmVideoModel.S3Virge325);
        MachineSnapshot snapshot = MachineSnapshot.Capture(source);
        byte[] vram = SnapshotField<byte[]>(snapshot, "_videoMemory");
        SetSnapshotField(snapshot, "_videoMemory", vram[..^1]);

        using Machine target = PreparedTarget();
        MachineState before = CaptureState(target);

        Assert.Throws<InvalidDataException>(() => snapshot.RestoreTo(target));

        AssertState(before, target);
    }

    [Fact]
    public void TruncatedVideoStateIsRejectedBeforeRam()
    {
        using var source = new Machine(videoModel: VmVideoModel.S3Virge325);
        MachineSnapshot snapshot = MachineSnapshot.Capture(source);
        byte[] registers = SnapshotField<byte[]>(snapshot, "_videoRegisters");
        SetSnapshotField(snapshot, "_videoRegisters", registers[..^1]);

        using Machine target = PreparedTarget();
        MachineState before = CaptureState(target);

        Assert.Throws<InvalidDataException>(() => snapshot.RestoreTo(target));

        AssertState(before, target);
    }

    [Fact]
    public void SaveLoadResumesAutoExecuteTriangleWithoutDrawingDuringRestore()
    {
        using var source = new Machine(videoModel: VmVideoModel.S3Virge325);
        EnableAndProgramTriangle(source);
        source.VideoMemory.GetSpan(0, VgaMemory.TotalSize).Fill(0xCC);
        string path = Path.GetTempFileName();

        try
        {
            MachineSnapshot.Capture(source).Save(path);
            using var target = new Machine(videoModel: VmVideoModel.S3Virge325);
            MachineSnapshot.Load(path).RestoreTo(target);

            byte[] beforeTrigger = source.VideoMemory.GetSpan(0, VgaMemory.TotalSize).ToArray();
            Assert.Equal(beforeTrigger, target.VideoMemory.GetSpan(0, VgaMemory.TotalSize).ToArray());

            WriteEngine(source, 0xB57C, 0x80040000);
            WriteEngine(target, 0xB57C, 0x80040000);

            byte[] expected = source.VideoMemory.GetSpan(0, VgaMemory.TotalSize).ToArray();
            Assert.NotEqual(beforeTrigger, expected);
            Assert.Equal(expected, target.VideoMemory.GetSpan(0, VgaMemory.TotalSize).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RestoredBarHasNoStaleAlias()
    {
        const uint snapshotBase = 0x74000000;
        const uint staleBase = 0x78000000;
        using var source = new Machine(videoModel: VmVideoModel.S3Virge325);
        SetAperture(source, snapshotBase);
        MachineSnapshot snapshot = MachineSnapshot.Capture(source);

        using var target = new Machine(videoModel: VmVideoModel.S3Virge325);
        SetAperture(target, staleBase);
        Assert.Equal(0x3000u, target.Memory.ReadDword(staleBase + VirgeMmioOffset + 0x8504));

        snapshot.RestoreTo(target);

        Assert.Equal(0x3000u, target.Memory.ReadDword(snapshotBase + VirgeMmioOffset + 0x8504));
        Assert.Equal(uint.MaxValue, target.Memory.ReadDword(staleBase + VirgeMmioOffset + 0x8504));
    }

    private static Machine PreparedTarget(VmVideoModel model = VmVideoModel.S3Virge325)
    {
        var machine = new Machine(videoModel: model);
        machine.GuestHasItsOwnCompactDiscExtensions = true;
        machine.Memory.A20Enabled = true;
        machine.Memory.WriteByte(0x1000, 0xA5);
        machine.Cpu.State.Eip = 0x12345678;
        machine.VideoMemory.WriteLinear(0, 0xCC);
        if (machine.Virge is not null)
        {
            SetAperture(machine, 0x78000000);
        }

        return machine;
    }

    private static byte[] SavedSnapshot(VmVideoModel model)
    {
        using var machine = new Machine(videoModel: model);
        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(machine).Save(path);
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] LittleEndian(params uint[] values)
    {
        byte[] bytes = new byte[values.Length * sizeof(uint)];
        for (int index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint)),
                values[index]);
        }

        return bytes;
    }

    private static void AssertInvalidFile(byte[] bytes)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, bytes);
            Assert.Throws<InvalidDataException>(() => MachineSnapshot.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void EnableAndProgramTriangle(Machine machine)
    {
        SetAperture(machine, ResetAperture);
        WriteEngine(machine, 0xB4D8, 0x100);
        WriteEngine(machine, 0xB4E4, 32u << 16);
        WriteEngine(machine, 0xB54C, 0x20001000);
        WriteEngine(machine, 0xB550, 0x00007000);
        WriteEngine(machine, 0xB574, 0x00100000);
        WriteEngine(machine, 0xB56C, 0x00500000);
        WriteEngine(machine, 0xB568, 0xFFF00000);
        WriteEngine(machine, 0xB578, 4);
        WriteEngine(machine, 0xB500, Command | 1);
    }

    private static void SetAperture(Machine machine, uint baseAddress)
    {
        machine.Pci.WriteConfiguration(0x0020, 0x10, 4, baseAddress);
        machine.Pci.WriteConfiguration(0x0020, 4, 2, 0x0002);
    }

    private static void WriteEngine(Machine machine, uint offset, uint value)
    {
        machine.Memory.WriteDword(machine.LinearFrameBufferBase + VirgeMmioOffset + offset, value);
    }

    private static T SnapshotField<T>(MachineSnapshot snapshot, string name)
    {
        FieldInfo field = typeof(MachineSnapshot).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (T)field.GetValue(snapshot)!;
    }

    private static void SetSnapshotField<T>(MachineSnapshot snapshot, string name, T value)
    {
        FieldInfo field = typeof(MachineSnapshot).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(snapshot, value);
    }

    private static int FindPciFunctionState(ReadOnlySpan<byte> pci, ushort address)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(pci[sizeof(uint)..]);
        int offset = 2 * sizeof(uint);
        for (int index = 0; index < count; index++)
        {
            ushort candidate = BinaryPrimitives.ReadUInt16LittleEndian(pci[offset..]);
            offset += sizeof(ushort);
            if (candidate == address)
            {
                return offset;
            }

            offset += 256;
        }

        throw new InvalidOperationException("PCI snapshot function was not found.");
    }

    private static MachineState CaptureState(Machine machine)
    {
        return new MachineState(
            machine.GuestHasItsOwnCompactDiscExtensions,
            machine.Memory.A20Enabled,
            machine.Memory.ReadByte(0x1000),
            machine.Cpu.State.Eip,
            machine.VideoMemory.CaptureMemory(),
            machine.VideoRegisters.CaptureRegisters(),
            machine.Virge?.CaptureEngines() ?? [],
            machine.Pci.Capture(),
            machine.Interrupts.Capture());
    }

    private static void AssertState(MachineState expected, Machine actual)
    {
        Assert.Equal(expected.GuestOwnsCompactDisc, actual.GuestHasItsOwnCompactDiscExtensions);
        Assert.Equal(expected.A20Enabled, actual.Memory.A20Enabled);
        Assert.Equal(expected.RamByte, actual.Memory.ReadByte(0x1000));
        Assert.Equal(expected.Eip, actual.Cpu.State.Eip);
        Assert.Equal(expected.VideoMemory, actual.VideoMemory.CaptureMemory());
        Assert.Equal(expected.VideoRegisters, actual.VideoRegisters.CaptureRegisters());
        Assert.Equal(expected.VideoEngine, actual.Virge?.CaptureEngines() ?? []);
        Assert.Equal(expected.Pci, actual.Pci.Capture());
        Assert.Equal(expected.Interrupts, actual.Interrupts.Capture());
    }

    private sealed record MachineState(
        bool GuestOwnsCompactDisc,
        bool A20Enabled,
        byte RamByte,
        uint Eip,
        byte[] VideoMemory,
        byte[] VideoRegisters,
        byte[] VideoEngine,
        byte[] Pci,
        byte[] Interrupts);

    public enum VideoTailFault
    {
        Magic,
        Version,
        Model,
        PayloadLength,
        PayloadLengthMismatch,
        EngineLength,
        NegativeEngineLength,
        Truncated,
        TrailingByte
    }

    public enum PciInterruptFault
    {
        Unaligned,
        SourceOutOfRange,
        InvalidLink,
        Duplicate
    }
}
