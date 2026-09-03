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
            case 0x00:
            case 0x10:   // extended variant: identical on a standard keyboard
                WaitForKey(cpu);
                return;

            case 0x01:
            case 0x11:
                CheckForKey(cpu);
                return;

            case 0x02:
            case 0x12:
                cpu.State.SetReg8(0, _bda.KeyboardFlags);   // AL: modifier state
                return;

            case 0x03:   // set typematic rate: no effect here
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
    private void WaitForKey(CpuNs.Cpu cpu)
    {
        if (TryDequeue(out ushort key))
        {
            cpu.State.Ax = key;
            return;
        }

        // No key available: stay parked on the INT. The next execution checks
        // again, and in the meantime hardware interrupts get serviced.
        RepeatInterrupt(cpu);
    }

    /// <summary>
    /// Function 0x01: peek at a keystroke without removing it. The zero flag
    /// signals an empty buffer, and AX holds the key when there is one.
    ///
    /// The flag has to be written into the <b>stacked</b> flags, not the live
    /// CPU ones: the entry point ends with IRET, which reloads them from the
    /// stack and would overwrite whatever the handler had set in the register.
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

        // An extended key produces no character of its own: the arrows and
        // their neighbours share their scancodes with the numeric keypad,
        // and it is only the prefix that tells them apart. Translating one
        // through the character table would turn the up arrow into the digit
        // 8 -- which is exactly what the keypad key beside it means.
        if (extended) return (ushort)(code << 8);
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
