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

    /// <summary>Verifies mixed identifiers have numeric-first ordering and satisfy transitivity regardless of input arrangement.</summary>
    [Fact]
    public void Compare_MixedIdentifiers_IsTransitiveAndSortsNumericFirst()
    {
        // Arrange
        string?[] channels = ["2", "10", "1a", "2.2", "02", "Cable", "2147483648", null, ""];
        string?[] expected = ["02", "2", "2.2", "10", "1a", "2147483648", "Cable", null, ""];
        var comparer = ChannelNumberComparer.Instance;

        // Act
        var orders = Enumerable.Range(0, channels.Length)
            .Select(offset => channels.Skip(offset).Concat(channels.Take(offset)).Reverse().OrderBy(channel => channel, comparer).ToArray()).ToArray();

        // Assert
        Assert.All(orders, order => Assert.Equal(expected, order));
        foreach (var left in channels)
        {
            foreach (var middle in channels)
            {
                Assert.Equal(-Math.Sign(comparer.Compare(middle, left)), Math.Sign(comparer.Compare(left, middle)));
                foreach (var right in channels)
                {
                    if (comparer.Compare(left, middle) <= 0 && comparer.Compare(middle, right) <= 0)
                    {
                        Assert.True(comparer.Compare(left, right) <= 0, $"Ordering is not transitive for '{left}', '{middle}', '{right}'.");
                    }
                }
            }
        }
    }
}
