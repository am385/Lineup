using System.IO.Pipes;
using System.Net.Sockets;
using Lineup.Core.VirtualOutputs.Protocol;
using Microsoft.Extensions.Logging;

namespace Lineup.Core.VirtualOutputs.Hosting;

/// <summary>Calls the trusted Lineup capability broker over operating-system local IPC.</summary>
public sealed partial class VirtualOutputBrokerClient
{
    private readonly VirtualOutputIpcEndpoint _endpoint;

    /// <summary>Initializes a local capability-broker client.</summary>
    public VirtualOutputBrokerClient(VirtualOutputIpcEndpoint endpoint)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.Address);
    }

    /// <summary>Registers one worker using its one-time bootstrap credential.</summary>
    public Task<VirtualOutputWorkerRegistrationResponse> RegisterAsync(VirtualOutputWorkerRegistrationRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<VirtualOutputWorkerRegistrationRequest, VirtualOutputWorkerRegistrationResponse>(
            VirtualOutputIpcOperation.Register,
            request,
            cancellationToken);

    /// <summary>Renews one authenticated worker lease.</summary>
    public Task<VirtualOutputLeaseRenewalResponse> RenewLeaseAsync(VirtualOutputLeaseRenewalRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<VirtualOutputLeaseRenewalRequest, VirtualOutputLeaseRenewalResponse>(
            VirtualOutputIpcOperation.RenewLease,
            request,
            cancellationToken);

    /// <summary>Gets the assigned virtual-device manifest.</summary>
    public Task<VirtualOutputManifest> GetManifestAsync(VirtualOutputManifestRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<VirtualOutputManifestRequest, VirtualOutputManifest>(
            VirtualOutputIpcOperation.GetManifest,
            request,
            cancellationToken);

    /// <summary>Gets the assigned virtual-device lineup.</summary>
    public Task<VirtualOutputLineupResponse> GetLineupAsync(VirtualOutputLineupRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<VirtualOutputLineupRequest, VirtualOutputLineupResponse>(
            VirtualOutputIpcOperation.GetLineup,
            request,
            cancellationToken);

    /// <summary>Gets the assigned virtual-device guide.</summary>
    public async Task<VirtualOutputGuideResponse> GetGuideAsync(VirtualOutputGuideRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken);
        await VirtualOutputWireProtocol.WriteAsync(connection, VirtualOutputIpcRequest.Create(VirtualOutputIpcOperation.GetGuide, request), cancellationToken);
        var response = await VirtualOutputWireProtocol.ReadGuideAsync(connection, cancellationToken);
        if (!response.Succeeded)
        {
            throw new VirtualOutputClientException(response.FailureCode ?? VirtualOutputFailureCode.Unavailable, response.Failure ?? "The guide operation failed.");
        }
        return response.Payload ?? throw new InvalidDataException("The guide response payload is empty.");
    }

    /// <summary>Reports safe worker readiness and listener health.</summary>
    public async Task ReportHealthAsync(VirtualOutputWorkerHealthReport report, CancellationToken cancellationToken = default)
    {
        _ = await SendAsync<VirtualOutputWorkerHealthReport, HealthAcknowledgement>(
            VirtualOutputIpcOperation.ReportHealth,
            report,
            cancellationToken);
    }

    /// <summary>Reports the normalized final presentation for one brokered stream.</summary>
    public Task<VirtualOutputStreamStatusResponse> ReportStreamStatusAsync(VirtualOutputStreamStatusReport report, CancellationToken cancellationToken = default) =>
        SendAsync<VirtualOutputStreamStatusReport, VirtualOutputStreamStatusResponse>(
            VirtualOutputIpcOperation.ReportStreamStatus,
            report,
            cancellationToken);

    /// <summary>Opens one assigned MPEG-TS stream and retains its IPC connection until disposal.</summary>
    public async Task<VirtualOutputClientStream> OpenStreamAsync(VirtualOutputOpenStreamRequest request, CancellationToken cancellationToken = default)
    {
        var connection = await ConnectAsync(cancellationToken);
        try
        {
            await VirtualOutputWireProtocol.WriteAsync(
                connection,
                VirtualOutputIpcRequest.Create(VirtualOutputIpcOperation.OpenStream, request),
                cancellationToken);
            var response = await VirtualOutputWireProtocol.ReadAsync<VirtualOutputIpcResponse>(connection, cancellationToken);
            ThrowIfFailed(response);
            return new VirtualOutputClientStream(this, connection, request, response.ReadPayload<VirtualOutputOpenStreamResponse>());
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Owns one connection permit until disposal and aborts its public transport if ownership is lost.</summary>
    public async Task<VirtualOutputClientConnection> AdmitConnectionAsync(VirtualOutputConnectionAdmissionRequest request, Action abortConnection, ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(abortConnection);
        ArgumentNullException.ThrowIfNull(logger);
        using var admissionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var admissionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, admissionTimeout.Token);
        Stream connection;
        try
        {
            connection = await ConnectAsync(admissionCancellation.Token);
        }
        catch (OperationCanceledException) when (admissionTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new VirtualOutputClientException(VirtualOutputFailureCode.Unavailable, "The connection admission control plane did not respond.");
        }
        try
        {
            await VirtualOutputWireProtocol.WriteAsync(
                connection, VirtualOutputIpcRequest.Create(VirtualOutputIpcOperation.AdmitConnection, request), admissionCancellation.Token);
            var response = await VirtualOutputWireProtocol.ReadAsync<VirtualOutputIpcResponse>(connection, admissionCancellation.Token);
            ThrowIfFailed(response);
            if (!response.ReadPayload<VirtualOutputConnectionAdmissionResponse>().Admitted)
            {
                throw new InvalidDataException("The broker did not acknowledge connection admission.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new VirtualOutputClientConnection(connection, abortConnection, logger);
        }
        catch (OperationCanceledException) when (admissionTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await connection.DisposeAsync();
            throw new VirtualOutputClientException(VirtualOutputFailureCode.Unavailable, "The connection admission control plane did not respond.");
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Closes stream asynchronously.</summary>
    /// <param name="request">The request value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The operation's result.</returns>
    internal Task<VirtualOutputCloseStreamResponse> CloseStreamAsync(VirtualOutputCloseStreamRequest request, CancellationToken cancellationToken) =>
        SendAsync<VirtualOutputCloseStreamRequest, VirtualOutputCloseStreamResponse>(
            VirtualOutputIpcOperation.CloseStream,
            request,
            cancellationToken);

    private async Task<TResponse> SendAsync<TRequest, TResponse>(VirtualOutputIpcOperation operation, TRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await ConnectAsync(cancellationToken);
        await VirtualOutputWireProtocol.WriteAsync(connection, VirtualOutputIpcRequest.Create(operation, request), cancellationToken);
        var response = await VirtualOutputWireProtocol.ReadAsync<VirtualOutputIpcResponse>(connection, cancellationToken);
        ThrowIfFailed(response);
        return response.ReadPayload<TResponse>();
    }

    private async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        if (_endpoint.Transport == VirtualOutputIpcTransport.NamedPipe)
        {
            var pipe = new NamedPipeClientStream(".", _endpoint.Address, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.WriteThrough);
            try
            {
                await pipe.ConnectAsync(cancellationToken);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_endpoint.Address), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static void ThrowIfFailed(VirtualOutputIpcResponse response)
    {
        if (!response.Succeeded)
        {
            throw new VirtualOutputClientException(
                response.FailureCode ?? VirtualOutputFailureCode.Unavailable,
                response.Failure ?? "The Lineup capability broker rejected the request.");
        }
    }

    private sealed record HealthAcknowledgement(bool Accepted);
}

/// <summary>Owns one brokered media stream and its local IPC connection.</summary>
public sealed class VirtualOutputClientStream : IAsyncDisposable
{
    private static readonly TimeSpan _closeTimeout = TimeSpan.FromSeconds(5);
    private readonly VirtualOutputBrokerClient _client;
    private readonly Stream _connection;
    private readonly VirtualOutputOpenStreamRequest _request;
    private int _closeStarted;
    private int _disposed;

    /// <summary>Initializes the virtual output client stream.</summary>
    /// <param name="client">The client value.</param>
    /// <param name="connection">The connection value.</param>
    /// <param name="request">The request value.</param>
    /// <param name="response">The response value.</param>
    internal VirtualOutputClientStream(VirtualOutputBrokerClient client, Stream connection, VirtualOutputOpenStreamRequest request, VirtualOutputOpenStreamResponse response)
    {
        _client = client;
        _connection = connection;
        _request = request;
        Response = response;
    }

    /// <summary>Gets accepted stream metadata.</summary>
    public VirtualOutputOpenStreamResponse Response { get; }

    /// <summary>Gets the readable MPEG-TS response body.</summary>
    public Stream Stream => _connection;

    /// <summary>Gets the correlated broker request identifier.</summary>
    public VirtualOutputStreamRequestId RequestId => _request.RequestId;

    /// <summary>Requests deterministic upstream closure before the streaming IPC connection is released.</summary>
    public async Task<bool> CloseAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _closeStarted, 1, 0) != 0)
        {
            return false;
        }
        if (Response.StreamSessionId is not { } streamSessionId)
        {
            return false;
        }
        try
        {
            var response = await _client.CloseStreamAsync(
                new VirtualOutputCloseStreamRequest
                {
                    Session = _request.Session,
                    RequestId = _request.RequestId,
                    StreamSessionId = streamSessionId
                },
                cancellationToken);
            return response.Closed;
        }
        catch
        {
            Volatile.Write(ref _closeStarted, 0);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        using var closeCancellation = new CancellationTokenSource(_closeTimeout);
        try
        {
            await CloseAsync(closeCancellation.Token);
        }
        finally
        {
            await _connection.DisposeAsync();
        }
    }
}

/// <summary>Represents a structured rejection returned by the trusted capability broker.</summary>
public sealed class VirtualOutputClientException : InvalidOperationException
{
    /// <summary>Initializes a broker-client rejection.</summary>
    public VirtualOutputClientException(VirtualOutputFailureCode failureCode, string message)
        : base(message)
    {
        FailureCode = failureCode;
    }

    /// <summary>Gets the structured failure code.</summary>
    public VirtualOutputFailureCode FailureCode { get; }
}
