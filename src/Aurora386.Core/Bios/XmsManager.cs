using Aurora386.Core.Memory;
using CpuNs = Aurora386.Core.Cpu;

namespace Aurora386.Core.Bios;

/// <summary>
/// Extended memory manager following the XMS specification -- the service that
/// HIMEM.SYS provides on a real PC.
///
/// It covers two things DOS 6.22 makes use of:
///  - the HMA, the 64 KB just above the one megabyte mark, where DOS relocates
///    itself when CONFIG.SYS says DOS=HIGH, freeing some forty KB of
///    conventional memory;
///  - extended memory blocks (EMBs), which programs allocate for their data.
///
/// Programs do not reach these functions through an INT: they ask INT 2Fh for
/// the address of an entry point and then far-call it directly. Here that entry
/// point is a native hook placed in the ROM.
/// </summary>
public sealed class XmsManager
{
    private readonly PhysicalMemoryBus _memory;

    /// <summary>First address of the HMA: the 64 KB just above one megabyte.</summary>
    private const uint HmaBase = 0x100000;

    /// <summary>
    /// Usable size of the HMA. It is not a full 64 KB: with a segment of
    /// 0xFFFF the largest reachable offset is 0xFFFF, so the last usable
    /// address is 0x10FFEF -- sixteen bytes short.
    /// </summary>
    private const uint HmaSize = 0xFFF0;

    /// <summary>First address of the memory handed out as extended memory.</summary>
    private const uint ExtendedMemoryBase = 0x110000;   // immediately above the HMA

    /// <summary>An extended memory block allocated by a program.</summary>
    private sealed class Block
    {
        public uint Base;
        public uint SizeKb;
        public int LockCount;
        public bool InUse;
    }

    private readonly Dictionary<ushort, Block> _blocks = [];
    private ushort _nextHandle = 1;

    /// <summary>Next free address in extended memory.</summary>
    private uint _allocationPointer = ExtendedMemoryBase;

    /// <summary>true once the HMA has been handed to a program.</summary>
    public bool HmaAllocated { get; private set; }

    public XmsManager(PhysicalMemoryBus memory) => _memory = memory;

    /// <summary>Total manageable extended memory, in KB.</summary>
    private uint TotalExtendedKb =>
        _memory.RamSize > ExtendedMemoryBase ? (_memory.RamSize - ExtendedMemoryBase) / 1024 : 0;

    /// <summary>Extended memory still free, in KB.</summary>
    private uint FreeExtendedKb =>
        _memory.RamSize > _allocationPointer ? (_memory.RamSize - _allocationPointer) / 1024 : 0;

    /// <summary>
    /// XMS entry point. The requested function is in AH; the outcome comes back
    /// in AX (1 success, 0 failure) with an error code in BL.
    /// </summary>
    public void Handle(CpuNs.Cpu cpu)
    {
        byte function = cpu.State.GetReg8(4);   // AH

        switch (function)
        {
            case 0x00: GetVersion(cpu); return;
            case 0x01: RequestHma(cpu); return;
            case 0x02: ReleaseHma(cpu); return;
            case 0x03: GlobalEnableA20(cpu); return;
            case 0x04: GlobalDisableA20(cpu); return;
            case 0x05: LocalEnableA20(cpu); return;
            case 0x06: LocalDisableA20(cpu); return;
            case 0x07: QueryA20(cpu); return;
            case 0x08: QueryFreeMemory(cpu); return;
            case 0x09: AllocateBlock(cpu); return;
            case 0x0A: FreeBlock(cpu); return;
            case 0x0B: MoveBlock(cpu); return;
            case 0x0C: LockBlock(cpu); return;
            case 0x0D: UnlockBlock(cpu); return;
            case 0x0E: GetHandleInformation(cpu); return;

            default:
                Fail(cpu, 0x80);   // function not implemented
                return;
        }
    }

    private void GetVersion(CpuNs.Cpu cpu)
    {
        cpu.State.Ax = 0x0300;    // XMS 3.0, in BCD digits
        cpu.State.Bx = 0x0001;    // internal revision of this manager
        cpu.State.Dx = 0x0001;    // the HMA exists
    }

    /// <summary>
    /// Function 0x01: hands the HMA to whoever asks for it. DOS requests it at
    /// startup when CONFIG.SYS contains DOS=HIGH, and moves its kernel there.
    /// </summary>
    private void RequestHma(CpuNs.Cpu cpu)
    {
        if (HmaAllocated) { Fail(cpu, 0x91); return; }   // already allocated

        // DX says how much space is needed. The HMA is 0xFFF0 bytes long, but
        // the value 0xFFFF carries a special meaning: it is what an operating
        // system passes to say "I am taking all of it", and it is exactly what
        // DOS does with DOS=HIGH. Rejecting it would stop DOS from relocating
        // into high memory.
        ushort requestedBytes = cpu.State.Dx;
        if (requestedBytes != 0xFFFF && requestedBytes > HmaSize) { Fail(cpu, 0xA1); return; }

        HmaAllocated = true;
        // Whoever owns the HMA needs A20 to stay on in order to use it.
        _memory.A20Enabled = true;
        Succeed(cpu);
    }

    private void ReleaseHma(CpuNs.Cpu cpu)
    {
        if (!HmaAllocated) { Fail(cpu, 0x93); return; }
        HmaAllocated = false;
        Succeed(cpu);
    }

    // The "global" and "local" flavors of the A20 control functions differ only
    // in reference counting, which is pointless here: there is a single manager
    // and no contention between callers.

    private void GlobalEnableA20(CpuNs.Cpu cpu)
    {
        _memory.A20Enabled = true;
        Succeed(cpu);
    }

    private void GlobalDisableA20(CpuNs.Cpu cpu)
    {
        // Turning A20 off while the HMA is in use would make the code currently
        // executing unreachable, so the request has to be refused.
        if (HmaAllocated) { Fail(cpu, 0x94); return; }
        _memory.A20Enabled = false;
        Succeed(cpu);
    }

    private void LocalEnableA20(CpuNs.Cpu cpu)
    {
        _memory.A20Enabled = true;
        Succeed(cpu);
    }

    private void LocalDisableA20(CpuNs.Cpu cpu)
    {
        if (HmaAllocated) { Fail(cpu, 0x94); return; }
        _memory.A20Enabled = false;
        Succeed(cpu);
    }

    private void QueryA20(CpuNs.Cpu cpu)
    {
        cpu.State.Ax = (ushort)(_memory.A20Enabled ? 1 : 0);
        cpu.State.SetReg8(3, 0);   // BL: no error
    }

    private void QueryFreeMemory(CpuNs.Cpu cpu)
    {
        uint free = FreeExtendedKb;
        // The registers are 16 bit wide, so anything past 64 MB saturates --
        // not a concern with 16 MB of RAM.
        cpu.State.Ax = (ushort)Math.Min(free, 0xFFFF);      // largest free block
        cpu.State.Dx = (ushort)Math.Min(TotalExtendedKb, 0xFFFF);
        cpu.State.SetReg8(3, 0);
    }

    /// <summary>
    /// Function 0x09: allocates an extended memory block. The allocator is
    /// purely linear and never reuses freed space, which is good enough for the
    /// way DOS and period software use XMS: a handful of allocations, made at
    /// startup.
    /// </summary>
    private void AllocateBlock(CpuNs.Cpu cpu)
    {
        uint requestedKb = cpu.State.Dx;
        if (requestedKb == 0) requestedKb = 1;

        if (requestedKb > FreeExtendedKb) { Fail(cpu, 0xA0); return; }

        var block = new Block
        {
            Base = _allocationPointer,
            SizeKb = requestedKb,
            InUse = true,
        };

        _allocationPointer += requestedKb * 1024;

        ushort handle = _nextHandle++;
        _blocks[handle] = block;

        cpu.State.Ax = 1;
        cpu.State.Dx = handle;
    }

    private void FreeBlock(CpuNs.Cpu cpu)
    {
        ushort handle = cpu.State.Dx;
        if (!_blocks.TryGetValue(handle, out Block? block) || !block.InUse)
        {
            Fail(cpu, 0xA2);   // invalid handle
            return;
        }
        if (block.LockCount > 0) { Fail(cpu, 0xAB); return; }   // still locked

        block.InUse = false;
        _blocks.Remove(handle);
        Succeed(cpu);
    }

    /// <summary>
    /// Function 0x0B: copies a block of memory. This is how programs shuttle
    /// data between conventional and extended memory, since in real mode they
    /// cannot address the latter directly.
    ///
    /// The parameters live in a structure pointed at by DS:SI: length, source
    /// handle and offset, destination handle and offset.
    /// </summary>
    private void MoveBlock(CpuNs.Cpu cpu)
    {
        uint parameters = cpu.State.Si;

        uint length = ReadDwordFromDs(cpu, parameters);
        ushort sourceHandle = cpu.ReadMemoryWord(CpuNs.SegReg.Ds, parameters + 4);
        uint sourceOffset = ReadDwordFromDs(cpu, parameters + 6);
        ushort destHandle = cpu.ReadMemoryWord(CpuNs.SegReg.Ds, parameters + 10);
        uint destOffset = ReadDwordFromDs(cpu, parameters + 12);

        // The specification does not allow an odd length.
        if ((length & 1) != 0) { Fail(cpu, 0xA7); return; }
        if (length == 0) { Succeed(cpu); return; }

        if (!TryResolve(sourceHandle, sourceOffset, length, out uint sourceAddress))
        {
            Fail(cpu, (byte)(sourceHandle == 0 ? 0xA7 : 0xA3));
            return;
        }
        if (!TryResolve(destHandle, destOffset, length, out uint destAddress))
        {
            Fail(cpu, (byte)(destHandle == 0 ? 0xA7 : 0xA5));
            return;
        }

        // The copy goes through the bus so that mappings and read-only regions
        // are honored; A20 has to be forced on, because otherwise addresses
        // above one megabyte would wrap around.
        bool previousA20 = _memory.A20Enabled;
        _memory.A20Enabled = true;

        // Copy backwards when the regions overlap and the destination lies
        // ahead of the source, otherwise the copy would clobber the source
        // before reading it.
        if (destAddress > sourceAddress && destAddress < sourceAddress + length)
        {
            for (uint i = length; i > 0; i--)
                _memory.WriteByte(destAddress + i - 1, _memory.ReadByte(sourceAddress + i - 1));
        }
        else
        {
            for (uint i = 0; i < length; i++)
                _memory.WriteByte(destAddress + i, _memory.ReadByte(sourceAddress + i));
        }

        _memory.A20Enabled = previousA20;
        Succeed(cpu);
    }

    /// <summary>
    /// Turns a handle and offset into a physical address. Handle zero means
    /// "conventional memory", and in that case the offset is a segment:offset
    /// pointer packed into 32 bits.
    /// </summary>
    private bool TryResolve(ushort handle, uint offset, uint length, out uint address)
    {
        if (handle == 0)
        {
            // Real-mode pointer: segment in the high half, offset in the low half.
            ushort segment = (ushort)(offset >> 16);
            ushort within = (ushort)offset;
            address = (uint)segment * 16 + within;
            return address + length <= _memory.RamSize;
        }

        if (!_blocks.TryGetValue(handle, out Block? block) || !block.InUse)
        {
            address = 0;
            return false;
        }

        if (offset + length > block.SizeKb * 1024)
        {
            address = 0;
            return false;
        }

        address = block.Base + offset;
        return true;
    }

    private void LockBlock(CpuNs.Cpu cpu)
    {
        ushort handle = cpu.State.Dx;
        if (!_blocks.TryGetValue(handle, out Block? block) || !block.InUse)
        {
            Fail(cpu, 0xA2);
            return;
        }

        block.LockCount++;
        cpu.State.Ax = 1;
        // The physical address of the block is returned in DX:BX.
        cpu.State.Dx = (ushort)(block.Base >> 16);
        cpu.State.Bx = (ushort)block.Base;
    }

    private void UnlockBlock(CpuNs.Cpu cpu)
    {
        ushort handle = cpu.State.Dx;
        if (!_blocks.TryGetValue(handle, out Block? block) || !block.InUse)
        {
            Fail(cpu, 0xA2);
            return;
        }
        if (block.LockCount == 0) { Fail(cpu, 0xAA); return; }

        block.LockCount--;
        Succeed(cpu);
    }

    private void GetHandleInformation(CpuNs.Cpu cpu)
    {
        ushort handle = cpu.State.Dx;
        if (!_blocks.TryGetValue(handle, out Block? block) || !block.InUse)
        {
            Fail(cpu, 0xA2);
            return;
        }

        cpu.State.Ax = 1;
        cpu.State.SetReg8(4, (byte)block.LockCount);            // BH: active locks
        cpu.State.SetReg8(3, (byte)(0xFF - _blocks.Count));     // BL: free handles
        cpu.State.Dx = (ushort)block.SizeKb;
    }

    private uint ReadDwordFromDs(CpuNs.Cpu cpu, uint offset)
        => (uint)(cpu.ReadMemoryWord(CpuNs.SegReg.Ds, offset)
                | (cpu.ReadMemoryWord(CpuNs.SegReg.Ds, offset + 2) << 16));

    private static void Succeed(CpuNs.Cpu cpu)
    {
        cpu.State.Ax = 1;
        cpu.State.SetReg8(3, 0);   // BL: no error
    }

    private static void Fail(CpuNs.Cpu cpu, byte errorCode)
    {
        cpu.State.Ax = 0;
        cpu.State.SetReg8(3, errorCode);   // BL
    }
}
