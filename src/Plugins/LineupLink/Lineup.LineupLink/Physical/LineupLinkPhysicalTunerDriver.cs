using System.Text.Json;
using Lineup.Common.Configuration;
using Lineup.Extensibility.Configuration;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.LineupLink.Protocol;

namespace Lineup.LineupLink.Physical;

/// <summary>Creates physical tuner inputs backed by remote Lineup Link virtual devices.</summary>
public sealed class LineupLinkPhysicalTunerDriver : IPhysicalTunerDriver, IPhysicalDeviceProbe, IAdapterSettingsProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };

    /// <inheritdoc />
    public string Kind => LineupLinkProtocol.AdapterKind;

    /// <inheritdoc />
    public string DisplayName => "Lineup Link";

    /// <inheritdoc />
    public bool SupportsUserCreation => true;

    /// <inheritdoc />
    public DeviceCapabilities Capabilities { get; } = new()
    {
        HasFiniteCapacity = true,
        SupportsTunerStatus = true,
        SupportsTunerControl = false,
        SupportsHardwareTranscoding = false
    };

    /// <inheritdoc />
    public AdapterSettingsDescriptor SettingsDescriptor { get; } = new()
    {
        AdapterKind = LineupLinkProtocol.AdapterKind,
        Fields =
        [
            new AdapterSettingDescriptor { Key = "baseUrl", Label = "Remote base URL", Kind = AdapterSettingKind.Uri, IsRequired = true, CanClear = false },
            new AdapterSettingDescriptor { Key = "virtualDeviceId", Label = "Remote virtual-device ID", Kind = AdapterSettingKind.Guid, IsRequired = true, CanClear = false },
            new AdapterSettingDescriptor { Key = "accessToken", Label = "Access token", Kind = AdapterSettingKind.Secret, IsRequired = true, CanClear = false },
            new AdapterSettingDescriptor
            {
                Key = "transportSecurity",
                Label = "Transport security",
                Kind = AdapterSettingKind.Choice,
                CanClear = false,
                Choices =
                [
                    new AdapterSettingChoice { Value = "Https", Label = "HTTPS" },
                    new AdapterSettingChoice { Value = "Http", Label = "HTTP" }
                ]
            },
            new AdapterSettingDescriptor
            {
                Key = "certificateSha256",
                Label = "Certificate SHA-256 fingerprint",
                Kind = AdapterSettingKind.CertificateFingerprint,
                VisibleWhen =
                [
                    new AdapterSettingCondition { FieldKey = "transportSecurity", Value = "Https" }
                ]
            }
        ]
    };

    /// <inheritdoc />
    public AdapterConfiguration CreateDefaultConfiguration(PhysicalDeviceId deviceId) =>
        new(Kind, JsonSerializer.SerializeToElement(new LineupLinkPhysicalConfiguration()));

    /// <inheritdoc />
    public AdapterSettingsSnapshot GetSettings(AdapterConfiguration configuration)
    {
        var typed = configuration.Configuration.Deserialize<LineupLinkPhysicalConfiguration>(SerializerOptions)
            ?? new LineupLinkPhysicalConfiguration();
        return new AdapterSettingsSnapshot
        {
            AdapterKind = Kind,
            Values =
            [
                Snapshot("baseUrl", typed.BaseUrl),
                Snapshot("virtualDeviceId", typed.VirtualDeviceId),
                new AdapterSettingSnapshot { Key = "accessToken", HasValue = !string.IsNullOrWhiteSpace(typed.AccessToken) },
                Snapshot("transportSecurity", typed.TransportSecurity.ToString()),
                Snapshot("certificateSha256", typed.CertificateSha256)
            ]
        };
    }

    /// <inheritdoc />
    public AdapterSettingsApplyResult ApplySettings(
        AdapterConfiguration configuration,
        IReadOnlyList<AdapterSettingEdit> edits)
    {
        var current = configuration.Configuration.Deserialize<LineupLinkPhysicalConfiguration>(SerializerOptions)
            ?? new LineupLinkPhysicalConfiguration();
        var baseUrl = current.BaseUrl;
        var virtualDeviceId = current.VirtualDeviceId;
        var accessToken = current.AccessToken;
        var transportSecurity = current.TransportSecurity;
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
                case "baseUrl":
                    if (!TryReadRequiredString(edit, ref baseUrl))
                    {
                        errors[edit.Key] = ["Remote base URL is required."];
                    }
                    break;
                case "virtualDeviceId":
                    if (edit.Action != AdapterSettingEditAction.Set ||
                        edit.Value is not JsonElement guidValue ||
                        guidValue.ValueKind != JsonValueKind.String ||
                        !Guid.TryParse(guidValue.GetString(), out virtualDeviceId))
                    {
                        errors[edit.Key] = ["A valid virtual-device GUID is required."];
                    }
                    break;
                case "accessToken":
                    if (!TryReadRequiredString(edit, ref accessToken))
                    {
                        errors[edit.Key] = ["Access token is required."];
                    }
                    break;
                case "transportSecurity":
                    if (edit.Action != AdapterSettingEditAction.Set ||
                        edit.Value is not JsonElement securityValue ||
                        securityValue.ValueKind != JsonValueKind.String ||
                        !Enum.TryParse(securityValue.GetString(), true, out transportSecurity))
                    {
                        errors[edit.Key] = ["Transport security must be HTTP or HTTPS."];
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

        var updated = new AdapterConfiguration(Kind, JsonSerializer.SerializeToElement(new LineupLinkPhysicalConfiguration
        {
            BaseUrl = baseUrl,
            VirtualDeviceId = virtualDeviceId,
            AccessToken = accessToken,
            TransportSecurity = transportSecurity,
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

        LineupLinkPhysicalConfiguration? typed = null;
        try
        {
            typed = configuration.Configuration.Deserialize<LineupLinkPhysicalConfiguration>(SerializerOptions);
        }
        catch (JsonException)
        {
            errors.Add("Lineup Link physical configuration must be a JSON object.");
        }

        if (typed is null)
        {
            if (errors.Count == 0)
            {
                errors.Add("Lineup Link physical configuration must be a JSON object.");
            }
            return errors;
        }
        if (!LineupLinkConfigurationValidation.TryNormalizeBaseUri(typed.BaseUrl, typed.TransportSecurity, out _))
        {
            errors.Add("Remote base URL must be an absolute HTTP or HTTPS root URL matching the selected transport security.");
        }
        if (typed.VirtualDeviceId == Guid.Empty)
        {
            errors.Add("Remote virtual-device ID is required.");
        }
        if (string.IsNullOrWhiteSpace(typed.AccessToken))
        {
            errors.Add("Access token is required.");
        }
        if (!LineupLinkConfigurationValidation.IsValidCertificateSha256(typed.CertificateSha256))
        {
            errors.Add("Certificate fingerprint must contain 64 hexadecimal SHA-256 characters.");
        }
        if (typed.TransportSecurity == LineupLinkTransportSecurity.Http && !string.IsNullOrWhiteSpace(typed.CertificateSha256))
        {
            errors.Add("Certificate pinning is available only for HTTPS connections.");
        }
        return errors;
    }

    /// <inheritdoc />
    public async ValueTask<IPhysicalTunerDevice> CreateAsync(
        PhysicalDeviceId deviceId,
        string name,
        AdapterConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var errors = Validate(configuration);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(configuration));
        }

        var typed = configuration.Configuration.Deserialize<LineupLinkPhysicalConfiguration>(SerializerOptions)!;
        var client = new LineupLinkClient(typed);
        try
        {
            var manifest = await client.GetManifestAsync(cancellationToken);
            if (manifest.TunerCount is < 0)
            {
                throw new InvalidDataException("The remote Lineup Link manifest reported a negative tuner capacity.");
            }
            return new LineupLinkPhysicalTunerDevice(deviceId, name, manifest, client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<PhysicalDeviceProbeResult> ProbeAsync(AdapterConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var errors = Validate(configuration);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(configuration));
        }

        var typed = configuration.Configuration.Deserialize<LineupLinkPhysicalConfiguration>(SerializerOptions)!;
        using var client = new LineupLinkClient(typed);
        LineupLinkManifest manifest;
        try
        {
            manifest = await client.GetManifestAsync(cancellationToken);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new UnauthorizedAccessException("The remote Lineup Link device rejected the configured access token.", exception);
        }
        catch (HttpRequestException exception)
        {
            var status = exception.StatusCode is { } statusCode ? $" with HTTP {(int)statusCode}" : string.Empty;
            throw new HttpRequestException($"The remote Lineup Link device could not be reached{status}.", exception, exception.StatusCode);
        }
        if (manifest.TunerCount is < 0)
        {
            throw new InvalidDataException("The remote Lineup Link manifest reported a negative tuner capacity.");
        }

        return new PhysicalDeviceProbeResult
        {
            SuggestedName = manifest.Name,
            Details =
            [
                Detail("Remote output", manifest.Name),
                Detail("Lineup instance ID", manifest.InstanceId.ToString("D")),
                Detail("Virtual device ID", manifest.VirtualDeviceId.ToString("D")),
                Detail("Tuners", manifest.TunerCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "Unlimited"),
                Detail("Protocol version", manifest.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Detail("Guide available", manifest.GuideAvailable ? "Yes" : "No")
            ]
        };
    }

    private static PhysicalDeviceProbeDetail Detail(string label, string value) => new() { Label = label, Value = value };

    private static AdapterSettingSnapshot Snapshot<T>(string key, T value) => new()
    {
        Key = key,
        Value = JsonSerializer.SerializeToElement(value),
        HasValue = value is not null
    };

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
