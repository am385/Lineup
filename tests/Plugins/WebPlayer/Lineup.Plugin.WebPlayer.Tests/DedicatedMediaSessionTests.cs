using Lineup.Core.Common.Streaming;
using Lineup.Plugin.WebPlayer.VirtualOutputHost;
using Xunit;

namespace Lineup.Plugin.WebPlayer.Tests;

/// <summary>Verifies dedicated Web Player session ownership and inactivity cleanup.</summary>
public sealed class DedicatedMediaSessionTests
{
    /// <summary>Only classified source completion is retryable; explicit stops and unknown aliases remain terminal.</summary>
    [Theory]
    [InlineData("source_eof", true)]
    [InlineData("source_failure", true)]
    [InlineData("media_process_failed", false)]
    [InlineData(null, false)]
    public async Task PlaybackStatus_ClassifiesSourceLossWithoutRevivingStoppedViewers(string? code, bool retryable)
    {
        // Arrange
        var clock = new MutableTimeProvider();
        await using var coordinator = new CmafOutputCoordinator(clock);
        var stopped = await coordinator.AcquireAsync(new StreamTransformKey("cmaf", "shared"),
            _ => ValueTask.FromResult<IAsyncDisposable>(new TestSession()), TestContext.Current.CancellationToken);
        var survivor = await coordinator.AcquireAsync(new StreamTransformKey("cmaf", "shared"),
            _ => ValueTask.FromException<IAsyncDisposable>(new InvalidOperationException("Must share.")), TestContext.Current.CancellationToken);
        await coordinator.ReleaseViewerAsync(stopped.ViewerSessionId);

        // Act
        await coordinator.ReleaseOutputAsync(survivor.OutputId, code);
        var status = coordinator.GetPlaybackStatus(survivor.ViewerSessionId);

        // Assert
        Assert.False(status.IsActive);
        Assert.Equal(retryable, status.Retryable);
        Assert.False(coordinator.GetPlaybackStatus(stopped.ViewerSessionId).Retryable);
        Assert.False(coordinator.GetPlaybackStatus(StreamSessionId.New()).Retryable);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(coordinator.GetPlaybackStatus(survivor.ViewerSessionId).Retryable);
    }

    /// <summary>A late explicit stop suppresses an earlier source-loss outcome, and shutdown drops recovery history.</summary>
    [Fact]
    public async Task PlaybackStatus_ExplicitStopAndShutdown_SuppressRecovery()
    {
        // Arrange
        await using var coordinator = new CmafOutputCoordinator();
        var first = await coordinator.AcquireAsync(new StreamTransformKey("cmaf", "one"),
            _ => ValueTask.FromResult<IAsyncDisposable>(new TestSession()), TestContext.Current.CancellationToken);
        var second = await coordinator.AcquireAsync(new StreamTransformKey("cmaf", "two"),
            _ => ValueTask.FromResult<IAsyncDisposable>(new TestSession()), TestContext.Current.CancellationToken);
        await coordinator.ReleaseOutputAsync(first.OutputId, "source_eof");
        await coordinator.ReleaseOutputAsync(second.OutputId, "source_failure");

        // Act
        await coordinator.ReleaseViewerAsync(first.ViewerSessionId);
        var stopped = coordinator.GetPlaybackStatus(first.ViewerSessionId);
        await coordinator.StopAsync();

        // Assert
        Assert.False(stopped.Retryable);
        Assert.False(coordinator.GetPlaybackStatus(second.ViewerSessionId).Retryable);
    }

    /// <summary>Source-loss history never grows beyond the advertised per-host alias bound.</summary>
    [Fact]
    public async Task PlaybackStatus_ManySourceLosses_RetainsAtMost2048Aliases()
    {
        // Arrange
        await using var coordinator = new CmafOutputCoordinator();
        var viewers = new List<ICmafOutputLease>();
        var key = new StreamTransformKey("cmaf", "many-viewers");
        for (var index = 0; index < 2049; index++)
        {
            viewers.Add(await coordinator.AcquireAsync(key, _ => ValueTask.FromResult<IAsyncDisposable>(new TestSession()), TestContext.Current.CancellationToken));
        }

        // Act
        await coordinator.ReleaseOutputAsync(viewers[0].OutputId, "source_eof");
        var retained = viewers.Count(viewer => coordinator.GetPlaybackStatus(viewer.ViewerSessionId).Retryable);

        // Assert
        Assert.Equal(2048, retained);
        Assert.Empty(coordinator.GetViewers());
    }

    /// <summary>Verifies wall-clock corrections cannot expire active media or indefinitely retain abandoned sessions.</summary>
    [Theory]
    [InlineData(3600)]
    [InlineData(-3600)]
    public async Task ViewerExpiration_WallClockChanges_UsesElapsedLifetime(int clockCorrectionSeconds)
    {
        // Arrange
        var clock = new MutableTimeProvider();
        await using var coordinator = new CmafOutputCoordinator(clock, TimeSpan.FromMilliseconds(20));
        var session = new TestSession();
        await coordinator.AcquireAsync(new StreamTransformKey("cmaf", "owned"), _ => ValueTask.FromResult<IAsyncDisposable>(session), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMilliseconds(19));
        clock.CorrectUtc(TimeSpan.FromSeconds(clockCorrectionSeconds));

        // Act
        var stillAvailable = coordinator.GetViewers().Count;
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await session.Disposed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, stillAvailable);
        Assert.Empty(coordinator.GetViewers());
        Assert.True(session.IsDisposed);
    }

    /// <summary>Concurrent passive snapshots cannot extend session leases.</summary>
    [Fact]
    public async Task GetViewers_DoesNotTouchLifetime()
    {
        // Arrange
        var clock = new MutableTimeProvider();
        await using var coordinator = new CmafOutputCoordinator(clock, TimeSpan.FromMilliseconds(20));
        var session = new TestSession();
        var lease = await coordinator.AcquireAsync(new StreamTransformKey("cmaf", "owned"), _ => ValueTask.FromResult<IAsyncDisposable>(session), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMilliseconds(19));

        // Act
        var snapshots = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(coordinator.GetViewers)));
        var passiveStatus = coordinator.GetPlaybackStatus(lease.ViewerSessionId);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await session.Disposed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        Assert.All(snapshots, snapshot => Assert.Equal(lease.ViewerSessionId, Assert.Single(snapshot).ViewerSessionId));
        Assert.True(passiveStatus.IsActive);
        Assert.Empty(coordinator.GetViewers());
        Assert.True(session.IsDisposed);
    }

    /// <summary>Verifies media access refreshes inactivity and an abandoned session is eventually disposed.</summary>
    [Fact]
    public async Task ViewerExpiration_AbandonedSession_DisposesAfterLastAccessTimeout()
    {
        // Arrange
        var timeProvider = new MutableTimeProvider();
        await using var coordinator = new CmafOutputCoordinator(timeProvider, TimeSpan.FromMilliseconds(20));
        var session = new TestSession();
        var lease = await coordinator.AcquireAsync(new StreamTransformKey("cmaf", "session"), _ => ValueTask.FromResult(new CmafOutputRegistration(session, session)), TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromMilliseconds(10));
        Assert.True(coordinator.TryGetViewer(lease.ViewerSessionId, out var touched));
        Assert.Same(session, touched!.State);

        // Act
        timeProvider.Advance(TimeSpan.FromMilliseconds(10));
        var retainedAfterOriginalDeadline = coordinator.GetViewers().Count;
        timeProvider.Advance(TimeSpan.FromMilliseconds(10));
        await session.Disposed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, retainedAfterOriginalDeadline);
        Assert.Empty(coordinator.GetViewers());
        Assert.True(session.IsDisposed);
    }

    /// <summary>Verifies a failed startup releases the opened source and transient directory before ownership transfer.</summary>
    [Fact]
    public async Task DisposeAsync_StartupOwnershipNotTransferred_ReleasesSourceAndDirectory()
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), "lineup-web-player-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "partial.m4s"), "partial", TestContext.Current.CancellationToken);
        var source = new TestSession();
        var guard = new MediaSessionStartupGuard(directory, source);

        // Act
        await guard.DisposeAsync();

        // Assert
        Assert.True(source.IsDisposed);
        Assert.False(Directory.Exists(directory));
    }

    /// <summary>Verifies one producer's failed cleanup cannot prevent independent producers from releasing during shutdown.</summary>
    [Fact]
    public async Task StopAsync_OneProducerFails_DisposesEveryOwnerBeforeReportingFailure()
    {
        // Arrange
        await using var coordinator = new CmafOutputCoordinator();
        var failed = new TestSession(failDisposal: true);
        var independent = new TestSession();
        await coordinator.AcquireAsync(new StreamTransformKey("cmaf", "failed"), _ => ValueTask.FromResult<IAsyncDisposable>(failed), TestContext.Current.CancellationToken);
        await coordinator.AcquireAsync(new StreamTransformKey("cmaf", "independent"), _ => ValueTask.FromResult<IAsyncDisposable>(independent), TestContext.Current.CancellationToken);

        // Act
        var failure = await Assert.ThrowsAsync<AggregateException>(() => coordinator.StopAsync().AsTask());

        // Assert
        Assert.IsType<IOException>(Assert.Single(failure.InnerExceptions));
        Assert.True(failed.IsDisposed);
        Assert.True(independent.IsDisposed);
        Assert.Empty(coordinator.GetDiagnostics());
    }

    private sealed class TestSession(bool failDisposal = false) : IAsyncDisposable
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Gets whether owned cleanup has completed.</summary>
        internal bool IsDisposed { get; private set; }

        /// <summary>Gets completion of owned cleanup.</summary>
        internal Task Disposed => _disposed.Task;

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            _disposed.TrySetResult();
            return failDisposal ? ValueTask.FromException(new IOException("Synthetic cleanup failure.")) : ValueTask.CompletedTask;
        }
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long _timestamp;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        /// <summary>Advances wall time and monotonic elapsed time together.</summary>
        internal void Advance(TimeSpan duration)
        {
            _utcNow += duration;
            _timestamp += duration.Ticks;
        }

        /// <summary>Corrects wall time without changing elapsed session lifetime.</summary>
        internal void CorrectUtc(TimeSpan duration) => _utcNow += duration;
    }
}
