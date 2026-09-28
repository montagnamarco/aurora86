using System.Buffers.Binary;
using Aurora386.Core;
using Aurora386.Core.Configuration;
using Aurora386.Core.Devices.IsaPnp;

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
        Assert.Equal(samples.Length, machine.SoundBlaster!.ReadSamples(samples));
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

    /// <summary>
    /// A file from before the last unsectioned builds' "DVS1" trailer
    /// existed at all, one from a build that had it but wrote it empty, and
    /// one from a build that only ever put the Trio accelerator's state in
    /// it: none of the three ever named the DMA controller, the DSP or the
    /// ISA PnP bus, and all three must reset those three to their power-on
    /// state rather than fabricate audio records that were never captured.
    /// </summary>
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

        string path = Path.GetTempFileName();
        string resavedPath = Path.GetTempFileName();
        try
        {
            LegacySnapshotFile.Write(source, path, LegacySnapshotEnd.AfterVideoTopology);
            if (legacyKind != 0)
            {
                byte[] trailer = LegacySnapshotFile.DeviceStateTrailer(source);
                int trioLength = source.Accelerator.Capture().Length;
                int length = legacyKind == 1 ? 12 : 24 + trioLength;
                byte[] customTrailer = trailer[..length];
                BinaryPrimitives.WriteInt32LittleEndian(customTrailer.AsSpan(0), length - 4);
                BinaryPrimitives.WriteUInt16LittleEndian(customTrailer.AsSpan(10), (ushort)(legacyKind - 1));

                using var file = new BinaryWriter(File.Open(path, FileMode.Append));
                file.Write(customTrailer);
            }

            // The DMA controller and the ISA PnP bus reset to their power-on
            // state, as every file older than their own sections does; the
            // Sound Blaster card's own resource assignment is not part of
            // that container-level fallback (only its DSP/mixer/FM state
            // does, through the card's own Reset()), so it is not asserted
            // here.
            MachineSnapshot loaded = MachineSnapshot.Load(path);
            loaded.RestoreTo(target);
            Assert.Equal(source.Dma.Capture(), target.Dma.Capture());
            Assert.Equal(source.IsaPlugAndPlay.Capture(), target.IsaPlugAndPlay.Capture());
            Assert.Equal(source.Interrupts.Capture(), target.Interrupts.Capture());
            // The card's own resource assignment survives at whatever the
            // target had before the restore (0x240, from RelocateAudio):
            // nothing claims the compiled-in default 0x220 range any more,
            // while the OPL3 at the card's real address still answers.
            Assert.Equal(0xFF, target.Io.ReadByte(0x22C));
            Assert.Equal(0x7F, target.Io.ReadByte(0x24C));

            // Re-saving in the current, sectioned format and restoring that
            // file into a fresh machine reaches the same state again.
            loaded.Save(resavedPath);
            using var viaResave = new Machine();
            MachineSnapshot.Load(resavedPath).RestoreTo(viaResave);
            Assert.Equal(source.Dma.Capture(), viaResave.Dma.Capture());
            Assert.Equal(source.IsaPlugAndPlay.Capture(), viaResave.IsaPlugAndPlay.Capture());
        }
        finally
        {
            File.Delete(path);
            File.Delete(resavedPath);
        }
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

    /// <summary>
    /// Corrupts one section of a saved snapshot in a way that is real for
    /// this machine -- a boolean the DMA controller's own format refuses, a
    /// DSP resource field that no longer agrees with the mixer or the
    /// applied ISA PnP configuration, a PnP card identity or activation
    /// state that cannot have arisen from any sequence of driver writes, or
    /// an audio card's section missing outright -- and checks that loading
    /// or restoring the file leaves RAM, the PIC, the DMA controller and the
    /// audio ports exactly as they were.
    /// </summary>
    [Theory]
    [InlineData("dma-boolean")]
    [InlineData("dsp-mixer-resource")]
    [InlineData("dsp-pnp-resource")]
    [InlineData("pnp-topology")]
    [InlineData("pnp-active-pending")]
    [InlineData("pnp-activation")]
    [InlineData("missing-audio")]
    public void InvalidDeviceSectionCannotMutateRamPicDmaAudioOrPorts(string fault)
    {
        using var source = new Machine();
        using var target = new Machine();
        RelocateAudio(target);
        Play(target, false);
        target.SoundBlaster.Advance(130);
        target.Memory.WriteByte(0x1000, 0xA5);
        target.Cpu.State.Eip = 0x123456;
        byte[] before = Save(MachineSnapshot.Capture(target));

        MachineSnapshot snapshot = MachineSnapshot.Capture(source);
        switch (fault)
        {
            case "dma-boolean":
                // Byte 18 of the "DMA " section is channel 0's masked flag.
                SnapshotSectionFile.GetPayload(snapshot, "DMA ")[18] = 2;
                break;
            case "dsp-mixer-resource":
                // Byte 118 of the "SB16" section sits inside the nested mixer block.
                SnapshotSectionFile.GetPayload(snapshot, "SB16")[118] = 7;
                break;
            case "dsp-pnp-resource":
                // Byte 16 is the low byte of the DSP's own recorded base port.
                SnapshotSectionFile.GetPayload(snapshot, "SB16")[16] = 0x40;
                break;
            case "pnp-topology":
                SnapshotSectionFile.GetPayload(snapshot, "PNP ")[21] ^= 1;
                break;
            case "pnp-active-pending":
            case "pnp-activation":
            {
                byte[] pnp = SnapshotSectionFile.GetPayload(snapshot, "PNP ");
                int logical = pnp.AsSpan().IndexOf("CTL0031"u8);
                int registers = logical + 7 + 1 + 7; // ID, compatible flag, compatible ID.
                pnp[registers + (fault == "pnp-activation" ? 0x30 : 0x61)] ^= 1;
                break;
            }
        }

        string path = Path.GetTempFileName();
        try
        {
            snapshot.Save(path);
            if (fault == "missing-audio")
            {
                // No "SB16" section at all: the machine has a sound card fitted, but
                // this file was never taken with one, unlike a file older than the
                // card sections themselves (which restores every card at its own
                // power-on state instead of being refused).
                File.WriteAllBytes(path, SnapshotSectionFile.WithoutSection(File.ReadAllBytes(path), "SB16"));
            }

            Assert.Throws<InvalidDataException>(() => MachineSnapshot.Load(path).RestoreTo(target));
            Assert.Equal(before, Save(MachineSnapshot.Capture(target)));
            Assert.Equal(0x7F, target.Io.ReadByte(0x24C));
            Assert.Equal(0xFF, target.Io.ReadByte(0x22C));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
