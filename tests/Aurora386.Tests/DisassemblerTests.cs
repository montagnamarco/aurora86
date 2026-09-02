using Aurora386.Core.Debug;

namespace Aurora386.Tests;

/// <summary>
/// The disassembler is a pure function over a byte stream, so every test simply
/// feeds it known encodings and checks the rendered text and the length.
/// </summary>
public class DisassemblerTests
{
    private const uint Origin = 0x1000;

    private static DecodedInstruction Dis16(params byte[] code)
        => Disassembler.Decode(code, Origin, bits32: false);

    private static DecodedInstruction Dis32(params byte[] code)
        => Disassembler.Decode(code, Origin, bits32: true);

    // --- immediates and register encodings ------------------------------------

    [Fact]
    public void DecodesMovImmediateWord()
    {
        var i = Dis16(0xB8, 0x34, 0x12);
        Assert.Equal("mov ax, 1234h", i.Text);
        Assert.Equal(3, i.Length);
        Assert.Equal("B8 34 12", i.Bytes);
    }

    [Fact]
    public void OperandSizePrefixSelectsThe32BitFormIn16BitCode()
    {
        var i = Dis16(0x66, 0xB8, 0x78, 0x56, 0x34, 0x12);
        Assert.Equal("mov eax, 12345678h", i.Text);
        Assert.Equal(6, i.Length);
    }

    [Fact]
    public void MovImmediateIs32BitByDefaultIn32BitCode()
    {
        var i = Dis32(0xB8, 0x78, 0x56, 0x34, 0x12);
        Assert.Equal("mov eax, 12345678h", i.Text);
        Assert.Equal(5, i.Length);
    }

    [Fact]
    public void DecodesMovImmediateByte()
    {
        Assert.Equal("mov bl, 0FFh", Dis16(0xB3, 0xFF).Text);
    }

    [Fact]
    public void HexLiteralsStartingWithALetterGetALeadingZero()
    {
        // 0FFh, not FFh: without the leading zero an assembler would read it as
        // an identifier.
        Assert.Equal("mov al, 0A0h", Dis16(0xB0, 0xA0).Text);
    }

    // --- ModR/M in 16-bit addressing -----------------------------------------

    [Fact]
    public void DecodesDirectMemoryOperandIn16BitMode()
    {
        // mod=0 rm=6 is the direct address, not [bp].
        var i = Dis16(0x8B, 0x06, 0x34, 0x12);
        Assert.Equal("mov ax, [1234h]", i.Text);
        Assert.Equal(4, i.Length);
    }

    [Fact]
    public void DecodesBasePlusIndexIn16BitMode()
    {
        Assert.Equal("mov ax, [bx+si]", Dis16(0x8B, 0x00).Text);
    }

    [Fact]
    public void DecodesSignedByteDisplacement()
    {
        // 0FCh is -4, so the operand reads back as a subtraction.
        Assert.Equal("mov ax, [bp-4h]", Dis16(0x8B, 0x46, 0xFC).Text);
    }

    [Fact]
    public void DecodesRegisterToRegisterMove()
    {
        Assert.Equal("mov ax, bx", Dis16(0x8B, 0xC3).Text);
    }

    [Fact]
    public void SegmentOverridePrefixIsPrintedOnTheMemoryOperand()
    {
        Assert.Equal("mov ax, es:[bx]", Dis16(0x26, 0x8B, 0x07).Text);
    }

    // --- ModR/M and SIB in 32-bit addressing ---------------------------------

    [Fact]
    public void DecodesSibWithScaledIndex()
    {
        // 8B 04 8D dd dd dd dd -> mov eax, [ecx*4 + disp32] (no base register)
        var i = Dis32(0x8B, 0x04, 0x8D, 0x00, 0x10, 0x00, 0x00);
        Assert.Equal("mov eax, [ecx*4+1000h]", i.Text);
        Assert.Equal(7, i.Length);
    }

    [Fact]
    public void DecodesSibWithBaseAndIndex()
    {
        // base = eax, index = ebx, scale = 1
        Assert.Equal("mov eax, [eax+ebx]", Dis32(0x8B, 0x04, 0x18).Text);
    }

    [Fact]
    public void Applies386ScaleQuirkToTheBaseWhenTheIndexIsAbsent()
    {
        // SIB = scale 2 (x4), index 4 (none), base 0 (eax). On the 386 the shift
        // is applied to the base register, so this really computes eax * 4.
        var i = Dis32(0x8B, 0x04, 0xA0);
        Assert.Equal("mov eax, [eax*4]", i.Text);
    }

    [Fact]
    public void ScaleZeroWithNoIndexLeavesTheBaseUnscaled()
    {
        Assert.Equal("mov eax, [eax]", Dis32(0x8B, 0x04, 0x20).Text);
    }

    [Fact]
    public void DecodesDirectMemoryOperandIn32BitMode()
    {
        // mod=0 rm=5 is the 32-bit direct address, not [ebp].
        Assert.Equal("mov eax, [12345678h]", Dis32(0x8B, 0x05, 0x78, 0x56, 0x34, 0x12).Text);
    }

    // --- branches -------------------------------------------------------------

    [Fact]
    public void DecodesShortJumpToItself()
    {
        // EB FE: the displacement -2 points back at the jump, the classic
        // "hang here" idiom.
        var i = Dis16(0xEB, 0xFE);
        Assert.Equal("jmp short 1000h", i.Text);
        Assert.Equal(2, i.Length);
    }

    [Fact]
    public void DecodesShortConditionalJump()
    {
        Assert.Equal("jz short 1002h", Dis16(0x74, 0x00).Text);
    }

    [Fact]
    public void DecodesLongConditionalJump()
    {
        // 0F 84 is the 386's full-range JZ.
        var i = Dis16(0x0F, 0x84, 0x10, 0x00);
        Assert.Equal("jz 1014h", i.Text);
        Assert.Equal(4, i.Length);
    }

    [Fact]
    public void DecodesNearCall()
    {
        Assert.Equal("call 1103h", Dis16(0xE8, 0x00, 0x01).Text);
    }

    [Fact]
    public void DecodesLoop()
    {
        Assert.Equal("loop 0FFEh", Dis16(0xE2, 0xFC).Text);
    }

    // --- interrupts, strings, prefixes ---------------------------------------

    [Fact]
    public void DecodesSoftwareInterrupt()
    {
        var i = Dis16(0xCD, 0x21);
        Assert.Equal("int 21h", i.Text);
        Assert.Equal(2, i.Length);
    }

    [Fact]
    public void DecodesRepeatedStringMove()
    {
        var i = Dis16(0xF3, 0xA5);
        Assert.Equal("rep movsw", i.Text);
        Assert.Equal(2, i.Length);
    }

    [Fact]
    public void RepPrefixWithOperandSizePrefixGivesTheDwordForm()
    {
        Assert.Equal("rep movsd", Dis16(0xF3, 0x66, 0xA5).Text);
    }

    [Fact]
    public void DecodesRepneScas()
    {
        Assert.Equal("repne scasb", Dis16(0xF2, 0xAE).Text);
    }

    [Fact]
    public void DecodesLockPrefix()
    {
        Assert.Equal("lock inc word ptr [bx]", Dis16(0xF0, 0xFF, 0x07).Text);
    }

    // --- 386 two-byte opcodes -------------------------------------------------

    [Fact]
    public void DecodesMovzx()
    {
        var i = Dis16(0x0F, 0xB6, 0xC3);
        Assert.Equal("movzx ax, bl", i.Text);
        Assert.Equal(3, i.Length);
    }

    [Fact]
    public void DecodesMovsx()
    {
        Assert.Equal("movsx ax, bl", Dis16(0x0F, 0xBE, 0xC3).Text);
    }

    [Fact]
    public void DecodesSetcc()
    {
        Assert.Equal("setz al", Dis16(0x0F, 0x94, 0xC0).Text);
    }

    [Fact]
    public void DecodesBitTestWithImmediate()
    {
        Assert.Equal("bt ax, 03h", Dis16(0x0F, 0xBA, 0xE0, 0x03).Text);
    }

    [Fact]
    public void DecodesShldWithImmediateCount()
    {
        Assert.Equal("shld ax, dx, 04h", Dis16(0x0F, 0xA4, 0xD0, 0x04).Text);
    }

    [Fact]
    public void DecodesBitScanForward()
    {
        Assert.Equal("bsf ax, bx", Dis16(0x0F, 0xBC, 0xC3).Text);
    }

    [Fact]
    public void DecodesTwoOperandImul()
    {
        Assert.Equal("imul ax, bx", Dis16(0x0F, 0xAF, 0xC3).Text);
    }

    [Fact]
    public void DecodesPushFs()
    {
        Assert.Equal("push fs", Dis16(0x0F, 0xA0).Text);
    }

    [Fact]
    public void DecodesLgdt()
    {
        Assert.Equal("lgdt fword ptr [1234h]", Dis16(0x0F, 0x01, 0x16, 0x34, 0x12).Text);
    }

    [Fact]
    public void DecodesMoveFromControlRegister()
    {
        Assert.Equal("mov eax, cr0", Dis16(0x0F, 0x20, 0xC0).Text);
    }

    [Fact]
    public void DecodesMoveToControlRegister()
    {
        Assert.Equal("mov cr0, eax", Dis16(0x0F, 0x22, 0xC0).Text);
    }

    [Fact]
    public void DecodesClts()
    {
        Assert.Equal("clts", Dis16(0x0F, 0x06).Text);
    }

    [Fact]
    public void DecodesLss()
    {
        Assert.Equal("lss sp, dword ptr [bx]", Dis16(0x0F, 0xB2, 0x27).Text);
    }

    // --- groups and one-byte odds and ends ------------------------------------

    [Fact]
    public void DecodesImmediateGroupWithSignExtendedByte()
    {
        // 83 /0 ib: add with an immediate sign-extended to the operand size.
        Assert.Equal("add ax, 0FFFFh", Dis16(0x83, 0xC0, 0xFF).Text);
    }

    [Fact]
    public void DecodesUnaryGroupTest()
    {
        Assert.Equal("test byte ptr [bx], 01h", Dis16(0xF6, 0x07, 0x01).Text);
    }

    [Fact]
    public void DecodesUnaryGroupNeg()
    {
        Assert.Equal("neg ax", Dis16(0xF7, 0xD8).Text);
    }

    [Fact]
    public void DecodesFarCallThroughMemory()
    {
        Assert.Equal("call far dword ptr [bx]", Dis16(0xFF, 0x1F).Text);
    }

    [Fact]
    public void DecodesShiftByCl()
    {
        Assert.Equal("shl ax, cl", Dis16(0xD3, 0xE0).Text);
    }

    [Fact]
    public void DecodesShiftByOne()
    {
        Assert.Equal("sar ax, 1", Dis16(0xD1, 0xF8).Text);
    }

    [Fact]
    public void DecodesLea()
    {
        // LEA computes the address, so no size hint is printed.
        Assert.Equal("lea ax, [bx+si+2h]", Dis16(0x8D, 0x40, 0x02).Text);
    }

    [Fact]
    public void DecodesSegmentRegisterMove()
    {
        Assert.Equal("mov ds, ax", Dis16(0x8E, 0xD8).Text);
    }

    [Fact]
    public void DecodesAccumulatorDirectMove()
    {
        Assert.Equal("mov ax, [1234h]", Dis16(0xA1, 0x34, 0x12).Text);
    }

    [Fact]
    public void DecodesEnterAndLeave()
    {
        Assert.Equal("enter 0010h, 00h", Dis16(0xC8, 0x10, 0x00, 0x00).Text);
        Assert.Equal("leave", Dis16(0xC9).Text);
    }

    [Fact]
    public void DecodesFarJump()
    {
        Assert.Equal("jmp far 0F000h:1234h", Dis16(0xEA, 0x34, 0x12, 0x00, 0xF0).Text);
    }

    [Fact]
    public void DecodesPushaAndPopa()
    {
        Assert.Equal("pusha", Dis16(0x60).Text);
        Assert.Equal("popad", Dis32(0x61).Text);
    }

    [Fact]
    public void DecodesNopAsTheDegenerateXchg()
    {
        Assert.Equal("nop", Dis16(0x90).Text);
        Assert.Equal("xchg ax, bx", Dis16(0x93).Text);
    }

    [Fact]
    public void DecodesInAndOut()
    {
        Assert.Equal("in al, 60h", Dis16(0xE4, 0x60).Text);
        Assert.Equal("out dx, al", Dis16(0xEE).Text);
    }

    [Fact]
    public void DecodesSimpleOneByteInstructions()
    {
        Assert.Equal("hlt", Dis16(0xF4).Text);
        Assert.Equal("cmc", Dis16(0xF5).Text);
        Assert.Equal("cli", Dis16(0xFA).Text);
        Assert.Equal("iret", Dis16(0xCF).Text);
        Assert.Equal("xlat", Dis16(0xD7).Text);
        Assert.Equal("aam", Dis16(0xD4, 0x0A).Text);
        Assert.Equal("cbw", Dis16(0x98).Text);
        Assert.Equal("pushf", Dis16(0x9C).Text);
        Assert.Equal("sahf", Dis16(0x9E).Text);
    }

    [Fact]
    public void DecodesAluBlockArithmetically()
    {
        Assert.Equal("add al, bl", Dis16(0x00, 0xD8).Text);
        Assert.Equal("or ax, bx", Dis16(0x09, 0xD8).Text);
        Assert.Equal("cmp ax, bx", Dis16(0x39, 0xD8).Text);
        Assert.Equal("xor al, 12h", Dis16(0x34, 0x12).Text);
    }

    // --- robustness -----------------------------------------------------------

    [Fact]
    public void UnknownOpcodeBecomesADataByteRatherThanAnException()
    {
        var i = Dis16(0x0F, 0xFE);
        Assert.StartsWith("db ", i.Text);
        Assert.Equal(1, i.Length);
    }

    [Fact]
    public void ARunOfPrefixesWithNoOpcodeIsTreatedAsData()
    {
        // A stream of nothing but prefixes has no opcode to prefix. The decoder
        // consumes them up to the architectural 15-byte limit and then gives up,
        // and whatever it reports must still let the caller move forward.
        var i = Disassembler.Decode(_ => 0x66, Origin, bits32: false);
        Assert.True(i.Length >= 1);
        Assert.False(string.IsNullOrEmpty(i.Text));
    }

    [Fact]
    public void NeverThrowsOnArbitraryBytes()
    {
        // Walk every single-byte opcode with a reader that returns garbage; the
        // debugger must survive disassembling data.
        for (int op = 0; op <= 0xFF; op++)
        {
            byte first = (byte)op;
            bool used = false;
            var i = Disassembler.Decode(a =>
            {
                if (a == Origin && !used) { used = true; return first; }
                return (byte)(a * 7);
            }, Origin, bits32: false);
            Assert.True(i.Length >= 1);
            Assert.False(string.IsNullOrEmpty(i.Text));
        }
    }

    [Fact]
    public void ReportsTheAddressItWasGiven()
    {
        var i = Disassembler.Decode(new byte[] { 0x90 }, 0xF000, bits32: false);
        Assert.Equal(0xF000u, i.Address);
    }
}
