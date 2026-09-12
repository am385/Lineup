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
    private readonly Uri _deviceUri;

    /// <summary>
    /// Creates an HTTP control client with a supplied factory-managed transport.
    /// </summary>
    /// <param name="baseUrl">The device base URL.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="httpClient">The HTTP transport.</param>
    public HDHomeRunHttpControl(string baseUrl, ILogger<HDHomeRunHttpControl> logger, HttpClient httpClient)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _deviceUri = new Uri(_baseUrl, UriKind.Absolute);
        _logger = logger;
        _httpClient = httpClient;
        _httpClient.BaseAddress ??= new Uri(_baseUrl);
    }

    /// <summary>
    /// Gets device discovery information
    /// </summary>
    public async Task<HttpDiscoverResponse?> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<HttpDiscoverResponse>("/" + DeviceEndpoints.DiscoverJson, cancellationToken);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
                var statusJson = await _httpClient.GetFromJsonAsync<List<HttpStatusEntry>>("/status.json", cancellationToken);

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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
            return await _httpClient.GetFromJsonAsync<List<HttpLineupItem>>("/" + DeviceEndpoints.LineupJson, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
        var builder = new UriBuilder(_deviceUri.Scheme, _deviceUri.Host, DeviceEndpoints.StreamingPort)
        {
            Path = $"auto/v{Uri.EscapeDataString(channel)}",
            Query = string.IsNullOrEmpty(transcodeProfile)
                ? string.Empty
                : $"transcode={Uri.EscapeDataString(transcodeProfile)}"
        };
        return builder.Uri.AbsoluteUri;
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restart device via HTTP API");
            return false;
        }
    }

    /// <summary>
    /// Releases resources used by this instance.
    /// </summary>
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
    /// <summary>
    /// Gets or sets friendly name.
    /// </summary>
    [JsonPropertyName("FriendlyName")]
    public string? FriendlyName { get; set; }

    /// <summary>
    /// Gets or sets model number.
    /// </summary>
    [JsonPropertyName("ModelNumber")]
    public string? ModelNumber { get; set; }

    /// <summary>
    /// Gets or sets firmware name.
    /// </summary>
    [JsonPropertyName("FirmwareName")]
    public string? FirmwareName { get; set; }

    /// <summary>
    /// Gets or sets firmware version.
    /// </summary>
    [JsonPropertyName("FirmwareVersion")]
    public string? FirmwareVersion { get; set; }

    /// <summary>
    /// Gets or sets device id.
    /// </summary>
    [JsonPropertyName("DeviceID")]
    public string? DeviceID { get; set; }

    /// <summary>
    /// Gets or sets device auth.
    /// </summary>
    [JsonPropertyName("DeviceAuth")]
    public string? DeviceAuth { get; set; }

    /// <summary>
    /// Gets or sets base url.
    /// </summary>
    [JsonPropertyName("BaseURL")]
    public string? BaseURL { get; set; }

    /// <summary>
    /// Gets or sets lineup url.
    /// </summary>
    [JsonPropertyName("LineupURL")]
    public string? LineupURL { get; set; }

    /// <summary>
    /// Gets or sets tuner count.
    /// </summary>
    [JsonPropertyName("TunerCount")]
    public int TunerCount { get; set; }

    /// <summary>
    /// Gets or sets legacy.
    /// </summary>
    [JsonPropertyName("Legacy")]
    public int Legacy { get; set; }
}

/// <summary>
/// Tuner status from HTTP API
/// </summary>
public class HttpTunerStatus
{
    /// <summary>
    /// Gets or sets tuner index.
    /// </summary>
    public int TunerIndex { get; set; }
    /// <summary>
    /// Gets or sets is active.
    /// </summary>
    public bool IsActive { get; set; }
    /// <summary>
    /// Gets or sets channel.
    /// </summary>
    public string? Channel { get; set; }
    /// <summary>
    /// Gets or sets virtual channel.
    /// </summary>
    public string? VirtualChannel { get; set; }
    /// <summary>
    /// Gets or sets target ip.
    /// </summary>
    public string? TargetIP { get; set; }
    /// <summary>
    /// Gets or sets raw response.
    /// </summary>
    public string? RawResponse { get; set; }
    /// <summary>
    /// Gets or sets signal strength percent.
    /// </summary>
    public int? SignalStrengthPercent { get; set; }
    /// <summary>
    /// Gets or sets signal quality percent.
    /// </summary>
    public int? SignalQualityPercent { get; set; }
    /// <summary>
    /// Gets or sets symbol quality percent.
    /// </summary>
    public int? SymbolQualityPercent { get; set; }
    /// <summary>
    /// Gets or sets network rate.
    /// </summary>
    public int? NetworkRate { get; set; }
}

/// <summary>
/// Status entry from /status.json (modern devices)
/// </summary>
public class HttpStatusEntry
{
    /// <summary>
    /// Gets or sets resource.
    /// </summary>
    [JsonPropertyName("Resource")]
    public string? Resource { get; set; }

    /// <summary>
    /// Gets or sets vct number.
    /// </summary>
    [JsonPropertyName("VctNumber")]
    public string? VctNumber { get; set; }

    /// <summary>
    /// Gets or sets vct name.
    /// </summary>
    [JsonPropertyName("VctName")]
    public string? VctName { get; set; }

    /// <summary>
    /// Gets or sets frequency.
    /// </summary>
    [JsonPropertyName("Frequency")]
    public string? Frequency { get; set; }

    /// <summary>
    /// Gets or sets signal strength percent.
    /// </summary>
    [JsonPropertyName("SignalStrengthPercent")]
    public int? SignalStrengthPercent { get; set; }

    /// <summary>
    /// Gets or sets signal quality percent.
    /// </summary>
    [JsonPropertyName("SignalQualityPercent")]
    public int? SignalQualityPercent { get; set; }

    /// <summary>
    /// Gets or sets symbol quality percent.
    /// </summary>
    [JsonPropertyName("SymbolQualityPercent")]
    public int? SymbolQualityPercent { get; set; }

    /// <summary>
    /// Gets or sets target ip.
    /// </summary>
    [JsonPropertyName("TargetIP")]
    public string? TargetIP { get; set; }

    /// <summary>
    /// Gets or sets network rate.
    /// </summary>
    [JsonPropertyName("NetworkRate")]
    public int? NetworkRate { get; set; }
}

/// <summary>
/// Lineup item from /lineup.json
/// </summary>
public class HttpLineupItem
{
    /// <summary>
    /// Gets or sets guide number.
    /// </summary>
    [JsonPropertyName("GuideNumber")]
    public string? GuideNumber { get; set; }

    /// <summary>
    /// Gets or sets guide name.
    /// </summary>
    [JsonPropertyName("GuideName")]
    public string? GuideName { get; set; }

    /// <summary>
    /// Gets or sets video codec.
    /// </summary>
    [JsonPropertyName("VideoCodec")]
    public string? VideoCodec { get; set; }

    /// <summary>
    /// Gets or sets audio codec.
    /// </summary>
    [JsonPropertyName("AudioCodec")]
    public string? AudioCodec { get; set; }

    /// <summary>
    /// Gets or sets hd.
    /// </summary>
    [JsonPropertyName("HD")]
    public int HD { get; set; }

    /// <summary>
    /// Gets or sets favorite.
    /// </summary>
    [JsonPropertyName("Favorite")]
    public int Favorite { get; set; }

    /// <summary>
    /// Gets or sets url.
    /// </summary>
    [JsonPropertyName("URL")]
    public string? URL { get; set; }
}
