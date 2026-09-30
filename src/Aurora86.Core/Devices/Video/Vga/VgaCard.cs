namespace Aurora86.Core.Devices.Video;

/// <summary>
/// The common VGA services introduced by IBM in 1987: indexed registers,
/// video memory and scanout. Concrete adapters own their bus identity,
/// address decoding and optional drawing engines used by later display drivers.
/// </summary>
public abstract class VgaCard : IDisposable
{
    protected VgaCard(VgaRegisters registers)
    {
        ArgumentNullException.ThrowIfNull(registers);
        Registers = registers;
        VideoMemory = new VgaMemory(registers);
    }

    public VgaRegisters Registers { get; }
    public VgaMemory VideoMemory { get; }
    public abstract VgaRenderer Renderer { get; }
    public virtual IVideoOutput Display => Renderer;

    /// <summary>Releases card-owned resources before the containing buses are disposed.</summary>
    public virtual void Dispose()
    {
    }
}
