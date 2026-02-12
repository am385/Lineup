using Lineup.HDHomeRun.Api;
using Lineup.HDHomeRun.Device;
using Lineup.Core.Converters;
using Lineup.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lineup.Core;

/// <summary>
/// Extension methods for configuring EPG Core services
/// </summary>
public static class CoreServiceExtensions
{
    /// <summary>
    /// Adds EPG Core services to the service collection.
    /// If an IDeviceAddressProvider is already registered, it will be used for dynamic device address resolution.
    /// Otherwise, a fixed address provider will be registered with the specified device address.
    /// </summary>
    /// <param name="services">Service collection to configure</param>
    /// <param name="deviceAddress">HDHomeRun device hostname or IP address (used only if no IDeviceAddressProvider is registered)</param>
    /// <param name="databasePath">Path to the SQLite database file</param>
    public static IServiceCollection AddEpgCore(
        this IServiceCollection services,
        string deviceAddress = AppConstants.DefaultDeviceAddress,
        string? databasePath = null)
    {
        // Register default device address provider if not already registered
        // TryAdd will only add if no IDeviceAddressProvider is already registered
        services.TryAddSingleton<IDeviceAddressProvider>(new FixedDeviceAddressProvider(deviceAddress));

        // Configure HttpClient for device service (no base address needed, provider handles it)
        services.AddHttpClient<HDHomeRunDeviceClient>();

        // Configure HttpClient for API service
        services.AddHttpClient<HDHomeRunApiClient>();

        // Register device auth provider
        services.AddSingleton<IDeviceAuthProvider, HDHomeRunDeviceAuthProvider>();

        // Configure EF Core with SQLite
        var dbPath = databasePath ?? AppConstants.DefaultDatabaseFileName;
        services.AddDbContext<EpgDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        // Register repository and data provider
        services.AddScoped<IEpgRepository, EpgRepository>();
        services.AddScoped<IEpgDataProvider, CachedEpgDataProvider>();

        // Register converter and orchestrator
        services.AddScoped<HDHomeRunToXmltvConverter>();
        services.AddScoped<EpgOrchestrator>();

        return services;
    }
}
