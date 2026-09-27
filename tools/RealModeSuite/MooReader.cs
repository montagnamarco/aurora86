using System.Text;

namespace RealModeSuite;

// Parses the MOO binary test format, described at
// https://github.com/dbalsom/moo/blob/main/doc/moo_format_v1.md (version 1.1).
// The file is a `MOO ` header chunk followed by a `META` chunk, an optional
// file-wide RMSK/RM32 undefined-bits mask, and one `TEST` chunk per test.
// Every chunk is `[4-byte ASCII id][u32 little-endian length][payload]`; a
// conforming reader advances by the length field rather than assuming chunks
// are adjacent, which is what lets this reader skip QUEU and EA32 without
// having to understand them.
//
// The 80386 vectors always carry `RG32`/`RM32` (the 32-bit register file the
// 386 added), never the 16-bit `REGS`/`RMSK` used by the 8088-class suites;
// that was confirmed by inspecting several files (90, EB, CD, F7.6, A4) by
// hand. ParseRegisters16 exists only to fail loudly if that assumption is
// ever wrong, rather than silently misreading a register file with a
// different layout.
internal static class MooReader
{
    // From LSB to MSB, the order of the 32-bit register file MOO defines.
    private static class Slot
    {
        public const int Cr0 = 0;
        public const int Cr3 = 1;
        public const int Eax = 2;
        public const int Ebx = 3;
        public const int Ecx = 4;
        public const int Edx = 5;
        public const int Esi = 6;
        public const int Edi = 7;
        public const int Ebp = 8;
        public const int Esp = 9;
        public const int Cs = 10;
        public const int Ds = 11;
        public const int Es = 12;
        public const int Fs = 13;
        public const int Gs = 14;
        public const int Ss = 15;
        public const int Eip = 16;
        public const int EFlags = 17;
        public const int Dr6 = 18;
        public const int Dr7 = 19;
        public const int Count = 20;
    }

    public static string SlotName(int bit) => bit switch
    {
        Slot.Cr0 => "cr0", Slot.Cr3 => "cr3", Slot.Eax => "eax", Slot.Ebx => "ebx",
        Slot.Ecx => "ecx", Slot.Edx => "edx", Slot.Esi => "esi", Slot.Edi => "edi",
        Slot.Ebp => "ebp", Slot.Esp => "esp", Slot.Cs => "cs", Slot.Ds => "ds",
        Slot.Es => "es", Slot.Fs => "fs", Slot.Gs => "gs", Slot.Ss => "ss",
        Slot.Eip => "eip", Slot.EFlags => "eflags", Slot.Dr6 => "dr6", Slot.Dr7 => "dr7",
        _ => "?",
    };

    public static uint Get(in MooRegisters regs, int bit) => bit switch
    {
        Slot.Cr0 => regs.Cr0, Slot.Cr3 => regs.Cr3, Slot.Eax => regs.Eax, Slot.Ebx => regs.Ebx,
        Slot.Ecx => regs.Ecx, Slot.Edx => regs.Edx, Slot.Esi => regs.Esi, Slot.Edi => regs.Edi,
        Slot.Ebp => regs.Ebp, Slot.Esp => regs.Esp, Slot.Cs => regs.Cs, Slot.Ds => regs.Ds,
        Slot.Es => regs.Es, Slot.Fs => regs.Fs, Slot.Gs => regs.Gs, Slot.Ss => regs.Ss,
        Slot.Eip => regs.Eip, Slot.EFlags => regs.EFlags, Slot.Dr6 => regs.Dr6, Slot.Dr7 => regs.Dr7,
        _ => 0,
    };

    public const int SlotCount = Slot.Count;
    public const int Cr0Bit = Slot.Cr0;
    public const int Cr3Bit = Slot.Cr3;
    public const int EaxBit = Slot.Eax;
    public const int EbxBit = Slot.Ebx;
    public const int EcxBit = Slot.Ecx;
    public const int EdxBit = Slot.Edx;
    public const int EsiBit = Slot.Esi;
    public const int EdiBit = Slot.Edi;
    public const int EbpBit = Slot.Ebp;
    public const int EspBit = Slot.Esp;
    public const int CsBit = Slot.Cs;
    public const int DsBit = Slot.Ds;
    public const int EsBit = Slot.Es;
    public const int FsBit = Slot.Fs;
    public const int GsBit = Slot.Gs;
    public const int SsBit = Slot.Ss;
    public const int EipBit = Slot.Eip;
    public const int EFlagsBit = Slot.EFlags;
    public const int Dr6Bit = Slot.Dr6;
    public const int Dr7Bit = Slot.Dr7;

    public static MooFile Load(byte[] data)
    {
        if (data.Length < MinimumFileLength || Ascii(data, 0) != "MOO ")
        {
            throw new InvalidDataException("not a MOO file (missing 'MOO ' header chunk)");
        }

        uint headerLength = ReadU32(data, 4);
        int offset = 8 + (int)headerLength;

        var tests = new List<MooTest>();
        MooRegisters? topMask = null;

        foreach ((string id, int start, int end) in Chunks(data, offset, data.Length))
        {
            switch (id)
            {
                case "TEST":
                    tests.Add(ParseTest(data, start, end));
                    break;
                case "RM32":
                    topMask = ParseRegisters32(data, start);
                    break;
                case "RMSK":
                    // Never seen in the 386 real-mode set; see the file comment.
                    throw new NotSupportedException("16-bit RMSK at top level: expected RM32 for a 386 vector file");
                // META carries nothing this runner needs.
            }
        }

        return new MooFile(tests, topMask);
    }

    private static MooTest ParseTest(byte[] data, int start, int end)
    {
        // The payload begins with the test's own index, which this runner has
        // no use for: tests are reported by name, and the file order is kept
        // regardless.
        int bodyStart = start + 4;

        string name = "";
        byte[] bytes = [];
        MooRegisters initRegs = default;
        List<(uint Address, byte Value)> initRam = [];
        MooRegisters finalRegs = default;
        MooRegisters? finalMask = null;
        List<(uint Address, byte Value)> finalRam = [];
        (byte Vector, uint FlagAddress)? exception = null;
        string hash = "";

        foreach ((string id, int s, int e) in Chunks(data, bodyStart, end))
        {
            switch (id)
            {
                case "NAME":
                {
                    uint length = ReadU32(data, s);
                    name = Encoding.ASCII.GetString(data, s + 4, (int)length);
                    break;
                }
                case "BYTS":
                {
                    uint length = ReadU32(data, s);
                    bytes = data.AsSpan(s + 4, (int)length).ToArray();
                    break;
                }
                case "INIT":
                    (initRegs, initRam, _) = ParseSnapshot(data, s, e);
                    break;
                case "FINA":
                    (finalRegs, finalRam, finalMask) = ParseSnapshot(data, s, e);
                    break;
                case "EXCP":
                    exception = (data[s], ReadU32(data, s + 1));
                    break;
                case "HASH":
                    hash = Convert.ToHexStringLower(data.AsSpan(s, 20));
                    break;
                // CYCL is bus-cycle detail this runner does not validate.
            }
        }

        return new MooTest(name, bytes, initRegs, initRam, finalRegs, finalMask, finalRam, exception, hash);
    }

    private static (MooRegisters Regs, List<(uint, byte)> Ram, MooRegisters? Mask) ParseSnapshot(byte[] data, int start, int end)
    {
        MooRegisters regs = default;
        MooRegisters? mask = null;
        var ram = new List<(uint, byte)>();

        foreach ((string id, int s, int e) in Chunks(data, start, end))
        {
            switch (id)
            {
                case "RG32":
                    regs = ParseRegisters32(data, s);
                    break;
                case "RM32":
                    mask = ParseRegisters32(data, s);
                    break;
                case "REGS":
                case "RMSK":
                    throw new NotSupportedException($"16-bit {id} chunk: expected the 386's 32-bit register file");
                case "RAM ":
                {
                    uint count = ReadU32(data, s);
                    int p = s + 4;
                    for (int i = 0; i < count; i++)
                    {
                        uint address = ReadU32(data, p);
                        byte value = data[p + 4];
                        ram.Add((address, value));
                        p += 5;
                    }
                    break;
                }
                // QUEU (prefetch queue) and EA32 (convenience effective-address
                // info) are not needed to run or check a test.
            }
        }

        return (regs, ram, mask);
    }

    private static MooRegisters ParseRegisters32(byte[] data, int start)
    {
        uint mask = ReadU32(data, start);
        int p = start + 4;
        var regs = new MooRegisters { Mask = mask };

        for (int bit = 0; bit < Slot.Count; bit++)
        {
            if ((mask & (1u << bit)) == 0)
            {
                continue;
            }

            uint value = ReadU32(data, p);
            p += 4;

            switch (bit)
            {
                case Slot.Cr0:
                    regs.Cr0 = value;
                    break;
                case Slot.Cr3:
                    regs.Cr3 = value;
                    break;
                case Slot.Eax:
                    regs.Eax = value;
                    break;
                case Slot.Ebx:
                    regs.Ebx = value;
                    break;
                case Slot.Ecx:
                    regs.Ecx = value;
                    break;
                case Slot.Edx:
                    regs.Edx = value;
                    break;
                case Slot.Esi:
                    regs.Esi = value;
                    break;
                case Slot.Edi:
                    regs.Edi = value;
                    break;
                case Slot.Ebp:
                    regs.Ebp = value;
                    break;
                case Slot.Esp:
                    regs.Esp = value;
                    break;
                case Slot.Cs:
                    regs.Cs = value;
                    break;
                case Slot.Ds:
                    regs.Ds = value;
                    break;
                case Slot.Es:
                    regs.Es = value;
                    break;
                case Slot.Fs:
                    regs.Fs = value;
                    break;
                case Slot.Gs:
                    regs.Gs = value;
                    break;
                case Slot.Ss:
                    regs.Ss = value;
                    break;
                case Slot.Eip:
                    regs.Eip = value;
                    break;
                case Slot.EFlags:
                    regs.EFlags = value;
                    break;
                case Slot.Dr6:
                    regs.Dr6 = value;
                    break;
                case Slot.Dr7:
                    regs.Dr7 = value;
                    break;
            }
        }

        return regs;
    }

    // Walks a run of sibling chunks between two byte offsets, yielding each
    // one's id and the [start, end) of its payload. The caller must consume
    // each chunk before asking for the next: nothing here looks ahead.
    private static IEnumerable<(string Id, int Start, int End)> Chunks(byte[] data, int offset, int end)
    {
        while (offset < end)
        {
            string id = Ascii(data, offset);
            uint length = ReadU32(data, offset + 4);
            int payloadStart = offset + 8;
            int payloadEnd = payloadStart + (int)length;
            yield return (id, payloadStart, payloadEnd);
            offset = payloadEnd;
        }
    }

    /// <summary>The MOO header chunk alone: id, length and at least a version dword.</summary>
    private const int MinimumFileLength = 12;

    private static string Ascii(byte[] data, int offset) => Encoding.ASCII.GetString(data, offset, 4);

    private static uint ReadU32(byte[] data, int offset)
        => (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
}

// The 386's 32-bit register file, as MOO's RG32/RM32 chunks lay it out. Mask
// records which of the twenty slots this instance actually carries: INIT
// always sets every bit, FINA and a mask chunk set only the ones present.
internal struct MooRegisters
{
    public uint Mask;
    public uint Cr0;
    public uint Cr3;
    public uint Eax;
    public uint Ebx;
    public uint Ecx;
    public uint Edx;
    public uint Esi;
    public uint Edi;
    public uint Ebp;
    public uint Esp;
    public uint Cs;
    public uint Ds;
    public uint Es;
    public uint Fs;
    public uint Gs;
    public uint Ss;
    public uint Eip;
    public uint EFlags;
    public uint Dr6;
    public uint Dr7;

    public readonly bool Has(int bit) => (Mask & (1u << bit)) != 0;
}

internal sealed record MooTest(
    string Name,
    byte[] Bytes,
    MooRegisters InitRegs,
    List<(uint Address, byte Value)> InitRam,
    MooRegisters FinalRegs,
    MooRegisters? FinalMask,
    List<(uint Address, byte Value)> FinalRam,
    (byte Vector, uint FlagAddress)? Exception,
    string Hash);

internal sealed record MooFile(List<MooTest> Tests, MooRegisters? TopMask);
