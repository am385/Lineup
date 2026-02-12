using System.Net.Http.Json;
using Lineup.HDHomeRun.Device.Models;
using Microsoft.Extensions.Logging;

namespace Lineup.HDHomeRun.Device;

/// <summary>
/// Service for interacting with the local HDHomeRun device.
/// Handles device discovery and channel lineup retrieval.
/// Uses IDeviceAddressProvider to dynamically resolve the device address,
/// or falls back to HttpClient's BaseAddress if no provider is supplied.
/// </summary>
public class HDHomeRunDeviceClient
{
    private readonly ILogger<HDHomeRunDeviceClient> _logger;
    private readonly HttpClient _httpClient;
    private readonly IDeviceAddressProvider? _addressProvider;

    /// <summary>
    /// Gets the current base URI for the device.
    /// Uses the address provider if available, otherwise falls back to HttpClient's BaseAddress.
    /// </summary>
    private Uri CurrentBaseUri => _addressProvider?.BaseUri ?? _httpClient.BaseAddress
        ?? throw new InvalidOperationException("No device address configured. Either provide an IDeviceAddressProvider or configure HttpClient.BaseAddress.");

    /// <summary>
    /// Initializes a new instance of HDHomeRunDeviceClient.
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="httpClient">HttpClient for making requests</param>
    /// <param name="addressProvider">Optional provider for dynamic device address resolution. If null, HttpClient.BaseAddress is used.</param>
    public HDHomeRunDeviceClient(
        ILogger<HDHomeRunDeviceClient> logger,
        HttpClient httpClient,
        IDeviceAddressProvider? addressProvider = null)
    {
        _logger = logger;
        _httpClient = httpClient;
        _addressProvider = addressProvider;

        // Validate that we have at least one way to get the address
        if (_addressProvider == null && _httpClient.BaseAddress == null)
        {
            throw new ArgumentException(
                "Either an IDeviceAddressProvider must be supplied or HttpClient must have BaseAddress configured",
                nameof(httpClient));
        }
    }

    /// <summary>
    /// Discovers the device information from the HDHomeRun device
    /// </summary>
    /// <returns>Complete device information including authentication token</returns>
    /// <exception cref="InvalidOperationException">Thrown when device cannot be discovered</exception>
    public async Task<HDHomeRunDeviceInfo> DiscoverDeviceAsync()
    {
        var baseUri = CurrentBaseUri;
        try
        {
            _logger.LogInformation("Discovering HDHomeRun device at {BaseAddress}", baseUri);
            var requestUri = new Uri(baseUri, DeviceEndpoints.DiscoverJson);
            var deviceInfo = await _httpClient.GetFromJsonAsync<HDHomeRunDeviceInfo>(requestUri);

            if (deviceInfo == null)
            {
                throw new InvalidOperationException($"No device found at {baseUri}");
            }

            _logger.LogInformation("Discovered device: {FriendlyName} ({ModelNumber}) with Device ID: {DeviceID}",
                deviceInfo.FriendlyName, deviceInfo.ModelNumber, deviceInfo.DeviceID);

            return deviceInfo;
        }
        catch (Exception e) when (e is not InvalidOperationException)
        {
            _logger.LogError(e, "Error discovering device at {BaseAddress}", baseUri);
            throw new InvalidOperationException($"Error discovering device at {baseUri}", e);
        }
    }

    /// <summary>
    /// Discovers the device authentication token from the HDHomeRun device
    /// </summary>
    /// <returns>Device authentication token</returns>
    /// <exception cref="InvalidOperationException">Thrown when device auth cannot be discovered</exception>
    public async Task<string> DiscoverDeviceAuthAsync()
    {
        var deviceInfo = await DiscoverDeviceAsync();
        return deviceInfo.DeviceAuth;
    }

    /// <summary>
    /// Fetches the list of channels from the HDHomeRun device
    /// </summary>
    /// <returns>List of channels from the device lineup</returns>
    /// <exception cref="InvalidOperationException">Thrown when channels cannot be retrieved</exception>
    public async Task<List<HDHomeRunChannel>> FetchChannelLineupAsync()
    {
        var baseUri = CurrentBaseUri;
        try
        {
            _logger.LogInformation("Fetching channel lineup from {BaseAddress}", baseUri);

            var requestUri = new Uri(baseUri, DeviceEndpoints.LineupJson);
            var channels = await _httpClient.GetFromJsonAsync<List<HDHomeRunChannel>>(requestUri);

            if (channels == null)
            {
                throw new InvalidOperationException($"Failed to retrieve channel lineup from {baseUri}");
            }

            _logger.LogInformation("Channel lineup retrieved successfully from {BaseAddress}. Found {ChannelCount} channels",
                baseUri, channels.Count);

            return channels;
        }
        catch (Exception e) when (e is not InvalidOperationException)
        {
            _logger.LogError(e, "Error fetching channel lineup from {BaseAddress}", baseUri);
            throw new InvalidOperationException($"Error fetching channel lineup from {baseUri}", e);
        }
    }
}
