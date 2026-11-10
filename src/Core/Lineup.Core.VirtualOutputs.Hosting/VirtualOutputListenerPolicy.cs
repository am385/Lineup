using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Lineup.Core.Common.Networking;
using Lineup.Core.Common.Streaming;
using Lineup.Core.VirtualOutputs.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;

namespace Lineup.Core.VirtualOutputs.Hosting;

/// <summary>Applies the shared binding, TLS, proxy, network and client-rate policy for dedicated listeners.</summary>
public sealed class VirtualOutputListenerPolicy
{
    private readonly VirtualOutputWorkerBootstrap _bootstrap;
    private readonly IReadOnlyDictionary<int, VirtualOutputBindingDefinition> _bindings;
    private readonly IReadOnlyDictionary<string, IpNetworkSet> _networks;
    private readonly IReadOnlyDictionary<string, VirtualOutputCertificateMaterial> _certificates;

    /// <summary>Validates shared listener policy before any public listener starts.</summary>
    public VirtualOutputListenerPolicy(VirtualOutputWorkerBootstrap bootstrap)
    {
        _bootstrap = bootstrap;
        var hosting = bootstrap.VirtualDevice.Hosting ?? throw new InvalidDataException("A dedicated worker requires hosting configuration.");
        _bindings = VirtualOutputBindingResolver.CreatePortMap(hosting);
        Policies = bootstrap.SecurityPolicies.ToDictionary(policy => policy.Key, StringComparer.OrdinalIgnoreCase);
        _networks = Policies.ToDictionary(pair => pair.Key,
            pair => IpNetworkSet.Parse(pair.Value.AllowedNetworks, $"Virtual output security policy '{pair.Value.Name}' allowed networks"), StringComparer.OrdinalIgnoreCase);
        _certificates = bootstrap.CertificateMaterials.ToDictionary(material => material.Reference, StringComparer.Ordinal);
    }

    /// <summary>Gets the per-binding policies used by trusted-forwarded-header processing.</summary>
    public IReadOnlyDictionary<string, VirtualOutputSecurityPolicyDefinition> Policies { get; }

    /// <summary>Registers the existing fixed-window policy per local port and resolved client.</summary>
    public void AddServices(IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var policy = Resolve(context.Connection.LocalPort);
                var key = $"{context.Connection.LocalPort}:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
                return policy.RequestsPerMinutePerClient > 0
                    ? RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = policy.RequestsPerMinutePerClient,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    })
                    : RateLimitPartition.GetNoLimiter(key);
            });
        });
    }

    /// <summary>Applies only explicitly trusted forwarded headers before rate and network checks.</summary>
    public void UseForwardedHeaders(IApplicationBuilder app) =>
        app.UseVirtualOutputForwardedHeaders(_bootstrap.VirtualDevice.Hosting!, Policies);

    /// <summary>Checks the effective client address against the binding's configured networks.</summary>
    public bool Allows(HttpContext context) => _networks[Resolve(context.Connection.LocalPort).Key].Contains(context.Connection.RemoteIpAddress);

    /// <summary>Binds configured addresses and TLS policy without a server-wide connection cap.</summary>
    public void Configure(KestrelServerOptions options)
    {
        foreach (var binding in _bootstrap.VirtualDevice.Hosting!.Bindings)
        {
            var policy = Policies[binding.SecurityPolicyKey];
            foreach (var address in binding.Addresses)
            {
                void ConfigureEndpoint(ListenOptions listen)
                {
                    if (binding.Transport != VirtualOutputTransport.Https)
                    {
                        return;
                    }
                    var reference = policy.CertificateReference ?? throw new InvalidOperationException($"HTTPS policy '{policy.Key}' does not reference a certificate.");
                    var material = _certificates[reference];
                    listen.UseHttps(new HttpsConnectionAdapterOptions
                    {
                        ServerCertificate = X509CertificateLoader.LoadPkcs12(material.Pkcs12, material.Password),
                        ClientCertificateMode = policy.ClientCertificateMode switch
                        {
                            VirtualOutputClientCertificateMode.Allow => ClientCertificateMode.AllowCertificate,
                            VirtualOutputClientCertificateMode.Require => ClientCertificateMode.RequireCertificate,
                            _ => ClientCertificateMode.NoCertificate
                        }
                    });
                }
                if (address is "*" or "0.0.0.0" or "::")
                {
                    options.ListenAnyIP(binding.Port, ConfigureEndpoint);
                }
                else if (string.Equals(address, "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    options.ListenLocalhost(binding.Port, ConfigureEndpoint);
                }
                else if (IPAddress.TryParse(address, out var ipAddress))
                {
                    options.Listen(ipAddress, binding.Port, ConfigureEndpoint);
                }
                else
                {
                    throw new InvalidOperationException($"Unsupported output binding address '{address}'.");
                }
            }
        }
    }

    private VirtualOutputSecurityPolicyDefinition Resolve(int localPort) => Policies[_bindings.TryGetValue(localPort, out var binding)
        ? binding.SecurityPolicyKey
        : throw new InvalidOperationException($"No virtual-output binding owns local port {localPort}.")];
}
