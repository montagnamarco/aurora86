namespace Emulator.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>
    /// Verifica se c'e' una interruzione hardware da servire e, in tal caso,
    /// la avvia. Va chiamata fra un'istruzione e l'altra, mai nel mezzo.
    /// </summary>
    /// <returns>true se e' stata avviata una interruzione.</returns>
    public bool CheckInterrupts()
    {
        if (Interrupts is null) return false;

        // La finestra d'ombra dopo STI dura esattamente una istruzione.
        if (_interruptShadow)
        {
            _interruptShadow = false;
            return false;
        }

        if (!Flags.Interrupt) return false;
        if (!Interrupts.HasPendingInterrupt) return false;

        int vector = Interrupts.GetPendingVector();
        if (vector < 0) return false;

        // Una interruzione risveglia la CPU da HLT anche prima di essere
        // servita: e' il modo in cui il PC resta in attesa consumando poco.
        State.Halted = false;

        EnterInterrupt((byte)vector, isSoftware: false);
        return true;
    }

    /// <summary>
    /// Esegue una istruzione e poi controlla le interruzioni, che e' l'ordine
    /// corretto: una interruzione non puo' interrompere una istruzione a meta'.
    /// </summary>
    public int StepWithInterrupts()
    {
        int cycles = Step();
        CheckInterrupts();
        return cycles;
    }
}
