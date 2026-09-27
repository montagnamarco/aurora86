using Aurora386.Core.Cpu;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// Work package P3.1 (instruction and MSR half): CMOVcc, FCMOVcc, the FCOMI
/// family, RDPMC, UD2, the reserved-NOP space, SYSENTER/SYSEXIT,
/// FXSAVE/FXRSTOR, and the family-six model-specific register set
/// (plans/specs/p6.md). Each test drives the real interpreter -- decode,
/// dispatch, fault delivery -- the same way PentiumInstructionTests.cs does,
/// rather than calling the private instruction bodies directly.
///
/// The hardware-researcher's original report names thirty register-level
/// test cases by number; only the summarised note (plans/specs/p6.md) was
/// available while writing this file, so the cases below are derived
/// straight from that note's own items rather than reproducing the
/// original numbering.
/// </summary>
public class P6InstructionTests
{
    private const uint CodeBase = 0x1000;

    /// <summary>A bare CPU in real mode, mirroring PentiumInstructionTests.Setup.</summary>
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

    /// <summary>As <see cref="Setup"/>, but selecting the Pentium II and, optionally, protected mode at a given CPL.</summary>
    private static (Cpu cpu, PhysicalMemoryBus bus) SetupP6(byte cpl, params byte[] code)
    {
        var (cpu, bus) = Setup(code);
        cpu.State.SelectModel(CpuModel.PentiumII);
        if (cpl != 0xFF)
        {
            cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable;
            cpu.State.Cpl = cpl;
        }
        return (cpu, bus);
    }

    /// <summary>The vector of the first exception the step raises, or 0xFF for none. See PentiumInstructionTests' own copy for why only the first matters.</summary>
    private static byte FaultVector(Cpu cpu, Action step)
    {
        byte vector = 0xFF;
        cpu.ProcessorException += (v, _, _, _) => { if (vector == 0xFF) vector = v; };
        step();
        return vector;
    }

    /// <summary>Loads ECX/EDX:EAX for a following WRMSR step; the caller still executes it.</summary>
    private static void WriteMsr(Cpu cpu, uint index, ulong value)
    {
        cpu.State.Ecx = index;
        cpu.State.Edx = (uint)(value >> 32);
        cpu.State.Eax = (uint)value;
    }

    // === CMOVcc (0F 40-4F) ======================================================

    [Fact]
    public void CmovccMovesOnlyWhenTheConditionHolds()
    {
        // 66 0F 42 C1 = CMOVB EAX, ECX ("below" tests CF); the 0x66 prefix
        // asks for the 32-bit form, since real mode defaults to 16-bit
        // operands.
        var (cpu, bus) = SetupP6(0xFF, 0x66, 0x0F, 0x42, 0xC1);
        using (bus)
        {
            cpu.State.Eax = 0x11111111;
            cpu.State.Ecx = 0x22222222;
            cpu.Flags.Carry = false;

            cpu.Step();

            Assert.Equal(0x11111111u, cpu.State.Eax);   // condition false: untouched
        }
    }

    [Fact]
    public void CmovccMovesWhenTheConditionHolds()
    {
        var (cpu, bus) = SetupP6(0xFF, 0x66, 0x0F, 0x42, 0xC1);
        using (bus)
        {
            cpu.State.Eax = 0x11111111;
            cpu.State.Ecx = 0x22222222;
            cpu.Flags.Carry = true;

            cpu.Step();

            Assert.Equal(0x22222222u, cpu.State.Eax);
        }
    }

    [Fact]
    public void CmovccReadsAFaultingMemorySourceEvenWhenTheConditionIsFalse()
    {
        // CMOVB AX, [BX] (0F 42 07): the source has to be read, and fault,
        // before the (false) condition is ever looked at.
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0x42, 0x07);
        using (bus)
        {
            cpu.State.Ds.Limit = 0;   // any offset at all is past the limit
            cpu.State.Bx = 0;
            cpu.Flags.Carry = false;   // condition false -- the move would not happen

            byte vector = FaultVector(cpu, () => cpu.Step());

            Assert.Equal(13, vector);
        }
    }

    [Fact]
    public void CmovccFaultsWithUdWithoutCmov()
    {
        var (cpu, bus) = Setup(0x0F, 0x42, 0xC1);   // default model predates CMOV
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    // === UD2 and the reserved-NOP space (0F 0B, 0F 18-1F) =======================

    [Fact]
    public void Ud2AlwaysFaultsWithInvalidOpcode()
    {
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0x0B);
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    [Fact]
    public void LongNopWithAMemoryOperandIsANopOnPentiumIi()
    {
        // 0F 1F /0, [BX]: the reserved-NOP space, memory form.
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0x1F, 0x07, 0x90);
        using (bus)
        {
            cpu.State.Eip = CodeBase;
            uint before = cpu.State.Eip;

            cpu.Step();

            Assert.Equal(before + 3, cpu.State.Eip);   // opcode + modrm, nothing touched
        }
    }

    [Fact]
    public void LongNopWithARegisterOperandIsAlsoANopOnPentiumIi()
    {
        // 0F 1F /0, EAX (mod=11): still a NOP, unlike 0F 18's own register form.
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0x1F, 0xC0);
        using (bus)
        {
            cpu.State.Eax = 0xAAAAAAAA;

            cpu.Step();

            Assert.Equal(0xAAAAAAAAu, cpu.State.Eax);
        }
    }

    [Fact]
    public void PrefetchGroupWithARegisterOperandIsUd()
    {
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0x18, 0xC0);
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    [Fact]
    public void PrefetchGroupWithAMemoryOperandIsANop()
    {
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0x18, 0x07);
        using (bus)
        {
            uint before = cpu.State.Eip;

            cpu.Step();

            Assert.Equal(before + 3, cpu.State.Eip);
        }
    }

    [Fact]
    public void ReservedNopSpaceIsUdBeforeThePentiumPro()
    {
        var (cpu, bus) = Setup(0x0F, 0x1F, 0xC0);   // default model: a 486
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    // === RDPMC (0F 33) ===========================================================

    [Fact]
    public void RdpmcAtRing0AlwaysWorks()
    {
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0x33);
        using (bus)
        {
            cpu.State.Ecx = 0;

            cpu.Step();   // CR4.PCE clear, but ring 0 never needs it

            Assert.Equal(0u, cpu.State.Eax);
            Assert.Equal(0u, cpu.State.Edx);
        }
    }

    [Fact]
    public void RdpmcAtRing3FaultsWithoutPce()
    {
        var (cpu, bus) = SetupP6(3, 0x0F, 0x33);
        using (bus)
        {
            cpu.State.Ecx = 0;

            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(13, vector);
        }
    }

    [Fact]
    public void RdpmcAtRing3WorksWithPceSet()
    {
        var (cpu, bus) = SetupP6(3, 0x0F, 0x33);
        using (bus)
        {
            cpu.State.Cr4 = CpuState.Cr4Pce;
            cpu.State.Ecx = 1;

            cpu.Step();

            Assert.Equal(0u, cpu.State.Eax);
        }
    }

    [Fact]
    public void RdpmcRejectsAnEcxAboveOne()
    {
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0x33);
        using (bus)
        {
            cpu.State.Ecx = 2;

            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(13, vector);
        }
    }

    [Fact]
    public void RdpmcReadsPerfCtr0ThroughTheSameStorageWrmsrWrites()
    {
        // WRMSR PERFCTR0 (C1h) ; RDPMC 0
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0x30, 0x0F, 0x33);
        using (bus)
        {
            // WRMSR writes the low 32 bits and sign-extends bit 31 into bits
            // 39:32 (SDM, P6 performance monitoring): 8456789A reads back with
            // FF in EDX.
            WriteMsr(cpu, ModelSpecificRegisters.PerfCtr0Index, 0x8456_789Au);
            cpu.Step();   // WRMSR

            cpu.State.Ecx = 0;
            cpu.Step();   // RDPMC

            Assert.Equal(0x8456789Au, cpu.State.Eax);
            Assert.Equal(0xFFu, cpu.State.Edx);   // bits 39:32 of the 40-bit counter, rest zero
        }
    }

    // === FCMOVcc (DA/DB, reg 0-3) ================================================

    /// <summary>Loads a single-precision float from memory onto the x87 stack: 67 D9 05 addr32 (direct 32-bit addressing forced by the 0x67 prefix, exactly like PentiumInstructionTests' CMPXCHG8B helper).</summary>
    private static byte[] FldAt(uint address) =>
        [0x67, 0xD9, 0x05, (byte)address, (byte)(address >> 8), (byte)(address >> 16), (byte)(address >> 24)];

    [Fact]
    public void FcmovbMovesWhenCarryIsSet()
    {
        // FLD [addr] (ST0=1.0) ; FCMOVB ST0, ST1 -- but ST1 does not exist
        // yet, so load twice: the first FLD becomes ST1 once the second
        // pushes its own value as ST0.
        const uint address = 0x4000;
        byte[] code = [.. FldAt(address), .. FldAt(address + 4), 0xDA, 0xC1];
        var (cpu, bus) = SetupP6(0xFF, code);
        using (bus)
        {
            bus.WriteDword(address, BitConverter.SingleToInt32Bits(1.0f) is var a ? (uint)a : 0);
            bus.WriteDword(address + 4, (uint)BitConverter.SingleToInt32Bits(2.0f));
            cpu.Flags.Carry = true;

            cpu.Step();   // FLD ST0 <- 1.0 (now ST1 once the next FLD runs)
            cpu.Step();   // FLD ST0 <- 2.0 (ST1 == 1.0)
            cpu.Step();   // FCMOVB ST0, ST1

            Assert.Equal(1.0, cpu.Fpu.Get(0).ToDouble());
        }
    }

    [Fact]
    public void FcmovbLeavesSt0AloneWhenCarryIsClear()
    {
        const uint address = 0x4000;
        byte[] code = [.. FldAt(address), .. FldAt(address + 4), 0xDA, 0xC1];
        var (cpu, bus) = SetupP6(0xFF, code);
        using (bus)
        {
            bus.WriteDword(address, (uint)BitConverter.SingleToInt32Bits(1.0f));
            bus.WriteDword(address + 4, (uint)BitConverter.SingleToInt32Bits(2.0f));
            cpu.Flags.Carry = false;

            cpu.Step();
            cpu.Step();
            cpu.Step();

            Assert.Equal(2.0, cpu.Fpu.Get(0).ToDouble());
        }
    }

    [Fact]
    public void FcmovccIsTheOldReservedEscapeWithoutCmov()
    {
        // Before the P6, DA C1 is a reserved escape, which the unit has always
        // ignored; a 387 or 486 is not documented to raise #UD for it.
        var (cpu, bus) = Setup(0xDA, 0xC1);   // default model: a 486
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(0xFF, vector);
        }
    }

    // === FCOMI / FCOMIP / FUCOMI / FUCOMIP =======================================

    private static void LoadTwo(Cpu cpu, PhysicalMemoryBus bus, double st1, double st0)
    {
        const uint address = 0x4000;
        bus.WriteDword(address, (uint)BitConverter.SingleToInt32Bits((float)st1));
        bus.WriteDword(address + 4, (uint)BitConverter.SingleToInt32Bits((float)st0));
    }

    [Theory]
    [InlineData(2.0, 1.0, false, false, true)]    // ST1=2.0, ST0=1.0: ST0 < ST1, less -- 001
    [InlineData(1.0, 2.0, false, false, false)]   // ST1=1.0, ST0=2.0: ST0 > ST1, greater -- 000
    [InlineData(1.0, 1.0, true, false, false)]    // equal -- 100
    public void FcomiReportsTheComparisonInZfPfCf(double st1Value, double st0Value, bool zero, bool parity, bool carry)
    {
        const uint address = 0x4000;
        byte[] code = [.. FldAt(address), .. FldAt(address + 4), 0xDB, 0xF1];   // FCOMI ST0, ST1
        var (cpu, bus) = SetupP6(0xFF, code);
        using (bus)
        {
            LoadTwo(cpu, bus, st1Value, st0Value);

            cpu.Step();
            cpu.Step();
            cpu.Step();

            Assert.Equal(zero, cpu.Flags.Zero);
            Assert.Equal(parity, cpu.Flags.Parity);
            Assert.Equal(carry, cpu.Flags.Carry);
            Assert.False(cpu.Flags.Overflow);
            Assert.False(cpu.Flags.Sign);
        }
    }

    [Fact]
    public void FcomipPopsTheStack()
    {
        const uint address = 0x4000;
        byte[] code = [.. FldAt(address), .. FldAt(address + 4), 0xDF, 0xF1];   // FCOMIP ST0, ST1
        var (cpu, bus) = SetupP6(0xFF, code);
        using (bus)
        {
            LoadTwo(cpu, bus, 1.0, 1.0);

            cpu.Step();
            cpu.Step();
            Assert.True(cpu.Fpu.IsUsed(1));

            cpu.Step();   // FCOMIP: pops one entry

            Assert.Equal(1.0, cpu.Fpu.Get(0).ToDouble());   // what was ST1 is now ST0
        }
    }

    [Fact]
    public void FucomiDoesNotPop()
    {
        const uint address = 0x4000;
        byte[] code = [.. FldAt(address), .. FldAt(address + 4), 0xDB, 0xE9];   // FUCOMI ST0, ST1
        var (cpu, bus) = SetupP6(0xFF, code);
        using (bus)
        {
            LoadTwo(cpu, bus, 1.0, 2.0);

            cpu.Step();
            cpu.Step();
            cpu.Step();

            Assert.Equal(2.0, cpu.Fpu.Get(0).ToDouble());   // ST0 unchanged: no pop
        }
    }

    // === SYSENTER / SYSEXIT (0F 34 / 0F 35) ======================================

    [Fact]
    public void SysenterFaultsInRealMode()
    {
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0x34);   // no Cr0.PE set at all
        using (bus)
        {
            cpu.Msrs.SysenterCs = 8;

            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(13, vector);
        }
    }

    [Fact]
    public void SysenterFaultsWithANullSysenterCs()
    {
        var (cpu, bus) = SetupP6(3, 0x0F, 0x34);
        using (bus)
        {
            cpu.Msrs.SysenterCs = 0;

            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(13, vector);
        }
    }

    [Fact]
    public void SysenterSynthesisesFlatCachesAndLoadsEipEspFromTheMsrs()
    {
        var (cpu, bus) = SetupP6(3, 0x0F, 0x34);
        using (bus)
        {
            cpu.Msrs.SysenterCs = 0x08;
            cpu.Msrs.SysenterEip = 0x40000;
            cpu.Msrs.SysenterEsp = 0x9000;
            cpu.Flags.Interrupt = true;
            cpu.Flags.Trap = true;
            cpu.Flags.Stored |= Aurora386.Core.Cpu.Flags.Resume;

            cpu.Step();

            Assert.Equal(0x08u, cpu.State.Cs.Selector);
            Assert.Equal(0x10u, cpu.State.Ss.Selector);   // SYSENTER_CS + 8
            Assert.Equal(0u, cpu.State.Cs.Base);
            Assert.Equal(0xFFFFFFFFu, cpu.State.Cs.Limit);
            Assert.True(cpu.State.Cs.Big);
            Assert.Equal(0, cpu.State.Cpl);
            Assert.Equal(0x40000u, cpu.State.Eip);
            Assert.Equal(0x9000u, cpu.State.Esp);

            Assert.False(cpu.Flags.Interrupt);
            Assert.Equal(0u, cpu.Flags.Value & (uint)Aurora386.Core.Cpu.Flags.Resume);
            // Chappell's finding: TF survives SYSENTER, unlike IF and RF.
            Assert.True(cpu.Flags.Trap);
        }
    }

    [Fact]
    public void SysexitFaultsOutsideRing0()
    {
        var (cpu, bus) = SetupP6(3, 0x0F, 0x35);
        using (bus)
        {
            cpu.Msrs.SysenterCs = 8;

            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(13, vector);
        }
    }

    [Fact]
    public void SysexitLoadsFlatUserCachesAtDpl3FromEcxEdx()
    {
        var (cpu, bus) = SetupP6(0, 0x0F, 0x35);
        using (bus)
        {
            cpu.Msrs.SysenterCs = 0x08;
            cpu.State.Edx = 0x50000;   // EIP
            cpu.State.Ecx = 0xA000;    // ESP

            cpu.Step();

            Assert.Equal((0x08u + 16) | 3, cpu.State.Cs.Selector);
            Assert.Equal((0x08u + 24) | 3, cpu.State.Ss.Selector);
            Assert.Equal(3, cpu.State.Cpl);
            Assert.Equal(0x50000u, cpu.State.Eip);
            Assert.Equal(0xA000u, cpu.State.Esp);
        }
    }

    // === FXSAVE / FXRSTOR (0F AE /0, /1) ==========================================

    private const uint FxsaveArea = 0x5000;   // 16-byte aligned

    private static byte[] FxsaveAt(uint address) =>
        [0x67, 0x0F, 0xAE, 0x05, (byte)address, (byte)(address >> 8), (byte)(address >> 16), (byte)(address >> 24)];

    private static byte[] FxrstorAt(uint address) =>
        [0x67, 0x0F, 0xAE, 0x0D, (byte)address, (byte)(address >> 8), (byte)(address >> 16), (byte)(address >> 24)];

    [Fact]
    public void FxsaveFaultsWithUdWithoutFxsr()
    {
        var (cpu, bus) = Setup(FxsaveAt(FxsaveArea));   // default model: a 486
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    [Fact]
    public void FxsaveFaultsWithUdOnARegisterOperand()
    {
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0xAE, 0xC0);   // mod=11
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(6, vector);
        }
    }

    [Fact]
    public void FxsaveFaultsWithDeviceNotAvailableWhenEmIsSet()
    {
        // CR0.EM gives #NM (7) for FXSAVE, the x87 rule (SDM FXSAVE); the
        // first version of this test expected #UD, following a wrong note.
        var (cpu, bus) = SetupP6(0xFF, FxsaveAt(FxsaveArea));
        using (bus)
        {
            cpu.State.Cr0 |= CpuState.Cr0Emulation;

            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(7, vector);
        }
    }

    [Fact]
    public void FxsaveFaultsWithDeviceNotAvailableWhenTsIsSet()
    {
        var (cpu, bus) = SetupP6(0xFF, FxsaveAt(FxsaveArea));
        using (bus)
        {
            cpu.State.Cr0 |= CpuState.Cr0TaskSwitched;

            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(7, vector);
        }
    }

    [Fact]
    public void FxsaveFaultsWithGpOnAnUnalignedAddress()
    {
        var (cpu, bus) = SetupP6(0xFF, FxsaveAt(FxsaveArea + 1));
        using (bus)
        {
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(13, vector);
        }
    }

    [Fact]
    public void FxsaveWritesTheAbridgedTagMxcsrAndXmmAreaForAPartWithoutSse()
    {
        // Three FLDs: TOP ends at 5, physical registers 5, 6 and 7 used --
        // "mixed tags" at the byte level.
        const uint value0 = 0x4000, value1 = 0x4004, value2 = 0x4008;
        byte[] code = [.. FldAt(value0), .. FldAt(value1), .. FldAt(value2), .. FxsaveAt(FxsaveArea)];
        var (cpu, bus) = SetupP6(0xFF, code);
        using (bus)
        {
            bus.WriteDword(value0, (uint)BitConverter.SingleToInt32Bits(1.0f));
            bus.WriteDword(value1, (uint)BitConverter.SingleToInt32Bits(2.0f));
            bus.WriteDword(value2, (uint)BitConverter.SingleToInt32Bits(3.0f));

            cpu.Step();
            cpu.Step();
            cpu.Step();
            cpu.Step();   // FXSAVE

            ushort statusWord = bus.ReadWord(FxsaveArea + 2);
            Assert.Equal(5, (statusWord >> 11) & 7);   // TOP

            byte abridgedTag = bus.ReadByte(FxsaveArea + 4);
            Assert.Equal((byte)0b1110_0000, abridgedTag);   // physical registers 5, 6, 7

            Assert.Equal(0x1F80u, bus.ReadDword(FxsaveArea + 24));   // MXCSR
            Assert.Equal(0u, bus.ReadDword(FxsaveArea + 28));         // MXCSR_MASK

            for (uint i = 0; i < 8; i++)
            {
                uint xmm = FxsaveArea + 160 + i * 16;
                Assert.Equal(0u, bus.ReadDword(xmm));
                Assert.Equal(0u, bus.ReadDword(xmm + 4));
                Assert.Equal(0u, bus.ReadDword(xmm + 8));
                Assert.Equal(0u, bus.ReadDword(xmm + 12));
            }

            // FOP/FIP/FCS/FDP/FDS: zero, like FSAVE's own environment.
            Assert.Equal(0u, bus.ReadDword(FxsaveArea + 8));
            Assert.Equal(0u, bus.ReadDword(FxsaveArea + 12));
            Assert.Equal(0u, bus.ReadDword(FxsaveArea + 16));
            Assert.Equal(0u, bus.ReadDword(FxsaveArea + 20));
        }
    }

    [Fact]
    public void FxsaveThenFxrstorRoundTripsTopAndMixedTags()
    {
        const uint value0 = 0x4000, value1 = 0x4004, value2 = 0x4008;
        byte[] code =
        [
            .. FldAt(value0), .. FldAt(value1), .. FldAt(value2),
            .. FxsaveAt(FxsaveArea),
            .. FxrstorAt(FxsaveArea),
        ];
        var (cpu, bus) = SetupP6(0xFF, code);
        using (bus)
        {
            bus.WriteDword(value0, (uint)BitConverter.SingleToInt32Bits(1.0f));
            bus.WriteDword(value1, (uint)BitConverter.SingleToInt32Bits(2.0f));
            bus.WriteDword(value2, (uint)BitConverter.SingleToInt32Bits(3.0f));

            cpu.Step();   // FLD 1.0
            cpu.Step();   // FLD 2.0
            cpu.Step();   // FLD 3.0

            int topBefore = (cpu.Fpu.StatusWord >> 11) & 7;
            var usedBefore = new bool[8];
            var registersBefore = new Aurora386.Core.Cpu.Fpu.Raw80[8];
            for (int i = 0; i < 8; i++)
            {
                usedBefore[i] = cpu.Fpu.IsUsed(i);
                registersBefore[i] = cpu.Fpu.GetRaw(i);
            }

            cpu.Step();   // FXSAVE

            // Disturb the unit completely, as an intervening task switch's
            // FSAVE-based context restore would.
            cpu.Fpu.Reset();
            Assert.Equal(0, (cpu.Fpu.StatusWord >> 11) & 7);
            Assert.False(cpu.Fpu.IsUsed(0));

            cpu.Step();   // FXRSTOR

            int topAfter = (cpu.Fpu.StatusWord >> 11) & 7;
            Assert.Equal(topBefore, topAfter);

            for (int i = 0; i < 8; i++)
            {
                Assert.Equal(usedBefore[i], cpu.Fpu.IsUsed(i));
                if (usedBefore[i])
                {
                    Assert.Equal(registersBefore[i].Significand, cpu.Fpu.GetRaw(i).Significand);
                    Assert.Equal(registersBefore[i].SignExponent, cpu.Fpu.GetRaw(i).SignExponent);
                }
            }
        }
    }

    // === the family-six model-specific register set =============================

    [Fact]
    public void FamilySixAcceptsAFull64BitTscWrite()
    {
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0x30);   // WRMSR
        using (bus)
        {
            WriteMsr(cpu, ModelSpecificRegisters.TimeStampCounter, 0x1122334455667788ul);

            cpu.Step();

            Assert.Equal(0x1122334455667788ul, cpu.Tsc);
        }
    }

    [Fact]
    public void P5ModelStillClearsTheHighHalfOnATscWrite()
    {
        var (cpu, bus) = Setup(0x0F, 0x30);   // WRMSR, default model 486 -> select Pentium
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.Pentium);
            WriteMsr(cpu, ModelSpecificRegisters.TimeStampCounter, 0x99999999_89ABCDEFul);

            cpu.Step();

            Assert.Equal(0x89ABCDEFu, (uint)cpu.Tsc);
            Assert.Equal(0u, (uint)(cpu.Tsc >> 32));
        }
    }

    [Fact]
    public void Msr8BhIsGpOnAPentiumButPresentOnPentiumIi()
    {
        var (cpu486, busP5) = Setup(0x0F, 0x32);   // RDMSR
        using (busP5)
        {
            cpu486.State.SelectModel(CpuModel.Pentium);
            cpu486.State.Ecx = ModelSpecificRegisters.BiosSignIdIndex;

            byte vector = FaultVector(cpu486, () => cpu486.Step());
            Assert.Equal(13, vector);
        }

        var (cpuP6, busP6) = SetupP6(0xFF, 0x0F, 0x32);
        using (busP6)
        {
            cpuP6.State.Ecx = ModelSpecificRegisters.BiosSignIdIndex;

            cpuP6.Step();

            Assert.Equal(0u, cpuP6.State.Eax);
            Assert.Equal(0u, cpuP6.State.Edx);
        }
    }

    [Fact]
    public void BiosSignIdReadsBackZeroRevisionAfterCpuidRunsFollowingTheIdiom()
    {
        // WRMSR 8Bh, 0 ; CPUID ; RDMSR 8Bh
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0x30, 0x0F, 0xA2, 0x0F, 0x32);
        using (bus)
        {
            WriteMsr(cpu, ModelSpecificRegisters.BiosSignIdIndex, 0);
            cpu.Step();   // WRMSR

            cpu.State.Eax = 1;   // CPUID leaf 1, an arbitrary function -- the idiom updates the MSR regardless of which one runs
            cpu.Step();   // CPUID

            cpu.State.Ecx = ModelSpecificRegisters.BiosSignIdIndex;
            cpu.Step();   // RDMSR

            Assert.Equal(0u, cpu.State.Edx);   // the revision Aurora reports: always 0
        }
    }

    [Fact]
    public void PlatformIdIsReadOnlyAndAnswersZero()
    {
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0x32, 0x0F, 0x30);   // RDMSR ; WRMSR
        using (bus)
        {
            cpu.State.Ecx = ModelSpecificRegisters.PlatformIdIndex;
            cpu.Step();
            Assert.Equal(0u, cpu.State.Eax);
            Assert.Equal(0u, cpu.State.Edx);

            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(13, vector);
        }
    }

    [Fact]
    public void ApicBaseResetsToTheBspEnabledValueAndRoundTrips()
    {
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0x32, 0x0F, 0x30, 0x0F, 0x32);   // RDMSR ; WRMSR ; RDMSR
        using (bus)
        {
            cpu.State.Ecx = ModelSpecificRegisters.ApicBaseIndex;
            cpu.Step();
            Assert.Equal(0xFEE00900u, cpu.State.Eax);
            Assert.Equal(0u, cpu.State.Edx);

            WriteMsr(cpu, ModelSpecificRegisters.ApicBaseIndex, 0xFEE01234ul);
            cpu.Step();   // WRMSR

            cpu.State.Ecx = ModelSpecificRegisters.ApicBaseIndex;
            cpu.Step();   // RDMSR

            Assert.Equal(0xFEE01234u, cpu.State.Eax);
        }
    }

    [Fact]
    public void MtrrCapReportsEightVariableRangesFixedAndWriteCombiningAndIsReadOnly()
    {
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0x32, 0x0F, 0x30);   // RDMSR ; WRMSR
        using (bus)
        {
            cpu.State.Ecx = ModelSpecificRegisters.MtrrCapIndex;
            cpu.Step();
            Assert.Equal(0x508u, cpu.State.Eax);   // VCNT=8, FIX, WC
            Assert.Equal(0u, cpu.State.Edx);

            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(13, vector);
        }
    }

    [Fact]
    public void MtrrDefTypeRejectsAReservedBitAndRoundTripsAValidWrite()
    {
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0x30);   // WRMSR
        using (bus)
        {
            WriteMsr(cpu, ModelSpecificRegisters.MtrrDefTypeIndex, 1u << 9);   // reserved bit
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(13, vector);
        }

        var (cpu2, bus2) = SetupP6(0xFF, 0x0F, 0x30, 0x0F, 0x32);   // WRMSR ; RDMSR
        using (bus2)
        {
            const uint validValue = 0x6 | (1u << 10) | (1u << 11);   // WB, FE, E
            WriteMsr(cpu2, ModelSpecificRegisters.MtrrDefTypeIndex, validValue);
            cpu2.Step();

            cpu2.State.Ecx = ModelSpecificRegisters.MtrrDefTypeIndex;
            cpu2.Step();

            Assert.Equal(validValue, cpu2.State.Eax);
        }
    }

    [Fact]
    public void MtrrVariableBaseRejectsAReservedBitAndAnInvalidType()
    {
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0x30);
        using (bus)
        {
            WriteMsr(cpu, ModelSpecificRegisters.MtrrPhysBase0Index, 1u << 3);   // reserved bit 3
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(13, vector);
        }

        var (cpu2, bus2) = SetupP6(0xFF, 0x0F, 0x30);
        using (bus2)
        {
            WriteMsr(cpu2, ModelSpecificRegisters.MtrrPhysBase0Index, 2);   // reserved memory type
            byte vector = FaultVector(cpu2, () => cpu2.Step());
            Assert.Equal(13, vector);
        }
    }

    [Fact]
    public void MtrrVariablePairRoundTrips()
    {
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0x30, 0x0F, 0x30, 0x0F, 0x32, 0x0F, 0x32);
        using (bus)
        {
            const ulong baseValue = 0x12345000ul | 6;   // WB, address 0x12345000
            const ulong maskValue = 0xFFFFF000ul | (1u << 11);   // V set

            WriteMsr(cpu, ModelSpecificRegisters.MtrrPhysBase0Index, baseValue);
            cpu.Step();

            WriteMsr(cpu, ModelSpecificRegisters.MtrrPhysBase0Index + 1, maskValue);
            cpu.Step();

            cpu.State.Ecx = ModelSpecificRegisters.MtrrPhysBase0Index;
            cpu.Step();
            Assert.Equal((uint)baseValue, cpu.State.Eax);

            cpu.State.Ecx = ModelSpecificRegisters.MtrrPhysBase0Index + 1;
            cpu.Step();
            Assert.Equal((uint)maskValue, cpu.State.Eax);
        }
    }

    [Fact]
    public void FixedMtrrRejectsAnInvalidByteAndRoundTripsAValidWrite()
    {
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0x30);
        using (bus)
        {
            WriteMsr(cpu, ModelSpecificRegisters.MtrrFix64KIndex, 3);   // byte 0 invalid
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(13, vector);
        }

        var (cpu2, bus2) = SetupP6(0xFF, 0x0F, 0x30, 0x0F, 0x32);
        using (bus2)
        {
            const ulong allWriteBack = 0x0606060606060606ul;
            WriteMsr(cpu2, ModelSpecificRegisters.MtrrFix64KIndex, allWriteBack);
            cpu2.Step();

            cpu2.State.Ecx = ModelSpecificRegisters.MtrrFix64KIndex;
            cpu2.Step();

            ulong readBack = cpu2.State.Eax | ((ulong)cpu2.State.Edx << 32);
            Assert.Equal(allWriteBack, readBack);
        }
    }

    [Fact]
    public void PatResetsToTheDocumentedValueAndRejectsAnInvalidByte()
    {
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0x32);   // RDMSR
        using (bus)
        {
            cpu.State.Ecx = ModelSpecificRegisters.PatIndex;
            cpu.Step();

            ulong reset = cpu.State.Eax | ((ulong)cpu.State.Edx << 32);
            Assert.Equal(0x0007040600070406ul, reset);
        }

        var (cpu2, bus2) = SetupP6(0xFF, 0x0F, 0x30);   // WRMSR
        using (bus2)
        {
            WriteMsr(cpu2, ModelSpecificRegisters.PatIndex, 3);   // byte 0 invalid
            byte vector = FaultVector(cpu2, () => cpu2.Step());
            Assert.Equal(13, vector);
        }
    }

    [Fact]
    public void McgCapReportsFiveBanksAndMcgCtlPresentAndIsReadOnly()
    {
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0x32, 0x0F, 0x30);   // RDMSR ; WRMSR
        using (bus)
        {
            cpu.State.Ecx = ModelSpecificRegisters.McgCapIndex;
            cpu.Step();
            Assert.Equal(0x105u, cpu.State.Eax);   // 5 banks | MCG_CTL_P

            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(13, vector);
        }
    }

    [Fact]
    public void McBankRegistersRoundTrip()
    {
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0x30, 0x0F, 0x32);   // WRMSR ; RDMSR
        using (bus)
        {
            WriteMsr(cpu, ModelSpecificRegisters.McBank0Index + 1, 0xDEADBEEFCAFEF00Dul);   // bank 0's STATUS
            cpu.Step();

            cpu.State.Ecx = ModelSpecificRegisters.McBank0Index + 1;
            cpu.Step();

            Assert.Equal(0xDEADBEEFCAFEF00Dul, cpu.State.Eax | ((ulong)cpu.State.Edx << 32));
        }
    }

    [Fact]
    public void DebugCtlMsrPerfEvtSelAndEblCrPoweronRoundTrip()
    {
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0x30, 0x0F, 0x32);   // WRMSR ; RDMSR
        using (bus)
        {
            (uint Index, ulong Value)[] cases =
            [
                (ModelSpecificRegisters.DebugCtlMsrIndex, 0x1ul),
                (ModelSpecificRegisters.PerfEvtSel0Index, 0x00412E),
                (ModelSpecificRegisters.PerfEvtSel1Index, 0x004300),
                (ModelSpecificRegisters.EblCrPoweronIndex, 0x12000000ul),
            ];

            foreach ((uint index, ulong value) in cases)
            {
                cpu.State.Eip = CodeBase;
                WriteMsr(cpu, index, value);
                cpu.Step();

                cpu.State.Eip = CodeBase + 2;
                cpu.State.Ecx = index;
                cpu.Step();

                Assert.Equal(value, cpu.State.Eax | ((ulong)cpu.State.Edx << 32));
            }
        }
    }

    [Fact]
    public void AnUnimplementedIndexIsGpOnPentiumIiToo()
    {
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0x32);
        using (bus)
        {
            cpu.State.Ecx = 0x1D0;   // not in the family-six set at all

            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(13, vector);
        }
    }

    [Fact]
    public void BiosUpdtTrigAcceptsAWriteButRefusesAread()
    {
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0x30, 0x0F, 0x32);   // WRMSR ; RDMSR
        using (bus)
        {
            WriteMsr(cpu, ModelSpecificRegisters.BiosUpdtTrigIndex, 0);
            cpu.Step();   // accepted

            cpu.State.Ecx = ModelSpecificRegisters.BiosUpdtTrigIndex;
            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(13, vector);
        }
    }

    // === CR4 on Pentium II (bits 0-9 writable, 10+ #GP) ==========================

    [Fact]
    public void Cr4AcceptsPceAndOsFxsrOnPentiumIi()
    {
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0x22, 0xE0, 0x0F, 0x20, 0xE0);   // MOV CR4,EAX ; MOV EAX,CR4
        using (bus)
        {
            const uint writable = CpuState.Cr4Pce | CpuState.Cr4OsFxsr;
            cpu.State.Eax = writable;

            cpu.Step();
            Assert.Equal(writable, cpu.State.Cr4);

            cpu.State.Eax = 0;
            cpu.Step();
            Assert.Equal(writable, cpu.State.Eax);
        }
    }

    [Fact]
    public void Cr4RejectsOsxmmexcptOnPentiumIi()
    {
        var (cpu, bus) = SetupP6(0xFF, 0x0F, 0x22, 0xE0);   // MOV CR4,EAX
        using (bus)
        {
            cpu.State.Eax = 1u << 10;   // OSXMMEXCPT: SSE, not implemented

            byte vector = FaultVector(cpu, () => cpu.Step());
            Assert.Equal(13, vector);
        }
    }
}
