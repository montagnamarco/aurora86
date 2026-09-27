using Aurora386.Core;
using Aurora386.Core.Cpu;
using Aurora386.Core.Devices.Apic;
using Aurora386.Core.Devices.Chipset;
using Aurora386.Core.Devices.Interrupts;
using Aurora386.Core.Memory;
using Aurora386.Core.Timing;

namespace Aurora386.Tests;

/// <summary>
/// Work package P4.4: the Intel 82093AA I/O APIC, read directly from
/// datasheet 290566-001 (plans/specs/apic.md §2) -- present only on the
/// i430TX/PIIX4 board, fixed at FEC00000h, twenty-four redirection table
/// entries reached through IOREGSEL/IOWIN.
/// </summary>
public class IoApicTests
{
    private const uint IoRegSelOffset = 0x00;
    private const uint IoWinOffset = 0x10;
    private const uint IoApicIdIndex = 0x00;
    private const uint IoApicVerIndex = 0x01;
    private const uint IoApicArbIndex = 0x02;
    private const uint RedirTableBase = 0x10;

    private const uint EntryMaskBit = 1u << 16;
    private const uint EntryTriggerLevelBit = 1u << 15;
    private const uint EntryDeliveryModeMask = 0x700;
    private const uint EntryDeliveryModeExtInt = 0x700;   // bits 10:8 = 111
    private const uint LocalApicSvrOffset = 0x0F0;
    private const uint LocalApicEoiOffset = 0x0B0;
    private const uint LocalApicIsrBase = 0x100;
    private const uint LocalApicSvrSoftwareEnableBit = 1u << 8;
    private const uint EntryRemoteIrrBit = 1u << 14;

    private static (IoApic IoApic, LocalApic Apic, InterruptController Pic) Build()
    {
        var bus = new PhysicalMemoryBus(1024 * 1024, 0xFEE01000);
        var cpu = new Cpu(bus);
        cpu.State.SelectModel(CpuModel.PentiumMmx);
        var pic = new InterruptController();
        var scheduler = new Scheduler();
        var apic = new LocalApic(pic, cpu, bus, scheduler, () => cpu.Cycles);
        cpu.Msrs.ApicBaseChanged += apic.OnApicBaseChanged;
        apic.OnApicBaseChanged(cpu.Msrs.ApicBaseRaw);
        apic.ImcrApicMode = true;
        cpu.Interrupts = apic;

        var ioApic = new IoApic(apic);
        apic.LevelTriggeredEoi += ioApic.ClearRemoteIrr;

        return (ioApic, apic, pic);
    }

    private static uint ReadIndirect(IoApic ioApic, uint index)
    {
        ioApic.WriteDword(IoRegSelOffset, index);
        return ioApic.ReadDword(IoWinOffset);
    }

    private static void WriteIndirect(IoApic ioApic, uint index, uint value)
    {
        ioApic.WriteDword(IoRegSelOffset, index);
        ioApic.WriteDword(IoWinOffset, value);
    }

    // ---- test cases 4-6: reset values ----------------------------------------

    [Fact]
    public void IoApicVerReads00170011()
    {
        var (ioApic, _, _) = Build();
        Assert.Equal(0x00170011u, ReadIndirect(ioApic, IoApicVerIndex));
    }

    [Fact]
    public void IoApicIdResetsToZero()
    {
        var (ioApic, _, _) = Build();
        Assert.Equal(0u, ReadIndirect(ioApic, IoApicIdIndex));
    }

    [Fact]
    public void IoApicArbIsLoadedFromId()
    {
        var (ioApic, _, _) = Build();
        WriteIndirect(ioApic, IoApicIdIndex, 0x03000000);
        Assert.Equal(0x03000000u, ReadIndirect(ioApic, IoApicArbIndex));
    }

    [Fact]
    public void RedirectionEntriesResetToMaskOnly()
    {
        var (ioApic, _, _) = Build();
        for (uint pin = 0; pin < IoApic.PinCount; pin++)
        {
            uint low = ReadIndirect(ioApic, RedirTableBase + pin * 2);
            uint high = ReadIndirect(ioApic, RedirTableBase + pin * 2 + 1);
            Assert.Equal(EntryMaskBit, low);
            Assert.Equal(0u, high);
        }
    }

    // ---- delivery, masking and Remote IRR ------------------------------------

    [Fact]
    public void AnUnmaskedEdgeEntryDeliversItsVectorOnAssertion()
    {
        var (ioApic, apic, _) = Build();
        apic.WriteDword(LocalApicSvrOffset, apic.ReadDword(LocalApicSvrOffset) | LocalApicSvrSoftwareEnableBit);   // SVR software-enable
        WriteIndirect(ioApic, RedirTableBase + 2 * 2, 0x30);         // pin 2, vector 30h, edge, unmasked

        ioApic.PulseLine(2);

        Assert.True(apic.HasPendingInterrupt);
        Assert.Equal(0x30, apic.GetPendingVector());
    }

    /// <summary>
    /// Coordinator ruling, item 2: every ISA-fed pulse is a fresh edge,
    /// regardless of any earlier one -- the PIT calls RaiseIrq over and over
    /// without ever lowering the line, and each tick must still get through.
    /// </summary>
    [Fact]
    public void ConsecutivePulsesOnAnIsaPinEachDeliverAgain()
    {
        var (ioApic, apic, _) = Build();
        apic.WriteDword(LocalApicSvrOffset, apic.ReadDword(LocalApicSvrOffset) | LocalApicSvrSoftwareEnableBit);
        WriteIndirect(ioApic, RedirTableBase + 2 * 2, 0x30);

        ioApic.PulseLine(2);
        Assert.Equal(0x30, apic.GetPendingVector());
        apic.WriteDword(LocalApicEoiOffset, 0);   // an ordinary handler's EOI, ending the first delivery

        ioApic.PulseLine(2);
        Assert.True(apic.HasPendingInterrupt);
        Assert.Equal(0x30, apic.GetPendingVector());
    }

    /// <summary>Test case 13: a masked entry drops edges and levels with no pending state.</summary>
    [Fact]
    public void AMaskedEntryDropsAnEdgeWithNoPendingState()
    {
        var (ioApic, apic, _) = Build();
        apic.WriteDword(LocalApicSvrOffset, apic.ReadDword(LocalApicSvrOffset) | LocalApicSvrSoftwareEnableBit);
        WriteIndirect(ioApic, RedirTableBase + 2 * 2, 0x30 | EntryMaskBit);

        ioApic.PulseLine(2);
        Assert.False(apic.HasPendingInterrupt);

        // Unmasking afterwards must not deliver the dropped edge: nothing
        // was ever recorded to deliver later.
        WriteIndirect(ioApic, RedirTableBase + 2 * 2, 0x30);
        Assert.False(apic.HasPendingInterrupt);
    }

    [Fact]
    public void AMaskedLevelEntryDropsTheLevelWithNoRemoteIrr()
    {
        var (ioApic, apic, _) = Build();
        apic.WriteDword(LocalApicSvrOffset, apic.ReadDword(LocalApicSvrOffset) | LocalApicSvrSoftwareEnableBit);
        WriteIndirect(ioApic, RedirTableBase + 16 * 2, 0x40 | EntryMaskBit | EntryTriggerLevelBit);

        ioApic.SetLevel(16, true);
        Assert.False(apic.HasPendingInterrupt);
        Assert.Equal(0u, ReadIndirect(ioApic, RedirTableBase + 16 * 2) & EntryRemoteIrrBit);
    }

    /// <summary>
    /// Second independent review, item N3: an edge-triggered entry reached
    /// through <see cref="IoApic.SetLevel"/> (an unusual way to configure a
    /// PIRQ pin, but not forbidden) delivers on the rising edge exactly like
    /// <see cref="IoApic.PulseLine"/> -- Remote IRR is meaningless for edge
    /// mode and must never be set by this path either.
    /// </summary>
    [Fact]
    public void SetLevelOnAnEdgeTriggeredEntryDeliversWithoutSettingRemoteIrr()
    {
        var (ioApic, apic, _) = Build();
        apic.WriteDword(LocalApicSvrOffset, apic.ReadDword(LocalApicSvrOffset) | LocalApicSvrSoftwareEnableBit);
        WriteIndirect(ioApic, RedirTableBase + 16 * 2, 0x40);   // pin 16, edge (bit 15 clear), unmasked

        ioApic.SetLevel(16, true);

        Assert.True(apic.HasPendingInterrupt);
        Assert.Equal(0x40, apic.GetPendingVector());
        Assert.Equal(0u, ReadIndirect(ioApic, RedirTableBase + 16 * 2) & EntryRemoteIrrBit);

        // A repeated call with the same asserted state (PciInterruptRouter
        // re-asserts every still-held link on every change to any of them)
        // is not a new edge and delivers nothing further.
        ioApic.SetLevel(16, true);
        Assert.False(apic.HasPendingInterrupt);

        // But a genuine deassert-then-reassert cycle is a fresh edge --
        // ended first with an EOI, since the earlier delivery of the same
        // vector is still in service and would otherwise mask its own class.
        apic.WriteDword(LocalApicEoiOffset, 0);
        ioApic.SetLevel(16, false);
        ioApic.SetLevel(16, true);
        Assert.True(apic.HasPendingInterrupt);
    }

    /// <summary>Test case 7: EOI of a level vector clears Remote IRR.</summary>
    [Fact]
    public void EoiOfALevelVectorClearsRemoteIrr()
    {
        var (ioApic, apic, _) = Build();
        apic.WriteDword(LocalApicSvrOffset, apic.ReadDword(LocalApicSvrOffset) | LocalApicSvrSoftwareEnableBit);
        WriteIndirect(ioApic, RedirTableBase + 16 * 2, 0x40 | EntryTriggerLevelBit);   // pin 16, level, unmasked

        ioApic.SetLevel(16, true);
        Assert.Equal(EntryRemoteIrrBit, ReadIndirect(ioApic, RedirTableBase + 16 * 2) & EntryRemoteIrrBit);

        Assert.True(apic.HasPendingInterrupt);
        apic.GetPendingVector();
        ioApic.SetLevel(16, false);   // the source stops holding the line before the EOI arrives
        apic.WriteDword(LocalApicEoiOffset, 0);

        Assert.Equal(0u, ReadIndirect(ioApic, RedirTableBase + 16 * 2) & EntryRemoteIrrBit);
    }

    [Fact]
    public void ReassertingALevelLineWhileRemoteIrrIsStillSetDeliversNothingFurther()
    {
        var (ioApic, apic, _) = Build();
        apic.WriteDword(LocalApicSvrOffset, apic.ReadDword(LocalApicSvrOffset) | LocalApicSvrSoftwareEnableBit);
        WriteIndirect(ioApic, RedirTableBase + 16 * 2, 0x40 | EntryTriggerLevelBit);

        ioApic.SetLevel(16, true);
        apic.GetPendingVector();   // moved to ISR; Remote IRR still set until EOI

        ioApic.SetLevel(16, false);
        ioApic.SetLevel(16, true);   // a second source on the same shared link, say

        Assert.False(apic.HasPendingInterrupt);   // nothing new queued: Remote IRR already latched
    }

    /// <summary>Item 4: an EOI that clears Remote IRR while the line is still held high re-delivers immediately -- the source never stopped asking.</summary>
    [Fact]
    public void EoiRedeliversWhenTheLineIsStillHighAfterRemoteIrrClears()
    {
        var (ioApic, apic, _) = Build();
        apic.WriteDword(LocalApicSvrOffset, apic.ReadDword(LocalApicSvrOffset) | LocalApicSvrSoftwareEnableBit);
        WriteIndirect(ioApic, RedirTableBase + 16 * 2, 0x40 | EntryTriggerLevelBit);

        ioApic.SetLevel(16, true);
        Assert.Equal(0x40, apic.GetPendingVector());

        // The source is still asserting when the guest's handler sends EOI:
        // Remote IRR clears, and since the level never actually went away,
        // the entry re-latches and delivers again on the spot.
        apic.WriteDword(LocalApicEoiOffset, 0);

        Assert.True(apic.HasPendingInterrupt);
        Assert.Equal(0x40, apic.GetPendingVector());
        Assert.Equal(EntryRemoteIrrBit, ReadIndirect(ioApic, RedirTableBase + 16 * 2) & EntryRemoteIrrBit);
    }

    /// <summary>Item 4: unmasking a level entry whose line is already high delivers on the spot, rather than waiting for an edge a level source may never produce again.</summary>
    [Fact]
    public void UnmaskingALevelEntryWithTheLineAlreadyHighDeliversAtOnce()
    {
        var (ioApic, apic, _) = Build();
        apic.WriteDword(LocalApicSvrOffset, apic.ReadDword(LocalApicSvrOffset) | LocalApicSvrSoftwareEnableBit);
        WriteIndirect(ioApic, RedirTableBase + 16 * 2, 0x40 | EntryTriggerLevelBit | EntryMaskBit);

        ioApic.SetLevel(16, true);
        Assert.False(apic.HasPendingInterrupt);   // masked: dropped, per test case 13

        WriteIndirect(ioApic, RedirTableBase + 16 * 2, 0x40 | EntryTriggerLevelBit);   // unmask

        Assert.True(apic.HasPendingInterrupt);
        Assert.Equal(0x40, apic.GetPendingVector());
    }

    // ---- delivery status always synchronous (decision A6) --------------------

    [Fact]
    public void DeliveryStatusAlwaysReadsZero()
    {
        var (ioApic, _, _) = Build();
        WriteIndirect(ioApic, RedirTableBase, 0xFFFFFFFF);
        Assert.Equal(0u, ReadIndirect(ioApic, RedirTableBase) & (1u << 12));
    }

    // ---- ExtINT via the I/O APIC: "virtual wire via the I/O APIC" -----------

    /// <summary>
    /// Second independent review, item N2: an entry programmed for ExtINT
    /// delivery -- the Linux <c>check_timer()</c> fallback some real BIOSes
    /// need, where INTIN2 (the PIT's own override pin) is left in ExtINT
    /// mode instead of Fixed -- never touches this unit's own IRR/ISR/PPR at
    /// all. The 8259 is asked for its own vector only at acceptance
    /// (<c>LocalApic.GetPendingVector</c>), never at the edge itself, and
    /// several ticks in a row are each served with nothing but an ordinary
    /// 8259 EOI: the LAPIC's own ISR stays empty throughout.
    /// </summary>
    [Fact]
    public void ExtIntEntryAsksThe8259OnlyAtAcceptanceAndNeedsOnlyItsOwnEoi()
    {
        var (ioApic, apic, pic) = Build();
        WriteIndirect(ioApic, RedirTableBase + 2 * 2, EntryDeliveryModeExtInt);   // INTIN2, unmasked

        // The BIOS's own ICW sequence, so RaiseIrq(0) answers a known vector.
        pic.Master.WriteByte(0x20, 0x11);
        pic.Master.WriteByte(0x21, 0x08);
        pic.Master.WriteByte(0x21, 0x04);
        pic.Master.WriteByte(0x21, 0x01);
        pic.Master.WriteByte(0x21, 0x00);

        for (int tick = 0; tick < 3; tick++)
        {
            // The same two calls Interrupts.RaiseIrq(0) makes on a real
            // machine: the 8259 sees the edge directly, and so does INTIN2.
            pic.RaiseIrq(0);
            ioApic.PulseLine(2);

            // Not acknowledged at edge time.
            Assert.False(pic.HasInterruptInService);

            Assert.True(apic.HasPendingInterrupt);
            int vector = apic.GetPendingVector();
            Assert.Equal(0x08, vector);          // the 8259's own vector, not the entry's (irrelevant) field
            Assert.True(pic.HasInterruptInService);   // acknowledged only now, at acceptance

            pic.Master.WriteByte(0x20, 0x20);    // an ordinary, non-specific 8259 EOI -- nothing else
        }

        // The LAPIC's own in-service register was never touched by any of it.
        Assert.Equal(0u, apic.ReadDword(LocalApicIsrBase));
    }

    /// <summary>ExtINT via the I/O APIC delivers nothing at all while the 8259 itself has nothing pending -- "when the LAPIC can't accept" (item N2).</summary>
    [Fact]
    public void ExtIntEntryDeliversNothingWhileThe8259HasNoPendingLine()
    {
        var (ioApic, apic, _) = Build();
        WriteIndirect(ioApic, RedirTableBase + 2 * 2, EntryDeliveryModeExtInt);

        ioApic.PulseLine(2);   // the pin pulses, but nothing ever raised it on the 8259 itself

        Assert.False(apic.HasPendingInterrupt);
        Assert.Equal(-1, apic.GetPendingVector());
    }

    // ---- Machine-level wiring: both the 8259 and the I/O APIC see every edge/level ----

    /// <summary>
    /// Test case 8, in the shape the contract asks for: program the I/O
    /// APIC's INTIN2 to a vector, mask the legacy 8259 entirely, and a
    /// PIT-shaped edge still reaches the CPU through the local APIC.
    /// <c>Interrupts.RaiseIrq(0)</c> is exactly the call
    /// <c>Devices.Timing.Pit8253</c> itself makes on every tick, so driving
    /// it directly exercises the identical wiring without needing to count
    /// down the timer's own divisor first.
    /// </summary>
    [Fact]
    public void IrqZeroArrivesOnIntin2()
    {
        using var machine = new Machine(chipset: Chipset.I430Tx, initialCpuModel: CpuModel.PentiumMmx);
        machine.RunInstructions(200);   // completes POST: IMCR reset, redirection table reset, 8259 programmed

        machine.Imcr!.WriteByte(0x22, 0x70);
        machine.Imcr.WriteByte(0x23, 1);   // symmetric mode
        WriteIndirect(machine.IoApic!, RedirTableBase + 2 * 2, 0x41);   // INTIN2, vector 41h, unmasked
        machine.Apic.WriteDword(LocalApicSvrOffset, machine.Apic.ReadDword(LocalApicSvrOffset) | LocalApicSvrSoftwareEnableBit);   // SVR enable
        machine.Interrupts.Master.WriteByte(0x21, 0xFF);   // mask every legacy 8259 line

        machine.Interrupts.RaiseIrq(0);

        Assert.True(machine.Apic.HasPendingInterrupt);
        Assert.Equal(0x41, machine.Apic.GetPendingVector());

        // Two or more consecutive ticks (the contract's own wording): an
        // ordinary EOI, exactly as a real handler sends, then the PIT's
        // next tick must still get through -- the bug this guards against
        // left INTIN2 permanently latched after the very first one.
        machine.Apic.WriteDword(LocalApicEoiOffset, 0);
        machine.Interrupts.RaiseIrq(0);

        Assert.True(machine.Apic.HasPendingInterrupt);
        Assert.Equal(0x41, machine.Apic.GetPendingVector());
    }

    [Fact]
    public void PirqALinkArrivesOnIntin16()
    {
        using var machine = new Machine(chipset: Chipset.I430Tx, initialCpuModel: CpuModel.PentiumMmx);
        machine.RunInstructions(200);

        machine.Imcr!.WriteByte(0x22, 0x70);
        machine.Imcr.WriteByte(0x23, 1);
        WriteIndirect(machine.IoApic!, RedirTableBase + 16 * 2, 0x50 | EntryTriggerLevelBit);
        machine.Apic.WriteDword(LocalApicSvrOffset, machine.Apic.ReadDword(LocalApicSvrOffset) | LocalApicSvrSoftwareEnableBit);

        // Device 2, pin A is link 0 (PIRQ A), wired to INTIN16. A bare
        // machine with no expansion cards fitted has nothing routing
        // through this link by default (RouteDefaults only routes a
        // function that actually declares an interrupt pin), so this test
        // routes it explicitly first -- the same way
        // ChipsetTests.RomRoutingTableNamesPiix4AndIncludesItsUsbPirqDWire
        // programs a link's IRQ directly rather than relying on a card.
        machine.Pci.InterruptRouter!.Route(0x0010, 1, 11);
        machine.Pci.InterruptRouter!.SetInterrupt(0x0010, 1, true);

        Assert.True(machine.Apic.HasPendingInterrupt);
        Assert.Equal(0x50, machine.Apic.GetPendingVector());
    }

    /// <summary>
    /// Item 4: two devices whose links (A and C) happen to share the same
    /// routed legacy IRQ (11, the BIOS default for both) still reach their
    /// own, distinct I/O APIC pins -- PciInterruptRouter now drives
    /// INTIN16-19 from its own per-link set, not by reconstructing which
    /// link owns which IRQ from the aggregate the 8259 sees.
    /// </summary>
    [Fact]
    public void TwoLinksSharingAnIrqEachReachOnlyTheirOwnIntin()
    {
        using var machine = new Machine(chipset: Chipset.I430Tx, initialCpuModel: CpuModel.PentiumMmx);
        machine.RunInstructions(200);

        WriteIndirect(machine.IoApic!, RedirTableBase + 16 * 2, 0x50 | EntryTriggerLevelBit);   // INTIN16: link A
        WriteIndirect(machine.IoApic!, RedirTableBase + 18 * 2, 0x52 | EntryTriggerLevelBit);   // INTIN18: link C

        // Device 2 pin A -> link 0 (A); device 4 pin A -> link 2 (C)
        // (PciInterruptRouter.Link's own (device-2+pin-1)&3 rule). Both
        // links keep the BIOS default routing of IRQ 11.
        machine.Pci.InterruptRouter!.SetInterrupt(0x0010, 1, true);   // link A only

        uint intin16 = ReadIndirect(machine.IoApic!, RedirTableBase + 16 * 2);
        uint intin18 = ReadIndirect(machine.IoApic!, RedirTableBase + 18 * 2);
        Assert.Equal(EntryRemoteIrrBit, intin16 & EntryRemoteIrrBit);
        Assert.Equal(0u, intin18 & EntryRemoteIrrBit);   // link C's own pin, untouched
    }

    /// <summary>Item 4: disabling a link's 60h-63h routing (or steering it to a non-allowed IRQ) does not stop it reaching the I/O APIC, exactly as on real PIIX silicon.</summary>
    [Fact]
    public void ADisabledLinkStillReachesTheIoApic()
    {
        using var machine = new Machine(chipset: Chipset.I430Tx, initialCpuModel: CpuModel.PentiumMmx);
        machine.RunInstructions(200);

        WriteIndirect(machine.IoApic!, RedirTableBase + 16 * 2, 0x50 | EntryTriggerLevelBit);
        machine.Pci.WriteConfiguration(8, 0x60, 1, 0x80);   // link A (offset 60h): disabled (bit 7)

        machine.Pci.InterruptRouter!.SetInterrupt(0x0010, 1, true);

        Assert.Equal(EntryRemoteIrrBit,
            ReadIndirect(machine.IoApic!, RedirTableBase + 16 * 2) & EntryRemoteIrrBit);
    }

    /// <summary>Item 4: re-routing a link's legacy IRQ while it is asserted does not disturb the I/O APIC pin, which answers to the link, not to the routing.</summary>
    [Fact]
    public void ReroutingALinkWhileAssertedLeavesTheIoApicPinAlone()
    {
        using var machine = new Machine(chipset: Chipset.I430Tx, initialCpuModel: CpuModel.PentiumMmx);
        machine.RunInstructions(200);

        WriteIndirect(machine.IoApic!, RedirTableBase + 16 * 2, 0x50 | EntryTriggerLevelBit);
        machine.Pci.InterruptRouter!.SetInterrupt(0x0010, 1, true);
        Assert.Equal(EntryRemoteIrrBit,
            ReadIndirect(machine.IoApic!, RedirTableBase + 16 * 2) & EntryRemoteIrrBit);

        machine.Pci.InterruptRouter!.Route(0x0010, 1, 10);   // moved from IRQ 11 to IRQ 10

        Assert.Equal(EntryRemoteIrrBit,
            ReadIndirect(machine.IoApic!, RedirTableBase + 16 * 2) & EntryRemoteIrrBit);
    }
}
