using Lineup.Core.Common.Streaming;
using Lineup.Core.Streaming;
using Xunit;

namespace Lineup.Core.VirtualOutputs.Runtime.Tests;

/// <summary>Verifies atomic active-stream presentation updates.</summary>
public sealed class ActiveStreamRegistryTests
{
    /// <summary>Verifies pending reservations atomically occupy active-stream capacity and cannot be bypassed by direct registration.</summary>
    [Fact]
    public void TryBeginStartup_PendingReservation_PreventsOversubscription()
    {
        // Arrange
        var registry = new ActiveStreamRegistry();
        using var startup = registry.TryBeginStartup(1, () => { });
        var stream = new ActiveStreamSnapshot("one", "1", HostedStreamFormat.Cmaf, DateTime.UtcNow, null, []);

        // Act
        var rejected = registry.TryBeginStartup(1, () => { });
        var bypass = registry.TryRegister(stream with { SessionId = "bypass" }, 1, () => { });
        var accepted = startup!.TryRegister(stream, () => { });
        startup.Dispose();

        // Assert
        Assert.Null(rejected);
        Assert.False(bypass);
        Assert.True(accepted);
        Assert.Equal("one", Assert.Single(registry.GetActiveStreams()).SessionId);
        Assert.Null(registry.TryBeginStartup(1, () => { }));
    }

    /// <summary>Verifies a registered but not-yet-ready producer still consumes a startup slot and completion releases only that slot.</summary>
    [Fact]
    public void TryBeginStartup_StartupLimit_IsIndependentOfActiveViewerLimit()
    {
        // Arrange
        var registry = new ActiveStreamRegistry(resources: new() { MaximumPendingStarts = 1 });
        using var startup = registry.TryBeginStartup(0, () => { });
        var stream = new ActiveStreamSnapshot("one", "1", HostedStreamFormat.Cmaf, DateTime.UtcNow, null, []);
        Assert.True(startup!.TryRegister(stream, () => { }));

        // Act
        var rejected = registry.TryBeginStartup(0, () => { });
        registry.CompleteStartup("one");
        using var next = registry.TryBeginStartup(0, () => { });

        // Assert
        Assert.Null(rejected);
        Assert.NotNull(next);
        Assert.Single(registry.GetActiveStreams());
    }

    /// <summary>Verifies failed or canceled startup cleanup is idempotent and restores both reservations.</summary>
    [Fact]
    public void Dispose_PendingStartup_ReleasesReservation()
    {
        // Arrange
        var registry = new ActiveStreamRegistry(resources: new() { MaximumPendingStarts = 1 });
        var startup = registry.TryBeginStartup(1, () => { });

        // Act
        startup!.Dispose();
        startup.Dispose();
        using var next = registry.TryBeginStartup(1, () => { });

        // Assert
        Assert.NotNull(next);
        Assert.False(startup.TryRegister(new("stale", "1", HostedStreamFormat.Cmaf, DateTime.UtcNow, null, []), () => { }));
        Assert.Empty(registry.GetActiveStreams());
    }

    /// <summary>Verifies simultaneous startups cannot race past the agreed host budget.</summary>
    [Fact]
    public void TryBeginStartup_ConcurrentCalls_EnforcesExactLimit()
    {
        // Arrange
        var registry = new ActiveStreamRegistry(resources: new() { MaximumPendingStarts = 8 });
        var leases = new System.Collections.Concurrent.ConcurrentBag<IStreamStartupLease>();

        // Act
        Parallel.For(0, 64, _ =>
        {
            if (registry.TryBeginStartup(0, () => { }) is { } lease)
            {
                leases.Add(lease);
            }
        });

        // Assert
        Assert.Equal(8, leases.Count);
        foreach (var lease in leases)
        {
            lease.Dispose();
        }
        using var recovered = registry.TryBeginStartup(0, () => { });
        Assert.NotNull(recovered);
    }

    /// <summary>Verifies shutdown requests cancellation and waits for pending startup ownership to be released.</summary>
    [Fact]
    public async Task StopAllAsync_PendingStartup_StopsAndDrainsReservation()
    {
        // Arrange
        var registry = new ActiveStreamRegistry();
        IStreamStartupLease? startup = null;
        var stopped = false;
        startup = registry.TryBeginStartup(0, () => { stopped = true; startup!.Dispose(); });

        // Act
        await registry.StopAllAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(stopped);
        Assert.Null(registry.TryBeginStartup(0, () => { }));
    }

    /// <summary>Verifies updates replace presentation metadata while preserving the registered stop callback.</summary>
    [Fact]
    public void TryUpdate_ExistingStream_PreservesStopCallback()
    {
        // Arrange
        var registry = new ActiveStreamRegistry();
        var stopRequested = false;
        registry.Register(
            new ActiveStreamSnapshot("stream", "48.3", HostedStreamFormat.MpegTs, DateTime.UtcNow, null, []),
            () => stopRequested = true);

        // Act
        var updated = registry.TryUpdate("stream", current => current with
        {
            Format = HostedStreamFormat.Cmaf,
            WorkerMediaSessionId = "worker-media"
        });
        var stopped = registry.RequestStop("stream");

        // Assert
        Assert.True(updated);
        Assert.True(stopped);
        Assert.True(stopRequested);
        var snapshot = Assert.Single(registry.GetActiveStreams());
        Assert.Equal(HostedStreamFormat.Cmaf, snapshot.Format);
        Assert.Equal("worker-media", snapshot.WorkerMediaSessionId);
    }

    /// <summary>Verifies stale worker reports cannot recreate an active stream that has already ended.</summary>
    [Fact]
    public void TryUpdate_MissingStream_DoesNotCreateStream()
    {
        // Arrange
        var registry = new ActiveStreamRegistry();

        // Act
        var updated = registry.TryUpdate(
            "missing",
            current => current with { WorkerMediaSessionId = "worker-media" });

        // Assert
        Assert.False(updated);
        Assert.Empty(registry.GetActiveStreams());
    }
}
