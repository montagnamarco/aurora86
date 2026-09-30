using Aurora86.Core.IO;

namespace Aurora86.Core.Devices.Audio;

/// <summary>
/// CT1745 mixer, the analogue front end of the Sound Blaster 16.
///
/// Everything on the card that makes a noise -- the digital (DAC) channel, the
/// FM synthesiser, the CD audio input, the line and microphone inputs -- meets
/// at this chip, which sets each one's level and sums them into the output.
/// On the real card these are genuine analogue attenuators; the register is a
/// digital potentiometer setting, not a multiplier applied to sample data.
///
/// The interface is the classic index/data pair: write a register number to
/// 0x224, then read or write its contents at 0x225. Two ports rather than
/// thirty-two, because ISA port space was scarce and a card was expected to
/// claim only a small contiguous block.
///
/// The registers matter beyond volume. 0x80 and 0x81 report the IRQ and DMA
/// channels the card is jumpered (later, software-configured) for, and this is
/// how a driver discovers its own hardware settings instead of having the user
/// type them in. Register 0x82 tells an interrupt handler which of the several
/// possible sources -- 8-bit DMA, 16-bit DMA or MIDI -- actually fired, which
/// is essential once one IRQ line serves all three.
/// </summary>
public sealed partial class SbMixer : IIoDevice
{
    private readonly byte[] _registers = new byte[256];
    private byte _index;

    /// <summary>The IRQ line the card reports in mixer register 0x80.</summary>
    public int Irq { get; set; } = 5;

    /// <summary>The 8-bit DMA channel reported in register 0x81.</summary>
    public int Dma8 { get; set; } = 1;

    /// <summary>The 16-bit DMA channel reported in register 0x81.</summary>
    public int Dma16 { get; set; } = 5;

    /// <summary>
    /// Interrupt status bits presented in register 0x82, set by the DSP when
    /// it raises an IRQ and cleared when the source is acknowledged.
    /// </summary>
    public byte InterruptStatus { get; set; }

    public SbMixer() => Reset();

    public void Register(IoBus io) => io.Register(0x224, 2, this);

    /// <summary>
    /// Restores the power-on defaults. Note that these are not zero: a card
    /// that came up silent would look broken to anyone who did not know to
    /// turn it up, so the master and voice levels default to near maximum.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_registers);

        // The 4-bit levels live in the high nibble, which is why the natural
        // "full volume" byte is 0xF8 rather than 0x0F: bits 3-7 hold the
        // level, the low bits are unimplemented and read back as zero.
        _registers[0x04] = 0xCC;   // voice (DAC) level, left and right nibbles
        _registers[0x22] = 0xCC;   // master level
        _registers[0x26] = 0xCC;   // FM level
        _registers[0x28] = 0x00;   // CD audio, off by default
        _registers[0x2E] = 0x00;   // line in, off by default

        _registers[0x30] = 0xC0;   // master left
        _registers[0x31] = 0xC0;   // master right
        _registers[0x32] = 0xC0;   // voice left
        _registers[0x33] = 0xC0;   // voice right
        _registers[0x34] = 0xC0;   // FM left
        _registers[0x35] = 0xC0;   // FM right
        _registers[0x36] = 0x00;   // CD left
        _registers[0x37] = 0x00;   // CD right
        _registers[0x38] = 0x00;   // line left
        _registers[0x39] = 0x00;   // line right
        _registers[0x3A] = 0x00;   // microphone
        _registers[0x3B] = 0x00;   // PC speaker
        _registers[0x3C] = 0x1F;   // output switches: everything routed to the output
        _registers[0x3D] = 0x15;   // input switches, left
        _registers[0x3E] = 0x0B;   // input switches, right
        _registers[0x3F] = 0x00;   // input gain
        _registers[0x40] = 0x00;
        _registers[0x41] = 0x00;   // output gain
        _registers[0x42] = 0x00;
        _registers[0x43] = 0x00;   // automatic gain control
        _registers[0x44] = 0x80;   // treble
        _registers[0x45] = 0x80;
        _registers[0x46] = 0x80;   // bass
        _registers[0x47] = 0x80;
    }

    public byte ReadByte(ushort port)
    {
        if ((port & 1) == 0) return _index;

        switch (_index)
        {
            case 0x80:
                // IRQ select, one bit per supported line. The card cannot be
                // set to arbitrary IRQs: only 2, 5, 7 and 10 are wired.
                return Irq switch
                {
                    2 => 0x01,
                    5 => 0x02,
                    7 => 0x04,
                    10 => 0x08,
                    _ => 0x02,
                };

            case 0x81:
                // DMA select, again one bit per channel: bits 0-2 are the
                // 8-bit channels 0, 1 and 3 (channel 2 belongs to the floppy
                // controller and was never offered), bits 5-7 the 16-bit
                // channels 5, 6 and 7.
                return (byte)(DmaMaskFor8Bit(Dma8) | DmaMaskFor16Bit(Dma16));

            case 0x82:
                // Interrupt status. The top bits read as set on a genuine
                // CT1745, and detection code checks for that.
                return (byte)(InterruptStatus | 0x00);

            default:
                return _registers[_index];
        }
    }

    private static byte DmaMaskFor8Bit(int channel) => channel switch
    {
        0 => 0x01,
        1 => 0x02,
        3 => 0x08,
        _ => 0x02,
    };

    private static byte DmaMaskFor16Bit(int channel) => channel switch
    {
        5 => 0x20,
        6 => 0x40,
        7 => 0x80,
        _ => 0x20,
    };

    public void WriteByte(ushort port, byte value)
    {
        if ((port & 1) == 0)
        {
            _index = value;
            return;
        }

        // Register 0x00 is the mixer reset: any write to it, regardless of
        // value, restores the defaults. It is write-only and reads back as 0.
        if (_index == 0x00)
        {
            Reset();
            return;
        }

        _registers[_index] = value;

        // The SB16 keeps the older SB Pro registers as aliases of the new
        // per-channel ones, so a program written for either generation sets
        // the same attenuators. Writing the Pro register must therefore update
        // both halves of the corresponding stereo pair, or a Pro-era game
        // would appear to set the volume and hear nothing change.
        switch (_index)
        {
            case 0x04: SplitProStereo(value, 0x32, 0x33); break;   // voice
            case 0x22: SplitProStereo(value, 0x30, 0x31); break;   // master
            case 0x26: SplitProStereo(value, 0x34, 0x35); break;   // FM
            case 0x28: SplitProStereo(value, 0x36, 0x37); break;   // CD
            case 0x2E: SplitProStereo(value, 0x38, 0x39); break;   // line in
        }
    }

    /// <summary>
    /// Expands a SB Pro register -- two 4-bit levels packed into one byte --
    /// into the SB16's pair of 5-bit registers, which hold their level in bits
    /// 3-7.
    /// </summary>
    private void SplitProStereo(byte value, int leftRegister, int rightRegister)
    {
        _registers[leftRegister] = (byte)((value & 0xF0) | 0x08);
        _registers[rightRegister] = (byte)(((value & 0x0F) << 4) | 0x08);
    }

    /// <summary>Reads a mixer register directly, bypassing the index port.</summary>
    public byte GetRegister(int register) => _registers[register & 0xFF];

    /// <summary>Writes the whole register file and the index, for the card's own snapshot.</summary>
    internal void Capture(BinaryWriter writer)
    {
        writer.Write(_registers);
        writer.Write(_index);
        writer.Write(InterruptStatus);
    }

    /// <summary>Restores what <see cref="Capture"/> wrote.</summary>
    internal void Restore(BinaryReader reader)
    {
        reader.ReadBytes(_registers.Length).CopyTo(_registers, 0);
        _index = reader.ReadByte();
        InterruptStatus = reader.ReadByte();
    }

    /// <summary>
    /// Master output level as a 0..1 gain, averaged across the two channels.
    /// The caller applies it when mixing, since we have no analogue path.
    /// </summary>
    public float MasterLevel => AverageLevel(0x30, 0x31);

    /// <summary>Digital (DAC) channel level as a 0..1 gain.</summary>
    public float VoiceLevel => AverageLevel(0x32, 0x33);

    /// <summary>FM synthesiser level as a 0..1 gain.</summary>
    public float FmLevel => AverageLevel(0x34, 0x35);

    private float AverageLevel(int leftRegister, int rightRegister)
    {
        // Only bits 3-7 are implemented: five bits of level, 0 to 31.
        int left = _registers[leftRegister] >> 3;
        int right = _registers[rightRegister] >> 3;
        return (left + right) / (2f * 31f);
    }
}
