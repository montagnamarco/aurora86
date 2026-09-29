using System.Numerics;
using Aurora386.Core.Devices.Pci;

namespace Aurora386.Core.Devices.Storage;

/// <summary>
/// One AHCI port's register file (AHCI 1.3.1 section 3.3, offset 100h +
/// n*80h in the ABAR) and the command engine that walks the command list a
/// driver built in guest RAM. See <see cref="AhciController"/> for the
/// device this belongs to and <c>docs/AHCI.md</c> for the register map.
///
/// Command execution -- reading the command header and its command table,
/// running the named ATA command against the attached <see cref="DiskImage"/>
/// and posting the completion FIS -- lives in the <c>AhciPort.Commands.cs</c>
/// half of this partial class; this half owns the register file itself: what
/// is stored, what is computed, and the CI/CMD engine gate.
/// </summary>
internal sealed partial class AhciPort
{
    // --- port register offsets (relative to this port's own 80h block) ---------

    private const uint RegClb = 0x00;
    private const uint RegClbu = 0x04;
    private const uint RegFb = 0x08;
    private const uint RegFbu = 0x0C;
    private const uint RegIs = 0x10;
    private const uint RegIe = 0x14;
    private const uint RegCmd = 0x18;
    private const uint RegTfd = 0x20;
    private const uint RegSig = 0x24;
    private const uint RegSsts = 0x28;
    private const uint RegSctl = 0x2C;
    private const uint RegSerr = 0x30;
    private const uint RegSact = 0x34;
    private const uint RegCi = 0x38;
    private const uint RegSntf = 0x3C;
    private const uint RegFbs = 0x40;

    private const uint ClbAlignmentMask = ~0x3FFu; // CLB is 1 KB aligned
    private const uint FbAlignmentMask = ~0xFFu;   // FB is 256 B aligned

    // --- PxIS / PxIE bits (AHCI 1.3.1 section 3.3.5/3.3.6) ---------------------

    internal const uint IsDhrsBit = 1u << 0;  // Device to Host Register FIS
    internal const uint IsPssBit = 1u << 1;   // PIO Setup FIS
    private const uint IsDssBit = 1u << 2;    // DMA Setup FIS
    private const uint IsSdbsBit = 1u << 3;   // Set Device Bits FIS
    private const uint IsUfsBit = 1u << 4;    // Unknown FIS
    internal const uint IsDpsBit = 1u << 5;   // Descriptor (PRDT entry) Processed
    private const uint IsPcsBit = 1u << 6;    // Port Connect Change
    private const uint IsDmpsBit = 1u << 7;   // Device Mechanical Presence
    private const uint IsPrcsBit = 1u << 22;  // PhyRdy Change
    private const uint IsIpmsBit = 1u << 23;  // Incorrect Port Multiplier
    private const uint IsOfsBit = 1u << 24;   // Overflow
    private const uint IsInfsBit = 1u << 26;  // Interface Non-fatal Error
    private const uint IsIfsBit = 1u << 27;   // Interface Fatal Error
    private const uint IsHbdsBit = 1u << 28;  // Host Bus Data Error
    private const uint IsHbfsBit = 1u << 29;  // Host Bus Fatal Error
    internal const uint IsTfesBit = 1u << 30; // Task File Error
    private const uint IsCpdsBit = 1u << 31;  // Cold Port Detect

    /// <summary>Every IS bit this HBA can ever set; IE only stores these, the rest read/write as 0.</summary>
    private const uint IsImplementedMask = IsDhrsBit | IsPssBit | IsDssBit | IsSdbsBit | IsUfsBit | IsDpsBit |
        IsPcsBit | IsDmpsBit | IsPrcsBit | IsIpmsBit | IsOfsBit | IsInfsBit | IsIfsBit | IsHbdsBit | IsHbfsBit |
        IsTfesBit | IsCpdsBit;

    // --- PxCMD bits (section 3.3.7) ---------------------------------------------

    private const uint CmdStBit = 1u << 0;

    /// <summary>
    /// SUD (Spin-Up Device): AHCI 1.3.1 section 3.3.7 makes this bit
    /// read-only, hardwired to 1, whenever CAP.SSS (Staggered Spin-up
    /// Support) is 0 -- which it always is on this HBA (CAP.SSS is fixed
    /// at 0, see <see cref="AhciController"/>'s CAP value). There is
    /// nothing to store: a write is simply not observable on the next read.
    /// </summary>
    private const uint CmdSudBit = 1u << 1;

    /// <summary>POD (Power On Device): the same CAP.SSS=0 rule as <see cref="CmdSudBit"/> hardwires this to 1 too.</summary>
    private const uint CmdPodBit = 1u << 2;

    private const uint CmdCloBit = 1u << 3;
    private const uint CmdFreBit = 1u << 4;
    private const int CmdCcsShift = 8;
    private const uint CmdCrBit = 1u << 14;
    private const uint CmdFrBit = 1u << 15;

    /// <summary>
    /// ICC (Interface Communication Control): software writes a link power
    /// state transition request here. This model has no link power state
    /// machine -- the requested transition is taken as completing at once,
    /// which is why AHCI 1.3.1 section 3.3.7 has software poll this field
    /// down to 0 ("No-Op/Idle") to know a transition finished: it always
    /// already reads 0 the instant after a write, and nothing is stored.
    /// </summary>
    private const int CmdIccShift = 28;
    private const uint CmdIccMask = 0xFu << CmdIccShift;

    // --- PxTFD bits (section 3.3.9): the packed ATA status/error the last command left ---

    private const byte TfdStatusBusyBit = 0x80;
    private const byte TfdStatusDrqBit = 0x08;
    private const byte TfdStatusErrBit = 0x01;
    private const byte TfdStatusDrdyBit = 0x40;
    private const byte TfdStatusDscBit = 0x10;

    /// <summary>A command finished cleanly: drive ready, seek/operation complete, no error.</summary>
    private const byte TfdStatusNormal = TfdStatusDrdyBit | TfdStatusDscBit;

    /// <summary>A command finished with the ATA ERR bit set; <see cref="_tfdError"/> names which.</summary>
    private const byte TfdStatusError = TfdStatusDrdyBit | TfdStatusDscBit | TfdStatusErrBit;

    private const byte AtaErrorAbort = 0x04;
    private const byte AtaErrorIdNotFound = 0x10;

    /// <summary>EXECUTE DEVICE DIAGNOSTIC's success code -- not an error despite living in the error register.</summary>
    private const byte AtaDiagnosticPassed = 0x01;

    // --- PxSIG (section 3.3.10) and PxSSTS (3.3.11): what a device negotiation would have found ---

    private const uint SignatureAtaDisk = 0x0000_0101;
    private const uint SignatureNoDevice = 0xFFFF_FFFF;

    /// <summary>DET=3 (device present, phy communication established), SPD=3 (Gen3, 6 Gb/s), IPM=1 (active).</summary>
    private const uint SstsDeviceReady = 0x3u | (0x3u << 4) | (0x1u << 8);

    // --- PxSCTL (3.3.12): DET is the only field this model acts on -------------

    private const uint SctlDetMask = 0xF;
    private const uint SctlDetComreset = 0x1;
    private const uint SctlDetIdle = 0x0;

    /// <summary>PxSERR.DIAG.X (bit 26): "device presence changed" -- set whenever a COMRESET completes, as real HBAs do.</summary>
    private const uint SerrDiagXBit = 1u << 26;

    private readonly IDmaMemory _dma;
    private readonly Func<bool> _busMasterEnabled;
    private DiskImage? _disk;

    /// <summary>
    /// The DMA mode a SET FEATURES 03h last selected (Commands.cs), reported
    /// back through IDENTIFY words 63/88. Defaults to Ultra DMA mode 5 at
    /// reset (*choice*, see A4 in docs/AHCI.md): a freshly attached SATA
    /// disk already has some transfer mode active before any driver
    /// negotiates one, and UDMA5 is a common real-drive default.
    /// </summary>
    private (bool Ultra, int Level)? _selectedTransferMode;

    private uint _clb, _clbu, _fb, _fbu;
    private uint _isBits, _ieBits;
    private bool _st, _fre;
    private int _ccs;

    /// <summary>Set when a command completed with PxTFD.STS.ERR: the engine stops walking CI until software clears PxCMD.ST (AHCI 1.3.1 section 6.2.2.1).</summary>
    private bool _halted;

    private byte _tfdStatus;
    private byte _tfdError;
    private uint _sctl;
    private uint _serr;
    private uint _sact;
    private uint _ci;
    private uint _sntf;
    private uint _fbs;

    internal int Index { get; }
    internal uint InterruptStatus => _isBits;
    internal uint InterruptEnable => _ieBits;

    internal AhciPort(int index, IDmaMemory dma, Func<bool> busMasterEnabled)
    {
        Index = index;
        _dma = dma;
        _busMasterEnabled = busMasterEnabled;
        Reset();
    }

    internal void AttachDisk(DiskImage? disk)
    {
        _disk = disk;
    }

    /// <summary>The power-on state of every register this port owns; leaves the attached disk untouched.</summary>
    internal void Reset()
    {
        _clb = 0;
        _clbu = 0;
        _fb = 0;
        _fbu = 0;
        _isBits = 0;
        _ieBits = 0;
        _st = false;
        _fre = false;
        _ccs = 0;
        _halted = false;
        _tfdStatus = TfdStatusNormal;
        _tfdError = 0;
        _sctl = 0;
        _serr = 0;
        _sact = 0;
        _ci = 0;
        _sntf = 0;
        _fbs = 0;
        _selectedTransferMode = (Ultra: true, Level: 5); // UDMA5 default, see the field's own summary
    }

    internal uint ReadRegister(uint offset) => offset switch
    {
        RegClb => _clb,
        RegClbu => _clbu,
        RegFb => _fb,
        RegFbu => _fbu,
        RegIs => _isBits,
        RegIe => _ieBits,
        RegCmd => ReadCmd(),
        RegTfd => ((uint)_tfdError << 8) | _tfdStatus,
        RegSig => _disk is null ? SignatureNoDevice : SignatureAtaDisk,
        RegSsts => _disk is null ? 0u : SstsDeviceReady,
        RegSctl => _sctl,
        RegSerr => _serr,
        RegSact => _sact,
        RegCi => _ci,
        RegSntf => _sntf,
        RegFbs => _fbs,
        _ => 0, // reserved within the port's own 80h block
    };

    /// <summary>
    /// <paramref name="writeMask"/> names which byte lanes of
    /// <paramref name="value"/> the guest actually addressed -- all-ones for
    /// a dword access, one or two 8-bit lanes for a byte or word access (see
    /// <see cref="AhciController.WriteByte"/>). Every register here except
    /// the three RW1C ones (IS, SERR -- SNTF is below) ignores it and keeps
    /// today's whole-dword behaviour, because storage registers already only
    /// change the bits the guest wrote (the caller composed
    /// <paramref name="value"/> from the current stored value plus the new
    /// lane). An RW1C register cannot use that same composition: reading
    /// back its own currently-pending bits and writing them straight back
    /// would clear them a second time, in lanes the guest's byte or word
    /// write never touched. Restricting the clear to <paramref
    /// name="writeMask"/> is what stops a byte write to PxIS's low lane from
    /// also clearing an unrelated bit pending in a different lane.
    /// </summary>
    internal void WriteRegister(uint offset, uint value, uint writeMask)
    {
        switch (offset)
        {
            case RegClb:
                _clb = value & ClbAlignmentMask;
                break;
            case RegClbu:
                _clbu = value;
                break;
            case RegFb:
                _fb = value & FbAlignmentMask;
                break;
            case RegFbu:
                _fbu = value;
                break;
            case RegIs:
                _isBits &= ~(value & writeMask & IsImplementedMask); // RW1C
                break;
            case RegIe:
                _ieBits = value & IsImplementedMask;
                break;
            case RegCmd:
                WriteCmd(value);
                break;
            case RegSctl:
                WriteSctl(value);
                break;
            case RegSerr:
                _serr &= ~(value & writeMask); // RW1C
                break;
            case RegSact:
                _sact = value;
                break;
            case RegCi:
                WriteCi(value);
                break;
            case RegSntf:
                _sntf &= ~(value & writeMask); // RW1C; nothing sets it (no hot-plug notification modelled)
                break;
            case RegFbs:
                _fbs = value; // stored only: FIS-based switching is unsupported (CAP.FBSS = 0)
                break;

            // RegTfd, RegSig, RegSsts: read-only, computed; writes ignored.
        }
    }

    private uint ReadCmd()
    {
        // SUD and POD (A5): hardwired to 1 because CAP.SSS = 0, section
        // 3.3.7. ICC: always reads 0, see its own field summary above.
        uint value = CmdSudBit | CmdPodBit;
        if (_st) value |= CmdStBit;
        if (_fre) value |= CmdFreBit;
        value |= (uint)_ccs << CmdCcsShift;
        if (_st) value |= CmdCrBit; // CR follows ST
        if (_fre) value |= CmdFrBit; // FR follows FRE
        return value;
    }

    /// <summary>
    /// Applies the software-writable PxCMD bits and, when ST is (or remains)
    /// set, tries to run whatever is already queued in CI -- covering both
    /// "software sets ST after CI already names a command" and a later CI
    /// write while ST is already 1 (<see cref="WriteCi"/> handles that case).
    /// SUD, POD and ICC are accepted but not stored: see their own summaries
    /// on <see cref="CmdSudBit"/>/<see cref="CmdPodBit"/>/<see cref="CmdIccShift"/>.
    /// </summary>
    private void WriteCmd(uint value)
    {
        bool wasStarted = _st;
        _st = (value & CmdStBit) != 0;
        _fre = (value & CmdFreBit) != 0;

        if ((value & CmdCloBit) != 0)
        {
            // Clear Busy Status: this engine never leaves BSY/DRQ set between
            // commands, so CLO has nothing to do beyond guaranteeing that and
            // reading back as already cleared (it is not stored at all).
            _tfdStatus &= unchecked((byte)~(TfdStatusBusyBit | TfdStatusDrqBit));
        }

        if (!_st)
        {
            // Clearing ST is how software recovers from a TFES halt (AHCI
            // 1.3.1 section 6.2.2.1); it also stops CR reading back set.
            _halted = false;

            if (wasStarted)
            {
                // AHCI 1.3.1 sections 3.3.13 (PxCI) and 3.3.14 (PxSACT): the
                // HBA clears both the instant ST goes from 1 to 0, so a
                // command a driver never got to see complete -- including
                // the slot a TFES halt left set -- cannot replay once
                // software restarts the engine and issues something new.
                _ci = 0;
                _sact = 0;
            }

            return;
        }

        ProcessCommandList();
    }

    /// <summary>Software may only set CI bits (issue commands); the HBA clears a bit itself as that command completes.</summary>
    private void WriteCi(uint value)
    {
        _ci |= value;
        if (_st)
        {
            ProcessCommandList();
        }
    }

    /// <summary>
    /// PxSCTL.DET's 1-then-0 write sequence models a software-issued
    /// COMRESET: on the instant it releases (DET back to 0) with a device
    /// attached, the link is modelled as up at once -- there is no real PHY
    /// to train. A D2H Register FIS carrying the ATA signature is posted
    /// (gated on FRE, like every FIS this model writes), PxIS.DHRS and
    /// PxSERR.DIAG.X (bit 26, "device presence changed") are set, exactly as
    /// a real HBA reports a device becoming visible after reset (AHCI 1.3.1
    /// section 3.3.12).
    /// </summary>
    private void WriteSctl(uint value)
    {
        uint previousDet = _sctl & SctlDetMask;
        _sctl = value;
        uint newDet = value & SctlDetMask;

        if (previousDet != SctlDetComreset || newDet != SctlDetIdle || _disk is null)
        {
            return;
        }

        _serr |= SerrDiagXBit;
        _tfdStatus = TfdStatusNormal;
        _tfdError = 0;
        PostD2HRegisterFis(interrupt: true, status: TfdStatusNormal, error: 0, sectorCount: 1, lbaLow: 1);
        _isBits |= IsDhrsBit;
    }

    /// <summary>
    /// Walks Command Issue from the lowest set slot up, running each command
    /// to completion before looking at the next -- there is no real overlap
    /// between commands in this synchronous engine. A command that leaves
    /// PxTFD.STS.ERR set halts the walk without clearing its own CI bit,
    /// matching AHCI 1.3.1 section 6.2.2.1's "software must clear ST" recovery.
    /// </summary>
    private void ProcessCommandList()
    {
        if (_halted || !_busMasterEnabled())
        {
            return;
        }

        while (_ci != 0)
        {
            int slot = BitOperations.TrailingZeroCount(_ci);
            _ccs = slot;
            bool success = ExecuteCommand(slot);
            if (!success)
            {
                _halted = true;
                _isBits |= IsTfesBit;
                return;
            }

            _ci &= ~(1u << slot);
        }
    }

    // --- snapshot ----------------------------------------------------------------

    internal void Capture(BinaryWriter writer)
    {
        writer.Write(_clb);
        writer.Write(_clbu);
        writer.Write(_fb);
        writer.Write(_fbu);
        writer.Write(_isBits);
        writer.Write(_ieBits);
        writer.Write(_st);
        writer.Write(_fre);
        writer.Write(_ccs);
        writer.Write(_halted);
        writer.Write(_tfdStatus);
        writer.Write(_tfdError);
        writer.Write(_sctl);
        writer.Write(_serr);
        writer.Write(_sact);
        writer.Write(_ci);
        writer.Write(_sntf);
        writer.Write(_fbs);
        writer.Write(_selectedTransferMode.HasValue);
        writer.Write(_selectedTransferMode?.Ultra ?? false);
        writer.Write((byte)(_selectedTransferMode?.Level ?? 0));
    }

    internal void Restore(BinaryReader reader)
    {
        _clb = reader.ReadUInt32();
        _clbu = reader.ReadUInt32();
        _fb = reader.ReadUInt32();
        _fbu = reader.ReadUInt32();
        _isBits = reader.ReadUInt32();
        _ieBits = reader.ReadUInt32();
        _st = reader.ReadBoolean();
        _fre = reader.ReadBoolean();
        _ccs = reader.ReadInt32();
        _halted = reader.ReadBoolean();
        _tfdStatus = reader.ReadByte();
        _tfdError = reader.ReadByte();
        _sctl = reader.ReadUInt32();
        _serr = reader.ReadUInt32();
        _sact = reader.ReadUInt32();
        _ci = reader.ReadUInt32();
        _sntf = reader.ReadUInt32();
        _fbs = reader.ReadUInt32();
        bool hasTransferMode = reader.ReadBoolean();
        bool transferModeUltra = reader.ReadBoolean();
        byte transferModeLevel = reader.ReadByte();
        _selectedTransferMode = hasTransferMode ? (transferModeUltra, transferModeLevel) : null;
    }
}
