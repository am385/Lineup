using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Lineup.Core.Common.Guides;
using Lineup.Plugin.LineupLink.Physical;
using Lineup.Plugin.LineupLink.Protocol;
using Xunit;

namespace Lineup.Plugin.LineupLink.Tests;

/// <summary>Verifies bounded Lineup Link control requests without imposing a lifetime on live media.</summary>
public sealed class LineupLinkControlRequestTests
{
    private static readonly TimeSpan _watchdog = TimeSpan.FromSeconds(3);

    /// <summary>Both declared and chunked over-limit guides fail as whole documents and do not poison a later refresh.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetGuideAsync_OverEnvelope_RejectsAndRecovers(bool declaredBytes)
    {
        // Arrange
        var id = Guid.NewGuid();
        var reject = true;
        var releaseHeaders = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new LineupLinkGuideChannel { Key = "1", Number = "1", Name = "Example" };
        await using var server = new LoopbackServer(async (context, cancellationToken) =>
        {
            if (reject && declaredBytes)
            {
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = GuideTransfer.MaximumBytes + 1L;
                await context.Response.OutputStream.WriteAsync(new byte[] { (byte)' ' }, cancellationToken);
                await context.Response.OutputStream.FlushAsync(cancellationToken);
                try { await releaseHeaders.Task.WaitAsync(cancellationToken); }
                finally { context.Response.Abort(); }
                return;
            }
            var payload = JsonSerializer.SerializeToUtf8Bytes(new LineupLinkGuide
            {
                VirtualDeviceId = id,
                Channels = reject ? Enumerable.Repeat(channel, GuideTransfer.MaximumChannels + 1).ToArray() : [channel]
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            context.Response.ContentType = "application/json";
            context.Response.SendChunked = true;
            await context.Response.OutputStream.WriteAsync(payload, cancellationToken);
        });
        using var client = new LineupLinkClient(CreateConfiguration(server.BaseUri) with { VirtualDeviceId = id });

        // Act
        try
        {
            await Assert.ThrowsAsync<GuideTransferLimitException>(() => client.GetGuideAsync(TestContext.Current.CancellationToken).WaitAsync(_watchdog, TestContext.Current.CancellationToken));
        }
        finally
        {
            releaseHeaders.TrySetResult();
        }
        reject = false;
        var recovered = await client.GetGuideAsync(TestContext.Current.CancellationToken).WaitAsync(_watchdog, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(id, recovered.VirtualDeviceId);
        Assert.Equal("1", Assert.Single(recovered.Channels).Key);
    }

    /// <summary>Verifies invalid remote control payloads are recoverable failures and do not poison subsequent requests.</summary>
    [Theory]
    [InlineData("manifest", false)]
    [InlineData("lineup", false)]
    [InlineData("guide", false)]
    [InlineData("manifest", true)]
    [InlineData("lineup", true)]
    [InlineData("guide", true)]
    public async Task ControlRequest_InvalidPayload_ThrowsRecoverableFailure(string endpoint, bool unsupportedProtocol)
    {
        // Arrange
        var virtualDeviceId = Guid.NewGuid();
        var valid = false;
        await using var server = new LoopbackServer((context, cancellationToken) => WriteJsonAsync(context.Response,
            valid || unsupportedProtocol
                ? JsonSerializer.Serialize(new
                {
                    VirtualDeviceId = virtualDeviceId,
                    InstanceId = Guid.NewGuid(),
                    Name = "Remote",
                    ProtocolVersion = valid ? LineupLinkProtocol.Version : 999
                })
                : "<html>temporarily unavailable</html>", cancellationToken));
        var clock = new ManualTimeProvider();
        using var client = new LineupLinkClient(CreateConfiguration(server.BaseUri) with { VirtualDeviceId = virtualDeviceId }, clock);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            SendControlAsync(client, endpoint, TestContext.Current.CancellationToken).WaitAsync(_watchdog, TestContext.Current.CancellationToken));
        valid = true;
        await SendControlAsync(client, endpoint, TestContext.Current.CancellationToken).WaitAsync(_watchdog, TestContext.Current.CancellationToken);

        // Assert
        if (unsupportedProtocol)
        {
            Assert.Contains("999", exception.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.IsType<JsonException>(exception.InnerException);
            Assert.DoesNotContain("<html>", exception.Message, StringComparison.Ordinal);
        }
        Assert.Equal(0, clock.ActiveTimerCount);
    }

    /// <summary>Verifies one deadline bounds headers and successful or error bodies across all control endpoints.</summary>
    [Theory]
    [InlineData("manifest", "headers")]
    [InlineData("lineup", "headers")]
    [InlineData("guide", "headers")]
    [InlineData("manifest", "body")]
    [InlineData("lineup", "body")]
    [InlineData("guide", "body")]
    [InlineData("manifest", "error-body")]
    [InlineData("lineup", "error-body")]
    [InlineData("guide", "error-body")]
    public async Task ControlRequest_StalledResponse_ThrowsRecoverableTimeout(string endpoint, string stalledPhase)
    {
        // Arrange
        var clock = new ManualTimeProvider();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHeaders = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LoopbackServer(async (context, cancellationToken) =>
        {
            received.SetResult();
            await releaseHeaders.Task.WaitAsync(cancellationToken);
            context.Response.StatusCode = stalledPhase == "error-body" ? 503 : 200;
            context.Response.ContentType = "application/json";
            context.Response.SendChunked = true;
            await context.Response.OutputStream.WriteAsync("{"u8.ToArray(), cancellationToken);
            await context.Response.OutputStream.FlushAsync(cancellationToken);
            bodyStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        using var client = new LineupLinkClient(CreateConfiguration(server.BaseUri), clock);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act
        var operation = SendControlAsync(client, endpoint, caller.Token);
        try
        {
            await received.Task.WaitAsync(_watchdog, TestContext.Current.CancellationToken);
            if (stalledPhase != "headers")
            {
                clock.Advance(TimeSpan.FromSeconds(20));
                releaseHeaders.SetResult();
                await bodyStarted.Task.WaitAsync(_watchdog, TestContext.Current.CancellationToken);
                clock.Advance(TimeSpan.FromSeconds(9));
            }
            else
            {
                clock.Advance(TimeSpan.FromSeconds(29));
            }
            Assert.False(operation.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(1));
            var exception = await Assert.ThrowsAsync<HttpRequestException>(() => operation.WaitAsync(_watchdog, TestContext.Current.CancellationToken));

            // Assert
            Assert.Contains("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
            Assert.False(caller.IsCancellationRequested);
        }
        finally
        {
            await caller.CancelAsync();
        }
    }

    /// <summary>Verifies caller cancellation is not relabeled as a remote timeout.</summary>
    [Theory]
    [InlineData("manifest", false)]
    [InlineData("lineup", false)]
    [InlineData("guide", false)]
    [InlineData("manifest", true)]
    [InlineData("lineup", true)]
    [InlineData("guide", true)]
    public async Task ControlRequest_CallerCancellation_PreservesCancellation(string endpoint, bool waitForBody)
    {
        // Arrange
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LoopbackServer(async (context, cancellationToken) =>
        {
            if (waitForBody)
            {
                context.Response.ContentType = "application/json";
                context.Response.SendChunked = true;
                await context.Response.OutputStream.WriteAsync("{"u8.ToArray(), cancellationToken);
                await context.Response.OutputStream.FlushAsync(cancellationToken);
            }
            received.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        var clock = new ManualTimeProvider();
        using var client = new LineupLinkClient(CreateConfiguration(server.BaseUri), clock);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var operation = SendControlAsync(client, endpoint, caller.Token);
        await received.Task.WaitAsync(_watchdog, TestContext.Current.CancellationToken);

        // Act
        await caller.CancelAsync();
        clock.Advance(TimeSpan.FromSeconds(30));
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(_watchdog, TestContext.Current.CancellationToken));

        // Assert
        Assert.True(caller.IsCancellationRequested);
        Assert.Equal(caller.Token, exception.CancellationToken);
    }

    /// <summary>Verifies healthy control responses preserve their payload and authentication semantics.</summary>
    [Theory]
    [InlineData("manifest")]
    [InlineData("lineup")]
    [InlineData("guide")]
    public async Task ControlRequest_HealthyResponse_Completes(string endpoint)
    {
        // Arrange
        var virtualDeviceId = Guid.NewGuid();
        await using var server = new LoopbackServer(async (context, cancellationToken) =>
        {
            Assert.EndsWith($"/{endpoint}", context.Request.Url!.AbsolutePath, StringComparison.Ordinal);
            Assert.Equal("Bearer test-token", context.Request.Headers["Authorization"]);
            await WriteJsonAsync(context.Response, endpoint == "manifest"
                ? JsonSerializer.Serialize(new LineupLinkManifest { InstanceId = Guid.NewGuid(), VirtualDeviceId = virtualDeviceId, Name = "Remote" })
                : JsonSerializer.Serialize(new { VirtualDeviceId = virtualDeviceId, ProtocolVersion = LineupLinkProtocol.Version }), cancellationToken);
        });
        var clock = new ManualTimeProvider();
        using var client = new LineupLinkClient(CreateConfiguration(server.BaseUri) with { VirtualDeviceId = virtualDeviceId }, clock);

        // Act
        await SendControlAsync(client, endpoint, TestContext.Current.CancellationToken).WaitAsync(_watchdog, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(1));
        await SendControlAsync(client, endpoint, TestContext.Current.CancellationToken).WaitAsync(_watchdog, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(server.BaseUri, client.BaseUri);
        Assert.Equal(0, clock.ActiveTimerCount);
    }

    /// <summary>Verifies MPEG-TS reads continue past the control-request deadline and still honor caller cancellation.</summary>
    [Fact]
    public async Task OpenStreamAsync_LiveBody_DoesNotUseControlDeadline()
    {
        // Arrange
        var continueStream = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPacket = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LoopbackServer(async (context, cancellationToken) =>
        {
            context.Response.ContentType = LineupLinkProtocol.MpegTsMediaType;
            context.Response.SendChunked = true;
            await context.Response.OutputStream.WriteAsync(new byte[] { 0x47 }, cancellationToken);
            await context.Response.OutputStream.FlushAsync(cancellationToken);
            await continueStream.Task.WaitAsync(cancellationToken);
            await context.Response.OutputStream.WriteAsync(new byte[] { 0x48 }, cancellationToken);
            await context.Response.OutputStream.FlushAsync(cancellationToken);
            secondPacket.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        var clock = new ManualTimeProvider();
        using var client = new LineupLinkClient(CreateConfiguration(server.BaseUri), clock);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var response = await client.OpenStreamAsync("7.1", caller.Token).WaitAsync(_watchdog, TestContext.Current.CancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(caller.Token);
        var buffer = new byte[1];
        await stream.ReadExactlyAsync(buffer, caller.Token);
        Assert.Equal(0x47, buffer[0]);

        // Act
        clock.Advance(TimeSpan.FromMinutes(5));
        continueStream.SetResult();
        await secondPacket.Task.WaitAsync(_watchdog, TestContext.Current.CancellationToken);
        await stream.ReadExactlyAsync(buffer, caller.Token);
        var pendingRead = stream.ReadAsync(new byte[1], caller.Token).AsTask();
        await caller.CancelAsync();

        // Assert
        Assert.Equal(0x48, buffer[0]);
        Assert.Equal(0, clock.ActiveTimerCount);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingRead.WaitAsync(_watchdog, TestContext.Current.CancellationToken));
    }

    private static async Task SendControlAsync(LineupLinkClient client, string endpoint, CancellationToken cancellationToken)
    {
        switch (endpoint)
        {
            case "manifest":
                await client.GetManifestAsync(cancellationToken);
                break;
            case "lineup":
                await client.GetLineupAsync(cancellationToken);
                break;
            case "guide":
                await client.GetGuideAsync(cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(endpoint));
        }
    }

    private static LineupLinkPhysicalConfiguration CreateConfiguration(Uri baseUri) => new()
    {
        BaseUrl = baseUri.ToString(),
        VirtualDeviceId = Guid.NewGuid(),
        AccessToken = "test-token",
        TransportSecurity = LineupLinkTransportSecurity.Http
    };

    private static async Task WriteJsonAsync(HttpListenerResponse response, string payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
    }

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _run;

        /// <summary>Initializes the loopback server.</summary>
        /// <param name="respond">The respond value.</param>
        public LoopbackServer(Func<HttpListenerContext, CancellationToken, Task> respond)
        {
            var portReservation = new TcpListener(IPAddress.Loopback, 0);
            portReservation.Start();
            var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
            portReservation.Stop();
            BaseUri = new Uri($"http://127.0.0.1:{port}/");
            _listener.Prefixes.Add(BaseUri.ToString());
            _listener.Start();
            _run = RunAsync(respond);
        }

        /// <summary>Gets the base URI.</summary>
        public Uri BaseUri { get; }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await _shutdown.CancelAsync();
            try
            {
                await _run.WaitAsync(_watchdog);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
            catch (HttpListenerException) when (_shutdown.IsCancellationRequested)
            {
            }
            finally
            {
                _listener.Close();
                _shutdown.Dispose();
            }
        }

        private async Task RunAsync(Func<HttpListenerContext, CancellationToken, Task> respond)
        {
            while (true)
            {
                var context = await _listener.GetContextAsync().WaitAsync(_shutdown.Token);
                try
                {
                    await respond(context, _shutdown.Token);
                }
                finally
                {
                    context.Response.Close();
                }
            }
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private TimeSpan _elapsed;

        /// <summary>Gets the active timer count.</summary>
        public int ActiveTimerCount
        {
            get
            {
                lock (_timers)
                {
                    return _timers.Count;
                }
            }
        }

        /// <inheritdoc />
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        /// <summary>Executes advance.</summary>
        /// <param name="duration">The duration value.</param>
        public void Advance(TimeSpan duration)
        {
            ManualTimer[] elapsed;
            lock (_timers)
            {
                _elapsed += duration;
                elapsed = _timers.Where(timer => timer.Due <= _elapsed).ToArray();
                foreach (var timer in elapsed)
                {
                    _timers.Remove(timer);
                }
            }
            foreach (var timer in elapsed)
            {
                timer.Callback(timer.State);
            }
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            /// <summary>Gets the callback.</summary>
            public TimerCallback Callback { get; } = callback;
            /// <summary>Gets the state.</summary>
            public object? State { get; } = state;
            /// <summary>Gets or sets the due.</summary>
            public TimeSpan Due { get; private set; }

            /// <inheritdoc />
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                lock (owner._timers)
                {
                    owner._timers.Remove(this);
                    if (dueTime != Timeout.InfiniteTimeSpan)
                    {
                        Due = owner._elapsed + dueTime;
                        owner._timers.Add(this);
                    }
                    return true;
                }
            }

            /// <inheritdoc />
            public void Dispose()
            {
                lock (owner._timers)
                {
                    owner._timers.Remove(this);
                }
            }

            /// <inheritdoc />
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
