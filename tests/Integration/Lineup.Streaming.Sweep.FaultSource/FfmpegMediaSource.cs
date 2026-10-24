using System.Diagnostics;

namespace Lineup.Streaming.Sweep.FaultSource;

internal static class FfmpegMediaSource
{
    public static async Task WriteDelayedAudioAsync(Stream destination, CancellationToken cancellationToken)
    {
        await CopyOutputAsync(
            destination,
            cancellationToken,
            "-hide_banner", "-loglevel", "error", "-re",
            "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30",
            "-t", "4",
            "-map", "0:v:0",
            "-c:v", "mpeg2video", "-g", "15", "-bf", "0",
            "-f", "mpegts", "pipe:1");
        await CopyOutputAsync(
            destination,
            cancellationToken,
            "-hide_banner", "-loglevel", "error", "-re",
            "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30",
            "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000",
            "-t", "90",
            "-map", "0:v:0", "-map", "1:a:0",
            "-c:v", "mpeg2video", "-g", "15", "-bf", "0",
            "-c:a", "ac3", "-b:a", "192k",
            "-output_ts_offset", "4",
            "-f", "mpegts", "pipe:1");
    }

    public static Task WriteVideoOnlyAsync(Stream destination, CancellationToken cancellationToken)
    {
        return CopyOutputAsync(
            destination,
            cancellationToken,
            "-hide_banner", "-loglevel", "error", "-re",
            "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30",
            "-t", "90",
            "-map", "0:v:0",
            "-c:v", "mpeg2video", "-g", "15", "-bf", "0",
            "-f", "mpegts", "pipe:1");
    }

    private static async Task CopyOutputAsync(Stream destination, CancellationToken cancellationToken, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start FFmpeg.");
        }

        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(destination, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"FFmpeg exited with code {process.ExitCode}: {await standardError}");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }
}
