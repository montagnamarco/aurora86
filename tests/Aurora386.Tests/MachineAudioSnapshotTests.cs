using System.Buffers.Binary;
using Aurora386.Core;
using Aurora386.Core.Configuration;
using Aurora386.Core.Devices.IsaPnp;
using Aurora386.Core.IO;

namespace Aurora386.Tests;

/// <summary>
/// Replays DOS/Windows ISA PnP resource programming and SB16 DMA audio across
/// complete PC snapshots, including historical omissions and atomic preflight.
/// </summary>
public sealed class MachineAudioSnapshotTests
{
    private static void PnpWrite(Machine machine, byte register, byte value)
    {
        machine.Io.WriteByte(0x279, register);
        machine.Io.WriteByte(0xA79, value);
    }

    private static void Enumerate(Machine machine)
    {
        byte key = 0x6A;
        for (int index = 0; index < 32; index++)
        {
            machine.Io.WriteByte(0x279, key);
            key = (byte)((key >> 1) | (((key ^ (key >> 1)) & 1) << 7));
        }
        for (byte csn = 1; csn <= 2; csn++)
        {
            PnpWrite(machine, 3, 0);
            machine.Io.WriteByte(0x279, 1);
            for (int index = 0; index < 144; index++)
            {
                machine.Io.ReadByte(machine.IsaPlugAndPlay.ReadDataPort);
            }
            PnpWrite(machine, 6, csn);
        }
    }

    private static void Select(Machine machine, string id)
    {
        IsaPnpCard card = machine.IsaPlugAndPlay.Cards.Single(card => card.LogicalDevices[0].Id == id);
        PnpWrite(machine, 3, card.Csn);
        PnpWrite(machine, 7, 0);
    }

    private static void RelocateAudio(Machine machine)
    {
        Enumerate(machine);
        Select(machine, "CTL0031");
        PnpWrite(machine, 0x30, 0);
        PnpWrite(machine, 0x60, 2);
        PnpWrite(machine, 0x61, 0x40);
        PnpWrite(machine, 0x70, 7);
        PnpWrite(machine, 0x74, 3);
        PnpWrite(machine, 0x75, 6);
        PnpWrite(machine, 0x30, 1);
        PnpWrite(machine, 0, 0xBC); // READ_DATA at 2F3h, away from the audio block.
    }

    private static void Command(Machine machine, params byte[] values)
    {
        ushort port = (ushort)(machine.SoundBlaster.BasePort + 0x0C);
        foreach (byte value in values)
        {
            machine.Io.WriteByte(port, value);
        }
    }

    private static void Play(Machine machine, bool word)
    {
        for (int index = 0; index < 16; index++)
        {
            machine.Memory.WriteByte(0x20000u + (uint)index, (byte)(31 + index * 11));
        }
        ushort address = word ? (ushort)0xC8 : (ushort)0x06; // DMA6 or DMA3.
        ushort count = word ? (ushort)0xCA : (ushort)0x07;
        machine.Io.WriteByte(word ? (ushort)0xD8 : (ushort)0x0C, 0);
        machine.Io.WriteByte(address, 0);
        machine.Io.WriteByte(address, 0);
        machine.Io.WriteByte(count, 7);
        machine.Io.WriteByte(count, 0);
        machine.Io.WriteByte(word ? (ushort)0x89 : (ushort)0x82, 2);
        machine.Io.WriteByte(word ? (ushort)0xD6 : (ushort)0x0B, word ? (byte)0x5A : (byte)0x5B);
        machine.Io.WriteByte(word ? (ushort)0xD4 : (ushort)0x0A, word ? (byte)2 : (byte)3);
        Command(machine, 0x41, 0x7D, 0);
        Command(machine, word ? (byte)0xB4 : (byte)0xC4, word ? (byte)0x10 : (byte)0, 3, 0);
    }

    private static short[] Drain(Machine machine)
    {
        short[] samples = new short[machine.SoundBlaster.AvailableSamples];
        Assert.Equal(samples.Length, machine.Audio.ReadSamples(samples));
        return samples;
    }

    private static byte[] Save(MachineSnapshot snapshot)
    {
        string path = Path.GetTempFileName();
        try
        {
            snapshot.Save(path);
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static MachineSnapshot Load(byte[] data)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, data);
            return MachineSnapshot.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false, false, VmVideoModel.S3Trio64VPlus)]
    [InlineData(true, true, VmVideoModel.S3Trio64VPlus)]
    [InlineData(false, true, VmVideoModel.S3Virge325)]
    [InlineData(true, false, VmVideoModel.S3Virge325)]
    public void Pnp60717475AndDspC4B4ReplayPcmDmaAndIrq82AfterFileRestore(
        bool word, bool afterBlock, VmVideoModel model)
    {
        using var source = new Machine(videoModel: model);
        using var target = new Machine(videoModel: model);
        source.EnableUsb();
        target.EnableUsb();
        RelocateAudio(source);
        Play(source, word);
        source.SoundBlaster.Advance(afterBlock ? 125 : 109);
        Command(source, 0x41, 0x7D); // Also preserve the partially written next rate.
        Command(target, 0xF2, 0x10, 0xFF, 0xE3);
        int targetIrqs = target.SoundBlaster.InterruptCount;
        int sourceIrqs = source.SoundBlaster.InterruptCount;
        byte[] saved = Save(MachineSnapshot.Capture(source));
        MachineSnapshot loaded = Load(saved);
        Assert.Equal(saved, Save(loaded));
        int writes = 0;
        target.Io.PortAccessed += (_, write, _) =>
        {
            if (write)
            {
                writes++;
            }
        };
        loaded.RestoreTo(target);
        Assert.Equal(0, writes);
        Assert.Equal(targetIrqs, target.SoundBlaster.InterruptCount);
        Assert.Equal(source.Interrupts.Capture(), target.Interrupts.Capture());
        Assert.Equal(source.Dma.Capture(), target.Dma.Capture());
        Assert.Equal(source.SoundBlaster.Capture(), target.SoundBlaster.Capture());
        Assert.Equal(source.IsaPlugAndPlay.Capture(), target.IsaPlugAndPlay.Capture());
        Assert.Equal(0xFF, target.Io.ReadByte(0x22C)); // Old DSP block has been removed.
        Assert.Equal(0x7F, target.Io.ReadByte(0x24C));
        Assert.Equal((ushort)0x2F3, target.IsaPlugAndPlay.ReadDataPort);
        Assert.Equal(0xFF, target.Io.ReadByte(0x203));
        short[] queued = Drain(source);
        Assert.Contains(queued, sample => sample != 0);
        Assert.Equal(queued, Drain(target));
        Command(source, 0);
        Command(target, 0);
        foreach (double duration in new[] { 17.0, 61.0, 123.0, 55.0 })
        {
            source.SoundBlaster.Advance(duration);
            target.SoundBlaster.Advance(duration);
            Assert.Equal(Drain(source), Drain(target));
            Assert.Equal(source.Dma.Capture(), target.Dma.Capture());
            Assert.Equal(source.Interrupts.Capture(), target.Interrupts.Capture());
            Assert.Equal(source.SoundBlaster.InterruptCount - sourceIrqs,
                target.SoundBlaster.InterruptCount - targetIrqs);
            Assert.Equal(source.SoundBlaster.Mixer.InterruptStatus, target.SoundBlaster.Mixer.InterruptStatus);
            ushort acknowledge = word ? (ushort)0x24F : (ushort)0x24E;
            source.Io.ReadByte(acknowledge);
            target.Io.ReadByte(acknowledge);
            Assert.Equal(0, target.SoundBlaster.Mixer.InterruptStatus);
        }
    }

    [Fact]
    public void InactivePnp61PendingBaseRemainsDistinctFromAppliedDecodeUntil30Activation()
    {
        using var source = new Machine();
        using var target = new Machine();
        RelocateAudio(source);
        Select(source, "CTL0031");
        PnpWrite(source, 0x30, 0);
        PnpWrite(source, 0x61, 0x60);
        MachineSnapshot.Capture(source).RestoreTo(target);
        Assert.False(target.SoundBlaster.Enabled);
        Assert.Equal((ushort)0x240, target.SoundBlaster.BasePort);
        Assert.Equal(0x60, target.Io.ReadByte(0x2F3));
        Assert.Equal(0xFF, target.Io.ReadByte(0x24C));
        PnpWrite(target, 0x30, 1);
        Assert.Equal((ushort)0x260, target.SoundBlaster.BasePort);
        Assert.Equal(0x7F, target.Io.ReadByte(0x26C));
    }

    [Theory]
    [InlineData(0)] // No trailer.
    [InlineData(1)] // Empty historical trailer.
    [InlineData(2)] // Historical Trio-only trailer.
    public void LegacyAbsenceResetsDmaDspAndPnpWithoutFabricatingAudioRecords(int legacyKind)
    {
        using var source = new Machine();
        using var target = new Machine();
        RelocateAudio(target);
        Play(target, false);
        target.SoundBlaster.Advance(170);
        byte[] modern = Save(MachineSnapshot.Capture(source));
        int trailer = SnapshotDeviceFile.TrailerOffset(modern);
        byte[] legacy = modern[..trailer];
        if (legacyKind != 0)
        {
            int trioLength = source.Accelerator.Capture().Length;
            int length = legacyKind == 1 ? 12 : 24 + trioLength;
            legacy = modern[..(trailer + length)];
            BinaryPrimitives.WriteInt32LittleEndian(legacy.AsSpan(trailer), length - 4);
            BinaryPrimitives.WriteUInt16LittleEndian(legacy.AsSpan(trailer + 10), (ushort)(legacyKind - 1));
        }
        MachineSnapshot loaded = Load(legacy);
        Assert.Equal(legacy, Save(loaded));
        loaded.RestoreTo(target);
        Assert.Equal(source.Dma.Capture(), target.Dma.Capture());
        Assert.Equal(source.SoundBlaster.Capture(), target.SoundBlaster.Capture());
        Assert.Equal(source.IsaPlugAndPlay.Capture(), target.IsaPlugAndPlay.Capture());
        Assert.Equal(source.Interrupts.Capture(), target.Interrupts.Capture());
        Assert.Equal(0x7F, target.Io.ReadByte(0x22C));
        Assert.Equal(0xFF, target.Io.ReadByte(0x24C));
        Assert.Equal(legacy, Save(loaded));
    }

    [Theory]
    [InlineData(0x220)]
    [InlineData(0x380)]
    public void PciBar4OnDirtyTargetMovesAwayBeforeRestoredIsaAndOplAreUsed(uint dirtyBase)
    {
        using var source = new Machine();
        using var target = new Machine();
        source.EnableUsb();
        target.EnableUsb();
        Command(source, 0xE4, 0xA7);
        MachineSnapshot saved = MachineSnapshot.Capture(source);
        target.Pci.WriteConfiguration(0x10, 0x20, 4, dirtyBase | 1);
        saved.RestoreTo(target);
        Assert.Equal(0xC101u, target.Pci.ReadConfiguration(0x10, 0x20, 4));
        Command(target, 0xE8);
        Assert.Equal(0xA7, target.Io.ReadByte(0x22A));
        target.Io.WriteByte(0x388, 0x02);
        target.Io.WriteByte(0x389, 0xFF);
        target.Io.WriteByte(0x388, 0x04);
        target.Io.WriteByte(0x389, 1);
        target.SoundBlaster.Advance(100);
        Assert.Equal(0xC0, target.Io.ReadByte(0x388) & 0xC0);
    }

    [Fact]
    public void PciBar4ConflictingWithSavedDsp220IsRejectedEvenAfterPnpReclaimsDecode()
    {
        using var source = new Machine();
        using var target = new Machine();
        source.EnableUsb();
        target.EnableUsb();
        MachineSnapshot snapshot = MachineSnapshot.Capture(source);
        source.Pci.WriteConfiguration(0x10, 0x20, 4, 0x221);
        // Reprogramming PnP makes SB16 the current owner, but both saved decoders
        // would still request the same ports on restore.
        Enumerate(source);
        Select(source, "CTL0031");
        PnpWrite(source, 0x70, 5);
        Assert.Throws<InvalidDataException>(() => MachineSnapshot.Capture(source));
        // Exercise restore preflight independently with a structurally valid PCI block.
        var field = typeof(MachineSnapshot).GetField("_pci",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        byte[] pci = (byte[])field.GetValue(snapshot)!;
        byte[] conflict = source.Pci.Capture();
        Assert.Equal(pci.Length, conflict.Length);
        conflict.CopyTo(pci, 0);
        byte[] before = Save(MachineSnapshot.Capture(target));
        Assert.Throws<InvalidDataException>(() => snapshot.RestoreTo(target));
        Assert.Equal(before, Save(MachineSnapshot.Capture(target)));
    }

    [Fact]
    public void Pnp61SwapsAudioAndNetworkDecodeWithoutErasingRestoredPorts()
    {
        using var source = new Machine();
        using var target = new Machine();
        void Configure(Machine machine, byte audioBase, byte networkBase)
        {
            Enumerate(machine);
            Select(machine, "CTL0031");
            PnpWrite(machine, 0x30, 0);
            Select(machine, "RTL8019");
            PnpWrite(machine, 0x30, 0);
            PnpWrite(machine, 0x60, 2);
            PnpWrite(machine, 0x61, networkBase);
            PnpWrite(machine, 0x70, 9);
            PnpWrite(machine, 0x30, 1);
            Select(machine, "CTL0031");
            PnpWrite(machine, 0x60, 2);
            PnpWrite(machine, 0x61, audioBase);
            PnpWrite(machine, 0x30, 1);
        }
        Configure(source, 0x40, 0x80);
        Configure(target, 0x80, 0x40);
        Command(source, 0xE4, 0xA7);
        MachineSnapshot.Capture(source).RestoreTo(target);
        Assert.Equal((ushort)0x280, target.Network.BasePort);
        Assert.Equal(9, target.Network.IrqLine);
        Assert.Equal(source.Io.ReadByte(0x280), target.Io.ReadByte(0x280));
        Command(target, 0xE8);
        Assert.Equal(0xA7, target.Io.ReadByte(0x24A));
        Assert.Equal(source.IsaPlugAndPlay.Capture(), target.IsaPlugAndPlay.Capture());
    }

    [Fact]
    public void DisabledTargetFfffBaseHasNoDecodeToDetachDuringRestore()
    {
        using var source = new Machine();
        using var target = new Machine();
        MachineSnapshot snapshot = MachineSnapshot.Capture(source);
        target.SoundBlaster.Relocate(0xFFFF, 5, 1, 5, false);
        target.Network.Relocate(0xFFFF, 3, false);
        snapshot.RestoreTo(target);
        Assert.Equal((ushort)0x220, target.SoundBlaster.BasePort);
        Assert.Equal((ushort)0x300, target.Network.BasePort);
        Assert.Equal(0x7F, target.Io.ReadByte(0x22C));
        Assert.Equal(source.SoundBlaster.Capture(), target.SoundBlaster.Capture());
    }

    [Fact]
    public void InvalidActiveTargetFfffDecodeRejectsBeforeRamMutation()
    {
        using var source = new Machine();
        using var target = new Machine();
        MachineSnapshot snapshot = MachineSnapshot.Capture(source);
        target.Memory.WriteByte(0x1000, 0xA5);
        Assert.Throws<IndexOutOfRangeException>(() => target.SoundBlaster.Relocate(0xFFFF, 5, 1, 5, true));
        Assert.Throws<InvalidDataException>(() => snapshot.RestoreTo(target));
        Assert.Equal(0xA5, target.Memory.ReadByte(0x1000));
    }

    [Fact]
    public void Opl388ForeignDecodeRejectsRestoreBeforeReplacingItsOwner()
    {
        using var source = new Machine();
        using var target = new Machine();
        MachineSnapshot saved = MachineSnapshot.Capture(source);
        target.Memory.WriteByte(0x1000, 0xA5);
        var owner = new ForeignPort();
        target.Io.Register(0x388, owner);
        byte[] dsp = target.SoundBlaster.Capture();
        Assert.Throws<InvalidDataException>(() => saved.RestoreTo(target));
        Assert.Equal(0xA5, target.Memory.ReadByte(0x1000));
        Assert.Equal(dsp, target.SoundBlaster.Capture());
        Assert.Equal(0x5A, target.Io.ReadByte(0x388));
    }

    /// <summary>Represents an unrelated ISA decoder already occupying an OPL port.</summary>
    private sealed class ForeignPort : IIoDevice
    {
        public byte ReadByte(ushort port) => 0x5A;
        public void WriteByte(ushort port, byte value) { }
    }

    [Theory]
    [InlineData("dma-boolean")]
    [InlineData("dsp-mixer-resource")]
    [InlineData("dsp-pnp-resource")]
    [InlineData("pnp-fixed-port")]
    [InlineData("pnp-topology")]
    [InlineData("pnp-active-pending")]
    [InlineData("pnp-activation")]
    [InlineData("missing-audio")]
    [InlineData("duplicate")]
    [InlineData("flags")]
    [InlineData("unknown-kind")]
    [InlineData("length")]
    public void Dvs1InvalidDeviceStateCannotMutateRamPicDmaAudioOrPorts(string fault)
    {
        using var source = new Machine();
        using var target = new Machine();
        RelocateAudio(target);
        Play(target, false);
        target.SoundBlaster.Advance(130);
        target.Memory.WriteByte(0x1000, 0xA5);
        target.Cpu.State.Eip = 0x123456;
        byte[] before = Save(MachineSnapshot.Capture(target));
        byte[] data = Save(MachineSnapshot.Capture(source));
        int dma = SnapshotDeviceFile.RecordOffset(data, 2);
        int dsp = SnapshotDeviceFile.RecordOffset(data, 3);
        int pnp = SnapshotDeviceFile.RecordOffset(data, 4);
        int trailer = SnapshotDeviceFile.TrailerOffset(data);
        switch (fault)
        {
            case "dma-boolean":
                data[dma + 12 + 8 + 10] = 2;
                break;
            case "dsp-mixer-resource":
                data[dsp + 12 + 108 + 10] = 7;
                break;
            case "dsp-pnp-resource":
                data[dsp + 12 + 16] = 0x40;
                break;
            case "pnp-fixed-port":
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(pnp + 12 + 8), 0x3F7);
                break;
            case "pnp-topology":
                data[pnp + 12 + 21] ^= 1;
                break;
            case "pnp-active-pending":
            case "pnp-activation":
                int logical = data.AsSpan(pnp + 12).IndexOf("CTL0031"u8) + pnp + 12;
                int registers = logical + 7 + 1 + 7; // ID, compatible flag, compatible ID.
                data[registers + (fault == "pnp-activation" ? 0x30 : 0x61)] ^= 1;
                break;
            case "missing-audio":
                int length = 12 + BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(dma + 8));
                data = [.. data[..dma], .. data[(dma + length)..]];
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(trailer), data.Length - trailer - 4);
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(trailer + 10), 3);
                break;
            case "duplicate":
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(dsp), 2);
                break;
            case "flags":
                data[dma + 6] = 1;
                break;
            case "unknown-kind":
                data[dma] = 99;
                break;
            case "length":
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(dma + 8), int.MaxValue);
                break;
        }
        Assert.Throws<InvalidDataException>(() => Load(data).RestoreTo(target));
        Assert.Equal(before, Save(MachineSnapshot.Capture(target)));
        Assert.Equal(0x7F, target.Io.ReadByte(0x24C));
        Assert.Equal(0xFF, target.Io.ReadByte(0x22C));
    }
}
