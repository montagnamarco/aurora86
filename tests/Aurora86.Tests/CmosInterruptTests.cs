using Aurora86.Core.Devices.Interrupts;
using Aurora86.Core.Devices.Timing;
using Aurora86.Core.IO;
using Xunit;

namespace Aurora86.Tests;

/// <summary>
/// The MC146818's interrupts on IRQ 8, driven through ports 70h and 71h as a
/// driver does: register A picks the periodic rate, register B enables the
/// periodic (PIE, bit 6), alarm (AIE, bit 5) and update-ended (UIE, bit 4)
/// interrupts, and register C reports the flags -- IRQF bit 7, PF bit 6, AF
/// bit 5, UF bit 4 -- and clears them all, lowering the line, when read.
/// </summary>
public class CmosInterruptTests
{
    private const ushort IndexPort = 0x70;
    private const ushort DataPort = 0x71;

    private const byte StatusA = 0x0A;
    private const byte StatusB = 0x0B;
    private const byte StatusC = 0x0C;
    private const byte StatusD = 0x0D;

    private const byte PeriodicEnable = 0x40;
    private const byte AlarmEnable = 0x20;
    private const byte UpdateEndedEnable = 0x10;
    private const byte SetBit = 0x80;

    private const byte InterruptRequest = 0x80;
    private const byte PeriodicFlag = 0x40;
    private const byte AlarmFlag = 0x20;
    private const byte UpdateEndedFlag = 0x10;

    /// <summary>DV = 010 (time base running), RS = 0110 (1024 Hz): the BIOS default.</summary>
    private const byte Rate1024Hz = 0x26;

    /// <summary>DV = 010, RS = 1111: 2 Hz, the slowest periodic rate.</summary>
    private const byte Rate2Hz = 0x2F;

    private const double Period1024HzMicroseconds = 1_000_000.0 / 1024.0;

    private static (Cmos Cmos, InterruptController Interrupts) NewChip()
    {
        var interrupts = new InterruptController();
        var cmos = new Cmos(16u * 1024 * 1024, interrupts)
        {
            TimeSource = () => new DateTime(2026, 9, 29, 12, 34, 56),
        };

        return (cmos, interrupts);
    }

    private static void Write(Cmos cmos, byte register, byte value)
    {
        cmos.WriteByte(IndexPort, register);
        cmos.WriteByte(DataPort, value);
    }

    private static byte Read(Cmos cmos, byte register)
    {
        cmos.WriteByte(IndexPort, register);
        return cmos.ReadByte(DataPort);
    }

    /// <summary>IRQ 8 is line 0 of the second 8259.</summary>
    private static bool Irq8Requested(InterruptController interrupts) =>
        (interrupts.Slave.Registers.Request & 0x01) != 0;

    [Fact]
    public void RegisterAReadsBackWhatWasWrittenWithUpdateInProgressClear()
    {
        var (cmos, _) = NewChip();

        Assert.Equal(Rate1024Hz, Read(cmos, StatusA));

        Write(cmos, StatusA, 0xAF);

        // Bit 7 (UIP) is read-only and kept clear; the divider and rate stay.
        Assert.Equal(Rate2Hz, Read(cmos, StatusA));
    }

    [Fact]
    public void RegisterDReportsTheBatteryGood()
    {
        var (cmos, _) = NewChip();

        Assert.Equal(0x80, Read(cmos, StatusD));
    }

    [Fact]
    public void PeriodicInterruptRaisesIrq8AtTheRateAndReadingRegisterCAcknowledges()
    {
        var (cmos, interrupts) = NewChip();

        Write(cmos, StatusB, (byte)(0x02 | PeriodicEnable));

        // Just short of one 1024 Hz period: nothing yet.
        cmos.Advance(Period1024HzMicroseconds * 0.9);
        Assert.False(Irq8Requested(interrupts));

        cmos.Advance(Period1024HzMicroseconds * 0.2);
        Assert.True(Irq8Requested(interrupts));

        byte flags = Read(cmos, StatusC);

        Assert.Equal(InterruptRequest | PeriodicFlag, flags & (InterruptRequest | PeriodicFlag));
        Assert.False(Irq8Requested(interrupts));

        // Read-to-clear: a second read finds nothing.
        Assert.Equal(0, Read(cmos, StatusC));
    }

    [Fact]
    public void PeriodicFlagIsSetEvenWhenItsInterruptIsNotEnabled()
    {
        var (cmos, interrupts) = NewChip();

        cmos.Advance(Period1024HzMicroseconds * 1.5);

        byte flags = Read(cmos, StatusC);

        Assert.Equal(PeriodicFlag, flags & PeriodicFlag);
        Assert.Equal(0, flags & InterruptRequest);
        Assert.False(Irq8Requested(interrupts));
    }

    [Fact]
    public void EnablingAnInterruptWhoseFlagIsAlreadyUpRaisesTheLineAtOnce()
    {
        var (cmos, interrupts) = NewChip();

        cmos.Advance(Period1024HzMicroseconds * 1.5);
        Assert.False(Irq8Requested(interrupts));

        Write(cmos, StatusB, (byte)(0x02 | PeriodicEnable));

        Assert.True(Irq8Requested(interrupts));
    }

    [Fact]
    public void TwoHertzRateGivesTwoPeriodicFlagsASecond()
    {
        var (cmos, _) = NewChip();

        Write(cmos, StatusA, Rate2Hz);

        int periods = 0;

        // One second in 10 ms steps, acknowledging each as a handler would.
        for (int step = 0; step < 100; step++)
        {
            cmos.Advance(10_000);

            if ((Read(cmos, StatusC) & PeriodicFlag) != 0)
            {
                periods++;
            }
        }

        Assert.Equal(2, periods);
    }

    [Fact]
    public void RateSelectZeroTurnsThePeriodicInterruptOff()
    {
        var (cmos, _) = NewChip();

        Write(cmos, StatusA, 0x20);
        cmos.Advance(100_000);

        Assert.Equal(0, Read(cmos, StatusC) & PeriodicFlag);
    }

    [Fact]
    public void StoppingTheDividerStopsEveryInterrupt()
    {
        var (cmos, _) = NewChip();

        // DV = 110: the divider chain held in reset.
        Write(cmos, StatusA, 0x66);
        cmos.Advance(2_000_000);

        Assert.Equal(0, Read(cmos, StatusC));
    }

    [Fact]
    public void UpdateEndedInterruptComesOnceASecond()
    {
        var (cmos, interrupts) = NewChip();

        Write(cmos, StatusA, 0x20); // periodic off, to see the update alone
        Write(cmos, StatusB, (byte)(0x02 | UpdateEndedEnable));

        cmos.Advance(999_000);
        Assert.False(Irq8Requested(interrupts));

        cmos.Advance(2_000);
        Assert.True(Irq8Requested(interrupts));
        Assert.Equal(InterruptRequest | UpdateEndedFlag, Read(cmos, StatusC));
    }

    [Fact]
    public void SettingSetStopsUpdatesAndClearsUpdateEndedEnable()
    {
        var (cmos, _) = NewChip();

        Write(cmos, StatusA, 0x20);
        Write(cmos, StatusB, (byte)(0x02 | SetBit | UpdateEndedEnable));

        Assert.Equal(0, Read(cmos, StatusB) & UpdateEndedEnable);

        cmos.Advance(2_000_000);
        Assert.Equal(0, Read(cmos, StatusC) & UpdateEndedFlag);
    }

    [Fact]
    public void AlarmFiresWhenTheTimeMatchesRegisters1And3And5()
    {
        var (cmos, interrupts) = NewChip();

        Write(cmos, StatusA, 0x20);
        Write(cmos, 0x01, 0x56); // seconds, BCD
        Write(cmos, 0x03, 0x34); // minutes
        Write(cmos, 0x05, 0x12); // hours
        Write(cmos, StatusB, (byte)(0x02 | AlarmEnable));

        Assert.Equal(0x56, Read(cmos, 0x01));

        cmos.Advance(1_000_000);

        Assert.True(Irq8Requested(interrupts));
        Assert.Equal(AlarmFlag, Read(cmos, StatusC) & AlarmFlag);
    }

    [Fact]
    public void AlarmValuesFromC0hAreDontCare()
    {
        var (cmos, _) = NewChip();

        Write(cmos, StatusA, 0x20);
        Write(cmos, 0x01, 0xFF);
        Write(cmos, 0x03, 0xC0);
        Write(cmos, 0x05, 0xFF);
        cmos.Advance(1_000_000);

        Assert.Equal(AlarmFlag, Read(cmos, StatusC) & AlarmFlag);
    }

    [Fact]
    public void AlarmThatDoesNotMatchStaysQuiet()
    {
        var (cmos, _) = NewChip();

        Write(cmos, StatusA, 0x20);
        Write(cmos, 0x01, 0x00);
        Write(cmos, 0x03, 0x00);
        Write(cmos, 0x05, 0x00);
        cmos.Advance(1_000_000);

        Assert.Equal(0, Read(cmos, StatusC) & AlarmFlag);
    }

    [Fact]
    public void RegistersCAndDIgnoreWrites()
    {
        var (cmos, _) = NewChip();

        Write(cmos, StatusC, 0xFF);
        Write(cmos, StatusD, 0x00);

        Assert.Equal(0, Read(cmos, StatusC));
        Assert.Equal(0x80, Read(cmos, StatusD));
    }

    [Fact]
    public void ResetClearsEnablesAndFlagsAndLowersTheLine()
    {
        var (cmos, interrupts) = NewChip();

        Write(cmos, StatusB, (byte)(0x02 | PeriodicEnable | AlarmEnable | UpdateEndedEnable));
        cmos.Advance(Period1024HzMicroseconds * 2);
        Assert.True(Irq8Requested(interrupts));

        cmos.Reset();

        Assert.False(Irq8Requested(interrupts));
        Assert.Equal(0x02, Read(cmos, StatusB));
        Assert.Equal(0, Read(cmos, StatusC));
    }

    [Fact]
    public void TimeRegistersStillIgnoreWrites()
    {
        var (cmos, _) = NewChip();

        Write(cmos, 0x00, 0x11);

        Assert.Equal(0x56, Read(cmos, 0x00));
    }
}
