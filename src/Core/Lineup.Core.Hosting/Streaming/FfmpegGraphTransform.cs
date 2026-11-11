using Lineup.Core.Streaming.FFmpeg;

namespace Lineup.Core.Hosting.Streaming;

/// <summary>Adapts a portable FFmpeg process output to the trusted streaming graph's ownership boundary.</summary>
internal sealed class FfmpegGraphTransform(FfmpegStreamTransform transform) : IStreamTransform
{
    /// <inheritdoc />
    public async ValueTask<IStreamSourceLease> StartAsync(Stream input, CancellationToken cancellationToken) =>
        new GraphOutputLease(await transform.StartAsync(input, cancellationToken));

    private sealed class GraphOutputLease(IMediaStreamLease output) : IStreamSourceLease
    {
        /// <inheritdoc />
        public Stream Stream => output.Stream;

        /// <inheritdoc />
        public string? ContentType => output.ContentType;

        /// <inheritdoc />
        public ValueTask DisposeAsync() => output.DisposeAsync();
    }
}
