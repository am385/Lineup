using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace Lineup.HDHomeRun.Api;

/// <summary>
/// Downloads guide data from the SiliconDust XMLTV API.
/// </summary>
public class HDHomeRunApiClient
{
    private const string XmltvGuideUrl = "https://api.hdhomerun.com/api/xmltv";
    private const int MaximumAttempts = 3;

    private readonly ILogger<HDHomeRunApiClient> _logger;
    private readonly HttpClient _httpClient;
    private readonly IDeviceAuthProvider _deviceAuthProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="HDHomeRunApiClient"/> class.
    /// </summary>
    public HDHomeRunApiClient(ILogger<HDHomeRunApiClient> logger, HttpClient httpClient, IDeviceAuthProvider deviceAuthProvider)
    {
        _logger = logger;
        _httpClient = httpClient;
        _deviceAuthProvider = deviceAuthProvider;
    }

    /// <summary>
    /// Downloads the complete XMLTV guide available to the configured HDHomeRun devices.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the download.</param>
    /// <returns>The canonical XMLTV document bytes.</returns>
    public async Task<byte[]> FetchXmltvAsync(CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            var deviceAuth = await _deviceAuthProvider.GetDeviceAuthAsync();
            if (string.IsNullOrWhiteSpace(deviceAuth))
            {
                throw new InvalidOperationException("No HDHomeRun DeviceAuth value is available.");
            }

            var requestUri = new Uri($"{XmltvGuideUrl}?DeviceAuth={Uri.EscapeDataString(deviceAuth)}");
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            request.Headers.UserAgent.ParseAdd("Lineup/2.0 (+https://github.com/am385/Lineup)");

            _logger.LogInformation("Downloading the SiliconDust XMLTV guide");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Forbidden && attempt < MaximumAttempts)
            {
                var error = await ReadErrorAsync(response, deviceAuth, cancellationToken);
                _logger.LogWarning("SiliconDust rejected XMLTV attempt {Attempt} of {MaximumAttempts} ({Error}); refreshing DeviceAuth before retrying", attempt, MaximumAttempts, error);
                await Task.Delay(TimeSpan.FromSeconds(attempt == 1 ? 2 : 5), cancellationToken);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                var error = await ReadErrorAsync(response, deviceAuth, cancellationToken);
                throw new HttpRequestException($"SiliconDust rejected the XMLTV request after DeviceAuth was refreshed: {error}", inner: null, response.StatusCode);
            }

            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (content.Length == 0)
            {
                throw new InvalidOperationException("The SiliconDust XMLTV API returned an empty document.");
            }

            _logger.LogInformation("Downloaded {ByteCount} bytes of SiliconDust XMLTV guide data", content.Length);
            return content;
        }

        throw new InvalidOperationException("The SiliconDust XMLTV request did not complete.");
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, string deviceAuth, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var sanitized = content.Replace(deviceAuth, "[redacted]", StringComparison.Ordinal).Trim();
        return string.IsNullOrEmpty(sanitized)
            ? $"{(int)response.StatusCode} {response.ReasonPhrase}"
            : sanitized[..Math.Min(sanitized.Length, 512)];
    }
}
