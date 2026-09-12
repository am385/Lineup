using System.Diagnostics;

namespace Lineup.Web.Services;

/// <summary>
/// Generates synthetic media for DRM-protected tuner channels.
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
    Task StreamAsync(HostedStreamFormat format, string channel, Stream output, CancellationToken cancellationToken);

    /// <summary>
    /// Starts a synthetic HLS slate encoder.
    /// </summary>
    /// <param name="channel">The protected virtual channel.</param>
    /// <param name="playlistPath">The destination HLS playlist path.</param>
    /// <returns>The running FFmpeg process.</returns>
    Process StartHls(string channel, string playlistPath);
}

/// <summary>
/// Uses FFmpeg test sources to generate a protected-content slate.
/// </summary>
public sealed class ProtectedContentSlateService : IProtectedContentSlateService
{
    /// <inheritdoc />
    public async Task StreamAsync(HostedStreamFormat format, string channel, Stream output, CancellationToken cancellationToken)
    {
        if (format == HostedStreamFormat.Hls)
        {
            throw new ArgumentOutOfRangeException(nameof(format), format, "HLS slates must be started with StartHls.");
        }

        using var process = StartProcess(ProtectedContentSlatePlanner.CreatePipeArguments(format, channel));
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
    public Process StartHls(string channel, string playlistPath)
    {
        return StartProcess(ProtectedContentSlatePlanner.CreateHlsArguments(channel, playlistPath));
    }

    private static Process StartProcess(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
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
    public static IReadOnlyList<string> CreatePipeArguments(HostedStreamFormat format, string channel)
    {
        var arguments = CreateCommonArguments(channel);
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
    public static IReadOnlyList<string> CreateHlsArguments(string channel, string playlistPath)
    {
        var arguments = CreateCommonArguments(channel);
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

    private static List<string> CreateCommonArguments(string channel)
    {
        var fontPath = OperatingSystem.IsWindows() ? "C\\:/Windows/Fonts/arial.ttf" : "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
        var filter = $"drawtext=fontfile='{fontPath}':text='Content Protected - Channel {EscapeFilterText(channel)}':fontcolor=white:fontsize=52:x=(w-text_w)/2:y=(h-text_h)/2";
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
