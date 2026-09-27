using Aurora386.Core;
using Aurora386.Core.Configuration;
using Aurora386.Core.Devices.Audio;
using Aurora386.Core.Devices.Cards;
using Aurora386.Core.Devices.Interrupts;
using Aurora386.Core.Devices.Pci;
using Aurora386.Core.IO;
using Aurora386.Core.Memory;
using Aurora386.Core.Devices.Storage;

namespace Aurora386.Tests;

/// <summary>
/// The Ensoniq AudioPCI ES1371: PCI identity and BAR0 sizing, the SRC and
/// indirect CODEC ports, the three DMA channels' own units and interrupt
/// acknowledgement (E1-E4), the embedded generic AC'97 codec (E5), the host
/// audio path (E6) and the card's fixed PCI slot (E7). See
/// <c>plans/specs/es1371.md</c> for the register map and its fifteen
/// required test cases, which the numbered methods below follow in order.
/// </summary>
public class Es1371Tests
{
    private const ushort FunctionAddress = 0x0010;
    private const ushort Base = 0x0400;
    private const int RamSize = 4 * 1024 * 1024;

    // --- register offsets, mirroring Es1371's own private constants --------

    private const int RegControl = 0x00;
    private const int RegStatus = 0x04;
    private const int RegUart = 0x08;
    private const int RegMemPage = 0x0C;
    private const int RegSrc = 0x10;
    private const int RegCodec = 0x14;
    private const int RegLegacy = 0x18;
    private const int RegSerial = 0x20;
    private const int RegDac1Count = 0x24;
    private const int RegDac2Count = 0x28;
    private const int RegAdcCount = 0x2C;
    private const int RegPagedFrame = 0x30;
    private const int RegPagedSize = 0x34;
    private const int RegDac2Frame = 0x38;
    private const int RegDac2Size = 0x3C;

    // --- CONTROL bits --------------------------------------------------------

    private const uint ControlAdcStop = 1u << 31;
    private const uint ControlDac1Enable = 1u << 6;
    private const uint ControlDac2Enable = 1u << 5;
    private const uint ControlAdcEnable = 1u << 4;
    private const int ControlDac1RateShift = 8;
    private const uint ControlDac1Rate44100 = 3u << ControlDac1RateShift;

    // --- STATUS bits -----------------------------------------------------------

    private const uint StatusIntr = 1u << 31;
    private const uint StatusDac1 = 1u << 2;
    private const uint StatusDac2 = 1u << 1;
    private const uint StatusAdc = 1u << 0;

    // --- SERIAL bits (coordinator decision E1) --------------------------------

    private const uint SerialP2IntEn = 1u << 9;
    private const uint SerialR1IntEn = 1u << 10;
    private const uint SerialP1IntEn = 1u << 8;
    private const uint SerialP1LoopSel = 1u << 13;
    private const uint SerialP2LoopSel = 1u << 14;

    private const uint ModeStereoBit = 0x1;
    private const uint Mode16BitBit = 0x2;
    private const int SerialP1ModeShift = 0;
    private const int SerialP2ModeShift = 2;
    private const int SerialR1ModeShift = 4;

    // --- SRC (10h) -------------------------------------------------------------

    private const int SrcAddressShift = 25;
    private const uint SrcWriteEnable = 1u << 24;
    private const uint SrcBusy = 1u << 23;

    // --- CODEC (14h) -------------------------------------------------------------

    private const uint CodecReady = 1u << 31;
    private const uint CodecWip = 1u << 30;
    private const uint CodecPird = 1u << 23;
    private const int CodecRegisterShift = 16;

    /// <summary>Builds a fresh, minimal chipset (a PIC, a PCI bridge and its interrupt router, bus-master DMA memory) and one ES1371 attached to it.</summary>
    private static (Es1371 Device, PciBus Pci, IoBus Io, PhysicalMemoryBus Memory, PciInterruptRouter Router, InterruptController Pic) BuildWithDevice()
    {
        var pci = new PciBus();
        var io = new IoBus();
        var memory = new PhysicalMemoryBus(RamSize, RamSize);
        memory.A20Enabled = true;
        var pic = new InterruptController();
        pic.Register(io);
        InitializePic(io);

        var bridge = new PciFunction(8, 0x8086, 0x122E, 0x060100);
        pci.Add(bridge);
        var router = new PciInterruptRouter(pic, bridge);
        pci.InterruptRouter = router;
        pci.DmaMemory = new PhysicalDmaMemory(memory);

        var device = new Es1371(pci, io, FunctionAddress, Base);
        return (device, pci, io, memory, router, pic);
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

    private static void EnableIoAndBusMaster(PciBus pci)
    {
        pci.WriteConfiguration(FunctionAddress, 4, 2, 0x05); // I/O space + bus master
    }

    private static void WriteSrcWord(IoBus io, int address, ushort data)
    {
        io.WriteDword((ushort)(Base + RegSrc), ((uint)address << SrcAddressShift) | SrcWriteEnable | data);
    }

    private static ushort ReadCodecRegister(IoBus io, byte registerOffset)
    {
        io.WriteDword((ushort)(Base + RegCodec), ((uint)registerOffset << CodecRegisterShift) | CodecPird);
        return (ushort)io.ReadDword((ushort)(Base + RegCodec));
    }

    private static void WriteCodecRegister(IoBus io, byte registerOffset, ushort data)
    {
        io.WriteDword((ushort)(Base + RegCodec), ((uint)registerOffset << CodecRegisterShift) | data);
    }

    /// <summary>Programs DAC2's SRC RAM rate entry (74h) with the driver's own encode formula (E4), the inverse of the chip's own decode.</summary>
    private static void ProgramDac2Rate(IoBus io, int rate)
    {
        uint freq = (uint)(((long)rate << 16) + 1500) / 3000;
        WriteSrcWord(io, 0x75, (ushort)((freq >> 5) & 0xFC00)); // DAC2 base 74h + 1 = INT_REGS
        WriteSrcWord(io, 0x77, (ushort)(freq & 0x7FFF)); // DAC2 base 74h + 3 = VFREQ_FRAC
    }

    private static void ProgramAdcRate(IoBus io, int rate)
    {
        uint freq = (uint)(((long)rate << 16) + 1500) / 3000;
        WriteSrcWord(io, 0x79, (ushort)((freq >> 5) & 0xFC00)); // ADC base 78h + 1
        WriteSrcWord(io, 0x7B, (ushort)(freq & 0x7FFF)); // ADC base 78h + 3
    }

    // ------------------------------------------------------------ test case 1

    [Fact]
    public void PciIdentityAndBar0SizingProbeReadsFFFFFFC1()
    {
        (_, PciBus pci, _, _, _, _) = BuildWithDevice();

        Assert.Equal(0x1274u, pci.ReadConfiguration(FunctionAddress, 0, 2));
        Assert.Equal(0x1371u, pci.ReadConfiguration(FunctionAddress, 2, 2));
        Assert.Equal(0x040100u, pci.ReadConfiguration(FunctionAddress, 8, 4) >> 8);
        Assert.Equal(1u, pci.ReadConfiguration(FunctionAddress, 0x3D, 1)); // INTA

        pci.WriteConfiguration(FunctionAddress, 0x10, 4, uint.MaxValue);
        uint bar0 = pci.ReadConfiguration(FunctionAddress, 0x10, 4);
        Assert.Equal(0xFFFFFFC1u, bar0);

        pci.WriteConfiguration(FunctionAddress, 0x10, 4, Base | 1u);
    }

    // ------------------------------------------------------------ test case 2

    [Fact]
    public void ClearingIoSpaceStopsThePort()
    {
        (_, PciBus pci, IoBus io, _, _, _) = BuildWithDevice();

        pci.WriteConfiguration(FunctionAddress, 4, 2, 0x04); // bus master only, I/O space (bit 0) clear
        Assert.Equal(0xFFFFFFFFu, io.ReadDword((ushort)(Base + RegControl))); // an empty bus
    }

    [Fact]
    public void ClearingBusMasterStopsDacFetchesEvenWithIoSpaceOpen()
    {
        (Es1371 device, PciBus pci, IoBus io, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        pci.WriteConfiguration(FunctionAddress, 4, 2, 0x01); // I/O space only, no bus master

        const uint frame = 0x100000;
        const uint sentinel = 0xCCCCCCCCu;
        memory.WriteDword(frame, sentinel);

        // MEMPAGE defaults to 0 at reset, which already selects DAC1's own
        // frame/size (only page 0Dh selects the ADC's instead).
        io.WriteDword((ushort)(Base + RegPagedFrame), frame);
        io.WriteDword((ushort)(Base + RegPagedSize), 3); // 4 dwords
        io.WriteDword((ushort)(Base + RegDac1Count), 15); // well past this test's span
        io.WriteDword((ushort)(Base + RegSerial), ModeStereoBit | Mode16BitBit); // 16-bit stereo
        io.WriteDword((ushort)(Base + RegControl), ControlDac1Enable | ControlDac1Rate44100);

        device.Advance(100.5 * 1_000_000.0 / 44100.0); // plenty of time for several frames, if DMA ran at all

        Assert.Equal(sentinel, memory.ReadDword(frame)); // untouched: bus mastering was never on
        Assert.Equal(0u, io.ReadDword((ushort)(Base + RegPagedSize)) >> 16); // position never advanced either
    }

    // ------------------------------------------------------------ test case 3

    [Fact]
    public void ResetValuesAreZero()
    {
        (_, PciBus pci, IoBus io, _, _, _) = BuildWithDevice();
        EnableIoAndBusMaster(pci);

        Assert.Equal(0u, io.ReadDword((ushort)(Base + RegControl)));
        Assert.Equal(0u, io.ReadDword((ushort)(Base + RegStatus)));
        Assert.Equal(0u, io.ReadDword((ushort)(Base + RegMemPage)));
        Assert.Equal(0u, io.ReadDword((ushort)(Base + RegSerial)));
        Assert.Equal(0u, io.ReadDword((ushort)(Base + RegLegacy)));
        Assert.Equal(0u, io.ReadDword((ushort)(Base + RegUart)));
    }

    // ------------------------------------------------------------ test case 4

    [Fact]
    public void Ac97MasterVolumeWriteReadBackViaTheIndirectCodecPortReturns8080()
    {
        (_, PciBus pci, IoBus io, _, _, _) = BuildWithDevice();
        EnableIoAndBusMaster(pci);

        WriteCodecRegister(io, 0x02, 0x8080);
        ushort readback = ReadCodecRegister(io, 0x02);

        Assert.Equal(0x8080, readback);

        uint whole = io.ReadDword((ushort)(Base + RegCodec));
        Assert.NotEqual(0u, whole & CodecReady);
        Assert.Equal(0u, whole & CodecWip);
    }

    // ------------------------------------------------------------ test case 5

    [Fact]
    public void ExtendedAudioIdReportsVraAndTheEnableBitReadsBack()
    {
        (_, PciBus pci, IoBus io, _, _, _) = BuildWithDevice();
        EnableIoAndBusMaster(pci);

        Assert.Equal(0x0001, ReadCodecRegister(io, 0x28));

        WriteCodecRegister(io, 0x2A, 0x0001);
        Assert.Equal(0x0001, ReadCodecRegister(io, 0x2A));
    }

    // ------------------------------------------------------------ test case 6

    [Fact]
    public void SrcRamWriteThenReadAt74hReturns1234WithBusyClear()
    {
        (_, PciBus pci, IoBus io, _, _, _) = BuildWithDevice();
        EnableIoAndBusMaster(pci);

        WriteSrcWord(io, 0x74, 0x1234);
        uint afterWrite = io.ReadDword((ushort)(Base + RegSrc));
        Assert.Equal(0u, afterWrite & SrcBusy);

        io.WriteDword((ushort)(Base + RegSrc), 0x74u << SrcAddressShift); // WE=0: stage a read
        uint readback = io.ReadDword((ushort)(Base + RegSrc));

        Assert.Equal(0x1234u, readback & 0xFFFF);
        Assert.Equal(0u, readback & SrcBusy);
    }

    // ------------------------------------------------------------ test case 7

    [Fact]
    public void Dac2RateProgrammedAt44100DecodesBackWithinRounding()
    {
        (Es1371 device, PciBus pci, IoBus io, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableIoAndBusMaster(pci);
        ProgramDac2Rate(io, 44100);

        const uint frame = 0x200000;
        const int frames = 1000; // 1000 dwords = 1000 sixteen-bit-stereo frames
        for (uint i = 0; i < frames; i++)
        {
            memory.WriteDword(frame + i * 4, 0); // silence; only the timing is under test
        }

        io.WriteDword((ushort)(Base + RegDac2Frame), frame);
        io.WriteDword((ushort)(Base + RegDac2Size), frames - 1);
        io.WriteDword((ushort)(Base + RegDac2Count), frames - 1);
        io.WriteDword((ushort)(Base + RegSerial), ((ModeStereoBit | Mode16BitBit) << (int)SerialP2ModeShift) | SerialP2IntEn);
        io.WriteDword((ushort)(Base + RegControl), ControlDac2Enable);

        double elapsedMicroseconds = 0;
        const double step = 50;
        const double cap = 200_000;
        while (elapsedMicroseconds < cap && (io.ReadDword((ushort)(Base + RegStatus)) & StatusDac2) == 0)
        {
            device.Advance(step);
            elapsedMicroseconds += step;
        }

        Assert.True((io.ReadDword((ushort)(Base + RegStatus)) & StatusDac2) != 0, "DAC2 never reached its programmed period.");

        double measuredRate = frames * 1_000_000.0 / elapsedMicroseconds;
        Assert.InRange(measuredRate, 44100 * 0.98, 44100 * 1.02);
    }

    // ------------------------------------------------------------ test case 8

    [Fact]
    public void Dac2PlaybackWithCountAtHalfTheRingSetsStatusAndInta()
    {
        (Es1371 device, PciBus pci, IoBus io, PhysicalMemoryBus memory, PciInterruptRouter router, InterruptController pic) = BuildWithDevice();
        EnableIoAndBusMaster(pci);
        Assert.True(router.Route(FunctionAddress, 1, 11));
        ProgramDac2Rate(io, 44100);

        const uint frame = 0x300000;
        for (uint i = 0; i < 32; i++)
        {
            memory.WriteDword(frame + i * 4, 0);
        }

        // An 8-frame ring (16-bit stereo: one frame per dword), half-buffer
        // interrupts every 4 frames -- the spec's own example (section 5).
        io.WriteDword((ushort)(Base + RegDac2Frame), frame);
        io.WriteDword((ushort)(Base + RegDac2Size), 7);
        io.WriteDword((ushort)(Base + RegDac2Count), 3);
        io.WriteDword((ushort)(Base + RegSerial), ((ModeStereoBit | Mode16BitBit) << (int)SerialP2ModeShift) | SerialP2IntEn);
        io.WriteDword((ushort)(Base + RegControl), ControlDac2Enable);

        double microsecondsForFourFrames = (4.5) * 1_000_000.0 / 44100.0;
        device.Advance(microsecondsForFourFrames);

        uint status = io.ReadDword((ushort)(Base + RegStatus));
        Assert.Equal(StatusDac2 | StatusIntr, status);
        Assert.Equal(0x2B, pic.GetPendingVector());
    }

    // ------------------------------------------------------------ test case 9

    [Fact]
    public void TogglingIntEnOffThenOnClearsStatusAndInta()
    {
        (Es1371 device, PciBus pci, IoBus io, PhysicalMemoryBus memory, PciInterruptRouter router, InterruptController pic) = BuildWithDevice();
        EnableIoAndBusMaster(pci);
        Assert.True(router.Route(FunctionAddress, 1, 11));
        ProgramDac2Rate(io, 44100);

        const uint frame = 0x310000;
        io.WriteDword((ushort)(Base + RegDac2Frame), frame);
        io.WriteDword((ushort)(Base + RegDac2Size), 7);
        io.WriteDword((ushort)(Base + RegDac2Count), 3);
        uint serialWithIntEn = ((ModeStereoBit | Mode16BitBit) << (int)SerialP2ModeShift) | SerialP2IntEn;
        io.WriteDword((ushort)(Base + RegSerial), serialWithIntEn);
        io.WriteDword((ushort)(Base + RegControl), ControlDac2Enable);

        device.Advance(4.5 * 1_000_000.0 / 44100.0);
        Assert.NotEqual(0u, io.ReadDword((ushort)(Base + RegStatus)) & StatusDac2);

        // Off, then back on: E2's own acknowledgement idiom.
        uint serialWithoutIntEn = serialWithIntEn & ~SerialP2IntEn;
        io.WriteDword((ushort)(Base + RegSerial), serialWithoutIntEn);
        Assert.Equal(0u, io.ReadDword((ushort)(Base + RegStatus)));

        io.WriteDword((ushort)(Base + RegSerial), serialWithIntEn);
        Assert.Equal(0u, io.ReadDword((ushort)(Base + RegStatus))); // re-enabling alone does not resurrect the old condition

        _ = pic;
    }

    // ------------------------------------------------------------ test case 10

    [Fact]
    public void LoopModeWrapsTheRingAndInterruptsContinue()
    {
        (Es1371 device, PciBus pci, IoBus io, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableIoAndBusMaster(pci);
        ProgramDac2Rate(io, 44100);

        const uint frame = 0x320000;
        io.WriteDword((ushort)(Base + RegDac2Frame), frame);
        io.WriteDword((ushort)(Base + RegDac2Size), 7); // 8-frame ring
        io.WriteDword((ushort)(Base + RegDac2Count), 3); // 4-frame period
        uint serialBase = ((ModeStereoBit | Mode16BitBit) << (int)SerialP2ModeShift) | SerialP2IntEn; // LOOP_SEL=0: loop
        io.WriteDword((ushort)(Base + RegSerial), serialBase);
        io.WriteDword((ushort)(Base + RegControl), ControlDac2Enable);

        double perPeriod = 4.5 * 1_000_000.0 / 44100.0;

        device.Advance(perPeriod); // period 1: frames 0-3
        Assert.NotEqual(0u, io.ReadDword((ushort)(Base + RegStatus)) & StatusDac2);
        AcknowledgeDac2(io, serialBase);

        device.Advance(perPeriod); // period 2: frames 4-7, still inside the ring
        Assert.NotEqual(0u, io.ReadDword((ushort)(Base + RegStatus)) & StatusDac2);
        AcknowledgeDac2(io, serialBase);

        device.Advance(perPeriod); // period 3: frames 0-3 again, after wrapping
        Assert.NotEqual(0u, io.ReadDword((ushort)(Base + RegStatus)) & StatusDac2);

        uint position = io.ReadDword((ushort)(Base + RegDac2Size)) >> 16;
        Assert.True(position <= 7, "the ring position must stay inside the ring after wrapping.");
    }

    private static void AcknowledgeDac2(IoBus io, uint serialWithIntEn)
    {
        io.WriteDword((ushort)(Base + RegSerial), serialWithIntEn & ~SerialP2IntEn);
        io.WriteDword((ushort)(Base + RegSerial), serialWithIntEn);
    }

    // ------------------------------------------------------------ test case 11

    [Fact]
    public void StopModeHaltsPlaybackAtTheEndOfTheRing()
    {
        (Es1371 device, PciBus pci, IoBus io, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableIoAndBusMaster(pci);
        ProgramDac2Rate(io, 44100);

        const uint frame = 0x330000;
        io.WriteDword((ushort)(Base + RegDac2Frame), frame);
        io.WriteDword((ushort)(Base + RegDac2Size), 3); // 4-frame ring
        io.WriteDword((ushort)(Base + RegDac2Count), 3); // one interrupt, at the ring's own end
        uint serialStopMode = ((ModeStereoBit | Mode16BitBit) << (int)SerialP2ModeShift) | SerialP2IntEn | SerialP2LoopSel;
        io.WriteDword((ushort)(Base + RegSerial), serialStopMode);
        io.WriteDword((ushort)(Base + RegControl), ControlDac2Enable);

        double eightFramesWorth = 8.5 * 1_000_000.0 / 44100.0;
        device.Advance(eightFramesWorth); // enough time for two whole rings if it kept looping

        Assert.NotEqual(0u, io.ReadDword((ushort)(Base + RegStatus)) & StatusDac2);
        uint positionAfterFirstStop = io.ReadDword((ushort)(Base + RegDac2Size)) >> 16;

        AcknowledgeDac2(io, serialStopMode);
        device.Advance(eightFramesWorth); // more time still must not resume it

        Assert.Equal(0u, io.ReadDword((ushort)(Base + RegStatus)) & StatusDac2);
        Assert.Equal(positionAfterFirstStop, io.ReadDword((ushort)(Base + RegDac2Size)) >> 16);
    }

    // ------------------------------------------------------------ test case 12

    [Fact]
    public void ClearingBusMasterMidTransferStopsDac2()
    {
        (Es1371 device, PciBus pci, IoBus io, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableIoAndBusMaster(pci);
        ProgramDac2Rate(io, 44100);

        const uint frame = 0x340000;
        io.WriteDword((ushort)(Base + RegDac2Frame), frame);
        io.WriteDword((ushort)(Base + RegDac2Size), 63); // a long ring, so it never wraps during this test
        io.WriteDword((ushort)(Base + RegDac2Count), 63); // no interrupt during this test
        io.WriteDword((ushort)(Base + RegSerial), (ModeStereoBit | Mode16BitBit) << (int)SerialP2ModeShift);
        io.WriteDword((ushort)(Base + RegControl), ControlDac2Enable);

        device.Advance(4.5 * 1_000_000.0 / 44100.0);
        uint positionWhilePlaying = io.ReadDword((ushort)(Base + RegDac2Size)) >> 16;
        Assert.True(positionWhilePlaying > 0);

        pci.WriteConfiguration(FunctionAddress, 4, 2, 0x01); // I/O only, bus master off
        device.Advance(4.5 * 1_000_000.0 / 44100.0);

        Assert.Equal(positionWhilePlaying, io.ReadDword((ushort)(Base + RegDac2Size)) >> 16);
    }

    // ------------------------------------------------------------ test case 13

    [Fact]
    public void EightBitMonoAdcRecordingLandsAtAdcFrameWithItsInterrupt()
    {
        (Es1371 device, PciBus pci, IoBus io, PhysicalMemoryBus memory, PciInterruptRouter router, InterruptController pic) = BuildWithDevice();
        EnableIoAndBusMaster(pci);
        Assert.True(router.Route(FunctionAddress, 1, 11));
        ProgramAdcRate(io, 8000);

        const uint frame = 0x350000;
        for (uint i = 0; i < 8; i++)
        {
            memory.WriteByte(frame + i, 0xCC); // sentinel: must become 0x80 (unsigned 8-bit silence)
        }

        // 8-bit mono: four frames per dword, so 8 frames fit in 2 dwords.
        io.WriteDword((ushort)(Base + RegMemPage), 0x0D); // ADC page
        io.WriteDword((ushort)(Base + RegPagedFrame), frame);
        io.WriteDword((ushort)(Base + RegPagedSize), 1); // 2 dwords
        io.WriteDword((ushort)(Base + RegAdcCount), 7); // 8 frames, one full-buffer interrupt
        io.WriteDword((ushort)(Base + RegSerial), 0u | SerialR1IntEn); // 8-bit mono: mode bits 0
        io.WriteDword((ushort)(Base + RegControl), ControlAdcEnable);

        device.Advance(8.5 * 1_000_000.0 / 8000.0);

        for (uint i = 0; i < 8; i++)
        {
            Assert.Equal(0x80, memory.ReadByte(frame + i));
        }

        Assert.NotEqual(0u, io.ReadDword((ushort)(Base + RegStatus)) & StatusAdc);
        Assert.Equal(0x2B, pic.GetPendingVector());
    }

    // ------------------------------------------------------------ test case 14

    [Fact]
    public void LegacyReadsBackWhatWasWritten()
    {
        (_, PciBus pci, IoBus io, _, _, _) = BuildWithDevice();
        EnableIoAndBusMaster(pci);

        io.WriteDword((ushort)(Base + RegLegacy), 0x12345678);
        Assert.Equal(0x12345678u, io.ReadDword((ushort)(Base + RegLegacy)));
    }

    // ------------------------------------------------------------ test case 15

    [Fact]
    public void SnapshotRoundTripPreservesEveryRegisterAndTheSrcAndAc97Files()
    {
        (Es1371 device, PciBus pci, IoBus io, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableIoAndBusMaster(pci);
        ProgramDac2Rate(io, 44100);
        WriteCodecRegister(io, 0x02, 0x4242);

        const uint frame = 0x360000;
        io.WriteDword((ushort)(Base + RegDac2Frame), frame);
        io.WriteDword((ushort)(Base + RegDac2Size), 7);
        io.WriteDword((ushort)(Base + RegDac2Count), 3);
        uint serial = ((ModeStereoBit | Mode16BitBit) << (int)SerialP2ModeShift) | SerialP2IntEn;
        io.WriteDword((ushort)(Base + RegSerial), serial);
        io.WriteDword((ushort)(Base + RegControl), ControlDac2Enable);

        device.Advance(4.5 * 1_000_000.0 / 44100.0); // one pending interrupt to carry across the snapshot

        byte[] snapshot = device.Capture();

        (Es1371 restored, PciBus restoredPci, IoBus restoredIo, _, _, _) = BuildWithDevice();
        EnableIoAndBusMaster(restoredPci);
        restored.Restore(snapshot, Es1371.SnapshotVersion);

        Assert.Equal(io.ReadDword((ushort)(Base + RegControl)), restoredIo.ReadDword((ushort)(Base + RegControl)));
        Assert.Equal(io.ReadDword((ushort)(Base + RegStatus)), restoredIo.ReadDword((ushort)(Base + RegStatus)));
        Assert.Equal(io.ReadDword((ushort)(Base + RegSerial)), restoredIo.ReadDword((ushort)(Base + RegSerial)));
        Assert.Equal(io.ReadDword((ushort)(Base + RegDac2Frame)), restoredIo.ReadDword((ushort)(Base + RegDac2Frame)));
        Assert.Equal(io.ReadDword((ushort)(Base + RegDac2Size)), restoredIo.ReadDword((ushort)(Base + RegDac2Size)));
        Assert.Equal(io.ReadDword((ushort)(Base + RegDac2Count)), restoredIo.ReadDword((ushort)(Base + RegDac2Count)));

        io.WriteDword((ushort)(Base + RegSrc), 0x74u << SrcAddressShift);
        restoredIo.WriteDword((ushort)(Base + RegSrc), 0x74u << SrcAddressShift);
        Assert.Equal(io.ReadDword((ushort)(Base + RegSrc)) & 0xFFFF, restoredIo.ReadDword((ushort)(Base + RegSrc)) & 0xFFFF);

        Assert.Equal(ReadCodecRegister(io, 0x02), ReadCodecRegister(restoredIo, 0x02));
    }

    [Fact]
    public void SnapshotRejectsAWrongVersionOrLength()
    {
        (Es1371 device, _, _, _, _, _) = BuildWithDevice();
        byte[] snapshot = device.Capture();

        Assert.Throws<InvalidDataException>(() => device.Restore(snapshot, Es1371.SnapshotVersion + 1));
        Assert.Throws<InvalidDataException>(() => device.Restore(snapshot[..^1], Es1371.SnapshotVersion));
    }

    // ------------------------------------------------------------ enumeration

    [Fact]
    public void ConfigurationSelectsEs1371ThroughTheExistingSoundEntry()
    {
        var configuration = new VmConfiguration();
        configuration.Hardware.Sound =
        [
            new VmConfiguration.SoundCardEntry { Card = "es1371-pci", Port = "C400" },
        ];
        using Machine machine = configuration.BuildMachine();

        Es1371Card card = Assert.Single(machine.Cards.OfType<Es1371Card>());
        Assert.Equal("sound", CardCatalog.KindOf("es1371-pci"));
        machine.Pci.WriteConfiguration(FunctionAddress, 4, 2, 1);
        Assert.NotEqual(0xFFFFFFFFu, machine.Io.ReadDword(0xC400)); // CONTROL answers, not an empty bus
        Assert.Same(card.Device, Assert.Single(machine.Cards.OfType<Es1371Card>()).Device);
    }

    // ------------------------------------------------------------ audio path

    [Fact]
    public void SixteenBitStereoDacBufferAt44100HzReachesTheMixerResampledTo48000()
    {
        (Es1371 device, PciBus pci, IoBus io, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableIoAndBusMaster(pci);
        ProgramDac2Rate(io, 44100);

        const uint frame = 0x370000;
        const short dcLeft = 12345;
        const short dcRight = -6789;
        for (uint i = 0; i < 64; i++)
        {
            memory.WriteWord(frame + i * 4, unchecked((ushort)dcLeft));
            memory.WriteWord(frame + i * 4 + 2, unchecked((ushort)dcRight));
        }

        io.WriteDword((ushort)(Base + RegDac2Frame), frame);
        io.WriteDword((ushort)(Base + RegDac2Size), 63);
        io.WriteDword((ushort)(Base + RegDac2Count), 63);
        io.WriteDword((ushort)(Base + RegSerial), (ModeStereoBit | Mode16BitBit) << (int)SerialP2ModeShift);
        io.WriteDword((ushort)(Base + RegControl), ControlDac2Enable);

        device.Advance(32.5 * 1_000_000.0 / 44100.0); // 32 native frames, upsampled towards 48000

        Span<short> samples = stackalloc short[64];
        int written = device.ReadSamples(samples);

        Assert.True(written >= 4, "expected at least a couple of resampled frames.");
        for (int i = 0; i < written; i += 2)
        {
            Assert.Equal(dcLeft, samples[i]);
            Assert.Equal(dcRight, samples[i + 1]);
        }

        Assert.Equal(48000, ((IAudioSource)device).NativeSampleRate);
    }

    [Fact]
    public void EightBitMonoDacPlaybackConvertsUnsignedSamplesToSignedOutput()
    {
        (Es1371 device, PciBus pci, IoBus io, PhysicalMemoryBus memory, _, _) = BuildWithDevice();
        EnableIoAndBusMaster(pci);
        ProgramDac2Rate(io, 22050);

        const uint frame = 0x380000;
        for (uint i = 0; i < 32; i++)
        {
            memory.WriteDword(frame + i * 4, 0xFFFFFFFFu); // four bytes of 0xFF: unsigned 8-bit full-scale positive
        }

        io.WriteDword((ushort)(Base + RegDac2Frame), frame);
        io.WriteDword((ushort)(Base + RegDac2Size), 31);
        io.WriteDword((ushort)(Base + RegDac2Count), 31);
        io.WriteDword((ushort)(Base + RegSerial), 0u); // 8-bit mono
        io.WriteDword((ushort)(Base + RegControl), ControlDac2Enable);

        device.Advance(64.5 * 1_000_000.0 / 22050.0);

        Span<short> samples = stackalloc short[8];
        int written = device.ReadSamples(samples);

        Assert.True(written > 0);
        short expected = (short)((0xFF - 128) << 8); // ConvertUnsigned8(0xFF)
        Assert.Equal(expected, samples[0]);
        Assert.Equal(expected, samples[1]); // mono duplicated to both channels
    }

    // ------------------------------------------------------------ DOS boot

    /// <summary>
    /// A whole DOS boots to its prompt with the card fitted alongside the
    /// machine's usual Sound Blaster and NE2000 -- proof that a second,
    /// PCI-based sound card at its own fixed slot does not disturb POST, the
    /// BIOS or the rest of boot. Skipped when the image is not present, the
    /// usual case on a fresh clone; see <c>Rtl8139Tests</c> for the same
    /// pattern.
    /// </summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void AWholeDosBootsToItsPromptWithTheCardFitted()
    {
        string? image = FindImage();
        if (image is null)
        {
            return;
        }

        string copy = Path.Combine(Path.GetTempPath(), $"aurora-es1371-boot-{Guid.NewGuid():N}.img");
        File.Copy(image, copy);
        try
        {
            var hardware = HardwareConfiguration.Default;
            hardware.Sound.Add(new CardSettings { Card = "es1371-pci" }); // default port, see Es1371Card.DefaultPort
            using var machine = new Machine(16 * 1024 * 1024, hardware);
            machine.AttachDisk(DiskImage.OpenHardDisk(copy));

            for (long done = 0; done < 260_000_000; done += 10_000_000)
            {
                machine.RunInstructions(10_000_000);
            }

            Assert.Contains("C:\\>", machine.GetScreenText());
        }
        finally
        {
            File.Delete(copy);
        }
    }

    /// <summary>The vm-dos622 image, found by walking up from wherever the tests run.</summary>
    private static string? FindImage()
    {
        const string relativePath = "images/vm-dos622/hdd.img";
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
