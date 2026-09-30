using Aurora86.Core.Memory;

namespace Aurora86.Core.Devices.Apic;

/// <summary>
/// The Intel 82093AA I/O APIC, read directly from datasheet 290566-001
/// (plans/specs/apic.md §2). Present only on the i430TX/PIIX4 board
/// (coordinator decision A2) at a fixed physical address, FEC00000h --
/// there is no XBCS chip-select or relocation register modelled (decision
/// A3, dated 2026-09-27: settle those from the PIIX3/4 datasheet
/// 290550-001 before advertising them).
///
/// Twenty-four redirection table entries take the place of the 8259's
/// sixteen fixed-priority lines: pin 2 carries the timer (IRQ 0's
/// historical override), pin 1 the keyboard, pins 3-11/14/15 the rest of
/// the ISA lines, and pins 16-19 the four PIIX PIRQ links -- see
/// <c>Machine</c> for the wiring, which drives both this device and the
/// legacy 8259 pair from the very same edges and levels so that whichever
/// one the local APIC is currently listening to sees the interrupt.
///
/// <b>Coordinator ruling (independent review, item 2):</b> the ISA pins
/// (INTIN0-15) are edge inputs fed by the same <c>RaiseIrq</c> events the
/// 8259 sees -- every call is a fresh rising edge (<see cref="PulseLine"/>),
/// never latched persistently, matching the many ISA sources (the PIT above
/// all) that raise their line over and over without ever lowering it.
///
/// <b>Item 4:</b> the four PIRQ pins (INTIN16-19) are genuinely level and
/// keep their asserted state (<see cref="SetLevel"/>): a source keeps
/// holding its line until it is serviced, so an EOI that clears Remote IRR
/// while the line is still high re-delivers immediately, and unmasking a
/// level entry while its line is high delivers on the spot rather than
/// waiting for the next edge that may never come.
///
/// Delivery is a direct call into the target <see cref="LocalApic"/>'s IRR
/// (<see cref="LocalApic.DeliverFixed"/>): there is no serial APIC bus to
/// model with only one logical CPU in this package (decision A10).
///
/// <b>Coordinator ruling (second independent review, item N2):</b> an entry
/// programmed for ExtINT delivery -- "virtual wire via the I/O APIC"
/// (plans/specs/apic.md §1.4) -- is never delivered through <see
/// cref="LocalApic.DeliverFixed"/> at all: its own vector field is
/// meaningless (the 8259 supplies the real one), and going through this
/// unit's IRR would wrongly subject it to TPR/PPR and require a LAPIC-side
/// EOI neither real hardware nor <see cref="LocalApic"/>'s own LINT0 ExtINT
/// path needs. Instead, whenever the redirection table has at least one
/// unmasked ExtINT entry, this class simply tells <see cref="LocalApic"/> so
/// (<see cref="LocalApic.SetIoApicExtIntActive"/>); the 8259 is asked for
/// its own vector lazily, in <c>LocalApic.GetPendingVector</c>, only at the
/// moment the CPU actually accepts an interrupt from this source -- never at
/// the edge/level event itself, and never when the unit has nothing to
/// accept it with.
///
/// NMI, SMI and INIT delivery modes are stored and read back but never
/// delivered at all: none of them belongs on <see
/// cref="LocalApic.DeliverFixed"/>'s IRR/ISR path either, and neither the
/// CPU nor this package has anywhere else to send them yet -- the same
/// documented gap <c>LocalApic</c>'s own LINT1/ICR-NMI note describes.
/// </summary>
public sealed class IoApic : IMemoryDevice
{
    public const uint PageSize = 0x1000;
    public const int PinCount = 24;

    private const uint IoRegSelOffset = 0x00;
    private const uint IoWinOffset = 0x10;

    private const uint IoApicIdRegister = 0x00;
    private const uint IoApicVersionRegister = 0x01;
    private const uint IoApicArbitrationRegister = 0x02;
    private const uint RedirectionTableBase = 0x10;
    private const uint RedirectionTableLast = RedirectionTableBase + PinCount * 2 - 1;   // 3Fh

    /// <summary>24 entries (17h), version 11h -- the 82093AA's own fixed answer, never the OS-assigned ID.</summary>
    private const uint VersionRegisterValue = 0x00170011;

    private const uint IdFieldMask = 0x0F000000;   // bits 27:24

    private const ulong EntryVectorMask = 0xFF;
    private const ulong EntryDeliveryModeMask = 0x700;
    private const ulong EntryDeliveryModeFixed = 0x000;
    private const ulong EntryDeliveryModeLowPriority = 0x100;
    private const ulong EntryDeliveryModeExtInt = 0x700;
    private const ulong EntryDestinationModeBit = 1UL << 11;
    private const ulong EntryDeliveryStatusBit = 1UL << 12;   // RO; synchronous delivery, always 0 (decision A6)
    private const ulong EntryPolarityBit = 1UL << 13;
    private const ulong EntryRemoteIrrBit = 1UL << 14;        // RO from the guest's side; this class alone sets/clears it
    private const ulong EntryTriggerLevelBit = 1UL << 15;
    private const ulong EntryMaskBit = 1UL << 16;
    private const ulong EntryGuestWritableMask = EntryVectorMask | EntryDeliveryModeMask | EntryDestinationModeBit
        | EntryPolarityBit | EntryTriggerLevelBit | EntryMaskBit | (0xFFUL << 56);

    /// <summary>Reset value: every field zero except the mask bit (decision A9).</summary>
    private const ulong EntryResetValue = EntryMaskBit;

    private readonly LocalApic _target;

    private readonly ulong[] _entries = new ulong[PinCount];

    /// <summary>
    /// Which pins are currently held high by their source. Meaningful only
    /// for the level-tracked pins <see cref="SetLevel"/> drives (the PIRQ
    /// pins on this board); <see cref="PulseLine"/>'s edge-fed pins never
    /// set an entry here, by design (item 2).
    /// </summary>
    private readonly bool[] _lineHigh = new bool[PinCount];

    private uint _id;
    private uint _ioRegSel;

    public IoApic(LocalApic target)
    {
        _target = target;
        Reset();
    }

    public void Reset()
    {
        Array.Fill(_entries, EntryResetValue);
        Array.Clear(_lineHigh);
        _id = 0;
        _ioRegSel = 0;

        // Every entry resets masked (decision A9), so there is nothing
        // ExtINT-active left after a reset either.
        RecomputeExtIntActive();
    }

    /// <summary>
    /// Tells the target LAPIC whether at least one redirection entry is
    /// currently unmasked and programmed for ExtINT delivery (item N2's
    /// "virtual wire via the I/O APIC" gate). Called after every
    /// redirection-table write and after <see cref="Reset"/>, rather than
    /// asked for on demand, so <see cref="LocalApic"/> never needs a
    /// reference back to this class.
    /// </summary>
    private void RecomputeExtIntActive()
    {
        bool active = false;
        for (int pin = 0; pin < PinCount; pin++)
        {
            ulong entry = _entries[pin];
            bool unmasked = (entry & EntryMaskBit) == 0;
            bool extInt = (entry & EntryDeliveryModeMask) == EntryDeliveryModeExtInt;
            if (unmasked && extInt)
            {
                active = true;
                break;
            }
        }

        _target.SetIoApicExtIntActive(active);
    }

    // ---- pin wiring, driven by Machine alongside the legacy 8259 ----------

    /// <summary>
    /// A rising edge on one of the ISA-fed pins (coordinator ruling, item
    /// 2): every call is a fresh pulse, whatever the pin's own trigger-mode
    /// bit says and regardless of any previous call -- there is no persistent
    /// line state to latch here, matching the many ISA sources (the PIT
    /// above all) that call this over and over without ever lowering
    /// anything. A masked entry drops it with no pending state at all (test
    /// case 13); a level-triggered entry (an unusual thing to configure for
    /// an ISA line, but not forbidden) still respects Remote IRR.
    /// </summary>
    public void PulseLine(int pin)
    {
        ulong entry = _entries[pin];
        if ((entry & EntryMaskBit) != 0)
        {
            return;
        }

        bool level = (entry & EntryTriggerLevelBit) != 0;
        if (level)
        {
            if ((entry & EntryRemoteIrrBit) != 0)
            {
                return;   // still waiting for EOI
            }
            entry |= EntryRemoteIrrBit;
            _entries[pin] = entry;
        }

        Deliver(entry);
    }

    /// <summary>
    /// A level input (the four PIRQ pins on this board): true while the
    /// source keeps asserting it. Deasserting only updates the tracked
    /// level -- Remote IRR clears from an EOI, never from here (item 4).
    ///
    /// An <b>edge</b>-triggered entry reached through this method (an
    /// unusual thing to configure a PIRQ pin as, but not forbidden) delivers
    /// on the rising edge exactly like <see cref="PulseLine"/> -- without
    /// ever touching Remote IRR, which is meaningless for edge mode
    /// (independent review, second round, item N3) -- and is de-duplicated
    /// against repeated same-state calls, since <c>PciInterruptRouter</c>
    /// recomputes and re-asserts every still-held link on every change to
    /// any of them, not only the one that actually moved.
    /// </summary>
    public void SetLevel(int pin, bool asserted)
    {
        bool wasHigh = _lineHigh[pin];
        _lineHigh[pin] = asserted;
        if (!asserted)
        {
            return;
        }

        ulong entry = _entries[pin];
        if ((entry & EntryMaskBit) != 0)
        {
            return;
        }

        bool level = (entry & EntryTriggerLevelBit) != 0;
        if (!level)
        {
            if (wasHigh)
            {
                return;   // no new edge
            }
            Deliver(entry);
            return;
        }

        if ((entry & EntryRemoteIrrBit) != 0)
        {
            return;   // still waiting for EOI
        }

        entry |= EntryRemoteIrrBit;
        _entries[pin] = entry;
        Deliver(entry);
    }

    /// <summary>
    /// Delivers one Fixed/LowPriority entry's vector into the target
    /// LAPIC's own IRR; every other delivery mode does nothing here --
    /// ExtINT is handled entirely through <see
    /// cref="LocalApic.SetIoApicExtIntActive"/> (item N2, see the type
    /// summary), and NMI/SMI/INIT have nowhere to go at all yet.
    /// </summary>
    private void Deliver(ulong entry)
    {
        ulong mode = entry & EntryDeliveryModeMask;
        if (mode is not (EntryDeliveryModeFixed or EntryDeliveryModeLowPriority))
        {
            return;
        }

        bool level = (entry & EntryTriggerLevelBit) != 0;
        byte vector = (byte)(entry & EntryVectorMask);
        _target.DeliverFixed(vector, level);
    }

    /// <summary>Called from <see cref="LocalApic.LevelTriggeredEoi"/>: clears Remote IRR on every level entry whose vector matches (test case 7), and re-delivers at once if the line is still held high (item 4).</summary>
    public void ClearRemoteIrr(byte vector)
    {
        for (int pin = 0; pin < PinCount; pin++)
        {
            ulong entry = _entries[pin];
            bool level = (entry & EntryTriggerLevelBit) != 0;
            bool matches = (byte)(entry & EntryVectorMask) == vector;
            if (!level || !matches || (entry & EntryRemoteIrrBit) == 0)
            {
                continue;
            }

            entry &= ~EntryRemoteIrrBit;
            _entries[pin] = entry;

            if (_lineHigh[pin] && (entry & EntryMaskBit) == 0)
            {
                entry |= EntryRemoteIrrBit;
                _entries[pin] = entry;
                Deliver(entry);
            }
        }
    }

    // ---- IOREGSEL/IOWIN MMIO window ---------------------------------------

    public byte ReadByte(uint offset) => (byte)(ReadDword(offset & ~3u) >> (int)((offset & 3) * 8));

    public void WriteByte(uint offset, byte value)
    {
        uint aligned = offset & ~3u;
        int shift = (int)((offset & 3) * 8);
        uint current = ReadDword(aligned);
        uint merged = (current & ~(0xFFu << shift)) | ((uint)value << shift);
        WriteDword(aligned, merged);
    }

    public uint ReadDword(uint offset)
    {
        if (offset == IoRegSelOffset)
        {
            return _ioRegSel;
        }
        if (offset == IoWinOffset)
        {
            return ReadIndirect(_ioRegSel);
        }
        return 0;
    }

    public void WriteDword(uint offset, uint value)
    {
        if (offset == IoRegSelOffset) { _ioRegSel = value & 0xFF; return; }
        if (offset == IoWinOffset) { WriteIndirect(_ioRegSel, value); }
    }

    private uint ReadIndirect(uint index)
    {
        if (index == IoApicIdRegister)
        {
            return _id;
        }
        if (index == IoApicVersionRegister)
        {
            return VersionRegisterValue;
        }
        if (index == IoApicArbitrationRegister)
        {
            return _id;   // "loaded from ID" (plans/specs/apic.md §2)
        }
        if (index >= RedirectionTableBase && index <= RedirectionTableLast)
        {
            int pin = (int)(index - RedirectionTableBase) / 2;
            bool high = ((index - RedirectionTableBase) & 1) != 0;
            ulong entry = _entries[pin] & ~EntryDeliveryStatusBit;   // A6: delivery status always reads 0
            return high ? (uint)(entry >> 32) : (uint)entry;
        }

        return 0;
    }

    private void WriteIndirect(uint index, uint value)
    {
        if (index == IoApicIdRegister) { _id = value & IdFieldMask; return; }
        if (index == IoApicVersionRegister || index == IoApicArbitrationRegister)
        {
            return;   // read-only
        }

        if (index < RedirectionTableBase || index > RedirectionTableLast)
        {
            return;
        }

        int pin = (int)(index - RedirectionTableBase) / 2;
        bool high = ((index - RedirectionTableBase) & 1) != 0;
        ulong current = _entries[pin];

        if (high)
        {
            ulong newHigh = ((ulong)value << 32) & (0xFFUL << 56);
            _entries[pin] = (current & 0xFFFFFFFFUL) | newHigh;
            return;
        }

        // Delivery status and Remote IRR are read-only from the guest's
        // side: a write to the low dword can never set or clear either,
        // only this class's own delivery/EOI logic can.
        ulong preserved = current & (EntryDeliveryStatusBit | EntryRemoteIrrBit);
        ulong written = value & EntryGuestWritableMask & ~(EntryDeliveryStatusBit | EntryRemoteIrrBit);
        ulong updated = (current & 0xFFFFFFFF00000000UL) | preserved | written;
        _entries[pin] = updated;

        // Item 4: unmasking a level entry whose line is already high
        // delivers on the spot, rather than waiting for an edge that a
        // level source (a PCI PIRQ link) may never produce again.
        bool wasMasked = (current & EntryMaskBit) != 0;
        bool nowMasked = (updated & EntryMaskBit) != 0;
        bool level = (updated & EntryTriggerLevelBit) != 0;
        if (wasMasked && !nowMasked && level && _lineHigh[pin] && (updated & EntryRemoteIrrBit) == 0)
        {
            updated |= EntryRemoteIrrBit;
            _entries[pin] = updated;
            Deliver(updated);
        }

        RecomputeExtIntActive();
    }

    // ---- snapshot -----------------------------------------------------------

    /// <summary>ID, IOREGSEL, the 24 entries and which pins are currently held high (independent review, item 11 -- this section is not committed anywhere yet, so it is defined complete from v1).</summary>
    public const int SnapshotLength = 4 + 4 + PinCount * 8 + 4;

    public byte[] Capture()
    {
        using var stream = new MemoryStream(SnapshotLength);
        using var writer = new BinaryWriter(stream);
        writer.Write(_id);
        writer.Write(_ioRegSel);
        foreach (ulong entry in _entries) writer.Write(entry);
        writer.Write(PackLineHigh());
        return stream.ToArray();
    }

    public void Restore(ReadOnlySpan<byte> state)
    {
        using var reader = new BinaryReader(new MemoryStream(state.ToArray()));
        _id = reader.ReadUInt32();
        _ioRegSel = reader.ReadUInt32();
        for (int i = 0; i < _entries.Length; i++) _entries[i] = reader.ReadUInt64();
        UnpackLineHigh(reader.ReadUInt32());
        RecomputeExtIntActive();
    }

    private uint PackLineHigh()
    {
        uint packed = 0;
        for (int pin = 0; pin < PinCount; pin++)
        {
            if (_lineHigh[pin])
            {
                packed |= 1u << pin;
            }
        }

        return packed;
    }

    private void UnpackLineHigh(uint packed)
    {
        for (int pin = 0; pin < PinCount; pin++)
        {
            _lineHigh[pin] = (packed & (1u << pin)) != 0;
        }
    }
}
