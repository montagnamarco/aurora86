using Aurora386.Core.Devices.Video;
using CpuNs = Aurora386.Core.Cpu;

namespace Aurora386.Core.Firmware;

/// <summary>
/// The VESA BIOS Extension for the Aurora Display Adapter (docs/AURORA-GPU.md
/// section 2): what a boot loader such as Limine, or an operating system
/// without a driver, uses to get a 32-bit linear frame buffer at up to
/// 1920x1080.
///
/// Every mode is direct colour X8R8G8B8, linear only: there is no 64 KB bank
/// window, which a program of the VBE 2.0 era learns from mode attribute
/// bit 6, and no palette. A mode set programs the card's own display
/// registers, the same ones a driver writes, so a driver that takes over
/// later finds the geometry the boot loader chose.
/// </summary>
public sealed class AuroraGpuVesaServices
{
    /// <summary>One VBE mode: its number and resolution; every mode is 32 bits per pixel.</summary>
    public readonly record struct Mode(ushort Number, int Width, int Height);

    /// <summary>The modes the BIOS publishes, in the order of the ROM's mode list.</summary>
    public static readonly IReadOnlyList<Mode> Modes =
    [
        new Mode(0x180, 640, 480),
        new Mode(0x181, 800, 600),
        new Mode(0x182, 1024, 768),
        new Mode(0x183, 1280, 720),
        new Mode(0x184, 1280, 1024),
        new Mode(0x185, 1366, 768),
        new Mode(0x186, 1600, 900),
        new Mode(0x187, 1920, 1080),
    ];

    private const int BytesPerPixel = 4;
    private const int BitsPerPixel = 32;

    private const ushort VbeSuccess = 0x004F;
    private const ushort VbeFailure = 0x014F;
    private const ushort VbeVersion20 = 0x0200;

    /// <summary>Bits 13-0 of a VBE mode number; bit 14 asks for the linear frame buffer, bit 15 to keep memory.</summary>
    private const ushort ModeNumberMask = 0x3FFF;
    private const ushort KeepMemoryBit = 0x8000;
    private const ushort LinearFrameBufferBit = 0x4000;
    private const ushort FirstVesaMode = 0x100;

    /// <summary>
    /// Mode attributes: supported (0), reserved-one (1), colour (3),
    /// graphics (4), not VGA compatible (5), no windowed access (6), linear
    /// frame buffer (7).
    /// </summary>
    private const ushort ModeAttributes = 0x00FB;

    /// <summary>VBE memory model 6: direct colour.</summary>
    private const byte DirectColourModel = 6;

    private const int ModeInfoLength = 256;
    private const ushort BiosRomSegment = 0xF000;

    private readonly Machine _machine;
    private readonly AuroraGpuCard _card;

    public AuroraGpuVesaServices(Machine machine, AuroraGpuCard card)
    {
        _machine = machine;
        _card = card;
    }

    /// <summary>Handles INT 10h AX=4Fxx. AL selects the function; the answer is in AX.</summary>
    public void Handle(CpuNs.Cpu cpu)
    {
        byte function = cpu.State.GetReg8(0);   // AL

        switch (function)
        {
            case 0x00:
                ReportCapabilities(cpu);
                return;

            case 0x01:
                ReportMode(cpu);
                return;

            case 0x02:
                SetMode(cpu);
                return;

            case 0x03:
                GetMode(cpu);
                return;

            case 0x06:
                ScanLineLength(cpu);
                return;

            case 0x07:
                DisplayStart(cpu);
                return;

            default:
                // 4F05 (bank window), 4F08 (DAC width) and 4F09 (palette)
                // have nothing to act on in a linear direct-colour card.
                cpu.State.Ax = VbeFailure;
                return;
        }
    }

    /// <summary>The published mode with this number, or null.</summary>
    public static Mode? Find(ushort number)
    {
        ushort masked = (ushort)(number & ModeNumberMask);
        foreach (Mode mode in Modes)
        {
            if (mode.Number == masked)
            {
                return mode;
            }
        }

        return null;
    }

    /// <summary>4F00: the controller information block at ES:DI.</summary>
    private void ReportCapabilities(CpuNs.Cpu cpu)
    {
        uint buffer = BufferAddress(cpu);

        WriteByte(buffer + 0, (byte)'V');
        WriteByte(buffer + 1, (byte)'E');
        WriteByte(buffer + 2, (byte)'S');
        WriteByte(buffer + 3, (byte)'A');
        WriteWord(buffer + 4, VbeVersion20);
        WriteFar(buffer + 6, BiosRom.Entry.VesaOemName);
        WriteDword(buffer + 10, 0);                                 // no switchable DAC, VGA-compatible controller
        WriteFar(buffer + 14, BiosRom.Entry.VesaModeList);
        WriteWord(buffer + 18, (ushort)(AuroraGpuInterface.VideoMemorySize / 0x10000));   // 64 KB units
        WriteWord(buffer + 20, 0x0100);                             // OEM software revision
        WriteFar(buffer + 22, BiosRom.Entry.VesaOemName);
        WriteFar(buffer + 26, BiosRom.Entry.VesaOemName);
        WriteFar(buffer + 30, BiosRom.Entry.VesaOemName);

        cpu.State.Ax = VbeSuccess;
    }

    /// <summary>4F01: the mode information block for CX at ES:DI.</summary>
    private void ReportMode(CpuNs.Cpu cpu)
    {
        if (Find(cpu.State.Cx) is not { } mode)
        {
            cpu.State.Ax = VbeFailure;
            return;
        }

        uint buffer = BufferAddress(cpu);
        for (uint index = 0; index < ModeInfoLength; index++)
        {
            WriteByte(buffer + index, 0);
        }

        int pitch = mode.Width * BytesPerPixel;
        int frameBytes = pitch * mode.Height;

        WriteWord(buffer + 0, ModeAttributes);
        // Window attributes, granularity, size, segments and the far
        // function pointer stay zero: there is no window.
        WriteWord(buffer + 16, (ushort)pitch);
        WriteWord(buffer + 18, (ushort)mode.Width);
        WriteWord(buffer + 20, (ushort)mode.Height);
        WriteByte(buffer + 22, 8);                                  // character cell width
        WriteByte(buffer + 23, 16);                                 // character cell height
        WriteByte(buffer + 24, 1);                                  // planes
        WriteByte(buffer + 25, BitsPerPixel);
        WriteByte(buffer + 26, 1);                                  // banks
        WriteByte(buffer + 27, DirectColourModel);
        WriteByte(buffer + 29, (byte)Math.Min(AuroraGpuInterface.VideoMemorySize / frameBytes - 1, byte.MaxValue));
        WriteByte(buffer + 30, 1);                                  // reserved, 1 by VBE 2.0

        // X8R8G8B8: blue in bits 7-0, green 15-8, red 23-16, reserved 31-24.
        WriteByte(buffer + 31, 8);
        WriteByte(buffer + 32, 16);
        WriteByte(buffer + 33, 8);
        WriteByte(buffer + 34, 8);
        WriteByte(buffer + 35, 8);
        WriteByte(buffer + 36, 0);
        WriteByte(buffer + 37, 8);
        WriteByte(buffer + 38, 24);

        WriteDword(buffer + 40, _card.FrameBufferBase);

        cpu.State.Ax = VbeSuccess;
    }

    /// <summary>
    /// 4F02: BX is the mode. A VGA mode number returns the screen to the VGA;
    /// one of ours programs the card, cleared unless bit 15 is set.
    /// </summary>
    private void SetMode(CpuNs.Cpu cpu)
    {
        ushort requested = cpu.State.Bx;
        ushort number = (ushort)(requested & ModeNumberMask);

        if (number < FirstVesaMode)
        {
            _card.ReturnToVga();
            _machine.VideoRegisters.SetVesaMode(null, false);
            _machine.VideoRegisters.VideoMode = (byte)number;
            _machine.VideoRegisters.NotifyModeChanged();
            cpu.State.Ax = VbeSuccess;
            return;
        }

        if (Find(number) is not { } mode)
        {
            cpu.State.Ax = VbeFailure;
            return;
        }

        bool clearMemory = (requested & KeepMemoryBit) == 0;
        _card.SetDisplayMode(mode.Width, mode.Height, clearMemory);
        cpu.State.Ax = VbeSuccess;
    }

    /// <summary>
    /// 4F03: the current mode, read back from the display registers rather
    /// than remembered, so it stays right after a snapshot restore or after a
    /// driver changed resolution itself. A geometry that is none of the
    /// published modes reports the VGA mode, as there is no number for it.
    /// </summary>
    private void GetMode(CpuNs.Cpu cpu)
    {
        Mode? current = null;
        if (_card.ScanoutActive)
        {
            foreach (Mode mode in Modes)
            {
                if (mode.Width == _card.DisplayWidth && mode.Height == _card.DisplayHeight)
                {
                    current = mode;
                }
            }
        }

        if (current is { } active)
        {
            cpu.State.Bx = (ushort)(active.Number | LinearFrameBufferBit);
        }
        else
        {
            cpu.State.Bx = _machine.VideoRegisters.VideoMode;
        }

        cpu.State.Ax = VbeSuccess;
    }

    /// <summary>
    /// 4F06: the scan line length. BL=0 sets it in pixels, BL=2 in bytes, BL=1
    /// reports it and BL=3 reports the longest possible. Never shorter than
    /// the visible line, and refused when the frame -- display offset
    /// included -- would then leave video memory. Answered only while a valid
    /// frame is displayed: a driver may have enabled scanout with no geometry,
    /// and there is no line length to talk about then.
    /// </summary>
    private void ScanLineLength(CpuNs.Cpu cpu)
    {
        if (!_card.ScanoutActive)
        {
            cpu.State.Ax = VbeFailure;
            return;
        }

        byte operation = cpu.State.GetReg8(3);   // BL
        uint visibleBytes = (uint)_card.DisplayWidth * BytesPerPixel;

        if (operation is 0x00 or 0x02)
        {
            uint requested = operation == 0x00
                ? (uint)cpu.State.Cx * BytesPerPixel
                : cpu.State.Cx;
            uint pitch = Math.Max(requested, visibleBytes);
            pitch = (pitch + BytesPerPixel - 1) / BytesPerPixel * BytesPerPixel;

            uint previousPitch = _card.DisplayPitch;
            WriteCardRegister(AuroraGpuInterface.DisplayPitchRegister, pitch);
            if (!_card.DisplayGeometryValid)
            {
                WriteCardRegister(AuroraGpuInterface.DisplayPitchRegister, previousPitch);
                cpu.State.Ax = VbeFailure;
                return;
            }
        }
        else if (operation == 0x03)
        {
            uint longest = MaximumPitch();
            ReportLineLength(cpu, longest);
            return;
        }
        else if (operation != 0x01)
        {
            cpu.State.Ax = VbeFailure;
            return;
        }

        ReportLineLength(cpu, _card.DisplayPitch);
    }

    /// <summary>The longest line, a multiple of four bytes, that keeps the current frame inside video memory.</summary>
    private uint MaximumPitch()
    {
        long available = AuroraGpuInterface.VideoMemorySize - _card.DisplayOffset;
        long lines = _card.DisplayHeight;
        long pitch = available / lines;
        pitch = pitch / BytesPerPixel * BytesPerPixel;
        return (uint)Math.Min(pitch, ushort.MaxValue / BytesPerPixel * BytesPerPixel);
    }

    /// <summary>
    /// BX bytes per line, CX pixels per line, DX the number of lines video
    /// memory holds at that length. The registers are 16 bits wide, so a
    /// line longer than 65535 bytes -- possible only with a geometry a driver
    /// programmed itself -- is reported modulo 65536; that is the VBE
    /// format's limit, not the card's.
    /// </summary>
    private static void ReportLineLength(CpuNs.Cpu cpu, uint bytes)
    {
        cpu.State.Bx = (ushort)bytes;
        cpu.State.Cx = (ushort)(bytes / BytesPerPixel);
        cpu.State.Dx = (ushort)Math.Min(AuroraGpuInterface.VideoMemorySize / bytes, ushort.MaxValue);
        cpu.State.Ax = VbeSuccess;
    }

    /// <summary>
    /// 4F07: the pixel shown at the top left. BL=0 or 80h sets it from CX
    /// (column) and DX (line), BL=1 reports it. There is no retrace to wait
    /// for: a frame is taken whole. Answered only while a valid frame is
    /// displayed, and a start that would take the frame out of video memory
    /// is refused.
    /// </summary>
    private void DisplayStart(CpuNs.Cpu cpu)
    {
        if (!_card.ScanoutActive)
        {
            cpu.State.Ax = VbeFailure;
            return;
        }

        byte operation = cpu.State.GetReg8(3);   // BL
        uint pitch = _card.DisplayPitch;

        if (operation is 0x00 or 0x80)
        {
            // In 64 bits: a pitch a driver programmed can be large enough for
            // DX times it to wrap a 32-bit sum round to a valid-looking start.
            long requestedOffset = (long)cpu.State.Dx * pitch + (long)cpu.State.Cx * BytesPerPixel;
            if (requestedOffset > uint.MaxValue)
            {
                cpu.State.Ax = VbeFailure;
                return;
            }

            uint previousOffset = _card.DisplayOffset;
            WriteCardRegister(AuroraGpuInterface.DisplayOffsetRegister, (uint)requestedOffset);
            if (!_card.DisplayGeometryValid)
            {
                WriteCardRegister(AuroraGpuInterface.DisplayOffsetRegister, previousOffset);
                cpu.State.Ax = VbeFailure;
                return;
            }

            cpu.State.Ax = VbeSuccess;
            return;
        }

        if (operation == 0x01)
        {
            uint offset = _card.DisplayOffset;
            cpu.State.Dx = (ushort)(offset / pitch);
            cpu.State.Cx = (ushort)(offset % pitch / BytesPerPixel);
            cpu.State.Ax = VbeSuccess;
            return;
        }

        cpu.State.Ax = VbeFailure;
    }

    /// <summary>Writes a display register exactly as a driver's 32-bit store to BAR1 would.</summary>
    private void WriteCardRegister(uint register, uint value)
    {
        _card.WriteRegisterFromFirmware(register, value);
    }

    private static uint BufferAddress(CpuNs.Cpu cpu)
    {
        return (uint)(cpu.State.Es.Selector << 4) + cpu.State.Di;
    }

    private void WriteByte(uint address, byte value)
    {
        _machine.Memory.WriteByte(address, value);
    }

    private void WriteWord(uint address, ushort value)
    {
        _machine.Memory.WriteWord(address, value);
    }

    private void WriteDword(uint address, uint value)
    {
        _machine.Memory.WriteDword(address, value);
    }

    /// <summary>A far pointer into the BIOS ROM segment, where the OEM string and mode list live.</summary>
    private void WriteFar(uint address, ushort offset)
    {
        WriteWord(address, offset);
        WriteWord(address + 2, BiosRomSegment);
    }
}
