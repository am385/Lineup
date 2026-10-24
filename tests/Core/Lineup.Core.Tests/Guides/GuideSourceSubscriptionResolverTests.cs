using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
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
        public string Kind => "guide";

        public string DisplayName => "Guide";

        public bool SupportsUserCreation => true;

        public AdapterConfiguration CreateDefaultConfiguration(GuideSourceId sourceId) => Adapter(Kind);

        public bool SupportsPhysicalAdapter(string physicalAdapterKind) =>
            string.Equals(physicalAdapterKind, "hdhomerun", StringComparison.Ordinal);

        public IReadOnlyList<string> Validate(AdapterConfiguration configuration) => [];

        public ValueTask<IGuideSource> CreateAsync(
            GuideSourceId sourceId,
            AdapterConfiguration configuration,
            IReadOnlyList<PhysicalDeviceId> physicalDeviceIds,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
