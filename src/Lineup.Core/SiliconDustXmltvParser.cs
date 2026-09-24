using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Lineup.HDHomeRun.Api.Models;

namespace Lineup.Core;

/// <summary>
/// Parses SiliconDust XMLTV input into Lineup's provider-neutral guide models.
/// </summary>
public sealed class SiliconDustXmltvParser
{
    private static readonly string[] TimestampFormats =
    [
        "yyyyMMddHHmmss zzz",
        "yyyyMMddHHmm zzz",
        "yyyyMMddHHmmss",
        "yyyyMMddHHmm"
    ];

    /// <summary>
    /// Parses channel and programme data from a SiliconDust XMLTV document.
    /// </summary>
    /// <param name="content">Complete XMLTV document bytes.</param>
    /// <returns>Normalized guide segments keyed by logical channel number.</returns>
    public IReadOnlyList<HDHomeRunChannelEpgSegment> Parse(ReadOnlyMemory<byte> content)
    {
        var document = LoadDocument(content);
        var root = document.Root!;
        var channelsById = ParseChannels(root);
        ParseProgrammes(root, channelsById);
        return channelsById.Values
            .SelectMany(channels => channels)
            .Select(channel => channel.Segment)
            .GroupBy(channel => channel.GuideNumber, StringComparer.OrdinalIgnoreCase)
            .Select(MergeLogicalChannel)
            .OrderBy(segment => segment.GuideNumber, ChannelNumberComparer.Instance)
            .ToArray();
    }

    private static XDocument LoadDocument(ReadOnlyMemory<byte> content)
    {
        using var stream = new MemoryStream(content.ToArray(), writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        var document = XDocument.Load(reader, LoadOptions.None);
        if (document.Root?.Name.LocalName != "tv")
        {
            throw new InvalidDataException("The downloaded guide is not an XMLTV document.");
        }

        return document;
    }

    private static HDHomeRunChannelEpgSegment MergeLogicalChannel(IGrouping<string?, HDHomeRunChannelEpgSegment> channels)
    {
        var primary = channels.First();
        var programmes = channels
            .SelectMany(channel => channel.Guide)
            .GroupBy(programme => new { programme.StartTime, programme.EndTime, programme.Title })
            .Select(group => group.First())
            .OrderBy(programme => programme.StartTime)
            .ToList();
        return primary with { Guide = programmes };
    }

    private static Dictionary<string, List<ParsedChannel>> ParseChannels(XElement root)
    {
        var channels = new Dictionary<string, List<ParsedChannel>>(StringComparer.Ordinal);
        foreach (var element in root.Elements().Where(element => element.Name.LocalName == "channel"))
        {
            var id = element.Attribute("id")?.Value;
            var guideNumber = ElementValue(element, "lcn");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(guideNumber))
            {
                continue;
            }

            var names = Elements(element, "display-name")
                .Select(displayName => displayName.Value.Trim())
                .Where(displayName => displayName.Length > 0)
                .ToArray();
            var guideName = names.FirstOrDefault(name => !name.StartsWith($"{guideNumber} ", StringComparison.OrdinalIgnoreCase))
                ?? names.FirstOrDefault()
                ?? guideNumber;

            if (!channels.TryGetValue(id, out var stationChannels))
            {
                stationChannels = [];
                channels.Add(id, stationChannels);
            }

            stationChannels.Add(new ParsedChannel(new HDHomeRunChannelEpgSegment
            {
                GuideNumber = guideNumber,
                GuideName = guideName,
                ImageURL = Elements(element, "icon").FirstOrDefault()?.Attribute("src")?.Value,
                Guide = []
            }));
        }

        return channels;
    }

    private static void ParseProgrammes(XElement root, IReadOnlyDictionary<string, List<ParsedChannel>> channelsById)
    {
        foreach (var element in root.Elements().Where(element => element.Name.LocalName == "programme"))
        {
            var channelId = element.Attribute("channel")?.Value;
            if (string.IsNullOrWhiteSpace(channelId) || !channelsById.TryGetValue(channelId, out var channels))
            {
                continue;
            }

            if (!TryParseTimestamp(element.Attribute("start")?.Value, out var start) ||
                !TryParseTimestamp(element.Attribute("stop")?.Value, out var stop) ||
                stop <= start)
            {
                continue;
            }

            var programme = new HDHomeRunProgram
            {
                Title = ElementValue(element, "title"),
                EpisodeTitle = ElementValue(element, "sub-title"),
                Synopsis = ElementValue(element, "desc"),
                StartTime = start.ToUnixTimeSeconds(),
                EndTime = stop.ToUnixTimeSeconds(),
                ImageURL = Elements(element, "icon").FirstOrDefault()?.Attribute("src")?.Value,
                EpisodeNumber = Elements(element, "episode-num")
                    .FirstOrDefault(episode => string.Equals(episode.Attribute("system")?.Value, "onscreen", StringComparison.OrdinalIgnoreCase))
                    ?.Value,
                OriginalAirdate = ParseOriginalAirdate(ElementValue(element, "date")),
                First = Elements(element, "new").Any() ? 1 : Elements(element, "previously-shown").Any() ? 0 : null,
                SeriesID = ElementValue(element, "series-id"),
                Filter = Elements(element, "category")
                    .Select(category => category.Value.Trim())
                    .Where(category => category.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };

            foreach (var channel in channels)
            {
                channel.Segment.Guide.Add(programme with { GuideNumber = channel.Segment.GuideNumber });
            }
        }

        foreach (var channel in channelsById.Values.SelectMany(channels => channels))
        {
            channel.Segment.Guide.Sort((left, right) => left.StartTime.CompareTo(right.StartTime));
        }
    }

    private static bool TryParseTimestamp(string? value, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.Length >= 5 && normalized[^5] is '+' or '-' && normalized[^3] != ':')
        {
            normalized = normalized.Insert(normalized.Length - 2, ":");
        }

        return DateTimeOffset.TryParseExact(normalized, TimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result);
    }

    private static long? ParseOriginalAirdate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 8 ||
            !DateTime.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            return null;
        }

        return new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)).ToUnixTimeSeconds();
    }

    private static string? ElementValue(XElement parent, string localName)
    {
        return Elements(parent, localName).FirstOrDefault()?.Value.Trim();
    }

    private static IEnumerable<XElement> Elements(XElement parent, string localName)
    {
        return parent.Elements().Where(element => element.Name.LocalName == localName);
    }

    private sealed record ParsedChannel(HDHomeRunChannelEpgSegment Segment);
}
