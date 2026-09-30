namespace Aurora86.Core.Devices.Usb;

/// <summary>
/// The asynchronous (QH+qTD, control and bulk) and periodic (QH-only,
/// interrupt) schedules EHCI 1.0 chapter 3 and 4 describe. There are no
/// isochronous or split-transaction descriptors here (plan decision 5,
/// <c>plans/usb2-ehci.md</c>): every attached device is high speed, so an
/// iTD, siTD or FSTN link pointer -- anywhere a queue head was expected --
/// is a malformed guest schedule and raises a host system error exactly
/// like an out-of-range address.
///
/// A queue head's "overlay" (its Next/Alternate-Next qTD pointers, Token
/// and five buffer pointers, EHCI 1.0 section 3.6) is treated as the one
/// live copy of whichever qTD is currently executing: <see cref="FetchQtdIntoOverlay"/>
/// copies a freshly linked qTD's 32 bytes into it, and every status change
/// (Active clearing, Halted, Babble, XactErr, Total Bytes, Current Page and
/// the buffer offset) is written back to both the overlay and the
/// standalone qTD in guest memory it came from (EHCI 1.0 section 4.10.5) --
/// a driver (Linux's <c>qh_completions</c>) reads completion status from
/// the qTD's own memory directly, not only from the queue head. Data Toggle
/// Control (Endpoint Characteristics bit 14, not to be confused with the
/// unrelated Control Endpoint Flag at bit 27) decides whether a freshly
/// fetched qTD's own toggle bit is trusted (DTC=1) or the overlay's already
/// self-maintained one is kept instead (DTC=0, sections 3.6 and 4.10.2):
/// either way, once a qTD is executing, every successful data packet still
/// flips the overlay's toggle and writes it back, so a DTC=0 queue head's
/// own toggle keeps advancing correctly across many qTDs even though the
/// driver never has to compute it itself.
///
/// Every guest command still runs to completion synchronously within the
/// microframe that reaches it, exactly as <see cref="UhciController"/> and
/// <c>AhciController</c> already do. Every loop below is bounded: the async
/// and periodic list walks by <see cref="AsyncListTraversalBudget"/> and
/// <see cref="PeriodicListTraversalBudget"/> (a malformed or deliberately
/// circular list of queue heads), one queue head's own qTD chaining by
/// <see cref="MaxQtdStepsPerQueueHead"/> (a qTD whose Next pointer cycles
/// back on itself or on a ring of peers), and one qTD's own multi-packet
/// transfer by <see cref="MaxPacketsPerTransactionPass"/> (an endpoint whose
/// own Maximum Packet Length can never make the progress its qTD asks for).
/// </summary>
public sealed partial class EhciController
{
    private enum TransactionOutcome { Retry, Halted, Completed, ShortPacket }

    // --- queue head layout, EHCI 1.0 section 3.6 (offsets from a QH's own base) ---

    private const uint QueueHeadCharacteristicsOffset = 0x04; // Endpoint Characteristics
    private const uint QueueHeadCapabilitiesOffset = 0x08;    // Endpoint Capabilities (S-mask/C-mask)
    private const uint QueueHeadCurrentQtdOffset = 0x0C;      // Current qTD Pointer: which standalone qTD the overlay below was last loaded from
    private const uint QueueHeadNextQtdOffset = 0x10;         // overlay begins here: Next qTD Pointer
    private const uint QueueHeadAltNextQtdOffset = 0x14;      // Alternate Next qTD Pointer
    private const uint QueueHeadTokenOffset = 0x18;           // Token
    private const uint QueueHeadBufferPointerOffset = 0x1C;   // Buffer Pointer (Page 0); +4 per further page
    private const uint QueueHeadLength = 0x30;                // 12 dwords total

    /// <summary>A standalone qTD (EHCI 1.0 section 3.5) is exactly the queue head's overlay region: Next/Alt pointers, Token, 5 buffer pointers.</summary>
    private const uint QtdLength = 0x20;
    private const uint QtdTokenOffset = 0x08;         // the standalone qTD's own Token, mirroring QueueHeadTokenOffset's layout
    private const uint QtdBufferPointerOffset = 0x0C; // the standalone qTD's own Buffer Pointer (Page 0)

    private const uint DeviceAddressMask = 0x7F;               // Endpoint Characteristics bits 6:0
    private const int EndpointNumberShift = 8;                 // bits 11:8
    private const uint EndpointNumberMask = 0xF;
    private const int MaxPacketLengthShift = 16;                // bits 26:16
    private const uint MaxPacketLengthMask = 0x7FF;
    private const uint EndpointCharacteristicsDtcBit = 1u << 14; // Data Toggle Control -- see this file's own summary
    private const uint InterruptScheduleMaskField = 0xFF;       // Endpoint Capabilities bits 7:0 (S-mask)

    // --- link pointer, shared by the periodic list, ASYNCLISTADDR and a queue head's own horizontal link ---

    private const uint LinkTerminate = 1u << 0;   // T
    private const uint LinkTypeMask = 3u << 1;    // Typ, bits 2:1
    private const uint LinkTypeQueueHead = 1u << 1; // 01b: the only type this schedule ever places
    private const uint LinkAddressMask = 0xFFFF_FFE0u; // 32-byte aligned

    // --- qTD/overlay Token, EHCI 1.0 section 3.5 ---

    private const uint TokenXactErr = 1u << 3;
    private const uint TokenBabble = 1u << 4;
    private const uint TokenHalted = 1u << 6;
    private const uint TokenActive = 1u << 7;
    private const int TokenPidShift = 8;
    private const uint TokenPidMask = 0x3;               // bits 9:8: 00 OUT, 01 IN, 10 SETUP, 11 reserved
    private const int TokenCerrShift = 10;
    private const uint TokenCerrMask = 0x3;              // bits 11:10, the error retry counter
    private const int TokenCurrentPageShift = 12;
    private const uint TokenCurrentPageMask = 0x7;       // bits 14:12: which of the 5 buffer pointers is current
    private const uint TokenIoc = 1u << 15;
    private const int TokenTotalBytesShift = 16;
    private const uint TokenTotalBytesMask = 0x7FFF;     // bits 30:16, bytes remaining in this qTD
    private const uint TokenDataToggle = 1u << 31;       // dt
    private const uint TokenErrorStatusMask = TokenHalted | TokenBabble | TokenXactErr;

    private const byte QtdPidOut = 0;
    private const byte QtdPidIn = 1;
    private const byte QtdPidSetup = 2;
    private const byte QtdPidReserved = 3;

    // The same USB wire PID byte values UhciController and IUsbDevice.Transfer already use (USB 2.0 section 8.3.1).
    private const byte WirePidOut = 0xE1;
    private const byte WirePidIn = 0x69;
    private const byte WirePidSetup = 0x2D;

    private const int BufferPointerCount = 5;
    private const uint BufferPageAddressMask = 0xFFFF_F000u; // bits 31:12
    private const uint BufferOffsetMask = 0xFFFu;            // Buffer Pointer (Page 0) bits 11:0, the live byte offset
    private const int PageSize = 4096;

    /// <summary>
    /// The largest single packet this controller ever moves: a high-speed
    /// interrupt or isochronous endpoint's own ceiling (USB 2.0 section
    /// 5.7.3/5.6.3), wider than a high-speed bulk endpoint's 512 bytes. A
    /// Maximum Packet Length past this (the Endpoint Characteristics field
    /// itself can encode up to 2047) is clamped, never faulted -- see
    /// <see cref="ExecuteOneTransaction"/>.
    /// </summary>
    private const int MaxPacketBufferLength = 1024;

    /// <summary>Bounds a circular async list or a malformed periodic chain, exactly as <see cref="UhciController.ExecuteFrame"/> bounds its own frame-list walk.</summary>
    private const int AsyncListTraversalBudget = 256;
    private const int PeriodicListTraversalBudget = 256;

    /// <summary>Bounds how many qTDs one queue head can fetch-and-run within a single microframe (see this file's own summary).</summary>
    private const int MaxQtdStepsPerQueueHead = 16;

    /// <summary>Bounds how many packets one qTD's own multi-packet transfer can attempt within a single microframe (see this file's own summary).</summary>
    private const int MaxPacketsPerTransactionPass = 128;

    private const uint FrameListIndexMask = 0x3FF; // the periodic list is always 1024 entries (HCCPARAMS' programmable length flag is 0)

    // --- periodic schedule -----------------------------------------------------------

    private void ExecutePeriodicMicroframe()
    {
        // PERIODICLISTBASE 0 is a legal physical address (EHCI 1.0 places no reserved
        // value on it); only PSE, checked by the caller, decides whether this schedule
        // runs at all.
        uint frameNumber = (uint)(_frameIndex >> MicroframesPerFrameShift) & FrameListIndexMask;
        uint entryAddress = _periodicListBase + frameNumber * 4;
        if (!InRam(entryAddress, 4))
        {
            Fatal();
            return;
        }

        uint link = _dma.ReadDword(entryAddress);
        uint microframeBit = 1u << (_frameIndex & MicroframesPerFrameMask);
        for (int visited = 0; visited < PeriodicListTraversalBudget && (link & LinkTerminate) == 0; visited++)
        {
            if ((link & LinkTypeMask) != LinkTypeQueueHead)
            {
                Fatal();
                return;
            }

            uint address = link & LinkAddressMask;
            if (!InRam(address, (int)QueueHeadLength))
            {
                Fatal();
                return;
            }

            uint capabilities = _dma.ReadDword(address + QueueHeadCapabilitiesOffset);
            if ((capabilities & InterruptScheduleMaskField & microframeBit) != 0)
            {
                ExecuteQueueHead(address);
                if ((_command & CommandRun) == 0)
                {
                    return;
                }
            }

            link = _dma.ReadDword(address); // the queue head's own horizontal link, continuing this frame slot's chain
        }
    }

    // --- asynchronous schedule ---------------------------------------------------------

    /// <summary>
    /// Walks the circular reclamation list once, starting at ASYNCLISTADDR
    /// (0 is as legal a physical address here as anywhere else; only ASE,
    /// checked by the caller, decides whether this schedule runs at all).
    /// Every guest command runs to completion synchronously (see the class
    /// summary), so there is nothing left "in progress" once this returns:
    /// a doorbell rung by <see cref="WriteUsbCommand"/> has already fired IAA
    /// by the time software could observe it.
    /// </summary>
    private void ExecuteAsyncSchedule()
    {
        uint start = _asyncListAddress & LinkAddressMask;
        uint address = start;
        for (int visited = 0; visited < AsyncListTraversalBudget; visited++)
        {
            if (!InRam(address, (int)QueueHeadLength))
            {
                Fatal();
                return;
            }

            ExecuteQueueHead(address);
            if ((_command & CommandRun) == 0)
            {
                return;
            }

            uint link = _dma.ReadDword(address);
            if ((link & LinkTypeMask) != LinkTypeQueueHead)
            {
                Fatal();
                return;
            }

            address = link & LinkAddressMask;
            if (address == start)
            {
                return; // completed one full circuit of the reclamation list
            }
        }
    }

    // --- the qTD engine shared by both schedules -------------------------------------

    /// <summary>
    /// Runs one queue head for up to <see cref="MaxQtdStepsPerQueueHead"/>
    /// steps: an overlay that is Halted and not Active (Linux's permanent
    /// async head is built exactly this way, with Maximum Packet Length 0)
    /// has nothing to do and is skipped, never faulted. Otherwise, if the
    /// overlay is idle, the next queued qTD is fetched (following the
    /// Alternate pointer instead of Next right after a short packet, EHCI
    /// 1.0 section 4.10.2); a freshly fetched qTD that is not itself Active
    /// yet is loaded for visibility and this pass stops there, rather than
    /// looking further ahead at its own Next pointer -- which is what keeps
    /// a qTD that links back to itself, or to a peer that does the same,
    /// from ever being walked more than once here. An Active transaction is
    /// executed and, on success, the loop keeps chaining depth-first through
    /// further qTDs -- mirroring how <see cref="UhciController.ExecuteFrame"/>
    /// walks one TD queue within a frame -- so a driver's whole
    /// SETUP/DATA/STATUS chain for one control transfer can retire within a
    /// single microframe when nothing NAKs.
    /// </summary>
    private void ExecuteQueueHead(uint address)
    {
        uint characteristics = _dma.ReadDword(address + QueueHeadCharacteristicsOffset);
        byte deviceAddress = (byte)(characteristics & DeviceAddressMask);
        byte endpoint = (byte)((characteristics >> EndpointNumberShift) & EndpointNumberMask);
        int maxPacketLength = (int)((characteristics >> MaxPacketLengthShift) & MaxPacketLengthMask);
        bool dataToggleControl = (characteristics & EndpointCharacteristicsDtcBit) != 0;

        uint fetchOffset = QueueHeadNextQtdOffset;
        for (int step = 0; step < MaxQtdStepsPerQueueHead; step++)
        {
            uint token = _dma.ReadDword(address + QueueHeadTokenOffset);
            if ((token & TokenActive) == 0)
            {
                if ((token & TokenHalted) != 0)
                {
                    return; // permanently halted until a driver clears it: nothing to do
                }

                uint pointer = _dma.ReadDword(address + fetchOffset);
                fetchOffset = QueueHeadNextQtdOffset; // only a short packet's outcome redirects the next fetch; reset once consumed
                if ((pointer & LinkTerminate) != 0)
                {
                    return; // nothing queued
                }

                // A qTD the driver has linked but not yet armed (Active
                // clear) is not loaded at all: the queue head keeps pointing
                // at it, so it runs once the driver sets Active (EHCI 1.0
                // section 4.10.2; QEMU's ehci_state_fetchqtd). Loading it and
                // stopping would make the next pass follow its stale Next
                // pointer and lose it -- independent review, finding 5.
                uint qtdAddress = pointer & LinkAddressMask;
                if (!InRam(qtdAddress, (int)QtdLength))
                {
                    Fatal();
                    return;
                }

                uint qtdToken = _dma.ReadDword(qtdAddress + QtdTokenOffset);
                if ((qtdToken & TokenActive) == 0)
                {
                    return;
                }

                FetchQtdIntoOverlay(address, qtdAddress, dataToggleControl);
                continue;
            }

            TransactionOutcome outcome = ExecuteOneTransaction(address, deviceAddress, endpoint, maxPacketLength);
            if (outcome == TransactionOutcome.ShortPacket)
            {
                fetchOffset = QueueHeadAltNextQtdOffset;
            }
            else if (outcome != TransactionOutcome.Completed)
            {
                return; // Retry (NAK, or a timeout that has not exhausted CERR) or Halted: this pass is done here
            }
        }
    }

    /// <summary>
    /// Runs the overlay's current transaction, possibly repeating it for
    /// further packets of the same qTD when more bytes remain and the
    /// packet just sent or received was full sized (EHCI 1.0 section
    /// 3.5.6): the qTD stays Active throughout, only its Total Bytes,
    /// Current Page and buffer offset advance, exactly as real hardware
    /// leaves them for a retry to resume from after a NAK. Every status
    /// change is written back through <see cref="WriteQtdToken"/> and
    /// <see cref="WriteQtdBufferOffset"/>, reaching both the overlay and the
    /// qTD's own memory.
    /// </summary>
    private TransactionOutcome ExecuteOneTransaction(uint queueHeadAddress, byte deviceAddress, byte endpoint, int maxPacketLength)
    {
        IUsbDevice? device = FindDevice(deviceAddress);

        // Allocated once outside the packet loop below: a qTD spanning several packets
        // reuses this same buffer for each one, rather than growing the native stack per pass.
        Span<byte> buffer = stackalloc byte[MaxPacketBufferLength];

        for (int packetsThisPass = 0; packetsThisPass < MaxPacketsPerTransactionPass; packetsThisPass++)
        {
            uint token = _dma.ReadDword(queueHeadAddress + QueueHeadTokenOffset);
            int totalBytes = (int)((token >> TokenTotalBytesShift) & TokenTotalBytesMask);
            int currentPage = (int)((token >> TokenCurrentPageShift) & TokenCurrentPageMask);
            byte pidField = (byte)((token >> TokenPidShift) & TokenPidMask);
            bool dataToggle = (token & TokenDataToggle) != 0;

            // Every fresh attempt clears the previous pass' own error status (Halted,
            // Babble, XactErr): those describe only the latest transaction, not a
            // running history, exactly as UhciController.ExecuteTd clears its own
            // status byte before applying a new result.
            uint attemptToken = token & ~TokenErrorStatusMask;

            if (pidField == QtdPidReserved || currentPage >= BufferPointerCount)
            {
                Fatal();
                return TransactionOutcome.Halted;
            }

            int packetLength = Math.Min(Math.Min(maxPacketLength, totalBytes), MaxPacketBufferLength);
            if (packetLength <= 0 && totalBytes > 0)
            {
                // This endpoint's own Maximum Packet Length cannot move the bytes this
                // qTD still has queued (Linux's permanent async head uses 0, but never
                // arms a qTD with a nonzero total on it): leave it Active, exactly as a
                // permanently NAKing device would, rather than attempting a zero-length
                // packet that can never make progress.
                return TransactionOutcome.Retry;
            }

            byte wirePid = MapPid(pidField);
            uint[] pages = ReadBufferPagePointers(queueHeadAddress);
            int currentOffset = (int)(_dma.ReadDword(queueHeadAddress + QueueHeadBufferPointerOffset) & BufferOffsetMask);

            Span<byte> packet = buffer[..packetLength];
            if (wirePid != WirePidIn && !CopyFromGuest(pages, currentPage, currentOffset, packet))
            {
                Fatal();
                return TransactionOutcome.Halted;
            }

            UsbTransactionResult result = device?.Transfer(wirePid, endpoint, dataToggle, packet)
                ?? new UsbTransactionResult(UsbHandshake.Timeout);

            switch (result.Handshake)
            {
                case UsbHandshake.Nak:
                    return TransactionOutcome.Retry;

                case UsbHandshake.Stall:
                    Retire(queueHeadAddress, attemptToken, TokenHalted);
                    return TransactionOutcome.Halted;

                case UsbHandshake.Timeout:
                {
                    uint cerr = (token >> TokenCerrShift) & TokenCerrMask;
                    if (cerr == 0)
                    {
                        // A zero error counter means unlimited retries (EHCI 1.0 section 3.5): nothing to decrement or record.
                        return TransactionOutcome.Retry;
                    }
                    cerr--;
                    // XactErr is set on every timeout while CERR still counts down, not only
                    // once it is exhausted (EHCI 1.0 section 4.10): only Halted waits for that.
                    uint updated = (attemptToken & ~(TokenCerrMask << TokenCerrShift)) | (cerr << TokenCerrShift) | TokenXactErr;
                    if (cerr == 0)
                    {
                        Retire(queueHeadAddress, updated, TokenHalted);
                        return TransactionOutcome.Halted;
                    }
                    WriteQtdToken(queueHeadAddress, updated);
                    return TransactionOutcome.Retry;
                }

                case UsbHandshake.Ack:
                    if (result.Length < 0 || result.Length > packetLength)
                    {
                        // Babble: the device answered with more than the buffer this packet offered it.
                        Retire(queueHeadAddress, attemptToken, TokenHalted | TokenBabble);
                        return TransactionOutcome.Halted;
                    }
                    if (wirePid == WirePidIn && !CopyToGuest(pages, currentPage, currentOffset, packet[..result.Length]))
                    {
                        Fatal();
                        return TransactionOutcome.Halted;
                    }

                    bool shortPacket = wirePid == WirePidIn && result.Length < packetLength;
                    bool finished = shortPacket || result.Length == totalBytes;
                    int newAbsoluteOffset = currentOffset + result.Length;
                    int newPage = currentPage + newAbsoluteOffset / PageSize;
                    int newOffset = newAbsoluteOffset % PageSize;

                    uint updatedToken = attemptToken
                        & ~(TokenTotalBytesMask << TokenTotalBytesShift)
                        & ~(TokenCurrentPageMask << TokenCurrentPageShift);
                    updatedToken |= (uint)(totalBytes - result.Length) << TokenTotalBytesShift;
                    updatedToken |= (uint)newPage << TokenCurrentPageShift;
                    updatedToken ^= TokenDataToggle; // hardware writes the flipped toggle back after every successful data packet
                    if (finished)
                    {
                        updatedToken &= ~TokenActive;
                    }
                    WriteQtdToken(queueHeadAddress, updatedToken);
                    WriteQtdBufferOffset(queueHeadAddress, newOffset);

                    if (!finished)
                    {
                        continue; // more packets remain in this same qTD; the qTD itself stays Active
                    }

                    // IOC asserts USBINT on any completion that asked for it; a short packet
                    // asserts it too, independently of IOC (EHCI 1.0 section 2.3.2).
                    if ((token & TokenIoc) != 0 || shortPacket)
                    {
                        _status |= StatusUsbInterrupt;
                    }
                    return shortPacket ? TransactionOutcome.ShortPacket : TransactionOutcome.Completed;

                default:
                    return TransactionOutcome.Retry;
            }
        }

        return TransactionOutcome.Retry; // this pass' own packet budget is exhausted; a later pass resumes
    }

    /// <summary>Clears Active, applies the given error status bits, and raises USBERRINT (and USBINT too, if this qTD asked for IOC).</summary>
    private void Retire(uint queueHeadAddress, uint token, uint statusFlags)
    {
        uint updated = (token & ~TokenActive) | statusFlags;
        WriteQtdToken(queueHeadAddress, updated);
        _status |= StatusUsbErrorInterrupt;
        if ((token & TokenIoc) != 0)
        {
            _status |= StatusUsbInterrupt;
        }
    }

    private static byte MapPid(byte pidField) => pidField switch
    {
        QtdPidOut => WirePidOut,
        QtdPidIn => WirePidIn,
        QtdPidSetup => WirePidSetup,
        _ => WirePidOut,
    };

    private IUsbDevice? FindDevice(byte address)
    {
        for (int i = 0; i < PortCount; i++)
        {
            if (_ports[i].Enabled && _ports[i].Device?.Find(address) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>
    /// Loads a queue head's overlay (Next/Alt qTD pointers, Token, 5 buffer
    /// pointers) from the standalone qTD in guest RAM it now points to,
    /// byte for byte -- except the Data Toggle bit when
    /// <paramref name="dataToggleControl"/> is false: EHCI 1.0 sections 3.6
    /// and 4.10.2 then have hardware keep whatever toggle the overlay
    /// already carries instead of taking the qTD's own, which is what lets
    /// a driver leave every qTD's own dt bit at a fixed placeholder value
    /// and trust the controller's self-maintained toggle across a whole run
    /// of qTDs (see this file's own summary).
    /// </summary>
    private bool FetchQtdIntoOverlay(uint queueHeadAddress, uint qtdAddress, bool dataToggleControl)
    {
        if (!InRam(qtdAddress, (int)QtdLength))
        {
            Fatal();
            return false;
        }

        uint preservedToggle = dataToggleControl ? 0 : _dma.ReadDword(queueHeadAddress + QueueHeadTokenOffset) & TokenDataToggle;
        for (uint i = 0; i < QtdLength; i += 4)
        {
            _dma.WriteDword(queueHeadAddress + QueueHeadNextQtdOffset + i, _dma.ReadDword(qtdAddress + i));
        }
        if (!dataToggleControl)
        {
            uint token = _dma.ReadDword(queueHeadAddress + QueueHeadTokenOffset);
            _dma.WriteDword(queueHeadAddress + QueueHeadTokenOffset, (token & ~TokenDataToggle) | preservedToggle);
        }
        _dma.WriteDword(queueHeadAddress + QueueHeadCurrentQtdOffset, qtdAddress);
        return true;
    }

    private uint[] ReadBufferPagePointers(uint queueHeadAddress)
    {
        var pointers = new uint[BufferPointerCount];
        for (int i = 0; i < BufferPointerCount; i++)
        {
            pointers[i] = _dma.ReadDword(queueHeadAddress + QueueHeadBufferPointerOffset + (uint)(i * 4)) & BufferPageAddressMask;
        }
        return pointers;
    }

    /// <summary>
    /// The physical address of byte <paramref name="byteIndex"/> of the
    /// current transaction, walking from <paramref name="currentPage"/>/
    /// <paramref name="currentOffset"/> into a further page of
    /// <paramref name="pages"/> exactly as a packet that straddles a 4 KB
    /// boundary needs to (EHCI 1.0 section 3.5.4: up to 5 pages per qTD).
    /// </summary>
    private static bool TryComputeBufferAddress(uint[] pages, int currentPage, int currentOffset, int byteIndex, out uint address)
    {
        int absolute = currentOffset + byteIndex;
        int page = currentPage + absolute / PageSize;
        if ((uint)page >= BufferPointerCount)
        {
            address = 0;
            return false;
        }
        address = pages[page] | (uint)(absolute % PageSize);
        return true;
    }

    private bool CopyFromGuest(uint[] pages, int currentPage, int currentOffset, Span<byte> packet)
    {
        for (int i = 0; i < packet.Length; i++)
        {
            if (!TryComputeBufferAddress(pages, currentPage, currentOffset, i, out uint address) || !InRam(address, 1))
            {
                return false;
            }
            packet[i] = _dma.ReadByte(address);
        }
        return true;
    }

    private bool CopyToGuest(uint[] pages, int currentPage, int currentOffset, ReadOnlySpan<byte> packet)
    {
        for (int i = 0; i < packet.Length; i++)
        {
            if (!TryComputeBufferAddress(pages, currentPage, currentOffset, i, out uint address) || !InRam(address, 1))
            {
                return false;
            }
            _dma.WriteByte(address, packet[i]);
        }
        return true;
    }

    /// <summary>
    /// Writes a qTD's Token to both the queue head's overlay and the
    /// standalone qTD in guest memory it was fetched from (its own Current
    /// qTD Pointer, offset 0Ch): EHCI 1.0 section 4.10.5 requires the
    /// guest's own copy to reflect every status change, not only the
    /// overlay -- a driver (Linux's <c>qh_completions</c>) reads completion
    /// status from the qTD's own memory directly.
    /// </summary>
    private void WriteQtdToken(uint queueHeadAddress, uint token)
    {
        _dma.WriteDword(queueHeadAddress + QueueHeadTokenOffset, token);
        uint qtdAddress = _dma.ReadDword(queueHeadAddress + QueueHeadCurrentQtdOffset);
        if (qtdAddress != 0 && InRam(qtdAddress, (int)QtdLength))
        {
            _dma.WriteDword(qtdAddress + QtdTokenOffset, token);
        }
    }

    /// <summary>The buffer-offset counterpart of <see cref="WriteQtdToken"/>: rewrites only Buffer Pointer (Page 0)'s live offset field (bits 11:0), leaving its page address untouched, in both the overlay and the standalone qTD.</summary>
    private void WriteQtdBufferOffset(uint queueHeadAddress, int newOffset)
    {
        uint overlayPointer0 = _dma.ReadDword(queueHeadAddress + QueueHeadBufferPointerOffset);
        overlayPointer0 = (overlayPointer0 & BufferPageAddressMask) | ((uint)newOffset & BufferOffsetMask);
        _dma.WriteDword(queueHeadAddress + QueueHeadBufferPointerOffset, overlayPointer0);

        uint qtdAddress = _dma.ReadDword(queueHeadAddress + QueueHeadCurrentQtdOffset);
        if (qtdAddress != 0 && InRam(qtdAddress, (int)QtdLength))
        {
            uint qtdPointer0 = _dma.ReadDword(qtdAddress + QtdBufferPointerOffset);
            qtdPointer0 = (qtdPointer0 & BufferPageAddressMask) | ((uint)newOffset & BufferOffsetMask);
            _dma.WriteDword(qtdAddress + QtdBufferPointerOffset, qtdPointer0);
        }
    }
}
