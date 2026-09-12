using System.Text;
using System.Xml.Linq;
using Lineup.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Lineup.Web.Controllers;

/// <summary>
/// Exposes HTTP discovery and lineup contracts for virtual HDHomeRun profiles.
/// </summary>
[ApiController]
public sealed class HdHomeRunProxyController : ControllerBase
{
    private readonly IHdHomeRunProxyProfileProvider _profiles;
    private readonly IHdHomeRunProxyDeviceClient _deviceClient;
    private readonly IAppSettingsService _settings;

    /// <summary>
    /// Initializes the virtual HDHomeRun HTTP controller.
    /// </summary>
    /// <param name="profiles">Provider for virtual device profiles.</param>
    /// <param name="deviceClient">Client for profile-scoped physical lineups.</param>
    /// <param name="settings">Provides virtual-tuner transcoding settings.</param>
    public HdHomeRunProxyController(IHdHomeRunProxyProfileProvider profiles, IHdHomeRunProxyDeviceClient deviceClient, IAppSettingsService settings)
    {
        _profiles = profiles;
        _deviceClient = deviceClient;
        _settings = settings;
    }

    /// <summary>
    /// Returns virtual device metadata for the primary or selected profile.
    /// </summary>
    /// <param name="virtualDeviceId">Optional scoped virtual DeviceID.</param>
    /// <returns>The virtual device discovery document.</returns>
    [HttpGet("/discover.json")]
    [HttpGet("/hdhomerun/{virtualDeviceId}/discover.json")]
    public async Task<IActionResult> Discover(string? virtualDeviceId = null)
    {
        var profile = await ResolveProfileAsync(virtualDeviceId);
        if (profile == null)
        {
            return ProfileUnavailable(virtualDeviceId);
        }

        var baseUri = profile.GetHttpBaseUri(GetRequestRootUri());
        var version = typeof(HdHomeRunProxyController).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        return Ok(new HdHomeRunProxyDiscoverResponse
        {
            FriendlyName = profile.FriendlyName,
            ModelNumber = "LINEUP-1",
            FirmwareName = "lineup_proxy",
            FirmwareVersion = version,
            DeviceId = profile.DeviceIdText,
            DeviceAuth = profile.DeviceAuth,
            BaseUrl = baseUri.AbsoluteUri.TrimEnd('/'),
            LineupUrl = new Uri(baseUri, "lineup.json").AbsoluteUri,
            TunerCount = profile.TunerCount
        });
    }

    /// <summary>
    /// Returns a profile's physical channel lineup with profile-consistent stream URLs.
    /// </summary>
    /// <param name="virtualDeviceId">Optional scoped virtual DeviceID.</param>
    /// <returns>The virtual device channel lineup.</returns>
    [HttpGet("/lineup.json")]
    [HttpGet("/hdhomerun/{virtualDeviceId}/lineup.json")]
    public async Task<IActionResult> Lineup(string? virtualDeviceId = null)
    {
        var profile = await ResolveProfileAsync(virtualDeviceId);
        if (profile == null)
        {
            return ProfileUnavailable(virtualDeviceId);
        }

        try
        {
            var baseUri = profile.GetHttpBaseUri(GetRequestRootUri());
            var channels = await _deviceClient.FetchLineupAsync(profile, HttpContext.RequestAborted);
            return Ok(channels.Select(channel => HdHomeRunProxyChannel.Create(channel, baseUri, _settings.Settings.VirtualTunerVideoMode)));
        }
        catch (InvalidOperationException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "The physical HDHomeRun lineup is unavailable." });
        }
    }

    /// <summary>
    /// Returns the static scan status of a virtual profile.
    /// </summary>
    /// <param name="virtualDeviceId">Optional scoped virtual DeviceID.</param>
    /// <returns>A scan-disabled lineup status document.</returns>
    [HttpGet("/lineup_status.json")]
    [HttpGet("/hdhomerun/{virtualDeviceId}/lineup_status.json")]
    public async Task<IActionResult> LineupStatus(string? virtualDeviceId = null)
    {
        var profile = await ResolveProfileAsync(virtualDeviceId);
        return profile == null ? ProfileUnavailable(virtualDeviceId) : Ok(new HdHomeRunProxyLineupStatus());
    }

    /// <summary>
    /// Returns a profile-consistent UPnP device description.
    /// </summary>
    /// <param name="virtualDeviceId">Optional scoped virtual DeviceID.</param>
    /// <returns>An XML device description.</returns>
    [HttpGet("/device.xml")]
    [HttpGet("/hdhomerun/{virtualDeviceId}/device.xml")]
    public async Task<IActionResult> DeviceDescription(string? virtualDeviceId = null)
    {
        var profile = await ResolveProfileAsync(virtualDeviceId);
        if (profile == null)
        {
            return ProfileUnavailable(virtualDeviceId);
        }

        var baseUri = profile.GetHttpBaseUri(GetRequestRootUri());
        XNamespace deviceNamespace = "urn:schemas-upnp-org:device-1-0";
        var document = new XDocument(
            new XElement(deviceNamespace + "root",
                new XElement(deviceNamespace + "URLBase", baseUri.AbsoluteUri.TrimEnd('/')),
                new XElement(deviceNamespace + "specVersion", new XElement(deviceNamespace + "major", 1), new XElement(deviceNamespace + "minor", 0)),
                new XElement(deviceNamespace + "device",
                    new XElement(deviceNamespace + "deviceType", "urn:schemas-upnp-org:device:MediaServer:1"),
                    new XElement(deviceNamespace + "friendlyName", profile.FriendlyName),
                    new XElement(deviceNamespace + "manufacturer", "Lineup"),
                    new XElement(deviceNamespace + "modelName", "Lineup HDHomeRun Proxy"),
                    new XElement(deviceNamespace + "modelNumber", "LINEUP-1"),
                    new XElement(deviceNamespace + "serialNumber", profile.DeviceIdText),
                    new XElement(deviceNamespace + "UDN", $"uuid:{profile.DeviceIdText}"))));
        return Content(document.ToString(SaveOptions.DisableFormatting), "application/xml", Encoding.UTF8);
    }

    private async Task<HdHomeRunProxyProfileSnapshot?> ResolveProfileAsync(string? virtualDeviceId)
    {
        return string.IsNullOrWhiteSpace(virtualDeviceId)
            ? await _profiles.GetPrimaryProfileAsync(HttpContext.RequestAborted)
            : await _profiles.FindProfileAsync(virtualDeviceId, HttpContext.RequestAborted);
    }

    private IActionResult ProfileUnavailable(string? virtualDeviceId)
    {
        return string.IsNullOrWhiteSpace(virtualDeviceId)
            ? StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "The primary physical HDHomeRun device is unavailable." })
            : NotFound(new { error = "The requested virtual HDHomeRun device was not found." });
    }

    private Uri GetRequestRootUri()
    {
        return new Uri($"{Request.Scheme}://{Request.Host}{Request.PathBase}/");
    }
}
