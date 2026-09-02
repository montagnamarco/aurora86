namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>
    /// Decodes the ModR/M byte and, when the operand lives in memory, works out
    /// its effective offset, consuming the SIB byte and the displacement from
    /// the instruction stream.
    ///
    /// The byte splits into mod(7-6) reg(5-3) rm(2-0). With mod == 3 the r/m
    /// operand is a register; otherwise it is memory, and the shape of the
    /// address depends on the address size in effect, not on the operand size.
    /// </summary>
    private ModRm DecodeModRm()
    {
        byte modrm = FetchByte();
        byte mod = (byte)(modrm >> 6);
        byte reg = (byte)((modrm >> 3) & 7);
        byte rm = (byte)(modrm & 7);

        ModRm result = default;
        result.Reg = reg;

        if (mod == 3)
        {
            result.IsRegister = true;
            result.Register = rm;
            return result;
        }

        result.IsRegister = false;
        if (AddressSize32)
            DecodeAddress32(mod, rm, ref result);
        else
            DecodeAddress16(mod, rm, ref result);

        // An explicit prefix always wins over the addressing mode's default
        // segment.
        if (_prefixes.Segment.HasValue)
            result.Segment = _prefixes.Segment.Value;

        return result;
    }

    /// <summary>
    /// 16-bit addressing: seven fixed combinations of BX/BP with SI/DI, plus a
    /// direct address. The forms built on BP default to SS, because BP exists
    /// to point into the stack frame and the 8086 designers wired that
    /// assumption into the addressing modes themselves.
    /// </summary>
    private void DecodeAddress16(byte mod, byte rm, ref ModRm result)
    {
        ushort address;
        SegReg segment = SegReg.Ds;

        switch (rm)
        {
            case 0: address = (ushort)(State.Bx + State.Si); break;
            case 1: address = (ushort)(State.Bx + State.Di); break;
            case 2: address = (ushort)(State.Bp + State.Si); segment = SegReg.Ss; break;
            case 3: address = (ushort)(State.Bp + State.Di); segment = SegReg.Ss; break;
            case 4: address = State.Si; break;
            case 5: address = State.Di; break;
            case 6:
                // mod == 0 with rm == 6 does not mean [BP]: it is the 16-bit
                // direct address. Plain [BP] with no displacement simply is not
                // encodable, and assemblers emit mod == 1 with a zero
                // displacement instead.
                if (mod == 0)
                {
                    address = FetchWord();
                }
                else
                {
                    address = State.Bp;
                    segment = SegReg.Ss;
                }
                break;
            default: address = State.Bx; break;
        }

        // The 8-bit displacement is signed: 0xFF means -1, not +255.
        if (mod == 1) address += (ushort)(sbyte)FetchByte();
        else if (mod == 2) address += FetchWord();

        result.Offset = address;
        result.Segment = segment;
    }

    /// <summary>
    /// 32-bit addressing: any register can serve as the base, and the optional
    /// SIB byte adds an index with a scale factor.
    /// </summary>
    private void DecodeAddress32(byte mod, byte rm, ref ModRm result)
    {
        uint address = 0;
        SegReg segment = SegReg.Ds;

        if (rm == 4)
        {
            // rm == 4 does not mean ESP here: it signals that a SIB byte
            // follows. ESP as a base is still reachable, just only through
            // that SIB byte.
            address = DecodeSib(mod, ref segment, ref result);
        }
        else if (rm == 5 && mod == 0)
        {
            // As in the 16-bit case, this combination encodes the direct
            // address rather than [EBP].
            address = FetchDword();
        }
        else
        {
            address = State.Gpr[rm];
            if (rm == 5) segment = SegReg.Ss;   // EBP as a base implies the stack
        }

        if (mod == 1) address += (uint)(sbyte)FetchByte();
        else if (mod == 2) address += FetchDword();

        result.Offset = address;
        result.Segment = segment;
    }

    /// <summary>
    /// The SIB byte: scale(7-6) index(5-3) base(2-0). The address it describes
    /// is base + index * 2^scale.
    /// </summary>
    private uint DecodeSib(byte mod, ref SegReg segment, ref ModRm result)
    {
        byte sib = FetchByte();
        byte scale = (byte)(sib >> 6);
        byte index = (byte)((sib >> 3) & 7);
        byte baseReg = (byte)(sib & 7);

        uint address = 0;

        // index == 4 encodes "no index register": ESP cannot be used as an
        // index, so its slot in the index field was repurposed to mean absent.
        //
        // On the 386 -- and only on the 386 -- the scale field does not go
        // unused when that happens. The address unit still applies the shift,
        // but to the *base* register instead, so a SIB byte encoding
        // "[EBX], no index, scale 4" actually computes EBX * 4. From the 486
        // onwards Intel cleaned this up: a scale with no index has no effect
        // whatsoever, and the base is added unscaled, which is what every
        // modern reference documents.
        //
        // Assemblers never emit such an encoding, so the quirk only shows up
        // in hand-written or generated code -- but it does show up, and the
        // SingleStepTests suite captured from real 386 silicon reproduces it
        // consistently across every SIB form. Getting it wrong silently
        // corrupts effective addresses in exactly the programs that were
        // clever enough to depend on it.
        bool scalaSullaBase = index == 4 && scale != 0;
        if (index != 4)
            address += State.Gpr[index] << scale;

        if (baseReg == 5 && mod == 0)
        {
            // No base register: a 32-bit displacement takes its place.
            address += FetchDword();
        }
        else
        {
            address += scalaSullaBase ? State.Gpr[baseReg] << scale
                                      : State.Gpr[baseReg];
            if (baseReg == 4 || baseReg == 5) segment = SegReg.Ss;  // ESP or EBP
            if (baseReg == 4) result.UsesStackPointer = true;
        }

        return address;
    }

    // --- reading and writing the r/m operand ----------------------------------

    private byte ReadRm8(in ModRm modrm)
        => modrm.IsRegister ? State.GetReg8(modrm.Register)
                            : ReadMem8(modrm.Segment, modrm.Offset);

    private ushort ReadRm16(in ModRm modrm)
        => modrm.IsRegister ? State.GetReg16(modrm.Register)
                            : ReadMem16(modrm.Segment, modrm.Offset);

    private uint ReadRm32(in ModRm modrm)
        => modrm.IsRegister ? State.GetReg32(modrm.Register)
                            : ReadMem32(modrm.Segment, modrm.Offset);

    private void WriteRm8(in ModRm modrm, byte value)
    {
        if (modrm.IsRegister) State.SetReg8(modrm.Register, value);
        else WriteMem8(modrm.Segment, modrm.Offset, value);
    }

    private void WriteRm16(in ModRm modrm, ushort value)
    {
        if (modrm.IsRegister) State.SetReg16(modrm.Register, value);
        else WriteMem16(modrm.Segment, modrm.Offset, value);
    }

    private void WriteRm32(in ModRm modrm, uint value)
    {
        if (modrm.IsRegister) State.SetReg32(modrm.Register, value);
        else WriteMem32(modrm.Segment, modrm.Offset, value);
    }

    /// <summary>Reads r/m at the current operand size.</summary>
    private uint ReadRm(in ModRm modrm)
        => OperandSize32 ? ReadRm32(modrm) : ReadRm16(modrm);

    private void WriteRm(in ModRm modrm, uint value)
    {
        if (OperandSize32) WriteRm32(modrm, value);
        else WriteRm16(modrm, (ushort)value);
    }

    /// <summary>Reads the register named by the reg field, at the current operand size.</summary>
    private uint ReadRegOperand(in ModRm modrm)
        => OperandSize32 ? State.GetReg32(modrm.Reg) : State.GetReg16(modrm.Reg);

    private void WriteRegOperand(in ModRm modrm, uint value)
    {
        if (OperandSize32) State.SetReg32(modrm.Reg, value);
        else State.SetReg16(modrm.Reg, (ushort)value);
    }
}
