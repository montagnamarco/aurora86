using System.Diagnostics;
using System.Runtime.InteropServices;
using Aurora86.Core.Devices.Audio;

namespace Aurora86.Core.Host.Midi;

/// <summary>
/// Sends an MPU-401's MIDI byte stream through the Windows multimedia API,
/// introduced with Windows 3.1. A background worker owns the endpoint and
/// every native buffer; guest writes and resets only touch a bounded queue.
/// A driver that stops returning buffers retains its worker and endpoint,
/// rather than allowing a timed-out caller to free memory still in use.
/// </summary>
public sealed class WinMmMidiSink : IMidiSink, IDisposable
{
    private const int QueueCapacity = 4096;
    private const int PollMilliseconds = 5;
    private const int NotReadyMilliseconds = 500;
    private const byte EndOfExclusive = 0xF7;
    private static readonly object RegistryLock = new();
    private static readonly Dictionary<IWinMmApi, HashSet<uint>> Endpoints =
        new(ReferenceEqualityComparer.Instance);
    private static readonly uint HeaderSize = checked((uint)Marshal.SizeOf<MidiHeader>());
    private static readonly int FlagsOffset = Marshal.OffsetOf<MidiHeader>(nameof(MidiHeader.Flags)).ToInt32();

    private readonly object _stateLock = new();
    private readonly IWinMmApi _api;
    private readonly uint _deviceId;
    private readonly TimeSpan _shutdownTimeout;
    private readonly TimeSpan _operationTimeout;
    private readonly byte[] _queue = new byte[QueueCapacity];
    private readonly AutoResetEvent _wake = new(false);
    private readonly TaskCompletionSource _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly MidiMessageFramer _framer;
    private readonly Thread _worker;
    private int _head;
    private int _count;
    private long _generation;
    private long _workerGeneration;
    private long _droppedSysExCount;
    private bool _ready;
    private bool _failed;
    private bool _stop;
    private bool _finished;
    private string? _lastError;

    // Only the worker accesses these fields. Prepared memory is released only
    // after WinMM explicitly returns ownership through successful unprepare.
    private nint _handle;
    private nint _header;
    private nint _data;
    private bool _prepared;
    private bool _submitted;
    private bool _submissionUncertain;
    private bool _resetStarted;
    private bool _needsEndOfExclusive;
    private bool _bufferIsEndOfExclusive;

    private WinMmMidiSink(uint deviceId, IWinMmApi api, TimeSpan shutdownTimeout, TimeSpan operationTimeout)
    {
        _deviceId = deviceId;
        _api = api;
        _shutdownTimeout = shutdownTimeout;
        _operationTimeout = operationTimeout;
        _framer = new MidiMessageFramer(new MessageSink(this));
        _worker = new Thread(Run)
        {
            IsBackground = true,
            Name = $"WinMM MIDI {deviceId}"
        };
    }

    /// <summary>The latest native failure or timeout, including quarantined cleanup.</summary>
    public string? LastError => Volatile.Read(ref _lastError);

    /// <summary>The lifetime number of overlong guest SysEx messages discarded by the framer.</summary>
    public ulong DroppedSysExCount => unchecked((ulong)Interlocked.Read(ref _droppedSysExCount));

    /// <summary>Completes only after native ownership and the endpoint reservation have ended.</summary>
    public Task Completion => _completion.Task;

    /// <summary>Lists host outputs without opening them; other platforms return an empty list.</summary>
    public static IReadOnlyList<MidiOutputDevice> GetDevices()
    {
        return WinMmMidiApi.GetDevices();
    }

    /// <summary>Opens an explicitly selected Windows endpoint with bounded startup and shutdown waits.</summary>
    public static WinMmMidiSink Open(uint deviceId)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("WinMM MIDI output requires Windows.");
        }

        if (deviceId == uint.MaxValue || deviceId >= WinMmMidiApi.Instance.GetNumDevs())
        {
            throw new ArgumentOutOfRangeException(nameof(deviceId), "Select an enumerated MIDI output device.");
        }

        return Open(deviceId, WinMmMidiApi.Instance);
    }

    /// <summary>Injects the native surface and short deadlines for deterministic ownership tests.</summary>
    internal static WinMmMidiSink Open(
        uint deviceId,
        IWinMmApi api,
        TimeSpan? startupTimeout = null,
        TimeSpan? shutdownTimeout = null,
        TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        TimeSpan startup = ValidateTimeout(startupTimeout, TimeSpan.FromSeconds(5), nameof(startupTimeout));
        TimeSpan shutdown = ValidateTimeout(shutdownTimeout, TimeSpan.FromSeconds(5), nameof(shutdownTimeout));
        TimeSpan operation = ValidateTimeout(operationTimeout, TimeSpan.FromSeconds(30), nameof(operationTimeout));
        var sink = new WinMmMidiSink(deviceId, api, shutdown, operation);
        bool reserved = false;
        try
        {
            sink.ReserveEndpoint();
            reserved = true;
            sink._worker.Start();
        }
        catch
        {
            if (reserved)
            {
                sink.ReleaseEndpoint();
            }

            sink._wake.Dispose();
            throw;
        }

        try
        {
            sink._startup.Task.WaitAsync(startup).GetAwaiter().GetResult();
            return sink;
        }
        catch (TimeoutException)
        {
            sink.Fail("WinMM MIDI startup timed out; the endpoint remains reserved until cleanup finishes.");
            sink.RequestStop();
            throw;
        }
    }

    /// <inheritdoc />
    public bool TryWriteByte(byte value)
    {
        lock (_stateLock)
        {
            if (!_ready || _failed || _stop || _count == QueueCapacity)
            {
                return false;
            }

            _queue[(_head + _count) % QueueCapacity] = value;
            _count++;
            _wake.Set();
            return true;
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        lock (_stateLock)
        {
            if (_stop || _finished)
            {
                return;
            }

            _generation++;
            _head = 0;
            _count = 0;
            _wake.Set();
        }
    }

    /// <summary>Stops accepting bytes and waits only for the configured shutdown deadline.</summary>
    public void Dispose()
    {
        RequestStop();
        if (!_worker.Join(_shutdownTimeout))
        {
            Fail("WinMM MIDI shutdown timed out; the worker retains native ownership until cleanup finishes.");
        }
    }

    private void WriteShort(uint message)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            CheckDispatch();
            uint result = _api.SendShort(_handle, message);
            if (result == WinMmResult.Success)
            {
                return;
            }

            RetryNotReady(result, "midiOutShortMsg", started);
        }
    }

    private void WriteSysEx(ReadOnlySpan<byte> message)
    {
        CheckDispatch();
        AllocateBuffer(message);
        PrepareBuffer();
        SubmitBuffer(false);
        ReturnBuffer(false);
    }

    /// <summary>Runs all endpoint operations serially, including cleanup after startup failure.</summary>
    private void Run()
    {
        try
        {
            CheckResult(_api.Open(out _handle, _deviceId), "midiOutOpen");
            lock (_stateLock)
            {
                _ready = !_stop && !_failed;
            }

            _startup.TrySetResult();
            RunMessages();
        }
        catch (Exception exception)
        {
            Fail(exception.Message);
            _startup.TrySetException(exception);
        }
        finally
        {
            // Cleanup deliberately has no deadline that frees native resources.
            // Caller deadlines only stop their waits; a late driver can recover.
            CleanupUntilClosed();
            lock (_stateLock)
            {
                _ready = false;
                _finished = true;
            }

            ReleaseEndpoint();
            _wake.Dispose();
            _completion.TrySetResult();
        }
    }

    /// <summary>Consumes bytes only after applying the newest reset generation.</summary>
    private void RunMessages()
    {
        while (true)
        {
            byte value = 0;
            bool hasByte;
            bool reset;
            lock (_stateLock)
            {
                if (_stop || _failed)
                {
                    return;
                }

                reset = _workerGeneration != _generation;
                hasByte = !reset && _count != 0;
                if (hasByte)
                {
                    value = _queue[_head];
                    _head = (_head + 1) % QueueCapacity;
                    _count--;
                }
            }

            if (reset)
            {
                ResetNativeStream();
                _framer.Reset();
                lock (_stateLock)
                {
                    _workerGeneration = _generation;
                }

                continue;
            }

            if (!hasByte)
            {
                _wake.WaitOne();
                continue;
            }

            try
            {
                CheckDispatch();
                _framer.WriteByte(value);
            }
            catch (GenerationChangedException)
            {
                // A byte can dispatch SysEx followed by a short message. Unwind
                // both callbacks before resetting the non-reentrant parser.
                _framer.Reset();
            }
            finally
            {
                Interlocked.Exchange(ref _droppedSysExCount, unchecked((long)_framer.DroppedSysExCount));
            }
        }
    }

    /// <summary>Reserves the next old-generation submission without holding a lock across WinMM.</summary>
    private void CheckDispatch()
    {
        lock (_stateLock)
        {
            if (_stop || _failed || _workerGeneration != _generation)
            {
                throw new GenerationChangedException();
            }
        }
    }

    /// <summary>Copies a borrowed parser span into stable, initially unprepared native memory.</summary>
    private unsafe void AllocateBuffer(ReadOnlySpan<byte> message)
    {
        _data = Marshal.AllocHGlobal(message.Length);
        message.CopyTo(new Span<byte>((void*)_data, message.Length));
        _header = Marshal.AllocHGlobal(checked((int)HeaderSize));
        var header = new MidiHeader
        {
            Data = _data,
            BufferLength = checked((uint)message.Length)
        };
        Marshal.StructureToPtr(header, _header, false);
    }

    /// <summary>Prepares the stable header, retaining it even if a native exception makes ownership uncertain.</summary>
    private void PrepareBuffer()
    {
        _prepared = true;
        uint result = _api.Prepare(_handle, _header, HeaderSize);
        uint ownershipFlags = MidiHeaderFlags.Prepared | MidiHeaderFlags.InQueue;
        if (result != WinMmResult.Success && (ReadFlags() & ownershipFlags) == 0)
        {
            _prepared = false;
        }

        CheckResult(result, "midiOutPrepareHeader");
    }

    /// <summary>Retries only unaccepted NOTREADY submissions; accepted buffers are never submitted twice.</summary>
    private void SubmitBuffer(bool cleanup)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            if (!cleanup)
            {
                CheckDispatch();
            }

            _submissionUncertain = true;
            uint result = _api.SendLong(_handle, _header, HeaderSize);
            _submissionUncertain = false;
            if (result == WinMmResult.Success)
            {
                _submitted = true;
                return;
            }

            if ((ReadFlags() & MidiHeaderFlags.InQueue) != 0)
            {
                _submitted = true;
                throw NativeError("midiOutLongMsg returned an error with INQUEUE set", result);
            }

            RetryNotReady(result, "midiOutLongMsg", started);
        }
    }

    /// <summary>Waits for returned long data, then relinquishes the prepared header before freeing it.</summary>
    private void ReturnBuffer(bool cleanup)
    {
        long started = Stopwatch.GetTimestamp();
        while (_submitted || _submissionUncertain || (ReadFlags() & MidiHeaderFlags.InQueue) != 0)
        {
            if (!cleanup)
            {
                CheckDispatch();
            }

            if ((ReadFlags() & MidiHeaderFlags.Done) != 0)
            {
                break;
            }

            WaitForDriver(started, cleanup, "MIDI long-message completion timed out.");
        }

        while (_prepared)
        {
            if (!cleanup)
            {
                CheckDispatch();
            }

            uint result = _api.Unprepare(_handle, _header, HeaderSize);
            if (result == WinMmResult.Success)
            {
                _prepared = false;
                break;
            }

            if (!cleanup && result != WinMmResult.StillPlaying)
            {
                throw NativeError("midiOutUnprepareHeader", result);
            }

            if (cleanup && result != WinMmResult.StillPlaying)
            {
                Fail(NativeError("midiOutUnprepareHeader", result).Message);
            }

            WaitForDriver(started, cleanup, "MIDI header release timed out.");
        }

        FreeBuffer();
    }

    /// <summary>Resets host output and closes an interrupted on-wire SysEx with a separate long EOX.</summary>
    private void ResetNativeStream()
    {
        if (!_resetStarted)
        {
            bool interrupted = _header != 0
                && (_submitted || _submissionUncertain || (ReadFlags() & MidiHeaderFlags.InQueue) != 0)
                && (ReadFlags() & MidiHeaderFlags.Done) == 0;
            _needsEndOfExclusive |= interrupted;
            CheckResult(_api.Reset(_handle), "midiOutReset");
            _resetStarted = true;
        }

        if ((_header != 0 || _data != 0) && !_bufferIsEndOfExclusive)
        {
            ReturnBuffer(true);
        }

        if (_needsEndOfExclusive)
        {
            if (_header == 0)
            {
                AllocateBuffer([EndOfExclusive]);
                _bufferIsEndOfExclusive = true;
            }

            try
            {
                if (!_prepared)
                {
                    PrepareBuffer();
                }

                if (!_submitted && !_submissionUncertain)
                {
                    SubmitBuffer(true);
                }
            }
            catch
            {
                // A failed EOX submission must release its preparation before
                // retrying. The obligation survives this buffer and all retries.
                // An uncertain/queued submission is waited out, never replayed.
                if (!_submitted && !_submissionUncertain)
                {
                    ReturnBuffer(true);
                }

                throw;
            }

            ReturnBuffer(true);
            _needsEndOfExclusive = false;
        }

        _resetStarted = false;
    }

    /// <summary>Keeps failed native ownership quarantined on this worker until the driver releases it.</summary>
    private void CleanupUntilClosed()
    {
        while (_handle != 0 || _header != 0 || _data != 0)
        {
            try
            {
                if (_handle != 0)
                {
                    ResetNativeStream();
                    CheckResult(_api.Close(_handle), "midiOutClose");
                    _handle = 0;
                }
                else
                {
                    // No prepare operation is possible without an open handle.
                    FreeBuffer();
                }
            }
            catch (Exception exception)
            {
                Fail(exception.Message);
                _wake.WaitOne(PollMilliseconds);
            }
        }
    }

    /// <summary>Reads only WinMM's asynchronously updated flags, never a copied stale header.</summary>
    private uint ReadFlags()
    {
        if (_header == 0)
        {
            return 0;
        }

        return unchecked((uint)Marshal.ReadInt32(_header, FlagsOffset));
    }

    /// <summary>Frees memory only after preparation ownership has ended.</summary>
    private void FreeBuffer()
    {
        if (_prepared)
        {
            throw new InvalidOperationException("Cannot free a prepared MIDI header.");
        }

        Marshal.FreeHGlobal(_header);
        Marshal.FreeHGlobal(_data);
        _header = 0;
        _data = 0;
        _submitted = false;
        _submissionUncertain = false;
        _bufferIsEndOfExclusive = false;
    }

    /// <summary>Bounds NOTREADY retry time while allowing reset and stop to wake the worker early.</summary>
    private void RetryNotReady(uint result, string operation, long started)
    {
        if (result != WinMmResult.NotReady)
        {
            throw NativeError(operation, result);
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        if (elapsed >= _operationTimeout || elapsed.TotalMilliseconds >= NotReadyMilliseconds)
        {
            throw new IOException($"{operation} remained NOTREADY beyond the retry deadline.");
        }

        _wake.WaitOne(PollMilliseconds);
    }

    /// <summary>Reports overdue cleanup but never turns a caller timeout into permission to free a buffer.</summary>
    private void WaitForDriver(long started, bool cleanup, string message)
    {
        if (Stopwatch.GetElapsedTime(started) >= _operationTimeout)
        {
            if (!cleanup)
            {
                throw new TimeoutException(message);
            }

            Fail(message);
        }

        _wake.WaitOne(PollMilliseconds);
    }

    private void RequestStop()
    {
        lock (_stateLock)
        {
            _stop = true;
            _ready = false;
            _head = 0;
            _count = 0;
            if (!_finished)
            {
                _wake.Set();
            }
        }
    }

    private void Fail(string message)
    {
        Volatile.Write(ref _lastError, message);
        lock (_stateLock)
        {
            _failed = true;
            _ready = false;
            _head = 0;
            _count = 0;
        }
    }

    private void ReserveEndpoint()
    {
        lock (RegistryLock)
        {
            if (!Endpoints.TryGetValue(_api, out HashSet<uint>? devices))
            {
                devices = [];
                Endpoints.Add(_api, devices);
            }

            if (!devices.Add(_deviceId))
            {
                throw new InvalidOperationException($"MIDI endpoint {_deviceId} is already open or awaiting cleanup.");
            }
        }
    }

    private void ReleaseEndpoint()
    {
        lock (RegistryLock)
        {
            HashSet<uint> devices = Endpoints[_api];
            devices.Remove(_deviceId);
            if (devices.Count == 0)
            {
                Endpoints.Remove(_api);
            }
        }
    }

    private static TimeSpan ValidateTimeout(TimeSpan? supplied, TimeSpan fallback, string name)
    {
        TimeSpan timeout = supplied ?? fallback;
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(name);
        }

        return timeout;
    }

    private static void CheckResult(uint result, string operation)
    {
        if (result != WinMmResult.Success)
        {
            throw NativeError(operation, result);
        }
    }

    private static IOException NativeError(string operation, uint result)
    {
        return new IOException($"{operation} failed with WinMM result {result}.");
    }

    /// <summary>Unwinds a parser callback whose native submission belongs to a superseded stream.</summary>
    private sealed class GenerationChangedException : Exception
    {
    }

    /// <summary>Keeps native dispatch private to the worker-owned parser.</summary>
    private sealed class MessageSink(WinMmMidiSink owner) : IMidiMessageSink
    {
        public void WriteShort(uint message)
        {
            owner.WriteShort(message);
        }

        public void WriteSysEx(ReadOnlySpan<byte> message)
        {
            owner.WriteSysEx(message);
        }
    }
}
