using System.IO;
using Aurora386.Core.IO;

using Aurora386.Core.Devices.Interrupts;
namespace Aurora386.Core.Devices.Timing;

/// <summary>
/// 8253/8254 programmable interval timer.
///
/// Three independent counters fed from a single 1,193,182 Hz clock -- one
/// third of the 3.579545 MHz NTSC colourburst crystal the original PC used,
/// chosen because that crystal was cheap and already on the board for the
/// composite video output. With the default divisor of 65536, channel 0 fires
/// 18.2 times a second, and that awkward number is the entire basis on which
/// DOS measures time.
///
/// Channel 0: drives IRQ 0, the system tick.
/// Channel 1: DRAM refresh request; no real need to emulate its effect.
/// Channel 2: the PC speaker.
/// </summary>
public sealed class Pit8253 : IIoDevice
{
    public const int ClockHz = 1_193_182;

    private sealed class Channel
    {
        public ushort ReloadValue = 0xFFFF;
        public ushort Counter;
        public byte AccessMode = 3;    // 3 = low byte then high byte
        public byte OperatingMode;
        public bool WaitingHighByte;
        public bool ReadingHighByte;
        public bool OutputHigh;
        public ushort LatchedValue;
        public bool Latched;

        /// <summary>
        /// Mode 0 only: set once the count has reached zero, which is when
        /// the output goes high. Cleared when a new count is loaded, because
        /// loading a count is what restarts the single shot.
        /// </summary>
        public bool Mode0Expired;

        /// <summary>Fractional cycles carried over, so rounding never loses time.</summary>
        public double Accumulator;
    }

    private readonly Channel[] _channels = [new(), new(), new()];
    private readonly InterruptController _interrupts;

    /// <summary>Channel 0 ticks since power-on, feeding the BIOS tick count.</summary>
    public long Channel0Ticks { get; private set; }

    public Pit8253(InterruptController interrupts) => _interrupts = interrupts;

    public void Register(IoBus io) => io.Register(0x40, 4, this);

    /// <summary>
    /// Advances the counters by the time that has elapsed. The caller hands
    /// us already-converted timer cycles, which is what keeps the emulated
    /// machine's sense of time tied to how fast the emulator is actually
    /// running rather than to the host wall clock.
    /// </summary>
    public void Advance(double pitCycles)
    {
        AdvanceChannel2(pitCycles);

        Channel channel0 = _channels[0];
        channel0.Accumulator += pitCycles;

        int whole = (int)channel0.Accumulator;
        if (whole <= 0)
        {
            return;
        }
        channel0.Accumulator -= whole;

        int reload = channel0.ReloadValue == 0 ? 65536 : channel0.ReloadValue;

        // Note that we raise the IRQ once per expiry but the controller
        // collapses repeats: if the emulator stalls and a large span of time
        // arrives at once, we must not deliver a burst of backlogged ticks.
        // Doing so would make the guest's clock sprint to catch up instead of
        // simply running slow, which is far more disruptive to software that
        // times things by counting ticks.
        while (whole > 0)
        {
            int step = Math.Min(whole, channel0.Counter == 0 ? reload : channel0.Counter);
            channel0.Counter = (ushort)(channel0.Counter - step);
            whole -= step;

            if (channel0.Counter == 0)
            {
                channel0.Counter = (ushort)reload;
                Channel0Ticks++;
                _interrupts.RaiseIrq(0);
            }
        }
    }

    public byte ReadByte(ushort port)
    {
        int index = port & 3;
        if (index == 3)
        {
            return 0;      // the control port is write-only
        }

        Channel channel = _channels[index];
        ushort value = channel.Latched ? channel.LatchedValue : channel.Counter;

        switch (channel.AccessMode)
        {
            case 1: return (byte)value;             // low byte only
            case 2: return (byte)(value >> 8);      // high byte only
            default:
                // Two-byte access: low byte first, then high. The channel
                // remembers which half is next, so an interleaved read from
                // another channel would not disturb it.
                if (!channel.ReadingHighByte)
                {
                    channel.ReadingHighByte = true;
                    return (byte)value;
                }
                channel.ReadingHighByte = false;
                channel.Latched = false;
                return (byte)(value >> 8);
        }
    }

    public void WriteByte(ushort port, byte value)
    {
        int index = port & 3;

        if (index == 3)
        {
            WriteControlWord(value);
            return;
        }

        Channel channel = _channels[index];

        switch (channel.AccessMode)
        {
            case 1:
                channel.ReloadValue = value;
                StartCounting(channel);
                return;
            case 2:
                channel.ReloadValue = (ushort)(value << 8);
                StartCounting(channel);
                return;
            default:
                if (!channel.WaitingHighByte)
                {
                    channel.ReloadValue = (ushort)((channel.ReloadValue & 0xFF00) | value);
                    channel.WaitingHighByte = true;
                }
                else
                {
                    channel.ReloadValue = (ushort)((channel.ReloadValue & 0x00FF) | (value << 8));
                    channel.WaitingHighByte = false;
                    StartCounting(channel);
                }
                return;
        }
    }

    private static void StartCounting(Channel channel)
    {
        channel.Counter = channel.ReloadValue;
        channel.Accumulator = 0;

        // Loading a count is what re-arms a mode 0 single shot: its output
        // drops low again and stays there until this new count expires.
        channel.Mode0Expired = false;
    }

    private void WriteControlWord(byte value)
    {
        int index = (value >> 6) & 3;
        if (index == 3)
        {
            return;        // read-back command, not emulated
        }

        Channel channel = _channels[index];
        byte access = (byte)((value >> 4) & 3);

        if (access == 0)
        {
            // Counter latch command: freezes a snapshot of the count so both
            // halves can be read consistently, while the counter itself keeps
            // running underneath.
            channel.LatchedValue = channel.Counter;
            channel.Latched = true;
            return;
        }

        channel.AccessMode = access;
        channel.OperatingMode = (byte)((value >> 1) & 7);
        channel.WaitingHighByte = false;
        channel.ReadingHighByte = false;
        channel.Latched = false;
    }

    /// <summary>
    /// Channel 2's GATE input, which on a PC is bit 0 of port 0x61.
    ///
    /// The other two channels have their gate wired permanently high; only
    /// channel 2's is under software control, and that is precisely what
    /// makes it the channel used for timing. The sequence every calibration
    /// routine uses is: close the gate, load a count, open the gate, and
    /// watch the output. With the gate closed the counter must NOT advance.
    ///
    /// Getting this wrong is invisible in ordinary use -- a delay loop still
    /// terminates, just at the wrong moment -- but it defeats any attempt to
    /// measure a known interval, because the counter has already been running
    /// before the measurement was supposed to start.
    /// </summary>
    public bool Channel2Gate { get; set; }

    /// <summary>
    /// Channel 2's output, visible as bit 5 of port 0x61. It is the signal
    /// that drives the PC speaker, and software samples it to time intervals
    /// far shorter than one 18.2 Hz system tick.
    /// </summary>
    public bool Channel2Output
    {
        get
        {
            Channel channel = _channels[2];

            // Mode 0 is the one a calibration routine chooses, because its
            // output is a single clean edge rather than a repeating wave:
            // the output stays LOW from the moment the count is loaded and
            // goes HIGH once, when the count reaches zero. Software times an
            // interval by waiting for that one transition.
            if (channel.OperatingMode == 0) return channel.Mode0Expired;

            int reload = channel.ReloadValue == 0 ? 65536 : channel.ReloadValue;
            // Square wave mode: the output is high for the first half of the
            // count and low for the second.
            return channel.Counter > reload / 2;
        }
    }

    /// <summary>
    /// Advances channel 2 as well. Unlike channel 0 it raises no interrupt,
    /// but it still has to count, because its output is directly observable
    /// through port 0x61 and delay loops depend on seeing it change.
    /// </summary>
    private void AdvanceChannel2(double pitCycles)
    {
        Channel channel = _channels[2];

        // A closed gate stops the count dead. This is the whole point of the
        // gate, and the reason channel 2 is the one used for measurement.
        if (!Channel2Gate) return;

        channel.Accumulator += pitCycles;

        int whole = (int)channel.Accumulator;
        if (whole <= 0)
        {
            return;
        }
        channel.Accumulator -= whole;

        int reload = channel.ReloadValue == 0 ? 65536 : channel.ReloadValue;

        if (channel.OperatingMode == 0)
        {
            // Mode 0 counts down once and stops, raising its output when it
            // reaches zero. It does not reload, so the counter saturates at
            // zero rather than wrapping: a wrap here would make the output
            // pulse repeatedly and a routine waiting for one edge would
            // measure whichever it happened to catch.
            int remaining = channel.Counter - whole;
            if (remaining <= 0)
            {
                channel.Counter = 0;
                channel.Mode0Expired = true;
            }
            else
            {
                channel.Counter = (ushort)remaining;
            }
            return;
        }

        int position = channel.Counter - (whole % reload);
        channel.Counter = (ushort)(position < 0 ? position + reload : position);
    }

    /// <summary>Channel 0's current output frequency, for diagnostics.</summary>
    public double Channel0Frequency
    {
        get
        {
            int reload = _channels[0].ReloadValue == 0 ? 65536 : _channels[0].ReloadValue;
            return (double)ClockHz / reload;
        }
    }

    /// <summary>
    /// The three channels' state, for a snapshot.
    ///
    /// Without it a restored machine's channel 0 comes back at its power-on
    /// divisor rather than the one the guest programmed, so time runs at the
    /// wrong speed -- and a guest that had reprogrammed it for a faster tick
    /// finds its clock suddenly slow.
    /// </summary>
    public byte[] Capture()
    {
        var buffer = new byte[3 * 16 + 8];
        var writer = new BinaryWriter(new MemoryStream(buffer));

        foreach (Channel channel in _channels)
        {
            writer.Write(channel.ReloadValue);
            writer.Write(channel.Counter);
            writer.Write(channel.AccessMode);
            writer.Write(channel.OperatingMode);
            writer.Write(channel.WaitingHighByte);
            writer.Write(channel.ReadingHighByte);
            writer.Write(channel.OutputHigh);
            writer.Write(channel.Latched);
            writer.Write(channel.LatchedValue);
            writer.Write((uint)0);              // padding, to a round 16
        }

        writer.Write(Channel0Ticks);
        return buffer;
    }

    /// <summary>Puts back what <see cref="Capture"/> took.</summary>
    public void Restore(ReadOnlySpan<byte> state)
    {
        if (state.Length < 3 * 16 + 8)
        {
            return;
        }

        var reader = new BinaryReader(new MemoryStream(state.ToArray()));

        foreach (Channel channel in _channels)
        {
            channel.ReloadValue = reader.ReadUInt16();
            channel.Counter = reader.ReadUInt16();
            channel.AccessMode = reader.ReadByte();
            channel.OperatingMode = reader.ReadByte();
            channel.WaitingHighByte = reader.ReadBoolean();
            channel.ReadingHighByte = reader.ReadBoolean();
            channel.OutputHigh = reader.ReadBoolean();
            channel.Latched = reader.ReadBoolean();
            channel.LatchedValue = reader.ReadUInt16();
            reader.ReadUInt32();
        }

        Channel0Ticks = reader.ReadInt64();
    }
}
