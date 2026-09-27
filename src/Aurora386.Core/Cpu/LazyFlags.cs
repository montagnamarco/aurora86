using System.Numerics;
using System.Runtime.CompilerServices;

namespace Aurora386.Core.Cpu;

/// <summary>
/// The kind of operation that produced the most recent arithmetic result.
/// </summary>
public enum FlagOp : byte
{
    /// <summary>The flags are already materialized in <see cref="LazyFlags.Stored"/>.</summary>
    None = 0,
    Add, Adc, Sub, Sbb,
    /// <summary>AND/OR/XOR/TEST: these always clear carry and overflow.</summary>
    Logic,
    Inc, Dec,
    Shl, Shr, Sar,
    Neg,
    Mul, Imul,
}

/// <summary>
/// Lazy evaluation of the arithmetic flags.
///
/// An instruction like ADD is supposed to update six flags, but real code
/// almost always reads at most one of them (typically ZF or CF right after a
/// CMP) and very often none at all: the result is simply overwritten by the
/// next instruction. Computing all six every time is therefore mostly wasted
/// work.
///
/// Instead we record the operands, the result and the kind of operation, and
/// derive the flags only when someone actually reads one. DOSBox and QEMU use
/// the same strategy, and on the hot path it makes a clear difference.
/// </summary>
public struct LazyFlags
{
    /// <summary>Flags already computed, or set explicitly (POPF, STI, ...).</summary>
    public Flags Stored;

    /// <summary>The pending operation, or None when Stored is already valid.</summary>
    public FlagOp Op;

    public uint Left;
    public uint Right;
    public uint Result;
    /// <summary>Operand width in bits: 8, 16 or 32.</summary>
    public byte Size;

    /// <summary>
    /// Incoming carry for ADC/SBB. It cannot be reconstructed from the result
    /// -- an ADC with carry 1 and an ADC with carry 0 on different operands can
    /// land on the same result -- so it has to be recorded when the operation
    /// is registered.
    /// </summary>
    public bool CarryIn;

    /// <summary>
    /// Records an operation without computing its flags. The non-arithmetic
    /// flags (IF, DF, TF...) live in Stored and are left untouched.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(FlagOp op, uint left, uint right, uint result, byte size,
                    bool carryIn = false)
    {
        // INC and DEC are the one family that *inherits* the carry flag rather
        // than computing it: the 386 deliberately leaves CF alone so that INC
        // can be used as a loop counter inside a multi-precision addition
        // without destroying the running carry.
        //
        // Inheriting means reading, and reading a lazy flag is only safe once
        // the pending operation has been resolved. If the previous instruction
        // is still lazy, its carry does not exist anywhere in Stored yet -- it
        // is implied by Op/Left/Right/Result, which we are about to overwrite.
        // Materializing first turns that implied carry into a real bit in
        // Stored; skipping it would leave INC copying whatever stale carry
        // predated the previous instruction, and the bug would only surface in
        // ADC/INC chains, i.e. precisely in bignum arithmetic.
        if (op is FlagOp.Inc or FlagOp.Dec) Materialize();

        Op = op;
        Left = left;
        Right = right;
        Result = result;
        Size = size;
        CarryIn = carryIn;
    }

    /// <summary>Mask of the significant bits for the current operand width.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint MaskFor(byte size) => size switch
    {
        8 => 0xFFu,
        16 => 0xFFFFu,
        _ => 0xFFFFFFFFu,
    };

    /// <summary>Sign bit for the current operand width.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint SignBit(byte size) => 1u << (size - 1);

    /// <summary>
    /// Materializes the arithmetic flags into Stored and clears the pending
    /// operation. Called only when a flag is actually read.
    /// </summary>
    public void Materialize()
    {
        if (Op == FlagOp.None) return;

        uint mask = MaskFor(Size);
        uint sign = SignBit(Size);
        uint result = Result & mask;
        uint left = Left & mask;
        uint right = Right & mask;

        // The flags that depend only on the result are common to every
        // operation, so they are computed once up front.
        Flags flags = Stored & ~(Flags.Carry | Flags.Parity | Flags.Adjust
                               | Flags.Zero | Flags.Sign | Flags.Overflow);

        if (result == 0) flags |= Flags.Zero;
        if ((result & sign) != 0) flags |= Flags.Sign;

        // Parity on x86 only ever looks at the low 8 bits, even for 16- and
        // 32-bit operands. It is a leftover from the 8080's serial-link
        // heritage, and no later processor changed it.
        if ((BitOperations.PopCount(result & 0xFF) & 1) == 0) flags |= Flags.Parity;

        switch (Op)
        {
            case FlagOp.Add:
            case FlagOp.Adc:
            {
                // With an incoming carry of 1 the result can equal the left
                // operand and still have carried out (0xFF + 0x00 + 1 = 0x00,
                // but also 0xFF + 0xFF + 1 = 0xFF), so the comparison has to
                // become <= rather than <.
                bool carry = CarryIn ? result <= left : result < left;
                if (carry) flags |= Flags.Carry;
                if (((left ^ result) & (right ^ result) & sign) != 0) flags |= Flags.Overflow;
                if (((left ^ right ^ result) & 0x10) != 0) flags |= Flags.Adjust;
                break;
            }

            case FlagOp.Sub:
            case FlagOp.Sbb:
            {
                bool carry = CarryIn ? left <= right : left < right;
                if (carry) flags |= Flags.Carry;
                if (((left ^ right) & (left ^ result) & sign) != 0) flags |= Flags.Overflow;
                if (((left ^ right ^ result) & 0x10) != 0) flags |= Flags.Adjust;
                break;
            }

            case FlagOp.Logic:
                // AND/OR/XOR clear CF and OF, which the mask above already did.
                // AF is architecturally undefined here; real silicon leaves
                // something unpredictable in it, and we leave it at zero.
                break;

            case FlagOp.Inc:
                // INC and DEC deliberately preserve the carry flag: that is
                // what makes them usable as counters inside multi-precision
                // addition loops, where CF must survive from one limb to the
                // next.
                flags |= Stored & Flags.Carry;
                if (result == sign) flags |= Flags.Overflow;
                if ((result & 0x0F) == 0) flags |= Flags.Adjust;
                break;

            case FlagOp.Dec:
                flags |= Stored & Flags.Carry;
                if (result == (sign - 1)) flags |= Flags.Overflow;
                if ((result & 0x0F) == 0x0F) flags |= Flags.Adjust;
                break;

            case FlagOp.Neg:
                // NEG is 0 - operand, so the borrow -- and hence CF -- is set
                // exactly when the operand was non-zero.
                if (left != 0) flags |= Flags.Carry;
                if (result == sign) flags |= Flags.Overflow;
                if ((result & 0x0F) != 0) flags |= Flags.Adjust;
                break;

            case FlagOp.Shl:
                // CF is the last bit shifted out of the top; OF is only
                // meaningful for a shift of 1, where it is CF xor the new sign
                // bit -- that is, "did the sign change".
                if (Right != 0 && Right <= Size
                    && ((left >> (Size - (int)Right)) & 1) != 0) flags |= Flags.Carry;
                if (Right == 1 && (((result & sign) != 0) != ((flags & Flags.Carry) != 0)))
                    flags |= Flags.Overflow;
                break;

            case FlagOp.Shr:
                if (Right != 0 && Right <= Size
                    && ((left >> ((int)Right - 1)) & 1) != 0) flags |= Flags.Carry;
                if (Right == 1 && (left & sign) != 0) flags |= Flags.Overflow;
                break;

            case FlagOp.Sar:
                if (Right != 0 && ((left >> Math.Min((int)Right - 1, Size - 1)) & 1) != 0)
                    flags |= Flags.Carry;
                // SAR replicates the sign bit, so the sign can never flip and
                // OF is always cleared.
                break;

            case FlagOp.Mul:
            case FlagOp.Imul:
                // For MUL/IMUL, CF and OF are set together and the caller has
                // already made the decision, passing 0 or 1 in Right: they
                // report whether the result overflowed the low half. ZF/SF/PF
                // are undefined on real silicon after a multiply.
                if (Right != 0) flags |= Flags.Carry | Flags.Overflow;
                break;
        }

        Stored = flags;
        Op = FlagOp.None;
    }

    /// <summary>The complete EFLAGS value, materializing first if needed.</summary>
    public uint Value
    {
        get
        {
            Materialize();
            return (uint)Stored | (uint)Flags.Reserved1;
        }
    }

    /// <summary>Sets EFLAGS wholesale (POPF, IRET), discarding any laziness.</summary>
    public void SetValue(uint value)
    {
        // The VM bit is not kept here: whether the processor is running a
        // virtual 8086 task is recorded in the state, and it is put back into
        // the image only when the flags are pushed by an interrupt out of the
        // task. Letting it in would leave it behind after the task has been
        // left -- the next interrupt from the monitor's own code would then
        // push an image claiming to come from the task, and Windows' monitor
        // reads exactly that bit to decide which way to dispatch.
        Stored = ((Flags)value & ~Flags.Virtual8086) | Flags.Reserved1;
        Op = FlagOp.None;
        CarryIn = false;
    }

    // --- access to the individual flags ---------------------------------------

    public bool Carry
    {
        get { Materialize(); return (Stored & Flags.Carry) != 0; }
        set { Materialize(); Stored = value ? Stored | Flags.Carry : Stored & ~Flags.Carry; }
    }

    public bool Parity
    {
        get { Materialize(); return (Stored & Flags.Parity) != 0; }
        set { Materialize(); Stored = value ? Stored | Flags.Parity : Stored & ~Flags.Parity; }
    }

    public bool Adjust
    {
        get { Materialize(); return (Stored & Flags.Adjust) != 0; }
        set { Materialize(); Stored = value ? Stored | Flags.Adjust : Stored & ~Flags.Adjust; }
    }

    public bool Zero
    {
        get { Materialize(); return (Stored & Flags.Zero) != 0; }
        set { Materialize(); Stored = value ? Stored | Flags.Zero : Stored & ~Flags.Zero; }
    }

    public bool Sign
    {
        get { Materialize(); return (Stored & Flags.Sign) != 0; }
        set { Materialize(); Stored = value ? Stored | Flags.Sign : Stored & ~Flags.Sign; }
    }

    public bool Overflow
    {
        get { Materialize(); return (Stored & Flags.Overflow) != 0; }
        set { Materialize(); Stored = value ? Stored | Flags.Overflow : Stored & ~Flags.Overflow; }
    }

    // The control flags are never lazy -- no arithmetic operation produces them
    // -- so they can be read and written directly.
    public bool Interrupt
    {
        readonly get => (Stored & Flags.Interrupt) != 0;
        set => Stored = value ? Stored | Flags.Interrupt : Stored & ~Flags.Interrupt;
    }

    public bool Direction
    {
        readonly get => (Stored & Flags.Direction) != 0;
        set => Stored = value ? Stored | Flags.Direction : Stored & ~Flags.Direction;
    }

    public bool Trap
    {
        readonly get => (Stored & Flags.Trap) != 0;
        set => Stored = value ? Stored | Flags.Trap : Stored & ~Flags.Trap;
    }

    // --- VME/PVI (P2.3) ---------------------------------------------------
    // Neither bit is ever lazy -- no arithmetic operation produces either --
    // so, like Interrupt/Direction/Trap above, they read and write Stored
    // directly. Cpu.VirtualMode.cs is what decides *when* code may touch
    // them; these two properties only say *how*.

    public bool VirtualInterrupt
    {
        readonly get => (Stored & Flags.VirtualInterrupt) != 0;
        set => Stored = value ? Stored | Flags.VirtualInterrupt : Stored & ~Flags.VirtualInterrupt;
    }

    public bool VirtualInterruptPending
    {
        readonly get => (Stored & Flags.VirtualInterruptPending) != 0;
        set => Stored = value ? Stored | Flags.VirtualInterruptPending : Stored & ~Flags.VirtualInterruptPending;
    }
}
