using Lineup.Core.Storage;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Extensibility.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lineup.Core.Guides;

/// <summary>
/// Host-neutral orchestration for guide refresh, binding diagnostics, and virtual projection.
/// </summary>
public sealed class GuideOrchestrator : IGuideOrchestrator
{
    private readonly IStreamingAdapterRegistry _adapters;
    private readonly DeviceGuideStore _guideStore;
    private readonly StreamingTopologyStore _topologyStore;
    private readonly ILogger<GuideOrchestrator> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ActiveConfiguration _active = new(new StreamingConfiguration(), [], []);

    /// <summary>Initializes a guide orchestrator.</summary>
    public GuideOrchestrator(
        IStreamingAdapterRegistry adapters,
        DeviceGuideStore guideStore,
        StreamingTopologyStore topologyStore,
        ILogger<GuideOrchestrator>? logger = null)
    {
        _adapters = adapters;
        _guideStore = guideStore;
        _topologyStore = topologyStore;
        _logger = logger ?? NullLogger<GuideOrchestrator>.Instance;
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
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

        var sources = new List<(IGuideSource Source, GuideSourceScope Scope)>();
        var failures = active.SourceDiagnostics.Select(diagnostic => new GuideRefreshResult
        {
            SourceId = diagnostic.SourceId,
            Succeeded = false,
            Error = diagnostic.Message,
            SourceDiagnostics = [diagnostic]
        }).ToList();
        foreach (var eligible in active.Sources)
        {
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

        var refreshed = await Task.WhenAll(sources.Select(source => RefreshAsync(source.Source, source.Scope, cancellationToken)));
        return [.. failures, .. refreshed];
    }

    /// <inheritdoc />
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
        var identities = await _topologyStore.ReadActiveAsync(cancellationToken);
        var resolution = ChannelTopologyResolver.Resolve(virtualDevice, physicalDevices, identities);
        var snapshot = await _guideStore.BuildSnapshotAsync(
            virtualDeviceId, resolution.Channels, active.Sources.Select(source => source.Scope).ToArray(), cancellationToken: cancellationToken);
        return snapshot with { SourceDiagnostics = active.SourceDiagnostics };
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
            return new GuideRefreshResult
            {
                SourceId = source.Id,
                Succeeded = false,
                Error = $"Guide refresh failed ({exception.GetType().Name}); the last committed import was preserved."
            };
        }
    }

    private sealed record ActiveConfiguration(
        StreamingConfiguration Configuration,
        IReadOnlyList<EligibleGuideSource> Sources,
        IReadOnlyList<GuideSourceDiagnostic> SourceDiagnostics);
}
