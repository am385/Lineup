namespace Lineup.App.Web.Services;

/// <summary>Discovers Linux device telemetry without enumerating processes or opening media contexts.</summary>
internal static class LinuxGpuResourceDiscovery
{
    /// <summary>Discovers NVIDIA, AMD sysfs and Intel i915 PMU providers exposed by the host/container.</summary>
    internal static IReadOnlyList<IGpuResourceSource> Discover(ILogger logger)
    {
        var sources = new List<IGpuResourceSource> { new NvidiaGpuResourceSource() };
        const string drm = "/sys/class/drm";
        if (Directory.Exists(drm))
        {
            foreach (var card in Directory.EnumerateDirectories(drm).Where(path => IsCard(Path.GetFileName(path))).Order(StringComparer.Ordinal).Take(16))
            {
                var vendorPath = Path.Combine(card, "device", "vendor");
                if (!File.Exists(vendorPath)) continue;
                var vendor = File.ReadAllText(vendorPath).Trim();
                if (vendor == "0x1002") sources.Add(new AmdGpuResourceSource(card));
            }
        }
        const string pmu = "/sys/bus/event_source/devices";
        if (Directory.Exists(pmu))
        {
            foreach (var path in Directory.EnumerateDirectories(pmu).Where(path => Path.GetFileName(path).StartsWith("i915", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Take(16))
            {
                sources.Add(new IntelGpuResourceSource(path));
            }
        }
        return sources;
    }

    private static bool IsCard(string name) => name.StartsWith("card", StringComparison.Ordinal) && name.Length > 4 && name.AsSpan(4).IndexOfAnyExceptInRange('0', '9') < 0;
}
