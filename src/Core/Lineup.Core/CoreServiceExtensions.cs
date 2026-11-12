using Lineup.Core.Storage;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Extensibility.Streaming;
using Lineup.Core.Common.Guides;
using Lineup.Core.Extensibility.Guides;
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
    /// Adds provider-neutral Lineup Core services to the service collection.
    /// </summary>
    /// <param name="services">Service collection to configure</param>
    /// <param name="databasePath">Path to the SQLite database file</param>
    /// <param name="appDataStore">Optional persistent application-data boundary for Lineup-owned file artifacts.</param>
    public static IServiceCollection AddEpgCore(
        this IServiceCollection services,
        string? databasePath = null,
        IAppDataStore? appDataStore = null)
    {
        // Configure EF Core with SQLite
        var dbPath = databasePath ?? AppConstants.DefaultDatabaseFileName;
        services.AddDbContextFactory<EpgDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));
        services.AddSingleton(provider => new StreamingTopologyStore(provider.GetRequiredService<IDbContextFactory<EpgDbContext>>()));
        services.AddSingleton<DeviceGuideStore>();
        services.AddSingleton<IPhysicalCapacityLeaseRegistry, PhysicalCapacityLeaseRegistry>();
        services.TryAddSingleton(provider => StreamingResourceOptions.Load(provider.GetService<Microsoft.Extensions.Configuration.IConfiguration>()));
        services.TryAddSingleton<MediaWorkAdmission>();
        services.TryAddSingleton<Lineup.Core.Common.Streaming.IMediaWorkAdmission>(provider => provider.GetRequiredService<MediaWorkAdmission>());
        services.TryAddSingleton(provider => PhysicalDeviceRefreshScheduler.Load(provider.GetService<Microsoft.Extensions.Configuration.IConfiguration>()));
        services.AddSingleton<IPluginConnectionBudget>(provider => new PluginConnectionBudget(provider.GetRequiredService<Lineup.Core.Settings.ILineupSettingsService>()));
        services.AddSingleton<IPhysicalLineupService, PhysicalLineupService>();
        services.AddSingleton<IPhysicalDeviceStatusService, PhysicalDeviceStatusService>();
        services.AddSingleton<IStreamingSessionGraph>(provider => new StreamingSessionGraph(
            provider.GetRequiredService<IPhysicalCapacityLeaseRegistry>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<StreamingSessionGraph>>(),
            TimeProvider.System,
            provider.GetRequiredService<PhysicalDeviceRuntimeState>(),
            provider.GetRequiredService<StreamingResourceOptions>(),
            provider.GetRequiredService<Lineup.Core.Common.Streaming.IMediaWorkAdmission>()));
        services.AddSingleton<IStreamingAdapterRegistry, StreamingAdapterRegistry>();
        services.AddSingleton<PhysicalDeviceRuntimeState>();
        services.AddSingleton<IPhysicalDeviceResolver>(provider => provider.GetRequiredService<PhysicalDeviceRuntimeState>());
        services.AddSingleton<IPhysicalDeviceCatalog>(provider => new PhysicalDeviceCatalog(
            provider.GetRequiredService<IStreamingAdapterRegistry>(),
            provider.GetRequiredService<PhysicalDeviceRuntimeState>(),
            provider.GetService<Microsoft.Extensions.Logging.ILogger<PhysicalDeviceCatalog>>(),
            provider.GetRequiredService<PhysicalDeviceRefreshScheduler>()));
        services.AddSingleton<IGuideOrchestrator, GuideOrchestrator>();
        services.AddSingleton<IGuideQueryService, GuideQueryService>();
        services.TryAddSingleton<IEpgRetentionPolicy, DefaultEpgRetentionPolicy>();

        services.AddSingleton<GuideSnapshotProjector>();
        services.TryAddSingleton<IXmltvPublicationStore, XmltvPublicationStore>();
        services.AddSingleton<GuideGenerationCoordinator>();

        return services;
    }
}
