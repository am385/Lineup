using Lineup.Common.Guides;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace Lineup.GuideOutputs.Json;

/// <summary>Versioned JSON representation of one normalized virtual guide snapshot.</summary>
public sealed record JsonGuideDocument
{
    /// <summary>Current serialized document schema version.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>Gets the serialized document schema version.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Gets the virtual device whose guide was projected.</summary>
    public required Guid VirtualDeviceId { get; init; }

    /// <summary>Gets compact supplemental XMLTV root metadata from contributing guide sources.</summary>
    public JsonObject Supplemental { get; init; } = [];

    /// <summary>Gets the projected guide channels.</summary>
    public IReadOnlyList<JsonGuideChannel> Channels { get; init; } = [];

    /// <summary>Creates a JSON document from a normalized virtual guide snapshot.</summary>
    public static JsonGuideDocument FromSnapshot(VirtualGuideSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new JsonGuideDocument
        {
            VirtualDeviceId = snapshot.VirtualDeviceId.Value,
            Supplemental = JsonGuideSupplementalMetadata.Merge(snapshot.SupplementalXml),
            Channels = snapshot.Channels.Select(JsonGuideChannel.FromChannel).ToArray()
        };
    }
}

/// <summary>Serialized normalized guide channel.</summary>
public sealed record JsonGuideChannel
{
    /// <summary>Gets the stable virtual channel identifier.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the effective channel number.</summary>
    public required string Number { get; init; }

    /// <summary>Gets the effective channel name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the network affiliate when known.</summary>
    public string? Affiliate { get; init; }

    /// <summary>Gets the primary channel image URL.</summary>
    public string? ImageUrl { get; init; }

    /// <summary>Gets whether the channel is protected.</summary>
    public bool IsProtected { get; init; }

    /// <summary>Gets whether the channel is a favorite.</summary>
    public bool IsFavorite { get; init; }

    /// <summary>Gets compact supplemental XMLTV channel metadata.</summary>
    public JsonObject? Supplemental { get; init; }

    /// <summary>Gets the channel's programs.</summary>
    public IReadOnlyList<JsonGuideProgram> Programs { get; init; } = [];

    /// <summary>Creates a serialized channel from one normalized projected channel.</summary>
    internal static JsonGuideChannel FromChannel(VirtualGuideChannel channel) =>
        new()
        {
            Id = channel.ChannelId.Value,
            Number = channel.Number,
            Name = channel.Name,
            Affiliate = channel.Affiliate,
            ImageUrl = channel.ImageUrl,
            IsProtected = channel.IsProtected,
            IsFavorite = channel.IsFavorite,
            Supplemental = JsonGuideSupplementalMetadata.FromXml(channel.SupplementalXml),
            Programs = channel.Programs.Select(JsonGuideProgram.FromProgram).ToArray()
        };
}

/// <summary>Serialized normalized guide program.</summary>
public sealed record JsonGuideProgram
{
    /// <summary>Gets the source-scoped guide channel identity.</summary>
    public required JsonGuideChannelReference ChannelId { get; init; }

    /// <summary>Gets the program start in UTC.</summary>
    public required DateTimeOffset StartUtc { get; init; }

    /// <summary>Gets the program end in UTC.</summary>
    public required DateTimeOffset EndUtc { get; init; }

    /// <summary>Gets the display title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets the optional synopsis.</summary>
    public string? Synopsis { get; init; }

    /// <summary>Gets the optional episode title.</summary>
    public string? EpisodeTitle { get; init; }

    /// <summary>Gets the primary program image URL.</summary>
    public string? ImageUrl { get; init; }

    /// <summary>Gets the optional poster image URL.</summary>
    public string? PosterUrl { get; init; }

    /// <summary>Gets the preferred display episode number.</summary>
    public string? EpisodeNumber { get; init; }

    /// <summary>Gets the original air date in UTC when known.</summary>
    public DateTimeOffset? OriginalAirdateUtc { get; init; }

    /// <summary>Gets the source first-airing marker when known.</summary>
    public bool? IsFirstAiring { get; init; }

    /// <summary>Gets the provider series identifier.</summary>
    public string? SeriesId { get; init; }

    /// <summary>Gets provider categories in source order.</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>Gets structured official XMLTV metadata.</summary>
    public XmltvProgramMetadata? Metadata { get; init; }

    /// <summary>Gets compact supplemental XMLTV metadata not represented by typed properties.</summary>
    public JsonObject? Supplemental { get; init; }

    /// <summary>Creates a serialized program from one normalized guide program.</summary>
    internal static JsonGuideProgram FromProgram(GuideProgram program) =>
        new()
        {
            ChannelId = new JsonGuideChannelReference
            {
                GuideSourceId = program.ChannelId.GuideSourceId.Value,
                ProviderKey = program.ChannelId.ProviderKey
            },
            StartUtc = program.StartUtc,
            EndUtc = program.EndUtc,
            Title = program.Title,
            Synopsis = program.Synopsis,
            EpisodeTitle = program.EpisodeTitle,
            ImageUrl = program.ImageUrl,
            PosterUrl = program.PosterUrl,
            EpisodeNumber = program.EpisodeNumber,
            OriginalAirdateUtc = program.OriginalAirdateUtc,
            IsFirstAiring = program.IsFirstAiring,
            SeriesId = program.SeriesId,
            Categories = program.Categories,
            Metadata = program.Metadata,
            Supplemental = JsonGuideSupplementalMetadata.FromXml(program.SupplementalXml)
        };
}

/// <summary>Converts supplemental XMLTV metadata into compact JSON properties.</summary>
internal static class JsonGuideSupplementalMetadata
{
    private static readonly XNamespace EnvelopeNamespace = "urn:lineup:xmltv-supplemental:v1";

    /// <summary>Merges multiple supplemental XML fragments into one compact object.</summary>
    public static JsonObject Merge(IEnumerable<string> contents)
    {
        var result = new JsonObject();
        foreach (var content in contents)
        {
            var supplemental = FromXml(content);
            if (supplemental is null)
            {
                continue;
            }

            foreach (var property in supplemental)
            {
                AddValue(result, property.Key, property.Value?.DeepClone());
            }
        }

        return result;
    }

    /// <summary>Creates compact metadata from Lineup's supplemental XML envelope.</summary>
    public static JsonObject? FromXml(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        XElement root;
        try
        {
            using var textReader = new StringReader(content);
            using var reader = XmlReader.Create(textReader, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            root = XElement.Load(reader, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("Supplemental XMLTV metadata is invalid.", exception);
        }

        if (root.Name != EnvelopeNamespace + "supplemental")
        {
            return new JsonObject
            {
                [root.Name.LocalName] = ConvertElement(root)
            };
        }

        var result = new JsonObject();
        foreach (var attribute in root.Element(EnvelopeNamespace + "attributes")?.Elements(EnvelopeNamespace + "attribute") ?? [])
        {
            var name = (string?)attribute.Attribute("name") ??
                throw new InvalidDataException("Supplemental XMLTV attribute name is missing.");
            AddValue(result, name, JsonValue.Create((string?)attribute.Attribute("value") ?? string.Empty));
        }

        foreach (var element in root.Element(EnvelopeNamespace + "elements")?.Elements() ?? [])
        {
            AddValue(result, element.Name.LocalName, ConvertElement(element));
        }

        foreach (var owned in root.Elements(EnvelopeNamespace + "owned"))
        {
            var key = (string?)owned.Attribute("key") ??
                throw new InvalidDataException("Supplemental XMLTV owned-element key is missing.");
            var element = owned.Elements().SingleOrDefault() ??
                throw new InvalidDataException("Supplemental XMLTV owned element is missing.");
            AddValue(result, key, ConvertElement(element));
        }

        return result;
    }

    private static JsonNode? ConvertElement(XElement element)
    {
        var attributes = element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration).ToArray();
        var children = element.Elements().ToArray();
        var text = string.Concat(element.Nodes().OfType<XText>().Select(node => node.Value));
        if (attributes.Length == 0 && children.Length == 0)
        {
            return JsonValue.Create(text);
        }

        var result = new JsonObject();
        foreach (var attribute in attributes)
        {
            AddValue(result, attribute.Name.LocalName, JsonValue.Create(attribute.Value));
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            result["$value"] = text;
        }

        foreach (var child in children)
        {
            AddValue(result, child.Name.LocalName, ConvertElement(child));
        }

        return result;
    }

    private static void AddValue(JsonObject target, string key, JsonNode? value)
    {
        if (!target.TryGetPropertyValue(key, out var existing))
        {
            target[key] = value;
            return;
        }

        if (existing is JsonArray array)
        {
            array.Add(value);
            return;
        }

        target[key] = new JsonArray(existing?.DeepClone(), value);
    }
}

/// <summary>Serialized source-scoped guide channel identity.</summary>
public sealed record JsonGuideChannelReference
{
    /// <summary>Gets the owning guide-source identifier.</summary>
    public required Guid GuideSourceId { get; init; }

    /// <summary>Gets the stable adapter-defined channel key.</summary>
    public required string ProviderKey { get; init; }
}
