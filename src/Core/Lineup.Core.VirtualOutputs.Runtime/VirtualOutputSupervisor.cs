using System.Collections.Concurrent;
using System.Text.Json;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Settings;
using Lineup.Core.VirtualOutputs.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lineup.Core.VirtualOutputs.Runtime;

/// <summary>Configures package discovery and the local capability-broker endpoint.</summary>
public sealed record VirtualOutputRuntimeOptions
{
    /// <summary>Gets the trusted root containing installed worker packages and declarative manifests.</summary>
    public required string WorkerPackageRoot { get; init; }

    /// <summary>Gets the local capability-broker endpoint.</summary>
    public required VirtualOutputIpcEndpoint IpcEndpoint { get; init; }

    /// <summary>Gets the lifetime of one-time worker bootstrap credentials.</summary>
    public TimeSpan BootstrapLifetime { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets the lease lifetime for shared runtime services. Virtual-output workers use their configured worker-policy lease lifetime.</summary>
    public TimeSpan LeaseLifetime { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the maximum number of concurrent per-output reconciliation or recovery transitions.</summary>
    public int MaximumConcurrentTransitions { get; init; } = 4;

    /// <summary>Gets the shared graceful-drain budget before all remaining managed children are forcibly cleaned up.</summary>
    public TimeSpan ShutdownGracePeriod { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Contains a one-time bootstrap descriptor for an externally supervised worker.</summary>
public sealed record VirtualOutputExternalEnrollment
{
    /// <summary>Gets the assigned virtual device.</summary>
    public required VirtualDeviceId VirtualDeviceId { get; init; }

    /// <summary>Gets the sensitive bootstrap descriptor that may be retrieved once.</summary>
    public required VirtualOutputWorkerBootstrap Bootstrap { get; init; }
}

/// <summary>Stores one-time external-worker enrollment descriptors for operator-controlled delivery.</summary>
public interface IVirtualOutputExternalEnrollmentStore
{
    /// <summary>Stores or replaces one pending enrollment and its credential-renewal factory.</summary>
    void Set(
        VirtualOutputExternalEnrollment enrollment,
        Func<VirtualOutputExternalEnrollment?> renewalFactory);

    /// <summary>Retrieves one pending enrollment, renewing its credentials when the prior descriptor was retrieved or expired.</summary>
    VirtualOutputExternalEnrollment? Take(VirtualDeviceId virtualDeviceId);

    /// <summary>Removes a pending enrollment.</summary>
    bool Remove(VirtualDeviceId virtualDeviceId);
}

/// <summary>Process-local one-time external-worker enrollment store.</summary>
public sealed class VirtualOutputExternalEnrollmentStore : IVirtualOutputExternalEnrollmentStore
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<VirtualDeviceId, EnrollmentState> _enrollments = [];

    /// <summary>Initializes an external-worker enrollment store.</summary>
    public VirtualOutputExternalEnrollmentStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public void Set(
        VirtualOutputExternalEnrollment enrollment,
        Func<VirtualOutputExternalEnrollment?> renewalFactory)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        ArgumentNullException.ThrowIfNull(renewalFactory);
        lock (_gate)
        {
            _enrollments[enrollment.VirtualDeviceId] = new EnrollmentState(enrollment, renewalFactory);
        }
    }

    /// <inheritdoc />
    public VirtualOutputExternalEnrollment? Take(VirtualDeviceId virtualDeviceId)
    {
        lock (_gate)
        {
            if (!_enrollments.TryGetValue(virtualDeviceId, out var state))
            {
                return null;
            }

            var enrollment = state.Pending;
            if (enrollment is null || _timeProvider.GetUtcNow() > enrollment.Bootstrap.BootstrapExpiresUtc)
            {
                enrollment = state.RenewalFactory();
            }
            state.Pending = null;
            return enrollment;
        }
    }

    /// <inheritdoc />
    public bool Remove(VirtualDeviceId virtualDeviceId)
    {
        lock (_gate)
        {
            return _enrollments.Remove(virtualDeviceId);
        }
    }

    private sealed class EnrollmentState(
        VirtualOutputExternalEnrollment pending,
        Func<VirtualOutputExternalEnrollment?> renewalFactory)
    {
        public VirtualOutputExternalEnrollment? Pending { get; set; } = pending;

        public Func<VirtualOutputExternalEnrollment?> RenewalFactory { get; } = renewalFactory;
    }
}

/// <summary>Reconciles configured virtual outputs with isolated managed or external workers.</summary>
public sealed class VirtualOutputSupervisor : BackgroundService, IVirtualOutputWorkerRecovery
{
    private static readonly TimeSpan LivenessCheckInterval = TimeSpan.FromSeconds(1);
    private static readonly IReadOnlySet<VirtualOutputCapability> WorkerCapabilities = new HashSet<VirtualOutputCapability>
    {
        VirtualOutputCapability.ReadManifest,
        VirtualOutputCapability.ReadLineup,
        VirtualOutputCapability.ReadGuide,
        VirtualOutputCapability.OpenStream,
        VirtualOutputCapability.ReportHealth,
        VirtualOutputCapability.AdmitConnection
    };

    private readonly ILineupSettingsService _settings;
    private readonly VirtualOutputRuntimeOptions _options;
    private readonly IVirtualOutputWorkerCatalog _catalog;
    private readonly IVirtualOutputWorkerLauncher _launcher;
    private readonly IVirtualOutputCertificateResolver _certificates;
    private readonly IVirtualOutputSessionAuthority _authority;
    private readonly IVirtualOutputRuntimeStatus _status;
    private readonly IVirtualOutputExternalEnrollmentStore _externalEnrollments;
    private readonly ILogger<VirtualOutputSupervisor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly SemaphoreSlim _transitionSlots;
    private readonly ConcurrentDictionary<VirtualDeviceId, SemaphoreSlim> _outputGates = new();
    private readonly CancellationTokenSource _retryCancellation = new();
    private readonly ConcurrentDictionary<VirtualDeviceId, ActiveWorker> _active = new();
    private readonly ConcurrentDictionary<VirtualDeviceId, ExternalWorker> _externalActive = new();
    private readonly ConcurrentDictionary<VirtualDeviceId, byte> _ownedOutputs = new();
    private readonly ConcurrentDictionary<VirtualDeviceId, VirtualOutputWorkerRestartBudget> _restartBudgets = new();
    private readonly ConcurrentDictionary<VirtualDeviceId, PendingRetry> _pendingRetries = new();
    private readonly ConcurrentDictionary<VirtualDeviceId, string> _quarantinedFingerprints = new();
    private CancellationToken _shutdownToken;

    /// <summary>Initializes the desired-versus-applied worker reconciler.</summary>
    public VirtualOutputSupervisor(
        ILineupSettingsService settings,
        VirtualOutputRuntimeOptions options,
        IVirtualOutputWorkerCatalog catalog,
        IVirtualOutputWorkerLauncher launcher,
        IVirtualOutputCertificateResolver certificates,
        IVirtualOutputSessionAuthority authority,
        IVirtualOutputRuntimeStatus status,
        IVirtualOutputExternalEnrollmentStore externalEnrollments,
        ILogger<VirtualOutputSupervisor> logger,
        TimeProvider? timeProvider = null)
    {
        _settings = settings;
        _options = options;
        _catalog = catalog;
        _launcher = launcher;
        _certificates = certificates;
        _authority = authority;
        _status = status;
        _externalEnrollments = externalEnrollments;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumConcurrentTransitions);
        if (options.ShutdownGracePeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The shared shutdown grace period must be positive.");
        }
        _transitionSlots = new SemaphoreSlim(options.MaximumConcurrentTransitions, options.MaximumConcurrentTransitions);
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _settings.OnSettingsChanged += SettingsChanged;
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _settings.OnSettingsChanged -= SettingsChanged;
        _shutdownToken = cancellationToken;
        _retryCancellation.Cancel();
        await base.StopAsync(cancellationToken);
        if (ExecuteTask is { } execution)
        {
            await execution;
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reconcile = true;
        Task? reconciliation = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (reconciliation is { IsCompleted: true })
                {
                    await reconciliation;
                    reconciliation = null;
                }
                if (reconcile && reconciliation is null)
                {
                    reconcile = false;
                    reconciliation = ReconcileAsync(stoppingToken);
                }
                await CheckManagedWorkerLivenessAsync(stoppingToken);
                CheckExternalWorkerLiveness();
                reconcile |= await WaitForChangesAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _retryCancellation.Cancel();
            try
            {
                if (reconciliation is not null)
                {
                    await reconciliation;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            finally
            {
                using var grace = new CancellationTokenSource(_options.ShutdownGracePeriod, _timeProvider);
                using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(grace.Token, _shutdownToken);
                await StopAllAsync(cleanup.Token);
            }
        }
    }

    /// <inheritdoc />
    public async Task<VirtualOutputWorkerRecoveryResult> RetryAsync(VirtualDeviceId virtualDeviceId, CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _retryCancellation.Token);
        VirtualOutputWorkerRecoveryResult? result = null;
        await RunTransitionAsync(virtualDeviceId, async token =>
        {
            token.ThrowIfCancellationRequested();
            var snapshot = _status.GetSnapshots().SingleOrDefault(item => item.VirtualDeviceId == virtualDeviceId);
            var configuration = _settings.Settings.Streaming;
            var device = configuration.VirtualDevices.SingleOrDefault(item => item.Id == virtualDeviceId && item.Enabled && item.Hosting is not null);
            if (device is null || snapshot?.State != VirtualOutputRuntimeState.Failed || _active.ContainsKey(virtualDeviceId)
                || snapshot.Generation is { } generation && _authority.HasActiveSession(virtualDeviceId, generation))
            {
                _logger.LogWarning("Rejected recovery for virtual output {VirtualDeviceId}: the saved output is not an unavailable worker", virtualDeviceId);
                result = new(false, snapshot);
                return;
            }
            _logger.LogInformation("Operator revalidating virtual output {VirtualDeviceId}; previous failure: {PreviousFailure}", virtualDeviceId, snapshot.LastFailure);
            _status.Update(virtualDeviceId, current => (current ?? snapshot) with
            {
                PreviousFailure = snapshot.LastFailure,
                LastRecoveryAttemptUtc = _timeProvider.GetUtcNow()
            });
            ResetFailureState(virtualDeviceId);
            _externalEnrollments.Remove(virtualDeviceId);
            _externalActive.TryRemove(virtualDeviceId, out _);
            _authority.Revoke(virtualDeviceId, "An operator requested revalidation of the failed worker.");
            await ReconcileOutputAsync(device, configuration, _retryCancellation.Token);
            result = new(true, _status.GetSnapshots().Single(item => item.VirtualDeviceId == virtualDeviceId));
        }, lifetime.Token);
        return result ?? throw new InvalidOperationException("The worker recovery transition did not produce an outcome.");
    }

    private SemaphoreSlim GetOutputGate(VirtualDeviceId deviceId) => _outputGates.GetOrAdd(deviceId, static _ => new SemaphoreSlim(1, 1));

    private async Task RunTransitionAsync(VirtualDeviceId deviceId, Func<CancellationToken, Task> transition, CancellationToken cancellationToken)
    {
        await _transitionSlots.WaitAsync(cancellationToken);
        try
        {
            var gate = GetOutputGate(deviceId);
            await gate.WaitAsync(cancellationToken);
            try
            {
                await transition(cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            _transitionSlots.Release();
        }
    }

    private async Task<bool> WaitForChangesAsync(CancellationToken cancellationToken)
    {
        using var interval = new CancellationTokenSource(LivenessCheckInterval, _timeProvider);
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, interval.Token);
        try
        {
            await _changed.WaitAsync(waitCancellation.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && interval.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task CheckManagedWorkerLivenessAsync(CancellationToken cancellationToken)
    {
        var snapshots = _status.GetSnapshots();
        foreach (var (deviceId, worker) in _active.ToArray())
        {
            var gate = GetOutputGate(deviceId);
            if (!gate.Wait(0))
            {
                continue;
            }
            try
            {
                if (!_active.TryGetValue(deviceId, out var current) || !ReferenceEquals(current, worker))
                {
                    continue;
                }
                if (worker.Process.Completion.IsCompleted)
                {
                    continue;
                }
                if (_authority.HasActiveSession(deviceId, worker.Generation))
                {
                    worker.HasRegistered = true;
                    continue;
                }
                worker.HasRegistered |= snapshots.Any(snapshot =>
                    snapshot.VirtualDeviceId == deviceId && snapshot.Generation == worker.Generation && snapshot.ProtocolVersion is not null);
                if (!worker.HasRegistered && _timeProvider.GetUtcNow() <= worker.BootstrapExpiresUtc)
                {
                    continue;
                }

                var failure = worker.HasRegistered
                    ? "The worker session is no longer active or its lease has expired."
                    : "The worker did not register before its bootstrap credential expired.";
                _active.TryRemove(deviceId, out _);
                _authority.Revoke(deviceId, failure);
                RecordManagedWorkerFailure(deviceId, worker, failure);
                _logger.LogWarning("Stopping unresponsive virtual-output worker for {VirtualDeviceId}: {Failure}", deviceId, failure);
                try
                {
                    await worker.Process.StopAsync(TimeSpan.Zero, cancellationToken);
                }
                finally
                {
                    await worker.Process.DisposeAsync();
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private void CheckExternalWorkerLiveness()
    {
        foreach (var snapshot in _status.GetSnapshots().Where(snapshot => _externalActive.ContainsKey(snapshot.VirtualDeviceId)))
        {
            var gate = GetOutputGate(snapshot.VirtualDeviceId);
            if (!gate.Wait(0))
            {
                continue;
            }
            try
            {
                // Enrollment can replace this generation without a settings reconciliation.
                _status.Update(snapshot.VirtualDeviceId, current =>
                {
                    if (current is not { Generation: { } generation, ProtocolVersion: not null, State: VirtualOutputRuntimeState.Starting or VirtualOutputRuntimeState.Healthy or VirtualOutputRuntimeState.Degraded }
                        || _authority.HasActiveSession(snapshot.VirtualDeviceId, generation))
                    {
                        return current ?? snapshot;
                    }

                    const string failure = "The externally supervised worker session is no longer active or its lease has expired. Waiting for re-enrollment.";
                    _logger.LogWarning("External virtual-output worker for {VirtualDeviceId} is unavailable: {Failure}", snapshot.VirtualDeviceId, failure);
                    return current with
                    {
                        State = VirtualOutputRuntimeState.Failed,
                        ReadySinceUtc = null,
                        LastFailure = failure
                    };
                });
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            var configuration = _settings.Settings.Streaming;
            var desired = configuration.VirtualDevices
                .Where(device => device.Enabled && device.Hosting is not null)
                .ToDictionary(device => device.Id);

            var outputs = _ownedOutputs.Keys.Union(desired.Keys).ToArray();
            await Task.WhenAll(outputs.Select(id => RunTransitionAsync(id, async token =>
            {
                var currentConfiguration = _settings.Settings.Streaming;
                var device = currentConfiguration.VirtualDevices.SingleOrDefault(item => item.Id == id && item.Enabled && item.Hosting is not null);
                if (device is null)
                {
                    await StopWorkerAsync(id, token);
                    _status.Remove(id);
                    ResetFailureState(id);
                    _ownedOutputs.TryRemove(id, out _);
                    return;
                }
                await ReconcileOutputAsync(device, currentConfiguration, token);
            }, cancellationToken)));
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task ReconcileOutputAsync(VirtualDeviceDefinition device, StreamingConfiguration configuration, CancellationToken cancellationToken)
    {
        _ownedOutputs[device.Id] = 0;
        string fingerprint;
        try
        {
            fingerprint = CreateFingerprint(device, configuration);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            ResetFailureState(device.Id);
            await StopWorkerAsync(device.Id, cancellationToken);
            _logger.LogError(exception, "Unable to evaluate isolated virtual output {VirtualDeviceId} certificate configuration", device.Id);
            _status.Set(new VirtualOutputRuntimeSnapshot
            {
                VirtualDeviceId = device.Id,
                State = VirtualOutputRuntimeState.Failed,
                Bindings = SafeBindings(device.Hosting!),
                LastFailure = "The virtual-output TLS certificate reference or file metadata could not be read. Correct the certificate configuration and retry."
            });
            return;
        }
        if (_pendingRetries.TryGetValue(device.Id, out var pendingRetry))
        {
            if (!string.Equals(pendingRetry.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                ResetFailureState(device.Id);
            }
            else if (_timeProvider.GetUtcNow() < pendingRetry.RetryUtc)
            {
                return;
            }
            else
            {
                _pendingRetries.TryRemove(device.Id, out _);
            }
        }
        if (_quarantinedFingerprints.TryGetValue(device.Id, out var quarantinedFingerprint))
        {
            if (string.Equals(quarantinedFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return;
            }
            ResetFailureState(device.Id);
        }
        if (_active.TryGetValue(device.Id, out var active) && string.Equals(active.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return;
        }
        if (_externalActive.TryGetValue(device.Id, out var external) &&
            string.Equals(external.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return;
        }
        if (active is not null || external is not null)
        {
            ResetFailureState(device.Id);
            await StopWorkerAsync(device.Id, cancellationToken);
        }
        await StartWorkerAsync(device, configuration, fingerprint, cancellationToken);
    }

    private async Task StartWorkerAsync(
        VirtualDeviceDefinition device,
        StreamingConfiguration configuration,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        VirtualOutputWorkerPolicyDefinition? workerPolicy = null;
        try
        {
            var hosting = device.Hosting!;
            workerPolicy = configuration.VirtualOutputWorkerPolicies.Single(
                policy => string.Equals(policy.Key, hosting.WorkerPolicyKey, StringComparison.OrdinalIgnoreCase));
            new VirtualOutputResourcePolicyValidator().Validate(configuration with { VirtualDevices = [device] });
            var leaseLifetime = TimeSpan.FromSeconds(workerPolicy.LeaseGracePeriodSeconds);
            var package = await _catalog.ResolveAsync(device.Adapter.Kind, cancellationToken);
            var ticket = _authority.CreateActivation(
                device.Id,
                package.Manifest.WorkerId,
                WorkerCapabilities,
                _options.BootstrapLifetime,
                leaseLifetime);
            var referencedSecurityKeys = hosting.Bindings.Select(binding => binding.SecurityPolicyKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var securityPolicies = configuration.VirtualOutputSecurityPolicies
                .Where(policy => referencedSecurityKeys.Contains(policy.Key))
                .ToArray();
            var certificateMaterials = new List<VirtualOutputCertificateMaterial>();
            foreach (var certificateReference in securityPolicies
                         .Select(policy => policy.CertificateReference)
                         .OfType<string>()
                         .Distinct(StringComparer.Ordinal))
            {
                certificateMaterials.Add(await _certificates.ResolveAsync(certificateReference, cancellationToken));
            }
            var bootstrap = new VirtualOutputWorkerBootstrap
            {
                Endpoint = _options.IpcEndpoint,
                VirtualDevice = device,
                SecurityPolicies = securityPolicies,
                CertificateMaterials = certificateMaterials,
                WorkerPolicy = workerPolicy,
                WorkerId = package.Manifest.WorkerId,
                Generation = ticket.Generation,
                BootstrapToken = ticket.BootstrapToken,
                BootstrapExpiresUtc = ticket.ExpiresUtc
            };

            if (hosting.ExecutionMode == VirtualOutputExecutionMode.ExternalProcess)
            {
                var currentGeneration = ticket.Generation;
                var bootstrapTemplate = bootstrap with
                {
                    Generation = 0,
                    BootstrapToken = string.Empty,
                    BootstrapExpiresUtc = DateTimeOffset.MinValue
                };
                var bindings = SafeBindings(hosting);
                _externalEnrollments.Set(new VirtualOutputExternalEnrollment
                {
                    VirtualDeviceId = device.Id,
                    Bootstrap = bootstrap
                }, () =>
                {
                    var gate = GetOutputGate(device.Id);
                    if (!gate.Wait(0))
                    {
                        return null;
                    }
                    try
                    {
                        if (_authority.HasActiveSession(device.Id, currentGeneration))
                        {
                            return null;
                        }

                        var renewedTicket = _authority.CreateActivation(
                            device.Id,
                            package.Manifest.WorkerId,
                            WorkerCapabilities,
                            _options.BootstrapLifetime,
                            leaseLifetime);
                        currentGeneration = renewedTicket.Generation;
                        var renewedBootstrap = bootstrapTemplate with
                        {
                            Generation = renewedTicket.Generation,
                            BootstrapToken = renewedTicket.BootstrapToken,
                            BootstrapExpiresUtc = renewedTicket.ExpiresUtc
                        };
                        _status.Set(new VirtualOutputRuntimeSnapshot
                        {
                            VirtualDeviceId = device.Id,
                            State = VirtualOutputRuntimeState.Starting,
                            Generation = renewedTicket.Generation,
                            Bindings = bindings,
                            LastFailure = "Waiting for the externally supervised worker to enroll."
                        });
                        return new VirtualOutputExternalEnrollment
                        {
                            VirtualDeviceId = device.Id,
                            Bootstrap = renewedBootstrap
                        };
                    }
                    finally
                    {
                        gate.Release();
                    }
                });
                _externalActive[device.Id] = new ExternalWorker(fingerprint, ticket.Generation);
                _status.Set(new VirtualOutputRuntimeSnapshot
                {
                    VirtualDeviceId = device.Id,
                    State = VirtualOutputRuntimeState.Starting,
                    Generation = ticket.Generation,
                    Bindings = bindings,
                    LastFailure = "Waiting for the externally supervised worker to enroll."
                });
                return;
            }

            var restartCount = GetRecentFailureCount(device.Id, workerPolicy);
            var managedBindings = SafeBindings(hosting);
            _status.Set(new VirtualOutputRuntimeSnapshot
            {
                VirtualDeviceId = device.Id,
                State = VirtualOutputRuntimeState.Starting,
                Generation = ticket.Generation,
                Bindings = managedBindings,
                RestartCount = restartCount
            });
            var remaining = ticket.ExpiresUtc - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException("The worker bootstrap credential expired before process launch.");
            }
            using var startupDeadline = new CancellationTokenSource(remaining, _timeProvider);
            using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, startupDeadline.Token);
            IVirtualOutputWorkerProcess process;
            using var activation = Lineup.Core.Extensibility.Diagnostics.LineupTelemetry.Begin("worker_activation");
            System.Diagnostics.Activity.Current?.SetTag("lineup.worker.generation", ticket.Generation);
            try
            {
                if (restartCount > 0)
                {
                    Lineup.Core.Extensibility.Diagnostics.LineupTelemetry.WorkerRestarts.Add(1);
                }
                process = await _launcher.StartAsync(package, bootstrap, startupCancellation.Token);
                activation.Succeed();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && startupDeadline.IsCancellationRequested)
            {
                throw new TimeoutException("The worker did not complete bootstrap delivery before its credential expired.");
            }
            var worker = new ActiveWorker(
                fingerprint,
                ticket.Generation,
                workerPolicy,
                process,
                managedBindings,
                restartCount,
                ticket.ExpiresUtc);
            _active[device.Id] = worker;
            _status.Update(device.Id, current => (current ?? new VirtualOutputRuntimeSnapshot
            {
                VirtualDeviceId = device.Id,
                State = VirtualOutputRuntimeState.Starting,
                Generation = ticket.Generation,
                Bindings = worker.Bindings,
                RestartCount = restartCount
            }) with
            { ProcessId = process.ProcessId });
            _ = ObserveExitAsync(device.Id, worker);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _authority.Revoke(device.Id, "The virtual output worker activation was canceled.");
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to activate isolated virtual output {VirtualDeviceId}", device.Id);
            _externalEnrollments.Remove(device.Id);
            _externalActive.TryRemove(device.Id, out _);
            _authority.Revoke(device.Id, "The virtual output worker could not be activated.");
            var policyUnavailable = exception is VirtualOutputResourcePolicyException;
            var decision = workerPolicy is null || policyUnavailable ? null : RecordFailure(device.Id, workerPolicy);
            var quarantined = policyUnavailable || decision?.Exhausted == true;
            if (quarantined)
            {
                _quarantinedFingerprints[device.Id] = fingerprint;
            }
            else if (decision is not null)
            {
                ScheduleRetry(device.Id, fingerprint, decision.RetryDelay);
            }
            _status.Set(new VirtualOutputRuntimeSnapshot
            {
                VirtualDeviceId = device.Id,
                State = device.Hosting is null ? VirtualOutputRuntimeState.RequiresConfiguration : VirtualOutputRuntimeState.Failed,
                Bindings = device.Hosting is null ? [] : SafeBindings(device.Hosting),
                RestartCount = decision?.FailureCount ?? 0,
                LastFailure = policyUnavailable
                    ? exception.Message
                    : quarantined
                    ? $"{exception.Message} The worker exceeded its restart budget."
                    : decision is null
                        ? exception.Message
                        : $"{exception.Message} Retrying in {decision.RetryDelay.TotalSeconds:0} seconds."
            });
        }
    }

    private async Task StopWorkerAsync(VirtualDeviceId deviceId, CancellationToken cancellationToken)
    {
        _externalEnrollments.Remove(deviceId);
        _externalActive.TryRemove(deviceId, out _);
        _authority.Revoke(deviceId, "The virtual output worker is being replaced or stopped.");
        if (!_active.TryRemove(deviceId, out var active))
        {
            return;
        }

        _status.Set(new VirtualOutputRuntimeSnapshot
        {
            VirtualDeviceId = deviceId,
            State = VirtualOutputRuntimeState.Draining,
            Generation = active.Generation,
            ProcessId = active.Process.ProcessId,
            Bindings = active.Bindings,
            RestartCount = active.RestartCount
        });
        using var drainDeadline = new CancellationTokenSource(
            active.Policy.DrainTimeoutSeconds == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(active.Policy.DrainTimeoutSeconds), _timeProvider);
        if (active.Policy.DrainTimeoutSeconds == 0)
        {
            drainDeadline.Cancel();
        }
        using var drainCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, drainDeadline.Token);
        try
        {
            try
            {
                await active.Process.Completion.WaitAsync(drainCancellation.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await active.Process.StopAsync(TimeSpan.Zero, cancellationToken);
            }
        }
        finally
        {
            await active.Process.DisposeAsync();
        }
    }

    private async Task ObserveExitAsync(VirtualDeviceId deviceId, ActiveWorker worker)
    {
        var exitCode = await worker.Process.Completion;
        var gate = GetOutputGate(deviceId);
        await gate.WaitAsync();
        try
        {
            if (_active.TryGetValue(deviceId, out var current) && ReferenceEquals(current, worker))
            {
                _active.TryRemove(deviceId, out _);
                _authority.Revoke(deviceId, "The virtual output worker exited.");
                RecordManagedWorkerFailure(deviceId, worker, $"The worker exited with code {exitCode}.");
                _logger.LogWarning("Virtual-output worker for {VirtualDeviceId} exited with code {ExitCode}", deviceId, exitCode);
            }
            await worker.Process.DisposeAsync();
        }
        finally
        {
            gate.Release();
        }
    }

    private void RecordManagedWorkerFailure(VirtualDeviceId deviceId, ActiveWorker worker, string failure)
    {
        var decision = RecordFailure(deviceId, worker.Policy);
        if (decision.Exhausted)
        {
            _quarantinedFingerprints[deviceId] = worker.Fingerprint;
        }
        else
        {
            ScheduleRetry(deviceId, worker.Fingerprint, decision.RetryDelay);
        }
        _status.Set(new VirtualOutputRuntimeSnapshot
        {
            VirtualDeviceId = deviceId,
            State = VirtualOutputRuntimeState.Failed,
            Generation = worker.Generation,
            ProcessId = worker.Process.ProcessId,
            Bindings = worker.Bindings,
            RestartCount = decision.FailureCount,
            LastFailure = decision.Exhausted
                ? $"{failure} The worker exceeded its restart budget."
                : $"{failure} Retrying in {decision.RetryDelay.TotalSeconds:0} seconds."
        });
    }

    private async Task StopAllAsync(CancellationToken cancellationToken)
    {
        var outputs = _ownedOutputs.Keys.Union(_active.Keys).Union(_externalActive.Keys).ToArray();
        await Task.WhenAll(outputs.Select(async deviceId =>
        {
            var gate = GetOutputGate(deviceId);
            await gate.WaitAsync(CancellationToken.None);
            try
            {
                await StopWorkerAsync(deviceId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                gate.Release();
            }
        }));
    }

    private static string CreateFingerprint(VirtualDeviceDefinition device, StreamingConfiguration configuration)
    {
        var hosting = device.Hosting!;
        var securityKeys = hosting.Bindings.Select(binding => binding.SecurityPolicyKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var snapshot = new
        {
            Device = device,
            Security = configuration.VirtualOutputSecurityPolicies
                .Where(policy => securityKeys.Contains(policy.Key))
                .OrderBy(policy => policy.Key)
                .Select(policy => new
                {
                    Policy = policy,
                    Certificate = GetCertificateFingerprintInput(policy.CertificateReference)
                }),
            Worker = configuration.VirtualOutputWorkerPolicies.SingleOrDefault(policy =>
                string.Equals(policy.Key, hosting.WorkerPolicyKey, StringComparison.OrdinalIgnoreCase))
        };
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot))).ToLowerInvariant();
    }

    private static object? GetCertificateFingerprintInput(string? certificateReference)
    {
        if (string.IsNullOrWhiteSpace(certificateReference))
        {
            return null;
        }
        var info = new FileInfo(Path.GetFullPath(certificateReference));
        return new
        {
            Reference = certificateReference,
            info.Exists,
            Length = info.Exists ? info.Length : 0,
            LastWriteUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue
        };
    }

    private static IReadOnlyList<string> SafeBindings(VirtualOutputHostingDefinition hosting) =>
        hosting.Bindings.Select(binding => $"{binding.Transport.ToString().ToLowerInvariant()}://{string.Join(",", binding.Addresses)}:{binding.Port}").ToArray();

    private int GetRecentFailureCount(VirtualDeviceId deviceId, VirtualOutputWorkerPolicyDefinition policy)
    {
        return _restartBudgets.TryGetValue(deviceId, out var budget)
            ? budget.GetRecentFailureCount(policy)
            : 0;
    }

    private VirtualOutputWorkerRestartDecision RecordFailure(VirtualDeviceId deviceId, VirtualOutputWorkerPolicyDefinition policy)
    {
        if (!_restartBudgets.TryGetValue(deviceId, out var budget))
        {
            budget = new VirtualOutputWorkerRestartBudget(_timeProvider);
            _restartBudgets[deviceId] = budget;
        }
        return budget.RecordFailure(policy);
    }

    private void ScheduleRetry(VirtualDeviceId deviceId, string fingerprint, TimeSpan delay)
    {
        _pendingRetries[deviceId] = new PendingRetry(fingerprint, _timeProvider.GetUtcNow().Add(delay));
        _ = SignalRetryAsync(delay);
    }

    private async Task SignalRetryAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _timeProvider, _retryCancellation.Token);
            SettingsChanged();
        }
        catch (OperationCanceledException) when (_retryCancellation.IsCancellationRequested)
        {
        }
    }

    private void ResetFailureState(VirtualDeviceId deviceId)
    {
        _restartBudgets.TryRemove(deviceId, out _);
        _pendingRetries.TryRemove(deviceId, out _);
        _quarantinedFingerprints.TryRemove(deviceId, out _);
    }

    private void SettingsChanged()
    {
        try
        {
            _changed.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private sealed record ActiveWorker(
        string Fingerprint,
        long Generation,
        VirtualOutputWorkerPolicyDefinition Policy,
        IVirtualOutputWorkerProcess Process,
        IReadOnlyList<string> Bindings,
        int RestartCount,
        DateTimeOffset BootstrapExpiresUtc)
    {
        /// <summary>Gets or sets whether this generation has registered with the authority.</summary>
        public bool HasRegistered { get; set; }
    }

    private sealed record ExternalWorker(string Fingerprint, long Generation);

    private sealed record PendingRetry(string Fingerprint, DateTimeOffset RetryUtc);
}
