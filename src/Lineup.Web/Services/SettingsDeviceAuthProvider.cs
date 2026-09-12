using Lineup.HDHomeRun.Api;
using Lineup.HDHomeRun.Device.Models;

namespace Lineup.Web.Services;

/// <summary>
/// Reads current DeviceAuth values from every enabled physical HDHomeRun profile.
/// </summary>
public sealed class SettingsDeviceAuthProvider : IDeviceAuthProvider
{
    private readonly IAppSettingsService _settings;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsDeviceAuthProvider"/> class.
    /// </summary>
    public SettingsDeviceAuthProvider(IAppSettingsService settings, IHttpClientFactory httpClientFactory)
    {
        _settings = settings;
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public async Task<string> GetDeviceAuthAsync()
    {
        _settings.Settings.EnsureHdHomeRunProxyProfiles();
        var profiles = _settings.Settings.HdHomeRunProxyProfiles
            .Where(profile => profile.Enabled)
            .DistinctBy(profile => profile.PhysicalAddress.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (profiles.Length == 0)
        {
            throw new InvalidOperationException("At least one enabled physical HDHomeRun profile is required to download guide data.");
        }

        var client = _httpClientFactory.CreateClient("HdHomeRunProxyDevice");
        var deviceAuthValues = new List<string>(profiles.Length);
        foreach (var profile in profiles)
        {
            var baseUri = HdHomeRunProxyProfileResolver.GetPhysicalBaseUri(profile.PhysicalAddress);
            var device = await client.GetFromJsonAsync<HDHomeRunDeviceInfo>(new Uri(baseUri, "discover.json"))
                ?? throw new InvalidOperationException($"No HDHomeRun device metadata was returned by {baseUri}.");
            if (string.IsNullOrWhiteSpace(device.DeviceAuth))
            {
                throw new InvalidOperationException($"The HDHomeRun device at {baseUri} did not return DeviceAuth.");
            }

            deviceAuthValues.Add(device.DeviceAuth);
        }

        return string.Concat(deviceAuthValues);
    }
}
