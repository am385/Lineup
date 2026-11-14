using System.Text.Json;
using Xunit;

namespace Lineup.Core.Tests.Guides;

/// <summary>Verifies active guide-source subscription resolution.</summary>
public sealed class GuideSourceSubscriptionResolverTests
{
    /// <summary>
    /// Verifies All mode includes enabled compatible devices in deterministic topology order.
    /// </summary>
    [Fact]
    public void Resolve_AllMode_IncludesEnabledCompatibleDevicesInOrder()
    {
        // Arrange
        var first = Physical("hdhomerun", order: 2);
        var second = Physical("hdhomerun", order: 1);
        var disabled = Physical("hdhomerun", order: 0, enabled: false);
        var incompatible = Physical("m3u", order: 3);
        var source = Source(new PhysicalDeviceSubscription());

        // Act
        var scope = GuideSourceSubscriptionResolver.Resolve(
            source,
            [first, disabled, incompatible, second],
            new TestGuideSourceDriver());

        // Assert
        Assert.Equal([second.Id, first.Id], scope.PhysicalDeviceIds);
    }

    /// <summary>
    /// Verifies Selected mode reports an incompatible physical adapter instead of silently ignoring it.
    /// </summary>
    [Fact]
    public void Resolve_SelectedMode_IncompatibleDevice_Throws()
    {
        // Arrange
        var incompatible = Physical("m3u", order: 0);
        var source = Source(new PhysicalDeviceSubscription
        {
            Mode = PhysicalDeviceSubscriptionMode.Selected,
            SelectedDeviceIds = [incompatible.Id]
        });

        // Act
        var action = () => GuideSourceSubscriptionResolver.Resolve(
            source,
            [incompatible],
            new TestGuideSourceDriver());

        // Assert
        var exception = Assert.Throws<InvalidOperationException>(action);
        Assert.Contains("does not support", exception.Message, StringComparison.Ordinal);
    }

    private static PhysicalDeviceDefinition Physical(string kind, int order, bool enabled = true) =>
        new()
        {
            Id = PhysicalDeviceId.New(),
            Name = $"{kind} {order}",
            Enabled = enabled,
            Order = order,
            Adapter = Adapter(kind)
        };

    private static GuideSourceDefinition Source(PhysicalDeviceSubscription subscription) =>
        new()
        {
            Id = GuideSourceId.New(),
            Name = "Guide",
            Adapter = Adapter("guide"),
            Subscription = subscription
        };

    private static AdapterConfiguration Adapter(string kind)
    {
        using var document = JsonDocument.Parse("{}");
        return new AdapterConfiguration(kind, document.RootElement.Clone());
    }

    private sealed class TestGuideSourceDriver : IGuideSourceDriver
    {
        /// <summary>Gets the kind.</summary>
        public string Kind => "guide";

        /// <summary>Gets the display name.</summary>
        public string DisplayName => "Guide";

        /// <summary>Gets the supports user creation.</summary>
        public bool SupportsUserCreation => true;

        /// <summary>Creates default configuration.</summary>
        /// <param name="sourceId">The source ID value.</param>
        /// <returns>The operation's result.</returns>
        public AdapterConfiguration CreateDefaultConfiguration(GuideSourceId sourceId) => Adapter(Kind);

        /// <summary>Determines support for physical adapter.</summary>
        /// <param name="physicalAdapterKind">The physical adapter kind value.</param>
        /// <returns>The operation's result.</returns>
        public bool SupportsPhysicalAdapter(string physicalAdapterKind) =>
            string.Equals(physicalAdapterKind, "hdhomerun", StringComparison.Ordinal);

        /// <summary>Validates the test guide source driver.</summary>
        /// <param name="configuration">Configuration to apply.</param>
        /// <returns>The operation's result.</returns>
        public IReadOnlyList<string> Validate(AdapterConfiguration configuration) => [];

        /// <summary>Creates the test guide source driver asynchronously.</summary>
        /// <param name="sourceId">The source ID value.</param>
        /// <param name="configuration">Configuration to apply.</param>
        /// <param name="physicalDeviceIds">The physical device IDs value.</param>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        /// <returns>The operation's result.</returns>
        public ValueTask<IGuideSource> CreateAsync(GuideSourceId sourceId, AdapterConfiguration configuration, IReadOnlyList<PhysicalDeviceId> physicalDeviceIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
