using Aurora386.Core.IO;

namespace Aurora386.Core.Devices.Timing;

/// <summary>
/// Real-time clock and configuration memory (MC146818).
///
/// Holds the date and time as BCD digits plus about a hundred bytes that
/// describe the machine: how much memory is fitted, which disks are
/// installed, what kind of display adapter. The BIOS reads them at power-on,
/// and they are the only way DOS ever learns that the machine has 16 MB
/// rather than 640 KB. The chip is battery-backed on real hardware, which is
/// why these settings survive a power cycle while everything else does not.
/// </summary>
public sealed class Cmos : IIoDevice
{
    private readonly byte[] _registers = new byte[128];
    private byte _selectedRegister;

    /// <summary>Time source backing the clock. Injectable so tests are reproducible.</summary>
    public Func<DateTime> TimeSource { get; set; } = () => DateTime.Now;

    public Cmos(uint totalMemoryBytes)
    {
        ConfigureMemory(totalMemoryBytes);

        // Status register B: 24-hour format, BCD digits.
        _registers[0x0B] = 0x02;

        // Diagnostic register: no faults detected on the previous boot.
        _registers[0x0E] = 0x00;

        // Equipment byte: floppy drive present, math coprocessor installed.
        _registers[0x14] = 0x27;

        // Floppy type byte: 0x40 means a single 1.44 MB drive as A:.
        _registers[0x10] = 0x40;

        // Hard disk type: 0xF0 means "see the extended registers", since the
        // classic type numbers cannot describe a modern geometry.
        _registers[0x12] = 0xF0;
    }

    /// <summary>Publishes installed diskette drives, independent of inserted media.</summary>
    public void ConfigureFloppyDrives(byte firstBiosType, byte secondBiosType)
    {
        // The 2.88 MB drive is type 6 in INT 13h but type 5 in CMOS.
        byte firstCmosType = firstBiosType == 6 ? (byte)5 : firstBiosType;
        byte secondCmosType = secondBiosType == 6 ? (byte)5 : secondBiosType;
        _registers[0x10] = (byte)((firstCmosType << 4) | secondCmosType);
        int count = (firstBiosType != 0 ? 1 : 0) + (secondBiosType != 0 ? 1 : 0);
        _registers[0x14] &= 0x3E;
        if (count != 0)
        {
            _registers[0x14] |= (byte)(1 | ((count - 1) << 6));
        }
    }

    private void ConfigureMemory(uint totalBytes)
    {
        // Conventional memory: always 640 KB. That is the architectural
        // ceiling, because the video framebuffer starts right above it.
        const ushort conventionalKb = 640;
        _registers[0x15] = unchecked((byte)conventionalKb);
        _registers[0x16] = (byte)(conventionalKb >> 8);

        // Extended memory above the first megabyte, in KB. The pair of
        // registers is only 16 bits wide and saturates at 65535 KB (64 MB);
        // past that the BIOS has to report memory some other way, but with
        // 16 MB installed we never get near the ceiling.
        uint extendedKb = totalBytes > 0x100000 ? (totalBytes - 0x100000) / 1024 : 0;
        ushort capped = (ushort)Math.Min(extendedKb, 0xFFFF);
        _registers[0x17] = (byte)capped;
        _registers[0x18] = (byte)(capped >> 8);

        // Mirror into the alias registers that some BIOSes read instead of
        // the pair above.
        _registers[0x30] = _registers[0x17];
        _registers[0x31] = _registers[0x18];

        // Memory above 16 MB, in 64 KB units, in the pair the AT BIOSes
        // settled on when the 16-bit count above ran out at 64 MB. A guest
        // that reads the CMOS directly -- some memory managers do -- finds
        // the rest of a 128 MB machine here; everything else asks INT 15h
        // E801h, which says the same thing.
        uint above16Mb = totalBytes > 0x1000000 ? (totalBytes - 0x1000000) / 65536 : 0;
        ushort blocks = (ushort)Math.Min(above16Mb, 0xFFFF);
        _registers[0x34] = (byte)blocks;
        _registers[0x35] = (byte)(blocks >> 8);
    }

    public void Register(IoBus io) => io.Register(0x70, 2, this);

    public byte ReadByte(ushort port)
    {
        // Port 0x70 is write-only: reading it is meaningless and returns
        // undefined values on real hardware.
        if ((port & 1) == 0) return 0xFF;

        return _selectedRegister switch
        {
            0x00 => ToBcd(TimeSource().Second),
            0x02 => ToBcd(TimeSource().Minute),
            0x04 => ToBcd(TimeSource().Hour),
            0x06 => ToBcd((int)TimeSource().DayOfWeek + 1),
            0x07 => ToBcd(TimeSource().Day),
            0x08 => ToBcd(TimeSource().Month),
            0x09 => ToBcd(TimeSource().Year % 100),
            0x32 => ToBcd(TimeSource().Year / 100),
            // Status register A: bit 7 flags an update in progress, during
            // which the time registers must not be read. We keep it clear, so
            // code that polls for a stable moment never has to wait.
            0x0A => 0x26,
            _ => _registers[_selectedRegister & 0x7F],
        };
    }

    public void WriteByte(ushort port, byte value)
    {
        if ((port & 1) == 0)
        {
            // Bit 7 of port 0x70 gates the NMI line; it is not part of the
            // register address, so mask it off before selecting.
            _selectedRegister = (byte)(value & 0x7F);
            return;
        }

        // Date and time are derived from the host clock, so writes to those
        // registers are accepted but discarded. Storing them would make the
        // guest believe it had changed the time when the next read would
        // silently contradict it.
        if (_selectedRegister <= 0x09 || _selectedRegister == 0x32) return;

        _registers[_selectedRegister & 0x7F] = value;
    }

    private static byte ToBcd(int value) => (byte)(((value / 10) << 4) | (value % 10));
}
