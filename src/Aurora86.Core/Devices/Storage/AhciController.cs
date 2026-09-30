using Aurora86.Core.Devices.Pci;
using Aurora86.Core.Memory;

namespace Aurora86.Core.Devices.Storage;

/// <summary>
/// Intel ICH9's AHCI 1.3 host bus adapter (PCI 8086:2922, class 01/06/01):
/// the SATA HBA every AHCI-aware OS drives directly through a memory-mapped
/// register file -- Windows XP's <c>msahci</c>/<c>storahci</c>, Linux's
/// <c>libahci</c>, SeaBIOS's own AHCI boot path -- rather than through the
/// legacy IDE task file this project's <see cref="IdeController"/> models for
/// DOS and Windows 3.1/95.
///
/// Software finds this device the same way any PCI function is found (scan
/// configuration space, read BAR5), maps its 4 KB ABAR window over BAR5, then
/// builds a command list and one or more command tables in guest RAM per
/// port before writing the port's Command Issue register: see
/// <see cref="AhciPort"/> and <c>AhciPort.Commands.cs</c> for the register
/// map and the command engine that walks those structures. Six ports are
/// implemented (*choice*: enough for the disks this machine ever attaches);
/// an unattached port reports no device present. See <c>docs/AHCI.md</c> for
/// the full register map as implemented and the choices made where the AHCI
/// 1.3.1 specification (Oct 2012) leaves an emulator room to decide.
///
/// Every guest command runs to completion synchronously within the register
/// write that issues it: there is no PCI arbitration delay and no queued
/// command outstanding across two device ticks, exactly as
/// <see cref="BusMasterIdeController"/> already does for bus-master IDE.
/// </summary>
public sealed class AhciController : IMemoryDevice
{
    // --- PCI identity ----------------------------------------------------------

    private const ushort VendorId = 0x8086; // Intel
    private const ushort DeviceId = 0x2922; // ICH9 AHCI controller
    private const uint Revision = 0x02;
    private const uint ClassCode = 0x010601; // mass storage (01), SATA (06), AHCI prog-if (01)
    private const byte InterruptPinIntA = 1;

    /// <summary>Memory space (bit 1) and bus master (bit 2); there is no I/O BAR to gate with bit 0.</summary>
    private const ushort CommandWritableMask = 0x6;

    // --- BAR5 (ABAR) geometry ---------------------------------------------------

    private const int AbarBarIndex = 5;

    /// <summary>*choice*: covers the generic host control block (0-0x2Ch) and six ports (0x100-0x3FFh) with room to spare.</summary>
    private const uint AbarLength = 0x1000;

    private const uint PortRegisterBase = 0x100;
    private const uint PortRegisterStride = 0x80;
    private const int PortCount = 6;

    // --- generic host control register offsets (relative to ABAR) --------------

    private const uint RegCap = 0x00;
    private const uint RegGhc = 0x04;
    private const uint RegIs = 0x08;
    private const uint RegPi = 0x0C;
    private const uint RegVs = 0x10;
    private const uint RegCccCtl = 0x14;
    private const uint RegCccPorts = 0x18;
    private const uint RegEmLoc = 0x1C;
    private const uint RegEmCtl = 0x20;
    private const uint RegCap2 = 0x24;
    private const uint RegBohc = 0x28;

    // --- CAP (00h) fixed value: what this HBA advertises, never written -------

    private const int CapNpShift = 0;
    private const int CapNcsShift = 8;
    private const int CapIssShift = 20;
    private const int CapSamBit = 18;
    private const int CapScloBit = 24;
    private const int CapS64aBit = 31;

    private const uint CapValue =
        (PortCount - 1u) << CapNpShift |   // NP: number of ports minus one
        31u << CapNcsShift |                // NCS: 32 command slots minus one
        1u << CapSamBit |                   // SAM: AHCI-only mode (no legacy/IDE-compatible index/data pair)
        3u << CapIssShift |                 // ISS: Gen3, 6 Gb/s
        1u << CapScloBit |                  // SCLO: supports PxCMD.CLO, the command-list-override "clear busy status" bit
        1u << CapS64aBit;                   // S64A: accepts a 64-bit CLB/FB/CTBA (the high dword is ignored, see AhciPort)

    /// <summary>All six ports report an attached-or-not device (*choice*, see the class summary).</summary>
    private const uint PortImplementedMask = 0x3F;

    /// <summary>AHCI 1.3.0: major 0001h, minor 0300h.</summary>
    private const uint VsValue = 0x0001_0300;

    // --- GHC (04h) ---------------------------------------------------------------

    private const uint GhcHrBit = 1u << 0;
    private const uint GhcIeBit = 1u << 1;
    private const uint GhcAeBit = 1u << 31;

    private readonly PciMemoryBarWindow _abarWindow;
    private readonly IDmaMemory _dma;
    private readonly Action<bool> _interruptSink;
    private readonly AhciPort[] _ports = new AhciPort[PortCount];

    private bool _globalInterruptEnable;

    public PciFunction Function { get; }

    /// <summary>
    /// Builds the AHCI function and attaches BAR5 to <paramref name="memory"/>.
    /// <paramref name="pci"/> must already carry bus-master DMA memory and
    /// interrupt routing, exactly as <see cref="Machine"/>'s chipset sets up
    /// before any expansion card is built.
    /// </summary>
    public AhciController(PciBus pci, PhysicalMemoryBus memory, ushort functionAddress)
    {
        PciInterruptRouter router = pci.InterruptRouter
            ?? throw new InvalidOperationException("AHCI requires PCI interrupt routing.");
        if (PciInterruptRouter.Link(functionAddress, InterruptPinIntA) < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(functionAddress));
        }

        _dma = pci.DmaMemory
            ?? throw new InvalidOperationException("AHCI requires the chipset's bus-master DMA memory.");

        Function = new PciFunction(functionAddress, VendorId, DeviceId, 0, commandMask: CommandWritableMask);
        Function.Define(0x08, (ClassCode << 8) | Revision, 4); // overrides the base constructor's zeroed revision byte
        Function.DefineBar(AbarBarIndex, AbarLength, io: false);
        Function.Define(0x3C, 0, 1, 0xFF); // Interrupt Line bookkeeping only
        Function.Define(0x3D, InterruptPinIntA, 1); // INTA wired to the motherboard router
        pci.Add(Function);

        _abarWindow = new PciMemoryBarWindow(Function, AbarBarIndex, AbarLength, memory, this);
        _interruptSink = asserted => router.SetInterrupt(functionAddress, InterruptPinIntA, asserted);

        for (int i = 0; i < PortCount; i++)
        {
            _ports[i] = new AhciPort(i, _dma, () => Function.BusMasterEnabled);
        }

        Reset();
    }

    /// <summary>Puts a disk on a port. <paramref name="port"/> is 0-5.</summary>
    public void AttachDisk(int port, DiskImage disk)
    {
        Port(port).AttachDisk(disk);
    }

    /// <summary>Removes whatever disk is on a port, if any.</summary>
    public void DetachDisk(int port)
    {
        Port(port).AttachDisk(null);
    }

    private AhciPort Port(int port)
    {
        if ((uint)port >= PortCount)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
        return _ports[port];
    }

    /// <summary>
    /// Returns every port to its power-on register state -- the same state
    /// <see cref="WriteGhc"/> reaches through GHC.HR -- without touching PCI
    /// configuration or which disks are attached.
    /// </summary>
    public void Reset()
    {
        foreach (AhciPort port in _ports)
        {
            port.Reset();
        }
        _globalInterruptEnable = false;
        UpdateInterruptLine();
    }

    // --- IMemoryDevice: the ABAR ------------------------------------------------

    public byte ReadByte(uint offset)
    {
        uint whole = ReadDword(offset & ~3u);
        return (byte)(whole >> (8 * (int)(offset & 3)));
    }

    /// <summary>
    /// Composed the way every other byte-write-onto-a-dword-register in this
    /// codebase is: merge the new byte into the current value, then write
    /// the whole dword back. <see cref="WriteDwordInLanes"/> is what keeps
    /// that composition safe for an RW1C register (PxIS, PxSERR, PxSNTF):
    /// see its own summary for why a plain <see cref="WriteDword(uint,uint)"/>
    /// of the merged value would be wrong there.
    /// </summary>
    public void WriteByte(uint offset, byte value)
    {
        uint aligned = offset & ~3u;
        int lane = (int)(offset & 3);
        uint laneMask = 0xFFu << (8 * lane);
        uint whole = ReadDword(aligned);
        whole = (whole & ~laneMask) | ((uint)value << (8 * lane));
        WriteDwordInLanes(aligned, whole, laneMask);
    }

    public uint ReadDword(uint offset)
    {
        if (offset >= AbarLength)
        {
            return uint.MaxValue;
        }

        if (offset < PortRegisterBase)
        {
            return ReadGlobalRegister(offset);
        }

        int portIndex = (int)((offset - PortRegisterBase) / PortRegisterStride);
        if (portIndex >= PortCount)
        {
            return 0; // reserved space past the last implemented port
        }

        uint portOffset = offset - PortRegisterBase - (uint)(portIndex * PortRegisterStride);
        return _ports[portIndex].ReadRegister(portOffset);
    }

    public void WriteDword(uint offset, uint value) => WriteDwordInLanes(offset, value, 0xFFFFFFFFu);

    /// <summary>
    /// The one place every register write actually lands, dword or byte.
    /// <paramref name="writeMask"/> is all ones for a genuine dword access
    /// and a single byte lane for one composed by <see cref="WriteByte"/>.
    ///
    /// It matters because an RW1C register (PxIS, PxSERR, PxSNTF) cannot be
    /// written like an ordinary stored one: <see cref="WriteByte"/> builds
    /// <paramref name="value"/> by reading the register's *current* value
    /// and overwriting only its own byte, so the lanes it did not touch
    /// still carry whatever was already pending there. Handed to an RW1C
    /// register unmasked, writing that value back would clear those
    /// untouched, still-pending bits a second time -- a byte write meant to
    /// acknowledge one interrupt source would silently acknowledge every
    /// other one pending in a different lane too. Restricting the clear to
    /// <paramref name="writeMask"/> is what <see cref="AhciPort.WriteRegister"/>
    /// does with it; every other register ignores the mask and keeps using
    /// <paramref name="value"/> as today, since a plain stored register's
    /// untouched lanes were already correctly preserved by the merge above.
    /// </summary>
    private void WriteDwordInLanes(uint offset, uint value, uint writeMask)
    {
        if (offset >= AbarLength)
        {
            return;
        }

        if (offset < PortRegisterBase)
        {
            WriteGlobalRegister(offset, value);
        }
        else
        {
            int portIndex = (int)((offset - PortRegisterBase) / PortRegisterStride);
            if (portIndex < PortCount)
            {
                uint portOffset = offset - PortRegisterBase - (uint)(portIndex * PortRegisterStride);
                _ports[portIndex].WriteRegister(portOffset, value, writeMask);
            }
        }

        UpdateInterruptLine();
    }

    private uint ReadGlobalRegister(uint offset) => offset switch
    {
        RegCap => CapValue,
        RegGhc => ReadGhc(),
        RegIs => ComputeGlobalIs(),
        RegPi => PortImplementedMask,
        RegVs => VsValue,
        RegCccCtl or RegCccPorts or RegEmLoc or RegEmCtl or RegCap2 or RegBohc => 0,
        _ => 0, // reserved
    };

    private void WriteGlobalRegister(uint offset, uint value)
    {
        switch (offset)
        {
            case RegGhc:
                WriteGhc(value);
                break;

            case RegIs:
                // IS is not separate storage: each bit is the live OR of a
                // port's PxIS & PxIE (see ComputeGlobalIs). A driver's usual
                // "write PxIS, then write IS" clearing sequence still works,
                // because by the time this write lands PxIS has already
                // dropped the bit and IS recomputes to 0 on the next read.
                break;

            // RegCap, RegPi, RegVs, RegCccCtl, RegCccPorts, RegEmLoc,
            // RegEmCtl, RegCap2, RegBohc: fixed or unsupported, no write
            // capability implemented (CCC and enclosure management are out
            // of scope for this stage).
        }
    }

    private uint ReadGhc() => GhcAeBit | (_globalInterruptEnable ? GhcIeBit : 0u);

    /// <summary>
    /// GHC.HR: a self-clearing host reset. It resets every port's register
    /// file and forces GHC.IE back to 0, exactly as AHCI 1.3.1 section 3.1.1
    /// specifies; GHC.AE always reads 1 here because CAP.SAM is 1, so it is
    /// never stored. A write that sets HR is not combined with any other bit
    /// in the same dword: the reset takes priority, matching real hardware
    /// where the reset sequence overrides whatever else the write asked for.
    /// </summary>
    private void WriteGhc(uint value)
    {
        if ((value & GhcHrBit) != 0)
        {
            foreach (AhciPort port in _ports)
            {
                port.Reset();
            }
            _globalInterruptEnable = false;
            return;
        }

        _globalInterruptEnable = (value & GhcIeBit) != 0;
    }

    private uint ComputeGlobalIs()
    {
        uint bits = 0;
        for (int i = 0; i < PortCount; i++)
        {
            if ((_ports[i].InterruptStatus & _ports[i].InterruptEnable) != 0)
            {
                bits |= 1u << i;
            }
        }
        return bits;
    }

    /// <summary>INTx is asserted exactly when GHC.IE is set and at least one port has a pending, enabled interrupt source.</summary>
    private void UpdateInterruptLine()
    {
        _interruptSink(_globalInterruptEnable && ComputeGlobalIs() != 0);
    }

    // --- snapshot ----------------------------------------------------------------

    public const int SnapshotVersion = 1;

    /// <summary>
    /// Every guest-visible register on one port: CLB/CLBU, FB/FBU, IS, IE,
    /// the CMD bits software actually controls (ST, FRE -- SUD, POD and ICC
    /// are hardwired/self-clearing, see <see cref="AhciPort"/>'s own field
    /// summaries, so there is nothing of theirs to capture) plus the derived
    /// CCS and the TFES halt latch, the task file (STS/ERR), SCTL, SERR,
    /// SACT, CI, SNTF/FBS scratch, and the SET FEATURES 03h transfer mode
    /// IDENTIFY words 63/88 report. Not captured: the received-FIS and
    /// command-list bytes themselves, which live in guest RAM and are
    /// covered by the machine's own memory snapshot.
    /// </summary>
    private const int PortPayloadLength =
        sizeof(uint) * 6   // CLB, CLBU, FB, FBU, IS, IE
        + sizeof(bool) * 2 // ST, FRE
        + sizeof(int)      // CCS
        + sizeof(bool)     // halted (TFES latch)
        + sizeof(byte) * 2 // TFD status, TFD error
        + sizeof(uint) * 6 // SCTL, SERR, SACT, CI, SNTF, FBS
        + sizeof(bool) * 2 // selected transfer mode: has-value, Ultra
        + sizeof(byte);    // selected transfer mode: Level

    /// <summary>GHC.IE plus every port's payload. PCI configuration (including BAR5) is captured separately by <see cref="Pci.PciBus"/>; disk contents and the attached-disk set are not captured.</summary>
    public const int SnapshotLength = sizeof(bool) + PortPayloadLength * PortCount;

    public byte[] Capture()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(_globalInterruptEnable);
        foreach (AhciPort port in _ports)
        {
            port.Capture(writer);
        }

        writer.Flush();
        return stream.ToArray();
    }

    public void Restore(byte[] payload, int version)
    {
        if (version != SnapshotVersion || payload.Length != SnapshotLength)
        {
            throw new InvalidDataException("Invalid AHCI snapshot envelope.");
        }

        using var reader = new BinaryReader(new MemoryStream(payload));

        _globalInterruptEnable = reader.ReadBoolean();
        foreach (AhciPort port in _ports)
        {
            port.Restore(reader);
        }

        UpdateInterruptLine();
    }
}
