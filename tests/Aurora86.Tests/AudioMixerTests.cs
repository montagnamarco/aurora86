using Aurora86.Core.Devices.Audio;
using Aurora86.Core.Devices.Interrupts;
using Aurora86.Core.Devices.System;
using Aurora86.Core.IO;
using Aurora86.Core.Memory;

namespace Aurora86.Tests;

/// <summary>
/// <see cref="AudioMixer"/>: resampling more than one source to one output
/// rate without drift, clipping the combined sum rather than any one
/// source, and the underrun/overrun policy decision D8 records. See the
/// integration test at the bottom for the Sound Blaster actually playing
/// through the mixer.
/// </summary>
public class AudioMixerTests
{
    /// <summary>
    /// A source whose content is entirely under the test's control: frames
    /// are queued by hand rather than rendered from any real hardware, the
    /// same way <c>SoundBlaster16.ReadSamples</c> drains its own ring.
    /// </summary>
    private sealed class FakeAudioSource : IAudioSource
    {
        private readonly Queue<short> _pending = new();

        public string Name { get; }
        public int NativeSampleRate { get; }

        public FakeAudioSource(string name, int nativeSampleRate)
        {
            Name = name;
            NativeSampleRate = nativeSampleRate;
        }

        /// <summary>Queues one stereo frame, the same value on both channels, repeated.</summary>
        public void ProduceConstant(int frames, short value)
        {
            for (int i = 0; i < frames; i++)
            {
                _pending.Enqueue(value);
                _pending.Enqueue(value);
            }
        }

        /// <summary>Queues frames with a distinct, increasing value per frame, for telling them apart later.</summary>
        public void ProduceSequence(int frames, int startValue)
        {
            for (int i = 0; i < frames; i++)
            {
                short value = (short)(startValue + i);
                _pending.Enqueue(value);
                _pending.Enqueue(value);
            }
        }

        public int ReadSamples(Span<short> destination)
        {
            int count = 0;
            while (count < destination.Length && _pending.Count > 0)
            {
                destination[count++] = _pending.Dequeue();
            }
            return count;
        }
    }

    [Fact]
    public void TwoSourcesAtDifferentRatesMixAt48000HertzWithoutDrift()
    {
        // 22050 and 44100 Hz are exactly the two rates plan F1.6 names. Both
        // divide 48000/rate into a whole number of frames per 100 ms chunk
        // (2205 and 4410), so producing and pulling in lockstep, the way a
        // real feed loop would, never needs a fractional frame on the
        // production side -- only the mixer's own resampling has to be exact.
        const int OutputRate = 48000;
        const int NativeRateA = 22050;
        const int NativeRateB = 44100;
        const int ChunkMilliseconds = 100;
        const int ChunkCount = 100; // 10 simulated seconds

        var mixer = new AudioMixer(OutputRate);
        var sourceA = new FakeAudioSource("A", NativeRateA);
        var sourceB = new FakeAudioSource("B", NativeRateB);
        mixer.AddSource(sourceA);
        mixer.AddSource(sourceB);

        int outputFramesPerChunk = OutputRate * ChunkMilliseconds / 1000;
        int nativeFramesPerChunkA = NativeRateA * ChunkMilliseconds / 1000;
        int nativeFramesPerChunkB = NativeRateB * ChunkMilliseconds / 1000;

        var destination = new short[outputFramesPerChunk * 2];
        int totalFramesRead = 0;
        short lastValueA = 0;
        short lastValueB = 0;

        for (int chunk = 0; chunk < ChunkCount; chunk++)
        {
            // A new constant per chunk, distinct from every other chunk's,
            // so the tail of the run can be checked against a known value
            // with no ambiguity from an earlier chunk's content lingering.
            lastValueA = (short)(1000 + chunk * 10);
            lastValueB = (short)(2000 + chunk * 5);
            sourceA.ProduceConstant(nativeFramesPerChunkA, lastValueA);
            sourceB.ProduceConstant(nativeFramesPerChunkB, lastValueB);

            totalFramesRead += mixer.ReadFrames(destination);
        }

        // The defining claim of D8's resampling design: over a long run, a
        // source at rate_in delivers exactly rate_out/rate_in frames per
        // input frame, with no accumulated rounding error. 480000 output
        // frames at 48000 Hz against 220500 input frames at 22050 Hz (10
        // exact seconds of each) is exactly the boundary case where a naive
        // floating-point accumulator's rounding would start to show.
        Assert.Equal(10 * OutputRate, totalFramesRead);
        Assert.Equal(10 * NativeRateA, mixer.ConsumedNativeFrames(sourceA));
        Assert.Equal(10 * NativeRateB, mixer.ConsumedNativeFrames(sourceB));

        // Both sources' content survives the resampling and the mix: the
        // last frame pulled reflects the last constant each source produced,
        // summed, not overwritten by whichever source's Pull ran second.
        short mixedLeft = destination[^2];
        short mixedRight = destination[^1];
        Assert.Equal((short)(lastValueA + lastValueB), mixedLeft);
        Assert.Equal((short)(lastValueA + lastValueB), mixedRight);
    }

    [Fact]
    public void CombinedSourcesClipAtTheInt16Limits()
    {
        // Two sources loud enough on their own that neither one alone would
        // clip, but whose sum overflows an int16 -- the case clipping exists
        // to describe honestly, checked at both rails.
        var mixer = new AudioMixer(48000);
        var loudPositive = new FakeAudioSource("loud+", 48000);
        var alsoLoudPositive = new FakeAudioSource("also-loud+", 48000);
        mixer.AddSource(loudPositive);
        mixer.AddSource(alsoLoudPositive);

        const int Frames = 32;
        loudPositive.ProduceConstant(Frames, 30000);
        alsoLoudPositive.ProduceConstant(Frames, 30000);

        var destination = new short[Frames * 2];
        mixer.ReadFrames(destination);

        Assert.All(destination, sample => Assert.Equal(short.MaxValue, sample));

        var negativeMixer = new AudioMixer(48000);
        var loudNegative = new FakeAudioSource("loud-", 48000);
        var alsoLoudNegative = new FakeAudioSource("also-loud-", 48000);
        negativeMixer.AddSource(loudNegative);
        negativeMixer.AddSource(alsoLoudNegative);

        loudNegative.ProduceConstant(Frames, -30000);
        alsoLoudNegative.ProduceConstant(Frames, -30000);

        var negativeDestination = new short[Frames * 2];
        negativeMixer.ReadFrames(negativeDestination);

        Assert.All(negativeDestination, sample => Assert.Equal(short.MinValue, sample));
    }

    [Fact]
    public void AnUnderrunningSourceContributesSilenceWithoutMutingTheOthers()
    {
        // A paused Sound Blaster or an idle synth must not take the rest of
        // the mix down with it: the other source's content survives exactly.
        var mixer = new AudioMixer(48000);
        var steady = new FakeAudioSource("steady", 48000);
        var silent = new FakeAudioSource("goes-quiet", 48000);
        mixer.AddSource(steady);
        mixer.AddSource(silent);

        const short SteadyValue = 5000;
        const int Frames = 64;

        // "silent" never produces a single frame: every ReadSamples call it
        // ever receives returns zero, exactly as an idle source does.
        steady.ProduceConstant(Frames, SteadyValue);

        var destination = new short[Frames * 2];
        mixer.ReadFrames(destination);

        Assert.All(destination, sample => Assert.Equal(SteadyValue, sample));
    }

    [Fact]
    public void OverrunDropsTheOldestFramesAndKeepsTheBufferBounded()
    {
        // A source producing far faster than anyone drains it must not grow
        // its buffer without bound: the ring is fixed size (about one second
        // of that source's own rate), and writing past the end silently
        // overwrites the oldest frame -- the entire overrun policy.
        const int NativeRate = 100; // small on purpose: a ring this size is easy to overrun in a unit test
        var mixer = new AudioMixer(NativeRate); // output rate equal to native: no resampling arithmetic to reason about
        var source = new FakeAudioSource("firehose", NativeRate);
        mixer.AddSource(source);

        // Five seconds' worth of distinctly numbered frames, produced before
        // the mixer ever pulls a single one: the ring (sized for about one
        // second, i.e. 100 frames) can hold only the newest 100 of these.
        const int TotalProduced = 500;
        source.ProduceSequence(TotalProduced, startValue: 0);

        var destination = new short[TotalProduced * 2];
        int written = mixer.ReadFrames(destination);
        Assert.Equal(TotalProduced, written);

        // Frames 0..399 were already gone by the time the mixer looked: the
        // ring had already wrapped past them, so they come back as silence
        // rather than stale data from a much older frame.
        for (int frame = 0; frame < TotalProduced - NativeRate; frame++)
        {
            Assert.Equal((short)0, destination[frame * 2]);
        }

        // Frames 400..499 are the newest 100 the ring could still hold, and
        // arrive with their original, distinct values intact.
        for (int frame = TotalProduced - NativeRate; frame < TotalProduced; frame++)
        {
            Assert.Equal((short)frame, destination[frame * 2]);
            Assert.Equal((short)frame, destination[frame * 2 + 1]);
        }
    }

    [Fact]
    public void SoundBlasterDmaPlaybackReachesTheHostThroughTheMixer()
    {
        // End to end: a real SoundBlaster16, driven the way a DOS driver
        // drives it (DMA-programmed 8-bit output), registered as a source,
        // still produces audible samples once pulled through the mixer --
        // not just through its own ReadSamples, which SoundBlasterTests
        // already covers on its own.
        const ushort Base = 0x220;
        const ushort WriteCommand = Base + 0x0C;

        var memory = new PhysicalMemoryBus(1 * 1024 * 1024);
        var dma = new Dma8237(memory);
        var interrupts = new InterruptController();
        var card = new SoundBlaster16(interrupts, dma);
        var io = new IoBus();
        card.Register(io);
        dma.Register(io);

        // A square wave at the DMA source address: 0x00 and 0xFF alternating
        // are the two extremes an unsigned 8-bit sample can hold, as far from
        // the silent midpoint (0x80) as this format gets.
        const int SampleCount = 64;
        for (int i = 0; i < SampleCount; i++)
        {
            memory.WriteByte((uint)(0x10000 + i), (byte)(i % 2 == 0 ? 0x00 : 0xFF));
        }

        io.WriteByte(0x0C, 0);          // clear the byte-pointer flip-flop
        io.WriteByte(0x02, 0x00);       // address low
        io.WriteByte(0x02, 0x00);       // address high
        io.WriteByte(0x83, 0x01);       // page: physical 0x10000
        io.WriteByte(0x03, (byte)((SampleCount - 1) & 0xFF));
        io.WriteByte(0x03, (byte)((SampleCount - 1) >> 8));
        io.WriteByte(0x0B, 0x49);       // mode: read from memory, single, channel 1
        io.WriteByte(0x0A, 0x01);       // unmask channel 1

        io.WriteByte(WriteCommand, 0x40);   // set time constant
        io.WriteByte(WriteCommand, 0xA6);   // -> roughly 11 kHz, plenty for 64 samples
        io.WriteByte(WriteCommand, 0x14);   // single-cycle 8-bit DMA
        io.WriteByte(WriteCommand, (byte)((SampleCount - 1) & 0xFF));
        io.WriteByte(WriteCommand, (byte)((SampleCount - 1) >> 8));

        // Long enough that every sample is certainly consumed and rendered
        // into the card's own ring, resampled to its output rate.
        card.Advance(20_000);

        // The mixer resamples the card's 44100 Hz output (the constructor's
        // default) to its own, different rate: proof that the two clocks
        // really are being reconciled, not just passed through unchanged.
        var mixer = new AudioMixer(48000);
        mixer.AddSource(card);

        var destination = new short[4096];
        bool everWroteNonSilence = false;

        // Several pulls, the way a host feed loop would: the card's ring may
        // not hand over everything in one call once resampled.
        for (int pull = 0; pull < 8 && !everWroteNonSilence; pull++)
        {
            mixer.ReadFrames(destination);
            everWroteNonSilence = Array.Exists(destination, sample => sample != 0);
        }

        Assert.True(everWroteNonSilence, "the mixer never produced an audible sample from the SB16's DMA playback");

        memory.Dispose();
    }
}
