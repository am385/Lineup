using Lineup.Common.Streaming;

namespace Lineup.Common.Guides;

/// <summary>
/// Identifies one configured guide source.
/// </summary>
/// <param name="Value">Stable guide-source identifier.</param>
public readonly record struct GuideSourceId(Guid Value)
{
    /// <summary>Creates a new guide-source identifier.</summary>
    public static GuideSourceId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// Identifies one configured guide output.
/// </summary>
/// <param name="Value">Stable guide-output identifier.</param>
public readonly record struct GuideOutputId(Guid Value)
{
    /// <summary>Creates a new guide-output identifier.</summary>
    public static GuideOutputId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// Identifies one provider guide channel within a configured guide source.
/// </summary>
/// <param name="GuideSourceId">Owning guide source.</param>
/// <param name="ProviderKey">Stable adapter-defined guide channel key.</param>
public readonly record struct GuideChannelId(GuideSourceId GuideSourceId, string ProviderKey);

/// <summary>
/// Describes one channel returned by a guide source.
/// </summary>
public sealed record GuideChannel
{
    /// <summary>Gets the source-scoped identity.</summary>
    public required GuideChannelId Id { get; init; }

    /// <summary>Gets the provider display number.</summary>
    public required string Number { get; init; }

    /// <summary>Gets the provider display name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the network affiliate when supplied by the guide source.</summary>
    public string? Affiliate { get; init; }

    /// <summary>Gets the primary channel image URL.</summary>
    public string? ImageUrl { get; init; }

    /// <summary>Gets whether the guide source marks the channel as protected.</summary>
    public bool IsProtected { get; init; }

    /// <summary>Gets whether the guide source marks the channel as a favorite.</summary>
    public bool IsFavorite { get; init; }

    /// <summary>Gets supplemental XMLTV metadata not represented by typed properties.</summary>
    public string? SupplementalXml { get; init; }
}

/// <summary>
/// Describes one normalized program from a guide source.
/// </summary>
public sealed record GuideProgram
{
    /// <summary>Gets the source guide channel.</summary>
    public required GuideChannelId ChannelId { get; init; }

    /// <summary>Gets the program start in UTC.</summary>
    public required DateTimeOffset StartUtc { get; init; }

    /// <summary>Gets the program end in UTC.</summary>
    public required DateTimeOffset EndUtc { get; init; }

    /// <summary>Gets the display title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets an optional synopsis.</summary>
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
}

/// <summary>
/// Atomic guide import returned by one configured source.
/// </summary>
public sealed record GuideImport
{
    /// <summary>Gets the guide source.</summary>
    public required GuideSourceId SourceId { get; init; }

    /// <summary>Gets discovered guide channels.</summary>
    public required IReadOnlyList<GuideChannel> Channels { get; init; }

    /// <summary>Gets normalized programs.</summary>
    public required IReadOnlyList<GuideProgram> Programs { get; init; }

    /// <summary>Gets the covered UTC start.</summary>
    public required DateTimeOffset CoverageStartUtc { get; init; }

    /// <summary>Gets the covered UTC end.</summary>
    public required DateTimeOffset CoverageEndUtc { get; init; }

    /// <summary>Gets when the source data was fetched.</summary>
    public DateTimeOffset FetchedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Gets supplemental XMLTV root metadata.</summary>
    public string? SupplementalXml { get; init; }
}

/// <summary>
/// Describes a resolved guide-output destination.
/// </summary>
public sealed record GuideOutputDestination
{
    /// <summary>Gets the configured publication path.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the response content type.</summary>
    public required string ContentType { get; init; }

    /// <summary>Gets the suggested download filename.</summary>
    public required string FileName { get; init; }
}

/// <summary>
/// Supplies host-resolved defaults when creating one guide output.
/// </summary>
public sealed record GuideOutputCreationContext
{
    /// <summary>Gets the directory where newly created guide outputs should be placed.</summary>
    public required string DefaultOutputDirectoryPath { get; init; }

    /// <summary>Gets the stable code-safe key assigned to the new output.</summary>
    public required string OutputKey { get; init; }
}

/// <summary>
/// Defines the physical devices whose channels may use one guide source.
/// </summary>
/// <param name="SourceId">Guide source identity.</param>
/// <param name="PhysicalDeviceIds">Compatible active physical-device identities.</param>
public sealed record GuideSourceScope(GuideSourceId SourceId, IReadOnlyList<PhysicalDeviceId> PhysicalDeviceIds);

/// <summary>
/// Provider-neutral guide projection for one virtual device.
/// </summary>
public sealed record VirtualGuideSnapshot
{
    /// <summary>Gets the virtual device.</summary>
    public required VirtualDeviceId VirtualDeviceId { get; init; }

    /// <summary>Gets resolved channels.</summary>
    public required IReadOnlyList<VirtualGuideChannel> Channels { get; init; }

    /// <summary>Gets supplemental XMLTV root metadata from contributing guide sources.</summary>
    public IReadOnlyList<string> SupplementalXml { get; init; } = [];
}

/// <summary>
/// Guide content resolved for one virtual channel.
/// </summary>
public sealed record VirtualGuideChannel
{
    /// <summary>Gets the virtual channel identity.</summary>
    public required VirtualChannelId ChannelId { get; init; }

    /// <summary>Gets the effective globally resolved number.</summary>
    public required string Number { get; init; }

    /// <summary>Gets the effective globally resolved name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the network affiliate from the authoritative guide channel.</summary>
    public string? Affiliate { get; init; }

    /// <summary>Gets the primary channel image URL.</summary>
    public string? ImageUrl { get; init; }

    /// <summary>Gets whether the authoritative guide channel is protected.</summary>
    public bool IsProtected { get; init; }

    /// <summary>Gets whether the authoritative guide channel is a favorite.</summary>
    public bool IsFavorite { get; init; }

    /// <summary>Gets supplemental XMLTV channel metadata.</summary>
    public string? SupplementalXml { get; init; }

    /// <summary>Gets programs from the authoritative physical guide binding.</summary>
    public required IReadOnlyList<GuideProgram> Programs { get; init; }
}
