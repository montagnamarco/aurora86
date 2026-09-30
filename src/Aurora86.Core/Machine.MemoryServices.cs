using CpuNs = Aurora86.Core.Cpu;

namespace Aurora86.Core;

public sealed partial class Machine
{
    /// <summary>
    /// The AT BIOS block move (INT 15h/87h), predating XMS. ES:SI names a
    /// descriptor table; entries 2 and 3 describe source and destination,
    /// and CX counts words. EMM386 uses it to install its page tables even
    /// with HIMEM loaded: rejecting the call becomes "insufficient memory".
    /// </summary>
    private void MoveExtendedMemory(CpuNs.Cpu cpu)
    {
        // Like a BIOS entering protected mode for REP MOVSW, temporarily
        // open A20 and restore it afterwards. The buffers are physical,
        // whereas the caller's pointer to the table is a far address.
        // Reference: SeaBIOS src/system.c, handle_1587 (rel-1.10.0).
        bool previousA20 = Memory.A20Enabled;
        byte status = 0;
        try
        {
            Memory.A20Enabled = true;
            uint table = cpu.FarPointerToLinear(cpu.State.Es.Selector, cpu.State.Si);
            uint count = (uint)cpu.State.Cx * 2;

            (uint Base, uint Limit, byte Access) Descriptor(uint offset)
            {
                uint low = cpu.ReadLinearWord(table + offset) |
                           ((uint)cpu.ReadLinearWord(table + offset + 2) << 16);
                uint high = cpu.ReadLinearWord(table + offset + 4) |
                            ((uint)cpu.ReadLinearWord(table + offset + 6) << 16);
                uint start = (low >> 16) | ((high & 0xFF) << 16) | (high & 0xFF000000);
                uint limit = (low & 0xFFFF) | (high & 0x000F0000);
                if ((high & 0x00800000) != 0) limit = (limit << 12) | 0xFFF;
                return (start, limit, (byte)(high >> 8));
            }

            var source = Descriptor(0x10);
            var destination = Descriptor(0x18);
            // Normal readable data segments, with a writable destination.
            // Fail before writing if either descriptor cannot cover the move.
            if ((source.Access & 0x9C) != 0x90 ||
                (destination.Access & 0x9E) != 0x92 ||
                (count != 0 && ((ulong)count - 1 > source.Limit ||
                                (ulong)count - 1 > destination.Limit ||
                                (ulong)source.Base + count > 0x100000000UL ||
                                (ulong)destination.Base + count > 0x100000000UL)))
                status = 2; // protection exception during the transfer
            else
                // Forward word transfers preserve REP MOVSW overlap behaviour
                // and keep memory-mapped devices and watchpoints on the bus.
                for (uint offset = 0; offset < count; offset += 2)
                    Memory.WriteWord(destination.Base + offset, Memory.ReadWord(source.Base + offset));
        }
        finally
        {
            Memory.A20Enabled = previousA20;
        }

        cpu.State.SetReg8(4, status);
        cpu.SetCarryInStackedFlags(status != 0);
    }
}
