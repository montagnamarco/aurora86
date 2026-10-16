using System.Text;
using Aurora86.Core;

namespace Aurora86.Tests;

/// <summary>
/// A snapshot's cycle counters and device deadlines only replay at their captured
/// nominal clock. CCLK version zero stores that ratio; older files mean 25 MHz.
/// Rejection must happen before RAM, CPU or CD ownership can be changed.
/// </summary>
public sealed class MachineSnapshotClockTests
{
    [Theory]
    [InlineData(25_000_000)]
    [InlineData(100_000_000)]
    [InlineData(200_000_000)]
    public void MatchingClockSurvivesFileRoundTrip(int frequency)
    {
        using var source = new Machine(cpuFrequencyHz: frequency);
        source.Memory.WriteByte(0x1000, 0xA5);
        source.Cpu.State.Eip = 0x1234;
        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(source).Save(path);
            using var target = new Machine(cpuFrequencyHz: frequency);
            MachineSnapshot.Load(path).RestoreTo(target);
            Assert.Equal(frequency, target.CpuFrequencyHz);
            Assert.Equal(0xA5, target.Memory.ReadByte(0x1000));
            Assert.Equal(0x1234u, target.Cpu.State.Eip);
            Assert.Equal(source.CyclesFromMicroseconds(1000), target.CyclesFromMicroseconds(1000));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClockMismatchRejectsBeforeChangingTarget(bool fileRoundTrip)
    {
        using var source = new Machine(cpuFrequencyHz: 100_000_000);
        source.Memory.WriteByte(0x1000, 0xA5);
        MachineSnapshot snapshot = MachineSnapshot.Capture(source);
        string path = Path.GetTempFileName();
        try
        {
            if (fileRoundTrip)
            {
                snapshot.Save(path);
                snapshot = MachineSnapshot.Load(path);
            }
            using var target = new Machine();
            target.Memory.WriteByte(0x1000, 0x5A);
            target.Cpu.State.Eip = 0x5678;
            Assert.Throws<InvalidDataException>(() => snapshot.RestoreTo(target));
            Assert.Equal(25_000_000, target.CpuFrequencyHz);
            Assert.Equal(0x5A, target.Memory.ReadByte(0x1000));
            Assert.Equal(0x5678u, target.Cpu.State.Eip);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingClockSectionMeansHistorical25Mhz()
    {
        using var source = new Machine();
        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(source).Save(path);
            RewriteClockSection(path, null);
            MachineSnapshot snapshot = MachineSnapshot.Load(path);
            using var matching = new Machine();
            snapshot.RestoreTo(matching);
            using var faster = new Machine(cpuFrequencyHz: 100_000_000);
            Assert.Throws<InvalidDataException>(() => snapshot.RestoreTo(faster));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(999_999)]
    [InlineData(1_000_000_001)]
    public void CaptureRefusesAClockTheFileCouldNotHold(int frequency)
    {
        using var machine = new Machine();
        machine.CpuFrequencyHz = frequency;
        Assert.Throws<InvalidOperationException>(() => MachineSnapshot.Capture(machine));
    }

    [Theory]
    [InlineData(0, 3, 25_000_000)]
    [InlineData(1, 4, 25_000_000)]
    [InlineData(0, 4, 0)]
    [InlineData(0, 4, 1_000_000_001)]
    public void InvalidClockSectionIsRejectedOnLoad(ushort version, int length, int frequency)
    {
        using var source = new Machine();
        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(source).Save(path);
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(Encoding.ASCII.GetBytes("CCLK"));
            writer.Write(version);
            writer.Write(length);
            writer.Write(BitConverter.GetBytes(frequency), 0, length);
            writer.Flush();
            RewriteClockSection(path, stream.ToArray());
            Assert.Throws<InvalidDataException>(() => MachineSnapshot.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void RewriteClockSection(string path, byte[]? replacement)
    {
        byte[] file = File.ReadAllBytes(path);
        using var source = new BinaryReader(new MemoryStream(file));
        using var output = new MemoryStream();
        using var target = new BinaryWriter(output);
        target.Write(source.ReadBytes(8)); // AURASNAP container magic.
        while (source.BaseStream.Position < source.BaseStream.Length)
        {
            byte[] name = source.ReadBytes(4);
            ushort version = source.ReadUInt16();
            int length = source.ReadInt32();
            byte[] payload = source.ReadBytes(length);
            if (Encoding.ASCII.GetString(name) == "CCLK")
            {
                if (replacement != null)
                {
                    target.Write(replacement);
                }
                continue;
            }
            target.Write(name);
            target.Write(version);
            target.Write(length);
            target.Write(payload);
        }
        target.Flush();
        File.WriteAllBytes(path, output.ToArray());
    }
}
