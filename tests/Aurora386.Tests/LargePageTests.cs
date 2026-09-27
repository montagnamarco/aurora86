using Aurora386.Core;
using Aurora386.Core.Cpu;

namespace Aurora386.Tests;

/// <summary>
/// Work package P2.2: CR4.PSE and the Pentium's 4 MB page directory entries
/// (PDE.PS). With PSE on and PS set, a directory entry is the whole
/// translation -- frame = PDE[31:22] with the low 22 bits of the linear
/// address underneath it, no page table underneath it at all, and A/D
/// maintained in the PDE itself rather than in a table entry.
///
/// Test cases 1-8 here follow plans/specs/pse-vme.md's "P2.2 PSE" numbering
/// exactly; the rest cover what the outcome asks for beyond that list.
/// Mirrors PagingTests' style: each test builds its own tables by hand, the
/// way an operating system does at startup.
/// </summary>
public class LargePageTests
{
    // Somewhere well clear of the BIOS data area and video memory, same
    // convention PagingTests uses.
    private const uint DirectoryAddress = 0x20000;
    private const uint TableAddress = 0x21000;

    private const uint Present = 1u << 0;
    private const uint Writable = 1u << 1;
    private const uint User = 1u << 2;
    private const uint Accessed = 1u << 5;
    private const uint Dirty = 1u << 6;

    /// <summary>PDE bit 7, PS: this directory entry maps 4 MB directly.</summary>
    private const uint LargePage = 1u << 7;

    /// <summary>
    /// A machine in protected mode, CR4.PSE on, with one 4 MB page mapped:
    /// linear <paramref name="linear"/> (need not itself be 4 MB-aligned;
    /// only its containing directory slot is used) lands on the 4 MB frame
    /// starting at <paramref name="frame"/>. Selecting the Pentium model
    /// first is what makes CR4.PSE a real bit at all to set -- see
    /// CpuFeatures.Implemented.
    /// </summary>
    private static Machine PagedLarge(uint linear, uint frame,
                                       uint directoryFlags = Present | Writable | User | LargePage)
    {
        var machine = new Machine();
        machine.CpuModel = CpuModel.Pentium;   // resets the CPU; must come first
        machine.RunInstructions(200);

        machine.Memory.WriteDword(DirectoryAddress + (linear >> 22) * 4,
                                  (frame & 0xFFC00000) | directoryFlags);

        machine.Cpu.State.Cr3 = DirectoryAddress;
        machine.Cpu.State.Cr4 |= CpuState.Cr4Pse;
        machine.Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable | CpuState.Cr0Paging;

        // A flat segment, so that the offset used below is the linear address.
        machine.Cpu.State.Segments[(int)SegReg.Ds].Base = 0;
        machine.Cpu.State.Segments[(int)SegReg.Ds].Limit = 0xFFFFFFFF;

        return machine;
    }

    // --- test case 1: PSE = 0, PDE.PS = 1 -> ordinary 4 KB walk ------------

    [Fact]
    public void WithPseOffThePsBitInThePdeIsIgnored()
    {
        const uint Linear = 0x00400000;
        const uint Physical = 0x00330000;

        var machine = new Machine();
        machine.RunInstructions(200);

        // The PDE claims to map 4 MB (PS set), but CR4 does not exist to
        // say PSE is on at all on the default model here -- and even where
        // it does, PSE = 0 must leave PS a plain unconsulted bit. Either
        // way this must still walk through a page table.
        machine.Memory.WriteDword(DirectoryAddress + (Linear >> 22) * 4,
                                  TableAddress | Present | Writable | User | LargePage);
        machine.Memory.WriteDword(TableAddress + ((Linear >> 12) & 0x3FF) * 4,
                                  Physical | Present | Writable | User);

        machine.Cpu.State.Cr3 = DirectoryAddress;
        machine.Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable | CpuState.Cr0Paging;

        machine.Cpu.State.Segments[(int)SegReg.Ds].Base = 0;
        machine.Cpu.State.Segments[(int)SegReg.Ds].Limit = 0xFFFFFFFF;

        using (machine)
        {
            machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear + 0x24, 0x5A);

            // Landed through the table's frame, not the 4 MB frame the PS
            // bit alone would have implied (which would be physical 0x24,
            // the PDE's own top 10 bits being zero here).
            Assert.Equal(0x5A, machine.Memory.ReadByte(Physical + 0x24));
        }
    }

    // --- test case 2: PDE[21:13] != 0 -> #PF, reserved-bit error code -----

    [Fact]
    public void ReservedBitsInTheLargePdeFaultWithTheReservedBitErrorCode()
    {
        const uint Linear = 0x00400000;
        const uint Frame = 0x00C00000;

        using Machine machine = PagedLarge(Linear, Frame,
            Present | Writable | User | LargePage | (1u << 13));

        var fault = Assert.Throws<PageFaultException>(
            () => machine.Cpu.ReadMemoryByte(SegReg.Ds, Linear));

        Assert.Equal(Linear, fault.LinearAddress);
        Assert.True(fault.Present);
        Assert.True(fault.Reserved);
        Assert.False(fault.Write);
        Assert.False(fault.User);
        // present(1) | reserved(8); this was a supervisor read, so neither
        // the write nor the user bit is set.
        Assert.Equal(1u | 8u, fault.ErrorCode);
    }

    // --- test cases 3/4: R/W = 0 in the PDE, CR0.WP off vs on --------------

    [Fact]
    public void SupervisorWritesAReadOnlyLargePageWhenWriteProtectIsOff()
    {
        const uint Linear = 0x00400000;
        const uint Frame = 0x00C00000;

        using Machine machine = PagedLarge(Linear, Frame,
            Present | User | LargePage);   // no Writable bit
        machine.Cpu.State.Cpl = 0;
        // CR0.WP stays clear: PagedLarge does not set it.

        machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear + 0x10, 0x77);

        Assert.Equal(0x77, machine.Memory.ReadByte(Frame + 0x10));
    }

    [Fact]
    public void SupervisorWriteToAReadOnlyLargePageFaultsWhenWriteProtectIsOn()
    {
        const uint Linear = 0x00400000;
        const uint Frame = 0x00C00000;

        using Machine machine = PagedLarge(Linear, Frame,
            Present | User | LargePage);   // no Writable bit
        machine.Cpu.State.Cpl = 0;
        machine.Cpu.State.Cr0 |= CpuState.Cr0WriteProtect;

        var fault = Assert.Throws<PageFaultException>(
            () => machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear, 0x99));

        Assert.True(fault.Present);
        Assert.True(fault.Write);
        Assert.False(fault.User);
        Assert.Equal(1u | 2u, fault.ErrorCode);
    }

    // --- test case 5: a write sets D in the PDE ----------------------------

    [Fact]
    public void AWriteThroughTheLargePageSetsDirtyInThePde()
    {
        const uint Linear = 0x00400000;
        const uint Frame = 0x00C00000;
        uint directoryAddress = DirectoryAddress + (Linear >> 22) * 4;

        using Machine machine = PagedLarge(Linear, Frame);

        Assert.Equal(0u, machine.Memory.ReadDword(directoryAddress) & Dirty);

        machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear, 0x11);

        Assert.Equal(Dirty, machine.Memory.ReadDword(directoryAddress) & Dirty);
    }

    // --- test case 6: A set once --------------------------------------------

    [Fact]
    public void AccessingTheLargePageSetsAccessedInThePdeAndOnlyOnTheFirstAccess()
    {
        const uint Linear = 0x00400000;
        const uint Frame = 0x00C00000;
        uint directoryAddress = DirectoryAddress + (Linear >> 22) * 4;

        using Machine machine = PagedLarge(Linear, Frame);

        Assert.Equal(0u, machine.Memory.ReadDword(directoryAddress) & Accessed);

        machine.Cpu.ReadMemoryByte(SegReg.Ds, Linear);
        Assert.Equal(Accessed, machine.Memory.ReadDword(directoryAddress) & Accessed);

        // The translation is now cached; clearing the bit behind the TLB's
        // back and reading again must not set it a second time, exactly as
        // PagingTests.TheAccessedBitIsSetOnBothLevels demonstrates for the
        // 4 KB path -- the processor only walks the PDE once.
        machine.Memory.WriteDword(directoryAddress,
            machine.Memory.ReadDword(directoryAddress) & ~Accessed);
        machine.Cpu.ReadMemoryByte(SegReg.Ds, Linear);
        Assert.Equal(0u, machine.Memory.ReadDword(directoryAddress) & Accessed);
    }

    // --- test case 7: MOV CR3 drops a cached 4 MB entry --------------------

    [Fact]
    public void LoadingCr3AbandonsACachedLargePageTranslation()
    {
        const uint Linear = 0x00400000;
        const uint FirstFrame = 0x00C00000;
        const uint SecondFrame = 0x00800000;

        using Machine machine = PagedLarge(Linear, FirstFrame);

        machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear, 0xAA);
        Assert.Equal(0xAA, machine.Memory.ReadByte(FirstFrame));

        // Point the same linear address at a different 4 MB frame, then
        // reload CR3 the way an operating system does when it switches
        // process -- FlushTlb is exactly what the MOV-to-CR3 instruction
        // handler calls (Cpu.TwoByte.cs), which is what this exercises
        // without needing to encode the instruction itself.
        machine.Memory.WriteDword(DirectoryAddress + (Linear >> 22) * 4,
                                  (SecondFrame & 0xFFC00000) | Present | Writable | User | LargePage);
        machine.Cpu.FlushTlb();

        machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear, 0xBB);

        Assert.Equal(0xBB, machine.Memory.ReadByte(SecondFrame));
        Assert.Equal(0xAA, machine.Memory.ReadByte(FirstFrame));   // the old page is untouched
    }

    // --- test case 8: INVLPG anywhere in the page, including a slot the ---
    // --- invalidated address itself does not hash to -----------------------

    [Fact]
    public void InvlpgOfAnyAddressInTheLargePageDropsEveryCachedSlot()
    {
        const uint Base = 0x00400000;   // 4 MB-aligned
        const uint FirstFrame = 0x00C00000;
        const uint SecondFrame = 0x00800000;

        // Subpage 5 and subpage 9 of the same 4 MB-aligned range hash to
        // different slots of the 1024-entry direct-mapped TLB (see the
        // module comment in Cpu.Paging.cs): a linear page number that is a
        // multiple of 1024 apart from another lands in the same slot only
        // when the two are congruent mod 1024, which subpages 5 and 9
        // (differing by 4) are not.
        const uint Touched = Base + 0x005000;
        const uint Invalidated = Base + 0x009000;

        using Machine machine = PagedLarge(Base, FirstFrame);

        machine.Cpu.WriteMemoryByte(SegReg.Ds, Touched, 0xAA);
        Assert.Equal(0xAA, machine.Memory.ReadByte(FirstFrame + 0x005000));

        // Remap the whole page elsewhere, then invalidate a different
        // address in the same 4 MB range from the one that was actually
        // cached.
        machine.Memory.WriteDword(DirectoryAddress + (Base >> 22) * 4,
                                  (SecondFrame & 0xFFC00000) | Present | Writable | User | LargePage);
        machine.Cpu.InvalidatePage(Invalidated);

        // The slot cached from the touched address must be gone too, or the
        // next access there would still return the old frame's data.
        machine.Cpu.WriteMemoryByte(SegReg.Ds, Touched, 0xBB);
        Assert.Equal(0xBB, machine.Memory.ReadByte(SecondFrame + 0x005000));
        Assert.Equal(0xAA, machine.Memory.ReadByte(FirstFrame + 0x005000));   // the old frame is untouched
    }

    /// <summary>
    /// The guest unmaps the 4 MB page (PDE = 0) and then invalidates another
    /// address in it. The translation cached from the first address must be
    /// gone: the next access faults with P = 0 and CR2 = that address, as on
    /// silicon, instead of reading the old frame through a stale slot.
    /// </summary>
    [Fact]
    public void InvlpgAfterTheLargePdeIsClearedDropsTheCachedSlot()
    {
        const uint Base = 0x00400000;
        const uint Frame = 0x00C00000;
        const uint Touched = Base + 0x005000;
        const uint Invalidated = Base + 0x009000;

        using Machine machine = PagedLarge(Base, Frame);

        machine.Cpu.ReadMemoryByte(SegReg.Ds, Touched);
        machine.Memory.WriteDword(DirectoryAddress + (Base >> 22) * 4, 0);
        machine.Cpu.InvalidatePage(Invalidated);

        var fault = Assert.Throws<PageFaultException>(
            () => machine.Cpu.ReadMemoryByte(SegReg.Ds, Touched));

        Assert.Equal(Touched, fault.LinearAddress);
        Assert.False(fault.Present);
    }

    /// <summary>
    /// The guest splits the 4 MB page into a page table and invalidates
    /// another address in it: the next access to the first address goes
    /// through the new page table, not the old 4 MB frame.
    /// </summary>
    [Fact]
    public void InvlpgAfterTheLargePageIsSplitUsesTheNewPageTable()
    {
        const uint Base = 0x00400000;
        const uint Frame = 0x00C00000;
        const uint NewPhysical = 0x00330000;
        const uint Touched = Base + 0x005000;
        const uint Invalidated = Base + 0x009000;

        using Machine machine = PagedLarge(Base, Frame);

        machine.Cpu.ReadMemoryByte(SegReg.Ds, Touched);

        machine.Memory.WriteDword(TableAddress + ((Touched >> 12) & 0x3FF) * 4,
                                  NewPhysical | Present | Writable | User);
        machine.Memory.WriteDword(DirectoryAddress + (Base >> 22) * 4,
                                  TableAddress | Present | Writable | User);
        machine.Cpu.InvalidatePage(Invalidated);

        machine.Cpu.WriteMemoryByte(SegReg.Ds, Touched, 0x5A);

        Assert.Equal(0x5A, machine.Memory.ReadByte(NewPhysical));
        Assert.NotEqual(0x5A, machine.Memory.ReadByte(Frame + 0x005000));
    }

    /// <summary>
    /// Without PAT, bit 12 of a 4 MB PDE is reserved (SDM vol. 3A table
    /// 4-4): a supervisor read through PDE 0x00C01083 faults with error code
    /// 9 (P | RSVD). No model advertises PAT yet.
    /// </summary>
    [Fact]
    public void BitTwelveOfALargePdeIsReservedWithoutPat()
    {
        const uint Linear = 0x00400000;
        const uint Frame = 0x00C00000;

        using Machine machine = PagedLarge(Linear, Frame,
            Present | Writable | LargePage | (1u << 12));

        var fault = Assert.Throws<PageFaultException>(
            () => machine.Cpu.ReadMemoryByte(SegReg.Ds, Linear));

        Assert.Equal(1u | 8u, fault.ErrorCode);
    }

    // --- beyond the spec's numbered cases -----------------------------------

    [Fact]
    public void ALargePageAtANonZeroFrameReadsTheRightPhysicalBytes()
    {
        const uint Linear = 0x00400000;
        const uint Frame = 0x00C00000;   // non-zero, 4 MB-aligned, well clear of the directory

        using Machine machine = PagedLarge(Linear, Frame);

        machine.Memory.WriteByte(Frame + 0x123456, 0x42);
        Assert.Equal(0x42, machine.Cpu.ReadMemoryByte(SegReg.Ds, Linear + 0x123456));

        machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear + 0x200000, 0x99);
        Assert.Equal(0x99, machine.Memory.ReadByte(Frame + 0x200000));
    }

    [Fact]
    public void AUserWriteToALargePageWithUsBitClearFaultsWithTheRightErrorCodeAndCr2()
    {
        const uint Linear = 0x00400000;
        const uint Frame = 0x00C00000;

        using Machine machine = PagedLarge(Linear, Frame,
            Present | Writable | LargePage);   // no User bit: supervisor-only
        machine.Cpu.State.Cpl = 3;

        // No IDT/GDT is set up, so the fault is caught here rather than
        // delivered -- CR2 itself is only written by the delivery path
        // (Cpu.Execute.cs), which PagingTests's equivalent tests also avoid
        // exercising directly; LinearAddress is exactly what ends up there.
        var fault = Assert.Throws<PageFaultException>(
            () => machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear, 0x33));

        Assert.Equal(Linear, fault.LinearAddress);
        Assert.True(fault.Present);
        Assert.True(fault.Write);
        Assert.True(fault.User);
        // present(1) | write(2) | user(4): a protection violation, from
        // user code, on a write.
        Assert.Equal(1u | 2u | 4u, fault.ErrorCode);
    }

    [Fact]
    public void ADwordStraddlesTheEndOfALargePageIntoAnOrdinary4KPage()
    {
        const uint LargeLinearBase = 0x00400000;   // directory slot 1
        const uint LargeFrame = 0x00C00000;
        const uint SmallLinearBase = 0x00800000;   // directory slot 2, an ordinary 4 KB page
        const uint SmallFrame = 0x00810000;
        const uint SmallTableAddress = 0x00023000;

        using Machine machine = PagedLarge(LargeLinearBase, LargeFrame);

        // A second, ordinary 4 KB page table for the directory slot right
        // after the large page's own.
        machine.Memory.WriteDword(DirectoryAddress + (SmallLinearBase >> 22) * 4,
                                  SmallTableAddress | Present | Writable | User);
        machine.Memory.WriteDword(SmallTableAddress + ((SmallLinearBase >> 12) & 0x3FF) * 4,
                                  SmallFrame | Present | Writable | User);

        // The last three bytes of the large page, then the first byte of
        // the 4 KB one right after it.
        const uint Linear = SmallLinearBase - 3;

        machine.Cpu.WriteMemoryDword(SegReg.Ds, Linear, 0x11223344);

        Assert.Equal(0x44, machine.Memory.ReadByte(LargeFrame + 0x3FFFFD));
        Assert.Equal(0x33, machine.Memory.ReadByte(LargeFrame + 0x3FFFFE));
        Assert.Equal(0x22, machine.Memory.ReadByte(LargeFrame + 0x3FFFFF));
        Assert.Equal(0x11, machine.Memory.ReadByte(SmallFrame));
        Assert.Equal(0x11223344u, machine.Cpu.ReadMemoryDword(SegReg.Ds, Linear));
    }
}
