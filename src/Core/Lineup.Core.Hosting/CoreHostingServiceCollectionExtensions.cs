using Microsoft.Extensions.DependencyInjection;

namespace Lineup.Core.Hosting;

/// <summary>
/// Registers reusable Lineup runtime workers.
/// </summary>
public static class CoreHostingServiceCollectionExtensions
{
    /// <summary>
    /// Registers physical-device status and information refresh workers.
    /// </summary>
    public static IServiceCollection AddLineupPhysicalDeviceRefreshWorkers(this IServiceCollection services)
    {
        services.AddHostedService<PhysicalDeviceRefreshService>();
        services.AddHostedService<PhysicalDeviceInformationRefreshService>();
        return services;
    }

    /// <summary>
    /// Registers streaming-topology activation.
    /// </summary>
    public static IServiceCollection AddLineupStreamingTopologyRuntime(this IServiceCollection services)
    {
        services.AddHostedService<StreamingTopologyRuntime>();
        return services;
    }

    /// <summary>
    /// Registers automatic guide fetch and publication scheduling.
    /// </summary>
    public static IServiceCollection AddLineupEpgAutoFetchWorker(this IServiceCollection services)
    {
        services.AddHostedService<EpgAutoFetchService>();
        return services;
    }

    /// <summary>Registers coordinated active-stream and shared-producer shutdown.</summary>
    public static IServiceCollection AddLineupStreamShutdownWorker(this IServiceCollection services)
    {
        services.AddHostedService<StreamShutdownService>();
        return services;
    }
}
