namespace Emulator.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>
    /// Esegue una singola istruzione: raccoglie i prefissi, legge l'opcode e
    /// lo esegue. Ritorna il numero di cicli consumati.
    /// </summary>
    public int Step()
    {
        if (State.Halted) return 1;

        _instructionStart = State.Eip;
        _prefixes.Clear();

        try
        {
            byte opcode = FetchPrefixesAndOpcode();
            Execute(opcode);
        }
        catch (PageFaultException fault)
        {
            // L'istruzione va riavviata da capo dopo che il gestore ha
            // sistemato la pagina, quindi EIP torna al suo inizio.
            State.Eip = _instructionStart;
            State.Cr2 = fault.LinearAddress;
            RaiseException(14, errorCode: (uint)((fault.Present ? 1 : 0) | (fault.Write ? 2 : 0)));
        }
        catch (CpuException ex)
        {
            State.Eip = _instructionStart;
            RaiseException(ex.Vector, ex.ErrorCode);
        }

        Cycles++;
        return 1;
    }

    /// <summary>
    /// Consuma i prefissi che precedono l'istruzione e ritorna il primo byte
    /// che non lo e'. I prefissi possono essere piu' di uno e in qualsiasi
    /// ordine.
    /// </summary>
    private byte FetchPrefixesAndOpcode()
    {
        while (true)
        {
            byte b = FetchByte();
            switch (b)
            {
                case 0x26: _prefixes.Segment = SegReg.Es; continue;
                case 0x2E: _prefixes.Segment = SegReg.Cs; continue;
                case 0x36: _prefixes.Segment = SegReg.Ss; continue;
                case 0x3E: _prefixes.Segment = SegReg.Ds; continue;
                case 0x64: _prefixes.Segment = SegReg.Fs; continue;
                case 0x65: _prefixes.Segment = SegReg.Gs; continue;
                case 0x66: _prefixes.OperandSize = true; continue;
                case 0x67: _prefixes.AddressSize = true; continue;
                case 0xF0: _prefixes.Lock = true; continue;
                case 0xF2: _prefixes.RepNe = true; continue;
                case 0xF3: _prefixes.Rep = true; continue;
                default: return b;
            }
        }
    }

    /// <summary>
    /// Dice se il prefisso LOCK e' ammesso davanti a questo opcode. La lista
    /// e' quella del manuale: solo le forme che scrivono in memoria il
    /// risultato di una lettura sullo stesso indirizzo. Il byte ModR/M non e'
    /// ancora stato letto, quindi qui si filtra per opcode e il caso
    /// "destinazione in registro" viene escluso subito dopo.
    /// </summary>
    private bool LockAllowed(byte opcode)
    {
        // Le otto operazioni ALU nelle forme con destinazione r/m. CMP
        // (gruppo 0x38) e' esclusa: confronta soltanto, non riscrive nulla,
        // quindi non c'e' niente da rendere atomico.
        if (opcode < 0x38 && (opcode & 0x07) <= 1) return PeekModRmIsMemory();

        // 0x0F apre gli opcode a due byte: la decisione dipende dal secondo,
        // che qui non e' ancora stato consumato.
        if (opcode == 0x0F) return LockAllowedTwoByte(ReadMem8(SegReg.Cs, State.Eip));

        return opcode switch
        {
            // ALU con immediato: il campo reg del ModR/M sceglie
            // l'operazione, e il valore 7 e' di nuovo CMP.
            // 0x82 e' l'alias non documentato di 0x80 e accetta LOCK come lui.
            0x80 or 0x81 or 0x82 or 0x83 => PeekModRmIsMemory() && PeekModRmReg() != 7,
            0x86 or 0x87 => PeekModRmIsMemory(),           // XCHG
            // Del gruppo F6/F7 solo NOT (2) e NEG (3) scrivono in memoria:
            // TEST, MUL e DIV no.
            0xF6 or 0xF7 => PeekModRmIsMemory()
                            && PeekModRmReg() is 2 or 3,
            // Del gruppo FE/FF solo INC (0) e DEC (1): le altre forme sono
            // salti e push, che non riscrivono l'operando.
            0xFE or 0xFF => PeekModRmIsMemory()
                            && PeekModRmReg() is 0 or 1,
            _ => false,
        };
    }

    /// <summary>
    /// Sbircia il byte ModR/M senza consumarlo per sapere se l'operando r/m
    /// e' in memoria: e' il solo caso in cui LOCK ha senso.
    /// </summary>
    /// <summary>
    /// Fra gli opcode a due byte solo le istruzioni di bit che riscrivono
    /// l'operando accettano LOCK: BT si limita a leggere, quindi e' esclusa
    /// come lo sono CMP e TEST fra quelli a un byte.
    /// </summary>
    private bool LockAllowedTwoByte(byte opcode)
    {
        // Il ModR/M viene dopo il secondo byte dell'opcode.
        bool memory = (ReadMem8(SegReg.Cs, State.Eip + 1) >> 6) != 3;
        int reg = (ReadMem8(SegReg.Cs, State.Eip + 1) >> 3) & 7;

        return opcode switch
        {
            0xAB or 0xB3 or 0xBB => memory,          // BTS, BTR, BTC
            0xBA => memory && reg >= 5,              // il gruppo, tranne BT (4)
            _ => false,
        };
    }

    private bool PeekModRmIsMemory()
        => (ReadMem8(SegReg.Cs, State.Eip) >> 6) != 3;

    /// <summary>Campo reg del ModR/M, che in alcuni gruppi sceglie l'operazione.</summary>
    private int PeekModRmReg()
        => (ReadMem8(SegReg.Cs, State.Eip) >> 3) & 7;

    /// <summary>
    /// Dispatch dell'opcode.
    ///
    /// La tabella x86 e' fortemente regolare: i primi 0x40 byte sono otto
    /// operazioni ALU con sei forme ciascuna, disposte in modo che i bit bassi
    /// dell'opcode indichino direzione e dimensione. Sfruttare questa
    /// regolarita' evita centinaia di casi quasi identici, e riduce il codice
    /// caldo abbastanza da tenerlo nella cache istruzioni.
    /// </summary>
    private void Execute(byte opcode)
    {
        // LOCK rende atomico un accesso in memoria, quindi ha senso solo sulle
        // istruzioni che leggono e riscrivono un operando in memoria. Su tutto
        // il resto -- destinazione in un registro, o istruzione che non e'
        // read-modify-write -- il processore rifiuta l'istruzione.
        if (_prefixes.Lock && !LockAllowed(opcode)) throw new CpuException(6);

        // --- 0x00-0x3F: ADD OR ADC SBB AND SUB XOR CMP ---------------------
        // Ogni gruppo di 8 opcode e' una operazione; dentro il gruppo i bit
        // 0-2 selezionano la forma. I due opcode restanti di ogni gruppo
        // (0x06/0x07 ecc.) sono PUSH/POP di segmento, gestiti dopo.
        if (opcode < 0x40)
        {
            int form = opcode & 7;
            if (form < 6)
            {
                ExecuteAluGroup((byte)(opcode >> 3), form);
                return;
            }
        }

        switch (opcode)
        {
            // --- PUSH/POP di segmento (i buchi nella tabella ALU) ----------
            case 0x06: PushOperand(State.Es.Selector); return;
            case 0x07: LoadSegment(SegReg.Es, (ushort)PopSegmentOperand()); return;
            case 0x0E: PushOperand(State.Cs.Selector); return;
            case 0x16: PushOperand(State.Ss.Selector); return;
            case 0x17: LoadSegment(SegReg.Ss, (ushort)PopSegmentOperand()); return;
            case 0x1E: PushOperand(State.Ds.Selector); return;
            case 0x1F: LoadSegment(SegReg.Ds, (ushort)PopSegmentOperand()); return;

            // --- INC/DEC di registro (0x40-0x4F) ---------------------------
            case >= 0x40 and <= 0x47: IncReg(opcode - 0x40); return;
            case >= 0x48 and <= 0x4F: DecReg(opcode - 0x48); return;

            // --- PUSH/POP di registro (0x50-0x5F) --------------------------
            case >= 0x50 and <= 0x57:
                PushOperand(State.Gpr[opcode - 0x50]);
                return;
            case >= 0x58 and <= 0x5F:
            {
                uint value = PopOperand();
                if (OperandSize32) State.SetReg32(opcode - 0x58, value);
                else State.SetReg16(opcode - 0x58, (ushort)value);
                return;
            }

            case 0x60: Pusha(); return;
            case 0x61: Popa(); return;

            // BOUND: verifica che il registro stia fra i due limiti letti da
            // memoria, estremi compresi. Fuori intervallo e' #BR, il vettore 5.
            case 0x62:
            {
                var m = DecodeModRm();
                if (m.IsRegister) throw new CpuException(6);
                if (OperandSize32)
                {
                    int valore = (int)State.GetReg32(m.Reg);
                    int minimo = (int)ReadMem32(m.Segment, m.Offset);
                    int massimo = (int)ReadMem32(m.Segment, m.Offset + 4);
                    if (valore < minimo || valore > massimo) throw new CpuException(5);
                }
                else
                {
                    short valore = (short)State.GetReg16(m.Reg);
                    short minimo = (short)ReadMem16(m.Segment, m.Offset);
                    short massimo = (short)ReadMem16(m.Segment, m.Offset + 2);
                    if (valore < minimo || valore > massimo) throw new CpuException(5);
                }
                return;
            }

            // ARPL: allinea il livello di privilegio di un selettore. Fuori dal
            // protected mode non e' definita e il 386 la rifiuta.
            case 0x63:
            {
                if (!State.ProtectedMode) throw new CpuException(6);
                var m = DecodeModRm();
                ushort destinazione = ReadRm16(m);
                ushort sorgente = State.GetReg16(m.Reg);
                if ((destinazione & 3) < (sorgente & 3))
                {
                    WriteRm16(m, (ushort)((destinazione & ~3) | (sorgente & 3)));
                    Flags.Zero = true;
                }
                else Flags.Zero = false;
                return;
            }

            // --- BCD (i "buchi" ogni 8 opcode nella tabella ALU) -----------
            case 0x27: Daa(); return;
            case 0x2F: Das(); return;
            case 0x37: Aaa(); return;
            case 0x3F: Aas(); return;
            case 0xD4: Aam(); return;
            case 0xD5: Aad(); return;

            // SALC: non documentata da Intel ma presente in tutti i chip.
            // Riempie AL con il carry: FF se acceso, 00 se spento, senza
            // toccare i flag. Serviva a trasformare CF in una maschera.
            case 0xD6: State.SetReg8(0, Flags.Carry ? (byte)0xFF : (byte)0x00); return;

            // --- IMUL con immediato (forme a tre operandi del 186) ---------
            case 0x69:
            {
                var m = DecodeModRm();
                ImulImmediate(m, FetchImmediate());
                return;
            }
            case 0x6B:
            {
                var m = DecodeModRm();
                ImulImmediate(m, (uint)(sbyte)FetchByte());
                return;
            }

            case 0x68: PushOperand(FetchImmediate()); return;
            case 0x6A: PushOperand((uint)(sbyte)FetchByte()); return;

            // --- istruzioni su stringhe -------------------------------------
            case 0x6C: case 0x6D:   // INS
            case 0x6E: case 0x6F:   // OUTS
            case 0xA4: case 0xA5:   // MOVS
            case 0xA6: case 0xA7:   // CMPS
            case 0xAA: case 0xAB:   // STOS
            case 0xAC: case 0xAD:   // LODS
            case 0xAE: case 0xAF:   // SCAS
                ExecuteString(opcode);
                return;

            // --- porte di I/O -----------------------------------------------
            case 0xE4: State.SetReg8(0, _io.ReadByte(FetchByte())); return;
            case 0xE5: ReadPortToAccumulator(FetchByte()); return;
            case 0xE6: _io.WriteByte(FetchByte(), State.GetReg8(0)); return;
            case 0xE7: WriteAccumulatorToPort(FetchByte()); return;
            case 0xEC: State.SetReg8(0, _io.ReadByte(State.Dx)); return;
            case 0xED: ReadPortToAccumulator(State.Dx); return;
            case 0xEE: _io.WriteByte(State.Dx, State.GetReg8(0)); return;
            case 0xEF: WriteAccumulatorToPort(State.Dx); return;

            // --- scorrimenti e rotazioni -------------------------------------
            case 0xC0: { var m = DecodeModRm(); ExecuteShift(m, FetchByte(), true); return; }
            case 0xC1: { var m = DecodeModRm(); ExecuteShift(m, FetchByte(), false); return; }
            case 0xD0: { var m = DecodeModRm(); ExecuteShift(m, 1, true); return; }
            case 0xD1: { var m = DecodeModRm(); ExecuteShift(m, 1, false); return; }
            case 0xD2: { var m = DecodeModRm(); ExecuteShift(m, State.GetReg8(1), true); return; }
            case 0xD3: { var m = DecodeModRm(); ExecuteShift(m, State.GetReg8(1), false); return; }

            // --- gruppi con estensione dell'opcode ---------------------------
            case 0xF6: Group3Byte(); return;
            case 0xF7: Group3Word(); return;
            case 0xFE: Group4(); return;
            case 0xFF: Group5(); return;

            // --- LES/LDS: caricano puntatore lontano in segmento + registro --
            case 0xC4: LoadFarPointer(SegReg.Es); return;
            case 0xC5: LoadFarPointer(SegReg.Ds); return;

            // --- ENTER/LEAVE ---------------------------------------------------
            // WAIT: sincronizza con il coprocessore. Senza 387 installato non
            // c'e' nulla da attendere, ma l'istruzione e' valida.
            case 0x9B: return;

            case 0xC8: Enter(); return;

            // --- cicli e salto su CX ------------------------------------------
            case 0xE0: LoopCondition(zeroRequired: false, useZeroFlag: true); return;
            case 0xE1: LoopCondition(zeroRequired: true, useZeroFlag: true); return;
            case 0xE2: LoopCondition(zeroRequired: false, useZeroFlag: false); return;
            case 0xE3: JumpIfCxZero(); return;

            // --- salti condizionati corti (0x70-0x7F) ----------------------
            case >= 0x70 and <= 0x7F:
            {
                sbyte offset = (sbyte)FetchByte();
                if (TestCondition((byte)(opcode & 0x0F)))
                    JumpRelative(offset);
                return;
            }

            // --- gruppo immediato (0x80-0x83) ------------------------------
            // 0x82 e' un alias non documentato di 0x80: stessa codifica,
            // stesso effetto. Il 386 lo esegue, e del software dell'epoca lo
            // usa, quindi va accettato invece di dare #UD.
            case 0x80 or 0x82: AluImmediate8(); return;
            case 0x81: AluImmediate(signExtendedByte: false); return;
            case 0x83: AluImmediate(signExtendedByte: true); return;

            // --- TEST e XCHG -----------------------------------------------
            case 0x84: { var m = DecodeModRm(); SetLogicFlags8((byte)(ReadRm8(m) & State.GetReg8(m.Reg))); return; }
            case 0x85: { var m = DecodeModRm(); SetLogicFlags(ReadRm(m) & ReadRegOperand(m)); return; }
            case 0x86: { var m = DecodeModRm(); byte t = ReadRm8(m); WriteRm8(m, State.GetReg8(m.Reg)); State.SetReg8(m.Reg, t); return; }
            case 0x87: { var m = DecodeModRm(); uint t = ReadRm(m); WriteRm(m, ReadRegOperand(m)); WriteRegOperand(m, t); return; }

            // --- MOV ---------------------------------------------------------
            case 0x88: { var m = DecodeModRm(); WriteRm8(m, State.GetReg8(m.Reg)); return; }
            case 0x89: { var m = DecodeModRm(); WriteRm(m, ReadRegOperand(m)); return; }
            case 0x8A: { var m = DecodeModRm(); State.SetReg8(m.Reg, ReadRm8(m)); return; }
            case 0x8B: { var m = DecodeModRm(); WriteRegOperand(m, ReadRm(m)); return; }
            case 0x8C:
            {
                var m = DecodeModRm();
                // I codici 6 e 7 non corrispondono ad alcun registro di
                // segmento: il 386 rifiuta l'istruzione.
                if (m.Reg > (int)SegReg.Gs) throw new CpuException(6);
                ushort selettore = State.Segments[m.Reg].Selector;
                // Verso un registro a 32 bit il selettore viene esteso con
                // zeri, non innestato nella meta' bassa: la parte alta va
                // azzerata. In memoria invece si scrivono due soli byte.
                if (m.IsRegister && OperandSize32) State.SetReg32(m.Register, selettore);
                else WriteRm16(m, selettore);
                return;
            }
            case 0x8D: Lea(); return;
            case 0x8E:
            {
                var m = DecodeModRm();
                // CS non si carica cosi': cambiarlo senza cambiare anche IP
                // lascerebbe l'esecuzione a un indirizzo senza senso, quindi
                // serve un salto lontano. I codici 6 e 7 non sono registri.
                if (m.Reg == (int)SegReg.Cs || m.Reg > (int)SegReg.Gs)
                    throw new CpuException(6);
                LoadSegment((SegReg)m.Reg, ReadRm16(m));
                return;
            }
            case 0x8F:
            {
                // La destinazione viene verificata prima che lo stack si
                // muova: se la scrittura sfora il limite del segmento
                // l'eccezione trova ESP ancora intatto, e il gestore vede il
                // puntatore che aveva chi ha eseguito la POP.
                // Anche di questo gruppo esiste solo la forma /0.
                var m = DecodeModRm();
                if (m.Reg != 0) throw new CpuException(6);
                if (!m.IsRegister)
                    CheckLimit(m.Segment, m.Offset, OperandSize32 ? 4u : 2u);

                uint valore = PopOperand();

                // Se la destinazione e' calcolata a partire dal puntatore di
                // stack, l'indirizzo effettivo e' quello di dopo la pop: il
                // ModR/M e' gia' stato decodificato, quindi basta spostarlo.
                ModRm destinazione = m;
                if (!m.IsRegister && m.UsesStackPointer)
                    destinazione.Offset += OperandSize32 ? 4u : 2u;

                WriteRm(destinazione, valore);
                return;
            }

            case 0x90: return;   // NOP (XCHG EAX, EAX)
            case >= 0x91 and <= 0x97:
            {
                int index = opcode - 0x90;
                if (OperandSize32)
                {
                    (State.Gpr[0], State.Gpr[index]) = (State.Gpr[index], State.Gpr[0]);
                }
                else
                {
                    ushort t = State.Ax;
                    State.Ax = State.GetReg16(index);
                    State.SetReg16(index, t);
                }
                return;
            }

            case 0x9A: CallFar(); return;
            case 0xD7: Xlat(); return;

            case 0x98: Cbw(); return;
            case 0x99: Cwd(); return;
            case 0x9C: Pushf(); return;
            case 0x9D: Popf(); return;
            case 0x9E: Sahf(); return;
            case 0x9F: Lahf(); return;

            // --- MOV con indirizzo diretto (moffs) --------------------------
            case 0xA0: State.SetReg8(0, ReadMem8(EffectiveSegment(SegReg.Ds), FetchAddress())); return;
            case 0xA1: WriteAccumulator(ReadMemOperand(EffectiveSegment(SegReg.Ds), FetchAddress())); return;
            case 0xA2: WriteMem8(EffectiveSegment(SegReg.Ds), FetchAddress(), State.GetReg8(0)); return;
            case 0xA3: WriteMemOperand(EffectiveSegment(SegReg.Ds), FetchAddress(), State.Eax); return;

            case 0xA8: SetLogicFlags8((byte)(State.GetReg8(0) & FetchByte())); return;
            case 0xA9: SetLogicFlags(ReadAccumulator() & FetchImmediate()); return;

            // --- MOV immediato in registro (0xB0-0xBF) ----------------------
            case >= 0xB0 and <= 0xB7: State.SetReg8(opcode - 0xB0, FetchByte()); return;
            case >= 0xB8 and <= 0xBF:
            {
                uint value = FetchImmediate();
                if (OperandSize32) State.SetReg32(opcode - 0xB8, value);
                else State.SetReg16(opcode - 0xB8, (ushort)value);
                return;
            }

            // --- ritorni ------------------------------------------------------
            // Il puntatore di stack va ripristinato se l'indirizzo di ritorno
            // sfora il limite del codice: l'eccezione deve trovare lo stack
            // come stava prima della RET, non a meta' strada.
            case 0xC2:
            {
                ushort pop = FetchWord();
                uint espPrima = State.Esp;
                if (OperandSize32) ControllaBloccoStack(4u);
                try { SaltaA(PopOperand()); }
                catch (CpuException) { State.Esp = espPrima; throw; }
                AdjustStack(pop);
                return;
            }
            case 0xC3:
            {
                uint espPrima = State.Esp;
                if (OperandSize32) ControllaBloccoStack(4u);
                try { SaltaA(PopOperand()); }
                catch (CpuException) { State.Esp = espPrima; throw; }
                return;
            }

            // Di questi due gruppi esiste solo la forma /0: il campo reg e'
            // altrimenti privo di significato e il 386 rifiuta l'istruzione.
            case 0xC6:
            {
                var m = DecodeModRm();
                if (m.Reg != 0) throw new CpuException(6);
                WriteRm8(m, FetchByte());
                return;
            }
            case 0xC7:
            {
                var m = DecodeModRm();
                if (m.Reg != 0) throw new CpuException(6);
                WriteRm(m, FetchImmediate());
                return;
            }

            case 0xC9: Leave(); return;
            case 0xCB: RetFar(0); return;
            case 0xCA: { ushort pop = FetchWord(); RetFar(pop); return; }

            case 0xCC: SoftwareInterrupt(3); return;
            case 0xCD: SoftwareInterrupt(FetchByte()); return;
            case 0xCE: if (Flags.Overflow) SoftwareInterrupt(4); return;
            case 0xCF: Iret(); return;

            // --- chiamate e salti ---------------------------------------------
            case 0xE8:
            {
                int offset = FetchDisplacement();
                PushOperand(State.Eip);
                JumpRelative(offset);
                return;
            }
            case 0xE9: JumpRelative(FetchDisplacement()); return;
            case 0xEA: JumpFar(); return;
            case 0xEB: { sbyte offset = (sbyte)FetchByte(); JumpRelative(offset); return; }

            case 0xF4: State.Halted = true; return;
            case 0xF5: Flags.Carry = !Flags.Carry; return;
            case 0xF8: Flags.Carry = false; return;
            case 0xF9: Flags.Carry = true; return;
            case 0xFA: Flags.Interrupt = false; return;
            case 0xFB:
                // L'abilitazione ha effetto dopo la prossima istruzione, non
                // subito: vedi _interruptShadow.
                Flags.Interrupt = true;
                _interruptShadow = true;
                return;
            case 0xFC: Flags.Direction = false; return;
            case 0xFD: Flags.Direction = true; return;

            case 0x0F: ExecuteTwoByte(FetchByte()); return;

            default:
                // Un opcode non riconosciuto e' un'istruzione non valida: sul
                // ferro reale genera l'eccezione 6, e certi programmi la usano
                // deliberatamente per rilevare il tipo di processore. Poiche'
                // pero' un opcode semplicemente non implementato produce lo
                // stesso sintomo, l'evento viene segnalato: senza questo, la
                // diagnosi di "il sistema non parte" richiederebbe di
                // ispezionare a mano ogni salto sospetto.
                UnknownOpcode?.Invoke(opcode, _instructionStart, State.Cs.Selector);
                throw new CpuException(6);
        }
    }

    /// <summary>
    /// Segnalato quando si incontra un opcode non riconosciuto: byte
    /// dell'opcode, offset dell'istruzione e segmento di codice.
    /// </summary>
    public event Action<byte, uint, ushort>? UnknownOpcode;

    /// <summary>Segmento effettivo, tenendo conto di un eventuale override.</summary>
    private SegReg EffectiveSegment(SegReg dflt)
        => _prefixes.Segment ?? dflt;

    /// <summary>Indirizzo diretto, della dimensione degli indirizzi in vigore.</summary>
    private uint FetchAddress() => AddressSize32 ? FetchDword() : FetchWord();

    /// <summary>
    /// Spiazzamento relativo di un salto o di una chiamata, esteso con segno
    /// dalla dimensione degli operandi in vigore. L'estensione va fatta dal
    /// tipo giusto: a 16 bit 0xFFF3 vale -13, non +65523.
    /// </summary>
    /// <summary>
    /// Porta EIP a un nuovo valore verificando che resti dentro il limite del
    /// segmento di codice. Un indirizzo oltre il limite non viene eseguito: il
    /// 386 lo rifiuta con #GP prima ancora di prelevare l'istruzione.
    /// </summary>
    private void SaltaA(uint destinazione)
    {
        if (destinazione > State.Cs.Limit) throw new CpuException(13, 0);
        State.Eip = destinazione;
    }

    /// <summary>
    /// Verifica in anticipo che un blocco di stack ci stia tutto dentro il
    /// limite. Le istruzioni che leggono piu' valori insieme -- i ritorni,
    /// POPA -- controllano l'intera estensione prima di cominciare, e quindi
    /// falliscono invece di avvolgere come farebbe una singola pop.
    /// </summary>
    private void ControllaBloccoStack(uint dimensione)
    {
        if (!State.Ss.Big) CheckLimit(SegReg.Ss, State.Sp, dimensione);
    }

    private int FetchDisplacement()
        => OperandSize32 ? (int)FetchDword() : (short)FetchWord();
}

/// <summary>
/// Eccezione del processore (divisione per zero, opcode non valido, ...).
/// Viene convertita in un vettore di interruzione da <see cref="Cpu.Step"/>.
/// </summary>
public class CpuException(byte vector, uint errorCode = 0)
    : Exception($"Eccezione CPU {vector}")
{
    public byte Vector { get; } = vector;
    public uint ErrorCode { get; } = errorCode;
}
