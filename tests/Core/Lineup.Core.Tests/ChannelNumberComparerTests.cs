using Xunit;

namespace Lineup.Core.Tests;

/// <summary>
/// Verifies numeric channel ordering.
/// </summary>
public class ChannelNumberComparerTests
{
    /// <summary>
    /// Verifies major and minor channel components are compared numerically.
    /// </summary>
    [Fact]
    public void OrderBy_SortsNumericChannelComponents()
    {
        // Arrange
        string[] channels = ["104.1", "10.1", "2.10", "2.2", "7.1", "2.1"];

        // Act
        var sorted = channels.OrderBy(channel => channel, ChannelNumberComparer.Instance).ToArray();

        // Assert
        Assert.Equal(["2.1", "2.2", "2.10", "7.1", "10.1", "104.1"], sorted);
    }

    /// <summary>
    /// Verifies nonnumeric values remain deterministic and empty values sort last.
    /// </summary>
    [Fact]
    public void OrderBy_NonnumericAndEmptyValues_UsesStableFallback()
    {
        // Arrange
        string?[] channels = ["Cable B", null, "Cable A", ""];

        // Act
        var sorted = channels.OrderBy(channel => channel, ChannelNumberComparer.Instance).ToArray();

        // Assert
        Assert.Equal(["Cable A", "Cable B", null, ""], sorted);
    }
}
