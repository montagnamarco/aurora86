namespace Aurora386.Core.Devices.Usb;

/// <summary>
/// The command ring (doorbell 0, target 0) and the eleven commands this
/// controller's subset understands (coordinator decision 5,
/// <c>plans/usb3-xhci.md</c>): Enable/Disable Slot, Address Device (with
/// BSR), Configure Endpoint, Evaluate Context, Reset Endpoint, Stop
/// Endpoint, Set TR Dequeue Pointer and No-Op. Every command reads and
/// writes the Input Context (from the command TRB's own parameter pointer)
/// and the Output Device Context (from DCBAAP[slot]) directly in guest
/// memory through <see cref="IDmaMemory"/> -- there is no shadow copy in
/// this class besides <see cref="_slotEnabled"/>, exactly the "campanello"
/// engine plan decision 6 calls for: a doorbell ring processes the whole
/// ring to completion synchronously, the same way <c>AhciController</c>
/// completes a command synchronously on its own doorbell.
///
/// Contexts are 32 bytes each (CSZ=0, plan decision 5): Slot Context at
/// device-context index 0, Endpoint Context <c>n</c> (1 = EP0, 2 = EP1 OUT,
/// 3 = EP1 IN, ..., 31 = EP15 IN, xHCI 1.2 section 4.5.1) at index
/// <c>n</c>. An Input Context is the same layout with one extra leading
/// Input Control Context, so index <c>n</c> there is device-context index
/// <c>n - 1</c> shifted up by one context's worth of bytes.
/// </summary>
public sealed partial class XhciController
{
    // --- TRB types (plan's "Costanti": commands 9-23, events 32-39) ---------------

    private const uint TrbTypeLink = 6;
    private const uint TrbTypeEnableSlot = 9;
    private const uint TrbTypeDisableSlot = 10;
    private const uint TrbTypeAddressDevice = 11;
    private const uint TrbTypeConfigureEndpoint = 12;
    private const uint TrbTypeEvaluateContext = 13;
    private const uint TrbTypeResetEndpoint = 14;
    private const uint TrbTypeStopEndpoint = 15;
    private const uint TrbTypeSetTrDequeuePointer = 16;
    private const uint TrbTypeNoOpCommand = 23;
    private const uint TrbTypeCommandCompletionEvent = 33;

    private const uint TrbTypeMask = 0x3F; // bits 15:10, 6 bits (values 0-63)

    // --- dword3 flag bits shared by every TRB (plan's "Costanti") -----------------

    private const uint TrbFlagToggleCycle = 1u << 1; // TC on a Link TRB (ENT on every other TRB type)
    private const int CommandTrbEndpointIdShift = 16; // Reset/Stop Endpoint and Set TR Dequeue Pointer's target endpoint (bits 20:16)
    private const uint CommandTrbEndpointIdMask = 0x1Fu << CommandTrbEndpointIdShift;
    private const uint CommandTrbBsrOrDcBit = 1u << 9; // BSR (Address Device) / DC (Configure Endpoint), plan's "Costanti"
    private const int CommandTrbSlotIdShift = 24; // every command and event TRB's dword3 bits 31:24

    // --- completion codes (Linux drivers/usb/host/xhci.h, plan's "Costanti") ------

    private const uint CompletionCodeTrbError = 5;
    private const uint CompletionCodeResourceError = 7; // a 64-bit pointer this bus master cannot reach below 4 GB (coordinator decision 5)
    private const uint CompletionCodeNoSlotsAvailable = 9;
    private const uint CompletionCodeSlotNotEnabled = 11;

    private const int CompletionCodeShift = 24; // dword2 bits 31:24 on every event this controller posts

    // --- command TRB pointer field alignment ---------------------------------------

    private const uint CommandTrbPointerMask = 0xFFFF_FFF0u; // 16-byte aligned, like every other TRB pointer
    private const uint InputContextPointerMask = 0xFFFF_FFC0u; // 64-byte aligned (xHCI 1.2 section 6.2.5.1)

    // --- Slot Context (device-context index 0), dwords relative to its own base ----

    private const uint SlotContextRouteAndEntriesOffset = 0x00; // dword0: route string, speed, MTT, hub, context entries
    private const uint SlotContextPortInfoOffset = 0x04; // dword1: max exit latency, root hub port number, number of ports
    private const uint SlotContextTtInfoOffset = 0x08; // dword2: parent hub slot/port, TTT, interrupter target
    private const uint SlotContextAddressAndStateOffset = 0x0C; // dword3: USB device address, slot state

    private const int SlotContextRootHubPortShift = 16; // dword1 bits 23:16
    private const uint SlotContextRootHubPortMask = 0xFFu << SlotContextRootHubPortShift;
    private const uint SlotContextDeviceAddressMask = 0xFF; // dword3 bits 7:0
    private const int SlotContextContextEntriesShift = 27; // dword0 bits 31:27
    private const uint SlotContextContextEntriesMask = 0x1Fu << SlotContextContextEntriesShift;
    private const int SlotContextSlotStateShift = 27; // dword3 bits 31:27 (plan's "Costanti")
    private const uint SlotContextSlotStateMask = 0x1Fu << SlotContextSlotStateShift;

    private const byte SlotStateDisabledOrEnabled = 0;
    private const byte SlotStateDefault = 1;
    private const byte SlotStateAddressed = 2;
    private const byte SlotStateConfigured = 3;

    // --- Endpoint Context, dwords relative to its own base --------------------------

    private const uint EndpointContextStateOffset = 0x00; // dword0: EP state (2:0), interval (23:16), ...
    private const uint EndpointContextTypeOffset = 0x04; // dword1: CErr, EP type (5:3), max packet size (31:16)
    private const uint EndpointContextDequeueLowOffset = 0x08; // dword2: DCS (bit 0) + TR dequeue pointer low
    private const uint EndpointContextDequeueHighOffset = 0x0C; // dword3: TR dequeue pointer high

    private const uint EndpointStateMask = 0x7;
    private const byte EndpointStateDisabled = 0;
    private const byte EndpointStateRunning = 1;
    private const byte EndpointStateHalted = 2;
    private const byte EndpointStateStopped = 3;

    private const int EndpointMaxPacketSizeShift = 16; // dword1 bits 31:16
    private const uint EndpointMaxPacketSizeMask = 0xFFFFu << EndpointMaxPacketSizeShift;

    private const uint EndpointDequeueCycleStateBit = 1u << 0; // DCS
    private const uint EndpointDequeuePointerMask = 0xFFFF_FFF0u; // pointer bits 31:4, 16-byte aligned

    // --- Input Control Context, dwords relative to its own base (device-context index -1) ---

    private const uint InputControlContextDropFlagsOffset = 0x00;
    private const uint InputControlContextAddFlagsOffset = 0x04;

    /// <summary>
    /// Drop bit 0 (the Slot Context) and bit 1 (EP0) must both be zero, and
    /// Add bit 0 (the Slot Context) must be one, exactly as QEMU's
    /// <c>hw/usb/hcd-xhci.c</c> <c>xhci_config_ep</c> checks before touching
    /// any endpoint context on a Configure Endpoint command -- the Slot
    /// Context and EP0 are never individually reconfigured that way, and
    /// every Configure Endpoint always re-evaluates the slot fields.
    /// </summary>
    private const uint InputControlContextSlotOrEp0Bit = 0x3;

    private const uint InputControlContextSlotAddBit = 1u << 0;

    // --- bounds ----------------------------------------------------------------------

    /// <summary>
    /// Command TRBs one doorbell ring can walk through, Link TRBs included:
    /// a guest-controlled loop this repository's own rule requires bounding,
    /// exactly like <see cref="MaxEventRingSegments"/> bounds the event
    /// ring's own segment-table walk.
    /// </summary>
    private const int MaxCommandTrbsPerDoorbell = 256;

    private readonly bool[] _slotEnabled = new bool[MaxDeviceSlots + 1]; // index 0 unused, 1..MaxDeviceSlots

    /// <summary>
    /// The command ring's own internal Dequeue Pointer and Consumer Cycle
    /// State (xHCI 1.2 section 4.6.1.1): loaded from CRCR by
    /// <see cref="ArmCommandRing"/> every time software writes that
    /// register, then walked by <see cref="RunCommandRing"/> alone from
    /// then on. This is deliberately not the same storage as CRCR's own
    /// pointer field: that field is quantised to a 64-byte boundary (bits
    /// 5:0 reserved), but the ring itself advances 16 bytes at a time, so
    /// reusing it would lose the low bits of every position that is not
    /// also 64-byte aligned.
    /// </summary>
    private ulong _commandRingPointer;
    private bool _commandRingCycleState;

    /// <summary>Loads the command ring's internal walking position from CRCR, exactly as real hardware does when software (re)programs it.</summary>
    private void ArmCommandRing()
    {
        _commandRingPointer = ((ulong)_crcrHigh << 32) | (_crcrLow & CrcrPointerMask);
        _commandRingCycleState = (_crcrLow & CrcrRingCycleState) != 0;
    }

    /// <summary>Doorbell 0, target 0: runs the command ring from its own internal pointer and cycle state.</summary>
    private void RunCommandRing()
    {
        if (!Function.BusMasterEnabled || (_command & CommandRunStop) == 0)
        {
            return;
        }

        ulong pointer = _commandRingPointer;
        bool cycle = _commandRingCycleState;

        // Ringing doorbell 0 starts the command ring, which stays running
        // (CRCR.CRR = 1) until software stops or aborts it (xHCI 1.2 section
        // 4.6.1.1), even while it is waiting on an empty slot. Review M4.
        _commandRingRunning = true;

        for (int steps = 0; steps < MaxCommandTrbsPerDoorbell; steps++)
        {
            if (pointer > uint.MaxValue - 16)
            {
                break; // this bus master only ever reaches the low 4 GB (coordinator decision 5)
            }
            uint address = (uint)pointer;
            uint dword0 = _dma.ReadDword(address);
            uint dword1 = _dma.ReadDword(address + 4);
            uint dword2 = _dma.ReadDword(address + 8);
            uint dword3 = _dma.ReadDword(address + 12);
            if (((dword3 & TrbCycleBit) != 0) != cycle)
            {
                break; // the consumer cycle state no longer matches: the ring is empty
            }

            uint type = (dword3 >> TrbTypeShift) & TrbTypeMask;
            if (type == TrbTypeLink)
            {
                pointer = ((ulong)dword1 << 32) | (dword0 & CommandTrbPointerMask);
                if ((dword3 & TrbFlagToggleCycle) != 0)
                {
                    cycle = !cycle;
                }
                continue;
            }

            ExecuteCommand(type, dword0, dword1, dword2, dword3, pointer);
            pointer += 16;
        }

        _commandRingPointer = pointer;
        _commandRingCycleState = cycle;
    }

    /// <summary>Whether the command ring is running: CRCR.CRR reads this.</summary>
    private bool _commandRingRunning;

    /// <summary>Completion code 24: the command ring was stopped or aborted.</summary>
    private const uint CompletionCodeCommandRingStopped = 24;

    /// <summary>
    /// Set when CS/CA stopped the ring through the low dword: the high
    /// dword that follows belongs to the same 64-bit abort write (Linux's
    /// xhci_abort_cmd_ring writes all of CRCR, low dword first) and must
    /// not re-arm the ring from CRCR -- that would rewind it to the pointer
    /// written at initialisation and replay every earlier command. QEMU's
    /// xhci_oper_write likewise skips xhci_ring_init on an abort.
    /// </summary>
    private bool _commandRingAbortHighPending;

    /// <summary>
    /// CRCR's low dword written by software. While the ring runs, the
    /// pointer and RCS cannot change (xHCI 1.2 section 5.4.5); CS or CA
    /// stops it -- every command here completes at once, so stopping and
    /// aborting are the same -- clears CRR and posts a Command Completion
    /// Event with Command Ring Stopped at the next command's position, as
    /// Linux's abort handling waits for (section 4.6.1.2). The ring keeps
    /// its internal dequeue pointer and cycle state, so the next doorbell 0
    /// resumes after the last completed command. While the ring is stopped
    /// a write reloads the internal dequeue pointer and cycle state.
    /// </summary>
    private void WriteCommandRingControlLow(uint value, uint writeMask)
    {
        const uint CommandStop = 1u << 1;
        const uint CommandAbort = 1u << 2;

        if (_commandRingRunning)
        {
            if ((value & writeMask & (CommandStop | CommandAbort)) != 0)
            {
                _commandRingRunning = false;
                _commandRingAbortHighPending = true;
                PostCommandCompletionEvent(_commandRingPointer, CompletionCodeCommandRingStopped, 0);
            }

            return;
        }

        _commandRingAbortHighPending = false;
        _crcrLow = (_crcrLow & ~(writeMask & CrcrLowWritableMask)) | (value & writeMask & CrcrLowWritableMask);
        ArmCommandRing();
    }

    /// <summary>
    /// CRCR's high dword: the upper pointer half, writable only while the
    /// ring is stopped. The second half of a 64-bit abort write is ignored
    /// (see <see cref="_commandRingAbortHighPending"/>).
    /// </summary>
    private void WriteCommandRingControlHigh(uint value, uint writeMask)
    {
        if (_commandRingRunning)
        {
            return;
        }

        if (_commandRingAbortHighPending)
        {
            _commandRingAbortHighPending = false;
            return;
        }

        _crcrHigh = (_crcrHigh & ~writeMask) | (value & writeMask);
        ArmCommandRing();
    }

    private void ExecuteCommand(uint type, uint dword0, uint dword1, uint dword2, uint dword3, ulong trbPointer)
    {
        byte slotId = (byte)(dword3 >> CommandTrbSlotIdShift);

        switch (type)
        {
            case TrbTypeEnableSlot:
                ExecuteEnableSlot(trbPointer);
                return;
            case TrbTypeNoOpCommand:
                PostCommandCompletionEvent(trbPointer, EventCompletionCodeSuccess, 0);
                return;
        }

        // A type this subset does not implement is a TRB Error whatever slot
        // it names (review finding M5); only then does the slot matter.
        if (type is not (TrbTypeDisableSlot or TrbTypeResetDevice or TrbTypeAddressDevice or TrbTypeConfigureEndpoint
            or TrbTypeEvaluateContext or TrbTypeResetEndpoint or TrbTypeStopEndpoint or TrbTypeSetTrDequeuePointer))
        {
            PostCommandCompletionEvent(trbPointer, CompletionCodeTrbError, slotId);
            return;
        }

        // Every other command in this subset names a slot, and every one of them
        // requires that slot to have gone through Enable Slot first (plan's own
        // test: "an invalid slot gives CC 11").
        if (slotId is < 1 or > MaxDeviceSlots || !_slotEnabled[slotId])
        {
            PostCommandCompletionEvent(trbPointer, CompletionCodeSlotNotEnabled, slotId);
            return;
        }

        switch (type)
        {
            case TrbTypeDisableSlot:
                _slotEnabled[slotId] = false;
                ClearDataToggles(slotId);
                PostCommandCompletionEvent(trbPointer, EventCompletionCodeSuccess, slotId);
                break;
            case TrbTypeResetDevice:
                ExecuteResetDevice(slotId, trbPointer);
                break;
            case TrbTypeAddressDevice:
                ExecuteAddressDevice(dword0, dword1, dword3, slotId, trbPointer);
                break;
            case TrbTypeConfigureEndpoint:
                ExecuteConfigureEndpoint(dword0, dword1, dword3, slotId, trbPointer);
                break;
            case TrbTypeEvaluateContext:
                ExecuteEvaluateContext(dword0, dword1, slotId, trbPointer);
                break;
            case TrbTypeResetEndpoint:
                // Reset Endpoint resets the host side of the data toggle unless
                // TSP (Transfer State Preserve, bit 9) asks to keep it (xHCI 1.2
                // section 4.6.8). Review finding H1.
                if ((dword3 & CommandTrbBsrOrDcBit) == 0)
                {
                    ClearDataToggle(slotId, (int)((dword3 & CommandTrbEndpointIdMask) >> CommandTrbEndpointIdShift));
                }

                ExecuteSetEndpointState(dword3, slotId, EndpointStateStopped, trbPointer);
                break;
            case TrbTypeStopEndpoint:
                ExecuteSetEndpointState(dword3, slotId, EndpointStateStopped, trbPointer);
                break;
            case TrbTypeSetTrDequeuePointer:
                ExecuteSetTrDequeuePointer(dword0, dword1, dword3, slotId, trbPointer);
                break;
            default:
                // A command outside this subset (bandwidth, LPM, vendor ...)
                // completes with TRB Error, as QEMU answers an unknown command,
                // rather than claiming a success nothing performed. Review M5.
                PostCommandCompletionEvent(trbPointer, CompletionCodeTrbError, slotId);
                break;
        }
    }

    /// <summary>Reset Device (TRB type 17): the value Linux sends from xhci_discover_or_reset_device.</summary>
    private const uint TrbTypeResetDevice = 17;

    /// <summary>
    /// Reset Device (xHCI 1.2 section 4.6.11): after a USB bus reset of the
    /// device, the slot goes back to Default with USB Device Address 0 and
    /// Context Entries 1, every endpoint but EP0 is disabled, and every data
    /// toggle restarts.
    /// </summary>
    private void ExecuteResetDevice(byte slotId, ulong trbPointer)
    {
        if (!TryReadDeviceContext(slotId, out uint deviceContext))
        {
            PostCommandCompletionEvent(trbPointer, CompletionCodeResourceError, slotId);
            return;
        }

        for (int epId = 2; epId <= MaxEndpointContextIndex; epId++)
        {
            SetEndpointState(deviceContext + (uint)epId * ContextSize, EndpointStateDisabled);
        }

        uint addressAndState = _dma.ReadDword(deviceContext + SlotContextAddressAndStateOffset);
        addressAndState &= ~(SlotContextSlotStateMask | SlotContextDeviceAddressMask);
        addressAndState |= (uint)SlotStateDefault << SlotContextSlotStateShift;
        _dma.WriteDword(deviceContext + SlotContextAddressAndStateOffset, addressAndState);

        uint routeAndEntries = _dma.ReadDword(deviceContext + SlotContextRouteAndEntriesOffset);
        routeAndEntries = (routeAndEntries & ~SlotContextContextEntriesMask) | (1u << SlotContextContextEntriesShift);
        _dma.WriteDword(deviceContext + SlotContextRouteAndEntriesOffset, routeAndEntries);

        ClearDataToggles(slotId);
        PostCommandCompletionEvent(trbPointer, EventCompletionCodeSuccess, slotId);
    }

    /// <summary>Every endpoint of a slot starts again at DATA0 (enable, disable, reset, deconfigure).</summary>
    private void ClearDataToggles(byte slotId)
    {
        if (slotId is 0 or > MaxDeviceSlots)
        {
            return; // the slot ID comes from a guest TRB; the command itself reports Slot Not Enabled
        }

        for (int epId = 0; epId <= MaxEndpointContextIndex; epId++)
        {
            _endpointDataToggle[slotId, epId] = false;
        }
    }

    /// <summary>One endpoint starts again at DATA0 (Reset Endpoint, or added by Configure Endpoint).</summary>
    private void ClearDataToggle(byte slotId, int endpointId)
    {
        if (slotId is >= 1 and <= MaxDeviceSlots && endpointId is >= 0 and <= MaxEndpointContextIndex)
        {
            _endpointDataToggle[slotId, endpointId] = false;
        }
    }

    private void ExecuteEnableSlot(ulong trbPointer)
    {
        for (int slot = 1; slot <= MaxDeviceSlots; slot++)
        {
            if (!_slotEnabled[slot])
            {
                _slotEnabled[slot] = true;
                ClearDataToggles((byte)slot);
                PostCommandCompletionEvent(trbPointer, EventCompletionCodeSuccess, (byte)slot);
                return;
            }
        }
        PostCommandCompletionEvent(trbPointer, CompletionCodeNoSlotsAvailable, 0);
    }

    /// <summary>
    /// Address Device: copies the driver-built Slot Context and EP0 context
    /// from the Input Context into the Output Device Context at
    /// DCBAAP[slot], then either addresses the device on the wire (BSR=0,
    /// USB address = the slot ID, exactly as QEMU's xHCI does) or leaves it
    /// at the Default state without ever sending SET_ADDRESS (BSR=1, plan's
    /// "Costanti").
    /// </summary>
    private void ExecuteAddressDevice(uint dword0, uint dword1, uint dword3, byte slotId, ulong trbPointer)
    {
        if (!TryReachInputContext(dword0, dword1, out uint inputContext))
        {
            PostCommandCompletionEvent(trbPointer, CompletionCodeResourceError, slotId);
            return;
        }
        if (!TryReadDeviceContext(slotId, out uint deviceContext))
        {
            PostCommandCompletionEvent(trbPointer, CompletionCodeResourceError, slotId);
            return;
        }

        uint inputSlotBase = inputContext + ContextSize; // device-context index 0, shifted by the input control context
        uint inputEp0Base = inputContext + 2 * ContextSize; // device-context index 1 (EP0)
        uint outputSlotBase = deviceContext; // device-context index 0
        uint outputEp0Base = deviceContext + ContextSize; // device-context index 1 (EP0)

        CopyContextDword(inputSlotBase, outputSlotBase, SlotContextRouteAndEntriesOffset);
        CopyContextDword(inputSlotBase, outputSlotBase, SlotContextPortInfoOffset);
        CopyContextDword(inputSlotBase, outputSlotBase, SlotContextTtInfoOffset);

        CopyContextDword(inputEp0Base, outputEp0Base, EndpointContextStateOffset);
        CopyContextDword(inputEp0Base, outputEp0Base, EndpointContextTypeOffset);
        CopyContextDword(inputEp0Base, outputEp0Base, EndpointContextDequeueLowOffset);
        CopyContextDword(inputEp0Base, outputEp0Base, EndpointContextDequeueHighOffset);
        SetEndpointState(outputEp0Base, EndpointStateRunning);

        bool bsr = (dword3 & CommandTrbBsrOrDcBit) != 0;
        byte usbAddress = 0;
        if (!bsr)
        {
            usbAddress = slotId;
            // Address Device never resets the device itself: the actual USB bus reset
            // already happened at PORTSC.PR (xHCI 1.2 section 4.3.3), which is also the
            // only place a device renegotiates its speed (see IUsbDevice.SelectSpeed);
            // this command only ever sends SET_ADDRESS on top of that.
            IUsbDevice? device = FindDeviceForSlot(outputSlotBase);
            SendSetAddress(device, slotId);
        }

        uint addressAndState = _dma.ReadDword(outputSlotBase + SlotContextAddressAndStateOffset);
        addressAndState &= ~(SlotContextSlotStateMask | SlotContextDeviceAddressMask);
        addressAndState |= (uint)((bsr ? SlotStateDefault : SlotStateAddressed) << SlotContextSlotStateShift) | usbAddress;
        _dma.WriteDword(outputSlotBase + SlotContextAddressAndStateOffset, addressAndState);

        PostCommandCompletionEvent(trbPointer, EventCompletionCodeSuccess, slotId);
    }

    /// <summary>Sends SET_ADDRESS(<paramref name="slotId"/>) as a standard control transfer: SETUP (DATA0), then a zero-length status IN (DATA1).</summary>
    private static void SendSetAddress(IUsbDevice? device, byte slotId)
    {
        if (device is null)
        {
            return; // no device behind this slot's port: documented gap, nothing to address
        }
        Span<byte> setup = stackalloc byte[8];
        setup[0] = 0x00; // bmRequestType: host-to-device, standard, device recipient
        setup[1] = 5;    // bRequest: SET_ADDRESS
        setup[2] = slotId;
        setup[3] = 0;
        setup[4] = 0;
        setup[5] = 0;
        setup[6] = 0;
        setup[7] = 0;
        UsbTransactionResult setupResult = device.Transfer(WirePidSetup, ControlEndpointNumber, false, setup);
        if (setupResult.Handshake == UsbHandshake.Ack)
        {
            device.Transfer(WirePidIn, ControlEndpointNumber, true, Span<byte>.Empty);
        }
    }

    /// <summary>
    /// Configure Endpoint: validated exactly as QEMU's <c>xhci_config_ep</c>
    /// does (see <see cref="InputControlContextSlotOrEp0Bit"/>). DC=1
    /// deconfigures every endpoint but EP0 and returns the slot to
    /// Addressed; otherwise each dropped endpoint is disabled, each added
    /// one is copied from the Input Context and set Running, and the slot
    /// becomes Configured.
    /// </summary>
    private void ExecuteConfigureEndpoint(uint dword0, uint dword1, uint dword3, byte slotId, ulong trbPointer)
    {
        if (!TryReadDeviceContext(slotId, out uint deviceContext))
        {
            PostCommandCompletionEvent(trbPointer, CompletionCodeResourceError, slotId);
            return;
        }

        if ((dword3 & CommandTrbBsrOrDcBit) != 0) // DC: deconfigure
        {
            for (int epId = 2; epId <= MaxEndpointContextIndex; epId++)
            {
                SetEndpointState(deviceContext + (uint)epId * ContextSize, EndpointStateDisabled);
            }
            ClearDataToggles(slotId);
            SetSlotState(deviceContext, SlotStateAddressed);
            PostCommandCompletionEvent(trbPointer, EventCompletionCodeSuccess, slotId);
            return;
        }

        if (!TryReachInputContext(dword0, dword1, out uint inputContext))
        {
            PostCommandCompletionEvent(trbPointer, CompletionCodeResourceError, slotId);
            return;
        }

        uint dropFlags = _dma.ReadDword(inputContext + InputControlContextDropFlagsOffset);
        uint addFlags = _dma.ReadDword(inputContext + InputControlContextAddFlagsOffset);
        if ((dropFlags & InputControlContextSlotOrEp0Bit) != 0 || (addFlags & InputControlContextSlotAddBit) == 0)
        {
            PostCommandCompletionEvent(trbPointer, CompletionCodeTrbError, slotId);
            return;
        }

        uint inputSlotBase = inputContext + ContextSize;
        uint outputSlotBase = deviceContext;
        CopyContextDword(inputSlotBase, outputSlotBase, SlotContextRouteAndEntriesOffset);
        CopyContextDword(inputSlotBase, outputSlotBase, SlotContextPortInfoOffset);
        CopyContextDword(inputSlotBase, outputSlotBase, SlotContextTtInfoOffset);

        for (int epId = 2; epId <= MaxEndpointContextIndex; epId++)
        {
            uint bit = 1u << epId;
            uint outputEpBase = deviceContext + (uint)epId * ContextSize;
            if ((dropFlags & bit) != 0)
            {
                SetEndpointState(outputEpBase, EndpointStateDisabled);
            }
            if ((addFlags & bit) != 0)
            {
                uint inputEpBase = inputContext + (uint)(epId + 1) * ContextSize;
                CopyContextDword(inputEpBase, outputEpBase, EndpointContextStateOffset);
                CopyContextDword(inputEpBase, outputEpBase, EndpointContextTypeOffset);
                CopyContextDword(inputEpBase, outputEpBase, EndpointContextDequeueLowOffset);
                CopyContextDword(inputEpBase, outputEpBase, EndpointContextDequeueHighOffset);
                SetEndpointState(outputEpBase, EndpointStateRunning);
                ClearDataToggle(slotId, epId);   // a newly added endpoint starts at DATA0
            }
        }

        SetSlotState(deviceContext, SlotStateConfigured);
        PostCommandCompletionEvent(trbPointer, EventCompletionCodeSuccess, slotId);
    }

    /// <summary>Evaluate Context: only EP0's max packet size and the slot's own fields (plan's own scope), never the slot or endpoint state.</summary>
    private void ExecuteEvaluateContext(uint dword0, uint dword1, byte slotId, ulong trbPointer)
    {
        if (!TryReachInputContext(dword0, dword1, out uint inputContext) || !TryReadDeviceContext(slotId, out uint deviceContext))
        {
            PostCommandCompletionEvent(trbPointer, CompletionCodeResourceError, slotId);
            return;
        }

        uint addFlags = _dma.ReadDword(inputContext + InputControlContextAddFlagsOffset);
        if ((addFlags & InputControlContextSlotAddBit) != 0)
        {
            uint inputSlotBase = inputContext + ContextSize;
            uint outputSlotBase = deviceContext;
            CopyContextDword(inputSlotBase, outputSlotBase, SlotContextRouteAndEntriesOffset);
            CopyContextDword(inputSlotBase, outputSlotBase, SlotContextPortInfoOffset);
            CopyContextDword(inputSlotBase, outputSlotBase, SlotContextTtInfoOffset);
        }
        if ((addFlags & (1u << 1)) != 0) // A1: EP0
        {
            uint inputEp0Base = inputContext + 2 * ContextSize;
            uint outputEp0Base = deviceContext + ContextSize;
            uint maxPacketSize = _dma.ReadDword(inputEp0Base + EndpointContextTypeOffset) & EndpointMaxPacketSizeMask;
            uint outputType = _dma.ReadDword(outputEp0Base + EndpointContextTypeOffset) & ~EndpointMaxPacketSizeMask;
            _dma.WriteDword(outputEp0Base + EndpointContextTypeOffset, outputType | maxPacketSize);
        }

        PostCommandCompletionEvent(trbPointer, EventCompletionCodeSuccess, slotId);
    }

    private void ExecuteSetEndpointState(uint dword3, byte slotId, byte state, ulong trbPointer)
    {
        if (!TryReadDeviceContext(slotId, out uint deviceContext))
        {
            PostCommandCompletionEvent(trbPointer, CompletionCodeResourceError, slotId);
            return;
        }
        int epId = (int)((dword3 & CommandTrbEndpointIdMask) >> CommandTrbEndpointIdShift);
        if (epId is >= 1 and <= MaxEndpointContextIndex)
        {
            SetEndpointState(deviceContext + (uint)epId * ContextSize, state);
        }
        PostCommandCompletionEvent(trbPointer, EventCompletionCodeSuccess, slotId);
    }

    private void ExecuteSetTrDequeuePointer(uint dword0, uint dword1, uint dword3, byte slotId, ulong trbPointer)
    {
        if (!TryReadDeviceContext(slotId, out uint deviceContext))
        {
            PostCommandCompletionEvent(trbPointer, CompletionCodeResourceError, slotId);
            return;
        }
        int epId = (int)((dword3 & CommandTrbEndpointIdMask) >> CommandTrbEndpointIdShift);
        if (epId is >= 1 and <= MaxEndpointContextIndex)
        {
            uint epBase = deviceContext + (uint)epId * ContextSize;
            _dma.WriteDword(epBase + EndpointContextDequeueLowOffset, dword0 & (EndpointDequeuePointerMask | EndpointDequeueCycleStateBit));
            _dma.WriteDword(epBase + EndpointContextDequeueHighOffset, dword1);
        }
        PostCommandCompletionEvent(trbPointer, EventCompletionCodeSuccess, slotId);
    }

    // --- shared context helpers (also used by XhciController.Transfers.cs) --------

    private const int ContextSize = 32; // CSZ=0 (coordinator decision 5)
    private const int MaxEndpointContextIndex = 31; // EP0 (1) plus EP1 OUT/IN .. EP15 OUT/IN (2..31)
    private const byte ControlEndpointNumber = 0;

    private const byte WirePidSetup = 0x2D;
    private const byte WirePidIn = 0x69;
    private const byte WirePidOut = 0xE1;

    private bool TryReachInputContext(uint dword0, uint dword1, out uint address)
    {
        ulong pointer = ((ulong)dword1 << 32) | (dword0 & InputContextPointerMask);
        if (pointer > uint.MaxValue)
        {
            address = 0;
            return false; // above the 4 GB this bus master reaches (coordinator decision 5)
        }
        address = (uint)pointer;
        return true;
    }

    /// <summary>Reads DCBAAP[slot] (a 64-byte-aligned pointer, plan's "Costanti") and reaches the per-slot Device Context it names.</summary>
    private bool TryReadDeviceContext(byte slotId, out uint address)
    {
        ulong dcbaap = ((ulong)_dcbaapHigh << 32) | (_dcbaapLow & DcbaapPointerMask);
        if (dcbaap > uint.MaxValue - 8u)
        {
            address = 0;
            return false;
        }
        uint entryAddress = (uint)dcbaap + (uint)slotId * 8u;
        uint low = _dma.ReadDword(entryAddress);
        uint high = _dma.ReadDword(entryAddress + 4);
        ulong pointer = ((ulong)high << 32) | (low & DcbaapPointerMask);
        if (pointer == 0 || pointer > uint.MaxValue)
        {
            address = 0;
            return false;
        }
        address = (uint)pointer;
        return true;
    }

    private void CopyContextDword(uint sourceBase, uint destinationBase, uint offset)
        => _dma.WriteDword(destinationBase + offset, _dma.ReadDword(sourceBase + offset));

    private void SetSlotState(uint slotContextBase, byte state)
    {
        uint value = _dma.ReadDword(slotContextBase + SlotContextAddressAndStateOffset);
        value = (value & ~SlotContextSlotStateMask) | ((uint)state << SlotContextSlotStateShift);
        _dma.WriteDword(slotContextBase + SlotContextAddressAndStateOffset, value);
    }

    private void SetEndpointState(uint endpointContextBase, byte state)
    {
        uint value = _dma.ReadDword(endpointContextBase + EndpointContextStateOffset);
        value = (value & ~EndpointStateMask) | state;
        _dma.WriteDword(endpointContextBase + EndpointContextStateOffset, value);
    }

    /// <summary>The device physically attached behind a slot's own Root Hub Port Number field, or null if that port is empty or out of range.</summary>
    private IUsbDevice? FindDeviceForSlot(uint outputSlotBase)
    {
        uint portInfo = _dma.ReadDword(outputSlotBase + SlotContextPortInfoOffset);
        int port = (int)((portInfo & SlotContextRootHubPortMask) >> SlotContextRootHubPortShift) - 1; // 1-based in the context
        return (uint)port < PortCount ? _ports[port].Device : null;
    }

    private void PostCommandCompletionEvent(ulong trbPointer, uint completionCode, byte slotId)
    {
        uint dword0 = (uint)(trbPointer & 0xFFFF_FFF0u);
        uint dword1 = (uint)(trbPointer >> 32);
        uint dword2 = completionCode << CompletionCodeShift;
        uint extraDword3Bits = (uint)slotId << CommandTrbSlotIdShift;
        PostEvent(dword0, dword1, dword2, TrbTypeCommandCompletionEvent, extraDword3Bits);
    }

    private void CaptureCommands(BinaryWriter writer)
    {
        for (int slot = 1; slot <= MaxDeviceSlots; slot++)
        {
            writer.Write(_slotEnabled[slot]);
        }
        writer.Write(_commandRingPointer);
        writer.Write(_commandRingCycleState);
        writer.Write(_commandRingRunning);
        writer.Write(_commandRingAbortHighPending);
    }

    private void RestoreCommands(BinaryReader reader)
    {
        for (int slot = 1; slot <= MaxDeviceSlots; slot++)
        {
            _slotEnabled[slot] = reader.ReadBoolean();
        }
        _commandRingPointer = reader.ReadUInt64();
        _commandRingCycleState = reader.ReadBoolean();
        _commandRingRunning = reader.ReadBoolean();
        _commandRingAbortHighPending = reader.ReadBoolean();
    }
}
