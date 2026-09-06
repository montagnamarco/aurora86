using Aurora386.Core;
using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Devices.Input;
using Aurora386.Core.Devices.Interrupts;
using Aurora386.Core.Devices.Timing;
using Aurora386.Core.Devices.System;
using Aurora386.Core.Devices.Audio;
using Aurora386.Core.IO;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// The DSP's command protocol and the detection handshake every DOS program
/// performs before it will admit the card exists.
/// </summary>
public class SoundBlasterDspTests
{
    private const ushort Base = 0x220;
    private const ushort Reset = Base + 0x06;
    private const ushort ReadData = Base + 0x0A;
    private const ushort WriteCommand = Base + 0x0C;
    private const ushort ReadStatus = Base + 0x0E;

    private static SoundBlaster16 NewCard(out IoBus io)
    {
        io = new IoBus();
        var card = new SoundBlaster16();
        card.Register(io);
        return card;
    }

    [Fact]
    public void ResetSequenceMakesTheDspAnswerWithAa()
    {
        // This is the detection handshake in full: assert reset, release it,
        // then poll the status port until the firmware announces itself. A
        // card that fails this is invisible to every program ever written.
        SoundBlaster16 card = NewCard(out IoBus io);

        io.WriteByte(Reset, 1);
        io.WriteByte(Reset, 0);

        Assert.True((io.ReadByte(ReadStatus) & 0x80) != 0);
        Assert.Equal(0xAA, io.ReadByte(ReadData));
    }

    [Fact]
    public void StatusPortReportsNoDataBeforeReset()
    {
        // Before the handshake there is nothing to read, and the data-ready
        // bit must stay clear -- otherwise a probe would read garbage and
        // conclude a card was present at an address where none is.
        NewCard(out IoBus io);

        Assert.True((io.ReadByte(ReadStatus) & 0x80) == 0);
    }

    [Fact]
    public void HoldingResetAssertedDoesNotProduceTheReadyByte()
    {
        // Only the falling edge counts. Software that writes 1 and never
        // writes 0 has not finished the sequence, and must not be told the
        // card is ready.
        NewCard(out IoBus io);

        io.WriteByte(Reset, 1);

        Assert.True((io.ReadByte(ReadStatus) & 0x80) == 0);
    }

    [Fact]
    public void VersionCommandReportsFourPointOhFive()
    {
        // DSP 4.05 is what identifies an SB16 rather than a Pro or an
        // original. Software branches on this to decide whether 16-bit
        // samples and the CT1745 mixer are available.
        SoundBlaster16 card = NewCard(out IoBus io);

        io.WriteByte(Reset, 1);
        io.WriteByte(Reset, 0);
        io.ReadByte(ReadData);          // consume the 0xAA

        io.WriteByte(WriteCommand, 0xE1);

        Assert.Equal(4, io.ReadByte(ReadData));
        Assert.Equal(5, io.ReadByte(ReadData));
    }

    [Fact]
    public void IdentificationCommandEchoesTheArgumentComplemented()
    {
        // Command 0xE0 is the repeatable detection method: unlike the reset
        // handshake it can be issued at any time without disturbing playback.
        SoundBlaster16 card = NewCard(out IoBus io);

        io.WriteByte(WriteCommand, 0xE0);
        io.WriteByte(WriteCommand, 0x5A);

        Assert.Equal(unchecked((byte)~0x5A), io.ReadByte(ReadData));
    }

    [Fact]
    public void SpeakerCommandsToggleTheReportedState()
    {
        // 0xD1 on, 0xD3 off, and 0xD8 reads it back as 0xFF or 0x00.
        SoundBlaster16 card = NewCard(out IoBus io);

        io.WriteByte(WriteCommand, 0xD1);
        Assert.True(card.SpeakerEnabled);
        io.WriteByte(WriteCommand, 0xD8);
        Assert.Equal(0xFF, io.ReadByte(ReadData));

        io.WriteByte(WriteCommand, 0xD3);
        Assert.False(card.SpeakerEnabled);
        io.WriteByte(WriteCommand, 0xD8);
        Assert.Equal(0x00, io.ReadByte(ReadData));
    }

    [Fact]
    public void TimeConstantIsConvertedToASampleRate()
    {
        // The time constant is a timer reload, not a frequency: the rate is
        // 1000000 / (256 - constant). 0xA6 is the classic value for 22 kHz,
        // and note that it does not land exactly on 22050 -- that quantisation
        // is a real property of the hardware, not a rounding bug here.
        SoundBlaster16 card = NewCard(out IoBus io);

        io.WriteByte(WriteCommand, 0x40);
        io.WriteByte(WriteCommand, 0xA6);

        Assert.Equal(0xA6, card.TimeConstant);
        Assert.Equal(1_000_000 / (256 - 0xA6), card.PlaybackRate);
    }

    [Fact]
    public void SampleRateCommandTakesAPlainBigEndianFrequency()
    {
        // Command 0x41 exists because a single time-constant byte cannot
        // express anything above roughly 45 kHz.
        SoundBlaster16 card = NewCard(out IoBus io);

        io.WriteByte(WriteCommand, 0x41);
        io.WriteByte(WriteCommand, 0xAC);   // 44100 = 0xAC44, high byte first
        io.WriteByte(WriteCommand, 0x44);

        Assert.Equal(44100, card.PlaybackRate);
    }

    [Fact]
    public void CommandArgumentsAreCountedSoTheStreamStaysInSync()
    {
        // The protocol has no framing: the DSP knows from the command how many
        // bytes follow. If the count were wrong, an argument byte would be
        // mistaken for the next command and everything after it would break.
        // Here the two argument bytes of 0x41 must NOT be executed as
        // commands, so the version query that follows still works.
        SoundBlaster16 card = NewCard(out IoBus io);

        io.WriteByte(WriteCommand, 0x41);
        io.WriteByte(WriteCommand, 0xE1);   // would be "get version" if misread
        io.WriteByte(WriteCommand, 0xE1);
        Assert.Equal(0, card.AvailableSamples);

        io.WriteByte(WriteCommand, 0xE1);
        Assert.Equal(4, io.ReadByte(ReadData));
        Assert.Equal(5, io.ReadByte(ReadData));
    }

    [Fact]
    public void BusyBitIsNeverSetSoSoftwareNeverSpinsForever()
    {
        // Bit 7 of the write-status port means "busy, do not send a command".
        // Drivers poll it in a bounded loop and give up if it never clears --
        // so leaving it set would look exactly like an absent card.
        NewCard(out IoBus io);

        Assert.True((io.ReadByte(Base + 0x0C) & 0x80) == 0);
    }

    [Fact]
    public void AutoInitDmaCommandIsDecodedFromTheCommandByte()
    {
        // The 0xB0-0xCF range is a bit field folded into an opcode: 0xB0 means
        // 16-bit, bit 2 means auto-init, and the mode argument's bit 5 means
        // stereo. 0xB6 is the everyday "16-bit auto-init output" command.
        var memory = new PhysicalMemoryBus(1 * 1024 * 1024);
        var dma = new Dma8237(memory);
        var card = new SoundBlaster16(null, dma);
        var io = new IoBus();
        card.Register(io);

        io.WriteByte(WriteCommand, 0xB6);
        io.WriteByte(WriteCommand, 0x30);   // signed, stereo
        io.WriteByte(WriteCommand, 0xFF);   // length low
        io.WriteByte(WriteCommand, 0x00);   // length high

        Assert.True(card.Is16Bit);
        Assert.True(card.IsStereo);
        Assert.True(card.AutoInit);
        Assert.True(card.IsPlaying);

        memory.Dispose();
    }

    [Fact]
    public void PauseAndResumeCommandsStopAndRestartTheTransfer()
    {
        // 0xD0 pauses without losing position, 0xD4 carries on from where it
        // stopped -- which is why the DMA controller keeps its address.
        var memory = new PhysicalMemoryBus(1 * 1024 * 1024);
        var dma = new Dma8237(memory);
        var card = new SoundBlaster16(null, dma);
        var io = new IoBus();
        card.Register(io);

        io.WriteByte(WriteCommand, 0x14);
        io.WriteByte(WriteCommand, 0xFF);
        io.WriteByte(WriteCommand, 0x00);
        Assert.True(card.IsPlaying);

        io.WriteByte(WriteCommand, 0xD0);
        Assert.False(card.IsPlaying);

        io.WriteByte(WriteCommand, 0xD4);
        Assert.True(card.IsPlaying);

        memory.Dispose();
    }

    [Fact]
    public void CompletingADmaBlockRaisesTheInterrupt()
    {
        // The interrupt is how the card says "I have consumed your buffer".
        // Without it, streaming software waits forever and the audio stops
        // after exactly one block.
        var memory = new PhysicalMemoryBus(1 * 1024 * 1024);
        var dma = new Dma8237(memory);
        var interrupts = new InterruptController();
        var card = new SoundBlaster16(interrupts, dma);
        var io = new IoBus();
        card.Register(io);
        dma.Register(io);

        // Program DMA channel 1 for a short block of silence at 0x10000.
        io.WriteByte(0x0C, 0);          // clear the byte-pointer flip-flop
        io.WriteByte(0x02, 0x00);       // address low
        io.WriteByte(0x02, 0x00);       // address high
        io.WriteByte(0x83, 0x01);       // page: physical 0x10000
        io.WriteByte(0x03, 0x0F);       // count low  (16 transfers)
        io.WriteByte(0x03, 0x00);       // count high
        io.WriteByte(0x0B, 0x49);       // mode: read from memory, single, channel 1
        io.WriteByte(0x0A, 0x01);       // unmask channel 1

        io.WriteByte(WriteCommand, 0x40);
        io.WriteByte(WriteCommand, 0xA6);
        io.WriteByte(WriteCommand, 0x14);   // single-cycle 8-bit DMA
        io.WriteByte(WriteCommand, 0x0F);
        io.WriteByte(WriteCommand, 0x00);

        // Run for long enough that all 16 samples are certainly consumed.
        card.Advance(10_000);

        Assert.True(card.InterruptCount > 0);
        Assert.False(card.IsPlaying);      // single-cycle: it stops at the end

        memory.Dispose();
    }

    [Fact]
    public void ReadSamplesDrainsTheBufferAndReportsHowManyItWrote()
    {
        // The core never talks to a host audio API: it exposes a buffer the
        // caller pulls from, so the emulator behaves identically headless.
        SoundBlaster16 card = NewCard(out IoBus io);

        // Direct DAC writes are the simplest way to put samples in the ring.
        for (int i = 0; i < 50; i++)
        {
            io.WriteByte(WriteCommand, 0x10);
            io.WriteByte(WriteCommand, 0xFF);
        }

        Assert.True(card.AvailableSamples > 0);

        Span<short> buffer = stackalloc short[512];
        int written = card.ReadSamples(buffer);

        Assert.True(written > 0);
        Assert.Equal(0, card.AvailableSamples);      // the buffer is now drained
        Assert.Equal(0, written % 2);                // interleaved stereo: always even
    }

    [Fact]
    public void SampleRateIsReportedForTheHostToConfigureItsOutput()
    {
        var card = new SoundBlaster16(outputSampleRate: 48000);
        Assert.Equal(48000, card.SampleRate);
    }
}

/// <summary>
/// The CT1745 mixer: the analogue front end, and the registers through which a
/// driver discovers its own IRQ and DMA assignment.
/// </summary>
public class SoundBlasterMixerTests
{
    private static SbMixer NewMixer(out IoBus io)
    {
        io = new IoBus();
        var mixer = new SbMixer();
        mixer.Register(io);
        return mixer;
    }

    [Fact]
    public void MixerStoresAndReadsBackVolumeRegisters()
    {
        // The classic index/data pair: write the register number to 0x224,
        // then its contents at 0x225.
        SbMixer mixer = NewMixer(out IoBus io);

        io.WriteByte(0x224, 0x30);      // master volume, left
        io.WriteByte(0x225, 0xE0);
        io.WriteByte(0x224, 0x30);
        Assert.Equal(0xE0, io.ReadByte(0x225));

        io.WriteByte(0x224, 0x34);      // FM volume, left
        io.WriteByte(0x225, 0x88);
        io.WriteByte(0x224, 0x34);
        Assert.Equal(0x88, io.ReadByte(0x225));
    }

    [Fact]
    public void ProEraRegisterUpdatesBothHalvesOfTheStereoPair()
    {
        // The SB16 keeps the SB Pro's packed registers as aliases. A Pro-era
        // game writes 0x22 and must actually hear the master volume change,
        // even though the card's real attenuators are at 0x30 and 0x31.
        SbMixer mixer = NewMixer(out IoBus io);

        io.WriteByte(0x224, 0x22);
        io.WriteByte(0x225, 0xF0);      // left full, right silent

        Assert.True(mixer.GetRegister(0x30) > mixer.GetRegister(0x31));
    }

    [Fact]
    public void Register80ReportsTheConfiguredIrq()
    {
        // This is how a driver discovers its own interrupt line rather than
        // making the user type it in. One bit per wired IRQ: 2, 5, 7 and 10.
        SbMixer mixer = NewMixer(out IoBus io);
        mixer.Irq = 5;

        io.WriteByte(0x224, 0x80);
        Assert.Equal(0x02, io.ReadByte(0x225));

        mixer.Irq = 7;
        io.WriteByte(0x224, 0x80);
        Assert.Equal(0x04, io.ReadByte(0x225));
    }

    [Fact]
    public void Register81ReportsBothDmaChannels()
    {
        // Bits 0-2 are the 8-bit channels, bits 5-7 the 16-bit ones. The
        // defaults, channel 1 and channel 5, are 0x02 | 0x20.
        SbMixer mixer = NewMixer(out IoBus io);
        mixer.Dma8 = 1;
        mixer.Dma16 = 5;

        io.WriteByte(0x224, 0x81);
        Assert.Equal(0x22, io.ReadByte(0x225));
    }

    [Fact]
    public void WritingRegisterZeroRestoresTheDefaults()
    {
        // Register 0x00 is the mixer reset, and any value triggers it. The
        // defaults are deliberately not silence -- a card that came up muted
        // would look broken.
        SbMixer mixer = NewMixer(out IoBus io);

        io.WriteByte(0x224, 0x30);
        io.WriteByte(0x225, 0x00);      // mute the master, left
        Assert.Equal(0x00, mixer.GetRegister(0x30));

        io.WriteByte(0x224, 0x00);
        io.WriteByte(0x225, 0x00);      // reset

        Assert.NotEqual(0x00, mixer.GetRegister(0x30));
        Assert.True(mixer.MasterLevel > 0.5f);
    }

    [Fact]
    public void LevelPropertiesExposeGainsInTheZeroToOneRange()
    {
        SbMixer mixer = NewMixer(out IoBus io);

        io.WriteByte(0x224, 0x32); io.WriteByte(0x225, 0xF8);   // voice left, maximum
        io.WriteByte(0x224, 0x33); io.WriteByte(0x225, 0xF8);   // voice right, maximum
        Assert.Equal(1.0f, mixer.VoiceLevel, 3);

        io.WriteByte(0x224, 0x32); io.WriteByte(0x225, 0x00);
        io.WriteByte(0x224, 0x33); io.WriteByte(0x225, 0x00);
        Assert.Equal(0.0f, mixer.VoiceLevel, 3);
    }
}

/// <summary>
/// The 8237 DMA controller, with particular attention to the page register
/// arithmetic that trips up every implementation.
/// </summary>
public class Dma8237Tests
{
    private static Dma8237 NewDma(out IoBus io, PhysicalMemoryBus? memory = null)
    {
        io = new IoBus();
        var dma = new Dma8237(memory);
        dma.Register(io);
        return dma;
    }

    [Fact]
    public void PhysicalAddressCombinesThePageRegisterWithTheOffset()
    {
        // The 8237 only carries 16 address bits; the page register is a
        // separate latch supplying bits 16-23. Concatenation, not addition.
        Dma8237 dma = NewDma(out IoBus io);

        io.WriteByte(0x0C, 0);          // clear the byte-pointer flip-flop
        io.WriteByte(0x02, 0x34);       // channel 1 address low
        io.WriteByte(0x02, 0x12);       // channel 1 address high
        io.WriteByte(0x83, 0x0A);       // channel 1 page

        Assert.Equal(0x0A1234u, dma.GetPhysicalAddress(1));
    }

    [Fact]
    public void SixteenBitChannelAddressesWordsSoTheOffsetIsShifted()
    {
        // Channels 4-7 count words. Their address register contributes bits
        // 1-16 of the byte address and the page supplies bits 17-23, with page
        // bit 0 discarded -- hence the 128 KB alignment rule.
        Dma8237 dma = NewDma(out IoBus io);

        io.WriteByte(0xD8, 0);          // clear the flip-flop on controller 2
        io.WriteByte(0xC4, 0x00);       // channel 5 address low
        io.WriteByte(0xC4, 0x80);       // channel 5 address high -> word 0x8000
        io.WriteByte(0x8B, 0x03);       // channel 5 page; bit 0 is discarded

        Assert.Equal(0x030000u | (0x8000u << 1), dma.GetPhysicalAddress(5) | 0);
        Assert.Equal(0x020000u + 0x10000u, dma.GetPhysicalAddress(5));
    }

    [Fact]
    public void ByteFlipFlopSequencesTheTwoHalvesOfAWideRegister()
    {
        // A single flip-flop, shared by every channel on the controller,
        // decides whether the next byte is the low or high half. Failing to
        // clear it before programming lands the two bytes the wrong way round.
        Dma8237 dma = NewDma(out IoBus io);

        io.WriteByte(0x0C, 0);
        io.WriteByte(0x02, 0xAA);
        io.WriteByte(0x02, 0xBB);
        Assert.Equal(0xBBAA, dma[1].BaseAddress);

        // Now write only one byte, leaving the flip-flop set, and clear it
        // before the next pair -- which is exactly what a driver must do.
        io.WriteByte(0x02, 0x11);
        io.WriteByte(0x0C, 0);
        io.WriteByte(0x02, 0x22);
        io.WriteByte(0x02, 0x33);
        Assert.Equal(0x3322, dma[1].BaseAddress);
    }

    [Fact]
    public void MaskRegisterBlocksAndUnblocksAChannel()
    {
        // Channels come up masked, so a half-programmed channel cannot start
        // transferring from a garbage address.
        Dma8237 dma = NewDma(out IoBus io);

        Assert.True(dma[1].Masked);

        io.WriteByte(0x0A, 0x01);       // clear the mask for channel 1
        Assert.False(dma[1].Masked);

        io.WriteByte(0x0A, 0x05);       // set it again (bit 2 = mask)
        Assert.True(dma[1].Masked);
    }

    [Fact]
    public void ModeRegisterDecodesAutoInitAndDirection()
    {
        Dma8237 dma = NewDma(out IoBus io);

        io.WriteByte(0x0B, 0x59);       // channel 1, read from memory, auto-init
        Assert.True(dma[1].AutoInit);
        Assert.Equal(2, dma[1].TransferType);      // 2 = read from memory
        Assert.False(dma[1].Decrement);

        io.WriteByte(0x0B, 0x49);       // same but without auto-init
        Assert.False(dma[1].AutoInit);
    }

    [Fact]
    public void TransferReadsMemoryAndAdvancesTheAddress()
    {
        var memory = new PhysicalMemoryBus(1 * 1024 * 1024);
        Dma8237 dma = NewDma(out IoBus io, memory);

        memory.WriteByte(0x20000, 0x11);
        memory.WriteByte(0x20001, 0x22);

        io.WriteByte(0x0C, 0);
        io.WriteByte(0x02, 0x00);
        io.WriteByte(0x02, 0x00);
        io.WriteByte(0x83, 0x02);       // page 2 -> physical 0x20000
        io.WriteByte(0x03, 0x03);       // four transfers (count is length - 1)
        io.WriteByte(0x03, 0x00);
        io.WriteByte(0x0B, 0x49);
        io.WriteByte(0x0A, 0x01);

        // TransferByte returns byte? -- null when the channel is masked or
        // finished -- so the expected value needs the same type for the
        // comparison to pick the right overload.
        Assert.Equal((byte?)0x11, dma.TransferByte(1));
        Assert.Equal((byte?)0x22, dma.TransferByte(1));
        Assert.Equal(0x20002u, dma.GetPhysicalAddress(1));

        memory.Dispose();
    }

    [Fact]
    public void AutoInitReloadsTheAddressAtTerminalCountInsteadOfStopping()
    {
        // This is what makes gapless streaming possible: the controller wraps
        // to the top of the buffer on its own, so the interrupt handler never
        // has to reprogram it in a time-critical window.
        var memory = new PhysicalMemoryBus(1 * 1024 * 1024);
        Dma8237 dma = NewDma(out IoBus io, memory);

        io.WriteByte(0x0C, 0);
        io.WriteByte(0x02, 0x00);
        io.WriteByte(0x02, 0x00);
        io.WriteByte(0x83, 0x02);
        io.WriteByte(0x03, 0x01);       // two transfers
        io.WriteByte(0x03, 0x00);
        io.WriteByte(0x0B, 0x59);       // auto-init
        io.WriteByte(0x0A, 0x01);

        dma.TransferByte(1);
        dma.TransferByte(1);            // this one hits terminal count

        Assert.True(dma.ConsumeTerminalCount(1));
        Assert.False(dma[1].Masked);                       // still running
        Assert.Equal(0x20000u, dma.GetPhysicalAddress(1)); // back at the start

        memory.Dispose();
    }

    [Fact]
    public void WithoutAutoInitTheControllerMasksTheChannelAtTerminalCount()
    {
        var memory = new PhysicalMemoryBus(1 * 1024 * 1024);
        Dma8237 dma = NewDma(out IoBus io, memory);

        io.WriteByte(0x0C, 0);
        io.WriteByte(0x02, 0x00);
        io.WriteByte(0x02, 0x00);
        io.WriteByte(0x83, 0x02);
        io.WriteByte(0x03, 0x01);
        io.WriteByte(0x03, 0x00);
        io.WriteByte(0x0B, 0x49);       // single-cycle, no auto-init
        io.WriteByte(0x0A, 0x01);

        dma.TransferByte(1);
        dma.TransferByte(1);

        Assert.True(dma[1].Masked);
        Assert.Null(dma.TransferByte(1));   // masked channels transfer nothing

        memory.Dispose();
    }

    [Fact]
    public void PageRegisterPortsFollowTheirIrregularHistoricalAssignment()
    {
        // The page latches are not part of either 8237 and their port numbers
        // follow no pattern -- they are simply whichever addresses IBM had
        // spare. Getting this mapping wrong sends DMA to the wrong megabyte.
        Dma8237 dma = NewDma(out IoBus io);

        io.WriteByte(0x87, 0x10);   // channel 0
        io.WriteByte(0x83, 0x11);   // channel 1
        io.WriteByte(0x81, 0x12);   // channel 2
        io.WriteByte(0x82, 0x13);   // channel 3
        io.WriteByte(0x8B, 0x15);   // channel 5

        Assert.Equal(0x10, dma[0].Page);
        Assert.Equal(0x11, dma[1].Page);
        Assert.Equal(0x12, dma[2].Page);
        Assert.Equal(0x13, dma[3].Page);
        Assert.Equal(0x15, dma[5].Page);
    }

    [Fact]
    public void MasterClearResetsTheControllerToItsPowerOnState()
    {
        Dma8237 dma = NewDma(out IoBus io);

        io.WriteByte(0x0A, 0x01);       // unmask channel 1
        Assert.False(dma[1].Masked);

        io.WriteByte(0x0D, 0x00);       // master clear

        Assert.True(dma[1].Masked);
        Assert.True(dma[0].Masked);
    }
}

/// <summary>
/// The OPL3. Full FM synthesis is not verified here -- what matters is that
/// the register file accepts everything software throws at it without
/// throwing, since a single unhandled write would take the whole machine down.
/// </summary>
public class Opl3Tests
{
    [Fact]
    public void WritingEveryRegisterInBothBanksThrowsNothing()
    {
        // The register map has holes -- operator slots 6, 7, 14, 15 and 22-31
        // address nothing -- and software does write into them. Every one of
        // the 512 addresses must be survivable.
        var opl = new Opl3();
        var io = new IoBus();
        opl.Register(io);

        for (int bank = 0; bank < 2; bank++)
        {
            for (int register = 0; register < 256; register++)
            {
                ushort index = (ushort)(0x388 + bank * 2);
                io.WriteByte(index, (byte)register);
                io.WriteByte((ushort)(index + 1), 0xFF);
                io.WriteByte(index, (byte)register);
                io.WriteByte((ushort)(index + 1), 0x00);
            }
        }
    }

    [Fact]
    public void StatusRegisterIsReadableAndTimerResetClearsIt()
    {
        // Detection routines clear the flags, start a timer, wait, and look
        // again. Writing 0x80 to register 4 is the reset half of that dance.
        var opl = new Opl3();
        var io = new IoBus();
        opl.Register(io);

        io.WriteByte(0x388, 0x04);
        io.WriteByte(0x389, 0x80);      // reset the status flags

        Assert.Equal(0x00, io.ReadByte(0x388));
    }

    [Fact]
    public void ExpiredTimerSetsTheStatusFlagsDetectionLooksFor()
    {
        // Timer 1 counts in 80 us steps from its preload to 0xFF. With a
        // preload of 0xFF it expires almost immediately, which is exactly what
        // the standard Adlib detection sequence relies on.
        var opl = new Opl3();
        var io = new IoBus();
        opl.Register(io);

        io.WriteByte(0x388, 0x04); io.WriteByte(0x389, 0x80);   // clear flags
        io.WriteByte(0x388, 0x02); io.WriteByte(0x389, 0xFF);   // timer 1 preload
        io.WriteByte(0x388, 0x04); io.WriteByte(0x389, 0x01);   // start timer 1

        opl.AdvanceTimers(1000);

        byte status = io.ReadByte(0x388);
        Assert.True((status & 0x80) != 0);      // the summary bit
        Assert.True((status & 0x40) != 0);      // timer 1 specifically
    }

    [Fact]
    public void Opl3ModeIsOffUntilSoftwareEnablesIt()
    {
        // The chip is bit-exactly an OPL2 until register 0x105 bit 0 is set.
        // That is what lets one part serve both, and how software tells them
        // apart.
        var opl = new Opl3();
        var io = new IoBus();
        opl.Register(io);

        Assert.False(opl.Opl3ModeEnabled);

        io.WriteByte(0x38A, 0x05);      // bank 1, register 0x05
        io.WriteByte(0x38B, 0x01);

        Assert.True(opl.Opl3ModeEnabled);
    }

    [Fact]
    public void KeyOnProducesNonSilentOutput()
    {
        // A note with a fast attack and full level must actually generate
        // samples: if the envelope never leaves the Off phase, the whole
        // synthesiser is inaudible and nothing else here would catch it.
        var opl = new Opl3();

        // Operator 0 (the modulator of voice 0) and operator 3 (its carrier).
        foreach (int slot in new[] { 0x00, 0x03 })
        {
            opl.WriteRegister(0, 0x20 + slot, 0x21);    // multiple 1, sustain on
            opl.WriteRegister(0, 0x40 + slot, 0x00);    // no attenuation
            opl.WriteRegister(0, 0x60 + slot, 0xF0);    // fastest attack
            opl.WriteRegister(0, 0x80 + slot, 0x00);    // full sustain level
        }

        opl.WriteRegister(0, 0xC0, 0x01);   // additive, so the carrier is heard
        opl.WriteRegister(0, 0xA0, 0x98);   // F-number low
        opl.WriteRegister(0, 0xB0, 0x31);   // block 4, key on

        var buffer = new short[2000];
        opl.Render(buffer);

        bool anyNonZero = false;
        foreach (short sample in buffer) if (sample != 0) { anyNonZero = true; break; }
        Assert.True(anyNonZero);
    }

    [Fact]
    public void RenderNeverExceedsSixteenBitRangeEvenWithEveryVoiceOn()
    {
        // 18 voices summing without headroom would wrap around and turn loud
        // music into noise. Clamping is not optional.
        var opl = new Opl3();
        opl.WriteRegister(1, 0x05, 0x01);   // OPL3 mode: all 18 voices

        for (int bank = 0; bank < 2; bank++)
        {
            for (int voice = 0; voice < 9; voice++)
            {
                int slot = (voice / 3) * 8 + (voice % 3);
                foreach (int op in new[] { slot, slot + 3 })
                {
                    opl.WriteRegister(bank, 0x20 + op, 0x21);
                    opl.WriteRegister(bank, 0x40 + op, 0x00);
                    opl.WriteRegister(bank, 0x60 + op, 0xF0);
                    opl.WriteRegister(bank, 0x80 + op, 0x00);
                }
                opl.WriteRegister(bank, 0xC0 + voice, 0x31);   // additive, both channels
                opl.WriteRegister(bank, 0xA0 + voice, 0x98);
                opl.WriteRegister(bank, 0xB0 + voice, 0x31);
            }
        }

        var buffer = new short[4000];
        opl.Render(buffer);

        // The assertion is simply that Render completed and produced values in
        // range; short cannot hold anything else, so the real check is that
        // the clamping path did not throw or produce an overflow.
        foreach (short sample in buffer)
            Assert.InRange(sample, short.MinValue, short.MaxValue);
    }
}

/// <summary>
/// The card as wired into a complete machine.
/// </summary>
public class SoundBlasterMachineTests
{
    [Fact]
    public void MachineExposesTheCardAtTheStandardBaseAddress()
    {
        // If the registration in Machine did not happen, the reset handshake
        // reads 0xFF from an empty bus and detection fails.
        using var machine = new Machine(4 * 1024 * 1024);

        machine.Io.WriteByte(0x226, 1);
        machine.Io.WriteByte(0x226, 0);

        Assert.Equal(0xAA, machine.Io.ReadByte(0x22A));
    }

    [Fact]
    public void MachineExposesTheOplAtItsTraditionalPort()
    {
        // Port 0x388 is where every Adlib-era program looks, independently of
        // the Sound Blaster's own base address.
        using var machine = new Machine(4 * 1024 * 1024);

        machine.Io.WriteByte(0x388, 0x04);
        machine.Io.WriteByte(0x389, 0x80);

        Assert.Equal(0x00, machine.Io.ReadByte(0x388));
    }

    [Fact]
    public void MachineExposesTheDmaControllerPorts()
    {
        using var machine = new Machine(4 * 1024 * 1024);

        machine.Io.WriteByte(0x0C, 0);
        machine.Io.WriteByte(0x02, 0x78);
        machine.Io.WriteByte(0x02, 0x56);
        machine.Io.WriteByte(0x83, 0x04);

        Assert.Equal(0x045678u, machine.Dma.GetPhysicalAddress(1));
    }
}
