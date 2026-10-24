using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;

namespace Lineup.SiliconDust.HDHomeRun.PhysicalTuner;

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

    /// <inheritdoc />
    public Stream Stream { get; }

    /// <inheritdoc />
    public string? ContentType { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await Stream.DisposeAsync();
        }
        finally
        {
            _response.Dispose();
            _capacity.Release();
        }
    }
}
