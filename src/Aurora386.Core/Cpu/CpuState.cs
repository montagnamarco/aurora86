using System.Runtime.CompilerServices;

namespace Aurora386.Core.Cpu;

/// <summary>
/// Parte nascosta di un registro di segmento. Su x86 caricare un selettore
/// copia dal descrittore base/limite/diritti in registri invisibili al
/// programma; da quel momento gli accessi usano questi valori, non il
/// selettore. E' il meccanismo che rende possibile lo "unreal mode" e le
/// transizioni di modo che Windows 3.1 usa in enhanced mode, quindi va
/// modellato fin dall'inizio anche se in real mode e' quasi sempre
/// selettore*16.
/// </summary>
public struct SegmentCache
{
    public ushort Selector;
    public uint Base;
    /// <summary>Ultimo offset valido (non la dimensione): 0xFFFF in real mode.</summary>
    public uint Limit;
    /// <summary>Operandi/indirizzi a 32 bit di default (bit D del descrittore).</summary>
    public bool Big;
    public bool Writable;
    public bool Readable;
    /// <summary>Cresce verso il basso (stack segment expand-down).</summary>
    public bool ExpandDown;
    /// <summary>Descriptor privilege level.</summary>
    public byte Dpl;
}

/// <summary>
/// Stato architetturale del 386: registri, flag, segmenti, tabelle di
/// descrittori e registri di controllo.
/// </summary>
public sealed class CpuState
{
    /// <summary>
    /// Registri generali a 32 bit. Sono tenuti come un array indicizzato
    /// dall'encoding dell'opcode, cosi' la decodifica non ha bisogno di uno
    /// switch. La sovrapposizione EAX/AX/AH/AL e' ottenuta mascherando questo
    /// stesso valore: non esistono copie separate da mantenere sincronizzate.
    /// </summary>
    public readonly uint[] Gpr = new uint[8];

    public uint Eip;

    /// <summary>Segmenti nell'ordine ES CS SS DS FS GS.</summary>
    public readonly SegmentCache[] Segments = new SegmentCache[6];

    // --- flag ---------------------------------------------------------------
    // I flag aritmetici sono calcolati pigramente: memorizzare gli operandi e
    // derivare i flag solo quando servono evita di calcolarne sei per ogni
    // istruzione, quando la maggior parte non viene mai letta. Vedi FlagsCache.
    public Flags OtherFlags = Flags.Reserved1;

    // --- registri di controllo ---------------------------------------------
    public uint Cr0;
    /// <summary>Indirizzo della page fault (CR2), scritto dall'eccezione 14.</summary>
    public uint Cr2;
    /// <summary>Base della page directory.</summary>
    public uint Cr3;

    // --- tabelle di descrittori --------------------------------------------
    public uint GdtBase;
    public ushort GdtLimit;
    public uint IdtBase;
    public ushort IdtLimit;
    public ushort Ldtr;

    /// <summary>
    /// Base e limite della tabella locale, ricavati dal descrittore quando
    /// LLDT carica il selettore: tenerli qui evita di rileggere la GDT a ogni
    /// caricamento di segmento.
    /// </summary>
    public uint LdtBase;
    public uint LdtLimit;
    public ushort TaskRegister;

    /// <summary>Registri di debug DR0-DR7.</summary>
    public readonly uint[] Dr = new uint[8];

    /// <summary>Halted da HLT: riprende alla prossima interruzione.</summary>
    public bool Halted;

    // --- bit di CR0 ---------------------------------------------------------
    public const uint Cr0ProtectionEnable = 1u << 0;
    public const uint Cr0MonitorCoprocessor = 1u << 1;
    public const uint Cr0Emulation = 1u << 2;
    public const uint Cr0TaskSwitched = 1u << 3;
    public const uint Cr0ExtensionType = 1u << 4;
    public const uint Cr0Paging = 1u << 31;

    public bool ProtectedMode => (Cr0 & Cr0ProtectionEnable) != 0;
    public bool PagingEnabled => (Cr0 & Cr0Paging) != 0;

    // --- accesso ai registri generali ---------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint GetReg32(int index) => Gpr[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetReg32(int index, uint value) => Gpr[index] = value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort GetReg16(int index) => (ushort)Gpr[index];

    /// <summary>
    /// Scrive i 16 bit bassi preservando quelli alti: su x86 scrivere AX non
    /// tocca la meta' alta di EAX (a differenza di x86-64, dove scrivere EAX
    /// azzera i 32 bit alti di RAX).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetReg16(int index, ushort value)
        => Gpr[index] = (Gpr[index] & 0xFFFF0000u) | value;

    /// <summary>
    /// Legge un registro a 8 bit. Gli indici 0-3 sono i byte bassi di EAX..EBX,
    /// 4-7 i byte alti degli stessi quattro registri.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetReg8(int index)
        => index < 4 ? (byte)Gpr[index] : (byte)(Gpr[index - 4] >> 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetReg8(int index, byte value)
    {
        if (index < 4)
            Gpr[index] = (Gpr[index] & 0xFFFFFF00u) | value;
        else
            Gpr[index - 4] = (Gpr[index - 4] & 0xFFFF00FFu) | ((uint)value << 8);
    }

    // --- scorciatoie leggibili per i registri usati spesso -------------------

    public uint Eax { get => Gpr[0]; set => Gpr[0] = value; }
    public uint Ecx { get => Gpr[1]; set => Gpr[1] = value; }
    public uint Edx { get => Gpr[2]; set => Gpr[2] = value; }
    public uint Ebx { get => Gpr[3]; set => Gpr[3] = value; }
    public uint Esp { get => Gpr[4]; set => Gpr[4] = value; }
    public uint Ebp { get => Gpr[5]; set => Gpr[5] = value; }
    public uint Esi { get => Gpr[6]; set => Gpr[6] = value; }
    public uint Edi { get => Gpr[7]; set => Gpr[7] = value; }

    public ushort Ax { get => (ushort)Gpr[0]; set => SetReg16(0, value); }
    public ushort Cx { get => (ushort)Gpr[1]; set => SetReg16(1, value); }
    public ushort Dx { get => (ushort)Gpr[2]; set => SetReg16(2, value); }
    public ushort Bx { get => (ushort)Gpr[3]; set => SetReg16(3, value); }
    public ushort Sp { get => (ushort)Gpr[4]; set => SetReg16(4, value); }
    public ushort Bp { get => (ushort)Gpr[5]; set => SetReg16(5, value); }
    public ushort Si { get => (ushort)Gpr[6]; set => SetReg16(6, value); }
    public ushort Di { get => (ushort)Gpr[7]; set => SetReg16(7, value); }

    public ref SegmentCache Es => ref Segments[(int)SegReg.Es];
    public ref SegmentCache Cs => ref Segments[(int)SegReg.Cs];
    public ref SegmentCache Ss => ref Segments[(int)SegReg.Ss];
    public ref SegmentCache Ds => ref Segments[(int)SegReg.Ds];
    public ref SegmentCache Fs => ref Segments[(int)SegReg.Fs];
    public ref SegmentCache Gs => ref Segments[(int)SegReg.Gs];

    /// <summary>
    /// Carica un segmento come farebbe il real mode: base = selettore * 16,
    /// limite 64 KB, tutti i diritti. In protected mode il caricamento passa
    /// invece per il descrittore nella GDT/LDT.
    /// </summary>
    public void LoadSegmentRealMode(SegReg segment, ushort selector)
    {
        ref SegmentCache cache = ref Segments[(int)segment];
        cache.Selector = selector;
        cache.Base = (uint)selector << 4;

        // Il limite NON viene riportato a 64 KB: in real mode il processore
        // ricalcola solo la base, e cio' che resta nella cache del descrittore
        // sopravvive. E' quello che i gestori di memoria chiamano "unreal
        // mode": entrano un istante in protected mode per caricare un limite
        // di 4 GB, tornano indietro, e continuano a indirizzare oltre il
        // megabyte con istruzioni normali. HIMEM.SYS ci copia la memoria
        // estesa, e azzerare il limite qui gli fa fallire ogni scrittura.
        cache.Big = false;
        cache.Readable = true;
        cache.Writable = true;
        cache.ExpandDown = false;
        cache.Dpl = 0;
    }

    /// <summary>
    /// Come sopra ma riportando anche il limite a 64 KB: serve al RESET e
    /// ovunque si voglia uno stato di partenza pulito.
    /// </summary>
    public void LoadSegmentRealModeReset(SegReg segment, ushort selector)
    {
        LoadSegmentRealMode(segment, selector);
        Segments[(int)segment].Limit = 0xFFFF;
    }

    /// <summary>
    /// Stato dopo un RESET, come da manuale Intel: CS:IP = F000:FFF0, che con
    /// la base nascosta di CS a 0xFFFF0000 porta il primo fetch a 0xFFFFFFF0,
    /// dove il chipset rimappa la ROM del BIOS.
    /// </summary>
    public void Reset()
    {
        Array.Clear(Gpr);
        Array.Clear(Dr);
        OtherFlags = Flags.Reserved1;
        Cr0 = 0;
        Cr2 = 0;
        Cr3 = 0;
        GdtBase = IdtBase = 0;
        GdtLimit = IdtLimit = 0xFFFF;
        Ldtr = TaskRegister = 0;
        Halted = false;

        for (int i = 0; i < Segments.Length; i++)
            LoadSegmentRealModeReset((SegReg)i, 0);

        // CS ha un valore di reset particolare: selettore F000 ma base alta,
        // cosi' il BIOS e' raggiungibile prima che venga caricato un descrittore.
        Cs.Selector = 0xF000;
        Cs.Base = 0xFFFF0000;
        Cs.Limit = 0xFFFF;
        Eip = 0xFFF0;

        Edx = 0x00000308;   // signature del 386 DX step D
    }
}
