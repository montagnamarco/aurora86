using System.Collections.Concurrent;
using System.Diagnostics;
using Aurora86.Core.Devices.Video;

namespace Aurora86.Graphics;

/// <summary>
/// Native offscreen OpenGL for Aurora's opaque and blended 2D fills on macOS.
/// A dedicated thread owns CGL and synchronously reads each completed operation
/// into caller-owned staging memory. Guest VRAM remains authoritative in Core;
/// this backend holds no persistent guest pixels and never changes snapshots.
/// </summary>
public sealed class MacOpenGlBackend : IAuroraGpuBackend, IDisposable
{
    /// <summary>One synchronous staging request, released before completion is published.</summary>
    private sealed record FillRequest(int Width, int Height, uint Colour, uint[] Pixels, bool Blend,
        TaskCompletionSource<bool> Completion, int PixelCount = 0,
        AuroraGpuFillOperation[]? BatchOperations = null);

    private readonly BlockingCollection<FillRequest> _requests = new();
    private readonly TaskCompletionSource<string?> _startup =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lifecycle = new();
    private readonly Thread _worker;
    private bool _disposed;
    private bool _collectionDisposed;
    private long _executedOperations;
    private uint _operations = AuroraGpuInterface.BackendOperationOpaqueFill;

    public uint InterfaceVersion => AuroraGpuInterface.BackendInterfaceVersion;
    public uint Kind => AuroraGpuInterface.BackendKindOpenGl;
    public uint Operations => _operations;
    private volatile bool _automaticRouting;
    private int _nativeBlendMinimumPixels = int.MaxValue;
    public int NativeBlendMinimumPixels => _nativeBlendMinimumPixels;
    public string AutomaticRoutingCalibration { get; private set; } = "Not calibrated.";
    public bool BatchSupported { get; private set; }
    public string BatchUnavailableReason { get; private set; } = "";
    public string BlendUnavailableReason { get; private set; } = "";
    public string NativeRenderer { get; private set; } = "";
    public string NativeVendor { get; private set; } = "";
    public string NativeVersion { get; private set; } = "";
    public long HostExecutedOperationCount => Interlocked.Read(ref _executedOperations);

    private MacOpenGlBackend()
    {
        _worker = new Thread(RunWorker)
        {
            IsBackground = true,
            Name = "Aurora OpenGL backend"
        };
        _worker.Start();
    }

    /// <summary>
    /// Creates an accelerated CGL context without a window. Failure reports its
    /// reason rather than representing a software renderer as native GPU work.
    /// </summary>
    public static bool TryCreate(out MacOpenGlBackend? backend, out string reason)
    {
        backend = null;
        if (!OperatingSystem.IsMacOS())
        {
            reason = "Native CGL requires macOS.";
            return false;
        }

        MacOpenGlBackend candidate;
        try
        {
            candidate = new MacOpenGlBackend();
        }
        catch (Exception exception)
        {
            reason = exception.Message;
            return false;
        }

        string? failure = candidate._startup.Task.GetAwaiter().GetResult();
        if (failure is not null)
        {
            candidate.Dispose();
            reason = failure;
            return false;
        }

        backend = candidate;
        reason = "";
        return true;
    }

    /// <summary>
    /// Returns true only after a native fill and synchronous exact readback.
    /// A false result leaves staging unspecified; the caller must not commit it.
    /// </summary>
    public bool TryFillOpaque(int width, int height, uint colour, uint[] pixels)
    {
        return !_automaticRouting && SubmitFill(width, height, colour, pixels, false);
    }

    /// <summary>Uploads private destination staging, blends in an integer shader, and reads it back.</summary>
    public bool TryFillBlend(int width, int height, uint colour, uint[] pixels)
    {
        if ((_automaticRouting && (_nativeBlendMinimumPixels == int.MaxValue ||
            (long)width * height < _nativeBlendMinimumPixels)) ||
            (Operations & AuroraGpuInterface.BackendOperationBlendFill) == 0u)
        {
            return false;
        }
        return SubmitFill(width, height, colour, pixels, true);
    }

    public bool TryFillBatch(int pixelCount, uint[] pixels,
        IReadOnlyList<AuroraGpuFillOperation> operations)
    {
        if (!BatchSupported || pixels is null || pixelCount <= 0 || pixelCount > pixels.Length ||
            operations is null || operations.Count == 0 || operations.Count > 128)
        {
            return false;
        }
        var captured = operations.ToArray();
        if (_automaticRouting && (_nativeBlendMinimumPixels == int.MaxValue ||
            pixelCount < _nativeBlendMinimumPixels ||
            captured.Any(operation => !operation.Blend)))
        {
            return false;
        }
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lifecycle)
        {
            if (_disposed)
            {
                return false;
            }
            _requests.Add(new FillRequest(0, 0, 0, pixels, false, completion, pixelCount, captured));
        }
        return completion.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Calibrates this host before opting into automatic routing. Small operations
    /// remain software; native blend is selected only with a sustained 20% margin.
    /// Call once before attaching this provider to a running machine.
    /// </summary>
    public void ConfigureAutomaticRouting()
    {
        if (_automaticRouting)
        {
            return;
        }
        var measurements = new List<string>();
        int threshold = int.MaxValue;
        if (BatchSupported)
        {
            foreach (int count in new[] { 512000, 1024000, 2073600 })
            {
                uint[] source = new uint[count];
                uint[] stage = new uint[count];
                uint[] nativeOutput = new uint[count];
                uint[] softwareOutput = new uint[count];
                for (int pixel = 0; pixel < count; pixel++)
                {
                    source[pixel] = unchecked((uint)(pixel * 783739));
                }
                const uint colour = 0x80605040u;
                AuroraGpuFillOperation[] operations = [new(0, count, 1, colour, true)];
                source.CopyTo(stage, 0);
                if (!TryFillBatch(count, stage, operations))
                {
                    continue;
                }
                source.CopyTo(softwareOutput, 0);
                for (int pixel = 0; pixel < count; pixel++)
                {
                    softwareOutput[pixel] = AuroraGpu2DEngine.BlendOver(colour, softwareOutput[pixel]);
                }
                bool sustained = true;
                double nativeTotal = 0;
                double softwareTotal = 0;
                for (int round = 0; round < 5; round++)
                {
                    // Restore authoritative inputs outside both timed paths. Native
                    // work includes capture and commit; software blends in place.
                    source.CopyTo(nativeOutput, 0);
                    source.CopyTo(softwareOutput, 0);
                    var timer = Stopwatch.StartNew();
                    nativeOutput.CopyTo(stage, 0);
                    bool completed = TryFillBatch(count, stage, operations);
                    if (completed)
                    {
                        stage.CopyTo(nativeOutput, 0);
                    }
                    double native = timer.Elapsed.TotalMilliseconds;
                    timer.Restart();
                    for (int pixel = 0; pixel < count; pixel++)
                    {
                        softwareOutput[pixel] = AuroraGpu2DEngine.BlendOver(colour, softwareOutput[pixel]);
                    }
                    double software = timer.Elapsed.TotalMilliseconds;
                    completed &= nativeOutput.AsSpan().SequenceEqual(softwareOutput);
                    sustained &= completed && native <= software * 0.8;
                    nativeTotal += native;
                    softwareTotal += software;
                }
                measurements.Add($"{count} pixels: native={nativeTotal:F3} ms, CPU={softwareTotal:F3} ms, accepted={sustained}");
                if (sustained)
                {
                    threshold = count;
                    break;
                }
            }
        }
        _nativeBlendMinimumPixels = threshold;
        _operations = threshold == int.MaxValue ? 0u : AuroraGpuInterface.BackendOperationBlendFill;
        AutomaticRoutingCalibration = string.Join("; ", measurements);
        if (measurements.Count == 0)
        {
            AutomaticRoutingCalibration = "Native batching unavailable; software routing selected.";
        }
        _automaticRouting = true;
    }

    private bool SubmitFill(int width, int height, uint colour, uint[] pixels, bool blend)
    {
        if (pixels is null || width <= 0 || height <= 0 ||
            (long)width * height > pixels.Length)
        {
            return false;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lifecycle)
        {
            if (_disposed)
            {
                return false;
            }
            _requests.Add(new FillRequest(width, height, colour, pixels, blend, completion));
        }

        return completion.Task.GetAwaiter().GetResult();
    }

    private void RunWorker()
    {
        MacOpenGlContext? context = null;
        try
        {
            context = new MacOpenGlContext();
            NativeRenderer = context.Renderer;
            NativeVendor = context.Vendor;
            NativeVersion = context.Version;
            BlendUnavailableReason = context.BlendUnavailableReason;
            BatchSupported = context.BatchSupported;
            BatchUnavailableReason = context.BatchUnavailableReason;
            _operations = AuroraGpuInterface.BackendOperationOpaqueFill |
                (context.BlendSupported ? AuroraGpuInterface.BackendOperationBlendFill : 0u);
            _startup.TrySetResult(null);

            FillRequest? request;
            while (_requests.TryTake(out request, Timeout.Infinite))
            {
                bool completed;
                try
                {
                    completed = request.BatchOperations is not null
                        ? context.TryBatch(request.PixelCount, request.Pixels, request.BatchOperations)
                        : request.Blend
                        ? context.TryBlend(request.Width, request.Height, request.Colour, request.Pixels)
                        : context.TryFill(request.Width, request.Height, request.Colour, request.Pixels);
                }
                catch (Exception)
                {
                    completed = false;
                }

                if (completed)
                {
                    Interlocked.Increment(ref _executedOperations);
                }
                TaskCompletionSource<bool> completion = request.Completion;
                request = null;
                completion.TrySetResult(completed);
            }
        }
        catch (Exception exception)
        {
            _startup.TrySetResult(exception.Message);
            lock (_lifecycle)
            {
                _disposed = true;
                _requests.CompleteAdding();
            }
            while (_requests.TryTake(out FillRequest? request))
            {
                request.Completion.TrySetResult(false);
            }
        }
        finally
        {
            context?.Dispose();
        }
    }

    /// <summary>Completes accepted requests and releases GL on its owning thread.</summary>
    public void Dispose()
    {
        lock (_lifecycle)
        {
            _disposed = true;
            if (!_collectionDisposed)
            {
                _requests.CompleteAdding();
            }
        }
        _worker.Join();
        lock (_lifecycle)
        {
            if (!_collectionDisposed)
            {
                _requests.Dispose();
                _collectionDisposed = true;
            }
        }
    }
}
