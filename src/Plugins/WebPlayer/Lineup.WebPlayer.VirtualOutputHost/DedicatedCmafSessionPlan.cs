using Lineup.Common.Streaming;
using Lineup.VirtualOutputs.Protocol;
using Lineup.WebPlayer.FFmpeg;

namespace Lineup.WebPlayer.VirtualOutputHost;

/// <summary>Describes the shared CMAF plan used by one dedicated Web Player session.</summary>
internal sealed record DedicatedCmafSessionPlan
{
    /// <summary>Gets the validated effective request.</summary>
    internal required CmafStreamRequest Request { get; init; }

    /// <summary>Gets the brokered MPEG-TS source metadata.</summary>
    internal required MediaProbeResult Source { get; init; }

    /// <summary>Gets the normalized native physical source metadata.</summary>
    internal required MediaProbeResult RawSource { get; init; }

    /// <summary>Gets the effective virtual-device presentation profile.</summary>
    internal required VirtualTranscodeProfile Profile { get; init; }

    /// <summary>Gets the selected tracks.</summary>
    internal required WebPlayerTrackSelection Selection { get; init; }

    /// <summary>Gets the packaged audio renditions.</summary>
    internal required IReadOnlyList<CmafAudioRendition> AudioRenditions { get; init; }

    /// <summary>Gets selectable subtitle tracks.</summary>
    internal required IReadOnlyList<MediaTrackMetadata> Subtitles { get; init; }

    /// <summary>Gets output paths for WebVTT sidecars.</summary>
    internal required IReadOnlyDictionary<int, string> SubtitlePaths { get; init; }

    /// <summary>Gets the main CMAF FFmpeg arguments.</summary>
    internal required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>Gets the embedded-caption track requiring a parallel extractor.</summary>
    internal MediaTrackMetadata? EmbeddedCaptions { get; init; }

    /// <summary>Gets whether the presentation contains copied source video.</summary>
    internal bool HasSourceVideoRendition { get; init; }

    /// <summary>Gets the copied HEVC codec string when one is required for manifest signaling.</summary>
    internal string? SourceVideoCodec { get; init; }

    /// <summary>Creates a plan from the trusted broker response and browser request.</summary>
    internal static DedicatedCmafSessionPlan Create(VirtualOutputOpenStreamResponse response, CmafStreamRequest request, string directory)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (request.CompatibilityProfile is not null && !CmafCompatibilityProfilePolicy.IsValid(request.CompatibilityProfile))
        {
            throw new ArgumentException("The browser compatibility profile is incomplete, stale, oversized, or invalid.", nameof(request));
        }

        var source = response.Source
            ?? throw new InvalidDataException("The capability broker did not provide media metadata for the dedicated Web Player stream.");
        var rawSource = response.RawSource ?? source;
        var profile = response.Profile
            ?? throw new InvalidDataException("The capability broker did not provide the effective virtual-device profile.");
        request = CmafStreamPlanner.ApplyOverrides(request);
        var selection = CmafStreamPlanner.SelectTracks(source, request);
        request = CmafStreamPlanner.ApplyTopologyProfile(request, profile);
        var audioRenditions = CmafStreamPlanner.CreatePresentationAudioRenditions(source, selection.Audio, request, profile);
        var subtitles = CmafStreamPlanner.GetSelectableSubtitles(source);
        var subtitlePaths = subtitles.ToDictionary(
            subtitle => subtitle.Index,
            subtitle => Path.Combine(directory, $"captions-{subtitle.Index}.vtt"));
        var manifestPath = Path.Combine(directory, CmafStreamPlanner.DashManifestName);
        var sourceVideo = source.Tracks.FirstOrDefault(track => track.Type == MediaTrackType.Video);
        var videoRenditions = CmafStreamPlanner.CreateVideoRenditions(
            sourceVideo,
            request,
            selection.SubtitlePresentation == SubtitlePresentation.BurnIn,
            profile.Deinterlace);
        var hasSourceVideoRendition = videoRenditions.Any(plan => plan.CopySource);
        return new DedicatedCmafSessionPlan
        {
            Request = request,
            Source = source,
            RawSource = rawSource,
            Profile = profile,
            Selection = selection,
            AudioRenditions = audioRenditions,
            Subtitles = subtitles,
            SubtitlePaths = subtitlePaths,
            Arguments = CmafStreamPlanner.CreateArguments(profile, source, selection, request, manifestPath, subtitlePaths),
            EmbeddedCaptions = subtitles.FirstOrDefault(subtitle => subtitle.IsEmbeddedClosedCaptions),
            HasSourceVideoRendition = hasSourceVideoRendition,
            SourceVideoCodec = hasSourceVideoRendition ? CmafStreamPlanner.CreateHevcCodecString(sourceVideo) : null
        };
    }

    /// <summary>Creates the normalized active-stream presentation reported to Core.</summary>
    internal ActiveStreamSnapshot CreateActiveStreamSnapshot(string streamSessionId, string channel, DateTime startedAtUtc)
    {
        return ActiveStreamPlanFactory.CreateCmaf(
            streamSessionId,
            channel,
            startedAtUtc,
            RawSource,
            Source,
            Selection,
            Request,
            Profile);
    }

    /// <summary>Creates the public dedicated-worker start response.</summary>
    internal DedicatedCmafStartResponse CreateResponse(string sessionId, Uri publicBaseUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(publicBaseUri);
        var mediaRoot = new Uri(publicBaseUri, $"media/{sessionId}/");
        var fallback = AudioRenditions.FirstOrDefault(rendition => !rendition.Plan.CopySource)?.Plan;
        return new DedicatedCmafStartResponse(
            sessionId,
            new Uri(mediaRoot, CmafStreamPlanner.HlsManifestName).AbsoluteUri,
            new Uri(mediaRoot, CmafStreamPlanner.DashManifestName).AbsoluteUri)
        {
            FallbackAudioTitle = fallback?.Title,
            FallbackAudioCodec = fallback?.Codec,
            Subtitles = Subtitles.Select(subtitle => new CmafSubtitleRendition(
                subtitle.Index,
                subtitle.Title ?? subtitle.Language ?? $"Subtitle {subtitle.Index}",
                subtitle.Language,
                new Uri(mediaRoot, $"captions-{subtitle.Index}.vtt").AbsoluteUri,
                subtitle.IsEmbeddedClosedCaptions)).ToArray(),
            HasSourceVideoRendition = HasSourceVideoRendition,
            SourceVideoCodec = SourceVideoCodec,
            StopUrl = new Uri(publicBaseUri, $"media/stop/{sessionId}").AbsoluteUri
        };
    }
}

/// <summary>Describes one started CMAF presentation hosted by the dedicated worker.</summary>
internal sealed record DedicatedCmafStartResponse(string SessionId, string HlsManifestUrl, string DashManifestUrl)
{
    /// <summary>Gets whether source audio required a fallback-only startup retry.</summary>
    public bool SourceAudioFallbackApplied { get; init; }

    /// <summary>Gets the configured fallback rendition title.</summary>
    public string? FallbackAudioTitle { get; init; }

    /// <summary>Gets the configured fallback rendition codec.</summary>
    public string? FallbackAudioCodec { get; init; }

    /// <summary>Gets browser-selectable subtitle sidecars.</summary>
    public IReadOnlyList<CmafSubtitleRendition> Subtitles { get; init; } = [];

    /// <summary>Gets whether this presentation includes copied source video.</summary>
    public bool HasSourceVideoRendition { get; init; }

    /// <summary>Gets the RFC 6381 codec string for copied source video.</summary>
    public string? SourceVideoCodec { get; init; }

    /// <summary>Gets the endpoint used to stop the dedicated presentation.</summary>
    public string? StopUrl { get; init; }
}
