using Lineup.Core.VirtualOutputs.Runtime;

namespace Lineup.App.Web.Services;

/// <summary>One fixed role's observation; null values indicate incomplete observation, not idle work.</summary>
public sealed record ResourceUsage(double? CpuPercent, long? ResidentBytes);

/// <summary>One bounded aggregate history point, independent of viewer and session cardinality.</summary>
public sealed record ResourceHistoryPoint(DateTimeOffset AtUtc, ResourceUsage Total, ResourceUsage Management, ResourceUsage Workers, ResourceUsage Media, bool Complete)
{
    /// <summary>Gets whole-device GPU readings, which are never included in the process totals.</summary>
    public IReadOnlyList<GpuResourceSample> Gpus { get; init; } = [];
}

/// <summary>A cached dashboard observation with explicit collection and coverage state.</summary>
public sealed record DashboardResourceSnapshot(bool Collecting, IReadOnlyList<ProcessResourceSample> Processes, IReadOnlyList<ResourceHistoryPoint> History, GpuResourceSnapshot? Gpu = null);

/// <summary>Shares lightweight collection and rolling history between all dashboards in this deployment.</summary>
public sealed class DashboardResourceMonitor : BackgroundService
{
    private readonly IWebSettingsService _settings;
    private readonly IVirtualOutputRuntimeStatus _workers;
    private readonly ILogger<DashboardResourceMonitor> _logger;
    private readonly ProcessResourceRegistry _registry;
    private readonly TimeProvider _clock;
    private readonly IGpuResourceSource? _gpu;
    private readonly object _collectionGate = new();
    private readonly object _gate = new();
    private readonly Dictionary<Guid, DateTimeOffset> _visible = [];
    private readonly Queue<ResourceHistoryPoint> _history = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private IReadOnlyList<ProcessResourceSample> _processes = [];
    private IReadOnlyList<ResourceHistoryPoint> _cachedHistory = [];
    private bool _historyChanged = true;
    private bool _collecting;
    private bool _numericOverflow;
    private bool _overflowReported;
    private GpuResourceSnapshot _gpuSnapshot = new([], "warming_up");

    /// <summary>Initializes a shared owner with replaceable process counters and time source.</summary>
    public DashboardResourceMonitor(
        IWebSettingsService settings,
        IVirtualOutputRuntimeStatus workers,
        ILogger<DashboardResourceMonitor> logger,
        ProcessResourceRegistry? registry = null,
        TimeProvider? clock = null,
        IGpuResourceSource? gpu = null
    )
    {
        _settings = settings;
        _workers = workers;
        _logger = logger;
        _registry = registry ?? ProcessResourceRegistry.Local;
        _clock = clock ?? TimeProvider.System;
        _gpu = gpu;
    }

    /// <summary>Renews or releases one foreground dashboard's bounded interest, without touching media lifetime.</summary>
    public void SetVisible(Guid dashboardId, bool visible)
    {
        lock (_gate)
        {
            if (visible)
            {
                _visible[dashboardId] = _clock.GetUtcNow().AddSeconds(8);
            }
            else
            {
                _visible.Remove(dashboardId);
            }
        }
        Wake();
    }

    /// <summary>Reads cached observations only; never measures processes or refreshes providers.</summary>
    public DashboardResourceSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            TrimHistory();
            if (_historyChanged)
            {
                _cachedHistory = _history.ToArray();
                _historyChanged = false;
            }
            return new(_collecting, _collecting ? _processes : [], _cachedHistory, _gpuSnapshot);
        }
    }

    /// <summary>Performs one serialized collection tick; exposed for deterministic counter/lifecycle verification.</summary>
    public void Collect()
    {
        lock (_collectionGate)
        {
            var point = CollectProcesses(out var resetGpu);
            if (resetGpu) _gpu?.ResetBaseline();
            if (point is null) return;
            // Driver calls must not hold the cached-snapshot lock or block dashboard rendering.
            var gpu = _gpu?.Read() ?? new GpuResourceSnapshot([], "unsupported");
            lock (_gate)
            {
                _gpuSnapshot = gpu;
                _history.Enqueue(point with { Gpus = gpu.Devices });
                _historyChanged = true;
                TrimHistory();
            }
        }
    }

    private ResourceHistoryPoint? CollectProcesses(out bool resetGpu)
    {
        resetGpu = false;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            foreach (var id in _visible.Where(item => item.Value <= now).Select(item => item.Key).ToArray())
            {
                _visible.Remove(id);
            }
            var requested = !_settings.Settings.MonitorResourcesOnlyWhenVisible || _visible.Count > 0;
            WorkerResourceReports.SetRequested(requested);
            if (!requested)
            {
                if (_collecting)
                {
                    _registry.ResetBaselines();
                    resetGpu = true;
                    _history.Enqueue(new(now, new(null, null), new(null, null), new(null, null), new(null, null), false));
                    _historyChanged = true;
                }
                _collecting = false;
                _processes = [];
                _gpuSnapshot = new([], "paused");
                TrimHistory();
                return null;
            }
            _collecting = true;
            var processes = _registry.Sample().Where(item => item.Role != "remote").ToList();
            var owners = _workers.GetSnapshots().Where(item => item.Generation is not null && item.State is
                VirtualOutputRuntimeState.Starting or VirtualOutputRuntimeState.Healthy or VirtualOutputRuntimeState.Degraded or VirtualOutputRuntimeState.Draining)
                .Select(item => $"{item.VirtualDeviceId}:{item.Generation}").ToHashSet(StringComparer.Ordinal);
            var reports = WorkerResourceReports.Read(owners);
            foreach (var report in reports)
            {
                var stale = now - report.ReceivedAtUtc > TimeSpan.FromSeconds(6);
                processes.AddRange(report.Processes.Select(item => item with
                {
                    ObservedAtUtc = report.ReceivedAtUtc,
                    CpuPercent = stale ? null : item.CpuPercent,
                    ResidentBytes = stale ? null : item.ResidentBytes,
                    UnavailableReason = stale ? "stale" : item.UnavailableReason
                }));
            }
            _processes = processes.DistinctBy(item => item.Id).ToArray();
            var workersMissing = owners.Count < _registry.RemoteProcessCount || owners.Count != reports.Count ||
                reports.Any(item => now - item.ReceivedAtUtc > TimeSpan.FromSeconds(6));
            var incompleteInterval = _registry.LastSampleIncomplete || reports.Any(item => item.IncompleteInterval);
            _numericOverflow = false;
            var total = Sum(_processes);
            if (workersMissing)
            {
                total = new(null, null);
            }
            else if (incompleteInterval)
            {
                total = total with { CpuPercent = null };
            }
            var media = Sum(_processes.Where(item => item.Role is not ("management" or "worker")));
            if (workersMissing)
            {
                media = new(null, null);
            }
            else if (incompleteInterval)
            {
                media = media with { CpuPercent = null };
            }
            var point = new ResourceHistoryPoint(now, total, Sum(_processes.Where(item => item.Role == "management")),
                workersMissing ? new(null, null) : Sum(_processes.Where(item => item.Role == "worker")),
                media,
                !workersMissing && !incompleteInterval && !_numericOverflow && _processes.All(item => item.UnavailableReason is null));
            if (_numericOverflow && !_overflowReported)
            {
                _logger.LogWarning("Resource monitoring received counters whose aggregate exceeds the supported numeric range; affected totals are unavailable.");
            }
            _overflowReported = _numericOverflow;
            return point;
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var host = _registry.Register(Environment.ProcessId, "management");
        _registry.ObservationFailed += LogFailure;
        _registry.Attach(host);
        _settings.OnSettingsChanged += Wake;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Collect();
                if (_collecting)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), _clock, stoppingToken);
                }
                else
                {
                    await _wake.WaitAsync(stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _settings.OnSettingsChanged -= Wake;
            _registry.ObservationFailed -= LogFailure;
            _registry.Unregister(host);
            WorkerResourceReports.SetRequested(false);
            _gpu?.ResetBaseline();
        }
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // Another dashboard already scheduled the single pending wakeup.
            }
        }
    }

    private void LogFailure(string message) => _logger.LogWarning("Resource monitoring: {ObservationFailure}", message);

    private void TrimHistory()
    {
        var cutoff = _clock.GetUtcNow().AddMinutes(-_settings.Settings.ResourceHistoryMinutes);
        while (_history.Count > 0 && (_history.Peek().AtUtc <= cutoff || _history.Count > 1800))
        {
            _history.Dequeue();
            _historyChanged = true;
        }
    }

    private ResourceUsage Sum(IEnumerable<ProcessResourceSample> processes)
    {
        double? cpu = 0;
        long? resident = 0;
        foreach (var process in processes)
        {
            if (cpu is { } previousCpu && process.CpuPercent is { } currentCpu && (!double.IsFinite(previousCpu + currentCpu) || currentCpu < 0))
            {
                _numericOverflow = true;
                cpu = null;
            }
            else
            {
                cpu += process.CpuPercent;
            }
            if (resident is { } previousBytes && process.ResidentBytes is { } currentBytes && (currentBytes < 0 || previousBytes > long.MaxValue - currentBytes))
            {
                _numericOverflow = true;
                resident = null;
            }
            else
            {
                resident += process.ResidentBytes;
            }
        }
        return new(cpu, resident);
    }
}
