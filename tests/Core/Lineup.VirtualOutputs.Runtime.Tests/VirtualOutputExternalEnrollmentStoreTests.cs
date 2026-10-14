using Lineup.Common.Streaming;
using Lineup.VirtualOutputs.Protocol;
using System.Text.Json;
using Xunit;

namespace Lineup.VirtualOutputs.Runtime.Tests;

/// <summary>Verifies external-worker enrollment retrieval and credential recovery.</summary>
public sealed class VirtualOutputExternalEnrollmentStoreTests
{
    /// <summary>Verifies a retrieved enrollment is replaced with a fresh activation on the next request.</summary>
    [Fact]
    public void Take_RetrievedEnrollment_RenewsCredentials()
    {
        // Arrange
        var store = new VirtualOutputExternalEnrollmentStore();
        var deviceId = VirtualDeviceId.New();
        var initial = CreateEnrollment(deviceId, 1, DateTimeOffset.UtcNow.AddMinutes(1));
        var renewed = CreateEnrollment(deviceId, 2, DateTimeOffset.UtcNow.AddMinutes(1));
        var renewalCount = 0;
        store.Set(initial, () =>
        {
            renewalCount++;
            return renewed;
        });

        // Act
        var first = store.Take(deviceId);
        var second = store.Take(deviceId);

        // Assert
        Assert.Same(initial, first);
        Assert.Same(renewed, second);
        Assert.Equal(1, renewalCount);
    }

    /// <summary>Verifies an expired pending enrollment is replaced before it is returned.</summary>
    [Fact]
    public void Take_ExpiredEnrollment_RenewsCredentials()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var store = new VirtualOutputExternalEnrollmentStore(new FixedTimeProvider(now));
        var deviceId = VirtualDeviceId.New();
        var expired = CreateEnrollment(deviceId, 1, now.AddSeconds(-1));
        var renewed = CreateEnrollment(deviceId, 2, now.AddMinutes(1));
        store.Set(expired, () => renewed);

        // Act
        var result = store.Take(deviceId);

        // Assert
        Assert.Same(renewed, result);
    }

    /// <summary>Verifies an active external worker can suppress replacement enrollment.</summary>
    [Fact]
    public void Take_ActiveWorker_ReturnsNoReplacement()
    {
        // Arrange
        var store = new VirtualOutputExternalEnrollmentStore();
        var deviceId = VirtualDeviceId.New();
        store.Set(
            CreateEnrollment(deviceId, 1, DateTimeOffset.UtcNow.AddMinutes(1)),
            () => null);
        _ = store.Take(deviceId);

        // Act
        var result = store.Take(deviceId);

        // Assert
        Assert.Null(result);
    }

    private static VirtualOutputExternalEnrollment CreateEnrollment(
        VirtualDeviceId deviceId,
        long generation,
        DateTimeOffset expiresUtc) =>
        new()
        {
            VirtualDeviceId = deviceId,
            Bootstrap = new VirtualOutputWorkerBootstrap
            {
                Endpoint = new VirtualOutputIpcEndpoint
                {
                    Transport = VirtualOutputIpcTransport.NamedPipe,
                    Address = "lineup-test"
                },
                VirtualDevice = new VirtualDeviceDefinition
                {
                    Id = deviceId,
                    Name = "Test output",
                    Adapter = new AdapterConfiguration("test", JsonSerializer.SerializeToElement(new { }))
                },
                WorkerPolicy = new VirtualOutputWorkerPolicyDefinition
                {
                    Key = "default",
                    Name = "Default"
                },
                WorkerId = "test-worker",
                Generation = generation,
                BootstrapToken = $"token-{generation}",
                BootstrapExpiresUtc = expiresUtc
            }
        };

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
