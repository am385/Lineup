using System.Text.Json;
using Lineup.HDHomeRun.Api.Models;
using Microsoft.Extensions.Logging;

namespace Lineup.HDHomeRun.Api;

/// <summary>
/// Service for interacting with the remote HDHomeRun API
/// Handles EPG data retrieval from api.hdhomerun.com
/// </summary>
public class HDHomeRunApiClient
{
    private const string ApiGuideUrl = "https://api.hdhomerun.com/api/guide";

    private readonly ILogger<HDHomeRunApiClient> _logger;
    private readonly HttpClient _httpClient;
    private readonly IDeviceAuthProvider _deviceAuthProvider;

    public HDHomeRunApiClient(
        ILogger<HDHomeRunApiClient> logger,
        HttpClient httpClient,
        IDeviceAuthProvider deviceAuthProvider)
    {
        _logger = logger;
        _httpClient = httpClient;
        _deviceAuthProvider = deviceAuthProvider;
    }

    /// <summary>
    /// Fetches raw EPG data from the HDHomeRun API for the specified time range.
    /// Returns the raw API response without any enrichment or filtering.
    /// </summary>
    /// <param name="days">Number of days to fetch</param>
    /// <param name="hours">Number of hours per request iteration</param>
    /// <returns>List of raw channel EPG segments from the API</returns>
    public async Task<List<HDHomeRunChannelEpgSegment>> FetchRawEpgDataAsync(int days, int hours)
    {
        var deviceAuth = await _deviceAuthProvider.GetDeviceAuthAsync();
        var allSegments = new List<HDHomeRunChannelEpgSegment>();
        var nextStartDate = DateTime.UtcNow;
        var endTime = nextStartDate.AddDays(days);

        _logger.LogInformation("Starting raw EPG data fetch for {Days} days with {Hours} hour intervals", days, hours);

        try
        {
            while (nextStartDate < endTime)
            {
                var segment = await FetchRawEpgSegmentAsync(startTimeUtc: nextStartDate, durationHours: (uint)hours);
                MergeSegments(allSegments, segment);
                nextStartDate = nextStartDate.AddHours(hours);
            }

            var totalPrograms = allSegments.Sum(s => s.Guide.Count);
            _logger.LogInformation("Raw EPG data fetch completed. Retrieved {ChannelCount} channels and {ProgramCount} programs",
                allSegments.Count, totalPrograms);

            return allSegments;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error fetching raw EPG data for start time {NextStartDate}", nextStartDate);
            return allSegments;
        }
    }

    /// <summary>
    /// Fetches a single segment of raw EPG data starting from a specific time.
    /// Use this method for incremental fetching to avoid API rate limiting.
    /// </summary>
    /// <param name="startTimeUtc">Optional. The start time for the EPG data segment. Default = current time</param>
    /// <param name="durationHours">Optional. Number of hours of data to fetch. Default = 4, Max = 24</param>
    /// <param name="channel">Optional. Specific channel number to request guide for. Default = all channels</param>
    /// <returns>List of raw channel EPG segments from the API</returns>
    public async Task<List<HDHomeRunChannelEpgSegment>> FetchRawEpgSegmentAsync(
        DateTime? startTimeUtc = null,
        uint? durationHours = null,
        string? channel = null)
    {
        var deviceAuth = await _deviceAuthProvider.GetDeviceAuthAsync();
        var requestUrl = $"{ApiGuideUrl}?DeviceAuth={deviceAuth}";

        // Add optional Start parameter
        if (startTimeUtc.HasValue)
        {
            var urlStartDate = new DateTimeOffset(startTimeUtc.Value).ToUnixTimeSeconds();
            requestUrl += $"&Start={urlStartDate}";
        }

        // Add optional Duration parameter (max 24 hours)
        if (durationHours.HasValue)
        {
            var duration = Math.Min(durationHours.Value, 24u);
            requestUrl += $"&Duration={duration}";
        }

        // Add optional Channel parameter
        if (!string.IsNullOrEmpty(channel))
        {
            requestUrl += $"&Channel={Uri.EscapeDataString(channel)}";
        }

        var effectiveStartTime = startTimeUtc ?? DateTime.UtcNow;
        _logger.LogInformation("Fetching raw EPG segment starting at {StartTime:yyyy-MM-dd HH:mm:ss}{Duration}{Channel}",
            effectiveStartTime,
            durationHours.HasValue ? $", duration: {durationHours}h" : "",
            !string.IsNullOrEmpty(channel) ? $", channel: {channel}" : "");

        try
        {
            var jsonString = await _httpClient.GetStringAsync(requestUrl);
            List<HDHomeRunChannelEpgSegment>? epgSegment;

            try
            {
                epgSegment = JsonSerializer.Deserialize<List<HDHomeRunChannelEpgSegment>>(jsonString);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to deserialize EPG segment JSON response");
                throw new InvalidOperationException("Failed to deserialize EPG segment JSON response", e);
            }

            var result = epgSegment ?? [];
            var totalPrograms = result.Sum(s => s.Guide.Count);

            _logger.LogInformation("Raw EPG segment fetch completed. Retrieved {ChannelCount} channels and {ProgramCount} programs",
                result.Count, totalPrograms);

            return result;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error fetching raw EPG segment for start time {StartTime}", effectiveStartTime);
            throw;
        }
    }

    /// <summary>
    /// Merges new segment data into existing segments, avoiding duplicate programs
    /// </summary>
    private void MergeSegments(List<HDHomeRunChannelEpgSegment> existing, List<HDHomeRunChannelEpgSegment> newSegments)
    {
        foreach (var newSegment in newSegments)
        {
            var existingSegment = existing.FirstOrDefault(s => s.GuideNumber == newSegment.GuideNumber);

            if (existingSegment == null)
            {
                existing.Add(newSegment);
            }
            else
            {
                // Merge programs, avoiding duplicates
                foreach (var program in newSegment.Guide)
                {
                    var isDuplicate = existingSegment.Guide.Any(p =>
                        p.StartTime == program.StartTime &&
                        p.Title == program.Title);

                    if (!isDuplicate)
                    {
                        existingSegment.Guide.Add(program);
                    }
                }
            }
        }
    }
}
