using Aurora86.Core;
using Aurora86.Core.Cpu;
using Aurora86.Core.Devices.Apic;
using Aurora86.Core.Devices.Chipset;
using Aurora86.Core.Devices.Interrupts;
using Aurora86.Core.Memory;
using Aurora86.Core.Timing;

namespace Aurora86.Tests;

/// <summary>
/// Work package P4.4: the processor-local APIC, register by register, and
/// the interrupt-source seam (<see cref="IInterruptSource"/>) that lets it
/// wrap the 8259 pair without changing what the CPU sees on a machine that
/// never engages it (coordinator decision A1). See plans/specs/apic.md for
/// the register table and the coordinator decisions cited throughout.
/// </summary>
public class LocalApicTests
{
    // MMIO offsets, restated here the way a driver's own header would name
    // them (AGENTS.md: "which register, which offset, which bit"), rather
    // than reaching into LocalApic's private constants.
    private const uint IdOffset = 0x020;
    private const uint VersionOffset = 0x030;
    private const uint TprOffset = 0x080;
    private const uint PprOffset = 0x0A0;
    private const uint EoiOffset = 0x0B0;
    private const uint SvrOffset = 0x0F0;
    private const uint IsrBase = 0x100;
    private const uint IrrBase = 0x200;
    private const uint EsrOffset = 0x280;
    private const uint IcrLowOffset = 0x300;
    private const uint IcrHighOffset = 0x310;
    private const uint LvtTimerOffset = 0x320;
    private const uint LvtLint0Offset = 0x350;
    private const uint LvtLint1Offset = 0x360;
    private const uint LvtErrorOffset = 0x370;
    private const uint InitialCountOffset = 0x380;
    private const uint CurrentCountOffset = 0x390;
    private const uint DivideConfigOffset = 0x3E0;

    private const uint LvtMaskBit = 1u << 16;
    private const uint LvtPeriodicBit = 1u << 17;
    private const uint LvtDeliveryModeExtInt = 0x700;   // bits 10:8 = 111
    private const uint SvrSoftwareEnableBit = 1u << 8;

    /// <summary>
    /// A local APIC wired exactly as <c>Machine</c> wires one, minus the
    /// devices this test does not need: a real <see cref="Cpu"/> (so
    /// APIC_BASE and <see cref="CpuFeature.Apic"/> are the real thing, not a
    /// stand-in), the wrapped 8259 pair, and a <see cref="Scheduler"/> for
    /// the timer.
    /// </summary>
    private static (LocalApic Apic, InterruptController Pic, Cpu Cpu, Scheduler Scheduler) Build(
        CpuModel model = CpuModel.PentiumMmx)
    {
        // Large enough to hold APIC_BASE's reset address (FEE00000h) plus its page.
        var bus = new PhysicalMemoryBus(1024 * 1024, 0xFEE01000);
        var cpu = new Cpu(bus);
        cpu.State.SelectModel(model);
        var pic = new InterruptController();
        // The BIOS's own ICW sequence (Machine.Post.cs's InitializeInterruptController):
        // master vector base 08h, slave 70h, nothing masked.
        pic.Master.WriteByte(0x20, 0x11);
        pic.Master.WriteByte(0x21, 0x08);
        pic.Master.WriteByte(0x21, 0x04);
        pic.Master.WriteByte(0x21, 0x01);
        pic.Master.WriteByte(0x21, 0x00);
        pic.Slave.WriteByte(0xA0, 0x11);
        pic.Slave.WriteByte(0xA1, 0x70);
        pic.Slave.WriteByte(0xA1, 0x02);
        pic.Slave.WriteByte(0xA1, 0x01);
        pic.Slave.WriteByte(0xA1, 0x00);

        var scheduler = new Scheduler();
        var apic = new LocalApic(pic, cpu, bus, scheduler, () => cpu.Cycles);
        cpu.Msrs.ApicBaseChanged += apic.OnApicBaseChanged;
        apic.OnApicBaseChanged(cpu.Msrs.ApicBaseRaw);
        cpu.Interrupts = apic;
        return (apic, pic, cpu, scheduler);
    }

    // ---- test cases 1-3, 15: reset values -----------------------------------

    [Fact]
    public void ApicBaseMsrResetsToFee00900()
    {
        var (_, _, cpu, _) = Build();
        Assert.Equal(0xFEE00900UL, cpu.Msrs.ApicBaseRaw);
    }

    [Fact]
    public void SvrResetsTo000000Ff()
    {
        var (apic, _, _, _) = Build();
        Assert.Equal(0x000000FFu, apic.ReadDword(SvrOffset));
    }

    [Fact]
    public void LvtRegistersResetTo00010000()
    {
        var (apic, _, _, _) = Build();
        Assert.Equal(0x00010000u, apic.ReadDword(LvtTimerOffset));
        Assert.Equal(0x00010000u, apic.ReadDword(LvtLint0Offset));
        Assert.Equal(0x00010000u, apic.ReadDword(LvtLint1Offset));
        Assert.Equal(0x00010000u, apic.ReadDword(LvtErrorOffset));
    }

    /// <summary>Coordinator decision A8: bits 23:16 (the highest LVT index) vary by model.</summary>
    [Theory]
    [InlineData(CpuModel.PentiumMmx, 0x00030011u)]
    [InlineData(CpuModel.PentiumII, 0x00040011u)]
    [InlineData(CpuModel.PentiumIII, 0x00040011u)]
    [InlineData(CpuModel.Pentium4, 0x00050014u)]
    [InlineData(CpuModel.Core2, 0x00050014u)]
    [InlineData(CpuModel.Haswell, 0x00050014u)]
    public void VersionRegisterAndMaxLvtFieldAreModelDependent(CpuModel model, uint expected)
    {
        var (apic, _, _, _) = Build(model);
        Assert.Equal(expected, apic.ReadDword(VersionOffset));
    }

    /// <summary>Test case 16: an xAPIC version (1xh) is what STARTUP IPIs need; the P55C's 0011h low byte already is one.</summary>
    [Fact]
    public void EveryModelledVersionIsAnXApicVersion()
    {
        foreach (CpuModel model in new[]
                 {
                     CpuModel.PentiumMmx, CpuModel.PentiumII, CpuModel.PentiumIII,
                     CpuModel.Pentium4, CpuModel.Core2, CpuModel.Haswell,
                 })
        {
            var (apic, _, _, _) = Build(model);
            uint version = apic.ReadDword(VersionOffset) & 0xFF;
            Assert.InRange(version, 0x10u, 0x1Fu);
        }
    }

    // ---- coordinator ruling, item 13: APIC_BASE is family six only -----------

    /// <summary>
    /// On a Pentium MMX (the P5 MSR set) there is no APIC_BASE MSR at all
    /// to move or hardware-disable the unit with: it is always fixed at
    /// FEE00000h and always hardware-enabled, and the only way a guest can
    /// quiet it is SVR's own software-enable bit.
    /// </summary>
    [Fact]
    public void ApicIsAlwaysFixedAndHardwareEnabledOnThePentiumMmxsP5Set()
    {
        var (apic, _, _, _) = Build(CpuModel.PentiumMmx);
        apic.ImcrApicMode = true;
        apic.WriteDword(SvrOffset, apic.ReadDword(SvrOffset) | SvrSoftwareEnableBit);

        apic.DeliverFixed(0x41, levelTriggered: false);
        Assert.True(apic.HasPendingInterrupt);
        Assert.Equal(0x41, apic.GetPendingVector());

        // SVR alone can still quiet it.
        apic.WriteDword(SvrOffset, apic.ReadDword(SvrOffset) & ~SvrSoftwareEnableBit);
        apic.DeliverFixed(0x42, levelTriggered: false);
        Assert.False(apic.HasPendingInterrupt);
    }

    // ---- coordinator decision A1: byte-identical machines --------------------

    [Fact]
    public void AbsentApicAlwaysForwardsThe8259Unchanged()
    {
        // Intel486 has no APIC in its silicon at all: LocalApic must not
        // even engage, on any board, exactly as A1 requires.
        var (apic, pic, _, _) = Build(CpuModel.Intel486);

        Assert.False(apic.HasPendingInterrupt);
        pic.RaiseIrq(0);
        Assert.True(apic.HasPendingInterrupt);

        int vector = apic.GetPendingVector();
        Assert.Equal(0x08, vector);            // master's own vector base (programmed above) + line 0
        Assert.True(pic.HasInterruptInService);   // genuinely the 8259 that served it, not separate state
    }

    [Fact]
    public void PicModeForwardsThe8259EvenWithAnApicPresentAndEnabled()
    {
        // The reset state (IMCR = 0, coordinator decision A1): an Apic-
        // capable model still forwards the 8259 completely unchanged until
        // something switches ImcrApicMode -- which nothing does by itself.
        var (apic, pic, _, _) = Build(CpuModel.PentiumMmx);

        pic.RaiseIrq(1);
        Assert.True(apic.HasPendingInterrupt);

        int vector = apic.GetPendingVector();
        Assert.Equal(0x09, vector);   // vector base 08h + line 1
        Assert.True(pic.HasInterruptInService);
    }

    [Fact]
    public void HardwareDisabledApicAlsoForwardsThe8259InPicMode()
    {
        // Family six (PentiumII), not PentiumMmx: coordinator decision A11
        // -- APIC_BASE exists as an MSR from the P6 family on only, and
        // a P5's own on-die APIC has no MSR to hardware-disable it with at
        // all (see ApicIsAlwaysFixedAndHardwareEnabledOnThePentiumMmxsP5Set).
        var (apic, pic, cpu, _) = Build(CpuModel.PentiumII);

        const uint ApicBaseIndex = 0x1B;
        const uint DisabledApicBase = 0xFEE00000u;   // enable bit (bit 11) and BSP both clear
        Assert.True(cpu.Msrs.TryWrite(ApicBaseIndex, DisabledApicBase));

        pic.RaiseIrq(0);
        Assert.True(apic.HasPendingInterrupt);

        int vector = apic.GetPendingVector();
        Assert.Equal(0x08, vector);
        Assert.True(pic.HasInterruptInService);
    }

    /// <summary>
    /// Coordinator ruling, second independent review, item N7: with the
    /// unit hardware-disabled, LINT0 reverts to a plain INTR pin regardless
    /// of what the IMCR holds -- there is no local APIC logic left running
    /// to route LINT0 through, so IMCR=1 does not strand the interrupt the
    /// way it would if the unit were merely software-disabled instead.
    /// </summary>
    [Fact]
    public void HardwareDisabledApicForwardsThe8259EvenWithImcrSetToOne()
    {
        var (apic, pic, cpu, _) = Build(CpuModel.PentiumII);
        apic.ImcrApicMode = true;

        const uint ApicBaseIndex = 0x1B;
        const uint DisabledApicBase = 0xFEE00000u;
        Assert.True(cpu.Msrs.TryWrite(ApicBaseIndex, DisabledApicBase));

        pic.RaiseIrq(0);
        Assert.True(apic.HasPendingInterrupt);
        Assert.Equal(0x08, apic.GetPendingVector());
    }

    // ---- virtual wire: LINT0 ExtINT + SVR enable + IMCR = 1 -----------------

    [Fact]
    public void VirtualWireStillDeliversThe8259SVectors()
    {
        var (apic, pic, _, _) = Build(CpuModel.PentiumMmx);

        // SVR software-enable, then LINT0 = ExtINT, unmasked -- the
        // sequence plans/specs/apic.md §1.4 describes for "virtual wire via
        // the local APIC".
        apic.WriteDword(SvrOffset, apic.ReadDword(SvrOffset) | SvrSoftwareEnableBit);
        apic.WriteDword(LvtLint0Offset, LvtDeliveryModeExtInt);   // mask bit clear: unmasked
        apic.ImcrApicMode = true;

        Assert.False(apic.HasPendingInterrupt);
        pic.RaiseIrq(1);   // the keyboard, an ordinary edge-triggered ISA line

        Assert.True(apic.HasPendingInterrupt);
        int vector = apic.GetPendingVector();
        Assert.Equal(0x09, vector);   // IRQ1's vector, straight from the 8259's own vector base
    }

    // ---- symmetric mode: a Fixed vector delivered through this class's own IRR ----

    [Fact]
    public void SoftwareDisabledSvrBlocksFixedDeliveryButLatchesIt()
    {
        var (apic, _, _, _) = Build(CpuModel.PentiumMmx);
        apic.ImcrApicMode = true;
        // SVR left at its reset value: software-disabled (bit 8 clear).

        apic.DeliverFixed(0x41, levelTriggered: false);
        Assert.False(apic.HasPendingInterrupt);   // latched in IRR, but not posted (plans/specs/apic.md §1.3)

        // Enabling SVR now reveals it: nothing was ever lost, only held back.
        apic.WriteDword(SvrOffset, apic.ReadDword(SvrOffset) | SvrSoftwareEnableBit);
        Assert.True(apic.HasPendingInterrupt);
        Assert.Equal(0x41, apic.GetPendingVector());
    }

    [Fact]
    public void SoftwareDisabledSvrHoldsBackBothFixedDeliveryAndLint0ExtInt()
    {
        // Independent review, item 12: while SVR is software-disabled every
        // LVT's mask bit reads 1 and a write can never clear it in storage
        // -- so LINT0 can never actually have been unmasked for ExtINT
        // while disabled, and the IMCR=1 path built on it delivers nothing.
        // (The IMCR=0 direct path is a completely different route, and
        // unaffected -- see PicModeForwardsThe8259EvenWithAnApicPresentAndEnabled.)
        var (apic, pic, _, _) = Build(CpuModel.PentiumMmx);
        apic.ImcrApicMode = true;
        // SVR left at its reset value: software-disabled (bit 8 clear).
        // Fixed-delivery-while-disabled is covered on its own by
        // SoftwareDisabledSvrBlocksFixedDeliveryButLatchesIt below; this
        // test is only about LINT0/ExtINT, so the IRR stays empty here.

        apic.WriteDword(LvtLint0Offset, LvtDeliveryModeExtInt);   // an attempted unmask: forced back to masked in storage
        pic.RaiseIrq(1);
        Assert.False(apic.HasPendingInterrupt);

        // Enabling SVR now does not retroactively unmask LINT0 either: the
        // write above never took hold, so there is nothing to reveal.
        apic.WriteDword(SvrOffset, apic.ReadDword(SvrOffset) | SvrSoftwareEnableBit);
        Assert.False(apic.HasPendingInterrupt);

        // Only a LINT0 write made *after* SVR is enabled actually unmasks it.
        apic.WriteDword(LvtLint0Offset, LvtDeliveryModeExtInt);
        Assert.True(apic.HasPendingInterrupt);
    }

    [Fact]
    public void FixedVectorDeliveredThroughIrrMovesToIsrOnDelivery()
    {
        var (apic, _, _, _) = Build(CpuModel.PentiumMmx);
        apic.ImcrApicMode = true;
        apic.WriteDword(SvrOffset, apic.ReadDword(SvrOffset) | SvrSoftwareEnableBit);

        apic.DeliverFixed(0x41, levelTriggered: false);
        Assert.True(apic.HasPendingInterrupt);

        int vector = apic.GetPendingVector();
        Assert.Equal(0x41, vector);

        // Vector 0x41 (65) lives in word 2 of each 256-bit bitmap (65 >> 5),
        // sixteen bytes further along than word 0 -- the register spacing
        // plans/specs/apic.md §1.2 documents.
        uint wordOffset = (uint)((vector >> 5) * 0x10);
        uint bit = 1u << (vector & 31);
        Assert.Equal(0u, apic.ReadDword(IrrBase + wordOffset) & bit);   // moved out of IRR
        Assert.Equal(bit, apic.ReadDword(IsrBase + wordOffset) & bit);  // and into ISR
        Assert.False(apic.HasPendingInterrupt);
    }

    /// <summary>
    /// SDM 10.8.4/10.8.6.1: the higher-class vector is delivered first, and
    /// the lower one stays blocked behind PPR -- which now reflects the
    /// vector still in service -- until an EOI ends it.
    /// </summary>
    [Fact]
    public void HigherPriorityVectorPreemptsALowerOneAtTheSamePpr()
    {
        var (apic, _, _, _) = Build(CpuModel.PentiumMmx);
        apic.ImcrApicMode = true;
        apic.WriteDword(SvrOffset, apic.ReadDword(SvrOffset) | SvrSoftwareEnableBit);

        apic.DeliverFixed(0x20, levelTriggered: false);   // class 2
        apic.DeliverFixed(0x81, levelTriggered: false);   // class 8, higher priority

        Assert.Equal(0x81, apic.GetPendingVector());
        Assert.False(apic.HasPendingInterrupt);   // 0x20's class does not beat PPR while 0x81 is in service

        apic.WriteDword(EoiOffset, 0);
        Assert.Equal(0x20, apic.GetPendingVector());
    }

    // ---- EOI and Remote IRR (test case 7) ------------------------------------

    [Fact]
    public void EoiOfALevelVectorClearsRemoteIrrOnEveryListeningIoApic()
    {
        var (apic, _, _, _) = Build(CpuModel.PentiumMmx);
        apic.ImcrApicMode = true;
        apic.WriteDword(SvrOffset, apic.ReadDword(SvrOffset) | SvrSoftwareEnableBit);

        byte? notifiedVector = null;
        apic.LevelTriggeredEoi += vector => notifiedVector = vector;

        apic.DeliverFixed(0x50, levelTriggered: true);
        apic.GetPendingVector();   // moves it to ISR
        Assert.Null(notifiedVector);

        apic.WriteDword(EoiOffset, 0);   // any value ends the highest in-service vector
        Assert.Equal((byte)0x50, notifiedVector);
    }

    [Fact]
    public void EoiOfAnEdgeVectorNeverNotifiesAnIoApic()
    {
        var (apic, _, _, _) = Build(CpuModel.PentiumMmx);
        apic.ImcrApicMode = true;
        apic.WriteDword(SvrOffset, apic.ReadDword(SvrOffset) | SvrSoftwareEnableBit);

        bool notified = false;
        apic.LevelTriggeredEoi += _ => notified = true;

        apic.DeliverFixed(0x50, levelTriggered: false);
        apic.GetPendingVector();
        apic.WriteDword(EoiOffset, 0);

        Assert.False(notified);
    }

    // ---- ESR write-then-read --------------------------------------------------

    [Fact]
    public void EsrWriteThenReadRoundTripsToZeroSinceNoErrorIsEverLatched()
    {
        var (apic, _, _, _) = Build();
        apic.WriteDword(EsrOffset, 0xFFFFFFFF);
        Assert.Equal(0u, apic.ReadDword(EsrOffset));
    }

    // ---- ICR: self and all-including-self, fixed and NMI only (decision A10) ----

    [Fact]
    public void IcrSelfShorthandFixedDeliversToThisSameApic()
    {
        var (apic, _, _, _) = Build(CpuModel.PentiumMmx);
        apic.ImcrApicMode = true;
        apic.WriteDword(SvrOffset, apic.ReadDword(SvrOffset) | SvrSoftwareEnableBit);

        const uint ShorthandSelf = 1u << 18;
        const uint DeliveryModeFixed = 0x000;
        apic.WriteDword(IcrLowOffset, ShorthandSelf | DeliveryModeFixed | 0x33);

        Assert.True(apic.HasPendingInterrupt);
        Assert.Equal(0x33, apic.GetPendingVector());
    }

    [Fact]
    public void IcrDeliveryStatusAlwaysReadsZero()
    {
        // Coordinator decision A6: delivery is synchronous, so bit 12 never
        // shows a send still in flight, whatever was written underneath it.
        var (apic, _, _, _) = Build();
        apic.WriteDword(IcrLowOffset, 0xFFFFFFFF);
        Assert.Equal(0u, apic.ReadDword(IcrLowOffset) & (1u << 12));
    }

    [Fact]
    public void IcrInitIsAcceptedAndStoredButDeliversNothing()
    {
        // Coordinator decision A10: INIT is self-targeted where meaningful,
        // and with one modelled CPU there is nothing for it to do beyond
        // being readable back -- it must not raise #GP or post a vector.
        var (apic, _, _, _) = Build(CpuModel.PentiumMmx);
        apic.ImcrApicMode = true;
        apic.WriteDword(SvrOffset, apic.ReadDword(SvrOffset) | SvrSoftwareEnableBit);

        const uint ShorthandAllIncludingSelf = 2u << 18;
        const uint DeliveryModeInit = 0x500;
        const uint LevelAssert = 1u << 14;
        apic.WriteDword(IcrLowOffset, ShorthandAllIncludingSelf | DeliveryModeInit | LevelAssert);

        Assert.Equal(ShorthandAllIncludingSelf | DeliveryModeInit | LevelAssert, apic.ReadDword(IcrLowOffset));
        Assert.False(apic.HasPendingInterrupt);

        // The deassert half of the same sequence (test case 19) is equally inert.
        apic.WriteDword(IcrLowOffset, ShorthandAllIncludingSelf | DeliveryModeInit);
        Assert.False(apic.HasPendingInterrupt);
    }

    // ---- timer: one-shot, periodic, masking (test case 14) -------------------

    [Fact]
    public void OneShotTimerFiresOnceAtInitialCountTimesTheDivisor()
    {
        var (apic, _, cpu, scheduler) = Build(CpuModel.PentiumMmx);
        apic.ImcrApicMode = true;
        apic.WriteDword(SvrOffset, apic.ReadDword(SvrOffset) | SvrSoftwareEnableBit);
        apic.WriteDword(LvtTimerOffset, 0x22);              // unmasked, vector 22h, one-shot
        apic.WriteDword(DivideConfigOffset, 0x0B);         // divide by 1 (plans/specs/apic.md §1.2)
        apic.WriteDword(InitialCountOffset, 100);

        for (int i = 0; i < 99; i++)
        {
            cpu.AccountIdleCycles(1);
            scheduler.RunDue(cpu.Cycles);
        }
        Assert.False(apic.HasPendingInterrupt);

        cpu.AccountIdleCycles(1);
        scheduler.RunDue(cpu.Cycles);
        Assert.True(apic.HasPendingInterrupt);
        Assert.Equal(0x22, apic.GetPendingVector());

        // One-shot: Current Count stays at zero, nothing further ever fires.
        Assert.Equal(0u, apic.ReadDword(CurrentCountOffset));
        cpu.AccountIdleCycles(1000);
        scheduler.RunDue(cpu.Cycles);
        Assert.False(apic.HasPendingInterrupt);
    }

    [Fact]
    public void PeriodicTimerReloadsAtTheRightEmulatedPeriod()
    {
        var (apic, _, cpu, scheduler) = Build(CpuModel.PentiumMmx);
        apic.ImcrApicMode = true;
        apic.WriteDword(SvrOffset, apic.ReadDword(SvrOffset) | SvrSoftwareEnableBit);
        apic.WriteDword(LvtTimerOffset, 0x22 | LvtPeriodicBit);
        apic.WriteDword(DivideConfigOffset, 0x0B);   // divide by 1
        apic.WriteDword(InitialCountOffset, 50);

        int fires = 0;
        for (int cycle = 1; cycle <= 200; cycle++)
        {
            cpu.AccountIdleCycles(1);
            scheduler.RunDue(cpu.Cycles);
            if (apic.HasPendingInterrupt)
            {
                apic.GetPendingVector();
                apic.WriteDword(EoiOffset, 0);   // a real timer ISR always does this before the next tick can win priority
                fires++;
            }
        }

        Assert.Equal(4, fires);   // 200 cycles / 50-cycle period
    }

    [Fact]
    public void MaskingTheTimerStopsDeliveryButTheCountKeepsRunning()
    {
        var (apic, _, cpu, scheduler) = Build(CpuModel.PentiumMmx);
        apic.ImcrApicMode = true;
        apic.WriteDword(SvrOffset, apic.ReadDword(SvrOffset) | SvrSoftwareEnableBit);
        apic.WriteDword(LvtTimerOffset, 0x22 | LvtMaskBit);
        apic.WriteDword(DivideConfigOffset, 0x0B);
        apic.WriteDword(InitialCountOffset, 10);

        for (int i = 0; i < 10; i++)
        {
            cpu.AccountIdleCycles(1);
            scheduler.RunDue(cpu.Cycles);
        }

        Assert.False(apic.HasPendingInterrupt);   // masked: nothing was ever posted
        Assert.Equal(0u, apic.ReadDword(CurrentCountOffset));   // but the count still ran down to zero
    }

    // ---- snapshot round trip, with the APIC enabled and a pending timer -----

    [Fact]
    public void SnapshotRoundTripsEveryRegisterAndThePendingTimer()
    {
        var (apic, _, cpu, scheduler) = Build(CpuModel.PentiumMmx);
        apic.ImcrApicMode = true;
        apic.WriteDword(SvrOffset, apic.ReadDword(SvrOffset) | SvrSoftwareEnableBit);
        apic.WriteDword(TprOffset, 0x10);   // below the timer's own class (0x22 & F0 = 0x20), or PPR would block it
        apic.WriteDword(LvtTimerOffset, 0x22 | LvtPeriodicBit);
        apic.WriteDword(DivideConfigOffset, 0x0B);
        apic.WriteDword(InitialCountOffset, 1000);

        // The timer is not due yet (200 < 1000 cycles), but its bookkeeping
        // (start cycle, divisor) is already live; capturing mid-flight is
        // the point of this test, not waiting for the vector to fire first.
        for (int i = 0; i < 200; i++)
        {
            cpu.AccountIdleCycles(1);
            scheduler.RunDue(cpu.Cycles);
        }

        byte[] captured = apic.Capture();

        var (restoredApic, _, restoredCpu, restoredScheduler) = Build(CpuModel.PentiumMmx);
        restoredApic.Restore(captured);

        // ImcrApicMode is chipset state, not part of LocalApic's own
        // section (see SnapshotLength's summary) -- a real restore applies
        // it from Machine's "IMCR" section, which this bare unit test
        // stands in for directly.
        restoredApic.ImcrApicMode = true;

        Assert.Equal(0x10u, restoredApic.ReadDword(TprOffset));
        Assert.Equal(apic.ReadDword(LvtTimerOffset), restoredApic.ReadDword(LvtTimerOffset));
        Assert.NotEqual(0u, restoredApic.ReadDword(CurrentCountOffset));   // still ticking, not already expired

        // 800 cycles remained when this was captured (1000 - 200); the
        // restored timer must still be due at exactly that distance from
        // whatever cycle the receiving machine's own clock is at -- not at
        // the file's absolute cycle count, which the receiving CPU's own
        // clock generally disagrees with (Cpu.Cycles is not itself part of
        // any snapshot).
        for (int i = 0; i < 799; i++)
        {
            restoredCpu.AccountIdleCycles(1);
            restoredScheduler.RunDue(restoredCpu.Cycles);
        }

        Assert.False(restoredApic.HasPendingInterrupt);   // one cycle short of due

        restoredCpu.AccountIdleCycles(1);
        restoredScheduler.RunDue(restoredCpu.Cycles);

        Assert.True(restoredApic.HasPendingInterrupt);
        Assert.Equal(0x22, restoredApic.GetPendingVector());
    }

    [Fact]
    public void SnapshotRestoreReDerivesTheMmioMappingWithoutResettingRestoredRegisters()
    {
        var (apic, _, _, _) = Build(CpuModel.PentiumMmx);
        apic.WriteDword(SvrOffset, 0x142);   // a distinctive, non-reset value
        byte[] captured = apic.Capture();

        var (restoredApic, _, restoredCpu, _) = Build(CpuModel.PentiumMmx);
        restoredCpu.State.SelectModel(CpuModel.PentiumMmx);
        restoredApic.Restore(captured);

        // Restore must not have run the 0-to-1 hardware-enable reset over
        // the payload it just read: the distinctive SVR value survives.
        Assert.Equal(0x142u | 0xFu, restoredApic.ReadDword(SvrOffset));
    }

    // ---- Machine-level regression: every existing machine stays byte-identical ----

    /// <summary>
    /// Neither board's reset state (IMCR = 0 where an IMCR exists at all,
    /// and no CPU model this codebase carries before P4.4 has an on-die
    /// APIC unless the caller asks for one) changes how the PIT's tick and
    /// the keyboard's IRQ 1 reach the CPU: <c>Cpu.Interrupts</c> answers
    /// from the plain 8259 pair exactly as it always did.
    /// </summary>
    [Theory]
    [InlineData(Chipset.I430Fx)]
    [InlineData(Chipset.I430Tx)]
    public void ResetStateDeliversThePitAndKeyboardExactlyAsBeforeThisPackage(Chipset chipset)
    {
        using var machine = new Machine(chipset: chipset);
        machine.RunInstructions(200);

        Assert.False(machine.Apic.HasPendingInterrupt);
        machine.Interrupts.RaiseIrq(0);   // the PIT's own tick
        Assert.True(machine.Apic.HasPendingInterrupt);
        Assert.Equal(0x08, machine.Apic.GetPendingVector());

        // A non-specific EOI, exactly as the BIOS's own IRQ entry sends
        // (BiosRom.Mp.cs's sibling, WriteIrqEntry): IRQ 0 must no longer be
        // in service before IRQ 1's lower priority is ever tested, or the
        // 8259's own fully-nested mode blocks it regardless of the APIC.
        machine.Interrupts.Master.WriteByte(0x20, 0x20);

        machine.Interrupts.RaiseIrq(1);   // the keyboard
        Assert.True(machine.Apic.HasPendingInterrupt);
        Assert.Equal(0x09, machine.Apic.GetPendingVector());
    }

    /// <summary>
    /// The same guarantee through <c>MachineSnapshot</c>'s full sectioned
    /// round trip, with the APIC actually enabled and a periodic timer
    /// mid-flight -- not just LocalApic's own bare Capture/Restore.
    /// </summary>
    [Fact]
    public void MachineSnapshotRoundTripsAnEnabledApicWithAPendingTimer()
    {
        using var machine = new Machine(chipset: Chipset.I430Tx, initialCpuModel: CpuModel.PentiumMmx);
        machine.RunInstructions(200);

        machine.Imcr!.WriteByte(0x22, 0x70);
        machine.Imcr.WriteByte(0x23, 1);
        machine.Apic.WriteDword(SvrOffset, machine.Apic.ReadDword(SvrOffset) | SvrSoftwareEnableBit);
        machine.Apic.WriteDword(LvtTimerOffset, 0x30 | LvtPeriodicBit);
        machine.Apic.WriteDword(DivideConfigOffset, 0x0B);
        machine.Apic.WriteDword(InitialCountOffset, 5000);

        MachineSnapshot snapshot = MachineSnapshot.Capture(machine);

        // Disturb the live machine so the assertions below only pass if the
        // snapshot, not incidental state, is what answers them.
        machine.Imcr.WriteByte(0x22, 0x70);
        machine.Imcr.WriteByte(0x23, 0);
        machine.Apic.WriteDword(LvtTimerOffset, 0x99);
        machine.RunInstructions(2000);

        snapshot.RestoreTo(machine);

        Assert.True(machine.Apic.ImcrApicMode);
        Assert.True(machine.Imcr.ApicMode);
        Assert.Equal(0x30u | LvtPeriodicBit, machine.Apic.ReadDword(LvtTimerOffset));

        // A generous margin past the 5000-cycle period rearmed at restore:
        // RunInstructions stops exactly at the next scheduled event, so
        // this reaches the timer's due cycle rather than looping past it.
        machine.RunInstructions(20000);

        Assert.True(machine.Apic.HasPendingInterrupt);
        Assert.Equal(0x30, machine.Apic.GetPendingVector());
    }

    /// <summary>
    /// Coordinator ruling, item 7's own required test: on an i430FX board --
    /// no IMCR device at all, so <c>ImcrApicMode</c> can never become true --
    /// a hardware- and software-enabled local APIC's own timer still fires.
    /// The revised ruling delivers the unit's own interrupts regardless of
    /// IMCR; only the legacy 8259's path depends on it.
    /// </summary>
    [Theory]
    [InlineData(CpuModel.PentiumMmx)]
    [InlineData(CpuModel.PentiumII)]
    public void LapicTimerIsDeliveredOnAnI430FxBoardEvenThoughThereIsNoImcr(CpuModel model)
    {
        using var machine = new Machine(chipset: Chipset.I430Fx, initialCpuModel: model);
        Assert.Null(machine.Imcr);
        Assert.False(machine.Apic.ImcrApicMode);

        machine.Apic.WriteDword(SvrOffset, machine.Apic.ReadDword(SvrOffset) | SvrSoftwareEnableBit);
        machine.Apic.WriteDword(LvtTimerOffset, 0x33);
        machine.Apic.WriteDword(DivideConfigOffset, 0x0B);
        machine.Apic.WriteDword(InitialCountOffset, 1000);

        machine.RunInstructions(5000);

        Assert.True(machine.Apic.HasPendingInterrupt);
        Assert.Equal(0x33, machine.Apic.GetPendingVector());
    }

    // ---- second independent review, item N4: CpuModel resets the LAPIC ------

    /// <summary>
    /// Switching <c>Machine.CpuModel</c> is not a full reboot, but it must
    /// still reset the local APIC exactly as one would: a stale timer LVT
    /// or a stale pending vector must not survive pretending to swap chips.
    /// </summary>
    [Fact]
    public void ChangingCpuModelResetsTheLapicSoNoStaleTimerVectorSurvives()
    {
        using var machine = new Machine(chipset: Chipset.I430Tx, initialCpuModel: CpuModel.PentiumMmx);
        machine.Apic.WriteDword(SvrOffset, machine.Apic.ReadDword(SvrOffset) | SvrSoftwareEnableBit);
        machine.Apic.WriteDword(LvtTimerOffset, 0x44 | LvtPeriodicBit);
        machine.Apic.WriteDword(DivideConfigOffset, 0x0B);
        machine.Apic.WriteDword(InitialCountOffset, 10);
        machine.Apic.DeliverFixed(0x55, levelTriggered: false);   // an own-IRR vector already latched

        machine.CpuModel = CpuModel.PentiumII;

        // Every register is back at its power-on value: no stale timer LVT,
        // no stale IRR bit, nothing pending from before the switch.
        Assert.Equal(0x00010000u, machine.Apic.ReadDword(LvtTimerOffset));
        Assert.Equal(0u, machine.Apic.ReadDword(InitialCountOffset));
        Assert.Equal(0u, machine.Apic.ReadDword(CurrentCountOffset));
        Assert.False(machine.Apic.HasPendingInterrupt);
    }

    // ---- second independent review, item N6: SVR 1-to-0 masks every LVT -----

    [Fact]
    public void SvrSoftwareDisableMasksEveryStoredLvtSoReEnablingDoesNotReviveThem()
    {
        var (apic, _, _, _) = Build(CpuModel.PentiumMmx);
        apic.WriteDword(SvrOffset, apic.ReadDword(SvrOffset) | SvrSoftwareEnableBit);
        apic.WriteDword(LvtLint0Offset, LvtDeliveryModeExtInt);   // unmasked while enabled
        apic.WriteDword(LvtTimerOffset, 0x22);                    // unmasked while enabled

        // The 1-to-0 transition itself must OR the mask bit into storage,
        // not merely change how reads/writes are displayed from now on.
        apic.WriteDword(SvrOffset, apic.ReadDword(SvrOffset) & ~SvrSoftwareEnableBit);

        // Re-enabling SVR does not reveal LINT0 or the timer LVT as unmasked
        // again: the transition already latched them masked in storage.
        apic.WriteDword(SvrOffset, apic.ReadDword(SvrOffset) | SvrSoftwareEnableBit);

        Assert.Equal(LvtMaskBit, apic.ReadDword(LvtLint0Offset) & LvtMaskBit);
        Assert.Equal(LvtMaskBit, apic.ReadDword(LvtTimerOffset) & LvtMaskBit);
    }
}
