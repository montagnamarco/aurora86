using Aurora386.Core;
using Aurora386.Core.Cpu;
using Aurora386.Core.Devices.Video;

namespace Aurora386.Tests;

/// <summary>
/// The VESA BIOS Extension.
///
/// Before VBE, a program that wanted 800x600 had to know which card it was
/// running on and drive that card's own registers, because every maker had
/// invented their own. Games shipped with a menu of card names: you picked
/// yours from the list and hoped. VBE replaced the list with a question --
/// the program asks the card what it can do, and the answer has the same
/// shape whoever built it.
///
/// The return convention is unusual enough to be worth stating: AL=0x4F means
/// the function exists, and AH is the result, zero for success. A program
/// that checks AX==0x004F is checking both at once, which is what VESA
/// intended.
/// </summary>
public class VesaTests
{
    private static Machine Started()
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        machine.Cpu.State.Halted = false;
        return machine;
    }

    /// <summary>Calls INT 10h the way a program would, through the registers.</summary>
    private static void Int10(Machine machine, ushort ax,
                              ushort bx = 0, ushort cx = 0, ushort dx = 0,
                              ushort es = 0, ushort di = 0)
    {
        machine.Cpu.SetSegment(SegReg.Es, es);
        machine.Cpu.State.Ax = ax;
        machine.Cpu.State.Bx = bx;
        machine.Cpu.State.Cx = cx;
        machine.Cpu.State.Dx = dx;
        machine.Cpu.State.Di = di;
        machine.VideoServices.Handle(machine.Cpu);
    }

    [Fact]
    public void TheCardIdentifiesItselfAsVbeTwo()
    {
        using Machine machine = Started();

        Int10(machine, 0x4F00, es: 0x5000);

        Assert.Equal(0x004F, machine.Cpu.State.Ax);

        // "VESA" at the start of the block, then the version. A program reads
        // the signature to be sure it is looking at a real answer rather than
        // whatever was in its buffer.
        uint at = 0x50000;
        Assert.Equal((byte)'V', machine.Memory.ReadByte(at));
        Assert.Equal((byte)'E', machine.Memory.ReadByte(at + 1));
        Assert.Equal((byte)'S', machine.Memory.ReadByte(at + 2));
        Assert.Equal((byte)'A', machine.Memory.ReadByte(at + 3));
        Assert.Equal(0x0200, machine.Memory.ReadWord(at + 4));
    }

    [Fact]
    public void MemoryIsReportedInSixtyFourKilobyteUnits()
    {
        // The unit VBE chose, and the reason a card with two megabytes
        // reports 32 rather than a number of bytes.
        using Machine machine = Started();

        Int10(machine, 0x4F00, es: 0x5000);

        Assert.Equal(VgaMemory.TotalSize / 0x10000, machine.Memory.ReadWord(0x50000 + 18));
    }

    [Fact]
    public void TheModeListIsReachedThroughAPointerAndEndsInAllOnes()
    {
        // The list is pointed at rather than copied, because a program is
        // entitled to keep the pointer and read it later. It walks the list
        // until 0xFFFF, so a missing terminator sends it off through whatever
        // follows in ROM.
        using Machine machine = Started();

        Int10(machine, 0x4F00, es: 0x5000);

        uint block = 0x50000;
        uint list = (uint)(machine.Memory.ReadWord(block + 16) << 4)
                  + machine.Memory.ReadWord(block + 14);

        var modes = new List<ushort>();
        for (int i = 0; i < 64; i++)
        {
            ushort mode = machine.Memory.ReadWord(list + (uint)i * 2);
            if (mode == 0xFFFF) break;
            modes.Add(mode);
        }

        Assert.Contains((ushort)0x101, modes);       // 640x480x8
        Assert.Contains((ushort)0x105, modes);       // 1024x768x8
        Assert.True(modes.Count < 64);               // it did terminate
    }

    [Fact]
    public void AModeTooLargeForTheMemoryIsNotOffered()
    {
        // Reporting a mode and then failing to set it is worse than not
        // reporting it: a program that asked the card what it could do is
        // entitled to believe the answer.
        using Machine machine = Started();

        // 1024x768 at 24 bits needs 2.25 MB, and this card has two.
        Int10(machine, 0x4F01, cx: 0x118, es: 0x5100);

        Assert.Equal(0x014F, machine.Cpu.State.Ax);
    }

    [Fact]
    public void AModeDescribesItsGeometryAndItsFrameBuffer()
    {
        using Machine machine = Started();

        Int10(machine, 0x4F01, cx: 0x105, es: 0x5100);

        Assert.Equal(0x004F, machine.Cpu.State.Ax);

        uint info = 0x51000;
        Assert.Equal(1024, machine.Memory.ReadWord(info + 18));
        Assert.Equal(768, machine.Memory.ReadWord(info + 20));
        Assert.Equal(8, machine.Memory.ReadByte(info + 25));
        Assert.Equal(1024, machine.Memory.ReadWord(info + 16));      // bytes per row

        // The field that changed everything: where the whole picture is,
        // addressable as one flat array.
        Assert.Equal(machine.LinearFrameBufferBase, machine.Memory.ReadDword(info + 40));
    }

    [Fact]
    public void SettingAModeChangesWhatTheRendererDraws()
    {
        using Machine machine = Started();

        Int10(machine, 0x4F02, bx: 0x101);

        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        Assert.Equal(VgaMemoryLayout.HighColour, machine.VideoRegisters.EffectiveMode.Layout);
        Assert.Equal(640, machine.Video.Width);
        Assert.Equal(480, machine.Video.Height);
    }

    [Fact]
    public void PixelsWrittenThroughTheWindowAppear()
    {
        using Machine machine = Started();
        Int10(machine, 0x4F02, bx: 0x101);

        machine.VideoMemory.WriteByte(0, 0x0F);
        machine.VideoMemory.MarkRowsDirty(0, 480);
        machine.Video.Render();

        Assert.NotEqual(0u, machine.Video.Framebuffer[0] & 0xFFFFFF);
    }

    [Fact]
    public void TheWindowMovesWhereTheBankRegisterSaysAndTheScreenDoesNot()
    {
        // The distinction that makes banking work: the bank register moves
        // what the processor can reach, not what the monitor shows. The beam
        // draws the whole picture from memory whatever slice is mapped.
        using Machine machine = Started();
        Int10(machine, 0x4F02, bx: 0x101);

        // Bank 1: the window at 0xA0000 now shows the second 64 KB.
        Int10(machine, 0x4F05, bx: 0x0000, dx: 1);
        Assert.Equal(0x004F, machine.Cpu.State.Ax);

        machine.VideoMemory.WriteByte(0, 0x2A);

        // The byte landed at 0x10000 in video memory, which is row 102 of a
        // 640-byte-per-row mode -- not row 0.
        Assert.Equal(0x2A, machine.VideoMemory.ReadLinear(0x10000));
        Assert.Equal(0, machine.VideoMemory.ReadLinear(0));
    }

    [Fact]
    public void TheBankCanBeReadBack()
    {
        using Machine machine = Started();
        Int10(machine, 0x4F02, bx: 0x101);

        Int10(machine, 0x4F05, bx: 0x0000, dx: 3);
        Int10(machine, 0x4F05, bx: 0x0100);

        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        Assert.Equal(3, machine.Cpu.State.Dx);
    }

    [Fact]
    public void TheLinearFrameBufferReachesTheSameMemory()
    {
        // What a protected-mode display driver uses: no window, no banking,
        // the whole picture at one address.
        using Machine machine = Started();
        Int10(machine, 0x4F02, bx: 0x101);

        machine.Memory.WriteByte(machine.LinearFrameBufferBase + 640, 0x0A);

        Assert.Equal(0x0A, machine.VideoMemory.ReadLinear(640));

        machine.Video.Render();
        Assert.NotEqual(0u, machine.Video.Framebuffer[640] & 0xFFFFFF);
    }

    [Fact]
    public void SettingAModeClearsTheScreenUnlessAskedNotTo()
    {
        // A program that has just changed resolution should not see the
        // previous mode's pixels reinterpreted at the new one, which looks
        // like static. Bit 15 asks for them to be kept.
        using Machine machine = Started();
        Int10(machine, 0x4F02, bx: 0x101);

        machine.VideoMemory.WriteByte(0, 0x3C);
        Int10(machine, 0x4F02, bx: 0x101);
        Assert.Equal(0, machine.VideoMemory.ReadLinear(0));

        machine.VideoMemory.WriteByte(0, 0x3C);
        Int10(machine, 0x4F02, bx: 0x8101);
        Assert.Equal(0x3C, machine.VideoMemory.ReadLinear(0));
    }

    [Fact]
    public void AVbeCallCanAlsoReturnToAnOrdinaryVgaMode()
    {
        // Programs do exactly this on the way out, rather than switching back
        // to INT 10h AH=00.
        using Machine machine = Started();
        Int10(machine, 0x4F02, bx: 0x101);

        Int10(machine, 0x4F02, bx: 0x03);

        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        Assert.Null(machine.VideoRegisters.VesaMode);
        Assert.Equal(VgaMemoryLayout.Text, machine.VideoRegisters.EffectiveMode.Layout);
    }

    [Fact]
    public void TheCurrentModeCanBeAskedFor()
    {
        using Machine machine = Started();
        Int10(machine, 0x4F02, bx: 0x4103);      // 800x600x8, linear

        Int10(machine, 0x4F03);

        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        Assert.Equal(0x4103, machine.Cpu.State.Bx);
    }

    [Fact]
    public void AnUnknownSubfunctionSaysSoRatherThanStayingSilent()
    {
        // AL=0x4F says VBE is here, AH=0x01 that this call failed. Returning
        // nothing would leave a program unable to tell the two apart.
        using Machine machine = Started();

        Int10(machine, 0x4F42);

        Assert.Equal(0x014F, machine.Cpu.State.Ax);
    }
}
