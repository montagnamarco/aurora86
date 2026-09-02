namespace Emulator.Core.Cpu;

/// <summary>
/// Operando decodificato da un byte ModR/M: o un registro, o un indirizzo di
/// memoria gia' risolto (segmento + offset).
/// </summary>
public struct ModRm
{
    /// <summary>L'indirizzo dipende da ESP: serve a POP, che lo valuta dopo.</summary>
    public bool UsesStackPointer;

    /// <summary>Campo reg: secondo operando, o estensione dell'opcode.</summary>
    public byte Reg;

    /// <summary>true se l'operando r/m e' un registro (mod == 3).</summary>
    public bool IsRegister;

    /// <summary>Indice del registro, quando IsRegister.</summary>
    public byte Register;

    /// <summary>Offset dentro il segmento, quando e' memoria.</summary>
    public uint Offset;

    /// <summary>Segmento effettivo, prefissi di override gia' applicati.</summary>
    public SegReg Segment;
}

/// <summary>
/// Prefissi che precedono un'istruzione. Il 386 ne ammette piu' di uno e
/// l'ordine e' irrilevante, quindi vengono raccolti in un ciclo prima
/// dell'opcode vero e proprio.
/// </summary>
public struct Prefixes
{
    /// <summary>Override di segmento esplicito, o null se assente.</summary>
    public SegReg? Segment;

    /// <summary>0x66: inverte la dimensione di default degli operandi.</summary>
    public bool OperandSize;

    /// <summary>0x67: inverte la dimensione di default degli indirizzi.</summary>
    public bool AddressSize;

    /// <summary>0xF3 REP/REPE, oppure 0xF2 REPNE.</summary>
    public bool Rep;
    public bool RepNe;

    /// <summary>0xF0: su un emulatore monoprocessore non ha effetti osservabili.</summary>
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
