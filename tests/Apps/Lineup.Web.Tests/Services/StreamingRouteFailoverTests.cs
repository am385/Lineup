using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Lineup.LineupLink.Physical;
using Lineup.LineupLink.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>Verifies ordered physical failover routing.</summary>
public sealed class StreamingRouteFailoverTests
{
    /// <summary>Verifies rejected Lineup Link credentials do not prevent acquisition from a healthy equivalent source.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task SubscribeFirstAvailableAsync_RemoteAuthorizationRejected_UsesHealthySource(HttpStatusCode status)
    {
        // Arrange
        await using var remote = new RejectedLinkSource(status);
        using var first = Assert.IsType<LineupLinkPhysicalTunerDevice>(await remote.CreateDeviceAsync());
        var firstChannel = Assert.Single(await first.RefreshLineupAsync(TestContext.Current.CancellationToken)).Id;
        var second = new FakeDevice(PhysicalDeviceId.New(), fail: false);
        var secondChannel = new SourceChannelId(second.Snapshot.Id, "7.1-local");
        var capacity = new PhysicalCapacityLeaseRegistry();
        var graph = new StreamingSessionGraph(capacity, NullLogger<StreamingSessionGraph>.Instance);
        var multiplexer = new TunerStreamMultiplexer(graph, Substitute.For<IMediaProcessFactory>(), NullLogger<TunerStreamMultiplexer>.Instance);
        var route = Route([Candidate(first, firstChannel, remote.BaseUri.ToString()), Candidate(second, secondChannel, "http://local/auto/v7.1")]);

        // Act
        await using var selected = await multiplexer.SubscribeFirstAvailableAsync(route, TestContext.Current.CancellationToken);
        await selected.DisposeAsync();

        // Assert
        Assert.Equal(secondChannel, selected.Candidate.SourceChannelId);
        Assert.Equal(secondChannel, Assert.Single(second.Requests));
        Assert.Equal(1, remote.StreamRequestCount);
        Assert.Empty(capacity.GetDiagnostics());
        Assert.Empty(graph.GetDiagnostics().Nodes);
    }

    /// <summary>Verifies a rejected grant without fallback produces a safe non-retryable failure with exact source identity.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task SubscribeFirstAvailableAsync_RemoteAuthorizationRejected_ReportsStructuredFailure(HttpStatusCode status)
    {
        // Arrange
        await using var remote = new RejectedLinkSource(status);
        using var device = Assert.IsType<LineupLinkPhysicalTunerDevice>(await remote.CreateDeviceAsync());
        var channel = Assert.Single(await device.RefreshLineupAsync(TestContext.Current.CancellationToken)).Id;
        var capacity = new PhysicalCapacityLeaseRegistry();
        var graph = new StreamingSessionGraph(capacity, NullLogger<StreamingSessionGraph>.Instance);
        var multiplexer = new TunerStreamMultiplexer(graph, Substitute.For<IMediaProcessFactory>(), NullLogger<TunerStreamMultiplexer>.Instance);

        // Act
        var exception = await Assert.ThrowsAsync<StreamAcquisitionException>(
            async () => await multiplexer.SubscribeFirstAvailableAsync(Route([Candidate(device, channel, remote.BaseUri.ToString())]), TestContext.Current.CancellationToken));

        // Assert
        var failure = Assert.Single(exception.CandidateFailures);
        Assert.Equal(channel.PhysicalDeviceId, failure.DeviceId);
        Assert.Equal(channel, failure.ChannelId);
        Assert.Equal(PhysicalStreamFailureCategory.Unavailable, failure.Category);
        Assert.Equal("source_unauthorized", failure.Code);
        Assert.False(failure.Retryable);
        Assert.DoesNotContain("test-token", failure.Message, StringComparison.Ordinal);
        var aggregate = Assert.IsType<AggregateException>(exception.InnerException);
        var physical = Assert.IsType<PhysicalStreamAcquisitionException>(Assert.Single(aggregate.InnerExceptions));
        Assert.IsType<UnauthorizedAccessException>(physical.InnerException);
        Assert.Empty(capacity.GetDiagnostics());
        Assert.Empty(graph.GetDiagnostics().Nodes);
    }

    /// <summary>Verifies stale preferred devices are skipped while a live equivalent candidate remains available.</summary>
    [Fact]
    public async Task ResolveHdHomeRunAsync_StalePreferredDevice_UsesFirstLiveCandidate()
    {
        // Arrange
        var databasePath = Path.Combine(AppContext.BaseDirectory, $"route-failover-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<EpgDbContext>().UseSqlite($"Data Source={databasePath};Pooling=False").Options;
        var store = new StreamingTopologyStore(new TestDbContextFactory(options));
        var staleId = PhysicalDeviceId.New();
        var live = new FakeDevice(PhysicalDeviceId.New(), fail: false, "7.1-live");
        var group = Guid.NewGuid();
        await store.ReconcileAsync(
            staleId,
            [new PhysicalSourceChannel { Id = new SourceChannelId(staleId, "7.1-stale"), ProviderNumber = "7.1", ProviderName = "News" }],
            TestContext.Current.CancellationToken);
        await store.SetIdentityAsync(new SourceChannelId(staleId, "7.1-stale"), null, null, group, TestContext.Current.CancellationToken);
        await store.ReconcileAsync(
            live.Snapshot.Id,
            [new PhysicalSourceChannel
            {
                Id = new SourceChannelId(live.Snapshot.Id, "7.1-live"),
                ProviderNumber = "7.1",
                ProviderName = "News",
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["audioCodec"] = "AC4" }
            }],
            TestContext.Current.CancellationToken);
        await store.SetIdentityAsync(new SourceChannelId(live.Snapshot.Id, "7.1-live"), null, null, group, TestContext.Current.CancellationToken);
        var virtualDevice = new VirtualDeviceDefinition
        {
            Id = VirtualDeviceId.New(),
            Name = "Aggregate",
            Adapter = new AdapterConfiguration("hdhomerun", JsonSerializer.SerializeToElement(new { }))
        };
        var configuration = new StreamingConfiguration
        {
            PhysicalDevices =
            [
                Physical(staleId, 0, "stale.local"),
                Physical(live.Snapshot.Id, 1, "live.local")
            ],
            VirtualDevices = [virtualDevice]
        };
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { Streaming = configuration });
        var catalog = new FakeCatalog([live]);
        var resolver = new StreamingRouteResolver(
            settings,
            catalog,
            Substitute.For<IPhysicalLineupService>(),
            store);

        try
        {
            // Act
            var route = await resolver.ResolveVirtualAsync(
                settings.Settings.Streaming.VirtualDevices.Single().Id,
                "7.1",
                TestContext.Current.CancellationToken);

            // Assert
            Assert.NotNull(route);
            Assert.Equal(live.Snapshot.Id, route.PhysicalDevice.Snapshot.Id);
            Assert.Equal(new SourceChannelId(live.Snapshot.Id, "7.1-live"), Assert.Single(route.SourceCandidates));
            Assert.Equal("AC4", Assert.Single(route.Candidates).SourceMetadata["audioCodec"]);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    /// <summary>Verifies a failed preferred source releases capacity before the next equivalent source opens.</summary>
    [Fact]
    public async Task OpenAsync_PreferredSourceFails_UsesNextCandidateWithoutLeakingCapacity()
    {
        // Arrange
        var first = new FakeDevice(PhysicalDeviceId.New(), fail: true);
        var second = new FakeDevice(PhysicalDeviceId.New(), fail: false);
        var firstChannel = new SourceChannelId(first.Snapshot.Id, "7.1-a");
        var secondChannel = new SourceChannelId(second.Snapshot.Id, "7.1-b");
        var capacity = new PhysicalCapacityLeaseRegistry();
        var firstCandidate = Candidate(first, firstChannel, "http://first/auto/v7.1");
        var secondCandidate = Candidate(second, secondChannel, "http://second/auto/v7.1", convertAc4ToAc3: true);
        var route = new ResolvedStreamingRoute
        {
            VirtualDevice = new VirtualDeviceDefinition
            {
                Id = VirtualDeviceId.New(),
                Name = "Aggregate",
                Adapter = new AdapterConfiguration("hdhomerun", JsonSerializer.SerializeToElement(new { }))
            },
            PhysicalDevice = first,
            SourceChannelId = firstChannel,
            SourceCandidates = [firstChannel, secondChannel],
            DiagnosticUri = new Uri("http://first/auto/v7.1"),
            Acquisition = firstCandidate.Acquisition,
            Candidates = [firstCandidate, secondCandidate],
            PhysicalProfile = new PhysicalTranscodeProfile(),
            VirtualProfile = new VirtualTranscodeProfile()
        };
        var graph = new StreamingSessionGraph(capacity, NullLogger<StreamingSessionGraph>.Instance);
        var multiplexer = new TunerStreamMultiplexer(
            graph,
            Substitute.For<IMediaProcessFactory>(),
            NullLogger<TunerStreamMultiplexer>.Instance);

        // Act
        await using var selected = await multiplexer.SubscribeFirstAvailableAsync(route, TestContext.Current.CancellationToken);
        await selected.DisposeAsync();

        // Assert
        Assert.Equal(firstChannel, Assert.Single(first.Requests));
        Assert.Equal(secondChannel, Assert.Single(second.Requests));
        Assert.Equal(second.Snapshot.Id, selected.Candidate.PhysicalDevice.Snapshot.Id);
        Assert.Equal(OutputAudioPolicy.Ac3, selected.Candidate.PhysicalProfile.Ac4Audio);
        Assert.Empty(capacity.GetDiagnostics());
    }

    /// <summary>Verifies routes with different candidate sets share the exact physical acquisition they both select.</summary>
    [Fact]
    public async Task SubscribeFirstAvailableAsync_OverlappingRoutes_SharesSelectedAcquisition()
    {
        // Arrange
        var unavailable = new FakeDevice(PhysicalDeviceId.New(), fail: true);
        var source = new BlockingStream();
        var shared = new FakeDevice(PhysicalDeviceId.New(), fail: false, streamFactory: () => source);
        var unavailableCandidate = Candidate(unavailable, new SourceChannelId(unavailable.Snapshot.Id, "7.1-a"), "http://first/auto/v7.1");
        var sharedCandidate = Candidate(shared, new SourceChannelId(shared.Snapshot.Id, "7.1-b"), "http://second/auto/v7.1");
        var capacity = new PhysicalCapacityLeaseRegistry();
        var graph = new StreamingSessionGraph(capacity, NullLogger<StreamingSessionGraph>.Instance);
        var multiplexer = new TunerStreamMultiplexer(
            graph,
            Substitute.For<IMediaProcessFactory>(),
            NullLogger<TunerStreamMultiplexer>.Instance);
        var firstRoute = Route([unavailableCandidate, sharedCandidate]);
        var secondRoute = Route([sharedCandidate]);

        // Act
        await using var first = await multiplexer.SubscribeFirstAvailableAsync(firstRoute, TestContext.Current.CancellationToken);
        await source.WaitForReadAsync(TestContext.Current.CancellationToken);
        await using var second = await multiplexer.SubscribeFirstAvailableAsync(secondRoute, TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(shared.Requests);
        Assert.Equal(2, Assert.Single(graph.GetDiagnostics().Nodes, node => node.Kind == "acquisition").ReceiverCount);
        Assert.Equal(1, Assert.Single(capacity.GetDiagnostics()).ActiveAcquisitions);
    }

    /// <summary>Verifies complete route failure produces a safe structured aggregate with candidate identities.</summary>
    [Fact]
    public async Task SubscribeFirstAvailableAsync_AllSourcesUnavailable_ThrowsStructuredFailure()
    {
        // Arrange
        var first = new FakeDevice(PhysicalDeviceId.New(), fail: true);
        var second = new FakeDevice(PhysicalDeviceId.New(), fail: true);
        var firstCandidate = Candidate(first, new SourceChannelId(first.Snapshot.Id, "7.1-a"), "http://first/auto/v7.1");
        var secondCandidate = Candidate(second, new SourceChannelId(second.Snapshot.Id, "7.1-b"), "http://second/auto/v7.1");
        var multiplexer = new TunerStreamMultiplexer(
            new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance),
            Substitute.For<IMediaProcessFactory>(),
            NullLogger<TunerStreamMultiplexer>.Instance);

        // Act
        var exception = await Assert.ThrowsAsync<StreamAcquisitionException>(
            async () => await multiplexer.SubscribeFirstAvailableAsync(Route([firstCandidate, secondCandidate]), TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal("source_unavailable", exception.Code);
        Assert.True(exception.Retryable);
        Assert.Equal("Every equivalent physical source is currently unavailable.", exception.Message);
        Assert.Collection(
            exception.CandidateFailures,
            failure =>
            {
                Assert.Equal(first.Snapshot.Id, failure.DeviceId);
                Assert.Equal(firstCandidate.SourceChannelId, failure.ChannelId);
                Assert.Equal("source_unavailable", failure.Code);
            },
            failure =>
            {
                Assert.Equal(second.Snapshot.Id, failure.DeviceId);
                Assert.Equal(secondCandidate.SourceChannelId, failure.ChannelId);
                Assert.Equal("source_unavailable", failure.Code);
            });
        Assert.IsType<AggregateException>(exception.InnerException);
    }

    private static ResolvedStreamingRoute Route(IReadOnlyList<ResolvedStreamingCandidate> candidates)
    {
        var preferred = candidates[0];
        return new ResolvedStreamingRoute
        {
            VirtualDevice = new VirtualDeviceDefinition
            {
                Id = VirtualDeviceId.New(),
                Name = "Aggregate",
                Adapter = new AdapterConfiguration("hdhomerun", JsonSerializer.SerializeToElement(new { }))
            },
            PhysicalDevice = preferred.PhysicalDevice,
            SourceChannelId = preferred.SourceChannelId,
            SourceCandidates = candidates.Select(candidate => candidate.SourceChannelId).ToArray(),
            DiagnosticUri = preferred.DiagnosticUri,
            Acquisition = preferred.Acquisition,
            Candidates = candidates,
            PhysicalProfile = preferred.PhysicalProfile,
            VirtualProfile = new VirtualTranscodeProfile()
        };
    }

    private static ResolvedStreamingCandidate Candidate(IPhysicalTunerDevice device, SourceChannelId channel, string diagnosticUri, bool convertAc4ToAc3 = false) =>
        new()
        {
            PhysicalDevice = device,
            SourceChannelId = channel,
            Acquisition = new StreamAcquisitionDescriptor(
                device.Snapshot.Id,
                PhysicalAcquisitionKey.Create(new PhysicalStreamRequest { ChannelId = channel }),
                device.Snapshot.Capacity),
            PhysicalProfile = new PhysicalTranscodeProfile
            {
                Ac4Audio = convertAc4ToAc3 ? OutputAudioPolicy.Ac3 : OutputAudioPolicy.Copy
            },
            DiagnosticUri = new Uri(diagnosticUri)
        };

    private sealed class RejectedLinkSource : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        private readonly Guid _virtualDeviceId = Guid.NewGuid();
        private readonly Task _responses;
        private int _streamRequestCount;

        public RejectedLinkSource(HttpStatusCode status)
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            BaseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)reservation.LocalEndpoint).Port}/");
            reservation.Stop();
            _listener.Prefixes.Add(BaseUri.ToString());
            _listener.Start();
            _responses = RespondAsync(status);
        }

        public Uri BaseUri { get; }

        public int StreamRequestCount => Volatile.Read(ref _streamRequestCount);

        public Task<IPhysicalTunerDevice> CreateDeviceAsync() => new LineupLinkPhysicalTunerDriver().CreateAsync(
            PhysicalDeviceId.New(),
            "Remote",
            new AdapterConfiguration(LineupLinkProtocol.AdapterKind, JsonSerializer.SerializeToElement(new LineupLinkPhysicalConfiguration
            {
                BaseUrl = BaseUri.ToString(),
                VirtualDeviceId = _virtualDeviceId,
                AccessToken = "test-token",
                TransportSecurity = LineupLinkTransportSecurity.Http
            })),
            _shutdown.Token).AsTask();

        public async ValueTask DisposeAsync()
        {
            await _shutdown.CancelAsync();
            try
            {
                await _responses.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
            finally
            {
                _listener.Close();
                _shutdown.Dispose();
            }
        }

        private async Task RespondAsync(HttpStatusCode status)
        {
            while (true)
            {
                var context = await _listener.GetContextAsync().WaitAsync(_shutdown.Token);
                var path = context.Request.Url!.AbsolutePath;
                string payload;
                if (path == LineupLinkProtocol.ManifestPath(_virtualDeviceId))
                {
                    payload = JsonSerializer.Serialize(new LineupLinkManifest { InstanceId = Guid.NewGuid(), VirtualDeviceId = _virtualDeviceId, Name = "Remote", TunerCount = 1 });
                }
                else if (path == LineupLinkProtocol.LineupPath(_virtualDeviceId))
                {
                    payload = JsonSerializer.Serialize(new LineupLinkLineup
                    {
                        VirtualDeviceId = _virtualDeviceId,
                        Channels = [new LineupLinkChannel { Key = "7.1", Number = "7.1", Name = "News" }]
                    });
                }
                else
                {
                    Assert.Equal(LineupLinkProtocol.StreamPath(_virtualDeviceId, "7.1"), path);
                    Interlocked.Increment(ref _streamRequestCount);
                    context.Response.StatusCode = (int)status;
                    payload = JsonSerializer.Serialize(new LineupLinkError { Code = "unauthorized", Message = "Rejected test-token" });
                }
                var bytes = Encoding.UTF8.GetBytes(payload);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, _shutdown.Token);
                context.Response.Close();
            }
        }
    }

    private sealed class FakeDevice : IPhysicalTunerDevice
    {
        private readonly bool _fail;

        private readonly string? _channelKey;
        private readonly Func<Stream> _streamFactory;

        public FakeDevice(PhysicalDeviceId id, bool fail, string? channelKey = null, Func<Stream>? streamFactory = null)
        {
            _fail = fail;
            _channelKey = channelKey;
            _streamFactory = streamFactory ?? (() => new MemoryStream([1]));
            Snapshot = new PhysicalDeviceSnapshot
            {
                Id = id,
                Kind = "fake",
                Name = id.ToString(),
                Capacity = new PhysicalTunerCapacity(1)
            };
        }

        public PhysicalDeviceSnapshot Snapshot { get; }

        public List<SourceChannelId> Requests { get; } = [];

        public Task<IReadOnlyList<PhysicalSourceChannel>> RefreshLineupAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PhysicalSourceChannel>>(
                _channelKey is null
                    ? []
                    : [new PhysicalSourceChannel { Id = new SourceChannelId(Snapshot.Id, _channelKey), ProviderNumber = "7.1", ProviderName = "News" }]);

        public ValueTask<IPhysicalStreamLease> OpenStreamAsync(PhysicalStreamRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request.ChannelId);
            return _fail
                ? ValueTask.FromException<IPhysicalStreamLease>(new IOException("Unavailable"))
                : ValueTask.FromResult<IPhysicalStreamLease>(new FakeLease(_streamFactory()));
        }
    }

    private static PhysicalDeviceDefinition Physical(PhysicalDeviceId id, int order, string address) =>
            new()
            {
                Id = id,
                Name = address,
                Order = order,
                Adapter = new AdapterConfiguration("hdhomerun", JsonSerializer.SerializeToElement(new { Address = address }))
            };

    private sealed class FakeCatalog(IReadOnlyList<IPhysicalTunerDevice> devices) : IPhysicalDeviceCatalog
    {
        public IReadOnlyList<IPhysicalTunerDevice> Devices { get; } = devices;

        public IPhysicalTunerDevice GetRequired(PhysicalDeviceId deviceId) =>
            Devices.Single(device => device.Snapshot.Id == deviceId);

        public Task ApplyAsync(StreamingConfiguration configuration, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class TestDbContextFactory(DbContextOptions<EpgDbContext> options) : IDbContextFactory<EpgDbContext>
    {
        public EpgDbContext CreateDbContext() => new(options);

        public Task<EpgDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class FakeLease(Stream stream) : IPhysicalStreamLease
    {
        public Stream Stream { get; } = stream;

        public string? ContentType => "video/mp2t";

        public async ValueTask DisposeAsync() => await Stream.DisposeAsync();
    }

    private sealed class BlockingStream : Stream
    {
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public Task WaitForReadAsync(CancellationToken cancellationToken) => _readStarted.Task.WaitAsync(cancellationToken);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _readStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
