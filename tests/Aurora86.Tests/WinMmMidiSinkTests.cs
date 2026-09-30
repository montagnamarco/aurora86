using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Aurora86.Core.Host.Midi;

namespace Aurora86.Tests;

/// <summary>
/// The WinMM worker against a controllable native seam. These tests make
/// header ownership and driver completion observable without opening a MIDI
/// endpoint or relying on time-based scheduling.
/// </summary>
public sealed class WinMmMidiSinkTests
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public void WorkerDeliversOrderedShortAndSysExMessages()
    {
        using var api = new FakeWinMmApi();
        WinMmMidiSink sink = Open(api);
        try
        {
            Write(sink, 0x90, 0x3C, 0x40, 0xF0, 0x7D, 0x01, 0xF7);

            Wait(api.ShortSent);
            Wait(api.LongSent);
            Assert.Equal(new uint[] { 0x00403C90 }, api.ShortMessages);
            Assert.Equal(new byte[][] { [0xF0, 0x7D, 0x01, 0xF7] }, api.LongMessages);
            Assert.Equal(
                new[] { "Open", "Short:00403C90", "Prepare", "Long:F07D01F7", "Unprepare" },
                api.Calls.Take(5));
        }
        finally
        {
            Dispose(sink, api);
        }
    }

    [Fact]
    public void AsynchronousLongMessageRetainsTheExactHeaderAndDataUntilSuccessfulUnprepare()
    {
        using var api = new FakeWinMmApi { CompleteLongSynchronously = false };
        WinMmMidiSink sink = Open(api);
        try
        {
            Write(sink, 0xF0, 0x7D, 0x11, 0x22, 0xF7);
            Wait(api.LongSent);

            Assert.NotEqual(nint.Zero, api.PendingHeader);
            Assert.NotEqual(nint.Zero, api.PendingData);
            Assert.Equal(new byte[] { 0xF0, 0x7D, 0x11, 0x22, 0xF7 }, api.ReadPendingData());
            Assert.Equal(0, api.UnprepareCallCount);

            api.CompletePendingLong();
            Wait(api.Unprepared);
            Assert.Equal(1, api.UnprepareCallCount);
        }
        finally
        {
            Dispose(sink, api);
        }
    }

    [Fact]
    public void NotReadyRetriesWithoutDuplicatingThePreparedLongMessage()
    {
        using var api = new FakeWinMmApi();
        api.LongResults.Enqueue(WinMmResult.NotReady);
        api.LongResults.Enqueue(WinMmResult.Success);
        WinMmMidiSink sink = Open(api);
        try
        {
            Write(sink, 0xF0, 0x7D, 0x44, 0xF7);
            Wait(api.LongSent);
            Wait(api.Unprepared);

            // The same prepared header is offered twice, and only the second
            // offer is accepted: the fake records every call but only the
            // messages the driver took.
            Assert.Equal(2, api.SendLongCallCount);
            Assert.Equal(1, api.PrepareCallCount);
            Assert.Equal(2, api.Calls.Count(call => call == "Long:F07D44F7"));
            Assert.Equal(new byte[][] { [0xF0, 0x7D, 0x44, 0xF7] }, api.LongMessages);
            Assert.Null(sink.LastError);
        }
        finally
        {
            Dispose(sink, api);
        }
    }

    [Fact]
    public void PrepareAndSendFailuresReleaseOnlyResourcesTheDriverAccepted()
    {
        using var prepareFailureApi = new FakeWinMmApi();
        prepareFailureApi.PrepareResults.Enqueue(1);
        WinMmMidiSink prepareFailureSink = Open(prepareFailureApi);
        try
        {
            Write(prepareFailureSink, 0xF0, 0x7D, 0x01, 0xF7);
            WaitUntil(() => prepareFailureSink.LastError is not null);
            Assert.Equal(0, prepareFailureApi.SendLongCallCount);
            Assert.Equal(0, prepareFailureApi.UnprepareCallCount);
            Assert.NotNull(prepareFailureSink.LastError);
            Assert.False(prepareFailureSink.TryWriteByte(0x90));
        }
        finally
        {
            Dispose(prepareFailureSink, prepareFailureApi);
        }

        using var sendFailureApi = new FakeWinMmApi();
        sendFailureApi.LongResults.Enqueue(1);
        WinMmMidiSink sendFailureSink = Open(sendFailureApi);
        try
        {
            Write(sendFailureSink, 0xF0, 0x7D, 0x02, 0xF7);
            WaitUntil(() => sendFailureSink.LastError is not null);
            Wait(sendFailureApi.Unprepared);
            Assert.Equal(1, sendFailureApi.PrepareCallCount);
            Assert.Equal(1, sendFailureApi.SendLongCallCount);
            Assert.Equal(1, sendFailureApi.UnprepareCallCount);
            Assert.NotNull(sendFailureSink.LastError);
        }
        finally
        {
            Dispose(sendFailureSink, sendFailureApi);
        }
    }

    [Fact]
    public void PrepareFailureWithDriverOwnershipWaitsForReturnBeforeUnprepareAndFree()
    {
        using var api = new FakeWinMmApi();
        api.PrepareResults.Enqueue(1);
        api.PrepareFailureFlags = MidiHeaderFlags.InQueue;
        WinMmMidiSink sink = Open(api);
        try
        {
            Write(sink, 0xF0, 0x7D, 0x03, 0xF7);
            Wait(api.PrepareCalled);
            Wait(api.ResetCalled);
            Assert.Equal(0, api.UnprepareCallCount);
            Assert.NotEqual(nint.Zero, api.PendingHeader);

            api.CompletePendingLong();
            Wait(api.Unprepared);
            Assert.NotNull(sink.LastError);
        }
        finally
        {
            Dispose(sink, api);
        }
    }

    [Fact]
    public void FailedCleanupEoxSubmissionIsReleasedThenRetriedBeforeTheEndpointCloses()
    {
        using var api = new FakeWinMmApi { CompleteLongSynchronously = false };
        api.LongResults.Enqueue(WinMmResult.Success);
        api.LongResults.Enqueue(1);
        api.LongResults.Enqueue(WinMmResult.Success);
        WinMmMidiSink sink = Open(api);
        try
        {
            Write(sink, 0xF0, 0x7D, 0x04, 0xF7);
            Wait(api.LongSent);
            sink.Reset();

            // The SysEx must still be on the wire when the worker resets,
            // or it rightly needs no EOX at all.
            Wait(api.ResetCalled);
            api.CompleteLongSynchronously = true;
            api.CompletePendingLong();

            // The failed EOX makes the sink fail, so cleanup closes the
            // endpoint -- but only after the EOX was released and retried.
            Wait(api.Closed);
            Assert.Equal(new byte[] { 0xF7 }, api.LongMessages[1]);
            string[] calls = api.Calls.ToArray();
            Assert.Equal(2, calls.Count(call => call == "Long:F7"));
            Assert.True(Array.LastIndexOf(calls, "Long:F7") < Array.IndexOf(calls, "Close"));
            Assert.Equal(1, api.CloseCallCount);
        }
        finally
        {
            Dispose(sink, api);
        }
    }

    [Fact]
    public void ResetWaitsForThePendingLongThenSendsAnEoxLongBeforeNewGenerationOutput()
    {
        using var api = new FakeWinMmApi { CompleteLongSynchronously = false };
        WinMmMidiSink sink = Open(api);
        try
        {
            Write(sink, 0xF0, 0x7D, 0x20, 0xF7);
            Wait(api.LongSent);
            sink.Reset();
            Wait(api.ResetCalled);

            api.CompletePendingLong();
            Wait(api.SecondLongSent);
            Assert.Equal(new byte[] { 0xF7 }, api.LongMessages[1]);
            Assert.DoesNotContain(0x000000F7u, api.ShortMessages);

            api.CompletePendingLong();
            WaitUntil(() => api.UnprepareCallCount >= 2);
            Write(sink, 0xC1, 0x10);
            Wait(api.ShortSent);
            Assert.Contains(0x000010C1u, api.ShortMessages);
        }
        finally
        {
            Dispose(sink, api);
        }
    }

    [Fact]
    public void ResetClearsAFullRawQueueWithoutNeedingAnotherQueueSlot()
    {
        using var api = new FakeWinMmApi();
        api.ShortGate.Reset();
        WinMmMidiSink sink = Open(api);
        try
        {
            Write(sink, 0x90, 0x10, 0x20);
            Wait(api.ShortEntered);

            for (int index = 0; index < 4096; index++)
            {
                Assert.True(sink.TryWriteByte(0x40));
            }

            Assert.False(sink.TryWriteByte(0x41));
            sink.Reset();
            Assert.True(sink.TryWriteByte(0xC2));
            Assert.True(sink.TryWriteByte(0x07));

            api.ShortGate.Set();
            Wait(api.ShortSent);
        }
        finally
        {
            Dispose(sink, api);
        }
    }

    [Fact]
    public void ResetDuringImplicitSysExThenF6PreventsTheStaleSecondCallback()
    {
        using var api = new FakeWinMmApi { CompleteLongSynchronously = false };
        WinMmMidiSink sink = Open(api);
        try
        {
            Write(sink, 0xF0, 0x7D, 0x01, 0xF6);
            Wait(api.LongSent);

            sink.Reset();
            api.CompletePendingLong();
            Wait(api.ResetCalled);
            Assert.DoesNotContain("Short:000000F6", api.Calls);
        }
        finally
        {
            Dispose(sink, api);
        }
    }

    [Fact]
    public async Task StillPlayingRetainsThePendingHeaderAndRepeatedDisposeDoesNotCloseEarly()
    {
        using var api = new FakeWinMmApi { CompleteLongSynchronously = false };
        api.UnprepareResults.Enqueue(WinMmResult.StillPlaying);
        api.UnprepareResults.Enqueue(WinMmResult.Success);
        api.AllowUnprepareRetry.Reset();
        WinMmMidiSink sink = Open(api, shutdownTimeout: ShortTimeout);
        try
        {
            Write(sink, 0xF0, 0x7D, 0x55, 0xF7);
            Wait(api.LongSent);
            nint header = api.PendingHeader;

            sink.Dispose();
            sink.Dispose();
            Wait(api.ResetCalled);
            api.CompletePendingLong();
            Wait(api.UnprepareCalled);
            Assert.Equal(header, api.LastUnpreparedHeader);
            Assert.Equal(0, api.CloseCallCount);

            // The interrupted SysEx is closed with an EOX after the header is
            // released; this driver returns that one at once, so the worker
            // can then close the endpoint.
            api.CompleteLongSynchronously = true;
            api.AllowUnprepareRetry.Set();
            Wait(api.Closed);
            await sink.Completion.WaitAsync(TestTimeout);
            Assert.Equal(1, api.CloseCallCount);
        }
        finally
        {
            api.ReleaseAllGates();
            sink.Dispose();
            await sink.Completion.WaitAsync(TestTimeout);
        }
    }

    [Fact]
    public async Task OpenAndBlockedDriverShutdownRemainBoundedUntilTheWorkerCanCleanUp()
    {
        using var lateOpenApi = new FakeWinMmApi();
        lateOpenApi.OpenGate.Reset();
        Assert.Throws<TimeoutException>(() =>
            WinMmMidiSink.Open(0, lateOpenApi, ShortTimeout, ShutdownTimeout, OperationTimeout));
        lateOpenApi.OpenGate.Set();
        Wait(lateOpenApi.Closed);

        using var blockedApi = new FakeWinMmApi();
        blockedApi.ShortGate.Reset();
        WinMmMidiSink sink = Open(blockedApi, shutdownTimeout: ShortTimeout);
        try
        {
            Write(sink, 0x90, 0x10, 0x20);
            Wait(blockedApi.ShortEntered);

            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            sink.Dispose();
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1));
            Assert.False(sink.Completion.IsCompleted);
            Assert.Equal(0, blockedApi.CloseCallCount);

            blockedApi.ShortGate.Set();
            Wait(blockedApi.Closed);
            await sink.Completion.WaitAsync(TestTimeout);
        }
        finally
        {
            blockedApi.ReleaseAllGates();
            sink.Dispose();
            await sink.Completion.WaitAsync(TestTimeout);
        }
    }

    private static WinMmMidiSink Open(
        FakeWinMmApi api,
        TimeSpan? startupTimeout = null,
        TimeSpan? shutdownTimeout = null,
        TimeSpan? operationTimeout = null)
    {
        return WinMmMidiSink.Open(
            0,
            api,
            startupTimeout ?? StartupTimeout,
            shutdownTimeout ?? ShutdownTimeout,
            operationTimeout ?? OperationTimeout);
    }

    private static void Write(WinMmMidiSink sink, params byte[] bytes)
    {
        foreach (byte value in bytes)
        {
            Assert.True(sink.TryWriteByte(value));
        }
    }

    private static void Wait(ManualResetEventSlim signal)
    {
        Assert.True(signal.Wait(TestTimeout), "The MIDI worker did not reach the expected native call.");
    }

    private static void WaitUntil(Func<bool> condition)
    {
        Assert.True(
            SpinWait.SpinUntil(condition, TestTimeout),
            "The MIDI worker did not publish the expected lifecycle state.");
    }

    private static void Dispose(WinMmMidiSink sink, FakeWinMmApi api)
    {
        api.ReleaseAllGates();
        sink.Dispose();
        Assert.True(sink.Completion.Wait(TestTimeout));
    }

    private sealed class FakeWinMmApi : IWinMmApi, IDisposable
    {
        private readonly object _sync = new();
        private readonly Dictionary<nint, nint> _headerData = [];
        private nint _pendingHeader;
        private nint _pendingData;
        private int _longMessageCount;
        private bool _completeLongSynchronously = true;

        public ConcurrentQueue<uint> PrepareResults { get; } = new();
        public ConcurrentQueue<uint> LongResults { get; } = new();
        public ConcurrentQueue<uint> UnprepareResults { get; } = new();
        public List<uint> ShortMessages { get; } = [];
        public List<byte[]> LongMessages { get; } = [];
        public ConcurrentQueue<string> Calls { get; } = new();
        public ManualResetEventSlim OpenGate { get; } = new(initialState: true);
        public ManualResetEventSlim ShortGate { get; } = new(initialState: true);
        public ManualResetEventSlim AllowUnprepareRetry { get; } = new(initialState: true);
        public ManualResetEventSlim ShortEntered { get; } = new();
        public ManualResetEventSlim ShortSent { get; } = new();
        public ManualResetEventSlim LongSent { get; } = new();
        public ManualResetEventSlim SecondLongSent { get; } = new();
        public ManualResetEventSlim PrepareCalled { get; } = new();
        public ManualResetEventSlim ResetCalled { get; } = new();
        public ManualResetEventSlim UnprepareCalled { get; } = new();
        public ManualResetEventSlim Unprepared { get; } = new();
        public ManualResetEventSlim Closed { get; } = new();
        public bool CompleteLongSynchronously
        {
            get
            {
                lock (_sync)
                {
                    return _completeLongSynchronously;
                }
            }
            set
            {
                lock (_sync)
                {
                    _completeLongSynchronously = value;
                }
            }
        }
        public uint PrepareFailureFlags { get; set; }
        public nint PendingHeader
        {
            get
            {
                lock (_sync)
                {
                    return _pendingHeader;
                }
            }
        }

        public nint PendingData
        {
            get
            {
                lock (_sync)
                {
                    return _pendingData;
                }
            }
        }
        public nint LastUnpreparedHeader { get; private set; }
        public int PrepareCallCount { get; private set; }
        public int SendLongCallCount { get; private set; }
        public int UnprepareCallCount { get; private set; }
        public int CloseCallCount { get; private set; }

        public uint Open(out nint handle, uint deviceId)
        {
            OpenGate.Wait();
            Calls.Enqueue("Open");
            handle = (nint)1;
            return WinMmResult.Success;
        }

        public uint SendShort(nint handle, uint message)
        {
            ShortEntered.Set();
            ShortGate.Wait();
            Calls.Enqueue($"Short:{message:X8}");
            lock (_sync)
            {
                ShortMessages.Add(message);
            }

            ShortSent.Set();
            return WinMmResult.Success;
        }

        public uint Prepare(nint handle, nint header, uint size)
        {
            PrepareCallCount++;
            PrepareCalled.Set();
            Calls.Enqueue("Prepare");
            uint result = DequeueOrSuccess(PrepareResults);
            if (result == WinMmResult.Success)
            {
                MidiHeader nativeHeader = Marshal.PtrToStructure<MidiHeader>(header);
                nativeHeader.Flags |= MidiHeaderFlags.Prepared;
                Marshal.StructureToPtr(nativeHeader, header, false);
                lock (_sync)
                {
                    _headerData[header] = nativeHeader.Data;
                }
            }
            else if (PrepareFailureFlags != 0)
            {
                MidiHeader nativeHeader = Marshal.PtrToStructure<MidiHeader>(header);
                nativeHeader.Flags |= PrepareFailureFlags;
                Marshal.StructureToPtr(nativeHeader, header, false);
                lock (_sync)
                {
                    _pendingHeader = header;
                    _pendingData = nativeHeader.Data;
                }
            }

            return result;
        }

        public uint SendLong(nint handle, nint header, uint size)
        {
            SendLongCallCount++;
            MidiHeader nativeHeader = Marshal.PtrToStructure<MidiHeader>(header);
            byte[] message = new byte[checked((int)nativeHeader.BufferLength)];
            Marshal.Copy(nativeHeader.Data, message, 0, message.Length);
            Calls.Enqueue($"Long:{Convert.ToHexString(message)}");

            uint result = DequeueOrSuccess(LongResults);
            if (result != WinMmResult.Success)
            {
                return result;
            }

            bool isSecondLongMessage;
            lock (_sync)
            {
                LongMessages.Add(message);
                _pendingHeader = header;
                _pendingData = nativeHeader.Data;
                _longMessageCount++;
                isSecondLongMessage = _longMessageCount > 1;
            }

            LongSent.Set();
            if (isSecondLongMessage)
            {
                SecondLongSent.Set();
            }

            if (CompleteLongSynchronously)
            {
                CompletePendingLong();
            }

            return WinMmResult.Success;
        }

        public uint Unprepare(nint handle, nint header, uint size)
        {
            UnprepareCallCount++;
            Calls.Enqueue("Unprepare");
            LastUnpreparedHeader = header;
            uint result = DequeueOrSuccess(UnprepareResults);
            if (result == WinMmResult.StillPlaying)
            {
                UnprepareCalled.Set();
                AllowUnprepareRetry.Wait();
                return result;
            }

            Unprepared.Set();
            lock (_sync)
            {
                _headerData.Remove(header);
            }

            lock (_sync)
            {
                if (_pendingHeader == header)
                {
                    _pendingHeader = nint.Zero;
                    _pendingData = nint.Zero;
                }
            }

            UnprepareCalled.Set();

            return result;
        }

        public uint Reset(nint handle)
        {
            ResetCalled.Set();
            Calls.Enqueue("Reset");
            return WinMmResult.Success;
        }

        public uint Close(nint handle)
        {
            CloseCallCount++;
            Closed.Set();
            Calls.Enqueue("Close");
            return WinMmResult.Success;
        }

        public uint GetNumDevs()
        {
            return 0;
        }

        public uint GetDevCaps(nuint deviceId, out MidiOutCaps caps, uint size)
        {
            caps = default;
            return WinMmResult.Success;
        }

        public byte[] ReadPendingData()
        {
            lock (_sync)
            {
                MidiHeader header = Marshal.PtrToStructure<MidiHeader>(_pendingHeader);
                var data = new byte[checked((int)header.BufferLength)];
                Marshal.Copy(_pendingData, data, 0, data.Length);
                return data;
            }
        }

        public void CompletePendingLong()
        {
            lock (_sync)
            {
                if (_pendingHeader == nint.Zero)
                {
                    return;
                }

                MidiHeader header = Marshal.PtrToStructure<MidiHeader>(_pendingHeader);
                header.Flags &= ~MidiHeaderFlags.InQueue;
                header.Flags |= MidiHeaderFlags.Done;
                Marshal.StructureToPtr(header, _pendingHeader, false);
            }
        }

        public void ReleaseAllGates()
        {
            OpenGate.Set();
            ShortGate.Set();
            AllowUnprepareRetry.Set();
            CompleteLongSynchronously = true;
            CompletePendingLong();
        }

        public void Dispose()
        {
            ReleaseAllGates();
            OpenGate.Dispose();
            ShortGate.Dispose();
            AllowUnprepareRetry.Dispose();
            ShortEntered.Dispose();
            ShortSent.Dispose();
            LongSent.Dispose();
            SecondLongSent.Dispose();
            PrepareCalled.Dispose();
            ResetCalled.Dispose();
            UnprepareCalled.Dispose();
            Unprepared.Dispose();
            Closed.Dispose();
        }

        private static uint DequeueOrSuccess(ConcurrentQueue<uint> results)
        {
            return results.TryDequeue(out uint result) ? result : WinMmResult.Success;
        }
    }
}
