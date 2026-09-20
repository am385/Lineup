using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lineup.Web.Services;

/// <summary>
/// Identifies a hosted stream's output container.
/// </summary>
public enum HostedStreamFormat
{
    /// <summary>
    /// MPEG transport stream output.
    /// </summary>
    MpegTs,

    /// <summary>
    /// Fragmented MP4 output.
    /// </summary>
    FragmentedMp4,

    /// <summary>
    /// HTTP Live Streaming output.
    /// </summary>
    Hls
}

/// <summary>
/// Identifies a media track type.
/// </summary>
public enum MediaTrackType
{
    /// <summary>
    /// Video track.
    /// </summary>
    Video,

    /// <summary>
    /// Audio track.
    /// </summary>
    Audio,

    /// <summary>
    /// Subtitle or caption track.
    /// </summary>
    Subtitle
}

/// <summary>
/// Identifies how a subtitle can be presented by Watch.
/// </summary>
public enum SubtitlePresentation
{
    /// <summary>The codec cannot be delivered by Watch.</summary>
    Unsupported,
    /// <summary>The codec can be converted to browser WebVTT.</summary>
    WebVtt,
    /// <summary>The bitmap codec must be burned into video.</summary>
    BurnIn
}

/// <summary>
/// Describes a media track reported by FFprobe.
/// </summary>
/// <param name="Index">The input stream index.</param>
/// <param name="Type">The media track type.</param>
/// <param name="Codec">The FFmpeg codec name.</param>
/// <param name="BitRate">The reported source bitrate in bits per second.</param>
/// <param name="Width">The video width in pixels.</param>
/// <param name="Height">The video height in pixels.</param>
/// <param name="Channels">The audio channel count.</param>
/// <param name="SampleRate">The audio sample rate in hertz.</param>
public sealed partial record MediaTrackMetadata(int Index, MediaTrackType Type, string Codec, long? BitRate, int? Width, int? Height, int? Channels, int? SampleRate);
public sealed partial record MediaTrackMetadata
{
    /// <summary>Gets the ISO language tag reported by the source.</summary>
    public string? Language { get; init; }
    /// <summary>Gets the source track title.</summary>
    public string? Title { get; init; }
    /// <summary>Gets whether the source marks this track as default.</summary>
    public bool IsDefault { get; init; }
    /// <summary>Gets whether the source marks this track as forced.</summary>
    public bool IsForced { get; init; }
    /// <summary>Gets whether the source marks this track for hearing-impaired viewers.</summary>
    public bool IsHearingImpaired { get; init; }
    /// <summary>Gets whether a video track reports embedded closed captions.</summary>
    public bool HasClosedCaptions { get; init; }
    /// <summary>Gets the supported Watch subtitle presentation.</summary>
    public SubtitlePresentation SubtitlePresentation { get; init; }
}

/// <summary>
/// Describes all media tracks and the overall source bitrate reported by FFprobe.
/// </summary>
/// <param name="Tracks">The discovered video and audio tracks.</param>
/// <param name="BitRate">The reported overall source bitrate in bits per second.</param>
public sealed record MediaProbeResult(IReadOnlyList<MediaTrackMetadata> Tracks, long? BitRate);

/// <summary>
/// Centralizes subtitle codec support for proxy and browser outputs.
/// </summary>
public static class SubtitleCapabilityPolicy
{
    private static readonly HashSet<string> TextCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ass", "ssa", "subrip", "text", "webvtt", "mov_text", "eia_608", "eia_708"
    };
    private static readonly HashSet<string> BitmapCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "dvb_subtitle", "dvd_subtitle", "hdmv_pgs_subtitle", "xsub"
    };
    private static readonly HashSet<string> MpegTsCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "dvb_subtitle", "dvb_teletext"
    };

    /// <summary>Classifies a subtitle codec for Watch presentation.</summary>
    public static SubtitlePresentation Classify(string? codec) =>
        codec is not null && TextCodecs.Contains(codec)
            ? SubtitlePresentation.WebVtt
            : codec is not null && BitmapCodecs.Contains(codec)
                ? SubtitlePresentation.BurnIn
                : SubtitlePresentation.Unsupported;

    /// <summary>Returns whether MPEG-TS can safely copy a standalone subtitle codec.</summary>
    public static bool CanCopyToMpegTs(string? codec) => codec is not null && MpegTsCodecs.Contains(codec);
}

/// <summary>
/// Describes how one source track is delivered to a client.
/// </summary>
/// <param name="Type">The media track type.</param>
/// <param name="SourceCodec">The source codec name.</param>
/// <param name="OutputCodec">The output codec name, or <c>copy</c> when unchanged.</param>
/// <param name="SourceBitRate">The reported source bitrate in bits per second.</param>
/// <param name="OutputBitRate">The configured output target bitrate in bits per second.</param>
/// <param name="SourceWidth">The source video width in pixels.</param>
/// <param name="SourceHeight">The source video height in pixels.</param>
/// <param name="SourceChannels">The source audio channel count.</param>
/// <param name="OutputChannels">The output audio channel count.</param>
/// <param name="OutputSampleRate">The configured output audio sample rate in hertz.</param>
public sealed partial record ActiveStreamTrack(
    MediaTrackType Type,
    string SourceCodec,
    string OutputCodec,
    long? SourceBitRate,
    long? OutputBitRate,
    int? SourceWidth,
    int? SourceHeight,
    int? SourceChannels,
    int? OutputChannels,
    int? OutputSampleRate);
public sealed partial record ActiveStreamTrack
{
    /// <summary>Gets the absolute source stream index.</summary>
    public int SourceIndex { get; init; }
    /// <summary>Gets the source language tag.</summary>
    public string? Language { get; init; }
    /// <summary>Gets the source title.</summary>
    public string? Title { get; init; }
    /// <summary>Gets whether the source marks this track as default.</summary>
    public bool IsDefault { get; init; }
    /// <summary>Gets whether the source marks this track as forced.</summary>
    public bool IsForced { get; init; }
    /// <summary>Gets whether the source marks this track for hearing-impaired viewers.</summary>
    public bool IsHearingImpaired { get; init; }
    /// <summary>Gets whether the source reports embedded closed captions.</summary>
    public bool HasClosedCaptions { get; init; }
    /// <summary>Gets whether this track is selected in the output.</summary>
    public bool IsSelected { get; init; } = true;
    /// <summary>Gets the subtitle presentation used for this output.</summary>
    public SubtitlePresentation? SubtitlePresentation { get; init; }
}

/// <summary>
/// Represents an active stream hosted by Lineup.
/// </summary>
/// <param name="SessionId">The unique runtime session identifier.</param>
/// <param name="Channel">The virtual channel number.</param>
/// <param name="Format">The hosted output format.</param>
/// <param name="StartedAtUtc">The UTC stream start time.</param>
/// <param name="SourceBitRate">The overall source bitrate reported by FFprobe.</param>
/// <param name="Tracks">The source-to-output track plans.</param>
public sealed record ActiveStreamSnapshot(string SessionId, string Channel, HostedStreamFormat Format, DateTime StartedAtUtc, long? SourceBitRate, IReadOnlyList<ActiveStreamTrack> Tracks)
{
    /// <summary>
    /// Gets the remote network endpoint connected to the hosted stream.
    /// </summary>
    public string? ClientAddress { get; init; }

    /// <summary>
    /// Gets the optional client identifier used to notify an owning web player.
    /// </summary>
    public string? ClientId { get; init; }
}

/// <summary>
/// Provides process-local active stream registration and snapshots.
/// </summary>
public interface IActiveStreamRegistry
{
    /// <summary>
    /// Occurs when an active stream receives an explicit stop request.
    /// </summary>
    event Action<ActiveStreamSnapshot>? StopRequested;

    /// <summary>
    /// Registers or replaces an active stream.
    /// </summary>
    /// <param name="stream">The active stream snapshot.</param>
    /// <param name="stop">Stops the underlying stream, or <see langword="null"/> to preserve an existing stop action.</param>
    void Register(ActiveStreamSnapshot stream, Action? stop = null);

    /// <summary>
    /// Registers a new active stream when the configured concurrency limit permits it.
    /// </summary>
    /// <param name="stream">The active stream snapshot.</param>
    /// <param name="maximumConcurrentStreams">The maximum active count, or zero for unlimited.</param>
    /// <param name="stop">Stops the underlying stream.</param>
    /// <returns><see langword="true"/> when the stream was registered.</returns>
    bool TryRegister(ActiveStreamSnapshot stream, int maximumConcurrentStreams, Action stop);

    /// <summary>
    /// Requests that an active stream stop.
    /// </summary>
    /// <param name="sessionId">The runtime session identifier.</param>
    /// <returns><see langword="true"/> when a stoppable stream was found.</returns>
    bool RequestStop(string sessionId);

    /// <summary>
    /// Removes an active stream.
    /// </summary>
    /// <param name="sessionId">The runtime session identifier.</param>
    /// <returns><see langword="true"/> when a stream was removed.</returns>
    bool Unregister(string sessionId);

    /// <summary>
    /// Returns immutable active stream snapshots ordered by start time.
    /// </summary>
    IReadOnlyList<ActiveStreamSnapshot> GetActiveStreams();
}

/// <summary>
/// Thread-safe in-memory active stream registry.
/// </summary>
public sealed class ActiveStreamRegistry : IActiveStreamRegistry
{
    private readonly ConcurrentDictionary<string, ActiveStreamRegistration> _streams = new(StringComparer.Ordinal);
    private readonly object _registrationLock = new();

    /// <inheritdoc />
    public event Action<ActiveStreamSnapshot>? StopRequested;

    /// <inheritdoc />
    public void Register(ActiveStreamSnapshot stream, Action? stop = null)
    {
        lock (_registrationLock)
        {
            RegisterCore(stream, stop);
        }
    }

    /// <inheritdoc />
    public bool TryRegister(ActiveStreamSnapshot stream, int maximumConcurrentStreams, Action stop)
    {
        lock (_registrationLock)
        {
            if (!_streams.ContainsKey(stream.SessionId) &&
                maximumConcurrentStreams > 0 &&
                _streams.Count >= maximumConcurrentStreams)
            {
                return false;
            }

            RegisterCore(stream, stop);
            return true;
        }
    }

    /// <inheritdoc />
    public bool RequestStop(string sessionId)
    {
        ActiveStreamRegistration registration;
        Action stop;
        lock (_registrationLock)
        {
            if (!_streams.TryGetValue(sessionId, out var current) ||
                current.Stop is not { } stopAction ||
                !_streams.TryRemove(sessionId, out var removed) ||
                removed is null)
            {
                return false;
            }

            registration = removed;
            stop = stopAction;
        }

        StopRequested?.Invoke(registration.Snapshot);
        stop();
        return true;
    }

    /// <inheritdoc />
    public bool Unregister(string sessionId)
    {
        lock (_registrationLock)
        {
            return _streams.TryRemove(sessionId, out _);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ActiveStreamSnapshot> GetActiveStreams()
    {
        return _streams.Values
            .Select(registration => registration.Snapshot)
            .OrderBy(stream => stream.StartedAtUtc)
            .Select(stream => stream with { Tracks = stream.Tracks.ToArray() })
            .ToArray();
    }

    private sealed record ActiveStreamRegistration(ActiveStreamSnapshot Snapshot, Action? Stop);

    private void RegisterCore(ActiveStreamSnapshot stream, Action? stop)
    {
        _streams.AddOrUpdate(
            stream.SessionId,
            _ => new ActiveStreamRegistration(stream with { Tracks = stream.Tracks.ToArray() }, stop),
            (_, current) => new ActiveStreamRegistration(
                stream with
                {
                    ClientAddress = stream.ClientAddress ?? current.Snapshot.ClientAddress,
                    ClientId = stream.ClientId ?? current.Snapshot.ClientId,
                    Tracks = stream.Tracks.ToArray()
                },
                stop ?? current.Stop));
    }
}

/// <summary>
/// Probes media stream metadata with FFprobe.
/// </summary>
public interface IMediaProbeService
{
    /// <summary>
    /// Probes the specified media source.
    /// </summary>
    /// <param name="inputUri">The live media source.</param>
    /// <param name="cancellationToken">Stops probing when the request is canceled.</param>
    Task<MediaProbeResult> ProbeAsync(Uri inputUri, CancellationToken cancellationToken);
}

/// <summary>
/// Uses Jellyfin FFprobe to inspect live media sources.
/// </summary>
public sealed class MediaProbeService : IMediaProbeService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);
    private readonly ILogger<MediaProbeService> _logger;
    private readonly ITunerStreamMultiplexer _tunerStreamMultiplexer;

    /// <summary>
    /// Initializes the media probe service.
    /// </summary>
    /// <param name="logger">Logger used for probe diagnostics.</param>
    /// <param name="tunerStreamMultiplexer">Shares tuner input across probes and stream encoders.</param>
    public MediaProbeService(ILogger<MediaProbeService> logger, ITunerStreamMultiplexer tunerStreamMultiplexer)
    {
        _logger = logger;
        _tunerStreamMultiplexer = tunerStreamMultiplexer;
    }

    /// <inheritdoc />
    public async Task<MediaProbeResult> ProbeAsync(Uri inputUri, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(ProbeTimeout);
        var probeToken = timeoutSource.Token;
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffprobe",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in MediaProbeParser.CreatePipeArguments())
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = StartProcess(startInfo);
        var inputTask = TunerInputPump.PumpAsync(_tunerStreamMultiplexer, inputUri, process, _logger, probeToken);
        Task<string>? outputTask = null;
        Task<string>? errorTask = null;
        try
        {
            outputTask = process.StandardOutput.ReadToEndAsync(probeToken);
            errorTask = process.StandardError.ReadToEndAsync(probeToken);
            await process.WaitForExitAsync(probeToken);

            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                throw new MpegTsTranscodeException($"FFprobe exited with code {process.ExitCode}: {error.Trim()}");
            }

            var result = MediaProbeParser.Parse(output);
            _logger.LogDebug("FFprobe found {TrackCount} media track(s) for {InputUri}", result.Tracks.Count, inputUri);
            return result;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MpegTsTranscodeException($"FFprobe timed out after {ProbeTimeout.TotalSeconds:0} seconds for {inputUri}.", ex);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            await ObserveTaskAsync(outputTask);
            await ObserveTaskAsync(errorTask);
            await ObserveTaskAsync(inputTask);
        }
    }

    private static async Task ObserveTaskAsync(Task<string>? task)
    {
        if (task is null || task.IsCompletedSuccessfully)
        {
            return;
        }

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task ObserveTaskAsync(Task task)
    {
        if (task.IsCompletedSuccessfully)
        {
            return;
        }

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static Process StartProcess(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo) ?? throw new MpegTsTranscodeException("Failed to start ffprobe.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new MpegTsTranscodeException($"ffprobe could not be started: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// Builds FFprobe arguments and parses its JSON output.
/// </summary>
public static class MediaProbeParser
{
    private static readonly string[] CommonArguments =
    [
        "-v", "error",
        "-analyzeduration", "1000000",
        "-probesize", "1000000"
    ];

    /// <summary>
    /// Creates deterministic FFprobe arguments for live media metadata.
    /// </summary>
    /// <param name="inputUri">The live media source.</param>
    /// <returns>The FFprobe argument list.</returns>
    public static IReadOnlyList<string> CreateArguments(Uri inputUri) =>
    [
        .. CommonArguments,
        "-rw_timeout", "10000000",
        "-reconnect", "1",
        "-reconnect_streamed", "1",
        "-reconnect_on_http_error", "503",
        "-reconnect_delay_max", "2",
        "-reconnect_max_retries", "3",
        "-read_intervals", "%+#100",
        "-show_entries", "stream=index,codec_type,codec_name,bit_rate,width,height,channels,sample_rate,closed_captions:stream_tags=language,title:stream_disposition=default,forced,hearing_impaired:format=bit_rate",
        "-of", "json",
        inputUri.AbsoluteUri
    ];

    /// <summary>
    /// Creates deterministic FFprobe arguments for shared standard-input media.
    /// </summary>
    /// <returns>The FFprobe argument list.</returns>
    public static IReadOnlyList<string> CreatePipeArguments() =>
    [
        .. CommonArguments,
        "-read_intervals", "%+#100",
        "-show_entries", "stream=index,codec_type,codec_name,bit_rate,width,height,channels,sample_rate,closed_captions:stream_tags=language,title:stream_disposition=default,forced,hearing_impaired:format=bit_rate",
        "-of", "json",
        "pipe:0"
    ];

    /// <summary>
    /// Parses FFprobe JSON output into typed media metadata.
    /// </summary>
    /// <param name="json">The FFprobe JSON payload.</param>
    /// <returns>The parsed media metadata.</returns>
    public static MediaProbeResult Parse(string json)
    {
        try
        {
            var result = JsonSerializer.Deserialize<FfprobeResult>(json) ?? new FfprobeResult();
            var tracks = result.Streams
                .Select(ParseTrack)
                .Where(track => track is not null)
                .Cast<MediaTrackMetadata>()
                .ToArray();
            return new MediaProbeResult(tracks, ParseLong(result.Format?.BitRate));
        }
        catch (JsonException ex)
        {
            throw new MpegTsTranscodeException("FFprobe returned invalid stream metadata.", ex);
        }
    }

    private static MediaTrackMetadata? ParseTrack(FfprobeStream stream)
    {
        var type = stream.CodecType switch
        {
            "video" => MediaTrackType.Video,
            "audio" => MediaTrackType.Audio,
            "subtitle" => MediaTrackType.Subtitle,
            _ => (MediaTrackType?)null
        };

        return type is null
            ? null
            : new MediaTrackMetadata(stream.Index, type.Value, stream.CodecName ?? "unknown", ParseLong(stream.BitRate), stream.Width, stream.Height, stream.Channels, ParseInt(stream.SampleRate))
            {
                Language = NormalizeMetadata(stream.Tags?.Language),
                Title = NormalizeMetadata(stream.Tags?.Title),
                IsDefault = stream.Disposition?.Default == 1,
                IsForced = stream.Disposition?.Forced == 1,
                IsHearingImpaired = stream.Disposition?.HearingImpaired == 1,
                HasClosedCaptions = stream.ClosedCaptions > 0,
                SubtitlePresentation = type == MediaTrackType.Subtitle
                    ? SubtitleCapabilityPolicy.Classify(stream.CodecName)
                    : SubtitlePresentation.Unsupported
            };
    }

    private static long? ParseLong(string? value)
    {
        return long.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;
    }

    private static int? ParseInt(string? value)
    {
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;
    }

    private static string? NormalizeMetadata(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class FfprobeResult
    {
        /// <summary>
        /// Gets or sets streams.
        /// </summary>
        [JsonPropertyName("streams")]
        public List<FfprobeStream> Streams { get; init; } = [];

        /// <summary>
        /// Gets or sets format.
        /// </summary>
        [JsonPropertyName("format")]
        public FfprobeFormat? Format { get; init; }
    }

    private sealed class FfprobeStream
    {
        /// <summary>
        /// Gets or sets index.
        /// </summary>
        [JsonPropertyName("index")]
        public int Index { get; init; }

        /// <summary>
        /// Gets or sets codec type.
        /// </summary>
        [JsonPropertyName("codec_type")]
        public string? CodecType { get; init; }

        /// <summary>
        /// Gets or sets codec name.
        /// </summary>
        [JsonPropertyName("codec_name")]
        public string? CodecName { get; init; }

        /// <summary>
        /// Gets or sets bit rate.
        /// </summary>
        [JsonPropertyName("bit_rate")]
        public string? BitRate { get; init; }

        /// <summary>
        /// Gets or sets width.
        /// </summary>
        [JsonPropertyName("width")]
        public int? Width { get; init; }

        /// <summary>
        /// Gets or sets height.
        /// </summary>
        [JsonPropertyName("height")]
        public int? Height { get; init; }

        /// <summary>
        /// Gets or sets channels.
        /// </summary>
        [JsonPropertyName("channels")]
        public int? Channels { get; init; }

        /// <summary>
        /// Gets or sets sample rate.
        /// </summary>
        [JsonPropertyName("sample_rate")]
        public string? SampleRate { get; init; }

        [JsonPropertyName("closed_captions")]
        public int ClosedCaptions { get; init; }

        [JsonPropertyName("tags")]
        public FfprobeTags? Tags { get; init; }

        [JsonPropertyName("disposition")]
        public FfprobeDisposition? Disposition { get; init; }
    }

    private sealed class FfprobeTags
    {
        [JsonPropertyName("language")]
        public string? Language { get; init; }
        [JsonPropertyName("title")]
        public string? Title { get; init; }
    }

    private sealed class FfprobeDisposition
    {
        [JsonPropertyName("default")]
        public int Default { get; init; }
        [JsonPropertyName("forced")]
        public int Forced { get; init; }
        [JsonPropertyName("hearing_impaired")]
        public int HearingImpaired { get; init; }
    }

    private sealed class FfprobeFormat
    {
        /// <summary>
        /// Gets or sets bit rate.
        /// </summary>
        [JsonPropertyName("bit_rate")]
        public string? BitRate { get; init; }
    }
}

/// <summary>
/// Parses input track metadata emitted by FFmpeg while opening a live source.
/// </summary>
public static partial class FfmpegInputMetadataParser
{
    /// <summary>
    /// Attempts to parse one FFmpeg input stream description.
    /// </summary>
    /// <param name="line">A line written by FFmpeg to standard error.</param>
    /// <param name="track">The parsed media track when successful.</param>
    /// <returns><see langword="true"/> when the line describes a supported input track.</returns>
    public static bool TryParseTrack(string line, out MediaTrackMetadata track)
    {
        var streamMatch = StreamPattern().Match(line);
        if (!streamMatch.Success)
        {
            track = null!;
            return false;
        }

        var type = streamMatch.Groups["type"].Value switch
        {
            "Video" => MediaTrackType.Video,
            "Audio" => MediaTrackType.Audio,
            _ => MediaTrackType.Subtitle
        };
        var details = streamMatch.Groups["details"].Value;
        var resolutionMatch = ResolutionPattern().Match(details);
        var sampleRateMatch = SampleRatePattern().Match(details);
        track = new MediaTrackMetadata(
            int.Parse(streamMatch.Groups["index"].Value),
            type,
            streamMatch.Groups["codec"].Value,
            null,
            ParseOptionalInt(resolutionMatch, "width"),
            ParseOptionalInt(resolutionMatch, "height"),
            type == MediaTrackType.Audio ? ParseChannels(details) : null,
            ParseOptionalInt(sampleRateMatch, "rate"))
        {
            SubtitlePresentation = type == MediaTrackType.Subtitle
                ? SubtitleCapabilityPolicy.Classify(streamMatch.Groups["codec"].Value)
                : SubtitlePresentation.Unsupported
        };
        return true;
    }

    private static int? ParseChannels(string details)
    {
        if (details.Contains("mono", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (details.Contains("stereo", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        var surroundMatch = SurroundPattern().Match(details);
        return surroundMatch.Success
            ? int.Parse(surroundMatch.Groups["major"].Value) + int.Parse(surroundMatch.Groups["minor"].Value)
            : ParseOptionalInt(ChannelCountPattern().Match(details), "count");
    }

    private static int? ParseOptionalInt(Match match, string groupName)
    {
        return match.Success && int.TryParse(match.Groups[groupName].Value, out var value) ? value : null;
    }

    [GeneratedRegex(@"^\s*Stream #0:(?<index>\d+)(?:\[[^\]]+\])?(?:\([^)]+\))?: (?<type>Video|Audio|Subtitle): (?<codec>[^,\s]+)(?<details>.*)$")]
    private static partial Regex StreamPattern();

    [GeneratedRegex(@"(?<width>\d{2,5})x(?<height>\d{2,5})")]
    private static partial Regex ResolutionPattern();

    [GeneratedRegex(@"(?<rate>\d+) Hz")]
    private static partial Regex SampleRatePattern();

    [GeneratedRegex(@"(?<major>\d+)\.(?<minor>\d+)")]
    private static partial Regex SurroundPattern();

    [GeneratedRegex(@"(?<count>\d+) channels?")]
    private static partial Regex ChannelCountPattern();
}

/// <summary>
/// Builds active stream snapshots from probed source metadata and configured output plans.
/// </summary>
public static class ActiveStreamPlanFactory
{
    /// <summary>
    /// Creates a transparent MPEG-TS active stream snapshot.
    /// </summary>
    public static ActiveStreamSnapshot CreateMpegTs(string sessionId, string channel, DateTime startedAtUtc, MediaProbeResult source, AppSettings settings)
    {
        var tracks = source.Tracks.Select(track =>
        {
            if (track.Type == MediaTrackType.Video)
            {
                var transcodeVideo = MpegTsTranscodePlanner.ShouldTranscodeVideo(track.Codec, settings.VirtualTunerVideoMode);
                return CreateTrack(track, transcodeVideo ? "h264" : "copy", transcodeVideo ? settings.MaximumVideoBitRateMbps * 1_000_000L : track.BitRate, track.Channels, track.SampleRate);
            }

            if (track.Type == MediaTrackType.Subtitle)
            {
                var compatible = SubtitleCapabilityPolicy.CanCopyToMpegTs(track.Codec);
                return CreateTrack(track, compatible ? "copy" : "not-mapped", track.BitRate, null, null) with
                {
                    IsSelected = compatible
                };
            }

            var outputCodec = MpegTsTranscodePlanner.GetOutputAudioCodec(track.Codec, settings.AudioTranscodeMode, settings.Ac4TranscodeTarget);
            var outputBitRate = outputCodec switch
            {
                "ac3" => 448_000,
                "eac3" => 640_000,
                _ => track.BitRate
            };
            var outputChannels = outputCodec == "copy"
                ? track.Channels
                : track.Channels.HasValue
                    ? Math.Min(track.Channels.Value, 6)
                    : null;
            return CreateTrack(track, outputCodec, outputBitRate, outputChannels, track.SampleRate);
        }).ToArray();

        return new ActiveStreamSnapshot(sessionId, channel, HostedStreamFormat.MpegTs, startedAtUtc, source.BitRate, tracks);
    }

    /// <summary>
    /// Creates a fragmented MP4 active stream snapshot.
    /// </summary>
    public static ActiveStreamSnapshot CreateFragmentedMp4(
        string sessionId,
        string channel,
        DateTime startedAtUtc,
        MediaProbeResult source,
        long outputVideoBitRate = 10_000_000,
        bool copyVideo = false,
        WatchTrackSelection? selection = null)
    {
        if (selection is null)
        {
            selection = WatchStreamPlanner.SelectTracks(source, null, null);
        }

        var tracks = source.Tracks.Select(track =>
        {
            var selected = track.Type == MediaTrackType.Video ||
                track.Type == MediaTrackType.Audio && track.Index == selection.Audio?.Index ||
                track.Type == MediaTrackType.Subtitle && track.Index == selection.Subtitle?.Index;
            var outputCodec = track.Type switch
            {
                MediaTrackType.Video => copyVideo ? "copy" : "h264",
                MediaTrackType.Audio when selected => "aac",
                MediaTrackType.Subtitle when selected && selection.SubtitlePresentation == SubtitlePresentation.WebVtt => "webvtt",
                MediaTrackType.Subtitle when selected => "burn-in",
                _ => "not-mapped"
            };
            return CreateTrack(
                track,
                outputCodec,
                track.Type == MediaTrackType.Video ? (copyVideo ? track.BitRate : outputVideoBitRate) :
                    track.Type == MediaTrackType.Audio && selected ? 128_000 : null,
                track.Type == MediaTrackType.Audio && selected ? 2 : track.Channels,
                track.Type == MediaTrackType.Audio && selected ? 44_100 : track.SampleRate) with
            {
                IsSelected = selected,
                SubtitlePresentation = track.Type == MediaTrackType.Subtitle ? track.SubtitlePresentation : null
            };
        }).ToArray();
        if (tracks.Length == 0)
        {
            return CreateFixedTranscode(sessionId, channel, HostedStreamFormat.FragmentedMp4, startedAtUtc, source, outputVideoBitRate, copyVideo);
        }

        return new ActiveStreamSnapshot(sessionId, channel, HostedStreamFormat.FragmentedMp4, startedAtUtc, source.BitRate, tracks);
    }

    /// <summary>
    /// Creates an HLS active stream snapshot.
    /// </summary>
    public static ActiveStreamSnapshot CreateHls(string sessionId, string channel, DateTime startedAtUtc, MediaProbeResult source, long outputVideoBitRate = 10_000_000)
    {
        return CreateFixedTranscode(sessionId, channel, HostedStreamFormat.Hls, startedAtUtc, source, outputVideoBitRate, copyVideo: false);
    }

    /// <summary>
    /// Creates metadata for a synthetic protected-content slate.
    /// </summary>
    public static ActiveStreamSnapshot CreateProtectedSlate(string sessionId, string channel, HostedStreamFormat format, DateTime startedAtUtc)
    {
        return CreateSlate(sessionId, channel, format, startedAtUtc, "protected");
    }

    /// <summary>
    /// Creates metadata for a synthetic disabled-channel slate.
    /// </summary>
    public static ActiveStreamSnapshot CreateDisabledSlate(string sessionId, string channel, HostedStreamFormat format, DateTime startedAtUtc)
    {
        return CreateSlate(sessionId, channel, format, startedAtUtc, "disabled");
    }

    private static ActiveStreamSnapshot CreateSlate(string sessionId, string channel, HostedStreamFormat format, DateTime startedAtUtc, string sourceCodec)
    {
        ActiveStreamTrack[] tracks =
        [
            new(MediaTrackType.Video, sourceCodec, "h264", null, 2_500_000, 1280, 720, null, null, null),
            new(MediaTrackType.Audio, sourceCodec, "aac", null, 128_000, null, null, null, 2, 44_100)
        ];
        return new ActiveStreamSnapshot(sessionId, channel, format, startedAtUtc, null, tracks);
    }

    private static ActiveStreamSnapshot CreateFixedTranscode(string sessionId, string channel, HostedStreamFormat format, DateTime startedAtUtc, MediaProbeResult source, long outputVideoBitRate, bool copyVideo)
    {
        var video = source.Tracks.FirstOrDefault(track => track.Type == MediaTrackType.Video)
            ?? new MediaTrackMetadata(0, MediaTrackType.Video, "unknown", null, null, null, null, null);
        var audio = source.Tracks.FirstOrDefault(track => track.Type == MediaTrackType.Audio)
            ?? new MediaTrackMetadata(1, MediaTrackType.Audio, "unknown", null, null, null, null, null);
        ActiveStreamTrack[] tracks =
        [
            CreateTrack(video, copyVideo ? "copy" : "h264", copyVideo ? video.BitRate : outputVideoBitRate, null, null),
            CreateTrack(audio, "aac", 128_000, 2, 44_100)
        ];
        return new ActiveStreamSnapshot(sessionId, channel, format, startedAtUtc, source.BitRate, tracks);
    }

    private static ActiveStreamTrack CreateTrack(MediaTrackMetadata source, string outputCodec, long? outputBitRate, int? outputChannels, int? outputSampleRate)
    {
        return new ActiveStreamTrack(source.Type, source.Codec, outputCodec, source.BitRate, outputBitRate, source.Width, source.Height, source.Channels, outputChannels, outputSampleRate)
        {
            SourceIndex = source.Index,
            Language = source.Language,
            Title = source.Title,
            IsDefault = source.IsDefault,
            IsForced = source.IsForced,
            IsHearingImpaired = source.IsHearingImpaired,
            HasClosedCaptions = source.HasClosedCaptions,
            SubtitlePresentation = source.Type == MediaTrackType.Subtitle ? source.SubtitlePresentation : null
        };
    }
}
