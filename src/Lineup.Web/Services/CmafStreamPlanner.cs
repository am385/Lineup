namespace Lineup.Web.Services;

/// <summary>
/// Identifies a manifest protocol exposed by a shared CMAF presentation.
/// </summary>
public enum CmafProtocol
{
    /// <summary>Dynamic Adaptive Streaming over HTTP.</summary>
    Dash,

    /// <summary>HTTP Live Streaming.</summary>
    Hls
}

/// <summary>
/// Selects the client playback protocol, including automatic DASH-to-HLS fallback.
/// </summary>
public enum CmafPlaybackProtocol
{
    /// <summary>Prefers DASH and permits HLS fallback.</summary>
    Auto,

    /// <summary>Uses HLS only.</summary>
    Hls,

    /// <summary>Uses DASH only.</summary>
    Dash
}

/// <summary>
/// Selects the audio policy for a shared CMAF presentation.
/// </summary>
public enum CmafPreferredAudio
{
    /// <summary>Copies an eligible source codec and otherwise uses the configured fallback.</summary>
    Source,

    /// <summary>Always uses the configured fallback output.</summary>
    Fallback,

    /// <summary>Legacy name for <see cref="Fallback"/> retained for query compatibility.</summary>
    Aac = Fallback,

    /// <summary>Uses measured browser capabilities to choose source copy or one fallback.</summary>
    Auto
}

/// <summary>
/// Selects the video policy for a shared CMAF presentation.
/// </summary>
public enum CmafPreferredVideo
{
    /// <summary>Copies eligible source video and packages H.264 beside it when compatibility fallback may be needed.</summary>
    Source,

    /// <summary>Always uses the H.264 compatibility output.</summary>
    Fallback,

    /// <summary>Uses measured browser capabilities to choose source copy or H.264.</summary>
    Auto
}

/// <summary>
/// Selects the codec and channel limit for the CMAF compatibility rendition.
/// </summary>
public enum CmafFallbackAudio
{
    /// <summary>Encodes broadly compatible stereo AAC.</summary>
    AacStereo,

    /// <summary>Encodes AAC matching the source up to 5.1 channels.</summary>
    AacUpTo5Point1,

    /// <summary>Encodes AAC matching the source up to 7.1 channels.</summary>
    AacUpTo7Point1,

    /// <summary>Encodes AC-3 matching the source up to 5.1 channels.</summary>
    Ac3,

    /// <summary>Encodes channel-based E-AC-3 matching the source up to 5.1 channels.</summary>
    Eac3
}

/// <summary>
/// Describes optional per-setting manual overrides applied over a measured browser profile.
/// </summary>
public sealed record CmafStreamOverrides
{
    /// <summary>Gets whether manual stream-setting overrides are enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>Gets the explicit protocol, or <see langword="null"/> to preserve the browser profile.</summary>
    public CmafPlaybackProtocol? Protocol { get; init; }

    /// <summary>Gets the explicit quality cap, or <see langword="null"/> to preserve automatic quality.</summary>
    public WebPlayerQuality? Quality { get; init; }

    /// <summary>Gets the explicit video policy, or <see langword="null"/> to preserve the browser profile.</summary>
    public CmafPreferredVideo? Video { get; init; }

    /// <summary>Gets the explicit audio policy, or <see langword="null"/> to preserve the browser profile.</summary>
    public CmafPreferredAudio? Audio { get; init; }

    /// <summary>Gets the explicit fallback profile, or <see langword="null"/> to preserve measured fallback ranking.</summary>
    public CmafFallbackAudio? FallbackAudio { get; init; }
}

/// <summary>
/// Configures creation of one shared CMAF DASH/HLS presentation.
/// </summary>
public sealed record CmafStreamRequest
{
    /// <summary>Gets the selected video quality.</summary>
    public WebPlayerQuality Quality { get; init; } = WebPlayerQuality.AppDefault;

    /// <summary>Gets the requested source-video or H.264 fallback policy.</summary>
    public CmafPreferredVideo PreferredVideo { get; init; } = CmafPreferredVideo.Source;

    /// <summary>Gets the absolute selected source audio stream index.</summary>
    public int? AudioTrack { get; init; }

    /// <summary>Gets the absolute selected source subtitle stream index, or <see langword="null"/> for subtitles off.</summary>
    public int? SubtitleTrack { get; init; }

    /// <summary>Gets the requested source-audio or fallback policy.</summary>
    public CmafPreferredAudio PreferredAudio { get; init; } = CmafPreferredAudio.Source;

    /// <summary>Gets the codec and channel limit used when source audio is not CMAF-copy eligible.</summary>
    public CmafFallbackAudio? FallbackAudio { get; init; }

    /// <summary>Gets the legacy AAC-only fallback setting retained for request compatibility.</summary>
    public CmafLegacyAacFallback? AacFallback { get; init; }

    /// <summary>Gets the previously validated subtitle presentation used after an incomplete retry probe.</summary>
    public SubtitlePresentation? SubtitlePresentation { get; init; }

    /// <summary>Gets whether the selected retry subtitle represents embedded captions.</summary>
    public bool EmbeddedCaptions { get; init; }

    /// <summary>Gets the optional owning web-player identifier.</summary>
    public string? ClientId { get; init; }

    /// <summary>Gets the optional completed browser compatibility profile used by Auto policies.</summary>
    public CmafCompatibilityProfile? CompatibilityProfile { get; init; }

    /// <summary>Gets optional manual settings applied over the measured compatibility profile.</summary>
    public CmafStreamOverrides Overrides { get; init; } = new();
}

/// <summary>
/// Describes a started shared CMAF presentation.
/// </summary>
/// <param name="SessionId">The stream session identifier.</param>
/// <param name="HlsManifestUrl">The shared HLS master-playlist URL.</param>
/// <param name="DashManifestUrl">The DASH MPD URL.</param>
public sealed record CmafStreamResponse(string SessionId, string HlsManifestUrl, string DashManifestUrl)
{
    /// <summary>Gets the protocols backed by the shared fragments.</summary>
    public IReadOnlyList<CmafProtocol> Protocols { get; init; } = [CmafProtocol.Dash, CmafProtocol.Hls];

    /// <summary>Gets whether an unsupported source-audio MP4 tag required a fallback-only startup retry.</summary>
    public bool SourceAudioFallbackApplied { get; init; }

    /// <summary>Gets the configured fallback rendition title.</summary>
    public string? FallbackAudioTitle { get; init; }

    /// <summary>Gets the configured fallback rendition codec.</summary>
    public string? FallbackAudioCodec { get; init; }

    /// <summary>Gets browser-selectable text and closed-caption sidecars prepared for this session.</summary>
    public IReadOnlyList<CmafSubtitleRendition> Subtitles { get; init; } = [];

    /// <summary>Gets whether this presentation includes a copied source-video rendition.</summary>
    public bool HasSourceVideoRendition { get; init; }

    /// <summary>Gets the RFC 6381 codec string for copied source video when browser capability testing is required.</summary>
    public string? SourceVideoCodec { get; init; }
}

/// <summary>
/// Describes one browser-selectable CMAF WebVTT sidecar.
/// </summary>
/// <param name="SourceIndex">The absolute source subtitle index.</param>
/// <param name="Label">The display label.</param>
/// <param name="Language">The optional source language.</param>
/// <param name="Url">The incremental WebVTT sidecar URL.</param>
/// <param name="IsEmbeddedClosedCaptions">Whether the sidecar is extracted from captions embedded in video.</param>
public sealed record CmafSubtitleRendition(int SourceIndex, string Label, string? Language, string Url, bool IsEmbeddedClosedCaptions);

/// <summary>
/// Describes the effective CMAF audio output.
/// </summary>
/// <param name="CopySource">Whether FFmpeg copies the source stream.</param>
/// <param name="Codec">The output codec name.</param>
/// <param name="Channels">The output channel count when known.</param>
/// <param name="BitRate">The output bitrate when configured.</param>
/// <param name="SampleRate">The output sample rate when configured.</param>
/// <param name="Title">The rendition title exposed to players and diagnostics.</param>
public sealed record CmafAudioPlan(bool CopySource, string Codec, int? Channels, long? BitRate, int? SampleRate, string Title);

/// <summary>
/// Associates one packaged CMAF audio output with the source track that produces it.
/// </summary>
/// <param name="Source">The source audio track.</param>
/// <param name="Plan">The copy or fallback output plan.</param>
public sealed record CmafAudioRendition(MediaTrackMetadata Source, CmafAudioPlan Plan);

/// <summary>
/// Describes one CMAF video output.
/// </summary>
/// <param name="CopySource">Whether FFmpeg copies the source stream.</param>
/// <param name="Codec">The output codec name.</param>
/// <param name="BitRate">The source or configured output bitrate.</param>
/// <param name="Title">The rendition title exposed to players and diagnostics.</param>
public sealed record CmafVideoPlan(bool CopySource, string Codec, long? BitRate, string Title);

/// <summary>
/// Builds the single FFmpeg DASH muxer presentation shared by DASH and HLS clients.
/// </summary>
public static class CmafStreamPlanner
{
    private static readonly HashSet<string> CopyEligibleAudioCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "aac", "ac3", "eac3", "ac4"
    };
    private static readonly HashSet<string> CopyEligibleVideoCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "h264", "hevc"
    };

    /// <summary>Gets the DASH manifest filename.</summary>
    public const string DashManifestName = "manifest.mpd";

    /// <summary>Gets the HLS master-playlist filename emitted by the DASH muxer.</summary>
    public const string HlsManifestName = "master.m3u8";

    /// <summary>Returns whether the selected source audio codec can be copied into fragmented MP4.</summary>
    public static bool CanCopySourceAudio(string? codec) => codec is not null && CopyEligibleAudioCodecs.Contains(codec);

    /// <summary>Returns whether the selected source video codec can be copied into fragmented MP4.</summary>
    public static bool CanCopySourceVideo(string? codec) => codec is not null && CopyEligibleVideoCodecs.Contains(codec);

    /// <summary>Validates and applies request-scoped stream overrides over the measured compatibility profile.</summary>
    public static CmafStreamRequest ApplyOverrides(CmafStreamRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var overrides = request.Overrides ?? throw new ArgumentException("Stream overrides are required.", nameof(request));
        ValidateOverrides(overrides);
        if (!overrides.Enabled)
        {
            return request;
        }

        var hasProfile = CmafCompatibilityProfilePolicy.IsValid(request.CompatibilityProfile);
        return request with
        {
            Quality = overrides.Quality ?? (hasProfile ? WebPlayerQuality.AppDefault : request.Quality),
            PreferredVideo = overrides.Video ?? (hasProfile ? CmafPreferredVideo.Auto : request.PreferredVideo),
            PreferredAudio = overrides.Audio ?? (hasProfile ? CmafPreferredAudio.Auto : request.PreferredAudio),
            FallbackAudio = overrides.FallbackAudio ?? request.FallbackAudio,
            CompatibilityProfile = CmafCompatibilityOverridePolicy.CreateEffectiveProfile(request.CompatibilityProfile, overrides)
        };
    }

    /// <summary>Validates manual override values without accepting Auto as an explicit override.</summary>
    public static void ValidateOverrides(CmafStreamOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        if (!overrides.Enabled)
        {
            return;
        }
        if (overrides.Protocol is { } protocol && protocol is not (CmafPlaybackProtocol.Dash or CmafPlaybackProtocol.Hls))
        {
            throw new ArgumentException("A protocol override must select DASH or HLS.", nameof(overrides));
        }
        if (overrides.Quality is { } quality && !Enum.IsDefined(quality))
        {
            throw new ArgumentException("The quality override is invalid.", nameof(overrides));
        }
        if (overrides.Video is { } video && video is not (CmafPreferredVideo.Source or CmafPreferredVideo.Fallback))
        {
            throw new ArgumentException("A video override must select Source or H.264 fallback.", nameof(overrides));
        }
        if (overrides.Audio is { } audio && audio is not (CmafPreferredAudio.Source or CmafPreferredAudio.Fallback))
        {
            throw new ArgumentException("An audio override must select Source or fallback.", nameof(overrides));
        }
        if (overrides.FallbackAudio is { } fallback && !Enum.IsDefined(fallback))
        {
            throw new ArgumentException("The fallback-audio override is invalid.", nameof(overrides));
        }
    }

    /// <summary>Creates an RFC 6381 HEVC codec string from FFprobe profile and level metadata.</summary>
    public static string? CreateHevcCodecString(MediaTrackMetadata? video)
    {
        if (video is null || !string.Equals(video.Codec, "hevc", StringComparison.OrdinalIgnoreCase) || video.Level is not > 0)
        {
            return null;
        }

        var profile = video.Profile?.Trim() switch
        {
            "Main" => "1.6",
            "Main 10" => "2.4",
            "Main Still Picture" => "3",
            _ => null
        };
        return profile is null ? null : $"hvc1.{profile}.L{video.Level.Value}";
    }

    /// <summary>Supplies HEVC codec signaling omitted by FFmpeg's DASH muxer when source video is copied.</summary>
    public static string RewriteManifestCodecs(string manifest, string hevcCodec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(hevcCodec);
        return manifest
            .Replace("codecs=\"\"", $"codecs=\"{hevcCodec}\"", StringComparison.Ordinal)
            .Replace("CODECS=\",", $"CODECS=\"{hevcCodec},", StringComparison.Ordinal);
    }

    /// <summary>Supplies user-facing audio labels omitted by FFmpeg's DASH and HLS manifest writers.</summary>
    public static string RewriteManifestAudioLabels(string manifest, IReadOnlyList<CmafAudioRendition> renditions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest);
        ArgumentNullException.ThrowIfNull(renditions);
        if (renditions.Count == 0)
        {
            return manifest;
        }

        if (manifest.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal))
        {
            for (var index = 0; index < renditions.Count; index++)
            {
                var rendition = renditions[index];
                var marker = $"NAME=\"audio_{index + 1}\"";
                var replacement = $"NAME=\"{EscapeHlsAttribute(rendition.Plan.Title)}\"";
                var language = rendition.Source.Language;
                if (!string.IsNullOrWhiteSpace(language))
                {
                    replacement += $",LANGUAGE=\"{EscapeHlsAttribute(language)}\"";
                }
                manifest = manifest.Replace(marker, replacement, StringComparison.Ordinal);
            }
            return manifest;
        }

        var document = System.Xml.Linq.XDocument.Parse(manifest, System.Xml.Linq.LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new System.Xml.XmlException("The DASH manifest has no root element.");
        var audioSets = root
            .Descendants(root.Name.Namespace + "AdaptationSet")
            .Where(element => string.Equals((string?)element.Attribute("contentType"), "audio", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        for (var index = 0; index < Math.Min(audioSets.Length, renditions.Count); index++)
        {
            audioSets[index].AddFirst(new System.Xml.Linq.XElement(root.Name.Namespace + "Label", renditions[index].Plan.Title));
        }
        return document.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
    }

    /// <summary>Validates selected CMAF tracks with the same selection and caption-extraction policy as Watch.</summary>
    public static WebPlayerTrackSelection SelectTracks(MediaProbeResult source, CmafStreamRequest request)
    {
        return WebPlayerTrackPlanner.SelectTracks(source, request.AudioTrack, request.SubtitleTrack, request.SubtitlePresentation, request.EmbeddedCaptions);
    }

    /// <summary>Resolves the fallback profile, including the legacy AAC-only request contract.</summary>
    public static CmafFallbackAudio ResolveFallbackAudio(CmafStreamRequest request)
    {
        if (request.FallbackAudio is { } fallback)
        {
            return Enum.IsDefined(fallback)
                ? fallback
                : throw new ArgumentOutOfRangeException(nameof(request), fallback, "Unsupported CMAF fallback audio.");
        }

        return request.AacFallback switch
        {
            null or CmafLegacyAacFallback.Stereo => CmafFallbackAudio.AacStereo,
            CmafLegacyAacFallback.UpTo5Point1 => CmafFallbackAudio.AacUpTo5Point1,
            CmafLegacyAacFallback.UpTo7Point1 => CmafFallbackAudio.AacUpTo7Point1,
            CmafLegacyAacFallback.Source => throw new ArgumentException("CMAF fallback audio cannot use source passthrough.", nameof(request)),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.AacFallback, "Unsupported legacy CMAF fallback audio.")
        };
    }

    /// <summary>Creates the effective audio plan, including source-copy fallback.</summary>
    public static CmafAudioPlan CreateAudioPlan(MediaTrackMetadata? audio, CmafPreferredAudio preferredAudio, CmafFallbackAudio fallback)
    {
        if (!Enum.IsDefined(preferredAudio))
        {
            throw new ArgumentOutOfRangeException(nameof(preferredAudio), preferredAudio, "Unsupported CMAF preferred audio.");
        }

        if (preferredAudio is CmafPreferredAudio.Source or CmafPreferredAudio.Auto && CanCopySourceAudio(audio?.Codec))
        {
            return new CmafAudioPlan(true, audio!.Codec, audio.Channels, audio.BitRate, audio.SampleRate, "Source");
        }

        var sourceChannels = audio?.Channels;
        var (codec, maximumChannels, bitRate, title) = fallback switch
        {
            CmafFallbackAudio.Eac3 => ("eac3", 6, 640_000, "Fallback EAC3"),
            CmafFallbackAudio.Ac3 => ("ac3", 6, 448_000, "Fallback AC3"),
            CmafFallbackAudio.AacUpTo7Point1 => ("aac", 8, 0, "Fallback AAC up to 7.1"),
            CmafFallbackAudio.AacUpTo5Point1 => ("aac", 6, 0, "Fallback AAC up to 5.1"),
            CmafFallbackAudio.AacStereo => ("aac", 2, 0, "Fallback AAC Stereo"),
            _ => throw new ArgumentOutOfRangeException(nameof(fallback), fallback, "Unsupported CMAF fallback audio.")
        };
        var channels = sourceChannels.HasValue ? Math.Min(sourceChannels.Value, maximumChannels) : 2;
        var effectiveBitRate = codec == "aac" ? Math.Max(channels, 2) * 64_000 : bitRate;
        return new CmafAudioPlan(false, codec, channels, effectiveBitRate, 48_000, title);
    }

    /// <summary>Creates one source-copy or configured fallback audio rendition without speculative duplicate encoding.</summary>
    public static IReadOnlyList<CmafAudioPlan> CreateAudioRenditions(MediaTrackMetadata? audio, CmafPreferredAudio preferredAudio, CmafFallbackAudio fallback)
    {
        return [CreateAudioPlan(audio, preferredAudio, fallback)];
    }

    /// <summary>Creates one copy-first or compatibility rendition for every source audio track.</summary>
    public static IReadOnlyList<CmafAudioRendition> CreatePresentationAudioRenditions(
        MediaProbeResult source,
        MediaTrackMetadata? preferredAudio,
        CmafPreferredAudio preferredAudioPolicy,
        CmafFallbackAudio fallback,
        CmafCompatibilityProfile? compatibilityProfile = null,
        CmafFallbackAudio? automaticFallbackOverride = null,
        bool manualPolicyOverride = false)
    {
        var audioTracks = source.Tracks
            .Where(track => track.Type == MediaTrackType.Audio)
            .OrderByDescending(track => track.Index == preferredAudio?.Index)
            .ThenBy(track => track.Index)
            .ToArray();
        var renditions = new List<CmafAudioRendition>();
        foreach (var audio in audioTracks)
        {
            if (preferredAudioPolicy == CmafPreferredAudio.Auto && CmafCompatibilityProfilePolicy.IsUsable(compatibilityProfile))
            {
                if (CmafCompatibilityProfilePolicy.SupportsAudio(compatibilityProfile!, audio) && CanCopySourceAudio(audio.Codec))
                {
                    var sourcePlan = CreateAudioPlan(audio, CmafPreferredAudio.Source, fallback);
                    renditions.Add(new CmafAudioRendition(audio, sourcePlan with { Title = CreateAudioTitle(audio, "Auto · Source (browser tested)") }));
                    continue;
                }

                var measuredFallback = automaticFallbackOverride ?? CmafCompatibilityProfilePolicy.SelectFallbackAudio(compatibilityProfile!, audio.Channels ?? 2);
                if (!measuredFallback.HasValue)
                {
                    throw new InvalidOperationException($"The browser compatibility profile has no playable audio output for source track {audio.Index}.");
                }
                var fallbackPlan = CreateAudioPlan(audio, CmafPreferredAudio.Fallback, measuredFallback.Value);
                var fallbackReason = automaticFallbackOverride.HasValue ? "manual fallback override" : "source unverified";
                renditions.Add(new CmafAudioRendition(audio, fallbackPlan with { Title = CreateAudioTitle(audio, $"Auto · {fallbackPlan.Title} ({fallbackReason})") }));
                continue;
            }

            var effectivePolicy = preferredAudioPolicy == CmafPreferredAudio.Auto ? CmafPreferredAudio.Source : preferredAudioPolicy;
            foreach (var plan in CreateAudioRenditions(audio, effectivePolicy, fallback))
            {
                var title = manualPolicyOverride ? $"Override · {plan.Title}" : plan.Title;
                renditions.Add(new CmafAudioRendition(audio, plan with { Title = CreateAudioTitle(audio, title) }));
            }
        }
        return renditions;
    }

    /// <summary>Creates the video renditions packaged into one shared CMAF presentation.</summary>
    public static IReadOnlyList<CmafVideoPlan> CreateVideoRenditions(MediaTrackMetadata? video, CmafStreamRequest request, bool burnIn, DeinterlaceMode deinterlaceMode = DeinterlaceMode.Preserve)
    {
        if (!Enum.IsDefined(request.PreferredVideo))
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.PreferredVideo, "Unsupported CMAF preferred video.");
        }

        var fallbackTitle = request.Overrides.Enabled && request.Overrides.Video.HasValue
            ? "Override · Fallback H.264"
            : request.PreferredVideo == CmafPreferredVideo.Auto
                ? "Auto · Fallback H.264 (source unverified)"
                : "Fallback H.264";
        var fallback = new CmafVideoPlan(false, "h264", null, fallbackTitle);
        var canSignalSource = !string.Equals(video?.Codec, "hevc", StringComparison.OrdinalIgnoreCase) || CreateHevcCodecString(video) is not null;
        var autoProfile = request.PreferredVideo == CmafPreferredVideo.Auto && CmafCompatibilityProfilePolicy.IsUsable(request.CompatibilityProfile)
            ? request.CompatibilityProfile
            : null;
        var copyRequested = request.PreferredVideo == CmafPreferredVideo.Source ||
            request.PreferredVideo == CmafPreferredVideo.Auto && (autoProfile is null || CmafCompatibilityProfilePolicy.SupportsVideo(autoProfile, video));
        if (burnIn ||
            SourceDeinterlacePlanner.ShouldDeinterlace(video, deinterlaceMode) ||
            !copyRequested ||
            request.Quality != WebPlayerQuality.AppDefault ||
            !CanCopySourceVideo(video?.Codec) ||
            !canSignalSource)
        {
            return [fallback];
        }

        var sourceTitle = request.Overrides.Enabled && request.Overrides.Video.HasValue
            ? "Override · Source"
            : request.PreferredVideo == CmafPreferredVideo.Auto
                ? "Auto · Source (browser tested)"
                : "Source";
        var source = new CmafVideoPlan(true, video!.Codec, video.BitRate, sourceTitle);
        return [source];
    }

    /// <summary>Returns whether startup diagnostics identify an unsupported source-audio MP4 muxer tag that can be retried with fallback audio.</summary>
    public static bool ShouldRetryWithFallback(CmafStreamRequest request, MediaTrackMetadata? audio, IEnumerable<string> diagnostics)
    {
        if (request.PreferredAudio is not (CmafPreferredAudio.Source or CmafPreferredAudio.Auto) || !CanCopySourceAudio(audio?.Codec))
        {
            return false;
        }

        var codecDiagnostic = $"codec {audio!.Codec}";
        return diagnostics.Any(line =>
            line.Contains(codecDiagnostic, StringComparison.OrdinalIgnoreCase) &&
            line.Contains("not currently supported in container", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns whether any packaged source-audio codec requires a fallback-only muxer retry.</summary>
    public static bool ShouldRetryWithFallback(CmafStreamRequest request, IEnumerable<MediaTrackMetadata> audioTracks, IEnumerable<string> diagnostics) =>
        request.PreferredAudio is CmafPreferredAudio.Source or CmafPreferredAudio.Auto &&
        audioTracks.Any(audio => ShouldRetryWithFallback(request, audio, diagnostics));

    /// <summary>Returns every text or detected closed-caption source that can be exposed as WebVTT without video encoding.</summary>
    public static IReadOnlyList<MediaTrackMetadata> GetSelectableSubtitles(MediaProbeResult source) =>
        source.Tracks
            .Where(track => track.Type == MediaTrackType.Subtitle && track.SubtitlePresentation == SubtitlePresentation.WebVtt)
            .OrderByDescending(track => track.IsEmbeddedClosedCaptions)
            .ThenBy(track => track.Index)
            .ToArray();

    /// <summary>Creates FFmpeg arguments for one two-second, windowed DASH muxer presentation with generated HLS playlists.</summary>
    public static IReadOnlyList<string> CreateArguments(
        AppSettings settings,
        MediaProbeResult source,
        WebPlayerTrackSelection selection,
        CmafStreamRequest request,
        string manifestPath,
        IReadOnlyDictionary<int, string>? webVttPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var audioRenditions = CreatePresentationAudioRenditions(
            source,
            selection.Audio,
            request.PreferredAudio,
            ResolveFallbackAudio(request),
            request.CompatibilityProfile,
            request.Overrides.Enabled ? request.Overrides.FallbackAudio : null,
            request.Overrides.Enabled && request.Overrides.Audio.HasValue);
        var burnIn = selection.SubtitlePresentation == SubtitlePresentation.BurnIn;
        var sourceVideo = source.Tracks.FirstOrDefault(track => track.Type == MediaTrackType.Video);
        var deinterlace = SourceDeinterlacePlanner.ShouldDeinterlace(sourceVideo, settings.WebPlayerDeinterlaceMode);
        var videoPlans = CreateVideoRenditions(sourceVideo, request, burnIn, settings.WebPlayerDeinterlaceMode);
        List<string> arguments =
        [
            "-hide_banner", "-loglevel", "info",
            "-analyzeduration", "10000000", "-probesize", "10000000",
            "-fflags", "+genpts", "-i", "pipe:0"
        ];

        if (burnIn)
        {
            var input = deinterlace
                ? $"[0:v:0]{SourceDeinterlacePlanner.CreateFilter(settings.WebPlayerDeinterlaceMode)}[deinterlaced];[deinterlaced]"
                : "[0:v:0]";
            var filter = request.Quality switch
            {
                WebPlayerQuality.Medium => $"{input}[0:{selection.Subtitle!.Index}]overlay,scale=-2:min(720\\,ih)[v]",
                WebPlayerQuality.Low => $"{input}[0:{selection.Subtitle!.Index}]overlay,scale=-2:min(480\\,ih)[v]",
                _ => $"{input}[0:{selection.Subtitle!.Index}]overlay[v]"
            };
            arguments.AddRange(["-filter_complex", filter, "-map", "[v]"]);
        }
        else
        {
            foreach (var _ in videoPlans)
            {
                arguments.AddRange(["-map", "0:v:0?"]);
            }
        }

        for (var index = 0; index < videoPlans.Count; index++)
        {
            AddVideoArguments(arguments, settings, videoPlans[index], index, request.Quality, burnIn, deinterlace);
        }
        foreach (var rendition in audioRenditions)
        {
            arguments.AddRange(["-map", $"0:{rendition.Source.Index}"]);
        }
        for (var index = 0; index < audioRenditions.Count; index++)
        {
            AddAudioArguments(arguments, audioRenditions[index], index);
        }
        arguments.AddRange([
            "-f", "dash",
            "-seg_duration", "2",
            "-frag_duration", "2",
            "-window_size", "10",
            "-extra_window_size", "5",
            "-use_template", "1",
            "-use_timeline", "1",
            "-streaming", "1",
            "-ldash", "1",
            "-hls_playlist", "1",
            "-hls_master_name", HlsManifestName,
            "-init_seg_name", "init-$RepresentationID$.mp4",
            "-media_seg_name", "chunk-$RepresentationID$-$Number%05d$.m4s",
            "-adaptation_sets", CreateAdaptationSets(videoPlans.Count, audioRenditions.Count),
            manifestPath
        ]);

        foreach (var subtitle in GetSelectableSubtitles(source).Where(track => !track.IsEmbeddedClosedCaptions))
        {
            if (webVttPaths is null || !webVttPaths.TryGetValue(subtitle.Index, out var webVttPath) || string.IsNullOrWhiteSpace(webVttPath))
            {
                throw new ArgumentException($"A contained WebVTT path is required for subtitle stream {subtitle.Index}.", nameof(webVttPaths));
            }
            arguments.AddRange(["-map", $"0:{subtitle.Index}", "-c:s", "webvtt", "-f", "webvtt", "-y", webVttPath]);
        }

        return arguments;
    }

    private static void AddAudioArguments(List<string> arguments, CmafAudioRendition rendition, int outputIndex)
    {
        var plan = rendition.Plan;
        var streamSpecifier = $":a:{outputIndex}";
        if (plan.CopySource)
        {
            arguments.AddRange([$"-c{streamSpecifier}", "copy"]);
        }
        else
        {
            arguments.AddRange([
                $"-c{streamSpecifier}", plan.Codec,
                $"-b{streamSpecifier}", $"{plan.BitRate!.Value / 1_000}k",
                $"-ar{streamSpecifier}", plan.SampleRate!.Value.ToString(),
                $"-ac{streamSpecifier}", plan.Channels!.Value.ToString()
            ]);
        }
        arguments.AddRange([$"-metadata:s{streamSpecifier}", $"title={plan.Title}"]);
        if (!string.IsNullOrWhiteSpace(rendition.Source.Language))
        {
            arguments.AddRange([$"-metadata:s{streamSpecifier}", $"language={rendition.Source.Language}"]);
        }
    }

    private static string CreateAudioTitle(MediaTrackMetadata audio, string renditionTitle)
    {
        return string.Join(
            " · ",
            new[] { $"Audio #{audio.Index}", audio.Language, audio.Title, renditionTitle }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string CreateAdaptationSets(int videoCount, int audioCount)
    {
        var videoStreams = string.Join(',', Enumerable.Range(0, videoCount));
        var adaptationSets = new List<string> { $"id=0,streams={videoStreams}" };
        adaptationSets.AddRange(
            Enumerable.Range(0, audioCount)
                .Select(index => $"id={index + 1},streams={videoCount + index}"));
        return string.Join(' ', adaptationSets);
    }

    private static string EscapeHlsAttribute(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    private static void AddVideoArguments(
        List<string> arguments,
        AppSettings settings,
        CmafVideoPlan plan,
        int outputIndex,
        WebPlayerQuality quality,
        bool burnIn,
        bool deinterlace)
    {
        var streamSpecifier = $":v:{outputIndex}";
        if (plan.CopySource)
        {
            arguments.AddRange([$"-c{streamSpecifier}", "copy", $"-metadata:s{streamSpecifier}", $"title={plan.Title}"]);
            if (string.Equals(plan.Codec, "h264", StringComparison.OrdinalIgnoreCase))
            {
                arguments.AddRange([$"-tag{streamSpecifier}", "avc1"]);
            }
            else if (string.Equals(plan.Codec, "hevc", StringComparison.OrdinalIgnoreCase))
            {
                arguments.AddRange([$"-tag{streamSpecifier}", "hvc1"]);
            }
            return;
        }

        var maximumBitRate = WebVideoTranscodePlanner.GetMaximumBitRate(settings, quality) / 1_000_000;
        var crf = quality switch
        {
            WebPlayerQuality.Medium => 23,
            WebPlayerQuality.Low => 25,
            _ => settings.WebVideoQuality
        };
        arguments.AddRange([
            $"-c{streamSpecifier}", "libx264", $"-preset{streamSpecifier}", settings.WebVideoPreset.ToString().ToLowerInvariant(),
            "-tune", "zerolatency", "-crf", crf.ToString(),
            $"-maxrate{streamSpecifier}", $"{maximumBitRate}M", $"-bufsize{streamSpecifier}", $"{maximumBitRate * 2}M",
            $"-profile{streamSpecifier}", "high", $"-level{streamSpecifier}", "4.2", $"-pix_fmt{streamSpecifier}", "yuv420p",
            "-flags", "+cgop", "-g", "120", "-keyint_min", "60", "-sc_threshold", "0",
            $"-force_key_frames{streamSpecifier}", "expr:gte(t,n_forced*2)",
            $"-metadata:s{streamSpecifier}", $"title={plan.Title}"
        ]);
        if (burnIn)
        {
            return;
        }

        List<string> filters = [];
        if (deinterlace)
        {
            filters.Add(SourceDeinterlacePlanner.CreateFilter(settings.WebPlayerDeinterlaceMode));
        }
        if (quality is WebPlayerQuality.Medium or WebPlayerQuality.Low)
        {
            filters.Add(quality == WebPlayerQuality.Medium ? "scale=-2:min(720\\,ih)" : "scale=-2:min(480\\,ih)");
        }
        if (filters.Count > 0)
        {
            arguments.AddRange([$"-filter{streamSpecifier}", string.Join(',', filters)]);
        }
    }
}
