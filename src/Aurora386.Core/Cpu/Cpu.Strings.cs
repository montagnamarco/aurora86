namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>
    /// Istruzioni su stringhe: MOVS, STOS, LODS, SCAS, CMPS.
    ///
    /// Sorgente e destinazione sono impliciti: DS:SI e ES:DI. Il segmento
    /// sorgente accetta un override, quello di destinazione no -- ES:DI e'
    /// fissato dall'architettura.
    ///
    /// Con un prefisso REP l'istruzione si ripete fino a CX = 0. Qui viene
    /// eseguita <b>una iterazione per chiamata</b>, riportando EIP all'inizio
    /// dell'istruzione finche' il conteggio non si esaurisce: cosi' un
    /// REP MOVSW da 64 KB resta interrompibile, come sul ferro reale, invece
    /// di bloccare la macchina emulata in un ciclo chiuso.
    /// </summary>
    private void ExecuteString(byte opcode)
    {
        bool byteOperand = (opcode & 1) == 0;
        int size = byteOperand ? 1 : (OperandSize32 ? 4 : 2);
        int delta = Flags.Direction ? -size : size;   // DF decide la direzione

        bool repeat = _prefixes.Rep || _prefixes.RepNe;
        if (repeat)
        {
            uint count = AddressSize32 ? State.Ecx : State.Cx;
            if (count == 0)
            {
                // Conteggio gia' esaurito: l'istruzione non fa nulla.
                return;
            }
        }

        SegReg source = EffectiveSegment(SegReg.Ds);

        switch (opcode)
        {
            case 0xA4:   // MOVSB
            case 0xA5:   // MOVSW/MOVSD
                if (byteOperand)
                    WriteMem8(SegReg.Es, DestinationIndex, ReadMem8(source, SourceIndex));
                else if (size == 2)
                    WriteMem16(SegReg.Es, DestinationIndex, ReadMem16(source, SourceIndex));
                else
                    WriteMem32(SegReg.Es, DestinationIndex, ReadMem32(source, SourceIndex));
                AdvanceIndex(ref SourceIndexRef, delta);
                AdvanceIndex(ref DestinationIndexRef, delta);
                break;

            // INS: legge dalla porta in DX e scrive in ES:DI. La destinazione
            // non accetta override di segmento, come per le altre stringhe.
            case 0x6C:   // INSB
            case 0x6D:   // INSW/INSD
                if (byteOperand) WriteMem8(SegReg.Es, DestinationIndex, _io.ReadByte(State.Dx));
                else if (size == 2) WriteMem16(SegReg.Es, DestinationIndex, _io.ReadWord(State.Dx));
                else WriteMem32(SegReg.Es, DestinationIndex, _io.ReadDword(State.Dx));
                AdvanceIndex(ref DestinationIndexRef, delta);
                break;

            // OUTS: il verso opposto, da DS:SI alla porta. Qui il segmento
            // sorgente accetta l'override.
            case 0x6E:   // OUTSB
            case 0x6F:   // OUTSW/OUTSD
                if (byteOperand) _io.WriteByte(State.Dx, ReadMem8(source, SourceIndex));
                else if (size == 2) _io.WriteWord(State.Dx, ReadMem16(source, SourceIndex));
                else _io.WriteDword(State.Dx, ReadMem32(source, SourceIndex));
                AdvanceIndex(ref SourceIndexRef, delta);
                break;

            case 0xAA:   // STOSB
            case 0xAB:   // STOSW/STOSD
                if (byteOperand) WriteMem8(SegReg.Es, DestinationIndex, State.GetReg8(0));
                else if (size == 2) WriteMem16(SegReg.Es, DestinationIndex, State.Ax);
                else WriteMem32(SegReg.Es, DestinationIndex, State.Eax);
                AdvanceIndex(ref DestinationIndexRef, delta);
                break;

            case 0xAC:   // LODSB
            case 0xAD:   // LODSW/LODSD
                if (byteOperand) State.SetReg8(0, ReadMem8(source, SourceIndex));
                else if (size == 2) State.Ax = ReadMem16(source, SourceIndex);
                else State.Eax = ReadMem32(source, SourceIndex);
                AdvanceIndex(ref SourceIndexRef, delta);
                break;

            case 0xA6:   // CMPSB
            case 0xA7:   // CMPSW/CMPSD
            {
                uint left = byteOperand ? ReadMem8(source, SourceIndex)
                          : size == 2 ? ReadMem16(source, SourceIndex)
                          : ReadMem32(source, SourceIndex);
                uint right = byteOperand ? ReadMem8(SegReg.Es, DestinationIndex)
                           : size == 2 ? ReadMem16(SegReg.Es, DestinationIndex)
                           : ReadMem32(SegReg.Es, DestinationIndex);
                Flags.Set(FlagOp.Sub, left, right, left - right, (byte)(size * 8));
                AdvanceIndex(ref SourceIndexRef, delta);
                AdvanceIndex(ref DestinationIndexRef, delta);
                break;
            }

            case 0xAE:   // SCASB
            case 0xAF:   // SCASW/SCASD
            {
                uint left = byteOperand ? State.GetReg8(0)
                          : size == 2 ? State.Ax : State.Eax;
                uint right = byteOperand ? ReadMem8(SegReg.Es, DestinationIndex)
                           : size == 2 ? ReadMem16(SegReg.Es, DestinationIndex)
                           : ReadMem32(SegReg.Es, DestinationIndex);
                Flags.Set(FlagOp.Sub, left, right, left - right, (byte)(size * 8));
                AdvanceIndex(ref DestinationIndexRef, delta);
                break;
            }
        }

        if (!repeat) return;

        // Decrementa il conteggio e decide se ripetere.
        uint remaining;
        if (AddressSize32) remaining = --State.Ecx;
        else { State.Cx = (ushort)(State.Cx - 1); remaining = State.Cx; }

        if (remaining == 0) return;

        // CMPS e SCAS confrontano: REPE si ferma quando il confronto fallisce,
        // REPNE quando riesce. Le altre istruzioni ignorano ZF.
        bool comparison = opcode is 0xA6 or 0xA7 or 0xAE or 0xAF;
        if (comparison)
        {
            if (_prefixes.Rep && !Flags.Zero) return;      // REPE: esce se ZF=0
            if (_prefixes.RepNe && Flags.Zero) return;     // REPNE: esce se ZF=1
        }

        // Riporta EIP all'inizio dell'istruzione: la prossima Step() la
        // rieseguira', dopo aver dato modo alle interruzioni di intervenire.
        State.Eip = _instructionStart;
    }

    // Gli indici sorgente e destinazione sono SI/DI o ESI/EDI a seconda della
    // dimensione degli indirizzi in vigore.

    private uint SourceIndex => AddressSize32 ? State.Esi : State.Si;
    private uint DestinationIndex => AddressSize32 ? State.Edi : State.Di;

    private ref uint SourceIndexRef => ref State.Gpr[(int)Reg.Esi];
    private ref uint DestinationIndexRef => ref State.Gpr[(int)Reg.Edi];

    /// <summary>
    /// Avanza un indice. A 16 bit l'incremento avvolge dentro i 16 bit bassi
    /// senza toccare la meta' alta del registro.
    /// </summary>
    private void AdvanceIndex(ref uint index, int delta)
    {
        if (AddressSize32) index = (uint)(index + delta);
        else index = (index & 0xFFFF0000u) | (ushort)(index + delta);
    }
}
