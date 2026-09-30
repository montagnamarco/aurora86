namespace Aurora86.Core.Cpu;

/// <summary>
/// An operand decoded from a ModR/M byte: either a register, or a memory
/// address that has already been resolved (segment + offset).
/// </summary>
public struct ModRm
{
    /// <summary>The address depends on ESP: POP needs to know, since it evaluates the address after the stack has moved.</summary>
    public bool UsesStackPointer;

    /// <summary>The reg field: either the second operand, or an opcode extension.</summary>
    public byte Reg;

    /// <summary>true when the r/m operand is a register (mod == 3).</summary>
    public bool IsRegister;

    /// <summary>Register index, when IsRegister.</summary>
    public byte Register;

    /// <summary>Offset within the segment, when the operand is in memory.</summary>
    public uint Offset;

    /// <summary>Effective segment, with any override prefix already applied.</summary>
    public SegReg Segment;
}

/// <summary>
/// The prefixes that may precede an instruction. The 386 accepts more than one
/// and their order carries no meaning, so they are collected in a loop before
/// the opcode proper is read.
/// </summary>
public struct Prefixes
{
    /// <summary>Explicit segment override, or null when there is none.</summary>
    public SegReg? Segment;

    /// <summary>0x66: flips the default operand size for this instruction.</summary>
    public bool OperandSize;

    /// <summary>0x67: flips the default address size for this instruction.</summary>
    public bool AddressSize;

    /// <summary>0xF3 REP/REPE, or 0xF2 REPNE.</summary>
    public bool Rep;
    public bool RepNe;

    /// <summary>0xF0: on a uniprocessor emulator it has no observable effect.</summary>
    public bool Lock;

    public void Clear()
    {
        Segment = null;
        OperandSize = false;
        AddressSize = false;
        Rep = false;
        RepNe = false;
        Lock = false;
    }
}
