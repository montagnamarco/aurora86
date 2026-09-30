using System.Buffers.Binary;
using Aurora86.Core;
using Aurora86.Core.Configuration;
using Aurora86.Core.Devices.Video;

namespace Aurora86.Tests;

/// <summary>
/// Verifies machine-level video snapshot topology, atomic preflight, the
/// ViRGE mapping, triangle latches and BitBLT state that must resume
/// together, and a file from before video topology had its own record --
/// the last unsectioned builds' "VID1" tail, which only <see
/// cref="LegacySnapshotFile"/> and <c>MachineSnapshot.ReadLegacyVideoTopology</c>
/// still speak, the sectioned format having its own "VMOD"/"VENG" sections
/// instead.
/// </summary>
public sealed class MachineVideoSnapshotTests
{
    private const uint ResetAperture = 0x70000000;
    private const uint VirgeMmioOffset = 0x01000000;
    private const uint Command = 0x83000008;

    /// <summary>
    /// Builds a legacy file that ends right after the serial mouse block --
    /// the shape a build from before video topology existed wrote -- and
    /// appends the given "VID1" record, possibly corrupted by the caller.
    /// </summary>
    private static byte[] LegacyFileWithVideoTopology(Machine machine, byte[] videoTopology)
    {
        string prefixPath = Path.GetTempFileName();
        try
        {
            LegacySnapshotFile.Write(machine, prefixPath, LegacySnapshotEnd.AfterSerialMouse);
            return [.. File.ReadAllBytes(prefixPath), .. videoTopology];
        }
        finally
        {
            File.Delete(prefixPath);
        }
    }

    /// <summary>
    /// A file whose video topology is present names the machine's model and,
    /// on a ViRGE, carries its engine bundle; restoring it into a matching
    /// machine reproduces that engine state exactly.
    /// </summary>
    [Theory]
    [InlineData(VmVideoModel.S3Trio64VPlus)]
    [InlineData(VmVideoModel.S3Virge325)]
    public void LegacyVideoTopologyRestoresTheCapturedModelAndEngine(VmVideoModel model)
    {
        using var source = new Machine(videoModel: model);
        if (source.Virge is not null)
        {
            EnableAndProgramTriangle(source);
        }

        byte[] videoTopology = LegacySnapshotFile.VideoTopology(source);
        byte[] file = LegacyFileWithVideoTopology(source, videoTopology);
        string path = Path.GetTempFileName();

        try
        {
            File.WriteAllBytes(path, file);
            using var target = new Machine(videoModel: model);
            MachineSnapshot.Load(path).RestoreTo(target);

            Assert.Equal(source.Virge?.CaptureEngines() ?? [], target.Virge?.CaptureEngines() ?? []);

            using var mismatched = new Machine(
                videoModel: model == VmVideoModel.S3Trio64VPlus ? VmVideoModel.S3Virge325 : VmVideoModel.S3Trio64VPlus);
            Assert.Throws<InvalidDataException>(() => MachineSnapshot.Load(path).RestoreTo(mismatched));
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
        using var machine = new Machine(videoModel: VmVideoModel.S3Virge325);
        byte[] tail = LegacySnapshotFile.VideoTopology(machine);

        switch (fault)
        {
            case VideoTailFault.Magic:
                tail[4] ^= 0xFF;
                break;
            case VideoTailFault.Version:
                BinaryPrimitives.WriteUInt16LittleEndian(tail.AsSpan(8), 2);
                break;
            case VideoTailFault.Model:
                BinaryPrimitives.WriteUInt16LittleEndian(tail.AsSpan(10), 2);
                break;
            case VideoTailFault.PayloadLength:
                BinaryPrimitives.WriteInt32LittleEndian(tail.AsSpan(0), -1);
                break;
            case VideoTailFault.PayloadLengthMismatch:
                int declared = BinaryPrimitives.ReadInt32LittleEndian(tail);
                BinaryPrimitives.WriteInt32LittleEndian(tail.AsSpan(0), declared - 1);
                break;
            case VideoTailFault.EngineLength:
                BinaryPrimitives.WriteInt32LittleEndian(tail.AsSpan(12), 83);
                break;
            case VideoTailFault.NegativeEngineLength:
                BinaryPrimitives.WriteInt32LittleEndian(tail.AsSpan(12), -1);
                break;
            case VideoTailFault.Truncated:
                tail = tail[..^1];
                break;
            case VideoTailFault.TrailingByte:
                tail = [.. tail, 0];
                break;
        }

        AssertInvalidFile(LegacyFileWithVideoTopology(machine, tail));
    }

    [Fact]
    public void EnginePayloadMustMatchVideoModel()
    {
        using var trioMachine = new Machine(videoModel: VmVideoModel.S3Trio64VPlus);
        byte[] trioTail = LegacySnapshotFile.VideoTopology(trioMachine);
        // Graft a bogus 4-byte engine block onto the Trio's own zero-length one.
        trioTail = [.. trioTail, 0, 0, 0, 0];
        BinaryPrimitives.WriteInt32LittleEndian(trioTail.AsSpan(0), 16); // 12-byte header + 4 engine bytes.
        BinaryPrimitives.WriteInt32LittleEndian(trioTail.AsSpan(12), 4);
        AssertInvalidFile(LegacyFileWithVideoTopology(trioMachine, trioTail));

        using var virgeMachine = new Machine(videoModel: VmVideoModel.S3Virge325);
        byte[] virgeTail = LegacySnapshotFile.VideoTopology(virgeMachine);
        byte[] emptyEngineTail = virgeTail[..16]; // Keep the header, strip the ViRGE's real engine bytes.
        BinaryPrimitives.WriteInt32LittleEndian(emptyEngineTail.AsSpan(0), 12);
        BinaryPrimitives.WriteInt32LittleEndian(emptyEngineTail.AsSpan(12), 0);
        AssertInvalidFile(LegacyFileWithVideoTopology(virgeMachine, emptyEngineTail));
    }

    /// <summary>
    /// A file with no video topology at all -- one from before it existed --
    /// defaults to the Trio64V+, the only adapter that existed then, and is
    /// still refused into a ViRGE machine.
    /// </summary>
    [Fact]
    public void LegacySnapshotRequiresTrio()
    {
        using var source = new Machine();
        source.Memory.WriteByte(0x1000, 0x5A);
        string legacyPath = Path.GetTempFileName();
        string resavedPath = Path.GetTempFileName();

        try
        {
            LegacySnapshotFile.Write(source, legacyPath, LegacySnapshotEnd.AfterSerialMouse);

            MachineSnapshot legacy = MachineSnapshot.Load(legacyPath);
            legacy.Save(resavedPath);

            using var trio = new Machine();
            legacy.RestoreTo(trio);
            Assert.Equal(0x5A, trio.Memory.ReadByte(0x1000));

            using var reloadedFromResave = new Machine();
            MachineSnapshot.Load(resavedPath).RestoreTo(reloadedFromResave);
            Assert.Equal(0x5A, reloadedFromResave.Memory.ReadByte(0x1000));

            using var virge = PreparedTarget();
            MachineState before = CaptureState(virge);
            Assert.Throws<InvalidDataException>(() => legacy.RestoreTo(virge));
            AssertState(before, virge);
        }
        finally
        {
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
        byte[] engine = SnapshotSectionFile.GetPayload(snapshot, "VENG");
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
        byte[] registers = SnapshotSectionFile.GetPayload(snapshot, "VREG");
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
        byte[] pci = SnapshotSectionFile.GetPayload(snapshot, "PCI ");
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
        SnapshotSectionFile.SetPayload(snapshot, "PCI ", pci);

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
        byte[] pci = SnapshotSectionFile.GetPayload(snapshot, "PCI ");
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
        byte[] pci = SnapshotSectionFile.GetPayload(snapshot, "PCI ");
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
        byte[] ram = SnapshotSectionFile.GetPayload(snapshot, "RAM ");
        SnapshotSectionFile.SetPayload(snapshot, "RAM ", ram[..^1]);

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
        byte[] vram = SnapshotSectionFile.GetPayload(snapshot, "VMEM");
        SnapshotSectionFile.SetPayload(snapshot, "VMEM", vram[..^1]);

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
        byte[] registers = SnapshotSectionFile.GetPayload(snapshot, "VREG");
        SnapshotSectionFile.SetPayload(snapshot, "VREG", registers[..^1]);

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
