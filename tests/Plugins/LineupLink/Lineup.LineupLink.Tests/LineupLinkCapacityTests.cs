using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.LineupLink.Protocol;
using Lineup.LineupLink.Virtual;
using Xunit;

namespace Lineup.LineupLink.Tests;

/// <summary>Verifies advertised capacity includes only compatible subscribed physical devices.</summary>
public sealed class LineupLinkCapacityTests
{
    /// <summary>Verifies recursive inputs cannot inflate a finite export or make it unbounded.</summary>
    [Theory]
    [InlineData(PhysicalDeviceSubscriptionMode.All, "lineup-link", 12)]
    [InlineData(PhysicalDeviceSubscriptionMode.All, "lineup-link", null)]
    [InlineData(PhysicalDeviceSubscriptionMode.All, "LINEUP-LINK", 12)]
    [InlineData(PhysicalDeviceSubscriptionMode.All, "LINEUP-LINK", null)]
    [InlineData(PhysicalDeviceSubscriptionMode.Selected, "lineup-link", 12)]
    [InlineData(PhysicalDeviceSubscriptionMode.Selected, "lineup-link", null)]
    [InlineData(PhysicalDeviceSubscriptionMode.Selected, "LINEUP-LINK", 12)]
    [InlineData(PhysicalDeviceSubscriptionMode.Selected, "LINEUP-LINK", null)]
    public void GetCapacity_MixedAdapters_ExcludesIncompatibleCapacity(PhysicalDeviceSubscriptionMode mode, string incompatibleKind, int? incompatibleCapacity)
    {
        // Arrange
        var driver = new LineupLinkVirtualTunerDriver();
        var first = Snapshot("hdhomerun", 200);
        var second = Snapshot("another-provider", 100);
        var incompatible = Snapshot(incompatibleKind, incompatibleCapacity);
        var definition = Virtual(driver, mode, first, second, incompatible);
        IVirtualTunerCapacityProvider provider = driver;

        // Act
        var capacity = provider.GetCapacity(definition, [first, incompatible, second]);

        // Assert
        Assert.True(capacity.IsFinite);
        Assert.Equal(300, capacity.TunerCount);
    }

    /// <summary>Verifies incompatible-only subscriptions advertise zero, not finite or unbounded remote capacity.</summary>
    [Theory]
    [InlineData(PhysicalDeviceSubscriptionMode.All, 12)]
    [InlineData(PhysicalDeviceSubscriptionMode.All, null)]
    [InlineData(PhysicalDeviceSubscriptionMode.Selected, 12)]
    [InlineData(PhysicalDeviceSubscriptionMode.Selected, null)]
    public void GetCapacity_OnlyIncompatibleAdapters_ReturnsZero(PhysicalDeviceSubscriptionMode mode, int? incompatibleCapacity)
    {
        // Arrange
        var driver = new LineupLinkVirtualTunerDriver();
        var incompatible = Snapshot(LineupLinkProtocol.AdapterKind, incompatibleCapacity);
        var definition = Virtual(driver, mode, incompatible);

        // Act
        var capacity = driver.GetCapacity(definition, [incompatible]);

        // Assert
        Assert.True(capacity.IsFinite);
        Assert.Equal(0, capacity.TunerCount);
    }

    /// <summary>Verifies filtering occurs before checked aggregation of incompatible capacities.</summary>
    [Theory]
    [InlineData(PhysicalDeviceSubscriptionMode.All)]
    [InlineData(PhysicalDeviceSubscriptionMode.Selected)]
    public void GetCapacity_IncompatibleCapacityWouldOverflow_ExcludesItBeforeSumming(PhysicalDeviceSubscriptionMode mode)
    {
        // Arrange
        var driver = new LineupLinkVirtualTunerDriver();
        var compatible = Snapshot("hdhomerun", 300);
        var incompatible = Snapshot(LineupLinkProtocol.AdapterKind, int.MaxValue);
        var definition = Virtual(driver, mode, compatible, incompatible);

        // Act
        var capacity = driver.GetCapacity(definition, [compatible, incompatible]);

        // Assert
        Assert.Equal(300, capacity.TunerCount);
    }

    /// <summary>Verifies Selected mode excludes unselected compatible sources, including unbounded ones.</summary>
    [Theory]
    [InlineData(12)]
    [InlineData(null)]
    public void GetCapacity_SelectedMode_ExcludesUnselectedCapacity(int? unselectedCapacity)
    {
        // Arrange
        var driver = new LineupLinkVirtualTunerDriver();
        var selected = Snapshot("hdhomerun", 4);
        var absent = Snapshot("another-provider", 50);
        var unselected = Snapshot("another-provider", unselectedCapacity);
        var definition = Virtual(driver, PhysicalDeviceSubscriptionMode.Selected, selected, absent);

        // Act
        var capacity = driver.GetCapacity(definition, [selected, unselected]);

        // Assert
        Assert.Equal(4, capacity.TunerCount);
    }

    /// <summary>Verifies compatible unbounded sources retain their capacity semantics.</summary>
    [Theory]
    [InlineData(PhysicalDeviceSubscriptionMode.All, "hdhomerun")]
    [InlineData(PhysicalDeviceSubscriptionMode.All, "another-provider")]
    [InlineData(PhysicalDeviceSubscriptionMode.Selected, "hdhomerun")]
    [InlineData(PhysicalDeviceSubscriptionMode.Selected, "another-provider")]
    public void GetCapacity_CompatibleUnboundedSource_ReturnsUnbounded(PhysicalDeviceSubscriptionMode mode, string kind)
    {
        // Arrange
        var driver = new LineupLinkVirtualTunerDriver();
        var finite = Snapshot("hdhomerun", 4);
        var unbounded = Snapshot(kind, null);
        var definition = Virtual(driver, mode, finite, unbounded);

        // Act
        var capacity = driver.GetCapacity(definition, [finite, unbounded]);

        // Assert
        Assert.False(capacity.IsFinite);
        Assert.Null(capacity.TunerCount);
    }

    /// <summary>Verifies absent active devices produce finite zero capacity in either subscription mode.</summary>
    [Theory]
    [InlineData(PhysicalDeviceSubscriptionMode.All)]
    [InlineData(PhysicalDeviceSubscriptionMode.Selected)]
    public void GetCapacity_NoActiveDevices_ReturnsZero(PhysicalDeviceSubscriptionMode mode)
    {
        // Arrange
        var driver = new LineupLinkVirtualTunerDriver();
        var definition = Virtual(driver, mode);

        // Act
        var capacity = driver.GetCapacity(definition, []);

        // Assert
        Assert.Equal(0, capacity.TunerCount);
    }

    /// <summary>Verifies an empty explicit subscription does not inherit active compatible capacity.</summary>
    [Fact]
    public void GetCapacity_EmptySelectedSubscription_ReturnsZero()
    {
        // Arrange
        var driver = new LineupLinkVirtualTunerDriver();
        var definition = Virtual(driver, PhysicalDeviceSubscriptionMode.Selected);
        var active = Snapshot("hdhomerun", null);

        // Act
        var capacity = driver.GetCapacity(definition, [active]);

        // Assert
        Assert.Equal(0, capacity.TunerCount);
    }

    /// <summary>Verifies All mode ignores saved explicit selections.</summary>
    [Fact]
    public void GetCapacity_AllMode_IgnoresSelectedIds()
    {
        // Arrange
        var driver = new LineupLinkVirtualTunerDriver();
        var first = Snapshot("hdhomerun", 4);
        var second = Snapshot("another-provider", 2);
        var definition = Virtual(driver, PhysicalDeviceSubscriptionMode.All, first);

        // Act
        var capacity = driver.GetCapacity(definition, [first, second]);

        // Assert
        Assert.Equal(6, capacity.TunerCount);
    }

    /// <summary>Verifies overflow of compatible finite capacity still fails explicitly.</summary>
    [Theory]
    [InlineData(PhysicalDeviceSubscriptionMode.All)]
    [InlineData(PhysicalDeviceSubscriptionMode.Selected)]
    public void GetCapacity_CompatibleCapacityOverflows_Throws(PhysicalDeviceSubscriptionMode mode)
    {
        // Arrange
        var driver = new LineupLinkVirtualTunerDriver();
        var first = Snapshot("hdhomerun", int.MaxValue);
        var second = Snapshot("another-provider", 1);
        var definition = Virtual(driver, mode, first, second);

        // Act
        var exception = Assert.Throws<OverflowException>(() => driver.GetCapacity(definition, [first, second]));

        // Assert
        Assert.NotNull(exception);
    }

    /// <summary>Verifies null inputs retain explicit argument validation.</summary>
    [Theory]
    [InlineData(true, "virtualDevice")]
    [InlineData(false, "physicalDevices")]
    public void GetCapacity_NullInput_ThrowsArgumentNull(bool nullDefinition, string expectedParameter)
    {
        // Arrange
        var driver = new LineupLinkVirtualTunerDriver();
        var definition = Virtual(driver, PhysicalDeviceSubscriptionMode.All);

        // Act
        var exception = nullDefinition
            ? Assert.Throws<ArgumentNullException>(() => driver.GetCapacity(null!, []))
            : Assert.Throws<ArgumentNullException>(() => driver.GetCapacity(definition, null!));

        // Assert
        Assert.Equal(expectedParameter, exception.ParamName);
    }

    /// <summary>Verifies malformed active snapshots still fail explicitly rather than being hidden by filtering.</summary>
    [Fact]
    public void GetCapacity_NullSnapshot_ThrowsArgumentNull()
    {
        // Arrange
        var driver = new LineupLinkVirtualTunerDriver();
        var definition = Virtual(driver, PhysicalDeviceSubscriptionMode.All);

        // Act
        var exception = Assert.Throws<ArgumentNullException>(() => driver.GetCapacity(definition, [null!]));

        // Assert
        Assert.NotNull(exception);
    }

    private static PhysicalDeviceSnapshot Snapshot(string kind, int? capacity) =>
        new()
        {
            Id = PhysicalDeviceId.New(),
            Kind = kind,
            Name = kind,
            Capacity = new PhysicalTunerCapacity(capacity)
        };

    private static VirtualDeviceDefinition Virtual(LineupLinkVirtualTunerDriver driver, PhysicalDeviceSubscriptionMode mode, params PhysicalDeviceSnapshot[] selected)
    {
        var id = VirtualDeviceId.New();
        return new VirtualDeviceDefinition
        {
            Id = id,
            Name = "Lineup Link",
            Adapter = driver.CreateDefaultConfiguration(id),
            Subscription = new PhysicalDeviceSubscription
            {
                Mode = mode,
                SelectedDeviceIds = selected.Select(device => device.Id).ToHashSet()
            }
        };
    }
}
