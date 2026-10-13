using System.Text.Json;
using Lineup.Common.Configuration;
using Lineup.Extensibility.Configuration;
using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.SiliconDust.HDHomeRun.Api;
using Microsoft.Extensions.Logging;

namespace Lineup.SiliconDust.GuideSource;

/// <summary>
/// Creates SiliconDust guide sources backed by one or more HDHomeRun devices.
/// </summary>
public sealed class SiliconDustGuideSourceDriver : IGuideSourceDriver, IAdapterSettingsProvider
{
    /// <summary>Gets the stable adapter kind.</summary>
    public const string DriverKind = "silicondust";

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
    public SiliconDustGuideSourceDriver(
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        ISiliconDustDeviceAuthorizationProvider authorizationProvider,
        SiliconDustXmltvParser parser)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _authorizationProvider = authorizationProvider;
        _parser = parser;
    }

    /// <inheritdoc />
    public string Kind => DriverKind;

    /// <inheritdoc />
    public string DisplayName => "SiliconDust";

    /// <inheritdoc />
    public bool SupportsUserCreation => true;

    /// <inheritdoc />
    public AdapterSettingsDescriptor SettingsDescriptor { get; } = new()
    {
        AdapterKind = DriverKind
    };

    /// <inheritdoc />
    public AdapterConfiguration CreateDefaultConfiguration(GuideSourceId sourceId)
    {
        if (sourceId.Value == Guid.Empty)
        {
            throw new ArgumentException("A SiliconDust guide source identifier is required.", nameof(sourceId));
        }

        return new AdapterConfiguration(Kind, JsonSerializer.SerializeToElement(new { }));
    }

    /// <inheritdoc />
    public AdapterSettingsSnapshot GetSettings(AdapterConfiguration configuration) => new()
    {
        AdapterKind = Kind
    };

    /// <inheritdoc />
    public AdapterSettingsApplyResult ApplySettings(
        AdapterConfiguration configuration,
        IReadOnlyList<AdapterSettingEdit> edits)
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

    /// <inheritdoc />
    public bool SupportsPhysicalAdapter(string physicalAdapterKind) =>
        string.Equals(physicalAdapterKind, "hdhomerun", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
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

    /// <inheritdoc />
    public ValueTask<IGuideSource> CreateAsync(
        GuideSourceId sourceId,
        AdapterConfiguration configuration,
        IReadOnlyList<PhysicalDeviceId> physicalDeviceIds,
        CancellationToken cancellationToken = default)
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
