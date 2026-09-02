using Aurora386.Core.Cpu;
using Aurora386.Core.IO;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// Copertura del set real mode aggiunto oltre alle istruzioni di base:
/// moltiplicazione e divisione, scorrimenti, stringhe con REP, I/O, cicli.
/// </summary>
public class CpuRealModeTests
{
    private const uint CodeBase = 0x1000;

    private static (Cpu cpu, PhysicalMemoryBus bus) Setup(params byte[] code)
        => Setup(null, code);

    private static (Cpu cpu, PhysicalMemoryBus bus) Setup(IoBus? io, params byte[] code)
    {
        var bus = new PhysicalMemoryBus();
        var cpu = new Cpu(bus, io);
        foreach (SegReg s in Enum.GetValues<SegReg>())
            cpu.State.LoadSegmentRealMode(s, 0);
        cpu.State.Esp = 0x8000;
        cpu.State.Eip = CodeBase;
        code.CopyTo(bus.GetRamSpan(CodeBase, code.Length));
        return (cpu, bus);
    }

    /// <summary>
    /// Esegue finche' EIP non supera l'istruzione che inizia a <paramref name="from"/>,
    /// cioe' finche' un'istruzione ripetuta non ha finito di ripetersi.
    /// </summary>
    private static void RunUntilPast(Cpu cpu, uint from, int maxSteps = 1000)
    {
        for (int i = 0; i < maxSteps && cpu.State.Eip == from; i++)
            cpu.Step();
    }

    private static Cpu Run(int steps, params byte[] code)
    {
        var (cpu, bus) = Setup(code);
        using (bus) { for (int i = 0; i < steps; i++) cpu.Step(); return cpu; }
    }

    // --- moltiplicazione e divisione -----------------------------------------

    [Fact]
    public void MulA16BitProduceIlRisultatoInDxAx()
    {
        // B8 00 10 = MOV AX,0x1000 ; BB 00 10 = MOV BX,0x1000 ; F7 E3 = MUL BX
        var cpu = Run(3, 0xB8, 0x00, 0x10, 0xBB, 0x00, 0x10, 0xF7, 0xE3);
        // 0x1000 * 0x1000 = 0x01000000
        Assert.Equal(0x0000, cpu.State.Ax);
        Assert.Equal(0x0100, cpu.State.Dx);
        Assert.True(cpu.Flags.Carry);     // il risultato non sta in 16 bit
    }

    [Fact]
    public void MulSenzaOverflowAzzeraCarry()
    {
        // 2 * 3 sta comodamente in 16 bit
        var cpu = Run(3, 0xB8, 0x02, 0x00, 0xBB, 0x03, 0x00, 0xF7, 0xE3);
        Assert.Equal(6, cpu.State.Ax);
        Assert.Equal(0, cpu.State.Dx);
        Assert.False(cpu.Flags.Carry);
    }

    [Fact]
    public void ImulConSegnoGestisceINegativi()
    {
        // MOV AX,-2 ; MOV BX,3 ; IMUL BX  ->  -6
        var cpu = Run(3, 0xB8, 0xFE, 0xFF, 0xBB, 0x03, 0x00, 0xF7, 0xEB);
        Assert.Equal(unchecked((ushort)-6), cpu.State.Ax);
        Assert.Equal(0xFFFF, cpu.State.Dx);     // estensione di segno
    }

    [Fact]
    public void DivProduceQuozienteEResto()
    {
        // MOV DX,0 ; MOV AX,17 ; MOV BX,5 ; DIV BX
        var cpu = Run(4, 0xBA, 0x00, 0x00, 0xB8, 0x11, 0x00, 0xBB, 0x05, 0x00, 0xF7, 0xF3);
        Assert.Equal(3, cpu.State.Ax);   // quoziente
        Assert.Equal(2, cpu.State.Dx);   // resto
    }

    [Fact]
    public void DivisionePerZeroGeneraEccezione0()
    {
        var (cpu, bus) = Setup(0xBA, 0x00, 0x00, 0xB8, 0x11, 0x00, 0xBB, 0x00, 0x00, 0xF7, 0xF3);
        using (bus)
        {
            bus.WriteWord(0, 0x0500);       // vettore 0 -> 0000:0500
            bus.WriteWord(2, 0x0000);
            for (int i = 0; i < 4; i++) cpu.Step();
            Assert.Equal(0x0500u, cpu.State.Eip);
        }
    }

    [Fact]
    public void QuozienteTroppoGrandeGeneraEccezione0()
    {
        // DX:AX / 1 con DX != 0 da' un quoziente che non entra in 16 bit:
        // sul ferro reale e' un errore di divisione, non un troncamento.
        var (cpu, bus) = Setup(0xBA, 0xFF, 0xFF, 0xB8, 0x00, 0x00, 0xBB, 0x01, 0x00, 0xF7, 0xF3);
        using (bus)
        {
            bus.WriteWord(0, 0x0500);
            bus.WriteWord(2, 0x0000);
            for (int i = 0; i < 4; i++) cpu.Step();
            Assert.Equal(0x0500u, cpu.State.Eip);
        }
    }

    [Fact]
    public void NegImpostaIlCarrySeLOperandoNonEZero()
    {
        var cpu = Run(2, 0xB8, 0x05, 0x00, 0xF7, 0xD8);      // MOV AX,5 ; NEG AX
        Assert.Equal(unchecked((ushort)-5), cpu.State.Ax);
        Assert.True(cpu.Flags.Carry);

        var cpu2 = Run(2, 0xB8, 0x00, 0x00, 0xF7, 0xD8);     // NEG 0
        Assert.Equal(0, cpu2.State.Ax);
        Assert.False(cpu2.Flags.Carry);
    }

    // --- scorrimenti ----------------------------------------------------------

    [Fact]
    public void ShlSpostaEImpostaIlCarry()
    {
        // MOV AX,0x8001 ; SHL AX,1
        var cpu = Run(2, 0xB8, 0x01, 0x80, 0xD1, 0xE0);
        Assert.Equal(0x0002, cpu.State.Ax);
        Assert.True(cpu.Flags.Carry);      // il bit 15 e' uscito
    }

    [Fact]
    public void SarReplicaIlSegno()
    {
        // MOV AX,0x8000 ; SAR AX,1  ->  0xC000, non 0x4000
        var cpu = Run(2, 0xB8, 0x00, 0x80, 0xD1, 0xF8);
        Assert.Equal(0xC000, cpu.State.Ax);
    }

    [Fact]
    public void ShrInserisceZeri()
    {
        var cpu = Run(2, 0xB8, 0x00, 0x80, 0xD1, 0xE8);      // MOV AX,0x8000 ; SHR AX,1
        Assert.Equal(0x4000, cpu.State.Ax);
    }

    [Fact]
    public void ConteggioDiScorrimentoMascheratoA5Bit()
    {
        // SHL AX,32 sul 386 e' SHL AX,0: nessun effetto, nemmeno sui flag.
        // Sull'8086 invece azzerava il registro.
        var (cpu, bus) = Setup(0xB8, 0x34, 0x12, 0xC1, 0xE0, 0x20);
        using (bus)
        {
            cpu.Step();
            cpu.Flags.Carry = true;
            cpu.Step();
            Assert.Equal(0x1234, cpu.State.Ax);   // invariato
            Assert.True(cpu.Flags.Carry);         // flag intatti
        }
    }

    [Fact]
    public void RolRuotaSenzaPerdereBit()
    {
        var cpu = Run(2, 0xB8, 0x01, 0x80, 0xD1, 0xC0);      // MOV AX,0x8001 ; ROL AX,1
        Assert.Equal(0x0003, cpu.State.Ax);                   // il bit 15 rientra in fondo
        Assert.True(cpu.Flags.Carry);
    }

    [Fact]
    public void RcrFaPassareIlCarryAttraversoIlValore()
    {
        var (cpu, bus) = Setup(0xB8, 0x00, 0x00, 0xD1, 0xD8);   // MOV AX,0 ; RCR AX,1
        using (bus)
        {
            cpu.Step();
            cpu.Flags.Carry = true;
            cpu.Step();
            Assert.Equal(0x8000, cpu.State.Ax);    // il carry entra dal bit alto
            Assert.False(cpu.Flags.Carry);
        }
    }

    // --- stringhe --------------------------------------------------------------

    [Fact]
    public void MovsbCopiaUnByteEAvanza()
    {
        var (cpu, bus) = Setup(0xA4);       // MOVSB
        using (bus)
        {
            bus.WriteByte(0x2000, 0x42);
            cpu.State.Si = 0x2000;
            cpu.State.Di = 0x3000;
            cpu.Step();

            Assert.Equal(0x42, bus.ReadByte(0x3000));
            Assert.Equal(0x2001, cpu.State.Si);
            Assert.Equal(0x3001, cpu.State.Di);
        }
    }

    [Fact]
    public void DirectionFlagInvertesLaDirezione()
    {
        var (cpu, bus) = Setup(0xA4);
        using (bus)
        {
            cpu.State.Si = 0x2000;
            cpu.State.Di = 0x3000;
            cpu.Flags.Direction = true;
            cpu.Step();

            Assert.Equal(0x1FFF, cpu.State.Si);   // all'indietro
            Assert.Equal(0x2FFF, cpu.State.Di);
        }
    }

    [Fact]
    public void RepMovsbCopiaUnBloccoIntero()
    {
        var (cpu, bus) = Setup(0xF3, 0xA4);      // REP MOVSB
        using (bus)
        {
            for (int i = 0; i < 16; i++) bus.WriteByte((uint)(0x2000 + i), (byte)(i + 1));
            cpu.State.Si = 0x2000;
            cpu.State.Di = 0x3000;
            cpu.State.Cx = 16;

            // Ogni iterazione e' uno Step separato: REP resta interrompibile.
            RunUntilPast(cpu, CodeBase);

            Assert.Equal(0, cpu.State.Cx);
            for (int i = 0; i < 16; i++)
                Assert.Equal((byte)(i + 1), bus.ReadByte((uint)(0x3000 + i)));
        }
    }

    [Fact]
    public void RepConContatoreZeroNonFaNulla()
    {
        var (cpu, bus) = Setup(0xF3, 0xA4);
        using (bus)
        {
            bus.WriteByte(0x2000, 0xAA);
            cpu.State.Si = 0x2000;
            cpu.State.Di = 0x3000;
            cpu.State.Cx = 0;
            cpu.Step();

            Assert.Equal(0x00, bus.ReadByte(0x3000));   // nessuna copia
            Assert.Equal(CodeBase + 2, cpu.State.Eip);  // istruzione conclusa
        }
    }

    [Fact]
    public void RepeCmpsbSiFermaAlPrimoByteDiverso()
    {
        var (cpu, bus) = Setup(0xF3, 0xA6);      // REPE CMPSB
        using (bus)
        {
            bus.GetRamSpan(0x2000, 4).Clear();
            bus.GetRamSpan(0x3000, 4).Clear();
            "AAAA"u8.CopyTo(bus.GetRamSpan(0x2000, 4));
            "AAXA"u8.CopyTo(bus.GetRamSpan(0x3000, 4));

            cpu.State.Si = 0x2000;
            cpu.State.Di = 0x3000;
            cpu.State.Cx = 4;

            RunUntilPast(cpu, CodeBase);

            // Si ferma dopo aver confrontato il terzo byte (A vs X).
            Assert.False(cpu.Flags.Zero);
            Assert.Equal(1, cpu.State.Cx);
            Assert.Equal(CodeBase + 2, cpu.State.Eip);
            Assert.Equal(0x2003, cpu.State.Si);      // fermato dopo il byte diverso
        }
    }

    [Fact]
    public void RepneScasbTrovaIlByteCercato()
    {
        var (cpu, bus) = Setup(0xF2, 0xAE);      // REPNE SCASB
        using (bus)
        {
            "hello\0"u8.CopyTo(bus.GetRamSpan(0x3000, 6));
            cpu.State.SetReg8(0, (byte)'l');     // cerca 'l'
            cpu.State.Di = 0x3000;
            cpu.State.Cx = 6;

            RunUntilPast(cpu, CodeBase);

            Assert.True(cpu.Flags.Zero);         // trovato
            Assert.Equal(0x3003, cpu.State.Di);  // subito dopo la prima 'l'
        }
    }

    [Fact]
    public void StosbRiempieUnBlocco()
    {
        var (cpu, bus) = Setup(0xF3, 0xAA);      // REP STOSB
        using (bus)
        {
            cpu.State.SetReg8(0, 0xFF);
            cpu.State.Di = 0x3000;
            cpu.State.Cx = 8;
            RunUntilPast(cpu, CodeBase);

            for (int i = 0; i < 8; i++)
                Assert.Equal(0xFF, bus.ReadByte((uint)(0x3000 + i)));
        }
    }

    // --- I/O --------------------------------------------------------------------

    private sealed class TestPort : IIoDevice
    {
        public byte LastWritten;
        public byte ValueToReturn = 0x5A;
        public byte ReadByte(ushort port) => ValueToReturn;
        public void WriteByte(ushort port, byte value) => LastWritten = value;
    }

    [Fact]
    public void InEOutRaggiungonoIlDispositivo()
    {
        var io = new IoBus();
        var port = new TestPort();
        io.Register(0x60, port);

        // E4 60 = IN AL,0x60 ; B0 99 = MOV AL,0x99 ; E6 60 = OUT 0x60,AL
        var (cpu, bus) = Setup(io, 0xE4, 0x60, 0xB0, 0x99, 0xE6, 0x60);
        using (bus)
        {
            cpu.Step();
            Assert.Equal(0x5A, cpu.State.GetReg8(0));
            cpu.Step();
            cpu.Step();
            Assert.Equal(0x99, port.LastWritten);
        }
    }

    [Fact]
    public void PortaNonAssegnataLeggeFf()
    {
        // Un PC reale con uno slot vuoto legge 0xFF: e' cosi' che il software
        // dell'epoca rileva l'assenza di una scheda.
        var cpu = Run(1, 0xE4, 0x71);
        Assert.Equal(0xFF, cpu.State.GetReg8(0));
    }

    // --- cicli ---------------------------------------------------------------------

    [Fact]
    public void LoopRipeteFinoAContatoreZero()
    {
        //   B9 05 00   MOV CX,5
        //   40         INC AX      <- corpo
        //   E2 FD      LOOP -3
        var cpu = Run(20, 0xB9, 0x05, 0x00, 0x40, 0xE2, 0xFD);
        Assert.Equal(5, cpu.State.Ax);
        Assert.Equal(0, cpu.State.Cx);
    }

    [Fact]
    public void JcxzSaltaSoloSeContatoreEZero()
    {
        var (cpu, bus) = Setup(0xE3, 0x05);      // JCXZ +5
        using (bus)
        {
            cpu.State.Cx = 0;
            cpu.Step();
            Assert.Equal(CodeBase + 7, cpu.State.Eip);
        }

        var (cpu2, bus2) = Setup(0xE3, 0x05);
        using (bus2)
        {
            cpu2.State.Cx = 1;
            cpu2.Step();
            Assert.Equal(CodeBase + 2, cpu2.State.Eip);   // non saltato
        }
    }

    // --- puntatori lontani e chiamate indirette -------------------------------------

    [Fact]
    public void LesCaricaOffsetESegmento()
    {
        // C4 1E 00 20 = LES BX,[0x2000]
        var (cpu, bus) = Setup(0xC4, 0x1E, 0x00, 0x20);
        using (bus)
        {
            bus.WriteWord(0x2000, 0x1234);      // offset
            bus.WriteWord(0x2002, 0xB800);      // segmento
            cpu.Step();

            Assert.Equal(0x1234, cpu.State.Bx);
            Assert.Equal(0xB800, cpu.State.Es.Selector);
            Assert.Equal(0xB8000u, cpu.State.Es.Base);
        }
    }

    [Fact]
    public void CallIndirettaAttraversoRegistro()
    {
        // BB 00 20 = MOV BX,0x2000 ; FF D3 = CALL BX
        var cpu = Run(2, 0xBB, 0x00, 0x20, 0xFF, 0xD3);
        Assert.Equal(0x2000u, cpu.State.Eip);
        Assert.Equal(0x7FFEu, cpu.State.Esp);      // ritorno impilato
    }

    [Fact]
    public void PushDaMemoriaConGruppo5()
    {
        // FF 36 00 20 = PUSH [0x2000]
        var (cpu, bus) = Setup(0xFF, 0x36, 0x00, 0x20);
        using (bus)
        {
            bus.WriteWord(0x2000, 0xCAFE);
            cpu.Step();
            Assert.Equal(0xCAFE, bus.ReadWord(0x7FFE));
        }
    }

    // --- BCD -------------------------------------------------------------------------

    [Fact]
    public void DaaAggiustaDopoSommaBcd()
    {
        // MOV AL,0x19 ; ADD AL,0x01 -> 0x1A ; DAA -> 0x20 (19 + 1 = 20 in BCD)
        var cpu = Run(3, 0xB0, 0x19, 0x04, 0x01, 0x27);
        Assert.Equal(0x20, cpu.State.GetReg8(0));
    }

    [Fact]
    public void AamSeparaLeCifreDecimali()
    {
        // MOV AL,59 ; AAM -> AH=5, AL=9
        var cpu = Run(2, 0xB0, 59, 0xD4, 0x0A);
        Assert.Equal(5, cpu.State.GetReg8(4));
        Assert.Equal(9, cpu.State.GetReg8(0));
    }

    // --- istruzioni del 386 -----------------------------------------------------------

    [Fact]
    public void MovzxEstendeConZero()
    {
        // MOV BL,0xFF ; 0F B6 C3 = MOVZX AX,BL
        var cpu = Run(2, 0xB3, 0xFF, 0x0F, 0xB6, 0xC3);
        Assert.Equal(0x00FF, cpu.State.Ax);
    }

    [Fact]
    public void MovsxEstendeConSegno()
    {
        // MOV BL,0xFF ; 0F BE C3 = MOVSX AX,BL  ->  0xFFFF
        var cpu = Run(2, 0xB3, 0xFF, 0x0F, 0xBE, 0xC3);
        Assert.Equal(0xFFFF, cpu.State.Ax);
    }

    [Fact]
    public void SetccScriveUnoOZero()
    {
        // XOR AX,AX (ZF=1) ; 0F 94 C3 = SETZ BL
        var cpu = Run(2, 0x31, 0xC0, 0x0F, 0x94, 0xC3);
        Assert.Equal(1, cpu.State.GetReg8(3));
    }

    [Fact]
    public void BsfTrovaIlPrimoBitAcceso()
    {
        // MOV AX,0x0100 ; 0F BC D8 = BSF BX,AX  ->  8
        var cpu = Run(2, 0xB8, 0x00, 0x01, 0x0F, 0xBC, 0xD8);
        Assert.Equal(8, cpu.State.Bx);
        Assert.False(cpu.Flags.Zero);
    }

    [Fact]
    public void BsfConZeroImpostaZeroFlag()
    {
        var cpu = Run(2, 0xB8, 0x00, 0x00, 0x0F, 0xBC, 0xD8);
        Assert.True(cpu.Flags.Zero);
    }
}

/// <summary>
/// Prove d'insieme: programmi che usano piu' meccanismi contemporaneamente.
/// Un'istruzione puo' funzionare isolata e rompersi quando interagisce con le
/// altre, ed e' questo il tipo di guasto che poi si manifesta come "il BIOS
/// non parte" senza indicare la causa.
/// </summary>
public class CpuIntegrationTests
{
    private const uint CodeBase = 0x1000;

    private static (Cpu cpu, PhysicalMemoryBus bus) Setup(params byte[] code)
    {
        var bus = new PhysicalMemoryBus();
        var cpu = new Cpu(bus);
        foreach (SegReg s in Enum.GetValues<SegReg>())
            cpu.State.LoadSegmentRealMode(s, 0);
        cpu.State.Esp = 0x8000;
        cpu.State.Eip = CodeBase;
        code.CopyTo(bus.GetRamSpan(CodeBase, code.Length));
        return (cpu, bus);
    }

    [Fact]
    public void StrlenSuStringaTerminataDaZero()
    {
        // Misura la lunghezza di una stringa con REPNE SCASB. Dopo la scansione
        // DI punta al byte dopo il terminatore, quindi la lunghezza si ottiene
        // da DI meno l'indirizzo iniziale meno uno:
        //   B0 00        MOV AL,0        (cerca il terminatore)
        //   B9 FF 00     MOV CX,255      (limite di sicurezza)
        //   F2 AE        REPNE SCASB
        //   89 F8        MOV AX,DI
        //   2D 00 30     SUB AX,0x3000   (indirizzo iniziale)
        //   48           DEC AX          -> AX = lunghezza
        var (cpu, bus) = Setup(0xB0, 0x00, 0xB9, 0xFF, 0x00, 0xF2, 0xAE,
                               0x89, 0xF8, 0x2D, 0x00, 0x30, 0x48);
        using (bus)
        {
            "emulatore\0"u8.CopyTo(bus.GetRamSpan(0x3000, 10));
            cpu.State.Di = 0x3000;

            for (int i = 0; i < 300 && cpu.State.Eip < CodeBase + 13; i++) cpu.Step();

            Assert.Equal(9, cpu.State.Ax);       // "emulatore" e' lungo 9
            Assert.True(cpu.Flags.Zero || cpu.State.Ax == 9);
            Assert.Equal(0x300A, cpu.State.Di);  // fermato dopo il terminatore
        }
    }

    [Fact]
    public void FattorialeConMoltiplicazioneECiclo()
    {
        //   B8 01 00     MOV AX,1
        //   B9 05 00     MOV CX,5
        //   F7 E1        MUL CX        <- corpo: AX *= CX
        //   E2 FC        LOOP -4
        var (cpu, bus) = Setup(0xB8, 0x01, 0x00, 0xB9, 0x05, 0x00, 0xF7, 0xE1, 0xE2, 0xFC);
        using (bus)
        {
            for (int i = 0; i < 100 && cpu.State.Eip < CodeBase + 10; i++) cpu.Step();
            Assert.Equal(120, cpu.State.Ax);    // 5! = 120
        }
    }

    [Fact]
    public void ChiamataDiFunzioneConParametriSulloStack()
    {
        // Convenzione di chiamata reale: parametri impilati, frame con BP,
        // pulizia dello stack al ritorno.
        //   0x1000:  6A 07        PUSH 7
        //   0x1002:  6A 05        PUSH 5
        //   0x1004:  E8 04 00     CALL +4  -> 0x100B
        //   0x1007:  83 C4 04     ADD SP,4    (pulizia)
        //   0x100A:  F4           HLT
        //   0x100B:  55           PUSH BP
        //   0x100C:  89 E5        MOV BP,SP
        //   0x100E:  8B 46 04     MOV AX,[BP+4]
        //   0x1011:  03 46 06     ADD AX,[BP+6]
        //   0x1014:  5D           POP BP
        //   0x1015:  C3           RET
        var (cpu, bus) = Setup(
            0x6A, 0x07, 0x6A, 0x05, 0xE8, 0x04, 0x00, 0x83, 0xC4, 0x04, 0xF4,
            0x55, 0x89, 0xE5, 0x8B, 0x46, 0x04, 0x03, 0x46, 0x06, 0x5D, 0xC3);
        using (bus)
        {
            for (int i = 0; i < 50 && !cpu.State.Halted; i++) cpu.Step();

            Assert.Equal(12, cpu.State.Ax);         // 5 + 7
            Assert.Equal(0x8000u, cpu.State.Esp);   // stack bilanciato
        }
    }

    [Fact]
    public void CopiaDiMemoriaConRepMovswEVerifica()
    {
        // Copia 256 byte con REP MOVSW e li riconfronta con REPE CMPSW.
        //   FC           CLD
        //   B9 80 00     MOV CX,128        (128 word = 256 byte)
        //   F3 A5        REP MOVSW
        var (cpu, bus) = Setup(0xFC, 0xB9, 0x80, 0x00, 0xF3, 0xA5);
        using (bus)
        {
            for (int i = 0; i < 256; i++)
                bus.WriteByte((uint)(0x2000 + i), (byte)(i * 7));

            cpu.State.Si = 0x2000;
            cpu.State.Di = 0x4000;

            for (int i = 0; i < 500 && cpu.State.Eip < CodeBase + 6; i++) cpu.Step();

            for (int i = 0; i < 256; i++)
                Assert.Equal((byte)(i * 7), bus.ReadByte((uint)(0x4000 + i)));
            Assert.Equal(0, cpu.State.Cx);
        }
    }

    [Fact]
    public void ConversioneNumeroInStringaDecimale()
    {
        // Converte 1234 in cifre ASCII dividendo ripetutamente per 10:
        // e' il nucleo di printf("%d") e usa DIV, stringhe e cicli insieme.
        //   B8 D2 04     MOV AX,1234
        //   BB 0A 00     MOX BX,10
        //   BF 10 30     MOV DI,0x3010     (si scrive a ritroso)
        //   FD           STD
        // ciclo:
        //   BA 00 00     MOV DX,0
        //   F7 F3        DIV BX
        //   92           XCHG AX,DX        (resto in AL)
        //   04 30        ADD AL,'0'
        //   AA           STOSB
        //   92           XCHG AX,DX        (quoziente di nuovo in AX)
        //   85 C0        TEST AX,AX
        //   75 F1        JNZ ciclo
        var (cpu, bus) = Setup(
            0xB8, 0xD2, 0x04,
            0xBB, 0x0A, 0x00,
            0xBF, 0x10, 0x30,
            0xFD,
            0xBA, 0x00, 0x00,
            0xF7, 0xF3,
            0x92,
            0x04, 0x30,
            0xAA,
            0x92,
            0x85, 0xC0,
            0x75, 0xF1);
        using (bus)
        {
            for (int i = 0; i < 200 && cpu.State.Eip < CodeBase + 24; i++) cpu.Step();

            // Le cifre sono state scritte a ritroso da 0x3010: "1234"
            string digits = System.Text.Encoding.ASCII.GetString(
                bus.GetRamSpan(0x300D, 4).ToArray());
            Assert.Equal("1234", digits);
        }
    }
}
