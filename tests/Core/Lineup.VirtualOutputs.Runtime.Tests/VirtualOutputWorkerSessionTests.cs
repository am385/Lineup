using System.IO.Pipes;
using System.Text.Json;
using Lineup.Common.Streaming;
using Lineup.VirtualOutputs.Hosting;
using Lineup.VirtualOutputs.Protocol;
using Xunit;

namespace Lineup.VirtualOutputs.Runtime.Tests;

/// <summary>Verifies worker lease deadlines cancel real IPC without extending an expired worker's lifetime.</summary>
public sealed class VirtualOutputWorkerSessionTests
{
    /// <summary>Verifies an unavailable broker cannot leave a worker connecting beyond its lease deadline.</summary>
    [Fact]
    public async Task RunLeaseLoopAsync_BrokerDisappears_ShutsDownAtExpiration()
    {
        // Arrange
        await using var fixture = new LeaseFixture();
        await fixture.RegisterAsync();

        // Act
        fixture.StartLoop();
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await fixture.HealthRead.Task.WaitAsync(fixture.Token);
        fixture.Time.Advance(TimeSpan.FromSeconds(2));

        // Assert
        await fixture.AssertShutdownAsync();
    }

    /// <summary>Verifies an accepted connection with no renewal response is canceled at lease expiration.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunLeaseLoopAsync_BrokerNeverResponds_ShutsDownAtExpiration(bool cancelOnShutdown)
    {
        // Arrange
        await using var fixture = new LeaseFixture();
        await fixture.RegisterAsync();
        await using var server = fixture.CreateServer();

        // Act
        fixture.StartLoop(cancelOnShutdown);
        await fixture.ReadRenewalAsync(server);
        fixture.Time.Advance(TimeSpan.FromSeconds(2));

        // Assert
        await fixture.AssertShutdownAsync();
    }

    /// <summary>Verifies a lease already expired before the loop starts shuts down without polling health.</summary>
    [Fact]
    public async Task RunLeaseLoopAsync_AlreadyExpired_ShutsDownImmediately()
    {
        // Arrange
        await using var fixture = new LeaseFixture();
        await fixture.RegisterAsync();
        fixture.Time.Advance(TimeSpan.FromSeconds(3));

        // Act
        fixture.StartLoop();

        // Assert
        await fixture.AssertShutdownAsync();
        Assert.False(fixture.HealthRead.Task.IsCompleted);
    }

    /// <summary>Verifies a remaining lease shorter than the polling interval does not delay expiration.</summary>
    [Fact]
    public async Task RunLeaseLoopAsync_ShortRemainingLease_ShutsDownAtExpiration()
    {
        // Arrange
        await using var fixture = new LeaseFixture();
        await fixture.RegisterAsync();
        fixture.Time.Advance(TimeSpan.FromMilliseconds(2750));

        // Act
        fixture.StartLoop();
        fixture.Time.Advance(TimeSpan.FromMilliseconds(250));

        // Assert
        await fixture.AssertShutdownAsync();
        Assert.False(fixture.HealthRead.Task.IsCompleted);
    }

    /// <summary>Verifies a valid renewal replaces the old deadline without requesting shutdown.</summary>
    [Fact]
    public async Task RunLeaseLoopAsync_HealthyRenewal_ExtendsDeadline()
    {
        // Arrange
        await using var fixture = new LeaseFixture();
        await fixture.RegisterAsync();
        var originalExpiration = fixture.Session.LeaseExpiresUtc;
        await using var server = fixture.CreateServer();
        fixture.StartLoop();
        await fixture.ReadRenewalAsync(server);
        var renewedExpiration = fixture.Time.GetUtcNow().AddSeconds(10);

        // Act
        await fixture.WriteRenewalAsync(server, new VirtualOutputLeaseRenewalResponse { Accepted = true, LeaseExpiresUtc = renewedExpiration });
        await fixture.Time.WaitForTimerAsync(TimeSpan.FromSeconds(5), fixture.Token);
        fixture.Time.Advance(TimeSpan.FromSeconds(3));

        // Assert
        Assert.True(fixture.Time.GetUtcNow() > originalExpiration);
        Assert.Equal(renewedExpiration, fixture.Session.LeaseExpiresUtc);
        Assert.Equal(0, fixture.ShutdownCount);
        Assert.False(fixture.Loop.IsCompleted);
    }

    /// <summary>Verifies a late response cannot revive the worker even when the deadline timer callback is delayed.</summary>
    [Fact]
    public async Task RunLeaseLoopAsync_ResponseAfterExpiration_RejectsExtendedLease()
    {
        // Arrange
        await using var fixture = new LeaseFixture();
        await fixture.RegisterAsync();
        var originalExpiration = fixture.Session.LeaseExpiresUtc;
        await using var server = fixture.CreateServer();
        fixture.StartLoop();
        await fixture.ReadRenewalAsync(server);

        // Act
        fixture.Time.Advance(TimeSpan.FromSeconds(2), fireTimers: false);
        await fixture.WriteRenewalAsync(server, new VirtualOutputLeaseRenewalResponse { Accepted = true, LeaseExpiresUtc = fixture.Time.GetUtcNow().AddSeconds(10) });

        // Assert
        await fixture.AssertShutdownAsync();
        Assert.Equal(originalExpiration, fixture.Session.LeaseExpiresUtc);
    }

    /// <summary>Verifies a broken response can be retried while the current lease is still valid.</summary>
    [Fact]
    public async Task RunLeaseLoopAsync_TransientFailure_RecoversBeforeExpiration()
    {
        // Arrange
        await using var fixture = new LeaseFixture();
        await fixture.RegisterAsync();
        await using var server = fixture.CreateServer();
        fixture.StartLoop();
        await fixture.ReadRenewalAsync(server);

        // Act
        await server.DisposeAsync();
        await fixture.Time.WaitForTimerAsync(TimeSpan.FromSeconds(1), fixture.Token);
        await using var retryServer = fixture.CreateServer();
        await fixture.ReadRenewalAsync(retryServer);
        var renewedExpiration = fixture.Time.GetUtcNow().AddSeconds(10);
        await fixture.WriteRenewalAsync(retryServer, new VirtualOutputLeaseRenewalResponse { Accepted = true, LeaseExpiresUtc = renewedExpiration });
        await fixture.Time.WaitForTimerAsync(TimeSpan.FromSeconds(5), fixture.Token);
        fixture.Time.Advance(TimeSpan.FromSeconds(1));

        // Assert
        Assert.Equal(renewedExpiration, fixture.Session.LeaseExpiresUtc);
        Assert.Equal(0, fixture.ShutdownCount);
        Assert.False(fixture.Loop.IsCompleted);
    }

    /// <summary>Verifies rejected, draining, missing, and expired renewal grants fail closed.</summary>
    [Theory]
    [InlineData(false, false, 10)]
    [InlineData(true, true, 10)]
    [InlineData(true, false, null)]
    [InlineData(true, false, 0)]
    [InlineData(true, false, -1)]
    public async Task RunLeaseLoopAsync_InvalidRenewal_ShutsDown(bool accepted, bool drain, int? expirationSeconds)
    {
        // Arrange
        await using var fixture = new LeaseFixture();
        await fixture.RegisterAsync();
        await using var server = fixture.CreateServer();
        fixture.StartLoop();
        await fixture.ReadRenewalAsync(server);

        // Act
        await fixture.WriteRenewalAsync(server, new VirtualOutputLeaseRenewalResponse
        {
            Accepted = accepted,
            Drain = drain,
            LeaseExpiresUtc = expirationSeconds is { } seconds ? fixture.Time.GetUtcNow().AddSeconds(seconds) : null
        });

        // Assert
        await fixture.AssertShutdownAsync();
    }

    /// <summary>Verifies caller cancellation stops delays and pending IPC without requesting lease-failure shutdown.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunLeaseLoopAsync_CallerCancellation_DoesNotRequestShutdown(bool renewalPending)
    {
        // Arrange
        await using var fixture = new LeaseFixture();
        await fixture.RegisterAsync();
        await using var server = fixture.CreateServer();
        fixture.StartLoop();
        if (renewalPending)
        {
            await fixture.ReadRenewalAsync(server);
        }

        // Act
        fixture.Cancel();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Loop);
        Assert.Equal(0, fixture.ShutdownCount);
    }

    private sealed class LeaseFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        private readonly VirtualOutputWorkerBootstrap _bootstrap;
        private int _shutdownCount;

        public LeaseFixture()
        {
            _lifetime.CancelAfter(TimeSpan.FromSeconds(15));
            _bootstrap = new VirtualOutputWorkerBootstrap
            {
                Endpoint = new VirtualOutputIpcEndpoint { Transport = VirtualOutputIpcTransport.NamedPipe, Address = $"lineup-lease-test-{Guid.NewGuid():N}" },
                VirtualDevice = new VirtualDeviceDefinition
                {
                    Id = VirtualDeviceId.New(),
                    Name = "Lease test",
                    Adapter = new AdapterConfiguration("test", JsonSerializer.SerializeToElement(new { }))
                },
                WorkerPolicy = new VirtualOutputWorkerPolicyDefinition { Key = "test", Name = "Test" },
                WorkerId = "test-worker",
                Generation = 1,
                BootstrapToken = "test-bootstrap",
                BootstrapExpiresUtc = Time.GetUtcNow().AddMinutes(1)
            };
            Session = new VirtualOutputWorkerSession(_bootstrap, Time);
        }

        public ManualTimeProvider Time { get; } = new();
        public VirtualOutputWorkerSession Session { get; }
        public TaskCompletionSource HealthRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Loop { get; private set; } = Task.CompletedTask;
        public CancellationToken Token => _lifetime.Token;
        public int ShutdownCount => Volatile.Read(ref _shutdownCount);

        public NamedPipeServerStream CreateServer() => new(_bootstrap.Endpoint.Address, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        public async Task RegisterAsync()
        {
            await using var server = CreateServer();
            var connection = server.WaitForConnectionAsync(Token);
            var registration = Session.RegisterAsync(Token);
            await connection;
            var request = await VirtualOutputWireProtocol.ReadAsync<VirtualOutputIpcRequest>(server, Token);
            Assert.Equal(VirtualOutputIpcOperation.Register, request.Operation);
            await VirtualOutputWireProtocol.WriteAsync(server, VirtualOutputIpcResponse.Success(new VirtualOutputWorkerRegistrationResponse
            {
                Accepted = true,
                SessionId = VirtualOutputWorkerSessionId.New(),
                Generation = _bootstrap.Generation,
                ProtocolVersion = VirtualOutputProtocol.MaximumVersion,
                LeaseExpiresUtc = Time.GetUtcNow().AddSeconds(3)
            }), Token);
            await registration;
        }

        public void StartLoop(bool cancelOnShutdown = false) => Loop = Session.RunLeaseLoopAsync(
            () =>
            {
                HealthRead.TrySetResult();
                return (true, 0);
            },
            () =>
            {
                Interlocked.Increment(ref _shutdownCount);
                if (cancelOnShutdown)
                {
                    Cancel();
                }
            },
            Token);

        public async Task ReadRenewalAsync(NamedPipeServerStream server)
        {
            var connection = server.WaitForConnectionAsync(Token);
            Time.Advance(TimeSpan.FromSeconds(1));
            await connection;
            var request = await VirtualOutputWireProtocol.ReadAsync<VirtualOutputIpcRequest>(server, Token);
            Assert.Equal(VirtualOutputIpcOperation.RenewLease, request.Operation);
            Assert.Equal(Session.Context, request.ReadPayload<VirtualOutputLeaseRenewalRequest>().Session);
        }

        public Task WriteRenewalAsync(NamedPipeServerStream server, VirtualOutputLeaseRenewalResponse response) =>
            VirtualOutputWireProtocol.WriteAsync(server, VirtualOutputIpcResponse.Success(response), Token);

        public async Task AssertShutdownAsync()
        {
            await Loop.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            Assert.Equal(1, ShutdownCount);
        }

        public void Cancel() => _lifetime.Cancel();

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            try
            {
                await Loop;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            finally
            {
                _lifetime.Dispose();
            }
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_timers)
            {
                return _utcNow;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan duration, bool fireTimers = true)
        {
            ManualTimer[] elapsed;
            lock (_timers)
            {
                _utcNow += duration;
                elapsed = fireTimers ? _timers.Where(timer => timer.DueUtc <= _utcNow).ToArray() : [];
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

        public async Task WaitForTimerAsync(TimeSpan dueTime, CancellationToken cancellationToken)
        {
            while (true)
            {
                Task changed;
                lock (_timers)
                {
                    if (_timers.Any(timer => timer.DueUtc == _utcNow + dueTime))
                    {
                        return;
                    }
                    changed = _changed.Task;
                }
                await changed.WaitAsync(cancellationToken);
            }
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public DateTimeOffset DueUtc { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                lock (owner._timers)
                {
                    owner._timers.Remove(this);
                    if (dueTime != Timeout.InfiniteTimeSpan)
                    {
                        DueUtc = owner._utcNow + dueTime;
                        owner._timers.Add(this);
                    }
                    owner._changed.TrySetResult();
                    owner._changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    return true;
                }
            }

            public void Dispose()
            {
                lock (owner._timers)
                {
                    owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
