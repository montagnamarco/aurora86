namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>
    /// Decodifica il byte ModR/M e, se l'operando e' in memoria, ne calcola
    /// l'offset effettivo consumando SIB e spiazzamento dal flusso.
    ///
    /// Il byte e' diviso in mod(7-6) reg(5-3) rm(2-0). Con mod == 3 l'operando
    /// r/m e' un registro; altrimenti e' memoria, e la forma dell'indirizzo
    /// dipende dalla dimensione degli indirizzi in vigore, non da quella degli
    /// operandi.
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

        // Un prefisso esplicito ha sempre la precedenza sul segmento di default.
        if (_prefixes.Segment.HasValue)
            result.Segment = _prefixes.Segment.Value;

        return result;
    }

    /// <summary>
    /// Indirizzamento a 16 bit: sette combinazioni fisse di BX/BP + SI/DI, piu'
    /// l'indirizzo diretto. Le forme che usano BP hanno SS come segmento di
    /// default, perche' BP serve a puntare nel frame di stack.
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
                // mod == 0 con rm == 6 non e' [BP]: e' l'indirizzo diretto a 16
                // bit. [BP] senza spiazzamento non e' codificabile, si usa
                // mod == 1 con spiazzamento zero.
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

        // Lo spiazzamento a 8 bit e' con segno: 0xFF significa -1, non +255.
        if (mod == 1) address += (ushort)(sbyte)FetchByte();
        else if (mod == 2) address += FetchWord();

        result.Offset = address;
        result.Segment = segment;
    }

    /// <summary>
    /// Indirizzamento a 32 bit: qualsiasi registro puo' fare da base, con
    /// l'aggiunta del byte SIB per indice e scala.
    /// </summary>
    private void DecodeAddress32(byte mod, byte rm, ref ModRm result)
    {
        uint address = 0;
        SegReg segment = SegReg.Ds;

        if (rm == 4)
        {
            // rm == 4 non e' ESP: segnala la presenza del byte SIB.
            address = DecodeSib(mod, ref segment, ref result);
        }
        else if (rm == 5 && mod == 0)
        {
            // Come nel caso a 16 bit, questa combinazione e' l'indirizzo diretto.
            address = FetchDword();
        }
        else
        {
            address = State.Gpr[rm];
            if (rm == 5) segment = SegReg.Ss;   // EBP come base implica lo stack
        }

        if (mod == 1) address += (uint)(sbyte)FetchByte();
        else if (mod == 2) address += FetchDword();

        result.Offset = address;
        result.Segment = segment;
    }

    /// <summary>
    /// Byte SIB: scala(7-6) indice(5-3) base(2-0). L'indirizzo risultante e'
    /// base + indice * 2^scala.
    /// </summary>
    private uint DecodeSib(byte mod, ref SegReg segment, ref ModRm result)
    {
        byte sib = FetchByte();
        byte scale = (byte)(sib >> 6);
        byte index = (byte)((sib >> 3) & 7);
        byte baseReg = (byte)(sib & 7);

        uint address = 0;

        // index == 4 significa "nessun indice": ESP non puo' fare da indice.
        // Sul 386, pero', la scala non resta inutilizzata: viene applicata
        // alla base. E' una stranezza del solo 386 -- dal 486 in poi la scala
        // senza indice non ha effetto -- e i test generati da hardware la
        // confermano su tutte le forme con SIB.
        bool scalaSullaBase = index == 4 && scale != 0;
        if (index != 4)
            address += State.Gpr[index] << scale;

        if (baseReg == 5 && mod == 0)
        {
            // Base assente: spiazzamento a 32 bit al suo posto.
            address += FetchDword();
        }
        else
        {
            address += scalaSullaBase ? State.Gpr[baseReg] << scale
                                      : State.Gpr[baseReg];
            if (baseReg == 4 || baseReg == 5) segment = SegReg.Ss;  // ESP o EBP
            if (baseReg == 4) result.UsesStackPointer = true;
        }

        return address;
    }

    // --- lettura e scrittura dell'operando r/m -------------------------------

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

    /// <summary>Legge r/m alla dimensione corrente degli operandi.</summary>
    private uint ReadRm(in ModRm modrm)
        => OperandSize32 ? ReadRm32(modrm) : ReadRm16(modrm);

    private void WriteRm(in ModRm modrm, uint value)
    {
        if (OperandSize32) WriteRm32(modrm, value);
        else WriteRm16(modrm, (ushort)value);
    }

    /// <summary>Legge il registro indicato dal campo reg, alla dimensione corrente.</summary>
    private uint ReadRegOperand(in ModRm modrm)
        => OperandSize32 ? State.GetReg32(modrm.Reg) : State.GetReg16(modrm.Reg);

    private void WriteRegOperand(in ModRm modrm, uint value)
    {
        if (OperandSize32) State.SetReg32(modrm.Reg, value);
        else State.SetReg16(modrm.Reg, (ushort)value);
    }
}
