namespace Aurora386.Core.Cpu;

/// <summary>
/// General-purpose register indices, in the order the reg/rm fields of an x86
/// instruction encode them. The order is neither alphabetical nor arbitrary:
/// it is the hardware's own, and it has to be respected because the decoder
/// indexes straight into the register file with bits taken from the opcode.
/// </summary>
public enum Reg
{
    Eax = 0, Ecx = 1, Edx = 2, Ebx = 3,
    Esp = 4, Ebp = 5, Esi = 6, Edi = 7,
}

/// <summary>
/// The 8-bit registers. The first four are the low bytes of EAX..EBX, the
/// other four the high bytes of those same registers: AL CL DL BL AH CH DH BH.
/// </summary>
public enum Reg8
{
    Al = 0, Cl = 1, Dl = 2, Bl = 3,
    Ah = 4, Ch = 5, Dh = 6, Bh = 7,
}

/// <summary>
/// Segment registers, in the order the hardware encodes them.
/// </summary>
public enum SegReg
{
    Es = 0, Cs = 1, Ss = 2, Ds = 3, Fs = 4, Gs = 5,
}

/// <summary>
/// Bits of the 386 EFLAGS register.
/// </summary>
[Flags]
public enum Flags : uint
{
    None = 0,
    Carry = 1u << 0,
    /// <summary>Reads back as 1 on real silicon, always.</summary>
    Reserved1 = 1u << 1,
    Parity = 1u << 2,
    Adjust = 1u << 4,        // auxiliary carry, used by the BCD adjust instructions
    Zero = 1u << 6,
    Sign = 1u << 7,
    Trap = 1u << 8,
    Interrupt = 1u << 9,
    Direction = 1u << 10,
    Overflow = 1u << 11,
    /// <summary>I/O privilege level: two bits, 12-13.</summary>
    Iopl = 3u << 12,
    NestedTask = 1u << 14,
    Resume = 1u << 16,
    /// <summary>Virtual 8086 mode: needed by Windows 3.1 in enhanced mode.</summary>
    Virtual8086 = 1u << 17,
}
