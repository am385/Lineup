using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Settings;
using Lineup.Core.Streaming;
using Lineup.Core.VirtualOutputs.Hosting;
using Lineup.Core.VirtualOutputs.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Core.VirtualOutputs.Runtime.Tests;

/// <summary>Verifies real TCP lifetime accounting and authenticated worker ownership over real local IPC.</summary>
public sealed class PluginConnectionAdmissionTests
{
    /// <summary>Verifies keep-alive and concurrent HTTP/2 requests retain one permit while mixed management traffic stays usable.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedListener_UsesOnePermitPerConnection_AndExemptsManagement(bool http2)
    {
        // Arrange
        await using var fixture = await ListenerFixture.CreateAsync(http2);
        using var first = fixture.CreateClient(http2);
        using var second = fixture.CreateClient(http2);
        var managementConnection = await first.GetStringAsync("/management", TestContext.Current.CancellationToken);
        Assert.Equal(0, fixture.Budget.GetSnapshot().ActiveConnections);

        // Act
        var requests = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => first.GetStringAsync("/plugin", TestContext.Current.CancellationToken)));
        using var refused = await second.GetAsync("/plugin", TestContext.Current.CancellationToken);
        var secondManagement = await second.GetStringAsync("/management", TestContext.Current.CancellationToken);
        using var diagnostic = await second.GetAsync("/diagnostic", TestContext.Current.CancellationToken);

        // Assert
        Assert.All(requests, connection => Assert.Equal(managementConnection, connection));
        Assert.Equal(1, fixture.Budget.GetSnapshot().ActiveConnections);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, diagnostic.StatusCode);
        Assert.NotEqual(managementConnection, secondManagement);
        Assert.Equal(secondManagement, await second.GetStringAsync("/management", TestContext.Current.CancellationToken));
        first.Dispose();
        await WaitForUsageAsync(fixture.Budget, 0);
        Assert.Equal(secondManagement, await second.GetStringAsync("/plugin", TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Budget.GetSnapshot().ActiveConnections);
    }

    /// <summary>Verifies worker revocation closes the actual public TCP socket and returns ownership only through connection cleanup.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task WorkerOwnershipLoss_ClosesPublicTransport(int failure)
    {
        // Arrange
        var clock = new AdmissionClock();
        await using var control = await ControlFixture.CreateAsync(timeProvider: clock);
        var admission = new PluginConnectionAdmission(control.Worker);
        await using var listener = await ListenerFixture.CreateAsync(false, control.Budget, admission);
        using var socket = new TcpClient();
        await socket.ConnectAsync(listener.Address.Host, listener.Address.Port, TestContext.Current.CancellationToken);
        await using var transport = socket.GetStream();
        await transport.WriteAsync(Encoding.ASCII.GetBytes("GET /plugin HTTP/1.1\r\nHost: localhost\r\n\r\n"), TestContext.Current.CancellationToken);
        using var reader = new StreamReader(transport, Encoding.ASCII, leaveOpen: true);
        Assert.Contains("200", await reader.ReadLineAsync(TestContext.Current.CancellationToken));
        var length = 0;
        while (await reader.ReadLineAsync(TestContext.Current.CancellationToken) is { Length: > 0 } header)
        {
            if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                length = int.Parse(header["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        Assert.True(length > 0);
        Assert.Equal(length, await reader.ReadBlockAsync(new char[length], TestContext.Current.CancellationToken));
        Assert.Equal(1, control.Budget.GetSnapshot().ActiveConnections);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));

        // Act
        if (failure == 1)
        {
            await control.Server.StopAsync(deadline.Token);
        }
        else if (failure == 0)
        {
            control.Authority.Revoke(control.Device.Id, "Test revocation");
        }
        else
        {
            clock.Advance(TimeSpan.FromMinutes(2));
        }
        var observed = await Record.ExceptionAsync(async () => Assert.Equal(0, await reader.ReadAsync(new char[1], deadline.Token)));

        // Assert
        Assert.True(observed is null or IOException, observed?.ToString());
        await WaitForUsageAsync(control.Budget, 0);
    }

    /// <summary>Verifies revocation does not reclaim a noncooperative owner's slot before its channel closes.</summary>
    [Fact]
    public async Task Revoke_NoncooperativeOwner_RetainsPermitUntilDisconnect()
    {
        // Arrange
        await using var control = await ControlFixture.CreateAsync();
        await using var ownership = await control.ConnectAsync();
        await VirtualOutputWireProtocol.WriteAsync(ownership,
            VirtualOutputIpcRequest.Create(VirtualOutputIpcOperation.AdmitConnection, control.Request("retained")), TestContext.Current.CancellationToken);
        var accepted = await VirtualOutputWireProtocol.ReadAsync<VirtualOutputIpcResponse>(ownership, TestContext.Current.CancellationToken);
        Assert.True(accepted.Succeeded);

        // Act
        control.Authority.Revoke(control.Device.Id, "Test revocation");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var signal = new byte[1];
        var count = await ownership.ReadAsync(signal, deadline.Token);

        // Assert
        Assert.Equal(1, count);
        Assert.Equal(1, signal[0]);
        Assert.Equal(1, control.Budget.GetSnapshot().ActiveConnections);
        Assert.Null(control.Budget.TryAcquire());
        await ownership.DisposeAsync();
        await WaitForUsageAsync(control.Budget, 0);
    }

    /// <summary>Verifies duplicate identity rejection, shared management capacity and recovery after worker disconnect.</summary>
    [Fact]
    public async Task WorkerAdmission_UsesCentralBudget_AndRejectsDuplicateOwnership()
    {
        // Arrange
        await using var control = await ControlFixture.CreateAsync();
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var lease = await control.Worker.Broker.AdmitConnectionAsync(
            control.Request("same"), () => aborted.TrySetResult(), NullLogger.Instance, TestContext.Current.CancellationToken);

        // Act
        var duplicate = await Assert.ThrowsAsync<VirtualOutputClientException>(() => control.Worker.Broker.AdmitConnectionAsync(
            control.Request("same"), () => { }, NullLogger.Instance, TestContext.Current.CancellationToken));
        var exhausted = await Assert.ThrowsAsync<VirtualOutputClientException>(() => control.Worker.Broker.AdmitConnectionAsync(
            control.Request("other"), () => { }, NullLogger.Instance, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(VirtualOutputFailureCode.InvalidRequest, duplicate.FailureCode);
        Assert.Equal(VirtualOutputFailureCode.ResourceLimit, exhausted.FailureCode);
        Assert.Null(control.Budget.TryAcquire());
        Assert.Equal(1, control.Budget.GetSnapshot().WorkerConnections[control.Device.Id]);
        Assert.False(aborted.Task.IsCompleted);
        await lease.DisposeAsync();
        await WaitForUsageAsync(control.Budget, 0);
        await using var management = control.Budget.TryAcquire();
        Assert.NotNull(management);
    }

    /// <summary>Verifies connection ownership never consumes the worker's outstanding broker-call quota.</summary>
    [Fact]
    public async Task WorkerAdmission_DoesNotReserveOutstandingBrokerCalls()
    {
        // Arrange
        var broker = Substitute.For<IVirtualOutputBroker>();
        await using var control = await ControlFixture.CreateAsync(maximum: 2, broker: broker);
        broker.GetManifestAsync(Arg.Any<VirtualOutputManifestRequest>(), Arg.Any<CancellationToken>()).Returns(
            new VirtualOutputManifest { VirtualDeviceId = control.Device.Id, Name = "Available" });
        await using var first = await control.Worker.Broker.AdmitConnectionAsync(
            control.Request("first"), () => { }, NullLogger.Instance, TestContext.Current.CancellationToken);
        await using var second = await control.Worker.Broker.AdmitConnectionAsync(
            control.Request("second"), () => { }, NullLogger.Instance, TestContext.Current.CancellationToken);

        // Act
        var manifest = await control.Worker.Broker.GetManifestAsync(new VirtualOutputManifestRequest { Session = control.Worker.Context! }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("Available", manifest.Name);
        Assert.Equal(2, control.Budget.GetSnapshot().ActiveConnections);
    }

    /// <summary>Verifies separate authenticated workers share one deployment cap rather than independent local limits.</summary>
    [Fact]
    public async Task WorkerAdmission_SeparateWorkers_ShareTheGlobalLimit()
    {
        // Arrange
        await using var control = await ControlFixture.CreateAsync();
        var other = await control.RegisterWorkerAsync(control.Device with { Id = VirtualDeviceId.New() });
        await using var first = await control.Worker.Broker.AdmitConnectionAsync(
            control.Request("first"), () => { }, NullLogger.Instance, TestContext.Current.CancellationToken);

        // Act
        var rejected = await Assert.ThrowsAsync<VirtualOutputClientException>(() => other.Broker.AdmitConnectionAsync(
            new VirtualOutputConnectionAdmissionRequest { Session = other.Context!, ConnectionId = "second" },
            () => { }, NullLogger.Instance, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(VirtualOutputFailureCode.ResourceLimit, rejected.FailureCode);
        Assert.Equal(1, control.Budget.GetSnapshot().ActiveConnections);
        Assert.Single(control.Budget.GetSnapshot().WorkerConnections);
    }

    /// <summary>Verifies an older generation's cleanup cannot release a replacement's identically named connection.</summary>
    [Fact]
    public async Task WorkerAdmission_ReplacementGeneration_IsNotReleasedByOldCleanup()
    {
        // Arrange
        await using var control = await ControlFixture.CreateAsync();
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var old = await control.Worker.Broker.AdmitConnectionAsync(
            control.Request("reused"), () => aborted.TrySetResult(), NullLogger.Instance, TestContext.Current.CancellationToken);
        var replacement = await control.RegisterWorkerAsync(control.Device);
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, control.Budget.GetSnapshot().ActiveConnections);
        await old.DisposeAsync();
        await WaitForUsageAsync(control.Budget, 0);
        await using var current = await replacement.Broker.AdmitConnectionAsync(
            new VirtualOutputConnectionAdmissionRequest { Session = replacement.Context!, ConnectionId = "reused" },
            () => { }, NullLogger.Instance, TestContext.Current.CancellationToken);

        // Act
        await old.DisposeAsync();
        var stale = await Assert.ThrowsAsync<VirtualOutputClientException>(() => control.Worker.Broker.AdmitConnectionAsync(
            control.Request("stale"), () => { }, NullLogger.Instance, TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains(stale.FailureCode, new[] { VirtualOutputFailureCode.StaleGeneration, VirtualOutputFailureCode.Unauthorized });
        Assert.Equal(1, control.Budget.GetSnapshot().ActiveConnections);
    }

    /// <summary>Verifies public disconnect cancels pending admission and disposes a grant returned too late.</summary>
    [Fact]
    public async Task Admission_LateGrantAfterPublicDisconnect_IsReleased()
    {
        // Arrange
        await using var control = await ControlFixture.CreateAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var granted = new TaskCompletionSource<IAsyncDisposable?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = new PluginConnectionAdmission(async (_, token) =>
        {
            using var registration = token.Register(() => disconnected.TrySetResult());
            started.TrySetResult();
            return await granted.Task;
        });
        await using var listener = await ListenerFixture.CreateAsync(false, control.Budget, admission);
        using var socket = new TcpClient();
        await socket.ConnectAsync(listener.Address.Host, listener.Address.Port, TestContext.Current.CancellationToken);
        await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /plugin HTTP/1.1\r\nHost: localhost\r\n\r\n"), TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Act
        socket.Dispose();
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        granted.SetResult(control.Budget.TryAcquire());

        // Assert
        await WaitForUsageAsync(control.Budget, 0);
        Assert.Equal(0, admission.ActiveConnections);
    }

    /// <summary>Verifies unavailable admission is HTTP 503 rather than capacity exhaustion and never blocks management.</summary>
    [Fact]
    public async Task Admission_ControlPlaneFailure_ReturnsUnavailableAndKeepsManagement()
    {
        // Arrange
        var admission = new PluginConnectionAdmission((_, _) => throw new IOException("Control plane unavailable."));
        await using var listener = await ListenerFixture.CreateAsync(true, admission: admission);
        using var client = listener.CreateClient(true);
        var connection = await client.GetStringAsync("/management", TestContext.Current.CancellationToken);

        // Act
        using var unavailable = await client.GetAsync("/plugin", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Contains("broker_unavailable", await unavailable.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(connection, await client.GetStringAsync("/management", TestContext.Current.CancellationToken));
        Assert.Equal(0, listener.Budget.GetSnapshot().ActiveConnections);
    }

    /// <summary>Verifies overlapping HTTP/2 refusals share one attempt without preventing a later retry on the same connection.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admission_ConcurrentHttp2Refusals_ShareInFlightAttempt(bool unavailable)
    {
        // Arrange
        var attempts = 0;
        var arrivals = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = new PluginConnectionAdmission(async (_, token) =>
        {
            Interlocked.Increment(ref attempts);
            await release.Task.WaitAsync(token);
            if (unavailable)
            {
                throw new IOException("Control plane unavailable.");
            }
            return null;
        });
        await using var listener = await ListenerFixture.CreateAsync(true, admission: admission, onRequestWaiting: () =>
        {
            if (Interlocked.Increment(ref arrivals) == 12)
            {
                allWaiting.TrySetResult();
            }
        });
        using var client = listener.CreateClient(true);
        var requests = Enumerable.Range(0, 12).Select(_ => client.GetAsync("/plugin", TestContext.Current.CancellationToken)).ToArray();

        // Act
        await allWaiting.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var concurrentAttempts = Volatile.Read(ref attempts);
        release.SetResult();
        var responses = await Task.WhenAll(requests);

        // Assert
        Assert.Equal(1, concurrentAttempts);
        Assert.Equal(1, Volatile.Read(ref attempts));
        var expected = unavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.TooManyRequests;
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(expected, response.StatusCode);
            }
        }
        using var retry = await client.GetAsync("/plugin", TestContext.Current.CancellationToken);
        Assert.Equal(expected, retry.StatusCode);
        Assert.Equal(2, Volatile.Read(ref attempts));
        using var management = await client.GetAsync("/management", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, management.StatusCode);
        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.Equal(0, admission.ActiveConnections);
    }

    private static async Task WaitForUsageAsync(IPluginConnectionBudget budget, int expected)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (budget.GetSnapshot().ActiveConnections != expected)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
        }
    }

    private sealed class ListenerFixture(WebApplication app, Uri address, IPluginConnectionBudget budget) : IAsyncDisposable
    {
        /// <summary>Gets the real dynamically bound loopback endpoint.</summary>
        internal Uri Address { get; } = address;

        /// <summary>Gets the authoritative budget shared with the control plane.</summary>
        internal IPluginConnectionBudget Budget { get; } = budget;

        /// <summary>Starts a real Kestrel mixed-role listener without tuner or browser dependencies.</summary>
        internal static async Task<ListenerFixture> CreateAsync(bool http2, IPluginConnectionBudget? budget = null, PluginConnectionAdmission? admission = null, Action? onRequestWaiting = null)
        {
            if (budget is null)
            {
                var settings = Substitute.For<ILineupSettingsService>();
                settings.Settings.Returns(new LineupSettings { Streaming = new StreamingConfiguration { MaximumPluginConnections = 1 } });
                budget = new PluginConnectionBudget(settings);
            }
            admission ??= new PluginConnectionAdmission((_, _) => ValueTask.FromResult(budget.TryAcquire()));
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(admission.Configure);
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
                listen => listen.Protocols = http2 ? HttpProtocols.Http2 : HttpProtocols.Http1));
            var app = builder.Build();
            app.UseRouting();
            if (onRequestWaiting is not null)
            {
                app.Use(async (context, next) =>
                {
                    var pending = next(context);
                    onRequestWaiting();
                    await pending;
                });
            }
            admission.Use(app, NullLogger.Instance);
            app.MapGet("/plugin", (HttpContext context) => Results.Text(context.Connection.Id)).WithMetadata(new PluginConnectionEndpointAttribute());
            app.MapGet("/management", (HttpContext context) => Results.Text(context.Connection.Id));
            app.MapGet("/diagnostic", () => Results.Ok()).WithMetadata(new PluginConnectionEndpointAttribute(false));
            await app.StartAsync(TestContext.Current.CancellationToken);
            var address = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));
            return new ListenerFixture(app, address, budget);
        }

        /// <summary>Creates an isolated transport pool constrained to one exact HTTP protocol and connection.</summary>
        internal HttpClient CreateClient(bool http2) => new(new SocketsHttpHandler { MaxConnectionsPerServer = 1, UseProxy = false })
        {
            BaseAddress = Address,
            DefaultRequestVersion = http2 ? HttpVersion.Version20 : HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    private sealed class AdmissionClock : TimeProvider
    {
        private long _ticks = DateTimeOffset.UtcNow.UtcTicks;

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        /// <summary>Expires authority leases without sleeping through the configured lease period.</summary>
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }

    private sealed class ControlFixture(TimeProvider? timeProvider) : IAsyncDisposable
    {
        /// <summary>Gets the real worker session authority.</summary>
        internal VirtualOutputSessionAuthority Authority { get; } = new(timeProvider);

        /// <summary>Gets the assigned worker device.</summary>
        internal VirtualDeviceDefinition Device { get; } = new()
        {
            Id = VirtualDeviceId.New(),
            Name = "Connection admission test",
            Adapter = new AdapterConfiguration("test", JsonSerializer.SerializeToElement(new { })),
            Hosting = new VirtualOutputHostingDefinition { WorkerPolicyKey = "test" }
        };

        /// <summary>Gets the OS-local ownership channel endpoint.</summary>
        internal VirtualOutputIpcEndpoint Endpoint { get; } = OperatingSystem.IsWindows()
            ? new() { Transport = VirtualOutputIpcTransport.NamedPipe, Address = $"lineup-admission-{Guid.NewGuid():N}" }
            : new() { Transport = VirtualOutputIpcTransport.UnixDomainSocket, Address = Path.Combine(Path.GetTempPath(), $"lineup-admission-{Guid.NewGuid():N}.sock") };

        /// <summary>Gets the single deployment connection budget.</summary>
        internal IPluginConnectionBudget Budget { get; private set; } = null!;

        /// <summary>Gets the supervised IPC server.</summary>
        internal VirtualOutputIpcServer Server { get; private set; } = null!;

        /// <summary>Gets the registered worker.</summary>
        internal VirtualOutputWorkerSession Worker { get; private set; } = null!;

        /// <summary>Starts a real authenticated local IPC authority and worker.</summary>
        internal static async Task<ControlFixture> CreateAsync(int maximum = 1, TimeProvider? timeProvider = null, IVirtualOutputBroker? broker = null)
        {
            var fixture = new ControlFixture(timeProvider);
            var settings = Substitute.For<ILineupSettingsService>();
            settings.Settings.Returns(new LineupSettings
            {
                Streaming = new StreamingConfiguration
                {
                    MaximumPluginConnections = maximum,
                    VirtualDevices = [fixture.Device],
                    VirtualOutputWorkerPolicies = [new VirtualOutputWorkerPolicyDefinition { Key = "test", Name = "Test", MaximumOutstandingBrokerCalls = 1 }]
                }
            });
            fixture.Budget = new PluginConnectionBudget(settings);
            fixture.Server = new VirtualOutputIpcServer(fixture.Endpoint, fixture.Authority, broker ?? Substitute.For<IVirtualOutputBroker>(),
                new VirtualOutputRuntimeStatus(), settings, new ActiveStreamRegistry(), NullLogger<VirtualOutputIpcServer>.Instance, fixture.Budget);
            await fixture.Server.StartAsync(TestContext.Current.CancellationToken);
            fixture.Worker = await fixture.RegisterWorkerAsync(fixture.Device);
            return fixture;
        }

        /// <summary>Registers a second worker or replaces the current activation with a fresh generation.</summary>
        internal async Task<VirtualOutputWorkerSession> RegisterWorkerAsync(VirtualDeviceDefinition device)
        {
            var ticket = Authority.CreateActivation(device.Id, "test",
                new HashSet<VirtualOutputCapability> { VirtualOutputCapability.AdmitConnection, VirtualOutputCapability.ReadManifest }, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            var worker = new VirtualOutputWorkerSession(new VirtualOutputWorkerBootstrap
            {
                Endpoint = Endpoint,
                VirtualDevice = device,
                WorkerPolicy = new VirtualOutputWorkerPolicyDefinition { Key = "test", Name = "Test" },
                WorkerId = ticket.WorkerId,
                Generation = ticket.Generation,
                BootstrapToken = ticket.BootstrapToken,
                BootstrapExpiresUtc = ticket.ExpiresUtc
            }, timeProvider);
            await worker.RegisterAsync(TestContext.Current.CancellationToken);
            return worker;
        }

        /// <summary>Creates one generation-bound connection ownership request.</summary>
        internal VirtualOutputConnectionAdmissionRequest Request(string id) => new() { Session = Worker.Context!, ConnectionId = id };

        /// <summary>Opens a raw ownership transport on either supported IPC platform.</summary>
        internal async Task<Stream> ConnectAsync()
        {
            if (Endpoint.Transport == VirtualOutputIpcTransport.NamedPipe)
            {
                var pipe = new System.IO.Pipes.NamedPipeClientStream(".", Endpoint.Address,
                    System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(TestContext.Current.CancellationToken);
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
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(Endpoint.Address), TestContext.Current.CancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await Server.StopAsync(TestContext.Current.CancellationToken);
            Server.Dispose();
            Authority.Dispose();
        }
    }
}
