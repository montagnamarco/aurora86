namespace Aurora86.Core.Devices.Audio;

/// <summary>
/// The command ring (CORB out, RIRB back) and the immediate command
/// registers (IC/IR/ICS): the only two ways a driver ever reaches
/// <see cref="HdaCodec"/>'s node registers. See <see cref="HdaController"/>'s
/// own class summary for how this fits the rest of the controller, and
/// <c>docs/HD-AUDIO.md</c> for this file's own *choice* marks.
///
/// <b>Processing is synchronous (*choice*).</b> A real controller walks the
/// CORB in the background, one entry at a time, independently of the guest
/// instruction stream. This model instead drains every entry from
/// <c>CORBRP+1</c> up to the write pointer the instant that write pointer
/// changes (<see cref="ProcessCorb"/>), or the instant CORBCTL/RIRBCTL turn
/// either engine on: a driver that writes CORBWP and then immediately polls
/// RIRBSTS never has to wait, which matches every driver's own expectation
/// (the command interface is meant to feel synchronous) without modelling a
/// ring's own drain latency no guest depends on.
/// </summary>
public sealed partial class HdaController
{
    // --- register offsets, command interface (40h-6Fh) -----------------------

    private const int RegCommandInterfaceBase = 0x40;
    private const int RegDmaPositionBase = 0x70;

    private const int RegCorbLBase = 0x40;
    private const int RegCorbUBase = 0x44;
    private const int RegCorbWp = 0x48;
    private const int RegCorbRp = 0x4A;
    private const int RegCorbCtl = 0x4C;
    private const int RegCorbSts = 0x4D;
    private const int RegCorbSize = 0x4E;

    private const int RegRirbLBase = 0x50;
    private const int RegRirbUBase = 0x54;
    private const int RegRirbWp = 0x58;
    private const int RegRintCnt = 0x5A;
    private const int RegRirbCtl = 0x5C;
    private const int RegRirbSts = 0x5D;
    private const int RegRirbSize = 0x5E;

    private const int RegIc = 0x60;
    private const int RegIr = 0x64;
    private const int RegIcs = 0x68;

    // --- CORBCTL (4Ch) / CORBSTS (4Dh) ----------------------------------------

    private const byte CorbCtlCmeieBit = 1 << 0;
    private const byte CorbCtlCorbRunBit = 1 << 1;
    private const byte CorbStsCmeiBit = 1 << 0; // never set by this model: no CORB memory-error condition is detected (*choice*)

    /// <summary>256 entries only, fixed: bits 1:0 select the current size (10b = 256), bits 7:4 report only that size as a capability.</summary>
    private const byte CorbSizeValue = 0x42;

    // --- RIRBCTL (5Ch) / RIRBSTS (5Dh) -----------------------------------------

    private const byte RirbCtlRintCtlBit = 1 << 0;
    private const byte RirbCtlDmaEnBit = 1 << 1;
    private const byte RirbCtlOicBit = 1 << 2;
    private const byte RirbStsRintFlBit = 1 << 0;
    private const byte RirbStsOisBit = 1 << 2; // never set: no RIRB overrun condition is detected (*choice*, see the class summary's synchronous drain)
    private const byte RirbSizeValue = 0x42; // same convention as CORBSIZE

    // --- ICS/IRS (68h) ---------------------------------------------------------

    private const byte IcsIcbBit = 1 << 0;
    private const byte IcsIrvBit = 1 << 1;

    private uint _corbLBase;
    private uint _corbUBase;
    private byte _corbWritePointer;
    private byte _corbReadPointer;
    private bool _corbRpResetLatch;
    private byte _corbCtl;
    private byte _corbSts;

    private uint _rirbLBase;
    private uint _rirbUBase;
    private byte _rirbWritePointer;
    private byte _rintCnt;
    private byte _rirbCtl;
    private byte _rirbSts;

    /// <summary>How many responses have been written to the RIRB since the last time RINTFL was set -- see <see cref="ProcessCorb"/>'s own two trigger conditions.</summary>
    private int _rirbResponsesSinceInterrupt;

    private uint _ic;
    private uint _ir;
    private bool _icsIrv;

    private void ResetCommandInterface()
    {
        _corbLBase = 0;
        _corbUBase = 0;
        _corbWritePointer = 0;
        _corbReadPointer = 0;
        _corbRpResetLatch = false;
        _corbCtl = 0;
        _corbSts = 0;

        _rirbLBase = 0;
        _rirbUBase = 0;
        _rirbWritePointer = 0;
        _rintCnt = 0;
        _rirbCtl = 0;
        _rirbSts = 0;
        _rirbResponsesSinceInterrupt = 0;

        _ic = 0;
        _ir = 0;
        _icsIrv = false;
    }

    private byte ReadCommandRegisterByte(int offset) => offset switch
    {
        >= RegCorbLBase and < RegCorbLBase + 4 => (byte)(_corbLBase >> (8 * (offset - RegCorbLBase))),
        >= RegCorbUBase and < RegCorbUBase + 4 => (byte)(_corbUBase >> (8 * (offset - RegCorbUBase))),
        RegCorbWp => _corbWritePointer,
        RegCorbWp + 1 => 0, // reserved
        RegCorbRp => _corbReadPointer,
        RegCorbRp + 1 => (byte)(_corbRpResetLatch ? 0x80 : 0),
        RegCorbCtl => _corbCtl,
        RegCorbSts => _corbSts,
        RegCorbSize => CorbSizeValue,
        >= RegRirbLBase and < RegRirbLBase + 4 => (byte)(_rirbLBase >> (8 * (offset - RegRirbLBase))),
        >= RegRirbUBase and < RegRirbUBase + 4 => (byte)(_rirbUBase >> (8 * (offset - RegRirbUBase))),
        RegRirbWp => _rirbWritePointer,
        RegRirbWp + 1 => 0, // a reset pulse, never a latch: this half always reads 0
        RegRintCnt => _rintCnt,
        RegRintCnt + 1 => 0, // reserved
        RegRirbCtl => _rirbCtl,
        RegRirbSts => _rirbSts,
        RegRirbSize => RirbSizeValue,
        >= RegIc and < RegIc + 4 => (byte)(_ic >> (8 * (offset - RegIc))),
        >= RegIr and < RegIr + 4 => (byte)(_ir >> (8 * (offset - RegIr))),
        // Both defined bits (ICB and IRV) live in this same low byte; ICB
        // always reads 0 (the command already ran synchronously and
        // self-cleared) and the high byte (69h) is entirely reserved.
        RegIcs => (byte)(_icsIrv ? IcsIrvBit : 0),
        RegIcs + 1 => 0,
        _ => 0,
    };

    private void WriteCommandRegisterByte(int offset, byte value)
    {
        switch (offset)
        {
            case >= RegCorbLBase and < RegCorbLBase + 4:
                _corbLBase = MergeLane(_corbLBase, offset - RegCorbLBase, value);
                break;
            case >= RegCorbUBase and < RegCorbUBase + 4:
                _corbUBase = MergeLane(_corbUBase, offset - RegCorbUBase, value);
                break;
            case RegCorbWp:
                _corbWritePointer = value;
                ProcessCorb();
                break;
            case RegCorbWp + 1:
                break; // reserved
            case RegCorbRp:
                break; // the read pointer is hardware-owned; only the reset handshake (high byte) is writable
            case RegCorbRp + 1:
                WriteCorbRpHighByte(value);
                break;
            case RegCorbCtl:
                _corbCtl = (byte)(value & (CorbCtlCmeieBit | CorbCtlCorbRunBit));
                ProcessCorb(); // may itself call UpdateInterruptLine; called again below since CMEIE or CORBRUN can also change what is already pending
                UpdateInterruptLine();
                break;
            case RegCorbSts:
                _corbSts &= (byte)~(value & CorbStsCmeiBit); // RW1C
                UpdateInterruptLine();
                break;
            case RegCorbSize:
                break; // RO, 256 entries only
            case >= RegRirbLBase and < RegRirbLBase + 4:
                _rirbLBase = MergeLane(_rirbLBase, offset - RegRirbLBase, value);
                break;
            case >= RegRirbUBase and < RegRirbUBase + 4:
                _rirbUBase = MergeLane(_rirbUBase, offset - RegRirbUBase, value);
                break;
            case RegRirbWp:
                break; // the low byte of a plain write carries no meaning; only bit 15 (the reset pulse) does
            case RegRirbWp + 1:
                if ((value & 0x80) != 0)
                {
                    _rirbWritePointer = 0;
                }

                break;
            case RegRintCnt:
                _rintCnt = value;
                break;
            case RegRintCnt + 1:
                break; // reserved
            case RegRirbCtl:
                _rirbCtl = (byte)(value & (RirbCtlRintCtlBit | RirbCtlDmaEnBit | RirbCtlOicBit));
                ProcessCorb(); // may itself call UpdateInterruptLine; called again below since RINTCTL/RIRBOIC can also change what is already pending
                UpdateInterruptLine();
                break;
            case RegRirbSts:
                _rirbSts &= (byte)~(value & (RirbStsRintFlBit | RirbStsOisBit)); // RW1C
                UpdateInterruptLine();
                break;
            case RegRirbSize:
                break; // RO, 256 entries only
            case >= RegIc and < RegIc + 4:
                _ic = MergeLane(_ic, offset - RegIc, value);
                break;
            case >= RegIr and < RegIr + 4:
                break; // IR is hardware-owned; software never writes it
            case RegIcs:
                WriteIcsLowByte(value);
                break;
            case RegIcs + 1:
                break; // reserved
            default:
                break;
        }
    }

    private void WriteCorbRpHighByte(byte value)
    {
        bool resetRequested = (value & 0x80) != 0;
        _corbRpResetLatch = resetRequested;
        if (resetRequested)
        {
            _corbReadPointer = 0;
        }
    }

    private void WriteIcsLowByte(byte value)
    {
        if ((value & IcsIrvBit) != 0)
        {
            _icsIrv = false; // RW1C
        }

        if ((value & IcsIcbBit) != 0)
        {
            ExecuteImmediateCommand();
        }
    }

    /// <summary>Runs <see cref="_ic"/>'s own verb against the codec directly, bypassing the CORB/RIRB ring entirely -- the protocol a BIOS or an early boot loader uses before it has set up a ring at all.</summary>
    private void ExecuteImmediateCommand()
    {
        byte codecAddress = (byte)(_ic >> 28);
        if (codecAddress != HdaCodec.CodecAddress)
        {
            return; // no codec at that address: no response, ICB still self-clears
        }

        byte nid = (byte)((_ic >> 20) & 0x7F);
        uint verbAndPayload = _ic & 0xFFFFF;
        _ir = _codec.ExecuteVerb(nid, verbAndPayload);
        _icsIrv = true;
    }

    /// <summary>
    /// Drains every CORB entry from <c>CORBRP+1</c> up to the current write
    /// pointer while both CORBRUN and RIRBDMAEN are set, writing one RIRB
    /// entry per response. RINTFL (and, through it, the command interface's
    /// own interrupt) is set either after <see cref="_rintCnt"/> responses
    /// (0 meaning 256, the HDA spec's own convention) or once the ring
    /// empties with at least one response still short of that count -- the
    /// contract's own two trigger conditions.
    /// </summary>
    private void ProcessCorb()
    {
        bool corbRunning = (_corbCtl & CorbCtlCorbRunBit) != 0;
        bool rirbDmaEnabled = (_rirbCtl & RirbCtlDmaEnBit) != 0;
        if (!corbRunning || !rirbDmaEnabled || !Function.BusMasterEnabled)
        {
            return; // no CORB fetch and no RIRB response write while the function may not master the bus
        }

        // Only the low base register addresses memory in this emulator: see
        // IDmaMemory's own summary about installed RAM being reached
        // through a 32-bit address, exactly like every other bus master on
        // this machine. A driver that programs a non-zero upper base is
        // therefore never actually reachable here (*choice*).
        uint corbBase = _corbLBase & 0xFFFFFF80u; // 128-byte aligned
        uint rirbBase = _rirbLBase & 0xFFFFFF80u;
        int effectiveRintCnt = _rintCnt == 0 ? 256 : _rintCnt;

        bool interruptRaised = false;

        while (_corbReadPointer != _corbWritePointer)
        {
            _corbReadPointer = (byte)(_corbReadPointer + 1);
            uint verb = _dma.ReadDword(corbBase + (uint)_corbReadPointer * 4);
            byte codecAddress = (byte)(verb >> 28);
            byte nid = (byte)((verb >> 20) & 0x7F);
            uint verbAndPayload = verb & 0xFFFFF;

            if (codecAddress != HdaCodec.CodecAddress)
            {
                continue; // verbs to a codec address with no codec produce no response
            }

            uint response = _codec.ExecuteVerb(nid, verbAndPayload);
            WriteRirbEntry(rirbBase, response, codecAddress);

            _rirbResponsesSinceInterrupt++;
            if (_rirbResponsesSinceInterrupt >= effectiveRintCnt)
            {
                _rirbSts |= RirbStsRintFlBit;
                _rirbResponsesSinceInterrupt = 0;
                interruptRaised = true;
            }
        }

        if (_rirbResponsesSinceInterrupt > 0)
        {
            _rirbSts |= RirbStsRintFlBit;
            _rirbResponsesSinceInterrupt = 0;
            interruptRaised = true;
        }

        if (interruptRaised)
        {
            UpdateInterruptLine();
        }
    }

    /// <summary>Writes one 64-bit RIRB entry at <c>RIRBWP+1</c> (incrementing RIRBWP first, per the contract's own wording): the response dword, then a dword whose low nibble is the responding codec's address and whose bit 4 (unsolicited) is always clear here -- every entry this model writes answers a command.</summary>
    private void WriteRirbEntry(uint rirbBase, uint response, byte codecAddress)
    {
        _rirbWritePointer = (byte)(_rirbWritePointer + 1);
        uint entryAddress = rirbBase + (uint)_rirbWritePointer * 8;
        _dma.WriteDword(entryAddress, response);
        _dma.WriteDword(entryAddress + 4, (uint)(codecAddress & 0x0F));
    }

    /// <summary>The command interface's own qualified pending condition -- see <see cref="HdaController.ReadIntstsDword"/>'s summary for how INTCTL's own enables further gate this.</summary>
    private bool IsCommandInterfaceInterruptPending()
    {
        bool memoryError = (_corbSts & CorbStsCmeiBit) != 0 && (_corbCtl & CorbCtlCmeieBit) != 0;
        bool responseInterrupt = (_rirbSts & RirbStsRintFlBit) != 0 && (_rirbCtl & RirbCtlRintCtlBit) != 0;
        bool overrunInterrupt = (_rirbSts & RirbStsOisBit) != 0 && (_rirbCtl & RirbCtlOicBit) != 0;
        return memoryError || responseInterrupt || overrunInterrupt;
    }

    /// <summary>Merges one byte into one lane of a stored register, leaving the other three lanes untouched -- the same read-modify-write a genuine byte-wide PCI memory write performs.</summary>
    private static uint MergeLane(uint current, int lane, byte value)
        => (current & ~(0xFFu << (8 * lane))) | ((uint)value << (8 * lane));

    // --- snapshot: embedded in HdaController.Capture/Restore ------------------

    private const int CommandInterfacePayloadLength =
        sizeof(uint) * 2 // CORBLBASE, CORBUBASE
        + sizeof(byte) * 2 // CORB write/read pointer
        + sizeof(bool) // CORBRP reset latch
        + sizeof(byte) * 2 // CORBCTL, CORBSTS
        + sizeof(uint) * 2 // RIRBLBASE, RIRBUBASE
        + sizeof(byte) // RIRB write pointer
        + sizeof(byte) // RINTCNT
        + sizeof(byte) * 2 // RIRBCTL, RIRBSTS
        + sizeof(int) // responses-since-interrupt counter
        + sizeof(uint) * 2 // IC, IR
        + sizeof(bool); // ICS IRV

    private void CaptureCommandInterface(BinaryWriter writer)
    {
        writer.Write(_corbLBase);
        writer.Write(_corbUBase);
        writer.Write(_corbWritePointer);
        writer.Write(_corbReadPointer);
        writer.Write(_corbRpResetLatch);
        writer.Write(_corbCtl);
        writer.Write(_corbSts);

        writer.Write(_rirbLBase);
        writer.Write(_rirbUBase);
        writer.Write(_rirbWritePointer);
        writer.Write(_rintCnt);
        writer.Write(_rirbCtl);
        writer.Write(_rirbSts);
        writer.Write(_rirbResponsesSinceInterrupt);

        writer.Write(_ic);
        writer.Write(_ir);
        writer.Write(_icsIrv);
    }

    private void RestoreCommandInterface(BinaryReader reader)
    {
        _corbLBase = reader.ReadUInt32();
        _corbUBase = reader.ReadUInt32();
        _corbWritePointer = reader.ReadByte();
        _corbReadPointer = reader.ReadByte();
        _corbRpResetLatch = reader.ReadBoolean();
        _corbCtl = reader.ReadByte();
        _corbSts = reader.ReadByte();

        _rirbLBase = reader.ReadUInt32();
        _rirbUBase = reader.ReadUInt32();
        _rirbWritePointer = reader.ReadByte();
        _rintCnt = reader.ReadByte();
        _rirbCtl = reader.ReadByte();
        _rirbSts = reader.ReadByte();
        _rirbResponsesSinceInterrupt = reader.ReadInt32();

        _ic = reader.ReadUInt32();
        _ir = reader.ReadUInt32();
        _icsIrv = reader.ReadBoolean();
    }
}
