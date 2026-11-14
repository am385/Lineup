using System.Text.Json;

namespace Lineup.App.Web.Tests;

/// <summary>Test guide-source driver used by Web settings tests.</summary>
internal sealed class TestGuideSourceDriver : IGuideSourceDriver
{
    /// <summary>Gets the kind.</summary>
    public string Kind => "silicondust";

    /// <summary>Gets the display name.</summary>
    public string DisplayName => "SiliconDust";

    /// <summary>Gets the supports user creation.</summary>
    public bool SupportsUserCreation => true;

    /// <summary>Creates default configuration.</summary>
    /// <param name="sourceId">The source ID value.</param>
    /// <returns>The operation's result.</returns>
    public AdapterConfiguration CreateDefaultConfiguration(GuideSourceId sourceId) =>
        new(Kind, JsonSerializer.SerializeToElement(new { }));

    /// <summary>Determines support for physical adapter.</summary>
    /// <param name="physicalAdapterKind">The physical adapter kind value.</param>
    /// <returns>The operation's result.</returns>
    public bool SupportsPhysicalAdapter(string physicalAdapterKind) =>
        string.Equals(physicalAdapterKind, "hdhomerun", StringComparison.OrdinalIgnoreCase);

    /// <summary>Validates the test guide source driver.</summary>
    /// <param name="configuration">Configuration to apply.</param>
    /// <returns>The operation's result.</returns>
    public IReadOnlyList<string> Validate(AdapterConfiguration configuration) => [];

    /// <summary>Creates the test guide source driver asynchronously.</summary>
    /// <param name="sourceId">The source ID value.</param>
    /// <param name="configuration">Configuration to apply.</param>
    /// <param name="physicalDeviceIds">The physical device IDs value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The operation's result.</returns>
    public ValueTask<IGuideSource> CreateAsync(GuideSourceId sourceId, AdapterConfiguration configuration, IReadOnlyList<PhysicalDeviceId> physicalDeviceIds, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
