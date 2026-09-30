using Aurora86.Core.Devices.Pci;
using Aurora86.Core.IO;
using Aurora86.Core.Memory;

namespace Aurora86.Core.Devices.Network;

/// <summary>
/// Realtek's 1997 RTL8139(C) Fast Ethernet controller: the card every
/// in-box Windows 98SE/2000/XP driver and every Linux <c>8139too</c> build
/// already knows, because it shipped on more motherboards of that era than
/// any other NIC.
///
/// Two ideas set it apart from the DP8390 core the RTL8029 reuses:
///
/// <b>It masters the PCI bus itself.</b> There is no remote-DMA port to walk
/// byte by byte; the driver hands the chip a physical address (<c>RBSTART</c>
/// for receive, <c>TSADn</c> for transmit) and the chip reads or writes host
/// RAM directly, gated by the PCI command register's bus-master bit exactly
/// as every other bus master on this machine is (see
/// <see cref="Pci.PciFunction.BusMasterEnabled"/>).
///
/// <b>The receive ring lives in host RAM, dword-aligned.</b> Each delivered
/// frame is a four-byte status/length header immediately followed by the
/// frame, padded to a whole number of four-byte words -- not the DP8390's
/// 256-byte pages. <c>CAPR</c> is the driver's read cursor, offset by -16 by
/// convention (so a freshly reset ring reads back <c>FFF0h</c>, since the
/// very first frame is written at ring offset zero); <c>CBR</c> is the
/// chip's own write cursor, never offset. When the ring's <c>WRAP</c> bit is
/// set a frame that would straddle the ring's end is allowed to spill into
/// 1500 extra bytes past the nominal end instead of being physically split,
/// which is what lets a driver read every frame as one contiguous run.
///
/// Four independent transmit descriptor slots (<c>TSD0-3</c>/<c>TSAD0-3</c>)
/// exist so a driver can queue several frames without waiting for each one;
/// this model has no transmit timing at all, so every slot fires the moment
/// its <c>OWN</c> bit is cleared with a non-zero size, in whatever order the
/// driver writes them -- see the class summary in <c>docs/RTL8139.md</c> for
/// what a real chip's own descriptor ordering would add on top.
///
/// What this model does not attempt: the 93C46 serial EEPROM's bit-bang
/// protocol (9346CR only gates configuration-register writes and a
/// synchronous "auto-load" that reloads the synthetic MAC, exactly the
/// RTL8029's own EEPROM-control simplification), early-transmit underrun,
/// PCI burst/threshold timing, and the missed-packet and timer-count
/// registers a diagnostic utility might poll but no driver depends on.
/// </summary>
public sealed class Rtl8139 : IIoDwordDevice, IMemoryDevice
{
    // --- identity -------------------------------------------------------

    private const ushort VendorId = 0x10EC;
    private const ushort DeviceId = 0x8139;
    private const uint ClassCode = 0x020000; // network controller, Ethernet

    /// <summary>
    /// The revision 8139too's own chip table expects for a "C" step
    /// (RTL_GIGA_MAC_VER... table entry "RTL-8139C"), per the coordinator's
    /// decision recorded in <c>plans/specs/rtl8029-rtl8139.md</c>: the 8139C
    /// datasheet's PDF text layer could not be extracted, so this value is
    /// taken as given rather than independently re-derived from the PDF.
    /// </summary>
    private const uint Revision = 0x10;

    private const uint CommandWritableMask = 0x7; // I/O space, memory space, bus master

    // --- BAR geometry -----------------------------------------------------

    /// <summary>
    /// The real chip's register file is 256 bytes; both BARs decode exactly
    /// that many, whatever their own reported size.
    /// </summary>
    private const int RegisterFileLength = 0x100;

    private const uint IoWindowLength = RegisterFileLength;

    /// <summary>
    /// <see cref="PhysicalMemoryBus.TryMapDevice"/> only ever places a device
    /// on a 4 KB page boundary, for a length that is itself a multiple of
    /// 4 KB -- the real chip's own 256-byte BAR1 cannot decode on this bus.
    /// The BAR is therefore sized to 4 KB rather than 256 bytes; offsets
    /// 100h-FFFh of that window answer as an empty bus, exactly as the real
    /// 256-byte decode would for anything past its own end.
    /// </summary>
    private const uint MemoryWindowLength = 0x1000;

    /// <summary>Full 32 address bits kept significant, as the RTL8029's own BAR0 does and for the same reason: a base above the 64 KB port space must refuse to decode, not alias down into it.</summary>
    private const uint IoBarAddressMask = 0xFFFFFF00u;

    // --- register offsets, byte 0 of each field ----------------------------

    private const int Idr0 = 0x00;
    private const int IdrLength = 6;
    private const int Mar0 = 0x08;
    private const int MarLength = 8;
    private const int Tsd0 = 0x10;
    private const int Tsad0 = 0x20;
    private const int TransmitSlotCount = 4;
    private const int RbStartOffset = 0x30;
    private const int CommandRegisterOffset = 0x37;
    private const int CaprOffset = 0x38;
    private const int CbrOffset = 0x3A;
    private const int ImrOffset = 0x3C;
    private const int IsrOffset = 0x3E;
    private const int TcrOffset = 0x40;
    private const int RcrOffset = 0x44;
    private const int EepromCommandOffset = 0x50;
    private const int Config0Offset = 0x51;
    private const int Config1Offset = 0x52;
    private const int MediaStatusOffset = 0x58;
    private const int Config3Offset = 0x59;
    private const int Config4Offset = 0x5A;
    private const int BmcrOffset = 0x62;
    private const int BmsrOffset = 0x64;
    private const int AnarOffset = 0x66;
    private const int AnlparOffset = 0x68;
    private const int AnerOffset = 0x6A;

    // --- CR (command register), offset 37h ---------------------------------

    private const byte CrBufferEmpty = 0x01; // RO, computed: the ring's read and write cursors coincide
    private const byte CrTransmitEnable = 0x04;
    private const byte CrReceiveEnable = 0x08;
    private const byte CrReset = 0x10; // self-clearing
    private const byte CrDriverWritableMask = CrTransmitEnable | CrReceiveEnable;

    // --- TSD (transmit status descriptor), 4 bytes per slot ----------------

    /// <summary>Bits 12:0 SIZE occupy lane 0 whole and the low five bits of lane 1.</summary>
    private const byte TsdLane1SizeAndOwnMask = 0x3F; // SIZE bits 12:8 (bits 0-4) + OWN (bit 5)
    private const byte TsdOwnByteBit = 0x20; // bit 13 overall = bit 5 of lane 1
    private const byte TsdTokByteBit = 0x80; // bit 15 overall = bit 7 of lane 1, hardware-only
    private const byte TsdLane2ErTxThMask = 0x0F; // bits 19:16, driver-writable early-tx threshold
    private static readonly byte[] TsdLaneWritableMask = [0xFF, TsdLane1SizeAndOwnMask, TsdLane2ErTxThMask, 0x00];

    // --- IMR/ISR, write-1-to-clear on ISR -----------------------------------
    // D3: the coordinator's decision on bit order, matching Linux 8139too
    // and QEMU's rtl8139.c rather than the researcher's first (swapped) draft.

    private const ushort IsrReceiveOk = 0x0001;
    private const ushort IsrReceiveError = 0x0002;
    private const ushort IsrTransmitOk = 0x0004;
    private const ushort IsrTransmitError = 0x0008;
    private const ushort IsrReceiveBufferOverflow = 0x0010; // RXOVW
    private const ushort IsrPacketUnderrunOrLinkChange = 0x0020;
    private const ushort IsrRxFifoOverflow = 0x0040;
    private const ushort IsrLengthChange = 0x2000;
    private const ushort IsrTimeOut = 0x4000;
    private const ushort IsrSystemError = 0x8000;
    private const ushort IsrDefinedBits = IsrReceiveOk | IsrReceiveError | IsrTransmitOk | IsrTransmitError |
        IsrReceiveBufferOverflow | IsrPacketUnderrunOrLinkChange | IsrRxFifoOverflow |
        IsrLengthChange | IsrTimeOut | IsrSystemError;

    // --- RCR (receive configuration register), offset 44h -------------------

    private const uint RcrAcceptAllPackets = 0x0001; // AAP: promiscuous
    private const uint RcrAcceptPhysicalMatch = 0x0002; // APM: unicast to our own address
    private const uint RcrAcceptMulticast = 0x0004; // AM
    private const uint RcrAcceptBroadcast = 0x0008; // AB
    private const uint RcrWrap = 0x0080; // bit 7: let a straddling frame spill past the ring instead of splitting
    private const int RcrRingLengthShift = 11;
    private const uint RcrRingLengthMask = 0x3u << RcrRingLengthShift; // bits 12:11

    private static readonly int[] RingLengthBytes = [8 * 1024, 16 * 1024, 32 * 1024, 64 * 1024];

    /// <summary>The extra scratch space past the nominal ring end a WRAP=1 frame may spill into.</summary>
    private const int WrapPaddingBytes = 1500;

    // --- receive frame header ------------------------------------------------

    private const int ReceiveHeaderLength = 4; // 16-bit status, 16-bit length
    private const int ReceiveReadPointerOffset = 16; // CAPR's own "pointer minus 16" driver convention
    private const int ReceiveFrameCrcLength = 4; // the header's length field counts the wire CRC our backend already stripped
    private const ushort ReceiveStatusOk = 0x0001; // this model never delivers a frame it judges to be in error
    private const int EthernetMinimumFrameLength = 14;
    private const int EthernetMaximumFrameLength = 1514;

    // --- 9346CR (93C46 EEPROM control), offset 50h --------------------------

    private const byte EepromModeMask = 0xC0; // bits 7:6
    private const byte EepromModeConfigWriteEnable = 0xC0; // "11": unlocks CONFIG0/1/3/4
    private const byte EepromModeAutoLoad = 0x40; // "01": synchronously reloads IDR0-5
    private const byte EepromWritableMask = 0xCE; // mode bits + EECS/EESK/EEDI; EEDO (bit 0) is chip-driven and stays 0

    // --- BMCR/BMSR (MII-style PHY registers), offsets 62h/64h ----------------

    private const ushort BmcrFullDuplex = 0x0100; // bit 8
    private const ushort BmcrRestartAutoNegotiation = 0x0200; // bit 9, self-clearing
    private const ushort BmcrAutoNegotiationEnable = 0x1000; // bit 12
    private const ushort BmcrSpeed100 = 0x2000; // bit 13
    private const ushort BmcrReset = 0x8000; // bit 15, self-clearing
    private const ushort BmcrResetValue = BmcrSpeed100 | BmcrAutoNegotiationEnable | BmcrFullDuplex;
    private const ushort BmcrWritableMask = BmcrFullDuplex | BmcrRestartAutoNegotiation |
        BmcrAutoNegotiationEnable | BmcrSpeed100 | BmcrReset;

    private const ushort BmsrExtendedCapability = 0x0001; // bit 0
    private const ushort BmsrLinkStatus = 0x0004; // bit 2: this model's wire is always up
    private const ushort BmsrAutoNegotiationAbility = 0x0008; // bit 3
    private const ushort BmsrAutoNegotiationComplete = 0x0020; // bit 5: always complete, nothing to negotiate against
    private const ushort Bmsr10BaseTHalfDuplex = 0x0800; // bit 11, PHY capability
    private const ushort Bmsr10BaseTFullDuplex = 0x1000; // bit 12
    private const ushort Bmsr100BaseTxHalfDuplex = 0x2000; // bit 13
    private const ushort Bmsr100BaseTxFullDuplex = 0x4000; // bit 14
    private const ushort BmsrFixedValue = BmsrExtendedCapability | BmsrLinkStatus | BmsrAutoNegotiationAbility |
        BmsrAutoNegotiationComplete | Bmsr10BaseTHalfDuplex | Bmsr10BaseTFullDuplex |
        Bmsr100BaseTxHalfDuplex | Bmsr100BaseTxFullDuplex;

    private const ushort AnarSelector8023 = 0x0001; // bits 4:0 = 00001, IEEE 802.3
    private const ushort Anar10BaseTHalfDuplex = 0x0020;
    private const ushort Anar10BaseTFullDuplex = 0x0040;
    private const ushort Anar100BaseTxHalfDuplex = 0x0080;
    private const ushort Anar100BaseTxFullDuplex = 0x0100;
    private const ushort AnarResetValue = AnarSelector8023 | Anar10BaseTHalfDuplex | Anar10BaseTFullDuplex |
        Anar100BaseTxHalfDuplex | Anar100BaseTxFullDuplex;
    private const ushort AnlparAcknowledge = 0x4000; // bit 14: pretend the (fictional) link partner already acknowledged
    private const ushort AnlparResetValue = AnarResetValue | AnlparAcknowledge;
    private const ushort AnerLinkPartnerNwayCapable = 0x0001;

    /// <summary>A locally administered, synthetic address: no real vendor OUI, loaded into IDR0-5 as though from the 93C46 at every reset.</summary>
    private static readonly byte[] DefaultMacAddress = [0x02, 0x00, 0x81, 0x39, 0x00, 0x00];

    private readonly PciIoBarWindow _ioWindow;
    private readonly PciMemoryBarWindow _memoryWindow;
    private readonly IDmaMemory _dma;
    private readonly Action<bool> _interruptSink;
    private readonly byte[] _syntheticMac = new byte[6];
    private readonly byte[] _idr = new byte[IdrLength];
    private readonly byte[] _mar = new byte[MarLength];
    private readonly byte[] _tsd = new byte[TransmitSlotCount * 4];
    private readonly byte[] _tsad = new byte[TransmitSlotCount * 4];
    private readonly byte[] _rbStart = new byte[4];
    private readonly byte[] _capr = new byte[2];
    private readonly byte[] _imr = new byte[2];
    private readonly byte[] _isr = new byte[2];
    private readonly byte[] _tcr = new byte[4];
    private readonly byte[] _rcr = new byte[4];
    private readonly byte[] _bmcr = new byte[2];
    private readonly byte[] _anar = new byte[2];
    private readonly byte[] _anlpar = new byte[2];
    private readonly byte[] _aner = new byte[2];

    /// <summary>Every offset this model does not give a defined meaning: plain storage, read back as written, exactly like an empty scratch register on the real chip.</summary>
    private readonly byte[] _scratch = new byte[RegisterFileLength];

    private byte _commandTeRe;
    private byte _eepromCommand;
    private byte _config0;
    private byte _config1;
    private byte _config3;
    private byte _config4;

    /// <summary>The chip's own receive write cursor, in ring-relative bytes -- never offset by 16, unlike <see cref="_capr"/>.</summary>
    private int _rxWritePointer;

    public PciFunction Function { get; }

    /// <summary>The wire this card is plugged into, swappable while the machine runs.</summary>
    public INetworkBackend Backend { get; set; }

    public ReadOnlySpan<byte> MacAddress => _syntheticMac;

    public long FramesSent { get; private set; }
    public long FramesReceived { get; private set; }
    public long FramesDropped { get; private set; }

    /// <summary>
    /// Builds one RTL8139 PCI function and attaches both its I/O and memory
    /// BARs. <paramref name="pci"/> must already carry bus-master DMA memory
    /// and interrupt routing -- exactly what a real <see cref="Machine"/>'s
    /// chipset sets up before any expansion card is built.
    /// </summary>
    public Rtl8139(
        PciBus pci,
        IoBus io,
        PhysicalMemoryBus memory,
        INetworkBackend backend,
        ushort functionAddress = 0x0028,
        ushort ioAddress = 0x0300,
        ReadOnlySpan<byte> macAddress = default)
    {
        PciInterruptRouter router = pci.InterruptRouter
            ?? throw new InvalidOperationException("RTL8139 requires PCI interrupt routing.");
        if (PciInterruptRouter.Link(functionAddress, 1) < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(functionAddress));
        }

        _dma = pci.DmaMemory
            ?? throw new InvalidOperationException("RTL8139 requires the chipset's bus-master DMA memory.");
        Backend = backend;

        if (macAddress.Length == _syntheticMac.Length)
        {
            macAddress.CopyTo(_syntheticMac);
        }
        else
        {
            DefaultMacAddress.CopyTo(_syntheticMac, 0);
        }

        Function = new PciFunction(functionAddress, VendorId, DeviceId, 0, commandMask: (ushort)CommandWritableMask);
        Function.Define(8, (ClassCode << 8) | Revision, 4); // overrides the base constructor's zeroed revision byte
        Function.Define(0x0C, 0, 1, 0xFF); // cache line size
        Function.Define(0x0D, 0, 1, 0xFF); // latency timer: a bus master benefits from one, nothing here schedules around it
        Function.Define(0x10, (ioAddress & IoBarAddressMask) | 1u, 4, IoBarAddressMask);
        Function.DefineBar(1, MemoryWindowLength, io: false);
        Function.Define(0x3C, 0, 1, 0x0F); // Interrupt Line bookkeeping only
        Function.Define(0x3D, 1, 1); // INTA wired to the motherboard router
        pci.Add(Function);

        _ioWindow = new PciIoBarWindow(Function, 0, (int)IoWindowLength, io, this);
        _memoryWindow = new PciMemoryBarWindow(Function, 1, MemoryWindowLength, memory, this);

        _interruptSink = asserted => router.SetInterrupt(functionAddress, 1, asserted);
        Reset();
    }

    /// <summary>Returns the chip to its power-on state: TE/RE off, the ring empty, the MAC reloaded from the synthetic EEPROM.</summary>
    public void Reset()
    {
        _syntheticMac.CopyTo(_idr, 0);
        Array.Clear(_mar);
        for (int slot = 0; slot < TransmitSlotCount; slot++)
        {
            // OWN = 1: every slot starts idle, available for the driver to fill.
            _tsd[slot * 4 + 1] = TsdOwnByteBit;
            _tsd[slot * 4] = 0;
            _tsd[slot * 4 + 2] = 0;
            _tsd[slot * 4 + 3] = 0;
        }

        Array.Clear(_tsad);
        Array.Clear(_rbStart);
        _commandTeRe = 0;
        _capr[0] = 0xF0;
        _capr[1] = 0xFF; // FFF0h: the driver's own "pointer minus 16" convention, empty at offset zero
        _rxWritePointer = 0;
        Array.Clear(_imr);
        Array.Clear(_isr);
        Array.Clear(_tcr);
        Array.Clear(_rcr);
        _eepromCommand = 0;
        _config0 = 0;
        _config1 = 0;
        _config3 = 0;
        _config4 = 0;
        WriteUInt16(_bmcr, 0, BmcrResetValue);
        WriteUInt16(_anar, 0, AnarResetValue);
        WriteUInt16(_anlpar, 0, AnlparResetValue);
        WriteUInt16(_aner, 0, AnerLinkPartnerNwayCapable);
        Array.Clear(_scratch);
        UpdateInterruptLine();
    }

    /// <summary>Drains every frame the backend is holding, ring space and RE permitting.</summary>
    public void Poll()
    {
        // Bus mastering off means there is nowhere in guest RAM this chip is
        // allowed to write to; frames are left queued in the backend rather
        // than delivered and lost, exactly as RE off would leave them.
        if ((_commandTeRe & CrReceiveEnable) == 0 || !Function.BusMasterEnabled)
        {
            return;
        }

        Span<byte> buffer = stackalloc byte[1600];
        while (Backend.TryReceive(buffer, out int length))
        {
            if (length is < EthernetMinimumFrameLength or > EthernetMaximumFrameLength)
            {
                continue;
            }

            DeliverFrame(buffer[..length]);
        }
    }

    // --- IIoDevice / IIoDwordDevice: the I/O BAR -----------------------------

    public byte ReadByte(ushort port)
    {
        int offset = IoOffset(port);
        return offset < 0 ? (byte)0xFF : ReadRegisterByte(offset);
    }

    public void WriteByte(ushort port, byte value)
    {
        int offset = IoOffset(port);
        if (offset >= 0)
        {
            WriteRegisterByte(offset, value);
        }
    }

    public uint ReadDword(ushort port)
    {
        int offset = IoOffset(port);
        return offset < 0 ? uint.MaxValue : ReadRegisters(offset, 4);
    }

    public void WriteDword(ushort port, uint value)
    {
        int offset = IoOffset(port);
        if (offset >= 0)
        {
            WriteRegisters(offset, 4, value);
        }
    }

    // --- IMemoryDevice: the memory BAR, aliasing the same registers ---------

    public byte ReadByte(uint offset)
    {
        return offset < RegisterFileLength ? ReadRegisterByte((int)offset) : (byte)0xFF;
    }

    public void WriteByte(uint offset, byte value)
    {
        if (offset < RegisterFileLength)
        {
            WriteRegisterByte((int)offset, value);
        }
    }

    /// <summary>The port relative to the current BAR0 base, or -1 while I/O decode is off or out of range.</summary>
    private int IoOffset(ushort port)
    {
        if (_ioWindow.MappedBase is not { } mapped)
        {
            return -1;
        }

        int offset = port - mapped;
        return offset is >= 0 and < RegisterFileLength ? offset : -1;
    }

    private uint ReadRegisters(int offset, int width)
    {
        uint value = 0;
        for (int i = 0; i < width; i++)
        {
            int at = offset + i;
            byte b = (uint)at < RegisterFileLength ? ReadRegisterByte(at) : (byte)0xFF;
            value |= (uint)b << (8 * i);
        }

        return value;
    }

    private void WriteRegisters(int offset, int width, uint value)
    {
        for (int i = 0; i < width; i++)
        {
            int at = offset + i;
            if ((uint)at < RegisterFileLength)
            {
                WriteRegisterByte(at, (byte)(value >> (8 * i)));
            }
        }
    }

    // --- per-byte register dispatch ------------------------------------------

    private byte ReadRegisterByte(int offset)
    {
        if (offset is >= Idr0 and < Idr0 + IdrLength)
        {
            return _idr[offset - Idr0];
        }

        if (offset is >= Mar0 and < Mar0 + MarLength)
        {
            return _mar[offset - Mar0];
        }

        if (offset is >= Tsd0 and < Tsd0 + TransmitSlotCount * 4)
        {
            return _tsd[offset - Tsd0];
        }

        if (offset is >= Tsad0 and < Tsad0 + TransmitSlotCount * 4)
        {
            return _tsad[offset - Tsad0];
        }

        if (offset is >= RbStartOffset and < RbStartOffset + 4)
        {
            return _rbStart[offset - RbStartOffset];
        }

        if (offset == CommandRegisterOffset)
        {
            return ReadCommandRegister();
        }

        if (offset is >= CaprOffset and < CaprOffset + 2)
        {
            return _capr[offset - CaprOffset];
        }

        if (offset is >= CbrOffset and < CbrOffset + 2)
        {
            return ReadCbrByte(offset - CbrOffset);
        }

        if (offset is >= ImrOffset and < ImrOffset + 2)
        {
            return _imr[offset - ImrOffset];
        }

        if (offset is >= IsrOffset and < IsrOffset + 2)
        {
            return _isr[offset - IsrOffset];
        }

        if (offset is >= TcrOffset and < TcrOffset + 4)
        {
            return _tcr[offset - TcrOffset];
        }

        if (offset is >= RcrOffset and < RcrOffset + 4)
        {
            return _rcr[offset - RcrOffset];
        }

        if (offset == EepromCommandOffset)
        {
            return _eepromCommand;
        }

        if (offset == Config0Offset)
        {
            return _config0;
        }

        if (offset == Config1Offset)
        {
            return _config1;
        }

        if (offset == MediaStatusOffset)
        {
            return 0; // LinkB (bit 2) and Speed10 (bit 3) both zero: link up, 100 Mb, no pause flags
        }

        if (offset == Config3Offset)
        {
            return _config3;
        }

        if (offset == Config4Offset)
        {
            return _config4;
        }

        if (offset is >= BmcrOffset and < BmcrOffset + 2)
        {
            return _bmcr[offset - BmcrOffset];
        }

        if (offset is >= BmsrOffset and < BmsrOffset + 2)
        {
            return (byte)(BmsrFixedValue >> (8 * (offset - BmsrOffset)));
        }

        if (offset is >= AnarOffset and < AnarOffset + 2)
        {
            return _anar[offset - AnarOffset];
        }

        if (offset is >= AnlparOffset and < AnlparOffset + 2)
        {
            return _anlpar[offset - AnlparOffset];
        }

        if (offset is >= AnerOffset and < AnerOffset + 2)
        {
            return _aner[offset - AnerOffset];
        }

        return _scratch[offset];
    }

    private void WriteRegisterByte(int offset, byte value)
    {
        if (offset is >= Idr0 and < Idr0 + IdrLength)
        {
            return; // read-only: loaded from the synthetic EEPROM at reset
        }

        if (offset is >= Mar0 and < Mar0 + MarLength)
        {
            _mar[offset - Mar0] = value;
            return;
        }

        if (offset is >= Tsd0 and < Tsd0 + TransmitSlotCount * 4)
        {
            WriteTsdByte(offset - Tsd0, value);
            return;
        }

        if (offset is >= Tsad0 and < Tsad0 + TransmitSlotCount * 4)
        {
            _tsad[offset - Tsad0] = value;
            return;
        }

        if (offset is >= RbStartOffset and < RbStartOffset + 4)
        {
            _rbStart[offset - RbStartOffset] = value;
            return;
        }

        if (offset == CommandRegisterOffset)
        {
            WriteCommandRegister(value);
            return;
        }

        if (offset is >= CaprOffset and < CaprOffset + 2)
        {
            _capr[offset - CaprOffset] = value;
            return;
        }

        if (offset is >= CbrOffset and < CbrOffset + 2)
        {
            return; // read-only
        }

        if (offset is >= ImrOffset and < ImrOffset + 2)
        {
            WriteImrByte(offset - ImrOffset, value);
            return;
        }

        if (offset is >= IsrOffset and < IsrOffset + 2)
        {
            WriteIsrByte(offset - IsrOffset, value);
            return;
        }

        if (offset is >= TcrOffset and < TcrOffset + 4)
        {
            _tcr[offset - TcrOffset] = value;
            return;
        }

        if (offset is >= RcrOffset and < RcrOffset + 4)
        {
            _rcr[offset - RcrOffset] = value;
            return;
        }

        if (offset == EepromCommandOffset)
        {
            WriteEepromCommand(value);
            return;
        }

        if (offset == Config0Offset)
        {
            if (ConfigWriteEnabled)
            {
                _config0 = value;
            }

            return;
        }

        if (offset == Config1Offset)
        {
            if (ConfigWriteEnabled)
            {
                _config1 = value;
            }

            return;
        }

        if (offset == MediaStatusOffset)
        {
            return; // read-only
        }

        if (offset == Config3Offset)
        {
            if (ConfigWriteEnabled)
            {
                _config3 = value;
            }

            return;
        }

        if (offset == Config4Offset)
        {
            if (ConfigWriteEnabled)
            {
                _config4 = value;
            }

            return;
        }

        if (offset is >= BmcrOffset and < BmcrOffset + 2)
        {
            WriteBmcrByte(offset - BmcrOffset, value);
            return;
        }

        if (offset is >= BmsrOffset and < BmsrOffset + 2)
        {
            return; // read-only
        }

        if (offset is >= AnarOffset and < AnarOffset + 2)
        {
            _anar[offset - AnarOffset] = value;
            return;
        }

        if (offset is >= AnlparOffset and < AnlparOffset + 2)
        {
            _anlpar[offset - AnlparOffset] = value;
            return;
        }

        if (offset is >= AnerOffset and < AnerOffset + 2)
        {
            _aner[offset - AnerOffset] = value;
            return;
        }

        _scratch[offset] = value;
    }

    // --- CR --------------------------------------------------------------

    private byte ReadCommandRegister()
    {
        byte value = _commandTeRe;
        if (IsReceiveBufferEmpty())
        {
            value |= CrBufferEmpty;
        }

        return value;
    }

    private void WriteCommandRegister(byte value)
    {
        if ((value & CrReset) != 0)
        {
            // A real reset discards whatever else this same write carried;
            // TE and RE come back off exactly as Reset() leaves them.
            Reset();
            return;
        }

        _commandTeRe = (byte)(value & CrDriverWritableMask);
    }

    private bool IsReceiveBufferEmpty()
    {
        int readPointer = (ToUInt16(_capr, 0) + ReceiveReadPointerOffset) & 0xFFFF;
        return readPointer == _rxWritePointer;
    }

    private byte ReadCbrByte(int lane)
    {
        return (byte)(_rxWritePointer >> (8 * lane));
    }

    // --- TSD/transmit ------------------------------------------------------

    private void WriteTsdByte(int localOffset, byte value)
    {
        int slot = localOffset / 4;
        int lane = localOffset % 4;
        int slotBase = slot * 4;
        byte writable = TsdLaneWritableMask[lane];
        bool ownWasSet = (_tsd[slotBase + 1] & TsdOwnByteBit) != 0;

        _tsd[slotBase + lane] = (byte)((_tsd[slotBase + lane] & ~writable) | (value & writable));

        bool ownNowClear = (_tsd[slotBase + 1] & TsdOwnByteBit) == 0;
        if (ownWasSet && ownNowClear)
        {
            TryTransmit(slot);
        }
    }

    /// <summary>
    /// Fetches the frame from host RAM and hands it to the backend, then sets
    /// OWN and TOK -- fetch-then-send, with no modelled underrun and no
    /// enforced descriptor order across the four slots: this machine has no
    /// PCI arbitration delay for a second slot's write to wait out.
    /// </summary>
    private void TryTransmit(int slot)
    {
        if ((_commandTeRe & CrTransmitEnable) == 0)
        {
            return; // the driver has not enabled the transmitter yet
        }

        if (!Function.BusMasterEnabled)
        {
            return; // no bus mastering, no DMA fetch: the descriptor is left exactly as the driver wrote it
        }

        int slotBase = slot * 4;
        uint size = (uint)(_tsd[slotBase] | ((_tsd[slotBase + 1] & 0x1F) << 8));
        if (size == 0)
        {
            return;
        }

        uint address = ToUInt32(_tsad, slotBase);
        var frame = new byte[size];
        for (uint i = 0; i < size; i++)
        {
            frame[i] = _dma.ReadByte(address + i);
        }

        Backend.Send(frame);
        FramesSent++;

        _tsd[slotBase + 1] |= (byte)(TsdOwnByteBit | TsdTokByteBit);
        RaiseInterrupt(IsrTransmitOk);
    }

    // --- receive ring --------------------------------------------------------

    /// <summary>
    /// Writes one accepted frame into the host-RAM ring at
    /// <see cref="_rbStart"/>+<see cref="_rxWritePointer"/>, or drops it with
    /// <see cref="IsrReceiveBufferOverflow"/> if it would not fit -- never
    /// writing a partial frame past the space the driver has freed.
    /// </summary>
    private void DeliverFrame(ReadOnlySpan<byte> frame)
    {
        if (!ShouldAccept(frame))
        {
            return;
        }

        int ringLength = RingLengthBytes[(int)((ToUInt32(_rcr, 0) & RcrRingLengthMask) >> RcrRingLengthShift)];
        int pad = (ToUInt32(_rcr, 0) & RcrWrap) != 0 ? WrapPaddingBytes : 0;

        int total = AlignToDword(frame.Length + ReceiveHeaderLength);
        int readPointer = (ToUInt16(_capr, 0) + ReceiveReadPointerOffset) & 0xFFFF;

        // One byte of the ring is always kept unusable, exactly as the
        // DP8390 ring in Ne2000 reserves one page: without it, "completely
        // full" and "completely empty" would both read back as the cursors
        // coinciding, and BUFE could never tell them apart.
        int free = _rxWritePointer >= readPointer
            ? (ringLength - _rxWritePointer) + readPointer
            : readPointer - _rxWritePointer;

        if (total >= free)
        {
            FramesDropped++;
            RaiseInterrupt(IsrReceiveBufferOverflow);
            return;
        }

        uint rbStart = ToUInt32(_rbStart, 0);
        int cursor = _rxWritePointer;
        ushort length = (ushort)(frame.Length + ReceiveFrameCrcLength);

        WriteRingByte(rbStart, ringLength, pad, ref cursor, (byte)ReceiveStatusOk);
        WriteRingByte(rbStart, ringLength, pad, ref cursor, (byte)(ReceiveStatusOk >> 8));
        WriteRingByte(rbStart, ringLength, pad, ref cursor, (byte)length);
        WriteRingByte(rbStart, ringLength, pad, ref cursor, (byte)(length >> 8));
        foreach (byte value in frame)
        {
            WriteRingByte(rbStart, ringLength, pad, ref cursor, value);
        }

        while (cursor - _rxWritePointer < total)
        {
            WriteRingByte(rbStart, ringLength, pad, ref cursor, 0); // dword padding, contents unread by any driver
        }

        _rxWritePointer = (_rxWritePointer + total) % ringLength;
        FramesReceived++;
        RaiseInterrupt(IsrReceiveOk);
    }

    /// <summary>
    /// Writes one byte at the ring-relative <paramref name="cursor"/>, which
    /// may run past <paramref name="ringLength"/> into the WRAP scratch pad
    /// (contiguous physical bytes, no split) or wrap back to ring offset zero
    /// once past both -- see the class summary for why a single frame never
    /// needs more than this one crossing.
    /// </summary>
    private void WriteRingByte(uint rbStart, int ringLength, int pad, ref int cursor, byte value)
    {
        int physical = cursor < ringLength + pad ? cursor : cursor - ringLength;
        _dma.WriteByte(rbStart + (uint)physical, value);
        cursor++;
    }

    private bool ShouldAccept(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 6)
        {
            return false;
        }

        uint rcr = ToUInt32(_rcr, 0);
        if ((rcr & RcrAcceptAllPackets) != 0)
        {
            return true;
        }

        bool broadcast = true;
        for (int i = 0; i < 6; i++)
        {
            if (frame[i] != 0xFF)
            {
                broadcast = false;
                break;
            }
        }

        if (broadcast)
        {
            return (rcr & RcrAcceptBroadcast) != 0;
        }

        if ((frame[0] & 0x01) != 0)
        {
            return (rcr & RcrAcceptMulticast) != 0;
        }

        for (int i = 0; i < 6; i++)
        {
            if (frame[i] != _idr[i])
            {
                return false;
            }
        }

        return (rcr & RcrAcceptPhysicalMatch) != 0;
    }

    // --- IMR/ISR -------------------------------------------------------------

    private void RaiseInterrupt(ushort bit)
    {
        ushort isr = ToUInt16(_isr, 0);
        WriteUInt16(_isr, 0, (ushort)(isr | bit));
        UpdateInterruptLine();
    }

    private void WriteImrByte(int lane, byte value)
    {
        byte mask = (byte)(IsrDefinedBits >> (8 * lane));
        _imr[lane] = (byte)(value & mask);
        UpdateInterruptLine();
    }

    private void WriteIsrByte(int lane, byte value)
    {
        byte mask = (byte)(IsrDefinedBits >> (8 * lane));
        _isr[lane] = (byte)(_isr[lane] & ~(value & mask)); // write 1 to clear
        UpdateInterruptLine();
    }

    private void UpdateInterruptLine()
    {
        bool asserted = (ToUInt16(_isr, 0) & ToUInt16(_imr, 0)) != 0;
        _interruptSink(asserted);
    }

    // --- 9346CR / CONFIGn ----------------------------------------------------

    private bool ConfigWriteEnabled => (_eepromCommand & EepromModeMask) == EepromModeConfigWriteEnable;

    private void WriteEepromCommand(byte value)
    {
        _eepromCommand = (byte)(value & EepromWritableMask);
        if ((_eepromCommand & EepromModeMask) == EepromModeAutoLoad)
        {
            _syntheticMac.CopyTo(_idr, 0); // the only part of the 93C46 image this model actually stores
        }
    }

    // --- BMCR ------------------------------------------------------------

    private void WriteBmcrByte(int lane, byte value)
    {
        byte mask = (byte)(BmcrWritableMask >> (8 * lane));
        _bmcr[lane] = (byte)((_bmcr[lane] & ~mask) | (value & mask));

        ushort combined = ToUInt16(_bmcr, 0);
        if ((combined & BmcrReset) != 0)
        {
            // The PHY has no negotiation delay to model: it comes back at
            // its fixed capability the instant the reset bit is written.
            WriteUInt16(_bmcr, 0, BmcrResetValue);
            return;
        }

        if ((combined & BmcrRestartAutoNegotiation) != 0)
        {
            // Nothing here negotiates against a real link partner, so
            // "restarting" completes on the same write that asked for it.
            WriteUInt16(_bmcr, 0, (ushort)(combined & ~BmcrRestartAutoNegotiation));
        }
    }

    // --- little-endian helpers ------------------------------------------------

    private static int AlignToDword(int length) => (length + 3) & ~3;

    private static ushort ToUInt16(byte[] bytes, int offset) => (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

    private static uint ToUInt32(byte[] bytes, int offset) =>
        (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));

    private static void WriteUInt16(byte[] bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
    }

    // --- snapshot --------------------------------------------------------

    /// <summary>The version of the payload <see cref="Capture"/> produces today.</summary>
    public const int SnapshotVersion = 1;

    /// <summary>
    /// The fixed size of a version-1 payload: every register field named
    /// above, the receive write cursor, and the scratch register bank --
    /// everything a driver could read back and notice was wrong. PCI
    /// configuration space (including both BARs) is captured separately by
    /// <see cref="Pci.PciBus"/>; the backend is host state with no guest
    /// counterpart, exactly as <see cref="Ne2000"/>'s own summary explains.
    /// </summary>
    public const int SnapshotLength =
        IdrLength + MarLength
        + TransmitSlotCount * 4 * 2 // TSD + TSAD
        + 4 // RBSTART
        + sizeof(byte) // CR (TE/RE only; BUFE and RST are computed)
        + 2 // CAPR
        + sizeof(int) // the receive write cursor
        + 2 + 2 // IMR, ISR
        + 4 + 4 // TCR, RCR
        + sizeof(byte) * 5 // 9346CR, CONFIG0, CONFIG1, CONFIG3, CONFIG4
        + 2 * 4 // BMCR, ANAR, ANLPAR, ANER
        + RegisterFileLength; // scratch

    public byte[] Capture()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(_idr);
        writer.Write(_mar);
        writer.Write(_tsd);
        writer.Write(_tsad);
        writer.Write(_rbStart);
        writer.Write(_commandTeRe);
        writer.Write(_capr);
        writer.Write(_rxWritePointer);
        writer.Write(_imr);
        writer.Write(_isr);
        writer.Write(_tcr);
        writer.Write(_rcr);
        writer.Write(_eepromCommand);
        writer.Write(_config0);
        writer.Write(_config1);
        writer.Write(_config3);
        writer.Write(_config4);
        writer.Write(_bmcr);
        writer.Write(_anar);
        writer.Write(_anlpar);
        writer.Write(_aner);
        writer.Write(_scratch);

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Restores what <see cref="Capture"/> wrote. <see
    /// cref="CardCatalog.NewestSnapshotVersions"/> and <see
    /// cref="CardCatalog.FixedSnapshotLengths"/> already reject a wrong
    /// version or a malformed length before this runs; the check here is
    /// only for a caller that reaches this method directly, bypassing the
    /// container.
    /// </summary>
    public void Restore(byte[] payload, int version)
    {
        if (version != SnapshotVersion || payload.Length != SnapshotLength)
        {
            throw new InvalidDataException("Invalid RTL8139 snapshot envelope.");
        }

        using var reader = new BinaryReader(new MemoryStream(payload));

        reader.ReadBytes(_idr.Length).CopyTo(_idr, 0);
        reader.ReadBytes(_mar.Length).CopyTo(_mar, 0);
        reader.ReadBytes(_tsd.Length).CopyTo(_tsd, 0);
        reader.ReadBytes(_tsad.Length).CopyTo(_tsad, 0);
        reader.ReadBytes(_rbStart.Length).CopyTo(_rbStart, 0);
        _commandTeRe = reader.ReadByte();
        reader.ReadBytes(_capr.Length).CopyTo(_capr, 0);
        _rxWritePointer = reader.ReadInt32();
        reader.ReadBytes(_imr.Length).CopyTo(_imr, 0);
        reader.ReadBytes(_isr.Length).CopyTo(_isr, 0);
        reader.ReadBytes(_tcr.Length).CopyTo(_tcr, 0);
        reader.ReadBytes(_rcr.Length).CopyTo(_rcr, 0);
        _eepromCommand = reader.ReadByte();
        _config0 = reader.ReadByte();
        _config1 = reader.ReadByte();
        _config3 = reader.ReadByte();
        _config4 = reader.ReadByte();
        reader.ReadBytes(_bmcr.Length).CopyTo(_bmcr, 0);
        reader.ReadBytes(_anar.Length).CopyTo(_anar, 0);
        reader.ReadBytes(_anlpar.Length).CopyTo(_anlpar, 0);
        reader.ReadBytes(_aner.Length).CopyTo(_aner, 0);
        reader.ReadBytes(_scratch.Length).CopyTo(_scratch, 0);

        UpdateInterruptLine();
    }
}
