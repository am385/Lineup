using Lineup.Core.Extensibility.Streaming;

namespace Lineup.Plugin.SiliconDust.HDHomeRun.PhysicalTuner;

/// <summary>
/// Owns an HDHomeRun HTTP response and its finite-capacity slot.
/// </summary>
internal sealed class HDHomeRunPhysicalStreamLease : IPhysicalStreamLease
{
    private readonly HttpResponseMessage _response;
    private readonly SemaphoreSlim _capacity;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="HDHomeRunPhysicalStreamLease"/> class.
    /// </summary>
    /// <param name="stream">Readable response stream.</param>
    /// <param name="response">Owning HTTP response.</param>
    /// <param name="capacity">Capacity semaphore to release.</param>
    internal HDHomeRunPhysicalStreamLease(Stream stream, HttpResponseMessage response, SemaphoreSlim capacity)
    {
        Stream = stream;
        _response = response;
        _capacity = capacity;
        ContentType = response.Content.Headers.ContentType?.MediaType;
    }

    /// <summary>Gets the stream.</summary>
    public Stream Stream { get; }

    /// <summary>Gets the content type.</summary>
    public string? ContentType { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await CloseResponseAsync(Stream, _response, _capacity);
    }

    /// <summary>Closes the HTTP response before making its tuner slot available, including failed stream startup or disposal.</summary>
    internal static async ValueTask CloseResponseAsync(Stream? stream, HttpResponseMessage? response, SemaphoreSlim capacity)
    {
        try
        {
            if (stream is not null)
            {
                await stream.DisposeAsync();
            }
        }
        finally
        {
            try
            {
                response?.Dispose();
            }
            finally
            {
                capacity.Release();
            }
        }
    }
}
