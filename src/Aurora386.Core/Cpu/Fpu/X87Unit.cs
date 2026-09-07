namespace Aurora386.Core.Cpu.Fpu;

/// <summary>
/// The floating-point unit, as a part of the processor rather than a library
/// standing in for one.
///
/// A 386 had its coprocessor in a second socket: an 80387 the buyer might or
/// might not have paid for. Where it was missing, CR0's emulation bit made
/// every floating-point instruction fault to vector 7, and a software library
/// caught the fault and finished the work. That is how this emulator did it
/// too, and it worked -- but it had to decode each instruction a second time
/// from the guest's own memory, which meant reimplementing the addressing
/// modes badly enough that 32-bit code could not be handled at all.
///
/// On a 486DX the unit is on the die and there is nothing to emulate. So the
/// instruction is executed where it is decoded, the addressing modes are the
/// processor's own, and 32-bit code needs no special case because there is no
/// second decoder to teach.
///
/// The register file is a stack of eight, addressed relative to a top-of-stack
/// pointer held in the status word. That is why the same name "ST(1)" means a
/// different register after a push: nothing moves, the pointer does. The
/// arrangement suits a compiler emitting expression trees and suits almost
/// nothing else, which is why every later floating-point design abandoned it.
/// </summary>
public sealed class X87Unit
{
    private readonly Extended80[] _stack = new Extended80[8];

    /// <summary>
    /// Which slots hold a value. The 387 keeps this in a tag word of eight
    /// two-bit fields; the distinction that matters in practice is empty
    /// against not, since reading an empty register is a stack fault rather
    /// than a value.
    /// </summary>
    private readonly bool[] _used = new bool[8];

    private int _top;

    private ushort _control = 0x037F;
    private ushort _status;

    /// <summary>The condition-code bits, which is where the 387 puts the
    /// answer to a comparison instead of in the processor's own flags.</summary>
    private const ushort C0 = 1 << 8;
    private const ushort C1 = 1 << 9;
    private const ushort C2 = 1 << 10;
    private const ushort C3 = 1 << 14;

    /// <summary>How many instructions this unit has executed; tests read it.</summary>
    public long Executed { get; private set; }

    /// <summary>
    /// The status word as software sees it: the condition codes together with
    /// the top-of-stack pointer, because "which register is ST(0)" is part of
    /// the answer to almost every question about the stack.
    /// </summary>
    public ushort StatusWord => (ushort)(_status | ((_top & 7) << 11));

    public ushort ControlWord => _control;

    /// <summary>
    /// The tag word, built from the used flags. Two bits per physical
    /// register: 3 means empty, 0 means a valid value, and the two remaining
    /// codes distinguish zero and the special values. Software reads it
    /// mainly to find out which registers it needs to save.
    /// </summary>
    public ushort TagWord
    {
        get
        {
            ushort tags = 0;
            for (int i = 0; i < 8; i++)
            {
                int tag;
                if (!_used[i]) tag = 3;
                else if (_stack[i].IsZero) tag = 1;
                else if (_stack[i].IsNaN || _stack[i].IsInfinity) tag = 2;
                else tag = 0;

                tags |= (ushort)(tag << (i * 2));
            }
            return tags;
        }
    }

    /// <summary>Puts the unit back to its power-on state, as FINIT does.</summary>
    public void Reset()
    {
        Array.Clear(_stack);
        Array.Clear(_used);
        _top = 0;
        _control = 0x037F;
        _status = 0;
    }

    // ---- the register stack -------------------------------------------------
    //
    // ST(i) counts down from the top and wraps: the file is a ring of eight and
    // it is the top pointer that moves, so a push shuffles nothing.

    public Extended80 Get(int i) => _stack[(_top + i) & 7];
    private void Set(int i, Extended80 value)
    {
        _stack[(_top + i) & 7] = value;
        _used[(_top + i) & 7] = true;
    }

    /// <summary>
    /// Writes a result computed in double. The transcendentals go through
    /// here: they are worked out at double precision and widened back, which
    /// costs the last eleven bits of significand. The four basic operations
    /// keep the full width, and those are the ones a long chain of arithmetic
    /// is made of -- a sine that is right to seventeen digits instead of
    /// nineteen has never been the thing that made a program wrong.
    /// </summary>
    private void Set(int i, double value) => Set(i, Extended80.FromDouble(value));

    /// <summary>Whether ST(i) holds a value at all.</summary>
    public bool IsUsed(int i) => _used[(_top + i) & 7];

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

    /// <summary>
    /// Rounds the way the control word says to. Rounding is to nearest by
    /// default, but a compiler emitting an integer store sets truncation
    /// first -- C's conversion from double to int is defined as truncating --
    /// and honouring that is the difference between 3 and 4.
    /// </summary>
    private long Round(double value) => ((_control >> 10) & 3) switch
    {
        1 => (long)Math.Floor(value),
        2 => (long)Math.Ceiling(value),
        3 => (long)Math.Truncate(value),
        _ => (long)Math.Round(value, MidpointRounding.ToEven),
    };

    /// <summary>
    /// Sets the condition codes after a comparison. Three outcomes go into
    /// C3/C2/C0, and "unordered" -- either operand a NaN -- is a fourth
    /// answer with all three set rather than an error.
    /// </summary>
    private void Compare(Extended80 a, Extended80 b)
    {
        _status &= unchecked((ushort)~(C0 | C2 | C3));

        switch (Extended80.Compare(a, b))
        {
            case 2: _status |= C0 | C2 | C3; return;   // unordered
            case 0: _status |= C3; return;             // equal
            case -1: _status |= C0; return;            // less
            default: return;                           // greater
        }
    }

    private void Compare(Extended80 a, double b) => Compare(a, Extended80.FromDouble(b));

    // ---- what the processor calls -------------------------------------------

    /// <summary>
    /// Executes an escape whose operand is a register, named by rm.
    ///
    /// <paramref name="opcode"/> is the escape itself, D8 through DF, and
    /// <paramref name="reg"/> and <paramref name="rm"/> the two fields of the
    /// ModRM byte the processor has already decoded.
    /// </summary>
    public void ExecuteRegister(byte opcode, int reg, int rm, out ushort? statusToAx)
    {
        statusToAx = null;
        Executed++;

        switch (opcode)
        {
            case 0xD8: Arithmetic(reg, Get(rm)); return;

            case 0xDC:
                // The same eight operations, but the result lands in ST(i)
                // rather than ST(0), and subtract and divide run the other
                // way round. The 387 provides both directions so that a
                // compiler need not shuffle the stack to get its operands in
                // the order it wants.
                if (reg is 2 or 3) { Arithmetic(reg, Get(rm)); return; }
                Set(rm, Combine(reg, Get(rm), Get(0), reversed: true));
                return;

            case 0xDE:
                // As DC, then pops. FCOMPP is the exception: reg 3, rm 1
                // compares and pops twice.
                if (reg == 3 && rm == 1) { Compare(Get(0), Get(1)); Pop(); Pop(); return; }
                Set(rm, Combine(reg, Get(rm), Get(0), reversed: true));
                Pop();
                return;

            case 0xD9: ExecuteD9(reg, rm); return;
            case 0xDD: ExecuteDD(reg, rm); return;

            case 0xDA:
                if (reg == 5 && rm == 1) { Compare(Get(0), Get(1)); Pop(); Pop(); }
                return;

            case 0xDB:
                switch (reg)
                {
                    case 4:
                        // FNCLEX clears the exception flags; FNINIT resets the
                        // whole unit. Both arrive here, told apart by rm.
                        if (rm == 2) _status &= 0xFF00;          // FNCLEX
                        else if (rm == 3) Reset();               // FNINIT
                        return;
                }
                return;

            case 0xDF:
                // FNSTSW AX: the one instruction that moves the status word
                // into a general register. Everything that tests the result
                // of a floating-point comparison goes through it, because
                // there is no other way to get the condition codes into the
                // flags.
                if (reg == 4 && rm == 0) statusToAx = StatusWord;
                return;
        }
    }

    /// <summary>
    /// The eight operations the reg field selects, against ST(0).
    /// Comparison is not arithmetic and writes nothing back.
    /// </summary>
    private void Arithmetic(int operation, Extended80 operand)
    {
        if (operation == 2) { Compare(Get(0), operand); return; }         // FCOM
        if (operation == 3) { Compare(Get(0), operand); Pop(); return; }  // FCOMP

        Set(0, Combine(operation, Get(0), operand, reversed: false));
    }

    private void Arithmetic(int operation, double operand)
        => Arithmetic(operation, Extended80.FromDouble(operand));

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
    /// The D9 register forms: reg 4 is the sign and test group, reg 5 the
    /// constants a compiler loads instead of fetching a literal from memory,
    /// and reg 6 and 7 the transcendentals the 387 added to the 8087.
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
                    case 0: Set(0, -Get(0)); return;                          // FCHS
                    case 1: Set(0, Math.Abs(Get(0).ToDouble())); return;      // FABS
                    case 4: Compare(Get(0), 0.0); return;                     // FTST
                    case 5: Examine(); return;                                // FXAM
                }
                return;

            case 5:
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
                    case 0:   // F2XM1: 2^x - 1 in one step, because computing
                              // the power and then subtracting would lose
                              // exactly the precision the instruction exists
                              // to keep for small x.
                        Set(0, Math.Pow(2.0, Get(0).ToDouble()) - 1.0);
                        return;

                    case 1:   // FYL2X: ST(1) * log2(ST(0)), then pop
                    {
                        double x = Get(0).ToDouble(), y = Get(1).ToDouble();
                        Pop();
                        Set(0, y * Math.Log2(x));
                        return;
                    }

                    case 2:   // FPTAN: tangent replaces ST(0), then 1.0 is
                              // pushed so that a following FDIVP yields the
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

                    case 5:   // FPREM1: the IEEE remainder, rounding to nearest
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
                              // because code older than the 387 asks for it
                    {
                        double divisor = Get(1).ToDouble();
                        if (divisor != 0.0)
                            Set(0, Get(0).ToDouble() - divisor * Math.Truncate(Get(0).ToDouble() / divisor));
                        _status &= unchecked((ushort)~C2);
                        return;
                    }

                    case 1:   // FYL2XP1: ST(1) * log2(ST(0) + 1), then pop.
                              // Computed through Log of the sum rather than
                              // Log2, so that a tiny ST(0) keeps the precision
                              // this instruction exists for.
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

                    case 4: Set(0, Math.Round(Get(0).ToDouble(), MidpointRounding.ToEven)); return;   // FRNDINT

                    case 5:   // FSCALE: multiply by a power of two
                        Set(0, Get(0).ToDouble() * Math.Pow(2.0, Math.Truncate(Get(1).ToDouble())));
                        return;

                    case 6: Set(0, Math.Sin(Get(0).ToDouble())); _status &= unchecked((ushort)~C2); return;
                    case 7: Set(0, Math.Cos(Get(0).ToDouble())); _status &= unchecked((ushort)~C2); return;
                }
                return;
        }
    }

    /// <summary>The DD register forms: moving and dropping stack entries.</summary>
    private void ExecuteDD(int reg, int rm)
    {
        switch (reg)
        {
            case 0: _used[(_top + rm) & 7] = false; return;   // FFREE
            case 2: Set(rm, Get(0)); return;                  // FST ST(i)
            case 3: Set(rm, Get(0)); Pop(); return;           // FSTP ST(i)
            case 4: Compare(Get(0), Get(rm)); return;         // FUCOM
            case 5: Compare(Get(0), Get(rm)); Pop(); return;  // FUCOMP
        }
    }

    /// <summary>
    /// FXAM: says what kind of value ST(0) holds -- zero, normal, infinite,
    /// NaN or empty -- in the same bits a comparison uses, plus the sign in
    /// C1. It is how a compiler tests for zero without a comparison, and the
    /// only way to ask whether a register is empty at all.
    /// </summary>
    private void Examine()
    {
        _status &= unchecked((ushort)~(C0 | C1 | C2 | C3));

        Extended80 value = Get(0);
        if (value.Negative) _status |= C1;

        if (!_used[_top & 7]) { _status |= C0 | C3; return; }        // empty
        if (value.IsNaN) { _status |= C0; return; }
        if (value.IsInfinity) { _status |= C0 | C2; return; }
        if (value.IsZero) { _status |= C3; return; }
        _status |= C2;                                                // normal
    }

    // ---- the memory forms ---------------------------------------------------
    //
    // The processor has already computed the address and read or written the
    // bytes; what arrives here is the value. Keeping it that way is the whole
    // benefit of executing inside the processor rather than beside it: the
    // segment limits, the page faults and the 32-bit addressing modes are all
    // handled once, by the code that already knows how.

    /// <summary>Loads a value pushed from memory, in whatever format it was read.</summary>
    public void LoadFromMemory(Extended80 value)
    {
        Push(value);
        Executed++;
    }

    /// <summary>
    /// The value ST(0) should be stored as, without popping. The caller
    /// writes it out in whichever format the escape named.
    /// </summary>
    public Extended80 PeekTop() => Get(0);

    /// <summary>As <see cref="PeekTop"/>, and then pops.</summary>
    public Extended80 PopTop()
    {
        Executed++;
        return Pop();
    }

    /// <summary>The integer ST(0) rounds to, under the current rounding mode.</summary>
    public long RoundTop() => Round(Get(0).ToDouble());

    /// <summary>Arithmetic against a value the processor read from memory.</summary>
    public void ArithmeticWithMemory(int operation, Extended80 operand)
    {
        Arithmetic(operation, operand);
        Executed++;
    }

    /// <summary>Drops ST(0) without reading it, after a store that pops.</summary>
    public void DropTop() => Pop();

    /// <summary>FLDCW: the rounding mode and which exceptions are masked.</summary>
    public void LoadControlWord(ushort value)
    {
        _control = value;
        Executed++;
    }

    /// <summary>
    /// Restores the whole visible state, as FRSTOR does after a task switch
    /// has saved it. The registers themselves are handed over separately.
    /// </summary>
    public void RestoreState(ushort control, ushort status, ushort tags, Extended80[] registers)
    {
        _control = control;
        _status = (ushort)(status & 0x47FF);
        _top = (status >> 11) & 7;

        for (int i = 0; i < 8; i++)
        {
            _stack[i] = registers[i];
            _used[i] = ((tags >> (i * 2)) & 3) != 3;
        }
    }

    /// <summary>The eight registers in physical order, for FSAVE.</summary>
    public Extended80[] PhysicalRegisters()
    {
        var copy = new Extended80[8];
        Array.Copy(_stack, copy, 8);
        return copy;
    }
}
