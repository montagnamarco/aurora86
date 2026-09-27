using Aurora386.Core.IO;
using Aurora386.Core.Memory;

namespace Aurora386.Core.Devices.System;

/// <summary>
/// 8237A programmable DMA controller, in the cascaded pair the AT fitted.
///
/// Why a sound card needs one at all: at 22 kHz stereo the CPU would have to
/// hand over a byte every 22 microseconds, forever, with jitter measured in
/// single-digit microseconds or the audio audibly tears. On a 25 MHz 386 that
/// is a few hundred cycles of budget per sample, and it has to be met while
/// the game is also drawing, reading the disk and running its own logic.
/// Direct memory access removes the CPU from the loop entirely: the program
/// tells the controller "here is a buffer, here is its length, feed it to the
/// card", and the DMA chip then arbitrates for the bus and moves the bytes on
/// its own. The CPU only hears about it again when the block is finished.
///
/// The layout is a historical accident worth understanding. The original PC
/// had one 8237, giving four channels, each of which can only count 65536
/// transfers and -- crucially -- only carries a 16-bit address. The XT bolted
/// on an external latch per channel, the "page register", supplying address
/// bits 16-23 so the chip could reach the full 1 MB. That latch is not part of
/// the 8237 and does not increment, which is exactly why a DMA buffer must
/// never straddle a 64 KB physical boundary: the low 16 bits wrap and the
/// transfer silently continues at the start of the same page.
///
/// The AT added a second 8237 for 16-bit transfers, cascaded through channel 4
/// of the first. Its channels count *words*, not bytes, so its address
/// register is shifted left by one and its page register only supplies bits
/// 17-23 -- giving a 128 KB alignment constraint instead of 64 KB.
/// </summary>
public sealed class Dma8237
{
    /// <summary>One of the eight DMA channels.</summary>
    public sealed class Channel
    {
        /// <summary>Base address as programmed, reloaded on auto-init.</summary>
        public ushort BaseAddress;

        /// <summary>Base count as programmed, reloaded on auto-init.</summary>
        public ushort BaseCount;

        /// <summary>Current address; incremented (or decremented) per transfer.</summary>
        public ushort CurrentAddress;

        /// <summary>
        /// Current count. The 8237 stores "transfers minus one", so a count of
        /// zero still means one transfer remains; the terminal count fires when
        /// it borrows past zero and wraps to 0xFFFF.
        /// </summary>
        public ushort CurrentCount;

        /// <summary>Address bits 16-23 (or 17-23 on the word-wide controller).</summary>
        public byte Page;

        /// <summary>The mode byte as written: transfer type, auto-init, direction.</summary>
        public byte Mode;

        /// <summary>True while the channel is masked, i.e. its request line is ignored.</summary>
        public bool Masked = true;

        /// <summary>Set when the channel reaches terminal count; cleared by reading the status register.</summary>
        public bool TerminalCount;

        /// <summary>Bits 2-3 of the mode byte: 0 verify, 1 write to memory, 2 read from memory.</summary>
        public int TransferType => (Mode >> 2) & 3;

        /// <summary>
        /// Bit 4: auto-initialisation. When set, hitting terminal count does
        /// not stop the channel -- it silently reloads address and count from
        /// the base registers and carries straight on. This is what makes
        /// gapless streaming audio possible: the program keeps one ring buffer
        /// permanently programmed, refills whichever half the card is not
        /// currently reading, and never has to reprogram the controller inside
        /// an interrupt handler where a few microseconds of delay would be an
        /// audible click.
        /// </summary>
        public bool AutoInit => (Mode & 0x10) != 0;

        /// <summary>Bit 5: address decrement rather than increment. Almost never used.</summary>
        public bool Decrement => (Mode & 0x20) != 0;
    }

    private readonly Channel[] _channels = new Channel[8];
    private readonly PhysicalMemoryBus? _memory;

    /// <summary>
    /// The flip-flop that sequences the two halves of every 16-bit register.
    /// The 8237's data bus is only 8 bits wide, so address and count are each
    /// written as low byte then high byte, and a single internal flip-flop --
    /// shared by *all* channels on that controller -- remembers which half is
    /// next. Software must clear it before programming a channel, or the two
    /// bytes land the wrong way round; that is the entire purpose of the
    /// otherwise mysterious "clear byte pointer" port.
    /// </summary>
    private bool _highByte8;
    private bool _highByte16;

    public Dma8237(PhysicalMemoryBus? memory = null)
    {
        for (int i = 0; i < _channels.Length; i++) _channels[i] = new Channel();
        _memory = memory;
    }

    public Channel this[int index] => _channels[index];

    public void Register(IoBus io)
    {
        var byteWide = new BytePorts(this);
        var wordWide = new WordPorts(this);
        var pages = new PagePorts(this);

        io.Register(0x00, 16, byteWide);   // controller 1: channels 0-3
        io.Register(0x80, 16, pages);      // page registers for both controllers
        io.Register(0xC0, 32, wordWide);   // controller 2: channels 4-7, word addressed
    }

    /// <summary>
    /// The full 24-bit physical address a channel is currently pointing at.
    ///
    /// For the 8-bit controller this is simply page:offset concatenated. For
    /// the 16-bit one the address register counts words, so it contributes
    /// bits 1-16 of the byte address and the page supplies bits 17-23 -- note
    /// that page bit 0 is discarded, not shifted in, which is the hardware
    /// reason 16-bit DMA buffers must be 128 KB aligned.
    /// </summary>
    public uint GetPhysicalAddress(int channel)
    {
        Channel c = _channels[channel];
        return channel < 4
            ? (uint)(c.Page << 16) | c.CurrentAddress
            : ((uint)(c.Page & 0xFE) << 16) | ((uint)c.CurrentAddress << 1);
    }

    /// <summary>True when the channel is unmasked and still has transfers left to run.</summary>
    public bool IsActive(int channel)
    {
        Channel c = _channels[channel];
        return !c.Masked && c.CurrentCount != 0xFFFF;
    }

    /// <summary>
    /// Performs one transfer on a channel that reads from memory, which is the
    /// direction a sound card uses: samples flow memory -> device. Returns the
    /// byte read, or null when the channel is masked or exhausted.
    ///
    /// A real 8237 would only do this when the device asserted DREQ; here the
    /// device calls in when it needs a sample, which comes to the same thing
    /// and avoids modelling bus arbitration we cannot observe.
    /// </summary>
    public byte? TransferByte(int channel)
    {
        Channel c = _channels[channel];
        if (c.Masked || _memory is null)
        {
            return null;
        }

        byte value = _memory.ReadByte(GetPhysicalAddress(channel));
        Step(c, channel);
        return value;
    }

    /// <summary>
    /// One transfer on a 16-bit channel. Note that a single transfer moves a
    /// whole word here -- the count register counts words, so a 16-bit stereo
    /// buffer of N samples is programmed with a count of N-1, not 2N-1.
    /// </summary>
    public ushort? TransferWord(int channel)
    {
        Channel c = _channels[channel];
        if (c.Masked || _memory is null)
        {
            return null;
        }

        ushort value = _memory.ReadWord(GetPhysicalAddress(channel));
        Step(c, channel);
        return value;
    }

    private void Step(Channel c, int channel)
    {
        c.CurrentAddress = (ushort)(c.Decrement ? c.CurrentAddress - 1 : c.CurrentAddress + 1);
        c.CurrentCount--;

        // Terminal count is the borrow out of the count register: it wraps
        // from 0 to 0xFFFF. That off-by-one is why the programmed count is
        // always "length - 1".
        if (c.CurrentCount == 0xFFFF)
        {
            c.TerminalCount = true;
            if (c.AutoInit)
            {
                c.CurrentAddress = c.BaseAddress;
                c.CurrentCount = c.BaseCount;
            }
            else
            {
                // Without auto-init the controller masks the channel itself,
                // so nothing further happens until software reprograms it.
                c.Masked = true;
            }
        }
    }

    /// <summary>Consumes the terminal-count flag for a channel, returning whether it was set.</summary>
    public bool ConsumeTerminalCount(int channel)
    {
        Channel c = _channels[channel];
        bool value = c.TerminalCount;
        c.TerminalCount = false;
        return value;
    }

    // --- snapshot --------------------------------------------------------

    /// <summary>The version of the payload <see cref="Capture"/> produces today.</summary>
    public const int SnapshotVersion = 1;

    /// <summary>
    /// The fixed size of a version-1 payload: eight channels, each its base
    /// and current address, base and current count, page, mode, masked and
    /// terminal-count flags, then the two controllers' own byte-pointer
    /// flip-flops.
    /// </summary>
    public const int SnapshotLength = 8 * (2 + 2 + 2 + 2 + 1 + 1 + 1 + 1) + 1 + 1;

    /// <summary>
    /// Captures every channel on both controllers -- a card mid-transfer
    /// depends on the address and count registers matching exactly where it
    /// left off, not merely on being masked or unmasked -- and the two
    /// byte-pointer flip-flops, so a driver that has written only the low
    /// half of a register when the snapshot is taken does not have its next
    /// write land in the wrong half after a restore.
    /// </summary>
    public byte[] Capture()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        foreach (Channel c in _channels)
        {
            writer.Write(c.BaseAddress);
            writer.Write(c.BaseCount);
            writer.Write(c.CurrentAddress);
            writer.Write(c.CurrentCount);
            writer.Write(c.Page);
            writer.Write(c.Mode);
            writer.Write(c.Masked);
            writer.Write(c.TerminalCount);
        }

        writer.Write(_highByte8);
        writer.Write(_highByte16);

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Restores what <see cref="Capture"/> wrote.</summary>
    public void Restore(byte[] payload)
    {
        using var reader = new BinaryReader(new MemoryStream(payload));

        foreach (Channel c in _channels)
        {
            c.BaseAddress = reader.ReadUInt16();
            c.BaseCount = reader.ReadUInt16();
            c.CurrentAddress = reader.ReadUInt16();
            c.CurrentCount = reader.ReadUInt16();
            c.Page = reader.ReadByte();
            c.Mode = reader.ReadByte();
            c.Masked = reader.ReadBoolean();
            c.TerminalCount = reader.ReadBoolean();
        }

        _highByte8 = reader.ReadBoolean();
        _highByte16 = reader.ReadBoolean();
    }

    // --- port decoding ---------------------------------------------------------

    private void WriteChannelRegister(int channel, bool isCount, byte value, ref bool flipFlop)
    {
        Channel c = _channels[channel];

        if (!flipFlop)
        {
            if (isCount)
            {
                c.BaseCount = (ushort)((c.BaseCount & 0xFF00) | value);
            }
            else c.BaseAddress = (ushort)((c.BaseAddress & 0xFF00) | value);
        }
        else
        {
            if (isCount)
            {
                c.BaseCount = (ushort)((c.BaseCount & 0x00FF) | (value << 8));
            }
            else c.BaseAddress = (ushort)((c.BaseAddress & 0x00FF) | (value << 8));
        }

        flipFlop = !flipFlop;

        // Writing either half updates the current register too: the base
        // registers exist only so auto-init has something to reload from.
        if (isCount)
        {
            c.CurrentCount = c.BaseCount;
        }
        else c.CurrentAddress = c.BaseAddress;
    }

    private byte ReadChannelRegister(int channel, bool isCount, ref bool flipFlop)
    {
        Channel c = _channels[channel];
        ushort value = isCount ? c.CurrentCount : c.CurrentAddress;
        byte result = flipFlop ? (byte)(value >> 8) : (byte)value;
        flipFlop = !flipFlop;
        return result;
    }

    private void WriteCommandRegister(int firstChannel, byte port, byte value, ref bool flipFlop)
    {
        switch (port)
        {
            case 0x8:   // command register: DMA enable, timing, priority. Nothing observable here.
                return;

            case 0x9:   // software DREQ: request a transfer without a device asking
                return;

            case 0xA:   // single-channel mask: bit 2 sets, clears the mask for the channel in bits 0-1
                _channels[firstChannel + (value & 3)].Masked = (value & 0x04) != 0;
                return;

            case 0xB:   // mode register: the channel is named in bits 0-1
            {
                Channel c = _channels[firstChannel + (value & 3)];
                c.Mode = value;
                return;
            }

            case 0xC:   // clear the byte pointer flip-flop
                flipFlop = false;
                return;

            case 0xD:   // master clear: a full reset, equivalent to power-on
                for (int i = 0; i < 4; i++)
                {
                    Channel c = _channels[firstChannel + i];
                    c.Masked = true;
                    c.TerminalCount = false;
                    c.CurrentCount = 0;
                    c.CurrentAddress = 0;
                }
                flipFlop = false;
                return;

            case 0xE:   // clear all masks
                for (int i = 0; i < 4; i++) _channels[firstChannel + i].Masked = false;
                return;

            case 0xF:   // write all four mask bits at once
                for (int i = 0; i < 4; i++)
                    _channels[firstChannel + i].Masked = (value & (1 << i)) != 0;
                return;
        }
    }

    private byte ReadStatusRegister(int firstChannel)
    {
        // Low nibble: channels that have reached terminal count. High nibble:
        // channels with a request pending. Reading is destructive -- the TC
        // bits clear -- so a driver gets one chance to see each event.
        byte status = 0;
        for (int i = 0; i < 4; i++)
        {
            Channel c = _channels[firstChannel + i];
            if (c.TerminalCount)
            {
                status |= (byte)(1 << i);
            }
            if (!c.Masked)
            {
                status |= (byte)(0x10 << i);
            }
            c.TerminalCount = false;
        }
        return status;
    }

    /// <summary>Ports 0x00-0x0F: the byte-wide controller serving channels 0-3.</summary>
    private sealed class BytePorts(Dma8237 owner) : IIoDevice
    {
        public byte ReadByte(ushort port)
        {
            int register = port & 0x0F;
            if (register < 8)
                return owner.ReadChannelRegister(register >> 1, (register & 1) != 0, ref owner._highByte8);
            if (register == 0x8)
            {
                return owner.ReadStatusRegister(0);
            }
            return 0xFF;
        }

        public void WriteByte(ushort port, byte value)
        {
            int register = port & 0x0F;
            if (register < 8)
                owner.WriteChannelRegister(register >> 1, (register & 1) != 0, value, ref owner._highByte8);
            else
                owner.WriteCommandRegister(0, (byte)register, value, ref owner._highByte8);
        }
    }

    /// <summary>
    /// Ports 0xC0-0xDF: the word-wide controller serving channels 4-7. Its
    /// registers are spaced two ports apart rather than one, because the AT
    /// decoded them on the 16-bit bus where consecutive addresses are the two
    /// halves of the same word.
    /// </summary>
    private sealed class WordPorts(Dma8237 owner) : IIoDevice
    {
        public byte ReadByte(ushort port)
        {
            int register = (port - 0xC0) >> 1;
            if (register < 8)
                return owner.ReadChannelRegister(4 + (register >> 1), (register & 1) != 0, ref owner._highByte16);
            if (register == 0x8)
            {
                return owner.ReadStatusRegister(4);
            }
            return 0xFF;
        }

        public void WriteByte(ushort port, byte value)
        {
            int register = (port - 0xC0) >> 1;
            if (register < 8)
                owner.WriteChannelRegister(4 + (register >> 1), (register & 1) != 0, value, ref owner._highByte16);
            else
                owner.WriteCommandRegister(4, (byte)register, value, ref owner._highByte16);
        }
    }

    /// <summary>
    /// Ports 0x80-0x8F: the page registers. These are not part of either 8237
    /// -- they are a separate latch chip -- and their port assignment follows
    /// no pattern whatsoever, being simply whichever addresses IBM had spare.
    /// Channel 4's slot at 0x8F is unused because that channel is the cascade.
    /// </summary>
    private sealed class PagePorts(Dma8237 owner) : IIoDevice
    {
        private static readonly int[] ChannelForPort =
        [
            -1,  2,  3,  1, -1, -1, -1,  0,     // 0x80-0x87
            -1,  6,  7,  5, -1, -1, -1,  4,     // 0x88-0x8F
        ];

        public byte ReadByte(ushort port)
        {
            int channel = ChannelForPort[port & 0x0F];
            return channel < 0 ? (byte)0xFF : owner._channels[channel].Page;
        }

        public void WriteByte(ushort port, byte value)
        {
            int channel = ChannelForPort[port & 0x0F];
            if (channel >= 0)
            {
                owner._channels[channel].Page = value;
            }
        }
    }
}
