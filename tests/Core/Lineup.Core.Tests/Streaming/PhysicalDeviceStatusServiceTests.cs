using System.Text.Json;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Xunit;

namespace Lineup.Core.Tests.Streaming;

/// <summary>
/// Verifies independent physical-device information and receiver-status refreshes.
/// </summary>
public sealed class PhysicalDeviceStatusServiceTests
{
    private const string PrivateDetail = "Synthetic private adapter credential.";

    /// <summary>Verifies every enabled definition is reported even when activation fails, with safe errors and deterministic ordering.</summary>
    [Theory]
    [InlineData("argument", false)]
    [InlineData("argument", true)]
    [InlineData("invalid-operation", false)]
    [InlineData("invalid-operation", true)]
    [InlineData("io", false)]
    [InlineData("io", true)]
    [InlineData("http", false)]
    [InlineData("http", true)]
    [InlineData("unauthorized", false)]
    [InlineData("unauthorized", true)]
    [InlineData("validation", false)]
    [InlineData("validation", true)]
    [InlineData("unknown-driver", false)]
    [InlineData("unknown-driver", true)]
    public async Task Refresh_ActivationFailure_ReportsEveryEnabledDefinition(string failure, bool informationOnly)
    {
        // Arrange
        var driver = new FakePhysicalDriver();
        var catalog = new PhysicalDeviceCatalog(new StreamingAdapterRegistry([driver], [], []));
        var service = new PhysicalDeviceStatusService(catalog);
        var failed = Definition("Unavailable", failure, 20);
        var healthy = Definition("Healthy", null, 10);
        var disabled = Definition("Disabled", "io", 0) with { Enabled = false };
        var configuration = new StreamingConfiguration { PhysicalDevices = [failed, disabled, healthy] };
        var changes = 0;
        service.StateChanged += () => changes++;

        // Act
        await RefreshSelectedAsync(service, configuration, informationOnly);

        // Assert
        Assert.Equal([healthy.Id, failed.Id], service.Devices.Select(runtime => runtime.Device.Id));
        var unavailable = Assert.Single(service.Devices, runtime => runtime.Error is not null);
        Assert.Equal(failed.Id, unavailable.Device.Id);
        Assert.Equal(failed.Name, unavailable.Device.Name);
        Assert.Equal(failed.Adapter.Kind, unavailable.Device.Kind);
        Assert.Contains("activation failed", unavailable.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(PrivateDetail, unavailable.Error!, StringComparison.Ordinal);
        Assert.Null(unavailable.Status);
        Assert.Equal(0, unavailable.Device.Capacity.TunerCount);
        Assert.False(unavailable.SupportsReceiverControl);
        Assert.False(unavailable.SupportsRestart);
        Assert.Null(service.Devices[0].Error);
        Assert.NotNull(service.Devices[0].Status);
        Assert.Equal(healthy.Id, Assert.Single(catalog.Devices).Snapshot.Id);
        Assert.DoesNotContain(disabled.Id, driver.CreatedIds);
        Assert.Equal(1, changes);
    }

    /// <summary>Verifies failed reactivation preserves the same adapter's last successful observations but not stale control support.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_ReactivationFailure_PreservesPriorSnapshotAndCurrentName(bool informationOnly)
    {
        // Arrange
        var driver = new FakePhysicalDriver { FailActivation = false };
        var service = new PhysicalDeviceStatusService(new PhysicalDeviceCatalog(new StreamingAdapterRegistry([driver], [], [])));
        var definition = Definition("Original", "io", 0);
        var configuration = new StreamingConfiguration { PhysicalDevices = [definition] };
        await service.RefreshAsync(configuration, TestContext.Current.CancellationToken);
        var prior = Assert.Single(service.Devices);
        Assert.True(prior.SupportsReceiverControl);
        Assert.True(prior.SupportsRestart);
        driver.FailActivation = true;
        configuration = configuration with { PhysicalDevices = [definition with { Name = "Renamed unavailable device" }] };

        // Act
        await RefreshSelectedAsync(service, configuration, informationOnly);

        // Assert
        var runtime = Assert.Single(service.Devices);
        Assert.NotNull(runtime.Error);
        Assert.Equal("Renamed unavailable device", runtime.Device.Name);
        Assert.Same(prior.Status, runtime.Status);
        Assert.Equal(prior.Device.Capacity, runtime.Device.Capacity);
        Assert.Equal("initial", runtime.Status?.Details["firmware"]);
        Assert.Equal(7, Assert.Single(runtime.Status?.Receivers ?? []).Index);
        Assert.False(runtime.SupportsReceiverControl);
        Assert.False(runtime.SupportsRestart);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.StopReceiverAsync(definition.Id, 7, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.RestartAsync(definition.Id, TestContext.Current.CancellationToken));
    }

    /// <summary>Verifies metadata from another adapter kind is not attached to a failed replacement using the same configured identity.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_DifferentAdapterFails_DiscardsIncompatiblePriorSnapshot(bool informationOnly)
    {
        // Arrange
        var driver = new FakePhysicalDriver();
        var service = new PhysicalDeviceStatusService(new PhysicalDeviceCatalog(new StreamingAdapterRegistry([driver], [], [])));
        var original = Definition("Original", null, 0);
        await service.RefreshAsync(new StreamingConfiguration { PhysicalDevices = [original] }, TestContext.Current.CancellationToken);
        var replacement = Definition("Replacement", "unknown-driver", 0) with { Id = original.Id };

        // Act
        await RefreshSelectedAsync(service, new StreamingConfiguration { PhysicalDevices = [replacement] }, informationOnly);

        // Assert
        var runtime = Assert.Single(service.Devices);
        Assert.Equal(replacement.Adapter.Kind, runtime.Device.Kind);
        Assert.Equal(replacement.Name, runtime.Device.Name);
        Assert.NotNull(runtime.Error);
        Assert.Null(runtime.Status);
        Assert.Equal(0, runtime.Device.Capacity.TunerCount);
        Assert.False(runtime.SupportsReceiverControl);
        Assert.False(runtime.SupportsRestart);
    }

    /// <summary>Verifies an unchanged configuration replaces activation failure with live status when the adapter recovers.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_ActivationRecovers_ClearsFailureAndRestoresCapabilities(bool informationOnly)
    {
        // Arrange
        var driver = new FakePhysicalDriver();
        var service = new PhysicalDeviceStatusService(new PhysicalDeviceCatalog(new StreamingAdapterRegistry([driver], [], [])));
        var definition = Definition("Recovering", "io", 0);
        var configuration = new StreamingConfiguration { PhysicalDevices = [definition] };
        await RefreshSelectedAsync(service, configuration, informationOnly);
        var failed = service.Devices;
        driver.FailActivation = false;

        // Act
        await RefreshSelectedAsync(service, configuration, informationOnly);

        // Assert
        Assert.NotNull(Assert.Single(failed).Error);
        var runtime = Assert.Single(service.Devices);
        Assert.Null(runtime.Error);
        Assert.NotNull(runtime.Status);
        Assert.Equal(1, runtime.Device.Capacity.TunerCount);
        Assert.True(runtime.SupportsReceiverControl);
        Assert.True(runtime.SupportsRestart);
        Assert.Equal([definition.Id, definition.Id], driver.CreatedIds);
    }

    /// <summary>Verifies removed and disabled definitions are pruned from cached status instead of becoming activation failures.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Refresh_NoEnabledDevices_RemovesPriorStatus(bool informationOnly, bool remove)
    {
        // Arrange
        var driver = new FakePhysicalDriver();
        var service = new PhysicalDeviceStatusService(new PhysicalDeviceCatalog(new StreamingAdapterRegistry([driver], [], [])));
        var definition = Definition("Original", null, 0);
        await service.RefreshAsync(new StreamingConfiguration { PhysicalDevices = [definition] }, TestContext.Current.CancellationToken);
        var configuration = new StreamingConfiguration { PhysicalDevices = remove ? [] : [definition with { Enabled = false }] };

        // Act
        await RefreshSelectedAsync(service, configuration, informationOnly);

        // Assert
        Assert.Empty(service.Devices);
        Assert.Equal([definition.Id], driver.CreatedIds);
    }

    /// <summary>Verifies caller cancellation does not publish fabricated failures or prevent a later successful refresh.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_CanceledActivation_PreservesPublishedStateAndAllowsRetry(bool informationOnly)
    {
        // Arrange
        var driver = new FakePhysicalDriver();
        var service = new PhysicalDeviceStatusService(new PhysicalDeviceCatalog(new StreamingAdapterRegistry([driver], [], [])));
        var definition = Definition("Original", null, 0);
        var configuration = new StreamingConfiguration { PhysicalDevices = [definition] };
        await service.RefreshAsync(configuration, TestContext.Current.CancellationToken);
        var prior = service.Devices;
        configuration = configuration with { PhysicalDevices = [definition with { Name = "Replacement" }] };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        driver.BeforeCreate = token =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
        };
        var changes = 0;
        service.StateChanged += () => changes++;

        // Act
        var exception = await Record.ExceptionAsync(() => RefreshSelectedAsync(service, configuration, informationOnly, cancellation.Token));
        var afterCancellation = service.Devices;
        var cancellationChanges = changes;
        driver.BeforeCreate = null;
        await RefreshSelectedAsync(service, configuration, informationOnly).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(exception, exactMatch: false).CancellationToken);
        Assert.Same(prior, afterCancellation);
        Assert.Equal(0, cancellationChanges);
        Assert.Equal(1, changes);
        Assert.Null(Assert.Single(service.Devices).Error);
        Assert.Equal("Replacement", Assert.Single(service.Devices).Device.Name);
    }

    /// <summary>Verifies device information refresh preserves current receiver state without polling receivers again.</summary>
    [Fact]
    public async Task RefreshDeviceInformationAsync_AfterStatusRefresh_PreservesReceivers()
    {
        // Arrange
        var driver = new FakePhysicalDriver();
        var catalog = new PhysicalDeviceCatalog(new StreamingAdapterRegistry([driver], [], []));
        var service = new PhysicalDeviceStatusService(catalog);
        var deviceId = PhysicalDeviceId.New();
        var configuration = new StreamingConfiguration
        {
            PhysicalDevices =
            [
                new PhysicalDeviceDefinition
                {
                    Id = deviceId,
                    Name = "Living room",
                    Enabled = true,
                    Adapter = new AdapterConfiguration(FakePhysicalDriver.DriverKind, JsonDocument.Parse("{}").RootElement.Clone())
                }
            ]
        };
        await service.RefreshAsync(configuration, TestContext.Current.CancellationToken);

        // Act
        await service.RefreshDeviceInformationAsync(configuration, TestContext.Current.CancellationToken);

        // Assert
        var device = Assert.IsType<FakePhysicalDevice>(driver.Device);
        Assert.Equal(1, device.StatusRefreshCount);
        Assert.Equal(1, device.InformationRefreshCount);
        var runtime = Assert.Single(service.Devices);
        Assert.Equal("updated", runtime.Status?.Details["firmware"]);
        Assert.Equal(7, Assert.Single(runtime.Status?.Receivers ?? []).Index);
    }

    /// <summary>Verifies rejected credentials retain the configured device and its prior status as unavailable.</summary>
    [Fact]
    public async Task RefreshAsync_UnauthorizedStatus_RetainsDeviceWithFailure()
    {
        // Arrange
        var driver = new FakePhysicalDriver();
        var catalog = new PhysicalDeviceCatalog(new StreamingAdapterRegistry([driver], [], []));
        var service = new PhysicalDeviceStatusService(catalog);
        var configuration = new StreamingConfiguration
        {
            PhysicalDevices =
            [
                new PhysicalDeviceDefinition
                {
                    Id = PhysicalDeviceId.New(),
                    Name = "Remote",
                    Enabled = true,
                    Adapter = new AdapterConfiguration(FakePhysicalDriver.DriverKind, JsonDocument.Parse("{}").RootElement.Clone())
                }
            ]
        };
        await service.RefreshAsync(configuration, TestContext.Current.CancellationToken);
        var device = Assert.IsType<FakePhysicalDevice>(driver.Device);
        device.RejectStatus = true;

        // Act
        await service.RefreshAsync(configuration, TestContext.Current.CancellationToken);

        // Assert
        var runtime = Assert.Single(service.Devices);
        Assert.Equal("Status refresh failed (UnauthorizedAccessException).", runtime.Error);
        Assert.Equal("initial", runtime.Status?.Details["firmware"]);
        Assert.Equal(7, Assert.Single(runtime.Status?.Receivers ?? []).Index);
    }

    private static PhysicalDeviceDefinition Definition(string name, string? failure, int order) => new()
    {
        Id = PhysicalDeviceId.New(),
        Name = name,
        Order = order,
        Adapter = new AdapterConfiguration(failure == "unknown-driver" ? "missing-status-driver" : FakePhysicalDriver.DriverKind, JsonSerializer.SerializeToElement(new { failure }))
    };

    private static Task RefreshSelectedAsync(PhysicalDeviceStatusService service, StreamingConfiguration configuration, bool informationOnly, CancellationToken? cancellationToken = null) =>
        informationOnly
            ? service.RefreshDeviceInformationAsync(configuration, cancellationToken ?? TestContext.Current.CancellationToken)
            : service.RefreshAsync(configuration, cancellationToken ?? TestContext.Current.CancellationToken);

    private sealed class FakePhysicalDriver : IPhysicalTunerDriver
    {
        public const string DriverKind = "fake-status";

        public FakePhysicalDevice? Device { get; private set; }

        public bool FailActivation { get; set; } = true;

        public Action<CancellationToken>? BeforeCreate { get; set; }

        public List<PhysicalDeviceId> CreatedIds { get; } = [];

        public string Kind => DriverKind;

        public string DisplayName => "Fake status";

        public bool SupportsUserCreation => true;

        public DeviceCapabilities Capabilities { get; } = new();

        public AdapterConfiguration CreateDefaultConfiguration(PhysicalDeviceId deviceId) =>
            new(DriverKind, JsonDocument.Parse("{}").RootElement.Clone());

        public IReadOnlyList<string> Validate(AdapterConfiguration configuration) =>
            FailActivation && GetFailure(configuration) == "validation" ? [PrivateDetail] : [];

        public ValueTask<IPhysicalTunerDevice> CreateAsync(
            PhysicalDeviceId deviceId,
            string name,
            AdapterConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            CreatedIds.Add(deviceId);
            BeforeCreate?.Invoke(cancellationToken);
            var failure = FailActivation ? GetFailure(configuration) : null;
            var exception = failure switch
            {
                "argument" => new ArgumentException(PrivateDetail),
                "invalid-operation" => new InvalidOperationException(PrivateDetail),
                "io" => new IOException(PrivateDetail),
                "http" => new HttpRequestException(PrivateDetail),
                "unauthorized" => (Exception)new UnauthorizedAccessException(PrivateDetail),
                _ => null
            };
            if (exception is not null)
            {
                return ValueTask.FromException<IPhysicalTunerDevice>(exception);
            }
            Device = new FakePhysicalDevice(deviceId, name);
            return ValueTask.FromResult<IPhysicalTunerDevice>(Device);
        }

        private static string? GetFailure(AdapterConfiguration configuration) =>
            configuration.Configuration.TryGetProperty("failure", out var failure) ? failure.GetString() : null;
    }

    private sealed class FakePhysicalDevice : IPhysicalTunerDevice, IPhysicalDeviceStatusCapability, IPhysicalDeviceInformationCapability, IPhysicalReceiverControlCapability, IPhysicalDeviceRestartCapability
    {
        private int _informationRefreshCount;
        private int _statusRefreshCount;

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

        public int InformationRefreshCount => Volatile.Read(ref _informationRefreshCount);

        public int StatusRefreshCount => Volatile.Read(ref _statusRefreshCount);

        public bool RejectStatus { get; set; }

        public PhysicalDeviceSnapshot Snapshot { get; }

        public Task<PhysicalDeviceStatusSnapshot> RefreshDeviceInformationAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _informationRefreshCount);
            return Task.FromResult(
                new PhysicalDeviceStatusSnapshot
                {
                    Device = Snapshot,
                    Details = new Dictionary<string, string>(StringComparer.Ordinal) { ["firmware"] = "updated" }
                });
        }

        public Task<PhysicalDeviceStatusSnapshot> RefreshStatusAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _statusRefreshCount);
            if (RejectStatus)
            {
                throw new UnauthorizedAccessException("Rejected");
            }
            return Task.FromResult(
                new PhysicalDeviceStatusSnapshot
                {
                    Device = Snapshot,
                    Details = new Dictionary<string, string>(StringComparer.Ordinal) { ["firmware"] = "initial" },
                    Receivers = [new PhysicalReceiverStatus { Index = 7 }]
                });
        }

        public Task<IReadOnlyList<PhysicalSourceChannel>> RefreshLineupAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PhysicalSourceChannel>>([]);

        public ValueTask<IPhysicalStreamLease> OpenStreamAsync(
            PhysicalStreamRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task StopReceiverAsync(int receiverIndex, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RestartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
