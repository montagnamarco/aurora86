using Aurora386.Core.Memory;
using CpuNs = Aurora386.Core.Cpu;

namespace Aurora386.Core.Firmware;

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
    // XMS 3.0 function numbers, passed in AH.
    private const byte FunctionGetVersion = 0x00;
    private const byte FunctionRequestHma = 0x01;
    private const byte FunctionReleaseHma = 0x02;
    private const byte FunctionGlobalEnableA20 = 0x03;
    private const byte FunctionGlobalDisableA20 = 0x04;
    private const byte FunctionLocalEnableA20 = 0x05;
    private const byte FunctionLocalDisableA20 = 0x06;
    private const byte FunctionQueryA20 = 0x07;
    private const byte FunctionQueryFreeMemory = 0x08;
    private const byte FunctionAllocateBlock = 0x09;
    private const byte FunctionFreeBlock = 0x0A;
    private const byte FunctionMoveBlock = 0x0B;
    private const byte FunctionLockBlock = 0x0C;
    private const byte FunctionUnlockBlock = 0x0D;
    private const byte FunctionGetHandleInformation = 0x0E;

    // XMS 3.0 error codes, returned in BL.
    private const byte NoError = 0x00;
    private const byte ErrorNotImplemented = 0x80;
    private const byte ErrorHmaInUse = 0x91;
    private const byte ErrorHmaNotAllocated = 0x93;
    private const byte ErrorA20StillEnabled = 0x94;
    private const byte ErrorAllMemoryAllocated = 0xA0;
    private const byte ErrorHmaTooSmall = 0xA1;
    private const byte ErrorInvalidHandle = 0xA2;
    private const byte ErrorInvalidSourceHandle = 0xA3;
    private const byte ErrorInvalidDestinationHandle = 0xA5;
    private const byte ErrorInvalidLength = 0xA7;
    private const byte ErrorBlockNotLocked = 0xAA;
    private const byte ErrorBlockLocked = 0xAB;

    /// <summary>XMS 3.0, in BCD digits.</summary>
    private const ushort SpecificationVersion = 0x0300;

    /// <summary>The internal revision of this manager, reported by function 00h.</summary>
    private const ushort DriverRevision = 0x0001;

    /// <summary>What function 00h puts in DX when the HMA exists.</summary>
    private const ushort HmaExists = 0x0001;

    /// <summary>The DX value an operating system passes to take the whole HMA.</summary>
    private const ushort HmaWholeAreaRequest = 0xFFFF;

    /// <summary>Handle zero in a move request names conventional memory, addressed by segment:offset.</summary>
    private const ushort ConventionalMemoryHandle = 0;

    /// <summary>The number of handles function 0Eh counts free ones against.</summary>
    private const int HandleCount = 0xFF;

    // Offsets inside the move structure DS:SI points at.
    private const uint MoveLengthOffset = 0;
    private const uint MoveSourceHandleOffset = 4;
    private const uint MoveSourceOffsetOffset = 6;
    private const uint MoveDestinationHandleOffset = 10;
    private const uint MoveDestinationOffsetOffset = 12;

    private const uint BytesPerKb = 1024;

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
        _memory.RamSize > ExtendedMemoryBase ? (_memory.RamSize - ExtendedMemoryBase) / BytesPerKb : 0;

    /// <summary>Extended memory still free, in KB.</summary>
    private uint FreeExtendedKb =>
        _memory.RamSize > _allocationPointer ? (_memory.RamSize - _allocationPointer) / BytesPerKb : 0;

    /// <summary>
    /// XMS entry point. The requested function is in AH; the outcome comes back
    /// in AX (1 success, 0 failure) with an error code in BL.
    /// </summary>
    public void Handle(CpuNs.Cpu cpu)
    {
        byte function = cpu.State.GetReg8((int)CpuNs.Reg8.Ah);

        switch (function)
        {
            case FunctionGetVersion:
                GetVersion(cpu);
                return;
            case FunctionRequestHma:
                RequestHma(cpu);
                return;
            case FunctionReleaseHma:
                ReleaseHma(cpu);
                return;
            case FunctionGlobalEnableA20:
                GlobalEnableA20(cpu);
                return;
            case FunctionGlobalDisableA20:
                GlobalDisableA20(cpu);
                return;
            case FunctionLocalEnableA20:
                LocalEnableA20(cpu);
                return;
            case FunctionLocalDisableA20:
                LocalDisableA20(cpu);
                return;
            case FunctionQueryA20:
                QueryA20(cpu);
                return;
            case FunctionQueryFreeMemory:
                QueryFreeMemory(cpu);
                return;
            case FunctionAllocateBlock:
                AllocateBlock(cpu);
                return;
            case FunctionFreeBlock:
                FreeBlock(cpu);
                return;
            case FunctionMoveBlock:
                MoveBlock(cpu);
                return;
            case FunctionLockBlock:
                LockBlock(cpu);
                return;
            case FunctionUnlockBlock:
                UnlockBlock(cpu);
                return;
            case FunctionGetHandleInformation:
                GetHandleInformation(cpu);
                return;
            default:
                Fail(cpu, ErrorNotImplemented);
                return;
        }
    }

    private void GetVersion(CpuNs.Cpu cpu)
    {
        cpu.State.Ax = SpecificationVersion;
        cpu.State.Bx = DriverRevision;
        cpu.State.Dx = HmaExists;
    }

    /// <summary>
    /// Function 0x01: hands the HMA to whoever asks for it. DOS requests it at
    /// startup when CONFIG.SYS contains DOS=HIGH, and moves its kernel there.
    /// </summary>
    private void RequestHma(CpuNs.Cpu cpu)
    {
        if (HmaAllocated)
        {
            Fail(cpu, ErrorHmaInUse);
            return;
        }

        // DX says how much space is needed. The HMA is 0xFFF0 bytes long, but
        // the value 0xFFFF carries a special meaning: it is what an operating
        // system passes to say "I am taking all of it", and it is exactly what
        // DOS does with DOS=HIGH. Rejecting it would stop DOS from relocating
        // into high memory.
        ushort requestedBytes = cpu.State.Dx;
        if (requestedBytes != HmaWholeAreaRequest && requestedBytes > HmaSize)
        {
            Fail(cpu, ErrorHmaTooSmall);
            return;
        }

        HmaAllocated = true;

        // Whoever owns the HMA needs A20 to stay on in order to use it.
        _memory.A20Enabled = true;
        Succeed(cpu);
    }

    private void ReleaseHma(CpuNs.Cpu cpu)
    {
        if (!HmaAllocated)
        {
            Fail(cpu, ErrorHmaNotAllocated);
            return;
        }

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
        if (HmaAllocated)
        {
            Fail(cpu, ErrorA20StillEnabled);
            return;
        }

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
        if (HmaAllocated)
        {
            Fail(cpu, ErrorA20StillEnabled);
            return;
        }

        _memory.A20Enabled = false;
        Succeed(cpu);
    }

    private void QueryA20(CpuNs.Cpu cpu)
    {
        cpu.State.Ax = (ushort)(_memory.A20Enabled ? 1 : 0);
        cpu.State.SetReg8((int)CpuNs.Reg8.Bl, NoError);
    }

    /// <summary>
    /// Function 0x08: how much extended memory is left. AX is the largest
    /// block that could be allocated in one piece and DX the total still
    /// free -- both of them free figures, not the installed size. Reporting
    /// the installed total in DX tells a caller there is memory that has
    /// already been handed out, and one that sizes a buffer from DX and then
    /// allocates it gets a refusal it was not expecting.
    ///
    /// Our allocator never splits a block, so the largest free block and the
    /// total free are the same number.
    /// </summary>
    private void QueryFreeMemory(CpuNs.Cpu cpu)
    {
        // The registers are 16 bit wide, so anything past 64 MB saturates --
        // not a concern with 16 MB of RAM.
        ushort free = (ushort)Math.Min(FreeExtendedKb, ushort.MaxValue);
        cpu.State.Ax = free;      // largest free block
        cpu.State.Dx = free;      // total free
        cpu.State.SetReg8((int)CpuNs.Reg8.Bl, NoError);
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
        if (requestedKb == 0)
        {
            requestedKb = 1;
        }

        if (requestedKb > FreeExtendedKb)
        {
            Fail(cpu, ErrorAllMemoryAllocated);
            return;
        }

        var block = new Block
        {
            Base = _allocationPointer,
            SizeKb = requestedKb,
            InUse = true,
        };

        _allocationPointer += requestedKb * BytesPerKb;

        ushort handle = _nextHandle;
        _nextHandle++;
        _blocks[handle] = block;

        cpu.State.Ax = 1;
        cpu.State.Dx = handle;
    }

    private void FreeBlock(CpuNs.Cpu cpu)
    {
        ushort handle = cpu.State.Dx;
        if (!_blocks.TryGetValue(handle, out Block? block) || !block.InUse)
        {
            Fail(cpu, ErrorInvalidHandle);
            return;
        }

        if (block.LockCount > 0)
        {
            Fail(cpu, ErrorBlockLocked);
            return;
        }

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

        uint length = ReadDwordFromDs(cpu, parameters + MoveLengthOffset);
        ushort sourceHandle = cpu.ReadMemoryWord(CpuNs.SegReg.Ds, parameters + MoveSourceHandleOffset);
        uint sourceOffset = ReadDwordFromDs(cpu, parameters + MoveSourceOffsetOffset);
        ushort destHandle = cpu.ReadMemoryWord(CpuNs.SegReg.Ds, parameters + MoveDestinationHandleOffset);
        uint destOffset = ReadDwordFromDs(cpu, parameters + MoveDestinationOffsetOffset);

        // The specification does not allow an odd length.
        if ((length & 1) != 0)
        {
            Fail(cpu, ErrorInvalidLength);
            return;
        }

        if (length == 0)
        {
            Succeed(cpu);
            return;
        }

        if (!TryResolve(sourceHandle, sourceOffset, length, out uint sourceAddress))
        {
            Fail(cpu, sourceHandle == ConventionalMemoryHandle ? ErrorInvalidLength : ErrorInvalidSourceHandle);
            return;
        }

        if (!TryResolve(destHandle, destOffset, length, out uint destAddress))
        {
            Fail(cpu, destHandle == ConventionalMemoryHandle ? ErrorInvalidLength : ErrorInvalidDestinationHandle);
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
            {
                _memory.WriteByte(destAddress + i - 1, _memory.ReadByte(sourceAddress + i - 1));
            }
        }
        else
        {
            for (uint i = 0; i < length; i++)
            {
                _memory.WriteByte(destAddress + i, _memory.ReadByte(sourceAddress + i));
            }
        }

        _memory.A20Enabled = previousA20;

        // A real HIMEM performs this copy by switching into protected mode,
        // loading segments with a four gigabyte limit, and dropping back to
        // real mode -- and the wide limits survive, because a real-mode load
        // recomputes only the base. Callers rely on that side effect: after
        // asking XMS to move a block they address extended memory directly,
        // which is how a DOS extender reaches its own image above the first
        // megabyte.
        //
        // Copying behind the guest's back is faster and gives the same bytes,
        // but it leaves the segments as they were, so the caller's next
        // 32-bit access faults on a 64 KB limit it thought it had removed.
        // Widening them here reproduces the state the real routine leaves
        // behind, without the cost of emulating the switch itself.
        foreach (CpuNs.SegReg segment in new[]
                 { CpuNs.SegReg.Ds, CpuNs.SegReg.Es, CpuNs.SegReg.Fs, CpuNs.SegReg.Gs })
        {
            cpu.State.Segments[(int)segment].Limit = 0xFFFFFFFF;
        }

        Succeed(cpu);
    }

    /// <summary>
    /// Turns a handle and offset into a physical address. Handle zero means
    /// "conventional memory", and in that case the offset is a segment:offset
    /// pointer packed into 32 bits.
    /// </summary>
    private bool TryResolve(ushort handle, uint offset, uint length, out uint address)
    {
        if (handle == ConventionalMemoryHandle)
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

        if (offset + length > block.SizeKb * BytesPerKb)
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
            Fail(cpu, ErrorInvalidHandle);
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
            Fail(cpu, ErrorInvalidHandle);
            return;
        }

        if (block.LockCount == 0)
        {
            Fail(cpu, ErrorBlockNotLocked);
            return;
        }

        block.LockCount--;
        Succeed(cpu);
    }

    private void GetHandleInformation(CpuNs.Cpu cpu)
    {
        ushort handle = cpu.State.Dx;
        if (!_blocks.TryGetValue(handle, out Block? block) || !block.InUse)
        {
            Fail(cpu, ErrorInvalidHandle);
            return;
        }

        cpu.State.Ax = 1;
        cpu.State.SetReg8((int)CpuNs.Reg8.Bh, (byte)block.LockCount);   // active locks
        cpu.State.SetReg8((int)CpuNs.Reg8.Bl, (byte)(HandleCount - _blocks.Count));   // free handles
        cpu.State.Dx = (ushort)block.SizeKb;
    }

    private uint ReadDwordFromDs(CpuNs.Cpu cpu, uint offset)
    {
        uint low = cpu.ReadMemoryWord(CpuNs.SegReg.Ds, offset);
        uint high = cpu.ReadMemoryWord(CpuNs.SegReg.Ds, offset + 2);
        return low | (high << 16);
    }

    private static void Succeed(CpuNs.Cpu cpu)
    {
        cpu.State.Ax = 1;
        cpu.State.SetReg8((int)CpuNs.Reg8.Bl, NoError);
    }

    private static void Fail(CpuNs.Cpu cpu, byte errorCode)
    {
        cpu.State.Ax = 0;
        cpu.State.SetReg8((int)CpuNs.Reg8.Bl, errorCode);
    }
}
