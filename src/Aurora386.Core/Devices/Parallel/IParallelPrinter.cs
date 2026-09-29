namespace Aurora386.Core.Devices.Parallel;

/// <summary>
/// The far end of the Centronics cable: whatever is plugged into LPT1.
///
/// <see cref="ParallelPort"/> calls this once per byte the handshake
/// delivers, and once whenever the guest pulses /INIT. Aurora386's own
/// implementation is <see cref="PrintJobCollector"/>, which turns a run of
/// bytes into a print job; a test rig is free to substitute a simpler stub
/// that just records what it was sent.
/// </summary>
public interface IParallelPrinter
{
    /// <summary>A byte the port's handshake has just delivered.</summary>
    void Receive(byte value);

    /// <summary>The guest has pulsed /INIT (CONTROL bit 2 going 0): the printer resets.</summary>
    void Initialize();
}
