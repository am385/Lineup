using System.Diagnostics;

namespace Lineup.Streaming.FFmpeg;

/// <summary>Identifies the message rendered by a synthetic channel slate.</summary>
public enum ChannelSlateReason
{
    /// <summary>The tuner channel is protected.</summary>
    ContentProtected,

    /// <summary>The channel was disabled in Lineup.</summary>
    DisabledChannel
}

/// <summary>Creates provider-neutral synthetic MPEG-TS channel slates.</summary>
public interface IChannelSlateStreamFactory
{
    /// <summary>Opens a readable synthetic MPEG-TS slate.</summary>
    Stream OpenMpegTs(string channel, ChannelSlateReason reason = ChannelSlateReason.ContentProtected);
}

/// <summary>Uses FFmpeg test sources to produce synthetic MPEG-TS channel slates.</summary>
public sealed class ChannelSlateStreamFactory : IChannelSlateStreamFactory
{
    private readonly IMediaProcessFactory _processFactory;

    /// <summary>Initializes the slate factory.</summary>
    public ChannelSlateStreamFactory(IMediaProcessFactory processFactory)
    {
        _processFactory = processFactory;
    }

    /// <inheritdoc />
    public Stream OpenMpegTs(string channel, ChannelSlateReason reason = ChannelSlateReason.ContentProtected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        Process process;
        try
        {
            process = _processFactory.Start(new MediaProcessRequest
            {
                FileName = "ffmpeg",
                Arguments = ChannelSlatePlanner.CreateMpegTsArguments(channel, reason),
                RedirectStandardOutput = true
            });
        }
        catch (MediaProcessException exception)
        {
            throw new MpegTsTranscodeException(exception.Message, exception);
        }
        return new ChannelSlateProcessStream(process);
    }

    private sealed class ChannelSlateProcessStream : Stream
    {
        private readonly Process _process;
        private readonly Task<string> _standardError;
        private int _disposed;

        public ChannelSlateProcessStream(Process process)
        {
            _process = process;
            _standardError = process.StandardError.ReadToEndAsync();
        }

        private Stream Inner => _process.StandardOutput.BaseStream;

        public override bool CanRead => Volatile.Read(ref _disposed) == 0;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Inner.Read(buffer, offset, count);
            ValidateEndOfStreamAsync(read).GetAwaiter().GetResult();
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await Inner.ReadAsync(buffer, cancellationToken);
            await ValidateEndOfStreamAsync(read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
            await _process.WaitForExitAsync(CancellationToken.None);
            await ObserveErrorAsync();
            _process.Dispose();
            GC.SuppressFinalize(this);
        }

        private async Task ValidateEndOfStreamAsync(int bytesRead)
        {
            if (bytesRead != 0)
            {
                return;
            }
            await _process.WaitForExitAsync(CancellationToken.None);
            if (_process.ExitCode != 0)
            {
                throw new MpegTsTranscodeException($"Channel slate FFmpeg exited with code {_process.ExitCode}: {(await _standardError).Trim()}");
            }
        }

        private async Task ObserveErrorAsync()
        {
            try
            {
                await _standardError;
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}

/// <summary>Builds deterministic FFmpeg arguments and metadata for synthetic channel slates.</summary>
public static class ChannelSlatePlanner
{
    /// <summary>Creates common synthetic audio and video arguments for a channel slate.</summary>
    public static IReadOnlyList<string> CreateBaseArguments(string channel, ChannelSlateReason reason = ChannelSlateReason.ContentProtected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        var fontPath = OperatingSystem.IsWindows() ? "C\\:/Windows/Fonts/arial.ttf" : "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
        var title = reason == ChannelSlateReason.DisabledChannel ? "Disabled Channel" : "Content Protected";
        var filter = $"drawtext=fontfile='{fontPath}':text='{title} - Channel {EscapeFilterText(channel)}':fontcolor=white:fontsize=52:x=(w-text_w)/2:y=(h-text_h)/2";
        return [
            "-hide_banner", "-loglevel", "error",
            "-re", "-f", "lavfi", "-i", "color=c=0x20252b:s=1280x720:r=30",
            "-re", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo",
            "-vf", filter,
            "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-pix_fmt", "yuv420p", "-g", "60",
            "-c:a", "aac", "-b:a", "128k", "-ac", "2", "-ar", "44100", "-shortest"
        ];
    }

    /// <summary>Creates pipe-based MPEG-TS arguments for a channel slate.</summary>
    public static IReadOnlyList<string> CreateMpegTsArguments(string channel, ChannelSlateReason reason = ChannelSlateReason.ContentProtected) =>
    [
        .. CreateBaseArguments(channel, reason),
        "-mpegts_flags", "+resend_headers",
        "-f", "mpegts",
        "pipe:1"
    ];

    /// <summary>Creates the normalized media description emitted by every channel slate.</summary>
    public static MediaProbeResult CreateMedia() =>
        new(
            [
                new MediaTrackMetadata(0, MediaTrackType.Video, "h264", null, 1280, 720, null, null),
                new MediaTrackMetadata(1, MediaTrackType.Audio, "aac", 128_000, null, null, 2, 44_100)
            ],
            null);

    private static string EscapeFilterText(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(":", "\\:", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
    }
}
