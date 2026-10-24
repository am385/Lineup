using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using Lineup.Common.Streaming;
using Lineup.Core.Settings;
using Lineup.Core.Streaming;
using Lineup.VirtualOutputs.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lineup.VirtualOutputs.Runtime;

/// <summary>Hosts the authenticated local capability broker over a named pipe or Unix-domain socket.</summary>
public sealed class VirtualOutputIpcServer : BackgroundService
{
    private readonly VirtualOutputIpcEndpoint _endpoint;
    private readonly IVirtualOutputSessionAuthority _authority;
    private readonly IVirtualOutputBroker _broker;
    private readonly IVirtualOutputRuntimeStatus _status;
    private readonly ILineupSettingsService _settings;
    private readonly IActiveStreamRegistry _activeStreams;
    private readonly ILogger<VirtualOutputIpcServer> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<VirtualDeviceId, int> _outstandingCalls = new();
    private readonly VirtualOutputBrokerStreamRegistry _brokerStreams;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Socket? _unixListener;

    /// <summary>Initializes the local IPC server.</summary>
    public VirtualOutputIpcServer(
        VirtualOutputIpcEndpoint endpoint,
        IVirtualOutputSessionAuthority authority,
        IVirtualOutputBroker broker,
        IVirtualOutputRuntimeStatus status,
        ILineupSettingsService settings,
        IActiveStreamRegistry activeStreams,
        ILogger<VirtualOutputIpcServer> logger)
    {
        _endpoint = endpoint;
        _authority = authority;
        _broker = broker;
        _status = status;
        _settings = settings;
        _activeStreams = activeStreams;
        _logger = logger;
        _brokerStreams = new VirtualOutputBrokerStreamRegistry(authority, logger);
        ValidateEndpoint(endpoint);
    }

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);
        await _ready.Task.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var brokerStreamCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var expirationTask = _brokerStreams.RunExpirationLoopAsync(brokerStreamCancellation.Token);
        try
        {
            if (_endpoint.Transport == VirtualOutputIpcTransport.NamedPipe)
            {
                await RunNamedPipeAsync(stoppingToken);
            }
            else
            {
                await RunUnixSocketAsync(stoppingToken);
            }
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
            throw;
        }
        finally
        {
            brokerStreamCancellation.Cancel();
            await expirationTask;
            await _brokerStreams.DisposeAllAsync();
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _unixListener?.Dispose();
        await base.StopAsync(cancellationToken);
        DeleteUnixSocket();
    }

    private async Task RunNamedPipeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                _endpoint.Address,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough);
            _ready.TrySetResult();
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
            _ = HandleOwnedConnectionAsync(pipe, cancellationToken);
        }
    }

    private async Task RunUnixSocketAsync(CancellationToken cancellationToken)
    {
        DeleteUnixSocket();
        var directory = Path.GetDirectoryName(_endpoint.Address)!;
        Directory.CreateDirectory(directory);
        _unixListener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _unixListener.Bind(new UnixDomainSocketEndPoint(_endpoint.Address));
        _unixListener.Listen(128);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_endpoint.Address, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        _ready.TrySetResult();

        while (!cancellationToken.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _unixListener.AcceptAsync(cancellationToken);
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            _ = HandleOwnedConnectionAsync(new NetworkStream(socket, ownsSocket: true), cancellationToken);
        }
    }

    private async Task HandleOwnedConnectionAsync(Stream stream, CancellationToken serverCancellationToken)
    {
        await using (stream)
        {
            try
            {
                var request = await VirtualOutputWireProtocol.ReadAsync<VirtualOutputIpcRequest>(stream, serverCancellationToken);
                await DispatchAsync(stream, request, serverCancellationToken);
            }
            catch (OperationCanceledException) when (serverCancellationToken.IsCancellationRequested)
            {
            }
            catch (VirtualOutputBrokerException exception)
            {
                await TryWriteErrorAsync(stream, exception.FailureCode, exception.Message, serverCancellationToken);
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException)
            {
                _logger.LogWarning("Rejected invalid virtual-output IPC request: {Error}", exception.Message);
                await TryWriteErrorAsync(stream, VirtualOutputFailureCode.InvalidRequest, exception.Message, serverCancellationToken);
            }
            catch (IOException exception)
            {
                _logger.LogDebug(exception, "Virtual-output IPC client disconnected");
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Unhandled virtual-output IPC request failure");
                await TryWriteErrorAsync(stream, VirtualOutputFailureCode.Unavailable, "The capability broker could not complete the request.", serverCancellationToken);
            }
        }
    }

    private async Task DispatchAsync(Stream stream, VirtualOutputIpcRequest request, CancellationToken cancellationToken)
    {
        switch (request.Operation)
        {
            case VirtualOutputIpcOperation.Register:
                await RegisterAsync(stream, request.ReadPayload<VirtualOutputWorkerRegistrationRequest>(), cancellationToken);
                break;
            case VirtualOutputIpcOperation.RenewLease:
                await RenewLeaseAsync(stream, request.ReadPayload<VirtualOutputLeaseRenewalRequest>(), cancellationToken);
                break;
            case VirtualOutputIpcOperation.GetManifest:
                await GetManifestAsync(stream, request.ReadPayload<VirtualOutputManifestRequest>(), cancellationToken);
                break;
            case VirtualOutputIpcOperation.GetLineup:
                await GetLineupAsync(stream, request.ReadPayload<VirtualOutputLineupRequest>(), cancellationToken);
                break;
            case VirtualOutputIpcOperation.GetGuide:
                await GetGuideAsync(stream, request.ReadPayload<VirtualOutputGuideRequest>(), cancellationToken);
                break;
            case VirtualOutputIpcOperation.OpenStream:
                await WriteStreamAsync(stream, request.ReadPayload<VirtualOutputOpenStreamRequest>(), cancellationToken);
                break;
            case VirtualOutputIpcOperation.CloseStream:
                await CloseStreamAsync(stream, request.ReadPayload<VirtualOutputCloseStreamRequest>(), cancellationToken);
                break;
            case VirtualOutputIpcOperation.ReportStreamStatus:
                await ReportStreamStatusAsync(stream, request.ReadPayload<VirtualOutputStreamStatusReport>(), cancellationToken);
                break;
            case VirtualOutputIpcOperation.ReportHealth:
                await ReportHealthAsync(stream, request.ReadPayload<VirtualOutputWorkerHealthReport>(), cancellationToken);
                break;
            default:
                throw new InvalidDataException($"Unsupported IPC operation {request.Operation}.");
        }
    }

    private async Task GetManifestAsync(
            Stream stream,
            VirtualOutputManifestRequest request,
            CancellationToken cancellationToken)
    {
        using var lease = AcquireBrokerCall(request.Session, VirtualOutputCapability.ReadManifest);
        await WriteAsync(stream, await _broker.GetManifestAsync(request, cancellationToken), cancellationToken);
    }

    private async Task GetLineupAsync(
            Stream stream,
            VirtualOutputLineupRequest request,
            CancellationToken cancellationToken)
    {
        using var lease = AcquireBrokerCall(request.Session, VirtualOutputCapability.ReadLineup);
        await WriteAsync(stream, await _broker.GetLineupAsync(request, cancellationToken), cancellationToken);
    }

    private async Task GetGuideAsync(
            Stream stream,
            VirtualOutputGuideRequest request,
            CancellationToken cancellationToken)
    {
        using var lease = AcquireBrokerCall(request.Session, VirtualOutputCapability.ReadGuide);
        await VirtualOutputWireProtocol.WriteAsync(
            stream,
            VirtualOutputIpcResponse.Success(await _broker.GetGuideAsync(request, cancellationToken)),
            VirtualOutputProtocol.MaximumGuideMessageBytes,
            cancellationToken);
    }

    private async Task RegisterAsync(
        Stream stream,
        VirtualOutputWorkerRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        var response = _authority.Register(request);
        if (!response.Accepted)
        {
            await WriteAsync(stream, response, cancellationToken);
            return;
        }
        _status.Update(request.VirtualDeviceId, current => new VirtualOutputRuntimeSnapshot
        {
            VirtualDeviceId = request.VirtualDeviceId,
            State = VirtualOutputRuntimeState.Starting,
            Generation = response.Generation,
            ProcessId = current?.ProcessId,
            ProtocolVersion = response.ProtocolVersion,
            Bindings = current?.Bindings ?? [],
            RestartCount = current?.RestartCount ?? 0
        });
        await WriteAsync(stream, response, cancellationToken);
    }

    private async Task RenewLeaseAsync(
        Stream stream,
        VirtualOutputLeaseRenewalRequest request,
        CancellationToken cancellationToken)
    {
        var response = _authority.Renew(request);
        if (response.Accepted)
        {
            var authorization = _authority.Authorize(request.Session, VirtualOutputCapability.ReportHealth);
            if (authorization.Authorized)
            {
                var virtualDeviceId = authorization.VirtualDeviceId!.Value;
                _status.Update(virtualDeviceId, current => new VirtualOutputRuntimeSnapshot
                {
                    VirtualDeviceId = virtualDeviceId,
                    State = request.Ready ? VirtualOutputRuntimeState.Healthy : VirtualOutputRuntimeState.Degraded,
                    Generation = request.Session.Generation,
                    ProcessId = current?.ProcessId,
                    ProtocolVersion = current?.ProtocolVersion,
                    Bindings = current?.Bindings ?? [],
                    ActiveConnections = request.ActiveConnections,
                    ActiveBrokerStreams = _brokerStreams.Count(virtualDeviceId),
                    RestartCount = current?.RestartCount ?? 0,
                    ReadySinceUtc = request.Ready
                        ? current?.ReadySinceUtc ?? DateTimeOffset.UtcNow
                        : current?.ReadySinceUtc,
                    LastFailure = request.Ready ? null : current?.LastFailure
                });
            }
        }
        await WriteAsync(stream, response, cancellationToken);
    }

    private async Task WriteStreamAsync(Stream stream, VirtualOutputOpenStreamRequest request, CancellationToken cancellationToken)
    {
        using var lease = AcquireBrokerCall(request.Session, VirtualOutputCapability.OpenStream);
        var output = await _broker.OpenStreamAsync(request, cancellationToken);
        if (!_brokerStreams.TryAdd(lease.VirtualDeviceId, request, output))
        {
            await output.DisposeAsync();
            throw new VirtualOutputBrokerException(VirtualOutputFailureCode.InvalidRequest, "The stream request ID is already active.");
        }
        try
        {
            await VirtualOutputWireProtocol.WriteAsync(stream, VirtualOutputIpcResponse.Success(output.Response), cancellationToken);
            await output.Stream.CopyToAsync(stream, cancellationToken);
        }
        finally
        {
            await _brokerStreams.CompleteAsync(request.RequestId, output);
        }
    }

    private async Task CloseStreamAsync(Stream stream, VirtualOutputCloseStreamRequest request, CancellationToken cancellationToken)
    {
        var validation = VirtualOutputProtocolValidation.Validate(request);
        if (validation.Count > 0)
        {
            throw new VirtualOutputBrokerException(VirtualOutputFailureCode.InvalidRequest, string.Join(" ", validation));
        }
        _ = AuthorizeBrokerCall(request.Session, VirtualOutputCapability.OpenStream);
        var closed = await _brokerStreams.CloseAsync(request);
        await WriteAsync(stream, new VirtualOutputCloseStreamResponse { Closed = closed }, cancellationToken);
    }

    private async Task ReportStreamStatusAsync(Stream stream, VirtualOutputStreamStatusReport report, CancellationToken cancellationToken)
    {
        var validation = VirtualOutputProtocolValidation.Validate(report);
        if (validation.Count > 0)
        {
            throw new VirtualOutputBrokerException(VirtualOutputFailureCode.InvalidRequest, string.Join(" ", validation));
        }
        _ = AuthorizeBrokerCall(report.Session, VirtualOutputCapability.OpenStream);
        if (!_brokerStreams.Matches(report.RequestId, report.Session, report.StreamSessionId))
        {
            throw new VirtualOutputBrokerException(
                VirtualOutputFailureCode.InvalidRequest,
                "The worker stream-status report does not match an active broker stream.");
        }
        var updated = _activeStreams.TryUpdate(report.StreamSessionId.ToString(), current => current with
        {
            Format = report.Format,
            Tracks = report.Tracks,
            PhysicalInput = report.PhysicalInput ?? current.PhysicalInput,
            PhysicalOutput = report.PhysicalOutput ?? current.PhysicalOutput,
            VirtualOutput = report.VirtualOutput ?? current.VirtualOutput,
            BrokerRequestId = report.RequestId.ToString(),
            WorkerMediaSessionId = report.WorkerMediaSessionId,
            WorkerStartupStage = report.StartupStage.ToString(),
            WorkerLastFailure = report.LastFailure
        });
        await WriteAsync(stream, new VirtualOutputStreamStatusResponse { Updated = updated }, cancellationToken);
    }

    private BrokerCallLease AcquireBrokerCall(
        VirtualOutputSessionContext session,
        VirtualOutputCapability capability)
    {
        var virtualDeviceId = AuthorizeBrokerCall(session, capability);
        var device = _settings.Settings.Streaming.VirtualDevices.Single(candidate => candidate.Id == virtualDeviceId);
        var workerPolicyKey = device.Hosting!.WorkerPolicyKey!;
        var policy = _settings.Settings.Streaming.VirtualOutputWorkerPolicies.Single(candidate =>
            string.Equals(candidate.Key, workerPolicyKey, StringComparison.OrdinalIgnoreCase));
        var count = _outstandingCalls.AddOrUpdate(virtualDeviceId, 1, (_, current) => checked(current + 1));
        if (count > policy.MaximumOutstandingBrokerCalls)
        {
            ReleaseBrokerCall(virtualDeviceId);
            throw new VirtualOutputBrokerException(
                VirtualOutputFailureCode.ResourceLimit,
                "The virtual-output worker exceeded its concurrent broker-call limit.");
        }
        return new BrokerCallLease(this, virtualDeviceId);
    }

    private VirtualDeviceId AuthorizeBrokerCall(
        VirtualOutputSessionContext session,
        VirtualOutputCapability capability)
    {
        var authorization = _authority.Authorize(session, capability);
        if (!authorization.Authorized)
        {
            throw new VirtualOutputBrokerException(
                authorization.FailureCode ?? VirtualOutputFailureCode.Unauthorized,
                authorization.Failure ?? "The worker request is not authorized.");
        }
        var virtualDeviceId = authorization.VirtualDeviceId!.Value;
        var device = _settings.Settings.Streaming.VirtualDevices.SingleOrDefault(candidate => candidate.Id == virtualDeviceId)
            ?? throw new VirtualOutputBrokerException(VirtualOutputFailureCode.Unavailable, "The assigned virtual output is unavailable.");
        var workerPolicyKey = device.Hosting?.WorkerPolicyKey
            ?? throw new VirtualOutputBrokerException(VirtualOutputFailureCode.Unavailable, "The assigned virtual output has no worker policy.");
        _ = _settings.Settings.Streaming.VirtualOutputWorkerPolicies.Single(candidate =>
            string.Equals(candidate.Key, workerPolicyKey, StringComparison.OrdinalIgnoreCase));
        return virtualDeviceId;
    }

    private void ReleaseBrokerCall(VirtualDeviceId virtualDeviceId)
    {
        _outstandingCalls.AddOrUpdate(
            virtualDeviceId,
            0,
            (_, current) => Math.Max(0, current - 1));
    }

    private async Task ReportHealthAsync(Stream stream, VirtualOutputWorkerHealthReport report, CancellationToken cancellationToken)
    {
        var authorization = _authority.Authorize(report.Session, VirtualOutputCapability.ReportHealth);
        if (!authorization.Authorized)
        {
            throw new VirtualOutputBrokerException(
                authorization.FailureCode ?? VirtualOutputFailureCode.Unauthorized,
                authorization.Failure ?? "The worker health report is not authorized.");
        }
        var virtualDeviceId = authorization.VirtualDeviceId!.Value;
        _status.Update(virtualDeviceId, current => new VirtualOutputRuntimeSnapshot
        {
            VirtualDeviceId = virtualDeviceId,
            State = report.Ready ? VirtualOutputRuntimeState.Healthy : VirtualOutputRuntimeState.Degraded,
            Generation = report.Session.Generation,
            Bindings = report.Bindings,
            ActiveConnections = report.ActiveConnections,
            ActiveBrokerStreams = _brokerStreams.Count(virtualDeviceId),
            ProcessId = current?.ProcessId,
            ProtocolVersion = current?.ProtocolVersion,
            RestartCount = current?.RestartCount ?? 0,
            ReadySinceUtc = report.Ready
                ? current?.ReadySinceUtc ?? DateTimeOffset.UtcNow
                : current?.ReadySinceUtc,
            LastFailure = report.Failure
        });
        await WriteAsync(stream, new { accepted = true }, cancellationToken);
    }

    private static Task WriteAsync<T>(Stream stream, T response, CancellationToken cancellationToken) =>
        VirtualOutputWireProtocol.WriteAsync(stream, VirtualOutputIpcResponse.Success(response), cancellationToken);

    private static async Task TryWriteErrorAsync(
        Stream stream,
        VirtualOutputFailureCode failureCode,
        string failure,
        CancellationToken cancellationToken)
    {
        try
        {
            await VirtualOutputWireProtocol.WriteAsync(stream, VirtualOutputIpcResponse.Error(failureCode, failure), cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private static void ValidateEndpoint(VirtualOutputIpcEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.Address);
        if (endpoint.Transport == VirtualOutputIpcTransport.NamedPipe && endpoint.Address.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Named-pipe address contains invalid characters.", nameof(endpoint));
        }
        if (endpoint.Transport == VirtualOutputIpcTransport.UnixDomainSocket && !Path.IsPathFullyQualified(endpoint.Address))
        {
            throw new ArgumentException("Unix-domain socket address must be absolute.", nameof(endpoint));
        }
    }

    private sealed class BrokerCallLease : IDisposable
    {
        private readonly VirtualOutputIpcServer _owner;
        private int _disposed;

        internal BrokerCallLease(VirtualOutputIpcServer owner, VirtualDeviceId virtualDeviceId)
        {
            _owner = owner;
            VirtualDeviceId = virtualDeviceId;
        }

        internal VirtualDeviceId VirtualDeviceId { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.ReleaseBrokerCall(VirtualDeviceId);
            }
        }
    }

    private void DeleteUnixSocket()
    {
        if (_endpoint.Transport == VirtualOutputIpcTransport.UnixDomainSocket && File.Exists(_endpoint.Address))
        {
            File.Delete(_endpoint.Address);
        }
    }
}
