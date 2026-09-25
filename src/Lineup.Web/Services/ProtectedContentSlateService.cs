using System.Diagnostics;

namespace Lineup.Web.Services;

/// <summary>
/// Identifies the message rendered by a synthetic channel slate.
/// </summary>
public enum ChannelSlateReason
{
    /// <summary>
    /// The tuner channel is DRM protected.
    /// </summary>
    ContentProtected,

    /// <summary>
    /// The channel was disabled in Lineup.
    /// </summary>
    DisabledChannel
}

/// <summary>
/// Generates synthetic media for unavailable tuner channels.
/// </summary>
public interface IProtectedContentSlateService
{
    /// <summary>
    /// Streams a synthetic slate in the requested pipe-based format.
    /// </summary>
    /// <param name="format">The requested hosted stream format.</param>
    /// <param name="channel">The protected virtual channel.</param>
    /// <param name="output">The destination response stream.</param>
    /// <param name="cancellationToken">Stops generation when the client disconnects.</param>
    /// <param name="reason">The reason shown on the slate.</param>
    Task StreamAsync(HostedStreamFormat format, string channel, Stream output, CancellationToken cancellationToken, ChannelSlateReason reason = ChannelSlateReason.ContentProtected);

    /// <summary>
    /// Starts a synthetic HLS slate encoder.
    /// </summary>
    /// <param name="channel">The protected virtual channel.</param>
    /// <param name="playlistPath">The destination HLS playlist path.</param>
    /// <param name="reason">The reason shown on the slate.</param>
    /// <returns>The running FFmpeg process.</returns>
    Process StartHls(string channel, string playlistPath, ChannelSlateReason reason = ChannelSlateReason.ContentProtected);

    /// <summary>
    /// Starts an AAC-only shared CMAF DASH/HLS slate encoder.
    /// </summary>
    /// <param name="channel">The unavailable virtual channel.</param>
    /// <param name="manifestPath">The destination DASH manifest path.</param>
    /// <param name="reason">The reason shown on the slate.</param>
    /// <returns>The running FFmpeg process.</returns>
    Process StartCmaf(string channel, string manifestPath, ChannelSlateReason reason = ChannelSlateReason.ContentProtected);
}

/// <summary>
/// Uses FFmpeg test sources to generate a channel-status slate.
/// </summary>
public sealed class ProtectedContentSlateService : IProtectedContentSlateService
{
    /// <inheritdoc />
    public async Task StreamAsync(HostedStreamFormat format, string channel, Stream output, CancellationToken cancellationToken, ChannelSlateReason reason = ChannelSlateReason.ContentProtected)
    {
        if (format == HostedStreamFormat.Hls)
        {
            throw new ArgumentOutOfRangeException(nameof(format), format, "HLS slates must be started with StartHls.");
        }

        using var process = StartProcess(ProtectedContentSlatePlanner.CreatePipeArguments(format, channel, reason));
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync(cancellationToken);
                throw new MpegTsTranscodeException($"Protected-content slate FFmpeg exited with code {process.ExitCode}: {error.Trim()}");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    /// <inheritdoc />
    public Process StartHls(string channel, string playlistPath, ChannelSlateReason reason = ChannelSlateReason.ContentProtected)
    {
        return StartProcess(ProtectedContentSlatePlanner.CreateHlsArguments(channel, playlistPath, reason));
    }

    /// <inheritdoc />
    public Process StartCmaf(string channel, string manifestPath, ChannelSlateReason reason = ChannelSlateReason.ContentProtected)
    {
        return StartProcess(ProtectedContentSlatePlanner.CreateCmafArguments(channel, manifestPath, reason), Path.GetDirectoryName(manifestPath));
    }

    private static Process StartProcess(IReadOnlyList<string> arguments, string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            WorkingDirectory = workingDirectory ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            return Process.Start(startInfo) ?? throw new MpegTsTranscodeException("Failed to start protected-content slate FFmpeg.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new MpegTsTranscodeException($"Protected-content slate FFmpeg could not be started: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// Builds deterministic FFmpeg arguments for protected-content slates.
/// </summary>
public static class ProtectedContentSlatePlanner
{
    /// <summary>
    /// Creates arguments for a pipe-based fMP4 or MPEG-TS slate.
    /// </summary>
    public static IReadOnlyList<string> CreatePipeArguments(HostedStreamFormat format, string channel, ChannelSlateReason reason = ChannelSlateReason.ContentProtected)
    {
        var arguments = CreateCommonArguments(channel, reason);
        if (format == HostedStreamFormat.FragmentedMp4)
        {
            arguments.AddRange(["-movflags", "frag_keyframe+empty_moov+default_base_moof", "-frag_duration", "1000000", "-f", "mp4", "pipe:1"]);
        }
        else if (format == HostedStreamFormat.MpegTs)
        {
            arguments.AddRange(["-mpegts_flags", "+resend_headers", "-f", "mpegts", "pipe:1"]);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format), format, "Only fMP4 and MPEG-TS support pipe output.");
        }

        return arguments;
    }

    /// <summary>
    /// Creates arguments for an HLS protected-content slate.
    /// </summary>
    public static IReadOnlyList<string> CreateHlsArguments(string channel, string playlistPath, ChannelSlateReason reason = ChannelSlateReason.ContentProtected)
    {
        var arguments = CreateCommonArguments(channel, reason);
        arguments.AddRange([
            "-f", "hls",
            "-hls_time", "4",
            "-hls_list_size", "10",
            "-hls_segment_type", "mpegts",
            "-hls_flags", "delete_segments+append_list+omit_endlist",
            "-hls_allow_cache", "0",
            "-hls_start_number_source", "epoch",
            playlistPath
        ]);
        return arguments;
    }

    /// <summary>
    /// Creates arguments for an AAC-only shared CMAF DASH/HLS slate.
    /// </summary>
    public static IReadOnlyList<string> CreateCmafArguments(string channel, string manifestPath, ChannelSlateReason reason = ChannelSlateReason.ContentProtected)
    {
        var arguments = CreateCommonArguments(channel, reason);
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
            "-hls_master_name", CmafStreamPlanner.HlsManifestName,
            "-init_seg_name", "init-$RepresentationID$.mp4",
            "-media_seg_name", "chunk-$RepresentationID$-$Number%05d$.m4s",
            "-adaptation_sets", "id=0,streams=v id=1,streams=a",
            manifestPath
        ]);
        return arguments;
    }

    private static List<string> CreateCommonArguments(string channel, ChannelSlateReason reason)
    {
        var fontPath = OperatingSystem.IsWindows() ? "C\\:/Windows/Fonts/arial.ttf" : "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
        var title = reason == ChannelSlateReason.DisabledChannel ? "Disabled Channel" : "Content Protected";
        var filter = $"drawtext=fontfile='{fontPath}':text='{title} - Channel {EscapeFilterText(channel)}':fontcolor=white:fontsize=52:x=(w-text_w)/2:y=(h-text_h)/2";
        return [
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=c=0x20252b:s=1280x720:r=30",
            "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo",
            "-vf", filter,
            "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-pix_fmt", "yuv420p", "-g", "60",
            "-c:a", "aac", "-b:a", "128k", "-ac", "2", "-ar", "44100", "-shortest"
        ];
    }

    private static string EscapeFilterText(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(":", "\\:", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
    }
}
