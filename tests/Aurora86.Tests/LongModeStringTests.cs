using Aurora86.Core;
using Aurora86.Core.Cpu;
using Aurora86.Core.Memory;

namespace Aurora86.Tests;

/// <summary>
/// Long mode, stage C2c (plans/long-mode.md): MOVS, CMPS, STOS, LODS, SCAS
/// and the REP/REPE/REPNE prefixes in 64-bit mode, implemented in
/// Cpu.LongMode.Strings.cs.
///
/// The harness is LongModeControlFlowTests' own, trimmed the same way: a
/// Pentium II given long mode by hand, put straight into 64-bit mode, with
/// the code page and whatever data pages a test needs identity-mapped.
///
/// Cpu.LongMode.Execute.cs's default branch calls TryExecuteString64 after
/// TryExecuteAlu64 and TryExecuteControl64 decline an opcode.
/// </summary>
public class LongModeStringTests
{
    private const uint CodeBase = 0x1000;
    private const byte InvalidOpcode = 6;
    private const byte PageFault = 14;
    private const byte NoFault = 0xFF;

    /// <summary>F3h 48h A5h: REP MOVSQ copies three qwords and leaves RSI/RDI/RCX past the last one.</summary>
    [Fact]
    public void RepMovsqCopiesThreeQwords()
    {
        var harness = new Harness(0xF3, 0x48, 0xA5);   // rep movsq
        const uint Source = 0x5000;
        const uint Destination = 0x6000;
        harness.Map(Source, Source);
        harness.Map(Destination, Destination);

        harness.WriteQword(Source + 0, 0x1111111111111111UL);
        harness.WriteQword(Source + 8, 0x2222222222222222UL);
        harness.WriteQword(Source + 16, 0x3333333333333333UL);

        harness.Cpu.State.Gpr[(int)Reg.Esi] = Source;
        harness.Cpu.State.Gpr[(int)Reg.Edi] = Destination;
        harness.Cpu.State.Gpr[(int)Reg.Ecx] = 3;

        harness.StepUntilRipAdvances(maxSteps: 3);

        Assert.Equal(0x1111111111111111UL, harness.ReadQword(Destination + 0));
        Assert.Equal(0x2222222222222222UL, harness.ReadQword(Destination + 8));
        Assert.Equal(0x3333333333333333UL, harness.ReadQword(Destination + 16));
        Assert.Equal((ulong)(Source + 24), harness.Cpu.State.Gpr[(int)Reg.Esi]);
        Assert.Equal((ulong)(Destination + 24), harness.Cpu.State.Gpr[(int)Reg.Edi]);
        Assert.Equal(0UL, harness.Cpu.State.Gpr[(int)Reg.Ecx]);
        Assert.Equal(CodeBase + 3UL, harness.Cpu.State.Rip);
    }

    /// <summary>F3h AAh: REP STOSB fills memory upward with AL, with DF clear.</summary>
    [Fact]
    public void RepStosbFillsMemoryForward()
    {
        var harness = new Harness(0xF3, 0xAA);   // rep stosb
        const uint Destination = 0x5000;
        harness.Map(Destination, Destination);

        harness.Cpu.State.SetReg8((int)Reg8.Al, 0xAB);
        harness.Cpu.State.Gpr[(int)Reg.Edi] = Destination;
        harness.Cpu.State.Gpr[(int)Reg.Ecx] = 4;

        harness.StepUntilRipAdvances(maxSteps: 4);

        for (uint i = 0; i < 4; i++)
        {
            Assert.Equal((byte)0xAB, harness.Bus.ReadByte(Destination + i));
        }

        Assert.Equal((ulong)(Destination + 4), harness.Cpu.State.Gpr[(int)Reg.Edi]);
        Assert.Equal(0UL, harness.Cpu.State.Gpr[(int)Reg.Ecx]);
    }

    /// <summary>F3h AAh with DF=1: REP STOSB fills memory downward, RDI ending below where it started.</summary>
    [Fact]
    public void RepStosbWithDirectionFlagFillsMemoryBackward()
    {
        var harness = new Harness(0xF3, 0xAA);   // rep stosb
        const uint Destination = 0x5010;
        harness.Map(0x5000, 0x5000);

        harness.Cpu.Flags.Direction = true;
        harness.Cpu.State.SetReg8((int)Reg8.Al, 0xCD);
        harness.Cpu.State.Gpr[(int)Reg.Edi] = Destination;
        harness.Cpu.State.Gpr[(int)Reg.Ecx] = 4;

        harness.StepUntilRipAdvances(maxSteps: 4);

        for (uint i = 0; i < 4; i++)
        {
            Assert.Equal((byte)0xCD, harness.Bus.ReadByte(Destination - i));
        }

        Assert.Equal((ulong)(Destination - 4), harness.Cpu.State.Gpr[(int)Reg.Edi]);
        Assert.Equal(0UL, harness.Cpu.State.Gpr[(int)Reg.Ecx]);
    }

    /// <summary>ADh with no prefix: LODSD reads a dword into EAX and zero-extends it into RAX.</summary>
    [Fact]
    public void LodsdZeroExtendsIntoRax()
    {
        var harness = new Harness(0xAD);   // lodsd
        const uint Source = 0x5000;
        harness.Map(Source, Source);
        harness.Bus.WriteDword(Source, 0x12345678);

        harness.Cpu.State.Gpr[(int)Reg.Eax] = 0xFFFFFFFF_FFFFFFFFUL;
        harness.Cpu.State.Gpr[(int)Reg.Esi] = Source;

        harness.Step();

        Assert.Equal(0x0000000012345678UL, harness.Cpu.State.Gpr[(int)Reg.Eax]);
        Assert.Equal((ulong)(Source + 4), harness.Cpu.State.Gpr[(int)Reg.Esi]);
    }

    /// <summary>F3h A6h: REPE CMPSB stops at the first differing byte, leaving RCX and ZF as SUB would.</summary>
    [Fact]
    public void RepeCmpsbStopsAtFirstDifference()
    {
        var harness = new Harness(0xF3, 0xA6);   // repe cmpsb
        const uint Source = 0x5000;
        const uint Destination = 0x6000;
        harness.Map(Source, Source);
        harness.Map(Destination, Destination);

        byte[] source = [0x10, 0x20, 0x30, 0x40, 0x50];
        byte[] destination = [0x10, 0x20, 0x99, 0x40, 0x50];   // differs at index 2
        for (int i = 0; i < source.Length; i++)
        {
            harness.Bus.WriteByte(Source + (uint)i, source[i]);
            harness.Bus.WriteByte(Destination + (uint)i, destination[i]);
        }

        harness.Cpu.State.Gpr[(int)Reg.Esi] = Source;
        harness.Cpu.State.Gpr[(int)Reg.Edi] = Destination;
        harness.Cpu.State.Gpr[(int)Reg.Ecx] = 5;

        // Three iterations: two equal bytes, then the differing third one stops REPE.
        harness.StepUntilRipAdvances(maxSteps: 3);

        Assert.Equal(2UL, harness.Cpu.State.Gpr[(int)Reg.Ecx]);
        Assert.Equal((ulong)(Source + 3), harness.Cpu.State.Gpr[(int)Reg.Esi]);
        Assert.Equal((ulong)(Destination + 3), harness.Cpu.State.Gpr[(int)Reg.Edi]);
        Assert.False(harness.Cpu.Flags.Zero);
        Assert.True(harness.Cpu.Flags.Carry);   // 0x30 - 0x99 borrows
    }

    /// <summary>F2h 66h AFh: REPNE SCASW finds a matching word and stops with ZF set.</summary>
    [Fact]
    public void RepneScaswFindsAValue()
    {
        var harness = new Harness(0x66, 0xF2, 0xAF);   // repne scasw
        const uint Destination = 0x5000;
        harness.Map(Destination, Destination);

        ushort[] words = [0x1111, 0x2222, 0x3333, 0x2222];
        for (int i = 0; i < words.Length; i++)
        {
            harness.Bus.WriteWord(Destination + (uint)(i * 2), words[i]);
        }

        harness.Cpu.State.Gpr[(int)Reg.Eax] = 0x3333;
        harness.Cpu.State.Gpr[(int)Reg.Edi] = Destination;
        harness.Cpu.State.Gpr[(int)Reg.Ecx] = 4;

        // Three iterations: two mismatches, then the matching third word stops REPNE.
        harness.StepUntilRipAdvances(maxSteps: 3);

        Assert.Equal(1UL, harness.Cpu.State.Gpr[(int)Reg.Ecx]);
        Assert.Equal((ulong)(Destination + 6), harness.Cpu.State.Gpr[(int)Reg.Edi]);
        Assert.True(harness.Cpu.Flags.Zero);
    }

    /// <summary>67h F3h A5h: REP MOVSD under 67h addresses through ESI/EDI/ECX and zero-extends all three after the write.</summary>
    [Fact]
    public void AddressSizePrefixUsesEsiEdiEcxAndZeroExtends()
    {
        var harness = new Harness(0xF3, 0x67, 0xA5);   // rep movsd, 32-bit addresses
        const uint Source = 0x2000;
        const uint Destination = 0x3000;
        harness.Map(Source, Source);
        harness.Map(Destination, Destination);
        harness.Bus.WriteDword(Source, 0xCAFEBABE);

        // Garbage above bit 31 of RSI/RDI/RCX: 67h must address only the low 32 bits.
        harness.Cpu.State.Gpr[(int)Reg.Esi] = 0xFFFFFFFF_00000000UL | Source;
        harness.Cpu.State.Gpr[(int)Reg.Edi] = 0xFFFFFFFF_00000000UL | Destination;
        harness.Cpu.State.Gpr[(int)Reg.Ecx] = 0xFFFFFFFF_00000000UL | 2;

        harness.Step();   // one iteration; RCX still 1 afterwards, so RIP rewinds

        Assert.Equal(0xCAFEBABEu, harness.Bus.ReadDword(Destination));
        Assert.Equal((ulong)(Source + 4), harness.Cpu.State.Gpr[(int)Reg.Esi]);
        Assert.Equal((ulong)(Destination + 4), harness.Cpu.State.Gpr[(int)Reg.Edi]);
        Assert.Equal(1UL, harness.Cpu.State.Gpr[(int)Reg.Ecx]);
        Assert.Equal(CodeBase, harness.Cpu.State.Rip);   // rewound: one iteration remains
    }

    /// <summary>64h ACh: LODSB with an FS override reads through FS's base rather than DS's zero base.</summary>
    [Fact]
    public void FsOverrideAppliesToTheSourceOfLods()
    {
        var harness = new Harness(0x64, 0xAC);   // fs: lodsb
        const uint UnrelatedPage = 0x0000;
        const uint FsBase = 0x5000;
        harness.Map(UnrelatedPage, UnrelatedPage);
        harness.Map(FsBase, FsBase);

        harness.Bus.WriteByte(UnrelatedPage + 0x10, 0xAA);   // what DS:RSI alone would read
        harness.Bus.WriteByte(FsBase + 0x10, 0x55);          // what FS:RSI should read

        harness.Cpu.State.Segments[(int)SegReg.Fs].Base = FsBase;
        harness.Cpu.State.Gpr[(int)Reg.Esi] = 0x10;

        harness.Step();

        Assert.Equal((byte)0x55, harness.Cpu.State.GetReg8((int)Reg8.Al));
        Assert.Equal(0x11UL, harness.Cpu.State.Gpr[(int)Reg.Esi]);
    }

    /// <summary>F3h A4h: REP MOVSB with RCX already zero does nothing at all, not even one iteration.</summary>
    [Fact]
    public void RepMovsWithZeroCountDoesNothing()
    {
        var harness = new Harness(0xF3, 0xA4);   // rep movsb
        const uint Source = 0x5000;
        const uint Destination = 0x6000;
        harness.Map(Source, Source);
        harness.Map(Destination, Destination);
        harness.Bus.WriteByte(Source, 0x42);
        harness.Bus.WriteByte(Destination, 0x00);

        harness.Cpu.State.Gpr[(int)Reg.Esi] = Source;
        harness.Cpu.State.Gpr[(int)Reg.Edi] = Destination;
        harness.Cpu.State.Gpr[(int)Reg.Ecx] = 0;

        harness.Step();

        Assert.Equal((byte)0x00, harness.Bus.ReadByte(Destination));   // untouched
        Assert.Equal(Source, harness.Cpu.State.Gpr[(int)Reg.Esi]);
        Assert.Equal(Destination, harness.Cpu.State.Gpr[(int)Reg.Edi]);
        Assert.Equal(0UL, harness.Cpu.State.Gpr[(int)Reg.Ecx]);
        Assert.Equal(CodeBase + 2UL, harness.Cpu.State.Rip);   // the instruction still completes
    }

    /// <summary>
    /// F3h 48h A5h: a REP MOVSQ whose destination page is missing for the
    /// third element faults with RSI, RDI and RCX pointing at that third
    /// element, and the first two qwords already copied -- precise faults,
    /// asserted inside the hook so nothing the (stage-D) legacy delivery
    /// path does afterwards can mask what the faulting instruction itself
    /// left behind (LongModeControlFlowTests' FaultWithRegisters documents
    /// the same gap).
    /// </summary>
    [Fact]
    public void RepMovsqFaultingOnTheThirdElementLeavesPreciseState()
    {
        var harness = new Harness(0xF3, 0x48, 0xA5);   // rep movsq
        const uint Source = 0x5000;
        const uint DestinationPage = 0x6000;   // only this page is mapped
        const uint Destination = DestinationPage + 0xFF0;   // elements 1 and 2 fit, element 3 spills past 0x7000
        harness.Map(Source, Source);
        harness.Map(DestinationPage, DestinationPage);

        harness.WriteQword(Source + 0, 0x1111111111111111UL);
        harness.WriteQword(Source + 8, 0x2222222222222222UL);
        harness.WriteQword(Source + 16, 0x3333333333333333UL);

        harness.Cpu.State.Gpr[(int)Reg.Esi] = Source;
        harness.Cpu.State.Gpr[(int)Reg.Edi] = Destination;
        harness.Cpu.State.Gpr[(int)Reg.Ecx] = 3;

        bool hookRan = false;
        harness.Cpu.ProcessorException += (vector, _, _, _) =>
        {
            if (hookRan) return;
            hookRan = true;

            Assert.Equal(PageFault, vector);
            Assert.Equal((ulong)(Source + 16), harness.Cpu.State.Gpr[(int)Reg.Esi]);
            Assert.Equal((ulong)(Destination + 16), harness.Cpu.State.Gpr[(int)Reg.Edi]);
            Assert.Equal(1UL, harness.Cpu.State.Gpr[(int)Reg.Ecx]);
        };

        // Two clean iterations, then the third faults.
        harness.Step();
        harness.Step();
        harness.Step();

        Assert.True(hookRan);
        Assert.Equal(0x1111111111111111UL, harness.ReadQword(Destination + 0));
        Assert.Equal(0x2222222222222222UL, harness.ReadQword(Destination + 8));
    }

    /// <summary>F3h AAh: a REP STOSB longer than one iteration per Step needs one Step call per byte to finish.</summary>
    [Fact]
    public void LongRepStosbCompletesOverSeveralSteps()
    {
        const int Count = 40;
        var harness = new Harness(0xF3, 0xAA);   // rep stosb
        const uint Destination = 0x5000;
        harness.Map(Destination, Destination);

        harness.Cpu.State.SetReg8((int)Reg8.Al, 0x7E);
        harness.Cpu.State.Gpr[(int)Reg.Edi] = Destination;
        harness.Cpu.State.Gpr[(int)Reg.Ecx] = Count;

        for (int i = 0; i < Count; i++)
        {
            Assert.Equal(CodeBase, harness.Cpu.State.Rip);           // still rewound to the instruction
            Assert.Equal((ulong)(Count - i), harness.Cpu.State.Gpr[(int)Reg.Ecx]);

            harness.Step();
        }

        Assert.Equal(0UL, harness.Cpu.State.Gpr[(int)Reg.Ecx]);
        Assert.Equal(CodeBase + 2UL, harness.Cpu.State.Rip);         // now past the instruction
        for (uint i = 0; i < Count; i++)
        {
            Assert.Equal((byte)0x7E, harness.Bus.ReadByte(Destination + i));
        }
    }

    /// <summary>F0h F3h A4h: LOCK in front of REP MOVSB is #UD -- LockAllowedAlu64 does not carry A4h-A7h/AAh-AFh on its list.</summary>
    [Fact]
    public void LockRepMovsIsInvalidOpcode()
    {
        var harness = new Harness(0xF0, 0xF3, 0xA4);   // lock rep movsb

        byte vector = harness.FaultVector();

        Assert.Equal(InvalidOpcode, vector);
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
                CpuFeature.Msr, CpuFeature.Identification);

            Map(CodeBase, CodeBase);

            Cpu.State.Cr3 = Pml4Address;
            Cpu.State.Cr4 |= CpuState.Cr4Pae;
            Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable | CpuState.Cr0Paging;
            Cpu.State.Efer = CpuState.EferLongModeEnable | CpuState.EferLongModeActive;
            Cpu.State.Segments[(int)SegReg.Cs].Long = true;
            Cpu.State.Rip = CodeBase;
            Cpu.FlushTlb();

            code.CopyTo(Bus.GetRamSpan(CodeBase, code.Length));
        }

        public void Step() => Cpu.Step();

        /// <summary>
        /// Calls Step repeatedly -- exactly as the machine's own instruction
        /// loop does for a running guest -- until RIP moves past the
        /// instruction's own bytes (a REP run has finished) or maxSteps is
        /// reached, whichever comes first.
        /// </summary>
        public void StepUntilRipAdvances(int maxSteps)
        {
            ulong start = Cpu.State.Rip;
            for (int i = 0; i < maxSteps; i++)
            {
                Cpu.Step();
                if (Cpu.State.Rip != start)
                {
                    return;
                }
            }

            Assert.Fail($"REP did not complete within {maxSteps} Step calls.");
        }

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
