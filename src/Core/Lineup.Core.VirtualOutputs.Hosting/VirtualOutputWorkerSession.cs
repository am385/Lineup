using System.Net.Sockets;
using System.Text.Json;
using Lineup.Core.VirtualOutputs.Protocol;
using Lineup.Core.Common.Streaming;

namespace Lineup.Core.VirtualOutputs.Hosting;

/// <summary>Reads one bounded worker bootstrap descriptor from redirected standard input.</summary>
public static class VirtualOutputWorkerBootstrapReader
{
    /// <summary>Reads and validates the single bootstrap line supplied by the trusted supervisor.</summary>
    public static async Task<VirtualOutputWorkerBootstrap> ReadAsync(TextReader? reader = null, CancellationToken cancellationToken = default)
    {
        reader ??= Console.In;
        var buffer = new char[VirtualOutputProtocol.MaximumControlMessageBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(length, 1), cancellationToken);
            if (read == 0 || buffer[length] is '\r' or '\n')
            {
                break;
            }
            length += read;
        }
        if (length == 0)
        {
            throw new InvalidDataException("The virtual-output worker bootstrap descriptor is empty.");
        }
        if (length > VirtualOutputProtocol.MaximumControlMessageBytes)
        {
            throw new InvalidDataException($"The virtual-output worker bootstrap descriptor exceeds {VirtualOutputProtocol.MaximumControlMessageBytes} characters.");
        }

        return JsonSerializer.Deserialize<VirtualOutputWorkerBootstrap>(
            new string(buffer, 0, length),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("The virtual-output worker bootstrap descriptor is invalid.");
    }
}

/// <summary>Owns worker registration and fail-closed control-plane lease renewal.</summary>
public sealed class VirtualOutputWorkerSession
{
    private readonly VirtualOutputWorkerBootstrap _bootstrap;
    private readonly VirtualOutputBrokerClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _resourceWake = new(0, 1);
    private volatile bool _monitorResources;

    /// <summary>Initializes one worker session from a supervisor bootstrap descriptor.</summary>
    public VirtualOutputWorkerSession(VirtualOutputWorkerBootstrap bootstrap, TimeProvider? timeProvider = null)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _client = new VirtualOutputBrokerClient(bootstrap.Endpoint);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Gets the capability-broker client.</summary>
    public VirtualOutputBrokerClient Broker => _client;

    /// <summary>Gets the authenticated session after registration.</summary>
    public VirtualOutputSessionContext? Context { get; private set; }

    /// <summary>Gets the current lease expiration after registration.</summary>
    public DateTimeOffset? LeaseExpiresUtc { get; private set; }

    /// <summary>Gets the negotiated protocol version after registration.</summary>
    public int? ProtocolVersion { get; private set; }

    /// <summary>Registers the worker using the one-time bootstrap credential.</summary>
    public async Task RegisterAsync(CancellationToken cancellationToken = default)
    {
        if (_timeProvider.GetUtcNow() > _bootstrap.BootstrapExpiresUtc)
        {
            throw new InvalidOperationException("The virtual-output worker bootstrap credential has expired.");
        }
        var registration = await _client.RegisterAsync(new VirtualOutputWorkerRegistrationRequest
        {
            WorkerId = _bootstrap.WorkerId,
            VirtualDeviceId = _bootstrap.VirtualDevice.Id,
            Generation = _bootstrap.Generation,
            BootstrapToken = _bootstrap.BootstrapToken,
            MinimumProtocolVersion = VirtualOutputProtocol.MinimumVersion,
            MaximumProtocolVersion = VirtualOutputProtocol.MaximumVersion
        }, cancellationToken);
        if (registration.Accepted && registration.ProtocolVersion is not (>= VirtualOutputProtocol.MinimumVersion and <= VirtualOutputProtocol.MaximumVersion))
        {
            throw new VirtualOutputClientException(VirtualOutputFailureCode.InvalidRequest,
                $"The Lineup control plane selected an unsupported worker protocol; this release requires protocol {VirtualOutputProtocol.MaximumVersion}.");
        }
        if (!registration.Accepted ||
            registration.SessionId is not { } sessionId ||
            registration.Generation != _bootstrap.Generation ||
            registration.ProtocolVersion is not (>= VirtualOutputProtocol.MinimumVersion and <= VirtualOutputProtocol.MaximumVersion) ||
            registration.LeaseExpiresUtc is not { } leaseExpiresUtc)
        {
            throw new VirtualOutputClientException(
                VirtualOutputFailureCode.Unauthorized,
                registration.Failure ?? "The Lineup control plane rejected worker registration.");
        }
        Context = new VirtualOutputSessionContext
        {
            SessionId = sessionId,
            Generation = _bootstrap.Generation
        };
        ProtocolVersion = registration.ProtocolVersion;
        LeaseExpiresUtc = leaseExpiresUtc;
    }

    /// <summary>Renews the worker lease within its current expiration deadline, invoking fail-closed shutdown on expiration or revocation.</summary>
    public async Task RunLeaseLoopAsync(Func<(bool Ready, int ActiveConnections)> readHealth, Action requestShutdown, CancellationToken cancellationToken = default)
    {
        using var resourceLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var resources = RunResourceLoopAsync(resourceLifetime.Token);
        try
        {
            await RunRenewalLoopAsync(readHealth, requestShutdown, cancellationToken);
        }
        finally
        {
            await resourceLifetime.CancelAsync();
            await resources;
        }
    }

    private async Task RunResourceLoopAsync(CancellationToken cancellationToken)
    {
        var registry = ProcessResourceRegistry.Local;
        var identity = registry.Register(Environment.ProcessId, "worker");
        var warningReported = false;
        void ReportFailure(string message) => Console.Error.WriteLine($"Resource monitoring: {message}");
        registry.ObservationFailed += ReportFailure;
        registry.Attach(identity);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_monitorResources)
                {
                    registry.ResetBaselines();
                    await _resourceWake.WaitAsync(cancellationToken);
                    continue;
                }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2), _timeProvider);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                try
                {
                    var processes = registry.Sample();
                    var response = await _client.ReportResourcesAsync(new(Context!, processes) { IncompleteInterval = registry.LastSampleIncomplete }, request.Token);
                    _monitorResources = response.Requested;
                    warningReported = false;
                }
                catch (Exception exception) when (exception is IOException or SocketException or VirtualOutputClientException ||
                    exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    if (!warningReported)
                    {
                        warningReported = true;
                        ReportFailure($"Worker counters could not be reported: {exception.Message}");
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(2), _timeProvider, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            registry.ObservationFailed -= ReportFailure;
            registry.Unregister(identity);
        }
    }

    private async Task RunRenewalLoopAsync(Func<(bool Ready, int ActiveConnections)> readHealth, Action requestShutdown, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readHealth);
        ArgumentNullException.ThrowIfNull(requestShutdown);
        var context = Context ?? throw new InvalidOperationException("The worker must register before starting lease renewal.");
        while (!cancellationToken.IsCancellationRequested)
        {
            var expiresUtc = LeaseExpiresUtc ?? _timeProvider.GetUtcNow();
            var remaining = expiresUtc - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                requestShutdown();
                return;
            }
            using var expiration = new CancellationTokenSource(remaining, _timeProvider);
            using var renewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiration.Token);
            var delay = remaining > TimeSpan.FromSeconds(3)
                ? TimeSpan.FromTicks(remaining.Ticks / 2)
                : TimeSpan.FromSeconds(1);

            try
            {
                await Task.Delay(delay < remaining ? delay : remaining, _timeProvider, renewalCancellation.Token);
                cancellationToken.ThrowIfCancellationRequested();
                if (expiration.IsCancellationRequested || _timeProvider.GetUtcNow() >= expiresUtc)
                {
                    requestShutdown();
                    return;
                }
                var health = readHealth();
                var renewal = await _client.RenewLeaseAsync(new VirtualOutputLeaseRenewalRequest
                {
                    Session = context,
                    Ready = health.Ready,
                    ActiveConnections = health.ActiveConnections
                }, renewalCancellation.Token);
                cancellationToken.ThrowIfCancellationRequested();
                var now = _timeProvider.GetUtcNow();
                if (expiration.IsCancellationRequested || now >= expiresUtc ||
                    !renewal.Accepted || renewal.Drain || renewal.LeaseExpiresUtc is not { } renewedExpiration || renewedExpiration <= now)
                {
                    requestShutdown();
                    return;
                }
                LeaseExpiresUtc = renewedExpiration;
                if (!_monitorResources && renewal.MonitorResources)
                {
                    _monitorResources = true;
                    if (_resourceWake.CurrentCount == 0)
                    {
                        _resourceWake.Release();
                    }
                }
                else if (!renewal.MonitorResources)
                {
                    _monitorResources = false;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (expiration.IsCancellationRequested)
            {
                requestShutdown();
                return;
            }
            catch (Exception exception) when (exception is IOException or SocketException or VirtualOutputClientException)
            {
                if (expiration.IsCancellationRequested || _timeProvider.GetUtcNow() >= expiresUtc)
                {
                    requestShutdown();
                    return;
                }
            }
        }
    }
}
