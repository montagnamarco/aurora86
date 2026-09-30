namespace Aurora86.Core.Cpu;

/// <summary>
/// A hypervisor call made with an I/O instruction: VMware's backdoor, where
/// one <c>IN EAX, DX</c> carries a command in the general registers and
/// returns its results in them (docs/VMWARE-BACKDOOR.md).
///
/// The processor offers the instruction here before its I/O permission
/// check, because VMware answers the call from any ring -- its tools probe
/// the port from ring 3 -- and before the I/O bus, which can only return a
/// value for EAX. An implementation claims the access by returning true
/// and must decline everything that is not one of its calls, which then
/// continues as an ordinary port read with every architectural check.
/// </summary>
public interface IBackdoorPort
{
    /// <summary>
    /// A 32-bit <c>IN EAX, DX</c> from <paramref name="port"/>. Returns true
    /// when the access was a backdoor call and the registers now hold its
    /// results.
    /// </summary>
    bool TryHandleInput(CpuState state, ushort port);
}
