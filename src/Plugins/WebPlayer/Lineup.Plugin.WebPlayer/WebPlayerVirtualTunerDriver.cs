using System.Text.Json;
using Lineup.Core.Common.Configuration;
using Lineup.Core.Extensibility.Configuration;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Extensibility.Streaming;

namespace Lineup.Plugin.WebPlayer;

/// <summary>
/// Validates the built-in Web Player virtual-device configuration.
/// </summary>
public sealed class WebPlayerVirtualTunerDriver : IVirtualTunerDriver, IAdapterSettingsProvider, IStreamingConfigurationNormalizer
{
    /// <summary>Stable adapter kind.</summary>
    public const string AdapterKind = "web-player";

    /// <summary>Fixed display name for the built-in virtual device.</summary>
    public const string BuiltInName = "Web Player";

    /// <summary>Gets the kind.</summary>
    public string Kind => AdapterKind;

    /// <summary>Gets the display name.</summary>
    public string DisplayName => BuiltInName;

    /// <summary>Gets the supports user creation.</summary>
    public bool SupportsUserCreation => false;

    /// <summary>Gets the output capabilities.</summary>
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

    /// <summary>Gets the settings descriptor.</summary>
    public AdapterSettingsDescriptor SettingsDescriptor { get; } = new()
    {
        AdapterKind = WebPlayerVirtualTunerDriver.AdapterKind,
        Fields =
        [
            new AdapterSettingDescriptor
            {
                Key = "extractEmbeddedCaptions",
                Label = "Extract embedded captions",
                Description = "Extract detected embedded captions for all Web Player viewers. Changes apply to new presentations; dedicated listeners reconfigure normally.",
                Kind = AdapterSettingKind.Boolean,
                CanClear = false
            }
        ]
    };

    /// <summary>Creates default configuration.</summary>
    /// <param name="deviceId">Identity of the target device.</param>
    /// <returns>The operation's result.</returns>
    public AdapterConfiguration CreateDefaultConfiguration(VirtualDeviceId deviceId) =>
        new(AdapterKind, JsonSerializer.SerializeToElement(new WebPlayerConfiguration()));

    /// <summary>Gets settings.</summary>
    /// <param name="configuration">Configuration to apply.</param>
    /// <returns>The operation's result.</returns>
    public AdapterSettingsSnapshot GetSettings(AdapterConfiguration configuration) => new()
    {
        AdapterKind = AdapterKind,
        Values =
        [
            new AdapterSettingSnapshot
            {
                Key = "extractEmbeddedCaptions",
                Value = JsonSerializer.SerializeToElement(WebPlayerConfiguration.Read(configuration).ExtractEmbeddedCaptions),
                HasValue = true
            }
        ]
    };

    /// <summary>Applies settings.</summary>
    /// <param name="configuration">Configuration to apply.</param>
    /// <param name="edits">The edits value.</param>
    /// <returns>The operation's result.</returns>
    public AdapterSettingsApplyResult ApplySettings(AdapterConfiguration configuration, IReadOnlyList<AdapterSettingEdit> edits)
    {
        var validation = Validate(configuration);
        if (validation.Count > 0)
        {
            return new AdapterSettingsApplyResult
            {
                Errors = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [""] = validation }
            };
        }
        var current = WebPlayerConfiguration.Read(configuration);
        var errors = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            if (edit.Action == AdapterSettingEditAction.Preserve)
            {
                continue;
            }
            if (edit.Key != "extractEmbeddedCaptions")
            {
                errors[edit.Key] = ["Unknown setting."];
            }
            else if (edit.Action != AdapterSettingEditAction.Set || edit.Value is not JsonElement value ||
                value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                errors[edit.Key] = ["Extract embedded captions must be true or false."];
            }
            else
            {
                current = current with { ExtractEmbeddedCaptions = value.GetBoolean() };
            }
        }
        return errors.Count > 0
            ? new AdapterSettingsApplyResult { Errors = errors }
            : new AdapterSettingsApplyResult { Configuration = new AdapterConfiguration(Kind, JsonSerializer.SerializeToElement(current)) };
    }

    /// <summary>Executes normalize.</summary>
    /// <param name="configuration">Configuration to apply.</param>
    /// <param name="context">Context used by the operation.</param>
    /// <returns>The operation's result.</returns>
    public StreamingConfiguration Normalize(StreamingConfiguration configuration, StreamingConfigurationInitializationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return EnsureBuiltIn(configuration);
    }

    /// <summary>Validates the web player virtual tuner driver.</summary>
    /// <param name="configuration">Configuration to apply.</param>
    /// <returns>The operation's result.</returns>
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
        try
        {
            _ = WebPlayerConfiguration.Read(configuration);
            return [];
        }
        catch (JsonException exception)
        {
            return [$"Invalid Web Player configuration: {exception.Message}"];
        }
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
