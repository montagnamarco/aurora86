namespace Aurora386.Core.Devices.Interrupts;

/// <summary>
/// What <see cref="Cpu.Cpu.CheckInterrupts"/> asks between instructions: is a
/// maskable hardware interrupt waiting, and if so what vector does it carry.
///
/// Before work package P4.4 <c>Cpu.Interrupts</c> was the concrete 8259 pair
/// (<see cref="InterruptController"/>) and nothing else ever stood in for it.
/// The local APIC (<c>Devices.Apic.LocalApic</c>) now sits in front of that
/// same pair on a machine whose processor has an on-die APIC: while the IMCR
/// is 0, or the APIC is hardware-disabled or the model has none, it forwards
/// the 8259 completely unchanged, so every machine without an engaged APIC
/// -- which today is every machine, since nothing yet switches the IMCR --
/// sees exactly the interrupts it always did. This interface is the seam
/// that lets the CPU ask either one the same two questions without knowing
/// which is behind <c>Cpu.Interrupts</c>.
/// </summary>
public interface IInterruptSource
{
    /// <summary>True when at least one unmasked interrupt is waiting to be serviced.</summary>
    bool HasPendingInterrupt { get; }

    /// <summary>The vector to service, or -1 if none is ready. Calling this commits to delivering it.</summary>
    int GetPendingVector();
}

/// <summary>
/// An <see cref="IInterruptSource"/> that needs to know about a processor
/// RESET, distinct from an ordinary write to one of its registers: SDM
/// 10.4.7 requires the local APIC's own registers to go back to their
/// power-on values (and any pending timer to be cancelled) on every RESET,
/// not only when APIC_BASE's hardware-enable bit makes a 0-to-1 transition
/// (coordinator decision A7, a different event a guest can trigger without
/// a full reset at all). <see cref="Cpu.Cpu.Reset"/> calls this on whatever
/// <see cref="Cpu.Cpu.Interrupts"/> currently holds, if it implements this;
/// the plain 8259 pair (<c>InterruptController</c>) does not need it, since
/// its own reset already happens through the BIOS reprogramming its ports.
/// </summary>
public interface IResettableInterruptSource : IInterruptSource
{
    void ResetOnCpuReset();
}
