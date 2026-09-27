using Aurora386.Core.Devices.Interrupts;
using Aurora386.Core.Devices.IsaPnp;
using Aurora386.Core.Devices.Pci;
using Aurora386.Core.Devices.System;
using Aurora386.Core.IO;
using Aurora386.Core.Memory;
using Aurora386.Core.Timing;

namespace Aurora386.Core.Devices.Cards;

/// <summary>
/// What an expansion card may reach, and nothing else. A card that wants at
/// the CPU, the BIOS, or another card directly is not a card -- it is
/// chipset, and belongs wired into <see cref="Machine"/> the way the PIC,
/// the PIT and the IDE controllers are.
///
/// The DMA controller and the PCI and ISA Plug and Play buses are shared
/// chipset silicon rather than part of any one card -- a real 8237 was
/// wired to the slot connector, not fitted to the sound card -- so they are
/// reached rather than owned; a card claims a channel or a function number
/// on them, it does not construct them.
/// </summary>
public sealed class MachineBus
{
    public IoBus Io { get; }
    public PhysicalMemoryBus Memory { get; }
    public InterruptController Interrupts { get; }
    public Dma8237 Dma { get; }
    public PciBus Pci { get; }
    public IsaPnpBus IsaPlugAndPlay { get; }
    public Scheduler Scheduler { get; }

    private readonly Func<int> _cpuFrequencyHz;

    /// <summary>
    /// The clock speed the emulated CPU claims to run at -- see <see
    /// cref="Machine.CpuFrequencyHz"/>. Read live rather than copied, since a
    /// card built before the guest changes the processor model must still
    /// convert against whatever the ratio has since become.
    /// </summary>
    public int CpuFrequencyHz => _cpuFrequencyHz();

    public MachineBus(
        IoBus io,
        PhysicalMemoryBus memory,
        InterruptController interrupts,
        Dma8237 dma,
        PciBus pci,
        IsaPnpBus isaPlugAndPlay,
        Scheduler scheduler,
        Func<int> cpuFrequencyHz)
    {
        Io = io;
        Memory = memory;
        Interrupts = interrupts;
        Dma = dma;
        Pci = pci;
        IsaPlugAndPlay = isaPlugAndPlay;
        Scheduler = scheduler;
        _cpuFrequencyHz = cpuFrequencyHz;
    }

    /// <summary>
    /// Converts a span of emulated wall-clock time into the number of CPU
    /// cycles it takes at <see cref="CpuFrequencyHz"/> -- see <see
    /// cref="Machine.CyclesFromMicroseconds"/>, which this mirrors so a card
    /// scheduling itself from a datasheet timing does not need its own copy
    /// of the ratio.
    /// </summary>
    public long CyclesFromMicroseconds(double microseconds)
        => (long)(microseconds * CpuFrequencyHz / 1_000_000.0);
}
