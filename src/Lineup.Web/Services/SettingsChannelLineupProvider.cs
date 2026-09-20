using Lineup.HDHomeRun.Device;
using Lineup.HDHomeRun.Device.Models;

namespace Lineup.Web.Services;

/// <summary>
/// Reads the current channel lineup from every enabled physical HDHomeRun profile.
/// </summary>
public sealed class SettingsChannelLineupProvider : IChannelLineupProvider
{
    private readonly IAppSettingsService _settings;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a provider for all configured physical tuners.
    /// </summary>
    public SettingsChannelLineupProvider(IAppSettingsService settings, IHttpClientFactory httpClientFactory)
    {
        _settings = settings;
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public async Task<List<HDHomeRunChannel>> FetchChannelLineupAsync(CancellationToken cancellationToken = default)
    {
        _settings.Settings.EnsureHdHomeRunProxyProfiles();
        var baseUris = _settings.Settings.HdHomeRunProxyProfiles
            .Where(profile => profile.Enabled)
            .Select(profile => HdHomeRunProxyProfileResolver.GetPhysicalBaseUri(profile.PhysicalAddress))
            .DistinctBy(baseUri => baseUri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (baseUris.Length == 0)
        {
            throw new InvalidOperationException("At least one enabled physical HDHomeRun profile is required to filter guide data.");
        }

        var client = _httpClientFactory.CreateClient("HdHomeRunProxyDevice");
        var channels = new List<HDHomeRunChannel>();
        foreach (var baseUri in baseUris)
        {
            var lineup = await client.GetFromJsonAsync<List<HDHomeRunChannel>>(new Uri(baseUri, "lineup.json"), cancellationToken) ??
                throw new InvalidOperationException($"No HDHomeRun channel lineup was returned by {baseUri}.");

            channels.AddRange(lineup);
        }

        return channels
            .Where(channel => !string.IsNullOrWhiteSpace(channel.GuideNumber))
            .DistinctBy(channel => channel.GuideNumber.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
