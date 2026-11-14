using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Settings;
using Lineup.Core.Streaming;
using Lineup.Core.Streaming.FFmpeg;
using Lineup.Core.VirtualOutputs.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lineup.Core.VirtualOutputs.Runtime;

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
    private readonly IPluginConnectionBudget _connectionBudget;
    private readonly IMediaWorkAdmission _mediaWork;
    private readonly StreamingResourceOptions _resources;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(VirtualOutputSessionContext Session, string ConnectionId), byte> _connectionOwners = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<VirtualDeviceId, int> _outstandingCalls = new();
    private readonly VirtualOutputBrokerStreamRegistry _brokerStreams;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Socket? _unixListener;
    private readonly HashSet<Task> _connectionHandlers = [];

    /// <summary>Initializes the local IPC server.</summary>
    /// <param name="endpoint">The local IPC listener endpoint.</param>
    /// <param name="authority">The worker session authorization authority.</param>
    /// <param name="broker">The host capability broker.</param>
    /// <param name="status">The worker runtime status store.</param>
    /// <param name="settings">The host settings service.</param>
    /// <param name="activeStreams">The host stream registry.</param>
    /// <param name="logger">The IPC service logger.</param>
    /// <param name="connectionBudget">The shared plugin connection budget.</param>
    /// <param name="mediaWork">Optional shared media admission accounting.</param>
    /// <param name="resources">Optional streaming resource limits.</param>
    /// <param name="timeProvider">Optional clock for broker stream expiration sweeps.</param>
    public VirtualOutputIpcServer(
        VirtualOutputIpcEndpoint endpoint,
        IVirtualOutputSessionAuthority authority,
        IVirtualOutputBroker broker,
        IVirtualOutputRuntimeStatus status,
        ILineupSettingsService settings,
        IActiveStreamRegistry activeStreams,
        ILogger<VirtualOutputIpcServer> logger,
        IPluginConnectionBudget connectionBudget,
        IMediaWorkAdmission? mediaWork = null,
        StreamingResourceOptions? resources = null,
        TimeProvider? timeProvider = null
    )
    {
        _endpoint = endpoint;
        _authority = authority;
        _broker = broker;
        _status = status;
        _settings = settings;
        _activeStreams = activeStreams;
        _logger = logger;
        _connectionBudget = connectionBudget ?? throw new ArgumentNullException(nameof(connectionBudget));
        _resources = resources ?? new();
        _mediaWork = mediaWork ?? new MediaWorkAdmission(_resources);
        _brokerStreams = new VirtualOutputBrokerStreamRegistry(authority, logger, timeProvider);
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
        Task listenerTask = Task.CompletedTask;
        try
        {
            listenerTask = _endpoint.Transport == VirtualOutputIpcTransport.NamedPipe
                ? RunNamedPipeAsync(brokerStreamCancellation.Token)
                : RunUnixSocketAsync(brokerStreamCancellation.Token);
            var completed = await Task.WhenAny(listenerTask, expirationTask);
            await completed;
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
            throw;
        }
        finally
        {
            try
            {
                brokerStreamCancellation.Cancel();
                await Task.WhenAll(listenerTask, expirationTask);
            }
            finally
            {
                try
                {
                    Task[] handlers;
                    lock (_connectionHandlers)
                    {
                        handlers = _connectionHandlers.ToArray();
                    }
                    await Task.WhenAll(handlers);
                }
                finally
                {
                    await _brokerStreams.DisposeAllAsync();
                }
            }
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
            TrackConnection(pipe, cancellationToken);
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
            TrackConnection(new NetworkStream(socket, ownsSocket: true), cancellationToken);
        }
    }

    private void TrackConnection(Stream stream, CancellationToken cancellationToken)
    {
        var handler = HandleOwnedConnectionAsync(stream, cancellationToken);
        lock (_connectionHandlers)
        {
            _connectionHandlers.Add(handler);
        }
        _ = ObserveConnectionHandlerAsync(handler);
    }

    private async Task ObserveConnectionHandlerAsync(Task handler)
    {
        try
        {
            await handler;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Virtual-output IPC connection ownership cleanup failed.");
        }
        finally
        {
            lock (_connectionHandlers)
            {
                _connectionHandlers.Remove(handler);
            }
        }
    }

    private async Task HandleOwnedConnectionAsync(Stream stream, CancellationToken serverCancellationToken)
    {
        await using (stream)
        {
            Lineup.Core.Extensibility.Diagnostics.LineupTelemetry.Operation? operation = null;
            IDisposable? scope = null;
            try
            {
                var request = await VirtualOutputWireProtocol.ReadAsync<VirtualOutputIpcRequest>(stream, serverCancellationToken);
                if (request.TraceParent is not null && (request.TraceParent.Length != 55 ||
                    !System.Diagnostics.ActivityContext.TryParse(request.TraceParent, null, out _)))
                {
                    throw new InvalidDataException("The broker trace context is invalid.");
                }
                operation = Lineup.Core.Extensibility.Diagnostics.LineupTelemetry.Begin("broker", request.TraceParent);
                if (request.Payload.ValueKind == JsonValueKind.Object)
                {
                    var identity = request.Payload.TryGetProperty("session", out var session) && session.ValueKind == JsonValueKind.Object ? session : request.Payload;
                    if (identity.TryGetProperty("generation", out var generation) && generation.ValueKind == JsonValueKind.Number && generation.TryGetInt64(out var number))
                    {
                        System.Diagnostics.Activity.Current?.SetTag("lineup.worker.generation", number);
                    }
                    if (request.Payload.TryGetProperty("requestId", out var requestId) && requestId.ValueKind == JsonValueKind.Object &&
                        requestId.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var id))
                    {
                        System.Diagnostics.Activity.Current?.SetTag("lineup.broker.request_id", id.ToString("N"));
                    }
                }
                scope = _logger.BeginScope(new Dictionary<string, object>
                {
                    ["CorrelationId"] = System.Diagnostics.Activity.Current!.TraceId.ToString()
                });
                await DispatchAsync(stream, request, serverCancellationToken);
                operation.Succeed();
            }
            catch (OperationCanceledException) when (serverCancellationToken.IsCancellationRequested)
            {
                operation?.Cancel();
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
            finally
            {
                scope?.Dispose();
                operation?.Dispose();
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
            case VirtualOutputIpcOperation.AdmitConnection:
                await AdmitConnectionAsync(stream, request.ReadPayload<VirtualOutputConnectionAdmissionRequest>(), cancellationToken);
                break;
            case VirtualOutputIpcOperation.ReserveMediaWork:
                await ReserveMediaWorkAsync(stream, request.ReadPayload<VirtualOutputMediaWorkRequest>(), cancellationToken);
                break;
            default:
                throw new InvalidDataException($"Unsupported IPC operation {request.Operation}.");
        }
    }

    private async Task ReserveMediaWorkAsync(Stream stream, VirtualOutputMediaWorkRequest request, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responses = System.Threading.Channels.Channel.CreateBounded<bool>(1);
        var control = new SemaphoreSlim(1, 1);
        async Task<bool> RetireAsync(CancellationToken token)
        {
            if (finished.Task.IsCompleted)
            {
                return true;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var entered = false;
            try
            {
                await ready.Task.WaitAsync(timeout.Token);
                entered = await control.WaitAsync(0, timeout.Token);
                if (!entered)
                {
                    return false;
                }
                await stream.WriteAsync(new byte[] { 1 }, timeout.Token);
                await stream.FlushAsync(timeout.Token);
                var reply = responses.Reader.ReadAsync(timeout.Token).AsTask();
                if (await Task.WhenAny(reply, finished.Task) == finished.Task)
                {
                    return true;
                }
                return await reply;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                await finished.Task.WaitAsync(token);
                return true;
            }
            finally
            {
                if (entered)
                {
                    control.Release();
                }
            }
        }
        try
        {
            using var call = AcquireBrokerCall(request.Session, VirtualOutputCapability.OpenStream);
            await using var permit = await _mediaWork.AcquireAsync(request.IsProcessing ? MediaWorkKind.Processing : MediaWorkKind.Producer,
                request.Reclaimable ? RetireAsync : null, cancellationToken);
            await WriteAsync(stream, new VirtualOutputMediaWorkResponse(_resources.MinimumMediaFreeSpaceMib), cancellationToken);
            call.Dispose();
            ready.TrySetResult();
            var revocation = ObserveConnectionRevocationAsync(request.Session, lifetime.Token, VirtualOutputCapability.OpenStream);
            var command = new byte[1];
            try
            {
                while (true)
                {
                    var read = stream.ReadAsync(command, lifetime.Token).AsTask();
                    if (await Task.WhenAny(read, revocation) == revocation)
                    {
                        await revocation;
                        await stream.WriteAsync(new byte[] { 2 }, cancellationToken);
                        await stream.FlushAsync(cancellationToken);
                        while (await read != 0)
                        {
                            if (command[0] == 3)
                            {
                                await permit.DisposeAsync();
                                await stream.WriteAsync(new byte[] { 3 }, cancellationToken);
                                await stream.FlushAsync(cancellationToken);
                                break;
                            }
                            read = stream.ReadAsync(command, lifetime.Token).AsTask();
                        }
                        break;
                    }
                    if (await read == 0)
                    {
                        break;
                    }
                    if (command[0] == 3)
                    {
                        await permit.DisposeAsync();
                        await stream.WriteAsync(new byte[] { 3 }, cancellationToken);
                        await stream.FlushAsync(cancellationToken);
                        break;
                    }
                    if (command[0] > 1)
                    {
                        throw new InvalidDataException("Invalid media retirement acknowledgement.");
                    }
                    if (!responses.Writer.TryWrite(command[0] == 1))
                    {
                        throw new InvalidDataException("Unexpected media retirement acknowledgement.");
                    }
                }
            }
            finally
            {
                lifetime.Cancel();
                try { await revocation; }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            }
        }
        catch (MediaResourceLimitException exception)
        {
            throw new VirtualOutputBrokerException(VirtualOutputFailureCode.ResourceLimit, exception.Message, exception);
        }
        finally
        {
            ready.TrySetCanceled();
            finished.TrySetResult();
        }
    }

    private async Task AdmitConnectionAsync(Stream stream, VirtualOutputConnectionAdmissionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionId) || request.ConnectionId.Length > 128)
        {
            throw new InvalidDataException("A connection identity of at most 128 characters is required.");
        }
        var authorization = AuthorizeConnection(request.Session);
        var key = (request.Session, request.ConnectionId);
        if (!_connectionOwners.TryAdd(key, 0))
        {
            throw new VirtualOutputBrokerException(VirtualOutputFailureCode.InvalidRequest, "This connection already owns an admission channel.");
        }
        try
        {
            await using var permit = _connectionBudget.TryAcquire(authorization.VirtualDeviceId
                ?? throw new InvalidOperationException("An authorized connection owner must identify its assigned virtual device."))
                ?? throw new VirtualOutputBrokerException(VirtualOutputFailureCode.ResourceLimit, "The deployment-wide plugin connection limit has been reached.");
            _ = AuthorizeConnection(request.Session);
            await WriteAsync(stream, new VirtualOutputConnectionAdmissionResponse(true), cancellationToken);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var disconnected = ObserveConnectionDisconnectAsync(stream, lifetime.Token);
            var revoked = ObserveConnectionRevocationAsync(request.Session, lifetime.Token);
            try
            {
                var completed = await Task.WhenAny(disconnected, revoked);
                await completed;
                if (completed == revoked)
                {
                    await stream.WriteAsync(new byte[] { 1 }, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    await disconnected;
                }
            }
            finally
            {
                lifetime.Cancel();
                try
                {
                    await Task.WhenAll(disconnected, revoked);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                }
            }
        }
        finally
        {
            _connectionOwners.TryRemove(key, out _);
        }
    }

    private VirtualOutputAuthorizationResult AuthorizeConnection(VirtualOutputSessionContext session)
    {
        var authorization = _authority.Authorize(session, VirtualOutputCapability.AdmitConnection);
        if (!authorization.Authorized)
        {
            throw new VirtualOutputBrokerException(authorization.FailureCode ?? VirtualOutputFailureCode.Unauthorized,
                authorization.Failure ?? "Connection admission is not authorized.");
        }
        return authorization;
    }

    private async Task ObserveConnectionRevocationAsync(VirtualOutputSessionContext session, CancellationToken cancellationToken, VirtualOutputCapability capability = VirtualOutputCapability.AdmitConnection)
    {
        while (_authority.Authorize(session, capability).Authorized)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task ObserveConnectionDisconnectAsync(Stream stream, CancellationToken cancellationToken)
    {
        var received = await stream.ReadAsync(new byte[1], cancellationToken);
        if (received != 0)
        {
            _logger.LogWarning("A connection-admission owner sent unexpected control data; its ownership channel will close.");
        }
    }

    private async Task GetManifestAsync(Stream stream, VirtualOutputManifestRequest request, CancellationToken cancellationToken)
    {
        using var lease = AcquireBrokerCall(request.Session, VirtualOutputCapability.ReadManifest);
        await WriteAsync(stream, await _broker.GetManifestAsync(request, cancellationToken), cancellationToken);
    }

    private async Task GetLineupAsync(Stream stream, VirtualOutputLineupRequest request, CancellationToken cancellationToken)
    {
        using var lease = AcquireBrokerCall(request.Session, VirtualOutputCapability.ReadLineup);
        await WriteAsync(stream, await _broker.GetLineupAsync(request, cancellationToken), cancellationToken);
    }

    private async Task GetGuideAsync(Stream stream, VirtualOutputGuideRequest request, CancellationToken cancellationToken)
    {
        using var lease = AcquireBrokerCall(request.Session, VirtualOutputCapability.ReadGuide);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var disconnected = CancelGuideOnDisconnectAsync(stream, lifetime);
        try
        {
            var response = await _broker.GetGuideAsync(request, lifetime.Token);
            await VirtualOutputWireProtocol.WriteGuideAsync(stream, response, lifetime.Token);
        }
        catch (Lineup.Core.Common.Guides.GuideTransferLimitException exception)
        {
            _logger.LogWarning("Guide transfer rejected: {Error}", exception.Message);
            throw new VirtualOutputBrokerException(VirtualOutputFailureCode.ResourceLimit, exception.Message, exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The guide client disconnected before the transfer completed.", exception);
        }
        finally
        {
            await lifetime.CancelAsync();
            await disconnected;
        }
    }

    private static async Task CancelGuideOnDisconnectAsync(Stream stream, CancellationTokenSource lifetime)
    {
        try
        {
            _ = await stream.ReadAsync(new byte[1], lifetime.Token);
            await lifetime.CancelAsync();
        }
        catch (IOException)
        {
            await lifetime.CancelAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task RegisterAsync(Stream stream, VirtualOutputWorkerRegistrationRequest request, CancellationToken cancellationToken)
    {
        var response = _authority.Register(request);
        if (!response.Accepted)
        {
            await WriteAsync(stream, response, cancellationToken);
            return;
        }
        UpdateWorkerStatus(request.VirtualDeviceId, request.Generation, current => new VirtualOutputRuntimeSnapshot
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

    private async Task RenewLeaseAsync(Stream stream, VirtualOutputLeaseRenewalRequest request, CancellationToken cancellationToken)
    {
        var response = _authority.Renew(request);
        if (response.Accepted)
        {
            var authorization = _authority.Authorize(request.Session, VirtualOutputCapability.ReportHealth);
            if (authorization.Authorized)
            {
                var virtualDeviceId = authorization.VirtualDeviceId!.Value;
                UpdateWorkerStatus(virtualDeviceId, request.Session.Generation, current => new VirtualOutputRuntimeSnapshot
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
        using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var disconnected = MonitorStreamDisconnectAsync(stream, streamCancellation);
        try
        {
            var output = await _broker.OpenStreamAsync(request, streamCancellation.Token);
            if (!_brokerStreams.TryAdd(lease.VirtualDeviceId, request, output))
            {
                await output.DisposeAsync();
                throw new VirtualOutputBrokerException(VirtualOutputFailureCode.InvalidRequest, "The stream request ID is already active.");
            }
            try
            {
                streamCancellation.Token.ThrowIfCancellationRequested();
                await VirtualOutputWireProtocol.WriteAsync(stream, VirtualOutputIpcResponse.Success(output.Response), streamCancellation.Token);
                await output.Stream.CopyToAsync(stream, streamCancellation.Token);
            }
            catch (Exception exception) when (exception is MediaProcessException or MpegTsTranscodeException or MediaInputException)
            {
                MediaProcessDiagnostics.LogFailure(_logger, exception, "broker-stream", output.Response.StreamSessionId?.ToString() ?? request.RequestId.ToString(),
                    request.RequestId.ToString(), output.Response.StreamSessionId?.ToString());
            }
            finally
            {
                await _brokerStreams.CompleteAsync(request.RequestId, output);
            }
        }
        catch (OperationCanceledException exception) when (exception.CancellationToken == streamCancellation.Token
            && streamCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Virtual-output streaming IPC client disconnected for request {RequestId}", request.RequestId);
        }
        finally
        {
            try
            {
                await streamCancellation.CancelAsync();
            }
            finally
            {
                await disconnected;
            }
        }
    }

    private async Task MonitorStreamDisconnectAsync(Stream stream, CancellationTokenSource cancellation)
    {
        try
        {
            // After its single request frame, a streaming connection carries only server-to-client media.
            if (await stream.ReadAsync(new byte[1], cancellation.Token) != 0)
            {
                _logger.LogWarning("Rejected unexpected client data on a virtual-output streaming IPC connection");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (IOException exception)
        {
            _logger.LogDebug(exception, "Virtual-output streaming IPC client disconnected");
        }
        finally
        {
            await cancellation.CancelAsync();
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
        var ownedStream = _brokerStreams.GetOwnedStream(report.RequestId, report.Session, report.StreamSessionId);
        if (report.ReleaseViewer)
        {
            ownedStream.ReleaseViewer();
            await WriteAsync(stream, new VirtualOutputStreamStatusResponse { Updated = true, StopRequested = ownedStream.ViewerStopRequested }, cancellationToken);
            return;
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
            Flow = report.WorkerProducerId is { } producerId && current.Flow is { } flow
                ? flow with { ReusableOutputId = new StreamSessionId(Guid.ParseExact(producerId, "N")) }
                : current.Flow,
            WorkerStartupStage = report.StartupStage.ToString(),
            WorkerLastFailure = report.LastFailure
        });
        if (updated && report.StartupStage != VirtualOutputStreamStartupStage.Starting)
        {
            _activeStreams.CompleteStartup(report.StreamSessionId.ToString());
        }
        await WriteAsync(stream, new VirtualOutputStreamStatusResponse { Updated = updated, StopRequested = ownedStream.ViewerStopRequested }, cancellationToken);
    }

    private BrokerCallLease AcquireBrokerCall(VirtualOutputSessionContext session, VirtualOutputCapability capability)
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

    private VirtualDeviceId AuthorizeBrokerCall(VirtualOutputSessionContext session, VirtualOutputCapability capability)
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
        UpdateWorkerStatus(virtualDeviceId, report.Session.Generation, current => new VirtualOutputRuntimeSnapshot
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

    private void UpdateWorkerStatus(VirtualDeviceId deviceId, long generation, Func<VirtualOutputRuntimeSnapshot?, VirtualOutputRuntimeSnapshot> update)
    {
        _status.Update(deviceId, current =>
        {
            if (current is { Generation: { } appliedGeneration } && appliedGeneration > generation)
            {
                _logger.LogDebug("Ignored stale virtual-output status for {VirtualDeviceId} generation {Generation}; applied generation is {AppliedGeneration}",
                    deviceId, generation, appliedGeneration);
                return current;
            }
            return update(current);
        });
    }

    private static Task WriteAsync<T>(Stream stream, T response, CancellationToken cancellationToken) =>
        VirtualOutputWireProtocol.WriteAsync(stream, VirtualOutputIpcResponse.Success(response), cancellationToken);

    private static async Task TryWriteErrorAsync(Stream stream, VirtualOutputFailureCode failureCode, string failure, CancellationToken cancellationToken)
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

        /// <summary>Initializes the broker call lease.</summary>
        /// <param name="owner">The owner value.</param>
        /// <param name="virtualDeviceId">The virtual device ID value.</param>
        internal BrokerCallLease(VirtualOutputIpcServer owner, VirtualDeviceId virtualDeviceId)
        {
            _owner = owner;
            VirtualDeviceId = virtualDeviceId;
        }

        /// <summary>Gets the virtual device ID.</summary>
        internal VirtualDeviceId VirtualDeviceId { get; }

        /// <inheritdoc />
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
