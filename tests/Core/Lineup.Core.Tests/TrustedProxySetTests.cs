using Lineup.Common.Networking;
using Xunit;

namespace Lineup.Core.Tests;

/// <summary>Verifies shared trusted proxy parsing and normalization.</summary>
public sealed class TrustedProxySetTests
{
    /// <summary>Verifies addresses and networks are normalized and deduplicated.</summary>
    [Fact]
    public void Parse_MixedValues_NormalizesAndDeduplicates()
    {
        // Arrange
        var values = new[] { "127.0.0.1, 172.18.0.0/16", "127.0.0.1", "2001:db8::1" };

        // Act
        var parsed = TrustedProxySet.Parse(values, "Known proxies");

        // Assert
        Assert.Equal(["127.0.0.1", "172.18.0.0/16", "2001:db8::1"], parsed.Values);
        Assert.Equal(2, parsed.Addresses.Count);
        Assert.Single(parsed.Networks);
    }

    /// <summary>Verifies hostnames are rejected because proxy trust requires explicit network identities.</summary>
    [Fact]
    public void Parse_Hostname_Throws()
    {
        // Arrange
        var values = new[] { "proxy.internal" };

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => TrustedProxySet.Parse(values, "Known proxies"));

        // Assert
        Assert.Contains("proxy.internal", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies allowed networks require CIDR notation and support address membership checks.</summary>
    [Fact]
    public void IpNetworkSet_ParseValidNetworks_NormalizesAndMatchesAddresses()
    {
        // Arrange
        var values = new[] { "192.0.2.0/24, 2001:db8::/32", "192.0.2.0/24" };

        // Act
        var parsed = IpNetworkSet.Parse(values, "Allowed networks");

        // Assert
        Assert.Equal(["192.0.2.0/24", "2001:db8::/32"], parsed.Values);
        Assert.True(parsed.Contains(System.Net.IPAddress.Parse("192.0.2.25")));
        Assert.False(parsed.Contains(System.Net.IPAddress.Parse("198.51.100.1")));
    }
}
