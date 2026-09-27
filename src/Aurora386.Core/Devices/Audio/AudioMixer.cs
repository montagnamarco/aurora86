namespace Aurora386.Core.Devices.Audio;

/// <summary>
/// Combines any number of <see cref="IAudioSource"/>s -- each rendering
/// stereo 16-bit audio at whatever rate it likes -- into one interleaved
/// stereo stream at <see cref="OutputSampleRate"/> hertz, which a host front
/// end pulls from by calling <see cref="ReadFrames"/>. This is the single
/// place that knows the Sound Blaster renders at 44100 Hz, a later ES1371
/// codec might render at 48000 or 44100, and an MPU-401 synth at yet another
/// rate: nothing downstream of the mixer ever juggles more than one clock.
///
/// Design (decision D8, plan F1.6):
///
/// <list type="bullet">
/// <item>
/// <b>Resampling.</b> Each source gets its own linear-interpolating
/// resampler with an exact rational phase accumulator (the same technique a
/// Bresenham line uses): the fractional position is tracked as an integer
/// numerator in <c>[0, OutputSampleRate)</c>, advanced by the source's native
/// rate every output frame and reduced modulo the output rate. Unlike a
/// running floating-point sum, this can never accumulate rounding error, so
/// the number of input frames consumed for N output frames is always exactly
/// <c>floor(N * nativeRate / OutputSampleRate)</c> -- no drift over an
/// arbitrarily long run.
/// </item>
/// <item>
/// <b>Underrun.</b> A source with nothing buffered for the frame currently
/// needed contributes silence for that source only; the rest of the mix, and
/// the phase accumulator's position, are unaffected. A paused Sound Blaster
/// or an idle synth must never mute everything else in the mix.
/// </item>
/// <item>
/// <b>Overrun.</b> Each source's own resampling buffer is bounded (about one
/// second of that source's native frames). A source produced faster than the
/// mixer is drained -- nobody pulling <see cref="ReadFrames"/> -- drops its
/// oldest frames rather than growing without bound: latency is capped, not
/// memory.
/// </item>
/// <item>
/// <b>Clipping.</b> Every source's contribution to a frame is summed before
/// clipping happens once, at the very end; clipping each source on its own
/// and then adding would silently distort a mix that is only loud in
/// combination, which is exactly the case clipping exists to represent
/// honestly.
/// </item>
/// </list>
///
/// Nothing here is snapshotted: the mixer holds only host-side presentation
/// state (per-source ring buffers and phase positions), the audio equivalent
/// of a video frame already drawn. A restore rebuilds the mixer from
/// scratch and each source re-registers itself, so at most a few tens of
/// milliseconds of already-rendered audio are discarded -- nothing a guest
/// can observe through any register.
/// </summary>
public sealed class AudioMixer
{
    /// <summary>
    /// The output rate a modern host audio API is happiest with, and the
    /// rate every front end in this repository asks for.
    /// </summary>
    public const int DefaultOutputSampleRate = 48000;

    /// <summary>
    /// How long a source's own resampling buffer holds, in native frames,
    /// before the overrun policy starts dropping the oldest ones. One
    /// second is generous headroom for any host feed loop that services
    /// audio at video frame rate or faster.
    /// </summary>
    private const double RingSeconds = 1.0;

    /// <summary>Frames pulled from a source at once while draining it. See <see cref="SourceState.Pull"/>.</summary>
    private const int DrainChunkFrames = 1024;

    private readonly List<SourceState> _sources = [];

    /// <summary>The rate <see cref="ReadFrames"/> delivers frames at.</summary>
    public int OutputSampleRate { get; }

    public AudioMixer(int outputSampleRate = DefaultOutputSampleRate)
    {
        if (outputSampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputSampleRate), "The mixer's output rate must be a positive number of hertz.");
        }

        OutputSampleRate = outputSampleRate;
    }

    /// <summary>Every source currently feeding the mix, in the order they were added.</summary>
    public IReadOnlyList<IAudioSource> Sources => _sources.ConvertAll(state => state.Source);

    /// <summary>
    /// Plugs a source into the mix. A Sound Blaster card calls this from its
    /// own <c>Attach</c>, the same moment it claims its ports; later sources
    /// (an ES1371, an MPU-401 synth) do the same.
    /// </summary>
    public void AddSource(IAudioSource source)
    {
        _sources.Add(new SourceState(source, OutputSampleRate, RingSeconds));
    }

    /// <summary>
    /// Unplugs a source, for a card that can be removed or replaced at run
    /// time. Returns whether it was found.
    /// </summary>
    public bool RemoveSource(IAudioSource source)
        => _sources.RemoveAll(state => ReferenceEquals(state.Source, source)) > 0;

    /// <summary>
    /// How many native frames of <paramref name="source"/> the phase
    /// accumulator has consumed so far. Exposed only for
    /// <c>AudioMixerTests</c> to check the no-drift claim exactly, against
    /// the closed-form <c>floor(outputFrames * nativeRate / outputRate)</c>
    /// rather than against audible content, which cannot distinguish "exact"
    /// from "close enough that nobody would notice".
    /// </summary>
    internal long ConsumedNativeFrames(IAudioSource source)
        => _sources.First(state => ReferenceEquals(state.Source, source)).PositionIndex;

    /// <summary>
    /// Fills <paramref name="destination"/> with interleaved stereo frames at
    /// <see cref="OutputSampleRate"/>, resampling and summing every source
    /// with clipping. Unlike a single source's own buffer, the mix always
    /// fills the whole span: a host asking for audio is never told "come
    /// back later", because there is always a well-defined answer -- silence
    /// where nothing is playing. Returns the number of stereo frames written,
    /// which is always <c>destination.Length / 2</c>.
    /// </summary>
    public int ReadFrames(Span<short> destination)
    {
        if (destination.Length % 2 != 0)
        {
            throw new ArgumentException(
                "destination must hold whole stereo frames: an even number of 16-bit samples.",
                nameof(destination));
        }

        int frameCount = destination.Length / 2;

        // Every source accumulates into the same wider buffer before the one
        // clipping pass at the end -- see the type summary for why clipping
        // happens once, not per source.
        int[] accumulator = new int[destination.Length];

        foreach (SourceState state in _sources)
        {
            state.Pull(DrainChunkFrames);
            state.AccumulateInto(accumulator, frameCount);
        }

        for (int i = 0; i < accumulator.Length; i++)
        {
            destination[i] = (short)Math.Clamp(accumulator[i], short.MinValue, short.MaxValue);
        }

        return frameCount;
    }

    /// <summary>
    /// One source's own resampling state: the bounded ring of native-rate
    /// frames it has produced, and the exact rational phase accumulator that
    /// walks through them at the mixer's output rate. See the enclosing
    /// type's summary for the resampling, underrun and overrun policy this
    /// implements.
    /// </summary>
    private sealed class SourceState
    {
        private readonly int _outputSampleRate;

        /// <summary>
        /// Interleaved stereo ring, sized in shorts (two per frame). Writing
        /// past the end wraps and silently overwrites the oldest frame,
        /// which is the entire overrun policy: there is no separate
        /// "dropped" bookkeeping because the wrap already discards it.
        /// </summary>
        private readonly short[] _ring;

        private readonly int _ringCapacityFrames;

        /// <summary>Total native frames ever written into the ring, monotonic.</summary>
        private long _framesWritten;

        /// <summary>
        /// Index, in the same monotonic coordinate space as <see
        /// cref="_framesWritten"/>, of the "left" sample used to interpolate
        /// the next output frame.
        /// </summary>
        private long _positionIndex;

        /// <summary>
        /// The fractional part of the read position, as a numerator over
        /// <see cref="_outputSampleRate"/>. Always kept in <c>[0,
        /// _outputSampleRate)</c> by <see cref="AccumulateInto"/>, which is
        /// what makes the whole scheme exact: nothing here is ever a
        /// floating-point sum that could drift.
        /// </summary>
        private long _positionFrac;

        public IAudioSource Source { get; }

        /// <summary>See <see cref="AudioMixer.ConsumedNativeFrames"/>.</summary>
        public long PositionIndex => _positionIndex;

        public SourceState(IAudioSource source, int outputSampleRate, double ringSeconds)
        {
            Source = source;
            _outputSampleRate = outputSampleRate;

            // At least a handful of milliseconds even for an implausibly low
            // native rate: the ring must hold at least two frames for the
            // interpolation window to ever have both its endpoints.
            _ringCapacityFrames = Math.Max(16, (int)(source.NativeSampleRate * ringSeconds));
            _ring = new short[_ringCapacityFrames * 2];
        }

        /// <summary>
        /// Drains whatever the source has rendered since the last call, in
        /// chunks of up to <paramref name="chunkFrames"/>, until it reports
        /// nothing left.
        /// </summary>
        public void Pull(int chunkFrames)
        {
            Span<short> scratch = stackalloc short[chunkFrames * 2];

            while (true)
            {
                int written = Source.ReadSamples(scratch);
                if (written <= 0) break;

                AppendToRing(scratch[..written]);
                if (written < scratch.Length) break;
            }
        }

        private void AppendToRing(ReadOnlySpan<short> samples)
        {
            for (int i = 0; i < samples.Length; i += 2)
            {
                int slot = (int)(_framesWritten % _ringCapacityFrames) * 2;
                _ring[slot] = samples[i];
                _ring[slot + 1] = samples[i + 1];
                _framesWritten++;
            }
        }

        /// <summary>
        /// Adds this source's contribution to <paramref name="accumulator"/>,
        /// one interpolated frame per output frame, advancing the phase
        /// accumulator exactly once per frame regardless of whether real
        /// data was available -- see the enclosing type's summary for why an
        /// underrun must not stall the position permanently.
        /// </summary>
        public void AccumulateInto(int[] accumulator, int frameCount)
        {
            int nativeRate = Source.NativeSampleRate;

            for (int frame = 0; frame < frameCount; frame++)
            {
                if (TryReadFrame(_positionIndex, out short leftA, out short rightA))
                {
                    if (TryReadFrame(_positionIndex + 1, out short leftB, out short rightB))
                    {
                        double t = _positionFrac / (double)_outputSampleRate;
                        accumulator[frame * 2] += (int)Math.Round(leftA + (leftB - leftA) * t);
                        accumulator[frame * 2 + 1] += (int)Math.Round(rightA + (rightB - rightA) * t);
                    }
                    else
                    {
                        // The far endpoint has not arrived yet: use the near
                        // sample unchanged rather than interpolating towards
                        // silence, which would fade the last real sample out
                        // instead of simply holding it for one frame.
                        accumulator[frame * 2] += leftA;
                        accumulator[frame * 2 + 1] += rightA;
                    }
                }

                // Silence otherwise: nothing is added, which is exactly the
                // underrun policy the type summary describes.

                _positionFrac += nativeRate;
                while (_positionFrac >= _outputSampleRate)
                {
                    _positionFrac -= _outputSampleRate;
                    _positionIndex++;
                }
            }
        }

        /// <summary>
        /// Reads one native frame at the given monotonic index, if it is
        /// still in the ring -- neither in the future (not produced yet) nor
        /// so far in the past that the ring has already overwritten it.
        /// </summary>
        private bool TryReadFrame(long index, out short left, out short right)
        {
            long oldestAvailable = Math.Max(0, _framesWritten - _ringCapacityFrames);

            if (index < oldestAvailable || index >= _framesWritten)
            {
                left = 0;
                right = 0;
                return false;
            }

            int slot = (int)(index % _ringCapacityFrames) * 2;
            left = _ring[slot];
            right = _ring[slot + 1];
            return true;
        }
    }
}
