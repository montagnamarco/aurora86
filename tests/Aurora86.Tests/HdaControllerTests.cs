using Aurora86.Core.Devices.Audio;
using Aurora86.Core.Devices.Interrupts;
using Aurora86.Core.Devices.Pci;
using Aurora86.Core.IO;
using Aurora86.Core.Memory;

namespace Aurora86.Tests;

/// <summary>
/// The Intel High Definition Audio controller: PCI identity and the memory
/// BAR (BAR0), the global registers (GCAP/GCTL/STATESTS/INTCTL/INTSTS), the
/// CORB/RIRB command ring and the immediate command interface (IC/IR/ICS)
/// reaching <see cref="HdaCodec"/>'s node registers, one output stream
/// descriptor's own DMA (Buffer Descriptor List, LPIB, interrupts, the DMA
/// position buffer), and the host audio path. See <c>docs/HD-AUDIO.md</c>
/// for the register map and node graph these tests exercise, and every
/// *choice* it made where the specification left room.
/// </summary>
public class HdaControllerTests
{
    private const ushort FunctionAddress = 0x0020;
    private const int RamSize = 4 * 1024 * 1024;
    private const int AddressableBytes = 8 * 1024 * 1024;

    /// <summary>Above installed RAM, inside the harness's own <see cref="AddressableBytes"/> -- the same placement pattern <c>Rtl8139Tests</c> uses for its own memory BAR.</summary>
    private const uint MemoryBase = 0x600000;

    // --- register offsets, mirroring HdaController's own private constants ---

    private const uint RegGcap = 0x00;
    private const uint RegVmin = 0x02;
    private const uint RegVmaj = 0x03;
    private const uint RegOutpay = 0x04;
    private const uint RegGctl = 0x08;
    private const uint RegWakeen = 0x0C;
    private const uint RegStatests = 0x0E;
    private const uint RegIntctl = 0x20;
    private const uint RegIntsts = 0x24;
    private const uint RegCorbLBase = 0x40;
    private const uint RegCorbUBase = 0x44;
    private const uint RegCorbWp = 0x48;
    private const uint RegCorbRp = 0x4A;
    private const uint RegCorbCtl = 0x4C;
    private const uint RegRirbLBase = 0x50;
    private const uint RegRirbUBase = 0x54;
    private const uint RegRirbWp = 0x58;
    private const uint RegRintCnt = 0x5A;
    private const uint RegRirbCtl = 0x5C;
    private const uint RegRirbSts = 0x5D;
    private const uint RegIc = 0x60;
    private const uint RegIr = 0x64;
    private const uint RegIcs = 0x68;
    private const uint RegDplBase = 0x70;
    private const uint RegStreamsBase = 0x80;
    private const uint StreamStride = 0x20;

    private const uint StreamRelCtl0 = 0x00;
    private const uint StreamRelCtl2 = 0x02;
    private const uint StreamRelSts = 0x03;
    private const uint StreamRelLpib = 0x04;
    private const uint StreamRelCbl = 0x08;
    private const uint StreamRelLvi = 0x0C;
    private const uint StreamRelFmt = 0x12;
    private const uint StreamRelBdpl = 0x18;

    /// <summary>The first output stream descriptor (SD4), per GCAP's 4 input / 4 output split.</summary>
    private const int FirstOutputStream = 4;

    // --- bits --------------------------------------------------------------

    private const uint GctlCrstBit = 1u << 0;
    private const uint IntctlGieBit = 1u << 31;
    private const uint IntctlCieBit = 1u << 30;
    private const byte CorbCtlCorbRunBit = 1 << 1;
    private const byte RirbCtlRintCtlBit = 1 << 0;
    private const byte RirbCtlDmaEnBit = 1 << 1;
    private const byte RirbStsRintFlBit = 1 << 0;
    private const ushort IcsIcbBit = 1 << 0;
    private const ushort IcsIrvBit = 1 << 1;
    private const byte StreamCtlSrstBit = 1 << 0;
    private const byte StreamCtlRunBit = 1 << 1;
    private const byte StreamCtlIoceBit = 1 << 2;
    private const byte StreamStatusBcisBit = 1 << 2;
    private const byte StreamStatusFifoRdyBit = 1 << 5;
    private const uint DmaPositionEnableBit = 1u << 0;

    // --- amplifier verb payload bits, HDA spec 7.3.3.7 ----------------------

    private const ushort SetAmpOutputBit = 1 << 15;
    private const ushort SetAmpInputBit = 1 << 14;
    private const ushort SetAmpLeftBit = 1 << 13;
    private const ushort SetAmpRightBit = 1 << 12;
    private const ushort SetAmpIndexShift = 8;
    private const ushort SetAmpMuteBit = 1 << 7;

    /// <summary>Linux's own Get Amplifier Gain/Mute payloads: output amp (bit 15), left (bit 13) or right (bit 13 clear), index 0.</summary>
    private const ushort GetAmpLeftPayload = 0xA000;

    private const ushort GetAmpRightPayload = 0x8000;

    /// <summary>48000 Hz, 16-bit, stereo: base rate bit clear, multiplier/divisor both x1, bits field 1 (16-bit), channels field 1 (2 channels - 1).</summary>
    private const ushort Format48000Stereo16Bit = (1 << 4) | 1;

    private static (HdaController Device, PciBus Pci, PhysicalMemoryBus Memory, PciInterruptRouter Router, InterruptController Pic) BuildWithDevice()
    {
        var pci = new PciBus();
        var memory = new PhysicalMemoryBus(RamSize, AddressableBytes);
        memory.A20Enabled = true;
        var pic = new InterruptController();
        var io = new IoBus();
        pic.Register(io);
        InitializePic(io);

        var bridge = new PciFunction(8, 0x8086, 0x122E, 0x060100);
        pci.Add(bridge);
        var router = new PciInterruptRouter(pic, bridge);
        pci.InterruptRouter = router;
        pci.DmaMemory = new PhysicalDmaMemory(memory);

        var device = new HdaController(pci, memory, FunctionAddress);
        return (device, pci, memory, router, pic);
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

    private static void EnableMemoryAndBusMaster(PciBus pci)
    {
        pci.WriteConfiguration(FunctionAddress, 0x10, 4, MemoryBase);
        pci.WriteConfiguration(FunctionAddress, 4, 2, 0x06); // memory space + bus master
    }

    private static void BringOutOfReset(PhysicalMemoryBus memory) => memory.WriteDword(MemoryBase + RegGctl, GctlCrstBit);

    private static uint ExecuteNarrowVerb(PhysicalMemoryBus memory, byte nid, ushort verbId, byte payload)
    {
        uint ic = ((uint)HdaCodec.CodecAddress << 28) | ((uint)nid << 20) | ((uint)verbId << 8) | payload;
        memory.WriteDword(MemoryBase + RegIc, ic);
        memory.WriteWord(MemoryBase + RegIcs, IcsIcbBit);
        return memory.ReadDword(MemoryBase + RegIr);
    }

    private static uint ExecuteWideVerb(PhysicalMemoryBus memory, byte nid, uint wideVerbId, ushort payload)
    {
        uint ic = ((uint)HdaCodec.CodecAddress << 28) | ((uint)nid << 20) | (wideVerbId << 16) | payload;
        memory.WriteDword(MemoryBase + RegIc, ic);
        memory.WriteWord(MemoryBase + RegIcs, IcsIcbBit);
        return memory.ReadDword(MemoryBase + RegIr);
    }

    private static void WriteBdlEntry(PhysicalMemoryBus memory, uint bdlAddress, int index, uint bufferAddress, uint length, bool ioc)
    {
        uint entryAddress = bdlAddress + (uint)index * 16;
        memory.WriteDword(entryAddress, bufferAddress);
        memory.WriteDword(entryAddress + 4, 0); // address high dword: unreachable in this model, see docs/HD-AUDIO.md
        memory.WriteDword(entryAddress + 8, length);
        memory.WriteDword(entryAddress + 12, ioc ? 1u : 0u);
    }

    /// <summary>Programs SD[<paramref name="streamIndex"/>]'s BDPL/CBL/LVI/FMT and sets its own stream tag, then sets RUN (and IOCE if asked) in CTL byte 0 -- the same order a driver follows before starting playback.</summary>
    private static void ConfigureAndRunStream(PhysicalMemoryBus memory, int streamIndex, uint bdlAddress, uint cbl, ushort lvi, ushort fmt, byte streamTag, bool ioce)
    {
        uint streamBase = MemoryBase + RegStreamsBase + (uint)streamIndex * StreamStride;
        memory.WriteDword(streamBase + StreamRelBdpl, bdlAddress);
        memory.WriteDword(streamBase + StreamRelBdpl + 4, 0);
        memory.WriteDword(streamBase + StreamRelCbl, cbl);
        memory.WriteWord(streamBase + StreamRelLvi, lvi);
        memory.WriteWord(streamBase + StreamRelFmt, fmt);
        memory.WriteByte(streamBase + StreamRelCtl2, (byte)(streamTag << 4));
        memory.WriteByte(streamBase + StreamRelCtl0, (byte)(StreamCtlRunBit | (ioce ? StreamCtlIoceBit : 0)));
    }

    private static byte ReadStreamStatus(PhysicalMemoryBus memory, int streamIndex)
        => memory.ReadByte(MemoryBase + RegStreamsBase + (uint)streamIndex * StreamStride + StreamRelSts);

    private static uint ReadStreamLpib(PhysicalMemoryBus memory, int streamIndex)
        => memory.ReadDword(MemoryBase + RegStreamsBase + (uint)streamIndex * StreamStride + StreamRelLpib);

    /// <summary>Advances in small steps (well under one sample frame's own period) until <paramref name="condition"/> holds, so a crossing never overshoots by more than the step itself.</summary>
    private static void AdvanceUntil(HdaController device, Func<bool> condition, double stepMicroseconds = 1, double capMicroseconds = 5000)
    {
        double elapsed = 0;
        while (elapsed < capMicroseconds && !condition())
        {
            device.Advance(stepMicroseconds);
            elapsed += stepMicroseconds;
        }
    }

    // ------------------------------------------------------------ identity

    [Fact]
    public void PciIdentityClassAndBar0SizeAre8086293ERev03Class040300WithA16KMemoryBar()
    {
        (_, PciBus pci, _, _, _) = BuildWithDevice();

        Assert.Equal(0x8086u, pci.ReadConfiguration(FunctionAddress, 0, 2));
        Assert.Equal(0x293Eu, pci.ReadConfiguration(FunctionAddress, 2, 2));
        Assert.Equal(0x040300u, pci.ReadConfiguration(FunctionAddress, 8, 4) >> 8);
        Assert.Equal(0x03u, pci.ReadConfiguration(FunctionAddress, 8, 1));
        Assert.Equal(1u, pci.ReadConfiguration(FunctionAddress, 0x3D, 1)); // INTA

        pci.WriteConfiguration(FunctionAddress, 0x10, 4, uint.MaxValue);
        uint bar0Mask = pci.ReadConfiguration(FunctionAddress, 0x10, 4) & 0xFFFFFFF0u;
        Assert.Equal(0x4000u, ~bar0Mask + 1);

        pci.WriteConfiguration(FunctionAddress, 0x10, 4, MemoryBase);
    }

    // ------------------------------------------------------------ global registers

    [Fact]
    public void GcapVmajAndOutpayReportFourInputFourOutputStreamsVersionOneAndSixtyBytesOutputPayload()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableMemoryAndBusMaster(pci);

        Assert.Equal(0x4401, memory.ReadWord(MemoryBase + RegGcap));
        Assert.Equal(0x00, memory.ReadByte(MemoryBase + RegVmin));
        Assert.Equal(0x01, memory.ReadByte(MemoryBase + RegVmaj));
        Assert.Equal(0x3C, memory.ReadWord(MemoryBase + RegOutpay));
    }

    [Fact]
    public void SettingGctlCrstBringsTheControllerOutOfResetAndSetsStatestsBit0ForCodecZero()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableMemoryAndBusMaster(pci);

        Assert.Equal(0u, memory.ReadDword(MemoryBase + RegGctl) & GctlCrstBit);
        Assert.Equal(0, memory.ReadWord(MemoryBase + RegStatests));

        BringOutOfReset(memory);

        Assert.Equal(GctlCrstBit, memory.ReadDword(MemoryBase + RegGctl) & GctlCrstBit);
        Assert.Equal(1, memory.ReadWord(MemoryBase + RegStatests) & 1);
    }

    [Fact]
    public void EveryRegisterButGctlDiscardsWritesAndReadsItsResetValueWhileCrstIsClear()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableMemoryAndBusMaster(pci);

        memory.WriteWord(MemoryBase + RegWakeen, 0x1234);
        Assert.Equal(0, memory.ReadWord(MemoryBase + RegWakeen));

        memory.WriteByte(MemoryBase + RegCorbCtl, 0x03);
        Assert.Equal(0, memory.ReadByte(MemoryBase + RegCorbCtl));

        uint sd0Cbl = MemoryBase + RegStreamsBase + StreamRelCbl;
        memory.WriteDword(sd0Cbl, 0xAABBCCDDu);
        Assert.Equal(0u, memory.ReadDword(sd0Cbl));
    }

    // ------------------------------------------------------------ CORB/RIRB

    [Fact]
    public void CorbRpResetHandshakeReadsBackTheBitAndZeroesTheReadPointer()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableMemoryAndBusMaster(pci);
        BringOutOfReset(memory);

        memory.WriteByte(MemoryBase + RegCorbRp + 1, 0x80);
        Assert.Equal(0x80, memory.ReadByte(MemoryBase + RegCorbRp + 1));
        Assert.Equal(0, memory.ReadByte(MemoryBase + RegCorbRp));

        memory.WriteByte(MemoryBase + RegCorbRp + 1, 0x00);
        Assert.Equal(0x00, memory.ReadByte(MemoryBase + RegCorbRp + 1));
    }

    [Fact]
    public void GetParameterVendorIdThroughCorbLandsResponseInRirbWithRintflAndTheRoutedInterrupt()
    {
        (HdaController device, PciBus pci, PhysicalMemoryBus memory, PciInterruptRouter router, InterruptController pic) = BuildWithDevice();
        EnableMemoryAndBusMaster(pci);
        BringOutOfReset(memory);
        Assert.True(router.Route(FunctionAddress, 1, 11));

        const uint corbAddress = 0x100000;
        const uint rirbAddress = 0x110000;
        memory.WriteDword(MemoryBase + RegCorbLBase, corbAddress);
        memory.WriteDword(MemoryBase + RegCorbUBase, 0);
        memory.WriteDword(MemoryBase + RegRirbLBase, rirbAddress);
        memory.WriteDword(MemoryBase + RegRirbUBase, 0);
        memory.WriteWord(MemoryBase + RegRintCnt, 1); // interrupt after a single response
        memory.WriteByte(MemoryBase + RegRirbCtl, (byte)(RirbCtlRintCtlBit | RirbCtlDmaEnBit));
        memory.WriteDword(MemoryBase + RegIntctl, IntctlGieBit | IntctlCieBit);
        memory.WriteByte(MemoryBase + RegCorbCtl, CorbCtlCorbRunBit);

        // Get Parameter (F00h), NID 0 (root), parameter 00h (Vendor ID) --
        // written at CORB index 1 since CORBWP starts at 0 (see
        // HdaController.Corb.cs's own class summary).
        const ushort getParameterVerb = 0xF00;
        uint verb = ((uint)HdaCodec.CodecAddress << 28) | (0u << 20) | ((uint)getParameterVerb << 8) | 0x00;
        memory.WriteDword(corbAddress + 4, verb);
        memory.WriteWord(MemoryBase + RegCorbWp, 1);

        Assert.Equal(0x1AF40022u, memory.ReadDword(rirbAddress + 8)); // RIRB entry 1's response dword
        Assert.Equal(1, memory.ReadWord(MemoryBase + RegRirbWp) & 0xFF);
        Assert.NotEqual(0, memory.ReadByte(MemoryBase + RegRirbSts) & RirbStsRintFlBit);
        Assert.Equal(0x2B, pic.GetPendingVector()); // IRQ 11 -> vector 2Bh
        _ = device;
    }

    [Fact]
    public void ImmediateCommandThroughIcAndIcsReturnsTheRevisionIdInIr()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableMemoryAndBusMaster(pci);
        BringOutOfReset(memory);

        uint response = ExecuteNarrowVerb(memory, nid: 0, verbId: 0xF00, payload: 0x02); // root node, Revision ID
        Assert.Equal(0x00100101u, response);
        Assert.Equal(0x00100101u, memory.ReadDword(MemoryBase + RegIr));
        Assert.NotEqual(0, memory.ReadWord(MemoryBase + RegIcs) & IcsIrvBit);
    }

    // ------------------------------------------------------------ codec node graph

    [Fact]
    public void WidgetCapabilitiesOfTheDacAndThePinMatchTheDocumentedNodeGraph()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableMemoryAndBusMaster(pci);
        BringOutOfReset(memory);

        uint dacCaps = ExecuteNarrowVerb(memory, nid: 2, verbId: 0xF00, payload: 0x09);
        uint pinCaps = ExecuteNarrowVerb(memory, nid: 3, verbId: 0xF00, payload: 0x09);

        Assert.Equal(0x0000001Du, dacCaps); // stereo, output amp present, amp param override, format override, type 0
        Assert.Equal(0x00400101u, pinCaps); // stereo, connection list, type 4 (pin complex)
    }

    [Fact]
    public void PinDefaultConfigurationResetsTo01014010h()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableMemoryAndBusMaster(pci);
        BringOutOfReset(memory);

        uint config = ExecuteNarrowVerb(memory, nid: 3, verbId: 0xF1C, payload: 0);
        Assert.Equal(0x01014010u, config);
    }

    [Fact]
    public void ConnectionListEntryZeroOfThePinReportsTheDacAsNid2()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableMemoryAndBusMaster(pci);
        BringOutOfReset(memory);

        uint entry = ExecuteNarrowVerb(memory, nid: 3, verbId: 0xF02, payload: 0);
        Assert.Equal(0x02u, entry);
    }

    [Fact]
    public void SettingAndReadingTheDacsAmplifierGainAndMuteRoundTrips()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableMemoryAndBusMaster(pci);
        BringOutOfReset(memory);

        const uint setAmpWideId = 0x3;
        const uint getAmpWideId = 0xB;

        ushort setUnmuted = (ushort)(SetAmpOutputBit | SetAmpLeftBit | SetAmpRightBit | 0x4A); // top step, unmuted
        ExecuteWideVerb(memory, nid: 2, wideVerbId: setAmpWideId, payload: setUnmuted);
        uint left = ExecuteWideVerb(memory, nid: 2, wideVerbId: getAmpWideId, payload: GetAmpLeftPayload);
        uint right = ExecuteWideVerb(memory, nid: 2, wideVerbId: getAmpWideId, payload: GetAmpRightPayload);

        Assert.Equal(0x4Au, left & 0x7F);
        Assert.Equal(0u, left & 0x80);
        Assert.Equal(0x4Au, right & 0x7F);
        Assert.Equal(0u, right & 0x80);

        ushort setMuted = (ushort)(SetAmpOutputBit | SetAmpLeftBit | SetAmpRightBit | SetAmpMuteBit | 0x20);
        ExecuteWideVerb(memory, nid: 2, wideVerbId: setAmpWideId, payload: setMuted);
        uint mutedLeft = ExecuteWideVerb(memory, nid: 2, wideVerbId: getAmpWideId, payload: GetAmpLeftPayload);
        Assert.NotEqual(0u, mutedLeft & 0x80);
        Assert.Equal(0x20u, mutedLeft & 0x7F);
    }

    [Fact]
    public void SettingTheInputAmpOrANonzeroIndexChangesNothingAndGettingANonexistentAmpReturnsZero()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableMemoryAndBusMaster(pci);
        BringOutOfReset(memory);

        const uint setAmpWideId = 0x3;
        const uint getAmpWideId = 0xB;

        // A known, non-default state to prove neither of the following two
        // writes disturbs it.
        ExecuteWideVerb(memory, nid: 2, wideVerbId: setAmpWideId, payload: (ushort)(SetAmpOutputBit | SetAmpLeftBit | SetAmpRightBit | 0x10));

        // Bit 14 (input amp) without bit 15 (output amp): this codec has no input amp on the DAC, so nothing changes.
        ExecuteWideVerb(memory, nid: 2, wideVerbId: setAmpWideId, payload: (ushort)(SetAmpInputBit | SetAmpLeftBit | 0x7F));
        Assert.Equal(0x10u, ExecuteWideVerb(memory, nid: 2, wideVerbId: getAmpWideId, payload: GetAmpLeftPayload) & 0x7F);

        // A nonzero amplifier index: only index 0 exists.
        ExecuteWideVerb(memory, nid: 2, wideVerbId: setAmpWideId, payload: (ushort)(SetAmpOutputBit | SetAmpLeftBit | (1 << SetAmpIndexShift) | 0x7F));
        Assert.Equal(0x10u, ExecuteWideVerb(memory, nid: 2, wideVerbId: getAmpWideId, payload: GetAmpLeftPayload) & 0x7F);

        // Get without the output bit (an amp that does not exist here) answers 0.
        Assert.Equal(0u, ExecuteWideVerb(memory, nid: 2, wideVerbId: getAmpWideId, payload: SetAmpLeftBit));
    }

    [Fact]
    public void OutputAmplifierCapabilitiesReport80034A4hMuteCapableOneDecibelStepsSeventyFiveStepsOffsetAtTheTop()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableMemoryAndBusMaster(pci);
        BringOutOfReset(memory);

        // Get Parameter (F00h) 0Dh: Output Amplifier Capabilities, HDA spec 7.3.4.10.
        uint caps = ExecuteNarrowVerb(memory, nid: 2, verbId: 0xF00, payload: 0x0D);
        Assert.Equal(0x80034A4Au, caps);
    }

    // ------------------------------------------------------------ stream DMA

    [Fact]
    public void OutputStreamWithATwoEntryBdlSetsBcisAndIntstsBitFourAfterTheFirstIocEntry()
    {
        (HdaController device, PciBus pci, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableMemoryAndBusMaster(pci);
        BringOutOfReset(memory);

        const uint bdlAddress = 0x120000;
        const uint bufferAddress = 0x121000;
        const uint entryLength = 16; // 4 frames of 16-bit stereo
        WriteBdlEntry(memory, bdlAddress, 0, bufferAddress, entryLength, ioc: true);
        WriteBdlEntry(memory, bdlAddress, 1, bufferAddress + entryLength, entryLength, ioc: false);

        ConfigureAndRunStream(memory, FirstOutputStream, bdlAddress, cbl: entryLength * 2, lvi: 1,
            fmt: Format48000Stereo16Bit, streamTag: 1, ioce: true);

        AdvanceUntil(device, () => (ReadStreamStatus(memory, FirstOutputStream) & StreamStatusBcisBit) != 0);

        Assert.Equal(entryLength, ReadStreamLpib(memory, FirstOutputStream));
        Assert.NotEqual(0, ReadStreamStatus(memory, FirstOutputStream) & StreamStatusBcisBit);
        Assert.NotEqual(0u, memory.ReadDword(MemoryBase + RegIntsts) & (1u << FirstOutputStream));
    }

    [Fact]
    public void LpibWrapsAtCblAfterTheSecondBdlEntryCompletes()
    {
        (HdaController device, PciBus pci, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableMemoryAndBusMaster(pci);
        BringOutOfReset(memory);

        const uint bdlAddress = 0x130000;
        const uint bufferAddress = 0x131000;
        const uint entryLength = 16;
        WriteBdlEntry(memory, bdlAddress, 0, bufferAddress, entryLength, ioc: true);
        WriteBdlEntry(memory, bdlAddress, 1, bufferAddress + entryLength, entryLength, ioc: true);

        ConfigureAndRunStream(memory, FirstOutputStream, bdlAddress, cbl: entryLength * 2, lvi: 1,
            fmt: Format48000Stereo16Bit, streamTag: 1, ioce: true);

        AdvanceUntil(device, () => (ReadStreamStatus(memory, FirstOutputStream) & StreamStatusBcisBit) != 0);
        Assert.Equal(entryLength, ReadStreamLpib(memory, FirstOutputStream));
        memory.WriteByte(MemoryBase + RegStreamsBase + FirstOutputStream * StreamStride + StreamRelSts, StreamStatusBcisBit); // RW1C acknowledge

        AdvanceUntil(device, () => (ReadStreamStatus(memory, FirstOutputStream) & StreamStatusBcisBit) != 0);
        Assert.Equal(0u, ReadStreamLpib(memory, FirstOutputStream)); // wrapped at CBL exactly
    }

    [Fact]
    public void SrstHandshakeReadsBackWhatWasWritten()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableMemoryAndBusMaster(pci);
        BringOutOfReset(memory);

        uint streamBase = MemoryBase + RegStreamsBase + FirstOutputStream * StreamStride;
        memory.WriteByte(streamBase + StreamRelCtl0, StreamCtlSrstBit);
        Assert.Equal(StreamCtlSrstBit, memory.ReadByte(streamBase + StreamRelCtl0) & StreamCtlSrstBit);

        memory.WriteByte(streamBase + StreamRelCtl0, 0);
        Assert.Equal(0, memory.ReadByte(streamBase + StreamRelCtl0) & StreamCtlSrstBit);
    }

    [Fact]
    public void SrstDoesNotPermanentlyClearFifordyOnceTheStreamIsOutOfResetAgain()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableMemoryAndBusMaster(pci);
        BringOutOfReset(memory);

        uint streamBase = MemoryBase + RegStreamsBase + FirstOutputStream * StreamStride;
        Assert.NotEqual(0, memory.ReadByte(streamBase + StreamRelSts) & StreamStatusFifoRdyBit); // fresh reset: ready

        memory.WriteByte(streamBase + StreamRelCtl0, StreamCtlSrstBit);
        Assert.Equal(0, memory.ReadByte(streamBase + StreamRelSts) & StreamStatusFifoRdyBit); // held in reset: not ready

        memory.WriteByte(streamBase + StreamRelCtl0, 0); // leave SRST
        Assert.NotEqual(0, memory.ReadByte(streamBase + StreamRelSts) & StreamStatusFifoRdyBit); // set up again: ready once more

        // A second SRST cycle behaves identically: FIFORDY is computed from
        // the current SRST bit, not a one-shot latch SRST can wipe forever.
        memory.WriteByte(streamBase + StreamRelCtl0, StreamCtlSrstBit);
        memory.WriteByte(streamBase + StreamRelCtl0, 0);
        Assert.NotEqual(0, memory.ReadByte(streamBase + StreamRelSts) & StreamStatusFifoRdyBit);
    }

    [Fact]
    public void InputStreamSilenceNeverWritesPastTheCurrentBdlEntry()
    {
        (HdaController device, PciBus pci, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableMemoryAndBusMaster(pci);
        BringOutOfReset(memory);

        const int inputStream = 0; // SD0
        const uint bufferAddress = 0x1A0000;
        const uint entryLength = 5; // not a multiple of the 4-byte, 16-bit-stereo frame
        const uint bdlAddress = 0x1A1000;
        WriteBdlEntry(memory, bdlAddress, 0, bufferAddress, entryLength, ioc: false);

        const byte sentinel = 0xCC;
        memory.WriteByte(bufferAddress + entryLength, sentinel); // just past the entry's own buffer

        ConfigureAndRunStream(memory, inputStream, bdlAddress, cbl: entryLength, lvi: 0,
            fmt: Format48000Stereo16Bit, streamTag: 0, ioce: false);

        device.Advance(200); // several frames' worth, wrapping the short ring many times over

        Assert.Equal(sentinel, memory.ReadByte(bufferAddress + entryLength));
    }

    [Fact]
    public void IntstsStreamBitReflectsRawBcisEvenWithoutIoceButNeverAssertsTheInterruptLine()
    {
        (HdaController device, PciBus pci, PhysicalMemoryBus memory, PciInterruptRouter router, InterruptController pic) = BuildWithDevice();
        EnableMemoryAndBusMaster(pci);
        BringOutOfReset(memory);
        Assert.True(router.Route(FunctionAddress, 1, 11));

        // GIE and this stream's own SIE bit are both set, but IOCE (SDnCTL)
        // is deliberately left clear below: HDA spec 3.3.14 does not gate
        // the raw INTSTS bit by IOCE, only the interrupt line's own
        // assertion is gated by it (3.3.13), matching QEMU's intel-hda.c.
        memory.WriteDword(MemoryBase + RegIntctl, IntctlGieBit | (1u << FirstOutputStream));

        const uint bdlAddress = 0x1B0000;
        const uint bufferAddress = 0x1B1000;
        const uint entryLength = 16;
        WriteBdlEntry(memory, bdlAddress, 0, bufferAddress, entryLength, ioc: true);

        ConfigureAndRunStream(memory, FirstOutputStream, bdlAddress, cbl: entryLength, lvi: 0,
            fmt: Format48000Stereo16Bit, streamTag: 1, ioce: false); // IOCE left clear

        AdvanceUntil(device, () => (ReadStreamStatus(memory, FirstOutputStream) & StreamStatusBcisBit) != 0);

        Assert.NotEqual(0u, memory.ReadDword(MemoryBase + RegIntsts) & (1u << FirstOutputStream)); // raw, unqualified
        Assert.False(pic.HasPendingInterrupt); // never qualified to assert the line: IOCE was never set
    }

    [Fact]
    public void IocCompletionAssertsThePciInterruptLineAndAcknowledgingBcisDeassertsIt()
    {
        (HdaController device, PciBus pci, PhysicalMemoryBus memory, PciInterruptRouter router, InterruptController pic) = BuildWithDevice();
        EnableMemoryAndBusMaster(pci);
        BringOutOfReset(memory);
        Assert.True(router.Route(FunctionAddress, 1, 11));
        memory.WriteDword(MemoryBase + RegIntctl, IntctlGieBit | (1u << FirstOutputStream));

        const uint bdlAddress = 0x1C0000;
        const uint bufferAddress = 0x1C1000;
        const uint entryLength = 16;
        WriteBdlEntry(memory, bdlAddress, 0, bufferAddress, entryLength, ioc: true);

        Assert.False(pic.HasPendingInterrupt);

        ConfigureAndRunStream(memory, FirstOutputStream, bdlAddress, cbl: entryLength, lvi: 0,
            fmt: Format48000Stereo16Bit, streamTag: 1, ioce: true);

        AdvanceUntil(device, () => pic.HasPendingInterrupt);
        Assert.True(pic.HasPendingInterrupt);

        memory.WriteByte(MemoryBase + RegStreamsBase + FirstOutputStream * StreamStride + StreamRelSts, StreamStatusBcisBit); // RW1C acknowledge

        Assert.False(pic.HasPendingInterrupt);
    }

    [Fact]
    public void RirbResponseWithRintctlAssertsThePciInterruptLineAndWritingRintflDeassertsIt()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, PciInterruptRouter router, InterruptController pic) = BuildWithDevice();
        EnableMemoryAndBusMaster(pci);
        BringOutOfReset(memory);
        Assert.True(router.Route(FunctionAddress, 1, 11));

        const uint corbAddress = 0x1D0000;
        const uint rirbAddress = 0x1D1000;
        memory.WriteDword(MemoryBase + RegCorbLBase, corbAddress);
        memory.WriteDword(MemoryBase + RegRirbLBase, rirbAddress);
        memory.WriteWord(MemoryBase + RegRintCnt, 1);
        memory.WriteByte(MemoryBase + RegRirbCtl, (byte)(RirbCtlRintCtlBit | RirbCtlDmaEnBit));
        memory.WriteDword(MemoryBase + RegIntctl, IntctlGieBit | IntctlCieBit);

        Assert.False(pic.HasPendingInterrupt);

        memory.WriteByte(MemoryBase + RegCorbCtl, CorbCtlCorbRunBit);
        uint verb = ((uint)HdaCodec.CodecAddress << 28) | ((uint)0xF00 << 8) | 0x00;
        memory.WriteDword(corbAddress + 4, verb);
        memory.WriteWord(MemoryBase + RegCorbWp, 1);

        Assert.True(pic.HasPendingInterrupt);

        memory.WriteByte(MemoryBase + RegRirbSts, RirbStsRintFlBit); // RW1C acknowledge

        Assert.False(pic.HasPendingInterrupt);
    }

    [Fact]
    public void ClearingBusMasterStopsCorbFetchAndRirbResponseWrites()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableMemoryAndBusMaster(pci);
        BringOutOfReset(memory);

        const uint corbAddress = 0x1E0000;
        const uint rirbAddress = 0x1E1000;
        memory.WriteDword(MemoryBase + RegCorbLBase, corbAddress);
        memory.WriteDword(MemoryBase + RegRirbLBase, rirbAddress);
        memory.WriteByte(MemoryBase + RegRirbCtl, (byte)(RirbCtlRintCtlBit | RirbCtlDmaEnBit));
        memory.WriteByte(MemoryBase + RegCorbCtl, CorbCtlCorbRunBit);

        const uint sentinel = 0xCCCCCCCCu;
        memory.WriteDword(rirbAddress + 8, sentinel); // where RIRB entry 1 would land

        pci.WriteConfiguration(FunctionAddress, 4, 2, 0x02); // memory space only, bus master off

        uint verb = ((uint)HdaCodec.CodecAddress << 28) | ((uint)0xF00 << 8) | 0x00;
        memory.WriteDword(corbAddress + 4, verb);
        memory.WriteWord(MemoryBase + RegCorbWp, 1); // would normally drain the CORB and write a response

        Assert.Equal(sentinel, memory.ReadDword(rirbAddress + 8)); // untouched: bus mastering was off
        Assert.Equal(0, memory.ReadWord(MemoryBase + RegRirbWp) & 0xFF); // never advanced
    }

    [Fact]
    public void SubordinateNodeCountReportsTheStartingNodeInBits23To16AndTheCountInBits7To0()
    {
        (_, PciBus pci, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableMemoryAndBusMaster(pci);
        BringOutOfReset(memory);

        uint rootCount = ExecuteNarrowVerb(memory, nid: 0, verbId: 0xF00, payload: 0x04);
        Assert.Equal(0x00010001u, rootCount); // starting node 1 (AFG), count 1

        uint afgCount = ExecuteNarrowVerb(memory, nid: 1, verbId: 0xF00, payload: 0x04);
        Assert.Equal(0x00020002u, afgCount); // starting node 2 (DAC), count 2 (DAC + Pin)
    }

    [Fact]
    public void DplBaseWritesEachStreamsOwnLpibAtBasePlusEightTimesTheStreamIndex()
    {
        (HdaController device, PciBus pci, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableMemoryAndBusMaster(pci);
        BringOutOfReset(memory);

        const uint positionBufferAddress = 0x140000;
        memory.WriteDword(MemoryBase + RegDplBase, positionBufferAddress | DmaPositionEnableBit);

        const uint bdlAddress = 0x141000;
        const uint bufferAddress = 0x142000;
        const uint entryLength = 64;
        WriteBdlEntry(memory, bdlAddress, 0, bufferAddress, entryLength, ioc: false);
        ConfigureAndRunStream(memory, FirstOutputStream, bdlAddress, cbl: entryLength, lvi: 0,
            fmt: Format48000Stereo16Bit, streamTag: 1, ioce: false);

        device.Advance(50);

        uint lpib = ReadStreamLpib(memory, FirstOutputStream);
        uint positionEntryAddress = positionBufferAddress + FirstOutputStream * 8;
        Assert.True(lpib > 0);
        Assert.Equal(lpib, memory.ReadDword(positionEntryAddress));
    }

    [Fact]
    public void ClearingBusMasterStopsLpibEvenWithMemorySpaceOpen()
    {
        (HdaController device, PciBus pci, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableMemoryAndBusMaster(pci);
        BringOutOfReset(memory);

        const uint bdlAddress = 0x150000;
        const uint bufferAddress = 0x151000;
        const uint entryLength = 64;
        WriteBdlEntry(memory, bdlAddress, 0, bufferAddress, entryLength, ioc: false);
        ConfigureAndRunStream(memory, FirstOutputStream, bdlAddress, cbl: entryLength, lvi: 0,
            fmt: Format48000Stereo16Bit, streamTag: 1, ioce: false);

        device.Advance(50);
        uint lpibWhileRunning = ReadStreamLpib(memory, FirstOutputStream);
        Assert.True(lpibWhileRunning > 0);

        pci.WriteConfiguration(FunctionAddress, 4, 2, 0x02); // memory space only, bus master off
        device.Advance(50);

        Assert.Equal(lpibWhileRunning, ReadStreamLpib(memory, FirstOutputStream));
    }

    // ------------------------------------------------------------ host audio path

    [Fact]
    public void TheDacBoundStreamsPcmReachesReadSamplesAndMutingSilencesIt()
    {
        (HdaController device, PciBus pci, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableMemoryAndBusMaster(pci);
        BringOutOfReset(memory);

        const short dcLeft = 12345;
        const short dcRight = -6789;
        const uint bufferAddress = 0x160000;
        const uint frameCount = 16;
        for (uint i = 0; i < frameCount; i++)
        {
            memory.WriteWord(bufferAddress + i * 4, unchecked((ushort)dcLeft));
            memory.WriteWord(bufferAddress + i * 4 + 2, unchecked((ushort)dcRight));
        }

        const uint bdlAddress = 0x161000;
        const uint cbl = frameCount * 4;
        WriteBdlEntry(memory, bdlAddress, 0, bufferAddress, cbl, ioc: false);

        const byte streamTag = 1;
        ConfigureAndRunStream(memory, FirstOutputStream, bdlAddress, cbl, lvi: 0,
            fmt: Format48000Stereo16Bit, streamTag: streamTag, ioce: false);

        // Bind the DAC to this stream's own tag (706h) and set its output
        // amplifier to the top step, unmuted -- 0 dB, per HdaCodec's own
        // *choice* scale.
        ExecuteNarrowVerb(memory, nid: 2, verbId: 0x706, payload: streamTag << 4);
        ExecuteWideVerb(memory, nid: 2, wideVerbId: 0x3, payload: (ushort)(SetAmpOutputBit | SetAmpLeftBit | SetAmpRightBit | 0x4A));

        device.Advance(300); // several 48 kHz frames' worth

        IAudioSource audioSource = device;
        Span<short> samples = stackalloc short[64];
        int written = audioSource.ReadSamples(samples);

        Assert.True(written > 0, "expected the DAC-bound stream's own PCM to have reached the host mix.");
        for (int i = 0; i < written; i += 2)
        {
            Assert.Equal(dcLeft, samples[i]);
            Assert.Equal(dcRight, samples[i + 1]);
        }

        // Mute the DAC and let the same, still non-zero, buffer render again.
        ExecuteWideVerb(memory, nid: 2, wideVerbId: 0x3, payload: (ushort)(SetAmpOutputBit | SetAmpLeftBit | SetAmpRightBit | SetAmpMuteBit | 0x4A));
        device.Advance(300);

        int mutedWritten = audioSource.ReadSamples(samples);
        Assert.True(mutedWritten > 0, "expected the mixer to still be fed while muted, just with silence.");
        for (int i = 0; i < mutedWritten; i++)
        {
            Assert.Equal(0, samples[i]);
        }
    }

    // ------------------------------------------------------------ snapshot

    [Fact]
    public void SnapshotRoundTripPreservesGlobalCommandAndStreamState()
    {
        (HdaController device, PciBus pci, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableMemoryAndBusMaster(pci);
        BringOutOfReset(memory);

        memory.WriteWord(MemoryBase + RegRintCnt, 4);
        ExecuteWideVerb(memory, nid: 2, wideVerbId: 0x3, payload: (ushort)(SetAmpOutputBit | SetAmpLeftBit | SetAmpRightBit | 0x30));

        const uint bdlAddress = 0x170000;
        const uint bufferAddress = 0x171000;
        const uint entryLength = 64;
        WriteBdlEntry(memory, bdlAddress, 0, bufferAddress, entryLength, ioc: false);
        ConfigureAndRunStream(memory, FirstOutputStream, bdlAddress, cbl: entryLength, lvi: 0,
            fmt: Format48000Stereo16Bit, streamTag: 1, ioce: false);
        device.Advance(50);

        byte[] snapshot = device.Capture();

        (HdaController restored, PciBus restoredPci, PhysicalMemoryBus restoredMemory, _, _) = BuildWithDevice();
        EnableMemoryAndBusMaster(restoredPci);
        restored.Restore(snapshot, HdaController.SnapshotVersion);

        Assert.Equal(memory.ReadDword(MemoryBase + RegGctl), restoredMemory.ReadDword(MemoryBase + RegGctl));
        Assert.Equal(ReadStreamLpib(memory, FirstOutputStream), ReadStreamLpib(restoredMemory, FirstOutputStream));
        Assert.Equal(
            memory.ReadDword(MemoryBase + RegStreamsBase + FirstOutputStream * StreamStride + StreamRelCbl),
            restoredMemory.ReadDword(MemoryBase + RegStreamsBase + FirstOutputStream * StreamStride + StreamRelCbl));

        uint left = ExecuteWideVerb(memory, nid: 2, wideVerbId: 0xB, payload: GetAmpLeftPayload);
        uint restoredLeft = ExecuteWideVerb(restoredMemory, nid: 2, wideVerbId: 0xB, payload: GetAmpLeftPayload);
        Assert.Equal(left & 0x7F, restoredLeft & 0x7F);
    }

    [Fact]
    public void SnapshotRejectsAWrongVersionOrLength()
    {
        (HdaController device, _, _, _, _) = BuildWithDevice();
        byte[] snapshot = device.Capture();

        Assert.Throws<InvalidDataException>(() => device.Restore(snapshot, HdaController.SnapshotVersion + 1));
        Assert.Throws<InvalidDataException>(() => device.Restore(snapshot[..^1], HdaController.SnapshotVersion));
    }
}
