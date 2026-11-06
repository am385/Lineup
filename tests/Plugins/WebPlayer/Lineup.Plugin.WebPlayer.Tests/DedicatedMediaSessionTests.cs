using Lineup.Plugin.WebPlayer.VirtualOutputHost;
using Xunit;

namespace Lineup.Plugin.WebPlayer.Tests;

/// <summary>Verifies dedicated Web Player session ownership and inactivity cleanup.</summary>
public sealed class DedicatedMediaSessionTests
{
    /// <summary>Verifies wall-clock corrections cannot expire active media or indefinitely retain abandoned sessions.</summary>
    [Theory]
    [InlineData(3600)]
    [InlineData(-3600)]
    public async Task ExpireInactiveAsync_WallClockChanges_UsesElapsedLifetime(int clockCorrectionSeconds)
    {
        // Arrange
        var clock = new MutableTimeProvider();
        var registry = new DedicatedMediaSessionRegistry<TestSession>(clock, TimeSpan.FromSeconds(20));
        var session = new TestSession();
        registry.TryAdd("owned", session);
        clock.Advance(TimeSpan.FromSeconds(19));
        clock.CorrectUtc(TimeSpan.FromSeconds(clockCorrectionSeconds));

        // Act
        await registry.ExpireInactiveAsync();
        var stillAvailable = registry.Count;
        clock.Advance(TimeSpan.FromSeconds(1));
        await registry.ExpireInactiveAsync();

        // Assert
        Assert.Equal(1, stillAvailable);
        Assert.Empty(registry.GetSessionIds());
        Assert.True(session.IsDisposed);
    }

    /// <summary>Concurrent passive snapshots cannot extend session leases.</summary>
    [Fact]
    public async Task GetSessionIds_DoesNotTouchLifetime()
    {
        // Arrange
        var clock = new MutableTimeProvider();
        var registry = new DedicatedMediaSessionRegistry<TestSession>(clock, TimeSpan.FromSeconds(20));
        var session = new TestSession();
        registry.TryAdd("owned", session);
        clock.Advance(TimeSpan.FromSeconds(19));

        // Act
        var snapshots = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(registry.GetSessionIds)));
        clock.Advance(TimeSpan.FromSeconds(1));
        await registry.ExpireInactiveAsync();

        // Assert
        Assert.All(snapshots, snapshot => Assert.Equal(["owned"], snapshot));
        Assert.Empty(registry.GetSessionIds());
        Assert.True(session.IsDisposed);
    }

    /// <summary>Verifies media access refreshes inactivity and an abandoned session is eventually disposed.</summary>
    [Fact]
    public async Task ExpireInactiveAsync_AbandonedSession_DisposesAfterLastAccessTimeout()
    {
        // Arrange
        var timeProvider = new MutableTimeProvider();
        var registry = new DedicatedMediaSessionRegistry<TestSession>(timeProvider, TimeSpan.FromMinutes(2));
        var session = new TestSession();
        Assert.True(registry.TryAdd("session", session));
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        Assert.True(registry.TryGetAndTouch("session", out var touched));
        Assert.Same(session, touched);

        // Act
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await registry.ExpireInactiveAsync();
        var retainedAfterOriginalDeadline = registry.Count;
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await registry.ExpireInactiveAsync();

        // Assert
        Assert.Equal(1, retainedAfterOriginalDeadline);
        Assert.Equal(0, registry.Count);
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

    private sealed class TestSession : IAsyncDisposable
    {
        internal bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
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
