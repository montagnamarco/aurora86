using Aurora86.Core;
using Aurora86.Core.Configuration;
using Aurora86.Core.Cpu;
using Aurora86.Core.Devices.Video;
using static Aurora86.Core.Devices.Video.AuroraGpuInterface;

namespace Aurora86.Tests;

/// <summary>
/// The Aurora Display Adapter as a driver and a boot loader see it
/// (docs/AURORA-GPU.md): PCI configuration space, the registers in BAR1, the
/// command ring in guest memory, and the VBE modes through INT 10h executed
/// by the real CPU.
/// </summary>
public class AuroraGpuTests
{
    private const ushort VideoFunction = 0x0020;
    private const int PciCommandRegister = 0x04;
    private const uint PciCommandIoMemoryAndBusMaster = 0x0007;
    private const int Bar0 = 0x10;
    private const int Bar1 = 0x14;

    /// <summary>
    /// Where the tests keep the command ring and their source images in guest
    /// RAM: below one megabyte, because after POST the A20 gate is closed and
    /// a CPU store to 100000h lands at 0, while the card's bus-master reads
    /// ignore A20 and would find the real 100000h empty.
    /// </summary>
    private const uint RingAddress = 0x00080000;
    private const uint RingBytes = 0x1000;
    private const uint ImageAddress = 0x00090000;

    /// <summary>ES:DI of every VBE call below: linear 50000h.</summary>
    private const uint VbeBuffer = 0x50000;

    private const uint OpaqueRed = 0xFFFF0000;
    private const uint OpaqueGreen = 0xFF00FF00;
    private const uint OpaqueBlue = 0xFF0000FF;

    // ---- PCI identity -------------------------------------------------------------

    [Fact]
    public void TheFunctionIdentifiesAsAVgaCompatibleDisplayWithInterruptA()
    {
        using Machine machine = Started();

        Assert.Equal(VendorId, (ushort)machine.Pci.ReadConfiguration(VideoFunction, 0x00, 2));
        Assert.Equal(DeviceId, (ushort)machine.Pci.ReadConfiguration(VideoFunction, 0x02, 2));
        Assert.Equal(Revision, (byte)machine.Pci.ReadConfiguration(VideoFunction, 0x08, 1));
        Assert.Equal(ClassCode, machine.Pci.ReadConfiguration(VideoFunction, 0x08, 4) >> 8);
        Assert.Equal(1u, machine.Pci.ReadConfiguration(VideoFunction, 0x3D, 1));
    }

    [Fact]
    public void SizingTheBarsReportsThirtyTwoMegabytesOfVideoMemoryAndSixtyFourKilobytesOfRegisters()
    {
        using Machine machine = Started();

        machine.Pci.WriteConfiguration(VideoFunction, Bar0, 4, 0xFFFFFFFF);
        machine.Pci.WriteConfiguration(VideoFunction, Bar1, 4, 0xFFFFFFFF);
        uint videoMemoryProbe = machine.Pci.ReadConfiguration(VideoFunction, Bar0, 4);
        uint registerProbe = machine.Pci.ReadConfiguration(VideoFunction, Bar1, 4);

        Assert.Equal(~(VideoMemorySize - 1), videoMemoryProbe & 0xFFFFFFF0);
        Assert.Equal(0x08u, videoMemoryProbe & 0x08);                     // prefetchable
        Assert.Equal(~(RegisterWindowSize - 1), registerProbe & 0xFFFFFFF0);
    }

    [Fact]
    public void AfterPostTheRegistersAnswerThroughBarOne()
    {
        using Machine machine = Started();

        Assert.Equal(RegisterWindowResetBase, machine.AuroraGpu!.RegisterBase);
        Assert.Equal(IdValue, ReadRegister(machine, IdRegister));
        Assert.Equal(VersionValue, ReadRegister(machine, VersionRegister));
        Assert.Equal(VideoMemorySize, ReadRegister(machine, VideoMemorySizeRegister));
        Assert.Equal(0x0Fu, ReadRegister(machine, CapabilitiesRegister));
    }

    // ---- VBE ------------------------------------------------------------------------

    [Fact]
    public void TheControllerInformationListsTheFullHdModeAndThirtyTwoMegabytes()
    {
        using Machine machine = Started();

        Int10(machine, 0x4F00);

        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        Assert.Equal((byte)'V', machine.Memory.ReadByte(VbeBuffer));
        Assert.Equal(512, machine.Memory.ReadWord(VbeBuffer + 18));       // 64 KB units

        uint listOffset = machine.Memory.ReadWord(VbeBuffer + 14);
        uint listSegment = machine.Memory.ReadWord(VbeBuffer + 16);
        var modes = new List<ushort>();
        for (uint at = listSegment * 16 + listOffset; ; at += 2)
        {
            ushort mode = machine.Memory.ReadWord(at);
            if (mode == 0xFFFF)
            {
                break;
            }

            modes.Add(mode);
        }

        Assert.Contains((ushort)0x187, modes);
        Assert.Contains((ushort)0x188, modes);
        Assert.Equal(9, modes.Count);
    }

    [Fact]
    public void SettingTheSixteenByTenModeGivesA1280By800Screen()
    {
        using Machine machine = Started();

        Int10(machine, 0x4F01, cx: 0x188);
        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        Assert.Equal(1280 * 4, machine.Memory.ReadWord(VbeBuffer + 16));
        Assert.Equal(1280, machine.Memory.ReadWord(VbeBuffer + 18));
        Assert.Equal(800, machine.Memory.ReadWord(VbeBuffer + 20));

        Int10(machine, 0x4F02, bx: 0x4188);
        machine.Display.Render();

        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        Assert.Equal(1280, machine.Display.Width);
        Assert.Equal(800, machine.Display.Height);
    }

    [Fact]
    public void TheModeInformationDescribesALinearThirtyTwoBitFullHdFrame()
    {
        using Machine machine = Started();

        Int10(machine, 0x4F01, cx: 0x187);

        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        Assert.Equal(0x00FB, machine.Memory.ReadWord(VbeBuffer + 0));    // linear only, no window
        Assert.Equal(1920 * 4, machine.Memory.ReadWord(VbeBuffer + 16));
        Assert.Equal(1920, machine.Memory.ReadWord(VbeBuffer + 18));
        Assert.Equal(1080, machine.Memory.ReadWord(VbeBuffer + 20));
        Assert.Equal(32, machine.Memory.ReadByte(VbeBuffer + 25));
        Assert.Equal(6, machine.Memory.ReadByte(VbeBuffer + 27));         // direct colour
        Assert.Equal(16, machine.Memory.ReadByte(VbeBuffer + 32));        // red position
        Assert.Equal(0, machine.Memory.ReadByte(VbeBuffer + 36));         // blue position
        Assert.Equal(VideoMemoryResetBase, machine.Memory.ReadDword(VbeBuffer + 40));
    }

    [Fact]
    public void SettingTheFullHdModeShowsVideoMemoryWithRedAndBlueInTheFrontEndOrder()
    {
        using Machine machine = Started();

        Int10(machine, 0x4F02, bx: 0x4187);
        machine.Memory.WriteDword(machine.LinearFrameBufferBase, OpaqueRed);
        machine.Memory.WriteDword(machine.LinearFrameBufferBase + 4, OpaqueBlue);
        machine.Display.Render();

        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        Assert.Equal(1920, machine.Display.Width);
        Assert.Equal(1080, machine.Display.Height);
        Assert.Equal(0xFF0000FFu, machine.Display.Framebuffer[0]);        // red in the low byte
        Assert.Equal(0xFFFF0000u, machine.Display.Framebuffer[1]);

        Int10(machine, 0x4F03);
        Assert.Equal(0x4187, machine.Cpu.State.Bx);
    }

    [Fact]
    public void AVgaModeSetGivesTheScreenBackToTheVga()
    {
        using Machine machine = Started();
        Int10(machine, 0x4F02, bx: 0x4187);

        Int10(machine, 0x0003);
        machine.Display.Render();

        Assert.Equal(0u, ReadRegister(machine, DisplayControlRegister));
        Assert.NotEqual(1920, machine.Display.Width);
    }

    // ---- display registers ------------------------------------------------------------

    [Fact]
    public void ADriverChangesResolutionThroughTheDisplayRegisters()
    {
        using Machine machine = Started();

        SetGeometry(machine, 800, 600, 800 * 4, 0);
        machine.Display.Render();

        Assert.Equal(DisplayStatusActive, ReadRegister(machine, DisplayStatusRegister));
        Assert.Equal(800, machine.Display.Width);
        Assert.Equal(600, machine.Display.Height);
    }

    [Fact]
    public void APitchNarrowerThanTheLineShowsBlackAndClearsTheStatus()
    {
        using Machine machine = Started();

        SetGeometry(machine, 800, 600, 799 * 4, 0);
        machine.Display.Render();

        Assert.Equal(0u, ReadRegister(machine, DisplayStatusRegister));
        Assert.Equal(0xFF000000u, machine.Display.Framebuffer[0]);
    }

    // ---- the command ring --------------------------------------------------------------

    [Fact]
    public void AFillFollowedByAFenceDrawsCompletesAndRaisesTheInterrupt()
    {
        using Machine machine = StartedWithRing(out CommandRing ring);
        SetGeometry(machine, 64, 64, 64 * 4, 0);
        WriteRegister(machine, InterruptMaskRegister, InterruptFence);

        ring.Add(OpcodeFillRect, 0, 0, 2, 3, 4, 5, OpaqueGreen);
        ring.Add(OpcodeFence, FlagFenceInterrupt, 0x1234);
        ring.Submit();

        Assert.Equal(OpaqueGreen, Pixel(machine, 2, 3));
        Assert.Equal(OpaqueGreen, Pixel(machine, 5, 7));
        Assert.Equal(0u, Pixel(machine, 6, 7));
        Assert.Equal(0x1234u, ReadRegister(machine, FenceRegister));
        Assert.Equal(InterruptFence, ReadRegister(machine, InterruptStatusRegister));
        Assert.True(machine.AuroraGpu!.InterruptAsserted);

        WriteRegister(machine, InterruptStatusRegister, InterruptFence);   // write one to clear
        Assert.False(machine.AuroraGpu.InterruptAsserted);
    }

    [Fact]
    public void ACopyWithinTheSameSurfaceScrollsAsIfTheSourceWereReadFirst()
    {
        using Machine machine = StartedWithRing(out CommandRing ring);
        SetGeometry(machine, 8, 8, 8 * 4, 0);
        for (uint row = 0; row < 8; row++)
        {
            ring.Add(OpcodeFillRect, 0, 0, 0, row, 8, 1, 0xFF000000 | row);
        }

        // Scroll everything up by one line: rows 1-7 onto rows 0-6.
        ring.Add(OpcodeCopyRect, 0, 0, 0, 1, 0, 0, 0, 8, 7);
        ring.Submit();

        for (int row = 0; row < 7; row++)
        {
            Assert.Equal(0xFF000000 | (uint)(row + 1), Pixel(machine, 3, row));
        }

        Assert.Equal(0xFF000007u, Pixel(machine, 3, 7));
    }

    [Fact]
    public void ABlendedImageIsComposedSourceOverWithPremultipliedAlpha()
    {
        using Machine machine = StartedWithRing(out CommandRing ring);
        SetGeometry(machine, 4, 4, 4 * 4, 0);

        // Half-transparent red, premultiplied: alpha 80h, red 80h.
        const uint HalfRed = 0x80800000;
        machine.Memory.WriteDword(ImageAddress, HalfRed);

        ring.Add(OpcodeFillRect, 0, 0, 0, 0, 4, 4, OpaqueBlue);
        ring.Add(OpcodeBlitImage, FlagBlend, SourceSpaceGuestMemory, ImageAddress, 4, 0, 1, 1, 1, 1);
        ring.Submit();

        // red 80h + 0; blue 0 + FFh x 7Fh / FFh = 7Fh; alpha 80h + 7Fh = FFh.
        Assert.Equal(0xFF80007Fu, Pixel(machine, 1, 1));
        Assert.Equal(OpaqueBlue, Pixel(machine, 0, 0));
    }

    [Fact]
    public void AMaskScalesTheColourByEachPixelsCoverage()
    {
        using Machine machine = StartedWithRing(out CommandRing ring);
        SetGeometry(machine, 4, 1, 4 * 4, 0);
        machine.Memory.WriteByte(ImageAddress + 0, 0);
        machine.Memory.WriteByte(ImageAddress + 1, 255);
        machine.Memory.WriteByte(ImageAddress + 2, 128);

        ring.Add(OpcodeFillRect, 0, 0, 0, 0, 4, 1, 0xFF000000);
        ring.Add(OpcodeDrawMask, 0, SourceSpaceGuestMemory, ImageAddress, 4, 0, 0, 0, 3, 1, 0xFFFFFFFF);
        ring.Submit();

        Assert.Equal(0xFF000000u, Pixel(machine, 0, 0));   // no coverage: untouched
        Assert.Equal(0xFFFFFFFFu, Pixel(machine, 1, 0));   // full coverage: the colour
        Assert.Equal(0xFF808080u, Pixel(machine, 2, 0));   // half: FFh x 128 / 255 = 80h over black
    }

    [Fact]
    public void TheClipRectangleLimitsEveryDrawingCommand()
    {
        using Machine machine = StartedWithRing(out CommandRing ring);
        SetGeometry(machine, 8, 8, 8 * 4, 0);

        ring.Add(OpcodeSetClip, FlagClipEnabled, 2, 2, 2, 2);
        ring.Add(OpcodeFillRect, 0, 0, 0, 0, 8, 8, OpaqueRed);
        ring.Submit();

        Assert.Equal(OpaqueRed, Pixel(machine, 2, 2));
        Assert.Equal(OpaqueRed, Pixel(machine, 3, 3));
        Assert.Equal(0u, Pixel(machine, 1, 2));
        Assert.Equal(0u, Pixel(machine, 4, 3));
    }

    [Fact]
    public void ALineIncludesBothEndPoints()
    {
        using Machine machine = StartedWithRing(out CommandRing ring);
        SetGeometry(machine, 8, 8, 8 * 4, 0);

        ring.Add(OpcodeDrawLine, 0, 0, 1, 1, 5, 4, OpaqueRed);
        ring.Submit();

        Assert.Equal(OpaqueRed, Pixel(machine, 1, 1));
        Assert.Equal(OpaqueRed, Pixel(machine, 5, 4));
        Assert.Equal(0u, Pixel(machine, 6, 4));
    }

    [Fact]
    public void ALineEndPointBeyondTheCoordinateRangeIsAnError()
    {
        using Machine machine = StartedWithRing(out CommandRing ring);
        SetGeometry(machine, 8, 8, 8 * 4, 0);

        ring.Add(OpcodeDrawLine, 0, 0, 0, 0, unchecked((uint)int.MinValue), 0, OpaqueRed);
        ring.Submit();

        Assert.Equal(ErrorBadCoordinate, ReadRegister(machine, ErrorRegister));
    }

    [Fact]
    public void AnUnknownOpcodeStopsTheRingBeforeItUntilTheDriverResetsIt()
    {
        using Machine machine = StartedWithRing(out CommandRing ring);
        SetGeometry(machine, 4, 4, 4 * 4, 0);

        ring.Add(OpcodeFillRect, 0, 0, 0, 0, 1, 1, OpaqueRed);
        uint badOffset = ring.Tail;
        ring.Add(0x7777);
        ring.Add(OpcodeFillRect, 0, 0, 1, 0, 1, 1, OpaqueRed);
        ring.Submit();

        Assert.Equal(OpaqueRed, Pixel(machine, 0, 0));
        Assert.Equal(0u, Pixel(machine, 1, 0));                          // after the error: not run
        Assert.Equal(ErrorUnknownOpcode, ReadRegister(machine, ErrorRegister));
        Assert.Equal(badOffset, ReadRegister(machine, ErrorOffsetRegister));
        Assert.Equal(badOffset, ReadRegister(machine, RingHeadRegister));
        Assert.Equal(InterruptCommandError, ReadRegister(machine, InterruptStatusRegister));

        WriteRegister(machine, RingControlRegister, RingControlEnable | RingControlReset);
        ring.Restart();
        ring.Add(OpcodeFillRect, 0, 0, 1, 0, 1, 1, OpaqueRed);
        ring.Submit();

        Assert.Equal(ErrorNone, ReadRegister(machine, ErrorRegister));
        Assert.Equal(OpaqueRed, Pixel(machine, 1, 0));
    }

    [Fact]
    public void WithoutBusMasteringTheRingIsNotRead()
    {
        using Machine machine = StartedWithRing(out CommandRing ring);
        SetGeometry(machine, 4, 4, 4 * 4, 0);
        machine.Pci.WriteConfiguration(VideoFunction, PciCommandRegister, 2, 0x0003);

        ring.Add(OpcodeFillRect, 0, 0, 0, 0, 1, 1, OpaqueRed);
        ring.Submit();

        Assert.Equal(0u, Pixel(machine, 0, 0));
        Assert.Equal(0u, ReadRegister(machine, RingHeadRegister));
    }

    [Fact]
    public void ACommandMayWrapPastTheEndOfTheRing()
    {
        using Machine machine = StartedWithRing(out CommandRing ring);
        SetGeometry(machine, 4, 4, 4 * 4, 0);

        // Advance the head to twelve bytes before the end with NOPs, so the
        // eight-dword fill that follows crosses the wrap.
        int nops = (int)(RingBytes - 12) / 4;
        for (int index = 0; index < nops; index++)
        {
            ring.Add(OpcodeNop);
        }

        ring.Submit();
        Assert.Equal(RingBytes - 12, ReadRegister(machine, RingHeadRegister));

        ring.Add(OpcodeFillRect, 0, 0, 3, 3, 1, 1, OpaqueGreen);
        ring.Submit();

        Assert.Equal(ErrorNone, ReadRegister(machine, ErrorRegister));
        Assert.Equal(OpaqueGreen, Pixel(machine, 3, 3));
        Assert.Equal(20u, ReadRegister(machine, RingHeadRegister));
    }

    [Fact]
    public void AnOffScreenSurfaceIsDefinedFilledAndCopiedToTheScreen()
    {
        using Machine machine = StartedWithRing(out CommandRing ring);
        SetGeometry(machine, 8, 8, 8 * 4, 0);
        const uint SurfaceOffset = 0x100000;

        ring.Add(OpcodeDefineSurface, 1, SurfaceOffset, 16, 4, 4);
        ring.Add(OpcodeFillRect, 1, 0, 0, 0, 4, 4, OpaqueRed);
        ring.Add(OpcodeCopyRect, 0, 1, 0, 0, 0, 2, 2, 4, 4);
        ring.Submit();

        Assert.Equal(OpaqueRed, Pixel(machine, 2, 2));
        Assert.Equal(OpaqueRed, Pixel(machine, 5, 5));
        Assert.Equal(0u, Pixel(machine, 6, 6));
    }

    [Fact]
    public void ASurfaceReachingPastVideoMemoryIsRejected()
    {
        using Machine machine = StartedWithRing(out CommandRing ring);

        ring.Add(OpcodeDefineSurface, 1, VideoMemorySize - 16, 16, 4, 4);
        ring.Submit();

        Assert.Equal(ErrorBadSurfaceDefinition, ReadRegister(machine, ErrorRegister));
    }

    [Fact]
    public void TheCursorIsCompositedOnTheDisplayButNeverWrittenToVideoMemory()
    {
        using Machine machine = StartedWithRing(out CommandRing ring);
        SetGeometry(machine, 128, 128, 128 * 4, 0);
        machine.Memory.WriteDword(ImageAddress, OpaqueRed);               // the image's top-left pixel

        ring.Add(OpcodeCursorImage, SourceSpaceGuestMemory, ImageAddress, 0, 0);
        ring.Add(OpcodeCursorMove, FlagCursorVisible, 10, 20);
        ring.Submit();
        machine.Display.Render();

        Assert.Equal(0xFF0000FFu, machine.Display.Framebuffer[20 * 128 + 10]);
        Assert.Equal(0u, Pixel(machine, 10, 20));

        ring.Add(OpcodeCursorMove, 0, 10, 20);
        ring.Submit();
        machine.Display.Render();

        Assert.Equal(0xFF000000u, machine.Display.Framebuffer[20 * 128 + 10]);
    }

    [Fact]
    public void PresentFlipsTheDisplayedFrameToAnotherOffset()
    {
        using Machine machine = StartedWithRing(out CommandRing ring);
        const uint SecondFrame = 0x10000;
        SetGeometry(machine, 4, 4, 4 * 4, 0);
        machine.Memory.WriteDword(machine.LinearFrameBufferBase + SecondFrame, OpaqueGreen);

        ring.Add(OpcodePresent, FlagPresentSetOffset, SecondFrame);
        ring.Submit();
        machine.Display.Render();

        Assert.Equal(SecondFrame, ReadRegister(machine, DisplayOffsetRegister));
        Assert.Equal(0xFF00FF00u, machine.Display.Framebuffer[0]);
    }

    // ---- guest values that must not reach the emulator ----------------------------------

    /// <summary>
    /// A copy whose source origin is near the end of the 32-bit range clips
    /// to nothing. Computed in 32 bits, the width wrapped round and the copy
    /// sliced far outside video memory, which threw out of the doorbell write.
    /// </summary>
    [Fact]
    public void ACopyFromASourceNearTheEndOfTheIntegerRangeDrawsNothing()
    {
        using Machine machine = StartedWithRing(out CommandRing ring);
        SetGeometry(machine, 64, 64, 64 * 4, 0);

        ring.Add(OpcodeCopyRect, 0, 0, 0x80000001, 0, 0, 0, 0, 10, 10);
        ring.Add(OpcodeFence, 0, 1);
        ring.Submit();

        Assert.Equal(ErrorNone, ReadRegister(machine, ErrorRegister));
        Assert.Equal(1u, ReadRegister(machine, FenceRegister));
    }

    /// <summary>
    /// Scanout switched on by a driver before any geometry has a pitch of
    /// zero; the line-length and display-start calls refuse rather than
    /// divide by it.
    /// </summary>
    [Fact]
    public void TheLineLengthCallsRefuseAnEnabledScanoutWithNoGeometry()
    {
        using Machine machine = Started();
        WriteRegister(machine, DisplayControlRegister, DisplayControlScanoutEnable);

        Int10(machine, 0x4F06, bx: 0x0001);
        Assert.Equal(0x014F, machine.Cpu.State.Ax);

        Int10(machine, 0x4F07, bx: 0x0001);
        Assert.Equal(0x014F, machine.Cpu.State.Ax);
    }

    [Fact]
    public void TheCurrentModeFollowsAResolutionTheDriverSetItself()
    {
        using Machine machine = Started();
        Int10(machine, 0x4F02, bx: 0x4187);

        SetGeometry(machine, 800, 600, 800 * 4, 0);
        Int10(machine, 0x4F03);

        Assert.Equal(0x4181, machine.Cpu.State.Bx);
    }

    [Fact]
    public void TheMemoryMapReservesBothBars()
    {
        using Machine machine = Started();

        IReadOnlyList<(ulong Start, ulong Length, uint Type)> map = machine.MemoryMap();

        Assert.Contains(map, entry => entry.Start == VideoMemoryResetBase && entry.Length == VideoMemorySize && entry.Type == 2);
        Assert.Contains(map, entry => entry.Start == RegisterWindowResetBase && entry.Length == RegisterWindowSize && entry.Type == 2);
    }

    // ---- snapshots ----------------------------------------------------------------------

    [Fact]
    public void ASnapshotRestoresTheRegistersTheRingAndVideoMemory()
    {
        string path = Path.GetTempFileName();
        try
        {
            using (Machine source = StartedWithRing(out CommandRing ring))
            {
                SetGeometry(source, 16, 16, 16 * 4, 0);
                ring.Add(OpcodeFillRect, 0, 0, 1, 1, 2, 2, OpaqueRed);
                ring.Add(OpcodeFence, 0, 77);
                ring.Submit();
                MachineSnapshot.Capture(source).Save(path);
            }

            using Machine target = Started();
            MachineSnapshot.Load(path).RestoreTo(target);
            target.Display.Render();

            Assert.Equal(OpaqueRed, Pixel(target, 2, 2, 16));
            Assert.Equal(77u, ReadRegister(target, FenceRegister));
            Assert.Equal(16u, ReadRegister(target, DisplayWidthRegister));
            Assert.Equal(16, target.Display.Width);
            Assert.Equal(0xFF0000FFu, target.Display.Framebuffer[2 * 16 + 2]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ATrioMachineRefusesASnapshotTakenOnTheAuroraAdapter()
    {
        string path = Path.GetTempFileName();
        try
        {
            using (Machine source = Started())
            {
                MachineSnapshot.Capture(source).Save(path);
            }

            using var trio = new Machine(videoModel: VmVideoModel.S3Trio64VPlus);
            trio.RunInstructions(200);
            MachineSnapshot snapshot = MachineSnapshot.Load(path);

            Assert.Throws<InvalidDataException>(() => snapshot.RestoreTo(trio));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- helpers ----------------------------------------------------------------------

    private static Machine Started()
    {
        var machine = new Machine(videoModel: VmVideoModel.AuroraGpu);
        machine.RunInstructions(200);
        return machine;
    }

    /// <summary>A started machine with bus mastering on and an enabled 4 KB ring at 80000h.</summary>
    private static Machine StartedWithRing(out CommandRing ring)
    {
        Machine machine = Started();
        machine.Pci.WriteConfiguration(VideoFunction, PciCommandRegister, 2, PciCommandIoMemoryAndBusMaster);
        WriteRegister(machine, RingBaseRegister, RingAddress);
        WriteRegister(machine, RingSizeRegister, RingBytes);
        WriteRegister(machine, RingControlRegister, RingControlEnable);
        ring = new CommandRing(machine);
        return machine;
    }

    private static void SetGeometry(Machine machine, uint width, uint height, uint pitch, uint offset)
    {
        WriteRegister(machine, DisplayControlRegister, 0);
        WriteRegister(machine, DisplayWidthRegister, width);
        WriteRegister(machine, DisplayHeightRegister, height);
        WriteRegister(machine, DisplayPitchRegister, pitch);
        WriteRegister(machine, DisplayOffsetRegister, offset);
        WriteRegister(machine, DisplayControlRegister, DisplayControlScanoutEnable);
    }

    private static uint ReadRegister(Machine machine, uint register)
    {
        return machine.Memory.ReadDword(machine.AuroraGpu!.RegisterBase + register);
    }

    private static void WriteRegister(Machine machine, uint register, uint value)
    {
        machine.Memory.WriteDword(machine.AuroraGpu!.RegisterBase + register, value);
    }

    /// <summary>A pixel of the displayed frame, read through BAR0 as the CPU would.</summary>
    private static uint Pixel(Machine machine, int x, int y, int? width = null)
    {
        int pitch = (width ?? machine.AuroraGpu!.DisplayWidth) * 4;
        return machine.Memory.ReadDword(machine.LinearFrameBufferBase + (uint)(y * pitch + x * 4));
    }

    private static void Int10(Machine machine, ushort ax, ushort bx = 0, ushort cx = 0, ushort dx = 0)
    {
        machine.Cpu.State.Ax = ax;
        machine.Cpu.State.Bx = bx;
        machine.Cpu.State.Cx = cx;
        machine.Cpu.State.Dx = dx;
        machine.Cpu.SetSegment(SegReg.Es, (ushort)(VbeBuffer >> 4));
        machine.Cpu.State.Di = 0;
        Run(machine, [0xCD, 0x10]);
    }

    private static void Run(Machine machine, byte[] code)
    {
        code.CopyTo(machine.Memory.GetRamSpan(0x20100, code.Length));
        machine.Memory.WriteByte(0x20100 + (uint)code.Length, 0xF4);
        machine.Cpu.SetSegment(SegReg.Cs, 0x2000);
        machine.Cpu.State.Eip = 0x100;
        machine.Cpu.SetSegment(SegReg.Ds, 0);
        machine.Cpu.SetSegment(SegReg.Ss, 0x3000);
        machine.Cpu.State.Esp = 0xFFFE;
        machine.Cpu.State.Halted = false;
        for (int count = 0; count < 50_000 && !machine.Cpu.State.Halted; count++)
        {
            machine.RunInstructions(1);
        }

        Assert.True(machine.Cpu.State.Halted);
    }

    /// <summary>
    /// The driver's side of the ring: commands written at the tail in guest
    /// RAM, then the tail written to RING_TAIL.
    /// </summary>
    private sealed class CommandRing(Machine machine)
    {
        public uint Tail { get; private set; }

        public void Add(ushort opcode, params uint[] fields)
        {
            WriteDword(Header(opcode, fields.Length + 1));
            foreach (uint field in fields)
            {
                WriteDword(field);
            }
        }

        public void Submit()
        {
            WriteRegister(machine, RingTailRegister, Tail);
        }

        public void Restart()
        {
            Tail = 0;
        }

        private void WriteDword(uint value)
        {
            machine.Memory.WriteDword(RingAddress + Tail, value);
            Tail = (Tail + 4) % RingBytes;
        }
    }
}
