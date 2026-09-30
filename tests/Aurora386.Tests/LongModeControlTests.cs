using Aurora386.Core;
using Aurora386.Core.Cpu;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// Long mode, stage A (plans/long-mode.md): the state x86-64 adds and the
/// switches that turn it on. IA32_EFER (MSR C0000080h) through RDMSR and
/// WRMSR; EFER.LMA set and cleared by MOV CR0 toggling PG with EFER.LME
/// armed; the refusals (#GP(0)) the SDM Vol.3A §9.8.5 lists around those
/// transitions; the 64-bit registers' write rules; and the snapshot carrying
/// the new state.
///
/// The processor is a Pentium II given the long-mode, NX and SYSCALL feature
/// bits by hand, the way PaePgeTests gives a Pentium PAE: no catalog model
/// advertises long mode until stage F, and every bit exists only where the
/// feature behind it does.
///
/// Paging is identity-mapped both ways at once -- through the PDPT
/// registers stage A still translates with, and through a PML4 at CR3 that
/// the four-level walk of stage B will read -- so the same tests keep
/// fetching their code when that stage lands.
/// </summary>
public class LongModeControlTests
{
    private const uint CodeBase = 0x1000;
    private const uint StackPageLinear = 0x7000;
    private const uint Pml4Address = 0x20000;
    private const uint PdptAddress = 0x21000;
    private const uint PdAddress = 0x22000;
    private const uint PtAddress = 0x23000;

    private const uint EferIndex = 0xC0000080;
    private const byte GeneralProtection = 13;
    private const byte NoFault = 0xFF;

    private const ulong PagePresent = 1UL << 0;
    private const ulong PageWritable = 1UL << 1;

    // === EFER through RDMSR / WRMSR ============================================

    [Fact]
    public void RdmsrOfEferIsGeneralProtectionOnAProcessorWithoutLongModeNxOrSyscall()
    {
        var (cpu, bus) = Setup(0x0F, 0x32);   // rdmsr
        using (bus)
        {
            cpu.State.Features = cpu.State.Features
                .Without(CpuFeature.LongMode)
                .Without(CpuFeature.Nx)
                .Without(CpuFeature.Syscall);
            cpu.State.Ecx = EferIndex;

            Assert.Equal(GeneralProtection, FaultVector(cpu));
        }
    }

    [Fact]
    public void WrmsrSetsEferLmeAndRdmsrReadsItBack()
    {
        var (cpu, bus) = Setup(0x0F, 0x30, 0x0F, 0x32);   // wrmsr / rdmsr
        using (bus)
        {
            cpu.State.Ecx = EferIndex;
            cpu.State.Edx = 0;
            cpu.State.Eax = (uint)CpuState.EferLongModeEnable;
            cpu.Step();

            cpu.State.Eax = 0;
            cpu.Step();

            Assert.Equal((uint)CpuState.EferLongModeEnable, cpu.State.Eax);
            Assert.Equal(0u, cpu.State.Edx);
            Assert.False(cpu.State.LongModeActive);
        }
    }

    /// <summary>EFER.LMA (bit 10) is the processor's own: a WRMSR's bit 10 is ignored rather than faulting.</summary>
    [Fact]
    public void WrmsrIgnoresTheLmaBit()
    {
        var (cpu, bus) = Setup(0x0F, 0x30);   // wrmsr
        using (bus)
        {
            cpu.State.Ecx = EferIndex;
            cpu.State.Edx = 0;   // after reset EDX holds the processor signature
            cpu.State.Eax = (uint)CpuState.EferLongModeActive;

            Assert.Equal(NoFault, FaultVector(cpu));
            Assert.Equal(0UL, cpu.State.Efer);
        }
    }

    /// <summary>Bit 9 of EFER is reserved, and NXE (bit 11) is one on a processor without NX: a 1 in either is #GP(0).</summary>
    [Theory]
    [InlineData(1u << 9, false)]
    [InlineData(1u << 11, true)]
    public void WrmsrOfAnAbsentEferBitIsGeneralProtection(uint bit, bool removeNx)
    {
        var (cpu, bus) = Setup(0x0F, 0x30);   // wrmsr
        using (bus)
        {
            if (removeNx)
            {
                cpu.State.Features = cpu.State.Features.Without(CpuFeature.Nx);
            }

            cpu.State.Ecx = EferIndex;
            cpu.State.Edx = 0;
            cpu.State.Eax = bit;

            Assert.Equal(GeneralProtection, FaultVector(cpu));
            Assert.Equal(0UL, cpu.State.Efer);
        }
    }

    /// <summary>LME cannot change while CR0.PG is set: long mode is entered and left only through PG.</summary>
    [Fact]
    public void WrmsrChangingLmeWhilePagingIsOnIsGeneralProtection()
    {
        var (cpu, bus) = Setup(0x0F, 0x30);   // wrmsr
        using (bus)
        {
            IdentityMap(cpu, bus);
            cpu.State.Cr4 |= CpuState.Cr4Pae;
            cpu.State.Cr0 |= CpuState.Cr0Paging;
            cpu.State.Ecx = EferIndex;
            cpu.State.Edx = 0;
            cpu.State.Eax = (uint)CpuState.EferLongModeEnable;

            Assert.Equal(GeneralProtection, FaultVector(cpu));
            Assert.Equal(0UL, cpu.State.Efer);
        }
    }

    // === CR0.PG entering and leaving long mode ================================

    /// <summary>
    /// MOV CR0 setting PG with EFER.LME and CR4.PAE set activates long mode
    /// (EFER.LMA, bit 10). The PDPT registers are not loaded: the table at
    /// CR3 is a PML4 now, and an entry with PAE-reserved bits set in it is
    /// no #GP.
    /// </summary>
    [Fact]
    public void SettingPagingWithLmeAndPaeActivatesLongModeWithoutLoadingPdptRegisters()
    {
        var (cpu, bus) = Setup(0x0F, 0x22, 0xC0);   // mov cr0, eax
        using (bus)
        {
            IdentityMap(cpu, bus);
            ulong pdpteRegisterBefore = cpu.Pdpte[1];
            const ulong BitReservedInAPaePdpte = 1UL << 5;
            WriteQword(bus, Pml4Address + 8, PdptAddress | PagePresent | BitReservedInAPaePdpte);
            cpu.State.Cr4 |= CpuState.Cr4Pae;
            cpu.State.Efer = CpuState.EferLongModeEnable;
            cpu.State.Eax = CpuState.Cr0ProtectionEnable | CpuState.Cr0Paging;

            Assert.Equal(NoFault, FaultVector(cpu));
            Assert.True(cpu.State.LongModeActive);
            Assert.NotEqual(0u, cpu.State.Cr0 & CpuState.Cr0Paging);
            Assert.Equal(pdpteRegisterBefore, cpu.Pdpte[1]);
        }
    }

    [Fact]
    public void SettingPagingWithLmeButWithoutPaeIsGeneralProtectionAndLeavesCr0Unchanged()
    {
        var (cpu, bus) = Setup(0x0F, 0x22, 0xC0);   // mov cr0, eax
        using (bus)
        {
            cpu.State.Efer = CpuState.EferLongModeEnable;
            cpu.State.Eax = CpuState.Cr0ProtectionEnable | CpuState.Cr0Paging;
            uint cr0Before = cpu.State.Cr0;

            Assert.Equal(GeneralProtection, FaultVector(cpu));
            Assert.Equal(cr0Before, cpu.State.Cr0);
            Assert.False(cpu.State.LongModeActive);
        }
    }

    /// <summary>Without LME, setting PG with PAE is ordinary PAE paging: LMA stays clear.</summary>
    [Fact]
    public void SettingPagingWithoutLmeLeavesLongModeInactive()
    {
        var (cpu, bus) = Setup(0x0F, 0x22, 0xC0);   // mov cr0, eax
        using (bus)
        {
            IdentityMap(cpu, bus);
            cpu.State.Cr3 = PdptAddress;
            cpu.State.Cr4 |= CpuState.Cr4Pae;
            cpu.State.Eax = CpuState.Cr0ProtectionEnable | CpuState.Cr0Paging;

            Assert.Equal(NoFault, FaultVector(cpu));
            Assert.False(cpu.State.LongModeActive);
        }
    }

    /// <summary>Compatibility-mode code (CS.L clear) may turn paging off; doing so leaves long mode (LMA clear).</summary>
    [Fact]
    public void ClearingPagingFromCompatibilityModeLeavesLongMode()
    {
        var (cpu, bus) = Setup(0x0F, 0x22, 0xC0);   // mov cr0, eax
        using (bus)
        {
            EnterLongModeDirectly(cpu, bus);
            cpu.State.Eax = 0;

            Assert.Equal(NoFault, FaultVector(cpu));
            Assert.False(cpu.State.LongModeActive);
            Assert.NotEqual(0UL, cpu.State.Efer & CpuState.EferLongModeEnable);
        }
    }

    /// <summary>64-bit code (CS.L set) may not turn paging off: #GP(0), still in long mode.</summary>
    [Fact]
    public void ClearingPagingFrom64BitModeIsGeneralProtection()
    {
        var (cpu, bus) = Setup(0x0F, 0x22, 0xC0);   // mov cr0, eax
        using (bus)
        {
            EnterLongModeDirectly(cpu, bus);
            cpu.State.Segments[(int)SegReg.Cs].Long = true;
            cpu.State.Eax = 0;

            Assert.Equal(GeneralProtection, FaultWithControlState(cpu, out ControlState atFault));
            Assert.True((atFault.Efer & CpuState.EferLongModeActive) != 0);
            Assert.NotEqual(0u, atFault.Cr0 & CpuState.Cr0Paging);
        }
    }

    /// <summary>CR4.PAE cannot be cleared while long mode is active: #GP(0), CR4 unchanged.</summary>
    [Fact]
    public void ClearingPaeInLongModeIsGeneralProtection()
    {
        var (cpu, bus) = Setup(0x0F, 0x22, 0xE0);   // mov cr4, eax
        using (bus)
        {
            EnterLongModeDirectly(cpu, bus);
            cpu.State.Eax = 0;

            Assert.Equal(GeneralProtection, FaultWithControlState(cpu, out ControlState atFault));
            Assert.NotEqual(0u, atFault.Cr4 & CpuState.Cr4Pae);
        }
    }

    // === the 64-bit registers ===================================================

    /// <summary>
    /// A 32-bit register write zero-extends into bits 63:32: the rule in
    /// 64-bit mode (SDM Vol.1 §3.4.1.1), and this emulator's choice where
    /// the SDM leaves the upper half undefined, as Bochs and QEMU choose.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A32BitWriteZeroExtendsInEveryMode(bool sixtyFourBitMode)
    {
        var state = new CpuState();
        if (sixtyFourBitMode)
        {
            state.Efer = CpuState.EferLongModeEnable | CpuState.EferLongModeActive;
            state.Segments[(int)SegReg.Cs].Long = true;
        }

        state.SetReg64(0, 0x12345678_00000000UL);
        state.SetReg32(0, 0xCAFEBABE);

        Assert.Equal(0xCAFEBABEUL, state.GetReg64(0));
    }

    /// <summary>16- and 8-bit writes never touch the upper bits, in any mode.</summary>
    [Fact]
    public void SixteenAndEightBitWritesKeepTheRestOfTheRegister()
    {
        var state = new CpuState();
        state.Efer = CpuState.EferLongModeEnable | CpuState.EferLongModeActive;
        state.Segments[(int)SegReg.Cs].Long = true;
        state.SetReg64(3, 0xFFFFFFFF_FFFFFFFFUL);

        state.SetReg16(3, 0x1234);
        state.SetReg8(7, 0x56);   // BH

        Assert.Equal(0xFFFFFFFF_FFFF5634UL, state.GetReg64(3));
    }

    // === the snapshot ==========================================================

    /// <summary>
    /// CPU snapshot version 4 carries the upper halves of RAX-RDI, R8-R15,
    /// RIP's upper half, EFER and each segment's L bit.
    /// </summary>
    [Fact]
    public void ASnapshotCarriesTheLongModeState()
    {
        string path = Path.GetTempFileName();
        try
        {
            using (var source = new Machine())
            {
                source.RunInstructions(200);
                CpuState state = source.Cpu.State;
                state.Features = state.Features.With(CpuFeature.LongMode, CpuFeature.Syscall);
                state.Rip = 0x00000001_00001000UL;
                state.SetReg64(2, 0x11111111_22222222UL);
                state.SetReg64(15, 0x33333333_44444444UL);
                state.Efer = CpuState.EferLongModeEnable | CpuState.EferSyscallEnable;
                state.Segments[(int)SegReg.Cs].Long = true;
                MachineSnapshot.Capture(source).Save(path);
            }

            using var target = new Machine();
            target.RunInstructions(200);
            target.Cpu.State.Features = target.Cpu.State.Features.With(CpuFeature.LongMode, CpuFeature.Syscall);
            MachineSnapshot.Load(path).RestoreTo(target);

            CpuState restored = target.Cpu.State;
            Assert.Equal(0x00000001_00001000UL, restored.Rip);
            Assert.Equal(0x11111111_22222222UL, restored.GetReg64(2));
            Assert.Equal(0x33333333_44444444UL, restored.GetReg64(15));
            Assert.Equal(CpuState.EferLongModeEnable | CpuState.EferSyscallEnable, restored.Efer);
            Assert.True(restored.Segments[(int)SegReg.Cs].Long);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>CPU snapshot version 5 carries FS's and GS's 64-bit bases and IA32_KERNEL_GS_BASE.</summary>
    [Fact]
    public void ASnapshotCarriesSixtyFourBitSegmentBases()
    {
        const ulong FsBase = 0xFFFF8000_00401000UL;
        const ulong KernelGsBase = 0xFFFF8000_00802000UL;
        string path = Path.GetTempFileName();
        try
        {
            using (var source = new Machine())
            {
                source.CpuModel = CpuModel.PentiumII;   // the family-six MSR set; resets the CPU
                source.RunInstructions(200);
                CpuState state = source.Cpu.State;
                state.Features = state.Features.With(CpuFeature.LongMode);
                Assert.True(source.Cpu.Msrs.TryWrite(0xC0000100, FsBase));
                Assert.True(source.Cpu.Msrs.TryWrite(0xC0000102, KernelGsBase));
                MachineSnapshot.Capture(source).Save(path);
            }

            using var target = new Machine();
            target.RunInstructions(200);
            MachineSnapshot.Load(path).RestoreTo(target);

            Assert.Equal(FsBase, target.Cpu.State.Segments[(int)SegReg.Fs].Base64);
            Assert.Equal(KernelGsBase, target.Cpu.State.KernelGsBase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// EFER from a file is masked to the bits the restoring processor has, as
    /// CR4 is: a model without long mode ends up with no LME, LMA or SCE.
    /// </summary>
    [Fact]
    public void RestoringIntoAProcessorWithoutLongModeClearsEfer()
    {
        string path = Path.GetTempFileName();
        try
        {
            using (var source = new Machine())
            {
                source.RunInstructions(200);
                CpuState state = source.Cpu.State;
                state.Features = state.Features.With(CpuFeature.LongMode, CpuFeature.Syscall);
                state.Efer = CpuState.EferLongModeEnable | CpuState.EferSyscallEnable;
                MachineSnapshot.Capture(source).Save(path);
            }

            using var target = new Machine();
            target.RunInstructions(200);
            MachineSnapshot.Load(path).RestoreTo(target);

            Assert.Equal(0UL, target.Cpu.State.Efer);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // === the review's coverage additions =======================================

    /// <summary>EFER is 64 bits wide: a 1 in EDX (bits 63:32, all reserved) is #GP(0).</summary>
    [Fact]
    public void WrmsrOfEferWithAHighDwordBitIsGeneralProtection()
    {
        var (cpu, bus) = Setup(0x0F, 0x30);   // wrmsr
        using (bus)
        {
            cpu.State.Ecx = EferIndex;
            cpu.State.Edx = 1;
            cpu.State.Eax = 0;

            Assert.Equal(GeneralProtection, FaultVector(cpu));
        }
    }

    /// <summary>Clearing LME (1 to 0) while paging is on is refused just like setting it.</summary>
    [Fact]
    public void WrmsrClearingLmeWhilePagingIsOnIsGeneralProtection()
    {
        var (cpu, bus) = Setup(0x0F, 0x30);   // wrmsr
        using (bus)
        {
            EnterLongModeDirectly(cpu, bus);
            cpu.State.Ecx = EferIndex;
            cpu.State.Edx = 0;
            cpu.State.Eax = 0;

            Assert.Equal(GeneralProtection, FaultWithControlState(cpu, out ControlState atFault));
            Assert.NotEqual(0UL, atFault.Efer & CpuState.EferLongModeEnable);
        }
    }

    /// <summary>A code segment already marked 64-bit (CS.L) cannot be the one long mode is entered from: #GP(0).</summary>
    [Fact]
    public void EnteringLongModeWithCsLSetIsGeneralProtection()
    {
        var (cpu, bus) = Setup(0x0F, 0x22, 0xC0);   // mov cr0, eax
        using (bus)
        {
            IdentityMap(cpu, bus);
            cpu.State.Cr4 |= CpuState.Cr4Pae;
            cpu.State.Efer = CpuState.EferLongModeEnable;
            cpu.State.Segments[(int)SegReg.Cs].Long = true;
            cpu.State.Eax = CpuState.Cr0ProtectionEnable | CpuState.Cr0Paging;

            Assert.Equal(GeneralProtection, FaultVector(cpu));
            Assert.False(cpu.State.LongModeActive);
            Assert.Equal(0u, cpu.State.Cr0 & CpuState.Cr0Paging);
        }
    }

    /// <summary>
    /// MOV CR3 in long mode loads no PDPT registers: the new table is a PML4,
    /// and an entry with PAE-reserved bits in it is no #GP.
    /// </summary>
    [Fact]
    public void MovCr3InLongModeLoadsNoPdptRegisters()
    {
        const uint NewPml4Address = 0x24000;
        var (cpu, bus) = Setup(0x0F, 0x22, 0xD8);   // mov cr3, eax
        using (bus)
        {
            EnterLongModeDirectly(cpu, bus);
            ulong pdpteRegisterBefore = cpu.Pdpte[0];
            const ulong BitReservedInAPaePdpte = 1UL << 5;
            WriteQword(bus, NewPml4Address, PdptAddress | PagePresent | BitReservedInAPaePdpte);
            cpu.State.Eax = NewPml4Address;

            Assert.Equal(NoFault, FaultVector(cpu));
            Assert.Equal(NewPml4Address, cpu.State.Cr3);
            Assert.Equal(pdpteRegisterBefore, cpu.Pdpte[0]);
        }
    }

    // === harness ================================================================

    /// <summary>
    /// A bare family-six CPU in real mode with paging off, given PAE, long
    /// mode, NX and SYSCALL. Mirrors PaePgeTests.Setup.
    /// </summary>
    private static (Cpu cpu, PhysicalMemoryBus bus) Setup(params byte[] code)
    {
        var bus = new PhysicalMemoryBus();
        var cpu = new Cpu(bus);
        foreach (SegReg segment in Enum.GetValues<SegReg>())
        {
            cpu.State.LoadSegmentRealMode(segment, 0);
        }

        cpu.State.Esp = 0x8000;
        cpu.State.Eip = CodeBase;
        code.CopyTo(bus.GetRamSpan(CodeBase, code.Length));

        cpu.State.SelectModel(CpuModel.PentiumII);
        cpu.State.Features = cpu.State.Features.With(
            CpuFeature.Pae,
            CpuFeature.LongMode,
            CpuFeature.Nx,
            CpuFeature.Syscall);

        return (cpu, bus);
    }

    /// <summary>
    /// The state MOV CR0 leaves after entering long mode, set directly: PG
    /// (without PE, so that a fault is still delivered through the real-mode
    /// vector table this bare harness has), PAE, LME and LMA, with the code
    /// and stack pages identity-mapped.
    /// </summary>
    private static void EnterLongModeDirectly(Cpu cpu, PhysicalMemoryBus bus)
    {
        IdentityMap(cpu, bus);
        cpu.State.Cr4 |= CpuState.Cr4Pae;
        cpu.State.Cr0 |= CpuState.Cr0Paging;
        cpu.State.Efer = CpuState.EferLongModeEnable | CpuState.EferLongModeActive;
    }

    /// <summary>
    /// Identity-maps the code page and the page real-mode fault delivery
    /// pushes onto, through both the PDPT registers and a PML4 at CR3 (see
    /// the class summary).
    /// </summary>
    private static void IdentityMap(Cpu cpu, PhysicalMemoryBus bus)
    {
        WriteQword(bus, PtAddress + ((CodeBase >> 12) & 0x1FF) * 8, CodeBase | PagePresent);
        WriteQword(bus, PtAddress + ((StackPageLinear >> 12) & 0x1FF) * 8, StackPageLinear | PagePresent | PageWritable);
        WriteQword(bus, PdAddress, PtAddress | PagePresent | PageWritable);
        // Present only: bit 1 (R/W) is reserved in a PAE PDPT register, and
        // the same table serves as PAE's PDPT in the test that stays in PAE.
        WriteQword(bus, PdptAddress, PdAddress | PagePresent);
        WriteQword(bus, Pml4Address, PdptAddress | PagePresent | PageWritable);

        cpu.Pdpte[0] = PdAddress | PagePresent;
        cpu.State.Cr3 = Pml4Address;
    }

    private static void WriteQword(PhysicalMemoryBus bus, uint address, ulong value)
    {
        bus.WriteDword(address, (uint)value);
        bus.WriteDword(address + 4, (uint)(value >> 32));
    }

    /// <summary>CR0, CR4 and EFER as they stood when an exception was raised.</summary>
    private readonly record struct ControlState(uint Cr0, uint Cr4, ulong Efer);

    /// <summary>
    /// Runs one instruction and returns the first exception vector, with the
    /// control state taken inside the exception hook: in long mode a fault is
    /// delivered through the 64-bit IDT, which this bare harness leaves empty,
    /// so what the faulting instruction left behind has to be read before
    /// that delivery escalates to a reset.
    /// </summary>
    private static byte FaultWithControlState(Cpu cpu, out ControlState atFault)
    {
        byte vector = NoFault;
        ControlState snapshot = default;
        cpu.ProcessorException += (raised, _, _, _) =>
        {
            if (vector == NoFault)
            {
                vector = raised;
                snapshot = new ControlState(cpu.State.Cr0, cpu.State.Cr4, cpu.State.Efer);
            }
        };

        cpu.Step();
        atFault = snapshot;
        return vector;
    }

    /// <summary>Runs one instruction and returns the vector of the first exception it raised, or <see cref="NoFault"/>.</summary>
    private static byte FaultVector(Cpu cpu)
    {
        byte vector = NoFault;
        cpu.ProcessorException += (raised, _, _, _) =>
        {
            if (vector == NoFault)
            {
                vector = raised;
            }
        };

        cpu.Step();
        return vector;
    }
}
