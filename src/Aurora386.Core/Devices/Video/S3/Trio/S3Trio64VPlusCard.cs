using Aurora386.Core.Devices.Pci;
using Aurora386.Core.IO;
using Aurora386.Core.Memory;

namespace Aurora386.Core.Devices.Video;

/// <summary>
/// The 1995 S3 Trio64V+ adapter: VGA scanout and S3's port/MMIO 2D engine
/// sharing two megabytes of VRAM. Windows drivers discover its PCI/CRTC
/// identity and access the established banked, linear and MMIO windows.
/// </summary>
public sealed class S3Trio64VPlusCard : VgaCard
{
    public const uint EngineApertureOffset = 0x01000000;

    /// <summary>
    /// Installs the existing Trio decoders on buses owned by the machine.
    /// These static mappings intentionally retain their established addresses;
    /// changing Trio BAR relocation semantics is separate hardware work.
    /// </summary>
    public S3Trio64VPlusCard(PhysicalMemoryBus memory, IoBus io, PciBus pci,
        uint frameBufferBase, ushort address = 0x0020)
        : base(new S3VgaRegisters(S3VideoIdentity.Trio64VPlus))
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(io);
        ArgumentNullException.ThrowIfNull(pci);
        FrameBufferBase = frameBufferBase;
        MmioBase = frameBufferBase + VgaMemory.TotalSize;
        Registers.Register(io);
        memory.MapDevice(VgaMemory.Base, VgaMemory.Size, VideoMemory);
        memory.MapDevice(frameBufferBase, VgaMemory.TotalSize, VideoMemory.Linear);
        Registers.ApertureBase = frameBufferBase;
        Renderer = new S3VgaRenderer(VideoMemory, Registers);
        Accelerator = new S3Accelerator(VideoMemory, Registers);
        Accelerator.Register(io);
        VideoMemory.Engine = Accelerator.Mmio;
        memory.MapDevice(MmioBase, 0x10000, Accelerator.Mmio);
        Registers.MmioBase = MmioBase;
        memory.MapDevice(frameBufferBase + EngineApertureOffset, 0x10000, Accelerator.Mmio);

        Function = S3VideoIdentity.Trio64VPlus.CreatePciFunction(address);
        const uint apertureMask = ~(uint)(VgaMemory.TotalSize - 1);
        Function.Define(0x10, frameBufferBase | 0x08, 4, apertureMask);
        pci.Add(Function);
    }

    public new S3VgaRegisters Registers => (S3VgaRegisters)base.Registers;
    public override VgaRenderer Renderer { get; }
    public S3Accelerator Accelerator { get; }
    public PciFunction Function { get; }
    public uint FrameBufferBase { get; }
    public uint MmioBase { get; }
}
