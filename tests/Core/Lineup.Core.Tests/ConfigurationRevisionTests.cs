using System.Text.Json;
using Lineup.Core.Settings;
using Xunit;

namespace Lineup.Core.Tests;

/// <summary>Verifies editor and topology content revision semantics.</summary>
public sealed class ConfigurationRevisionTests
{
    /// <summary>Verifies operational schedule writes do not invalidate an editor draft.</summary>
    [Fact]
    public void ForSettings_OperationalTimestampsChange_PreservesRevision()
    {
        // Arrange
        var settings = new LineupSettings();
        var original = ConfigurationRevision.ForSettings(settings);

        // Act
        settings.NextAutoFetchTime = DateTime.UtcNow.AddDays(1);
        settings.LastAutoFetchTime = DateTime.UtcNow;

        // Assert
        Assert.Equal(original, ConfigurationRevision.ForSettings(settings));
    }

    /// <summary>Verifies meaningful scheduling policy edits still invalidate stale drafts.</summary>
    [Fact]
    public void ForSettings_SchedulingPolicyChanges_ChangesRevision()
    {
        // Arrange
        var settings = new LineupSettings();
        var original = ConfigurationRevision.ForSettings(settings);

        // Act
        settings.AutoFetchInterval = TimeSpan.FromHours(24);

        // Assert
        Assert.NotEqual(original, ConfigurationRevision.ForSettings(settings));
    }

    /// <summary>Verifies JSON object order is immaterial while adapter-owned arrays retain their order.</summary>
    [Fact]
    public void Create_JsonObjectsReordered_PreservesRevisionWithoutSortingArrays()
    {
        // Arrange
        var first = JsonSerializer.Deserialize<JsonElement>("""{"b":2,"a":[1,2]}""");
        var reordered = JsonSerializer.Deserialize<JsonElement>("""{"a":[1,2],"b":2}""");
        var changed = JsonSerializer.Deserialize<JsonElement>("""{"a":[2,1],"b":2}""");

        // Act
        var revision = ConfigurationRevision.Create(first);

        // Assert
        Assert.Equal(revision, ConfigurationRevision.Create(reordered));
        Assert.NotEqual(revision, ConfigurationRevision.Create(changed));
    }

    /// <summary>Verifies subscription identifier sets compare by content rather than insertion order.</summary>
    [Fact]
    public void Create_IdentifierSetReordered_PreservesRevision()
    {
        // Arrange
        var first = PhysicalDeviceId.New();
        var second = PhysicalDeviceId.New();
        var subscription = new PhysicalDeviceSubscription { Mode = PhysicalDeviceSubscriptionMode.Selected, SelectedDeviceIds = [first, second] };
        var reordered = subscription with { SelectedDeviceIds = [second, first] };

        // Act
        var revision = ConfigurationRevision.Create(subscription);

        // Assert
        Assert.Equal(revision, ConfigurationRevision.Create(reordered));
        Assert.NotEqual(revision, ConfigurationRevision.Create(subscription with { SelectedDeviceIds = [first] }));
    }
}
