namespace Aurora86.Core.Devices.Interrupts;

/// <summary>
/// The task-priority class an x86-64 processor reaches through CR8: the upper
/// four bits of the local APIC's TPR (SDM Vol.3A §10.8.6.1). 64-bit kernels
/// raise and lower it with MOV CR8 instead of an MMIO write -- Windows x64
/// does so constantly -- so the interrupt source that owns the TPR exposes
/// it here for the processor's MOV CR8 to use.
/// </summary>
public interface ITaskPriority
{
    /// <summary>TPR[7:4] as a value 0-15; writing it clears TPR[3:0].</summary>
    int TaskPriorityClass { get; set; }
}
