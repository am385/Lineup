using Lineup.Core.Common.Configuration;
using Lineup.Core.Extensibility.Configuration;
using Lineup.Core.Common.Guides;
using Lineup.Core.Extensibility.Guides;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Extensibility.Streaming;
using Lineup.Plugin.GuideOutputs.Xmltv;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lineup.Plugin.GuideOutputs.Xmltv.Plugin;

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
