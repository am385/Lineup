using System.Text;
using System.Text.Json;
using Lineup.VirtualOutputs.Protocol;

namespace Lineup.VirtualOutputs.Hosting;

/// <summary>Reads one bounded worker bootstrap descriptor from redirected standard input.</summary>
public static class VirtualOutputWorkerBootstrapReader
{
    /// <summary>Reads and validates the single bootstrap line supplied by the trusted supervisor.</summary>
    public static async Task<VirtualOutputWorkerBootstrap> ReadAsync(
        TextReader? reader = null,
        CancellationToken cancellationToken = default)
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
        if (!registration.Accepted ||
            registration.SessionId is not { } sessionId ||
            registration.Generation != _bootstrap.Generation ||
            registration.ProtocolVersion is null ||
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
        LeaseExpiresUtc = leaseExpiresUtc;
    }

    /// <summary>Renews the worker lease until cancellation or revocation, invoking fail-closed shutdown on failure.</summary>
    public async Task RunLeaseLoopAsync(
        Func<(bool Ready, int ActiveConnections)> readHealth,
        Action requestShutdown,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readHealth);
        ArgumentNullException.ThrowIfNull(requestShutdown);
        var context = Context ?? throw new InvalidOperationException("The worker must register before starting lease renewal.");
        while (!cancellationToken.IsCancellationRequested)
        {
            var expiresUtc = LeaseExpiresUtc ?? _timeProvider.GetUtcNow();
            var remaining = expiresUtc - _timeProvider.GetUtcNow();
            var delay = remaining > TimeSpan.FromSeconds(3)
                ? TimeSpan.FromTicks(remaining.Ticks / 2)
                : TimeSpan.FromSeconds(1);
            await Task.Delay(delay, _timeProvider, cancellationToken);

            try
            {
                var health = readHealth();
                var renewal = await _client.RenewLeaseAsync(new VirtualOutputLeaseRenewalRequest
                {
                    Session = context,
                    Ready = health.Ready,
                    ActiveConnections = health.ActiveConnections
                }, cancellationToken);
                if (!renewal.Accepted || renewal.Drain || renewal.LeaseExpiresUtc is not { } renewedExpiration)
                {
                    requestShutdown();
                    return;
                }
                LeaseExpiresUtc = renewedExpiration;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                if (_timeProvider.GetUtcNow() >= expiresUtc)
                {
                    requestShutdown();
                    return;
                }
            }
        }
    }
}
