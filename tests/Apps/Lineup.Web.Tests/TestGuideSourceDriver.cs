using System.Text.Json;

namespace Lineup.Web.Tests;

/// <summary>Test guide-source driver used by Web settings tests.</summary>
internal sealed class TestGuideSourceDriver : IGuideSourceDriver
{
    /// <inheritdoc />
    public string Kind => "silicondust";

    /// <inheritdoc />
    public string DisplayName => "SiliconDust";

    /// <inheritdoc />
    public bool SupportsUserCreation => true;

    /// <inheritdoc />
    public AdapterConfiguration CreateDefaultConfiguration(GuideSourceId sourceId) =>
        new(Kind, JsonSerializer.SerializeToElement(new { }));

    /// <inheritdoc />
    public bool SupportsPhysicalAdapter(string physicalAdapterKind) =>
        string.Equals(physicalAdapterKind, "hdhomerun", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(AdapterConfiguration configuration) => [];

    /// <inheritdoc />
    public ValueTask<IGuideSource> CreateAsync(
        GuideSourceId sourceId,
        AdapterConfiguration configuration,
        IReadOnlyList<PhysicalDeviceId> physicalDeviceIds,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
