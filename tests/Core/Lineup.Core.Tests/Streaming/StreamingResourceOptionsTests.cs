using Microsoft.Extensions.Configuration;
using Xunit;

namespace Lineup.Core.Tests.Streaming;

/// <summary>Verifies resource budgets are finite by default and invalid operator configuration fails explicitly.</summary>
public sealed class StreamingResourceOptionsTests
{
    /// <summary>Verifies the approved defaults and supported startup-configuration overrides.</summary>
    [Fact]
    public void Load_DefaultsAndOverrides_UsesApprovedBudgets()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["StreamingResources:MaximumPendingStarts"] = "3" }).Build();

        // Act
        var defaults = StreamingResourceOptions.Load(null);
        var overridden = StreamingResourceOptions.Load(configuration);

        // Assert
        Assert.Equal(8, defaults.MaximumPendingStarts);
        Assert.Equal(8, defaults.SubscriberBufferMib);
        Assert.Equal(128, defaults.GraphBufferMib);
        Assert.Equal(5, defaults.MaximumBufferAgeSeconds);
        Assert.Equal(20, defaults.AcquisitionTimeoutSeconds);
        Assert.Equal(3, overridden.MaximumPendingStarts);
    }

    /// <summary>Verifies zero and negative values cannot silently disable resource protection.</summary>
    [Theory]
    [InlineData("MaximumPendingStarts", "0")]
    [InlineData("SubscriberBufferMib", "-1")]
    [InlineData("GraphBufferMib", "0")]
    [InlineData("MaximumBufferAgeSeconds", "0")]
    [InlineData("AcquisitionTimeoutSeconds", "301")]
    [InlineData("MaximumProducers", "-1")]
    [InlineData("MaximumProcessingOperations", "-1")]
    [InlineData("MinimumMediaFreeSpaceMib", "-1")]
    public void Load_InvalidBudget_RejectsConfiguration(string key, string value)
    {
        // Arrange
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [$"StreamingResources:{key}"] = value }).Build();

        // Act
        var error = Record.Exception(() => StreamingResourceOptions.Load(configuration));

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(error);
    }
}
