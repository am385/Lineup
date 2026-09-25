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
    Aac = Fallback
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
/// Configures creation of one shared CMAF DASH/HLS presentation.
/// </summary>
public sealed record CmafStreamRequest
{
    /// <summary>Gets the selected video quality.</summary>
    public WebPlayerQuality Quality { get; init; } = WebPlayerQuality.AppDefault;

    /// <summary>Gets the absolute selected source audio stream index.</summary>
    public int? AudioTrack { get; init; }

    /// <summary>Gets the absolute selected source subtitle stream index, or <see langword="null"/> for subtitles off.</summary>
    public int? SubtitleTrack { get; init; }

    /// <summary>Gets the requested source-audio or fallback policy.</summary>
    public CmafPreferredAudio PreferredAudio { get; init; } = CmafPreferredAudio.Source;

    /// <summary>Gets the codec and channel limit used when source audio is not CMAF-copy eligible.</summary>
    public CmafFallbackAudio? FallbackAudio { get; init; }

    /// <summary>Gets the legacy AAC-only fallback setting retained for request compatibility.</summary>
    public WatchAudioOutput? AacFallback { get; init; }

    /// <summary>Gets the previously validated subtitle presentation used after an incomplete retry probe.</summary>
    public SubtitlePresentation? SubtitlePresentation { get; init; }

    /// <summary>Gets whether the selected retry subtitle represents embedded captions.</summary>
    public bool EmbeddedCaptions { get; init; }

    /// <summary>Gets the optional owning web-player identifier.</summary>
    public string? ClientId { get; init; }
}

/// <summary>
/// Describes a started shared CMAF presentation.
/// </summary>
/// <param name="SessionId">The stream session identifier.</param>
/// <param name="HlsManifestUrl">The shared HLS master-playlist URL.</param>
/// <param name="HlsCompatibilityManifestUrl">The compatibility alias for the HLS master playlist.</param>
/// <param name="DashManifestUrl">The DASH MPD URL.</param>
public sealed record CmafStreamResponse(string SessionId, string HlsManifestUrl, string HlsCompatibilityManifestUrl, string DashManifestUrl)
{
    /// <summary>Gets the protocols backed by the shared fragments.</summary>
    public IReadOnlyList<CmafProtocol> Protocols { get; init; } = [CmafProtocol.Dash, CmafProtocol.Hls];

    /// <summary>Gets the legacy HLS response property retained for compatibility.</summary>
    public string PlaylistUrl => HlsManifestUrl;

    /// <summary>Gets whether an unsupported source-audio MP4 tag required a fallback-only startup retry.</summary>
    public bool SourceAudioFallbackApplied { get; init; }

    /// <summary>Gets the configured fallback rendition title.</summary>
    public string? FallbackAudioTitle { get; init; }

    /// <summary>Gets browser-selectable text and closed-caption sidecars prepared for this session.</summary>
    public IReadOnlyList<CmafSubtitleRendition> Subtitles { get; init; } = [];
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
/// Builds the single FFmpeg DASH muxer presentation shared by DASH and HLS clients.
/// </summary>
public static class CmafStreamPlanner
{
    private static readonly HashSet<string> CopyEligibleAudioCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "aac", "ac3", "eac3", "ac4"
    };

    /// <summary>Gets the DASH manifest filename.</summary>
    public const string DashManifestName = "manifest.mpd";

    /// <summary>Gets the HLS master-playlist filename emitted by the DASH muxer.</summary>
    public const string HlsManifestName = "master.m3u8";

    /// <summary>Returns whether the selected source audio codec can be copied into fragmented MP4.</summary>
    public static bool CanCopySourceAudio(string? codec) => codec is not null && CopyEligibleAudioCodecs.Contains(codec);

    /// <summary>Validates selected CMAF tracks with the same selection and caption-extraction policy as Watch.</summary>
    public static WatchTrackSelection SelectTracks(MediaProbeResult source, CmafStreamRequest request)
    {
        return WatchStreamPlanner.SelectTracks(source, request.AudioTrack, request.SubtitleTrack, request.SubtitlePresentation, request.EmbeddedCaptions);
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
            null or WatchAudioOutput.Stereo => CmafFallbackAudio.AacStereo,
            WatchAudioOutput.UpTo5Point1 => CmafFallbackAudio.AacUpTo5Point1,
            WatchAudioOutput.UpTo7Point1 => CmafFallbackAudio.AacUpTo7Point1,
            WatchAudioOutput.Source => throw new ArgumentException("CMAF fallback audio cannot use source passthrough.", nameof(request)),
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

        if (preferredAudio == CmafPreferredAudio.Source && CanCopySourceAudio(audio?.Codec))
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

    /// <summary>Creates the audio renditions packaged into one shared CMAF presentation.</summary>
    public static IReadOnlyList<CmafAudioPlan> CreateAudioRenditions(MediaTrackMetadata? audio, CmafPreferredAudio preferredAudio, CmafFallbackAudio fallback)
    {
        var fallbackPlan = CreateAudioPlan(audio, CmafPreferredAudio.Fallback, fallback);
        if (preferredAudio != CmafPreferredAudio.Source || !CanCopySourceAudio(audio?.Codec))
        {
            return [fallbackPlan];
        }

        return [CreateAudioPlan(audio, CmafPreferredAudio.Source, fallback), fallbackPlan];
    }

    /// <summary>Returns whether startup diagnostics identify an unsupported source-audio MP4 muxer tag that can be retried with fallback audio.</summary>
    public static bool ShouldRetryWithFallback(CmafStreamRequest request, MediaTrackMetadata? audio, IEnumerable<string> diagnostics)
    {
        if (request.PreferredAudio != CmafPreferredAudio.Source || !CanCopySourceAudio(audio?.Codec))
        {
            return false;
        }

        var codecDiagnostic = $"codec {audio!.Codec}";
        return diagnostics.Any(line =>
            line.Contains(codecDiagnostic, StringComparison.OrdinalIgnoreCase) &&
            line.Contains("not currently supported in container", StringComparison.OrdinalIgnoreCase));
    }

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
        WatchTrackSelection selection,
        CmafStreamRequest request,
        string manifestPath,
        IReadOnlyDictionary<int, string>? webVttPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var audioPlans = CreateAudioRenditions(selection.Audio, request.PreferredAudio, ResolveFallbackAudio(request));
        var burnIn = selection.SubtitlePresentation == SubtitlePresentation.BurnIn;
        List<string> arguments =
        [
            "-hide_banner", "-loglevel", "info",
            "-analyzeduration", "1000000", "-probesize", "1000000",
            "-fflags", "+genpts", "-i", "pipe:0"
        ];

        if (burnIn)
        {
            var filter = request.Quality switch
            {
                WebPlayerQuality.Medium => $"[0:v:0][0:{selection.Subtitle!.Index}]overlay,scale=-2:min(720\\,ih)[v]",
                WebPlayerQuality.Low => $"[0:v:0][0:{selection.Subtitle!.Index}]overlay,scale=-2:min(480\\,ih)[v]",
                _ => $"[0:v:0][0:{selection.Subtitle!.Index}]overlay[v]"
            };
            arguments.AddRange(["-filter_complex", filter, "-map", "[v]"]);
        }
        else
        {
            arguments.AddRange(["-map", "0:v:0?"]);
        }

        AddVideoArguments(arguments, settings, source, request.Quality, burnIn);
        foreach (var _ in audioPlans)
        {
            arguments.AddRange(selection.Audio is not null ? ["-map", $"0:{selection.Audio.Index}"] : ["-map", "0:a:0?"]);
        }
        for (var index = 0; index < audioPlans.Count; index++)
        {
            AddAudioArguments(arguments, audioPlans[index], index);
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
            "-adaptation_sets", "id=0,streams=v id=1,streams=a",
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

    private static void AddAudioArguments(List<string> arguments, CmafAudioPlan plan, int outputIndex)
    {
        var streamSpecifier = $":a:{outputIndex}";
        if (plan.CopySource)
        {
            arguments.AddRange([$"-c{streamSpecifier}", "copy", $"-metadata:s{streamSpecifier}", "title=Source"]);
            return;
        }

        arguments.AddRange([
            $"-c{streamSpecifier}", plan.Codec,
            $"-b{streamSpecifier}", $"{plan.BitRate!.Value / 1_000}k",
            $"-ar{streamSpecifier}", plan.SampleRate!.Value.ToString(),
            $"-ac{streamSpecifier}", plan.Channels!.Value.ToString(),
            $"-metadata:s{streamSpecifier}", $"title={plan.Title}"
        ]);
    }

    private static void AddVideoArguments(List<string> arguments, AppSettings settings, MediaProbeResult source, WebPlayerQuality quality, bool burnIn)
    {
        var codec = source.Tracks.FirstOrDefault(track => track.Type == MediaTrackType.Video)?.Codec;
        if (!burnIn && quality == WebPlayerQuality.AppDefault && string.Equals(codec, "h264", StringComparison.OrdinalIgnoreCase))
        {
            arguments.AddRange(["-c:v", "copy"]);
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
            "-c:v", "libx264", "-preset", settings.WebVideoPreset.ToString().ToLowerInvariant(),
            "-tune", "zerolatency", "-crf", crf.ToString(),
            "-maxrate", $"{maximumBitRate}M", "-bufsize", $"{maximumBitRate * 2}M",
            "-profile:v", "high", "-level", "4.2", "-pix_fmt", "yuv420p",
            "-flags", "+cgop", "-g", "120", "-keyint_min", "60", "-sc_threshold", "0",
            "-force_key_frames", "expr:gte(t,n_forced*2)"
        ]);
        if (!burnIn && quality is (WebPlayerQuality.Medium or WebPlayerQuality.Low))
        {
            arguments.AddRange(["-vf", quality == WebPlayerQuality.Medium ? "scale=-2:min(720\\,ih)" : "scale=-2:min(480\\,ih)"]);
        }
    }
}
