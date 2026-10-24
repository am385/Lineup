using System.Text.Json;
using Lineup.Common.Configuration;
using Lineup.Extensibility.Configuration;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;

namespace Lineup.SiliconDust.HDHomeRun.VirtualTuner;

/// <summary>
/// Validates virtual HDHomeRun configuration and derives its advertised capacity.
/// </summary>
public sealed class HdHomeRunVirtualTunerDriver : IVirtualTunerDriver, IVirtualTunerCapacityProvider, IAdapterSettingsProvider
{
    /// <summary>Stable adapter kind.</summary>
    public const string AdapterKind = "hdhomerun";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <inheritdoc />
    public string Kind => AdapterKind;

    /// <inheritdoc />
    public string DisplayName => "HDHomeRun";

    /// <inheritdoc />
    public bool SupportsUserCreation => true;

    /// <inheritdoc />
    public VirtualOutputCapabilities OutputCapabilities { get; } = new()
    {
        RequiresExternalHosting = true,
        SupportedTransports = new HashSet<VirtualOutputTransport>
        {
            VirtualOutputTransport.Http,
            VirtualOutputTransport.Https
        },
        RequiresSharedDiscoveryBroker = true,
        ProtocolPaths =
        [
            "/discover.json",
            "/lineup.json",
            "/lineup.xml",
            "/lineup.m3u",
            "/lineup_status.json",
            "/auto/v{channel}"
        ]
    };

    /// <inheritdoc />
    public AdapterSettingsDescriptor SettingsDescriptor { get; } = new()
    {
        AdapterKind = AdapterKind,
        Fields =
        [
            new AdapterSettingDescriptor
            {
                Key = "deviceId",
                Label = "Device ID",
                Description = "Checksum-valid eight-character HDHomeRun DeviceID.",
                Kind = AdapterSettingKind.Text
            },
            new AdapterSettingDescriptor
            {
                Key = "tunerCountCap",
                Label = "Tuner count cap",
                Description = "Optional maximum number of virtual tuners advertised to clients.",
                Kind = AdapterSettingKind.Integer,
                Minimum = 1,
                Maximum = byte.MaxValue
            },
            new AdapterSettingDescriptor
            {
                Key = "enableNetworkAdvertisement",
                Label = "Enable network advertisement",
                Description = "Advertise this virtual device through SiliconDust discovery and SSDP.",
                Kind = AdapterSettingKind.Boolean,
                CanClear = false
            },
            new AdapterSettingDescriptor
            {
                Key = "advertisedBaseUrl",
                Label = "Advertised base URL",
                Description = "Externally reachable HTTP or HTTPS root URL used by discovery clients.",
                Kind = AdapterSettingKind.Uri,
                IsRequired = true,
                CanClear = false,
                VisibleWhen =
                [
                    new AdapterSettingCondition
                    {
                        FieldKey = "enableNetworkAdvertisement",
                        Value = "true"
                    }
                ]
            }
        ]
    };

    /// <summary>Gets the protocol capabilities exposed by this adapter.</summary>
    public HdHomeRunVirtualTunerCapabilities Capabilities { get; } = new();

    /// <inheritdoc />
    public AdapterConfiguration CreateDefaultConfiguration(VirtualDeviceId deviceId) =>
        new(AdapterKind, JsonSerializer.SerializeToElement(new HdHomeRunVirtualTunerConfiguration
        {
            DeviceId = HdHomeRunProxyIdentity.CreateDeviceId(deviceId.Value.ToString("N"))
        }));

    /// <inheritdoc />
    public AdapterSettingsSnapshot GetSettings(AdapterConfiguration configuration)
    {
        var typed = configuration.Configuration.Deserialize<HdHomeRunVirtualTunerConfiguration>(SerializerOptions)
            ?? new HdHomeRunVirtualTunerConfiguration();
        return new AdapterSettingsSnapshot
        {
            AdapterKind = Kind,
            Values =
            [
                Snapshot("deviceId", typed.DeviceId),
                Snapshot("tunerCountCap", typed.TunerCountCap),
                Snapshot("enableNetworkAdvertisement", typed.EnableNetworkAdvertisement),
                Snapshot("advertisedBaseUrl", typed.AdvertisedBaseUrl)
            ]
        };
    }

    /// <inheritdoc />
    public AdapterSettingsApplyResult ApplySettings(
        AdapterConfiguration configuration,
        IReadOnlyList<AdapterSettingEdit> edits)
    {
        var current = configuration.Configuration.Deserialize<HdHomeRunVirtualTunerConfiguration>(SerializerOptions)
            ?? new HdHomeRunVirtualTunerConfiguration();
        var deviceId = current.DeviceId;
        var tunerCountCap = current.TunerCountCap;
        var enableNetworkAdvertisement = current.EnableNetworkAdvertisement;
        var advertisedBaseUrl = current.AdvertisedBaseUrl;
        var errors = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            switch (edit.Key)
            {
                case "deviceId":
                    if (!TryReadOptionalString(edit, ref deviceId))
                    {
                        errors[edit.Key] = ["Device ID must be text or cleared."];
                    }
                    break;
                case "tunerCountCap":
                    if (!TryReadOptionalInt32(edit, ref tunerCountCap))
                    {
                        errors[edit.Key] = ["Tuner count cap must be a whole number or cleared."];
                    }
                    break;
                case "enableNetworkAdvertisement":
                    if (!TryReadBoolean(edit, ref enableNetworkAdvertisement))
                    {
                        errors[edit.Key] = ["Network advertisement must be true or false."];
                    }
                    break;
                case "advertisedBaseUrl":
                    if (!TryReadOptionalString(edit, ref advertisedBaseUrl))
                    {
                        errors[edit.Key] = ["Advertised base URL must be text or cleared."];
                    }
                    break;
                default:
                    errors[edit.Key] = ["Unknown setting."];
                    break;
            }
        }

        if (errors.Count > 0)
        {
            return new AdapterSettingsApplyResult { Errors = errors };
        }

        var updated = new AdapterConfiguration(Kind, JsonSerializer.SerializeToElement(new HdHomeRunVirtualTunerConfiguration
        {
            DeviceId = deviceId,
            TunerCountCap = tunerCountCap,
            EnableNetworkAdvertisement = enableNetworkAdvertisement,
            AdvertisedBaseUrl = advertisedBaseUrl
        }));
        var validation = Validate(updated);
        return validation.Count == 0
            ? new AdapterSettingsApplyResult { Configuration = updated }
            : new AdapterSettingsApplyResult
            {
                Errors = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    [""] = validation
                }
            };
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(AdapterConfiguration configuration)
    {
        var errors = new List<string>();
        if (!string.Equals(configuration.Kind, Kind, StringComparison.Ordinal))
        {
            errors.Add($"Expected adapter kind '{Kind}'.");
        }

        HdHomeRunVirtualTunerConfiguration? typedConfiguration = null;
        try
        {
            typedConfiguration = configuration.Configuration.Deserialize<HdHomeRunVirtualTunerConfiguration>(SerializerOptions);
        }
        catch (JsonException)
        {
            errors.Add("Virtual HDHomeRun configuration must be a JSON object.");
        }

        if (typedConfiguration is null)
        {
            if (errors.Count == 0)
            {
                errors.Add("Virtual HDHomeRun configuration must be a JSON object.");
            }
        }
        else if (typedConfiguration.TunerCountCap is < 1 or > byte.MaxValue)
        {
            errors.Add("Virtual HDHomeRun tunerCountCap must be between 1 and 255.");
        }
        if (typedConfiguration?.DeviceId is { Length: > 0 } deviceId && !HdHomeRunProxyIdentity.IsValidDeviceId(deviceId))
        {
            errors.Add("Virtual HDHomeRun deviceId must be an externally valid eight-character HDHomeRun DeviceID.");
        }
        if (typedConfiguration?.EnableNetworkAdvertisement == true &&
            !HdHomeRunProxyProfileResolver.TryGetHttpRoot(typedConfiguration.AdvertisedBaseUrl, out _))
        {
            errors.Add("Virtual HDHomeRun network advertisement requires an absolute HTTP or HTTPS advertised base URL.");
        }

        return errors;
    }

    /// <summary>
    /// Sums finite capacities from subscribed physical devices and applies the protocol and configured caps.
    /// </summary>
    /// <param name="virtualDevice">Virtual device whose subscription and adapter configuration are applied.</param>
    /// <param name="physicalDevices">Current snapshots for enabled physical devices.</param>
    /// <returns>The tuner count that can be represented by the HDHomeRun discovery protocol.</returns>
    public byte GetTunerCount(VirtualDeviceDefinition virtualDevice, IEnumerable<PhysicalDeviceSnapshot> physicalDevices)
    {
        ArgumentNullException.ThrowIfNull(virtualDevice);
        ArgumentNullException.ThrowIfNull(physicalDevices);
        var subscribedDevices = physicalDevices.Where(snapshot =>
            virtualDevice.Subscription.Mode == PhysicalDeviceSubscriptionMode.All ||
            virtualDevice.Subscription.SelectedDeviceIds.Contains(snapshot.Id));
        return GetTunerCount(subscribedDevices, virtualDevice.Adapter);
    }

    /// <inheritdoc />
    public PhysicalTunerCapacity GetCapacity(VirtualDeviceDefinition virtualDevice, IEnumerable<PhysicalDeviceSnapshot> physicalDevices) =>
        new(GetTunerCount(virtualDevice, physicalDevices));

    /// <summary>
    /// Sums finite capacities from an already subscribed set of physical devices and applies the protocol and configured caps.
    /// </summary>
    /// <param name="physicalDevices">Current snapshots for subscribed physical devices.</param>
    /// <param name="configuration">Adapter configuration.</param>
    /// <returns>The tuner count that can be represented by the HDHomeRun discovery protocol.</returns>
    public byte GetTunerCount(IEnumerable<PhysicalDeviceSnapshot> physicalDevices, AdapterConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(physicalDevices);
        var errors = Validate(configuration);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(configuration));
        }

        var configured = configuration.Configuration.Deserialize<HdHomeRunVirtualTunerConfiguration>(SerializerOptions)!;
        long tunerCount = 0;
        foreach (var physicalDevice in physicalDevices)
        {
            ArgumentNullException.ThrowIfNull(physicalDevice);
            if (physicalDevice.Capacity.TunerCount is int finiteCapacity)
            {
                if (finiteCapacity < 0)
                {
                    throw new ArgumentException($"Physical device {physicalDevice.Id} has a negative tuner capacity.", nameof(physicalDevices));
                }

                tunerCount = Math.Min(byte.MaxValue, tunerCount + finiteCapacity);
            }
        }

        var cap = configured.TunerCountCap ?? byte.MaxValue;
        return (byte)Math.Min(tunerCount, cap);
    }

    /// <summary>Gets the persisted external DeviceID, deriving a stable valid value for older configurations.</summary>
    public string GetDeviceId(VirtualDeviceDefinition virtualDevice)
    {
        ArgumentNullException.ThrowIfNull(virtualDevice);
        var errors = Validate(virtualDevice.Adapter);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(virtualDevice));
        }

        var configured = virtualDevice.Adapter.Configuration.Deserialize<HdHomeRunVirtualTunerConfiguration>(SerializerOptions)!;
        return string.IsNullOrWhiteSpace(configured.DeviceId)
            ? HdHomeRunProxyIdentity.CreateDeviceId(virtualDevice.Id.Value.ToString("N"))
            : configured.DeviceId.ToUpperInvariant();
    }

    private static AdapterSettingSnapshot Snapshot<T>(string key, T value) => new()
    {
        Key = key,
        Value = JsonSerializer.SerializeToElement(value),
        HasValue = value is not null
    };

    private static bool TryReadOptionalString(AdapterSettingEdit edit, ref string? value)
    {
        if (edit.Action == AdapterSettingEditAction.Preserve)
        {
            return true;
        }
        if (edit.Action == AdapterSettingEditAction.Clear)
        {
            value = null;
            return true;
        }
        if (edit.Action != AdapterSettingEditAction.Set ||
            edit.Value is not JsonElement element ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = string.IsNullOrWhiteSpace(element.GetString()) ? null : element.GetString()!.Trim();
        return true;
    }

    private static bool TryReadOptionalInt32(AdapterSettingEdit edit, ref int? value)
    {
        if (edit.Action == AdapterSettingEditAction.Preserve)
        {
            return true;
        }
        if (edit.Action == AdapterSettingEditAction.Clear)
        {
            value = null;
            return true;
        }
        if (edit.Action != AdapterSettingEditAction.Set ||
            edit.Value is not JsonElement element ||
            !element.TryGetInt32(out var parsed))
        {
            return false;
        }
        value = parsed;
        return true;
    }

    private static bool TryReadBoolean(AdapterSettingEdit edit, ref bool value)
    {
        if (edit.Action == AdapterSettingEditAction.Preserve)
        {
            return true;
        }
        if (edit.Action != AdapterSettingEditAction.Set ||
            edit.Value is not JsonElement element ||
            element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }
        value = element.GetBoolean();
        return true;
    }
}

/// <summary>
/// Adapter-owned virtual HDHomeRun settings.
/// </summary>
public sealed record HdHomeRunVirtualTunerConfiguration
{
    /// <summary>Gets the persisted checksum-valid externally advertised DeviceID.</summary>
    public string? DeviceId { get; init; }

    /// <summary>Gets the optional externally reachable Lineup root URL advertised during discovery.</summary>
    public string? AdvertisedBaseUrl { get; init; }

    /// <summary>Gets whether this device participates in SiliconDust and SSDP network advertisement.</summary>
    public bool EnableNetworkAdvertisement { get; init; }

    /// <summary>Gets an optional maximum advertised tuner count.</summary>
    public int? TunerCountCap { get; init; }
}

/// <summary>
/// Describes the HDHomeRun representations implemented by the virtual adapter.
/// </summary>
public sealed record HdHomeRunVirtualTunerCapabilities
{
    /// <summary>Gets whether SiliconDust UDP discovery is supported.</summary>
    public bool SupportsNetworkDiscovery => true;

    /// <summary>Gets whether JSON lineup output is supported.</summary>
    public bool SupportsJsonLineup => true;

    /// <summary>Gets whether XML lineup output is supported.</summary>
    public bool SupportsXmlLineup => true;

    /// <summary>Gets whether extended M3U lineup output is supported.</summary>
    public bool SupportsM3uLineup => true;

    /// <summary>Gets the largest tuner count representable by the protocol.</summary>
    public int MaximumTunerCount => byte.MaxValue;
}
