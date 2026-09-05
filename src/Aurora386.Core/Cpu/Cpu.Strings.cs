namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>
    /// The string instructions: MOVS, STOS, LODS, SCAS, CMPS.
    ///
    /// Source and destination are implicit: DS:SI and ES:DI. The source segment
    /// accepts an override prefix, the destination does not -- ES:DI is fixed
    /// by the architecture, which is why string code so often has to reload ES.
    ///
    /// With a REP prefix the instruction repeats until CX reaches zero. Here we
    /// execute <b>one iteration per call</b>, rewinding EIP to the start of the
    /// instruction until the count runs out. That keeps a 64 KB REP MOVSW
    /// interruptible, exactly as it is on real hardware, instead of locking the
    /// emulated machine inside a closed loop where no timer tick or keystroke
    /// could ever be serviced.
    /// </summary>
    private void ExecuteString(byte opcode)
    {
        bool byteOperand = (opcode & 1) == 0;
        int size = byteOperand ? 1 : (OperandSize32 ? 4 : 2);
        int delta = Flags.Direction ? -size : size;   // DF picks the direction

        bool repeat = _prefixes.Rep || _prefixes.RepNe;
        if (repeat)
        {
            uint count = AddressSize32 ? State.Ecx : State.Cx;
            if (count == 0)
            {
                // The count was already exhausted: REP with CX = 0 does
                // nothing at all, not even one iteration.
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

            // INS: reads from the port in DX and stores at ES:DI. As with the
            // other string instructions the destination takes no segment
            // override.
            case 0x6C:   // INSB
            case 0x6D:   // INSW/INSD
                CheckIoPermission(State.Dx, byteOperand ? 1 : size);
                if (byteOperand) WriteMem8(SegReg.Es, DestinationIndex, _io.ReadByte(State.Dx));
                else if (size == 2) WriteMem16(SegReg.Es, DestinationIndex, _io.ReadWord(State.Dx));
                else WriteMem32(SegReg.Es, DestinationIndex, _io.ReadDword(State.Dx));
                AdvanceIndex(ref DestinationIndexRef, delta);
                break;

            // OUTS: the other direction, from DS:SI out to the port. Here the
            // source segment does accept an override.
            case 0x6E:   // OUTSB
            case 0x6F:   // OUTSW/OUTSD
                CheckIoPermission(State.Dx, byteOperand ? 1 : size);
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

        // Decrement the count and decide whether to go round again.
        uint remaining;
        if (AddressSize32) remaining = --State.Ecx;
        else { State.Cx = (ushort)(State.Cx - 1); remaining = State.Cx; }

        if (remaining == 0) return;

        // CMPS and SCAS compare, so they also honour ZF: REPE stops when the
        // comparison fails, REPNE when it succeeds. The other string
        // instructions ignore ZF entirely and only ever count down.
        bool comparison = opcode is 0xA6 or 0xA7 or 0xAE or 0xAF;
        if (comparison)
        {
            if (_prefixes.Rep && !Flags.Zero) return;      // REPE: leave when ZF=0
            if (_prefixes.RepNe && Flags.Zero) return;     // REPNE: leave when ZF=1
        }

        // Rewind EIP to the start of the instruction: the next Step() will run
        // it again, having given pending interrupts a chance to come through
        // in between.
        State.Eip = _instructionStart;
    }

    // The source and destination indices are SI/DI or ESI/EDI depending on the
    // address size in effect -- not on the operand size.

    private uint SourceIndex => AddressSize32 ? State.Esi : State.Si;
    private uint DestinationIndex => AddressSize32 ? State.Edi : State.Di;

    private ref uint SourceIndexRef => ref State.Gpr[(int)Reg.Esi];
    private ref uint DestinationIndexRef => ref State.Gpr[(int)Reg.Edi];

    /// <summary>
    /// Advances an index. At 16-bit address size the increment wraps within the
    /// low 16 bits and leaves the top half of the register alone, so a string
    /// operation running off the end of a segment comes back round to its
    /// start rather than spilling into the next one.
    /// </summary>
    private void AdvanceIndex(ref uint index, int delta)
    {
        if (AddressSize32) index = (uint)(index + delta);
        else index = (index & 0xFFFF0000u) | (ushort)(index + delta);
    }
}
