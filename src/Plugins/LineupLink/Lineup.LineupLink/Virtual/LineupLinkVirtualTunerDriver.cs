using System.Text.Json;
using Lineup.Common.Configuration;
using Lineup.Extensibility.Configuration;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.LineupLink.Protocol;

namespace Lineup.LineupLink.Virtual;

/// <summary>Validates Lineup Link virtual outputs and derives their capacity.</summary>
public sealed class LineupLinkVirtualTunerDriver : IVirtualTunerDriver, IVirtualTunerCapacityProvider, IAdapterSettingsProvider, IAdapterAccessGrantProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };

    /// <inheritdoc />
    public string Kind => LineupLinkProtocol.AdapterKind;

    /// <inheritdoc />
    public string DisplayName => "Lineup Link";

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
        ProtocolPaths =
        [
            "/api/lineup-link/v2/virtual-devices/{virtualDeviceId}/manifest",
            "/api/lineup-link/v2/virtual-devices/{virtualDeviceId}/lineup",
            "/api/lineup-link/v2/virtual-devices/{virtualDeviceId}/guide",
            "/api/lineup-link/v2/virtual-devices/{virtualDeviceId}/streams/{channelKey}"
        ]
    };

    /// <inheritdoc />
    public AdapterSettingsDescriptor SettingsDescriptor { get; } = new()
    {
        AdapterKind = LineupLinkProtocol.AdapterKind,
        Fields =
        [
            new AdapterSettingDescriptor { Key = "instanceId", Label = "Instance ID", Kind = AdapterSettingKind.Guid, IsRequired = true, CanClear = false },
            new AdapterSettingDescriptor { Key = "publicBaseUrl", Label = "Public base URL", Kind = AdapterSettingKind.Uri, CanClear = true },
            new AdapterSettingDescriptor { Key = "requireEncryption", Label = "Require HTTPS", Kind = AdapterSettingKind.Boolean, CanClear = false },
            new AdapterSettingDescriptor
            {
                Key = "certificateSha256",
                Label = "Certificate SHA-256 fingerprint",
                Kind = AdapterSettingKind.CertificateFingerprint,
                VisibleWhen =
                [
                    new AdapterSettingCondition { FieldKey = "requireEncryption", Value = "true" }
                ]
            }
        ]
    };

    /// <inheritdoc />
    public bool SupportsPhysicalAdapter(string physicalAdapterKind) =>
        !string.Equals(physicalAdapterKind, LineupLinkProtocol.AdapterKind, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public AdapterConfiguration CreateDefaultConfiguration(VirtualDeviceId deviceId) =>
        new(Kind, JsonSerializer.SerializeToElement(new LineupLinkVirtualConfiguration()));

    /// <inheritdoc />
    public AdapterSettingsSnapshot GetSettings(AdapterConfiguration configuration)
    {
        var typed = configuration.Configuration.Deserialize<LineupLinkVirtualConfiguration>(SerializerOptions)
            ?? new LineupLinkVirtualConfiguration();
        return new AdapterSettingsSnapshot
        {
            AdapterKind = Kind,
            Values =
            [
                Snapshot("instanceId", typed.InstanceId),
                Snapshot("publicBaseUrl", typed.PublicBaseUrl),
                Snapshot("requireEncryption", typed.RequireEncryption),
                Snapshot("certificateSha256", typed.CertificateSha256)
            ]
        };
    }

    /// <inheritdoc />
    public AdapterSettingsApplyResult ApplySettings(
        AdapterConfiguration configuration,
        IReadOnlyList<AdapterSettingEdit> edits)
    {
        var current = configuration.Configuration.Deserialize<LineupLinkVirtualConfiguration>(SerializerOptions)
            ?? new LineupLinkVirtualConfiguration();
        var instanceId = current.InstanceId;
        var publicBaseUrl = current.PublicBaseUrl;
        var requireEncryption = current.RequireEncryption;
        var accessGrants = current.AccessGrants;
        var certificateSha256 = current.CertificateSha256;
        var errors = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            if (edit.Action == AdapterSettingEditAction.Preserve)
            {
                continue;
            }
            switch (edit.Key)
            {
                case "instanceId":
                    if (edit.Action != AdapterSettingEditAction.Set ||
                        edit.Value is not JsonElement instanceValue ||
                        instanceValue.ValueKind != JsonValueKind.String ||
                        !Guid.TryParse(instanceValue.GetString(), out instanceId))
                    {
                        errors[edit.Key] = ["A valid instance GUID is required."];
                    }
                    break;
                case "publicBaseUrl":
                    if (!TryReadRequiredString(edit, ref publicBaseUrl))
                    {
                        errors[edit.Key] = ["Public base URL is required."];
                    }
                    break;
                case "requireEncryption":
                    if (edit.Action != AdapterSettingEditAction.Set ||
                        edit.Value is not JsonElement encryptionValue ||
                        encryptionValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        errors[edit.Key] = ["Require HTTPS must be true or false."];
                    }
                    else
                    {
                        requireEncryption = encryptionValue.GetBoolean();
                    }
                    break;
                case "certificateSha256":
                    if (!TryReadOptionalString(edit, ref certificateSha256))
                    {
                        errors[edit.Key] = ["Certificate fingerprint must be text or cleared."];
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

        var updated = new AdapterConfiguration(Kind, JsonSerializer.SerializeToElement(new LineupLinkVirtualConfiguration
        {
            InstanceId = instanceId,
            PublicBaseUrl = publicBaseUrl,
            RequireEncryption = requireEncryption,
            AccessGrants = accessGrants,
            CertificateSha256 = certificateSha256
        }));
        var validation = Validate(updated);
        return validation.Count == 0
            ? new AdapterSettingsApplyResult { Configuration = updated }
            : new AdapterSettingsApplyResult
            {
                Errors = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [""] = validation }
            };
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(AdapterConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var errors = new List<string>();
        if (!string.Equals(configuration.Kind, Kind, StringComparison.Ordinal))
        {
            errors.Add($"Adapter kind must be '{Kind}'.");
        }

        LineupLinkVirtualConfiguration? typed = null;
        try
        {
            typed = configuration.Configuration.Deserialize<LineupLinkVirtualConfiguration>(SerializerOptions);
        }
        catch (JsonException)
        {
            errors.Add("Lineup Link virtual configuration must be a JSON object.");
        }

        if (typed is null)
        {
            if (errors.Count == 0)
            {
                errors.Add("Lineup Link virtual configuration must be a JSON object.");
            }
            return errors;
        }

        if (typed.InstanceId == Guid.Empty)
        {
            errors.Add("A stable Lineup Link instance ID is required.");
        }
        var security = typed.RequireEncryption ? LineupLinkTransportSecurity.Https : LineupLinkTransportSecurity.Http;
        if (!string.IsNullOrWhiteSpace(typed.PublicBaseUrl) &&
            !LineupLinkConfigurationValidation.TryNormalizeBaseUri(typed.PublicBaseUrl, security, out _))
        {
            errors.Add("Public base URL must be an absolute HTTP or HTTPS root URL matching the encryption setting.");
        }
        errors.AddRange(LineupLinkAccessGrantManager.Validate(typed));
        if (!LineupLinkConfigurationValidation.IsValidCertificateSha256(typed.CertificateSha256))
        {
            errors.Add("Certificate fingerprint must contain 64 hexadecimal SHA-256 characters.");
        }
        if (!typed.RequireEncryption && !string.IsNullOrWhiteSpace(typed.CertificateSha256))
        {
            errors.Add("Certificate pinning is available only when encryption is required.");
        }
        return errors;
    }

    /// <summary>Gets the aggregate capacity of already subscribed, compatible physical devices.</summary>
    public PhysicalTunerCapacity GetCapacity(IEnumerable<PhysicalDeviceSnapshot> physicalDevices)
    {
        ArgumentNullException.ThrowIfNull(physicalDevices);
        var finiteCapacity = 0;
        foreach (var device in physicalDevices)
        {
            ArgumentNullException.ThrowIfNull(device);
            if (!device.Capacity.IsFinite)
            {
                return new PhysicalTunerCapacity(null);
            }
            finiteCapacity = checked(finiteCapacity + device.Capacity.TunerCount!.Value);
        }
        return new PhysicalTunerCapacity(finiteCapacity);
    }

    /// <summary>Gets output capacity from physical devices that are both subscribed and adapter-compatible.</summary>
    public PhysicalTunerCapacity GetCapacity(VirtualDeviceDefinition virtualDevice, IEnumerable<PhysicalDeviceSnapshot> physicalDevices)
    {
        ArgumentNullException.ThrowIfNull(virtualDevice);
        ArgumentNullException.ThrowIfNull(physicalDevices);
        var subscribed = physicalDevices.Where(snapshot =>
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            return SupportsPhysicalAdapter(snapshot.Kind) &&
                (virtualDevice.Subscription.Mode == PhysicalDeviceSubscriptionMode.All ||
                 virtualDevice.Subscription.SelectedDeviceIds.Contains(snapshot.Id));
        });
        return GetCapacity(subscribed);
    }

    /// <summary>Reads typed adapter configuration.</summary>
    public LineupLinkVirtualConfiguration GetConfiguration(AdapterConfiguration configuration)
    {
        var errors = Validate(configuration);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(configuration));
        }
        var typed = configuration.Configuration.Deserialize<LineupLinkVirtualConfiguration>(SerializerOptions)!;
        return typed;
    }

    /// <inheritdoc />
    public IReadOnlyList<AdapterAccessGrantSnapshot> GetAccessGrants(AdapterConfiguration configuration)
    {
        var typed = Deserialize(configuration);
        return LineupLinkAccessGrantManager.GetSnapshots(typed);
    }

    /// <inheritdoc />
    public AdapterAccessGrantMutationResult CreateAccessGrant(
        AdapterConfiguration configuration,
        string name,
        DateTime? expiresAtUtc,
        DateTime utcNow) =>
        ToResult(
            LineupLinkAccessGrantManager.Create(Deserialize(configuration), name, expiresAtUtc, utcNow));

    /// <inheritdoc />
    public AdapterAccessGrantMutationResult RotateAccessGrant(
        AdapterConfiguration configuration,
        Guid grantId,
        DateTime utcNow) =>
        ToResult(
            LineupLinkAccessGrantManager.Rotate(Deserialize(configuration), grantId, utcNow));

    /// <inheritdoc />
    public AdapterAccessGrantMutationResult RevokeAccessGrant(
        AdapterConfiguration configuration,
        Guid grantId,
        DateTime utcNow) =>
        ToResult(
            LineupLinkAccessGrantManager.Revoke(Deserialize(configuration), grantId, utcNow));

    /// <inheritdoc />
    public AdapterAccessGrantMutationResult PurgeAccessGrant(
        AdapterConfiguration configuration,
        Guid grantId) =>
        ToResult(
            LineupLinkAccessGrantManager.Purge(Deserialize(configuration), grantId));

    private static AdapterSettingSnapshot Snapshot<T>(string key, T value) => new()
    {
        Key = key,
        Value = JsonSerializer.SerializeToElement(value),
        HasValue = value is not null
    };

    private static LineupLinkVirtualConfiguration Deserialize(AdapterConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!string.Equals(configuration.Kind, LineupLinkProtocol.AdapterKind, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Adapter kind must be '{LineupLinkProtocol.AdapterKind}'.", nameof(configuration));
        }
        return configuration.Configuration.Deserialize<LineupLinkVirtualConfiguration>(SerializerOptions)
            ?? throw new ArgumentException("Lineup Link virtual configuration must be a JSON object.", nameof(configuration));
    }

    private static AdapterAccessGrantMutationResult ToResult(LineupLinkAccessGrantMutation mutation)
    {
        if (mutation.Configuration is null || mutation.Grant is null)
        {
            return new AdapterAccessGrantMutationResult { Errors = mutation.Errors };
        }
        return new AdapterAccessGrantMutationResult
        {
            Configuration = new AdapterConfiguration(
                LineupLinkProtocol.AdapterKind,
                JsonSerializer.SerializeToElement(mutation.Configuration)),
            Grant = mutation.Grant,
            GeneratedSecret = mutation.GeneratedToken
        };
    }

    private static bool TryReadRequiredString(AdapterSettingEdit edit, ref string value)
    {
        if (edit.Action != AdapterSettingEditAction.Set ||
            edit.Value is not JsonElement element ||
            element.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(element.GetString()))
        {
            return false;
        }
        value = element.GetString()!.Trim();
        return true;
    }

    private static bool TryReadOptionalString(AdapterSettingEdit edit, ref string? value)
    {
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
}
