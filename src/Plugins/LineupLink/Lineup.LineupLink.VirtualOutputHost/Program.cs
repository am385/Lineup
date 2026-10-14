using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.RateLimiting;
using Lineup.Common.Networking;
using Lineup.Common.Streaming;
using Lineup.LineupLink.Protocol;
using Lineup.LineupLink.Virtual;
using Lineup.VirtualOutputs.Hosting;
using Lineup.VirtualOutputs.Protocol;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

var bootstrap = await VirtualOutputWorkerBootstrapReader.ReadAsync(cancellationToken: CancellationToken.None);
if (!string.Equals(bootstrap.VirtualDevice.Adapter.Kind, LineupLinkProtocol.AdapterKind, StringComparison.Ordinal))
{
    throw new InvalidOperationException($"Lineup Link worker cannot host adapter kind '{bootstrap.VirtualDevice.Adapter.Kind}'.");
}

var configuration = bootstrap.VirtualDevice.Adapter.Configuration.Deserialize<LineupLinkVirtualConfiguration>(
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidDataException("Lineup Link virtual configuration is empty.");
var hosting = bootstrap.VirtualDevice.Hosting
    ?? throw new InvalidDataException("Lineup Link worker requires dedicated hosting configuration.");
var securityPolicies = bootstrap.SecurityPolicies.ToDictionary(policy => policy.Key, StringComparer.OrdinalIgnoreCase);
var bindingsByPort = VirtualOutputBindingResolver.CreatePortMap(hosting);
var allowedNetworks = securityPolicies.ToDictionary(
    pair => pair.Key,
    pair => IpNetworkSet.Parse(pair.Value.AllowedNetworks, $"Virtual output security policy '{pair.Value.Name}' allowed networks"),
    StringComparer.OrdinalIgnoreCase);
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
        var policy = ResolveSecurityPolicy(context.Connection.LocalPort, bindingsByPort, securityPolicies);
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
app.UseVirtualOutputForwardedHeaders(hosting, securityPolicies);
app.UseRateLimiter();
var activeRequests = 0;
app.Use(async (context, next) =>
{
    Interlocked.Increment(ref activeRequests);
    try
    {
        var policy = ResolveSecurityPolicy(context.Connection.LocalPort, bindingsByPort, securityPolicies);
        if (!allowedNetworks[policy.Key].Contains(context.Connection.RemoteIpAddress))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (configuration.RequireEncryption && !context.Request.IsHttps)
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            context.Response.Headers.Upgrade = "TLS/1.2";
            await context.Response.WriteAsJsonAsync(new LineupLinkError
            {
                Code = "https_required",
                Message = "This Lineup Link virtual device requires HTTPS."
            });
            return;
        }
        var authenticatedGrant = TryAuthorize(context.Request.Headers.Authorization, configuration);
        if (authenticatedGrant is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await context.Response.WriteAsJsonAsync(new LineupLinkError
            {
                Code = "unauthorized",
                Message = "A valid Lineup Link access token is required."
            });
            return;
        }
        context.Items[typeof(LineupLinkAuthenticatedGrant)] = authenticatedGrant;
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
var deviceRoot = LineupLinkProtocol.DevicePath(bootstrap.VirtualDevice.Id.Value);

app.MapGet($"{deviceRoot}/manifest", async (CancellationToken cancellationToken) =>
{
    var manifest = await session.Broker.GetManifestAsync(
        new VirtualOutputManifestRequest { Session = sessionContext },
        cancellationToken);
    return Results.Ok(new LineupLinkManifest
    {
        InstanceId = configuration.InstanceId,
        VirtualDeviceId = manifest.VirtualDeviceId.Value,
        Name = manifest.Name,
        TunerCount = manifest.TunerCount,
        GuideAvailable = manifest.GuideAvailable
    });
});

app.MapGet($"{deviceRoot}/lineup", async (CancellationToken cancellationToken) =>
{
    var lineup = await session.Broker.GetLineupAsync(
        new VirtualOutputLineupRequest { Session = sessionContext },
        cancellationToken);
    return Results.Ok(new LineupLinkLineup
    {
        VirtualDeviceId = bootstrap.VirtualDevice.Id.Value,
        Channels = lineup.Channels
            .OrderBy(channel => channel.Number, StringComparer.OrdinalIgnoreCase)
            .Select(channel => new LineupLinkChannel
            {
                Key = channel.Number,
                Number = channel.Number,
                Name = channel.Name,
                Metadata = channel.Metadata
            })
            .ToArray()
    });
});

app.MapGet($"{deviceRoot}/guide", async (CancellationToken cancellationToken) =>
{
    var guide = await session.Broker.GetGuideAsync(
        new VirtualOutputGuideRequest { Session = sessionContext },
        cancellationToken);
    var snapshot = guide.Snapshot;
    return Results.Ok(new LineupLinkGuide
    {
        VirtualDeviceId = bootstrap.VirtualDevice.Id.Value,
        Channels = snapshot.Channels.Select(channel => new LineupLinkGuideChannel
        {
            Key = channel.Number,
            Number = channel.Number,
            Name = channel.Name,
            Affiliate = channel.Affiliate,
            ImageUrl = channel.ImageUrl,
            IsProtected = channel.IsProtected,
            IsFavorite = channel.IsFavorite,
            SupplementalXml = channel.SupplementalXml
        }).ToArray(),
        Programs = snapshot.Channels.SelectMany(channel => channel.Programs.Select(program => new LineupLinkGuideProgram
        {
            ChannelKey = channel.Number,
            StartUtc = program.StartUtc,
            EndUtc = program.EndUtc,
            Title = program.Title,
            Synopsis = program.Synopsis,
            EpisodeTitle = program.EpisodeTitle,
            ImageUrl = program.ImageUrl,
            PosterUrl = program.PosterUrl,
            EpisodeNumber = program.EpisodeNumber,
            OriginalAirdateUtc = program.OriginalAirdateUtc,
            IsFirstAiring = program.IsFirstAiring,
            SeriesId = program.SeriesId,
            Categories = program.Categories,
            Metadata = program.Metadata,
            SupplementalXml = program.SupplementalXml
        })).ToArray(),
        SupplementalXml = snapshot.SupplementalXml.Count == 0
            ? null
            : string.Join(Environment.NewLine, snapshot.SupplementalXml)
    });
});

app.MapGet($"{deviceRoot}/streams/{{channelKey}}", async (HttpContext context, string channelKey, CancellationToken cancellationToken) =>
{
    try
    {
        var authenticatedGrant = (LineupLinkAuthenticatedGrant)context.Items[typeof(LineupLinkAuthenticatedGrant)]!;
        app.Logger.LogInformation(
            "Opening Lineup Link stream for access grant {AccessGrantId} ({AccessGrantName}) on channel {ChannelKey}",
            authenticatedGrant.Id,
            authenticatedGrant.Name,
            channelKey);
        await using var output = await session.Broker.OpenStreamAsync(new VirtualOutputOpenStreamRequest
        {
            Session = sessionContext,
            RequestId = VirtualOutputStreamRequestId.New(),
            ChannelKey = channelKey,
            Client = new VirtualOutputClientContext
            {
                Address = context.Connection.RemoteIpAddress?.ToString(),
                UserAgent = context.Request.Headers.UserAgent.ToString(),
                AuthenticatedClientId = authenticatedGrant.Id.ToString("D"),
                AuthenticatedClientName = authenticatedGrant.Name
            }
        }, cancellationToken);
        context.Response.ContentType = output.Response.ContentType ?? LineupLinkProtocol.MpegTsMediaType;
        context.Response.Headers.CacheControl = "no-cache, no-store";
        await output.Stream.CopyToAsync(context.Response.Body, cancellationToken);
    }
    catch (VirtualOutputClientException exception)
    {
        await WriteBrokerFailureAsync(context, exception, cancellationToken);
    }
});

await app.StartAsync();
await session.Broker.ReportHealthAsync(new VirtualOutputWorkerHealthReport
{
    Session = sessionContext,
    Ready = true,
    Bindings = bootstrap.VirtualDevice.Hosting!.Bindings
        .Select(binding => binding.PublicBaseUri)
        .ToArray()
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
    IReadOnlyDictionary<int, VirtualOutputBindingDefinition> bindingsByPort,
    IReadOnlyDictionary<string, VirtualOutputSecurityPolicyDefinition> policies)
{
    var binding = bindingsByPort.TryGetValue(localPort, out var configuredBinding)
        ? configuredBinding
        : throw new InvalidOperationException($"No virtual-output binding owns local port {localPort}.");
    return policies[binding.SecurityPolicyKey];
}

static LineupLinkAuthenticatedGrant? TryAuthorize(
    StringValues authorization,
    LineupLinkVirtualConfiguration configuration)
{
    return AuthenticationHeaderValue.TryParse(authorization.ToString(), out var parsed) &&
        string.Equals(parsed.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(parsed.Parameter)
            ? LineupLinkAccessGrantAuthenticator.Authenticate(parsed.Parameter, configuration, DateTime.UtcNow)
            : null;
}

static async Task WriteBrokerFailureAsync(
    HttpContext context,
    VirtualOutputClientException exception,
    CancellationToken cancellationToken)
{
    var status = exception.FailureCode switch
    {
        VirtualOutputFailureCode.ChannelNotFound => StatusCodes.Status404NotFound,
        VirtualOutputFailureCode.ResourceLimit => StatusCodes.Status429TooManyRequests,
        VirtualOutputFailureCode.Unauthorized or VirtualOutputFailureCode.StaleGeneration => StatusCodes.Status503ServiceUnavailable,
        VirtualOutputFailureCode.CapabilityDenied => StatusCodes.Status403Forbidden,
        VirtualOutputFailureCode.InvalidRequest => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status503ServiceUnavailable
    };
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new LineupLinkError
    {
        Code = exception.FailureCode.ToString().ToLowerInvariant(),
        Message = exception.Message
    }, cancellationToken);
}
