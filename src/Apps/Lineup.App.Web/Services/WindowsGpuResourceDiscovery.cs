namespace Lineup.App.Web.Services;

/// <summary>Discovers Windows GPU counter providers without starting collection.</summary>
internal static class WindowsGpuResourceDiscovery
{
    /// <summary>Discovers the Windows GPU engine provider without opening native counters.</summary>
    internal static IReadOnlyList<IGpuResourceSource> Discover(ILogger logger)
    {
        var sources = new List<IGpuResourceSource> { new WindowsGpuResourceSource(logger) };
        return sources;
    }
}
