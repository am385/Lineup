using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Lineup.HDHomeRun.Api.Models;
using Lineup.HDHomeRun.Device.Models;

namespace Lineup.Core;

/// <summary>
/// Writes Lineup's provider-neutral normalized guide data as XMLTV.
/// </summary>
public sealed class LineupXmltvWriter
{
    private const string PlaceholderTitle = "Not Available";

    /// <summary>
    /// Creates a deterministic XMLTV document from normalized database guide data.
    /// </summary>
    /// <param name="segments">Normalized guide channels and programmes.</param>
    /// <param name="channels">Physical channels to publish.</param>
    /// <param name="placeholderStart">Inclusive placeholder start for channels without programmes.</param>
    /// <param name="placeholderStop">Exclusive placeholder end for channels without programmes.</param>
    /// <returns>A complete XMLTV document.</returns>
    public byte[] Write(
        IEnumerable<HDHomeRunChannelEpgSegment> segments,
        IEnumerable<HDHomeRunChannel> channels,
        DateTimeOffset placeholderStart,
        DateTimeOffset placeholderStop)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(channels);
        if (placeholderStop <= placeholderStart)
        {
            throw new ArgumentOutOfRangeException(nameof(placeholderStop), "The placeholder guide end must be later than its start.");
        }

        var segmentsByNumber = segments
            .Where(segment => !string.IsNullOrWhiteSpace(segment.GuideNumber))
            .GroupBy(segment => segment.GuideNumber!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var publishedChannels = channels
            .Where(channel => !string.IsNullOrWhiteSpace(channel.GuideNumber))
            .DistinctBy(channel => channel.GuideNumber.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(channel => channel.GuideNumber, ChannelNumberComparer.Instance)
            .ToArray();
        var root = new XElement("tv", new XAttribute("source-info-name", "Lineup"));
        var channelIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in publishedChannels)
        {
            var guideNumber = channel.GuideNumber.Trim();
            var channelId = $"lineup.channel.{Uri.EscapeDataString(guideNumber)}";
            channelIds.Add(guideNumber, channelId);
            segmentsByNumber.TryGetValue(guideNumber, out var segment);
            var element = CreateChannelElement(channelId, channel);
            if (!string.IsNullOrWhiteSpace(segment?.ImageURL))
            {
                element.Add(new XElement("icon", new XAttribute("src", segment.ImageURL)));
            }

            root.Add(element);
        }

        foreach (var channel in publishedChannels)
        {
            var guideNumber = channel.GuideNumber.Trim();
            var programmes = segmentsByNumber.TryGetValue(guideNumber, out var segment)
                ? segment.Guide.OrderBy(programme => programme.StartTime).ToArray()
                : [];
            if (programmes.Length == 0)
            {
                root.Add(CreatePlaceholderProgramme(channelIds[guideNumber], placeholderStart, placeholderStop));
                continue;
            }

            foreach (var programme in programmes)
            {
                root.Add(CreateProgrammeElement(channelIds[guideNumber], programme));
            }
        }

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false
        }))
        {
            new XDocument(root).Save(writer);
        }

        return stream.ToArray();
    }

    private static XElement CreateChannelElement(string channelId, HDHomeRunChannel channel)
    {
        var guideNumber = channel.GuideNumber.Trim();
        var guideName = string.IsNullOrWhiteSpace(channel.GuideName) ? guideNumber : channel.GuideName.Trim();
        return new XElement(
            "channel",
            new XAttribute("id", channelId),
            new XElement("display-name", guideName),
            new XElement("lcn", guideNumber));
    }

    private static XElement CreatePlaceholderProgramme(string channelId, DateTimeOffset start, DateTimeOffset stop)
    {
        return new XElement(
            "programme",
            new XAttribute("start", FormatTimestamp(start)),
            new XAttribute("stop", FormatTimestamp(stop)),
            new XAttribute("channel", channelId),
            new XElement("title", new XAttribute("lang", "en"), PlaceholderTitle));
    }

    private static XElement CreateProgrammeElement(string channelId, HDHomeRunProgram programme)
    {
        var element = new XElement(
            "programme",
            new XAttribute("start", FormatTimestamp(DateTimeOffset.FromUnixTimeSeconds(programme.StartTime))),
            new XAttribute("stop", FormatTimestamp(DateTimeOffset.FromUnixTimeSeconds(programme.EndTime))),
            new XAttribute("channel", channelId),
            new XElement("title", new XAttribute("lang", "en"), programme.Title ?? PlaceholderTitle));
        AddOptionalElement(element, "sub-title", programme.EpisodeTitle, includeLanguage: true);
        AddOptionalElement(element, "desc", programme.Synopsis, includeLanguage: true);
        if (programme.OriginalAirdate.HasValue)
        {
            element.Add(new XElement("date", DateTimeOffset.FromUnixTimeSeconds(programme.OriginalAirdate.Value).UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture)));
        }

        if (!string.IsNullOrWhiteSpace(programme.ImageURL))
        {
            element.Add(new XElement("icon", new XAttribute("src", programme.ImageURL)));
        }

        if (!string.IsNullOrWhiteSpace(programme.EpisodeNumber))
        {
            element.Add(new XElement("episode-num", new XAttribute("system", "onscreen"), programme.EpisodeNumber));
        }

        AddOptionalElement(element, "series-id", programme.SeriesID, includeLanguage: false);
        foreach (var category in programme.Filter ?? [])
        {
            element.Add(new XElement("category", new XAttribute("lang", "en"), category));
        }

        if (programme.First == 1)
        {
            element.Add(new XElement("new"));
        }
        else if (programme.First == 0)
        {
            element.Add(new XElement("previously-shown"));
        }

        return element;
    }

    private static void AddOptionalElement(XElement parent, string name, string? value, bool includeLanguage)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var element = new XElement(name, value);
        if (includeLanguage)
        {
            element.Add(new XAttribute("lang", "en"));
        }

        parent.Add(element);
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        $"{timestamp.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)} +0000";
}
