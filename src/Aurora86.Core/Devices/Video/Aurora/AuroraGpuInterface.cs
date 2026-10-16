namespace Aurora86.Core.Devices.Video;

/// <summary>
/// The numbers of the Aurora Display Adapter's guest interface, named as in
/// docs/AURORA-GPU.md, which is the contract a guest driver is written
/// against. Nothing here is period hardware: the adapter was designed for
/// Aurora86 and the Aurora operating system in 2026, so every value is a
/// decision recorded in that document rather than a measurement.
/// </summary>
public static class AuroraGpuInterface
{
    // ---- PCI identity ----------------------------------------------------------

    /// <summary>Vendor 1234h: the value QEMU and Bochs use for their own virtual devices.</summary>
    public const ushort VendorId = 0x1234;

    /// <summary>Device A386h, distinct from Bochs' 1111h so no Bochs driver binds to it.</summary>
    public const ushort DeviceId = 0xA386;

    public const byte Revision = 0x01;

    /// <summary>Display controller, VGA compatible.</summary>
    public const uint ClassCode = 0x030000;

    /// <summary>Video memory: 32 MB, room for a 1920x1080x32 frame and off-screen surfaces.</summary>
    public const uint VideoMemorySize = 32 * 1024 * 1024;

    /// <summary>The register window of BAR1. Registers use the first page; the rest is reserved.</summary>
    public const uint RegisterWindowSize = 64 * 1024;

    public const uint VideoMemoryResetBase = 0xE0000000;
    public const uint RegisterWindowResetBase = 0xE2000000;

    // ---- registers (BAR1 offsets) ------------------------------------------------

    public const uint IdRegister = 0x000;
    public const uint VersionRegister = 0x004;
    public const uint VideoMemorySizeRegister = 0x008;
    public const uint CapabilitiesRegister = 0x00C;
    public const uint DisplayControlRegister = 0x010;
    public const uint DisplayWidthRegister = 0x014;
    public const uint DisplayHeightRegister = 0x018;
    public const uint DisplayPitchRegister = 0x01C;
    public const uint DisplayOffsetRegister = 0x020;
    public const uint DisplayStatusRegister = 0x024;
    public const uint InterruptStatusRegister = 0x030;
    public const uint InterruptMaskRegister = 0x034;
    public const uint RingBaseRegister = 0x040;
    public const uint RingSizeRegister = 0x044;
    public const uint RingHeadRegister = 0x048;
    public const uint RingTailRegister = 0x04C;
    public const uint RingControlRegister = 0x050;
    public const uint FenceRegister = 0x054;
    public const uint ErrorRegister = 0x058;
    public const uint ErrorOffsetRegister = 0x05C;
    public const uint BackendInfoRegister = 0x060;
    public const uint BackendOperationsRegister = 0x064;
    public const uint HostPreferredWidthRegister = 0x068;
    public const uint HostPreferredHeightRegister = 0x06C;
    public const uint HostPreferredGenerationRegister = 0x070;

    /// <summary>"AGPU" read as a little-endian dword.</summary>
    public const uint IdValue = 0x55504741;

    /// <summary>Interface version 1.2: major in the high word, minor in the low.</summary>
    public const uint VersionValue = 0x00010002;

    public const uint CapabilityCommandRing = 1u << 0;
    public const uint CapabilityAlphaBlending = 1u << 1;
    public const uint CapabilityHardwareCursor = 1u << 2;
    public const uint CapabilityInterrupts = 1u << 3;
    public const uint CapabilityNativeBackend = 1u << 4;
    public const uint CapabilityHostPreferredSize = 1u << 5;

    public const uint BackendInterfaceVersion = 1;
    public const uint BackendKindSoftware = 0;
    public const uint BackendKindOpenGl = 1;
    public const uint BackendKindVulkan = 2;
    public const uint BackendOperationOpaqueFill = 1u << 0;
    public const uint BackendOperationBlendFill = 1u << 1;
    public const uint BackendOperationsKnown = BackendOperationOpaqueFill | BackendOperationBlendFill;

    public const uint DisplayControlScanoutEnable = 1u << 0;
    public const uint DisplayStatusActive = 1u << 0;

    public const uint InterruptFence = 1u << 0;
    public const uint InterruptCommandError = 1u << 1;
    public const uint InterruptAll = InterruptFence | InterruptCommandError;

    public const uint RingControlEnable = 1u << 0;
    public const uint RingControlReset = 1u << 1;

    public const int MaximumDisplayDimension = 4096;
    public const int MaximumHostPreferredWidth = 1920;
    public const int MaximumHostPreferredHeight = 1080;
    public const uint MinimumRingSize = 4 * 1024;
    public const uint MaximumRingSize = 1024 * 1024;
    public const uint RingBaseAlignment = 4 * 1024;

    // ---- commands ----------------------------------------------------------------

    public const ushort OpcodeNop = 0x0000;
    public const ushort OpcodeFence = 0x0001;
    public const ushort OpcodeDefineSurface = 0x0010;
    public const ushort OpcodeSetClip = 0x0011;
    public const ushort OpcodeFillRect = 0x0020;
    public const ushort OpcodeCopyRect = 0x0021;
    public const ushort OpcodeBlitImage = 0x0022;
    public const ushort OpcodeDrawMask = 0x0023;
    public const ushort OpcodeDrawLine = 0x0024;
    public const ushort OpcodeCursorImage = 0x0030;
    public const ushort OpcodeCursorMove = 0x0031;
    public const ushort OpcodePresent = 0x0040;

    /// <summary>Flag bit 0 of the drawing commands: blend source-over instead of replacing.</summary>
    public const uint FlagBlend = 1u << 0;

    /// <summary>Flag bit 0 of FENCE: raise the fence interrupt when it completes.</summary>
    public const uint FlagFenceInterrupt = 1u << 0;

    /// <summary>Flag bit 0 of SET_CLIP: the clip rectangle applies.</summary>
    public const uint FlagClipEnabled = 1u << 0;

    /// <summary>Flag bit 0 of CURSOR_MOVE: the cursor is shown.</summary>
    public const uint FlagCursorVisible = 1u << 0;

    /// <summary>Flag bit 0 of PRESENT: the command carries a new display offset.</summary>
    public const uint FlagPresentSetOffset = 1u << 0;

    public const uint SourceSpaceGuestMemory = 0;
    public const uint SourceSpaceVideoMemory = 1;

    public const int SurfaceCount = 16;
    public const int CursorSize = 64;

    // ---- errors ------------------------------------------------------------------

    public const uint ErrorNone = 0;
    public const uint ErrorUnknownOpcode = 1;
    public const uint ErrorBadLength = 2;
    public const uint ErrorBadSurface = 3;
    public const uint ErrorBadSurfaceDefinition = 4;
    public const uint ErrorBadSource = 5;
    public const uint ErrorBadRing = 6;
    public const uint ErrorBadCoordinate = 7;

    /// <summary>DRAW_LINE end points must lie in this range (docs/AURORA-GPU.md section 6, error 7).</summary>
    public const int MinimumLineCoordinate = -32768;
    public const int MaximumLineCoordinate = 32767;

    /// <summary>
    /// The number of dwords each opcode needs, header included. A command may
    /// be longer; the extra dwords are reserved for later fields.
    /// </summary>
    public static int RequiredLength(ushort opcode)
    {
        return opcode switch
        {
            OpcodeNop => 1,
            OpcodeFence => 3,
            OpcodeDefineSurface => 6,
            OpcodeSetClip => 6,
            OpcodeFillRect => 8,
            OpcodeCopyRect => 10,
            OpcodeBlitImage => 10,
            OpcodeDrawMask => 11,
            OpcodeDrawLine => 8,
            OpcodeCursorImage => 5,
            OpcodeCursorMove => 4,
            OpcodePresent => 3,
            _ => 0,
        };
    }

    /// <summary>Builds a command header: the opcode in the low word, the length in dwords in the high word.</summary>
    public static uint Header(ushort opcode, int lengthInDwords)
    {
        return opcode | ((uint)lengthInDwords << 16);
    }
}
