using Aurora386.Core.Devices.Video;
using CpuNs = Aurora386.Core.Cpu;

namespace Aurora386.Core.Firmware;

/// <summary>
/// The VESA BIOS Extension, INT 10h function 4Fh.
///
/// Before VBE, a program that wanted 800x600 had to know which card it was
/// running on and program that card's own registers, because every maker had
/// invented their own. Games shipped with a menu of card names and you picked
/// yours from the list and hoped. VBE replaced the list with a question: the
/// program asks the card what it can do, and the answer has the same shape
/// whoever built it.
///
/// The functions divide into asking and doing. 4F00 and 4F01 describe the
/// card and one of its modes; 4F02 and 4F03 set and report the current mode;
/// 4F05 moves the memory window. Every one of them answers in AX, and the
/// convention is worth knowing because it is unusual: AL=0x4F means "this
/// function exists", and AH is the actual result -- zero for success. A
/// program that checks only AX==0x004F is checking both at once, which is
/// exactly what VESA intended.
/// </summary>
public sealed class VesaServices
{
    private readonly Machine _machine;

    public VesaServices(Machine machine) => _machine = machine;

    /// <summary>Video memory available to the SVGA modes.</summary>
    private static int VideoMemory => VgaMemory.TotalSize;

    /// <summary>
    /// The physical address the linear frame buffer appears at.
    ///
    /// A real card put it above the 16 MB line, because below that is the ISA
    /// hole and whatever RAM the machine has, and a card plugged into a slot
    /// cannot know how much of either. Here the machine does know, so the
    /// buffer sits directly above the installed memory -- the same reasoning,
    /// with the answer available.
    /// </summary>
    private uint LinearFrameBufferBase => _machine.LinearFrameBufferBase;

    public bool Handle(CpuNs.Cpu cpu)
    {
        byte function = cpu.State.GetReg8(0);   // AL

        switch (function)
        {
            case 0x00: ReportCapabilities(cpu); return true;
            case 0x01: ReportMode(cpu); return true;
            case 0x02: SetMode(cpu); return true;
            case 0x03: GetMode(cpu); return true;
            case 0x05: WindowControl(cpu); return true;

            default:
                // Unsupported subfunction: AL=0x4F says VBE is here, AH=0x01
                // says this particular call failed. Returning nothing at all
                // would leave a program unable to tell the two apart.
                cpu.State.Ax = 0x014F;
                return true;
        }
    }

    /// <summary>
    /// 4F00: the card's own description, written into the caller's buffer at
    /// ES:DI.
    ///
    /// The awkward part of this structure is that three of its fields are far
    /// pointers to memory the BIOS has to provide -- the OEM name string and
    /// the list of supported modes. On a real card they point into its ROM;
    /// here they point into the emulated BIOS ROM, at addresses reserved for
    /// exactly this.
    /// </summary>
    private void ReportCapabilities(CpuNs.Cpu cpu)
    {
        uint buffer = (uint)(cpu.State.Es.Selector << 4) + cpu.State.Di;

        // A caller that wants VBE 2.0 writes "VBE2" into the buffer before
        // calling, which tells the card it may use the 2.0-only fields. We
        // answer 2.0 either way; the signature we write back is "VESA".
        Write(buffer + 0, (byte)'V');
        Write(buffer + 1, (byte)'E');
        Write(buffer + 2, (byte)'S');
        Write(buffer + 3, (byte)'A');

        WriteWord(buffer + 4, 0x0200);                  // VBE version 2.0
        WriteFar(buffer + 6, BiosRom.Entry.VesaOemName);
        WriteDword(buffer + 10, 0x00000001);            // capabilities: 8-bit DAC
        WriteFar(buffer + 14, BiosRom.Entry.VesaModeList);

        // Memory in 64 KB units, which is the unit VBE chose and the reason a
        // card with 1 MB reports 16.
        WriteWord(buffer + 18, (ushort)(VideoMemory / 0x10000));

        WriteWord(buffer + 20, 0x0100);                 // OEM software revision
        WriteFar(buffer + 22, BiosRom.Entry.VesaOemName);
        WriteFar(buffer + 26, BiosRom.Entry.VesaOemName);
        WriteFar(buffer + 30, BiosRom.Entry.VesaOemName);

        cpu.State.Ax = 0x004F;
    }

    /// <summary>
    /// 4F01: everything about one mode, written to ES:DI.
    ///
    /// This is the structure a program reads to find out whether a mode is
    /// worth using: how the pixels are arranged, where the window is, and --
    /// the field that changed everything -- whether there is a linear frame
    /// buffer and at what address.
    /// </summary>
    private void ReportMode(CpuNs.Cpu cpu)
    {
        VesaModes.VesaModeInfo? found = VesaModes.Get(cpu.State.Cx, VideoMemory);
        if (found is not { } mode)
        {
            cpu.State.Ax = 0x014F;      // no such mode on this card
            return;
        }

        uint buffer = (uint)(cpu.State.Es.Selector << 4) + cpu.State.Di;
        for (uint i = 0; i < 256; i++) Write(buffer + i, 0);

        // Attributes: supported, colour, graphics, and -- bit 7 -- a linear
        // frame buffer is available. A program that wants the frame buffer
        // checks that bit before asking for it.
        WriteWord(buffer + 0, 0x009B);

        WriteWord(buffer + 2, 0x0700);                          // window A: readable, writable
        WriteWord(buffer + 4, 64);                              // granularity, KB
        WriteWord(buffer + 6, 64);                              // window size, KB
        WriteWord(buffer + 8, 0xA000);                          // window A segment
        WriteWord(buffer + 16, (ushort)mode.BytesPerRow);
        WriteWord(buffer + 18, (ushort)mode.Width);
        WriteWord(buffer + 20, (ushort)mode.Height);
        Write(buffer + 22, 8);                                  // character cell width
        Write(buffer + 23, 16);                                 // character cell height
        Write(buffer + 24, 1);                                  // memory planes
        Write(buffer + 25, (byte)mode.BitsPerPixel);
        Write(buffer + 26, 1);                                  // banks
        Write(buffer + 27, (byte)(mode.BitsPerPixel == 8 ? 4 : 6));   // memory model
        Write(buffer + 29, 8);                                  // DAC bits per colour

        // How the colour bits are laid out, which a program drawing at 15 or
        // 16 bits has to know: the two differ only in whether green gets the
        // spare bit, and an image drawn assuming the wrong one has a green
        // cast.
        if (mode.BitsPerPixel is 15 or 16)
        {
            Write(buffer + 31, 5);                              // red size
            Write(buffer + 32, (byte)(mode.BitsPerPixel == 15 ? 10 : 11));
            Write(buffer + 33, (byte)(mode.BitsPerPixel == 15 ? 5 : 6));   // green size
            Write(buffer + 34, 5);                              // green position
            Write(buffer + 35, 5);                              // blue size
            Write(buffer + 36, 0);                              // blue position
        }
        else if (mode.BitsPerPixel >= 24)
        {
            Write(buffer + 31, 8); Write(buffer + 32, 16);
            Write(buffer + 33, 8); Write(buffer + 34, 8);
            Write(buffer + 35, 8); Write(buffer + 36, 0);
        }

        WriteDword(buffer + 40, LinearFrameBufferBase);
        WriteWord(buffer + 50, (ushort)mode.BytesPerRow);       // linear bytes per row

        cpu.State.Ax = 0x004F;
    }

    /// <summary>
    /// 4F02: sets a mode. Bit 14 of the number asks for the linear frame
    /// buffer, bit 15 to keep the memory contents.
    /// </summary>
    private void SetMode(CpuNs.Cpu cpu)
    {
        ushort requested = cpu.State.Bx;

        // A VBE call can also be used to return to an ordinary VGA mode, and
        // programs do exactly that on the way out.
        if ((requested & 0x01FF) < 0x100)
        {
            _machine.VideoRegisters.SetVesaMode(null, false);
            _machine.VideoRegisters.VideoMode = (byte)(requested & 0xFF);
            _machine.VideoRegisters.NotifyModeChanged();
            cpu.State.Ax = 0x004F;
            return;
        }

        VesaModes.VesaModeInfo? found = VesaModes.Get(requested, VideoMemory);
        if (found is not { } mode)
        {
            cpu.State.Ax = 0x014F;
            return;
        }

        bool linear = (requested & 0x4000) != 0;
        _machine.VideoRegisters.SetVesaMode(mode, linear);

        // Unless asked to preserve it, the screen is cleared: a program that
        // has just changed resolution should not see the previous mode's
        // pixels reinterpreted at the new one, which looks like static.
        if ((requested & 0x8000) == 0)
            for (uint i = 0; i < mode.MemoryNeeded; i++)
                _machine.VideoMemory.WriteLinear(i, 0);

        cpu.State.Ax = 0x004F;
    }

    /// <summary>4F03: which mode is in force.</summary>
    private void GetMode(CpuNs.Cpu cpu)
    {
        VgaRegisters registers = _machine.VideoRegisters;

        cpu.State.Bx = registers.VesaMode is { } mode
            ? (ushort)(mode.Number | (registers.VesaLinearFrameBuffer ? 0x4000 : 0))
            : registers.VideoMode;

        cpu.State.Ax = 0x004F;
    }

    /// <summary>
    /// 4F05: moves the 64 KB window onto video memory, which is how a
    /// real-mode program reaches more pixels than the address space has room
    /// for.
    ///
    /// BH selects setting or reading, BL which window -- there are two on
    /// paper, A and B, and almost every card made them the same one.
    /// </summary>
    private void WindowControl(CpuNs.Cpu cpu)
    {
        byte operation = cpu.State.GetReg8(7);   // BH

        if (operation == 0x00)
        {
            _machine.VideoRegisters.VesaWindowBank = cpu.State.Dx;
            cpu.State.Ax = 0x004F;
            return;
        }

        if (operation == 0x01)
        {
            cpu.State.Dx = (ushort)_machine.VideoRegisters.VesaWindowBank;
            cpu.State.Ax = 0x004F;
            return;
        }

        cpu.State.Ax = 0x014F;
    }

    private void Write(uint address, byte value) => _machine.Memory.WriteByte(address, value);
    private void WriteWord(uint address, ushort value) => _machine.Memory.WriteWord(address, value);
    private void WriteDword(uint address, uint value) => _machine.Memory.WriteDword(address, value);

    /// <summary>Writes a far pointer into the BIOS ROM segment.</summary>
    private void WriteFar(uint address, ushort offset)
    {
        WriteWord(address, offset);
        WriteWord(address + 2, 0xF000);
    }
}
