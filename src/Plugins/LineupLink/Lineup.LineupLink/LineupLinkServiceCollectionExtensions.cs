using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.LineupLink.Guides;
using Lineup.LineupLink.Physical;
using Lineup.LineupLink.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lineup.LineupLink;

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
