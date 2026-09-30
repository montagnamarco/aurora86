using System.Buffers.Binary;

namespace Aurora386.Core.Devices.Usb;

/// <summary>
/// Endpoint transfer rings (doorbells 1..<see cref="MaxDeviceSlots"/>,
/// target 1..31, plan's "Costanti"): control transfers on EP0 (Setup/Data/
/// Status) and Normal TRBs on bulk and interrupt endpoints, run to
/// completion synchronously on the doorbell that rings them (plan decision
/// 6, the same "campanello" engine <c>XhciController.Commands.cs</c> uses
/// for the command ring), against whichever <see cref="IUsbDevice"/> sits
/// behind the slot's own Root Hub Port Number (<see cref="FindDeviceForSlot"/>).
///
/// The endpoint context in guest memory is the only thing this class treats
/// as authoritative between doorbells: TR Dequeue Pointer, DCS and EP State
/// are read fresh at the start of every ring and written back at the end,
/// exactly as <c>XhciController.Commands.cs</c> does for the Slot and
/// Endpoint contexts. The one exception is the USB Data Toggle a bulk or
/// interrupt endpoint carries from one TD to the next: real xHCI silicon
/// keeps that internally too (nothing in the 32-byte Endpoint Context
/// stores it), so <see cref="_endpointDataToggle"/> is this controller's own
/// equivalent, persisted the same way a QH's own toggle bit survives across
/// qTDs in <c>EhciController.Schedule.cs</c>.
/// </summary>
public sealed partial class XhciController
{
    // --- TRB types (plan's "Costanti": TRBs 1-8, events 32-39) --------------------

    private const uint TrbTypeNormal = 1;
    private const uint TrbTypeSetupStage = 2;
    private const uint TrbTypeDataStage = 3;
    private const uint TrbTypeStatusStage = 4;
    private const uint TrbTypeEventDataTransfer = 7;
    private const uint TrbTypeNoOpTransfer = 8;
    private const uint TrbTypeTransferEvent = 32;

    // --- dword3 flag bits specific to transfer TRBs (plan's "Costanti") -----------

    private const uint TrbFlagInterruptOnShortPacket = 1u << 2; // ISP
    private const uint TrbFlagChain = 1u << 4; // CH
    private const uint TrbFlagInterruptOnCompletion = 1u << 5; // IOC
    private const uint TrbFlagImmediateData = 1u << 6; // IDT: Setup Stage, and OUT Normal/Data Stage of at most 8 bytes
    private const uint TrbDataStageDirectionBit = 1u << 16; // DIR: Data/Status Stage, 0 = OUT, 1 = IN

    private const uint TrbTransferLengthMask = 0x1FFFF; // dword2 bits 16:0

    // --- Transfer Event dword2/dword3 fields ---------------------------------------

    private const uint CompletionCodeUsbTransactionError = 4;
    private const uint CompletionCodeStallError = 6;
    private const uint CompletionCodeShortPacket = 13;

    private const int TransferEventEndpointIdShift = 16; // dword3 bits 20:16
    private const uint TransferEventEventDataBit = 1u << 2; // ED, dword3 bit 2

    // --- bounds ----------------------------------------------------------------------

    /// <summary>TRBs one doorbell (or one <see cref="Advance"/>-driven interrupt poll) can walk through, bounded for the same reason <see cref="MaxCommandTrbsPerDoorbell"/> is.</summary>
    private const int MaxTransferTrbsPerDoorbell = 256;

    /// <summary>How many interval periods <see cref="AdvanceTransferRings"/> ever catches up in one call, so a huge <paramref name="microseconds"/> jump (a slow host, a debugger pause) cannot spin forever re-polling the same endpoint.</summary>
    private const int MaxIntervalCatchUpPerAdvance = 8;

    // --- Endpoint Context fields this file also needs (Commands.cs owns the rest) ---

    private const uint EndpointContextIntervalOffset = EndpointContextStateOffset; // dword0, Interval at bits 23:16
    private const int EndpointIntervalShift = 16;

    private const int EndpointTypeShift = 3; // dword1 bits 5:3
    private const uint EndpointTypeMask = 0x7u << EndpointTypeShift;
    private const byte EndpointTypeIsochOut = 1;
    private const byte EndpointTypeInterruptOut = 3;
    private const byte EndpointTypeIsochIn = 5;
    private const byte EndpointTypeInterruptIn = 7;

    /// <summary>
    /// The USB Data Toggle a bulk or interrupt endpoint (device-context
    /// index 2..31) carries from one TD to the next -- see this class's own
    /// summary. Index 0 of each dimension is unused (slot 0 and EP0 do not
    /// use this array); EP0's own toggle is always deterministic per USB
    /// 2.0's own control-transfer rules and needs no persisted state.
    /// </summary>
    private readonly bool[,] _endpointDataToggle = new bool[MaxDeviceSlots + 1, MaxEndpointContextIndex + 1];

    /// <summary>Time accumulated since <see cref="AdvanceTransferRings"/> last polled this interrupt endpoint on its own, without a doorbell.</summary>
    private readonly double[,] _endpointPollElapsedMicroseconds = new double[MaxDeviceSlots + 1, MaxEndpointContextIndex + 1];

    private void ResetTransfers()
    {
        Array.Clear(_endpointDataToggle);
        Array.Clear(_endpointPollElapsedMicroseconds);
    }

    /// <summary>
    /// Interrupt endpoints poll on their own interval, not only when
    /// software rings the doorbell (plan's stage-4 spec): every enabled
    /// slot's Running interrupt endpoints are checked here, each bounded to
    /// <see cref="MaxIntervalCatchUpPerAdvance"/> catch-up polls so a large
    /// <paramref name="microseconds"/> jump cannot spin forever. Bulk
    /// endpoints are deliberately not polled here -- a bulk NAK only ever
    /// retries on the next doorbell (documented choice, plan's own either/or).
    /// </summary>
    private void AdvanceTransferRings(double microseconds)
    {
        if (!Function.BusMasterEnabled)
        {
            return;
        }

        for (int slot = 1; slot <= MaxDeviceSlots; slot++)
        {
            if (!_slotEnabled[slot] || !TryReadDeviceContext((byte)slot, out uint deviceContext))
            {
                continue;
            }

            for (int epId = 2; epId <= MaxEndpointContextIndex; epId++)
            {
                uint epBase = deviceContext + (uint)epId * ContextSize;
                uint typeDword = _dma.ReadDword(epBase + EndpointContextTypeOffset);
                byte epType = (byte)((typeDword & EndpointTypeMask) >> EndpointTypeShift);
                if (epType != EndpointTypeInterruptIn && epType != EndpointTypeInterruptOut)
                {
                    continue;
                }
                uint stateDword = _dma.ReadDword(epBase + EndpointContextStateOffset);
                if ((stateDword & EndpointStateMask) != EndpointStateRunning)
                {
                    continue;
                }

                int intervalExponent = (int)((stateDword >> EndpointIntervalShift) & 0xFF);
                double intervalMicroseconds = MicrosecondsPerMicroframe * (1 << Math.Min(intervalExponent, 16));

                _endpointPollElapsedMicroseconds[slot, epId] += microseconds;
                for (int catchUp = 0; catchUp < MaxIntervalCatchUpPerAdvance && _endpointPollElapsedMicroseconds[slot, epId] >= intervalMicroseconds; catchUp++)
                {
                    _endpointPollElapsedMicroseconds[slot, epId] -= intervalMicroseconds;
                    RunTransferRing(slot, epId);
                }
            }
        }
    }

    /// <summary>Doorbell 1..<see cref="MaxDeviceSlots"/>, target 1..31: runs one endpoint's transfer ring to completion, synchronously.</summary>
    private void RunTransferRing(int slotId, int endpointId)
    {
        if (!Function.BusMasterEnabled || (_command & CommandRunStop) == 0)
        {
            return;
        }
        if (slotId is < 1 or > MaxDeviceSlots || !_slotEnabled[slotId])
        {
            return;
        }
        if (endpointId is < 1 or > MaxEndpointContextIndex)
        {
            return;
        }
        if (!TryReadDeviceContext((byte)slotId, out uint deviceContext))
        {
            return;
        }

        uint epBase = deviceContext + (uint)endpointId * ContextSize;
        uint stateDword = _dma.ReadDword(epBase + EndpointContextStateOffset);
        byte epState = (byte)(stateDword & EndpointStateMask);
        if (epState is EndpointStateHalted or EndpointStateDisabled)
        {
            return; // a Halted endpoint does not run (plan's stage-4 spec); Reset Endpoint clears it first
        }
        if (epState == EndpointStateStopped)
        {
            _dma.WriteDword(epBase + EndpointContextStateOffset, (stateDword & ~EndpointStateMask) | EndpointStateRunning);
        }

        uint typeDword = _dma.ReadDword(epBase + EndpointContextTypeOffset);
        int maxPacketSize = (int)(typeDword >> EndpointMaxPacketSizeShift);
        if (maxPacketSize <= 0)
        {
            maxPacketSize = 8; // defensive: an unconfigured or malformed context must never divide, or loop, by zero
        }

        IUsbDevice? device = FindDeviceForSlot(deviceContext);
        if (device is null)
        {
            return; // the port behind this slot is empty: documented gap, nothing to run against
        }

        uint dequeueLow = _dma.ReadDword(epBase + EndpointContextDequeueLowOffset);
        uint dequeueHigh = _dma.ReadDword(epBase + EndpointContextDequeueHighOffset);
        ulong pointer = ((ulong)dequeueHigh << 32) | (dequeueLow & EndpointDequeuePointerMask);
        bool cycle = (dequeueLow & EndpointDequeueCycleStateBit) != 0;

        bool toggle = endpointId >= 2 && _endpointDataToggle[slotId, endpointId];
        bool halted = false;
        bool skippingChain = false;

        for (int steps = 0; steps < MaxTransferTrbsPerDoorbell; steps++)
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

            bool chain = (dword3 & TrbFlagChain) != 0;

            if (skippingChain)
            {
                // The short packet earlier in this TD already ended it (xHCI 1.2
                // section 4.10.1.1): every remaining chained TRB is consumed without
                // running, landing on the next TD's own first TRB.
                pointer += 16;
                skippingChain = chain;
                continue;
            }

            bool ioc = (dword3 & TrbFlagInterruptOnCompletion) != 0;
            int trbLength = (int)(dword2 & TrbTransferLengthMask);

            TrbOutcome outcome = ExecuteTransferTrb(device, endpointId, type, dword0, dword1, dword3, trbLength, maxPacketSize,
                ref toggle, out int transferred, out uint completionCode, out bool isEventData);

            if (outcome == TrbOutcome.Nak)
            {
                // The TD is left pending exactly where it is: the whole TRB (and any
                // toggle progress this attempt made) is retried from scratch next time,
                // whether that is the next doorbell or, for an interrupt endpoint, the
                // next AdvanceTransferRings poll.
                break;
            }

            pointer += 16;

            bool errored = completionCode is CompletionCodeStallError or CompletionCodeUsbTransactionError;
            bool shortPacket = completionCode == CompletionCodeShortPacket;
            if (errored)
            {
                halted = true;
            }

            bool postEvent = isEventData ? ioc : ioc || shortPacket || errored;
            if (postEvent)
            {
                uint residual = (uint)Math.Max(0, trbLength - transferred);
                PostTransferEvent(dword0, dword1, address, residual, completionCode, (byte)slotId, (byte)endpointId, isEventData);
            }

            if (errored)
            {
                break;
            }
            if (shortPacket && chain)
            {
                skippingChain = true;
            }
        }

        _dma.WriteDword(epBase + EndpointContextDequeueLowOffset, ((uint)pointer & EndpointDequeuePointerMask) | (cycle ? EndpointDequeueCycleStateBit : 0u));
        _dma.WriteDword(epBase + EndpointContextDequeueHighOffset, (uint)(pointer >> 32));
        if (endpointId >= 2)
        {
            _endpointDataToggle[slotId, endpointId] = toggle;
        }
        stateDword = _dma.ReadDword(epBase + EndpointContextStateOffset);
        _dma.WriteDword(epBase + EndpointContextStateOffset, (stateDword & ~EndpointStateMask) | (halted ? EndpointStateHalted : EndpointStateRunning));
    }

    private enum TrbOutcome
    {
        Nak,
        Done,
    }

    /// <summary>
    /// Runs one transfer TRB against <paramref name="device"/>: a Setup
    /// Stage (IDT, 8 bytes inline, SETUP/DATA0), a Data Stage (DIR, DATA1
    /// onward, in <paramref name="maxPacketSize"/> chunks), a Status Stage
    /// (a zero-length packet, DATA1), a Normal TRB on a bulk or interrupt
    /// endpoint (the endpoint's own persisted toggle, in max-packet chunks)
    /// or an Event Data TRB (no USB traffic at all, plan's stage-4 scope).
    /// </summary>
    private TrbOutcome ExecuteTransferTrb(IUsbDevice device, int endpointId, uint type, uint dword0, uint dword1, uint dword3,
        int trbLength, int maxPacketSize, ref bool toggle, out int transferred, out uint completionCode, out bool isEventData)
    {
        transferred = 0;
        completionCode = EventCompletionCodeSuccess;
        isEventData = false;

        switch (type)
        {
            case TrbTypeSetupStage:
            {
                Span<byte> setup = stackalloc byte[8];
                if ((dword3 & TrbFlagImmediateData) != 0)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(setup, dword0);
                    BinaryPrimitives.WriteUInt32LittleEndian(setup[4..], dword1);
                }
                else
                {
                    ulong setupAddress = ((ulong)dword1 << 32) | dword0;
                    for (int i = 0; i < 8 && setupAddress + (uint)i <= uint.MaxValue; i++)
                    {
                        setup[i] = _dma.ReadByte((uint)(setupAddress + (uint)i));
                    }
                }
                UsbTransactionResult result = device.Transfer(WirePidSetup, ControlEndpointNumber, false, setup);
                ApplyHandshake(result, 8, out transferred, out completionCode);
                return result.Handshake == UsbHandshake.Nak ? TrbOutcome.Nak : TrbOutcome.Done;
            }
            case TrbTypeDataStage:
            {
                bool directionIn = (dword3 & TrbDataStageDirectionBit) != 0;
                byte pid = directionIn ? WirePidIn : WirePidOut;
                bool dataToggle = true; // USB 2.0 section 8.5.2: the Data stage always starts at DATA1
                if (!directionIn && (dword3 & TrbFlagImmediateData) != 0)
                {
                    return RunImmediateOut(device, ControlEndpointNumber, ref dataToggle, dword0, dword1, trbLength,
                        out transferred, out completionCode);
                }
                TrbOutcome outcome = RunChunks(device, pid, ControlEndpointNumber, ref dataToggle, ((ulong)dword1 << 32) | dword0,
                    trbLength, maxPacketSize, out transferred, out completionCode);
                return outcome;
            }
            case TrbTypeStatusStage:
            {
                bool directionIn = (dword3 & TrbDataStageDirectionBit) != 0;
                byte pid = directionIn ? WirePidIn : WirePidOut;
                UsbTransactionResult result = device.Transfer(pid, ControlEndpointNumber, true, Span<byte>.Empty);
                ApplyHandshake(result, 0, out transferred, out completionCode);
                return result.Handshake == UsbHandshake.Nak ? TrbOutcome.Nak : TrbOutcome.Done;
            }
            case TrbTypeNormal:
            {
                byte pid = (endpointId & 1) == 1 ? WirePidIn : WirePidOut; // odd id = IN, even id = OUT (plan's "Costanti")
                byte usbEndpointNumber = (byte)(endpointId / 2);
                if (pid == WirePidOut && (dword3 & TrbFlagImmediateData) != 0)
                {
                    return RunImmediateOut(device, usbEndpointNumber, ref toggle, dword0, dword1, trbLength,
                        out transferred, out completionCode);
                }
                return RunChunks(device, pid, usbEndpointNumber, ref toggle, ((ulong)dword1 << 32) | dword0,
                    trbLength, maxPacketSize, out transferred, out completionCode);
            }
            case TrbTypeEventDataTransfer:
                isEventData = true;
                return TrbOutcome.Done;
            case TrbTypeNoOpTransfer:
            default:
                // A TRB outside this subset (isochronous, ...): a documented no-op, not a crash.
                return TrbOutcome.Done;
        }
    }

    /// <summary>
    /// An OUT TRB with IDT set carries its payload in the pointer field
    /// itself (xHCI 1.2 4.11.2.1 and 6.4.1.1: dword0 holds bytes 0-3, dword1
    /// bytes 4-7, TRB Transfer Length at most 8). The payload goes out as a
    /// single packet; a length above 8 is clamped, since the specification
    /// makes it invalid and there is no other buffer to read.
    /// </summary>
    private static TrbOutcome RunImmediateOut(IUsbDevice device, byte usbEndpointNumber, ref bool toggle, uint dword0, uint dword1,
        int trbLength, out int transferred, out uint completionCode)
    {
        Span<byte> inline = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(inline, dword0);
        BinaryPrimitives.WriteUInt32LittleEndian(inline[4..], dword1);
        int length = Math.Min(trbLength, inline.Length);

        UsbTransactionResult result = device.Transfer(WirePidOut, usbEndpointNumber, toggle, inline[..length]);
        if (result.Handshake == UsbHandshake.Nak)
        {
            transferred = 0;
            completionCode = EventCompletionCodeSuccess;
            return TrbOutcome.Nak;
        }
        if (result.Handshake == UsbHandshake.Ack)
        {
            toggle = !toggle;
        }
        ApplyHandshake(result, length, out transferred, out completionCode);
        return TrbOutcome.Done;
    }

    private static void ApplyHandshake(UsbTransactionResult result, int requestedLength, out int transferred, out uint completionCode)
    {
        switch (result.Handshake)
        {
            case UsbHandshake.Ack:
                transferred = requestedLength;
                completionCode = EventCompletionCodeSuccess;
                break;
            case UsbHandshake.Stall:
                transferred = 0;
                completionCode = CompletionCodeStallError;
                break;
            case UsbHandshake.Timeout:
                transferred = 0;
                completionCode = CompletionCodeUsbTransactionError;
                break;
            default: // Nak
                transferred = 0;
                completionCode = EventCompletionCodeSuccess;
                break;
        }
    }

    /// <summary>
    /// Moves <paramref name="length"/> bytes in <paramref name="maxPacketSize"/>
    /// chunks, exactly as a real endpoint would packetise it on the wire.
    /// A NAK on any chunk discards every byte this call itself moved for
    /// that attempt (a documented simplification: this bus master does not
    /// model the exact mid-TRB retry state real silicon keeps, only that the
    /// whole TRB is retried from scratch next time) and reports
    /// <see cref="TrbOutcome.Nak"/> without touching <paramref name="toggle"/>.
    /// A short IN packet ends the TRB's own data movement early and reports
    /// <see cref="CompletionCodeShortPacket"/>.
    /// </summary>
    private TrbOutcome RunChunks(IUsbDevice device, byte pid, byte usbEndpointNumber, ref bool toggle, ulong bufferPointer,
        int length, int maxPacketSize, out int transferred, out uint completionCode)
    {
        bool isIn = pid == WirePidIn;
        bool localToggle = toggle;
        int moved = 0;
        int remaining = length;
        uint code = EventCompletionCodeSuccess;

        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, maxPacketSize);
            if (bufferPointer + (uint)moved > uint.MaxValue - (uint)chunk)
            {
                transferred = 0;
                completionCode = CompletionCodeResourceError; // above the 4 GB this bus master reaches (coordinator decision 5)
                return TrbOutcome.Done;
            }
            uint chunkAddress = (uint)(bufferPointer + (uint)moved);

            byte[] buffer = new byte[chunk];
            if (!isIn)
            {
                for (int i = 0; i < chunk; i++)
                {
                    buffer[i] = _dma.ReadByte(chunkAddress + (uint)i);
                }
            }

            UsbTransactionResult result = device.Transfer(pid, usbEndpointNumber, localToggle, buffer.AsSpan(0, chunk));
            if (result.Handshake == UsbHandshake.Nak)
            {
                transferred = 0;
                completionCode = EventCompletionCodeSuccess;
                return TrbOutcome.Nak;
            }
            if (result.Handshake == UsbHandshake.Stall)
            {
                transferred = moved;
                completionCode = CompletionCodeStallError;
                return TrbOutcome.Done;
            }
            if (result.Handshake == UsbHandshake.Timeout)
            {
                transferred = moved;
                completionCode = CompletionCodeUsbTransactionError;
                return TrbOutcome.Done;
            }

            int actual = isIn ? result.Length : chunk;
            if (isIn && result.Length > 0)
            {
                for (int i = 0; i < result.Length; i++)
                {
                    _dma.WriteByte(chunkAddress + (uint)i, buffer[i]);
                }
            }
            localToggle = !localToggle;
            moved += actual;
            remaining -= actual;

            if (isIn && result.Length < chunk)
            {
                code = CompletionCodeShortPacket;
                break; // a short IN packet ends this TRB's own data movement early (xHCI 1.2 section 4.10.1.1)
            }
        }

        toggle = localToggle;
        transferred = moved;
        completionCode = code;
        return TrbOutcome.Done;
    }

    /// <summary>
    /// Posts a Transfer Event (type 32): the TRB pointer (or, for an Event
    /// Data TRB, its own dword0/dword1 verbatim with ED set), the residual
    /// length, the completion code, the endpoint ID and the slot ID (plan's
    /// stage-4 spec).
    /// </summary>
    private void PostTransferEvent(uint eventDataDword0, uint eventDataDword1, uint trbAddress, uint residual, uint completionCode,
        byte slotId, byte endpointId, bool isEventData)
    {
        uint dword0 = isEventData ? eventDataDword0 : trbAddress & 0xFFFF_FFF0u;
        uint dword1 = isEventData ? eventDataDword1 : 0u;
        uint dword2 = (residual & TrbTransferLengthMask) | (completionCode << CompletionCodeShift);
        uint extraDword3Bits = ((uint)endpointId << TransferEventEndpointIdShift) | ((uint)slotId << CommandTrbSlotIdShift)
            | (isEventData ? TransferEventEventDataBit : 0u);
        PostEvent(dword0, dword1, dword2, TrbTypeTransferEvent, extraDword3Bits);
    }

    private void CaptureTransfers(BinaryWriter writer)
    {
        for (int slot = 1; slot <= MaxDeviceSlots; slot++)
        {
            for (int epId = 2; epId <= MaxEndpointContextIndex; epId++)
            {
                writer.Write(_endpointDataToggle[slot, epId]);
                writer.Write(_endpointPollElapsedMicroseconds[slot, epId]);
            }
        }
    }

    private void RestoreTransfers(BinaryReader reader)
    {
        for (int slot = 1; slot <= MaxDeviceSlots; slot++)
        {
            for (int epId = 2; epId <= MaxEndpointContextIndex; epId++)
            {
                bool toggle = reader.ReadBoolean();
                double elapsed = reader.ReadDouble();
                if (!double.IsFinite(elapsed) || elapsed < 0)
                {
                    throw new InvalidDataException("Invalid xHCI transfer-ring snapshot.");
                }
                _endpointDataToggle[slot, epId] = toggle;
                _endpointPollElapsedMicroseconds[slot, epId] = elapsed;
            }
        }
    }
}
