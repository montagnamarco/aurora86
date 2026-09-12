namespace Aurora386.Core.Devices.Audio;

/// <summary>
/// The signed 16-bit interleaved stereo samples a frontend pulls from audio.
/// The count returned by ReadSamples is the number of shorts, not frames.
/// </summary>
public interface IAudioOutput
{
    int SampleRate { get; }

    int ReadSamples(Span<short> destination);
}
