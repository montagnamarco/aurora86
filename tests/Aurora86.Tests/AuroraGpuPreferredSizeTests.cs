using System.Text;
using Aurora86.Core;
using Aurora86.Core.Configuration;
using Aurora86.Core.Devices.Video;
using static Aurora86.Core.Devices.Video.AuroraGpuInterface;

namespace Aurora86.Tests;

/// <summary>
/// The version-1.2 host preferred-size registers as a guest observes them
/// through BAR1, including changed-pair publication and snapshot ownership.
/// </summary>
public class AuroraGpuPreferredSizeTests
{
    [Fact]
    public void VersionCapabilityAndReadOnlyRegistersAdvertiseTheHostPreference()
    {
        using Machine machine = Started();
        AuroraGpuCard card = machine.AuroraGpu!;

        Assert.Equal(0x00010002u, Read(machine, VersionRegister));
        Assert.NotEqual(0u, Read(machine, CapabilitiesRegister) & CapabilityHostPreferredSize);
        Assert.Equal(0u, Read(machine, HostPreferredWidthRegister));
        Assert.Equal(0u, Read(machine, HostPreferredHeightRegister));
        Assert.Equal(0u, Read(machine, HostPreferredGenerationRegister));

        card.SetHostPreferredSize(1280, 800);
        Assert.Equal(1280u, Read(machine, HostPreferredWidthRegister));
        Assert.Equal(800u, Read(machine, HostPreferredHeightRegister));
        Assert.Equal(1u, Read(machine, HostPreferredGenerationRegister));

        Write(machine, HostPreferredWidthRegister, 640);
        Write(machine, HostPreferredHeightRegister, 480);
        Write(machine, HostPreferredGenerationRegister, 99);
        Assert.Equal(1280u, Read(machine, HostPreferredWidthRegister));
        Assert.Equal(800u, Read(machine, HostPreferredHeightRegister));
        Assert.Equal(1u, Read(machine, HostPreferredGenerationRegister));
    }

    [Fact]
    public void OnlyChangedValidPairsAdvanceGenerationAndInvalidPairsAreAtomic()
    {
        using Machine machine = Started();
        AuroraGpuCard card = machine.AuroraGpu!;

        card.SetHostPreferredSize(1920, 1080);
        card.SetHostPreferredSize(1920, 1080);
        Assert.Equal(1u, Read(machine, HostPreferredGenerationRegister));

        foreach ((int width, int height) in new[]
        {
            (-1, 480), (0, 480), (640, 0), (1921, 1080), (1920, 1081),
        })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => card.SetHostPreferredSize(width, height));
            Assert.Equal(1920u, Read(machine, HostPreferredWidthRegister));
            Assert.Equal(1080u, Read(machine, HostPreferredHeightRegister));
            Assert.Equal(1u, Read(machine, HostPreferredGenerationRegister));
        }

        card.SetHostPreferredSize(0, 0);
        Assert.Equal(0u, Read(machine, HostPreferredWidthRegister));
        Assert.Equal(0u, Read(machine, HostPreferredHeightRegister));
        Assert.Equal(2u, Read(machine, HostPreferredGenerationRegister));
        card.SetHostPreferredSize(0, 0);
        Assert.Equal(2u, Read(machine, HostPreferredGenerationRegister));
    }

    [Fact]
    public void SnapshotRestoreGivesBackTheRecordedPreferenceAndResetKeepsIt()
    {
        using Machine machine = Started();
        AuroraGpuCard card = machine.AuroraGpu!;

        card.SetHostPreferredSize(640, 480);
        byte[] payload = card.Capture();
        Assert.Equal(AuroraGpuCard.SnapshotLength, payload.Length);

        card.SetHostPreferredSize(800, 600);
        card.Restore(payload);
        Assert.Equal(640u, Read(machine, HostPreferredWidthRegister));
        Assert.Equal(480u, Read(machine, HostPreferredHeightRegister));
        Assert.Equal(1u, Read(machine, HostPreferredGenerationRegister));
        Assert.Equal(payload, card.Capture());

        card.Reset();
        Assert.Equal(640u, Read(machine, HostPreferredWidthRegister));
        Assert.Equal(480u, Read(machine, HostPreferredHeightRegister));
        Assert.Equal(1u, Read(machine, HostPreferredGenerationRegister));
    }

    [Fact]
    public void AnotherFrontEndsFirstPublicationAfterRestoreAdvancesPastTheRecordedGeneration()
    {
        // The guest saw generation 1 at 1600x900. A new process whose window
        // is 1280x800 also publishes its first size as generation 1 -- before
        // or after the restore -- and the guest must still see a change.
        using Machine source = Started();
        source.AuroraGpu!.SetHostPreferredSize(1600, 900);
        byte[] payload = source.AuroraGpu.Capture();

        using Machine target = Started();
        AuroraGpuCard card = target.AuroraGpu!;
        card.SetHostPreferredSize(1280, 800);
        card.Restore(payload);
        Assert.Equal(1600u, Read(target, HostPreferredWidthRegister));
        Assert.Equal(1u, Read(target, HostPreferredGenerationRegister));

        card.SetHostPreferredSize(1280, 800);
        Assert.Equal(1280u, Read(target, HostPreferredWidthRegister));
        Assert.Equal(800u, Read(target, HostPreferredHeightRegister));
        Assert.Equal(2u, Read(target, HostPreferredGenerationRegister));
    }

    [Fact]
    public void VersionOnePayloadRestoresWithTheHostPreferenceClearedAtGenerationZero()
    {
        using Machine machine = Started();
        AuroraGpuCard card = machine.AuroraGpu!;
        byte[] legacy = card.Capture()[..AuroraGpuCard.LegacySnapshotLength];

        card.SetHostPreferredSize(800, 600);
        card.Restore(legacy, AuroraGpuCard.LegacySnapshotVersion);
        Assert.Equal(0u, Read(machine, HostPreferredWidthRegister));
        Assert.Equal(0u, Read(machine, HostPreferredHeightRegister));
        Assert.Equal(0u, Read(machine, HostPreferredGenerationRegister));

        Assert.Throws<InvalidDataException>(() => AuroraGpuCard.ValidateSnapshot(legacy));
        Assert.Throws<InvalidDataException>(() => AuroraGpuCard.ValidateSnapshot(legacy, 3));
    }

    [Theory]
    [InlineData(0u, 600u)]
    [InlineData((uint)(MaximumHostPreferredWidth + 1), 600u)]
    [InlineData(800u, (uint)(MaximumHostPreferredHeight + 1))]
    public void OutOfRangeRecordedPreferenceIsRejectedBeforeAnyStateChanges(uint width, uint height)
    {
        using Machine machine = Started();
        AuroraGpuCard card = machine.AuroraGpu!;
        card.SetHostPreferredSize(800, 600);
        byte[] payload = card.Capture();
        BitConverter.TryWriteBytes(payload.AsSpan(AuroraGpuCard.LegacySnapshotLength), width);
        BitConverter.TryWriteBytes(payload.AsSpan(AuroraGpuCard.LegacySnapshotLength + sizeof(uint)), height);

        card.SetHostPreferredSize(1024, 768);
        Assert.Throws<InvalidDataException>(() => card.Restore(payload));
        Assert.Equal(1024u, Read(machine, HostPreferredWidthRegister));
        Assert.Equal(2u, Read(machine, HostPreferredGenerationRegister));
    }

    [Fact]
    public async Task GenerationBracketNeverAcceptsAMixedPairAcrossHostUpdates()
    {
        using Machine machine = Started();
        AuroraGpuCard card = machine.AuroraGpu!;
        card.SetHostPreferredSize(640, 480);

        Task publisher = Task.Run(() =>
        {
            for (int index = 0; index < 5_000; index++)
            {
                card.SetHostPreferredSize(800, 600);
                card.SetHostPreferredSize(640, 480);
            }
        });

        for (int index = 0; index < 5_000; index++)
        {
            uint before = Read(machine, HostPreferredGenerationRegister);
            uint width = Read(machine, HostPreferredWidthRegister);
            uint height = Read(machine, HostPreferredHeightRegister);
            uint after = Read(machine, HostPreferredGenerationRegister);
            if (before == after)
            {
                Assert.True((width, height) is (640u, 480u) or (800u, 600u));
            }
        }

        await publisher;
        Assert.Equal(640u, Read(machine, HostPreferredWidthRegister));
        Assert.Equal(480u, Read(machine, HostPreferredHeightRegister));
        Assert.Equal(10_001u, Read(machine, HostPreferredGenerationRegister));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MachineSnapshotFileCarriesThePreferenceAndStillReadsVersionOneSections(bool versionOne)
    {
        using Machine source = Started();
        source.AuroraGpu!.SetHostPreferredSize(1600, 900);
        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(source).Save(path);
            if (versionOne)
            {
                DowngradeAuroraGpuSection(path);
            }

            using Machine target = Started();
            target.AuroraGpu!.SetHostPreferredSize(1280, 800);
            MachineSnapshot.Load(path).RestoreTo(target);
            Assert.Equal(versionOne ? 0u : 1600u, Read(target, HostPreferredWidthRegister));
            Assert.Equal(versionOne ? 0u : 900u, Read(target, HostPreferredHeightRegister));
            Assert.Equal(versionOne ? 0u : 1u, Read(target, HostPreferredGenerationRegister));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Rewrites the file's "AGPU" section as a version-1 one: the same payload without the appended preference.</summary>
    private static void DowngradeAuroraGpuSection(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        using var source = new BinaryReader(new MemoryStream(file));
        using var output = new MemoryStream();
        using var target = new BinaryWriter(output);
        target.Write(source.ReadBytes(8)); // AURASNAP container magic.
        bool found = false;
        while (source.BaseStream.Position < source.BaseStream.Length)
        {
            byte[] name = source.ReadBytes(4);
            ushort version = source.ReadUInt16();
            int length = source.ReadInt32();
            byte[] payload = source.ReadBytes(length);
            if (Encoding.ASCII.GetString(name) == "AGPU")
            {
                Assert.Equal(AuroraGpuCard.SnapshotVersion, version);
                version = AuroraGpuCard.LegacySnapshotVersion;
                payload = payload[..AuroraGpuCard.LegacySnapshotLength];
                length = payload.Length;
                found = true;
            }
            target.Write(name);
            target.Write(version);
            target.Write(length);
            target.Write(payload);
        }
        Assert.True(found);
        target.Flush();
        File.WriteAllBytes(path, output.ToArray());
    }

    private static Machine Started()
    {
        var machine = new Machine(videoModel: VmVideoModel.AuroraGpu);
        machine.RunInstructions(200);
        return machine;
    }

    private static uint Read(Machine machine, uint register)
    {
        return machine.Memory.ReadDword(machine.AuroraGpu!.RegisterBase + register);
    }

    private static void Write(Machine machine, uint register, uint value)
    {
        machine.Memory.WriteDword(machine.AuroraGpu!.RegisterBase + register, value);
    }
}
