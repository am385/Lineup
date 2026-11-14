namespace Lineup.Core.Streaming;

/// <summary>Reports deployment-wide media reservations and their effective restart-scoped limits.</summary>
public sealed record MediaWorkDiagnostics(int Producers, int MaximumProducers, int Processing, int MaximumProcessing, int MinimumFreeSpaceMib)
{
    /// <summary>Gets the scope shared by management and every authenticated dedicated worker.</summary>
    public string EnforcementScope => "deployment";
}

/// <summary>Owns atomic deployment-wide reservations, including broker-owned dedicated-worker media.</summary>
public sealed class MediaWorkAdmission(StreamingResourceOptions options) : IMediaWorkAdmission
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];

    /// <summary>Executes acquire asynchronously.</summary>
    /// <param name="kind">The kind value.</param>
    /// <param name="retireIdle">The retire idle value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The operation's result.</returns>
    public async ValueTask<IAsyncDisposable> AcquireAsync(MediaWorkKind kind, Func<CancellationToken, Task<bool>>? retireIdle = null, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var limit = kind == MediaWorkKind.Producer ? options.MaximumProducers : options.MaximumProcessingOperations;
                if (limit == 0 || _entries.Values.Count(entry => entry.Kind == kind) < limit)
                {
                    var id = Guid.NewGuid();
                    _entries.Add(id, new(kind, retireIdle));
                    return new Lease(this, id);
                }
            }
            if (attempt == 0)
            {
                await ReclaimIdleAsync(cancellationToken);
            }
        }
        throw new MediaResourceLimitException(kind == MediaWorkKind.Producer ? "media_producer_limit" : "media_processing_limit",
            $"The deployment-wide {kind.ToString().ToLowerInvariant()} limit has been reached.");
    }

    /// <summary>Executes reclaim idle asynchronously.</summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the operation has finished.</returns>
    public async Task ReclaimIdleAsync(CancellationToken cancellationToken = default)
    {
        Entry[] entries;
        lock (_gate)
        {
            entries = _entries.Values.Where(entry => entry.RetireIdle is not null).ToArray();
        }
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await entry.RetireIdle!(cancellationToken);
        }
    }

    /// <summary>Executes check workspace.</summary>
    /// <param name="path">Path used by the operation.</param>
    public void CheckWorkspace(string path) => MediaWorkspaceAdmission.Check(path, options.MinimumMediaFreeSpaceMib);

    /// <summary>Gets immutable usage and effective limits for management diagnostics.</summary>
    public MediaWorkDiagnostics GetDiagnostics()
    {
        lock (_gate)
        {
            return new(_entries.Values.Count(entry => entry.Kind == MediaWorkKind.Producer), options.MaximumProducers,
                _entries.Values.Count(entry => entry.Kind == MediaWorkKind.Processing), options.MaximumProcessingOperations, options.MinimumMediaFreeSpaceMib);
        }
    }

    private sealed record Entry(MediaWorkKind Kind, Func<CancellationToken, Task<bool>>? RetireIdle);

    private sealed class Lease(MediaWorkAdmission owner, Guid id) : IAsyncDisposable
    {
        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            lock (owner._gate)
            {
                owner._entries.Remove(id);
            }
            return ValueTask.CompletedTask;
        }
    }
}
