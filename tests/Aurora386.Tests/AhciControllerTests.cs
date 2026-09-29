using Aurora386.Core.Devices.Interrupts;
using Aurora386.Core.Devices.Pci;
using Aurora386.Core.Devices.Storage;
using Aurora386.Core.IO;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// Intel ICH9 AHCI (PCI 8086:2922): PCI identity and BAR5 sizing, the
/// generic host control block (CAP/GHC/IS/PI/VS), one port's SSTS/SIG device
/// detection and its CMD engine (FRE/FR, ST/CR, GHC.HR), the ATA command
/// path (IDENTIFY, READ/WRITE DMA EXT across a PRDT, an aborted unknown
/// command, an out-of-range LBA), interrupt gating and RW1C clearing, the
/// bus-master-disabled gate on Command Issue, and a snapshot round trip. See
/// <c>docs/AHCI.md</c> for the register map this drives directly through
/// guest RAM and the memory-mapped ABAR, exactly as a real AHCI driver would.
/// </summary>
public class AhciControllerTests
{
    private const ushort FunctionAddress = 0x0020; // device 4, function 0: PIRQ link B
    private const uint RamSize = 4 * 1024 * 1024;
    private const uint AddressableBytes = 8 * 1024 * 1024;
    private const uint AbarBase = 0x600000; // above RamSize, inside AddressableBytes

    // --- generic host control offsets, mirroring AhciController's own private constants ---

    private const uint RegCap = 0x00;
    private const uint RegGhc = 0x04;
    private const uint RegIs = 0x08;
    private const uint RegPi = 0x0C;
    private const uint RegVs = 0x10;

    private const uint GhcHrBit = 1u << 0;
    private const uint GhcIeBit = 1u << 1;
    private const uint GhcAeBit = 1u << 31;

    // --- port offsets, mirroring AhciPort's own private constants -------------

    private const uint PortRegisterBase = 0x100;
    private const uint PortRegisterStride = 0x80;

    private const uint RegClb = 0x00;
    private const uint RegFb = 0x08;
    private const uint RegPortIs = 0x10;
    private const uint RegPortIe = 0x14;
    private const uint RegCmd = 0x18;
    private const uint RegTfd = 0x20;
    private const uint RegSig = 0x24;
    private const uint RegSsts = 0x28;
    private const uint RegSctl = 0x2C;
    private const uint RegSerr = 0x30;
    private const uint RegSact = 0x34;
    private const uint RegCi = 0x38;

    private const uint CmdStBit = 1u << 0;
    private const uint CmdSudBit = 1u << 1;
    private const uint CmdPodBit = 1u << 2;
    private const uint CmdFreBit = 1u << 4;
    private const uint CmdCrBit = 1u << 14;
    private const uint CmdFrBit = 1u << 15;
    private const int CmdIccShift = 28;
    private const uint CmdIccMask = 0xFu << CmdIccShift;

    private const uint IsDhrsBit = 1u << 0;
    private const uint IsPssBit = 1u << 1;
    private const uint IsDpsBit = 1u << 5;
    private const uint IsTfesBit = 1u << 30;

    private const uint SerrDiagXBit = 1u << 26;

    private const uint SignatureAtaDisk = 0x0000_0101;
    private const uint SignatureNoDevice = 0xFFFF_FFFF;
    private const uint SstsDeviceReady = 0x133; // DET=3, SPD=3, IPM=1

    private const byte TfdStatusNormal = 0x50;
    private const byte TfdStatusError = 0x51;
    private const byte AtaErrorAbort = 0x04;
    private const byte AtaErrorIdNotFound = 0x10;

    // --- command list / table layout, mirroring AhciPort.Commands.cs's own private constants ---

    private const uint CommandHeaderLength = 0x20;
    private const uint CommandHeaderPrdbcOffset = 0x04;
    private const uint CommandHeaderCtbaOffset = 0x08;
    private const uint PrdtOffset = 0x80;
    private const uint PrdtEntryLength = 0x10;
    private const uint PrdtDbcOffset = 0x0C;
    private const uint PrdtInterruptBit = 1u << 31;

    private const byte AtaIdentifyDevice = 0xEC;
    private const byte AtaReadDmaExt = 0x25;
    private const byte AtaReadDma = 0xC8; // LBA28/CHS
    private const byte AtaWriteDmaExt = 0x35;
    private const byte AtaFlushCache = 0xE7;
    private const byte AtaSetFeatures = 0xEF;
    private const byte SetFeaturesTransferMode = 0x03;

    /// <summary>Device register bit 6, "L": set names an LBA28 sector; clear names legacy CHS (A5).</summary>
    private const byte DeviceLbaBit = 0x40;

    // IDENTIFY word offsets touched by A4's tests, in bytes from the buffer's own start.
    private const uint IdentifyWord60Offset = 120;
    private const uint IdentifyWord61Offset = 122;
    private const uint IdentifyWord63Offset = 126;
    private const uint IdentifyWord76Offset = 152;
    private const uint IdentifyWord88Offset = 176;

    // Fixed RAM addresses this suite's command lists and buffers use --
    // arbitrary, only required to be inside RamSize and correctly aligned.
    private const uint Clb = 0x1000;
    private const uint Fb = 0x2000;
    private const uint Ctba = 0x3000;
    private const uint Ctba1 = 0x3200; // a second command table, for a second slot
    private const uint DataBuffer = 0x4000;
    private const uint DataBufferB = 0x5000;

    private static (AhciController Controller, PciBus Pci, PhysicalMemoryBus Memory,
        PciInterruptRouter Router, InterruptController Pic) Build()
    {
        var pci = new PciBus();
        var io = new IoBus();
        var memory = new PhysicalMemoryBus(RamSize, AddressableBytes);
        memory.A20Enabled = true;
        var pic = new InterruptController();
        pic.Register(io);
        InitializePic(io);

        var bridge = new PciFunction(8, 0x8086, 0x122E, 0x060100);
        pci.Add(bridge);
        var router = new PciInterruptRouter(pic, bridge);
        pci.InterruptRouter = router;
        pci.DmaMemory = new PhysicalDmaMemory(memory);

        var controller = new AhciController(pci, memory, FunctionAddress);
        pci.WriteConfiguration(FunctionAddress, 0x24, 4, AbarBase); // BAR5
        return (controller, pci, memory, router, pic);
    }

    private static void InitializePic(IoBus io)
    {
        foreach (ushort port in new ushort[] { 0x20, 0xA0 })
        {
            io.WriteByte(port, 0x11);
            io.WriteByte((ushort)(port + 1), port == 0x20 ? (byte)0x20 : (byte)0x28);
            io.WriteByte((ushort)(port + 1), port == 0x20 ? (byte)4 : (byte)2);
            io.WriteByte((ushort)(port + 1), 1);
            io.WriteByte((ushort)(port + 1), 0);
        }
    }

    private static void EnableMemoryAndBusMaster(PciBus pci) =>
        pci.WriteConfiguration(FunctionAddress, 4, 2, 0x06); // memory space + bus master

    private static uint PortOffset(int port) => PortRegisterBase + (uint)port * PortRegisterStride;

    private static uint ReadAbar(PhysicalMemoryBus memory, uint offset) => memory.ReadDword(AbarBase + offset);

    private static void WriteAbar(PhysicalMemoryBus memory, uint offset, uint value) => memory.WriteDword(AbarBase + offset, value);

    private static void ConfigurePort(PhysicalMemoryBus memory, uint portBase)
    {
        WriteAbar(memory, portBase + RegClb, Clb);
        WriteAbar(memory, portBase + RegFb, Fb);
    }

    private static void SetFreAndSt(PhysicalMemoryBus memory, uint portBase) =>
        WriteAbar(memory, portBase + RegCmd, CmdFreBit | CmdStBit);

    private static void WriteCommandHeader(PhysicalMemoryBus memory, uint clb, int slot, uint ctba, int prdtl)
    {
        uint header = clb + (uint)(slot * CommandHeaderLength);
        memory.WriteDword(header, (uint)prdtl << 16);
        memory.WriteDword(header + CommandHeaderPrdbcOffset, 0);
        memory.WriteDword(header + CommandHeaderCtbaOffset, ctba);
        memory.WriteDword(header + 0x0C, 0); // CTBAU
    }

    private static void WriteH2DFis(PhysicalMemoryBus memory, uint ctba, byte command, long lba, ushort count, byte device = DeviceLbaBit, byte features = 0)
    {
        memory.WriteByte(ctba + 0, 0x27); // FIS type: Register H2D
        memory.WriteByte(ctba + 1, 0x80); // C bit: this is a command
        memory.WriteByte(ctba + 2, command);
        memory.WriteByte(ctba + 3, features);
        memory.WriteByte(ctba + 4, (byte)lba);
        memory.WriteByte(ctba + 5, (byte)(lba >> 8));
        memory.WriteByte(ctba + 6, (byte)(lba >> 16));
        memory.WriteByte(ctba + 7, device);
        memory.WriteByte(ctba + 8, (byte)(lba >> 24));
        memory.WriteByte(ctba + 9, (byte)(lba >> 32));
        memory.WriteByte(ctba + 10, (byte)(lba >> 40));
        memory.WriteByte(ctba + 12, (byte)count);
        memory.WriteByte(ctba + 13, (byte)(count >> 8));
    }

    private static void WritePrdtEntry(PhysicalMemoryBus memory, uint ctba, int index, uint dataAddress, int byteCount, bool interrupt)
    {
        uint entry = ctba + PrdtOffset + (uint)(index * PrdtEntryLength);
        memory.WriteDword(entry, dataAddress);
        memory.WriteDword(entry + 4, 0); // DBAU
        memory.WriteDword(entry + 8, 0); // reserved
        uint dw3 = (uint)(byteCount - 1) & 0x003F_FFFF;
        if (interrupt)
        {
            dw3 |= PrdtInterruptBit;
        }
        memory.WriteDword(entry + PrdtDbcOffset, dw3);
    }

    /// <summary>Builds a one-PRDT-entry command in slot 0's table and issues it through CI, waiting for the synchronous engine to finish.</summary>
    private static void IssueSingleEntryCommand(PhysicalMemoryBus memory, uint portBase, byte command,
        long lba, ushort count, uint dataAddress, int byteCount)
    {
        WriteCommandHeader(memory, Clb, 0, Ctba, byteCount > 0 ? 1 : 0);
        WriteH2DFis(memory, Ctba, command, lba, count);
        if (byteCount > 0)
        {
            WritePrdtEntry(memory, Ctba, 0, dataAddress, byteCount, interrupt: true);
        }
        WriteAbar(memory, portBase + RegCi, 1u);
    }

    /// <summary>Un-swaps one of IDENTIFY's ASCII fields back into a readable string -- the inverse of <c>AhciPort.WriteIdentifyText</c>.</summary>
    private static string DecodeIdentifyText(PhysicalMemoryBus memory, uint bufferAddress, int firstWord, int wordCount)
    {
        var chars = new char[wordCount * 2];
        for (int i = 0; i < wordCount; i++)
        {
            byte low = memory.ReadByte(bufferAddress + (uint)(firstWord + i) * 2);
            byte high = memory.ReadByte(bufferAddress + (uint)(firstWord + i) * 2 + 1);
            chars[i * 2] = (char)high;
            chars[i * 2 + 1] = (char)low;
        }
        return new string(chars);
    }

    private static ushort ReadIdentifyWord(PhysicalMemoryBus memory, uint bufferAddress, uint byteOffset) =>
        (ushort)(memory.ReadByte(bufferAddress + byteOffset) | (memory.ReadByte(bufferAddress + byteOffset + 1) << 8));

    private static ulong ReadLba48SectorCount(PhysicalMemoryBus memory, uint bufferAddress)
    {
        ulong word100 = memory.ReadByte(bufferAddress + 200) | ((ulong)memory.ReadByte(bufferAddress + 201) << 8);
        ulong word101 = memory.ReadByte(bufferAddress + 202) | ((ulong)memory.ReadByte(bufferAddress + 203) << 8);
        ulong word102 = memory.ReadByte(bufferAddress + 204) | ((ulong)memory.ReadByte(bufferAddress + 205) << 8);
        ulong word103 = memory.ReadByte(bufferAddress + 206) | ((ulong)memory.ReadByte(bufferAddress + 207) << 8);
        return word100 | (word101 << 16) | (word102 << 32) | (word103 << 48);
    }

    private static DiskImage AttachDisk(AhciController controller, int port = 0) =>
        AttachDisk(controller, 16, 4, 32, port); // 2048 sectors, ~1 MB

    private static DiskImage AttachDisk(AhciController controller, int cylinders, int heads, int sectorsPerTrack, int port)
    {
        var disk = DiskImage.CreateInMemory(0x80, cylinders, heads, sectorsPerTrack);
        controller.AttachDisk(port, disk);
        return disk;
    }

    // ------------------------------------------------------------ PCI identity and generic host control

    [Fact]
    public void PciIdentityAndClassMatchIch9Ahci()
    {
        (_, PciBus pci, _, _, _) = Build();

        Assert.Equal(0x8086u, pci.ReadConfiguration(FunctionAddress, 0, 2));
        Assert.Equal(0x2922u, pci.ReadConfiguration(FunctionAddress, 2, 2));
        Assert.Equal(0x02u, pci.ReadConfiguration(FunctionAddress, 8, 1)); // revision
        Assert.Equal(0x010601u, pci.ReadConfiguration(FunctionAddress, 8, 4) >> 8); // mass storage/SATA/AHCI
        Assert.Equal(1u, pci.ReadConfiguration(FunctionAddress, 0x3D, 1)); // INTA
    }

    [Fact]
    public void Bar5SizesToOnePageAndSitsAtOffset24h()
    {
        (_, PciBus pci, _, _, _) = Build();

        pci.WriteConfiguration(FunctionAddress, 0x24, 4, uint.MaxValue);
        uint mask = pci.ReadConfiguration(FunctionAddress, 0x24, 4) & 0xFFFF_FFF0u;
        Assert.Equal(0x1000u, ~mask + 1);

        pci.WriteConfiguration(FunctionAddress, 0x24, 4, AbarBase);
    }

    [Fact]
    public void CapAdvertisesSixPortsThirtyTwoSlotsSataOnlyModeAndGen3()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);

        uint cap = ReadAbar(memory, RegCap);
        Assert.Equal(5u, cap & 0x1Fu); // NP: six ports (NP+1)
        Assert.Equal(31u, (cap >> 8) & 0x1Fu); // NCS: 32 command slots (NCS+1)
        Assert.Equal(1u, (cap >> 18) & 1u); // SAM: AHCI-only mode
        Assert.Equal(3u, (cap >> 20) & 0xFu); // ISS: Gen3, 6 Gb/s
        Assert.Equal(1u, (cap >> 24) & 1u); // SCLO: supports PxCMD.CLO, the command-list-override "clear busy status" bit
        Assert.Equal(1u, (cap >> 31) & 1u); // S64A: accepts (and ignores) a 64-bit CLB/FB/CTBA
    }

    [Fact]
    public void GhcAeReadsOneBecauseCapSamIsOne()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);

        Assert.Equal(GhcAeBit, ReadAbar(memory, RegGhc) & GhcAeBit);
    }

    [Fact]
    public void PiImplementsAllSixPorts()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);

        Assert.Equal(0x3Fu, ReadAbar(memory, RegPi));
    }

    [Fact]
    public void VsReportsAhci130()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);

        Assert.Equal(0x0001_0300u, ReadAbar(memory, RegVs));
    }

    // ------------------------------------------------------------ port device detection

    [Fact]
    public void SstsAndSigReflectWhetherAPortHasADiskAttached()
    {
        (AhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);
        uint portBase = PortOffset(0);

        Assert.Equal(0u, ReadAbar(memory, portBase + RegSsts));
        Assert.Equal(SignatureNoDevice, ReadAbar(memory, portBase + RegSig));

        AttachDisk(controller);

        Assert.Equal(SstsDeviceReady, ReadAbar(memory, portBase + RegSsts));
        Assert.Equal(SignatureAtaDisk, ReadAbar(memory, portBase + RegSig));
    }

    [Fact]
    public void GhcHrClearsPortStateAndSelfClears()
    {
        (AhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);
        AttachDisk(controller);
        uint portBase = PortOffset(0);
        ConfigurePort(memory, portBase);
        WriteAbar(memory, portBase + RegPortIe, IsDhrsBit);
        WriteAbar(memory, RegGhc, GhcIeBit);

        WriteAbar(memory, RegGhc, GhcHrBit);

        Assert.Equal(0u, ReadAbar(memory, portBase + RegClb));
        Assert.Equal(0u, ReadAbar(memory, portBase + RegFb));
        Assert.Equal(0u, ReadAbar(memory, portBase + RegPortIe));
        Assert.Equal(0u, ReadAbar(memory, RegGhc) & GhcHrBit); // self-cleared
        Assert.Equal(0u, ReadAbar(memory, RegGhc) & GhcIeBit); // forced off by HR
    }

    [Fact]
    public void FreDrivesFrAndStDrivesCr()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);
        uint portBase = PortOffset(0);

        WriteAbar(memory, portBase + RegCmd, CmdFreBit);
        uint cmd = ReadAbar(memory, portBase + RegCmd);
        Assert.NotEqual(0u, cmd & CmdFrBit);
        Assert.Equal(0u, cmd & CmdCrBit);

        WriteAbar(memory, portBase + RegCmd, CmdFreBit | CmdStBit);
        cmd = ReadAbar(memory, portBase + RegCmd);
        Assert.NotEqual(0u, cmd & CmdCrBit);
    }

    // ------------------------------------------------------------ ATA commands through the command engine

    [Fact]
    public void IdentifyReturnsModelAndLba48SectorCountAndPostsPioSetupThenD2H()
    {
        (AhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        DiskImage disk = AttachDisk(controller);
        EnableMemoryAndBusMaster(pci);
        uint portBase = PortOffset(0);
        ConfigurePort(memory, portBase);
        SetFreAndSt(memory, portBase);

        IssueSingleEntryCommand(memory, portBase, AtaIdentifyDevice, lba: 0, count: 0, DataBuffer, byteCount: 512);

        uint header = Clb;
        Assert.Equal(512u, memory.ReadDword(header + CommandHeaderPrdbcOffset));

        string model = DecodeIdentifyText(memory, DataBuffer, firstWord: 27, wordCount: 20);
        Assert.Equal("Aurora386 Virtual SATA Disk", model.TrimEnd());

        Assert.Equal((ulong)disk.TotalSectors, ReadLba48SectorCount(memory, DataBuffer));

        Assert.Equal(0x5F, memory.ReadByte(Fb + 0x20)); // PIO Setup FIS type
        Assert.Equal(0x34, memory.ReadByte(Fb + 0x40)); // D2H Register FIS type

        uint portIs = ReadAbar(memory, portBase + RegPortIs);
        Assert.NotEqual(0u, portIs & IsPssBit);
        Assert.NotEqual(0u, portIs & IsDhrsBit);
        Assert.NotEqual(0u, portIs & IsDpsBit); // the PRDT entry asked for its own completion interrupt

        Assert.Equal((uint)TfdStatusNormal, ReadAbar(memory, portBase + RegTfd));
    }

    [Fact]
    public void ReadDmaExtScattersAcrossTwoPrdtEntriesAndReportsPrdbc()
    {
        (AhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        DiskImage disk = AttachDisk(controller);
        EnableMemoryAndBusMaster(pci);
        uint portBase = PortOffset(0);
        ConfigurePort(memory, portBase);
        SetFreAndSt(memory, portBase);

        const long lba = 10;
        const int sectorCount = 4;
        var pattern = new byte[sectorCount * DiskImage.SectorSize];
        for (int i = 0; i < pattern.Length; i++)
        {
            pattern[i] = (byte)(i * 7 + 3);
        }
        Assert.True(disk.WriteLinear(lba, sectorCount, pattern));

        WriteCommandHeader(memory, Clb, 0, Ctba, prdtl: 2);
        WriteH2DFis(memory, Ctba, AtaReadDmaExt, lba, (ushort)sectorCount);
        WritePrdtEntry(memory, Ctba, 0, DataBuffer, 1024, interrupt: false);
        WritePrdtEntry(memory, Ctba, 1, DataBufferB, 1024, interrupt: true);
        WriteAbar(memory, portBase + RegCi, 1u);

        for (int i = 0; i < 1024; i++)
        {
            Assert.Equal(pattern[i], memory.ReadByte(DataBuffer + (uint)i));
        }
        for (int i = 0; i < 1024; i++)
        {
            Assert.Equal(pattern[1024 + i], memory.ReadByte(DataBufferB + (uint)i));
        }

        Assert.Equal(2048u, memory.ReadDword(Clb + CommandHeaderPrdbcOffset));
        Assert.Equal((uint)TfdStatusNormal, ReadAbar(memory, portBase + RegTfd));

        uint portIs = ReadAbar(memory, portBase + RegPortIs);
        Assert.NotEqual(0u, portIs & IsDhrsBit);
        Assert.Equal(0u, portIs & IsPssBit); // DMA, not PIO: no PIO Setup FIS
    }

    [Fact]
    public void WriteDmaExtCommitsRamToTheDisk()
    {
        (AhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        DiskImage disk = AttachDisk(controller);
        EnableMemoryAndBusMaster(pci);
        uint portBase = PortOffset(0);
        ConfigurePort(memory, portBase);
        SetFreAndSt(memory, portBase);

        const long lba = 20;
        const int sectorCount = 2;
        var pattern = new byte[sectorCount * DiskImage.SectorSize];
        for (int i = 0; i < pattern.Length; i++)
        {
            pattern[i] = (byte)(i * 11 + 5);
        }
        for (int i = 0; i < pattern.Length; i++)
        {
            memory.WriteByte(DataBuffer + (uint)i, pattern[i]);
        }

        IssueSingleEntryCommand(memory, portBase, AtaWriteDmaExt, lba, (ushort)sectorCount, DataBuffer, pattern.Length);

        var readBack = new byte[pattern.Length];
        Assert.True(disk.ReadLinear(lba, sectorCount, readBack));
        Assert.Equal(pattern, readBack);
        Assert.Equal((uint)pattern.Length, memory.ReadDword(Clb + CommandHeaderPrdbcOffset));
        Assert.Equal((uint)TfdStatusNormal, ReadAbar(memory, portBase + RegTfd));
    }

    [Fact]
    public void UnknownCommandAbortsAndHaltsTheEngineWithBothSlotsStillInCi()
    {
        (AhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        AttachDisk(controller);
        EnableMemoryAndBusMaster(pci);
        uint portBase = PortOffset(0);
        ConfigurePort(memory, portBase);
        SetFreAndSt(memory, portBase);

        WriteCommandHeader(memory, Clb, 0, Ctba, prdtl: 0);
        WriteH2DFis(memory, Ctba, command: 0xFF, lba: 0, count: 0); // not an ATA command this disk knows
        WriteCommandHeader(memory, Clb, 1, Ctba1, prdtl: 0);
        WriteH2DFis(memory, Ctba1, AtaFlushCache, lba: 0, count: 0);

        WriteAbar(memory, portBase + RegCi, (1u << 0) | (1u << 1));

        uint tfd = ReadAbar(memory, portBase + RegTfd);
        Assert.Equal((uint)((AtaErrorAbort << 8) | TfdStatusError), tfd);
        Assert.NotEqual(0u, ReadAbar(memory, portBase + RegPortIs) & IsTfesBit);

        uint ci = ReadAbar(memory, portBase + RegCi);
        Assert.Equal(1u, ci & 1u); // the failing slot was never cleared
        Assert.Equal(2u, ci & 2u); // the second command never even started

        uint ccs = (ReadAbar(memory, portBase + RegCmd) >> 8) & 0x1F;
        Assert.Equal(0u, ccs); // PxCMD.CCS names the failing slot
    }

    [Fact]
    public void ReadPastTheDiskEndFailsWithIdNotFound()
    {
        (AhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        DiskImage disk = AttachDisk(controller);
        EnableMemoryAndBusMaster(pci);
        uint portBase = PortOffset(0);
        ConfigurePort(memory, portBase);
        SetFreAndSt(memory, portBase);

        IssueSingleEntryCommand(memory, portBase, AtaReadDmaExt, lba: disk.TotalSectors, count: 1, DataBuffer, byteCount: 512);

        uint tfd = ReadAbar(memory, portBase + RegTfd);
        Assert.Equal((uint)((AtaErrorIdNotFound << 8) | TfdStatusError), tfd);
        Assert.NotEqual(0u, ReadAbar(memory, portBase + RegPortIs) & IsTfesBit);
    }

    // ------------------------------------------------------------ interrupts and the bus-master gate

    [Fact]
    public void InterruptNeedsGhcIeAndPortIeTogetherAndClearsThroughPxisThenIs()
    {
        (AhciController controller, PciBus pci, PhysicalMemoryBus memory, PciInterruptRouter router, InterruptController pic) = Build();
        AttachDisk(controller);
        EnableMemoryAndBusMaster(pci);
        Assert.True(router.Route(FunctionAddress, 1, 11));
        uint portBase = PortOffset(0);
        ConfigurePort(memory, portBase);
        SetFreAndSt(memory, portBase);

        // PxIE clear: PxIS.DHRS becomes pending, but nothing reaches the PIC.
        IssueSingleEntryCommand(memory, portBase, AtaFlushCache, lba: 0, count: 0, DataBuffer, byteCount: 0);
        Assert.False(pic.HasPendingInterrupt);
        WriteAbar(memory, portBase + RegPortIs, IsDhrsBit); // clear the stray pending bit before the next case

        // PxIE set but GHC.IE clear: still not asserted.
        WriteAbar(memory, portBase + RegPortIe, IsDhrsBit);
        IssueSingleEntryCommand(memory, portBase, AtaFlushCache, lba: 0, count: 0, DataBuffer, byteCount: 0);
        Assert.False(pic.HasPendingInterrupt);

        // GHC.IE set too: now asserted.
        WriteAbar(memory, RegGhc, GhcIeBit);
        Assert.True(pic.HasPendingInterrupt);

        // Writing PxIS then (global) IS clears it, the usual driver sequence.
        WriteAbar(memory, portBase + RegPortIs, IsDhrsBit);
        WriteAbar(memory, RegIs, 1u);
        Assert.False(pic.HasPendingInterrupt);
    }

    [Fact]
    public void CommandIssuedWithoutBusMasterEnabledStaysQueuedInCi()
    {
        (AhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        AttachDisk(controller);
        pci.WriteConfiguration(FunctionAddress, 4, 2, 0x02); // memory space only, no bus master
        uint portBase = PortOffset(0);
        ConfigurePort(memory, portBase);
        SetFreAndSt(memory, portBase);

        IssueSingleEntryCommand(memory, portBase, AtaIdentifyDevice, lba: 0, count: 0, DataBuffer, byteCount: 512);

        Assert.Equal(1u, ReadAbar(memory, portBase + RegCi) & 1u); // never cleared: nothing ran
        Assert.Equal((uint)TfdStatusNormal, ReadAbar(memory, portBase + RegTfd)); // still the reset default
        Assert.Equal(0u, ReadAbar(memory, portBase + RegPortIs)); // no completion FIS was posted
    }

    // ------------------------------------------------------------ snapshot

    [Fact]
    public void SnapshotRoundTripRestoresGlobalAndPortRegisters()
    {
        (AhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        AttachDisk(controller);
        EnableMemoryAndBusMaster(pci);
        uint portBase = PortOffset(0);
        ConfigurePort(memory, portBase);
        WriteAbar(memory, RegGhc, GhcIeBit);
        WriteAbar(memory, portBase + RegPortIe, IsDhrsBit);
        SetFreAndSt(memory, portBase);

        // A COMRESET dance: DET=1 (asserted) then DET=0 (released) with a
        // disk attached posts the signature FIS and sets SERR.DIAG.X.
        WriteAbar(memory, portBase + RegSctl, 1);
        WriteAbar(memory, portBase + RegSctl, 0);

        IssueSingleEntryCommand(memory, portBase, AtaFlushCache, lba: 0, count: 0, DataBuffer, byteCount: 0);

        byte[] snapshot = controller.Capture();
        Assert.Equal(AhciController.SnapshotLength, snapshot.Length);

        uint expectedIs = ReadAbar(memory, portBase + RegPortIs);
        uint expectedSerr = ReadAbar(memory, portBase + RegSerr);
        Assert.NotEqual(0u, expectedSerr & SerrDiagXBit);

        // Corrupt the live registers so restore has something to prove.
        WriteAbar(memory, RegGhc, GhcHrBit); // wipes everything, including GHC.IE

        controller.Restore(snapshot, AhciController.SnapshotVersion);

        Assert.Equal(GhcIeBit, ReadAbar(memory, RegGhc) & GhcIeBit);
        Assert.Equal(Clb, ReadAbar(memory, portBase + RegClb));
        Assert.Equal(Fb, ReadAbar(memory, portBase + RegFb));
        Assert.Equal(IsDhrsBit, ReadAbar(memory, portBase + RegPortIe));
        Assert.Equal(expectedIs, ReadAbar(memory, portBase + RegPortIs));
        Assert.Equal(expectedSerr, ReadAbar(memory, portBase + RegSerr));
        Assert.Equal((uint)TfdStatusNormal, ReadAbar(memory, portBase + RegTfd));
        Assert.Equal(0u, ReadAbar(memory, portBase + RegCi)); // the flush command had already completed
    }

    // ------------------------------------------------------------ A1: PxCMD.ST 1->0 clears PxCI and PxSACT

    [Fact]
    public void ClearingStAfterATfesHaltClearsCiAndSactSoTheFailedSlotNeverReplays()
    {
        (AhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        AttachDisk(controller);
        EnableMemoryAndBusMaster(pci);
        uint portBase = PortOffset(0);
        ConfigurePort(memory, portBase);
        SetFreAndSt(memory, portBase);
        WriteAbar(memory, portBase + RegSact, 0x0000_0005u); // arbitrary nonzero, to prove it clears too

        WriteCommandHeader(memory, Clb, 0, Ctba, prdtl: 0);
        WriteH2DFis(memory, Ctba, command: 0xFF, lba: 0, count: 0); // not an ATA command this disk knows
        WriteAbar(memory, portBase + RegCi, 1u);

        Assert.Equal((uint)((AtaErrorAbort << 8) | TfdStatusError), ReadAbar(memory, portBase + RegTfd));
        Assert.NotEqual(0u, ReadAbar(memory, portBase + RegPortIs) & IsTfesBit);
        Assert.Equal(1u, ReadAbar(memory, portBase + RegCi) & 1u); // the failed slot, still set

        // AHCI 1.3.1 sections 3.3.13/3.3.14: ST 1->0 clears PxCI and PxSACT as a whole.
        WriteAbar(memory, portBase + RegCmd, CmdFreBit); // ST cleared, FRE kept
        Assert.Equal(0u, ReadAbar(memory, portBase + RegCi));
        Assert.Equal(0u, ReadAbar(memory, portBase + RegSact));

        // Software restarts the engine and issues a fresh command in a different slot.
        WriteCommandHeader(memory, Clb, 1, Ctba1, prdtl: 0);
        WriteH2DFis(memory, Ctba1, AtaFlushCache, lba: 0, count: 0);
        WriteAbar(memory, portBase + RegCmd, CmdFreBit | CmdStBit); // ST 0->1
        WriteAbar(memory, portBase + RegCi, 1u << 1); // slot 1

        Assert.Equal((uint)TfdStatusNormal, ReadAbar(memory, portBase + RegTfd)); // the new command completed cleanly
        Assert.Equal(0u, ReadAbar(memory, portBase + RegCi)); // slot 1 completed and cleared; slot 0 never came back
        uint ccs = (ReadAbar(memory, portBase + RegCmd) >> 8) & 0x1F;
        Assert.Equal(1u, ccs); // PxCMD.CCS names the slot that actually ran, not the stale failure
    }

    // ------------------------------------------------------------ A2: an undersized PRD table never fakes success

    [Fact]
    public void ShortPrdtOnWriteDmaExtAbortsAndLeavesTheDiskImageUnchanged()
    {
        (AhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        DiskImage disk = AttachDisk(controller);
        EnableMemoryAndBusMaster(pci);
        uint portBase = PortOffset(0);
        ConfigurePort(memory, portBase);
        SetFreAndSt(memory, portBase);

        const long lba = 30;
        const int sectorCount = 2; // 1024 bytes required
        var before = new byte[sectorCount * DiskImage.SectorSize];
        for (int i = 0; i < before.Length; i++)
        {
            before[i] = (byte)(i + 1);
        }
        Assert.True(disk.WriteLinear(lba, sectorCount, before));

        for (int i = 0; i < 512; i++)
        {
            memory.WriteByte(DataBuffer + (uint)i, 0xAA); // what the guest would have written, if it fit
        }

        WriteCommandHeader(memory, Clb, 0, Ctba, prdtl: 1);
        WriteH2DFis(memory, Ctba, AtaWriteDmaExt, lba, (ushort)sectorCount);
        WritePrdtEntry(memory, Ctba, 0, DataBuffer, byteCount: 512, interrupt: false); // half of the 1024 bytes required
        WriteAbar(memory, portBase + RegCi, 1u);

        Assert.Equal((uint)((AtaErrorAbort << 8) | TfdStatusError), ReadAbar(memory, portBase + RegTfd));
        Assert.NotEqual(0u, ReadAbar(memory, portBase + RegPortIs) & IsTfesBit);
        Assert.Equal(512u, memory.ReadDword(Clb + CommandHeaderPrdbcOffset)); // PRDBC: what the table could gather

        var afterImage = new byte[before.Length];
        Assert.True(disk.ReadLinear(lba, sectorCount, afterImage));
        Assert.Equal(before, afterImage); // untouched: the short table never reached WriteLinear
    }

    [Fact]
    public void PrdtlZeroOnWriteDmaExtAbortsAndLeavesTheDiskImageUnchanged()
    {
        (AhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        DiskImage disk = AttachDisk(controller);
        EnableMemoryAndBusMaster(pci);
        uint portBase = PortOffset(0);
        ConfigurePort(memory, portBase);
        SetFreAndSt(memory, portBase);

        const long lba = 40;
        const int sectorCount = 1;
        var before = new byte[sectorCount * DiskImage.SectorSize];
        for (int i = 0; i < before.Length; i++)
        {
            before[i] = (byte)(i + 2);
        }
        Assert.True(disk.WriteLinear(lba, sectorCount, before));

        WriteCommandHeader(memory, Clb, 0, Ctba, prdtl: 0); // PRDTL=0: nowhere to gather from
        WriteH2DFis(memory, Ctba, AtaWriteDmaExt, lba, (ushort)sectorCount);
        WriteAbar(memory, portBase + RegCi, 1u);

        Assert.Equal((uint)((AtaErrorAbort << 8) | TfdStatusError), ReadAbar(memory, portBase + RegTfd));
        Assert.Equal(0u, memory.ReadDword(Clb + CommandHeaderPrdbcOffset));

        var afterImage = new byte[before.Length];
        Assert.True(disk.ReadLinear(lba, sectorCount, afterImage));
        Assert.Equal(before, afterImage);
    }

    [Fact]
    public void ShortPrdtOnReadDmaExtAbortsWithOnlyTheSuppliedBytesScattered()
    {
        (AhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        DiskImage disk = AttachDisk(controller);
        EnableMemoryAndBusMaster(pci);
        uint portBase = PortOffset(0);
        ConfigurePort(memory, portBase);
        SetFreAndSt(memory, portBase);

        const long lba = 50;
        const int sectorCount = 2; // 1024 bytes required
        var pattern = new byte[sectorCount * DiskImage.SectorSize];
        for (int i = 0; i < pattern.Length; i++)
        {
            pattern[i] = (byte)(i * 3 + 1);
        }
        Assert.True(disk.WriteLinear(lba, sectorCount, pattern));

        const byte sentinel = 0xEE;
        for (int i = 0; i < 1024; i++)
        {
            memory.WriteByte(DataBuffer + (uint)i, sentinel);
        }

        WriteCommandHeader(memory, Clb, 0, Ctba, prdtl: 1);
        WriteH2DFis(memory, Ctba, AtaReadDmaExt, lba, (ushort)sectorCount);
        WritePrdtEntry(memory, Ctba, 0, DataBuffer, byteCount: 512, interrupt: false); // half of what's required
        WriteAbar(memory, portBase + RegCi, 1u);

        Assert.Equal((uint)((AtaErrorAbort << 8) | TfdStatusError), ReadAbar(memory, portBase + RegTfd));
        Assert.NotEqual(0u, ReadAbar(memory, portBase + RegPortIs) & IsTfesBit);
        Assert.Equal(512u, memory.ReadDword(Clb + CommandHeaderPrdbcOffset));

        for (int i = 0; i < 512; i++)
        {
            Assert.Equal(pattern[i], memory.ReadByte(DataBuffer + (uint)i)); // what the table could hold did land
        }
        for (int i = 512; i < 1024; i++)
        {
            Assert.Equal(sentinel, memory.ReadByte(DataBuffer + (uint)i)); // never written past the PRDT's own entry
        }
    }

    [Fact]
    public void PrdtlZeroOnReadDmaExtAbortsWithNothingScattered()
    {
        (AhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        DiskImage disk = AttachDisk(controller);
        EnableMemoryAndBusMaster(pci);
        uint portBase = PortOffset(0);
        ConfigurePort(memory, portBase);
        SetFreAndSt(memory, portBase);

        const long lba = 60;
        const int sectorCount = 1;
        var pattern = new byte[sectorCount * DiskImage.SectorSize];
        for (int i = 0; i < pattern.Length; i++)
        {
            pattern[i] = (byte)(i + 9);
        }
        Assert.True(disk.WriteLinear(lba, sectorCount, pattern));

        const byte sentinel = 0xDD;
        memory.WriteByte(DataBuffer, sentinel);

        WriteCommandHeader(memory, Clb, 0, Ctba, prdtl: 0); // no PRDT entries at all
        WriteH2DFis(memory, Ctba, AtaReadDmaExt, lba, (ushort)sectorCount);
        WriteAbar(memory, portBase + RegCi, 1u);

        Assert.Equal((uint)((AtaErrorAbort << 8) | TfdStatusError), ReadAbar(memory, portBase + RegTfd));
        Assert.Equal(0u, memory.ReadDword(Clb + CommandHeaderPrdbcOffset));
        Assert.Equal(sentinel, memory.ReadByte(DataBuffer)); // nothing scattered
    }

    // ------------------------------------------------------------ A4: IDENTIFY words 60-61, 63, 76, 88

    [Fact]
    public void Word63AdvertisesMultiwordDmaModesAndTracksSetFeatures()
    {
        (AhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        AttachDisk(controller);
        EnableMemoryAndBusMaster(pci);
        uint portBase = PortOffset(0);
        ConfigurePort(memory, portBase);
        SetFreAndSt(memory, portBase);

        IssueSingleEntryCommand(memory, portBase, AtaIdentifyDevice, lba: 0, count: 0, DataBuffer, byteCount: 512);
        ushort word63 = ReadIdentifyWord(memory, DataBuffer, IdentifyWord63Offset);
        Assert.Equal(0x0007, word63 & 0x0007); // modes 0-2 supported
        Assert.Equal(0, word63 & 0xFF00); // none selected: the reset default lives in word 88 (Ultra DMA), not here

        WriteCommandHeader(memory, Clb, 0, Ctba, prdtl: 0);
        WriteH2DFis(memory, Ctba, AtaSetFeatures, lba: 0, count: 0x0022, features: SetFeaturesTransferMode); // multiword DMA mode 2
        WriteAbar(memory, portBase + RegCi, 1u);
        Assert.Equal((uint)TfdStatusNormal, ReadAbar(memory, portBase + RegTfd));

        IssueSingleEntryCommand(memory, portBase, AtaIdentifyDevice, lba: 0, count: 0, DataBuffer, byteCount: 512);
        word63 = ReadIdentifyWord(memory, DataBuffer, IdentifyWord63Offset);
        Assert.Equal(1 << 10, word63 & 0xFF00); // bit 8+2: multiword DMA mode 2 selected
    }

    [Fact]
    public void Word88DefaultsToUltraDma5AndTracksSetFeatures()
    {
        (AhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        AttachDisk(controller);
        EnableMemoryAndBusMaster(pci);
        uint portBase = PortOffset(0);
        ConfigurePort(memory, portBase);
        SetFreAndSt(memory, portBase);

        IssueSingleEntryCommand(memory, portBase, AtaIdentifyDevice, lba: 0, count: 0, DataBuffer, byteCount: 512);
        ushort word88 = ReadIdentifyWord(memory, DataBuffer, IdentifyWord88Offset);
        Assert.Equal(0x007F, word88 & 0x007F); // modes 0-6 supported
        Assert.Equal(1 << (8 + 5), word88 & 0x7F00); // UDMA5 selected by default (*choice*, A4)

        WriteCommandHeader(memory, Clb, 0, Ctba, prdtl: 0);
        WriteH2DFis(memory, Ctba, AtaSetFeatures, lba: 0, count: 0x0046, features: SetFeaturesTransferMode); // Ultra DMA mode 6
        WriteAbar(memory, portBase + RegCi, 1u);
        Assert.Equal((uint)TfdStatusNormal, ReadAbar(memory, portBase + RegTfd));

        IssueSingleEntryCommand(memory, portBase, AtaIdentifyDevice, lba: 0, count: 0, DataBuffer, byteCount: 512);
        word88 = ReadIdentifyWord(memory, DataBuffer, IdentifyWord88Offset);
        Assert.Equal(1 << (8 + 6), word88 & 0x7F00); // UDMA6 now selected
    }

    [Fact]
    public void Word76AdvertisesGen1Gen2Gen3MatchingCapIss()
    {
        (AhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        AttachDisk(controller);
        EnableMemoryAndBusMaster(pci);
        uint portBase = PortOffset(0);
        ConfigurePort(memory, portBase);
        SetFreAndSt(memory, portBase);

        IssueSingleEntryCommand(memory, portBase, AtaIdentifyDevice, lba: 0, count: 0, DataBuffer, byteCount: 512);

        Assert.Equal(0x000E, ReadIdentifyWord(memory, DataBuffer, IdentifyWord76Offset)); // Gen1+Gen2+Gen3, matching CAP.ISS=3
    }

    [Fact]
    [Trait("Speed", "Slow")]
    public void Words60And61CapAtTheLba28MaximumForADiskLargerThanIt()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ahci-lba28-cap-{Guid.NewGuid():N}.img");
        try
        {
            // 16800 x 255 x 63 sectors, ~269.9 million, past the LBA28 maximum
            // of 268,435,455 (0FFFFFFFh). A sparse file: this costs no real
            // disk space even though its logical size is ~129 GB.
            using DiskImage disk = DiskImage.CreateSparseFile(path, 0x80, 16800, 255, 63);
            Assert.True(disk.TotalSectors > 0x0FFF_FFFFL);

            (AhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
            controller.AttachDisk(0, disk);
            EnableMemoryAndBusMaster(pci);
            uint portBase = PortOffset(0);
            ConfigurePort(memory, portBase);
            SetFreAndSt(memory, portBase);

            IssueSingleEntryCommand(memory, portBase, AtaIdentifyDevice, lba: 0, count: 0, DataBuffer, byteCount: 512);

            uint capacity = ReadIdentifyWord(memory, DataBuffer, IdentifyWord60Offset)
                | ((uint)ReadIdentifyWord(memory, DataBuffer, IdentifyWord61Offset) << 16);
            Assert.Equal(0x0FFF_FFFFu, capacity);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ------------------------------------------------------------ A5: SUD/POD hardwired, ICC self-clears, RW1C lanes, LBA28 CHS

    [Fact]
    public void SudAndPodAlwaysReadOneBecauseCapSssIsZero()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);
        uint portBase = PortOffset(0);

        uint cmd = ReadAbar(memory, portBase + RegCmd);
        Assert.NotEqual(0u, cmd & CmdSudBit);
        Assert.NotEqual(0u, cmd & CmdPodBit);

        // A write attempting to clear them has no effect: both are read-only in this configuration.
        WriteAbar(memory, portBase + RegCmd, 0);
        cmd = ReadAbar(memory, portBase + RegCmd);
        Assert.NotEqual(0u, cmd & CmdSudBit);
        Assert.NotEqual(0u, cmd & CmdPodBit);
    }

    [Fact]
    public void IccReadsZeroImmediatelyAfterAWrite()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        EnableMemoryAndBusMaster(pci);
        uint portBase = PortOffset(0);

        WriteAbar(memory, portBase + RegCmd, 5u << CmdIccShift); // request ICC = 5

        uint cmd = ReadAbar(memory, portBase + RegCmd);
        Assert.Equal(0u, cmd & CmdIccMask); // no link power state machine: the transition is already "done"
    }

    [Fact]
    public void ByteAndWordWritesToPortIsOnlyClearBitsInTheirOwnLanes()
    {
        (AhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        AttachDisk(controller);
        EnableMemoryAndBusMaster(pci);
        uint portBase = PortOffset(0);
        ConfigurePort(memory, portBase);
        SetFreAndSt(memory, portBase);

        // An unknown command sets DHRS (bit 0, lane 0) and TFES (bit 30, lane 3) together.
        WriteCommandHeader(memory, Clb, 0, Ctba, prdtl: 0);
        WriteH2DFis(memory, Ctba, command: 0xFF, lba: 0, count: 0);
        WriteAbar(memory, portBase + RegCi, 1u);
        uint pending = ReadAbar(memory, portBase + RegPortIs);
        Assert.NotEqual(0u, pending & IsDhrsBit);
        Assert.NotEqual(0u, pending & IsTfesBit);

        // A byte write to lane 0 (PxIS+0) clearing only bit 0 must leave bit 30, in lane 3, alone.
        memory.WriteByte(AbarBase + portBase + RegPortIs, 0x01);
        pending = ReadAbar(memory, portBase + RegPortIs);
        Assert.Equal(0u, pending & IsDhrsBit);
        Assert.NotEqual(0u, pending & IsTfesBit); // still pending: a different lane, never addressed

        // Re-arm DHRS through the COMRESET path (independent of the halted command engine) for the word case.
        WriteAbar(memory, portBase + RegSctl, 1);
        WriteAbar(memory, portBase + RegSctl, 0);
        pending = ReadAbar(memory, portBase + RegPortIs);
        Assert.NotEqual(0u, pending & IsDhrsBit);
        Assert.NotEqual(0u, pending & IsTfesBit); // TFES from before is still pending too

        // A word write to PxIS+0 (lanes 0-1, bits 0-15) clearing only bit 0 must leave bit 30, in lane 3, alone.
        memory.WriteWord(AbarBase + portBase + RegPortIs, 0x0001);
        pending = ReadAbar(memory, portBase + RegPortIs);
        Assert.Equal(0u, pending & IsDhrsBit);
        Assert.NotEqual(0u, pending & IsTfesBit);
    }

    [Fact]
    public void Lba28HonoursDeviceRegisterBitSixFallingBackToChsWhenClear()
    {
        (AhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        DiskImage disk = AttachDisk(controller); // 16 cylinders, 4 heads, 32 sectors/track
        EnableMemoryAndBusMaster(pci);
        uint portBase = PortOffset(0);
        ConfigurePort(memory, portBase);
        SetFreAndSt(memory, portBase);

        const int cylinder = 2;
        const int head = 1;
        const int sector = 5;
        long expectedLba = disk.ToLinearSector(cylinder, head, sector);
        var pattern = new byte[DiskImage.SectorSize];
        for (int i = 0; i < pattern.Length; i++)
        {
            pattern[i] = (byte)(i + 21);
        }
        Assert.True(disk.WriteLinear(expectedLba, 1, pattern));

        byte device = (byte)head; // bit 6 (LBA) clear: CHS addressing
        WriteCommandHeader(memory, Clb, 0, Ctba, prdtl: 1);
        WriteH2DFis(memory, Ctba, AtaReadDma, lba: 0, count: 1, device: device);
        // The CHS fields ride the same byte positions LBA0/1/2 otherwise would: sector, cylinder low, cylinder high.
        memory.WriteByte(Ctba + 4, (byte)sector);
        memory.WriteByte(Ctba + 5, (byte)cylinder);
        memory.WriteByte(Ctba + 6, (byte)(cylinder >> 8));
        WritePrdtEntry(memory, Ctba, 0, DataBuffer, DiskImage.SectorSize, interrupt: false);
        WriteAbar(memory, portBase + RegCi, 1u);

        Assert.Equal((uint)TfdStatusNormal, ReadAbar(memory, portBase + RegTfd));
        for (int i = 0; i < pattern.Length; i++)
        {
            Assert.Equal(pattern[i], memory.ReadByte(DataBuffer + (uint)i));
        }
    }

    [Fact]
    public void Lba28WithDeviceBitSixSetStillAddressesByLba()
    {
        (AhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        DiskImage disk = AttachDisk(controller);
        EnableMemoryAndBusMaster(pci);
        uint portBase = PortOffset(0);
        ConfigurePort(memory, portBase);
        SetFreAndSt(memory, portBase);

        const long lba = 77;
        var pattern = new byte[DiskImage.SectorSize];
        for (int i = 0; i < pattern.Length; i++)
        {
            pattern[i] = (byte)(i + 33);
        }
        Assert.True(disk.WriteLinear(lba, 1, pattern));

        IssueSingleEntryCommand(memory, portBase, AtaReadDma, lba, count: 1, DataBuffer, byteCount: DiskImage.SectorSize);

        Assert.Equal((uint)TfdStatusNormal, ReadAbar(memory, portBase + RegTfd));
        for (int i = 0; i < pattern.Length; i++)
        {
            Assert.Equal(pattern[i], memory.ReadByte(DataBuffer + (uint)i));
        }
    }

    [Fact]
    public void InvalidChsAddressFailsWithIdNotFound()
    {
        (AhciController controller, PciBus pci, PhysicalMemoryBus memory, _, _) = Build();
        AttachDisk(controller); // 16 cylinders, 4 heads, 32 sectors/track
        EnableMemoryAndBusMaster(pci);
        uint portBase = PortOffset(0);
        ConfigurePort(memory, portBase);
        SetFreAndSt(memory, portBase);

        WriteCommandHeader(memory, Clb, 0, Ctba, prdtl: 1);
        WriteH2DFis(memory, Ctba, AtaReadDma, lba: 0, count: 1, device: 0); // head 0, LBA bit clear
        memory.WriteByte(Ctba + 4, 0); // sector 0 does not exist: sectors start at 1
        memory.WriteByte(Ctba + 5, 0);
        memory.WriteByte(Ctba + 6, 0);
        WritePrdtEntry(memory, Ctba, 0, DataBuffer, DiskImage.SectorSize, interrupt: false);
        WriteAbar(memory, portBase + RegCi, 1u);

        Assert.Equal((uint)((AtaErrorIdNotFound << 8) | TfdStatusError), ReadAbar(memory, portBase + RegTfd));
    }
}
