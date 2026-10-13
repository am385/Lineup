using System.Globalization;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;

namespace Lineup.WebPlayer;

/// <summary>Identifies a media track used by provider-neutral Web Player planning.</summary>
public enum WebPlayerMediaType
{
    /// <summary>Video.</summary>
    Video,
    /// <summary>Audio.</summary>
    Audio,
    /// <summary>Subtitle or caption data.</summary>
    Subtitle
}

/// <summary>Identifies how a subtitle track can be presented.</summary>
public enum WebPlayerSubtitleMode
{
    /// <summary>The track cannot be presented.</summary>
    Unsupported,
    /// <summary>The browser renders a WebVTT sidecar.</summary>
    WebVtt,
    /// <summary>The server burns the subtitle into video.</summary>
    BurnIn
}

/// <summary>Provider-neutral media metadata required by Web Player planning.</summary>
public sealed record WebPlayerMediaTrack
{
    /// <summary>Gets the absolute source stream index.</summary>
    public required int Index { get; init; }
    /// <summary>Gets the media type.</summary>
    public required WebPlayerMediaType Type { get; init; }
    /// <summary>Gets the normalized codec name.</summary>
    public required string Codec { get; init; }
    /// <summary>Gets the channel count when known.</summary>
    public int? Channels { get; init; }
    /// <summary>Gets whether this is the source default.</summary>
    public bool IsDefault { get; init; }
    /// <summary>Gets the subtitle delivery mode.</summary>
    public WebPlayerSubtitleMode SubtitleMode { get; init; }
}

/// <summary>Describes browser capabilities trusted for one playback request.</summary>
public sealed record WebPlayerCompatibility
{
    /// <summary>Gets normalized playable video codecs.</summary>
    public IReadOnlySet<string> VideoCodecs { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Gets normalized playable audio codecs.</summary>
    public IReadOnlySet<string> AudioCodecs { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Provider-neutral playback choices supplied by a Web Player client.</summary>
public sealed record WebPlayerPlaybackRequest
{
    /// <summary>Gets the selected audio stream, or the source default when omitted.</summary>
    public int? AudioTrackIndex { get; init; }
    /// <summary>Gets the selected subtitle stream, or subtitles off when omitted.</summary>
    public int? SubtitleTrackIndex { get; init; }
    /// <summary>Gets the optional quality height cap.</summary>
    public int? MaximumHeight { get; init; }
    /// <summary>Gets measured browser compatibility.</summary>
    public WebPlayerCompatibility Compatibility { get; init; } = new();
}

/// <summary>Describes the transforms required for one Web Player CMAF output.</summary>
public sealed record WebPlayerPlaybackPlan
{
    /// <summary>Gets the selected audio track.</summary>
    public WebPlayerMediaTrack? Audio { get; init; }
    /// <summary>Gets the selected subtitle track.</summary>
    public WebPlayerMediaTrack? Subtitle { get; init; }
    /// <summary>Gets whether video must be encoded as H.264.</summary>
    public bool EncodeVideoAsH264 { get; init; }
    /// <summary>Gets whether the selected subtitle must be burned into video.</summary>
    public bool BurnInSubtitle { get; init; }
    /// <summary>Gets the output audio codec, or <c>copy</c>.</summary>
    public required string AudioCodec { get; init; }
    /// <summary>Gets the effective virtual-stage profile.</summary>
    public required VirtualTranscodeProfile EffectiveProfile { get; init; }
    /// <summary>Gets the canonical reusable transform key.</summary>
    public required StreamTransformKey TransformKey { get; init; }
}

/// <summary>Plans provider-neutral Web Player playback and reusable transform identity.</summary>
public static class WebPlayerPlaybackPlanner
{
    private static readonly HashSet<string> CmafVideoCodecs = new(StringComparer.OrdinalIgnoreCase) { "h264", "hevc" };
    private static readonly HashSet<string> CmafAudioCodecs = new(StringComparer.OrdinalIgnoreCase) { "aac", "ac3", "eac3", "ac4" };

    /// <summary>Creates a playback plan using inherited sparse virtual-device settings.</summary>
    public static WebPlayerPlaybackPlan Create(
        StreamingConfiguration configuration,
        VirtualDeviceId deviceId,
        PhysicalDeviceId sourceDeviceId,
        IReadOnlyList<WebPlayerMediaTrack> tracks,
        WebPlayerPlaybackRequest request)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(request);
        var device = configuration.VirtualDevices.SingleOrDefault(candidate => candidate.Id == deviceId)
            ?? throw new ArgumentException($"Virtual device {deviceId} was not found.", nameof(deviceId));
        if (!string.Equals(device.Adapter.Kind, WebPlayerVirtualTunerDriver.AdapterKind, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Virtual device {deviceId} is not a Web Player.", nameof(deviceId));
        }
        if (!device.Enabled)
        {
            throw new InvalidOperationException($"Web Player {deviceId} is disabled.");
        }
        if (device.Subscription.Mode == PhysicalDeviceSubscriptionMode.Selected && !device.Subscription.SelectedDeviceIds.Contains(sourceDeviceId))
        {
            throw new InvalidOperationException($"Web Player {deviceId} is not subscribed to physical device {sourceDeviceId}.");
        }

        var effective = device.TranscodeOverrides.Apply(configuration.VirtualDefaults);
        var video = tracks.FirstOrDefault(track => track.Type == WebPlayerMediaType.Video);
        var audioTracks = tracks.Where(track => track.Type == WebPlayerMediaType.Audio).ToArray();
        var audio = request.AudioTrackIndex.HasValue
            ? FindRequired(tracks, request.AudioTrackIndex.Value, WebPlayerMediaType.Audio)
            : audioTracks.FirstOrDefault(track => track.IsDefault) ?? audioTracks.FirstOrDefault();
        var subtitle = request.SubtitleTrackIndex.HasValue
            ? FindRequired(tracks, request.SubtitleTrackIndex.Value, WebPlayerMediaType.Subtitle)
            : null;
        if (subtitle?.SubtitleMode == WebPlayerSubtitleMode.Unsupported)
        {
            throw new ArgumentException($"Subtitle stream index {subtitle.Index} is not supported.", nameof(request));
        }

        var burnIn = subtitle?.SubtitleMode == WebPlayerSubtitleMode.BurnIn;
        var encodeVideo = video is not null &&
            (burnIn ||
             request.MaximumHeight.HasValue ||
             !CmafVideoCodecs.Contains(video.Codec) ||
             effective.ConvertHevcToH264 && string.Equals(video.Codec, "hevc", StringComparison.OrdinalIgnoreCase) ||
             request.Compatibility.VideoCodecs.Count > 0 && !request.Compatibility.VideoCodecs.Contains(video.Codec));
        var audioCodec = ResolveAudioCodec(audio, effective, request.Compatibility);
        var settingsKey = string.Join(
            '|',
            $"v={(encodeVideo ? "h264" : "copy")}",
            $"a={audioCodec}",
            $"deinterlace={effective.Deinterlace}",
            $"preset={effective.VideoPreset}",
            $"quality={effective.VideoQuality.ToString(CultureInfo.InvariantCulture)}",
            $"br={effective.MaximumVideoBitRateMbps.ToString(CultureInfo.InvariantCulture)}",
            $"height={request.MaximumHeight?.ToString(CultureInfo.InvariantCulture) ?? "source"}",
            $"audio={audio?.Index.ToString(CultureInfo.InvariantCulture) ?? "none"}",
            $"subtitle={subtitle?.Index.ToString(CultureInfo.InvariantCulture) ?? "none"}",
            $"subtitleMode={subtitle?.SubtitleMode.ToString() ?? "off"}");
        return new WebPlayerPlaybackPlan
        {
            Audio = audio,
            Subtitle = subtitle,
            EncodeVideoAsH264 = encodeVideo,
            BurnInSubtitle = burnIn,
            AudioCodec = audioCodec,
            EffectiveProfile = effective,
            TransformKey = new StreamTransformKey("web-player-cmaf", settingsKey)
        };
    }

    private static string ResolveAudioCodec(WebPlayerMediaTrack? audio, VirtualTranscodeProfile profile, WebPlayerCompatibility compatibility)
    {
        if (audio is null)
        {
            return "none";
        }
        var policy = string.Equals(audio.Codec, "ac4", StringComparison.OrdinalIgnoreCase) ? profile.Ac4Audio : profile.Audio;
        if (policy == OutputAudioPolicy.Ac3)
        {
            return "ac3";
        }
        if (policy == OutputAudioPolicy.Eac3)
        {
            return "eac3";
        }
        return CmafAudioCodecs.Contains(audio.Codec) &&
            (compatibility.AudioCodecs.Count == 0 || compatibility.AudioCodecs.Contains(audio.Codec))
                ? "copy"
                : "aac";
    }

    private static WebPlayerMediaTrack FindRequired(IReadOnlyList<WebPlayerMediaTrack> tracks, int index, WebPlayerMediaType type)
    {
        var track = tracks.FirstOrDefault(candidate => candidate.Index == index)
            ?? throw new ArgumentException($"Source stream index {index} was not found.");
        return track.Type == type
            ? track
            : throw new ArgumentException($"Source stream index {index} is {track.Type}, not {type}.");
    }
}
