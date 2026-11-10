using System.Diagnostics;
using System.Threading.Channels;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Streaming;
using Lineup.Core.Streaming.FFmpeg;
using Lineup.Core.VirtualOutputs.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Core.VirtualOutputs.Runtime.Tests;

/// <summary>Verifies deterministic and lease-backed cleanup of brokered worker streams.</summary>
public sealed class VirtualOutputBrokerStreamRegistryTests
{
    /// <summary>Verifies a correlated close request immediately unregisters and disposes its broker stream.</summary>
    [Fact]
    public async Task CloseAsync_CorrelatedRequest_ReleasesActiveStream()
    {
        // Arrange
        using var authority = new VirtualOutputSessionAuthority();
        var context = Register(authority, out var deviceId);
        var activeStreams = new ActiveStreamRegistry();
        var stream = new TrackingStream();
        var requestId = VirtualOutputStreamRequestId.New();
        var streamSessionId = StreamSessionId.New();
        activeStreams.Register(new ActiveStreamSnapshot(streamSessionId.ToString(), "48.3", HostedStreamFormat.MpegTs, DateTime.UtcNow, null, []));
        var brokered = CreateBrokeredStream(activeStreams, stream, streamSessionId);
        var registry = new VirtualOutputBrokerStreamRegistry(authority, NullLogger.Instance);
        var openRequest = CreateOpenRequest(context, requestId);
        Assert.True(registry.TryAdd(deviceId, openRequest, brokered));

        // Act
        var closed = await registry.CloseAsync(new VirtualOutputCloseStreamRequest
        {
            Session = context,
            RequestId = requestId,
            StreamSessionId = streamSessionId
        });

        // Assert
        Assert.True(closed);
        Assert.True(stream.IsDisposed);
        Assert.Empty(activeStreams.GetActiveStreams());
        Assert.Equal(0, registry.Count(deviceId));
    }

    /// <summary>Verifies lease revocation removes a broker stream even when its worker never sends a close request.</summary>
    [Fact]
    public async Task ExpireUnauthorizedAsync_RevokedWorker_ReleasesOrphanedStream()
    {
        // Arrange
        using var authority = new VirtualOutputSessionAuthority();
        var context = Register(authority, out var deviceId);
        var activeStreams = new ActiveStreamRegistry();
        var stream = new TrackingStream();
        var requestId = VirtualOutputStreamRequestId.New();
        var streamSessionId = StreamSessionId.New();
        activeStreams.Register(new ActiveStreamSnapshot(streamSessionId.ToString(), "48.3", HostedStreamFormat.MpegTs, DateTime.UtcNow, null, []));
        var registry = new VirtualOutputBrokerStreamRegistry(authority, NullLogger.Instance);
        Assert.True(registry.TryAdd(deviceId, CreateOpenRequest(context, requestId), CreateBrokeredStream(activeStreams, stream, streamSessionId)));
        Assert.True(authority.Revoke(deviceId, "Test worker disconnected."));

        // Act
        await registry.ExpireUnauthorizedAsync();

        // Assert
        Assert.True(stream.IsDisposed);
        Assert.Empty(activeStreams.GetActiveStreams());
        Assert.Equal(0, registry.Count(deviceId));
    }

    /// <summary>Verifies a failed orphan disposal is logged without preventing independent entries from being cleaned.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpireUnauthorizedAsync_DisposalFailures_AttemptsEveryOrphan(bool canceledDisposal)
    {
        // Arrange
        using var authority = new VirtualOutputSessionAuthority();
        var context = Register(authority, out var deviceId);
        var activeStreams = new ActiveStreamRegistry();
        var logger = Substitute.For<ILogger>();
        var registry = new VirtualOutputBrokerStreamRegistry(authority, logger);
        Exception firstFailure = canceledDisposal ? new OperationCanceledException("Stream cleanup canceled.") : new IOException("Stream cleanup failed.");
        var secondFailure = new InvalidOperationException("Another stream cleanup failed.");
        var streams = new[] { new TrackingStream(firstFailure), new TrackingStream(secondFailure), new TrackingStream() };
        foreach (var stream in streams)
        {
            AddStream(registry, activeStreams, context, deviceId, stream);
        }
        Assert.True(authority.Revoke(deviceId, "Test worker disconnected."));

        // Act
        var failure = await Record.ExceptionAsync(registry.ExpireUnauthorizedAsync);

        // Assert
        Assert.Null(failure);
        Assert.All(streams, stream => Assert.Equal(1, stream.DisposeCount));
        Assert.Empty(activeStreams.GetActiveStreams());
        Assert.Equal(0, registry.Count(deviceId));
        var errors = logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log) && Equals(call.GetArguments()[0], LogLevel.Error)).ToArray();
        Assert.Equal(2, errors.Length);
        Assert.Contains(errors, call => ReferenceEquals(call.GetArguments()[3], firstFailure));
        Assert.Contains(errors, call => ReferenceEquals(call.GetArguments()[3], secondFailure));
    }

    /// <summary>Verifies final shutdown attempts every entry and reports all failed disposals instead of succeeding silently.</summary>
    [Fact]
    public async Task DisposeAllAsync_DisposalFailures_AttemptsEveryEntryAndReportsFailures()
    {
        // Arrange
        using var authority = new VirtualOutputSessionAuthority();
        var context = Register(authority, out var deviceId);
        var activeStreams = new ActiveStreamRegistry();
        var logger = Substitute.For<ILogger>();
        var registry = new VirtualOutputBrokerStreamRegistry(authority, logger);
        var firstFailure = new IOException("First cleanup failed.");
        var secondFailure = new InvalidOperationException("Second cleanup failed.");
        var streams = new[] { new TrackingStream(firstFailure), new TrackingStream(secondFailure), new TrackingStream() };
        foreach (var stream in streams)
        {
            AddStream(registry, activeStreams, context, deviceId, stream);
        }

        // Act
        var failure = await Record.ExceptionAsync(registry.DisposeAllAsync);

        // Assert
        var aggregate = Assert.IsType<AggregateException>(failure);
        Assert.Equal(2, aggregate.InnerExceptions.Count);
        Assert.Contains(firstFailure, aggregate.InnerExceptions);
        Assert.Contains(secondFailure, aggregate.InnerExceptions);
        Assert.All(streams, stream => Assert.Equal(1, stream.DisposeCount));
        Assert.Empty(activeStreams.GetActiveStreams());
        Assert.Equal(0, registry.Count(deviceId));
        await registry.DisposeAllAsync();
        Assert.All(streams, stream => Assert.Equal(1, stream.DisposeCount));
        Assert.Equal(2, logger.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(ILogger.Log) && Equals(call.GetArguments()[0], LogLevel.Error)));
    }

    /// <summary>Verifies an entry failure does not terminate the periodic task or prevent a later orphan from expiring.</summary>
    [Fact]
    public async Task RunExpirationLoopAsync_DisposalFailure_ProcessesLaterOrphans()
    {
        // Arrange
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(10));
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(watchdog.Token);
        using var authority = new VirtualOutputSessionAuthority();
        var context = Register(authority, out var deviceId);
        var activeStreams = new ActiveStreamRegistry();
        var time = new SweepTimeProvider();
        var registry = new VirtualOutputBrokerStreamRegistry(authority, NullLogger.Instance, time);
        var failed = new TrackingStream(new IOException("Orphan cleanup failed."));
        var healthy = new TrackingStream();
        AddStream(registry, activeStreams, context, deviceId, failed);
        AddStream(registry, activeStreams, context, deviceId, healthy);
        var liveContext = Register(authority, out var liveDeviceId);
        var live = new TrackingStream();
        AddStream(registry, activeStreams, liveContext, liveDeviceId, live);
        Assert.True(authority.Revoke(deviceId, "Test worker disconnected."));
        var loop = registry.RunExpirationLoopAsync(stopping.Token);
        try
        {
            // Act
            await time.TickAsync(watchdog.Token);
            await failed.DisposalStarted.Task.WaitAsync(watchdog.Token);
            var later = new TrackingStream();
            AddStream(registry, activeStreams, context, deviceId, later);
            await time.TickAsync(watchdog.Token);
            await later.DisposalStarted.Task.WaitAsync(watchdog.Token);
            await stopping.CancelAsync();
            await loop.WaitAsync(watchdog.Token);

            // Assert
            Assert.Equal(1, failed.DisposeCount);
            Assert.Equal(1, healthy.DisposeCount);
            Assert.Equal(1, later.DisposeCount);
            Assert.Equal(0, live.DisposeCount);
            Assert.Single(activeStreams.GetActiveStreams());
            Assert.Equal(0, registry.Count(deviceId));
            Assert.Equal(1, registry.Count(liveDeviceId));
        }
        finally
        {
            await stopping.CancelAsync();
            await loop.WaitAsync(watchdog.Token);
            await registry.DisposeAllAsync();
        }
    }

    /// <summary>Verifies explicit close, stream completion, expiration and shutdown cannot claim the same disposal twice.</summary>
    [Theory]
    [InlineData("close")]
    [InlineData("complete")]
    [InlineData("expire")]
    [InlineData("shutdown")]
    public async Task Cleanup_ConcurrentOwners_DisposesStreamOnce(string firstOwner)
    {
        // Arrange
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(10));
        using var authority = new VirtualOutputSessionAuthority();
        var context = Register(authority, out var deviceId);
        var activeStreams = new ActiveStreamRegistry();
        var registry = new VirtualOutputBrokerStreamRegistry(authority, NullLogger.Instance);
        var releaseDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new TrackingStream(releaseDisposal: releaseDisposal.Task);
        var entry = AddStream(registry, activeStreams, context, deviceId, stream);
        var close = new VirtualOutputCloseStreamRequest
        {
            Session = context,
            RequestId = entry.Request.RequestId,
            StreamSessionId = entry.Stream.Response.StreamSessionId!.Value
        };
        Assert.True(authority.Revoke(deviceId, "Test worker disconnected."));
        Task first = firstOwner switch
        {
            "close" => registry.CloseAsync(close),
            "complete" => registry.CompleteAsync(entry.Request.RequestId, entry.Stream),
            "expire" => registry.ExpireUnauthorizedAsync(),
            "shutdown" => registry.DisposeAllAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(firstOwner))
        };
        try
        {
            await stream.DisposalStarted.Task.WaitAsync(watchdog.Token);

            // Act
            await Task.WhenAll(registry.CloseAsync(close), registry.CompleteAsync(entry.Request.RequestId, entry.Stream),
                registry.ExpireUnauthorizedAsync(), registry.DisposeAllAsync()).WaitAsync(watchdog.Token);
            releaseDisposal.TrySetResult();
            await first.WaitAsync(watchdog.Token);

            // Assert
            Assert.Equal(1, stream.DisposeCount);
            Assert.Empty(activeStreams.GetActiveStreams());
            Assert.Equal(0, registry.Count(deviceId));
            Assert.False(await registry.CloseAsync(close));
        }
        finally
        {
            releaseDisposal.TrySetResult();
            await first.WaitAsync(watchdog.Token);
        }
    }

    /// <summary>Verifies a real slate stream's process-state disposal failure cannot disable independent broker cleanup.</summary>
    [Fact]
    public async Task ExpireUnauthorizedAsync_SlateProcessDisposalFails_ContinuesCleanup()
    {
        // Arrange
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(10));
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Name;
        var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Lineup.TestSupport.FakeFFmpeg", configuration, "Lineup.TestSupport.FakeFFmpeg.dll"));
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { executable, "diagnostics", "0", "0", "hold" })
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start the owned slate process.");
        var detached = false;
        try
        {
            var processFactory = Substitute.For<IMediaProcessFactory>();
            processFactory.Start(Arg.Any<MediaProcessRequest>()).Returns(process);
            var slate = new ChannelSlateStreamFactory(processFactory).OpenMpegTs("48.3");
            using var authority = new VirtualOutputSessionAuthority();
            var context = Register(authority, out var deviceId);
            var activeStreams = new ActiveStreamRegistry();
            var logger = Substitute.For<ILogger>();
            var registry = new VirtualOutputBrokerStreamRegistry(authority, logger);
            AddStream(registry, activeStreams, context, deviceId, slate);
            var healthy = new TrackingStream();
            AddStream(registry, activeStreams, context, deviceId, healthy);
            process.StandardInput.Close();
            await process.WaitForExitAsync(watchdog.Token);
            process.Dispose();
            detached = true;
            Assert.True(authority.Revoke(deviceId, "Test worker disconnected."));

            // Act
            var failure = await Record.ExceptionAsync(registry.ExpireUnauthorizedAsync);

            // Assert
            Assert.Null(failure);
            Assert.Equal(1, healthy.DisposeCount);
            Assert.Empty(activeStreams.GetActiveStreams());
            Assert.Equal(0, registry.Count(deviceId));
            var error = Assert.Single(logger.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(ILogger.Log) && Equals(call.GetArguments()[0], LogLevel.Error));
            Assert.IsAssignableFrom<InvalidOperationException>(error.GetArguments()[3]);
            Assert.Contains("cleanup is incomplete", error.GetArguments()[2]?.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (!detached && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            }
        }
    }

    /// <summary>Verifies failed cleanup of expired-generation streams cannot remove a replacement's ownership or prevent later cleanup.</summary>
    [Fact]
    public async Task ExpireUnauthorizedAsync_ExpiredGenerationAndFailedCleanup_PreservesReplacement()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var clock = Substitute.For<TimeProvider>();
        clock.GetUtcNow().Returns(_ => now);
        using var authority = new VirtualOutputSessionAuthority(clock);
        var original = Register(authority, out var deviceId);
        var activeStreams = new ActiveStreamRegistry();
        var logger = Substitute.For<ILogger>();
        var registry = new VirtualOutputBrokerStreamRegistry(authority, logger);
        var failed = new TrackingStream(new IOException("Owned retired stream cleanup failed."));
        var retired = new TrackingStream();
        AddStream(registry, activeStreams, original, deviceId, failed);
        var oldEntry = AddStream(registry, activeStreams, original, deviceId, retired);
        now = now.AddMinutes(2);
        Assert.False(authority.Authorize(original, VirtualOutputCapability.OpenStream).Authorized);
        var replacement = Register(authority, out _, deviceId);
        var current = new TrackingStream();
        AddStream(registry, activeStreams, replacement, deviceId, current);

        // Act
        await registry.ExpireUnauthorizedAsync();
        var staleClose = await registry.CloseAsync(new VirtualOutputCloseStreamRequest
        {
            Session = original,
            RequestId = oldEntry.Request.RequestId,
            StreamSessionId = oldEntry.Stream.Response.StreamSessionId!.Value
        });

        // Assert
        Assert.False(staleClose);
        Assert.Equal(1, failed.DisposeCount);
        Assert.Equal(1, retired.DisposeCount);
        Assert.Equal(0, current.DisposeCount);
        Assert.Equal(1, registry.Count(deviceId));
        Assert.Single(activeStreams.GetActiveStreams());
        Assert.True(authority.Authorize(replacement, VirtualOutputCapability.OpenStream).Authorized);
        Assert.Single(logger.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(ILogger.Log) && Equals(call.GetArguments()[0], LogLevel.Error));

        // Act
        Assert.True(authority.Revoke(deviceId, "Owned replacement shutdown."));
        await registry.ExpireUnauthorizedAsync();
        await registry.DisposeAllAsync();

        // Assert
        Assert.Equal(1, current.DisposeCount);
        Assert.Empty(activeStreams.GetActiveStreams());
        Assert.Equal(0, registry.Count(deviceId));
    }

    private static (VirtualOutputOpenStreamRequest Request, VirtualOutputBrokeredStream Stream) AddStream(
        VirtualOutputBrokerStreamRegistry registry,
        IActiveStreamRegistry activeStreams,
        VirtualOutputSessionContext context,
        VirtualDeviceId deviceId,
        Stream stream)
    {
        var request = CreateOpenRequest(context, VirtualOutputStreamRequestId.New());
        var sessionId = StreamSessionId.New();
        activeStreams.Register(new ActiveStreamSnapshot(sessionId.ToString(), "48.3", HostedStreamFormat.MpegTs, DateTime.UtcNow, null, []));
        var brokered = CreateBrokeredStream(activeStreams, stream, sessionId);
        Assert.True(registry.TryAdd(deviceId, request, brokered));
        return (request, brokered);
    }

    private static VirtualOutputSessionContext Register(VirtualOutputSessionAuthority authority, out VirtualDeviceId deviceId, VirtualDeviceId? assignedDeviceId = null)
    {
        deviceId = assignedDeviceId ?? VirtualDeviceId.New();
        var ticket = authority.CreateActivation(
            deviceId,
            "test-worker",
            new HashSet<VirtualOutputCapability> { VirtualOutputCapability.OpenStream },
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
        var registration = authority.Register(new VirtualOutputWorkerRegistrationRequest
        {
            WorkerId = ticket.WorkerId,
            VirtualDeviceId = ticket.VirtualDeviceId,
            Generation = ticket.Generation,
            BootstrapToken = ticket.BootstrapToken,
            MinimumProtocolVersion = VirtualOutputProtocol.MinimumVersion,
            MaximumProtocolVersion = VirtualOutputProtocol.MaximumVersion
        });
        return new VirtualOutputSessionContext
        {
            SessionId = registration.SessionId!.Value,
            Generation = ticket.Generation
        };
    }

    private static VirtualOutputOpenStreamRequest CreateOpenRequest(VirtualOutputSessionContext context, VirtualOutputStreamRequestId requestId) =>
        new()
        {
            Session = context,
            RequestId = requestId,
            ChannelKey = "48.3",
            Stage = VirtualOutputStreamStage.PhysicalOutput
        };

    private static VirtualOutputBrokeredStream CreateBrokeredStream(IActiveStreamRegistry activeStreams, Stream stream, StreamSessionId streamSessionId)
    {
        var source = new MediaProbeResult(
            [new MediaTrackMetadata(0, MediaTrackType.Video, "h264", 4_000_000, 1280, 720, null, null)],
            4_000_000);
        return new VirtualOutputBrokeredStream(
            activeStreams,
            new VirtualMpegTsOutput(stream, source, source, new VirtualTranscodeProfile(), new ActiveStreamFlowContext()),
            new VirtualOutputOpenStreamResponse
            {
                Accepted = true,
                ContentType = "video/mp2t",
                StreamSessionId = streamSessionId,
                Source = source,
                Profile = new VirtualTranscodeProfile()
            });
    }

    private sealed class SweepTimeProvider : TimeProvider
    {
        private readonly Channel<(TimerCallback Callback, object? State)> _timers = Channel.CreateUnbounded<(TimerCallback, object?)>();

        /// <inheritdoc />
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromSeconds(5), dueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            Assert.True(_timers.Writer.TryWrite((callback, state)));
            return Substitute.For<ITimer>();
        }

        /// <summary>Fires the next scheduled orphan sweep without advancing wall-clock time.</summary>
        public async Task TickAsync(CancellationToken cancellationToken)
        {
            var timer = await _timers.Reader.ReadAsync(cancellationToken);
            timer.Callback(timer.State);
        }
    }

    private sealed class TrackingStream(Exception? failure = null, Task? releaseDisposal = null) : MemoryStream
    {
        private int _disposeCount;

        /// <summary>Gets whether disposal has been attempted.</summary>
        internal bool IsDisposed { get; private set; }

        /// <summary>Gets the number of disposal attempts.</summary>
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        /// <summary>Gets the signal that a cleanup owner has entered disposal.</summary>
        internal TaskCompletionSource DisposalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            IsDisposed = true;
            DisposalStarted.TrySetResult();
            if (releaseDisposal is not null)
            {
                await releaseDisposal;
            }
            if (failure is not null)
            {
                throw failure;
            }
            await base.DisposeAsync();
        }
    }
}
