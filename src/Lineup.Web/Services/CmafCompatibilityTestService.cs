using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace Lineup.Web.Services;

/// <summary>
/// Identifies a synthetic video codec offered by Watch Test.
/// </summary>
public enum CmafTestVideoCodec
{
    /// <summary>H.264/AVC encoded by libx264.</summary>
    H264,

    /// <summary>HEVC encoded by libx265 and tagged for browser MP4 playback.</summary>
    Hevc
}

/// <summary>
/// Identifies a synthetic audio codec offered by Watch Test.
/// </summary>
public enum CmafTestAudioCodec
{
    /// <summary>Advanced Audio Coding.</summary>
    Aac,

    /// <summary>Dolby Digital.</summary>
    Ac3,

    /// <summary>Dolby Digital Plus.</summary>
    Eac3,

    /// <summary>Dolby AC-4, which the bundled FFmpeg can decode but cannot encode.</summary>
    Ac4
}

/// <summary>
/// Identifies a standard audio channel layout offered by Watch Test.
/// </summary>
public enum CmafTestChannelLayout
{
    /// <summary>One center channel.</summary>
    Mono,

    /// <summary>Left and right channels.</summary>
    Stereo,

    /// <summary>Left, right, and center channels.</summary>
    Surround3Point0,

    /// <summary>Four-channel quadraphonic audio.</summary>
    Quad,

    /// <summary>Five full-range surround channels.</summary>
    Surround5Point0,

    /// <summary>Five full-range channels plus LFE.</summary>
    Surround5Point1,

    /// <summary>Seven full-range channels plus LFE.</summary>
    Surround7Point1
}

/// <summary>
/// Configures one exact synthetic CMAF compatibility presentation.
/// </summary>
public sealed record CmafCompatibilityTestRequest
{
    /// <summary>Gets the protocol the browser will test.</summary>
    public CmafProtocol Protocol { get; init; } = CmafProtocol.Dash;

    /// <summary>Gets the production quality profile used by the synthetic encoder.</summary>
    public WebPlayerQuality Quality { get; init; } = WebPlayerQuality.AppDefault;

    /// <summary>Gets the only video codec included in the presentation.</summary>
    public CmafTestVideoCodec VideoCodec { get; init; } = CmafTestVideoCodec.H264;

    /// <summary>Gets the exact synthetic video profile.</summary>
    public CmafTestVideoProfile VideoProfile { get; init; } = CmafTestVideoProfile.Main;

    /// <summary>Gets the only audio codec included in the presentation.</summary>
    public CmafTestAudioCodec AudioCodec { get; init; } = CmafTestAudioCodec.Aac;

    /// <summary>Gets the generated audio channel layout.</summary>
    public CmafTestChannelLayout ChannelLayout { get; init; } = CmafTestChannelLayout.Stereo;

    /// <summary>Gets the exact synthetic subtitle presentation.</summary>
    public CmafTestSubtitleMode SubtitleMode { get; init; }
}

/// <summary>
/// Describes one started synthetic CMAF compatibility presentation.
/// </summary>
/// <param name="SessionId">The transient session identifier.</param>
/// <param name="ManifestUrl">The exact manifest selected for this test.</param>
/// <param name="VideoCodec">The expected RFC 6381 video codec string.</param>
/// <param name="AudioCodec">The expected RFC 6381 audio codec string.</param>
/// <param name="Channels">The generated channel count.</param>
/// <param name="Width">The generated video width.</param>
/// <param name="Height">The generated video height.</param>
/// <param name="SubtitleUrl">The optional WebVTT sidecar URL.</param>
public sealed record CmafCompatibilityTestResponse(
    string SessionId,
    string ManifestUrl,
    string VideoCodec,
    string AudioCodec,
    int Channels,
    int Width,
    int Height,
    string? SubtitleUrl = null);

/// <summary>
/// Starts deterministic synthetic media used by the Watch Test page.
/// </summary>
public interface ICmafCompatibilityTestService
{
    /// <summary>Starts FFmpeg for one validated compatibility test.</summary>
    Process Start(AppSettings settings, CmafCompatibilityTestRequest request, string manifestPath);
}

/// <summary>
/// Uses bundled FFmpeg encoders to generate exact single-rendition CMAF tests.
/// </summary>
public sealed class CmafCompatibilityTestService : ICmafCompatibilityTestService
{
    /// <inheritdoc />
    public Process Start(AppSettings settings, CmafCompatibilityTestRequest request, string manifestPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            WorkingDirectory = Path.GetDirectoryName(manifestPath) ?? string.Empty,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in CmafCompatibilityTestPlanner.CreateArguments(settings, request, manifestPath))
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Watch Test FFmpeg.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"Watch Test FFmpeg could not be started: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// Validates compatibility-test selections and builds deterministic FFmpeg arguments.
/// </summary>
public static class CmafCompatibilityTestPlanner
{
    private const double ChannelToneSeconds = 0.75;
    private static readonly TimeSpan SubtitleDuration = TimeSpan.FromMinutes(10);
    private static readonly IReadOnlyDictionary<CmafTestChannelLayout, (string Name, string[] Labels)> Layouts =
        new Dictionary<CmafTestChannelLayout, (string Name, string[] Labels)>
        {
            [CmafTestChannelLayout.Mono] = ("mono", ["Mono"]),
            [CmafTestChannelLayout.Stereo] = ("stereo", ["Front Left", "Front Right"]),
            [CmafTestChannelLayout.Surround3Point0] = ("3.0", ["Front Left", "Front Right", "Front Center"]),
            [CmafTestChannelLayout.Quad] = ("quad", ["Front Left", "Front Right", "Back Left", "Back Right"]),
            [CmafTestChannelLayout.Surround5Point0] = ("5.0", ["Front Left", "Front Right", "Front Center", "Back Left", "Back Right"]),
            [CmafTestChannelLayout.Surround5Point1] = ("5.1", ["Front Left", "Front Right", "Front Center", "LFE", "Back Left", "Back Right"]),
            [CmafTestChannelLayout.Surround7Point1] = ("7.1", ["Front Left", "Front Right", "Front Center", "LFE", "Back Left", "Back Right", "Side Left", "Side Right"])
        };

    /// <summary>Returns whether the bundled FFmpeg can encode the requested audio codec.</summary>
    public static bool IsEncoderAvailable(CmafTestAudioCodec codec) => codec != CmafTestAudioCodec.Ac4;

    /// <summary>Returns layouts supported by the bundled encoder for the selected codec.</summary>
    public static IReadOnlyList<CmafTestChannelLayout> GetSupportedLayouts(CmafTestAudioCodec codec) =>
        codec switch
        {
            CmafTestAudioCodec.Aac => Enum.GetValues<CmafTestChannelLayout>(),
            CmafTestAudioCodec.Ac3 or CmafTestAudioCodec.Eac3 => Enum.GetValues<CmafTestChannelLayout>()
                .Where(layout => GetChannelCount(layout) <= 6)
                .ToArray(),
            CmafTestAudioCodec.Ac4 => [],
            _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "Unsupported Watch Test audio codec.")
        };

    /// <summary>Returns the channel count represented by a test layout.</summary>
    public static int GetChannelCount(CmafTestChannelLayout layout) =>
        Layouts.TryGetValue(layout, out var value)
            ? value.Labels.Length
            : throw new ArgumentOutOfRangeException(nameof(layout), layout, "Unsupported Watch Test channel layout.");

    /// <summary>Returns the channel activation order for a test layout.</summary>
    public static IReadOnlyList<string> GetChannelLabels(CmafTestChannelLayout layout) =>
        Layouts.TryGetValue(layout, out var value)
            ? value.Labels
            : throw new ArgumentOutOfRangeException(nameof(layout), layout, "Unsupported Watch Test channel layout.");

    /// <summary>Creates synchronized channel-name cues for the selected test layout.</summary>
    public static string CreateChannelSubtitleWebVtt(CmafTestChannelLayout layout)
    {
        var labels = GetChannelLabels(layout);
        var builder = new StringBuilder("WEBVTT\n\n");
        for (var second = 0; second < (int)SubtitleDuration.TotalSeconds; second++)
        {
            var start = TimeSpan.FromSeconds(second);
            var end = start.Add(TimeSpan.FromSeconds(ChannelToneSeconds));
            builder.Append(FormatWebVttTimestamp(start))
                .Append(" --> ")
                .Append(FormatWebVttTimestamp(end))
                .Append('\n')
                .Append(labels[second % labels.Count])
                .Append("\n\n");
        }

        return builder.ToString();
    }

    /// <summary>Returns the expected browser codec identifier for the selected video encoder.</summary>
    public static string GetVideoCodecString(CmafTestVideoCodec codec) =>
        codec switch
        {
            CmafTestVideoCodec.H264 => "avc1.64002a",
            CmafTestVideoCodec.Hevc => "hvc1.1.6.L120",
            _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "Unsupported Watch Test video codec.")
        };

    /// <summary>Returns the expected browser codec identifier for the selected video codec and profile.</summary>
    public static string GetVideoCodecString(CmafTestVideoCodec codec, CmafTestVideoProfile profile) =>
        (codec, profile) switch
        {
            (CmafTestVideoCodec.H264, CmafTestVideoProfile.Main) => "avc1.64002a",
            (CmafTestVideoCodec.Hevc, CmafTestVideoProfile.Main) => "hvc1.1.6.L120",
            (CmafTestVideoCodec.Hevc, CmafTestVideoProfile.Main10) => "hvc1.2.4.L120",
            (CmafTestVideoCodec.H264, CmafTestVideoProfile.Main10) => throw new ArgumentException("H.264 Main 10 is not a supported Watch Test profile.", nameof(profile)),
            _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "Unsupported Watch Test video codec.")
        };

    /// <summary>Returns the expected browser codec identifier for the selected audio encoder.</summary>
    public static string GetAudioCodecString(CmafTestAudioCodec codec) =>
        codec switch
        {
            CmafTestAudioCodec.Aac => "mp4a.40.2",
            CmafTestAudioCodec.Ac3 => "ac-3",
            CmafTestAudioCodec.Eac3 => "ec-3",
            CmafTestAudioCodec.Ac4 => "ac-4",
            _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "Unsupported Watch Test audio codec.")
        };

    /// <summary>Reads the actual video and audio RFC 6381 codec strings emitted by FFmpeg.</summary>
    public static (string VideoCodec, string AudioCodec) ParseManifestCodecs(string manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest);
        var document = XDocument.Parse(manifest);
        var representations = document.Descendants().Where(element => element.Name.LocalName == "Representation").ToArray();
        var videoCodec = GetRepresentationCodec(representations, "video/mp4");
        var audioCodec = GetRepresentationCodec(representations, "audio/mp4");
        return (videoCodec, audioCodec);
    }

    /// <summary>Returns the deterministic synthetic resolution for a production quality profile.</summary>
    public static (int Width, int Height) GetResolution(WebPlayerQuality quality) =>
        quality switch
        {
            WebPlayerQuality.AppDefault or WebPlayerQuality.High => (1920, 1080),
            WebPlayerQuality.Medium => (1280, 720),
            WebPlayerQuality.Low => (854, 480),
            _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, "Unsupported Watch Test quality.")
        };

    /// <summary>Creates one-video, one-audio DASH/HLS CMAF encoder command without fallback renditions.</summary>
    public static IReadOnlyList<string> CreateArguments(AppSettings settings, CmafCompatibilityTestRequest request, string manifestPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ValidateRequest(request);
        var (width, height) = GetResolution(request.Quality);
        var layout = Layouts[request.ChannelLayout];
        var channelCount = layout.Labels.Length;
        var maximumBitRateMbps = Math.Max(1, WebVideoTranscodePlanner.GetMaximumBitRate(settings, request.Quality) / 1_000_000);
        var crf = request.Quality switch
        {
            WebPlayerQuality.Medium => 23,
            WebPlayerQuality.Low => 25,
            _ => settings.WebVideoQuality
        };
        var videoEncoder = request.VideoCodec == CmafTestVideoCodec.H264 ? "libx264" : "libx265";
        var audioEncoder = request.AudioCodec switch
        {
            CmafTestAudioCodec.Aac => "aac",
            CmafTestAudioCodec.Ac3 => "ac3",
            CmafTestAudioCodec.Eac3 => "eac3",
            _ => throw new NotSupportedException("The bundled FFmpeg does not include an AC-4 encoder.")
        };
        var audioBitRate = request.AudioCodec switch
        {
            CmafTestAudioCodec.Ac3 => 448_000,
            CmafTestAudioCodec.Eac3 => 640_000,
            _ => Math.Max(channelCount, 2) * 64_000
        };
        var title = $"Lineup Watch Test - {request.VideoCodec} + {request.AudioCodec} {channelCount}ch";
        List<string> arguments =
        [
            "-hide_banner", "-loglevel", "info",
            "-re", "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate=30",
            "-re", "-f", "lavfi", "-i", CreateChannelToneSource(layout.Name, layout.Labels),
            "-map", "0:v:0", "-map", "1:a:0",
            "-metadata:s:v:0", $"title={title}",
            "-metadata:s:a:0", $"title={request.AudioCodec} {channelCount}ch",
            "-c:v", videoEncoder,
            "-preset", settings.WebVideoPreset.ToString().ToLowerInvariant(),
            "-crf", crf.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-maxrate", $"{maximumBitRateMbps}M",
            "-bufsize", $"{maximumBitRateMbps * 2}M",
            "-pix_fmt", request.VideoProfile == CmafTestVideoProfile.Main10 ? "yuv420p10le" : "yuv420p",
            "-flags", "+cgop",
            "-g", "60",
            "-keyint_min", "60",
            "-sc_threshold", "0"
        ];
        if (request.VideoCodec == CmafTestVideoCodec.H264)
        {
            arguments.AddRange(["-profile:v", "high", "-level:v", "4.2"]);
        }
        else
        {
            var profile = request.VideoProfile == CmafTestVideoProfile.Main10 ? "main10" : "main";
            arguments.AddRange(["-tag:v", "hvc1", "-profile:v", profile, "-x265-params", "repeat-headers=1:open-gop=0:keyint=60:min-keyint=60:scenecut=0"]);
        }
        if (request.SubtitleMode == CmafTestSubtitleMode.BurnIn)
        {
            arguments.AddRange(["-vf", CreateChannelBurnInFilter(layout.Labels)]);
        }
        arguments.AddRange([
            "-c:a", audioEncoder,
            "-b:a", audioBitRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-ar", "48000",
            "-ac", channelCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-t", SubtitleDuration.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
            "-hls_master_name", CmafStreamPlanner.HlsManifestName,
            "-init_seg_name", "init-$RepresentationID$.mp4",
            "-media_seg_name", "chunk-$RepresentationID$-$Number%05d$.m4s",
            "-adaptation_sets", "id=0,streams=v id=1,streams=a",
            manifestPath
        ]);
        return arguments;
    }

    private static string CreateChannelToneSource(string layoutName, IReadOnlyList<string> labels)
    {
        var cycleSeconds = labels.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var expressions = labels.Select((label, index) =>
        {
            var start = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var end = (index + ChannelToneSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var frequency = string.Equals(label, "LFE", StringComparison.Ordinal) ? "80" : "440";
            return $"0.08*sin(2*PI*{frequency}*t)*between(mod(t\\,{cycleSeconds})\\,{start}\\,{end})";
        });
        return $"aevalsrc={string.Join('|', expressions)}:s=48000:c={layoutName}";
    }

    private static string CreateChannelBurnInFilter(IReadOnlyList<string> labels)
    {
        var fontPath = OperatingSystem.IsWindows()
            ? "C\\:/Windows/Fonts/arial.ttf"
            : OperatingSystem.IsMacOS()
                ? "/System/Library/Fonts/Supplemental/Arial.ttf"
                : "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
        var cycleSeconds = labels.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return string.Join(
            ',',
            labels.Select((label, index) =>
            {
                var start = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var end = (index + ChannelToneSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return $"drawtext=fontfile='{fontPath}':text='{label}':fontcolor=white:fontsize=52:box=1:boxcolor=black@0.8:boxborderw=20:x=(w-text_w)/2:y=h*0.8:enable='between(mod(t\\,{cycleSeconds})\\,{start}\\,{end})'";
            }));
    }

    private static string FormatWebVttTimestamp(TimeSpan value) =>
        value.ToString(@"hh\:mm\:ss\.fff", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Validates a compatibility-test request without starting FFmpeg.</summary>
    public static void ValidateRequest(CmafCompatibilityTestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Protocol))
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Protocol, "Unsupported Watch Test protocol.");
        }
        _ = GetResolution(request.Quality);
        _ = GetVideoCodecString(request.VideoCodec, request.VideoProfile);
        _ = GetAudioCodecString(request.AudioCodec);
        if (!Enum.IsDefined(request.SubtitleMode))
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.SubtitleMode, "Unsupported Watch Test subtitle mode.");
        }
        if (!IsEncoderAvailable(request.AudioCodec))
        {
            throw new NotSupportedException("The bundled FFmpeg can decode AC-4 but does not include an AC-4 encoder for a synthetic playback test.");
        }
        if (!GetSupportedLayouts(request.AudioCodec).Contains(request.ChannelLayout))
        {
            throw new ArgumentException($"{request.ChannelLayout} is not supported by the bundled {request.AudioCodec} encoder.", nameof(request));
        }
    }

    private static string GetRepresentationCodec(IEnumerable<XElement> representations, string mimeType)
    {
        var codec = representations
            .FirstOrDefault(element => string.Equals((string?)element.Attribute("mimeType"), mimeType, StringComparison.OrdinalIgnoreCase))
            ?.Attribute("codecs")
            ?.Value;
        return string.IsNullOrWhiteSpace(codec)
            ? throw new InvalidDataException($"Watch Test manifest did not contain a {mimeType} codec.")
            : codec;
    }
}
