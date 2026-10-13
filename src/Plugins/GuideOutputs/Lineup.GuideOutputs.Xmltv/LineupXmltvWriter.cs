using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Lineup.Common.Guides;

namespace Lineup.GuideOutputs.Xmltv;

/// <summary>
/// Writes Lineup's provider-neutral normalized guide data as XMLTV.
/// </summary>
public sealed class LineupXmltvWriter
{
    private const string PlaceholderTitle = "Not Available";
    private static readonly string[] ProgramElementOrder =
    [
        "title", "sub-title", "desc", "credits", "date", "category", "keyword", "language", "orig-language", "length", "icon", "url", "country",
        "episode-num", "video", "audio", "previously-shown", "premiere", "last-chance", "new", "subtitles", "rating", "star-rating", "review", "image"
    ];

    /// <summary>
    /// Creates a deterministic XMLTV document from a normalized guide snapshot.
    /// </summary>
    public byte[] Write(
        XmltvGuideSnapshot snapshot,
        IEnumerable<TunerChannel> channels,
        DateTimeOffset placeholderStart,
        DateTimeOffset placeholderStop)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return WriteCore(snapshot, channels, placeholderStart, placeholderStop);
    }

    /// <summary>
    /// Creates a deterministic XMLTV document from normalized database guide data.
    /// </summary>
    /// <param name="segments">Normalized guide channels and programs.</param>
    /// <param name="channels">Physical channels to publish.</param>
    /// <param name="placeholderStart">Inclusive placeholder start for channels without programs.</param>
    /// <param name="placeholderStop">Exclusive placeholder end for channels without programs.</param>
    /// <returns>A complete XMLTV document.</returns>
    public byte[] Write(
        IEnumerable<GuideChannelSegment> segments,
        IEnumerable<TunerChannel> channels,
        DateTimeOffset placeholderStart,
        DateTimeOffset placeholderStop)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return WriteCore(new XmltvGuideSnapshot { Segments = segments.ToArray() }, channels, placeholderStart, placeholderStop);
    }

    private static byte[] WriteCore(
        XmltvGuideSnapshot snapshot,
        IEnumerable<TunerChannel> channels,
        DateTimeOffset placeholderStart,
        DateTimeOffset placeholderStop)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (placeholderStop <= placeholderStart)
        {
            throw new ArgumentOutOfRangeException(nameof(placeholderStop), "The placeholder guide end must be later than its start.");
        }

        var segmentsByNumber = snapshot.Segments
            .Where(segment => !string.IsNullOrWhiteSpace(segment.GuideNumber))
            .GroupBy(segment => segment.GuideNumber!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var publishedChannels = channels
            .Where(channel => !string.IsNullOrWhiteSpace(channel.GuideNumber))
            .DistinctBy(channel => channel.GuideNumber.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(channel => channel.GuideNumber, ChannelNumberComparer.Instance)
            .ToArray();
        var rootSupplemental = XmltvSupplementalMetadata.Parse(snapshot.SupplementalXml);
        var root = new XElement("tv");
        ApplyAttributes(root, rootSupplemental?.Attributes, "generator-info-name", "generator-info-url");
        root.SetAttributeValue("generator-info-name", "Lineup");
        if (rootSupplemental != null)
        {
            root.Add(rootSupplemental.Elements);
        }
        var channelIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in publishedChannels)
        {
            var guideNumber = channel.GuideNumber.Trim();
            var channelId = $"lineup.channel.{Uri.EscapeDataString(guideNumber)}";
            channelIds.Add(guideNumber, channelId);
            segmentsByNumber.TryGetValue(guideNumber, out var segment);
            root.Add(CreateChannelElement(channelId, channel, segment));
        }

        foreach (var channel in publishedChannels)
        {
            var guideNumber = channel.GuideNumber.Trim();
            var programs = segmentsByNumber.TryGetValue(guideNumber, out var segment)
                ? segment.Guide.OrderBy(program => program.StartTime).ToArray()
                : [];
            if (programs.Length == 0)
            {
                root.Add(CreatePlaceholderProgram(channelIds[guideNumber], placeholderStart, placeholderStop));
                continue;
            }

            foreach (var program in programs)
            {
                root.Add(CreateProgramElement(channelIds[guideNumber], program));
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

    private static XElement CreateChannelElement(string channelId, TunerChannel channel, GuideChannelSegment? segment)
    {
        var guideNumber = channel.GuideNumber.Trim();
        var guideName = string.IsNullOrWhiteSpace(channel.GuideName) ? guideNumber : channel.GuideName.Trim();
        var supplemental = XmltvSupplementalMetadata.Parse(segment?.SupplementalXml);
        var element = new XElement("channel", new XAttribute("id", channelId));
        ApplyAttributes(element, supplemental?.Attributes, "id");

        var displayName = CreateTypedElement("display-name", guideName, supplemental?.GetOwnedElement("display-name"));
        element.Add(displayName);
        element.Add(supplemental?.Elements.Where(child => IsUnqualifiedNamed(child, "display-name")));
        element.Add(new XElement("lcn", guideNumber));

        if (!string.IsNullOrWhiteSpace(segment?.ImageURL))
        {
            var icon = CreateTypedElement("icon", value: null, supplemental?.GetOwnedElement("icon"));
            icon.SetAttributeValue("src", segment.ImageURL);
            element.Add(icon);
        }
        element.Add(supplemental?.Elements.Where(child => IsUnqualifiedNamed(child, "icon")));
        element.Add(supplemental?.Elements.Where(child =>
            !IsUnqualifiedNamed(child, "display-name") &&
            !IsUnqualifiedNamed(child, "icon")));
        return element;
    }

    private static XElement CreatePlaceholderProgram(string channelId, DateTimeOffset start, DateTimeOffset stop)
    {
        return new XElement(
            "programme",
            new XAttribute("start", FormatTimestamp(start)),
            new XAttribute("stop", FormatTimestamp(stop)),
            new XAttribute("channel", channelId),
            new XElement("title", new XAttribute("lang", "en"), PlaceholderTitle));
    }

    private static XElement CreateProgramElement(string channelId, GuideCacheProgram program)
    {
        var supplemental = XmltvSupplementalMetadata.Parse(program.SupplementalXml);
        var element = new XElement(
            "programme",
            new XAttribute("start", FormatTimestamp(DateTimeOffset.FromUnixTimeSeconds(program.StartTime))),
            new XAttribute("stop", FormatTimestamp(DateTimeOffset.FromUnixTimeSeconds(program.EndTime))),
            new XAttribute("channel", channelId));
        ApplyAttributes(element, supplemental?.Attributes, "start", "stop", "channel");
        var supplementalElements = supplemental?.Elements ?? [];
        foreach (var name in ProgramElementOrder)
        {
            AddProgramElements(element, name, program, supplemental, supplementalElements);
        }

        var extensionElements = supplementalElements.Where(child => !IsOfficialProgramElement(child)).ToArray();
        if (!extensionElements.Any(child => IsUnqualifiedNamed(child, "series-id")) && !string.IsNullOrWhiteSpace(program.SeriesID))
        {
            element.Add(new XElement("series-id", program.SeriesID));
        }
        element.Add(extensionElements);
        return element;
    }

    private static void AddProgramElements(
        XElement parent,
        string name,
        GuideCacheProgram program,
        XmltvSupplementalMetadata? supplemental,
        IReadOnlyList<XElement> supplementalElements)
    {
        var preserved = supplementalElements.Where(element => IsUnqualifiedNamed(element, name)).ToArray();
        switch (name)
        {
            case "title":
                parent.Add(CreateTypedElement("title", program.Title ?? PlaceholderTitle, supplemental?.GetOwnedElement("title")));
                parent.Add(preserved);
                break;
            case "sub-title":
                AddTypedElement(parent, "sub-title", program.EpisodeTitle, supplemental?.GetOwnedElement("sub-title"));
                parent.Add(preserved);
                break;
            case "desc":
                AddTypedElement(parent, "desc", program.Synopsis, supplemental?.GetOwnedElement("desc"));
                parent.Add(preserved);
                break;
            case "date" when preserved.Length == 0 && program.OriginalAirdate.HasValue:
                parent.Add(new XElement("date", DateTimeOffset.FromUnixTimeSeconds(program.OriginalAirdate.Value).UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture)));
                break;
            case "category" when preserved.Length == 0:
                parent.Add((program.Filter ?? []).Select(category => new XElement("category", category)));
                break;
            case "icon":
                if (!string.IsNullOrWhiteSpace(program.ImageURL))
                {
                    var icon = CreateTypedElement("icon", value: null, supplemental?.GetOwnedElement("icon"));
                    icon.SetAttributeValue("src", program.ImageURL);
                    parent.Add(icon);
                }
                parent.Add(preserved);
                break;
            case "episode-num" when preserved.Length == 0 && !string.IsNullOrWhiteSpace(program.EpisodeNumber):
                parent.Add(new XElement("episode-num", new XAttribute("system", "onscreen"), program.EpisodeNumber));
                break;
            case "new" when preserved.Length == 0 && program.First == 1:
                parent.Add(new XElement("new"));
                break;
            case "previously-shown" when preserved.Length == 0 && program.First == 0:
                parent.Add(new XElement("previously-shown"));
                break;
            default:
                parent.Add(preserved);
                break;
        }
    }

    private static void AddTypedElement(XElement parent, string name, string? value, XElement? shell)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parent.Add(CreateTypedElement(name, value, shell));
        }
    }

    private static XElement CreateTypedElement(string name, string? value, XElement? shell)
    {
        var element = new XElement(name);
        if (shell != null)
        {
            ApplyAttributes(element, shell.Attributes());
        }
        if (value != null)
        {
            element.Value = value;
        }
        return element;
    }

    private static void ApplyAttributes(XElement element, IEnumerable<XAttribute>? attributes, params string[] controlledNames)
    {
        if (attributes == null)
        {
            return;
        }

        var controlled = controlledNames.ToHashSet(StringComparer.Ordinal);
        foreach (var attribute in attributes.Where(attribute => attribute.Name.Namespace != XNamespace.None || !controlled.Contains(attribute.Name.LocalName)))
        {
            element.SetAttributeValue(attribute.Name, attribute.Value);
        }
    }

    private static bool IsOfficialProgramElement(XElement element) =>
        element.Name.Namespace == XNamespace.None &&
        ProgramElementOrder.Contains(element.Name.LocalName, StringComparer.Ordinal);

    private static bool IsUnqualifiedNamed(XElement element, string localName) =>
        element.Name.Namespace == XNamespace.None &&
        string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal);

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        $"{timestamp.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)} +0000";
}
