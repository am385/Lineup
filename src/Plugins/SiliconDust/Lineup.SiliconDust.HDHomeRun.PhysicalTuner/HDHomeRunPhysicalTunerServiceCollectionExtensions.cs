using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.SiliconDust.HDHomeRun.Device;
using Lineup.SiliconDust.HDHomeRun.Device.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lineup.SiliconDust.HDHomeRun.PhysicalTuner;

/// <summary>
/// Extension methods for registering the physical HDHomeRun adapter.
/// </summary>
public static class HDHomeRunPhysicalTunerServiceCollectionExtensions
{
    /// <summary>Gets the named HTTP client used for HDHomeRun device traffic.</summary>
    public const string HttpClientName = "Lineup.SiliconDust.HDHomeRun.PhysicalTuner";

    /// <summary>
    /// Adds the physical HDHomeRun tuner driver.
    /// </summary>
    /// <param name="services">Services to configure.</param>
    /// <returns>The supplied service collection.</returns>
    public static IServiceCollection AddHDHomeRunPhysicalTuner(this IServiceCollection services)
    {
        services.AddHttpClient(HttpClientName);
        services.AddHttpClient(HDHomeRunHttpControlFactory.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
        services.TryAddSingleton<IHDHomeRunHttpControlFactory, HDHomeRunHttpControlFactory>();
        services.TryAddSingleton<HDHomeRunService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPhysicalTunerDriver, HDHomeRunPhysicalTunerDriver>());
        return services;
    }
}
