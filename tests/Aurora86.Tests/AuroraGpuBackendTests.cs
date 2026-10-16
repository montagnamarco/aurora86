using System.Runtime.InteropServices;
using System.Buffers.Binary;
using Aurora86.Core;
using Aurora86.Core.Configuration;
using Aurora86.Core.Devices.Video;
using static Aurora86.Core.Devices.Video.AuroraGpuInterface;

namespace Aurora86.Tests;

/// <summary>
/// The Aurora adapter's version 1 host backend through its real engine and
/// BAR1 registers. Temporary output cannot replace guest VRAM before success.
/// </summary>
public sealed class AuroraGpuBackendTests
{
    private const uint Sentinel = 0x715293A4;
    private const uint Colour = 0xFF123456;
    private const int PitchPixels = 11;
    private const int OffsetPixels = 13;

    private sealed class Backend(uint version = BackendInterfaceVersion,
        uint kind = BackendKindOpenGl) : IAuroraGpuBackend
    {
        public uint InterfaceVersion => version;
        public uint Kind => kind;
        public uint Operations { get; set; } = BackendOperationOpaqueFill;
        public int Calls { get; private set; }
        public int BlendCalls { get; private set; }
        public int BatchCalls { get; private set; }
        public int BatchPixelCount { get; private set; }
        public int BatchOperationCount { get; private set; }
        public uint[]? BlendInput { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool Refuse { get; set; }
        public bool Throw { get; set; }
        public Action? BeforeReturn { get; set; }

        public bool TryFillOpaque(int width, int height, uint colour, uint[] pixels)
        {
            Calls++;
            Width = width;
            Height = height;
            Array.Fill(pixels, Refuse ? 0xDEADBEEFu : colour);
            BeforeReturn?.Invoke();
            if (Throw)
            {
                throw new InvalidOperationException("Lost test graphics context.");
            }
            return !Refuse;
        }

        public bool TryFillBatch(int pixelCount, uint[] pixels,
            IReadOnlyList<AuroraGpuFillOperation> operations)
        {
            BatchCalls++;
            BatchPixelCount = pixelCount;
            BatchOperationCount = operations.Count;
            foreach (AuroraGpuFillOperation operation in operations)
            {
                for (int index = operation.PixelOffset;
                     index < operation.PixelOffset + operation.Width * operation.Height; index++)
                {
                    pixels[index] = operation.Blend
                        ? AuroraGpu2DEngine.BlendOver(operation.Colour, pixels[index])
                        : operation.Colour;
                }
            }
            BeforeReturn?.Invoke();
            if (Throw)
            {
                throw new InvalidOperationException("Lost test graphics context.");
            }
            if (Refuse)
            {
                Array.Fill(pixels, 0xDEADBEEFu);
            }
            return !Refuse;
        }

        public bool TryFillBlend(int width, int height, uint colour, uint[] pixels)
        {
            BlendCalls++;
            BlendInput = pixels.AsSpan(0, width * height).ToArray();
            for (int index = 0; index < width * height; index++)
            {
                pixels[index] = AuroraGpu2DEngine.BlendOver(colour, pixels[index]);
            }
            BeforeReturn?.Invoke();
            if (Throw)
            {
                throw new InvalidOperationException("Lost test graphics context.");
            }
            if (Refuse)
            {
                Array.Fill(pixels, 0xDEADBEEFu);
                return false;
            }
            return true;
        }
    }

    [Theory]
    [InlineData(0u, 1u)]
    [InlineData(2u, 1u)]
    [InlineData(1u, 0u)]
    [InlineData(1u, 3u)]
    [InlineData(1u, uint.MaxValue)]
    public void InvalidContractPreservesThePreviousBackend(uint version, uint kind)
    {
        AuroraGpu2DEngine engine = Engine(out _, out _);
        var original = new Backend();
        engine.Backend = original;
        Assert.Throws<ArgumentException>(() => engine.Backend = new Backend(version, kind));
        Assert.Same(original, engine.Backend);
        engine.Backend = null;
        Assert.Null(engine.Backend);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ClippedOpaqueFillPreservesUncommittedVramAndPitchGuards(bool refuse, bool fail)
    {
        AuroraGpu2DEngine engine = Engine(out byte[] memory, out List<(uint, uint)> dirty);
        byte[] before = memory.ToArray();
        var backend = new Backend { Refuse = refuse, Throw = fail };
        bool unchangedBeforeReturn = false;
        backend.BeforeReturn = () => unchangedBeforeReturn = before.SequenceEqual(memory);
        engine.Backend = backend;
        engine.ClipEnabled = true;
        engine.ClipX = 2;
        engine.ClipY = 1;
        engine.ClipWidth = 3;
        engine.ClipHeight = 2;
        engine.FillRect(Surface(), -4, -3, 14, 9, Colour, blend: false);
        Assert.True(unchangedBeforeReturn);
        Assert.Equal(1, backend.Calls);
        Assert.Equal(3, backend.Width);
        Assert.Equal(2, backend.Height);
        Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(memory.AsSpan());
        for (int index = 0; index < pixels.Length; index++)
        {
            int row = (index - OffsetPixels) / PitchPixels;
            int column = (index - OffsetPixels) % PitchPixels;
            bool written = index >= OffsetPixels && row >= 1 && row < 3 &&
                column >= 2 && column < 5;
            Assert.Equal(written ? Colour : Sentinel, pixels[index]);
        }
        Assert.Single(dirty);
        Assert.Equal(((uint)((OffsetPixels + PitchPixels + 2) * 4),
            (uint)((PitchPixels + 3) * 4)), dirty[0]);
    }

    [Fact]
    public void BlendingAndEmptyClipsNeverCallTheBackend()
    {
        AuroraGpu2DEngine engine = Engine(out byte[] memory, out _);
        var backend = new Backend();
        engine.Backend = backend;
        engine.FillRect(Surface(), 1, 1, 2, 2, 0x80402010, blend: true);
        Assert.Equal(0, backend.Calls);
        AuroraGpu2DEngine reference = Engine(out byte[] expected, out _);
        reference.FillRect(Surface(), 1, 1, 2, 2, 0x80402010, blend: true);
        Assert.Equal(expected, memory);
        byte[] before = memory.ToArray();
        engine.FillRect(Surface(), int.MaxValue, int.MinValue, int.MaxValue, 1,
            Colour, blend: false);
        Assert.Equal(0, backend.Calls);
        Assert.Equal(before, memory);
    }

    [Fact]
    public void CpuWritesAndSurfaceAliasesRemainAuthoritative()
    {
        AuroraGpu2DEngine engine = Engine(out byte[] memory, out _);
        var backend = new Backend();
        engine.Backend = backend;
        engine.FillRect(Surface(), 0, 0, 8, 5, Colour, blend: false);
        Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(memory.AsSpan());
        pixels[OffsetPixels + PitchPixels + 1] = 0x12345678;
        var alias = new AuroraGpuSurface(true, OffsetPixels * 4, PitchPixels * 4, 8, 5);
        engine.FillRect(alias, 2, 1, 1, 1, 0xFFABCDEF, blend: false);
        Assert.Equal(0x12345678u, pixels[OffsetPixels + PitchPixels + 1]);
        Assert.Equal(0xFFABCDEFu, pixels[OffsetPixels + PitchPixels + 2]);
        Assert.Equal(2, backend.Calls);
    }

    [Theory]
    [InlineData(BackendKindOpenGl)]
    [InlineData(BackendKindVulkan)]
    public void BarOneRegisters060And064AreReadOnlyAndCapabilityBit4IsConditional(uint kind)
    {
        using var machine = new Machine(videoModel: VmVideoModel.AuroraGpu);
        machine.RunInstructions(200);
        AuroraGpuCard card = machine.AuroraGpu!;
        uint registerBase = card.RegisterBase;
        uint Read(uint offset) => machine.Memory.ReadDword(registerBase + offset);
        void Write(uint offset, uint value) => machine.Memory.WriteDword(registerBase + offset, value);
        Assert.Equal(0x00010002u, Read(VersionRegister));
        Assert.Equal(0x2Fu, Read(CapabilitiesRegister));
        Assert.Equal(0u, Read(BackendInfoRegister));
        Assert.Equal(0u, Read(BackendOperationsRegister));
        card.Backend = new Backend(kind: kind);
        Assert.Equal(kind, Read(BackendInfoRegister));
        Assert.Equal(1u, Read(BackendOperationsRegister));
        Assert.Equal(0x3Fu, Read(CapabilitiesRegister));
        Write(BackendInfoRegister, uint.MaxValue);
        Write(BackendOperationsRegister, uint.MaxValue);
        Write(CapabilitiesRegister, 0);
        Assert.Equal(kind, Read(BackendInfoRegister));
        Assert.Equal(1u, Read(BackendOperationsRegister));
        Assert.Equal(0x3Fu, Read(CapabilitiesRegister));
        card.Backend = null;
        Assert.Equal(0u, Read(BackendInfoRegister));
        Assert.Equal(0u, Read(BackendOperationsRegister));
        Assert.Equal(0x2Fu, Read(CapabilitiesRegister));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void BlendStagesCurrentCpuPixelsAndFallbackPreservesExactSoftwareResult(bool refuse, bool fail)
    {
        AuroraGpu2DEngine engine = Engine(out byte[] memory, out List<(uint, uint)> dirty);
        AuroraGpu2DEngine reference = Engine(out byte[] expected, out List<(uint, uint)> expectedDirty);
        Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(memory.AsSpan());
        for (int index = 0; index < pixels.Length; index++)
        {
            pixels[index] = unchecked((uint)index * 123456789u);
        }
        memory.CopyTo(expected, 0);
        byte[] before = memory.ToArray();
        bool uncommitted = false;
        var backend = new Backend { Operations = BackendOperationsKnown, Refuse = refuse, Throw = fail };
        backend.BeforeReturn = () => uncommitted = before.SequenceEqual(memory);
        engine.Backend = backend;
        const uint source = 0x8D102030;
        engine.FillRect(Surface(), -1, 1, 4, 2, source, blend: true);
        reference.FillRect(Surface(), -1, 1, 4, 2, source, blend: true);
        Assert.True(uncommitted);
        Assert.Equal(1, backend.BlendCalls);
        Assert.Equal(0, backend.Calls);
        uint[] input = backend.BlendInput!;
        for (int row = 0; row < 2; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                uint old = BinaryPrimitives.ReadUInt32LittleEndian(
                    before.AsSpan((OffsetPixels + (row + 1) * PitchPixels + column) * 4));
                Assert.Equal(old, input[row * 3 + column]);
            }
        }
        Assert.Equal(expected, memory);
        Assert.Equal(expectedDirty, dirty);
    }

    [Fact]
    public void UnsupportedOperationsStaySoftwareAndUnknownMaskBitsAreNotAdvertised()
    {
        AuroraGpu2DEngine engine = Engine(out _, out _);
        var backend = new Backend { Operations = 0x80000000u };
        engine.Backend = backend;
        engine.FillRect(Surface(), 0, 0, 1, 1, Colour, blend: false);
        engine.FillRect(Surface(), 0, 0, 1, 1, Colour, blend: true);
        Assert.Equal(0, backend.Calls);
        Assert.Equal(0, backend.BlendCalls);
        using var machine = new Machine(videoModel: VmVideoModel.AuroraGpu);
        machine.RunInstructions(200);
        machine.AuroraGpu!.Backend = backend;
        Assert.Equal(0u, machine.Memory.ReadDword(machine.AuroraGpu.RegisterBase + BackendOperationsRegister));
    }

    [Theory]
    [InlineData(BackendKindSoftware)]
    [InlineData(BackendKindVulkan)]
    public void SnapshotRestoresLogicalBackendRegistersAndAllVramAcrossHostProviders(uint targetKind)
    {
        using var source = new Machine(videoModel: VmVideoModel.AuroraGpu);
        source.RunInstructions(200);
        source.AuroraGpu!.Backend = new Backend { Operations = BackendOperationsKnown };
        source.AuroraGpu.Engine.FillRect(Surface(), 0, 0, 8, 5, Colour, blend: false);
        source.Memory.WriteDword(source.AuroraGpu.FrameBufferBase + 72, 0x12345678);
        byte[] snapshot = source.AuroraGpu.Capture();
        using var target = new Machine(videoModel: VmVideoModel.AuroraGpu);
        target.RunInstructions(200);
        var actual = targetKind == BackendKindSoftware ? null : new Backend(kind: targetKind);
        target.AuroraGpu!.Backend = actual;
        target.AuroraGpu.Restore(snapshot);
        Assert.Same(actual, target.AuroraGpu.Backend);
        Assert.Equal(snapshot, target.AuroraGpu.Capture());
        Assert.Equal(BackendKindOpenGl, target.Memory.ReadDword(target.AuroraGpu.RegisterBase + BackendInfoRegister));
        Assert.Equal(BackendOperationsKnown, target.Memory.ReadDword(target.AuroraGpu.RegisterBase + BackendOperationsRegister));
        Assert.Equal(0x3Fu, target.Memory.ReadDword(target.AuroraGpu.RegisterBase + CapabilitiesRegister));
        Assert.Equal(0x12345678u, target.Memory.ReadDword(target.AuroraGpu.FrameBufferBase + 72));
        if (actual is not null)
        {
            Assert.Equal(0, actual.Calls);
        }
    }

    [Fact]
    public void OldZeroReservedPayloadRestoresIntoNativeHostWithoutSerializingItsCounters()
    {
        using var machine = new Machine(videoModel: VmVideoModel.AuroraGpu);
        machine.RunInstructions(200);
        byte[] oldPayload = machine.AuroraGpu!.Capture();
        var actual = new Backend { Operations = BackendOperationsKnown };
        machine.AuroraGpu.Backend = actual;
        machine.AuroraGpu.Engine.FillRect(Surface(), 0, 0, 1, 1, Colour, blend: false);
        machine.AuroraGpu.Restore(oldPayload);
        Assert.Same(actual, machine.AuroraGpu.Backend);
        Assert.Equal(1, actual.Calls);
        Assert.Equal(oldPayload, machine.AuroraGpu.Capture());
        Assert.Equal(0u, machine.Memory.ReadDword(machine.AuroraGpu.RegisterBase + BackendInfoRegister));
        Assert.Equal(0x2Fu, machine.Memory.ReadDword(machine.AuroraGpu.RegisterBase + CapabilitiesRegister));
        machine.AuroraGpu.Reset();
        Assert.Equal(BackendKindOpenGl, machine.Memory.ReadDword(machine.AuroraGpu.RegisterBase + BackendInfoRegister));
        Assert.Equal(BackendOperationsKnown, machine.Memory.ReadDword(machine.AuroraGpu.RegisterBase + BackendOperationsRegister));
    }

    [Theory]
    [InlineData(3u, 0u)]
    [InlineData(0u, 1u)]
    [InlineData(1u, 4u)]
    public void MalformedSnapshotBackendFieldsAreRejectedBeforeAnyCardMutation(uint kind, uint operations)
    {
        using var machine = new Machine(videoModel: VmVideoModel.AuroraGpu);
        machine.RunInstructions(200);
        byte[] before = machine.AuroraGpu!.Capture();
        byte[] invalid = before.ToArray();
        const int MagicWords = 1;
        const int BackendKindSlot = 15;
        const int BackendOperationsSlot = 16;
        BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan((MagicWords + BackendKindSlot) * 4), kind);
        BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan((MagicWords + BackendOperationsSlot) * 4), operations);
        Assert.Throws<InvalidDataException>(() => machine.AuroraGpu.Restore(invalid));
        Assert.Equal(before, machine.AuroraGpu.Capture());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void BatchUsesOnlyDisjointPixelsAndCapturesClipBeforeItChanges(bool refuse, bool fail)
    {
        AuroraGpu2DEngine engine = Engine(out byte[] memory, out List<(uint, uint)> dirty);
        AuroraGpu2DEngine reference = Engine(out byte[] expected, out List<(uint, uint)> expectedDirty);
        byte[] before = memory.ToArray();
        bool uncommitted = false;
        var backend = new Backend { Operations = BackendOperationsKnown, Refuse = refuse, Throw = fail };
        backend.BeforeReturn = () => uncommitted = before.SequenceEqual(memory);
        engine.Backend = backend;
        engine.BeginBatch();
        for (int index = 0; index < 2; index++)
        {
            foreach (AuroraGpu2DEngine current in new[] { engine, reference })
            {
                current.ClipEnabled = true;
                current.ClipX = index == 0 ? 0 : 6;
                current.ClipY = index == 0 ? 0 : 4;
                current.ClipWidth = 2;
                current.ClipHeight = 1;
                current.FillRect(Surface(), -1, -1, 20, 20, index == 0 ? Colour : 0x80402010,
                    blend: index != 0);
            }
        }
        Assert.True(engine.HasPendingBatch);
        Assert.Equal(before, memory);
        engine.EndBatch();
        Assert.False(engine.HasPendingBatch);
        Assert.True(uncommitted);
        Assert.Equal(1, backend.BatchCalls);
        Assert.Equal(2, backend.BatchOperationCount);
        Assert.Equal(4, backend.BatchPixelCount);
        Assert.Equal(expected, memory);
        Assert.Equal(expectedDirty, dirty);
    }

    [Fact]
    public void OverlapAndTargetChangesFlushBeforeLaterBlendOrAliasWrites()
    {
        AuroraGpu2DEngine engine = Engine(out byte[] memory, out _);
        AuroraGpu2DEngine reference = Engine(out byte[] expected, out _);
        var backend = new Backend { Operations = BackendOperationsKnown };
        engine.Backend = backend;
        engine.BeginBatch();
        foreach (AuroraGpu2DEngine current in new[] { engine, reference })
        {
            current.FillRect(Surface(), 0, 0, 2, 2, Colour, blend: false);
            current.FillRect(Surface(), 1, 1, 2, 2, 0x80402010, blend: true);
            var alias = new AuroraGpuSurface(true, (OffsetPixels + 1) * 4, PitchPixels * 4, 7, 5);
            current.FillRect(alias, 0, 0, 2, 1, 0x80403020, blend: true);
        }
        engine.EndBatch();
        Assert.Equal(0, backend.BatchCalls);
        Assert.Equal(1, backend.Calls);
        Assert.Equal(2, backend.BlendCalls);
        Assert.Equal(expected, memory);
    }

    [Fact]
    public void CopyAndNestedBatchEndObserveCompletedFillPrefixes()
    {
        AuroraGpu2DEngine engine = Engine(out byte[] memory, out _);
        AuroraGpu2DEngine reference = Engine(out byte[] expected, out _);
        var backend = new Backend();
        engine.Backend = backend;
        engine.BeginBatch();
        engine.BeginBatch();
        engine.FillRect(Surface(), 0, 0, 1, 1, Colour, blend: false);
        engine.EndBatch();
        Assert.True(engine.HasPendingBatch);
        foreach (AuroraGpu2DEngine current in new[] { engine, reference })
        {
            if (current == reference)
            {
                current.FillRect(Surface(), 0, 0, 1, 1, Colour, blend: false);
            }
            current.CopyRect(Surface(), 0, 0, Surface(), 5, 4, 1, 1, blend: false);
        }
        Assert.False(engine.HasPendingBatch);
        engine.EndBatch();
        Assert.Equal(expected, memory);
    }

    [Fact]
    public void BatchCapacity128FlushesWithoutDroppingOrExpandingThinPixels()
    {
        byte[] memory = new byte[132 * 4];
        var engine = new AuroraGpu2DEngine(memory, _ => 0, _ => 0, (_, _) => { });
        var backend = new Backend();
        engine.Backend = backend;
        var target = new AuroraGpuSurface(true, 0, 132 * 4, 132, 1);
        engine.BeginBatch();
        for (int column = 0; column < 129; column++)
        {
            engine.FillRect(target, column, 0, 1, 1, Colour, blend: false);
        }
        engine.EndBatch();
        Assert.Equal(1, backend.BatchCalls);
        Assert.Equal(128, backend.BatchOperationCount);
        Assert.Equal(128, backend.BatchPixelCount);
        Assert.Equal(1, backend.Calls);
        Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(memory.AsSpan());
        for (int column = 0; column < pixels.Length; column++)
        {
            Assert.Equal(column < 129 ? Colour : 0u, pixels[column]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RingFenceAndErrorPublishOnlyAfterValidFillPrefixCommits(bool malformed)
    {
        using var machine = new Machine(videoModel: VmVideoModel.AuroraGpu);
        machine.RunInstructions(200);
        AuroraGpuCard card = machine.AuroraGpu!;
        var backend = new Backend();
        card.Backend = backend;
        const uint RingAddress = 0x80000;
        const ushort VideoFunction = 0x0020;
        machine.Pci.WriteConfiguration(VideoFunction, 4, 2, 7);
        uint registerBase = card.RegisterBase;
        void Write(uint offset, uint value) => machine.Memory.WriteDword(registerBase + offset, value);
        Write(RingBaseRegister, RingAddress);
        Write(RingSizeRegister, 4096);
        Write(RingControlRegister, RingControlEnable);
        Write(DisplayWidthRegister, 8);
        Write(DisplayHeightRegister, 5);
        Write(DisplayPitchRegister, 8 * 4);
        Write(DisplayControlRegister, DisplayControlScanoutEnable);
        uint[] commands = [Header(OpcodeFillRect, 8), 0, 0, 0, 0, 1, 1, Colour,
            Header(OpcodeFillRect, 8), 0, 0, 7, 4, 1, 1, Colour,
            malformed ? Header(0xFFFF, 1) : Header(OpcodeFence, 3), 0, 77];
        for (int index = 0; index < commands.Length; index++)
        {
            machine.Memory.WriteDword(RingAddress + (uint)index * 4, commands[index]);
        }
        Write(RingTailRegister, (uint)commands.Length * 4);
        Assert.Equal(1, backend.BatchCalls);
        Assert.False(card.Engine.HasPendingBatch);
        Assert.Equal(Colour, machine.Memory.ReadDword(card.FrameBufferBase));
        Assert.Equal(Colour, machine.Memory.ReadDword(card.FrameBufferBase + (4 * 8 + 7) * 4));
        Assert.Equal(malformed ? ErrorUnknownOpcode : ErrorNone,
            machine.Memory.ReadDword(registerBase + ErrorRegister));
        Assert.Equal(malformed ? 0u : 77u, machine.Memory.ReadDword(registerBase + FenceRegister));
        Assert.Equal(64u + (malformed ? 0u : 12u), machine.Memory.ReadDword(registerBase + RingHeadRegister));
        byte[] snapshot = card.Capture();
        card.Restore(snapshot);
        Assert.Equal(snapshot, card.Capture());
    }

    [Fact]
    public void UnsupportedBlendFlushesQueuedOpaquePrefixBeforeSoftwareReadsItsDestination()
    {
        AuroraGpu2DEngine engine = Engine(out byte[] memory, out _);
        AuroraGpu2DEngine reference = Engine(out byte[] expected, out _);
        var backend = new Backend();
        engine.Backend = backend;
        engine.BeginBatch();
        foreach (AuroraGpu2DEngine current in new[] { engine, reference })
        {
            current.FillRect(Surface(), 0, 0, 1, 1, Colour, blend: false);
            current.FillRect(Surface(), 7, 4, 1, 1, Colour, blend: false);
            current.FillRect(Surface(), 0, 0, 1, 1, 0x80402010, blend: true);
        }
        engine.EndBatch();
        Assert.Equal(1, backend.BatchCalls);
        Assert.Equal(0, backend.BlendCalls);
        Assert.Equal(expected, memory);
    }

    [Fact]
    public void BarZeroAddressRingRetainsRamPriorityBusMasterReads()
    {
        (byte[] Pixels, uint[] Registers, int Batches, int Singles) Run(bool native)
        {
            using var machine = new Machine(ramSize: 64 * 1024 * 1024, videoModel: VmVideoModel.AuroraGpu);
            machine.RunInstructions(200);
            AuroraGpuCard card = machine.AuroraGpu!;
            var backend = new Backend();
            if (native)
            {
                card.Backend = backend;
            }
            const ushort VideoFunction = 0x0020;
            machine.Pci.WriteConfiguration(VideoFunction, 0x10, 4, 0x02000000);
            machine.Pci.WriteConfiguration(VideoFunction, 4, 2, 7);
            uint registerBase = card.RegisterBase;
            uint ringAddress = card.FrameBufferBase;
            void Write(uint offset, uint value) => machine.Memory.WriteDword(registerBase + offset, value);
            Write(RingBaseRegister, ringAddress);
            Write(RingSizeRegister, 4096);
            Write(RingControlRegister, RingControlEnable);
            Write(DisplayWidthRegister, 64);
            Write(DisplayHeightRegister, 1);
            Write(DisplayPitchRegister, 64 * 4);
            Write(DisplayOffsetRegister, 0);
            Write(DisplayControlRegister, DisplayControlScanoutEnable);
            uint[] commands = [Header(OpcodeFillRect, 8), 0, 0, 16, 0, 1, 1, Header(0xFFFF, 1),
                Header(OpcodeFillRect, 8), 0, 0, 20, 0, 1, 1, Colour,
                Header(OpcodeFence, 3), 0, 77];
            for (int index = 0; index < commands.Length; index++)
            {
                machine.Memory.WriteDword(ringAddress + (uint)index * 4, commands[index]);
            }
            Write(RingTailRegister, (uint)commands.Length * 4);
            Assert.False(card.Engine.HasPendingBatch);
            uint[] registers = [machine.Memory.ReadDword(registerBase + ErrorRegister),
                machine.Memory.ReadDword(registerBase + ErrorOffsetRegister),
                machine.Memory.ReadDword(registerBase + RingHeadRegister),
                machine.Memory.ReadDword(registerBase + FenceRegister)];
            return (card.VideoMemoryBytes.ToArray(), registers, backend.BatchCalls, backend.Calls);
        }
        var reference = Run(false);
        var actual = Run(true);
        Assert.Equal(reference.Pixels, actual.Pixels);
        Assert.Equal(reference.Registers, actual.Registers);
        // Bus-master reads below RamSize retain the RAM page priority over BAR0.
        // Engine writes private VRAM; the original RAM fence remains visible.
        Assert.Equal(new uint[] { 0u, 0u, 76u, 77u }, actual.Registers);
        Assert.Equal(0, actual.Batches);
        Assert.Equal(2, actual.Singles);
    }

    private static AuroraGpuSurface Surface() => new(true, OffsetPixels * 4, PitchPixels * 4, 8, 5);

    private static AuroraGpu2DEngine Engine(out byte[] memory, out List<(uint, uint)> dirty)
    {
        memory = new byte[128 * 4];
        MemoryMarshal.Cast<byte, uint>(memory.AsSpan()).Fill(Sentinel);
        var changes = new List<(uint, uint)>();
        dirty = changes;
        return new AuroraGpu2DEngine(memory, _ => 0, _ => 0,
            (offset, count) => changes.Add((offset, count)));
    }
}
