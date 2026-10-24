using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;

namespace Lineup.LineupLink.Protocol;

/// <summary>Defines the versioned Lineup Link wire protocol.</summary>
public static class LineupLinkProtocol
{
    /// <summary>Gets the protocol version implemented by this release.</summary>
    public const int Version = 2;

    /// <summary>Gets the Lineup Link adapter kind.</summary>
    public const string AdapterKind = "lineup-link";

    /// <summary>Gets the ASP.NET Core controller route for this protocol version.</summary>
    public const string ControllerRoute = "api/lineup-link/v2/virtual-devices/{virtualDeviceId:guid}";

    /// <summary>Gets the MPEG-TS media type used for linked streams.</summary>
    public const string MpegTsMediaType = "video/mp2t";

    /// <summary>Creates the device-scoped API root.</summary>
    public static string DevicePath(Guid virtualDeviceId) =>
        $"/api/lineup-link/v{Version}/virtual-devices/{virtualDeviceId:D}";

    /// <summary>Creates the manifest route.</summary>
    public static string ManifestPath(Guid virtualDeviceId) => $"{DevicePath(virtualDeviceId)}/manifest";

    /// <summary>Creates the lineup route.</summary>
    public static string LineupPath(Guid virtualDeviceId) => $"{DevicePath(virtualDeviceId)}/lineup";

    /// <summary>Creates the guide route.</summary>
    public static string GuidePath(Guid virtualDeviceId) => $"{DevicePath(virtualDeviceId)}/guide";

    /// <summary>Creates one channel stream route.</summary>
    public static string StreamPath(Guid virtualDeviceId, string channelKey) =>
        $"{DevicePath(virtualDeviceId)}/streams/{Uri.EscapeDataString(channelKey)}";
}

/// <summary>Describes one remote Lineup Link virtual device.</summary>
public sealed record LineupLinkManifest
{
    /// <summary>Gets the wire protocol version.</summary>
    public int ProtocolVersion { get; init; } = LineupLinkProtocol.Version;

    /// <summary>Gets the remote Lineup process identity.</summary>
    public required Guid InstanceId { get; init; }

    /// <summary>Gets the remote virtual-device identity.</summary>
    public required Guid VirtualDeviceId { get; init; }

    /// <summary>Gets the remote virtual-device display name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the finite tuner capacity, or <see langword="null"/> when unbounded.</summary>
    public int? TunerCount { get; init; }

    /// <summary>Gets whether the remote output can publish guide data.</summary>
    public bool GuideAvailable { get; init; } = true;
}

/// <summary>Describes one channel exported by a Lineup Link virtual device.</summary>
public sealed record LineupLinkChannel
{
    /// <summary>Gets the stable remote channel key.</summary>
    public required string Key { get; init; }

    /// <summary>Gets the display channel number.</summary>
    public required string Number { get; init; }

    /// <summary>Gets the display channel name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets provider-neutral channel metadata.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>Describes one Lineup Link lineup response.</summary>
public sealed record LineupLinkLineup
{
    /// <summary>Gets the wire protocol version.</summary>
    public int ProtocolVersion { get; init; } = LineupLinkProtocol.Version;

    /// <summary>Gets the remote virtual-device identity.</summary>
    public required Guid VirtualDeviceId { get; init; }

    /// <summary>Gets channels in canonical numeric order.</summary>
    public IReadOnlyList<LineupLinkChannel> Channels { get; init; } = [];
}

/// <summary>Describes one guide channel exported through Lineup Link.</summary>
public sealed record LineupLinkGuideChannel
{
    /// <summary>Gets the stable channel key shared with the lineup response.</summary>
    public required string Key { get; init; }

    /// <summary>Gets the display channel number.</summary>
    public required string Number { get; init; }

    /// <summary>Gets the display channel name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets an optional affiliate.</summary>
    public string? Affiliate { get; init; }

    /// <summary>Gets an optional channel image URL.</summary>
    public string? ImageUrl { get; init; }

    /// <summary>Gets whether the channel is protected.</summary>
    public bool IsProtected { get; init; }

    /// <summary>Gets whether the channel is a favorite.</summary>
    public bool IsFavorite { get; init; }

    /// <summary>Gets preserved XMLTV metadata.</summary>
    public string? SupplementalXml { get; init; }
}

/// <summary>Describes one guide program exported through Lineup Link.</summary>
public sealed record LineupLinkGuideProgram
{
    /// <summary>Gets the stable channel key.</summary>
    public required string ChannelKey { get; init; }

    /// <summary>Gets the program start in UTC.</summary>
    public required DateTimeOffset StartUtc { get; init; }

    /// <summary>Gets the program end in UTC.</summary>
    public required DateTimeOffset EndUtc { get; init; }

    /// <summary>Gets the display title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets an optional synopsis.</summary>
    public string? Synopsis { get; init; }

    /// <summary>Gets an optional episode title.</summary>
    public string? EpisodeTitle { get; init; }

    /// <summary>Gets an optional image URL.</summary>
    public string? ImageUrl { get; init; }

    /// <summary>Gets an optional poster URL.</summary>
    public string? PosterUrl { get; init; }

    /// <summary>Gets the preferred episode number.</summary>
    public string? EpisodeNumber { get; init; }

    /// <summary>Gets the original air date when known.</summary>
    public DateTimeOffset? OriginalAirdateUtc { get; init; }

    /// <summary>Gets whether this is a first airing when known.</summary>
    public bool? IsFirstAiring { get; init; }

    /// <summary>Gets the provider series identifier.</summary>
    public string? SeriesId { get; init; }

    /// <summary>Gets categories in source order.</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>Gets structured XMLTV metadata.</summary>
    public XmltvProgramMetadata? Metadata { get; init; }

    /// <summary>Gets preserved XMLTV metadata.</summary>
    public string? SupplementalXml { get; init; }
}

/// <summary>Describes one Lineup Link guide response.</summary>
public sealed record LineupLinkGuide
{
    /// <summary>Gets the wire protocol version.</summary>
    public int ProtocolVersion { get; init; } = LineupLinkProtocol.Version;

    /// <summary>Gets the remote virtual-device identity.</summary>
    public required Guid VirtualDeviceId { get; init; }

    /// <summary>Gets guide channels.</summary>
    public IReadOnlyList<LineupLinkGuideChannel> Channels { get; init; } = [];

    /// <summary>Gets guide programs.</summary>
    public IReadOnlyList<LineupLinkGuideProgram> Programs { get; init; } = [];

    /// <summary>Gets preserved XMLTV root metadata.</summary>
    public string? SupplementalXml { get; init; }
}

/// <summary>Describes a safe Lineup Link protocol failure.</summary>
public sealed record LineupLinkError
{
    /// <summary>Gets a stable error code.</summary>
    public required string Code { get; init; }

    /// <summary>Gets an operator-facing message without secrets.</summary>
    public required string Message { get; init; }
}
