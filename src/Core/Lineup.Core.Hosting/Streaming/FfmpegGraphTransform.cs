using Lineup.Core.Streaming.FFmpeg;

namespace Lineup.Core.Hosting.Streaming;

/// <summary>Adapts a portable FFmpeg process output to the trusted streaming graph's ownership boundary.</summary>
internal sealed class FfmpegGraphTransform(FfmpegStreamTransform transform) : IStreamTransform
{
    /// <summary>Starts the FFmpeg graph transform asynchronously.</summary>
    /// <param name="input">The input value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The operation's result.</returns>
    public async ValueTask<IStreamSourceLease> StartAsync(Stream input, CancellationToken cancellationToken) =>
        new GraphOutputLease(await transform.StartAsync(input, cancellationToken));

    private sealed class GraphOutputLease(IMediaStreamLease output) : IStreamSourceLease
    {
        /// <summary>Gets the stream.</summary>
        public Stream Stream => output.Stream;

        /// <summary>Gets the content type.</summary>
        public string? ContentType => output.ContentType;

        /// <inheritdoc />
        public ValueTask DisposeAsync() => output.DisposeAsync();
    }
}
