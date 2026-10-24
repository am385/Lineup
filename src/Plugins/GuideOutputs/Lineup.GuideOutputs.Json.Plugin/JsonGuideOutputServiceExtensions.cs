using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lineup.GuideOutputs.Json.Plugin;

/// <summary>Registers JSON guide-output services.</summary>
public static class JsonGuideOutputServiceExtensions
{
    /// <summary>Adds the JSON guide-output driver and publisher.</summary>
    public static IServiceCollection AddJsonGuideOutput(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<JsonGuideOutputDriver>();
        services.TryAddSingleton<JsonGuidePublisher>();
        services.AddSingleton<IGuideOutputDriver>(provider => provider.GetRequiredService<JsonGuideOutputDriver>());
        services.AddSingleton<IGuidePublisher>(provider => provider.GetRequiredService<JsonGuidePublisher>());
        return services;
    }
}
