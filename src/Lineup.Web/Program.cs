using Lineup.HDHomeRun.Device;
using Lineup.HDHomeRun.Device.Protocol;
using Lineup.HDHomeRun.Api;
using Lineup.Core;
using Lineup.Core.Storage;
using Lineup.Web.Components;
using Lineup.Web.Services;

var builder = WebApplication.CreateBuilder(args);
var statusRuntime = new StatusApiRuntime(Guid.NewGuid().ToString("N"));
var appDataStore = AppDataStore.Create(builder.Configuration);
builder.Services.AddSingleton(appDataStore);
builder.Services.AddSingleton<IAppDataStore>(appDataStore);
var transientDataStore = TransientDataStore.Create(builder.Configuration);
builder.Services.AddSingleton(transientDataStore);
builder.Services.AddSingleton<ITransientDataStore>(transientDataStore);
var xmltvPublicationStore = new XmltvPublicationStore();
builder.Services.AddSingleton<IXmltvPublicationStore>(xmltvPublicationStore);
builder.Services.AddScoped<IBrowserDataStore, BrowserDataStore>();
builder.Services.AddScoped<IStatusNotificationService, StatusNotificationService>();
builder.Services.AddSingleton<IFileSystemBrowser, FileSystemBrowser>();

// Prefer the verified repo-local Jellyfin FFmpeg installed by scripts/Install-JellyfinFfmpeg.ps1.
var sourceProjectPath = Path.Combine(builder.Environment.ContentRootPath, "Lineup.Web.csproj");
var localFfmpegDirectory = File.Exists(sourceProjectPath)
    ? Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", ".ffmpeg"))
    : Path.Combine(builder.Environment.ContentRootPath, ".ffmpeg");
var localFfmpegExecutable = Path.Combine(localFfmpegDirectory, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
var localFfprobeExecutable = Path.Combine(localFfmpegDirectory, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
if (File.Exists(localFfmpegExecutable) && File.Exists(localFfprobeExecutable))
{
    var currentPath = Environment.GetEnvironmentVariable("PATH");
    Environment.SetEnvironmentVariable("PATH", string.IsNullOrEmpty(currentPath) ? localFfmpegDirectory : $"{localFfmpegDirectory}{Path.PathSeparator}{currentPath}");
}

using var webEndpointConfiguration = builder.Environment.IsDevelopment()
    ? null
    : WebEndpointConfigurator.Load(builder.Configuration);

// Configure Kestrel to optionally enable HTTPS when a valid certificate is available.
if (webEndpointConfiguration != null)
{
    builder.WebHost.ConfigureKestrel(serverOptions => WebEndpointConfigurator.Configure(serverOptions, webEndpointConfiguration));
}

var configuredXmltvPath = builder.Configuration[AppConstants.XmltvPathConfigKey];
transientDataStore.DeleteInactiveOwnerDirectories();
FactoryResetCoordinator.ApplyPendingReset(appDataStore, configuredXmltvPath, transientDataStore, xmltvPublicationStore);
DataProtectionService.Configure(builder.Services, appDataStore);
var logging = LoggingBootstrapper.Configure(builder, appDataStore);
builder.Services.AddSingleton<ILogEventStore>(logging.EventStore);
builder.Services.AddSingleton(logging.RuntimeState);
builder.Services.AddSingleton<LogFileService>();

// Add application settings service (must be registered before services that depend on it)
builder.Services.AddSingleton<AppSettingsService>();
builder.Services.AddSingleton<IAppSettingsService>(provider => provider.GetRequiredService<AppSettingsService>());
builder.Services.AddSingleton<IEpgRetentionPolicy>(provider => provider.GetRequiredService<AppSettingsService>());
builder.Services.AddSingleton<IApplicationRestartService, ApplicationRestartService>();
builder.Services.AddSingleton<IFactoryResetService, FactoryResetService>();

// Register dynamic device address provider (uses settings service)
builder.Services.AddSingleton<IDeviceAddressProvider, SettingsDeviceAddressProvider>();

// Add timezone service (resolves from settings ? TZ env var ? UTC)
builder.Services.AddSingleton<ITimeZoneService, TimeZoneService>();

// Add EPG Core services (device address from settings, not config)
builder.Services.AddSingleton<IChannelLineupProvider, SettingsChannelLineupProvider>();
builder.Services.AddEpgCore(databasePath: appDataStore.DatabasePath, appDataStore: appDataStore);
builder.Services.AddSingleton<IDeviceAuthProvider, SettingsDeviceAuthProvider>();

// Add HDHomeRun device control service
builder.Services.AddHttpClient(HDHomeRunHttpControlFactory.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton<IHDHomeRunHttpControlFactory, HDHomeRunHttpControlFactory>();
builder.Services.AddSingleton<HDHomeRunService>();

// Add device state service (holds device info and tuner status, auto-refreshes)
builder.Services.AddSingleton<IDeviceStateService, DeviceStateService>();
builder.Services.AddSingleton<VirtualDeviceStatusCache>();
builder.Services.AddSingleton<IHdHomeRunProxyProfileProvider, HdHomeRunProxyProfileProvider>();
builder.Services.AddSingleton<IHdHomeRunProxyDeviceClient, HdHomeRunProxyDeviceClient>();

// Add background service for device auto-refresh (every 10 min for device, 30 sec for tuners)
builder.Services.AddHostedService<DeviceRefreshService>();

// Add opt-in network discovery listeners for the virtual HDHomeRun device
builder.Services.AddHostedService<HdHomeRunDiscoveryService>();
builder.Services.AddHostedService<SsdpDiscoveryService>();

// Add auto-fetch state service (singleton so it can be shared between background service and UI)
builder.Services.AddSingleton<IAutoFetchStateService, AutoFetchStateService>();

// Add background service for automatic EPG fetching
builder.Services.AddHostedService<EpgAutoFetchService>();

// Add HttpClient for stream proxying
builder.Services.AddHttpClient("StreamProxy")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        // Allow streaming without buffering
        MaxConnectionsPerServer = 10
    });
builder.Services.AddHttpClient("HdHomeRunProxyDevice");

builder.Services.AddSingleton<IMpegTsTranscodeService, MpegTsTranscodeService>();
builder.Services.AddSingleton<IMediaProbeService, MediaProbeService>();
builder.Services.AddSingleton<IActiveStreamRegistry, ActiveStreamRegistry>();
builder.Services.AddSingleton(statusRuntime);
builder.Services.AddScoped<StatusApiService>();
builder.Services.AddSingleton<IProtectedContentSlateService, ProtectedContentSlateService>();
builder.Services.AddSingleton<ITunerStreamMultiplexer, TunerStreamMultiplexer>();
builder.Services.AddSingleton<ITunerCapacityLeaseRegistry, TunerCapacityLeaseRegistry>();
builder.Services.AddSingleton<SubtitleSidecarService>();
builder.Services.AddHostedService<StreamShutdownService>();

// Add controllers for API endpoints (stream proxy)
builder.Services.AddControllers();

// Add Razor Components with Interactive Server rendering
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();
if (webEndpointConfiguration is { HttpsStatus: HttpsEndpointStatus.Enabled })
{
    app.Logger.LogInformation(
        "Web endpoints configured with HTTP on port {HttpPort} and HTTPS on port {HttpsPort}",
        webEndpointConfiguration.HttpPort,
        webEndpointConfiguration.HttpsPort);
}
else if (webEndpointConfiguration is { HttpsStatus: HttpsEndpointStatus.Unavailable })
{
    app.Logger.LogWarning(
        "HTTPS is unavailable ({HttpsConfigurationReason}); listening on HTTP port {HttpPort}",
        webEndpointConfiguration.Warning,
        webEndpointConfiguration.HttpPort);
}
else if (webEndpointConfiguration != null)
{
    app.Logger.LogInformation("HTTPS is not configured; listening on HTTP port {HttpPort}", webEndpointConfiguration.HttpPort);
}
foreach (var target in logging.RuntimeState.ExternalTargets.Where(target => target.Status == "Invalid"))
{
    app.Logger.LogWarning("{LoggingTarget} logging is not active because its startup configuration is invalid: {Reason}", target.Name, target.Message);
}

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseRouting();
app.UseMiddleware<ApiRequestLoggingMiddleware>();
app.UseAntiforgery();

// Map API controllers (stream proxy)
app.MapControllers();

// Minimal API for theme persistence from the header toggle (pure JS, no Blazor circuit)
app.MapPut("/api/theme/{theme}", async (string theme, IAppSettingsService settings) =>
{
    if (theme is not ("light" or "dark" or "auto"))
    {
        return Results.BadRequest("Theme must be 'light', 'dark', or 'auto'.");
    }

    await settings.UpdateAsync(s => s.Theme = theme);
    return Results.NoContent();
}).DisableAntiforgery();

app.MapGet("/api/runtime", () => Results.Ok(new { instanceId = statusRuntime.InstanceId })).DisableAntiforgery();

app.MapGet("/api/v1/status", async (HttpContext context, StatusApiService status, CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(await status.GetStatusAsync(cancellationToken));
}).DisableAntiforgery();

app.MapGet("/api/logs/files/{fileName}", (string fileName, LogFileService files) =>
{
    var stream = files.OpenRead(fileName);
    return stream == null ? Results.NotFound() : Results.File(stream, "text/plain", fileName);
}).DisableAntiforgery();

// Endpoint for external programs (Jellyfin, Plex, etc.) to download the XMLTV guide file
app.MapGet("/api/xmltv", async Task<IResult> (
    IAppSettingsService settings,
    EpgOrchestrator orchestrator,
    IXmltvPublicationStore publications,
    CancellationToken cancellationToken) =>
{
    var path = settings.Settings.XmltvOutputPath;
    if (!publications.Exists(path))
    {
        try
        {
            await orchestrator.GenerateEpgFromCacheAsync(settings.Settings.TargetDays, path, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return Results.NotFound(ex.Message);
        }
    }

    var stream = publications.OpenRead(path);
    if (stream == null)
    {
        return Results.NotFound("The XMLTV publication is not available.");
    }
    return Results.File(stream, "application/xml", "epg.xml");
}).DisableAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();