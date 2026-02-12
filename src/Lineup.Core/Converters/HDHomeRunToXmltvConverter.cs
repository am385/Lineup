using Lineup.HDHomeRun.Api.Models;
using Lineup.Core.Models;
using Lineup.Core.Models.Xmltv;
using Microsoft.Extensions.Logging;

namespace Lineup.Core.Converters;

/// <summary>
/// Converts Lineup data to XMLTV format
/// </summary>
public class HDHomeRunToXmltvConverter
{
    private readonly ILogger<HDHomeRunToXmltvConverter> _logger;
    private readonly TimeZoneInfo _localTimeZone;

    public HDHomeRunToXmltvConverter(ILogger<HDHomeRunToXmltvConverter> logger)
    {
        _logger = logger;

        // Initialize local timezone with fallback to UTC
        try
        {
            _localTimeZone = TimeZoneInfo.Local;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not detect local timezone. Falling back to UTC.");
            _localTimeZone = TimeZoneInfo.Utc;
        }
    }

    /// <summary>
    /// Converts HDHomeRun enriched channel to XMLTV channel
    /// </summary>
    /// <param name="channelData">HDHomeRun enriched channel data (combining device and API info)</param>
    /// <returns>XMLTV channel</returns>
    public XmltvChannel ConvertChannel(HDHomeRunEnrichedChannel channelData)
    {
        var channel = new XmltvChannel
        {
            Id = channelData.GuideNumber,
            DisplayName = channelData.GuideName ?? "Unknown",
            Icon = new XmltvIcon
            {
                Source = channelData.ImageURL ?? ""
            }
        };

        _logger.LogDebug("Converted enriched channel: {GuideName}", channelData.GuideName ?? "Unknown");
        return channel;
    }

    /// <summary>
    /// Converts HDHomeRun program to XMLTV programme
    /// </summary>
    /// <param name="programData">HDHomeRun program data</param>
    /// <param name="channelNumber">Channel number/ID</param>
    /// <returns>XMLTV programme or null if conversion fails</returns>
    public XmltvProgramme? ConvertProgramme(HDHomeRunProgram programData, string channelNumber)
    {
        try
        {
            var startTimeUtc = DateTimeOffset.FromUnixTimeSeconds(programData.StartTime).UtcDateTime;
            var startTime = TimeZoneInfo.ConvertTimeFromUtc(startTimeUtc, _localTimeZone);

            var duration = programData.EndTime - programData.StartTime;
            var endTime = startTime.AddSeconds(duration);

            var programme = new XmltvProgramme
            {
                Start = FormatXmltvDateTime(startTime),
                Stop = FormatXmltvDateTime(endTime),
                Channel = channelNumber,
                Title = new XmltvText
                {
                    Language = "en",
                    Value = programData.Title ?? ""
                }
            };

            // Add sub-title if present
            if (!string.IsNullOrEmpty(programData.EpisodeTitle))
            {
                programme.SubTitle = new XmltvText
                {
                    Language = "en",
                    Value = programData.EpisodeTitle
                };
            }

            // Add description if present
            if (!string.IsNullOrEmpty(programData.Synopsis))
            {
                programme.Description = new XmltvText
                {
                    Language = "en",
                    Value = programData.Synopsis
                };
            }

            // Add categories
            if (programData.Filter != null)
            {
                foreach (var filter in programData.Filter)
                {
                    programme.Categories.Add(new XmltvText
                    {
                        Language = "en",
                        Value = filter
                    });
                }
            }

            // Add icon if present
            if (!string.IsNullOrEmpty(programData.ImageURL))
            {
                programme.Icon = new XmltvIcon
                {
                    Source = programData.ImageURL
                };
            }

            // Add episode numbers
            AddEpisodeNumbers(programme, programData);

            // Add previously-shown information
            AddPreviouslyShownInfo(programme, programData, startTime);

            // Mark as new if applicable
            if (programData.First.HasValue && programData.First.Value == 1)
            {
                programme.New = new XmltvNew();
            }

            _logger.LogDebug("Converted programme: {Title}", programData.Title);
            return programme;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error converting programme {Title}", programData.Title ?? "unknown");
            return null;
        }
    }

    /// <summary>
    /// Adds episode number information to the programme
    /// </summary>
    private void AddEpisodeNumbers(XmltvProgramme programme, HDHomeRunProgram programmeData)
    {
        if (string.IsNullOrEmpty(programmeData.EpisodeNumber))
            return;

        try
        {
            var episodeNumber = programmeData.EpisodeNumber;
            if (episodeNumber.Contains('S') && episodeNumber.Contains('E'))
            {
                var sIndex = episodeNumber.IndexOf('S');
                var eIndex = episodeNumber.IndexOf('E');
                var series = int.Parse(episodeNumber.Substring(sIndex + 1, eIndex - sIndex - 1)) - 1;
                var episode = int.Parse(episodeNumber.Substring(eIndex + 1)) - 1;

                programme.EpisodeNumbers.Add(new XmltvEpisodeNum
                {
                    System = "onscreen",
                    Value = episodeNumber
                });
                programme.EpisodeNumbers.Add(new XmltvEpisodeNum
                {
                    System = AppConstants.XmltvNsSystem,
                    Value = $"{series}.{episode}.0/0"
                });
            }
        }
        catch (Exception)
        {
            _logger.LogWarning("Invalid Series/Episode data for {Title}", programmeData.Title);
        }
    }

    /// <summary>
    /// Formats a DateTime as an XMLTV timestamp with the correct timezone offset from _localTimeZone.
    /// </summary>
    private string FormatXmltvDateTime(DateTime localTime)
    {
        var offset = _localTimeZone.GetUtcOffset(localTime);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var absOffset = offset.Duration();
        return $"{localTime:yyyyMMddHHmmss} {sign}{absOffset.Hours:D2}{absOffset.Minutes:D2}";
    }

    /// <summary>
    /// Adds previously-shown information to the programme
    /// </summary>
    private void AddPreviouslyShownInfo(XmltvProgramme programme, HDHomeRunProgram programmeData, DateTime startTime)
    {
        if (!programmeData.OriginalAirdate.HasValue)
            return;

        var airDate = DateTimeOffset.FromUnixTimeSeconds(programmeData.OriginalAirdate.Value).UtcDateTime;
        airDate = TimeZoneInfo.ConvertTimeFromUtc(airDate, _localTimeZone);
        var startDate = new DateTime(startTime.Year, startTime.Month, startTime.Day, 0, 0, 0, startTime.Kind);

        if (airDate.Date != startDate.Date)
        {
            programme.PreviouslyShown = new XmltvPreviouslyShown
            {
                Start = airDate.ToString("yyyyMMddHHmmss")
            };
        }
        else if (programmeData.First.HasValue && programmeData.First.Value != 1)
        {
            programme.PreviouslyShown = new XmltvPreviouslyShown();
        }
    }
}

