using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Lineup.Streaming.FFmpeg;

/// <summary>
/// Probes media stream metadata with FFprobe.
/// </summary>
public interface IMediaProbeService
{
    /// <summary>Probes a provider-owned physical acquisition.</summary>
    Task<MediaProbeResult> ProbeAsync(
        StreamAcquisitionDescriptor descriptor,
        StreamAcquisitionFactory acquisitionFactory,
        string sourceName,
        CancellationToken cancellationToken);
}

/// <summary>
/// Uses Jellyfin FFprobe to inspect live media sources.
/// </summary>
public sealed class MediaProbeService : IMediaProbeService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);
    private readonly ILogger<MediaProbeService> _logger;
    private readonly ITunerStreamMultiplexer _tunerStreamMultiplexer;
    private readonly IMediaProcessFactory _processFactory;
    private readonly IStreamingSessionGraph? _streamingGraph;

    /// <summary>
    /// Initializes the media probe service.
    /// </summary>
    /// <param name="logger">Logger used for probe diagnostics.</param>
    /// <param name="tunerStreamMultiplexer">Shares tuner input across probes and stream encoders.</param>
    /// <param name="processFactory">Starts FFprobe without shell interpretation.</param>
    /// <param name="streamingGraph">Optionally shares probe results for the lifetime of a live acquisition.</param>
    public MediaProbeService(
        ILogger<MediaProbeService> logger,
        ITunerStreamMultiplexer tunerStreamMultiplexer,
        IMediaProcessFactory processFactory,
        IStreamingSessionGraph? streamingGraph = null)
    {
        _logger = logger;
        _tunerStreamMultiplexer = tunerStreamMultiplexer;
        _processFactory = processFactory;
        _streamingGraph = streamingGraph;
    }

    /// <summary>Initializes the probe service with the default process factory.</summary>
    public MediaProbeService(ILogger<MediaProbeService> logger, ITunerStreamMultiplexer tunerStreamMultiplexer)
        : this(logger, tunerStreamMultiplexer, new MediaProcessFactory())
    {
    }

    /// <inheritdoc />
    public async Task<MediaProbeResult> ProbeAsync(
        StreamAcquisitionDescriptor descriptor,
        StreamAcquisitionFactory acquisitionFactory,
        string sourceName,
        CancellationToken cancellationToken)
    {
        if (_streamingGraph is not null)
        {
            return await _streamingGraph.GetOrCreateAcquisitionMetadataAsync(
                descriptor,
                acquisitionFactory,
                "ffprobe-v1",
                (stream, token) => ProbeStreamCoreAsync(sourceName, stream, token),
                cancellationToken);
        }
        return await ProbeCoreAsync(
            sourceName,
            token => _tunerStreamMultiplexer.SubscribeAsync(descriptor, acquisitionFactory, token),
            cancellationToken);
    }

    private async Task<MediaProbeResult> ProbeCoreAsync(
        string sourceName,
        Func<CancellationToken, ValueTask<Stream>> subscribe,
        CancellationToken cancellationToken)
    {
        await using var input = await subscribe(cancellationToken);
        return await ProbeStreamCoreAsync(sourceName, input, cancellationToken);
    }

    private async Task<MediaProbeResult> ProbeStreamCoreAsync(
        string sourceName,
        Stream input,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(ProbeTimeout);
        var probeToken = timeoutSource.Token;
        using var process = StartProcess(new MediaProcessRequest
        {
            FileName = "ffprobe",
            Arguments = MediaProbeParser.CreatePipeArguments(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        var inputTask = MediaStreamPump.PumpAsync(input, process, _logger, sourceName, probeToken);
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
            _logger.LogDebug("FFprobe found {TrackCount} media track(s) for {InputSource}", result.Tracks.Count, sourceName);
            return result;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MpegTsTranscodeException($"FFprobe timed out after {ProbeTimeout.TotalSeconds:0} seconds for {sourceName}.", ex);
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

    private Process StartProcess(MediaProcessRequest request)
    {
        try
        {
            return _processFactory.Start(request);
        }
        catch (MediaProcessException ex)
        {
            throw new MpegTsTranscodeException(ex.Message, ex);
        }
    }
}

/// <summary>
/// Builds FFprobe arguments and parses its JSON output.
/// </summary>
public static class MediaProbeParser
{
    private const string ShowEntries = "stream=index,codec_type,codec_name,profile,level,bit_rate,width,height,field_order,channels,sample_rate,closed_captions:stream_tags=language,title:" +
        "stream_disposition=default,forced,hearing_impaired:frame=media_type,stream_index,interlaced_frame:frame_side_data=side_data_type:format=bit_rate";
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
        "-read_intervals", "%+3",
        "-show_frames",
        "-show_entries", ShowEntries,
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
        "-read_intervals", "%+3",
        "-show_frames",
        "-show_entries", ShowEntries,
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
            var hasEmbeddedClosedCaptions = result.Frames.Any(frame =>
                string.Equals(frame.MediaType, "video", StringComparison.OrdinalIgnoreCase) &&
                frame.SideDataList.Any(sideData => string.Equals(sideData.SideDataType, "ATSC A53 Part 4 Closed Captions", StringComparison.OrdinalIgnoreCase)));
            var interlacedVideoStreams = result.Frames
                .Where(frame =>
                    string.Equals(frame.MediaType, "video", StringComparison.OrdinalIgnoreCase) &&
                    frame.StreamIndex.HasValue &&
                    frame.InterlacedFrame == 1)
                .Select(frame => frame.StreamIndex!.Value)
                .ToHashSet();
            var tracks = result.Streams
                .Select(stream => ParseTrack(stream, hasEmbeddedClosedCaptions, interlacedVideoStreams.Contains(stream.Index)))
                .Where(track => track is not null)
                .Cast<MediaTrackMetadata>()
                .ToList();
            if (hasEmbeddedClosedCaptions &&
                !tracks.Any(track => track.Type == MediaTrackType.Subtitle && track.Codec is "eia_608" or "eia_708"))
            {
                int index = tracks.Count == 0 ? 0 : tracks.Max(track => track.Index) + 1;
                tracks.Add(new MediaTrackMetadata(index, MediaTrackType.Subtitle, "eia_608", null, null, null, null, null)
                {
                    Title = "Closed Captions",
                    IsEmbeddedClosedCaptions = true,
                    SubtitlePresentation = SubtitlePresentation.WebVtt
                });
            }
            return new MediaProbeResult(tracks, ParseLong(result.Format?.BitRate));
        }
        catch (JsonException ex)
        {
            throw new MpegTsTranscodeException("FFprobe returned invalid stream metadata.", ex);
        }
    }

    private static MediaTrackMetadata? ParseTrack(FfprobeStream stream, bool hasEmbeddedClosedCaptions, bool hasInterlacedVideoFrames)
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
            : new MediaTrackMetadata(stream.Index, type.Value, stream.CodecName ?? "unknown", ParseLong(stream.BitRate), stream.Width, stream.Height, NormalizeChannelCount(stream.Channels), ParseInt(stream.SampleRate))
            {
                Profile = NormalizeMetadata(stream.Profile),
                Level = stream.Level,
                Language = NormalizeMetadata(stream.Tags?.Language),
                Title = NormalizeMetadata(stream.Tags?.Title),
                IsDefault = stream.Disposition?.Default == 1,
                IsForced = stream.Disposition?.Forced == 1,
                IsHearingImpaired = stream.Disposition?.HearingImpaired == 1,
                HasClosedCaptions = type == MediaTrackType.Video && (stream.ClosedCaptions > 0 || hasEmbeddedClosedCaptions),
                ScanType = type == MediaTrackType.Video
                    ? hasInterlacedVideoFrames ? VideoScanType.Interlaced : ParseVideoScanType(stream.FieldOrder)
                    : VideoScanType.Unknown,
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

    private static int? NormalizeChannelCount(int? value) => value is >= 1 and <= 64 ? value : null;

    private static VideoScanType ParseVideoScanType(string? fieldOrder) =>
        fieldOrder?.Trim().ToLowerInvariant() switch
        {
            "progressive" => VideoScanType.Progressive,
            "tt" or "bb" or "tb" or "bt" => VideoScanType.Interlaced,
            _ => VideoScanType.Unknown
        };

    private static string? NormalizeMetadata(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class FfprobeResult
    {
        /// <summary>
        /// Gets or sets streams.
        /// </summary>
        [JsonPropertyName("streams")]
        public List<FfprobeStream> Streams { get; init; } = [];

        /// <summary>Gets the probed media frames used for side-data inspection.</summary>
        [JsonPropertyName("frames")]
        public List<FfprobeFrame> Frames { get; init; } = [];

        /// <summary>
        /// Gets or sets format.
        /// </summary>
        [JsonPropertyName("format")]
        public FfprobeFormat? Format { get; init; }
    }

    private sealed class FfprobeFrame
    {
        /// <summary>Gets the frame media type.</summary>
        [JsonPropertyName("media_type")]
        public string? MediaType { get; init; }

        /// <summary>Gets the source stream index that emitted this frame.</summary>
        [JsonPropertyName("stream_index")]
        public int? StreamIndex { get; init; }

        /// <summary>Gets whether FFprobe marked this frame as interlaced.</summary>
        [JsonPropertyName("interlaced_frame")]
        public int? InterlacedFrame { get; init; }

        /// <summary>Gets the frame side-data entries.</summary>
        [JsonPropertyName("side_data_list")]
        public List<FfprobeSideData> SideDataList { get; init; } = [];
    }

    private sealed class FfprobeSideData
    {
        /// <summary>Gets the side-data type.</summary>
        [JsonPropertyName("side_data_type")]
        public string? SideDataType { get; init; }
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

        /// <summary>Gets the codec profile.</summary>
        [JsonPropertyName("profile")]
        public string? Profile { get; init; }

        /// <summary>Gets the codec level.</summary>
        [JsonPropertyName("level")]
        public int? Level { get; init; }

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

        /// <summary>Gets the video field order.</summary>
        [JsonPropertyName("field_order")]
        public string? FieldOrder { get; init; }

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

        /// <summary>Gets whether the stream reports embedded closed captions.</summary>
        [JsonPropertyName("closed_captions")]
        public int ClosedCaptions { get; init; }

        /// <summary>Gets the stream metadata tags.</summary>
        [JsonPropertyName("tags")]
        public FfprobeTags? Tags { get; init; }

        /// <summary>Gets the stream disposition flags.</summary>
        [JsonPropertyName("disposition")]
        public FfprobeDisposition? Disposition { get; init; }
    }

    private sealed class FfprobeTags
    {
        /// <summary>Gets the stream language tag.</summary>
        [JsonPropertyName("language")]
        public string? Language { get; init; }

        /// <summary>Gets the stream title.</summary>
        [JsonPropertyName("title")]
        public string? Title { get; init; }
    }

    private sealed class FfprobeDisposition
    {
        /// <summary>Gets the default disposition flag.</summary>
        [JsonPropertyName("default")]
        public int Default { get; init; }

        /// <summary>Gets the forced disposition flag.</summary>
        [JsonPropertyName("forced")]
        public int Forced { get; init; }

        /// <summary>Gets the hearing-impaired disposition flag.</summary>
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
        if (surroundMatch.Success)
        {
            var channels = int.Parse(surroundMatch.Groups["major"].Value) +
                int.Parse(surroundMatch.Groups["minor"].Value) +
                ParseOptionalInt(surroundMatch, "height").GetValueOrDefault();
            return NormalizeChannelCount(channels);
        }

        return NormalizeChannelCount(ParseOptionalInt(ChannelCountPattern().Match(details), "count"));
    }

    private static int? ParseOptionalInt(Match match, string groupName)
    {
        return match.Success && int.TryParse(match.Groups[groupName].Value, out var value) ? value : null;
    }

    private static int? NormalizeChannelCount(int? value) => value is >= 1 and <= 64 ? value : null;

    [GeneratedRegex(@"^\s*Stream #0:(?<index>\d+)(?:\[[^\]]+\])?(?:\([^)]+\))?: (?<type>Video|Audio|Subtitle): (?<codec>[^,\s]+)(?<details>.*)$")]
    private static partial Regex StreamPattern();

    [GeneratedRegex(@"(?<width>\d{2,5})x(?<height>\d{2,5})")]
    private static partial Regex ResolutionPattern();

    [GeneratedRegex(@"(?<rate>\d+) Hz")]
    private static partial Regex SampleRatePattern();

    [GeneratedRegex(@"(?:^|,\s*)(?<major>\d{1,2})\.(?<minor>\d{1,2})(?:\.(?<height>\d{1,2}))?(?:\([^,)]*\))?(?=,|$)")]
    private static partial Regex SurroundPattern();

    [GeneratedRegex(@"(?<!\d)(?<count>\d{1,2}) channels?(?!\d)")]
    private static partial Regex ChannelCountPattern();
}

