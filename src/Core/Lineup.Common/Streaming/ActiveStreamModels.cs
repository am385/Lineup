namespace Lineup.Common.Streaming;

/// <summary>
/// Identifies a hosted stream's output container.
/// </summary>
public enum HostedStreamFormat
{
    /// <summary>MPEG transport stream output.</summary>
    MpegTs = 0,

    /// <summary>Shared Common Media Application Format output.</summary>
    Cmaf = 2
}

/// <summary>Describes topology identities associated with one active hosted stream.</summary>
public sealed record ActiveStreamFlowContext
{
    /// <summary>Gets the selected physical device.</summary>
    public PhysicalDeviceId? PhysicalDeviceId { get; init; }

    /// <summary>Gets the selected provider channel.</summary>
    public SourceChannelId? SourceChannelId { get; init; }

    /// <summary>Gets the destination virtual device.</summary>
    public VirtualDeviceId? VirtualDeviceId { get; init; }

    /// <summary>Gets the optional generated source description.</summary>
    public string? SyntheticSource { get; init; }

    /// <summary>Gets the optional upstream producer graph node.</summary>
    public string? ProducerNodeId { get; init; }

    /// <summary>Gets the reusable output identity.</summary>
    public StreamSessionId? ReusableOutputId { get; init; }
}

/// <summary>
/// Describes how one source track is delivered to a client.
/// </summary>
public sealed record ActiveStreamTrack(
    MediaTrackType Type,
    string SourceCodec,
    string OutputCodec,
    long? SourceBitRate,
    long? OutputBitRate,
    int? SourceWidth,
    int? SourceHeight,
    int? SourceChannels,
    int? OutputChannels,
    int? OutputSampleRate)
{
    /// <summary>Gets the absolute source stream index.</summary>
    public int SourceIndex { get; init; }

    /// <summary>Gets the source language tag.</summary>
    public string? Language { get; init; }

    /// <summary>Gets the source title.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the output rendition title when one source track produces multiple outputs.</summary>
    public string? OutputTitle { get; init; }

    /// <summary>Gets whether the source marks this track as default.</summary>
    public bool IsDefault { get; init; }

    /// <summary>Gets whether the source marks this track as forced.</summary>
    public bool IsForced { get; init; }

    /// <summary>Gets whether the source marks this track for hearing-impaired viewers.</summary>
    public bool IsHearingImpaired { get; init; }

    /// <summary>Gets whether the source reports embedded closed captions.</summary>
    public bool HasClosedCaptions { get; init; }

    /// <summary>Gets whether this synthetic subtitle track represents captions embedded in a video stream.</summary>
    public bool IsEmbeddedClosedCaptions { get; init; }

    /// <summary>Gets whether this track is selected in the output.</summary>
    public bool IsSelected { get; init; } = true;

    /// <summary>Gets the subtitle presentation used for this output.</summary>
    public SubtitlePresentation? SubtitlePresentation { get; init; }
}

/// <summary>Describes one media track at a specific streaming stage.</summary>
public sealed record ActiveStreamMediaTrack(
    int Index,
    MediaTrackType Type,
    string Codec,
    long? BitRate,
    int? Width,
    int? Height,
    int? Channels,
    int? SampleRate)
{
    /// <summary>Gets the language tag.</summary>
    public string? Language { get; init; }

    /// <summary>Gets the track title.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the rendition title.</summary>
    public string? RenditionTitle { get; init; }
}

/// <summary>Describes the complete media presented at one streaming stage.</summary>
public sealed record ActiveStreamMediaSnapshot(long? BitRate, IReadOnlyList<ActiveStreamMediaTrack> Tracks);

/// <summary>Represents an active stream hosted by Lineup.</summary>
public sealed record ActiveStreamSnapshot(string SessionId, string Channel, HostedStreamFormat Format, DateTime StartedAtUtc, long? SourceBitRate, IReadOnlyList<ActiveStreamTrack> Tracks)
{
    /// <summary>Gets the remote network endpoint connected to the hosted stream.</summary>
    public string? ClientAddress { get; init; }

    /// <summary>Gets the optional client identifier used to notify an owning frontend.</summary>
    public string? ClientId { get; init; }

    /// <summary>Gets the stable authenticated external-client identifier.</summary>
    public string? AuthenticatedClientId { get; init; }

    /// <summary>Gets the safe authenticated external-client display name.</summary>
    public string? AuthenticatedClientName { get; init; }

    /// <summary>Gets topology identities associated with this stream.</summary>
    public ActiveStreamFlowContext? Flow { get; init; }

    /// <summary>Gets the raw media received from the physical source.</summary>
    public ActiveStreamMediaSnapshot? PhysicalInput { get; init; }

    /// <summary>Gets the effective media after physical-device processing.</summary>
    public ActiveStreamMediaSnapshot? PhysicalOutput { get; init; }

    /// <summary>Gets the final media delivered after virtual-device processing.</summary>
    public ActiveStreamMediaSnapshot? VirtualOutput { get; init; }
}
