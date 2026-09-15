using System.Buffers.Binary;
using Aurora386.Core;
using Aurora386.Core.Configuration;

namespace Aurora386.Tests;

/// <summary>
/// Verifies the DVS1 extension boundary for the PC's device continuation state,
/// preserving older positional snapshots without claiming unsaved hardware.
/// </summary>
public sealed class MachineDeviceSnapshotTests
{
    [Theory]
    [InlineData(VmVideoModel.S3Trio64VPlus, false)]
    [InlineData(VmVideoModel.S3Trio64VPlus, true)]
    [InlineData(VmVideoModel.S3Virge325, false)]
    [InlineData(VmVideoModel.S3Virge325, true)]
    public void Dvs1EmptyTrailerPresenceSurvivesRoundTrip(VmVideoModel model, bool present)
    {
        using var source = new Machine(videoModel: model);
        source.Memory.WriteByte(0x1000, 0xA5);
        source.Cpu.State.Eip = 0x1234;
        source.VideoMemory.WriteLinear(0, 0xCC);
        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(source).Save(path);
            byte[] original = File.ReadAllBytes(path);
            original = SnapshotDeviceFile.WithoutTrailer(original);
            byte[] expected = present ? [.. original, .. EmptyTrailer()] : original;
            File.WriteAllBytes(path, expected);
            MachineSnapshot loaded = MachineSnapshot.Load(path);
            loaded.Save(path);
            Assert.Equal(expected, File.ReadAllBytes(path));

            using var target = new Machine(videoModel: model);
            loaded.RestoreTo(target);
            Assert.Equal(0xA5, target.Memory.ReadByte(0x1000));
            Assert.Equal(0x1234u, target.Cpu.State.Eip);
            Assert.Equal(0xCC, target.VideoMemory.ReadLinear(0));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("negative-length")]
    [InlineData("oversized-length")]
    [InlineData("short-header")]
    [InlineData("magic")]
    [InlineData("version")]
    [InlineData("unsupported-record")]
    [InlineData("duplicate-record")]
    [InlineData("truncated-length")]
    [InlineData("truncated-header")]
    [InlineData("trailing-byte")]
    [InlineData("empty-with-payload")]
    public void Dvs1MalformedOrUnsupportedTrailerCannotReachRestore(string fault)
    {
        using var source = new Machine();
        using var target = new Machine();
        target.Memory.WriteByte(0x1000, 0x5A);
        target.Cpu.State.Eip = 0x5678;
        target.VideoMemory.WriteLinear(0, 0xDD);
        byte[] pic = target.Interrupts.Capture();
        bool cdOwnership = target.GuestHasItsOwnCompactDiscExtensions;
        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(source).Save(path);
            byte[] prefix = SnapshotDeviceFile.WithoutTrailer(File.ReadAllBytes(path));
            byte[] trailer = EmptyTrailer();
            switch (fault)
            {
                case "negative-length":
                    BinaryPrimitives.WriteInt32LittleEndian(trailer, -1);
                    break;
                case "oversized-length":
                    BinaryPrimitives.WriteInt32LittleEndian(trailer, int.MaxValue);
                    break;
                case "short-header":
                    BinaryPrimitives.WriteInt32LittleEndian(trailer, 7);
                    break;
                case "magic": trailer[4] ^= 0xFF; break;
                case "version": trailer[8] = 2; break;
                case "unsupported-record":
                case "duplicate-record":
                    int count = fault == "duplicate-record" ? 2 : 1;
                    Array.Resize(ref trailer, 12 + count * 12);
                    BinaryPrimitives.WriteInt32LittleEndian(trailer, trailer.Length - 4);
                    BinaryPrimitives.WriteUInt16LittleEndian(trailer.AsSpan(10), (ushort)count);
                    for (int i = 0; i < count; i++)
                    {
                        // Kind 1 (Trio), model 0, version 1, flags/length 0.
                        trailer[12 + i * 12] = 1;
                        trailer[16 + i * 12] = 1;
                    }
                    break;
                case "truncated-length": Array.Resize(ref trailer, 3); break;
                case "truncated-header": Array.Resize(ref trailer, 11); break;
                case "trailing-byte": Array.Resize(ref trailer, 13); break;
                case "empty-with-payload":
                    Array.Resize(ref trailer, 13);
                    BinaryPrimitives.WriteInt32LittleEndian(trailer, 9);
                    break;
            }

            File.WriteAllBytes(path, [.. prefix, .. trailer]);
            Assert.Throws<InvalidDataException>(() => MachineSnapshot.Load(path).RestoreTo(target));
            Assert.Equal(0x5A, target.Memory.ReadByte(0x1000));
            Assert.Equal(0x5678u, target.Cpu.State.Eip);
            Assert.Equal(0xDD, target.VideoMemory.ReadLinear(0));
            Assert.Equal(pic, target.Interrupts.Capture());
            Assert.Equal(cdOwnership, target.GuestHasItsOwnCompactDiscExtensions);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] EmptyTrailer()
    {
        // Length 8, DVS1 magic, version 1, zero records.
        return [8, 0, 0, 0, 0x44, 0x56, 0x53, 0x31, 1, 0, 0, 0];
    }
}
