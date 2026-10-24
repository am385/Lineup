using System.Diagnostics;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Lineup.Common.Networking;
using Lineup.Common.Streaming;
using Lineup.VirtualOutputs.Hosting;
using Lineup.VirtualOutputs.Protocol;
using Lineup.WebPlayer;
using Lineup.WebPlayer.FFmpeg;
using Lineup.WebPlayer.VirtualOutputHost;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;

var bootstrap = await VirtualOutputWorkerBootstrapReader.ReadAsync(cancellationToken: CancellationToken.None);
if (!string.Equals(bootstrap.VirtualDevice.Adapter.Kind, WebPlayerVirtualTunerDriver.AdapterKind, StringComparison.Ordinal))
{
    throw new InvalidOperationException($"Web Player worker cannot host adapter kind '{bootstrap.VirtualDevice.Adapter.Kind}'.");
}
var hosting = bootstrap.VirtualDevice.Hosting
    ?? throw new InvalidDataException("Web Player worker requires dedicated hosting configuration.");
var securityPolicies = bootstrap.SecurityPolicies.ToDictionary(policy => policy.Key, StringComparer.OrdinalIgnoreCase);
var allowedNetworks = securityPolicies.ToDictionary(
    pair => pair.Key,
    pair => IpNetworkSet.Parse(pair.Value.AllowedNetworks, $"Virtual output security policy '{pair.Value.Name}' allowed networks"),
    StringComparer.OrdinalIgnoreCase);
var certificates = bootstrap.CertificateMaterials.ToDictionary(material => material.Reference, StringComparer.Ordinal);
var bindingsByPort = VirtualOutputBindingResolver.CreatePortMap(hosting);
var sessions = new DedicatedMediaSessionRegistry<MediaSession>(TimeProvider.System, TimeSpan.FromSeconds(20));
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
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(DedicatedWorkerCorsPolicy.Create());
});
builder.WebHost.ConfigureKestrel(options => ConfigureListeners(options, bootstrap, securityPolicies, certificates));
var app = builder.Build();
var workerSession = new VirtualOutputWorkerSession(bootstrap);
await workerSession.RegisterAsync();
var activeRequests = 0;
app.UseVirtualOutputForwardedHeaders(hosting, securityPolicies);
app.UseCors();
app.UseRateLimiter();
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
        await next(context);
    }
    finally
    {
        Interlocked.Decrement(ref activeRequests);
    }
});

app.MapPost("/media/start/{channel}", async (
    string channel,
    CmafStreamRequest request,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    var stopwatch = Stopwatch.StartNew();
    var stage = DedicatedWorkerStartupStage.Preparation;
    var id = Guid.NewGuid().ToString("N");
    var directory = Path.Combine(Path.GetTempPath(), "lineup-web-player", id);
    try
    {
        Directory.CreateDirectory(directory);
        stage = DedicatedWorkerStartupStage.Broker;
        VirtualOutputClientStream source;
        try
        {
            source = await workerSession.Broker.OpenStreamAsync(new VirtualOutputOpenStreamRequest
            {
                Session = workerSession.Context!,
                RequestId = VirtualOutputStreamRequestId.New(),
                ChannelKey = channel,
                Stage = VirtualOutputStreamStage.PhysicalOutput,
                Client = new VirtualOutputClientContext
                {
                    Address = context.Connection.RemoteIpAddress?.ToString(),
                    UserAgent = context.Request.Headers.UserAgent.ToString()
                }
            }, cancellationToken);
        }
        catch
        {
            await MediaSessionStartupGuard.DeleteDirectoryAsync(directory);
            throw;
        }

        await using var startupGuard = new MediaSessionStartupGuard(directory, source);
        stage = DedicatedWorkerStartupStage.Planning;
        var plan = DedicatedCmafSessionPlan.Create(source.Response, request, directory);
        var streamSessionId = source.Response.StreamSessionId
            ?? throw new InvalidDataException("The capability broker did not provide a stream-session identifier.");
        var streamStartedAtUtc = DateTime.UtcNow;
        await ReportStreamStatusAsync(
            workerSession,
            source,
            plan,
            id,
            channel,
            streamSessionId,
            streamStartedAtUtc,
            VirtualOutputStreamStartupStage.Starting,
            null,
            cancellationToken);
        stage = DedicatedWorkerStartupStage.Process;
        var process = StartMediaProcess(directory, plan.Arguments);
        startupGuard.RegisterProcess(process);
        Process? captionProcess = null;
        if (plan.EmbeddedCaptions is not null)
        {
            IReadOnlyList<string> captionArguments = WebPlayerTrackPlanner.CreateEmbeddedCaptionArguments(plan.SubtitlePaths[plan.EmbeddedCaptions.Index]);
            captionProcess = StartMediaProcess(directory, captionArguments);
            startupGuard.RegisterProcess(captionProcess);
        }
        var session = new MediaSession(id, directory, process, captionProcess, source, plan, new CancellationTokenSource(), app.Logger);
        startupGuard.TransferOwnership();
        if (!sessions.TryAdd(id, session))
        {
            await session.DisposeAsync();
            return Results.Conflict();
        }
        session.PumpTask = PumpInputAsync(session);
        var dashPath = Path.Combine(directory, CmafStreamPlanner.DashManifestName);
        var hlsPath = Path.Combine(directory, CmafStreamPlanner.HlsManifestName);
        stage = DedicatedWorkerStartupStage.Readiness;
        try
        {
            await WaitForOutputAsync(process, session.ErrorTask, dashPath, hlsPath, plan.AudioRenditions.Count + 1, cancellationToken);
        }
        catch (Exception exception)
        {
            var failure = DedicatedWorkerStartupFailureMapper.Map(exception, stage);
            await TryReportStreamFailureAsync(
                workerSession,
                source,
                plan,
                id,
                channel,
                streamSessionId,
                streamStartedAtUtc,
                $"{failure.Code}: {failure.Error}",
                app.Logger);
            await sessions.RemoveAndDisposeAsync(id);
            throw;
        }
        await ReportStreamStatusAsync(
            workerSession,
            source,
            plan,
            id,
            channel,
            streamSessionId,
            streamStartedAtUtc,
            VirtualOutputStreamStartupStage.Ready,
            null,
            cancellationToken);
        session.ObserveTask = ObserveProcessAsync(session, sessions);
        var publicBaseUri = VirtualOutputBindingResolver.ResolvePublicBaseUri(bindingsByPort, context.Connection.LocalPort);
        return Results.Ok(plan.CreateResponse(id, publicBaseUri));
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception exception)
    {
        var failure = DedicatedWorkerStartupFailureMapper.Map(exception, stage);
        app.Logger.LogWarning(
            exception,
            "Dedicated Web Player startup failed for channel {Channel} in worker generation {Generation} with code {FailureCode} during stage {StartupStage} after {ElapsedMilliseconds} ms.",
            channel,
            bootstrap.Generation,
            failure.Code,
            failure.Stage,
            stopwatch.ElapsedMilliseconds);
        return Results.Json(failure, statusCode: failure.StatusCode);
    }
});

static async Task ReportStreamStatusAsync(
    VirtualOutputWorkerSession workerSession,
    VirtualOutputClientStream source,
    DedicatedCmafSessionPlan plan,
    string workerMediaSessionId,
    string channel,
    StreamSessionId streamSessionId,
    DateTime startedAtUtc,
    VirtualOutputStreamStartupStage startupStage,
    string? lastFailure,
    CancellationToken cancellationToken)
{
    if (workerSession.ProtocolVersion < 2)
    {
        return;
    }
    var snapshot = plan.CreateActiveStreamSnapshot(streamSessionId.ToString(), channel, startedAtUtc);
    var response = await workerSession.Broker.ReportStreamStatusAsync(new VirtualOutputStreamStatusReport
    {
        Session = workerSession.Context!,
        RequestId = source.RequestId,
        StreamSessionId = streamSessionId,
        WorkerMediaSessionId = workerMediaSessionId,
        Format = snapshot.Format,
        Tracks = snapshot.Tracks,
        PhysicalInput = snapshot.PhysicalInput,
        PhysicalOutput = snapshot.PhysicalOutput,
        VirtualOutput = snapshot.VirtualOutput,
        StartupStage = startupStage,
        LastFailure = lastFailure
    }, cancellationToken);
    if (!response.Updated)
    {
        throw new InvalidOperationException("The capability broker no longer recognizes the dedicated Web Player stream.");
    }
}

static async Task TryReportStreamFailureAsync(
    VirtualOutputWorkerSession workerSession,
    VirtualOutputClientStream source,
    DedicatedCmafSessionPlan plan,
    string workerMediaSessionId,
    string channel,
    StreamSessionId streamSessionId,
    DateTime startedAtUtc,
    string failure,
    ILogger logger)
{
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ReportStreamStatusAsync(
            workerSession,
            source,
            plan,
            workerMediaSessionId,
            channel,
            streamSessionId,
            startedAtUtc,
            VirtualOutputStreamStartupStage.Failed,
            failure,
            timeout.Token);
    }
    catch (Exception exception)
    {
        logger.LogWarning(
            exception,
            "Unable to report failed dedicated Web Player session {WorkerMediaSessionId} for broker stream {StreamSessionId}.",
            workerMediaSessionId,
            streamSessionId);
    }
}

app.MapGet("/media/{sessionId}/{filename}", async (string sessionId, string filename, long? offset, HttpContext context) =>
{
    if (!sessions.TryGetAndTouch(sessionId, out var session) || !TryResolveMediaFile(session.Directory, filename, out var path))
    {
        return Results.NotFound();
    }
    context.Response.Headers.CacheControl = "no-cache, no-store";
    if (string.Equals(Path.GetExtension(filename), ".vtt", StringComparison.OrdinalIgnoreCase))
    {
        if (!File.Exists(path))
        {
            byte[] emptySubtitleBytes = [];
            context.Response.Headers["X-Lineup-Subtitle-Offset"] = "0";
            return Results.File(emptySubtitleBytes, "text/vtt; charset=utf-8");
        }
        await using var stream = OpenGrowingFile(path);
        var effectiveOffset = Math.Clamp(offset ?? 0, 0, stream.Length);
        stream.Position = effectiveOffset;
        var data = new byte[(int)Math.Min(stream.Length - effectiveOffset, 64 * 1024)];
        await stream.ReadExactlyAsync(data, context.RequestAborted);
        context.Response.Headers["X-Lineup-Subtitle-Offset"] = (effectiveOffset + data.Length).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.File(data, "text/vtt; charset=utf-8");
    }
    if (!File.Exists(path))
    {
        return Results.StatusCode(425);
    }
    if (string.Equals(filename, CmafStreamPlanner.DashManifestName, StringComparison.Ordinal))
    {
        var manifest = await ReadGrowingTextAsync(path, context.RequestAborted);
        manifest = CmafStreamPlanner.RewriteManifestAudioLabels(manifest, session.Plan.AudioRenditions);
        if (session.Plan.SourceVideoCodec is { } sourceVideoCodec)
        {
            manifest = CmafStreamPlanner.RewriteManifestCodecs(manifest, sourceVideoCodec);
        }
        return Results.Content(manifest, "application/dash+xml");
    }
    if (string.Equals(filename, CmafStreamPlanner.HlsManifestName, StringComparison.Ordinal))
    {
        var manifest = await ReadGrowingTextAsync(path, context.RequestAborted);
        manifest = CmafStreamPlanner.RewriteManifestAudioLabels(manifest, session.Plan.AudioRenditions);
        if (session.Plan.SourceVideoCodec is { } sourceVideoCodec)
        {
            manifest = CmafStreamPlanner.RewriteManifestCodecs(manifest, sourceVideoCodec);
        }
        return Results.Content(manifest, "application/vnd.apple.mpegurl");
    }
    if (filename.StartsWith("media_", StringComparison.Ordinal) && filename.EndsWith(".m3u8", StringComparison.Ordinal))
    {
        var dashPath = Path.Combine(session.Directory, CmafStreamPlanner.DashManifestName);
        if (!File.Exists(dashPath))
        {
            return Results.StatusCode(425);
        }
        var playlist = await ReadGrowingTextAsync(path, context.RequestAborted);
        var dashManifest = await ReadGrowingTextAsync(dashPath, context.RequestAborted);
        return Results.Content(CmafStreamPlanner.RewriteHlsProgramDateTimes(playlist, dashManifest), "application/vnd.apple.mpegurl");
    }
    return Results.File(path, GetContentType(filename), enableRangeProcessing: true);
});

app.MapPost("/media/stop/{sessionId}", async (string sessionId) =>
{
    if (!await sessions.RemoveAndDisposeAsync(sessionId))
    {
        return Results.NotFound();
    }
    return Results.NoContent();
});

app.MapGet("/health", () => Results.Ok(new { ready = true, activeSessions = sessions.Count }));

// This passive inventory uses the same listener network, TLS and rate-limit policies as media requests.
app.MapGet("/media/sessions", () => Results.Ok(new { generation = bootstrap.Generation, sessionIds = sessions.GetSessionIds() }));

using var leaseCancellation = new CancellationTokenSource();
var leaseTask = workerSession.RunLeaseLoopAsync(
    () => (Ready: true, ActiveConnections: Volatile.Read(ref activeRequests) + sessions.Count),
    () => leaseCancellation.Cancel(),
    leaseCancellation.Token);
var expirationTask = sessions.RunExpirationLoopAsync(leaseCancellation.Token);
var runTask = app.RunAsync(leaseCancellation.Token);
try
{
    await Task.WhenAny(runTask, leaseTask, expirationTask);
}
finally
{
    leaseCancellation.Cancel();
    await app.StopAsync();
    try
    {
        await expirationTask;
    }
    finally
    {
        await sessions.DisposeAllAsync();
    }
}

static Process StartMediaProcess(string directory, IReadOnlyList<string> arguments)
{
    var configuredExecutable = Environment.GetEnvironmentVariable("LINEUP_FFMPEG_EXECUTABLE");
    var executable = string.IsNullOrWhiteSpace(configuredExecutable) ? "ffmpeg" : configuredExecutable;
    var startInfo = new ProcessStartInfo
    {
        FileName = executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : executable,
        WorkingDirectory = directory,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardError = true
    };
    if (executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
    {
        startInfo.ArgumentList.Add(executable);
    }
    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }
    return Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start FFmpeg for the Web Player output.");
}

static async Task PumpInputAsync(MediaSession session)
{
    var buffer = new byte[64 * 1024];
    var captionInputAvailable = session.CaptionProcess is not null;
    try
    {
        while (true)
        {
            var bytesRead = await session.Source.Stream.ReadAsync(buffer, session.Lifetime.Token);
            if (bytesRead == 0)
            {
                break;
            }
            await session.Process.StandardInput.BaseStream.WriteAsync(buffer.AsMemory(0, bytesRead), session.Lifetime.Token);
            if (captionInputAvailable)
            {
                try
                {
                    await session.CaptionProcess!.StandardInput.BaseStream.WriteAsync(buffer.AsMemory(0, bytesRead), session.Lifetime.Token);
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                {
                    captionInputAvailable = false;
                    session.CaptionProcess!.StandardInput.Close();
                }
            }
        }
    }
    catch (OperationCanceledException) when (session.Lifetime.IsCancellationRequested)
    {
    }
    catch (IOException)
    {
    }
    finally
    {
        session.Process.StandardInput.Close();
        session.CaptionProcess?.StandardInput.Close();
    }
}

static async Task ObserveProcessAsync(MediaSession session, DedicatedMediaSessionRegistry<MediaSession> sessions)
{
    await session.Process.WaitForExitAsync();
    await sessions.RemoveAndDisposeAsync(session.Id);
}

static async Task WaitForOutputAsync(
    Process process,
    Task<string> errorTask,
    string dashPath,
    string hlsPath,
    int representationCount,
    CancellationToken cancellationToken)
{
    using var readinessTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, readinessTimeout.Token);
    int[] representationFragmentCounts = [];
    try
    {
        var directory = Path.GetDirectoryName(dashPath)!;
        while (true)
        {
            representationFragmentCounts = Enumerable.Range(0, representationCount)
                .Select(index => Directory.EnumerateFiles(directory, $"chunk-{index}-*.m4s").Take(2).Count())
                .ToArray();
            if (File.Exists(dashPath) &&
                File.Exists(hlsPath) &&
                representationFragmentCounts.All(count => count >= 2))
            {
                return;
            }
            if (process.HasExited)
            {
                var error = await errorTask.WaitAsync(linkedCancellation.Token);
                throw new InvalidOperationException($"Web Player FFmpeg exited before producing media: {error}");
            }
            await Task.Delay(100, linkedCancellation.Token);
        }
    }
    catch (OperationCanceledException) when (readinessTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
        var error = await errorTask;
        throw new TimeoutException(
            "The Web Player media presentation did not become ready within 20 seconds. " +
            $"Expected {representationCount} representations with fragment counts [{string.Join(", ", representationFragmentCounts)}]. " +
            $"FFmpeg diagnostics: {GetDiagnosticTail(error)}");
    }
}

static string GetDiagnosticTail(string value)
{
    var lines = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    return string.Join(" | ", lines.TakeLast(10));
}

static FileStream OpenGrowingFile(string path) =>
    new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

static async Task<string> ReadGrowingTextAsync(string path, CancellationToken cancellationToken)
{
    await using var stream = OpenGrowingFile(path);
    using var reader = new StreamReader(stream);
    return await reader.ReadToEndAsync(cancellationToken);
}

static bool TryResolveMediaFile(string directory, string filename, out string path)
{
    path = string.Empty;
    if (string.IsNullOrWhiteSpace(filename) ||
        Path.GetFileName(filename) != filename ||
        !new[] { ".mpd", ".m3u8", ".m4s", ".mp4", ".vtt" }.Contains(Path.GetExtension(filename), StringComparer.OrdinalIgnoreCase))
    {
        return false;
    }
    var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
    var candidate = Path.GetFullPath(Path.Combine(directory, filename));
    if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }
    path = candidate;
    return true;
}

static string GetContentType(string filename) =>
    Path.GetExtension(filename).ToLowerInvariant() switch
    {
        ".mpd" => "application/dash+xml",
        ".m3u8" => "application/vnd.apple.mpegurl",
        ".m4s" or ".mp4" => "video/mp4",
        ".vtt" => "text/vtt",
        _ => "application/octet-stream"
    };

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
    foreach (var binding in bootstrap.VirtualDevice.Hosting!.Bindings)
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
                var certificateReference = policy.CertificateReference
                    ?? throw new InvalidOperationException($"HTTPS policy '{policy.Key}' does not reference a certificate.");
                var material = certificates[certificateReference];
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
    IReadOnlyDictionary<string, VirtualOutputSecurityPolicyDefinition> policies) =>
    policies[bindingsByPort.TryGetValue(localPort, out var binding)
        ? binding.SecurityPolicyKey
        : throw new InvalidOperationException($"No virtual-output binding owns local port {localPort}.")];

internal sealed class MediaSession : IAsyncDisposable
{
    private int _disposed;

    public MediaSession(
        string id,
        string directory,
        Process process,
        Process? captionProcess,
        VirtualOutputClientStream source,
        DedicatedCmafSessionPlan plan,
        CancellationTokenSource lifetime,
        ILogger logger)
    {
        Id = id;
        Directory = directory;
        Process = process;
        CaptionProcess = captionProcess;
        Source = source;
        Plan = plan;
        Lifetime = lifetime;
        Logger = logger;
        ErrorTask = ReadDiagnosticTailAsync(process.StandardError);
        CaptionErrorTask = captionProcess is null ? null : ReadDiagnosticTailAsync(captionProcess.StandardError);
    }

    public string Id { get; }

    public string Directory { get; }

    public Process Process { get; }

    public Process? CaptionProcess { get; }

    public VirtualOutputClientStream Source { get; }

    public DedicatedCmafSessionPlan Plan { get; }

    public CancellationTokenSource Lifetime { get; }

    public ILogger Logger { get; }

    public Task? PumpTask { get; set; }

    public Task? ObserveTask { get; set; }

    public Task<string> ErrorTask { get; }

    public Task<string>? CaptionErrorTask { get; }

    /// <summary>Drains FFmpeg stderr without owning the reader, retaining at most fifty non-empty lines from the last 16,384 characters with normalized LF separators.</summary>
    internal static async Task<string> ReadDiagnosticTailAsync(TextReader reader)
    {
        var buffer = new char[16_384];
        var offset = 0;
        var retained = 0;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(offset));
            if (count == 0)
            {
                break;
            }
            offset = (offset + count) % buffer.Length;
            retained = Math.Min(buffer.Length, retained + count);
        }
        var tail = retained < buffer.Length
            ? new string(buffer, 0, retained)
            : string.Concat(buffer.AsSpan(offset), buffer.AsSpan(0, offset));
        return string.Join("\n", tail.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).TakeLast(50));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        Lifetime.Cancel();
        try
        {
            await CloseBrokerStreamAsync();
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
            }
            if (CaptionProcess is not null && !CaptionProcess.HasExited)
            {
                CaptionProcess.Kill(entireProcessTree: true);
            }
            try
            {
                await Process.WaitForExitAsync();
            }
            catch (InvalidOperationException)
            {
            }
            if (CaptionProcess is not null)
            {
                try
                {
                    await CaptionProcess.WaitForExitAsync();
                }
                catch (InvalidOperationException)
                {
                }
            }
            if (PumpTask is not null)
            {
                try
                {
                    await PumpTask;
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
                {
                }
            }
            await ErrorTask;
            if (CaptionErrorTask is not null)
            {
                await CaptionErrorTask;
            }
        }
        finally
        {
            try
            {
                await Source.DisposeAsync();
            }
            finally
            {
                Process.Dispose();
                CaptionProcess?.Dispose();
                Lifetime.Dispose();
                await MediaSessionStartupGuard.DeleteDirectoryAsync(Directory);
            }
        }
    }

    private async Task CloseBrokerStreamAsync()
    {
        using var closeCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var closed = await Source.CloseAsync(closeCancellation.Token);
            Logger.LogDebug(
                "Dedicated Web Player session {MediaSessionId} closed broker request {BrokerRequestId} and stream {StreamSessionId}: {Closed}.",
                Id,
                Source.RequestId,
                Source.Response.StreamSessionId,
                closed);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or VirtualOutputClientException)
        {
            Logger.LogWarning(
                exception,
                "Dedicated Web Player session {MediaSessionId} could not confirm closure of broker request {BrokerRequestId} and stream {StreamSessionId}; releasing the IPC connection as fallback.",
                Id,
                Source.RequestId,
                Source.Response.StreamSessionId);
        }
    }
}
