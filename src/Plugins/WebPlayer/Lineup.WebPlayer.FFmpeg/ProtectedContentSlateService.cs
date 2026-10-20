using System.Diagnostics;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.Streaming.FFmpeg;

namespace Lineup.WebPlayer.FFmpeg;

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
    private readonly IMediaProcessFactory _processFactory;

    /// <summary>Initializes the service with the default media-process factory.</summary>
    public ProtectedContentSlateService()
        : this(new MediaProcessFactory())
    {
    }

    /// <summary>Initializes the service with a host-provided media-process factory.</summary>
    public ProtectedContentSlateService(IMediaProcessFactory processFactory)
    {
        _processFactory = processFactory;
    }

    /// <inheritdoc />
    public async Task StreamAsync(HostedStreamFormat format, string channel, Stream output, CancellationToken cancellationToken, ChannelSlateReason reason = ChannelSlateReason.ContentProtected)
    {
        if (format == HostedStreamFormat.Cmaf)
        {
            throw new ArgumentOutOfRangeException(nameof(format), format, "CMAF slates must be started with StartCmaf.");
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

    private Process StartProcess(IReadOnlyList<string> arguments, string? workingDirectory = null)
    {
        try
        {
            return _processFactory.Start(new MediaProcessRequest
            {
                FileName = "ffmpeg",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true
            });
        }
        catch (MediaProcessException ex)
        {
            throw new MpegTsTranscodeException(ex.Message, ex);
        }
    }
}

/// <summary>
/// Builds deterministic FFmpeg arguments for protected-content slates.
/// </summary>
public static class ProtectedContentSlatePlanner
{
    /// <summary>
    /// Creates arguments for a pipe-based MPEG-TS slate.
    /// </summary>
    public static IReadOnlyList<string> CreatePipeArguments(HostedStreamFormat format, string channel, ChannelSlateReason reason = ChannelSlateReason.ContentProtected)
    {
        if (format == HostedStreamFormat.MpegTs)
        {
            return ChannelSlatePlanner.CreateMpegTsArguments(channel, reason);
        }
        throw new ArgumentOutOfRangeException(nameof(format), format, "Only MPEG-TS supports pipe output.");
    }

    /// <summary>
    /// Creates arguments for an HLS protected-content slate.
    /// </summary>
    public static IReadOnlyList<string> CreateHlsArguments(string channel, string playlistPath, ChannelSlateReason reason = ChannelSlateReason.ContentProtected)
    {
        var arguments = ChannelSlatePlanner.CreateBaseArguments(channel, reason).ToList();
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
        var arguments = ChannelSlatePlanner.CreateBaseArguments(channel, reason).ToList();
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

}
