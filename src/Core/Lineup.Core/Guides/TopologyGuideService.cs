using Lineup.Core.Settings;
using Lineup.Core.Storage;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Extensibility.Streaming;
using Lineup.Core.Common.Configuration;

namespace Lineup.Core.Guides;

/// <summary>
/// Refreshes configured guide sources and publishes the current virtual guide.
/// </summary>
public interface ITopologyGuideService
{
    /// <summary>Gets bounded process-local refresh observations, without provider errors, credentials or new source calls.</summary>
    IReadOnlyList<GuideSourceRefreshObservation> SourceRefreshes => [];

    /// <summary>Refreshes every enabled guide source without publishing guide outputs.</summary>
    Task<TopologyGuideRefreshResult> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Refreshes every enabled guide source and republishes enabled guide outputs.</summary>
    Task<TopologyGuideRefreshResult> RefreshAndPublishAsync(CancellationToken cancellationToken = default);

    /// <summary>Refreshes selected sources from a captured configuration and optionally publishes the latest topology from committed cache.</summary>
    Task<TopologyGuideRefreshResult> RefreshSourcesAsync(StreamingConfiguration configuration, IReadOnlySet<GuideSourceId> sourceIds, bool publish, CancellationToken cancellationToken = default);

    /// <summary>Durably invalidates outputs before a channel mutation, then publishes its committed result without fetching providers.</summary>
    Task<IReadOnlyList<GuideOutputPublicationResult>> ApplyChannelChangeAsync(Func<CancellationToken, Task> mutation, CancellationToken cancellationToken = default);

    /// <summary>Retries due pending publications without refreshing guide sources.</summary>
    Task<IReadOnlyList<GuideOutputPublicationResult>> RetryPublicationsAsync(DateTime utcNow, CancellationToken cancellationToken = default);

    /// <summary>Publishes enabled guide outputs from eligible cached data without activating guide-source runtimes.</summary>
    Task<IReadOnlyList<GuideOutputPublicationResult>> PublishAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies and publishes one captured topology without mixing concurrent configuration revisions.</summary>
    Task<IReadOnlyList<GuideOutputPublicationResult>> PublishConfigurationAsync(StreamingConfiguration configuration, CancellationToken cancellationToken = default);

    /// <summary>Publishes one enabled guide output from eligible cached data without activating guide-source runtimes.</summary>
    Task<GuideOutputPublicationResult?> PublishAsync(
        GuideOutputId outputId,
        CancellationToken cancellationToken = default);
}

/// <summary>Retains the latest observed source attempt and successful refresh for its captured definition revision.</summary>
public sealed record GuideSourceRefreshObservation(GuideSourceId SourceId, string SourceRevision, bool Succeeded, DateTimeOffset ObservedAtUtc, DateTimeOffset? LastSuccessUtc);

/// <summary>
/// Summarizes one topology-aware guide refresh.
/// </summary>
public sealed record TopologyGuideRefreshResult
{
    /// <summary>Gets the per-source refresh results.</summary>
    public required IReadOnlyList<GuideRefreshResult> Sources { get; init; }

    /// <summary>Gets the number of successfully refreshed guide sources.</summary>
    public int SuccessfulSourceCount => Sources.Count(source => source.Succeeded);

    /// <summary>Gets the number of failed guide sources.</summary>
    public int FailedSourceCount => Sources.Count(source => !source.Succeeded);

    /// <summary>Gets the total number of channels imported by successful sources.</summary>
    public int ChannelCount => Sources.Where(source => source.Succeeded).Sum(source => source.ChannelCount);

    /// <summary>Gets the total number of programs imported by successful sources.</summary>
    public int ProgramCount => Sources.Where(source => source.Succeeded).Sum(source => source.ProgramCount);

    /// <summary>Gets per-output publication results when publication was requested.</summary>
    public IReadOnlyList<GuideOutputPublicationResult> Outputs { get; init; } = [];
}

/// <summary>
/// Describes one guide-output publication attempt.
/// </summary>
public sealed record GuideOutputPublicationResult
{
    /// <summary>Gets excluded sources affecting the attempted guide publication.</summary>
    public IReadOnlyList<GuideSourceDiagnostic> SourceDiagnostics { get; init; } = [];

    /// <summary>Gets whether configured source exclusions made the attempted guide partial.</summary>
    public bool IsPartial => SourceDiagnostics.Count > 0;

    /// <summary>Gets the configured output identity.</summary>
    public required GuideOutputId OutputId { get; init; }

    /// <summary>Gets the configured output name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets whether publication completed successfully.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Gets the resolved output path when available.</summary>
    public string? Path { get; init; }

    /// <summary>Gets the published channel count.</summary>
    public int ChannelCount { get; init; }

    /// <summary>Gets the published program count.</summary>
    public int ProgramCount { get; init; }

    /// <summary>Gets an actionable failure message.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Default topology-aware guide refresh and publication service.
/// </summary>
public sealed class TopologyGuideService : ITopologyGuideService
{
    private readonly ILineupSettingsService _settings;
    private readonly IGuideOrchestrator _guides;
    private readonly IGuideQueryService _guideQueries;
    private readonly IStreamingAdapterRegistry _adapters;
    private readonly IReadOnlyDictionary<string, IGuidePublisher> _publishers;
    private readonly IXmltvPublicationStore _publications;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Lock _observationsGate = new();
    private readonly Dictionary<GuideSourceId, GuideSourceRefreshObservation> _sourceRefreshes = [];

    /// <inheritdoc />
    public IReadOnlyList<GuideSourceRefreshObservation> SourceRefreshes
    {
        get
        {
            lock (_observationsGate)
            {
                return _sourceRefreshes.Values.OrderBy(observation => observation.SourceId.Value).ToArray();
            }
        }
    }

    /// <summary>Initializes a topology-aware guide service.</summary>
    public TopologyGuideService(
        ILineupSettingsService settings,
        IGuideOrchestrator guides,
        IGuideQueryService guideQueries,
        IStreamingAdapterRegistry adapters,
        IEnumerable<IGuidePublisher> publishers,
        IXmltvPublicationStore publications)
    {
        _settings = settings;
        _guides = guides;
        _guideQueries = guideQueries;
        _adapters = adapters;
        _publishers = publishers.ToDictionary(publisher => publisher.Kind, StringComparer.OrdinalIgnoreCase);
        _publications = publications;
    }

    /// <inheritdoc />
    public Task<TopologyGuideRefreshResult> RefreshAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(publish: false, cancellationToken);

    /// <inheritdoc />
    public Task<TopologyGuideRefreshResult> RefreshAndPublishAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(publish: true, cancellationToken);

    private async Task<TopologyGuideRefreshResult> RefreshAsync(bool publish, CancellationToken cancellationToken)
    {
        var configuration = _settings.Settings.Streaming;
        var sourceIds = configuration.GuideSources.Where(source => source.Enabled).Select(source => source.Id).ToHashSet();
        return await RefreshSourcesAsync(configuration, sourceIds, publish, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TopologyGuideRefreshResult> RefreshSourcesAsync(StreamingConfiguration configuration, IReadOnlySet<GuideSourceId> sourceIds, bool publish, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(sourceIds);
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var results = await _guides.RefreshSourcesAsync(configuration, sourceIds, cancellationToken);
            var observedAt = DateTimeOffset.UtcNow;
            lock (_observationsGate)
            {
                var configured = configuration.GuideSources.ToDictionary(source => source.Id);
                foreach (var removed in _sourceRefreshes.Keys.Where(id => !configured.ContainsKey(id)).ToArray())
                {
                    _sourceRefreshes.Remove(removed);
                }
                foreach (var result in results)
                {
                    if (configured.TryGetValue(result.SourceId, out var source))
                    {
                        var revision = ConfigurationRevision.Create(source);
                        _sourceRefreshes.TryGetValue(result.SourceId, out var previous);
                        _sourceRefreshes[result.SourceId] = new(result.SourceId, revision, result.Succeeded, observedAt,
                            result.Succeeded ? observedAt : previous?.SourceRevision == revision ? previous.LastSuccessUtc : null);
                    }
                }
            }
            if (publish)
            {
                var outputs = await PublishAsync(cancellationToken);
                return new TopologyGuideRefreshResult { Sources = results, Outputs = outputs };
            }
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await _guides.ApplyAsync(_settings.Settings.Streaming, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
            return new TopologyGuideRefreshResult { Sources = results };
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GuideOutputPublicationResult>> ApplyChannelChangeAsync(Func<CancellationToken, Task> mutation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var configuration = _settings.Settings.Streaming;
            await MarkPendingAsync(configuration.GuideOutputs.Where(output => output.Enabled));
            await mutation(cancellationToken);
            configuration = _settings.Settings.Streaming;
            await _guides.ApplyAsync(configuration, cancellationToken);
            await MarkPendingAsync(configuration.GuideOutputs.Where(output => output.Enabled));
            return await PublishOutputsAsync(configuration, configuration.GuideOutputs.Where(output => output.Enabled), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GuideOutputPublicationResult>> RetryPublicationsAsync(DateTime utcNow, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var configuration = _settings.Settings.Streaming;
            var due = configuration.GuideOutputs.Where(output => output.Enabled &&
                _settings.Settings.GuidePublicationRetries.TryGetValue(output.Id.Value, out var next) && next <= utcNow).ToArray();
            if (due.Length == 0)
            {
                return [];
            }
            await _guides.ApplyAsync(configuration, cancellationToken);
            return await PublishOutputsAsync(configuration, due, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GuideOutputPublicationResult>> PublishAsync(CancellationToken cancellationToken = default) =>
        PublishConfigurationCoreAsync(null, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<GuideOutputPublicationResult>> PublishConfigurationAsync(StreamingConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return PublishConfigurationCoreAsync(configuration, cancellationToken);
    }

    private async Task<IReadOnlyList<GuideOutputPublicationResult>> PublishConfigurationCoreAsync(StreamingConfiguration? configuration, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            configuration ??= _settings.Settings.Streaming;
            await _guides.ApplyAsync(configuration, cancellationToken);
            var outputs = configuration.GuideOutputs.Where(output => output.Enabled).ToArray();
            await MarkPendingAsync(outputs);
            return await PublishOutputsAsync(configuration, outputs, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<GuideOutputPublicationResult?> PublishAsync(
        GuideOutputId outputId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var configuration = _settings.Settings.Streaming;
            var output = configuration.GuideOutputs.SingleOrDefault(candidate => candidate.Enabled && candidate.Id == outputId);
            if (output is null)
            {
                return null;
            }

            await _guides.ApplyAsync(configuration, cancellationToken);
            await MarkPendingAsync([output]);
            return (await PublishOutputsAsync(configuration, [output], cancellationToken)).Single();
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task MarkPendingAsync(IEnumerable<GuideOutputDefinition> outputs)
    {
        var ids = outputs.Select(output => output.Id.Value).ToArray();
        return ids.Length == 0 ? Task.CompletedTask : _settings.UpdateAsync(settings =>
        {
            foreach (var id in ids)
            {
                settings.GuidePublicationRetries[id] = DateTime.UtcNow;
            }
        });
    }

    private async Task<IReadOnlyList<GuideOutputPublicationResult>> PublishOutputsAsync(StreamingConfiguration configuration, IEnumerable<GuideOutputDefinition> outputs, CancellationToken cancellationToken)
    {
        var results = new List<GuideOutputPublicationResult>();
        foreach (var output in outputs)
        {
            results.Add(await PublishOutputAsync(configuration, output, cancellationToken));
        }
        if (results.Count > 0)
        {
            await _settings.UpdateAsync(settings =>
            {
                foreach (var result in results)
                {
                    if (result.Succeeded && !result.IsPartial)
                    {
                        settings.GuidePublicationRetries.Remove(result.OutputId.Value);
                    }
                    else
                    {
                        settings.GuidePublicationRetries[result.OutputId.Value] = DateTime.UtcNow.AddHours(1);
                    }
                }
            });
        }
        return results;
    }

    private async Task<GuideOutputPublicationResult> PublishOutputAsync(
        StreamingConfiguration configuration,
        GuideOutputDefinition output,
        CancellationToken cancellationToken)
    {
        string? path = null;
        IReadOnlyList<GuideSourceDiagnostic> diagnostics = [];
        try
        {
            var driver = _adapters.GetGuideOutput(output.Adapter.Kind);
            var destination = driver.ResolveDestination(output.Adapter);
            path = destination.Path;
            if (!_publishers.TryGetValue(driver.PublisherKind, out var publisher))
            {
                throw new InvalidOperationException(
                    $"No guide publisher is registered for kind '{driver.PublisherKind}'.");
            }

            var snapshot = await _guideQueries.BuildOutputSnapshotAsync(configuration, output, cancellationToken: cancellationToken);
            diagnostics = snapshot.SourceDiagnostics;
            await _publications.PublishAsync(
                destination.Path,
                (stream, token) => publisher.PublishAsync(snapshot, stream, token),
                cancellationToken);
            return new GuideOutputPublicationResult
            {
                OutputId = output.Id,
                Name = output.Name,
                Succeeded = true,
                Path = destination.Path,
                ChannelCount = snapshot.Channels.Count,
                ProgramCount = snapshot.Channels.Sum(channel => channel.Programs.Count),
                SourceDiagnostics = diagnostics
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new GuideOutputPublicationResult
            {
                OutputId = output.Id,
                Name = output.Name,
                Succeeded = false,
                Path = path,
                Error = exception.Message,
                SourceDiagnostics = diagnostics
            };
        }
    }
}
