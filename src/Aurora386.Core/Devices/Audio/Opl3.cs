using Aurora386.Core.IO;

namespace Aurora386.Core.Devices.Audio;

/// <summary>
/// YMF262 (OPL3) FM synthesiser, as fitted to the Sound Blaster 16.
///
/// FM synthesis exists because in 1985 a wavetable of real instrument samples
/// needed megabytes of ROM, and a chip that computes its waveform from a
/// handful of registers needed none. The trick, patented by John Chowning at
/// Stanford, is that modulating the *phase* of one sine oscillator with the
/// output of another produces a spectrum of sidebands whose richness follows
/// the modulator's amplitude. So a single envelope on the modulator sweeps an
/// entire timbre from pure to brassy, which is exactly how real instruments
/// behave -- and it costs two sine lookups and an add.
///
/// The chip has 18 operators, each a sine oscillator with its own ADSR
/// envelope, paired into 9 two-operator voices (OPL3 can also chain pairs into
/// 4-operator voices). An operator is not a voice: it is one oscillator, and
/// whether it is heard directly or only heard through its effect on another is
/// decided by the connection bit.
///
/// This implementation models the register file, the phase accumulators and
/// the envelope generators faithfully enough to produce correct pitches and
/// amplitude envelopes. The operator pairing is honoured in additive mode; the
/// full frequency-modulation feedback path is deliberately simplified, so
/// timbres are purer than real hardware.
/// </summary>
public sealed partial class Opl3 : IIoDevice
{
    /// <summary>
    /// The chip's master clock divided by 288, giving the rate at which the
    /// hardware itself produces samples: 14.318 MHz / 288 = 49716 Hz. Every
    /// envelope and phase rate in the datasheet is expressed against this, so
    /// synthesising at any other rate means rescaling everything.
    /// </summary>
    public const int NativeSampleRate = 49716;

    private const int OperatorCount = 18;

    /// <summary>
    /// Quarter-sine log table, 1024 entries -- the same resolution the real
    /// chip uses. The hardware works in the logarithmic domain throughout
    /// (attenuation in units of 0.1875 dB) because adding logs is how it
    /// multiplies without a multiplier.
    /// </summary>
    private static readonly float[] SineTable = BuildSineTable();

    private static float[] BuildSineTable()
    {
        var table = new float[1024];
        for (int i = 0; i < table.Length; i++)
            table[i] = MathF.Sin((i + 0.5f) * MathF.PI * 2f / table.Length);
        return table;
    }

    private enum EnvelopePhase
    {
        /// <summary>Silent, waiting for a key-on.</summary>
        Off,
        /// <summary>Rising to full level. The only phase that goes up.</summary>
        Attack,
        /// <summary>Falling from full level to the sustain level.</summary>
        Decay,
        /// <summary>Holding at the sustain level while the key is held.</summary>
        Sustain,
        /// <summary>Falling to silence after key-off.</summary>
        Release,
    }

    /// <summary>
    /// One of the 18 operators: a sine oscillator plus an ADSR envelope.
    /// </summary>
    private sealed class Operator
    {
        // --- register-backed parameters ---

        /// <summary>Frequency multiplier, register 0x20 bits 0-3. Not linear: 0 means x0.5.</summary>
        public int Multiple = 1;

        /// <summary>Total level, register 0x40 bits 0-5: attenuation in 0.75 dB steps.</summary>
        public int TotalLevel;

        /// <summary>Key scale level, register 0x40 bits 6-7: how much high notes are attenuated.</summary>
        public int KeyScaleLevel;

        public int AttackRate;
        public int DecayRate;

        /// <summary>Sustain level, register 0x80 bits 4-7: attenuation held while the key is down.</summary>
        public int SustainLevel;

        public int ReleaseRate;

        /// <summary>
        /// Register 0x20 bit 5: when set, the envelope holds at the sustain
        /// level indefinitely (an organ). When clear, it decays away even with
        /// the key held (a plucked string).
        /// </summary>
        public bool SustainEnabled;

        /// <summary>Register 0x20 bit 4: key scale rate -- envelopes run faster at high pitch, as real instruments do.</summary>
        public bool KeyScaleRate;

        /// <summary>Register 0xE0: which of the eight waveform variants to use. OPL2 had four.</summary>
        public int Waveform;

        // --- running state ---

        /// <summary>Phase accumulator, in fractional table positions.</summary>
        public double Phase;

        /// <summary>Current envelope output, 0 (silent) to 1 (full).</summary>
        public double EnvelopeLevel;

        public EnvelopePhase Phase_ = EnvelopePhase.Off;

        public void KeyOn()
        {
            Phase = 0;
            Phase_ = EnvelopePhase.Attack;
        }

        public void KeyOff()
        {
            if (Phase_ != EnvelopePhase.Off) Phase_ = EnvelopePhase.Release;
        }
    }

    /// <summary>One of the 9 (x2 banks) two-operator channels.</summary>
    private sealed class Voice
    {
        /// <summary>F-number: the 10-bit fractional part of the pitch, register 0xA0 plus 0xB0 bits 0-1.</summary>
        public int FNumber;

        /// <summary>Block: the octave, register 0xB0 bits 2-4. Pitch is FNumber * 2^(Block-1).</summary>
        public int Block;

        public bool KeyOn;

        /// <summary>Feedback amount, register 0xC0 bits 1-3: the modulator fed back into itself.</summary>
        public int Feedback;

        /// <summary>
        /// Connection, register 0xC0 bit 0. Clear means FM: operator 1
        /// modulates operator 2 and only operator 2 is heard. Set means
        /// additive: both operators are heard directly, giving two independent
        /// sines rather than one complex timbre.
        /// </summary>
        public bool Additive;

        /// <summary>OPL3 stereo: register 0xC0 bits 4-5 pan the voice left/right.</summary>
        public bool Left = true;
        public bool Right = true;
    }

    private readonly Operator[] _operators = new Operator[OperatorCount * 2];
    private readonly Voice[] _voices = new Voice[18];
    private readonly byte[,] _registers = new byte[2, 256];

    /// <summary>The register selected by the last write to the index port, per bank.</summary>
    private readonly int[] _selected = [0, 0];

    /// <summary>
    /// OPL3 mode, register 0x105 bit 0. Until software sets it the chip is
    /// bit-exactly an OPL2, which is how a card can be both -- and why OPL3
    /// detection works by writing this bit and checking the status register
    /// behaves differently.
    /// </summary>
    private bool _opl3Mode;

    private readonly int _outputRate;

    /// <summary>
    /// Timers 1 and 2. Their only visible effect is setting status bits, but
    /// that is precisely what every OPL detection routine looks at: it starts
    /// a timer, waits, and checks whether the overflow flag appeared.
    /// </summary>
    private byte _timer1;
    private byte _timer2;
    private byte _timerControl;
    private byte _status;

    public Opl3(int outputRate = 44100)
    {
        _outputRate = outputRate;
        for (int i = 0; i < _operators.Length; i++) _operators[i] = new Operator();
        for (int i = 0; i < _voices.Length; i++) _voices[i] = new Voice();
    }

    public void Register(IoBus io)
    {
        // The adlib-compatible pair, where every FM card has always lived, and
        // the OPL3's second register bank two ports above it.
        io.Register(0x388, 4, this);
    }

    /// <summary>
    /// Status register. Bit 7 mirrors "either timer expired"; bits 6 and 5 are
    /// the individual timers. Bits 1-2 read back as zero on an OPL3 and as
    /// something else on an OPL2, which is one way software tells them apart.
    /// </summary>
    public byte ReadByte(ushort port)
    {
        // Only the even port in each pair is readable; the data ports are
        // write-only and float high.
        if ((port & 1) != 0) return 0xFF;
        return _status;
    }

    public void WriteByte(ushort port, byte value)
    {
        // 0x388/0x389 select and write bank 0; 0x38A/0x38B do the same for
        // bank 1, the extra nine OPL3 voices.
        int bank = (port & 2) != 0 ? 1 : 0;

        if ((port & 1) == 0) _selected[bank] = value;
        else WriteRegister(bank, _selected[bank], value);
    }

    /// <summary>
    /// Writes an FM register. Exposed because the Sound Blaster mirrors the
    /// same registers at its own base ports 0x220-0x223, so that a program can
    /// drive both digital audio and FM without knowing about port 0x388.
    /// </summary>
    public void WriteRegister(int bank, int register, byte value)
    {
        register &= 0xFF;
        _registers[bank, register] = value;

        // 0x01: test register / waveform select enable.
        if (bank == 0 && register == 0x01) return;

        if (register == 0x02) { _timer1 = value; return; }
        if (register == 0x03) { _timer2 = value; return; }

        if (register == 0x04)
        {
            if (bank == 1)
            {
                // 0x104: which voice pairs are joined into 4-operator voices.
                return;
            }

            // 0x04 on bank 0 is the timer control register. Bit 7 resets the
            // status flags, and it is the one write every detection routine
            // makes -- clear the flags, start a timer, look again.
            if ((value & 0x80) != 0)
            {
                _status = 0;
                _timerControl = 0;
                return;
            }
            _timerControl = value;
            return;
        }

        if (bank == 1 && register == 0x05)
        {
            _opl3Mode = (value & 1) != 0;
            return;
        }

        // The register map is not uniform, and the split matters.
        //
        // The operator blocks (0x20, 0x40, 0x60, 0x80, 0xE0) each cover 32
        // addresses, because the 18 operators are laid out with holes. Masking
        // with 0xE0 and taking five index bits is right for those.
        //
        // The voice blocks (0xA0, 0xB0, 0xC0) are only 9 registers each and
        // sit 16 apart, so bit 4 is part of the block number rather than the
        // index. Using the operator masks here would fold 0xB0 into 0xA0 and
        // hand it index 16 -- past the ninth voice, so every key-on would be
        // silently dropped and the chip would never make a sound.
        int group, index;
        if (register >= 0xA0 && register < 0xE0)
        {
            group = register & 0xF0;
            index = register & 0x0F;
        }
        else
        {
            group = register & 0xE0;
            index = register & 0x1F;
        }

        switch (group)
        {
            case 0x20:   // multiple / key-scale-rate / sustain / vibrato / tremolo
            {
                Operator? op = GetOperator(bank, index);
                if (op is null) return;
                op.Multiple = value & 0x0F;
                op.KeyScaleRate = (value & 0x10) != 0;
                op.SustainEnabled = (value & 0x20) != 0;
                return;
            }

            case 0x40:   // key scale level / total level
            {
                Operator? op = GetOperator(bank, index);
                if (op is null) return;
                op.TotalLevel = value & 0x3F;
                op.KeyScaleLevel = (value >> 6) & 3;
                return;
            }

            case 0x60:   // attack rate / decay rate
            {
                Operator? op = GetOperator(bank, index);
                if (op is null) return;
                op.AttackRate = (value >> 4) & 0x0F;
                op.DecayRate = value & 0x0F;
                return;
            }

            case 0x80:   // sustain level / release rate
            {
                Operator? op = GetOperator(bank, index);
                if (op is null) return;
                op.SustainLevel = (value >> 4) & 0x0F;
                op.ReleaseRate = value & 0x0F;
                return;
            }

            case 0xE0:   // waveform select
            {
                Operator? op = GetOperator(bank, index);
                if (op is null) return;
                // Only four waveforms exist unless OPL3 mode is on.
                op.Waveform = value & (_opl3Mode ? 7 : 3);
                return;
            }

            case 0xA0:   // F-number low byte
            {
                if (index >= 9) return;
                Voice voice = _voices[bank * 9 + index];
                voice.FNumber = (voice.FNumber & 0x300) | value;
                return;
            }

            case 0xB0:   // F-number high bits / block / key on
            {
                if (index >= 9) return;
                Voice voice = _voices[bank * 9 + index];
                voice.FNumber = (voice.FNumber & 0xFF) | ((value & 3) << 8);
                voice.Block = (value >> 2) & 7;

                bool keyOn = (value & 0x20) != 0;
                if (keyOn != voice.KeyOn)
                {
                    voice.KeyOn = keyOn;
                    int slot = bank * 9 + index;
                    Operator modulator = _operators[ModulatorIndex(slot)];
                    Operator carrier = _operators[CarrierIndex(slot)];
                    if (keyOn) { modulator.KeyOn(); carrier.KeyOn(); }
                    else { modulator.KeyOff(); carrier.KeyOff(); }
                }
                return;
            }

            case 0xC0:   // feedback / connection / stereo panning
            {
                if (index >= 9) return;
                Voice voice = _voices[bank * 9 + index];
                voice.Additive = (value & 1) != 0;
                voice.Feedback = (value >> 1) & 7;
                // In OPL2 mode both channels are always on: the panning bits
                // did not exist, so a card that ignored them would be silent.
                voice.Left = !_opl3Mode || (value & 0x10) != 0;
                voice.Right = !_opl3Mode || (value & 0x20) != 0;
                return;
            }
        }
    }

    public byte ReadRegister(int bank, int register) => _registers[bank, register & 0xFF];

    /// <summary>
    /// Maps a register's operator index to an operator. The chip's operator
    /// numbering has holes: registers 0x06-0x07, 0x0E-0x0F and 0x16-0x1F
    /// address nothing, because the 18 operators are laid out as three groups
    /// of six within a 32-slot address space.
    /// </summary>
    private Operator? GetOperator(int bank, int index)
    {
        int column = index & 7;
        if (column >= 6) return null;
        int row = index >> 3;
        if (row >= 3) return null;
        int slot = row * 6 + column;
        return _operators[bank * OperatorCount + slot];
    }

    /// <summary>
    /// The modulator of a two-operator voice. The pairing is not adjacent:
    /// voice N uses operator slots N and N+3 within each group of six, an
    /// arrangement that let Yamaha reuse the same internal counter for both.
    /// </summary>
    private static int ModulatorIndex(int voice)
    {
        int bank = voice / 9;
        int v = voice % 9;
        return bank * OperatorCount + (v / 3) * 6 + (v % 3);
    }

    private static int CarrierIndex(int voice) => ModulatorIndex(voice) + 3;

    /// <summary>
    /// Renders interleaved stereo samples into the buffer, adding to whatever
    /// is already there so the caller can mix FM over digital audio.
    /// </summary>
    public void Render(Span<short> destination)
    {
        int frames = destination.Length / 2;

        for (int frame = 0; frame < frames; frame++)
        {
            double left = 0, right = 0;

            for (int v = 0; v < _voices.Length; v++)
            {
                Voice voice = _voices[v];
                if (voice.FNumber == 0) continue;
                if (v >= 9 && !_opl3Mode) continue;

                Operator modulator = _operators[ModulatorIndex(v)];
                Operator carrier = _operators[CarrierIndex(v)];

                double modOutput = StepOperator(modulator, voice, 0);
                // In FM mode the modulator's output shifts the carrier's phase
                // rather than being heard; in additive mode both are summed.
                double carOutput = StepOperator(carrier, voice, voice.Additive ? 0 : modOutput);
                double sample = voice.Additive ? modOutput + carOutput : carOutput;

                if (voice.Left) left += sample;
                if (voice.Right) right += sample;
            }

            // 18 voices summing to full scale would be inaudibly quiet for the
            // usual case of a handful of voices, so scale for a realistic load.
            AddClamped(destination, frame * 2, left / 6.0);
            AddClamped(destination, frame * 2 + 1, right / 6.0);
        }
    }

    private static void AddClamped(Span<short> destination, int index, double value)
    {
        int mixed = destination[index] + (int)(value * 8000);
        destination[index] = (short)Math.Clamp(mixed, short.MinValue, short.MaxValue);
    }

    /// <summary>
    /// Advances one operator by a single sample and returns its output.
    /// </summary>
    private double StepOperator(Operator op, Voice voice, double phaseModulation)
    {
        AdvanceEnvelope(op);
        if (op.EnvelopeLevel <= 0.0 && op.Phase_ == EnvelopePhase.Off) return 0;

        // The pitch: F-number scaled by the block (an octave shift), against
        // the chip's own 49716 Hz reference. The "multiple" then harmonically
        // relates the operator to its voice -- except for the value 0, which
        // means half rather than zero, so an operator can sound an octave down.
        double multiple = op.Multiple == 0 ? 0.5 : op.Multiple == 11 || op.Multiple == 13 ? op.Multiple - 1
            : op.Multiple == 14 ? 15 : op.Multiple;

        double frequency = voice.FNumber * Math.Pow(2, voice.Block - 1)
                           * NativeSampleRate / (1 << 19) * multiple;

        op.Phase += frequency / _outputRate;
        if (op.Phase >= 1.0) op.Phase -= Math.Floor(op.Phase);

        double phase = op.Phase + phaseModulation;
        phase -= Math.Floor(phase);

        double raw = Waveform(op.Waveform, phase);

        // Total level is attenuation in 0.75 dB steps, so it multiplies the
        // amplitude by 10^(-0.75*TL/20).
        double attenuation = Math.Pow(10.0, -0.75 * op.TotalLevel / 20.0);
        return raw * op.EnvelopeLevel * attenuation;
    }

    /// <summary>
    /// The eight OPL3 waveforms, all derived from the one sine table by
    /// rectifying or truncating it -- another consequence of having a single
    /// ROM to work from.
    /// </summary>
    private static double Waveform(int waveform, double phase)
    {
        int position = (int)(phase * SineTable.Length) & (SineTable.Length - 1);
        float sine = SineTable[position];

        return waveform switch
        {
            0 => sine,                                        // full sine
            1 => sine > 0 ? sine : 0,                         // half-wave rectified
            2 => Math.Abs(sine),                              // full-wave rectified
            3 => phase % 0.5 < 0.25 ? Math.Abs(sine) : 0,     // pulse sine (quarter segments)
            4 => phase < 0.5 ? SineTable[(int)(phase * 2 * SineTable.Length) & (SineTable.Length - 1)] : 0,
            5 => phase < 0.5 ? Math.Abs(SineTable[(int)(phase * 2 * SineTable.Length) & (SineTable.Length - 1)]) : 0,
            6 => sine >= 0 ? 1.0 : -1.0,                      // square
            _ => 1.0 - 2.0 * phase,                           // derived sawtooth
        };
    }

    /// <summary>
    /// One step of the ADSR envelope. The rates are 4-bit codes, not times:
    /// each step up roughly doubles the speed, giving a range from a few
    /// milliseconds to several seconds with only 16 values.
    /// </summary>
    private void AdvanceEnvelope(Operator op)
    {
        switch (op.Phase_)
        {
            case EnvelopePhase.Off:
                op.EnvelopeLevel = 0;
                return;

            case EnvelopePhase.Attack:
            {
                if (op.AttackRate == 0) { op.EnvelopeLevel = 0; return; }
                if (op.AttackRate >= 15) { op.EnvelopeLevel = 1; op.Phase_ = EnvelopePhase.Decay; return; }
                op.EnvelopeLevel += RateToStep(op.AttackRate);
                if (op.EnvelopeLevel >= 1.0)
                {
                    op.EnvelopeLevel = 1.0;
                    op.Phase_ = EnvelopePhase.Decay;
                }
                return;
            }

            case EnvelopePhase.Decay:
            {
                double sustain = 1.0 - op.SustainLevel / 15.0;
                if (op.DecayRate == 0) { op.Phase_ = EnvelopePhase.Sustain; return; }
                op.EnvelopeLevel -= RateToStep(op.DecayRate);
                if (op.EnvelopeLevel <= sustain)
                {
                    op.EnvelopeLevel = sustain;
                    op.Phase_ = EnvelopePhase.Sustain;
                }
                return;
            }

            case EnvelopePhase.Sustain:
                // With sustain disabled the note keeps fading even while held,
                // which is how percussive sounds are made on a chip that has
                // no separate "decay 2" stage.
                if (!op.SustainEnabled)
                {
                    op.EnvelopeLevel -= RateToStep(op.ReleaseRate);
                    if (op.EnvelopeLevel <= 0) { op.EnvelopeLevel = 0; op.Phase_ = EnvelopePhase.Off; }
                }
                return;

            case EnvelopePhase.Release:
            {
                op.EnvelopeLevel -= RateToStep(op.ReleaseRate);
                if (op.EnvelopeLevel <= 0)
                {
                    op.EnvelopeLevel = 0;
                    op.Phase_ = EnvelopePhase.Off;
                }
                return;
            }
        }
    }

    /// <summary>
    /// Converts a 4-bit rate code into a per-sample level change. The real
    /// chip uses a table of counter increments; this approximates its shape,
    /// where rate 1 takes seconds and rate 15 is effectively instant.
    /// </summary>
    private double RateToStep(int rate)
    {
        if (rate <= 0) return 0;
        double seconds = 8.0 / Math.Pow(2, rate * 0.75);
        return 1.0 / (seconds * _outputRate);
    }

    /// <summary>True once software has enabled the OPL3's extended mode.</summary>
    public bool Opl3ModeEnabled => _opl3Mode;

    /// <summary>Exposed for tests and diagnostics: the raw status byte.</summary>
    public byte Status => _status;

    /// <summary>
    /// Advances the two timers. They are the chip's only source of interrupts
    /// and, far more importantly, the mechanism every game's detection routine
    /// uses to prove an OPL is really there.
    /// </summary>
    public void AdvanceTimers(double microseconds)
    {
        if ((_timerControl & 0x01) != 0)
        {
            // Timer 1 counts in 80 us steps from its preload value to 0xFF.
            _timer1Accumulator += microseconds / 80.0;
            if (_timer1Accumulator >= 256 - _timer1)
            {
                _timer1Accumulator = 0;
                if ((_timerControl & 0x40) == 0) _status |= 0xC0;   // bit 6 plus the summary bit
            }
        }

        if ((_timerControl & 0x02) != 0)
        {
            // Timer 2 counts in 320 us steps -- four times slower, so between
            // them they cover a usefully wide range with 8-bit preloads.
            _timer2Accumulator += microseconds / 320.0;
            if (_timer2Accumulator >= 256 - _timer2)
            {
                _timer2Accumulator = 0;
                if ((_timerControl & 0x20) == 0) _status |= 0xA0;   // bit 5 plus the summary bit
            }
        }
    }

    private double _timer1Accumulator;
    private double _timer2Accumulator;
}
