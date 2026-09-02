using Aurora386.Core.Devices;
using Aurora386.Core.Devices.Audio;

namespace Aurora386.Tests;

/// <summary>
/// Tests for the Sound Blaster 16.
///
/// These exercise the card the way a DOS program would: reset it, ask what it
/// is, set a sample rate, start a transfer. Everything a game does before it
/// makes a sound happens through this handful of ports.
/// </summary>
public class SoundBlasterTests
{
    private const ushort Base = 0x220;

    // The DSP's four interesting ports, as offsets from the card's base.
    private const ushort ResetPort = Base + 0x06;
    private const ushort ReadDataPort = Base + 0x0A;
    private const ushort WriteCommandPort = Base + 0x0C;
    private const ushort ReadStatusPort = Base + 0x0E;

    private const ushort MixerIndexPort = Base + 0x04;
    private const ushort MixerDataPort = Base + 0x05;

    private static SoundBlaster16 Build() => new(new InterruptController(), null);

    /// <summary>
    /// Performs the reset handshake a driver uses to find the card: write 1,
    /// write 0, then read the data port. A card that is there answers 0xAA.
    /// </summary>
    private static byte ResetAndRead(SoundBlaster16 card)
    {
        card.WriteByte(ResetPort, 1);
        card.WriteByte(ResetPort, 0);
        return card.ReadByte(ReadDataPort);
    }

    [Fact]
    public void TheResetHandshakeAnswersWithAA()
    {
        // This is how every DOS program detects a Sound Blaster. No 0xAA, no
        // card as far as the software is concerned.
        SoundBlaster16 card = Build();
        Assert.Equal(0xAA, ResetAndRead(card));
    }

    [Fact]
    public void TheStatusPortSaysWhenThereIsSomethingToRead()
    {
        SoundBlaster16 card = Build();
        card.WriteByte(ResetPort, 1);
        card.WriteByte(ResetPort, 0);

        // Bit 7 set means "data available". A driver spins on this rather
        // than assuming the card answers instantly.
        Assert.NotEqual(0, card.ReadByte(ReadStatusPort) & 0x80);
    }

    [Fact]
    public void TheVersionCommandReportsFourPointFive()
    {
        SoundBlaster16 card = Build();
        ResetAndRead(card);

        card.WriteByte(WriteCommandPort, 0xE1);   // get DSP version

        // 4.05 is what a real SB16 reports, and programs branch on it: below
        // 4.00 they assume no 16-bit playback.
        Assert.Equal(4, card.ReadByte(ReadDataPort));
        Assert.Equal(5, card.ReadByte(ReadDataPort));
    }

    [Fact]
    public void TheIdentifyCommandReturnsTheComplementOfWhatItWasGiven()
    {
        SoundBlaster16 card = Build();
        ResetAndRead(card);

        // Command 0xE0 echoes back the one's complement of its argument. It
        // exists so a driver can prove the card is really answering rather
        // than reading a floating bus.
        card.WriteByte(WriteCommandPort, 0xE0);
        card.WriteByte(WriteCommandPort, 0x5A);

        Assert.Equal(unchecked((byte)~0x5A), card.ReadByte(ReadDataPort));
    }

    [Fact]
    public void TheMixerRemembersWhatWasWrittenToIt()
    {
        SoundBlaster16 card = Build();

        card.WriteByte(MixerIndexPort, 0x22);   // master volume
        card.WriteByte(MixerDataPort, 0xCC);

        card.WriteByte(MixerIndexPort, 0x22);
        Assert.Equal(0xCC, card.ReadByte(MixerDataPort));
    }

    [Fact]
    public void TheMixerReportsTheConfiguredInterruptAndDmaChannels()
    {
        SoundBlaster16 card = Build();

        // Registers 0x80 and 0x81 are how a driver discovers which IRQ and
        // DMA channel the card is jumpered for, instead of being told.
        card.WriteByte(MixerIndexPort, 0x80);
        byte irqField = card.ReadByte(MixerDataPort);

        card.WriteByte(MixerIndexPort, 0x81);
        byte dmaField = card.ReadByte(MixerDataPort);

        Assert.NotEqual(0, irqField);
        Assert.NotEqual(0, dmaField);
    }

    [Fact]
    public void ResettingTheMixerRestoresTheDefaultVolumes()
    {
        SoundBlaster16 card = Build();

        card.WriteByte(MixerIndexPort, 0x22);
        card.WriteByte(MixerDataPort, 0x00);    // silence

        card.WriteByte(MixerIndexPort, 0x00);   // register 0 resets the mixer
        card.WriteByte(MixerDataPort, 0x00);

        card.WriteByte(MixerIndexPort, 0x22);
        Assert.NotEqual(0x00, card.ReadByte(MixerDataPort));
    }

    [Fact]
    public void TheTimeConstantSetsTheSampleRate()
    {
        SoundBlaster16 card = Build();
        ResetAndRead(card);

        // The time constant is not a frequency: it is 256 - (1000000 / rate),
        // because the hardware counts down a timer rather than dividing. 165
        // works out to about 11 kHz, the rate half the games of the era used.
        card.WriteByte(WriteCommandPort, 0x40);
        card.WriteByte(WriteCommandPort, 165);

        Assert.InRange(card.PlaybackRate, 10_000, 12_000);
    }

    [Fact]
    public void TheDirectRateCommandSetsExactlyWhatItIsGiven()
    {
        SoundBlaster16 card = Build();
        ResetAndRead(card);

        // Command 0x41 takes the rate in Hz directly, high byte first. The
        // SB16 added it precisely because the time constant could not express
        // 44100 accurately.
        card.WriteByte(WriteCommandPort, 0x41);
        card.WriteByte(WriteCommandPort, 0xAC);   // 44100 >> 8
        card.WriteByte(WriteCommandPort, 0x44);   // 44100 & 0xFF

        Assert.Equal(44100, card.PlaybackRate);
    }

    [Fact]
    public void SpeakerCommandsAreAcceptedWithoutComplaint()
    {
        SoundBlaster16 card = Build();
        ResetAndRead(card);

        // On an original Sound Blaster these connected and disconnected the
        // output amplifier. On a 16 they do nothing, but every driver still
        // sends them, so the card must not choke.
        card.WriteByte(WriteCommandPort, 0xD1);   // speaker on
        card.WriteByte(WriteCommandPort, 0xD3);   // speaker off

        // The card must still answer afterwards: a driver sends these during
        // initialisation and then carries on talking to it.
        card.WriteByte(WriteCommandPort, 0xE1);
        Assert.Equal(4, card.ReadByte(ReadDataPort));
    }

    [Fact]
    public void PauseAndResumeToggleThePlayingState()
    {
        SoundBlaster16 card = Build();
        ResetAndRead(card);

        card.WriteByte(WriteCommandPort, 0xD0);   // pause 8-bit playback
        Assert.False(card.IsPlaying);

        card.WriteByte(WriteCommandPort, 0xD4);   // resume
        // Nothing was started, so resuming leaves it stopped rather than
        // inventing a transfer.
        Assert.False(card.IsPlaying);
    }

    [Fact]
    public void AnUnknownCommandDoesNotThrow()
    {
        SoundBlaster16 card = Build();
        ResetAndRead(card);

        // Real hardware ignores what it does not recognise. An emulator that
        // threw here would turn a harmless driver quirk into a crash.
        card.WriteByte(WriteCommandPort, 0x7F);
        card.WriteByte(WriteCommandPort, 0x00);
    }

    [Fact]
    public void WritingFmRegistersIsAccepted()
    {
        SoundBlaster16 card = Build();

        // The OPL3 is addressed through an index/data pair, like the mixer.
        // 0x20 is operator 1's tremolo/multiplier register.
        card.WriteByte(Base + 0x00, 0x20);
        card.WriteByte(Base + 0x01, 0x01);

        card.WriteByte(Base + 0x00, 0xA0);   // frequency low
        card.WriteByte(Base + 0x01, 0x98);
    }

    [Fact]
    public void TheFmStatusPortReportsTimersNotBusy()
    {
        SoundBlaster16 card = Build();

        // Bits 7, 6 and 5 are the timer flags. With no timers running they are
        // clear, which is how a driver knows the chip is idle.
        Assert.Equal(0, card.ReadByte(Base + 0x00) & 0xE0);
    }

    [Fact]
    public void ReadingSamplesFromASilentCardYieldsSilence()
    {
        SoundBlaster16 card = Build();
        card.Advance(10_000);   // ten milliseconds of emulated time

        Span<short> buffer = stackalloc short[256];
        int count = card.ReadSamples(buffer);

        // Whatever it hands back must be silence: nothing is playing.
        for (int i = 0; i < count; i++)
            Assert.Equal(0, buffer[i]);
    }

    [Fact]
    public void AdvancingTimeProducesSamplesToCollect()
    {
        SoundBlaster16 card = Build();
        card.Advance(100_000);   // a tenth of a second

        // The card renders continuously once time passes, so a host that asks
        // for audio has something to play even during silence -- otherwise the
        // output stream would gap.
        Assert.True(card.AvailableSamples > 0);
    }
}
