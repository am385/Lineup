using System.Text.Json;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Xunit;

namespace Lineup.Core.Tests.Streaming;

/// <summary>
/// Verifies configured physical device activation.
/// </summary>
public sealed class PhysicalDeviceCatalogTests
{
    /// <summary>
    /// Verifies enabled devices are created in deterministic configured order.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_EnabledDevices_CreatesOrderedCatalog()
    {
        // Arrange
        var registry = new StreamingAdapterRegistry([new FakePhysicalDriver()], [], []);
        var catalog = new PhysicalDeviceCatalog(registry);
        var firstId = PhysicalDeviceId.New();
        var secondId = PhysicalDeviceId.New();
        var configuration = new StreamingConfiguration
        {
            PhysicalDevices =
            [
                CreateDefinition(secondId, "Second", 20, true),
                CreateDefinition(PhysicalDeviceId.New(), "Disabled", 0, false),
                CreateDefinition(firstId, "First", 10, true)
            ]
        };

        // Act
        await catalog.ApplyAsync(configuration, TestContext.Current.CancellationToken);

        // Assert
        Assert.Collection(
            catalog.Devices,
            device => Assert.Equal(firstId, device.Snapshot.Id),
            device => Assert.Equal(secondId, device.Snapshot.Id));
        Assert.Equal(secondId, catalog.GetRequired(secondId).Snapshot.Id);
    }

    /// <summary>Verifies one device activation failure does not suppress healthy devices.</summary>
    [Fact]
    public async Task ApplyAsync_OneDeviceFails_ActivatesRemainingDevices()
    {
        // Arrange
        var registry = new StreamingAdapterRegistry([new FakePhysicalDriver()], [], []);
        var catalog = new PhysicalDeviceCatalog(registry);
        var failedId = PhysicalDeviceId.New();
        var healthyId = PhysicalDeviceId.New();
        var configuration = new StreamingConfiguration
        {
            PhysicalDevices =
            [
                CreateDefinition(failedId, "Unauthorized", 0, true),
                CreateDefinition(healthyId, "Healthy", 1, true)
            ]
        };

        // Act
        await catalog.ApplyAsync(configuration, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(healthyId, Assert.Single(catalog.Devices).Snapshot.Id);
    }

    /// <summary>Verifies repeated identical applications reuse active runtime devices.</summary>
    [Fact]
    public async Task ApplyAsync_IdenticalConfiguration_DoesNotRecreateDevices()
    {
        // Arrange
        var driver = new FakePhysicalDriver();
        var registry = new StreamingAdapterRegistry([driver], [], []);
        var catalog = new PhysicalDeviceCatalog(registry);
        var deviceId = PhysicalDeviceId.New();
        var configuration = new StreamingConfiguration
        {
            PhysicalDevices = [CreateDefinition(deviceId, "Living room", 0, true)]
        };

        // Act
        await catalog.ApplyAsync(configuration, TestContext.Current.CancellationToken);
        var original = Assert.Single(catalog.Devices);
        await catalog.ApplyAsync(
            new StreamingConfiguration
            {
                PhysicalDevices = [CreateDefinition(deviceId, "Living room", 0, true)]
            },
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Same(original, Assert.Single(catalog.Devices));
        Assert.Equal(1, driver.CreateCount);
    }

    /// <summary>Verifies concurrent identical applications activate one runtime device.</summary>
    [Fact]
    public async Task ApplyAsync_ConcurrentIdenticalConfiguration_CreatesDeviceOnce()
    {
        // Arrange
        var driver = new FakePhysicalDriver();
        var registry = new StreamingAdapterRegistry([driver], [], []);
        var catalog = new PhysicalDeviceCatalog(registry);
        var configuration = new StreamingConfiguration
        {
            PhysicalDevices = [CreateDefinition(PhysicalDeviceId.New(), "Living room", 0, true)]
        };

        // Act
        await Task.WhenAll(
            catalog.ApplyAsync(configuration, TestContext.Current.CancellationToken),
            catalog.ApplyAsync(configuration, TestContext.Current.CancellationToken));

        // Assert
        Assert.Single(catalog.Devices);
        Assert.Equal(1, driver.CreateCount);
    }

    /// <summary>Verifies a changed physical definition replaces and disposes the prior runtime device.</summary>
    [Fact]
    public async Task ApplyAsync_ChangedConfiguration_ReplacesRuntimeDevice()
    {
        // Arrange
        var driver = new FakePhysicalDriver();
        var registry = new StreamingAdapterRegistry([driver], [], []);
        var catalog = new PhysicalDeviceCatalog(registry);
        var deviceId = PhysicalDeviceId.New();
        await catalog.ApplyAsync(
            new StreamingConfiguration
            {
                PhysicalDevices = [CreateDefinition(deviceId, "Living room", 0, true)]
            },
            TestContext.Current.CancellationToken);
        var original = Assert.IsType<FakePhysicalDevice>(Assert.Single(catalog.Devices));

        // Act
        await catalog.ApplyAsync(
            new StreamingConfiguration
            {
                PhysicalDevices = [CreateDefinition(deviceId, "Family room", 0, true)]
            },
            TestContext.Current.CancellationToken);

        // Assert
        var replacement = Assert.IsType<FakePhysicalDevice>(Assert.Single(catalog.Devices));
        Assert.NotSame(original, replacement);
        Assert.True(original.IsDisposed);
        Assert.False(replacement.IsDisposed);
        Assert.Equal(2, driver.CreateCount);
    }

    /// <summary>Verifies failed activations remain eligible for a later retry.</summary>
    [Fact]
    public async Task ApplyAsync_FailedActivation_RetriesIdenticalConfiguration()
    {
        // Arrange
        var driver = new FakePhysicalDriver();
        var registry = new StreamingAdapterRegistry([driver], [], []);
        var catalog = new PhysicalDeviceCatalog(registry);
        var configuration = new StreamingConfiguration
        {
            PhysicalDevices = [CreateDefinition(PhysicalDeviceId.New(), "Fail", 0, true)]
        };

        // Act
        await catalog.ApplyAsync(configuration, TestContext.Current.CancellationToken);
        await catalog.ApplyAsync(configuration, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(catalog.Devices);
        Assert.Equal(2, driver.CreateCount);
    }

    private static PhysicalDeviceDefinition CreateDefinition(PhysicalDeviceId id, string name, int order, bool enabled) =>
        new()
        {
            Id = id,
            Name = name,
            Order = order,
            Enabled = enabled,
            Adapter = new AdapterConfiguration(FakePhysicalDriver.DriverKind, JsonDocument.Parse("{}").RootElement.Clone())
        };

    private sealed class FakePhysicalDriver : IPhysicalTunerDriver
    {
        private int _createCount;

        public const string DriverKind = "fake";

        public int CreateCount => Volatile.Read(ref _createCount);

        public string Kind => DriverKind;

        public string DisplayName => "Fake";

        public bool SupportsUserCreation => true;

        public DeviceCapabilities Capabilities { get; } = new();

        public AdapterConfiguration CreateDefaultConfiguration(PhysicalDeviceId deviceId) =>
            new(DriverKind, JsonDocument.Parse("{}").RootElement.Clone());

        public IReadOnlyList<string> Validate(AdapterConfiguration configuration) => [];

        public ValueTask<IPhysicalTunerDevice> CreateAsync(
            PhysicalDeviceId deviceId,
            string name,
            AdapterConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _createCount);
            return name switch
            {
                "Fail" => ValueTask.FromException<IPhysicalTunerDevice>(new IOException("Unavailable")),
                "Unauthorized" => ValueTask.FromException<IPhysicalTunerDevice>(new UnauthorizedAccessException("Rejected")),
                _ => ValueTask.FromResult<IPhysicalTunerDevice>(new FakePhysicalDevice(deviceId, name))
            };
        }
    }

    private sealed class FakePhysicalDevice : IPhysicalTunerDevice, IDisposable
    {
        private int _disposed;

        public FakePhysicalDevice(PhysicalDeviceId id, string name)
        {
            Snapshot = new PhysicalDeviceSnapshot
            {
                Id = id,
                Kind = FakePhysicalDriver.DriverKind,
                Name = name,
                Capacity = new PhysicalTunerCapacity(1)
            };
        }

        public PhysicalDeviceSnapshot Snapshot { get; }

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public Task<IReadOnlyList<PhysicalSourceChannel>> RefreshLineupAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PhysicalSourceChannel>>([]);

        public ValueTask<IPhysicalStreamLease> OpenStreamAsync(
            PhysicalStreamRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }
}
