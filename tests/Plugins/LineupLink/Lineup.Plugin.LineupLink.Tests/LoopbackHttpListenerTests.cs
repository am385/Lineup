using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Lineup.Plugin.LineupLink.Tests;

/// <summary>Verifies loopback fixtures recover from real prefix conflicts without hiding terminal setup errors.</summary>
public sealed class LoopbackHttpListenerTests
{
    /// <summary>A registered HTTP prefix cannot be claimed, so startup retries and returns a different listening endpoint.</summary>
    [Fact]
    public void Start_OccupiedPrefix_RetriesWithAnotherPort()
    {
        // Arrange
        using var occupied = LoopbackHttpListener.Start(out var occupiedPort);
        var attempts = 0;

        // Act
        using var listener = LoopbackHttpListener.Start(out var port, () =>
        {
            if (attempts++ == 0)
            {
                return occupiedPort;
            }
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        });

        // Assert
        Assert.True(listener.IsListening);
        Assert.True(occupied.IsListening);
        Assert.NotEqual(occupiedPort, port);
        Assert.InRange(attempts, 2, 10);
    }

    /// <summary>Persistent prefix conflicts stop after the bounded attempt count and leave the existing owner untouched.</summary>
    [Fact]
    public void Start_PersistentConflict_StopsAfterTenAttempts()
    {
        // Arrange
        using var occupied = LoopbackHttpListener.Start(out var port);
        var attempts = 0;

        // Act
        Assert.Throws<HttpListenerException>(() => LoopbackHttpListener.Start(out _, () =>
        {
            attempts++;
            return port;
        }));

        // Assert
        Assert.Equal(10, attempts);
        Assert.True(occupied.IsListening);
    }

    /// <summary>Access-denied errors are propagated unchanged rather than retried as address conflicts.</summary>
    [Fact]
    public void Start_AccessDenied_PropagatesWithoutRetry()
    {
        // Arrange
        var failure = new HttpListenerException(5, "Access denied.");
        var attempts = 0;

        // Act
        var exception = Assert.Throws<HttpListenerException>(() => LoopbackHttpListener.Start(out _, () =>
        {
            attempts++;
            throw failure;
        }));

        // Assert
        Assert.Same(failure, exception);
        Assert.Equal(1, attempts);
    }
}
