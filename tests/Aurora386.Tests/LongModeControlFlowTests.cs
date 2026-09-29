using Aurora386.Core;
using Aurora386.Core.Cpu;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// Long mode, stage C2b (plans/long-mode.md): the stack, control-flow,
/// conditional and miscellaneous instructions of 64-bit mode -- PUSH/POP and
/// their relatives, PUSHF/POPF, ENTER/LEAVE, the near branches (JMP, CALL,
/// RET, Jcc, LOOP, JRCXZ), SETcc/CMOVcc, MOVZX/MOVSX/BSWAP, CBW/CWD and their
/// wider forms, the flag instructions, and CPUID/RDTSC/RDMSR/CLTS/multi-byte
/// NOP -- implemented in Cpu.LongMode.Control.cs.
///
/// The harness is LongModeAluTests' own, trimmed the same way: a Pentium II
/// given long mode by hand, put straight into 64-bit mode, with the code
/// page, one data page (5000h) and a stack page (7000h-7FFFh, under an
/// initial RSP of 8000h) identity-mapped.
///
/// These tests assume Cpu.LongMode.Execute.cs's default branches call
/// TryExecuteControl64/TryExecuteControlTwoByte64 after
/// TryExecuteAlu64/TryExecuteAluTwoByte64 decline an opcode -- the wiring
/// stage C2b's contract hands to the owner (see the handoff report for the
/// exact two call sites and the diff self-verified against a scratch copy).
/// </summary>
public class LongModeControlFlowTests
{
    private const uint CodeBase = 0x1000;
    private const uint DataPage = 0x5000;
    // Inside the harness's single mapped stack page (7000h-7FFFh) with plenty
    // of room both below (for a push) and at the pointer itself (for a pop
    // that reads before RSP moves).
    private const ulong InitialRsp = 0x7800;
    private const byte InvalidOpcode = 6;
    private const byte GeneralProtection = 13;
    private const byte StackFault = 12;
    private const byte NoFault = 0xFF;

    // === PUSH/POP r64 (50h-5Fh) ===================================================

    /// <summary>53h: PUSH RBX writes the register below the old RSP and moves RSP down by eight.</summary>
    [Fact]
    public void PushRegisterWritesBelowOldStackPointer()
    {
        var harness = new Harness(0x53);   // push rbx
        harness.Cpu.State.Gpr[3] = 0x1234567890ABCDEFUL;

        harness.Step();

        Assert.Equal(InitialRsp - 8, harness.Cpu.State.Gpr[4]);
        Assert.Equal(0x1234567890ABCDEFUL, harness.ReadQword((uint)(InitialRsp - 8)));
    }

    /// <summary>50h with REX.B: PUSH R8.</summary>
    [Fact]
    public void PushWithRexBReachesR8()
    {
        var harness = new Harness(0x41, 0x50);   // push r8
        harness.Cpu.State.Gpr[8] = 0xAAAABBBBCCCCDDDDUL;

        harness.Step();

        Assert.Equal(0xAAAABBBBCCCCDDDDUL, harness.ReadQword((uint)(InitialRsp - 8)));
    }

    /// <summary>66h + 53h: PUSH BX pushes only two bytes and moves RSP by two, not eight.</summary>
    [Fact]
    public void PushSixteenBitOperandMovesStackByTwo()
    {
        var harness = new Harness(0x66, 0x53);   // push bx
        harness.Cpu.State.Gpr[3] = 0x1234_5678;

        harness.Step();

        Assert.Equal(InitialRsp - 2, harness.Cpu.State.Gpr[4]);
        Assert.Equal(0x5678u, harness.Bus.ReadWord((uint)(InitialRsp - 2)));
    }

    /// <summary>54h: PUSH RSP pushes RSP's value from before the instruction, not the already-decremented one.</summary>
    [Fact]
    public void PushRspPushesTheOldValue()
    {
        var harness = new Harness(0x54);   // push rsp

        harness.Step();

        Assert.Equal(InitialRsp - 8, harness.Cpu.State.Gpr[4]);
        Assert.Equal(InitialRsp, harness.ReadQword((uint)(InitialRsp - 8)));
    }

    /// <summary>59h: POP RCX reads the qword at RSP and advances RSP by eight.</summary>
    [Fact]
    public void PopRegisterReadsAndAdvancesStackPointer()
    {
        var harness = new Harness(0x59);   // pop rcx
        harness.WriteQword((uint)InitialRsp, 0xFEEDFACECAFEBEEFUL);

        harness.Step();

        Assert.Equal(0xFEEDFACECAFEBEEFUL, harness.Cpu.State.Gpr[1]);
        Assert.Equal(InitialRsp + 8, harness.Cpu.State.Gpr[4]);
    }

    /// <summary>5Ch: POP RSP -- the popped value wins over the address PopValue64 computed, so RSP ends at the popped value, not InitialRsp+8.</summary>
    [Fact]
    public void PopRspTheValuePoppedWins()
    {
        var harness = new Harness(0x5C);   // pop rsp
        harness.WriteQword((uint)InitialRsp, 0x9000UL);
        harness.Map(0x9000, 0x9000);

        harness.Step();

        Assert.Equal(0x9000UL, harness.Cpu.State.Gpr[4]);
    }

    // === PUSH imm (68h/6Ah), POP r/m (8Fh /0), FFh /6 PUSH r/m ====================

    /// <summary>68h: PUSH imm32 sign-extends 0FFFFFFFFh to a 64-bit -1.</summary>
    [Fact]
    public void PushImmediateThirtyTwoBitSignExtendsToSixtyFour()
    {
        var harness = new Harness(0x68, 0xFF, 0xFF, 0xFF, 0xFF);   // push 0FFFFFFFFh

        harness.Step();

        Assert.Equal(0xFFFFFFFFFFFFFFFFUL, harness.ReadQword((uint)(InitialRsp - 8)));
    }

    /// <summary>6Ah: PUSH imm8 sign-extends 80h to a 64-bit -128.</summary>
    [Fact]
    public void PushImmediateByteSignExtends()
    {
        var harness = new Harness(0x6A, 0x80);   // push -128

        harness.Step();

        Assert.Equal(0xFFFFFFFFFFFFFF80UL, harness.ReadQword((uint)(InitialRsp - 8)));
    }

    /// <summary>8Fh /0, mod=00: POP [RBX] writes the popped value to memory and still advances RSP.</summary>
    [Fact]
    public void PopOperandToMemoryWritesThroughModRm()
    {
        var harness = new Harness(0x8F, 0x03);   // pop [rbx]
        harness.Cpu.State.Gpr[3] = DataPage;
        harness.WriteQword((uint)InitialRsp, 0x0102030405060708UL);

        harness.Step();

        Assert.Equal(0x0102030405060708UL, harness.ReadQword(DataPage));
        Assert.Equal(InitialRsp + 8, harness.Cpu.State.Gpr[4]);
    }

    /// <summary>FFh /6, register form: PUSH RAX through group 5 rather than the 50h-57h encoding.</summary>
    [Fact]
    public void Ff6PushesARegisterOperand()
    {
        var harness = new Harness(0xFF, 0xF0);   // push rax (group 5, /6)
        harness.Cpu.State.Gpr[0] = 0xDEADBEEFUL;

        harness.Step();

        Assert.Equal(0xDEADBEEFUL, harness.ReadQword((uint)(InitialRsp - 8)));
    }

    // === CALL/RET round trips, RET imm16 ==========================================

    /// <summary>E8h then C3h: CALL rel32 pushes the return address and jumps; RET pops it back and restores RSP.</summary>
    [Fact]
    public void CallThenRetRoundTrips()
    {
        // call +0x0B (from CodeBase+5 to CodeBase+0x10), filler, ret.
        var harness = new Harness(
            0xE8, 0x0B, 0x00, 0x00, 0x00,                           // 0: call CodeBase+0x10
            0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90,   // 5-15: filler (11 bytes)
            0xC3);                                                   // 0x10: ret

        harness.Step();   // call

        Assert.Equal(CodeBase + 0x10, harness.Cpu.State.Rip);
        Assert.Equal(InitialRsp - 8, harness.Cpu.State.Gpr[4]);
        Assert.Equal((ulong)(CodeBase + 5), harness.ReadQword((uint)(InitialRsp - 8)));

        harness.Step();   // ret

        Assert.Equal(CodeBase + 5, harness.Cpu.State.Rip);
        Assert.Equal(InitialRsp, harness.Cpu.State.Gpr[4]);
    }

    /// <summary>C2h: RET imm16 pops the return address and then discards four more bytes of arguments off the stack.</summary>
    [Fact]
    public void RetImmediateAlsoDiscardsStackArguments()
    {
        var harness = new Harness(0xC2, 0x04, 0x00);   // ret 4
        harness.WriteQword((uint)InitialRsp, CodeBase + 0x40);

        harness.Step();

        Assert.Equal(CodeBase + 0x40, harness.Cpu.State.Rip);
        Assert.Equal(InitialRsp + 8 + 4, harness.Cpu.State.Gpr[4]);
    }

    /// <summary>FFh /2, register form: CALL RAX pushes the return address and jumps to RAX.</summary>
    [Fact]
    public void IndirectCallThroughRegisterPushesAndJumps()
    {
        var harness = new Harness(0xFF, 0xD0);   // call rax (group 5, /2)
        harness.Cpu.State.Gpr[0] = CodeBase + 0x40;

        harness.Step();

        Assert.Equal(CodeBase + 0x40, harness.Cpu.State.Rip);
        Assert.Equal((ulong)(CodeBase + 2), harness.ReadQword((uint)(InitialRsp - 8)));
    }

    /// <summary>FFh /4, register form: JMP RAX -- no push, RIP simply becomes RAX.</summary>
    [Fact]
    public void IndirectJumpThroughRegisterMovesRip()
    {
        var harness = new Harness(0xFF, 0xE0);   // jmp rax (group 5, /4)
        harness.Cpu.State.Gpr[0] = CodeBase + 0x40;

        harness.Step();

        Assert.Equal(CodeBase + 0x40, harness.Cpu.State.Rip);
        Assert.Equal(InitialRsp, harness.Cpu.State.Gpr[4]);   // no stack effect
    }

    // === Jcc forward and back, LOOP, JRCXZ =========================================

    /// <summary>74h (JZ) rel8, forward: the branch lands two bytes past the end of the two-byte instruction.</summary>
    [Fact]
    public void JccForwardTakesTheBranchWhenConditionHolds()
    {
        var harness = new Harness(0x74, 0x02);   // jz +2
        harness.Cpu.Flags.Zero = true;

        harness.Step();

        Assert.Equal(CodeBase + 2 + 2, harness.Cpu.State.Rip);
    }

    /// <summary>75h (JNZ) rel8, not taken: RIP simply falls through to the next instruction.</summary>
    [Fact]
    public void JccNotTakenFallsThrough()
    {
        var harness = new Harness(0x75, 0x02);   // jnz +2
        harness.Cpu.Flags.Zero = true;

        harness.Step();

        Assert.Equal(CodeBase + 2, harness.Cpu.State.Rip);
    }

    /// <summary>75h (JNZ) rel8, backward: a negative displacement lands before the instruction itself.</summary>
    [Fact]
    public void JccBackwardTakesANegativeDisplacement()
    {
        var harness = new Harness(0x90, 0x90, 0x90, 0x90, 0x90, 0x75, 0xF9);   // at +5: jnz -7
        harness.Cpu.State.Rip = CodeBase + 5;
        harness.Cpu.Flags.Zero = false;

        harness.Step();

        Assert.Equal(CodeBase, harness.Cpu.State.Rip);
    }

    /// <summary>0F 84h: JZ rel32, the two-byte-opcode long form.</summary>
    [Fact]
    public void JccRel32TwoByteFormTakesTheBranch()
    {
        var harness = new Harness(0x0F, 0x84, 0x10, 0x00, 0x00, 0x00);   // jz +0x10
        harness.Cpu.Flags.Zero = true;

        harness.Step();

        Assert.Equal(CodeBase + 6 + 0x10, harness.Cpu.State.Rip);
    }

    /// <summary>E2h: LOOP decrements RCX, and a nonzero result takes the branch back to the instruction itself.</summary>
    [Fact]
    public void LoopDecrementsRcxAndBranchesWhileNonzero()
    {
        var harness = new Harness(0xE2, 0xFE);   // loop -2 (back onto itself)
        harness.Cpu.State.Gpr[1] = 5;

        harness.Step();

        Assert.Equal(4UL, harness.Cpu.State.Gpr[1]);
        Assert.Equal(CodeBase, harness.Cpu.State.Rip);
    }

    /// <summary>E2h: LOOP stops branching once RCX reaches zero.</summary>
    [Fact]
    public void LoopStopsWhenRcxReachesZero()
    {
        var harness = new Harness(0xE2, 0xFE);   // loop -2
        harness.Cpu.State.Gpr[1] = 1;

        harness.Step();

        Assert.Equal(0UL, harness.Cpu.State.Gpr[1]);
        Assert.Equal(CodeBase + 2, harness.Cpu.State.Rip);
    }

    /// <summary>67h + E2h: LOOP counts ECX, not RCX, and zero-extends the decremented value into RCX's upper half.</summary>
    [Fact]
    public void LoopWithAddressSizePrefixCountsEcx()
    {
        var harness = new Harness(0x67, 0xE2, 0xFD);   // loop -3, 32-bit counter
        harness.Cpu.State.Gpr[1] = 0x0000_0001_0000_0005UL;   // ECX = 5, upper garbage

        harness.Step();

        Assert.Equal(4UL, harness.Cpu.State.Gpr[1]);   // upper half zero-extended away
        Assert.Equal(CodeBase, harness.Cpu.State.Rip);
    }

    /// <summary>E3h: JRCXZ takes the branch only when RCX is already zero.</summary>
    [Fact]
    public void JrcxzBranchesOnlyWhenRcxIsZero()
    {
        var harness = new Harness(0xE3, 0x02);   // jrcxz +2
        harness.Cpu.State.Gpr[1] = 0;

        harness.Step();

        Assert.Equal(CodeBase + 2 + 2, harness.Cpu.State.Rip);
    }

    /// <summary>E3h: JRCXZ does not branch when RCX is nonzero.</summary>
    [Fact]
    public void JrcxzDoesNotBranchWhenRcxIsNonzero()
    {
        var harness = new Harness(0xE3, 0x02);   // jrcxz +2
        harness.Cpu.State.Gpr[1] = 1;

        harness.Step();

        Assert.Equal(CodeBase + 2, harness.Cpu.State.Rip);
    }

    // === faults: a non-canonical target, a non-canonical push =====================

    /// <summary>
    /// FFh /4 to a non-canonical register value: #GP(0), with RIP left at the
    /// faulting instruction. RIP is read inside the ProcessorException hook,
    /// not after Step returns: 64-bit mode's own fault delivery is still the
    /// legacy path over an IDT this harness never sets up (LongModeAluTests'
    /// own FaultVectorWithFlags/FaultVectorWithRegister document the same
    /// gap), so it can go on to move RIP again once EnterInterrupt runs.
    /// </summary>
    [Fact]
    public void NonCanonicalIndirectJumpTargetFaultsWithRipUnchanged()
    {
        var harness = new Harness(0xFF, 0xE0);   // jmp rax
        harness.Cpu.State.Gpr[0] = 0x0000_8000_0000_0000UL;   // bit 47 set, bits 63-48 clear: not canonical

        byte vector = harness.FaultVectorWithRip(out ulong ripAtFault);

        Assert.Equal(GeneralProtection, vector);
        Assert.Equal(CodeBase, ripAtFault);
    }

    /// <summary>PUSH RAX with RSP eight below a non-canonical address: #SS(0), the vector LinearAddress64 raises for a bad address through SS.</summary>
    [Fact]
    public void PushToANonCanonicalStackAddressFaultsWithStackFault()
    {
        var harness = new Harness(0x50);   // push rax
        harness.Cpu.State.Gpr[4] = 0x0000_8000_0000_0008UL;   // RSP - 8 lands on bit 47 exactly

        byte vector = harness.FaultVector();

        Assert.Equal(StackFault, vector);
    }

    // === SETcc, CMOVcc ==============================================================

    /// <summary>40h (REX, no bits set) + 0F 94h: SETE writes SIL, the REX byte-register rule reaching register 6 as SIL rather than DH.</summary>
    [Fact]
    public void SetccWithRexTargetsSilNotDh()
    {
        var harness = new Harness(0x40, 0x0F, 0x94, 0xC6);   // sete sil
        harness.Cpu.State.Gpr[6] = 0xFFFFFFFFFFFFFFFFUL;
        harness.Cpu.Flags.Zero = true;

        harness.Step();

        Assert.Equal(0x01UL, harness.Cpu.State.Gpr[6] & 0xFF);
        Assert.Equal(0xFFFFFFFFFFFFFF00UL, harness.Cpu.State.Gpr[6] & ~0xFFUL);
    }

    /// <summary>0F 94h without REX: the same ModR/M byte now names DH, the high byte of RDX.</summary>
    [Fact]
    public void SetccWithoutRexTargetsDh()
    {
        var harness = new Harness(0x0F, 0x94, 0xC6);   // sete dh
        harness.Cpu.State.Gpr[2] = 0;
        harness.Cpu.Flags.Zero = true;

        harness.Step();

        Assert.Equal((byte)1, harness.Cpu.State.GetReg8(6));   // DH
    }

    /// <summary>0F 44h: CMOVE eax, ecx with ZF clear does not move ECX, but still zero-extends EAX's own low half into RAX's upper half.</summary>
    [Fact]
    public void ThirtyTwoBitCmovZeroExtendsDestinationWhenConditionIsFalse()
    {
        var harness = new Harness(0x0F, 0x44, 0xC1);   // cmove eax, ecx
        harness.Cpu.State.Gpr[0] = 0xFFFFFFFF_12345678UL;
        harness.Cpu.State.Gpr[1] = 0x99999999UL;
        harness.Cpu.Flags.Zero = false;

        harness.Step();

        Assert.Equal(0x0000000012345678UL, harness.Cpu.State.Gpr[0]);
    }

    /// <summary>0F 44h: CMOVE eax, ecx with ZF set moves ECX and zero-extends it into RAX.</summary>
    [Fact]
    public void ThirtyTwoBitCmovMovesAndZeroExtendsWhenConditionIsTrue()
    {
        var harness = new Harness(0x0F, 0x44, 0xC1);   // cmove eax, ecx
        harness.Cpu.State.Gpr[0] = 0xFFFFFFFF_12345678UL;
        harness.Cpu.State.Gpr[1] = 0x99999999UL;
        harness.Cpu.Flags.Zero = true;

        harness.Step();

        Assert.Equal(0x0000000099999999UL, harness.Cpu.State.Gpr[0]);
    }

    // === MOVSX/MOVZX, BSWAP, CDQE, CQO =============================================

    /// <summary>REX.W + 0F BEh: MOVSX RAX, BL sign-extends a negative byte across all 64 bits.</summary>
    [Fact]
    public void MovsxSixtyFourBitFromByteSignExtends()
    {
        var harness = new Harness(0x48, 0x0F, 0xBE, 0xC3);   // movsx rax, bl
        harness.Cpu.State.Gpr[3] = 0x80;

        harness.Step();

        Assert.Equal(0xFFFFFFFFFFFFFF80UL, harness.Cpu.State.Gpr[0]);
    }

    /// <summary>REX.W + 0F BFh: MOVSX RAX, BX sign-extends a negative word across all 64 bits.</summary>
    [Fact]
    public void MovsxSixtyFourBitFromWordSignExtends()
    {
        var harness = new Harness(0x48, 0x0F, 0xBF, 0xC3);   // movsx rax, bx
        harness.Cpu.State.Gpr[3] = 0x8000;

        harness.Step();

        Assert.Equal(0xFFFFFFFFFFFF8000UL, harness.Cpu.State.Gpr[0]);
    }

    /// <summary>0F B6h: MOVZX EAX, BL never sign-extends, and the 32-bit write zero-extends into RAX's upper half.</summary>
    [Fact]
    public void MovzxThirtyTwoBitFromByteNeverSignExtends()
    {
        var harness = new Harness(0x0F, 0xB6, 0xC3);   // movzx eax, bl
        harness.Cpu.State.Gpr[0] = 0xFFFFFFFF_00000000UL;
        harness.Cpu.State.Gpr[3] = 0x80;

        harness.Step();

        Assert.Equal(0x80UL, harness.Cpu.State.Gpr[0]);
    }

    /// <summary>REX.W + 0F C8h: BSWAP RAX reverses all eight bytes.</summary>
    [Fact]
    public void BswapSixtyFourBitReversesEightBytes()
    {
        var harness = new Harness(0x48, 0x0F, 0xC8);   // bswap rax
        harness.Cpu.State.Gpr[0] = 0x0102030405060708UL;

        harness.Step();

        Assert.Equal(0x0807060504030201UL, harness.Cpu.State.Gpr[0]);
    }

    /// <summary>REX.W + 98h: CDQE sign-extends EAX into RAX.</summary>
    [Fact]
    public void CdqeSignExtendsEaxIntoRax()
    {
        var harness = new Harness(0x48, 0x98);   // cdqe
        harness.Cpu.State.Gpr[0] = 0xDEADBEEF_FFFFFFFFUL;   // EAX = -1

        harness.Step();

        Assert.Equal(0xFFFFFFFFFFFFFFFFUL, harness.Cpu.State.Gpr[0]);
    }

    /// <summary>REX.W + 99h: CQO sign-extends RAX's sign bit across all of RDX.</summary>
    [Fact]
    public void CqoSignExtendsRaxSignIntoRdx()
    {
        var harness = new Harness(0x48, 0x99);   // cqo
        harness.Cpu.State.Gpr[0] = 0x8000000000000000UL;   // RAX negative

        harness.Step();

        Assert.Equal(0xFFFFFFFFFFFFFFFFUL, harness.Cpu.State.Gpr[2]);
    }

    // === LAHF/SAHF, CPUID, RDMSR ====================================================

    /// <summary>9Fh: LAHF is #UD without CpuFeature.LahfLm, exactly like any opcode 64-bit mode does not carry.</summary>
    [Fact]
    public void LahfWithoutLahfLmFeatureIsInvalidOpcode()
    {
        var harness = new Harness(0x9F);   // lahf

        byte vector = harness.FaultVector();

        Assert.Equal(InvalidOpcode, vector);
    }

    /// <summary>9Fh: LAHF with CpuFeature.LahfLm reuses the legacy, pure Lahf() and writes AH from the low byte of the flags.</summary>
    [Fact]
    public void LahfWithFeaturePresentWritesAh()
    {
        var harness = new Harness(0x9F);   // lahf
        harness.Cpu.State.Features = harness.Cpu.State.Features.With(CpuFeature.LahfLm);
        harness.Cpu.Flags.Carry = true;

        harness.Step();

        Assert.Equal((byte)1, harness.Cpu.State.GetReg8(4) & 1);   // AH bit 0 is CF
    }

    /// <summary>0F A2h: CPUID always zero-extends EAX-EDX into RAX-RDX, whatever garbage sat in the upper half beforehand.</summary>
    [Fact]
    public void CpuidZeroExtendsAllFourResultRegisters()
    {
        var harness = new Harness(0x0F, 0xA2);   // cpuid
        harness.Cpu.State.Gpr[0] = 0xFFFFFFFF_00000000UL;   // leaf 0, garbage upper half
        harness.Cpu.State.Gpr[1] = 0xFFFFFFFF_FFFFFFFFUL;
        harness.Cpu.State.Gpr[2] = 0xFFFFFFFF_FFFFFFFFUL;
        harness.Cpu.State.Gpr[3] = 0xFFFFFFFF_FFFFFFFFUL;

        harness.Step();

        Assert.Equal(0UL, harness.Cpu.State.Gpr[0] >> 32);   // RAX (EAX: max standard leaf)
        Assert.Equal(0UL, harness.Cpu.State.Gpr[1] >> 32);   // RCX (ECX: "ntel")
        Assert.Equal(0UL, harness.Cpu.State.Gpr[2] >> 32);   // RDX (EDX: "ineI")
        Assert.Equal(0UL, harness.Cpu.State.Gpr[3] >> 32);   // RBX (EBX: "Genu")
        // EBX:EDX:ECX spells "GenuineIntel" for leaf 0 (Cpu.Identification.cs). RBX is Gpr[3], not Gpr[1] (RCX).
        Assert.Equal(0x756E6547UL, harness.Cpu.State.Gpr[3] & 0xFFFFFFFF);
    }

    /// <summary>0F 32h: RDMSR at CPL 3 is #GP(0), before the MSR index is even looked at.</summary>
    [Fact]
    public void RdmsrAtCplThreeFaults()
    {
        var harness = new Harness(0x0F, 0x32);   // rdmsr
        harness.Cpu.State.Cpl = 3;
        harness.Cpu.State.Gpr[1] = 0xC0000080;   // ECX = EFER's index, irrelevant to the fault

        byte vector = harness.FaultVector();

        Assert.Equal(GeneralProtection, vector);
    }

    // === review coverage (stage C2b) ===========================================

    private const int StackPointer = 4;
    private const int FramePointer = 5;
    private const int Counter = 1;
    private const byte PageFault = 14;
    private const ulong NonCanonical = 0x0000_8000_0000_0000UL;

    /// <summary>RET to a non-canonical return address is #GP(0) with RSP exactly where RET found it (precise faults, SDM Vol.3A §6.5).</summary>
    [Fact]
    public void RetToANonCanonicalAddressLeavesRspUnchanged()
    {
        var harness = new Harness(0xC3);                    // ret
        harness.Cpu.State.Gpr[StackPointer] = 0x7F00;
        harness.WriteQword(0x7F00, NonCanonical);

        byte vector = harness.FaultWithRegisters(out ulong[] registers, out uint errorCode);

        Assert.Equal(GeneralProtection, vector);
        Assert.Equal(0u, errorCode);
        Assert.Equal(0x7F00UL, registers[StackPointer]);
    }

    /// <summary>POP to a memory operand on a missing page is #PF with RSP not yet advanced.</summary>
    [Fact]
    public void PopToAMissingPageLeavesRspUnchanged()
    {
        var harness = new Harness(0x8F, 0x03);              // pop qword [rbx]
        harness.Cpu.State.Gpr[StackPointer] = 0x7F00;
        harness.Cpu.State.Gpr[3] = 0x9000;                  // not mapped

        byte vector = harness.FaultWithRegisters(out ulong[] registers, out _);

        Assert.Equal(PageFault, vector);
        Assert.Equal(0x7F00UL, registers[StackPointer]);
    }

    /// <summary>POP [RSP] computes its destination after the increment: the value lands eight bytes above the old RSP.</summary>
    [Fact]
    public void PopToRspBasedMemoryUsesTheIncrementedRsp()
    {
        var harness = new Harness(0x8F, 0x04, 0x24);        // pop qword [rsp]
        harness.Cpu.State.Gpr[StackPointer] = 0x7F00;
        harness.WriteQword(0x7F00, 0x1122334455667788UL);

        harness.Step();

        Assert.Equal(0x1122334455667788UL, harness.ReadQword(0x7F08));
        Assert.Equal(0x7F08UL, harness.Cpu.State.Gpr[StackPointer]);
    }

    /// <summary>POP RSP in its r/m form (8F C4) leaves the popped value in RSP, as the 58h+r form does.</summary>
    [Fact]
    public void PopRspThroughModRmLeavesThePoppedValue()
    {
        var harness = new Harness(0x8F, 0xC4);              // pop rsp (r/m form)
        harness.Cpu.State.Gpr[StackPointer] = 0x7F00;
        harness.WriteQword(0x7F00, 0x9000);

        harness.Step();

        Assert.Equal(0x9000UL, harness.Cpu.State.Gpr[StackPointer]);
    }

    /// <summary>LEAVE with a non-canonical RBP is #SS(0) with RSP and RBP untouched.</summary>
    [Fact]
    public void LeaveWithANonCanonicalFramePointerLeavesBothRegisters()
    {
        var harness = new Harness(0xC9);                    // leave
        harness.Cpu.State.Gpr[StackPointer] = 0x7F00;
        harness.Cpu.State.Gpr[FramePointer] = NonCanonical;

        byte vector = harness.FaultWithRegisters(out ulong[] registers, out _);

        Assert.Equal(StackFault, vector);
        Assert.Equal(0x7F00UL, registers[StackPointer]);
        Assert.Equal(NonCanonical, registers[FramePointer]);
    }

    /// <summary>
    /// ENTER whose second push lands on a missing page (6000h) faults with
    /// RSP and RBP as ENTER found them, at nesting levels 1 and 2.
    /// </summary>
    [Theory]
    [InlineData(1, 0x7008UL)]
    [InlineData(2, 0x7010UL)]
    public void EnterFaultingPartwayLeavesRspAndRbpUnchanged(byte level, ulong stackPointer)
    {
        var harness = new Harness(0xC8, 0x10, 0x00, level);   // enter 10h, level
        harness.Cpu.State.Gpr[StackPointer] = stackPointer;
        harness.Cpu.State.Gpr[FramePointer] = 0x7F00;

        byte vector = harness.FaultWithRegisters(out ulong[] registers, out _);

        Assert.Equal(PageFault, vector);
        Assert.Equal(stackPointer, registers[StackPointer]);
        Assert.Equal(0x7F00UL, registers[FramePointer]);
    }

    /// <summary>66h 68h is PUSH imm16: a 4-byte instruction pushing two bytes.</summary>
    [Fact]
    public void PushImmediateWith66hTakesASixteenBitImmediate()
    {
        var harness = new Harness(0x66, 0x68, 0x34, 0x12);
        harness.Cpu.State.Gpr[StackPointer] = 0x7F00;

        harness.Step();

        Assert.Equal(CodeBase + 4UL, harness.Cpu.State.Rip);
        Assert.Equal(0x7EFEUL, harness.Cpu.State.Gpr[StackPointer]);
        Assert.Equal((ushort)0x1234, harness.Bus.ReadWord(0x7EFE));
    }

    /// <summary>66h does not narrow a near CALL on Intel: rel32, an eight-byte return address.</summary>
    [Fact]
    public void CallWith66hStillPushesEightBytes()
    {
        var harness = new Harness(0x66, 0xE8, 0x00, 0x00, 0x00, 0x00);   // call +0
        harness.Cpu.State.Gpr[StackPointer] = 0x7F00;

        harness.Step();

        Assert.Equal(0x7EF8UL, harness.Cpu.State.Gpr[StackPointer]);
        Assert.Equal(CodeBase + 6UL, harness.ReadQword(0x7EF8));
        Assert.Equal(CodeBase + 6UL, harness.Cpu.State.Rip);
    }

    /// <summary>LOOP to a non-canonical target is #GP with RCX not yet decremented.</summary>
    [Fact]
    public void LoopToANonCanonicalTargetLeavesRcxUnchanged()
    {
        // loop +0 in the last two bytes of the canonical lower half: the
        // instruction after it, its target, is non-canonical.
        var harness = new Harness();
        harness.Map(NonCanonical - 0x1000, CodeBase);
        harness.Bus.WriteByte(CodeBase + 0xFFE, 0xE2);
        harness.Bus.WriteByte(CodeBase + 0xFFF, 0x00);
        harness.Cpu.State.Rip = NonCanonical - 2;
        harness.Cpu.State.Gpr[Counter] = 5;

        byte vector = harness.FaultWithRegisters(out ulong[] registers, out _);

        Assert.Equal(GeneralProtection, vector);
        Assert.Equal(5UL, registers[Counter]);
    }

    /// <summary>LOOPE branches while RCX is non-zero and ZF is set; LOOPNE while ZF is clear.</summary>
    [Theory]
    [InlineData(0xE1, true, true)]
    [InlineData(0xE1, false, false)]
    [InlineData(0xE0, false, true)]
    [InlineData(0xE0, true, false)]
    public void LoopeAndLoopneTestTheZeroFlag(byte opcode, bool zero, bool taken)
    {
        var harness = new Harness(opcode, 0x10);
        harness.Cpu.State.Gpr[Counter] = 3;
        harness.Cpu.Flags.Zero = zero;

        harness.Step();

        Assert.Equal(2UL, harness.Cpu.State.Gpr[Counter]);
        Assert.Equal(taken ? CodeBase + 0x12UL : CodeBase + 2UL, harness.Cpu.State.Rip);
    }

    /// <summary>JECXZ (67h E3h) tests ECX alone: RCX = 1_0000_0000h has ECX = 0.</summary>
    [Fact]
    public void JecxzTestsOnlyEcx()
    {
        var harness = new Harness(0x67, 0xE3, 0x10);
        harness.Cpu.State.Gpr[Counter] = 0x1_0000_0000UL;

        harness.Step();

        Assert.Equal(CodeBase + 0x13UL, harness.Cpu.State.Rip);
    }

    [Fact]
    public void PushfThenPopfRoundTripsTheFlags()
    {
        var harness = new Harness(0x9C, 0x9D);              // pushfq ; popfq
        harness.Cpu.State.Gpr[StackPointer] = 0x7F00;
        harness.Cpu.Flags.Carry = true;
        harness.Cpu.Flags.Overflow = true;

        harness.Step();
        harness.Cpu.Flags.Carry = false;
        harness.Cpu.Flags.Overflow = false;
        harness.Step();

        Assert.True(harness.Cpu.Flags.Carry);
        Assert.True(harness.Cpu.Flags.Overflow);
        Assert.Equal(0x7F00UL, harness.Cpu.State.Gpr[StackPointer]);
    }

    /// <summary>CLI at CPL 3 with IOPL 0 is #GP(0).</summary>
    [Fact]
    public void CliAboveIoplIsGeneralProtection()
    {
        var harness = new Harness(0xFA);
        harness.Cpu.State.Cpl = 3;

        Assert.Equal(GeneralProtection, harness.FaultVector());
    }

    /// <summary>CLTS clears CR0.TS (bit 3).</summary>
    [Fact]
    public void CltsClearsTaskSwitched()
    {
        var harness = new Harness(0x0F, 0x06);
        harness.Cpu.State.Cr0 |= CpuState.Cr0TaskSwitched;

        harness.Step();

        Assert.Equal(0u, harness.Cpu.State.Cr0 & CpuState.Cr0TaskSwitched);
    }

    /// <summary>RDTSC writes EDX:EAX, zero-extending RAX and RDX.</summary>
    [Fact]
    public void RdtscZeroExtendsRaxAndRdx()
    {
        var harness = new Harness(0x0F, 0x31);
        harness.Cpu.State.Gpr[0] = ulong.MaxValue;
        harness.Cpu.State.Gpr[2] = ulong.MaxValue;

        harness.Step();

        Assert.Equal(0UL, harness.Cpu.State.Gpr[0] >> 32);
        Assert.Equal(0UL, harness.Cpu.State.Gpr[2] >> 32);
    }

    /// <summary>WRMSR at CPL 0 writes IA32_FS_BASE (C0000100h) from EDX:EAX.</summary>
    [Fact]
    public void WrmsrWritesTheFsBase()
    {
        var harness = new Harness(0x0F, 0x30);
        harness.Cpu.State.Gpr[Counter] = 0xC0000100;
        harness.Cpu.State.Gpr[2] = 0xFFFF8000;
        harness.Cpu.State.Gpr[0] = 0x00401000;

        harness.Step();

        Assert.Equal(0xFFFF8000_00401000UL, harness.Cpu.State.Segments[(int)SegReg.Fs].Base64);
    }

    /// <summary>0F 1F /0 is a multi-byte NOP where the hint-NOP space exists.</summary>
    [Fact]
    public void MultiByteNopDoesNothing()
    {
        var harness = new Harness(0x0F, 0x1F, 0x00);        // nop dword [rax]
        harness.Cpu.State.Features = harness.Cpu.State.Features.With(CpuFeature.HintNops);

        Assert.Equal(NoFault, harness.FaultVector());
        Assert.Equal(CodeBase + 3UL, harness.Cpu.State.Rip);
    }

    /// <summary>CMOV reads its source whether or not the condition holds, so a missing page faults even when nothing would be moved.</summary>
    [Fact]
    public void CmovWithAFalseConditionStillFaultsOnItsSource()
    {
        var harness = new Harness(0x48, 0x0F, 0x44, 0x03);  // cmove rax, [rbx]
        harness.Cpu.State.Gpr[3] = 0x9000;                  // not mapped
        harness.Cpu.Flags.Zero = false;

        Assert.Equal(PageFault, harness.FaultVector());
    }

    // === POPF's privilege rule ======================================================

    /// <summary>9Dh: POPF at CPL 3 cannot raise IOPL, and cannot set IF while IOPL stays below the caller's privilege.</summary>
    [Fact]
    public void PopfAtCplThreeCannotRaiseIoplOrSetInterruptFlag()
    {
        var harness = new Harness(0x9D);   // popf
        harness.Cpu.State.Cpl = 3;
        harness.Cpu.Flags.SetValue(0x2);   // IOPL = 0, IF = 0, only the always-one bit set

        const uint IoplThreeAndInterruptFlag = 0x2 | (3u << 12) | (1u << 9);
        harness.WriteQword((uint)InitialRsp, IoplThreeAndInterruptFlag);

        harness.Step();

        uint flags = harness.Cpu.Flags.Value;
        Assert.Equal(0u, (flags >> 12) & 3);   // IOPL still 0
        Assert.False(harness.Cpu.Flags.Interrupt);
    }

    private sealed class Harness
    {
        private const uint Pml4Address = 0x20000;
        private const ulong Present = 1UL << 0;
        private const ulong ReadWrite = 1UL << 1;
        private const ulong UserSupervisor = 1UL << 2;
        private const ulong TableFlags = Present | ReadWrite | UserSupervisor;

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
            Cpu.State.Features = Cpu.State.Features.With(
                CpuFeature.Pae, CpuFeature.LongMode, CpuFeature.Nx,
                CpuFeature.Msr, CpuFeature.Identification, CpuFeature.Cmov,
                CpuFeature.Isa486, CpuFeature.Tsc);

            Map(CodeBase, CodeBase);
            Map(DataPage, DataPage);
            Map(0x7000, 0x7000);

            Cpu.State.Cr3 = Pml4Address;
            Cpu.State.Cr4 |= CpuState.Cr4Pae;
            Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable | CpuState.Cr0Paging;
            Cpu.State.Efer = CpuState.EferLongModeEnable | CpuState.EferLongModeActive;
            Cpu.State.Segments[(int)SegReg.Cs].Long = true;
            Cpu.State.Rip = CodeBase;
            Cpu.State.Gpr[4] = InitialRsp;
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
        /// The same snapshot-at-fault-time idea as LongModeAluTests'
        /// FaultVectorWithFlags, for RIP: the hook fires at the top of
        /// RaiseException (Cpu.Ops.cs), before EnterInterrupt's own legacy
        /// delivery -- over an IDT this harness never sets up -- has a chance
        /// to move RIP again on top of what the faulting instruction left it.
        /// </summary>
        public byte FaultVectorWithRip(out ulong ripAtFault)
        {
            byte vector = NoFault;
            ulong snapshot = 0;
            Cpu.ProcessorException += (raised, _, _, _) =>
            {
                if (vector == NoFault)
                {
                    vector = raised;
                    snapshot = Cpu.State.Rip;
                }
            };

            Cpu.Step();
            ripAtFault = snapshot;
            return vector;
        }

        /// <summary>
        /// Runs one instruction and returns the first exception vector, with a
        /// copy of the general registers and the error code taken inside the
        /// exception hook -- before the (stage-D) legacy delivery path changes
        /// anything -- so a test can assert what the faulting instruction
        /// itself left behind.
        /// </summary>
        public byte FaultWithRegisters(out ulong[] registersAtFault, out uint errorCode)
        {
            byte vector = NoFault;
            ulong[] snapshot = [];
            uint code = 0;
            Cpu.ProcessorException += (raised, error, _, _) =>
            {
                if (vector == NoFault)
                {
                    vector = raised;
                    code = error;
                    snapshot = (ulong[])Cpu.State.Gpr.Clone();
                }
            };

            Cpu.Step();
            registersAtFault = snapshot;
            errorCode = code;
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
