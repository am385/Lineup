using System.Net.Sockets;
using System.Text.Json;
using Lineup.Core.VirtualOutputs.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Lineup.Core.VirtualOutputs.Hosting;

/// <summary>Marks an endpoint's participation in the plugin TCP connection budget.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class PluginConnectionEndpointAttribute(bool counts = true) : Attribute
{
    /// <summary>Gets whether this endpoint requires plugin connection admission.</summary>
    public bool Counts { get; } = counts;
}

/// <summary>Shares one transport-owned admission across keep-alive and concurrent HTTP/2 requests.</summary>
public sealed class PluginConnectionAdmission
{
    private readonly Func<ConnectionContext, CancellationToken, ValueTask<IAsyncDisposable?>> _acquire;
    private ILogger? _logger;
    private int _activeConnections;

    /// <summary>Gets admitted public TCP connections, independent of active HTTP requests or media sessions.</summary>
    public int ActiveConnections => Volatile.Read(ref _activeConnections);

    /// <summary>Initializes the trusted local or authenticated broker admission boundary.</summary>
    public PluginConnectionAdmission(Func<ConnectionContext, CancellationToken, ValueTask<IAsyncDisposable?>> acquire)
    {
        _acquire = acquire ?? throw new ArgumentNullException(nameof(acquire));
    }

    /// <summary>Initializes a worker's authenticated connection ownership and public transport abort boundary.</summary>
    public PluginConnectionAdmission(VirtualOutputWorkerSession session)
    {
        _acquire = async (connection, token) => await session.Broker.AdmitConnectionAsync(
            new VirtualOutputConnectionAdmissionRequest
            {
                Session = session.Context ?? throw new InvalidOperationException("The worker must register before admitting plugin connections."),
                ConnectionId = connection.ConnectionId
            },
            () => connection.Abort(new ConnectionAbortedException("Plugin connection admission ownership was revoked.")),
            _logger ?? throw new InvalidOperationException("Connection admission middleware has not been configured."),
            token);
    }

    /// <summary>Tracks connection ownership without charging idle sockets or TLS handshakes.</summary>
    public void Configure(KestrelServerOptions options)
    {
        options.ConfigureEndpointDefaults(listen => listen.Use(next => async connection =>
        {
            await using var state = new ConnectionAdmissionState(connection, this);
            connection.Features.Set(state);
            await next(connection);
        }));
    }

    /// <summary>Admits only eligible routed requests; management and explicit control endpoints remain accessible.</summary>
    public void Use(IApplicationBuilder app, ILogger logger, bool countUnmarkedEndpoints = false, Func<HttpContext, VirtualOutputFailureCode, string, Task>? writeFailure = null)
    {
        _logger = logger;
        app.Use(async (context, next) =>
        {
            var role = context.GetEndpoint()?.Metadata.GetMetadata<PluginConnectionEndpointAttribute>();
            if (!(role?.Counts ?? countUnmarkedEndpoints))
            {
                await next(context);
                return;
            }
            var state = context.Features.Get<ConnectionAdmissionState>();
            if (state is null)
            {
                logger.LogError("Plugin connection admission is missing its Kestrel transport feature.");
                await RejectAsync(context, VirtualOutputFailureCode.Unavailable, "Plugin connection admission is unavailable.", writeFailure);
                return;
            }
            bool admitted;
            try
            {
                admitted = await state.AdmitAsync();
            }
            catch (VirtualOutputClientException exception)
            {
                logger.LogWarning("Plugin connection admission was rejected: {FailureCode}", exception.FailureCode);
                await RejectAsync(context, exception.FailureCode, exception.Message, writeFailure);
                return;
            }
            catch (Exception exception) when (exception is IOException or SocketException or InvalidDataException or JsonException)
            {
                logger.LogWarning(exception, "Plugin connection admission could not reach its control plane.");
                await RejectAsync(context, VirtualOutputFailureCode.Unavailable, "Plugin connection admission could not reach its control plane.", writeFailure);
                return;
            }
            if (!admitted)
            {
                await RejectAsync(context, VirtualOutputFailureCode.ResourceLimit, "The deployment-wide plugin connection limit has been reached.", writeFailure);
                return;
            }
            await next(context);
        });
    }

    private static Task RejectAsync(HttpContext context, VirtualOutputFailureCode failure, string message, Func<HttpContext, VirtualOutputFailureCode, string, Task>? writeFailure)
    {
        context.Response.StatusCode = failure == VirtualOutputFailureCode.ResourceLimit ? StatusCodes.Status429TooManyRequests : StatusCodes.Status503ServiceUnavailable;
        return writeFailure is not null ? writeFailure(context, failure, message) : context.Response.WriteAsJsonAsync(new
        {
            code = failure == VirtualOutputFailureCode.ResourceLimit ? "resource_limit" : "broker_unavailable",
            error = message
        }, context.RequestAborted);
    }

    private sealed class ConnectionAdmissionState(ConnectionContext connection, PluginConnectionAdmission owner) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Lock _attemptGate = new();
        private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed);
        private IAsyncDisposable? _permit;
        private Task<bool>? _pendingAdmission;

        /// <summary>Shares an in-flight admission attempt across overlapping requests using transport-lifetime cancellation.</summary>
        internal Task<bool> AdmitAsync()
        {
            lock (_attemptGate)
            {
                if (_pendingAdmission is not { IsCompleted: false })
                {
                    _pendingAdmission = AdmitCoreAsync();
                }
                return _pendingAdmission;
            }
        }

        private async Task<bool> AdmitCoreAsync()
        {
            await _gate.WaitAsync(_lifetime.Token);
            try
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                if (_permit is null)
                {
                    _permit = await owner._acquire(connection, _lifetime.Token);
                    if (_permit is not null)
                    {
                        Interlocked.Increment(ref owner._activeConnections);
                    }
                }
                if (_lifetime.IsCancellationRequested)
                {
                    await ReleasePermitAsync();
                    _lifetime.Token.ThrowIfCancellationRequested();
                }
                return _permit is not null;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            await _gate.WaitAsync();
            try
            {
                await ReleasePermitAsync();
            }
            finally
            {
                _gate.Release();
                _gate.Dispose();
                _lifetime.Dispose();
            }
        }

        private async ValueTask ReleasePermitAsync()
        {
            if (_permit is not null)
            {
                await _permit.DisposeAsync();
                _permit = null;
                Interlocked.Decrement(ref owner._activeConnections);
            }
        }
    }
}
