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
    /// <returns>The normalized guide and supplemental root metadata.</returns>
    public XmltvGuideSnapshot Parse(ReadOnlyMemory<byte> content)
    {
        var document = LoadDocument(content);
        var root = document.Root!;
        var channelsById = ParseChannels(root);
        ParseProgrammes(root, channelsById);
        var segments = channelsById.Values
            .SelectMany(channels => channels)
            .Select(channel => channel.Segment)
            .GroupBy(channel => channel.GuideNumber, StringComparer.OrdinalIgnoreCase)
            .Select(MergeLogicalChannel)
            .OrderBy(segment => segment.GuideNumber, ChannelNumberComparer.Instance)
            .ToArray();
        var supplementalXml = XmltvSupplementalMetadata.Create(
            root.Attributes().Where(attribute =>
                !IsUnqualifiedNamed(attribute, "generator-info-name") &&
                !IsUnqualifiedNamed(attribute, "generator-info-url")),
            root.Elements().Where(element =>
                !IsUnqualifiedNamed(element, "channel") &&
                !IsUnqualifiedNamed(element, "programme")));
        return new XmltvGuideSnapshot
        {
            Segments = segments,
            SupplementalXml = supplementalXml
        };
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
        foreach (var element in root.Elements().Where(element => IsUnqualifiedNamed(element, "channel")))
        {
            var id = element.Attribute("id")?.Value;
            var guideNumber = ElementValue(element, "lcn");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(guideNumber))
            {
                continue;
            }

            var displayNames = Elements(element, "display-name")
                .Where(displayName => displayName.Value.Trim().Length > 0)
                .ToArray();
            var selectedDisplayName = displayNames.FirstOrDefault(displayName => !displayName.Value.Trim().StartsWith($"{guideNumber} ", StringComparison.OrdinalIgnoreCase))
                ?? displayNames.FirstOrDefault();
            var guideName = selectedDisplayName?.Value.Trim()
                ?? guideNumber;
            var icons = Elements(element, "icon").ToArray();
            var primaryIcon = icons.FirstOrDefault();
            var preservedElements = element.Elements()
                .Where(child => child != selectedDisplayName &&
                                child != primaryIcon &&
                                !IsUnqualifiedNamed(child, "lcn"))
                .Select(child => new XElement(child))
                .ToArray();
            var ownedElements = new List<(string Key, XElement Element)>();
            AddOwnedShell(ownedElements, "display-name", selectedDisplayName);
            AddOwnedShell(ownedElements, "icon", primaryIcon, "src");

            if (!channels.TryGetValue(id, out var stationChannels))
            {
                stationChannels = [];
                channels.Add(id, stationChannels);
            }

            stationChannels.Add(new ParsedChannel(new HDHomeRunChannelEpgSegment
            {
                GuideNumber = guideNumber,
                GuideName = guideName,
                ImageURL = primaryIcon?.Attribute("src")?.Value,
                SupplementalXml = XmltvSupplementalMetadata.Create(
                    element.Attributes().Where(attribute => !IsUnqualifiedNamed(attribute, "id")),
                    preservedElements,
                    ownedElements),
                Guide = []
            }));
        }

        return channels;
    }

    private static void ParseProgrammes(XElement root, IReadOnlyDictionary<string, List<ParsedChannel>> channelsById)
    {
        foreach (var element in root.Elements().Where(element => IsUnqualifiedNamed(element, "programme")))
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

            var title = Elements(element, "title").FirstOrDefault();
            var subtitle = Elements(element, "sub-title").FirstOrDefault();
            var description = Elements(element, "desc").FirstOrDefault();
            var icon = Elements(element, "icon").FirstOrDefault();
            var preservedElements = element.Elements()
                .Where(child => child != title &&
                                child != subtitle &&
                                child != description &&
                                child != icon)
                .Select(child => new XElement(child))
                .ToArray();
            var ownedElements = new List<(string Key, XElement Element)>();
            AddOwnedShell(ownedElements, "title", title);
            AddOwnedShell(ownedElements, "sub-title", subtitle);
            AddOwnedShell(ownedElements, "desc", description);
            AddOwnedShell(ownedElements, "icon", icon, "src");
            var programme = new HDHomeRunProgram
            {
                Title = title?.Value.Trim(),
                EpisodeTitle = subtitle?.Value.Trim(),
                Synopsis = description?.Value.Trim(),
                StartTime = start.ToUnixTimeSeconds(),
                EndTime = stop.ToUnixTimeSeconds(),
                ImageURL = icon?.Attribute("src")?.Value,
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
                    .ToList(),
                SupplementalXml = XmltvSupplementalMetadata.Create(
                    element.Attributes().Where(attribute =>
                        !IsUnqualifiedNamed(attribute, "start") &&
                        !IsUnqualifiedNamed(attribute, "stop") &&
                        !IsUnqualifiedNamed(attribute, "channel")),
                    preservedElements,
                    ownedElements)
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
        return parent.Elements().Where(element => IsUnqualifiedNamed(element, localName));
    }

    private static void AddOwnedShell(List<(string Key, XElement Element)> ownedElements, string key, XElement? element, params string[] excludedAttributes)
    {
        if (element == null)
        {
            return;
        }

        var excluded = excludedAttributes.ToHashSet(StringComparer.Ordinal);
        var shell = new XElement(
            element.Name,
            element.Attributes().Where(attribute => attribute.Name.Namespace != XNamespace.None || !excluded.Contains(attribute.Name.LocalName)));
        if (shell.HasAttributes)
        {
            ownedElements.Add((key, shell));
        }
    }

    private static bool IsUnqualifiedNamed(XAttribute attribute, string localName) =>
        attribute.Name.Namespace == XNamespace.None &&
        string.Equals(attribute.Name.LocalName, localName, StringComparison.Ordinal);

    private static bool IsUnqualifiedNamed(XElement element, string localName) =>
        element.Name.Namespace == XNamespace.None &&
        string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal);

    private sealed record ParsedChannel(HDHomeRunChannelEpgSegment Segment);
}
