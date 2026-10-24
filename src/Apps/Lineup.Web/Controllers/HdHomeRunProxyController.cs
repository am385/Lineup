using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace Lineup.Web.Controllers;

/// <summary>Exposes management-hosted HTTP contracts for virtual HDHomeRun outputs.</summary>
[ApiController]
public sealed class HdHomeRunProxyController : ControllerBase
{
    private readonly IHdHomeRunProxyProfileProvider _profiles;
    private readonly ILineupSettingsService _settings;
    private readonly StreamingTopologyStore _topologyStore;

    /// <summary>Initializes the management-hosted virtual HDHomeRun controller.</summary>
    public HdHomeRunProxyController(
        IHdHomeRunProxyProfileProvider profiles,
        ILineupSettingsService settings,
        StreamingTopologyStore topologyStore)
    {
        _profiles = profiles;
        _settings = settings;
        _topologyStore = topologyStore;
    }

    /// <summary>Returns virtual device metadata for the primary or selected profile.</summary>
    [HttpGet("/discover.json")]
    [HttpGet("/hdhomerun/{virtualDeviceId}/discover.json")]
    public async Task<IActionResult> Discover(string? virtualDeviceId = null)
    {
        var profile = await ResolveProfileAsync(virtualDeviceId);
        if (profile is null)
        {
            return ProfileUnavailable(virtualDeviceId);
        }
        var baseUri = profile.GetHttpBaseUri(GetRequestRootUri());
        var version = typeof(HdHomeRunProxyController).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        return Ok(HdHomeRunProxyResponseFactory.CreateDiscover(
            profile.FriendlyName,
            profile.DeviceIdText,
            profile.DeviceAuth,
            baseUri,
            profile.TunerCount,
            version));
    }

    /// <summary>Returns a profile's channel lineup as JSON.</summary>
    [HttpGet("/lineup.json")]
    [HttpGet("/hdhomerun/{virtualDeviceId}/lineup.json")]
    public Task<IActionResult> Lineup(string? virtualDeviceId = null) =>
        RenderLineupAsync(virtualDeviceId, channels =>
            Content(HdHomeRunProxyLineupFormatter.ToJson(channels), "application/json", Encoding.UTF8));

    /// <summary>Returns a profile's channel lineup as XML.</summary>
    [HttpGet("/lineup.xml")]
    [HttpGet("/hdhomerun/{virtualDeviceId}/lineup.xml")]
    public Task<IActionResult> LineupXml(string? virtualDeviceId = null) =>
        RenderLineupAsync(virtualDeviceId, channels =>
            Content(HdHomeRunProxyLineupFormatter.ToXml(channels), "text/xml", Encoding.UTF8));

    /// <summary>Returns a profile's channel lineup as extended M3U.</summary>
    [HttpGet("/lineup.m3u")]
    [HttpGet("/hdhomerun/{virtualDeviceId}/lineup.m3u")]
    public Task<IActionResult> LineupM3u(string? virtualDeviceId = null) =>
        RenderLineupAsync(virtualDeviceId, channels =>
            Content(HdHomeRunProxyLineupFormatter.ToM3u(channels), "text/plain", Encoding.UTF8));

    /// <summary>Returns the static scan status of a virtual profile.</summary>
    [HttpGet("/lineup_status.json")]
    [HttpGet("/hdhomerun/{virtualDeviceId}/lineup_status.json")]
    public async Task<IActionResult> LineupStatus(string? virtualDeviceId = null) =>
        await ResolveProfileAsync(virtualDeviceId) is null
            ? ProfileUnavailable(virtualDeviceId)
            : Ok(new HdHomeRunProxyLineupStatus());

    /// <summary>Returns a profile-consistent UPnP device description.</summary>
    [HttpGet("/device.xml")]
    [HttpGet("/hdhomerun/{virtualDeviceId}/device.xml")]
    public async Task<IActionResult> DeviceDescription(string? virtualDeviceId = null)
    {
        var profile = await ResolveProfileAsync(virtualDeviceId);
        if (profile is null)
        {
            return ProfileUnavailable(virtualDeviceId);
        }
        var baseUri = profile.GetHttpBaseUri(GetRequestRootUri());
        return Content(
            HdHomeRunProxyLineupFormatter.ToDeviceXml(baseUri, profile.FriendlyName, profile.DeviceIdText),
            "application/xml",
            Encoding.UTF8);
    }

    private async Task<HdHomeRunProxyProfileSnapshot?> ResolveProfileAsync(string? virtualDeviceId)
    {
        var profiles = await _profiles.GetProfilesAsync(HttpContext.RequestAborted);
        var managementProfiles = profiles.Where(profile => profile.VirtualDevice?.Hosting is null).ToArray();
        return string.IsNullOrWhiteSpace(virtualDeviceId)
            ? managementProfiles.FirstOrDefault()
            : managementProfiles.FirstOrDefault(profile =>
                string.Equals(profile.DeviceIdText, virtualDeviceId, StringComparison.OrdinalIgnoreCase));
    }

    private IActionResult ProfileUnavailable(string? virtualDeviceId) =>
        !string.IsNullOrWhiteSpace(virtualDeviceId)
            ? NotFound(new { error = "The requested virtual HDHomeRun device was not found." })
            : StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "No management-hosted virtual HDHomeRun device is available." });

    private async Task<IActionResult> RenderLineupAsync(
        string? virtualDeviceId,
        Func<IReadOnlyList<HdHomeRunProxyChannel>, IActionResult> render)
    {
        var profile = await ResolveProfileAsync(virtualDeviceId);
        if (profile is null)
        {
            return ProfileUnavailable(virtualDeviceId);
        }
        try
        {
            var baseUri = profile.GetHttpBaseUri(GetRequestRootUri());
            var virtualDevice = profile.VirtualDevice ??
                throw new InvalidOperationException("The virtual HDHomeRun topology definition is unavailable.");
            var channels = await _topologyStore.ReadActiveAsync(HttpContext.RequestAborted);
            var resolution = ChannelTopologyResolver.Resolve(
                virtualDevice,
                _settings.Settings.Streaming.PhysicalDevices,
                channels);
            return render(resolution.Channels.Select(channel => HdHomeRunProxyChannel.Create(channel, baseUri)).ToArray());
        }
        catch (InvalidOperationException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "The physical HDHomeRun lineup is unavailable." });
        }
    }

    private Uri GetRequestRootUri() =>
        new($"{Request.Scheme}://{Request.Host}{Request.PathBase}/");
}
