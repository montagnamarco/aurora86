using Aurora86.Core;
using Aurora86.Core.Devices.Video;

namespace Aurora86.Graphics;

/// <summary>
/// A frontend's optional native rendering connection to one Aurora adapter.
/// Core and headless guests keep their software engine; the frontend owns
/// native resources and must detach them before disposing or replacing its
/// machine. Other GPU providers can implement the same versioned contract.
/// </summary>
public sealed class AuroraGpuBackendConnection : IDisposable
{
    private readonly AuroraGpuCard? _card;
    private readonly MacOpenGlBackend? _backend;
    private bool _disposed;

    public string Description { get; }
    public bool NativeAttached => _backend is not null && !_disposed;
    public long NativeOperationsCompleted => _backend?.HostExecutedOperationCount ?? 0;

    private AuroraGpuBackendConnection(AuroraGpuCard? card, MacOpenGlBackend? backend,
        string description)
    {
        _card = card;
        _backend = backend;
        Description = description;
    }

    /// <summary>
    /// Connects only when explicitly requested by the host frontend. Failed
    /// context creation preserves the software renderer and reports why. An
    /// unknown AURORA_GPU_BACKEND value also keeps the software renderer: a
    /// typo in the environment must not stop the emulator from starting, or
    /// leave a reset session without a connection.
    /// </summary>
    public static AuroraGpuBackendConnection Attach(Machine machine, string? selection = null)
    {
        ArgumentNullException.ThrowIfNull(machine);
        if (selection is not null && !IsKnownSelection(selection))
        {
            throw new ArgumentException("The GPU backend must be software, opengl or auto.", nameof(selection));
        }

        AuroraGpuCard? card = machine.AuroraGpu;
        if (selection is null)
        {
            string requested = Environment.GetEnvironmentVariable(BackendVariable)?.Trim() ?? "software";
            selection = requested.ToLowerInvariant();
            if (!IsKnownSelection(selection))
            {
                return new AuroraGpuBackendConnection(card, null,
                    $"software: {BackendVariable}={requested} is not software, opengl or auto");
            }
        }

        if (card is null || selection == "software")
        {
            return new AuroraGpuBackendConnection(card, null, "software");
        }

        if (!MacOpenGlBackend.TryCreate(out MacOpenGlBackend? backend, out string reason))
        {
            return new AuroraGpuBackendConnection(card, null, $"software fallback: {reason}");
        }

        try
        {
            if (selection == "auto")
            {
                backend!.ConfigureAutomaticRouting();
                if (backend.Operations == 0)
                {
                    string calibration = backend.AutomaticRoutingCalibration;
                    backend.Dispose();
                    return new AuroraGpuBackendConnection(card, null,
                        $"automatic software routing: {calibration}");
                }
            }
            card.Backend = backend;
            string description = $"OpenGL: {backend!.NativeRenderer}; {backend.NativeVersion}";
            if (selection == "auto")
            {
                description += $"; automatic native blend minimum: {backend.NativeBlendMinimumPixels} pixels; "
                    + backend.AutomaticRoutingCalibration;
            }
            return new AuroraGpuBackendConnection(card, backend, description);
        }
        catch
        {
            backend!.Dispose();
            throw;
        }
    }

    private const string BackendVariable = "AURORA_GPU_BACKEND";

    private static bool IsKnownSelection(string selection) => selection is "software" or "opengl" or "auto";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_card is not null && _backend is not null && ReferenceEquals(_card.Backend, _backend))
        {
            _card.Backend = null;
        }

        _backend?.Dispose();
        _disposed = true;
    }
}
