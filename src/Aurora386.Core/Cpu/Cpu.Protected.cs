namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    // Protected mode. What is implemented so far is the slice needed to run
    // extended memory managers such as HIMEM.SYS: they enter protected mode
    // only to copy blocks above the one-megabyte line and drop straight back
    // out, so they need segment descriptors but never privilege transitions,
    // task gates or call gates.

    /// <summary>
    /// Loads a segment register by reading its descriptor out of the GDT or the
    /// LDT. The selector splits into an index (bits 15-3), a table bit (bit 2)
    /// and the requested privilege level (bits 1-0).
    /// </summary>
    private void LoadSegmentProtected(SegReg segment, ushort selector)
    {
        ref SegmentCache cache = ref State.Segments[(int)segment];

        // The null selector may legitimately be loaded into a data segment
        // register: it makes the register unusable, but loading it is not an
        // error -- operating systems use it to make sure stale descriptors
        // cannot be dereferenced by accident. Into CS or SS it is an error.
        if ((selector & 0xFFFC) == 0)
        {
            if (segment is SegReg.Cs or SegReg.Ss)
                throw new CpuException(13, selector);

            cache.Selector = selector;
            cache.Base = 0;
            cache.Limit = 0;
            cache.Readable = cache.Writable = false;
            return;
        }

        ulong descrittore = LeggiDescrittore(selector);

        // The descriptor's fields are scattered across the eight bytes so that
        // the layout stays backwards compatible with the 286, which only had
        // 24-bit bases and 16-bit limits: the base arrives in three pieces and
        // the limit in two, with the 386's extra bits bolted on at the top.
        uint baseAddress = (uint)(((descrittore >> 16) & 0xFFFFFF)
                                | ((descrittore >> 32) & 0xFF000000));
        uint limite = (uint)((descrittore & 0xFFFF)
                           | ((descrittore >> 32) & 0x0F0000));

        byte accesso = (byte)(descrittore >> 40);
        byte granularita = (byte)(descrittore >> 52);

        // The granularity bit scales the limit by 4096. The remainder of the
        // final page stays accessible, so the low twelve bits are filled with
        // ones rather than zeroed -- a 4 GB segment is encoded as a limit of
        // 0xFFFFF pages, which has to expand to 0xFFFFFFFF.
        if ((granularita & 0x08) != 0) limite = (limite << 12) | 0xFFF;

        // Bit 7 of the access byte: the descriptor is present in memory. A
        // not-present segment raises #NP (or #SS when it is the stack), which
        // is the hook a virtual memory system uses to load it on demand.
        if ((accesso & 0x80) == 0)
            throw new CpuException(segment == SegReg.Ss ? (byte)12 : (byte)11, selector);

        cache.Selector = selector;
        cache.Base = baseAddress;
        cache.Limit = limite;
        cache.Big = (granularita & 0x04) != 0;
        cache.Dpl = (byte)((accesso >> 5) & 3);

        // Bit 3 of the access byte separates code from data; for code, bit 1
        // says whether it is readable, for data whether it is writable. Code is
        // never writable, which is how the architecture keeps a program from
        // rewriting itself through its own CS.
        bool codice = (accesso & 0x08) != 0;
        cache.Readable = !codice || (accesso & 0x02) != 0;
        cache.Writable = !codice && (accesso & 0x02) != 0;
        cache.ExpandDown = !codice && (accesso & 0x04) != 0;
    }

    /// <summary>
    /// Reads the eight bytes of the descriptor named by the selector. The index
    /// has to fall inside the table's limit, otherwise the selector simply is
    /// not valid and the load raises #GP with the selector as error code.
    /// </summary>
    private ulong LeggiDescrittore(ushort selector)
    {
        bool locale = (selector & 0x04) != 0;
        uint indice = (uint)(selector & 0xFFF8);

        uint tabellaBase = locale ? State.LdtBase : State.GdtBase;
        uint tabellaLimite = locale ? State.LdtLimit : State.GdtLimit;

        if (indice + 7 > tabellaLimite)
            throw new CpuException(13, selector);

        uint indirizzo = tabellaBase + indice;
        return _memory.ReadDword(TranslateLinear(indirizzo, false))
             | ((ulong)_memory.ReadDword(TranslateLinear(indirizzo + 4, false)) << 32);
    }

    private void EnterInterruptProtected(byte vector, uint errorCode)
        => throw new NotImplementedException(
            $"Interrupt {vector} in protected mode is not implemented yet.");

    private void IretProtected()
        => throw new NotImplementedException(
            "IRET in protected mode is not implemented yet.");
}
