namespace Aurora86.Core.Devices.Video;

/// <summary>One disjoint clipped fill in a packed temporary pixel array.</summary>
public readonly record struct AuroraGpuFillOperation(
    int PixelOffset, int Width, int Height, uint Colour, bool Blend);

/// <summary>
/// Optional host-native rendering for the 2026 Aurora virtual display adapter.
/// Implementations synchronously dispatch to their own graphics-context thread.
/// Version, kind and operations remain constant while attached. Guest VRAM remains the
/// authoritative storage; this interface never receives that memory directly.
/// </summary>
public interface IAuroraGpuBackend
{
    uint InterfaceVersion { get; }
    uint Kind { get; }

    /// <summary>Supported operations; older opaque-only implementations inherit bit 0.</summary>
    uint Operations => AuroraGpuInterface.BackendOperationOpaqueFill;

    /// <summary>
    /// Produce width * height opaque pixels, packed contiguously in the first
    /// elements of pixels. True promises complete output before returning.
    /// False may alter this temporary buffer but must never alter guest memory.
    /// The buffer belongs to the caller and must not be retained after return.
    /// No existing texture input is needed for this overwrite operation.
    /// </summary>
    bool TryFillOpaque(int width, int height, uint colour, uint[] pixels);

    /// <summary>
    /// Blend premultiplied colour over the first width * height input pixels,
    /// producing the exact rounded and saturated source-over result in place.
    /// Input is copied from current VRAM. False may alter staging; the caller
    /// ignores it and falls back to software.
    /// The synchronous output ownership rules of TryFillOpaque also apply.
    /// </summary>
    bool TryFillBlend(int width, int height, uint colour, uint[] pixels) => false;

    /// <summary>
    /// Apply independent fills to their packed segments in order. The first
    /// pixelCount elements contain current VRAM, without pitch padding or
    /// bounding-box gaps. False may alter staging but commits no guest pixels.
    /// Implementations must not retain either input after returning.
    /// </summary>
    bool TryFillBatch(int pixelCount, uint[] pixels,
        IReadOnlyList<AuroraGpuFillOperation> operations) => false;
}
