using System.Text.Json;
using Lineup.Common.Configuration;
using Lineup.Extensibility.Configuration;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.SiliconDust.HDHomeRun.Device;
using Lineup.SiliconDust.HDHomeRun.Device.Models;
using Microsoft.Extensions.Logging;

namespace Lineup.SiliconDust.HDHomeRun.PhysicalTuner;

/// <summary>
/// Creates physical HDHomeRun tuner devices.
/// </summary>
public sealed class HDHomeRunPhysicalTunerDriver : IPhysicalTunerDriver, IPhysicalDeviceProbe, IAdapterSettingsProvider
{
    /// <summary>Gets the stable adapter kind.</summary>
    public const string DriverKind = "hdhomerun";

    /// <summary>Gets the default local-network HDHomeRun hostname.</summary>
    public const string DefaultAddress = "hdhomerun.local";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly HDHomeRunService _deviceService;

    /// <summary>
    /// Initializes a new instance of the <see cref="HDHomeRunPhysicalTunerDriver"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Factory for device HTTP clients.</param>
    /// <param name="loggerFactory">Factory for adapter loggers.</param>
    /// <param name="deviceService">Provides native HDHomeRun status and control.</param>
    public HDHomeRunPhysicalTunerDriver(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, HDHomeRunService deviceService)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _deviceService = deviceService;
    }

    /// <inheritdoc />
    public string Kind => DriverKind;

    /// <inheritdoc />
    public string DisplayName => "HDHomeRun";

    /// <inheritdoc />
    public bool SupportsUserCreation => true;

    /// <inheritdoc />
    public DeviceCapabilities Capabilities { get; } = new()
    {
        HasFiniteCapacity = true,
        SupportsTunerStatus = true,
        SupportsTunerControl = true,
        SupportsHardwareTranscoding = false
    };

    /// <inheritdoc />
    public AdapterSettingsDescriptor SettingsDescriptor { get; } = new()
    {
        AdapterKind = DriverKind,
        Fields =
        [
            new AdapterSettingDescriptor
            {
                Key = "address",
                Label = "Device address",
                Description = "Host name, IP address, or absolute HTTP/HTTPS root URL.",
                Kind = AdapterSettingKind.Text,
                IsRequired = true,
                CanClear = false
            }
        ]
    };

    /// <inheritdoc />
    public AdapterConfiguration CreateDefaultConfiguration(PhysicalDeviceId deviceId) =>
        new(DriverKind, JsonSerializer.SerializeToElement(new HDHomeRunPhysicalTunerConfiguration { Address = DefaultAddress }));

    /// <inheritdoc />
    public AdapterSettingsSnapshot GetSettings(AdapterConfiguration configuration)
    {
        var typed = configuration.Configuration.Deserialize<HDHomeRunPhysicalTunerConfiguration>(SerializerOptions);
        return new AdapterSettingsSnapshot
        {
            AdapterKind = Kind,
            Values =
            [
                new AdapterSettingSnapshot
                {
                    Key = "address",
                    Value = JsonSerializer.SerializeToElement(typed?.Address ?? string.Empty),
                    HasValue = !string.IsNullOrWhiteSpace(typed?.Address)
                }
            ]
        };
    }

    /// <inheritdoc />
    public AdapterSettingsApplyResult ApplySettings(
        AdapterConfiguration configuration,
        IReadOnlyList<AdapterSettingEdit> edits)
    {
        var current = configuration.Configuration.Deserialize<HDHomeRunPhysicalTunerConfiguration>(SerializerOptions)
            ?? new HDHomeRunPhysicalTunerConfiguration { Address = DefaultAddress };
        var address = current.Address;
        var errors = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            if (!string.Equals(edit.Key, "address", StringComparison.Ordinal))
            {
                errors[edit.Key] = ["Unknown setting."];
                continue;
            }

            if (edit.Action == AdapterSettingEditAction.Preserve)
            {
                continue;
            }
            if (edit.Action != AdapterSettingEditAction.Set ||
                edit.Value is not JsonElement value ||
                value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(value.GetString()))
            {
                errors[edit.Key] = ["Device address is required."];
                continue;
            }
            address = value.GetString()!.Trim();
        }

        if (errors.Count > 0)
        {
            return new AdapterSettingsApplyResult { Errors = errors };
        }
        var updated = new AdapterConfiguration(Kind, JsonSerializer.SerializeToElement(new HDHomeRunPhysicalTunerConfiguration { Address = address }));
        var validation = Validate(updated);
        return validation.Count == 0
            ? new AdapterSettingsApplyResult { Configuration = updated }
            : new AdapterSettingsApplyResult { Errors = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["address"] = validation } };
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(AdapterConfiguration configuration)
    {
        var errors = new List<string>();
        if (!string.Equals(configuration.Kind, Kind, StringComparison.Ordinal))
        {
            errors.Add($"Adapter kind must be '{Kind}'.");
        }

        HDHomeRunPhysicalTunerConfiguration? typedConfiguration = null;
        try
        {
            typedConfiguration = configuration.Configuration.Deserialize<HDHomeRunPhysicalTunerConfiguration>(SerializerOptions);
        }
        catch (JsonException)
        {
            errors.Add("Configuration must be an object containing a string address.");
        }

        if (typedConfiguration is null)
        {
            if (errors.Count == 0)
            {
                errors.Add("Configuration must be an object containing a string address.");
            }
        }
        else if (!TryCreateBaseAddress(typedConfiguration.Address, out _))
        {
            errors.Add("Address must be a host name, IP address, or absolute HTTP or HTTPS address without a path, query, or fragment.");
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
        cancellationToken.ThrowIfCancellationRequested();

        var connection = await DiscoverAsync(configuration, cancellationToken);

        return new HDHomeRunPhysicalTunerDevice(
            deviceId,
            name,
            connection.DeviceInfo,
            connection.DeviceClient,
            connection.HttpClient,
            connection.Address,
            _deviceService,
            _loggerFactory.CreateLogger<HDHomeRunPhysicalTunerDevice>());
    }

    /// <inheritdoc />
    public async ValueTask<PhysicalDeviceProbeResult> ProbeAsync(AdapterConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var connection = await DiscoverAsync(configuration, cancellationToken);
        connection.HttpClient.Dispose();
        var deviceInfo = connection.DeviceInfo;
        return new PhysicalDeviceProbeResult
        {
            SuggestedName = $"{deviceInfo.FriendlyName} ({deviceInfo.DeviceID})",
            Details =
            [
                Detail("Friendly name", deviceInfo.FriendlyName),
                Detail("Model", deviceInfo.ModelNumber),
                Detail("Device ID", deviceInfo.DeviceID),
                Detail("Tuners", deviceInfo.TunerCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Detail("Firmware", $"{deviceInfo.FirmwareName} {deviceInfo.FirmwareVersion}".Trim()),
                Detail("Base URL", deviceInfo.BaseURL)
            ]
        };
    }

    private async ValueTask<HDHomeRunConnection> DiscoverAsync(AdapterConfiguration configuration, CancellationToken cancellationToken)
    {
        var errors = Validate(configuration);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(configuration));
        }

        var typedConfiguration = configuration.Configuration.Deserialize<HDHomeRunPhysicalTunerConfiguration>(SerializerOptions)
            ?? throw new ArgumentException("Configuration must contain an address.", nameof(configuration));
        _ = TryCreateBaseAddress(typedConfiguration.Address, out var baseAddress);

        var httpClient = _httpClientFactory.CreateClient(HDHomeRunPhysicalTunerServiceCollectionExtensions.HttpClientName);
        try
        {
            httpClient.BaseAddress = baseAddress;
            var deviceClient = new HDHomeRunDeviceClient(_loggerFactory.CreateLogger<HDHomeRunDeviceClient>(), httpClient);
            var deviceInfo = await deviceClient.DiscoverDeviceAsync(cancellationToken);
            if (deviceInfo.TunerCount <= 0)
            {
                throw new InvalidOperationException($"HDHomeRun device '{deviceInfo.DeviceID}' reported invalid tuner count {deviceInfo.TunerCount}.");
            }

            return new HDHomeRunConnection(baseAddress.DnsSafeHost, httpClient, deviceClient, deviceInfo);
        }
        catch
        {
            httpClient.Dispose();
            throw;
        }
    }

    private static PhysicalDeviceProbeDetail Detail(string label, string value) => new() { Label = label, Value = value };

    private static bool TryCreateBaseAddress(string? address, out Uri baseAddress)
    {
        baseAddress = null!;
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var candidate = address.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = $"http://{candidate}";
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(parsed.Host)
            || parsed.AbsolutePath != "/"
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment)
            || !string.IsNullOrEmpty(parsed.UserInfo))
        {
            return false;
        }

        baseAddress = new UriBuilder(parsed) { Path = "/" }.Uri;
        return true;
    }

    private sealed record HDHomeRunConnection(string Address, HttpClient HttpClient, HDHomeRunDeviceClient DeviceClient, HDHomeRunDeviceInfo DeviceInfo);
}
