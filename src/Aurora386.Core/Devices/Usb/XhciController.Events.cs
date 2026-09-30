namespace Aurora386.Core.Devices.Usb;

/// <summary>
/// Interrupter 0's own registers (IMAN, IMOD, ERSTSZ, ERSTBA, ERDP, all at
/// RTSOFF+20h -- the only interrupter this stage models, coordinator
/// decision 3) and the event ring it drives: the producer side of a
/// segmented, cycle-bit ring exactly as xHCI 1.2 section 4.9.4 describes,
/// and interrupt delivery to the one INTA line exactly as QEMU's own
/// <c>xhci_intr_raise</c> does it. Every TRB this class writes is a 16-byte
/// event; nothing here ever reads one -- posting is always this
/// controller's own doing, in response to a port change today and a
/// completed command or transfer once <c>RingDoorbell</c> is filled in.
/// </summary>
public sealed partial class XhciController
{
    // --- IMAN (interrupter 0, RTSOFF+20h) -----------------------------------------

    private const uint InterrupterManagementInterruptPending = 1u << 0; // IP, RW1C (also hardware-set)
    private const uint InterrupterManagementInterruptEnable = 1u << 1; // IE, RW

    // --- ERDP (interrupter 0, RTSOFF+38h/3Ch) -------------------------------------

    private const uint EventRingDequeuePointerEventHandlerBusy = 1u << 3; // EHB, RW1C
    private const uint EventRingDequeuePointerLowStorableMask = 0xFFFF_FFFFu; // DESI (2:0), EHB (3, special-cased) and the pointer (31:4)

    // --- interrupter register offsets, relative to InterrupterRegisterSetBase ------

    private const uint RegInterrupterManagement = 0x00;
    private const uint RegInterrupterModeration = 0x04;
    private const uint RegEventRingSegmentTableSize = 0x08;
    private const uint RegEventRingSegmentTableBaseLow = 0x10;
    private const uint RegEventRingSegmentTableBaseHigh = 0x14;
    private const uint RegEventRingDequeuePointerLow = 0x18;
    private const uint RegEventRingDequeuePointerHigh = 0x1C;

    private const uint EventRingSegmentTableSizeMask = 0xFFFF; // ERSTSZ, bits 15:0
    private const uint EventRingSegmentTableBaseLowMask = 0xFFFF_FFC0u; // 64-byte aligned (plan's "Costanti")

    /// <summary>One 16-byte entry of the segment table ERSTBA points at: a base address and a TRB count.</summary>
    private const int EventRingSegmentEntryLength = 16;

    private const int EventTrbLength = 16;

    /// <summary>
    /// TRBs a wild ERSTSZ can never actually make this controller walk past
    /// in one lap: the plan's own single-segment scope (coordinator decision
    /// 3) means one is the only value real software ever programs, but the
    /// loop that reads the table is bounded regardless, per this repository's
    /// own rule that every guest-controlled loop has a fixed ceiling.
    /// </summary>
    private const int MaxEventRingSegments = 256;

    private const uint PortStatusChangeEventTrbType = 34;
    private const uint EventCompletionCodeSuccess = 1;
    private const int TrbTypeShift = 10;
    private const uint TrbCycleBit = 1u << 0;

    private uint _interrupterManagement;
    private uint _interrupterModeration;
    private uint _eventRingSegmentTableSize;
    private uint _eventRingSegmentTableBaseLow;
    private uint _eventRingSegmentTableBaseHigh;
    private uint _eventRingDequeuePointerLow;
    private uint _eventRingDequeuePointerHigh;

    // --- event ring producer state (xHCI 1.2 section 4.9.4) -----------------------

    private int _eventProducerSegment;
    private int _eventProducerIndex;
    private bool _eventProducerCycle;

    private void ResetEvents()
    {
        _interrupterManagement = 0;
        _interrupterModeration = 0;
        _eventRingSegmentTableSize = 0;
        _eventRingSegmentTableBaseLow = 0;
        _eventRingSegmentTableBaseHigh = 0;
        _eventRingDequeuePointerLow = 0;
        _eventRingDequeuePointerHigh = 0;
        _eventProducerSegment = 0;
        _eventProducerIndex = 0;
        _eventProducerCycle = true; // Producer Cycle State initialises to 1 (xHCI 1.2 section 4.9.4)
    }

    private uint ReadInterrupterRegister(uint offset) => offset switch
    {
        RegInterrupterManagement => _interrupterManagement,
        RegInterrupterModeration => _interrupterModeration,
        RegEventRingSegmentTableSize => _eventRingSegmentTableSize,
        RegEventRingSegmentTableBaseLow => _eventRingSegmentTableBaseLow,
        RegEventRingSegmentTableBaseHigh => _eventRingSegmentTableBaseHigh,
        RegEventRingDequeuePointerLow => _eventRingDequeuePointerLow,
        RegEventRingDequeuePointerHigh => _eventRingDequeuePointerHigh,
        _ => 0,
    };

    private void WriteInterrupterRegister(uint offset, uint value, uint writeMask)
    {
        switch (offset)
        {
            case RegInterrupterManagement:
                WriteInterrupterManagement(value, writeMask);
                break;
            case RegInterrupterModeration:
                _interrupterModeration = (_interrupterModeration & ~writeMask) | (value & writeMask);
                break;
            case RegEventRingSegmentTableSize:
                _eventRingSegmentTableSize = (_eventRingSegmentTableSize & ~writeMask) | (value & writeMask & EventRingSegmentTableSizeMask);
                break;
            case RegEventRingSegmentTableBaseLow:
                _eventRingSegmentTableBaseLow = (_eventRingSegmentTableBaseLow & ~writeMask) | (value & writeMask & EventRingSegmentTableBaseLowMask);

                // Writing ERSTBA (re)initialises the event ring: the producer
                // starts at the first TRB of the first segment with a cycle
                // state of 1 (xHCI 1.2 section 5.5.2.3.2; QEMU xhci_er_reset).
                // Review of the xHCI package, finding M3.
                ResetEventRingProducer();
                break;
            case RegEventRingSegmentTableBaseHigh:
                _eventRingSegmentTableBaseHigh = (_eventRingSegmentTableBaseHigh & ~writeMask) | (value & writeMask);
                break;
            case RegEventRingDequeuePointerLow:
                WriteEventRingDequeuePointerLow(value, writeMask);
                break;
            case RegEventRingDequeuePointerHigh:
                _eventRingDequeuePointerHigh = (_eventRingDequeuePointerHigh & ~writeMask) | (value & writeMask);
                break;
        }
    }

    /// <summary>Puts the event ring producer back at the first TRB of the first segment, cycle state 1.</summary>
    private void ResetEventRingProducer()
    {
        _eventProducerSegment = 0;
        _eventProducerIndex = 0;
        _eventProducerCycle = true;
    }

    /// <summary>
    /// The address of the TRB slot the producer will write after the current
    /// one: the next index in this segment, or the first TRB of the next
    /// segment (wrapping to the first), or <see cref="ulong.MaxValue"/> when
    /// that segment's descriptor cannot be read.
    /// </summary>
    private ulong NextEventSlotAddress(int segmentCount, ulong segmentBase, uint segmentTrbCount)
    {
        uint nextIndex = (uint)_eventProducerIndex + 1;
        if (nextIndex < segmentTrbCount)
        {
            return segmentBase + (ulong)nextIndex * EventTrbLength;
        }

        int nextSegment = (_eventProducerSegment + 1) % segmentCount;
        if (!TryReadEventRingSegment(nextSegment, out ulong nextBase, out _))
        {
            return ulong.MaxValue;
        }

        return nextBase;
    }

    /// <summary>Writing IMAN.IP=1 clears it (RW1C); IP is otherwise only ever set by <see cref="PostEvent"/>. IE is plain RW.</summary>
    private void WriteInterrupterManagement(uint value, uint writeMask)
    {
        bool interruptPending = (_interrupterManagement & InterrupterManagementInterruptPending) != 0;
        bool clearRequested = (writeMask & InterrupterManagementInterruptPending) != 0
            && (value & InterrupterManagementInterruptPending) != 0;
        bool interruptEnable = (writeMask & InterrupterManagementInterruptEnable) != 0
            ? (value & InterrupterManagementInterruptEnable) != 0
            : (_interrupterManagement & InterrupterManagementInterruptEnable) != 0;

        _interrupterManagement = (interruptPending && !clearRequested ? InterrupterManagementInterruptPending : 0u)
            | (interruptEnable ? InterrupterManagementInterruptEnable : 0u);
    }

    /// <summary>Writing ERDP with EHB=1 clears EHB (RW1C); DESI and the pointer bits are plain RW.</summary>
    private void WriteEventRingDequeuePointerLow(uint value, uint writeMask)
    {
        bool eventHandlerBusy = (_eventRingDequeuePointerLow & EventRingDequeuePointerEventHandlerBusy) != 0;
        bool clearRequested = (writeMask & EventRingDequeuePointerEventHandlerBusy) != 0
            && (value & EventRingDequeuePointerEventHandlerBusy) != 0;

        uint preserved = _eventRingDequeuePointerLow & ~writeMask;
        uint written = value & writeMask & ~EventRingDequeuePointerEventHandlerBusy & EventRingDequeuePointerLowStorableMask;
        _eventRingDequeuePointerLow = preserved | written
            | (eventHandlerBusy && !clearRequested ? EventRingDequeuePointerEventHandlerBusy : 0u);
    }

    /// <summary>
    /// The PCI INTA line is asserted while IMAN.IP and IMAN.IE and
    /// USBCMD.INTE, exactly as QEMU's own <c>xhci_intr_raise</c> gates
    /// MSI/MSI-X or the legacy INTx line; recomputed after every register
    /// write (<see cref="WriteDwordInLanes"/> calls this unconditionally).
    /// </summary>
    private void RecomputeInterruptLine()
    {
        bool pending = (_interrupterManagement & InterrupterManagementInterruptPending) != 0;
        bool enabled = (_interrupterManagement & InterrupterManagementInterruptEnable) != 0;
        bool controllerEnabled = (_command & CommandInterrupterEnable) != 0;
        _router.SetInterrupt(Function.Address, InterruptPinIntA, pending && enabled && controllerEnabled);
    }

    /// <summary>
    /// Port ID is 1-based in the TRB (xHCI 1.2 section 6.4.2.3), unlike this
    /// class's own 0-based port indices.
    /// </summary>
    private void PostPortStatusChangeEvent(int port)
    {
        uint dword0 = (uint)(port + 1) << 24;
        uint dword2 = EventCompletionCodeSuccess << 24;
        PostEvent(dword0, 0, dword2, PortStatusChangeEventTrbType);
    }

    /// <summary>
    /// Appends one event TRB to interrupter 0's ring: reads the segment
    /// table at ERSTBA (bounded by <see cref="MaxEventRingSegments"/>),
    /// writes the four dwords with the producer cycle bit, advances the
    /// producer position through the segments and toggles the cycle bit at
    /// the end of a full lap (xHCI 1.2 section 4.9.4). Every DMA access here
    /// is bus-master DMA (<see cref="IDmaMemory"/>) and only happens while
    /// <see cref="PciFunction.BusMasterEnabled"/> is true, exactly like
    /// every other bus master in this emulator.
    /// </summary>
    private void PostEvent(uint dword0, uint dword1, uint dword2, uint trbType, uint extraDword3Bits = 0)
    {
        if (!Function.BusMasterEnabled)
        {
            return;
        }

        int segmentCount = (int)Math.Min(_eventRingSegmentTableSize, MaxEventRingSegments);
        if (segmentCount == 0)
        {
            return; // the event ring was never configured: nothing to write, no consumer to notice
        }
        if (_eventProducerSegment >= segmentCount)
        {
            _eventProducerSegment = 0;
            _eventProducerIndex = 0;
        }

        if (!TryReadEventRingSegment(_eventProducerSegment, out ulong segmentBase, out uint segmentTrbCount)
            || segmentTrbCount == 0)
        {
            return; // a malformed segment descriptor: documented gap, no event is written
        }

        // A position left outside the segment -- the guest shrank it, or a
        // snapshot says so -- starts over at the segment's first TRB rather
        // than writing past the ring the guest supplied.
        if (_eventProducerIndex >= segmentTrbCount)
        {
            _eventProducerIndex = 0;
        }

        ulong slotAddress64 = segmentBase + (ulong)_eventProducerIndex * EventTrbLength;
        if (slotAddress64 > uint.MaxValue)
        {
            return; // this bus master only ever reaches the low 4 GB (coordinator decision 5)
        }
        uint slotAddress = (uint)slotAddress64;

        // The ring is full when the slot after this one is the consumer's
        // dequeue pointer: writing here would leave the producer on top of
        // it, indistinguishable from an empty ring. ERDP equal to this slot is
        // the opposite case -- the driver has consumed everything, which is
        // how every driver leaves it after initialisation and after each
        // interrupt (QEMU's xhci_event: full when (ep_idx + 1) % size ==
        // dp_idx). The event is dropped and HCE raised (the plan's choice;
        // QEMU posts an Event Ring Full event instead). Review finding B1.
        ulong dequeue64 = ((ulong)_eventRingDequeuePointerHigh << 32) | (_eventRingDequeuePointerLow & ~0xFu);
        if (NextEventSlotAddress(segmentCount, segmentBase, segmentTrbCount) == dequeue64)
        {
            _status |= StatusHostControllerError;
            return;
        }

        uint dword3 = (trbType << TrbTypeShift) | extraDword3Bits | (_eventProducerCycle ? TrbCycleBit : 0u);
        _dma.WriteDword(slotAddress, dword0);
        _dma.WriteDword(slotAddress + 4, dword1);
        _dma.WriteDword(slotAddress + 8, dword2);
        _dma.WriteDword(slotAddress + 12, dword3);

        _eventProducerIndex++;
        if (_eventProducerIndex >= segmentTrbCount)
        {
            _eventProducerIndex = 0;
            _eventProducerSegment++;
            if (_eventProducerSegment >= segmentCount)
            {
                _eventProducerSegment = 0;
                _eventProducerCycle = !_eventProducerCycle;
            }
        }

        _eventRingDequeuePointerLow |= EventRingDequeuePointerEventHandlerBusy;
        _interrupterManagement |= InterrupterManagementInterruptPending;
        _status |= StatusEventInterrupt;
        RecomputeInterruptLine();
    }

    /// <summary>Reads one 16-byte entry (base qword, size dword) of the segment table at ERSTBA, bounds-checked against <see cref="MaxEventRingSegments"/>.</summary>
    private bool TryReadEventRingSegment(int segmentIndex, out ulong segmentBase, out uint trbCount)
    {
        segmentBase = 0;
        trbCount = 0;
        if ((uint)segmentIndex >= MaxEventRingSegments)
        {
            return false;
        }

        ulong tableBase = ((ulong)_eventRingSegmentTableBaseHigh << 32) | _eventRingSegmentTableBaseLow;
        ulong entryAddress64 = tableBase + (ulong)segmentIndex * EventRingSegmentEntryLength;
        if (entryAddress64 > uint.MaxValue - EventRingSegmentEntryLength)
        {
            return false;
        }
        uint entryAddress = (uint)entryAddress64;

        uint baseLow = _dma.ReadDword(entryAddress);
        uint baseHigh = _dma.ReadDword(entryAddress + 4);
        uint size = _dma.ReadDword(entryAddress + 8) & 0xFFFF; // bits 15:0 only (xHCI 1.2 table 6-5)
        segmentBase = ((ulong)baseHigh << 32) | baseLow;
        trbCount = size;
        return true;
    }

    private void CaptureEvents(BinaryWriter writer)
    {
        writer.Write(_interrupterManagement);
        writer.Write(_interrupterModeration);
        writer.Write(_eventRingSegmentTableSize);
        writer.Write(_eventRingSegmentTableBaseLow);
        writer.Write(_eventRingSegmentTableBaseHigh);
        writer.Write(_eventRingDequeuePointerLow);
        writer.Write(_eventRingDequeuePointerHigh);
        writer.Write(_eventProducerSegment);
        writer.Write(_eventProducerIndex);
        writer.Write(_eventProducerCycle);
    }

    private void RestoreEvents(BinaryReader reader)
    {
        uint interrupterManagement = reader.ReadUInt32();
        uint interrupterModeration = reader.ReadUInt32();
        uint eventRingSegmentTableSize = reader.ReadUInt32();
        uint eventRingSegmentTableBaseLow = reader.ReadUInt32();
        uint eventRingSegmentTableBaseHigh = reader.ReadUInt32();
        uint eventRingDequeuePointerLow = reader.ReadUInt32();
        uint eventRingDequeuePointerHigh = reader.ReadUInt32();
        int eventProducerSegment = reader.ReadInt32();
        int eventProducerIndex = reader.ReadInt32();
        bool eventProducerCycle = reader.ReadBoolean();
        if (eventProducerSegment < 0 || eventProducerIndex < 0)
        {
            throw new InvalidDataException("Invalid xHCI event ring snapshot.");
        }

        _interrupterManagement = interrupterManagement;
        _interrupterModeration = interrupterModeration;
        _eventRingSegmentTableSize = eventRingSegmentTableSize;
        _eventRingSegmentTableBaseLow = eventRingSegmentTableBaseLow;
        _eventRingSegmentTableBaseHigh = eventRingSegmentTableBaseHigh;
        _eventRingDequeuePointerLow = eventRingDequeuePointerLow;
        _eventRingDequeuePointerHigh = eventRingDequeuePointerHigh;
        _eventProducerSegment = eventProducerSegment;
        _eventProducerIndex = eventProducerIndex;
        _eventProducerCycle = eventProducerCycle;
    }
}
