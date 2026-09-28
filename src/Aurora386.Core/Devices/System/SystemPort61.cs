using Aurora386.Core.IO;

using Aurora386.Core.Devices.Timing;
namespace Aurora386.Core.Devices.System;

/// <summary>
/// System port 0x61, inherited from the original PC's 8255 PPI.
///
/// It gathers together functions with nothing whatever in common:
///  - bits 0-1 on write: enable the speaker and gate it to timer channel 2;
///  - bit 4 on read: the DRAM refresh signal, which toggles continuously on
///    real hardware;
///  - bit 5 on read: the output of timer channel 2.
///
/// The refresh bit is the one that really matters to an emulator. DOS and a
/// great deal of period software use it as a fine-grained time base, spinning
/// in a loop until it changes state. Return a constant and that loop never
/// exits: the machine appears to hang solid, with no error and nothing in any
/// log to explain it.
/// </summary>
public sealed class SystemPort61 : IIoDevice
{
    private readonly Pit8253 _timer;

    /// <summary>The writable bits: speaker enable and the channel 2 gate.</summary>
    private byte _outputBits;

    /// <summary>
    /// Drives the refresh bit's oscillation. On real hardware the signal has
    /// a period of roughly 15 microseconds; here it is enough that it flips on
    /// every read, since software only ever uses it to notice that time is
    /// passing at all.
    /// </summary>
    private int _refreshCounter;

    public SystemPort61(Pit8253 timer) => _timer = timer;

    public void Register(IoBus io) => io.Register(0x61, this);

    /// <summary>True when the speaker is gated to the timer (both bits 0 and 1 set).</summary>
    public bool SpeakerEnabled => (_outputBits & 0x03) == 0x03;

    public byte ReadByte(ushort port)
    {
        // Bit 4 flips on every read, which is what lets those delay loops
        // terminate. Tying it to the read count rather than to real elapsed
        // time also keeps tests deterministic.
        _refreshCounter++;
        byte refreshBit = (byte)((_refreshCounter & 1) != 0 ? 0x10 : 0x00);

        // Bit 5 reports channel 2's output, which programs sample to time
        // short intervals by counting transitions.
        byte timerBit = (byte)(_timer.Channel2Output ? 0x20 : 0x00);

        return (byte)((_outputBits & 0x0F) | refreshBit | timerBit);
    }

    public void WriteByte(ushort port, byte value)
    {
        // Only the low four bits are writable; the rest are status inputs.
        _outputBits = (byte)(value & 0x0F);

        // Bit 0 is not merely a speaker switch: it is timer channel 2's GATE
        // input, and with it low the channel does not count at all. Every
        // routine that measures an interval closes the gate, loads a count,
        // opens it, and watches bit 5 -- so a gate that was ignored would let
        // the counter run before the measurement began, and the measurement
        // would be of nothing.
        _timer.Channel2Gate = (value & 0x01) != 0;
    }
}
