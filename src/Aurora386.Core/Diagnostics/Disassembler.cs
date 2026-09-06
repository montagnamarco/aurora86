using System.Globalization;
using System.Text;

namespace Aurora386.Core.Diagnostics;

/// <summary>
/// One instruction, decoded and rendered.
/// </summary>
/// <param name="Address">Linear address the instruction starts at.</param>
/// <param name="Length">Length in bytes, prefixes included.</param>
/// <param name="Text">Intel-syntax text, e.g. "mov ax, 1234h".</param>
/// <param name="Bytes">The raw bytes, space separated, e.g. "B8 34 12".</param>
public sealed record DecodedInstruction(
    uint Address,
    int Length,
    string Text,
    string Bytes);

/// <summary>
/// A pure, stateless x86 disassembler covering the 80386 instruction set.
///
/// It is deliberately independent of the CPU core: it reads memory through a
/// callback, so the debugger can point it at guest RAM, at a file, or at a
/// scratch buffer in a unit test. It never throws and never faults; anything it
/// cannot make sense of comes back as "db 0NNh", because a debugger is
/// routinely asked to disassemble data.
/// </summary>
public static class Disassembler
{
    // --- register name tables -------------------------------------------------
    //
    // The order below is the hardware's own, the one the reg and r/m fields of
    // the ModR/M byte index directly. It is neither alphabetical nor the order
    // in which the registers were historically named, and it must be respected
    // exactly.

    private static readonly string[] Reg8 = { "al", "cl", "dl", "bl", "ah", "ch", "dh", "bh" };
    private static readonly string[] Reg16 = { "ax", "cx", "dx", "bx", "sp", "bp", "si", "di" };
    private static readonly string[] Reg32 = { "eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi" };
    private static readonly string[] SegRegs = { "es", "cs", "ss", "ds", "fs", "gs" };

    /// <summary>The seven fixed base+index pairs of 16-bit addressing, plus [bx].</summary>
    private static readonly string[] Mem16 =
    {
        "bx+si", "bx+di", "bp+si", "bp+di", "si", "di", "bp", "bx",
    };

    /// <summary>Condition codes, in the order the low nibble of a Jcc/SETcc opcode encodes them.</summary>
    private static readonly string[] Cc =
    {
        "o", "no", "b", "ae", "z", "nz", "be", "a",
        "s", "ns", "p", "np", "l", "ge", "le", "g",
    };

    private static readonly string[] AluOps = { "add", "or", "adc", "sbb", "and", "sub", "xor", "cmp" };
    private static readonly string[] ShiftOps = { "rol", "ror", "rcl", "rcr", "shl", "shr", "sal", "sar" };

    /// <summary>
    /// Decodes a single instruction starting at <paramref name="address"/>.
    /// </summary>
    /// <param name="readByte">Reads one byte at a linear address. May return anything.</param>
    /// <param name="address">Linear address of the first byte.</param>
    /// <param name="bits32">
    /// True when the code segment's default size is 32 bits. This flips the
    /// meaning of the 0x66 and 0x67 prefixes: they always toggle away from the
    /// default, they do not select a fixed size.
    /// </param>
    public static DecodedInstruction Decode(Func<uint, byte> readByte, uint address, bool bits32)
    {
        ArgumentNullException.ThrowIfNull(readByte);

        var ctx = new Context(readByte, address, bits32);
        string text;
        try
        {
            text = DecodeInstruction(ref ctx);
        }
        catch (Exception)
        {
            // The reader callback belongs to someone else and the decoder walks
            // arbitrary bytes; neither is allowed to take the debugger down.
            ctx.Reset();
            text = Db(ctx.Next());
        }

        // A run of prefixes with nothing to prefix (or a truncated instruction)
        // is data, not code: fall back to a single byte so the caller can keep
        // walking forward one byte at a time.
        if (ctx.Length == 0)
        {
            ctx.Reset();
            text = Db(ctx.Next());
        }

        return new DecodedInstruction(address, ctx.Length, text, ctx.HexBytes());
    }

    /// <summary>
    /// Convenience overload for a byte array treated as living at
    /// <paramref name="address"/>.
    /// </summary>
    public static DecodedInstruction Decode(byte[] code, uint address, bool bits32)
    {
        ArgumentNullException.ThrowIfNull(code);
        return Decode(a =>
        {
            long i = (long)a - address;
            return i >= 0 && i < code.Length ? code[i] : (byte)0;
        }, address, bits32);
    }

    // =========================================================================
    // decoding context
    // =========================================================================

    /// <summary>
    /// Cursor over the instruction stream plus the prefix state gathered so far.
    /// A struct so that decoding allocates nothing beyond the strings it builds.
    /// </summary>
    private struct Context
    {
        private readonly Func<uint, byte> _read;
        private readonly uint _start;
        private readonly List<byte> _bytes;

        public Context(Func<uint, byte> read, uint start, bool bits32)
        {
            _read = read;
            _start = start;
            _bytes = new List<byte>(16);
            DefaultIs32 = bits32;
            OperandSize32 = bits32;
            AddressSize32 = bits32;
            Segment = null;
            Rep = null;
            Lock = false;
        }

        public bool DefaultIs32 { get; }
        public bool OperandSize32 { get; set; }
        public bool AddressSize32 { get; set; }
        public string? Segment { get; set; }
        public string? Rep { get; set; }
        public bool Lock { get; set; }

        public readonly int Length => _bytes.Count;
        public readonly uint Start => _start;
        public readonly uint CurrentAddress => (uint)(_start + (uint)_bytes.Count);

        /// <summary>Reads the next byte without consuming it.</summary>
        public readonly byte Peek() => _bytes.Count >= 15 ? (byte)0 : _read(CurrentAddress);

        /// <summary>Consumes the next byte, recording it for the byte dump.</summary>
        public byte Next()
        {
            // 15 bytes is the architectural maximum instruction length. Beyond
            // that we are certainly decoding garbage, so stop consuming rather
            // than run away over a long stretch of prefixes.
            if (_bytes.Count >= 15) return 0;
            byte b = _read(CurrentAddress);
            _bytes.Add(b);
            return b;
        }

        public ushort NextWord()
        {
            byte lo = Next();
            byte hi = Next();
            return (ushort)(lo | (hi << 8));
        }

        public uint NextDword()
        {
            uint lo = NextWord();
            uint hi = NextWord();
            return lo | (hi << 16);
        }

        /// <summary>Immediate/displacement at the current operand size.</summary>
        public uint NextImmediate() => OperandSize32 ? NextDword() : NextWord();

        /// <summary>Address-sized immediate, used by the moffs forms of MOV.</summary>
        public uint NextAddressImmediate() => AddressSize32 ? NextDword() : NextWord();

        public void Reset() => _bytes.Clear();

        public string HexBytes()
        {
            var sb = new StringBuilder(_bytes.Count * 3);
            for (int i = 0; i < _bytes.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(_bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }

    /// <summary>A decoded ModR/M byte, with the r/m operand already rendered.</summary>
    private readonly record struct ModRm(byte Mod, byte Reg, byte Rm, string Text, bool IsRegister);

    // =========================================================================
    // number formatting
    // =========================================================================

    /// <summary>
    /// Renders a value in the classic MASM/TASM style: hexadecimal with an "h"
    /// suffix, and a leading zero whenever the first digit is a letter, so that
    /// 0FFh cannot be mistaken for a symbol named FFh.
    /// </summary>
    private static string Hex(uint value, int digits = 0)
    {
        string s = value.ToString("X", CultureInfo.InvariantCulture);
        if (digits > 0) s = s.PadLeft(digits, '0');
        if (s[0] > '9') s = "0" + s;
        return s + "h";
    }

    /// <summary>Renders a signed displacement as "+5" / "-3" for use inside brackets.</summary>
    private static string SignedDisp(long value)
    {
        if (value < 0) return "-" + Hex((uint)(-value));
        return "+" + Hex((uint)value);
    }

    private static string Db(byte b) => "db " + Hex(b, 2);

    private static string RegName(byte index, int size) => size switch
    {
        1 => Reg8[index & 7],
        4 => Reg32[index & 7],
        _ => Reg16[index & 7],
    };

    private static string SizePrefix(int size) => size switch
    {
        1 => "byte ",
        2 => "word ",
        4 => "dword ",
        6 => "fword ",
        _ => "",
    };

    // =========================================================================
    // ModR/M and SIB
    // =========================================================================

    /// <summary>
    /// Reads the ModR/M byte and renders its r/m operand.
    ///
    /// The byte splits into mod(7-6) reg(5-3) rm(2-0): three fields of two, three
    /// and three bits. That layout is why an x86 instruction can name one memory
    /// operand and one register operand in a single byte -- the reg field always
    /// names a register (or, for the group opcodes, an opcode extension), while
    /// mod and rm together describe the other operand, register or memory.
    /// </summary>
    /// <param name="size">Operand size in bytes, used for the "byte/word/dword ptr" hint.</param>
    /// <param name="showSize">
    /// Whether to emit the size hint. It is needed only when the other operand
    /// does not already imply the width, e.g. "mov byte ptr [bx], 1".
    /// </param>
    private static ModRm ReadModRm(ref Context ctx, int size, bool showSize = false)
    {
        byte modrm = ctx.Next();
        byte mod = (byte)(modrm >> 6);
        byte reg = (byte)((modrm >> 3) & 7);
        byte rm = (byte)(modrm & 7);

        if (mod == 3)
        {
            // mod == 3 is the "no memory at all" case: r/m names a register of
            // the same width as the reg operand.
            return new ModRm(mod, reg, rm, RegName(rm, size), true);
        }

        string inner = ctx.AddressSize32
            ? DecodeAddress32(ref ctx, mod, rm)
            : DecodeAddress16(ref ctx, mod, rm);

        // A segment override prefix is printed on the memory operand it applies
        // to, which is where the programmer wrote it conceptually.
        string seg = ctx.Segment is not null ? ctx.Segment + ":" : "";
        string hint = showSize ? SizePrefix(size) + "ptr " : "";
        return new ModRm(mod, reg, rm, hint + seg + "[" + inner + "]", false);
    }

    /// <summary>
    /// Looks at the reg field of the ModR/M byte that is about to be read,
    /// without consuming anything. Group 5 needs this because the size of its
    /// memory operand depends on which member of the group the reg field
    /// selects, and the size has to be known before the operand is rendered.
    /// </summary>
    private static byte PeekModRmReg(in Context ctx) => (byte)((ctx.Peek() >> 3) & 7);

    /// <summary>
    /// 16-bit addressing, inherited straight from the 8086: only seven fixed
    /// combinations of a base (BX or BP) with an index (SI or DI), plus a direct
    /// address. There is no scale and no free choice of register, because the
    /// 8086's address adder was hard-wired to exactly these pairings.
    /// </summary>
    private static string DecodeAddress16(ref Context ctx, byte mod, byte rm)
    {
        string basePart;
        long disp = 0;

        if (mod == 0 && rm == 6)
        {
            // mod == 0 with rm == 6 is NOT [bp]: it is the 16-bit direct
            // address. The encoding space had to steal a slot from somewhere for
            // a plain absolute address, and [bp] with no displacement was the
            // least useful combination to give up -- BP points at a stack frame
            // and is almost always used with an offset. Assemblers that really
            // want [bp] emit mod == 1 with a zero displacement instead.
            return Hex(ctx.NextWord(), 4);
        }

        basePart = Mem16[rm];

        // The 8-bit displacement is sign-extended: 0FFh means -1, not +255.
        // That is what lets one byte reach backwards into a stack frame, which
        // is by far the common case.
        if (mod == 1) disp = (sbyte)ctx.Next();
        else if (mod == 2) disp = (short)ctx.NextWord();

        if (disp == 0 && mod != 0) return basePart + "+0h";
        return disp == 0 ? basePart : basePart + SignedDisp(disp);
    }

    /// <summary>
    /// 32-bit addressing, new with the 386: any general register can be the base,
    /// and an optional SIB byte adds a scaled index.
    /// </summary>
    private static string DecodeAddress32(ref Context ctx, byte mod, byte rm)
    {
        string inner;
        long disp = 0;
        bool haveDisp32Only = false;

        if (rm == 4)
        {
            // rm == 4 does not mean [esp] here. ESP's slot was repurposed as an
            // escape hatch: it announces that a SIB byte follows. ESP as a base
            // is still reachable, just only through that SIB byte.
            inner = DecodeSib(ref ctx, mod, out haveDisp32Only);
        }
        else if (rm == 5 && mod == 0)
        {
            // Same trick as in 16-bit mode, one register along: this combination
            // is the 32-bit direct address rather than [ebp].
            return Hex(ctx.NextDword(), 8);
        }
        else
        {
            inner = Reg32[rm];
        }

        if (mod == 1) disp = (sbyte)ctx.Next();
        else if (mod == 2) disp = (int)ctx.NextDword();

        if (inner.Length == 0)
        {
            // SIB with no base and no index: the displacement is the whole address.
            return Hex((uint)disp, 8);
        }

        if (haveDisp32Only && disp == 0) return inner;
        if (disp == 0) return inner;
        return inner + SignedDisp(disp);
    }

    /// <summary>
    /// The SIB byte: scale(7-6) index(5-3) base(2-0), describing base + index*2^scale.
    /// </summary>
    private static string DecodeSib(ref Context ctx, byte mod, out bool sawDisp32)
    {
        byte sib = ctx.Next();
        byte scale = (byte)(sib >> 6);
        byte index = (byte)((sib >> 3) & 7);
        byte baseReg = (byte)(sib & 7);
        sawDisp32 = false;

        var parts = new StringBuilder();

        // index == 4 encodes "no index register". ESP can never be an index --
        // scaling the stack pointer is meaningless -- so its slot in the index
        // field was free to be repurposed as a flag meaning "absent".
        //
        // On the 386, and only on the 386, the scale field does not go unused
        // when the index is absent: the address unit still applies the shift, but
        // to the BASE register instead. A SIB encoding "base = eax, no index,
        // scale = 4" therefore computes eax * 4, not eax. From the 486 onwards
        // Intel cleaned this up and a scale without an index has no effect at
        // all, which is what every modern reference documents. Assemblers never
        // emit such an encoding, so it only turns up in hand-written or
        // generated code -- but the disassembler has to show what the 386 will
        // actually do, so we render it as [eax*4].
        bool scaleAppliesToBase = index == 4 && scale != 0;

        if (index != 4)
        {
            parts.Append(Reg32[index]);
            if (scale != 0) parts.Append('*').Append(1 << scale);
        }

        if (baseReg == 5 && mod == 0)
        {
            // No base register: a full 32-bit displacement takes its place. This
            // is how [esi*4 + 1234h] with no base is encoded.
            uint d = ctx.NextDword();
            sawDisp32 = true;
            if (parts.Length == 0) return Hex(d, 8);
            if (d != 0) parts.Append(SignedDisp((int)d));
            return parts.ToString();
        }

        if (parts.Length > 0) parts.Insert(0, '+').Insert(0, Reg32[baseReg]);
        else parts.Append(Reg32[baseReg]);

        if (scaleAppliesToBase) parts.Append('*').Append(1 << scale);
        return parts.ToString();
    }

    // =========================================================================
    // operand helpers
    // =========================================================================

    private static int OperandSize(in Context ctx) => ctx.OperandSize32 ? 4 : 2;

    private static string Accumulator(in Context ctx, int size) => size switch
    {
        1 => "al",
        4 => "eax",
        _ => "ax",
    };

    /// <summary>Renders a relative branch target as an absolute address.</summary>
    private static string RelativeTarget(in Context ctx, long delta)
    {
        // The displacement is relative to the address of the NEXT instruction,
        // which is why the whole instruction must already have been consumed
        // before this is called.
        uint target = (uint)(ctx.CurrentAddress + (uint)delta);
        if (!ctx.DefaultIs32) target &= 0xFFFF;   // 16-bit code wraps inside the segment
        return Hex(target, ctx.DefaultIs32 ? 8 : 4);
    }

    private static string Far(uint offset, ushort segment, bool wide)
        => Hex(segment, 4) + ":" + Hex(offset, wide ? 8 : 4);

    /// <summary>Applies the LOCK and REP prefixes to the finished mnemonic.</summary>
    private static string WithPrefixes(in Context ctx, string text)
    {
        if (ctx.Lock) text = "lock " + text;
        if (ctx.Rep is not null) text = ctx.Rep + " " + text;
        return text;
    }

    // =========================================================================
    // main dispatch
    // =========================================================================

    private static string DecodeInstruction(ref Context ctx)
    {
        // --- prefixes ---------------------------------------------------------
        //
        // Prefixes are ordinary bytes prepended to the instruction. The decoder
        // loops over them because any number may appear in any order; only the
        // last of each kind has an effect.
        while (true)
        {
            byte b = ctx.Next();
            switch (b)
            {
                case 0x26: ctx.Segment = "es"; continue;
                case 0x2E: ctx.Segment = "cs"; continue;
                case 0x36: ctx.Segment = "ss"; continue;
                case 0x3E: ctx.Segment = "ds"; continue;
                case 0x64: ctx.Segment = "fs"; continue;
                case 0x65: ctx.Segment = "gs"; continue;

                // 0x66 and 0x67 do not select 32 bits: they toggle away from the
                // segment's default. In a 16-bit segment 66 means "this one is
                // 32-bit"; in a 32-bit segment it means the opposite. That is how
                // the same opcode bytes serve both worlds.
                case 0x66: ctx.OperandSize32 = !ctx.DefaultIs32; continue;
                case 0x67: ctx.AddressSize32 = !ctx.DefaultIs32; continue;

                case 0xF0: ctx.Lock = true; continue;
                case 0xF2: ctx.Rep = "repne"; continue;
                case 0xF3: ctx.Rep = "rep"; continue;

                default:
                    return b == 0x0F
                        ? DecodeTwoByte(ref ctx)
                        : DecodeOneByte(ref ctx, b);
            }
        }
    }

    private static string DecodeOneByte(ref Context ctx, byte op)
    {
        int osize = OperandSize(in ctx);

        // --- 0x00-0x3F: the ALU block ----------------------------------------
        //
        // Eight operations laid out in a regular grid. Bits 5-3 select the
        // operation (add, or, adc, sbb, and, sub, xor, cmp) and bits 2-0 the
        // operand form, so the whole block decodes arithmetically rather than
        // through a table. Only the two odd men out -- the segment
        // push/pop and the BCD adjusts at x6/x7/xF -- break the pattern.
        if (op < 0x40 && (op & 7) < 6)
        {
            string mnem = AluOps[(op >> 3) & 7];
            return DecodeAluForm(ref ctx, mnem, op & 7);
        }

        switch (op)
        {
            // segment push/pop and BCD, wedged into the ALU grid's spare slots
            case 0x06: return "push es";
            case 0x07: return "pop es";
            case 0x0E: return "push cs";
            case 0x16: return "push ss";
            case 0x17: return "pop ss";
            case 0x1E: return "push ds";
            case 0x1F: return "pop ds";
            case 0x27: return "daa";
            case 0x2F: return "das";
            case 0x37: return "aaa";
            case 0x3F: return "aas";

            // --- 0x40-0x5F: register-encoded one-byte forms -------------------
            //
            // The register number lives in the low three bits of the opcode
            // itself, so these need no ModR/M byte at all. That is why
            // push/pop/inc/dec of a register are a single byte -- the 8086's
            // designers spent 32 opcodes to make the most common operations as
            // short as possible.
            case >= 0x40 and <= 0x47: return "inc " + RegName((byte)(op & 7), osize);
            case >= 0x48 and <= 0x4F: return "dec " + RegName((byte)(op & 7), osize);
            case >= 0x50 and <= 0x57: return "push " + RegName((byte)(op & 7), osize);
            case >= 0x58 and <= 0x5F: return "pop " + RegName((byte)(op & 7), osize);

            case 0x60: return ctx.OperandSize32 ? "pushad" : "pusha";
            case 0x61: return ctx.OperandSize32 ? "popad" : "popa";

            case 0x62:
            {
                var m = ReadModRm(ref ctx, osize);
                return "bound " + RegName(m.Reg, osize) + ", " + m.Text;
            }
            case 0x63:
            {
                // ARPL always works on 16-bit selectors, whatever the operand size.
                var m = ReadModRm(ref ctx, 2);
                return "arpl " + m.Text + ", " + Reg16[m.Reg];
            }

            case 0x68:
            {
                uint imm = ctx.NextImmediate();
                return "push " + Hex(imm, ctx.OperandSize32 ? 8 : 4);
            }
            case 0x69:
            {
                var m = ReadModRm(ref ctx, osize);
                uint imm = ctx.NextImmediate();
                return "imul " + RegName(m.Reg, osize) + ", " + m.Text + ", " + Hex(imm);
            }
            case 0x6A:
            {
                // push imm8 sign-extends to the operand size: one byte pushes -1.
                sbyte imm = (sbyte)ctx.Next();
                return "push " + Hex((uint)imm & (ctx.OperandSize32 ? 0xFFFFFFFFu : 0xFFFFu));
            }
            case 0x6B:
            {
                var m = ReadModRm(ref ctx, osize);
                sbyte imm = (sbyte)ctx.Next();
                return "imul " + RegName(m.Reg, osize) + ", " + m.Text + ", " + Hex((uint)imm & (ctx.OperandSize32 ? 0xFFFFFFFFu : 0xFFFFu));
            }

            case 0x6C: return WithPrefixes(in ctx, "insb");
            case 0x6D: return WithPrefixes(in ctx, ctx.OperandSize32 ? "insd" : "insw");
            case 0x6E: return WithPrefixes(in ctx, "outsb");
            case 0x6F: return WithPrefixes(in ctx, ctx.OperandSize32 ? "outsd" : "outsw");

            // --- 0x70-0x7F: short conditional jumps ---------------------------
            //
            // The low nibble is the condition code, which is why there are
            // exactly sixteen of them and why they pair up as condition/negation.
            case >= 0x70 and <= 0x7F:
            {
                sbyte rel = (sbyte)ctx.Next();
                return "j" + Cc[op & 0xF] + " short " + RelativeTarget(in ctx, rel);
            }

            // --- 0x80-0x83: the immediate group -------------------------------
            //
            // One opcode, eight instructions: the reg field of the ModR/M byte is
            // not a register here but an extension of the opcode, selecting which
            // of the eight ALU operations to perform. This "group" trick is how
            // the 8086 fit its instruction set into 256 opcodes.
            case 0x80:
            {
                var m = ReadModRm(ref ctx, 1, showSize: true);
                byte imm = ctx.Next();
                return WithPrefixes(in ctx, AluOps[m.Reg] + " " + m.Text + ", " + Hex(imm, 2));
            }
            case 0x81:
            {
                var m = ReadModRm(ref ctx, osize, showSize: true);
                uint imm = ctx.NextImmediate();
                return WithPrefixes(in ctx, AluOps[m.Reg] + " " + m.Text + ", " + Hex(imm));
            }
            case 0x82:
            case 0x83:
            {
                // 0x83 takes a one-byte immediate and sign-extends it to the
                // operand size: "add ax, -1" costs three bytes instead of four.
                // 0x82 is an undocumented alias of 0x80 that the 386 still honours.
                int size = op == 0x82 ? 1 : osize;
                var m = ReadModRm(ref ctx, size, showSize: true);
                sbyte imm = (sbyte)ctx.Next();
                uint mask = size == 1 ? 0xFFu : ctx.OperandSize32 ? 0xFFFFFFFFu : 0xFFFFu;
                return WithPrefixes(in ctx, AluOps[m.Reg] + " " + m.Text + ", " + Hex((uint)imm & mask));
            }

            case 0x84:
            {
                var m = ReadModRm(ref ctx, 1);
                return "test " + m.Text + ", " + Reg8[m.Reg];
            }
            case 0x85:
            {
                var m = ReadModRm(ref ctx, osize);
                return "test " + m.Text + ", " + RegName(m.Reg, osize);
            }
            case 0x86:
            {
                var m = ReadModRm(ref ctx, 1);
                return WithPrefixes(in ctx, "xchg " + m.Text + ", " + Reg8[m.Reg]);
            }
            case 0x87:
            {
                var m = ReadModRm(ref ctx, osize);
                return WithPrefixes(in ctx, "xchg " + m.Text + ", " + RegName(m.Reg, osize));
            }

            // --- 0x88-0x8B: MOV between register and r/m ----------------------
            //
            // Bit 1 is the direction bit: clear means "r/m <- reg", set means the
            // reverse. Bit 0 is the width bit: clear means 8-bit. Those two bits
            // recur all over the instruction set, which is what makes the ALU
            // block above decodable arithmetically.
            case 0x88:
            {
                var m = ReadModRm(ref ctx, 1);
                return WithPrefixes(in ctx, "mov " + m.Text + ", " + Reg8[m.Reg]);
            }
            case 0x89:
            {
                var m = ReadModRm(ref ctx, osize);
                return WithPrefixes(in ctx, "mov " + m.Text + ", " + RegName(m.Reg, osize));
            }
            case 0x8A:
            {
                var m = ReadModRm(ref ctx, 1);
                return "mov " + Reg8[m.Reg] + ", " + m.Text;
            }
            case 0x8B:
            {
                var m = ReadModRm(ref ctx, osize);
                return "mov " + RegName(m.Reg, osize) + ", " + m.Text;
            }

            case 0x8C:
            {
                // Segment registers are 16 bits, so the r/m operand is 16-bit
                // even in 32-bit code (a register destination is zero-extended).
                var m = ReadModRm(ref ctx, 2);
                return "mov " + m.Text + ", " + SegName(m.Reg);
            }
            case 0x8D:
            {
                // LEA computes the address instead of dereferencing it, so no
                // segment prefix and no size hint are meaningful here.
                string? savedSeg = ctx.Segment;
                ctx.Segment = null;
                var m = ReadModRm(ref ctx, osize);
                ctx.Segment = savedSeg;
                return "lea " + RegName(m.Reg, osize) + ", " + m.Text;
            }
            case 0x8E:
            {
                var m = ReadModRm(ref ctx, 2);
                return "mov " + SegName(m.Reg) + ", " + m.Text;
            }
            case 0x8F:
            {
                var m = ReadModRm(ref ctx, osize, showSize: true);
                return m.Reg == 0 ? "pop " + m.Text : Db(0x8F);
            }

            // --- 0x90-0x97: XCHG with the accumulator -------------------------
            //
            // 0x90 is "xchg ax, ax", which does nothing -- and that is exactly
            // why NOP is one byte on x86: it was never a dedicated instruction,
            // just the degenerate case of this family.
            case 0x90:
                return ctx.Rep is not null ? "pause" : "nop";
            case >= 0x91 and <= 0x97:
                return "xchg " + Accumulator(in ctx, osize) + ", " + RegName((byte)(op & 7), osize);

            case 0x98: return ctx.OperandSize32 ? "cwde" : "cbw";
            case 0x99: return ctx.OperandSize32 ? "cdq" : "cwd";
            case 0x9A:
            {
                uint off = ctx.NextImmediate();
                ushort seg = ctx.NextWord();
                return "call far " + Far(off, seg, ctx.OperandSize32);
            }
            case 0x9B: return "wait";
            case 0x9C: return ctx.OperandSize32 ? "pushfd" : "pushf";
            case 0x9D: return ctx.OperandSize32 ? "popfd" : "popf";
            case 0x9E: return "sahf";
            case 0x9F: return "lahf";

            // --- 0xA0-0xA3: MOV accumulator to/from a direct address ----------
            //
            // The "moffs" forms: the address is a bare displacement with no
            // ModR/M byte, which makes loading a global into AL or AX shorter
            // than the general form. Note the displacement is address-sized, not
            // operand-sized.
            case 0xA0: return "mov al, " + MemDirect(ref ctx);
            case 0xA1: return "mov " + Accumulator(in ctx, osize) + ", " + MemDirect(ref ctx);
            case 0xA2: return "mov " + MemDirect(ref ctx) + ", al";
            case 0xA3: return "mov " + MemDirect(ref ctx) + ", " + Accumulator(in ctx, osize);

            // --- 0xA4-0xAF: the string instructions ---------------------------
            //
            // These take their operands implicitly from SI/DI and step them by
            // the operand size, in the direction given by the DF flag. Combined
            // with a REP prefix they are the 8086's memory block primitives.
            case 0xA4: return WithPrefixes(in ctx, "movsb");
            case 0xA5: return WithPrefixes(in ctx, ctx.OperandSize32 ? "movsd" : "movsw");
            case 0xA6: return WithPrefixes(in ctx, "cmpsb");
            case 0xA7: return WithPrefixes(in ctx, ctx.OperandSize32 ? "cmpsd" : "cmpsw");
            case 0xA8: return "test al, " + Hex(ctx.Next(), 2);
            case 0xA9: return "test " + Accumulator(in ctx, osize) + ", " + Hex(ctx.NextImmediate());
            case 0xAA: return WithPrefixes(in ctx, "stosb");
            case 0xAB: return WithPrefixes(in ctx, ctx.OperandSize32 ? "stosd" : "stosw");
            case 0xAC: return WithPrefixes(in ctx, "lodsb");
            case 0xAD: return WithPrefixes(in ctx, ctx.OperandSize32 ? "lodsd" : "lodsw");
            case 0xAE: return WithPrefixes(in ctx, "scasb");
            case 0xAF: return WithPrefixes(in ctx, ctx.OperandSize32 ? "scasd" : "scasw");

            // --- 0xB0-0xBF: MOV immediate into a register ---------------------
            //
            // Again the register is encoded in the low three bits of the opcode;
            // bit 3 selects 8-bit versus operand-sized. Loading a constant is
            // common enough to deserve sixteen opcodes of its own.
            case >= 0xB0 and <= 0xB7:
                return "mov " + Reg8[op & 7] + ", " + Hex(ctx.Next(), 2);
            case >= 0xB8 and <= 0xBF:
                return "mov " + RegName((byte)(op & 7), osize) + ", " + Hex(ctx.NextImmediate(), ctx.OperandSize32 ? 8 : 4);

            // --- shift groups -------------------------------------------------
            //
            // Another opcode group: the reg field picks one of eight rotates and
            // shifts. C0/C1 take an immediate count and are new with the 186;
            // D0/D1 shift by exactly one, D2/D3 by CL.
            case 0xC0:
            {
                var m = ReadModRm(ref ctx, 1, showSize: true);
                return ShiftOps[m.Reg] + " " + m.Text + ", " + Hex(ctx.Next(), 2);
            }
            case 0xC1:
            {
                var m = ReadModRm(ref ctx, osize, showSize: true);
                return ShiftOps[m.Reg] + " " + m.Text + ", " + Hex(ctx.Next(), 2);
            }

            case 0xC2: return "ret " + Hex(ctx.NextWord(), 4);
            case 0xC3: return "ret";
            case 0xC4:
            {
                // LES/LDS load a far pointer: a 16-bit selector plus an
                // operand-sized offset, hence the "fword" hint in 32-bit code.
                var m = ReadModRm(ref ctx, ctx.OperandSize32 ? 6 : 4, showSize: true);
                return "les " + RegName(m.Reg, osize) + ", " + m.Text;
            }
            case 0xC5:
            {
                var m = ReadModRm(ref ctx, ctx.OperandSize32 ? 6 : 4, showSize: true);
                return "lds " + RegName(m.Reg, osize) + ", " + m.Text;
            }
            case 0xC6:
            {
                var m = ReadModRm(ref ctx, 1, showSize: true);
                byte imm = ctx.Next();
                return m.Reg == 0 ? "mov " + m.Text + ", " + Hex(imm, 2) : Db(0xC6);
            }
            case 0xC7:
            {
                var m = ReadModRm(ref ctx, osize, showSize: true);
                uint imm = ctx.NextImmediate();
                return m.Reg == 0 ? "mov " + m.Text + ", " + Hex(imm) : Db(0xC7);
            }

            case 0xC8:
            {
                // ENTER builds a stack frame: the word is the frame size, the
                // byte the lexical nesting level for Pascal-style display copying.
                ushort size = ctx.NextWord();
                byte level = ctx.Next();
                return "enter " + Hex(size, 4) + ", " + Hex(level, 2);
            }
            case 0xC9: return "leave";
            case 0xCA: return "retf " + Hex(ctx.NextWord(), 4);
            case 0xCB: return "retf";
            case 0xCC: return "int3";
            case 0xCD: return "int " + Hex(ctx.Next(), 2);
            case 0xCE: return "into";
            case 0xCF: return ctx.OperandSize32 ? "iretd" : "iret";

            case 0xD0:
            {
                var m = ReadModRm(ref ctx, 1, showSize: true);
                return ShiftOps[m.Reg] + " " + m.Text + ", 1";
            }
            case 0xD1:
            {
                var m = ReadModRm(ref ctx, osize, showSize: true);
                return ShiftOps[m.Reg] + " " + m.Text + ", 1";
            }
            case 0xD2:
            {
                var m = ReadModRm(ref ctx, 1, showSize: true);
                return ShiftOps[m.Reg] + " " + m.Text + ", cl";
            }
            case 0xD3:
            {
                var m = ReadModRm(ref ctx, osize, showSize: true);
                return ShiftOps[m.Reg] + " " + m.Text + ", cl";
            }

            case 0xD4:
            {
                // AAM is really "divide AL by an arbitrary base"; the base is the
                // second byte, and it is 10 in every sane program. AAM/AAD are
                // the only two-byte instructions whose second byte is an operand
                // rather than an opcode.
                byte b = ctx.Next();
                return b == 0x0A ? "aam" : "aam " + Hex(b, 2);
            }
            case 0xD5:
            {
                byte b = ctx.Next();
                return b == 0x0A ? "aad" : "aad " + Hex(b, 2);
            }
            case 0xD6: return "salc";   // undocumented: AL = CF ? 0FFh : 0
            case 0xD7: return "xlat";

            // 0xD8-0xDF are the x87 coprocessor escapes. The 386 has no FPU on
            // die, and this emulator does not model a 387, so we consume the
            // ModR/M byte to stay in sync with the stream and print a placeholder.
            case >= 0xD8 and <= 0xDF:
            {
                var m = ReadModRm(ref ctx, 4);
                return "esc " + Hex((uint)(op & 7), 2) + ", " + m.Text;
            }

            // --- 0xE0-0xE3: LOOP and JCXZ -------------------------------------
            case 0xE0: { sbyte r = (sbyte)ctx.Next(); return "loopne " + RelativeTarget(in ctx, r); }
            case 0xE1: { sbyte r = (sbyte)ctx.Next(); return "loope " + RelativeTarget(in ctx, r); }
            case 0xE2: { sbyte r = (sbyte)ctx.Next(); return "loop " + RelativeTarget(in ctx, r); }
            case 0xE3:
            {
                // The counter is CX or ECX depending on the ADDRESS size, not the
                // operand size: it is an index register here, not a value.
                sbyte r = (sbyte)ctx.Next();
                return (ctx.AddressSize32 ? "jecxz " : "jcxz ") + RelativeTarget(in ctx, r);
            }

            case 0xE4: return "in al, " + Hex(ctx.Next(), 2);
            case 0xE5: return "in " + Accumulator(in ctx, osize) + ", " + Hex(ctx.Next(), 2);
            case 0xE6: return "out " + Hex(ctx.Next(), 2) + ", al";
            case 0xE7: return "out " + Hex(ctx.Next(), 2) + ", " + Accumulator(in ctx, osize);

            case 0xE8:
            {
                // A near call's displacement is operand-sized, so a 32-bit call
                // reaches the whole address space while a 16-bit one wraps.
                long rel = ctx.OperandSize32 ? (int)ctx.NextDword() : (short)ctx.NextWord();
                return "call " + RelativeTarget(in ctx, rel);
            }
            case 0xE9:
            {
                long rel = ctx.OperandSize32 ? (int)ctx.NextDword() : (short)ctx.NextWord();
                return "jmp " + RelativeTarget(in ctx, rel);
            }
            case 0xEA:
            {
                uint off = ctx.NextImmediate();
                ushort seg = ctx.NextWord();
                return "jmp far " + Far(off, seg, ctx.OperandSize32);
            }
            case 0xEB:
            {
                sbyte rel = (sbyte)ctx.Next();
                return "jmp short " + RelativeTarget(in ctx, rel);
            }

            case 0xEC: return "in al, dx";
            case 0xED: return "in " + Accumulator(in ctx, osize) + ", dx";
            case 0xEE: return "out dx, al";
            case 0xEF: return "out dx, " + Accumulator(in ctx, osize);

            case 0xF1: return "int1";   // ICEBP, the in-circuit-emulator breakpoint
            case 0xF4: return "hlt";
            case 0xF5: return "cmc";

            // --- 0xF6/0xF7: the unary group -----------------------------------
            //
            // TEST, NOT, NEG, MUL, IMUL, DIV, IDIV all share one opcode, again
            // selected by the reg field. TEST is the odd one out because it
            // carries an immediate the others do not have -- the decoder has to
            // special-case it when computing the instruction length.
            case 0xF6:
            case 0xF7:
                return DecodeGroup3(ref ctx, op == 0xF7 ? osize : 1);

            case 0xF8: return "clc";
            case 0xF9: return "stc";
            case 0xFA: return "cli";
            case 0xFB: return "sti";
            case 0xFC: return "cld";
            case 0xFD: return "std";

            case 0xFE: return DecodeGroup4(ref ctx);
            case 0xFF: return DecodeGroup5(ref ctx, osize);

            default: return Db(op);
        }
    }

    private static string SegName(byte index) => index < 6 ? SegRegs[index] : "?";

    /// <summary>Renders the bare-displacement memory operand used by the moffs MOVs.</summary>
    private static string MemDirect(ref Context ctx)
    {
        uint addr = ctx.NextAddressImmediate();
        string seg = ctx.Segment is not null ? ctx.Segment + ":" : "";
        return seg + "[" + Hex(addr, ctx.AddressSize32 ? 8 : 4) + "]";
    }

    /// <summary>
    /// The six regular forms an ALU opcode can take, selected by the low three
    /// bits of the opcode: r/m8,r8 / r/m,r / r8,r/m8 / r,r/m / al,imm8 / acc,imm.
    /// </summary>
    private static string DecodeAluForm(ref Context ctx, string mnem, int form)
    {
        int osize = OperandSize(in ctx);
        switch (form)
        {
            case 0:
            {
                var m = ReadModRm(ref ctx, 1);
                return WithPrefixes(in ctx, mnem + " " + m.Text + ", " + Reg8[m.Reg]);
            }
            case 1:
            {
                var m = ReadModRm(ref ctx, osize);
                return WithPrefixes(in ctx, mnem + " " + m.Text + ", " + RegName(m.Reg, osize));
            }
            case 2:
            {
                var m = ReadModRm(ref ctx, 1);
                return mnem + " " + Reg8[m.Reg] + ", " + m.Text;
            }
            case 3:
            {
                var m = ReadModRm(ref ctx, osize);
                return mnem + " " + RegName(m.Reg, osize) + ", " + m.Text;
            }
            case 4:
                return mnem + " al, " + Hex(ctx.Next(), 2);
            default:
                return mnem + " " + Accumulator(in ctx, osize) + ", " + Hex(ctx.NextImmediate());
        }
    }

    /// <summary>Group 3: the F6/F7 unary operations.</summary>
    private static string DecodeGroup3(ref Context ctx, int size)
    {
        var m = ReadModRm(ref ctx, size, showSize: true);
        switch (m.Reg)
        {
            case 0:
            case 1:   // reg == 1 is an undocumented alias of TEST
            {
                uint imm = size == 1 ? ctx.Next() : ctx.NextImmediate();
                return "test " + m.Text + ", " + (size == 1 ? Hex(imm, 2) : Hex(imm));
            }
            case 2: return WithPrefixes(in ctx, "not " + m.Text);
            case 3: return WithPrefixes(in ctx, "neg " + m.Text);
            case 4: return "mul " + m.Text;
            case 5: return "imul " + m.Text;
            case 6: return "div " + m.Text;
            default: return "idiv " + m.Text;
        }
    }

    /// <summary>Group 4: FE, the 8-bit INC/DEC on an r/m operand.</summary>
    private static string DecodeGroup4(ref Context ctx)
    {
        var m = ReadModRm(ref ctx, 1, showSize: true);
        return m.Reg switch
        {
            0 => WithPrefixes(in ctx, "inc " + m.Text),
            1 => WithPrefixes(in ctx, "dec " + m.Text),
            _ => Db(0xFE),
        };
    }

    /// <summary>
    /// Group 5: FF, a grab-bag of INC/DEC/CALL/JMP/PUSH on an r/m operand. The
    /// far forms (reg 3 and 5) require a memory operand, since a register cannot
    /// hold a selector:offset pair.
    /// </summary>
    private static string DecodeGroup5(ref Context ctx, int osize)
    {
        // Peek at the reg field before consuming the ModR/M byte: the far forms
        // (reg 3 and 5) address a selector:offset pair, which is 4 bytes in
        // 16-bit code and 6 in 32-bit -- not the plain operand size the near
        // forms use.
        byte peek = PeekModRmReg(in ctx);
        bool far = peek == 3 || peek == 5;
        int size = far ? (ctx.OperandSize32 ? 6 : 4) : osize;
        var m = ReadModRm(ref ctx, size, showSize: true);
        return m.Reg switch
        {
            0 => WithPrefixes(in ctx, "inc " + m.Text),
            1 => WithPrefixes(in ctx, "dec " + m.Text),
            2 => "call " + m.Text,
            3 => "call far " + m.Text,
            4 => "jmp " + m.Text,
            5 => "jmp far " + m.Text,
            6 => "push " + m.Text,
            _ => Db(0xFF),
        };
    }

    // =========================================================================
    // two-byte opcodes (the 0x0F escape)
    // =========================================================================

    /// <summary>
    /// The 8086 used up its 256 opcodes, so the 286 and 386 reached for a second
    /// byte. 0x0F was a free slot ("POP CS", which nobody could use sensibly) and
    /// became the escape prefix that still carries every extension Intel has
    /// added since.
    /// </summary>
    private static string DecodeTwoByte(ref Context ctx)
    {
        byte op = ctx.Next();
        int osize = OperandSize(in ctx);

        switch (op)
        {
            case 0x00: return DecodeGroup6(ref ctx);
            case 0x01: return DecodeGroup7(ref ctx);

            case 0x02:
            {
                var m = ReadModRm(ref ctx, 2);
                return "lar " + RegName(m.Reg, osize) + ", " + m.Text;
            }
            case 0x03:
            {
                var m = ReadModRm(ref ctx, 2);
                return "lsl " + RegName(m.Reg, osize) + ", " + m.Text;
            }
            case 0x06: return "clts";
            case 0x08: return "invd";
            case 0x09: return "wbinvd";

            // --- 0x20-0x24: the control, debug and test registers -------------
            //
            // These always move a full 32 bits regardless of the operand size
            // prefix -- CR0 and DR7 have no 16-bit form -- and the mod field is
            // ignored: the operand is always a register.
            case 0x20:
            {
                var m = ReadModRm(ref ctx, 4);
                return "mov " + Reg32[m.Rm] + ", cr" + m.Reg;
            }
            case 0x21:
            {
                var m = ReadModRm(ref ctx, 4);
                return "mov " + Reg32[m.Rm] + ", dr" + m.Reg;
            }
            case 0x22:
            {
                var m = ReadModRm(ref ctx, 4);
                return "mov cr" + m.Reg + ", " + Reg32[m.Rm];
            }
            case 0x23:
            {
                var m = ReadModRm(ref ctx, 4);
                return "mov dr" + m.Reg + ", " + Reg32[m.Rm];
            }
            case 0x24:
            {
                var m = ReadModRm(ref ctx, 4);
                return "mov " + Reg32[m.Rm] + ", tr" + m.Reg;
            }
            case 0x26:
            {
                var m = ReadModRm(ref ctx, 4);
                return "mov tr" + m.Reg + ", " + Reg32[m.Rm];
            }

            // --- 0x80-0x8F: the long conditional jumps ------------------------
            //
            // The 8086 could only branch +/-128 bytes; the 386 added these
            // full-range forms, keeping the same condition encoding in the low
            // nibble so the mnemonics line up with 0x70-0x7F.
            case >= 0x80 and <= 0x8F:
            {
                long rel = ctx.OperandSize32 ? (int)ctx.NextDword() : (short)ctx.NextWord();
                return "j" + Cc[op & 0xF] + " " + RelativeTarget(in ctx, rel);
            }

            // --- 0x90-0x9F: SETcc ---------------------------------------------
            //
            // Materialises a condition as a 0 or 1 byte, which turns a branch
            // into straight-line code. Always 8-bit, whatever the operand size.
            case >= 0x90 and <= 0x9F:
            {
                var m = ReadModRm(ref ctx, 1, showSize: true);
                return "set" + Cc[op & 0xF] + " " + m.Text;
            }

            case 0xA0: return "push fs";
            case 0xA1: return "pop fs";
            case 0xA2: return "cpuid";

            // --- the bit-test family ------------------------------------------
            //
            // BT/BTS/BTR/BTC address a single bit. With a memory operand the bit
            // index is not masked to the operand size: it selects a bit in a
            // notional bit string of unbounded length, which is why these are the
            // natural primitives for bitmap allocators.
            case 0xA3:
            {
                var m = ReadModRm(ref ctx, osize);
                return WithPrefixes(in ctx, "bt " + m.Text + ", " + RegName(m.Reg, osize));
            }
            case 0xAB:
            {
                var m = ReadModRm(ref ctx, osize);
                return WithPrefixes(in ctx, "bts " + m.Text + ", " + RegName(m.Reg, osize));
            }
            case 0xB3:
            {
                var m = ReadModRm(ref ctx, osize);
                return WithPrefixes(in ctx, "btr " + m.Text + ", " + RegName(m.Reg, osize));
            }
            case 0xBB:
            {
                var m = ReadModRm(ref ctx, osize);
                return WithPrefixes(in ctx, "btc " + m.Text + ", " + RegName(m.Reg, osize));
            }
            case 0xBA:
            {
                // Group 8: the immediate forms of the bit-test family.
                var m = ReadModRm(ref ctx, osize, showSize: true);
                byte imm = ctx.Next();
                string mnem = m.Reg switch
                {
                    4 => "bt", 5 => "bts", 6 => "btr", 7 => "btc", _ => "",
                };
                if (mnem.Length == 0) return Db(0x0F);
                return WithPrefixes(in ctx, mnem + " " + m.Text + ", " + Hex(imm, 2));
            }

            // --- SHLD/SHRD: double-precision shifts ---------------------------
            //
            // Shift the destination while feeding in bits from the source, which
            // is how you shift a value wider than one register.
            case 0xA4:
            {
                var m = ReadModRm(ref ctx, osize);
                byte imm = ctx.Next();
                return "shld " + m.Text + ", " + RegName(m.Reg, osize) + ", " + Hex(imm, 2);
            }
            case 0xA5:
            {
                var m = ReadModRm(ref ctx, osize);
                return "shld " + m.Text + ", " + RegName(m.Reg, osize) + ", cl";
            }
            case 0xAC:
            {
                var m = ReadModRm(ref ctx, osize);
                byte imm = ctx.Next();
                return "shrd " + m.Text + ", " + RegName(m.Reg, osize) + ", " + Hex(imm, 2);
            }
            case 0xAD:
            {
                var m = ReadModRm(ref ctx, osize);
                return "shrd " + m.Text + ", " + RegName(m.Reg, osize) + ", cl";
            }

            case 0xA8: return "push gs";
            case 0xA9: return "pop gs";

            case 0xAF:
            {
                // The two-operand IMUL, new with the 386: unlike the F7 form it
                // keeps only the low half of the product, in the destination.
                var m = ReadModRm(ref ctx, osize);
                return "imul " + RegName(m.Reg, osize) + ", " + m.Text;
            }

            case 0xB0:
            {
                var m = ReadModRm(ref ctx, 1);
                return WithPrefixes(in ctx, "cmpxchg " + m.Text + ", " + Reg8[m.Reg]);
            }
            case 0xB1:
            {
                var m = ReadModRm(ref ctx, osize);
                return WithPrefixes(in ctx, "cmpxchg " + m.Text + ", " + RegName(m.Reg, osize));
            }

            // --- LSS/LFS/LGS: load a far pointer into a segment register ------
            case 0xB2:
            {
                var m = ReadModRm(ref ctx, ctx.OperandSize32 ? 6 : 4, showSize: true);
                return "lss " + RegName(m.Reg, osize) + ", " + m.Text;
            }
            case 0xB4:
            {
                var m = ReadModRm(ref ctx, ctx.OperandSize32 ? 6 : 4, showSize: true);
                return "lfs " + RegName(m.Reg, osize) + ", " + m.Text;
            }
            case 0xB5:
            {
                var m = ReadModRm(ref ctx, ctx.OperandSize32 ? 6 : 4, showSize: true);
                return "lgs " + RegName(m.Reg, osize) + ", " + m.Text;
            }

            // --- MOVZX/MOVSX: widening moves ----------------------------------
            //
            // Before the 386 you widened a byte by zeroing AH by hand or by CBW.
            // These do it in one instruction, from any register to any register,
            // which is why the source is 8 or 16 bits while the destination is
            // whatever the operand size says.
            case 0xB6:
            {
                var m = ReadModRm(ref ctx, 1, showSize: true);
                return "movzx " + RegName(m.Reg, osize) + ", " + m.Text;
            }
            case 0xB7:
            {
                var m = ReadModRm(ref ctx, 2, showSize: true);
                return "movzx " + RegName(m.Reg, osize) + ", " + m.Text;
            }
            case 0xBE:
            {
                var m = ReadModRm(ref ctx, 1, showSize: true);
                return "movsx " + RegName(m.Reg, osize) + ", " + m.Text;
            }
            case 0xBF:
            {
                var m = ReadModRm(ref ctx, 2, showSize: true);
                return "movsx " + RegName(m.Reg, osize) + ", " + m.Text;
            }

            // --- BSF/BSR: bit scan --------------------------------------------
            case 0xBC:
            {
                var m = ReadModRm(ref ctx, osize);
                return "bsf " + RegName(m.Reg, osize) + ", " + m.Text;
            }
            case 0xBD:
            {
                var m = ReadModRm(ref ctx, osize);
                return "bsr " + RegName(m.Reg, osize) + ", " + m.Text;
            }

            case 0xC0:
            {
                var m = ReadModRm(ref ctx, 1);
                return WithPrefixes(in ctx, "xadd " + m.Text + ", " + Reg8[m.Reg]);
            }
            case 0xC1:
            {
                var m = ReadModRm(ref ctx, osize);
                return WithPrefixes(in ctx, "xadd " + m.Text + ", " + RegName(m.Reg, osize));
            }

            case 0xC8: case 0xC9: case 0xCA: case 0xCB:
            case 0xCC: case 0xCD: case 0xCE: case 0xCF:
                return "bswap " + Reg32[op & 7];

            case 0xFF: return "ud0";
            case 0x0B: return "ud2";

            default:
                // Unknown two-byte opcode: rewind to the 0Fh escape byte and emit
                // it alone, so the caller advances by exactly one byte and can
                // re-synchronise on the next. Reporting length 2 here would make
                // the debugger skip a byte that may well start a real instruction.
                ctx.Reset();
                return Db(ctx.Next());
        }
    }

    /// <summary>
    /// Group 6 (0F 00): the segment-descriptor instructions. All operate on
    /// 16-bit selectors.
    /// </summary>
    private static string DecodeGroup6(ref Context ctx)
    {
        var m = ReadModRm(ref ctx, 2, showSize: true);
        return m.Reg switch
        {
            0 => "sldt " + m.Text,
            1 => "str " + m.Text,
            2 => "lldt " + m.Text,
            3 => "ltr " + m.Text,
            4 => "verr " + m.Text,
            5 => "verw " + m.Text,
            _ => Db(0x0F),
        };
    }

    /// <summary>
    /// Group 7 (0F 01): the system-table instructions. GDT and IDT are six-byte
    /// pseudo-descriptors (a 16-bit limit followed by a 32-bit base), which is
    /// why the operand is an "fword"; SMSW/LMSW are the 286-compatible window
    /// onto the low half of CR0.
    /// </summary>
    private static string DecodeGroup7(ref Context ctx)
    {
        var m = ReadModRm(ref ctx, 6, showSize: true);
        return m.Reg switch
        {
            0 => "sgdt " + m.Text,
            1 => "sidt " + m.Text,
            2 => "lgdt " + m.Text,
            3 => "lidt " + m.Text,
            4 => "smsw " + m.Text,
            6 => "lmsw " + m.Text,
            7 => "invlpg " + m.Text,
            _ => Db(0x0F),
        };
    }
}
