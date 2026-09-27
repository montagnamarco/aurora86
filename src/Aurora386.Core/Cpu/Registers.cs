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

    /// <summary>
    /// Alignment check, from the 486. Enforces alignment for ring 3 code when
    /// CR0.AM is also set, which almost nothing ever asked for.
    /// </summary>
    AlignmentCheck = 1u << 18,

    /// <summary>
    /// The bit that says CPUID exists, and the oddest one in the register: a
    /// program finds out whether it can ask the processor its name by trying
    /// to invert this bit and seeing whether the change sticks. On a processor
    /// without CPUID it reads back unchanged.
    /// </summary>
    Identification = 1u << 21,

    /// <summary>
    /// Virtual interrupt flag (P2.3, VME/PVI). The monitor's own copy of IF,
    /// substituted for the real one wherever CR4.VME (in a V86 task) or
    /// CR4.PVI (ring 3 in protected mode) says the current privilege is not
    /// trusted with the real flag: CLI/STI, and below IOPL 3 also PUSHF,
    /// POPF, IRET and a redirected INT n, read and write this bit instead of
    /// <see cref="Interrupt"/>. Exists at all only on silicon that carries
    /// CPUID.VME (Intel SDM Vol.1 §3.4.3) -- see the Identification bit
    /// above for the same "does this bit even exist" gate.
    /// </summary>
    VirtualInterrupt = 1u << 19,

    /// <summary>
    /// Virtual interrupt pending (P2.3, VME/PVI). Set only by software -- the
    /// monitor, through an IRET or POPF at CPL 0 -- to record that a virtual
    /// interrupt is queued behind <see cref="VirtualInterrupt"/> being clear;
    /// no hardware interrupt or exception ever touches it. STI (real or
    /// virtualized) and the VME/PVI virtualized POPF/IRET refuse to set VIF
    /// while this bit is set, with #GP(0), which is precisely the signal that
    /// tells the monitor to deliver what it was holding back.
    /// </summary>
    VirtualInterruptPending = 1u << 20,
}
