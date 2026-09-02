using CpuNs = Emulator.Core.Cpu;
using Emulator.Core.Devices;
using Emulator.Core.Memory;

namespace Emulator.Core.Bios;

/// <summary>
/// Funzioni tastiera del BIOS (INT 16h) e gestore di IRQ 1.
///
/// La tastiera manda scancode; il BIOS li traduce in coppie (ASCII, scancode)
/// e le accoda in un buffer circolare nella BDA, da cui DOS le preleva.
/// </summary>
public sealed class KeyboardServices
{
    private readonly BiosDataArea _bda;
    private readonly PhysicalMemoryBus _memory;
    private readonly Keyboard8042 _controller;

    public KeyboardServices(BiosDataArea bda, PhysicalMemoryBus memory, Keyboard8042 controller)
    {
        _bda = bda;
        _memory = memory;
        _controller = controller;
    }

    public void Handle(CpuNs.Cpu cpu)
    {
        byte function = cpu.State.GetReg8(4);   // AH

        switch (function)
        {
            case 0x00:
            case 0x10:   // versione estesa: stessa cosa per una tastiera standard
                WaitForKey(cpu);
                return;

            case 0x01:
            case 0x11:
                CheckForKey(cpu);
                return;

            case 0x02:
            case 0x12:
                cpu.State.SetReg8(0, _bda.KeyboardFlags);   // AL: stato dei modificatori
                return;

            case 0x03:   // imposta la velocita' di ripetizione: nessun effetto qui
                return;

            case 0x05:   // inserisce un tasto nel buffer
                Enqueue(cpu.State.Cx);
                return;

            default:
                return;
        }
    }

    /// <summary>
    /// Funzione 0x00: attende un tasto. Se il buffer e' vuoto la CPU viene
    /// lasciata in attesa riportando EIP all'istruzione della INT, cosi' il
    /// ciclo di attesa resta interrompibile e il timer continua a scorrere.
    /// </summary>
    private void WaitForKey(CpuNs.Cpu cpu)
    {
        if (TryDequeue(out ushort key))
        {
            cpu.State.Ax = key;
            return;
        }

        // Nessun tasto: si rimane sulla INT. La prossima esecuzione ricontrolla,
        // e nel frattempo le interruzioni hardware vengono servite.
        RepeatInterrupt(cpu);
    }

    /// <summary>
    /// Funzione 0x01: guarda se c'e' un tasto senza rimuoverlo. Lo zero flag
    /// segnala il buffer vuoto, e AX contiene il tasto quando c'e'.
    ///
    /// Il flag va scritto nei flag <b>impilati</b>, non in quelli della CPU:
    /// il punto di ingresso termina con IRET, che li ripristina dallo stack
    /// sovrascrivendo qualsiasi cosa l'handler avesse impostato nei registri.
    /// </summary>
    private void CheckForKey(CpuNs.Cpu cpu)
    {
        if (TryPeek(out ushort key))
        {
            cpu.State.Ax = key;
            SetZeroInStackedFlags(cpu, false);
        }
        else
        {
            // Nessun tasto: AH resta la funzione richiesta, come sul ferro reale.
            SetZeroInStackedFlags(cpu, true);
        }
    }

    /// <summary>
    /// Riporta l'esecuzione all'istruzione INT che ha portato qui, in modo che
    /// venga rieseguita. Serve per le attese che non devono bloccare la macchina.
    /// </summary>
    private static void RepeatInterrupt(CpuNs.Cpu cpu)
    {
        // Sullo stack ci sono IP, CS e FLAGS: riportando IP indietro di due
        // byte si torna sulla INT stessa.
        uint stackPointer = cpu.State.Ss.Big ? cpu.State.Esp : cpu.State.Sp;
        ushort returnIp = cpu.ReadMemoryWord(CpuNs.SegReg.Ss, stackPointer);
        cpu.WriteMemoryWord(CpuNs.SegReg.Ss, stackPointer, (ushort)(returnIp - 2));

        // Il ciclo di attesa deve girare con le interruzioni abilitate,
        // altrimenti il tasto che sblocca l'attesa non arriva mai: la INT ha
        // azzerato IF, e chi ha chiamato poteva gia' averlo azzerato prima.
        // Il BIOS vero fa la stessa cosa eseguendo STI dentro il ciclo.
        ushort flags = cpu.ReadMemoryWord(CpuNs.SegReg.Ss, stackPointer + 4);
        cpu.WriteMemoryWord(CpuNs.SegReg.Ss, stackPointer + 4, (ushort)(flags | 0x0200));
    }

    private static void SetZeroInStackedFlags(CpuNs.Cpu cpu, bool zero)
    {
        uint stackPointer = cpu.State.Ss.Big ? cpu.State.Esp : cpu.State.Sp;
        ushort flags = cpu.ReadMemoryWord(CpuNs.SegReg.Ss, stackPointer + 4);
        flags = zero ? (ushort)(flags | 0x40) : (ushort)(flags & ~0x40);
        cpu.WriteMemoryWord(CpuNs.SegReg.Ss, stackPointer + 4, flags);
    }

    // --- buffer circolare della BDA ------------------------------------------

    public bool TryPeek(out ushort key)
    {
        ushort head = _bda.KeyboardBufferHead;
        ushort tail = _bda.KeyboardBufferTail;

        if (head == tail) { key = 0; return false; }

        key = _memory.ReadWord(BiosDataArea.Base + head);
        return true;
    }

    public bool TryDequeue(out ushort key)
    {
        ushort head = _bda.KeyboardBufferHead;
        ushort tail = _bda.KeyboardBufferTail;

        if (head == tail) { key = 0; return false; }

        key = _memory.ReadWord(BiosDataArea.Base + head);
        _bda.KeyboardBufferHead = Advance(head);
        return true;
    }

    public bool Enqueue(ushort key)
    {
        ushort tail = _bda.KeyboardBufferTail;
        ushort next = Advance(tail);

        // Buffer pieno: il tasto viene perso, come sul ferro reale.
        if (next == _bda.KeyboardBufferHead) return false;

        _memory.WriteWord(BiosDataArea.Base + tail, key);
        _bda.KeyboardBufferTail = next;
        return true;
    }

    /// <summary>Avanza un puntatore nel buffer circolare, con avvolgimento.</summary>
    private static ushort Advance(ushort pointer)
    {
        ushort next = (ushort)(pointer + 2);
        return next >= BiosDataArea.KeyboardBufferEnd
            ? (ushort)BiosDataArea.KeyboardBufferStart
            : next;
    }

    /// <summary>
    /// Gestore di IRQ 1: legge lo scancode dal controller, aggiorna i
    /// modificatori e accoda il tasto tradotto.
    /// </summary>
    public void HandleIrq(CpuNs.Cpu cpu)
    {
        byte scanCode = cpu.Io.ReadByte(0x60);
        bool released = (scanCode & 0x80) != 0;
        byte code = (byte)(scanCode & 0x7F);

        UpdateModifiers(code, released);

        if (!released)
        {
            ushort key = Translate(code);
            if (key != 0) Enqueue(key);
        }

        // Fine interruzione al PIC master.
        cpu.Io.WriteByte(0x20, 0x20);
    }

    private void UpdateModifiers(byte code, bool released)
    {
        byte flags = _bda.KeyboardFlags;

        // I bit dello stato tastiera sono definiti dall'architettura del PC:
        // 0 shift destro, 1 shift sinistro, 2 ctrl, 3 alt.
        byte bit = code switch
        {
            0x36 => 0x01,   // shift destro
            0x2A => 0x02,   // shift sinistro
            0x1D => 0x04,   // ctrl
            0x38 => 0x08,   // alt
            _ => 0,
        };

        if (bit != 0)
        {
            flags = released ? (byte)(flags & ~bit) : (byte)(flags | bit);
            _bda.KeyboardFlags = flags;
            return;
        }

        // I tasti di commutazione cambiano stato solo alla pressione.
        if (released) return;

        byte toggle = code switch
        {
            0x3A => 0x40,   // bloc maiusc
            0x45 => 0x20,   // bloc num
            0x46 => 0x10,   // bloc scorr
            _ => 0,
        };

        if (toggle != 0) _bda.KeyboardFlags = (byte)(flags ^ toggle);
    }

    /// <summary>
    /// Traduce uno scancode nella coppia (scancode, ASCII) che DOS si aspetta.
    /// I tasti che non producono un carattere hanno ASCII zero, e i programmi
    /// li riconoscono dallo scancode.
    /// </summary>
    private ushort Translate(byte code)
    {
        byte flags = _bda.KeyboardFlags;
        bool shift = (flags & 0x03) != 0;
        bool capsLock = (flags & 0x40) != 0;
        bool ctrl = (flags & 0x04) != 0;

        char character = ScanCodeTable.ToChar(code, shift);
        if (character == '\0') return (ushort)(code << 8);

        // Il bloc maiusc agisce solo sulle lettere, non su cifre e simboli:
        // e' la differenza fra bloc maiusc e shift permanente.
        if (capsLock && char.IsAsciiLetter(character))
            character = shift ? char.ToLowerInvariant(character) : char.ToUpperInvariant(character);

        // Ctrl con una lettera produce il codice di controllo corrispondente:
        // Ctrl+C e' 3, Ctrl+M e' 13, e cosi' via.
        if (ctrl && char.IsAsciiLetter(character))
            character = (char)(char.ToUpperInvariant(character) - 'A' + 1);

        return (ushort)((code << 8) | (byte)character);
    }
}
