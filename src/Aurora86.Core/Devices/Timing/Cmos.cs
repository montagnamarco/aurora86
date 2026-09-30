using Aurora86.Core.Devices.Interrupts;
using Aurora86.Core.IO;

namespace Aurora86.Core.Devices.Timing;

/// <summary>
/// Real-time clock and configuration memory (MC146818).
///
/// Holds the date and time as BCD digits plus about a hundred bytes that
/// describe the machine: how much memory is fitted, which disks are
/// installed, what kind of display adapter. The BIOS reads them at power-on,
/// and they are the only way DOS ever learns that the machine has 16 MB
/// rather than 640 KB. The chip is battery-backed on real hardware, which is
/// why these settings survive a power cycle while everything else does not.
///
/// It also interrupts, on IRQ 8, for three reasons a program chooses in
/// status register B: a PERIODIC interrupt at a rate from 2 Hz to 8192 Hz set
/// in register A (what DOS programs used for a fast timer that left the
/// 8253 alone, and what Windows NT and Linux use as a profiling clock), an
/// UPDATE-ENDED interrupt once a second, and an ALARM when the time matches
/// registers 01h, 03h and 05h. Each sets its flag in register C whether or
/// not it is enabled; an enabled one also sets IRQF (bit 7) and raises the
/// line. READING register C clears every flag and lowers the line, which is
/// how a handler acknowledges the chip -- one that forgets never hears from
/// it again. Until 29 September 2026 none of this existed and register C was
/// plain storage; found when Aurora OS's first driver in user space waited
/// for line 8 and nothing came.
/// </summary>
public sealed class Cmos : IIoDevice
{
    private readonly byte[] _registers = new byte[128];
    private byte _selectedRegister;

    // The registers the interrupts live in, and their bits (MC146818A data sheet).
    private const int SecondsAlarmRegister = 0x01;
    private const int MinutesAlarmRegister = 0x03;
    private const int HoursAlarmRegister = 0x05;
    private const int StatusA = 0x0A;
    private const int StatusB = 0x0B;
    private const int StatusC = 0x0C;
    private const int StatusD = 0x0D;

    private const byte UpdateInProgress = 0x80;   // A bit 7: read-only
    private const byte DividerMask = 0x70;        // A bits 4-6: DV2-DV0
    private const byte DividerRunning = 0x20;     // DV = 010, the 32.768 kHz time base
    private const byte RateMask = 0x0F;           // A bits 0-3: RS3-RS0
    private const byte SetBit = 0x80;             // B bit 7: updates stopped
    private const byte PeriodicEnable = 0x40;     // B bit 6: PIE
    private const byte AlarmEnable = 0x20;        // B bit 5: AIE
    private const byte UpdateEndedEnable = 0x10;  // B bit 4: UIE
    private const byte InterruptRequest = 0x80;   // C bit 7: IRQF
    private const byte PeriodicFlag = 0x40;       // C bit 6: PF
    private const byte AlarmFlag = 0x20;          // C bit 5: AF
    private const byte UpdateEndedFlag = 0x10;    // C bit 4: UF
    private const byte ValidRamAndTime = 0x80;    // D bit 7: VRT, the battery is good

    /// <summary>An alarm register at or above this matches every value ("don't care").</summary>
    private const byte AlarmDontCare = 0xC0;

    /// <summary>The chip's interrupt line on the AT: IRQ 8, the first of the second 8259.</summary>
    public const int Irq = 8;

    private const double TimeBaseHz = 32768.0;
    private const double MicrosecondsPerSecond = 1_000_000.0;

    private readonly InterruptController? _interrupts;
    private double _periodicElapsedMicroseconds;
    private double _updateElapsedMicroseconds;
    private bool _lineRaised;

    /// <summary>Time source backing the clock. Injectable so tests are reproducible.</summary>
    public Func<DateTime> TimeSource { get; set; } = () => DateTime.Now;

    public Cmos(uint totalMemoryBytes, InterruptController? interrupts = null)
    {
        _interrupts = interrupts;
        ConfigureMemory(totalMemoryBytes);

        // Status register A as the BIOS leaves it: the 32.768 kHz time base
        // running (DV = 010) and the periodic rate at 1024 Hz (RS = 0110).
        _registers[StatusA] = 0x26;

        // Status register D: the battery is good, the time can be trusted.
        _registers[StatusD] = ValidRamAndTime;

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
        if ((port & 1) == 0)
        {
            return 0xFF;
        }

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
            // code that polls for a stable moment never has to wait; the rest
            // is what the guest wrote.
            StatusA => (byte)(_registers[StatusA] & ~UpdateInProgress),
            StatusC => ReadAndClearFlags(),
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
        // silently contradict it. The three ALARM registers among them are
        // the exception: they are the guest's, and the alarm compares them.
        bool alarm = _selectedRegister is SecondsAlarmRegister or MinutesAlarmRegister
                                          or HoursAlarmRegister;

        if ((_selectedRegister <= 0x09 && !alarm) || _selectedRegister == 0x32)
        {
            return;
        }

        switch (_selectedRegister)
        {
            // Registers C and D are read-only on the chip.
            case StatusC:
            case StatusD:
                return;

            case StatusA:
                _registers[StatusA] = (byte)(value & ~UpdateInProgress);
                return;

            case StatusB:
                // Setting SET stops the updates and, by the data sheet, clears
                // UIE with it. Enabling an interrupt whose flag is already up
                // raises the line at once: IRQF follows flag AND enable.
                _registers[StatusB] = (value & SetBit) != 0
                    ? (byte)(value & ~UpdateEndedEnable)
                    : value;
                UpdateInterruptLine();
                return;
        }

        _registers[_selectedRegister & 0x7F] = value;
    }

    /// <summary>
    /// Advances the chip's time base by emulated time: the periodic
    /// interrupt at the rate register A selects, and the once-a-second
    /// update that ends with the update-ended and alarm checks. Nothing runs
    /// unless the divider is at DV = 010, which is how software stops the
    /// clock.
    /// </summary>
    public void Advance(double microseconds)
    {
        if ((_registers[StatusA] & DividerMask) != DividerRunning)
        {
            return;
        }

        bool raised = false;

        double period = PeriodicPeriodMicroseconds();

        if (period > 0)
        {
            _periodicElapsedMicroseconds += microseconds;

            if (_periodicElapsedMicroseconds >= period)
            {
                // Several periods in one step still set one flag: the chip has
                // one PF bit, and a handler that was late sees it once.
                _periodicElapsedMicroseconds %= period;
                _registers[StatusC] |= PeriodicFlag;
                raised = true;
            }
        }
        else
        {
            _periodicElapsedMicroseconds = 0;
        }

        _updateElapsedMicroseconds += microseconds;

        if (_updateElapsedMicroseconds >= MicrosecondsPerSecond)
        {
            _updateElapsedMicroseconds %= MicrosecondsPerSecond;

            if ((_registers[StatusB] & SetBit) == 0)
            {
                _registers[StatusC] |= UpdateEndedFlag;

                if (AlarmMatches())
                {
                    _registers[StatusC] |= AlarmFlag;
                }

                raised = true;
            }
        }

        if (raised)
        {
            UpdateInterruptLine();
        }
    }

    /// <summary>
    /// The chip's RESET pin, which the AT ties to the system reset: the three
    /// interrupt enables and every flag are cleared, the line goes down. The
    /// time, the alarm and the configuration survive -- they are battery-backed.
    /// </summary>
    public void Reset()
    {
        _registers[StatusB] &= unchecked((byte)~(PeriodicEnable | AlarmEnable | UpdateEndedEnable));
        _registers[StatusC] = 0;
        UpdateInterruptLine();
    }

    /// <summary>
    /// The periodic interrupt's period for register A's rate select, or zero
    /// for none. RS = 0 is off; 1 and 2 repeat 8 and 9 (256 and 128 Hz), a
    /// quirk of the divider chain the data sheet's table keeps; from 3 the rate
    /// halves each step, 8192 Hz down to 2 Hz at 15.
    /// </summary>
    private double PeriodicPeriodMicroseconds()
    {
        int rate = _registers[StatusA] & RateMask;

        if (rate == 0)
        {
            return 0;
        }

        if (rate <= 2)
        {
            rate += 7;
        }

        double hertz = TimeBaseHz / (1 << (rate - 1));

        return MicrosecondsPerSecond / hertz;
    }

    private bool AlarmMatches()
    {
        DateTime now = TimeSource();

        return AlarmFieldMatches(_registers[SecondsAlarmRegister], ToBcd(now.Second))
            && AlarmFieldMatches(_registers[MinutesAlarmRegister], ToBcd(now.Minute))
            && AlarmFieldMatches(_registers[HoursAlarmRegister], ToBcd(now.Hour));
    }

    private static bool AlarmFieldMatches(byte alarm, byte current) =>
        alarm >= AlarmDontCare || alarm == current;

    /// <summary>
    /// Register C as read: the flags, with IRQF when any enabled one is up --
    /// and then all of them cleared and the line lowered, because reading is
    /// the acknowledgement.
    /// </summary>
    private byte ReadAndClearFlags()
    {
        byte flags = (byte)(_registers[StatusC] & (PeriodicFlag | AlarmFlag | UpdateEndedFlag));

        if (InterruptPending())
        {
            flags |= InterruptRequest;
        }

        _registers[StatusC] = 0;
        UpdateInterruptLine();

        return flags;
    }

    private bool InterruptPending()
    {
        byte enabled = (byte)(_registers[StatusB] & (PeriodicEnable | AlarmEnable | UpdateEndedEnable));

        // The enable bits sit exactly over the flags they enable: B bit 6 over
        // C bit 6, and so on.
        return (_registers[StatusC] & enabled) != 0;
    }

    /// <summary>IRQ 8 follows IRQF: up while an enabled flag is up, down once C is read.</summary>
    private void UpdateInterruptLine()
    {
        bool pending = InterruptPending();

        if (pending == _lineRaised)
        {
            return;
        }

        _lineRaised = pending;

        if (pending)
        {
            _interrupts?.RaiseIrq(Irq);
        }
        else
        {
            _interrupts?.LowerIrq(Irq);
        }
    }

    private static byte ToBcd(int value) => (byte)(((value / 10) << 4) | (value % 10));
}
