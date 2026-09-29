// Long-mode (IA-32e) paging: the four-level walk AMD introduced with the K8
// in 2003 and Intel adopted with EM64T, used whenever EFER.LMA is set
// (plans/long-mode.md, stage B). CR3 names a page-map level-4 table; each
// of the four levels -- PML4E, PDPTE, PDE, PTE -- is a 64-bit entry
// carrying its own R/W, U/S, A and XD bits, and the effective rights are
// the AND of all of them (execute: none of them has XD). A PDE with PS set
// maps 2 MB; a PDPTE with PS set would map 1 GB, which this processor does
// not declare (no CPUID 80000001h EDX bit 26), so there the bit is
// reserved. Sources: SDM Vol.3A §4.5 and Tables 4-14 to 4-20; AMD APM
// Vol.2 §5.3.
//
// Linear addresses are still 32 bits wide throughout the core: until stage C
// widens address generation, only compatibility-mode code reaches this walk,
// and its linear addresses are zero-extended, hence always canonical and
// always in the first PML4 and PDPT slot ranges. The walk itself indexes
// with the full 48-bit layout, so stage C only has to widen its parameter.

namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>
    /// Bits 51:36 of every long-mode paging entry: address bits beyond this
    /// processor's 36-bit physical width (CPUID 80000008h), reserved. Bits
    /// 62:52 are ignored by the processor (available to software), and bit
    /// 63 is XD or reserved; see <see cref="LongModeReservedMask"/>.
    /// </summary>
    private const ulong LongModeAddressReservedMask = ((1UL << 52) - 1) & ~((1UL << 36) - 1);

    /// <summary>A PML4E's bit 7: reserved, a PML4E never maps a page itself.</summary>
    private const ulong Pml4ePageSizeReserved = 1UL << 7;

    /// <summary>A PDPTE's bit 7 (PS): it would map a 1 GB page, which this processor does not declare, so it is reserved (see the file comment).</summary>
    private const ulong LongModePdptePageSize = 1UL << 7;

    /// <summary>
    /// The reserved bits every long-mode entry shares: the address bits above
    /// the physical width, and XD while EFER.NXE is clear.
    /// </summary>
    private ulong LongModeReservedMask => (State.Efer & CpuState.EferNoExecuteEnable) != 0
        ? LongModeAddressReservedMask
        : LongModeAddressReservedMask | ExecuteDisableBit;

    /// <summary>
    /// Linear -> physical through the four long-mode levels, in place of
    /// <see cref="TranslatePae"/> whenever EFER.LMA is set. Shares the TLB,
    /// the permission check and the lazy dirty-bit update with the other two
    /// walks (D is bit 6 of the low dword in every format).
    ///
    /// Each level is checked as it is reached, in the order the processor
    /// walks: not present, then reserved bits. Rights are decided only once
    /// the leaf is found, from the AND of every level's R/W and U/S and the
    /// OR of their XD bits. The accessed bit is set on each level walked; the
    /// dirty bit on the leaf alone.
    /// </summary>
    private uint TranslateLongMode(uint linear, bool write, bool asSupervisor, bool execute)
    {
        uint page = linear & 0xFFFFF000;
        uint offset = linear & 0xFFF;

        ref TlbEntry entry = ref _tlb[(page >> 12) & (TlbSize - 1)];
        if (entry.Valid && entry.Page == page)
        {
            CheckPagePermissions(linear, write, entry.Writable, entry.User, asSupervisor, execute, entry.Executable);

            if (write && !entry.Dirty)
            {
                MarkDirty(entry.TableEntryAddress);
                entry.Dirty = true;
            }

            return entry.Frame | offset;
        }

        ulong wideLinear = linear;
        ulong reserved = LongModeReservedMask;

        // Level 4: the PML4, at CR3's bits 35:12.
        ulong pml4eAddress = (State.Cr3 & PaeAddressMask4K) + LongModeIndex(wideLinear, 39) * 8;
        ulong pml4e = ReadPagingStructureEntry(pml4eAddress);
        CheckLongModeEntry(pml4e, reserved | Pml4ePageSizeReserved, linear, write, asSupervisor, execute);

        // Level 3: the page-directory-pointer table. PS here would be a 1 GB
        // page, which this processor does not declare: reserved.
        ulong pdpteAddress = (pml4e & PaeAddressMask4K) + LongModeIndex(wideLinear, 30) * 8;
        ulong pdpte = ReadPagingStructureEntry(pdpteAddress);
        CheckLongModeEntry(pdpte, reserved | LongModePdptePageSize, linear, write, asSupervisor, execute);

        // Level 2: the page directory, whose PS bit maps 2 MB.
        ulong pdeAddress = (pdpte & PaeAddressMask4K) + LongModeIndex(wideLinear, 21) * 8;
        ulong pde = ReadPagingStructureEntry(pdeAddress);
        bool largePage = (pde & PaePresent) != 0 && (pde & PaePageSize) != 0;
        ulong pdeReserved = largePage ? reserved | Pae2MbLeafReservedBits : reserved;
        CheckLongModeEntry(pde, pdeReserved, linear, write, asSupervisor, execute);

        bool writable = (pml4e & PaeReadWrite) != 0 && (pdpte & PaeReadWrite) != 0 && (pde & PaeReadWrite) != 0;
        bool user = (pml4e & PaeUserSupervisor) != 0 && (pdpte & PaeUserSupervisor) != 0 && (pde & PaeUserSupervisor) != 0;
        bool executable = IsExecutable(pml4e) && IsExecutable(pdpte) && IsExecutable(pde);

        ulong leaf;
        ulong leafAddress;
        ulong frame;
        uint largePageMask;

        if (largePage)
        {
            leaf = pde;
            leafAddress = pdeAddress;
            frame = (pde & PaeAddressMask2M) | (linear & 0x1FF000UL);
            largePageMask = PaeLargePageMask;
        }
        else
        {
            // Level 1: the page table.
            ulong pteAddress = (pde & PaeAddressMask4K) + LongModeIndex(wideLinear, 12) * 8;
            ulong pte = ReadPagingStructureEntry(pteAddress);
            CheckLongModeEntry(pte, reserved | PteReservedPatBit, linear, write, asSupervisor, execute);

            writable = writable && (pte & PaeReadWrite) != 0;
            user = user && (pte & PaeUserSupervisor) != 0;
            executable = executable && IsExecutable(pte);

            leaf = pte;
            leafAddress = pteAddress;
            frame = pte & PaeAddressMask4K;
            largePageMask = 0;
        }

        CheckPagePermissions(linear, write, writable, user, asSupervisor, execute, executable);

        // Accessed on every level the walk passed through, then accessed and
        // (for a write) dirty on the leaf. Only the low dword of an entry
        // changes, as in TranslatePae. The table addresses are below 4 GB:
        // a higher one read as all ones and has already faulted as reserved.
        SetAccessed((uint)pml4eAddress, pml4e);
        SetAccessed((uint)pdpteAddress, pdpte);
        if (!largePage)
        {
            SetAccessed((uint)pdeAddress, pde);
        }

        ulong updatedLeaf = leaf | PaeAccessed | (write ? PaeDirty : 0);
        if (updatedLeaf != leaf)
        {
            _memory.WriteDword((uint)leafAddress, (uint)updatedLeaf);
        }

        bool openBus = IsOpenBusPhysicalAddress(frame);
        if (openBus)
        {
            AssertOpenBusPhysicalAddressIsSafe();
        }

        entry.Valid = true;
        entry.Page = page;
        entry.Frame = openBus ? OpenBusPhysicalAddress : (uint)frame;
        entry.Writable = writable;
        entry.User = user;
        entry.Dirty = (updatedLeaf & PaeDirty) != 0;
        entry.Executable = executable;
        entry.TableEntryAddress = (uint)leafAddress;
        entry.LargePageMask = largePageMask;
        entry.Global = (State.Cr4 & CpuState.Cr4Pge) != 0 && (leaf & PaeGlobal) != 0;
        if (largePage)
        {
            _largeEntriesCached = true;
        }

        return entry.Frame | offset;
    }

    /// <summary>
    /// Bits 20:13 of a 2 MB PDE, plus bit 12 (PAT) on a processor without
    /// PAT: the same rule as the PAE format's 2 MB page, less that format's
    /// high bits, which <see cref="LongModeReservedMask"/> replaces.
    /// </summary>
    private ulong Pae2MbLeafReservedBits => Pae2MbReservedMask
        | (State.Features.Has(CpuFeature.Pat) ? 0UL : PaeLargePagePatBit);

    /// <summary>Bit 7 of a PTE (PAT) on a processor without PAT: reserved.</summary>
    private ulong PteReservedPatBit => State.Features.Has(CpuFeature.Pat) ? 0UL : PaePatBit;

    /// <summary>The nine-bit table index a level takes from the linear address, starting at <paramref name="shift"/>.</summary>
    private static ulong LongModeIndex(ulong linear, int shift)
    {
        const ulong IndexMask = 0x1FF;
        return (linear >> shift) & IndexMask;
    }

    /// <summary>
    /// The per-level checks of the walk: a missing entry is #PF with P=0, a
    /// present one with a reserved bit set #PF with P=1 and RSVD.
    /// </summary>
    private void CheckLongModeEntry(ulong entryValue, ulong reservedMask, uint linear, bool write,
                                    bool asSupervisor, bool execute)
    {
        if ((entryValue & PaePresent) == 0)
        {
            throw WalkFault(linear, write, present: false, asSupervisor, execute);
        }

        if ((entryValue & reservedMask) != 0)
        {
            throw WalkFault(linear, write, present: true, asSupervisor, execute, reserved: true);
        }
    }

    /// <summary>Sets a non-leaf entry's accessed bit (bit 5) if the walk found it clear.</summary>
    private void SetAccessed(uint entryAddress, ulong entryValue)
    {
        if ((entryValue & PaeAccessed) == 0)
        {
            _memory.WriteDword(entryAddress, (uint)(entryValue | PaeAccessed));
        }
    }

    /// <summary>
    /// PeekLinear's long-mode walk: the four levels by hand, never faulting
    /// and never setting an accessed bit; a missing or malformed entry, or a
    /// physical address beyond the 32-bit bus, reads 0xFF.
    /// </summary>
    private byte PeekLinearLongMode(uint linear)
    {
        ulong reserved = LongModeReservedMask;

        ulong pml4e = ReadPagingStructureEntry((State.Cr3 & PaeAddressMask4K) + LongModeIndex(linear, 39) * 8);
        if ((pml4e & PaePresent) == 0 || (pml4e & (reserved | Pml4ePageSizeReserved)) != 0)
        {
            return 0xFF;
        }

        ulong pdpte = ReadPagingStructureEntry((pml4e & PaeAddressMask4K) + LongModeIndex(linear, 30) * 8);
        if ((pdpte & PaePresent) == 0 || (pdpte & (reserved | LongModePdptePageSize)) != 0)
        {
            return 0xFF;
        }

        ulong pde = ReadPagingStructureEntry((pdpte & PaeAddressMask4K) + LongModeIndex(linear, 21) * 8);
        if ((pde & PaePresent) == 0)
        {
            return 0xFF;
        }

        ulong physical;
        if ((pde & PaePageSize) != 0)
        {
            if ((pde & (reserved | Pae2MbLeafReservedBits)) != 0)
            {
                return 0xFF;
            }

            physical = (pde & PaeAddressMask2M) | (linear & 0x1FFFFFu);
        }
        else
        {
            if ((pde & reserved) != 0)
            {
                return 0xFF;
            }

            ulong pte = ReadPagingStructureEntry((pde & PaeAddressMask4K) + LongModeIndex(linear, 12) * 8);
            if ((pte & PaePresent) == 0 || (pte & (reserved | PteReservedPatBit)) != 0)
            {
                return 0xFF;
            }

            physical = (pte & PaeAddressMask4K) | (linear & 0xFFFu);
        }

        return IsOpenBusPhysicalAddress(physical) ? (byte)0xFF : _memory.ReadByte((uint)physical);
    }
}
