using System.Diagnostics;

namespace Lineup.Web.Services;

/// <summary>
/// Describes an audio stream discovered in an input media source.
/// </summary>
/// <param name="Index">The input stream index reported by FFprobe.</param>
/// <param name="CodecName">The FFmpeg codec name.</param>
/// <param name="Channels">The number of audio channels.</param>
public sealed record AudioStreamInfo(int Index, string CodecName, int Channels);

/// <summary>
/// Transcodes live input streams into compatibility-oriented MPEG-TS output.
/// </summary>
public interface IMpegTsTranscodeService
{
    /// <summary>
    /// Probes the input audio tracks and streams MPEG-TS output using the configured audio behavior.
    /// </summary>
    /// <param name="inputUri">The live media source.</param>
    /// <param name="input">An active subscription to the live media source.</param>
    /// <param name="source">Metadata discovered for the live media source.</param>
    /// <param name="settings">The current video and audio transcode settings.</param>
    /// <param name="output">The destination stream for MPEG-TS data.</param>
    /// <param name="cancellationToken">Stops probing and transcoding when the client disconnects.</param>
    Task TranscodeAsync(Uri inputUri, Stream input, MediaProbeResult source, AppSettings settings, Stream output, CancellationToken cancellationToken);
}

/// <summary>
/// Represents an FFprobe or FFmpeg failure while producing an MPEG-TS stream.
/// </summary>
public sealed class MpegTsTranscodeException : Exception
{
    /// <summary>
    /// Initializes the exception with an error message.
    /// </summary>
    /// <param name="message">The error message.</param>
    public MpegTsTranscodeException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes the exception with an error message and underlying exception.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The exception that caused the transcode failure.</param>
    public MpegTsTranscodeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Uses Jellyfin FFprobe and FFmpeg to produce transparent MPEG-TS proxy output.
/// </summary>
/// <param name="logger">Logger used for transcode lifecycle diagnostics.</param>
public sealed class MpegTsTranscodeService(ILogger<MpegTsTranscodeService> logger) : IMpegTsTranscodeService
{
    private const int BufferSize = 64 * 1024;

    /// <inheritdoc />
    public async Task TranscodeAsync(Uri inputUri, Stream input, MediaProbeResult source, AppSettings settings, Stream output, CancellationToken cancellationToken)
    {
        var sourceVideoCodec = source.Tracks.FirstOrDefault(track => track.Type == MediaTrackType.Video)?.Codec;
        var audioStreams = source.Tracks
            .Where(track => track.Type == MediaTrackType.Audio)
            .Select(track => new AudioStreamInfo(track.Index, track.Codec, track.Channels ?? 0))
            .ToArray();
        var subtitleStreams = source.Tracks.Where(track => track.Type == MediaTrackType.Subtitle).ToArray();
        var arguments = MpegTsTranscodePlanner.CreateFfmpegArguments(inputUri, sourceVideoCodec, audioStreams, settings, subtitleStreams).ToList();
        arguments[arguments.IndexOf(inputUri.AbsoluteUri)] = "pipe:0";
        foreach (var subtitle in subtitleStreams.Where(track => !SubtitleCapabilityPolicy.CanCopyToMpegTs(track.Codec)))
        {
            logger.LogWarning(
                "Subtitle stream {StreamIndex} using codec {SubtitleCodec} cannot be represented by the MPEG-TS muxer and will not be mapped",
                subtitle.Index,
                subtitle.Codec);
        }

        var startInfo = CreateStartInfo("ffmpeg", arguments, redirectStandardOutput: true, redirectStandardInput: true);
        logger.LogInformation(
            "Starting MPEG-TS transcode with source video {VideoCodec}, {AudioStreamCount} audio stream(s), audio mode {AudioMode}, and AC-4 target {Ac4Target}",
            sourceVideoCodec,
            audioStreams.Length,
            settings.AudioTranscodeMode,
            settings.Ac4TranscodeTarget);

        using var process = StartProcess(startInfo);
        var inputTask = TunerInputPump.PumpAsync(input, inputUri, process, logger, cancellationToken);
        var errorTask = CaptureStandardErrorAsync(process, cancellationToken);
        IReadOnlyList<string>? errors = null;

        try
        {
            var buffer = new byte[BufferSize];
            int bytesRead;
            while ((bytesRead = await process.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            await process.WaitForExitAsync(cancellationToken);
            errors = await errorTask;

            if (process.ExitCode != 0)
            {
                throw new MpegTsTranscodeException($"FFmpeg exited with code {process.ExitCode}: {string.Join(Environment.NewLine, errors)}");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            if (errors is null)
            {
                try
                {
                    await errorTask;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
            }

            await inputTask;
        }
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, IReadOnlyList<string> arguments, bool redirectStandardOutput, bool redirectStandardInput)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = redirectStandardInput,
            RedirectStandardOutput = redirectStandardOutput,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static Process StartProcess(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo) ?? throw new MpegTsTranscodeException($"Failed to start {startInfo.FileName}.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            var reason = ex.NativeErrorCode == 2
                ? "was not found. Install Jellyfin FFmpeg and ensure it is on PATH"
                : $"could not be started: {ex.Message}";
            throw new MpegTsTranscodeException($"{startInfo.FileName} {reason}.", ex);
        }
    }

    private static async Task<IReadOnlyList<string>> CaptureStandardErrorAsync(Process process, CancellationToken cancellationToken)
    {
        var lines = new Queue<string>();

        while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            lines.Enqueue(line);
            while (lines.Count > 20)
            {
                lines.Dequeue();
            }
        }

        return lines.ToArray();
    }

}

/// <summary>
/// Builds deterministic FFprobe and FFmpeg argument lists for MPEG-TS audio transcoding.
/// </summary>
public static class MpegTsTranscodePlanner
{
    /// <summary>
    /// Creates FFmpeg arguments that copy video and apply per-track audio codec decisions.
    /// </summary>
    /// <param name="inputUri">The live media source.</param>
    /// <param name="sourceVideoCodec">The detected source video codec.</param>
    /// <param name="audioStreams">Audio stream metadata in output mapping order.</param>
    /// <param name="settings">The current video and audio transcode settings.</param>
    /// <param name="subtitleStreams">Detected standalone subtitle streams.</param>
    /// <returns>The FFmpeg argument list.</returns>
    public static IReadOnlyList<string> CreateFfmpegArguments(
        Uri inputUri,
        string? sourceVideoCodec,
        IReadOnlyList<AudioStreamInfo> audioStreams,
        AppSettings settings,
        IReadOnlyList<MediaTrackMetadata>? subtitleStreams = null)
    {
        List<string> arguments =
        [
            "-hide_banner",
            "-loglevel", "warning",
            "-analyzeduration", "1000000",
            "-probesize", "1000000",
            "-fflags", "+genpts",
            "-i", inputUri.AbsoluteUri,
            "-map", "0:v?",
            "-map", "0:a?",
            "-map_metadata", "0",
            "-c", "copy"
        ];

        foreach (var subtitle in subtitleStreams ?? [])
        {
            if (subtitle.Type != MediaTrackType.Subtitle)
            {
                throw new ArgumentException($"Stream index {subtitle.Index} is not a subtitle stream.", nameof(subtitleStreams));
            }

            if (SubtitleCapabilityPolicy.CanCopyToMpegTs(subtitle.Codec))
            {
                arguments.AddRange(["-map", $"0:{subtitle.Index}"]);
            }
        }

        if (ShouldTranscodeVideo(sourceVideoCodec, settings.VirtualTunerVideoMode))
        {
            var preset = settings.WebVideoPreset.ToString().ToLowerInvariant();
            arguments.AddRange(
            [
                "-c:v:0", "libx264",
                "-preset", preset,
                "-tune", "zerolatency",
                "-crf", settings.WebVideoQuality.ToString(),
                "-maxrate", $"{settings.MaximumVideoBitRateMbps}M",
                "-bufsize", $"{settings.MaximumVideoBitRateMbps * 2}M",
                "-profile:v:0", "high",
                "-level:v:0", "4.2",
                "-pix_fmt:v:0", "yuv420p",
                "-a53cc:v:0", "1",
                "-flags:v:0", "+cgop",
                "-g:v:0", "120",
                "-keyint_min:v:0", "60",
                "-sc_threshold:v:0", "0",
                "-x264-params:v:0", "repeat-headers=1"
            ]);
        }

        for (var audioIndex = 0; audioIndex < audioStreams.Count; audioIndex++)
        {
            var stream = audioStreams[audioIndex];
            var codec = GetOutputAudioCodec(stream.CodecName, settings.AudioTranscodeMode, settings.Ac4TranscodeTarget);
            if (codec == "copy")
            {
                continue;
            }

            arguments.Add($"-c:a:{audioIndex}");
            arguments.Add(codec);
            arguments.Add($"-b:a:{audioIndex}");
            arguments.Add(codec == "ac3" ? "448k" : "640k");

            if (stream.Channels > 6)
            {
                arguments.Add($"-ac:a:{audioIndex}");
                arguments.Add("6");
            }
        }

        arguments.AddRange(["-max_muxing_queue_size", "1024", "-mpegts_flags", "+resend_headers", "-muxdelay", "0", "-f", "mpegts", "pipe:1"]);

        return arguments;
    }

    /// <summary>
    /// Returns whether a source video codec requires conversion for virtual-tuner compatibility.
    /// </summary>
    public static bool ShouldTranscodeVideo(string? sourceVideoCodec, VirtualTunerVideoMode videoMode) =>
        videoMode == VirtualTunerVideoMode.ConvertHevcToH264 &&
        string.Equals(sourceVideoCodec, "hevc", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the configured output codec for one audio track.
    /// </summary>
    public static string GetOutputAudioCodec(string inputCodec, AudioTranscodeMode audioMode, Ac4TranscodeTarget ac4Target)
    {
        if (string.Equals(inputCodec, "ac4", StringComparison.OrdinalIgnoreCase))
        {
            return ac4Target switch
            {
                Ac4TranscodeTarget.Preserve => "copy",
                Ac4TranscodeTarget.Ac3 => "ac3",
                Ac4TranscodeTarget.Eac3 => "eac3",
                _ => throw new ArgumentOutOfRangeException(nameof(ac4Target), ac4Target, "Unknown AC-4 target.")
            };
        }

        return audioMode switch
        {
            AudioTranscodeMode.Copy => "copy",
            AudioTranscodeMode.Ac3 => "ac3",
            AudioTranscodeMode.Eac3 => "eac3",
            _ => throw new ArgumentOutOfRangeException(nameof(audioMode), audioMode, "Unknown audio transcode mode.")
        };
    }
}
