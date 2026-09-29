using Aurora386.Core;
using Aurora386.Core.Cpu;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// Long mode, stage C1 (plans/long-mode.md): 64-bit mode's own decoder --
/// REX and the legacy prefixes, the fetch from RIP, ModR/M and SIB with
/// REX.R/X/B, RIP-relative addressing, the 67h address size, the REX byte
/// registers, canonical addresses, 64-bit FS/GS bases and the TLB's 64-bit
/// tags -- proved through the data-movement instructions stage C1 carries.
/// Encodings and results are the SDM Vol.2 ones (§2.2 and the MOV, LEA,
/// MOVSXD and XCHG pages).
///
/// The processor is a Pentium II given long mode by hand, put straight into
/// 64-bit mode: CR0.PE and PG, CR4.PAE, EFER.LME and LMA, CS.L. Page tables
/// are built on demand by <see cref="Map"/>, identity or not, anywhere in
/// the canonical space.
/// </summary>
public class LongModeDecoderTests
{
    private const uint CodeBase = 0x1000;
    private const uint DataPage = 0x5000;
    private const byte InvalidOpcode = 6;
    private const byte StackFault = 12;
    private const byte GeneralProtection = 13;
    private const byte NoFault = 0xFF;

    // === immediates and registers =============================================

    [Fact]
    public void MovR64Imm64LoadsAllEightBytes()
    {
        var harness = new Harness(0x48, 0xB8, 0xEF, 0xCD, 0xAB, 0x89, 0x67, 0x45, 0x23, 0x01);   // mov rax, 0123456789ABCDEFh

        harness.Step();

        Assert.Equal(0x01234567_89ABCDEFUL, harness.Cpu.State.Gpr[0]);
        Assert.Equal(CodeBase + 10UL, harness.Cpu.State.Rip);
    }

    /// <summary>A 32-bit register write clears bits 63:32 (SDM Vol.1 §3.4.1.1).</summary>
    [Fact]
    public void MovR32Imm32ZeroExtendsIntoTheUpperHalf()
    {
        var harness = new Harness(0xB8, 0x78, 0x56, 0x34, 0x12);   // mov eax, 12345678h
        harness.Cpu.State.Gpr[0] = ulong.MaxValue;

        harness.Step();

        Assert.Equal(0x12345678UL, harness.Cpu.State.Gpr[0]);
    }

    [Fact]
    public void RexBReachesR15()
    {
        var harness = new Harness(0x49, 0xBF, 1, 0, 0, 0, 0, 0, 0, 0x80);   // mov r15, 8000000000000001h

        harness.Step();

        Assert.Equal(0x80000000_00000001UL, harness.Cpu.State.Gpr[15]);
    }

    /// <summary>With any REX prefix, byte register 4 is SPL; without one it is AH.</summary>
    [Fact]
    public void AnyRexPrefixTurnsAhIntoSpl()
    {
        var withoutRex = new Harness(0x88, 0xE0);           // mov al, ah
        var withRex = new Harness(0x40, 0x88, 0xE0);        // mov al, spl
        foreach (Harness harness in new[] { withoutRex, withRex })
        {
            harness.Cpu.State.Gpr[0] = 0;
            harness.Cpu.State.Gpr[4] = 0x5A;                // SPL = 5Ah
            harness.Cpu.State.Gpr[0] = 0xA500;              // AH = A5h
            harness.Step();
        }

        Assert.Equal(0xA5UL, withoutRex.Cpu.State.Gpr[0] & 0xFF);
        Assert.Equal(0x5AUL, withRex.Cpu.State.Gpr[0] & 0xFF);
    }

    /// <summary>A legacy prefix between REX and the opcode cancels the REX: 48h 66h B8h is mov ax, imm16.</summary>
    [Fact]
    public void ALegacyPrefixAfterRexCancelsIt()
    {
        var harness = new Harness(0x48, 0x66, 0xB8, 0x34, 0x12);
        harness.Cpu.State.Gpr[0] = 0xFFFFFFFF_FFFF0000UL;

        harness.Step();

        Assert.Equal(0xFFFFFFFF_FFFF1234UL, harness.Cpu.State.Gpr[0]);
        Assert.Equal(CodeBase + 5UL, harness.Cpu.State.Rip);
    }

    [Fact]
    public void MovsxdSignExtendsADoubleword()
    {
        var harness = new Harness(0x48, 0x63, 0xC1);        // movsxd rax, ecx
        harness.Cpu.State.Gpr[1] = 0x12345678_80000000UL;

        harness.Step();

        Assert.Equal(0xFFFFFFFF_80000000UL, harness.Cpu.State.Gpr[0]);
    }

    [Fact]
    public void XchgWithRexBExchangesR8AndRax()
    {
        var harness = new Harness(0x49, 0x90);              // xchg r8, rax
        harness.Cpu.State.Gpr[0] = 1;
        harness.Cpu.State.Gpr[8] = 2;

        harness.Step();

        Assert.Equal(2UL, harness.Cpu.State.Gpr[0]);
        Assert.Equal(1UL, harness.Cpu.State.Gpr[8]);
    }

    // === addressing ==============================================================

    /// <summary>mod 00, r/m 101: RIP-relative, from the end of the instruction.</summary>
    [Fact]
    public void RipRelativeAddressingCountsFromTheNextInstruction()
    {
        // mov [rip + 3FF9h], rax: 7 bytes, so the target is 1007h + 3FF9h = 5000h.
        var harness = new Harness(0x48, 0x89, 0x05, 0xF9, 0x3F, 0x00, 0x00);
        harness.Cpu.State.Gpr[0] = 0x11223344_55667788UL;

        harness.Step();

        Assert.Equal(0x55667788u, harness.Bus.ReadDword(DataPage));
        Assert.Equal(0x11223344u, harness.Bus.ReadDword(DataPage + 4));
    }

    /// <summary>The end of the instruction includes an immediate that follows the displacement.</summary>
    [Fact]
    public void RipRelativeAddressingIncludesTheImmediateInTheLength()
    {
        // mov dword [rip + 3FF6h], 0CAFEBABEh: 10 bytes, so 100Ah + 3FF6h = 5000h.
        var harness = new Harness(0xC7, 0x05, 0xF6, 0x3F, 0x00, 0x00, 0xBE, 0xBA, 0xFE, 0xCA);

        harness.Step();

        Assert.Equal(0xCAFEBABEu, harness.Bus.ReadDword(DataPage));
    }

    /// <summary>REX.X makes SIB index 100 mean R12 rather than "no index".</summary>
    [Fact]
    public void RexXMakesR12AnIndex()
    {
        var harness = new Harness(0x4A, 0x8B, 0x04, 0xA0);  // mov rax, [rax + r12*4]
        harness.Bus.WriteDword(DataPage + 0x10, 0x600D);
        harness.Cpu.State.Gpr[0] = DataPage;
        harness.Cpu.State.Gpr[12] = 4;

        harness.Step();

        Assert.Equal(0x600DUL, harness.Cpu.State.Gpr[0]);
    }

    [Fact]
    public void SibIndex100WithoutRexXMeansNoIndex()
    {
        var harness = new Harness(0x48, 0x8B, 0x04, 0x24);  // mov rax, [rsp]
        harness.Bus.WriteDword(DataPage, 0x5717);
        harness.Cpu.State.Gpr[4] = DataPage;

        harness.Step();

        Assert.Equal(0x5717UL, harness.Cpu.State.Gpr[0]);
    }

    /// <summary>67h: the address is computed from EBX alone, the upper half of RBX ignored.</summary>
    [Fact]
    public void The67hPrefixUses32BitAddresses()
    {
        var harness = new Harness(0x67, 0x8B, 0x03);        // mov eax, [ebx]
        harness.Bus.WriteDword(DataPage, 0x32B1);
        harness.Cpu.State.Gpr[3] = 0xDEAD0000_00000000UL | DataPage;

        harness.Step();

        Assert.Equal(0x32B1UL, harness.Cpu.State.Gpr[0]);
    }

    [Fact]
    public void LeaOfRipZeroIsTheNextInstruction()
    {
        var harness = new Harness(0x48, 0x8D, 0x05, 0, 0, 0, 0);   // lea rax, [rip]

        harness.Step();

        Assert.Equal(CodeBase + 7UL, harness.Cpu.State.Gpr[0]);
    }

    /// <summary>An address in the upper canonical half, far above 4 GB, translates through its own PML4 entry.</summary>
    [Fact]
    public void AnAddressInTheUpperCanonicalHalfIsReachable()
    {
        const ulong HighLinear = 0xFFFF8000_12345000UL;
        const uint HighFrame = 0x9000;
        var harness = new Harness(0x48, 0x8B, 0x00);        // mov rax, [rax]
        harness.Map(HighLinear, HighFrame);
        harness.Bus.WriteDword(HighFrame + 8, 0x4167);
        harness.Cpu.State.Gpr[0] = HighLinear + 8;

        harness.Step();

        Assert.Equal(0x4167UL, harness.Cpu.State.Gpr[0]);
    }

    /// <summary>
    /// Pages 4 GB apart hash to the same TLB slot; the 64-bit tag keeps them
    /// apart, so the second read sees its own frame, not the first's.
    /// </summary>
    [Fact]
    public void PagesFourGigabytesApartDoNotShareATlbEntry()
    {
        const ulong AliasLinear = 0x1_0000_0000UL + DataPage;
        const uint AliasFrame = 0xA000;
        var harness = new Harness(0x8B, 0x03, 0x8B, 0x01);  // mov eax, [rbx] ; mov eax, [rcx]
        harness.Map(AliasLinear, AliasFrame);
        harness.Bus.WriteDword(DataPage, 1);
        harness.Bus.WriteDword(AliasFrame, 2);
        harness.Cpu.State.Gpr[3] = DataPage;
        harness.Cpu.State.Gpr[1] = AliasLinear;

        harness.Step();
        harness.Step();

        Assert.Equal(2UL, harness.Cpu.State.Gpr[0]);
    }

    // === segments and faults =====================================================

    /// <summary>An FS override adds the 64-bit base IA32_FS_BASE (MSR C0000100h) wrote.</summary>
    [Fact]
    public void AnFsOverrideAddsTheSixtyFourBitBase()
    {
        const ulong FsBase = 0xFFFF8000_12345000UL;
        const uint FsFrame = 0x9000;
        var harness = new Harness(0x64, 0x8B, 0x04, 0x25, 0x10, 0, 0, 0);   // mov eax, fs:[10h]
        harness.Map(FsBase, FsFrame);
        harness.Bus.WriteDword(FsFrame + 0x10, 0xF5);
        Assert.True(harness.Cpu.Msrs.TryWrite(0xC0000100, FsBase));

        harness.Step();

        Assert.Equal(0xF5UL, harness.Cpu.State.Gpr[0]);
    }

    [Fact]
    public void WritingANonCanonicalFsBaseIsRefused()
    {
        var harness = new Harness();

        Assert.False(harness.Cpu.Msrs.TryWrite(0xC0000100, 0x00008000_00000000UL));
    }

    /// <summary>A non-canonical data address is #GP(0); through RSP or RBP (the SS forms) it is #SS(0).</summary>
    [Theory]
    [InlineData(0x03, 3, GeneralProtection)]   // mov eax, [rbx]
    [InlineData(0x45, 5, StackFault)]          // mov eax, [rbp + 0]
    public void ANonCanonicalAddressFaults(byte modrm, int baseRegister, byte expectedVector)
    {
        byte[] code = modrm == 0x45 ? [0x8B, modrm, 0x00] : [0x8B, modrm];
        var harness = new Harness(code);
        harness.Cpu.State.Gpr[baseRegister] = 0x00008000_00000000UL;

        Assert.Equal(expectedVector, harness.FaultVector());
    }

    /// <summary>06h (PUSH ES) does not exist in 64-bit mode: #UD.</summary>
    [Fact]
    public void AnOpcodeRemovedFrom64BitModeIsInvalid()
    {
        var harness = new Harness(0x06);

        Assert.Equal(InvalidOpcode, harness.FaultVector());
    }

    /// <summary>More than 15 bytes of instruction is #GP(0), however they are made of prefixes.</summary>
    [Fact]
    public void AnInstructionLongerThanFifteenBytesIsGeneralProtection()
    {
        byte[] code = Enumerable.Repeat((byte)0x66, 15).Append((byte)0x90).ToArray();
        var harness = new Harness(code);

        Assert.Equal(GeneralProtection, harness.FaultVector());
    }

    [Fact]
    public void HltAtRingZeroHalts()
    {
        var harness = new Harness(0xF4);

        harness.Step();

        Assert.True(harness.Cpu.State.Halted);
    }

    /// <summary>Compatibility mode (CS.L clear) stays on the legacy decoder: 48h is DEC EAX there, not REX.W.</summary>
    [Fact]
    public void CompatibilityModeKeepsTheLegacyDecoder()
    {
        var harness = new Harness(0x48);
        harness.Cpu.State.Segments[(int)SegReg.Cs].Long = false;
        harness.Cpu.State.Segments[(int)SegReg.Cs].Big = true;
        harness.Cpu.State.Gpr[0] = 5;

        harness.Step();

        Assert.Equal(4UL, harness.Cpu.State.Gpr[0]);
    }

    // === control registers ========================================================

    [Fact]
    public void MovFromCr0ReadsAllSixtyFourBits()
    {
        var harness = new Harness(0x0F, 0x20, 0xC0);        // mov rax, cr0
        harness.Cpu.State.Gpr[0] = ulong.MaxValue;

        harness.Step();

        Assert.Equal((ulong)harness.Cpu.State.Cr0, harness.Cpu.State.Gpr[0]);
    }

    /// <summary>Bits 63:32 of CR0 are reserved: a 1 there is #GP(0).</summary>
    [Fact]
    public void MovToCr0WithAnUpperBitIsGeneralProtection()
    {
        var harness = new Harness(0x0F, 0x22, 0xC0);        // mov cr0, rax
        harness.Cpu.State.Gpr[0] = (1UL << 32) | harness.Cpu.State.Cr0;

        Assert.Equal(GeneralProtection, harness.FaultVector());
    }

    /// <summary>REX.R extends the control register number; CR8 (the TPR) is stage D's, so it is #UD for now, as are the numbers that do not exist.</summary>
    [Theory]
    [InlineData(0x44, 0xC0)]   // mov rax, cr8
    [InlineData(0x00, 0xE8)]   // mov rax, cr5
    public void AControlRegisterWithoutAnImplementationIsInvalid(byte rex, byte modrm)
    {
        byte[] code = rex == 0 ? [0x0F, 0x20, modrm] : [rex, 0x0F, 0x20, modrm];
        var harness = new Harness(code);

        Assert.Equal(InvalidOpcode, harness.FaultVector());
    }

    // === review coverage (stage C1) ================================================

    /// <summary>LOCK is #UD in front of every instruction carried so far, MOV CR0 included (no AMD CR8 alias is declared).</summary>
    [Theory]
    [InlineData(new byte[] { 0xF0, 0x89, 0x03 })]
    [InlineData(new byte[] { 0xF0, 0x0F, 0x22, 0xC0 })]
    public void LockIsInvalidInFrontOfTheseInstructions(byte[] code)
    {
        var harness = new Harness(code);

        Assert.Equal(InvalidOpcode, harness.FaultVector());
    }

    /// <summary>REX.B does not turn RIP-relative addressing into [R13]: mod 00, r/m 101 is RIP-relative whatever REX.B says.</summary>
    [Fact]
    public void RexBLeavesRipRelativeAddressingAlone()
    {
        var harness = new Harness(0x49, 0x8B, 0x05, 0xF9, 0x3F, 0x00, 0x00);   // mov rax, [rip + 3FF9h]
        harness.Bus.WriteDword(DataPage, 0x71B);
        harness.Cpu.State.Gpr[13] = 0xDEAD;

        harness.Step();

        Assert.Equal(0x71BUL, harness.Cpu.State.Gpr[0]);
    }

    /// <summary>SIB base 101 with mod 00 means disp32 and no base, REX.B or not: it never names R13.</summary>
    [Fact]
    public void SibBase101WithRexBIsStillAnAbsoluteDisplacement()
    {
        var harness = new Harness(0x41, 0x8B, 0x04, 0x25, 0x00, 0x50, 0x00, 0x00);   // mov eax, [5000h]
        harness.Bus.WriteDword(DataPage, 0xAB5);
        harness.Cpu.State.Gpr[13] = 0x100;

        harness.Step();

        Assert.Equal(0xAB5UL, harness.Cpu.State.Gpr[0]);
    }

    /// <summary>NOP (90h without REX.B) is not a 32-bit write: RAX keeps its upper half.</summary>
    [Fact]
    public void NopLeavesTheUpperHalfOfRaxAlone()
    {
        var harness = new Harness(0x90);
        harness.Cpu.State.Gpr[0] = 0x12345678_9ABCDEF0UL;

        harness.Step();

        Assert.Equal(0x12345678_9ABCDEF0UL, harness.Cpu.State.Gpr[0]);
    }

    /// <summary>C6h with a reg field other than 0 is #UD.</summary>
    [Fact]
    public void C6WithRegOneIsInvalid()
    {
        var harness = new Harness(0xC6, 0x08, 0x00);        // C6 /1 [rax], imm8

        Assert.Equal(InvalidOpcode, harness.FaultVector());
    }

    /// <summary>An access whose last byte crosses into non-canonical space is #GP(0) though its first byte is canonical.</summary>
    [Fact]
    public void AnAccessEndingPastTheCanonicalBoundaryFaults()
    {
        var harness = new Harness(0x48, 0x8B, 0x03);        // mov rax, [rbx]
        harness.Cpu.State.Gpr[3] = 0x00007FFF_FFFFFFFCUL;

        Assert.Equal(GeneralProtection, harness.FaultVector());
    }

    /// <summary>A 64-bit write that crosses into a missing page writes nothing: every page is translated first.</summary>
    [Fact]
    public void APageCrossingWriteThatFaultsLeavesMemoryUntouched()
    {
        var harness = new Harness(0x48, 0x89, 0x03);        // mov [rbx], rax
        harness.Cpu.State.Gpr[3] = DataPage + 0xFFC;        // the next page (6000h) is not mapped
        harness.Cpu.State.Gpr[0] = ulong.MaxValue;

        Assert.Equal(14, harness.FaultVector());
        Assert.Equal(0u, harness.Bus.ReadDword(DataPage + 0xFFC));
    }

    // === harness ==================================================================

    /// <summary>
    /// A bare CPU in 64-bit mode with the code page, the data page and the
    /// stack page identity-mapped; <see cref="Map"/> adds any other page.
    /// </summary>
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

        /// <summary>Maps one 4 KB page, creating whatever PDPT, PD and PT it needs.</summary>
        public void Map(ulong linear, uint frame)
        {
            uint pdpt = NextLevel(Pml4Address, (linear >> 39) & 0x1FF);
            uint pd = NextLevel(pdpt, (linear >> 30) & 0x1FF);
            uint pt = NextLevel(pd, (linear >> 21) & 0x1FF);
            WriteQword(pt + (uint)((linear >> 12) & 0x1FF) * 8, frame | TableFlags);
            Cpu.FlushTlb();
        }

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

        private void WriteQword(uint address, ulong value)
        {
            Bus.WriteDword(address, (uint)value);
            Bus.WriteDword(address + 4, (uint)(value >> 32));
        }

        private ulong ReadQword(uint address) =>
            Bus.ReadDword(address) | ((ulong)Bus.ReadDword(address + 4) << 32);
    }
}
