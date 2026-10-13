using Lineup.Common.Configuration;
using Lineup.Extensibility.Configuration;
using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;

namespace Lineup.Core.Streaming;

/// <summary>
/// Resolves registered physical, virtual, guide-source, and guide-output adapter drivers by stable kind.
/// </summary>
public interface IStreamingAdapterRegistry
{
    /// <summary>Gets a physical driver or throws an actionable configuration error.</summary>
    IPhysicalTunerDriver GetPhysical(string kind);

    /// <summary>Gets a virtual driver or throws an actionable configuration error.</summary>
    IVirtualTunerDriver GetVirtual(string kind);

    /// <summary>Gets a guide driver or throws an actionable configuration error.</summary>
    IGuideSourceDriver GetGuide(string kind);

    /// <summary>Gets a guide-output driver or throws an actionable configuration error.</summary>
    IGuideOutputDriver GetGuideOutput(string kind);

    /// <summary>Gets portable settings behavior for a physical driver.</summary>
    IAdapterSettingsProvider GetPhysicalSettings(string kind);

    /// <summary>Gets portable settings behavior for a virtual driver.</summary>
    IAdapterSettingsProvider GetVirtualSettings(string kind);

    /// <summary>Gets portable settings behavior for a guide driver.</summary>
    IAdapterSettingsProvider GetGuideSettings(string kind);

    /// <summary>Gets portable settings behavior for a guide-output driver.</summary>
    IAdapterSettingsProvider GetGuideOutputSettings(string kind);
}

/// <summary>
/// Immutable adapter registry assembled by the application composition root.
/// </summary>
public sealed class StreamingAdapterRegistry : IStreamingAdapterRegistry
{
    private readonly IReadOnlyDictionary<string, IPhysicalTunerDriver> _physical;
    private readonly IReadOnlyDictionary<string, IVirtualTunerDriver> _virtual;
    private readonly IReadOnlyDictionary<string, IGuideSourceDriver> _guides;
    private readonly IReadOnlyDictionary<string, IGuideOutputDriver> _guideOutputs;

    /// <summary>Initializes the registry from DI-provided drivers.</summary>
    public StreamingAdapterRegistry(
        IEnumerable<IPhysicalTunerDriver> physicalDrivers,
        IEnumerable<IVirtualTunerDriver> virtualDrivers,
        IEnumerable<IGuideSourceDriver> guideDrivers,
        IEnumerable<IGuideOutputDriver> guideOutputDrivers)
    {
        _physical = CreateIndex(physicalDrivers, driver => driver.Kind, "physical");
        _virtual = CreateIndex(virtualDrivers, driver => driver.Kind, "virtual");
        _guides = CreateIndex(guideDrivers, driver => driver.Kind, "guide");
        _guideOutputs = CreateIndex(guideOutputDrivers, driver => driver.Kind, "guide output");
    }

    /// <summary>Initializes a registry without guide-output drivers.</summary>
    public StreamingAdapterRegistry(
        IEnumerable<IPhysicalTunerDriver> physicalDrivers,
        IEnumerable<IVirtualTunerDriver> virtualDrivers,
        IEnumerable<IGuideSourceDriver> guideDrivers)
        : this(physicalDrivers, virtualDrivers, guideDrivers, [])
    {
    }

    /// <inheritdoc />
    public IPhysicalTunerDriver GetPhysical(string kind) => Get(_physical, kind, "physical");

    /// <inheritdoc />
    public IVirtualTunerDriver GetVirtual(string kind) => Get(_virtual, kind, "virtual");

    /// <inheritdoc />
    public IGuideSourceDriver GetGuide(string kind) => Get(_guides, kind, "guide");

    /// <inheritdoc />
    public IGuideOutputDriver GetGuideOutput(string kind) => Get(_guideOutputs, kind, "guide output");

    /// <inheritdoc />
    public IAdapterSettingsProvider GetPhysicalSettings(string kind) => GetSettingsProvider(GetPhysical(kind), kind, "physical");

    /// <inheritdoc />
    public IAdapterSettingsProvider GetVirtualSettings(string kind) => GetSettingsProvider(GetVirtual(kind), kind, "virtual");

    /// <inheritdoc />
    public IAdapterSettingsProvider GetGuideSettings(string kind) => GetSettingsProvider(GetGuide(kind), kind, "guide");

    /// <inheritdoc />
    public IAdapterSettingsProvider GetGuideOutputSettings(string kind) => GetSettingsProvider(GetGuideOutput(kind), kind, "guide output");

    private static IReadOnlyDictionary<string, TDriver> CreateIndex<TDriver>(IEnumerable<TDriver> drivers, Func<TDriver, string> getKind, string category)
    {
        var result = new Dictionary<string, TDriver>(StringComparer.OrdinalIgnoreCase);
        foreach (var driver in drivers)
        {
            var kind = getKind(driver);
            if (string.IsNullOrWhiteSpace(kind))
            {
                throw new InvalidOperationException($"A registered {category} adapter has an empty kind.");
            }
            if (!result.TryAdd(kind, driver))
            {
                throw new InvalidOperationException($"More than one {category} adapter is registered for kind '{kind}'.");
            }

            ValidateSettingsProvider(driver, kind, category);
        }
        return result;
    }

    private static TDriver Get<TDriver>(IReadOnlyDictionary<string, TDriver> drivers, string kind, string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        return drivers.TryGetValue(kind, out var driver)
            ? driver
            : throw new InvalidOperationException($"No {category} adapter is registered for kind '{kind}'.");
    }

    private static IAdapterSettingsProvider GetSettingsProvider(object driver, string kind, string category) =>
        driver as IAdapterSettingsProvider
            ?? throw new InvalidOperationException($"The {category} adapter '{kind}' does not expose portable settings.");

    private static void ValidateSettingsProvider<TDriver>(TDriver driver, string kind, string category)
    {
        if (driver is not IAdapterSettingsProvider provider)
        {
            return;
        }

        if (!string.Equals(provider.SettingsDescriptor.AdapterKind, kind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The {category} adapter '{kind}' exposes settings for kind '{provider.SettingsDescriptor.AdapterKind}'.");
        }

        var errors = AdapterSettingsValidator.ValidateDescriptor(provider.SettingsDescriptor);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"The {category} adapter '{kind}' exposes invalid settings: {string.Join(" ", errors)}");
        }
    }
}
