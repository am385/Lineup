using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Xunit;

namespace Lineup.Core.Tests.Streaming;

/// <summary>Tests virtual-device physical subscription compatibility.</summary>
public sealed class VirtualDeviceSubscriptionResolverTests
{
    /// <summary>Verifies All subscriptions exclude incompatible physical adapters.</summary>
    [Fact]
    public void Resolve_AllModeFiltersIncompatibleAdapters()
    {
        // Arrange
        var compatible = Physical("hdhomerun", order: 0);
        var incompatible = Physical("lineup-link", order: 1);
        var virtualDevice = Virtual(PhysicalDeviceSubscriptionMode.All, []);
        var driver = new CompatibleDriver("hdhomerun");

        // Act
        var resolved = VirtualDeviceSubscriptionResolver.Resolve(virtualDevice, [compatible, incompatible], driver);

        // Assert
        Assert.Equal(compatible.Id, Assert.Single(resolved).Id);
    }

    /// <summary>Verifies Selected subscriptions explicitly reject incompatible devices.</summary>
    [Fact]
    public void Resolve_SelectedModeRejectsIncompatibleAdapters()
    {
        // Arrange
        var incompatible = Physical("lineup-link", order: 0);
        var virtualDevice = Virtual(PhysicalDeviceSubscriptionMode.Selected, [incompatible.Id]);
        var driver = new CompatibleDriver("hdhomerun");

        // Act
        var exception = Assert.Throws<InvalidOperationException>(
            () => VirtualDeviceSubscriptionResolver.Resolve(virtualDevice, [incompatible], driver));

        // Assert
        Assert.Contains("does not support", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static PhysicalDeviceDefinition Physical(string kind, int order) =>
        new()
        {
            Id = PhysicalDeviceId.New(),
            Name = kind,
            Enabled = true,
            Order = order,
            Adapter = new AdapterConfiguration(kind, System.Text.Json.JsonSerializer.SerializeToElement(new { }))
        };

    private static VirtualDeviceDefinition Virtual(
        PhysicalDeviceSubscriptionMode mode,
        HashSet<PhysicalDeviceId> selected) =>
        new()
        {
            Id = VirtualDeviceId.New(),
            Name = "Virtual",
            Enabled = true,
            Adapter = new AdapterConfiguration("test", System.Text.Json.JsonSerializer.SerializeToElement(new { })),
            Subscription = new PhysicalDeviceSubscription
            {
                Mode = mode,
                SelectedDeviceIds = selected
            }
        };

    private sealed class CompatibleDriver(string supportedKind) : IVirtualTunerDriver
    {
        public string Kind => "test";

        public string DisplayName => "Test";

        public bool SupportsUserCreation => false;

        public AdapterConfiguration CreateDefaultConfiguration(VirtualDeviceId deviceId) =>
            throw new NotSupportedException();

        public IReadOnlyList<string> Validate(AdapterConfiguration configuration) => [];

        public bool SupportsPhysicalAdapter(string physicalAdapterKind) =>
            string.Equals(physicalAdapterKind, supportedKind, StringComparison.Ordinal);
    }
}
