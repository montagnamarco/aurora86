using Aurora86.Core;
using Aurora86.Core.Cpu;
using Aurora86.Core.Devices.Chipset;

namespace Aurora86.Tests;

/// <summary>
/// Work package P4.5's ACPI runtime (plans/specs/acpi.md §4): SMI_CMD
/// toggling PM1_CNT.SCI_EN, the SCI itself on IRQ 9, the power button, the
/// SLP_EN strobe, and GPE0 -- test cases 9-13, plus the ELCR bit and the
/// snapshot section decision C6/C4/C5 add on top of the existing PM block.
/// Every test drives the real I/O ports a guest OS or an ACPI-aware BIOS
/// caller would, on a real <see cref="Machine"/>.
/// </summary>
public class AcpiRuntimeTests
{
    private const ushort Pmba = Piix4PowerManagement.DefaultPmBase;
    private const ushort Pm1StatusPort = Pmba;
    private const ushort Pm1EnablePort = Pmba + 2;
    private const ushort Pm1ControlPort = Pmba + 4;
    private const ushort Gpe0StatusPort = Pmba + 0x0C;
    private const ushort Gpe0EnablePort = Pmba + 0x0E;
    private const ushort SmiCommandPort = 0xB2;

    private const byte AcpiEnableCommand = 0xF1;
    private const byte AcpiDisableCommand = 0xF0;

    private const ushort PowerButtonStatusBit = 0x0100;   // PM1_STS bit 8, PWRBTN_STS.
    private const ushort PowerButtonEnableBit = 0x0100;   // PM1_EN bit 8, PWRBTN_EN.
    private const ushort TimerStatusBit = 0x0001;         // PM1_STS bit 0, TMR_STS.
    private const ushort TimerEnableBit = 0x0001;         // PM1_EN bit 0, TMR_EN.
    private const ushort SciEnableBit = 0x0001;           // PM1_CNT bit 0, SCI_EN.
    private const ushort SleepEnableBit = 0x2000;         // PM1_CNT bit 13, SLP_EN.
    private const int SleepTypeShift = 10;                // PM1_CNT bits 12:10, SLP_TYPx.

    private static Machine NewTxMachine()
    {
        var machine = new Machine(chipset: Chipset.I430Tx, initialCpuModel: CpuModel.PentiumMmx);
        machine.RunInstructions(200);
        return machine;
    }

    /// <summary>
    /// Isolates IRQ 9 so a test can read <see cref="Machine.Interrupts"/>'s
    /// own pending flag without another line's own traffic -- the timer,
    /// above all -- making it ambiguous which line is actually pending.
    /// </summary>
    private static void IsolateSciLine(Machine machine)
    {
        machine.Io.WriteByte(0xA1, unchecked((byte)~(1 << 1)));   // unmask only IRQ 9 on the slave.
        machine.Io.WriteByte(0x21, unchecked((byte)~(1 << 2)));   // unmask only the cascade on the master.
    }

    // ---- test case 9: SMI_CMD toggles SCI_EN ----------------------------------

    [Fact]
    public void SmiCmdF1SetsSciEnAndF0ClearsIt()
    {
        using Machine machine = NewTxMachine();

        machine.Io.WriteByte(SmiCommandPort, AcpiEnableCommand);
        Assert.Equal(SciEnableBit, machine.Io.ReadWord(Pm1ControlPort) & SciEnableBit);

        machine.Io.WriteByte(SmiCommandPort, AcpiDisableCommand);
        Assert.Equal(0, machine.Io.ReadWord(Pm1ControlPort) & SciEnableBit);
    }

    // ---- test case 10: the PM timer's own overflow status and the SCI --------

    [Fact]
    public void PmTimerOverflowSetsStatusRaisesSciWhenEnabledAndW1CClearsItAgain()
    {
        using Machine machine = NewTxMachine();
        IsolateSciLine(machine);
        machine.Io.WriteByte(SmiCommandPort, AcpiEnableCommand);
        machine.Io.WriteWord(Pm1EnablePort, TimerEnableBit);

        // Dropping the CPU's own clock means a handful of cycles already
        // cross bit 23 of the timer's fixed 3.579545 MHz count --
        // Piix4PowerManagementTests already measures how many cycles a real
        // 25 MHz clock needs; this test only cares about the overflow's
        // side effects, not that arithmetic.
        machine.CpuFrequencyHz = 1;
        machine.RunInstructions(16);

        Assert.Equal(TimerStatusBit, machine.Io.ReadWord(Pm1StatusPort) & TimerStatusBit);
        Assert.True(machine.Interrupts.HasPendingInterrupt);

        machine.Io.WriteWord(Pm1StatusPort, TimerStatusBit);   // W1C.
        Assert.Equal(0, machine.Io.ReadWord(Pm1StatusPort) & TimerStatusBit);
        Assert.False(machine.Interrupts.HasPendingInterrupt);
    }

    // ---- test case 11: the power button, both with and without an ACPI OS ----

    [Fact]
    public void PowerButtonInLegacyModeCallsRequestPowerOffImmediately()
    {
        using Machine machine = NewTxMachine();

        bool poweredOff = false;
        machine.PowerOffRequested += () => poweredOff = true;
        machine.PressPowerButton();

        Assert.True(poweredOff);
        Assert.Equal(0, machine.Io.ReadWord(Pm1StatusPort) & PowerButtonStatusBit);
    }

    [Fact]
    public void PowerButtonInAcpiModeSetsPwrbtnStatusAndRaisesTheSciInstead()
    {
        using Machine machine = NewTxMachine();
        IsolateSciLine(machine);
        machine.Io.WriteByte(SmiCommandPort, AcpiEnableCommand);
        machine.Io.WriteWord(Pm1EnablePort, PowerButtonEnableBit);

        bool poweredOff = false;
        machine.PowerOffRequested += () => poweredOff = true;
        machine.PressPowerButton();

        Assert.False(poweredOff);
        Assert.Equal(PowerButtonStatusBit, machine.Io.ReadWord(Pm1StatusPort) & PowerButtonStatusBit);
        Assert.True(machine.Interrupts.HasPendingInterrupt);
    }

    [Fact]
    public void PowerButtonOnTheI430FxBoardAlwaysCallsRequestPowerOff()
    {
        using var machine = new Machine(chipset: Chipset.I430Fx, initialCpuModel: CpuModel.PentiumMmx);
        machine.RunInstructions(200);

        bool poweredOff = false;
        machine.PowerOffRequested += () => poweredOff = true;
        machine.PressPowerButton();

        Assert.True(poweredOff);
    }

    // ---- test case 12: SLP_EN, SLP_TYP and \_S5 -------------------------------

    [Fact]
    public void SleepEnableWithSlpTypeZeroPowersOffOnceAndReadsBackAsZero()
    {
        using Machine machine = NewTxMachine();
        int powerOffCount = 0;
        machine.PowerOffRequested += () => powerOffCount++;

        // SLP_TYP = 0 (\_S5) and SLP_EN together, one word write, exactly as
        // an ACPI OS's own S5 transition does.
        machine.Io.WriteWord(Pm1ControlPort, SleepEnableBit);
        Assert.Equal(1, powerOffCount);
        Assert.Equal(0, machine.Io.ReadWord(Pm1ControlPort) & SleepEnableBit);

        // No \_S1/\_S3/\_S4 exists for an ACPI OS to have asked for
        // (decision C6), so a different SLP_TYP does nothing here.
        const ushort OtherSleepType = 1 << SleepTypeShift;
        machine.Io.WriteWord(Pm1ControlPort, OtherSleepType | SleepEnableBit);
        Assert.Equal(1, powerOffCount);
    }

    // ---- test case 13: GPE0 always reads 0 ------------------------------------

    [Fact]
    public void Gpe0StatusAlwaysReadsZeroWhileEnableIsOrdinaryStorage()
    {
        using Machine machine = NewTxMachine();

        machine.Io.WriteWord(Gpe0EnablePort, 0xFFFF);
        machine.Io.WriteWord(Gpe0StatusPort, 0xFFFF);   // W1C: a no-op, since nothing ever sets a status bit.

        Assert.Equal(0, machine.Io.ReadWord(Gpe0StatusPort));
        Assert.Equal(0xFFFF, machine.Io.ReadWord(Gpe0EnablePort));
    }

    // ---- decision C4: the SCI's own ELCR bit ----------------------------------

    [Fact]
    public void PostMarksIrq9LevelTriggeredInTheElcrOnTheI430TxBoardOnly()
    {
        using Machine tx = NewTxMachine();
        Assert.Equal(1 << 1, tx.Io.ReadByte(0x4D1) & (1 << 1));

        using var fx = new Machine(chipset: Chipset.I430Fx, initialCpuModel: CpuModel.PentiumMmx);
        fx.RunInstructions(200);
        Assert.Equal(0, fx.Io.ReadByte(0x4D1) & (1 << 1));
    }

    // ---- the PM section's GPE0 addition round-trips through a snapshot -------

    [Fact]
    public void MachineSnapshotRoundTripsGpe0Enable()
    {
        using Machine machine = NewTxMachine();
        machine.Io.WriteWord(Gpe0EnablePort, 0x1234);

        MachineSnapshot snapshot = MachineSnapshot.Capture(machine);
        machine.Io.WriteWord(Gpe0EnablePort, 0);
        snapshot.RestoreTo(machine);

        Assert.Equal(0x1234, machine.Io.ReadWord(Gpe0EnablePort));
    }
}
