using Aurora386.Core.IO;

using Aurora386.Core.Devices.Interrupts;
namespace Aurora386.Core.Devices.Network;

/// <summary>
/// An NE2000 Ethernet card, built around National Semiconductor's DP8390
/// network interface controller.
///
/// The NE2000 became the card every DOS program supported, for a reason worth
/// understanding: it was cheap, its register interface was published, and
/// clones were everywhere. Packet drivers, NDIS shims and mTCP all speak to it,
/// so emulating this one card covers almost all DOS networking.
///
/// Two ideas shape the whole design, and both come from 1988 constraints:
///
/// <b>The card has its own memory, and the CPU cannot see it.</b> There is no
/// window into it in the ISA memory space. Instead the driver programs a start
/// address and a byte count into registers and then reads or writes a single
/// I/O port repeatedly; the card walks its own memory in step. This is the
/// "remote DMA", and it exists because mapping 16 KB into the crowded region
/// below 1 MB would have been a configuration nightmare on a machine with no
/// plug-and-play.
///
/// <b>Received frames go into a ring, not a queue.</b> The receive area is
/// divided into 256-byte pages, and two registers chase each other around it:
/// CURR is where the card will write next, BNRY is how far the driver has read.
/// A ring never needs the card to shuffle data down after a read, which matters
/// when frames arrive faster than a 386 can drain them. When CURR catches up to
/// BNRY the ring is full and further frames are simply dropped -- the card has
/// nowhere to put them, and a dropped frame is what Ethernet expects anyway.
/// </summary>
public sealed class Ne2000 : IIoDevice
{
    // --- register offsets, page 0 -------------------------------------------

    private const int Command = 0x00;       // present in every page
    private const int PageStart = 0x01;     // first page of the receive ring
    private const int PageStop = 0x02;      // one past the last page
    private const int Boundary = 0x03;      // how far the driver has read
    private const int TransmitStatus = 0x04;
    private const int TransmitPageStart = 0x04;
    private const int TransmitByteCount0 = 0x05;
    private const int TransmitByteCount1 = 0x06;
    private const int InterruptStatus = 0x07;
    private const int RemoteStartAddress0 = 0x08;
    private const int RemoteStartAddress1 = 0x09;
    private const int RemoteByteCount0 = 0x0A;
    private const int RemoteByteCount1 = 0x0B;
    private const int ReceiveConfiguration = 0x0C;
    private const int TransmitConfiguration = 0x0D;
    private const int DataConfiguration = 0x0E;
    private const int InterruptMask = 0x0F;

    // --- register offsets, page 1 -------------------------------------------

    private const int PhysicalAddress0 = 0x01;   // through 0x06: our MAC
    private const int Current = 0x07;            // where the card writes next
    private const int MulticastAddress0 = 0x08;  // through 0x0F

    // --- ports outside the register file -------------------------------------

    /// <summary>The remote DMA window: every access moves one byte or word.</summary>
    private const int DataPort = 0x10;

    /// <summary>Reading this resets the card, whatever state it was in.</summary>
    private const int ResetPort = 0x1F;

    // --- interrupt status bits ----------------------------------------------

    private const byte IsrPacketReceived = 0x01;
    private const byte IsrPacketTransmitted = 0x02;
    private const byte IsrReceiveError = 0x04;
    private const byte IsrTransmitError = 0x08;
    private const byte IsrOverwriteWarning = 0x10;
    private const byte IsrRemoteDmaComplete = 0x40;
    private const byte IsrReset = 0x80;

    // --- command register bits ----------------------------------------------

    private const byte CmdStop = 0x01;
    private const byte CmdStart = 0x02;
    private const byte CmdTransmit = 0x04;

    /// <summary>
    /// The card's private memory. A real NE2000 has 16 KB fitted, which the
    /// documentation addresses from 0x4000 to 0x7FFF -- so page numbers run
    /// from 0x40 to 0x80 rather than from zero. Keeping that offset here means
    /// register values can be used as page numbers directly, exactly as the
    /// datasheet and every driver assume.
    /// </summary>
    private const int MemoryBase = 0x4000;
    private const int MemorySize = 16 * 1024;
    private const int PageSize = 256;

    /// <summary>
    /// Every received frame is preceded by four bytes the card writes itself:
    /// a copy of the receive status, the page where the next frame begins, and
    /// the frame length. The driver reads this header first to learn how much
    /// to fetch and where to look next.
    /// </summary>
    private const int FrameHeaderSize = 4;

    private readonly byte[] _memory = new byte[MemorySize];
    private readonly byte[] _registers = new byte[4 * 16];
    private readonly byte[] _macAddress = new byte[6];
    private readonly InterruptController _interrupts;
    private readonly ushort _basePort;
    private readonly int _irqLine;

    private byte _commandRegister = CmdStop;
    private int _remoteAddress;
    private int _remoteCount;

    /// <summary>
    /// The wire the card is plugged into. Swappable while running, because
    /// plugging a cable into a card is something one does to a machine that
    /// is on, and the driver cannot tell the difference.
    /// </summary>
    public INetworkBackend Backend { get; set; }

    /// <summary>Frames sent and received, for the user interface to show activity.</summary>
    public long FramesSent { get; private set; }
    public long FramesReceived { get; private set; }
    public long FramesDropped { get; private set; }

    public Ne2000(InterruptController interrupts, INetworkBackend backend,
                  ushort basePort = 0x300, int irqLine = 3,
                  ReadOnlySpan<byte> macAddress = default)
    {
        _interrupts = interrupts;
        Backend = backend;
        _basePort = basePort;
        _irqLine = irqLine;

        // A locally administered address: bit 1 of the first byte marks it as
        // assigned by the network's owner rather than by a manufacturer, which
        // is exactly what an emulated card is.
        if (macAddress.Length == 6) macAddress.CopyTo(_macAddress);
        else _macAddress = [0x02, 0x00, 0x41, 0x55, 0x52, 0x41];

        Reset();
    }

    public ReadOnlySpan<byte> MacAddress => _macAddress;

    public void Register(IoBus io) => io.Register(_basePort, 0x20, this);

    /// <summary>
    /// Returns the card to its power-on state.
    ///
    /// The ring is configured the way every NE2000 driver expects to find it:
    /// the first six pages hold the transmit buffer (a maximum-size Ethernet
    /// frame is 1514 bytes, which needs six 256-byte pages), and the rest is
    /// the receive ring.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_registers);
        _commandRegister = CmdStop;
        _remoteAddress = 0;
        _remoteCount = 0;

        SetRegister(0, PageStart, 0x46);
        SetRegister(0, PageStop, 0x80);
        SetRegister(0, Boundary, 0x46);
        SetRegister(1, Current, 0x47);
        SetRegister(0, TransmitPageStart, 0x40);

        // The reset bit tells a polling driver the card has finished coming up.
        SetRegister(0, InterruptStatus, IsrReset);

        for (int i = 0; i < 6; i++)
            SetRegister(1, PhysicalAddress0 + i, _macAddress[i]);
    }

    // --- register file access ------------------------------------------------

    /// <summary>
    /// The page currently selected by the command register's PS1:PS0 bits.
    ///
    /// Sixteen I/O ports were all the card could afford, so the same addresses
    /// mean different registers depending on which page is selected. Page 0 is
    /// the working set, page 1 holds the addresses, page 2 mirrors the
    /// configuration for diagnostics.
    /// </summary>
    private int CurrentPage => (_commandRegister >> 6) & 3;

    private byte GetRegister(int page, int offset) => _registers[page * 16 + offset];

    /// <summary>Bit 0 of the data configuration register: remote DMA moves words, not bytes.</summary>
    private bool WordMode => (GetRegister(0, DataConfiguration) & 0x01) != 0;

    private void SetRegister(int page, int offset, byte value)
        => _registers[page * 16 + offset] = value;

    public byte ReadByte(ushort port)
    {
        int offset = port - _basePort;

        if (offset == ResetPort)
        {
            // Reading the reset port is how a driver puts the card into a known
            // state before it trusts any other register.
            Reset();
            return 0;
        }

        if (offset is >= DataPort and < DataPort + 8)
        {
            // In word mode every DMA cycle moves a word, whatever the width
            // of the bus access: a byte read gets the low half and the high
            // half is gone. The Crynwr driver reads its station address
            // exactly this way -- word mode, byte reads -- and expects the
            // doubled PROM to come out as one copy.
            if (!WordMode) return ReadRemoteDma();
            byte low = ReadRemoteDma();
            ReadRemoteDma();
            return low;
        }
        if (offset == Command) return _commandRegister;
        if (offset is < 0 or > 0x0F) return 0xFF;

        // Page 2 is a read-only mirror of the configuration the driver wrote,
        // provided so it can verify the card accepted its settings.
        int page = CurrentPage == 2 ? 0 : CurrentPage;
        return GetRegister(page, offset);
    }

    /// <summary>
    /// The data port is the one genuinely 16-bit register on the card: an
    /// <c>in ax</c> there moves two bytes of the transfer, low then high.
    /// Served as two byte reads it would read the port and the one above
    /// it, which is not the second byte but a mirror of the same port, and
    /// the driver would see every other byte of its own station address.
    /// The ports from 0x10 to 0x17 all decode to the data port, as on the
    /// real card.
    /// </summary>
    public ushort ReadWord(ushort port)
    {
        int offset = port - _basePort;
        if (offset is >= DataPort and < DataPort + 8)
        {
            byte low = ReadRemoteDma();
            byte high = ReadRemoteDma();
            return (ushort)(low | (high << 8));
        }
        return (ushort)(ReadByte(port) | (ReadByte((ushort)(port + 1)) << 8));
    }

    public void WriteWord(ushort port, ushort value)
    {
        int offset = port - _basePort;
        if (offset is >= DataPort and < DataPort + 8)
        {
            WriteRemoteDma((byte)value);
            WriteRemoteDma((byte)(value >> 8));
            return;
        }
        WriteByte(port, (byte)value);
        WriteByte((ushort)(port + 1), (byte)(value >> 8));
    }

    public void WriteByte(ushort port, byte value)
    {
        int offset = port - _basePort;

        if (offset == ResetPort) { Reset(); return; }
        if (offset is >= DataPort and < DataPort + 8)
        {
            WriteRemoteDma(value);
            if (WordMode) WriteRemoteDma(0);
            return;
        }
        if (offset == Command) { WriteCommand(value); return; }
        if (offset is < 0 or > 0x0F) return;

        int page = CurrentPage;

        switch (page)
        {
            case 0:
                WritePage0(offset, value);
                return;

            case 1:
                SetRegister(1, offset, value);
                // Keep our copy of the MAC in step: the driver writes it here
                // during initialisation, usually copying it out of the card's
                // own serial EEPROM.
                if (offset is >= PhysicalAddress0 and < PhysicalAddress0 + 6)
                    _macAddress[offset - PhysicalAddress0] = value;
                return;

            default:
                SetRegister(page, offset, value);
                return;
        }
    }

    private void WritePage0(int offset, byte value)
    {
        switch (offset)
        {
            case InterruptStatus:
                // Writing a 1 *clears* that bit. Acknowledging interrupts by
                // writing back the bits you handled avoids the race where an
                // interrupt arrives between the read and the clear.
                SetRegister(0, InterruptStatus,
                            (byte)(GetRegister(0, InterruptStatus) & ~value));
                UpdateInterruptLine();
                return;

            case RemoteStartAddress0:
            case RemoteStartAddress1:
                SetRegister(0, offset, value);
                _remoteAddress = GetRegister(0, RemoteStartAddress0)
                               | (GetRegister(0, RemoteStartAddress1) << 8);
                return;

            case RemoteByteCount0:
            case RemoteByteCount1:
                SetRegister(0, offset, value);
                _remoteCount = GetRegister(0, RemoteByteCount0)
                             | (GetRegister(0, RemoteByteCount1) << 8);
                return;

            case InterruptMask:
                SetRegister(0, InterruptMask, value);
                UpdateInterruptLine();
                return;

            default:
                SetRegister(0, offset, value);
                return;
        }
    }

    private void WriteCommand(byte value)
    {
        _commandRegister = value;

        if ((value & CmdStop) != 0)
        {
            // Stopping the card is how a driver reconfigures it safely.
            return;
        }

        if ((value & CmdTransmit) != 0) Transmit();

        // Remote DMA direction lives in bits 3-5. Command 4 ("abort/complete")
        // ends a transfer early, and the card reports it like a finished one.
        int dmaCommand = (value >> 3) & 7;
        if (dmaCommand == 4)
        {
            _remoteCount = 0;
            RaiseInterrupt(IsrRemoteDmaComplete);
        }
    }

    // --- remote DMA ----------------------------------------------------------

    /// <summary>
    /// Hands the driver the next byte of the card's memory.
    ///
    /// The address advances by itself, which is the entire point: the driver
    /// does an <c>insb</c>/<c>rep insw</c> against a single port and the card
    /// walks its memory in lockstep. When the count reaches zero the card
    /// raises the DMA-complete interrupt so a driver can wait for it rather
    /// than counting bytes itself.
    /// </summary>
    private byte ReadRemoteDma()
    {
        if (_remoteCount <= 0) return 0;

        byte value = ReadCardMemory(_remoteAddress);
        _remoteAddress++;
        _remoteCount--;

        if (_remoteCount == 0) RaiseInterrupt(IsrRemoteDmaComplete);
        return value;
    }

    private void WriteRemoteDma(byte value)
    {
        if (_remoteCount <= 0) return;

        WriteCardMemory(_remoteAddress, value);
        _remoteAddress++;
        _remoteCount--;

        if (_remoteCount == 0) RaiseInterrupt(IsrRemoteDmaComplete);
    }

    /// <summary>
    /// Reads the card's private memory by its documented address, which starts
    /// at 0x4000 rather than zero. Addresses outside the fitted memory read as
    /// 0xFF, the value a floating bus settles at.
    /// </summary>
    public byte ReadCardMemory(int address)
    {
        // Below the RAM sits the station address PROM, which is how a driver
        // learns the MAC: a remote DMA read of the first 32 bytes. Each byte
        // appears twice because the PROM is 8 bits wide on a 16-bit bus, and
        // 0x57 0x57 at the end is the mark drivers look for to tell an
        // NE2000 from an NE1000 ("WW" against "BB").
        if ((uint)address < 32)
        {
            if (address < 12) return _macAddress[address >> 1];
            return address >= 28 ? (byte)0x57 : (byte)0;
        }

        int index = address - MemoryBase;
        return (uint)index < MemorySize ? _memory[index] : (byte)0xFF;
    }

    public void WriteCardMemory(int address, byte value)
    {
        int index = address - MemoryBase;
        if ((uint)index < MemorySize) _memory[index] = value;
    }

    // --- transmit ------------------------------------------------------------

    /// <summary>
    /// Sends the frame the driver has already copied into the card's transmit
    /// buffer through remote DMA. TPSR says which page it starts on and TBCR
    /// how many bytes it is.
    /// </summary>
    private void Transmit()
    {
        int start = GetRegister(0, TransmitPageStart) * PageSize;
        int length = GetRegister(0, TransmitByteCount0)
                   | (GetRegister(0, TransmitByteCount1) << 8);

        if (length <= 0 || length > 1514)
        {
            SetRegister(0, TransmitStatus, 0x00);
            RaiseInterrupt(IsrTransmitError);
            return;
        }

        var frame = new byte[length];
        for (int i = 0; i < length; i++)
            frame[i] = ReadCardMemory(start + i);

        Backend.Send(frame);
        FramesSent++;

        // Bit 0 of TSR is "transmitted without error", which is what a driver
        // checks before reusing the buffer.
        SetRegister(0, TransmitStatus, 0x01);

        // The transmit command bit clears itself once the frame is away.
        _commandRegister &= unchecked((byte)~CmdTransmit);

        RaiseInterrupt(IsrPacketTransmitted);
    }

    // --- receive -------------------------------------------------------------

    /// <summary>
    /// Collects any waiting frames from the backend and files them in the ring.
    /// The machine calls this periodically; a real card would act the moment a
    /// frame arrived on the wire.
    /// </summary>
    public void Poll()
    {
        // A stopped card is deaf: the driver stops it precisely so it can
        // reconfigure the ring without frames landing underneath it.
        if ((_commandRegister & CmdStop) != 0) return;

        Span<byte> buffer = stackalloc byte[1600];
        while (Backend.TryReceive(buffer, out int length))
        {
            if (length is < 14 or > 1514) continue;
            DeliverFrame(buffer[..length]);
        }
    }

    /// <summary>
    /// Writes one frame into the receive ring and tells the driver about it.
    ///
    /// The layout is the card's own: a four-byte header, then the frame,
    /// rounded up to a whole number of 256-byte pages. Rounding up wastes a
    /// little memory but means the "next frame" pointer is always a page
    /// number, which is all the header has room for.
    /// </summary>
    public void DeliverFrame(ReadOnlySpan<byte> frame)
    {
        if (!ShouldAccept(frame)) return;

        int start = GetRegister(0, PageStart);
        int stop = GetRegister(0, PageStop);
        int current = GetRegister(1, Current);
        int boundary = GetRegister(0, Boundary);

        int total = frame.Length + FrameHeaderSize;
        int pagesNeeded = (total + PageSize - 1) / PageSize;

        // How many pages are free between where we write and where the driver
        // has read. Leaving the ring completely full would make CURR == BNRY,
        // which is also what "completely empty" looks like -- so one page is
        // always kept spare to keep the two states distinguishable.
        int free = current >= boundary
            ? (stop - current) + (boundary - start)
            : boundary - current;

        if (pagesNeeded >= free)
        {
            // No room. A real card drops the frame and notes an overwrite
            // warning; Ethernet has always assumed frames can be lost.
            FramesDropped++;
            RaiseInterrupt(IsrOverwriteWarning);
            return;
        }

        int nextPage = current + pagesNeeded;
        if (nextPage >= stop) nextPage -= stop - start;

        int address = current * PageSize;

        // Header: status (bit 0 = received intact), the page the next frame
        // will start on, then the length including this header.
        WriteRingByte(ref address, start, stop, 0x01);
        WriteRingByte(ref address, start, stop, (byte)nextPage);
        WriteRingByte(ref address, start, stop, (byte)(total & 0xFF));
        WriteRingByte(ref address, start, stop, (byte)(total >> 8));

        foreach (byte value in frame)
            WriteRingByte(ref address, start, stop, value);

        SetRegister(1, Current, (byte)nextPage);
        FramesReceived++;

        RaiseInterrupt(IsrPacketReceived);
    }

    /// <summary>
    /// Writes one byte, wrapping back to the start of the ring at the top.
    /// This wrap is the whole reason a frame can be split across the end of
    /// the buffer without the card having to move anything.
    /// </summary>
    private void WriteRingByte(ref int address, int start, int stop, byte value)
    {
        if (address >= stop * PageSize) address = start * PageSize;
        WriteCardMemory(address, value);
        address++;
    }

    /// <summary>
    /// Decides whether a frame is ours.
    ///
    /// A real card compares the destination address in hardware, because
    /// waking the CPU for every frame on a busy segment would swamp it. The
    /// receive configuration register can relax this: promiscuous mode accepts
    /// everything, which is how a packet sniffer works.
    /// </summary>
    private bool ShouldAccept(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 6) return false;

        byte config = GetRegister(0, ReceiveConfiguration);

        // Bit 4: promiscuous -- accept every frame regardless of address.
        if ((config & 0x10) != 0) return true;

        // Broadcast: all six address bytes 0xFF. Accepted when bit 2 is set,
        // which every driver does because ARP depends on it.
        bool broadcast = true;
        for (int i = 0; i < 6; i++)
            if (frame[i] != 0xFF) { broadcast = false; break; }
        if (broadcast) return (config & 0x04) != 0;

        // Multicast frames have the low bit of the first byte set.
        if ((frame[0] & 0x01) != 0) return (config & 0x08) != 0;

        for (int i = 0; i < 6; i++)
            if (frame[i] != _macAddress[i]) return false;

        return true;
    }

    // --- interrupts ----------------------------------------------------------

    private void RaiseInterrupt(byte statusBit)
    {
        SetRegister(0, InterruptStatus,
                    (byte)(GetRegister(0, InterruptStatus) | statusBit));
        UpdateInterruptLine();
    }

    /// <summary>
    /// Drives the interrupt line from the status and mask registers together.
    ///
    /// The card asserts the line while any *unmasked* status bit is set, and
    /// holds it there until the driver acknowledges. That is level-triggered
    /// behaviour, and it is why acknowledging by writing back the handled bits
    /// matters: clear the wrong ones and the line stays high forever.
    /// </summary>
    private void UpdateInterruptLine()
    {
        byte status = GetRegister(0, InterruptStatus);
        byte mask = GetRegister(0, InterruptMask);

        if ((status & mask & 0x7F) != 0) _interrupts.RaiseIrq(_irqLine);
        else _interrupts.LowerIrq(_irqLine);
    }
}
