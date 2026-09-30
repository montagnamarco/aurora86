using Aurora86.Core;
using Aurora86.Core.Cpu;
using Aurora86.Core.Memory;

namespace Aurora86.Tests;

/// <summary>
/// Work package P2.1: CR4, RDTSC, RDMSR/WRMSR and the P5 MSR set, and
/// CMPXCHG8B. Each test drives the real interpreter -- decode, dispatch,
/// fault delivery -- the same way the rest of the CPU test suite does,
/// rather than calling the private instruction bodies directly.
/// </summary>
public class PentiumInstructionTests
{
    private const uint CodeBase = 0x1000;

    /// <summary>A bare CPU in real mode. See CpuFeatureTests.Setup, which this mirrors.</summary>
    private static (Cpu cpu, PhysicalMemoryBus bus) Setup(params byte[] code)
    {
        var bus = new PhysicalMemoryBus();
        var cpu = new Cpu(bus);
        foreach (SegReg s in Enum.GetValues<SegReg>())
            cpu.State.LoadSegmentRealMode(s, 0);
        cpu.State.Esp = 0x8000;
        cpu.State.Eip = CodeBase;
        code.CopyTo(bus.GetRamSpan(CodeBase, code.Length));
        return (cpu, bus);
    }

    /// <summary>Puts the CPU at ring 3 under protection, without a real GDT/IDT: enough to exercise a CPL check, not enough to survive delivering the fault past the point the test's own event handler observes it.</summary>
    private static void EnterProtectedRing3(Cpu cpu)
    {
        cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable;
        cpu.State.Cpl = 3;
    }

    /// <summary>
    /// The vector of the <i>first</i> exception the step raises. Only the
    /// first matters here: with no IDT/GDT set up, delivering it can itself
    /// fault and escalate into a double fault (vector 8), which would also
    /// fire this same event -- capturing every firing instead of just the
    /// first would report the escalation's vector, not the one the
    /// instruction under test actually raised.
    /// </summary>
    private static byte FaultVector(Cpu cpu, Action step)
    {
        byte vector = 0xFF;
        cpu.ProcessorException += (v, _, _, _) => { if (vector == 0xFF) vector = v; };
        step();
        return vector;
    }

    // === CR4 ====================================================================

    [Fact]
    public void Cr4IsReadableAndWritableOnAPentium()
    {
        // MOV CR4,EAX ; MOV EAX,CR4
        var (cpu, bus) = Setup(0x0F, 0x22, 0xE0, 0x0F, 0x20, 0xE0);
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.Pentium);
            // TSD(2) | DE(3) | MCE(6): every bit CpuFeatures.Implemented
            // backs on a Pentium today (see CpuState.Cr4Writable).
            const uint writable = CpuState.Cr4Tsd | CpuState.Cr4De | CpuState.Cr4Mce;
            cpu.State.Eax = writable;

            cpu.Step();
            Assert.Equal(writable, cpu.State.Cr4);

            cpu.State.Eax = 0;
            cpu.Step();
            Assert.Equal(writable, cpu.State.Eax);
        }
    }

    [Fact]
    public void Cr4FaultsWithUdOnA486()
    {
        var (cpu, bus) = Setup(0x0F, 0x20, 0xE0);   // MOV EAX,CR4 -- default model is a 486
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    [Fact]
    public void Cr4AcceptsVmeAndPviSinceWorkPackageP2_3()
    {
        // Work package P2.3 (Virtual-8086 Mode Extensions / Protected-mode
        // Virtual Interrupts) added CpuFeature.Vme to CpuFeatures.Implemented,
        // so CR4.VME and CR4.PVI are now writable on every model whose
        // silicon carries them (CpuState.Cr4Writable), a Pentium among them.
        // This test used to assert the opposite -- that the write was
        // rejected -- back when nothing backed the bit.
        var (cpu, bus) = Setup(0x0F, 0x22, 0xE0, 0x0F, 0x20, 0xE0);   // MOV CR4,EAX ; MOV EAX,CR4
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.Pentium);
            const uint writable = CpuState.Cr4Vme | CpuState.Cr4Pvi;
            cpu.State.Eax = writable;

            cpu.Step();
            Assert.Equal(writable, cpu.State.Cr4);

            cpu.State.Eax = 0;
            cpu.Step();
            Assert.Equal(writable, cpu.State.Eax);
        }
    }

    [Fact]
    public void Cr4RejectsAnUndefinedBit()
    {
        var (cpu, bus) = Setup(0x0F, 0x22, 0xE0);   // MOV CR4,EAX
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.Pentium);
            cpu.State.Eax = 1u << 31;

            byte vector = FaultVector(cpu, () => cpu.Step());

            Assert.Equal(13, vector);
            Assert.Equal(0u, cpu.State.Cr4);
        }
    }

    [Fact]
    public void Cr4FaultsFromRing3()
    {
        var (cpu, bus) = Setup(0x0F, 0x20, 0xE0);   // MOV EAX,CR4
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.Pentium);
            EnterProtectedRing3(cpu);

            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(13, vector);
        }
    }

    // === MOV DR4/DR5: the CR4.DE aliasing rule =================================

    [Fact]
    public void MovDr4FaultsWithUdWhenDebugExtensionsAreOn()
    {
        var (cpu, bus) = Setup(0x0F, 0x21, 0xE0);   // MOV EAX,DR4
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.Pentium);
            cpu.State.Cr4 = CpuState.Cr4De;

            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    [Fact]
    public void MovDr4AliasesDr6WhenDebugExtensionsAreOff()
    {
        var (cpu, bus) = Setup(0x0F, 0x21, 0xE0);   // MOV EAX,DR4
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.Pentium);
            cpu.State.Dr[6] = 0xAABBCCDDu;

            cpu.Step();

            Assert.Equal(0xAABBCCDDu, cpu.State.Eax);
        }
    }

    [Fact]
    public void MovToDr4AliasesDr6WhenDebugExtensionsAreOff()
    {
        var (cpu, bus) = Setup(0x0F, 0x23, 0xE0);   // MOV DR4,EAX
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.Pentium);
            cpu.State.Eax = 0x11223344u;

            cpu.Step();

            Assert.Equal(0x11223344u, cpu.State.Dr[6]);
        }
    }

    // === RDTSC ==================================================================

    [Fact]
    public void RdtscIsMonotonicAndCountsExecutedInstructions()
    {
        var (cpu, bus) = Setup(0x0F, 0x31, 0x90, 0x0F, 0x31);   // RDTSC ; NOP ; RDTSC
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.Pentium);

            cpu.Step();
            ulong first = cpu.State.Eax | ((ulong)cpu.State.Edx << 32);

            cpu.Step();   // NOP
            cpu.Step();
            ulong second = cpu.State.Eax | ((ulong)cpu.State.Edx << 32);

            // Exactly two instructions retired between the two reads: the
            // NOP, and the second RDTSC's own predecessor step.
            Assert.Equal(2ul, second - first);
        }
    }

    [Fact]
    public void RdtscFaultsWithUdOnA486()
    {
        var (cpu, bus) = Setup(0x0F, 0x31);
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    [Fact]
    public void RdtscFaultsAtRing3WhenTsdIsSet()
    {
        var (cpu, bus) = Setup(0x0F, 0x31);
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.Pentium);
            cpu.State.Cr4 = CpuState.Cr4Tsd;
            EnterProtectedRing3(cpu);

            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(13, vector);
        }
    }

    [Fact]
    public void RdtscIsAllowedAtRing3WhenTsdIsClear()
    {
        var (cpu, bus) = Setup(0x0F, 0x31);
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.Pentium);
            EnterProtectedRing3(cpu);   // TSD stays clear (Cr4 defaults to 0)

            cpu.Step();

            Assert.Equal(0u, cpu.State.Eax);
            Assert.Equal(0u, cpu.State.Edx);
        }
    }

    // === RDMSR / WRMSR ==========================================================

    [Fact]
    public void WrmsrToTscWritesTheLow32AndClearsTheHigh32()
    {
        var (cpu, bus) = Setup(0x0F, 0x30);   // WRMSR
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.Pentium);
            cpu.State.Ecx = ModelSpecificRegisters.TimeStampCounter;
            cpu.State.Edx = 0xFFFFFFFF;   // garbage in the high half, to prove it gets cleared
            cpu.State.Eax = 0x89ABCDEF;

            cpu.Step();

            Assert.Equal(0x89ABCDEFu, (uint)cpu.Tsc);
            Assert.Equal(0u, (uint)(cpu.Tsc >> 32));
        }
    }

    [Fact]
    public void RdmsrReadsTheTscFreshFromAResetCpu()
    {
        var (cpu, bus) = Setup(0x0F, 0x32);   // RDMSR
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.Pentium);
            cpu.State.Ecx = ModelSpecificRegisters.TimeStampCounter;

            cpu.Step();

            Assert.Equal(0u, cpu.State.Eax);
            Assert.Equal(0u, cpu.State.Edx);
        }
    }

    [Fact]
    public void CesrRoundTripsThroughRdmsrAndWrmsr()
    {
        var (cpu, bus) = Setup(0x0F, 0x30, 0x0F, 0x32);   // WRMSR ; RDMSR
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.Pentium);
            cpu.State.Ecx = ModelSpecificRegisters.CesrIndex;
            cpu.State.Edx = 0x9ABCDEF0u;
            cpu.State.Eax = 0x12345678u;

            cpu.Step();   // WRMSR

            cpu.State.Eax = 0;
            cpu.State.Edx = 0;
            cpu.Step();   // RDMSR, ECX unchanged

            Assert.Equal(0x12345678u, cpu.State.Eax);
            Assert.Equal(0x9ABCDEF0u, cpu.State.Edx);
        }
    }

    [Theory]
    [InlineData(0x03u)]
    [InlineData(0x1Bu)]
    public void RdmsrFaultsOnAnUnimplementedIndex(uint index)
    {
        var (cpu, bus) = Setup(0x0F, 0x32);   // RDMSR
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.Pentium);
            cpu.State.Ecx = index;

            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(13, vector);
        }
    }

    [Fact]
    public void WrmsrFaultsAtRing3()
    {
        var (cpu, bus) = Setup(0x0F, 0x30);   // WRMSR
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.Pentium);
            cpu.State.Ecx = ModelSpecificRegisters.TimeStampCounter;
            EnterProtectedRing3(cpu);

            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(13, vector);
        }
    }

    // === CMPXCHG8B ===============================================================

    private const uint QwordAddress = 0x3000;

    /// <summary>CMPXCHG8B [addr32], with the 0x67 prefix forcing 32-bit addressing so mod=00/rm=101 means a direct disp32 address rather than [DI].</summary>
    private static byte[] CmpXchg8BAt(uint address) =>
    [
        0x67, 0x0F, 0xC7, 0x0D,
        (byte)address, (byte)(address >> 8), (byte)(address >> 16), (byte)(address >> 24),
    ];

    [Fact]
    public void CmpXchg8BStoresEcxEbxWhenEqual()
    {
        var (cpu, bus) = Setup(CmpXchg8BAt(QwordAddress));
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.Pentium);
            bus.WriteDword(QwordAddress, 0x11111111u);
            bus.WriteDword(QwordAddress + 4, 0x22222222u);

            cpu.State.Eax = 0x11111111u;
            cpu.State.Edx = 0x22222222u;
            cpu.State.Ebx = 0x33333333u;
            cpu.State.Ecx = 0x44444444u;

            cpu.Flags.Carry = true; cpu.Flags.Parity = true; cpu.Flags.Adjust = true;
            cpu.Flags.Sign = true; cpu.Flags.Overflow = true;

            cpu.Step();

            Assert.True(cpu.Flags.Zero);
            Assert.Equal(0x33333333u, bus.ReadDword(QwordAddress));
            Assert.Equal(0x44444444u, bus.ReadDword(QwordAddress + 4));
            // The accumulator is untouched on a match.
            Assert.Equal(0x11111111u, cpu.State.Eax);
            Assert.Equal(0x22222222u, cpu.State.Edx);

            Assert.True(cpu.Flags.Carry);
            Assert.True(cpu.Flags.Parity);
            Assert.True(cpu.Flags.Adjust);
            Assert.True(cpu.Flags.Sign);
            Assert.True(cpu.Flags.Overflow);
        }
    }

    [Fact]
    public void CmpXchg8BLoadsTheDestinationWhenNotEqual()
    {
        var (cpu, bus) = Setup(CmpXchg8BAt(QwordAddress));
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.Pentium);
            bus.WriteDword(QwordAddress, 0x11111111u);
            bus.WriteDword(QwordAddress + 4, 0x22222222u);

            cpu.State.Eax = 0x99999999u;   // does not match the low dword
            cpu.State.Edx = 0x88888888u;
            cpu.State.Ebx = 0x33333333u;
            cpu.State.Ecx = 0x44444444u;

            cpu.Flags.Carry = true; cpu.Flags.Parity = false; cpu.Flags.Adjust = true;
            cpu.Flags.Sign = false; cpu.Flags.Overflow = true;

            cpu.Step();

            Assert.False(cpu.Flags.Zero);
            Assert.Equal(0x11111111u, cpu.State.Eax);
            Assert.Equal(0x22222222u, cpu.State.Edx);
            // The P5 performs the locked read-modify-write either way: the
            // memory ends up written back with the value it already held.
            Assert.Equal(0x11111111u, bus.ReadDword(QwordAddress));
            Assert.Equal(0x22222222u, bus.ReadDword(QwordAddress + 4));
            // ECX:EBX are only ever a source; a mismatch never touches them.
            Assert.Equal(0x33333333u, cpu.State.Ebx);
            Assert.Equal(0x44444444u, cpu.State.Ecx);

            Assert.True(cpu.Flags.Carry);
            Assert.False(cpu.Flags.Parity);
            Assert.True(cpu.Flags.Adjust);
            Assert.False(cpu.Flags.Sign);
            Assert.True(cpu.Flags.Overflow);
        }
    }

    [Fact]
    public void LockCmpXchg8BWithAMemoryOperandStillRunsOrdinarily()
    {
        // LOCK on the memory form is legal, if redundant -- the real chip
        // always performs this as a locked read-modify-write, prefix or not
        // (cpu-models.md §2). Cpu.Execute.cs's LockAllowedTwoByte has to say
        // so, or this would wrongly fault as though the prefix combination
        // itself were invalid.
        byte[] code = [0xF0, .. CmpXchg8BAt(QwordAddress)];
        var (cpu, bus) = Setup(code);
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.Pentium);
            bus.WriteDword(QwordAddress, 0x11111111u);
            bus.WriteDword(QwordAddress + 4, 0x22222222u);
            cpu.State.Eax = 0x11111111u;
            cpu.State.Edx = 0x22222222u;
            cpu.State.Ebx = 0x33333333u;
            cpu.State.Ecx = 0x44444444u;

            byte vector = 0xFF;
            cpu.ProcessorException += (v, _, _, _) => vector = v;
            cpu.Step();

            Assert.Equal(0xFF, vector);   // no fault at all
            Assert.True(cpu.Flags.Zero);
            Assert.Equal(0x33333333u, bus.ReadDword(QwordAddress));
            Assert.Equal(0x44444444u, bus.ReadDword(QwordAddress + 4));
        }
    }

    [Fact]
    public void CmpXchg8BWithARegisterOperandIsUd()
    {
        var (cpu, bus) = Setup(0x0F, 0xC7, 0xC8);   // CMPXCHG8B EAX (mod=11)
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.Pentium);
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    [Fact]
    public void LockCmpXchg8BWithARegisterOperandIsUdNotTheF00FHang()
    {
        var (cpu, bus) = Setup(0xF0, 0x0F, 0xC7, 0xC8);   // LOCK CMPXCHG8B EAX
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.Pentium);
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    [Fact]
    public void CmpXchg8BFaultsWhenTheQwordCrossesTheSegmentLimitAndMemoryIsUntouched()
    {
        var (cpu, bus) = Setup(CmpXchg8BAt(QwordAddress));
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.Pentium);
            // The low dword fits (offsets 0x3000-0x3003); the high one does
            // not (0x3004-0x3007 needs the limit to reach 0x3007).
            cpu.State.Ds.Limit = QwordAddress + 4;

            bus.WriteDword(QwordAddress, 0xCAFEF00Du);
            bus.WriteDword(QwordAddress + 4, 0xDEADBEEFu);
            cpu.State.Eax = 0xCAFEF00Du;
            cpu.State.Edx = 0xDEADBEEFu;   // would compare equal, if it ever got that far
            cpu.State.Ebx = 0x11111111u;
            cpu.State.Ecx = 0x22222222u;

            byte vector = FaultVector(cpu, () => cpu.Step());

            Assert.Equal(13, vector);
            Assert.Equal(0xCAFEF00Du, bus.ReadDword(QwordAddress));
            Assert.Equal(0xDEADBEEFu, bus.ReadDword(QwordAddress + 4));
            Assert.Equal(0xCAFEF00Du, cpu.State.Eax);
            Assert.Equal(0xDEADBEEFu, cpu.State.Edx);
            Assert.Equal(0x11111111u, cpu.State.Ebx);
            Assert.Equal(0x22222222u, cpu.State.Ecx);
        }
    }

    // === Idle cycles: the halted-batch cycle count feeds the TSC too ===========

    [Fact]
    public void AHaltedBatchAdvancesCyclesAndTheTimeStampCounterForTheWholeBatch()
    {
        using var machine = new Machine();
        machine.CpuModel = CpuModel.Pentium;   // resets the CPU

        // CLI ; HLT, placed by hand: IF is already 0 straight out of RESET,
        // and the CLI makes that explicit rather than relying on it. The
        // interrupt controller's own mask register defaults to "everything
        // masked" (Pic8259's _maskRegister = 0xFF) and nothing here ever
        // programs it otherwise, so HasPendingInterrupt stays false for the
        // whole run regardless of how many PIT ticks the batch advances.
        machine.Cpu.SetSegment(SegReg.Cs, 0);
        machine.Cpu.State.Eip = 0x2000;
        machine.Memory.WriteByte(0x2000, 0xFA);   // CLI
        machine.Memory.WriteByte(0x2001, 0xF4);   // HLT

        const long instructions = 50;
        machine.RunInstructions(instructions);

        Assert.True(machine.Cpu.State.Halted);
        Assert.Equal(instructions, machine.Cpu.Cycles);
        Assert.Equal((ulong)instructions, machine.Cpu.Tsc);
    }
}
