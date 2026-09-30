using Aurora386.Core;
using Aurora386.Core.Cpu;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// Long mode, stage D2 (plans/long-mode.md): interrupt and exception delivery
/// through the 64-bit IDT, IRETQ, and the far transfers, against the rules of
/// SDM Vol.3A §6.14 and Vol.2 IRET/RET/JMP. A real IDT of 16-byte gates, a
/// GDT with 64-bit and compatibility code segments and a 64-bit TSS (RSP0,
/// IST1) are built in the upper canonical half, as a 64-bit kernel keeps them.
/// </summary>
public class LongModeInterruptTests
{
    private const uint CodeBase = 0x1000;
    private const uint HandlerBase = 0x2000;
    private const uint DataPage = 0x5000;
    private const byte InvalidOpcode = 6;
    private const byte GeneralProtection = 13;
    private const byte PageFault = 14;
    private const byte NoFault = 0xFF;

    private const ulong GdtLinear = 0xFFFF8000_00010000UL;
    private const uint GdtFrame = 0x9000;
    private const ulong TssLinear = 0xFFFF8000_00020000UL;
    private const uint TssFrame = 0xC000;
    private const ulong IdtLinear = 0xFFFF8000_00040000UL;
    private const uint IdtFrame = 0xB000;

    private const ushort KernelCode = 0x08;
    private const ushort KernelData = 0x10;
    private const ushort UserData = 0x1B;
    private const ushort TssSelector = 0x20;
    private const ushort UserCode = 0x33;          // index 6, RPL 3
    private const ushort CompatCode = 0x38;        // 32-bit code, DPL 0

    private const ulong KernelStackTop = 0x7F00;   // TSS.RSP0
    private const ulong IstStackTop = 0x6F00;      // TSS.IST1
    private const ulong UserStackTop = 0x4F08;     // deliberately not 16-byte aligned

    private const uint InterruptGate = 0x0E;
    private const uint TrapGate = 0x0F;

    // === delivery ===================================================================

    /// <summary>
    /// INT 80h at ring 0 through a DPL-3 interrupt gate: the frame (SS, RSP,
    /// RFLAGS, CS, RIP) goes on the current stack aligned down to 16 bytes,
    /// IF is cleared, and RIP is the gate's 64-bit offset.
    /// </summary>
    [Fact]
    public void AnInterruptAtRingZeroPushesAFiveQwordFrameOnAnAlignedStack()
    {
        var harness = new Harness(0xCD, 0x80);              // int 80h
        harness.SetGate(0x80, HandlerBase, InterruptGate, dpl: 3);
        harness.Cpu.State.Gpr[4] = 0x7F08;
        harness.Cpu.Flags.Interrupt = true;

        harness.Step();

        ulong frame = 0x7F00 - 5 * 8;
        Assert.Equal(frame, harness.Cpu.State.Gpr[4]);
        Assert.Equal(CodeBase + 2UL, harness.ReadQword(frame));            // RIP after INT
        Assert.Equal((ulong)KernelCode, harness.ReadQword(frame + 8));
        Assert.Equal(0x7F08UL, harness.ReadQword(frame + 24));              // the old RSP
        Assert.Equal((ulong)HandlerBase, harness.Cpu.State.Rip);
        Assert.False(harness.Cpu.Flags.Interrupt);
    }

    /// <summary>A trap gate (type Fh) leaves IF as it was.</summary>
    [Fact]
    public void ATrapGateKeepsTheInterruptFlag()
    {
        var harness = new Harness(0xCD, 0x81);
        harness.SetGate(0x81, HandlerBase, TrapGate, dpl: 3);
        harness.Cpu.Flags.Interrupt = true;

        harness.Step();

        Assert.True(harness.Cpu.Flags.Interrupt);
    }

    /// <summary>
    /// INT from ring 3 to a ring-0 handler switches to TSS.RSP0, loads a null
    /// SS carrying the new CPL, and saves the user SS:RSP in the frame.
    /// </summary>
    [Fact]
    public void AnInterruptFromRingThreeSwitchesToTheRingZeroStack()
    {
        var harness = new Harness(0xCD, 0x80);
        harness.SetGate(0x80, HandlerBase, InterruptGate, dpl: 3);
        harness.EnterUserMode();

        harness.Step();

        ulong frame = KernelStackTop - 5 * 8;
        Assert.Equal(0, harness.Cpu.State.Cpl);
        Assert.Equal(frame, harness.Cpu.State.Gpr[4]);
        Assert.Equal((ushort)0, harness.Cpu.State.Segments[(int)SegReg.Ss].Selector);
        Assert.Equal((ulong)UserCode, harness.ReadQword(frame + 8));
        Assert.Equal(UserStackTop, harness.ReadQword(frame + 24));
        Assert.Equal((ulong)UserData, harness.ReadQword(frame + 32));
    }

    /// <summary>INT from ring 3 through a DPL-0 gate is #GP with the IDT error code (vector × 8 + 2).</summary>
    [Fact]
    public void ASoftwareInterruptThroughAMorePrivilegedGateIsGeneralProtection()
    {
        var harness = new Harness(0xCD, 0x80);
        harness.SetGate(0x80, HandlerBase, InterruptGate, dpl: 0);
        harness.EnterUserMode();

        (byte vector, uint errorCode) = harness.FirstFault();

        Assert.Equal(GeneralProtection, vector);
        Assert.Equal(0x80u * 8 + 2, errorCode);
    }

    /// <summary>A gate of a legacy type (a 16-bit interrupt gate, type 6) does not exist in IA-32e mode: #GP(vector × 8 + 2).</summary>
    [Fact]
    public void ALegacyGateTypeIsGeneralProtection()
    {
        var harness = new Harness(0xCD, 0x80);
        harness.SetGate(0x80, HandlerBase, 0x06, dpl: 3);

        (byte vector, uint errorCode) = harness.FirstFault();

        Assert.Equal(GeneralProtection, vector);
        Assert.Equal(0x80u * 8 + 2, errorCode);
    }

    /// <summary>
    /// An exception with an error code: #GP(0) from MOV CR0 with an upper bit
    /// set is delivered with the error code below RIP, and the saved RIP is
    /// the faulting instruction's own address.
    /// </summary>
    [Fact]
    public void AnExceptionFrameCarriesItsErrorCodeAndTheFaultingRip()
    {
        var harness = new Harness(0x0F, 0x22, 0xC0);        // mov cr0, rax
        harness.SetGate(GeneralProtection, HandlerBase, InterruptGate, dpl: 0);
        harness.Cpu.State.Gpr[0] = 1UL << 32;
        harness.Cpu.State.Gpr[4] = 0x7F00;

        harness.Step();

        ulong frame = 0x7F00 - 6 * 8;
        Assert.Equal((ulong)HandlerBase, harness.Cpu.State.Rip);
        Assert.Equal(0UL, harness.ReadQword(frame));                        // the error code
        Assert.Equal((ulong)CodeBase, harness.ReadQword(frame + 8));        // the faulting RIP
    }

    /// <summary>A page fault at an address above 4 GB leaves all 64 bits of it in CR2.</summary>
    [Fact]
    public void APageFaultAtAHighAddressSetsAllOfCr2()
    {
        const ulong Missing = 0xFFFF8000_DEAD0000UL;
        var harness = new Harness(0x48, 0x8B, 0x00);        // mov rax, [rax]
        harness.SetGate(PageFault, HandlerBase, InterruptGate, dpl: 0);
        harness.Cpu.State.Gpr[0] = Missing;

        harness.Step();

        Assert.Equal(Missing, harness.Cpu.State.Cr2Full);
        Assert.Equal((ulong)HandlerBase, harness.Cpu.State.Rip);
    }

    /// <summary>A gate naming IST1 always switches to TSS.IST1, even without a privilege change.</summary>
    [Fact]
    public void AnIstGateSwitchesToItsOwnStack()
    {
        var harness = new Harness(0xCD, 0x82);
        harness.SetGate(0x82, HandlerBase, InterruptGate, dpl: 3, ist: 1);
        harness.Cpu.State.Gpr[4] = 0x7F00;

        harness.Step();

        Assert.Equal(IstStackTop - 5 * 8, harness.Cpu.State.Gpr[4]);
    }

    /// <summary>An interrupt from compatibility-mode code (CS.L clear) is delivered through the 64-bit IDT too.</summary>
    [Fact]
    public void AnInterruptFromCompatibilityModeUsesTheSixtyFourBitIdt()
    {
        var harness = new Harness(0xCD, 0x80);
        harness.SetGate(0x80, HandlerBase, InterruptGate, dpl: 3);
        harness.Cpu.State.Segments[(int)SegReg.Cs].Long = false;
        harness.Cpu.State.Segments[(int)SegReg.Cs].Big = true;
        harness.Cpu.State.Segments[(int)SegReg.Cs].Limit = 0xFFFFFFFF;
        harness.Cpu.State.Gpr[4] = 0x7F00;

        Assert.Equal((NoFault, 0u), harness.FirstFault());
        Assert.Equal((ulong)HandlerBase, harness.Cpu.State.Rip);
        Assert.True(harness.Cpu.State.Segments[(int)SegReg.Cs].Long);
    }

    // === IRETQ ======================================================================

    /// <summary>IRETQ back to ring 3 restores RIP, CS, RFLAGS, RSP and SS from the frame, and CPL becomes 3.</summary>
    [Fact]
    public void IretqReturnsToRingThree()
    {
        var harness = new Harness(0x48, 0xCF);              // iretq
        harness.Cpu.State.Gpr[4] = 0x7F00;
        harness.WriteQword(0x7F00, 0x3000);                 // RIP
        harness.WriteQword(0x7F08, UserCode);
        harness.WriteQword(0x7F10, 0x202);                  // RFLAGS: IF
        harness.WriteQword(0x7F18, UserStackTop);
        harness.WriteQword(0x7F20, UserData);

        harness.Step();

        Assert.Equal(3, harness.Cpu.State.Cpl);
        Assert.Equal(0x3000UL, harness.Cpu.State.Rip);
        Assert.Equal(UserStackTop, harness.Cpu.State.Gpr[4]);
        Assert.Equal(UserData, harness.Cpu.State.Segments[(int)SegReg.Ss].Selector);
        Assert.True(harness.Cpu.State.Segments[(int)SegReg.Cs].Long);
        Assert.True(harness.Cpu.Flags.Interrupt);
    }

    /// <summary>With NT set IRET would be a task return, which IA-32e mode does not have: #GP(0).</summary>
    [Fact]
    public void IretWithNestedTaskIsGeneralProtection()
    {
        var harness = new Harness(0x48, 0xCF);
        harness.Cpu.Flags.SetValue(harness.Cpu.Flags.Value | (1u << 14));

        Assert.Equal(GeneralProtection, harness.FirstFault().Vector);
    }

    // === far transfers ================================================================

    /// <summary>LRETQ (48 CB) at the same privilege pops RIP and CS as qwords.</summary>
    [Fact]
    public void LretqAtTheSamePrivilegePopsRipAndCs()
    {
        var harness = new Harness(0x48, 0xCB);              // lretq
        harness.Cpu.State.Gpr[4] = 0x7F00;
        harness.WriteQword(0x7F00, HandlerBase);
        harness.WriteQword(0x7F08, KernelCode);

        harness.Step();

        Assert.Equal((ulong)HandlerBase, harness.Cpu.State.Rip);
        Assert.Equal(0x7F10UL, harness.Cpu.State.Gpr[4]);
    }

    /// <summary>JMP far through memory to a 32-bit code segment enters compatibility mode (CS.L clear, D set).</summary>
    [Fact]
    public void AFarJumpToAThirtyTwoBitSegmentEntersCompatibilityMode()
    {
        var harness = new Harness(0xFF, 0x28);              // jmp far [rax] (m16:32)
        harness.Bus.WriteDword(DataPage, HandlerBase);
        harness.Bus.WriteWord(DataPage + 4, CompatCode);
        harness.Cpu.State.Gpr[0] = DataPage;

        harness.Step();

        Assert.False(harness.Cpu.State.Segments[(int)SegReg.Cs].Long);
        Assert.True(harness.Cpu.State.Segments[(int)SegReg.Cs].Big);
        Assert.Equal((ulong)HandlerBase, harness.Cpu.State.Rip);
    }

    /// <summary>INTO does not exist in 64-bit mode: #UD.</summary>
    [Fact]
    public void IntoIsInvalidInSixtyFourBitMode()
    {
        var harness = new Harness(0xCE);

        Assert.Equal(InvalidOpcode, harness.FirstFault().Vector);
    }

    // === SYSCALL / SYSRET (stage D3) =================================================

    private const uint StarIndex = 0xC0000081;
    private const uint LstarIndex = 0xC0000082;
    private const uint SfmaskIndex = 0xC0000084;

    /// <summary>
    /// STAR as Linux programs it: kernel CS in bits 47:32 (08h, so SS is 10h),
    /// user base in bits 63:48 (10h, so user SS is 18h|3 and 64-bit user CS
    /// is 20h|3 -- this GDT keeps its 64-bit user code at 30h, so a test
    /// that returns uses base 20h).
    /// </summary>
    private static ulong Star(ushort kernelBase, ushort userBase) => ((ulong)userBase << 48) | ((ulong)kernelBase << 32);

    [Fact]
    public void SyscallWithoutSceIsInvalid()
    {
        var harness = new Harness(0x0F, 0x05);              // syscall

        Assert.Equal(InvalidOpcode, harness.FirstFault().Vector);
    }

    /// <summary>
    /// SYSCALL from ring 3: RCX takes the next RIP, R11 the flags; CS and SS
    /// become STAR[47:32] and + 8 at ring 0; the SFMASK bits (IF here) clear;
    /// RIP is LSTAR.
    /// </summary>
    [Fact]
    public void SyscallEntersTheKernelThroughLstar()
    {
        var harness = new Harness(0x0F, 0x05);
        harness.EnableSyscall(Star(KernelCode, 0x20));
        Assert.True(harness.Cpu.Msrs.TryWrite(LstarIndex, HandlerBase));
        Assert.True(harness.Cpu.Msrs.TryWrite(SfmaskIndex, 1u << 9));
        harness.EnterUserMode();
        harness.Cpu.Flags.Interrupt = true;

        harness.Step();

        Assert.Equal(0, harness.Cpu.State.Cpl);
        Assert.Equal((ulong)HandlerBase, harness.Cpu.State.Rip);
        Assert.Equal(CodeBase + 2UL, harness.Cpu.State.Gpr[1]);             // RCX
        Assert.NotEqual(0UL, harness.Cpu.State.Gpr[11] & (1UL << 9));       // R11 kept IF
        Assert.Equal(KernelCode, harness.Cpu.State.Segments[(int)SegReg.Cs].Selector);
        Assert.Equal(KernelData, harness.Cpu.State.Segments[(int)SegReg.Ss].Selector);
        Assert.False(harness.Cpu.Flags.Interrupt);
        Assert.True(harness.Cpu.State.Segments[(int)SegReg.Cs].Long);
    }

    /// <summary>SYSRET with REX.W returns to 64-bit ring-3 code at RCX with RFLAGS from R11; CS = STAR[63:48] + 16, SS = + 8, both RPL 3.</summary>
    [Fact]
    public void SysretqReturnsToRingThree()
    {
        var harness = new Harness(0x48, 0x0F, 0x07);        // sysretq
        harness.EnableSyscall(Star(KernelCode, 0x20));
        harness.Cpu.State.Gpr[1] = 0x3000;
        harness.Cpu.State.Gpr[11] = 0x202;

        harness.Step();

        Assert.Equal(3, harness.Cpu.State.Cpl);
        Assert.Equal(0x3000UL, harness.Cpu.State.Rip);
        Assert.Equal((ushort)0x33, harness.Cpu.State.Segments[(int)SegReg.Cs].Selector);
        Assert.Equal((ushort)0x2B, harness.Cpu.State.Segments[(int)SegReg.Ss].Selector);
        Assert.True(harness.Cpu.State.Segments[(int)SegReg.Cs].Long);
        Assert.True(harness.Cpu.Flags.Interrupt);
    }

    [Fact]
    public void SysretAboveRingZeroIsGeneralProtection()
    {
        var harness = new Harness(0x48, 0x0F, 0x07);
        harness.EnableSyscall(Star(KernelCode, 0x20));
        harness.EnterUserMode();

        Assert.Equal(GeneralProtection, harness.FirstFault().Vector);
    }

    /// <summary>SYSRETQ to a non-canonical RCX is #GP(0) in ring 0, as Intel raises it (before any state changes).</summary>
    [Fact]
    public void SysretqToANonCanonicalAddressIsGeneralProtection()
    {
        var harness = new Harness(0x48, 0x0F, 0x07);
        harness.EnableSyscall(Star(KernelCode, 0x20));
        harness.Cpu.State.Gpr[1] = 0x00008000_00000000UL;
        int privilegeAtFault = -1;
        harness.Cpu.ProcessorException += (_, _, _, _) =>
        {
            if (privilegeAtFault < 0)
            {
                privilegeAtFault = harness.Cpu.State.Cpl;
            }
        };

        Assert.Equal(GeneralProtection, harness.FirstFault().Vector);
        Assert.Equal(0, privilegeAtFault);                  // still ring 0: nothing was committed
    }

    /// <summary>
    /// IRETD from compatibility-mode code to the same privilege pops only
    /// EIP, CS and EFLAGS; SS:ESP stay (SDM Vol.2 IRET, IA-32e mode).
    /// </summary>
    [Fact]
    public void IretInCompatibilityModeToTheSameLevelPopsThreeValues()
    {
        var harness = new Harness(0xCF);                    // iretd
        ref SegmentCache cs = ref harness.Cpu.State.Segments[(int)SegReg.Cs];
        cs.Selector = CompatCode;
        cs.Long = false;
        cs.Big = true;
        cs.Limit = 0xFFFFFFFF;
        harness.Cpu.State.Gpr[4] = 0x7F00;
        harness.Bus.WriteDword(0x7F00, HandlerBase);        // EIP
        harness.Bus.WriteDword(0x7F04, CompatCode);         // CS
        harness.Bus.WriteDword(0x7F08, 0x2);                // EFLAGS

        harness.Step();

        Assert.Equal((ulong)HandlerBase, harness.Cpu.State.Rip);
        Assert.Equal(0x7F0CUL, harness.Cpu.State.Gpr[4]);
        Assert.Equal(KernelData, harness.Cpu.State.Segments[(int)SegReg.Ss].Selector);
    }

    // === port I/O (stage D3) ============================================================

    /// <summary>
    /// OUT at ring 3 above IOPL is #GP(0) when the TSS has no bitmap: the map
    /// offset at 66h points past the TSS limit, as Linux leaves it for a task
    /// without I/O permissions.
    /// </summary>
    [Fact]
    public void OutAboveIoplWithoutABitmapIsGeneralProtection()
    {
        var harness = new Harness(0xE6, 0x80);              // out 80h, al
        harness.Bus.WriteWord(TssFrame + 0x66, 0x68);       // the map would start past the limit (67h)
        harness.EnterUserMode();

        Assert.Equal(GeneralProtection, harness.FirstFault().Vector);
    }

    /// <summary>IN EAX, DX at ring 0 zero-extends into RAX (an empty port reads all ones).</summary>
    [Fact]
    public void InEaxZeroExtendsIntoRax()
    {
        var harness = new Harness(0xED);                    // in eax, dx
        harness.Cpu.State.Gpr[2] = 0x1234;
        harness.Cpu.State.Gpr[0] = ulong.MaxValue;

        harness.Step();

        Assert.Equal(0xFFFFFFFFUL, harness.Cpu.State.Gpr[0]);
    }

    /// <summary>REP INSB stores each byte at ES:RDI, advances RDI and counts RCX down.</summary>
    [Fact]
    public void RepInsbFillsMemoryFromThePort()
    {
        var harness = new Harness(0xF3, 0x6C);              // rep insb
        harness.Cpu.State.Gpr[2] = 0x1234;
        harness.Cpu.State.Gpr[7] = DataPage;
        harness.Cpu.State.Gpr[1] = 3;

        harness.Step();

        Assert.Equal(0UL, harness.Cpu.State.Gpr[1]);
        Assert.Equal(DataPage + 3UL, harness.Cpu.State.Gpr[7]);
        Assert.Equal(0xFFFFFFu, harness.Bus.ReadDword(DataPage) & 0xFFFFFF);
    }

    // === harness ====================================================================

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

            foreach (uint page in new uint[] { CodeBase, HandlerBase, 0x3000, 0x4000, DataPage, 0x6000, 0x7000 })
            {
                Map(page, page);
            }

            Map(GdtLinear, GdtFrame);
            Map(TssLinear, TssFrame);
            Map(IdtLinear, IdtFrame);

            BuildTables();

            Cpu.State.Cr3 = Pml4Address;
            Cpu.State.Cr4 |= CpuState.Cr4Pae;
            Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable | CpuState.Cr0Paging;
            Cpu.State.Efer = CpuState.EferLongModeEnable | CpuState.EferLongModeActive;

            ref SegmentCache cs = ref Cpu.State.Segments[(int)SegReg.Cs];
            cs.Selector = KernelCode;
            cs.Long = true;
            Cpu.State.Segments[(int)SegReg.Ss].Selector = KernelData;
            Cpu.State.Rip = CodeBase;
            Cpu.State.Gpr[4] = 0x7F00;
            Cpu.FlushTlb();

            code.CopyTo(Bus.GetRamSpan(CodeBase, code.Length));
        }

        /// <summary>Puts the CPU at ring 3: user CS and SS, user RSP.</summary>
        public void EnterUserMode()
        {
            Cpu.State.Cpl = 3;
            Cpu.State.Segments[(int)SegReg.Cs].Selector = UserCode;
            Cpu.State.Segments[(int)SegReg.Ss].Selector = UserData;
            Cpu.State.Gpr[4] = UserStackTop;
        }

        /// <summary>Sets EFER.SCE and STAR, the way a 64-bit kernel enables SYSCALL.</summary>
        public void EnableSyscall(ulong star)
        {
            Cpu.State.Features = Cpu.State.Features.With(CpuFeature.Syscall);
            Cpu.State.Efer |= CpuState.EferSyscallEnable;
            Assert.True(Cpu.Msrs.TryWrite(StarIndex, star));
        }

        /// <summary>Writes a 16-byte IDT gate.</summary>
        public void SetGate(int vector, ulong offset, uint type, int dpl, int ist = 0)
        {
            ulong access = 0x80UL | ((ulong)dpl << 5) | type;
            ulong low = (offset & 0xFFFF)
                        | ((ulong)KernelCode << 16)
                        | ((ulong)ist << 32)
                        | (access << 40)
                        | (((offset >> 16) & 0xFFFF) << 48);
            ulong high = offset >> 32;

            WriteQword(IdtFrame + (uint)vector * 16, low);
            WriteQword(IdtFrame + (uint)vector * 16 + 8, high);
        }

        public void Step() => Cpu.Step();

        /// <summary>Runs one instruction; the first exception's vector and error code.</summary>
        public (byte Vector, uint ErrorCode) FirstFault()
        {
            byte vector = NoFault;
            uint errorCode = 0;
            Cpu.ProcessorException += (raised, code, _, _) =>
            {
                if (vector == NoFault)
                {
                    vector = raised;
                    errorCode = code;
                }
            };

            Cpu.Step();
            return (vector, errorCode);
        }

        private void BuildTables()
        {
            WriteQword(GdtFrame + 0x00, 0);
            WriteQword(GdtFrame + 0x08, 0x00AF9A000000FFFFUL);   // 64-bit code, DPL 0
            WriteQword(GdtFrame + 0x10, 0x00CF92000000FFFFUL);   // data, DPL 0
            WriteQword(GdtFrame + 0x18, 0x00CFF2000000FFFFUL);   // data, DPL 3
            WriteQword(GdtFrame + 0x20, 0x0000890200000067UL);   // 64-bit TSS at 00020000h ...
            WriteQword(GdtFrame + 0x28, 0x00000000FFFF8000UL);   // ... + FFFF8000_00000000h
            WriteQword(GdtFrame + 0x30, 0x00AFFA000000FFFFUL);   // 64-bit code, DPL 3
            WriteQword(GdtFrame + 0x38, 0x00CF9A000000FFFFUL);   // 32-bit code, DPL 0

            Cpu.State.GdtBase = unchecked((uint)GdtLinear);
            Cpu.State.GdtBaseHigh = (uint)(GdtLinear >> 32);
            Cpu.State.GdtLimit = 0x3F;

            Cpu.State.IdtBase = unchecked((uint)IdtLinear);
            Cpu.State.IdtBaseHigh = (uint)(IdtLinear >> 32);
            Cpu.State.IdtLimit = 0xFFF;

            Bus.WriteDword(TssFrame + 0x04, (uint)KernelStackTop);   // RSP0
            Bus.WriteDword(TssFrame + 0x24, (uint)IstStackTop);      // IST1
            Cpu.State.TaskRegister = TssSelector;
            Cpu.State.TssBase = unchecked((uint)TssLinear);
            Cpu.State.TssBaseHigh = (uint)(TssLinear >> 32);
            Cpu.State.TssLimit = 0x67;
        }

        private void Map(ulong linear, uint frame)
        {
            uint pdpt = NextLevel(Pml4Address, (linear >> 39) & 0x1FF);
            uint pd = NextLevel(pdpt, (linear >> 30) & 0x1FF);
            uint pt = NextLevel(pd, (linear >> 21) & 0x1FF);
            WriteQword(pt + (uint)((linear >> 12) & 0x1FF) * 8, frame | TableFlags);
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

        public void WriteQword(uint address, ulong value)
        {
            Bus.WriteDword(address, (uint)value);
            Bus.WriteDword(address + 4, (uint)(value >> 32));
        }

        public ulong ReadQword(uint address) =>
            Bus.ReadDword(address) | ((ulong)Bus.ReadDword(address + 4) << 32);

        public ulong ReadQword(ulong address) => ReadQword((uint)address);
    }
}
