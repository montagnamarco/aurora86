using Aurora86.Core;
using Aurora86.Core.Cpu;
using Aurora86.Core.Memory;

namespace Aurora86.Tests;

/// <summary>
/// Long mode, stage C2a (plans/long-mode.md): the integer arithmetic and
/// logic instructions of 64-bit mode -- the eight ALU operations, TEST, the
/// F6/F7, FE and FF groups, IMUL's immediate and register forms, the shift
/// and rotate group with SHLD/SHRD, the bit-test and bit-scan instructions,
/// and the 486's read-modify-write pair XADD/CMPXCHG together with
/// CMPXCHG8B/16B -- implemented in Cpu.LongMode.Alu.cs.
///
/// Flags are computed eagerly there rather than through LazyFlags' pending
/// scheme, so every test that checks a flag is checking that the eager
/// formula reproduces the SDM Vol.2 result (including the vectors' own
/// answer for the flags the manual leaves undefined) at the operand's own
/// width, from 8 to 64 bits.
///
/// The harness is LongModeDecoderTests' own, trimmed: a Pentium II given long
/// mode by hand, put straight into 64-bit mode, with the code page, one data
/// page (5000h) and a stack page identity-mapped.
///
/// These tests assume Cpu.LongMode.Execute.cs's default branches call
/// TryExecuteAlu64/TryExecuteAluTwoByte64, and that its LOCK check calls
/// LockAllowedAlu64 -- the wiring stage C2a's contract hands to the owner
/// (see the handoff report for the exact two call sites).
/// </summary>
public class LongModeAluTests
{
    private const uint CodeBase = 0x1000;
    private const uint DataPage = 0x5000;
    private const uint ReadOnlyPage = 0x9000;
    private const byte InvalidOpcode = 6;
    private const byte DivideError = 0;
    private const byte GeneralProtection = 13;
    private const byte PageFault = 14;
    private const byte NoFault = 0xFF;

    // === the eight ALU operations, across operand widths =======================

    /// <summary>00h /r, byte register form: 0xFF + 0x01 wraps to zero, carrying, with even parity and an auxiliary carry.</summary>
    [Fact]
    public void AddByteRegisterFormSetsCarryZeroParityAndAdjust()
    {
        var harness = new Harness(0x00, 0xC1);   // add cl, al
        harness.Cpu.State.Gpr[1] = 0xFF;         // cl
        harness.Cpu.State.Gpr[0] = 0x01;         // al

        harness.Step();

        Assert.Equal(0x00UL, harness.Cpu.State.Gpr[1] & 0xFF);
        Assert.True(harness.Cpu.Flags.Carry);
        Assert.True(harness.Cpu.Flags.Zero);
        Assert.True(harness.Cpu.Flags.Parity);
        Assert.True(harness.Cpu.Flags.Adjust);
        Assert.False(harness.Cpu.Flags.Overflow);
        Assert.False(harness.Cpu.Flags.Sign);
    }

    /// <summary>11h /r with REX.W: ADC RAX, RBX with an incoming carry wraps 0xFFFFFFFFFFFFFFFF to zero and CF survives the wrap.</summary>
    [Fact]
    public void AdcWithIncomingCarrySixtyFourBitsWrapsToZero()
    {
        var harness = new Harness(0x48, 0x11, 0xD8);   // adc rax, rbx
        harness.Cpu.State.Gpr[0] = 0xFFFFFFFF_FFFFFFFFUL;
        harness.Cpu.State.Gpr[3] = 0;
        harness.Cpu.Flags.Carry = true;

        harness.Step();

        Assert.Equal(0UL, harness.Cpu.State.Gpr[0]);
        Assert.True(harness.Cpu.Flags.Carry);
        Assert.True(harness.Cpu.Flags.Zero);
    }

    /// <summary>2Bh /r form3, 32-bit: SUB EAX, ECX borrows, and the 32-bit write zero-extends into the upper half of RAX.</summary>
    [Fact]
    public void SubForm3ThirtyTwoBitBorrowsAndZeroExtendsRax()
    {
        var harness = new Harness(0x2B, 0xC1);   // sub eax, ecx
        harness.Cpu.State.Gpr[0] = 0xDEADBEEF_00000005UL;
        harness.Cpu.State.Gpr[1] = 10;

        harness.Step();

        Assert.Equal(0xFFFFFFFBUL, harness.Cpu.State.Gpr[0]);
        Assert.True(harness.Cpu.Flags.Carry);
        Assert.True(harness.Cpu.Flags.Sign);
        Assert.False(harness.Cpu.Flags.Zero);
    }

    /// <summary>3Ch ib: CMP AL, imm8 sets ZF but never writes AL back.</summary>
    [Fact]
    public void CmpFormFourDoesNotWriteTheAccumulator()
    {
        var harness = new Harness(0x3C, 0x10);
        harness.Cpu.State.Gpr[0] = 0x10;

        harness.Step();

        Assert.Equal(0x10UL, harness.Cpu.State.Gpr[0] & 0xFF);
        Assert.True(harness.Cpu.Flags.Zero);
    }

    /// <summary>35h iw: XOR AX, imm16 with equal operands clears CF/OF and sets ZF and PF.</summary>
    [Fact]
    public void XorFormFiveSixteenBitClearsToZero()
    {
        var harness = new Harness(0x66, 0x35, 0x34, 0x12);
        harness.Cpu.State.Gpr[0] = 0x1234;

        harness.Step();

        Assert.Equal(0UL, harness.Cpu.State.Gpr[0] & 0xFFFF);
        Assert.True(harness.Cpu.Flags.Zero);
        Assert.True(harness.Cpu.Flags.Parity);
        Assert.False(harness.Cpu.Flags.Carry);
        Assert.False(harness.Cpu.Flags.Overflow);
    }

    /// <summary>05h id with REX.W: ADD RAX, imm32 sign-extends the immediate -- adding 0FFFFFFFFh subtracts one.</summary>
    [Fact]
    public void AddFormFiveSixtyFourBitSignExtendsImm32()
    {
        var harness = new Harness(0x48, 0x05, 0xFF, 0xFF, 0xFF, 0xFF);   // add rax, 0FFFFFFFFh
        harness.Cpu.State.Gpr[0] = 1;

        harness.Step();

        Assert.Equal(0UL, harness.Cpu.State.Gpr[0]);
        Assert.True(harness.Cpu.Flags.Carry);
        Assert.True(harness.Cpu.Flags.Zero);
    }

    /// <summary>05h id, no REX: a 32-bit ALU immediate form also zero-extends RAX.</summary>
    [Fact]
    public void AddFormFiveThirtyTwoBitZeroExtendsRax()
    {
        var harness = new Harness(0x05, 0x01, 0x00, 0x00, 0x00);   // add eax, 1
        harness.Cpu.State.Gpr[0] = 0xFFFFFFFF_00000001UL;

        harness.Step();

        Assert.Equal(2UL, harness.Cpu.State.Gpr[0]);
    }

    /// <summary>01h /r with REX.W: ADD RAX, RBX sets AF from a low-nibble carry, the same formula LazyFlags uses, extended to 64 bits.</summary>
    [Fact]
    public void AddSixtyFourBitSetsAdjustFlag()
    {
        var harness = new Harness(0x48, 0x01, 0xD8);   // add rax, rbx
        harness.Cpu.State.Gpr[0] = 0x0F;
        harness.Cpu.State.Gpr[3] = 0x01;

        harness.Step();

        Assert.Equal(0x10UL, harness.Cpu.State.Gpr[0]);
        Assert.True(harness.Cpu.Flags.Adjust);
        Assert.False(harness.Cpu.Flags.Carry);
        Assert.False(harness.Cpu.Flags.Overflow);
    }

    /// <summary>01h /r with REX.W: ADD RAX, RBX at the 64-bit signed boundary (INT64_MAX + 1) sets OF and SF but not CF.</summary>
    [Fact]
    public void AddSixtyFourBitSignedOverflowSetsOverflowFlag()
    {
        var harness = new Harness(0x48, 0x01, 0xD8);   // add rax, rbx
        harness.Cpu.State.Gpr[0] = 0x7FFFFFFF_FFFFFFFFUL;   // INT64_MAX
        harness.Cpu.State.Gpr[3] = 1;

        harness.Step();

        Assert.Equal(0x8000000000000000UL, harness.Cpu.State.Gpr[0]);
        Assert.True(harness.Cpu.Flags.Overflow);
        Assert.True(harness.Cpu.Flags.Sign);
        Assert.False(harness.Cpu.Flags.Carry);
    }

    // === group 1 (80h/81h/83h) ===================================================

    /// <summary>83h /0 ib: ADD r/m32, imm8 sign-extends 0FFh to -1, so ECX drops by one rather than rising by 255.</summary>
    [Fact]
    public void Group1SignExtendedByteImmediateSubtractsOne()
    {
        var harness = new Harness(0x83, 0xC1, 0xFF);   // add ecx, -1
        harness.Cpu.State.Gpr[1] = 5;

        harness.Step();

        Assert.Equal(4UL, harness.Cpu.State.Gpr[1]);
    }

    /// <summary>81h /4 iw: AND AX, imm16.</summary>
    [Fact]
    public void Group1SixteenBitImmediateAnds()
    {
        var harness = new Harness(0x66, 0x81, 0xE0, 0x0F, 0x00);   // and ax, 0Fh
        harness.Cpu.State.Gpr[0] = 0xFF00;

        harness.Step();

        Assert.Equal(0UL, harness.Cpu.State.Gpr[0] & 0xFFFF);
        Assert.True(harness.Cpu.Flags.Zero);
    }

    /// <summary>80h /7 ib: CMP r/m8, imm8 sets flags without writing DL back.</summary>
    [Fact]
    public void Group1ByteCompareDoesNotWrite()
    {
        var harness = new Harness(0x80, 0xFA, 0x05);   // cmp dl, 5
        harness.Cpu.State.Gpr[2] = 5;

        harness.Step();

        Assert.Equal(5UL, harness.Cpu.State.Gpr[2] & 0xFF);
        Assert.True(harness.Cpu.Flags.Zero);
    }

    /// <summary>82h -- the undocumented 386 alias of 80h -- does not exist in 64-bit mode.</summary>
    [Fact]
    public void Opcode82hIsInvalidInSixtyFourBitMode()
    {
        var harness = new Harness(0x82, 0xC0);

        Assert.Equal(InvalidOpcode, harness.FaultVector());
    }

    // === TEST ====================================================================

    /// <summary>85h /r: TEST EAX, ECX sets ZF from the AND but leaves EAX untouched.</summary>
    [Fact]
    public void TestSetsZeroFlagWithoutWriting()
    {
        var harness = new Harness(0x85, 0xC8);   // test eax, ecx
        harness.Cpu.State.Gpr[0] = 0x0F0F0F0F;
        harness.Cpu.State.Gpr[1] = 0xF0F0F0F0;

        harness.Step();

        Assert.Equal(0x0F0F0F0FUL, harness.Cpu.State.Gpr[0]);
        Assert.True(harness.Cpu.Flags.Zero);
    }

    // === group 3 (F6h/F7h): NOT, NEG, MUL, IMUL, DIV, IDIV ======================

    /// <summary>F6h /2: NOT touches no flags at all.</summary>
    [Fact]
    public void NotLeavesFlagsAlone()
    {
        var harness = new Harness(0xF6, 0xD0);   // not al
        harness.Cpu.State.Gpr[0] = 0x0F;
        harness.Cpu.Flags.Carry = true;

        harness.Step();

        Assert.Equal(0xF0UL, harness.Cpu.State.Gpr[0] & 0xFF);
        Assert.True(harness.Cpu.Flags.Carry);
    }

    /// <summary>F7h /3 with REX.W: NEG on RAX = 1 sets CF (the operand was non-zero) and SF.</summary>
    [Fact]
    public void NegSixtyFourBitSetsCarryWhenOperandNonZero()
    {
        var harness = new Harness(0x48, 0xF7, 0xD8);   // neg rax
        harness.Cpu.State.Gpr[0] = 1;

        harness.Step();

        Assert.Equal(0xFFFFFFFF_FFFFFFFFUL, harness.Cpu.State.Gpr[0]);
        Assert.True(harness.Cpu.Flags.Carry);
        Assert.True(harness.Cpu.Flags.Sign);
    }

    /// <summary>F7h /4 with REX.W: MUL RBX at 64 bits produces the full 128-bit product in RDX:RAX and sets CF/OF when the high half is not empty.</summary>
    [Fact]
    public void MulSixtyFourBitProducesRdxRaxPair()
    {
        var harness = new Harness(0x48, 0xF7, 0xE3);   // mul rbx
        harness.Cpu.State.Gpr[0] = 0x8000000000000000UL;   // 2^63
        harness.Cpu.State.Gpr[3] = 2;                        // 2^63 * 2 = 2^64, one bit past RAX alone

        harness.Step();

        Assert.Equal(0UL, harness.Cpu.State.Gpr[0]);
        Assert.Equal(1UL, harness.Cpu.State.Gpr[2]);
        Assert.True(harness.Cpu.Flags.Carry);
        Assert.True(harness.Cpu.Flags.Overflow);
    }

    /// <summary>F7h /5 with REX.W: IMUL RBX, a negative product that still fits in RAX alone, sign-extends into RDX and clears CF/OF.</summary>
    [Fact]
    public void ImulSixtyFourBitNoOverflowSignExtendsIntoRdx()
    {
        var harness = new Harness(0x48, 0xF7, 0xEB);   // imul rbx
        harness.Cpu.State.Gpr[0] = unchecked((ulong)-5L);
        harness.Cpu.State.Gpr[3] = 3;

        harness.Step();

        Assert.Equal(unchecked((ulong)-15L), harness.Cpu.State.Gpr[0]);
        Assert.Equal(0xFFFFFFFF_FFFFFFFFUL, harness.Cpu.State.Gpr[2]);
        Assert.False(harness.Cpu.Flags.Carry);
        Assert.False(harness.Cpu.Flags.Overflow);
    }

    /// <summary>F7h /6 with REX.W: DIV by zero is #DE at 64 bits, exactly as at every other width.</summary>
    [Fact]
    public void DivSixtyFourBitByZeroIsDivideError()
    {
        var harness = new Harness(0x48, 0xF7, 0xF3);   // div rbx
        harness.Cpu.State.Gpr[3] = 0;

        Assert.Equal(DivideError, harness.FaultVector());
    }

    /// <summary>
    /// F7h /6 with REX.W: an unsigned quotient that overflows 64 bits is
    /// #DE. ExecuteDiv64 (Cpu.LongMode.Alu.cs) checks the quotient's range
    /// before either WriteRegister64 call runs, so RDX/RAX are never touched
    /// by this instruction on the error path -- this only checks the fault
    /// vector, because 64-bit mode's own fault delivery still reuses the
    /// legacy 32-bit interrupt path on top of an IDT this harness never sets
    /// up (plans/long-mode.md's C1 review notes, "il percorso dei guasti
    /// riusa codice legacy a 32 bit"), which can itself go on to touch
    /// registers once DeliverException runs -- a stage D concern, not
    /// stage C2a's.
    /// </summary>
    [Fact]
    public void DivSixtyFourBitOverflowIsDivideError()
    {
        var harness = new Harness(0x48, 0xF7, 0xF3);   // div rbx
        harness.Cpu.State.Gpr[2] = 1;   // rdx
        harness.Cpu.State.Gpr[0] = 0;   // rax: dividend = 2^64
        harness.Cpu.State.Gpr[3] = 1;   // divisor 1 -> quotient 2^64, does not fit

        Assert.Equal(DivideError, harness.FaultVector());
    }

    /// <summary>F7h /7 with REX.W: signed IDIV truncates the quotient towards zero.</summary>
    [Fact]
    public void IdivSixtyFourBitTruncatesTowardsZero()
    {
        var harness = new Harness(0x48, 0xF7, 0xFB);   // idiv rbx
        harness.Cpu.State.Gpr[2] = 0xFFFFFFFF_FFFFFFFFUL;       // rdx: sign extension of -100
        harness.Cpu.State.Gpr[0] = unchecked((ulong)-100L);     // rax
        harness.Cpu.State.Gpr[3] = 3;                            // rbx

        harness.Step();

        Assert.Equal(unchecked((ulong)-33L), harness.Cpu.State.Gpr[0]);
        Assert.Equal(unchecked((ulong)-1L), harness.Cpu.State.Gpr[2]);
    }

    /// <summary>
    /// F7h /7 with REX.W: Int128.MinValue divided by -1 would overflow even
    /// .NET's own Int128 arithmetic (the mathematical quotient does not fit
    /// in 128 bits either), which ExecuteDiv64 has to catch itself and turn
    /// into #DE before the division runs, rather than let the runtime's
    /// OverflowException escape Step64 uncaught.
    /// </summary>
    [Fact]
    public void IdivSixtyFourBitMinValueByMinusOneIsDivideErrorNotAnOverflowException()
    {
        var harness = new Harness(0x48, 0xF7, 0xFB);   // idiv rbx
        harness.Cpu.State.Gpr[2] = 0x8000000000000000UL;   // rdx: high half of Int128.MinValue
        harness.Cpu.State.Gpr[0] = 0;                        // rax: low half
        harness.Cpu.State.Gpr[3] = unchecked((ulong)-1L);    // rbx: divisor -1

        Assert.Equal(DivideError, harness.FaultVector());
    }

    /// <summary>
    /// F6h /6 with REX.B: DIV R8B divides AX by R8B -- proving REX.B still
    /// reaches R8B for the r/m operand -- and writes the remainder to AH
    /// (State.SetReg8(4, ...), Cpu.LongMode.Alu.cs), not SPL, though REX is
    /// present. RSP (register 4 at the full width) is a different storage
    /// location from AH and must be left alone.
    /// </summary>
    [Fact]
    public void DivByteWithRexBWritesRemainderToAhNotSpl()
    {
        var harness = new Harness(0x41, 0xF6, 0xF0);   // div r8b
        harness.Cpu.State.Gpr[0] = 17;    // ax
        harness.Cpu.State.Gpr[8] = 5;     // r8b
        ulong rspBefore = harness.Cpu.State.Gpr[4];

        harness.Step();

        Assert.Equal(3UL, harness.Cpu.State.Gpr[0] & 0xFF);          // al: quotient
        Assert.Equal(2UL, (harness.Cpu.State.Gpr[0] >> 8) & 0xFF);   // ah: remainder
        Assert.Equal(rspBefore, harness.Cpu.State.Gpr[4]);
    }

    /// <summary>
    /// F6h /6 with a bare REX (no bits set): DIV SIL divides AX by SIL --
    /// any REX prefix redirects register 6 from DH to SIL for the r/m
    /// operand -- and the remainder still lands in AH, not in the low byte
    /// of RSP.
    /// </summary>
    [Fact]
    public void DivByteWithBareRexWritesRemainderToAhNotSpl()
    {
        var harness = new Harness(0x40, 0xF6, 0xF6);   // div sil
        harness.Cpu.State.Gpr[0] = 17;    // ax
        harness.Cpu.State.Gpr[6] = 5;     // sil
        ulong rspBefore = harness.Cpu.State.Gpr[4];

        harness.Step();

        Assert.Equal(3UL, harness.Cpu.State.Gpr[0] & 0xFF);
        Assert.Equal(2UL, (harness.Cpu.State.Gpr[0] >> 8) & 0xFF);
        Assert.Equal(rspBefore, harness.Cpu.State.Gpr[4]);
    }

    /// <summary>F6h /4 with REX.B: MUL R8B, the multiply side of the same REX.B-reaches-R8B story.</summary>
    [Fact]
    public void MulByteWithRexBReachesR8B()
    {
        var harness = new Harness(0x41, 0xF6, 0xE0);   // mul r8b
        harness.Cpu.State.Gpr[0] = 10;    // al
        harness.Cpu.State.Gpr[8] = 20;    // r8b
        ulong rspBefore = harness.Cpu.State.Gpr[4];

        harness.Step();

        Assert.Equal(200UL, harness.Cpu.State.Gpr[0] & 0xFFFF);
        Assert.Equal(rspBefore, harness.Cpu.State.Gpr[4]);
    }

    // === group 4 (FEh) and group 5 (FFh /0 /1) ==================================

    /// <summary>FEh /0: INC AL wraps 0xFF to zero and preserves whatever CF already held.</summary>
    [Fact]
    public void IncByteWrapsAndPreservesCarry()
    {
        var harness = new Harness(0xFE, 0xC0);   // inc al
        harness.Cpu.State.Gpr[0] = 0xFF;
        harness.Cpu.Flags.Carry = true;

        harness.Step();

        Assert.Equal(0UL, harness.Cpu.State.Gpr[0] & 0xFF);
        Assert.True(harness.Cpu.Flags.Carry);
        Assert.True(harness.Cpu.Flags.Zero);
    }

    /// <summary>FFh /1 with REX.W: DEC on the most negative 64-bit value overflows.</summary>
    [Fact]
    public void DecSixtyFourBitFromIntMinOverflows()
    {
        var harness = new Harness(0x48, 0xFF, 0xC8);   // dec rax
        harness.Cpu.State.Gpr[0] = 0x8000000000000000UL;

        harness.Step();

        Assert.Equal(0x7FFFFFFF_FFFFFFFFUL, harness.Cpu.State.Gpr[0]);
        Assert.True(harness.Cpu.Flags.Overflow);
    }

    // === IMUL: 69h, 6Bh, 0F AFh ===================================================

    /// <summary>69h /r iw: a 16-bit three-operand IMUL that overflows sets CF/OF and truncates the low half.</summary>
    [Fact]
    public void Imul69SixteenBitOverflowSetsCarryAndOverflow()
    {
        var harness = new Harness(0x66, 0x69, 0xC3, 0x04, 0x00);   // imul ax, bx, 4
        harness.Cpu.State.Gpr[3] = 0x4000;   // bx = 16384

        harness.Step();

        Assert.Equal(0UL, harness.Cpu.State.Gpr[0] & 0xFFFF);
        Assert.True(harness.Cpu.Flags.Carry);
        Assert.True(harness.Cpu.Flags.Overflow);
    }

    /// <summary>6Bh /r ib: the immediate is sign-extended, so a multiply by 0FFh means multiply by -1.</summary>
    [Fact]
    public void Imul6BThirtyTwoBitSignExtendsTheByteImmediate()
    {
        var harness = new Harness(0x6B, 0xCA, 0xFF);   // imul ecx, edx, -1
        harness.Cpu.State.Gpr[2] = 10;   // edx

        harness.Step();

        Assert.Equal(0xFFFFFFF6UL, harness.Cpu.State.Gpr[1] & 0xFFFFFFFF);
        Assert.False(harness.Cpu.Flags.Carry);
        Assert.False(harness.Cpu.Flags.Overflow);
    }

    /// <summary>0F AFh /r with REX.W: the two-operand register form.</summary>
    [Fact]
    public void ImulAfSixtyFourBitRegisterForm()
    {
        var harness = new Harness(0x48, 0x0F, 0xAF, 0xC1);   // imul rax, rcx
        harness.Cpu.State.Gpr[0] = 7;
        harness.Cpu.State.Gpr[1] = 6;

        harness.Step();

        Assert.Equal(42UL, harness.Cpu.State.Gpr[0]);
        Assert.False(harness.Cpu.Flags.Overflow);
    }

    // === shift and rotate group ==================================================

    /// <summary>C0h /0 ib: ROL AL, 1 with the top bit set rotates it into bit 0, setting CF and (sign changed) OF.</summary>
    [Fact]
    public void RolByteByOneSetsCarryAndOverflow()
    {
        var harness = new Harness(0xC0, 0xC0, 0x01);   // rol al, 1
        harness.Cpu.State.Gpr[0] = 0x81;

        harness.Step();

        Assert.Equal(0x03UL, harness.Cpu.State.Gpr[0] & 0xFF);
        Assert.True(harness.Cpu.Flags.Carry);
        Assert.True(harness.Cpu.Flags.Overflow);
    }

    /// <summary>C1h /5 ib: the count is masked to five bits at 32 bits, so 33 behaves like 1.</summary>
    [Fact]
    public void ShrThirtyTwoBitMasksCountToFiveBits()
    {
        var harness = new Harness(0xC1, 0xE8, 0x21);   // shr eax, 33
        harness.Cpu.State.Gpr[0] = 0x80000000;

        harness.Step();

        Assert.Equal(0x40000000UL, harness.Cpu.State.Gpr[0]);
        Assert.False(harness.Cpu.Flags.Carry);
        Assert.True(harness.Cpu.Flags.Overflow);
    }

    /// <summary>D3h /4 with REX.W: the count is masked to six bits at 64 bits, so CL = 32 still shifts (a five-bit mask would have reduced it to zero).</summary>
    [Fact]
    public void ShlSixtyFourBitMasksCountToSixBits()
    {
        var harness = new Harness(0x48, 0xD3, 0xE0);   // shl rax, cl
        harness.Cpu.State.Gpr[0] = 1;
        harness.Cpu.State.Gpr[1] = 32;   // cl

        harness.Step();

        Assert.Equal(0x1_00000000UL, harness.Cpu.State.Gpr[0]);
    }

    /// <summary>The same six-bit mask reduces CL = 65 to 1, not zero -- 65 & 3Fh = 1.</summary>
    [Fact]
    public void ShlSixtyFourBitCountSixtyFiveMasksToOne()
    {
        var harness = new Harness(0x48, 0xD3, 0xE0);   // shl rax, cl
        harness.Cpu.State.Gpr[0] = 1;
        harness.Cpu.State.Gpr[1] = 65;

        harness.Step();

        Assert.Equal(2UL, harness.Cpu.State.Gpr[0]);
    }

    /// <summary>A count of zero leaves the operand and every flag exactly as they were.</summary>
    [Fact]
    public void ShiftByZeroLeavesOperandAndFlagsUntouched()
    {
        var harness = new Harness(0x48, 0xD3, 0xE0);   // shl rax, cl
        harness.Cpu.State.Gpr[0] = 0x1234;
        harness.Cpu.State.Gpr[1] = 64;   // masked to zero
        harness.Cpu.Flags.Carry = true;
        harness.Cpu.Flags.Zero = false;

        harness.Step();

        Assert.Equal(0x1234UL, harness.Cpu.State.Gpr[0]);
        Assert.True(harness.Cpu.Flags.Carry);
        Assert.False(harness.Cpu.Flags.Zero);
    }

    /// <summary>0F A4h ib: SHLD EAX, EBX, 8 feeds the top 8 bits of EBX into the bottom of EAX as it shifts left.</summary>
    [Fact]
    public void ShldThirtyTwoBitImmediateFeedsInFromTheSource()
    {
        var harness = new Harness(0x0F, 0xA4, 0xD8, 0x08);   // shld eax, ebx, 8
        harness.Cpu.State.Gpr[0] = 0x00000001;
        harness.Cpu.State.Gpr[3] = 0xFF000000;

        harness.Step();

        Assert.Equal(0x000001FFUL, harness.Cpu.State.Gpr[0]);
        Assert.False(harness.Cpu.Flags.Carry);
        Assert.False(harness.Cpu.Flags.Overflow);
    }

    /// <summary>
    /// 0F ADh /CL, 16-bit: a count above 16 exercises the barrel-shifter
    /// doubling case the legacy path also special-cases -- which returns
    /// before ever computing OF, so it stays cleared (Cpu.Shifts.cs:150-172,
    /// verified against the hardware suite), unlike the ordinary count<=16
    /// case below it, where OF follows the sign-change formula.
    /// </summary>
    [Fact]
    public void ShrdSixteenBitCountAboveSixteenDoublesTheSourceAndClearsOverflow()
    {
        var harness = new Harness(0x66, 0x0F, 0xAD, 0xD0);   // shrd ax, dx, cl
        harness.Cpu.State.Gpr[2] = 0x0001;   // dx (source)
        harness.Cpu.State.Gpr[1] = 17;       // cl

        harness.Step();

        Assert.Equal(0x8000UL, harness.Cpu.State.Gpr[0] & 0xFFFF);
        Assert.True(harness.Cpu.Flags.Carry);
        Assert.False(harness.Cpu.Flags.Overflow);
    }

    /// <summary>D1h /2, REX.W, count 1: RCL RAX, 1 rotates the top bit into CF and CF (initially clear) into bit 0.</summary>
    [Fact]
    public void RclSixtyFourBitRotatesThroughCarry()
    {
        var harness = new Harness(0x48, 0xD1, 0xD0);   // rcl rax, 1
        harness.Cpu.State.Gpr[0] = 0x8000000000000000UL;
        harness.Cpu.Flags.Carry = false;

        harness.Step();

        Assert.Equal(0UL, harness.Cpu.State.Gpr[0]);
        Assert.True(harness.Cpu.Flags.Carry);
        Assert.True(harness.Cpu.Flags.Overflow);
    }

    /// <summary>D1h /3, REX.W, count 1: RCR RAX, 1 rotates CF (initially set) into the top bit and the low bit into CF.</summary>
    [Fact]
    public void RcrSixtyFourBitRotatesThroughCarry()
    {
        var harness = new Harness(0x48, 0xD1, 0xD8);   // rcr rax, 1
        harness.Cpu.State.Gpr[0] = 1;
        harness.Cpu.Flags.Carry = true;

        harness.Step();

        Assert.Equal(0x8000000000000000UL, harness.Cpu.State.Gpr[0]);
        Assert.True(harness.Cpu.Flags.Carry);
        Assert.True(harness.Cpu.Flags.Overflow);
    }

    /// <summary>D1h /7, REX.W, count 1: SAR RAX, 1 on a negative value replicates the sign bit and never sets OF.</summary>
    [Fact]
    public void SarSixtyFourBitReplicatesSignBit()
    {
        var harness = new Harness(0x48, 0xD1, 0xF8);   // sar rax, 1
        harness.Cpu.State.Gpr[0] = 0x8000000000000000UL;
        harness.Cpu.Flags.Overflow = true;   // must end up cleared: SAR never sets OF

        harness.Step();

        Assert.Equal(0xC000000000000000UL, harness.Cpu.State.Gpr[0]);
        Assert.False(harness.Cpu.Flags.Carry);
        Assert.False(harness.Cpu.Flags.Overflow);
    }

    // === bit test (BT/BTS/BTR/BTC) and bit scan (BSF/BSR) =======================

    /// <summary>0F A3h /r: a negative register index displaces a memory operand backwards, testing bit 31 of the dword one slot before it.</summary>
    [Fact]
    public void BtNegativeRegisterOffsetIndexesMemoryBackwards()
    {
        var harness = new Harness(0x0F, 0xA3, 0x0B);   // bt [rbx], ecx
        harness.Cpu.State.Gpr[3] = DataPage + 8;        // rbx: the base address
        harness.Cpu.State.Gpr[1] = 0xFFFFFFFF;          // ecx = -1
        harness.Bus.WriteDword(DataPage + 4, 0x80000000);   // bit 31 of the dword one slot back

        harness.Step();

        Assert.True(harness.Cpu.Flags.Carry);
    }

    /// <summary>0F BAh /5 ib: BTS sets the bit and writes the operand back; CF reports the bit's previous value.</summary>
    [Fact]
    public void BtsImmediateGroupSetsBitAndReportsPreviousValue()
    {
        var harness = new Harness(0x0F, 0xBA, 0xE8, 0x03);   // bts eax, 3
        harness.Cpu.State.Gpr[0] = 0;

        harness.Step();

        Assert.Equal(0x08UL, harness.Cpu.State.Gpr[0]);
        Assert.False(harness.Cpu.Flags.Carry);
    }

    /// <summary>0F B3h /r: BTR clears the bit and reports whatever it held.</summary>
    [Fact]
    public void BtrRegisterFormClearsBitAndReportsPreviousValue()
    {
        var harness = new Harness(0x0F, 0xB3, 0xC8);   // btr eax, ecx
        harness.Cpu.State.Gpr[0] = 0xFF;
        harness.Cpu.State.Gpr[1] = 3;   // bit index

        harness.Step();

        Assert.Equal(0xF7UL, harness.Cpu.State.Gpr[0]);
        Assert.True(harness.Cpu.Flags.Carry);
    }

    /// <summary>0F BBh /r: BTC complements the bit.</summary>
    [Fact]
    public void BtcRegisterFormComplementsBit()
    {
        var harness = new Harness(0x0F, 0xBB, 0xC8);   // btc eax, ecx
        harness.Cpu.State.Gpr[0] = 0;
        harness.Cpu.State.Gpr[1] = 3;   // bit index

        harness.Step();

        Assert.Equal(0x08UL, harness.Cpu.State.Gpr[0]);
        Assert.False(harness.Cpu.Flags.Carry);
    }

    /// <summary>0F BAh with a reg field below 4 (BT has no immediate encoding) is #UD.</summary>
    [Fact]
    public void BitGroupImmediateWithRegBelowFourIsInvalid()
    {
        var harness = new Harness(0x0F, 0xBA, 0xD0, 0x00);   // reg field 2: undefined

        Assert.Equal(InvalidOpcode, harness.FaultVector());
    }

    /// <summary>0F BCh with REX.W: BSF finds the lowest set bit.</summary>
    [Fact]
    public void BsfSixtyFourBitFindsTheLowestSetBit()
    {
        var harness = new Harness(0x48, 0x0F, 0xBC, 0xC3);   // bsf rax, rbx
        harness.Cpu.State.Gpr[3] = 0x40;   // bit 6

        harness.Step();

        Assert.Equal(6UL, harness.Cpu.State.Gpr[0]);
        Assert.False(harness.Cpu.Flags.Zero);
    }

    /// <summary>0F BDh with REX.W: BSR on a zero source leaves the destination unchanged and sets ZF.</summary>
    [Fact]
    public void BsrSixtyFourBitZeroSourceLeavesDestinationUnchanged()
    {
        var harness = new Harness(0x48, 0x0F, 0xBD, 0xC3);   // bsr rax, rbx
        harness.Cpu.State.Gpr[0] = 0xDEAD;
        harness.Cpu.State.Gpr[3] = 0;

        harness.Step();

        Assert.Equal(0xDEADUL, harness.Cpu.State.Gpr[0]);
        Assert.True(harness.Cpu.Flags.Zero);
    }

    // === XADD and CMPXCHG =========================================================

    /// <summary>0F C0h /r: XADD CL, AL exchanges and adds a byte pair.</summary>
    [Fact]
    public void XaddByteExchangesAndAdds()
    {
        var harness = new Harness(0x0F, 0xC0, 0xC8);   // xadd al, cl
        harness.Cpu.State.Gpr[0] = 5;   // al
        harness.Cpu.State.Gpr[1] = 3;   // cl

        harness.Step();

        Assert.Equal(8UL, harness.Cpu.State.Gpr[0] & 0xFF);
        Assert.Equal(5UL, harness.Cpu.State.Gpr[1] & 0xFF);
    }

    /// <summary>0F C1h /r with a memory destination: the destination gets the sum, the source register the destination's old value.</summary>
    [Fact]
    public void XaddThirtyTwoBitToMemoryAddsAndReportsOldValue()
    {
        var harness = new Harness(0x0F, 0xC1, 0x0B);   // xadd [rbx], ecx
        harness.Cpu.State.Gpr[3] = DataPage;
        harness.Bus.WriteDword(DataPage, 5);
        harness.Cpu.State.Gpr[1] = 3;   // ecx

        harness.Step();

        Assert.Equal(8u, harness.Bus.ReadDword(DataPage));
        Assert.Equal(5UL, harness.Cpu.State.Gpr[1]);
    }

    /// <summary>
    /// 0F C1h /r with both operands naming the same register: the SDM's own
    /// operation listing is SRC = DEST then DEST = TEMP, so the sum is what
    /// is left standing even though the "source register" write would
    /// otherwise have clobbered it.
    /// </summary>
    [Fact]
    public void XaddRegisterSelfReferenceLeavesTheSum()
    {
        var harness = new Harness(0x0F, 0xC1, 0xC0);   // xadd eax, eax
        harness.Cpu.State.Gpr[0] = 5;

        harness.Step();

        Assert.Equal(10UL, harness.Cpu.State.Gpr[0]);
    }

    /// <summary>0F B1h /r: a matching CMPXCHG stores the source and sets ZF.</summary>
    [Fact]
    public void CmpxchgThirtyTwoBitSuccessStoresSourceAndSetsZeroFlag()
    {
        var harness = new Harness(0x0F, 0xB1, 0xCB);   // cmpxchg ebx, ecx
        harness.Cpu.State.Gpr[0] = 100;   // eax (accumulator)
        harness.Cpu.State.Gpr[3] = 100;   // ebx (destination)
        harness.Cpu.State.Gpr[1] = 999;   // ecx (source)

        harness.Step();

        Assert.Equal(999UL, harness.Cpu.State.Gpr[3]);
        Assert.Equal(100UL, harness.Cpu.State.Gpr[0]);
        Assert.True(harness.Cpu.Flags.Zero);
    }

    /// <summary>A mismatched CMPXCHG loads the accumulator with what it actually found, and leaves the destination alone.</summary>
    [Fact]
    public void CmpxchgThirtyTwoBitFailureLoadsAccumulatorWithActualValue()
    {
        var harness = new Harness(0x0F, 0xB1, 0xCB);   // cmpxchg ebx, ecx
        harness.Cpu.State.Gpr[0] = 100;
        harness.Cpu.State.Gpr[3] = 5;
        harness.Cpu.State.Gpr[1] = 999;

        harness.Step();

        Assert.Equal(5UL, harness.Cpu.State.Gpr[3]);
        Assert.Equal(5UL, harness.Cpu.State.Gpr[0]);
        Assert.False(harness.Cpu.Flags.Zero);
    }

    /// <summary>0F C7h /1, no REX.W: a matching CMPXCHG8B stores ECX:EBX and sets ZF.</summary>
    /// <summary>
    /// CMPXCHG ebx, ecx that fails leaves the register destination whole --
    /// bits 63:32 of RBX survive -- while EAX takes the destination's value,
    /// zero-extended.
    /// </summary>
    [Fact]
    public void CmpxchgFailureLeavesARegisterDestinationWhole()
    {
        var harness = new Harness(0x0F, 0xB1, 0xCB);        // cmpxchg ebx, ecx
        harness.Cpu.State.Gpr[3] = 0xDEADBEEF_00000005UL;
        harness.Cpu.State.Gpr[0] = 0xFFFFFFFF_00000064UL;   // EAX = 100, not 5
        harness.Cpu.State.Gpr[1] = 7;

        harness.Step();

        Assert.Equal(0xDEADBEEF_00000005UL, harness.Cpu.State.Gpr[3]);
        Assert.Equal(5UL, harness.Cpu.State.Gpr[0]);
        Assert.False(harness.Cpu.Flags.Zero);
    }

    [Fact]
    public void Cmpxchg8BSuccessStoresEcxEbxPair()
    {
        var harness = new Harness(0x0F, 0xC7, 0x0E);   // cmpxchg8b [rsi]
        harness.Cpu.State.Gpr[6] = DataPage;           // rsi: the address
        harness.Bus.WriteDword(DataPage, 0x22222222);      // low dword: matches eax below
        harness.Bus.WriteDword(DataPage + 4, 0x11111111);  // high dword: matches edx below
        harness.Cpu.State.Gpr[2] = 0x11111111;   // edx
        harness.Cpu.State.Gpr[0] = 0x22222222;   // eax
        harness.Cpu.State.Gpr[1] = 0x44444444;   // ecx
        harness.Cpu.State.Gpr[3] = 0x55555555;   // ebx

        harness.Step();

        Assert.True(harness.Cpu.Flags.Zero);
        Assert.Equal(0x55555555u, harness.Bus.ReadDword(DataPage));
        Assert.Equal(0x44444444u, harness.Bus.ReadDword(DataPage + 4));
        Assert.Equal(0x11111111UL, harness.Cpu.State.Gpr[2]);
        Assert.Equal(0x22222222UL, harness.Cpu.State.Gpr[0]);
    }

    /// <summary>A mismatched CMPXCHG8B loads EDX:EAX with the value it actually found and leaves memory unchanged.</summary>
    [Fact]
    public void Cmpxchg8BFailureLoadsEdxEaxWithActualValue()
    {
        var harness = new Harness(0x0F, 0xC7, 0x0E);   // cmpxchg8b [rsi]
        harness.Cpu.State.Gpr[6] = DataPage;
        harness.Bus.WriteDword(DataPage, 0x99999999);
        harness.Bus.WriteDword(DataPage + 4, 0x88888888);
        harness.Cpu.State.Gpr[2] = 0x11111111;
        harness.Cpu.State.Gpr[0] = 0x22222222;
        harness.Cpu.State.Gpr[1] = 0x44444444;
        harness.Cpu.State.Gpr[3] = 0x55555555;

        harness.Step();

        Assert.False(harness.Cpu.Flags.Zero);
        Assert.Equal(0x99999999u, harness.Bus.ReadDword(DataPage));
        Assert.Equal(0x88888888u, harness.Bus.ReadDword(DataPage + 4));
        Assert.Equal(0x88888888UL, harness.Cpu.State.Gpr[2]);
        Assert.Equal(0x99999999UL, harness.Cpu.State.Gpr[0]);
    }

    /// <summary>0F C7h /1, with REX.W: a matching CMPXCHG16B stores RCX:RBX and sets ZF, at a naturally 16-byte-aligned address.</summary>
    [Fact]
    public void Cmpxchg16BSuccessStoresRcxRbxPair()
    {
        var harness = new Harness(0x48, 0x0F, 0xC7, 0x0E);   // cmpxchg16b [rsi]
        harness.Cpu.State.Features = harness.Cpu.State.Features.With(CpuFeature.Cx16);
        harness.Cpu.State.Gpr[6] = DataPage;   // rsi, 16-byte aligned
        harness.WriteQword(DataPage, 0x2222222222222222UL);
        harness.WriteQword(DataPage + 8, 0x1111111111111111UL);
        harness.Cpu.State.Gpr[2] = 0x1111111111111111UL;   // rdx
        harness.Cpu.State.Gpr[0] = 0x2222222222222222UL;   // rax
        harness.Cpu.State.Gpr[1] = 0x3333333333333333UL;   // rcx
        harness.Cpu.State.Gpr[3] = 0x4444444444444444UL;   // rbx

        harness.Step();

        Assert.True(harness.Cpu.Flags.Zero);
        Assert.Equal(0x4444444444444444UL, harness.ReadQword(DataPage));
        Assert.Equal(0x3333333333333333UL, harness.ReadQword(DataPage + 8));
    }

    /// <summary>A mismatched CMPXCHG16B loads RDX:RAX with the value found and leaves memory unchanged.</summary>
    [Fact]
    public void Cmpxchg16BFailureLoadsRdxRaxWithActualValue()
    {
        var harness = new Harness(0x48, 0x0F, 0xC7, 0x0E);
        harness.Cpu.State.Features = harness.Cpu.State.Features.With(CpuFeature.Cx16);
        harness.Cpu.State.Gpr[6] = DataPage;
        harness.WriteQword(DataPage, 0x9999999999999999UL);
        harness.WriteQword(DataPage + 8, 0x8888888888888888UL);
        harness.Cpu.State.Gpr[2] = 0x1111111111111111UL;
        harness.Cpu.State.Gpr[0] = 0x2222222222222222UL;
        harness.Cpu.State.Gpr[1] = 0x3333333333333333UL;
        harness.Cpu.State.Gpr[3] = 0x4444444444444444UL;

        harness.Step();

        Assert.False(harness.Cpu.Flags.Zero);
        Assert.Equal(0x9999999999999999UL, harness.ReadQword(DataPage));
        Assert.Equal(0x8888888888888888UL, harness.Cpu.State.Gpr[2]);
        Assert.Equal(0x9999999999999999UL, harness.Cpu.State.Gpr[0]);
    }

    /// <summary>CMPXCHG16B on an address that is not 16-byte aligned is #GP(0), whether or not the comparison would have matched.</summary>
    [Fact]
    public void Cmpxchg16BMisalignedAddressIsGeneralProtection()
    {
        var harness = new Harness(0x48, 0x0F, 0xC7, 0x0E);
        harness.Cpu.State.Features = harness.Cpu.State.Features.With(CpuFeature.Cx16);
        harness.Cpu.State.Gpr[6] = DataPage + 4;   // not 16-byte aligned

        Assert.Equal(GeneralProtection, harness.FaultVector());
    }

    /// <summary>Without CPUID.CX16 (which the harness's default feature set does not include), CMPXCHG16B is #UD.</summary>
    [Fact]
    public void Cmpxchg16BWithoutCx16FeatureIsInvalid()
    {
        var harness = new Harness(0x48, 0x0F, 0xC7, 0x0E);   // cmpxchg16b [rsi], Cx16 not granted
        harness.Cpu.State.Gpr[6] = DataPage;

        Assert.Equal(InvalidOpcode, harness.FaultVector());
    }

    /// <summary>
    /// M1: the SDM's own operation listing writes the destination back even
    /// on a mismatch (DEST = TEMP in the "else" branch too), so a losing
    /// CMPXCHG against a read-only page still faults -- and, because the
    /// write happens before the flags and the accumulator load, EAX is left
    /// exactly as it was, not loaded with the value the write never got to
    /// deliver. EAX is read inside the fault hook, for the same reason
    /// FaultVectorWithFlags reads Flags there: 64-bit mode's own fault
    /// delivery, still the legacy path over an IDT this harness never sets
    /// up, can go on to touch unrelated registers once EnterInterrupt runs.
    /// </summary>
    [Fact]
    public void CmpxchgMismatchOnReadOnlyPageStillFaults()
    {
        var harness = new Harness(0x0F, 0xB1, 0x0B);   // cmpxchg [rbx], ecx
        harness.MapReadOnly(ReadOnlyPage, ReadOnlyPage);
        harness.Cpu.State.Cr0 |= CpuState.Cr0WriteProtect;
        harness.Cpu.State.Gpr[3] = ReadOnlyPage;
        harness.Bus.WriteDword(ReadOnlyPage, 5);    // does not match eax below
        harness.Cpu.State.Gpr[0] = 100;             // eax (accumulator)
        harness.Cpu.State.Gpr[1] = 999;             // ecx (source)

        byte vector = harness.FaultVectorWithRegister(0, out ulong accumulatorAtFault);

        Assert.Equal(PageFault, vector);
        Assert.Equal(5u, harness.Bus.ReadDword(ReadOnlyPage));
        Assert.Equal(100UL, accumulatorAtFault);
    }

    // === LOCK legality ============================================================

    /// <summary>LOCK ADD [rbx], ecx is legal: the destination is memory and ADD is on the SDM's list.</summary>
    [Fact]
    public void LockIsAllowedInFrontOfAddWithAMemoryDestination()
    {
        var harness = new Harness(0xF0, 0x01, 0x0B);   // lock add [rbx], ecx
        harness.Cpu.State.Gpr[3] = DataPage;
        harness.Bus.WriteDword(DataPage, 5);
        harness.Cpu.State.Gpr[1] = 3;

        Assert.Equal(NoFault, harness.FaultVector());
        Assert.Equal(8u, harness.Bus.ReadDword(DataPage));
    }

    /// <summary>LOCK ADD ecx, eax is #UD: the destination is a register, so there is nothing to make atomic.</summary>
    [Fact]
    public void LockIsInvalidInFrontOfAddWithARegisterDestination()
    {
        var harness = new Harness(0xF0, 0x01, 0xC1);   // lock add ecx, eax

        Assert.Equal(InvalidOpcode, harness.FaultVector());
    }

    /// <summary>LOCK CMP [rbx], ecx is #UD: CMP writes nothing back, memory destination or not.</summary>
    [Fact]
    public void LockIsInvalidInFrontOfCmp()
    {
        var harness = new Harness(0xF0, 0x39, 0x0B);   // lock cmp [rbx], ecx
        harness.Cpu.State.Gpr[3] = DataPage;

        Assert.Equal(InvalidOpcode, harness.FaultVector());
    }

    /// <summary>LOCK BTS [rbx], ecx is legal: BTS is on the SDM's list, unlike BT.</summary>
    [Fact]
    public void LockIsAllowedInFrontOfBtsOnA0FOpcode()
    {
        var harness = new Harness(0xF0, 0x0F, 0xAB, 0x0B);   // lock bts [rbx], ecx
        harness.Cpu.State.Gpr[3] = DataPage;
        harness.Bus.WriteDword(DataPage, 0);
        harness.Cpu.State.Gpr[1] = 3;

        Assert.Equal(NoFault, harness.FaultVector());
        Assert.Equal(0x08u, harness.Bus.ReadDword(DataPage));
    }

    /// <summary>LOCK BT [rbx], ecx is #UD: BT only ever reads, so a 0Fh opcode can be excluded from the list too.</summary>
    [Fact]
    public void LockIsInvalidInFrontOfBtOnA0FOpcode()
    {
        var harness = new Harness(0xF0, 0x0F, 0xA3, 0x0B);   // lock bt [rbx], ecx
        harness.Cpu.State.Gpr[3] = DataPage;

        Assert.Equal(InvalidOpcode, harness.FaultVector());
    }

    /// <summary>LOCK CMPXCHG [rbx], ecx is legal: it exists to be locked.</summary>
    [Fact]
    public void LockIsAllowedInFrontOfCmpxchgOnA0FOpcode()
    {
        var harness = new Harness(0xF0, 0x0F, 0xB1, 0x0B);   // lock cmpxchg [rbx], ecx
        harness.Cpu.State.Gpr[3] = DataPage;
        harness.Bus.WriteDword(DataPage, 100);
        harness.Cpu.State.Gpr[0] = 100;   // eax matches, so the store branch runs
        harness.Cpu.State.Gpr[1] = 999;

        Assert.Equal(NoFault, harness.FaultVector());
        Assert.Equal(999u, harness.Bus.ReadDword(DataPage));
    }

    /// <summary>LOCK NEG [rbx] is legal: NEG is one of group 3's two locked forms.</summary>
    [Fact]
    public void LockIsAllowedInFrontOfNegOnF7()
    {
        var harness = new Harness(0xF0, 0xF7, 0x1B);   // lock neg dword [rbx]
        harness.Cpu.State.Gpr[3] = DataPage;
        harness.Bus.WriteDword(DataPage, 1);

        Assert.Equal(NoFault, harness.FaultVector());
        Assert.Equal(0xFFFFFFFFu, harness.Bus.ReadDword(DataPage));
    }

    /// <summary>LOCK TEST [rbx], imm8 is #UD: TEST never writes back, so it is excluded from group 3's locked forms.</summary>
    [Fact]
    public void LockIsInvalidInFrontOfTestOnF6()
    {
        var harness = new Harness(0xF0, 0xF6, 0x03, 0x00);   // lock test byte [rbx], 0
        harness.Cpu.State.Gpr[3] = DataPage;

        Assert.Equal(InvalidOpcode, harness.FaultVector());
    }

    /// <summary>LOCK INC [rbx] is legal: group 4's own locked form.</summary>
    [Fact]
    public void LockIsAllowedInFrontOfIncOnFe()
    {
        var harness = new Harness(0xF0, 0xFE, 0x03);   // lock inc byte [rbx]
        harness.Cpu.State.Gpr[3] = DataPage;
        harness.Bus.WriteDword(DataPage, 5);

        Assert.Equal(NoFault, harness.FaultVector());
        Assert.Equal(6u, harness.Bus.ReadDword(DataPage) & 0xFF);
    }

    // === RIP-relative operands ====================================================

    /// <summary>83h /0 ib with a RIP-relative operand: the displacement counts from the end of the instruction, past the trailing imm8.</summary>
    [Fact]
    public void RipRelativeGroup1WithAnImm8Byte()
    {
        // add dword [rip + 3FF9h], 5: 7 bytes, so the target is 1007h + 3FF9h = 5000h.
        var harness = new Harness(0x83, 0x05, 0xF9, 0x3F, 0x00, 0x00, 0x05);
        harness.Bus.WriteDword(DataPage, 10);

        harness.Step();

        Assert.Equal(15u, harness.Bus.ReadDword(DataPage));
    }

    /// <summary>81h /0 id with a RIP-relative operand: the displacement counts past the trailing imm32 too.</summary>
    [Fact]
    public void RipRelativeGroup1WithAnImm32Dword()
    {
        // add dword [rip + 3FF6h], 1: 10 bytes, so the target is 100Ah + 3FF6h = 5000h.
        var harness = new Harness(0x81, 0x05, 0xF6, 0x3F, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00);
        harness.Bus.WriteDword(DataPage, 0x0FFFFFFF);

        harness.Step();

        Assert.Equal(0x10000000u, harness.Bus.ReadDword(DataPage));
    }

    /// <summary>F7h /0 id with a RIP-relative operand: group 3's own TEST form also resolves RIP-relative addressing, with the immediate counted into the instruction length.</summary>
    [Fact]
    public void TestF7WithRipRelativeOperand()
    {
        // test dword [rip + 3FF6h], 0Fh: 10 bytes, so the target is 100Ah + 3FF6h = 5000h.
        var harness = new Harness(0xF7, 0x05, 0xF6, 0x3F, 0x00, 0x00, 0x0F, 0x00, 0x00, 0x00);
        harness.Bus.WriteDword(DataPage, 0x000000F0);   // no bits in common with 0Fh

        harness.Step();

        Assert.Equal(0x000000F0u, harness.Bus.ReadDword(DataPage));   // TEST never writes
        Assert.True(harness.Cpu.Flags.Zero);
    }

    // === fault safety: a read-only destination must leave state as it was =======
    //
    // Every read-modify-write instruction here targets ReadOnlyPage with
    // CR0.WP set, so the write inside it raises #PF (Cpu.Paging.LongMode.cs's
    // CheckPagePermissions): the state the faulting instruction leaves has to
    // be exactly what it was before (AGENTS.md's critical invariants), which
    // for these instructions means memory untouched and every flag exactly
    // as this test set it, not partway updated. Flags are read through
    // FaultVectorWithFlags, which snapshots them at the moment
    // ProcessorException fires -- before 64-bit mode's own fault delivery
    // (still the legacy path, on an IDT this harness never sets up) can go
    // on to disturb RFLAGS for reasons that have nothing to do with the
    // instruction under test.

    /// <summary>ADC [rbx], ecx: CF is read for the incoming carry, which must not itself commit CF before the write succeeds.</summary>
    [Fact]
    public void AdcMemoryDestinationFaultLeavesFlagsUnchanged()
    {
        var harness = new Harness(0x11, 0x0B);   // adc [rbx], ecx
        harness.MapReadOnly(ReadOnlyPage, ReadOnlyPage);
        harness.Cpu.State.Cr0 |= CpuState.Cr0WriteProtect;
        harness.Cpu.State.Gpr[3] = ReadOnlyPage;
        harness.Cpu.State.Gpr[1] = 3;
        harness.Bus.WriteDword(ReadOnlyPage, 5);
        harness.Cpu.Flags.Carry = true;
        harness.Cpu.Flags.Zero = false;

        byte vector = harness.FaultVectorWithFlags(out Flags flagsAtFault);

        Assert.Equal(PageFault, vector);
        Assert.Equal(5u, harness.Bus.ReadDword(ReadOnlyPage));
        Assert.NotEqual(Flags.None, flagsAtFault & Flags.Carry);
        Assert.Equal(Flags.None, flagsAtFault & Flags.Zero);
    }

    /// <summary>SBB [rbx], ecx: the same incoming-carry hazard as ADC, on the subtract side.</summary>
    [Fact]
    public void SbbMemoryDestinationFaultLeavesFlagsUnchanged()
    {
        var harness = new Harness(0x19, 0x0B);   // sbb [rbx], ecx
        harness.MapReadOnly(ReadOnlyPage, ReadOnlyPage);
        harness.Cpu.State.Cr0 |= CpuState.Cr0WriteProtect;
        harness.Cpu.State.Gpr[3] = ReadOnlyPage;
        harness.Cpu.State.Gpr[1] = 3;
        harness.Bus.WriteDword(ReadOnlyPage, 5);
        harness.Cpu.Flags.Carry = false;
        harness.Cpu.Flags.Zero = true;

        byte vector = harness.FaultVectorWithFlags(out Flags flagsAtFault);

        Assert.Equal(PageFault, vector);
        Assert.Equal(5u, harness.Bus.ReadDword(ReadOnlyPage));
        Assert.Equal(Flags.None, flagsAtFault & Flags.Carry);
        Assert.NotEqual(Flags.None, flagsAtFault & Flags.Zero);
    }

    /// <summary>RCL [rbx], 1: the rotation reads and updates a running copy of CF, which must not reach Cpu.Flags until the write succeeds.</summary>
    [Fact]
    public void RclMemoryDestinationFaultLeavesFlagsUnchanged()
    {
        var harness = new Harness(0xD1, 0x13);   // rcl dword [rbx], 1
        harness.MapReadOnly(ReadOnlyPage, ReadOnlyPage);
        harness.Cpu.State.Cr0 |= CpuState.Cr0WriteProtect;
        harness.Cpu.State.Gpr[3] = ReadOnlyPage;
        harness.Bus.WriteDword(ReadOnlyPage, 1);
        harness.Cpu.Flags.Carry = false;

        byte vector = harness.FaultVectorWithFlags(out Flags flagsAtFault);

        Assert.Equal(PageFault, vector);
        Assert.Equal(1u, harness.Bus.ReadDword(ReadOnlyPage));
        Assert.Equal(Flags.None, flagsAtFault & Flags.Carry);
    }

    /// <summary>RCR [rbx], 1: same as RCL, rotating the other way.</summary>
    [Fact]
    public void RcrMemoryDestinationFaultLeavesFlagsUnchanged()
    {
        var harness = new Harness(0xD1, 0x1B);   // rcr dword [rbx], 1
        harness.MapReadOnly(ReadOnlyPage, ReadOnlyPage);
        harness.Cpu.State.Cr0 |= CpuState.Cr0WriteProtect;
        harness.Cpu.State.Gpr[3] = ReadOnlyPage;
        harness.Bus.WriteDword(ReadOnlyPage, 1);
        harness.Cpu.Flags.Carry = true;

        byte vector = harness.FaultVectorWithFlags(out Flags flagsAtFault);

        Assert.Equal(PageFault, vector);
        Assert.Equal(1u, harness.Bus.ReadDword(ReadOnlyPage));
        Assert.NotEqual(Flags.None, flagsAtFault & Flags.Carry);
    }

    /// <summary>SHL [rbx], 1: a shift's flags (ZF/SF/PF baseline plus CF/OF) must all wait for the write.</summary>
    [Fact]
    public void ShlMemoryDestinationFaultLeavesFlagsUnchanged()
    {
        var harness = new Harness(0xD1, 0x23);   // shl dword [rbx], 1
        harness.MapReadOnly(ReadOnlyPage, ReadOnlyPage);
        harness.Cpu.State.Cr0 |= CpuState.Cr0WriteProtect;
        harness.Cpu.State.Gpr[3] = ReadOnlyPage;
        harness.Bus.WriteDword(ReadOnlyPage, 0x80000000);
        harness.Cpu.Flags.Carry = false;
        harness.Cpu.Flags.Zero = true;

        byte vector = harness.FaultVectorWithFlags(out Flags flagsAtFault);

        Assert.Equal(PageFault, vector);
        Assert.Equal(0x80000000u, harness.Bus.ReadDword(ReadOnlyPage));
        Assert.Equal(Flags.None, flagsAtFault & Flags.Carry);
        Assert.NotEqual(Flags.None, flagsAtFault & Flags.Zero);
    }

    /// <summary>NEG [rbx]: computes the result before the write, but must not commit CF/OF/AF before it succeeds.</summary>
    [Fact]
    public void NegMemoryDestinationFaultLeavesFlagsUnchanged()
    {
        var harness = new Harness(0xF7, 0x1B);   // neg dword [rbx]
        harness.MapReadOnly(ReadOnlyPage, ReadOnlyPage);
        harness.Cpu.State.Cr0 |= CpuState.Cr0WriteProtect;
        harness.Cpu.State.Gpr[3] = ReadOnlyPage;
        harness.Bus.WriteDword(ReadOnlyPage, 1);
        harness.Cpu.Flags.Carry = false;

        byte vector = harness.FaultVectorWithFlags(out Flags flagsAtFault);

        Assert.Equal(PageFault, vector);
        Assert.Equal(1u, harness.Bus.ReadDword(ReadOnlyPage));
        Assert.Equal(Flags.None, flagsAtFault & Flags.Carry);
    }

    /// <summary>INC [rbx]: group 5's own memory form must not commit CF/OF/AF before the write succeeds.</summary>
    [Fact]
    public void IncMemoryDestinationFaultLeavesFlagsUnchanged()
    {
        var harness = new Harness(0xFF, 0x03);   // inc dword [rbx]
        harness.MapReadOnly(ReadOnlyPage, ReadOnlyPage);
        harness.Cpu.State.Cr0 |= CpuState.Cr0WriteProtect;
        harness.Cpu.State.Gpr[3] = ReadOnlyPage;
        harness.Bus.WriteDword(ReadOnlyPage, 5);
        harness.Cpu.Flags.Carry = true;

        byte vector = harness.FaultVectorWithFlags(out Flags flagsAtFault);

        Assert.Equal(PageFault, vector);
        Assert.Equal(5u, harness.Bus.ReadDword(ReadOnlyPage));
        Assert.NotEqual(Flags.None, flagsAtFault & Flags.Carry);
    }

    /// <summary>
    /// XADD [rbx], ecx: the memory write has to come first, or ECX would
    /// pick up the old destination value even though the write that was
    /// supposed to justify it never happened. ECX is read inside the fault
    /// hook (FaultVectorWithRegister), for the same reason the CMPXCHG
    /// mismatch test above does.
    /// </summary>
    [Fact]
    public void XaddMemoryDestinationFaultLeavesRegisterUnchanged()
    {
        var harness = new Harness(0x0F, 0xC1, 0x0B);   // xadd [rbx], ecx
        harness.MapReadOnly(ReadOnlyPage, ReadOnlyPage);
        harness.Cpu.State.Cr0 |= CpuState.Cr0WriteProtect;
        harness.Cpu.State.Gpr[3] = ReadOnlyPage;
        harness.Cpu.State.Gpr[1] = 3;   // ecx: must survive unchanged
        harness.Bus.WriteDword(ReadOnlyPage, 5);

        byte vector = harness.FaultVectorWithRegister(1, out ulong ecxAtFault);

        Assert.Equal(PageFault, vector);
        Assert.Equal(5u, harness.Bus.ReadDword(ReadOnlyPage));
        Assert.Equal(3UL, ecxAtFault);
    }

    /// <summary>BTS [rbx], ecx: CF (the bit's previous value) must not be committed before the write succeeds.</summary>
    [Fact]
    public void BtsMemoryDestinationFaultLeavesFlagsUnchanged()
    {
        var harness = new Harness(0x0F, 0xAB, 0x0B);   // bts [rbx], ecx
        harness.MapReadOnly(ReadOnlyPage, ReadOnlyPage);
        harness.Cpu.State.Cr0 |= CpuState.Cr0WriteProtect;
        harness.Cpu.State.Gpr[3] = ReadOnlyPage;
        harness.Cpu.State.Gpr[1] = 0;   // bit 0: already set below, so CF would become 1 if committed
        harness.Bus.WriteDword(ReadOnlyPage, 1);
        harness.Cpu.Flags.Carry = false;

        byte vector = harness.FaultVectorWithFlags(out Flags flagsAtFault);

        Assert.Equal(PageFault, vector);
        Assert.Equal(1u, harness.Bus.ReadDword(ReadOnlyPage));
        Assert.Equal(Flags.None, flagsAtFault & Flags.Carry);
    }

    // === harness ==================================================================

    /// <summary>
    /// A bare CPU in 64-bit mode with the code page, the data page and the
    /// stack page identity-mapped; the counterpart of LongModeDecoderTests'
    /// own Harness, trimmed to what these tests need.
    /// </summary>
    private sealed class Harness
    {
        private const uint Pml4Address = 0x20000;
        private const ulong Present = 1UL << 0;
        private const ulong ReadWrite = 1UL << 1;
        private const ulong UserSupervisor = 1UL << 2;
        private const ulong TableFlags = Present | ReadWrite | UserSupervisor;
        private const ulong ReadOnlyLeafFlags = Present | UserSupervisor;

        private uint _nextTable = Pml4Address + 0x1000;

        public Cpu Cpu { get; }
        public PhysicalMemoryBus Bus { get; }

        public Harness(params byte[] code)
        {
            Bus = new PhysicalMemoryBus();
            Cpu = new Cpu(Bus);
            foreach (SegReg segment in Enum.GetValues<SegReg>())
            {
                Cpu.State.LoadSegmentRealMode(segment, 0);
            }

            Cpu.State.SelectModel(CpuModel.PentiumII);
            Cpu.State.Features = Cpu.State.Features.With(CpuFeature.Pae, CpuFeature.LongMode, CpuFeature.Nx);

            Map(CodeBase, CodeBase);
            Map(DataPage, DataPage);
            Map(0x7000, 0x7000);

            Cpu.State.Cr3 = Pml4Address;
            Cpu.State.Cr4 |= CpuState.Cr4Pae;
            Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable | CpuState.Cr0Paging;
            Cpu.State.Efer = CpuState.EferLongModeEnable | CpuState.EferLongModeActive;
            Cpu.State.Segments[(int)SegReg.Cs].Long = true;
            Cpu.State.Rip = CodeBase;
            Cpu.State.Gpr[4] = 0x8000;
            Cpu.FlushTlb();

            code.CopyTo(Bus.GetRamSpan(CodeBase, code.Length));
        }

        public void Step() => Cpu.Step();

        /// <summary>Runs one instruction and returns the first exception vector it raised, or 0xFF.</summary>
        public byte FaultVector()
        {
            byte vector = NoFault;
            Cpu.ProcessorException += (raised, _, _, _) =>
            {
                if (vector == NoFault)
                {
                    vector = raised;
                }
            };

            Cpu.Step();
            return vector;
        }

        /// <summary>
        /// Runs one instruction and returns the first exception vector it
        /// raised, together with the flags exactly as they stood at that
        /// moment -- read inside the ProcessorException hook, which fires
        /// at the top of RaiseException (Cpu.Ops.cs) before EnterInterrupt
        /// does anything. Reading Flags afterwards instead would also see
        /// whatever 64-bit mode's own fault delivery goes on to do to
        /// RFLAGS on top of an IDT this harness never sets up (the C1
        /// review's already-recorded gap, deferred to stage D), which has
        /// nothing to do with the instruction under test.
        /// </summary>
        public byte FaultVectorWithFlags(out Flags flagsAtFault)
        {
            byte vector = NoFault;
            Flags snapshot = default;
            Cpu.ProcessorException += (raised, _, _, _) =>
            {
                if (vector == NoFault)
                {
                    vector = raised;
                    snapshot = Cpu.Flags.Stored;
                }
            };

            Cpu.Step();
            flagsAtFault = snapshot;
            return vector;
        }

        /// <summary>
        /// The same snapshot-at-fault-time idea as FaultVectorWithFlags, for
        /// one general register: some fault delivery paths that follow a
        /// #PF go on to touch GPRs unrelated to the instruction under test
        /// once EnterInterrupt runs (the same legacy-path gap
        /// FaultVectorWithFlags documents), so a register that must stay
        /// untouched has to be read inside the hook too, not after Step
        /// returns.
        /// </summary>
        public byte FaultVectorWithRegister(int registerIndex, out ulong registerAtFault)
        {
            byte vector = NoFault;
            ulong snapshot = 0;
            Cpu.ProcessorException += (raised, _, _, _) =>
            {
                if (vector == NoFault)
                {
                    vector = raised;
                    snapshot = Cpu.State.Gpr[registerIndex];
                }
            };

            Cpu.Step();
            registerAtFault = snapshot;
            return vector;
        }

        /// <summary>Maps one 4 KB page, creating whatever PDPT, PD and PT it needs.</summary>
        public void Map(ulong linear, uint frame)
        {
            uint pdpt = NextLevel(Pml4Address, (linear >> 39) & 0x1FF);
            uint pd = NextLevel(pdpt, (linear >> 30) & 0x1FF);
            uint pt = NextLevel(pd, (linear >> 21) & 0x1FF);
            WriteQword(pt + (uint)((linear >> 12) & 0x1FF) * 8, frame | TableFlags);
            Cpu.FlushTlb();
        }

        /// <summary>Maps one 4 KB page without the writable bit, for the read-only-page fault tests.</summary>
        public void MapReadOnly(ulong linear, uint frame)
        {
            uint pdpt = NextLevel(Pml4Address, (linear >> 39) & 0x1FF);
            uint pd = NextLevel(pdpt, (linear >> 30) & 0x1FF);
            uint pt = NextLevel(pd, (linear >> 21) & 0x1FF);
            WriteQword(pt + (uint)((linear >> 12) & 0x1FF) * 8, frame | ReadOnlyLeafFlags);
            Cpu.FlushTlb();
        }

        public void WriteQword(uint address, ulong value)
        {
            Bus.WriteDword(address, (uint)value);
            Bus.WriteDword(address + 4, (uint)(value >> 32));
        }

        public ulong ReadQword(uint address) =>
            Bus.ReadDword(address) | ((ulong)Bus.ReadDword(address + 4) << 32);

        private uint NextLevel(uint table, ulong index)
        {
            uint entryAddress = table + (uint)index * 8;
            ulong entry = ReadQword(entryAddress);
            if ((entry & Present) != 0)
            {
                return (uint)(entry & ~0xFFFUL);
            }

            uint next = _nextTable;
            _nextTable += 0x1000;
            WriteQword(entryAddress, next | TableFlags);
            return next;
        }
    }
}
