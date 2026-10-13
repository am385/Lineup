using Lineup.Common.Configuration;
using Lineup.Extensibility.Configuration;
using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.GuideOutputs.Xmltv;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lineup.GuideOutputs.Xmltv.Plugin;

/// <summary>
/// Registers XMLTV guide-output services.
/// </summary>
public static class XmltvGuideOutputServiceExtensions
{
    /// <summary>Adds the XMLTV guide-output driver and built-in output normalizer.</summary>
    public static IServiceCollection AddXmltvGuideOutput(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<XmltvGuideOutputDriver>();
        services.TryAddSingleton<XmltvGuidePublisher>();
        services.AddSingleton<IGuideOutputDriver>(
            provider => provider.GetRequiredService<XmltvGuideOutputDriver>());
        services.AddSingleton<IGuidePublisher>(
            provider => provider.GetRequiredService<XmltvGuidePublisher>());
        services.AddSingleton<IStreamingConfigurationNormalizer>(
            provider => provider.GetRequiredService<XmltvGuideOutputDriver>());
        return services;
    }
}
