using Lineup.Core.Common.Streaming;
using Lineup.Core.Streaming.FFmpeg;
using Lineup.Core.VirtualOutputs.Hosting;
using Lineup.Core.VirtualOutputs.Protocol;
using Lineup.Plugin.WebPlayer;
using Lineup.Plugin.WebPlayer.FFmpeg;
using Lineup.Plugin.WebPlayer.VirtualOutputHost;

var bootstrap = await VirtualOutputWorkerBootstrapReader.ReadAsync(cancellationToken: CancellationToken.None);
if (!string.Equals(bootstrap.VirtualDevice.Adapter.Kind, WebPlayerVirtualTunerDriver.AdapterKind, StringComparison.Ordinal))
{
    throw new InvalidOperationException($"Web Player worker cannot host adapter kind '{bootstrap.VirtualDevice.Adapter.Kind}'.");
}
var hosting = bootstrap.VirtualDevice.Hosting
    ?? throw new InvalidDataException("Web Player worker requires dedicated hosting configuration.");
var listenerPolicy = new VirtualOutputListenerPolicy(bootstrap);
var workerSession = new VirtualOutputWorkerSession(bootstrap);
await workerSession.RegisterAsync();
var admission = new PluginConnectionAdmission(workerSession);
var bindingsByPort = VirtualOutputBindingResolver.CreatePortMap(hosting);
var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
    options.UseUtcTimestamp = true;
});
listenerPolicy.AddServices(builder.Services);
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(DedicatedWorkerCorsPolicy.Create());
});
builder.WebHost.ConfigureKestrel(admission.Configure);
builder.WebHost.ConfigureKestrel(listenerPolicy.Configure);
await using var app = builder.Build();
await using var playback = new DedicatedPlaybackRuntime(workerSession, app.Logger);
listenerPolicy.UseForwardedHeaders(app);
app.UseCors();
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
    writeFailure: (context, code, message) => context.Response.WriteAsJsonAsync(
        DedicatedWorkerStartupFailureMapper.Map(new VirtualOutputClientException(code, message), DedicatedWorkerStartupStage.Broker), context.RequestAborted));

app.MapPost("/media/start/{channel}", async (string channel, CmafStreamRequest request, HttpContext context, CancellationToken cancellationToken) =>
{
    try
    {
        var viewer = await playback.StartAsync(channel, request, new VirtualOutputClientContext
        {
            Address = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = context.Request.Headers.UserAgent.ToString()
        }, cancellationToken);
        var publicBaseUri = VirtualOutputBindingResolver.ResolvePublicBaseUri(bindingsByPort, context.Connection.LocalPort);
        return Results.Ok(viewer.Plan.CreateResponse(viewer.Id, publicBaseUri) with { WorkerGeneration = bootstrap.Generation });
    }
    catch (PlaybackStartupException exception)
    {
        var failure = DedicatedWorkerStartupFailureMapper.Map(exception.InnerException!, exception.Stage);
        app.Logger.LogWarning("Dedicated playback startup failed for channel {Channel} in generation {Generation}: {Code} during {Stage}.",
            channel, bootstrap.Generation, failure.Code, failure.Stage);
        return Results.Json(failure, statusCode: failure.StatusCode);
    }
});

app.MapGet("/media/{sessionId}/{filename}", async (string sessionId, string filename, long? offset, HttpContext context) =>
{
    if (!playback.TryGet(sessionId, out var session) || !DedicatedPlaybackRuntime.TryResolveMediaFile(session!.Directory, filename, out var path))
    {
        return Results.NotFound();
    }
    return await DedicatedMediaFileResponse.CreateAsync(path, filename, session.Plan, offset, context);
});

app.MapPost("/media/stop/{sessionId}", async (string sessionId) =>
{
    if (!await playback.StopAsync(sessionId))
    {
        return Results.NotFound();
    }
    return Results.NoContent();
}).WithMetadata(new PluginConnectionEndpointAttribute(false));

app.MapGet("/health", () => Results.Ok(new { ready = true, activeSessions = playback.ViewerIds.Length, activeProducers = playback.Producers.Count })).WithMetadata(new PluginConnectionEndpointAttribute(false));

// This passive inventory uses the same listener network, TLS and rate-limit policies as media requests.
app.MapGet("/media/sessions", () => Results.Ok(new { generation = bootstrap.Generation, sessionIds = playback.ViewerIds, producers = playback.Producers }))
    .WithMetadata(new PluginConnectionEndpointAttribute(false));

app.MapGet("/media/status/{sessionId}", (string sessionId, HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(playback.GetPlaybackStatus(sessionId));
}).WithMetadata(new PluginConnectionEndpointAttribute(false));

using var leaseCancellation = new CancellationTokenSource();
var leaseTask = workerSession.RunLeaseLoopAsync(
    () => (Ready: true, ActiveConnections: admission.ActiveConnections),
    () => leaseCancellation.Cancel(),
    leaseCancellation.Token);
var runTask = app.RunAsync(leaseCancellation.Token);
try
{
    await Task.WhenAny(runTask, leaseTask);
}
finally
{
    await leaseCancellation.CancelAsync();
    await app.StopAsync();
    try
    {
        await Task.WhenAll(runTask, leaseTask);
    }
    catch (OperationCanceledException) when (leaseCancellation.IsCancellationRequested)
    {
    }
}
