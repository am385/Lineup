using Lineup;
using Lineup.GuideOutputs.Json.Plugin;
using Lineup.LineupLink;
using Lineup.Web.Components;
using Lineup.Web.Services;
using Lineup.GuideOutputs.Xmltv.Plugin;
using Lineup.SiliconDust.HDHomeRun.DiscoveryRuntime;
using Lineup.VirtualOutputs.Protocol;
using Lineup.VirtualOutputs.Runtime;
using Scalar.AspNetCore;

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
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

// Prefer the verified repo-local Jellyfin FFmpeg installed by scripts/Install-JellyfinFfmpeg.ps1.
var sourceProjectPath = Path.Combine(builder.Environment.ContentRootPath, "Lineup.Web.csproj");
var repositoryRoot = FindRepositoryRoot(builder.Environment.ContentRootPath);
var localFfmpegDirectory = File.Exists(sourceProjectPath) && repositoryRoot is not null
    ? Path.Combine(repositoryRoot, ".ffmpeg")
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

var configuredGuideOutputDirectory = builder.Configuration[AppConstants.GuideOutputDirConfigKey];
FactoryResetCoordinator.ApplyPendingReset(appDataStore, configuredGuideOutputDirectory, transientDataStore, xmltvPublicationStore);
var startupState = StartupSchemaGate.Check(appDataStore);
builder.Services.AddSingleton(startupState);
if (!startupState.IsFactoryResetRequired)
{
    transientDataStore.DeleteInactiveOwnerDirectories();
}
DataProtectionService.Configure(builder.Services, appDataStore);
var logging = LoggingBootstrapper.Configure(builder, appDataStore);
builder.Services.AddSingleton<ILogEventStore>(logging.EventStore);
builder.Services.AddSingleton(logging.RuntimeState);
builder.Services.AddSingleton<LogFileService>();
builder.Services.AddSingleton<WebSettingsService>();
builder.Services.AddSingleton<IWebSettingsService>(provider => provider.GetRequiredService<WebSettingsService>());

// Add application settings service (must be registered before services that depend on it)
builder.Services.AddSingleton<LineupSettingsService>();
builder.Services.AddSingleton<ILineupSettingsService>(provider => provider.GetRequiredService<LineupSettingsService>());
builder.Services.AddSingleton<IEpgRetentionPolicy>(provider => provider.GetRequiredService<LineupSettingsService>());
builder.Services.AddSingleton<IApplicationRestartService, ApplicationRestartService>();
builder.Services.AddSingleton<IFactoryResetService, FactoryResetService>();

// Add timezone service (resolves from settings ? TZ env var ? UTC)
builder.Services.AddSingleton<ITimeZoneService, TimeZoneService>();

// Add shared streaming and guide services.
builder.Services.AddEpgCore(databasePath: appDataStore.DatabasePath, appDataStore: appDataStore);
builder.Services.AddXmltvGuideOutput();
builder.Services.AddJsonGuideOutput();
builder.Services.AddHDHomeRunPhysicalTuner();
builder.Services.AddSiliconDustGuideSource();
builder.Services.AddLineupLinkPhysicalTuner();
builder.Services.AddLineupLinkVirtualTuner();
builder.Services.AddLineupLinkGuideSource();
builder.Services.AddSingleton<HdHomeRunVirtualTunerDriver>();
builder.Services.AddSingleton<IVirtualTunerDriver>(provider => provider.GetRequiredService<HdHomeRunVirtualTunerDriver>());
builder.Services.AddSingleton<WebPlayerVirtualTunerDriver>();
builder.Services.AddSingleton<IVirtualTunerDriver>(provider => provider.GetRequiredService<WebPlayerVirtualTunerDriver>());
builder.Services.AddSingleton<IStreamingConfigurationNormalizer>(provider => provider.GetRequiredService<WebPlayerVirtualTunerDriver>());

builder.Services.AddSingleton<IHdHomeRunProxyProfileProvider>(services =>
    new HdHomeRunProxyProfileProvider(
        () => services.GetRequiredService<ILineupSettingsService>().Settings.Streaming.VirtualDevices,
        services.GetRequiredService<IPhysicalDeviceResolver>(),
        services.GetRequiredService<HdHomeRunVirtualTunerDriver>()));

// Add background service for device auto-refresh (every 10 min for device, 30 sec for tuners)
if (!startupState.IsFactoryResetRequired)
{
    builder.Services.AddLineupPhysicalDeviceRefreshWorkers();
    builder.Services.AddLineupStreamingTopologyRuntime();
}

// Add auto-fetch state service (singleton so it can be shared between background service and UI)
builder.Services.AddSingleton<IAutoFetchStateService, AutoFetchStateService>();
builder.Services.AddSingleton<ITopologyGuideService, TopologyGuideService>();
builder.Services.AddSingleton<ILineupGuideReader, LineupGuideReader>();

// Add background service for automatic EPG fetching
if (!startupState.IsFactoryResetRequired)
{
    builder.Services.AddLineupEpgAutoFetchWorker();
}

// Add HttpClient for stream proxying
builder.Services.AddHttpClient("StreamProxy")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        // Allow streaming without buffering
        MaxConnectionsPerServer = 10
    });

builder.Services.AddLineupMpegTsStreaming();
builder.Services.AddSingleton<IStreamingFlowSnapshotService, StreamingFlowSnapshotService>();
builder.Services.AddSingleton(statusRuntime);
builder.Services.AddScoped<StatusApiService>();
builder.Services.AddSingleton<IProtectedContentSlateService, ProtectedContentSlateService>();
builder.Services.AddSingleton<ICmafCompatibilityTestService, CmafCompatibilityTestService>();
builder.Services.AddSingleton<ICmafOutputCoordinator, CmafOutputCoordinator>();
builder.Services.AddSingleton<IStreamingProducerDiagnosticsContributor>(
    services => services.GetRequiredService<ICmafOutputCoordinator>());
builder.Services.AddSingleton<SubtitleSidecarService>();
if (!startupState.IsFactoryResetRequired)
{
    builder.Services.AddLineupStreamShutdownWorker();
    builder.Services.AddLineupVirtualOutputRuntime(CreateVirtualOutputRuntimeOptions(builder.Configuration, transientDataStore));
    builder.Services.AddSingleton<IHdHomeRunDiscoveryPolicy, WebHdHomeRunDiscoveryPolicy>();
    builder.Services.AddHdHomeRunDiscoveryRuntime();
}

// Add controllers for API endpoints (stream proxy)
builder.Services.AddControllers();
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "Lineup API";
        document.Info.Description = "HTTP APIs exposed by the Lineup management host and its configured virtual outputs.";
        return Task.CompletedTask;
    });
    options.AddOperationTransformer((operation, _, _) =>
    {
        if (string.IsNullOrWhiteSpace(operation.Description))
        {
            operation.Description = operation.Summary;
        }
        return Task.CompletedTask;
    });
});

// Add Razor Components with Interactive Server rendering
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();
var reverseProxyConfiguration = ReverseProxyConfigurator.Load(app.Services.GetRequiredService<ILineupSettingsService>().Settings);
if (reverseProxyConfiguration.Enabled)
{
    app.UseForwardedHeaders(reverseProxyConfiguration.Options);
    app.Logger.LogInformation(
        "Trusted reverse-proxy metadata is enabled for the management listener with forward limit {ForwardLimit} and {KnownProxyCount} configured proxies",
        reverseProxyConfiguration.Options.ForwardLimit,
        reverseProxyConfiguration.ConfiguredProxyCount);
}
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
app.Use(UiLocalization.InvokeAsync);
if (startupState.IsFactoryResetRequired)
{
    app.Use(async (context, next) =>
    {
        var action = FactoryResetRecoveryRequestPolicy.GetAction(context.Request.Path);
        if (action == FactoryResetRecoveryRequestAction.RedirectToSettings)
        {
            context.Response.Redirect("/settings");
            return;
        }

        if (action == FactoryResetRecoveryRequestAction.RejectApi)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { status = "factoryResetRequired" });
            return;
        }

        await next();
    });
}
app.UseMiddleware<ApiRequestLoggingMiddleware>();
app.UseAntiforgery();

// Map API controllers (stream proxy)
app.MapControllers();
app.MapOpenApi()
    .DisableAntiforgery();
app.MapScalarApiReference("/api-docs", options =>
{
    options
        .WithTitle("Lineup API")
        .WithOpenApiRoutePattern("/openapi/{documentName}.json")
        .SortTagsAlphabetically()
        .SortOperationsByMethod()
        .DisableAgent();
});

// Minimal API for theme persistence from the header toggle (pure JS, no Blazor circuit)
app.MapPut("/api/theme/{theme}", async (string theme, IWebSettingsService settings) =>
{
    if (theme is not ("light" or "dark" or "auto"))
    {
        return Results.BadRequest("Theme must be 'light', 'dark', or 'auto'.");
    }

    await settings.UpdateAsync(s => s.Theme = theme);
    return Results.NoContent();
})
    .DisableAntiforgery()
    .WithName("SetUiTheme")
    .WithTags("Appearance")
    .WithSummary("Set the Web UI theme")
    .WithDescription("Persists the light, dark, or automatic theme used by the Lineup Web UI.");

app.MapGet("/api/runtime", () => Results.Ok(new { instanceId = statusRuntime.InstanceId }))
    .DisableAntiforgery()
    .WithName("GetRuntime")
    .WithTags("System")
    .WithSummary("Get the runtime instance identifier")
    .WithDescription("Returns an identifier that changes whenever the Lineup Web host restarts.");

app.MapGet("/api/v1/status", async (HttpContext context, StatusApiService status, CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(await status.GetStatusAsync(cancellationToken));
})
    .DisableAntiforgery()
    .WithName("GetStatus")
    .WithTags("System")
    .WithSummary("Get the current Lineup status")
    .WithDescription("Returns the current system, device, guide, channel, and active-stream status using the configured privacy redaction settings.");

app.MapGet("/api/logs/files/{fileName}", (string fileName, LogFileService files) =>
{
    var stream = files.OpenRead(fileName);
    return stream == null ? Results.NotFound() : Results.File(stream, "text/plain", fileName);
})
    .DisableAntiforgery()
    .WithName("DownloadLogFile")
    .WithTags("Logs")
    .WithSummary("Download a rolling log file")
    .WithDescription("Returns one available rolling log file as plain text.");

// Stable endpoint for the built-in global XMLTV output.
app.MapGet("/api/xmltv", async Task<IResult> (
    ITopologyGuideService guides,
    ILineupSettingsService settings,
    IStreamingAdapterRegistry adapters,
    IXmltvPublicationStore publications,
    bool? download,
    CancellationToken cancellationToken) =>
{
    var output = settings.Settings.Streaming.GuideOutputs.SingleOrDefault(candidate => candidate.IsBuiltIn && candidate.Enabled);
    return output is null
        ? Results.NotFound("The built-in XMLTV guide output is not enabled.")
        : await ServeGuideOutputAsync(output, guides, adapters, publications, download ?? true, cancellationToken);
})
    .DisableAntiforgery()
    .WithName("GetGlobalXmltvGuideOutput")
    .WithTags("Guide Outputs")
    .WithSummary("Get the global XMLTV guide output")
    .WithDescription("Returns the built-in XMLTV output inline or as a file attachment. The download parameter defaults to true.");

app.MapGet("/api/guide-outputs/{guideOutputIdentifier}", async Task<IResult> (
    string guideOutputIdentifier,
    ITopologyGuideService guides,
    ILineupSettingsService settings,
    IStreamingAdapterRegistry adapters,
    IXmltvPublicationStore publications,
    bool? download,
    CancellationToken cancellationToken) =>
{
    var output = GuideOutputIdentifierResolver.Resolve(
        settings.Settings.Streaming.GuideOutputs,
        guideOutputIdentifier);
    return output is null
        ? Results.NotFound()
        : await ServeGuideOutputAsync(output, guides, adapters, publications, download ?? true, cancellationToken);
})
    .DisableAntiforgery()
    .WithName("GetGuideOutput")
    .WithTags("Guide Outputs")
    .WithSummary("Get a configured guide output")
    .WithDescription("Returns a guide output selected by its editable key or stable identifier, inline or as a file attachment. The download parameter defaults to true.");

static async Task<IResult> ServeGuideOutputAsync(
    GuideOutputDefinition output,
    ITopologyGuideService guides,
    IStreamingAdapterRegistry adapters,
    IXmltvPublicationStore publications,
    bool download,
    CancellationToken cancellationToken)
{
    GuideOutputDestination destination;
    try
    {
        destination = adapters.GetGuideOutput(output.Adapter.Kind).ResolveDestination(output.Adapter);
    }
    catch (InvalidOperationException exception)
    {
        return Results.NotFound(exception.Message);
    }

    if (!publications.Exists(destination.Path))
    {
        var publication = await guides.PublishAsync(output.Id, cancellationToken);
        if (publication is null || !publication.Succeeded)
        {
            return Results.NotFound(publication?.Error ?? "The guide output is not available.");
        }
    }

    var stream = publications.OpenRead(destination.Path);
    if (stream is null)
    {
        return Results.NotFound("The guide output publication is not available.");
    }
    return download
        ? Results.File(stream, destination.ContentType, destination.FileName)
        : Results.File(stream, destination.ContentType);
}

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static VirtualOutputRuntimeOptions CreateVirtualOutputRuntimeOptions(
    IConfiguration configuration,
    ITransientDataStore transientDataStore)
{
    var endpoint = OperatingSystem.IsWindows()
        ? new VirtualOutputIpcEndpoint
        {
            Transport = VirtualOutputIpcTransport.NamedPipe,
            Address = $"lineup-virtual-outputs-{Environment.ProcessId}"
        }
        : new VirtualOutputIpcEndpoint
        {
            Transport = VirtualOutputIpcTransport.UnixDomainSocket,
            Address = Path.Combine(transientDataStore.RootPath, $"virtual-outputs-{Environment.ProcessId}.sock")
        };
    var configuredWorkerPath = configuration[AppConstants.VirtualOutputWorkerPathConfigKey];
    return new VirtualOutputRuntimeOptions
    {
        WorkerPackageRoot = string.IsNullOrWhiteSpace(configuredWorkerPath)
            ? Path.Combine(AppContext.BaseDirectory, "virtual-output-workers")
            : Path.GetFullPath(configuredWorkerPath),
        IpcEndpoint = endpoint
    };
}

static string? FindRepositoryRoot(string startPath)
{
    var directory = new DirectoryInfo(startPath);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lineup.slnx")))
    {
        directory = directory.Parent;
    }
    return directory?.FullName;
}