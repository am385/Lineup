using Lineup.HDHomeRun.Api;
using Lineup.HDHomeRun.Api.Models;
using Lineup.HDHomeRun.Device;
using Lineup.HDHomeRun.Device.Models;
using Lineup.Core.Converters;
using Lineup.Core.Models;
using Lineup.Core.Models.Xmltv;
using Lineup.Core.Services;
using Lineup.Core.Storage;
using Microsoft.Extensions.Logging;

namespace Lineup.Core;

/// <summary>
/// Orchestrates the EPG generation process
/// Coordinates between device service, API service, and converter
/// </summary>
public class EpgOrchestrator
{
    private readonly ILogger<EpgOrchestrator> _logger;
    private readonly HDHomeRunDeviceClient _deviceService;
    private readonly HDHomeRunApiClient _apiService;
    private readonly HDHomeRunToXmltvConverter _converter;
    private readonly IEpgDataProvider _epgDataProvider;
    private readonly IEpgRepository _repository;

    public EpgOrchestrator(
        ILogger<EpgOrchestrator> logger,
        HDHomeRunDeviceClient deviceService,
        HDHomeRunApiClient apiService,
        HDHomeRunToXmltvConverter converter,
        IEpgDataProvider epgDataProvider,
        IEpgRepository repository)
    {
        _logger = logger;
        _deviceService = deviceService;
        _apiService = apiService;
        _converter = converter;
        _epgDataProvider = epgDataProvider;
        _repository = repository;
    }

    /// <summary>
    /// Generates an XMLTV file from Lineup data.
    /// Fetches directly from API and enriches in-memory (original behavior).
    /// </summary>
    /// <param name="days">Number of days to fetch</param>
    /// <param name="hours">Number of hours per iteration</param>
    /// <param name="filename">Output filename</param>
    public async Task GenerateEpgAsync(int days, int hours, string filename)
    {
        // Fetch channel lineup from device
        var channels = await _deviceService.FetchChannelLineupAsync();

        if (channels.Count == 0)
        {
            _logger.LogError("No channels retrieved. Exiting.");
            throw new InvalidOperationException("No channels retrieved from device");
        }

        // Fetch raw EPG data from remote API
        _logger.LogInformation("Lineup Extraction Started");
        var rawSegments = await _apiService.FetchRawEpgDataAsync(days, hours);
        _logger.LogInformation("Lineup Extraction Completed");

        // Enrich the raw data with device channel info
        var epgData = EnrichEpgData(rawSegments, channels);

        // Convert and write to file
        await WriteXmltvFileAsync(epgData, filename);
    }

    /// <summary>
    /// Fetches raw EPG data from the API and stores it locally without generating XMLTV.
    /// Automatically determines the optimal start time based on cached data.
    /// Use this for scheduled fetches to build up EPG data incrementally.
    /// </summary>
    /// <param name="targetDays">Number of days of future data to accumulate</param>
    /// <param name="force">If true, fetches from API starting from now, ignoring cached data</param>
    /// <param name="progress">Optional progress reporter for fetch status updates</param>
    public async Task FetchAndStoreEpgAsync(int targetDays, bool force = false, IProgress<FetchProgressInfo>? progress = null)
    {
        _logger.LogInformation("Fetching raw EPG data and storing locally{ForceNote}",
            force ? " (forced)" : "");

        // Cleanup old data first
        await _epgDataProvider.CleanupOldDataAsync();

        // Fetch and store raw data (no device channels needed for storage)
        var rawSegments = await _epgDataProvider.FetchAndStoreRawDataAsync(targetDays, force, progress);

        var newPrograms = rawSegments.Sum(s => s.Guide.Count);
        _logger.LogInformation("Added {ChannelCount} channels and {ProgramCount} new programs",
            rawSegments.Count, newPrograms);

        // Log updated cache statistics
        await LogCacheStatisticsAsync();
    }

    /// <summary>
    /// Logs the current cache statistics
    /// </summary>
    private async Task LogCacheStatisticsAsync()
    {
        var stats = await _repository.GetCacheStatisticsAsync();
        var safeFetchStart = await _repository.GetSafeFetchStartTimeAsync();

        _logger.LogInformation(
            "Cache stats: {Channels} channels, {Programs} programs, " +
            "spanning {Start:yyyy-MM-dd HH:mm} to {End:yyyy-MM-dd HH:mm} UTC ({Span:d' days 'h' hours'})",
            stats.ChannelCount,
            stats.ProgramCount,
            stats.EarliestProgramStart,
            stats.LatestProgramEnd,
            stats.TotalTimeSpan);

        if (safeFetchStart.HasValue && stats.LatestProgramEnd.HasValue &&
            safeFetchStart.Value < stats.LatestProgramEnd.Value)
        {
            var gap = stats.LatestProgramEnd.Value - safeFetchStart.Value;
            _logger.LogInformation(
                "Next fetch will start at {SafeStart:yyyy-MM-dd HH:mm} UTC (channel gap: {Gap:h' hours 'm' minutes'})",
                safeFetchStart.Value, gap);
        }
        else if (safeFetchStart.HasValue)
        {
            _logger.LogInformation(
                "Next fetch will start at {SafeStart:yyyy-MM-dd HH:mm} UTC (all channels aligned)",
                safeFetchStart.Value);
        }
    }

    /// <summary>
    /// Generates an XMLTV file from locally cached EPG data without making API calls.
    /// Enriches cached data with current device channel lineup.
    /// </summary>
    /// <param name="days">Number of days of data to include</param>
    /// <param name="filename">Output filename</param>
    public async Task GenerateEpgFromCacheAsync(int days, string filename)
    {
        _logger.LogInformation("Generating XMLTV from cached data for {Days} days", days);

        // Fetch current channel lineup from device for enrichment
        var channels = await _deviceService.FetchChannelLineupAsync();

        if (channels.Count == 0)
        {
            _logger.LogError("No channels retrieved from device. Exiting.");
            throw new InvalidOperationException("No channels retrieved from device");
        }

        // Start 6 hours before now, rounded to nearest 30-minute interval
        var startTime = RoundToNearest30Minutes(DateTime.UtcNow.AddHours(-6));
        var endTime = DateTime.UtcNow.AddDays(days);

        _logger.LogInformation("Exporting programs from {Start:yyyy-MM-dd HH:mm} to {End:yyyy-MM-dd HH:mm} UTC",
            startTime, endTime);

        // Get cached data and enrich with current device channels
        var epgData = await _epgDataProvider.GetEnrichedEpgDataAsync(channels, startTime, endTime);

        if (epgData.Channels.Count == 0)
        {
            _logger.LogWarning("No channels found in cache. Run 'fetch' command first.");
            throw new InvalidOperationException("No cached EPG data available. Run 'fetch' command first.");
        }

        _logger.LogInformation("Found {ChannelCount} channels and {ProgramCount} programs in cache",
            epgData.Channels.Count, epgData.Programs.Count);

        await WriteXmltvFileAsync(epgData, filename);
    }

    /// <summary>
    /// Rounds a DateTime to the nearest 30-minute interval
    /// </summary>
    private static DateTime RoundToNearest30Minutes(DateTime dateTime)
    {
        var totalMinutes = (int)dateTime.TimeOfDay.TotalMinutes;
        var roundedMinutes = (int)(Math.Round(totalMinutes / 30.0) * 30);

        return dateTime.Date.AddMinutes(roundedMinutes);
    }

    /// <summary>
    /// Enriches raw API data with device channel information.
    /// Filters to only include channels that are in the device lineup.
    /// </summary>
    private HDHomeRunEpgData EnrichEpgData(List<HDHomeRunChannelEpgSegment> rawSegments, List<HDHomeRunChannel> deviceChannels)
    {
        var epgData = new HDHomeRunEpgData();

        _logger.LogDebug("Enriching {SegmentCount} raw segments with {DeviceChannelCount} device channels",
            rawSegments.Count, deviceChannels.Count);

        foreach (var segment in rawSegments)
        {
            // Find matching device channel
            var deviceChannel = deviceChannels.FirstOrDefault(dc => dc.GuideNumber == segment.GuideNumber);

            if (deviceChannel == null)
            {
                _logger.LogDebug("Skipping channel {GuideNumber} - not in device lineup", segment.GuideNumber);
                continue;
            }

            // Create enriched channel combining device and API data
            var enrichedChannel = new HDHomeRunEnrichedChannel
            {
                GuideNumber = deviceChannel.GuideNumber,
                GuideName = deviceChannel.GuideName ?? segment.GuideName,
                Affiliate = segment.Affiliate,
                ImageURL = segment.ImageURL ?? ""
            };
            epgData.Channels.Add(enrichedChannel);

            // Add programs for this channel
            foreach (var program in segment.Guide)
            {
                var enrichedProgram = program with { GuideNumber = segment.GuideNumber };
                epgData.Programs.Add(enrichedProgram);
            }
        }

        _logger.LogInformation("Enriched EPG data: {ChannelCount} channels, {ProgramCount} programs",
            epgData.Channels.Count, epgData.Programs.Count);

        return epgData;
    }

    /// <summary>
    /// Converts EPG data to XMLTV format and writes to file
    /// </summary>
    private Task WriteXmltvFileAsync(HDHomeRunEpgData epgData, string filename)
    {
        // Initialize XMLTV document
        var xmltvDocument = new XmltvDocument
        {
            SourceInfoName = "HDHomeRun",
            GeneratorInfoName = "Lineup"
        };

        // Convert HDHomeRun data to XMLTV format
        _logger.LogInformation("HDHomeRun XMLTV Transformation Started");

        foreach (var guideChannel in epgData.Channels)
        {
            var xmltvChannel = _converter.ConvertChannel(guideChannel);
            xmltvDocument.Channels.Add(xmltvChannel);
        }

        foreach (var guideChannel in epgData.Channels)
        {
            var guideNumber = guideChannel.GuideNumber;
            foreach (var guideProgramme in epgData.Programs)
            {
                if (guideProgramme.GuideNumber == guideNumber)
                {
                    var xmltvProgramme = _converter.ConvertProgramme(guideProgramme, guideNumber);
                    if (xmltvProgramme != null)
                    {
                        xmltvDocument.Programmes.Add(xmltvProgramme);
                    }
                }
            }
        }

        _logger.LogInformation("HDHomeRun XMLTV Transformation Completed");

        // Write to XML file
        try
        {
            _logger.LogInformation("Writing XMLTV to file {Filename} Started", filename);
            XmltvSerializer.SerializeToFile(xmltvDocument, filename);
            _logger.LogInformation("Writing XMLTV to file {Filename} Completed", filename);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error writing XML file");
            throw;
        }

        return Task.CompletedTask;
    }
}
