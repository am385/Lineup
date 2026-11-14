using System.Text.Json;
using Lineup.Core.Common.Configuration;
using Lineup.Core.Extensibility.Configuration;
using Lineup.Core.Common.Guides;
using Lineup.Core.Extensibility.Guides;
using Lineup.Core.Common.Streaming;
using Lineup.Plugin.SiliconDust.HDHomeRun.Api;
using Microsoft.Extensions.Logging;

namespace Lineup.Plugin.SiliconDust.GuideSource;

/// <summary>
/// Creates SiliconDust guide sources backed by one or more HDHomeRun devices.
/// </summary>
public sealed class SiliconDustGuideSourceDriver : IGuideSourceDriver, IAdapterSettingsProvider, IGuideSourceRefreshPolicy
{
    /// <summary>Gets the stable adapter kind.</summary>
    public const string DriverKind = "silicondust";

    /// <summary>Gets refresh interval.</summary>
    /// <param name="configuredInterval">The configured interval value.</param>
    /// <returns>The operation's result.</returns>
    public TimeSpan GetRefreshInterval(TimeSpan configuredInterval) => TimeSpan.FromHours(20) + TimeSpan.FromMinutes(Random.Shared.Next(0, 481));

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ISiliconDustDeviceAuthorizationProvider _authorizationProvider;
    private readonly SiliconDustXmltvParser _parser;

    /// <summary>
    /// Initializes a SiliconDust guide-source driver.
    /// </summary>
    /// <param name="httpClientFactory">Creates clients for SiliconDust API requests.</param>
    /// <param name="loggerFactory">Creates API loggers.</param>
    /// <param name="authorizationProvider">Supplies short-lived device authorization.</param>
    /// <param name="parser">Parses downloaded XMLTV documents.</param>
    public SiliconDustGuideSourceDriver(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ISiliconDustDeviceAuthorizationProvider authorizationProvider, SiliconDustXmltvParser parser)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _authorizationProvider = authorizationProvider;
        _parser = parser;
    }

    /// <summary>Gets the kind.</summary>
    public string Kind => DriverKind;

    /// <summary>Gets the display name.</summary>
    public string DisplayName => "SiliconDust";

    /// <summary>Gets the supports user creation.</summary>
    public bool SupportsUserCreation => true;

    /// <summary>Gets the settings descriptor.</summary>
    public AdapterSettingsDescriptor SettingsDescriptor { get; } = new()
    {
        AdapterKind = DriverKind
    };

    /// <summary>Creates default configuration.</summary>
    /// <param name="sourceId">The source ID value.</param>
    /// <returns>The operation's result.</returns>
    public AdapterConfiguration CreateDefaultConfiguration(GuideSourceId sourceId)
    {
        if (sourceId.Value == Guid.Empty)
        {
            throw new ArgumentException("A SiliconDust guide source identifier is required.", nameof(sourceId));
        }

        return new AdapterConfiguration(Kind, JsonSerializer.SerializeToElement(new { }));
    }

    /// <summary>Gets settings.</summary>
    /// <param name="configuration">Configuration to apply.</param>
    /// <returns>The operation's result.</returns>
    public AdapterSettingsSnapshot GetSettings(AdapterConfiguration configuration) => new()
    {
        AdapterKind = Kind
    };

    /// <summary>Applies settings.</summary>
    /// <param name="configuration">Configuration to apply.</param>
    /// <param name="edits">The edits value.</param>
    /// <returns>The operation's result.</returns>
    public AdapterSettingsApplyResult ApplySettings(AdapterConfiguration configuration, IReadOnlyList<AdapterSettingEdit> edits)
    {
        if (edits.Any(edit => edit.Action != AdapterSettingEditAction.Preserve))
        {
            return new AdapterSettingsApplyResult
            {
                Errors = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    [""] = ["SiliconDust guide sources do not accept adapter-specific settings."]
                }
            };
        }
        return new AdapterSettingsApplyResult { Configuration = configuration };
    }

    /// <summary>Determines support for physical adapter.</summary>
    /// <param name="physicalAdapterKind">The physical adapter kind value.</param>
    /// <returns>The operation's result.</returns>
    public bool SupportsPhysicalAdapter(string physicalAdapterKind) =>
        string.Equals(physicalAdapterKind, "hdhomerun", StringComparison.OrdinalIgnoreCase);

    /// <summary>Validates the silicon dust guide source driver.</summary>
    /// <param name="configuration">Configuration to apply.</param>
    /// <returns>The operation's result.</returns>
    public IReadOnlyList<string> Validate(AdapterConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var errors = new List<string>();
        if (!string.Equals(configuration.Kind, Kind, StringComparison.Ordinal))
        {
            errors.Add($"Adapter kind must be '{Kind}'.");
        }

        if (configuration.Configuration.ValueKind != JsonValueKind.Object)
        {
            errors.Add("SiliconDust guide configuration must be a JSON object.");
            return errors;
        }

        foreach (var property in configuration.Configuration.EnumerateObject())
        {
            errors.Add($"SiliconDust guide configuration does not support property '{property.Name}'. Authorization must be supplied by the device capability.");
        }

        return errors;
    }

    /// <summary>Creates the silicon dust guide source driver asynchronously.</summary>
    /// <param name="sourceId">The source ID value.</param>
    /// <param name="configuration">Configuration to apply.</param>
    /// <param name="physicalDeviceIds">The physical device IDs value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The operation's result.</returns>
    public ValueTask<IGuideSource> CreateAsync(GuideSourceId sourceId, AdapterConfiguration configuration, IReadOnlyList<PhysicalDeviceId> physicalDeviceIds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sourceId.Value == Guid.Empty)
        {
            throw new ArgumentException("A SiliconDust guide source identifier is required.", nameof(sourceId));
        }
        ArgumentNullException.ThrowIfNull(physicalDeviceIds);
        if (physicalDeviceIds.Count == 0)
        {
            throw new ArgumentException("A SiliconDust guide source requires at least one HDHomeRun physical device.", nameof(physicalDeviceIds));
        }
        if (physicalDeviceIds.Count != physicalDeviceIds.Distinct().Count())
        {
            throw new ArgumentException("A SiliconDust guide source cannot include the same physical device more than once.", nameof(physicalDeviceIds));
        }

        var errors = Validate(configuration);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(configuration));
        }

        IGuideSource source = new SiliconDustGuideSource(
            sourceId,
            _httpClientFactory,
            _loggerFactory.CreateLogger<HDHomeRunApiClient>(),
            _authorizationProvider,
            _parser,
            physicalDeviceIds);
        return ValueTask.FromResult(source);
    }
}
