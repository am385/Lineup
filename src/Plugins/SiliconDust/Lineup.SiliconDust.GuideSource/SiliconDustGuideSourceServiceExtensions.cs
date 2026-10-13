using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lineup.SiliconDust.GuideSource;

/// <summary>
/// Registers SiliconDust guide-source services.
/// </summary>
public static class SiliconDustGuideSourceServiceExtensions
{
    /// <summary>Gets the named HTTP client used for SiliconDust guide traffic.</summary>
    public const string HttpClientName = "Lineup.SiliconDust.GuideSource";

    /// <summary>
    /// Adds the SiliconDust guide-source driver.
    /// </summary>
    /// <param name="services">The service collection to update.</param>
    /// <returns>The supplied service collection.</returns>
    public static IServiceCollection AddSiliconDustGuideSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                    System.Net.DecompressionMethods.Deflate |
                    System.Net.DecompressionMethods.Brotli
            });
        services.TryAddSingleton<SiliconDustXmltvParser>();
        services.TryAddSingleton<ISiliconDustDeviceAuthorizationProvider, PhysicalDeviceSiliconDustAuthorizationProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IGuideSourceDriver, SiliconDustGuideSourceDriver>());
        return services;
    }
}
