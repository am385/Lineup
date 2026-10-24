using System.Text.Json;
using Lineup.Common.Streaming;
using Lineup.Core.Settings;
using Lineup.VirtualOutputs.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lineup.VirtualOutputs.Runtime;

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
public sealed class VirtualOutputSupervisor : BackgroundService
{
    private static readonly IReadOnlySet<VirtualOutputCapability> WorkerCapabilities = new HashSet<VirtualOutputCapability>
    {
        VirtualOutputCapability.ReadManifest,
        VirtualOutputCapability.ReadLineup,
        VirtualOutputCapability.ReadGuide,
        VirtualOutputCapability.OpenStream,
        VirtualOutputCapability.ReportHealth
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
    private readonly CancellationTokenSource _retryCancellation = new();
    private readonly Dictionary<VirtualDeviceId, ActiveWorker> _active = [];
    private readonly Dictionary<VirtualDeviceId, ExternalWorker> _externalActive = [];
    private readonly HashSet<VirtualDeviceId> _ownedOutputs = [];
    private readonly Dictionary<VirtualDeviceId, VirtualOutputWorkerRestartBudget> _restartBudgets = [];
    private readonly Dictionary<VirtualDeviceId, PendingRetry> _pendingRetries = [];
    private readonly Dictionary<VirtualDeviceId, string> _quarantinedFingerprints = [];

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
        _retryCancellation.Cancel();
        await base.StopAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _stateGate.WaitAsync(stoppingToken);
                try
                {
                    await ReconcileAsync(stoppingToken);
                }
                finally
                {
                    _stateGate.Release();
                }
                await _changed.WaitAsync(stoppingToken);
            }
        }
        finally
        {
            await _stateGate.WaitAsync(CancellationToken.None);
            try
            {
                await StopAllAsync(CancellationToken.None);
            }
            finally
            {
                _stateGate.Release();
            }
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var configuration = _settings.Settings.Streaming;
        var desired = configuration.VirtualDevices
            .Where(device => device.Enabled && device.Hosting is not null)
            .ToDictionary(device => device.Id);

        foreach (var obsoleteId in _ownedOutputs
                     .Where(id => !desired.ContainsKey(id))
                     .ToArray())
        {
            await StopWorkerAsync(obsoleteId, cancellationToken);
            _status.Remove(obsoleteId);
            ResetFailureState(obsoleteId);
            _ownedOutputs.Remove(obsoleteId);
        }

        foreach (var device in desired.Values)
        {
            _ownedOutputs.Add(device.Id);
            var fingerprint = CreateFingerprint(device, configuration);
            if (_pendingRetries.TryGetValue(device.Id, out var pendingRetry))
            {
                if (!string.Equals(pendingRetry.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    ResetFailureState(device.Id);
                }
                else if (_timeProvider.GetUtcNow() < pendingRetry.RetryUtc)
                {
                    continue;
                }
                else
                {
                    _pendingRetries.Remove(device.Id);
                }
            }
            if (_quarantinedFingerprints.TryGetValue(device.Id, out var quarantinedFingerprint))
            {
                if (string.Equals(quarantinedFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    continue;
                }
                ResetFailureState(device.Id);
            }
            if (_active.TryGetValue(device.Id, out var active) && string.Equals(active.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                continue;
            }
            if (_externalActive.TryGetValue(device.Id, out var external) &&
                string.Equals(external.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                continue;
            }
            if (active is not null || external is not null)
            {
                ResetFailureState(device.Id);
                await StopWorkerAsync(device.Id, cancellationToken);
            }
            await StartWorkerAsync(device, configuration, fingerprint, cancellationToken);
        }
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

            var process = await _launcher.StartAsync(package, bootstrap, cancellationToken);
            var restartCount = GetRecentFailureCount(device.Id, workerPolicy);
            var worker = new ActiveWorker(
                fingerprint,
                ticket.Generation,
                workerPolicy,
                process,
                SafeBindings(hosting),
                restartCount);
            _active[device.Id] = worker;
            _status.Set(new VirtualOutputRuntimeSnapshot
            {
                VirtualDeviceId = device.Id,
                State = VirtualOutputRuntimeState.Starting,
                Generation = ticket.Generation,
                ProcessId = process.ProcessId,
                Bindings = worker.Bindings,
                RestartCount = restartCount
            });
            _ = ObserveExitAsync(device.Id, worker);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to activate isolated virtual output {VirtualDeviceId}", device.Id);
            _externalEnrollments.Remove(device.Id);
            _externalActive.Remove(device.Id);
            _authority.Revoke(device.Id, "The virtual output worker could not be activated.");
            var decision = workerPolicy is null ? null : RecordFailure(device.Id, workerPolicy);
            var quarantined = decision?.Exhausted == true;
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
                LastFailure = quarantined
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
        _externalActive.Remove(deviceId);
        _authority.Revoke(deviceId, "The virtual output worker is being replaced or stopped.");
        if (!_active.Remove(deviceId, out var active))
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
        using var drainCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        drainCancellation.CancelAfter(TimeSpan.FromSeconds(active.Policy.DrainTimeoutSeconds));
        try
        {
            await active.Process.Completion.WaitAsync(drainCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await active.Process.StopAsync(TimeSpan.Zero, cancellationToken);
        }
        await active.Process.DisposeAsync();
    }

    private async Task ObserveExitAsync(VirtualDeviceId deviceId, ActiveWorker worker)
    {
        var exitCode = await worker.Process.Completion;
        await _stateGate.WaitAsync();
        try
        {
            if (_active.TryGetValue(deviceId, out var current) && ReferenceEquals(current, worker))
            {
                _active.Remove(deviceId);
                _authority.Revoke(deviceId, "The virtual output worker exited.");
                var decision = RecordFailure(deviceId, worker.Policy);
                var quarantined = decision.Exhausted;
                if (quarantined)
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
                    LastFailure = quarantined
                        ? $"The worker exited with code {exitCode} and exceeded its restart budget."
                        : $"The worker exited with code {exitCode}. Retrying in {decision.RetryDelay.TotalSeconds:0} seconds."
                });
                _logger.LogWarning("Virtual-output worker for {VirtualDeviceId} exited with code {ExitCode}", deviceId, exitCode);
            }
        }
        finally
        {
            _stateGate.Release();
        }
        await worker.Process.DisposeAsync();
    }

    private async Task StopAllAsync(CancellationToken cancellationToken)
    {
        foreach (var deviceId in _active.Keys.ToArray())
        {
            await StopWorkerAsync(deviceId, cancellationToken);
        }
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
        _restartBudgets.Remove(deviceId);
        _pendingRetries.Remove(deviceId);
        _quarantinedFingerprints.Remove(deviceId);
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
        int RestartCount);

    private sealed record ExternalWorker(string Fingerprint, long Generation);

    private sealed record PendingRetry(string Fingerprint, DateTimeOffset RetryUtc);
}
