using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Lineup.Common.Streaming;
using Lineup.VirtualOutputs.Hosting;
using Lineup.VirtualOutputs.Protocol;
using Lineup.WebPlayer;
using Lineup.WebPlayer.FFmpeg;
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
var certificates = bootstrap.CertificateMaterials.ToDictionary(material => material.Reference, StringComparer.Ordinal);
var publicBaseUri = new Uri(hosting.Bindings[0].PublicBaseUri.EndsWith('/') ? hosting.Bindings[0].PublicBaseUri : $"{hosting.Bindings[0].PublicBaseUri}/");
var sessions = new ConcurrentDictionary<string, MediaSession>(StringComparer.Ordinal);
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
        var policy = ResolveSecurityPolicy(context.Connection.LocalPort, hosting, securityPolicies);
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
var workerSession = new VirtualOutputWorkerSession(bootstrap);
await workerSession.RegisterAsync();
var activeRequests = 0;
app.Use((context, next) =>
{
    ApplyForwardedHeaders(context, ResolveSecurityPolicy(context.Connection.LocalPort, hosting, securityPolicies));
    return next(context);
});
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    Interlocked.Increment(ref activeRequests);
    try
    {
        var policy = ResolveSecurityPolicy(context.Connection.LocalPort, hosting, securityPolicies);
        if (!IsClientAllowed(context.Connection.RemoteIpAddress, policy.AllowedNetworks))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (context.Request.Headers.Origin is { Count: > 0 } origin)
        {
            context.Response.Headers.AccessControlAllowOrigin = origin.ToString();
            context.Response.Headers.Vary = "Origin";
            context.Response.Headers.AccessControlAllowMethods = "GET,POST,OPTIONS";
            context.Response.Headers.AccessControlAllowHeaders = "Content-Type";
        }
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
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
    var id = Guid.NewGuid().ToString("N");
    var directory = Path.Combine(Path.GetTempPath(), "lineup-web-player", id);
    Directory.CreateDirectory(directory);
    VirtualOutputClientStream source;
    try
    {
        source = await workerSession.Broker.OpenStreamAsync(new VirtualOutputOpenStreamRequest
        {
            Session = workerSession.Context!,
            RequestId = VirtualOutputStreamRequestId.New(),
            ChannelKey = channel,
            Client = new VirtualOutputClientContext
            {
                Address = context.Connection.RemoteIpAddress?.ToString(),
                UserAgent = context.Request.Headers.UserAgent.ToString()
            }
        }, cancellationToken);
    }
    catch
    {
        Directory.Delete(directory, recursive: true);
        throw;
    }

    var process = StartFfmpeg(directory, request);
    var lifetime = new CancellationTokenSource();
    var session = new MediaSession(id, directory, process, source, lifetime);
    if (!sessions.TryAdd(id, session))
    {
        await session.DisposeAsync();
        return Results.Conflict();
    }
    session.PumpTask = PumpInputAsync(session);
    session.ObserveTask = ObserveProcessAsync(session, sessions);
    var dashPath = Path.Combine(directory, CmafStreamPlanner.DashManifestName);
    var hlsPath = Path.Combine(directory, CmafStreamPlanner.HlsManifestName);
    try
    {
        await WaitForOutputAsync(process, dashPath, hlsPath, cancellationToken);
    }
    catch
    {
        sessions.TryRemove(id, out _);
        await session.DisposeAsync();
        throw;
    }
    var mediaRoot = new Uri(publicBaseUri, $"media/{id}/");
    return Results.Ok(new
    {
        SessionId = id,
        HlsManifestUrl = new Uri(mediaRoot, CmafStreamPlanner.HlsManifestName).AbsoluteUri,
        DashManifestUrl = new Uri(mediaRoot, CmafStreamPlanner.DashManifestName).AbsoluteUri,
        StopUrl = new Uri(publicBaseUri, $"media/stop/{id}").AbsoluteUri
    });
});

app.MapGet("/media/{sessionId}/{filename}", (string sessionId, string filename) =>
{
    if (!sessions.TryGetValue(sessionId, out var session) || !TryResolveMediaFile(session.Directory, filename, out var path))
    {
        return Results.NotFound();
    }
    if (!File.Exists(path))
    {
        return Results.StatusCode(425);
    }
    return Results.File(path, GetContentType(filename), enableRangeProcessing: true);
});

app.MapPost("/media/stop/{sessionId}", async (string sessionId) =>
{
    if (!sessions.TryRemove(sessionId, out var session))
    {
        return Results.NotFound();
    }
    await session.DisposeAsync();
    return Results.NoContent();
});
app.MapGet("/health", () => Results.Ok(new { ready = true, activeSessions = sessions.Count }));

using var leaseCancellation = new CancellationTokenSource();
var leaseTask = workerSession.RunLeaseLoopAsync(
    () => (Ready: true, ActiveConnections: Volatile.Read(ref activeRequests) + sessions.Count),
    () => leaseCancellation.Cancel(),
    leaseCancellation.Token);
var runTask = app.RunAsync(leaseCancellation.Token);
try
{
    await Task.WhenAny(runTask, leaseTask);
}
finally
{
    leaseCancellation.Cancel();
    await app.StopAsync();
    foreach (var session in sessions.Values)
    {
        await session.DisposeAsync();
    }
    sessions.Clear();
}

static Process StartFfmpeg(string directory, CmafStreamRequest request)
{
    var height = request.Quality switch
    {
        WebPlayerQuality.Low => 480,
        WebPlayerQuality.Medium => 720,
        WebPlayerQuality.High => 1080,
        _ => 0
    };
    var arguments = new List<string>
    {
        "-hide_banner", "-loglevel", "warning", "-fflags", "+genpts+discardcorrupt",
        "-i", "pipe:0", "-map", "0:v:0?", "-map", "0:a:0?",
        "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency",
        "-g", "60", "-keyint_min", "60", "-sc_threshold", "0"
    };
    if (height > 0)
    {
        arguments.AddRange(["-vf", $"scale=-2:{height}:force_original_aspect_ratio=decrease"]);
    }
    arguments.AddRange(
    [
        "-c:a", "aac", "-b:a", "192k", "-ac", "2",
        "-f", "dash", "-seg_duration", "2", "-window_size", "6", "-extra_window_size", "3",
        "-remove_at_exit", "1", "-use_template", "1", "-use_timeline", "1",
        "-hls_playlist", "1", "-hls_master_name", CmafStreamPlanner.HlsManifestName,
        CmafStreamPlanner.DashManifestName
    ]);
    var startInfo = new ProcessStartInfo
    {
        FileName = "ffmpeg",
        WorkingDirectory = directory,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardError = true
    };
    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }
    return Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start FFmpeg for the Web Player output.");
}

static async Task PumpInputAsync(MediaSession session)
{
    try
    {
        await session.Source.Stream.CopyToAsync(session.Process.StandardInput.BaseStream, session.Lifetime.Token);
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
    }
}

static async Task ObserveProcessAsync(MediaSession session, ConcurrentDictionary<string, MediaSession> sessions)
{
    await session.Process.WaitForExitAsync();
    sessions.TryRemove(session.Id, out _);
    await session.DisposeAsync();
}

static async Task WaitForOutputAsync(Process process, string dashPath, string hlsPath, CancellationToken cancellationToken)
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(20));
    while (!File.Exists(dashPath) || !File.Exists(hlsPath))
    {
        if (process.HasExited)
        {
            var error = await process.StandardError.ReadToEndAsync(timeout.Token);
            throw new InvalidOperationException($"Web Player FFmpeg exited before producing media: {error}");
        }
        await Task.Delay(100, timeout.Token);
    }
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
    VirtualOutputHostingDefinition hosting,
    IReadOnlyDictionary<string, VirtualOutputSecurityPolicyDefinition> policies) =>
    policies[hosting.Bindings.Single(binding => binding.Port == localPort).SecurityPolicyKey];

static bool IsClientAllowed(IPAddress? address, IReadOnlyList<string> allowedNetworks) =>
    allowedNetworks.Count == 0 ||
    address is not null && allowedNetworks.Any(network => IPNetwork.TryParse(network, out var parsed) && parsed.Contains(address));

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

internal sealed class MediaSession : IAsyncDisposable
{
    private int _disposed;

    public MediaSession(
        string id,
        string directory,
        Process process,
        VirtualOutputClientStream source,
        CancellationTokenSource lifetime)
    {
        Id = id;
        Directory = directory;
        Process = process;
        Source = source;
        Lifetime = lifetime;
        ErrorTask = process.StandardError.ReadToEndAsync();
    }

    public string Id { get; }

    public string Directory { get; }

    public Process Process { get; }

    public VirtualOutputClientStream Source { get; }

    public CancellationTokenSource Lifetime { get; }

    public Task? PumpTask { get; set; }

    public Task? ObserveTask { get; set; }

    public Task<string> ErrorTask { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        Lifetime.Cancel();
        if (!Process.HasExited)
        {
            Process.Kill(entireProcessTree: true);
        }
        try
        {
            await Process.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
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
        await Source.DisposeAsync();
        Process.Dispose();
        Lifetime.Dispose();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (System.IO.Directory.Exists(Directory))
                {
                    System.IO.Directory.Delete(Directory, recursive: true);
                }
                break;
            }
            catch (IOException) when (attempt < 2)
            {
                await Task.Delay(100);
            }
        }
    }
}
