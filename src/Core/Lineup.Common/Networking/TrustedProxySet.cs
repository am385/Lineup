using System.Net;

namespace Lineup.Common.Networking;

/// <summary>Represents validated trusted proxy addresses and networks.</summary>
public sealed class TrustedProxySet
{
    private TrustedProxySet(IReadOnlyList<IPAddress> addresses, IReadOnlyList<IPNetwork> networks, IReadOnlyList<string> values)
    {
        Addresses = addresses;
        Networks = networks;
        Values = values;
    }

    /// <summary>Gets the trusted individual IP addresses.</summary>
    public IReadOnlyList<IPAddress> Addresses { get; }

    /// <summary>Gets the trusted IP networks.</summary>
    public IReadOnlyList<IPNetwork> Networks { get; }

    /// <summary>Gets normalized persisted values.</summary>
    public IReadOnlyList<string> Values { get; }

    /// <summary>Parses trusted proxy addresses and CIDR networks.</summary>
    /// <param name="values">Values containing individual addresses or CIDR networks.</param>
    /// <param name="settingName">Setting name included in validation failures.</param>
    /// <returns>The validated trusted proxy set.</returns>
    public static TrustedProxySet Parse(IEnumerable<string> values, string settingName)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingName);

        var addresses = new List<IPAddress>();
        var networks = new List<IPNetwork>();
        var normalizedValues = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values.SelectMany(SplitValues))
        {
            if (value.Contains('/', StringComparison.Ordinal))
            {
                if (!IPNetwork.TryParse(value, out var network))
                {
                    throw InvalidValue(settingName, value);
                }

                var normalized = $"{network.BaseAddress}/{network.PrefixLength}";
                if (seen.Add(normalized))
                {
                    networks.Add(network);
                    normalizedValues.Add(normalized);
                }
            }
            else
            {
                if (!IPAddress.TryParse(value, out var address))
                {
                    throw InvalidValue(settingName, value);
                }

                var normalized = address.ToString();
                if (seen.Add(normalized))
                {
                    addresses.Add(address);
                    normalizedValues.Add(normalized);
                }
            }
        }

        return new TrustedProxySet(addresses, networks, normalizedValues);
    }

    private static IEnumerable<string> SplitValues(string value) =>
        value.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static InvalidOperationException InvalidValue(string settingName, string value) =>
        new($"{settingName} contains invalid trusted proxy '{value}'; expected an IP address or CIDR network.");
}
