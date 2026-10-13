using System.Net;

namespace Lineup.Common.Networking;

/// <summary>Represents a validated set of IP networks.</summary>
public sealed class IpNetworkSet
{
    private IpNetworkSet(IReadOnlyList<IPNetwork> networks, IReadOnlyList<string> values)
    {
        Networks = networks;
        Values = values;
    }

    /// <summary>Gets the parsed IP networks.</summary>
    public IReadOnlyList<IPNetwork> Networks { get; }

    /// <summary>Gets normalized CIDR values.</summary>
    public IReadOnlyList<string> Values { get; }

    /// <summary>Parses required CIDR network values.</summary>
    /// <param name="values">Values containing CIDR networks.</param>
    /// <param name="settingName">Setting name included in validation failures.</param>
    /// <returns>The validated network set.</returns>
    public static IpNetworkSet Parse(IEnumerable<string> values, string settingName)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingName);

        var networks = new List<IPNetwork>();
        var normalizedValues = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values.SelectMany(SplitValues))
        {
            if (!value.Contains('/', StringComparison.Ordinal) || !IPNetwork.TryParse(value, out var network))
            {
                throw new InvalidOperationException($"{settingName} contains invalid network '{value}'; expected CIDR notation such as 192.0.2.0/24.");
            }

            var normalized = $"{network.BaseAddress}/{network.PrefixLength}";
            if (seen.Add(normalized))
            {
                networks.Add(network);
                normalizedValues.Add(normalized);
            }
        }
        return new IpNetworkSet(networks, normalizedValues);
    }

    /// <summary>Gets whether an address belongs to at least one configured network.</summary>
    /// <param name="address">Address to test.</param>
    /// <returns><see langword="true"/> when the set is empty or contains the address.</returns>
    public bool Contains(IPAddress? address) =>
        Networks.Count == 0 ||
        address is not null && Networks.Any(network => network.Contains(address));

    private static IEnumerable<string> SplitValues(string value) =>
        value.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
