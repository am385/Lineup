using Lineup.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lineup.Core.Settings;

namespace Lineup.Core.Guides;

/// <summary>
/// Host-neutral orchestration for guide refresh, binding diagnostics, and virtual projection.
/// </summary>
public sealed class GuideOrchestrator : IGuideOrchestrator
{
    private readonly IStreamingAdapterRegistry _adapters;
    private readonly DeviceGuideStore _guideStore;
    private readonly ILogger<GuideOrchestrator> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _refreshSlots = new(4, 4);
    private ActiveConfiguration _active = new(new StreamingConfiguration(), [], []);

    /// <summary>Initializes a guide orchestrator.</summary>
    public GuideOrchestrator(IStreamingAdapterRegistry adapters, DeviceGuideStore guideStore, StreamingTopologyStore topologyStore, ILogger<GuideOrchestrator>? logger = null)
    {
        _adapters = adapters;
        _guideStore = guideStore;
        ArgumentNullException.ThrowIfNull(topologyStore);
        _logger = logger ?? NullLogger<GuideOrchestrator>.Instance;
    }

    /// <summary>Applies the guide orchestrator asynchronously.</summary>
    /// <param name="configuration">Configuration to apply.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the operation has finished.</returns>
    public async Task ApplyAsync(StreamingConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var errors = StreamingConfigurationValidator.ValidateForGuideConfiguration(configuration);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }

        var eligibility = GuideSourceSubscriptionResolver.ResolveEligibleSources(configuration.GuideSources, configuration.PhysicalDevices, _adapters, cancellationToken);
        foreach (var diagnostic in eligibility.Diagnostics)
        {
            _logger.LogWarning("Guide source {GuideSourceId} was excluded: {Reason}", diagnostic.SourceId, diagnostic.Code);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _active = new ActiveConfiguration(configuration, eligibility.Sources, eligibility.Diagnostics);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Refreshes all asynchronously.</summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The operation's result.</returns>
    public async Task<IReadOnlyList<GuideRefreshResult>> RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        ActiveConfiguration active;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            active = _active;
        }
        finally
        {
            _gate.Release();
        }

        return await RefreshAsync(active, null, cancellationToken);
    }

    /// <summary>Refreshes sources asynchronously.</summary>
    /// <param name="configuration">Configuration to apply.</param>
    /// <param name="sourceIds">The source IDs value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The operation's result.</returns>
    public async Task<IReadOnlyList<GuideRefreshResult>> RefreshSourcesAsync(StreamingConfiguration configuration, IReadOnlySet<GuideSourceId> sourceIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(sourceIds);
        var errors = StreamingConfigurationValidator.ValidateForGuideConfiguration(configuration);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }
        var eligibility = GuideSourceSubscriptionResolver.ResolveEligibleSources(configuration.GuideSources, configuration.PhysicalDevices, _adapters, cancellationToken);
        return await RefreshAsync(new ActiveConfiguration(configuration, eligibility.Sources, eligibility.Diagnostics), sourceIds, cancellationToken);
    }

    private async Task<IReadOnlyList<GuideRefreshResult>> RefreshAsync(ActiveConfiguration active, IReadOnlySet<GuideSourceId>? sourceIds, CancellationToken cancellationToken)
    {
        var sources = new List<(IGuideSource Source, GuideSourceScope Scope)>();
        var failures = active.SourceDiagnostics.Where(diagnostic => sourceIds is null || sourceIds.Contains(diagnostic.SourceId)).Select(diagnostic => new GuideRefreshResult
        {
            SourceId = diagnostic.SourceId,
            Succeeded = false,
            Error = diagnostic.Message,
            SourceDiagnostics = [diagnostic]
        }).ToList();
        foreach (var eligible in active.Sources)
        {
            if (sourceIds is not null && !sourceIds.Contains(eligible.Definition.Id))
            {
                continue;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var (definition, driver, scope) = eligible;
            try
            {
                var adapterErrors = driver.Validate(definition.Adapter);
                if (adapterErrors.Count > 0)
                {
                    throw new InvalidOperationException($"Guide source '{definition.Name}' is invalid: {string.Join(" ", adapterErrors)}");
                }

                var source = await driver.CreateAsync(definition.Id, definition.Adapter, scope.PhysicalDeviceIds.ToArray(), cancellationToken);
                if (source.Id != definition.Id)
                {
                    throw new InvalidOperationException($"Guide adapter '{driver.Kind}' returned a source with the wrong identity.");
                }
                sources.Add((source, scope));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or HttpRequestException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Unable to activate guide source {GuideSourceId}; other guide sources will continue", definition.Id);
                failures.Add(new GuideRefreshResult
                {
                    SourceId = definition.Id,
                    Succeeded = false,
                    Error = $"Guide source activation failed ({exception.GetType().Name}); the last committed import was preserved."
                });
            }
        }

        var refreshed = await Task.WhenAll(sources.Select(source => RefreshBoundedAsync(source.Source, source.Scope, cancellationToken)));
        return [.. failures, .. refreshed];
    }

    private async Task<GuideRefreshResult> RefreshBoundedAsync(IGuideSource source, GuideSourceScope scope, CancellationToken cancellationToken)
    {
        await _refreshSlots.WaitAsync(cancellationToken);
        try
        {
            return await RefreshAsync(source, scope, cancellationToken);
        }
        finally
        {
            _refreshSlots.Release();
        }
    }

    /// <summary>Builds snapshot asynchronously.</summary>
    /// <param name="virtualDeviceId">The virtual device ID value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The operation's result.</returns>
    public async Task<VirtualGuideSnapshot> BuildSnapshotAsync(VirtualDeviceId virtualDeviceId, CancellationToken cancellationToken = default)
    {
        ActiveConfiguration active;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            active = _active;
        }
        finally
        {
            _gate.Release();
        }

        var virtualDevice = active.Configuration.VirtualDevices.SingleOrDefault(device => device.Enabled && device.Id == virtualDeviceId)
            ?? throw new InvalidOperationException($"Virtual device {virtualDeviceId} is not active.");
        var physicalDevices = VirtualDeviceSubscriptionResolver.Resolve(
            virtualDevice,
            active.Configuration.PhysicalDevices,
            _adapters.GetVirtual(virtualDevice.Adapter.Kind));
        var snapshot = await _guideStore.BuildResolvedSnapshotAsync(
            virtualDeviceId,
            identities => ChannelTopologyResolver.Resolve(virtualDevice, physicalDevices, identities).Channels,
            active.Sources.Select(source => source.Scope).ToArray(), physicalDeviceIds: physicalDevices.Select(device => device.Id).ToArray(),
            enforceTransferLimits: true, cancellationToken: cancellationToken);
        return snapshot with
        {
            SourceDiagnostics = active.SourceDiagnostics,
            Generation = snapshot.Generation! with { ConfigurationRevision = ConfigurationRevision.Create(active.Configuration) }
        };
    }

    private async Task<GuideRefreshResult> RefreshAsync(IGuideSource source, GuideSourceScope scope, CancellationToken cancellationToken)
    {
        try
        {
            var import = await source.RefreshAsync(cancellationToken);
            if (import.SourceId != source.Id)
            {
                throw new InvalidDataException("The guide source returned an import with a different identity.");
            }

            await _guideStore.ImportAsync(import, cancellationToken);
            var bindings = await _guideStore.GetBindingDiagnosticsAsync(source.Id, cancellationToken, scope);
            return new GuideRefreshResult
            {
                SourceId = source.Id,
                Succeeded = true,
                ChannelCount = import.Channels.Count,
                ProgramCount = import.Programs.Count,
                Bindings = bindings
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Unable to refresh guide source {GuideSourceId}; the last committed import was preserved", source.Id);
            return new GuideRefreshResult
            {
                SourceId = source.Id,
                Succeeded = false,
                Error = $"Guide refresh failed ({exception.GetType().Name}); the last committed import was preserved."
            };
        }
    }

    private sealed record ActiveConfiguration(StreamingConfiguration Configuration, IReadOnlyList<EligibleGuideSource> Sources, IReadOnlyList<GuideSourceDiagnostic> SourceDiagnostics);
}
