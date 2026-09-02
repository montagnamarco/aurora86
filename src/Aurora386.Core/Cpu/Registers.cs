namespace Aurora386.Core.Cpu;

/// <summary>
/// Indici dei registri generali, nell'ordine in cui li codifica il campo
/// reg/rm delle istruzioni x86. L'ordine non e' alfabetico ne' arbitrario:
/// e' quello dell'hardware, e va rispettato perche' la decodifica indicizza
/// direttamente con i bit dell'opcode.
/// </summary>
public enum Reg
{
    Eax = 0, Ecx = 1, Edx = 2, Ebx = 3,
    Esp = 4, Ebp = 5, Esi = 6, Edi = 7,
}

/// <summary>
/// Registri a 8 bit. I primi quattro sono i byte bassi di EAX..EBX, gli altri
/// quattro i byte alti: AL CL DL BL AH CH DH BH.
/// </summary>
public enum Reg8
{
    Al = 0, Cl = 1, Dl = 2, Bl = 3,
    Ah = 4, Ch = 5, Dh = 6, Bh = 7,
}

/// <summary>
/// Registri di segmento, nell'ordine codificato dall'hardware.
/// </summary>
public enum SegReg
{
    Es = 0, Cs = 1, Ss = 2, Ds = 3, Fs = 4, Gs = 5,
}

/// <summary>
/// Bit del registro EFLAGS del 386.
/// </summary>
[Flags]
public enum Flags : uint
{
    None = 0,
    Carry = 1u << 0,
    /// <summary>Sempre 1 sui processori reali.</summary>
    Reserved1 = 1u << 1,
    Parity = 1u << 2,
    Adjust = 1u << 4,        // carry ausiliario, per l'aritmetica BCD
    Zero = 1u << 6,
    Sign = 1u << 7,
    Trap = 1u << 8,
    Interrupt = 1u << 9,
    Direction = 1u << 10,
    Overflow = 1u << 11,
    /// <summary>I/O privilege level: due bit, 12-13.</summary>
    Iopl = 3u << 12,
    NestedTask = 1u << 14,
    Resume = 1u << 16,
    /// <summary>Virtual 8086 mode: serve a Windows 3.1 in enhanced mode.</summary>
    Virtual8086 = 1u << 17,
}
