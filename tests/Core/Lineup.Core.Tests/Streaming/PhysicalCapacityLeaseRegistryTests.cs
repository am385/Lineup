using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Xunit;

namespace Lineup.Core.Tests.Streaming;

/// <summary>
/// Verifies provider-neutral physical capacity accounting.
/// </summary>
public sealed class PhysicalCapacityLeaseRegistryTests
{
    /// <summary>Verifies concurrent distinct acquisitions cannot exceed finite capacity.</summary>
    [Fact]
    public async Task TryAcquireAsync_ConcurrentDifferentAcquisitions_GrantsOnlyCapacity()
    {
        // Arrange
        var registry = new PhysicalCapacityLeaseRegistry();
        var deviceId = PhysicalDeviceId.New();
        var keys = Enumerable.Range(0, 8).Select(index => new PhysicalAcquisitionKey(new SourceChannelId(deviceId, $"{index}.1"), "none")).ToArray();

        // Act
        var leases = await Task.WhenAll(keys.Select(key => registry.TryAcquireAsync(deviceId, key, new PhysicalTunerCapacity(2), TestContext.Current.CancellationToken).AsTask()));

        // Assert
        Assert.Equal(2, leases.Count(lease => lease is not null));
        foreach (var lease in leases)
        {
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Verifies identical acquisitions share one finite tuner slot across receivers.
    /// </summary>
    [Fact]
    public async Task TryAcquireAsync_SameAcquisition_SharesCapacity()
    {
        // Arrange
        var registry = new PhysicalCapacityLeaseRegistry();
        var deviceId = PhysicalDeviceId.New();
        var firstKey = new PhysicalAcquisitionKey(new SourceChannelId(deviceId, "7.1"), "none");
        var secondKey = new PhysicalAcquisitionKey(new SourceChannelId(deviceId, "9.1"), "none");

        // Act
        await using var first = await registry.TryAcquireAsync(deviceId, firstKey, new PhysicalTunerCapacity(1), TestContext.Current.CancellationToken);
        await using var shared = await registry.TryAcquireAsync(deviceId, firstKey, new PhysicalTunerCapacity(1), TestContext.Current.CancellationToken);
        await using var rejected = await registry.TryAcquireAsync(deviceId, secondKey, new PhysicalTunerCapacity(1), TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(first);
        Assert.NotNull(shared);
        Assert.Null(rejected);
        var diagnostics = Assert.Single(registry.GetDiagnostics());
        Assert.Equal(1, diagnostics.ActiveAcquisitions);
        Assert.Equal(2, diagnostics.Receivers);
    }

    /// <summary>Verifies final shared release frees its physical tuner slot.</summary>
    [Fact]
    public async Task DisposeAsync_FinalSharedLease_FreesSlot()
    {
        // Arrange
        var registry = new PhysicalCapacityLeaseRegistry();
        var deviceId = PhysicalDeviceId.New();
        var sharedKey = new PhysicalAcquisitionKey(new SourceChannelId(deviceId, "7.1"), "none");
        var nextKey = new PhysicalAcquisitionKey(new SourceChannelId(deviceId, "9.1"), "none");
        var first = await registry.TryAcquireAsync(deviceId, sharedKey, new PhysicalTunerCapacity(1), TestContext.Current.CancellationToken);
        var second = await registry.TryAcquireAsync(deviceId, sharedKey, new PhysicalTunerCapacity(1), TestContext.Current.CancellationToken);

        // Act
        await first!.DisposeAsync();
        var occupied = await registry.TryAcquireAsync(deviceId, nextKey, new PhysicalTunerCapacity(1), TestContext.Current.CancellationToken);
        await second!.DisposeAsync();
        var available = await registry.TryAcquireAsync(deviceId, nextKey, new PhysicalTunerCapacity(1), TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(occupied);
        Assert.NotNull(available);
        await available.DisposeAsync();
    }

    /// <summary>Verifies output-profile-specific acquisitions consume distinct finite slots.</summary>
    [Fact]
    public async Task TryAcquireAsync_DifferentProfiles_IsolatesCapacity()
    {
        // Arrange
        var registry = new PhysicalCapacityLeaseRegistry();
        var deviceId = PhysicalDeviceId.New();
        var source = new SourceChannelId(deviceId, "7.1");

        // Act
        await using var first = await registry.TryAcquireAsync(deviceId, new PhysicalAcquisitionKey(source, "none"), new PhysicalTunerCapacity(1), TestContext.Current.CancellationToken);
        await using var rejected = await registry.TryAcquireAsync(deviceId, new PhysicalAcquisitionKey(source, "mobile"), new PhysicalTunerCapacity(1), TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(first);
        Assert.Null(rejected);
    }

    /// <summary>Verifies cancellation before acquisition cannot leak capacity.</summary>
    [Fact]
    public async Task TryAcquireAsync_Canceled_DoesNotLeakLease()
    {
        // Arrange
        var registry = new PhysicalCapacityLeaseRegistry();
        var deviceId = PhysicalDeviceId.New();
        var key = new PhysicalAcquisitionKey(new SourceChannelId(deviceId, "7.1"), "none");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        var exception = Record.Exception(() =>
            registry.TryAcquireAsync(deviceId, key, new PhysicalTunerCapacity(1), cancellation.Token));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        await using var subsequent = await registry.TryAcquireAsync(deviceId, key, new PhysicalTunerCapacity(1), TestContext.Current.CancellationToken);
        Assert.NotNull(subsequent);
    }

    /// <summary>Verifies disposal remains safe after a consumer failure.</summary>
    [Fact]
    public async Task DisposeAsync_AfterConsumerFailure_DoesNotLeakLease()
    {
        // Arrange
        var registry = new PhysicalCapacityLeaseRegistry();
        var deviceId = PhysicalDeviceId.New();
        var firstKey = new PhysicalAcquisitionKey(new SourceChannelId(deviceId, "7.1"), "none");
        var secondKey = new PhysicalAcquisitionKey(new SourceChannelId(deviceId, "9.1"), "none");
        var lease = await registry.TryAcquireAsync(deviceId, firstKey, new PhysicalTunerCapacity(1), TestContext.Current.CancellationToken);

        // Act
        await lease!.DisposeAsync();
        await lease.DisposeAsync();
        await using var subsequent = await registry.TryAcquireAsync(deviceId, secondKey, new PhysicalTunerCapacity(1), TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(subsequent);
        Assert.Single(registry.GetDiagnostics());
    }
}
