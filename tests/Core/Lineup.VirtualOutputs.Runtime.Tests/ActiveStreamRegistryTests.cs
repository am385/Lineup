using Lineup.Common.Streaming;
using Lineup.Core.Streaming;
using Xunit;

namespace Lineup.VirtualOutputs.Runtime.Tests;

/// <summary>Verifies atomic active-stream presentation updates.</summary>
public sealed class ActiveStreamRegistryTests
{
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
