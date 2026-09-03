using CpuNs = Aurora386.Core.Cpu;
using Aurora386.Core.Devices;
using Aurora386.Core.Memory;

namespace Aurora386.Core.Bios;

/// <summary>
/// BIOS keyboard services (INT 16h) and the IRQ 1 handler.
///
/// The keyboard sends scancodes; the BIOS translates them into (ASCII,
/// scancode) pairs and appends them to a circular buffer in the BDA, which is
/// where DOS picks them up.
/// </summary>
public sealed class KeyboardServices
{
    private readonly BiosDataArea _bda;
    private readonly PhysicalMemoryBus _memory;
    private readonly Keyboard8042 _controller;

    /// <summary>
    /// Set when a 0xE0 byte has arrived and its partner is still to come.
    /// </summary>
    private bool _extendedPrefix;

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
            // The plain functions and their extended twins differ in what
            // they are willing to hand back.
            //
            // A 101-key keyboard has keys the original had not -- F11, F12,
            // the cursor block separate from the numeric keypad -- and their
            // scancodes had to go somewhere. The plain functions predate
            // them and would return nonsense, so they hide those keystrokes
            // instead; the extended ones return everything. A program that
            // asks the old way and is given a new key reads it as whatever
            // the old keyboard had in that position.
            case 0x00: WaitForKey(cpu, extended: false); return;
            case 0x10: WaitForKey(cpu, extended: true); return;

            case 0x01: CheckForKey(cpu, extended: false); return;
            case 0x11: CheckForKey(cpu, extended: true); return;

            case 0x02:
                cpu.State.SetReg8(0, _bda.KeyboardFlags);   // AL: modifier state
                return;

            case 0x12:
                // The extended call reports two bytes rather than one: the
                // second says which of the paired modifiers is down, telling
                // right Alt from left Alt and right Ctrl from left Ctrl.
                cpu.State.SetReg8(0, _bda.KeyboardFlags);        // AL
                cpu.State.SetReg8(4, _bda.KeyboardFlags2);      // AH
                return;

            case 0x03:   // set typematic rate: no effect here
                return;

            case 0x09:
                // Which of the extended functions this BIOS supports, as a
                // bitmap in AL. Software asks before using them, and a BIOS
                // that does not answer is taken for one too old to have them
                // -- KEYB asks exactly this, and on getting no answer decides
                // it is dealing with an 84-key keyboard.
                //
                // Bits 0-3: functions 10h, 11h, 12h and the typematic call
                // 0305h are all present. Bits 4-5 cover the keyboard ID and
                // the 122-key industrial keyboard, which we do not have.
                cpu.State.SetReg8(0, 0x0F);   // AL
                return;

            case 0x0A:
                // The keyboard's own two-byte identifier, which software may
                // read to tell the models apart. 0xAB41 is the 101-key
                // keyboard every PC has had since the AT.
                cpu.State.Bx = 0xAB41;
                return;

            case 0x05:   // push a keystroke into the buffer
                Enqueue(cpu.State.Cx);
                return;

            default:
                return;
        }
    }

    /// <summary>
    /// Function 0x00: wait for a keystroke. If the buffer is empty the CPU is
    /// left waiting by rewinding EIP back to the INT instruction, so the wait
    /// loop stays interruptible and the timer keeps ticking.
    /// </summary>
    private void WaitForKey(CpuNs.Cpu cpu, bool extended)
    {
        while (TryDequeue(out ushort key))
        {
            // The plain call discards the keys the original keyboard did not
            // have rather than returning them: their scancodes mean something
            // else on that keyboard, so handing one back would have the
            // caller act on a key the user never pressed.
            if (!extended && IsExtendedOnly(key)) continue;

            cpu.State.Ax = extended ? key : TranslateForPlainCall(key);
            return;
        }

        // No key available: stay parked on the INT. The next execution checks
        // again, and in the meantime hardware interrupts get serviced.
        RepeatInterrupt(cpu);
    }

    /// <summary>
    /// Whether a keystroke exists only on the 101-key keyboard, and so has to
    /// stay hidden from the calls that predate it.
    /// </summary>
    private static bool IsExtendedOnly(ushort key)
    {
        byte scanCode = (byte)(key >> 8);

        // F11 and F12, and the shifted and controlled forms of them.
        return scanCode is >= 0x85 and <= 0x8C;
    }

    /// <summary>
    /// What the plain call reports for a key both keyboards have.
    ///
    /// The cursor block of a 101-key keyboard duplicates the numeric keypad,
    /// and the BIOS marks the duplicates with an ASCII byte of 0xE0. Software
    /// written for the older keyboard does not expect that byte and treats it
    /// as a character, so the plain call reports zero instead, which is what
    /// the older keyboard would have sent.
    /// </summary>
    private static ushort TranslateForPlainCall(ushort key)
        => (key & 0xFF) == 0xE0 ? (ushort)(key & 0xFF00) : key;

    /// <summary>
    /// Function 0x01: peek at a keystroke without removing it. The zero flag
    /// signals an empty buffer, and AX holds the key when there is one.
    ///
    /// The flag has to be written into the <b>stacked</b> flags, not the live
    /// CPU ones: the entry point ends with IRET, which reloads them from the
    /// stack and would overwrite whatever the handler had set in the register.
    /// </summary>
    private void CheckForKey(CpuNs.Cpu cpu, bool extended)
    {
        // Discard anything the plain call is not allowed to see, so that a
        // program polling this way is not told a key is waiting and then
        // handed nothing when it asks for it.
        while (!extended && TryPeek(out ushort pending) && IsExtendedOnly(pending))
            TryDequeue(out _);

        if (TryPeek(out ushort key))
        {
            cpu.State.Ax = extended ? key : TranslateForPlainCall(key);
            SetZeroInStackedFlags(cpu, false);
        }
        else
        {
            // No key: AH keeps the requested function number, just as on real
            // hardware.
            SetZeroInStackedFlags(cpu, true);
        }
    }

    /// <summary>
    /// Rewinds execution back to the INT instruction that got us here, so that
    /// it runs again. This is how a wait can be implemented without freezing
    /// the machine.
    /// </summary>
    private static void RepeatInterrupt(CpuNs.Cpu cpu)
    {
        // The stack holds IP, CS and FLAGS: backing IP up by two bytes lands
        // right on the INT itself.
        uint stackPointer = cpu.State.Ss.Big ? cpu.State.Esp : cpu.State.Sp;
        ushort returnIp = cpu.ReadMemoryWord(CpuNs.SegReg.Ss, stackPointer);
        cpu.WriteMemoryWord(CpuNs.SegReg.Ss, stackPointer, (ushort)(returnIp - 2));

        // The wait loop must run with interrupts enabled, otherwise the very
        // keystroke that would end the wait can never arrive: taking the INT
        // cleared IF, and the caller may well have cleared it beforehand too.
        // A real BIOS does exactly the same thing by executing STI inside its
        // wait loop.
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

    // --- the BDA circular buffer ---------------------------------------------
    // Head and tail are offsets relative to segment 0x40, so every access here
    // adds BiosDataArea.Base to them. Programs that bypass INT 16h and walk the
    // buffer themselves read those same words as 0040:[041A] offsets, which is
    // why storing absolute addresses in them would break those programs.

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

        // Buffer full: the keystroke is dropped, just as on real hardware.
        if (next == _bda.KeyboardBufferHead) return false;

        _memory.WriteWord(BiosDataArea.Base + tail, key);
        _bda.KeyboardBufferTail = next;
        return true;
    }

    /// <summary>
    /// Advances a pointer within the circular buffer, wrapping around.
    ///
    /// The bounds come from 0x480 and 0x482 rather than from constants,
    /// because those two words are what defines the buffer: software is
    /// allowed to move it somewhere roomier, and a wrap computed against the
    /// old address would then walk straight off the end. Should they be
    /// unset, fall back to the buffer's traditional home so that a pointer
    /// still wraps somewhere sane instead of climbing forever.
    /// </summary>
    private ushort Advance(ushort pointer)
    {
        ushort start = _bda.KeyboardBufferStartPointer;
        ushort end = _bda.KeyboardBufferEndPointer;
        if (end <= start)
        {
            start = (ushort)BiosDataArea.KeyboardBufferStart;
            end = (ushort)BiosDataArea.KeyboardBufferEnd;
        }

        ushort next = (ushort)(pointer + 2);
        return next >= end ? start : next;
    }

    /// <summary>
    /// IRQ 1 handler: read the scancode from the controller, update the
    /// modifier state and queue the translated keystroke.
    /// </summary>
    public void HandleIrq(CpuNs.Cpu cpu)
    {
        byte scanCode = cpu.Io.ReadByte(0x60);

        // 0xE0 is not a key. It announces that the next byte names one of the
        // keys added after the original PC keyboard -- the arrow cluster, the
        // keypad's Enter and slash, right Ctrl and Alt -- which share their
        // scancodes with older keys and are told apart only by this prefix.
        //
        // It must be recognised on the raw byte, before the release bit is
        // stripped: 0xE0 has that bit set already, so masking it first turns
        // it into 0x60 and the prefix becomes an ordinary keypress of a key
        // that does not exist.
        if (scanCode is 0xE0 or 0xE1)
        {
            _extendedPrefix = true;
            cpu.Io.WriteByte(0x20, 0x20);
            return;
        }

        bool released = (scanCode & 0x80) != 0;
        byte code = (byte)(scanCode & 0x7F);
        bool extended = _extendedPrefix;
        _extendedPrefix = false;

        UpdateModifiers(code, released, extended);

        if (!released)
        {
            ushort key = Translate(code, extended);
            if (key != 0) Enqueue(key);
        }

        // End of interrupt to the master PIC.
        cpu.Io.WriteByte(0x20, 0x20);
    }

    private void UpdateModifiers(byte code, bool released, bool extended)
    {
        byte flags = _bda.KeyboardFlags;

        // The keypad's own Enter and slash sit behind the prefix and are not
        // modifiers at all; right Ctrl and right Alt are, and they set the
        // same status bits as the left-hand pair, so the prefix changes
        // nothing here. It matters only for the toggle keys below, where an
        // extended 0x45 is Pause rather than Num Lock.

        // The keyboard status bits are fixed by the PC architecture:
        // 0 right shift, 1 left shift, 2 ctrl, 3 alt.
        byte bit = code switch
        {
            0x36 => 0x01,   // right shift
            0x2A => 0x02,   // left shift
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

        // Toggle keys only change state on press, never on release.
        if (released) return;

        // Pause arrives as an extended 0x45 and must not be mistaken for Num
        // Lock, which is the same code without the prefix.
        if (extended && code == 0x45) return;

        byte toggle = code switch
        {
            0x3A => 0x40,   // caps lock
            0x45 => 0x20,   // num lock
            0x46 => 0x10,   // scroll lock
            _ => 0,
        };

        if (toggle != 0) _bda.KeyboardFlags = (byte)(flags ^ toggle);
    }

    /// <summary>
    /// Translates a scancode into the (scancode, ASCII) pair DOS expects. Keys
    /// that do not produce a character get an ASCII byte of zero, and programs
    /// identify them by the scancode instead.
    /// </summary>
    private ushort Translate(byte code, bool extended)
    {
        byte flags = _bda.KeyboardFlags;

        // An extended key gets an ASCII byte of 0xE0 rather than a character
        // of its own.
        //
        // The cursor block of a 101-key keyboard duplicates the numeric
        // keypad and shares its scancodes, so the scancode alone cannot say
        // which was pressed. The BIOS marks the duplicates with 0xE0, and a
        // program that wants to tell them apart looks at that byte. Passing
        // an extended key through the character table instead would turn the
        // up arrow into the digit 8, which is what the keypad key beside it
        // means.
        if (extended) return (ushort)((code << 8) | 0xE0);

        // F11 and F12 are reported under scancodes of their own rather than
        // the ones the keyboard sends.
        //
        // The keyboard sends 0x57 and 0x58, which on the original keyboard
        // were free -- but the BIOS reports 0x85 and 0x86, continuing the
        // series the other function keys occupy. Software looks for those,
        // and it is also what lets the plain INT 16h calls recognise the two
        // keys as ones they must not hand back.
        if (code == 0x57) return 0x8500;
        if (code == 0x58) return 0x8600;
        bool shift = (flags & 0x03) != 0;
        bool capsLock = (flags & 0x40) != 0;
        bool ctrl = (flags & 0x04) != 0;

        char character = ScanCodeTable.ToChar(code, shift);
        if (character == '\0') return (ushort)(code << 8);

        // Caps lock only affects letters, not digits or symbols: that is the
        // difference between caps lock and a permanently held shift.
        if (capsLock && char.IsAsciiLetter(character))
            character = shift ? char.ToLowerInvariant(character) : char.ToUpperInvariant(character);

        // Ctrl plus a letter yields the matching control code: Ctrl+C is 3,
        // Ctrl+M is 13, and so on.
        if (ctrl && char.IsAsciiLetter(character))
            character = (char)(char.ToUpperInvariant(character) - 'A' + 1);

        return (ushort)((code << 8) | (byte)character);
    }
}
