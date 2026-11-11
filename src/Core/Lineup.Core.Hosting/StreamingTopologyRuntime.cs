using Lineup.Core.Common.Guides;
using Lineup.Core.Extensibility.Guides;
using Lineup.Core.Settings;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Extensibility.Streaming;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lineup.Core.Hosting;

/// <summary>
/// Applies persisted streaming topology at startup and after settings changes.
/// </summary>
public sealed class StreamingTopologyRuntime : BackgroundService, IStreamingTopologyRuntime
{
    private readonly ILineupSettingsService _settings;
    private readonly IPhysicalLineupService _lineups;
    private readonly ITopologyGuideService _guides;
    private readonly ILogger<StreamingTopologyRuntime> _logger;
    private readonly SemaphoreSlim _settingsChanged = new(0, 1);
    private readonly object _stateLock = new();
    private StreamingTopologyApplication _application;
    private TaskCompletionSource _applicationChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _retryRequested;
    private bool _stopped;

    /// <summary>Initializes the hosted topology runtime.</summary>
    public StreamingTopologyRuntime(
        ILineupSettingsService settings,
        IPhysicalLineupService lineups,
        ITopologyGuideService guides,
        ILogger<StreamingTopologyRuntime> logger)
    {
        _settings = settings;
        _lineups = lineups;
        _guides = guides;
        _logger = logger;
        _application = new StreamingTopologyApplication { DesiredRevision = ConfigurationRevision.Create(settings.Settings.Streaming) };
    }

    /// <inheritdoc />
    public StreamingTopologyApplication Application
    {
        get
        {
            lock (_stateLock)
            {
                return _application;
            }
        }
    }

    /// <inheritdoc />
    public event Action? ApplicationChanged;

    /// <inheritdoc />
    public void Retry()
    {
        lock (_stateLock)
        {
            if (_stopped)
            {
                throw new InvalidOperationException("The topology runtime has stopped.");
            }
        }
        Interlocked.Exchange(ref _retryRequested, 1);
        UpdateApplication(state => state with { Phase = TopologyApplicationPhase.Pending });
        Signal();
    }

    /// <inheritdoc />
    public async Task<StreamingTopologyApplication> WaitForApplicationAsync(string revision, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task changed;
            lock (_stateLock)
            {
                if (_stopped)
                {
                    throw new OperationCanceledException("The topology runtime has stopped.");
                }
                ConfigurationRevision.RequireMatch(revision, ConfigurationRevision.Create(_settings.Settings.Streaming));
                if (_application.DesiredRevision == revision && _application.AttemptedRevision == revision
                    && _application.Phase is TopologyApplicationPhase.Applied or TopologyApplicationPhase.Degraded)
                {
                    return _application;
                }
                changed = _applicationChanged.Task;
            }
            await changed.WaitAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _settings.OnSettingsChanged += SettingsChanged;
        SettingsChanged();
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _settings.OnSettingsChanged -= SettingsChanged;
        CompleteWaiters();
        await base.StopAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _settings.OnSettingsChanged -= SettingsChanged;
        CompleteWaiters();
        base.Dispose();
    }

    private void CompleteWaiters()
    {
        lock (_stateLock)
        {
            _stopped = true;
            _applicationChanged.TrySetCanceled();
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? lastAttempted = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var configuration = _settings.Settings.Streaming;
            var revision = ConfigurationRevision.Create(configuration);
            if (Interlocked.Exchange(ref _retryRequested, 0) != 0 || lastAttempted != revision || Application.Phase == TopologyApplicationPhase.Pending)
            {
                UpdateApplication(state =>
                {
                    var desiredRevision = ConfigurationRevision.Create(_settings.Settings.Streaming);
                    return state with
                    {
                        DesiredRevision = desiredRevision,
                        AttemptedRevision = revision,
                        Phase = desiredRevision == revision ? TopologyApplicationPhase.Applying : TopologyApplicationPhase.Pending,
                        Devices = [],
                        Outputs = []
                    };
                });
                var (devices, outputs, failed) = await ApplyAsync(configuration, stoppingToken);
                UpdateApplication(state =>
                {
                    var desiredRevision = ConfigurationRevision.Create(_settings.Settings.Streaming);
                    return state with
                    {
                        DesiredRevision = desiredRevision,
                        AppliedRevision = failed ? state.AppliedRevision : revision,
                        Phase = desiredRevision != revision ? TopologyApplicationPhase.Pending
                            : failed ? TopologyApplicationPhase.Degraded : TopologyApplicationPhase.Applied,
                        Devices = devices,
                        Outputs = outputs
                    };
                });
                lastAttempted = revision;
                if (ConfigurationRevision.Create(_settings.Settings.Streaming) != revision)
                {
                    continue;
                }
            }
            await _settingsChanged.WaitAsync(stoppingToken);
        }
    }

    private async Task<(IReadOnlyList<PhysicalLineupRefreshResult> Devices, IReadOnlyList<GuideOutputPublicationResult> Outputs, bool Failed)> ApplyAsync(
        StreamingConfiguration configuration,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PhysicalLineupRefreshResult> devices;
        try
        {
            devices = await _lineups.RefreshAsync(configuration, cancellationToken);
            foreach (var result in devices.Where(result => !result.Succeeded))
            {
                _logger.LogWarning("Physical lineup refresh failed for device {PhysicalDeviceId}: {Error}", result.DeviceId, result.Error);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to apply the configured physical streaming topology");
            return ([], [], true);
        }

        try
        {
            var outputs = await _guides.PublishConfigurationAsync(configuration, cancellationToken);
            foreach (var output in outputs.Where(output => !output.Succeeded || output.IsPartial))
            {
                _logger.LogWarning("Guide output {GuideOutputId} failed or is partial: {Error}; excluded sources: {ExcludedSourceCount}",
                    output.OutputId, output.Error, output.SourceDiagnostics.Count);
            }
            return (devices, outputs, devices.Any(device => !device.Succeeded) || outputs.Any(output => !output.Succeeded || output.IsPartial));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Unable to apply guide topology; physical streaming remains available");
            return (devices, [], true);
        }
    }

    private void SettingsChanged()
    {
        if (UpdateApplication(state =>
        {
            var revision = ConfigurationRevision.Create(_settings.Settings.Streaming);
            return state.DesiredRevision == revision ? state : state with { DesiredRevision = revision, Phase = TopologyApplicationPhase.Pending };
        }))
        {
            Signal();
        }
    }

    private bool UpdateApplication(Func<StreamingTopologyApplication, StreamingTopologyApplication> update)
    {
        lock (_stateLock)
        {
            if (_stopped)
            {
                return false;
            }
            var application = update(_application);
            if (ReferenceEquals(application, _application))
            {
                return false;
            }
            _application = application;
            var changed = _applicationChanged;
            _applicationChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            changed.TrySetResult();
        }
        foreach (var subscriber in ApplicationChanged?.GetInvocationList().Cast<Action>() ?? [])
        {
            try
            {
                subscriber();
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Topology application changed, but a status subscriber failed");
            }
        }
        return true;
    }

    private void Signal()
    {
        try
        {
            _settingsChanged.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }
}
