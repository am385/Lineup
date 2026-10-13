using Lineup.Core.Settings;
using Lineup.Core.Storage;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;

namespace Lineup.Core.Guides;

/// <summary>
/// Refreshes configured guide sources and publishes the current virtual guide.
/// </summary>
public interface ITopologyGuideService
{
    /// <summary>Refreshes every enabled guide source without publishing XMLTV.</summary>
    Task<TopologyGuideRefreshResult> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Refreshes every enabled guide source and republishes XMLTV.</summary>
    Task<TopologyGuideRefreshResult> RefreshAndPublishAsync(CancellationToken cancellationToken = default);

    /// <summary>Publishes XMLTV from the currently cached guide data.</summary>
    Task<IReadOnlyList<GuideOutputPublicationResult>> PublishAsync(CancellationToken cancellationToken = default);

    /// <summary>Publishes one enabled guide output from currently cached guide data.</summary>
    Task<GuideOutputPublicationResult?> PublishAsync(
        GuideOutputId outputId,
        CancellationToken cancellationToken = default);
}

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
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var configuration = _settings.Settings.Streaming;
            await _guides.ApplyAsync(configuration, cancellationToken);
            var results = await _guides.RefreshAllAsync(cancellationToken);
            if (publish)
            {
                var outputs = await PublishAsync(configuration, cancellationToken);
                return new TopologyGuideRefreshResult { Sources = results, Outputs = outputs };
            }
            return new TopologyGuideRefreshResult { Sources = results };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GuideOutputPublicationResult>> PublishAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var configuration = _settings.Settings.Streaming;
            await _guides.ApplyAsync(configuration, cancellationToken);
            return await PublishAsync(configuration, cancellationToken);
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
            return await PublishOutputAsync(configuration, output, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<GuideOutputPublicationResult>> PublishAsync(
        StreamingConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var results = new List<GuideOutputPublicationResult>();
        foreach (var output in configuration.GuideOutputs.Where(output => output.Enabled))
        {
            results.Add(await PublishOutputAsync(configuration, output, cancellationToken));
        }
        return results;
    }

    private async Task<GuideOutputPublicationResult> PublishOutputAsync(
        StreamingConfiguration configuration,
        GuideOutputDefinition output,
        CancellationToken cancellationToken)
    {
        string? path = null;
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
                ProgramCount = snapshot.Channels.Sum(channel => channel.Programs.Count)
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
                Error = exception.Message
            };
        }
    }
}
