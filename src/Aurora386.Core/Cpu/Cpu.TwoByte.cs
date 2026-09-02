namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>
    /// Opcode a due byte, preceduti da 0x0F. Sono le istruzioni aggiunte dal
    /// 286 e dal 386: salti condizionati lunghi, SETcc, scorrimenti a doppia
    /// precisione, manipolazione di bit e accesso ai registri di controllo.
    /// </summary>
    private void ExecuteTwoByte(byte opcode)
    {
        switch (opcode)
        {
            // --- salti condizionati con spiazzamento lungo (0x80-0x8F) ------
            case >= 0x80 and <= 0x8F:
            {
                int offset = FetchDisplacement();
                if (TestCondition((byte)(opcode & 0x0F)))
                    JumpRelative(offset);
                return;
            }

            // --- SETcc (0x90-0x9F): scrive 1 o 0 in un byte -----------------
            case >= 0x90 and <= 0x9F:
            {
                var m = DecodeModRm();
                WriteRm8(m, TestCondition((byte)(opcode & 0x0F)) ? (byte)1 : (byte)0);
                return;
            }

            // --- PUSH/POP di FS e GS, aggiunti dal 386 ----------------------
            case 0xA0: PushOperand(State.Fs.Selector); return;
            case 0xA1: LoadSegment(SegReg.Fs, (ushort)PopSegmentOperand()); return;
            case 0xA8: PushOperand(State.Gs.Selector); return;
            case 0xA9: LoadSegment(SegReg.Gs, (ushort)PopSegmentOperand()); return;

            // --- estensioni di segno e di zero (0xB6/0xB7/0xBE/0xBF) --------
            // Sono le istruzioni che rendono efficiente lavorare con byte e
            // word in registri a 32 bit; il codice a 16 bit di DOS non le usa,
            // ma i compilatori a 32 bit le producono di continuo.
            case 0xB6: { var m = DecodeModRm(); WriteRegOperand(m, ReadRm8(m)); return; }
            case 0xB7: { var m = DecodeModRm(); WriteRegOperand(m, ReadRm16(m)); return; }
            case 0xBE: { var m = DecodeModRm(); WriteRegOperand(m, (uint)(sbyte)ReadRm8(m)); return; }
            case 0xBF: { var m = DecodeModRm(); WriteRegOperand(m, (uint)(short)ReadRm16(m)); return; }

            // --- scorrimenti a doppia precisione (386) ----------------------
            case 0xA4: { var m = DecodeModRm(); ShiftDouble(m, ReadRegOperand(m), FetchByte(), left: true); return; }
            case 0xA5: { var m = DecodeModRm(); ShiftDouble(m, ReadRegOperand(m), State.GetReg8(1), left: true); return; }
            case 0xAC: { var m = DecodeModRm(); ShiftDouble(m, ReadRegOperand(m), FetchByte(), left: false); return; }
            case 0xAD: { var m = DecodeModRm(); ShiftDouble(m, ReadRegOperand(m), State.GetReg8(1), left: false); return; }

            // --- BSF/BSR: cercano il primo bit acceso ------------------------
            case 0xBC: BitScan(forward: true); return;
            case 0xBD: BitScan(forward: false); return;

            // --- gruppo 0F 01: tabelle dei descrittori e parola di stato -----
            // Serve a chi prepara il passaggio in protected mode. HIMEM.SYS lo
            // usa gia' in real mode: senza, il caricamento di DOS si ferma.
            case 0x01:
            {
                var m = DecodeModRm();
                switch (m.Reg)
                {
                    case 0:   // SGDT: limite e base in memoria
                        if (m.IsRegister) throw new CpuException(6);
                        WriteMem16(m.Segment, m.Offset, State.GdtLimit);
                        WriteMem32(m.Segment, m.Offset + 2, State.GdtBase);
                        return;

                    case 1:   // SIDT
                        if (m.IsRegister) throw new CpuException(6);
                        WriteMem16(m.Segment, m.Offset, State.IdtLimit);
                        WriteMem32(m.Segment, m.Offset + 2, State.IdtBase);
                        return;

                    case 2:   // LGDT
                    {
                        if (m.IsRegister) throw new CpuException(6);
                        State.GdtLimit = ReadMem16(m.Segment, m.Offset);
                        uint baseValue = ReadMem32(m.Segment, m.Offset + 2);
                        // Con operandi a 16 bit il byte alto della base viene
                        // ignorato: la tabella sta nel primo megabyte.
                        State.GdtBase = OperandSize32 ? baseValue : baseValue & 0x00FFFFFF;
                        return;
                    }

                    case 3:   // LIDT
                    {
                        if (m.IsRegister) throw new CpuException(6);
                        State.IdtLimit = ReadMem16(m.Segment, m.Offset);
                        uint baseValue = ReadMem32(m.Segment, m.Offset + 2);
                        State.IdtBase = OperandSize32 ? baseValue : baseValue & 0x00FFFFFF;
                        return;
                    }

                    case 4:   // SMSW: i sedici bit bassi di CR0
                        if (m.IsRegister) State.SetReg16(m.Register, (ushort)State.Cr0);
                        else WriteMem16(m.Segment, m.Offset, (ushort)State.Cr0);
                        return;

                    case 6:   // LMSW: scrive i bit bassi di CR0, ma PE non si spegne
                    {
                        ushort valore = ReadRm16(m);
                        uint nuovo = (State.Cr0 & 0xFFFFFFF0) | (uint)(valore & 0x0F);
                        // Una volta acceso, il bit di protezione non si spegne
                        // con LMSW: serve un reset.
                        if ((State.Cr0 & CpuState.Cr0ProtectionEnable) != 0)
                            nuovo |= CpuState.Cr0ProtectionEnable;
                        WriteCr0(nuovo);
                        return;
                    }

                    default:
                        throw new CpuException(6);
                }
            }

            // --- CLTS: azzera il flag "task switched" di CR0 -----------------
            case 0x06: State.Cr0 &= ~CpuState.Cr0TaskSwitched; return;

            // --- istruzioni sui bit -----------------------------------------
            // Nella forma /r l'indice del bit e' un registro con segno, e su
            // memoria puo' puntare ben oltre l'operando indirizzato: il 386 lo
            // usa per indicizzare vettori di bit, quindi l'indirizzo effettivo
            // va spostato di indice/dimensione parole.
            case 0xA3: BitOperation(BitOp.Test, register: true); return;
            case 0xAB: BitOperation(BitOp.Set, register: true); return;
            case 0xB3: BitOperation(BitOp.Reset, register: true); return;
            case 0xBB: BitOperation(BitOp.Complement, register: true); return;

            // Gruppo 0F BA: stesse quattro operazioni con indice immediato. Qui
            // l'indice non ha segno e viene ridotto alla dimensione operando,
            // quindi non puo' uscire dall'operando.
            case 0xBA:
            {
                var m = DecodeModRm();
                if (m.Reg < 4) throw new CpuException(6);
                BitOperationImmediate(m, (BitOp)(m.Reg - 4));
                return;
            }

            // --- IMUL a due operandi: reg = reg * r/m (386) ------------------
            // Stessa logica della forma con immediato: il secondo fattore e'
            // il registro di destinazione invece di una costante.
            case 0xAF:
            {
                var m = DecodeModRm();
                ImulImmediate(m, ReadRegOperand(m));
                return;
            }

            // --- caricamento di puntatori lontani con i segmenti del 386 -----
            case 0xB2: LoadFarPointer(SegReg.Ss); return;
            case 0xB4: LoadFarPointer(SegReg.Fs); return;
            case 0xB5: LoadFarPointer(SegReg.Gs); return;

            // --- accesso ai registri di controllo ---------------------------
            case 0x20:
            {
                var m = DecodeModRm();
                State.SetReg32(m.Register, m.Reg switch
                {
                    0 => State.Cr0,
                    2 => State.Cr2,
                    3 => State.Cr3,
                    _ => 0,
                });
                return;
            }
            case 0x22:
            {
                var m = DecodeModRm();
                uint value = State.GetReg32(m.Register);
                switch (m.Reg)
                {
                    case 0: WriteCr0(value); break;
                    case 2: State.Cr2 = value; break;
                    case 3: State.Cr3 = value; break;
                }
                return;
            }

            // --- accesso ai registri di debug -------------------------------
            // Non c'e' hardware di breakpoint dietro, ma i valori vanno
            // conservati: chi li scrive si aspetta di rileggerli uguali.
            case 0x21:
            {
                var m = DecodeModRm();
                State.SetReg32(m.Register, State.Dr[m.Reg]);
                return;
            }
            case 0x23:
            {
                var m = DecodeModRm();
                State.Dr[m.Reg] = State.GetReg32(m.Register);
                return;
            }

            // --- aggancio agli handler nativi -------------------------------
            // 0x0F 0xFF non e' un opcode del 386: viene usato qui come punto
            // di ingresso per le funzioni BIOS realizzate in C#. Il byte che
            // segue identifica quale handler chiamare. E' lo stesso
            // meccanismo delle callback di DOSBox: molto piu' veloce e piu'
            // facile da correggere che scrivere il BIOS in assembler.
            case 0xFF:
            {
                byte handlerId = FetchByte();
                if (NativeHandler is null) throw new CpuException(6);
                NativeHandler(this, handlerId);
                return;
            }

            default:
                // Opcode a due byte non riconosciuto: si segnala con 0x0F00
                // sommato al secondo byte, per distinguerlo da quelli a un byte.
                UnknownTwoByteOpcode?.Invoke(opcode, _instructionStart, State.Cs.Selector);
                throw new CpuException(6);
        }
    }

    /// <summary>Segnalato per gli opcode 0x0F non riconosciuti.</summary>
    public event Action<byte, uint, ushort>? UnknownTwoByteOpcode;

    /// <summary>
    /// Handler delle chiamate native, installato dalla macchina emulata.
    /// Riceve la CPU e l'identificativo codificato dopo l'opcode.
    /// </summary>
    public Action<Cpu, byte>? NativeHandler { get; set; }

    /// <summary>
    /// BSF/BSR: trovano la posizione del primo bit a 1, dal basso o dall'alto.
    /// Con operando zero il risultato e' indefinito sul ferro reale e ZF viene
    /// posto a 1; il registro di destinazione resta invariato.
    /// </summary>
    private void BitScan(bool forward)
    {
        var m = DecodeModRm();
        uint value = ReadRm(m);

        if (value == 0)
        {
            Flags.Zero = true;
            return;
        }

        int position = forward
            ? System.Numerics.BitOperations.TrailingZeroCount(value)
            : 31 - System.Numerics.BitOperations.LeadingZeroCount(value);

        WriteRegOperand(m, (uint)position);
        Flags.Zero = false;
    }

    /// <summary>Le quattro operazioni del gruppo di bit, nell'ordine del campo reg.</summary>
    private enum BitOp { Test = 0, Set = 1, Reset = 2, Complement = 3 }

    /// <summary>
    /// BT/BTS/BTR/BTC con indice in un registro. Su memoria l'indice e' con
    /// segno e non e' limitato alla dimensione dell'operando: l'indirizzo
    /// effettivo scorre di indice/16 word (o /32 dword), cosi' che un vettore
    /// di bit lungo si indirizzi con una sola istruzione.
    /// </summary>
    private void BitOperation(BitOp operation, bool register)
    {
        var m = DecodeModRm();
        int width = OperandSize32 ? 32 : 16;
        // L'indice e' con segno: a 16 bit va esteso, altrimenti un valore come
        // 0xC386 verrebbe letto come +50054 invece che -15482 e l'indirizzo
        // effettivo scorrerebbe dalla parte sbagliata.
        int index = OperandSize32 ? (int)ReadRegOperand(m)
                                  : (short)ReadRegOperand(m);

        if (m.IsRegister)
        {
            // Su registro non c'e' nessun indirizzo da spostare: l'indice
            // viene semplicemente ridotto modulo la dimensione.
            uint value = ReadRm(m);
            int bit = index & (width - 1);
            ApplyBit(m, operation, value, bit);
            return;
        }

        // La divisione dev'essere quella che arrotonda verso il basso, non
        // verso lo zero: con indice negativo l'indirizzo scende. Il conto sta
        // in long perche' con indice 0x80000000 la sottrazione traboccherebbe.
        long indiceLungo = index;
        long word = indiceLungo >= 0 ? indiceLungo / width
                                     : (indiceLungo - width + 1) / width;
        int bitInWord = (int)(indiceLungo - word * width);

        uint offset = (uint)(m.Offset + word * (width / 8));
        // L'indirizzo avvolge nei 16 bit solo quando anche il calcolo
        // dell'indirizzo e' a 16 bit: col prefisso 67 resta a 32.
        if (!AddressSize32) offset &= 0xFFFF;

        ModRm target = m;
        target.Offset = offset;
        ApplyBit(target, operation, ReadRm(target), bitInWord);
    }

    /// <summary>Gruppo 0F BA: l'indice immediato non esce mai dall'operando.</summary>
    private void BitOperationImmediate(in ModRm m, BitOp operation)
    {
        int width = OperandSize32 ? 32 : 16;
        int bit = FetchByte() & (width - 1);
        ApplyBit(m, operation, ReadRm(m), bit);
    }

    /// <summary>
    /// Copia il bit selezionato in CF e, tranne che per BT, riscrive
    /// l'operando. Gli altri flag aritmetici restano quelli di prima.
    /// </summary>
    private void ApplyBit(in ModRm m, BitOp operation, uint value, int bit)
    {
        uint mask = 1u << bit;
        Flags.Carry = (value & mask) != 0;

        uint updated = operation switch
        {
            BitOp.Set => value | mask,
            BitOp.Reset => value & ~mask,
            BitOp.Complement => value ^ mask,
            _ => value,
        };

        if (operation != BitOp.Test) WriteRm(m, updated);
    }

    /// <summary>
    /// Scrittura di CR0. E' il registro che accende protezione e paging: il
    /// passaggio di PE da 0 a 1 e' la transizione in protected mode, quella
    /// che Windows 3.1 compie all'avvio.
    /// </summary>
    private void WriteCr0(uint value)
    {
        State.Cr0 = value;
    }
}
