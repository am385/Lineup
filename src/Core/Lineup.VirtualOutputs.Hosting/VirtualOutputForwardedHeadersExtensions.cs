using Lineup.Common.Networking;
using Lineup.Common.Streaming;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lineup.VirtualOutputs.Hosting;

/// <summary>Configures trusted forwarded request metadata for isolated virtual-output listeners.</summary>
public static class VirtualOutputForwardedHeadersExtensions
{
    /// <summary>Applies each listener binding's trusted proxy policy before downstream request processing.</summary>
    /// <param name="app">Worker request pipeline.</param>
    /// <param name="hosting">Virtual-output listener configuration.</param>
    /// <param name="policies">Security policies indexed by key.</param>
    /// <returns>The request pipeline builder.</returns>
    public static IApplicationBuilder UseVirtualOutputForwardedHeaders(
        this IApplicationBuilder app,
        VirtualOutputHostingDefinition hosting,
        IReadOnlyDictionary<string, VirtualOutputSecurityPolicyDefinition> policies)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(hosting);
        ArgumentNullException.ThrowIfNull(policies);

        var policyByPort = VirtualOutputBindingResolver.CreatePortMap(hosting)
            .ToDictionary(
                pair => pair.Key,
                pair => policies[pair.Value.SecurityPolicyKey]);
        var loggerFactory = app.ApplicationServices.GetRequiredService<ILoggerFactory>();
        return app.Use(next =>
        {
            var middlewareByPolicy = policyByPort.Values
                .DistinctBy(policy => policy.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    policy => policy.Key,
                    policy => new ForwardedHeadersMiddleware(next, loggerFactory, Options.Create(CreateOptions(policy))),
                    StringComparer.OrdinalIgnoreCase);
            return context =>
            {
                var policy = policyByPort.TryGetValue(context.Connection.LocalPort, out var configuredPolicy)
                    ? configuredPolicy
                    : throw new InvalidOperationException($"No virtual-output security policy owns local port {context.Connection.LocalPort}.");
                return middlewareByPolicy[policy.Key].Invoke(context);
            };
        });
    }

    private static ForwardedHeadersOptions CreateOptions(VirtualOutputSecurityPolicyDefinition policy)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = policy.AllowForwardedHeaders
                ? ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
                : ForwardedHeaders.None,
            ForwardLimit = 1
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        var trustedProxies = TrustedProxySet.Parse(policy.TrustedProxies, $"Virtual output security policy '{policy.Name}' trusted proxies");
        foreach (var address in trustedProxies.Addresses)
        {
            options.KnownProxies.Add(address);
        }
        foreach (var network in trustedProxies.Networks)
        {
            options.KnownIPNetworks.Add(network);
        }
        return options;
    }
}
