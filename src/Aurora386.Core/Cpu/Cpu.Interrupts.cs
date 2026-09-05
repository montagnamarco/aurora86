namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>
    /// Checks whether a hardware interrupt is waiting to be serviced and, if so,
    /// starts it. It must be called between instructions, never in the middle
    /// of one.
    /// </summary>
    /// <returns>true when an interrupt was started.</returns>
    public bool CheckInterrupts()
    {
        if (Interrupts is null) return false;

        // The shadow window opened by STI lasts exactly one instruction.
        if (_interruptShadow)
        {
            _interruptShadow = false;
            return false;
        }

        if (!Flags.Interrupt) return false;
        if (!Interrupts.HasPendingInterrupt) return false;

        int vector = Interrupts.GetPendingVector();
        if (vector < 0) return false;

        // An interrupt wakes the CPU out of HLT even before it is serviced:
        // that is how a PC idles at a fraction of its normal power draw while
        // still responding promptly to the timer and the keyboard.
        State.Halted = false;

        DeliverExternalInterrupt((byte)vector);
        return true;
    }

    /// <summary>
    /// Executes one instruction and then checks for interrupts, which is the
    /// correct order: an interrupt may not cut an instruction in half.
    /// </summary>
    public int StepWithInterrupts()
    {
        int cycles = Step();
        CheckInterrupts();
        return cycles;
    }
}
