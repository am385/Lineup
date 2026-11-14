namespace Lineup.App.Web.Services.Playback;

/// <summary>Classifies application startup failures without prescribing an HTTP response.</summary>
public enum ManagementPlaybackFailureKind
{
    /// <summary>The request is invalid.</summary>
    InvalidRequest,
    /// <summary>The requested presentation cannot be produced.</summary>
    Unsupported,
    /// <summary>The channel is disabled.</summary>
    Disabled,
    /// <summary>The requested channel or route does not exist.</summary>
    NotFound,
    /// <summary>The required source or runtime is unavailable.</summary>
    Unavailable,
    /// <summary>The viewer or startup admission limit was reached.</summary>
    AdmissionLimit,
    /// <summary>The upstream source or media processor failed.</summary>
    MediaFailure,
    /// <summary>The bounded startup deadline expired.</summary>
    Timeout,
    /// <summary>Content protection prevents playback.</summary>
    ProtectedContent,
    /// <summary>Source acquisition failed with a classified source error.</summary>
    SourceAcquisition,
    /// <summary>An unexpected application failure prevented startup.</summary>
    InternalError
}

/// <summary>Describes a startup failure and optional structured media diagnostics.</summary>
public sealed record ManagementPlaybackFailure(ManagementPlaybackFailureKind Kind, string Error, string? Code = null, string? Stage = null)
{
    /// <summary>Gets the protection classification when protected content prevented playback.</summary>
    public ProtectedContentDetection? Protection { get; init; }

    /// <summary>Gets the classified source acquisition failure for transport-specific translation.</summary>
    public StreamAcquisitionException? Acquisition { get; init; }
}

/// <summary>Identifies one available subtitle without constructing a transport URL.</summary>
public sealed record ManagementPlaybackSubtitle(int SourceIndex, string Title, string? Language, bool IsEmbeddedClosedCaptions);

/// <summary>Describes a ready shared presentation independently of transport routes.</summary>
public sealed record ManagementPlaybackPresentation(string SessionId)
{
    /// <summary>Gets whether source audio was replaced after a muxer compatibility failure.</summary>
    public bool SourceAudioFallbackApplied { get; init; }

    /// <summary>Gets the packaged fallback audio title.</summary>
    public string? FallbackAudioTitle { get; init; }

    /// <summary>Gets the packaged fallback audio codec.</summary>
    public string? FallbackAudioCodec { get; init; }

    /// <summary>Gets whether the presentation includes copied source video.</summary>
    public bool HasSourceVideoRendition { get; init; }

    /// <summary>Gets the copied source video codec string.</summary>
    public string? SourceVideoCodec { get; init; }

    /// <summary>Gets available subtitle tracks.</summary>
    public IReadOnlyList<ManagementPlaybackSubtitle> Subtitles { get; init; } = [];
}

/// <summary>Describes a ready synthetic compatibility presentation without constructing transport URLs.</summary>
public sealed record ManagementCompatibilityPresentation(string SessionId, CmafProtocol Protocol, string VideoCodec, string AudioCodec, int ChannelCount, int Width, int Height, bool HasSubtitles);

/// <summary>Represents the terminal application result of starting one viewer.</summary>
public sealed record ManagementPlaybackStartResult
{
    /// <summary>Gets a successfully started channel presentation.</summary>
    public ManagementPlaybackPresentation? Presentation { get; init; }

    /// <summary>Gets a successfully started compatibility presentation.</summary>
    public ManagementCompatibilityPresentation? Compatibility { get; init; }

    /// <summary>Gets a classified startup failure.</summary>
    public ManagementPlaybackFailure? Failure { get; init; }

    /// <summary>Gets whether the viewer stopped or canceled before startup completed.</summary>
    public bool IsCanceled { get; init; }
}

/// <summary>Exposes immutable artifact metadata without exposing owned processes or lifecycle resources.</summary>
public sealed record ManagementPlaybackArtifacts(string CmafDirectory, bool IsCompatibilityTest, string? SourceVideoCodec, IReadOnlyList<CmafAudioRendition> AudioRenditions);

/// <summary>Describes the current state of one independently owned viewer alias.</summary>
public sealed record ManagementPlaybackViewer(string SessionId, string Channel, DateTime StartTime, double DurationMinutes, bool IsRunning);
