using System.Text.Json;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Extensibility.Streaming;
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
    [Theory]
    [InlineData("Unauthorized")]
    [InlineData("HttpTimeout")]
    public async Task ApplyAsync_OneDeviceFails_ActivatesRemainingDevices(string failure)
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
                CreateDefinition(failedId, failure, 0, true),
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

    /// <summary>Verifies retrying a failed sibling retains the healthy runtime and later admits the recovered device.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_PartialActivation_RetainsUnchangedHealthyDevice(bool recover)
    {
        // Arrange
        var driver = new FakePhysicalDriver();
        var catalog = new PhysicalDeviceCatalog(new StreamingAdapterRegistry([driver], [], []));
        var healthyId = PhysicalDeviceId.New();
        var failedId = PhysicalDeviceId.New();
        var configuration = new StreamingConfiguration
        {
            PhysicalDevices = [CreateDefinition(healthyId, "Healthy", 0, true), CreateDefinition(failedId, "Fail", 1, true)]
        };
        await catalog.ApplyAsync(configuration, TestContext.Current.CancellationToken);
        var original = Assert.IsType<FakePhysicalDevice>(Assert.Single(catalog.Devices));
        driver.FailActivation = !recover;

        // Act
        await catalog.ApplyAsync(configuration, TestContext.Current.CancellationToken);

        // Assert
        Assert.Same(original, catalog.GetRequired(healthyId));
        Assert.False(original.IsDisposed);
        Assert.Equal(recover ? 2 : 1, catalog.Devices.Count);
        Assert.Equal(3, driver.CreateCount);
    }

    /// <summary>Verifies cancellation rolls back only newly created devices, not retained or previously published devices.</summary>
    [Fact]
    public async Task ApplyAsync_CanceledReplacement_PreservesPublishedDevices()
    {
        // Arrange
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var driver = new FakePhysicalDriver();
        var catalog = new PhysicalDeviceCatalog(new StreamingAdapterRegistry([driver], [], []));
        var retained = CreateDefinition(PhysicalDeviceId.New(), "Retained", 0, true);
        var changed = CreateDefinition(PhysicalDeviceId.New(), "Original", 1, true);
        var configuration = new StreamingConfiguration { PhysicalDevices = [retained, changed] };
        await catalog.ApplyAsync(configuration, cancellation.Token);
        var original = catalog.Devices.Cast<FakePhysicalDevice>().ToArray();
        driver.BeforeCreate = (name, token) =>
        {
            if (name == "Cancel")
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
        };

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.ApplyAsync(configuration with
        {
            PhysicalDevices = [retained, changed with { Name = "Replacement" }, CreateDefinition(PhysicalDeviceId.New(), "Cancel", 2, true)]
        }, cancellation.Token));

        // Assert
        Assert.Equal(original, catalog.Devices);
        Assert.All(original, device => Assert.False(device.IsDisposed));
        Assert.True(Assert.Single(driver.Created, device => device.Snapshot.Name == "Replacement").IsDisposed);
    }

    /// <summary>Verifies removing or disabling a definition disposes only that runtime and updates the published configuration identity.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_RemovedOrDisabledDevice_RetainsRemainingRuntime(bool disable)
    {
        // Arrange
        var driver = new FakePhysicalDriver();
        var catalog = new PhysicalDeviceCatalog(new StreamingAdapterRegistry([driver], [], []));
        var retained = CreateDefinition(PhysicalDeviceId.New(), "Retained", 0, true);
        var removed = CreateDefinition(PhysicalDeviceId.New(), "Removed", 1, true);
        var original = new StreamingConfiguration { PhysicalDevices = [retained, removed] };
        await catalog.ApplyAsync(original, TestContext.Current.CancellationToken);
        var retainedRuntime = Assert.IsType<FakePhysicalDevice>(catalog.GetRequired(retained.Id));
        var removedRuntime = Assert.IsType<FakePhysicalDevice>(catalog.GetRequired(removed.Id));
        var replacement = original with { PhysicalDevices = disable ? [retained, removed with { Enabled = false }] : [retained] };

        // Act
        await catalog.ApplyAsync(replacement, TestContext.Current.CancellationToken);

        // Assert
        Assert.Same(retainedRuntime, Assert.Single(catalog.Devices));
        Assert.False(retainedRuntime.IsDisposed);
        Assert.True(removedRuntime.IsDisposed);
        Assert.True(catalog.IsConfigurationCurrent(replacement));
        Assert.False(catalog.IsConfigurationCurrent(original));
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

        /// <summary>Gets or sets whether the named failing device remains unavailable.</summary>
        public bool FailActivation { get; set; } = true;

        /// <summary>Gets or sets an activation hook for deterministic cancellation.</summary>
        public Action<string, CancellationToken>? BeforeCreate { get; set; }

        /// <summary>Gets owned fake runtime instances for rollback checks.</summary>
        public List<FakePhysicalDevice> Created { get; } = [];

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
            BeforeCreate?.Invoke(name, cancellationToken);
            if (name is not ("Unauthorized" or "HttpTimeout") && (name != "Fail" || !FailActivation))
            {
                var device = new FakePhysicalDevice(deviceId, name);
                Created.Add(device);
                return ValueTask.FromResult<IPhysicalTunerDevice>(device);
            }
            return name switch
            {
                "Fail" => ValueTask.FromException<IPhysicalTunerDevice>(new IOException("Unavailable")),
                "Unauthorized" => ValueTask.FromException<IPhysicalTunerDevice>(new UnauthorizedAccessException("Rejected")),
                "HttpTimeout" => ValueTask.FromException<IPhysicalTunerDevice>(new HttpRequestException("Remote control request timed out.", new TaskCanceledException())),
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
