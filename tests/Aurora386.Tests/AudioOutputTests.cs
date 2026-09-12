using Aurora386.Core;

namespace Aurora386.Tests;

/// <summary>
/// Tests the frontend-facing audio output contract over Sound Blaster PCM.
/// </summary>
public class AudioOutputTests
{
    [Fact]
    public void OutputUsesSoundBlasterSamples()
    {
        using var machine = new Machine();
        const ushort WriteCommand = 0x22C;

        machine.Io.WriteByte(WriteCommand, 0x41);
        machine.Io.WriteByte(WriteCommand, 0xAC);
        machine.Io.WriteByte(WriteCommand, 0x44);

        foreach (byte register in new byte[] { 0x30, 0x31, 0x32, 0x33 })
        {
            machine.Io.WriteByte(0x224, register);
            machine.Io.WriteByte(0x225, 0xF8);
        }

        for (int i = 0; i < 4; i++)
        {
            machine.Io.WriteByte(WriteCommand, 0x10);
            machine.Io.WriteByte(WriteCommand, (byte)(0x40 + i));
        }

        Span<short> samples = stackalloc short[8];
        int count = machine.Audio.ReadSamples(samples);

        Assert.Equal(8, count);
        Assert.Equal(
            new short[] { -16384, -16384, -16128, -16128, -15872, -15872, -15616, -15616 },
            samples.ToArray());

        Assert.Equal(0, machine.Audio.ReadSamples(samples));
    }

    [Fact]
    public void OutputRetainsSampleRate()
    {
        using var machine = new Machine();

        Assert.Equal(44100, machine.Audio.SampleRate);
    }

    [Fact]
    public void OutputUsesTheExistingSoundBlaster()
    {
        using var machine = new Machine();

        Assert.Same(machine.SoundBlaster, machine.Audio);
    }
}
