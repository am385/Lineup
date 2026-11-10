using System.Globalization;
using System.Text;
using System.Text.Json;
using Lineup.Core.Common.Streaming;
using Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner;
using Lineup.Core.VirtualOutputs.Hosting;
using Lineup.Core.VirtualOutputs.Protocol;

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
var hosting = bootstrap.VirtualDevice.Hosting
    ?? throw new InvalidDataException("HDHomeRun worker requires dedicated hosting configuration.");
var bindingsByPort = VirtualOutputBindingResolver.CreatePortMap(hosting);
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
    await next(context);
});

admission.Use(app, app.Logger, countUnmarkedEndpoints: true,
    writeFailure: (context, _, message) => context.Response.WriteAsJsonAsync(new HdHomeRunWorkerError { Error = message }, context.RequestAborted));
var sessionContext = session.Context!;
var manifest = await session.Broker.GetManifestAsync(
    new VirtualOutputManifestRequest { Session = sessionContext },
    CancellationToken.None);

app.MapGet("/discover.json", async (HttpContext context, CancellationToken cancellationToken) =>
{
    var currentManifest = await session.Broker.GetManifestAsync(
        new VirtualOutputManifestRequest { Session = sessionContext },
        cancellationToken);
    var baseUri = VirtualOutputBindingResolver.ResolvePublicBaseUri(bindingsByPort, context.Connection.LocalPort);
    var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    return Results.Ok(HdHomeRunProxyResponseFactory.CreateDiscover(
        currentManifest.Name,
        deviceId,
        deviceAuth,
        baseUri,
        checked((byte)Math.Clamp(currentManifest.TunerCount ?? 0, 0, byte.MaxValue)),
        version));
});
app.MapGet("/lineup.json", async (HttpContext context, CancellationToken cancellationToken) =>
{
    var channels = await GetChannelsAsync(
        session,
        sessionContext,
        VirtualOutputBindingResolver.ResolvePublicBaseUri(bindingsByPort, context.Connection.LocalPort),
        cancellationToken);
    return Results.Content(HdHomeRunProxyLineupFormatter.ToJson(channels), "application/json", Encoding.UTF8);
});
app.MapGet("/lineup.xml", async (HttpContext context, CancellationToken cancellationToken) =>
{
    var channels = await GetChannelsAsync(
        session,
        sessionContext,
        VirtualOutputBindingResolver.ResolvePublicBaseUri(bindingsByPort, context.Connection.LocalPort),
        cancellationToken);
    return Results.Content(HdHomeRunProxyLineupFormatter.ToXml(channels), "text/xml", Encoding.UTF8);
});
app.MapGet("/lineup.m3u", async (HttpContext context, CancellationToken cancellationToken) =>
{
    var channels = await GetChannelsAsync(
        session,
        sessionContext,
        VirtualOutputBindingResolver.ResolvePublicBaseUri(bindingsByPort, context.Connection.LocalPort),
        cancellationToken);
    return Results.Content(HdHomeRunProxyLineupFormatter.ToM3u(channels), "text/plain", Encoding.UTF8);
});
app.MapGet("/lineup_status.json", () => Results.Ok(new HdHomeRunProxyLineupStatus()));
app.MapGet("/device.xml", (HttpContext context) =>
{
    var baseUri = VirtualOutputBindingResolver.ResolvePublicBaseUri(bindingsByPort, context.Connection.LocalPort);
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

static async Task<IReadOnlyList<HdHomeRunProxyChannel>> GetChannelsAsync(
    VirtualOutputWorkerSession session,
    VirtualOutputSessionContext sessionContext,
    Uri baseUri,
    CancellationToken cancellationToken)
{
    var lineup = await session.Broker.GetLineupAsync(
        new VirtualOutputLineupRequest { Session = sessionContext },
        cancellationToken);
    return lineup.Channels
        .OrderBy(channel => channel.Number, StringComparer.OrdinalIgnoreCase)
        .Select(channel => HdHomeRunProxyChannel.Create(channel, baseUri))
        .ToArray();
}

static int GetFailureStatusCode(VirtualOutputFailureCode failureCode) =>
    failureCode switch
    {
        VirtualOutputFailureCode.ChannelNotFound => StatusCodes.Status404NotFound,
        VirtualOutputFailureCode.ResourceLimit => StatusCodes.Status429TooManyRequests,
        VirtualOutputFailureCode.CapabilityDenied => StatusCodes.Status403Forbidden,
        VirtualOutputFailureCode.InvalidRequest => StatusCodes.Status400BadRequest,
        VirtualOutputFailureCode.SourceNotFound => StatusCodes.Status404NotFound,
        VirtualOutputFailureCode.SourceInvalid => StatusCodes.Status400BadRequest,
        VirtualOutputFailureCode.ProtectedContent => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status503ServiceUnavailable
    };

/// <summary>Describes an HTTP error returned by the isolated HDHomeRun worker.</summary>
public sealed record HdHomeRunWorkerError
{
    /// <summary>Gets the safe client-facing error.</summary>
    public required string Error { get; init; }
}
