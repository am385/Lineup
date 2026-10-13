using Lineup.Common.Guides;

namespace Lineup.GuideOutputs.Json;

/// <summary>Versioned JSON representation of one normalized virtual guide snapshot.</summary>
public sealed record JsonGuideDocument
{
    /// <summary>Current serialized document schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the serialized document schema version.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Gets the virtual device whose guide was projected.</summary>
    public required Guid VirtualDeviceId { get; init; }

    /// <summary>Gets supplemental XMLTV root metadata from contributing guide sources.</summary>
    public IReadOnlyList<string> SupplementalXml { get; init; } = [];

    /// <summary>Gets the projected guide channels.</summary>
    public IReadOnlyList<JsonGuideChannel> Channels { get; init; } = [];

    /// <summary>Creates a JSON document from a normalized virtual guide snapshot.</summary>
    public static JsonGuideDocument FromSnapshot(VirtualGuideSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new JsonGuideDocument
        {
            VirtualDeviceId = snapshot.VirtualDeviceId.Value,
            SupplementalXml = snapshot.SupplementalXml,
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

    /// <summary>Gets supplemental XMLTV channel metadata.</summary>
    public string? SupplementalXml { get; init; }

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
            SupplementalXml = channel.SupplementalXml,
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

    /// <summary>Gets supplemental XMLTV metadata not represented by typed properties.</summary>
    public string? SupplementalXml { get; init; }

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
            SupplementalXml = program.SupplementalXml
        };
}

/// <summary>Serialized source-scoped guide channel identity.</summary>
public sealed record JsonGuideChannelReference
{
    /// <summary>Gets the owning guide-source identifier.</summary>
    public required Guid GuideSourceId { get; init; }

    /// <summary>Gets the stable adapter-defined channel key.</summary>
    public required string ProviderKey { get; init; }
}
