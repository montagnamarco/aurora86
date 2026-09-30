using Aurora86.Core;
using Aurora86.Core.Cpu;
using Aurora86.Core.Memory;

namespace Aurora86.Tests;

/// <summary>
/// Long mode, stage D1 (plans/long-mode.md): the system state and
/// instructions a 64-bit kernel sets itself up with. The descriptor-table
/// registers take 10-byte operands with 64-bit bases; LTR and LLDT read
/// 16-byte system descriptors; segment loads read the GDT through its 64-bit
/// base; SWAPGS, INVLPG, CR8, SMSW/LMSW; and the CPU snapshot's version 6.
/// Encodings and rules are the SDM Vol.2 ones; descriptor layouts are Vol.3A
/// §3.4.5 and §7.2.3.
///
/// The GDT lives in the upper canonical half, FFFF8000_00010000h, as a
/// 64-bit kernel's does, so every descriptor read here has to go through the
/// 64-bit table base.
/// </summary>
public class LongModeSystemTests
{
    private const uint CodeBase = 0x1000;
    private const uint DataPage = 0x5000;
    private const byte InvalidOpcode = 6;
    private const byte GeneralProtection = 13;
    private const byte PageFault = 14;
    private const byte NoFault = 0xFF;

    private const ulong GdtLinear = 0xFFFF8000_00010000UL;
    private const uint GdtFrame = 0x9000;
    private const ulong TssBase = 0xFFFF8000_00020000UL;
    private const ulong LdtBase = 0xFFFF8000_00030000UL;

    private const ushort KernelCode = 0x08;
    private const ushort KernelData = 0x10;
    private const ushort UserData = 0x1B;          // index 3, RPL 3
    private const ushort TssSelector = 0x20;       // a 16-byte descriptor: slots 4 and 5
    private const ushort LdtSelector = 0x30;       // slots 6 and 7
    private const ushort LegacyTssSelector = 0x40; // a 16-bit TSS, which long mode does not have

    // === descriptor-table registers ================================================

    /// <summary>LGDT then SGDT in 64-bit mode move 10 bytes: a 16-bit limit and all 64 bits of the base.</summary>
    [Fact]
    public void LgdtAndSgdtCarryASixtyFourBitBase()
    {
        var harness = new Harness(0x0F, 0x01, 0x10, 0x0F, 0x01, 0x03);   // lgdt [rax] ; sgdt [rbx]
        harness.Bus.WriteWord(DataPage, 0x0047);
        harness.WriteQword(DataPage + 2, GdtLinear);
        harness.Cpu.State.Gpr[0] = DataPage;
        harness.Cpu.State.Gpr[3] = DataPage + 0x100;

        harness.Step();
        harness.Step();

        Assert.Equal(GdtLinear, harness.Cpu.State.GdtBase64);
        Assert.Equal((ushort)0x0047, harness.Cpu.State.GdtLimit);
        Assert.Equal((ushort)0x0047, harness.Bus.ReadWord(DataPage + 0x100));
        Assert.Equal(GdtLinear, harness.ReadQword(DataPage + 0x102));
    }

    /// <summary>A non-canonical base is #GP(0), and the IDTR keeps what it had.</summary>
    [Fact]
    public void LidtWithANonCanonicalBaseIsGeneralProtection()
    {
        var harness = new Harness(0x0F, 0x01, 0x18);        // lidt [rax]
        harness.Bus.WriteWord(DataPage, 0x0FFF);
        harness.WriteQword(DataPage + 2, 0x00008000_00000000UL);
        harness.Cpu.State.Gpr[0] = DataPage;

        harness.Cpu.State.IdtBase = 0x1000;
        harness.Cpu.State.IdtLimit = 0x7F;
        ulong idtBaseAtFault = ulong.MaxValue;
        harness.Cpu.ProcessorException += (_, _, _, _) =>
        {
            if (idtBaseAtFault == ulong.MaxValue)
            {
                idtBaseAtFault = harness.Cpu.State.IdtBase64;
            }
        };

        Assert.Equal(GeneralProtection, harness.FaultVector());
        Assert.Equal(0x1000UL, idtBaseAtFault);
    }

    [Fact]
    public void LgdtAtRingThreeIsGeneralProtection()
    {
        var harness = new Harness(0x0F, 0x01, 0x10);        // lgdt [rax]
        harness.Cpu.State.Gpr[0] = DataPage;
        harness.Cpu.State.Cpl = 3;

        Assert.Equal(GeneralProtection, harness.FaultVector());
    }

    // === LTR and LLDT with 16-byte descriptors ==========================================

    /// <summary>LTR loads a 64-bit TSS (type 9) with the base split over both qwords, and marks it busy (type Bh) in the GDT.</summary>
    [Fact]
    public void LtrLoadsASixteenByteTssDescriptorAndMarksItBusy()
    {
        var harness = new Harness(0x0F, 0x00, 0xD8);        // ltr ax
        harness.InstallGdt();
        harness.Cpu.State.Gpr[0] = TssSelector;

        harness.Step();

        const byte BusyTss64Access = 0x8B;
        Assert.Equal(TssBase, harness.Cpu.State.TssBase64);
        Assert.Equal(0x67u, harness.Cpu.State.TssLimit);
        Assert.Equal(TssSelector, harness.Cpu.State.TaskRegister);
        Assert.Equal(BusyTss64Access, harness.Bus.ReadByte(GdtFrame + TssSelector + 5));
    }

    /// <summary>A 16-bit TSS descriptor (type 1) does not exist in IA-32e mode: #GP(selector).</summary>
    [Fact]
    public void LtrOfALegacyTssTypeIsGeneralProtection()
    {
        var harness = new Harness(0x0F, 0x00, 0xD8);        // ltr ax
        harness.InstallGdt();
        harness.Cpu.State.Gpr[0] = LegacyTssSelector;

        Assert.Equal(GeneralProtection, harness.FaultVector());
    }

    [Fact]
    public void LldtLoadsASixteenByteLdtDescriptor()
    {
        var harness = new Harness(0x0F, 0x00, 0xD0);        // lldt ax
        harness.InstallGdt();
        harness.Cpu.State.Gpr[0] = LdtSelector;

        harness.Step();

        Assert.Equal(LdtBase, harness.Cpu.State.LdtBase64);
        Assert.Equal(0xFFu, harness.Cpu.State.LdtLimit);
    }

    // === segment registers ============================================================

    /// <summary>MOV DS reads the descriptor through the GDT's 64-bit base.</summary>
    [Fact]
    public void MovDsReadsTheDescriptorThroughTheSixtyFourBitGdtBase()
    {
        var harness = new Harness(0x8E, 0xD8);              // mov ds, ax
        harness.InstallGdt();
        harness.Cpu.State.Gpr[0] = KernelData;

        Assert.Equal(NoFault, harness.FaultVector());
        Assert.Equal(KernelData, harness.Cpu.State.Segments[(int)SegReg.Ds].Selector);
    }

    /// <summary>MOV CS is not an instruction: #UD.</summary>
    [Fact]
    public void MovToCsIsInvalid()
    {
        var harness = new Harness(0x8E, 0xC8);              // mov cs, ax
        harness.InstallGdt();

        Assert.Equal(InvalidOpcode, harness.FaultVector());
    }

    /// <summary>In 64-bit mode SS may take a null selector below ring 3 (RPL = CPL); at ring 3 it is #GP(0).</summary>
    [Theory]
    [InlineData(0, NoFault)]
    [InlineData(3, GeneralProtection)]
    public void MovSsNullDependsOnThePrivilegeLevel(byte cpl, byte expected)
    {
        var harness = new Harness(0x8E, 0xD0);              // mov ss, ax
        harness.InstallGdt();
        harness.Cpu.State.Cpl = cpl;
        harness.Cpu.State.Gpr[0] = cpl;                     // null selector with RPL = CPL

        Assert.Equal(expected, harness.FaultVector());
    }

    /// <summary>MOV r64, DS zero-extends the selector into the whole register.</summary>
    [Fact]
    public void MovFromASegmentRegisterZeroExtendsIntoASixtyFourBitRegister()
    {
        var harness = new Harness(0x48, 0x8C, 0xD8);        // mov rax, ds
        harness.Cpu.State.Segments[(int)SegReg.Ds].Selector = KernelData;
        harness.Cpu.State.Gpr[0] = ulong.MaxValue;

        harness.Step();

        Assert.Equal((ulong)KernelData, harness.Cpu.State.Gpr[0]);
    }

    /// <summary>PUSH FS pushes the selector as eight bytes; POP FS loads it back and moves RSP.</summary>
    [Fact]
    public void PushAndPopFsRoundTripTheSelector()
    {
        var harness = new Harness(0x0F, 0xA0, 0x0F, 0xA1);  // push fs ; pop fs
        harness.InstallGdt();
        harness.Cpu.State.Segments[(int)SegReg.Fs].Selector = UserData & 0xFFFC;
        harness.Cpu.State.Gpr[4] = 0x7F00;

        harness.Step();
        Assert.Equal(0x7EF8UL, harness.Cpu.State.Gpr[4]);
        Assert.Equal((ulong)(UserData & 0xFFFC), harness.ReadQword(0x7EF8));

        harness.Step();
        Assert.Equal(0x7F00UL, harness.Cpu.State.Gpr[4]);
    }

    /// <summary>POP FS of a selector past the GDT limit is #GP with RSP not yet moved.</summary>
    [Fact]
    public void PopFsThatFaultsLeavesRspUnchanged()
    {
        var harness = new Harness(0x0F, 0xA1);              // pop fs
        harness.InstallGdt();
        harness.Cpu.State.Gpr[4] = 0x7F00;
        harness.WriteQword(0x7F00, 0x0100);                  // index 32: beyond the limit

        byte vector = harness.FaultWithStackPointer(out ulong stackPointer);

        Assert.Equal(GeneralProtection, vector);
        Assert.Equal(0x7F00UL, stackPointer);
    }

    // === SWAPGS, INVLPG, CR8, MSW =========================================================

    [Fact]
    public void SwapgsExchangesGsBaseWithKernelGsBase()
    {
        var harness = new Harness(0x0F, 0x01, 0xF8);        // swapgs
        Assert.True(harness.Cpu.Msrs.TryWrite(0xC0000101, 0x00007000_00001000UL));
        Assert.True(harness.Cpu.Msrs.TryWrite(0xC0000102, 0xFFFF8000_00002000UL));

        harness.Step();

        Assert.Equal(0xFFFF8000_00002000UL, harness.Cpu.State.Segments[(int)SegReg.Gs].Base64);
        Assert.Equal(0x00007000_00001000UL, harness.Cpu.State.KernelGsBase);
    }

    [Fact]
    public void SwapgsAtRingThreeIsGeneralProtection()
    {
        var harness = new Harness(0x0F, 0x01, 0xF8);
        harness.Cpu.State.Cpl = 3;

        Assert.Equal(GeneralProtection, harness.FaultVector());
    }

    /// <summary>INVLPG at a 64-bit address drops a cached translation there; the next access walks the tables again.</summary>
    [Fact]
    public void InvlpgDropsATranslationAtASixtyFourBitAddress()
    {
        const ulong HighLinear = 0xFFFF8000_12345000UL;
        var harness = new Harness(0x0F, 0x01, 0x38);        // invlpg [rax]
        harness.Map(HighLinear, 0xA000);
        harness.Bus.WriteByte(0xA000, 0x11);
        harness.Bus.WriteByte(0xB000, 0x22);
        harness.Cpu.State.Gpr[0] = HighLinear;
        harness.ReadThroughCpu(HighLinear);                 // cache the old translation
        harness.Remap(HighLinear, 0xB000);                  // without flushing
        harness.Step();

        Assert.Equal(0x22, harness.ReadThroughCpu(HighLinear));
    }

    /// <summary>INVLPG of a non-canonical address is a no-op in 64-bit mode, not a fault.</summary>
    [Fact]
    public void InvlpgOfANonCanonicalAddressDoesNothing()
    {
        var harness = new Harness(0x0F, 0x01, 0x38);        // invlpg [rax]
        harness.Cpu.State.Gpr[0] = 0x00008000_00000000UL;

        Assert.Equal(NoFault, harness.FaultVector());
    }

    /// <summary>
    /// MOV CR8 writes the task-priority class and reads it back. This bare
    /// CPU has no local APIC, so it exercises the processor's own copy; the
    /// APIC's TPR[7:4] mapping is LocalApic.TaskPriorityClass.
    /// </summary>
    [Fact]
    public void Cr8KeepsTheTaskPriorityClass()
    {
        var harness = new Harness(0x44, 0x0F, 0x22, 0xC0, 0x44, 0x0F, 0x20, 0xC3);   // mov cr8, rax ; mov rbx, cr8
        harness.Cpu.State.Gpr[0] = 0xA;

        harness.Step();
        harness.Step();

        Assert.Equal(0xAUL, harness.Cpu.State.Gpr[3]);
    }

    [Fact]
    public void Cr8WithAReservedBitIsGeneralProtection()
    {
        var harness = new Harness(0x44, 0x0F, 0x22, 0xC0);  // mov cr8, rax
        harness.Cpu.State.Gpr[0] = 0x10;

        Assert.Equal(GeneralProtection, harness.FaultVector());
    }

    /// <summary>LMSW can set CR0's low bits but never clear PE.</summary>
    [Fact]
    public void LmswCannotClearProtectionEnable()
    {
        var harness = new Harness(0x0F, 0x01, 0xF0);        // lmsw ax
        harness.Cpu.State.Gpr[0] = CpuState.Cr0TaskSwitched;

        harness.Step();

        Assert.NotEqual(0u, harness.Cpu.State.Cr0 & CpuState.Cr0ProtectionEnable);
        Assert.NotEqual(0u, harness.Cpu.State.Cr0 & CpuState.Cr0TaskSwitched);
    }

    // === snapshot ==========================================================================

    /// <summary>CPU snapshot version 6 carries CR2's upper half and the four table bases' upper halves.</summary>
    [Fact]
    public void ASnapshotCarriesTheSixtyFourBitSystemState()
    {
        string path = Path.GetTempFileName();
        try
        {
            using (var source = new Machine())
            {
                source.RunInstructions(200);
                CpuState state = source.Cpu.State;
                state.SetCr2(0xFFFF8000_DEADB000UL);
                state.GdtBaseHigh = 0xFFFF8000;
                state.IdtBaseHigh = 0xFFFF8001;
                state.TssBaseHigh = 0xFFFF8002;
                state.LdtBaseHigh = 0xFFFF8003;
                MachineSnapshot.Capture(source).Save(path);
            }

            using var target = new Machine();
            target.RunInstructions(200);
            MachineSnapshot.Load(path).RestoreTo(target);

            CpuState restored = target.Cpu.State;
            Assert.Equal(0xFFFF8000_DEADB000UL, restored.Cr2Full);
            Assert.Equal(0xFFFF8000u, restored.GdtBaseHigh);
            Assert.Equal(0xFFFF8001u, restored.IdtBaseHigh);
            Assert.Equal(0xFFFF8002u, restored.TssBaseHigh);
            Assert.Equal(0xFFFF8003u, restored.LdtBaseHigh);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // === harness ==================================================================

    /// <summary>A bare CPU in 64-bit mode at ring 0, like LongModeDecoderTests' harness, with a GDT it can install in the upper half.</summary>
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
            Map(GdtLinear, GdtFrame);

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

        /// <summary>
        /// Writes the test GDT at <see cref="GdtFrame"/> (linear
        /// <see cref="GdtLinear"/>) and points GDTR at it: null, 64-bit
        /// kernel code, kernel data, user data, a 16-byte available 64-bit TSS
        /// at <see cref="TssBase"/>, a 16-byte LDT at <see cref="LdtBase"/>,
        /// and a 16-bit TSS descriptor.
        /// </summary>
        public void InstallGdt()
        {
            WriteQword(GdtFrame + 0x00, 0);
            WriteQword(GdtFrame + 0x08, 0x00AF9A000000FFFFUL);   // 64-bit code, DPL 0
            WriteQword(GdtFrame + 0x10, 0x00CF92000000FFFFUL);   // data, DPL 0
            WriteQword(GdtFrame + 0x18, 0x00CFF2000000FFFFUL);   // data, DPL 3
            WriteQword(GdtFrame + 0x20, 0x0000890200000067UL);   // TSS: base 31:0 = 00020000h, type 9
            WriteQword(GdtFrame + 0x28, 0x00000000FFFF8000UL);   // TSS: base 63:32
            WriteQword(GdtFrame + 0x30, 0x00008203000000FFUL);   // LDT: base 31:0 = 00030000h, type 2
            WriteQword(GdtFrame + 0x38, 0x00000000FFFF8000UL);   // LDT: base 63:32
            WriteQword(GdtFrame + 0x40, 0x0000810000000067UL);   // a 16-bit TSS (type 1)

            Cpu.State.GdtBase = unchecked((uint)GdtLinear);
            Cpu.State.GdtBaseHigh = (uint)(GdtLinear >> 32);
            Cpu.State.GdtLimit = 0x47;
        }

        public void Step() => Cpu.Step();

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

        /// <summary>Runs one instruction; the first vector, with RSP as it stood when the exception was raised.</summary>
        public byte FaultWithStackPointer(out ulong stackPointer)
        {
            byte vector = NoFault;
            ulong snapshot = 0;
            Cpu.ProcessorException += (raised, _, _, _) =>
            {
                if (vector == NoFault)
                {
                    vector = raised;
                    snapshot = Cpu.State.Gpr[4];
                }
            };

            Cpu.Step();
            stackPointer = snapshot;
            return vector;
        }

        /// <summary>Reads a byte through the TLB the way a data access would, by stepping a MOV AL, [RAX] placed on a scratch page.</summary>
        public byte ReadThroughCpu(ulong linear)
        {
            const uint ScratchCode = 0x3000;
            Map(ScratchCode, ScratchCode, flush: false);
            Bus.WriteByte(ScratchCode, 0x8A);              // mov al, [rax]
            Bus.WriteByte(ScratchCode + 1, 0x00);

            ulong savedRip = Cpu.State.Rip;
            ulong savedRax = Cpu.State.Gpr[0];
            Cpu.State.Rip = ScratchCode;
            Cpu.State.Gpr[0] = linear;
            Cpu.Step();
            byte value = (byte)Cpu.State.Gpr[0];
            Cpu.State.Rip = savedRip;
            Cpu.State.Gpr[0] = savedRax;
            return value;
        }

        /// <summary>Points an already-mapped page at another frame without flushing the TLB, the way a kernel edits a PTE before its INVLPG.</summary>
        public void Remap(ulong linear, uint frame) => Map(linear, frame, flush: false);

        public void Map(ulong linear, uint frame) => Map(linear, frame, flush: true);

        private void Map(ulong linear, uint frame, bool flush)
        {
            uint pdpt = NextLevel(Pml4Address, (linear >> 39) & 0x1FF);
            uint pd = NextLevel(pdpt, (linear >> 30) & 0x1FF);
            uint pt = NextLevel(pd, (linear >> 21) & 0x1FF);
            WriteQword(pt + (uint)((linear >> 12) & 0x1FF) * 8, frame | TableFlags);
            if (flush)
            {
                Cpu.FlushTlb();
            }
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
    }
}
