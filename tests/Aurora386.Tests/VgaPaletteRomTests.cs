using Aurora386.Core;
using Xunit;

namespace Aurora386.Tests;

/// <summary>
/// INT 10h AH=10h: the palette functions the ROM performs with OUT
/// instructions, and the read functions the native handler answers.
/// </summary>
public class VgaPaletteRomTests
{
    private static void Run(Machine machine, byte[] program)
    {
        uint code = 0x20100;
        for (int i = 0; i < program.Length; i++) machine.Memory.WriteByte(code + (uint)i, program[i]);
        machine.Memory.WriteByte(code + (uint)program.Length, 0xF4);   // HLT
        machine.Cpu.SetSegment(Aurora386.Core.Cpu.SegReg.Cs, 0x2000);
        machine.Cpu.SetSegment(Aurora386.Core.Cpu.SegReg.Es, 0x2000);
        machine.Cpu.State.Eip = 0x0100;
        machine.Cpu.SetSegment(Aurora386.Core.Cpu.SegReg.Ss, 0x3000);
        machine.Cpu.State.Esp = 0xFFFE;
        machine.Cpu.State.Halted = false;
        for (int i = 0; i < 20000 && !machine.Cpu.State.Halted; i++) machine.RunInstructions(1);
        Assert.True(machine.Cpu.State.Halted, "the program did not reach its HLT");
    }

    [Fact]
    public void SettingAllPaletteRegistersRemapsThePixels()
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);
        // Table at 2000:0400: register i -> DAC entry 15 - i, overscan 5.
        for (int i = 0; i < 16; i++) machine.Memory.WriteByte(0x20400 + (uint)i, (byte)(15 - i));
        machine.Memory.WriteByte(0x20410, 5);
        Run(machine, [0xB8, 0x02, 0x10, 0xBA, 0x00, 0x04, 0xCD, 0x10]);   // MOV AX,1002h / MOV DX,0400h / INT 10h

        Assert.Equal(14, machine.VideoRegisters.PaletteIndexFor(1));
        Assert.Equal(0, machine.VideoRegisters.PaletteIndexFor(15));
        Assert.Equal(5, machine.VideoRegisters.Attribute(0x11));
        // The display is switched back on afterwards.
        Assert.Equal(0x20, machine.Io.ReadByte(0x3C0) & 0x20);
    }

    [Fact]
    public void SettingOneRegisterAndOneDacEntry()
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);
        // MOV AX,1000h / MOV BX,0C07h (register 7 -> entry 0Ch) / INT 10h
        // MOV AX,1010h / MOV BX,0005h / MOV DH,10h / MOV CX,2030h / INT 10h
        Run(machine, [0xB8, 0x00, 0x10, 0xBB, 0x07, 0x0C, 0xCD, 0x10,
                      0xB8, 0x10, 0x10, 0xBB, 0x05, 0x00, 0xB6, 0x10, 0xB9, 0x30, 0x20, 0xCD, 0x10]);

        Assert.Equal(0x0C, machine.VideoRegisters.PaletteIndexFor(7));
        Assert.Equal((0x10, 0x20, 0x30), machine.VideoRegisters.GetRawPaletteEntry(5));
    }

    [Fact]
    public void ABlockOfDacEntriesAndTheReadBack()
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);
        for (int i = 0; i < 6; i++) machine.Memory.WriteByte(0x20500 + (uint)i, (byte)(i + 1));   // two entries
        // MOV AX,1012h / MOV BX,0008h / MOV CX,0002h / MOV DX,0500h / INT 10h
        // MOV AX,1015h / MOV BX,0009h / INT 10h  -> DH,CH,CL
        Run(machine, [0xB8, 0x12, 0x10, 0xBB, 0x08, 0x00, 0xB9, 0x02, 0x00, 0xBA, 0x00, 0x05, 0xCD, 0x10,
                      0xB8, 0x15, 0x10, 0xBB, 0x09, 0x00, 0xCD, 0x10]);

        Assert.Equal((1, 2, 3), machine.VideoRegisters.GetRawPaletteEntry(8));
        Assert.Equal((4, 5, 6), machine.VideoRegisters.GetRawPaletteEntry(9));
        Assert.Equal(4, machine.Cpu.State.GetReg8(6));   // DH
        Assert.Equal(5, machine.Cpu.State.GetReg8(5));   // CH
        Assert.Equal(6, machine.Cpu.State.GetReg8(1));   // CL
    }

    [Fact]
    public void BlinkBitAndColourSelect()
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);
        // MOV AX,1003h / MOV BL,00h (intensity) / INT 10h ; MOV AX,1013h / MOV BX,0501h (select page 5) / INT 10h
        Run(machine, [0xB8, 0x03, 0x10, 0xB3, 0x00, 0xCD, 0x10, 0xB8, 0x13, 0x10, 0xBB, 0x01, 0x05, 0xCD, 0x10]);

        Assert.Equal(0, machine.VideoRegisters.Attribute(0x10) & 0x08);
        Assert.Equal(5, machine.VideoRegisters.Attribute(0x14));
        Assert.Equal(0x04, machine.VideoRegisters.Attribute(0x10) & 0x04);   // the rest of the mode register untouched
    }
}
