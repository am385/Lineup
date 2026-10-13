using System.Text.Json;
using Lineup.Common.Configuration;
using Lineup.Extensibility.Configuration;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;

namespace Lineup.WebPlayer;

/// <summary>
/// Validates the built-in Web Player virtual-device configuration.
/// </summary>
public sealed class WebPlayerVirtualTunerDriver : IVirtualTunerDriver, IAdapterSettingsProvider, IStreamingConfigurationNormalizer
{
    /// <summary>Stable adapter kind.</summary>
    public const string AdapterKind = "web-player";

    /// <summary>Fixed display name for the built-in virtual device.</summary>
    public const string BuiltInName = "Web Player";

    /// <inheritdoc />
    public string Kind => AdapterKind;

    /// <inheritdoc />
    public string DisplayName => BuiltInName;

    /// <inheritdoc />
    public bool SupportsUserCreation => false;

    /// <inheritdoc />
    public VirtualOutputCapabilities OutputCapabilities { get; } = new()
    {
        RequiresExternalHosting = true,
        SupportedTransports = new HashSet<VirtualOutputTransport>
        {
            VirtualOutputTransport.Http,
            VirtualOutputTransport.Https
        },
        ProtocolPaths =
        [
            "/media/{session}/manifest.mpd",
            "/media/{session}/{fragment}"
        ]
    };

    /// <inheritdoc />
    public AdapterSettingsDescriptor SettingsDescriptor { get; } = new()
    {
        AdapterKind = WebPlayerVirtualTunerDriver.AdapterKind
    };

    /// <inheritdoc />
    public AdapterConfiguration CreateDefaultConfiguration(VirtualDeviceId deviceId) =>
        new(AdapterKind, JsonSerializer.SerializeToElement(new { }));

    /// <inheritdoc />
    public AdapterSettingsSnapshot GetSettings(AdapterConfiguration configuration) => new()
    {
        AdapterKind = AdapterKind
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
                    [""] = ["Web Player does not accept adapter-specific settings."]
                }
            };
        }
        return new AdapterSettingsApplyResult { Configuration = configuration };
    }

    /// <inheritdoc />
    public StreamingConfiguration Normalize(
        StreamingConfiguration configuration,
        StreamingConfigurationInitializationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return EnsureBuiltIn(configuration);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(AdapterConfiguration configuration)
    {
        if (!string.Equals(configuration.Kind, Kind, StringComparison.Ordinal))
        {
            return [$"Expected adapter kind '{Kind}'."];
        }
        if (configuration.Configuration.ValueKind != JsonValueKind.Object)
        {
            return ["Web Player configuration must be a JSON object."];
        }
        return configuration.Configuration.EnumerateObject().Any()
            ? ["Web Player configuration does not accept adapter-specific properties."]
            : [];
    }

    /// <summary>Creates the persisted built-in Web Player definition.</summary>
    public static VirtualDeviceDefinition CreateBuiltIn(VirtualDeviceId? id = null) => new()
    {
        Id = id ?? VirtualDeviceId.New(),
        Name = BuiltInName,
        Adapter = new AdapterConfiguration(AdapterKind, JsonSerializer.SerializeToElement(new { }))
    };

    /// <summary>Ensures exactly one persisted built-in Web Player exists with its fixed name without replacing its identity or settings.</summary>
    public static StreamingConfiguration EnsureBuiltIn(StreamingConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var hasBuiltIn = false;
        var virtualDevices = configuration.VirtualDevices.Select(device =>
        {
            if (!IsBuiltIn(device))
            {
                return device;
            }
            hasBuiltIn = true;
            return device;
        }).ToList();
        if (!hasBuiltIn)
        {
            virtualDevices.Add(CreateBuiltIn());
        }
        return configuration with { VirtualDevices = virtualDevices };
    }

    /// <summary>Validates the built-in Web Player invariant and its adapter-owned configuration.</summary>
    public static IReadOnlyList<string> ValidateBuiltIn(StreamingConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var players = configuration.VirtualDevices.Where(IsBuiltIn).ToArray();
        var errors = new List<string>();
        if (players.Length != 1)
        {
            errors.Add("Exactly one built-in Web Player virtual device is required.");
        }
        var driver = new WebPlayerVirtualTunerDriver();
        foreach (var player in players)
        {
            errors.AddRange(driver.Validate(player.Adapter).Select(error => $"Web Player {player.Id}: {error}"));
        }
        return errors;
    }

    private static bool IsBuiltIn(VirtualDeviceDefinition definition) =>
        string.Equals(definition.Adapter.Kind, AdapterKind, StringComparison.OrdinalIgnoreCase);
}
