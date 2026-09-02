using System.Runtime.CompilerServices;

namespace Emulator.Core.Cpu;

public sealed partial class Cpu
{
    // --- accumulatore alla dimensione corrente -------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadAccumulator() => OperandSize32 ? State.Eax : State.Ax;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteAccumulator(uint value)
    {
        if (OperandSize32) State.Eax = value; else State.Ax = (ushort)value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadMemOperand(SegReg segment, uint offset)
        => OperandSize32 ? ReadMem32(segment, offset) : ReadMem16(segment, offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteMemOperand(SegReg segment, uint offset, uint value)
    {
        if (OperandSize32) WriteMem32(segment, offset, value);
        else WriteMem16(segment, offset, (ushort)value);
    }

    /// <summary>Dimensione degli operandi in bit, per i flag.</summary>
    private byte OperandBits => OperandSize32 ? (byte)32 : (byte)16;

    // --- operazioni ALU ------------------------------------------------------

    /// <summary>
    /// Le otto operazioni ALU condividono la struttura: cambia solo il calcolo
    /// e il tipo di flag. L'indice corrisponde ai bit 3-5 dell'opcode.
    /// </summary>
    private uint AluOperate(int operation, uint left, uint right, byte size)
    {
        bool carryIn = false;
        uint result;
        FlagOp flagOp;

        switch (operation)
        {
            case 0: result = left + right; flagOp = FlagOp.Add; break;
            case 1: result = left | right; flagOp = FlagOp.Logic; break;
            case 2:
                carryIn = Flags.Carry;
                result = left + right + (carryIn ? 1u : 0u);
                flagOp = FlagOp.Adc;
                break;
            case 3:
                carryIn = Flags.Carry;
                result = left - right - (carryIn ? 1u : 0u);
                flagOp = FlagOp.Sbb;
                break;
            case 4: result = left & right; flagOp = FlagOp.Logic; break;
            case 5: result = left - right; flagOp = FlagOp.Sub; break;
            case 6: result = left ^ right; flagOp = FlagOp.Logic; break;
            default: result = left - right; flagOp = FlagOp.Sub; break;   // CMP
        }

        Flags.Set(flagOp, left, right, result, size, carryIn);
        return result;
    }

    /// <summary>
    /// Una delle sei forme di un'operazione ALU:
    /// 0: r/m8, r8   1: r/m, r   2: r8, r/m8   3: r, r/m   4: AL, imm8   5: eAX, imm
    /// </summary>
    private void ExecuteAluGroup(int operation, int form)
    {
        bool isCompare = operation == 7;   // CMP scarta il risultato

        switch (form)
        {
            case 0:
            {
                var m = DecodeModRm();
                uint r = AluOperate(operation, ReadRm8(m), State.GetReg8(m.Reg), 8);
                if (!isCompare) WriteRm8(m, (byte)r);
                return;
            }
            case 1:
            {
                var m = DecodeModRm();
                uint r = AluOperate(operation, ReadRm(m), ReadRegOperand(m), OperandBits);
                if (!isCompare) WriteRm(m, r);
                return;
            }
            case 2:
            {
                var m = DecodeModRm();
                uint r = AluOperate(operation, State.GetReg8(m.Reg), ReadRm8(m), 8);
                if (!isCompare) State.SetReg8(m.Reg, (byte)r);
                return;
            }
            case 3:
            {
                var m = DecodeModRm();
                uint r = AluOperate(operation, ReadRegOperand(m), ReadRm(m), OperandBits);
                if (!isCompare) WriteRegOperand(m, r);
                return;
            }
            case 4:
            {
                uint r = AluOperate(operation, State.GetReg8(0), FetchByte(), 8);
                if (!isCompare) State.SetReg8(0, (byte)r);
                return;
            }
            default:
            {
                uint r = AluOperate(operation, ReadAccumulator(), FetchImmediate(), OperandBits);
                if (!isCompare) WriteAccumulator(r);
                return;
            }
        }
    }

    /// <summary>Gruppo 0x80: operazione ALU su r/m8 con immediato a 8 bit.</summary>
    private void AluImmediate8()
    {
        var m = DecodeModRm();
        byte immediate = FetchByte();
        uint result = AluOperate(m.Reg, ReadRm8(m), immediate, 8);
        if (m.Reg != 7) WriteRm8(m, (byte)result);
    }

    /// <summary>
    /// Gruppi 0x81/0x83. Il secondo prende un immediato a 8 bit esteso con
    /// segno alla dimensione degli operandi: e' la forma compatta usata per le
    /// costanti piccole, molto frequente nel codice reale.
    /// </summary>
    private void AluImmediate(bool signExtendedByte)
    {
        var m = DecodeModRm();
        uint immediate = signExtendedByte ? (uint)(sbyte)FetchByte() : FetchImmediate();
        uint result = AluOperate(m.Reg, ReadRm(m), immediate, OperandBits);
        if (m.Reg != 7) WriteRm(m, result);
    }

    private void SetLogicFlags(uint result) => Flags.Set(FlagOp.Logic, 0, 0, result, OperandBits);
    private void SetLogicFlags8(byte result) => Flags.Set(FlagOp.Logic, 0, 0, result, 8);

    // --- INC/DEC -------------------------------------------------------------

    private void IncReg(int index)
    {
        if (OperandSize32)
        {
            uint value = State.Gpr[index] + 1;
            Flags.Set(FlagOp.Inc, State.Gpr[index], 1, value, 32);
            State.Gpr[index] = value;
        }
        else
        {
            ushort value = (ushort)(State.GetReg16(index) + 1);
            Flags.Set(FlagOp.Inc, State.GetReg16(index), 1, value, 16);
            State.SetReg16(index, value);
        }
    }

    private void DecReg(int index)
    {
        if (OperandSize32)
        {
            uint value = State.Gpr[index] - 1;
            Flags.Set(FlagOp.Dec, State.Gpr[index], 1, value, 32);
            State.Gpr[index] = value;
        }
        else
        {
            ushort value = (ushort)(State.GetReg16(index) - 1);
            Flags.Set(FlagOp.Dec, State.GetReg16(index), 1, value, 16);
            State.SetReg16(index, value);
        }
    }

    // --- PUSHA/POPA ----------------------------------------------------------

    private void Pusha()
    {
        // Il valore di SP salvato e' quello *prima* delle push, non quello che
        // si ha quando tocca a lui.
        uint originalSp = State.Esp;

        if (OperandSize32)
        {
            Push32(State.Eax); Push32(State.Ecx); Push32(State.Edx); Push32(State.Ebx);
            Push32(originalSp); Push32(State.Ebp); Push32(State.Esi); Push32(State.Edi);
        }
        else
        {
            Push16(State.Ax); Push16(State.Cx); Push16(State.Dx); Push16(State.Bx);
            Push16((ushort)originalSp); Push16(State.Bp); Push16(State.Si); Push16(State.Di);
        }
    }

    private void Popa()
    {
        // POPA legge un blocco unico di 16 o 32 byte: il processore ne
        // verifica l'estensione prima di cominciare, quindi un blocco che
        // sfora il limite fallisce subito, senza avvolgere come farebbe una
        // singola pop.
        // Vale per la forma a 32 bit: quella a 16 usa pop ordinarie, che
        // seguono le regole delle singole letture.
        if (!State.Ss.Big && OperandSize32)
            CheckLimit(SegReg.Ss, State.Sp, 32u);

        if (OperandSize32)
        {
            State.Edi = Pop32(); State.Esi = Pop32(); State.Ebp = Pop32();
            // Il valore di ESP impilato non viene ignorato del tutto: la sua
            // meta' alta finisce comunque nel registro, e solo la meta' bassa
            // resta quella prodotta dalle pop. Con SS a 16 bit e' l'unico modo
            // di spiegare l'ESP finale osservato sul 386.
            uint impilato = Pop32();
            if (!State.Ss.Big)
            {
                ushort sp = State.Sp;
                State.Esp = (impilato & 0xFFFF0000u) | sp;
            }
            State.Ebx = Pop32(); State.Edx = Pop32(); State.Ecx = Pop32(); State.Eax = Pop32();
        }
        else
        {
            State.Di = Pop16(); State.Si = Pop16(); State.Bp = Pop16();
            Pop16();
            State.Bx = Pop16(); State.Dx = Pop16(); State.Cx = Pop16(); State.Ax = Pop16();
        }
    }

    // --- flag ----------------------------------------------------------------

    // PUSHFD impila i flag su 32 bit, ma i bit 16-31 escono azzerati: il 386
    // non ha ancora nulla da metterci (VM e RF arrivano col 486).
    private void Pushf() => PushOperand(OperandSize32 ? Flags.Value & 0x0000FFFFu
                                                      : Flags.Value & 0xFFFFu);

    private void Popf()
    {
        uint value = PopOperand();
        // In real mode tutti i flag sono scrivibili; in protected mode IOPL e
        // IF sono protetti dal livello di privilegio, cosa che andra' gestita
        // insieme al resto della protezione.
        Flags.SetValue(OperandSize32 ? value : (Flags.Value & 0xFFFF0000) | (value & 0xFFFF));
    }

    private void Sahf()
    {
        // SAHF carica AH nel byte basso di EFLAGS, preservando i bit riservati.
        uint value = (Flags.Value & 0xFFFFFF00) | State.GetReg8(4);
        Flags.SetValue(value);
    }

    private void Lahf() => State.SetReg8(4, (byte)(Flags.Value & 0xFF));

    // --- estensioni di segno -------------------------------------------------

    /// <summary>CBW/CWDE: estende AL in AX, o AX in EAX.</summary>
    private void Cbw()
    {
        if (OperandSize32) State.Eax = (uint)(short)State.Ax;
        else State.Ax = (ushort)(sbyte)State.GetReg8(0);
    }

    /// <summary>CWD/CDQ: estende il segno di AX in DX:AX, o di EAX in EDX:EAX.</summary>
    private void Cwd()
    {
        if (OperandSize32) State.Edx = (State.Eax & 0x80000000) != 0 ? 0xFFFFFFFF : 0;
        else State.Dx = (ushort)((State.Ax & 0x8000) != 0 ? 0xFFFF : 0);
    }

    // --- indirizzi e salti ---------------------------------------------------

    private void Lea()
    {
        var m = DecodeModRm();
        // LEA calcola l'indirizzo senza accedere alla memoria; con mod == 3
        // non ha senso e sul ferro reale e' un'istruzione non valida.
        if (m.IsRegister) throw new CpuException(6);
        WriteRegOperand(m, m.Offset);
    }

    /// <summary>
    /// Salto relativo. In modalita' a 16 bit EIP viene troncato a 16 bit: e'
    /// cio' che fa avvolgere l'esecuzione dentro il segmento invece di uscirne.
    /// </summary>
    private void JumpRelative(int offset)
    {
        uint target = (uint)(State.Eip + offset);
        State.Eip = OperandSize32 ? target : target & 0xFFFF;
    }

    /// <summary>
    /// CALL lontana diretta (0x9A): l'indirizzo completo segmento:offset e'
    /// codificato nell'istruzione. Impila CS e IP di ritorno prima di saltare,
    /// cosi' una RETF puo' tornare indietro.
    ///
    /// E' il modo in cui il codice a 16 bit chiama routine fuori dal proprio
    /// segmento, quindi DOS la usa di continuo per le chiamate interne.
    /// </summary>
    private void CallFar()
    {
        uint offset = FetchImmediate();
        ushort selector = FetchWord();

        // L'ordine di impilamento e' fissato dall'architettura: prima il
        // segmento, poi l'offset, cosi' la RETF li estrae al contrario.
        PushOperand(State.Cs.Selector);
        PushOperand(State.Eip);

        LoadSegment(SegReg.Cs, selector);
        State.Eip = OperandSize32 ? offset : offset & 0xFFFF;
    }

    /// <summary>
    /// XLAT (0xD7): sostituisce AL con il byte che si trova in DS:[BX + AL].
    ///
    /// E' una traduzione tramite tabella in una sola istruzione, usata per
    /// convertire caratteri: maiuscole/minuscole, pagine di codici, set di
    /// caratteri nazionali. DOS ne fa un uso intensissimo.
    /// </summary>
    private void Xlat()
    {
        SegReg segment = EffectiveSegment(SegReg.Ds);
        uint index = AddressSize32
            ? State.Ebx + State.GetReg8(0)
            : (uint)(ushort)(State.Bx + State.GetReg8(0));

        State.SetReg8(0, ReadMem8(segment, index));
    }

    private void JumpFar()
    {
        uint offset = FetchImmediate();
        ushort selector = FetchWord();
        LoadSegment(SegReg.Cs, selector);
        State.Eip = OperandSize32 ? offset : offset & 0xFFFF;
    }

    /// <summary>
    /// Limite che avrebbe il segmento caricando questo selettore. In real mode
    /// e' sempre 64 KB meno uno, ma serve saperlo prima di toccare CS.
    /// </summary>
    private static uint LimiteRealMode(ushort selector) => 0xFFFF;

    private void RetFar(ushort popBytes)
    {
        uint espPrima = State.Esp;
        try
        {
            // Solo la forma a 32 bit legge il blocco in un colpo solo.
            if (OperandSize32) ControllaBloccoStack(8u);
            uint offset = PopOperand();
            ushort selector = (ushort)PopOperand();
            uint destinazione = OperandSize32 ? offset : offset & 0xFFFF;

            // Il limite si controlla sul segmento che si sta per caricare, ma
            // prima di caricarlo davvero: se l'indirizzo non ci sta, CS deve
            // restare quello di partenza, perche' e' quello che finisce nel
            // frame dell'eccezione.
            uint limiteNuovo = LimiteRealMode(selector);
            if (destinazione > limiteNuovo) throw new CpuException(13, 0);

            LoadSegment(SegReg.Cs, selector);
            State.Eip = destinazione;
        }
        catch (CpuException)
        {
            // Lo stack torna com'era: il gestore deve vedere il puntatore che
            // aveva chi ha eseguito la RET, non uno a meta' dell'operazione.
            State.Esp = espPrima;
            throw;
        }
        AdjustStack(popBytes);
    }

    /// <summary>Rimuove dallo stack i byte richiesti da RET n.</summary>
    private void AdjustStack(ushort bytes)
    {
        if (bytes == 0) return;
        if (State.Ss.Big) State.Esp += bytes;
        else State.Sp += bytes;
    }

    private void Leave()
    {
        // LEAVE annulla ENTER: sposta EBP in ESP e ripristina EBP.
        uint espPrima = State.Esp;
        try
        {
            if (State.Ss.Big) State.Esp = State.Ebp; else State.Sp = State.Bp;
            if (OperandSize32) State.Ebp = Pop32(); else State.Bp = Pop16();
        }
        catch (CpuException) { State.Esp = espPrima; throw; }
    }

    /// <summary>
    /// Carica un registro di segmento. In real mode e' base = selettore * 16;
    /// in protected mode passera' per il descrittore.
    ///
    /// Caricare SS inibisce le interruzioni per l'istruzione successiva. E'
    /// una garanzia dell'architettura, non un dettaglio: lo stack si cambia
    /// con due istruzioni (MOV SS,.. seguito da MOV SP,..) e fra le due
    /// SS:SP indica un'area che non esiste. Un'interruzione in quel punto
    /// impilerebbe il proprio contesto in un posto arbitrario, corrompendo
    /// la memoria e lasciando lo stack del chiamante in uno stato che il
    /// codice interrotto non si aspetta.
    /// </summary>
    private void LoadSegment(SegReg segment, ushort selector)
    {
        if (segment == SegReg.Ss) _interruptShadow = true;

        if (!State.ProtectedMode)
        {
            State.LoadSegmentRealMode(segment, selector);
            return;
        }
        LoadSegmentProtected(segment, selector);
    }

    // --- condizioni dei salti -------------------------------------------------

    /// <summary>
    /// Valuta la condizione di un Jcc/SETcc/CMOVcc. Il bit 0 del codice inverte
    /// la condizione, per questo i casi vanno a coppie.
    /// </summary>
    private bool TestCondition(byte condition) => condition switch
    {
        0x0 => Flags.Overflow,
        0x1 => !Flags.Overflow,
        0x2 => Flags.Carry,                                  // below
        0x3 => !Flags.Carry,                                 // above or equal
        0x4 => Flags.Zero,
        0x5 => !Flags.Zero,
        0x6 => Flags.Carry || Flags.Zero,                    // below or equal
        0x7 => !Flags.Carry && !Flags.Zero,                  // above
        0x8 => Flags.Sign,
        0x9 => !Flags.Sign,
        0xA => Flags.Parity,
        0xB => !Flags.Parity,
        0xC => Flags.Sign != Flags.Overflow,                 // less
        0xD => Flags.Sign == Flags.Overflow,                 // greater or equal
        0xE => Flags.Zero || Flags.Sign != Flags.Overflow,   // less or equal
        _ => !Flags.Zero && Flags.Sign == Flags.Overflow,    // greater
    };

    // --- interruzioni ---------------------------------------------------------

    /// <summary>
    /// INT n. In real mode il vettore si legge dalla tabella a indirizzo 0:
    /// quattro byte per vettore, offset e poi segmento.
    /// </summary>
    private void SoftwareInterrupt(byte vector) => EnterInterrupt(vector, isSoftware: true);

    /// <summary>Eccezione generata dal processore.</summary>
    private void RaiseException(byte vector, uint errorCode)
    {
        // In protected mode certe eccezioni impilano anche un codice di errore;
        // in real mode non esiste e viene ignorato.
        EnterInterrupt(vector, isSoftware: false, errorCode);
    }

    private void EnterInterrupt(byte vector, bool isSoftware, uint errorCode = 0)
    {
        if (State.ProtectedMode)
        {
            EnterInterruptProtected(vector, errorCode);
            return;
        }

        // Real mode: salva FLAGS, CS e IP, poi salta al vettore.
        Push16((ushort)Flags.Value);
        Push16(State.Cs.Selector);
        Push16((ushort)State.Eip);

        Flags.Interrupt = false;   // le interruzioni sono disabilitate nel gestore
        Flags.Trap = false;

        uint tableEntry = (uint)vector * 4;
        ushort offset = _memory.ReadWord(tableEntry);
        ushort segment = _memory.ReadWord(tableEntry + 2);

        State.LoadSegmentRealMode(SegReg.Cs, segment);
        State.Eip = offset;
    }

    private void Iret()
    {
        if (State.ProtectedMode)
        {
            IretProtected();
            return;
        }

        // IRET ripristina l'intero registro dei flag, IF compreso: e' cosi' che
        // le interruzioni si riabilitano all'uscita dal gestore. Con il
        // prefisso di dimensione ogni voce impilata occupa quattro byte, e il
        // selettore di CS ne spreca due.
        uint espPrima = State.Esp;
        try
        {
            if (OperandSize32)
            {
                uint destinazione = Pop32();
                ushort selettore = (ushort)Pop32();
                if (destinazione > LimiteRealMode(selettore)) throw new CpuException(13, 0);
                State.LoadSegmentRealMode(SegReg.Cs, selettore);
                State.Eip = destinazione;
                Flags.SetValue(Pop32());
            }
            else
            {
                uint destinazione = Pop16();
                ushort selettore = Pop16();
                if (destinazione > LimiteRealMode(selettore)) throw new CpuException(13, 0);
                State.LoadSegmentRealMode(SegReg.Cs, selettore);
                State.Eip = destinazione;
                Flags.SetValue((Flags.Value & 0xFFFF0000) | Pop16());
            }
        }
        catch (CpuException) { State.Esp = espPrima; throw; }
    }
}

public sealed partial class Cpu
{
    // --- porte di I/O alla dimensione corrente -------------------------------

    private void ReadPortToAccumulator(ushort port)
    {
        if (OperandSize32) State.Eax = _io.ReadDword(port);
        else State.Ax = _io.ReadWord(port);
    }

    private void WriteAccumulatorToPort(ushort port)
    {
        if (OperandSize32) _io.WriteDword(port, State.Eax);
        else _io.WriteWord(port, State.Ax);
    }

    // --- IMUL a tre operandi --------------------------------------------------

    /// <summary>
    /// IMUL reg, r/m, imm. A differenza della forma a un operando non tocca
    /// DX:AX: il risultato sta tutto nel registro di destinazione, e CF/OF
    /// segnalano se e' stato troncato.
    /// </summary>
    private void ImulImmediate(in ModRm m, uint immediate)
    {
        if (OperandSize32)
        {
            long result = (long)(int)ReadRm32(m) * (int)immediate;
            State.SetReg32(m.Reg, (uint)result);
            bool overflow = result != (int)result;
            Flags.Set(FlagOp.Imul, 0, (uint)(overflow ? 1 : 0), (uint)result, 32);
        }
        else
        {
            int result = (short)ReadRm16(m) * (short)immediate;
            State.SetReg16(m.Reg, (ushort)result);
            bool overflow = result != (short)result;
            Flags.Set(FlagOp.Imul, 0, (uint)(overflow ? 1 : 0), (uint)result, 16);
        }
    }

    // --- puntatori lontani ----------------------------------------------------

    /// <summary>
    /// LES/LDS: caricano da memoria un puntatore completo, offset nel registro
    /// e segmento nel registro di segmento indicato. E' il modo in cui il
    /// codice a 16 bit passa puntatori che escono dai 64 KB di un segmento.
    /// </summary>
    private void LoadFarPointer(SegReg segment)
    {
        var m = DecodeModRm();
        if (m.IsRegister) throw new CpuException(6);

        uint offset = ReadMemOperand(m.Segment, m.Offset);
        // Il selettore segue l'offset, e il suo indirizzo avvolge dentro il
        // segmento: con offset FFFE il selettore si legge a 0000, non oltre
        // la fine. Solo la singola lettura a cavallo del limite e' un errore.
        uint selectorOffset = m.Offset + (OperandSize32 ? 4u : 2u);
        // L'avvolgimento segue la dimensione degli indirizzi in vigore, non
        // il bit B del segmento: col prefisso 67 l'offset resta a 32 bit
        // anche dentro un segmento a 16.
        if (!AddressSize32) selectorOffset &= 0xFFFF;
        ushort selector = ReadMem16(m.Segment, selectorOffset);

        WriteRegOperand(m, offset);
        LoadSegment(segment, selector);
    }

    // --- ENTER ----------------------------------------------------------------

    /// <summary>
    /// ENTER: costruisce il frame di stack di una procedura annidata. Il
    /// livello di annidamento copia i puntatori ai frame esterni, meccanismo
    /// pensato per linguaggi come il Pascal; il codice C usa sempre livello 0.
    /// </summary>
    private void Enter()
    {
        ushort frameSize = FetchWord();
        byte level = (byte)(FetchByte() & 0x1F);

        // Le voci da copiare si leggono a BP calante: il processore verifica
        // che stiano tutte dentro il segmento prima di toccare lo stack, e se
        // una sfora non esegue nemmeno la push iniziale di BP.
        if (!State.Ss.Big && level > 1)
        {
            uint voce = OperandSize32 ? 4u : 2u;
            for (int k = 1; k < level; k++)
                CheckLimit(SegReg.Ss, (uint)(ushort)(State.Bp - voce * (uint)k), voce);
        }

        PushOperand(State.Ebp);
        uint framePointer = State.Ss.Big ? State.Esp : State.Sp;

        for (int i = 1; i < level; i++)
        {
            // Il puntatore da cui si copiano i frame annidati segue la
            // larghezza dello stack, non quella degli operandi: con SS a 16
            // bit resta BP, altrimenti EBP.
            // Il puntatore scende di una voce e poi si copia quello che
            // trova: leggere prima di scendere copierebbe il frame corrente
            // invece di quello che lo racchiude.
            // Prima si verifica che la push ci stia, poi si legge la voce da
            // copiare: quando lo spazio finisce il processore si ferma senza
            // aver scritto niente, e lo stack resta all'iterazione precedente.
            if (OperandSize32)
            {
                if (State.Ss.Big) State.Ebp -= 4; else State.Bp -= 4;
                if (!State.Ss.Big) CheckLimit(SegReg.Ss, (uint)(ushort)(State.Sp - 4), 4);
                uint voce = ReadMem32(SegReg.Ss, State.Ss.Big ? State.Ebp : State.Bp);
                Push32(voce);
            }
            else
            {
                if (State.Ss.Big) State.Ebp -= 2; else State.Bp -= 2;
                if (!State.Ss.Big) CheckLimit(SegReg.Ss, (uint)(ushort)(State.Sp - 2), 2);
                ushort voce = ReadMem16(SegReg.Ss, State.Ss.Big ? State.Ebp : State.Bp);
                Push16(voce);
            }
        }

        if (level > 0) PushOperand(framePointer);

        // Con operandi a 32 bit EBP riceve il puntatore intero, anche quando
        // lo stack e' a 16 bit: in quel caso la meta' alta resta azzerata,
        // perche' framePointer viene da SP.
        if (OperandSize32) State.Ebp = framePointer;
        else State.Bp = (ushort)framePointer;

        if (State.Ss.Big) State.Esp -= frameSize;
        else State.Sp -= frameSize;
    }

    // --- cicli -----------------------------------------------------------------

    /// <summary>
    /// LOOP, LOOPE e LOOPNE: decrementano CX e saltano se non e' zero,
    /// eventualmente anche in base a ZF. Il conteggio usa CX o ECX a seconda
    /// della dimensione degli indirizzi, non di quella degli operandi.
    /// </summary>
    private void LoopCondition(bool zeroRequired, bool useZeroFlag)
    {
        sbyte offset = (sbyte)FetchByte();

        uint remaining;
        if (AddressSize32) remaining = --State.Ecx;
        else { State.Cx = (ushort)(State.Cx - 1); remaining = State.Cx; }

        bool take = remaining != 0;
        if (take && useZeroFlag) take = Flags.Zero == zeroRequired;

        if (take) JumpRelative(offset);
    }

    /// <summary>JCXZ/JECXZ: salta se il contatore e' gia' zero.</summary>
    private void JumpIfCxZero()
    {
        sbyte offset = (sbyte)FetchByte();
        uint count = AddressSize32 ? State.Ecx : State.Cx;
        if (count == 0) JumpRelative(offset);
    }
}
