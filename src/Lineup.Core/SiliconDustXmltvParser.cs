using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Lineup.HDHomeRun.Api.Models;

namespace Lineup.Core;

/// <summary>
/// Parses a SiliconDust XMLTV document into Lineup's normalized guide models.
/// </summary>
public class SiliconDustXmltvParser
{
    private static readonly string[] TimestampFormats =
    [
        "yyyyMMddHHmmss zzz",
        "yyyyMMddHHmm zzz",
        "yyyyMMddHHmmss",
        "yyyyMMddHHmm"
    ];

    /// <summary>
    /// Parses channel and programme data from a canonical XMLTV document.
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
            .OrderBy(segment => ParseChannelNumber(segment.GuideNumber))
            .ThenBy(segment => segment.GuideNumber, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Removes channels and programmes that are not present in the tuner's current lineup.
    /// </summary>
    /// <param name="content">Complete XMLTV document bytes.</param>
    /// <param name="guideNumbers">Logical channel numbers returned by the tuner.</param>
    /// <returns>A valid XMLTV document containing only available tuner channels.</returns>
    public byte[] FilterByGuideNumbers(ReadOnlyMemory<byte> content, IEnumerable<string> guideNumbers)
    {
        ArgumentNullException.ThrowIfNull(guideNumbers);

        var allowedGuideNumbers = guideNumbers
            .Where(guideNumber => !string.IsNullOrWhiteSpace(guideNumber))
            .Select(guideNumber => guideNumber.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var document = LoadDocument(content);
        var root = document.Root!;
        var channelElements = root.Elements()
            .Where(element => element.Name.LocalName == "channel")
            .ToArray();
        var retainedChannelIds = channelElements
            .Where(element => allowedGuideNumbers.Contains(ElementValue(element, "lcn") ?? string.Empty))
            .Select(element => element.Attribute("id")?.Value)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        foreach (var channelElement in channelElements)
        {
            var guideNumber = ElementValue(channelElement, "lcn");
            if (guideNumber == null || !allowedGuideNumbers.Contains(guideNumber))
            {
                channelElement.Remove();
            }
        }

        var programmeElements = root.Elements().Where(element => element.Name.LocalName == "programme").ToArray();
        foreach (var programmeElement in programmeElements)
        {
            var channelId = programmeElement.Attribute("channel")?.Value;
            if (channelId == null || !retainedChannelIds.Contains(channelId))
            {
                programmeElement.Remove();
            }
        }

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false
        }))
        {
            document.Save(writer);
        }

        return stream.ToArray();
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
                !TryParseTimestamp(element.Attribute("stop")?.Value, out var stop))
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

    private static double ParseChannelNumber(string? value)
    {
        return double.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            ? number
            : double.MaxValue;
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
