using Lineup.HDHomeRun.Device;
using Lineup.Core;
using Lineup.Web.Components;
using Lineup.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Resolve application data directory from configuration (supports appsettings.json + environment variables)
// Environment variable: Lineup__AppDataPath (maps to Lineup:AppDataPath)
var appDataPath = builder.Configuration[AppConstants.AppDataPathConfigKey] ?? Directory.GetCurrentDirectory();
if (!Directory.Exists(appDataPath))
{
    Directory.CreateDirectory(appDataPath);
}

// Add application settings service (must be registered before services that depend on it)
builder.Services.AddSingleton<IAppSettingsService>(sp =>
    new AppSettingsService(
        sp.GetRequiredService<ILogger<AppSettingsService>>(),
        Path.Combine(appDataPath, AppConstants.SettingsFileName)));

// Register dynamic device address provider (uses settings service)
builder.Services.AddSingleton<IDeviceAddressProvider, SettingsDeviceAddressProvider>();

// Add EPG Core services (device address from settings, not config)
var databasePath = Path.Combine(appDataPath, AppConstants.DefaultDatabaseFileName);
builder.Services.AddEpgCore(databasePath: databasePath);

// Add HDHomeRun device control service (uses native protocol)
builder.Services.AddSingleton<HDHomeRunService>();

// Add device state service (holds device info and tuner status, auto-refreshes)
builder.Services.AddSingleton<IDeviceStateService, DeviceStateService>();

// Add background service for device auto-refresh (every 10 min for device, 30 sec for tuners)
builder.Services.AddHostedService<DeviceRefreshService>();

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

// Add controllers for API endpoints (stream proxy)
builder.Services.AddControllers();

// Add Razor Components with Interactive Server rendering
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

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

// Endpoint for external programs (Jellyfin, Plex, etc.) to download the XMLTV guide file
app.MapGet("/api/xmltv", (IAppSettingsService settings) =>
{
    var path = Path.GetFullPath(settings.Settings.XmltvOutputPath);
    if (!File.Exists(path))
    {
        return Results.NotFound("No XMLTV file has been generated yet.");
    }

    var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    return Results.File(stream, "application/xml", "epg.xml");
}).DisableAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();