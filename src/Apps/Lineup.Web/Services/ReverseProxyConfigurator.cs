using Lineup.Common.Networking;
using Lineup.Core.Settings;
using Microsoft.AspNetCore.HttpOverrides;

namespace Lineup.Web.Services;

/// <summary>Holds validated management-listener reverse-proxy request metadata configuration.</summary>
internal sealed class ReverseProxyConfiguration
{
    /// <summary>Initializes validated reverse-proxy configuration.</summary>
    internal ReverseProxyConfiguration(bool enabled, ForwardedHeadersOptions options, int configuredProxyCount)
    {
        Enabled = enabled;
        Options = options;
        ConfiguredProxyCount = configuredProxyCount;
    }

    /// <summary>Gets whether forwarded request metadata processing is enabled.</summary>
    internal bool Enabled { get; }

    /// <summary>Gets the validated ASP.NET Core forwarded-header options.</summary>
    internal ForwardedHeadersOptions Options { get; }

    /// <summary>Gets the number of explicitly configured trusted proxy addresses.</summary>
    internal int ConfiguredProxyCount { get; }

}

/// <summary>Loads strict persisted trusted reverse-proxy configuration for the management request pipeline.</summary>
internal static class ReverseProxyConfigurator
{
    /// <summary>Loads and validates reverse-proxy configuration.</summary>
    internal static ReverseProxyConfiguration Load(LineupSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var trustedProxies = TrustedProxySet.Parse(settings.KnownProxies, "Known proxies");
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = trustedProxies.Values.Count == 0
                ? ForwardedHeaders.None
                : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var address in trustedProxies.Addresses)
        {
            options.KnownProxies.Add(address);
        }
        foreach (var network in trustedProxies.Networks)
        {
            options.KnownIPNetworks.Add(network);
        }
        return new ReverseProxyConfiguration(trustedProxies.Values.Count > 0, options, trustedProxies.Values.Count);
    }
}
