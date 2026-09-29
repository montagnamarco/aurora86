using Aurora386.Core;
using Aurora386.Core.Cpu;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// Long mode, stage B (plans/long-mode.md): the four-level walk EFER.LMA
/// selects -- PML4E, PDPTE, PDE, PTE, each 64 bits -- and execute-disable
/// (XD, bit 63, under EFER.NXE) in both it and the PAE format. Tables are
/// built by hand (SDM Vol.3A Tables 4-14 to 4-20) and driven through
/// <c>ReadMemoryByte</c>/<c>WriteMemoryByte</c> the way PaePgeTests drives
/// the PAE walk; instruction fetches run through <c>Step</c>.
///
/// The processor is a Pentium II given PAE, PAT, long mode and NX by hand
/// (no catalog model advertises long mode until stage F), put straight into
/// long mode: CR0.PG, CR4.PAE, EFER.LME and LMA set directly. PE is left
/// clear, as LongModeControlTests does, so a fault is delivered through the
/// real-mode table rather than a 64-bit IDT this stage does not have yet.
/// </summary>
public class LongModePagingTests
{
    private const uint Pml4Address = 0x20000;
    private const uint PdptAddress = 0x21000;
    private const uint PdAddress = 0x22000;
    private const uint PtAddress = 0x23000;

    private const uint DataLinear = 0x00405000;   // PML4 0, PDPT 0, PD 2, PT 5
    private const uint DataFrame = 0x00150000;

    private const ulong Present = 1UL << 0;
    private const ulong ReadWrite = 1UL << 1;
    private const ulong UserSupervisor = 1UL << 2;
    private const ulong Accessed = 1UL << 5;
    private const ulong Dirty = 1UL << 6;
    private const ulong PageSize = 1UL << 7;
    private const ulong ExecuteDisable = 1UL << 63;
    private const ulong AllRights = Present | ReadWrite | UserSupervisor;

    private const uint PageFaultPresent = 1u << 0;
    private const uint PageFaultWrite = 1u << 1;
    private const uint PageFaultReserved = 1u << 3;
    private const uint PageFaultInstructionFetch = 1u << 4;

    private const uint EferIndex = 0xC0000080;

    // === the walk ===============================================================

    [Fact]
    public void AFourKilobytePageTranslatesThroughAllFourLevels()
    {
        var (cpu, bus) = LongModeCpu();
        using (bus)
        {
            MapFourKilobytePage(bus, DataLinear, DataFrame);
            bus.WriteByte(DataFrame + 0x123, 0x5A);

            Assert.Equal(0x5A, cpu.ReadMemoryByte(SegReg.Ds, DataLinear + 0x123));
        }
    }

    /// <summary>The accessed bit (5) is set on every level walked; the dirty bit (6) on the PTE alone, by a write.</summary>
    [Fact]
    public void AWriteSetsAccessedOnEveryLevelAndDirtyOnThePte()
    {
        var (cpu, bus) = LongModeCpu();
        using (bus)
        {
            MapFourKilobytePage(bus, DataLinear, DataFrame);

            cpu.WriteMemoryByte(SegReg.Ds, DataLinear, 0x11);

            Assert.Equal(0x11, bus.ReadByte(DataFrame));
            Assert.NotEqual(0UL, ReadQword(bus, Pml4Address) & Accessed);
            Assert.NotEqual(0UL, ReadQword(bus, PdptAddress) & Accessed);
            Assert.NotEqual(0UL, ReadQword(bus, PdeAddress(DataLinear)) & Accessed);
            Assert.Equal(0UL, ReadQword(bus, PdeAddress(DataLinear)) & Dirty);
            Assert.NotEqual(0UL, ReadQword(bus, PteAddress(DataLinear)) & (Accessed | Dirty));
        }
    }

    /// <summary>A PDE with PS (bit 7) maps 2 MB directly; its own accessed and dirty bits record the access.</summary>
    [Fact]
    public void ATwoMegabytePdeMapsDirectly()
    {
        const uint LargeFrame = 0x00600000;
        var (cpu, bus) = LongModeCpu();
        using (bus)
        {
            WriteQword(bus, Pml4Address, PdptAddress | AllRights);
            WriteQword(bus, PdptAddress, PdAddress | AllRights);
            WriteQword(bus, PdeAddress(DataLinear), LargeFrame | AllRights | PageSize);

            cpu.WriteMemoryByte(SegReg.Ds, DataLinear, 0x22);

            uint offsetInLargePage = DataLinear & 0x1FFFFF;
            Assert.Equal(0x22, bus.ReadByte(LargeFrame + offsetInLargePage));
            Assert.NotEqual(0UL, ReadQword(bus, PdeAddress(DataLinear)) & (Accessed | Dirty));
        }
    }

    /// <summary>A missing entry at any of the four levels is #PF with P (bit 0) clear.</summary>
    [Theory]
    [InlineData(4)]
    [InlineData(3)]
    [InlineData(2)]
    [InlineData(1)]
    public void AMissingEntryAtAnyLevelIsANotPresentPageFault(int level)
    {
        var (cpu, bus) = LongModeCpu();
        using (bus)
        {
            MapFourKilobytePage(bus, DataLinear, DataFrame);
            WriteQword(bus, LevelEntryAddress(level), 0);

            var fault = Assert.Throws<PageFaultException>(() => cpu.ReadMemoryByte(SegReg.Ds, DataLinear));

            Assert.Equal(0u, fault.ErrorCode & PageFaultPresent);
            Assert.Equal(DataLinear, fault.LinearAddress);
        }
    }

    /// <summary>
    /// Reserved bits are #PF with P and RSVD (bit 3): a PML4E's bit 7, a
    /// PDPTE's bit 7 (a 1 GB page, not declared), address bits 51:36 above the
    /// 36-bit physical width, and XD (bit 63) while EFER.NXE is clear.
    /// </summary>
    [Theory]
    [InlineData(4, 1UL << 7)]
    [InlineData(3, 1UL << 7)]
    [InlineData(2, 1UL << 40)]
    [InlineData(1, 1UL << 51)]
    [InlineData(1, 1UL << 63)]
    public void AReservedBitIsAReservedBitPageFault(int level, ulong reservedBit)
    {
        var (cpu, bus) = LongModeCpu();
        using (bus)
        {
            MapFourKilobytePage(bus, DataLinear, DataFrame);
            uint address = LevelEntryAddress(level);
            WriteQword(bus, address, ReadQword(bus, address) | reservedBit);

            var fault = Assert.Throws<PageFaultException>(() => cpu.ReadMemoryByte(SegReg.Ds, DataLinear));

            Assert.Equal(PageFaultPresent | PageFaultReserved, fault.ErrorCode);
        }
    }

    /// <summary>Bits 62:52 are ignored by the processor (free for software): no fault.</summary>
    [Fact]
    public void TheIgnoredHighBitsDoNotFault()
    {
        var (cpu, bus) = LongModeCpu();
        using (bus)
        {
            MapFourKilobytePage(bus, DataLinear, DataFrame);
            WriteQword(bus, PteAddress(DataLinear), ReadQword(bus, PteAddress(DataLinear)) | (1UL << 52) | (1UL << 62));
            bus.WriteByte(DataFrame, 0x33);

            Assert.Equal(0x33, cpu.ReadMemoryByte(SegReg.Ds, DataLinear));
        }
    }

    /// <summary>
    /// Rights are the AND of all four levels: R/W clear in the PML4E alone
    /// makes the page read-only, which a supervisor write feels once CR0.WP
    /// (bit 16) is set.
    /// </summary>
    [Fact]
    public void ReadOnlyAtThePml4LevelMakesThePageReadOnly()
    {
        var (cpu, bus) = LongModeCpu();
        using (bus)
        {
            MapFourKilobytePage(bus, DataLinear, DataFrame);
            WriteQword(bus, Pml4Address, PdptAddress | Present | UserSupervisor);
            cpu.State.Cr0 |= CpuState.Cr0WriteProtect;

            var fault = Assert.Throws<PageFaultException>(() => cpu.WriteMemoryByte(SegReg.Ds, DataLinear, 1));

            Assert.Equal(PageFaultPresent | PageFaultWrite, fault.ErrorCode);
        }
    }

    /// <summary>Supervisor-only at the PDPT level hides the page from ring 3.</summary>
    [Fact]
    public void SupervisorOnlyAtThePdptLevelHidesThePageFromRingThree()
    {
        const uint UserBit = 1u << 2;
        var (cpu, bus) = LongModeCpu();
        using (bus)
        {
            MapFourKilobytePage(bus, DataLinear, DataFrame);
            WriteQword(bus, PdptAddress, PdAddress | Present | ReadWrite);
            cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable;   // CPL means something only in protected mode
            cpu.State.Cpl = 3;

            var fault = Assert.Throws<PageFaultException>(() => cpu.ReadMemoryByte(SegReg.Ds, DataLinear));

            Assert.Equal(PageFaultPresent | UserBit, fault.ErrorCode);
        }
    }

    [Fact]
    public void PeekLinearReadsThroughTheFourLevelsWithoutSettingAccessedBits()
    {
        var (cpu, bus) = LongModeCpu();
        using (bus)
        {
            MapFourKilobytePage(bus, DataLinear, DataFrame);
            bus.WriteByte(DataFrame + 7, 0x44);

            Assert.Equal(0x44, cpu.PeekLinear(DataLinear + 7));
            Assert.Equal(0UL, ReadQword(bus, Pml4Address) & Accessed);
        }
    }

    // === execute-disable ========================================================

    /// <summary>
    /// With EFER.NXE (bit 11) set, XD in any level forbids fetching from the
    /// page -- #PF with P and I/D (bit 4) -- while reading it stays allowed.
    /// </summary>
    [Fact]
    public void AnExecuteDisabledPageCanBeReadButNotExecuted()
    {
        var (cpu, bus) = LongModeCpu();
        using (bus)
        {
            MapCodeAndStack(bus);
            cpu.State.Efer |= CpuState.EferNoExecuteEnable;
            WriteQword(bus, PdeAddress(CodeBase), ReadQword(bus, PdeAddress(CodeBase)) | ExecuteDisable);
            bus.WriteByte(CodeBase, 0x90);   // nop

            Assert.Equal(0x90, cpu.ReadMemoryByte(SegReg.Ds, CodeBase));
            Assert.Equal((14, PageFaultPresent | PageFaultInstructionFetch), Fault(cpu));
        }
    }

    /// <summary>Under execute-disable a fetch from a missing page reports I/D too: the bit describes the access, not the cause.</summary>
    [Fact]
    public void AFetchFromAMissingPageReportsTheInstructionFetchBit()
    {
        var (cpu, bus) = LongModeCpu();
        using (bus)
        {
            MapCodeAndStack(bus);
            cpu.State.Efer |= CpuState.EferNoExecuteEnable;
            WriteQword(bus, PteAddress(CodeBase), 0);

            Assert.Equal((14, PageFaultInstructionFetch), Fault(cpu));
        }
    }

    /// <summary>
    /// A dword immediate that runs from an executable page into an XD one is
    /// fetched as code, not read as data: #PF with I/D at the second page.
    /// </summary>
    [Fact]
    public void AnImmediateRunningIntoAnExecuteDisabledPageFaults()
    {
        const uint NextPage = CodeBase + 0x1000;
        var (cpu, bus) = LongModeCpu();
        using (bus)
        {
            MapCodeAndStack(bus);
            WriteQword(bus, PteAddress(NextPage), NextPage | AllRights | ExecuteDisable);
            cpu.State.Efer |= CpuState.EferNoExecuteEnable;

            // mov eax, imm32 with the opcode on the last byte of the code page.
            uint opcodeAddress = NextPage - 1;
            bus.WriteByte(opcodeAddress, 0xB8);
            cpu.State.Eip = opcodeAddress;

            uint faultAddress = 0;
            cpu.ProcessorException += (vector, _, _, _) =>
            {
                if (vector == 14 && faultAddress == 0)
                {
                    faultAddress = cpu.State.Cr2;
                }
            };

            Assert.Equal((14, PageFaultPresent | PageFaultInstructionFetch), Fault(cpu));
            Assert.Equal(NextPage, faultAddress);
        }
    }

    /// <summary>EFER.NXE also brings XD to PAE paging outside long mode: a PTE with bit 63 forbids the fetch.</summary>
    [Fact]
    public void PaePagingHonoursExecuteDisableUnderNxe()
    {
        var (cpu, bus) = LongModeCpu();
        using (bus)
        {
            MapCodeAndStack(bus);
            cpu.State.Efer = CpuState.EferNoExecuteEnable;   // PAE paging, not long mode
            cpu.Pdpte[0] = PdAddress | Present;
            WriteQword(bus, PteAddress(CodeBase), CodeBase | AllRights | ExecuteDisable);

            Assert.Equal((14, PageFaultPresent | PageFaultInstructionFetch), Fault(cpu));
        }
    }

    /// <summary>
    /// Setting EFER.NXE through WRMSR flushes the TLB: a translation cached
    /// while XD was a reserved bit's neighbour is walked again, and the page
    /// that was fetched from a moment ago is now refused.
    /// </summary>
    [Fact]
    public void TurningNxeOnFlushesCachedTranslations()
    {
        var (cpu, bus) = LongModeCpu();
        using (bus)
        {
            MapCodeAndStack(bus);
            bus.WriteByte(CodeBase, 0x90);   // nop
            bus.WriteByte(CodeBase + 1, 0x90);
            cpu.Step();

            WriteQword(bus, PteAddress(CodeBase), CodeBase | AllRights | ExecuteDisable);
            Assert.True(cpu.Msrs.TryWrite(EferIndex, cpu.State.Efer | CpuState.EferNoExecuteEnable));

            Assert.Equal((14, PageFaultPresent | PageFaultInstructionFetch), Fault(cpu));
        }
    }

    // === the review's coverage additions =======================================

    /// <summary>
    /// An immediate whose first byte is still on the executable page and the
    /// rest on an XD one faults at the first byte of the XD page (CR2), as
    /// Bochs and QEMU report it.
    /// </summary>
    [Fact]
    public void AStraddlingImmediateFaultsAtTheFirstByteOfTheExecuteDisabledPage()
    {
        const uint NextPage = CodeBase + 0x1000;
        var (cpu, bus) = LongModeCpu();
        using (bus)
        {
            MapCodeAndStack(bus);
            WriteQword(bus, PteAddress(NextPage), NextPage | AllRights | ExecuteDisable);
            cpu.State.Efer |= CpuState.EferNoExecuteEnable;

            // mov ax, imm16 (real-mode operand size): opcode and the
            // immediate's low byte on the code page, its high byte on the next.
            uint opcodeAddress = NextPage - 2;
            bus.WriteByte(opcodeAddress, 0xB8);
            cpu.State.Eip = opcodeAddress;

            uint faultAddress = 0;
            cpu.ProcessorException += (vector, _, _, _) =>
            {
                if (vector == 14 && faultAddress == 0)
                {
                    faultAddress = cpu.State.Cr2;
                }
            };

            Assert.Equal((14, PageFaultPresent | PageFaultInstructionFetch), Fault(cpu));
            Assert.Equal(NextPage, faultAddress);
        }
    }

    /// <summary>
    /// A code-segment limit violation outranks a code page fault (SDM Vol.3A
    /// Table 6-2, class 7): an immediate running past both the CS limit and
    /// into an XD page is #GP, not #PF.
    /// </summary>
    [Fact]
    public void TheCsLimitIsCheckedBeforeTheNextPageIsWalked()
    {
        const uint NextPage = CodeBase + 0x1000;
        var (cpu, bus) = LongModeCpu();
        using (bus)
        {
            MapCodeAndStack(bus);
            WriteQword(bus, PteAddress(NextPage), NextPage | AllRights | ExecuteDisable);
            cpu.State.Efer |= CpuState.EferNoExecuteEnable;
            uint opcodeAddress = NextPage - 2;
            bus.WriteByte(opcodeAddress, 0xB8);   // mov ax, imm16
            cpu.State.Eip = opcodeAddress;
            cpu.State.Segments[(int)SegReg.Cs].Limit = NextPage - 1;

            Assert.Equal(GeneralProtectionVector, Fault(cpu).Vector);
        }
    }

    /// <summary>XD in the PML4E forbids fetching from everything beneath it.</summary>
    [Fact]
    public void ExecuteDisableInThePml4eForbidsTheFetch()
    {
        var (cpu, bus) = LongModeCpu();
        using (bus)
        {
            MapCodeAndStack(bus);
            cpu.State.Efer |= CpuState.EferNoExecuteEnable;
            WriteQword(bus, Pml4Address, PdptAddress | AllRights | ExecuteDisable);

            Assert.Equal((14, PageFaultPresent | PageFaultInstructionFetch), Fault(cpu));
        }
    }

    /// <summary>Without EFER.NXE, bit 63 of a PAE PTE is reserved: P and RSVD, and no I/D.</summary>
    [Fact]
    public void PaeBit63WithoutNxeIsAReservedBitFault()
    {
        var (cpu, bus) = LongModeCpu();
        using (bus)
        {
            cpu.State.Efer = 0;   // PAE paging, not long mode, no NXE
            cpu.Pdpte[0] = PdAddress | Present;
            MapFourKilobytePage(bus, DataLinear, DataFrame);
            WriteQword(bus, PteAddress(DataLinear), DataFrame | AllRights | ExecuteDisable);

            var fault = Assert.Throws<PageFaultException>(() => cpu.ReadMemoryByte(SegReg.Ds, DataLinear));

            Assert.Equal(PageFaultPresent | PageFaultReserved, fault.ErrorCode);
        }
    }

    /// <summary>
    /// Legacy two-level paging has no execute-disable: with NXE set but
    /// CR4.PAE clear, a fetch fault never reports I/D (SDM Vol.3A §4.7).
    /// </summary>
    [Fact]
    public void LegacyPagingNeverReportsTheInstructionFetchBit()
    {
        const uint DirectoryAddress = 0x30000;
        var (cpu, bus) = LongModeCpu();
        using (bus)
        {
            cpu.State.Efer = CpuState.EferNoExecuteEnable;
            cpu.State.Cr4 &= ~CpuState.Cr4Pae;
            cpu.State.Cr3 = DirectoryAddress;   // an empty directory: the code page is missing
            cpu.FlushTlb();

            Assert.Equal((14, 0u), Fault(cpu));
        }
    }

    private const byte GeneralProtectionVector = 13;

    // === harness ================================================================

    private const uint CodeBase = 0x1000;
    private const uint StackPageLinear = 0x7000;

    /// <summary>A bare family-six CPU already in long mode (see the class summary), with no mapping yet.</summary>
    private static (Cpu cpu, PhysicalMemoryBus bus) LongModeCpu()
    {
        var bus = new PhysicalMemoryBus();
        var cpu = new Cpu(bus);
        foreach (SegReg segment in Enum.GetValues<SegReg>())
        {
            cpu.State.LoadSegmentRealMode(segment, 0);
        }

        cpu.State.Segments[(int)SegReg.Ds].Limit = 0xFFFFFFFF;
        cpu.State.Esp = 0x8000;
        cpu.State.Eip = CodeBase;

        cpu.State.SelectModel(CpuModel.PentiumII);
        cpu.State.Features = cpu.State.Features.With(
            CpuFeature.Pae,
            CpuFeature.Pat,
            CpuFeature.LongMode,
            CpuFeature.Nx);

        cpu.State.Cr3 = Pml4Address;
        cpu.State.Cr4 |= CpuState.Cr4Pae;
        cpu.State.Cr0 |= CpuState.Cr0Paging;
        cpu.State.Efer = CpuState.EferLongModeEnable | CpuState.EferLongModeActive;
        cpu.FlushTlb();

        return (cpu, bus);
    }

    /// <summary>
    /// Maps one 4 KB page with full rights at every level, sharing the one
    /// PML4, PDPT, PD and PT every mapping here uses (all linear addresses in
    /// these tests lie below 1 GB).
    /// </summary>
    private static void MapFourKilobytePage(PhysicalMemoryBus bus, uint linear, uint frame)
    {
        WriteQword(bus, Pml4Address, PdptAddress | AllRights);
        WriteQword(bus, PdptAddress, PdAddress | AllRights);
        WriteQword(bus, PdeAddress(linear), PtAddress | AllRights);
        WriteQword(bus, PteAddress(linear), frame | AllRights);
    }

    /// <summary>Identity-maps the code page, the page after it, and the page real-mode fault delivery pushes onto.</summary>
    private static void MapCodeAndStack(PhysicalMemoryBus bus)
    {
        MapFourKilobytePage(bus, CodeBase, CodeBase);
        MapFourKilobytePage(bus, CodeBase + 0x1000, CodeBase + 0x1000);
        MapFourKilobytePage(bus, StackPageLinear, StackPageLinear);
    }

    private static uint PdeAddress(uint linear) => PdAddress + ((linear >> 21) & 0x1FF) * 8;

    private static uint PteAddress(uint linear) => PtAddress + ((linear >> 12) & 0x1FF) * 8;

    /// <summary>The entry a level (4 = PML4E ... 1 = PTE) uses for <see cref="DataLinear"/>.</summary>
    private static uint LevelEntryAddress(int level) => level switch
    {
        4 => Pml4Address,
        3 => PdptAddress,
        2 => PdeAddress(DataLinear),
        _ => PteAddress(DataLinear),
    };

    private static void WriteQword(PhysicalMemoryBus bus, uint address, ulong value)
    {
        bus.WriteDword(address, (uint)value);
        bus.WriteDword(address + 4, (uint)(value >> 32));
    }

    private static ulong ReadQword(PhysicalMemoryBus bus, uint address) =>
        bus.ReadDword(address) | ((ulong)bus.ReadDword(address + 4) << 32);

    /// <summary>Runs one instruction; the vector and error code of the first exception it raised, or (0xFF, 0).</summary>
    private static (byte Vector, uint ErrorCode) Fault(Cpu cpu)
    {
        byte vector = 0xFF;
        uint errorCode = 0;
        cpu.ProcessorException += (raised, code, _, _) =>
        {
            if (vector == 0xFF)
            {
                vector = raised;
                errorCode = code;
            }
        };

        cpu.Step();
        return (vector, errorCode);
    }
}
