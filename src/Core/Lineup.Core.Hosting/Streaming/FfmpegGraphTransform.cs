using Lineup.Core.Streaming.FFmpeg;

namespace Lineup.Core.Hosting.Streaming;

/// <summary>Adapts a portable FFmpeg process output to the trusted streaming graph's ownership boundary.</summary>
internal sealed class FfmpegGraphTransform(FfmpegStreamTransform transform, string resourceRole = "media") : IStreamTransform
{
    /// <summary>Starts the FFmpeg graph transform asynchronously.</summary>
    /// <param name="input">The input value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The operation's result.</returns>
    public async ValueTask<IStreamSourceLease> StartAsync(Stream input, CancellationToken cancellationToken)
    {
        var output = await transform.StartAsync(input, cancellationToken);
        foreach (var id in output.ResourceIds)
        {
            ProcessResourceRegistry.Local.SetRole(id, resourceRole);
        }
        return new GraphOutputLease(output);
    }

    private sealed class GraphOutputLease(IMediaStreamLease output) : IStreamSourceLease
    {
        /// <summary>Gets the stream.</summary>
        public Stream Stream => output.Stream;

        /// <summary>Gets the content type.</summary>
        public string? ContentType => output.ContentType;

        /// <inheritdoc />
        public VideoEncodingSnapshot? VideoEncoding => output.VideoEncoding;

        /// <inheritdoc />
        public StreamProcessingSnapshot? Processing => output.Processing;

        /// <inheritdoc />
        public IReadOnlyList<string> ResourceIds => output.ResourceIds;

        /// <inheritdoc />
        public ValueTask DisposeAsync() => output.DisposeAsync();
    }
}
