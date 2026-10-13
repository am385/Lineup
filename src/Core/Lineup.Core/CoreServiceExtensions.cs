using Lineup.Core.Storage;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
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
        services.AddSingleton(provider => new DeviceGuideStore(provider.GetRequiredService<IDbContextFactory<EpgDbContext>>()));
        services.AddSingleton<IPhysicalCapacityLeaseRegistry, PhysicalCapacityLeaseRegistry>();
        services.AddSingleton<IPhysicalLineupService, PhysicalLineupService>();
        services.AddSingleton<IPhysicalDeviceStatusService, PhysicalDeviceStatusService>();
        services.AddSingleton<IStreamingSessionGraph, StreamingSessionGraph>();
        services.AddSingleton<IStreamingAdapterRegistry, StreamingAdapterRegistry>();
        services.AddSingleton<PhysicalDeviceRuntimeState>();
        services.AddSingleton<IPhysicalDeviceResolver>(provider => provider.GetRequiredService<PhysicalDeviceRuntimeState>());
        services.AddSingleton<IPhysicalDeviceCatalog>(provider => new PhysicalDeviceCatalog(
            provider.GetRequiredService<IStreamingAdapterRegistry>(),
            provider.GetRequiredService<PhysicalDeviceRuntimeState>(),
            provider.GetService<Microsoft.Extensions.Logging.ILogger<PhysicalDeviceCatalog>>()));
        services.AddSingleton<IGuideOrchestrator, GuideOrchestrator>();
        services.AddSingleton<IGuideQueryService, GuideQueryService>();
        services.TryAddSingleton<IEpgRetentionPolicy, DefaultEpgRetentionPolicy>();

        services.AddSingleton<GuideSnapshotProjector>();
        services.TryAddSingleton<IXmltvPublicationStore, XmltvPublicationStore>();
        services.AddSingleton<GuideGenerationCoordinator>();

        return services;
    }
}
