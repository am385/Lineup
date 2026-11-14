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

    private static VirtualDeviceDefinition Virtual(PhysicalDeviceSubscriptionMode mode, HashSet<PhysicalDeviceId> selected) =>
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
        /// <summary>Gets the kind.</summary>
        public string Kind => "test";

        /// <summary>Gets the display name.</summary>
        public string DisplayName => "Test";

        /// <summary>Gets the supports user creation.</summary>
        public bool SupportsUserCreation => false;

        /// <summary>Creates default configuration.</summary>
        /// <param name="deviceId">Identity of the target device.</param>
        /// <returns>The operation's result.</returns>
        public AdapterConfiguration CreateDefaultConfiguration(VirtualDeviceId deviceId) =>
            throw new NotSupportedException();

        /// <summary>Validates the compatible driver.</summary>
        /// <param name="configuration">Configuration to apply.</param>
        /// <returns>The operation's result.</returns>
        public IReadOnlyList<string> Validate(AdapterConfiguration configuration) => [];

        /// <summary>Determines support for physical adapter.</summary>
        /// <param name="physicalAdapterKind">The physical adapter kind value.</param>
        /// <returns>The operation's result.</returns>
        public bool SupportsPhysicalAdapter(string physicalAdapterKind) =>
            string.Equals(physicalAdapterKind, supportedKind, StringComparison.Ordinal);
    }
}
