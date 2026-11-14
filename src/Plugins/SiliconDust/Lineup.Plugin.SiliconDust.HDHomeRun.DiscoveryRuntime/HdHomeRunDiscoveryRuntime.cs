using System.Globalization;
using System.Text.Json;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Settings;
using Lineup.Core.Extensibility.Streaming;
using Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner;
using Lineup.Core.VirtualOutputs.Protocol;
using Lineup.Core.VirtualOutputs.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryRuntime;

/// <summary>Registers the isolated shared HDHomeRun discovery worker.</summary>
public static class HdHomeRunDiscoveryRuntimeServiceCollectionExtensions
{
    /// <summary>Adds the provider-specific discovery snapshot reconciler.</summary>
    public static IServiceCollection AddHdHomeRunDiscoveryRuntime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IHdHomeRunDiscoveryPolicy, EnabledHdHomeRunDiscoveryPolicy>();
        services.AddHostedService<HdHomeRunDiscoveryWorkerSupervisor>();
        return services;
    }
}

/// <summary>Controls whether the host permits shared HDHomeRun network discovery.</summary>
public interface IHdHomeRunDiscoveryPolicy
{
    /// <summary>Gets whether the discovery worker may run.</summary>
    bool Enabled { get; }

    /// <summary>Occurs when the host discovery preference changes.</summary>
    event Action? Changed;
}

/// <summary>Enables provider discovery for hosts without a separate presentation preference.</summary>
public sealed class EnabledHdHomeRunDiscoveryPolicy : IHdHomeRunDiscoveryPolicy
{
    /// <summary>Gets the enabled.</summary>
    public bool Enabled => true;

    /// <summary>Provides the changed.</summary>
    public event Action? Changed
    {
        add { }
        remove { }
    }
}

/// <summary>Reconciles all advertised virtual HDHomeRun devices into one isolated discovery process.</summary>
public sealed class HdHomeRunDiscoveryWorkerSupervisor : BackgroundService
{
    /// <summary>Gets the catalog adapter kind implemented by the shared discovery worker.</summary>
    public const string DiscoveryAdapterKind = "hdhomerun-discovery";

    private static readonly VirtualDeviceId _discoveryWorkerId = new(new Guid("d1d41379-85f9-4834-970d-3ba27ea7c104"));
    private static readonly TimeSpan _advertisementRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly IReadOnlySet<VirtualOutputCapability> _capabilities = new HashSet<VirtualOutputCapability>
    {
        VirtualOutputCapability.ReportHealth
    };
    private static readonly VirtualOutputWorkerPolicyDefinition _defaultWorkerPolicy = new()
    {
        Key = "hdhomerun-discovery-worker",
        Name = "HDHomeRun discovery worker"
    };
    private readonly ILineupSettingsService _settings;
    private readonly IPhysicalDeviceResolver _physicalDevices;
    private readonly HdHomeRunVirtualTunerDriver _driver;
    private readonly IHdHomeRunDiscoveryPolicy _policy;
    private readonly VirtualOutputRuntimeOptions _options;
    private readonly IVirtualOutputWorkerCatalog _catalog;
    private readonly IVirtualOutputWorkerLauncher _launcher;
    private readonly IVirtualOutputSessionAuthority _authority;
    private readonly ILogger<HdHomeRunDiscoveryWorkerSupervisor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly CancellationTokenSource _retryCancellation = new();
    private readonly VirtualOutputWorkerRestartBudget _restartBudget;
    private ActiveDiscoveryWorker? _active;
    private PendingRetry? _pendingRetry;
    private string? _quarantinedFingerprint;

    /// <summary>Initializes the shared discovery worker reconciler.</summary>
    public HdHomeRunDiscoveryWorkerSupervisor(
        ILineupSettingsService settings,
        IPhysicalDeviceResolver physicalDevices,
        HdHomeRunVirtualTunerDriver driver,
        IHdHomeRunDiscoveryPolicy policy,
        VirtualOutputRuntimeOptions options,
        IVirtualOutputWorkerCatalog catalog,
        IVirtualOutputWorkerLauncher launcher,
        IVirtualOutputSessionAuthority authority,
        ILogger<HdHomeRunDiscoveryWorkerSupervisor> logger,
        TimeProvider? timeProvider = null
    )
    {
        _settings = settings;
        _physicalDevices = physicalDevices;
        _driver = driver;
        _policy = policy;
        _options = options;
        _catalog = catalog;
        _launcher = launcher;
        _authority = authority;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _restartBudget = new VirtualOutputWorkerRestartBudget(_timeProvider);
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _settings.OnSettingsChanged += SettingsChanged;
        _policy.Changed += SettingsChanged;
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _settings.OnSettingsChanged -= SettingsChanged;
        _policy.Changed -= SettingsChanged;
        _retryCancellation.Cancel();
        await base.StopAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var refreshTimer = _timeProvider.CreateTimer(_ => SettingsChanged(), null, _advertisementRefreshInterval, _advertisementRefreshInterval);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
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
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Unable to reconcile HDHomeRun network discovery");
                }
                await _changed.WaitAsync(stoppingToken);
            }
        }
        finally
        {
            await _stateGate.WaitAsync(CancellationToken.None);
            try
            {
                await StopWorkerAsync(CancellationToken.None);
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
        if (!_policy.Enabled)
        {
            ResetFailureState();
            await StopWorkerAsync(cancellationToken);
            return;
        }
        var advertisements = CreateAdvertisements(configuration);
        if (advertisements.Count == 0)
        {
            ResetFailureState();
            await StopWorkerAsync(cancellationToken);
            return;
        }

        var policy = ResolveWorkerPolicy(configuration, advertisements);
        var fingerprint = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(new { Advertisements = advertisements, Policy = policy })))
            .ToLowerInvariant();
        if (_pendingRetry is { } pendingRetry)
        {
            if (!string.Equals(pendingRetry.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                ResetFailureState();
            }
            else if (_timeProvider.GetUtcNow() < pendingRetry.RetryUtc)
            {
                return;
            }
            else
            {
                _pendingRetry = null;
            }
        }
        if (_quarantinedFingerprint is not null)
        {
            if (string.Equals(_quarantinedFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return;
            }
            ResetFailureState();
        }
        if (_active is not null && string.Equals(_active.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return;
        }
        if (_active is not null)
        {
            ResetFailureState();
        }

        await StopWorkerAsync(cancellationToken);
        try
        {
            var package = await _catalog.ResolveAsync(DiscoveryAdapterKind, cancellationToken);
            var ticket = _authority.CreateActivation(
                _discoveryWorkerId,
                package.Manifest.WorkerId,
                _capabilities,
                _options.BootstrapLifetime,
                _options.LeaseLifetime);
            var bootstrap = new VirtualOutputWorkerBootstrap
            {
                Endpoint = _options.IpcEndpoint,
                VirtualDevice = new VirtualDeviceDefinition
                {
                    Id = _discoveryWorkerId,
                    Name = "HDHomeRun discovery",
                    Adapter = new AdapterConfiguration(DiscoveryAdapterKind, JsonSerializer.SerializeToElement(new { }))
                },
                Advertisements = advertisements,
                WorkerPolicy = policy,
                WorkerId = package.Manifest.WorkerId,
                Generation = ticket.Generation,
                BootstrapToken = ticket.BootstrapToken,
                BootstrapExpiresUtc = ticket.ExpiresUtc
            };
            var process = await _launcher.StartAsync(package, bootstrap, cancellationToken);
            var active = new ActiveDiscoveryWorker(
                fingerprint,
                policy,
                process);
            _active = active;
            _ = ObserveExitAsync(active);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _authority.Revoke(_discoveryWorkerId, "The HDHomeRun discovery worker could not be activated.");
            var decision = _restartBudget.RecordFailure(policy);
            if (decision.Exhausted)
            {
                _quarantinedFingerprint = fingerprint;
                _logger.LogError(
                    exception,
                    "Unable to activate the isolated HDHomeRun discovery worker; the restart budget was exceeded after {FailureCount} failures",
                    decision.FailureCount);
                return;
            }

            ScheduleRetry(fingerprint, decision.RetryDelay);
            _logger.LogError(
                exception,
                "Unable to activate the isolated HDHomeRun discovery worker; retrying in {RetryDelaySeconds} seconds",
                decision.RetryDelay.TotalSeconds);
        }
    }

    private IReadOnlyList<VirtualOutputAdvertisementSnapshot> CreateAdvertisements(StreamingConfiguration configuration)
    {
        var snapshots = _physicalDevices.Devices.Select(device => device.Snapshot).ToArray();
        var advertisements = new List<VirtualOutputAdvertisementSnapshot>();
        var devices = configuration.VirtualDevices
            .Where(device => device.Enabled && string.Equals(device.Adapter.Kind, HdHomeRunVirtualTunerDriver.AdapterKind, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var primaryManagementDevice = devices.FirstOrDefault(device => device.Hosting is null);
        foreach (var device in devices)
        {
            var adapter = device.Adapter.Configuration.Deserialize<HdHomeRunVirtualTunerConfiguration>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (adapter?.EnableNetworkAdvertisement != true)
            {
                continue;
            }
            var publicUri = device.Hosting?.Bindings
                .Select(binding => HdHomeRunProxyProfileResolver.TryGetHttpRoot(binding.PublicBaseUri, out var uri) ? uri : null)
                .OfType<Uri>()
                .FirstOrDefault();
            if (device.Hosting is null && HdHomeRunProxyProfileResolver.TryGetHttpRoot(adapter?.AdvertisedBaseUrl, out var managementUri))
            {
                publicUri = HdHomeRunProxyProfileResolver.GetProfileBaseUri(
                    managementUri!,
                    uint.Parse(_driver.GetDeviceId(device), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    device == primaryManagementDevice);
            }
            if (publicUri is null)
            {
                continue;
            }

            var deviceId = _driver.GetDeviceId(device);
            advertisements.Add(new VirtualOutputAdvertisementSnapshot
            {
                VirtualDeviceId = device.Id,
                AdapterKind = device.Adapter.Kind,
                Name = device.Name,
                PublicBaseUri = publicUri,
                TunerCount = _driver.GetTunerCount(device, snapshots),
                Metadata = JsonSerializer.SerializeToElement(new HdHomeRunDiscoveryAdvertisementMetadata
                {
                    DeviceId = deviceId,
                    DeviceAuth = HdHomeRunProxyIdentity.CreateDeviceAuth(device.Id.Value.ToString("N"))
                })
            });
        }
        return OrderAdvertisements(advertisements);
    }

    /// <summary>Executes order advertisements.</summary>
    /// <param name="advertisements">The advertisements value.</param>
    /// <returns>The operation's result.</returns>
    internal static IReadOnlyList<VirtualOutputAdvertisementSnapshot> OrderAdvertisements(IEnumerable<VirtualOutputAdvertisementSnapshot> advertisements) =>
        advertisements.OrderBy(
            advertisement => advertisement.Metadata.GetProperty(nameof(HdHomeRunDiscoveryAdvertisementMetadata.DeviceId)).GetString(),
            StringComparer.Ordinal).ToArray();

    /// <summary>Resolves worker policy.</summary>
    /// <param name="configuration">Configuration to apply.</param>
    /// <param name="advertisements">The advertisements value.</param>
    /// <returns>The operation's result.</returns>
    internal static VirtualOutputWorkerPolicyDefinition ResolveWorkerPolicy(StreamingConfiguration configuration, IReadOnlyList<VirtualOutputAdvertisementSnapshot> advertisements)
    {
        var advertisedIds = advertisements.Select(advertisement => advertisement.VirtualDeviceId).ToHashSet();
        var policyKey = configuration.VirtualDevices
            .Where(device => advertisedIds.Contains(device.Id))
            .Select(device => device.Hosting?.WorkerPolicyKey)
            .FirstOrDefault(key => !string.IsNullOrWhiteSpace(key));
        if (policyKey is null)
        {
            return _defaultWorkerPolicy;
        }
        return configuration.VirtualOutputWorkerPolicies.Single(policy =>
            string.Equals(policy.Key, policyKey, StringComparison.OrdinalIgnoreCase));
    }

    private async Task StopWorkerAsync(CancellationToken cancellationToken)
    {
        _authority.Revoke(_discoveryWorkerId, "The HDHomeRun discovery worker is being replaced or stopped.");
        if (_active is not { } active)
        {
            return;
        }
        _active = null;
        await active.Process.StopAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await active.Process.DisposeAsync();
    }

    private async Task ObserveExitAsync(ActiveDiscoveryWorker active)
    {
        var exitCode = await active.Process.Completion;
        await _stateGate.WaitAsync();
        try
        {
            if (ReferenceEquals(_active, active))
            {
                _active = null;
                _authority.Revoke(_discoveryWorkerId, "The HDHomeRun discovery worker exited.");
                var decision = _restartBudget.RecordFailure(active.Policy);
                if (decision.Exhausted)
                {
                    _quarantinedFingerprint = active.Fingerprint;
                    _logger.LogWarning(
                        "HDHomeRun discovery worker exited with code {ExitCode} and exceeded its restart budget after {FailureCount} failures",
                        exitCode,
                        decision.FailureCount);
                }
                else
                {
                    ScheduleRetry(active.Fingerprint, decision.RetryDelay);
                    _logger.LogWarning(
                        "HDHomeRun discovery worker exited with code {ExitCode}; retrying in {RetryDelaySeconds} seconds",
                        exitCode,
                        decision.RetryDelay.TotalSeconds);
                }
            }
        }
        finally
        {
            _stateGate.Release();
        }
        await active.Process.DisposeAsync();
    }

    private void ScheduleRetry(string fingerprint, TimeSpan delay)
    {
        _pendingRetry = new PendingRetry(fingerprint, _timeProvider.GetUtcNow().Add(delay));
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

    private void ResetFailureState()
    {
        _restartBudget.Reset();
        _pendingRetry = null;
        _quarantinedFingerprint = null;
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

    private sealed record ActiveDiscoveryWorker(string Fingerprint, VirtualOutputWorkerPolicyDefinition Policy, IVirtualOutputWorkerProcess Process);

    private sealed record PendingRetry(string Fingerprint, DateTimeOffset RetryUtc);
}
