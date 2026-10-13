using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Lineup.Common.Streaming;
using Lineup.SiliconDust.HDHomeRun.VirtualTuner;
using Lineup.VirtualOutputs.Hosting;
using Lineup.VirtualOutputs.Protocol;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var bootstrap = await VirtualOutputWorkerBootstrapReader.ReadAsync(cancellationToken: CancellationToken.None);
if (!string.Equals(bootstrap.VirtualDevice.Adapter.Kind, HdHomeRunVirtualTunerDriver.AdapterKind, StringComparison.Ordinal))
{
    throw new InvalidOperationException($"HDHomeRun worker cannot host adapter kind '{bootstrap.VirtualDevice.Adapter.Kind}'.");
}

var configuration = bootstrap.VirtualDevice.Adapter.Configuration.Deserialize<HdHomeRunVirtualTunerConfiguration>(
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidDataException("Virtual HDHomeRun configuration is empty.");
var deviceId = string.IsNullOrWhiteSpace(configuration.DeviceId)
    ? HdHomeRunProxyIdentity.CreateDeviceId(bootstrap.VirtualDevice.Id.Value.ToString("N"))
    : configuration.DeviceId.ToUpperInvariant();
var deviceAuth = HdHomeRunProxyIdentity.CreateDeviceAuth(bootstrap.VirtualDevice.Id.Value.ToString("N"));
var securityPolicies = bootstrap.SecurityPolicies.ToDictionary(policy => policy.Key, StringComparer.OrdinalIgnoreCase);
var certificates = bootstrap.CertificateMaterials.ToDictionary(material => material.Reference, StringComparer.Ordinal);
var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
    options.UseUtcTimestamp = true;
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var policy = ResolveSecurityPolicy(context.Connection.LocalPort, bootstrap.VirtualDevice.Hosting!, securityPolicies);
        var client = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return policy.RequestsPerMinutePerClient > 0
            ? RateLimitPartition.GetFixedWindowLimiter(
                $"{context.Connection.LocalPort}:{client}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = policy.RequestsPerMinutePerClient,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                })
            : RateLimitPartition.GetNoLimiter($"{context.Connection.LocalPort}:{client}");
    });
});
builder.WebHost.ConfigureKestrel(options => ConfigureListeners(options, bootstrap, securityPolicies, certificates));

var app = builder.Build();
app.Use((context, next) =>
{
    ApplyForwardedHeaders(
        context,
        ResolveSecurityPolicy(context.Connection.LocalPort, bootstrap.VirtualDevice.Hosting!, securityPolicies));
    return next(context);
});
app.UseRateLimiter();
var activeRequests = 0;
app.Use(async (context, next) =>
{
    Interlocked.Increment(ref activeRequests);
    try
    {
        var policy = ResolveSecurityPolicy(context.Connection.LocalPort, bootstrap.VirtualDevice.Hosting!, securityPolicies);
        if (!IsClientAllowed(context.Connection.RemoteIpAddress, policy.AllowedNetworks))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next(context);
    }
    finally
    {
        Interlocked.Decrement(ref activeRequests);
    }
});

var session = new VirtualOutputWorkerSession(bootstrap);
await session.RegisterAsync();
var sessionContext = session.Context!;
var manifest = await session.Broker.GetManifestAsync(
    new VirtualOutputManifestRequest { Session = sessionContext },
    CancellationToken.None);

app.MapGet("/discover.json", () =>
{
    var baseUri = GetPublicBaseUri(bootstrap.VirtualDevice.Hosting!);
    var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    return Results.Ok(HdHomeRunProxyResponseFactory.CreateDiscover(
        manifest.Name,
        deviceId,
        deviceAuth,
        baseUri,
        checked((byte)Math.Clamp(manifest.TunerCount ?? 0, 0, byte.MaxValue)),
        version));
});
app.MapGet("/lineup.json", async (CancellationToken cancellationToken) =>
{
    var channels = await GetChannelsAsync(session, sessionContext, bootstrap.VirtualDevice.Hosting!, cancellationToken);
    return Results.Content(HdHomeRunProxyLineupFormatter.ToJson(channels), "application/json", Encoding.UTF8);
});
app.MapGet("/lineup.xml", async (CancellationToken cancellationToken) =>
{
    var channels = await GetChannelsAsync(session, sessionContext, bootstrap.VirtualDevice.Hosting!, cancellationToken);
    return Results.Content(HdHomeRunProxyLineupFormatter.ToXml(channels), "text/xml", Encoding.UTF8);
});
app.MapGet("/lineup.m3u", async (CancellationToken cancellationToken) =>
{
    var channels = await GetChannelsAsync(session, sessionContext, bootstrap.VirtualDevice.Hosting!, cancellationToken);
    return Results.Content(HdHomeRunProxyLineupFormatter.ToM3u(channels), "text/plain", Encoding.UTF8);
});
app.MapGet("/lineup_status.json", () => Results.Ok(new HdHomeRunProxyLineupStatus()));
app.MapGet("/device.xml", () =>
{
    var baseUri = GetPublicBaseUri(bootstrap.VirtualDevice.Hosting!);
    return Results.Content(HdHomeRunProxyLineupFormatter.ToDeviceXml(baseUri, manifest.Name, deviceId), "application/xml", Encoding.UTF8);
});
app.MapGet("/auto/v{channel}", async (HttpContext context, string channel, string? transcode, CancellationToken cancellationToken) =>
{
    if (!string.IsNullOrWhiteSpace(transcode) && !string.Equals(transcode, "none", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new HdHomeRunWorkerError
        {
            Error = "Hardware transcode query profiles are not supported by configured topology streams."
        });
    }
    try
    {
        await using var output = await session.Broker.OpenStreamAsync(new VirtualOutputOpenStreamRequest
        {
            Session = sessionContext,
            RequestId = VirtualOutputStreamRequestId.New(),
            ChannelKey = channel,
            Client = new VirtualOutputClientContext
            {
                Address = context.Connection.RemoteIpAddress?.ToString(),
                UserAgent = context.Request.Headers.UserAgent.ToString()
            }
        }, cancellationToken);
        context.Response.ContentType = output.Response.ContentType ?? "video/mp2t";
        context.Response.Headers.CacheControl = "no-cache, no-store";
        await output.Stream.CopyToAsync(context.Response.Body, cancellationToken);
        return Results.Empty;
    }
    catch (VirtualOutputClientException exception)
    {
        return Results.Json(
            new HdHomeRunWorkerError { Error = exception.Message },
            statusCode: GetFailureStatusCode(exception.FailureCode));
    }
});

await app.StartAsync();
await session.Broker.ReportHealthAsync(new VirtualOutputWorkerHealthReport
{
    Session = sessionContext,
    Ready = true,
    Bindings = bootstrap.VirtualDevice.Hosting!.Bindings.Select(binding => binding.PublicBaseUri).ToArray()
});
using var leaseCancellation = new CancellationTokenSource();
var leaseLoop = session.RunLeaseLoopAsync(
    () => (true, Volatile.Read(ref activeRequests)),
    app.Lifetime.StopApplication,
    leaseCancellation.Token);
await app.WaitForShutdownAsync();
leaseCancellation.Cancel();
try
{
    await leaseLoop;
}
catch (OperationCanceledException) when (leaseCancellation.IsCancellationRequested)
{
}
await app.DisposeAsync();

static async Task<IReadOnlyList<HdHomeRunProxyChannel>> GetChannelsAsync(
    VirtualOutputWorkerSession session,
    VirtualOutputSessionContext sessionContext,
    VirtualOutputHostingDefinition hosting,
    CancellationToken cancellationToken)
{
    var lineup = await session.Broker.GetLineupAsync(
        new VirtualOutputLineupRequest { Session = sessionContext },
        cancellationToken);
    var baseUri = GetPublicBaseUri(hosting);
    return lineup.Channels
        .OrderBy(channel => channel.Number, StringComparer.OrdinalIgnoreCase)
        .Select(channel => HdHomeRunProxyChannel.Create(channel, baseUri))
        .ToArray();
}

static Uri GetPublicBaseUri(VirtualOutputHostingDefinition hosting)
{
    var value = hosting.Bindings
        .Select(binding => binding.PublicBaseUri)
        .FirstOrDefault(uri => !string.IsNullOrWhiteSpace(uri))
        ?? throw new InvalidOperationException("The HDHomeRun output requires a public base URI.");
    return new Uri(value.EndsWith('/') ? value : $"{value}/", UriKind.Absolute);
}

static void ConfigureListeners(
    KestrelServerOptions options,
    VirtualOutputWorkerBootstrap bootstrap,
    IReadOnlyDictionary<string, VirtualOutputSecurityPolicyDefinition> policies,
    IReadOnlyDictionary<string, VirtualOutputCertificateMaterial> certificates)
{
    var connectionLimits = bootstrap.VirtualDevice.Hosting!.Bindings
        .Select(binding => policies[binding.SecurityPolicyKey].MaximumConnections)
        .Where(limit => limit > 0)
        .ToArray();
    if (connectionLimits.Length > 0)
    {
        options.Limits.MaxConcurrentConnections = connectionLimits.Min();
    }
    foreach (var binding in bootstrap.VirtualDevice.Hosting.Bindings)
    {
        var policy = policies[binding.SecurityPolicyKey];
        foreach (var address in binding.Addresses)
        {
            void Configure(ListenOptions listenOptions)
            {
                if (binding.Transport != VirtualOutputTransport.Https)
                {
                    return;
                }
                var certificatePath = policy.CertificateReference
                    ?? throw new InvalidOperationException($"HTTPS policy '{policy.Key}' does not reference a certificate.");
                var material = certificates[certificatePath];
                var certificate = X509CertificateLoader.LoadPkcs12(material.Pkcs12, material.Password);
                listenOptions.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateMode = policy.ClientCertificateMode switch
                    {
                        VirtualOutputClientCertificateMode.Allow => ClientCertificateMode.AllowCertificate,
                        VirtualOutputClientCertificateMode.Require => ClientCertificateMode.RequireCertificate,
                        _ => ClientCertificateMode.NoCertificate
                    }
                });
            }
            if (address == "*" || address is "0.0.0.0" or "::")
            {
                options.ListenAnyIP(binding.Port, Configure);
            }
            else if (string.Equals(address, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                options.ListenLocalhost(binding.Port, Configure);
            }
            else if (IPAddress.TryParse(address, out var ipAddress))
            {
                options.Listen(ipAddress, binding.Port, Configure);
            }
            else
            {
                throw new InvalidOperationException($"Unsupported output binding address '{address}'.");
            }
        }
    }
}

static VirtualOutputSecurityPolicyDefinition ResolveSecurityPolicy(
    int localPort,
    VirtualOutputHostingDefinition hosting,
    IReadOnlyDictionary<string, VirtualOutputSecurityPolicyDefinition> policies)
{
    var binding = hosting.Bindings.SingleOrDefault(candidate => candidate.Port == localPort)
        ?? throw new InvalidOperationException($"No output binding owns local port {localPort}.");
    return policies[binding.SecurityPolicyKey];
}

static bool IsClientAllowed(IPAddress? address, IReadOnlyList<string> allowedNetworks)
{
    if (allowedNetworks.Count == 0)
    {
        return true;
    }
    return address is not null &&
        allowedNetworks.Any(network => IPNetwork.TryParse(network, out var parsed) && parsed.Contains(address));
}

static void ApplyForwardedHeaders(HttpContext context, VirtualOutputSecurityPolicyDefinition policy)
{
    if (!policy.AllowForwardedHeaders ||
        !IsAddressAllowed(context.Connection.RemoteIpAddress, policy.TrustedProxies))
    {
        return;
    }
    var forwardedFor = context.Request.Headers["X-Forwarded-For"].ToString()
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault();
    if (IPAddress.TryParse(forwardedFor, out var clientAddress))
    {
        context.Connection.RemoteIpAddress = clientAddress;
    }
    var forwardedProto = context.Request.Headers["X-Forwarded-Proto"].ToString()
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault();
    if (forwardedProto is "http" or "https")
    {
        context.Request.Scheme = forwardedProto;
    }
}

static bool IsAddressAllowed(IPAddress? address, IReadOnlyList<string> addresses)
{
    if (address is null)
    {
        return false;
    }
    return addresses.Any(value =>
        (IPAddress.TryParse(value, out var exact) && exact.Equals(address)) ||
        (IPNetwork.TryParse(value, out var network) && network.Contains(address)));
}

static int GetFailureStatusCode(VirtualOutputFailureCode failureCode) =>
    failureCode switch
    {
        VirtualOutputFailureCode.ChannelNotFound => StatusCodes.Status404NotFound,
        VirtualOutputFailureCode.ResourceLimit => StatusCodes.Status429TooManyRequests,
        VirtualOutputFailureCode.CapabilityDenied => StatusCodes.Status403Forbidden,
        VirtualOutputFailureCode.InvalidRequest => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status503ServiceUnavailable
    };

/// <summary>Describes an HTTP error returned by the isolated HDHomeRun worker.</summary>
public sealed record HdHomeRunWorkerError
{
    /// <summary>Gets the safe client-facing error.</summary>
    public required string Error { get; init; }
}
