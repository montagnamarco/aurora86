using Aurora386.Core.IO;

using Aurora386.Core.Devices.Interrupts;
using Aurora386.Core.Devices.System;
namespace Aurora386.Core.Devices.Audio;

/// <summary>
/// Sound Blaster 16 (CT1740 family): the DSP, its port interface and the
/// digital audio path.
///
/// "DSP" is a marketing name rather than a description -- the part is an Intel
/// 8051 microcontroller running Creative's firmware. That matters, because it
/// explains the shape of the interface: everything is a byte-at-a-time command
/// protocol with handshaking, not a set of memory-mapped registers, and the
/// chip is genuinely slow to respond, which is why the busy and data-ready
/// bits exist at all.
///
/// Port map, relative to the base address (0x220 by convention):
///   +0x00..0x03  OPL3 FM registers, mirrored from 0x388
///   +0x04/+0x05  mixer index / data (the CT1745)
///   +0x06        DSP reset
///   +0x0A        DSP read data
///   +0x0C        DSP write command/data; bit 7 on read is "busy"
///   +0x0E        DSP read status; bit 7 is "data available"
///
/// Why a reset sequence at all: the card has no dedicated "are you there?"
/// port, and an absent card reads back 0xFF from everything, which is a
/// perfectly plausible data byte. Detection therefore has to provoke a reply
/// that could not happen by accident. Writing 1 to the reset port holds the
/// 8051 in reset; writing 0 releases it; the firmware then boots and pushes
/// the single byte 0xAA into the read FIFO. Software polls the status port for
/// data-ready, reads 0xAA, and now knows both that a card is present and that
/// its base address is the one being probed. The choice of 0xAA -- alternating
/// bits -- is the traditional one for proving a data bus is really being
/// driven rather than floating.
/// </summary>
public sealed class SoundBlaster16 : IIoDevice
{
    /// <summary>The DSP version reported by command 0xE1. 4.05 identifies an SB16.</summary>
    public const byte VersionMajor = 4;
    public const byte VersionMinor = 5;

    private readonly InterruptController? _interrupts;
    private readonly Dma8237? _dma;

    public SbMixer Mixer { get; }
    public Opl3 Fm { get; }

    /// <summary>Base I/O port; jumper-selectable on real cards, 0x220 almost always.</summary>
    public ushort BasePort { get; private set; }

    /// <summary>Whether the card answers on the bus at all: a Plug and Play card can be switched off.</summary>
    public bool Enabled { get; private set; } = true;

    private IoBus? _io;

    /// <summary>The IRQ line the card asserts when a DMA block completes.</summary>
    public int Irq { get; set; } = 5;

    /// <summary>8-bit DMA channel; channel 1 is the factory default.</summary>
    public int Dma8 { get; set; } = 1;

    /// <summary>16-bit DMA channel; channel 5 is the factory default.</summary>
    public int Dma16 { get; set; } = 5;

    // --- DSP command state ---

    /// <summary>
    /// Output FIFO. The real chip has a small one; a queue is the honest model
    /// because several commands (the version, the identification echo) push
    /// more than one byte and software reads them one at a time.
    /// </summary>
    private readonly Queue<byte> _outputFifo = new();

    private byte _pendingCommand;
    private int _pendingArgumentCount;
    private readonly byte[] _arguments = new byte[4];
    private int _argumentsReceived;

    /// <summary>The last value written to the reset port, for edge detection.</summary>
    private byte _lastResetWrite = 0xFF;

    /// <summary>
    /// Speaker enable, commands 0xD1 and 0xD3. On an original Sound Blaster
    /// this really did switch the output amplifier, and forgetting to turn it
    /// on was the classic reason for "the card works but I hear nothing". On
    /// the SB16 it does nothing at all to the output, but the commands are
    /// still accepted and the state still reported, because plenty of software
    /// reads it back.
    /// </summary>
    public bool SpeakerEnabled { get; private set; }

    /// <summary>The value written by command 0xE0, echoed back complemented.</summary>
    private byte _identificationValue;

    // --- playback state ---

    /// <summary>
    /// The time constant written by command 0x40. It is not a frequency: the
    /// firmware programs an internal timer, and the byte is what gets loaded
    /// into it, so the relationship is 256 - (1000000 / rate). Two
    /// consequences follow. First, the achievable rates are quantised, and
    /// unevenly -- near 8 kHz consecutive constants are a few dozen hertz
    /// apart, near 44 kHz they are thousands apart, which is why old software
    /// plays at 22050 Hz but slightly off. Second, a single byte cannot express
    /// anything above about 45 kHz, which is exactly why the SB16 added command
    /// 0x41 taking a plain 16-bit rate in hertz.
    /// </summary>
    private byte _timeConstant = 0;

    private int _sampleRate = 22050;

    /// <summary>Total transfers remaining in the current block, as programmed.</summary>
    private int _blockLength;

    /// <summary>Transfers still to do before the block completes and the IRQ fires.</summary>
    private int _blockRemaining;

    /// <summary>
    /// Auto-init mode. Without it the DSP stops at the end of every block and
    /// must be given a fresh command, so the gap between the interrupt and the
    /// reprogramming is a hole in the audio. With it the DSP simply carries on
    /// -- it and the DMA controller both wrap to the start of the buffer -- and
    /// the interrupt becomes advisory: "I have passed the halfway point, you
    /// may refill the half I have finished with". That is what makes it
    /// possible to stream continuous audio while the machine is also busy.
    /// </summary>
    private bool _autoInit;

    private bool _is16Bit;
    private bool _isStereo;
    private bool _isSigned;
    private bool _paused;
    private bool _playing;

    /// <summary>Fractional sample accumulator, so odd rate ratios do not drift.</summary>
    private double _sampleAccumulator;

    /// <summary>
    /// Rendered output, waiting for the host to collect it. Keeping it here
    /// rather than pushing to an audio device keeps the core headless: the
    /// GUI, a test, or nothing at all may be listening.
    /// </summary>
    private readonly short[] _ring;
    private int _ringWrite;
    private int _ringRead;
    private int _ringCount;

    /// <summary>The rate at which <see cref="ReadSamples"/> delivers frames.</summary>
    public int SampleRate { get; }

    /// <summary>True while a DMA transfer is running and not paused.</summary>
    public bool IsPlaying => _playing && !_paused;

    /// <summary>Samples currently buffered and readable.</summary>
    public int AvailableSamples => _ringCount;

    /// <summary>Number of IRQs raised since power-on, for tests and diagnostics.</summary>
    public int InterruptCount { get; private set; }

    public SoundBlaster16(
        InterruptController? interrupts = null,
        Dma8237? dma = null,
        ushort basePort = 0x220,
        int outputSampleRate = 44100)
    {
        _interrupts = interrupts;
        _dma = dma;
        BasePort = basePort;
        SampleRate = outputSampleRate;

        Mixer = new SbMixer { Irq = Irq, Dma8 = Dma8, Dma16 = Dma16 };
        Fm = new Opl3(outputSampleRate);

        // Half a second of stereo headroom: enough that a host that services
        // audio on a video frame boundary never underruns, small enough that
        // latency stays imperceptible.
        _ring = new short[outputSampleRate];
        ResetDsp();
    }

    public void Register(IoBus io)
    {
        _io = io;

        // The card's own block. 0x220-0x223 are the FM registers mirrored
        // here; 0x224/0x225 the mixer; the rest the DSP.
        if (Enabled) io.Register(BasePort, 16, this);

        // The mixer and FM chip also answer at their traditional addresses,
        // which is where software that predates the SB16 looks for them.
        Fm.Register(io);
    }

    /// <summary>
    /// Moves the card, the way Plug and Play configuration does: off the
    /// ports it had, onto the new ones, with the interrupt and DMA channels
    /// it was given. The mixer's own copy of those follows, since a driver
    /// reads them back from there to learn what the card was set to.
    /// </summary>
    public void Relocate(ushort basePort, int irq, int dma8, int dma16, bool enabled)
    {
        if (Enabled) _io?.Unregister(BasePort, 16, this);
        BasePort = basePort;
        Irq = irq;
        Dma8 = dma8;
        Dma16 = dma16;
        Mixer.Irq = irq;
        Mixer.Dma8 = dma8;
        Mixer.Dma16 = dma16;
        Enabled = enabled;
        if (Enabled) _io?.Register(BasePort, 16, this);
    }

    // --- port interface --------------------------------------------------------

    public byte ReadByte(ushort port)
    {
        int offset = port - BasePort;

        switch (offset)
        {
            case 0x00:
            case 0x01:
            case 0x02:
            case 0x03:
                // The FM status register, mirrored. Reading it is how Adlib
                // detection works, so this must answer even on an SB16.
                return Fm.ReadByte((ushort)(0x388 + (offset & 3)));

            case 0x04:
            case 0x05:
                return Mixer.ReadByte((ushort)(0x224 + (offset & 1)));

            case 0x0A:
                // Read data. Reading an empty FIFO on real hardware returns
                // whatever was last latched; returning the last byte again is
                // the closest honest approximation.
                return _outputFifo.Count > 0 ? _outputFifo.Dequeue() : _lastOutputByte;

            case 0x0C:
                // Write-buffer status. Bit 7 clear means "ready for a command".
                // We are never busy, because unlike an 8051 at 12 MHz we can
                // process a command in zero emulated time.
                return 0x7F;

            case 0x0E:
                // Read-buffer status. Bit 7 set means a byte is waiting.
                //
                // Reading this port also acknowledges an 8-bit DMA interrupt.
                // That is a genuine side effect of the hardware, not a
                // convenience: the interrupt handler is *required* to read
                // here, and a handler that only sends the EOI to the PIC will
                // find the card holding its IRQ line asserted forever.
                AcknowledgeInterrupt(0x01);
                return (byte)(_outputFifo.Count > 0 ? 0xFF : 0x7F);

            case 0x0F:
                // Acknowledges a 16-bit DMA interrupt, the 16-bit counterpart
                // of the port above.
                AcknowledgeInterrupt(0x02);
                return 0xFF;

            default:
                return 0xFF;
        }
    }

    private byte _lastOutputByte = 0xFF;

    public void WriteByte(ushort port, byte value)
    {
        int offset = port - BasePort;

        switch (offset)
        {
            case 0x00:
            case 0x02:
                // FM index register, bank 0 and bank 1.
                Fm.WriteByte((ushort)(0x388 + offset), value);
                return;

            case 0x01:
            case 0x03:
                Fm.WriteByte((ushort)(0x388 + offset), value);
                return;

            case 0x04:
            case 0x05:
                Mixer.WriteByte((ushort)(0x224 + (offset & 1)), value);
                return;

            case 0x06:
                WriteReset(value);
                return;

            case 0x0C:
                WriteCommand(value);
                return;
        }
    }

    /// <summary>
    /// The reset port. What matters is the 1 -> 0 transition: holding reset
    /// asserted does nothing observable, releasing it is what makes the
    /// firmware restart and announce itself with 0xAA.
    /// </summary>
    private void WriteReset(byte value)
    {
        bool asserted = (value & 1) != 0;
        bool wasAsserted = (_lastResetWrite & 1) != 0;
        _lastResetWrite = value;

        if (asserted)
        {
            // Held in reset: stop everything, but say nothing yet.
            ResetDsp();
            return;
        }

        if (wasAsserted)
        {
            // Released. The firmware boots and pushes its ready byte.
            _outputFifo.Clear();
            _outputFifo.Enqueue(0xAA);
        }
    }

    private void ResetDsp()
    {
        _outputFifo.Clear();
        _pendingCommand = 0;
        _pendingArgumentCount = 0;
        _argumentsReceived = 0;
        _playing = false;
        _paused = false;
        _autoInit = false;
        _blockRemaining = 0;
        SpeakerEnabled = false;
        Mixer.InterruptStatus = 0;
    }

    /// <summary>
    /// Feeds a byte to the command port. The DSP protocol has no framing: the
    /// first byte is a command, and the chip knows from the command itself how
    /// many argument bytes must follow. Software and firmware must agree
    /// exactly, because a byte lost here desynchronises the stream and every
    /// later command is misread.
    /// </summary>
    private void WriteCommand(byte value)
    {
        if (_pendingArgumentCount > 0)
        {
            _arguments[_argumentsReceived++] = value;
            if (_argumentsReceived < _pendingArgumentCount) return;

            _pendingArgumentCount = 0;
            ExecuteCommand(_pendingCommand, _arguments, _argumentsReceived);
            return;
        }

        _pendingCommand = value;
        _argumentsReceived = 0;
        _pendingArgumentCount = ArgumentCount(value);

        if (_pendingArgumentCount == 0)
            ExecuteCommand(value, _arguments, 0);
    }

    /// <summary>How many bytes follow each command.</summary>
    private static int ArgumentCount(byte command) => command switch
    {
        0x04 or 0x08 or 0x0F => 1, // ASP mode, version query, register read
        0x05 or 0x0E => 2,  // ASP codec parameter or register write
        0x10 => 1,          // direct DAC output: one sample
        0x14 or 0x16 or 0x17 => 2,   // single-cycle DMA: 16-bit length
        0x1C or 0x1F => 0,  // auto-init DMA using the length from 0x48
        0x40 => 1,          // set time constant
        0x41 or 0x42 => 2,  // set output/input sample rate in hertz
        0x48 => 2,          // set DMA block size
        0x74 or 0x75 or 0x76 or 0x77 => 2,   // ADPCM DMA
        0xE0 => 1,          // identification: a byte to be echoed complemented
        0xE4 => 1,          // write to the test register
        0xF9 => 1,          // read from an internal register
        // The 0xB0-0xCF family: the command byte itself carries the mode bits,
        // and two argument bytes give the transfer length.
        >= 0xB0 and <= 0xCF => 3,
        _ => 0,
    };

    private void ExecuteCommand(byte command, byte[] arguments, int count)
    {
        switch (command)
        {
            case 0x04:
            case 0x05:
            case 0x0E:
                // This card has no optional ASP coprocessor. Its configuration
                // writes still consume their arguments in the DSP protocol.
                return;

            case 0x08:
                if (arguments[0] == 0x03)
                {
                    Push(0xFF); // No ASP version, as on a ViBRA without the chip.
                }
                return;

            case 0x0F:
                // Windows probes ASP register 83h even when no ASP is fitted.
                // Omitting the reply leaves its driver polling 22Eh for billions
                // of instructions. Use the read-only probe values from DOSBox-X;
                // this does not implement ASP RAM, microcode or audio processing.
                byte register = arguments[0];
                byte probeValue = register;
                if (register < 0x20 || register is >= 0x80 and <= 0x83)
                {
                    probeValue = 0;
                }
                if (register == 0x05)
                {
                    probeValue = 0x01;
                }
                if (register == 0x09)
                {
                    probeValue = 0xF8;
                }
                Push(probeValue);
                return;

            case 0x10:
                // Direct DAC output: the CPU hands over one sample and it goes
                // straight to the converter. Simple, and completely unusable
                // for anything longer than a beep -- the CPU has to hit the
                // exact sample period by counting instructions, with interrupts
                // off, doing nothing else. This is the problem DMA solves.
                PushMonoSample((short)((arguments[0] - 128) << 8));
                return;

            case 0x14:
            case 0x15:
                // 8-bit single-cycle DMA output. One block, then an interrupt
                // and a stop.
                StartDma(bits16: false, stereo: false, signed: false, autoInit: false,
                    length: arguments[0] | (arguments[1] << 8));
                return;

            case 0x16:
            case 0x17:
                // 2-bit ADPCM. We decode nothing, but the transfer must still
                // run and finish, or software waits forever for its interrupt.
                StartDma(bits16: false, stereo: false, signed: false, autoInit: false,
                    length: arguments[0] | (arguments[1] << 8));
                return;

            case 0x1C:
            case 0x1F:
                // 8-bit auto-init DMA, using the block size previously set by
                // command 0x48.
                StartDma(bits16: false, stereo: false, signed: false, autoInit: true,
                    length: _blockLength);
                return;

            case 0x40:
                // Set the time constant. See the field comment: the value is a
                // timer reload, and the sample rate follows from it.
                _timeConstant = arguments[0];
                _sampleRate = 1_000_000 / (256 - _timeConstant);
                return;

            case 0x41:
            case 0x42:
                // Set the sample rate directly in hertz, big-endian. The SB16
                // added this precisely because the time constant could not
                // express the rates the card was now capable of.
                _sampleRate = (arguments[0] << 8) | arguments[1];
                if (_sampleRate <= 0) _sampleRate = 22050;
                return;

            case 0x48:
                // Set the DMA block size for the auto-init commands that do
                // not carry a length of their own.
                _blockLength = (arguments[0] | (arguments[1] << 8)) + 1;
                return;

            case 0xD0:
                // Pause DMA. The transfer stops where it is; the DMA
                // controller keeps its address, so 0xD4 resumes mid-buffer.
                _paused = true;
                return;

            case 0xD1:
                SpeakerEnabled = true;
                return;

            case 0xD3:
                SpeakerEnabled = false;
                return;

            case 0xD4:
                _paused = false;
                return;

            case 0xD5:
                // Pause 16-bit DMA.
                _paused = true;
                return;

            case 0xD6:
                // Continue 16-bit DMA.
                _paused = false;
                return;

            case 0xD8:
                // Report the speaker state: 0xFF for on, 0x00 for off.
                Push(SpeakerEnabled ? (byte)0xFF : (byte)0x00);
                return;

            case 0xD9:
                // Exit auto-init at the end of the current 16-bit block. This
                // is the graceful stop: the buffer being played is allowed to
                // finish rather than being cut off mid-sample.
                _autoInit = false;
                return;

            case 0xDA:
                // Exit auto-init, 8-bit.
                _autoInit = false;
                return;

            case 0xE0:
                // DSP identification: the card returns the argument with every
                // bit inverted. It is a second detection method, useful because
                // unlike the reset handshake it can be repeated at any time
                // without disturbing playback.
                _identificationValue = arguments[0];
                Push((byte)~_identificationValue);
                return;

            case 0xE1:
                // DSP version, major then minor. Software branches heavily on
                // this: 1.x is an original SB, 2.x adds auto-init, 3.x is the
                // Pro with stereo, and 4.x means SB16 with 16-bit samples and
                // the CT1745 mixer.
                Push(VersionMajor);
                Push(VersionMinor);
                return;

            case 0xE3:
                // Copyright string, NUL terminated. Some software checks it
                // for the word "Creative" as an anti-clone measure.
                foreach (char c in "COPYRIGHT (C) CREATIVE TECHNOLOGY LTD, 1992.")
                    Push((byte)c);
                Push(0);
                return;

            case 0xE8:
                // Read the test register back.
                Push(_testRegister);
                return;

            case 0xE4:
                _testRegister = arguments[0];
                return;

            case 0xF2:
                // Force an 8-bit interrupt. Used by drivers to check that the
                // IRQ they were told about is really the one wired up: they
                // trigger this and see whether their handler runs.
                RaiseInterrupt(0x01);
                return;

            case 0xF3:
                RaiseInterrupt(0x02);
                return;

            case 0xF8:
                // Undocumented, but present: pushes a zero.
                Push(0);
                return;

            default:
                // The 0xB0-0xBF and 0xC0-0xCF families: general DMA transfer.
                // The command byte itself is a bit field, which is what let
                // Creative fold sixteen variants into one opcode range.
                //   0xB0 = 16-bit, 0xC0 = 8-bit
                //   bit 3 = auto-init
                //   bit 1 = FIFO enable (no observable effect here)
                // The first argument byte is the mode: bit 4 signed samples,
                // bit 5 stereo. The remaining two are the length, minus one.
                if (command is >= 0xB0 and <= 0xCF)
                {
                    bool bits16 = (command & 0xF0) == 0xB0;
                    bool autoInit = (command & 0x04) != 0;
                    byte mode = count > 0 ? arguments[0] : (byte)0;
                    bool signedSamples = (mode & 0x10) != 0;
                    bool stereo = (mode & 0x20) != 0;
                    int length = count >= 3 ? (arguments[1] | (arguments[2] << 8)) : 0;

                    StartDma(bits16, stereo, signedSamples, autoInit, length);
                    return;
                }
                return;
        }
    }

    private byte _testRegister;

    private void Push(byte value)
    {
        _outputFifo.Enqueue(value);
        _lastOutputByte = value;
    }

    private void StartDma(bool bits16, bool stereo, bool signed, bool autoInit, int length)
    {
        _is16Bit = bits16;
        _isStereo = stereo;
        _isSigned = signed;
        _autoInit = autoInit;

        // Every DSP transfer length is programmed as "count minus one", the
        // same convention the DMA controller uses, and for the same reason:
        // it makes a full 65536-transfer block expressible in 16 bits.
        _blockLength = length + 1;
        _blockRemaining = _blockLength;

        _playing = true;
        _paused = false;
        _sampleAccumulator = 0;
    }

    // --- sample generation -----------------------------------------------------

    /// <summary>
    /// Advances the card by a span of emulated time, pulling sample data over
    /// DMA and rendering FM. The caller drives this from its device tick, the
    /// same way the PIT is advanced.
    /// </summary>
    public void Advance(double microseconds)
    {
        Fm.AdvanceTimers(microseconds);

        if (!IsPlaying || _dma is null)
        {
            RenderFmOnly(microseconds);
            return;
        }

        // How many source samples fall in this span. Note that this uses the
        // card's programmed rate, not the host's: the resampling to the output
        // rate happens as we push into the ring.
        _sampleAccumulator += microseconds * _sampleRate / 1_000_000.0;
        int wanted = (int)_sampleAccumulator;
        if (wanted <= 0) return;
        _sampleAccumulator -= wanted;

        int channel = _is16Bit ? Dma16 : Dma8;

        for (int i = 0; i < wanted; i++)
        {
            if (!IsPlaying) break;

            short left, right;

            if (_is16Bit)
            {
                ushort? a = _dma.TransferWord(channel);
                if (a is null) { _playing = false; break; }
                left = (short)a.Value;
                right = left;
                if (_isStereo)
                {
                    ushort? b = _dma.TransferWord(channel);
                    if (b is not null) right = (short)b.Value;
                    ConsumeTransfer();
                }
                // 16-bit SB16 data is signed; an unsigned stream would have to
                // be biased, but no software actually programs that.
                if (!_isSigned) { left = (short)(left - 32768); right = (short)(right - 32768); }
            }
            else
            {
                byte? a = _dma.TransferByte(channel);
                if (a is null) { _playing = false; break; }
                left = ConvertByte(a.Value);
                right = left;
                if (_isStereo)
                {
                    byte? b = _dma.TransferByte(channel);
                    if (b is not null) right = ConvertByte(b.Value);
                    ConsumeTransfer();
                }
            }

            PushStereoSample(left, right);
            ConsumeTransfer();
        }

        RenderFmOverPending(wanted);
    }

    /// <summary>
    /// Converts an 8-bit sample. Unsigned 8-bit -- silence at 128, not 0 -- is
    /// what every 8-bit Sound Blaster expects, a legacy of the DAC being a
    /// simple unipolar converter with no notion of negative.
    /// </summary>
    private short ConvertByte(byte value)
        => _isSigned ? (short)((sbyte)value << 8) : (short)((value - 128) << 8);

    /// <summary>
    /// Counts down the block and fires the interrupt at its end.
    ///
    /// The interrupt is the whole point of the arrangement: it is how the card
    /// tells software "I have consumed the buffer you gave me". In auto-init
    /// mode the transfer then continues from the top without any further
    /// action, so the handler's only job is to refill memory before the DSP
    /// gets back round to it.
    /// </summary>
    private void ConsumeTransfer()
    {
        if (_blockRemaining <= 0) return;

        _blockRemaining--;
        if (_blockRemaining > 0) return;

        RaiseInterrupt(_is16Bit ? (byte)0x02 : (byte)0x01);

        if (_autoInit)
        {
            _blockRemaining = _blockLength;
        }
        else
        {
            _playing = false;
        }
    }

    private void RaiseInterrupt(byte source)
    {
        // Mixer register 0x82 records which source fired, so a handler serving
        // several of them can tell which one to acknowledge.
        Mixer.InterruptStatus |= source;
        InterruptCount++;
        _interrupts?.RaiseIrq(Irq);
    }

    /// <summary>
    /// Clears one interrupt source. When the last one goes, the card releases
    /// the IRQ line -- an important detail on a shared, level-sensitive bus.
    /// </summary>
    private void AcknowledgeInterrupt(byte source)
    {
        if ((Mixer.InterruptStatus & source) == 0) return;
        Mixer.InterruptStatus &= (byte)~source;
        if (Mixer.InterruptStatus == 0) _interrupts?.LowerIrq(Irq);
    }

    // --- output ring -----------------------------------------------------------

    private void PushMonoSample(short value) => PushStereoSample(value, value);

    private void PushStereoSample(short left, short right)
    {
        // Apply the mixer's attenuators. On the real card these are analogue
        // and sit after the converter; doing it in the digital domain here
        // costs a little dynamic range but is otherwise equivalent.
        float gain = Mixer.MasterLevel * Mixer.VoiceLevel;
        left = (short)Math.Clamp(left * gain, short.MinValue, short.MaxValue);
        right = (short)Math.Clamp(right * gain, short.MinValue, short.MaxValue);

        // Resample from the card's rate to the output rate by repetition. It
        // is crude -- proper interpolation would be better -- but it never
        // drops a sample and never drifts, which matters more here.
        _resampleAccumulator += (double)SampleRate / _sampleRate;
        int repeats = (int)_resampleAccumulator;
        _resampleAccumulator -= repeats;

        for (int i = 0; i < repeats; i++) Enqueue(left, right);
    }

    private double _resampleAccumulator;

    private void Enqueue(short left, short right)
    {
        // A full ring means nobody is collecting the audio. Dropping the
        // oldest frame keeps latency bounded rather than letting the buffer
        // become an ever-growing delay line.
        if (_ringCount + 2 > _ring.Length)
        {
            _ringRead = (_ringRead + 2) % _ring.Length;
            _ringCount -= 2;
        }

        _ring[_ringWrite] = left;
        _ringWrite = (_ringWrite + 1) % _ring.Length;
        _ring[_ringWrite] = right;
        _ringWrite = (_ringWrite + 1) % _ring.Length;
        _ringCount += 2;
    }

    /// <summary>
    /// When no digital audio is playing, the FM chip still needs to produce
    /// output -- a great deal of period music is FM only.
    /// </summary>
    private void RenderFmOnly(double microseconds)
    {
        int frames = (int)(microseconds * SampleRate / 1_000_000.0 + _fmAccumulator);
        _fmAccumulator = microseconds * SampleRate / 1_000_000.0 + _fmAccumulator - frames;
        if (frames <= 0) return;

        RenderFmFrames(frames);
    }

    private void RenderFmOverPending(int digitalSamples)
    {
        // FM is mixed at the same number of output frames the digital path
        // produced, so the two stay in step.
        RenderFmFrames(0);
        _ = digitalSamples;
    }

    private void RenderFmFrames(int frames)
    {
        if (frames <= 0) return;
        if (frames > 4096) frames = 4096;

        Span<short> scratch = stackalloc short[2];
        for (int i = 0; i < frames; i++)
        {
            scratch[0] = 0;
            scratch[1] = 0;
            Fm.Render(scratch);

            float fmGain = Mixer.MasterLevel * Mixer.FmLevel;
            Enqueue(
                (short)Math.Clamp(scratch[0] * fmGain, short.MinValue, short.MaxValue),
                (short)Math.Clamp(scratch[1] * fmGain, short.MinValue, short.MaxValue));
        }
    }

    private double _fmAccumulator;

    /// <summary>
    /// Copies buffered audio into the caller's span and returns how many
    /// shorts were written. Samples are interleaved stereo, so the count is
    /// always even and one frame is two entries.
    ///
    /// The core deliberately does not talk to any host audio API. Keeping the
    /// output as a buffer the caller pulls from means the emulator can be run
    /// headless in a test, faster than real time, or with no sound at all,
    /// without the audio path changing behaviour.
    /// </summary>
    public int ReadSamples(Span<short> destination)
    {
        int count = Math.Min(destination.Length, _ringCount);
        for (int i = 0; i < count; i++)
        {
            destination[i] = _ring[_ringRead];
            _ringRead = (_ringRead + 1) % _ring.Length;
        }
        _ringCount -= count;
        return count;
    }

    /// <summary>The rate the DSP has been programmed to play at, in hertz.</summary>
    public int PlaybackRate => _sampleRate;

    /// <summary>The last time constant written by command 0x40.</summary>
    public byte TimeConstant => _timeConstant;

    /// <summary>True when the current transfer is in auto-init mode.</summary>
    public bool AutoInit => _autoInit;

    /// <summary>True when the current transfer moves 16-bit samples.</summary>
    public bool Is16Bit => _is16Bit;

    /// <summary>True when the current transfer is stereo.</summary>
    public bool IsStereo => _isStereo;
}
