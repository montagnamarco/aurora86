using CpuNs = Aurora386.Core.Cpu;

namespace Aurora386.Core.Firmware.Fpu;

/// <summary>
/// A software floating-point unit, reached through interrupt 7.
///
/// A 386 without an 80387 fitted has CR0's emulation bit set, and every
/// floating-point instruction then faults to vector 7 instead of being
/// executed. That is not a failure: it is the hook a coprocessor emulator
/// hangs from, and how machines sold without the chip ran software that
/// needed one. Windows shipped exactly such a library.
///
/// The awkward part of the arrangement -- and the reason it is worth writing
/// down -- is that the fault reports only where the instruction is, never
/// what it was. Everything else has to be recovered by reading the guest's
/// own memory back and decoding the instruction a second time: prefixes,
/// ModRM, the effective address, the operand size. The processor has already
/// done all of that once and thrown it away.
///
/// The registers are a stack of eight, addressed relative to a top-of-stack
/// pointer held in the status word, which is why the same "ST(1)" names a
/// different register after a push. Values are kept here as C# doubles
/// rather than the 387's 80-bit extended format: the wider format only shows
/// through in the last few bits of a long chain of operations, and nothing
/// DOS or Windows does depends on it.
/// </summary>
public sealed class FpuEmulator
{
    private readonly Extended80[] _stack = new Extended80[8];

    /// <summary>Which stack slots hold a value; the 387 tracks this in its tag word.</summary>
    private readonly bool[] _used = new bool[8];

    /// <summary>Top of stack: ST(0) is _stack[_top], ST(1) is _stack[_top + 1], and so on.</summary>
    private int _top;

    private ushort _control = 0x037F;   // the 387's state after FINIT
    private ushort _status;

    /// <summary>The three condition-code bits the compare instructions set.</summary>
    private const ushort C0 = 1 << 8;
    private const ushort C2 = 1 << 10;
    private const ushort C3 = 1 << 14;

    /// <summary>How many instructions were emulated; the tests read it.</summary>
    public long Emulated { get; private set; }

    /// <summary>
    /// Handles one faulted floating-point instruction.
    ///
    /// On entry the stack holds the frame interrupt 7 pushed -- IP, CS and
    /// flags -- and the return address in it points at the instruction that
    /// faulted, not past it. Emulating the instruction therefore means
    /// rewriting that return address to step over it, or the same
    /// instruction faults again the moment the handler returns and the
    /// machine makes no progress at all.
    /// </summary>
    public void Handle(CpuNs.Cpu cpu)
    {
        // The frame sits at SS:SP: offset 0 is IP, 2 is CS, 4 is FLAGS.
        uint sp = cpu.State.Sp;
        ushort ip = cpu.ReadMemoryWord(CpuNs.SegReg.Ss, sp);
        ushort cs = cpu.ReadMemoryWord(CpuNs.SegReg.Ss, sp + 2);

        int length = Emulate(cpu, cs, ip);

        // Step the saved IP over what was just emulated.
        cpu.WriteMemoryWord(CpuNs.SegReg.Ss, sp, (ushort)(ip + length));
        Emulated++;
    }

    /// <summary>
    /// Decodes and performs the instruction at cs:ip, and says how many bytes
    /// long it was.
    /// </summary>
    private int Emulate(CpuNs.Cpu cpu, ushort cs, ushort ip)
    {
        uint linear = (uint)(cs << 4);
        int at = 0;

        // Prefixes come first. Only the segment overrides change what the
        // instruction means here; the rest are consumed so the length comes
        // out right.
        CpuNs.SegReg? overridden = null;
        while (true)
        {
            byte prefix = cpu.Memory.ReadByte(linear + (uint)(ushort)(ip + at));
            switch (prefix)
            {
                case 0x26: overridden = CpuNs.SegReg.Es; at++; continue;
                case 0x2E: overridden = CpuNs.SegReg.Cs; at++; continue;
                case 0x36: overridden = CpuNs.SegReg.Ss; at++; continue;
                case 0x3E: overridden = CpuNs.SegReg.Ds; at++; continue;
                case 0x64: overridden = CpuNs.SegReg.Fs; at++; continue;
                case 0x65: overridden = CpuNs.SegReg.Gs; at++; continue;
                case 0x66:
                case 0x67:
                case 0xF0:
                case 0xF2:
                case 0xF3: at++; continue;
            }
            break;
        }

        byte opcode = cpu.Memory.ReadByte(linear + (uint)(ushort)(ip + at));
        byte modrm = cpu.Memory.ReadByte(linear + (uint)(ushort)(ip + at + 1));
        at += 2;

        int mod = modrm >> 6;
        int reg = (modrm >> 3) & 7;
        int rm = modrm & 7;

        // A ModRM with mod == 3 names a stack register; anything else is a
        // memory operand whose address has to be worked out here.
        if (mod == 3)
        {
            Execute(opcode, reg, rm, cpu, hasMemory: false, address: 0, segment: CpuNs.SegReg.Ds);
            return at;
        }

        (uint offset, CpuNs.SegReg segment, int extra) = Address16(cpu, linear, ip, at, mod, rm);
        at += extra;

        Execute(opcode, reg, rm, cpu, hasMemory: true,
                address: offset, segment: overridden ?? segment);
        return at;
    }

    /// <summary>
    /// The 16-bit addressing forms. DOS-era code is all 16-bit, and a 387
    /// operand reached from a 32-bit segment would need the other table; that
    /// is a gap, not a simplification, and it is why 32-bit code is refused
    /// rather than mis-addressed.
    /// </summary>
    private static (uint Offset, CpuNs.SegReg Segment, int Extra) Address16(
        CpuNs.Cpu cpu, uint linear, ushort ip, int at, int mod, int rm)
    {
        ushort bx = cpu.State.Bx, bp = cpu.State.Bp;
        ushort si = cpu.State.Si, di = cpu.State.Di;

        // BP-relative forms address the stack; everything else addresses data.
        (int baseValue, CpuNs.SegReg segment) = rm switch
        {
            0 => (bx + si, CpuNs.SegReg.Ds),
            1 => (bx + di, CpuNs.SegReg.Ds),
            2 => (bp + si, CpuNs.SegReg.Ss),
            3 => (bp + di, CpuNs.SegReg.Ss),
            4 => (si, CpuNs.SegReg.Ds),
            5 => (di, CpuNs.SegReg.Ds),
            6 => (bp, CpuNs.SegReg.Ss),
            _ => (bx, CpuNs.SegReg.Ds),
        };

        // mod 0 with rm 6 is not BP at all: it is a bare 16-bit address.
        if (mod == 0 && rm == 6)
        {
            ushort direct = (ushort)(cpu.Memory.ReadByte(linear + (uint)(ushort)(ip + at))
                          | (cpu.Memory.ReadByte(linear + (uint)(ushort)(ip + at + 1)) << 8));
            return (direct, CpuNs.SegReg.Ds, 2);
        }

        int displacement = 0, extra = 0;
        if (mod == 1)
        {
            displacement = (sbyte)cpu.Memory.ReadByte(linear + (uint)(ushort)(ip + at));
            extra = 1;
        }
        else if (mod == 2)
        {
            displacement = (short)(cpu.Memory.ReadByte(linear + (uint)(ushort)(ip + at))
                         | (cpu.Memory.ReadByte(linear + (uint)(ushort)(ip + at + 1)) << 8));
            extra = 2;
        }

        return ((uint)(ushort)(baseValue + displacement), segment, extra);
    }

    // ---- the stack ---------------------------------------------------------
    //
    // ST(i) counts down from the top, wrapping: the register file is a ring of
    // eight and the top pointer moves, so pushing does not shuffle any values.

    private Extended80 Get(int i) => _stack[(_top + i) & 7];
    private void Set(int i, Extended80 value) => _stack[(_top + i) & 7] = value;

    /// <summary>
    /// The transcendentals are computed in double and put back; only the four
    /// basic operations carry the full width, which is where a long chain of
    /// arithmetic would otherwise drift.
    /// </summary>
    private void Set(int i, double value) => Set(i, Extended80.FromDouble(value));

    private void Push(Extended80 value)
    {
        _top = (_top - 1) & 7;
        _stack[_top] = value;
        _used[_top] = true;
    }

    private void Push(double value) => Push(Extended80.FromDouble(value));

    private Extended80 Pop()
    {
        Extended80 value = _stack[_top];
        _used[_top] = false;
        _top = (_top + 1) & 7;
        return value;
    }

    // ---- memory operands ---------------------------------------------------

    private static double ReadSingle(CpuNs.Cpu cpu, CpuNs.SegReg segment, uint address)
    {
        uint bits = cpu.ReadMemoryWord(segment, address)
                  | ((uint)cpu.ReadMemoryWord(segment, address + 2) << 16);
        return BitConverter.Int32BitsToSingle(unchecked((int)bits));
    }

    private static void WriteSingle(CpuNs.Cpu cpu, CpuNs.SegReg segment, uint address, double value)
    {
        uint bits = unchecked((uint)BitConverter.SingleToInt32Bits((float)value));
        cpu.WriteMemoryWord(segment, address, (ushort)bits);
        cpu.WriteMemoryWord(segment, address + 2, (ushort)(bits >> 16));
    }

    /// <summary>
    /// Reads the 387's own 80-bit format: a 15-bit exponent, a sign, and a
    /// 64-bit significand whose top bit is written out explicitly rather than
    /// being implied as it is in the shorter formats. This is the format the
    /// registers themselves use, so it is what FLD and FSTP of a "long double"
    /// move about, and what a compiler's spill code writes.
    ///
    /// Nothing is narrowed here: the value goes straight into a register at
    /// full width, which is the whole point of the format existing.
    /// </summary>
    private static Extended80 ReadExtended(CpuNs.Cpu cpu, CpuNs.SegReg segment, uint address)
    {
        ulong significand = 0;
        for (int i = 0; i < 4; i++)
            significand |= (ulong)cpu.ReadMemoryWord(segment, address + (uint)(i * 2)) << (i * 16);

        return Extended80.FromBits(significand, cpu.ReadMemoryWord(segment, address + 8));
    }

    /// <summary>Writes a register back out in the 80-bit format, whole.</summary>
    private static void WriteExtended(CpuNs.Cpu cpu, CpuNs.SegReg segment, uint address,
                                      Extended80 value)
    {
        (ulong significand, ushort signAndExponent) = value.ToBits();

        for (int i = 0; i < 4; i++)
            cpu.WriteMemoryWord(segment, address + (uint)(i * 2), (ushort)(significand >> (i * 16)));
        cpu.WriteMemoryWord(segment, address + 8, signAndExponent);
    }

    private static double ReadDouble(CpuNs.Cpu cpu, CpuNs.SegReg segment, uint address)
    {
        ulong bits = 0;
        for (int i = 0; i < 4; i++)
            bits |= (ulong)cpu.ReadMemoryWord(segment, address + (uint)(i * 2)) << (i * 16);
        return BitConverter.Int64BitsToDouble(unchecked((long)bits));
    }

    private static void WriteDouble(CpuNs.Cpu cpu, CpuNs.SegReg segment, uint address, double value)
    {
        ulong bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        for (int i = 0; i < 4; i++)
            cpu.WriteMemoryWord(segment, address + (uint)(i * 2), (ushort)(bits >> (i * 16)));
    }

    /// <summary>
    /// Rounds the way the control word says to. The 387 rounds to nearest by
    /// default, but a compiler emitting an integer store sets truncation
    /// first, and getting this wrong shifts results by one.
    /// </summary>
    private long Round(double value) => ((_control >> 10) & 3) switch
    {
        1 => (long)Math.Floor(value),
        2 => (long)Math.Ceiling(value),
        3 => (long)Math.Truncate(value),
        _ => (long)Math.Round(value, MidpointRounding.ToEven),
    };

    /// <summary>
    /// Sets the condition codes after a comparison. The 387 reports the three
    /// outcomes in C3/C2/C0 rather than in the processor's own flags, and
    /// "unordered" -- either operand a NaN -- is a fourth answer with all
    /// three bits set.
    /// </summary>
    private void Compare(Extended80 a, Extended80 b)
    {
        _status &= unchecked((ushort)~(C0 | C2 | C3));

        switch (Extended80.Compare(a, b))
        {
            case 2: _status |= C0 | C2 | C3; return;   // unordered: a NaN
            case 0: _status |= C3; return;             // equal
            case -1: _status |= C0; return;            // less than
            default: return;                           // greater: all clear
        }
    }

    private void Compare(Extended80 a, double b) => Compare(a, Extended80.FromDouble(b));

    /// <summary>
    /// Performs one decoded instruction.
    ///
    /// The 387's encoding splits the work across the escape opcode and the
    /// ModRM byte: with a memory operand the reg field selects the operation
    /// and the escape says what size the operand is, while with mod == 3 the
    /// two together name the operation and rm picks a stack register. The two
    /// halves are therefore kept apart below rather than merged into one
    /// table, which would hide which field means what.
    /// </summary>
    private void Execute(byte opcode, int reg, int rm, CpuNs.Cpu cpu,
                         bool hasMemory, uint address, CpuNs.SegReg segment)
    {
        if (hasMemory)
        {
            switch (opcode)
            {
                // D8: the arithmetic group against a 32-bit float in memory.
                case 0xD8:
                {
                    double operand = ReadSingle(cpu, segment, address);
                    Arithmetic(reg, operand);
                    return;
                }

                // DC: the same group against a 64-bit double.
                case 0xDC:
                {
                    double operand = ReadDouble(cpu, segment, address);
                    Arithmetic(reg, operand);
                    return;
                }

                // D9: load and store 32-bit, and the control word.
                case 0xD9:
                    switch (reg)
                    {
                        case 0: Push(ReadSingle(cpu, segment, address)); return;
                        case 2: WriteSingle(cpu, segment, address, Get(0).ToDouble()); return;
                        case 3: WriteSingle(cpu, segment, address, Pop().ToDouble()); return;
                        case 5: _control = cpu.ReadMemoryWord(segment, address); return;   // FLDCW
                        case 7: cpu.WriteMemoryWord(segment, address, _control); return;   // FSTCW
                    }
                    return;

                // DD: load and store 64-bit, and the status word.
                case 0xDD:
                    switch (reg)
                    {
                        case 0: Push(ReadDouble(cpu, segment, address)); return;
                        case 2: WriteDouble(cpu, segment, address, Get(0).ToDouble()); return;
                        case 3: WriteDouble(cpu, segment, address, Pop().ToDouble()); return;
                        case 7: cpu.WriteMemoryWord(segment, address, StatusWord); return; // FSTSW
                    }
                    return;

                // DB: 32-bit integers.
                case 0xDB:
                    switch (reg)
                    {
                        case 0:   // FILD
                        {
                            int value = cpu.ReadMemoryWord(segment, address)
                                      | (cpu.ReadMemoryWord(segment, address + 2) << 16);
                            Push(value);
                            return;
                        }
                        case 2:   // FIST
                        case 3:   // FISTP
                        {
                            long value = Round(Get(0).ToDouble());
                            cpu.WriteMemoryWord(segment, address, (ushort)value);
                            cpu.WriteMemoryWord(segment, address + 2, (ushort)(value >> 16));
                            if (reg == 3) Pop();
                            return;
                        }

                        // The 80-bit format: this is what a "long double" is,
                        // and what a compiler spills a register to when it
                        // needs the full width preserved.
                        case 5: Push(ReadExtended(cpu, segment, address)); return;   // FLD
                        case 7: WriteExtended(cpu, segment, address, Pop()); return; // FSTP
                    }
                    return;

                // DF: 16-bit integers, and the 64-bit one that only DF carries.
                case 0xDF:
                    switch (reg)
                    {
                        case 0: Push((short)cpu.ReadMemoryWord(segment, address)); return;
                        case 2:
                        case 3:
                        {
                            long value = Round(Get(0).ToDouble());
                            cpu.WriteMemoryWord(segment, address, (ushort)value);
                            if (reg == 3) Pop();
                            return;
                        }
                        case 5:   // FILD qword
                        {
                            long value = 0;
                            for (int i = 0; i < 4; i++)
                                value |= (long)cpu.ReadMemoryWord(segment, address + (uint)(i * 2)) << (i * 16);
                            Push(value);
                            return;
                        }
                        case 7:   // FISTP qword
                        {
                            long value = Round(Pop().ToDouble());
                            for (int i = 0; i < 4; i++)
                                cpu.WriteMemoryWord(segment, address + (uint)(i * 2), (ushort)(value >> (i * 16)));
                            return;
                        }
                    }
                    return;

                // DA and DE: the arithmetic group against an integer operand.
                case 0xDA:
                {
                    int value = cpu.ReadMemoryWord(segment, address)
                              | (cpu.ReadMemoryWord(segment, address + 2) << 16);
                    Arithmetic(reg, value);
                    return;
                }

                case 0xDE:
                    Arithmetic(reg, (short)cpu.ReadMemoryWord(segment, address));
                    return;
            }

            return;
        }

        // ---- mod == 3: the operand is a stack register --------------------
        switch (opcode)
        {
            case 0xD8: Arithmetic(reg, Get(rm)); return;

            case 0xDC:
                // The same operations, but the result goes to ST(i) rather
                // than to ST(0), and the subtract and divide directions are
                // swapped relative to D8.
                Set(rm, Combine(reg, Get(rm), Get(0), reversed: true));
                return;

            case 0xDE:
                // As DC, and then pops. FCOMPP is the exception: reg 3 with
                // rm 1 compares and pops twice.
                if (reg == 3 && rm == 1) { Compare(Get(0), Get(1)); Pop(); Pop(); return; }
                Set(rm, Combine(reg, Get(rm), Get(0), reversed: true));
                Pop();
                return;

            case 0xD9: ExecuteD9(reg, rm); return;
            case 0xDD: ExecuteDD(reg, rm); return;

            case 0xDA:
                // The 387 has no register forms here worth emulating beyond
                // the compare-and-pop pair.
                if (reg == 5 && rm == 1) { Compare(Get(0), Get(1)); Pop(); Pop(); }
                return;

            case 0xDB:
                // FINIT and friends arrive here; the useful one is clearing
                // exceptions, which costs nothing to honour.
                if (reg == 4) { _status = 0; return; }
                return;

            case 0xDF:
                // FSTSW AX: the one instruction that moves the status word
                // into a general register, which is how a program reads back
                // the result of a comparison.
                if (reg == 4 && rm == 0) cpu.State.Ax = StatusWord;
                return;
        }
    }

    /// <summary>
    /// The eight operations the reg field selects, applied to ST(0).
    /// Comparison is not arithmetic and writes nothing back.
    /// </summary>
    private void Arithmetic(int operation, Extended80 operand)
    {
        if (operation == 2) { Compare(Get(0), operand); return; }         // FCOM
        if (operation == 3) { Compare(Get(0), operand); Pop(); return; }  // FCOMP

        Set(0, Combine(operation, Get(0), operand, reversed: false));
    }

    /// <summary>The forms whose operand came from memory as a narrower type.</summary>
    private void Arithmetic(int operation, double operand)
        => Arithmetic(operation, Extended80.FromDouble(operand));

    /// <summary>
    /// The arithmetic itself. The reversed forms exist because subtraction and
    /// division do not commute and the 387 provides both orders rather than
    /// making the compiler shuffle the stack.
    /// </summary>
    private static Extended80 Combine(int operation, Extended80 a, Extended80 b, bool reversed)
        => operation switch
        {
            0 => a + b,
            1 => a * b,
            4 => reversed ? b - a : a - b,     // FSUB / FSUBR
            5 => reversed ? a - b : b - a,
            6 => reversed ? b / a : a / b,     // FDIV / FDIVR
            7 => reversed ? a / b : b / a,
            _ => a,
        };

    /// <summary>
    /// The D9 register forms. The reg field groups them and rm picks one out:
    /// reg 4 is the sign and test group, reg 5 the constants, and reg 6 and 7
    /// the transcendentals the 387 added -- an 8087 fault here rather than
    /// computing a sine.
    /// </summary>
    private void ExecuteD9(int reg, int rm)
    {
        switch (reg)
        {
            case 0: Push(Get(rm)); return;                       // FLD ST(i)

            case 1:                                              // FXCH
            {
                Extended80 t = Get(0); Set(0, Get(rm)); Set(rm, t);
                return;
            }

            case 4:
                switch (rm)
                {
                    case 0: Set(0, -Get(0)); return;             // FCHS
                    case 1: Set(0, Math.Abs(Get(0).ToDouble())); return;    // FABS
                    case 4: Compare(Get(0), 0.0); return;        // FTST
                    case 5: Examine(); return;                   // FXAM
                }
                return;

            case 5:
                // The constants. A compiler emits these rather than loading a
                // literal from memory, so they turn up in almost any
                // floating-point code.
                switch (rm)
                {
                    case 0: Push(1.0); return;                   // FLD1
                    case 1: Push(Math.Log2(10.0)); return;       // FLDL2T
                    case 2: Push(Math.Log2(Math.E)); return;     // FLDL2E
                    case 3: Push(Math.PI); return;               // FLDPI
                    case 4: Push(Math.Log10(2.0)); return;       // FLDLG2
                    case 5: Push(Math.Log(2.0)); return;         // FLDLN2
                    case 6: Push(0.0); return;                   // FLDZ
                }
                return;

            case 6:
                switch (rm)
                {
                    case 0:   // F2XM1: 2^x - 1, done as one step because the
                              // subtraction afterwards would lose the precision
                              // this instruction exists to keep for small x.
                        Set(0, Math.Pow(2.0, Get(0).ToDouble()) - 1.0);
                        return;

                    case 1:   // FYL2X: ST(1) * log2(ST(0)), then pop
                    {
                        double x = Get(0).ToDouble(), y = Get(1).ToDouble();
                        Pop();
                        Set(0, y * Math.Log2(x));
                        return;
                    }

                    case 2:   // FPTAN: replaces ST(0) with its tangent and then
                              // pushes 1.0, so that a following FDIVP yields a
                              // cotangent without reloading anything.
                        Set(0, Math.Tan(Get(0).ToDouble()));
                        Push(1.0);
                        _status &= unchecked((ushort)~C2);
                        return;

                    case 3:   // FPATAN: atan(ST(1) / ST(0)), then pop
                    {
                        double x = Get(0).ToDouble(), y = Get(1).ToDouble();
                        Pop();
                        Set(0, Math.Atan2(y, x));
                        return;
                    }

                    case 4:   // FXTRACT: exponent replaces ST(0), significand pushed
                    {
                        double value = Get(0).ToDouble();
                        if (value == 0.0)
                        {
                            Set(0, double.NegativeInfinity);
                            Push(0.0);
                            return;
                        }
                        int exponent = (int)Math.Floor(Math.Log2(Math.Abs(value)));
                        Set(0, exponent);
                        Push(value / Math.Pow(2.0, exponent));
                        return;
                    }

                    case 5:   // FPREM1: the IEEE remainder, which rounds to
                              // nearest where FPREM truncates.
                        Set(0, Math.IEEERemainder(Get(0).ToDouble(), Get(1).ToDouble()));
                        _status &= unchecked((ushort)~C2);
                        return;

                    case 6: _top = (_top - 1) & 7; return;        // FDECSTP
                    case 7: _top = (_top + 1) & 7; return;        // FINCSTP
                }
                return;

            case 7:
                switch (rm)
                {
                    case 0:   // FPREM: the 8087's truncating remainder, kept
                              // because code older than the 387 asks for it.
                    {
                        double divisor = Get(1).ToDouble();
                        if (divisor != 0.0)
                            Set(0, Get(0).ToDouble() - divisor * Math.Truncate(Get(0).ToDouble() / divisor));
                        _status &= unchecked((ushort)~C2);
                        return;
                    }

                    case 1:   // FYL2XP1: ST(1) * log2(ST(0) + 1), then pop.
                              // Computed with Log rather than Log2 of the sum
                              // so that a tiny ST(0) keeps its precision, which
                              // is the only reason this instruction exists.
                    {
                        double x = Get(0).ToDouble(), y = Get(1).ToDouble();
                        Pop();
                        Set(0, y * (Math.Log(1.0 + x) / Math.Log(2.0)));
                        return;
                    }

                    case 2: Set(0, Math.Sqrt(Get(0).ToDouble())); return;    // FSQRT

                    case 3:   // FSINCOS: sine replaces ST(0), cosine is pushed
                    {
                        double angle = Get(0).ToDouble();
                        Set(0, Math.Sin(angle));
                        Push(Math.Cos(angle));
                        _status &= unchecked((ushort)~C2);
                        return;
                    }

                    case 4: Set(0, Math.Round(Get(0).ToDouble(), MidpointRounding.ToEven)); return;  // FRNDINT

                    case 5:   // FSCALE: multiplies by a power of two
                        Set(0, Get(0).ToDouble() * Math.Pow(2.0, Math.Truncate(Get(1).ToDouble())));
                        return;

                    case 6: Set(0, Math.Sin(Get(0).ToDouble())); _status &= unchecked((ushort)~C2); return;
                    case 7: Set(0, Math.Cos(Get(0).ToDouble())); _status &= unchecked((ushort)~C2); return;
                }
                return;
        }
    }

    /// <summary>
    /// FXAM: reports what kind of value ST(0) holds -- zero, normal, infinite,
    /// NaN or empty -- in the same three condition bits a comparison uses, plus
    /// the sign in C1. Compilers use it to test for zero without a comparison.
    /// </summary>
    private void Examine()
    {
        const ushort C1 = 1 << 9;
        _status &= unchecked((ushort)~(C0 | C1 | C2 | C3));

        Extended80 value = Get(0);
        if (value.Negative) _status |= C1;

        if (!_used[_top & 7]) { _status |= C0 | C3; return; }        // empty
        if (value.IsNaN) { _status |= C0; return; }
        if (value.IsInfinity) { _status |= C0 | C2; return; }
        if (value.IsZero) { _status |= C3; return; }
        _status |= C2;                                                // normal
    }

    /// <summary>The DD register forms: moving and dropping stack entries.</summary>
    private void ExecuteDD(int reg, int rm)
    {
        switch (reg)
        {
            case 0: _used[(_top + rm) & 7] = false; return;   // FFREE
            case 2: Set(rm, Get(0)); return;                  // FST ST(i)
            case 3: Set(rm, Get(0)); Pop(); return;           // FSTP ST(i)
        }
    }

    /// <summary>
    /// The status word as software sees it: the condition codes, plus the
    /// top-of-stack pointer that says which physical register ST(0) is.
    /// </summary>
    private ushort StatusWord => (ushort)(_status | ((_top & 7) << 11));

    /// <summary>Puts the unit back to its power-on state, as FINIT does.</summary>
    public void Reset()
    {
        Array.Clear(_stack);
        Array.Clear(_used);
        _top = 0;
        _control = 0x037F;
        _status = 0;
    }
}
