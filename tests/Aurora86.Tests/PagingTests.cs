using Aurora86.Core;
using Aurora86.Core.Cpu;

namespace Aurora86.Tests;

/// <summary>
/// Paging turns the linear addresses a program uses into the physical ones the
/// memory actually has, through two levels of tables. It is what lets an
/// operating system give every process the same addresses without them
/// colliding, and what EMM386 and Windows 3.1 use to conjure memory into the
/// gaps below one megabyte.
///
/// Each test builds its own tables by hand, which is exactly what an operating
/// system does at startup.
/// </summary>
public class PagingTests
{
    // Somewhere well clear of the BIOS data area and video memory.
    private const uint DirectoryAddress = 0x20000;
    private const uint TableAddress = 0x21000;

    private const uint Present = 1u << 0;
    private const uint Writable = 1u << 1;
    private const uint User = 1u << 2;
    private const uint Accessed = 1u << 5;
    private const uint Dirty = 1u << 6;

    /// <summary>
    /// A machine in protected mode with paging on and one page mapped.
    /// Linear <paramref name="linear"/> lands on physical
    /// <paramref name="physical"/>; everything else is unmapped.
    /// </summary>
    private static Machine Paged(uint linear, uint physical,
                                 uint directoryFlags = Present | Writable | User,
                                 uint tableFlags = Present | Writable | User)
    {
        var machine = new Machine();
        machine.RunInstructions(200);

        machine.Memory.WriteDword(DirectoryAddress + (linear >> 22) * 4,
                                  TableAddress | directoryFlags);
        machine.Memory.WriteDword(TableAddress + ((linear >> 12) & 0x3FF) * 4,
                                  (physical & 0xFFFFF000) | tableFlags);

        machine.Cpu.State.Cr3 = DirectoryAddress;
        machine.Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable | CpuState.Cr0Paging;

        // A flat segment, so that the offset used below is the linear address.
        machine.Cpu.State.Segments[(int)SegReg.Ds].Base = 0;
        machine.Cpu.State.Segments[(int)SegReg.Ds].Limit = 0xFFFFFFFF;

        return machine;
    }

    [Fact]
    public void ALinearAddressLandsOnThePageItIsMappedTo()
    {
        const uint Linear = 0x00400000;
        const uint Physical = 0x00330000;

        using Machine machine = Paged(Linear, Physical);

        // Write through the mapping, then look at the physical memory
        // underneath: the byte must be where the tables said it would go.
        machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear + 0x24, 0x5A);

        Assert.Equal(0x5A, machine.Memory.ReadByte(Physical + 0x24));
    }

    [Fact]
    public void TheOffsetWithinThePageIsPreserved()
    {
        const uint Linear = 0x00800000;
        const uint Physical = 0x00340000;

        using Machine machine = Paged(Linear, Physical);

        // The low twelve bits never go through translation, so every byte of
        // the page has to keep its position within it.
        for (uint offset = 0; offset < 0x1000; offset += 0x137)
            machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear + offset, (byte)(offset & 0xFF));

        for (uint offset = 0; offset < 0x1000; offset += 0x137)
            Assert.Equal((byte)(offset & 0xFF), machine.Memory.ReadByte(Physical + offset));
    }

    [Fact]
    public void AWordAcrossAPageBoundaryLandsInBothFrames()
    {
        // Two consecutive linear pages need not be consecutive physically.
        // KRNL386's fixup loop writes selectors wherever the linker put them,
        // and one of GDI's landed on the last byte of a page.
        const uint Linear = 0x00400000;
        using Machine machine = Paged(Linear, 0x00330000);
        machine.Memory.WriteDword(TableAddress + (((Linear + 0x1000) >> 12) & 0x3FF) * 4,
                                  0x00350000 | Present | Writable | User);

        machine.Cpu.WriteMemoryWord(SegReg.Ds, Linear + 0xFFF, 0x050F);
        Assert.Equal(0x0F, machine.Memory.ReadByte(0x00330FFF));
        Assert.Equal(0x05, machine.Memory.ReadByte(0x00350000));

        machine.Memory.WriteByte(0x00330FFF, 0x58);
        machine.Memory.WriteByte(0x00350000, 0x34);
        Assert.Equal((ushort)0x3458, machine.Cpu.ReadMemoryWord(SegReg.Ds, Linear + 0xFFF));
    }

    [Fact]
    public void ADwordAcrossAPageBoundaryLandsInBothFrames()
    {
        const uint Linear = 0x00400000;
        using Machine machine = Paged(Linear, 0x00330000);
        machine.Memory.WriteDword(TableAddress + (((Linear + 0x1000) >> 12) & 0x3FF) * 4,
                                  0x00350000 | Present | Writable | User);

        machine.Cpu.WriteMemoryDword(SegReg.Ds, Linear + 0xFFD, 0x11223344);
        Assert.Equal(0x44, machine.Memory.ReadByte(0x00330FFD));
        Assert.Equal(0x33, machine.Memory.ReadByte(0x00330FFE));
        Assert.Equal(0x22, machine.Memory.ReadByte(0x00330FFF));
        Assert.Equal(0x11, machine.Memory.ReadByte(0x00350000));
        Assert.Equal(0x11223344u, machine.Cpu.ReadMemoryDword(SegReg.Ds, Linear + 0xFFD));
    }

    [Fact]
    public void AWriteAcrossAMissingSecondPageFaultsWithoutTouchingTheFirst()
    {
        const uint Linear = 0x00400000;
        using Machine machine = Paged(Linear, 0x00330000);
        machine.Memory.WriteByte(0x00330FFF, 0xAA);

        var fault = Assert.Throws<PageFaultException>(
            () => machine.Cpu.WriteMemoryWord(SegReg.Ds, Linear + 0xFFF, 0x050F));
        Assert.Equal(Linear + 0x1000, fault.LinearAddress);
        Assert.Equal(0xAA, machine.Memory.ReadByte(0x00330FFF));
    }

    [Fact]
    public void AMissingPageRaisesAFaultAndReportsTheAddress()
    {
        using Machine machine = Paged(0x00400000, 0x00330000);

        // An address in a different directory slot, which was never mapped.
        const uint Unmapped = 0x00C00000;

        var fault = Assert.Throws<PageFaultException>(
            () => machine.Cpu.ReadMemoryByte(SegReg.Ds, Unmapped));

        Assert.Equal(Unmapped, fault.LinearAddress);

        // Bit 0 of the error code clear means "not present", which is how the
        // handler tells a missing page from a protection violation.
        Assert.False(fault.Present);
        Assert.Equal(0u, fault.ErrorCode & 1);
    }

    [Fact]
    public void TheAccessedBitIsSetOnBothLevels()
    {
        // The processor walked both tables, so both entries have to record
        // that they were used: an operating system reads these to decide
        // which pages are worth keeping.
        const uint Linear = 0x00400000;

        using Machine machine = Paged(Linear, 0x00330000);

        machine.Cpu.ReadMemoryByte(SegReg.Ds, Linear);

        uint directoryEntry = machine.Memory.ReadDword(DirectoryAddress + (Linear >> 22) * 4);
        uint tableEntry = machine.Memory.ReadDword(TableAddress + ((Linear >> 12) & 0x3FF) * 4);

        Assert.Equal(Accessed, directoryEntry & Accessed);
        Assert.Equal(Accessed, tableEntry & Accessed);
    }

    [Fact]
    public void TheDirtyBitIsSetOnlyByAWrite()
    {
        const uint Linear = 0x00400000;

        using Machine machine = Paged(Linear, 0x00330000);
        uint entryAddress = TableAddress + ((Linear >> 12) & 0x3FF) * 4;

        machine.Cpu.ReadMemoryByte(SegReg.Ds, Linear);
        Assert.Equal(0u, machine.Memory.ReadDword(entryAddress) & Dirty);

        machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear, 0x11);
        Assert.Equal(Dirty, machine.Memory.ReadDword(entryAddress) & Dirty);
    }

    [Fact]
    public void AWriteToAPageAlreadyInTheCacheStillMarksItDirty()
    {
        // The cached translation says nothing about whether the page has been
        // written yet, so a read followed by a write must not let the dirty
        // bit slip through: the operating system would then skip writing that
        // page to swap and quietly lose the data.
        const uint Linear = 0x00400000;

        using Machine machine = Paged(Linear, 0x00330000);
        uint entryAddress = TableAddress + ((Linear >> 12) & 0x3FF) * 4;

        // First access caches the translation.
        machine.Cpu.ReadMemoryByte(SegReg.Ds, Linear);
        Assert.Equal(0u, machine.Memory.ReadDword(entryAddress) & Dirty);

        // Second access hits the cache and writes.
        machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear, 0x22);
        Assert.Equal(Dirty, machine.Memory.ReadDword(entryAddress) & Dirty);
    }

    [Fact]
    public void LoadingCr3AbandonsTheCachedTranslations()
    {
        // A new page directory describes a different address space. Anything
        // remembered from the old one would send the program to another
        // process's memory.
        const uint Linear = 0x00400000;
        const uint First = 0x00330000;
        const uint Second = 0x00350000;

        using Machine machine = Paged(Linear, First);

        machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear, 0xAA);
        Assert.Equal(0xAA, machine.Memory.ReadByte(First));

        // Point the same linear address somewhere else, then reload CR3 the
        // way an operating system does when it switches process.
        machine.Memory.WriteDword(TableAddress + ((Linear >> 12) & 0x3FF) * 4,
                                  Second | Present | Writable | User);
        machine.Cpu.FlushTlb();

        machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear, 0xBB);

        Assert.Equal(0xBB, machine.Memory.ReadByte(Second));
        Assert.Equal(0xAA, machine.Memory.ReadByte(First));   // the old page is untouched
    }

    [Fact]
    public void UserCodeCannotReachASupervisorPage()
    {
        // This is the whole basis of protection: ring 3 must not be able to
        // read the kernel, however it phrases the address.
        const uint Linear = 0x00400000;

        using Machine machine = Paged(Linear, 0x00330000,
                                      tableFlags: Present | Writable);   // no User bit
        machine.Cpu.State.Cpl = 3;

        var fault = Assert.Throws<PageFaultException>(
            () => machine.Cpu.ReadMemoryByte(SegReg.Ds, Linear));

        // Present, and from user code: a protection violation rather than a
        // missing page, which is what tells the handler not to go looking in
        // swap for it.
        Assert.True(fault.Present);
        Assert.True(fault.User);
        Assert.Equal(1u | 4u, fault.ErrorCode);
    }

    [Fact]
    public void UserCodeCannotWriteAReadOnlyPage()
    {
        const uint Linear = 0x00400000;

        using Machine machine = Paged(Linear, 0x00330000,
                                      tableFlags: Present | User);   // no Writable bit
        machine.Cpu.State.Cpl = 3;

        // Reading is allowed.
        machine.Cpu.ReadMemoryByte(SegReg.Ds, Linear);

        var fault = Assert.Throws<PageFaultException>(
            () => machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear, 0x33));

        Assert.True(fault.Write);
        Assert.Equal(1u | 2u | 4u, fault.ErrorCode);
    }

    [Fact]
    public void APageIsOnlyAsPermissiveAsTheDirectoryAboveIt()
    {
        // The two levels are combined with an and, not an or. A page marked
        // user-accessible inside a supervisor directory stays out of reach,
        // which is how a kernel locks away a whole four-megabyte region with
        // one bit instead of rewriting a thousand page entries.
        const uint Linear = 0x00400000;

        using Machine machine = Paged(Linear, 0x00330000,
                                      directoryFlags: Present | Writable,       // no User
                                      tableFlags: Present | Writable | User);   // User
        machine.Cpu.State.Cpl = 3;

        Assert.Throws<PageFaultException>(
            () => machine.Cpu.ReadMemoryByte(SegReg.Ds, Linear));
    }

    [Fact]
    public void SupervisorCodeReachesEverything()
    {
        // Ring 0 is not subject to the user bit, and on a 386 it may write a
        // read-only page as well: the CR0 bit that changes this arrived with
        // the 486, and software written for the 386 relies on being able to.
        const uint Linear = 0x00400000;
        const uint Physical = 0x00330000;

        using Machine machine = Paged(Linear, Physical, tableFlags: Present);   // neither bit
        machine.Cpu.State.Cpl = 0;

        machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear, 0x44);

        Assert.Equal(0x44, machine.Memory.ReadByte(Physical));
    }
}
