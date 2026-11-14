using Lineup.Core.Extensibility.Guides;
using Lineup.Core.Extensibility.Streaming;
using Lineup.Plugin.LineupLink.Guides;
using Lineup.Plugin.LineupLink.Physical;
using Lineup.Plugin.LineupLink.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lineup.Plugin.LineupLink;

/// <summary>Registers Lineup Link adapters.</summary>
public static class LineupLinkServiceCollectionExtensions
{
    /// <summary>Registers the Lineup Link physical tuner adapter.</summary>
    public static IServiceCollection AddLineupLinkPhysicalTuner(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPhysicalTunerDriver, LineupLinkPhysicalTunerDriver>());
        return services;
    }

    /// <summary>Registers the Lineup Link virtual tuner adapter.</summary>
    public static IServiceCollection AddLineupLinkVirtualTuner(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IVirtualTunerDriver, LineupLinkVirtualTunerDriver>());
        return services;
    }

    /// <summary>Registers the Lineup Link guide-source adapter.</summary>
    public static IServiceCollection AddLineupLinkGuideSource(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IGuideSourceDriver, LineupLinkGuideSourceDriver>());
        return services;
    }
}
