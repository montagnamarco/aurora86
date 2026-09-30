namespace Aurora86.Core.Devices.Audio;

/// <summary>
/// Something that renders stereo 16-bit audio at a rate of its own choosing:
/// the Sound Blaster's DSP output today, a PCI ES1371 codec or an MPU-401
/// synth later. <see cref="AudioMixer"/> is the only thing that talks to this
/// interface, and it is deliberately thin -- a source knows nothing about the
/// mixer's output rate, the host's audio API, or any other source sharing the
/// mix. That is what lets a second and third source be added later (F1.6's
/// plan explicitly names the ES1371 and the MPU-401 synth) without touching
/// the mixer or any source already plugged in.
/// </summary>
public interface IAudioSource
{
    /// <summary>A short name for diagnostics and snapshot/log messages, e.g. "SB16".</summary>
    string Name { get; }

    /// <summary>
    /// The rate, in hertz, at which this source hands frames to <see
    /// cref="ReadSamples"/>. Fixed for the source's lifetime: a card that
    /// changes its own internal rate (the SB16's DSP does, constantly, as
    /// software reprograms it) is expected to resample to one fixed rate
    /// itself before this point, exactly as <c>SoundBlaster16</c> already
    /// does into its own ring buffer.
    /// </summary>
    int NativeSampleRate { get; }

    /// <summary>
    /// Copies interleaved stereo frames (left, right, left, right...)
    /// rendered since the last call into <paramref name="destination"/>, and
    /// returns how many 16-bit samples were written -- always an even
    /// number, since a partial frame cannot be delivered. A source with
    /// nothing new to say writes nothing and returns zero: that is not an
    /// error, it is silence, and <see cref="AudioMixer"/> treats it exactly
    /// that way (see the mixer's own summary for the underrun policy, D8).
    /// </summary>
    int ReadSamples(Span<short> destination);
}
