using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Lineup.HDHomeRun.Device.Protocol;

/// <summary>
/// Controls an HDHomeRun device using the HTTP API.
/// This is an alternative to the native control protocol that works
/// reliably on all modern HDHomeRun devices.
/// 
/// HTTP API documentation: https://info.hdhomerun.com/info/http_api
/// </summary>
public class HDHomeRunHttpControl : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HDHomeRunHttpControl> _logger;
    private readonly string _baseUrl;

    /// <summary>
    /// Creates a new HTTP control client
    /// </summary>
    public HDHomeRunHttpControl(IPAddress deviceAddress, ILogger<HDHomeRunHttpControl> logger)
        : this($"http://{deviceAddress}", logger)
    {
    }

    /// <summary>
    /// Creates a new HTTP control client with a base URL
    /// </summary>
    public HDHomeRunHttpControl(string baseUrl, ILogger<HDHomeRunHttpControl> logger)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _logger = logger;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    /// <summary>
    /// Gets device discovery information
    /// </summary>
    public async Task<HttpDiscoverResponse?> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<HttpDiscoverResponse>(
                "/" + DeviceEndpoints.DiscoverJson, cancellationToken);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get discover.json");
            return null;
        }
    }

    /// <summary>
    /// Gets tuner status
    /// </summary>
    public async Task<List<HttpTunerStatus>> GetTunerStatusAsync(CancellationToken cancellationToken = default)
    {
        var statuses = new List<HttpTunerStatus>();

        try
        {
            // The HTTP API returns tuner status at /status.json on modern devices
            var discover = await DiscoverAsync(cancellationToken);
            var tunerCount = discover?.TunerCount ?? 4;

            for (int i = 0; i < tunerCount; i++)
            {
                var status = await GetTunerStatusAsync(i, cancellationToken);
                if (status != null)
                {
                    statuses.Add(status);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get tuner status");
        }

        return statuses;
    }

    /// <summary>
    /// Gets status for a specific tuner via HTTP API
    /// </summary>
    public async Task<HttpTunerStatus?> GetTunerStatusAsync(int tunerIndex, CancellationToken cancellationToken = default)
    {
        try
        {
            // Try status.json first (available on modern devices like FLEX)
            try
            {
                var statusJson = await _httpClient.GetFromJsonAsync<List<HttpStatusEntry>>(
                    "/status.json", cancellationToken);

                if (statusJson != null)
                {
                    var tunerEntry = statusJson.FirstOrDefault(s =>
                        s.Resource?.StartsWith($"tuner{tunerIndex}") == true);

                    if (tunerEntry != null)
                    {
                        return new HttpTunerStatus
                        {
                            TunerIndex = tunerIndex,
                            IsActive = !string.IsNullOrEmpty(tunerEntry.VctName),
                            VirtualChannel = tunerEntry.VctNumber,
                            Channel = tunerEntry.Frequency,
                            TargetIP = tunerEntry.TargetIP,
                            SignalStrengthPercent = tunerEntry.SignalStrengthPercent,
                            SignalQualityPercent = tunerEntry.SignalQualityPercent,
                            SymbolQualityPercent = tunerEntry.SymbolQualityPercent,
                            NetworkRate = tunerEntry.NetworkRate
                        };
                    }

                    // Tuner exists but not in use
                    return new HttpTunerStatus
                    {
                        TunerIndex = tunerIndex,
                        IsActive = false
                    };
                }
            }
            catch (HttpRequestException)
            {
                // status.json not available, try legacy method
            }

            // Fallback: Try tuners.html (legacy method)
            var response = await _httpClient.GetStringAsync($"/tuners.html", cancellationToken);

            var status = new HttpTunerStatus
            {
                TunerIndex = tunerIndex,
                RawResponse = response
            };

            // Check if tuner is in use by looking for channel info
            if (response.Contains("none") || response.Contains("not in use"))
            {
                status.IsActive = false;
            }
            else if (response.Contains("ch=") || response.Contains("vchannel"))
            {
                status.IsActive = true;
            }

            return status;
        }
        catch (HttpRequestException)
        {
            // Tuner doesn't exist
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get status for tuner {TunerIndex}", tunerIndex);
            return null;
        }
    }

    /// <summary>
    /// Tunes to a channel (virtual channel like "5.1")
    /// </summary>
    public async Task<bool> TuneChannelAsync(int tunerIndex, string channel, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync($"/tuner{tunerIndex}/v{channel}", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to tune tuner {TunerIndex} to channel {Channel}", tunerIndex, channel);
            return false;
        }
    }

    /// <summary>
    /// Gets the lineup (channel list)
    /// </summary>
    public async Task<List<HttpLineupItem>?> GetLineupAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<HttpLineupItem>>(
                "/" + DeviceEndpoints.LineupJson, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get lineup.json");
            return null;
        }
    }

    /// <summary>
    /// Gets the HTTP streaming URL for a channel
    /// </summary>
    public string GetStreamUrl(string channel, string? transcodeProfile = null)
    {
        var url = $"{_baseUrl}/auto/v{channel}";
        if (!string.IsNullOrEmpty(transcodeProfile))
        {
            url += $"?transcode={transcodeProfile}";
        }
        return url;
    }

    /// <summary>
    /// Restarts the device by accessing the system page
    /// Note: This may not work on all devices - prefer the native control protocol for restart
    /// </summary>
    public async Task<bool> RestartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // There's no direct HTTP API for restart, but we can try the system page
            var response = await _httpClient.PostAsync("/system.post?restart=1", null, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restart device via HTTP API");
            return false;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

/// <summary>
/// Response from /discover.json
/// </summary>
public class HttpDiscoverResponse
{
    [JsonPropertyName("FriendlyName")]
    public string? FriendlyName { get; set; }

    [JsonPropertyName("ModelNumber")]
    public string? ModelNumber { get; set; }

    [JsonPropertyName("FirmwareName")]
    public string? FirmwareName { get; set; }

    [JsonPropertyName("FirmwareVersion")]
    public string? FirmwareVersion { get; set; }

    [JsonPropertyName("DeviceID")]
    public string? DeviceID { get; set; }

    [JsonPropertyName("DeviceAuth")]
    public string? DeviceAuth { get; set; }

    [JsonPropertyName("BaseURL")]
    public string? BaseURL { get; set; }

    [JsonPropertyName("LineupURL")]
    public string? LineupURL { get; set; }

    [JsonPropertyName("TunerCount")]
    public int TunerCount { get; set; }

    [JsonPropertyName("Legacy")]
    public int Legacy { get; set; }
}

/// <summary>
/// Tuner status from HTTP API
/// </summary>
public class HttpTunerStatus
{
    public int TunerIndex { get; set; }
    public bool IsActive { get; set; }
    public string? Channel { get; set; }
    public string? VirtualChannel { get; set; }
    public string? TargetIP { get; set; }
    public string? RawResponse { get; set; }
    public int? SignalStrengthPercent { get; set; }
    public int? SignalQualityPercent { get; set; }
    public int? SymbolQualityPercent { get; set; }
    public int? NetworkRate { get; set; }
}

/// <summary>
/// Status entry from /status.json (modern devices)
/// </summary>
public class HttpStatusEntry
{
    [JsonPropertyName("Resource")]
    public string? Resource { get; set; }

    [JsonPropertyName("VctNumber")]
    public string? VctNumber { get; set; }

    [JsonPropertyName("VctName")]
    public string? VctName { get; set; }

    [JsonPropertyName("Frequency")]
    public string? Frequency { get; set; }

    [JsonPropertyName("SignalStrengthPercent")]
    public int? SignalStrengthPercent { get; set; }

    [JsonPropertyName("SignalQualityPercent")]
    public int? SignalQualityPercent { get; set; }

    [JsonPropertyName("SymbolQualityPercent")]
    public int? SymbolQualityPercent { get; set; }

    [JsonPropertyName("TargetIP")]
    public string? TargetIP { get; set; }

    [JsonPropertyName("NetworkRate")]
    public int? NetworkRate { get; set; }
}

/// <summary>
/// Lineup item from /lineup.json
/// </summary>
public class HttpLineupItem
{
    [JsonPropertyName("GuideNumber")]
    public string? GuideNumber { get; set; }

    [JsonPropertyName("GuideName")]
    public string? GuideName { get; set; }

    [JsonPropertyName("VideoCodec")]
    public string? VideoCodec { get; set; }

    [JsonPropertyName("AudioCodec")]
    public string? AudioCodec { get; set; }

    [JsonPropertyName("HD")]
    public int HD { get; set; }

    [JsonPropertyName("Favorite")]
    public int Favorite { get; set; }

    [JsonPropertyName("URL")]
    public string? URL { get; set; }
}
