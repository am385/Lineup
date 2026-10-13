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

    private sealed class FakePhysicalDriver : IPhysicalTunerDriver
    {
        public const string DriverKind = "fake-status";

        public FakePhysicalDevice? Device { get; private set; }

        public string Kind => DriverKind;

        public string DisplayName => "Fake status";

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
            Device = new FakePhysicalDevice(deviceId, name);
            return ValueTask.FromResult<IPhysicalTunerDevice>(Device);
        }
    }

    private sealed class FakePhysicalDevice : IPhysicalTunerDevice, IPhysicalDeviceStatusCapability, IPhysicalDeviceInformationCapability
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
    }
}
