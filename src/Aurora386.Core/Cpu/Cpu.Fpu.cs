using Aurora386.Core.Cpu.Fpu;


// The coprocessor escape opcodes D8-DF as the integer unit sees them:
// decode the operand, then hand the instruction to the software x87 in
// Firmware/Fpu, or raise the device-not-available fault when it is
// switched off. The 387's state and arithmetic live there, not here.

namespace Aurora386.Core.Cpu;

/// <summary>
/// The floating-point escapes, D8 through DF, executed where they are decoded.
///
/// The encoding is unusual and worth reading slowly, because it is not one
/// opcode space but two overlaid. With a memory operand the reg field of the
/// ModRM byte selects the operation and the escape opcode says what *format*
/// the operand is in -- D8 a 32-bit float, DC a 64-bit double, DA a 32-bit
/// integer, DE a 16-bit one. With mod == 3 there is no memory operand, so the
/// escape and the reg field together name the operation and rm picks a stack
/// register instead. The same three bits therefore mean different things
/// depending on a fourth, which is why the two halves are kept apart below
/// rather than merged into one table.
///
/// Nothing here computes an address or touches a segment: the processor has
/// already done that through the same code every other instruction uses. That
/// is the entire benefit of having the unit inside the processor. The software
/// emulator this replaced had to decode each instruction a second time out of
/// guest memory, and got 32-bit addressing wrong because it had to
/// reimplement it.
/// </summary>
public sealed partial class Cpu
{
    /// <summary>The floating-point unit. Present from the 486; on a 386 the
    /// escapes fault to vector 7 so a software library can serve them.</summary>
    public X87Unit Fpu { get; } = new();

    /// <summary>
    /// Executes one escape. The opcode has been read; the ModRM byte has not.
    /// </summary>
    private void ExecuteEscape(byte opcode)
    {
        // CR0 decides whether there is a unit here to execute on at all.
        //
        // EM set says there is no coprocessor and the instruction should fault
        // to vector 7, where a software emulator picks it up. TS set says the
        // registers belong to a task other than the running one, and the
        // handler must save and restore them before the instruction can go
        // ahead. Both raise the same "device not available" exception, and the
        // handler tells them apart by reading CR0.
        if ((State.Cr0 & (CpuState.Cr0Emulation | CpuState.Cr0TaskSwitched)) != 0)
        {
            // The operand is still decoded first, so that the effective
            // address is computed and any segment or page fault is raised in
            // the order the processor would raise it.
            DecodeModRm();
            FaultNow(7, 0);
            return;
        }

        ModRm operand = DecodeModRm();

        if (operand.IsRegister)
        {
            Fpu.ExecuteRegister(opcode, operand.Reg, operand.Register, out ushort? status);
            if (status is { } value) State.Ax = value;
            return;
        }

        ExecuteEscapeWithMemory(opcode, operand);
    }

    /// <summary>
    /// The forms whose operand is in memory. The escape says which format,
    /// the reg field which operation.
    /// </summary>
    private void ExecuteEscapeWithMemory(byte opcode, ModRm operand)
    {
        SegReg segment = operand.Segment;
        uint address = operand.Offset;

        switch (opcode)
        {
            // D8 and DC are the arithmetic group, against a single- or
            // double-precision value in memory.
            case 0xD8:
                Fpu.ArithmeticWithMemory(operand.Reg, ReadSingle(segment, address));
                return;

            case 0xDC:
                Fpu.ArithmeticWithMemory(operand.Reg, ReadDouble(segment, address));
                return;

            // DA and DE are the same operations against an integer, which the
            // 387 converts on the way in. A compiler emits these rather than
            // converting first, because the conversion is free here.
            case 0xDA:
                Fpu.ArithmeticWithMemory(operand.Reg,
                    Extended80.FromDouble((int)ReadMem32(segment, address)));
                return;

            case 0xDE:
                Fpu.ArithmeticWithMemory(operand.Reg,
                    Extended80.FromDouble((short)ReadMem16(segment, address)));
                return;

            case 0xD9: EscapeD9Memory(operand.Reg, segment, address); return;
            case 0xDB: EscapeDBMemory(operand.Reg, segment, address); return;
            case 0xDD: EscapeDDMemory(operand.Reg, segment, address); return;
            case 0xDF: EscapeDFMemory(operand.Reg, segment, address); return;
        }
    }

    /// <summary>D9: single-precision moves, and the control word.</summary>
    private void EscapeD9Memory(int reg, SegReg segment, uint address)
    {
        switch (reg)
        {
            case 0: Fpu.LoadFromMemory(ReadSingle(segment, address)); return;       // FLD
            case 2: WriteSingle(segment, address, Fpu.PeekTop()); return;           // FST
            case 3: WriteSingle(segment, address, Fpu.PopTop()); return;            // FSTP

            // FLDCW and FNSTCW: the rounding mode, the working precision, and
            // which exceptions are masked. A compiler switches rounding to
            // truncation around every conversion from double to int, so these
            // two are far more common than the arithmetic they configure.
            case 5: Fpu.LoadControlWord(ReadMem16(segment, address)); return;
            case 7: WriteMem16(segment, address, Fpu.ControlWord); return;

            // FNSTENV and FLDENV: the control, status and tag words plus the
            // saved instruction and operand pointers, but not the registers.
            // A signal handler saves the environment to find out *which*
            // instruction faulted; saving the registers too would be FSAVE.
            case 6: StoreEnvironment(segment, address); return;
            case 4: LoadEnvironment(segment, address); return;
        }
    }

    /// <summary>DB: 32-bit integers, and the 80-bit format the registers use.</summary>
    private void EscapeDBMemory(int reg, SegReg segment, uint address)
    {
        switch (reg)
        {
            case 0:   // FILD dword
                Fpu.LoadFromMemory(Extended80.FromDouble((int)ReadMem32(segment, address)));
                return;

            case 2:   // FIST
            case 3:   // FISTP
                WriteMem32(segment, address, (uint)Fpu.RoundTop());
                if (reg == 3) Fpu.DropTop();
                return;

            // The 80-bit format: this is what a C "long double" is, and what a
            // compiler spills a register to when the full width has to
            // survive. Nothing is narrowed on the way through, which is the
            // whole reason the format exists.
            case 5: Fpu.LoadFromMemory(ReadExtended(segment, address)); return;     // FLD
            case 7: WriteExtended(segment, address, Fpu.PopTop()); return;          // FSTP
        }
    }

    /// <summary>DD: double-precision moves, the status word, and FSAVE/FRSTOR.</summary>
    private void EscapeDDMemory(int reg, SegReg segment, uint address)
    {
        switch (reg)
        {
            case 0: Fpu.LoadFromMemory(ReadDouble(segment, address)); return;       // FLD
            case 2: WriteDouble(segment, address, Fpu.PeekTop()); return;           // FST
            case 3: WriteDouble(segment, address, Fpu.PopTop()); return;            // FSTP

            case 4: RestoreFullState(segment, address); return;                     // FRSTOR
            case 6: SaveFullState(segment, address); return;                        // FNSAVE
            case 7: WriteMem16(segment, address, Fpu.StatusWord); return;           // FNSTSW
        }
    }

    /// <summary>DF: 16-bit integers, and the 64-bit one only DF carries.</summary>
    private void EscapeDFMemory(int reg, SegReg segment, uint address)
    {
        switch (reg)
        {
            case 0:   // FILD word
                Fpu.LoadFromMemory(Extended80.FromDouble((short)ReadMem16(segment, address)));
                return;

            case 2:
            case 3:
                WriteMem16(segment, address, (ushort)Fpu.RoundTop());
                if (reg == 3) Fpu.DropTop();
                return;

            case 5:   // FILD qword
            {
                ulong bits = ReadMem32(segment, address)
                           | ((ulong)ReadMem32(segment, address + 4) << 32);
                Fpu.LoadFromMemory(Extended80.FromDouble((long)bits));
                return;
            }

            case 7:   // FISTP qword
            {
                long value = Fpu.RoundTop();
                Fpu.DropTop();
                WriteMem32(segment, address, (uint)value);
                WriteMem32(segment, address + 4, (uint)((ulong)value >> 32));
                return;
            }
        }
    }

    // ---- the memory formats --------------------------------------------------

    private Extended80 ReadSingle(SegReg segment, uint address)
        => Extended80.FromDouble(
            BitConverter.Int32BitsToSingle(unchecked((int)ReadMem32(segment, address))));

    private void WriteSingle(SegReg segment, uint address, Extended80 value)
        => WriteMem32(segment, address,
            unchecked((uint)BitConverter.SingleToInt32Bits((float)value.ToDouble())));

    private Extended80 ReadDouble(SegReg segment, uint address)
    {
        ulong bits = ReadMem32(segment, address)
                   | ((ulong)ReadMem32(segment, address + 4) << 32);
        return Extended80.FromDouble(BitConverter.Int64BitsToDouble(unchecked((long)bits)));
    }

    private void WriteDouble(SegReg segment, uint address, Extended80 value)
    {
        ulong bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value.ToDouble()));
        WriteMem32(segment, address, (uint)bits);
        WriteMem32(segment, address + 4, (uint)(bits >> 32));
    }

    /// <summary>
    /// The 387's own format: a 15-bit exponent, a sign, and a 64-bit
    /// significand whose top bit is written out explicitly instead of being
    /// implied as it is in the shorter formats. Ten bytes, so the last two
    /// are read on their own.
    /// </summary>
    private Extended80 ReadExtended(SegReg segment, uint address)
    {
        ulong significand = ReadMem32(segment, address)
                          | ((ulong)ReadMem32(segment, address + 4) << 32);
        return Extended80.FromBits(significand, ReadMem16(segment, address + 8));
    }

    private void WriteExtended(SegReg segment, uint address, Extended80 value)
    {
        (ulong significand, ushort signAndExponent) = value.ToBits();
        WriteMem32(segment, address, (uint)significand);
        WriteMem32(segment, address + 4, (uint)(significand >> 32));
        WriteMem16(segment, address + 8, signAndExponent);
    }

    // ---- saving and restoring the unit ---------------------------------------
    //
    // Both come in four layouts: 16- or 32-bit, real or protected mode. What
    // varies is how the instruction and operand pointers are recorded -- as a
    // segment and offset in real mode, as a selector and offset in protected
    // mode -- and whether the fields are words or dwords. The control, status
    // and tag words are always the first three.
    //
    // The pointers are stored as zero here. They exist so that an exception
    // handler can report which instruction faulted and on what; since
    // exceptions are masked in every configuration this emulator has been run
    // with, nothing has ever read them back. Writing zeros is honest about
    // that in a way that writing plausible-looking rubbish would not be.

    private bool EnvironmentIs32Bit => OperandSize32;

    private void StoreEnvironment(SegReg segment, uint address)
    {
        if (EnvironmentIs32Bit)
        {
            WriteMem32(segment, address, Fpu.ControlWord);
            WriteMem32(segment, address + 4, Fpu.StatusWord);
            WriteMem32(segment, address + 8, Fpu.TagWord);
            for (uint i = 12; i < 28; i += 4) WriteMem32(segment, address + i, 0);
        }
        else
        {
            WriteMem16(segment, address, Fpu.ControlWord);
            WriteMem16(segment, address + 2, Fpu.StatusWord);
            WriteMem16(segment, address + 4, Fpu.TagWord);
            for (uint i = 6; i < 14; i += 2) WriteMem16(segment, address + i, 0);
        }
    }

    private void LoadEnvironment(SegReg segment, uint address)
    {
        ushort control, status, tags;

        if (EnvironmentIs32Bit)
        {
            control = (ushort)ReadMem32(segment, address);
            status = (ushort)ReadMem32(segment, address + 4);
            tags = (ushort)ReadMem32(segment, address + 8);
        }
        else
        {
            control = ReadMem16(segment, address);
            status = ReadMem16(segment, address + 2);
            tags = ReadMem16(segment, address + 4);
        }

        // The registers are not part of an environment, so they are handed
        // back unchanged: FLDENV restores how the unit is configured and what
        // it has been told, not what it holds.
        Fpu.RestoreState(control, status, tags, Fpu.PhysicalRegisters());
    }

    /// <summary>
    /// FNSAVE: the environment followed by the eight registers in physical
    /// order, ten bytes each. Physical order, not stack order -- the top of
    /// stack pointer is in the status word, and restoring puts it back, so
    /// the registers must not be rotated on the way out.
    /// </summary>
    private void SaveFullState(SegReg segment, uint address)
    {
        StoreEnvironment(segment, address);

        uint registersAt = address + (EnvironmentIs32Bit ? 28u : 14u);
        Extended80[] registers = Fpu.PhysicalRegisters();

        for (int i = 0; i < 8; i++)
            WriteExtended(segment, registersAt + (uint)(i * 10), registers[i]);

        // FNSAVE leaves the unit initialised, which is what makes it usable
        // as the "save and forget" half of a context switch: the incoming
        // task finds a clean stack without having to run FINIT itself.
        Fpu.Reset();
    }

    private void RestoreFullState(SegReg segment, uint address)
    {
        ushort control, status, tags;

        if (EnvironmentIs32Bit)
        {
            control = (ushort)ReadMem32(segment, address);
            status = (ushort)ReadMem32(segment, address + 4);
            tags = (ushort)ReadMem32(segment, address + 8);
        }
        else
        {
            control = ReadMem16(segment, address);
            status = ReadMem16(segment, address + 2);
            tags = ReadMem16(segment, address + 4);
        }

        uint registersAt = address + (EnvironmentIs32Bit ? 28u : 14u);
        var registers = new Extended80[8];
        for (int i = 0; i < 8; i++)
            registers[i] = ReadExtended(segment, registersAt + (uint)(i * 10));

        Fpu.RestoreState(control, status, tags, registers);
    }
}
