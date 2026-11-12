using System.Net.Http.Headers;
using System.Text.Json;
using Lineup.Core.Common.Streaming;
using Lineup.Plugin.LineupLink.Guides;
using Lineup.Plugin.LineupLink.Protocol;
using Lineup.Plugin.LineupLink.Virtual;
using Lineup.Core.VirtualOutputs.Hosting;
using Lineup.Core.VirtualOutputs.Protocol;
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
var listenerPolicy = new VirtualOutputListenerPolicy(bootstrap);
var session = new VirtualOutputWorkerSession(bootstrap);
await session.RegisterAsync();
var admission = new PluginConnectionAdmission(session);
var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
    options.UseUtcTimestamp = true;
});
listenerPolicy.AddServices(builder.Services);
builder.WebHost.ConfigureKestrel(admission.Configure);
builder.WebHost.ConfigureKestrel(listenerPolicy.Configure);

var app = builder.Build();
listenerPolicy.UseForwardedHeaders(app);
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if (!listenerPolicy.Allows(context))
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
});

admission.Use(app, app.Logger, countUnmarkedEndpoints: true,
    writeFailure: (context, code, message) => WriteBrokerFailureAsync(context, new VirtualOutputClientException(code, message), context.RequestAborted));
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
    try
    {
        var guide = await session.Broker.GetGuideAsync(new VirtualOutputGuideRequest { Session = sessionContext }, cancellationToken);
        return Results.Ok(await LineupLinkGuideExporter.CreateForTransferAsync(guide.Snapshot, cancellationToken: cancellationToken));
    }
    catch (Lineup.Core.Common.Guides.GuideTransferLimitException exception)
    {
        app.Logger.LogWarning("Lineup Link guide transfer rejected: {Error}", exception.Message);
        return Results.Json(new LineupLinkError { Code = "guide_limit", Message = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (VirtualOutputClientException exception)
    {
        app.Logger.LogWarning("Lineup Link broker guide request rejected: {FailureCode}", exception.FailureCode);
        return Results.Json(new LineupLinkError
        {
            Code = exception.FailureCode == VirtualOutputFailureCode.ResourceLimit ? "guide_limit" : GetFailureCode(exception.FailureCode),
            Message = exception.Message
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
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
    () => (true, admission.ActiveConnections),
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
        VirtualOutputFailureCode.SourceNotFound => StatusCodes.Status404NotFound,
        VirtualOutputFailureCode.SourceInvalid => StatusCodes.Status400BadRequest,
        VirtualOutputFailureCode.ProtectedContent => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status503ServiceUnavailable
    };
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new LineupLinkError
    {
        Code = GetFailureCode(exception.FailureCode),
        Message = exception.Message
    }, cancellationToken);
}

static string GetFailureCode(VirtualOutputFailureCode failureCode) =>
    failureCode switch
    {
        VirtualOutputFailureCode.ChannelNotFound => "channel_not_found",
        VirtualOutputFailureCode.ResourceLimit => "resource_limit",
        VirtualOutputFailureCode.CapabilityDenied => "capability_denied",
        VirtualOutputFailureCode.InvalidRequest => "invalid_request",
        VirtualOutputFailureCode.Unauthorized => "broker_unauthorized",
        VirtualOutputFailureCode.StaleGeneration => "stale_worker_generation",
        VirtualOutputFailureCode.SourceUnavailable => "source_unavailable",
        VirtualOutputFailureCode.SourceNoSignal => "source_no_signal",
        VirtualOutputFailureCode.SourceNoData => "source_no_video_data",
        VirtualOutputFailureCode.SourceCapacityExceeded => "source_capacity_exceeded",
        VirtualOutputFailureCode.SourceNotFound => "source_not_found",
        VirtualOutputFailureCode.SourceInvalid => "source_invalid",
        VirtualOutputFailureCode.ProtectedContent => "protected_content",
        _ => "broker_unavailable"
    };
